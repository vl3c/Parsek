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
        private double lastBackgroundStateDriftCheckUT = double.MinValue;

        // Debris TTL: stop recording debris after this many seconds
        internal const double DebrisTTLSeconds = 60.0;

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

        // Reusable buffer for transition-check methods (avoids per-frame List<PartEvent> allocations)
        private readonly List<PartEvent> reusableEventBuffer = new List<PartEvent>();

        // Robotic sampling constants (mirrors FlightRecorder)
        private const double roboticSampleIntervalSeconds = 0.25;
        private const float roboticAngularDeadbandDegrees = 0.5f;
        private const float roboticLinearDeadbandMeters = 0.01f;

        // Injectable vessel finder for CheckpointAllVessels (null = use FlightRecorder.FindVesselByPid)
        private System.Func<uint, Vessel> vesselFinderOverride;

        // Injectable distance override for testing (null = use FlightGlobals.ActiveVessel)
        private System.Func<Vessel, double> distanceOverrideForTesting;

        public BackgroundRecorder(RecordingTree tree)
        {
            this.tree = tree;

            // Initialize on-rails state for each existing background vessel
            foreach (var kvp in tree.BackgroundMap)
            {
                uint vesselPid = kvp.Key;
                string recordingId = kvp.Value;

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

        // INVARIANT: on-rails BG vessels never produce env-classified TrackSections.
        // This class deliberately omits the `currentTrackSection` / `trackSections` /
        // `environmentHysteresis` fields that BackgroundVesselState (loaded mode) carries.
        // An on-rails BG vessel grazing atmosphere across N orbits therefore cannot generate
        // optimizer-splittable Atmospheric<->ExoBallistic toggles — there is no place to
        // store such a section, and no per-frame path runs while the vessel is packed.
        // See `OnBackgroundPhysicsFrame`'s early-return on `bgVessel.packed`. Adding a
        // TrackSection field here would resurrect the eccentric-orbit chain-explosion
        // failure mode flagged in `docs/dev/research/extending-rewind-to-stable-leaves.md`
        // §S16; do not.
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

            // Proximity-based sample interval tracking
            public double currentSampleInterval = ProximityRateSelector.OutOfRangeInterval;

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

            treeRec.PartEvents.Add(new PartEvent
            {
                ut = Planetarium.GetUniversalTime(),
                partPersistentId = joint.Child.persistentId,
                eventType = PartEventType.Decoupled,
                partName = joint.Child.partInfo?.name ?? "unknown"
            });
            treeRec.MarkFilesDirty();

            ParsekLog.Verbose("BgRecorder",
                $"Part joint break on background vessel: Decoupled " +
                $"'{joint.Child.partInfo?.name}' pid={joint.Child.persistentId} " +
                $"vesselPid={vesselPid} breakForce={breakForce:F1}");
            RefreshFinalizationCacheForVessel(joint.Child.vessel, recordingId, FinalizationCacheOwner.BackgroundLoaded,
                "background_joint_break", force: true);

            // Schedule a deferred split check for this background vessel.
            // KSP needs one frame to finalize vessel splits after joint breaks.
            if (!pendingBackgroundSplitChecks.ContainsKey(vesselPid))
            {
                double branchUT = Planetarium.GetUniversalTime();

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
                    Generation = parentRec.Generation
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

            // Add each child recording to tree and BackgroundMap
            RegisterChildRecordingsFromSplit(childRecordings, newVesselInfos, branchUT, parentEngineState);

            ParsekLog.Info("BgRecorder",
                $"Background split branch complete: bp={bp.Id} type={branchType} " +
                $"parentRecId={parentRecordingId} children={bp.ChildRecordingIds.Count} " +
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
        /// closes any open orbit segment, samples a final boundary point, and removes from tracking dicts.
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
                if (parentBoundaryPoint.HasValue)
                {
                    ApplyTrajectoryPointToRecording(parentRec, parentBoundaryPoint.Value);
                }
                else
                {
                    Vessel parentVessel = FlightRecorder.FindVesselByPid(parentPid);
                    if (parentVessel != null)
                    {
                        double sampleUT = Planetarium.GetUniversalTime();
                        SampleBoundaryPoint(parentVessel, parentRec, sampleUT);
                    }
                }
            }

            // Remove parent from BackgroundMap (it has branched, no longer a live recording)
            tree.BackgroundMap.Remove(parentPid);
            onRailsStates.Remove(parentPid);
            loadedStates.Remove(parentPid);
            debrisTTLExpiry.Remove(parentPid);
            finalizationCaches.Remove(parentPid);
        }

        /// <summary>
        /// Registers child recordings from a vessel split: captures snapshots, adds to tree
        /// and BackgroundMap, initializes tracking state, and sets TTL for debris children.
        /// </summary>
        private void RegisterChildRecordingsFromSplit(
            List<Recording> childRecordings,
            List<(uint pid, string name, bool hasController)> newVesselInfos,
            double branchUT,
            InheritedEngineState? inherited = null)
        {
            // Capture vessel snapshots so ghosts can be built during playback
            // (without snapshots, GhostVisualBuilder returns null → no ghost appears).
            for (int i = 0; i < childRecordings.Count; i++)
            {
                var child = childRecordings[i];

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
                    ParsekLog.Verbose("BgRecorder",
                        $"Captured snapshot for child vessel: pid={child.VesselPersistentId} " +
                        $"name='{child.VesselName}' hasSnapshot={child.VesselSnapshot != null} " +
                        $"startResources={child.StartResources?.Count ?? 0} type(s) " +
                        $"startInventory={child.StartInventory?.Count ?? 0} item(s) " +
                        $"startCrew={child.StartCrew?.Count ?? 0} trait(s)");
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

                bool hasController = newVesselInfos[i].hasController;
                if (!hasController)
                {
                    // Debris child: set TTL
                    child.IsDebris = true;
                    debrisTTLExpiry[child.VesselPersistentId] = branchUT + DebrisTTLSeconds;
                    ParsekLog.Info("BgRecorder",
                        $"Child recording created (debris, TTL={DebrisTTLSeconds:F0}s): " +
                        $"recId={child.RecordingId} vesselPid={child.VesselPersistentId} " +
                        $"name='{child.VesselName}'");
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
            List<uint> expired = null;

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
                    if (expired == null) expired = new List<uint>();
                    expired.Add(vesselPid);

                    ParsekLog.Info("BgRecorder",
                        $"Debris TTL: vessel destroyed/despawned, ending recording: " +
                        $"vesselPid={vesselPid}");
                    continue;
                }

                // Check if TTL expired
                if (currentUT >= expiry)
                {
                    if (expired == null) expired = new List<uint>();
                    expired.Add(vesselPid);

                    ParsekLog.Info("BgRecorder",
                        $"Debris TTL expired, ending recording: " +
                        $"vesselPid={vesselPid} expiry={expiry:F1} currentUT={currentUT:F1}");
                    continue;
                }

                // Check if vessel went out of physics bubble (packed + on rails)
                if (!v.loaded)
                {
                    if (expired == null) expired = new List<uint>();
                    expired.Add(vesselPid);

                    ParsekLog.Info("BgRecorder",
                        $"Debris TTL: vessel left physics bubble, ending recording: " +
                        $"vesselPid={vesselPid}");
                    continue;
                }
            }

            if (expired == null) return;

            for (int i = 0; i < expired.Count; i++)
            {
                EndDebrisRecording(expired[i], currentUT);
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
        private void EndDebrisRecording(uint vesselPid, double endUT)
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
            OnVesselRemovedFromBackground(vesselPid);

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
                RefreshOnRailsFinalizationCache(state, currentUT, "background_on_rails_periodic", force: false);

                // Update ExplicitEndUT periodically
                if (currentUT - state.lastExplicitEndUpdate >= ExplicitEndUpdateInterval)
                {
                    Recording treeRec;
                    if (tree.Recordings.TryGetValue(state.recordingId, out treeRec))
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

            // Compute distance to focused vessel and determine proximity-based sample interval
            double distance = ComputeDistanceToFocusedVessel(bgVessel);
            double proximityInterval = ProximityRateSelector.GetSampleInterval(distance);

            // Log when sample rate changes for this vessel
            if (proximityInterval != state.currentSampleInterval)
            {
                float newHz = ProximityRateSelector.GetSampleRateHz(distance);
                string distStr = distance.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                string hzStr = newHz.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                ParsekLog.Info("BgRecorder",
                    $"Sample rate changed: pid={pid} dist={distStr}m " +
                    $"interval={FormatInterval(proximityInterval)} ({hzStr} Hz)");
                state.currentSampleInterval = proximityInterval;
            }

            // Skip trajectory sampling if out of range
            if (proximityInterval >= ProximityRateSelector.OutOfRangeInterval)
            {
                return;
            }

            // Adaptive sampling (velocity-based, gated by the proximity interval as min floor).
            // proximityInterval is passed as ShouldRecordPoint's minInterval — single call
            // path for the rate floor across foreground and background. The *value* still
            // differs (foreground uses ParsekSettings.minSampleInterval; background uses the
            // proximity tier from ProximityRateSelector), but the gating logic is unified.
            Vector3 currentVelocity = (Vector3)(bgVessel.rb_velocityD + Krakensbane.GetFrameVelocity());

            float maxSampleInterval = ParsekSettings.Current?.maxSampleInterval ?? ParsekSettings.GetMaxSampleInterval(SamplingDensity.Medium);
            float velocityDirThreshold = ParsekSettings.Current?.velocityDirThreshold ?? ParsekSettings.GetVelocityDirThreshold(SamplingDensity.Medium);
            float speedChangeThreshold = (ParsekSettings.Current?.speedChangeThreshold ?? ParsekSettings.GetSpeedChangeThreshold(SamplingDensity.Medium)) / 100f;

            if (!TrajectoryMath.ShouldRecordPoint(currentVelocity, state.lastRecordedVelocity,
                ut, state.lastRecordedUT,
                (float)proximityInterval, maxSampleInterval,
                velocityDirThreshold, speedChangeThreshold))
            {
                return;
            }

            TrajectoryPoint point = CreateAbsoluteTrajectoryPointFromVessel(bgVessel, ut, currentVelocity);

            treeRec.Points.Add(point);
            treeRec.MarkFilesDirty();
            state.lastRecordedUT = point.ut;
            state.lastRecordedVelocity = point.velocity;

            // Dual-write: also add to current TrackSection's frames list
            AddFrameToActiveTrackSection(state, point);

            // Update ExplicitEndUT
            treeRec.ExplicitEndUT = ut;

            ParsekLog.VerboseRateLimited("BgRecorder", $"bgPhysics.{pid}",
                $"Background point sampled: pid={pid} pts={treeRec.Points.Count} " +
                $"alt={bgVessel.altitude:F0} dist={distance:F0}m interval={FormatInterval(proximityInterval)}", 5.0);
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
            double? ut = null;

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
                }
                loadedStates.Remove(vesselPid);
            }

            pendingInitialEnvironmentOverrides.Remove(vesselPid);
            pendingInitialTrajectoryPoints.Remove(vesselPid);
            finalizationCaches.Remove(vesselPid);
            ParsekLog.Info("BgRecorder", $"Vessel removed from background: pid={vesselPid}");
        }

        /// <summary>
        /// Called when a background vessel goes on rails.
        /// Transitions from loaded/physics mode to on-rails mode.
        /// </summary>
        public void OnBackgroundVesselGoOnRails(Vessel v)
        {
            if (v == null || tree == null) return;

            uint pid = v.persistentId;
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(pid, out recordingId) || recordingId == null) return;

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

            // Initialize on-rails state
            InitializeOnRailsState(v, pid, recordingId);
            RefreshFinalizationCacheForVessel(v, recordingId, FinalizationCacheOwner.BackgroundOnRails,
                "background_go_on_rails", force: true);
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
            UnsubscribePartEvents();

            double ut = Planetarium.GetUniversalTime();

            // Close all open orbit segments
            foreach (var kvp in onRailsStates)
            {
                var state = kvp.Value;
                if (state.hasOpenOrbitSegment)
                {
                    CloseOrbitSegment(state, ut);
                }
            }

            // Close and flush TrackSections for all loaded vessels
            foreach (var kvp in loadedStates)
            {
                var state = kvp.Value;
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
        /// Finalizes all background recordings for tree commit.
        /// Closes all open orbit segments, flushes any pending trajectory data,
        /// and sets ExplicitEndUT on all background recordings.
        /// Does NOT clear state — call Shutdown() after this if shutting down.
        /// </summary>
        public void FinalizeAllForCommit(double commitUT)
        {
            // Close all open orbit segments
            foreach (var kvp in onRailsStates)
            {
                var state = kvp.Value;
                if (state.hasOpenOrbitSegment)
                {
                    RefreshOnRailsFinalizationCache(
                        state,
                        commitUT,
                        "background_commit_on_rails",
                        force: true);
                    CloseOrbitSegment(state, commitUT);
                }
            }

            // Close and flush TrackSections for all loaded vessels, emit terminal engine/RCS/robotic events (bug #108)
            foreach (var kvp in loadedStates)
            {
                var state = kvp.Value;
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

            // Update ExplicitEndUT on all background recordings
            foreach (var kvp in tree.BackgroundMap)
            {
                Recording treeRec;
                if (tree.Recordings.TryGetValue(kvp.Value, out treeRec))
                {
                    treeRec.ExplicitEndUT = commitUT;
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

        internal bool TryApplyFinalizationCacheForBackgroundEnd(
            Recording recording,
            uint vesselPid,
            double endUT,
            string consumerPath,
            bool allowStale,
            bool requireDestroyedTerminal,
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
                "checkpoint-all-{0}-{1}-{2}",
                checkpointed, skippedNotOrbital, skippedNoVessel);
            ParsekLog.VerboseRateLimited("BgRecorder", key,
                $"CheckpointAllVessels at UT={ut:F2}: checkpointed={checkpointed}, " +
                $"skippedNotOrbital={skippedNotOrbital}, skippedNoVessel={skippedNoVessel}");
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
            TrajectoryPoint initialTrajectoryPoint;
            bool hasInitialTrajectoryPoint = TryConsumePendingInitialTrajectoryPoint(vesselPid, out initialTrajectoryPoint);
            if (hasInitialTrajectoryPoint && initialTrajectoryPoint.ut > ut)
                initialTrajectoryPoint.ut = ut;
            Recording treeRec;
            bool hasTreeRecording = tree.Recordings.TryGetValue(recordingId, out treeRec);
            if (hasInitialTrajectoryPoint && hasTreeRecording)
            {
                // #419: only log "seeded" if the flat-append was accepted. A rejection
                // already produces its own warn inside ApplyTrajectoryPointToRecording.
                if (ApplyTrajectoryPointToRecording(treeRec, initialTrajectoryPoint))
                {
                    ParsekLog.Info("BgRecorder",
                        $"Initial trajectory point seeded (on-rails): pid={vesselPid} " +
                        $"ut={initialTrajectoryPoint.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                        $"alt={initialTrajectoryPoint.altitude.ToString("F0", CultureInfo.InvariantCulture)}");
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

                ParsekLog.Verbose("BgRecorder", $"On-rails state initialized (atmosphere, skip orbit): pid={vesselPid} " +
                    $"body={v.mainBody?.name} alt={v.altitude:F0} atmoDepth={v.mainBody.atmosphereDepth:F0}");
            }
            else if (v.orbit != null)
            {
                // Open orbit segment
                state.currentOrbitSegment = CreateOrbitSegmentFromVessel(
                    v,
                    hasInitialTrajectoryPoint ? initialTrajectoryPoint.ut : ut,
                    "Kerbin");
                state.hasOpenOrbitSegment = true;

                if (hasTreeRecording)
                {
                    treeRec.ExplicitEndUT = ut;
                }

                ParsekLog.Verbose("BgRecorder", $"On-rails state initialized (orbiting): pid={vesselPid} " +
                    $"body={v.mainBody?.name} sma={v.orbit.semiMajorAxis:F1}");
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
            StartBackgroundTrackSection(state, initialEnv, ReferenceFrame.Absolute,
                hasInitialTrajectoryPoint ? initialTrajectoryPoint.ut : ut);
            if (hasInitialTrajectoryPoint)
            {
                if (hasTreeRecording)
                    ApplyInitialTrajectoryPoint(state, treeRecForSeed, initialTrajectoryPoint);
                else
                    AppendFrameToCurrentTrackSection(state, initialTrajectoryPoint);
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
                reputation = Reputation.Instance != null ? Reputation.CurrentRep : 0
            };
        }

        private void CloseOrbitSegment(BackgroundOnRailsState state, double ut)
        {
            if (!state.hasOpenOrbitSegment) return;

            state.currentOrbitSegment.endUT = ut;

            Recording treeRec;
            if (tree.Recordings.TryGetValue(state.recordingId, out treeRec))
            {
                treeRec.OrbitSegments.Add(state.currentOrbitSegment);
                treeRec.MarkFilesDirty();
                treeRec.ExplicitEndUT = ut;
            }

            state.hasOpenOrbitSegment = false;

            ParsekLog.Verbose("BgRecorder", $"Orbit segment closed: pid={state.vesselPid} " +
                $"UT={state.currentOrbitSegment.startUT:F1}-{ut:F1} body={state.currentOrbitSegment.bodyName}");
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
            if (preferRootPartSurfacePose)
                TryResolveRootPartSurfacePose(v, out latitude, out longitude, out altitude, out rotation);

            return new TrajectoryPoint
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
                reputation = Reputation.Instance != null ? Reputation.CurrentRep : 0
            };
        }

        private void FlushLoadedStateForOnRailsTransition(
            BackgroundVesselState loadedState,
            Recording flushRec,
            SegmentEnvironment nextEnv,
            bool willHavePlayableOnRailsPayload,
            TrajectoryPoint boundaryPoint,
            double ut)
        {
            SegmentEnvironment previousEnv = loadedState.trackSectionActive
                ? loadedState.currentTrackSection.environment
                : loadedState.environmentHysteresis != null
                    ? loadedState.environmentHysteresis.CurrentEnvironment
                    : SegmentEnvironment.Atmospheric;
            bool persistNoPayloadBoundarySection = ShouldPersistNoPayloadOnRailsBoundaryTrackSection(
                previousEnv, nextEnv, willHavePlayableOnRailsPayload);

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
                ParsekLog.Info("BgRecorder",
                    $"Persisted no-payload on-rails boundary section: pid={loadedState.vesselPid} " +
                    $"{previousEnv}->{nextEnv} at UT={ut.ToString("F2", CultureInfo.InvariantCulture)} (seam=1)");
            }

            FlushTrackSectionsToRecording(loadedState, flushRec);
            flushRec.ExplicitEndUT = ut;

            if (!persistNoPayloadBoundarySection)
                AppendBoundaryPointToRecording(flushRec, boundaryPoint, loadedState.vesselPid);
        }

        private void SampleBoundaryPoint(Vessel v, Recording treeRec, double ut)
        {
            if (v == null) return;

            AppendBoundaryPointToRecording(treeRec, CreateAbsoluteTrajectoryPointFromVessel(v, ut), v.persistentId);
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
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>(),
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            };
            state.trackSectionActive = true;
            ParsekLog.Info("BgRecorder",
                $"TrackSection started: env={env} ref={refFrame} source=Background " +
                $"pid={state.vesselPid} at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        private static void AppendFrameToCurrentTrackSection(BackgroundVesselState state, TrajectoryPoint point)
        {
            if (state == null || !state.trackSectionActive || state.currentTrackSection.frames == null)
                return;

            state.currentTrackSection.frames.Add(point);
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
            BackgroundVesselState state, Recording treeRec, TrajectoryPoint point)
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

            AppendFrameToCurrentTrackSection(state, point);
            state.lastRecordedUT = point.ut;
            state.lastRecordedVelocity = point.velocity;

            ParsekLog.Info("BgRecorder",
                $"Initial trajectory point seeded: pid={state.vesselPid} " +
                $"ut={point.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"alt={point.altitude.ToString("F0", CultureInfo.InvariantCulture)}");
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
            state.currentTrackSection = new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = ut,
                source = TrackSectionSource.Checkpoint,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>(),
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            };
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

        private static void AddFrameToActiveTrackSection(BackgroundVesselState state, TrajectoryPoint point)
        {
            if (!state.trackSectionActive || state.currentTrackSection.frames == null)
                return;

            state.currentTrackSection.frames.Add(point);
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

            state.trackSections.Add(state.currentTrackSection);
            state.trackSectionActive = false;

            int frameCount = state.currentTrackSection.frames?.Count ?? 0;
            int checkpointCount = state.currentTrackSection.checkpoints?.Count ?? 0;
            double sectionDuration = ut - state.currentTrackSection.startUT;
            ParsekLog.Info("BgRecorder",
                $"TrackSection closed: env={state.currentTrackSection.environment} " +
                $"ref={state.currentTrackSection.referenceFrame} " +
                $"frames={frameCount} checkpoints={checkpointCount} " +
                $"duration={sectionDuration.ToString("F2", CultureInfo.InvariantCulture)}s " +
                $"pid={state.vesselPid}");
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
                bool ignited = engine.EngineIgnited && engine.isOperational;
                float throttle = engine.currentThrottle;

                reusableEventBuffer.Clear();
                FlightRecorder.CheckEngineTransition(
                    key, part.persistentId, moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    ignited, throttle,
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
            TrajectoryPoint? initialPoint = null)
        {
            var state = new BackgroundVesselState
            {
                vesselPid = vesselPid,
                recordingId = recordingId,
            };
            state.environmentHysteresis = new EnvironmentHysteresis(initialEnv);
            StartBackgroundTrackSection(state, initialEnv, ReferenceFrame.Absolute,
                initialPoint.HasValue ? initialPoint.Value.ut : ut);
            if (initialPoint.HasValue)
            {
                Recording treeRec;
                if (tree != null && tree.Recordings.TryGetValue(recordingId, out treeRec))
                    ApplyInitialTrajectoryPoint(state, treeRec, initialPoint.Value);
                else
                    AppendFrameToCurrentTrackSection(state, initialPoint.Value);
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
