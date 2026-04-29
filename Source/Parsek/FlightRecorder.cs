using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Owns all recording state and sampling logic.
    /// Called each physics frame by the Harmony postfix on VesselPrecalculate.
    /// </summary>
    public class FlightRecorder
    {
        internal static Func<double> QuickloadResumeUTProviderForTesting;

        internal enum VesselSwitchDecision
        {
            None,
            ContinueOnEva,
            ChainToVessel,  // EVA recording -> boarded a vessel
            DockMerge,      // Vessel absorbed into dock target (pid changed)
            UndockSwitch,   // Player switched to undocked sibling vessel
            TransitionToBackground,   // active recording -> background (orbit segment)
            PromoteFromBackground     // background recording -> active (resume physics sampling)
        }

        // Recording output
        public List<TrajectoryPoint> Recording { get; } = new List<TrajectoryPoint>();
        public List<OrbitSegment> OrbitSegments { get; } = new List<OrbitSegment>();
        public List<PartEvent> PartEvents { get; } = new List<PartEvent>();
        public List<FlagEvent> FlagEvents { get; } = new List<FlagEvent>();
        public List<SegmentEvent> SegmentEvents { get; } = new List<SegmentEvent>();
        public List<TrackSection> TrackSections { get; } = new List<TrackSection>();

        // Environment tracking
        private EnvironmentHysteresis environmentHysteresis;
        private TrackSection currentTrackSection;
        private bool trackSectionActive;
        private bool pendingRestoreEnvironmentResync;
        private SegmentEnvironment restoreEnvironmentResyncTarget;

        // Phase 7 (design doc §13, §18 Phase 7): per-section diagnostics for the
        // SurfaceMobile clearance summary line emitted at section close. Reset
        // when a new section opens so each closed section emits its own line.
        private int surfaceMobileSamplesThisSection;
        private double surfaceMobileMinClearanceThisSection = double.NaN;
        private double surfaceMobileMaxClearanceThisSection = double.NaN;
        private double surfaceMobileClearanceSumThisSection;

        // Anchor detection for RELATIVE frame (Phase 3a)
        private bool isRelativeMode;
        private uint currentAnchorPid;
        private HashSet<uint> treeVesselPids;
        private List<(uint pid, Vector3d position)> vesselInfoBuffer = new List<(uint, Vector3d)>();

        // Part event tracking
        private HashSet<uint> decoupledPartIds = new HashSet<uint>();
        private Dictionary<uint, int> parachuteStates = new Dictionary<uint, int>(); // 0=stowed, 1=semi, 2=deployed
        private HashSet<uint> jettisonedShrouds = new HashSet<uint>();
        private Dictionary<ulong, string> jettisonNameRawCache = new Dictionary<ulong, string>();
        private Dictionary<ulong, string[]> parsedJettisonNamesCache = new Dictionary<ulong, string[]>();
        private HashSet<uint> extendedDeployables = new HashSet<uint>();
        private HashSet<uint> lightsOn = new HashSet<uint>();
        private HashSet<uint> blinkingLights = new HashSet<uint>();
        private Dictionary<uint, float> lightBlinkRates = new Dictionary<uint, float>();
        private HashSet<uint> deployedGear = new HashSet<uint>();
        private HashSet<uint> openCargoBays = new HashSet<uint>();
        private HashSet<uint> deployedFairings = new HashSet<uint>();
        private HashSet<ulong> deployedLadders = new HashSet<ulong>();
        private HashSet<ulong> deployedAnimationGroups = new HashSet<ulong>();
        private HashSet<ulong> deployedAnimateGenericModules = new HashSet<ulong>();
        private HashSet<ulong> deployedAeroSurfaceModules = new HashSet<ulong>();
        private HashSet<ulong> deployedControlSurfaceModules = new HashSet<ulong>();
        private HashSet<ulong> deployedRobotArmScannerModules = new HashSet<ulong>();
        private Dictionary<ulong, HeatLevel> animateHeatLevels = new Dictionary<ulong, HeatLevel>();

        // Engine state tracking (key = EncodeEngineKey(pid, moduleIndex))
        private List<(Part part, ModuleEngines engine, int moduleIndex)> cachedEngines;
        private HashSet<ulong> activeEngineKeys;
        private HashSet<ulong> allEngineKeys = new HashSet<ulong>();
        private Dictionary<ulong, float> lastThrottle;
        private HashSet<ulong> loggedEngineModuleKeys = new HashSet<ulong>();

        /// <summary>Bug #298: access to active engine/RCS state for parent-to-child
        /// inheritance during breakup. Populated by SeedExistingPartStates, NOT cleared by
        /// FinalizeRecordingState — survives StopRecordingForChainBoundary. Callers must
        /// defensively copy if they need a snapshot (these are live mutable references).</summary>
        internal HashSet<ulong> ActiveEngineKeys => activeEngineKeys;
        internal Dictionary<ulong, float> LastEngineThrottles => lastThrottle;
        internal HashSet<ulong> ActiveRcsKeys => activeRcsKeys;
        internal Dictionary<ulong, float> LastRcsThrottles => lastRcsThrottle;

        // RCS state tracking (separate dicts from engines — keys can overlap for same part)
        private List<(Part part, ModuleRCS rcs, int moduleIndex)> cachedRcsModules;
        private HashSet<ulong> activeRcsKeys;
        private Dictionary<ulong, float> lastRcsThrottle;
        private HashSet<ulong> loggedRcsModuleKeys = new HashSet<ulong>();
        // RCS debounce: filters SAS micro-corrections (1-3 frames) while preserving intentional burns (>0.15s)
        internal const int RcsDebounceFrameThreshold = 8; // ~0.15s at 50Hz
        private Dictionary<ulong, int> rcsActiveFrameCount = new Dictionary<ulong, int>();
        // Robotics state tracking (Breaking Ground; sparse sampling to limit event volume)
        private List<(Part part, PartModule module, int moduleIndex, string moduleName)> cachedRoboticModules;
        private HashSet<ulong> activeRoboticKeys;
        private Dictionary<ulong, float> lastRoboticPosition;
        private Dictionary<ulong, double> lastRoboticSampleUT;
        private HashSet<ulong> loggedRoboticModuleKeys;
        private HashSet<ulong> loggedLadderClassificationMisses = new HashSet<ulong>();
        private HashSet<ulong> loggedAnimationGroupClassificationMisses = new HashSet<ulong>();
        private HashSet<ulong> loggedAnimateGenericClassificationMisses = new HashSet<ulong>();
        private HashSet<ulong> loggedAeroSurfaceClassificationMisses = new HashSet<ulong>();
        private HashSet<ulong> loggedControlSurfaceClassificationMisses = new HashSet<ulong>();
        private HashSet<ulong> loggedRobotArmScannerClassificationMisses = new HashSet<ulong>();
        private HashSet<ulong> loggedAnimateHeatClassificationMisses = new HashSet<ulong>();
        // Reusable buffer for transition-check methods that return multiple events per call.
        // Cleared before each call to avoid per-frame List<PartEvent> allocations.
        private readonly List<PartEvent> reusableEventBuffer = new List<PartEvent>();
        private HashSet<uint> loggedCargoBayDeployIndexIssues = new HashSet<uint>();
        private HashSet<uint> loggedCargoBayAnimationIssues = new HashSet<uint>();
        private HashSet<uint> loggedCargoBayClosedPositionIssues = new HashSet<uint>();
        private HashSet<uint> loggedFairingReadFailures = new HashSet<uint>();
        internal bool partEventsSubscribed;
        public bool IsRecording { get; internal set; }

        /// <summary>
        /// When true, this recorder operates in Gloops ghost-only mode:
        /// registers with GloopsRecorderInstance instead of ActiveRecorder,
        /// skips rewind save/pre-launch resources, auto-stops on vessel switch.
        /// </summary>
        internal bool IsGloopsMode { get; set; }

        /// <summary>
        /// Stash for the ghost visual snapshot captured at Gloops recording start.
        /// Applied to the committed Recording by ParsekFlight.StopGloopsRecording().
        /// </summary>
        internal ConfigNode GloopsGhostVisualSnapshot { get; set; }

        /// <summary>
        /// Set true when the Gloops recorder is auto-stopped by a vessel switch.
        /// ParsekFlight checks this flag to auto-commit the orphaned recording.
        /// </summary>
        internal bool GloopsAutoStoppedByVesselSwitch { get; set; }

        public uint RecordingVesselId { get; private set; }
        public bool RecordingStartedAsEva { get; private set; }
        public bool VesselDestroyedDuringRecording { get; set; }
        public bool ChainToVesselPending { get; internal set; }
        public bool DockMergePending { get; set; }
        public bool UndockSwitchPending { get; set; }
        // Set by ParsekFlight to enable sibling vessel switch detection in OnPhysicsFrame
        public uint UndockSiblingPid { get; set; }

        // Tree mode: set by ParsekFlight when a recording tree is active
        public RecordingTree ActiveTree { get; set; }
        public bool TransitionToBackgroundPending { get; internal set; }

        // Joint break split detection: set by OnPartJointBreak, consumed by ParsekFlight.Update()
        public bool HasPendingJointBreakCheck { get; private set; }
        public double PendingJointBreakUT { get; private set; } = double.NaN;

        // Exact terminal events appended by the most recent FinalizeRecordingState call with
        // emitTerminalEvents=true. ResumeAfterFalseAlarm uses this list to remove the orphaned
        // terminals by content-matching rather than index arithmetic. Index-based removal
        // (see #255/#263/#281) was brittle across unstable sort paths (flush/merge) and any
        // code path that appends events between stop and resume — it caused spurious terminal
        // Shutdowns to survive into committed recordings (#287).
        internal List<PartEvent> lastEmittedTerminalEvents;

        /// <summary>
        /// Consumes the pending joint break flag, returning whether a check was pending.
        /// </summary>
        public bool ConsumePendingJointBreakCheck(out double jointBreakUT)
        {
            bool was = HasPendingJointBreakCheck;
            jointBreakUT = PendingJointBreakUT;
            HasPendingJointBreakCheck = false;
            PendingJointBreakUT = double.NaN;
            return was;
        }

        /// <summary>
        /// Clears atmosphere-boundary and SOI-change flags without stopping or restarting
        /// the recorder. Used when tree mode intercepts a boundary crossing — the recorder
        /// keeps accumulating data into the tree recording, but the standalone chain-commit
        /// path is suppressed.
        /// </summary>
        public void ClearBoundaryFlags()
        {
            AtmosphereBoundaryCrossed = false;
            EnteredAtmosphere = false;
            atmosphereBoundaryPending = false;
            AltitudeBoundaryCrossed = false;
            DescendedBelowThreshold = false;
            altitudeBoundaryPending = false;
            SoiChangePending = false;
            SoiChangeFromBody = null;
            ParsekLog.Verbose("Recorder", "Boundary flags cleared (tree mode bypass)");
        }

        /// <summary>
        /// True when the recorder is conceptually recording but not actively sampling
        /// physics frames (e.g., vessel switched away in tree mode).
        /// </summary>
        public bool IsBackgrounded => IsRecording && Patches.PhysicsFramePatch.ActiveRecorder != this;

        public Recording CaptureAtStop { get; private set; }
        public double PreLaunchFunds { get; private set; }
        public double PreLaunchScience { get; private set; }
        public float PreLaunchReputation { get; private set; }
        public string RewindSaveFileName { get; private set; }

        /// <summary>
        /// Sets <see cref="RewindSaveFileName"/> from the quickload-resume restore path,
        /// logging the transition with a reason so the state change is visible in KSP.log.
        /// The normal recording-start path uses <see cref="CaptureRewindSave"/> which
        /// writes the field directly; this setter exists only for restore.
        /// </summary>
        internal void SetRewindSaveFileNameForRestore(string fileName, string reason)
        {
            string prev = RewindSaveFileName;
            RewindSaveFileName = fileName;
            ParsekLog.Info("Recorder",
                $"RewindSaveFileName set for restore: '{prev ?? "<null>"}' → '{fileName ?? "<null>"}' ({reason})");
        }

        /// <summary>
        /// #294: Overwrites start location fields from saved standalone restore data.
        /// StartRecording captures the quickload-time location (wrong — e.g. "in orbit"),
        /// but we want the original launch location from the F5 save.
        /// </summary>
        internal void SetStartLocationForRestore(
            string bodyName, string biome, string situation, string launchSite)
        {
            StartBodyName = bodyName;
            StartBiome = biome;
            StartSituation = situation;
            LaunchSiteName = launchSite;
            ParsekLog.Verbose("Recorder",
                $"Start location restored: body={bodyName ?? "(null)"}, biome={biome ?? "(null)"}, " +
                $"situation={situation ?? "(null)"}, launchSite={launchSite ?? "(null)"}");
        }

        internal void ArmRestoreEnvironmentResync(SegmentEnvironment target, string reason)
        {
            pendingRestoreEnvironmentResync = true;
            restoreEnvironmentResyncTarget = target;
            ParsekLog.Verbose("Recorder",
                $"Restore environment resync armed: target={target} ({reason})");
        }

        internal bool TryApplyRestoreEnvironmentResync(SegmentEnvironment transitionedEnv, double ut)
        {
            if (!pendingRestoreEnvironmentResync)
                return false;

            pendingRestoreEnvironmentResync = false;
            if (transitionedEnv != restoreEnvironmentResyncTarget || !trackSectionActive)
            {
                ParsekLog.Verbose("Recorder",
                    $"Restore environment resync disarmed: transition={transitionedEnv} " +
                    $"target={restoreEnvironmentResyncTarget} trackSectionActive={trackSectionActive}");
                return false;
            }

            SegmentEnvironment previousEnv = currentTrackSection.environment;
            currentTrackSection.environment = transitionedEnv;
            ParsekLog.Info("Recorder",
                $"Restore environment resync: {previousEnv} -> {transitionedEnv} " +
                $"at UT={ut.ToString("F2", CultureInfo.InvariantCulture)} treated as restored state");
            return true;
        }

        private void PrepareQuickloadResumeStateIfNeeded()
        {
            pendingRestoreEnvironmentResync = false;

            if (ActiveTree == null || string.IsNullOrEmpty(ActiveTree.Id))
                return;

            string activeRecId = ActiveTree.ActiveRecordingId;
            if (string.IsNullOrEmpty(activeRecId)
                || !ParsekScenario.MatchesPendingQuickloadResumeContext(ActiveTree.Id))
                return;

            if (!ActiveTree.Recordings.TryGetValue(activeRecId, out Recording activeRec) || activeRec == null)
            {
                ParsekLog.Warn("Recorder",
                    $"Quickload resume prep skipped: active tree '{ActiveTree.TreeName}' " +
                    $"missing activeRecId={activeRecId}");
                ParsekScenario.ClearPendingQuickloadResumeContext();
                return;
            }

            double resumeUT = GetQuickloadResumeUT();
            double preTrimEndUT = activeRec.EndUT;
            int recordingsInTree = ActiveTree.Recordings.Count;

            // Bug #610: pick trim scope. F9 quickload trims the whole tree;
            // Re-Fly in-place continuation trims only the active rec because the
            // splice (SpliceMissingCommittedRecordings) just restored other-vessel
            // post-RP recordings that the tree-wide trim would prune again.
            // Use `?.` instead of `!= null` so the read works in unit tests where
            // ParsekScenario (a UnityEngine MonoBehaviour) is fake-destroyed by
            // Unity's overloaded `==` operator; `?.` uses reference equality.
            var marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            var trimScope = ParsekScenario.ChooseQuickloadTrimScope(
                ActiveTree.Id, marker, out string trimScopeReason);
            bool treeTrimmed;
            if (trimScope == ParsekScenario.QuickloadTrimScope.ActiveRecOnly)
            {
                treeTrimmed = ParsekScenario.TrimRecordingPastUT(activeRec, resumeUT);
            }
            else
            {
                treeTrimmed = ParsekScenario.TrimRecordingTreePastUT(ActiveTree, resumeUT);
            }

            bool hasTailEnv = TryGetTailTrackSectionEnvironment(activeRec, out SegmentEnvironment tailEnv);
            if (hasTailEnv)
                ArmRestoreEnvironmentResync(tailEnv, "quickload-resume tail environment");

            ParsekScenario.ClearPendingQuickloadResumeContext();
            ParsekLog.Info("Recorder",
                $"Quickload resume prep: activeRec='{activeRec.RecordingId}' " +
                $"cutoffUT={resumeUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"preTrimEndUT={preTrimEndUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"trimScope={trimScope} ({trimScopeReason}) " +
                $"recordingsInTree={recordingsInTree} " +
                $"treeTrimmed={treeTrimmed}" +
                (hasTailEnv ? $" envResyncTarget={tailEnv}" : ""));
        }

        private static double GetQuickloadResumeUT()
        {
            if (QuickloadResumeUTProviderForTesting != null)
                return QuickloadResumeUTProviderForTesting();

            return Planetarium.GetUniversalTime();
        }

        internal static bool TryGetTailTrackSectionEnvironment(Recording rec, out SegmentEnvironment env)
        {
            if (rec != null && rec.TrackSections != null)
            {
                for (int i = rec.TrackSections.Count - 1; i >= 0; i--)
                {
                    TrackSection section = rec.TrackSections[i];
                    bool hasFrames = section.frames != null && section.frames.Count > 0;
                    bool hasCheckpoints = section.checkpoints != null && section.checkpoints.Count > 0;
                    if (hasFrames || hasCheckpoints || section.endUT > section.startUT)
                    {
                        env = section.environment;
                        return true;
                    }
                }
            }

            env = default(SegmentEnvironment);
            return false;
        }

        public double RewindReservedFunds { get; private set; }
        public double RewindReservedScience { get; private set; }
        public float RewindReservedRep { get; private set; }

        // Location context (Phase 10) — captured at recording start
        public string StartBodyName { get; private set; }
        public string StartBiome { get; private set; }
        public string StartSituation { get; private set; }
        public string LaunchSiteName { get; private set; }
        public ConfigNode LastGoodVesselSnapshot => lastGoodVesselSnapshot;
        public ConfigNode InitialGhostVisualSnapshot => initialGhostVisualSnapshot;
        internal RecordingFinalizationCache FinalizationCache { get; private set; }

        // Adaptive sampling thresholds (read from settings, fallback to Medium defaults)
        private static float minSampleInterval =>
            ParsekSettings.Current?.minSampleInterval ?? ParsekSettings.GetMinSampleInterval(SamplingDensity.Medium);
        private static float maxSampleInterval =>
            ParsekSettings.Current?.maxSampleInterval ?? ParsekSettings.GetMaxSampleInterval(SamplingDensity.Medium);
        private static float velocityDirThreshold =>
            ParsekSettings.Current?.velocityDirThreshold ?? ParsekSettings.GetVelocityDirThreshold(SamplingDensity.Medium);
        private static float speedChangeThreshold =>
            (ParsekSettings.Current?.speedChangeThreshold ?? ParsekSettings.GetSpeedChangeThreshold(SamplingDensity.Medium)) / 100f;
        private const double snapshotRefreshIntervalUT = 10.0;
        private static readonly double finalizationCacheRefreshIntervalUT =
            RecordingFinalizationCacheProducer.DefaultRefreshIntervalUT;
        private const float snapshotPerfLogThresholdMs = 25.0f;
        private const float attitudeSampleThresholdDegrees = 1.0f;
        private const double roboticSampleIntervalSeconds = 0.25; // 4 Hz
        private const float roboticAngularDeadbandDegrees = 0.5f;
        private const float roboticLinearDeadbandMeters = 0.01f;
        internal const float AnimateHeatHotThreshold = 0.80f;
        internal const float AnimateHeatHotFallbackThreshold = 0.75f; // hysteresis: fall from Hot at 0.75, rise at 0.80
        internal const float AnimateHeatMediumThreshold = 0.40f;
        internal const float AnimateHeatMediumFallbackThreshold = 0.35f; // hysteresis: fall from Medium at 0.35, rise at 0.40
        private double lastRecordedUT = -1;
        private Vector3 lastRecordedVelocity;
        private Quaternion lastRecordedWorldRotation = Quaternion.identity;
        private bool hasLastRecordedWorldRotation;

        /// <summary>
        /// UT of the most recently sampled trajectory point. <c>double.NaN</c>
        /// if no point has been sampled yet (matches the never-sampled sentinel
        /// the snapshot logger expects). Exposed read-only for the
        /// <c>[RecState]</c> observability dump — the recorder's
        /// <see cref="Recording"/> buffer is not a reliable proxy because it
        /// can be cleared by <c>FlushRecorderIntoActiveTreeForSerialization</c>
        /// while sampling continues.
        /// </summary>
        internal double LastRecordedUT => lastRecordedUT < 0 ? double.NaN : lastRecordedUT;

        /// <summary>
        /// Altitude (m) of the most recently committed point. Double.NaN until the
        /// first point is recorded. Exposed so callers (e.g. HandleSoiAutoSplit in
        /// ParsekFlight) can read the "last known altitude" without depending on
        /// the Recording buffer being populated — the buffer is cleared by
        /// FlushRecorderIntoActiveTreeForSerialization on every OnSave while
        /// IsRecording stays true.
        /// </summary>
        internal double LastRecordedAltitude { get; private set; } = double.NaN;
        private ConfigNode lastGoodVesselSnapshot;
        private ConfigNode initialGhostVisualSnapshot;
        private Dictionary<string, ResourceAmount> pendingStartResources;
        private Dictionary<string, InventoryItem> pendingStartInventory;
        private int pendingStartInventorySlots;
        private Dictionary<string, int> pendingStartCrew;
        private double lastSnapshotRefreshUT = double.MinValue;
        private double lastFinalizationCacheRefreshUT = double.MinValue;

        // Boundary anchor: if set, inserted as the first point when recording starts.
        // Used for chain continuation to seamlessly stitch segments.
        public TrajectoryPoint? BoundaryAnchor { get; set; }

        // On-rails state
        private bool isOnRails;
        private bool hasPersistentRotation;
        private OrbitSegment currentOrbitSegment;

        // Atmosphere boundary detection
        private bool wasInAtmosphere;
        private bool atmosphereBoundaryPending;
        private double atmosphereBoundaryPendingUT;
        public bool AtmosphereBoundaryCrossed { get; private set; }
        public bool EnteredAtmosphere { get; private set; }

        // Altitude boundary detection (airless bodies)
        private bool wasAboveAltitudeThreshold;
        private bool altitudeBoundaryPending;
        private double altitudeBoundaryPendingUT;
        private double currentAltitudeThreshold;
        public bool AltitudeBoundaryCrossed { get; private set; }
        public bool DescendedBelowThreshold { get; private set; }

        // SOI change detection
        public bool SoiChangePending { get; private set; }
        public string SoiChangeFromBody { get; private set; }

        #region Part Event Subscription

        private void SubscribePartEvents()
        {
            if (partEventsSubscribed) return;
            GameEvents.onPartDie.Add(OnPartDie);
            GameEvents.onPartJointBreak.Add(OnPartJointBreak);
            partEventsSubscribed = true;
            ParsekLog.Verbose("Recorder", "Subscribed part event hooks");
        }

        private void UnsubscribePartEvents()
        {
            if (!partEventsSubscribed) return;
            GameEvents.onPartDie.Remove(OnPartDie);
            GameEvents.onPartJointBreak.Remove(OnPartJointBreak);
            partEventsSubscribed = false;
            ParsekLog.Verbose("Recorder", "Unsubscribed part event hooks");
        }

        private void OnPartDie(Part p)
        {
            if (!IsRecording) return;
            if (p?.vessel == null)
            {
                ParsekLog.VerboseRateLimited("Recorder", "part-die-null",
                    "OnPartDie: part or vessel is null");
                return;
            }
            if (p.vessel.persistentId != RecordingVesselId) return;

            bool hasChute = p.FindModuleImplementing<ModuleParachute>() != null;
            var evtType = ClassifyPartDeath(p.persistentId, hasChute, parachuteStates);

            PartEvents.Add(new PartEvent
            {
                ut = Planetarium.GetUniversalTime(),
                partPersistentId = p.persistentId,
                eventType = evtType,
                partName = p.partInfo?.name ?? "unknown"
            });
            ParsekLog.Verbose("Recorder", $"Part event: {evtType} '{p.partInfo?.name}' pid={p.persistentId}");
            RefreshFinalizationCache(p.vessel, "part_die", force: true);
        }

        internal static PartEventType ClassifyPartDeath(
            uint partPersistentId, bool hasParachuteModule, Dictionary<uint, int> parachuteStates)
        {
            int state;
            if (hasParachuteModule && parachuteStates.TryGetValue(partPersistentId, out state) && state > 0)
            {
                parachuteStates.Remove(partPersistentId);
                return PartEventType.ParachuteDestroyed;
            }
            return PartEventType.Destroyed;
        }

        private void OnPartJointBreak(PartJoint joint, float breakForce)
        {
            if (!IsRecording) return;
            if (joint?.Child?.vessel == null)
            {
                ParsekLog.VerboseRateLimited("Recorder", "joint-break-null",
                    "OnPartJointBreak: joint, child, or vessel is null");
                return;
            }
            if (joint.Child.vessel.persistentId != RecordingVesselId) return;

            // Skip non-structural joint breaks (e.g., wheel suspension stress)
            // Part.attachJoint is the structural connection to parent — only that indicates real separation
            var childAttachJoint = joint.Child.attachJoint;
            if (!IsStructuralJointBreak(joint == childAttachJoint, childAttachJoint != null))
            {
                ParsekLog.VerboseRateLimited("Recorder", $"joint-break-nonstructural-{joint.Child.persistentId}",
                    $"OnPartJointBreak: non-structural joint break on '{joint.Child.partInfo?.name}' pid={joint.Child.persistentId} (breakForce={breakForce:F1}), skipping");
                return;
            }

            // Skip duplicate decoupled events for the same part
            if (decoupledPartIds.Contains(joint.Child.persistentId))
            {
                ParsekLog.VerboseRateLimited("Recorder", $"joint-break-dup-{joint.Child.persistentId}",
                    $"OnPartJointBreak: duplicate for already-decoupled '{joint.Child.partInfo?.name}' pid={joint.Child.persistentId}, skipping");
                return;
            }
            decoupledPartIds.Add(joint.Child.persistentId);

            double jointBreakUT = Planetarium.GetUniversalTime();

            PartEvents.Add(new PartEvent
            {
                ut = jointBreakUT,
                partPersistentId = joint.Child.persistentId,
                eventType = PartEventType.Decoupled,
                partName = joint.Child.partInfo?.name ?? "unknown"
            });
            ParsekLog.Verbose("Recorder", $"Part event: Decoupled '{joint.Child.partInfo?.name}' pid={joint.Child.persistentId}");
            RefreshFinalizationCache(joint.Child.vessel, "joint_break", force: true);

            // Phase 9 (design doc §12, §18 Phase 9): synthesize a structural-event
            // snapshot for the recorded vessel at the exact joint-break UT so
            // AnchorCandidateBuilder gets a physics-precision sample for the
            // resulting BranchPointType.JointBreak ε. Both the joint host and the
            // (pre-split) child still share v.persistentId == RecordingVesselId
            // here; only the recording vessel matches and gets a snapshot.
            // joint.Host.vessel and joint.Child.vessel typically reference the
            // same Vessel pre-split, so we pass both — AppendStructuralEventSnapshot
            // dedups by RecordingVesselId.
            var jointBreakInvolved = new List<Vessel>(2);
            if (joint.Child.vessel != null) jointBreakInvolved.Add(joint.Child.vessel);
            if (joint.Host?.vessel != null && (joint.Host.vessel != joint.Child.vessel))
                jointBreakInvolved.Add(joint.Host.vessel);
            AppendStructuralEventSnapshot(jointBreakUT, jointBreakInvolved, "JointBreak");

            // Signal potential vessel split for deferred check by ParsekFlight
            if (!HasPendingJointBreakCheck || double.IsNaN(PendingJointBreakUT) || jointBreakUT < PendingJointBreakUT)
                PendingJointBreakUT = jointBreakUT;
            HasPendingJointBreakCheck = true;
        }

        /// <summary>
        /// Records a fallback Decoupled PartEvent for a part that split off into a new vessel.
        /// Called by <see cref="ParsekFlight.DeferredJointBreakCheck"/> after the vessel-split
        /// classification, once per new vessel's root part. This is a safety net for bug #263:
        /// when a symmetry group of radial decouplers fires, individual <c>onPartJointBreak</c>
        /// events race with KSP's vessel-split processing, and some may be dropped mid-pipeline,
        /// leaving decoupled subtrees visible on the ghost during playback.
        ///
        /// The deferred scan is deterministic — every new debris vessel has exactly one root
        /// part, and that root is by construction the part that separated from the recording
        /// vessel, so emitting a <c>Decoupled</c> event for it hides the correct subtree.
        ///
        /// <para><b>Dedup source:</b> scans <see cref="PartEvents"/> directly for an existing
        /// <see cref="PartEventType.Decoupled"/> entry with the same pid, rather than the
        /// parallel <see cref="decoupledPartIds"/> tracking set. <c>decoupledPartIds</c>
        /// tracks "events OnPartJointBreak attempted to emit" but can drift out of sync
        /// with the actual serialized list if events are dropped or stripped downstream.
        /// Scanning the list is the only way to ensure the fallback recovers genuinely
        /// missing events. <c>PartEvents</c> is small at deferred-check time (~tens of
        /// entries), so the linear scan is negligible relative to the Unity physics cost
        /// that precedes it.</para>
        /// </summary>
        /// <param name="partPid">The persistent ID of the new vessel's root part.</param>
        /// <param name="partName">The root part's name (for logging/serialization).</param>
        /// <param name="ut">Universal time to stamp on the event.</param>
        /// <returns>1 if a new event was added, 0 if <see cref="PartEvents"/> already contains a Decoupled entry for this pid.</returns>
        internal int RecordFallbackDecoupleEvent(uint partPid, string partName, double ut)
        {
            // Dedup against the serialized list (source of truth), NOT the parallel
            // decoupledPartIds set. decoupledPartIds is a fast-path tracker for
            // OnPartJointBreak's own cycle-level dedup; it can be stale relative to
            // PartEvents if events were added there but then dropped or stripped
            // downstream. The fallback must check what will actually be serialized,
            // otherwise it silently skips the exact cases it was written to recover.
            for (int i = 0; i < PartEvents.Count; i++)
            {
                var existing = PartEvents[i];
                if (existing.eventType == PartEventType.Decoupled && existing.partPersistentId == partPid)
                {
                    ParsekLog.Verbose("Recorder",
                        $"RecordFallbackDecoupleEvent: pid={partPid} already has a Decoupled PartEvent, skipping");
                    return 0;
                }
            }

            // Keep decoupledPartIds in sync so any subsequent OnPartJointBreak for
            // the same pid (unlikely but defensive) short-circuits via its own fast path.
            decoupledPartIds.Add(partPid);

            PartEvents.Add(new PartEvent
            {
                ut = ut,
                partPersistentId = partPid,
                eventType = PartEventType.Decoupled,
                partName = partName ?? "unknown",
                value = 0f,
                moduleIndex = 0
            });
            ParsekLog.Info("Recorder",
                $"Fallback Decoupled event recorded: '{partName}' pid={partPid} at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
            return 1;
        }

        /// <summary>
        /// Determines whether a joint break is structural (real separation) or non-structural
        /// (e.g., wheel suspension stress). Only the structural attachJoint indicates real decoupling.
        /// </summary>
        /// <param name="brokenJointIsAttachJoint">True if the broken joint is the child's attachJoint.</param>
        /// <param name="hasAttachJoint">True if the child part has a non-null attachJoint (false for root parts).</param>
        internal static bool IsStructuralJointBreak(bool brokenJointIsAttachJoint, bool hasAttachJoint)
        {
            // If the child has no attach joint (root part), any break is structural
            if (!hasAttachJoint) return true;
            // Only the structural attach joint indicates real separation
            return brokenJointIsAttachJoint;
        }

        /// <summary>
        /// Detect parachute state transitions. States: 0=stowed/active/cut, 1=semi-deployed, 2=deployed.
        /// </summary>
        internal static PartEvent? CheckParachuteTransition(
            uint partPersistentId, string partName, int newState, Dictionary<uint, int> stateMap, double ut)
        {
            int oldState;
            if (!stateMap.TryGetValue(partPersistentId, out oldState))
                oldState = 0;

            if (newState == oldState)
                return null;

            if (newState > 0)
                stateMap[partPersistentId] = newState;
            else
                stateMap.Remove(partPersistentId);

            // 0→1: semi-deployed (streamer)
            if (newState == 1 && oldState == 0)
            {
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.ParachuteSemiDeployed,
                    partName = partName
                };
            }

            // 0→2 or 1→2: fully deployed (dome)
            if (newState == 2)
            {
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.ParachuteDeployed,
                    partName = partName
                };
            }

            // 1→0 or 2→0: cut
            if (newState == 0 && oldState > 0)
            {
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.ParachuteCut,
                    partName = partName
                };
            }

            return null;
        }

        private void CheckParachuteState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var chute = p.FindModuleImplementing<ModuleParachute>();
                if (chute == null) continue;

                int state = chute.deploymentState == ModuleParachute.deploymentStates.DEPLOYED ? 2
                          : chute.deploymentState == ModuleParachute.deploymentStates.SEMIDEPLOYED ? 1
                          : 0;

                var evt = CheckParachuteTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", state, parachuteStates, ut);
                if (evt.HasValue)
                {
                    PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
                }
            }
        }

        internal static PartEvent? CheckJettisonTransition(
            uint partPersistentId, string partName, bool isJettisoned, HashSet<uint> jettisonedSet, double ut)
        {
            if (isJettisoned && jettisonedSet.Add(partPersistentId))
            {
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.ShroudJettisoned,
                    partName = partName
                };
            }

            return null;
        }

        private string[] GetCachedJettisonNames(ulong moduleKey, string rawNames)
        {
            rawNames = rawNames ?? string.Empty;
            string cachedRaw;
            if (jettisonNameRawCache.TryGetValue(moduleKey, out cachedRaw) &&
                string.Equals(cachedRaw, rawNames, StringComparison.Ordinal) &&
                parsedJettisonNamesCache.TryGetValue(moduleKey, out string[] cachedNames))
            {
                return cachedNames;
            }

            string[] parsedNames = ParseJettisonNames(rawNames);
            jettisonNameRawCache[moduleKey] = rawNames;
            parsedJettisonNamesCache[moduleKey] = parsedNames;
            return parsedNames;
        }

        internal static string[] ParseJettisonNames(string rawNames)
        {
            if (string.IsNullOrWhiteSpace(rawNames))
                return Array.Empty<string>();

            string[] split = rawNames.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (split.Length == 0)
                return Array.Empty<string>();

            var cleaned = new List<string>(split.Length);
            for (int i = 0; i < split.Length; i++)
            {
                string name = split[i].Trim();
                if (!string.IsNullOrEmpty(name))
                    cleaned.Add(name);
            }

            return cleaned.Count > 0 ? cleaned.ToArray() : Array.Empty<string>();
        }

        private void CheckJettisonState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                bool hasJettisonModule = false;
                bool isJettisoned = false;
                int jettisonModuleIndex = 0;
                // Parts with ModulePartVariants legitimately hide non-selected transforms
                // (e.g. Poodle DoubleBell hides Shroud2). The transform-visibility fallback
                // would misinterpret those as jettisoned, so skip it for variant parts.
                bool skipTransformFallback = p.FindModuleImplementing<ModulePartVariants>() != null;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    var jettison = p.Modules[m] as ModuleJettison;
                    if (jettison == null) continue;
                    hasJettisonModule = true;
                    ulong moduleKey = EncodeEngineKey(p.persistentId, jettisonModuleIndex);
                    jettisonModuleIndex++;

                    // Primary state flag.
                    if (jettison.isJettisoned)
                    {
                        isJettisoned = true;
                        break;
                    }

                    // Fallback: if any configured jettison object is already hidden,
                    // treat as jettisoned. Skip for variant parts (see above).
                    if (skipTransformFallback)
                        continue;

                    string jettisonNames = jettison.jettisonName;
                    if (string.IsNullOrWhiteSpace(jettisonNames))
                        continue;

                    string[] names = GetCachedJettisonNames(moduleKey, jettisonNames);
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

                var evt = CheckJettisonTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isJettisoned, jettisonedShrouds, ut);
                if (evt.HasValue)
                {
                    PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
                }
            }
        }

        internal static PartEvent? CheckDeployableTransition(
            uint partPersistentId, string partName, bool isExtended, HashSet<uint> extendedSet, double ut)
        {
            bool wasExtended = extendedSet.Contains(partPersistentId);

            if (isExtended && !wasExtended)
            {
                extendedSet.Add(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.DeployableExtended,
                    partName = partName
                };
            }

            if (!isExtended && wasExtended)
            {
                extendedSet.Remove(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.DeployableRetracted,
                    partName = partName
                };
            }

            return null;
        }

        private void CheckDeployableState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var deployable = p.FindModuleImplementing<ModuleDeployablePart>();
                if (deployable == null) continue;

                var ds = deployable.deployState;

                // Skip broken and transitional states — only fire on completed transitions
                if (ds == ModuleDeployablePart.DeployState.BROKEN) continue;
                if (ds == ModuleDeployablePart.DeployState.EXTENDING) continue;
                if (ds == ModuleDeployablePart.DeployState.RETRACTING) continue;

                bool isExtended = ds == ModuleDeployablePart.DeployState.EXTENDED;

                var evt = CheckDeployableTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isExtended, extendedDeployables, ut);
                if (evt.HasValue)
                {
                    PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
                }
            }
        }

        internal static PartEvent? CheckLightTransition(
            uint partPersistentId, string partName, bool isOn, HashSet<uint> lightsOnSet, double ut)
        {
            bool wasOn = lightsOnSet.Contains(partPersistentId);

            if (isOn && !wasOn)
            {
                lightsOnSet.Add(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.LightOn,
                    partName = partName
                };
            }

            if (!isOn && wasOn)
            {
                lightsOnSet.Remove(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.LightOff,
                    partName = partName
                };
            }

            return null;
        }

        internal static void CheckLightBlinkTransition(
            uint partPersistentId, string partName, bool isBlinking, float blinkRate,
            HashSet<uint> blinkingSet, Dictionary<uint, float> blinkRateMap, double ut,
            List<PartEvent> events)
        {
            bool wasBlinking = blinkingSet.Contains(partPersistentId);
            float safeBlinkRate = blinkRate > 0f ? blinkRate : 1f;

            if (isBlinking && !wasBlinking)
            {
                blinkingSet.Add(partPersistentId);
                blinkRateMap[partPersistentId] = safeBlinkRate;
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.LightBlinkEnabled,
                    partName = partName,
                    value = safeBlinkRate
                });
                return;
            }

            if (!isBlinking && wasBlinking)
            {
                blinkingSet.Remove(partPersistentId);
                blinkRateMap.Remove(partPersistentId);
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.LightBlinkDisabled,
                    partName = partName
                });
                return;
            }

            if (isBlinking && wasBlinking)
            {
                float lastRate;
                if (!blinkRateMap.TryGetValue(partPersistentId, out lastRate))
                    lastRate = safeBlinkRate;

                if (Math.Abs(safeBlinkRate - lastRate) >= 0.01f)
                {
                    blinkRateMap[partPersistentId] = safeBlinkRate;
                    events.Add(new PartEvent
                    {
                        ut = ut,
                        partPersistentId = partPersistentId,
                        eventType = PartEventType.LightBlinkRate,
                        partName = partName,
                        value = safeBlinkRate
                    });
                }
            }
        }

        internal static void ClassifyLadderState(float animTime, out bool isExtended, out bool isRetracted)
        {
            isExtended = animTime >= 0.99f;
            isRetracted = animTime <= 0.01f;
        }

        internal static PartEvent? CheckLadderTransition(
            ulong key, uint partPersistentId, string partName, bool isExtended,
            HashSet<ulong> deployedSet, double ut, int moduleIndex)
        {
            bool wasExtended = deployedSet.Contains(key);

            if (isExtended && !wasExtended)
            {
                deployedSet.Add(key);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.DeployableExtended,
                    partName = partName,
                    moduleIndex = moduleIndex
                };
            }

            if (!isExtended && wasExtended)
            {
                deployedSet.Remove(key);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.DeployableRetracted,
                    partName = partName,
                    moduleIndex = moduleIndex
                };
            }

            return null;
        }

        internal static PartEvent? CheckAnimationGroupTransition(
            ulong key, uint partPersistentId, string partName, bool isDeployed,
            HashSet<ulong> deployedSet, double ut, int moduleIndex)
        {
            bool wasDeployed = deployedSet.Contains(key);

            if (isDeployed && !wasDeployed)
            {
                deployedSet.Add(key);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.DeployableExtended,
                    partName = partName,
                    moduleIndex = moduleIndex
                };
            }

            if (!isDeployed && wasDeployed)
            {
                deployedSet.Remove(key);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.DeployableRetracted,
                    partName = partName,
                    moduleIndex = moduleIndex
                };
            }

            return null;
        }

        internal static PartEvent? CheckAnimateHeatTransition(
            ulong key, uint partPersistentId, string partName,
            float normalizedHeat,
            Dictionary<ulong, HeatLevel> levelMap, double ut, int moduleIndex)
        {
            HeatLevel current;
            if (!levelMap.TryGetValue(key, out current))
                current = HeatLevel.Cold;

            // Determine target level with hysteresis gaps:
            // Cold-Medium: rise at 0.40, fall at 0.35 (gap [0.35, 0.40) preserves current)
            // Medium-Hot:  rise at 0.80, fall at 0.75 (gap [0.75, 0.80) preserves current)
            HeatLevel target;
            if (normalizedHeat >= AnimateHeatHotThreshold)
                target = HeatLevel.Hot;
            else if (normalizedHeat >= AnimateHeatMediumThreshold)
            {
                // In the Medium zone — but check if falling from Hot with hysteresis
                if (current == HeatLevel.Hot && normalizedHeat >= AnimateHeatHotFallbackThreshold)
                    target = HeatLevel.Hot; // stay Hot in [0.75, 0.80) gap
                else
                    target = HeatLevel.Medium;
            }
            else if (normalizedHeat >= AnimateHeatMediumFallbackThreshold)
            {
                // In the hysteresis gap [0.35, 0.40) — keep current level
                target = current;
            }
            else
                target = HeatLevel.Cold;

            if (target == current)
                return null;

            levelMap[key] = target;

            PartEventType eventType;
            switch (target)
            {
                case HeatLevel.Hot:
                    eventType = PartEventType.ThermalAnimationHot;
                    break;
                case HeatLevel.Medium:
                    eventType = PartEventType.ThermalAnimationMedium;
                    break;
                default:
                    eventType = PartEventType.ThermalAnimationCold;
                    break;
            }

            ParsekLog.Verbose("Recorder",
                $"[Recording][AnimateHeat] Part '{partName}' pid={partPersistentId}: " +
                $"heat level {current} -> {target} (normalizedHeat={normalizedHeat:F3})");

            return new PartEvent
            {
                ut = ut,
                partPersistentId = partPersistentId,
                eventType = eventType,
                partName = partName,
                value = normalizedHeat,
                moduleIndex = moduleIndex
            };
        }

        private void CheckLightState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                bool hasModuleLight = false;
                var light = p.FindModuleImplementing<ModuleLight>();
                if (light != null)
                {
                    hasModuleLight = true;
                    var evt = CheckLightTransition(
                        p.persistentId, p.partInfo?.name ?? "unknown", light.isOn, lightsOn, ut);
                    if (evt.HasValue)
                    {
                        PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
                    }

                    reusableEventBuffer.Clear();
                    CheckLightBlinkTransition(
                        p.persistentId, p.partInfo?.name ?? "unknown",
                        light.isBlinking, light.blinkRate,
                        blinkingLights, lightBlinkRates, ut, reusableEventBuffer);
                    for (int e = 0; e < reusableEventBuffer.Count; e++)
                    {
                        PartEvents.Add(reusableEventBuffer[e]);
                        ParsekLog.Verbose("Recorder", $"Part event: {reusableEventBuffer[e].eventType} '{reusableEventBuffer[e].partName}' " +
                            $"pid={reusableEventBuffer[e].partPersistentId} val={reusableEventBuffer[e].value:F2}");
                    }
                }

                // Also check ModuleColorChanger with toggleInFlight=true (cabin lights).
                // Parts with both ModuleLight and ModuleColorChanger toggle together via the
                // Light action group, so the lightsOn HashSet deduplicates via partPersistentId.
                // For parts with ONLY ModuleColorChanger, this produces the LightOn/LightOff events.
                if (!hasModuleLight)
                {
                    bool foundToggleableColorChanger = false;
                    var modules = p.Modules;
                    for (int mi = 0; mi < modules.Count; mi++)
                    {
                        var cc = modules[mi] as ModuleColorChanger;
                        if (cc == null) continue;
                        if (!cc.toggleInFlight) continue;

                        foundToggleableColorChanger = true;
                        var ccEvt = CheckLightTransition(
                            p.persistentId, p.partInfo?.name ?? "unknown", cc.animState, lightsOn, ut);
                        if (ccEvt.HasValue)
                        {
                            PartEvents.Add(ccEvt.Value);
                            ParsekLog.Verbose("Recorder",
                                $"ColorChanger state change: pid={p.persistentId} animState={cc.animState} " +
                                $"event={ccEvt.Value.eventType} '{ccEvt.Value.partName}'");
                        }
                        break; // Only one toggleable ColorChanger per part matters
                    }

                    if (!foundToggleableColorChanger)
                        continue;
                }
            }
        }

        internal static bool TryClassifyLadderStateFromEventActivity(
            bool canExtend, bool canRetract, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;

            // For ladders, mutually-exclusive UI event activity indicates current state:
            // - can retract => currently deployed
            // - can extend  => currently retracted
            if (canExtend == canRetract)
                return false;

            isDeployed = canRetract;
            isRetracted = canExtend;
            return true;
        }

        internal static bool TryClassifyRetractableLadderState(
            PartModule ladderModule, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;
            if (ladderModule == null) return false;

            bool sawExtendEvent = false;
            bool sawRetractEvent = false;
            bool canExtend = false;
            bool canRetract = false;

            // Prefer event activity, which directly reflects action availability.
            if (ladderModule.Events != null)
            {
                for (int i = 0; i < ladderModule.Events.Count; i++)
                {
                    BaseEvent evt = ladderModule.Events[i];
                    if (evt == null) continue;

                    string evtName = (evt.name ?? string.Empty).ToLowerInvariant();
                    string guiName = (evt.guiName ?? string.Empty).ToLowerInvariant();

                    bool isExtendEvent = evtName.Contains("extend") || guiName.Contains("extend");
                    bool isRetractEvent = evtName.Contains("retract") || guiName.Contains("retract");

                    if (isExtendEvent)
                    {
                        sawExtendEvent = true;
                        canExtend = canExtend || evt.active;
                    }

                    if (isRetractEvent)
                    {
                        sawRetractEvent = true;
                        canRetract = canRetract || evt.active;
                    }
                }
            }

            if ((sawExtendEvent || sawRetractEvent) &&
                TryClassifyLadderStateFromEventActivity(
                    canExtend, canRetract, out isDeployed, out isRetracted))
                return true;

            // Fallback: some module variants expose direct bool fields.
            bool boolValue;
            if (TryGetModuleBoolField(ladderModule, "isDeployed", out boolValue) ||
                TryGetModuleBoolField(ladderModule, "deployed", out boolValue) ||
                TryGetModuleBoolField(ladderModule, "isExtended", out boolValue) ||
                TryGetModuleBoolField(ladderModule, "extended", out boolValue))
            {
                isDeployed = boolValue;
                isRetracted = !boolValue;
                return true;
            }

            if (TryGetModuleBoolField(ladderModule, "isRetracted", out boolValue) ||
                TryGetModuleBoolField(ladderModule, "retracted", out boolValue))
            {
                isRetracted = boolValue;
                isDeployed = !boolValue;
                return true;
            }

            return false;
        }

        internal static bool TryClassifyAnimationGroupState(
            PartModule animationGroupModule, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;
            if (animationGroupModule == null) return false;

            bool sawDeployEvent = false;
            bool sawRetractEvent = false;
            bool canDeploy = false;
            bool canRetract = false;

            // Prefer event activity: for ModuleAnimationGroup this directly tracks whether
            // the opposite action is currently available (deploy vs retract).
            if (animationGroupModule.Events != null)
            {
                for (int i = 0; i < animationGroupModule.Events.Count; i++)
                {
                    BaseEvent evt = animationGroupModule.Events[i];
                    if (evt == null) continue;

                    string evtName = (evt.name ?? string.Empty).ToLowerInvariant();
                    string guiName = (evt.guiName ?? string.Empty).ToLowerInvariant();

                    bool isDeployEvent =
                        evtName.Contains("deploy") || guiName.Contains("deploy") ||
                        evtName.Contains("extend") || guiName.Contains("extend");
                    bool isRetractEvent =
                        evtName.Contains("retract") || guiName.Contains("retract");

                    if (isDeployEvent)
                    {
                        sawDeployEvent = true;
                        canDeploy = canDeploy || evt.active;
                    }

                    if (isRetractEvent)
                    {
                        sawRetractEvent = true;
                        canRetract = canRetract || evt.active;
                    }
                }
            }

            if ((sawDeployEvent || sawRetractEvent) &&
                TryClassifyLadderStateFromEventActivity(
                    canExtend: canDeploy, canRetract: canRetract,
                    out isDeployed, out isRetracted))
                return true;

            // Fallback: some variants expose direct bool fields.
            bool boolValue;
            if (TryGetModuleBoolField(animationGroupModule, "isDeployed", out boolValue) ||
                TryGetModuleBoolField(animationGroupModule, "deployed", out boolValue) ||
                TryGetModuleBoolField(animationGroupModule, "isExtended", out boolValue) ||
                TryGetModuleBoolField(animationGroupModule, "extended", out boolValue))
            {
                isDeployed = boolValue;
                isRetracted = !boolValue;
                return true;
            }

            if (TryGetModuleBoolField(animationGroupModule, "isRetracted", out boolValue) ||
                TryGetModuleBoolField(animationGroupModule, "retracted", out boolValue))
            {
                isRetracted = boolValue;
                isDeployed = !boolValue;
                return true;
            }

            return false;
        }

        internal static bool TryClassifyAnimateGenericState(
            ModuleAnimateGeneric animateModule, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;
            if (animateModule == null) return false;

            float animTime = animateModule.animTime;
            if (!float.IsNaN(animTime) && !float.IsInfinity(animTime))
            {
                ClassifyLadderState(animTime, out isDeployed, out isRetracted);
                if (isDeployed || isRetracted)
                    return true;
            }

            bool sawDeployEvent = false;
            bool sawRetractEvent = false;
            bool canDeploy = false;
            bool canRetract = false;

            if (animateModule.Events != null)
            {
                for (int i = 0; i < animateModule.Events.Count; i++)
                {
                    BaseEvent evt = animateModule.Events[i];
                    if (evt == null) continue;

                    string evtName = (evt.name ?? string.Empty).ToLowerInvariant();
                    string guiName = (evt.guiName ?? string.Empty).ToLowerInvariant();

                    bool isDeployEvent =
                        evtName.Contains("deploy") || guiName.Contains("deploy") ||
                        evtName.Contains("extend") || guiName.Contains("extend") ||
                        evtName.Contains("open") || guiName.Contains("open") ||
                        evtName.Contains("inflate") || guiName.Contains("inflate");
                    bool isRetractEvent =
                        evtName.Contains("retract") || guiName.Contains("retract") ||
                        evtName.Contains("close") || guiName.Contains("close") ||
                        evtName.Contains("deflate") || guiName.Contains("deflate");

                    if (isDeployEvent)
                    {
                        sawDeployEvent = true;
                        canDeploy = canDeploy || evt.active;
                    }

                    if (isRetractEvent)
                    {
                        sawRetractEvent = true;
                        canRetract = canRetract || evt.active;
                    }
                }
            }

            if ((sawDeployEvent || sawRetractEvent) &&
                TryClassifyLadderStateFromEventActivity(
                    canExtend: canDeploy, canRetract: canRetract,
                    out isDeployed, out isRetracted))
                return true;

            bool boolValue;
            if (TryGetModuleBoolField(animateModule, "isDeployed", out boolValue) ||
                TryGetModuleBoolField(animateModule, "deployed", out boolValue) ||
                TryGetModuleBoolField(animateModule, "isExtended", out boolValue) ||
                TryGetModuleBoolField(animateModule, "extended", out boolValue))
            {
                isDeployed = boolValue;
                isRetracted = !boolValue;
                return true;
            }

            if (TryGetModuleBoolField(animateModule, "isRetracted", out boolValue) ||
                TryGetModuleBoolField(animateModule, "retracted", out boolValue))
            {
                isRetracted = boolValue;
                isDeployed = !boolValue;
                return true;
            }

            return false;
        }

        internal static bool TryClassifyAeroSurfaceState(
            PartModule aeroSurfaceModule, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;
            if (aeroSurfaceModule == null) return false;

            bool sawDeployEvent = false;
            bool sawRetractEvent = false;
            bool canDeploy = false;
            bool canRetract = false;

            if (aeroSurfaceModule.Events != null)
            {
                for (int i = 0; i < aeroSurfaceModule.Events.Count; i++)
                {
                    BaseEvent evt = aeroSurfaceModule.Events[i];
                    if (evt == null) continue;

                    string evtName = (evt.name ?? string.Empty).ToLowerInvariant();
                    string guiName = (evt.guiName ?? string.Empty).ToLowerInvariant();

                    bool isDeployEvent =
                        evtName.Contains("deploy") || guiName.Contains("deploy") ||
                        evtName.Contains("extend") || guiName.Contains("extend") ||
                        evtName.Contains("open") || guiName.Contains("open") ||
                        evtName.Contains("brake") || guiName.Contains("brake") ||
                        evtName.Contains("enable") || guiName.Contains("enable");
                    bool isRetractEvent =
                        evtName.Contains("retract") || guiName.Contains("retract") ||
                        evtName.Contains("close") || guiName.Contains("close") ||
                        evtName.Contains("stow") || guiName.Contains("stow") ||
                        evtName.Contains("disable") || guiName.Contains("disable");

                    if (isDeployEvent)
                    {
                        sawDeployEvent = true;
                        canDeploy = canDeploy || evt.active;
                    }

                    if (isRetractEvent)
                    {
                        sawRetractEvent = true;
                        canRetract = canRetract || evt.active;
                    }
                }
            }

            if ((sawDeployEvent || sawRetractEvent) &&
                TryClassifyLadderStateFromEventActivity(
                    canExtend: canDeploy, canRetract: canRetract,
                    out isDeployed, out isRetracted))
                return true;

            bool boolValue;
            string[] deployedFields =
            {
                "isDeployed",
                "deployed",
                "isExtended",
                "extended",
                "isBraking",
                "brakesOn",
                "isActivated",
                "active"
            };
            for (int i = 0; i < deployedFields.Length; i++)
            {
                if (TryReadModuleBoolField(aeroSurfaceModule, deployedFields[i], out boolValue))
                {
                    isDeployed = boolValue;
                    isRetracted = !boolValue;
                    return true;
                }
            }

            string[] retractedFields =
            {
                "isRetracted",
                "retracted",
                "isStowed",
                "stowed",
                "isPacked",
                "packed"
            };
            for (int i = 0; i < retractedFields.Length; i++)
            {
                if (TryReadModuleBoolField(aeroSurfaceModule, retractedFields[i], out boolValue))
                {
                    isRetracted = boolValue;
                    isDeployed = !boolValue;
                    return true;
                }
            }

            string[] deflectionFields =
            {
                "currentDeflection",
                "deflection",
                "deployPercent",
                "position"
            };
            for (int i = 0; i < deflectionFields.Length; i++)
            {
                if (!TryReadModuleFloatField(aeroSurfaceModule, deflectionFields[i], out float deflection))
                    continue;

                if (float.IsNaN(deflection) || float.IsInfinity(deflection))
                    continue;

                isDeployed = Math.Abs(deflection) > 0.01f;
                isRetracted = !isDeployed;
                return true;
            }

            return false;
        }

        internal static bool TryClassifyControlSurfaceState(
            PartModule controlSurfaceModule, out bool isDeployed, out bool isRetracted)
        {
            // ModuleControlSurface exposes similar fields/deflection semantics to ModuleAeroSurface.
            return TryClassifyAeroSurfaceState(controlSurfaceModule, out isDeployed, out isRetracted);
        }

        internal static bool TryClassifyRobotArmScannerState(
            PartModule robotArmScannerModule, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;
            if (robotArmScannerModule == null) return false;

            bool sawDeployEvent = false;
            bool sawRetractEvent = false;
            bool canDeploy = false;
            bool canRetract = false;

            if (robotArmScannerModule.Events != null)
            {
                for (int i = 0; i < robotArmScannerModule.Events.Count; i++)
                {
                    BaseEvent evt = robotArmScannerModule.Events[i];
                    if (evt == null) continue;

                    string evtName = (evt.name ?? string.Empty).ToLowerInvariant();
                    string guiName = (evt.guiName ?? string.Empty).ToLowerInvariant();

                    bool isDeployEvent =
                        evtName.Contains("deploy") || guiName.Contains("deploy") ||
                        evtName.Contains("unpack") || guiName.Contains("unpack") ||
                        evtName.Contains("extend") || guiName.Contains("extend") ||
                        evtName.Contains("scan") || guiName.Contains("scan") ||
                        evtName.Contains("start") || guiName.Contains("start");
                    bool isRetractEvent =
                        evtName.Contains("retract") || guiName.Contains("retract") ||
                        ((evtName.Contains("pack") || guiName.Contains("pack")) &&
                            !evtName.Contains("unpack") && !guiName.Contains("unpack")) ||
                        evtName.Contains("stow") || guiName.Contains("stow") ||
                        evtName.Contains("stop") || guiName.Contains("stop") ||
                        evtName.Contains("cancel") || guiName.Contains("cancel");

                    if (isDeployEvent)
                    {
                        sawDeployEvent = true;
                        canDeploy = canDeploy || evt.active;
                    }

                    if (isRetractEvent)
                    {
                        sawRetractEvent = true;
                        canRetract = canRetract || evt.active;
                    }
                }
            }

            if ((sawDeployEvent || sawRetractEvent) &&
                TryClassifyLadderStateFromEventActivity(
                    canExtend: canDeploy, canRetract: canRetract,
                    out isDeployed, out isRetracted))
                return true;

            bool boolValue;
            string[] deployedFields =
            {
                "isUnpacked",
                "unpacked",
                "isDeployed",
                "deployed",
                "isExtended",
                "extended",
                "isScanning",
                "scanning",
                "isWorking",
                "working"
            };
            for (int i = 0; i < deployedFields.Length; i++)
            {
                if (TryReadModuleBoolField(robotArmScannerModule, deployedFields[i], out boolValue))
                {
                    isDeployed = boolValue;
                    isRetracted = !boolValue;
                    return true;
                }
            }

            string[] retractedFields =
            {
                "isRetracted",
                "retracted",
                "isPacked",
                "packed",
                "isStowed",
                "stowed"
            };
            for (int i = 0; i < retractedFields.Length; i++)
            {
                if (TryReadModuleBoolField(robotArmScannerModule, retractedFields[i], out boolValue))
                {
                    isRetracted = boolValue;
                    isDeployed = !boolValue;
                    return true;
                }
            }

            if (TryReadModuleFloatField(robotArmScannerModule, "animTime", out float animTime) &&
                !float.IsNaN(animTime) && !float.IsInfinity(animTime))
            {
                ClassifyLadderState(animTime, out isDeployed, out isRetracted);
                if (isDeployed || isRetracted)
                    return true;
            }

            return false;
        }

        internal static bool TryClassifyAnimateHeatState(
            PartModule animateHeatModule, out float normalizedHeat, out string sourceField)
        {
            normalizedHeat = 0f;
            sourceField = null;
            if (animateHeatModule == null) return false;

            string[] candidateFields =
            {
                "animTime",
                "heatAnimTime",
                "thermalAnimState",
                "normalizedHeat",
                "heat",
                "heatValue",
                "temperatureRatio",
                "tempRatio"
            };

            for (int i = 0; i < candidateFields.Length; i++)
            {
                if (!TryReadModuleFloatField(animateHeatModule, candidateFields[i], out float raw))
                    continue;
                if (float.IsNaN(raw) || float.IsInfinity(raw))
                    continue;

                normalizedHeat = NormalizeAnimateHeatScalar(raw);
                sourceField = candidateFields[i];
                return true;
            }

            return false;
        }

        private static bool TryGetModuleBoolField(PartModule module, string fieldName, out bool value)
        {
            value = false;
            if (module == null || module.Fields == null || string.IsNullOrEmpty(fieldName))
                return false;

            try
            {
                BaseField field = module.Fields[fieldName];
                if (field == null)
                    return false;

                object raw = field.GetValue(module);
                if (raw == null)
                    return false;

                if (raw is bool)
                {
                    value = (bool)raw;
                    return true;
                }

                string text = raw.ToString();
                bool parsedBool;
                if (bool.TryParse(text, out parsedBool))
                {
                    value = parsedBool;
                    return true;
                }

                int parsedInt;
                if (int.TryParse(text, out parsedInt))
                {
                    value = parsedInt != 0;
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        internal static void ClassifyGearState(string stateString, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = stateString == "Deployed";
            isRetracted = stateString == "Retracted";
        }

        internal static PartEvent? CheckGearTransition(
            uint partPersistentId, string partName, bool isDeployed, HashSet<uint> deployedSet, double ut)
        {
            bool wasDeployed = deployedSet.Contains(partPersistentId);

            if (isDeployed && !wasDeployed)
            {
                deployedSet.Add(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.GearDeployed,
                    partName = partName
                };
            }

            if (!isDeployed && wasDeployed)
            {
                deployedSet.Remove(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.GearRetracted,
                    partName = partName
                };
            }

            return null;
        }

        private void CheckGearState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    var wheel = p.Modules[m] as ModuleWheels.ModuleWheelDeployment;
                    if (wheel == null) continue;

                    ClassifyGearState(wheel.stateString, out bool isDeployed, out bool isRetracted);

                    // Skip transitional states ("Deploying"/"Retracting") — only fire on endpoints
                    if (!isDeployed && !isRetracted) continue;

                    var evt = CheckGearTransition(
                        p.persistentId, p.partInfo?.name ?? "unknown", isDeployed, deployedGear, ut);
                    if (evt.HasValue)
                    {
                        PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
                    }
                    break; // one deployment module per part
                }
            }
        }

        internal static void ClassifyCargoBayState(
            float animTime, float closedPosition, out bool isOpen, out bool isClosed)
        {
            bool atStart = animTime <= 0.01f;
            bool atEnd = animTime >= 0.99f;

            if (closedPosition > 0.9f)
            {
                // closedPosition near 1 → closed at animTime≈1, open at animTime≈0
                isClosed = atEnd;
                isOpen = atStart;
            }
            else if (closedPosition < 0.1f)
            {
                // closedPosition near 0 → closed at animTime≈0, open at animTime≈1
                isClosed = atStart;
                isOpen = atEnd;
            }
            else
            {
                // Non-standard closedPosition (modded part) — skip
                isClosed = false;
                isOpen = false;
            }
        }

        internal static PartEvent? CheckCargoBayTransition(
            uint partPersistentId, string partName, bool isOpen,
            HashSet<uint> openSet, double ut)
        {
            bool wasOpen = openSet.Contains(partPersistentId);

            if (isOpen && !wasOpen)
            {
                openSet.Add(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.CargoBayOpened,
                    partName = partName
                };
            }

            if (!isOpen && wasOpen)
            {
                openSet.Remove(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.CargoBayClosed,
                    partName = partName
                };
            }

            return null;
        }

        private void CheckCargoBayState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var cargo = p.FindModuleImplementing<ModuleCargoBay>();
                if (cargo == null) continue;

                // Resolve the linked ModuleAnimateGeneric via DeployModuleIndex
                int deployIdx = cargo.DeployModuleIndex;
                if (deployIdx < 0 || deployIdx >= p.Modules.Count)
                {
                    if (loggedCargoBayDeployIndexIssues.Add(p.persistentId))
                    {
                        ParsekLog.Verbose("Recorder", $"CargoBay: invalid DeployModuleIndex for '{p.partInfo?.name}' " +
                            $"pid={p.persistentId} deployIdx={deployIdx} modules={p.Modules.Count}");
                    }
                    continue;
                }
                var animModule = p.Modules[deployIdx] as ModuleAnimateGeneric;
                if (animModule == null)
                {
                    if (loggedCargoBayAnimationIssues.Add(p.persistentId))
                    {
                        PartModule moduleAtIndex = p.Modules[deployIdx];
                        string moduleName = moduleAtIndex?.moduleName ?? "<null>";
                        ParsekLog.Verbose("Recorder", $"CargoBay: DeployModuleIndex did not resolve to ModuleAnimateGeneric for " +
                            $"'{p.partInfo?.name}' pid={p.persistentId} deployIdx={deployIdx} module='{moduleName}'");
                    }
                    continue;
                }

                ClassifyCargoBayState(animModule.animTime, cargo.closedPosition,
                    out bool isOpen, out bool isClosed);

                if (!isOpen && !isClosed)
                {
                    if (cargo.closedPosition >= 0.1f && cargo.closedPosition <= 0.9f &&
                        loggedCargoBayClosedPositionIssues.Add(p.persistentId))
                    {
                        ParsekLog.Verbose("Recorder", $"CargoBay: unsupported closedPosition for '{p.partInfo?.name}' " +
                            $"pid={p.persistentId} closedPosition={cargo.closedPosition:F3} animTime={animModule.animTime:F3}");
                    }
                    continue; // mid-transition or non-standard closedPosition
                }

                var evt = CheckCargoBayTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isOpen, openCargoBays, ut);
                if (evt.HasValue)
                {
                    PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
                }
            }
        }

        private void CheckLadderState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
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

                    ulong key = EncodeEngineKey(p.persistentId, m);
                    bool isDeployed;
                    bool isRetracted;
                    if (!TryClassifyRetractableLadderState(module, out isDeployed, out isRetracted))
                    {
                        if (loggedLadderClassificationMisses.Add(key))
                        {
                            ParsekLog.Verbose("Recorder", $"Ladder: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}; fields=[{DescribeModuleFields(module)}]");
                        }
                        continue;
                    }

                    var evt = CheckLadderTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, deployedLadders, ut, m);
                    if (evt.HasValue)
                    {
                        PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} (ladder)");
                    }

                    break; // one RetractableLadder module per ladder part
                }
            }
        }

        private void CheckAnimationGroupState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
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
                    if (!TryClassifyAnimationGroupState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = EncodeEngineKey(p.persistentId, m);
                        if (loggedAnimationGroupClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("Recorder", $"AnimationGroup: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}; fields=[{DescribeModuleFields(module)}]");
                        }
                        continue;
                    }

                    ulong key = EncodeEngineKey(p.persistentId, m);
                    var evt = CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, deployedAnimationGroups, ut, m);
                    if (evt.HasValue)
                    {
                        PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (animation-group)");
                    }
                }
            }
        }

        private void CheckAeroSurfaceState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
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
                    if (!TryClassifyAeroSurfaceState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = EncodeEngineKey(p.persistentId, m);
                        if (loggedAeroSurfaceClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("Recorder", $"AeroSurface: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}; fields=[{DescribeModuleFields(module)}]");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted)
                        continue;

                    ulong key = EncodeEngineKey(p.persistentId, m);
                    var evt = CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, deployedAeroSurfaceModules, ut, m);
                    if (evt.HasValue)
                    {
                        PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (aero-surface)");
                    }
                }
            }
        }

        private void CheckControlSurfaceState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
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
                    if (!TryClassifyControlSurfaceState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = EncodeEngineKey(p.persistentId, m);
                        if (loggedControlSurfaceClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("Recorder", $"ControlSurface: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}; fields=[{DescribeModuleFields(module)}]");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted)
                        continue;

                    ulong key = EncodeEngineKey(p.persistentId, m);
                    var evt = CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, deployedControlSurfaceModules, ut, m);
                    if (evt.HasValue)
                    {
                        PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (control-surface)");
                    }
                }
            }
        }

        private void CheckRobotArmScannerState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
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
                    if (!TryClassifyRobotArmScannerState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = EncodeEngineKey(p.persistentId, m);
                        if (loggedRobotArmScannerClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("Recorder", $"RobotArmScanner: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}; fields=[{DescribeModuleFields(module)}]");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted)
                        continue;

                    ulong key = EncodeEngineKey(p.persistentId, m);
                    var evt = CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, deployedRobotArmScannerModules, ut, m);
                    if (evt.HasValue)
                    {
                        PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (robot-arm-scanner)");
                    }
                }
            }
        }

        /// <summary>
        /// Returns true if the part has a dedicated animation handler module (deployable, gear,
        /// cargo bay, ladder, animation group, aero/control surface, robot arm, or animate heat).
        /// Parts with dedicated handlers are skipped by CheckAnimateGenericState to avoid duplicate events.
        /// </summary>
        internal static bool HasDedicatedAnimateHandler(Part p)
        {
            if (p.FindModuleImplementing<ModuleDeployablePart>() != null) return true;
            if (p.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null) return true;
            if (p.FindModuleImplementing<ModuleCargoBay>() != null) return true;

            for (int m = 0; m < p.Modules.Count; m++)
            {
                PartModule module = p.Modules[m];
                if (module == null) continue;
                if (string.Equals(module.moduleName, "RetractableLadder", StringComparison.Ordinal)) return true;
                if (string.Equals(module.moduleName, "ModuleAnimationGroup", StringComparison.Ordinal)) return true;
                if (string.Equals(module.moduleName, "ModuleAeroSurface", StringComparison.Ordinal)) return true;
                if (string.Equals(module.moduleName, "ModuleControlSurface", StringComparison.Ordinal)) return true;
                if (string.Equals(module.moduleName, "ModuleRobotArmScanner", StringComparison.Ordinal)) return true;
                if (string.Equals(module.moduleName, "ModuleAnimateHeat", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private void CheckAnimateGenericState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                // Avoid duplicate events for modules already tracked via dedicated handlers.
                if (HasDedicatedAnimateHandler(p)) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    ModuleAnimateGeneric animateModule = p.Modules[m] as ModuleAnimateGeneric;
                    if (animateModule == null) continue;
                    if (string.IsNullOrEmpty(animateModule.animationName)) continue;

                    bool isDeployed;
                    bool isRetracted;
                    if (!TryClassifyAnimateGenericState(animateModule, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = EncodeEngineKey(p.persistentId, m);
                        if (loggedAnimateGenericClassificationMisses.Add(diagnosticKey))
                        {
                            string fields = DescribeModuleFields(animateModule);
                            if (fields.Length > 200) fields = fields.Substring(0, 200) + "...";
                            ParsekLog.Verbose("Recorder", $"AnimateGeneric: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m} anim='{animateModule.animationName}'; fields=[{fields}]");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted)
                        continue;

                    ulong key = EncodeEngineKey(p.persistentId, m);
                    var evt = CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, deployedAnimateGenericModules, ut, m);
                    if (evt.HasValue)
                    {
                        PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (anim-generic)");
                    }
                }
            }
        }

        private void CheckAnimateHeatState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
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

                    if (!TryClassifyAnimateHeatState(
                        module, out float normalizedHeat, out string sourceField))
                    {
                        ulong diagnosticKey = EncodeEngineKey(p.persistentId, m);
                        if (loggedAnimateHeatClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("Recorder", $"AnimateHeat: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}; fields=[{DescribeModuleFields(module)}]");
                        }
                        continue;
                    }

                    ulong key = EncodeEngineKey(p.persistentId, m);
                    var evt = CheckAnimateHeatTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        normalizedHeat, animateHeatLevels, ut, m);
                    if (evt.HasValue)
                    {
                        PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} " +
                            $"heat={normalizedHeat:F2} src={sourceField ?? "<unknown>"}");
                    }
                }
            }
        }

        internal static PartEvent? CheckFairingTransition(
            uint partPersistentId, string partName, bool isDeployed,
            HashSet<uint> deployedSet, double ut)
        {
            if (isDeployed && deployedSet.Add(partPersistentId))
            {
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.FairingJettisoned,
                    partName = partName
                };
            }
            return null;
        }

        private void CheckFairingState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
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
                    if (loggedFairingReadFailures.Add(p.persistentId))
                    {
                        ParsekLog.Verbose("Recorder", $"Fairing: unable to read GetScalar for '{p.partInfo?.name}' " +
                            $"pid={p.persistentId} ({ex.GetType().Name}: {ex.Message})");
                    }
                    continue;
                }

                var evt = CheckFairingTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isDeployed, deployedFairings, ut);
                if (evt.HasValue)
                {
                    PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
                }
            }
        }

        internal static List<(Part part, ModuleEngines engine, int moduleIndex)> CacheEngineModules(Vessel v)
        {
            var result = new List<(Part, ModuleEngines, int)>();
            if (v == null || v.parts == null) return result;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                int midx = 0;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    var eng = p.Modules[m] as ModuleEngines;
                    if (eng != null)
                    {
                        result.Add((p, eng, midx));
                        midx++;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Builds an OrbitSegment from the vessel's current orbital parameters.
        /// Extracted to deduplicate the identical construction in StartRecording,
        /// OnVesselGoOnRails, OnVesselSOIChanged, and TransitionToBackground.
        /// </summary>
        internal static OrbitSegment CreateOrbitSegmentFromVessel(Vessel v, double startUT)
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
                bodyName = v.mainBody.name
            };
        }

        internal static ulong EncodeEngineKey(uint pid, int moduleIndex)
        {
            return ((ulong)pid << 8) | (uint)moduleIndex;
        }

        internal static void DecodeEngineKey(ulong key, out uint pid, out int moduleIndex)
        {
            pid = (uint)(key >> 8);
            moduleIndex = (int)(key & 0xFF);
        }

        /// <summary>
        /// Emits synthetic EngineShutdown, RCSStopped, and RoboticMotionStopped events
        /// for all engines/RCS/robotics still active when a recording ends (user stop,
        /// chain boundary, scene change). Without these, ghost plumes and FX may stay
        /// frozen at the last throttle level instead of shutting down at recording end.
        /// Bug #108.
        /// </summary>
        internal static List<PartEvent> EmitTerminalEngineAndRcsEvents(
            HashSet<ulong> activeEngineKeys,
            HashSet<ulong> activeRcsKeys,
            HashSet<ulong> activeRoboticKeys,
            Dictionary<ulong, float> lastRoboticPosition,
            double finalUT,
            string logTag)
        {
            var events = new List<PartEvent>();

            // Terminal EngineShutdown for each active engine
            if (activeEngineKeys != null && activeEngineKeys.Count > 0)
            {
                // Snapshot keys before iterating (we don't modify the set here, but defensive)
                var keys = new List<ulong>(activeEngineKeys);
                for (int i = 0; i < keys.Count; i++)
                {
                    uint pid; int midx;
                    DecodeEngineKey(keys[i], out pid, out midx);
                    events.Add(new PartEvent
                    {
                        ut = finalUT,
                        partPersistentId = pid,
                        eventType = PartEventType.EngineShutdown,
                        partName = "unknown",
                        moduleIndex = midx
                    });
                    ParsekLog.Verbose(logTag,
                        $"Terminal event: EngineShutdown pid={pid} midx={midx} at UT={finalUT.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
                }
            }

            // Terminal RCSStopped for each active RCS
            if (activeRcsKeys != null && activeRcsKeys.Count > 0)
            {
                var keys = new List<ulong>(activeRcsKeys);
                for (int i = 0; i < keys.Count; i++)
                {
                    uint pid; int midx;
                    DecodeEngineKey(keys[i], out pid, out midx);
                    events.Add(new PartEvent
                    {
                        ut = finalUT,
                        partPersistentId = pid,
                        eventType = PartEventType.RCSStopped,
                        partName = "unknown",
                        moduleIndex = midx
                    });
                    ParsekLog.Verbose(logTag,
                        $"Terminal event: RCSStopped pid={pid} midx={midx} at UT={finalUT.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
                }
            }

            // Terminal RoboticMotionStopped for each active robotic module
            if (activeRoboticKeys != null && activeRoboticKeys.Count > 0)
            {
                var keys = new List<ulong>(activeRoboticKeys);
                for (int i = 0; i < keys.Count; i++)
                {
                    uint pid; int midx;
                    DecodeEngineKey(keys[i], out pid, out midx);
                    float lastPos = 0f;
                    if (lastRoboticPosition != null)
                        lastRoboticPosition.TryGetValue(keys[i], out lastPos);
                    events.Add(new PartEvent
                    {
                        ut = finalUT,
                        partPersistentId = pid,
                        eventType = PartEventType.RoboticMotionStopped,
                        partName = "unknown",
                        value = lastPos,
                        moduleIndex = midx
                    });
                    ParsekLog.Verbose(logTag,
                        $"Terminal event: RoboticMotionStopped pid={pid} midx={midx} pos={lastPos.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} at UT={finalUT.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
                }
            }

            if (events.Count > 0)
            {
                ParsekLog.Info(logTag,
                    $"Emitted {events.Count} terminal events at UT={finalUT.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}" +
                    $" (engines={activeEngineKeys?.Count ?? 0}, rcs={activeRcsKeys?.Count ?? 0}, robotics={activeRoboticKeys?.Count ?? 0})");
            }

            return events;
        }

        private static string FormatCoverageEntries(List<string> entries, int maxEntries = 8)
        {
            if (entries == null || entries.Count == 0)
                return "<none>";

            if (entries.Count <= maxEntries)
                return string.Join(", ", entries.ToArray());

            var preview = entries.GetRange(0, maxEntries);
            return string.Join(", ", preview.ToArray()) + $", +{entries.Count - maxEntries} more";
        }

        private static void LogCoverageDetails(string label, List<string> entries)
        {
            ParsekLog.Verbose("Recorder", $"  Visual coverage [{label}] {entries.Count}: {FormatCoverageEntries(entries)}");
        }

        private sealed class VisualCoverageAccumulator
        {
            public int ModuleCount;
            public readonly List<string> ParachuteParts = new List<string>();
            public readonly List<string> JettisonModules = new List<string>();
            public readonly List<string> DeployableParts = new List<string>();
            public readonly List<string> LadderModules = new List<string>();
            public readonly List<string> AnimationGroupModules = new List<string>();
            public readonly List<string> AnimateGenericModules = new List<string>();
            public readonly List<string> LightParts = new List<string>();
            public readonly List<string> GearModules = new List<string>();
            public readonly List<string> CargoBayParts = new List<string>();
            public readonly List<string> FairingParts = new List<string>();
            public readonly List<string> AeroSurfaceModules = new List<string>();
            public readonly List<string> ControlSurfaceModules = new List<string>();
            public readonly List<string> RobotArmScannerModules = new List<string>();
            public readonly List<string> AnimateHeatModules = new List<string>();
            public readonly List<string> EngineModules = new List<string>();
            public readonly List<string> RcsModules = new List<string>();
            public readonly List<string> RoboticModules = new List<string>();
        }

        private void LogVisualRecordingCoverage(Vessel v)
        {
            if (v == null || v.parts == null)
                return;

            var coverage = new VisualCoverageAccumulator();

            CollectPartVisualCoverage(v, coverage);
            CollectCachedEngineVisualCoverage(coverage);
            CollectCachedRcsVisualCoverage(coverage);
            CollectCachedRoboticVisualCoverage(coverage);
            LogVisualCoverageSummary(v, coverage);
            LogVisualCoverageDetails(coverage);
        }

        private static void CollectPartVisualCoverage(
            Vessel v,
            VisualCoverageAccumulator coverage)
        {
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                string partName = p.partInfo?.name ?? "unknown";
                string partRef = $"{partName}[pid={p.persistentId}]";

                if (p.FindModuleImplementing<ModuleParachute>() != null)
                    coverage.ParachuteParts.Add(partRef);

                ModuleDeployablePart deployable = p.FindModuleImplementing<ModuleDeployablePart>();
                if (deployable != null)
                    coverage.DeployableParts.Add(
                        $"{partRef}(state={deployable.deployState})");

                if (p.FindModuleImplementing<ModuleLight>() != null)
                    coverage.LightParts.Add(partRef);

                ModuleCargoBay cargoBay = p.FindModuleImplementing<ModuleCargoBay>();
                if (cargoBay != null)
                    coverage.CargoBayParts.Add(
                        $"{partRef}(deployIdx={cargoBay.DeployModuleIndex},closed={cargoBay.closedPosition:F2})");

                if (p.FindModuleImplementing<ModuleProceduralFairing>() != null)
                    coverage.FairingParts.Add(partRef);

                bool hasRetractableLadder = false;
                bool hasAnimationGroup = false;
                bool hasAeroSurface = false;
                bool hasControlSurface = false;
                bool hasRobotArmScanner = false;
                bool hasAnimateHeat = false;
                bool hasDedicatedAnimateHandler =
                    deployable != null ||
                    p.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null ||
                    cargoBay != null;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    coverage.ModuleCount++;

                    string moduleName = module.moduleName ?? string.Empty;

                    var jettison = module as ModuleJettison;
                    if (jettison != null && !string.IsNullOrWhiteSpace(jettison.jettisonName))
                    {
                        coverage.JettisonModules.Add(
                            $"{partRef}(midx={m},names={jettison.jettisonName})");
                    }

                    var wheel = module as ModuleWheels.ModuleWheelDeployment;
                    if (wheel != null)
                    {
                        coverage.GearModules.Add(
                            $"{partRef}(midx={m},state='{wheel.stateString}')");
                    }

                    if (string.Equals(moduleName, "RetractableLadder", StringComparison.Ordinal))
                    {
                        coverage.LadderModules.Add($"{partRef}(midx={m})");
                        hasRetractableLadder = true;
                    }

                    if (string.Equals(moduleName, "ModuleAnimationGroup", StringComparison.Ordinal))
                    {
                        coverage.AnimationGroupModules.Add($"{partRef}(midx={m})");
                        hasAnimationGroup = true;
                    }

                    if (string.Equals(moduleName, "ModuleAeroSurface", StringComparison.Ordinal))
                    {
                        coverage.AeroSurfaceModules.Add($"{partRef}(midx={m})");
                        hasAeroSurface = true;
                    }

                    if (string.Equals(moduleName, "ModuleControlSurface", StringComparison.Ordinal))
                    {
                        coverage.ControlSurfaceModules.Add($"{partRef}(midx={m})");
                        hasControlSurface = true;
                    }

                    if (string.Equals(moduleName, "ModuleRobotArmScanner", StringComparison.Ordinal))
                    {
                        coverage.RobotArmScannerModules.Add($"{partRef}(midx={m})");
                        hasRobotArmScanner = true;
                    }

                    if (string.Equals(moduleName, "ModuleAnimateHeat", StringComparison.Ordinal))
                    {
                        coverage.AnimateHeatModules.Add($"{partRef}(midx={m})");
                        hasAnimateHeat = true;
                    }
                }

                if (hasDedicatedAnimateHandler || hasRetractableLadder || hasAnimationGroup ||
                    hasAeroSurface || hasControlSurface || hasRobotArmScanner || hasAnimateHeat)
                    continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    var animateModule = p.Modules[m] as ModuleAnimateGeneric;
                    if (animateModule == null) continue;
                    if (string.IsNullOrEmpty(animateModule.animationName)) continue;

                    coverage.AnimateGenericModules.Add(
                        $"{partRef}(midx={m},anim={animateModule.animationName})");
                }
            }
        }

        private void CollectCachedEngineVisualCoverage(VisualCoverageAccumulator coverage)
        {
            if (cachedEngines != null)
            {
                for (int i = 0; i < cachedEngines.Count; i++)
                {
                    var (part, engine, moduleIndex) = cachedEngines[i];
                    if (part == null || engine == null) continue;
                    string partName = part.partInfo?.name ?? "unknown";
                    string engineId = string.IsNullOrEmpty(engine.engineID) ? "<none>" : engine.engineID;
                    int thrustTransformCount = engine.thrustTransforms != null ? engine.thrustTransforms.Count : 0;
                    coverage.EngineModules.Add(
                        $"{partName}[pid={part.persistentId}](midx={moduleIndex},id={engineId},thrust={thrustTransformCount})");
                }
            }
        }

        private void CollectCachedRcsVisualCoverage(VisualCoverageAccumulator coverage)
        {
            if (cachedRcsModules != null)
            {
                for (int i = 0; i < cachedRcsModules.Count; i++)
                {
                    var (part, rcs, moduleIndex) = cachedRcsModules[i];
                    if (part == null || rcs == null) continue;
                    string partName = part.partInfo?.name ?? "unknown";
                    int thrusterCount = rcs.thrusterTransforms != null ? rcs.thrusterTransforms.Count : 0;
                    int forceCount = rcs.thrustForces != null ? rcs.thrustForces.Length : 0;
                    coverage.RcsModules.Add(
                        $"{partName}[pid={part.persistentId}](midx={moduleIndex},thrusters={thrusterCount},forces={forceCount},power={rcs.thrusterPower:F1})");
                }
            }
        }

        private void CollectCachedRoboticVisualCoverage(VisualCoverageAccumulator coverage)
        {
            if (cachedRoboticModules != null)
            {
                for (int i = 0; i < cachedRoboticModules.Count; i++)
                {
                    var (part, _, moduleIndex, moduleName) = cachedRoboticModules[i];
                    if (part == null) continue;
                    string partName = part.partInfo?.name ?? "unknown";
                    coverage.RoboticModules.Add(
                        $"{partName}[pid={part.persistentId}](midx={moduleIndex},module={moduleName})");
                }
            }
        }

        private static void LogVisualCoverageSummary(
            Vessel v,
            VisualCoverageAccumulator coverage)
        {
            ParsekLog.Verbose("Recorder", 
                $"Visual recording coverage for '{v.vesselName}' pid={v.persistentId}: " +
                $"parts={v.parts.Count} modules={coverage.ModuleCount} " +
                $"parachute={coverage.ParachuteParts.Count} jettison={coverage.JettisonModules.Count} deployable={coverage.DeployableParts.Count} " +
                $"ladder={coverage.LadderModules.Count} animationGroup={coverage.AnimationGroupModules.Count} animateGeneric={coverage.AnimateGenericModules.Count} " +
                $"lights={coverage.LightParts.Count} gear={coverage.GearModules.Count} cargoBay={coverage.CargoBayParts.Count} fairing={coverage.FairingParts.Count} " +
                $"aeroSurface={coverage.AeroSurfaceModules.Count} controlSurface={coverage.ControlSurfaceModules.Count} " +
                $"robotArmScanner={coverage.RobotArmScannerModules.Count} animateHeat={coverage.AnimateHeatModules.Count} " +
                $"engine={coverage.EngineModules.Count} rcs={coverage.RcsModules.Count} robotics={coverage.RoboticModules.Count}");
        }

        private static void LogVisualCoverageDetails(VisualCoverageAccumulator coverage)
        {
            LogCoverageDetails("Parachute", coverage.ParachuteParts);
            LogCoverageDetails("Jettison", coverage.JettisonModules);
            LogCoverageDetails("Deployable", coverage.DeployableParts);
            LogCoverageDetails("Ladder", coverage.LadderModules);
            LogCoverageDetails("AnimationGroup", coverage.AnimationGroupModules);
            LogCoverageDetails("AnimateGeneric", coverage.AnimateGenericModules);
            LogCoverageDetails("Light", coverage.LightParts);
            LogCoverageDetails("Gear", coverage.GearModules);
            LogCoverageDetails("CargoBay", coverage.CargoBayParts);
            LogCoverageDetails("Fairing", coverage.FairingParts);
            LogCoverageDetails("AeroSurface", coverage.AeroSurfaceModules);
            LogCoverageDetails("ControlSurface", coverage.ControlSurfaceModules);
            LogCoverageDetails("RobotArmScanner", coverage.RobotArmScannerModules);
            LogCoverageDetails("AnimateHeat", coverage.AnimateHeatModules);
            LogCoverageDetails("Engine", coverage.EngineModules);
            LogCoverageDetails("RCS", coverage.RcsModules);
            LogCoverageDetails("Robotics", coverage.RoboticModules);
        }

        internal static void CheckEngineTransition(
            ulong key, uint pid, int moduleIndex, string partName,
            bool ignited, float throttle,
            HashSet<ulong> activeSet, Dictionary<ulong, float> lastThrottleMap, double ut,
            List<PartEvent> events)
        {
            bool wasActive = activeSet.Contains(key);

            if (ignited && !wasActive)
            {
                activeSet.Add(key);
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = pid,
                    eventType = PartEventType.EngineIgnited,
                    partName = partName,
                    value = throttle,
                    moduleIndex = moduleIndex
                });
                lastThrottleMap[key] = throttle;
            }
            else if (!ignited && wasActive)
            {
                activeSet.Remove(key);
                lastThrottleMap.Remove(key);
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = pid,
                    eventType = PartEventType.EngineShutdown,
                    partName = partName,
                    moduleIndex = moduleIndex
                });
            }
            else if (ignited && wasActive)
            {
                float lastT;
                if (!lastThrottleMap.TryGetValue(key, out lastT))
                    lastT = 0f;

                float delta = throttle - lastT;
                if (delta > 0.05f || delta < -0.05f)
                {
                    lastThrottleMap[key] = throttle;
                    events.Add(new PartEvent
                    {
                        ut = ut,
                        partPersistentId = pid,
                        eventType = PartEventType.EngineThrottle,
                        partName = partName,
                        value = throttle,
                        moduleIndex = moduleIndex
                    });
                }
            }
        }

        private void CheckEngineState(Vessel v)
        {
            if (cachedEngines == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < cachedEngines.Count; i++)
            {
                var (part, engine, moduleIndex) = cachedEngines[i];
                if (part == null || engine == null) continue;

                ulong key = EncodeEngineKey(part.persistentId, moduleIndex);
                bool ignited = engine.EngineIgnited && engine.isOperational;
                float throttle = engine.currentThrottle;
                if (loggedEngineModuleKeys.Add(key))
                {
                    int thrustTransformCount = engine.thrustTransforms != null
                        ? engine.thrustTransforms.Count
                        : 0;
                    string engineId = string.IsNullOrEmpty(engine.engineID) ? "<none>" : engine.engineID;
                    ParsekLog.Verbose("Recorder", $"Engine tracking: '{part.partInfo?.name}' pid={part.persistentId} " +
                        $"midx={moduleIndex} id={engineId} thrustTransforms={thrustTransformCount}");
                }

                reusableEventBuffer.Clear();
                CheckEngineTransition(
                    key, part.persistentId, moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    ignited, throttle,
                    activeEngineKeys, lastThrottle, ut, reusableEventBuffer);

                for (int e = 0; e < reusableEventBuffer.Count; e++)
                {
                    PartEvents.Add(reusableEventBuffer[e]);
                    ParsekLog.Verbose("Recorder", $"Part event: {reusableEventBuffer[e].eventType} '{reusableEventBuffer[e].partName}' " +
                        $"pid={reusableEventBuffer[e].partPersistentId} midx={reusableEventBuffer[e].moduleIndex} val={reusableEventBuffer[e].value:F2}");

                    // Some engines with ModuleJettison (e.g. Mainsail/Skipper/Vector) can
                    // visually drop covers on ignition even when isJettisoned polling lags.
                    // Emit a one-shot shroud event on ignition as a reliable fallback.
                    if (reusableEventBuffer[e].eventType == PartEventType.EngineIgnited &&
                        part.FindModuleImplementing<ModuleJettison>() != null)
                    {
                        var shroudEvt = CheckJettisonTransition(
                            part.persistentId,
                            part.partInfo?.name ?? "unknown",
                            isJettisoned: true,
                            jettisonedSet: jettisonedShrouds,
                            ut: ut);
                        if (shroudEvt.HasValue)
                        {
                            PartEvents.Add(shroudEvt.Value);
                            ParsekLog.Info("Recorder", $"Part event: {shroudEvt.Value.eventType} '{shroudEvt.Value.partName}' " +
                                $"pid={shroudEvt.Value.partPersistentId} (engine-ignite fallback)");
                        }
                    }
                }
            }
        }

        internal static List<(Part part, ModuleRCS rcs, int moduleIndex)> CacheRcsModules(Vessel v)
        {
            var result = new List<(Part, ModuleRCS, int)>();
            if (v == null || v.parts == null) return result;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                int midx = 0;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    var rcs = p.Modules[m] as ModuleRCS;
                    if (rcs != null)
                    {
                        result.Add((p, rcs, midx));
                        midx++;
                    }
                }
            }
            return result;
        }

        internal static float ComputeRcsPower(float[] thrustForces, float thrusterPower)
        {
            if (thrusterPower <= 0f || thrustForces == null || thrustForces.Length == 0)
                return 0f;

            float sum = 0f;
            for (int i = 0; i < thrustForces.Length; i++)
                sum += thrustForces[i];

            float power = sum / (thrusterPower * thrustForces.Length);
            if (power < 0f) power = 0f;
            if (power > 1f) power = 1f;
            return power;
        }

        /// <summary>
        /// Returns true when the RCS frame count has exactly reached the debounce threshold,
        /// meaning this is the first frame where recording should start.
        /// </summary>
        internal static bool ShouldStartRcsRecording(int frameCount, int threshold)
        {
            return frameCount == threshold;
        }

        /// <summary>
        /// Returns true when the RCS frame count is at or above the debounce threshold,
        /// meaning the activation was sustained and is being recorded.
        /// </summary>
        internal static bool IsRcsRecordingSustained(int frameCount, int threshold)
        {
            return frameCount >= threshold;
        }

        internal static void CheckRcsTransition(
            ulong key, uint pid, int moduleIndex, string partName,
            bool active, float power,
            HashSet<ulong> activeSet, Dictionary<ulong, float> lastThrottleMap, double ut,
            List<PartEvent> events)
        {
            bool wasActive = activeSet.Contains(key);

            if (active && !wasActive)
            {
                activeSet.Add(key);
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = pid,
                    eventType = PartEventType.RCSActivated,
                    partName = partName,
                    value = power,
                    moduleIndex = moduleIndex
                });
                lastThrottleMap[key] = power;
            }
            else if (!active && wasActive)
            {
                activeSet.Remove(key);
                lastThrottleMap.Remove(key);
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = pid,
                    eventType = PartEventType.RCSStopped,
                    partName = partName,
                    moduleIndex = moduleIndex
                });
            }
            else if (active && wasActive)
            {
                float lastP;
                if (!lastThrottleMap.TryGetValue(key, out lastP))
                    lastP = 0f;

                float delta = power - lastP;
                if (delta > 0.05f || delta < -0.05f)
                {
                    lastThrottleMap[key] = power;
                    events.Add(new PartEvent
                    {
                        ut = ut,
                        partPersistentId = pid,
                        eventType = PartEventType.RCSThrottle,
                        partName = partName,
                        value = power,
                        moduleIndex = moduleIndex
                    });
                }
            }
        }

        /// <summary>
        /// Shared RCS debounce state machine. Manages frame counting, debounce
        /// threshold crossing, throttle change detection, and micro-correction
        /// filtering. Returns any part events produced this frame.
        /// </summary>
        internal static void ProcessRcsDebounce(
            ulong key, uint pid, int moduleIndex, string partName,
            bool active, float[] thrustForces, float thrusterPower, double ut,
            Dictionary<ulong, int> frameCountMap,
            HashSet<ulong> activeSet, Dictionary<ulong, float> throttleMap,
            List<PartEvent> events)
        {
            if (active)
            {
                int count;
                frameCountMap.TryGetValue(key, out count);
                count++;
                frameCountMap[key] = count;

                if (ShouldStartRcsRecording(count, RcsDebounceFrameThreshold))
                {
                    // Debounce threshold crossed — emit RCSActivated
                    float power = ComputeRcsPower(thrustForces, thrusterPower);
                    CheckRcsTransition(
                        key, pid, moduleIndex, partName,
                        true, power, activeSet, throttleMap, ut, events);
                }
                else if (count > RcsDebounceFrameThreshold)
                {
                    // Already recording — check for throttle changes
                    float power = ComputeRcsPower(thrustForces, thrusterPower);
                    CheckRcsTransition(
                        key, pid, moduleIndex, partName,
                        true, power, activeSet, throttleMap, ut, events);
                }
                // else: count < threshold, still debouncing — skip
            }
            else
            {
                // RCS not active this frame
                int count;
                if (frameCountMap.TryGetValue(key, out count))
                {
                    if (IsRcsRecordingSustained(count, RcsDebounceFrameThreshold))
                    {
                        // Was a sustained activation — emit RCSStopped
                        CheckRcsTransition(
                            key, pid, moduleIndex, partName,
                            false, 0f, activeSet, throttleMap, ut, events);
                    }
                    else
                    {
                        // Filtered micro-correction — clean up any stale state
                        activeSet.Remove(key);
                        throttleMap.Remove(key);
                    }
                    frameCountMap.Remove(key);
                }
            }
        }

        private void CheckRcsState(Vessel v)
        {
            if (cachedRcsModules == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < cachedRcsModules.Count; i++)
            {
                var (part, rcs, moduleIndex) = cachedRcsModules[i];
                if (part == null || rcs == null) continue;

                ulong key = EncodeEngineKey(part.persistentId, moduleIndex);
                bool active = rcs.rcs_active && rcs.rcsEnabled;
                if (loggedRcsModuleKeys.Add(key))
                {
                    int thrusterCount = rcs.thrusterTransforms != null ? rcs.thrusterTransforms.Count : 0;
                    int forceCount = rcs.thrustForces != null ? rcs.thrustForces.Length : 0;
                    ParsekLog.Verbose("Recorder", $"RCS tracking: '{part.partInfo?.name}' pid={part.persistentId} " +
                        $"midx={moduleIndex} thrusters={thrusterCount} forces={forceCount} power={rcs.thrusterPower:F2}");
                }

                uint pid = part.persistentId;
                string partName = part.partInfo?.name ?? "unknown";

                reusableEventBuffer.Clear();
                ProcessRcsDebounce(
                    key, pid, moduleIndex, partName,
                    active, rcs.thrustForces, rcs.thrusterPower, ut,
                    rcsActiveFrameCount, activeRcsKeys, lastRcsThrottle, reusableEventBuffer);
                for (int e = 0; e < reusableEventBuffer.Count; e++)
                {
                    PartEvents.Add(reusableEventBuffer[e]);
                    ParsekLog.Verbose("Recorder", $"Part event: {reusableEventBuffer[e].eventType} '{reusableEventBuffer[e].partName}' " +
                        $"pid={reusableEventBuffer[e].partPersistentId} midx={reusableEventBuffer[e].moduleIndex} val={reusableEventBuffer[e].value:F2}");
                }
            }
        }

        private static bool IsWheelRoboticModuleName(string moduleName)
        {
            return string.Equals(moduleName, "ModuleWheelSuspension", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleWheelSteering", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleWheelMotor", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleWheelMotorSteering", StringComparison.Ordinal);
        }

        internal static bool IsRoboticModuleName(string moduleName)
        {
            return string.Equals(moduleName, "ModuleRoboticServoHinge", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleRoboticServoPiston", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleRoboticRotationServo", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleRoboticServoRotor", StringComparison.Ordinal) ||
                   IsWheelRoboticModuleName(moduleName);
        }

        internal static List<(Part part, PartModule module, int moduleIndex, string moduleName)> CacheRoboticModules(Vessel v)
        {
            var result = new List<(Part, PartModule, int, string)>();
            if (v == null || v.parts == null) return result;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                int roboticModuleIndex = 0;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;

                    string moduleName = module.moduleName;
                    if (!IsRoboticModuleName(moduleName)) continue;

                    result.Add((p, module, roboticModuleIndex, moduleName));
                    roboticModuleIndex++;
                }
            }

            return result;
        }

        private static BaseField FindModuleField(PartModule module, string fieldName)
        {
            if (module == null || module.Fields == null || string.IsNullOrEmpty(fieldName))
                return null;

            try
            {
                return module.Fields[fieldName];
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited("Recorder", $"field-{fieldName}",
                    $"FindModuleField exception for '{fieldName}' on {module.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static bool TryParseFloatValue(object rawValue, out float value)
        {
            value = 0f;
            if (rawValue == null) return false;

            if (rawValue is float f)
            {
                value = f;
                return !float.IsNaN(value) && !float.IsInfinity(value);
            }

            if (rawValue is double d)
            {
                value = (float)d;
                return !float.IsNaN(value) && !float.IsInfinity(value);
            }

            if (rawValue is int i)
            {
                value = i;
                return true;
            }

            if (rawValue is uint ui)
            {
                value = ui;
                return true;
            }

            if (rawValue is bool b)
            {
                value = b ? 1f : 0f;
                return true;
            }

            string text = rawValue.ToString();
            if (string.IsNullOrEmpty(text)) return false;
            text = text.Trim();

            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return !float.IsNaN(value) && !float.IsInfinity(value);

            // Some KSP fields serialize vectors/quaternions as comma-separated values.
            string[] parts = text.Split(',');
            if (parts.Length > 0 &&
                float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return !float.IsNaN(value) && !float.IsInfinity(value);
            }

            return false;
        }

        private static bool TryParseBoolValue(object rawValue, out bool value)
        {
            value = false;
            if (rawValue == null) return false;

            if (rawValue is bool b)
            {
                value = b;
                return true;
            }

            if (rawValue is int i)
            {
                value = i != 0;
                return true;
            }

            string text = rawValue.ToString();
            if (string.IsNullOrEmpty(text)) return false;
            text = text.Trim();

            if (bool.TryParse(text, out value)) return true;

            int intValue;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
            {
                value = intValue != 0;
                return true;
            }

            return false;
        }

        private static bool TryParseVector3Value(object rawValue, out Vector3 value)
        {
            value = Vector3.zero;
            if (rawValue == null) return false;

            if (rawValue is Vector3 vec)
            {
                value = vec;
                return true;
            }

            string text = rawValue.ToString();
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split(',');
            if (parts.Length != 3) return false;

            float x;
            float y;
            float z;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;

            value = new Vector3(x, y, z);
            return true;
        }

        private static bool TryParseQuaternionValue(object rawValue, out Quaternion value)
        {
            value = Quaternion.identity;
            if (rawValue == null) return false;

            if (rawValue is Quaternion quat)
            {
                value = quat;
                return true;
            }

            string text = rawValue.ToString();
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split(',');
            if (parts.Length != 4) return false;

            float x;
            float y;
            float z;
            float w;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
            if (!float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out w)) return false;

            value = new Quaternion(x, y, z, w);
            return true;
        }

        private static bool TryReadModuleFloatField(PartModule module, string fieldName, out float value)
        {
            value = 0f;
            BaseField field = FindModuleField(module, fieldName);
            if (field == null) return false;
            return TryParseFloatValue(field.GetValue(module), out value);
        }

        private static float NormalizeAnimateHeatScalar(float raw)
        {
            if (float.IsNaN(raw) || float.IsInfinity(raw))
                return 0f;

            // Common case: ModuleAnimateHeat animTime style 0..1 scalar.
            if (raw >= -0.25f && raw <= 1.25f)
                return Mathf.Clamp01(raw);

            // Percent-like values.
            if (raw >= 0f && raw <= 100f)
                return Mathf.Clamp01(raw / 100f);

            // Absolute-temperature fallback (ambient ~300K, hot ~1500K+).
            if (raw >= 250f && raw <= 2500f)
                return Mathf.Clamp01((raw - 300f) / 1200f);

            return Mathf.Clamp01(raw);
        }

        private static bool TryReadModuleBoolField(PartModule module, string fieldName, out bool value)
        {
            value = false;
            BaseField field = FindModuleField(module, fieldName);
            if (field == null) return false;
            return TryParseBoolValue(field.GetValue(module), out value);
        }

        private static bool TryReadModuleVector3Field(PartModule module, string fieldName, out Vector3 value)
        {
            value = Vector3.zero;
            BaseField field = FindModuleField(module, fieldName);
            if (field == null) return false;
            return TryParseVector3Value(field.GetValue(module), out value);
        }

        private static bool TryReadModuleQuaternionField(PartModule module, string fieldName, out Quaternion value)
        {
            value = Quaternion.identity;
            BaseField field = FindModuleField(module, fieldName);
            if (field == null) return false;
            return TryParseQuaternionValue(field.GetValue(module), out value);
        }

        internal static bool TryGetRoboticMovingState(PartModule module, out bool moving)
        {
            moving = false;
            string[] movingFields =
            {
                "servoIsMoving",
                "isMoving",
                "moving",
                "isTraversing",
                "isRotating"
            };

            for (int i = 0; i < movingFields.Length; i++)
            {
                if (TryReadModuleBoolField(module, movingFields[i], out moving))
                    return true;
            }

            return false;
        }

        internal static bool TryGetWheelRoboticPositionValue(
            PartModule module,
            string moduleName,
            out float positionValue,
            out float deadband,
            out string sourceField)
        {
            positionValue = 0f;
            sourceField = null;

            string[] preferredScalarFields;
            if (string.Equals(moduleName, "ModuleWheelSuspension", StringComparison.Ordinal))
            {
                deadband = 0.0025f;
                preferredScalarFields = new[]
                {
                    "currentSuspensionOffset",
                    "suspensionOffset",
                    "compression",
                    "suspensionCompression",
                    "suspensionTravel"
                };
            }
            else if (string.Equals(moduleName, "ModuleWheelSteering", StringComparison.Ordinal))
            {
                deadband = 0.25f;
                preferredScalarFields = new[]
                {
                    "steeringAngle",
                    "currentSteering",
                    "steerAngle",
                    "steeringInput"
                };
            }
            else
            {
                deadband = 1f;
                preferredScalarFields = new[]
                {
                    "currentRPM",
                    "rpm",
                    "wheelRPM",
                    "motorRPM",
                    "targetRPM",
                    "driveOutput",
                    "motorOutput",
                    "wheelSpeed"
                };
            }

            for (int i = 0; i < preferredScalarFields.Length; i++)
            {
                if (TryReadModuleFloatField(module, preferredScalarFields[i], out positionValue))
                {
                    sourceField = preferredScalarFields[i];
                    return true;
                }
            }

            if (string.Equals(moduleName, "ModuleWheelSuspension", StringComparison.Ordinal))
            {
                if (TryReadModuleVector3Field(module, "suspensionPos", out Vector3 suspensionPos))
                {
                    positionValue = suspensionPos.magnitude;
                    sourceField = "suspensionPos";
                    return true;
                }
            }

            if (string.Equals(moduleName, "ModuleWheelMotorSteering", StringComparison.Ordinal) &&
                TryReadModuleFloatField(module, "steeringAngle", out positionValue))
            {
                deadband = 0.25f;
                sourceField = "steeringAngle";
                return true;
            }

            return false;
        }

        internal static bool TryGetRoboticPositionValue(
            PartModule module, string moduleName, out float positionValue,
            out float deadband, out string sourceField)
        {
            positionValue = 0f;
            sourceField = null;
            deadband = string.Equals(moduleName, "ModuleRoboticServoPiston", StringComparison.Ordinal)
                ? roboticLinearDeadbandMeters
                : roboticAngularDeadbandDegrees;

            if (IsWheelRoboticModuleName(moduleName))
                return TryGetWheelRoboticPositionValue(
                    module, moduleName, out positionValue, out deadband, out sourceField);

            string[] preferredScalarFields;
            if (string.Equals(moduleName, "ModuleRoboticServoPiston", StringComparison.Ordinal))
            {
                preferredScalarFields = new[]
                {
                    "currentPosition",
                    "position",
                    "targetPosition",
                    "traverseVelocity"
                };
            }
            else if (string.Equals(moduleName, "ModuleRoboticServoRotor", StringComparison.Ordinal))
            {
                preferredScalarFields = new[]
                {
                    "currentRPM",
                    "rpm",
                    "rpmLimit"
                };
            }
            else
            {
                preferredScalarFields = new[]
                {
                    "currentAngle",
                    "angle",
                    "targetAngle"
                };
            }

            for (int i = 0; i < preferredScalarFields.Length; i++)
            {
                if (TryReadModuleFloatField(module, preferredScalarFields[i], out positionValue))
                {
                    sourceField = preferredScalarFields[i];
                    return true;
                }
            }

            if (TryReadModuleVector3Field(module, "servoTransformPosition", out Vector3 servoPos))
            {
                positionValue = servoPos.magnitude;
                sourceField = "servoTransformPosition";
                return true;
            }

            if (TryReadModuleQuaternionField(module, "servoTransformRotation", out Quaternion servoRot))
            {
                positionValue = Quaternion.Angle(Quaternion.identity, servoRot);
                sourceField = "servoTransformRotation";
                return true;
            }

            return false;
        }

        private static string DescribeModuleFields(PartModule module)
        {
            if (module == null || module.Fields == null) return "<none>";

            var names = new List<string>();
            for (int i = 0; i < module.Fields.Count; i++)
            {
                BaseField field = module.Fields[i];
                if (field == null || string.IsNullOrEmpty(field.name)) continue;
                names.Add(field.name);
            }

            return names.Count == 0 ? "<none>" : string.Join(", ", names.ToArray());
        }

        internal static List<PartEvent> CheckRoboticTransition(
            ulong key, uint pid, int moduleIndex, string partName,
            bool movingSignal, float positionValue, float deadband, double ut,
            HashSet<ulong> movingSet, Dictionary<ulong, float> lastPositionMap,
            Dictionary<ulong, double> lastSampleMap, double sampleIntervalSeconds)
        {
            var events = new List<PartEvent>();
            bool emittedEvent = false;
            bool wasMoving = movingSet.Contains(key);

            float lastPosition;
            bool hadLastPosition = lastPositionMap.TryGetValue(key, out lastPosition);
            float delta = hadLastPosition ? Mathf.Abs(positionValue - lastPosition) : float.PositiveInfinity;
            bool changedEnough = !hadLastPosition || delta >= deadband;
            bool inferredMoving = hadLastPosition && delta >= Math.Max(deadband * 0.25f, 0.0001f);
            bool movingNow = movingSignal || inferredMoving;

            if (movingNow && !wasMoving)
            {
                movingSet.Add(key);
                lastSampleMap[key] = ut;
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = pid,
                    eventType = PartEventType.RoboticMotionStarted,
                    partName = partName,
                    value = positionValue,
                    moduleIndex = moduleIndex
                });
                emittedEvent = true;
            }
            else if (!movingNow && wasMoving)
            {
                movingSet.Remove(key);
                lastSampleMap.Remove(key);
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = pid,
                    eventType = PartEventType.RoboticMotionStopped,
                    partName = partName,
                    value = positionValue,
                    moduleIndex = moduleIndex
                });
                emittedEvent = true;
            }
            else if (movingNow && wasMoving)
            {
                double lastSampleUT;
                bool sampleDue = !lastSampleMap.TryGetValue(key, out lastSampleUT) ||
                    (ut - lastSampleUT) >= sampleIntervalSeconds;
                if (sampleDue && changedEnough)
                {
                    lastSampleMap[key] = ut;
                    events.Add(new PartEvent
                    {
                        ut = ut,
                        partPersistentId = pid,
                        eventType = PartEventType.RoboticPositionSample,
                        partName = partName,
                        value = positionValue,
                        moduleIndex = moduleIndex
                    });
                    emittedEvent = true;
                }
            }

            if (emittedEvent || !hadLastPosition)
                lastPositionMap[key] = positionValue;

            return events;
        }

        private void CheckRoboticState(Vessel v)
        {
            if (cachedRoboticModules == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < cachedRoboticModules.Count; i++)
            {
                var (part, module, moduleIndex, moduleName) = cachedRoboticModules[i];
                if (part == null || module == null) continue;

                ulong key = EncodeEngineKey(part.persistentId, moduleIndex);

                bool hasMovingSignal = TryGetRoboticMovingState(module, out bool movingSignal);
                bool hasPosition = TryGetRoboticPositionValue(
                    module, moduleName, out float positionValue, out float deadband, out string sourceField);

                if (!hasPosition)
                {
                    if (loggedRoboticModuleKeys.Add(key))
                    {
                        ParsekLog.Verbose("Recorder", $"Robotics: unable to sample '{part.partInfo?.name}' pid={part.persistentId} " +
                            $"midx={moduleIndex} module={moduleName}; fields=[{DescribeModuleFields(module)}]");
                    }
                    continue;
                }

                if (loggedRoboticModuleKeys.Add(key))
                {
                    string movingSource = hasMovingSignal ? "module-flag" : "inferred";
                    ParsekLog.Verbose("Recorder", $"Robotics: tracking '{part.partInfo?.name}' pid={part.persistentId} " +
                        $"midx={moduleIndex} module={moduleName} source={sourceField} " +
                        $"deadband={deadband:F3} sampleHz=4.0 moving={movingSource}");
                }

                var events = CheckRoboticTransition(
                    key, part.persistentId, moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    movingSignal, positionValue, deadband, ut,
                    activeRoboticKeys, lastRoboticPosition, lastRoboticSampleUT, roboticSampleIntervalSeconds);

                for (int e = 0; e < events.Count; e++)
                {
                    PartEvents.Add(events[e]);
                    ParsekLog.Verbose("Recorder", $"Part event: {events[e].eventType} '{events[e].partName}' " +
                        $"pid={events[e].partPersistentId} midx={events[e].moduleIndex} " +
                        $"val={events[e].value:F3}");
                }
            }
        }

        #endregion

        #region Atmosphere Boundary Detection

        /// <summary>
        /// Pure testable function: determines whether an orbit segment should be skipped
        /// because the vessel is below the atmosphere. Keplerian orbits ignore drag, so
        /// an orbit segment recorded during atmospheric flight produces underground ghost paths.
        /// </summary>
        internal static bool ShouldSkipOrbitSegmentForAtmosphere(
            bool bodyHasAtmosphere, double altitude, double atmosphereDepth)
        {
            return bodyHasAtmosphere && altitude < atmosphereDepth;
        }

        /// <summary>
        /// Pure testable function: determines whether a recording should split at the atmosphere boundary.
        /// Requires both sustained time (hysteresisSeconds) AND distance beyond boundary (hysteresisMeters).
        /// </summary>
        internal static bool ShouldSplitAtAtmosphereBoundary(
            bool wasInAtmo, double altitude, double atmosphereDepth,
            bool pendingCross, double pendingUT, double currentUT,
            double hysteresisSeconds = 3.0, double hysteresisMeters = 1000.0)
        {
            if (atmosphereDepth <= 0) return false; // no atmosphere

            bool nowInAtmo = altitude < atmosphereDepth;
            if (nowInAtmo == wasInAtmo)
                return false; // no boundary crossed

            // Check distance beyond boundary
            double distBeyond = nowInAtmo
                ? (atmosphereDepth - altitude)
                : (altitude - atmosphereDepth);
            if (distBeyond < hysteresisMeters)
                return false; // not far enough past boundary

            // Check sustained time
            if (!pendingCross)
                return false; // first detection — caller should start the timer

            return (currentUT - pendingUT) >= hysteresisSeconds;
        }

        /// <summary>
        /// Returns the approach altitude threshold for an airless body.
        /// Prefers KSP's timeWarpAltitudeLimits[4] (100x warp limit) when available — this is
        /// KSP's own definition of "close enough that fast warp is dangerous" and adapts to modded
        /// planets automatically. Falls back to body.Radius * 0.15 clamped to [5000, 200000].
        /// WARNING: Callers must check body.atmosphere first — this method does not guard against
        /// atmospheric bodies (which use atmosphere boundary splits instead).
        /// </summary>
        internal static double ComputeApproachAltitude(CelestialBody body)
        {
            if (body != null && body.timeWarpAltitudeLimits != null
                && body.timeWarpAltitudeLimits.Length >= 5
                && body.timeWarpAltitudeLimits[4] > 0)
            {
                return body.timeWarpAltitudeLimits[4];
            }
            return ComputeApproachAltitude(body != null ? body.Radius : 0);
        }

        /// <summary>
        /// Radius-only fallback for tests and contexts without a CelestialBody reference.
        /// body.Radius * 0.15, clamped to [5000, 200000] meters.
        /// </summary>
        internal static double ComputeApproachAltitude(double bodyRadius)
        {
            double raw = bodyRadius * 0.15;
            if (raw < 5000.0) return 5000.0;
            if (raw > 200000.0) return 200000.0;
            return raw;
        }

        /// <summary>
        /// Pure testable function: determines whether a recording should split at the altitude boundary
        /// on an airless body. Mirrors ShouldSplitAtAtmosphereBoundary.
        /// Requires both sustained time (hysteresisSeconds) AND distance beyond boundary.
        /// If hysteresisMeters is negative, it is computed as max(1000, threshold * 0.02).
        /// </summary>
        internal static bool ShouldSplitAtAltitudeBoundary(
            double altitude, double threshold, bool wasAbove,
            bool pendingCross, double pendingUT, double currentUT,
            double hysteresisSeconds = 3.0, double hysteresisMeters = -1.0)
        {
            if (threshold <= 0) return false;

            if (hysteresisMeters < 0)
                hysteresisMeters = Math.Max(1000.0, threshold * 0.02);

            bool nowAbove = altitude >= threshold;
            if (nowAbove == wasAbove)
                return false; // no boundary crossed

            // Check distance beyond boundary
            double distBeyond = nowAbove
                ? (altitude - threshold)
                : (threshold - altitude);
            if (distBeyond < hysteresisMeters)
                return false; // not far enough past boundary

            // Check sustained time
            if (!pendingCross)
                return false; // first detection — caller should start the timer

            return (currentUT - pendingUT) >= hysteresisSeconds;
        }

        /// <summary>
        /// Called every physics frame to detect atmosphere boundary crossings.
        /// Sets AtmosphereBoundaryCrossed and EnteredAtmosphere when confirmed.
        /// </summary>
        public void CheckAtmosphereBoundary(Vessel v)
        {
            if (!IsRecording || isOnRails) return;
            if (v == null || v.mainBody == null || !v.mainBody.atmosphere) return;

            double altitude = v.altitude;
            double atmoDepth = v.mainBody.atmosphereDepth;
            bool nowInAtmo = altitude < atmoDepth;
            double currentUT = Planetarium.GetUniversalTime();

            if (nowInAtmo == wasInAtmosphere)
            {
                // No boundary — reset pending if we drifted back
                if (atmosphereBoundaryPending)
                    ParsekLog.Verbose("Recorder", $"Atmosphere boundary pending reset — drifted back to same side (inAtmo={nowInAtmo})");
                atmosphereBoundaryPending = false;
                return;
            }

            // Boundary detected — check hysteresis
            if (!atmosphereBoundaryPending)
            {
                // Start the timer
                atmosphereBoundaryPending = true;
                atmosphereBoundaryPendingUT = currentUT;
                ParsekLog.Verbose("Recorder", $"Atmosphere boundary detected — starting hysteresis timer " +
                    $"(body={v.mainBody.name}, {(nowInAtmo ? "entering" : "exiting")}, alt={altitude:F0}m, atmoDepth={atmoDepth:F0}m)");
                return;
            }

            if (ShouldSplitAtAtmosphereBoundary(
                wasInAtmosphere, altitude, atmoDepth,
                atmosphereBoundaryPending, atmosphereBoundaryPendingUT, currentUT))
            {
                // Confirmed crossing — force-sample a boundary point to ensure >= 2 points
                SamplePosition(v);

                AtmosphereBoundaryCrossed = true;
                EnteredAtmosphere = nowInAtmo;
                wasInAtmosphere = nowInAtmo;
                atmosphereBoundaryPending = false;
                ParsekLog.Info("Recorder", $"Atmosphere boundary confirmed: {(nowInAtmo ? "entered" : "exited")} " +
                    $"atmosphere of {v.mainBody.name} at alt {altitude:F0}m " +
                    $"(hysteresis: {(currentUT - atmosphereBoundaryPendingUT):F1}s, {Math.Abs(altitude - atmoDepth):F0}m past boundary)");
            }
        }

        /// <summary>
        /// Reseeds atmosphere state from current vessel. Call on SOI change and off-rails.
        /// </summary>
        private void ReseedAtmosphereState(Vessel v)
        {
            bool oldState = wasInAtmosphere;
            if (v == null || v.mainBody == null)
            {
                wasInAtmosphere = false;
            }
            else
            {
                wasInAtmosphere = v.mainBody.atmosphere && v.altitude < v.mainBody.atmosphereDepth;
            }
            atmosphereBoundaryPending = false;
            AtmosphereBoundaryCrossed = false;
            if (v?.mainBody != null)
            {
                ParsekLog.Verbose("Recorder", $"Atmosphere state reseeded: body={v.mainBody.name}, " +
                    $"inAtmo={wasInAtmosphere} (was {oldState}), " +
                    $"hasAtmo={v.mainBody.atmosphere}, alt={v.altitude:F0}m" +
                    (v.mainBody.atmosphere ? $", atmoDepth={v.mainBody.atmosphereDepth:F0}m" : ""));
            }
        }

        /// <summary>
        /// Called every physics frame to detect altitude boundary crossings on airless bodies.
        /// Sets AltitudeBoundaryCrossed and DescendedBelowThreshold when confirmed.
        /// Mirrors CheckAtmosphereBoundary.
        /// </summary>
        public void CheckAltitudeBoundary(Vessel v)
        {
            if (!IsRecording || isOnRails) return;
            if (v == null || v.mainBody == null) return;
            if (v.mainBody.atmosphere) return; // atmospheric bodies use atmosphere boundary split
            if (currentAltitudeThreshold <= 0) return; // no threshold computed (defensive)

            double altitude = v.altitude;
            double currentUT = Planetarium.GetUniversalTime();
            bool nowAbove = altitude >= currentAltitudeThreshold;

            if (nowAbove == wasAboveAltitudeThreshold)
            {
                // No boundary — reset pending if we drifted back
                if (altitudeBoundaryPending)
                    ParsekLog.Verbose("Recorder", $"Altitude boundary pending reset — drifted back to same side (above={nowAbove})");
                altitudeBoundaryPending = false;
                return;
            }

            // Boundary detected — check hysteresis
            if (!altitudeBoundaryPending)
            {
                // Start the timer
                altitudeBoundaryPending = true;
                altitudeBoundaryPendingUT = currentUT;
                ParsekLog.Verbose("Recorder", $"Altitude boundary detected — starting hysteresis timer " +
                    $"(body={v.mainBody.name}, {(nowAbove ? "ascending" : "descending")}, alt={altitude:F0}m, threshold={currentAltitudeThreshold:F0}m)");
                return;
            }

            if (ShouldSplitAtAltitudeBoundary(
                altitude, currentAltitudeThreshold, wasAboveAltitudeThreshold,
                altitudeBoundaryPending, altitudeBoundaryPendingUT, currentUT))
            {
                // Confirmed crossing — force-sample a boundary point to ensure >= 2 points
                SamplePosition(v);

                AltitudeBoundaryCrossed = true;
                DescendedBelowThreshold = !nowAbove;
                wasAboveAltitudeThreshold = nowAbove;
                altitudeBoundaryPending = false;
                ParsekLog.Info("Recorder", $"Altitude boundary confirmed: {(nowAbove ? "ascended above" : "descended below")} " +
                    $"threshold on {v.mainBody.name} at alt {altitude:F0}m " +
                    $"(threshold={currentAltitudeThreshold:F0}m, hysteresis: {(currentUT - altitudeBoundaryPendingUT):F1}s, " +
                    $"{Math.Abs(altitude - currentAltitudeThreshold):F0}m past boundary)");
            }
        }

        /// <summary>
        /// Reseeds altitude state from current vessel. Call on SOI change, off-rails, and recording start.
        /// Mirrors ReseedAtmosphereState.
        /// </summary>
        private void ReseedAltitudeState(Vessel v)
        {
            bool oldState = wasAboveAltitudeThreshold;
            if (v == null || v.mainBody == null || v.mainBody.atmosphere)
            {
                // Atmospheric body or null — altitude split not applicable
                wasAboveAltitudeThreshold = true;
                currentAltitudeThreshold = 0;
            }
            else
            {
                currentAltitudeThreshold = ComputeApproachAltitude(v.mainBody);
                wasAboveAltitudeThreshold = v.altitude >= currentAltitudeThreshold;
            }
            altitudeBoundaryPending = false;
            AltitudeBoundaryCrossed = false;
            DescendedBelowThreshold = false;
            if (v?.mainBody != null)
            {
                ParsekLog.Verbose("Recorder", $"Altitude state reseeded: body={v.mainBody.name}, " +
                    $"aboveThreshold={wasAboveAltitudeThreshold} (was {oldState}), " +
                    $"threshold={currentAltitudeThreshold:F0}m, alt={v.altitude:F0}m");
            }
        }

        #endregion

        /// <summary>
        /// Captures a quicksave for rewind at recording start. Saves via KSP API
        /// (which writes to root saves dir), then moves to Parsek/Saves/ subdirectory.
        /// </summary>
        /// <summary>
        /// Deletes any orphaned rewind save file from a previous aborted recording.
        /// Clears RewindSaveFileName if an orphan was found.
        /// </summary>
        private void CleanupOrphanedRewindSave()
        {
            if (string.IsNullOrEmpty(RewindSaveFileName))
                return;

            try
            {
                string oldPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildRewindSaveRelativePath(RewindSaveFileName));
                if (!string.IsNullOrEmpty(oldPath) && System.IO.File.Exists(oldPath))
                    System.IO.File.Delete(oldPath);
                ParsekLog.Info("Recorder",
                    $"Deleted orphaned rewind save: {RewindSaveFileName}");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Recorder",
                    $"Failed to delete orphaned rewind save: {ex.Message}");
            }
            RewindSaveFileName = null;
        }

        private void CaptureRewindSave(Vessel v, bool isPromotion)
        {
            if (isPromotion)
            {
                ParsekLog.Verbose("Recorder", "Rewind save skipped: tree branch/chain promotion");
                return;
            }

            // Clean up orphaned rewind save from a previous aborted recording
            CleanupOrphanedRewindSave();

            string shortId = Guid.NewGuid().ToString("N").Substring(0, 6);
            string saveFileName = $"parsek_rw_{shortId}";

            try
            {
                // Save to root saves dir (only KSP API path available)
                string result = GamePersistence.SaveGame(saveFileName, HighLogic.SaveFolder, SaveMode.OVERWRITE);
                if (string.IsNullOrEmpty(result))
                {
                    ParsekLog.Error("Recorder", "Failed to capture rewind save: SaveGame returned null");
                    return;
                }

                // Move from root to Parsek/Saves/
                string savesDir = System.IO.Path.Combine(
                    KSPUtil.ApplicationRootPath ?? "",
                    "saves",
                    HighLogic.SaveFolder ?? "");
                string rootPath = System.IO.Path.Combine(savesDir, saveFileName + ".sfs");

                string rewindDir = RecordingPaths.EnsureRewindSavesDirectory();
                if (string.IsNullOrEmpty(rewindDir))
                {
                    ParsekLog.Error("Recorder", "Failed to capture rewind save: cannot create Parsek/Saves/ directory");
                    try { System.IO.File.Delete(rootPath); } catch { }
                    return;
                }

                string destPath = System.IO.Path.Combine(rewindDir, saveFileName + ".sfs");
                System.IO.File.Move(rootPath, destPath);

                RewindSaveFileName = saveFileName;
            }
            catch (Exception ex)
            {
                ParsekLog.Error("Recorder", $"Failed to capture rewind save: {ex.Message}");
                return;
            }

            RewindReservedFunds = 0;
            RewindReservedScience = 0;
            RewindReservedRep = 0;

            ParsekLog.Info("Recorder",
                $"Captured rewind save at UT {Planetarium.GetUniversalTime()}: vessel \"{v.vesselName}\" in {v.situation} (save: {saveFileName})");
        }

        #region Environment Tracking

        /// <summary>
        /// Extracts vessel state into primitive parameters and calls EnvironmentDetector.Classify.
        /// This indirection keeps EnvironmentDetector pure-static and testable.
        /// </summary>
        /// <summary>
        /// Classifies vessel environment using the cached engine list to avoid
        /// per-frame FindModulesImplementing allocations on every part.
        /// </summary>
        private SegmentEnvironment ClassifyCurrentEnvironment(Vessel v)
        {
            bool hasAtmosphere = v.mainBody != null && v.mainBody.atmosphere;
            double atmosphereDepth = hasAtmosphere ? v.mainBody.atmosphereDepth : 0;
            bool hasActiveThrust = false;

            // Use cached engine list (built in CacheEngineModules at recording start)
            // instead of iterating all parts with FindModulesImplementing every frame
            if (cachedEngines != null)
            {
                for (int i = 0; i < cachedEngines.Count; i++)
                {
                    if (cachedEngines[i].engine.EngineIgnited && cachedEngines[i].engine.finalThrust > 0)
                    {
                        hasActiveThrust = true;
                        break;
                    }
                }
            }

            double approachAlt = (!hasAtmosphere && v.mainBody != null)
                ? currentAltitudeThreshold : 0;

            return EnvironmentDetector.Classify(
                hasAtmosphere, v.altitude, atmosphereDepth,
                (int)v.situation, v.srfSpeed, hasActiveThrust, approachAlt,
                v.isEVA, v.heightFromTerrain,
                EnvironmentDetector.IsHeightFromTerrainValid(v.heightFromTerrain),
                v.mainBody != null && v.mainBody.ocean);
        }

        /// <summary>
        /// Opens a new TrackSection with the given environment and reference frame.
        /// </summary>
        internal void StartNewTrackSection(SegmentEnvironment env, ReferenceFrame refFrame, double ut,
            TrackSectionSource source = TrackSectionSource.Active)
        {
            currentTrackSection = new TrackSection
            {
                environment = env,
                referenceFrame = refFrame,
                startUT = ut,
                source = source,
                frames = new List<TrajectoryPoint>(),
                absoluteFrames = refFrame == ReferenceFrame.Relative ? new List<TrajectoryPoint>() : null,
                checkpoints = new List<OrbitSegment>(),
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            };
            trackSectionActive = true;
            // Phase 7: reset SurfaceMobile clearance accumulators so the summary
            // log at the next CloseCurrentTrackSection reflects ONLY this section.
            surfaceMobileSamplesThisSection = 0;
            surfaceMobileMinClearanceThisSection = double.NaN;
            surfaceMobileMaxClearanceThisSection = double.NaN;
            surfaceMobileClearanceSumThisSection = 0.0;
            ParsekLog.Info("Recorder",
                $"TrackSection started: env={env} ref={refFrame} source={source} " +
                $"at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Updates min/max altitude on the current TrackSection.
        /// </summary>
        private void UpdateTrackSectionAltitude(float altitude)
        {
            if (float.IsNaN(currentTrackSection.minAltitude) || altitude < currentTrackSection.minAltitude)
                currentTrackSection.minAltitude = altitude;
            if (float.IsNaN(currentTrackSection.maxAltitude) || altitude > currentTrackSection.maxAltitude)
                currentTrackSection.maxAltitude = altitude;
        }

        /// <summary>
        /// Closes the current TrackSection, computes sample rate, and adds it to TrackSections list.
        /// </summary>
        internal void CloseCurrentTrackSection(double ut)
        {
            if (!trackSectionActive) return;
            currentTrackSection.endUT = ut;

            // Compute approximate sample rate
            if (currentTrackSection.frames != null && currentTrackSection.frames.Count > 1)
            {
                double duration = currentTrackSection.endUT - currentTrackSection.startUT;
                if (duration > 0)
                    currentTrackSection.sampleRateHz = (float)(currentTrackSection.frames.Count / duration);
            }

            int frameCount = currentTrackSection.frames?.Count ?? 0;
            int checkpointCount = currentTrackSection.checkpoints?.Count ?? 0;

            // Skip degenerate zero-frame sections from brief RELATIVE/environment flickers
            // (e.g., debris triggers anchor for one frame then leaves). Only discard if
            // the section was very short — longer empty sections may be intentional (tests, checkpoints).
            double sectionDuration = ut - currentTrackSection.startUT;
            if (frameCount == 0 && checkpointCount == 0 && sectionDuration < 1.0)
            {
                trackSectionActive = false;
                ParsekLog.Verbose("Recorder",
                    $"TrackSection discarded (zero frames, {sectionDuration.ToString("F3", CultureInfo.InvariantCulture)}s): " +
                    $"env={currentTrackSection.environment} ref={currentTrackSection.referenceFrame}");
                return;
            }

            TrackSections.Add(currentTrackSection);
            trackSectionActive = false;
            ParsekLog.Info("Recorder",
                $"TrackSection closed: env={currentTrackSection.environment} ref={currentTrackSection.referenceFrame} " +
                $"frames={frameCount} checkpoints={checkpointCount} " +
                $"duration={(ut - currentTrackSection.startUT).ToString("F2", CultureInfo.InvariantCulture)}s");

            // Phase 7 (design doc §13, §19.2 Pipeline-Terrain row): when a
            // SurfaceMobile section closes, summarise the clearance distribution
            // captured during its samples. One Verbose line per closed section —
            // bounded by the section count, no per-frame spam.
            if (currentTrackSection.environment == SegmentEnvironment.SurfaceMobile
                && surfaceMobileSamplesThisSection > 0)
            {
                var ic = CultureInfo.InvariantCulture;
                double avg = surfaceMobileClearanceSumThisSection / surfaceMobileSamplesThisSection;
                ParsekLog.Verbose("Pipeline-Terrain",
                    $"section close env=SurfaceMobile " +
                    $"clearanceMin={surfaceMobileMinClearanceThisSection.ToString("F3", ic)}m " +
                    $"clearanceMax={surfaceMobileMaxClearanceThisSection.ToString("F3", ic)}m " +
                    $"clearanceAvg={avg.ToString("F3", ic)}m " +
                    $"N={surfaceMobileSamplesThisSection}");
            }

            // P3-3: defensive accumulator reset. StartNewTrackSection already
            // resets these when a new section opens, but closing without
            // immediately opening (e.g. Stop Recording, scene exit) leaves
            // stale state that any future defensive-coding path emitting a
            // summary between sections would carry forward. Reset here so
            // the accumulators always reflect the in-progress section only.
            surfaceMobileSamplesThisSection = 0;
            surfaceMobileMinClearanceThisSection = double.NaN;
            surfaceMobileMaxClearanceThisSection = double.NaN;
            surfaceMobileClearanceSumThisSection = 0.0;
        }

        /// <summary>
        /// Adds a finalized orbit segment to the current TrackSection's checkpoints list,
        /// if the current section is ORBITAL_CHECKPOINT. Called whenever an orbit segment
        /// is committed to the flat OrbitSegments list.
        /// </summary>
        internal void AddOrbitSegmentToCurrentTrackSection(OrbitSegment segment)
        {
            if (!trackSectionActive) return;
            if (currentTrackSection.referenceFrame != ReferenceFrame.OrbitalCheckpoint) return;
            if (currentTrackSection.checkpoints == null) return;

            currentTrackSection.checkpoints.Add(segment);
            ParsekLog.Verbose("Recorder",
                $"Orbit segment added to TrackSection checkpoints: body={segment.bodyName} " +
                $"UT={segment.startUT.ToString("F2", CultureInfo.InvariantCulture)}-{segment.endUT.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Closes the recorder's current open TrackSection at the given UT and immediately
        /// opens a continuation section with the same environment/reference metadata.
        /// Used by OnSave serialization flushes so the active tree persists the live
        /// trajectory section that is still in progress, without stopping the recorder.
        /// </summary>
        internal void CheckpointOpenTrackSectionForSerialization(double ut)
        {
            if (!trackSectionActive) return;

            var currentEnv = currentTrackSection.environment;
            var currentRef = currentTrackSection.referenceFrame;
            var currentSource = currentTrackSection.source;
            uint currentAnchor = currentTrackSection.anchorVesselId;
            TrajectoryPoint? boundaryPoint = GetLastTrackSectionFrame();
            TrajectoryPoint? absoluteBoundaryPoint = GetLastTrackSectionAbsoluteFrame();

            CloseCurrentTrackSection(ut);
            StartNewTrackSection(currentEnv, currentRef, ut, currentSource);

            if (currentRef == ReferenceFrame.Relative)
                currentTrackSection.anchorVesselId = currentAnchor;

            // Only absolute/relative sections carry sparse frame payloads.
            if (currentRef != ReferenceFrame.OrbitalCheckpoint)
                SeedBoundaryPoint(boundaryPoint, absoluteBoundaryPoint);

            ParsekLog.Verbose("Recorder",
                $"Serialization checkpoint: env={currentEnv} ref={currentRef} " +
                $"source={currentSource} at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        #endregion

        #region Anchor detection helpers (Phase 3a)

        /// <summary>
        /// Builds the set of vessel PIDs belonging to the current recording tree.
        /// These are excluded from anchor candidates — we only anchor to pre-existing
        /// vessels, not to sibling vessels within the same mission tree.
        /// </summary>
        private HashSet<uint> BuildTreeVesselPids()
        {
            if (ActiveTree == null) return null;

            var pids = new HashSet<uint>();
            foreach (var rec in ActiveTree.Recordings.Values)
            {
                if (rec.VesselPersistentId != 0)
                    pids.Add(rec.VesselPersistentId);
            }
            // Also include background map PIDs (covers vessels tracked by PID before recording creation)
            foreach (var pid in ActiveTree.BackgroundMap.Keys)
            {
                pids.Add(pid);
            }
            // Include the focused vessel itself (FindNearestAnchor already skips it,
            // but this makes the set complete for diagnostics)
            if (RecordingVesselId != 0)
                pids.Add(RecordingVesselId);

            return pids;
        }

        /// <summary>
        /// Collects (pid, worldPosition) for all loaded vessels in the physics scene.
        /// Reuses vesselInfoBuffer to avoid per-frame allocation.
        /// Filters out vessel types that are not valid anchor candidates for RELATIVE mode:
        /// debris, EVA kerbals, space objects, and flags.
        /// </summary>
        private List<(uint pid, Vector3d position)> BuildVesselInfoList()
        {
            vesselInfoBuffer.Clear();
            if (FlightGlobals.Vessels == null) return vesselInfoBuffer;
            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                var vessel = FlightGlobals.Vessels[i];
                if (vessel == null || !vessel.loaded) continue;
                if (GhostMapPresence.IsGhostMapVessel(vessel.persistentId)) continue;

                // Filter out non-docking-target vessel types: debris from staging,
                // EVA kerbals, space objects, and flags are not valid anchor candidates.
                if (vessel.vesselType == VesselType.Debris ||
                    vessel.vesselType == VesselType.EVA ||
                    vessel.vesselType == VesselType.SpaceObject ||
                    vessel.vesselType == VesselType.Flag)
                {
                    continue;
                }

                // Exclude landed/splashed/prelaunch vessels as anchors.
                // These won't be loaded during ghost playback when the ghost is far from
                // the surface location (KSP only loads vessels within physics range).
                // RELATIVE mode is for orbital rendezvous/docking with in-flight vessels.
                if (vessel.situation == Vessel.Situations.LANDED ||
                    vessel.situation == Vessel.Situations.SPLASHED ||
                    vessel.situation == Vessel.Situations.PRELAUNCH)
                {
                    continue;
                }

                vesselInfoBuffer.Add((vessel.persistentId, (Vector3d)vessel.transform.position));
            }
            // Per-frame logging removed (was 0.4% of all log output)
            return vesselInfoBuffer;
        }

        /// <summary>
        /// Per-physics-frame anchor detection state machine for RELATIVE frame (Phase 3a).
        /// Detects proximity to pre-existing vessels, enters/exits RELATIVE mode, and
        /// transitions TrackSections accordingly. Extracted from OnPhysicsFrame.
        /// </summary>
        /// <summary>
        /// Resolves the "on the surface" classification used by anchor detection.
        /// Prefers the debounced environment hysteresis when available (which filters
        /// EVA situation jitter — see #246 for the precedent fix); falls back to the
        /// raw KSP situation enum when the hysteresis instance is null. Extracted as
        /// internal static so the wiring (null branch + CurrentEnvironment read) is
        /// directly testable in xUnit without needing a Vessel instance.
        /// </summary>
        internal static bool ResolveAnchorOnSurface(EnvironmentHysteresis hysteresis, int situation)
        {
            return EnvironmentDetector.IsSurfaceForAnchorDetection(
                envHint: hysteresis?.CurrentEnvironment,
                situation: situation);
        }

        private void UpdateAnchorDetection(Vessel v)
        {
            // Skip anchor detection while on the surface — RELATIVE mode is for orbital
            // docking approaches, not pad neighbors. Surface vessels are pinned to the
            // ground and don't need relative positioning. Also handles the case where a
            // vessel lands near a base — exits RELATIVE mode on landing.
            bool onSurface = ResolveAnchorOnSurface(environmentHysteresis, (int)v.situation);
            if (onSurface && isRelativeMode)
            {
                // Was flying in RELATIVE mode, just landed — exit RELATIVE
                // Sample boundary point BEFORE closing — adaptive sampler may have
                // skipped frames, so last recorded point could be far from here.
                SamplePosition(v);
                var ic = CultureInfo.InvariantCulture;
                var oldAnchor = currentAnchorPid;
                isRelativeMode = false;
                currentAnchorPid = 0;
                CloseCurrentTrackSection(Planetarium.GetUniversalTime());
                var env = environmentHysteresis != null
                    ? environmentHysteresis.CurrentEnvironment
                    : SegmentEnvironment.Atmospheric;
                StartNewTrackSection(env, ReferenceFrame.Absolute, Planetarium.GetUniversalTime());
                ParsekLog.Info("Anchor",
                    $"RELATIVE mode exited on landing: previousAnchorPid={oldAnchor} " +
                    $"situation={v.situation} vesselPid={RecordingVesselId}");
            }
            else if (!onSurface)
            {
                // Rebuild treeVesselPids on every anchor-detection call. The
                // initial cache from InitializeEnvironmentAndAnchorTracking is
                // built ONCE at recording start, before staging spawns new
                // chain members (probe, debris) — so a vessel that joins the
                // tree mid-flight stays absent from the cached set forever.
                // Concrete consequence (KSP.log 2026-04-26 a0d14b08 sections
                // at UT 438.73 / 442.21): the probe (PID 2450432355) joined
                // the tree at UT 140.79 but the upper stage's
                // treeVesselPids was frozen at recording start, so when the
                // upper stage came off rails at UT 438.71 the probe was
                // selected as anchor again (cache said "non-tree, eligible")
                // and the next 18 s were captured Relative against a vessel
                // whose live pose during Re-Fly playback no longer matches
                // the recorded one. The rebuild is O(tree members + bg map
                // entries) and runs once per anchor-detection tick — same
                // cost as BuildVesselInfoList itself, so amortised cost is
                // negligible compared to physics frame work.
                treeVesselPids = BuildTreeVesselPids();
                var vesselInfos = BuildVesselInfoList();
                var (anchorPid, anchorDist) = AnchorDetector.FindNearestAnchor(
                    RecordingVesselId, (Vector3d)v.transform.position, vesselInfos, treeVesselPids);

                bool shouldBeRelative = anchorPid != 0 &&
                    AnchorDetector.ShouldUseRelativeFrame(anchorDist, isRelativeMode);

                if (shouldBeRelative && !isRelativeMode)
                {
                    // Entering RELATIVE mode
                    // Sample boundary point BEFORE closing — adaptive sampler may have
                    // skipped frames, so last recorded point could be far from here.
                    SamplePosition(v);
                    var ic = CultureInfo.InvariantCulture;
                    isRelativeMode = true;
                    currentAnchorPid = anchorPid;
                    double boundaryUT = Planetarium.GetUniversalTime();
                    CloseCurrentTrackSection(boundaryUT);
                    var env = environmentHysteresis != null
                        ? environmentHysteresis.CurrentEnvironment
                        : SegmentEnvironment.Atmospheric;
                    StartNewTrackSection(env, ReferenceFrame.Relative, boundaryUT);
                    currentTrackSection.anchorVesselId = currentAnchorPid;
                    SeedRelativeBoundaryPoint(v, currentAnchorPid, boundaryUT);
                    ParsekLog.Info("Anchor",
                        $"RELATIVE mode entered: anchorPid={currentAnchorPid} " +
                        $"dist={anchorDist.ToString("F1", ic)}m " +
                        $"vesselPid={RecordingVesselId}");
                }
                else if (!shouldBeRelative && isRelativeMode)
                {
                    // Exiting RELATIVE mode
                    // Sample boundary point BEFORE closing — adaptive sampler may have
                    // skipped frames, so last recorded point could be far from here.
                    SamplePosition(v);
                    var ic = CultureInfo.InvariantCulture;
                    var oldAnchor = currentAnchorPid;
                    isRelativeMode = false;
                    currentAnchorPid = 0;
                    CloseCurrentTrackSection(Planetarium.GetUniversalTime());
                    var env = environmentHysteresis != null
                        ? environmentHysteresis.CurrentEnvironment
                        : SegmentEnvironment.Atmospheric;
                    StartNewTrackSection(env, ReferenceFrame.Absolute, Planetarium.GetUniversalTime());
                    ParsekLog.Info("Anchor",
                        $"RELATIVE mode exited: previousAnchorPid={oldAnchor} " +
                        $"dist={anchorDist.ToString("F1", ic)}m " +
                        $"vesselPid={RecordingVesselId}");
                }
            }
        }

        #endregion

        internal static bool ShouldShowStartRecordingScreenMessage(
            bool isPromotion,
            bool suppressStartScreenMessage)
        {
            return !isPromotion && !suppressStartScreenMessage;
        }

        // Pass suppressStartScreenMessage=true for fresh, non-continuation starts where
        // the caller posts its own custom screen message.
        public void StartRecording(bool isPromotion = false, bool suppressStartScreenMessage = false)
        {
            if (Time.timeScale < 0.01f)
            {
                ParsekLog.Warn("Recorder", "Cannot start recording while paused");
                ParsekLog.ScreenMessage("Cannot record while paused", 2f);
                return;
            }

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null)
            {
                ParsekLog.Warn("Recorder", "Cannot start recording: no active vessel");
                return;
            }

            Recording.Clear();
            OrbitSegments.Clear();
            PartEvents.Clear();
            FlagEvents.Clear();
            SegmentEvents.Clear();
            ResetPartEventTrackingState(v, emitSeedEvents: !isPromotion);
            PrepareQuickloadResumeStateIfNeeded();
            MaybeUpgradeActiveRecordingRelativeContract(
                isPromotion ? "resume-or-promotion" : "fresh-start");

            LogVisualRecordingCoverage(v);

            if (!isPromotion && !IsGloopsMode)
                CapturePreLaunchResources();

            // Capture rewind save (quicksave stored in Parsek/Saves/)
            // Gloops mode skips rewind saves — ghost-only recordings have no revert target
            if (!IsGloopsMode)
                CaptureRewindSave(v, isPromotion);

            InitializeRecordingFlags(v);
            CaptureStartLocation(v, isPromotion);
            var initialEnv = InitializeEnvironmentAndAnchorTracking(v);
            InsertBoundaryAnchorAndSnapshot(v);

            // Check if vessel is already on rails (e.g. started recording during time warp)
            if (v.packed)
            {
                // Take one boundary point first
                SamplePosition(v);

                // Skip orbit segment if below atmosphere — Keplerian orbit ignores drag
                if (ShouldSkipOrbitSegmentForAtmosphere(v.mainBody.atmosphere, v.altitude, v.mainBody.atmosphereDepth))
                {
                    ParsekLog.Info("Recorder",
                        $"Recording started on rails in atmosphere — skipping orbit segment " +
                        $"(alt={v.altitude:F0}, atmoDepth={v.mainBody.atmosphereDepth:F0})");
                }
                else
                {
                    InitializeOnRailsOrbitSegment(v, initialEnv);
                }
            }

            // Register the Harmony patch to call us each physics frame
            if (IsGloopsMode)
                Patches.PhysicsFramePatch.GloopsRecorderInstance = this;
            else
                Patches.PhysicsFramePatch.ActiveRecorder = this;

            SubscribePartEvents();

            // Initialize diagnostics growth rate tracking
            DiagnosticsState.activeGrowthRate = default;
            DiagnosticsState.hasActiveGrowthRate = true;
            BootstrapAvgBytesPerPoint();

            int partCount = v.parts != null ? v.parts.Count : 0;
            string treeRecDbg = "-";
            if (ActiveTree != null && !string.IsNullOrEmpty(ActiveTree.ActiveRecordingId)
                && ActiveTree.Recordings != null
                && ActiveTree.Recordings.TryGetValue(ActiveTree.ActiveRecordingId, out var activeTreeRec)
                && activeTreeRec != null)
            {
                treeRecDbg = activeTreeRec.DebugName;
            }
            ParsekLog.Info("Recorder",
                string.Format(CultureInfo.InvariantCulture,
                    "Recording started: vessel=\"{0}\", parts={1}, points=0{2}, treeRec={3}",
                    v.vesselName, partCount, isPromotion ? ", promotion" : "", treeRecDbg));
            if (ShouldShowStartRecordingScreenMessage(isPromotion, suppressStartScreenMessage))
                ParsekLog.ScreenMessage("Recording STARTED", 2f);
        }

        /// <summary>
        /// Captures the pre-launch resource snapshot (funds, science, reputation) for delta
        /// tracking. Skipped for promotion — resources belong to the tree root.
        /// </summary>
        private void CapturePreLaunchResources()
        {
            PreLaunchFunds = 0;
            PreLaunchScience = 0;
            PreLaunchReputation = 0;
            try
            {
                if (Funding.Instance != null)
                    PreLaunchFunds = Funding.Instance.Funds;
                if (ResearchAndDevelopment.Instance != null)
                    PreLaunchScience = ResearchAndDevelopment.Instance.Science;
                if (Reputation.Instance != null)
                    PreLaunchReputation = Reputation.Instance.reputation;
            }
            catch (Exception ex) { ParsekLog.Verbose("Recorder", $"Pre-launch resource capture failed: {ex.Message}"); }
            ParsekLog.Verbose("Recorder",
                $"Pre-launch resources captured: funds={PreLaunchFunds:F0}, science={PreLaunchScience:F1}, rep={PreLaunchReputation:F1}");
        }

        /// <summary>
        /// Captures start location context (Phase 10): body, biome, situation, and launch site.
        /// </summary>
        private void CaptureStartLocation(Vessel v, bool isPromotion)
        {
            StartBodyName = v.mainBody?.name;
            StartSituation = v.isEVA ? "EVA" : VesselSpawner.HumanizeSituation(v.situation);
            StartBiome = VesselSpawner.TryResolveBiome(v.mainBody?.name, v.latitude, v.longitude);
            // Chain continuations (BoundaryAnchor set) inherit a stale FlightDriver value — skip
            bool isChainContinuation = BoundaryAnchor.HasValue;
            LaunchSiteName = ResolveLaunchSiteName(v, isPromotion || isChainContinuation);
            ParsekLog.Verbose("Recorder",
                $"Start location captured: body={StartBodyName ?? "(null)"}, biome={StartBiome ?? "(null)"}, " +
                $"situation={StartSituation ?? "(null)"}, launchSite={LaunchSiteName ?? "(null)"}");
        }

        /// <summary>
        /// Resolves the launch site name for the current vessel.
        /// Uses FlightDriver.LaunchSiteName for stock sites (LaunchPad, Runway, Desert Airfield,
        /// Woomerang Launch Site, Island Airfield). The value persists in FlightDriver after
        /// the vessel transitions from PRELAUNCH to FLYING, so we capture it regardless of
        /// current situation. Returns null when the launch site is unknown.
        /// </summary>
        internal static string ResolveLaunchSiteName(Vessel v, bool isPromotion)
        {
            if (v == null) return null;

            // Only capture launch site for the initial launch — not for EVAs,
            // chain continuations, or promoted background recordings.
            // FlightDriver.LaunchSiteName persists from the original launch
            // and would incorrectly tag all subsequent recordings.
            if (v.isEVA || isPromotion) return null;

            try
            {
                string site = FlightDriver.LaunchSiteName;
                if (string.IsNullOrEmpty(site))
                    return null;
                return HumanizeLaunchSiteName(site);
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose("Recorder", $"ResolveLaunchSiteName failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Humanizes stock launch site names. KSP internal names are "LaunchPad", "Runway".
        /// Making History DLC names are already human-readable.
        /// </summary>
        internal static string HumanizeLaunchSiteName(string siteName)
        {
            if (string.IsNullOrEmpty(siteName)) return null;
            switch (siteName)
            {
                case "LaunchPad": return "Launch Pad";
                case "Runway":    return "Runway";
                default:
                    // MH DLC uses underscores internally (e.g., "Woomerang_Launch_Site")
                    return siteName.Replace('_', ' ');
            }
        }

        /// <summary>
        /// Resets all recording state flags, vessel identity, velocity tracking, mod detection,
        /// and atmosphere/altitude boundary state for the given vessel.
        /// </summary>
        private void InitializeRecordingFlags(Vessel v)
        {
            IsRecording = true;
            isOnRails = false;
            VesselDestroyedDuringRecording = false;
            CaptureAtStop = null;
            TransitionToBackgroundPending = false;
            RecordingVesselId = v.persistentId;
            RecordingStartedAsEva = v.isEVA;
            lastRecordedUT = -1;
            lastRecordedVelocity = Vector3.zero;
            lastRecordedWorldRotation = Quaternion.identity;
            hasLastRecordedWorldRotation = false;
            LastRecordedAltitude = double.NaN;
            FinalizationCache = null;
            lastFinalizationCacheRefreshUT = double.MinValue;

            hasPersistentRotation = AssemblyLoader.loadedAssemblies.Any(
                a => a.name == "PersistentRotation");
            ParsekLog.Info("Recorder", $"PersistentRotation mod detected: {hasPersistentRotation}");

            // Seed atmosphere state
            wasInAtmosphere = v.mainBody != null && v.mainBody.atmosphere
                && v.altitude < v.mainBody.atmosphereDepth;
            atmosphereBoundaryPending = false;
            AtmosphereBoundaryCrossed = false;
            EnteredAtmosphere = false;
            SoiChangePending = false;
            SoiChangeFromBody = null;

            // Seed altitude boundary state (airless bodies)
            ReseedAltitudeState(v);

            ParsekLog.Verbose("Recorder", $"Boundary detection initialized: body={v.mainBody?.name}, " +
                $"inAtmo={wasInAtmosphere}, hasAtmo={v.mainBody?.atmosphere}, alt={v.altitude:F0}m" +
                $", altThreshold={currentAltitudeThreshold:F0}m, aboveThreshold={wasAboveAltitudeThreshold}");
        }

        /// <summary>
        /// Initializes environment hysteresis, TrackSections, anchor detection for RELATIVE
        /// frame, and logs the surface-relative rotation. Returns the initial environment
        /// classification (needed by the on-rails orbit segment path).
        /// </summary>
        private SegmentEnvironment InitializeEnvironmentAndAnchorTracking(Vessel v)
        {
            // Initialize environment tracking
            TrackSections.Clear();
            var initialEnv = ClassifyCurrentEnvironment(v);
            environmentHysteresis = new EnvironmentHysteresis(initialEnv);
            StartNewTrackSection(initialEnv, ReferenceFrame.Absolute, Planetarium.GetUniversalTime());

            // Initialize anchor detection for RELATIVE frame (Phase 3a)
            isRelativeMode = false;
            currentAnchorPid = 0;
            treeVesselPids = BuildTreeVesselPids();
            ParsekLog.Info("Anchor",
                $"Anchor detection initialized: treeVesselPids={treeVesselPids?.Count ?? 0} " +
                $"hasTree={ActiveTree != null}");

            // Log surface-relative rotation for synthetic recording calibration
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            ParsekLog.Info("Recorder",
                $"srfRelRotation=({v.srfRelRotation.x.ToString("R", ic)},{v.srfRelRotation.y.ToString("R", ic)}," +
                $"{v.srfRelRotation.z.ToString("R", ic)},{v.srfRelRotation.w.ToString("R", ic)}) " +
                $"UT={Planetarium.GetUniversalTime().ToString("R", ic)}");

            return initialEnv;
        }

        /// <summary>
        /// Inserts the boundary anchor point from the previous chain segment (if present),
        /// captures the initial backup snapshot, and stores the ghost visual snapshot.
        /// Must be called AFTER InitializeRecordingFlags (depends on lastRecordedUT reset).
        /// </summary>
        private void InsertBoundaryAnchorAndSnapshot(Vessel v)
        {
            // Insert boundary anchor from previous chain segment if present.
            // Must come AFTER the lastRecordedUT reset above so the anchor's
            // UT is preserved (prevents duplicate-UT with the first live sample).
            if (BoundaryAnchor.HasValue)
            {
                var anchor = BoundaryAnchor.Value;
                // Nudge UT backward by a tiny epsilon to maintain strict monotonicity
                // if the anchor UT would equal the first live sample's UT
                double anchorUT = anchor.ut - 0.001;
                anchor.ut = anchorUT;
                Recording.Add(anchor);
                lastRecordedUT = anchorUT;
                lastRecordedVelocity = anchor.velocity;
                // Mirror lastRecordedVelocity: the anchor is a full TrajectoryPoint with
                // an altitude field, and HandleSoiAutoSplit's "approach vs exo" decision
                // reads LastRecordedAltitude. Without this, a chain continuation that
                // hits an SOI split before the first live sample would misclassify the
                // fromPhase as "exo" (because the cached altitude is still NaN).
                LastRecordedAltitude = anchor.altitude;
                BoundaryAnchor = null;
                ParsekLog.Verbose("Recorder", $"Boundary anchor inserted at UT {anchorUT:F3}");
            }
            RefreshBackupSnapshot(v, "record_start", force: true);
            RefreshFinalizationCache(v, "record_start", force: true);
            pendingStartResources = VesselSpawner.ExtractResourceManifest(lastGoodVesselSnapshot);
            ParsekLog.Verbose("Recorder", $"StartRecording: captured {pendingStartResources?.Count ?? 0} start resource type(s)");
            pendingStartInventory = VesselSpawner.ExtractInventoryManifest(lastGoodVesselSnapshot, out pendingStartInventorySlots);
            ParsekLog.Verbose("Recorder", $"StartRecording: captured {pendingStartInventory?.Count ?? 0} start inventory item type(s), {pendingStartInventorySlots} slot(s)");
            pendingStartCrew = VesselSpawner.ExtractCrewManifest(lastGoodVesselSnapshot);
            ParsekLog.Verbose("Recorder", $"StartRecording: captured {pendingStartCrew?.Count ?? 0} start crew trait(s)");
            initialGhostVisualSnapshot = lastGoodVesselSnapshot != null
                ? lastGoodVesselSnapshot.CreateCopy()
                : VesselSpawner.TryBackupSnapshot(v);
        }

        /// <summary>
        /// Resets all part event tracking state and rebuilds module caches for the given vessel.
        /// Extracted from StartRecording for reuse by the promotion path.
        /// </summary>
        /// <summary>
        /// Clears part-event tracking state, re-caches modules, seeds current state,
        /// and optionally emits seed events for the initial visual baseline.
        /// </summary>
        /// <param name="emitSeedEvents">True on new recording starts so ghost playback
        /// has an initial visual baseline (bugs #70/#65). False on chain continuation
        /// promotions — the prior chain segment already has the seed events, and emitting
        /// new ones at the promotion UT poisons FindLastInterestingUT (bug A / #263 sibling).</param>
        private void ResetPartEventTrackingState(Vessel v, bool emitSeedEvents = true)
        {
            decoupledPartIds.Clear();
            parachuteStates.Clear();
            jettisonedShrouds.Clear();
            jettisonNameRawCache.Clear();
            parsedJettisonNamesCache.Clear();
            extendedDeployables.Clear();
            lightsOn.Clear();
            blinkingLights.Clear();
            lightBlinkRates.Clear();
            deployedGear.Clear();
            openCargoBays.Clear();
            deployedFairings.Clear();
            deployedLadders.Clear();
            deployedAnimationGroups.Clear();
            deployedAnimateGenericModules.Clear();
            deployedAeroSurfaceModules.Clear();
            deployedControlSurfaceModules.Clear();
            deployedRobotArmScannerModules.Clear();
            animateHeatLevels.Clear();
            loggedLadderClassificationMisses.Clear();
            loggedAnimationGroupClassificationMisses.Clear();
            loggedAnimateGenericClassificationMisses.Clear();
            loggedAeroSurfaceClassificationMisses.Clear();
            loggedControlSurfaceClassificationMisses.Clear();
            loggedRobotArmScannerClassificationMisses.Clear();
            loggedAnimateHeatClassificationMisses.Clear();
            loggedCargoBayDeployIndexIssues.Clear();
            loggedCargoBayAnimationIssues.Clear();
            loggedCargoBayClosedPositionIssues.Clear();
            loggedFairingReadFailures.Clear();
            cachedEngines = CacheEngineModules(v);
            activeEngineKeys = new HashSet<ulong>();
            allEngineKeys = new HashSet<ulong>();
            lastThrottle = new Dictionary<ulong, float>();
            loggedEngineModuleKeys.Clear();
            cachedRcsModules = CacheRcsModules(v);
            activeRcsKeys = new HashSet<ulong>();
            lastRcsThrottle = new Dictionary<ulong, float>();
            rcsActiveFrameCount.Clear();
            loggedRcsModuleKeys.Clear();
            cachedRoboticModules = CacheRoboticModules(v);
            activeRoboticKeys = new HashSet<ulong>();
            lastRoboticPosition = new Dictionary<ulong, float>();
            lastRoboticSampleUT = new Dictionary<ulong, double>();
            loggedRoboticModuleKeys = new HashSet<ulong>();
            ParsekLog.Info("Recorder",
                $"Module caches seeded for vessel pid={v.persistentId}: engines={cachedEngines?.Count ?? 0}, " +
                $"rcs={cachedRcsModules?.Count ?? 0}, robotics={cachedRoboticModules?.Count ?? 0}");

            // Seed all tracking sets with current part state so we don't emit
            // false events at first poll (e.g., shrouds already jettisoned,
            // engines already running, lights already on, etc.)
            SeedExistingPartStates(v);

            if (!emitSeedEvents)
            {
                ParsekLog.Verbose("Recorder",
                    "ResetPartEventTrackingState: skipping seed events (chain promotion)");
                return;
            }

            // Emit seed events for the initial visual state so ghost playback
            // can reconstruct correct visuals from recording start (bugs #70/#65).
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
            var seedSets = BuildCurrentTrackingSets();
            double seedUT = Planetarium.GetUniversalTime();
            var seedEvents = PartStateSeeder.EmitSeedEvents(seedSets, partNamesByPid, seedUT, "Recorder");
            PartEvents.AddRange(seedEvents);
        }

        /// <summary>
        /// Builds the PartTrackingSets struct from current instance tracking state.
        /// Extracted to deduplicate the identical struct literal in ResetPartEventTrackingState
        /// and SeedExistingPartStates.
        /// </summary>
        private PartTrackingSets BuildCurrentTrackingSets()
        {
            return new PartTrackingSets
            {
                deployedFairings = deployedFairings,
                jettisonedShrouds = jettisonedShrouds,
                parachuteStates = parachuteStates,
                extendedDeployables = extendedDeployables,
                lightsOn = lightsOn,
                blinkingLights = blinkingLights,
                lightBlinkRates = lightBlinkRates,
                deployedGear = deployedGear,
                openCargoBays = openCargoBays,
                deployedLadders = deployedLadders,
                deployedAnimationGroups = deployedAnimationGroups,
                deployedAnimateGenericModules = deployedAnimateGenericModules,
                deployedAeroSurfaceModules = deployedAeroSurfaceModules,
                deployedControlSurfaceModules = deployedControlSurfaceModules,
                deployedRobotArmScannerModules = deployedRobotArmScannerModules,
                animateHeatLevels = animateHeatLevels,
                activeEngineKeys = activeEngineKeys,
                allEngineKeys = allEngineKeys,
                lastThrottle = lastThrottle,
                activeRcsKeys = activeRcsKeys,
                lastRcsThrottle = lastRcsThrottle,
            };
        }

        /// <summary>
        /// Pre-populates all part-event tracking sets with the current state of
        /// every part on the vessel. This prevents spurious events on the first
        /// physics-frame poll when recording starts mid-flight (e.g., chain
        /// continuation after staging, or background recorder initialization).
        /// </summary>
        internal void SeedExistingPartStates(Vessel v)
        {
            PartStateSeeder.SeedPartStates(v,
                BuildCurrentTrackingSets(),
                cachedEngines, cachedRcsModules,
                seedColorChangerLights: true, logTag: "Recorder");
        }

        /// <summary>
        /// Builds the CaptureAtStop recording object from current state.
        /// Extracted from StopRecording/StopRecordingForChainBoundary/OnPhysicsFrame to eliminate
        /// the triple duplication of the CaptureAtStop construction block.
        /// </summary>
        private Recording BuildCaptureRecording(
            string vesselName,
            bool isDestroyed,
            Vessel snapshotVessel = null,
            ConfigNode destroyedFallbackSnapshot = null)
        {
            var capture = new Recording
            {
                RecordingId = System.Guid.NewGuid().ToString("N"),
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,

                VesselName = Parsek.Recording.ResolveLocalizedName(vesselName),
                Points = new List<TrajectoryPoint>(Recording),
                OrbitSegments = new List<OrbitSegment>(OrbitSegments),
                PartEvents = new List<PartEvent>(PartEvents),
                FlagEvents = new List<FlagEvent>(FlagEvents),
                SegmentEvents = new List<SegmentEvent>(SegmentEvents),
                PreLaunchFunds = PreLaunchFunds,
                PreLaunchScience = PreLaunchScience,
                PreLaunchReputation = PreLaunchReputation,
                RewindSaveFileName = RewindSaveFileName,
                RewindReservedFunds = RewindReservedFunds,
                RewindReservedScience = RewindReservedScience,
                RewindReservedRep = RewindReservedRep,
                TrackSections = Parsek.Recording.DeepCopyTrackSections(TrackSections),
                StartBodyName = StartBodyName,
                StartBiome = StartBiome,
                StartSituation = StartSituation,
                LaunchSiteName = LaunchSiteName
            };
            // Clear after first capture — prevents chain children from inheriting root's rewind save
            RewindSaveFileName = null;

            VesselSpawner.SnapshotVessel(
                capture,
                isDestroyed,
                snapshotVessel,
                destroyedFallbackSnapshot ?? lastGoodVesselSnapshot);
            GetFinalizationCacheForRecording(capture);
            capture.StartResources = pendingStartResources;
            capture.EndResources = VesselSpawner.ExtractResourceManifest(capture.VesselSnapshot);
            ParsekLog.Verbose("Recorder", $"BuildCaptureRecording: captured {capture.EndResources?.Count ?? 0} end resource type(s)");
            capture.StartInventory = pendingStartInventory;
            capture.StartInventorySlots = pendingStartInventorySlots;
            int endInvSlots;
            capture.EndInventory = VesselSpawner.ExtractInventoryManifest(capture.VesselSnapshot, out endInvSlots);
            capture.EndInventorySlots = endInvSlots;
            ParsekLog.Verbose("Recorder", $"BuildCaptureRecording: captured {capture.EndInventory?.Count ?? 0} end inventory item type(s), {endInvSlots} slot(s)");
            capture.StartCrew = pendingStartCrew;
            capture.EndCrew = VesselSpawner.ExtractCrewManifest(capture.VesselSnapshot);
            ParsekLog.Verbose("Recorder", $"BuildCaptureRecording: captured {capture.EndCrew?.Count ?? 0} end crew trait(s)");
            capture.GhostVisualSnapshot = initialGhostVisualSnapshot != null
                ? initialGhostVisualSnapshot.CreateCopy()
                : (capture.VesselSnapshot != null ? capture.VesselSnapshot.CreateCopy() : null);

            int densifiedCheckpointPoints = OrbitalCheckpointDensifier.DensifyRecording(capture);

            ParsekLog.Verbose("Recorder",
                $"Built capture recording: vessel=\"{vesselName}\", points={capture.Points.Count}, " +
                $"orbits={capture.OrbitSegments.Count}, partEvents={capture.PartEvents.Count}, " +
                $"checkpointDensifiedPoints={densifiedCheckpointPoints}, " +
                $"hasSnapshot={capture.VesselSnapshot != null}");

            return capture;
        }

        /// <summary>
        /// Shared finalization logic for StopRecording, StopRecordingForChainBoundary, and ForceStop.
        /// Finalizes orbit segments, closes TrackSection, disconnects Harmony, and optionally
        /// emits terminal events and clears RELATIVE mode state.
        /// </summary>
        /// <param name="emitTerminalEvents">True to emit terminal engine/RCS/robotic shutdown events.</param>
        /// <param name="clearRelativeMode">True to clear RELATIVE mode state (ForceStop passes false
        /// to preserve current behavior — it did not have RELATIVE cleanup before extraction).</param>
        /// <param name="sampleBoundaryOnRails">True to sample a boundary point if on rails at stop time.</param>
        /// <param name="logTag">Tag for the RELATIVE mode cleared log message.</param>
        private void FinalizeRecordingState(bool emitTerminalEvents, bool clearRelativeMode,
            bool sampleBoundaryOnRails, string logTag)
        {
            // Finalize in-progress orbit segment
            if (isOnRails)
            {
                currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
                OrbitSegments.Add(currentOrbitSegment);
                AddOrbitSegmentToCurrentTrackSection(currentOrbitSegment);
                isOnRails = false;

                if (sampleBoundaryOnRails)
                {
                    Vessel v = FlightGlobals.ActiveVessel;
                    if (v != null) SamplePosition(v);
                }
            }

            // Close final TrackSection
            CloseCurrentTrackSection(Planetarium.GetUniversalTime());

            // Clear RELATIVE mode state
            if (clearRelativeMode)
            {
                if (isRelativeMode)
                {
                    ParsekLog.Info("Anchor",
                        $"RELATIVE mode cleared on {logTag}: anchorPid={currentAnchorPid}");
                }
                isRelativeMode = false;
                currentAnchorPid = 0;
            }

            // Disconnect from Harmony patch
            if (IsGloopsMode)
                Patches.PhysicsFramePatch.GloopsRecorderInstance = null;
            else
                Patches.PhysicsFramePatch.ActiveRecorder = null;
            UnsubscribePartEvents();
            IsRecording = false;

            // Finalize diagnostics growth rate tracking
            if (DiagnosticsState.hasActiveGrowthRate)
            {
                var finalGr = DiagnosticsState.activeGrowthRate;
                ParsekLog.Verbose("Diagnostics",
                    string.Format(CultureInfo.InvariantCulture,
                        "Recording growth rate at stop: {0} points, {1} events, {2:F1}s elapsed, " +
                        "{3:F2} pts/s, {4:F2} evts/s, est {5} bytes",
                        finalGr.totalPoints, finalGr.totalEvents, finalGr.elapsedSeconds,
                        finalGr.pointsPerSecond, finalGr.eventsPerSecond, finalGr.estimatedFinalBytes));
                DiagnosticsState.hasActiveGrowthRate = false;
            }

            // Optionally emit terminal shutdown events for engines/RCS/robotics still active (bug #108)
            if (emitTerminalEvents)
            {
                double terminalUT = Planetarium.GetUniversalTime();
                var terminalEvts = EmitTerminalEngineAndRcsEvents(
                    activeEngineKeys, activeRcsKeys, activeRoboticKeys,
                    lastRoboticPosition, terminalUT, "Recorder");
                PartEvents.AddRange(terminalEvts);
                // Save the exact terminal events so ResumeAfterFalseAlarm can remove them
                // by content-match (#287). Index-based RemoveRange is brittle across unstable
                // sort paths and any code path that appends events between stop and resume.
                lastEmittedTerminalEvents = terminalEvts;
            }
            else
            {
                lastEmittedTerminalEvents = null;
            }

            // Sort part/flag events chronologically with STABLE semantics so same-UT
            // events retain their insertion order. See #263/#287 for the root-cause
            // narrative — LINQ OrderBy is stable; List<T>.Sort(Comparison) is not.
            StableSortPartEventsByUT();

            // FlagEvents also uses stable sort for consistency — bug #287 established
            // that every flush/merge site should use stable semantics so future code
            // that adds a rollback path or high-frequency flag emission cannot silently
            // regress on same-UT ordering.
            var sortedFlags = StableSortByUT(FlagEvents, e => e.ut);
            FlagEvents.Clear();
            FlagEvents.AddRange(sortedFlags);
        }

        /// <summary>
        /// Sorts <see cref="PartEvents"/> by UT with stable semantics (equal-UT events
        /// keep their insertion order). Uses LINQ <c>OrderBy</c> which is documented as
        /// stable — unlike <see cref="List{T}.Sort(System.Comparison{T})"/> which uses
        /// introspective sort and may reorder equal elements. See bug #263.
        /// </summary>
        internal void StableSortPartEventsByUT()
        {
            // The static helper handles 0/1-element lists by returning a new empty/copy
            // list — Clear+AddRange stays safe for those cases, no need for a short-circuit.
            var sorted = StableSortPartEventsByUT(PartEvents);
            PartEvents.Clear();
            PartEvents.AddRange(sorted);
        }

        /// <summary>
        /// Static helper for stable UT-sorting of a <see cref="PartEvent"/> list. Returns a
        /// new list with equal-UT events in their original insertion order. Call sites that
        /// merge multiple event sources (tree flushes, session merger, recording optimizer)
        /// must use this instead of <c>List&lt;T&gt;.Sort(Comparison)</c> to preserve the
        /// terminal-vs-ignited ordering at chain boundaries (#263, #287).
        /// Always returns a NEW list (even for null/small inputs) so callers can safely
        /// Clear the source list before copying back, without aliasing issues.
        /// </summary>
        internal static List<PartEvent> StableSortPartEventsByUT(List<PartEvent> events)
        {
            return StableSortByUT(events, e => e.ut);
        }

        /// <summary>
        /// Generic stable UT-sort helper for any event type with a UT double. Returns a NEW
        /// list (never the same reference as input) so callers can safely Clear+AddRange.
        /// Uses LINQ <c>OrderBy</c> which is documented stable — same-UT events keep their
        /// insertion order. Used for <see cref="FlagEvent"/> and <see cref="SegmentEvent"/>
        /// sorts at flush/merge sites so the sort semantics stay consistent with the
        /// <see cref="PartEvent"/> path and future same-UT hazards cannot silently regress.
        /// </summary>
        internal static List<T> StableSortByUT<T>(List<T> list, System.Func<T, double> utSelector)
        {
            if (list == null) return new List<T>();
            if (list.Count < 2) return new List<T>(list);
            return list.OrderBy(utSelector).ToList();
        }

        /// <summary>
        /// Removes the exact terminal events saved by the most recent FinalizeRecordingState
        /// call from <see cref="PartEvents"/> by content-matching on (ut, pid, eventType,
        /// moduleIndex). Robust against sort reordering — unlike the index-based RemoveRange
        /// it replaces (#287). Returns the number of terminal events removed.
        /// </summary>
        internal int RemoveLastEmittedTerminals()
        {
            if (lastEmittedTerminalEvents == null || lastEmittedTerminalEvents.Count == 0)
                return 0;

            int removed = 0;
            int notFound = 0;
            for (int i = 0; i < lastEmittedTerminalEvents.Count; i++)
            {
                var target = lastEmittedTerminalEvents[i];
                int idx = FindTerminalEventIndex(PartEvents, target);
                if (idx >= 0)
                {
                    PartEvents.RemoveAt(idx);
                    removed++;
                }
                else
                {
                    notFound++;
                }
            }

            if (notFound > 0)
            {
                ParsekLog.Warn("Recorder",
                    $"RemoveLastEmittedTerminals: {notFound} terminal event(s) not found in PartEvents " +
                    $"(expected {lastEmittedTerminalEvents.Count}, removed {removed}) — possible race " +
                    $"condition with intervening flush/sort");
            }

            lastEmittedTerminalEvents = null;
            return removed;
        }

        /// <summary>
        /// Removes terminal events from an arbitrary PartEvent list using the same
        /// content-matching logic as RemoveLastEmittedTerminals. Used by callers that
        /// need to clean terminals from a COPY of the recorder's events (e.g.,
        /// CaptureAtStop.PartEvents) rather than the recorder's own live list.
        /// Bug #299 — CaptureAtStop may bake terminals before the caller can call
        /// RemoveLastEmittedTerminals on the recorder's internal list.
        /// </summary>
        internal static int RemoveTerminalsFromList(List<PartEvent> targetList, List<PartEvent> terminals)
        {
            if (targetList == null || terminals == null || terminals.Count == 0)
                return 0;

            int removed = 0;
            for (int i = 0; i < terminals.Count; i++)
            {
                int idx = FindTerminalEventIndex(targetList, terminals[i]);
                if (idx >= 0)
                {
                    targetList.RemoveAt(idx);
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>
        /// Pure helper: finds the index of the first <see cref="PartEvent"/> in
        /// <paramref name="list"/> that matches <paramref name="target"/> by
        /// (ut, partPersistentId, eventType, moduleIndex). Returns -1 if not found.
        /// Ignores <c>partName</c> and <c>value</c> because terminal events stamp
        /// <c>partName="unknown"</c> while a natural event with the same (ut, pid, type,
        /// midx) tuple would not realistically coexist in the list (the active-key set
        /// gates both emission sites).
        /// </summary>
        internal static int FindTerminalEventIndex(List<PartEvent> list, PartEvent target)
        {
            if (list == null) return -1;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e.ut == target.ut
                    && e.partPersistentId == target.partPersistentId
                    && e.eventType == target.eventType
                    && e.moduleIndex == target.moduleIndex)
                {
                    return i;
                }
            }
            return -1;
        }

        public void StopRecording()
        {
            FinalizeRecordingState(
                emitTerminalEvents: true,
                clearRelativeMode: true,
                sampleBoundaryOnRails: true,
                logTag: "recording stop");

            // Capture persistence artifacts at stop-time so later scene changes
            // don't depend on whatever vessel is currently active.
            CaptureAtStop = BuildCaptureRecording(
                FlightGlobals.ActiveVessel != null
                    ? FlightGlobals.ActiveVessel.vesselName
                    : "Unknown Vessel",
                VesselDestroyedDuringRecording);

            double duration = Recording.Count > 0
                ? Recording[Recording.Count - 1].ut - Recording[0].ut
                : 0;

            ParsekLog.Info("Recorder", $"Recording stopped. {Recording.Count} points, {OrbitSegments.Count} orbit segments over {duration:F1}s");
            ParsekLog.ScreenMessage($"Recording STOPPED: {Recording.Count} points", 3f);
        }

        /// <summary>
        /// Stops recording silently for a chain boundary (dock/undock/boarding).
        /// Same as StopRecording but without the "STOPPED" screen message.
        /// </summary>
        public void StopRecordingForChainBoundary()
        {
            // FinalizeRecordingState stashes the exact terminal events it emits in
            // lastEmittedTerminalEvents; ResumeAfterFalseAlarm uses that list to remove
            // them by content-match if this stop turns out to be a false alarm (#287).
            FinalizeRecordingState(
                emitTerminalEvents: true,
                clearRelativeMode: true,
                sampleBoundaryOnRails: true,
                logTag: "chain boundary stop");

            CaptureAtStop = BuildCaptureRecording(
                FlightGlobals.ActiveVessel != null
                    ? FlightGlobals.ActiveVessel.vesselName
                    : "Unknown Vessel",
                VesselDestroyedDuringRecording);

            double duration = Recording.Count > 0
                ? Recording[Recording.Count - 1].ut - Recording[0].ut
                : 0;

            ParsekLog.Info("Recorder", $"Recording stopped (chain boundary). {Recording.Count} points, {OrbitSegments.Count} orbit segments over {duration:F1}s");
        }

        /// <summary>
        /// Re-enables recording after a false-alarm split detection (e.g., joint break that
        /// only produced debris, undock of a non-trackable vessel). Restores the recorder to
        /// the active state it was in before StopRecordingForChainBoundary() was called.
        /// The internal Recording/OrbitSegments/PartEvents lists are still intact since
        /// StopRecordingForChainBoundary only copies them into CaptureAtStop but does not clear them.
        /// </summary>
        public void ResumeAfterFalseAlarm()
        {
            if (IsRecording)
            {
                ParsekLog.Warn("Recorder", "ResumeAfterFalseAlarm called while already recording — ignoring");
                return;
            }

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null)
            {
                ParsekLog.Warn("Recorder", "ResumeAfterFalseAlarm: no active vessel — cannot resume");
                return;
            }

            IsRecording = true;

            // Undo orphaned terminal events added by FinalizeRecordingState (#255).
            // StopRecordingForChainBoundary emits EngineShutdown/RCSStop events for all
            // active engines. If the split turns out to be a false alarm (debris only),
            // these events are wrong — the engines are still running.
            //
            // Content-based removal (#287): we match each saved terminal event against
            // PartEvents by (ut, pid, type, midx) and remove the matching entry. This is
            // robust across unstable sort paths and any code that appends events between
            // stop and resume — index-based RemoveRange was brittle and left spurious
            // terminal Shutdowns in committed recordings, causing engine FX to turn off
            // permanently after booster staging (bug #287 2026-04-09 Kerbal X playtest).
            int contentRemoved = RemoveLastEmittedTerminals();
            if (contentRemoved > 0)
            {
                ParsekLog.Info("Recorder",
                    $"ResumeAfterFalseAlarm: removed {contentRemoved} orphaned terminal event(s) " +
                    $"(PartEvents count: {PartEvents.Count})");
            }

            // Restore rewind save from CaptureAtStop — StopRecordingForChainBoundary
            // cleared RewindSaveFileName after copying it to CaptureAtStop, so if we
            // resume, we need to reclaim it before discarding CaptureAtStop.
            if (CaptureAtStop != null && !string.IsNullOrEmpty(CaptureAtStop.RewindSaveFileName)
                && string.IsNullOrEmpty(RewindSaveFileName))
            {
                RewindSaveFileName = CaptureAtStop.RewindSaveFileName;
                RewindReservedFunds = CaptureAtStop.RewindReservedFunds;
                RewindReservedScience = CaptureAtStop.RewindReservedScience;
                RewindReservedRep = CaptureAtStop.RewindReservedRep;
            }

            CaptureAtStop = null;
            HasPendingJointBreakCheck = false;

            // Re-register the Harmony patch and part event hooks
            Patches.PhysicsFramePatch.ActiveRecorder = this;
            SubscribePartEvents();

            // Update RecordingVesselId in case the vessel PID changed (unlikely but defensive)
            RecordingVesselId = v.persistentId;

            // StopRecordingForChainBoundary closes the active TrackSection before copying
            // CaptureAtStop. If we resume the same recorder without reopening a continuation
            // section, subsequent samples only extend the flat Recording list and the
            // section-authoritative sidecar truncates playback at the false-alarm boundary.
            double resumeUT = Planetarium.GetUniversalTime();
            OrbitSegment? resumeOrbitSegment = null;
            if (TryGetFalseAlarmResumeTrackSection(out var resumeSection)
                && resumeSection.referenceFrame == ReferenceFrame.OrbitalCheckpoint
                && v.packed)
            {
                resumeOrbitSegment = CreateOrbitSegmentWithRotation(v, resumeUT);
            }

            RestoreTrackSectionAfterFalseAlarm(resumeUT, resumeOrbitSegment);

            ParsekLog.Info("Recorder", $"Recording resumed after false alarm ({Recording.Count} points preserved)");
        }

        /// <summary>
        /// Reopens a continuation TrackSection after <see cref="StopRecordingForChainBoundary"/>
        /// was later treated as a false alarm. Preserves the last section's environment /
        /// reference-frame metadata and seeds the new section with the previous boundary point
        /// so section-authoritative playback stays continuous across the stop/resume seam.
        /// </summary>
        internal void RestoreTrackSectionAfterFalseAlarm(double ut)
        {
            RestoreTrackSectionAfterFalseAlarm(ut, null);
        }

        internal void RestoreTrackSectionAfterFalseAlarm(double ut, OrbitSegment? resumeOrbitSegment)
        {
            if (trackSectionActive)
                return;

            TrackSection? resumeSection = null;
            if (TryGetFalseAlarmResumeTrackSection(out var restoredSection))
                resumeSection = restoredSection;

            SegmentEnvironment resumeEnv = resumeSection.HasValue
                ? resumeSection.Value.environment
                : (environmentHysteresis != null
                    ? environmentHysteresis.CurrentEnvironment
                    : SegmentEnvironment.Atmospheric);
            ReferenceFrame resumeRef = resumeSection.HasValue
                ? resumeSection.Value.referenceFrame
                : (isRelativeMode ? ReferenceFrame.Relative : ReferenceFrame.Absolute);
            TrackSectionSource resumeSource = resumeSection.HasValue
                ? resumeSection.Value.source
                : TrackSectionSource.Active;

            uint resumeAnchor = 0;
            if (resumeRef == ReferenceFrame.Relative)
            {
                resumeAnchor = resumeSection.HasValue ? resumeSection.Value.anchorVesselId : currentAnchorPid;

                // Validate the resume anchor before re-entering Relative. The
                // saved anchor PID may now belong to a tree member (post-staging
                // sibling) or to a vessel that's no longer loaded (destroyed,
                // unloaded after time warp). In either case, decoded relative
                // offsets at playback would multiply against the wrong pose
                // and produce visible drift / sub-surface jumps. Same root
                // cause as the freshly-rebuilt treeVesselPids in
                // UpdateAnchorDetection, applied here at the resume seam so a
                // stale-anchor restore downgrades cleanly to Absolute and
                // lets the next anchor-detection tick re-pick a valid one.
                //
                // Skip the safety check when the scene's vessel list is
                // unqueryable (xUnit / pre-FlightReady scene loads): we
                // cannot prove the anchor is gone, so trust the saved
                // metadata. Production paths that legitimately reach this
                // method have FlightReady scene state available.
                bool sceneVesselsQueryable = TryGetFlightGlobalsVesselsForResumeValidation() != null;
                bool anchorIsTreeMember = false;
                bool anchorLoaded = true;
                if (sceneVesselsQueryable && resumeAnchor != 0)
                {
                    var freshTreePids = BuildTreeVesselPids();
                    if (freshTreePids != null && freshTreePids.Contains(resumeAnchor))
                        anchorIsTreeMember = true;
                    Vessel anchorVessel = FindVesselByPid(resumeAnchor);
                    anchorLoaded = anchorVessel != null && anchorVessel.loaded;
                }
                if (resumeAnchor == 0
                    || (sceneVesselsQueryable && (anchorIsTreeMember || !anchorLoaded)))
                {
                    if (resumeAnchor != 0)
                    {
                        ParsekLog.Info("Anchor",
                            $"RELATIVE resume rejected: anchorPid={resumeAnchor} " +
                            $"treeMember={anchorIsTreeMember} loaded={anchorLoaded} " +
                            $"vesselPid={RecordingVesselId} — starting ABSOLUTE section instead");
                    }
                    resumeAnchor = 0;
                    resumeRef = ReferenceFrame.Absolute;
                    isRelativeMode = false;
                    currentAnchorPid = 0;
                }
                else
                {
                    isRelativeMode = true;
                    currentAnchorPid = resumeAnchor;
                }
            }
            else
            {
                isRelativeMode = false;
                currentAnchorPid = 0;
            }

            TrajectoryPoint? boundaryPoint = resumeSection.HasValue
                ? GetLastFrameFromTrackSection(resumeSection.Value)
                : (TrajectoryPoint?)null;
            TrajectoryPoint? absoluteBoundaryPoint = resumeSection.HasValue
                ? GetLastAbsoluteFrameFromTrackSection(resumeSection.Value)
                : (TrajectoryPoint?)null;

            // Frame-mismatch repair when stale-anchor validation downgraded a
            // RELATIVE resume to ABSOLUTE: `boundaryPoint` is the prior
            // Relative section's last frame, whose lat/lon/alt fields hold
            // anchor-local Cartesian metres (NOT body-fixed surface coords).
            // Seeding it into a freshly-opened ABSOLUTE section would write
            // a meaningless metre-scale "lat/lon/alt" sample at the seam,
            // re-introducing the corrupted-trajectory class this PR closes.
            // The parallel v7 absolute shadow already carries the focused
            // vessel's true body-fixed position at the same UT — swap
            // boundaryPoint to that shadow value so the new ABSOLUTE
            // section's first sample matches its declared contract. Legacy
            // recordings without an absolute shadow (v5 and earlier
            // RELATIVE) fall through with no boundary seed; the next normal
            // sample seeds the ABSOLUTE section cleanly.
            bool downgradedRelativeToAbsolute = resumeSection.HasValue
                && resumeSection.Value.referenceFrame == ReferenceFrame.Relative
                && resumeRef == ReferenceFrame.Absolute;
            if (downgradedRelativeToAbsolute)
            {
                if (absoluteBoundaryPoint.HasValue)
                {
                    ParsekLog.Verbose("Anchor",
                        $"RELATIVE->ABSOLUTE resume: substituting absolute-shadow boundary point " +
                        $"at ut={absoluteBoundaryPoint.Value.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                        $"in place of relative-frame boundary point");
                    boundaryPoint = absoluteBoundaryPoint;
                }
                else
                {
                    ParsekLog.Verbose("Anchor",
                        $"RELATIVE->ABSOLUTE resume: prior Relative section has no absolute-shadow " +
                        $"(legacy v5/v6); skipping boundary seed to avoid mis-framed sample");
                    boundaryPoint = null;
                }
                // The new section is ABSOLUTE, so absoluteBoundaryPoint will
                // be ignored by SeedBoundaryPoint anyway — clear it for
                // clarity.
                absoluteBoundaryPoint = null;
            }

            StartNewTrackSection(resumeEnv, resumeRef, ut, resumeSource);

            if (resumeRef == ReferenceFrame.Relative)
                currentTrackSection.anchorVesselId = resumeAnchor;

            isOnRails = false;
            if (resumeRef == ReferenceFrame.OrbitalCheckpoint)
            {
                if (resumeOrbitSegment.HasValue)
                {
                    currentOrbitSegment = resumeOrbitSegment.Value;
                    isOnRails = true;
                }
                else
                {
                    ParsekLog.Warn("Recorder",
                        $"ResumeAfterFalseAlarm: reopened OrbitalCheckpoint TrackSection without restoring on-rails state at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
                }
            }

            if (resumeRef != ReferenceFrame.OrbitalCheckpoint)
                SeedBoundaryPoint(boundaryPoint, absoluteBoundaryPoint);
        }

        private bool TryGetFalseAlarmResumeTrackSection(out TrackSection section)
        {
            // Prefer the recorder's most recently closed section snapshot even when it was
            // discarded instead of persisted (e.g. a <1s zero-frame RELATIVE flicker).
            if (currentTrackSection.frames != null || currentTrackSection.checkpoints != null)
            {
                section = currentTrackSection;
                return true;
            }

            if (TrackSections.Count > 0)
            {
                section = TrackSections[TrackSections.Count - 1];
                return true;
            }

            section = default;
            return false;
        }

        /// <summary>
        /// Handles vessel PID change detected during OnPhysicsFrame.
        /// Decides whether to continue (EVA), background (tree), capture+stop, or chain.
        /// Returns true if the frame should be skipped (recording stopped/backgrounded).
        /// </summary>
        private bool HandleVesselSwitchDuringRecording(Vessel v)
        {
            // Gloops mode: auto-stop on vessel switch (no tree/chain logic).
            // Call StopRecording (not bare FinalizeRecordingState) so CaptureAtStop
            // is built — ParsekFlight detects the auto-stopped state and commits.
            if (IsGloopsMode)
            {
                ParsekLog.Info("Recorder",
                    $"Gloops recorder auto-stopping on vessel switch " +
                    $"(was pid={RecordingVesselId}, now pid={v.persistentId})");
                StopRecording();
                GloopsAutoStoppedByVesselSwitch = true;
                return true;
            }

            // 1. Dock merge guard — must be first. DockMergePending was set by
            //    OnPartCouple; let the existing capture+stop flow handle it below.
            //    Do NOT enter tree decision logic for dock PID changes.
            VesselSwitchDecision decision = DockMergePending
                ? VesselSwitchDecision.DockMerge
                : DecideOnVesselSwitch(
                    RecordingVesselId, v.persistentId, v.isEVA, RecordingStartedAsEva,
                    UndockSiblingPid, activeTree: ActiveTree);

            ParsekLog.Verbose("Recorder", $"Vessel switch detected: decision={decision}, " +
                $"oldPid={RecordingVesselId}, newPid={v.persistentId}, " +
                $"isEva={v.isEVA}, hasTree={ActiveTree != null}");

            // 2. ContinueOnEva — early return (existing, unchanged)
            if (decision == VesselSwitchDecision.ContinueOnEva)
            {
                RecordingVesselId = v.persistentId;
                SamplePosition(v);
                RefreshBackupSnapshot(v, "eva_switch", force: true);
                ParsekLog.Verbose("Recorder", $"Recording switched to EVA vessel (pid={v.persistentId})");
                return true; // skip remaining OnPhysicsFrame polling for this frame
            }

            // 3. Tree decisions — early return BEFORE CaptureAtStop
            if (decision == VesselSwitchDecision.TransitionToBackground)
            {
                TransitionToBackground();
                TransitionToBackgroundPending = true;
                ParsekLog.Verbose("Recorder", $"Tree: transition to background " +
                    $"(was pid={RecordingVesselId}, now pid={v.persistentId})");
                return true;
            }
            if (decision == VesselSwitchDecision.PromoteFromBackground)
            {
                // Cannot promote here — vessel may not be loaded yet.
                // TransitionToBackground the current recorder, let onVesselSwitchComplete handle promotion.
                TransitionToBackground();
                TransitionToBackgroundPending = true;
                ParsekLog.Verbose("Recorder", $"Tree: promote from background " +
                    $"(was pid={RecordingVesselId}, now pid={v.persistentId}) — backgrounding current, promotion deferred");
                return true;
            }

            // 4. Existing CaptureAtStop + IsRecording=false block
            Vessel recordedVessel = FindVesselByPid(RecordingVesselId);

            CaptureAtStop = BuildCaptureRecording(
                recordedVessel != null ? recordedVessel.vesselName : v.vesselName,
                VesselDestroyedDuringRecording || recordedVessel == null,
                recordedVessel);

            Patches.PhysicsFramePatch.ActiveRecorder = null;
            UnsubscribePartEvents();
            IsRecording = false;

            // 5. Existing decision dispatch
            if (decision == VesselSwitchDecision.ChainToVessel)
            {
                ChainToVesselPending = true;
                ParsekLog.Verbose("Recorder", $"EVA boarded vessel (was pid={RecordingVesselId}, now pid={v.persistentId}) — chain pending");
                return true;
            }

            if (decision == VesselSwitchDecision.DockMerge)
            {
                DockMergePending = true;
                ParsekLog.Verbose("Recorder", $"Dock merge detected (was pid={RecordingVesselId}, now pid={v.persistentId}) — dock pending");
                return true;
            }

            if (decision == VesselSwitchDecision.UndockSwitch)
            {
                UndockSwitchPending = true;
                ParsekLog.Verbose("Recorder", $"Undock sibling switch (was pid={RecordingVesselId}, now pid={v.persistentId}) — undock switch pending");
                return true;
            }

            ParsekLog.Verbose("Recorder", $"Active vessel changed during recording — auto-stopping " +
                $"(decision={decision}, was pid={RecordingVesselId}, now pid={v.persistentId}, " +
                $"nowIsEva={v.isEVA}, startedAsEva={RecordingStartedAsEva})");
            ParsekLog.ScreenMessage("Recording stopped - vessel changed", 3f);
            return true;
        }

        /// <summary>
        /// Called by the Harmony postfix on each physics frame for the active vessel.
        /// </summary>
        public void OnPhysicsFrame(Vessel v)
        {
            if (!IsRecording) return;
            if (isOnRails) return;

            if (v.persistentId != RecordingVesselId)
            {
                if (HandleVesselSwitchDuringRecording(v))
                    return;
            }

            // Check atmosphere / altitude boundary (before part state polling)
            CheckAtmosphereBoundary(v);
            CheckAltitudeBoundary(v);

            PollPartStates(v);
            UpdateEnvironmentTracking(v);

            UpdateAnchorDetection(v);
            RefreshFinalizationCache(v, "periodic");

            double currentUT = Planetarium.GetUniversalTime();
            Vector3 currentVelocity = SampleCurrentVelocity(v);
            if (v.packed)
            {
                // OnPhysicsFrame normally runs off-rails, but a packed vessel occasionally
                // tick-enters this path during pack/unpack transitions. Log so the shift
                // from rb_velocityD+Krakensbane to obt_velocity is observable in logs.
                ParsekLog.VerboseRateLimited("Recorder", "onphysicsframe-packed",
                    $"OnPhysicsFrame sample with packed vessel at ut={currentUT:F2}; " +
                    $"using obt_velocity instead of rb_velocityD+Krakensbane",
                    5.0);
            }
            Quaternion currentWorldRotation = TrajectoryMath.SanitizeQuaternion(v.transform.rotation);
            bool motionTriggered = TrajectoryMath.ShouldRecordPoint(
                currentVelocity,
                lastRecordedVelocity,
                currentUT,
                lastRecordedUT,
                minSampleInterval,
                maxSampleInterval,
                velocityDirThreshold,
                speedChangeThreshold);
            bool attitudeTriggered = ShouldRecordAttitudePoint(
                currentWorldRotation,
                lastRecordedWorldRotation,
                currentUT,
                lastRecordedUT,
                hasLastRecordedWorldRotation,
                minSampleInterval,
                attitudeSampleThresholdDegrees);

            if (!motionTriggered && !attitudeTriggered)
            {
                // Bug #595: the previous 2.0s rate-limit window still produced
                // ~200 lines per 30-min session during stationary intervals.
                // Use the default 5s window — the line conveys "stationary,
                // waiting" which is a steady state, so per-window granularity
                // is plenty.
                ParsekLog.VerboseRateLimited("Recorder", "sample-skipped",
                    $"Sample skipped at ut={currentUT:F2}; waiting for motion/attitude trigger");
                return;
            }

            TrajectoryPoint point = BuildTrajectoryPoint(v, currentVelocity, currentUT);
            TrajectoryPoint absolutePoint = point;

            bool relativeApplied = ApplyRelativeOffset(ref point, v);
            CommitRecordedPoint(point, v, relativeApplied ? (TrajectoryPoint?)absolutePoint : null);
        }

        /// <summary>
        /// Polls all part-module states (parachutes, jettisons, engines, RCS, deployables,
        /// ladders, animation groups, aero/control surfaces, lights, gear, cargo bays,
        /// fairings, robotics). Runs every physics frame before adaptive sampling.
        /// </summary>
        private void PollPartStates(Vessel v)
        {
            CheckParachuteState(v);
            CheckJettisonState(v);
            CheckEngineState(v);
            CheckRcsState(v);
            CheckDeployableState(v);
            CheckLadderState(v);
            CheckAnimationGroupState(v);
            CheckAeroSurfaceState(v);
            CheckControlSurfaceState(v);
            CheckRobotArmScannerState(v);
            CheckAnimateHeatState(v);
            CheckAnimateGenericState(v);
            CheckLightState(v);
            CheckGearState(v);
            CheckCargoBayState(v);
            CheckFairingState(v);
            CheckRoboticState(v);
        }

        /// <summary>
        /// Checks the environment hysteresis for a transition (e.g. Atmospheric -> ExoBallistic)
        /// and creates a new TrackSection at the boundary if the environment changed.
        /// Runs every physics frame regardless of adaptive sampling.
        /// </summary>
        private void UpdateEnvironmentTracking(Vessel v)
        {
            if (environmentHysteresis != null)
            {
                var rawEnv = ClassifyCurrentEnvironment(v);
                double currentUT = Planetarium.GetUniversalTime();
                if (environmentHysteresis.Update(rawEnv, currentUT))
                {
                    if (TryApplyRestoreEnvironmentResync(environmentHysteresis.CurrentEnvironment, currentUT))
                        return;

                    // Environment changed — close current section, start new one.
                    // Preserve current reference frame (RELATIVE stays RELATIVE).
                    // Sample boundary point BEFORE closing — adaptive sampler may have
                    // skipped frames, so last recorded point could be far from here.
                    SamplePosition(v);

                    // Capture boundary point to seed the new section (#283).
                    // Same reference frame on both sides, so the point is directly reusable.
                    TrajectoryPoint? boundaryPoint = GetLastTrackSectionFrame();
                    TrajectoryPoint? absoluteBoundaryPoint = GetLastTrackSectionAbsoluteFrame();

                    var currentRef = isRelativeMode ? ReferenceFrame.Relative : ReferenceFrame.Absolute;
                    CloseCurrentTrackSection(currentUT);
                    StartNewTrackSection(environmentHysteresis.CurrentEnvironment, currentRef,
                        currentUT);
                    if (isRelativeMode)
                        currentTrackSection.anchorVesselId = currentAnchorPid;

                    SeedBoundaryPoint(boundaryPoint, absoluteBoundaryPoint);
                }
            }
        }

        /// <summary>
        /// In RELATIVE mode, rewrites the trajectory point using the version-specific
        /// RELATIVE-frame contract. If the anchor is no longer loaded, force-exits
        /// RELATIVE mode and starts a new ABSOLUTE TrackSection.
        /// </summary>
        private bool ApplyRelativeOffset(ref TrajectoryPoint point, Vessel v)
        {
            if (!isRelativeMode || currentAnchorPid == 0)
                return false;

            Vessel anchor = FindVesselByPid(currentAnchorPid);
            if (anchor != null)
            {
                return ApplyRelativeOffsetForAnchor(ref point, v, anchor, currentAnchorPid, logSample: true);
            }
            else
            {
                // Anchor vessel no longer loaded — force transition out of RELATIVE mode.
                // The point keeps its absolute lat/lon/alt from the vessel (correct for ABSOLUTE).
                // Close the RELATIVE section and start a new ABSOLUTE section so the frame
                // tag matches the data.
                ParsekLog.Warn("Anchor",
                    $"Anchor vessel pid={currentAnchorPid} not found in loaded vessels — " +
                    $"forcing transition to ABSOLUTE at UT={Planetarium.GetUniversalTime():F2}");
                var oldAnchor = currentAnchorPid;
                isRelativeMode = false;
                currentAnchorPid = 0;
                CloseCurrentTrackSection(Planetarium.GetUniversalTime());
                var env = environmentHysteresis != null
                    ? environmentHysteresis.CurrentEnvironment
                    : SegmentEnvironment.Atmospheric;
                StartNewTrackSection(env, ReferenceFrame.Absolute, Planetarium.GetUniversalTime());
                ParsekLog.Info("Anchor",
                    $"RELATIVE mode force-exited: anchor pid={oldAnchor} unloaded, " +
                    $"new ABSOLUTE section started");
                return false;
            }
        }

        private bool ApplyRelativeOffsetForAnchor(
            ref TrajectoryPoint point,
            Vessel v,
            Vessel anchor,
            uint anchorPid,
            bool logSample)
        {
            if (v == null || anchor == null || anchorPid == 0u)
                return false;

            int recordingFormatVersion = ResolveActiveRecordingFormatVersion();
            bool useLocalContract = RecordingStore.UsesRelativeLocalFrameContract(
                recordingFormatVersion);
            Vector3d offset;
            if (useLocalContract)
            {
                offset = TrajectoryMath.ComputeRelativeLocalOffset(
                    v.GetWorldPos3D(),
                    anchor.GetWorldPos3D(),
                    anchor.transform.rotation);
                point.rotation = TrajectoryMath.ComputeRelativeLocalRotation(
                    v.transform.rotation,
                    anchor.transform.rotation);
            }
            else
            {
                Vector3d focusPos = v.mainBody.GetWorldSurfacePosition(
                    v.latitude, v.longitude, v.altitude);
                Vector3d anchorPos = v.mainBody.GetWorldSurfacePosition(
                    anchor.latitude, anchor.longitude, anchor.altitude);
                offset = TrajectoryMath.ComputeRelativeOffset(focusPos, anchorPos);
            }

            point.latitude = offset.x;
            point.longitude = offset.y;
            point.altitude = offset.z;

            if (logSample)
            {
                ParsekLog.VerboseRateLimited("Anchor", "relative-offset",
                    $"RELATIVE sample: contract={RecordingStore.DescribeRelativeFrameContract(recordingFormatVersion)} " +
                    $"version={recordingFormatVersion} dx={offset.x:F2} dy={offset.y:F2} dz={offset.z:F2} " +
                    $"anchorPid={anchorPid} |offset|={offset.magnitude:F2}m",
                    2.0);
            }
            return true;
        }

        private void SeedRelativeBoundaryPoint(Vessel v, uint anchorPid, double boundaryUT)
        {
            if (v == null || anchorPid == 0u)
                return;
            Vessel anchor = FindVesselByPid(anchorPid);
            if (anchor == null)
                return;

            TrajectoryPoint absolutePoint = BuildTrajectoryPoint(
                v,
                SampleCurrentVelocity(v),
                boundaryUT);
            TrajectoryPoint relativePoint = absolutePoint;
            if (!ApplyRelativeOffsetForAnchor(ref relativePoint, v, anchor, anchorPid, logSample: false))
                return;

            SeedBoundaryPoint(relativePoint, absolutePoint);
            ParsekLog.Verbose("Anchor",
                $"RELATIVE boundary seeded: anchorPid={anchorPid} " +
                $"ut={boundaryUT.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Appends the trajectory point to the flat Recording list and the current TrackSection,
        /// updates last-recorded bookkeeping, refreshes the backup snapshot, and emits periodic
        /// diagnostic logging.
        /// </summary>
        private void CommitRecordedPoint(TrajectoryPoint point, Vessel v, TrajectoryPoint? absoluteShadowPoint = null)
        {
            // Guard: detect time regression (quickload/revert during recording).
            // Trim all points recorded after the new time to maintain monotonicity.
            // Callers (this method + SamplePosition) reset lastRecordedUT to point.ut
            // immediately after the trim returns, so the stale value inside
            // TrimRecordingToUT's log line is fine — it reflects the pre-trim state.
            if (Recording.Count > 0 && point.ut < lastRecordedUT - TimeRegressionThresholdSeconds)
            {
                TrimRecordingToUT(point.ut);
            }

            // Phase 7 (design doc §13, §18 Phase 7): for SurfaceMobile sections,
            // capture the per-sample ground clearance so playback can apply
            // continuous terrain correction at render time. NaN for any other
            // environment / RELATIVE frame; the playback path falls through
            // to the legacy altitude branch. Mutates `point` BEFORE the flat
            // list / section append so both stores see the same value.
            if (trackSectionActive
                && currentTrackSection.referenceFrame == ReferenceFrame.Absolute
                && currentTrackSection.environment == SegmentEnvironment.SurfaceMobile
                && v != null && v.mainBody != null && v.mainBody.pqsController != null)
            {
                double terrainHeight = v.mainBody.TerrainAltitude(point.latitude, point.longitude, true);
                point.recordedGroundClearance = point.altitude - terrainHeight;
                surfaceMobileSamplesThisSection++;
                if (double.IsNaN(surfaceMobileMinClearanceThisSection)
                    || point.recordedGroundClearance < surfaceMobileMinClearanceThisSection)
                    surfaceMobileMinClearanceThisSection = point.recordedGroundClearance;
                if (double.IsNaN(surfaceMobileMaxClearanceThisSection)
                    || point.recordedGroundClearance > surfaceMobileMaxClearanceThisSection)
                    surfaceMobileMaxClearanceThisSection = point.recordedGroundClearance;
                surfaceMobileClearanceSumThisSection += point.recordedGroundClearance;
            }

            Recording.Add(point);
            lastRecordedUT = point.ut;
            lastRecordedVelocity = point.velocity;
            lastRecordedWorldRotation = v != null
                ? TrajectoryMath.SanitizeQuaternion(v.transform.rotation)
                : Quaternion.identity;
            hasLastRecordedWorldRotation = v != null;
            LastRecordedAltitude = point.altitude;

            // Dual-write: flat Points list + current TrackSection
            if (trackSectionActive && currentTrackSection.frames != null)
            {
                currentTrackSection.frames.Add(point);
                if (currentTrackSection.referenceFrame == ReferenceFrame.Relative
                    && absoluteShadowPoint.HasValue)
                {
                    if (currentTrackSection.absoluteFrames == null)
                        currentTrackSection.absoluteFrames = new List<TrajectoryPoint>();
                    currentTrackSection.absoluteFrames.Add(absoluteShadowPoint.Value);
                }
                UpdateTrackSectionAltitude((float)point.altitude);
            }

            RefreshBackupSnapshot(v, "periodic");

            // Update diagnostics growth rate
            if (DiagnosticsState.hasActiveGrowthRate)
            {
                var gr = DiagnosticsState.activeGrowthRate;
                gr.totalPoints = Recording.Count;
                gr.totalEvents = PartEvents.Count;
                double startUT = Recording.Count > 0 ? Recording[0].ut : point.ut;
                gr.elapsedSeconds = point.ut - startUT;
                if (gr.elapsedSeconds > 0)
                {
                    gr.pointsPerSecond = gr.totalPoints / gr.elapsedSeconds;
                    gr.eventsPerSecond = gr.totalEvents / gr.elapsedSeconds;
                }
                else
                {
                    gr.pointsPerSecond = 0;
                    gr.eventsPerSecond = 0;
                }
                gr.estimatedFinalBytes = (long)(gr.totalPoints * DiagnosticsState.avgBytesPerPoint + gr.totalEvents * 40);
                DiagnosticsState.activeGrowthRate = gr;
            }

            if (Recording.Count % 10 == 0)
            {
                ParsekLog.VerboseRateLimited("Recorder", "recorded-point",
                    $"Recorded point #{Recording.Count}: {point}", 5.0);
            }
        }

        /// <summary>
        /// Minimum backwards UT jump (seconds) that triggers a quickload/revert trim.
        /// Guards against false positives from sub-second time-warp boundary jitter
        /// while still catching user-initiated F9 (which typically rewinds tens of
        /// seconds or more).
        /// </summary>
        internal const double TimeRegressionThresholdSeconds = 1.0;

        /// <summary>
        /// Trims the recording buffer back to before the given UT.
        /// Called when a quickload/revert causes game time to regress, making
        /// previously-recorded points invalid (alternate timeline data).
        /// Also trims PartEvents, OrbitSegments, and TrackSection frames.
        /// </summary>
        internal void TrimRecordingToUT(double newUT)
        {
            int oldCount = Recording.Count;

            // Linear scan: find first point with UT >= newUT. Points are monotonic
            // by construction (guard in CommitRecordedPoint), so binary search would
            // work, but recording sizes are bounded at low thousands and linear is
            // clearer. Revisit if profiling shows this is hot.
            int trimIdx = Recording.Count;
            for (int i = 0; i < Recording.Count; i++)
            {
                if (Recording[i].ut >= newUT)
                {
                    trimIdx = i;
                    break;
                }
            }

            if (trimIdx < Recording.Count)
                Recording.RemoveRange(trimIdx, Recording.Count - trimIdx);

            // Trim PartEvents after newUT
            int oldPartEvents = PartEvents.Count;
            PartEvents.RemoveAll(e => e.ut >= newUT);

            // Trim OrbitSegments that start after newUT
            int oldOrbitSegs = OrbitSegments.Count;
            OrbitSegments.RemoveAll(s => s.startUT >= newUT);

            // Trim current TrackSection frames
            if (trackSectionActive && currentTrackSection.frames != null)
            {
                int frameTrimIdx = currentTrackSection.frames.Count;
                for (int i = 0; i < currentTrackSection.frames.Count; i++)
                {
                    if (currentTrackSection.frames[i].ut >= newUT)
                    {
                        frameTrimIdx = i;
                        break;
                    }
                }
                if (frameTrimIdx < currentTrackSection.frames.Count)
                    currentTrackSection.frames.RemoveRange(frameTrimIdx,
                        currentTrackSection.frames.Count - frameTrimIdx);
            }
            if (trackSectionActive && currentTrackSection.absoluteFrames != null)
            {
                int shadowTrimIdx = currentTrackSection.absoluteFrames.Count;
                for (int i = 0; i < currentTrackSection.absoluteFrames.Count; i++)
                {
                    if (currentTrackSection.absoluteFrames[i].ut >= newUT)
                    {
                        shadowTrimIdx = i;
                        break;
                    }
                }
                if (shadowTrimIdx < currentTrackSection.absoluteFrames.Count)
                    currentTrackSection.absoluteFrames.RemoveRange(shadowTrimIdx,
                        currentTrackSection.absoluteFrames.Count - shadowTrimIdx);
            }

            // Locale-safe formatting (comma-locale machines would otherwise write "27 266,0"
            // which breaks downstream log parsers).
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            ParsekLog.Warn("Recorder",
                $"Time regression detected: UT went from {lastRecordedUT.ToString("F1", ic)} to " +
                $"{newUT.ToString("F1", ic)} " +
                $"(delta={(newUT - lastRecordedUT).ToString("F1", ic)}s). Trimmed recording: " +
                $"points {oldCount}→{Recording.Count}, " +
                $"partEvents {oldPartEvents}→{PartEvents.Count}, " +
                $"orbitSegments {oldOrbitSegs}→{OrbitSegments.Count}");
        }

        /// <summary>
        /// Bug #419 — pure monotonicity predicate for trajectory-point appends.
        /// Returns true when appending <paramref name="incomingUT"/> to
        /// <paramref name="points"/> preserves the monotonically-non-decreasing UT
        /// invariant that <c>CommittedRecordingsHaveValidData</c> checks
        /// (<c>points[i].ut &gt;= points[i-1].ut</c>). Equal UTs are accepted —
        /// boundary seeds and flush-time overlap dedup can produce same-UT duplicates
        /// that are harmless to downstream consumers. Empty / null <paramref name="points"/>
        /// always returns true (first append is trivially monotonic).
        /// </summary>
        internal static bool IsAppendUTMonotonic(List<TrajectoryPoint> points, double incomingUT)
        {
            if (points == null || points.Count == 0)
                return true;
            return incomingUT >= points[points.Count - 1].ut;
        }

        /// <summary>
        /// Bug #419 — pure helper that trims trajectory points at or after
        /// <paramref name="boundaryUT"/> from <paramref name="points"/>, preserving all
        /// earlier points. Returns the number of points removed. Null / empty input
        /// is a no-op that returns 0. Callers use this to guarantee that a freshly
        /// spawned debris/breakup child recording cannot hold any inherited sample
        /// whose UT would collide with the post-boundary sampler stream.
        /// The comparison is inclusive of <paramref name="boundaryUT"/> — the caller
        /// is expected to re-seed the child with an authoritative initial point at
        /// exactly <paramref name="boundaryUT"/>, so any pre-existing point at that
        /// UT would be a duplicate anyway.
        /// </summary>
        internal static int TrimPointsAtOrAfterUT(List<TrajectoryPoint> points, double boundaryUT)
        {
            if (points == null || points.Count == 0)
                return 0;

            int trimIdx = points.Count;
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i].ut >= boundaryUT)
                {
                    trimIdx = i;
                    break;
                }
            }

            int removed = points.Count - trimIdx;
            if (removed > 0)
                points.RemoveRange(trimIdx, removed);
            return removed;
        }

        internal static Vector3 SampleCurrentVelocity(Vessel v)
        {
            if (v == null)
                return Vector3.zero;

            return v.packed
                ? (Vector3)v.obt_velocity
                : (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity());
        }

        internal static bool ShouldRecordAttitudePoint(
            Quaternion currentWorldRotation,
            Quaternion lastWorldRotation,
            double currentUT,
            double lastRecordedUT,
            bool hasLastWorldRotation,
            float minInterval,
            float rotationThresholdDegrees)
        {
            if (!hasLastWorldRotation || lastRecordedUT < 0)
                return false;

            double elapsed = currentUT - lastRecordedUT;
            if (elapsed < 0 || elapsed < minInterval)
                return false;

            return TrajectoryMath.ComputeQuaternionAngleDegrees(
                currentWorldRotation,
                lastWorldRotation) >= rotationThresholdDegrees;
        }

        /// <summary>
        /// Constructs a TrajectoryPoint from the vessel's current state and the given velocity.
        /// Extracted to deduplicate the identical construction in OnPhysicsFrame, SamplePosition,
        /// and post-switch baseline seeding.
        /// </summary>
        internal static TrajectoryPoint BuildTrajectoryPoint(Vessel v, Vector3 velocity, double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = v.latitude,
                longitude = v.longitude,
                altitude = v.altitude,
                rotation = v.srfRelRotation,
                velocity = velocity,
                bodyName = v.mainBody.name,
                funds = Funding.Instance != null ? Funding.Instance.Funds : 0,
                science = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0,
                reputation = Reputation.Instance != null ? Reputation.CurrentRep : 0,
                // Phase 7: NaN sentinel = "not captured / non-SurfaceMobile". The
                // SurfaceMobile section append in CommitRecordedPoint overwrites
                // with the real clearance when applicable.
                recordedGroundClearance = double.NaN
            };
        }

        /// <summary>
        /// Force-record a boundary point (used at on-rails/off-rails transitions).
        /// </summary>
        public void SamplePosition(Vessel v)
        {
            if (v == null)
            {
                ParsekLog.VerboseRateLimited("Recorder", "sample-null-vessel",
                    "SamplePosition called with null vessel");
                return;
            }

            Vector3 currentVelocity = SampleCurrentVelocity(v);
            TrajectoryPoint point = BuildTrajectoryPoint(
                v,
                currentVelocity,
                Planetarium.GetUniversalTime());
            TrajectoryPoint absolutePoint = point;
            bool relativeApplied = ApplyRelativeOffset(ref point, v);

            CommitRecordedPoint(point, v, relativeApplied ? (TrajectoryPoint?)absolutePoint : null);
            ParsekLog.Verbose("Recorder", $"Boundary point sampled at UT={point.ut:F1}");
        }

        /// <summary>
        /// Phase 9 (design doc §12, §17.3.2, §18 Phase 9): builds a synthetic
        /// <see cref="TrajectoryPoint"/> for <paramref name="v"/> at the exact
        /// <paramref name="eventUT"/> of a structural event, with
        /// <see cref="TrajectoryPointFlags.StructuralEventSnapshot"/> set on the
        /// returned <see cref="TrajectoryPoint.flags"/>. The point uses the vessel's
        /// current physics state — KSP doesn't surface a sub-tick interpolation API
        /// for <c>Vessel.latitude/longitude/altitude</c>, so this samples the live
        /// state at the moment the event handler fires, which is the same instant
        /// KSP's structural-event pipeline records as the event UT. Using one
        /// helper for every event handler guarantees both vessels' snapshots come
        /// from the same physics state read.
        /// </summary>
        /// <remarks>
        /// Pure / static so xUnit can drive it directly with a fake vessel. The
        /// caller (see <see cref="AppendStructuralEventSnapshot"/>) decides whether
        /// to commit the point to a recording.
        /// </remarks>
        internal static TrajectoryPoint BuildStructuralEventSnapshot(
            Vessel v, Vector3 velocity, double eventUT)
        {
            TrajectoryPoint pt = BuildTrajectoryPoint(v, velocity, eventUT);
            return ApplyStructuralEventFlag(pt);
        }

        /// <summary>
        /// Phase 9: pure flag-set helper. Returns a copy of <paramref name="pt"/>
        /// with the <see cref="TrajectoryPointFlags.StructuralEventSnapshot"/>
        /// bit OR'd into <see cref="TrajectoryPoint.flags"/>. Idempotent —
        /// already-flagged points are returned unchanged. Tests use this seam
        /// directly because the Vessel-driven overload requires a live KSP
        /// runtime; this overload pins the bit-set semantics independently.
        /// </summary>
        internal static TrajectoryPoint ApplyStructuralEventFlag(TrajectoryPoint pt)
        {
            pt.flags = (byte)((TrajectoryPointFlags)pt.flags | TrajectoryPointFlags.StructuralEventSnapshot);
            return pt;
        }

        /// <summary>
        /// Phase 9: appends one <see cref="TrajectoryPointFlags.StructuralEventSnapshot"/>-flagged
        /// point per involved vessel that matches this recorder's
        /// <see cref="RecordingVesselId"/>. The full FlightGlobals state is read once at the top
        /// of the call, so multiple involved vessels (e.g. dock parent + child) sampled in the
        /// same call share the same physics-state read — the §12 invariant.
        ///
        /// <para>No-op when the recorder is not currently recording (e.g. mid-stop), when the
        /// recording's format version pre-dates v10 (legacy recordings keep their interpolation
        /// path per §15.17), or when no involved vessel matches this recorder. Callers are
        /// responsible for filtering "noise" structural events (e.g. non-structural joint
        /// breaks, transient pid mismatches) before invoking the helper.</para>
        /// </summary>
        internal void AppendStructuralEventSnapshot(
            double eventUT, IEnumerable<Vessel> involved, string eventType)
        {
            if (!IsRecording)
            {
                ParsekLog.Verbose("Pipeline-Smoothing",
                    string.Format(CultureInfo.InvariantCulture,
                        "structural event snapshot skipped: not recording (eventType={0} ut={1})",
                        eventType ?? "<unknown>",
                        eventUT.ToString("F2", CultureInfo.InvariantCulture)));
                return;
            }
            if (involved == null)
                return;

            // Phase 9 schema gating: legacy recordings (format < v10) keep the
            // interpolated event ε path (§15.17). Reading the active recording's
            // format version mirrors how Phase 7 gates `recordedGroundClearance`.
            int activeFormatVersion = ResolveActiveRecordingFormatVersion();
            if (activeFormatVersion < RecordingStore.StructuralEventFlagFormatVersion)
            {
                ParsekLog.Verbose("Pipeline-Smoothing",
                    string.Format(CultureInfo.InvariantCulture,
                        "structural event snapshot skipped: recording format v{0} < v{1} " +
                        "(legacy interpolation path, §15.17) eventType={2} ut={3}",
                        activeFormatVersion,
                        RecordingStore.StructuralEventFlagFormatVersion,
                        eventType ?? "<unknown>",
                        eventUT.ToString("F2", CultureInfo.InvariantCulture)));
                return;
            }

            int snapshotsAppended = 0;
            int vesselsConsidered = 0;
            foreach (Vessel v in involved)
            {
                vesselsConsidered++;
                if (v == null) continue;
                if (v.persistentId != RecordingVesselId) continue;

                // Same physics state read as a regular tick sample. KSP's
                // event-fire timing means the live state IS the event-UT state —
                // there's no sub-tick interpolation API to bracket against.
                Vector3 velocity = SampleCurrentVelocity(v);
                TrajectoryPoint point = BuildStructuralEventSnapshot(v, velocity, eventUT);
                TrajectoryPoint absolutePoint = point;
                bool relativeApplied = ApplyRelativeOffset(ref point, v);

                CommitRecordedPoint(point, v, relativeApplied ? (TrajectoryPoint?)absolutePoint : null);
                snapshotsAppended++;

                ParsekLog.Verbose("Pipeline-Smoothing",
                    string.Format(CultureInfo.InvariantCulture,
                        "structural event snapshot UT={0} vesselId={1} eventType={2} flags={3} " +
                        "lat={4} lon={5} alt={6} relativeApplied={7}",
                        eventUT.ToString("F3", CultureInfo.InvariantCulture),
                        v.persistentId,
                        eventType ?? "<unknown>",
                        point.flags,
                        point.latitude.ToString("F4", CultureInfo.InvariantCulture),
                        point.longitude.ToString("F4", CultureInfo.InvariantCulture),
                        point.altitude.ToString("F1", CultureInfo.InvariantCulture),
                        relativeApplied ? "true" : "false"));
            }

            if (vesselsConsidered > 0 && snapshotsAppended == 0)
            {
                ParsekLog.Verbose("Pipeline-Smoothing",
                    string.Format(CultureInfo.InvariantCulture,
                        "structural event snapshot: no involved vessel matched RecordingVesselId={0} " +
                        "(eventType={1} ut={2} considered={3})",
                        RecordingVesselId,
                        eventType ?? "<unknown>",
                        eventUT.ToString("F2", CultureInfo.InvariantCulture),
                        vesselsConsidered));
            }
        }

        /// <summary>
        /// Baseline seed points built via <see cref="BuildTrajectoryPoint"/> carry
        /// body-relative lat/lon/alt and surface-relative rotation — both only valid
        /// inside an ABSOLUTE track section. A RELATIVE section would need the anchor
        /// pose captured at baseline time, which the post-switch path does not record.
        /// Returns true when the seed must be skipped to avoid corrupting the section.
        /// </summary>
        internal static bool ShouldSkipSeedDueToRelativeSection(
            bool trackSectionActive,
            ReferenceFrame sectionReferenceFrame)
        {
            return trackSectionActive && sectionReferenceFrame == ReferenceFrame.Relative;
        }

        internal void SeedTrajectoryPoint(TrajectoryPoint point, Vessel v, string reason)
        {
            if (v == null)
            {
                ParsekLog.VerboseRateLimited("Recorder", "seed-null-vessel",
                    "SeedTrajectoryPoint called with null vessel");
                return;
            }

            // Skip rather than corrupt — the first adaptive sample after the flip
            // still captures the current pose via the normal SamplePosition path.
            if (ShouldSkipSeedDueToRelativeSection(
                    trackSectionActive,
                    currentTrackSection.referenceFrame))
            {
                ParsekLog.Warn("Recorder",
                    $"Seed point skipped: reason={reason} UT={point.ut:F2} " +
                    $"sectionRef=Relative (baseline absolute lat/lon/alt cannot be " +
                    $"committed into a relative-frame section without the baseline " +
                    $"anchor pose, which was not captured)");
                return;
            }

            CommitRecordedPoint(point, v);
            ParsekLog.Verbose("Recorder",
                $"Seed point committed: reason={reason} UT={point.ut:F2}");
        }

        private int ResolveActiveRecordingFormatVersion()
        {
            if (ActiveTree != null
                && ActiveTree.Recordings != null
                && !string.IsNullOrEmpty(ActiveTree.ActiveRecordingId)
                && ActiveTree.Recordings.TryGetValue(ActiveTree.ActiveRecordingId, out Recording activeRec)
                && activeRec != null)
            {
                return activeRec.RecordingFormatVersion;
            }

            // A fresh sample that can't locate its owning Recording falls back to the
            // current format version, so new data always uses the newest RELATIVE-frame
            // contract. Warn — in practice we should always have an ActiveRecordingId
            // by the time sampling runs, so hitting this path points at a stale tree
            // state or a missing recording row.
            ParsekLog.VerboseRateLimited("Recorder", "format-version-fallback",
                $"ResolveActiveRecordingFormatVersion fallback to v" +
                $"{RecordingStore.CurrentRecordingFormatVersion}: " +
                $"activeTree={(ActiveTree != null)} " +
                $"activeRecordingId={ActiveTree?.ActiveRecordingId ?? "null"}",
                5.0);
            return RecordingStore.CurrentRecordingFormatVersion;
        }

        private static bool HasRelativeTrackSections(Recording rec)
        {
            if (rec?.TrackSections == null)
                return false;

            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                if (rec.TrackSections[i].referenceFrame == ReferenceFrame.Relative)
                    return true;
            }

            return false;
        }

        private void MaybeUpgradeActiveRecordingRelativeContract(string reason)
        {
            if (ActiveTree == null
                || ActiveTree.Recordings == null
                || string.IsNullOrEmpty(ActiveTree.ActiveRecordingId)
                || !ActiveTree.Recordings.TryGetValue(ActiveTree.ActiveRecordingId, out Recording activeRec)
                || activeRec == null)
            {
                return;
            }

            if (activeRec.RecordingFormatVersion >= RecordingStore.CurrentRecordingFormatVersion)
            {
                return;
            }

            bool hasRelativeTrackSections = HasRelativeTrackSections(activeRec);
            if (hasRelativeTrackSections
                && activeRec.RecordingFormatVersion < RecordingStore.RelativeLocalFrameFormatVersion)
            {
                ParsekLog.Info("Recorder",
                    $"Relative contract preserved: recording={activeRec.RecordingId} " +
                    $"version={activeRec.RecordingFormatVersion} " +
                    $"contract={RecordingStore.DescribeRelativeFrameContract(activeRec.RecordingFormatVersion)} " +
                    $"reason={reason} existingRelativeSections=true");
                return;
            }

            if (activeRec.RecordingFormatVersion < RecordingStore.PredictedOrbitSegmentFormatVersion)
                return;

            // Upgrading an already-open v6 recording does not synthesize
            // absoluteFrames for relative samples captured before this point.
            // Those legacy sections deliberately fall back to the pre-ReFly
            // frozen anchor trajectory path; only new v7 samples append
            // absolute shadow frames.
            int previousVersion = activeRec.RecordingFormatVersion;
            activeRec.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            ParsekLog.Info("Recorder",
                $"Relative contract upgraded: recording={activeRec.RecordingId} " +
                $"version={previousVersion}->{activeRec.RecordingFormatVersion} " +
                $"contract={RecordingStore.DescribeRelativeFrameContract(activeRec.RecordingFormatVersion)} " +
                $"reason={reason}");
        }

        /// <summary>
        /// Returns the last trajectory frame of the current TrackSection, or null if empty/inactive.
        /// Used to capture a boundary point before closing a section (#283).
        /// </summary>
        private TrajectoryPoint? GetLastTrackSectionFrame()
        {
            if (trackSectionActive && currentTrackSection.frames != null
                && currentTrackSection.frames.Count > 0)
                return currentTrackSection.frames[currentTrackSection.frames.Count - 1];
            return null;
        }

        private TrajectoryPoint? GetLastTrackSectionAbsoluteFrame()
        {
            if (trackSectionActive && currentTrackSection.absoluteFrames != null
                && currentTrackSection.absoluteFrames.Count > 0)
                return currentTrackSection.absoluteFrames[currentTrackSection.absoluteFrames.Count - 1];
            return null;
        }

        /// <summary>
        /// Returns the last payload frame from the most recently closed TrackSection, or null
        /// if no closed section carries sparse frames.
        /// </summary>
        private static TrajectoryPoint? GetLastFrameFromTrackSection(TrackSection section)
        {
            if (section.frames != null && section.frames.Count > 0)
                return section.frames[section.frames.Count - 1];
            return null;
        }

        private static TrajectoryPoint? GetLastAbsoluteFrameFromTrackSection(TrackSection section)
        {
            if (section.absoluteFrames != null && section.absoluteFrames.Count > 0)
                return section.absoluteFrames[section.absoluteFrames.Count - 1];
            return null;
        }

        /// <summary>
        /// Seeds the current (newly opened) TrackSection with a boundary point from the
        /// previous section. Eliminates the physics-frame gap that causes position
        /// discontinuities at section boundaries (#283).
        /// Only writes to the TrackSection frames — does NOT dual-write to the flat
        /// Recording list (the point is already there from SamplePosition).
        /// </summary>
        private void SeedBoundaryPoint(TrajectoryPoint? point, TrajectoryPoint? absoluteShadowPoint = null)
        {
            if (!point.HasValue) return;
            if (!trackSectionActive || currentTrackSection.frames == null) return;
            currentTrackSection.frames.Add(point.Value);
            if (currentTrackSection.referenceFrame == ReferenceFrame.Relative
                && absoluteShadowPoint.HasValue)
            {
                if (currentTrackSection.absoluteFrames == null)
                    currentTrackSection.absoluteFrames = new List<TrajectoryPoint>();
                currentTrackSection.absoluteFrames.Add(absoluteShadowPoint.Value);
            }
            UpdateTrackSectionAltitude((float)point.Value.altitude);
            ParsekLog.Verbose("Recorder",
                $"Boundary point seeded: ut={point.Value.ut.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Extracts the on-rails startup block from StartRecording: creates an orbit segment,
        /// captures orbital-frame rotation, sets isOnRails, and transitions the TrackSection
        /// from ABSOLUTE to ORBITAL_CHECKPOINT.
        /// </summary>
        private void InitializeOnRailsOrbitSegment(Vessel v, SegmentEnvironment initialEnv)
        {
            double packedUT = Planetarium.GetUniversalTime();
            currentOrbitSegment = CreateOrbitSegmentWithRotation(v, packedUT);
            isOnRails = true;

            // Recording started on rails — switch initial ABSOLUTE section to ORBITAL_CHECKPOINT
            CloseCurrentTrackSection(packedUT);
            StartNewTrackSection(initialEnv, ReferenceFrame.OrbitalCheckpoint, packedUT,
                TrackSectionSource.Checkpoint);
            ParsekLog.Info("Recorder",
                $"Reference frame transition: Absolute -> OrbitalCheckpoint (started on rails) at UT={packedUT.ToString("F2", CultureInfo.InvariantCulture)}");

            ParsekLog.Info("Recorder",
                $"Recording started on rails — orbit segment (body={v.mainBody.name}, ofrRot={currentOrbitSegment.orbitalFrameRotation})");
        }

        public void OnVesselGoOnRails(Vessel v)
        {
            if (!IsRecording) return;
            if (v != FlightGlobals.ActiveVessel)
            {
                ParsekLog.VerboseRateLimited("Recorder", "on-rails-other-vessel",
                    $"OnVesselGoOnRails: ignoring non-active vessel (pid={v?.persistentId})");
                return;
            }
            if (v.persistentId != RecordingVesselId) return;

            ClearRelativeModeForRailsTransition();
            SamplePosition(v);
            RefreshFinalizationCache(v, "go_on_rails", force: true);

            // Layer 1: Surface vessels (LANDED/SPLASHED/PRELAUNCH) stay in place on rails —
            // their Keplerian orbit is a sub-surface path through the planet, not a valid trajectory.
            // Skip orbit segment creation and keep the current Absolute track section.
            if (v.situation == Vessel.Situations.LANDED ||
                v.situation == Vessel.Situations.SPLASHED ||
                v.situation == Vessel.Situations.PRELAUNCH)
            {
                ParsekLog.Info("Recorder",
                    $"Vessel went on rails (surface, sit={v.situation}) — skipping orbit segment, position unchanged");
                return;
            }

            // Layer 2: Vessels below atmosphere — Keplerian orbit ignores drag, producing
            // underground trajectories during reentry/descent. Skip orbit segment; point
            // interpolation will lerp across the gap, which is far better than an underground arc.
            if (ShouldSkipOrbitSegmentForAtmosphere(v.mainBody.atmosphere, v.altitude, v.mainBody.atmosphereDepth))
            {
                ParsekLog.Info("Recorder",
                    $"Vessel went on rails in atmosphere (alt={v.altitude:F0}, atmoDepth={v.mainBody.atmosphereDepth:F0}) — skipping orbit segment");
                return;
            }

            CaptureOrbitSegmentWithRotation(v);
            EmitTerminalEventsAndClearActiveState();

            isOnRails = true;

            // Reference frame transition: ABSOLUTE -> ORBITAL_CHECKPOINT
            // (RELATIVE already cleared above before SamplePosition)
            double onRailsUT = Planetarium.GetUniversalTime();
            var currentEnv = environmentHysteresis != null
                ? environmentHysteresis.CurrentEnvironment
                : SegmentEnvironment.ExoBallistic;
            CloseCurrentTrackSection(onRailsUT);
            StartNewTrackSection(currentEnv, ReferenceFrame.OrbitalCheckpoint, onRailsUT,
                TrackSectionSource.Checkpoint);
            ParsekLog.Info("Recorder",
                $"Reference frame transition: -> OrbitalCheckpoint at UT={onRailsUT.ToString("F2", CultureInfo.InvariantCulture)}");

            ParsekLog.Verbose("Recorder",
                $"Vessel went on rails — orbit segment (body={v.mainBody.name}, " +
                $"ofrRot={currentOrbitSegment.orbitalFrameRotation}, " +
                $"angVel={currentOrbitSegment.angularVelocity}, " +
                $"persistentRotation={hasPersistentRotation})");
        }

        /// <summary>
        /// Clears RELATIVE mode state before sampling a boundary point at a rails transition.
        /// SamplePosition records raw lat/lon/alt (absolute coordinates); if called while
        /// isRelativeMode is true the point would have absolute values in a RELATIVE
        /// TrackSection, causing a position discontinuity during playback (#74).
        /// </summary>
        private void ClearRelativeModeForRailsTransition()
        {
            if (isRelativeMode)
            {
                ParsekLog.Info("Anchor",
                    $"RELATIVE mode cleared on rails transition: anchorPid={currentAnchorPid}");
                double clearUT = Planetarium.GetUniversalTime();
                var clearEnv = environmentHysteresis != null
                    ? environmentHysteresis.CurrentEnvironment
                    : SegmentEnvironment.ExoBallistic;
                CloseCurrentTrackSection(clearUT);
                StartNewTrackSection(clearEnv, ReferenceFrame.Absolute, clearUT);
                isRelativeMode = false;
                currentAnchorPid = 0;
            }
        }

        /// <summary>
        /// Creates a new orbit segment from the vessel's current orbit and captures the
        /// orbital-frame-relative rotation and angular velocity (if PersistentRotation is active).
        /// </summary>
        private void CaptureOrbitSegmentWithRotation(Vessel v)
        {
            currentOrbitSegment = CreateOrbitSegmentWithRotation(v, Planetarium.GetUniversalTime());
        }

        private OrbitSegment CreateOrbitSegmentWithRotation(Vessel v, double startUT)
        {
            var segment = CreateOrbitSegmentFromVessel(v, startUT);

            // Capture orbital-frame-relative rotation
            Vector3d orbVel = v.obt_velocity;
            Vector3d radialOut = (v.CoMD - v.mainBody.position).normalized;
            segment.orbitalFrameRotation =
                TrajectoryMath.ComputeOrbitalFrameRotation(v.transform.rotation, orbVel, radialOut);

            // Capture angular velocity if PersistentRotation is active and vessel is spinning
            if (hasPersistentRotation && v.rootPart != null && v.rootPart.rb != null)
            {
                Vector3 worldAngVel = v.angularVelocity;
                if (worldAngVel.magnitude > TrajectoryMath.SpinThreshold)
                {
                    segment.angularVelocity =
                        Quaternion.Inverse(v.transform.rotation) * worldAngVel;
                    ParsekLog.Verbose("Recorder",
                        $"Spinning vessel detected (|angVel|={worldAngVel.magnitude:F4}), recording angular velocity for spin-forward");
                }
            }

            return segment;
        }

        /// <summary>
        /// Emits terminal EngineShutdown/RCSStopped/RoboticMotionStopped events so ghost FX
        /// stops during orbit segments (#150), then clears all active engine/RCS/robotic
        /// tracking state so off-rails re-detection starts fresh.
        /// </summary>
        private void EmitTerminalEventsAndClearActiveState()
        {
            double railsUT = Planetarium.GetUniversalTime();
            var railsTerminalEvts = EmitTerminalEngineAndRcsEvents(
                activeEngineKeys, activeRcsKeys, activeRoboticKeys,
                lastRoboticPosition, railsUT, "Recorder");
            PartEvents.AddRange(railsTerminalEvts);
            if (railsTerminalEvts.Count > 0)
                ParsekLog.Info("Recorder",
                    $"Emitted {railsTerminalEvts.Count} terminal FX events at on-rails transition (UT={railsUT.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)})");

            // Clear active state so off-rails re-detection starts fresh
            activeEngineKeys.Clear();
            activeRcsKeys.Clear();
            activeRoboticKeys.Clear();
            lastThrottle.Clear();
            lastRcsThrottle.Clear();
            lastRoboticPosition.Clear();
            rcsActiveFrameCount.Clear();
            decoupledPartIds.Clear();
        }

        public void OnVesselGoOffRails(Vessel v)
        {
            if (!IsRecording) return;
            if (v != FlightGlobals.ActiveVessel)
            {
                ParsekLog.VerboseRateLimited("Recorder", "off-rails-other-vessel",
                    $"OnVesselGoOffRails: ignoring non-active vessel (pid={v?.persistentId})");
                return;
            }
            if (v.persistentId != RecordingVesselId) return;

            // Surface vessel went on rails without orbit segment — sample boundary point for continuity
            if (!isOnRails)
            {
                SamplePosition(v);
                RefreshFinalizationCache(v, "go_off_rails_surface", force: true);
                return;
            }

            // Finalize orbit segment
            currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
            OrbitSegments.Add(currentOrbitSegment);
            AddOrbitSegmentToCurrentTrackSection(currentOrbitSegment);
            isOnRails = false;

            // Reference frame transition: ORBITAL_CHECKPOINT -> ABSOLUTE
            double offRailsUT = Planetarium.GetUniversalTime();
            var currentEnv = environmentHysteresis != null
                ? environmentHysteresis.CurrentEnvironment
                : SegmentEnvironment.ExoBallistic;
            CloseCurrentTrackSection(offRailsUT);
            StartNewTrackSection(currentEnv, ReferenceFrame.Absolute, offRailsUT);
            ParsekLog.Info("Recorder",
                $"Reference frame transition: OrbitalCheckpoint -> Absolute at UT={offRailsUT.ToString("F2", CultureInfo.InvariantCulture)}");

            // Record a boundary TrajectoryPoint at current UT.
            // Note: KSP may not perfectly preserve SAS orientation across on/off-rails,
            // so the vessel's actual rotation here can differ slightly from the orbital-frame
            // rotation stored at the on-rails boundary. This causes a sub-degree rotation
            // discontinuity in the ghost at this transition — acceptable.
            SamplePosition(v);

            // Reseed atmosphere and altitude state for the current body
            ReseedAtmosphereState(v);
            ReseedAltitudeState(v);
            RefreshFinalizationCache(v, "go_off_rails", force: true);

            ParsekLog.Verbose("Recorder", $"Vessel went off rails — orbit segment closed " +
                $"(UT {currentOrbitSegment.startUT:F0}-{currentOrbitSegment.endUT:F0})");
        }

        public void OnVesselSOIChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> data)
        {
            if (!IsRecording || !isOnRails) return;
            if (data.host != FlightGlobals.ActiveVessel)
            {
                ParsekLog.VerboseRateLimited("Recorder", "soi-other-vessel",
                    $"OnVesselSOIChanged: ignoring non-active vessel (pid={data.host?.persistentId})");
                return;
            }
            if (data.host.persistentId != RecordingVesselId) return;

            // Close current orbit segment in old SOI
            currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
            OrbitSegments.Add(currentOrbitSegment);
            AddOrbitSegmentToCurrentTrackSection(currentOrbitSegment);

            // Start new orbit segment in new SOI
            var v = data.host;
            currentOrbitSegment = CreateOrbitSegmentFromVessel(v, Planetarium.GetUniversalTime());

            // Capture orbital-frame-relative rotation for new SOI
            Vector3d orbVel = v.obt_velocity;
            Vector3d radialOut = (v.CoMD - v.mainBody.position).normalized;
            currentOrbitSegment.orbitalFrameRotation =
                TrajectoryMath.ComputeOrbitalFrameRotation(v.transform.rotation, orbVel, radialOut);

            // Note: vessel is on rails during SOI change — rb is null, so angular velocity
            // is unavailable here. Spin data is only captured at the initial go-on-rails boundary.
            // A spinning vessel crossing an SOI boundary will use orbital-frame rotation (not spin-forward)
            // for the new segment. This is an acceptable v1 limitation.

            // Close current TrackSection and start a new one at the SOI boundary (#251).
            // Without this, a single TrackSection spans both SOIs and the optimizer cannot
            // split at the SOI boundary (it only splits at section boundaries).
            double soiUT = Planetarium.GetUniversalTime();
            CloseCurrentTrackSection(soiUT);
            SegmentEnvironment currentEnv = environmentHysteresis != null
                ? environmentHysteresis.CurrentEnvironment
                : SegmentEnvironment.ExoBallistic;
            StartNewTrackSection(currentEnv, ReferenceFrame.Absolute, soiUT);

            // Reseed atmosphere and altitude state for the new body
            ReseedAtmosphereState(v);
            ReseedAltitudeState(v);

            // Flag for ParsekFlight to trigger chain split in Update()
            SoiChangePending = true;
            SoiChangeFromBody = data.from.name;
            RefreshFinalizationCache(v, "soi_change", force: true);

            ParsekLog.Verbose("Recorder",
                $"SOI changed {data.from.name} → {data.to.name} — new segment ofrRot={currentOrbitSegment.orbitalFrameRotation}, " +
                $"angVel={currentOrbitSegment.angularVelocity}");
        }

        public void OnVesselWillDestroy(Vessel v)
        {
            if (!IsRecording) return;
            if (FlightGlobals.ActiveVessel == null)
            {
                ParsekLog.Verbose("Recorder", "OnVesselWillDestroy: no active vessel — skipping");
                return;
            }
            if (v != FlightGlobals.ActiveVessel) return;
            if (v.persistentId != RecordingVesselId) return;

            // Finalize in-progress orbit segment if on rails
            if (isOnRails)
            {
                currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
                OrbitSegments.Add(currentOrbitSegment);
                AddOrbitSegmentToCurrentTrackSection(currentOrbitSegment);
                isOnRails = false;
            }

            // Capture final trajectory point at destruction — the last adaptively-sampled
            // point can be meters above/away from the actual impact location.
            SamplePosition(v);

            VesselDestroyedDuringRecording = true;
            RefreshBackupSnapshot(v, "destroy_event", force: true);
            RefreshFinalizationCache(v, "destroy_event", force: true);
            ParsekLog.Warn("Recorder",
                "Active vessel destroyed during recording — captured final position at " +
                $"lat={v.latitude.ToString("F4", CultureInfo.InvariantCulture)} " +
                $"lon={v.longitude.ToString("F4", CultureInfo.InvariantCulture)} " +
                $"alt={v.altitude.ToString("F1", CultureInfo.InvariantCulture)}m");
        }

        /// <summary>
        /// Force-stop recording during scene change (no boundary point needed).
        /// </summary>
        public void ForceStop()
        {
            // Intentionally does not set CaptureAtStop. Forced stops happen during
            // scene transitions where vessel state may already be changing/unreliable;
            // ParsekFlight falls back to scene-change snapshot capture in that path.
            FinalizeRecordingState(
                emitTerminalEvents: false,
                clearRelativeMode: false,
                sampleBoundaryOnRails: false,
                logTag: "force stop");

            double duration = Recording.Count > 0
                ? Recording[Recording.Count - 1].ut - Recording[0].ut
                : 0;

            // Keep stop-line contract identical to normal StopRecording so live
            // KSP.log validation does not require a manual in-flight stop.
            ParsekLog.Info("Recorder", $"Recording stopped. {Recording.Count} points, {OrbitSegments.Count} orbit segments over {duration:F1}s");
            ParsekLog.Info("Recorder", "Auto-stopped recording due to scene change");
        }

        /// <summary>
        /// Transitions the current recording to background mode (tree vessel switch).
        /// Captures an orbit segment for the background phase. IsRecording stays true
        /// but the Harmony patch is disconnected so OnPhysicsFrame won't fire.
        /// </summary>
        public void TransitionToBackground()
        {
            if (!IsRecording) return;

            Vessel v = FindVesselByPid(RecordingVesselId);
            if (v == null)
            {
                string recordingId = ActiveTree?.ActiveRecordingId ?? FinalizationCache?.RecordingId;
                string vesselName = null;
                Recording treeRec;
                if (ActiveTree != null
                    && ActiveTree.Recordings != null
                    && !string.IsNullOrEmpty(recordingId)
                    && ActiveTree.Recordings.TryGetValue(recordingId, out treeRec))
                {
                    vesselName = treeRec.VesselName;
                }

                ParsekLog.Warn("Recorder",
                    FormatTransitionToBackgroundMissingVesselWarning(
                        RecordingVesselId,
                        recordingId,
                        vesselName,
                        Recording.Count,
                        OrbitSegments.Count,
                        TrackSections.Count,
                        FinalizationCache));
            }

            // Sample a boundary point if vessel is still accessible
            if (v != null)
                SamplePosition(v);

            // Finalize any in-progress orbit segment (if already on rails during switch)
            if (isOnRails)
            {
                currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
                OrbitSegments.Add(currentOrbitSegment);
                AddOrbitSegmentToCurrentTrackSection(currentOrbitSegment);
                isOnRails = false;
            }

            // Close current TrackSection before backgrounding
            CloseCurrentTrackSection(Planetarium.GetUniversalTime());

            // Start a new orbit segment for the background phase.
            // Layer 1: Skip for surface vessels — their Keplerian orbit is sub-surface.
            bool isSurfaceVessel = v != null &&
                (v.situation == Vessel.Situations.LANDED ||
                 v.situation == Vessel.Situations.SPLASHED ||
                 v.situation == Vessel.Situations.PRELAUNCH);
            if (v != null && v.orbit != null && !isSurfaceVessel)
            {
                currentOrbitSegment = CreateOrbitSegmentFromVessel(v, Planetarium.GetUniversalTime());
                isOnRails = true;
            }
            else if (isSurfaceVessel)
            {
                ParsekLog.Info("Recorder",
                    $"Background transition: surface vessel (sit={v.situation}), skipping orbit segment");
            }

            if (v != null)
                RefreshFinalizationCache(v, "transition_to_background", force: true);

            // Disconnect from Harmony patch (stop physics-frame sampling)
            Patches.PhysicsFramePatch.ActiveRecorder = null;
            UnsubscribePartEvents();

            // NOTE: IsRecording stays TRUE. The recording is still conceptually active,
            // just in background mode. The Harmony patch is disconnected so OnPhysicsFrame
            // won't fire.
            ParsekLog.Info("Recorder", $"Transitioned to background (pid={RecordingVesselId}, " +
                $"points={Recording.Count}, orbitSegments={OrbitSegments.Count})");
        }

        internal static string FormatTransitionToBackgroundMissingVesselWarning(
            uint vesselPid,
            string recordingId,
            string vesselName,
            int pointCount,
            int orbitSegmentCount,
            int trackSectionCount,
            RecordingFinalizationCache finalizationCache)
        {
            string cacheState = finalizationCache == null
                ? "none"
                : string.Format(CultureInfo.InvariantCulture,
                    "status={0} owner={1} terminal={2} cachedUT={3}",
                    finalizationCache.Status,
                    finalizationCache.Owner,
                    finalizationCache.TerminalState?.ToString() ?? "(null)",
                    double.IsNaN(finalizationCache.CachedAtUT)
                        ? "NaN"
                        : finalizationCache.CachedAtUT.ToString("F1", CultureInfo.InvariantCulture));

            return string.Format(CultureInfo.InvariantCulture,
                "TransitionToBackground: live vessel missing pid={0} recId={1} vessel='{2}' points={3} orbitSegments={4} trackSections={5} finalizerCache={6}",
                vesselPid,
                string.IsNullOrEmpty(recordingId) ? "(null)" : recordingId,
                string.IsNullOrEmpty(vesselName) ? "(unknown)" : vesselName,
                pointCount,
                orbitSegmentCount,
                trackSectionCount,
                cacheState);
        }

        /// <summary>
        /// Closes any open orbit segment (isOnRails=true) by setting endUT to current UT
        /// and adding it to OrbitSegments. Called before flushing recorder data to a tree
        /// recording so that background orbit segments are not lost.
        /// </summary>
        public void FinalizeOpenOrbitSegment()
        {
            if (isOnRails)
            {
                currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
                OrbitSegments.Add(currentOrbitSegment);
                AddOrbitSegmentToCurrentTrackSection(currentOrbitSegment);
                isOnRails = false;
            }
        }

        internal static Vessel FindVesselByPid(uint pid)
        {
            // The FlightGlobals.Vessels accessor triggers FlightGlobals' static
            // initializer (Quaternion.Euler ECall), which throws in unit-test
            // environments where Unity native methods are unavailable. Wrap the
            // first access so any future test-only caller that drives a recorder
            // path through this method gets a clean null instead of a
            // TypeInitializationException. Current unit tests of
            // FinalizeIndividualRecording (Bug278FinalizeLimboTests) all pass
            // pid=0, which short-circuits before this method is reached, so the
            // catch is currently unreachable from the test suite — kept as
            // defensive scaffolding for future test paths. Production behavior
            // is unchanged.
            var vessels = TryGetFlightGlobalsVessels();
            if (vessels == null) return null;
            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel vessel = vessels[i];
                if (vessel != null && vessel.persistentId == pid)
                {
                    if (GhostMapPresence.IsGhostMapVessel(vessel.persistentId)) return null;
                    return vessel;
                }
            }
            return null;
        }

        /// <summary>
        /// Defensive accessor for <c>FlightGlobals.Vessels</c> that returns null
        /// when the static initializer fails (unit-test environments where Unity
        /// native code is not available). Production callers see the same value
        /// as the underlying property.
        /// </summary>
        private static List<Vessel> TryGetFlightGlobalsVessels()
        {
            try { return FlightGlobals.Vessels; }
            catch (TypeInitializationException) { return null; }
        }

        /// <summary>
        /// Used by <see cref="RestoreTrackSectionAfterFalseAlarm"/> to decide
        /// whether the scene is ready enough to validate the resumed
        /// Relative-mode anchor. Returns null when the scene's vessel list is
        /// unqueryable (xUnit, pre-FlightReady), in which case the resume
        /// path skips the safety check and trusts the saved anchor metadata.
        /// </summary>
        private static List<Vessel> TryGetFlightGlobalsVesselsForResumeValidation()
        {
            if (resumeValidationVesselsOverrideForTesting != null)
                return resumeValidationVesselsOverrideForTesting();
            return TryGetFlightGlobalsVessels();
        }

        // Test-only seam: lets xUnit drive the stale-anchor downgrade branch
        // of RestoreTrackSectionAfterFalseAlarm without a live KSP scene. In
        // production this stays null and the helper falls back to
        // TryGetFlightGlobalsVessels(), which returns null in xUnit and so
        // skips the safety check entirely. Returning a non-null (possibly
        // empty) list from the override forces the validation path to run
        // and treat the resume anchor as unloaded → downgrade to Absolute.
        internal static System.Func<List<Vessel>> resumeValidationVesselsOverrideForTesting;

        internal static void SetResumeValidationVesselsOverrideForTesting(
            System.Func<List<Vessel>> @override)
        {
            resumeValidationVesselsOverrideForTesting = @override;
        }

        internal static void ResetResumeValidationVesselsOverrideForTesting()
        {
            resumeValidationVesselsOverrideForTesting = null;
        }

        private void RefreshBackupSnapshot(Vessel vessel, string reason, bool force = false)
        {
            if (vessel == null) return;

            double ut = Planetarium.GetUniversalTime();
            if (!ShouldRefreshSnapshot(lastSnapshotRefreshUT, ut, snapshotRefreshIntervalUT, force))
                return;

            float startMs = Time.realtimeSinceStartup * 1000f;
            var snapshot = VesselSpawner.TryBackupSnapshot(vessel);
            float elapsedMs = Time.realtimeSinceStartup * 1000f - startMs;

            if (snapshot != null)
            {
                lastGoodVesselSnapshot = snapshot;
                lastSnapshotRefreshUT = ut;
            }
            else
            {
                ParsekLog.Warn("Recorder", $"Snapshot backup failed ({reason}): pid={vessel.persistentId}, " +
                    $"loaded={vessel.loaded}, packed={vessel.packed} — using previous snapshot");
            }

            if (elapsedMs >= snapshotPerfLogThresholdMs)
            {
                DiagnosticsState.health.snapshotRefreshSpikes++;
                ParsekLog.Verbose("Recorder",
                    $"Snapshot backup cost ({reason}): {elapsedMs:F1}ms " +
                    $"pid={vessel.persistentId}, points={Recording.Count}");
            }
        }

        internal bool RefreshFinalizationCache(Vessel vessel, string reason, bool force = false)
        {
            if (vessel == null)
                return false;

            double ut = Planetarium.GetUniversalTime();
            bool hasMeaningfulThrust = RecordingFinalizationCacheProducer.HasMeaningfulThrust(
                activeEngineKeys,
                lastThrottle,
                activeRcsKeys,
                lastRcsThrottle);
            bool inAtmosphere = RecordingFinalizationCacheProducer.IsInAtmosphere(vessel);
            string currentDigest = RecordingFinalizationCacheProducer.BuildVesselDigest(
                vessel,
                hasMeaningfulThrust);
            bool requiresPeriodicRefresh = hasMeaningfulThrust
                || inAtmosphere
                || RecordingFinalizationCacheProducer.IsSurfaceTerminalSituation(vessel.situation)
                || FinalizationCache == null
                || FinalizationCache.Status == FinalizationCacheStatus.Failed;

            if (!RecordingFinalizationCacheProducer.ShouldRefresh(
                    lastFinalizationCacheRefreshUT,
                    ut,
                    finalizationCacheRefreshIntervalUT,
                    force,
                    FinalizationCache?.LastObservedOrbitDigest,
                    currentDigest,
                    requiresPeriodicRefresh))
            {
                RecordingFinalizationCacheProducer.TryTouchObservedTerminalCache(
                    FinalizationCache,
                    ut,
                    reason,
                    currentDigest);
                return false;
            }

            RecordingFinalizationCache previous = FinalizationCache;
            Recording context = BuildFinalizationCacheContext(ut);
            RecordingFinalizationCache refreshed;
            bool success = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                context,
                vessel,
                ut,
                FinalizationCacheOwner.ActiveRecorder,
                reason,
                hasMeaningfulThrust,
                out refreshed);

            if (!success
                && RecordingFinalizationCacheProducer.IsAlreadyClassifiedDestroyedSkip(refreshed))
            {
                FinalizationCache = refreshed;
                lastFinalizationCacheRefreshUT = ut;
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
                FinalizationCache = previous;
                lastFinalizationCacheRefreshUT = ut;
                return false;
            }
            FinalizationCache = refreshed;
            lastFinalizationCacheRefreshUT = ut;
            return success;
        }

        private Recording BuildFinalizationCacheContext(double currentUT)
        {
            Recording context = null;
            if (ActiveTree != null
                && !string.IsNullOrEmpty(ActiveTree.ActiveRecordingId)
                && ActiveTree.Recordings != null)
            {
                ActiveTree.Recordings.TryGetValue(ActiveTree.ActiveRecordingId, out context);
            }

            if (context != null)
                return context;

            return new Recording
            {
                RecordingId = FinalizationCache?.RecordingId,
                VesselPersistentId = RecordingVesselId,
                ExplicitEndUT = !double.IsNaN(LastRecordedUT) ? LastRecordedUT : currentUT
            };
        }

        internal RecordingFinalizationCache GetFinalizationCacheForRecording(Recording recording)
        {
            if (FinalizationCache == null || recording == null)
                return FinalizationCache;

            if (!string.IsNullOrEmpty(FinalizationCache.RecordingId))
                return FinalizationCache;

            FinalizationCache.RecordingId = recording.RecordingId;
            if (FinalizationCache.VesselPersistentId == 0)
            {
                if (recording.VesselPersistentId != 0)
                {
                    FinalizationCache.VesselPersistentId = recording.VesselPersistentId;
                }
                else
                {
                    FinalizationCache.VesselPersistentId = RecordingVesselId;
                    if (RecordingVesselId != 0)
                    {
                        ParsekLog.Verbose("FinalizerCache",
                            $"Backfilled active cache vessel pid from recorder state: rec={recording.RecordingId ?? "(null)"} " +
                            $"pid={RecordingVesselId}");
                    }
                }
            }
            return FinalizationCache;
        }

        internal static bool ShouldRefreshSnapshot(
            double lastSnapshotRefreshUT, double currentUT, double intervalUT, bool force)
        {
            if (force) return true;
            if (lastSnapshotRefreshUT == double.MinValue) return true;
            return currentUT - lastSnapshotRefreshUT >= intervalUT;
        }

        /// <summary>
        /// Bootstraps DiagnosticsState.avgBytesPerPoint from committed recordings' .prec files.
        /// Called once at recording start. If no committed recordings exist, keeps the default (85).
        /// </summary>
        private static void BootstrapAvgBytesPerPoint()
        {
            try
            {
                // [ERS-exempt] reason: samples on-disk .prec file sizes for a
                // bytes-per-point heuristic; includes NotCommitted / superseded
                // recordings so the average reflects real storage, not visible
                // storage (admin/debug surface per design §3.4).
                var committed = RecordingStore.CommittedRecordings;
                if (committed == null || committed.Count == 0)
                {
                    ParsekLog.Verbose("Diagnostics", "avgBytesPerPoint bootstrap: no committed recordings, using default 85");
                    return;
                }

                long totalBytes = 0;
                int totalPoints = 0;
                int filesChecked = 0;

                for (int i = 0; i < committed.Count; i++)
                {
                    var rec = committed[i];
                    if (rec == null || rec.Points == null || rec.Points.Count == 0)
                        continue;

                    string relativePath = RecordingPaths.BuildTrajectoryRelativePath(rec.RecordingId);
                    string fullPath = RecordingPaths.ResolveSaveScopedPath(relativePath);
                    if (string.IsNullOrEmpty(fullPath))
                        continue;

                    try
                    {
                        var fi = new FileInfo(fullPath);
                        if (fi.Exists)
                        {
                            totalBytes += fi.Length;
                            totalPoints += rec.Points.Count;
                            filesChecked++;
                        }
                    }
                    catch
                    {
                        // Skip files that can't be stat'd
                    }
                }

                if (totalPoints > 0)
                {
                    DiagnosticsState.avgBytesPerPoint = (double)totalBytes / totalPoints;
                    ParsekLog.Verbose("Diagnostics",
                        string.Format(CultureInfo.InvariantCulture,
                            "avgBytesPerPoint bootstrapped: {0:F1} bytes/pt from {1} files ({2} bytes, {3} points)",
                            DiagnosticsState.avgBytesPerPoint, filesChecked, totalBytes, totalPoints));
                }
                else
                {
                    ParsekLog.Verbose("Diagnostics", "avgBytesPerPoint bootstrap: no .prec files found, using default 85");
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose("Diagnostics", $"avgBytesPerPoint bootstrap failed: {ex.Message}");
            }
        }

        internal static VesselSwitchDecision DecideOnVesselSwitch(
            uint recordingVesselId, uint currentVesselId, bool currentIsEva,
            bool recordingStartedAsEva, uint undockSiblingPid = 0,
            RecordingTree activeTree = null)
        {
            if (currentVesselId == recordingVesselId)
                return VesselSwitchDecision.None;
            // Player switched to undocked sibling vessel
            if (undockSiblingPid != 0 && currentVesselId == undockSiblingPid)
                return VesselSwitchDecision.UndockSwitch;
            // Avoid mixed-mode tracks (ship trajectory followed by EVA walking).
            // Continue-on-EVA is only valid for recordings that started as EVA.
            if (currentIsEva && recordingStartedAsEva)
                return VesselSwitchDecision.ContinueOnEva;
            // EVA kerbal boarded a vessel — potential chain continuation.
            // ParsekFlight checks activeChainId before actually continuing the chain;
            // if not in a chain, this is treated as a normal stop.
            if (!currentIsEva && recordingStartedAsEva)
                return VesselSwitchDecision.ChainToVessel;

            // Tree mode: switching to a vessel already tracked in the tree
            if (activeTree != null && activeTree.BackgroundMap.ContainsKey(currentVesselId))
                return VesselSwitchDecision.PromoteFromBackground;
            // Tree mode: switching to any other vessel (background the current recording)
            if (activeTree != null)
                return VesselSwitchDecision.TransitionToBackground;

            // Safety fallback: with always-tree mode, activeTree is never null during recording.
            // If somehow reached, default to background transition rather than stopping.
            return VesselSwitchDecision.TransitionToBackground;
        }
    }
}
