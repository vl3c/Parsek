using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Main flight-scene controller for Parsek.
    /// Handles recording, playback, timeline management, and UI.
    /// </summary>
    // [ERS-exempt — Phase 3] ParsekFlight is the top-level playback host: its
    // ghostStates dictionary is keyed by committed recording index, and it hands
    // off the raw list to WatchModeController / policy
    // events that share the same index space. Routing through
    // EffectiveState.ComputeERS() file-wide would require simultaneous
    // refactors across GhostPlaybackEngine, ghostStates, and every ParsekFlight
    // -> policy event handoff; deferred to a recording-id-keyed pass.
    // TODO(phase 6+): migrate ParsekFlight ghost-state dicts + event indices
    // to recording-id keying and route every read here through ComputeERS().
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public partial class ParsekFlight : MonoBehaviour
    {
        internal enum DeferredSplitCheckTrigger
        {
            None,
            JointBreak,
            DecoupleCreatedVessel
        }

        internal enum AutoRecordLaunchDecision
        {
            SkipAlreadyRecording,
            SkipInactiveVessel,
            SkipBounce,
            SkipNotLaunchTransition,
            SkipDisabled,
            SkipTimeJumpTransient,
            StartFromPrelaunch,
            StartFromSettledLanded
        }

        // Auto-record trigger for staging while still on the pad. The launch trigger
        // (EvaluateAutoRecordLaunchDecision) only fires on the ACTIVE vessel's
        // PRELAUNCH->FLYING transition, so a stage that detaches debris (e.g. SRBs
        // that fly off the pad) before the main stage lifts off is never recorded.
        // Triggering on the first staging action while PRELAUNCH starts the recorder
        // before that decouple so the debris is captured as a branch of the main.
        internal enum AutoRecordStagingDecision
        {
            SkipAlreadyRecording,
            SkipTimeJumpTransient,
            SkipNoActiveVessel,
            SkipNotOnPad,
            SkipDisabled,
            StartFromPrelaunchStaging
        }

        internal enum CommittedSpawnedVesselRestoreAction
        {
            None,
            PromoteFromBackground,
            ResumeActiveRecording
        }

        public enum EvaBackgroundParentRouteDecision
        {
            NotHandled,
            HandledSkipPendingSplit,
            HandledUntrackedSource,
            HandledInvalidMapEntry,
            HandledMissingCrewName,
            StartDeferredBackgroundParentBranch
        }

        public enum EvaBackgroundParentFocusDecision
        {
            Invalid,
            EvaKerbalActive,
            SourceVesselActive
        }

        /// <summary>
        /// Board-time rebind of an EVA branch recording that is still only tracked in
        /// <see cref="RecordingTree.BackgroundMap"/>. The board merge
        /// (<see cref="HandleTreeBoardMerge"/>) needs the EVA recording to be the tree's
        /// ACTIVE recording behind a live recorder; the post-switch first-modification
        /// watcher normally promotes it, but a kerbal that hops out and re-boards without
        /// moving never trips a trigger, so the promotion must also happen at the board.
        /// </summary>
        internal enum EvaBoardPromotionDecision
        {
            /// <summary>Live recorder already owns the boarding EVA kerbal - nothing to do.</summary>
            NotNeeded,
            Promote,
            SkipNoTree,
            SkipRestoring,
            SkipNotEva,
            SkipGhostVessel,
            SkipRecorderBusy,
            SkipEvaNotActiveVessel,
            SkipNoTrackedRecording,

            /// <summary>The tree still names some OTHER recording as its active one even though
            /// no live recorder owns it (e.g. a merge child whose StartRecording failed - see
            /// <c>CreateMergeBranch</c> step 10, which nulls the recorder but leaves
            /// <c>ActiveRecordingId</c> set). Promoting here would overwrite that id, and
            /// <see cref="PromoteRecordingFromBackground"/> never parks the previous value back
            /// into BackgroundMap, so the earlier recording would be silently orphaned.</summary>
            SkipTreeActiveRecordingBusy
        }

        internal static ParsekFlight Instance { get; private set; }

        /// <summary>
        /// #431: returns the id of the currently-live recording for GameStateEvent tagging.
        /// Empty string when there is no active flight, no active tree, or the tree has no active leaf.
        /// The secondary fallback to <see cref="PendingTreeSlot.PendingTree"/> while
        /// <see cref="PendingTreeState.LimboVesselSwitch"/> is owned by the Emit resolver —
        /// this accessor only exposes the live flight-scene value.
        /// </summary>
        internal static string GetActiveRecordingIdForTagging()
            => Instance?.activeTree?.ActiveRecordingId ?? "";

        /// <summary>
        /// BUG #2: pure id-selection guard for
        /// <see cref="GetLiveResumedRecordingIdForDialog"/>. Returns the active
        /// recording id only when a recorder is actively recording AND there is an
        /// active tree id; null otherwise. Extracted so the guard is unit-testable
        /// without a Unity <see cref="Instance"/>.
        /// </summary>
        internal static string ResolveLiveResumedRecordingId(
            bool recorderIsRecording, string activeRecordingId)
            => recorderIsRecording && !string.IsNullOrEmpty(activeRecordingId)
                ? activeRecordingId
                : null;

        /// <summary>
        /// BUG #2: the currently-live resumed recording id the pre-switch Merge/Discard
        /// dialog ties its live-segment duration to. Returns the active tree's leaf id
        /// only while a recorder is actively recording; null off-Unity (Instance null),
        /// when not recording, or when there is no active tree. The dialog's
        /// <c>tree.Recordings.ContainsKey</c> guard then confirms the id belongs to the
        /// tree being rendered (cross-tree safety).
        /// </summary>
        internal static string GetLiveResumedRecordingIdForDialog()
            => Instance != null && Instance.recorder != null
               && Instance.activeTree != null
                ? ResolveLiveResumedRecordingId(
                    Instance.recorder.IsRecording, Instance.activeTree.ActiveRecordingId)
                : null;

        /// <summary>
        /// BUG #2: UT at which the current recorder session began appending (the
        /// live-resume anchor). <c>double.NaN</c> off-Unity (Instance null) or when no
        /// resume/promote session armed it; the dialog falls back to the tree-wide
        /// duration on NaN.
        /// </summary>
        internal static double GetLiveResumeSessionStartUT()
            => Instance != null ? Instance.liveResumeSessionStartUT : double.NaN;

        /// <summary>
        /// BUG #2: reads the current Planetarium UT for the live-resume anchor, returning
        /// <c>double.NaN</c> when no Planetarium singleton exists (headless / pre-scene).
        /// A NaN anchor falls the dialog back to the safe tree-wide duration.
        /// </summary>
        private static double SafeCurrentUtForResumeAnchor()
        {
            try { return Planetarium.GetUniversalTime(); }
            catch { return double.NaN; }
        }

        /// <summary>
        /// #431: true when the flight scene has a live tree with a recorder attached.
        /// Used by <see cref="GameStateRecorder.Emit"/> to decide whether an in-flight
        /// untagged event is a drift signal worth a warn log.
        /// </summary>
        internal static bool HasLiveRecorderForTagging()
            => Instance != null && Instance.activeTree != null && Instance.recorder != null && Instance.recorder.IsRecording;

        /// <summary>
        /// True when <paramref name="persistentId"/> is the live recorder's vessel or a
        /// background member of the active tree; false off-flight or for 0.
        /// </summary>
        internal static bool IsLiveFlightVesselForTagging(uint persistentId)
            => persistentId != 0u && Instance != null && Instance.activeTree != null
                && ((Instance.recorder != null && Instance.recorder.RecordingVesselId == persistentId)
                    || (Instance.activeTree.BackgroundMap != null
                        && Instance.activeTree.BackgroundMap.ContainsKey(persistentId)));

        /// <summary>
        /// #466: returns true whenever the flight scene is carrying an uncommitted tree,
        /// including outsider-state restores where <see cref="recorder"/> is intentionally
        /// null until the player returns to a tracked vessel.
        /// </summary>
        internal static bool HasUncommittedTreeForKspPatchDeferral()
            => Instance != null
                && Instance.activeTree != null
                && !RecordingStore.CommittedTrees.Contains(Instance.activeTree);

        /// <summary>
        /// The live active tree when it is not a committed tree (the same test as
        /// <see cref="HasUncommittedTreeForKspPatchDeferral"/>), else null. Read by the
        /// live-pool reputation seed to name the recordings whose hits the pool holds.
        /// </summary>
        internal static RecordingTree UncommittedActiveTreeForLedger()
            => HasUncommittedTreeForKspPatchDeferral() ? Instance.activeTree : null;

        /// <summary>
        /// Returns true when <paramref name="recordingId"/> belongs to the currently-live
        /// active tree. Used by the post-epoch timeline visibility helpers so events from
        /// a reverted-away flight stay hidden until the matching quickload-resume tree is
        /// restored.
        /// </summary>
        internal static bool IsActiveTreeRecordingId(string recordingId)
            => !string.IsNullOrEmpty(recordingId)
                && Instance?.activeTree?.Recordings != null
                && Instance.activeTree.Recordings.ContainsKey(recordingId);

        internal static bool IsActiveTreeRecordingIdForTree(string recordingId, string treeId)
            => !string.IsNullOrEmpty(recordingId)
                && !string.IsNullOrEmpty(treeId)
                && string.Equals(Instance?.activeTree?.Id, treeId, StringComparison.Ordinal)
                && Instance.activeTree.Recordings != null
                && Instance.activeTree.Recordings.ContainsKey(recordingId);

        internal const string MODID = "Parsek_NS";
        internal const string MODNAME = "Parsek";

        // Recording (delegated to FlightRecorder)
        internal FlightRecorder recorder;

        /// <summary>
        /// BUG #2: UT at which the CURRENT recorder session began appending.
        /// Captured per-session-start in <see cref="PrepareSessionStateForRecorderStart"/>
        /// (reset to NaN there) and re-armed only on the no-session resume / promote
        /// paths (<see cref="ResumeCommittedActiveRecording"/>,
        /// <see cref="PromoteRecordingFromBackground"/>). The pre-switch Merge/Discard
        /// dialog reads it (via <see cref="GetLiveResumeSessionStartUT"/>) to show the
        /// short live segment the player just flew instead of the resumed committed
        /// recording's whole multi-year span — that recording's Points are appended in
        /// place, so its <c>StartUT</c> is the original launch UT and is the wrong
        /// anchor. Session-transient (NOT serialized); NaN falls the dialog back to the
        /// safe tree-wide duration.
        /// </summary>
        private double liveResumeSessionStartUT = double.NaN;
        internal List<TrajectoryPoint> recording => recorder?.Recording ?? noRecording;
        internal List<OrbitSegment> orbitSegments => recorder?.OrbitSegments ?? noOrbitSegments;
        private static readonly List<TrajectoryPoint> noRecording = new List<TrajectoryPoint>();
        private static readonly List<OrbitSegment> noOrbitSegments = new List<OrbitSegment>();

        // Auto-record from LANDED: tracks when the active vessel last entered LANDED state.
        // Only triggers auto-record if the vessel was settled for at least this long,
        // filtering out physics bounces (rovers, crashes, EVA kerbals).
        private double lastLandedUT = -1;
        internal const double LandedSettleThreshold = 5.0; // seconds
        private const double PostSwitchManifestEvaluateNextFrameUt = 0.0;
        private const float CommittedSpawnedRestoreRetryIntervalSeconds = 1.0f;
        internal const double MissedVesselSwitchRecoveryRecStateIntervalSeconds = 5.0;

        // Set true in OnSceneChangeRequested — suppresses Update() to prevent
        // ghost spawns and other processing into the dying scene.
        private bool sceneChangeInProgress;
        private float nextCommittedSpawnedRestoreRetryAt;
        // Marks a world-first the committed timeline credits complete in stock when the
        // flight clock passes its row (no flight recalc runs at 1x, and one under a live
        // recorder is deferred), so a re-earning flight is not paid for it again.
        private readonly CommittedMilestoneClockCursor committedMilestoneClock =
            new CommittedMilestoneClockCursor("flight");
        // Pid of the active vessel the M-A2 DiscardTree seam verb asked to leave idle
        // (0 = none). While it matches the active vessel, TryRestoreCommittedTreeForSpawnedActiveVessel
        // refuses to re-adopt that vessel's committed tree; otherwise the 1 Hz Update
        // retry re-clones it within a second of the discard. Instance-scoped, so a scene
        // load (a new ParsekFlight) drops it; cleared per frame by
        // ResolveCommittedSpawnedRestoreSuppressionClearReason.
        private uint committedSpawnedRestoreSuppressedPid;
        private MissedVesselSwitchRecoveryDiagnosticContext currentVesselSwitchRecoveryDiagnosticContext;

        // Captured at Start when FlightDriver.StartupBehaviour reports a fresh
        // launch. Identifies the one specific vessel that was rolled out from
        // the editor for this FLIGHT scene, so the committed-tree restore guard
        // skips ONLY that vessel's pid — switching to another already-spawned
        // committed vessel mid-scene still resumes that vessel's recording.
        // Zero if the scene was loaded from a save / revert (RESUME_SAVED_*).
        private uint freshRolloutVesselPid;

        // Deferred watch target after fast-forward — the ghost needs one frame
        // to be positioned after the time jump before we can enter watch mode.
        // Uses recording ID (not index) to be safe against list reordering.
        internal string pendingWatchAfterFFId;

        // Manual playback state (preview of current recording)
        internal bool isPlaying = false;
        internal double playbackStartUT;
        internal double recordingStartUT;
        internal GameObject ghostObject;
        internal int lastPlaybackIndex = 0;

        // Ghost state is owned by the engine (T25). This property forwards to the engine field
        // so existing ParsekFlight code accesses engine-owned collections transparently.
        internal Dictionary<int, GhostPlaybackState> ghostStates => engine.ghostStates;

        // Cached TimelineGhosts dictionary — rebuilt once per frame on first access (T20)
        private Dictionary<int, GameObject> cachedTimelineGhosts = new Dictionary<int, GameObject>();
        private int cachedTimelineGhostsFrame = -1;

        // Real Spawn Control: proximity-based nearby craft detection
        internal readonly List<NearbySpawnCandidate> nearbySpawnCandidates = new List<NearbySpawnCandidate>();
        internal const double NearbySpawnRadius = 250.0;
        // Real Spawn Control: maximum relative speed (m/s) between the active vessel and a
        // ghost for the ghost to qualify as a spawn candidate. Tight enough to require a
        // deliberate rendezvous (matched orbits, station-keeping) before fast-forward is offered.
        // Computed frame-agnostically as d(active_pos - ghost_pos)/dt across consecutive
        // proximity scans — see CollectNearbySpawnCandidates.
        internal const double MaxRelativeSpeed = 2.0;
        // Real Spawn Control: NearbySpawnRadius is both the spawn gate and the list bound -
        // a ghost farther away cannot spawn from here, so the window does not list it. The
        // proximity scan still SAMPLES ghosts out to NearbySpawnTrackRadius, so a ghost that
        // closes inside the radius is listed on that scan with its speed already measured.
        // A listed ghost passing faster than MaxRelativeSpeed (up to MaxListRelativeSpeed)
        // stays in the list with a greyed "Too fast" row: closing the speed is what fixes it.
        internal const double NearbySpawnTrackRadius = 1000.0;      // 4x the spawn radius
        internal const double MaxListRelativeSpeed = 50.0;          // active-rendezvous range
        // Per-recording position samples from the prior proximity scan, used to derive a
        // frame-agnostic relative speed without depending on which frame
        // GhostPlaybackState.lastInterpolatedVelocity happens to be in (it varies by
        // playback path — orbit-only ghosts never seed it; trajectory-replay velocity is
        // not guaranteed surface-relative).
        internal struct ProximityVelocitySample
        {
            public Vector3d activePos;
            public Vector3d ghostPos;
            public float time;
        }

        internal struct RelativeAnchorPose
        {
            public Vector3d worldPos;
            public Quaternion worldRotation;
            public bool fromRecordedTrajectory;
            public bool hasSurfacePose;
            public CelestialBody bodyBefore;
            public CelestialBody bodyAfter;
            public double latBefore, lonBefore, altBefore;
            public double latAfter, lonAfter, altAfter;
            public float t;
            public Quaternion surfaceRot;
        }

        internal sealed class RelativeAnchorPoseSnapshot
        {
            public Vector3d worldPos;
            public Quaternion worldRotation;
            public bool hasSurfacePose;
            public CelestialBody bodyBefore;
            public CelestialBody bodyAfter;
            public double latBefore, lonBefore, altBefore;
            public double latAfter, lonAfter, altAfter;
            public float t;
            public Quaternion surfaceRot;

            public static RelativeAnchorPoseSnapshot FromRecordedPose(RelativeAnchorPose pose)
            {
                if (!pose.fromRecordedTrajectory)
                    return null;

                return new RelativeAnchorPoseSnapshot
                {
                    worldPos = pose.worldPos,
                    worldRotation = pose.worldRotation,
                    hasSurfacePose = pose.hasSurfacePose,
                    bodyBefore = pose.bodyBefore,
                    bodyAfter = pose.bodyAfter,
                    latBefore = pose.latBefore,
                    lonBefore = pose.lonBefore,
                    altBefore = pose.altBefore,
                    latAfter = pose.latAfter,
                    lonAfter = pose.lonAfter,
                    altAfter = pose.altAfter,
                    t = pose.t,
                    surfaceRot = pose.surfaceRot
                };
            }
        }

        internal readonly Dictionary<string, ProximityVelocitySample> proximityVelocitySamples =
            new Dictionary<string, ProximityVelocitySample>();
        internal readonly HashSet<string> notifiedSpawnRecordingIds = new HashSet<string>();
        internal int proximityCheckGeneration;
        internal int ProximityCheckGeneration => proximityCheckGeneration;
        internal List<NearbySpawnCandidate> NearbySpawnCandidates => nearbySpawnCandidates;

        // --- Floating-origin correction ---
        // Ghosts are plain GameObjects not registered with KSP's FloatingOrigin.
        // FloatingOrigin shifts all registered objects in LateUpdate(), but ghosts
        // positioned in Update() would be left behind, causing one-frame jumps.
        // We store each ghost's last positioning inputs and re-apply in LateUpdate()
        // so the position is correct in the post-shift frame that actually renders.

        internal enum GhostPosMode { PointInterp, SinglePoint, Orbit, Surface, Relative, CheckpointPoint }

        internal enum GhostPositionReapplyPhase { LateUpdate, CameraPreCull }

        internal struct GhostPosEntry
        {
            public GameObject ghost;
            public GhostPosMode mode;
            public string recordingId;
            public int recordingIndex;
            public bool hasRecordingIndex;

            // PointInterp fields
            public CelestialBody bodyBefore;
            public CelestialBody bodyAfter;
            public double latBefore, lonBefore, altBefore;
            public double latAfter, lonAfter, altAfter;
            public float t;
            public double pointUT;
            public double pointDeltaTimeSeconds;
            public Quaternion interpolatedRot;
            public Vector3 velocityBefore;
            public Vector3 velocityAfter;

            // SinglePoint fields (also used body/lat/lon/alt from "before")
            // (uses bodyBefore, latBefore, lonBefore, altBefore, pointUT, interpolatedRot)


            // Orbit fields
            public long orbitCacheKey;
            public double orbitUT;
            public Vector3d orbitContinuityOffset;
            public bool hasOrbitContinuityOffset;
            public double orbitContinuityAnchorUT;
            public double orbitContinuityBlendSeconds;

            // Orbital rotation fields (Phase: orbital-rotation)
            public Quaternion orbitFrameRot;       // Orbital-frame-relative rotation from segment
            public bool hasOrbitFrameRot;          // True if segment has rotation data
            public CelestialBody orbitBody;        // Body reference for radial-out computation in LateUpdate
            public Vector3 orbitAngularVelocity;   // Vessel-local angular velocity (rad/s)
            public bool isSpinning;                // True if above spin threshold
            public double orbitSegmentStartUT;     // Segment start UT for computing dt
            public Quaternion boundaryWorldRot;    // Pre-computed boundary world rotation (for spinning case)

            // Surface fields
            public Quaternion surfaceRot; // body-relative rotation

            // Relative fields
            public uint anchorVesselId;           // anchor vessel PID for re-lookup in LateUpdate
            public double relDx, relDy, relDz;    // interpolated offset (meters)
            public Quaternion relativeRot;         // interpolated relative rotation
            public string relativeBodyName;        // body name for altitude computation
            public RelativeAnchorPoseSnapshot relativeRecordedAnchorPose;
            public string relativeAnchorRecordingId;
            public bool relativeLoopLiveAnchor;     // true only for loop playback's explicit live-PID contract

            // Phase 1-3 lookup key for LateUpdate re-evaluation (design doc
            // §6.1 / §6.2 / §6.3 / §6.4 / §7.1 / §8 / §18 Phase 1-3 + Phase 4).
            // The LateUpdate FloatingOrigin re-positioning recomputes the
            // ghost's world position from lat/lon/alt. Without re-evaluation
            // here, two regressions return:
            //   1. Phase 2+3 anchor ε would snap back to the un-corrected
            //      lerp position (LateUpdate must re-add ε every late frame).
            //      Phase 3 makes ε time-varying along a both-end lerp interval
            //      (§6.4), so caching a single Vector3d at registration time
            //      would freeze the lerp progression for every late-frame
            //      within the segment.
            //   2. Phase 1+4 spline positioning would be discarded — the raw
            //      latBefore/After lerp would overwrite the spline-positioned
            //      transform every LateUpdate. The same lookup key drives the
            //      spline re-eval and the anchor re-add, so both are stored
            //      together. An empty recordingId or negative sectionIndex
            //      means the registration-time guards (no recording context
            //      passed in, or both gates closed) skipped both paths.
            public string anchorRecordingId;
            public int anchorSectionIndex;

        }

        internal readonly List<GhostPosEntry> ghostPosEntries = new List<GhostPosEntry>();
        private int ghostPreCullReapplyFrame = -1;
        private int ghostLateUpdateReapplyFrame = -1;

        internal void AddOrReplaceGhostPosEntry(GhostPosEntry entry)
        {
            if (entry.ghost == null)
                return;

            int removed = 0;
            for (int i = ghostPosEntries.Count - 1; i >= 0; i--)
            {
                if (ghostPosEntries[i].ghost != entry.ghost)
                    continue;

                ghostPosEntries.RemoveAt(i);
                removed++;
            }

            if (removed > 0)
            {
                ParsekLog.VerboseRateLimited(
                    "Playback",
                    "ghost-pos-entry-replaced|" + (entry.recordingId ?? "<no-rec>"),
                    "Ghost position reapply entry replaced: recording="
                    + ParsekFlightGhostPositioner.ShortRecordingId(entry.recordingId)
                    + " mode=" + entry.mode
                    + " removed=" + removed.ToString(CultureInfo.InvariantCulture)
                    + " reason=same-frame-latest-position-wins",
                    5.0);
            }

            ghostPosEntries.Add(entry);
        }

        // Auto-record: EVA from pad triggers recording after vessel switch completes
        internal bool pendingAutoRecord = false;
        internal PostSwitchAutoRecordState postSwitchAutoRecord;

        // Boarding confirmation via onCrewBoardVessel
        internal uint pendingBoardingTargetPid; // vessel PID from onCrewBoardVessel, 0 = none
        internal int boardingConfirmFrames;     // frames since boarding event (auto-clear after 10)

        // Pending dock transition (set by OnPartCouple, consumed by HandleTreeDockMerge)
        internal uint pendingDockMergedPid;          // merged vessel pid, 0 = no pending dock
        internal int dockConfirmFrames;              // frame counter for confirmation window (auto-clear after 5)

        // Recording tree mode (null when not in tree mode)
        internal RecordingTree activeTree;

        /// <summary>
        /// Read-only access to the currently-active recording tree, for ParsekScenario.OnSave
        /// to serialize as a PARSEK_ACTIVE_TREE-flagged node (quickload-resume support).
        /// Null when not in tree mode.
        /// </summary>
        internal RecordingTree ActiveTreeForSerialization => activeTree;

        /// <summary>
        /// Read-only access to the current active recorder, for ParsekScenario.OnSave
        /// to check whether serialization of the active tree is appropriate and to flush
        /// buffered recorder data into the tree before writing.
        /// </summary>
        internal FlightRecorder ActiveRecorderForSerialization => recorder;

        /// <summary>
        /// The recorder a deferred split parked (EVA / undock / decouple, until the split finishes
        /// next frame), for <c>ParsekScenario.SaveActiveTreeIfAny</c>; null otherwise.
        /// </summary>
        internal FlightRecorder ParkedSplitRecorderForSerialization => pendingSplitRecorder;

        /// <summary>
        /// Lays the parked split recorder's unflushed buffers over the active tree's current
        /// recording for one save (audit R6, <see cref="ParkedSplitSaveOverlay"/>), or returns null
        /// when no overlay applies (logged with the reason). The caller restores it after the write.
        /// </summary>
        internal ParkedSplitSaveOverlay BeginParkedSplitSaveOverlay()
        {
            FlightRecorder parked = pendingSplitRecorder;
            string skip = ParkedSplitSaveOverlay.ResolveSkipReason(
                liveRecorderPresent: recorder != null,
                parkedRecorderPresent: parked != null,
                activeTree: activeTree,
                parkedRecorderTree: parked?.ActiveTree,
                parkedRecorderVesselPid: parked != null ? parked.RecordingVesselId : 0u,
                out Recording target);
            if (skip != null)
            {
                if (parked != null)
                    ParsekLog.Warn("Flight",
                        $"BeginParkedSplitSaveOverlay: save during a deferred split not overlaid ({skip}); " +
                        "the parked recorder's unflushed data is not in this save");
                return null;
            }

            ParkedSplitSaveOverlay overlay = ParkedSplitSaveOverlay.Apply(
                target,
                parked.Recording,
                parked.OrbitSegments,
                parked.PartEvents,
                parked.FlagEvents,
                parked.TrackSections,
                parked.lastEmittedTerminalEvents);
            ParsekLog.Info("Flight",
                $"BeginParkedSplitSaveOverlay: save during a deferred split; parked recorder data laid over " +
                $"the active recording for this write ({overlay.Describe()})");
            return overlay;
        }

        /// <summary>
        /// The live tree's leg recording the live vessel <paramref name="vesselPid"/> right now
        /// (the active recorder's leg, else its background leg), or null. The vessel names live
        /// write rule reads it as the vessel's current leg (design 7.4 step 3).
        /// </summary>
        internal string ResolveRecordingLegIdForLiveVessel(uint vesselPid)
            => VesselNameLiveSync.ResolveRecordingLegId(
                activeTree,
                recorder != null && recorder.IsRecording,
                recorder != null ? recorder.RecordingVesselId : 0u,
                vesselPid);

        /// <summary>
        /// Captures a structured snapshot of every recorder-relevant field at the
        /// current instant. Used by <see cref="RecorderStateLog.RecState"/> emit sites for
        /// the structured <c>[RecState]</c> diagnostic dump. Pure data gather — no
        /// side effects, safe to call from any lifecycle event handler.
        /// </summary>
        internal RecorderStateSnapshot CaptureRecorderState() =>
            RecorderStateSnapshot.CaptureFromParts(
                activeTree,
                recorder,
                PendingTreeSlot.PendingTree,
                PendingTreeSlot.PendingTreeStateValue,
                null, // standalone pending removed (T56)
                pendingSplitRecorder,
                pendingSplitInProgress,
                Planetarium.GetUniversalTime(),
                HighLogic.LoadedScene);

        internal bool HasArmedPostSwitchAutoRecordWatch => postSwitchAutoRecord != null;

        // The post-switch auto-record watch's behaviour (ParsekFlightPostSwitchAutoRecord); the
        // watch state above stays here. Created on first use; holds only this host.
        private ParsekFlightPostSwitchAutoRecord postSwitchAutoRecorder;
        internal ParsekFlightPostSwitchAutoRecord PostSwitchAutoRecorder =>
            postSwitchAutoRecorder ?? (postSwitchAutoRecorder = new ParsekFlightPostSwitchAutoRecord(this));

        // PhysicsFramePatch and the in-game tests call this on the flight; the watch lives on
        // PostSwitchAutoRecorder.
        internal void OnPostSwitchAutoRecordPhysicsFrame(Vessel v) =>
            PostSwitchAutoRecorder.OnPostSwitchAutoRecordPhysicsFrame(v);

        internal bool IsPostSwitchAutoRecordArmedForPid(uint vesselPid)
        {
            return postSwitchAutoRecord != null
                && vesselPid != 0
                && postSwitchAutoRecord.VesselPid == vesselPid;
        }

        /// <summary>
        /// Flushes the active recorder's buffered points/events into the active tree's
        /// current recording, WITHOUT stopping the recorder or clearing its state. Called
        /// by <see cref="ParsekScenario.OnSave"/> via <c>SaveActiveTreeIfAny</c> so the
        /// serialized active tree reflects the latest in-flight data.
        ///
        /// Unlike <see cref="FlushRecorderToTreeRecording"/> which also sets
        /// <c>IsRecording = false</c>, this variant leaves the recorder running so the
        /// user's current flight continues uninterrupted after the save completes.
        /// The recorder's buffers are cleared so subsequent frames don't re-flush the
        /// same data on the next save.
        /// </summary>
        internal void FlushRecorderIntoActiveTreeForSerialization()
        {
            if (recorder == null || activeTree == null) return;

            RecorderStateLog.RecState("FlushRecorderIntoActiveTree:entry", CaptureRecorderState());
            string recId = activeTree.ActiveRecordingId;
            if (recId == null)
            {
                ParsekLog.Verbose("Flight",
                    "FlushRecorderIntoActiveTreeForSerialization: no active recording id in tree — skipping");
                return;
            }

            if (!activeTree.Recordings.TryGetValue(recId, out var treeRec))
            {
                ParsekLog.Warn("Flight",
                    $"FlushRecorderIntoActiveTreeForSerialization: active recording id '{recId}' not found in tree");
                return;
            }

            // Persist the currently-open TrackSection as a closed segment before we append
            // recorder.TrackSections into the tree. Without this, mid-flight saves drop the
            // active sparse trajectory chunk, and quickload playback can freeze or drift
            // across the missing interval (#327).
            recorder.CheckpointOpenTrackSectionForSerialization(Planetarium.GetUniversalTime());

            int prevPointCount = treeRec.Points.Count;
            int prevEventCount = treeRec.PartEvents.Count;

            // Append buffered data from the live recorder into the tree recording
            treeRec.Points.AddRange(recorder.Recording);
            treeRec.OrbitSegments.AddRange(recorder.OrbitSegments);
            treeRec.PartEvents.AddRange(recorder.PartEvents);
            treeRec.FlagEvents.AddRange(recorder.FlagEvents);
            treeRec.TrackSections.AddRange(recorder.TrackSections);

            // Sort events chronologically — mixed sources can produce out-of-order data.
            // PartEvents MUST use a STABLE sort so same-UT terminal Shutdowns stay before
            // continuation seed EngineIgnited events (#287). LINQ OrderBy is stable;
            // List<T>.Sort(Comparison) is not. FlagEvents uses the same stable pattern
            // for consistency across every flush/merge site.
            var sortedPartEvents = FlightRecorder.StableSortPartEventsByUT(treeRec.PartEvents);
            treeRec.PartEvents.Clear();
            treeRec.PartEvents.AddRange(sortedPartEvents);
            var sortedFlagEvents = FlightRecorder.StableSortByUT(treeRec.FlagEvents, e => e.ut);
            treeRec.FlagEvents.Clear();
            treeRec.FlagEvents.AddRange(sortedFlagEvents);

            // Populate VesselPersistentId if not already set
            if (treeRec.VesselPersistentId == 0 && recorder.RecordingVesselId != 0)
                treeRec.VesselPersistentId = recorder.RecordingVesselId;

            // Copy start location fields from recorder if not already set on the tree recording.
            // FlushRecorderToTreeRecording (normal stop path) copies these from the captured
            // recording, but this serialization flush runs during OnSave before BuildCaptureRecording.
            if (string.IsNullOrEmpty(treeRec.StartBodyName))
                treeRec.StartBodyName = recorder.StartBodyName;
            if (string.IsNullOrEmpty(treeRec.StartBiome))
                treeRec.StartBiome = recorder.StartBiome;
            if (string.IsNullOrEmpty(treeRec.StartSituation))
                treeRec.StartSituation = recorder.StartSituation;
            if (string.IsNullOrEmpty(treeRec.LaunchSiteName))
                treeRec.LaunchSiteName = recorder.LaunchSiteName;

            // Mark tree recording dirty so the sidecar file is rewritten
            treeRec.FilesDirty = true;

            // Clear the recorder's buffers so subsequent saves don't re-flush the same data.
            // The recorder stays IsRecording=true and will append new frames as they come in.
            int flushedPoints = recorder.Recording.Count;
            int flushedEvents = recorder.PartEvents.Count;
            recorder.Recording.Clear();
            recorder.OrbitSegments.Clear();
            recorder.PartEvents.Clear();
            recorder.FlagEvents.Clear();
            recorder.TrackSections.Clear();

            ParsekLog.Verbose("Flight",
                $"Flushed recorder→active-tree '{recId}' for serialization: " +
                $"{flushedPoints} points, {flushedEvents} part events " +
                $"(tree recording now has {treeRec.Points.Count} points " +
                $"(+{treeRec.Points.Count - prevPointCount}), " +
                $"{treeRec.PartEvents.Count} part events " +
                $"(+{treeRec.PartEvents.Count - prevEventCount}))");
            RecorderStateLog.RecState("FlushRecorderIntoActiveTree:post", CaptureRecorderState());
        }

        // Background recorder for tree mode (null when not in tree mode)
        internal BackgroundRecorder backgroundRecorder;

        // #267: reentrancy guard for RestoreActiveTreeFromPending / ForVesselSwitch.
        // Set at coroutine entry, cleared in finally. Guards OnVesselSwitchComplete,
        // OnVesselWillDestroy, and FinalizeTreeOnSceneChange from mutating activeTree /
        // recorder / backgroundRecorder while the restore coroutine is mid-yield.
        internal static bool restoringActiveTree;

        // Split event detection: deduplication and race-condition guard
        internal double lastBranchUT = -1;
        internal HashSet<uint> lastBranchVesselPids = new HashSet<uint>();
        internal bool pendingSplitInProgress;
        internal FlightRecorder pendingSplitRecorder;
        internal DeferredSplitCheckTrigger pendingDeferredSplitCheckTrigger;
        internal double pendingSplitTriggerUT = double.NaN;
        // Vessel PIDs that existed before a joint break (for filtering pre-existing vessels)
        internal HashSet<uint> preBreakVesselPids;
        // Vessels created by decouple during recording (caught via onPartDeCoupleNewVesselComplete
        // synchronously during Part.decouple(), before KSP's debris cleanup can destroy them).
        // Subscribed for the entire recording session, consumed by the next deferred split check.
        internal List<Vessel> decoupleCreatedVessels;
        // Controller status captured at creation time (vessel parts may be cleared before deferred check)
        internal Dictionary<uint, bool> decoupleControllerStatus;
        // Exact split-time seed points captured from onPartDeCoupleNewVesselComplete.
        // Used to avoid seeding breakup children from the later deferred-check frame.
        internal Dictionary<uint, TrajectoryPoint> decoupleCreatedTrajectoryPoints;
        // Every onPartDeCoupleNewVesselComplete seen while the listener is subscribed, new
        // vessel pid -> the vessel it came off (foreign origins included). The deferred
        // joint-break scan drops a candidate whose origin chain never reaches the recorded
        // vessel (SegmentBoundaryLogic.IsForeignDecoupleOrigin). Cleared by the deferred
        // check and whenever the listener is torn down.
        internal readonly Dictionary<uint, uint> decoupleOriginByNewPid = new Dictionary<uint, uint>();

        // Crash coalescer: groups rapid structural splits into single BREAKUP events
        internal CrashCoalescer crashCoalescer = new CrashCoalescer();

        // Merge event detection (tree mode)
        internal bool pendingTreeDockMerge;           // true when a tree dock merge is pending
        internal uint pendingDockAbsorbedPid;         // PID of absorbed vessel in dock merge
        // Route partner PID resolved from the couple event (independent of BackgroundMap).
        // 0 means partner was foreign or unresolved; non-zero means the merge child should
        // carry a route window. Cleared when the pending dock state is cleared.
        internal uint pendingDockRouteTargetPid;
        // Branch-point partner stamp: the couple-event partner identity, decoupled from
        // route eligibility (design-dock-event-graph.md 6.1). Set WITHOUT the
        // snapshot/known-recording disjunct (EVA grabs still suppress), so a
        // route-ineligible dock records who it docked with. Feeds
        // BranchPoint.TargetVesselPersistentId, and THROUGH that stamp the
        // absorbed-vessel spawn suppression (PHANTOM-SUPERSEDE-RIDES-GATED-PID: the
        // suppression asks who the dock absorbed, which is a partner-identity
        // question, not a route one - see ResolveDockMergeSpawnSuppressionPid; do NOT
        // "restore" it to the gated pid). The ROUTE surfaces - route windows,
        // TransferKind, TransferTargetVesselPid - keep the gated
        // pendingDockRouteTargetPid.
        internal uint pendingDockPartnerPid;
        // Launch guid (Vessel.id) of the vessel pendingDockPartnerPid names, read from the
        // live couple event. Feeds BranchPoint.TargetVesselGuid only; set and cleared with
        // pendingDockPartnerPid. Null when the partner stamp is 0 or its vessel had no id.
        internal string pendingDockPartnerGuid;
        // Connection producer that made the pending couple, classified from the live
        // onPartCouple parts (DockingPort / Grapple / Unknown). Stamped on the merge
        // branch data and the route connection window so admission can gate by
        // producer. None when no couple is pending.
        internal RouteConnectionKind pendingDockTransferKind;
        // Pre-couple partner vessel snapshot, captured in OnPartCouple while data.from
        // and data.to still reference DISTINCT vessels. Used by CreateMergeBranch to
        // build the route window's endpoint baseline from the partner's pre-dock state,
        // instead of inflating it with the post-couple merged vessel (which includes
        // transport parts and gives DOCK_ENDPOINT_RESOURCES = transport + endpoint).
        internal ConfigNode pendingDockPartnerSnapshot;
        internal uint pendingDockPartnerSnapshotPid;
        // Pre-couple SELF (transport / recorded vessel) snapshot, captured in OnPartCouple
        // while data.from and data.to still reference DISTINCT vessels. Mirror of
        // pendingDockPartnerSnapshot for the transport side: CreateMergeBranch uses it to
        // build the route window's DOCK_TRANSPORT_RESOURCES baseline from the transport's
        // pre-dock state, so a same-frame stock crossfeed equalisation that drained the
        // transport tank into the depot does not deflate the baseline and trip the strict
        // MixedPickupDelivery pickup gate on a clean delivery run. Cleared with the partner
        // snapshot.
        internal ConfigNode pendingDockSelfSnapshot;
        internal uint pendingDockSelfSnapshotPid;
        internal RouteEndpoint? pendingDockRouteEndpointAtDock;
        internal int pendingDockRouteEndpointSituation = -1;
        internal bool pendingBoardingTargetInTree;    // true if boarding target is in the tree

        // Docking race condition guard (Task 5 sets, Task 6 checks)
        internal HashSet<uint> dockingInProgress = new HashSet<uint>();

        // Phantom terrain crash detection: tracks pre-pack vessel state.
        // Key: vessel PID, Value: (packUT, pre-pack situation).
        const double PhantomCrashWindowSeconds = 5.0;
        readonly Dictionary<uint, (double packUT, Vessel.Situations situation)> packStates
            = new Dictionary<uint, (double, Vessel.Situations)>();

        // Tree destruction merge dialog guard (prevents duplicate coroutines)
        private bool treeDestructionDialogPending;

        /// <summary>
        /// Holds captured vessel state between Phase 1 (synchronous, inside OnVesselWillDestroy)
        /// and Phase 3 (deferred, after yield return null). Captured by the coroutine state machine
        /// (heap-allocated, one per deferred check).
        /// </summary>
        internal struct PendingDestruction
        {
            public uint vesselPid;
            public string recordingId;
            public double capturedUT;
            public Vessel.Situations situation;

            // Orbit (captured from vessel.orbit before destruction)
            public bool hasOrbit;
            public double inclination;
            public double eccentricity;
            public double semiMajorAxis;
            public double lan;
            public double argumentOfPeriapsis;
            public double meanAnomalyAtEpoch;
            public double epoch;
            public string bodyName;

            // Surface position
            public bool hasSurface;
            public SurfacePosition surfacePosition;

            // True when the death was already classified (and stamped) as an
            // EVA-construction disassembly at the synchronous destroy seam. The
            // deferred pass then only attaches the captured terminal data.
            public bool disassembled;
        }

        internal enum DeferredDestructionOutcome
        {
            ConfirmedDestroyed = 0,
            DockingInProgress = 1,
            FalseDestroyReattach = 2
        }

        // Diagnostic logging guards (log once per state transition, not per frame)
        internal HashSet<long> loggedOrbitSegments = new HashSet<long>();
        internal HashSet<long> loggedOrbitRotationSegments = new HashSet<long>();

        // Anchor vessel tracking moved to engine (T25).
        private HashSet<uint> loadedAnchorVessels => engine.loadedAnchorVessels;

        // Phase 6b: Ghost chain evaluation + vessel ghosting
        internal VesselGhoster vesselGhoster;
        internal Dictionary<uint, GhostChain> activeGhostChains;

        // Deferred spawn queue moved to ParsekPlaybackPolicy (#132)
        // Phase F: timelineResourceReplayPausedLogged removed alongside ApplyResourceDeltas.
        private bool wasWarpActive = false; // tracks previous warp state for exit detection
        private double warpStartUT = 0.0;  // UT when warp began — used for facility visual updates
        private bool hasLastWarpCheckpointEvent;
        private RecordingTree lastWarpCheckpointTree;
        private float lastWarpCheckpointRate;
        private double lastWarpCheckpointUT;
        // Camera follow (watch mode) — extracted to WatchModeController
        internal WatchModeController watchMode;
        internal WatchModeController WatchMode => watchMode;

        // UI
        private Rect windowRect = new Rect(20, 100, 250, 250);
        private bool showUI = false;

        /// <summary>
        /// The toolbar's "is the Parsek UI shown" flag, exposed for in-game tests.
        /// <para>Every window draw in <c>OnGUI</c> sits inside <c>if (showUI)</c>, and nothing
        /// clicks the toolbar in an unattended harness run - so a test that needs a window to
        /// actually DRAW (rather than just flipping its open flag) has to raise this first, and
        /// restore it afterwards. `MissionRevealInGameTests` is the first such test: its subject
        /// is a scroll offset measured from real IMGUI layout rects, which only exist inside a
        /// live draw pass.</para>
        /// </summary>
        internal bool ShowUIForTesting
        {
            get { return showUI; }
            set { showUI = value; }
        }

        /// <summary>
        /// The IMGUI window id the main Parsek window is drawn with. Read by
        /// <c>UiWindowHandle.GetWindowId</c> (wired by
        /// <c>ParsekTestCommandAddon.ResolveWindowHandle</c>) so <c>UiAction op=find</c> can
        /// scope a captured GUI tree to this window's subtree; the sub-windows publish a
        /// <c>WindowIdKey</c> const instead, because theirs is a hash of a literal while
        /// this one is the host MonoBehaviour's own instance id and cannot be a const.
        /// </summary>
        internal int MainWindowIdForTesting => GetInstanceID();

        /// <summary>
        /// The main window's live rect. Read by the automation-only <c>UiAction</c> seam verb
        /// (<c>op=describe</c> reports it, <c>op=rect</c> moves it).
        /// <para>Its SIZE is host-controlled: <c>OnGUI</c> passes a fixed
        /// <c>GUILayout.Width(250)</c>, so a written width is replaced on the next draw. A
        /// written POSITION holds, which is what a census needs - the sub-windows seed their
        /// own default position from this rect.</para>
        /// </summary>
        internal Rect MainWindowRectForTesting
        {
            get { return windowRect; }
            set { windowRect = value; }
        }
        private static ToolbarControl toolbarControl;
        private ParsekUI ui;

        // Ghost playback engine + policy (T25 extraction)
        internal GhostPlaybackEngine engine;
        internal GhostPlaybackEngine Engine => engine;
        internal ParsekPlaybackPolicy policy;

        // The IGhostPositioner the engine is built with: ghost placement and the positioning caches
        // only it reads (ParsekFlightGhostPositioner). Created on first use; holds only this host.
        private ParsekFlightGhostPositioner positioner;
        internal ParsekFlightGhostPositioner Positioner =>
            positioner ?? (positioner = new ParsekFlightGhostPositioner(this));
        private ParsekFlightPlaybackHost playbackHost;
        internal ParsekFlightPlaybackHost PlaybackHost =>
            playbackHost ?? (playbackHost = new ParsekFlightPlaybackHost(this));
        private ParsekFlightSpawnHost spawnHost;
        internal ParsekFlightSpawnHost SpawnHost =>
            spawnHost ?? (spawnHost = new ParsekFlightSpawnHost(this));
        private ParsekFlightSurfaceCapture surfaceCapture;
        internal ParsekFlightSurfaceCapture SurfaceCapture =>
            surfaceCapture ?? (surfaceCapture = new ParsekFlightSurfaceCapture(this));

        // GameEvents handlers subscribed in Start / removed in OnDestroy; the bodies live on SurfaceCapture.
        void OnGroundSciencePartDeployed(ModuleGroundSciencePart deployedPart) =>
            SurfaceCapture.OnGroundSciencePartDeployed(deployedPart);
        void OnGroundSciencePartRemoved(ModuleGroundSciencePart removedPart) =>
            SurfaceCapture.OnGroundSciencePartRemoved(removedPart);
        void OnNewVesselCreatedForGroundPart(Vessel v) => SurfaceCapture.OnNewVesselCreatedForGroundPart(v);
        void OnDeployGroundPart(string partName) => SurfaceCapture.OnDeployGroundPart(partName);
        void OnEditorPartEventForConstructionDrop(ConstructionEventType evt, Part part) =>
            SurfaceCapture.OnEditorPartEventForConstructionDrop(evt, part);
        void OnModuleInventorySlotChanged(ModuleInventoryPart module, int slot) =>
            SurfaceCapture.OnModuleInventorySlotChanged(module, slot);
        void OnEvaConstructionPartAttached(Vessel vessel, Part part) =>
            SurfaceCapture.OnEvaConstructionPartAttached(vessel, part);
        void OnEvaConstructionPartDetached(Vessel vessel, Part part) =>
            SurfaceCapture.OnEvaConstructionPartDetached(vessel, part);
        void OnAfterFlagPlanted(FlagSite flagSite) => SurfaceCapture.OnAfterFlagPlanted(flagSite);

        private ParsekFlightSplitBrancher splitBrancher;
        internal ParsekFlightSplitBrancher SplitBrancher =>
            splitBrancher ?? (splitBrancher = new ParsekFlightSplitBrancher(this));

        // GameEvents handlers subscribed in Start (OnDecoupleNewVesselDuringSplitCheck by the
        // recorder session) and removed in OnDestroy / StopRecording; the bodies live on SplitBrancher.
        void OnCrewBoardVessel(GameEvents.FromToAction<Part, Part> data) => SplitBrancher.OnCrewBoardVessel(data);
        void OnCrewOnEva(GameEvents.FromToAction<Part, Part> data) => SplitBrancher.OnCrewOnEva(data);
        void OnPartCouple(GameEvents.FromToAction<Part, Part> data) => SplitBrancher.OnPartCouple(data);
        void OnPartUndock(Part undockedPart) => SplitBrancher.OnPartUndock(undockedPart);
        void OnVesselsUndocking(Vessel oldVessel, Vessel newVessel) =>
            SplitBrancher.OnVesselsUndocking(oldVessel, newVessel);
        private void OnDecoupleNewVesselDuringSplitCheck(Vessel originalVessel, Vessel newVessel) =>
            SplitBrancher.OnDecoupleNewVesselDuringSplitCheck(originalVessel, newVessel);

        private ParsekFlightMergeFlow mergeFlow;
        internal ParsekFlightMergeFlow MergeFlow =>
            mergeFlow ?? (mergeFlow = new ParsekFlightMergeFlow(this));

        // The MergeDialog.OnTreeCommitted handler (subscribed in Start, removed in OnDestroy) and
        // the entry points other types call (by name too: the in-game tests reflect on the private
        // scene-exit discard and finalize); the bodies live on MergeFlow.
        private void OnTreeCommittedFromMergeDialog(RecordingTree tree) => MergeFlow.OnTreeCommittedFromMergeDialog(tree);
        public void CommitTreeFlight() => MergeFlow.CommitTreeFlight();
        internal bool IsActiveTreeIdleOnPad() => MergeFlow.IsActiveTreeIdleOnPad();
        internal void AutoDiscardIdleActiveTree(string reason) => MergeFlow.AutoDiscardIdleActiveTree(reason);
        internal void AutoDiscardActiveTreeWithMessage(
            string reason,
            string screenMessage,
            string ledgerRecalcReason,
            bool reapSidecars = true) =>
            MergeFlow.AutoDiscardActiveTreeWithMessage(reason, screenMessage, ledgerRecalcReason, reapSidecars);
        internal string LiveRecordingIdForVessel(uint vesselPid) => MergeFlow.LiveRecordingIdForVessel(vesselPid);
        internal void FinalizeTreeOnSceneChangeForCallback(GameScenes scene) =>
            MergeFlow.FinalizeTreeOnSceneChangeForCallback(scene);
        internal void FinalizeTreeOnSceneChangeForTesting(GameScenes scene, double commitUT) =>
            MergeFlow.FinalizeTreeOnSceneChangeForTesting(scene, commitUT);
        private void DiscardActiveTreeForSuppressedSceneExit(
            GameScenes scene,
            double commitUT,
            string suppressReason,
            bool logRecorderState) =>
            MergeFlow.DiscardActiveTreeForSuppressedSceneExit(scene, commitUT, suppressReason, logRecorderState);
        void FinalizeTreeRecordings(RecordingTree tree, double commitUT, bool isSceneExit) =>
            MergeFlow.FinalizeTreeRecordings(tree, commitUT, isSceneExit);

        // UI and test-command entry points; the bodies live on SpawnHost.
        internal void WarpToRecordingEnd(int recordingIndex) => SpawnHost.WarpToRecordingEnd(recordingIndex);
        internal void WarpToDeparture(int recordingIndex, double departureUT) =>
            SpawnHost.WarpToDeparture(recordingIndex, departureUT);
        internal void FastForwardToRecording(Recording rec) => SpawnHost.FastForwardToRecording(rec);
        internal void FastForwardToEventUT(double eventUT, string label) => SpawnHost.FastForwardToEventUT(eventUT, label);
        internal void WarpToNextCraftSpawn() => SpawnHost.WarpToNextCraftSpawn();
        internal bool TimeJumpTo(double targetUT) => SpawnHost.TimeJumpTo(targetUT);

        // Entry points other types call on the flight instance; the bodies live on PlaybackHost.
        public void DestroyAllTimelineGhosts() => PlaybackHost.DestroyAllTimelineGhosts();
        internal List<ReFlyInvokeHold.Entry> HoldReFlyInvokeVessels(
            IList<ReFlyInvokeHold.Entry> entries, out int notLive, out int alreadyHeld) =>
            PlaybackHost.HoldReFlyInvokeVessels(entries, out notLive, out alreadyHeld);
        internal IEnumerable<RecordingAnchorCandidate> BuildGhostRecordingAnchorCandidatesForRecorder(
            RecordingTree focusTree, string focusedRecordingId, uint focusedVesselPid, double ut) =>
            PlaybackHost.BuildGhostRecordingAnchorCandidatesForRecorder(focusTree, focusedRecordingId, focusedVesselPid, ut);
        internal bool TryResolveGhostCommNetRecordingPosition(
            string recordingId, int indexHint, double ut, out Vector3d worldPos) =>
            PlaybackHost.TryResolveGhostCommNetRecordingPosition(recordingId, indexHint, ut, out worldPos);
        internal void PositionBlockedChainTipGhostForInGameTest(
            GameObject ghost, CelestialBody body, Orbit orbit, Quaternion surfaceRelativeRotation,
            double holdLat, double holdLon, double holdAlt, double ut) =>
            PlaybackHost.PositionBlockedChainTipGhostForInGameTest(
                ghost, body, orbit, surfaceRelativeRotation, holdLat, holdLon, holdAlt, ut);
        internal void ClearGhostSkipReasonLogStateForTesting() => PlaybackHost.ClearGhostSkipReasonLogStateForTesting();

        // Accessors for WatchModeController (delegates need host services)
        internal bool ShouldLoopPlaybackForWatch(Recording rec) => GhostPlaybackEngine.ShouldLoopPlayback(rec);
        internal double GetLoopIntervalSecondsForWatch(Recording rec, int recIdx = -1)
        {
            return PlaybackHost.GetLoopIntervalSeconds(rec, recIdx);
        }
        internal bool TryGetLoopScheduleForWatch(
            Recording rec,
            int recIdx,
            out double playbackStartUT,
            out double scheduleStartUT,
            out double duration,
            out double intervalSeconds)
            => PlaybackHost.TryGetLoopSchedule(rec, recIdx,
                out playbackStartUT, out scheduleStartUT, out duration, out intervalSeconds);
        internal bool TryComputeLoopPlaybackUTForWatch(
            Recording rec, double currentUT,
            out double loopUT, out long cycleIndex, out bool inPauseWindow,
            int recIdx = -1)
        {
            return PlaybackHost.TryComputeLoopPlaybackUT(rec, currentUT,
                out loopUT, out cycleIndex, out inPauseWindow, recIdx);
        }
        // Mission loop-unit watch sync (Phase D2): resolve a watched member's span loopUT so
        // watch entry syncs ghost visuals at the same clock the engine renders the member at.
        // Returns false (loopUT = currentUT) for non-unit-member / non-looping watch.
        internal bool TryResolveUnitMemberPlaybackUTForWatch(
            int recordingIndex, double currentUT, double memberStartUT, double memberEndUT, out double loopUT)
            => engine.TryResolveUnitMemberPlaybackUT(
                recordingIndex, currentUT, memberStartUT, memberEndUT, out loopUT);
        internal void PositionGhostAtForWatch(GameObject ghost, TrajectoryPoint point) => Positioner.PositionGhostAt(ghost, point);
        internal GhostPlaybackLogic.LoopUnitSet cachedLoopUnits = GhostPlaybackLogic.LoopUnitSet.Empty;

        /// <summary>
        /// Read-only accessor on the current frame's cached loop unit set.
        /// Exposed for <see cref="Parsek.Display.GhostTrajectoryPolylineRenderer"/>'s
        /// DDOL Driver (mirrors <c>ParsekTrackingStation.CurrentCachedLoopUnits</c>).
        /// </summary>
        internal GhostPlaybackLogic.LoopUnitSet CurrentCachedLoopUnits => cachedLoopUnits;

        // Phase 4 decision-only shadow (design §6.7): the flight-map scene adapter the
        // ShadowRenderDriver runs the new pipeline against. The shadow is always enabled
        // (ShadowRenderDriver.Enabled is always true now that 8e S4 dropped the director-drive gate;
        // the Director pipeline is unconditional).
        internal readonly MapRender.MapViewScene mapViewScene = new MapRender.MapViewScene();
        internal GhostCommNetManager ghostCommNet;
        internal readonly Dictionary<string, int> reFlyAnchorHoldStartFrameByAnchor =
            new Dictionary<string, int>(StringComparer.Ordinal);
        internal readonly Dictionary<string, int> reFlyAnchorHoldCountsThisFrame =
            new Dictionary<string, int>(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> reFlyAnchorHoldReasonsThisFrame =
            new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly List<string> reFlyAnchorHoldReleaseScratch = new List<string>();
        internal readonly Dictionary<string, ReFlyDenseAbsolutePlaybackFrameCacheEntry> reFlyDenseAbsolutePlaybackFrameCache =
            new Dictionary<string, ReFlyDenseAbsolutePlaybackFrameCacheEntry>(StringComparer.Ordinal);
        internal readonly HashSet<string> loggedReFlyDenseAbsolutePlaybackFrameSelections =
            new HashSet<string>(StringComparer.Ordinal);
        internal int reFlySettlePoseLogActiveFrame = -1;
        // Scene-scoped; cleared alongside TerrainCacheBuckets.Clear() in OnSceneChangeRequested.
        internal static readonly Dictionary<string, TerrainCorrector.TailLiftPlan> s_tailLiftPlanCache =
            new Dictionary<string, TerrainCorrector.TailLiftPlan>();
        internal static readonly Dictionary<string, string> s_tailLiftPlanTerminalBodyCache =
            new Dictionary<string, string>();
        // P2 review (PR #706): the manual-preview Recording (recorder.CaptureAtStop)
        // is never added to RecordingStore.CommittedRecordings, so the tail-lift
        // resolver would log recording-missing every frame and skip the lift.
        // ParsekFlight registers/clears the preview source in StartPlayback /
        // StopPlayback so ResolveTailLiftPlan can find it.
        internal static Recording s_previewRecordingForTailLift;

        void OnEnable()
        {
            // ParsekFlight is a single FLIGHT-scene addon; clear scene-scoped Re-Fly stability state on entry.
            ReFlySettleStabilityTracker.Reset();
            reFlyAnchorHoldStartFrameByAnchor.Clear();
            reFlyAnchorHoldCountsThisFrame.Clear();
            reFlyAnchorHoldReasonsThisFrame.Clear();
            reFlyAnchorHoldReleaseScratch.Clear();
            reFlySettlePoseLogActiveFrame = -1;
        }

        // S9: true when this instance started in an inert game mode (ParsekGameModeGate)
        // and removed itself before subscribing to anything.
        private bool inertForGameMode;

        void Start()
        {
            // S9 game-mode gate: in a mission / scenario game the flight controller never
            // becomes Instance, subscribes nothing, builds no UI or toolbar button, and
            // removes itself, so no recorder, auto-record trigger, ghost or map presence runs.
            if (ParsekGameModeGate.CheckInert("ParsekFlight.Start"))
            {
                inertForGameMode = true;
                enabled = false;
                Destroy(this);
                return;
            }

            Instance = this;
            Log("Parsek Flight loaded.");

            GameEvents.onGameSceneLoadRequested.Add(OnSceneChangeRequested);
            GameEvents.onFlightReady.Add(OnFlightReady);
            GameEvents.onVesselWillDestroy.Add(OnVesselWillDestroy);
            GameEvents.onVesselSituationChange.Add(OnVesselSituationChange);
            GameEvents.onStageActivate.Add(OnStageActivate);
            GameEvents.onCrewOnEva.Add(OnCrewOnEva);
            GameEvents.onCrewBoardVessel.Add(OnCrewBoardVessel);
            GameEvents.onVesselGoOnRails.Add(OnVesselGoOnRails);
            GameEvents.onVesselGoOffRails.Add(OnVesselGoOffRails);
            GameEvents.onVesselLoaded.Add(OnVesselLoaded);
            GameEvents.onVesselUnloaded.Add(OnVesselUnloaded);
            GameEvents.onVesselSOIChanged.Add(OnVesselSOIChanged);
            GameEvents.onPartCouple.Add(OnPartCouple);
            GameEvents.onPartUndock.Add(OnPartUndock);
            GameEvents.onVesselsUndocking.Add(OnVesselsUndocking);
            GameEvents.onGroundSciencePartDeployed.Add(OnGroundSciencePartDeployed);
            GameEvents.onGroundSciencePartRemoved.Add(OnGroundSciencePartRemoved);
            GameEvents.onNewVesselCreated.Add(OnNewVesselCreatedForGroundPart);
            GameEvents.onDeployGroundPart.Add(OnDeployGroundPart);
            GameEvents.onEditorPartEvent.Add(OnEditorPartEventForConstructionDrop);
            GameEvents.onModuleInventorySlotChanged.Add(OnModuleInventorySlotChanged);
            GameEvents.OnEVAConstructionModePartAttached.Add(OnEvaConstructionPartAttached);
            GameEvents.OnEVAConstructionModePartDetached.Add(OnEvaConstructionPartDetached);
            GameEvents.onVesselChange.Add(OnVesselSwitchComplete);
            GameEvents.onVesselWasModified.Add(OnVesselWasModified);
            GameEvents.onTimeWarpRateChanged.Add(OnTimeWarpRateChanged);
            MergeDialog.OnTreeCommitted += OnTreeCommittedFromMergeDialog;
            GameEvents.afterFlagPlanted.Add(OnAfterFlagPlanted);
            GameEvents.onGamePause.Add(OnGamePause);
            GameEvents.onGameUnpause.Add(OnGameUnpause);
            Camera.onPreCull += OnCameraPreCull;
            StartCoroutine(PostPhysicsPoseCaptureLoop());

            CaptureFreshRolloutVesselPidIfApplicable();

            ui = new ParsekUI(this);

            // Camera follow (watch mode)
            watchMode = new WatchModeController(this);

            // Ghost playback engine + policy (T25 extraction)
            engine = new GhostPlaybackEngine(Positioner);
            // Tracing-only attitude residual source (GhostRenderTrace AfterUpdate dRotDeg=).
            GhostRenderTrace.ExpectedAttitudeSource = Positioner.TryResolveRecordedAttitudeForTrace;
            engine.OnLoopCameraAction += watchMode.HandleLoopCameraAction;
            engine.OnOverlapCameraAction += watchMode.HandleOverlapCameraAction;
            engine.IsWatchedGhostStateResolver = (recordingIndex, state) =>
                watchMode != null && watchMode.IsWatchedGhostState(recordingIndex, state);
            engine.ResolvePlaybackDistanceOverride = Positioner.ResolvePlaybackDistanceForEngine;
            engine.ResolvePlaybackActiveVesselDistanceOverride = Positioner.ResolvePlaybackActiveVesselDistanceForEngine;
            policy = new ParsekPlaybackPolicy(engine, this);
            // Phase 8d.0: route the per-frame map-presence tick through the scene adapter. The seam
            // delegates verbatim to policy.CheckPendingMapVessels; relocating the body is Phase 8d.1.
            mapViewScene.SetPresenceDriver(policy);

            // The committed list is restructured mid-session (optimizer merges / splits on a
            // tree commit, the Re-Fly origin splitter, deletes); every index-keyed slot here
            // must shift with it.
            RecordingStore.CommittedRecordingRemoving += OnCommittedRecordingRemoving;
            RecordingStore.CommittedRecordingRemoved += OnCommittedRecordingRemoved;
            RecordingStore.CommittedRecordingInserted += OnCommittedRecordingInserted;

            // Clean up any orphaned toolbar from rapid scene transitions (e.g. rewind)
            if (toolbarControl != null)
            {
                toolbarControl.OnDestroy();
                Destroy(toolbarControl);
                toolbarControl = null;
            }

            toolbarControl = gameObject.AddComponent<ToolbarControl>();
            toolbarControl.AddToAllToolbars(
                () => { showUI = true; },
                () => { showUI = false; },
                ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW,
                MODID, "parsekButton",
                "Parsek/Textures/parsek_64",
                "Parsek/Textures/parsek_32",
                MODNAME
            );

            ui.CloseMainWindow = () =>
            {
                showUI = false;
                if (toolbarControl != null) toolbarControl.SetFalse();
            };
        }

        void Update()
        {
            // Frame-latched Basic / Advanced UI mode apply (design 7.2). Unity runs Update
            // before OnGUI, so latching here gives every UiSurfaceVisibility gate one
            // stable mode across this frame's Layout and Repaint passes. Ahead of the
            // teardown early-out on purpose: the main window can still draw on a frame
            // where the rest of Update stands down.
            ParsekUI.ApplyPendingUiComplexityModeIfAny();

            // After OnSceneChangeRequested, the scene is tearing down — skip all processing
            // to prevent ghost spawns and other work into the dying scene.
            if (sceneChangeInProgress) return;

            // Frame F+1 of a Real Spawn Control placement jump: FixedUpdate has run since the
            // clock set, so body rotation and Zup are the new clock's.
            TimeJumpManager.TickPendingPlacement(recorder, backgroundRecorder);

            // A rewind's pending UT adjustment means Planetarium still reads the pre-rewind
            // future: never pass a committed row on that clock.
            if (!RecordingStore.RewindUTAdjustmentPending)
                committedMilestoneClock.Advance(Planetarium.GetUniversalTime());

            // Vessel name decisions re-applied when the committed set moved or (1 Hz) the
            // live tree gained a leg (vessel names design 7.4). Inert with no decision.
            VesselNameStore.SelfHealTick(inFlight: true, realtime: Time.realtimeSinceStartup);
            // The post-load live write pass (design 7.6 step 2), once the flight is ready and
            // no Re-Fly strip is still pending (RewindInvoker runs it itself after its strip).
            VesselNameLiveSync.FlightHostTick(Time.realtimeSinceStartup);

            // Defensive clear for any prior-frame entries that did not reach
            // the camera pre-cull hook (for example, scene/camera teardown).
            if (ghostPosEntries.Count > 0)
                ghostPosEntries.Clear();

            ClearStaleConfirmations();
            HandleMissedVesselSwitchRecovery();

            SplitBrancher.HandleTreeDockMerge();
            SplitBrancher.HandleTreeBoardMerge();
            SplitBrancher.HandleUnconfirmedBoardingStop();

            HandleAtmosphereBoundarySplit();
            HandleAltitudeBoundarySplit();
            HandleSoiChangeSplit();
            HandleTreeBackgroundFlush();

            // Background recorder: update on-rails background vessels, split checks, TTL
            if (backgroundRecorder != null)
            {
                double bgUT = Planetarium.GetUniversalTime();
                backgroundRecorder.UpdateOnRails(bgUT);
                backgroundRecorder.ProcessPendingSplitChecks();
                backgroundRecorder.CheckDebrisTTL(bgUT);
            }

            SplitBrancher.HandleJointBreakDeferredCheck();
            SplitBrancher.TickCrashCoalescer();
            SpawnedHarvesterRestore.Tick();

            // Skip auto-record and chain handlers while a split is being processed
            if (pendingSplitInProgress)
            {
                if (ParsekLog.IsVerboseEnabled)
                    ParsekLog.VerboseRateLimited("Flight", "update_split_suppressed", "Update suppressed: pendingSplitInProgress");
                // Still run playback and input
                HandleInput();
                if (isPlaying) PlaybackHost.UpdatePlayback();
                PlaybackHost.UpdateTimelinePlaybackViaEngine();
                TimeJumpManager.TickPlacementWatch();
                SpawnHost.TickSpawnWarpWatch();
                return;
            }

            PostSwitchAutoRecorder.HandleDeferredAutoRecordEva();

            HandleInput();

            if (isPlaying)
            {
                PlaybackHost.UpdatePlayback();
            }

            PlaybackHost.UpdateTimelinePlaybackViaEngine();
            TimeJumpManager.TickPlacementWatch();
            SpawnHost.TickSpawnWarpWatch();
            SpawnHost.UpdateProximityCheck();
        }

        /// <summary>
        /// Re-applies ghost positions after FloatingOrigin has shifted all registered
        /// objects. Without this, ghosts would be displaced by one frame each time
        /// the floating origin shifts, causing erratic visual behavior during flight.
        /// </summary>
        void LateUpdate()
        {
            if (sceneChangeInProgress) return;

            // Ghost icon popup: check for outside clicks (runs after UI event processing)
            Patches.GhostIconClickPatch.CheckOutsideClick();

            PlaybackHost.ApplyGhostPosEntries(GhostPositionReapplyPhase.LateUpdate);

            PlaybackHost.ClampGhostsToTerrain(GhostPositionReapplyPhase.LateUpdate);
            ghostLateUpdateReapplyFrame = Time.frameCount;

            PlaybackHost.LogReFlySettleActiveVesselPoseIfArmed("late-update", Time.frameCount);

            watchMode.UpdateWatchCamera();
        }

        private void OnCameraPreCull(Camera camera)
        {
            if (sceneChangeInProgress)
                return;

            int frame = Time.frameCount;
            GhostCameraPreCullAction action = ResolveGhostCameraPreCullAction(
                frame, ghostLateUpdateReapplyFrame, ghostPreCullReapplyFrame, ghostPosEntries.Count);
            if (action == GhostCameraPreCullAction.KeepForLateUpdate)
            {
                // A camera rendered between Update and LateUpdate (stock crew portraits
                // call Camera.Render() from a coroutine every 0.10-0.15 s). The entries
                // still carry this frame's FloatingOrigin reapply and terrain clamp, so
                // they must survive until LateUpdate consumes them.
                if (ParsekLog.IsVerboseEnabled)
                {
                    ParsekLog.VerboseRateLimited(
                        "Playback",
                        "ghost-camera-pre-cull-before-late-update",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Ghost camera pre-cull before LateUpdate: kept {0} reapply entries for LateUpdate (camera={1})",
                            ghostPosEntries.Count,
                            camera != null ? camera.name : "<null>"),
                        5.0);
                }
                return;
            }
            if (action != GhostCameraPreCullAction.RunAndConsume)
                return;

            ghostPreCullReapplyFrame = frame;
            PlaybackHost.ApplyGhostPosEntries(GhostPositionReapplyPhase.CameraPreCull);
            PlaybackHost.ClampGhostsToTerrain(GhostPositionReapplyPhase.CameraPreCull);
            ghostPosEntries.Clear();
        }

        /// <summary>
        /// In-game test seam: positions <paramref name="ghost"/> at a surface pose through
        /// the production surface path, registering its LateUpdate reapply entry exactly as
        /// playback does. Not called by any player path.
        /// </summary>
        internal void PositionGhostAtSurfaceForInGameTest(
            GameObject ghost, SurfacePosition surfPos, string recordingId)
        {
            Positioner.PositionGhostAtSurface(ghost, surfPos, allowActivation: false, recordingId: recordingId);
        }

        internal int PendingGhostPosEntryCountForInGameTest => ghostPosEntries.Count;

        void OnGUI()
        {
            // The Esc / pause overlay lives on KSP's Canvas and sorts above
            // our IMGUI layer, so without this gate the custom map markers,
            // watch-mode HUD, ghost labels, and Parsek windows render on top
            // of the pause menu. Stock vessel labels hide automatically
            // because they live on the same Canvas as the overlay; ours do
            // not. Skip both Layout and Repaint passes so layout-driven sizes
            // don't flicker between events while paused.
            // The UI-scale lock sweep runs before the pause gate so a window hidden by
            // the pause menu never leaves the game input locked.
            ParsekUiScale.SweepStaleLocks();
            if (PauseMenuGate.IsPauseMenuOpen())
                return;

            // Map markers and ghost labels are projected from world positions in
            // physical pixels and stay unscaled (UI-scale ruling 2026-11-04).
            if (MapView.MapIsEnabled)
                ui.DrawMapMarkers();

            if (watchMode.IsWatchingGhost)
            {
                using (ParsekUiScale.BeginHost("WatchOverlay"))
                    watchMode.DrawWatchModeOverlay();
            }

            // Phase 6d: Ghost labels on chain ghost GOs (no-op when ghost GOs are null)
            SpawnHost.DrawGhostLabels();

            if (showUI)
            {
                using (ParsekUiScale.BeginHost("Flight"))
                    DrawFlightWindows();
            }
        }

        // Every Parsek window in FLIGHT, inside the UI-scale host scope: the rects, the
        // after-draw hover checks and the screen clamps all read scaled-root units here.
        private void DrawFlightWindows()
        {
            windowRect.height = 0f;
            var mainWindowStyle = ui.GetMainWindowStyle();
            if (mainWindowStyle == null)
                return;

            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                windowRect = ParsekUiScale.Window(
                    GetInstanceID(),
                    windowRect,
                    ui.DrawWindow,
                    "Parsek",
                    mainWindowStyle,
                    GUILayout.Width(250)
                );
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
            ui.LogMainWindowPosition(windowRect);
            ui.DrawRecordingsWindowIfOpen(windowRect);
            ui.DrawTimelineWindowIfOpen(windowRect);
            ui.DrawKerbalsWindowIfOpen(windowRect);
            ui.DrawLogisticsWindowIfOpen(windowRect);
            ui.DrawStructureWindowIfOpen(windowRect);
            ui.DrawSettingsWindowIfOpen(windowRect);
            ui.DrawSpawnControlWindowIfOpen(windowRect);
            ui.DrawTestRunnerWindowIfOpen(windowRect, this);
        }

        void OnDestroy()
        {
            ParsekUiScale.ReleaseAllScaledLocks("flight-destroy");
            SpawnedHarvesterRestore.Clear("flight-destroy");
            if (inertForGameMode)
                return; // never subscribed, never became Instance (S9 game-mode gate)
            Instance = null;
            PostPhysicsPoseCache.Clear();
            TimeJumpManager.DropPendingPlacement("scene-change");
            // #267: clear the static restore-reentrancy guard. The restore coroutines
            // set it true and clear it in a finally, but Unity abandons a running
            // coroutine when the MonoBehaviour is destroyed (scene change) WITHOUT
            // running the finally — so a mid-restore scene exit would leave the static
            // flag stuck true, and the next scene's fresh ParsekFlight would inherit it
            // (suppressing the OnFlightReady merge dialog and tripping
            // ReentrancyGuard_ClearedAfterRestore). The next scene re-schedules its own
            // restore from scratch, so clearing here is always safe.
            if (restoringActiveTree)
                ParsekLog.Info("Flight", "OnDestroy: clearing stale restoringActiveTree guard (coroutine aborted by destroy)");
            restoringActiveTree = false;
            ParsekLog.Info("Flight", "OnDestroy: cleaning up ParsekFlight");
            Camera.onPreCull -= OnCameraPreCull;
            GhostRenderTrace.ExpectedAttitudeSource = null;
            // First, before anything below can throw: a dead instance left on these static
            // events would run ahead of the next scene's ParsekFlight and abort its reindex.
            RecordingStore.CommittedRecordingRemoving -= OnCommittedRecordingRemoving;
            RecordingStore.CommittedRecordingRemoved -= OnCommittedRecordingRemoved;
            RecordingStore.CommittedRecordingInserted -= OnCommittedRecordingInserted;
            PostSwitchAutoRecorder.DisarmPostSwitchAutoRecord("ParsekFlight destroyed");

            if (toolbarControl != null)
            {
                ParsekLog.Info("Flight", "OnDestroy: destroying toolbar control");
                toolbarControl.OnDestroy();
                Destroy(toolbarControl);
                toolbarControl = null;
            }

            UnregisterGameEvents();
            packStates.Clear();

            if (ghostCommNet != null)
            {
                ghostCommNet.Shutdown("ParsekFlight destroyed");
                ghostCommNet = null;
            }

            // Clean up decouple listener if still active
            if (decoupleCreatedVessels != null)
            {
                GameEvents.onPartDeCoupleNewVesselComplete.Remove(OnDecoupleNewVesselDuringSplitCheck);
                decoupleCreatedVessels = null;
                decoupleControllerStatus = null;
                decoupleCreatedTrajectoryPoints = null;
            }
            decoupleOriginByNewPid.Clear();
            pendingDeferredSplitCheckTrigger = DeferredSplitCheckTrigger.None;
            pendingSplitTriggerUT = double.NaN;

            // Clean up background recorder if active
            if (backgroundRecorder != null)
            {
                ParsekLog.Info("Flight", "OnDestroy: cleaning up background recorder");
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                backgroundRecorder = null;
            }

            // Clear tree destruction dialog guard
            treeDestructionDialogPending = false;

            // Clean up recording if active
            if (IsRecording)
            {
                ParsekLog.Info("Flight", "OnDestroy: force-stopping active recording");
                recorder.ForceStop();
            }
            PlaybackHost.StopPlayback();
            watchMode.ExitWatchMode();
            InputLockManager.RemoveControlLock(WatchModeController.WatchModeLockId); // safety net

            // Phase 6b: Clean up ghost chain state
            PlaybackHost.ClearClaimedVesselHoldGhosts("scene-teardown");
            vesselGhoster?.CleanupAll();
            vesselGhoster = null;
            reFlyInvokeHolds.Clear();
            activeGhostChains = null;

            // Remove ghost map ProtoVessels on scene teardown
            GhostMapPresence.RemoveAllGhostVessels("scene-cleanup", sceneTeardown: true);

            // Unsubscribe camera events before policy/engine disposal
            if (engine != null)
            {
                engine.OnLoopCameraAction -= watchMode.HandleLoopCameraAction;
                engine.OnOverlapCameraAction -= watchMode.HandleOverlapCameraAction;
            }

            // Clean up engine + policy (engine.Dispose calls DestroyAllGhosts internally)
            policy?.Dispose();
            engine?.Dispose();

            // Clear ParsekFlight-local state after engine disposal
            Positioner.ClearOrbitPlaybackCaches();
            loggedOrbitSegments.Clear();
            loggedOrbitRotationSegments.Clear();
            nearbySpawnCandidates.Clear();
            proximityVelocitySamples.Clear();
            notifiedSpawnRecordingIds.Clear();
            loggedRelativeStart.Clear();
            loggedRecordedRelativeStart.Clear();
            loggedRelativeBodyFixedPrimaryStart?.Clear();
            loggedAnchorNotFound.Clear();
            reFlyDenseAbsolutePlaybackFrameCache.Clear();
            loggedReFlyDenseAbsolutePlaybackFrameSelections.Clear();
            unknownFrameTagWarned.Clear();
            PlaybackHost.ClearGhostSkipReasonLogState();
            ui?.Cleanup();
        }

        /// <summary>
        /// Unregisters all GameEvents and delegate subscriptions to prevent handler leaks.
        /// </summary>
        private void UnregisterGameEvents()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChangeRequested);
            GameEvents.onFlightReady.Remove(OnFlightReady);
            GameEvents.onVesselWillDestroy.Remove(OnVesselWillDestroy);
            GameEvents.onVesselSituationChange.Remove(OnVesselSituationChange);
            GameEvents.onStageActivate.Remove(OnStageActivate);
            GameEvents.onCrewOnEva.Remove(OnCrewOnEva);
            GameEvents.onCrewBoardVessel.Remove(OnCrewBoardVessel);
            GameEvents.onVesselGoOnRails.Remove(OnVesselGoOnRails);
            GameEvents.onVesselGoOffRails.Remove(OnVesselGoOffRails);
            GameEvents.onVesselLoaded.Remove(OnVesselLoaded);
            GameEvents.onVesselUnloaded.Remove(OnVesselUnloaded);
            GameEvents.onVesselSOIChanged.Remove(OnVesselSOIChanged);
            GameEvents.onPartCouple.Remove(OnPartCouple);
            GameEvents.onPartUndock.Remove(OnPartUndock);
            GameEvents.onVesselsUndocking.Remove(OnVesselsUndocking);
            GameEvents.onGroundSciencePartDeployed.Remove(OnGroundSciencePartDeployed);
            GameEvents.onGroundSciencePartRemoved.Remove(OnGroundSciencePartRemoved);
            GameEvents.onNewVesselCreated.Remove(OnNewVesselCreatedForGroundPart);
            GameEvents.onDeployGroundPart.Remove(OnDeployGroundPart);
            GameEvents.onEditorPartEvent.Remove(OnEditorPartEventForConstructionDrop);
            GameEvents.onModuleInventorySlotChanged.Remove(OnModuleInventorySlotChanged);
            GameEvents.OnEVAConstructionModePartAttached.Remove(OnEvaConstructionPartAttached);
            GameEvents.OnEVAConstructionModePartDetached.Remove(OnEvaConstructionPartDetached);
            GameEvents.onVesselChange.Remove(OnVesselSwitchComplete);
            GameEvents.onVesselWasModified.Remove(OnVesselWasModified);
            GameEvents.onTimeWarpRateChanged.Remove(OnTimeWarpRateChanged);
            MergeDialog.OnTreeCommitted -= OnTreeCommittedFromMergeDialog;
            GameEvents.afterFlagPlanted.Remove(OnAfterFlagPlanted);
            GameEvents.onGamePause.Remove(OnGamePause);
            GameEvents.onGameUnpause.Remove(OnGameUnpause);
        }

        void OnSceneChangeRequested(GameScenes scene)
        {
            sceneChangeInProgress = true;

            // Phase 7 (design doc §13.2, §18 Phase 7): clear the per-scene
            // terrain-height bucket cache. Terrain is regenerated each session
            // and on body switches; stale bucket values would yield wrong
            // ground clearances at the destination scene's lat/lon.
            Parsek.Rendering.TerrainCacheBuckets.Clear();
            InvalidateTailLiftPlanCache();

            // Stamp the pre-transition UT so ParsekScenario.OnLoad can detect
            // F5/F9 quickloads (UT regresses across the transition) and
            // discard any pending stashed this transition. Bug A (2026-04-09).
            double preChangeUT = Planetarium.fetch != null
                ? Planetarium.GetUniversalTime()
                : 0.0;
            ParsekScenario.StampSceneChangeRequestedUT(preChangeUT);

            ParsekLog.Info("Flight",
                "Scene change requested: " + scene + " at UT=" +
                preChangeUT.ToString("F2", CultureInfo.InvariantCulture));
            RecorderStateLog.RecState("OnSceneChangeRequested", CaptureRecorderState());

            // Exit watch mode on scene change
            if (watchMode.IsWatchingGhost)
                ParsekLog.Info("CameraFollow", "Watch mode cleared on scene change");
            watchMode.ExitWatchMode();
            InputLockManager.RemoveControlLock(WatchModeController.WatchModeLockId); // safety net

            // Clear ghost-icon sticky state and force atlas re-init so the next
            // scene loads its own sprite atlas (the tracking station and flight
            // scenes may resolve different Texture2D instances for the same
            // logical atlas). See MapMarkerRenderer.ResetForSceneChange.
            MapMarkerRenderer.ResetForSceneChange();

            ClearSceneChangeTransientState();

            // Tree mode: finalize and commit tree on scene exit.
            // Background recorder must be finalized BEFORE the tree commit so
            // its data is flushed to the tree recordings.
            if (activeTree != null)
            {
                MergeFlow.FinalizeTreeOnSceneChange(scene);
            }
            else if (PendingTreeSlot.TryConsumeNextTreeSceneExitCommitSuppression(
                scene,
                out string suppressReason))
            {
                ParsekLog.Warn("Flight",
                    $"OnSceneChangeRequested: consumed tree scene-exit commit suppression " +
                    $"with no active tree dest={scene} reason='{suppressReason ?? "<unspecified>"}'");
            }

            // No recovery request survives the scene change that should have consumed it
            // (no active tree, a suppressed exit, or a FLIGHT destination).
            InFlightRecoveryRequest.Clear("scene change requested dest=" + scene);

            // Stop manual playback
            PlaybackHost.StopPlayback();
            DestroyAllTimelineGhosts();
            GhostVisualBuilder.ClearCanopyCache();
            GhostVisualBuilder.ClearAnimationSampleCache();
            GhostVisualBuilder.ClearFxPrefabCache();
            GhostVisualBuilder.ClearAnimateHeatCache();
        }

        // HasActiveTree is the existing public property at line 900.

        /// <summary>
        /// Read-only accessor for the dialog's display data. Caller must
        /// not mutate the returned reference; mutation happens inside
        /// <see cref="MergeDialog.MergeCommit"/> /
        /// <see cref="MergeDialog.MergeDiscard"/> after
        /// <see cref="FinalizeTreeOnSceneChangeForCallback"/> stashes the
        /// pending tree.
        /// </summary>
        internal RecordingTree ActiveTreeForDisplay => activeTree;

        void OnVesselWillDestroy(Vessel v)
        {
            if (v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId)) return;

            // A flag picked up by a kerbal, recovered or destroyed here is never planted again
            // by a replay (todo LOOP-REPLAY-REPLANTS-REMOVED-FLAG). Before the restore guard:
            // the removal is world state, not tree bookkeeping.
            if (v != null && v.vesselType == VesselType.Flag)
            {
                bool pickUp = FlagPickUp.IsPendingPickUp(v.persistentId);
                if (pickUp)
                    SurfaceCapture.TryCaptureFlagPickUp(v, FlagPickUp.PendingPickerPid);
                ParsekScenario.NoteFlagVesselRemoved(v, pickUp ? "picked-up" : "destroyed-in-flight");
            }

            // #267: skip tree-related processing if restore coroutine is mid-yield
            if (restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    $"OnVesselWillDestroy: skipped — restore coroutine in progress " +
                    $"(vessel '{v?.vesselName}')");
                return;
            }

            RecorderStateLog.RecState("OnVesselWillDestroy:entry", CaptureRecorderState());

            // EVA-construction pocket: the LAST part of a DroppedPart/Debris vessel
            // being stored in an inventory is a deliberate disassembly, not a loss.
            // Stamped HERE, before the background recorder's "background_destroy"
            // finalization refresh, because that refresh eagerly stamps
            // TerminalState.Destroyed onto a still-unstamped recording
            // (RecordingFinalizationCacheProducer.PopulateDestroyEventTerminalCache);
            // stamping first turns that eager write into a no-op instead of a
            // one-frame Destroyed flicker that later has to be re-stamped.
            bool disassembledDeath = TryStampDisassembledTerminal(v);

            // Watch mode: handle vessel destruction (null saved camera, exit if active vessel destroyed)
            watchMode.OnVesselWillDestroy(v);

            recorder?.OnVesselWillDestroy(v);

            // Also propagate to pending split recorder — joint break may have already
            // moved recorder to pendingSplitRecorder (making recorder null)
            if (pendingSplitRecorder != null && v == FlightGlobals.ActiveVessel
                && !pendingSplitRecorder.VesselDestroyedDuringRecording)
            {
                pendingSplitRecorder.VesselDestroyedDuringRecording = true;
                ParsekLog.Info("Flight", "Vessel destroyed during pending split — flagged for commit");
            }

            // Skip background recorder cleanup for vessels being absorbed by docking.
            // The merge handler (CreateMergeBranch) will clean up via OnVesselRemovedFromBackground.
            if (!dockingInProgress.Contains(v.persistentId))
                backgroundRecorder?.OnBackgroundVesselWillDestroy(v);

            // Classify vessel destruction mode using extracted decision method.
            bool shouldDefer = activeTree != null && ShouldDeferDestructionCheck(
                v.persistentId, true, dockingInProgress, activeTree.BackgroundMap);
            var destructionMode = ClassifyVesselDestruction(
                hasActiveTree: activeTree != null,
                isRecording: recorder != null && recorder.IsRecording,
                vesselDestroyedDuringRecording: recorder != null && recorder.VesselDestroyedDuringRecording,
                isActiveVessel: v == FlightGlobals.ActiveVessel,
                shouldDeferForTree: shouldDefer,
                treeDestructionDialogPending: treeDestructionDialogPending);

            switch (destructionMode)
            {
                case DestructionMode.TreeDeferred:
                {
                    string recordingId = activeTree.BackgroundMap[v.persistentId];
                    var pending = CaptureVesselStateForTerminal(v, recordingId);
                    // The terminal verdict for a pocketed part was already stamped
                    // synchronously above; the deferred pass must not overwrite it
                    // with the finalization cache's Destroyed.
                    pending.disassembled = disassembledDeath;
                    StartCoroutine(DeferredDestructionCheck(pending));
                    break;
                }
                case DestructionMode.TreeAllLeavesCheck:
                    // #434: do NOT arm FlightResultsPatch here. The stock KSP flight-results dialog
                    // (which offers Revert / Space Center) must surface first so the player can
                    // revert without Parsek pre-committing their mission. The tree gets finalized
                    // + stashed below and either auto-discards on revert (ParsekScenario isRevert
                    // branch) or surfaces the deferred merge dialog in the destination scene.
                    treeDestructionDialogPending = true;
                    ParsekLog.Info("Flight", "Active vessel destroyed in tree mode — scheduling tree destruction check");
                    StartCoroutine(ShowPostDestructionTreeMergeDialog());
                    break;
            }
        }

        internal static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        internal static bool ShouldIgnoreFlightReadyReset(
            bool hasActiveRecorder,
            bool hasActiveTree,
            bool hasPendingTree,
            ParsekScenario.ActiveTreeRestoreMode restoreMode)
        {
            return hasActiveRecorder
                && hasActiveTree
                && !hasPendingTree
                && restoreMode == ParsekScenario.ActiveTreeRestoreMode.None;
        }

        /// <summary>
        /// True when the active tree is THIS flight scene's own recording that was started
        /// and then stopped before stock onFlightReady (a fresh ParsekFlight instance per
        /// flight scene means a recorder bound to the active tree can only have been
        /// started here): the reset would drop a tree nothing else holds, so it is kept, as
        /// <see cref="ShouldIgnoreFlightReadyReset"/> keeps a live one. A pending tree, a
        /// scheduled restore, or a committed-tree restore clone (whose recorder failed to
        /// start when it was attached) still takes the reset path.
        /// </summary>
        internal static bool ShouldKeepStoppedTreeAcrossFlightReadyReset(
            bool hasStoppedRecorderBoundToActiveTree,
            bool activeTreeIsCommittedRestoreClone,
            bool hasPendingTree,
            ParsekScenario.ActiveTreeRestoreMode restoreMode)
        {
            return hasStoppedRecorderBoundToActiveTree
                && !activeTreeIsCommittedRestoreClone
                && !hasPendingTree
                && restoreMode == ParsekScenario.ActiveTreeRestoreMode.None;
        }

        /// <summary>
        /// Pauses all ghost audio when the KSP pause menu (ESC) opens. Stock KSP audio
        /// mutes on pause but ghost AudioSources don't respond to any global mute.
        /// Uses AudioSource.Pause() (not Stop()) to preserve playback position.
        /// </summary>
        void OnGamePause()
        {
            ParsekLog.Verbose("Flight", "OnGamePause: pausing ghost audio");
            engine?.PauseAllGhostAudio();
        }

        /// <summary>
        /// Resumes all ghost audio when the KSP pause menu closes.
        /// </summary>
        void OnGameUnpause()
        {
            ParsekLog.Verbose("Flight", "OnGameUnpause: resuming ghost audio");
            engine?.UnpauseAllGhostAudio();
        }

        /// <summary>
        /// True once <c>GameEvents.onFlightReady</c> has fired for THIS ParsekFlight
        /// instance. Read by the in-game test runner's reload-readiness wait: KSP sets
        /// <c>FlightGlobals.ready</c> some hundreds of milliseconds BEFORE
        /// <c>FlightDriver</c> fires the event, and the handler below resets the
        /// post-switch auto-record watch ("flight ready reset"), so a batch cell that
        /// starts in that window and arms the watch has it wiped from under it (H68 /
        /// H69, 2026-09-07: the orbital and landed hosts lost every post-switch cell to
        /// a 128 ms race the pad host happened to win). Set before every early return.
        /// </summary>
        internal bool FlightReadyObserved { get; private set; }

        void OnFlightReady()
        {
            FlightReadyObserved = true;
            // The vessels already loaded at scene start fired onVesselLoaded before this
            // scene subscribed; seed them so a loop anchored to one plays. First, before any
            // early return below.
            SeedLoadedAnchorVesselsFromScene("flight-ready");
            // Vessel names (design 7.6): a new stock revert target starts a new flight epoch,
            // and the first host tick re-applies the name decisions to every live vessel.
            // First, before any early return below.
            VesselNameLiveSync.OnFlightReady(FlightDriver.PostInitState, Time.realtimeSinceStartup);
            // From here on an onVesselSwitching in this scene is a real focus change;
            // the scene-entry initial focus fired before this point and was ignored.
            ParsekScenario.NoteFlightSceneReadyForVesselSwitchFlag();
            Log("Flight ready. Checking for pending recordings...");
            RecorderStateLog.RecState("OnFlightReady", CaptureRecorderState());

            // #267: if a restore coroutine is already running (double OnFlightReady fire),
            // skip ResetFlightReadyState and the dispatch — the running coroutine owns
            // activeTree/recorder and ResetFlightReadyState would null them.
            if (restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    "OnFlightReady: restore coroutine already in progress — skipping reset and dispatch");
                ConsumeFreshRecordingAfterDiscardedRestore(restoreScheduled: true);
                return;
            }

            // Bug 1 (post-#876 playtest 2026-05-17): the TS Fly / KSC marker Fly
            // consume must run on EVERY OnFlightReady path, not only the full-reset
            // path. When TryRestoreCommittedTreeForSpawnedActiveVessel attached the
            // recorder pre-OnFlightReady, ShouldIgnoreFlightReadyReset returns true
            // and the function used to bail before reaching the consume block at the
            // bottom — so the marker armed in TS / SPACECENTER survived OnLoad but
            // never got consumed, and the segment-scoped Merge dialog never fired.
            // Dispatch the consume helper here at the top (after the in-progress
            // restore coroutine skip, BEFORE ShouldIgnoreFlightReadyReset and the
            // full-reset path). The helper's own gates (CurrentStockActionIntent
            // != null, freshness, target-PID match) keep it a no-op when no intent
            // is armed.
            DispatchConsumeIntentIfArmed();

            var restoreMode = ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady;
            if (ShouldIgnoreFlightReadyReset(
                hasActiveRecorder: recorder != null && recorder.IsRecording,
                hasActiveTree: activeTree != null,
                hasPendingTree: PendingTreeSlot.HasPendingTree,
                restoreMode: restoreMode))
            {
                ParsekLog.Info("Flight",
                    "OnFlightReady: live recorder/tree already own the current flight — skipping reset");
                ConsumeFreshRecordingAfterDiscardedRestore(restoreScheduled: false);
                return;
            }

            if (ShouldKeepStoppedTreeAcrossFlightReadyReset(
                hasStoppedRecorderBoundToActiveTree: recorder != null && !recorder.IsRecording
                    && activeTree != null && object.ReferenceEquals(recorder.ActiveTree, activeTree),
                activeTreeIsCommittedRestoreClone: activeTree != null
                    && CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptTree(activeTree.Id),
                hasPendingTree: PendingTreeSlot.HasPendingTree,
                restoreMode: restoreMode))
            {
                ParsekLog.Info("Flight",
                    $"OnFlightReady: keeping tree '{activeTree.TreeName}' id={activeTree.Id} " +
                    $"recordings={activeTree.Recordings.Count.ToString(CultureInfo.InvariantCulture)} " +
                    "stopped before flight ready in this scene - skipping reset");
                ConsumeFreshRecordingAfterDiscardedRestore(restoreScheduled: false);
                return;
            }

            // Reset scene-scoped state from the previous flight BEFORE the restore
            // coroutine runs. ResetFlightReadyState clears activeTree, backgroundRecorder,
            // pendingSplitRecorder, etc. — all scene-scoped state that must
            // be zeroed before the restore can rebuild its own.
            //
            // Ordering is load-bearing: RestoreActiveTreeFromPending runs SYNCHRONOUSLY
            // when FlightGlobals.ActiveVessel is already loaded and matches the target
            // on the first iteration of its wait loop (the `break` exits without hitting
            // `yield return null`, so Unity runs the entire coroutine body inline inside
            // StartCoroutine). If ResetFlightReadyState runs AFTER the restore, it nulls
            // activeTree and destroys everything the coroutine just set up — turning a
            // successful quickload-resume into a standalone-mode fragment and losing
            // the entire launch tree (bug observed 2026-04-09 playtest; see CHANGELOG).
            ResetFlightReadyState();

            // Active-tree restore: ParsekScenario.OnLoad detected a pending tree on the
            // !isRevert branch and deferred restore to this point. Two modes:
            //   - Quickload (existing path): name-match against active vessel and resume.
            //   - VesselSwitch (#266 / #546): tree was pre-transitioned at stash time, just
            //     reinstall it and arm the post-switch watcher for the new active vessel.
            //
            // Re-Fly retry carve-out: when the previous Re-Fly attempt was
            // post-destruction finalized before the player clicked Retry from
            // Rewind Point, OnLoad's TryRestoreActiveTreeNode keeps the pending
            // tree in Finalized state (#290d) so no Quickload restore was
            // scheduled — but AtomicMarkerWrite still attached a fresh in-place
            // fork to that tree and the live vessel needs a recorder bound to
            // it. Schedule the same Quickload path here; RestoreActiveTreeFromPending's
            // own carve-out (accept Finalized when Re-Fly session is active)
            // does the pop / recorder setup the same way it would for a Limbo
            // tree.
            if (ShouldUpgradeRestoreModeForReFlyRetry(
                    restoreMode,
                    hasPendingTree: PendingTreeSlot.HasPendingTree,
                    pendingTreeIsFinalized:
                        PendingTreeSlot.PendingTreeStateValue == PendingTreeState.Finalized,
                    reFlyInPlaceContinuationActive:
                        ParsekScenario.IsReFlyInPlaceContinuationActive()))
            {
                ParsekLog.Info("Flight",
                    "OnFlightReady: scheduling Quickload restore for Re-Fly retry " +
                    $"(pending tree '{PendingTreeSlot.PendingTree?.TreeName}' is Finalized; " +
                    "AtomicMarkerWrite attached a fresh fork that needs a live recorder)");
                restoreMode = ParsekScenario.ActiveTreeRestoreMode.Quickload;
            }
            if (restoreMode != ParsekScenario.ActiveTreeRestoreMode.None)
            {
                ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady =
                    ParsekScenario.ActiveTreeRestoreMode.None;
                if (restoreMode == ParsekScenario.ActiveTreeRestoreMode.VesselSwitch)
                {
                    StartCoroutine(RestoreActiveTreeFromPendingForVesselSwitch());
                }
                else
                {
                    StartCoroutine(RestoreActiveTreeFromPending());
                }
                ConsumeFreshRecordingAfterDiscardedRestore(restoreScheduled: true);
            }
            else
            {
                TryRestoreCommittedTreeForSpawnedActiveVessel();
                // Runs after the committed-tree restore so a vessel that path binds is not
                // double-started; the consume refuses when a recorder or tree is live.
                ConsumeFreshRecordingAfterDiscardedRestore(restoreScheduled: false);
            }

            // Note: consume dispatch lives at the top of OnFlightReady — see DispatchConsumeIntentIfArmed.

            // Belt-and-suspenders: recover orphaned spawned vessels that survived
            // the protoVessel stripping in OnLoad (e.g., FLIGHT→FLIGHT revert where
            // SpaceCenter was never visited, or name change edge cases).
            // Here FlightGlobals.Vessels is populated and vessel.persistentId is reliable.
            if (RewindLauncher.ShouldRunPendingCleanupOnFlightReady())
            {
                // BUG-H: the live-vessel cleanup is launch-identity-aware (pid + Vessel.id Guid) and
                // scoped to the reverted flight (skips vessels in the revert-target launch quicksave).
                // The PendingCleanupPids/Names sets are only the "cleanup armed" trigger now — the
                // actual recover decision derives from the committed recordings + the pre-existing
                // whitelist so a real, separately-launched craft is never recovered by name.
                SpawnHost.CleanupOrphanedSpawnedVessels(
                    RecordingStore.CollectAllCommittedRecordings(),
                    RewindLauncher.PendingRevertPreExistingPids);
                RewindLauncher.PendingCleanupPids = null;
                RewindLauncher.PendingCleanupNames = null;
                RewindLauncher.PendingRevertPreExistingPids = null;
            }
            else
            {
                ParsekLog.Verbose("Flight",
                    "OnFlightReady: no pending cleanup data — skipping CleanupOrphanedSpawnedVessels");
            }

            // Seed lastLandedUT if vessel is already on the surface (save-loaded pad, Mun lander, etc.)
            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel != null &&
                (activeVessel.situation == Vessel.Situations.LANDED ||
                 activeVessel.situation == Vessel.Situations.SPLASHED))
            {
                lastLandedUT = Planetarium.GetUniversalTime();
                ParsekLog.Verbose("Flight", $"Seeded lastLandedUT={lastLandedUT:F1} (vessel already {activeVessel.situation})");
            }

            // Phase 6b: Evaluate ghost chains and ghost claimed vessels
            SpawnHost.EvaluateAndApplyGhostChains();
            PlaybackHost.RestoreReFlyInvokeHoldsFromMarker(Planetarium.GetUniversalTime());
            PlaybackHost.RestoreRewindVesselHolds(Planetarium.GetUniversalTime());

            // Capture active vessel PID at scene entry for stateless spawn dedup bypass.
            // After revert, the pad vessel shares the same PID as recorded vessels — at
            // spawn time, SpawnVesselOrChainTip checks this static PID instead of relying
            // on per-recording transient flags that could be lost on Recording recreation.
            SceneEntryVesselScope.SceneEntryActiveVesselPid =
                (activeVessel != null) ? activeVessel.persistentId : 0;
            SceneEntryVesselScope.SceneEntryActiveVesselGuid =
                (activeVessel != null && activeVessel.id != Guid.Empty)
                    ? activeVessel.id.ToString("N")
                    : null;
            if (SceneEntryVesselScope.SceneEntryActiveVesselPid != 0)
                ParsekLog.Verbose("Flight",
                    $"Scene entry active vessel pid={SceneEntryVesselScope.SceneEntryActiveVesselPid} " +
                    $"guid={SceneEntryVesselScope.SceneEntryActiveVesselGuid ?? "(none)"}");

            MaybeShowPendingTreeMergeDialogOnFlightReady();

            // Swap reserved crew out of the active vessel so the player
            // can't record with them again (they belong to deferred-spawn vessels).
            // A brand-new VAB/SPH launch suppresses Pass-2 orphan placement inside
            // SwapReservedCrewInFlight (via SceneEntryVesselScope.SceneEntryFreshRolloutVesselPid),
            // otherwise KSP's craft-stable part persistentId reuse lets the pid
            // matcher mis-seat stand-ins into the fresh vessel the player just crewed.
            int swapResult = CrewReservationManager.SwapReservedCrewInFlight();
            if (swapResult > 0)
                Log($"Crew swap on flight ready: {swapResult} crew swapped out of active vessel");
            else if (CrewReservationManager.CrewReplacements.Count > 0)
                Log($"Crew swap on flight ready: 0 swapped ({CrewReservationManager.CrewReplacements.Count} reservations exist but no matches on active vessel)");

            var committed = RecordingStore.CommittedRecordings;
            Log($"Timeline has {committed.Count} committed recording(s)");
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                string hasVessel = rec.VesselSnapshot != null ? "vessel" :
                    rec.GhostVisualSnapshot != null ? "ghost" : "ghost-only";
                string orbitInfo = rec.OrbitSegments.Count > 0
                    ? $", {rec.OrbitSegments.Count} orbit seg(s)"
                    : "";
                string chainInfo = !string.IsNullOrEmpty(rec.ChainId)
                    ? $", chain idx={rec.ChainIndex}"
                    : "";
                Log($"  Recording #{i}: \"{rec.VesselName}\" UT {rec.StartUT:F0}-{rec.EndUT:F0}, " +
                    $"{rec.Points.Count} pts{orbitInfo}, {hasVessel}{chainInfo}");
            }
        }

        /// <summary>
        /// Clears all transient dock/undock, merge detection, and split event state
        /// on scene change. Called from OnSceneChangeRequested after continuation finalization.
        /// </summary>
        internal void ClearSceneChangeTransientState()
        {
            PostSwitchAutoRecorder.DisarmPostSwitchAutoRecord("scene change transient reset");

            // Clear dock/undock pending state
            SplitBrancher.ClearDockUndockState();

            // Clear merge event detection state
            pendingTreeDockMerge = false;
            pendingBoardingTargetInTree = false;
            dockingInProgress.Clear();
            treeDestructionDialogPending = false;

            // Clear split event detection state
            lastBranchUT = -1;
            lastBranchVesselPids.Clear();
            pendingSplitInProgress = false;
            pendingSplitRecorder = null;
            preBreakVesselPids = null;
        }

        /// <summary>
        /// Resets all transient state on flight ready: background recorder, tree, chain,
        /// split detection, dock/undock, merge detection. Called at the start of OnFlightReady.
        /// </summary>
        private void ResetFlightReadyState()
        {
            ParsekLog.Info("Flight", "Resetting flight-ready state");
            RecorderStateLog.RecState("ResetFlightReady:entry", CaptureRecorderState());
            PostSwitchAutoRecorder.DisarmPostSwitchAutoRecord("flight ready reset");

            // Phase 6b: Clean up ghost chain state from previous flight scene
            PlaybackHost.ClearClaimedVesselHoldGhosts("flight-ready-reset");
            vesselGhoster?.CleanupAll();
            vesselGhoster = null;
            reFlyInvokeHolds.Clear();
            activeGhostChains = null;


            // Shutdown background recorder before clearing tree
            if (backgroundRecorder != null)
            {
                backgroundRecorder.Shutdown();
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                backgroundRecorder = null;
            }

            // Clear tree state on flight ready (revert resets everything). When this tree
            // is the armed committed-restore attempt's clone (same tree id), the attempt is
            // cleared with it, so no stale attempt outlives the tree it guarded.
            if (activeTree != null)
            {
                ParsekLog.Info("Flight",
                    $"ResetFlightReadyState: dropping active tree '{activeTree.TreeName}' id={activeTree.Id} " +
                    $"recordings={(activeTree.Recordings?.Count ?? 0).ToString(CultureInfo.InvariantCulture)} " +
                    $"recorderLive={(recorder != null && recorder.IsRecording)}");
            }

            // The flight scene was just loaded from a save, which KSP's career came from too.
            CommittedTreeRestoreAttempt.ClearCommittedTreeRestoreAttemptForDroppedTree(
                activeTree?.Id, "flight-ready reset dropped the restore clone",
                preserveIrreversibleTails: false);
            activeTree = null;

            // Clear split event detection state
            lastBranchUT = -1;
            lastBranchVesselPids.Clear();
            pendingSplitInProgress = false;
            pendingSplitRecorder = null;
            preBreakVesselPids = null;

            pendingBoardingTargetPid = 0;

            // Clear dock/undock state
            SplitBrancher.ClearDockUndockState();

            // Clear merge event detection state
            pendingTreeDockMerge = false;
            pendingBoardingTargetInTree = false;
            dockingInProgress.Clear();
            treeDestructionDialogPending = false;
        }

        /// <summary>
        /// Expires stale boarding/dock/undock confirmation windows.
        /// Called at the start of Update() each frame.
        /// </summary>
        private void ClearStaleConfirmations()
        {
            // Auto-clear stale boarding confirmation after 10 frames (#57)
            // Was 3 frames (~60ms at 50fps) — too short for vessel switches
            if (pendingBoardingTargetPid != 0)
            {
                boardingConfirmFrames++;
                if (boardingConfirmFrames > 10)
                {
                    ParsekLog.Info("Flight", $"Boarding confirmation expired (targetPid={pendingBoardingTargetPid})");
                    pendingBoardingTargetPid = 0;
                    boardingConfirmFrames = 0;
                }
            }

            // Auto-clear stale dock/undock confirmation after 5 frames
            if (pendingDockMergedPid != 0)
            {
                dockConfirmFrames++;
                if (dockConfirmFrames > 5)
                {
                    ParsekLog.Info("Flight", $"Dock confirmation expired (mergedPid={pendingDockMergedPid}, treeDock={pendingTreeDockMerge}, absorbedPid={pendingDockAbsorbedPid})");
                    if (pendingDockAbsorbedPid != 0)
                        dockingInProgress.Remove(pendingDockAbsorbedPid);
                    pendingDockMergedPid = 0;
                    pendingTreeDockMerge = false;
                    pendingDockAbsorbedPid = 0;
                    pendingDockRouteTargetPid = 0;
                    pendingDockPartnerPid = 0;
                    pendingDockPartnerGuid = null;
                    pendingDockTransferKind = RouteConnectionKind.None;
                    pendingDockPartnerSnapshot = null;
                    pendingDockPartnerSnapshotPid = 0u;
                    pendingDockSelfSnapshot = null;
                    pendingDockSelfSnapshotPid = 0u;
                    pendingDockRouteEndpointAtDock = null;
                    pendingDockRouteEndpointSituation = -1;
                    dockConfirmFrames = 0;
                }
            }
        }

        internal delegate bool TryResolveTrajectoryPoint(uint persistentId, out TrajectoryPoint point);

        void HandleInput()
        {
            bool leftBracket = Input.GetKeyDown(KeyCode.LeftBracket);
            bool rightBracket = Input.GetKeyDown(KeyCode.RightBracket);
            bool vKey = Input.GetKeyDown(KeyCode.V);
            bool wKey = Input.GetKeyDown(KeyCode.W);

            // Suppress watch-mode shortcuts while a text field (recording rename,
            // settings text fields, etc.) holds keyboard focus. W is the load-bearing
            // case - it's a common letter in vessel names, so without this guard
            // typing "Kerbal X Probe" into the rename field would cycle the watch
            // camera between every keystroke. Raw Input polling bypasses
            // InputLockManager, so the focus check has to happen here.
            if ((leftBracket || rightBracket || vKey || wKey)
                && InputFocusGuard.IsTextFieldFocused())
            {
                ParsekLog.Verbose("Input",
                    $"Watch-mode shortcut suppressed - text field focused " +
                    $"(L={leftBracket} R={rightBracket} V={vKey} W={wKey})");
                return;
            }

            // [ or ] — Exit watch mode (return to vessel)
            // Avoids Backspace which is KSP's Abort action group key
            if (watchMode.IsWatchingGhost && (leftBracket || rightBracket))
            {
                watchMode.ExitWatchMode();
            }

            // V — Toggle watch camera mode (Free / Horizon-Locked)
            // Stock V (camera mode switch) is blocked by CAMERAMODES control lock during watch
            if (watchMode.IsWatchingGhost && vKey)
            {
                watchMode.ToggleCameraMode();
            }

            // W — Cycle through watchable ghosts. Stock pitch-down (W) is blocked
            // by PITCH in WatchModeLockMask so the unattended active vessel does
            // not receive the keypress. The raw Unity Input poll here is
            // unaffected by the input lock.
            if (watchMode.IsWatchingGhost && wKey)
            {
                watchMode.CycleToNextWatchable();
            }
        }

        /// <summary>
        /// Per-recording terminal lines emitted by <see cref="LogCommitTerminalVerdicts"/>
        /// before it collapses to a single summary. Matches the repo batch-counting
        /// convention: per-item lines only while the item count is bounded (~20).
        /// A tree with more leaves than this is a debris storm, not a mission.
        /// </summary>
        internal const int CommitTerminalLogLimit = 20;

        /// <summary>
        /// Slack for <see cref="IsCommittedRecordingAheadOfLiveClock"/>: a save written in the
        /// frame that ended the recording reads the same UT up to float noise.
        /// </summary>
        internal const double CommittedRestoreAheadOfClockToleranceSeconds = 0.01;

        internal static Func<string, double?> TerminalInferenceBodyRadiusResolverForTesting;

        /// <summary>
        /// Test seam for the periapsis floor (<see cref="OrbitClearance"/>) the inference reads.
        /// When only the radius seam is installed the floor defaults to sea level.
        /// </summary>
        internal static Func<string, double?> TerminalInferencePeriapsisFloorResolverForTesting;

        /// <summary>
        /// Test seam replacing the leaf spawn call in <see cref="SpawnVesselOrChainTip"/>
        /// (rec, index, preserveIdentity, allowExistingSourceDuplicate); the real call needs a
        /// live KSP scene.
        /// </summary>
        internal static Action<Recording, int, bool, bool> LeafSpawnOverrideForTesting;

        private static readonly Func<uint, string, double, (Vector3d pos, Quaternion rot)?> LiveAnchorTransformDelegate =
            TryResolveLiveAnchorTransformForResolver;

        /// <summary>
        /// A committed recording is about to leave the list (any internal remover: an
        /// optimizer merge, a Re-Fly rollback). The list is still unshifted, so the engine's OnGhostDestroyed
        /// subscribers (GhostMapPresence's index-keyed teardown) read the right recording,
        /// and a retained ghost-less map presence at the slot is torn down too.
        /// </summary>
        private void OnCommittedRecordingRemoving(int index, Recording removed)
        {
            ParsekLog.Verbose("Flight",
                $"Committed recording #{index} ('{removed?.VesselName}', id={removed?.RecordingId}) " +
                "removing - destroying its ghost state");
            PlaybackHost.DestroyAllOverlapGhosts(index);
            engine.DestroyGhost(index, reason: "committed-recording-removed");
            GhostMapPresence.HandleCommittedRecordingRemoving(index);
        }

        /// <summary>
        /// A committed recording left the list: every index above <paramref name="index"/>
        /// shifted down, so every index-keyed slot (engine dicts, held ghosts, map presence,
        /// watch-mode index, orbit and map-marker caches) shifts with
        /// it. <paramref name="absorbedInto"/> is the optimizer merge target that now carries
        /// the removed trajectory, null for a plain delete.
        /// </summary>
        private void OnCommittedRecordingRemoved(int index, Recording removed, Recording absorbedInto)
        {
            watchMode.OnRecordingDeleted(index);
            engine.ReindexAfterDelete(index);
            policy.ReindexHeldGhostsAfterDelete(index);
            GhostMapPresence.ReindexPresenceAfterDelete(index);
            // Orbit cache keys are index-derived (i * 10000 + segIdx); the map marker
            // waypoint cache and the diagnostic guards are index-keyed too.
            Positioner.ClearOrbitPlaybackCaches();
            ui?.ClearMapMarkerCache();
            loggedOrbitSegments.Clear();
            loggedOrbitRotationSegments.Clear();
            if (removed != null)
                policy.RemovePendingSpawn(removed.RecordingId);
            ParsekLog.Verbose("Flight",
                $"Committed recording #{index} removed" +
                (absorbedInto != null ? $" (merged into id={absorbedInto.RecordingId})" : "") +
                $" - reindexed engine, held, map and watch state " +
                $"(ghostStates={engine.ghostStates.Count}, committed={RecordingStore.CommittedRecordings.Count})");
        }

        /// <summary>
        /// Insert mirror of <see cref="OnCommittedRecordingRemoved"/>: a recording was inserted
        /// at <paramref name="index"/> (the optimizer's split second half, the Re-Fly origin
        /// splitter's TIP), so every index-keyed slot at or above it shifts up by one. A split
        /// first half keeps its ghost state at <c>index - 1</c> and plays its (now shorter)
        /// trajectory; the inserted recording spawns through the normal path.
        /// </summary>
        private void OnCommittedRecordingInserted(int index)
        {
            engine.ReindexAfterInsert(index);
            policy.ReindexHeldGhostsAfterInsert(index);
            GhostMapPresence.ReindexPresenceAfterInsert(index);
            watchMode.OnRecordingInserted(index);
            Positioner.ClearOrbitPlaybackCaches();
            ui?.ClearMapMarkerCache();
            loggedOrbitSegments.Clear();
            loggedOrbitRotationSegments.Clear();
            ParsekLog.Verbose("Flight",
                $"Committed recording inserted at #{index} - reindexed engine, held, map and watch state " +
                $"(ghostStates={engine.ghostStates.Count}, committed={RecordingStore.CommittedRecordings.Count})");
        }

        private const double PadFailureDurationThresholdSeconds = 10.0;
        private const double PadLocalizedDistanceThresholdMeters = 30.0;
        private const double PadLocalizedAltitudeDisplacementThresholdMeters = 30.0;

        // Stock KSP body radii for runtime-free distance estimates in unit tests.
        // Unknown/modded bodies conservatively fall back to Kerbin's radius.
        private static readonly Dictionary<string, double> PadHeuristicBodyRadii =
            new Dictionary<string, double>
            {
                { "Kerbol", 261600000 },
                { "Moho", 250000 },
                { "Eve", 700000 },
                { "Gilly", 13000 },
                { "Kerbin", 600000 },
                { "Mun", 200000 },
                { "Minmus", 60000 },
                { "Duna", 320000 },
                { "Ike", 130000 },
                { "Dres", 138000 },
                { "Jool", 6000000 },
                { "Laythe", 500000 },
                { "Vall", 300000 },
                { "Tylo", 600000 },
                { "Bop", 65000 },
                { "Pol", 44000 },
                { "Eeloo", 210000 }
            };

        /// <summary>A converter event within this many seconds of its recording's start is a
        /// start seed (<see cref="PartStateSeeder"/>), not something the player did.</summary>
        internal const double ConverterSeedEpsilonSeconds = 1.0;

        /// <summary>
        /// Test seam for <see cref="TreeMovesHeldKerbalForIdle"/>: replaces the "does the
        /// committed timeline hold this kerbal now" read (default
        /// <see cref="KerbalsModule.IsReservedNow"/> on <see cref="LedgerOrchestrator.Kerbals"/>).
        /// </summary>
        internal static Func<string, bool> HeldKerbalProbeForTesting;

        /// <summary>
        /// Reindexes an int-keyed dictionary after a deletion: keys above removedIndex shift down by 1.
        /// The entry at removedIndex (if any) is dropped.
        /// </summary>
        internal static void ReindexAfterDelete<T>(Dictionary<int, T> dict, int removedIndex)
        {
            IndexShift.DictAfterDelete(dict, removedIndex);
        }

        // Camera Follow (Watch Mode) region moved to WatchModeController

        // Forwarding methods for external callers (RecordingsTableUI, GhostVesselLoadPatch, etc.)
        internal bool HasActiveGhost(int index) => watchMode.HasActiveGhost(index);
        internal bool IsGhostWithinVisualRange(int index) => watchMode.IsGhostWithinVisualRange(index);
        internal bool IsGhostOnSameBody(int index) => watchMode.IsGhostOnSameBody(index);
        internal bool IsGhostBodyReadingCurrent(int index) => watchMode.IsGhostBodyReadingCurrent(index);
        internal void EnterWatchMode(int index) => watchMode.EnterWatchMode(index);
        internal void ExitWatchMode(bool skipCameraRestore = false) => watchMode.ExitWatchMode(skipCameraRestore);

        // Static methods forwarded to WatchModeController for test compatibility
        internal static bool IsVesselSituationSafe(Vessel.Situations situation, double periapsis, double atmosphereAltitude) =>
            WatchModeController.IsVesselSituationSafe(situation, periapsis, atmosphereAltitude);
        internal static (int newIndex, string newId) ComputeWatchIndexAfterDelete(
            int watchedIndex, string watchedId, int deletedIndex, List<Recording> recordings) =>
            WatchModeController.ComputeWatchIndexAfterDelete(watchedIndex, watchedId, deletedIndex, recordings);
        internal static bool ShouldAutoHorizonLock(bool hasAtmosphere, double atmosphereDepth, double altitude) =>
            WatchModeController.ShouldAutoHorizonLock(hasAtmosphere, atmosphereDepth, altitude);
        internal static bool ShouldUseSurfaceRelativeWatchHeading(
            bool hasAtmosphere, double atmosphereDepth, double altitude) =>
            WatchModeController.ShouldUseSurfaceRelativeWatchHeading(
                hasAtmosphere, atmosphereDepth, altitude);
        internal static (Vector3 forward, Vector3 horizonVelocity, HorizonForwardSource source)
            ComputeWatchHorizonForward(Vector3 up, Vector3 headingVelocity, Vector3 lastForward) =>
            WatchModeController.ComputeWatchHorizonForward(up, headingVelocity, lastForward);
        internal static (Vector3 forward, Vector3 horizonVelocity, Vector3 headingVelocity,
            string headingFrame, HorizonForwardSource source)
            ComputeWatchHorizonBasis(
                bool hasAtmosphere, double atmosphereDepth, double inverseRotThresholdAltitude,
                double altitude,
                Vector3 up, Vector3 surfaceVelocity, GhostSurfaceVelocitySource surfaceSource,
                Vector3 playbackVelocity, Vector3 rotatingFrameVelocity,
                Vector3 lastForward) =>
            WatchModeController.ComputeWatchHorizonBasis(
                hasAtmosphere, atmosphereDepth, inverseRotThresholdAltitude, altitude,
                up, surfaceVelocity, surfaceSource,
                playbackVelocity, rotatingFrameVelocity, lastForward);
        internal static Vector3 ComputeHorizonForward(Vector3 up, Vector3 velocity, Vector3 lastForward) =>
            WatchModeController.ComputeHorizonForward(up, velocity, lastForward);
        internal static (Quaternion rotation, Vector3 forward) ComputeHorizonRotation(
            Vector3 up, Vector3 velocity, Vector3 lastForward) =>
            WatchModeController.ComputeHorizonRotation(up, velocity, lastForward);
        internal static (float pitch, float hdg) CompensateCameraAngles(
            Quaternion oldTargetRot, Quaternion newTargetRot, float pitch, float hdg) =>
            WatchModeController.CompensateCameraAngles(oldTargetRot, newTargetRot, pitch, hdg);

        // Literal floor returned by the immediate-clearance resolver when it
        // cannot compute a distance-aware value. Matches the legacy pre-#262
        // hardcoded floor — callers that hit this branch silently regress to
        // the old behavior, so ResolveImmediateLandedGhostClearanceMeters
        // emits a rate-limited warning naming the offending ghost (#373).
        internal const double ImmediateLandedGhostClearanceFallbackMeters = 0.5;

        // -------------------------------------------------------------------
        //  L4 — per-frame spline-eval summary (design doc §19.2 Stage 1).
        //
        //  Counts every successful spline evaluation across Update + LateUpdate
        //  paths and emits a single Verbose Pipeline-Smoothing line per second
        //  with the accumulated count, then resets. Distinct from
        //  ParsekLog.VerboseRateLimited (which suppresses repeated lines and
        //  loses the count); here the count is the payload, so we own the gate.
        //  Wall-clock based (DateTime.UtcNow) so it works in tests without
        //  Unity's Time API and across scene loads where Planetarium UT can
        //  reset to zero.
        // -------------------------------------------------------------------
        private static int s_splineEvalCount;
        private static long s_splineEvalLogLastTicks;
        private const long SplineEvalLogIntervalTicks = TimeSpan.TicksPerSecond; // 1 s

        /// <summary>Test-only wall-clock override for the L4 summary gate.</summary>
        internal static Func<DateTime> NowProviderForTesting;

        // Cache to avoid reconstructing Orbit objects every frame
        internal Dictionary<long, Orbit> orbitCache = new Dictionary<long, Orbit>();

        // Tracks which (recording index, anchor PID) combos have been logged for loop-relative playback start.
        internal readonly HashSet<long> loggedRelativeStart = new HashSet<long>();
        internal readonly HashSet<string> loggedRecordedRelativeStart =
            new HashSet<string>(StringComparer.Ordinal);
        internal readonly HashSet<string> loggedRelativeBodyFixedPrimaryStart =
            new HashSet<string>(StringComparer.Ordinal);
        // Tracks which anchor-not-found warnings have been logged
        internal readonly HashSet<long> loggedAnchorNotFound = new HashSet<long>();
        // Phase 4 HR-9: per-(recordingId, sectionIndex) dedup so an unrecognised
        // FrameTag warns once per session instead of every per-frame dispatch.
        // Cleared in the same Cleanup hooks as the other per-session log state.
        internal readonly HashSet<string> unknownFrameTagWarned =
            new HashSet<string>(StringComparer.Ordinal);
        internal const bool NormalPlaybackPointHermiteEnabled = false;

        private const double RecordedAnchorFallbackGapWarnThresholdSeconds = 5.0;

        internal const double RecordedRelativeOffsetSpikeNeighborMaxMeters = 5.0;
        internal const double RecordedRelativeOffsetSpikeMinDeviationMeters = 15.0;

        internal static bool IsFinite(Vector3d value)
        {
            return !double.IsNaN(value.x) && !double.IsInfinity(value.x)
                && !double.IsNaN(value.y) && !double.IsInfinity(value.y)
                && !double.IsNaN(value.z) && !double.IsInfinity(value.z);
        }

        /// <summary>
        /// Accessor for active ghost chains — used by ParsekUI for chain status display.
        /// Returns null when no chains are active.
        /// </summary>
        internal Dictionary<uint, GhostChain> ActiveGhostChains => activeGhostChains;

        internal int ghostChainEvaluationCount;

        /// <summary>
        /// How many times THIS flight-scene instance has run
        /// <see cref="EvaluateAndApplyGhostChains"/>, on any of its exits. Zero means the
        /// scene never derived its chain set (the component is re-created per scene, so a
        /// load always starts at zero). Read by the automation seam's
        /// <c>ListHandles kind=chains</c> to tell "derived an empty set" from "never derived".
        /// </summary>
        internal int GhostChainEvaluationCount => ghostChainEvaluationCount;

        internal void Log(string message) => ParsekLog.Verbose("Flight", message);
        internal void ScreenMessage(string message, float duration) => ParsekLog.ScreenMessage(message, duration);
    }
}
