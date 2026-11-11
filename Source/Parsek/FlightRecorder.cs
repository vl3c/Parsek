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
    public partial class FlightRecorder
    {
        internal static Func<double> QuickloadResumeUTProviderForTesting;
        internal enum VesselSwitchDecision
        {
            None,
            ContinueOnEva,
            ChainToVessel,  // EVA recording -> boarded a vessel
            TransitionToBackground,   // active recording -> background (orbit segment)
            PromoteFromBackground     // background recording -> active (resume physics sampling)
        }

        internal struct ReFlyPostLoadSettleDecision
        {
            public bool Hold;
            public bool Clear;
            public int ConsecutiveUnpackedFrames;
            public string Reason;
        }

        // Recording output
        public List<TrajectoryPoint> Recording { get; } = new List<TrajectoryPoint>();
        public List<OrbitSegment> OrbitSegments { get; } = new List<OrbitSegment>();
        public List<PartEvent> PartEvents { get; } = new List<PartEvent>();
        public List<FlagEvent> FlagEvents { get; } = new List<FlagEvent>();
        public List<SegmentEvent> SegmentEvents { get; } = new List<SegmentEvent>();
        public List<TrackSection> TrackSections { get; } = new List<TrackSection>();

        // Two unpacked frames let Krakensbane/frame velocity settle after pack->unpack;
        // 0.1s covers scene-load physics tick variability across local framerates.
        internal const int ReFlyPostLoadSettleRequiredUnpackedFrames = 2;
        internal const float ReFlyPostLoadSettleMinLevelSeconds = 0.1f;
        internal const int StabilityExtensionFramesAfterShift = 2;
        // TODO: retune from Re-Fly settle/FloatingOrigin evidence after playtest.
        internal const int StabilitySettleClearHoldFrames = 60;
        private const double SectionBoundaryUtEpsilon = 1e-6;
        internal static Func<int> FrameCountProviderForTesting;
        internal static Func<float> TimeSinceLevelLoadProviderForTesting;
        private bool reFlyPostLoadSettleActive;
        private int reFlyPostLoadSettleUnpackedFrames;
        private float reFlyPostLoadSettleStartLevelTime;
        private string reFlyPostLoadSettleSessionId;
        private string reFlyPostLoadSettleRecordingId;

        // Environment tracking
        private EnvironmentHysteresis environmentHysteresis;
        private TrackSection currentTrackSection;
        private bool trackSectionActive;
        private bool pendingRestoreEnvironmentResync;
        private SegmentEnvironment restoreEnvironmentResyncTarget;

        // Phase 7 (design doc §13, §18 Phase 7): per-section diagnostics for the
        // surface-clearance summary line emitted at section close. Reset
        // when a new section opens so each closed section emits its own line.
        // Field names retain the original SurfaceMobile wording, but the gate
        // now covers both SurfaceMobile and SurfaceStationary sections.
        private int surfaceMobileSamplesThisSection;
        private double surfaceMobileMinClearanceThisSection = double.NaN;
        private double surfaceMobileMaxClearanceThisSection = double.NaN;
        private double surfaceMobileClearanceSumThisSection;

        // Per-frame rails flags for the current section, index-aligned with
        // currentTrackSection.frames (flag[i] == true means frame i was sampled
        // on rails or under rails warp). Used at section close to classify each
        // large gap: a gap touching a rails sample is a structurally-expected
        // jump (Verbose); any other large gap, at 1x or under physics warp, is a
        // dropped-sample signal (WARN). Reset per section in StartNewTrackSection,
        // trimmed in lockstep with frames in TrimRecordingToUT.
        private readonly List<bool> sectionFrameWarpFlags = new List<bool>();

        // The current section's frames folded into contiguous warp runs AT SAMPLE TIME
        // (SectionWarpRuns.Append: one entry per 1x / physics / rails stretch, never one
        // per frame). Diagnostic only and never serialized: it feeds the `warpRuns=`
        // token of the section-close line, which the harness sampling verifier reads to
        // classify each recorded gap. Cleared when a section opens and when it closes.
        private readonly List<SectionWarpRuns.Run> sectionWarpRuns = new List<SectionWarpRuns.Run>();

        // Anchor detection for RELATIVE frame (Phase 3a)
        private bool isRelativeMode;
        private uint currentAnchorPid;
        private string currentAnchorRecordingId;
        private RecordingAnchorCandidate currentAnchorCandidate;
        private bool hasCurrentAnchorCandidate;
        private double currentAnchorDistanceMeters = double.NaN;
        private HashSet<uint> treeVesselPids;
        private readonly HashSet<uint> highFidelityProximityVesselPids = new HashSet<uint>();
        private readonly List<(uint pid, Vector3d position)> vesselInfoBuffer = new List<(uint, Vector3d)>();
        private readonly List<RecordingAnchorCandidate> recordingAnchorCandidateBuffer =
            new List<RecordingAnchorCandidate>();
        private readonly HashSet<string> recordedAnchorResolverVisited =
            new HashSet<string>(StringComparer.Ordinal);

        internal Func<RecordingTree, string, uint, double, IEnumerable<RecordingAnchorCandidate>>
            ExternalRecordingAnchorCandidatesProvider { get; set; }

        internal delegate bool TryResolveRecordedAnchorPoseOverride(
            string anchorRecordingId,
            double ut,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure);
        internal static TryResolveRecordedAnchorPoseOverride RecordedAnchorPoseOverrideForTesting;

        // Part event tracking
        private HashSet<uint> decoupledPartIds = new HashSet<uint>();
        // Structural joint-break children whose joint broke under force (breakForce > 0),
        // and parts that came off through a pre-attached docking port's Undock. Read by
        // ParsekFlight's deferred split check to pick the split child's cause.
        private readonly HashSet<uint> forceBrokenJointChildPartIds = new HashSet<uint>();
        private readonly HashSet<uint> preAttachedUndockPartIds = new HashSet<uint>();
        // 0=stowed/active, 1=semi, 2=deployed, 3=cut — see ClassifyParachuteState. Stowed is the
        // implicit default and removes the entry, so this only holds non-build-time-pose chutes.
        private Dictionary<uint, int> parachuteStates = new Dictionary<uint, int>();
        private HashSet<uint> jettisonedShrouds = new HashSet<uint>();
        private Dictionary<ulong, string> jettisonNameRawCache = new Dictionary<ulong, string>();
        private Dictionary<ulong, string[]> parsedJettisonNamesCache = new Dictionary<ulong, string[]>();
        private HashSet<uint> extendedDeployables = new HashSet<uint>();
        /// <summary>
        /// S6: pids whose ModuleDeployablePart is currently BROKEN. Disjoint from
        /// <see cref="extendedDeployables"/> by construction — CheckDeployableBrokenTransition
        /// clears the extended flag when a panel breaks, so no pid is ever in both.
        /// </summary>
        private HashSet<uint> brokenDeployables = new HashSet<uint>();
        /// <summary>
        /// S7: pids with at least one running BaseConverter. Per-PART, unlike the vessel-scoped
        /// harvest window - the running animation belongs to one part's ModuleAnimationGroup.
        /// </summary>
        private HashSet<uint> activeConverterParts = new HashSet<uint>();
        /// <summary>Pids with at least one broken ModuleWheelDamage (stock <c>isDamaged</c>).</summary>
        private HashSet<uint> damagedWheelParts = new HashSet<uint>();
        /// <summary>S4: pids whose KerbalEVA has its jetpack extended.</summary>
        private HashSet<uint> jetpackDeployedParts = new HashSet<uint>();
        /// <summary>S4: pids whose KerbalEVA is in a SUSTAINED (post-debounce) thrust burst.</summary>
        private HashSet<uint> thrustingJetpackParts = new HashSet<uint>();
        /// <summary>S4: pids whose KerbalEVA is ragdolled.</summary>
        private HashSet<uint> ragdollParts = new HashSet<uint>();
        /// <summary>
        /// S4: consecutive frames each pid has read JetpackIsThrusting == true. Recorder-local
        /// bookkeeping for the debounce, NOT part state, so it deliberately does not join
        /// PartTrackingSets - there is no such thing as "seeding" a frame count, and a rails-span
        /// diff of one would be meaningless.
        /// </summary>
        private Dictionary<uint, int> evaThrustFrameCounts = new Dictionary<uint, int>();
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

        /// <summary>
        /// Minimum change in recorded engine power that emits an <see cref="PartEventType.EngineThrottle"/>.
        /// Shared by the per-frame transition check and the background rails-span diff so both
        /// halves quantise the same way.
        /// </summary>
        internal const float EngineThrottleDeadband = 0.05f;

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

        /// <summary>
        /// Last observed throttle for engine / RCS modules whose part LEFT the recorded vessel,
        /// moved here by <see cref="OnVesselWasModified"/>'s departed-key prune.
        ///
        /// <para>
        /// Read by exactly one consumer: <c>InheritedEngineState.FromRecorder</c>, the #298
        /// parent-to-child breakup snapshot. That snapshot is taken from
        /// <c>ParsekFlight.ProcessBreakupEvent</c>, which runs a whole crash-coalescer window AFTER
        /// <c>onVesselWasModified</c> has already fired for the split — so without this carry-over a
        /// prune would delete the "the booster was at full throttle when it came off" fact before
        /// the child debris recording could inherit it. Nothing polls these, and the terminal emit
        /// deliberately does not see them: that is the whole point of the prune.
        /// </para>
        /// <para>
        /// Lifetime matches <see cref="ActiveEngineKeys"/>: cleared only by
        /// <c>ResetPartEventTrackingState</c> (a new recording), never by
        /// <c>FinalizeRecordingState</c>, so a chain-boundary stop keeps it. Bounded by the number of
        /// engine/RCS modules that leave the vessel during one recording, and an entry is dropped
        /// the moment its pid comes back.
        /// </para>
        /// </summary>
        private readonly Dictionary<ulong, float> departedEngineThrottles = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, float> departedRcsThrottles = new Dictionary<ulong, float>();
        internal Dictionary<ulong, float> DepartedEngineThrottles => departedEngineThrottles;
        internal Dictionary<ulong, float> DepartedRcsThrottles => departedRcsThrottles;

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
        /// <summary>One-shot log guard for the cached-module ownership guard (M5): a cached engine /
        /// RCS / robotic entry whose part has left the recorded vessel is reported once per key,
        /// not once per physics frame.</summary>
        private HashSet<ulong> loggedForeignCachedModuleKeys = new HashSet<ulong>();
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

        public uint RecordingVesselId { get; private set; }
        public bool RecordingStartedAsEva { get; private set; }
        public bool VesselDestroyedDuringRecording { get; set; }
        public bool ChainToVesselPending { get; internal set; }

        // Tree mode: set by ParsekFlight when a recording tree is active
        public RecordingTree ActiveTree { get; set; }
        public bool TransitionToBackgroundPending { get; internal set; }

        // Joint break split detection: set by OnPartJointBreak, consumed by ParsekFlight.Update()
        public bool HasPendingJointBreakCheck { get; private set; }
        public double PendingJointBreakUT { get; private set; } = double.NaN;
        // Bounded by structural-break part count. Survives chain-boundary stops so
        // ParsekFlight can hand the seed to the new child, then clears on normal
        // stop/force-stop, new recording starts, or false-alarm resumes.
        private readonly Dictionary<uint, TrajectoryPoint> pendingJointChildPartOriginSeeds =
            new Dictionary<uint, TrajectoryPoint>();

        // Exact terminal events appended by the most recent FinalizeRecordingState call with
        // emitTerminalEvents=true. ResumeAfterFalseAlarm uses this list to remove the orphaned
        // terminals by content-matching rather than index arithmetic. Index-based removal
        // (see #255/#263/#281) was brittle across unstable sort paths (flush/merge) and any
        // code path that appends events between stop and resume — it caused spurious terminal
        // Shutdowns to survive into committed recordings (#287).
        internal List<PartEvent> lastEmittedTerminalEvents;

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
            // The decision is captured when OnLoad arms the pending context so
            // recorder resume cannot fall back to tree-wide if the scenario
            // singleton is temporarily unavailable.
            var trimScope = ParsekScenario.GetPendingQuickloadTrimScope(
                ActiveTree.Id, out string trimScopeReason);
            // The arming load's kind is logged; the quicksave facts keep its committed history whole.
            LoadKind? loadKind = ParsekScenario.GetPendingQuickloadLoadKind(ActiveTree.Id);
            var quicksaveFacts = ParsekScenario.GetPendingQuickloadQuicksaveFacts(ActiveTree.Id);
            bool treeTrimmed = ParsekScenario.TrimForQuickloadResume(
                ActiveTree, activeRec, resumeUT, trimScope, quicksaveFacts);

            bool hasTailEnv = TryGetTailTrackSectionEnvironment(activeRec, out SegmentEnvironment tailEnv);
            if (hasTailEnv)
                ArmRestoreEnvironmentResync(tailEnv, "quickload-resume tail environment");

            ParsekScenario.ClearPendingQuickloadResumeContext();
            ParsekLog.Info("Recorder",
                $"Quickload resume prep: activeRec='{activeRec.RecordingId}' " +
                $"cutoffUT={resumeUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"preTrimEndUT={preTrimEndUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"trimScope={trimScope} ({trimScopeReason}) " +
                $"loadKind={(loadKind.HasValue ? loadKind.Value.ToString() : "none")} " +
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

        private static float GetTimeSinceLevelLoad()
        {
            return TimeSinceLevelLoadProviderForTesting != null
                ? TimeSinceLevelLoadProviderForTesting()
                : Time.timeSinceLevelLoad;
        }

        internal static int GetFrameCount()
        {
            return FrameCountProviderForTesting != null
                ? FrameCountProviderForTesting()
                : GetUnityFrameCount();
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int GetUnityFrameCount()
        {
            return Time.frameCount;
        }

        internal static ReFlyPostLoadSettleDecision EvaluateReFlyPostLoadSettle(
            bool active,
            bool vesselPacked,
            float elapsedLevelSeconds,
            int consecutiveUnpackedFrames,
            int requiredUnpackedFrames = ReFlyPostLoadSettleRequiredUnpackedFrames,
            float minElapsedLevelSeconds = ReFlyPostLoadSettleMinLevelSeconds)
        {
            if (!active)
            {
                return new ReFlyPostLoadSettleDecision
                {
                    Hold = false,
                    Clear = false,
                    ConsecutiveUnpackedFrames = 0,
                    Reason = "inactive"
                };
            }

            if (vesselPacked)
            {
                return new ReFlyPostLoadSettleDecision
                {
                    Hold = true,
                    Clear = false,
                    ConsecutiveUnpackedFrames = 0,
                    Reason = "packed"
                };
            }

            int unpackedFrames = Math.Max(0, consecutiveUnpackedFrames) + 1;
            if (unpackedFrames < requiredUnpackedFrames)
            {
                return new ReFlyPostLoadSettleDecision
                {
                    Hold = true,
                    Clear = false,
                    ConsecutiveUnpackedFrames = unpackedFrames,
                    Reason = "unpacked-frame-warmup"
                };
            }

            if (elapsedLevelSeconds < minElapsedLevelSeconds)
            {
                return new ReFlyPostLoadSettleDecision
                {
                    Hold = true,
                    Clear = false,
                    ConsecutiveUnpackedFrames = unpackedFrames,
                    Reason = "level-time-warmup"
                };
            }

            return new ReFlyPostLoadSettleDecision
            {
                Hold = false,
                Clear = true,
                ConsecutiveUnpackedFrames = unpackedFrames,
                Reason = "settled"
            };
        }

        internal static bool ShouldArmReFlyPostLoadSettle(
            bool isPromotion,
            bool vesselInAtmosphere,
            string activeTreeId,
            string activeRecordingId,
            ReFlySessionMarker marker,
            out string reason)
        {
            if (!isPromotion)
            {
                reason = "not-promotion";
                return false;
            }
            if (!vesselInAtmosphere)
            {
                reason = "not-atmospheric";
                return false;
            }
            if (marker == null)
            {
                reason = "marker-missing";
                return false;
            }
            if (string.IsNullOrEmpty(marker.SessionId))
            {
                reason = "marker-session-missing";
                return false;
            }
            if (string.IsNullOrEmpty(marker.TreeId) || string.IsNullOrEmpty(activeTreeId))
            {
                reason = "tree-id-missing";
                return false;
            }
            if (!string.Equals(marker.TreeId, activeTreeId, StringComparison.Ordinal))
            {
                reason = "tree-mismatch";
                return false;
            }
            if (string.IsNullOrEmpty(marker.ActiveReFlyRecordingId) || string.IsNullOrEmpty(activeRecordingId))
            {
                reason = "active-recording-missing";
                return false;
            }
            if (!string.Equals(marker.ActiveReFlyRecordingId, activeRecordingId, StringComparison.Ordinal))
            {
                reason = "active-recording-mismatch";
                return false;
            }

            reason = "armed";
            return true;
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
        /// <summary>
        /// Set by <c>ParsekFlight.StartRecording</c> when the start was triggered by the
        /// active vessel leaving PRELAUNCH (the auto-record launch transition). By then the
        /// vessel's situation is already FLYING / LANDED, so this is how
        /// <see cref="ShouldCaptureLaunchSite"/> still knows the start is a launch.
        /// </summary>
        internal bool StartedFromPrelaunchTransition { get; set; }
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
        internal const double HighFidelityProximityRangeMeters = 250.0;
        internal const double ReFlyTreeFullFidelityProximityRangeMeters = 250.0;
        internal const double ReFlyTreeHalfFidelityProximityRangeMeters = 500.0;
        // Floor for the sparse-sampling large-gap threshold; only used as a
        // backstop for a degenerate / unset maxSampleInterval (see
        // ResolveSparseGapWarningThreshold). The live threshold is derived from
        // the configured sampler backstop, not this constant.
        internal const double SparseSectionGapWarningThresholdSeconds = 0.50;
        // Multiplier applied to the configured maxSampleInterval to get the
        // "sparse sampling" WARN threshold (BUG-D). 1.5x leaves a 50% margin above
        // the design backstop for physics-frame quantization + frame-budget jitter
        // so an on-schedule coast gap (~maxInterval) never warns, while a gap well
        // past the backstop (a genuine stalled-sampler / severe-lag signal) still does.
        internal const double SparseGapHeartbeatMarginFactor = 1.5;
        private const double roboticSampleIntervalSeconds = 0.25; // 4 Hz
        private const float roboticAngularDeadbandDegrees = 0.5f;
        private const float roboticLinearDeadbandMeters = 0.01f;
        internal const float AnimateHeatHotThreshold = 0.80f;
        internal const float AnimateHeatHotFallbackThreshold = 0.75f; // hysteresis: fall from Hot at 0.75, rise at 0.80
        internal const float AnimateHeatMediumThreshold = 0.40f;
        internal const float AnimateHeatMediumFallbackThreshold = 0.35f; // hysteresis: fall from Medium at 0.35, rise at 0.40
        private double lastRecordedUT = -1;
        // Physics-callback sample-source tallies (diagnostics only; see PostPhysicsPoseCache).
        private int physicsSampleFixedStepPoseCount;
        private int physicsSampleSkipCount;
        // True while the open section's only frame is a boundary seed copied from the
        // closed section (SeedBoundaryPoint); the flat list's same-UT tail then belongs
        // to the closed section.
        private bool currentSectionHoldsOnlySeed;
        private Vector3 lastRecordedVelocity;
        private Quaternion lastRecordedWorldRotation = Quaternion.identity;
        private bool hasLastRecordedWorldRotation;
        private double highFidelitySamplingUntilUT = double.NaN;
        private string highFidelitySamplingReason;
        private double reFlyTreeSamplingProximityMeters = double.NaN;
        private string reFlyTreeSamplingProximitySource;

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

        internal bool CurrentTrackSectionUsesRelativeFrame =>
            trackSectionActive && currentTrackSection.referenceFrame == ReferenceFrame.Relative;

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
        // Launch-unique vessel identity (Vessel.id Guid, "N" form) captured at record-start.
        private string pendingRecordedVesselGuid;
        // Captured once at recording-start time. Forwarded to CaptureAtStop verbatim;
        // never re-captured from the stop/remnant vessel, so a destructive breakup
        // that leaves a 1-part decoupler cannot overwrite the recorded controllable
        // identity used by the BG go-on-rails Destroyed override.
        private List<ControllerInfo> pendingStartControllers;

        // Logistics start-docked origin proof captured at recording-start time.
        // Non-null only when CaptureStartRouteOriginProofIfDocked detected exactly one
        // valid non-KSC partner vessel coupled to the active vessel at record_start.
        // pendingRouteOriginProofStartPartPids holds the snapshot's part-pid set so
        // BuildCaptureRecording's end manifest extraction is scoped to the same parts
        // that produced the start manifests, without persisting a transient field on
        // the RouteOriginProof DTO itself.
        private RouteOriginProof pendingRouteOriginProof;
        private List<uint> pendingRouteOriginProofStartPartPids;

        // M2 full-run cargo manifest (plan D3). Recorder-private working copy:
        // the START half is written onto the tree recording immediately at
        // capture time (write-once); this pending instance exists so
        // BuildCaptureRecording can complete the END half scoped to the START
        // pid set and forward a complete clone onto CaptureAtStop. On
        // quickload resume / recorder re-bind the pending copy is re-adopted
        // (cloned) from the tree recording's persisted start half - never
        // re-captured from the live vessel (round-2 BLOCKER 1).
        private RouteRunCargoManifest pendingRouteRunManifest;

        // M2 harvest-window capture (plan D4). Converter cache: every
        // BaseConverter-derived module on the recorded vessel (covers stock
        // harvesters/converters, asteroid/comet drills, and modded derivatives
        // by base class). Rebuilt at recording start and on part-count change
        // (EVA-constructed drills); Unity-null entries count as inactive so a
        // staged-away drill closes the window. Per-frame cost: N IsActivated
        // bool reads + one int part-count compare, zero allocations.
        private List<BaseConverter> cachedConverters;
        private int cachedConverterPartCount = -1;
        // Windows live RECORDER-SIDE until stop forwarding (plan D3 rule 2);
        // at most one window is open at a time. lastStopClosedHarvestWindow
        // tracks the window closed by the most recent BuildCaptureRecording so
        // ResumeAfterFalseAlarm can unwind exactly that close (the #287
        // RemoveLastEmittedTerminals precedent).
        private readonly List<RouteHarvestWindow> recorderHarvestWindows = new List<RouteHarvestWindow>();
        private RouteHarvestWindow openHarvestWindow;
        private RouteHarvestWindow lastStopClosedHarvestWindow;
        // Rails-exit funnel (plan D4 / round-2 nit 8): isOnRails flips false at
        // ~5 sites, so rails-ENTRY arms this flag and the FIRST poll after the
        // recorder is off rails again consumes it (poll runs only off-rails),
        // attributing a warp-period toggle at the exit boundary.
        private bool harvestRailsExitPollPending;

        /// <summary>
        /// UT at which <see cref="harvestRailsExitPollPending"/> was armed, so the
        /// rails-exit label can expire instead of surviving until the next transition of
        /// any kind. NaN when nothing is armed. See
        /// <see cref="RouteHarvestCapture.IsRailsExitLabelStillValid"/>.
        /// </summary>
        private double harvestRailsExitPollArmedUT = double.NaN;

        /// <summary>
        /// Read-only view of the controller identity captured at the most recent
        /// <see cref="StartRecording"/> call. Used by
        /// <see cref="ParsekFlight.FlushRecorderToTreeRecording"/> to forward the
        /// identity onto the tree recording in case the always-tree root creation
        /// fired before a live vessel was available.
        /// </summary>
        internal IReadOnlyList<ControllerInfo> StartControllers => pendingStartControllers;
        private double lastSnapshotRefreshUT = double.MinValue;
        private double lastFinalizationCacheRefreshUT = double.MinValue;

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

        internal const float EngineRecordedThrustEpsilon = 0.001f;
        internal const float EngineRecordedPowerEpsilon = 0.001f;

        /// <summary>
        /// Resets all recording state flags, vessel identity, velocity tracking, mod detection,
        /// and atmosphere/altitude boundary state for the given vessel.
        /// </summary>
        /// <summary>
        /// The names a loaded PersistentRotation build answers to. The original mod shipped
        /// <c>PersistentRotation.dll</c>; the only KSP 1.12 build (linuxgurugamer's
        /// PersistentRotationUpgraded 1.9.2.1) ships <c>Plugins/PersistentRotationUpgraded.dll</c>
        /// carrying <c>[KSPAssembly("Persistent Rotation Upgraded", 1, 9, 2)]</c>, and KSP's
        /// <c>AssemblyLoader.LoadedAssembly</c> sets <c>dllName</c> from the file name but then
        /// overwrites <c>name</c> with the KSPAssembly name (decompiled KSP 1.12.5), so both
        /// fields are checked against every spelling.
        /// </summary>
        internal static readonly string[] PersistentRotationAssemblyNames =
        {
            "PersistentRotation",
            "PersistentRotationUpgraded",
            "Persistent Rotation Upgraded",
        };

        private void ResetReFlyPostLoadSettle()
        {
            reFlyPostLoadSettleActive = false;
            reFlyPostLoadSettleUnpackedFrames = 0;
            reFlyPostLoadSettleStartLevelTime = 0f;
            reFlyPostLoadSettleSessionId = null;
            reFlyPostLoadSettleRecordingId = null;
        }

        internal void ActivateReFlyPostLoadSettleForTesting(string sessionId, string recordingId)
        {
            reFlyPostLoadSettleActive = true;
            reFlyPostLoadSettleUnpackedFrames = 0;
            reFlyPostLoadSettleStartLevelTime = 0f;
            reFlyPostLoadSettleSessionId = sessionId;
            reFlyPostLoadSettleRecordingId = recordingId;
        }

        /// <summary>
        /// PR 3b §"Re-Fly settle window hook" (Decision §11): expose the focus
        /// recorder's private Re-Fly post-load settle state as a derived predicate
        /// without leaking the underlying field. Returns true iff the focus
        /// recorder is non-null AND its settle window is currently active AND its
        /// settle target is the queried recording id.
        ///
        /// Reads <see cref="Patches.PhysicsFramePatch.ActiveRecorder"/>, the
        /// static handle to the focus recorder. For background-vessel debris (parent is
        /// itself a background recording, never the settle target), the
        /// accessor returns false and the per-frame check is a no-op.
        /// </summary>
        internal static bool IsReFlyPostLoadSettleActiveForRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return false;
            var focus = Patches.PhysicsFramePatch.ActiveRecorder;
            return focus != null
                && focus.reFlyPostLoadSettleActive
                && string.Equals(focus.reFlyPostLoadSettleRecordingId, recordingId,
                    StringComparison.Ordinal);
        }

        internal static bool IsReFlyPostLoadSettleOrStabilityHoldActiveForRecording(string recordingId)
        {
            return IsReFlyPostLoadSettleOrStabilityHoldActiveForRecording(recordingId, GetFrameCount());
        }

        internal static bool IsReFlyPostLoadSettleOrStabilityHoldActiveForRecording(
            string recordingId,
            int frame)
        {
            return IsReFlyPostLoadSettleActiveForRecording(recordingId)
                || ReFlySettleStabilityTracker.IsHoldActiveForRecording(recordingId, frame);
        }

        internal TrackSection CurrentTrackSectionForTesting => currentTrackSection;

        private void ArmReFlyPostLoadSettleIfNeeded(Vessel v, bool isPromotion)
        {
            bool vesselInAtmosphere = v != null
                && v.mainBody != null
                && ShouldSkipOrbitSegmentForAtmosphere(
                    v.mainBody.atmosphere,
                    v.altitude,
                    v.mainBody.atmosphereDepth);
            ReFlySessionMarker marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            if (!ShouldArmReFlyPostLoadSettle(
                    isPromotion,
                    vesselInAtmosphere,
                    ActiveTree?.Id,
                    ActiveTree?.ActiveRecordingId,
                    marker,
                    out string reason))
            {
                ParsekLog.Verbose("Recorder",
                    $"Re-Fly post-load settle not armed: reason={reason} " +
                    $"isPromotion={isPromotion} inAtmosphere={vesselInAtmosphere}");
                return;
            }

            reFlyPostLoadSettleActive = true;
            reFlyPostLoadSettleUnpackedFrames = 0;
            reFlyPostLoadSettleStartLevelTime = GetTimeSinceLevelLoad();
            reFlyPostLoadSettleSessionId = marker.SessionId;
            reFlyPostLoadSettleRecordingId = marker.ActiveReFlyRecordingId;
            string altText = v != null
                ? v.altitude.ToString("F0", CultureInfo.InvariantCulture)
                : "NaN";

            ParsekLog.Info("Recorder",
                $"Re-Fly post-load settle armed: session={ShortId(reFlyPostLoadSettleSessionId)} " +
                $"recording={ShortId(reFlyPostLoadSettleRecordingId)} " +
                $"packed={v?.packed} alt={altText} " +
                $"requiredUnpackedFrames={ReFlyPostLoadSettleRequiredUnpackedFrames} " +
                $"minLevelSeconds={ReFlyPostLoadSettleMinLevelSeconds.ToString("F3", CultureInfo.InvariantCulture)}");
        }

        private bool ShouldHoldReFlyPostLoadSettle(Vessel v, double currentUT)
        {
            if (!reFlyPostLoadSettleActive)
                return false;

            float elapsed = GetTimeSinceLevelLoad() - reFlyPostLoadSettleStartLevelTime;
            ReFlyPostLoadSettleDecision decision = EvaluateReFlyPostLoadSettle(
                active: true,
                vesselPacked: v != null && v.packed,
                elapsedLevelSeconds: elapsed,
                consecutiveUnpackedFrames: reFlyPostLoadSettleUnpackedFrames);
            reFlyPostLoadSettleUnpackedFrames = decision.ConsecutiveUnpackedFrames;

            if (decision.Clear)
            {
                string clearRecordingId = reFlyPostLoadSettleRecordingId;
                ParsekLog.Info("Recorder",
                    $"Re-Fly post-load settle complete: session={ShortId(reFlyPostLoadSettleSessionId)} " +
                    $"recording={ShortId(clearRecordingId)} " +
                    $"ut={currentUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"unpackedFrames={reFlyPostLoadSettleUnpackedFrames} " +
                    $"elapsedLevelSeconds={elapsed.ToString("F3", CultureInfo.InvariantCulture)}");
                ReFlySettleStabilityTracker.RecordSettleCleared(clearRecordingId, GetFrameCount());
                ResetReFlyPostLoadSettle();
                return false;
            }

            if (decision.Hold)
            {
                ReFlySettleStabilityTracker.RecordSettleActive(
                    reFlyPostLoadSettleRecordingId,
                    GetFrameCount());
                ParsekLog.VerboseRateLimited("Recorder",
                    "refly-post-load-settle|" + (reFlyPostLoadSettleSessionId ?? "<no-session>"),
                    $"Re-Fly post-load settle holding trajectory sample: " +
                    $"session={ShortId(reFlyPostLoadSettleSessionId)} " +
                    $"recording={ShortId(reFlyPostLoadSettleRecordingId)} " +
                    $"reason={decision.Reason} packed={v?.packed} " +
                    $"ut={currentUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"unpackedFrames={reFlyPostLoadSettleUnpackedFrames} " +
                    $"elapsedLevelSeconds={elapsed.ToString("F3", CultureInfo.InvariantCulture)}",
                    1.0);
                return true;
            }

            ResetReFlyPostLoadSettle();
            return false;
        }

        private bool ShouldSuppressReFlyPostLoadTrajectoryWrite(double ut, string source)
        {
            if (!reFlyPostLoadSettleActive)
                return false;

            ParsekLog.VerboseRateLimited("Recorder",
                "refly-post-load-settle-write|" + (reFlyPostLoadSettleSessionId ?? "<no-session>") + "|" + (source ?? "unknown"),
                $"Re-Fly post-load settle suppressed trajectory write: " +
                $"source={source ?? "unknown"} " +
                $"session={ShortId(reFlyPostLoadSettleSessionId)} " +
                $"recording={ShortId(reFlyPostLoadSettleRecordingId)} " +
                $"ut={ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"unpackedFrames={reFlyPostLoadSettleUnpackedFrames}",
                1.0);
            return true;
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id))
                return "(none)";
            return id.Length <= 8 ? id : id.Substring(0, 8);
        }

        #region M2 harvest-window capture (plan D4)

        /// <summary>
        /// Rebuilds the BaseConverter cache for the recorded vessel. Called at
        /// recording start (module-cache block) and from the per-frame poll on
        /// part-count change (EVA-constructed drills).
        /// </summary>
        private void RebuildHarvestConverterCache(Vessel v, string reason)
        {
            cachedConverters = null;
            cachedConverterPartCount = -1;
            if (v == null || v.parts == null)
                return;

            var converters = new List<BaseConverter>();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null || p.Modules == null)
                    continue;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    // Base-class keying (the TimeJumpManager precedent): covers
                    // ModuleResourceHarvester, ModuleResourceConverter, the
                    // asteroid/comet drills, and modded derivatives - no module
                    // name list to maintain (M-MIS-11 cross-note).
                    if (p.Modules[m] is BaseConverter converter)
                        converters.Add(converter);
                }
            }

            cachedConverters = converters;
            cachedConverterPartCount = v.parts.Count;
            ParsekLog.Verbose("Recorder",
                $"Harvest converter cache: vessel='{v.vesselName}' converters={converters.Count} " +
                $"(rebuild reason={reason})");
        }

        /// <summary>
        /// N boolean reads over the cached converter list. Unity-destroyed
        /// entries (staged-away drill) count as inactive, so destruction of the
        /// last active converter closes the window on the next poll.
        /// </summary>
        private bool IsAnyCachedConverterActive()
        {
            if (cachedConverters == null)
                return false;
            for (int i = 0; i < cachedConverters.Count; i++)
            {
                BaseConverter c = cachedConverters[i];
                if (c == null)
                    continue; // Unity-null: destroyed/staged-away module
                if (c.IsActivated)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Per-frame harvest poll (plan D4): N bool reads + one int compare in
        /// the steady state, zero allocations; manifest extraction happens only
        /// at open/close transitions (player-toggle or rails-transition
        /// frequency). Steady state logs nothing.
        ///
        /// <para><b>Rails gate.</b> The poll must never open or close a window
        /// off a PACKED frame's manifest.
        /// <see cref="OnPhysicsFrame"/>'s <c>isOnRails</c> gate does not deliver
        /// that on its own: <see cref="OnVesselGoOnRails"/> early-returns for
        /// LANDED / SPLASHED / PRELAUNCH (and for below-atmosphere) BEFORE
        /// setting <c>isOnRails</c>, so a packed surface vessel keeps ticking
        /// physics frames with <c>isOnRails == false</c>. The vessel's own
        /// <c>packed</c> flag is therefore the gate here
        /// (<see cref="RouteHarvestCapture.ShouldRunHarvestPoll"/>). Do NOT
        /// "fix" that by setting <c>isOnRails</c> in the surface branch - it
        /// gates orbit-segment creation and SOI handling.</para>
        ///
        /// <para><b>Funnel consume.</b> The rails-exit pending flag is consumed
        /// only when a transition is actually emitted
        /// (<see cref="RouteHarvestCapture.ShouldConsumeRailsExitFunnel"/>), so
        /// a no-transition poll cannot burn the boundary label before the
        /// warp-period toggle's close reaches it.</para>
        /// </summary>
        private void PollHarvestActivity(Vessel v)
        {
            if (v == null)
                return;
            if (!RouteHarvestCapture.ShouldRunHarvestPoll(v.packed))
            {
                // Zero-allocation on the packed path, for real: the IsVerboseEnabled test
                // comes FIRST, so with verbose off (the shipping default) this frame builds
                // nothing at all - no string, and no closure/display-class for the
                // Func<string> either, which the lambda alone would still have allocated
                // every frame. With verbose ON the factory defers the interpolation to the
                // frames the rate limiter actually emits.
                if (ParsekLog.IsVerboseEnabled)
                    ParsekLog.VerboseRateLimited("Recorder", "harvest-poll-packed-skip",
                        () => $"Harvest poll skipped on packed frame: vessel='{v.vesselName}' " +
                            $"pid={v.persistentId.ToString(CultureInfo.InvariantCulture)} " +
                            $"sit={v.situation} isOnRails={(isOnRails ? "1" : "0")} " +
                            $"railsExitPending={(harvestRailsExitPollPending ? "1" : "0")} " +
                            $"windowOpen={(openHarvestWindow != null ? "1" : "0")}");
                return;
            }

            int partCount = v.parts != null ? v.parts.Count : 0;
            if (partCount != cachedConverterPartCount)
            {
                RebuildHarvestConverterCache(v, "part-count");
                // A drill built on EVA inside an open window: fold its rate in now, or a
                // window that closes at the recording stop (which keeps the open sample)
                // would cap the new drill's output to zero.
                if (openHarvestWindow != null)
                {
                    openHarvestWindow.PlausibleRates = RouteHarvestCapture.MergePlausibleRates(
                        openHarvestWindow.PlausibleRates, BuildPlausibleHarvestRates(v));
                    ParsekLog.Verbose("Recorder",
                        $"Harvest window rates resampled on part-count change: " +
                        $"window={openHarvestWindow.WindowId} " +
                        $"plausibleRates={FormatPlausibleRates(openHarvestWindow.PlausibleRates)}");
                }
            }

            bool anyActive = IsAnyCachedConverterActive();
            HarvestActivityTransition transition = RouteHarvestCapture.EvaluateTransition(
                anyActive,
                openHarvestWindow != null);
            if (!RouteHarvestCapture.ShouldConsumeRailsExitFunnel(transition))
                return; // no transition: the funnel flag stays armed by design

            double ut = Planetarium.GetUniversalTime();
            // The rails-exit label is only true NEAR the boundary. Left unbounded the flag
            // survives until the next transition of any kind, so a player toggle minutes
            // later would be attributed to a warp exit it has nothing to do with. Bound it
            // to the UT stamped at the exit.
            bool railsExitLabelValid = RouteHarvestCapture.IsRailsExitLabelStillValid(
                harvestRailsExitPollPending, harvestRailsExitPollArmedUT, ut);
            string trigger = railsExitLabelValid ? "rails-exit" : "toggle";
            if (harvestRailsExitPollPending && !railsExitLabelValid)
                ParsekLog.Verbose("Recorder",
                    "Harvest funnel label expired: the armed rails-exit label is older than " +
                    $"{RouteHarvestCapture.RailsExitLabelMaxAgeSeconds.ToString("R", CultureInfo.InvariantCulture)}s " +
                    $"(armedUT={harvestRailsExitPollArmedUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"ut={ut.ToString("F2", CultureInfo.InvariantCulture)}); attributing this transition " +
                    "to a player toggle instead");
            harvestRailsExitPollPending = false;
            harvestRailsExitPollArmedUT = double.NaN;

            ParsekLog.Verbose("Recorder",
                $"Harvest funnel consumed at transition: trigger={trigger} " +
                $"transition={transition} anyActive={(anyActive ? "1" : "0")} " +
                $"vessel='{v.vesselName}' pid={v.persistentId.ToString(CultureInfo.InvariantCulture)} " +
                $"recVessel={RecordingVesselId.ToString(CultureInfo.InvariantCulture)} " +
                $"ut={ut.ToString("F2", CultureInfo.InvariantCulture)}");

            if (transition == HarvestActivityTransition.Open)
                OpenHarvestWindow(v, ut, atRecordingStart: false, trigger: trigger);
            else
                CloseHarvestWindow(v, ut, trigger);
        }

        /// <summary>
        /// Open-at-start handling (plan D5 part i): a converter already active
        /// at recording start opens a window AT start, so the stock
        /// lastUpdateTime catch-up burst that fires in the first frames after a
        /// vessel loads/unpacks lands INSIDE the open window and attributes as
        /// harvested.
        /// </summary>
        private void InitializeHarvestWindowAtStart(Vessel v)
        {
            if (v == null)
                return;
            if (!IsAnyCachedConverterActive())
                return;

            OpenHarvestWindow(v, Planetarium.GetUniversalTime(), atRecordingStart: true,
                trigger: "recording-start");
        }

        /// <summary>
        /// Rails-ENTRY re-baseline (plan D4 warp rule): with a window open it
        /// stays open (production continues inside it); with converters active
        /// and no window open (activation raced the poll), open one AT the
        /// rails boundary.
        ///
        /// <para><b>It does NOT arm the exit funnel, and that is a correction.</b>
        /// An earlier version armed the funnel here too, arguing that the exit
        /// event can miss this recorder (<see cref="OnVesselGoOffRails"/>
        /// early-returns unless the unpacking vessel is both the active vessel
        /// AND the recorded pid) while its polls resume anyway. That argument
        /// stopped working the moment the label gained a validity window
        /// (<see cref="RouteHarvestCapture.RailsExitLabelMaxAgeSeconds"/>): an arm
        /// stamped at rails ENTRY is, by construction, older than the entire warp
        /// by the time any post-exit poll runs, so it expires and labels nothing.
        /// Keeping it would have been a comment describing a path the code could
        /// no longer take. The arm therefore lives ONLY at the exit
        /// (<see cref="OnVesselGoOffRails"/>, above its surface early-return), which
        /// is the path the live H38 flights actually exercise - and measure at a
        /// 0.02 s age, comfortably inside the window.</para>
        /// </summary>
        private void HandleHarvestRailsEntry(Vessel v)
        {
            if (v == null)
                return;

            bool anyActive = IsAnyCachedConverterActive();
            if (openHarvestWindow != null)
            {
                ParsekLog.VerboseRateLimited("Recorder", "harvest-rails-window-open",
                    $"Harvest window stays open across rails entry: window={openHarvestWindow.WindowId} " +
                    $"anyActive={anyActive}");
                return;
            }

            if (RouteHarvestCapture.ShouldOpenWindowAtRailsEntry(anyActive, windowOpen: false))
                OpenHarvestWindow(v, Planetarium.GetUniversalTime(), atRecordingStart: false,
                    trigger: "rails-entry");
        }

        private void OpenHarvestWindow(Vessel v, double ut, bool atRecordingStart, string trigger)
        {
            Dictionary<string, ResourceAmount> startManifest =
                LiveVesselSnapshot.ExtractLiveResourceManifest(v);
            List<string> converterIds = BuildActiveConverterIds();
            Dictionary<string, double> plausibleRates = BuildPlausibleHarvestRates(v);

            openHarvestWindow = RouteHarvestCapture.OpenWindow(
                ut,
                startManifest,
                v.mainBody != null ? v.mainBody.name : null,
                v.latitude,
                v.longitude,
                v.altitude,
                (int)v.situation,
                converterIds,
                atRecordingStart);
            openHarvestWindow.PlausibleRates = plausibleRates;
            recorderHarvestWindows.Add(openHarvestWindow);

            ParsekLog.Info("Recorder",
                $"Harvest window opened: window={openHarvestWindow.WindowId} " +
                $"ut={ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"converters=[{string.Join(",", converterIds)}] " +
                $"res={startManifest?.Count ?? 0} atStart={(atRecordingStart ? "1" : "0")} " +
                $"trigger={trigger} plausibleRates={FormatPlausibleRates(plausibleRates)}");
        }

        private void CloseHarvestWindow(Vessel v, double ut, string trigger)
        {
            RouteHarvestWindow window = openHarvestWindow;
            if (window == null)
                return;

            Dictionary<string, ResourceAmount> endManifest =
                LiveVesselSnapshot.ExtractLiveResourceManifest(v);
            RouteHarvestCapture.CloseWindow(window, ut, endManifest, atRecordingStop: false);
            // Second rate sample: a crew bonus that rose inside the window (an
            // engineer boarded) still counts. The close-at-stop path keeps the open
            // sample - at a dock stop the live vessel is the merged stack.
            window.PlausibleRates = RouteHarvestCapture.MergePlausibleRates(
                window.PlausibleRates, BuildPlausibleHarvestRates(v));
            openHarvestWindow = null;

            ParsekLog.Info("Recorder",
                $"Harvest window closed: window={window.WindowId} " +
                $"ut={ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"res={endManifest?.Count ?? 0} atStop=0 trigger={trigger} " +
                $"plausibleRates={FormatPlausibleRates(window.PlausibleRates)}");
        }

        /// <summary>
        /// Reads the plausibility-bound inputs off every cached converter
        /// (HARVEST-ROUTE-PLAUSIBILITY-CAP) and folds them through the pure
        /// <see cref="RouteHarvestCapture.ComputePlausibleRates"/>. Exact stock types
        /// only: a surface / ocean <c>ModuleResourceHarvester</c> (abundance at the
        /// vessel's location, as the drill itself asks <c>ResourceMap</c>), a
        /// <c>ModuleResourceConverter</c> (its recipe outputs, already in units), the
        /// science lab (no resource), an asteroid / comet drill on a vessel with no
        /// asteroid or comet part (no resource). Any other converter - atmospheric or
        /// exospheric harvesters, an asteroid / comet drill with a space object aboard,
        /// modded subclasses - makes the window unbounded (null), as does any exception.
        /// </summary>
        private Dictionary<string, double> BuildPlausibleHarvestRates(Vessel v)
        {
            if (cachedConverters == null || v == null)
                return null;
            try
            {
                bool carriesSpaceObject = VesselCarriesSpaceObject(v);
                var inputs = new List<HarvestConverterRate>(cachedConverters.Count);
                for (int i = 0; i < cachedConverters.Count; i++)
                {
                    BaseConverter c = cachedConverters[i];
                    if (c == null)
                        continue; // Unity-null: destroyed/staged-away module
                    inputs.Add(ReadHarvestConverterRate(
                        c, c.GetCrewEfficiencyBonus(), v.mainBody, v.latitude, v.longitude, v.altitude,
                        carriesSpaceObject));
                }
                return RouteHarvestCapture.ComputePlausibleRates(inputs);
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn("Recorder",
                    $"Harvest plausibility rates unavailable ({ex.GetType().Name}: {ex.Message}); window unbounded");
                return null;
            }
        }

        /// <summary>
        /// One converter's plausibility-bound inputs (HARVEST-ROUTE-PLAUSIBILITY-CAP).
        /// Exact stock types only: a surface / ocean <c>ModuleResourceHarvester</c> reads the
        /// abundance at the given location the way the drill asks <c>ResourceMap</c>, a
        /// <c>ModuleResourceConverter</c> its recipe outputs (already in units), the science
        /// lab produces no resource; any other type, or a drill with no body or no
        /// <c>ResourceMap</c>, is <see cref="HarvestConverterKind.Unmodeled"/>.
        /// <paramref name="crewBonus"/> is passed in because stock's
        /// <c>GetCrewEfficiencyBonus</c> reads the part's vessel, which a part prefab (the
        /// in-game check of this reader) does not have.
        ///
        /// <para>Both stock surface drills also carry a <c>ModuleAsteroidDrill</c>, which mines
        /// only an asteroid part on its own vessel (decompiled: it looks for a
        /// <c>ModuleAsteroid</c> among <c>vessel.parts</c>); <c>ModuleCometDrill</c> is the
        /// comet twin. On a vessel with no asteroid or comet part
        /// (<paramref name="hostCarriesSpaceObject"/> false) they produce nothing and read as
        /// <see cref="HarvestConverterKind.NoResourceOutput"/>, so a stock drill keeps its
        /// bound; with one they stay unmodeled and the window uncapped.</para>
        /// </summary>
        internal static HarvestConverterRate ReadHarvestConverterRate(
            BaseConverter c, double crewBonus, CelestialBody body,
            double latitude, double longitude, double altitude, bool hostCarriesSpaceObject)
        {
            var input = new HarvestConverterRate
            {
                Kind = HarvestConverterKind.Unmodeled,
                Multiplier = (double)c.EfficiencyBonus * crewBonus
            };
            System.Type type = c.GetType();
            if (type == typeof(ModuleResourceHarvester))
            {
                var drill = (ModuleResourceHarvester)c;
                if ((drill.HarvesterType == 0 || drill.HarvesterType == 1)
                    && ResourceMap.Instance != null && body != null)
                {
                    input.Kind = HarvestConverterKind.Drill;
                    input.ResourceName = drill.ResourceName;
                    input.Efficiency = drill.Efficiency;
                    input.Abundance = ResourceMap.Instance.GetAbundance(new AbundanceRequest
                    {
                        Altitude = altitude,
                        BodyId = body.flightGlobalsIndex,
                        CheckForLock = false,
                        Latitude = latitude,
                        Longitude = longitude,
                        ResourceType = (HarvestTypes)drill.HarvesterType,
                        ResourceName = drill.ResourceName
                    });
                }
            }
            else if (type == typeof(ModuleResourceConverter))
            {
                ConversionRecipe recipe = ((ModuleResourceConverter)c).Recipe;
                input.Kind = HarvestConverterKind.Recipe;
                input.Outputs = new List<KeyValuePair<string, double>>();
                if (recipe != null && recipe.Outputs != null)
                {
                    for (int o = 0; o < recipe.Outputs.Count; o++)
                        input.Outputs.Add(new KeyValuePair<string, double>(
                            recipe.Outputs[o].ResourceName, recipe.Outputs[o].Ratio));
                }
            }
            else if (type == typeof(ModuleScienceConverter))
            {
                input.Kind = HarvestConverterKind.NoResourceOutput;
            }
            else if ((type == typeof(ModuleAsteroidDrill) || type == typeof(ModuleCometDrill))
                && !hostCarriesSpaceObject)
            {
                input.Kind = HarvestConverterKind.NoResourceOutput;
            }
            return input;
        }

        /// <summary>
        /// True when any part of <paramref name="v"/> is an asteroid or a comet, the only
        /// thing a stock asteroid / comet drill can mine (see
        /// <see cref="ReadHarvestConverterRate"/>).
        /// </summary>
        private static bool VesselCarriesSpaceObject(Vessel v)
        {
            if (v == null || v.parts == null)
                return false;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null)
                    continue;
                if (p.FindModuleImplementing<ModuleAsteroid>() != null
                    || p.FindModuleImplementing<ModuleComet>() != null)
                    return true;
            }
            return false;
        }

        internal static string FormatPlausibleRates(Dictionary<string, double> rates)
        {
            if (rates == null)
                return "unbounded";
            var names = new List<string>(rates.Keys);
            names.Sort(System.StringComparer.Ordinal);
            var parts = new List<string>(names.Count);
            for (int i = 0; i < names.Count; i++)
                parts.Add(names[i] + ":" + rates[names[i]].ToString("R", CultureInfo.InvariantCulture));
            return "[" + string.Join(",", parts) + "]";
        }

        /// <summary>
        /// Close-at-stop + forwarding (plan D3 rule 4 / D4): an open window is
        /// closed with a manifest extracted from the CAPTURE SNAPSHOT (never a
        /// live walk at the stop frame - at the dock pid-change stop the live
        /// vessel is the merged stack), and the full recorder-side window list
        /// is forwarded onto the capture as deep clones. The closed window is
        /// remembered so ResumeAfterFalseAlarm can unwind exactly this close.
        /// </summary>
        private void CloseHarvestWindowsAtStopAndForward(Recording capture)
        {
            lastStopClosedHarvestWindow = null;
            if (capture == null)
                return;

            if (openHarvestWindow != null)
            {
                double stopUT = Planetarium.GetUniversalTime();
                Dictionary<string, ResourceAmount> endManifest =
                    VesselSnapshotOps.ExtractResourceManifest(capture.VesselSnapshot);
                RouteHarvestCapture.CloseWindow(openHarvestWindow, stopUT, endManifest,
                    atRecordingStop: true);
                lastStopClosedHarvestWindow = openHarvestWindow;
                openHarvestWindow = null;

                ParsekLog.Info("Recorder",
                    $"Harvest window closed: window={lastStopClosedHarvestWindow.WindowId} " +
                    $"ut={stopUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"res={endManifest?.Count ?? 0} atStop=1 trigger=recording-stop");
            }

            if (recorderHarvestWindows.Count > 0)
            {
                capture.RouteHarvestWindows =
                    RouteProofMetadata.CloneHarvestWindows(recorderHarvestWindows);
                ParsekLog.Verbose("Recorder",
                    $"BuildCaptureRecording: forwarded {recorderHarvestWindows.Count} harvest window(s) " +
                    $"recording={capture.RecordingId ?? "<none>"}");
            }
        }

        private List<string> BuildActiveConverterIds()
        {
            var ids = new List<string>();
            if (cachedConverters == null)
                return ids;

            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < cachedConverters.Count; i++)
            {
                BaseConverter c = cachedConverters[i];
                if (c == null || !c.IsActivated)
                    continue;
                uint partPid = c.part != null ? c.part.persistentId : 0u;
                ids.Add(partPid.ToString(ic) + ":" + c.GetType().Name + ":" + (c.ConverterName ?? ""));
            }
            return ids;
        }

        #endregion

        /// <summary>
        /// Handles vessel PID change detected during OnPhysicsFrame.
        /// Decides whether to continue (EVA), background (tree), or capture+stop (boarding).
        /// Returns true if the frame should be skipped (recording stopped/backgrounded).
        /// </summary>
        private bool HandleVesselSwitchDuringRecording(Vessel v)
        {
            // 1. Classify the pid change.
            VesselSwitchDecision decision = DecideOnVesselSwitch(
                RecordingVesselId, v.persistentId, v.isEVA, RecordingStartedAsEva,
                activeTree: ActiveTree);

            ParsekLog.Verbose("Recorder", $"Vessel switch detected: decision={decision}, " +
                $"oldPid={RecordingVesselId}, newPid={v.persistentId}, " +
                $"isEva={v.isEVA}, hasTree={ActiveTree != null}");

            // 2. ContinueOnEva — early return (existing, unchanged)
            if (decision == VesselSwitchDecision.ContinueOnEva)
            {
                RecordingVesselId = v.persistentId;
                // The recording now follows the kerbal, so the craft's cached engine/RCS/robotic
                // modules are foreign from here on. Rebuilding drops them outright rather than
                // leaving the per-poll ownership guard to reject each one (and log each rejection)
                // every frame for the rest of the EVA. The same call also prunes the tracking-set
                // keys for every part left behind on the craft, so the EVA's own recording does not
                // close with terminal EngineShutdown / RCSStopped events for the craft's engines —
                // events the kerbal's ghost has no part to play them on, and which would pin the
                // tail-trim to the end of the EVA.
                OnVesselWasModified(v);
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

            // A vessel-switch / pid-change stop is a CHAIN BOUNDARY too: the pair may be
            // separating right now (the dock/undock pid change), so it must not conclude
            // "stopped while docked".
            CaptureAtStop = BuildCaptureRecording(
                recordedVessel != null ? recordedVessel.vesselName : v.vesselName,
                VesselDestroyedDuringRecording || recordedVessel == null,
                recordedVessel,
                stopIsChainBoundary: true);

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

            ParsekLog.Verbose("Recorder", $"Active vessel changed during recording — auto-stopping " +
                $"(decision={decision}, was pid={RecordingVesselId}, now pid={v.persistentId}, " +
                $"nowIsEva={v.isEVA}, startedAsEva={RecordingStartedAsEva})");
            ParsekLog.ScreenMessage("Recording stopped - vessel changed", 3f);
            return true;
        }

        /// <summary>
        /// Minimum backwards UT jump (seconds) that triggers a quickload/revert trim.
        /// Guards against false positives from sub-second time-warp boundary jitter
        /// while still catching user-initiated F9 (which typically rewinds tens of
        /// seconds or more).
        /// </summary>
        internal const double TimeRegressionThresholdSeconds = 1.0;

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
            // M2 harvest: deliberately NO exit-funnel arm here, for the same reason
            // HandleHarvestRailsEntry no longer carries one - an arm stamped at rails
            // ENTRY (or at a rails START) is older than the whole warp by the time a
            // post-exit poll runs, so it expires against
            // RouteHarvestCapture.RailsExitLabelMaxAgeSeconds and labels nothing. The
            // funnel is armed at the EXIT (OnVesselGoOffRails), which is where the
            // boundary the label names actually is. Open-at-start already handled any
            // converter active at StartRecording.

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

            // M2 harvest rails-entry re-baseline (plan D4 warp rule). Runs for
            // EVERY rails entry, including the surface/atmosphere early-return
            // branches below - the vessel still packs and loaded converters
            // keep producing at warp, so the window state must be settled here.
            HandleHarvestRailsEntry(v);

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
            environmentHysteresis?.SettleForPackedVessel(onRailsUT, "go-on-rails");
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
        /// <remarks>
        /// No explicit AppendSectionStartSeamPoint call here: the sole caller
        /// (OnVesselGoOnRails) invokes SamplePosition(v) immediately after this
        /// method returns, which builds + commits a point at the same UT we
        /// just used as the new ABSOLUTE section's startUT. That commit IS the
        /// seam — adding an explicit call would emit a duplicate-UT point.
        /// </remarks>
        private void ClearRelativeModeForRailsTransition()
        {
            if (isRelativeMode)
            {
                ParsekLog.Info("Anchor",
                    $"RELATIVE mode cleared on rails transition: anchorRecordingId={currentAnchorRecordingId ?? "(none)"} " +
                    $"diagnosticPid={currentAnchorPid}");
                double clearUT = Planetarium.GetUniversalTime();
                var clearEnv = environmentHysteresis != null
                    ? environmentHysteresis.CurrentEnvironment
                    : SegmentEnvironment.ExoBallistic;
                CloseCurrentTrackSection(clearUT);
                StartNewTrackSection(clearEnv, ReferenceFrame.Absolute, clearUT);
                isRelativeMode = false;
                ClearCurrentRecordingAnchor();
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

        /// <summary>
        /// THE RECORDER'S ORBITAL-FRAME ENCODE, and the reference implementation of the
        /// orbital-frame contract: the stored rotation is the vessel's world attitude expressed
        /// relative to <c>LookRotation(orbitalVelocity_WORLD, radialOut_WORLD)</c>.
        /// <para>
        /// Both halves are Y-up WORLD and that is not incidental — <c>v.obt_velocity</c> is
        /// <c>orbit.GetRelativeVel()</c> == <c>orbit.vel.xzy</c> (decompiled <c>Vessel</c> /
        /// <c>Orbit</c>), i.e. the SWIZZLE of what <c>Orbit.getOrbitalVelocityAtUT</c> returns,
        /// and <c>v.CoMD - v.mainBody.position</c> is world. Recorder-authored segments are the
        /// bulk of the recorded population and cannot be re-encoded, which is why the fifth
        /// frame mismatch (docs/dev/todo-and-known-bugs.md) was closed by moving the CONSUMER
        /// and the predicted-tail producer onto THIS convention rather than the other way round.
        /// See the contract banner on <see cref="TrajectoryMath.SwizzleZupBodyRelativeToWorld"/>.
        /// </para>
        /// <para>
        /// Shared by the go-on-rails capture and the SOI-change capture (they used to hold
        /// two literal copies of it), and called directly by the in-game
        /// <c>RecorderEncodedOrbitalFrameRotationResolvesToTheVesselAttitude</c> probe so the
        /// live round trip runs the production encode rather than a transcription of it.
        /// </para>
        /// </summary>
        internal static void StampVesselOrbitalFrameRotation(ref OrbitSegment segment, Vessel v)
        {
            Vector3d orbVel = v.obt_velocity;
            Vector3d radialOut = (v.CoMD - v.mainBody.position).normalized;
            segment.orbitalFrameRotation =
                TrajectoryMath.ComputeOrbitalFrameRotation(v.transform.rotation, orbVel, radialOut);
        }

        private OrbitSegment CreateOrbitSegmentWithRotation(Vessel v, double startUT)
        {
            var segment = CreateOrbitSegmentFromVessel(v, startUT);
            LogActiveReFlyOrbitSegmentVelocityNonFinite(
                v,
                startUT,
                "orbit-segment");

            StampVesselOrbitalFrameRotation(ref segment, v);

            // Capture angular velocity if PersistentRotation is active and vessel is spinning
            if (hasPersistentRotation && v.rootPart != null && v.rootPart.rb != null)
            {
                // v.angularVelocity is local to v.ReferenceTransform (see the helper), and its
                // magnitude is frame-invariant, so the threshold reads the same either way.
                Vector3 referenceLocalAngVel = v.angularVelocity;
                if (referenceLocalAngVel.magnitude > TrajectoryMath.SpinThreshold)
                {
                    Transform referenceTransform = v.ReferenceTransform != null
                        ? v.ReferenceTransform
                        : v.transform;
                    segment.angularVelocity = TrajectoryMath.ComputeSpinAngularVelocityVesselLocal(
                        v.transform.rotation, referenceTransform.rotation, referenceLocalAngVel);
                    ParsekLog.Verbose("Recorder",
                        $"Spinning vessel detected (|angVel|={referenceLocalAngVel.magnitude:F4}), recording angular velocity for spin-forward"
                        + $" (vesselLocal={segment.angularVelocity}, referenceLocal={referenceLocalAngVel})");
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
                lastRoboticPosition, railsUT, "Recorder", thrustingJetpackParts);
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
            // S4 joins the same convention as the three families above: the terminal emit does NOT
            // mutate its sets, so BOTH halves of the thrust debounce state are cleared here. The
            // thrusting SET must be emptied for the same reason activeEngineKeys is — it is what
            // makes a second rails emit a no-op instead of a double-close — and the frame COUNTS
            // must go with it, so a half-accumulated burst cannot resume into a start event
            // off-rails that its own frames never earned.
            thrustingJetpackParts.Clear();
            evaThrustFrameCounts.Clear();
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

            // M2 harvest rails-EXIT funnel (plan D4 warp rule). Armed here, ABOVE
            // the surface early-return, so BOTH exit branches arm it: the first
            // off-rails poll after any pack must carry trigger=rails-exit, and the
            // surface-situation vessel - which packs without isOnRails ever being
            // set, and so returns below without touching the orbital bookkeeping -
            // is exactly the one whose warp-period toggle the funnel has to
            // attribute. This is the ONLY arm site: an entry-side or start-side arm
            // would be stamped a whole warp before any post-exit poll and would expire
            // against RouteHarvestCapture.RailsExitLabelMaxAgeSeconds without ever
            // labelling anything.
            harvestRailsExitPollPending = true;
            harvestRailsExitPollArmedUT = Planetarium.GetUniversalTime();

            // Surface vessel went on rails without orbit segment — sample boundary point for continuity
            if (!isOnRails)
            {
                // A cheat teleport armed at PostOrbitSet finishes here, where the vessel's
                // live position is valid again (FlightRecorder.CheatTeleport.cs).
                SegmentEnvironment teleportEnv = environmentHysteresis != null
                    ? environmentHysteresis.CurrentEnvironment
                    : SegmentEnvironment.ExoBallistic;
                if (FinishPendingCheatTeleportAtOffRails(
                        Planetarium.GetUniversalTime(), teleportEnv, () => SamplePosition(v)))
                {
                    ReseedAtmosphereState(v);
                    ReseedAltitudeState(v);
                    RefreshFinalizationCache(v, "go_off_rails_cheat_teleport", force: true);
                    return;
                }
                SamplePosition(v);
                RefreshFinalizationCache(v, "go_off_rails_surface", force: true);
                return;
            }

            // Finalize orbit segment
            currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
            OrbitSegments.Add(currentOrbitSegment);
            AddOrbitSegmentToCurrentTrackSection(currentOrbitSegment);
            isOnRails = false;
            LogTimeJumpRecaptureCloseIfAny(v, currentOrbitSegment);

            // Reference frame transition: ORBITAL_CHECKPOINT -> ABSOLUTE
            double offRailsUT = Planetarium.GetUniversalTime();
            // The vessel was packed until now: whatever burn the hysteresis last
            // confirmed ended before the pack, so the first physics section after the
            // unpack opens as a coast (a real burn re-confirms through the 1 s debounce).
            environmentHysteresis?.SettleForPackedVessel(offRailsUT, "go-off-rails");
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
            if (!IsRecording) return;
            if (data.host != FlightGlobals.ActiveVessel)
            {
                ParsekLog.VerboseRateLimited("Recorder", "soi-other-vessel",
                    $"OnVesselSOIChanged: ignoring non-active vessel (pid={data.host?.persistentId})");
                return;
            }
            if (data.host.persistentId != RecordingVesselId) return;

            // Off rails (a burn through the boundary, a 1x or physics-warp coast) there is no
            // orbit segment to roll over; the per-frame sampler splits the section at the
            // first sample on the new body (SplitSectionIfPhysicsBodyChanged).
            if (!isOnRails)
            {
                ArmPhysicsSoiSample(data.from?.name, data.to?.name, Planetarium.GetUniversalTime());
                return;
            }

            // Close current orbit segment in old SOI
            currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
            OrbitSegments.Add(currentOrbitSegment);
            AddOrbitSegmentToCurrentTrackSection(currentOrbitSegment);

            // Start new orbit segment in new SOI
            var v = data.host;
            double soiUT = Planetarium.GetUniversalTime();
            currentOrbitSegment = CreateOrbitSegmentFromVessel(v, soiUT);
            LogActiveReFlyOrbitSegmentVelocityNonFinite(
                v,
                soiUT,
                "soi-change");

            // Capture orbital-frame-relative rotation for new SOI, through the same encode the
            // go-on-rails capture uses (world radial + world velocity).
            StampVesselOrbitalFrameRotation(ref currentOrbitSegment, v);

            // Note: vessel is on rails during SOI change — rb is null, so angular velocity
            // is unavailable here. Spin data is only captured at the initial go-on-rails boundary.
            // A spinning vessel crossing an SOI boundary will use orbital-frame rotation (not spin-forward)
            // for the new segment. This is an acceptable v1 limitation.

            // Close current TrackSection and start a new one at the SOI boundary (#251).
            // Without this, a single TrackSection spans both SOIs and the optimizer cannot
            // split at the SOI boundary (it only splits at section boundaries).
            SegmentEnvironment currentEnv = environmentHysteresis != null
                ? environmentHysteresis.CurrentEnvironment
                : SegmentEnvironment.ExoBallistic;
            TransitionTrackSectionAtSoiBoundary(currentEnv, soiUT, isOnRails);

            // Reseed atmosphere and altitude state for the new body
            ReseedAtmosphereState(v);
            ReseedAltitudeState(v);

            // Flag for ParsekFlight to trigger chain split in Update()
            SoiChangePending = true;
            SoiChangeFromBody = data.from.name;
            RefreshFinalizationCache(v, "soi_change", force: true);

            ParsekLog.Verbose("Recorder",
                $"SOI changed {data.from.name} → {data.to.name} — new segment ofrRot={currentOrbitSegment.orbitalFrameRotation}, " +
                $"angVel={currentOrbitSegment.angularVelocity}, frame={TrajectoryMath.OrbitalFrameConventionTag}");
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
            pendingJointChildPartOriginSeeds.Clear();

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
                double backgroundUT = Planetarium.GetUniversalTime();
                currentOrbitSegment = CreateOrbitSegmentFromVessel(v, backgroundUT);
                LogActiveReFlyOrbitSegmentVelocityNonFinite(
                    v,
                    backgroundUT,
                    "transition-to-background");
                isOnRails = true;
            }
            else if (isSurfaceVessel)
            {
                ParsekLog.Info("Recorder",
                    $"Background transition: surface vessel (sit={v.situation}), skipping orbit segment");
            }

            if (v != null)
                RefreshFinalizationCache(v, "transition_to_background", force: true);

            // M2 run manifest (plan D3 rule 3): a leg that transits background
            // can never complete its END half trustworthily - VOID the manifest
            // on the tree recording (Warn-logged inside) and drop the pending
            // working copy. The analysis presence gate degrades the tree to
            // legacy behavior for this lineage.
            RouteProofCapture.VoidRunManifestForBackgroundTransition(
                ActiveTree,
                ActiveTree?.ActiveRecordingId);
            pendingRouteRunManifest = null;
            // Harvest windows die with the backgrounded leg: they only land on
            // a recording through an active-stop capture, which a BG transit
            // never produces (the flush runs with CaptureAtStop == null). The
            // voided manifest already degrades this tree to legacy analysis.
            if (recorderHarvestWindows.Count > 0 || openHarvestWindow != null)
            {
                ParsekLog.Warn("Recorder",
                    $"Harvest windows discarded on background transition: " +
                    $"recording={ActiveTree?.ActiveRecordingId ?? "(none)"} " +
                    $"windows={recorderHarvestWindows.Count} " +
                    $"open={(openHarvestWindow != null ? "1" : "0")}");
                recorderHarvestWindows.Clear();
                openHarvestWindow = null;
            }

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
        /// <see cref="FindVesselByPid"/> plus the launch-identity gate: returns the live vessel
        /// carrying the recording's <c>VesselPersistentId</c> ONLY when it is not conclusively a
        /// different launch (<see cref="AnchorDetector.LiveAnchorLaunchMatches"/>).
        ///
        /// <para>
        /// Use this wherever a live vessel is looked up FROM a recording to supply that
        /// recording's world pose (parent-anchor pose, debris proximity, anchor candidates). A
        /// bare pid lookup is craft-baked: with the world preserved across Re-Fly, a relaunch of
        /// the same craft answers the lookup after the recorded vessel is gone, and its pose then
        /// drives Relative frames / proximity tiers for a vessel it has nothing to do with. A
        /// null result is the same "parent not live" case those call sites already handle
        /// (recorded-anchor fallback / TTL end), so the failure mode is a graceful degrade rather
        /// than a corrupted frame. Unknown launch Guid on either side = prior pid-only behaviour.
        /// </para>
        /// </summary>
        internal static Vessel FindLaunchMatchedVesselForRecording(Recording rec)
        {
            if (rec == null || rec.VesselPersistentId == 0u)
                return null;
            Vessel vessel = FindVesselByPid(rec.VesselPersistentId);
            if (vessel == null)
                return null;
            if (AnchorDetector.LiveAnchorLaunchMatches(
                    rec, AnchorDetector.TryReadLiveVesselGuid(vessel)))
            {
                return vessel;
            }

            ParsekLog.VerboseRateLimited("Anchor", "live-vessel-launch-mismatch",
                $"Live vessel pid={rec.VesselPersistentId} rejected for rec={rec.RecordingId ?? "(none)"}: " +
                $"recorded launch guid differs conclusively (relaunch of the same craft)",
                5.0);
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

        // Test-only seam: lets xUnit drive the resume-anchor downgrade branch
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
            RecordedAnchorPoseOverrideForTesting = null;
        }

        internal static VesselSwitchDecision DecideOnVesselSwitch(
            uint recordingVesselId, uint currentVesselId, bool currentIsEva,
            bool recordingStartedAsEva, RecordingTree activeTree = null)
        {
            if (currentVesselId == recordingVesselId)
                return VesselSwitchDecision.None;
            // Avoid mixed-mode tracks (ship trajectory followed by EVA walking).
            // Continue-on-EVA is only valid for recordings that started as EVA.
            if (currentIsEva && recordingStartedAsEva)
                return VesselSwitchDecision.ContinueOnEva;
            // EVA kerbal boarded a vessel: ParsekFlight's tree board merge consumes it when the
            // boarding is confirmed, otherwise it ends as a normal stop.
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
