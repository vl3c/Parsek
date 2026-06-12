using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Carries parent vessel engine/RCS state to child recordings at decouple time.
    /// Consumed once by InitializeLoadedState to seed engines that SeedEngines missed
    /// (isOperational=false after fuel severance). Bug #298.
    /// </summary>
    internal struct InheritedEngineState
    {
        public HashSet<ulong> activeEngineKeys;
        public Dictionary<ulong, float> engineThrottles;
        public HashSet<ulong> activeRcsKeys;
        public Dictionary<ulong, float> rcsThrottles;

        /// <summary>
        /// Creates a snapshot from a FlightRecorder's active engine/RCS state.
        /// Returns null if no engines or RCS are active. Defensively copies all
        /// collections (the recorder's fields are live mutable references).
        /// </summary>
        internal static InheritedEngineState? FromRecorder(FlightRecorder rec)
        {
            if (rec == null) return null;
            bool hasEngines = rec.ActiveEngineKeys != null && rec.ActiveEngineKeys.Count > 0;
            bool hasRcs = rec.ActiveRcsKeys != null && rec.ActiveRcsKeys.Count > 0;
            if (!hasEngines && !hasRcs) return null;

            return new InheritedEngineState
            {
                activeEngineKeys = hasEngines ? new HashSet<ulong>(rec.ActiveEngineKeys) : null,
                engineThrottles = hasEngines && rec.LastEngineThrottles != null
                    ? new Dictionary<ulong, float>(rec.LastEngineThrottles) : null,
                activeRcsKeys = hasRcs ? new HashSet<ulong>(rec.ActiveRcsKeys) : null,
                rcsThrottles = hasRcs && rec.LastRcsThrottles != null
                    ? new Dictionary<ulong, float>(rec.LastRcsThrottles) : null
            };
        }

    }

    /// <summary>
    /// Manages continuous recording for background vessels in a recording tree.
    /// Supports two modes per vessel:
    ///   - On-rails: captures OrbitSegment or SurfacePosition snapshots
    ///   - Loaded/physics: full trajectory points, part events, adaptive sampling
    ///
    /// Not a MonoBehaviour. Owned by ParsekFlight, which calls its methods from
    /// Update() and PhysicsFramePatch.Postfix.
    /// </summary>
    internal class BackgroundRecorder
    {
        private enum BackgroundRecordingEndReason
        {
            Unknown = 0,
            VesselDestroyedOrDespawned = 1,
            TtlExpired = 2,
            LeftPhysicsBubble = 3,
            ParentRecordingMissing = 4,
            ParentRecordingClosedOrSuperseded = 5,
            ParentOnRailsOrDestroyed = 6
        }

        private RecordingTree tree;

        // Per-vessel tracking state for loaded/physics mode
        private Dictionary<uint, BackgroundVesselState> loadedStates
            = new Dictionary<uint, BackgroundVesselState>();

        // Per-vessel on-rails tracking
        private Dictionary<uint, BackgroundOnRailsState> onRailsStates
            = new Dictionary<uint, BackgroundOnRailsState>();

        // Per-vessel last-known finalization tails. Consumers land in later phases;
        // this phase only owns refresh and transfer.
        private Dictionary<uint, RecordingFinalizationCache> finalizationCaches
            = new Dictionary<uint, RecordingFinalizationCache>();

        // GameEvent subscriptions for discrete events (onPartDie, onPartJointBreak)
        private bool partEventsSubscribed;

        // Interval for updating ExplicitEndUT on background recordings
        private const double ExplicitEndUpdateInterval = 30.0;
        private const double BackgroundStateDriftCheckInterval = 5.0;
        private const double BranchBoundaryUTTolerance = 1e-6;
        private double lastBackgroundStateDriftCheckUT = double.MinValue;

        // Debris TTL: stop recording debris after this many seconds
        internal const double DebrisTTLSeconds = 60.0;
        internal const double DebrisFullFidelityProximityRangeMeters = 250.0;
        internal const double DebrisHalfFidelityProximityRangeMeters = 500.0;
        internal const double DebrisRelativeSectionExitMeters = 550.0;

        // Cascade depth cap. Background vessel splits whose parent recording is at
        // Generation >= MaxRecordingGeneration are skipped entirely — the parent's
        // existing recording continues sampling, the new fragments are not tracked.
        // Currently 1 = "only record primary debris (decoupled boosters)". See #284.
        internal const int MaxRecordingGeneration = 1;

        // Per-vessel debris TTL tracking (vesselPid -> UT at which to stop recording)
        // double.NaN = no TTL (controlled vessel, keep recording indefinitely)
        private Dictionary<uint, double> debrisTTLExpiry
            = new Dictionary<uint, double>();

        // Pending background split checks: after OnBackgroundPartJointBreak,
        // we defer one frame to let KSP finalize the vessel split, then check.
        // The parentBoundaryPoint is captured at the actual joint-break UT so
        // parent continuation/closure can use an exact split-time pose instead
        // of backdating a later sample from the deferred check frame.
        private Dictionary<uint, (double branchUT, string recordingId, TrajectoryPoint? parentBoundaryPoint)>
            pendingBackgroundSplitChecks
                = new Dictionary<uint, (double, string, TrajectoryPoint?)>();

        // Pre-break snapshot of all vessel PIDs in FlightGlobals, per parent vessel.
        // Used to identify NEW vessels that appeared from the split.
        private Dictionary<uint, HashSet<uint>> preBreakVesselPidSnapshots
            = new Dictionary<uint, HashSet<uint>>();

        // Consumed on the first loaded-state init after a branch backgrounds a vessel.
        // This survives the "backgrounded while not yet loaded" path used by EVA splits.
        private Dictionary<uint, SegmentEnvironment> pendingInitialEnvironmentOverrides
            = new Dictionary<uint, SegmentEnvironment>();

        // Optional branch-boundary point captured when a split child is first created.
        // Consumed when the vessel opens its first loaded background TrackSection so the
        // recording has a real playable payload at the split moment instead of waiting for
        // the first later proximity sample.
        private Dictionary<uint, TrajectoryPoint> pendingInitialTrajectoryPoints
            = new Dictionary<uint, TrajectoryPoint>();

        // Focused-vessel breakup can create debris before the active parent's open
        // TrackSection has been flushed into tree.Recordings. Queue the parent
        // structural-event point so the debris seed can still use the split-UT
        // parent pose instead of the live parent pose at background initialization.
        private Dictionary<uint, (string parentRecordingId, TrajectoryPoint point)> pendingDebrisSeedParentAnchorPoints
            = new Dictionary<uint, (string, TrajectoryPoint)>();

        // Reusable buffer for transition-check methods (avoids per-frame List<PartEvent> allocations)
        private readonly List<PartEvent> reusableEventBuffer = new List<PartEvent>();

        // Robotic sampling constants (mirrors FlightRecorder)
        private const double roboticSampleIntervalSeconds = 0.25;
        private const float roboticAngularDeadbandDegrees = 0.5f;
        private const float roboticLinearDeadbandMeters = 0.01f;
        private const float backgroundAttitudeSampleThresholdDegrees = 1.0f;

        // Injectable vessel finder for CheckpointAllVessels (null = use FlightRecorder.FindVesselByPid)
        private System.Func<uint, Vessel> vesselFinderOverride;

        // Injectable distance override for testing (null = use FlightGlobals.ActiveVessel)
        private System.Func<Vessel, double> distanceOverrideForTesting;
        private readonly List<RecordingAnchorCandidate> backgroundAnchorCandidateBuffer =
            new List<RecordingAnchorCandidate>();
        private readonly HashSet<string> backgroundAnchorVisitedRecordingIds =
            new HashSet<string>(StringComparer.Ordinal);

        public BackgroundRecorder(RecordingTree tree)
        {
            this.tree = tree;

            // Initialize on-rails state for each existing background vessel.
            // Skip destroyed recordings: a sealed recording must not be in onRailsStates
            // (the UpdateOnRails / FinalizeAllForCommit secondary guards would catch
            // it, but the invariant "destroyed recordings are not in BG-tracking
            // structures" is cleaner). This also covers legacy saves that may have
            // left a Destroyed-terminated recording in BackgroundMap.
            foreach (var kvp in tree.BackgroundMap)
            {
                uint vesselPid = kvp.Key;
                string recordingId = kvp.Value;

                if (IsBackgroundRecordingDestroyed(recordingId)) continue;

                onRailsStates[vesselPid] = new BackgroundOnRailsState
                {
                    vesselPid = vesselPid,
                    recordingId = recordingId,
                    hasOpenOrbitSegment = false,
                    isLanded = false,
                    lastExplicitEndUpdate = -1
                };
            }
        }

        #region Inner Classes

        // INVARIANT: on-rails BG vessels never produce env-classified per-frame TrackSections.
        // This class deliberately omits the `currentTrackSection` / `trackSections` /
        // `environmentHysteresis` fields that BackgroundVesselState (loaded mode) carries.
        // An on-rails BG vessel grazing atmosphere across N orbits therefore cannot generate
        // optimizer-splittable Atmospheric<->ExoBallistic toggles. The packed path may emit
        // OrbitalCheckpoint sections when closed orbit segments are committed, but those
        // sections are orbit-only bridge payloads, not per-frame environment classifications.
        // See `OnBackgroundPhysicsFrame`'s early-return on `bgVessel.packed`. Adding a
        // TrackSection field or EnvironmentHysteresis here would resurrect the eccentric-orbit
        // chain-explosion failure mode flagged in
        // `docs/dev/research/extending-rewind-to-stable-leaves.md` §S16; do not.
        private class BackgroundOnRailsState
        {
            public uint vesselPid;
            public string recordingId;
            public bool hasOpenOrbitSegment;
            public OrbitSegment currentOrbitSegment;
            public bool isLanded;
            public double lastExplicitEndUpdate;
        }

        private class BackgroundVesselState
        {
            public uint vesselPid;
            public string recordingId;

            // Adaptive sampling state
            public double lastRecordedUT = -1;
            public Vector3 lastRecordedVelocity;
            public Quaternion lastWorldRotation;
            public bool hasLastWorldRotation;

            // Proximity-based sample interval tracking
            public double currentSampleInterval = ProximityRateSelector.OutOfRangeInterval;
            public double highFidelitySamplingUntilUT = double.NaN;
            public string highFidelitySamplingReason;
            public bool loggedFirstDebrisOrdinarySample;

            // Part event tracking (mirrors FlightRecorder's instance fields)
            public Dictionary<uint, int> parachuteStates = new Dictionary<uint, int>();
            public HashSet<uint> jettisonedShrouds = new HashSet<uint>();
            public Dictionary<ulong, string> jettisonNameRawCache = new Dictionary<ulong, string>();
            public Dictionary<ulong, string[]> parsedJettisonNamesCache = new Dictionary<ulong, string[]>();
            public HashSet<uint> extendedDeployables = new HashSet<uint>();
            public HashSet<uint> lightsOn = new HashSet<uint>();
            public HashSet<uint> blinkingLights = new HashSet<uint>();
            public Dictionary<uint, float> lightBlinkRates = new Dictionary<uint, float>();
            public HashSet<uint> deployedGear = new HashSet<uint>();
            public HashSet<uint> openCargoBays = new HashSet<uint>();
            public HashSet<uint> deployedFairings = new HashSet<uint>();
            public HashSet<ulong> deployedLadders = new HashSet<ulong>();
            public HashSet<ulong> deployedAnimationGroups = new HashSet<ulong>();
            public HashSet<ulong> deployedAnimateGenericModules = new HashSet<ulong>();
            public HashSet<ulong> deployedAeroSurfaceModules = new HashSet<ulong>();
            public HashSet<ulong> deployedControlSurfaceModules = new HashSet<ulong>();
            public HashSet<ulong> deployedRobotArmScannerModules = new HashSet<ulong>();
            public Dictionary<ulong, HeatLevel> animateHeatLevels = new Dictionary<ulong, HeatLevel>();

            // Engine/RCS/robotic caches
            public List<(Part part, ModuleEngines engine, int moduleIndex)> cachedEngines;
            public HashSet<ulong> activeEngineKeys = new HashSet<ulong>();
            public HashSet<ulong> allEngineKeys = new HashSet<ulong>(); // all engine modules, active + inactive (#298)
            public Dictionary<ulong, float> lastThrottle = new Dictionary<ulong, float>();
            public HashSet<ulong> loggedEngineModuleKeys = new HashSet<ulong>();
            public List<(Part part, ModuleRCS rcs, int moduleIndex)> cachedRcsModules;
            public HashSet<ulong> activeRcsKeys = new HashSet<ulong>();
            public Dictionary<ulong, float> lastRcsThrottle = new Dictionary<ulong, float>();
            public Dictionary<ulong, int> rcsActiveFrameCount = new Dictionary<ulong, int>();
            public HashSet<ulong> loggedRcsModuleKeys = new HashSet<ulong>();
            public List<(Part part, PartModule module, int moduleIndex, string moduleName)> cachedRoboticModules;
            public HashSet<ulong> activeRoboticKeys = new HashSet<ulong>();
            public Dictionary<ulong, float> lastRoboticPosition = new Dictionary<ulong, float>();
            public Dictionary<ulong, double> lastRoboticSampleUT = new Dictionary<ulong, double>();
            public HashSet<ulong> loggedRoboticModuleKeys = new HashSet<ulong>();

            // Environment tracking (TrackSection management)
            public EnvironmentHysteresis environmentHysteresis;
            public TrackSection currentTrackSection;
            public bool trackSectionActive;
            public List<TrackSection> trackSections = new List<TrackSection>();
            public bool isRelativeMode;
            public string currentAnchorRecordingId;
            public RecordingAnchorCandidate currentAnchorCandidate;
            public bool hasCurrentAnchorCandidate;
            public ProximitySamplingTier debrisProximityTier = ProximitySamplingTier.None;
            public double debrisProximityDistanceMeters = double.NaN;
            public string debrisProximityReason;

            // Phase 7: per-SurfaceMobile-section ground-clearance accumulators
            // (background side mirror of FlightRecorder's foreground state).
            // Reset on `StartBackgroundTrackSection`, populated by the
            // background sampler for Absolute SurfaceMobile/SurfaceStationary
            // sections, and summarised at section close so a `.prec` written
            // from background recording surfaces the same clearance
            // distribution diagnostic as foreground (HR-9 visibility parity).
            // Field names retain the original SurfaceMobile wording.
            public int surfaceMobileSamplesThisSection;
            public double surfaceMobileMinClearanceThisSection = double.NaN;
            public double surfaceMobileMaxClearanceThisSection = double.NaN;
            public double surfaceMobileClearanceSumThisSection;

            // Per-frame warp flags for the current section, index-aligned with
            // currentTrackSection.frames (flag[i] == true means frame i was
            // sampled under physics time-warp). Used at section close to
            // classify each large gap: a gap touching a warp sample is a
            // structurally-expected jump (Verbose), a large gap whose both ends
            // were at 1x is a genuine dropped-sample signal (WARN). On-rails BG
            // samples never reach the per-frame tick (OnBackgroundPhysicsFrame
            // early-returns on bgVessel.packed), so physics warp is the only
            // signal here. Reset on StartBackgroundTrackSection, trimmed in
            // lockstep with frames in TrimParentAtBranchBoundary.
            public readonly List<bool> sectionFrameWarpFlags = new List<bool>();

            // Part destruction/decoupling tracking
            public HashSet<uint> decoupledPartIds = new HashSet<uint>();

            // Diagnostic guards (prevent log spam, one per module type)
            public HashSet<ulong> loggedLadderClassificationMisses = new HashSet<ulong>();
            public HashSet<ulong> loggedAnimationGroupClassificationMisses = new HashSet<ulong>();
            public HashSet<ulong> loggedAnimateGenericClassificationMisses = new HashSet<ulong>();
            public HashSet<ulong> loggedAeroSurfaceClassificationMisses = new HashSet<ulong>();
            public HashSet<ulong> loggedControlSurfaceClassificationMisses = new HashSet<ulong>();
            public HashSet<ulong> loggedRobotArmScannerClassificationMisses = new HashSet<ulong>();
            public HashSet<ulong> loggedAnimateHeatClassificationMisses = new HashSet<ulong>();
            public HashSet<uint> loggedCargoBayDeployIndexIssues = new HashSet<uint>();
            public HashSet<uint> loggedCargoBayAnimationIssues = new HashSet<uint>();
            public HashSet<uint> loggedCargoBayClosedPositionIssues = new HashSet<uint>();
            public HashSet<uint> loggedFairingReadFailures = new HashSet<uint>();
        }

        #endregion

        internal struct BackgroundStateDriftSummary
        {
            public int BackgroundMapCount;
            public int MissingRecording;
            public int MissingTrackingState;
            public int LoadedWithoutLoadedState;
            public int PackedWithoutOnRailsState;
            public int PackedButLoadedState;
            public int LoadedButOnRailsState;

            public bool HasDrift =>
                MissingRecording > 0
                || MissingTrackingState > 0
                || LoadedWithoutLoadedState > 0
                || PackedWithoutOnRailsState > 0
                || PackedButLoadedState > 0
                || LoadedButOnRailsState > 0;

            internal string BuildStateKey()
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "map={0}|missingRec={1}|missingState={2}|loadedNoState={3}|packedNoRails={4}|packedLoadedState={5}|loadedRailsState={6}",
                    BackgroundMapCount,
                    MissingRecording,
                    MissingTrackingState,
                    LoadedWithoutLoadedState,
                    PackedWithoutOnRailsState,
                    PackedButLoadedState,
                    LoadedButOnRailsState);
            }
        }

        internal static string FormatBackgroundStateDriftSummary(
            BackgroundStateDriftSummary summary,
            string reason)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Background map/state drift: reason={0} map={1} missingRecording={2} missingTrackingState={3} loadedWithoutLoadedState={4} packedWithoutOnRailsState={5} packedButLoadedState={6} loadedButOnRailsState={7}",
                string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                summary.BackgroundMapCount,
                summary.MissingRecording,
                summary.MissingTrackingState,
                summary.LoadedWithoutLoadedState,
                summary.PackedWithoutOnRailsState,
                summary.PackedButLoadedState,
                summary.LoadedButOnRailsState);
        }

        internal static string FormatBackgroundVesselStateDrift(
            uint vesselPid,
            string recordingId,
            string reason,
            bool vesselLoaded,
            bool vesselPacked,
            bool hasLoadedState,
            bool hasOnRailsState,
            bool hasRecording)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Background vessel state drift: pid={0} recId={1} reason={2} vesselLoaded={3} vesselPacked={4} hasLoadedState={5} hasOnRailsState={6} hasRecording={7}",
                vesselPid,
                string.IsNullOrEmpty(recordingId) ? "(null)" : recordingId,
                string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                vesselLoaded,
                vesselPacked,
                hasLoadedState,
                hasOnRailsState,
                hasRecording);
        }

        #region GameEvent Subscriptions

        /// <summary>
        /// Subscribes to GameEvents for discrete part events (onPartDie, onPartJointBreak)
        /// that cannot be captured by per-frame polling. These events fire globally for all
        /// vessels; the handlers check if the part belongs to a background vessel.
        /// </summary>
        internal void SubscribePartEvents()
        {
            if (partEventsSubscribed) return;
            GameEvents.onPartDie.Add(OnBackgroundPartDie);
            GameEvents.onPartJointBreak.Add(OnBackgroundPartJointBreak);
            partEventsSubscribed = true;
            ParsekLog.Verbose("BgRecorder", "Subscribed to onPartDie and onPartJointBreak for background vessels");
        }

        internal void UnsubscribePartEvents()
        {
            if (!partEventsSubscribed) return;
            GameEvents.onPartDie.Remove(OnBackgroundPartDie);
            GameEvents.onPartJointBreak.Remove(OnBackgroundPartJointBreak);
            partEventsSubscribed = false;
            ParsekLog.Verbose("BgRecorder", "Unsubscribed from onPartDie and onPartJointBreak");
        }

        /// <summary>
        /// Handles part death for background vessels. Looks up the dying part's vessel
        /// in the tree's BackgroundMap; if found and in loaded state, emits a Destroyed
        /// or ParachuteDestroyed event.
        /// </summary>
        internal void OnBackgroundPartDie(Part p)
        {
            if (tree == null) return;
            if (p?.vessel == null)
            {
                ParsekLog.VerboseRateLimited("BgRecorder", "bg-part-die-null",
                    "OnBackgroundPartDie: part or vessel is null");
                return;
            }

            uint vesselPid = p.vessel.persistentId;

            // Only handle background vessels (not the active/focused vessel)
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(vesselPid, out recordingId)) return;

            // Destroyed-recording guard: do not append new part events to a sealed
            // recording. The recording's PartEvents list is part of its terminal
            // payload — appending here after destruction would shift playback events
            // past the identity-loss UT.
            if (IsBackgroundRecordingDestroyed(recordingId))
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"destroyed-skip.part_die.{vesselPid}",
                    $"OnBackgroundPartDie: skipping destroyed recording pid={vesselPid} rec={recordingId}",
                    60.0);
                return;
            }

            // Must have loaded state (part events only meaningful for loaded vessels)
            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(vesselPid, out state)) return;

            Recording treeRec;
            if (!tree.Recordings.TryGetValue(recordingId, out treeRec)) return;

            bool hasChute = p.FindModuleImplementing<ModuleParachute>() != null;
            var evtType = FlightRecorder.ClassifyPartDeath(
                p.persistentId, hasChute, state.parachuteStates);

            treeRec.PartEvents.Add(new PartEvent
            {
                ut = Planetarium.GetUniversalTime(),
                partPersistentId = p.persistentId,
                eventType = evtType,
                partName = p.partInfo?.name ?? "unknown"
            });
            treeRec.MarkFilesDirty();

            ParsekLog.Verbose("BgRecorder",
                $"Part death on background vessel: {evtType} '{p.partInfo?.name}' " +
                $"pid={p.persistentId} vesselPid={vesselPid}");
            RefreshFinalizationCacheForVessel(p.vessel, recordingId, FinalizationCacheOwner.BackgroundLoaded,
                "background_part_die", force: true);
        }

        /// <summary>
        /// Handles part joint breaks for background vessels. Looks up the child part's
        /// vessel in the tree's BackgroundMap; if found and in loaded state, emits a
        /// Decoupled event (with structural-joint and dedup guards).
        /// </summary>
        internal void OnBackgroundPartJointBreak(PartJoint joint, float breakForce)
        {
            if (tree == null) return;
            if (joint?.Child?.vessel == null)
            {
                ParsekLog.VerboseRateLimited("BgRecorder", "bg-joint-break-null",
                    "OnBackgroundPartJointBreak: joint, child, or vessel is null");
                return;
            }

            uint vesselPid = joint.Child.vessel.persistentId;

            // Only handle background vessels
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(vesselPid, out recordingId)) return;

            // Destroyed-recording guard: a sealed recording must not accept new
            // joint-break / decouple events after destruction.
            if (IsBackgroundRecordingDestroyed(recordingId))
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"destroyed-skip.part_joint_break.{vesselPid}",
                    $"OnBackgroundPartJointBreak: skipping destroyed recording pid={vesselPid} rec={recordingId}",
                    60.0);
                return;
            }

            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(vesselPid, out state)) return;

            Recording treeRec;
            if (!tree.Recordings.TryGetValue(recordingId, out treeRec)) return;

            // Skip non-structural joint breaks (same logic as FlightRecorder)
            var childAttachJoint = joint.Child.attachJoint;
            if (!FlightRecorder.IsStructuralJointBreak(joint == childAttachJoint, childAttachJoint != null))
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"bg-joint-break-nonstructural-{joint.Child.persistentId}",
                    $"OnBackgroundPartJointBreak: non-structural joint break on " +
                    $"'{joint.Child.partInfo?.name}' pid={joint.Child.persistentId} " +
                    $"vesselPid={vesselPid} (breakForce={breakForce:F1}), skipping");
                return;
            }

            // Skip duplicate decoupled events for the same part
            if (state.decoupledPartIds.Contains(joint.Child.persistentId))
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"bg-joint-break-dup-{joint.Child.persistentId}",
                    $"OnBackgroundPartJointBreak: duplicate for already-decoupled " +
                    $"'{joint.Child.partInfo?.name}' pid={joint.Child.persistentId} " +
                    $"vesselPid={vesselPid}, skipping");
                return;
            }
            state.decoupledPartIds.Add(joint.Child.persistentId);
            double jointBreakUT = Planetarium.GetUniversalTime();

            treeRec.PartEvents.Add(new PartEvent
            {
                ut = jointBreakUT,
                partPersistentId = joint.Child.persistentId,
                eventType = PartEventType.Decoupled,
                partName = joint.Child.partInfo?.name ?? "unknown"
            });
            treeRec.MarkFilesDirty();

            ParsekLog.Verbose("BgRecorder",
                $"Part joint break on background vessel: Decoupled " +
                $"'{joint.Child.partInfo?.name}' pid={joint.Child.persistentId} " +
                $"vesselPid={vesselPid} breakForce={breakForce:F1}");

            var jointBreakInvolved = new List<Vessel>(2);
            if (joint.Child.vessel != null) jointBreakInvolved.Add(joint.Child.vessel);
            if (joint.Host?.vessel != null && joint.Host.vessel != joint.Child.vessel)
                jointBreakInvolved.Add(joint.Host.vessel);
            AppendStructuralEventSnapshot(
                jointBreakUT, jointBreakInvolved, "JointBreak", preferRootPartSurfacePose: true);

            RefreshFinalizationCacheForVessel(joint.Child.vessel, recordingId, FinalizationCacheOwner.BackgroundLoaded,
                "background_joint_break", force: true);

            // Schedule a deferred split check for this background vessel.
            // KSP needs one frame to finalize vessel splits after joint breaks.
            if (!pendingBackgroundSplitChecks.ContainsKey(vesselPid))
            {
                double branchUT = jointBreakUT;

                // Snapshot all current vessel PIDs so we can identify NEW ones next frame
                var snapshot = new HashSet<uint>();
                if (FlightGlobals.Vessels != null)
                {
                    for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
                    {
                        Vessel v = FlightGlobals.Vessels[i];
                        if (v == null) continue;
                        if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
                        snapshot.Add(v.persistentId);
                    }
                }
                preBreakVesselPidSnapshots[vesselPid] = snapshot;
                TrajectoryPoint? parentBoundaryPoint = joint.Child.vessel != null
                    ? (TrajectoryPoint?)CreateAbsoluteTrajectoryPointFromVessel(
                        joint.Child.vessel, branchUT, preferRootPartSurfacePose: true)
                    : null;
                if (parentBoundaryPoint.HasValue)
                    parentBoundaryPoint = FlightRecorder.ApplyStructuralEventFlag(parentBoundaryPoint.Value);
                pendingBackgroundSplitChecks[vesselPid] = (branchUT, recordingId, parentBoundaryPoint);

                ParsekLog.Info("BgRecorder",
                    $"Scheduled deferred split check for background vessel: " +
                    $"parentPid={vesselPid} branchUT={branchUT:F1} " +
                    $"preBreakVesselCount={snapshot.Count}");
            }
        }

        #endregion

        #region Background Vessel Split Detection

        /// <summary>
        /// Called from Update() to process any pending background split checks.
        /// Must be called one frame after the joint break event to allow KSP to
        /// finalize the vessel split.
        /// </summary>
        internal void ProcessPendingSplitChecks()
        {
            if (pendingBackgroundSplitChecks.Count == 0) return;

            // Copy keys to avoid modifying dict during iteration
            var pending = new List<KeyValuePair<uint, (double branchUT, string recordingId, TrajectoryPoint? parentBoundaryPoint)>>(
                pendingBackgroundSplitChecks);
            pendingBackgroundSplitChecks.Clear();

            for (int i = 0; i < pending.Count; i++)
            {
                uint parentPid = pending[i].Key;
                double branchUT = pending[i].Value.branchUT;
                string parentRecordingId = pending[i].Value.recordingId;
                TrajectoryPoint? parentBoundaryPoint = pending[i].Value.parentBoundaryPoint;

                HashSet<uint> preBreakPids;
                preBreakVesselPidSnapshots.TryGetValue(parentPid, out preBreakPids);
                preBreakVesselPidSnapshots.Remove(parentPid);

                HandleBackgroundVesselSplit(
                    parentPid, branchUT, parentRecordingId, preBreakPids, parentBoundaryPoint);
            }
        }

        /// <summary>
        /// Called when a background vessel may have split. Checks if new vessels appeared
        /// from the split, creates BranchPoint + child recordings for each new vessel.
        /// Spent stages and debris get a TTL (stop recording after 30s or on destruction
        /// or when leaving physics bubble).
        /// </summary>
        internal void HandleBackgroundVesselSplit(uint parentPid, double branchUT,
            string parentRecordingId, HashSet<uint> preBreakPids,
            TrajectoryPoint? parentBoundaryPoint = null)
        {
            if (tree == null) return;

            Recording parentRec;
            if (!tree.Recordings.TryGetValue(parentRecordingId, out parentRec))
            {
                ParsekLog.Warn("BgRecorder",
                    $"HandleBackgroundVesselSplit: parent recording not found: " +
                    $"parentPid={parentPid} recId={parentRecordingId}");
                return;
            }

            // Destroyed-parent guard: defense-in-depth for the (joint-break → next-frame
            // identity-loss-on-go-on-rails) race. The retirement seam at
            // OnBackgroundVesselGoOnRails drains pendingBackgroundSplitChecks /
            // preBreakVesselPidSnapshots, so ProcessPendingSplitChecks should not
            // normally dispatch here for a sealed parent. This guard pins the
            // invariant: a Destroyed parent does not spawn branch-point children.
            if (parentRec.VesselDestroyed)
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"destroyed-skip.split.{parentPid}",
                    $"HandleBackgroundVesselSplit: skipping destroyed parent " +
                    $"parentPid={parentPid} rec={parentRecordingId}",
                    60.0);
                return;
            }

            var newVesselInfos = CollectNewBackgroundSplitVessels(parentPid, preBreakPids);

            if (newVesselInfos.Count == 0)
            {
                ParsekLog.Verbose("BgRecorder",
                    $"HandleBackgroundVesselSplit: no new vessels from parentPid={parentPid} — " +
                    $"joint break did not cause vessel split");
                return;
            }

            // Cascade depth cap (#284): if the parent recording is already at the
            // generation cap, do not create any branch, child recordings, or parent
            // continuation. The parent stays in BackgroundMap/loadedStates and keeps
            // accumulating samples into its existing recording until destruction.
            // The new fragment vessels remain alive in KSP but become Parsek-orphans.
            // Logged at Info because it represents a real recording-policy decision
            // visible in the user-facing recording count, and only fires for
            // secondary/tertiary breakups (not per frame).
            if (ShouldSkipForCascadeCap(parentRec.Generation))
            {
                ParsekLog.Info("BgRecorder",
                    $"Cascade depth cap fired: skipping background split — " +
                    $"parentRec={parentRec.DebugName} gen={parentRec.Generation} " +
                    $"parentPid={parentPid} skippedChildren={newVesselInfos.Count} " +
                    $"branchUT={branchUT:F1}");
                return;
            }

            // A split occurred. Build the branch structure.
            ParsekLog.Info("BgRecorder",
                $"Background vessel split detected: parentPid={parentPid} " +
                $"childCount={newVesselInfos.Count} branchUT={branchUT:F1}");

            // Determine BranchPoint type: JointBreak (structural separation)
            var branchType = BranchPointType.JointBreak;

            // Build a BranchPoint and child recordings using our pure static method.
            // Children inherit parentRec.Generation + 1.
            var result = BuildBackgroundSplitBranchData(
                parentRecordingId, tree.Id, branchUT, branchType,
                parentPid, newVesselInfos, parentRec.Generation);

            var bp = result.bp;
            var childRecordings = result.childRecordings;

            // Bug #298: snapshot parent engine/RCS state before CloseParentRecording
            // destroys loadedStates[parentPid].
            BackgroundVesselState parentLoaded;
            loadedStates.TryGetValue(parentPid, out parentLoaded);
            InheritedEngineState? parentEngineState = InheritedStateFromBackgroundVessel(parentLoaded);

            // Close parent recording: set ChildBranchPointId, close orbit segment/trajectory
            CloseParentRecording(parentRec, parentPid, bp.Id, branchUT, parentBoundaryPoint);

            // Add BranchPoint to tree
            tree.BranchPoints.Add(bp);

            // Only create a parent continuation if the parent vessel still exists.
            // If the parent was destroyed before the deferred split check ran (bug #285),
            // skip the continuation — the parent recording is already closed above with
            // ChildBranchPointId set. Creating a continuation for a dead vessel would
            // produce an empty Recording (zero points) that clutters disk and logs.
            Vessel parentVessel = FlightRecorder.FindVesselByPid(parentPid);
            int continuationCount = 0;
            // Phase 4 (Rewind-to-Staging, §5.1): track the parent continuation recording
            // outside the if-branch so the post-registration RP hook can include it as a
            // candidate controllable child if the parent is still alive and trackable.
            Recording parentContRec = null;

            if (parentVessel != null)
            {
                // Create a continuation recording for the parent vessel itself
                // (it keeps existing with potentially fewer parts).
                // The continuation stays at the same Generation as the parent — it's
                // the same logical vessel, just with fewer parts. Only spinoff children
                // get parentGeneration + 1.
                // NOTE: with MaxRecordingGeneration=1 the cap above this point already
                // returned for any parentRec.Generation >= 1, so today this assignment
                // is always Generation=0. Kept explicit so future cap bumps (e.g.
                // MaxRecordingGeneration=2) just work without re-auditing this site.
                string parentContRecId = System.Guid.NewGuid().ToString("N");
                parentContRec = new Recording
                {
                    RecordingId = parentContRecId,
                    TreeId = tree.Id,
                    VesselPersistentId = parentPid,
                    VesselName = parentRec.VesselName,
                    ParentBranchPointId = bp.Id,
                    ExplicitStartUT = branchUT,
                    IsDebris = parentRec.IsDebris,
                    // Forward-compat propagation (plan Decision §10): MaxRecordingGeneration=1
                    // means parent continuations of *debris* recordings are never created
                    // today, but if Step 4a raises the cap, the contract must already be in
                    // place — see plan §"`IsDebris` propagation surface" site #8.
                    ParentAnchorRecordingId = parentRec.ParentAnchorRecordingId,
                    Generation = parentRec.Generation,
                    // Pin start-of-recording controller identity for the parent continuation
                    // from the live post-split parent vessel. Captures the parts that
                    // stayed with the parent after the split, so a later destructive
                    // crash on the continuation can be detected via identity loss.
                    Controllers = ControllerInfo.CaptureFromVessel(parentVessel)
                };
                bp.ChildRecordingIds.Insert(0, parentContRecId);
                tree.AddOrReplaceRecording(parentContRec);
                tree.BackgroundMap[parentPid] = parentContRecId;

                // Re-initialize tracking state for the parent continuation.
                // Seed the continuation with the exact split-time pose so playback can
                // start from the branch boundary instead of waiting for the next sample.
                TrajectoryPoint? parentInitialPoint = parentBoundaryPoint;
                if (!parentInitialPoint.HasValue && parentVessel != null)
                {
                    double sampleUT = Planetarium.GetUniversalTime();
                    parentInitialPoint = CreateAbsoluteTrajectoryPointFromVessel(
                        parentVessel, sampleUT, preferRootPartSurfacePose: true);
                }
                OnVesselBackgrounded(parentPid, parentEngineState,
                    initialTrajectoryPoint: parentInitialPoint);

                // Set TTL for parent continuation if it's debris
                if (parentRec.IsDebris)
                {
                    debrisTTLExpiry[parentPid] = branchUT + DebrisTTLSeconds;
                    ParsekLog.Info("BgRecorder",
                        $"Parent continuation is debris, TTL set: parentPid={parentPid} " +
                        $"expiry={branchUT + DebrisTTLSeconds:F1}");
                }

                continuationCount = 1;
            }
            else
            {
                ParsekLog.Info("BgRecorder",
                    $"Skipping parent continuation — parent vessel destroyed: " +
                    $"parentPid={parentPid} parentRec={parentRec.DebugName}");
            }

            // PR 3b review follow-up: when the parent vessel still exists, the
            // continuation recording (`parentContRec`) is the one that will keep
            // receiving samples for the parent's ongoing trajectory — the original
            // `parentRecordingId` was just closed by `CloseParentRecording` above
            // (its `ChildBranchPointId` is set, `ExplicitEndUT == branchUT`).
            // Anchoring debris to the closed pre-split id would render every
            // post-`branchUT` debris sample unresolvable at playback and trigger an
            // immediate "parent recording finalized" exit from `CheckDebrisTTL`.
            //
            // When no continuation was created (parent vessel destroyed at the same
            // frame as the split, the "Skipping parent continuation" branch above),
            // fall back to the original parent id — debris in that case will be
            // ended quickly by the parent-vessel-destroyed branch in CheckDebrisTTL,
            // and the closed-parent fallback is the only id available.
            string anchorParentRecordingId = parentContRec != null
                ? parentContRec.RecordingId
                : parentRecordingId;
            RegisterChildRecordingsFromSplit(childRecordings, newVesselInfos, branchUT, anchorParentRecordingId, parentEngineState);

            ParsekLog.Info("BgRecorder",
                $"Background split branch complete: bp={bp.Id} type={branchType} " +
                $"parentRecId={parentRecordingId} " +
                $"anchorParentRecId={anchorParentRecordingId} " +
                $"children={bp.ChildRecordingIds.Count} " +
                $"({continuationCount} parent continuation + {newVesselInfos.Count} new vessels)");

            // Phase 4 (Rewind-to-Staging, design §5.1 + §7.1): if the background split
            // produced >=2 controllable outputs (surviving parent continuation + each
            // newVesselInfo with hasController=true), author a RewindPoint. Mirrors
            // the active-vessel path in ParsekFlight.TryAuthorRewindPointForSplit.
            TryAuthorRewindPointForBackgroundSplit(
                bp, parentVessel, parentContRec, newVesselInfos, childRecordings);
        }

        private List<(uint pid, string name, bool hasController)> CollectNewBackgroundSplitVessels(
            uint parentPid,
            HashSet<uint> preBreakPids)
        {
            // Find new vessel PIDs that appeared since the pre-break snapshot
            var newVesselInfos = new List<(uint pid, string name, bool hasController)>();
            if (FlightGlobals.Vessels != null)
            {
                for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
                {
                    Vessel v = FlightGlobals.Vessels[i];
                    if (v == null) continue;
                    if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
                    if (v.persistentId == parentPid) continue;

                    // Only consider vessels that didn't exist before the break
                    if (preBreakPids != null && preBreakPids.Contains(v.persistentId))
                        continue;

                    // Skip vessels we're already tracking in the tree
                    if (tree.BackgroundMap.ContainsKey(v.persistentId))
                        continue;

                    if (!ParsekFlight.ShouldIncludeVesselInDeferredBreakupScan(v, out string rejectReason))
                    {
                        ParsekLog.Verbose("BgRecorder",
                            $"Background split scan: ignoring unrelated new vessel pid={v.persistentId} " +
                            $"name='{v.vesselName ?? "<unnamed>"}' type={v.vesselType} reason={rejectReason}");
                        continue;
                    }

                    bool hasController = ParsekFlight.IsTrackableVessel(v);
                    newVesselInfos.Add((v.persistentId, Recording.ResolveLocalizedName(v.vesselName) ?? "Unknown", hasController));
                }
            }

            return newVesselInfos;
        }

        /// <summary>
        /// Phase 4 (Rewind-to-Staging): background-split analogue of
        /// <c>ParsekFlight.TryAuthorRewindPointForSplit</c>. Builds controllable
        /// child slots + a recording-resolver from the live background vessels and
        /// delegates to <see cref="RewindPointAuthor.Begin"/>.
        ///
        /// <para>
        /// Background vessels <i>are</i> in <c>FlightGlobals.Vessels</c> at split
        /// time — the joint-break physics that caused the split only fires when the
        /// vessel is loaded, so <c>rootPart</c> is accessible here just like on the
        /// active-vessel path. If the parent vessel has already been destroyed
        /// (<c>parentVessel == null</c>) the parent continuation slot is skipped;
        /// remaining controllable children still contribute.
        /// </para>
        /// </summary>
        private void TryAuthorRewindPointForBackgroundSplit(
            BranchPoint bp,
            Vessel parentVessel,
            Recording parentContRec,
            List<(uint pid, string name, bool hasController)> newVesselInfos,
            List<Recording> childRecordings)
        {
            if (bp == null)
            {
                ParsekLog.Warn("Rewind",
                    "Background split: RP skipped (bp is null) — this is a bug");
                return;
            }

            // Collect candidate (vessel, recording) pairs for controllable outputs.
            var pairs = new List<(Vessel vessel, Recording rec)>();
            var candidatePids = new List<uint>();

            if (parentVessel != null && parentContRec != null
                && ParsekFlight.IsTrackableVessel(parentVessel))
            {
                pairs.Add((parentVessel, parentContRec));
                candidatePids.Add(parentVessel.persistentId);
            }

            // Children: walk newVesselInfos in parallel with childRecordings (same
            // order — BuildBackgroundSplitBranchData iterates newVesselInfos directly).
            int childCount = childRecordings != null ? childRecordings.Count : 0;
            for (int i = 0; i < childCount; i++)
            {
                if (i >= newVesselInfos.Count) break;
                var info = newVesselInfos[i];
                if (!info.hasController) continue;

                Vessel childVessel = FlightRecorder.FindVesselByPid(info.pid);
                if (childVessel == null)
                {
                    // Vessel vanished between split detection and RP hook. Skip — the
                    // RP would be unusable without a live vessel to correlate against.
                    ParsekLog.Warn("Rewind",
                        $"Background split: controllable child pid={info.pid} not in " +
                        $"FlightGlobals.Vessels at RP-hook time; skipping slot");
                    continue;
                }
                pairs.Add((childVessel, childRecordings[i]));
                candidatePids.Add(info.pid);
            }

            if (!SegmentBoundaryLogic.IsMultiControllableSplit(candidatePids.Count))
            {
                ParsekLog.Info("Rewind",
                    $"Background split: single-controllable, no RP " +
                    $"(bp={bp.Id} controllable={candidatePids.Count})");
                return;
            }

            // Build ChildSlots + recId->PID resolver (same contract as
            // ParsekFlight.AuthorRewindPointFromVesselRecordings).
            var slots = new List<ChildSlot>();
            var recIdToPid = new Dictionary<string, uint>(StringComparer.Ordinal);
            int slotIndex = 0;
            for (int i = 0; i < pairs.Count; i++)
            {
                var (vessel, rec) = pairs[i];
                if (vessel == null || rec == null) continue;
                if (string.IsNullOrEmpty(rec.RecordingId)) continue;

                slots.Add(new ChildSlot
                {
                    SlotIndex = slotIndex++,
                    OriginChildRecordingId = rec.RecordingId,
                    Controllable = true,
                    Disabled = false,
                    DisabledReason = null
                });
                recIdToPid[rec.RecordingId] = vessel.persistentId;
            }

            if (slots.Count < 2)
            {
                ParsekLog.Info("Rewind",
                    $"Background split: multi-controllable classifier passed " +
                    $"({candidatePids.Count}) but slot build produced only " +
                    $"{slots.Count} entries (bp={bp.Id}); skipping RP");
                return;
            }

            ParsekLog.Info("Rewind",
                $"Background split: authoring RP (bp={bp.Id} slots={slots.Count})");

            var ctx = new RewindPointAuthorContext
            {
                RecordingResolver = recId =>
                    recIdToPid.TryGetValue(recId ?? "", out var pid) ? (uint?)pid : null
            };

            RewindPointAuthor.Begin(bp, slots, candidatePids, ctx);
        }

        /// <summary>
        /// Closes the parent recording at a branch point: sets ChildBranchPointId, ExplicitEndUT,
        /// closes any open orbit/loaded track section, samples a final boundary point, and removes from tracking dicts.
        /// </summary>
        private void CloseParentRecording(Recording parentRec, uint parentPid, string branchPointId,
            double branchUT, TrajectoryPoint? parentBoundaryPoint = null)
        {
            parentRec.ChildBranchPointId = branchPointId;
            parentRec.ExplicitEndUT = branchUT;

            // Close parent's orbit segment if open
            BackgroundOnRailsState parentRails;
            if (onRailsStates.TryGetValue(parentPid, out parentRails) && parentRails.hasOpenOrbitSegment)
            {
                CloseOrbitSegment(parentRails, branchUT);
            }

            // Sample a final boundary point for the parent if loaded
            BackgroundVesselState parentLoaded;
            if (loadedStates.TryGetValue(parentPid, out parentLoaded))
            {
                TrimParentAtBranchBoundary(parentRec, parentLoaded, parentPid, branchUT);

                if (parentBoundaryPoint.HasValue)
                {
                    TrajectoryPoint boundary = parentBoundaryPoint.Value;
                    TryCanonicalizeBackgroundReFlyRecordingPoint(
                        parentRec.RecordingId,
                        ref boundary,
                        "parent-branch-boundary");
                    if (HasStructuralEventPointAtUT(parentRec, boundary.ut))
                    {
                        ParsekLog.Verbose("BgRecorder",
                            string.Format(CultureInfo.InvariantCulture,
                                "CloseParentRecording: structural boundary already recorded " +
                                "parentPid={0} recId={1} ut={2:R}",
                                parentPid,
                                parentRec.RecordingId,
                                boundary.ut));
                    }
                    else
                    {
                        ApplyTrajectoryPointToRecording(parentRec, boundary);
                    }
                }
                else
                {
                    Vessel parentVessel = FlightRecorder.FindVesselByPid(parentPid);
                    if (parentVessel != null)
                    {
                        SampleBoundaryPoint(parentVessel, parentRec, branchUT);
                    }
                }

                CloseBackgroundTrackSection(parentLoaded, branchUT);
                FlushTrackSectionsToRecording(parentLoaded, parentRec);
            }

            // Remove parent from BackgroundMap (it has branched, no longer a live recording)
            tree.BackgroundMap.Remove(parentPid);
            onRailsStates.Remove(parentPid);
            loadedStates.Remove(parentPid);
            debrisTTLExpiry.Remove(parentPid);
            finalizationCaches.Remove(parentPid);
        }

        // Walks rec.Points in reverse, breaking once we step earlier than
        // (ut - tolerance). Relies on Points being sorted ascending by ut —
        // every callsite (CloseParentRecording, the structural-event helpers)
        // appends in UT order, and TrimParentAtBranchBoundary preserves that
        // ordering by removing tail entries only. If a future caller appends
        // out of order, this scan will exit too early and miss matches.
        private static bool HasStructuralEventPointAtUT(Recording rec, double ut)
        {
            if (rec?.Points == null) return false;
            for (int i = rec.Points.Count - 1; i >= 0; i--)
            {
                TrajectoryPoint point = rec.Points[i];
                if (Math.Abs(point.ut - ut) > BranchBoundaryUTTolerance)
                {
                    if (point.ut < ut) break;
                    continue;
                }

                if (((TrajectoryPointFlags)point.flags & TrajectoryPointFlags.StructuralEventSnapshot) != 0)
                    return true;
            }

            return false;
        }

        private static void TrimParentAtBranchBoundary(
            Recording parentRec, BackgroundVesselState parentLoaded, uint parentPid, double branchUT)
        {
            int flatRemoved = TrimTrajectoryPointsAfterUT(parentRec?.Points, branchUT);
            int sectionFramesRemoved = 0;
            int bodyFixedFramesRemoved = 0;
            if (parentLoaded != null && parentLoaded.trackSectionActive)
            {
                // Trim the section frames and keep the per-section warp-flag list
                // index-aligned with them in one step (see the helper for why).
                sectionFramesRemoved = TrimSectionFramesAndWarpFlagsAfterUT(
                    parentLoaded.currentTrackSection.frames,
                    parentLoaded.sectionFrameWarpFlags,
                    branchUT);
                bodyFixedFramesRemoved = TrimTrajectoryPointsAfterUT(
                    parentLoaded.currentTrackSection.bodyFixedFrames, branchUT);
                if (sectionFramesRemoved > 0 || bodyFixedFramesRemoved > 0)
                    RecomputeCurrentTrackSectionAltitudeRange(parentLoaded);
            }

            int removed = flatRemoved + sectionFramesRemoved + bodyFixedFramesRemoved;
            if (removed == 0) return;

            if (parentRec != null)
            {
                parentRec.MarkFilesDirty();
                parentRec.CachedStats = null;
                parentRec.CachedStatsPointCount = 0;
            }

            ParsekLog.Warn("BgRecorder",
                string.Format(CultureInfo.InvariantCulture,
                    "CloseParentRecording: trimmed post-branch parent samples " +
                    "parentPid={0} recId={1} branchUT={2:R} flatRemoved={3} " +
                    "sectionFramesRemoved={4} bodyFixedFramesRemoved={5}",
                    parentPid,
                    parentRec?.RecordingId ?? "<null>",
                    branchUT,
                    flatRemoved,
                    sectionFramesRemoved,
                    bodyFixedFramesRemoved));
        }

        private static int TrimTrajectoryPointsAfterUT(List<TrajectoryPoint> points, double maxUT)
        {
            if (points == null || points.Count == 0) return 0;

            int removed = 0;
            double limit = maxUT + BranchBoundaryUTTolerance;
            for (int i = points.Count - 1; i >= 0; i--)
            {
                if (points[i].ut <= limit) continue;
                points.RemoveAt(i);
                removed++;
            }

            return removed;
        }

        /// <summary>
        /// Trims a section's <paramref name="frames"/> after <paramref name="maxUT"/>
        /// and truncates the index-aligned <paramref name="warpFlags"/> list back to
        /// the new frame count, restoring the 1:1 alignment the sparse-sampling
        /// per-gap classifier relies on.
        ///
        /// Frames are appended in UT order, so <see cref="TrimTrajectoryPointsAfterUT"/>
        /// removes a contiguous post-<paramref name="maxUT"/> tail; truncating the
        /// flags down to <c>frames.Count</c> drops the warp flags for exactly those
        /// removed tail samples. Without this the flags list stays longer than frames
        /// for the rest of the section's life and
        /// <see cref="FlightRecorder.ComputeSectionGapStats"/> falls back to its
        /// length-mismatch path (every gap counted as 1x =&gt; over-WARN) instead of
        /// per-gap warp classification. Mirrors the active-side
        /// <see cref="FlightRecorder.TrimRecordingToUT"/> lockstep trim.
        ///
        /// Pure (no recorder/Unity state) for direct unit testing. Returns the number
        /// of frames removed.
        /// </summary>
        internal static int TrimSectionFramesAndWarpFlagsAfterUT(
            List<TrajectoryPoint> frames, List<bool> warpFlags, double maxUT)
        {
            int framesRemoved = TrimTrajectoryPointsAfterUT(frames, maxUT);
            if (framesRemoved > 0 && warpFlags != null)
            {
                int frameCount = frames?.Count ?? 0;
                if (warpFlags.Count > frameCount)
                    warpFlags.RemoveRange(frameCount, warpFlags.Count - frameCount);
            }

            return framesRemoved;
        }

        private static void RecomputeCurrentTrackSectionAltitudeRange(BackgroundVesselState state)
        {
            if (state == null)
                return;

            if (state.currentTrackSection.frames == null
                || state.currentTrackSection.frames.Count == 0)
            {
                state.currentTrackSection.minAltitude = float.NaN;
                state.currentTrackSection.maxAltitude = float.NaN;
                return;
            }

            float min = float.NaN;
            float max = float.NaN;
            for (int i = 0; i < state.currentTrackSection.frames.Count; i++)
            {
                double altitude = state.currentTrackSection.frames[i].altitude;
                if (double.IsNaN(altitude)) continue;
                if (float.IsNaN(min) || altitude < min)
                    min = (float)altitude;
                if (float.IsNaN(max) || altitude > max)
                    max = (float)altitude;
            }

            state.currentTrackSection.minAltitude = min;
            state.currentTrackSection.maxAltitude = max;
        }

        /// <summary>
        /// Registers child recordings from a vessel split: captures snapshots, adds to tree
        /// and BackgroundMap, initializes tracking state, and sets TTL for debris children.
        ///
        /// Per plan §3b §"Primary creation sites — invocation and ordering" (PR 3b),
        /// `IsDebris` and the <see cref="Recording.ParentAnchorRecordingId"/> contract
        /// are set on the Recording BEFORE <see cref="RecordingTree.AddOrReplaceRecording"/>
        /// and BEFORE <see cref="OnVesselBackgrounded"/>. The seed-point orchestration in
        /// <see cref="InitializeLoadedState"/> reads these fields to decide whether to open
        /// the first track section as Relative-to-parent or as Absolute.
        /// </summary>
        private void RegisterChildRecordingsFromSplit(
            List<Recording> childRecordings,
            List<(uint pid, string name, bool hasController)> newVesselInfos,
            double branchUT,
            string parentRecordingId,
            InheritedEngineState? inherited = null)
        {
            // Capture vessel snapshots so ghosts can be built during playback
            // (without snapshots, GhostVisualBuilder returns null → no ghost appears).
            for (int i = 0; i < childRecordings.Count; i++)
            {
                var child = childRecordings[i];

                // Stamp IsDebris + ParentAnchorRecordingId BEFORE the tree write and the
                // OnVesselBackgrounded dispatch so InitializeLoadedState can read the contract
                // and open the first track section as Relative-to-parent. The helper is
                // caller-decides since the controlled-child extension: both debris
                // (IsDebris=true) and controlled-decoupled children (IsDebris=false) receive
                // the same parent-anchor surface while close to the parent.
                bool hasController = newVesselInfos[i].hasController;
                child.IsDebris = !hasController;
                Recording.ApplyParentAnchorContract(child, parentRecordingId);
                if (!string.IsNullOrEmpty(child.ParentAnchorRecordingId))
                {
                    string population = child.IsDebris ? "debris" : "controlled-child";
                    ParsekLog.Verbose("BgRecorder",
                        $"Parent-anchor contract applied at split: childRecId={child.RecordingId} " +
                        $"childPid={child.VesselPersistentId} population={population} " +
                        $"parentRecId={parentRecordingId ?? "(none)"}");
                }

                // Capture snapshot for ghost building — the vessel is still loaded at this point
                Vessel childVessel = FlightRecorder.FindVesselByPid(child.VesselPersistentId);
                if (childVessel != null)
                {
                    child.VesselSnapshot = VesselSpawner.TryBackupSnapshot(childVessel);
                    child.GhostVisualSnapshot = child.VesselSnapshot != null
                        ? child.VesselSnapshot.CreateCopy() : null;
                    child.StartResources = VesselSpawner.ExtractResourceManifest(child.VesselSnapshot);
                    int bgChildInvSlots;
                    child.StartInventory = VesselSpawner.ExtractInventoryManifest(child.VesselSnapshot, out bgChildInvSlots);
                    child.StartInventorySlots = bgChildInvSlots;
                    child.StartCrew = VesselSpawner.ExtractCrewManifest(child.VesselSnapshot);
                    // Pin start-of-recording controller identity from the live child vessel
                    // at split moment. Forwarded by Recording copy/clone paths so a later
                    // BG go-on-rails identity-loss check can detect destructive crash.
                    child.Controllers = ControllerInfo.CaptureFromVessel(childVessel);
                    ParsekLog.Verbose("BgRecorder",
                        $"Captured snapshot for child vessel: pid={child.VesselPersistentId} " +
                        $"name='{child.VesselName}' hasSnapshot={child.VesselSnapshot != null} " +
                        $"startResources={child.StartResources?.Count ?? 0} type(s) " +
                        $"startInventory={child.StartInventory?.Count ?? 0} item(s) " +
                        $"startCrew={child.StartCrew?.Count ?? 0} trait(s) " +
                        $"controllers={child.Controllers?.Count ?? 0} part(s)");
                }
                else
                {
                    ParsekLog.Warn("BgRecorder",
                        $"Could not capture snapshot for child vessel: pid={child.VesselPersistentId} " +
                        $"name='{child.VesselName}' — vessel not found, ghost will not render");
                }

                tree.AddOrReplaceRecording(child);
                tree.BackgroundMap[child.VesselPersistentId] = child.RecordingId;

                // Initialize tracking state for new child vessel. This check runs
                // one frame after the joint break, so any child seed captured here
                // must use the actual sample UT rather than the earlier branchUT.
                double sampleUT = Planetarium.GetUniversalTime();
                TrajectoryPoint? childInitialPoint = childVessel != null
                    ? (TrajectoryPoint?)CreateAbsoluteTrajectoryPointFromVessel(
                        childVessel, sampleUT, preferRootPartSurfacePose: true)
                    : null;
                OnVesselBackgrounded(child.VesselPersistentId, inherited,
                    initialTrajectoryPoint: childInitialPoint);

                if (!hasController)
                {
                    // Debris child: TTL keyed on branch UT, distinct logging
                    debrisTTLExpiry[child.VesselPersistentId] = branchUT + DebrisTTLSeconds;
                    ParsekLog.Info("BgRecorder",
                        $"Child recording created (debris, TTL={DebrisTTLSeconds:F0}s): " +
                        $"recId={child.RecordingId} vesselPid={child.VesselPersistentId} " +
                        $"name='{child.VesselName}' parentRecId={parentRecordingId ?? "(none)"}");
                }
                else
                {
                    // Controlled child: no TTL
                    ParsekLog.Info("BgRecorder",
                        $"Child recording created (controlled, no TTL): " +
                        $"recId={child.RecordingId} vesselPid={child.VesselPersistentId} " +
                        $"name='{child.VesselName}'");
                }
            }
        }

        /// <summary>
        /// Pure static method: creates a BranchPoint and child Recording objects for a
        /// background vessel split. Testable without Unity.
        /// The parent vessel may get a continuation recording (added by the caller
        /// if the parent vessel still exists — see bug #285);
        /// this method creates recordings for the NEW child vessels only.
        /// Children inherit Generation = parentGeneration + 1. The cascade-depth cap
        /// is NOT applied here — the caller (HandleBackgroundVesselSplit) decides
        /// whether to invoke this method based on ShouldSkipForCascadeCap.
        /// </summary>
        internal static (BranchPoint bp, List<Recording> childRecordings)
            BuildBackgroundSplitBranchData(
                string parentRecordingId, string treeId, double branchUT,
                BranchPointType branchType, uint parentVesselPid,
                List<(uint pid, string name, bool hasController)> newVesselInfos,
                int parentGeneration = 0)
        {
            string bpId = System.Guid.NewGuid().ToString("N");

            var bp = new BranchPoint
            {
                Id = bpId,
                UT = branchUT,
                Type = branchType,
                ParentRecordingIds = new List<string> { parentRecordingId },
                ChildRecordingIds = new List<string>(),
                SplitCause = "DECOUPLE"
            };

            var childRecordings = new List<Recording>(newVesselInfos.Count);
            for (int i = 0; i < newVesselInfos.Count; i++)
            {
                string childId = System.Guid.NewGuid().ToString("N");
                bp.ChildRecordingIds.Add(childId);

                var child = new Recording
                {
                    RecordingId = childId,
                    TreeId = treeId,
                    VesselPersistentId = newVesselInfos[i].pid,
                    VesselName = newVesselInfos[i].name,
                    ParentBranchPointId = bpId,
                    ExplicitStartUT = branchUT,
                    IsDebris = !newVesselInfos[i].hasController,
                    Generation = parentGeneration + 1
                };
                // PR 3b: stamp the debris parent-anchor contract on the new
                // child Recording adjacent to the IsDebris assignment. String
                // overload because this static factory is invoked with the
                // parent recording id.
                //
                // PR 3b review follow-up note (intentional double-application):
                // RegisterChildRecordingsFromSplit on the live path re-stamps
                // the contract a moment later, AFTER `parentContRec` is created,
                // using the continuation's recording id so the debris anchors to
                // the still-open continuation rather than the closed pre-split
                // parent. Stamping here too is harmless (idempotent) and serves
                // two purposes: (1) it preserves the propagation invariant for
                // test fixtures that call this static factory without going
                // through RegisterChildRecordingsFromSplit; (2) it keeps the
                // contract local to "primary creation sites" per plan §"`IsDebris`
                // propagation surface" — a future caller that bypasses
                // RegisterChildRecordingsFromSplit still gets a contract-stamped
                // debris recording (pointing at the original parent id, which
                // is the right answer when no continuation exists).
                Recording.ApplyParentAnchorContract(child, parentRecordingId);
                childRecordings.Add(child);
            }

            return (bp, childRecordings);
        }

        /// <summary>
        /// Checks debris TTL expiry for all tracked debris vessels.
        /// Called once per frame from Update(). When a debris vessel's TTL has expired,
        /// its recording is finalized and tracking state is cleaned up.
        /// Also checks for destroyed/out-of-bubble vessels.
        /// </summary>
        internal void CheckDebrisTTL(double currentUT)
        {
            if (debrisTTLExpiry.Count == 0) return;

            // Collect expired entries (can't modify dict during iteration)
            List<(uint vesselPid, BackgroundRecordingEndReason reason)> expired = null;

            foreach (var kvp in debrisTTLExpiry)
            {
                uint vesselPid = kvp.Key;
                double expiry = kvp.Value;

                if (double.IsNaN(expiry)) continue; // no TTL

                // Check if vessel is destroyed or out of bubble
                Vessel v = FlightRecorder.FindVesselByPid(vesselPid);
                if (v == null)
                {
                    // Vessel gone (destroyed or despawned)
                    if (expired == null)
                        expired = new List<(uint vesselPid, BackgroundRecordingEndReason reason)>();
                    expired.Add((vesselPid, BackgroundRecordingEndReason.VesselDestroyedOrDespawned));

                    ParsekLog.Info("BgRecorder",
                        $"Debris TTL: vessel destroyed/despawned, ending recording: " +
                        $"vesselPid={vesselPid}");
                    continue;
                }

                // Check if TTL expired
                if (currentUT >= expiry)
                {
                    if (expired == null)
                        expired = new List<(uint vesselPid, BackgroundRecordingEndReason reason)>();
                    expired.Add((vesselPid, BackgroundRecordingEndReason.TtlExpired));

                    ParsekLog.Info("BgRecorder",
                        $"Debris TTL expired, ending recording: " +
                        $"vesselPid={vesselPid} expiry={expiry:F1} currentUT={currentUT:F1}");
                    continue;
                }

                // Check if vessel went out of physics bubble (packed + on rails)
                if (!v.loaded)
                {
                    if (expired == null)
                        expired = new List<(uint vesselPid, BackgroundRecordingEndReason reason)>();
                    expired.Add((vesselPid, BackgroundRecordingEndReason.LeftPhysicsBubble));

                    ParsekLog.Info("BgRecorder",
                        $"Debris TTL: vessel left physics bubble, ending recording: " +
                        $"vesselPid={vesselPid}");
                    continue;
                }

                // PR 3b §"Parent on-rails hook" (Decision §10): for debris
                // carrying the parent-anchor contract, end the debris recording when
                // the parent's recorded data is gone or the parent vessel is no
                // longer live. Three end-conditions, one for each failure mode.
                // Legacy v11 debris (no ParentAnchorRecordingId) keeps original lifetime.
                if (tree == null
                    || tree.BackgroundMap == null
                    || !tree.BackgroundMap.TryGetValue(vesselPid, out string childRecId))
                {
                    continue;
                }
                Recording childRec;
                if (tree.Recordings == null
                    || !tree.Recordings.TryGetValue(childRecId, out childRec))
                {
                    continue;
                }
                if (string.IsNullOrEmpty(childRec.ParentAnchorRecordingId))
                    continue; // legacy v11 — keep original behavior

                Recording parentRec;
                if (!tree.Recordings.TryGetValue(childRec.ParentAnchorRecordingId, out parentRec))
                {
                    if (expired == null)
                        expired = new List<(uint vesselPid, BackgroundRecordingEndReason reason)>();
                    expired.Add((vesselPid, BackgroundRecordingEndReason.ParentRecordingMissing));
                    ParsekLog.Info("BgRecorder",
                        $"Debris TTL: parent recording missing from tree, ending: " +
                        $"parentRecId={childRec.ParentAnchorRecordingId} childPid={vesselPid}");
                    continue;
                }

                // Parent recording closed/superseded while the vessel may still be
                // loaded (the §"Bug evidence" closed-coverage-hole case at UT
                // 1213.398/1228.435): end the debris too — its Relative samples
                // beyond the parent's recorded UT range cannot resolve at playback.
                //
                // PR 3b review follow-up: predicate extracted to
                // DebrisParentStateGate.IsParentRecordingClosedOrSuperseded so xUnit
                // can validate the truth table. Do NOT regress to
                // parentRec.ExplicitEndUT < currentUT — active background recordings
                // update ExplicitEndUT only at sample boundaries, so that signal
                // lags by the sample interval and would end every parent-anchored debris on
                // the next TTL tick.
                if (DebrisParentStateGate.IsParentRecordingClosedOrSuperseded(parentRec, tree))
                {
                    if (expired == null)
                        expired = new List<(uint vesselPid, BackgroundRecordingEndReason reason)>();
                    expired.Add((vesselPid, BackgroundRecordingEndReason.ParentRecordingClosedOrSuperseded));
                    bool parentClosedAtSplit = !string.IsNullOrEmpty(parentRec.ChildBranchPointId);
                    ParsekLog.Info("BgRecorder",
                        $"Debris TTL: parent recording closed/superseded, ending recording: " +
                        $"parentRecId={childRec.ParentAnchorRecordingId} " +
                        $"parentClosedAtSplit={parentClosedAtSplit} " +
                        $"currentUT={currentUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                        $"childPid={vesselPid}");
                    continue;
                }

                Vessel parentVessel = FlightRecorder.FindVesselByPid(parentRec.VesselPersistentId);
                if (parentVessel == null || parentVessel.packed || !parentVessel.loaded)
                {
                    if (expired == null)
                        expired = new List<(uint vesselPid, BackgroundRecordingEndReason reason)>();
                    expired.Add((vesselPid, BackgroundRecordingEndReason.ParentOnRailsOrDestroyed));
                    ParsekLog.Info("BgRecorder",
                        $"Debris TTL: parent on-rails or destroyed, ending recording: " +
                        $"parentRecId={childRec.ParentAnchorRecordingId} " +
                        $"parentPid={parentRec.VesselPersistentId} " +
                        $"parentLoaded={parentVessel?.loaded ?? false} " +
                        $"parentPacked={parentVessel?.packed ?? false} " +
                        $"childPid={vesselPid}");
                    continue;
                }
            }

            if (expired == null) return;

            for (int i = 0; i < expired.Count; i++)
            {
                EndDebrisRecording(expired[i].vesselPid, currentUT, expired[i].reason);
            }
        }

        /// <summary>
        /// Sets the debris TTL expiry for an externally-created debris recording.
        /// Called by ParsekFlight.ProcessBreakupEvent when adding debris child
        /// recordings to the active tree.
        /// </summary>
        public void SetDebrisExpiry(uint vesselPid, double expiryUT)
        {
            debrisTTLExpiry[vesselPid] = expiryUT;
            ParsekLog.Info("BgRecorder",
                "Debris expiry set: pid=" + vesselPid +
                ", expiryUT=" + expiryUT.ToString("F1", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Ends a debris recording: finalizes the recording with a terminal state,
        /// cleans up tracking state, removes from BackgroundMap.
        /// </summary>
        private void EndDebrisRecording(
            uint vesselPid,
            double endUT,
            BackgroundRecordingEndReason endReason)
        {
            debrisTTLExpiry.Remove(vesselPid);

            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(vesselPid, out recordingId)) return;

            Recording rec = null;
            tree.Recordings.TryGetValue(recordingId, out rec);

            Vessel v = FlightRecorder.FindVesselByPid(vesselPid);
            if (v != null)
            {
                RefreshFinalizationCacheForVessel(
                    v,
                    recordingId,
                    v.loaded && !v.packed
                        ? FinalizationCacheOwner.BackgroundLoaded
                        : FinalizationCacheOwner.BackgroundOnRails,
                    "background_debris_end",
                    force: true);
            }
            else
            {
                RecordingFinalizationCache existingCache;
                BackgroundOnRailsState railsState;
                if ((!finalizationCaches.TryGetValue(vesselPid, out existingCache)
                        || !IsPotentiallyApplicableFinalizationCache(existingCache))
                    && onRailsStates.TryGetValue(vesselPid, out railsState))
                {
                    RefreshOnRailsFinalizationCache(
                        railsState,
                        endUT,
                        "background_debris_end_missing",
                        force: true);
                }
            }

            if (rec != null)
            {
                rec.ExplicitEndUT = endUT;
            }

            RecordingFinalizationCache cacheForApply = null;
            finalizationCaches.TryGetValue(vesselPid, out cacheForApply);

            // Clean up tracking state — also flushes any accumulated TrackSections
            // to rec via OnVesselRemovedFromBackground → FlushTrackSectionsToRecording.
            OnVesselRemovedFromBackground(vesselPid, endUT);
            if (rec != null)
            {
                DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                    rec,
                    $"EndDebrisRecording pid={vesselPid}");
            }

            RecordingFinalizationCacheApplyResult cacheResult;
            bool cacheApplied = rec != null
                && TryApplyFinalizationCacheForBackgroundEnd(
                    rec,
                    cacheForApply,
                    vesselPid,
                    endUT,
                    "EndDebrisRecording",
                    allowStale: true,
                    requireDestroyedTerminal: false,
                    confirmedDestroyed: IsConfirmedDestroyedBackgroundEnd(endReason),
                    out cacheResult);

            if (rec != null && !cacheApplied && !rec.TerminalStateValue.HasValue)
            {
                if (v == null)
                {
                    rec.TerminalStateValue = TerminalState.Destroyed;
                }
                else
                {
                    rec.TerminalStateValue = RecordingTree.DetermineTerminalState((int)v.situation, v);
                    ParsekFlight.CaptureTerminalOrbit(rec, v);
                }
            }

            tree.BackgroundMap.Remove(vesselPid);

            // Bug #280 follow-up (PR #177): persist the flushed data + snapshot to
            // disk IMMEDIATELY at finalization. The #280 fix (PR #167) wired
            // PersistFinalizedRecording into OnBackgroundVesselWillDestroy and
            // Shutdown but left EndDebrisRecording — the CheckDebrisTTL termination
            // site for both the v == null "destroyed/despawned" branch and the 60s
            // TTL expiry branch — uncovered. PR #176's #278 fix removed the
            // user-visible symptom by stopping the blanket-Destroyed stamp in
            // FinalizePendingLimboTreeForRevert, but the underlying #280 wiring
            // gap remains and would re-surface as data loss the moment a future
            // change reintroduces a code path that depends on the TTL/destroyed
            // sub-path persisting at finalization. Mirroring the call here closes
            // the gap so the TTL path matches the destroy/shutdown paths.
            if (rec != null)
            {
                PersistFinalizedRecording(rec, $"EndDebrisRecording pid={vesselPid}");
            }

            ParsekLog.Info("BgRecorder",
                $"Debris recording ended: vesselPid={vesselPid} recId={recordingId} " +
                $"terminal={rec?.TerminalStateValue?.ToString() ?? "null"}");
        }

        /// <summary>
        /// Pure static method: determines whether a debris vessel's TTL has expired,
        /// or whether it should stop recording for another reason.
        /// Returns the reason string if should stop, null if should continue.
        /// </summary>
        internal static string ShouldStopDebrisRecording(
            double currentUT, double ttlExpiry, bool vesselExists, bool vesselLoaded)
        {
            if (!vesselExists)
                return "destroyed";

            if (!double.IsNaN(ttlExpiry) && currentUT >= ttlExpiry)
                return "ttl_expired";

            if (!vesselLoaded)
                return "out_of_bubble";

            return null;
        }

        /// <summary>
        /// Phase 7 (review pass 3): pure gate predicate for the background
        /// surface-clearance computation. Mirrors the foreground gate in
        /// <see cref="FlightRecorder.ShouldEmitSurfaceClearance"/> so xUnit can
        /// pin the four-condition AND without needing a live <c>Vessel</c> /
        /// <c>FlightGlobals</c> runtime. The Unity-only
        /// <c>TerrainAltitude</c> call is isolated in
        /// <see cref="ApplySurfaceClearanceToPoint"/>.
        ///
        /// <para>All four conditions must hold for the BG sampler to
        /// populate <c>recordedGroundClearance</c>: a section must be
        /// active (no orphaned samples), the section must be
        /// <see cref="ReferenceFrame.Absolute"/> (RELATIVE sections store
        /// metres in lat/lon/alt — terrain math would be nonsense), the
        /// environment must be a surface-clearance environment, and the body
        /// must have a PQS controller (gas giants without surface mesh return
        /// zero from <c>TerrainAltitude</c>, which would produce a wrong-sign
        /// clearance).</para>
        /// </summary>
        internal static bool ShouldEmitBackgroundSurfaceClearance(
            bool trackSectionActive,
            ReferenceFrame frame,
            SegmentEnvironment env,
            bool hasPqsController)
        {
            return FlightRecorder.ShouldEmitSurfaceClearance(
                trackSectionActive, frame, env, hasPqsController);
        }

        /// <summary>
        /// Pure gate predicate for the body-fixed-shadow clearance path.
        /// Mirrors
        /// <see cref="ShouldEmitBackgroundSurfaceClearance"/> but forces
        /// <see cref="ReferenceFrame.Absolute"/> -- the shadow carries
        /// genuine body-fixed lat/lon/alt regardless of the active
        /// section's frame. The remaining three conditions
        /// (<c>trackSectionActive</c>, surface env, PQS) still apply.
        /// </summary>
        internal static bool ShouldEmitBackgroundBodyFixedShadowClearance(
            bool trackSectionActive,
            SegmentEnvironment env,
            bool hasPqsController)
        {
            return FlightRecorder.ShouldEmitSurfaceClearance(
                trackSectionActive,
                ReferenceFrame.Absolute,
                env,
                hasPqsController);
        }

        /// <summary>
        /// Apply v9 surface clearance to the body-fixed shadow
        /// copy that gets stored in <c>TrackSection.bodyFixedFrames</c>
        /// alongside a RELATIVE section. The in-frames <c>point</c> stays
        /// NaN-clearance because anchor-local metre offsets in
        /// <c>lat/lon/alt</c> are not body-fixed coordinates -- but the shadow
        /// carries genuine body-fixed lat/lon/alt and the body-fixed
        /// primary playback path applies terrain correction via
        /// <c>recordedGroundClearance</c>. Without this helper, parent-
        /// anchored surface debris recorded inside a parent-relative section
        /// would replay at raw recorded altitude and bury or pop on terrain
        /// differences.
        ///
        /// <para>Per-section stats (<c>surfaceMobileMin/MaxClearanceThisSection</c>,
        /// etc.) are intentionally NOT updated here: those track the
        /// section's own primary <c>frames</c> samples, not the parallel
        /// body-fixed shadow store.</para>
        /// </summary>
        private static void ApplySurfaceClearanceToBodyFixedShadow(
            BackgroundVesselState state,
            Vessel bgVessel,
            ref TrajectoryPoint shadow)
        {
            bool hasPqs = bgVessel != null && bgVessel.mainBody != null
                && bgVessel.mainBody.pqsController != null;
            if (!ShouldEmitBackgroundBodyFixedShadowClearance(
                    state != null && state.trackSectionActive,
                    state != null && state.trackSectionActive
                        ? state.currentTrackSection.environment
                        : SegmentEnvironment.SurfaceMobile,
                    hasPqs))
            {
                return;
            }
            double terrainHeight = bgVessel.mainBody.TerrainAltitude(
                shadow.latitude, shadow.longitude, true);
            shadow.recordedGroundClearance = shadow.altitude - terrainHeight;
        }

        private static void ApplySurfaceClearanceToPoint(
            BackgroundVesselState state,
            Vessel bgVessel,
            ref TrajectoryPoint point)
        {
            bool hasPqs = bgVessel != null && bgVessel.mainBody != null && bgVessel.mainBody.pqsController != null;
            if (!ShouldEmitBackgroundSurfaceClearance(
                    state != null && state.trackSectionActive,
                    state != null && state.trackSectionActive
                        ? state.currentTrackSection.referenceFrame
                        : ReferenceFrame.Absolute,
                    state != null && state.trackSectionActive
                        ? state.currentTrackSection.environment
                        : SegmentEnvironment.SurfaceMobile,
                    hasPqs))
            {
                return;
            }

            double terrainHeight = bgVessel.mainBody.TerrainAltitude(point.latitude, point.longitude, true);
            point.recordedGroundClearance = point.altitude - terrainHeight;
            state.surfaceMobileSamplesThisSection++;
            if (double.IsNaN(state.surfaceMobileMinClearanceThisSection)
                || point.recordedGroundClearance < state.surfaceMobileMinClearanceThisSection)
                state.surfaceMobileMinClearanceThisSection = point.recordedGroundClearance;
            if (double.IsNaN(state.surfaceMobileMaxClearanceThisSection)
                || point.recordedGroundClearance > state.surfaceMobileMaxClearanceThisSection)
                state.surfaceMobileMaxClearanceThisSection = point.recordedGroundClearance;
            state.surfaceMobileClearanceSumThisSection += point.recordedGroundClearance;
        }

        /// <summary>
        /// Pure decision: should a background vessel split be skipped because
        /// the parent recording's generation already meets the cascade-depth cap?
        /// True means HandleBackgroundVesselSplit returns without creating any
        /// branch, child recordings, or parent continuation; the parent's
        /// existing recording continues sampling unchanged.
        /// </summary>
        internal static bool ShouldSkipForCascadeCap(int parentGeneration)
            => parentGeneration >= MaxRecordingGeneration;

        #endregion

        private void WarnBackgroundVesselStateDrift(
            uint vesselPid,
            string recordingId,
            string reason,
            Vessel vessel,
            bool hasLoadedState,
            bool hasOnRailsState,
            bool hasRecording)
        {
            bool vesselLoaded = vessel != null && vessel.loaded;
            bool vesselPacked = vessel != null && vessel.packed;
            ParsekLog.WarnRateLimited("BgRecorder",
                $"bg-state-drift-{vesselPid}-{reason}",
                FormatBackgroundVesselStateDrift(
                    vesselPid,
                    recordingId,
                    reason,
                    vesselLoaded,
                    vesselPacked,
                    hasLoadedState,
                    hasOnRailsState,
                    hasRecording),
                10.0);
        }

        private void WarnIfBackgroundStateDrift(double currentUT, string reason)
        {
            if (tree == null || tree.BackgroundMap == null)
                return;

            if (!ShouldRunBackgroundStateDriftCheck(
                    lastBackgroundStateDriftCheckUT,
                    currentUT,
                    out lastBackgroundStateDriftCheckUT))
                return;

            var summary = BuildBackgroundStateDriftSummary();
            if (!summary.HasDrift)
                return;

            ParsekLog.WarnRateLimited("BgRecorder",
                "background-map-state-drift",
                FormatBackgroundStateDriftSummary(summary, reason),
                10.0);
        }

        internal void WarnIfBackgroundStateDriftForTesting(double currentUT, string reason)
        {
            WarnIfBackgroundStateDrift(currentUT, reason);
        }

        internal double LastBackgroundStateDriftCheckUTForTesting =>
            lastBackgroundStateDriftCheckUT;

        internal static bool ShouldRunBackgroundStateDriftCheck(
            double lastCheckUT,
            double currentUT,
            out double nextLastCheckUT)
        {
            nextLastCheckUT = lastCheckUT;
            if (lastCheckUT != double.MinValue)
            {
                if (currentUT < lastCheckUT)
                {
                    nextLastCheckUT = double.MinValue;
                }
                else if (currentUT - lastCheckUT < BackgroundStateDriftCheckInterval)
                {
                    return false;
                }
            }

            nextLastCheckUT = currentUT;
            return true;
        }

        private BackgroundStateDriftSummary BuildBackgroundStateDriftSummary()
        {
            var summary = new BackgroundStateDriftSummary();
            if (tree == null || tree.BackgroundMap == null)
                return summary;

            summary.BackgroundMapCount = tree.BackgroundMap.Count;
            foreach (var kvp in tree.BackgroundMap)
            {
                uint pid = kvp.Key;
                string recordingId = kvp.Value;

                bool hasRecording = tree.Recordings != null
                    && !string.IsNullOrEmpty(recordingId)
                    && tree.Recordings.ContainsKey(recordingId);
                if (!hasRecording)
                    summary.MissingRecording++;

                bool hasLoadedState = loadedStates.ContainsKey(pid);
                bool hasOnRailsState = onRailsStates.ContainsKey(pid);
                if (!hasLoadedState && !hasOnRailsState)
                    summary.MissingTrackingState++;

                Vessel vessel = vesselFinderOverride != null
                    ? vesselFinderOverride(pid)
                    : FlightRecorder.FindVesselByPid(pid);
                if (vessel == null)
                    continue;

                bool loadedPhysics = vessel.loaded && !vessel.packed;
                if (loadedPhysics && !hasLoadedState)
                    summary.LoadedWithoutLoadedState++;
                if (!loadedPhysics && !hasOnRailsState)
                    summary.PackedWithoutOnRailsState++;
                if (!loadedPhysics && hasLoadedState)
                    summary.PackedButLoadedState++;
                if (loadedPhysics && hasOnRailsState)
                    summary.LoadedButOnRailsState++;
            }

            return summary;
        }

        #region Public API

        /// <summary>
        /// Called from ParsekFlight.Update() once per frame.
        /// Updates ExplicitEndUT for on-rails background vessels.
        /// </summary>
        public void UpdateOnRails(double currentUT)
        {
            if (tree == null) return;
            WarnIfBackgroundStateDrift(currentUT, "UpdateOnRails");

            // Dictionary is not modified during this loop, safe to iterate directly
            foreach (var kvp in onRailsStates)
            {
                uint pid = kvp.Key;
                var state = kvp.Value;

                // Defense-in-depth: a destroyed recording must never have its
                // ExplicitEndUT or finalization cache overwritten by a periodic
                // background tick. The primary fix retires destroyed records from
                // onRailsStates at OnBackgroundVesselGoOnRails time, so this guard
                // is a backstop for any code path that injects a destroyed record
                // into onRailsStates without going through that seam.
                Recording treeRec;
                if (tree.Recordings.TryGetValue(state.recordingId, out treeRec)
                    && treeRec.VesselDestroyed)
                {
                    continue;
                }

                RefreshOnRailsFinalizationCache(state, currentUT, "background_on_rails_periodic", force: false);

                // Update ExplicitEndUT periodically
                if (currentUT - state.lastExplicitEndUpdate >= ExplicitEndUpdateInterval)
                {
                    if (treeRec != null)
                    {
                        treeRec.ExplicitEndUT = currentUT;
                        state.lastExplicitEndUpdate = currentUT;
                        ParsekLog.VerboseRateLimited("BgRecorder", $"onRailsEndUT.{pid}",
                            $"On-rails ExplicitEndUT updated: pid={pid} UT={currentUT:F1}", 30.0);
                    }
                }
            }
        }

        /// <summary>
        /// Called from PhysicsFramePatch.Postfix for each non-active loaded vessel.
        /// Handles loaded/physics mode recording for background vessels.
        /// </summary>
        public void OnBackgroundPhysicsFrame(Vessel bgVessel)
        {
            if (tree == null || bgVessel == null) return;

            uint pid = bgVessel.persistentId;

            // Check if this vessel is in the tree's BackgroundMap
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(pid, out recordingId)) return;

            bool hasLoadedState = loadedStates.ContainsKey(pid);
            bool hasOnRailsState = onRailsStates.ContainsKey(pid);
            bool hasRecording = tree.Recordings != null
                && !string.IsNullOrEmpty(recordingId)
                && tree.Recordings.ContainsKey(recordingId);
            if (!hasRecording)
            {
                WarnBackgroundVesselStateDrift(
                    pid,
                    recordingId,
                    "missing-recording",
                    bgVessel,
                    hasLoadedState,
                    hasOnRailsState,
                    hasRecording);
                return;
            }

            // Defense-in-depth: a destroyed recording must not be sampled by per-frame
            // BG ticks. The primary fix retires destroyed records from BackgroundMap
            // at OnBackgroundVesselGoOnRails time, so this guard catches the unusual
            // case where a recording was marked Destroyed elsewhere without retiring
            // its BG entry.
            if (tree.Recordings[recordingId].VesselDestroyed)
                return;

            // Only process loaded/physics vessels (not packed).
            // Load-bearing for the eccentric-orbit invariant: this early-return is the
            // sole reason on-rails BG vessels do not run `EnvironmentHysteresis.Update`
            // and therefore do not emit Atmospheric<->ExoBallistic TrackSection toggles
            // for periapsis grazes. Removing it would cause `RecordingOptimizer` to
            // accumulate one split per orbit on long-coasting vessels — see
            // `docs/dev/research/extending-rewind-to-stable-leaves.md` §S16.
            if (bgVessel.packed)
            {
                if (!hasOnRailsState)
                {
                    WarnBackgroundVesselStateDrift(
                        pid,
                        recordingId,
                        "packed-without-on-rails-state",
                        bgVessel,
                        hasLoadedState,
                        hasOnRailsState,
                        hasRecording);
                }
                return;
            }

            // Look up the loaded state (created by OnBackgroundVesselGoOffRails)
            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(pid, out state))
            {
                WarnBackgroundVesselStateDrift(
                    pid,
                    recordingId,
                    "loaded-without-loaded-state",
                    bgVessel,
                    hasLoadedState,
                    hasOnRailsState,
                    hasRecording);
                return;
            }

            // Get the tree recording
            Recording treeRec;
            if (!tree.Recordings.TryGetValue(recordingId, out treeRec)) return;

            double ut = Planetarium.GetUniversalTime();

            // Poll part events (always, regardless of sampling rate)
            PollPartEvents(bgVessel, state, treeRec, ut);
            RefreshFinalizationCacheForVessel(bgVessel, recordingId, FinalizationCacheOwner.BackgroundLoaded,
                "background_periodic", force: false);

            // Check for environment transitions (always, regardless of sampling rate)
            HandleBackgroundEnvironmentTransition(state, bgVessel, pid, ut);

            ProximitySamplingTier debrisTier = UpdateDebrisProximityState(state, treeRec, bgVessel);

            UpdateBackgroundAnchorDetection(state, bgVessel, treeRec, ut);

            // Compute distance to focused vessel and determine proximity-based sample interval
            double distance = ComputeDistanceToFocusedVessel(bgVessel);
            double rawProximityInterval = ProximityRateSelector.GetSampleInterval(distance);
            double proximityInterval = ResolveDebrisAwareSampleInterval(rawProximityInterval, treeRec);
            bool highFidelityActive = IsBackgroundHighFidelitySamplingActive(state, ut, distance);
            // Log when sample rate changes for this vessel
            if (proximityInterval != state.currentSampleInterval)
            {
                float newHz = (proximityInterval > 0 && proximityInterval < ProximityRateSelector.OutOfRangeInterval)
                    ? (float)(1.0 / proximityInterval)
                    : 0f;
                string distStr = distance.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                string hzStr = newHz.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                bool capApplied = proximityInterval != rawProximityInterval;
                string capSuffix = capApplied
                    ? $" debris-cap=applied (rawTier={FormatInterval(rawProximityInterval)})"
                    : string.Empty;
                ParsekLog.Info("BgRecorder",
                    $"Sample rate changed: pid={pid} dist={distStr}m " +
                    $"interval={FormatInterval(proximityInterval)} ({hzStr} Hz){capSuffix}");
                state.currentSampleInterval = proximityInterval;
            }

            // Skip trajectory sampling if out of range
            if (ShouldSkipTrajectorySamplingForProximity(
                    proximityInterval,
                    highFidelityActive,
                    debrisTier))
            {
                return;
            }

            // Adaptive sampling (velocity-based). Normal background sampling uses the
            // proximity tier as its min floor; high-fidelity proximity uses the player's
            // configured foreground minimum so close split ghosts follow the same density
            // policy as the active recorder instead of a hard-coded physics-frame rate.
            Vector3 currentVelocity = (Vector3)(bgVessel.rb_velocityD + Krakensbane.GetFrameVelocity());
            Quaternion currentWorldRotation = TrajectoryMath.SanitizeQuaternion(bgVessel.transform.rotation);

            float maxSampleInterval = ParsekSettings.Current?.maxSampleInterval ?? ParsekSettings.GetMaxSampleInterval(SamplingDensity.Medium);
            float minSampleInterval = ParsekSettings.Current?.minSampleInterval ?? ParsekSettings.GetMinSampleInterval(SamplingDensity.Medium);
            float velocityDirThreshold = ParsekSettings.Current?.velocityDirThreshold ?? ParsekSettings.GetVelocityDirThreshold(SamplingDensity.Medium);
            float speedChangeThreshold = (ParsekSettings.Current?.speedChangeThreshold ?? ParsekSettings.GetSpeedChangeThreshold(SamplingDensity.Medium)) / 100f;
            float effectiveMinSampleInterval = debrisTier != ProximitySamplingTier.None
                ? FlightRecorder.ResolveEffectiveMinSampleInterval(
                    ProximitySamplingTier.None,
                    debrisTier,
                    highFidelityActive,
                    minSampleInterval,
                    maxSampleInterval)
                : highFidelityActive
                    ? FlightRecorder.ResolveEffectiveMinSampleInterval(true, minSampleInterval)
                    : (float)proximityInterval;
            float effectiveMaxSampleInterval = FlightRecorder.ResolveEffectiveMaxSampleInterval(
                ProximitySamplingTier.None,
                debrisTier,
                highFidelityActive,
                maxSampleInterval,
                minSampleInterval);
            if (debrisTier == ProximitySamplingTier.None)
            {
                effectiveMaxSampleInterval = ResolveDebrisAwareMaxSampleInterval(
                    effectiveMaxSampleInterval,
                    treeRec);
            }

            bool motionTriggered = TrajectoryMath.ShouldRecordPoint(currentVelocity, state.lastRecordedVelocity,
                ut, state.lastRecordedUT,
                effectiveMinSampleInterval, effectiveMaxSampleInterval,
                velocityDirThreshold, speedChangeThreshold);
            float attitudeMinSampleInterval = ResolveBackgroundAttitudeMinSampleInterval(
                highFidelityActive,
                effectiveMinSampleInterval,
                minSampleInterval);
            bool attitudeTriggered = FlightRecorder.ShouldRecordAttitudePoint(
                currentWorldRotation,
                state.lastWorldRotation,
                ut,
                state.lastRecordedUT,
                state.hasLastWorldRotation,
                attitudeMinSampleInterval,
                backgroundAttitudeSampleThresholdDegrees);

            if (!motionTriggered && !attitudeTriggered)
            {
                return;
            }

            bool preferRootPartSurfacePose = ShouldPreferRootPartSurfacePoseForBackgroundSample(treeRec);
            TrajectoryPoint point = CreateAbsoluteTrajectoryPointFromVessel(
                bgVessel,
                ut,
                currentVelocity,
                preferRootPartSurfacePose: preferRootPartSurfacePose);
            TryCanonicalizeBackgroundReFlyRecordingPoint(
                state.recordingId,
                ref point,
                "physics-sample");
            TrajectoryPoint absolutePoint = point;
            bool relativeApplied = ApplyBackgroundRelativeOffset(state, ref point, bgVessel, ut);
            LogFirstDebrisOrdinarySampleDiagnostics(
                state,
                treeRec,
                bgVessel,
                absolutePoint,
                point,
                relativeApplied,
                ut);

            // Phase 7 (design doc §13, §18 Phase 7): for surface sections
            // recorded by the loaded-background sampler, capture per-sample
            // ground clearance so playback applies continuous terrain
            // correction at render time. Mirrors `FlightRecorder.CommitRecordedPoint`
            // for parity (a rover that drops out of focus and continues
            // recording in background must keep emitting v9-shape clearance,
            // not silently fall through to NaN). Mutates `point` BEFORE the
            // flat list / section append so both stores see the same value.
            // Non-surface / RELATIVE-frame / missing-PQS keep NaN, the
            // sentinel for the legacy altitude path.
            ApplySurfaceClearanceToPoint(state, bgVessel, ref point);
            // When the active section is RELATIVE, also apply
            // surface clearance to the body-fixed shadow that gets persisted
            // in bodyFixedFrames. The shadow carries body-fixed lat/lon/alt
            // and the body-fixed-primary playback path needs
            // recordedGroundClearance for v9 terrain correction; without
            // this, parent-anchored surface debris replays at raw recorded
            // altitude.
            if (relativeApplied)
            {
                ApplySurfaceClearanceToBodyFixedShadow(state, bgVessel, ref absolutePoint);
            }

            treeRec.Points.Add(point);
            treeRec.MarkFilesDirty();
            state.lastRecordedUT = point.ut;
            state.lastRecordedVelocity = point.velocity;
            state.lastWorldRotation = currentWorldRotation;
            state.hasLastWorldRotation = true;

            // Dual-write: also add to current TrackSection's frames list. The
            // per-sample warp flag is appended in lockstep inside
            // AddFrameToActiveTrackSection so each large gap is classified by the
            // warp state at its bounding samples at section close.
            AddFrameToActiveTrackSection(
                state,
                point,
                relativeApplied ? (TrajectoryPoint?)absolutePoint : null);

            // Update ExplicitEndUT
            treeRec.ExplicitEndUT = ut;

            ParsekLog.VerboseRateLimited("BgRecorder", $"bgPhysics.{pid}",
                $"Background point sampled: pid={pid} pts={treeRec.Points.Count} " +
                $"alt={bgVessel.altitude:F0} dist={distance:F0}m interval={FormatInterval(proximityInterval)} " +
                $"highFidelity={highFidelityActive} reason={state.highFidelitySamplingReason ?? "(none)"}", 5.0);
        }

        /// <summary>
        /// Queues the focused parent's split-time pose for a debris child seed.
        /// </summary>
        internal void QueueDebrisSeedParentAnchorPoint(
            uint vesselPid,
            string parentRecordingId,
            TrajectoryPoint parentAnchorPoint)
        {
            if (vesselPid == 0u || string.IsNullOrWhiteSpace(parentRecordingId))
                return;

            pendingDebrisSeedParentAnchorPoints[vesselPid] = (parentRecordingId, parentAnchorPoint);
            ParsekLog.Verbose("BgRecorder",
                $"Queued debris seed parent anchor point: pid={vesselPid} " +
                $"parentRecId={parentRecordingId} " +
                $"anchorUT={parentAnchorPoint.ut.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"flags={parentAnchorPoint.flags}");
        }

        /// <summary>
        /// Called when a vessel is added to the background map (transition or branch creation).
        /// </summary>
        public void OnVesselBackgrounded(uint vesselPid, InheritedEngineState? inherited = null,
            SegmentEnvironment? initialEnvironmentOverride = null,
            TrajectoryPoint? initialTrajectoryPoint = null,
            RecordingFinalizationCache inheritedFinalizationCache = null)
        {
            if (tree == null) return;

            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(vesselPid, out recordingId)) return;

            // Destroyed-recording guard: do not initialize BG tracking state for an
            // already-destroyed recording — the recording is sealed and any BG
            // sampling/cache refresh from here would mutate its terminal state.
            if (IsBackgroundRecordingDestroyed(recordingId))
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"destroyed-skip.backgrounded.{vesselPid}",
                    $"OnVesselBackgrounded: skipping destroyed recording pid={vesselPid} rec={recordingId}",
                    60.0);
                return;
            }

            AdoptInheritedFinalizationCache(vesselPid, recordingId, inheritedFinalizationCache);

            if (initialEnvironmentOverride.HasValue)
            {
                pendingInitialEnvironmentOverrides[vesselPid] = initialEnvironmentOverride.Value;
                ParsekLog.Verbose("BgRecorder",
                    $"Queued initial environment override for pid={vesselPid}: {initialEnvironmentOverride.Value}");
            }

            if (initialTrajectoryPoint.HasValue)
            {
                pendingInitialTrajectoryPoints[vesselPid] = initialTrajectoryPoint.Value;
                ParsekLog.Verbose("BgRecorder",
                    $"Queued initial trajectory point for pid={vesselPid}: " +
                    $"ut={initialTrajectoryPoint.Value.ut.ToString("F2", CultureInfo.InvariantCulture)}");
            }

            // Remove any stale state from a prior initialization (e.g. constructor
            // created on-rails state before this method is called for the same vessel).
            // Close any open orbit segment first so data is not lost.
            BackgroundOnRailsState staleRails;
            if (onRailsStates.TryGetValue(vesselPid, out staleRails) && staleRails.hasOpenOrbitSegment)
            {
                double ut = Planetarium.GetUniversalTime();
                CloseOrbitSegment(staleRails, ut);
            }
            onRailsStates.Remove(vesselPid);
            loadedStates.Remove(vesselPid);

            // Look up the vessel
            Vessel v = FlightRecorder.FindVesselByPid(vesselPid);
            if (v == null)
            {
                // Vessel not found yet; create minimal on-rails state
                onRailsStates[vesselPid] = new BackgroundOnRailsState
                {
                    vesselPid = vesselPid,
                    recordingId = recordingId,
                    hasOpenOrbitSegment = false,
                    isLanded = false,
                    lastExplicitEndUpdate = -1
                };
                ParsekLog.Info("BgRecorder", $"Vessel backgrounded (not found): pid={vesselPid} recId={recordingId}");
                return;
            }

            if (v.loaded && !v.packed)
            {
                // Loaded/physics mode
                InitializeLoadedState(v, vesselPid, recordingId, inherited);
                RefreshFinalizationCacheForVessel(v, recordingId, FinalizationCacheOwner.BackgroundLoaded,
                    "backgrounded_loaded", force: true);
                ParsekLog.Info("BgRecorder", $"Vessel backgrounded (loaded/physics): pid={vesselPid} recId={recordingId}");
            }
            else
            {
                // On-rails mode
                InitializeOnRailsState(v, vesselPid, recordingId);
                RefreshFinalizationCacheForVessel(v, recordingId, FinalizationCacheOwner.BackgroundOnRails,
                    "backgrounded_on_rails", force: true);
                ParsekLog.Info("BgRecorder", $"Vessel backgrounded (on-rails): pid={vesselPid} recId={recordingId}");
            }
        }

        /// <summary>
        /// Called when a vessel is removed from the background map (promotion, terminal event).
        /// </summary>
        public void OnVesselRemovedFromBackground(uint vesselPid)
        {
            OnVesselRemovedFromBackground(vesselPid, null);
        }

        private void OnVesselRemovedFromBackground(uint vesselPid, double? closeUT)
        {
            // When supplied for a live vessel, closeUT must be from the same frame
            // as the pose sampled below so the boundary point stays coherent.
            double? ut = null;
            if (closeUT.HasValue && IsFiniteUT(closeUT.Value))
                ut = closeUT.Value;

            // Close any open orbit segment
            BackgroundOnRailsState railsState;
            if (onRailsStates.TryGetValue(vesselPid, out railsState))
            {
                if (railsState.hasOpenOrbitSegment)
                {
                    if (!ut.HasValue) ut = Planetarium.GetUniversalTime();
                    CloseOrbitSegment(railsState, ut.Value);
                }
                onRailsStates.Remove(vesselPid);
            }

            // Close TrackSection and flush if loaded
            BackgroundVesselState loadedState;
            if (loadedStates.TryGetValue(vesselPid, out loadedState))
            {
                if (!ut.HasValue) ut = Planetarium.GetUniversalTime();
                CloseBackgroundTrackSection(loadedState, ut.Value);

                Recording flushRec;
                if (tree.Recordings.TryGetValue(loadedState.recordingId, out flushRec))
                {
                    FlushTrackSectionsToRecording(loadedState, flushRec);

                    // Sample a final boundary point
                    Vessel v = FlightRecorder.FindVesselByPid(vesselPid);
                    if (v != null)
                    {
                        SampleBoundaryPoint(v, flushRec, ut.Value);
                    }

                    DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                        flushRec,
                        $"OnVesselRemovedFromBackground pid={vesselPid}");
                }
                loadedStates.Remove(vesselPid);
            }

            pendingInitialEnvironmentOverrides.Remove(vesselPid);
            pendingInitialTrajectoryPoints.Remove(vesselPid);
            pendingDebrisSeedParentAnchorPoints.Remove(vesselPid);
            finalizationCaches.Remove(vesselPid);
            ParsekLog.Info("BgRecorder", $"Vessel removed from background: pid={vesselPid}");
        }

        /// <summary>
        /// Called when a background vessel goes on rails.
        /// Transitions from loaded/physics mode to on-rails mode.
        ///
        /// <para>
        /// Identity-loss override: before any on-rails state is initialized or any
        /// cache refresh fires, check whether the recorded controllable identity has
        /// survived into the live remnant. KSP sets <c>vessel.situation = LANDED</c>
        /// purely from terrain proximity, so a destructive breakup that leaves a
        /// 1-part decoupler at non-trivial surface speed would otherwise be classified
        /// as <c>terminal=Landed</c> by the FinalizerCache short-circuit. When
        /// <see cref="IdentityLossClassifier.IsRecordedIdentityLost"/> fires, we mark
        /// the recording <c>Destroyed</c> through the centralized hygiene helper —
        /// the subsequent cache refresh then short-circuits via the
        /// already-classified-destroyed skip path, preserving the correct terminal.
        /// </para>
        /// </summary>
        public void OnBackgroundVesselGoOnRails(Vessel v)
        {
            if (v == null || tree == null) return;

            uint pid = v.persistentId;
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(pid, out recordingId) || recordingId == null) return;

            // Out-of-band destroyed-recording guard. The identity-loss override below
            // handles the normal path (active recording crashes destructively while BG),
            // but any other code that left an already-Destroyed recording in BackgroundMap
            // is short-circuited here so it cannot mutate the sealed terminal verdict.
            if (IsBackgroundRecordingDestroyed(recordingId))
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"destroyed-skip.go_on_rails.{pid}",
                    $"OnBackgroundVesselGoOnRails: skipping destroyed recording pid={pid} rec={recordingId}",
                    60.0);
                return;
            }

            double ut = Planetarium.GetUniversalTime();

            // If the vessel was in loaded/physics mode, transition to on-rails
            BackgroundVesselState loadedState;
            if (loadedStates.TryGetValue(pid, out loadedState))
            {
                SegmentEnvironment nextEnv = ClassifyCurrentBackgroundEnvironment(v, loadedState.cachedEngines);
                bool willHavePlayableOnRailsPayload = WillInitializeOnRailsWithPlayablePayload(v);
                Recording flushRec;
                if (tree.Recordings.TryGetValue(loadedState.recordingId, out flushRec))
                {
                    FlushLoadedStateForOnRailsTransition(
                        loadedState,
                        flushRec,
                        nextEnv,
                        willHavePlayableOnRailsPayload,
                        CreateTrajectoryPointFromVessel(v, ut),
                        ut);
                }

                loadedStates.Remove(pid);
                ParsekLog.Info("BgRecorder", $"Mode transition loaded→on-rails: pid={pid}");
            }

            // Identity-loss override runs BEFORE InitializeOnRailsState and BEFORE
            // the cache refresh. On a positive identity-loss verdict the recording
            // is sealed at MarkDestroyedAtTerminal-time AND retired from BG tracking
            // (removed from BackgroundMap + onRailsStates + finalizationCaches).
            // Retirement is load-bearing: without it, the destroyed recording's
            // ExplicitEndUT would be overwritten by UpdateOnRails (per-frame
            // periodic update) and FinalizeAllForCommit (per-BackgroundMap-entry
            // bulk update), moving the crash endpoint from the identity-loss UT
            // to the eventual commit/latest-remnant UT — which downstream
            // GhostPlaybackLogic uses to time the non-debris destroyed explosion.
            Recording onRailsRec;
            if (tree.Recordings.TryGetValue(recordingId, out onRailsRec)
                && !onRailsRec.VesselDestroyed
                && IdentityLossClassifier.IsRecordedIdentityLost(onRailsRec, v))
            {
                int recordedControllerCount = onRailsRec.Controllers?.Count ?? 0;
                int survivingPartCount = v.parts?.Count ?? 0;
                ParsekLog.Info("BgRecorder",
                    $"Recorded controllable identity lost on go-on-rails: pid={pid} " +
                    $"recordedControllers={recordedControllerCount} survivingParts={survivingPartCount} " +
                    $"sit={v.situation} rec={recordingId} — classifying as Destroyed");
                onRailsRec.MarkDestroyedAtTerminal(ut, "BgRecorder.OnBackgroundVesselGoOnRails");
                RetireDestroyedBackgroundEntry(pid, recordingId, ut);
                return;
            }

            // Initialize on-rails state
            InitializeOnRailsState(v, pid, recordingId);
            RefreshFinalizationCacheForVessel(v, recordingId, FinalizationCacheOwner.BackgroundOnRails,
                "background_go_on_rails", force: true);
        }

        /// <summary>
        /// Returns true when the BG-tracked recording for <paramref name="recordingId"/>
        /// is already marked Destroyed. Every BG entrypoint that takes a Vessel
        /// (go-on-rails / go-off-rails / SOI / will-destroy / part-die / part-joint-break /
        /// vessel-backgrounded) calls this immediately after the
        /// <see cref="RecordingTree.BackgroundMap"/> lookup to skip out-of-band
        /// destroyed records that didn't go through the identity-loss
        /// <see cref="RetireDestroyedBackgroundEntry"/> retirement seam. Without this
        /// guard, a destroyed record left in <see cref="RecordingTree.BackgroundMap"/>
        /// could still be sampled, get its <see cref="Recording.ExplicitEndUT"/>
        /// advanced, accept new <see cref="PartEvent"/>s, or have an open orbit
        /// segment closed at a later UT — all of which would shift the
        /// destruction-explosion timing downstream in <see cref="GhostPlaybackLogic"/>.
        /// </summary>
        private bool IsBackgroundRecordingDestroyed(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId) || tree == null || tree.Recordings == null)
                return false;
            Recording rec;
            return tree.Recordings.TryGetValue(recordingId, out rec)
                && rec != null
                && rec.VesselDestroyed;
        }

        /// <summary>
        /// Removes a destroyed recording from every BG-tracking data structure so
        /// later <see cref="UpdateOnRails"/>, <see cref="FinalizeAllForCommit"/>, and
        /// <see cref="OnBackgroundPhysicsFrame"/> iterations cannot touch its
        /// <see cref="Recording.ExplicitEndUT"/> or sample the inert remnant. Called
        /// after <see cref="Recording.MarkDestroyedAtTerminal"/> seals the verdict.
        ///
        /// <para>
        /// Drains the four primary BG-state dicts (<c>BackgroundMap</c>,
        /// <c>onRailsStates</c>, <c>loadedStates</c>, <c>finalizationCaches</c>) plus
        /// the per-parent split-detection dicts (<c>pendingBackgroundSplitChecks</c>,
        /// <c>preBreakVesselPidSnapshots</c>) — both populated by
        /// <see cref="OnBackgroundPartJointBreak"/> for any non-debris BG parent
        /// that suffers a joint break, so a parent that experiences a joint break
        /// followed by go-on-rails identity loss on the same/next frame would
        /// otherwise leave entries that <see cref="ProcessPendingSplitChecks"/>
        /// could use to create branch points and child recordings off the sealed
        /// parent. Debris-only dicts (<c>pendingDebrisSeedParentAnchorPoints</c>,
        /// <c>debrisTTLExpiry</c>) are not drained here because the identity-loss
        /// override only fires on non-debris recordings (<c>IsDebris == false</c>)
        /// and those dicts are populated only on the debris paths.
        /// </para>
        /// <para>
        /// Logged at Verbose to avoid doubling Info-level output: this is a
        /// downstream consequence of the Info-level
        /// <see cref="Recording.MarkDestroyedAtTerminal"/> entry that fires
        /// immediately before.
        /// </para>
        /// </summary>
        private void RetireDestroyedBackgroundEntry(uint pid, string recordingId, double terminalUT)
        {
            tree.BackgroundMap.Remove(pid);
            onRailsStates.Remove(pid);
            loadedStates.Remove(pid);
            finalizationCaches.Remove(pid);
            // Drain the deferred-split detection dicts keyed on this parent pid so
            // ProcessPendingSplitChecks → HandleBackgroundVesselSplit cannot fire
            // for a sealed parent next frame.
            pendingBackgroundSplitChecks.Remove(pid);
            preBreakVesselPidSnapshots.Remove(pid);
            ParsekLog.Verbose("BgRecorder",
                $"Retired BG tracking for destroyed remnant: pid={pid} rec={recordingId} " +
                $"terminalUT={terminalUT.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Called when a background vessel goes off rails.
        /// Transitions from on-rails mode to loaded/physics mode.
        /// </summary>
        public void OnBackgroundVesselGoOffRails(Vessel v)
        {
            if (v == null || tree == null) return;

            uint pid = v.persistentId;
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(pid, out recordingId) || recordingId == null) return;

            // Destroyed-recording guard: a destroyed remnant that later unpacks
            // (player switches focus to it) must not get a loaded-state
            // initialization or a boundary point sampled — that would append a
            // post-terminal point to the sealed recording and shift the destroyed
            // explosion timing in GhostPlaybackLogic.
            if (IsBackgroundRecordingDestroyed(recordingId))
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"destroyed-skip.go_off_rails.{pid}",
                    $"OnBackgroundVesselGoOffRails: skipping destroyed recording pid={pid} rec={recordingId}",
                    60.0);
                return;
            }

            double ut = Planetarium.GetUniversalTime();

            // Close any open orbit segment
            BackgroundOnRailsState railsState;
            if (onRailsStates.TryGetValue(pid, out railsState))
            {
                if (railsState.hasOpenOrbitSegment)
                {
                    CloseOrbitSegment(railsState, ut);
                }
                onRailsStates.Remove(pid);
                ParsekLog.Info("BgRecorder", $"Mode transition on-rails→loaded: pid={pid}");
            }

            // Initialize loaded/physics state if vessel is unpacked
            if (!v.packed)
            {
                InitializeLoadedState(v, pid, recordingId);
                RefreshFinalizationCacheForVessel(v, recordingId, FinalizationCacheOwner.BackgroundLoaded,
                    "background_go_off_rails", force: true);

                // Sample a boundary point
                Recording treeRec;
                if (tree.Recordings.TryGetValue(recordingId, out treeRec))
                {
                    SampleBoundaryPoint(v, treeRec, ut);
                }
            }
        }

        /// <summary>
        /// Called when a background vessel changes SOI.
        /// Closes the orbit segment in the old SOI and opens one in the new SOI.
        /// </summary>
        public void OnBackgroundVesselSOIChanged(Vessel v, CelestialBody fromBody)
        {
            if (v == null || tree == null) return;

            uint pid = v.persistentId;
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(pid, out recordingId) || recordingId == null) return;

            // Destroyed-recording guard: a destroyed remnant's SOI change is moot —
            // do not close / open orbit segments or refresh the finalization cache.
            if (IsBackgroundRecordingDestroyed(recordingId))
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"destroyed-skip.soi_change.{pid}",
                    $"OnBackgroundVesselSOIChanged: skipping destroyed recording pid={pid} rec={recordingId}",
                    60.0);
                return;
            }

            double ut = Planetarium.GetUniversalTime();

            BackgroundOnRailsState state;
            if (onRailsStates.TryGetValue(pid, out state))
            {
                // Close the old orbit segment
                if (state.hasOpenOrbitSegment)
                {
                    CloseOrbitSegment(state, ut);
                }

                // Open a new orbit segment with the new body's orbit
                if (v.orbit != null)
                {
                    state.currentOrbitSegment = CreateOrbitSegmentFromVessel(v, ut, "Unknown");
                    TryCanonicalizeBackgroundReFlyRecordingOrbitSegment(
                        recordingId,
                        v,
                        ut,
                        ref state.currentOrbitSegment,
                        "soi-change");
                    state.hasOpenOrbitSegment = true;
                }
                RefreshFinalizationCacheForVessel(v, recordingId, FinalizationCacheOwner.BackgroundOnRails,
                    "background_soi_change", force: true);

                ParsekLog.Info("BgRecorder", $"SOI change for background vessel: pid={pid} " +
                    $"from={fromBody?.name ?? "?"} to={v.mainBody?.name ?? "?"}");
            }
        }

        /// <summary>
        /// Called when a background vessel is about to be destroyed.
        /// Closes any open orbit segment, cleans up tracking state.
        /// </summary>
        public void OnBackgroundVesselWillDestroy(Vessel v)
        {
            if (v == null || tree == null) return;

            uint pid = v.persistentId;
            if (!tree.BackgroundMap.ContainsKey(pid)) return;
            double ut = Planetarium.GetUniversalTime();
            string recordingId;
            tree.BackgroundMap.TryGetValue(pid, out recordingId);

            // Destroyed-recording guard: if the recording is already sealed (e.g.
            // via the identity-loss override that didn't fully retire, or any
            // out-of-band path that called MarkDestroyedAtTerminal), skip the
            // finalization-cache refresh and reuse RetireDestroyedBackgroundEntry
            // for the dictionary drain so this branch is single-source-of-truth
            // with the identity-loss retirement seam (including the
            // pendingBackgroundSplitChecks / preBreakVesselPidSnapshots drain
            // that prevents ProcessPendingSplitChecks from dispatching on a sealed
            // parent). The pending-initial dicts that the live-vessel-destroy
            // fall-through normally drains are also cleared here for symmetry.
            if (IsBackgroundRecordingDestroyed(recordingId))
            {
                pendingInitialEnvironmentOverrides.Remove(pid);
                pendingInitialTrajectoryPoints.Remove(pid);
                RetireDestroyedBackgroundEntry(pid, recordingId, ut);
                ParsekLog.Info("BgRecorder",
                    $"OnBackgroundVesselWillDestroy: cleared BG state for already-destroyed recording " +
                    $"pid={pid} rec={recordingId}");
                return;
            }

            RefreshFinalizationCacheForVessel(v, recordingId, v.loaded && !v.packed
                ? FinalizationCacheOwner.BackgroundLoaded
                : FinalizationCacheOwner.BackgroundOnRails,
                "background_destroy", force: true);

            BackgroundOnRailsState railsState;
            if (onRailsStates.TryGetValue(pid, out railsState))
            {
                if (railsState.hasOpenOrbitSegment)
                {
                    CloseOrbitSegment(railsState, ut);
                }
                onRailsStates.Remove(pid);
            }

            BackgroundVesselState loadedState;
            if (loadedStates.TryGetValue(pid, out loadedState))
            {
                CloseBackgroundTrackSection(loadedState, ut);

                Recording flushRec;
                if (tree.Recordings.TryGetValue(loadedState.recordingId, out flushRec))
                {
                    FlushTrackSectionsToRecording(loadedState, flushRec);
                    // Bug #280: persist the flushed data to disk IMMEDIATELY at
                    // finalization time. Relying on OnSave to see FilesDirty and write
                    // the file has proven fragile — the 2026-04-09 playtest showed
                    // destroyed debris recordings consistently losing their 22-frame
                    // TrackSection data across the scene reload because either the
                    // dirty flag or the save path dropped them. Writing here bypasses
                    // OnSave entirely for the destroy path and guarantees the file
                    // exists on disk before any scene transition.
                    PersistFinalizedRecording(flushRec, $"OnBackgroundVesselWillDestroy pid={pid}");
                }
                loadedStates.Remove(pid);
            }

            // Do not apply the cache here. KSP can raise the destroy signal for
            // cases that are later classified as docking/false unloads; ParsekFlight's
            // DeferredDestructionCheck consumes the cache after true destruction is
            // confirmed and persists the sidecar again with terminal metadata.

            if (v.vesselType == VesselType.EVA)
                ParsekLog.Info("BgRecorder", $"Background EVA vessel ended: pid={pid}");
            else
                ParsekLog.Warn("BgRecorder", $"Background vessel destroyed: pid={pid}");

            pendingInitialEnvironmentOverrides.Remove(pid);
            pendingInitialTrajectoryPoints.Remove(pid);
            // Keep the finalization cache until DeferredDestructionCheck confirms
            // whether this was true destruction or a false unload signal.
        }

        internal void ForgetFinalizationCache(uint vesselPid)
        {
            finalizationCaches.Remove(vesselPid);
        }

        /// <summary>
        /// Writes a background recording's .prec sidecar to disk immediately after a
        /// finalization flush, bypassing OnSave's FilesDirty-driven path. Used by
        /// destroy/shutdown/split sites where the in-memory data must survive any
        /// scene reload that might happen before the next OnSave.
        ///
        /// Background: the 2026-04-09 playtest revealed ~16 debris recordings losing
        /// all their trajectory data on F9 quickload. BackgroundRecorder flushed the
        /// TrackSections to the Recording in memory and called <c>MarkFilesDirty</c>,
        /// but by OnSave time the files were never written — so on reload, the
        /// missing files produced 0-point recordings, and the subsequent CommitTree
        /// pass wrote those empty recordings over the in-memory data. Persisting at
        /// flush time closes the window between "data exists in memory" and "data
        /// exists on disk" (bug #280).
        /// DeferredDestructionCheck calls this directly for the same invariant:
        /// terminal sidecars must match in-memory state before later scene or merge
        /// transitions.
        /// </summary>
        internal static void PersistFinalizedRecording(Recording rec, string context)
        {
            if (rec == null) return;
            DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                rec,
                $"PersistFinalizedRecording {context}");
            if (RecordingStore.SaveRecordingFiles(rec, incrementEpoch: false))
            {
                ParsekLog.Verbose("BgRecorder",
                    $"PersistFinalizedRecording: wrote sidecar for " +
                    $"recId={rec.RecordingId} ({context})");
            }
            else
            {
                ParsekLog.Warn("BgRecorder",
                    $"PersistFinalizedRecording: failed to write sidecar for " +
                    $"recId={rec.RecordingId} ({context})");
            }
        }

        /// <summary>
        /// Called on scene change or tree teardown. Closes all open orbit segments
        /// and cleans up all per-vessel state.
        /// </summary>
        public void Shutdown()
        {
            Shutdown(Planetarium.GetUniversalTime());
        }

        /// <summary>
        /// Shutdown with an explicit UT, for tests that exercise the destroyed-record
        /// skip paths without requiring the Unity Planetarium singleton.
        /// </summary>
        internal void Shutdown(double ut)
        {
            UnsubscribePartEvents();

            // Close all open orbit segments (skip destroyed — closing an open segment
            // at shutdown UT would extend the sealed recording past its terminalUT).
            foreach (var kvp in onRailsStates)
            {
                var state = kvp.Value;
                if (!state.hasOpenOrbitSegment) continue;
                if (IsBackgroundRecordingDestroyed(state.recordingId))
                {
                    ParsekLog.VerboseRateLimited("BgRecorder",
                        $"destroyed-skip.shutdown_orbit.{state.vesselPid}",
                        $"Shutdown: skipping orbit-segment close for destroyed recording " +
                        $"pid={state.vesselPid} rec={state.recordingId}",
                        60.0);
                    continue;
                }
                CloseOrbitSegment(state, ut);
            }

            // Close and flush TrackSections for all loaded vessels (skip destroyed —
            // flushing a TrackSection at shutdown UT and persisting it would similarly
            // extend Recording.EndUT past the sealed identity-loss UT).
            foreach (var kvp in loadedStates)
            {
                var state = kvp.Value;
                if (IsBackgroundRecordingDestroyed(state.recordingId))
                {
                    ParsekLog.VerboseRateLimited("BgRecorder",
                        $"destroyed-skip.shutdown_loaded.{state.vesselPid}",
                        $"Shutdown: skipping loaded-state flush for destroyed recording " +
                        $"pid={state.vesselPid} rec={state.recordingId}",
                        60.0);
                    continue;
                }
                CloseBackgroundTrackSection(state, ut);

                Recording flushRec;
                if (tree.Recordings.TryGetValue(state.recordingId, out flushRec))
                {
                    FlushTrackSectionsToRecording(state, flushRec);
                    // Bug #280: persist flushed data immediately (see PersistFinalizedRecording docs).
                    PersistFinalizedRecording(flushRec, $"Shutdown pid={state.vesselPid}");
                }
            }

            onRailsStates.Clear();
            loadedStates.Clear();
            pendingInitialEnvironmentOverrides.Clear();
            pendingInitialTrajectoryPoints.Clear();
            finalizationCaches.Clear();

            ParsekLog.Info("BgRecorder", "Shutdown complete — all background states cleared");
        }

        /// <summary>
        /// Drops all background recording state without flushing or persisting
        /// trajectory data. Used when the caller is intentionally discarding the
        /// entire in-memory attempt, such as Discard Re-Fly loading the origin
        /// rewind point.
        /// </summary>
        public void DiscardWithoutPersist(string reason)
        {
            UnsubscribePartEvents();

            int onRailsCount = onRailsStates.Count;
            int loadedCount = loadedStates.Count;
            int finalizationCacheCount = finalizationCaches.Count;

            onRailsStates.Clear();
            loadedStates.Clear();
            pendingInitialEnvironmentOverrides.Clear();
            pendingInitialTrajectoryPoints.Clear();
            finalizationCaches.Clear();

            ParsekLog.Info("BgRecorder",
                $"DiscardWithoutPersist: cleared background states without flushing " +
                $"onRails={onRailsCount} loaded={loadedCount} caches={finalizationCacheCount} " +
                $"reason='{(string.IsNullOrEmpty(reason) ? "<unspecified>" : reason)}'");
        }

        /// <summary>
        /// Finalizes all background recordings for tree commit.
        /// Closes all open orbit segments, flushes any pending trajectory data,
        /// and sets ExplicitEndUT on all background recordings.
        /// Does NOT clear state — call Shutdown() after this if shutting down.
        /// </summary>
        public void FinalizeAllForCommit(double commitUT)
        {
            // Close all open orbit segments (skip destroyed recordings — their
            // recording is sealed, no open segments should exist, and any cache
            // refresh here would overwrite the terminal verdict).
            foreach (var kvp in onRailsStates)
            {
                var state = kvp.Value;
                if (!state.hasOpenOrbitSegment) continue;

                Recording finalizeOrbitTreeRec;
                if (tree.Recordings.TryGetValue(state.recordingId, out finalizeOrbitTreeRec)
                    && finalizeOrbitTreeRec.VesselDestroyed)
                {
                    continue;
                }

                RefreshOnRailsFinalizationCache(
                    state,
                    commitUT,
                    "background_commit_on_rails",
                    force: true);
                CloseOrbitSegment(state, commitUT);
            }

            // Close and flush TrackSections for all loaded vessels, emit terminal engine/RCS/robotic events (bug #108).
            // Skip destroyed recordings — flushing a new TrackSection at commitUT
            // would extend Recording.EndUT (which uses playable TrackSection bounds
            // when frames are present, see Recording.cs ~line 421) past the sealed
            // identity-loss UT, moving the destroyed-explosion timing in
            // GhostPlaybackLogic. The cache refresh + terminal-engine events would
            // similarly overwrite the sealed terminal payload.
            foreach (var kvp in loadedStates)
            {
                var state = kvp.Value;
                if (IsBackgroundRecordingDestroyed(state.recordingId))
                {
                    ParsekLog.VerboseRateLimited("BgRecorder",
                        $"destroyed-skip.finalize_loaded.{state.vesselPid}",
                        $"FinalizeAllForCommit: skipping destroyed loaded recording " +
                        $"pid={state.vesselPid} rec={state.recordingId}",
                        60.0);
                    continue;
                }
                Vessel v = FlightRecorder.FindVesselByPid(state.vesselPid);
                RecordingFinalizationCache previous = null;
                finalizationCaches.TryGetValue(state.vesselPid, out previous);
                if (v != null)
                {
                    RefreshFinalizationCacheForVessel(
                        v,
                        state.recordingId,
                        FinalizationCacheOwner.BackgroundLoaded,
                        "background_commit_loaded",
                        force: true);
                }
                else
                {
                    if (previous != null)
                    {
                        TryTouchSkippedFinalizationCache(
                            previous,
                            commitUT,
                            "background_commit_missing_vessel",
                            previous.LastObservedOrbitDigest,
                            requiresPeriodicRefresh: false);
                    }
                }

                double closeUT = ResolveBackgroundCommitTrackCloseUT(
                    state,
                    commitUT,
                    v,
                    previous);
                CloseBackgroundTrackSection(state, closeUT);

                Recording flushRec;
                if (tree.Recordings.TryGetValue(state.recordingId, out flushRec))
                {
                    FlushTrackSectionsToRecording(state, flushRec);

                    var terminalEvents = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                        state.activeEngineKeys, state.activeRcsKeys, state.activeRoboticKeys,
                        state.lastRoboticPosition, commitUT, "BgRecorder");
                    if (terminalEvents.Count > 0)
                    {
                        flushRec.PartEvents.AddRange(terminalEvents);
                        // FlushTrackSectionsToRecording only marks dirty when
                        // state.trackSections.Count > 0 — a background vessel
                        // finalized with no accumulated sections (e.g. one that
                        // just entered loaded state) would otherwise leave
                        // flushRec.FilesDirty false after this terminal emit.
                        flushRec.MarkFilesDirty();
                    }
                }
            }

            // Update ExplicitEndUT on all background recordings (skip destroyed —
            // their ExplicitEndUT was sealed at MarkDestroyedAtTerminal time and
            // moving it here would shift the non-debris explosion timing in
            // GhostPlaybackLogic from the identity-loss UT to commitUT).
            foreach (var kvp in tree.BackgroundMap)
            {
                Recording treeRec;
                if (tree.Recordings.TryGetValue(kvp.Value, out treeRec) && !treeRec.VesselDestroyed)
                {
                    treeRec.ExplicitEndUT = commitUT;
                    DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                        treeRec,
                        $"FinalizeAllForCommit pid={kvp.Key}");
                }
            }

            ParsekLog.Info("BgRecorder", $"FinalizeAllForCommit complete at UT={commitUT:F1} " +
                $"({onRailsStates.Count} on-rails, {loadedStates.Count} loaded)");
        }

        private static double ResolveBackgroundCommitTrackCloseUT(
            BackgroundVesselState state,
            double commitUT,
            Vessel vessel,
            RecordingFinalizationCache cache)
        {
            if (vessel != null || state == null || !IsPotentiallyApplicableFinalizationCache(cache))
                return commitUT;

            if (!IsFiniteUT(state.lastRecordedUT) || state.lastRecordedUT < 0.0)
                return commitUT;

            if (!IsFiniteUT(commitUT) || state.lastRecordedUT <= commitUT)
                return state.lastRecordedUT;

            return commitUT;
        }

        private static bool IsPotentiallyApplicableFinalizationCache(
            RecordingFinalizationCache cache)
        {
            return RecordingFinalizationCacheProducer.IsPotentiallyApplicableCache(cache);
        }

        private static bool IsFiniteUT(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool IsConfirmedDestroyedBackgroundEnd(BackgroundRecordingEndReason reason)
        {
            return reason == BackgroundRecordingEndReason.VesselDestroyedOrDespawned;
        }

        internal bool TryApplyFinalizationCacheForBackgroundEnd(
            Recording recording,
            uint vesselPid,
            double endUT,
            string consumerPath,
            bool allowStale,
            bool requireDestroyedTerminal,
            bool confirmedDestroyed,
            out RecordingFinalizationCacheApplyResult result)
        {
            result = default(RecordingFinalizationCacheApplyResult);
            if (recording == null)
                return false;

            RecordingFinalizationCache cache = GetFinalizationCacheForRecording(recording);
            if (cache == null && vesselPid != 0
                && finalizationCaches.TryGetValue(vesselPid, out RecordingFinalizationCache pidLookup))
            {
                cache = CopyAndAlignFinalizationCacheIdentity(pidLookup, recording);
            }

            return TryApplyFinalizationCacheForBackgroundEnd(
                recording,
                cache,
                vesselPid,
                endUT,
                consumerPath,
                allowStale,
                requireDestroyedTerminal,
                confirmedDestroyed,
                out result);
        }

        internal bool TryApplyFinalizationCacheForBackgroundEnd(
            Recording recording,
            RecordingFinalizationCache cache,
            uint vesselPid,
            double endUT,
            string consumerPath,
            bool allowStale,
            bool requireDestroyedTerminal,
            bool confirmedDestroyed,
            out RecordingFinalizationCacheApplyResult result)
        {
            result = default(RecordingFinalizationCacheApplyResult);
            if (recording == null)
                return false;

            if (cache == null)
            {
                ParsekLog.Verbose("FinalizerCache",
                    $"Background apply skipped: consumer={consumerPath ?? "(null)"} " +
                    $"rec={recording.DebugName} pid={vesselPid} reason=no-cache");
                return false;
            }

            RecordingFinalizationCache scopedCache =
                ScopeFinalizationCacheToBackgroundEnd(cache, endUT);
            ApplyFinalizationCacheIdentity(scopedCache, recording);

            if (requireDestroyedTerminal && scopedCache.TerminalState != TerminalState.Destroyed)
            {
                ParsekLog.Verbose("FinalizerCache",
                    $"Background apply skipped: consumer={consumerPath ?? "(null)"} " +
                    $"rec={recording.DebugName} pid={vesselPid} reason=non-destroyed-cache " +
                    $"terminal={scopedCache.TerminalState?.ToString() ?? "(null)"} " +
                    "using live destruction fallback");
                return false;
            }

            TryClampDestroyedBackgroundTerminalAfterAuthoredData(
                recording,
                scopedCache,
                vesselPid,
                endUT,
                consumerPath,
                confirmedDestroyed);

            var options = new RecordingFinalizationCacheApplyOptions
            {
                ConsumerPath = consumerPath,
                AllowStale = allowStale
            };

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                recording,
                scopedCache,
                options,
                out result);

            if (applied)
            {
                ParsekLog.Info("BgRecorder",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Finalization source=cache consumer={0} rec={1} terminal={2} " +
                        "terminalUT={3:F3} endUT={4:F3} appendedSegments={5}",
                        consumerPath ?? "(null)",
                        recording.DebugName,
                        result.TerminalState?.ToString() ?? "(null)",
                        result.TerminalUT,
                        endUT,
                        result.AppendedSegmentCount));
            }

            return applied;
        }

        private static bool TryClampDestroyedBackgroundTerminalAfterAuthoredData(
            Recording recording,
            RecordingFinalizationCache scopedCache,
            uint vesselPid,
            double endUT,
            string consumerPath,
            bool confirmedDestroyed)
        {
            double lastAuthoredUT = double.NaN;
            if (!confirmedDestroyed)
            {
                LogBackgroundTerminalClampSkipped(recording, scopedCache, vesselPid, endUT, consumerPath,
                    confirmedDestroyed, "not-confirmed-destroyed", lastAuthoredUT);
                return false;
            }

            if (recording == null || scopedCache == null)
            {
                return false;
            }

            if (scopedCache.TerminalState != TerminalState.Destroyed)
            {
                LogBackgroundTerminalClampSkipped(recording, scopedCache, vesselPid, endUT, consumerPath,
                    confirmedDestroyed, "non-destroyed-terminal", lastAuthoredUT);
                return false;
            }

            if (!IsFiniteUT(scopedCache.TerminalUT))
            {
                LogBackgroundTerminalClampSkipped(recording, scopedCache, vesselPid, endUT, consumerPath,
                    confirmedDestroyed, "invalid-terminal-ut", lastAuthoredUT);
                return false;
            }

            if (!RecordingFinalizationCacheApplier.TryGetLastAuthoredUT(recording, out lastAuthoredUT))
            {
                LogBackgroundTerminalClampSkipped(recording, scopedCache, vesselPid, endUT, consumerPath,
                    confirmedDestroyed, "no-authored-data", lastAuthoredUT);
                return false;
            }

            if (scopedCache.TerminalUT + RecordingFinalizationCacheApplier.UtEpsilon >= lastAuthoredUT)
            {
                LogBackgroundTerminalClampSkipped(recording, scopedCache, vesselPid, endUT, consumerPath,
                    confirmedDestroyed, "terminal-not-before-last-authored", lastAuthoredUT);
                return false;
            }

            if (IsFiniteUT(endUT) && lastAuthoredUT > endUT + RecordingFinalizationCacheApplier.UtEpsilon)
            {
                ParsekLog.Warn("FinalizerCache",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Background terminal clamp skipped: consumer={0} rec={1} pid={2} " +
                        "reason=LastAuthoredAfterEndUT oldTerminalUT={3:F3} lastAuthoredUT={4:F3} endUT={5:F3} " +
                        "cacheStatus={6} owner={7} confirmedDestroyed={8}",
                        consumerPath ?? "(null)",
                        recording.DebugName,
                        vesselPid,
                        scopedCache.TerminalUT,
                        lastAuthoredUT,
                        endUT,
                        scopedCache.Status,
                        scopedCache.Owner,
                        confirmedDestroyed));
                return false;
            }

            double oldTerminalUT = scopedCache.TerminalUT;
            scopedCache.TerminalUT = lastAuthoredUT;
            if (IsFiniteUT(scopedCache.TailStartsAtUT)
                && scopedCache.TailStartsAtUT > scopedCache.TerminalUT)
            {
                scopedCache.TailStartsAtUT = scopedCache.TerminalUT;
            }

            ParsekLog.Info("FinalizerCache",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Background terminal clamped: consumer={0} rec={1} pid={2} " +
                    "oldTerminalUT={3:F3} newTerminalUT={4:F3} lastAuthoredUT={5:F3} endUT={6:F3} " +
                    "cacheStatus={7} owner={8} confirmedDestroyed={9}",
                    consumerPath ?? "(null)",
                    recording.DebugName,
                    vesselPid,
                    oldTerminalUT,
                    scopedCache.TerminalUT,
                    lastAuthoredUT,
                    endUT,
                    scopedCache.Status,
                    scopedCache.Owner,
                    confirmedDestroyed));
            return true;
        }

        private static void LogBackgroundTerminalClampSkipped(
            Recording recording,
            RecordingFinalizationCache scopedCache,
            uint vesselPid,
            double endUT,
            string consumerPath,
            bool confirmedDestroyed,
            string reason,
            double lastAuthoredUT)
        {
            ParsekLog.Verbose("FinalizerCache",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Background terminal clamp skipped: consumer={0} rec={1} pid={2} " +
                    "reason={3} terminal={4} terminalUT={5:F3} lastAuthoredUT={6:F3} endUT={7:F3} " +
                    "cacheStatus={8} owner={9} confirmedDestroyed={10}",
                    consumerPath ?? "(null)",
                    recording != null ? recording.DebugName : "(null)",
                    vesselPid,
                    reason,
                    scopedCache != null && scopedCache.TerminalState.HasValue
                        ? scopedCache.TerminalState.Value.ToString()
                        : "(null)",
                    scopedCache != null ? scopedCache.TerminalUT : double.NaN,
                    lastAuthoredUT,
                    endUT,
                    scopedCache != null ? scopedCache.Status.ToString() : "(null)",
                    scopedCache != null ? scopedCache.Owner.ToString() : "(null)",
                    confirmedDestroyed));
        }

        private static void ApplyFinalizationCacheIdentity(
            RecordingFinalizationCache cache,
            Recording recording)
        {
            if (cache == null || recording == null)
                return;

            if (string.IsNullOrEmpty(cache.RecordingId))
                cache.RecordingId = recording.RecordingId;
            if (cache.VesselPersistentId == 0)
                cache.VesselPersistentId = recording.VesselPersistentId;
        }

        internal static RecordingFinalizationCache ScopeFinalizationCacheToBackgroundEndForTesting(
            RecordingFinalizationCache cache,
            double endUT)
        {
            return ScopeFinalizationCacheToBackgroundEnd(cache, endUT);
        }

        private static RecordingFinalizationCache ScopeFinalizationCacheToBackgroundEnd(
            RecordingFinalizationCache cache,
            double endUT)
        {
            if (cache == null)
                return null;

            var scoped = new RecordingFinalizationCache
            {
                RecordingId = cache.RecordingId,
                VesselPersistentId = cache.VesselPersistentId,
                Owner = cache.Owner,
                Status = cache.Status,
                CachedAtUT = cache.CachedAtUT,
                CachedAtRealtime = cache.CachedAtRealtime,
                RefreshReason = cache.RefreshReason,
                DeclineReason = cache.DeclineReason,
                LastObservedUT = cache.LastObservedUT,
                LastObservedBodyName = cache.LastObservedBodyName,
                LastSituation = cache.LastSituation,
                LastWasInAtmosphere = cache.LastWasInAtmosphere,
                LastHadMeaningfulThrust = cache.LastHadMeaningfulThrust,
                LastObservedOrbitDigest = cache.LastObservedOrbitDigest,
                TailStartsAtUT = cache.TailStartsAtUT,
                TerminalUT = ResolveBackgroundEndTerminalUT(cache, endUT),
                TerminalState = cache.TerminalState,
                TerminalBodyName = cache.TerminalBodyName,
                TerminalOrbit = cache.TerminalOrbit,
                TerminalPosition = cache.TerminalPosition,
                TerrainHeightAtEnd = cache.TerrainHeightAtEnd,
                PredictedSegments = cache.PredictedSegments != null
                    ? new List<OrbitSegment>(cache.PredictedSegments)
                    : new List<OrbitSegment>()
            };

            if (IsFiniteUT(scoped.TerminalUT)
                && (!IsFiniteUT(scoped.TailStartsAtUT)
                    || scoped.TailStartsAtUT > scoped.TerminalUT))
            {
                scoped.TailStartsAtUT = scoped.TerminalUT;
            }

            return scoped;
        }

        private static double ResolveBackgroundEndTerminalUT(
            RecordingFinalizationCache cache,
            double endUT)
        {
            if (!IsFiniteUT(endUT))
                return cache != null ? cache.TerminalUT : double.NaN;
            if (cache == null || !cache.TerminalState.HasValue)
                return endUT;

            if (cache.TerminalState.Value == TerminalState.Destroyed
                && IsFiniteUT(cache.TerminalUT))
            {
                return Math.Min(cache.TerminalUT, endUT);
            }

            return endUT;
        }

        internal RecordingFinalizationCache GetFinalizationCacheForRecording(Recording recording)
        {
            if (recording == null)
                return null;

            RecordingFinalizationCache cache;
            if (recording.VesselPersistentId != 0
                && finalizationCaches.TryGetValue(recording.VesselPersistentId, out cache))
            {
                return CopyAndAlignFinalizationCacheIdentity(cache, recording);
            }

            if (!string.IsNullOrEmpty(recording.RecordingId))
            {
                // RecordingId fallback is only for transitional PID-less recordings at
                // scene-exit commit time; typical tree sizes keep this linear scan small.
                foreach (var kvp in finalizationCaches)
                {
                    cache = kvp.Value;
                    if (cache != null
                        && string.Equals(cache.RecordingId, recording.RecordingId, StringComparison.Ordinal))
                    {
                        return CopyAndAlignFinalizationCacheIdentity(cache, recording);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Captures an orbital checkpoint for all on-rails background vessels at the given UT.
        /// For each vessel with an open orbit segment, closes the current segment and opens a
        /// fresh one with the vessel's current orbital elements. This creates clean reference
        /// points for orbital propagation during playback.
        /// Called at time warp boundaries and scene changes.
        /// </summary>
        internal void CheckpointAllVessels(double ut)
        {
            CheckpointAllVessels(ut, vesselFinderOverride);
        }

        /// <summary>
        /// Core checkpoint logic with injectable vessel finder for testability.
        /// If vesselFinder is null, uses FlightRecorder.FindVesselByPid.
        /// </summary>
        internal void CheckpointAllVessels(double ut, System.Func<uint, Vessel> vesselFinder)
        {
            if (tree == null) return;

            int checkpointed = 0;
            int skippedNoVessel = 0;
            int skippedNotOrbital = 0;
            int skippedDuplicateBoundary = 0;

            foreach (var kvp in onRailsStates)
            {
                uint vesselPid = kvp.Key;
                var state = kvp.Value;

                // Only checkpoint vessels with open orbit segments (on-rails, orbiting)
                if (!state.hasOpenOrbitSegment)
                {
                    // Landed/splashed or no-orbit vessels don't need orbital checkpoints
                    skippedNotOrbital++;
                    continue;
                }

                // Skip destroyed recordings — closing/opening a fresh orbit segment
                // at checkpoint UT would extend the sealed recording past its
                // identity-loss terminalUT, shifting the destroyed-explosion timing
                // in GhostPlaybackLogic.
                if (IsBackgroundRecordingDestroyed(state.recordingId))
                {
                    ParsekLog.VerboseRateLimited("BgRecorder",
                        $"destroyed-skip.checkpoint.{vesselPid}",
                        $"CheckpointAllVessels: skipping destroyed recording " +
                        $"pid={vesselPid} rec={state.recordingId}",
                        60.0);
                    continue;
                }

                if (IsDuplicateOrbitSegmentBoundary(state.currentOrbitSegment, ut))
                {
                    skippedDuplicateBoundary++;
                    continue;
                }

                // Find the live vessel to get current orbital elements
                Vessel v = vesselFinder != null
                    ? vesselFinder(vesselPid)
                    : FlightRecorder.FindVesselByPid(vesselPid);

                // Close the current orbit segment at checkpoint UT regardless of vessel availability.
                // The recorded orbital data up to this point is preserved even if the vessel
                // can't be found (e.g., destroyed between frames).
                CloseOrbitSegment(state, ut);

                if (v == null || v.orbit == null)
                {
                    skippedNoVessel++;
                    ParsekLog.Verbose("BgRecorder",
                        $"CheckpointAllVessels: closed segment for pid={vesselPid} but " +
                        $"vessel/orbit not found — no new segment opened");
                    continue;
                }

                // Open a fresh orbit segment with current orbital elements
                state.currentOrbitSegment = CreateOrbitSegmentFromVessel(v, ut, "Unknown");
                TryCanonicalizeBackgroundReFlyRecordingOrbitSegment(
                    state.recordingId,
                    v,
                    ut,
                    ref state.currentOrbitSegment,
                    "checkpoint");
                state.hasOpenOrbitSegment = true;
                checkpointed++;
            }

            // Bug #592: this summary fires once per onTimeWarpRateChanged, which KSP
            // can re-emit hundreds of times at the same rate during a single session.
            // Rate-limit per "shape" of the result so changes in checkpoint counts still
            // surface (we want to see the moment behavior changes), but identical
            // no-op summaries during a quiet warp burst collapse into one line.
            string key = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "checkpoint-all-{0}-{1}-{2}-{3}",
                checkpointed, skippedNotOrbital, skippedNoVessel, skippedDuplicateBoundary);
            ParsekLog.VerboseRateLimited("BgRecorder", key,
                $"CheckpointAllVessels at UT={ut:F2}: checkpointed={checkpointed}, " +
                $"skippedNotOrbital={skippedNotOrbital}, skippedNoVessel={skippedNoVessel}, " +
                $"skippedDuplicateBoundary={skippedDuplicateBoundary}");
        }

        private static bool IsDuplicateOrbitSegmentBoundary(OrbitSegment segment, double ut)
        {
            const double UTEpsilon = 0.000001;
            return IsFiniteUT(segment.startUT)
                && IsFiniteUT(ut)
                && Math.Abs(segment.startUT - ut) <= UTEpsilon;
        }

        /// <summary>
        /// Pure static method for determining whether an orbital checkpoint should be
        /// captured for a given vessel situation.
        /// </summary>
        internal static bool IsOrbitalCheckpointSituation(Vessel.Situations situation)
        {
            return situation == Vessel.Situations.ORBITING
                || situation == Vessel.Situations.SUB_ORBITAL
                || situation == Vessel.Situations.ESCAPING;
        }

        internal static bool ShouldPersistNoPayloadOnRailsBoundaryTrackSection(
            SegmentEnvironment previousEnv,
            SegmentEnvironment nextEnv,
            bool willHavePlayableOnRailsPayload)
        {
            if (willHavePlayableOnRailsPayload)
                return false;

            return RecordingOptimizer.SplitEnvironmentClass(previousEnv)
                != RecordingOptimizer.SplitEnvironmentClass(nextEnv);
        }

        #endregion

        #region Internal State Management

        private static SegmentEnvironment ClassifyCurrentBackgroundEnvironment(
            Vessel v,
            List<(Part part, ModuleEngines engine, int moduleIndex)> cachedEngines)
        {
            bool hasAtmo = v.mainBody != null && v.mainBody.atmosphere;
            double atmoDepth = hasAtmo ? v.mainBody.atmosphereDepth : 0;
            double approachAlt = (!hasAtmo && v.mainBody != null)
                ? FlightRecorder.ComputeApproachAltitude(v.mainBody) : 0;
            return ClassifyBackgroundEnvironment(
                hasAtmo, v.altitude, atmoDepth,
                (int)v.situation, v.srfSpeed, cachedEngines, approachAlt,
                v.isEVA, v.heightFromTerrain,
                EnvironmentDetector.IsHeightFromTerrainValid(v.heightFromTerrain),
                v.mainBody != null && v.mainBody.ocean);
        }

        private static bool WillInitializeOnRailsWithPlayablePayload(Vessel v)
        {
            bool isLanded = v.situation == Vessel.Situations.LANDED ||
                            v.situation == Vessel.Situations.SPLASHED;
            if (isLanded || v.situation == Vessel.Situations.PRELAUNCH)
                return false;

            if (v.mainBody != null &&
                FlightRecorder.ShouldSkipOrbitSegmentForAtmosphere(
                    v.mainBody.atmosphere, v.altitude, v.mainBody.atmosphereDepth))
            {
                return false;
            }

            return v.orbit != null;
        }

        private void InitializeOnRailsState(Vessel v, uint vesselPid, string recordingId)
        {
            double ut = Planetarium.GetUniversalTime();
            Recording treeRec;
            bool hasTreeRecording = tree.Recordings.TryGetValue(recordingId, out treeRec);

            // Destroyed-remnant guard runs FIRST — before the pending initial
            // trajectory point is consumed/applied and before any state-machine
            // branch writes landed/orbiting surface metadata. A destroyed recording
            // must NOT have an onRailsStates entry at all: leaving one would let
            // UpdateOnRails / FinalizeAllForCommit iterate over it (the secondary
            // guards on those paths catch the case, but the cleanest invariant is
            // "destroyed recordings are not in BG-tracking structures"). The pending
            // initial point is still drained from the dictionary so it does not leak
            // across to a future recording reusing the same pid.
            if (hasTreeRecording && treeRec.VesselDestroyed)
            {
                TrajectoryPoint discardedPoint;
                TryConsumePendingInitialTrajectoryPoint(vesselPid, out discardedPoint);
                ParsekLog.Verbose("BgRecorder",
                    $"InitializeOnRailsState: skipping destroyed recording — no on-rails state created " +
                    $"pid={vesselPid} body={v.mainBody?.name} sit={v.situation} rec={recordingId}");
                return;
            }

            TrajectoryPoint initialTrajectoryPoint;
            bool hasInitialTrajectoryPoint = TryConsumePendingInitialTrajectoryPoint(vesselPid, out initialTrajectoryPoint);
            if (hasInitialTrajectoryPoint && initialTrajectoryPoint.ut > ut)
                initialTrajectoryPoint.ut = ut;
            if (hasInitialTrajectoryPoint)
                TryCanonicalizeBackgroundReFlyRecordingPoint(
                    recordingId,
                    ref initialTrajectoryPoint,
                    "on-rails-initial");
            if (hasInitialTrajectoryPoint && hasTreeRecording)
            {
                // #419: only log "seeded" if the flat-append was accepted. A rejection
                // already produces its own warn inside ApplyTrajectoryPointToRecording.
                if (ApplyTrajectoryPointToRecording(treeRec, initialTrajectoryPoint))
                {
                    ParsekLog.Info("BgRecorder", string.Format(CultureInfo.InvariantCulture,
                        "Initial trajectory point seeded (on-rails): pid={0} ut={1:F2} " +
                        "lat={2:F4} lon={3:F4} alt={4:F0} vel=({5:F2},{6:F2},{7:F2}) |vel|={8:F2}",
                        vesselPid,
                        initialTrajectoryPoint.ut,
                        initialTrajectoryPoint.latitude,
                        initialTrajectoryPoint.longitude,
                        initialTrajectoryPoint.altitude,
                        initialTrajectoryPoint.velocity.x,
                        initialTrajectoryPoint.velocity.y,
                        initialTrajectoryPoint.velocity.z,
                        initialTrajectoryPoint.velocity.magnitude));
                }
            }

            var state = new BackgroundOnRailsState
            {
                vesselPid = vesselPid,
                recordingId = recordingId,
                lastExplicitEndUpdate = ut
            };

            bool isLanded = v.situation == Vessel.Situations.LANDED ||
                            v.situation == Vessel.Situations.SPLASHED;

            if (isLanded)
            {
                state.isLanded = true;
                state.hasOpenOrbitSegment = false;

                // Capture SurfacePosition
                if (hasTreeRecording)
                {
                    treeRec.SurfacePos = new SurfacePosition
                    {
                        body = v.mainBody?.name ?? "Kerbin",
                        latitude = v.latitude,
                        longitude = v.longitude,
                        altitude = v.altitude,
                        rotation = v.srfRelRotation,
                        rotationRecorded = true,
                        situation = v.situation == Vessel.Situations.SPLASHED
                            ? SurfaceSituation.Splashed
                            : SurfaceSituation.Landed
                    };
                    treeRec.ExplicitEndUT = ut;
                }

                ParsekLog.Verbose("BgRecorder", $"On-rails state initialized (landed): pid={vesselPid} " +
                    $"body={v.mainBody?.name} sit={v.situation}");
            }
            else if (v.mainBody != null &&
                     FlightRecorder.ShouldSkipOrbitSegmentForAtmosphere(
                         v.mainBody.atmosphere, v.altitude, v.mainBody.atmosphereDepth))
            {
                // In atmosphere — Keplerian orbit ignores drag, skip orbit segment
                state.hasOpenOrbitSegment = false;

                if (hasTreeRecording)
                {
                    treeRec.ExplicitEndUT = ut;
                }

                string atmoOrbitShape = v.orbit != null
                    ? string.Format(CultureInfo.InvariantCulture,
                        " sma={0:F1} ecc={1:F4} periAlt={2:F1} apoAlt={3:F1}",
                        v.orbit.semiMajorAxis, v.orbit.eccentricity, v.orbit.PeA, v.orbit.ApA)
                    : "";
                ParsekLog.Verbose("BgRecorder", $"On-rails state initialized (atmosphere, skip orbit): pid={vesselPid} " +
                    $"body={v.mainBody?.name} alt={v.altitude:F0} atmoDepth={v.mainBody.atmosphereDepth:F0}{atmoOrbitShape}");
            }
            else if (v.orbit != null)
            {
                // Open orbit segment
                state.currentOrbitSegment = CreateOrbitSegmentFromVessel(
                    v,
                    hasInitialTrajectoryPoint ? initialTrajectoryPoint.ut : ut,
                    "Kerbin");
                TryCanonicalizeBackgroundReFlyRecordingOrbitSegment(
                    recordingId,
                    v,
                    hasInitialTrajectoryPoint ? initialTrajectoryPoint.ut : ut,
                    ref state.currentOrbitSegment,
                    "on-rails-orbit");
                state.hasOpenOrbitSegment = true;

                if (hasTreeRecording)
                {
                    treeRec.ExplicitEndUT = ut;
                }

                ParsekLog.Verbose("BgRecorder", string.Format(CultureInfo.InvariantCulture,
                    "On-rails state initialized (orbiting): pid={0} body={1} sma={2:F1} ecc={3:F4} " +
                    "periAlt={4:F1} apoAlt={5:F1} inc={6:F2}",
                    vesselPid,
                    v.mainBody?.name,
                    v.orbit.semiMajorAxis,
                    v.orbit.eccentricity,
                    v.orbit.PeA,
                    v.orbit.ApA,
                    v.orbit.inclination));
            }
            else
            {
                // No orbit and not landed — edge case (e.g. vessel on launchpad with no orbit)
                state.hasOpenOrbitSegment = false;

                if (hasTreeRecording)
                {
                    treeRec.ExplicitEndUT = ut;
                }

                ParsekLog.Verbose("BgRecorder", $"On-rails state initialized (no orbit): pid={vesselPid}");
            }

            onRailsStates[vesselPid] = state;
        }

        private void InitializeLoadedState(Vessel v, uint vesselPid, string recordingId,
            InheritedEngineState? inherited = null)
        {
            var state = new BackgroundVesselState
            {
                vesselPid = vesselPid,
                recordingId = recordingId
            };
            if (v != null && v.transform != null)
            {
                state.lastWorldRotation = TrajectoryMath.SanitizeQuaternion(v.transform.rotation);
                state.hasLastWorldRotation = true;
            }

            // Cache engine/RCS/robotic modules
            state.cachedEngines = FlightRecorder.CacheEngineModules(v);
            state.cachedRcsModules = FlightRecorder.CacheRcsModules(v);
            state.cachedRoboticModules = FlightRecorder.CacheRoboticModules(v);

            // Seed all tracking sets with current part state (mirrors FlightRecorder.SeedExistingPartStates)
            SeedBackgroundPartStates(v, state);

            MergeInheritedLoadedEngineState(v, vesselPid, recordingId, inherited, state);

            // Emit seed events ONLY if the recording has no part events yet.
            // If a prior active recorder (or earlier background load) already seeded events,
            // re-emitting at the current UT would poison FindLastInterestingUT for boring-tail
            // trimming (bug A / #263 sibling — rover recording not trimmed because of stale
            // DeployableExtended events at UT 2068).
            Recording treeRecForSeed;
            bool hasTreeRecording = TrySeedLoadedPartEvents(v, vesselPid, recordingId, state, out treeRecForSeed);

            // Initialize environment tracking and open first TrackSection
            double ut = Planetarium.GetUniversalTime();
            TrajectoryPoint initialTrajectoryPoint;
            bool hasInitialTrajectoryPoint = TryConsumePendingInitialTrajectoryPoint(vesselPid, out initialTrajectoryPoint);
            if (hasInitialTrajectoryPoint && initialTrajectoryPoint.ut > ut)
                initialTrajectoryPoint.ut = ut;
            if (hasInitialTrajectoryPoint)
                TryCanonicalizeBackgroundReFlyRecordingPoint(
                    recordingId,
                    ref initialTrajectoryPoint,
                    "loaded-initial");
            SegmentEnvironment overrideEnv;
            SegmentEnvironment initialEnv;
            if (TryConsumePendingInitialEnvironmentOverride(vesselPid, out overrideEnv))
            {
                initialEnv = overrideEnv;
                ParsekLog.Verbose("BgRecorder",
                    $"InitializeLoadedState: consumed initial environment override for pid={vesselPid}: {initialEnv}");
            }
            else
            {
                bool hasAtmo = v.mainBody != null && v.mainBody.atmosphere;
                double atmoDepth = hasAtmo ? v.mainBody.atmosphereDepth : 0;
                double approachAlt = (!hasAtmo && v.mainBody != null)
                    ? FlightRecorder.ComputeApproachAltitude(v.mainBody) : 0;
                initialEnv = ClassifyBackgroundEnvironment(
                    hasAtmo, v.altitude, atmoDepth,
                    (int)v.situation, v.srfSpeed, state.cachedEngines, approachAlt,
                    v.isEVA, v.heightFromTerrain,
                    EnvironmentDetector.IsHeightFromTerrainValid(v.heightFromTerrain),
                    v.mainBody != null && v.mainBody.ocean);
            }
            state.environmentHysteresis = new EnvironmentHysteresis(initialEnv);

            // PR 3b §"Initial seed point coordinate transform": when the recording
            // carries the debris parent-anchor contract, open the first track section
            // as Relative-to-parent and convert the body-fixed seed point into anchor-
            // local metres BEFORE it lands in the section. Without this, the seed has
            // CLAUDE.md "metres-as-degrees" mismatch — its lat/lon/alt are body-fixed
            // degrees but downstream playback in a Relative section interprets them
            // as anchor-local (dx, dy, dz) metres.
            //
            // Resolve the recording: TrySeedLoadedPartEvents already provides
            // `treeRecForSeed` when `hasTreeRecording == true`; fall back to a tree
            // lookup when seeding was skipped (e.g. recording already had part events).
            Recording treeRecForDebris = hasTreeRecording ? treeRecForSeed : null;
            if (treeRecForDebris == null && tree?.Recordings != null && !string.IsNullOrEmpty(recordingId))
                tree.Recordings.TryGetValue(recordingId, out treeRecForDebris);
            bool debrisContractApplies = treeRecForDebris != null
                && !string.IsNullOrEmpty(treeRecForDebris.ParentAnchorRecordingId)
                && hasInitialTrajectoryPoint;

            bool debrisSeedOpened = false;
            if (debrisContractApplies)
            {
                double seedParentDistance = ResolveDebrisParentDistanceMeters(
                    treeRecForDebris,
                    v,
                    out Recording parentRec,
                    out Vessel parentVessel);
                bool seedWithinRelativeRange = IsFinite(seedParentDistance)
                    && seedParentDistance <= DebrisHalfFidelityProximityRangeMeters;
                state.debrisProximityDistanceMeters = seedParentDistance;
                state.debrisProximityTier = ProximitySamplingCadence.Resolve(
                    seedParentDistance,
                    DebrisFullFidelityProximityRangeMeters,
                    DebrisHalfFidelityProximityRangeMeters,
                    out state.debrisProximityReason);
                LogDebrisProximityTierTransition(
                    state.vesselPid,
                    treeRecForDebris,
                    ProximitySamplingTier.None,
                    state.debrisProximityTier,
                    seedParentDistance,
                    state.debrisProximityReason,
                    "InitializeLoadedState");
                if (parentRec == null
                    || parentVessel == null
                    || !parentVessel.loaded
                    || parentVessel.packed
                    || !seedWithinRelativeRange)
                {
                    pendingDebrisSeedParentAnchorPoints.Remove(vesselPid);
                    ParsekLog.Warn("BgRecorder",
                        $"InitializeLoadedState: debris contract uses Absolute seed, " +
                        $"falling back to Absolute seed: pid={vesselPid} recId={recordingId} " +
                        $"parentRecId={treeRecForDebris.ParentAnchorRecordingId} " +
                        $"parentRecResolved={parentRec != null} " +
                        $"parentVesselResolved={parentVessel != null} " +
                        $"parentVesselLoaded={parentVessel != null && parentVessel.loaded} " +
                        $"parentVesselPacked={parentVessel != null && parentVessel.packed} " +
                        $"parentDistance={seedParentDistance.ToString("F1", CultureInfo.InvariantCulture)}m " +
                        $"relativeEntryRange={DebrisHalfFidelityProximityRangeMeters.ToString("F1", CultureInfo.InvariantCulture)}m " +
                        $"initUT={ut.ToString("F3", CultureInfo.InvariantCulture)} " +
                        $"seed={DescribeTrajectoryPointForDiagnostics(initialTrajectoryPoint)} " +
                        $"{DescribeVesselRootForDiagnostics("child", v)}");
                }
                else
                {
                    // Same vesselTransform contract as TryResolveBackgroundAnchorPoseForCandidate:
                    // the seed fallback (used when both queued-parent-seed and
                    // recorded-parent-pose resolution fail) and the live candidate's
                    // stored WorldPos must share the surface-pose frame the parent's
                    // recorded lla lives in. GetWorldPos3D (CoMD) here would re-introduce
                    // a smaller version of the same CoM-vs-vesselTransform mismatch on
                    // the warn-logged failure path.
                    var liveAnchorPose = new AnchorPose(
                        (Vector3d)parentVessel.transform.position,
                        parentVessel.transform.rotation,
                        -1,
                        treeRecForDebris.ParentAnchorRecordingId);

                    var liveCandidate = new RecordingAnchorCandidate(
                        treeRecForDebris.ParentAnchorRecordingId,
                        liveAnchorPose.WorldPos,
                        liveAnchorPose.WorldRotation,
                        AnchorCandidateSource.Live,
                        diagnosticPid: parentVessel.persistentId,
                        ghostIndex: -1,
                        isSealed: false,
                        isSameReplayPoint: false,
                        isSameVesselLineage: false);
                    SetBackgroundCurrentAnchor(state, liveCandidate);

                    AnchorPose seedAnchorPose = liveAnchorPose;
                    string seedAnchorSource = "live-warn-fallback";
                    string queuedSeedAnchorReason;
                    RelativeAnchorResolveFailure recordedSeedAnchorFailure = default;
                    string seedRecordedFallbackFrameContract = "unresolved";
                    if (TryConsumePendingDebrisSeedParentAnchorPose(
                            vesselPid,
                            treeRecForDebris.ParentAnchorRecordingId,
                            initialTrajectoryPoint.ut,
                            out AnchorPose queuedSeedAnchorPose,
                            out queuedSeedAnchorReason))
                    {
                        seedAnchorPose = queuedSeedAnchorPose;
                        seedAnchorSource = "queued-parent-seed";
                        ParsekLog.Verbose("BgRecorder",
                            $"InitializeLoadedState: consumed queued debris seed parent anchor: " +
                            $"pid={vesselPid} recId={recordingId} " +
                            $"parentRecId={treeRecForDebris.ParentAnchorRecordingId} " +
                            $"seedUT={initialTrajectoryPoint.ut.ToString("F3", CultureInfo.InvariantCulture)}");
                    }
                    else if (TryResolveBackgroundRecordedAnchorPose(
                            treeRecForDebris.ParentAnchorRecordingId,
                            initialTrajectoryPoint.ut,
                            out AnchorPose recordedSeedAnchorPose,
                            out recordedSeedAnchorFailure))
                    {
                        seedAnchorPose = recordedSeedAnchorPose;
                        seedAnchorSource = "recorded";
                        seedRecordedFallbackFrameContract = ClassifyBackgroundRelativeFrameContract(
                            treeRecForDebris.ParentAnchorRecordingId,
                            seedAnchorSource,
                            AnchorCandidateSource.Ghost,
                            hasCurrentAnchorCandidate: false,
                            hasPreReFlyAnchorSnapshot: HasActiveReFlyPreReFlyAnchorSnapshot(
                                treeRecForDebris.ParentAnchorRecordingId));
                    }
                    else
                    {
                        ParsekLog.Warn("BgRecorder",
                            $"InitializeLoadedState: debris seed recorded parent pose unavailable, " +
                            $"falling back to live parent pose: pid={vesselPid} recId={recordingId} " +
                            $"parentRecId={treeRecForDebris.ParentAnchorRecordingId} " +
                            $"reason={RelativeAnchorResolveFailure.ReasonOrFallback(recordedSeedAnchorFailure, "unknown")} " +
                            $"queuedReason={queuedSeedAnchorReason ?? "unknown"} " +
                            $"seedUT={initialTrajectoryPoint.ut.ToString("F3", CultureInfo.InvariantCulture)} " +
                            $"initUT={ut.ToString("F3", CultureInfo.InvariantCulture)}");
                    }

                    StartBackgroundTrackSection(state, initialEnv, ReferenceFrame.Relative,
                        initialTrajectoryPoint.ut);
                    TrajectoryPoint absoluteSeedPoint = initialTrajectoryPoint;
                    bool seedRelativeApplied = ApplyBackgroundRelativeOffsetForAnchorPose(
                        state,
                        ref initialTrajectoryPoint,
                        v,
                        seedAnchorPose,
                        treeRecForDebris.ParentAnchorRecordingId,
                        AnchorCandidateSource.Live,
                        parentVessel.persistentId,
                        logSample: true,
                        sourceLabel: seedAnchorSource);
                    string seedFrameContract = ClassifyBackgroundRelativeFrameContract(
                        treeRecForDebris.ParentAnchorRecordingId,
                        seedAnchorSource,
                        AnchorCandidateSource.Live,
                        hasCurrentAnchorCandidate: string.Equals(seedAnchorSource, "live-warn-fallback", StringComparison.Ordinal),
                        hasPreReFlyAnchorSnapshot: HasActiveReFlyPreReFlyAnchorSnapshot(
                            treeRecForDebris.ParentAnchorRecordingId));
                    string seedParentDrift = FormatActiveReFlyParentDriftFromRecorded(
                        treeRecForDebris.ParentAnchorRecordingId,
                        parentVessel.persistentId,
                        initialTrajectoryPoint.ut);
                    string seedParentDriftField = FormatParentDriftLogField(seedParentDrift);
                    string seedFocusRecordingId = string.IsNullOrEmpty(recordingId) ? "(missing)" : recordingId;
                    LogDebrisSeedRelativeConversionDiagnostics(
                        vesselPid,
                        recordingId,
                        treeRecForDebris.ParentAnchorRecordingId,
                        v,
                        parentVessel,
                        absoluteSeedPoint,
                        initialTrajectoryPoint,
                        seedAnchorPose,
                        seedAnchorSource,
                        seedFrameContract,
                        seedParentDrift,
                        seedFocusRecordingId,
                        seedRecordedFallbackFrameContract,
                        seedRelativeApplied,
                        ut);
                    if (!seedRelativeApplied)
                    {
                        ParsekLog.Warn("BgRecorder",
                            $"InitializeLoadedState: debris seed transform failed, " +
                            $"section opened Relative but offset not applied: " +
                            $"pid={vesselPid} recId={recordingId} parentRecId={treeRecForDebris.ParentAnchorRecordingId}");
                    }
                    ActivateBackgroundHighFidelitySampling(
                        state,
                        initialTrajectoryPoint.ut,
                        "initial-trajectory-point-debris");
                    // v9 surface clearance must reach the very first
                    // body-fixed shadow frame too -- otherwise surface debris
                    // replays its seed at raw recorded altitude while every
                    // subsequent shadow frame gets terrain correction,
                    // producing a one-frame visual hop at terrain transitions.
                    // The in-frames point is anchor-local metres under a
                    // Relative section, so ApplySurfaceClearanceToPoint
                    // correctly gate-skips it (the helper's frame gate
                    // rejects Relative). Mirrors the periodic-sample wiring
                    // at OnBackgroundPhysicsFrame and the structural-event
                    // flush wiring -- those previously got the body-fixed
                    // shadow clearance, but the seed path did not.
                    ApplySurfaceClearanceToPoint(state, v, ref initialTrajectoryPoint);
                    if (seedRelativeApplied)
                    {
                        ApplySurfaceClearanceToBodyFixedShadow(
                            state, v, ref absoluteSeedPoint);
                    }
                    if (hasTreeRecording)
                        ApplyInitialTrajectoryPoint(
                            state, treeRecForSeed, initialTrajectoryPoint, absoluteSeedPoint);
                    else
                        AppendFrameToCurrentTrackSection(
                            state, initialTrajectoryPoint, absoluteSeedPoint);
                    debrisSeedOpened = true;

                    ParsekLog.Info("BgRecorder",
                        $"Debris parent-anchor contract applied at seed: pid={vesselPid} " +
                        $"recId={recordingId} parentRecId={treeRecForDebris.ParentAnchorRecordingId} " +
                        $"parentPid={parentVessel.persistentId} " +
                        $"frameContract={seedFrameContract} " +
                        seedParentDriftField +
                        $"seedAnchorSource={seedAnchorSource} seedFocusRecordingId={seedFocusRecordingId} " +
                        $"seedRecordedFallbackFrameContract={seedRecordedFallbackFrameContract} " +
                        $"seedUT={initialTrajectoryPoint.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                        $"|offset|=(dx={initialTrajectoryPoint.latitude:F2},dy={initialTrajectoryPoint.longitude:F2},dz={initialTrajectoryPoint.altitude:F2})");
                }
            }

            if (!debrisSeedOpened)
            {
                StartBackgroundTrackSection(state, initialEnv, ReferenceFrame.Absolute,
                    hasInitialTrajectoryPoint ? initialTrajectoryPoint.ut : ut);
                if (hasInitialTrajectoryPoint)
                {
                    ActivateBackgroundHighFidelitySampling(
                        state,
                        initialTrajectoryPoint.ut,
                        "initial-trajectory-point");
                    if (hasTreeRecording)
                        ApplyInitialTrajectoryPoint(state, treeRecForSeed, initialTrajectoryPoint);
                    else
                        AppendFrameToCurrentTrackSection(state, initialTrajectoryPoint);
                }
            }

            ParsekLog.Verbose("BgRecorder", $"Loaded state initialized: pid={vesselPid} " +
                $"engines={state.cachedEngines?.Count ?? 0} rcs={state.cachedRcsModules?.Count ?? 0} " +
                $"robotics={state.cachedRoboticModules?.Count ?? 0} initialEnv={initialEnv}");

            loadedStates[vesselPid] = state;
        }

        private void MergeInheritedLoadedEngineState(
            Vessel v,
            uint vesselPid,
            string recordingId,
            InheritedEngineState? inherited,
            BackgroundVesselState state)
        {
            // Bug #298: merge inherited parent engine/RCS state for child debris.
            // SeedEngines checks engine.isOperational which is false after fuel severance,
            // so child debris recordings get zero engine seed events. Merge from the parent's
            // known-good state to fill in the gaps.
            if (inherited.HasValue)
            {
                var inh = inherited.Value;
                ParsekLog.Verbose("BgRecorder",
                    $"InitializeLoadedState: inherited state for pid={vesselPid}: " +
                    $"engines={inh.activeEngineKeys?.Count ?? 0} rcs={inh.activeRcsKeys?.Count ?? 0} " +
                    $"childParts={v.parts?.Count ?? 0}");

                var childPartPids = new HashSet<uint>();
                if (v.parts != null)
                    for (int i = 0; i < v.parts.Count; i++)
                        if (v.parts[i] != null)
                            childPartPids.Add(v.parts[i].persistentId);

                int merged = MergeInheritedEngineState(inherited,
                    state.activeEngineKeys, state.lastThrottle,
                    state.activeRcsKeys, state.lastRcsThrottle,
                    childPartPids, state.allEngineKeys);
                if (merged > 0)
                    ParsekLog.Info("BgRecorder",
                        $"Merged {merged} inherited engine/RCS key(s) for pid={vesselPid} recId={recordingId} (#298)");
                else
                    ParsekLog.Verbose("BgRecorder",
                        $"InitializeLoadedState: 0 inherited keys merged for pid={vesselPid} " +
                        $"(no matching PIDs on child, or all already seeded)");
            }
            else
            {
                ParsekLog.Verbose("BgRecorder",
                    $"InitializeLoadedState: no inherited state for pid={vesselPid} (inherited=null)");
            }
        }

        private bool TrySeedLoadedPartEvents(
            Vessel v,
            uint vesselPid,
            string recordingId,
            BackgroundVesselState state,
            out Recording treeRecForSeed)
        {
            bool hasTreeRecording = tree.Recordings.TryGetValue(recordingId, out treeRecForSeed);
            if (!hasTreeRecording)
            {
                // Tree recording not found — unexpected state. The caller supplied a recordingId
                // that doesn't exist in the tree's Recordings dict, which means a lifecycle bug
                // somewhere upstream (tree corrupted, recording deleted without updating the
                // background map, race between StopRecording and InitializeLoadedState, etc.).
                // Log loud so we notice; seeding is skipped because there's no target anyway.
                ParsekLog.Warn("BgRecorder",
                    $"InitializeLoadedState: tree recording '{recordingId}' not found for " +
                    $"pid={vesselPid} — skipping seed events (upstream lifecycle bug)");
            }
            else if (treeRecForSeed.PartEvents.Count > 0)
            {
                ParsekLog.Verbose("BgRecorder",
                    $"InitializeLoadedState: skipping seed events for pid={vesselPid} recId={recordingId} " +
                    $"— recording already has {treeRecForSeed.PartEvents.Count} part event(s)");
            }
            else
            {
                var partNamesByPid = new Dictionary<uint, string>();
                if (v.parts != null)
                {
                    for (int i = 0; i < v.parts.Count; i++)
                    {
                        Part p = v.parts[i];
                        if (p != null && !partNamesByPid.ContainsKey(p.persistentId))
                            partNamesByPid[p.persistentId] = p.partInfo?.name ?? "unknown";
                    }
                }
                double seedUT = Planetarium.GetUniversalTime();
                var seedSets = BuildPartTrackingSetsFromState(state);
                var seedEvents = PartStateSeeder.EmitSeedEvents(seedSets, partNamesByPid, seedUT, "BgRecorder");
                if (seedEvents.Count > 0)
                {
                    treeRecForSeed.PartEvents.AddRange(seedEvents);
                    // If the very next event is an OnSave (e.g., the player
                    // F5s immediately after a background vessel enters loaded
                    // physics), the seed events would otherwise be lost because
                    // nothing else marks dirty until the next poll emits an event.
                    treeRecForSeed.MarkFilesDirty();
                }
            }

            return hasTreeRecording;
        }

        /// <summary>
        /// Builds a PartTrackingSets struct from a BackgroundVesselState's tracking fields.
        /// Used by both InitializeLoadedState (seed events) and SeedBackgroundPartStates (seed state).
        /// </summary>
        private static PartTrackingSets BuildPartTrackingSetsFromState(BackgroundVesselState state)
        {
            return new PartTrackingSets
            {
                deployedFairings = state.deployedFairings,
                jettisonedShrouds = state.jettisonedShrouds,
                parachuteStates = state.parachuteStates,
                extendedDeployables = state.extendedDeployables,
                lightsOn = state.lightsOn,
                blinkingLights = state.blinkingLights,
                lightBlinkRates = state.lightBlinkRates,
                deployedGear = state.deployedGear,
                openCargoBays = state.openCargoBays,
                deployedLadders = state.deployedLadders,
                deployedAnimationGroups = state.deployedAnimationGroups,
                deployedAnimateGenericModules = state.deployedAnimateGenericModules,
                deployedAeroSurfaceModules = state.deployedAeroSurfaceModules,
                deployedControlSurfaceModules = state.deployedControlSurfaceModules,
                deployedRobotArmScannerModules = state.deployedRobotArmScannerModules,
                animateHeatLevels = state.animateHeatLevels,
                activeEngineKeys = state.activeEngineKeys,
                allEngineKeys = state.allEngineKeys,
                lastThrottle = state.lastThrottle,
                activeRcsKeys = state.activeRcsKeys,
                lastRcsThrottle = state.lastRcsThrottle,
            };
        }

        /// <summary>
        /// Pre-populates a BackgroundVesselState's tracking sets with the current
        /// state of every part on the vessel. Delegates to shared PartStateSeeder.
        /// </summary>
        private static void SeedBackgroundPartStates(Vessel v, BackgroundVesselState state)
        {
            PartStateSeeder.SeedPartStates(v,
                BuildPartTrackingSetsFromState(state),
                state.cachedEngines, state.cachedRcsModules,
                seedColorChangerLights: false, logTag: "BgRecorder");
        }

        /// <summary>
        /// Creates an InheritedEngineState snapshot from a BackgroundVesselState.
        /// Returns null if no engines or RCS are active. Used in HandleBackgroundVesselSplit
        /// to capture parent state before CloseParentRecording destroys loadedStates.
        /// Defined here (not on the struct) because BackgroundVesselState is a private inner class.
        /// </summary>
        private static InheritedEngineState? InheritedStateFromBackgroundVessel(BackgroundVesselState state)
        {
            if (state == null) return null;
            bool hasEngines = state.activeEngineKeys != null && state.activeEngineKeys.Count > 0;
            bool hasRcs = state.activeRcsKeys != null && state.activeRcsKeys.Count > 0;
            if (!hasEngines && !hasRcs) return null;

            return new InheritedEngineState
            {
                activeEngineKeys = hasEngines ? new HashSet<ulong>(state.activeEngineKeys) : null,
                engineThrottles = hasEngines && state.lastThrottle != null
                    ? new Dictionary<ulong, float>(state.lastThrottle) : null,
                activeRcsKeys = hasRcs ? new HashSet<ulong>(state.activeRcsKeys) : null,
                rcsThrottles = hasRcs && state.lastRcsThrottle != null
                    ? new Dictionary<ulong, float>(state.lastRcsThrottle) : null
            };
        }

        /// <summary>
        /// Merges inherited engine/RCS state from a parent vessel into a child's
        /// tracking collections. Only keys whose decoded PID matches a part on the
        /// child vessel are merged. Upgrades throttle if the inherited value is higher
        /// than the live-seeded value — SeedEngines may find throttle=0 (KSP timing)
        /// while the parent had the engine at full power before breakup.
        /// Pure static method for testability. Bug #298.
        /// </summary>
        internal static int MergeInheritedEngineState(
            InheritedEngineState? inherited,
            HashSet<ulong> targetActiveEngineKeys,
            Dictionary<ulong, float> targetLastThrottle,
            HashSet<ulong> targetActiveRcsKeys,
            Dictionary<ulong, float> targetLastRcsThrottle,
            HashSet<uint> childPartPids,
            HashSet<ulong> allEngineKeys = null)
        {
            if (!inherited.HasValue) return 0;
            var inh = inherited.Value;
            int merged = 0;
            int skippedNonOperational = 0;

            if (inh.activeEngineKeys != null)
            {
                foreach (ulong key in inh.activeEngineKeys)
                {
                    uint pid; int midx;
                    FlightRecorder.DecodeEngineKey(key, out pid, out midx);
                    if (!childPartPids.Contains(pid)) continue; // engine not on this child vessel

                    float inhThrottle = 1f;
                    if (inh.engineThrottles != null)
                    {
                        float t;
                        if (inh.engineThrottles.TryGetValue(key, out t))
                            inhThrottle = t;
                    }

                    if (targetActiveEngineKeys.Contains(key))
                    {
                        // Already seeded by SeedEngines — upgrade throttle if inherited is higher.
                        // SeedEngines may find throttle=0 (KSP hasn't propagated throttle to
                        // debris yet) while the parent had the engine at full power.
                        float existing = 0f;
                        targetLastThrottle.TryGetValue(key, out existing);
                        if (inhThrottle > existing)
                        {
                            targetLastThrottle[key] = inhThrottle;
                            merged++;
                            ParsekLog.Verbose("BgRecorder",
                                $"Inherited engine throttle upgraded: pid={pid} midx={midx} " +
                                $"{existing:F2}→{inhThrottle:F2} (#298)");
                        }
                        continue;
                    }

                    // T58: SeedEngines adds ALL engines to allEngineKeys (operational or not).
                    // If the key is in allEngineKeys but NOT in activeEngineKeys, SeedEngines
                    // found the engine but it's non-operational (fuel severed, flameout after
                    // staging). Don't override with inherited state — the child's assessment
                    // is correct. Only inherit when the engine wasn't found at all (timing).
                    if (allEngineKeys != null && allEngineKeys.Contains(key))
                    {
                        skippedNonOperational++;
                        ParsekLog.Verbose("BgRecorder",
                            $"Inherited engine skipped (non-operational on child): pid={pid} midx={midx} " +
                            $"inhThrottle={inhThrottle:F2} (T58)");
                        continue;
                    }

                    targetActiveEngineKeys.Add(key);
                    targetLastThrottle[key] = inhThrottle;
                    merged++;
                    ParsekLog.Verbose("BgRecorder",
                        $"Inherited engine key merged: pid={pid} midx={midx} throttle={inhThrottle:F2} (#298)");
                }
            }

            if (skippedNonOperational > 0)
                ParsekLog.Info("BgRecorder",
                    $"Skipped {skippedNonOperational} inherited engine(s) — non-operational on child (T58)");

            if (inh.activeRcsKeys != null)
            {
                foreach (ulong key in inh.activeRcsKeys)
                {
                    uint pid; int midx;
                    FlightRecorder.DecodeEngineKey(key, out pid, out midx);
                    if (!childPartPids.Contains(pid)) continue;

                    float inhThrottle = 1f;
                    if (inh.rcsThrottles != null)
                    {
                        float t;
                        if (inh.rcsThrottles.TryGetValue(key, out t))
                            inhThrottle = t;
                    }

                    if (targetActiveRcsKeys.Contains(key))
                    {
                        float existing = 0f;
                        targetLastRcsThrottle.TryGetValue(key, out existing);
                        if (inhThrottle > existing)
                        {
                            targetLastRcsThrottle[key] = inhThrottle;
                            merged++;
                            ParsekLog.Verbose("BgRecorder",
                                $"Inherited RCS throttle upgraded: pid={pid} midx={midx} " +
                                $"{existing:F2}→{inhThrottle:F2} (#298)");
                        }
                        continue;
                    }

                    targetActiveRcsKeys.Add(key);
                    targetLastRcsThrottle[key] = inhThrottle;
                    merged++;
                    ParsekLog.Verbose("BgRecorder",
                        $"Inherited RCS key merged: pid={pid} midx={midx} throttle={inhThrottle:F2} (#298)");
                }
            }

            return merged;
        }

        /// <summary>
        /// Creates an OrbitSegment from a vessel's current orbital elements.
        /// Used by OnBackgroundVesselSOIChanged, CheckpointAllVessels, and InitializeOnRailsState.
        /// </summary>
        private static OrbitSegment CreateOrbitSegmentFromVessel(Vessel v, double startUT, string defaultBodyName)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                inclination = v.orbit.inclination,
                eccentricity = v.orbit.eccentricity,
                semiMajorAxis = v.orbit.semiMajorAxis,
                longitudeOfAscendingNode = v.orbit.LAN,
                argumentOfPeriapsis = v.orbit.argumentOfPeriapsis,
                meanAnomalyAtEpoch = v.orbit.meanAnomalyAtEpoch,
                epoch = v.orbit.epoch,
                bodyName = v.mainBody?.name ?? defaultBodyName
            };
        }

        private static TrajectoryPoint CreateTrajectoryPointFromVessel(Vessel v, double ut)
        {
            Vector3 velocity = v.packed
                ? (Vector3)v.obt_velocity
                : (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity());

            return new TrajectoryPoint
            {
                ut = ut,
                latitude = v.latitude,
                longitude = v.longitude,
                altitude = v.altitude,
                rotation = v.srfRelRotation,
                velocity = velocity,
                bodyName = v.mainBody?.name ?? "Unknown",
                funds = Funding.Instance != null ? Funding.Instance.Funds : 0,
                science = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0,
                reputation = Reputation.Instance != null ? Reputation.CurrentRep : 0,
                // Phase 7: packed/on-rails background vessels do not have
                // surface-clearance sections; leave clearance as NaN sentinel.
                recordedGroundClearance = double.NaN
            };
        }

        private void CloseOrbitSegment(BackgroundOnRailsState state, double ut)
        {
            if (!state.hasOpenOrbitSegment) return;

            state.currentOrbitSegment.endUT = ut;

            Recording treeRec;
            bool appendedCheckpoint = false;
            string checkpointSkipReason = null;
            if (tree.Recordings.TryGetValue(state.recordingId, out treeRec))
            {
                appendedCheckpoint = RecordingStore.TryAppendClosedOnRailsCheckpointSection(
                    treeRec,
                    state.currentOrbitSegment,
                    markDirty: true,
                    out checkpointSkipReason);
                treeRec.ExplicitEndUT = ut;
            }

            state.hasOpenOrbitSegment = false;

            ParsekLog.Verbose("BgRecorder", $"Orbit segment closed: pid={state.vesselPid} " +
                $"UT={state.currentOrbitSegment.startUT:F1}-{ut:F1} body={state.currentOrbitSegment.bodyName} " +
                $"checkpointSection={(appendedCheckpoint ? "added" : "skipped:" + (checkpointSkipReason ?? "unknown"))}");
        }

        private static bool TryResolveRootPartSurfacePose(
            Vessel v,
            out double latitude,
            out double longitude,
            out double altitude,
            out Quaternion rotation)
        {
            latitude = v != null ? v.latitude : 0.0;
            longitude = v != null ? v.longitude : 0.0;
            altitude = v != null ? v.altitude : 0.0;
            rotation = v != null ? v.srfRelRotation : Quaternion.identity;

            if (v?.mainBody == null || v.rootPart == null || v.rootPart.transform == null || v.mainBody.bodyTransform == null)
                return false;

            Vector3d worldPos = v.rootPart.transform.position;
            latitude = v.mainBody.GetLatitude(worldPos);
            longitude = v.mainBody.GetLongitude(worldPos);
            altitude = v.mainBody.GetAltitude(worldPos);
            rotation = Quaternion.Inverse(v.mainBody.bodyTransform.rotation) * v.rootPart.transform.rotation;
            return true;
        }

        private static void LogDebrisSeedRelativeConversionDiagnostics(
            uint vesselPid,
            string recordingId,
            string parentRecordingId,
            Vessel childVessel,
            Vessel parentVessel,
            TrajectoryPoint absoluteSeedPoint,
            TrajectoryPoint relativeSeedPoint,
            AnchorPose anchorPose,
            string anchorSource,
            string frameContract,
            string parentDriftFromRecorded,
            string seedFocusRecordingId,
            string seedRecordedFallbackFrameContract,
            bool relativeApplied,
            double initUT)
        {
            bool hasSeedWorld = TryResolveTrajectoryPointWorldPosition(
                absoluteSeedPoint,
                childVessel,
                out Vector3d seedWorld);
            string seedAnchorDelta = hasSeedWorld
                ? DiagnosticFormatters.FormatVector3d(seedWorld - anchorPose.WorldPos)
                : "unresolved";
            string parentDriftField = FormatParentDriftLogField(parentDriftFromRecorded);

            ParsekLog.Verbose("BgRecorder",
                $"Debris seed relative conversion diagnostics: pid={vesselPid} " +
                $"recId={recordingId} parentRecId={parentRecordingId} " +
                $"relativeApplied={(relativeApplied ? "T" : "F")} " +
                $"initUT={initUT.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"seedUT={absoluteSeedPoint.ut.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"init-seed-dt={(initUT - absoluteSeedPoint.ut).ToString("F3", CultureInfo.InvariantCulture)} " +
                $"anchorSource={anchorSource ?? "unknown"} " +
                $"frameContract={frameContract ?? "unknown"} " +
                parentDriftField +
                $"seedFocusRecordingId={seedFocusRecordingId ?? "(missing)"} " +
                $"seedRecordedFallbackFrameContract={seedRecordedFallbackFrameContract ?? "unresolved"} " +
                $"seedAbs={DescribeTrajectoryPointForDiagnostics(absoluteSeedPoint)} " +
                $"seedWorld={(hasSeedWorld ? DiagnosticFormatters.FormatVector3d(seedWorld) : "unresolved")} " +
                $"anchorWorld={DiagnosticFormatters.FormatVector3d(anchorPose.WorldPos)} " +
                $"seed-anchor={seedAnchorDelta} " +
                $"relativeOffset={DiagnosticFormatters.FormatVector3d(new Vector3d(relativeSeedPoint.latitude, relativeSeedPoint.longitude, relativeSeedPoint.altitude))} " +
                $"{DescribeVesselRootForDiagnostics("child", childVessel)} " +
                $"{DescribeVesselRootForDiagnostics("parent", parentVessel)}");
        }

        private static void LogFirstDebrisOrdinarySampleDiagnostics(
            BackgroundVesselState state,
            Recording treeRec,
            Vessel bgVessel,
            TrajectoryPoint absolutePoint,
            TrajectoryPoint recordedPoint,
            bool relativeApplied,
            double sampleUT)
        {
            if (state == null
                || treeRec == null
                || string.IsNullOrEmpty(treeRec.ParentAnchorRecordingId)
                || state.loggedFirstDebrisOrdinarySample)
            {
                return;
            }

            state.loggedFirstDebrisOrdinarySample = true;
            bool hasAbsoluteWorld = TryResolveTrajectoryPointWorldPosition(
                absolutePoint,
                bgVessel,
                out Vector3d absoluteWorld);
            Vector3d rootWorld = bgVessel?.rootPart?.transform != null
                ? (Vector3d)bgVessel.rootPart.transform.position
                : Vector3d.zero;
            bool hasRootWorld = bgVessel?.rootPart?.transform != null;
            string absoluteRootDelta = hasAbsoluteWorld && hasRootWorld
                ? DiagnosticFormatters.FormatVector3d(absoluteWorld - rootWorld)
                : "unresolved";
            string seedToSampleDt = IsFiniteUT(state.lastRecordedUT)
                ? (sampleUT - state.lastRecordedUT).ToString("F3", CultureInfo.InvariantCulture)
                : "n/a";

            ParsekLog.Verbose("BgRecorder",
                $"First debris ordinary sample diagnostics: pid={state.vesselPid} " +
                $"recId={treeRec.RecordingId} parentRecId={treeRec.ParentAnchorRecordingId} " +
                $"sampleUT={sampleUT.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"prevRecordedUT={(IsFiniteUT(state.lastRecordedUT) ? state.lastRecordedUT.ToString("F3", CultureInfo.InvariantCulture) : "n/a")} " +
                $"sample-prev-dt={seedToSampleDt} " +
                $"relativeApplied={(relativeApplied ? "T" : "F")} " +
                $"absolutePoint={DescribeTrajectoryPointForDiagnostics(absolutePoint)} " +
                $"recordedPoint={DescribeTrajectoryPointForDiagnostics(recordedPoint)} " +
                $"absoluteWorld={(hasAbsoluteWorld ? DiagnosticFormatters.FormatVector3d(absoluteWorld) : "unresolved")} " +
                $"rootWorld={(hasRootWorld ? DiagnosticFormatters.FormatVector3d(rootWorld) : "unresolved")} " +
                $"absolute-root={absoluteRootDelta} " +
                $"{DescribeVesselRootForDiagnostics("sample", bgVessel)}");
        }

        private static bool TryResolveTrajectoryPointWorldPosition(
            TrajectoryPoint point,
            Vessel contextVessel,
            out Vector3d worldPosition)
        {
            worldPosition = Vector3d.zero;
            CelestialBody body = contextVessel?.mainBody;
            if ((body == null || !string.Equals(body.name, point.bodyName, StringComparison.Ordinal))
                && FlightGlobals.Bodies != null)
            {
                body = FlightGlobals.Bodies.Find(b =>
                    b != null && string.Equals(b.name, point.bodyName, StringComparison.Ordinal));
            }

            if (body == null)
                return false;

            try
            {
                worldPosition = body.GetWorldSurfacePosition(
                    point.latitude,
                    point.longitude,
                    point.altitude);
                return true;
            }
            catch (Exception)
            {
                worldPosition = Vector3d.zero;
                return false;
            }
        }

        private static string DescribeVesselRootForDiagnostics(string label, Vessel vessel)
        {
            if (vessel == null)
                return label + "=null";

            Part rootPart = vessel.rootPart;
            string vesselWorld = SafeFormatVesselWorldPosition(vessel);
            if (rootPart == null)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}Vessel pid={1} name='{2}' vesselWorld={3} root=null",
                    label,
                    vessel.persistentId,
                    vessel.vesselName ?? "unknown",
                    vesselWorld);
            }

            string rootWorld = rootPart.transform != null
                ? DiagnosticFormatters.FormatVector3d(rootPart.transform.position)
                : "no-transform";
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}Vessel pid={1} name='{2}' vesselWorld={3} root='{4}' rootPid={5} rootWorld={6} srfAttach={7}",
                label,
                vessel.persistentId,
                vessel.vesselName ?? "unknown",
                vesselWorld,
                rootPart.partInfo?.name ?? rootPart.name ?? "unknown",
                rootPart.persistentId,
                rootWorld,
                DiagnosticFormatters.DescribeSurfaceAttachNode(rootPart));
        }

        private static string SafeFormatVesselWorldPosition(Vessel vessel)
        {
            try
            {
                return vessel != null
                    ? DiagnosticFormatters.FormatVector3d(vessel.GetWorldPos3D())
                    : "null";
            }
            catch (Exception)
            {
                // Diagnostics should not fail sampling if KSP cannot resolve the
                // world position during scene/load transitions.
                return "unresolved";
            }
        }

        private static string DescribeTrajectoryPointForDiagnostics(TrajectoryPoint point)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "ut={0:F3} body={1} lla=({2:F4},{3:F4},{4:F2}) rot={5} vel={6}",
                point.ut,
                point.bodyName ?? "unknown",
                point.latitude,
                point.longitude,
                point.altitude,
                DiagnosticFormatters.FormatQuaternion(point.rotation),
                DiagnosticFormatters.FormatVector3(point.velocity));
        }

        internal static bool ShouldPreferRootPartSurfacePoseForBackgroundSample(Recording treeRec)
        {
            // Parent-anchored debris snapshots and seed samples are root-part based.
            // Keep periodic background samples on the same pose contract so the ghost root
            // does not jump from a root-part seed to a vessel-origin ordinary sample.
            return treeRec != null && !string.IsNullOrEmpty(treeRec.ParentAnchorRecordingId);
        }

        internal static float ResolveBackgroundAttitudeMinSampleInterval(
            bool highFidelityActive,
            float effectiveMotionMinSampleInterval,
            float foregroundMinSampleInterval)
        {
            // highFidelityActive is already folded into
            // effectiveMotionMinSampleInterval by the caller; attitude uses the
            // more aggressive of motion cadence and foreground attitude floor.
            _ = highFidelityActive;
            // The two-arg helper currently returns the configured foreground
            // minimum unchanged; keep the call here so this background path stays
            // aligned if foreground cadence policy grows another override.
            float foregroundAttitudeMin = FlightRecorder.ResolveEffectiveMinSampleInterval(
                true,
                foregroundMinSampleInterval);
            return Math.Min(effectiveMotionMinSampleInterval, foregroundAttitudeMin);
        }

        internal static TrajectoryPoint CreateAbsoluteTrajectoryPointFromVessel(
            Vessel v,
            double ut,
            Vector3? explicitVelocity = null,
            bool preferRootPartSurfacePose = false)
        {
            Vector3 velocity = explicitVelocity ?? (v.packed
                ? (Vector3)v.obt_velocity
                : (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity()));

            double latitude = v.latitude;
            double longitude = v.longitude;
            double altitude = v.altitude;
            Quaternion rotation = v.srfRelRotation;
            string llaSource = "vessel-fields";
            if (preferRootPartSurfacePose)
            {
                llaSource = TryResolveRootPartSurfacePose(v, out latitude, out longitude, out altitude, out rotation)
                    ? "root-part"
                    : "root-part-fallback-vessel-fields";
            }
            else if (!v.packed && v.mainBody != null && v.transform != null)
            {
                // Packed/on-rails background vessels are positioned by orbit propagation,
                // not PhysX; keep those on the Vessel-field path.
                // Match FlightRecorder.BuildTrajectoryPoint for loaded vessels:
                // Vessel.latitude/longitude/altitude can lag transform.position
                // by one physics tick, which is visible right after separation.
                Vector3d freshWorldPos = v.transform.position;
                latitude = v.mainBody.GetLatitude(freshWorldPos);
                longitude = v.mainBody.GetLongitude(freshWorldPos);
                altitude = v.mainBody.GetAltitude(freshWorldPos);
                llaSource = "transform";
            }

            TrajectoryPoint pt = new TrajectoryPoint
            {
                ut = ut,
                latitude = latitude,
                longitude = longitude,
                altitude = altitude,
                rotation = rotation,
                velocity = velocity,
                bodyName = v.mainBody?.name ?? "Unknown",
                funds = Funding.Instance != null ? Funding.Instance.Funds : 0,
                science = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0,
                reputation = Reputation.Instance != null ? Reputation.CurrentRep : 0,
                // Phase 7: NaN sentinel for non-surface points.
                recordedGroundClearance = double.NaN
            };
            // ---- Trace-Sep: log every BG per-tick + structural-event sample ----
            if (v != null && (TraceSeparation.RecordingWindowActive || TraceSeparation.PlaybackWindowActive))
            {
                Vector3d worldPos = v.mainBody != null
                    ? v.mainBody.GetWorldSurfacePosition(pt.latitude, pt.longitude, pt.altitude)
                    : Vector3d.zero;
                Vector3d transformPos = v.transform != null ? (Vector3d)v.transform.position : Vector3d.zero;
                // tickSinceBreak parity with FG BuildTP. See
                // FlightRecorder.BuildTrajectoryPoint trace comment for the
                // diagnostic value of this field.
                double lastBreak = TraceSeparation.LastRecordingTriggerUT;
                double tickSinceBreak = double.NaN;
                if (!double.IsNaN(lastBreak))
                {
                    float dt = Time.fixedDeltaTime;
                    if (dt > 0f && !float.IsNaN(dt) && !float.IsInfinity(dt))
                        tickSinceBreak = (ut - lastBreak) / dt;
                }
                TraceSeparation.RecordLog("BG_CreateAbs",
                    "pid=" + v.persistentId +
                    " name='" + v.vesselName + "'" +
                    " ut=" + ut.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    " tickSinceBreak=" + (double.IsNaN(tickSinceBreak) ? "NaN" : tickSinceBreak.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)) +
                    " packed=" + v.packed +
                    " explicitVel=" + explicitVelocity.HasValue +
                    " preferRoot=" + preferRootPartSurfacePose +
                    " llaSource=" + llaSource +
                    " LLA=(" + pt.latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    "," + pt.longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    "," + pt.altitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")" +
                    " worldFromLLA=" + TraceSeparation.FormatVector3d(worldPos) +
                    " transformPos=" + TraceSeparation.FormatVector3d(transformPos) +
                    " transformVsLLAdelta=" + TraceSeparation.FormatVector3d(transformPos - worldPos) +
                    " |delta|=" + (transformPos - worldPos).magnitude.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    " velIn=" + TraceSeparation.FormatVector3(velocity) + " |v|=" + velocity.magnitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    " srfVel=" + TraceSeparation.FormatVector3(v.srf_velocity) + " |sv|=" + v.srf_velocity.magnitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
            // ---- /Trace-Sep ----
            return pt;
        }

        private static bool TryCanonicalizeBackgroundReFlyRecordingPoint(
            string recordingId,
            ref TrajectoryPoint point,
            string context)
        {
            return FlightRecorder.TryCanonicalizeReFlyRecordingPoint(
                recordingId,
                ref point,
                "BgRecorder",
                context);
        }

        private static bool TryCanonicalizeBackgroundReFlyRecordingOrbitSegment(
            string recordingId,
            Vessel vessel,
            double startUT,
            ref OrbitSegment segment,
            string context)
        {
            return FlightRecorder.TryCanonicalizeReFlyRecordingOrbitSegment(
                recordingId,
                vessel,
                startUT,
                ref segment,
                "BgRecorder",
                context);
        }

        private void FlushLoadedStateForOnRailsTransition(
            BackgroundVesselState loadedState,
            Recording flushRec,
            SegmentEnvironment nextEnv,
            bool willHavePlayableOnRailsPayload,
            TrajectoryPoint boundaryPoint,
            double ut)
        {
            TryCanonicalizeBackgroundReFlyRecordingPoint(
                loadedState.recordingId,
                ref boundaryPoint,
                "loaded-to-rails-boundary");
            SegmentEnvironment previousEnv = loadedState.trackSectionActive
                ? loadedState.currentTrackSection.environment
                : loadedState.environmentHysteresis != null
                    ? loadedState.environmentHysteresis.CurrentEnvironment
                    : SegmentEnvironment.Atmospheric;
            bool persistNoPayloadBoundarySection = ShouldPersistNoPayloadOnRailsBoundaryTrackSection(
                previousEnv, nextEnv, willHavePlayableOnRailsPayload);

            if (loadedState.isRelativeMode)
            {
                ParsekLog.Info("BgRecorder",
                    $"RELATIVE mode cleared on rails transition: pid={loadedState.vesselPid} " +
                    $"anchorRecordingId={loadedState.currentAnchorRecordingId ?? "(none)"} " +
                    $"diagnosticPid={loadedState.currentAnchorCandidate.DiagnosticPid}");
                ClearBackgroundCurrentAnchor(loadedState);
            }

            CloseBackgroundTrackSection(loadedState, ut);

            if (persistNoPayloadBoundarySection)
            {
                StartBackgroundTrackSection(loadedState, nextEnv, ReferenceFrame.Absolute, ut);
                AddFrameToActiveTrackSection(loadedState, boundaryPoint);
                // Mark the section as a recorder bookkeeping artifact: RecordingOptimizer
                // never treats boundaries on either side of a seam-flagged section as
                // split candidates (hard step-1 override, ahead of body / Surface /
                // ExoPropulsive short-circuits). See TrackSection.isBoundarySeam and
                // docs/dev/plans/optimizer-persistence-split.md §5.
                // TrackSection is a struct; mutate via field assignment on the held copy.
                loadedState.currentTrackSection.isBoundarySeam = true;
                CloseBackgroundTrackSection(loadedState, ut);
                // Read the flag from the closed section's held copy. CloseBackgroundTrackSection
                // doesn't zero `currentTrackSection`, so the flag survives. This phrasing keeps
                // the log honest if a future producer ever shares this code path with a non-seam
                // emit (today the persistNoPayloadBoundarySection branch always sets the flag).
                string seamSuffix = loadedState.currentTrackSection.isBoundarySeam ? " (seam=1)" : "";
                ParsekLog.Info("BgRecorder",
                    $"Persisted no-payload on-rails boundary section: pid={loadedState.vesselPid} " +
                    $"{previousEnv}->{nextEnv} at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}{seamSuffix}");
            }

            FlushTrackSectionsToRecording(loadedState, flushRec);
            flushRec.ExplicitEndUT = ut;

            if (!persistNoPayloadBoundarySection)
                AppendBoundaryPointToRecording(flushRec, boundaryPoint, loadedState.vesselPid);

            DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                flushRec,
                $"FlushLoadedStateForOnRailsTransition pid={loadedState.vesselPid}");
        }

        private void SampleBoundaryPoint(Vessel v, Recording treeRec, double ut)
        {
            if (v == null) return;

            TrajectoryPoint point = CreateAbsoluteTrajectoryPointFromVessel(v, ut);
            TryCanonicalizeBackgroundReFlyRecordingPoint(
                treeRec?.RecordingId,
                ref point,
                "boundary-sample");
            AppendBoundaryPointToRecording(treeRec, point, v.persistentId);
        }

        /// <summary>
        /// Handles a background vessel's environment transition: classifies the raw environment,
        /// feeds the hysteresis, and on a confirmed transition activates high-fidelity sampling,
        /// captures the boundary point, closes the current BG track section, opens a new Absolute
        /// section, and seeds the boundary point. Gated on a non-null hysteresis. Runs strictly
        /// below the packed/on-rails early-return in <see cref="OnBackgroundPhysicsFrame"/> (the
        /// eccentric-orbit invariant, §S16).
        /// </summary>
        private void HandleBackgroundEnvironmentTransition(
            BackgroundVesselState state, Vessel bgVessel, uint pid, double ut)
        {
            if (state.environmentHysteresis != null)
            {
                bool hasAtmo = bgVessel.mainBody != null && bgVessel.mainBody.atmosphere;
                double atmoDepth = hasAtmo ? bgVessel.mainBody.atmosphereDepth : 0;
                double approachAlt = (!hasAtmo && bgVessel.mainBody != null)
                    ? FlightRecorder.ComputeApproachAltitude(bgVessel.mainBody) : 0;
                var rawEnv = ClassifyBackgroundEnvironment(
                    hasAtmo, bgVessel.altitude, atmoDepth,
                    (int)bgVessel.situation, bgVessel.srfSpeed, state.cachedEngines, approachAlt,
                    bgVessel.isEVA, bgVessel.heightFromTerrain,
                    EnvironmentDetector.IsHeightFromTerrainValid(bgVessel.heightFromTerrain),
                    bgVessel.mainBody != null && bgVessel.mainBody.ocean);
                if (state.environmentHysteresis.Update(rawEnv, ut))
                {
                    var newEnv = state.environmentHysteresis.CurrentEnvironment;
                    ActivateBackgroundHighFidelitySampling(state, ut, "environment-transition");
                    ParsekLog.Info("BgRecorder",
                        $"Environment transition: pid={pid} -> {newEnv} " +
                        $"at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");

                    // Capture boundary point before closing (#283)
                    TrajectoryPoint? boundaryPoint = GetLastBackgroundFrame(state);
                    CloseBackgroundTrackSection(state, ut);
                    StartBackgroundTrackSection(state, newEnv, ReferenceFrame.Absolute, ut);
                    SeedBackgroundBoundaryPoint(state, boundaryPoint);
                }
            }
        }

        private void UpdateBackgroundAnchorDetection(
            BackgroundVesselState state,
            Vessel bgVessel,
            Recording treeRec,
            double ut)
        {
            if (state == null || bgVessel == null || treeRec == null)
                return;

            // When the recording carries the debris parent-anchor contract,
            // bypass the generic candidate-list / nearest-search entirely and
            // pin the anchor to the parent recording. The recorder still decides
            // Absolute-vs-Relative sections by parent proximity; the parent id is
            // ownership, not a generic live-anchor candidate.
            if (!string.IsNullOrEmpty(treeRec.ParentAnchorRecordingId))
            {
                ApplyParentAnchorContractToState(state, treeRec, bgVessel, ut);
                return;
            }

            bool onSurface = EnvironmentDetector.IsSurfaceForAnchorDetection(
                state.environmentHysteresis?.CurrentEnvironment,
                (int)bgVessel.situation);
            if (onSurface)
            {
                if (state.isRelativeMode)
                    ExitBackgroundRelativeMode(state, bgVessel, ut, "surface");
                return;
            }

            var candidates = BuildBackgroundRecordingAnchorCandidates(
                state,
                bgVessel,
                treeRec,
                ut,
                out int liveScanned,
                out int liveAdded,
                out int ghostScanned,
                out int ghostAdded);

            // Narrowed-gate filter for re-fly provisionals: mirror of the
            // FlightRecorder.UpdateAnchorDetection filter. Drop same-tree
            // candidates so the nearest-search picks only out-of-tree real
            // anchors.
            //
            // We use the pure overload with treeRec.RecordingId explicitly
            // (not the production overload's derived activeTree.ActiveRecordingId)
            // because tree.ActiveRecordingId is the FG-focused recording, and
            // BG recorders run only on unfocused vessels. Using the production
            // overload here would widen the scope to "any BG vessel in the
            // re-fly tree". Passing treeRec.RecordingId explicitly keeps the
            // scope at "this BG vessel IS the re-fly provisional": the filter
            // fires only if marker.ActiveReFlyRecordingId == treeRec.RecordingId.
            // See docs/dev/plans/narrow-refly-relative-gate.md.
            ReFlySessionMarker marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            ICollection<string> sameTreeRecordingIds = tree?.Recordings?.Keys;
            IReadOnlyList<RecordingAnchorCandidate> nearestSearchCandidates =
                ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional(
                    marker,
                    treeRec.RecordingId,
                    sameTreeRecordingIds,
                    candidates);

            var result = AnchorDetector.FindNearestRecordingAnchor(
                treeRec.RecordingId,
                state.vesselPid,
                bgVessel.GetWorldPos3D(),
                nearestSearchCandidates,
                AnchorDetector.RelativeFrameRangeLimit(state.isRelativeMode));
            bool shouldBeRelative = result.found
                && AnchorDetector.ShouldUseRelativeFrame(result.distance, state.isRelativeMode);

            if (shouldBeRelative)
            {
                bool anchorChanged = !state.isRelativeMode
                    || !string.Equals(
                        state.currentAnchorRecordingId,
                        result.candidate.RecordingId,
                        StringComparison.Ordinal);
                if (anchorChanged)
                {
                    string previousAnchorRecordingId = state.currentAnchorRecordingId;
                    uint previousDiagnosticPid = state.currentAnchorCandidate.DiagnosticPid;
                    SetBackgroundCurrentAnchor(state, result.candidate);
                    CloseBackgroundTrackSection(state, ut);
                    SegmentEnvironment env = state.environmentHysteresis != null
                        ? state.environmentHysteresis.CurrentEnvironment
                        : SegmentEnvironment.Atmospheric;
                    StartBackgroundTrackSection(state, env, ReferenceFrame.Relative, ut);
                    ApplyBackgroundCurrentAnchorToTrackSection(state);
                    ActivateBackgroundHighFidelitySampling(state, ut, "relative-enter");
                    if (!SeedBackgroundRelativeBoundaryPoint(state, bgVessel, result.candidate, ut))
                    {
                        ExitBackgroundRelativeMode(state, bgVessel, ut, "relative-boundary-seed-failed");
                        return;
                    }

                    var ic = CultureInfo.InvariantCulture;
                    string transition = previousAnchorRecordingId == null ? "entered" : "switched";
                    ParsekLog.Info("BgRecorder",
                        $"RELATIVE mode {transition}: pid={state.vesselPid} " +
                        $"recordingId={treeRec.RecordingId} anchorRecordingId={state.currentAnchorRecordingId} " +
                        $"source={result.candidate.Source} diagnosticPid={result.candidate.DiagnosticPid} " +
                        $"previousAnchorRecordingId={previousAnchorRecordingId ?? "(none)"} " +
                        $"previousDiagnosticPid={previousDiagnosticPid} " +
                        $"dist={result.distance.ToString("F1", ic)}m " +
                        $"sealed={result.candidate.IsSealed} " +
                        $"affinity={AnchorDetector.RecordingAnchorAffinityRank(result.candidate)} " +
                        $"liveCandidates={liveAdded}/{liveScanned} ghostCandidates={ghostAdded}/{ghostScanned}");
                }
                else
                {
                    SetBackgroundCurrentAnchor(state, result.candidate);
                    ApplyBackgroundCurrentAnchorToTrackSection(state);
                }
            }
            else if (state.isRelativeMode)
            {
                if (!result.found)
                {
                    ParsekLog.Info("BgRecorder",
                        $"RELATIVE exit: pid={state.vesselPid} no eligible recording anchor candidates " +
                        $"liveCandidates={liveAdded}/{liveScanned} ghostCandidates={ghostAdded}/{ghostScanned}");
                }
                ExitBackgroundRelativeMode(state, bgVessel, ut, "distance-or-missing-anchor");
            }
        }

        private IReadOnlyList<RecordingAnchorCandidate> BuildBackgroundRecordingAnchorCandidates(
            BackgroundVesselState state,
            Vessel bgVessel,
            Recording focusRecording,
            double ut,
            out int liveScanned,
            out int liveAdded,
            out int ghostScanned,
            out int ghostAdded)
        {
            backgroundAnchorCandidateBuffer.Clear();
            liveScanned = liveAdded = ghostScanned = ghostAdded = 0;
            if (tree?.Recordings == null || focusRecording == null)
                return backgroundAnchorCandidateBuffer;

            AddBackgroundLiveAnchorCandidates(
                state,
                bgVessel,
                focusRecording,
                out liveScanned,
                out liveAdded);
            AddBackgroundGhostAnchorCandidates(
                state,
                focusRecording,
                ut,
                out ghostScanned,
                out ghostAdded);

            ParsekLog.VerboseRateLimited("BgRecorder", "bg-recording-anchor-candidates",
                $"Background recording anchor candidates: pid={state.vesselPid} " +
                $"recordingId={focusRecording.RecordingId} live={liveAdded}/{liveScanned} " +
                $"ghost={ghostAdded}/{ghostScanned} total={backgroundAnchorCandidateBuffer.Count}",
                5.0);

            return backgroundAnchorCandidateBuffer;
        }

        private void AddBackgroundLiveAnchorCandidates(
            BackgroundVesselState state,
            Vessel bgVessel,
            Recording focusRecording,
            out int scanned,
            out int added)
        {
            scanned = added = 0;
            List<Vessel> vessels;
            try { vessels = FlightGlobals.Vessels; }
            catch (TypeInitializationException) { vessels = null; }
            if (vessels == null)
                return;

            // Resolve the active recording's vessel pid so a still-backgrounded
            // recording can anchor to the recording the player just switched to
            // (e.g. the docking target). The active recording lives outside
            // BackgroundMap, so without this it is invisible to the candidate
            // scan and the RELATIVE anchor silently drops to ABSOLUTE mid-
            // approach. DAG-order eligibility still decides whether the anchor
            // is actually allowed.
            string activeRecordingId = tree.ActiveRecordingId;
            uint activeRecordingVesselPid =
                !string.IsNullOrWhiteSpace(activeRecordingId)
                && tree.Recordings != null
                && tree.Recordings.TryGetValue(activeRecordingId, out Recording activeRec)
                && activeRec != null
                    ? activeRec.VesselPersistentId
                    : 0u;

            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel vessel = vessels[i];
                if (vessel == null || !vessel.loaded)
                    continue;
                if (vessel.persistentId == state.vesselPid)
                    continue;
                if (GhostMapPresence.IsGhostMapVessel(vessel.persistentId))
                    continue;
                if (vessel.persistentId == (bgVessel?.persistentId ?? 0u))
                    continue;

                scanned++;
                if (!AnchorDetector.TryResolveLoadedAnchorRecordingId(
                        vessel.persistentId,
                        tree.BackgroundMap,
                        activeRecordingId,
                        activeRecordingVesselPid,
                        out string recordingId,
                        out _))
                {
                    continue;
                }
                if (!TryGetBackgroundEligibleAnchorRecording(
                        recordingId,
                        focusRecording,
                        AnchorCandidateSource.Live,
                        vessel.persistentId,
                        out Recording candidateRecording))
                {
                    continue;
                }

                // Use vesselTransform.position to stay symmetric with
                // vessel.transform.rotation -- mixing GetWorldPos3D (CoM) with
                // vesselTransform rotation systematically shifts the recorded
                // offset by the CoM-to-vesselTransform vector (the v0.9 radial-
                // booster ~10 m forward bug). Falls back to CoM only when the
                // Unity transform is unavailable (mid-teardown fake-null).
                Transform candidateTransform = vessel.transform;
                Vector3d candidatePos = candidateTransform != null
                    ? (Vector3d)candidateTransform.position
                    : vessel.GetWorldPos3D();
                Quaternion candidateRot = candidateTransform != null
                    ? candidateTransform.rotation
                    : Quaternion.identity;
                if (AnchorDetector.TryCreateRecordingAnchorCandidate(
                        focusRecording,
                        candidateRecording,
                        candidatePos,
                        candidateRot,
                        AnchorCandidateSource.Live,
                        vessel.persistentId,
                        -1,
                        out RecordingAnchorCandidate candidate))
                {
                    backgroundAnchorCandidateBuffer.Add(candidate);
                    added++;
                }
            }
        }

        private void AddBackgroundGhostAnchorCandidates(
            BackgroundVesselState state,
            Recording focusRecording,
            double ut,
            out int scanned,
            out int added)
        {
            scanned = added = 0;
            if (ParsekFlight.Instance == null)
                return;

            IEnumerable<RecordingAnchorCandidate> candidates =
                ParsekFlight.Instance.BuildGhostRecordingAnchorCandidatesForRecorder(
                    tree,
                    focusRecording.RecordingId,
                    state.vesselPid,
                    ut);
            if (candidates == null)
                return;

            foreach (RecordingAnchorCandidate provided in candidates)
            {
                scanned++;
                if (!TryGetBackgroundEligibleAnchorRecording(
                        provided.RecordingId,
                        focusRecording,
                        provided.Source,
                        provided.DiagnosticPid,
                        out Recording candidateRecording))
                {
                    continue;
                }

                if (AnchorDetector.TryCreateRecordingAnchorCandidate(
                        focusRecording,
                        candidateRecording,
                        provided.WorldPos,
                        provided.WorldRotation,
                        provided.Source,
                        provided.DiagnosticPid,
                        provided.GhostIndex,
                        out RecordingAnchorCandidate candidate))
                {
                    backgroundAnchorCandidateBuffer.Add(candidate);
                    added++;
                }
            }
        }

        private bool TryGetBackgroundEligibleAnchorRecording(
            string recordingId,
            Recording focusRecording,
            AnchorCandidateSource source,
            uint diagnosticPid,
            out Recording candidateRecording)
        {
            candidateRecording = null;
            if (tree?.Recordings == null || string.IsNullOrWhiteSpace(recordingId))
                return false;
            // NOTE: the active recording is intentionally NOT blanket-excluded
            // here. A still-backgrounded recording must be able to anchor to the
            // recording the player just switched to (the docking target the
            // player is now flying) to keep RELATIVE engaged through the close
            // approach. Cycle safety is provided by IsRecordingAnchorDAGOrderEligible
            // (candidate.TreeOrder < focus.TreeOrder) below: the active recording
            // can only anchor a higher-TreeOrder background recording, never the
            // reverse, so no A<->B anchor cycle can form. The re-fly provisional
            // (still-being-appended fork) is still excluded by the marker gate.
            ReFlySessionMarker marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            if (marker != null
                && !string.IsNullOrEmpty(marker.ActiveReFlyRecordingId)
                && string.Equals(marker.ActiveReFlyRecordingId, recordingId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!tree.Recordings.TryGetValue(recordingId, out candidateRecording))
                return false;

            if (!AnchorDetector.IsRecordingAnchorEligible(focusRecording, candidateRecording))
                return false;
            if (!AnchorDetector.IsRecordingAnchorDAGOrderEligible(focusRecording, candidateRecording))
            {
                LogBackgroundRecordingAnchorDagOrderSkip(focusRecording, candidateRecording, source, diagnosticPid);
                return false;
            }

            return true;
        }

        private static void LogBackgroundRecordingAnchorDagOrderSkip(
            Recording focusRecording,
            Recording candidateRecording,
            AnchorCandidateSource source,
            uint diagnosticPid)
        {
            string focusId = focusRecording?.RecordingId ?? "(none)";
            string candidateId = candidateRecording?.RecordingId ?? "(none)";
            ParsekLog.VerboseRateLimited("BgRecorder",
                "bg-recording-anchor-dag-order-skip|" + focusId + "|" + candidateId + "|" + source,
                $"Background recording anchor candidate skipped: reason=dag-order focusRecordingId={focusId} " +
                $"candidateRecordingId={candidateId} focusTreeOrder={focusRecording?.TreeOrder ?? -1} " +
                $"candidateTreeOrder={candidateRecording?.TreeOrder ?? -1} source={source} diagnosticPid={diagnosticPid}",
                5.0);
        }

        private static void SetBackgroundCurrentAnchor(
            BackgroundVesselState state,
            RecordingAnchorCandidate candidate)
        {
            state.isRelativeMode = true;
            state.currentAnchorRecordingId = candidate.RecordingId;
            state.currentAnchorCandidate = candidate;
            state.hasCurrentAnchorCandidate = true;
        }

        /// <summary>
        /// PR 3b (Decision §5 + plan §3b §"Per-frame anchor write" / §"Structural-event
        /// seam"): pins <paramref name="state"/>'s anchor to the parent recording id
        /// carried by the <see cref="Recording.ParentAnchorRecordingId"/> contract.
        /// Skips the candidate-list / nearest-search and lets
        /// <see cref="ApplyBackgroundCurrentAnchorToTrackSection"/> propagate the anchor
        /// id into <see cref="TrackSection.anchorRecordingId"/>. Builds a Live anchor
        /// candidate from the parent vessel when available; falls back to a record-only
        /// candidate that resolves through the recorded-anchor pose path.
        /// </summary>
        private void ApplyParentAnchorContractToState(
            BackgroundVesselState state,
            Recording treeRec,
            Vessel bgVessel,
            double ut)
        {
            if (state == null || treeRec == null) return;
            string parentRecId = treeRec.ParentAnchorRecordingId;
            if (string.IsNullOrEmpty(parentRecId)) return;

            if (!ShouldUseDebrisRelativeSection(state))
            {
                if (state.isRelativeMode
                    || (state.trackSectionActive
                        && state.currentTrackSection.referenceFrame == ReferenceFrame.Relative))
                {
                    ExitBackgroundRelativeMode(
                        state,
                        bgVessel,
                        ut,
                        "debris-parent-proximity-exit");
                }
                else
                {
                    ClearBackgroundCurrentAnchor(state);
                }
                return;
            }

            string previousAnchorRecordingId = state.currentAnchorRecordingId;
            bool wasRelative = state.isRelativeMode;
            var marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            bool markerActive = marker != null && !string.IsNullOrEmpty(marker.ActiveReFlyRecordingId);
            bool mutableActiveReFlyAnchor = IsActiveReFlyParentMatch(marker, parentRecId);

            // Resolve the parent vessel by its persistent id so we can author a Live
            // anchor candidate when the parent is loaded. The recorded-anchor pose
            // path is a graceful fallback when the parent vessel is gone (the
            // CheckDebrisTTL hook ends the recording shortly after this).
            Recording parentRec = null;
            tree?.Recordings?.TryGetValue(parentRecId, out parentRec);
            Vessel parentVessel = parentRec != null
                ? FlightRecorder.FindVesselByPid(parentRec.VesselPersistentId)
                : null;

            if (parentVessel != null && parentVessel.loaded)
            {
                // Use vesselTransform.position (NOT GetWorldPos3D / CoM) to stay
                // symmetric with parentVessel.transform.rotation and with the
                // recorder's seed-time anchor capture (which writes
                // v.latitude/longitude/altitude derived from vesselTransform via
                // KSP's UpdatePosVel). Mixing CoM position with vesselTransform
                // rotation was the v0.9 radial-booster ~10 m forward offset bug
                // -- body-fixed primary masks the inconsistency for normal
                // rendering, but the live candidate also leaks into diagnostics,
                // fallback paths, and future anchor-based render logic. Falls
                // back to CoM when transform is null (mid-teardown fake-null).
                Transform parentTransform = parentVessel.transform;
                Vector3d parentAnchorPos = parentTransform != null
                    ? (Vector3d)parentTransform.position
                    : parentVessel.GetWorldPos3D();
                Quaternion parentAnchorRot = parentTransform != null
                    ? parentTransform.rotation
                    : Quaternion.identity;
                var liveCandidate = new RecordingAnchorCandidate(
                    parentRecId,
                    parentAnchorPos,
                    parentAnchorRot,
                    AnchorCandidateSource.Live,
                    diagnosticPid: parentVessel.persistentId,
                    ghostIndex: -1,
                    isSealed: false,
                    isSameReplayPoint: false,
                    isSameVesselLineage: false);
                SetBackgroundCurrentAnchor(state, liveCandidate);
            }
            else
            {
                // Parent not in scene as a live vessel — pin the recording id; pose
                // resolution falls through to TryResolveBackgroundRecordedAnchorPose
                // (recorded-data path) which the resolver chain handles.
                state.isRelativeMode = true;
                state.currentAnchorRecordingId = parentRecId;
                state.currentAnchorCandidate = default;
                state.hasCurrentAnchorCandidate = false;
            }

            // If we just transitioned into the relative surface or swapped anchors, close
            // the Absolute section we opened (or re-open) and start a Relative one.
            // The contract is proximity-scoped: parent-local Relative sections are
            // only opened while the parent remains in the debris relative range.
            bool needsSectionFlip = state.trackSectionActive
                && state.currentTrackSection.referenceFrame != ReferenceFrame.Relative;
            bool anchorChanged = !wasRelative
                || !string.Equals(previousAnchorRecordingId, parentRecId, StringComparison.Ordinal);
            if (needsSectionFlip || anchorChanged)
            {
                SegmentEnvironment env = state.environmentHysteresis != null
                    ? state.environmentHysteresis.CurrentEnvironment
                    : SegmentEnvironment.Atmospheric;
                StartDebrisParentRelativeTrackSection(state, env, ut);
                if (anchorChanged)
                {
                    string transition = previousAnchorRecordingId == null ? "entered" : "switched";
                    ParsekLog.Info("BgRecorder",
                        $"RELATIVE mode {transition} (debris parent-anchor contract): pid={state.vesselPid} " +
                        $"recordingId={treeRec.RecordingId} parentRecId={parentRecId} " +
                        $"previousAnchorRecordingId={previousAnchorRecordingId ?? "(none)"} " +
                        $"parentVesselLoaded={parentVessel != null && parentVessel.loaded} " +
                        $"mutableActiveReFlyAnchor={(mutableActiveReFlyAnchor ? "true" : "false")} " +
                        $"markerActive={(markerActive ? "true" : "false")} " +
                        $"markerActiveReFlyId={marker?.ActiveReFlyRecordingId ?? "(none)"} " +
                        $"ut={ut.ToString("F2", CultureInfo.InvariantCulture)}");
                }
            }
            else
            {
                ApplyBackgroundCurrentAnchorToTrackSection(state);
            }
        }

        private void StartDebrisParentRelativeTrackSection(
            BackgroundVesselState state,
            SegmentEnvironment env,
            double ut)
        {
            if (state.trackSectionActive)
                CloseBackgroundTrackSection(state, ut);
            StartBackgroundTrackSection(state, env, ReferenceFrame.Relative, ut);
            ApplyBackgroundCurrentAnchorToTrackSection(state);
            ForceNextBackgroundTrajectorySample(
                state,
                ut,
                "debris-parent-relative-enter");
        }

        private static void ForceNextBackgroundTrajectorySample(
            BackgroundVesselState state,
            double ut,
            string reason)
        {
            if (state == null)
                return;

            // TrajectoryMath.ShouldRecordPoint treats negative lastRecordedUT as
            // the first-sample case, bypassing min/max interval gates.
            state.lastRecordedUT = -1.0;
            state.hasLastWorldRotation = false;
            ActivateBackgroundHighFidelitySampling(
                state,
                ut,
                reason ?? "force-immediate-sample");
            ParsekLog.Verbose("BgRecorder",
                $"Immediate background trajectory sample forced: pid={state.vesselPid} " +
                $"reason={reason ?? "unknown"} ut={ut.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        private static void ClearBackgroundCurrentAnchor(BackgroundVesselState state)
        {
            state.isRelativeMode = false;
            state.currentAnchorRecordingId = null;
            state.currentAnchorCandidate = default;
            state.hasCurrentAnchorCandidate = false;
        }

        private static void ApplyBackgroundCurrentAnchorToTrackSection(BackgroundVesselState state)
        {
            if (state == null
                || !state.trackSectionActive
                || state.currentTrackSection.referenceFrame != ReferenceFrame.Relative)
            {
                return;
            }

            state.currentTrackSection.anchorRecordingId = state.currentAnchorRecordingId;
            state.currentTrackSection.anchorVesselId = 0u;
        }

        private bool ApplyBackgroundRelativeOffset(
            BackgroundVesselState state,
            ref TrajectoryPoint point,
            Vessel vessel,
            double ut)
        {
            if (state == null) return false;

            // PR 3b §"Structural-event seam": the structural-event snapshot path
            // bypasses UpdateBackgroundAnchorDetection, so the debris contract must
            // be enforced here too. For parent-anchored debris, pin state to the parent
            // recording id before any pose resolution. Re-runs of the periodic path
            // hit the same helper but it's idempotent when the contract is already
            // applied (anchorChanged=false → no log spam, no section flip).
            Recording treeRec = null;
            if (!string.IsNullOrEmpty(state.recordingId))
                tree?.Recordings?.TryGetValue(state.recordingId, out treeRec);
            if (treeRec != null && !string.IsNullOrEmpty(treeRec.ParentAnchorRecordingId))
            {
                UpdateDebrisProximityState(state, treeRec, vessel);
                ApplyParentAnchorContractToState(state, treeRec, vessel, ut);
            }

            if (!state.isRelativeMode)
                return false;
            if (string.IsNullOrWhiteSpace(state.currentAnchorRecordingId))
            {
                ForceBackgroundRelativeToAbsolute(state, ut, "anchor-recording-id-missing");
                return false;
            }

            if (!TryResolveBackgroundCurrentAnchorPose(
                    state,
                    ut,
                    out AnchorPose anchorPose,
                    out RelativeAnchorResolveFailure failure,
                    out string anchorPoseSourceLabel))
            {
                string reason = RelativeAnchorResolveFailure.ReasonOrFallback(
                    failure,
                    "recorded-anchor-unresolved");
                ParsekLog.Warn("BgRecorder",
                    $"RELATIVE sample unresolved: pid={state.vesselPid} " +
                    $"anchorRecordingId={state.currentAnchorRecordingId} reason={reason} " +
                    $"ut={ut.ToString("F2", CultureInfo.InvariantCulture)} — forcing ABSOLUTE");
                ForceBackgroundRelativeToAbsolute(state, ut, reason);
                return false;
            }

            return ApplyBackgroundRelativeOffsetForAnchorPose(
                state,
                ref point,
                vessel,
                anchorPose,
                state.currentAnchorRecordingId,
                state.currentAnchorCandidate.Source,
                state.currentAnchorCandidate.DiagnosticPid,
                logSample: true,
                sourceLabel: anchorPoseSourceLabel);
        }

        private bool SeedBackgroundRelativeBoundaryPoint(
            BackgroundVesselState state,
            Vessel vessel,
            RecordingAnchorCandidate candidate,
            double ut)
        {
            if (!TryResolveBackgroundAnchorPoseForCandidate(
                    candidate,
                    ut,
                    out AnchorPose anchorPose,
                    out RelativeAnchorResolveFailure failure))
            {
                string reason = RelativeAnchorResolveFailure.ReasonOrFallback(
                    failure,
                    "recorded-anchor-unresolved");
                ParsekLog.Warn("BgRecorder",
                    $"RELATIVE boundary seed skipped: pid={state.vesselPid} " +
                    $"anchorRecordingId={candidate.RecordingId} reason={reason}");
                return false;
            }

            TrajectoryPoint absolutePoint = CreateAbsoluteTrajectoryPointFromVessel(vessel, ut);
            TryCanonicalizeBackgroundReFlyRecordingPoint(
                state.recordingId,
                ref absolutePoint,
                "relative-boundary");
            TrajectoryPoint relativePoint = absolutePoint;
            if (!ApplyBackgroundRelativeOffsetForAnchorPose(
                    state,
                    ref relativePoint,
                    vessel,
                    anchorPose,
                    candidate.RecordingId,
                    candidate.Source,
                    candidate.DiagnosticPid,
                    logSample: false))
            {
                return false;
            }

            // Apply v9 surface clearance to both stores. The in-frames
            // relativePoint goes into a Relative section so the helper's
            // frame gate correctly skips it (anchor-local metres in
            // lat/lon/alt would corrupt terrain math), but the body-fixed
            // shadow carries genuine lat/lon/alt and the body-fixed
            // primary playback path applies terrain correction via
            // recordedGroundClearance. Mirrors the periodic-sample,
            // structural-event, and debris-seed wirings.
            ApplySurfaceClearanceToPoint(state, vessel, ref relativePoint);
            ApplySurfaceClearanceToBodyFixedShadow(state, vessel, ref absolutePoint);

            AddFrameToActiveTrackSection(state, relativePoint, absolutePoint);
            ParsekLog.Verbose("BgRecorder",
                $"RELATIVE boundary seeded: pid={state.vesselPid} " +
                $"anchorRecordingId={candidate.RecordingId} source={candidate.Source} " +
                $"diagnosticPid={candidate.DiagnosticPid} ut={ut.ToString("F2", CultureInfo.InvariantCulture)}");
            return true;
        }

        private bool ApplyBackgroundRelativeOffsetForAnchorPose(
            BackgroundVesselState state,
            ref TrajectoryPoint point,
            Vessel vessel,
            AnchorPose anchorPose,
            string anchorRecordingId,
            AnchorCandidateSource source,
            uint diagnosticPid,
            bool logSample,
            string sourceLabel = null)
        {
            if (state == null || vessel == null || string.IsNullOrWhiteSpace(anchorRecordingId))
                return false;

            int recordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            if (!FlightRecorder.TryResolveAbsolutePointWorldForRelativeOffset(
                    point,
                    vessel,
                    out Vector3d focusWorldPos))
            {
                // Fall back to vesselTransform.position (NOT GetWorldPos3D / CoM)
                // to stay symmetric with the rotation fallback below at
                // vessel.transform.rotation. Both pieces must come from the same
                // frame or the recorded offset is shifted by the CoM-to-
                // vesselTransform vector. CoM remains as the last-resort fallback
                // if the Unity transform is mid-teardown (fake-null).
                Transform focusTransform = vessel.transform;
                focusWorldPos = focusTransform != null
                    ? (Vector3d)focusTransform.position
                    : vessel.GetWorldPos3D();
            }
            Vector3d offset = TrajectoryMath.ComputeRelativeLocalOffset(
                focusWorldPos,
                anchorPose.WorldPos,
                anchorPose.WorldRotation);
            point.latitude = offset.x;
            point.longitude = offset.y;
            point.altitude = offset.z;
            Quaternion focusWorldRotation;
            if (!FlightRecorder.TryResolveAbsolutePointWorldRotationForRelativeOffset(
                    point,
                    vessel,
                    out focusWorldRotation))
            {
                focusWorldRotation = vessel.transform.rotation;
            }
            point.rotation = TrajectoryMath.ComputeRelativeLocalRotation(
                focusWorldRotation,
                anchorPose.WorldRotation);

            ApplyBackgroundCurrentAnchorToTrackSection(state);

            // ---- Trace-Sep: log both parent-vs-debris distance measurements ----
            // recorded-relative = |offset|, the magnitude of the anchor-local
            // Cartesian offset just written into frames[].latitude/longitude/altitude.
            // recorded-absolute = |focusWorldPos - anchorPose.WorldPos|, the
            // ground-truth world-space distance between the live debris and
            // the live anchor at this same instant. These two MUST match
            // exactly (the relative offset only rotates the world vector into
            // the anchor's frame, magnitude is preserved). A mismatch here is
            // a smoking gun for ComputeRelativeLocalOffset doing more than
            // rotation (scale, axis-swap, mis-applied anchor rotation, etc.)
            // and would mean the recorded anchor-local distance does not
            // correspond to the real ground-truth distance.
            if (TraceSeparation.RecordingWindowActive || TraceSeparation.PlaybackWindowActive)
            {
                Vector3d worldDelta = focusWorldPos - anchorPose.WorldPos;
                double recordedRelativeDist = offset.magnitude;
                double recordedAbsoluteDist = worldDelta.magnitude;
                TraceSeparation.RecordLog("BG_ApplyRel",
                    "vesselPid=" + state.vesselPid +
                    " anchorRecId=" + (anchorRecordingId ?? "<null>") +
                    " ut=" + point.ut.ToString("R", CultureInfo.InvariantCulture) +
                    " focusWorldPos=" + TraceSeparation.FormatVector3d(focusWorldPos) +
                    " anchorWorldPos=" + TraceSeparation.FormatVector3d(anchorPose.WorldPos) +
                    " worldDelta=" + TraceSeparation.FormatVector3d(worldDelta) +
                    " offset=" + TraceSeparation.FormatVector3d(offset) +
                    " recordedRelativeDist=" + recordedRelativeDist.ToString("F3", CultureInfo.InvariantCulture) +
                    " recordedAbsoluteDist=" + recordedAbsoluteDist.ToString("F3", CultureInfo.InvariantCulture) +
                    " distMismatch=" + System.Math.Abs(recordedRelativeDist - recordedAbsoluteDist).ToString("F3", CultureInfo.InvariantCulture));
            }
            // ---- /Trace-Sep ----

            if (logSample)
            {
                double sampleUT = point.ut;
                ParsekLog.VerboseRateLimited("BgRecorder",
                    "bg-relative-offset|" + state.vesselPid,
                    () =>
                    {
                        string effectiveSource = sourceLabel
                            ?? (state.hasCurrentAnchorCandidate ? source.ToString() : "recorded");
                        string frameContract = ClassifyBackgroundRelativeFrameContract(
                            anchorRecordingId,
                            effectiveSource,
                            source,
                            state.hasCurrentAnchorCandidate,
                            HasActiveReFlyPreReFlyAnchorSnapshot(anchorRecordingId));
                        string parentDrift = FormatActiveReFlyParentDriftFromRecorded(
                            anchorRecordingId,
                            diagnosticPid,
                            sampleUT);
                        string parentDriftField = FormatParentDriftLogField(parentDrift);

                        return $"RELATIVE sample: pid={state.vesselPid} " +
                               $"contract=anchor-local " +
                               $"version={recordingFormatVersion} dx={offset.x:F2} dy={offset.y:F2} dz={offset.z:F2} " +
                               $"anchorRecordingId={anchorRecordingId} source={effectiveSource} " +
                               $"frameContract={frameContract} " +
                               parentDriftField +
                               $"diagnosticPid={diagnosticPid} " +
                               $"|offset|={offset.magnitude:F2}m";
                    },
                    2.0);
            }

            return true;
        }

        private bool TryResolveBackgroundCurrentAnchorPose(
            BackgroundVesselState state,
            double ut,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure,
            out string sourceLabel)
        {
            sourceLabel = null;
            if (state.hasCurrentAnchorCandidate
                && string.Equals(
                    state.currentAnchorCandidate.RecordingId,
                    state.currentAnchorRecordingId,
                    StringComparison.Ordinal))
            {
                return TryResolveBackgroundAnchorPoseForCandidate(
                    state.currentAnchorCandidate,
                    ut,
                    out pose,
                    out failure,
                    out sourceLabel);
            }

            bool resolved = TryResolveBackgroundRecordedAnchorPose(
                state.currentAnchorRecordingId,
                ut,
                out pose,
                out failure);
            if (resolved)
                sourceLabel = "recorded";
            return resolved;
        }

        private bool TryResolveBackgroundAnchorPoseForCandidate(
            RecordingAnchorCandidate candidate,
            double ut,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            return TryResolveBackgroundAnchorPoseForCandidate(
                candidate,
                ut,
                out pose,
                out failure,
                out _);
        }

        private bool TryResolveBackgroundAnchorPoseForCandidate(
            RecordingAnchorCandidate candidate,
            double ut,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure,
            out string sourceLabel)
        {
            pose = default;
            failure = default;
            sourceLabel = null;
            if (string.IsNullOrWhiteSpace(candidate.RecordingId))
            {
                failure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.PreconditionFailed,
                    "anchor-recording-id-missing",
                    null,
                    candidate.RecordingId,
                    ut);
                return false;
            }

            if (candidate.Source == AnchorCandidateSource.Live && candidate.DiagnosticPid != 0u)
            {
                Vessel liveAnchor = FlightRecorder.FindVesselByPid(candidate.DiagnosticPid);
                if (liveAnchor != null && liveAnchor.loaded)
                {
                    // Match the recorded surface-pose frame: KSP's UpdatePosVel writes
                    // Vessel.latitude/longitude/altitude from vesselTransform.position
                    // and srfRelRotation from vesselTransform.rotation, so playback
                    // (body.GetWorldSurfacePosition of the recorded lla) reconstructs
                    // a vesselTransform-aligned anchor world position. The previous
                    // GetWorldPos3D (CoM) source baked the parent's CoM-to-vesselTransform
                    // vector into every relative ordinary offset. Use vessel.transform
                    // explicitly, not GetTransform() — the latter follows
                    // ReferenceTransform ("Control From Here") which is not the surface
                    // pose contract.
                    pose = new AnchorPose(
                        (Vector3d)liveAnchor.transform.position,
                        liveAnchor.transform.rotation,
                        -1,
                        candidate.RecordingId);
                    sourceLabel = "live";
                    return true;
                }

                ParsekLog.WarnRateLimited("BgRecorder",
                    "bg-live-recording-anchor-missing|" + candidate.RecordingId,
                    $"Live background anchor candidate missing; falling back to recorded pose " +
                    $"anchorRecordingId={candidate.RecordingId} diagnosticPid={candidate.DiagnosticPid} " +
                    $"ut={ut.ToString("F2", CultureInfo.InvariantCulture)}",
                    5.0);
            }

            bool resolved = TryResolveBackgroundRecordedAnchorPose(candidate.RecordingId, ut, out pose, out failure);
            if (resolved)
                sourceLabel = "recorded";
            return resolved;
        }

        private bool TryResolveBackgroundRecordedAnchorPose(
            string anchorRecordingId,
            double ut,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            failure = default;
            if (string.IsNullOrWhiteSpace(anchorRecordingId))
            {
                pose = default;
                failure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.PreconditionFailed,
                    "anchor-recording-id-missing",
                    null,
                    anchorRecordingId,
                    ut);
                return false;
            }

            var context = new RelativeAnchorResolverContext(
                tree,
                focusRecordingId: null,
                focusTreeId: tree?.Id,
                activeReFlyMarker: ParsekScenario.Instance?.ActiveReFlySessionMarker,
                pendingTree: RecordingStore.HasPendingTree ? RecordingStore.PendingTree : null,
                absoluteWorldPositionResolver: ResolveBackgroundPointWorldPosition,
                bodyWorldRotationResolver: ResolveBackgroundBodyWorldRotation,
                orbitalCheckpointPoseResolver: TryResolveBackgroundOrbitalAnchorPose);

            HashSet<string> visited = backgroundAnchorVisitedRecordingIds;
            visited.Clear();
            bool resolved;
            try
            {
                resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                    context,
                    anchorRecordingId,
                    ut,
                    visited,
                    out pose,
                    out failure);
            }
            finally
            {
                visited.Clear();
            }

            if (!resolved && !failure.HasFailure)
            {
                failure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.Other,
                    "recorded-anchor-unresolved",
                    null,
                    anchorRecordingId,
                    ut);
            }
            return resolved;
        }

        private static Vector3d ResolveBackgroundPointWorldPosition(TrajectoryPoint point)
        {
            CelestialBody body = FindBackgroundBody(point.bodyName);
            if (body == null)
                return new Vector3d(double.NaN, double.NaN, double.NaN);
            return body.GetWorldSurfacePosition(point.latitude, point.longitude, point.altitude);
        }

        private static Quaternion ResolveBackgroundBodyWorldRotation(TrajectoryPoint point)
        {
            CelestialBody body = FindBackgroundBody(point.bodyName);
            return body != null && body.bodyTransform != null
                ? body.bodyTransform.rotation
                : Quaternion.identity;
        }

        private static bool TryResolveBackgroundOrbitalAnchorPose(
            Recording recording,
            TrackSection section,
            int sectionIndex,
            double ut,
            out AnchorPose pose)
        {
            pose = default;
            if (section.checkpoints == null || section.checkpoints.Count == 0)
                return false;

            for (int i = 0; i < section.checkpoints.Count; i++)
            {
                OrbitSegment segment = section.checkpoints[i];
                if (ut < segment.startUT || ut > segment.endUT)
                    continue;

                CelestialBody body = FindBackgroundBody(segment.bodyName);
                if (body == null)
                    return false;

                Orbit orbit = new Orbit(
                    segment.inclination,
                    segment.eccentricity,
                    segment.semiMajorAxis,
                    segment.longitudeOfAscendingNode,
                    segment.argumentOfPeriapsis,
                    segment.meanAnomalyAtEpoch,
                    segment.epoch,
                    body);
                Vector3d worldPos = orbit.getPositionAtUT(ut);
                Vector3d velocity = orbit.getOrbitalVelocityAtUT(ut);
                var rotation = ParsekFlight.ComputeOrbitalRotation(
                    segment,
                    orbit,
                    ut,
                    velocity,
                    worldPos,
                    body.position,
                    Quaternion.identity,
                    sectionIndex,
                    TrajectoryMath.HasOrbitalFrameRotation(segment),
                    TrajectoryMath.IsSpinning(segment));

                pose = new AnchorPose(worldPos, rotation.ghostRot, sectionIndex, recording?.RecordingId);
                return true;
            }

            return false;
        }

        private static CelestialBody FindBackgroundBody(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName))
                return null;
            try
            {
                return FlightGlobals.Bodies?.Find(b => b != null && b.name == bodyName);
            }
            catch (TypeInitializationException)
            {
                return null;
            }
        }

        private void ExitBackgroundRelativeMode(
            BackgroundVesselState state,
            Vessel vessel,
            double ut,
            string reason)
        {
            TrajectoryPoint boundaryPoint = CreateAbsoluteTrajectoryPointFromVessel(vessel, ut);
            TryCanonicalizeBackgroundReFlyRecordingPoint(
                state.recordingId,
                ref boundaryPoint,
                "relative-exit");
            string oldAnchorRecordingId = state.currentAnchorRecordingId;
            uint oldDiagnosticPid = state.currentAnchorCandidate.DiagnosticPid;
            ClearBackgroundCurrentAnchor(state);
            CloseBackgroundTrackSection(state, ut);
            SegmentEnvironment env = state.environmentHysteresis != null
                ? state.environmentHysteresis.CurrentEnvironment
                : SegmentEnvironment.Atmospheric;
            StartBackgroundTrackSection(state, env, ReferenceFrame.Absolute, ut);
            ActivateBackgroundHighFidelitySampling(state, ut, $"relative-exit-{reason ?? "unknown"}");
            SeedBackgroundBoundaryPoint(state, boundaryPoint);
            ParsekLog.Info("BgRecorder",
                $"RELATIVE mode exited: pid={state.vesselPid} " +
                $"previousAnchorRecordingId={oldAnchorRecordingId ?? "(none)"} " +
                $"diagnosticPid={oldDiagnosticPid} reason={reason}");
        }

        private void ForceBackgroundRelativeToAbsolute(
            BackgroundVesselState state,
            double ut,
            string reason)
        {
            string oldAnchorRecordingId = state.currentAnchorRecordingId;
            uint oldDiagnosticPid = state.currentAnchorCandidate.DiagnosticPid;
            ClearBackgroundCurrentAnchor(state);
            CloseBackgroundTrackSection(state, ut);
            SegmentEnvironment env = state.environmentHysteresis != null
                ? state.environmentHysteresis.CurrentEnvironment
                : SegmentEnvironment.Atmospheric;
            StartBackgroundTrackSection(state, env, ReferenceFrame.Absolute, ut);
            ActivateBackgroundHighFidelitySampling(state, ut, $"relative-force-exit-{reason ?? "unknown"}");
            ParsekLog.Info("BgRecorder",
                $"RELATIVE mode force-exited: pid={state.vesselPid} " +
                $"anchorRecordingId={oldAnchorRecordingId ?? "(none)"} " +
                $"diagnosticPid={oldDiagnosticPid} reason={reason} new ABSOLUTE section started");
        }

        /// <summary>
        /// Computes the distance in meters between a background vessel and the focused vessel.
        /// Returns double.MaxValue if there is no focused vessel (defensive).
        /// Uses an injectable override for testing; falls back to FlightGlobals.ActiveVessel.
        /// </summary>
        internal double ComputeDistanceToFocusedVessel(Vessel bgVessel)
        {
            if (distanceOverrideForTesting != null)
                return distanceOverrideForTesting(bgVessel);

            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null) return double.MaxValue;
            return Vector3d.Distance(active.transform.position, bgVessel.transform.position);
        }

        /// <summary>
        /// Formats a sample interval for log messages.
        /// </summary>
        private static string FormatInterval(double interval)
        {
            if (interval >= ProximityRateSelector.OutOfRangeInterval || double.IsInfinity(interval))
                return "none";
            return interval.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "s";
        }

        private static bool IsBackgroundHighFidelitySamplingActive(
            BackgroundVesselState state,
            double currentUT,
            double proximityDistanceMeters)
        {
            return state != null
                && FlightRecorder.IsHighFidelitySamplingActive(
                    currentUT,
                    state.highFidelitySamplingUntilUT,
                    proximityDistanceMeters);
        }

        // Predicate gate for the debris parent-anchored sampling caps below.
        // Both the proximity-tier (min) cap and the configured-max backstop cap
        // share this gate so they always engage or disengage together.
        //
        // KEEP debris-only: the dual conjunct `IsDebris && ParentAnchorRecordingId != null`
        // is intentional - the cap was sized for short-lived debris orbiting a parent
        // for tens of seconds. Controlled-decoupled children (extension of the parent-anchor
        // contract) also carry ParentAnchorRecordingId but record indefinitely, so admitting
        // them here would multiply long-loiter sample storage 6-16x against an MIN-floor
        // / MAX-backstop tuning that was never meant for their lifetime profile. If a
        // future fidelity regression is reported for radial-breakup controlled children,
        // file a separate follow-up rather than collapsing this gate.
        internal static bool IsDebrisAwareSampleCapEligible(Recording treeRec)
        {
            return treeRec != null
                && treeRec.IsDebris
                && !string.IsNullOrEmpty(treeRec.ParentAnchorRecordingId);
        }

        private double ResolveDebrisParentDistanceMeters(Recording debrisRecording, Vessel debrisVessel)
        {
            if (!IsDebrisAwareSampleCapEligible(debrisRecording)
                || debrisVessel == null)
            {
                return double.NaN;
            }

            return ResolveDebrisParentDistanceMeters(
                debrisRecording,
                debrisVessel,
                out _,
                out _);
        }

        private double ResolveDebrisParentDistanceMeters(
            Recording debrisRecording,
            Vessel debrisVessel,
            out Recording parentRecording,
            out Vessel parentVessel)
        {
            parentRecording = null;
            parentVessel = null;

            if (debrisRecording == null
                || debrisVessel == null
                || tree?.Recordings == null
                || string.IsNullOrEmpty(debrisRecording.ParentAnchorRecordingId))
            {
                return double.NaN;
            }

            if (!tree.Recordings.TryGetValue(
                    debrisRecording.ParentAnchorRecordingId,
                    out parentRecording)
                || parentRecording == null
                || parentRecording.VesselPersistentId == 0u)
            {
                return double.NaN;
            }

            parentVessel = FlightRecorder.FindVesselByPid(parentRecording.VesselPersistentId);
            if (parentVessel == null || !parentVessel.loaded || parentVessel.packed)
                return double.NaN;

            // Use CoM (GetWorldPos3D) rather than vesselTransform.position here.
            // Pose composition deliberately uses vesselTransform to stay symmetric
            // with the recorded lat/lon/alt frame (CoM-to-vesselTransform vector is
            // ~10 m for radial boosters), but for the cadence-tier thresholds
            // (250 / 500 / 550 m) the CoM vs vesselTransform delta is well below
            // the boundary resolution and CoM is the cheaper, more conventional
            // proximity metric.
            Vector3d parentWorldPos = parentVessel.GetWorldPos3D();
            Vector3d debrisWorldPos = debrisVessel.GetWorldPos3D();
            if (!IsFinite(parentWorldPos) || !IsFinite(debrisWorldPos))
                return double.NaN;

            return Vector3d.Distance(parentWorldPos, debrisWorldPos);
        }

        private ProximitySamplingTier UpdateDebrisProximityState(
            BackgroundVesselState state,
            Recording treeRec,
            Vessel debrisVessel)
        {
            if (state == null)
                return ProximitySamplingTier.None;

            ProximitySamplingTier previousTier = state.debrisProximityTier;
            double distance = ResolveDebrisParentDistanceMeters(treeRec, debrisVessel);
            ProximitySamplingTier tier = ProximitySamplingTier.None;
            string reason = null;
            if (IsDebrisAwareSampleCapEligible(treeRec))
            {
                tier = ProximitySamplingCadence.Resolve(
                    distance,
                    DebrisFullFidelityProximityRangeMeters,
                    DebrisHalfFidelityProximityRangeMeters,
                    out reason);
            }

            if (previousTier != tier)
            {
                LogDebrisProximityTierTransition(
                    state.vesselPid,
                    treeRec,
                    previousTier,
                    tier,
                    distance,
                    reason,
                    "UpdateDebrisProximityState");
            }

            state.debrisProximityTier = tier;
            state.debrisProximityDistanceMeters = distance;
            state.debrisProximityReason = reason;
            return tier;
        }

        private static void LogDebrisProximityTierTransition(
            uint vesselPid,
            Recording treeRec,
            ProximitySamplingTier previousTier,
            ProximitySamplingTier newTier,
            double distance,
            string reason,
            string site)
        {
            if (previousTier == newTier)
                return;

            ParsekLog.Info(
                "Anchor",
                $"debris-proximity-tier-transition: site={site ?? "(unknown)"} " +
                $"pid={vesselPid.ToString(CultureInfo.InvariantCulture)} " +
                $"recId={treeRec?.RecordingId ?? "(none)"} " +
                $"parentRecId={treeRec?.ParentAnchorRecordingId ?? "(none)"} " +
                $"old={previousTier} new={newTier} " +
                $"distance={(IsFinite(distance) ? distance.ToString("F1", CultureInfo.InvariantCulture) : "NaN")}m " +
                $"reason={reason ?? "ineligible"}");
        }

        private static bool ShouldUseDebrisRelativeSection(BackgroundVesselState state)
        {
            if (state == null)
                return false;

            bool alreadyRelative = state.isRelativeMode
                || (state.trackSectionActive
                    && state.currentTrackSection.referenceFrame == ReferenceFrame.Relative);
            return ShouldUseDebrisRelativeSectionForDistance(
                state.debrisProximityDistanceMeters,
                alreadyRelative);
        }

        internal static bool ShouldUseDebrisRelativeSectionForDistance(
            double distanceMeters,
            bool alreadyRelative)
        {
            if (!IsFinite(distanceMeters))
                return false;

            double threshold = alreadyRelative
                ? DebrisRelativeSectionExitMeters
                : DebrisHalfFidelityProximityRangeMeters;
            return distanceMeters <= threshold;
        }

        // For parent-anchored debris recordings whose parent recording id is known, cap the
        // proximity-derived sample interval (the adaptive sampler's MIN floor)
        // at MidInterval (0.5 s) regardless of distance from the focused vessel.
        // Without this cap, radial debris that flies past 1000 m of its parent
        // during high-velocity breakup falls into the FarRange tier (2.0 s gap),
        // producing visibly choppy trajectory rendering.
        //
        // Out-of-range tier is preserved as-is; the later proximity skip is
        // bypassed only when the parent-proximity debris tier is active. Legacy
        // v11 debris (no
        // ParentAnchorRecordingId) and non-debris BG vessels keep the unaltered
        // tier table.
        internal static double ResolveDebrisAwareSampleInterval(
            double tierInterval,
            Recording treeRec)
        {
            if (!IsDebrisAwareSampleCapEligible(treeRec)) return tierInterval;
            if (tierInterval >= ProximityRateSelector.OutOfRangeInterval) return tierInterval;
            return Math.Min(tierInterval, ProximityRateSelector.MidInterval);
        }

        internal static bool ShouldSkipTrajectorySamplingForProximity(
            double proximityInterval,
            bool highFidelityActive,
            ProximitySamplingTier debrisTier)
        {
            return proximityInterval >= ProximityRateSelector.OutOfRangeInterval
                && !highFidelityActive
                && debrisTier == ProximitySamplingTier.None;
        }

        // Companion to ResolveDebrisAwareSampleInterval that bounds the adaptive
        // sampler's MAX backstop at MidInterval (0.5 s). Without this cap,
        // parent-anchored debris with stable velocity after the 3-second
        // high-fidelity post-decouple window only samples when the configured
        // max backstop fires — 3.0 s on Medium and 8.0 s on Low — even though
        // the proximity-tier floor was capped to 0.5 s. ShouldRecordPoint takes
        // the backstop as `if (elapsed >= maxInterval) return true`, so the
        // floor cap alone does not enforce the claimed 0.5 s sample-gap ceiling
        // for stable-velocity drift.
        //
        // Capped value composes with FlightRecorder.ResolveEffectiveMaxSampleInterval:
        // during the high-fidelity window that helper already returns
        // min(configuredMax, configuredMin) which is tighter than 0.5 s, so the
        // cap here is a no-op inside the window and a 0.5 s ceiling outside it.
        internal static float ResolveDebrisAwareMaxSampleInterval(
            float configuredMax,
            Recording treeRec)
        {
            if (!IsDebrisAwareSampleCapEligible(treeRec)) return configuredMax;
            return Math.Min(configuredMax, (float)ProximityRateSelector.MidInterval);
        }

        private static void ActivateBackgroundHighFidelitySampling(
            BackgroundVesselState state,
            double eventUT,
            string reason)
        {
            if (state == null || double.IsNaN(eventUT) || double.IsInfinity(eventUT))
                return;

            float configuredMax = ParsekSettings.Current?.maxSampleInterval
                ?? ParsekSettings.GetMaxSampleInterval(SamplingDensity.Medium);
            double windowSeconds = FlightRecorder.ResolveHighFidelitySamplingWindowSeconds(configuredMax);
            double untilUT = eventUT + windowSeconds;
            bool extendsWindow = double.IsNaN(state.highFidelitySamplingUntilUT)
                || untilUT > state.highFidelitySamplingUntilUT + 0.001;
            state.highFidelitySamplingUntilUT = Math.Max(
                double.IsNaN(state.highFidelitySamplingUntilUT)
                    ? double.MinValue
                    : state.highFidelitySamplingUntilUT,
                untilUT);
            state.highFidelitySamplingReason = reason ?? "unknown";

            if (extendsWindow)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Info("BgRecorder",
                    $"High-fidelity sampling window active: pid={state.vesselPid} " +
                    $"reason={state.highFidelitySamplingReason} " +
                    $"eventUT={eventUT.ToString("F2", ic)} " +
                    $"untilUT={state.highFidelitySamplingUntilUT.ToString("F2", ic)} " +
                    $"windowSeconds={windowSeconds.ToString("F3", ic)} " +
                    $"proximityRange={FlightRecorder.HighFidelityProximityRangeMeters.ToString("F1", ic)}m " +
                    $"intervalPolicy=configured-min-sample-interval");
            }
        }

        #endregion

        #region Background Environment Classification & TrackSection Management

        /// <summary>
        /// Classifies the environment for a background vessel using its cached engine list.
        /// Similar to FlightRecorder.ClassifyCurrentEnvironment but works with
        /// BackgroundVesselState's cached engines instead of FlightRecorder instance fields.
        /// Pure enough to be internal static for testability.
        /// </summary>
        internal static SegmentEnvironment ClassifyBackgroundEnvironment(
            bool hasAtmosphere, double altitude, double atmosphereDepth,
            int situation, double srfSpeed,
            List<(Part part, ModuleEngines engine, int moduleIndex)> cachedEngines,
            double approachAltitude = 0,
            bool isEva = false,
            double heightFromTerrain = -1,
            bool heightFromTerrainValid = false,
            bool hasOcean = false)
        {
            bool hasActiveThrust = false;
            if (cachedEngines != null)
            {
                for (int i = 0; i < cachedEngines.Count; i++)
                {
                    if (cachedEngines[i].engine != null &&
                        cachedEngines[i].engine.EngineIgnited &&
                        cachedEngines[i].engine.finalThrust > 0)
                    {
                        hasActiveThrust = true;
                        break;
                    }
                }
            }

            return EnvironmentDetector.Classify(
                hasAtmosphere, altitude, atmosphereDepth,
                situation, srfSpeed, hasActiveThrust, approachAltitude,
                isEva, heightFromTerrain, heightFromTerrainValid, hasOcean);
        }

        private bool TryConsumePendingInitialEnvironmentOverride(uint vesselPid, out SegmentEnvironment environment)
        {
            if (pendingInitialEnvironmentOverrides.TryGetValue(vesselPid, out environment))
            {
                pendingInitialEnvironmentOverrides.Remove(vesselPid);
                return true;
            }

            environment = SegmentEnvironment.Atmospheric;
            return false;
        }

        private bool TryConsumePendingInitialTrajectoryPoint(uint vesselPid, out TrajectoryPoint point)
        {
            if (pendingInitialTrajectoryPoints.TryGetValue(vesselPid, out point))
            {
                pendingInitialTrajectoryPoints.Remove(vesselPid);
                return true;
            }

            point = default(TrajectoryPoint);
            return false;
        }

        internal static bool IsPendingDebrisSeedParentAnchorUsable(
            string queuedParentRecordingId,
            string expectedParentRecordingId,
            double queuedUT,
            double seedUT,
            out string reason,
            double toleranceSeconds = 1e-6)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(queuedParentRecordingId))
            {
                reason = "queued-parent-recording-id-missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(expectedParentRecordingId))
            {
                reason = "expected-parent-recording-id-missing";
                return false;
            }

            if (!string.Equals(queuedParentRecordingId, expectedParentRecordingId, StringComparison.Ordinal))
            {
                reason = "queued-parent-recording-id-mismatch";
                return false;
            }

            if (double.IsNaN(queuedUT)
                || double.IsInfinity(queuedUT)
                || double.IsNaN(seedUT)
                || double.IsInfinity(seedUT))
            {
                reason = "queued-parent-anchor-ut-invalid";
                return false;
            }

            if (double.IsNaN(toleranceSeconds)
                || double.IsInfinity(toleranceSeconds)
                || toleranceSeconds < 0.0)
            {
                reason = "queued-parent-anchor-tolerance-invalid";
                return false;
            }

            if (Math.Abs(queuedUT - seedUT) > toleranceSeconds)
            {
                reason = "queued-parent-anchor-ut-mismatch";
                return false;
            }

            return true;
        }

        private bool TryConsumePendingDebrisSeedParentAnchorPose(
            uint vesselPid,
            string expectedParentRecordingId,
            double seedUT,
            out AnchorPose pose,
            out string reason)
        {
            pose = default;
            reason = null;
            if (!pendingDebrisSeedParentAnchorPoints.TryGetValue(
                    vesselPid,
                    out (string parentRecordingId, TrajectoryPoint point) pending))
            {
                reason = "queued-parent-anchor-missing";
                return false;
            }

            pendingDebrisSeedParentAnchorPoints.Remove(vesselPid);
            if (!IsPendingDebrisSeedParentAnchorUsable(
                    pending.parentRecordingId,
                    expectedParentRecordingId,
                    pending.point.ut,
                    seedUT,
                    out reason))
            {
                return false;
            }

            if (!TryCreateAnchorPoseFromAbsolutePoint(
                    pending.point,
                    expectedParentRecordingId,
                    out pose,
                    out reason))
            {
                return false;
            }

            return true;
        }

        private static bool TryCreateAnchorPoseFromAbsolutePoint(
            TrajectoryPoint point,
            string recordingId,
            out AnchorPose pose,
            out string reason)
        {
            pose = default;
            reason = null;
            CelestialBody body = FindBackgroundBody(point.bodyName);
            if (body == null || body.bodyTransform == null)
            {
                reason = "queued-parent-anchor-body-unresolved";
                return false;
            }

            Vector3d worldPos;
            try
            {
                worldPos = body.GetWorldSurfacePosition(point.latitude, point.longitude, point.altitude);
            }
            catch (Exception)
            {
                reason = "queued-parent-anchor-world-position-unresolved";
                return false;
            }

            Quaternion worldRotation = TrajectoryMath.PureMultiply(
                body.bodyTransform.rotation,
                TrajectoryMath.SanitizeQuaternion(point.rotation));
            pose = new AnchorPose(worldPos, worldRotation, -1, recordingId);
            if (!IsFiniteAnchorPose(pose))
            {
                reason = "queued-parent-anchor-pose-nonfinite";
                pose = default;
                return false;
            }

            return true;
        }

        internal static string ClassifyBackgroundRelativeFrameContract(
            string sourceLabel,
            AnchorCandidateSource source,
            bool hasCurrentAnchorCandidate,
            bool activeReFlyParentMatch,
            bool hasPreReFlyAnchorSnapshot)
        {
            string normalized = sourceLabel ?? string.Empty;
            if (string.Equals(normalized, "queued-parent-seed", StringComparison.Ordinal))
                return "recorded";

            if (string.Equals(normalized, "live", StringComparison.Ordinal)
                || string.Equals(normalized, "live-warn-fallback", StringComparison.Ordinal))
            {
                return "live";
            }

            if (string.Equals(normalized, "recorded", StringComparison.Ordinal)
                || !hasCurrentAnchorCandidate)
            {
                return activeReFlyParentMatch && hasPreReFlyAnchorSnapshot
                    ? "frozen-refly"
                    : "recorded";
            }

            return source == AnchorCandidateSource.Live ? "live" : "recorded";
        }

        internal static bool IsActiveReFlyParentMatch(
            string activeReFlyRecordingId,
            string parentRecordingId)
        {
            return !string.IsNullOrEmpty(activeReFlyRecordingId)
                && !string.IsNullOrEmpty(parentRecordingId)
                && string.Equals(activeReFlyRecordingId, parentRecordingId, StringComparison.Ordinal);
        }

        private string ClassifyBackgroundRelativeFrameContract(
            string anchorRecordingId,
            string sourceLabel,
            AnchorCandidateSource source,
            bool hasCurrentAnchorCandidate,
            bool hasPreReFlyAnchorSnapshot)
        {
            var marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            return ClassifyBackgroundRelativeFrameContract(
                sourceLabel,
                source,
                hasCurrentAnchorCandidate,
                IsActiveReFlyParentMatch(marker, anchorRecordingId),
                hasPreReFlyAnchorSnapshot);
        }

        private bool HasActiveReFlyPreReFlyAnchorSnapshot(string anchorRecordingId)
        {
            var marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            if (!IsActiveReFlyParentMatch(marker, anchorRecordingId)
                || tree?.Recordings == null
                || !tree.Recordings.TryGetValue(anchorRecordingId, out Recording anchorRecording)
                || anchorRecording == null)
            {
                return false;
            }

            return anchorRecording.HasPreReFlyAnchorTrajectory(marker.SessionId);
        }

        private static bool IsActiveReFlyParentMatch(
            ReFlySessionMarker marker,
            string parentRecordingId)
        {
            return marker != null
                && IsActiveReFlyParentMatch(
                    marker.ActiveReFlyRecordingId,
                    parentRecordingId);
        }

        private string FormatActiveReFlyParentDriftFromRecorded(
            string parentRecordingId,
            uint diagnosticPid,
            double ut)
        {
            var marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            bool activeReFlyParentMatch = IsActiveReFlyParentMatch(marker, parentRecordingId);
            if (!activeReFlyParentMatch)
                return null;

            bool liveResolved = TryResolveLiveParentWorldPosition(
                parentRecordingId,
                diagnosticPid,
                out Vector3d liveWorldPos);
            bool recordedResolved = TryResolveBackgroundRecordedAnchorPose(
                parentRecordingId,
                ut,
                out AnchorPose recordedPose,
                out _);

            return FormatActiveReFlyParentDriftFromRecorded(
                activeReFlyParentMatch,
                liveResolved,
                liveWorldPos,
                recordedResolved,
                recordedPose.WorldPos);
        }

        internal static string FormatActiveReFlyParentDriftFromRecorded(
            bool activeReFlyParentMatch,
            bool liveResolved,
            Vector3d liveWorldPos,
            bool recordedResolved,
            Vector3d recordedWorldPos)
        {
            if (!activeReFlyParentMatch)
                return null;

            if (!liveResolved
                || !recordedResolved
                || !IsFinite(liveWorldPos)
                || !IsFinite(recordedWorldPos))
            {
                return "(unresolved)";
            }

            double drift = (liveWorldPos - recordedWorldPos).magnitude;
            return IsFinite(drift)
                ? drift.ToString("F3", CultureInfo.InvariantCulture)
                : "(unresolved)";
        }

        internal static string FormatParentDriftLogField(string parentDriftFromRecorded)
        {
            return parentDriftFromRecorded != null
                ? $"parentDriftFromRecorded={parentDriftFromRecorded} "
                : string.Empty;
        }

        private bool TryResolveLiveParentWorldPosition(
            string parentRecordingId,
            uint diagnosticPid,
            out Vector3d liveWorldPos)
        {
            liveWorldPos = Vector3d.zero;

            Vessel liveParent = diagnosticPid != 0u
                ? FlightRecorder.FindVesselByPid(diagnosticPid)
                : null;
            if ((liveParent == null || !liveParent.loaded)
                && tree?.Recordings != null
                && !string.IsNullOrEmpty(parentRecordingId)
                && tree.Recordings.TryGetValue(parentRecordingId, out Recording parentRecording)
                && parentRecording != null
                && parentRecording.VesselPersistentId != 0u)
            {
                liveParent = FlightRecorder.FindVesselByPid(parentRecording.VesselPersistentId);
            }

            if (liveParent != null && liveParent.loaded && liveParent.transform != null)
            {
                liveWorldPos = (Vector3d)liveParent.transform.position;
                return IsFinite(liveWorldPos);
            }

            return false;
        }

        private static bool IsFiniteAnchorPose(AnchorPose pose)
        {
            return IsFinite(pose.WorldPos)
                && IsFinite(pose.WorldRotation);
        }

        private static bool IsFinite(Vector3d value)
        {
            return IsFinite(value.x)
                && IsFinite(value.y)
                && IsFinite(value.z);
        }

        private static bool IsFinite(Quaternion value)
        {
            return IsFinite(value.x)
                && IsFinite(value.y)
                && IsFinite(value.z)
                && IsFinite(value.w);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>
        /// Opens a new TrackSection for a background vessel.
        /// Sets source = Background.
        /// </summary>
        private void StartBackgroundTrackSection(
            BackgroundVesselState state, SegmentEnvironment env, ReferenceFrame refFrame, double ut)
        {
            state.currentTrackSection = new TrackSection
            {
                environment = env,
                referenceFrame = refFrame,
                startUT = ut,
                source = TrackSectionSource.Background,
                anchorRecordingId = refFrame == ReferenceFrame.Relative
                    ? state.currentAnchorRecordingId
                    : null,
                anchorVesselId = 0u,
                frames = new List<TrajectoryPoint>(),
                bodyFixedFrames = refFrame == ReferenceFrame.Relative
                    ? new List<TrajectoryPoint>()
                    : null,
                checkpoints = new List<OrbitSegment>(),
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            };
            state.trackSectionActive = true;
            // Phase 7: reset per-section clearance accumulators (mirror
            // FlightRecorder.StartNewTrackSection). Even when the new section
            // is not a surface-clearance environment, resetting is cheap and
            // keeps state crisp.
            state.surfaceMobileSamplesThisSection = 0;
            state.surfaceMobileMinClearanceThisSection = double.NaN;
            state.surfaceMobileMaxClearanceThisSection = double.NaN;
            state.surfaceMobileClearanceSumThisSection = 0.0;
            state.sectionFrameWarpFlags.Clear();
            ParsekLog.Info("BgRecorder",
                $"TrackSection started: env={env} ref={refFrame} source=Background " +
                $"pid={state.vesselPid} at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Appends one entry to <paramref name="state"/>'s per-section warp-flag
        /// list in lockstep with a <c>currentTrackSection.frames.Add</c>. Records
        /// whether the sample was taken under physics time-warp so the section-
        /// close sparse-sampling check can classify each gap. On-rails BG samples
        /// never reach these append paths (the per-frame tick early-returns on
        /// <c>bgVessel.packed</c>), so physics warp is the only signal.
        /// </summary>
        private static void AppendSectionFrameWarpFlag(BackgroundVesselState state)
        {
            state?.sectionFrameWarpFlags.Add(FlightRecorder.IsTimeWarpActiveForDiagnostics());
        }

        private static void AppendFrameToCurrentTrackSection(
            BackgroundVesselState state,
            TrajectoryPoint point,
            TrajectoryPoint? bodyFixedPrimaryPoint = null)
        {
            if (state == null || !state.trackSectionActive || state.currentTrackSection.frames == null)
                return;

            state.currentTrackSection.frames.Add(point);
            AppendSectionFrameWarpFlag(state);
            if (bodyFixedPrimaryPoint.HasValue && state.currentTrackSection.bodyFixedFrames != null)
                state.currentTrackSection.bodyFixedFrames.Add(bodyFixedPrimaryPoint.Value);
            if (float.IsNaN(state.currentTrackSection.minAltitude)
                || point.altitude < state.currentTrackSection.minAltitude)
            {
                state.currentTrackSection.minAltitude = (float)point.altitude;
            }
            if (float.IsNaN(state.currentTrackSection.maxAltitude)
                || point.altitude > state.currentTrackSection.maxAltitude)
            {
                state.currentTrackSection.maxAltitude = (float)point.altitude;
            }
        }

        private static void ApplyInitialTrajectoryPoint(
            BackgroundVesselState state,
            Recording treeRec,
            TrajectoryPoint point,
            TrajectoryPoint? bodyFixedPrimaryPoint = null)
        {
            // Bug #419: ApplyTrajectoryPointToRecording rejects non-monotonic appends. If
            // the flat-points append was rejected, the seed must NOT enter the track
            // section either — otherwise the same point re-materializes in
            // treeRec.Points on the next FlushTrackSectionsToRecording call via
            // AppendPointsFromTrackSections. Similarly, do not advance lastRecordedUT /
            // lastRecordedVelocity from a rejected seed, or the next frame-delta check
            // would be keyed on a phantom time.
            bool accepted = treeRec == null || ApplyTrajectoryPointToRecording(treeRec, point);
            if (!accepted)
            {
                ParsekLog.Warn("BgRecorder",
                    $"Initial trajectory point rejected (non-monotonic): pid={state.vesselPid} " +
                    $"ut={point.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"recId={treeRec.RecordingId} — dropped from track section and state (#419)");
                return;
            }

            AppendFrameToCurrentTrackSection(state, point, bodyFixedPrimaryPoint);
            state.lastRecordedUT = point.ut;
            state.lastRecordedVelocity = point.velocity;

            ParsekLog.Info("BgRecorder", string.Format(CultureInfo.InvariantCulture,
                "Initial trajectory point seeded: pid={0} ut={1:F2} " +
                "lat={2:F4} lon={3:F4} alt={4:F0} vel=({5:F2},{6:F2},{7:F2}) |vel|={8:F2}",
                state.vesselPid,
                point.ut,
                point.latitude,
                point.longitude,
                point.altitude,
                point.velocity.x,
                point.velocity.y,
                point.velocity.z,
                point.velocity.magnitude));
        }

        internal static bool ApplyTrajectoryPointToRecording(Recording treeRec, TrajectoryPoint point)
        {
            if (treeRec == null)
                return false;

            // Bug #419: enforce monotonicity at the choke point. Every sampler path that
            // appends to treeRec.Points — breakup initial seeds, on-rails seeds,
            // background fallback seeds — funnels through here. If a caller tries to
            // append a point whose UT is strictly less than the current last point's UT
            // (e.g., a belated breakup-initial seed consumed after physics-frame samples
            // already advanced, or a duplicate suffix re-append from a flush path),
            // reject the point with a warn log so binary-search and interpolation
            // consumers keep their monotonicity invariant. Equal UTs are tolerated —
            // duplicate-UT samples occur at boundary seeds and are deduped downstream.
            // Returns false when rejected so state-advancing callers (track-section
            // append, lastRecordedUT bookkeeping) can short-circuit too — otherwise the
            // same rejected point would re-enter treeRec.Points through the track-section
            // flush path (RecordingStore.AppendPointsFromTrackSections).
            if (!FlightRecorder.IsAppendUTMonotonic(treeRec.Points, point.ut))
            {
                TrajectoryPoint last = treeRec.Points[treeRec.Points.Count - 1];
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Warn("BgRecorder",
                    $"ApplyTrajectoryPointToRecording: rejected non-monotonic UT " +
                    $"recId={treeRec.RecordingId} " +
                    $"incoming ut={point.ut.ToString("R", ic)} " +
                    $"< last ut={last.ut.ToString("R", ic)} " +
                    $"(points={treeRec.Points.Count}, dropped to preserve monotonicity — #419)");
                return false;
            }

            treeRec.Points.Add(point);
            treeRec.MarkFilesDirty();
            if (double.IsNaN(treeRec.ExplicitEndUT) || treeRec.ExplicitEndUT < point.ut)
                treeRec.ExplicitEndUT = point.ut;
            return true;
        }

        /// <summary>
        /// Opens an OrbitalCheckpoint TrackSection for a background vessel transitioning to on-rails.
        /// Sets source = Checkpoint.
        /// </summary>
        private void StartCheckpointTrackSection(BackgroundVesselState state, double ut)
        {
            state.currentTrackSection = RecordingStore.BuildOpenOnRailsCheckpointSection(ut);
            state.trackSectionActive = true;
            ParsekLog.Info("BgRecorder",
                $"TrackSection started: env=ExoBallistic ref=OrbitalCheckpoint source=Checkpoint " +
                $"pid={state.vesselPid} at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Returns the last trajectory frame of a background vessel's current TrackSection,
        /// or null if empty/inactive. Used to capture a boundary point before closing (#283).
        /// </summary>
        private static TrajectoryPoint? GetLastBackgroundFrame(BackgroundVesselState state)
        {
            if (state.trackSectionActive && state.currentTrackSection.frames != null
                && state.currentTrackSection.frames.Count > 0)
                return state.currentTrackSection.frames[state.currentTrackSection.frames.Count - 1];
            return null;
        }

        /// <summary>
        /// Seeds a background vessel's current (newly opened) TrackSection with a boundary
        /// point from the previous section, eliminating position discontinuities (#283).
        /// </summary>
        private static void SeedBackgroundBoundaryPoint(BackgroundVesselState state, TrajectoryPoint? point)
        {
            if (!point.HasValue) return;
            AddFrameToActiveTrackSection(state, point.Value);
            ParsekLog.Verbose("BgRecorder",
                $"Boundary point seeded: pid={state.vesselPid} ut={point.Value.ut.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        private static void AddFrameToActiveTrackSection(
            BackgroundVesselState state,
            TrajectoryPoint point,
            TrajectoryPoint? bodyFixedPrimaryPoint = null)
        {
            if (!state.trackSectionActive || state.currentTrackSection.frames == null)
                return;

            state.currentTrackSection.frames.Add(point);
            AppendSectionFrameWarpFlag(state);
            if (bodyFixedPrimaryPoint.HasValue && state.currentTrackSection.bodyFixedFrames != null)
                state.currentTrackSection.bodyFixedFrames.Add(bodyFixedPrimaryPoint.Value);
            if (float.IsNaN(state.currentTrackSection.minAltitude)
                || point.altitude < state.currentTrackSection.minAltitude)
            {
                state.currentTrackSection.minAltitude = (float)point.altitude;
            }

            if (float.IsNaN(state.currentTrackSection.maxAltitude)
                || point.altitude > state.currentTrackSection.maxAltitude)
            {
                state.currentTrackSection.maxAltitude = (float)point.altitude;
            }
        }

        private static void AppendBoundaryPointToRecording(Recording treeRec, TrajectoryPoint point, uint vesselPid)
        {
            if (treeRec == null)
                return;

            treeRec.Points.Add(point);
            treeRec.MarkFilesDirty();
            treeRec.ExplicitEndUT = point.ut;

            ParsekLog.Verbose("BgRecorder", $"Boundary point sampled: pid={vesselPid} " +
                $"UT={point.ut:F1} alt={point.altitude:F0} pts={treeRec.Points.Count}");
        }

        /// <summary>
        /// Closes the current TrackSection for a background vessel and appends it
        /// to the per-vessel trackSections list.
        /// </summary>
        private void CloseBackgroundTrackSection(BackgroundVesselState state, double ut)
        {
            if (!state.trackSectionActive) return;

            state.currentTrackSection.endUT = ut;

            // Compute approximate sample rate
            if (state.currentTrackSection.frames != null && state.currentTrackSection.frames.Count > 1)
            {
                double duration = state.currentTrackSection.endUT - state.currentTrackSection.startUT;
                if (duration > 0)
                    state.currentTrackSection.sampleRateHz =
                        (float)(state.currentTrackSection.frames.Count / duration);
            }

            int frameCount = state.currentTrackSection.frames?.Count ?? 0;
            int checkpointCount = state.currentTrackSection.checkpoints?.Count ?? 0;
            double sectionDuration = ut - state.currentTrackSection.startUT;

            // Discard seed-only transient RELATIVE sections that were closed
            // within one physics frame of opening. Mirror of
            // FlightRecorder.CloseCurrentTrackSection's discard (see that
            // method for the full rationale and scope notes). The canonical
            // BG case: ExitBackgroundRelativeMode fires from the force-Absolute
            // gate one physics frame after an environment-boundary handler
            // opened a Relative section with only the env-boundary seed
            // point, producing a 1-frame 0.02s Relative section that
            // would otherwise generate a multi-meter
            // boundaryDiscontinuityMeters against the next Absolute section
            // and a matching anchor-correction offset at playback. The
            // observed user-visible symptom: vessel ghosts spawning ~24m
            // displaced from the live re-fly vessel on entry. 0.05s threshold
            // = the tightest production min sample interval (Full-tier
            // ProximitySamplingCadence). Restricted to Relative to preserve
            // legitimate Absolute single-frame finalizations used by
            // FinalizeAllForCommit; isBoundarySeam-flagged sections are
            // also exempted for the optimizer split-suppression contract
            // (TrackSection.isBoundarySeam, docs/dev/plans/optimizer-persistence-split.md §5).
            if (frameCount <= 1
                && checkpointCount == 0
                && sectionDuration < 0.05
                && state.currentTrackSection.referenceFrame == ReferenceFrame.Relative
                && !state.currentTrackSection.isBoundarySeam)
            {
                state.trackSectionActive = false;
                ParsekLog.Verbose("BgRecorder",
                    $"TrackSection discarded (seed-only Relative transient, frames={frameCount} " +
                    $"duration={sectionDuration.ToString("F4", CultureInfo.InvariantCulture)}s): " +
                    $"env={state.currentTrackSection.environment} ref={state.currentTrackSection.referenceFrame} " +
                    $"pid={state.vesselPid} " +
                    $"startUT={state.currentTrackSection.startUT.ToString("F3", CultureInfo.InvariantCulture)}");
                return;
            }

            state.trackSections.Add(state.currentTrackSection);
            state.trackSectionActive = false;

            float configuredMaxSampleInterval = ParsekSettings.Current?.maxSampleInterval
                ?? ParsekSettings.GetMaxSampleInterval(SamplingDensity.Medium);
            double sparseGapThreshold =
                FlightRecorder.ResolveSparseGapWarningThreshold(configuredMaxSampleInterval);
            FlightRecorder.SectionGapStats gapStats =
                FlightRecorder.ComputeSectionGapStats(
                    state.currentTrackSection.frames,
                    largeGapThresholdSeconds: sparseGapThreshold,
                    warpFlags: state.sectionFrameWarpFlags);
            if (state.currentTrackSection.referenceFrame == ReferenceFrame.Relative
                && string.IsNullOrWhiteSpace(state.currentTrackSection.anchorRecordingId))
            {
                ParsekLog.Warn("BgRecorder",
                    $"Relative TrackSection closing without anchorRecordingId: " +
                    $"pid={state.vesselPid} startUT={state.currentTrackSection.startUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"endUT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
            }

            string anchorSuffix = state.currentTrackSection.referenceFrame == ReferenceFrame.Relative
                ? $" anchorRecordingId={state.currentTrackSection.anchorRecordingId ?? "(none)"}"
                : string.Empty;
            ParsekLog.Info("BgRecorder",
                $"TrackSection closed: env={state.currentTrackSection.environment} " +
                $"ref={state.currentTrackSection.referenceFrame} " +
                $"frames={frameCount} checkpoints={checkpointCount} " +
                $"duration={sectionDuration.ToString("F2", CultureInfo.InvariantCulture)}s " +
                $"avgGap={gapStats.AverageGapSeconds.ToString("F3", CultureInfo.InvariantCulture)}s " +
                $"maxGap={gapStats.MaxGapSeconds.ToString("F3", CultureInfo.InvariantCulture)}s " +
                $"largeGaps={gapStats.LargeGapCount} " +
                $"pid={state.vesselPid}{anchorSuffix}");

            if (gapStats.LargeGapCount > 0)
            {
                bool warn = FlightRecorder.ShouldWarnOnSparseSampling(
                    gapStats.LargeGapCountAtNormalRate);
                string message =
                    $"TrackSection sparse sampling: pid={state.vesselPid} " +
                    $"env={state.currentTrackSection.environment} " +
                    $"ref={state.currentTrackSection.referenceFrame} frames={frameCount} " +
                    $"maxGap={gapStats.MaxGapSeconds.ToString("F3", CultureInfo.InvariantCulture)}s " +
                    $"threshold={sparseGapThreshold.ToString("F2", CultureInfo.InvariantCulture)}s " +
                    $"largeGaps={gapStats.LargeGapCount} largeGaps1x={gapStats.LargeGapCountAtNormalRate}";
                if (warn)
                    ParsekLog.Warn("BgRecorder", message);
                else
                    ParsekLog.Verbose("BgRecorder", message);
            }

            // Phase 7: per-surface-section clearance distribution
            // diagnostic. Mirrors FlightRecorder.CloseCurrentTrackSection so
            // BG-recorded surface vessels emit the same `clearanceMin/Max/Avg/N`
            // line as foreground-recorded surface vessels. Suppressed when no
            // surface samples were captured (non-surface sections
            // and Relative-frame sections leave the counter at zero).
            if (FlightRecorder.IsSurfaceClearanceEnvironment(state.currentTrackSection.environment)
                && state.surfaceMobileSamplesThisSection > 0)
            {
                CultureInfo ic = CultureInfo.InvariantCulture;
                double avg = state.surfaceMobileClearanceSumThisSection / state.surfaceMobileSamplesThisSection;
                ParsekLog.Verbose("Pipeline-Terrain",
                    $"BG section close env={state.currentTrackSection.environment} pid={state.vesselPid} " +
                    $"clearanceMin={state.surfaceMobileMinClearanceThisSection.ToString("F3", ic)}m " +
                    $"clearanceMax={state.surfaceMobileMaxClearanceThisSection.ToString("F3", ic)}m " +
                    $"clearanceAvg={avg.ToString("F3", ic)}m " +
                    $"N={state.surfaceMobileSamplesThisSection}");
            }

            // Defensive accumulator reset (mirrors P3-3 follow-up on
            // FlightRecorder.CloseCurrentTrackSection — closing without
            // immediately opening must not leave stale state).
            state.surfaceMobileSamplesThisSection = 0;
            state.surfaceMobileMinClearanceThisSection = double.NaN;
            state.surfaceMobileMaxClearanceThisSection = double.NaN;
            state.surfaceMobileClearanceSumThisSection = 0.0;
        }

        /// <summary>
        /// Copies all accumulated TrackSections from a BackgroundVesselState to the
        /// recording's TrackSections list.
        /// </summary>
        private static void FlushTrackSectionsToRecording(BackgroundVesselState state, Recording treeRec)
        {
            if (state.trackSections.Count == 0) return;

            for (int i = 0; i < state.trackSections.Count; i++)
            {
                treeRec.TrackSections.Add(state.trackSections[i]);
            }
            treeRec.MarkFilesDirty();
            int dedupedPointCopies = RecordingStore.AppendPointsFromTrackSections(state.trackSections, treeRec.Points);
            int dedupedOrbitCopies = RecordingStore.AppendOrbitSegmentsFromTrackSections(state.trackSections, treeRec.OrbitSegments);
            treeRec.CachedStats = null;
            treeRec.CachedStatsPointCount = 0;
            DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                treeRec,
                $"FlushTrackSectionsToRecording pid={state.vesselPid}");

            ParsekLog.Info("BgRecorder",
                $"Flushed {state.trackSections.Count} TrackSections to recording: " +
                $"pid={state.vesselPid} recId={state.recordingId} " +
                $"points={treeRec.Points.Count} orbitSegments={treeRec.OrbitSegments.Count} " +
                $"dedupedPointCopies={dedupedPointCopies} dedupedOrbitCopies={dedupedOrbitCopies}");

            // Clear after flush to prevent duplicate sections if flushed again
            // (e.g., FinalizeAllForCommit followed by Shutdown)
            state.trackSections.Clear();
        }

        #endregion

        #region Part Event Polling

        /// <summary>
        /// Polls all part event types for a background vessel in loaded/physics mode.
        /// Duplicates the Layer 2 wrapper methods from FlightRecorder, calling the
        /// same Layer 1 static transition methods.
        ///
        /// Part Event Coverage Audit (BackgroundRecorder vs FlightRecorder):
        ///
        ///   POLLED (per-physics-frame, in PollPartEvents):
        ///     [x] CheckParachuteState        - ParachuteDeployed / ParachuteCut / ParachuteSemiDeployed
        ///     [x] CheckJettisonState          - ShroudJettisoned
        ///     [x] CheckEngineState            - EngineIgnited / EngineShutdown / EngineThrottle
        ///     [x] CheckRcsState               - RCSActivated / RCSStopped / RCSThrottle
        ///     [x] CheckDeployableState        - DeployableExtended / DeployableRetracted
        ///     [x] CheckLadderState            - DeployableExtended / DeployableRetracted (ladders)
        ///     [x] CheckAnimationGroupState    - DeployableExtended / DeployableRetracted (anim groups)
        ///     [x] CheckAeroSurfaceState       - DeployableExtended / DeployableRetracted (aero surfaces)
        ///     [x] CheckControlSurfaceState    - DeployableExtended / DeployableRetracted (control surfaces)
        ///     [x] CheckRobotArmScannerState   - DeployableExtended / DeployableRetracted (robot arms)
        ///     [x] CheckAnimateHeatState       - ThermalAnimationHot / ThermalAnimationMedium / ThermalAnimationCold
        ///     [x] CheckAnimateGenericState    - DeployableExtended / DeployableRetracted (generic animations)
        ///     [x] CheckLightState             - LightOn / LightOff / LightBlinkEnabled / LightBlinkDisabled / LightBlinkRate
        ///     [x] CheckGearState              - GearDeployed / GearRetracted
        ///     [x] CheckCargoBayState          - CargoBayOpened / CargoBayClosed
        ///     [x] CheckFairingState           - FairingJettisoned
        ///     [x] CheckRoboticState           - RoboticMotionStarted / RoboticPositionSample / RoboticMotionStopped
        ///
        ///   GAME-EVENT DRIVEN (via SubscribePartEvents):
        ///     [x] onPartDie                   - Destroyed / ParachuteDestroyed (OnBackgroundPartDie)
        ///     [x] onPartJointBreak            - Decoupled (OnBackgroundPartJointBreak)
        ///
        ///   NOT APPLICABLE to background vessels (handled elsewhere):
        ///     [-] Docked / Undocked           - branch management in ParsekFlight
        ///     [-] InventoryPartPlaced/Removed  - EVA-only, active vessel only
        /// </summary>
        private void PollPartEvents(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            // Count delta before/after pattern: all CheckXState methods only
            // mutate treeRec.PartEvents (no Points/OrbitSegments/etc changes),
            // so a single post-polling dirty mark guarded on count-delta covers
            // all 17 child checks without 19 inline MarkFilesDirty calls.
            // If no events were emitted this poll, the mark is skipped — keeps
            // the frequent per-physics-frame poll cheap.
            int prePartEventCount = treeRec.PartEvents.Count;

            CheckParachuteState(v, state, treeRec, ut);
            CheckJettisonState(v, state, treeRec, ut);
            CheckEngineState(v, state, treeRec, ut);
            CheckRcsState(v, state, treeRec, ut);
            CheckDeployableState(v, state, treeRec, ut);
            CheckLadderState(v, state, treeRec, ut);
            CheckAnimationGroupState(v, state, treeRec, ut);
            CheckAeroSurfaceState(v, state, treeRec, ut);
            CheckControlSurfaceState(v, state, treeRec, ut);
            CheckRobotArmScannerState(v, state, treeRec, ut);
            CheckAnimateHeatState(v, state, treeRec, ut);
            CheckAnimateGenericState(v, state, treeRec, ut);
            CheckLightState(v, state, treeRec, ut);
            CheckGearState(v, state, treeRec, ut);
            CheckCargoBayState(v, state, treeRec, ut);
            CheckFairingState(v, state, treeRec, ut);
            CheckRoboticState(v, state, treeRec, ut);

            if (treeRec.PartEvents.Count > prePartEventCount)
                treeRec.MarkFilesDirty();
        }

        private void CheckParachuteState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var chute = p.FindModuleImplementing<ModuleParachute>();
                if (chute == null) continue;

                int chuteState = chute.deploymentState == ModuleParachute.deploymentStates.DEPLOYED ? 2
                    : chute.deploymentState == ModuleParachute.deploymentStates.SEMIDEPLOYED ? 1
                    : 0;

                var evt = FlightRecorder.CheckParachuteTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", chuteState, state.parachuteStates, ut);
                if (evt.HasValue)
                {
                    treeRec.PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId} (bg vessel {state.vesselPid})");
                }
            }
        }

        private void CheckJettisonState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                bool hasJettisonModule = false;
                bool isJettisoned = false;
                int jettisonModuleIndex = 0;
                bool skipTransformFallback = p.FindModuleImplementing<ModulePartVariants>() != null;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    var jettison = p.Modules[m] as ModuleJettison;
                    if (jettison == null) continue;
                    hasJettisonModule = true;
                    ulong moduleKey = FlightRecorder.EncodeEngineKey(p.persistentId, jettisonModuleIndex);
                    jettisonModuleIndex++;

                    if (jettison.isJettisoned)
                    {
                        isJettisoned = true;
                        break;
                    }

                    if (skipTransformFallback)
                        continue;

                    string jettisonNames = jettison.jettisonName;
                    if (string.IsNullOrWhiteSpace(jettisonNames))
                        continue;

                    string[] names = GetCachedJettisonNames(state, moduleKey, jettisonNames);
                    for (int n = 0; n < names.Length; n++)
                    {
                        Transform jt = p.FindModelTransform(names[n]);
                        if (jt != null && !jt.gameObject.activeInHierarchy)
                        {
                            isJettisoned = true;
                            break;
                        }
                    }
                    if (isJettisoned)
                        break;
                }

                if (!hasJettisonModule) continue;

                var evt = FlightRecorder.CheckJettisonTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isJettisoned, state.jettisonedShrouds, ut);
                if (evt.HasValue)
                {
                    treeRec.PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId} (bg vessel {state.vesselPid})");
                }
            }
        }

        private void CheckDeployableState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var deployable = p.FindModuleImplementing<ModuleDeployablePart>();
                if (deployable == null) continue;

                var ds = deployable.deployState;
                if (ds == ModuleDeployablePart.DeployState.BROKEN) continue;
                if (ds == ModuleDeployablePart.DeployState.EXTENDING) continue;
                if (ds == ModuleDeployablePart.DeployState.RETRACTING) continue;

                bool isExtended = ds == ModuleDeployablePart.DeployState.EXTENDED;

                var evt = FlightRecorder.CheckDeployableTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isExtended, state.extendedDeployables, ut);
                if (evt.HasValue)
                {
                    treeRec.PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId} (bg vessel {state.vesselPid})");
                }
            }
        }

        private void CheckLightState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var light = p.FindModuleImplementing<ModuleLight>();
                if (light == null) continue;

                var evt = FlightRecorder.CheckLightTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", light.isOn, state.lightsOn, ut);
                if (evt.HasValue)
                {
                    treeRec.PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId} (bg vessel {state.vesselPid})");
                }

                reusableEventBuffer.Clear();
                FlightRecorder.CheckLightBlinkTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown",
                    light.isBlinking, light.blinkRate,
                    state.blinkingLights, state.lightBlinkRates, ut, reusableEventBuffer);
                for (int e = 0; e < reusableEventBuffer.Count; e++)
                {
                    treeRec.PartEvents.Add(reusableEventBuffer[e]);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {reusableEventBuffer[e].eventType} '{reusableEventBuffer[e].partName}' " +
                        $"pid={reusableEventBuffer[e].partPersistentId} val={reusableEventBuffer[e].value:F2} (bg vessel {state.vesselPid})");
                }
            }
        }

        private void CheckGearState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    var wheel = p.Modules[m] as ModuleWheels.ModuleWheelDeployment;
                    if (wheel == null) continue;

                    FlightRecorder.ClassifyGearState(wheel.stateString, out bool isDeployed, out bool isRetracted);
                    if (!isDeployed && !isRetracted) continue;

                    var evt = FlightRecorder.CheckGearTransition(
                        p.persistentId, p.partInfo?.name ?? "unknown", isDeployed, state.deployedGear, ut);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} (bg vessel {state.vesselPid})");
                    }
                    break;
                }
            }
        }

        private void CheckCargoBayState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var cargo = p.FindModuleImplementing<ModuleCargoBay>();
                if (cargo == null) continue;

                int deployIdx = cargo.DeployModuleIndex;
                if (deployIdx < 0 || deployIdx >= p.Modules.Count)
                {
                    if (state.loggedCargoBayDeployIndexIssues.Add(p.persistentId))
                    {
                        ParsekLog.Verbose("BgRecorder", $"CargoBay: invalid DeployModuleIndex for '{p.partInfo?.name}' " +
                            $"pid={p.persistentId} deployIdx={deployIdx} modules={p.Modules.Count}");
                    }
                    continue;
                }
                var animModule = p.Modules[deployIdx] as ModuleAnimateGeneric;
                if (animModule == null)
                {
                    if (state.loggedCargoBayAnimationIssues.Add(p.persistentId))
                    {
                        PartModule moduleAtIndex = p.Modules[deployIdx];
                        string moduleName = moduleAtIndex?.moduleName ?? "<null>";
                        ParsekLog.Verbose("BgRecorder", $"CargoBay: DeployModuleIndex did not resolve to ModuleAnimateGeneric for " +
                            $"'{p.partInfo?.name}' pid={p.persistentId} deployIdx={deployIdx} module='{moduleName}'");
                    }
                    continue;
                }

                FlightRecorder.ClassifyCargoBayState(animModule.animTime, cargo.closedPosition,
                    out bool isOpen, out bool isClosed);

                if (!isOpen && !isClosed)
                {
                    if (cargo.closedPosition >= 0.1f && cargo.closedPosition <= 0.9f &&
                        state.loggedCargoBayClosedPositionIssues.Add(p.persistentId))
                    {
                        ParsekLog.Verbose("BgRecorder", $"CargoBay: unsupported closedPosition for '{p.partInfo?.name}' " +
                            $"pid={p.persistentId} closedPosition={cargo.closedPosition:F3} animTime={animModule.animTime:F3}");
                    }
                    continue;
                }

                var evt = FlightRecorder.CheckCargoBayTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isOpen, state.openCargoBays, ut);
                if (evt.HasValue)
                {
                    treeRec.PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId} (bg vessel {state.vesselPid})");
                }
            }
        }

        private void CheckFairingState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var fairing = p.FindModuleImplementing<ModuleProceduralFairing>();
                if (fairing == null) continue;

                bool isDeployed;
                try
                {
                    float scalar = fairing.GetScalar;
                    if (float.IsNaN(scalar) || float.IsInfinity(scalar)) continue;
                    isDeployed = scalar >= 0.5f;
                }
                catch (Exception ex)
                {
                    if (state.loggedFairingReadFailures.Add(p.persistentId))
                    {
                        ParsekLog.Verbose("BgRecorder", $"Fairing: unable to read GetScalar for '{p.partInfo?.name}' " +
                            $"pid={p.persistentId} ({ex.GetType().Name}: {ex.Message})");
                    }
                    continue;
                }

                var evt = FlightRecorder.CheckFairingTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isDeployed, state.deployedFairings, ut);
                if (evt.HasValue)
                {
                    treeRec.PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId} (bg vessel {state.vesselPid})");
                }
            }
        }

        private void CheckEngineState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (state.cachedEngines == null) return;

            for (int i = 0; i < state.cachedEngines.Count; i++)
            {
                var (part, engine, moduleIndex) = state.cachedEngines[i];
                if (part == null || engine == null) continue;

                ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);
                bool ignited = FlightRecorder.ShouldRecordEngineAsIgnited(
                    engine.EngineIgnited, engine.isOperational, engine.finalThrust);
                float recordedPower = FlightRecorder.ComputeRecordedEnginePower(
                    engine.currentThrottle, engine.finalThrust, engine.maxThrust);

                reusableEventBuffer.Clear();
                FlightRecorder.CheckEngineTransition(
                    key, part.persistentId, moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    ignited, recordedPower,
                    state.activeEngineKeys, state.lastThrottle, ut, reusableEventBuffer);

                for (int e = 0; e < reusableEventBuffer.Count; e++)
                {
                    treeRec.PartEvents.Add(reusableEventBuffer[e]);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {reusableEventBuffer[e].eventType} '{reusableEventBuffer[e].partName}' " +
                        $"pid={reusableEventBuffer[e].partPersistentId} midx={reusableEventBuffer[e].moduleIndex} " +
                        $"val={reusableEventBuffer[e].value:F2} (bg vessel {state.vesselPid})");

                    // Engine-ignite jettison fallback (same as FlightRecorder)
                    if (reusableEventBuffer[e].eventType == PartEventType.EngineIgnited &&
                        part.FindModuleImplementing<ModuleJettison>() != null)
                    {
                        var shroudEvt = FlightRecorder.CheckJettisonTransition(
                            part.persistentId,
                            part.partInfo?.name ?? "unknown",
                            isJettisoned: true,
                            jettisonedSet: state.jettisonedShrouds,
                            ut: ut);
                        if (shroudEvt.HasValue)
                        {
                            treeRec.PartEvents.Add(shroudEvt.Value);
                            ParsekLog.Verbose("BgRecorder", $"Part event: {shroudEvt.Value.eventType} '{shroudEvt.Value.partName}' " +
                                $"pid={shroudEvt.Value.partPersistentId} (engine-ignite fallback, bg vessel {state.vesselPid})");
                        }
                    }
                }
            }
        }

        private void CheckRcsState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (state.cachedRcsModules == null) return;

            for (int i = 0; i < state.cachedRcsModules.Count; i++)
            {
                var (part, rcs, moduleIndex) = state.cachedRcsModules[i];
                if (part == null || rcs == null) continue;

                ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);
                bool active = rcs.rcs_active && rcs.rcsEnabled;
                uint pid = part.persistentId;
                string partName = part.partInfo?.name ?? "unknown";

                reusableEventBuffer.Clear();
                FlightRecorder.ProcessRcsDebounce(
                    key, pid, moduleIndex, partName,
                    active, rcs.thrustForces, rcs.thrusterPower, ut,
                    state.rcsActiveFrameCount, state.activeRcsKeys, state.lastRcsThrottle, reusableEventBuffer);
                for (int e = 0; e < reusableEventBuffer.Count; e++)
                {
                    treeRec.PartEvents.Add(reusableEventBuffer[e]);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {reusableEventBuffer[e].eventType} '{reusableEventBuffer[e].partName}' " +
                        $"pid={reusableEventBuffer[e].partPersistentId} midx={reusableEventBuffer[e].moduleIndex} " +
                        $"val={reusableEventBuffer[e].value:F2} (bg vessel {state.vesselPid})");
                }
            }
        }

        private void CheckLadderState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "RetractableLadder", StringComparison.Ordinal))
                        continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    bool isDeployed;
                    bool isRetracted;
                    if (!FlightRecorder.TryClassifyRetractableLadderState(module, out isDeployed, out isRetracted))
                    {
                        if (state.loggedLadderClassificationMisses.Add(key))
                        {
                            ParsekLog.Verbose("BgRecorder", $"Ladder: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }

                    var evt = FlightRecorder.CheckLadderTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, state.deployedLadders, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} (ladder, bg vessel {state.vesselPid})");
                    }

                    break; // one RetractableLadder module per ladder part
                }
            }
        }

        private void CheckAnimationGroupState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "ModuleAnimationGroup", StringComparison.Ordinal))
                        continue;

                    bool isDeployed;
                    bool isRetracted;
                    if (!FlightRecorder.TryClassifyAnimationGroupState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (state.loggedAnimationGroupClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("BgRecorder", $"AnimationGroup: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted) continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, state.deployedAnimationGroups, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (animation-group, bg vessel {state.vesselPid})");
                    }
                }
            }
        }

        private void CheckAeroSurfaceState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "ModuleAeroSurface", StringComparison.Ordinal))
                        continue;

                    bool isDeployed;
                    bool isRetracted;
                    if (!FlightRecorder.TryClassifyAeroSurfaceState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (state.loggedAeroSurfaceClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("BgRecorder", $"AeroSurface: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted) continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, state.deployedAeroSurfaceModules, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (aero-surface, bg vessel {state.vesselPid})");
                    }
                }
            }
        }

        private void CheckControlSurfaceState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "ModuleControlSurface", StringComparison.Ordinal))
                        continue;

                    bool isDeployed;
                    bool isRetracted;
                    if (!FlightRecorder.TryClassifyControlSurfaceState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (state.loggedControlSurfaceClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("BgRecorder", $"ControlSurface: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted) continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, state.deployedControlSurfaceModules, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (control-surface, bg vessel {state.vesselPid})");
                    }
                }
            }
        }

        private void CheckRobotArmScannerState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "ModuleRobotArmScanner", StringComparison.Ordinal))
                        continue;

                    bool isDeployed;
                    bool isRetracted;
                    if (!FlightRecorder.TryClassifyRobotArmScannerState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (state.loggedRobotArmScannerClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("BgRecorder", $"RobotArmScanner: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted) continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, state.deployedRobotArmScannerModules, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (robot-arm-scanner, bg vessel {state.vesselPid})");
                    }
                }
            }
        }

        private void CheckAnimateHeatState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "ModuleAnimateHeat", StringComparison.Ordinal))
                        continue;

                    if (!FlightRecorder.TryClassifyAnimateHeatState(
                        module, out float normalizedHeat, out string sourceField))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (state.loggedAnimateHeatClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("BgRecorder", $"AnimateHeat: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimateHeatTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        normalizedHeat, state.animateHeatLevels, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (animate-heat, bg vessel {state.vesselPid})");
                    }
                }
            }
        }

        private void CheckAnimateGenericState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                // Avoid duplicate events for modules already tracked via dedicated handlers
                bool hasDedicatedHandler =
                    p.FindModuleImplementing<ModuleDeployablePart>() != null ||
                    p.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null ||
                    p.FindModuleImplementing<ModuleCargoBay>() != null;

                bool hasRetractableLadder = false;
                bool hasAnimationGroup = false;
                bool hasAeroSurface = false;
                bool hasControlSurface = false;
                bool hasRobotArmScanner = false;
                bool hasAnimateHeat = false;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (string.Equals(module.moduleName, "RetractableLadder", StringComparison.Ordinal))
                        hasRetractableLadder = true;
                    if (string.Equals(module.moduleName, "ModuleAnimationGroup", StringComparison.Ordinal))
                        hasAnimationGroup = true;
                    if (string.Equals(module.moduleName, "ModuleAeroSurface", StringComparison.Ordinal))
                        hasAeroSurface = true;
                    if (string.Equals(module.moduleName, "ModuleControlSurface", StringComparison.Ordinal))
                        hasControlSurface = true;
                    if (string.Equals(module.moduleName, "ModuleRobotArmScanner", StringComparison.Ordinal))
                        hasRobotArmScanner = true;
                    if (string.Equals(module.moduleName, "ModuleAnimateHeat", StringComparison.Ordinal))
                        hasAnimateHeat = true;
                    if (hasRetractableLadder || hasAnimationGroup ||
                        hasAeroSurface || hasControlSurface || hasRobotArmScanner || hasAnimateHeat)
                        break;
                }
                if (hasDedicatedHandler || hasRetractableLadder || hasAnimationGroup ||
                    hasAeroSurface || hasControlSurface || hasRobotArmScanner || hasAnimateHeat) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    ModuleAnimateGeneric animateModule = p.Modules[m] as ModuleAnimateGeneric;
                    if (animateModule == null) continue;
                    if (string.IsNullOrEmpty(animateModule.animationName)) continue;

                    bool isDeployed;
                    bool isRetracted;
                    if (!FlightRecorder.TryClassifyAnimateGenericState(animateModule, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (state.loggedAnimateGenericClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("BgRecorder", $"AnimateGeneric: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted) continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, state.deployedAnimateGenericModules, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (anim-generic, bg vessel {state.vesselPid})");
                    }
                }
            }
        }

        private void CheckRoboticState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (state.cachedRoboticModules == null) return;

            for (int i = 0; i < state.cachedRoboticModules.Count; i++)
            {
                var (part, module, moduleIndex, moduleName) = state.cachedRoboticModules[i];
                if (part == null || module == null) continue;

                ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);

                bool hasPosition = FlightRecorder.TryGetRoboticPositionValue(
                    module, moduleName, out float positionValue, out float deadband, out string sourceField);

                if (!hasPosition) continue;

                bool hasMovingSignal = FlightRecorder.TryGetRoboticMovingState(module, out bool movingSignal);

                var events = FlightRecorder.CheckRoboticTransition(
                    key, part.persistentId, moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    movingSignal, positionValue, deadband, ut,
                    state.activeRoboticKeys, state.lastRoboticPosition,
                    state.lastRoboticSampleUT, roboticSampleIntervalSeconds);

                for (int e = 0; e < events.Count; e++)
                {
                    treeRec.PartEvents.Add(events[e]);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {events[e].eventType} '{events[e].partName}' " +
                        $"pid={events[e].partPersistentId} midx={events[e].moduleIndex} " +
                        $"val={events[e].value:F3} (bg vessel {state.vesselPid})");
                }
            }
        }

        internal int AppendStructuralEventSnapshot(
            double eventUT,
            IEnumerable<Vessel> involved,
            string eventType,
            bool preferRootPartSurfacePose = false)
        {
            if (tree == null || involved == null) return 0;

            int considered = 0;
            int backgroundMatches = 0;
            int loadedMatches = 0;
            int recordingMatches = 0;
            int rejected = 0;
            int deduped = 0;
            int appended = 0;

            foreach (Vessel v in involved)
            {
                considered++;
                if (v == null) continue;

                uint pid = v.persistentId;
                string recordingId;
                if (!tree.BackgroundMap.TryGetValue(pid, out recordingId))
                    continue;
                backgroundMatches++;

                BackgroundVesselState state;
                if (!loadedStates.TryGetValue(pid, out state))
                    continue;
                loadedMatches++;

                Recording treeRec;
                if (!tree.Recordings.TryGetValue(recordingId, out treeRec))
                    continue;
                recordingMatches++;

                // Mirrors the foreground recorder's same-UT structural dedup:
                // multiple structural events inside one Part.Undock() call fire
                // at the same physics-clock UT and would each append an
                // identical flagged point. Equal-UT appends pass the #419
                // tolerance but break strictly-increasing consumers
                // (Catmull-Rom section fit).
                if (FlightRecorder.HasStructuralEventSnapshotAtTail(treeRec.Points, eventUT))
                {
                    ParsekLog.Verbose("Pipeline-Smoothing",
                        string.Format(CultureInfo.InvariantCulture,
                            "BG structural event snapshot deduped: same-UT structural point already " +
                            "committed (event={0} ut={1:R} vesselId={2} recId={3})",
                            eventType ?? "unknown",
                            eventUT,
                            pid,
                            recordingId ?? "<null>"));
                    deduped++;
                    continue;
                }

                Vector3 velocity = v.packed
                    ? (Vector3)v.obt_velocity
                    : (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity());
                TrajectoryPoint point = CreateAbsoluteTrajectoryPointFromVessel(
                    v,
                    eventUT,
                    velocity,
                    preferRootPartSurfacePose);
                point = FlightRecorder.ApplyStructuralEventFlag(point);
                TryCanonicalizeBackgroundReFlyRecordingPoint(
                    recordingId,
                    ref point,
                    "structural-event-" + (eventType ?? "unknown"));
                TrajectoryPoint absolutePoint = point;
                bool relativeApplied = ApplyBackgroundRelativeOffset(state, ref point, v, eventUT);
                ApplySurfaceClearanceToPoint(state, v, ref point);
                // Body-fixed shadow gets clearance too when
                // the active section is RELATIVE (see helper docstring).
                if (relativeApplied)
                {
                    ApplySurfaceClearanceToBodyFixedShadow(state, v, ref absolutePoint);
                }

                if (!ApplyTrajectoryPointToRecording(treeRec, point))
                {
                    rejected++;
                    continue;
                }

                AppendFrameToCurrentTrackSection(
                    state,
                    point,
                    relativeApplied ? (TrajectoryPoint?)absolutePoint : null);
                state.lastRecordedUT = point.ut;
                state.lastRecordedVelocity = point.velocity;
                if (v.transform != null)
                {
                    state.lastWorldRotation = TrajectoryMath.SanitizeQuaternion(v.transform.rotation);
                    state.hasLastWorldRotation = true;
                }
                ActivateBackgroundHighFidelitySampling(
                    state,
                    eventUT,
                    $"structural-event-{eventType ?? "unknown"}");
                appended++;

                ParsekLog.Verbose("Pipeline-Smoothing",
                    string.Format(CultureInfo.InvariantCulture,
                        "BG structural event snapshot appended: event={0} ut={1:R} vesselId={2} recId={3} " +
                        "flags={4} lat={5:R} lon={6:R} alt={7:R} relativeApplied={8}",
                        eventType ?? "unknown",
                        point.ut,
                        pid,
                        recordingId ?? "<null>",
                        point.flags,
                        point.latitude,
                        point.longitude,
                        point.altitude,
                        relativeApplied ? "true" : "false"));
            }

            if (considered > appended + deduped)
            {
                ParsekLog.Verbose("Pipeline-Smoothing",
                    string.Format(CultureInfo.InvariantCulture,
                        "BG structural event snapshot skipped: event={0} ut={1:R} considered={2} " +
                        "appended={3} backgroundMatches={4} loadedMatches={5} recordingMatches={6} " +
                        "rejected={7} deduped={8}",
                        eventType ?? "unknown",
                        eventUT,
                        considered,
                        appended,
                        backgroundMatches,
                        loadedMatches,
                        recordingMatches,
                        rejected,
                        deduped));
            }

            return appended;
        }

        #endregion

        #region Jettison Name Cache

        /// <summary>
        /// Per-vessel jettison name cache (mirrors FlightRecorder.GetCachedJettisonNames).
        /// </summary>
        private static string[] GetCachedJettisonNames(BackgroundVesselState state, ulong moduleKey, string rawNames)
        {
            rawNames = rawNames ?? string.Empty;
            string cachedRaw;
            if (state.jettisonNameRawCache.TryGetValue(moduleKey, out cachedRaw) &&
                string.Equals(cachedRaw, rawNames, StringComparison.Ordinal) &&
                state.parsedJettisonNamesCache.TryGetValue(moduleKey, out string[] cachedNames))
            {
                return cachedNames;
            }

            string[] parsedNames = ParseJettisonNames(rawNames);
            state.jettisonNameRawCache[moduleKey] = rawNames;
            state.parsedJettisonNamesCache[moduleKey] = parsedNames;
            return parsedNames;
        }

        // ParseJettisonNames: use FlightRecorder.ParseJettisonNames (internal static)
        private static string[] ParseJettisonNames(string rawNames)
            => FlightRecorder.ParseJettisonNames(rawNames);

        #endregion

        private void AdoptInheritedFinalizationCache(
            uint vesselPid,
            string recordingId,
            RecordingFinalizationCache inheritedCache)
        {
            if (inheritedCache == null)
                return;

            inheritedCache.RecordingId = recordingId;
            inheritedCache.VesselPersistentId = vesselPid;
            finalizationCaches[vesselPid] = inheritedCache;

            ParsekLog.Verbose("FinalizerCache",
                $"Inherited active cache for background pid={vesselPid} rec={recordingId ?? "(null)"} " +
                $"owner={inheritedCache.Owner} status={inheritedCache.Status} " +
                $"terminal={inheritedCache.TerminalState?.ToString() ?? "(null)"}");
        }

        private static RecordingFinalizationCache CopyAndAlignFinalizationCacheIdentity(
            RecordingFinalizationCache cache,
            Recording recording)
        {
            if (cache == null)
                return null;

            RecordingFinalizationCache aligned = CopyFinalizationCache(cache);
            if (recording == null)
                return aligned;

            if (string.IsNullOrEmpty(aligned.RecordingId))
                aligned.RecordingId = recording.RecordingId;
            if (aligned.VesselPersistentId == 0)
                aligned.VesselPersistentId = recording.VesselPersistentId;

            return aligned;
        }

        private static RecordingFinalizationCache CopyFinalizationCache(RecordingFinalizationCache source)
        {
            if (source == null)
                return null;

            return new RecordingFinalizationCache
            {
                RecordingId = source.RecordingId,
                VesselPersistentId = source.VesselPersistentId,
                Owner = source.Owner,
                Status = source.Status,
                CachedAtUT = source.CachedAtUT,
                CachedAtRealtime = source.CachedAtRealtime,
                RefreshReason = source.RefreshReason,
                DeclineReason = source.DeclineReason,
                LastObservedUT = source.LastObservedUT,
                LastObservedBodyName = source.LastObservedBodyName,
                LastSituation = source.LastSituation,
                LastWasInAtmosphere = source.LastWasInAtmosphere,
                LastHadMeaningfulThrust = source.LastHadMeaningfulThrust,
                LastObservedOrbitDigest = source.LastObservedOrbitDigest,
                TailStartsAtUT = source.TailStartsAtUT,
                TerminalUT = source.TerminalUT,
                TerminalState = source.TerminalState,
                TerminalBodyName = source.TerminalBodyName,
                TerminalOrbit = source.TerminalOrbit,
                TerminalPosition = source.TerminalPosition,
                TerrainHeightAtEnd = source.TerrainHeightAtEnd,
                PredictedSegments = source.PredictedSegments != null
                    ? new List<OrbitSegment>(source.PredictedSegments)
                    : new List<OrbitSegment>()
            };
        }

        private bool RefreshFinalizationCacheForVessel(
            Vessel vessel,
            string recordingId,
            FinalizationCacheOwner owner,
            string reason,
            bool force)
        {
            if (vessel == null || string.IsNullOrEmpty(recordingId))
                return false;

            double ut = Planetarium.GetUniversalTime();
            uint vesselPid = vessel.persistentId;
            Recording treeRec = null;
            if (tree != null && tree.Recordings != null)
                tree.Recordings.TryGetValue(recordingId, out treeRec);

            BackgroundVesselState loadedState;
            loadedStates.TryGetValue(vesselPid, out loadedState);
            bool hasMeaningfulThrust = loadedState != null
                && RecordingFinalizationCacheProducer.HasMeaningfulThrust(
                    loadedState.activeEngineKeys,
                    loadedState.lastThrottle,
                    loadedState.activeRcsKeys,
                    loadedState.lastRcsThrottle);
            bool inAtmosphere = RecordingFinalizationCacheProducer.IsInAtmosphere(vessel);
            string currentDigest = RecordingFinalizationCacheProducer.BuildVesselDigest(
                vessel,
                hasMeaningfulThrust);

            RecordingFinalizationCache previous;
            finalizationCaches.TryGetValue(vesselPid, out previous);
            bool requiresPeriodicRefresh = hasMeaningfulThrust
                || inAtmosphere
                || RecordingFinalizationCacheProducer.IsSurfaceTerminalSituation(vessel.situation)
                || previous == null
                || previous.Status == FinalizationCacheStatus.Failed;

            if (!RecordingFinalizationCacheProducer.ShouldRefresh(
                    previous != null ? previous.CachedAtUT : double.MinValue,
                    ut,
                    RecordingFinalizationCacheProducer.DefaultRefreshIntervalUT,
                    force,
                    previous?.LastObservedOrbitDigest,
                    currentDigest,
                    requiresPeriodicRefresh))
            {
                TryTouchSkippedFinalizationCache(
                    previous,
                    ut,
                    reason,
                    currentDigest,
                    requiresPeriodicRefresh);
                return false;
            }

            RecordingFinalizationCache refreshed;
            bool success = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                treeRec,
                vessel,
                ut,
                owner,
                reason,
                hasMeaningfulThrust,
                tree,
                out refreshed);

            refreshed.RecordingId = recordingId;
            refreshed.VesselPersistentId = vesselPid;
            if (!success
                && RecordingFinalizationCacheProducer.IsAlreadyClassifiedDestroyedSkip(refreshed))
            {
                finalizationCaches[vesselPid] = refreshed;
                return false;
            }

            if (!success
                && RecordingFinalizationCacheProducer.TryPreservePreviousCacheAfterFailedRefresh(
                    previous,
                    refreshed,
                    ut,
                    reason,
                    currentDigest))
            {
                finalizationCaches[vesselPid] = previous;
                return false;
            }
            finalizationCaches[vesselPid] = refreshed;
            return success;
        }

        internal static bool TryTouchSkippedFinalizationCache(
            RecordingFinalizationCache previous,
            double currentUT,
            string reason,
            string currentDigest,
            bool requiresPeriodicRefresh)
        {
            if (requiresPeriodicRefresh)
                return false;

            return RecordingFinalizationCacheProducer.TryTouchObservedTerminalCache(
                previous,
                currentUT,
                reason,
                currentDigest);
        }

        private bool RefreshOnRailsFinalizationCache(
            BackgroundOnRailsState state,
            double currentUT,
            string reason,
            bool force)
        {
            if (state == null || !state.hasOpenOrbitSegment)
                return false;

            string currentDigest = RecordingFinalizationCacheProducer.BuildOrbitSegmentDigest(
                state.currentOrbitSegment);
            RecordingFinalizationCache previous;
            finalizationCaches.TryGetValue(state.vesselPid, out previous);
            // Failed caches include already-Destroyed skip caches; refresh them on cadence
            // so the rate-limited skip diagnostic and per-refresh summary remain visible.
            bool requiresPeriodicRefresh = previous == null
                || previous.Status == FinalizationCacheStatus.Failed;
            if (!RecordingFinalizationCacheProducer.ShouldRefresh(
                    previous != null ? previous.CachedAtUT : double.MinValue,
                    currentUT,
                    RecordingFinalizationCacheProducer.DefaultRefreshIntervalUT,
                    force,
                    previous?.LastObservedOrbitDigest,
                    currentDigest,
                    requiresPeriodicRefresh))
            {
                TryTouchSkippedFinalizationCache(
                    previous,
                    currentUT,
                    reason,
                    currentDigest,
                    requiresPeriodicRefresh);
                return false;
            }

            Recording treeRec = null;
            if (tree != null && tree.Recordings != null && !string.IsNullOrEmpty(state.recordingId))
                tree.Recordings.TryGetValue(state.recordingId, out treeRec);

            RecordingFinalizationCache skipped;
            if (RecordingFinalizationCacheProducer.TryBuildAlreadyClassifiedDestroyedSkip(
                    treeRec,
                    state.vesselPid,
                    FinalizationCacheOwner.BackgroundOnRails,
                    currentUT,
                    reason,
                    out skipped))
            {
                skipped.LastObservedOrbitDigest = currentDigest;
                skipped.LastObservedBodyName = state.currentOrbitSegment.bodyName;
                skipped.LastSituation = Vessel.Situations.ORBITING;
                finalizationCaches[state.vesselPid] = skipped;
                RecordingFinalizationCacheProducer.LogRefreshSummary(
                    FinalizationCacheOwner.BackgroundOnRails,
                    reason,
                    treeRec != null ? 1 : 0,
                    1,
                    0,
                    skipped.RecordingId,
                    skipped.VesselPersistentId,
                    skipped.Status,
                    skipped.TerminalState,
                    skipped.DeclineReason,
                    skipped.PredictedSegments?.Count ?? -1);
                return false;
            }

            RecordingFinalizationCache refreshed;
            bool success = RecordingFinalizationCacheProducer.TryBuildFromStableOrbitSegment(
                state.recordingId,
                state.vesselPid,
                state.currentOrbitSegment,
                currentUT,
                FinalizationCacheOwner.BackgroundOnRails,
                reason,
                out refreshed);
            finalizationCaches[state.vesselPid] = refreshed;
            return success;
        }

        #region Testing Support

        // Expose internal state for testing
        internal int OnRailsStateCount => onRailsStates.Count;
        internal int LoadedStateCount => loadedStates.Count;
        internal int FinalizationCacheCount => finalizationCaches.Count;

        internal bool HasOnRailsState(uint pid) => onRailsStates.ContainsKey(pid);
        internal bool HasLoadedState(uint pid) => loadedStates.ContainsKey(pid);
        internal bool HasFinalizationCache(uint pid) => finalizationCaches.ContainsKey(pid);

        internal RecordingFinalizationCache GetFinalizationCacheForTesting(uint pid)
        {
            RecordingFinalizationCache cache;
            return finalizationCaches.TryGetValue(pid, out cache) ? cache : null;
        }

        internal void CloseParentRecordingForTesting(Recording parentRec, uint parentPid, string branchPointId,
            double branchUT, TrajectoryPoint? parentBoundaryPoint = null)
        {
            CloseParentRecording(parentRec, parentPid, branchPointId, branchUT, parentBoundaryPoint);
        }

        internal bool GetOnRailsHasOpenSegment(uint pid)
        {
            BackgroundOnRailsState state;
            return onRailsStates.TryGetValue(pid, out state) && state.hasOpenOrbitSegment;
        }

        internal bool GetOnRailsIsLanded(uint pid)
        {
            BackgroundOnRailsState state;
            return onRailsStates.TryGetValue(pid, out state) && state.isLanded;
        }

        internal double GetOnRailsLastExplicitEndUpdate(uint pid)
        {
            BackgroundOnRailsState state;
            if (onRailsStates.TryGetValue(pid, out state))
                return state.lastExplicitEndUpdate;
            return -1;
        }

        internal bool IsPartEventsSubscribed => partEventsSubscribed;

        /// <summary>
        /// For testing: injects a loaded state for a vessel PID so that
        /// OnBackgroundPartDie / OnBackgroundPartJointBreak can find it
        /// without needing a real KSP Vessel.
        /// </summary>
        internal void InjectLoadedStateForTesting(uint vesselPid, string recordingId)
        {
            loadedStates[vesselPid] = new BackgroundVesselState
            {
                vesselPid = vesselPid,
                recordingId = recordingId,
            };
        }

        /// <summary>
        /// For testing: gets the decoupledPartIds set for a given vessel PID.
        /// Returns null if no loaded state exists.
        /// </summary>
        internal HashSet<uint> GetDecoupledPartIdsForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.decoupledPartIds;
            return null;
        }

        /// <summary>
        /// For testing: injects an open orbit segment into the on-rails state for a vessel PID.
        /// The vessel must already have an on-rails state (created by constructor or OnVesselBackgrounded).
        /// </summary>
        internal void InjectOpenOrbitSegmentForTesting(uint vesselPid, OrbitSegment segment)
        {
            BackgroundOnRailsState state;
            if (onRailsStates.TryGetValue(vesselPid, out state))
            {
                state.currentOrbitSegment = segment;
                state.hasOpenOrbitSegment = true;
            }
        }

        internal bool RefreshOnRailsFinalizationCacheForTesting(
            uint vesselPid,
            double currentUT,
            bool force = false)
        {
            BackgroundOnRailsState state;
            return onRailsStates.TryGetValue(vesselPid, out state)
                && RefreshOnRailsFinalizationCache(state, currentUT, "test_on_rails", force);
        }

        internal void AdoptFinalizationCacheForTesting(
            uint vesselPid,
            string recordingId,
            RecordingFinalizationCache cache)
        {
            AdoptInheritedFinalizationCache(vesselPid, recordingId, cache);
        }

        /// <summary>
        /// For testing: overrides the vessel finder used by CheckpointAllVessels.
        /// Set to null to restore default behavior (FlightRecorder.FindVesselByPid).
        /// </summary>
        internal void SetVesselFinderForTesting(System.Func<uint, Vessel> finder)
        {
            vesselFinderOverride = finder;
        }

        /// <summary>
        /// For testing: overrides the distance-to-focused-vessel computation.
        /// Set to null to restore default behavior (FlightGlobals.ActiveVessel distance).
        /// </summary>
        internal void SetDistanceOverrideForTesting(System.Func<Vessel, double> distanceFunc)
        {
            distanceOverrideForTesting = distanceFunc;
        }

        /// <summary>
        /// For testing: gets the current proximity sample interval for a loaded vessel.
        /// Returns double.MaxValue if no loaded state exists.
        /// </summary>
        internal double GetCurrentSampleIntervalForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.currentSampleInterval;
            return double.MaxValue;
        }

        /// <summary>
        /// For testing: gets the debris TTL expiry for a vessel PID.
        /// Returns double.NaN if not tracked.
        /// </summary>
        internal double GetDebrisTTLExpiryForTesting(uint vesselPid)
        {
            double expiry;
            if (debrisTTLExpiry.TryGetValue(vesselPid, out expiry))
                return expiry;
            return double.NaN;
        }

        /// <summary>
        /// For testing: injects a debris TTL expiry for a vessel PID.
        /// </summary>
        internal void InjectDebrisTTLForTesting(uint vesselPid, double expiry)
        {
            debrisTTLExpiry[vesselPid] = expiry;
        }

        /// <summary>
        /// For testing: gets the count of pending background split checks.
        /// </summary>
        internal int PendingSplitCheckCount => pendingBackgroundSplitChecks.Count;

        /// <summary>
        /// For testing: gets the count of debris TTL entries.
        /// </summary>
        internal int DebrisTTLCount => debrisTTLExpiry.Count;

        internal int PendingInitialEnvironmentOverrideCount => pendingInitialEnvironmentOverrides.Count;
        internal int PendingInitialTrajectoryPointCount => pendingInitialTrajectoryPoints.Count;

        internal SegmentEnvironment? PeekPendingInitialEnvironmentOverrideForTesting(uint vesselPid)
        {
            SegmentEnvironment env;
            if (pendingInitialEnvironmentOverrides.TryGetValue(vesselPid, out env))
                return env;
            return null;
        }

        internal TrajectoryPoint? PeekPendingInitialTrajectoryPointForTesting(uint vesselPid)
        {
            TrajectoryPoint point;
            if (pendingInitialTrajectoryPoints.TryGetValue(vesselPid, out point))
                return point;
            return null;
        }

        internal SegmentEnvironment? ConsumePendingInitialEnvironmentOverrideForTesting(uint vesselPid)
        {
            SegmentEnvironment env;
            if (TryConsumePendingInitialEnvironmentOverride(vesselPid, out env))
                return env;
            return null;
        }

        internal TrajectoryPoint? ConsumePendingInitialTrajectoryPointForTesting(uint vesselPid)
        {
            TrajectoryPoint point;
            if (TryConsumePendingInitialTrajectoryPoint(vesselPid, out point))
                return point;
            return null;
        }

        /// <summary>
        /// For testing: gets the accumulated TrackSections for a loaded vessel.
        /// Returns null if no loaded state exists.
        /// </summary>
        internal List<TrackSection> GetTrackSectionsForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.trackSections;
            return null;
        }

        /// <summary>
        /// For testing: checks if a loaded vessel has an active TrackSection.
        /// Returns false if no loaded state exists.
        /// </summary>
        internal bool GetTrackSectionActiveForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.trackSectionActive;
            return false;
        }

        /// <summary>
        /// For testing: gets the current TrackSection for a loaded vessel.
        /// Returns null/default if no loaded state exists.
        /// </summary>
        internal TrackSection? GetCurrentTrackSectionForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.currentTrackSection;
            return null;
        }

        /// <summary>
        /// For testing: gets the last recorded UT baseline for a loaded vessel.
        /// Returns double.NaN if no loaded state exists.
        /// </summary>
        internal double GetLastRecordedUTForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.lastRecordedUT;
            return double.NaN;
        }

        /// <summary>
        /// For testing: gets the last recorded velocity baseline for a loaded vessel.
        /// Returns Vector3.zero if no loaded state exists.
        /// </summary>
        internal Vector3 GetLastRecordedVelocityForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.lastRecordedVelocity;
            return Vector3.zero;
        }

        /// <summary>
        /// For testing: injects a loaded state with environment tracking initialized.
        /// Creates the state, sets up EnvironmentHysteresis, and opens the first TrackSection.
        /// </summary>
        internal void InjectLoadedStateWithEnvironmentForTesting(
            uint vesselPid, string recordingId, SegmentEnvironment initialEnv, double ut,
            TrajectoryPoint? initialPoint = null,
            ReferenceFrame initialReferenceFrame = ReferenceFrame.Absolute,
            string anchorRecordingId = null,
            TrajectoryPoint? bodyFixedInitialPoint = null)
        {
            var state = new BackgroundVesselState
            {
                vesselPid = vesselPid,
                recordingId = recordingId,
            };
            state.environmentHysteresis = new EnvironmentHysteresis(initialEnv);
            if (initialReferenceFrame == ReferenceFrame.Relative
                && !string.IsNullOrWhiteSpace(anchorRecordingId))
            {
                SetBackgroundCurrentAnchor(
                    state,
                    new RecordingAnchorCandidate(
                        anchorRecordingId,
                        Vector3d.zero,
                        Quaternion.identity,
                        AnchorCandidateSource.Ghost));
            }
            StartBackgroundTrackSection(state, initialEnv, initialReferenceFrame,
                initialPoint.HasValue ? initialPoint.Value.ut : ut);
            if (initialReferenceFrame == ReferenceFrame.Relative)
                ApplyBackgroundCurrentAnchorToTrackSection(state);
            if (initialPoint.HasValue)
            {
                Recording treeRec;
                if (tree != null && tree.Recordings.TryGetValue(recordingId, out treeRec))
                    ApplyInitialTrajectoryPoint(
                        state,
                        treeRec,
                        initialPoint.Value,
                        bodyFixedInitialPoint);
                else
                    AppendFrameToCurrentTrackSection(
                        state,
                        initialPoint.Value,
                        bodyFixedInitialPoint);
            }
            loadedStates[vesselPid] = state;
        }

        /// <summary>
        /// For testing: injects an on-rails state and consumes any queued initial
        /// trajectory point into the target recording, mirroring InitializeOnRailsState's
        /// seed-persistence behavior without requiring a live KSP Vessel.
        /// </summary>
        internal void InjectOnRailsStateForTesting(uint vesselPid, string recordingId, double ut)
        {
            TrajectoryPoint point;
            if (TryConsumePendingInitialTrajectoryPoint(vesselPid, out point))
            {
                if (point.ut > ut)
                    point.ut = ut;

                Recording treeRec;
                if (tree != null && tree.Recordings.TryGetValue(recordingId, out treeRec))
                    ApplyTrajectoryPointToRecording(treeRec, point);
            }

            onRailsStates[vesselPid] = new BackgroundOnRailsState
            {
                vesselPid = vesselPid,
                recordingId = recordingId,
                hasOpenOrbitSegment = false,
                isLanded = false,
                lastExplicitEndUpdate = ut
            };
        }

        internal void InjectCurrentTrackSectionFrameForTesting(uint vesselPid, TrajectoryPoint point)
        {
            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(vesselPid, out state))
                return;

            AddFrameToActiveTrackSection(state, point);
            loadedStates[vesselPid] = state;
        }

        internal void StartRelativeTrackSectionForTesting(
            uint vesselPid,
            string anchorRecordingId,
            SegmentEnvironment env,
            double ut)
        {
            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(vesselPid, out state))
                return;

            SetBackgroundCurrentAnchor(
                state,
                new RecordingAnchorCandidate(
                    anchorRecordingId,
                    Vector3d.zero,
                    Quaternion.identity,
                    AnchorCandidateSource.Ghost));
            if (state.trackSectionActive)
                CloseBackgroundTrackSection(state, ut);

            StartBackgroundTrackSection(state, env, ReferenceFrame.Relative, ut);
            ApplyBackgroundCurrentAnchorToTrackSection(state);
            loadedStates[vesselPid] = state;
        }

        internal void StartDebrisParentRelativeTrackSectionForTesting(
            uint vesselPid,
            string parentRecordingId,
            SegmentEnvironment env,
            double ut)
        {
            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(vesselPid, out state))
                return;

            state.isRelativeMode = true;
            state.currentAnchorRecordingId = parentRecordingId;
            state.currentAnchorCandidate = default;
            state.hasCurrentAnchorCandidate = false;
            StartDebrisParentRelativeTrackSection(state, env, ut);
            loadedStates[vesselPid] = state;
        }

        internal void FlushLoadedStateForOnRailsTransitionForTesting(
            uint vesselPid,
            SegmentEnvironment nextEnv,
            bool willHavePlayableOnRailsPayload,
            TrajectoryPoint boundaryPoint,
            double ut)
        {
            BackgroundVesselState loadedState;
            Recording flushRec;
            if (!loadedStates.TryGetValue(vesselPid, out loadedState)
                || !tree.Recordings.TryGetValue(loadedState.recordingId, out flushRec))
            {
                return;
            }

            FlushLoadedStateForOnRailsTransition(
                loadedState,
                flushRec,
                nextEnv,
                willHavePlayableOnRailsPayload,
                boundaryPoint,
                ut);
            loadedStates.Remove(vesselPid);
        }

        #endregion
    }
}
