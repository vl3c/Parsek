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
    public partial class ParsekFlight : MonoBehaviour, IGhostPositioner
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

        internal enum PostSwitchAutoRecordStartDecision
        {
            None,
            PromoteTrackedRecording,
            RestoreAndPromoteTrackedRecording,
            StartFreshRecording
        }

        internal enum PostSwitchAutoRecordArmDecision
        {
            None,
            ArmTrackedBackgroundMember,
            ArmOutsider
        }

        internal enum PostSwitchAutoRecordTrigger
        {
            None,
            EngineActivity,
            SustainedRcsActivity,
            AttitudeChange,
            CrewChange,
            ResourceChange,
            PartStateChange,
            LandedMotion,
            OrbitChange
        }

        internal enum PostSwitchAutoRecordSuppressionReason
        {
            None,
            Disabled,
            AlreadyRecording,
            NoActiveVessel,
            ActiveVesselMismatch,
            GhostMapVessel,
            RestoreInProgress,
            PendingTransition,
            WarpActive,
            PackedOrOnRails
        }

        internal struct MissedVesselSwitchRecoveryDiagnosticContext
        {
            public bool IsRecovery;
            public uint ActiveVesselPid;
            public uint RecorderVesselPid;
            public bool HasRecorder;
            public bool RecorderIsRecording;
            public bool RecorderIsBackgrounded;
            public bool RecorderChainToVesselPending;
            public bool ActiveVesselTrackedInBackground;
            public bool ActiveVesselAlreadyArmedForPostSwitchAutoRecord;
            public string ActiveTreeRecordingId;
            public int ActiveTreeBackgroundMapCount;

            internal string BuildRecStateRateLimitKey()
            {
                if (!IsRecovery) return null;

                return string.Format(CultureInfo.InvariantCulture,
                    "missed-vessel-switch|activePid={0}|recorderPid={1}|hasRecorder={2}|rec={3}/{4}|chain={5}|tracked={6}|armed={7}|activeRec={8}|bgCount={9}",
                    ActiveVesselPid,
                    RecorderVesselPid,
                    HasRecorder ? 1 : 0,
                    RecorderIsRecording ? 1 : 0,
                    RecorderIsBackgrounded ? 1 : 0,
                    RecorderChainToVesselPending ? 1 : 0,
                    ActiveVesselTrackedInBackground ? 1 : 0,
                    ActiveVesselAlreadyArmedForPostSwitchAutoRecord ? 1 : 0,
                    string.IsNullOrEmpty(ActiveTreeRecordingId) ? "-" : ActiveTreeRecordingId,
                    ActiveTreeBackgroundMapCount);
            }
        }

        internal struct PostSwitchOrbitSnapshot
        {
            public bool IsValid;
            public string BodyName;
            public double SemiMajorAxis;
            public double Eccentricity;
            public double Inclination;
            public double LongitudeOfAscendingNode;
            public double ArgumentOfPeriapsis;
        }

        private sealed class PostSwitchAutoRecordState
        {
            public uint VesselPid;
            public string VesselName;
            public double ArmedAtUt;
            public string ArmedReason;
            public bool TrackedInActiveTree;
            public string FreshStartParentRecordingId;
            public bool BaselineCaptured;
            public double BaselineCapturedUt;
            public double ComparisonsReadyUt;
            public double NextManifestEvaluationUt;
            public Vector3d BaselineWorldPosition;
            public Quaternion BaselineWorldRotation;
            public TrajectoryPoint? BaselineSeedPoint;
            public PostSwitchOrbitSnapshot BaselineOrbit;
            public double AttitudeThresholdExceededAtUt = double.NaN;
            public Dictionary<string, ResourceAmount> BaselineResources;
            public Dictionary<string, InventoryItem> BaselineInventory;
            public Dictionary<string, int> BaselineCrew;
            public HashSet<string> BaselinePartStateTokens = new HashSet<string>();
            public bool ModuleCachesDirty;
            public int CachedPartCount;
            public List<(Part part, ModuleEngines engine, int moduleIndex)> CachedEngines
                = new List<(Part, ModuleEngines, int)>();
            public HashSet<ulong> ActiveEngineKeys = new HashSet<ulong>();
            public Dictionary<ulong, float> EngineThrottleMap = new Dictionary<ulong, float>();
            public List<(Part part, ModuleRCS rcs, int moduleIndex)> CachedRcsModules
                = new List<(Part, ModuleRCS, int)>();
            public Dictionary<ulong, int> RcsFrameCountMap = new Dictionary<ulong, int>();
            public HashSet<ulong> ActiveRcsKeys = new HashSet<ulong>();
            public Dictionary<ulong, float> RcsThrottleMap = new Dictionary<ulong, float>();
        }

        internal static ParsekFlight Instance { get; private set; }

        /// <summary>
        /// #431: returns the id of the currently-live recording for GameStateEvent tagging.
        /// Empty string when there is no active flight, no active tree, or the tree has no active leaf.
        /// The secondary fallback to <see cref="RecordingStore.PendingTree"/> while
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

        /// <summary>Above this altitude, skip the per-frame terrain clamp (no terrain risk in flight).</summary>
        const double TerrainClampAltitudeLimit = 10000;

        // Recording (delegated to FlightRecorder)
        private FlightRecorder recorder;

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
        private List<OrbitSegment> orbitSegments => recorder?.OrbitSegments ?? noOrbitSegments;
        private static readonly List<TrajectoryPoint> noRecording = new List<TrajectoryPoint>();
        private static readonly List<OrbitSegment> noOrbitSegments = new List<OrbitSegment>();

        // Auto-record from LANDED: tracks when the active vessel last entered LANDED state.
        // Only triggers auto-record if the vessel was settled for at least this long,
        // filtering out physics bounces (rovers, crashes, EVA kerbals).
        private double lastLandedUT = -1;
        private const double LandedSettleThreshold = 5.0; // seconds
        private const double PostSwitchLandedMotionDistanceThreshold = 0.5;
        private const double PostSwitchLandedMotionSpeedThreshold = 0.5;
        private const double PostSwitchOrbitSemiMajorAxisThresholdMeters = 5.0;
        private const double PostSwitchOrbitEccentricityThreshold = 1e-6;
        private const double PostSwitchOrbitAngleThresholdDegrees = 0.01;
        private const float PostSwitchAttitudeChangeThresholdDegrees = 3f;
        private const double PostSwitchAttitudeChangeDebounceSeconds = 0.2;
        private const double PostSwitchResourceDeltaEpsilon = 0.01;
        private const double PostSwitchManifestEvaluationIntervalSeconds = 0.25;
        private const double PostSwitchManifestEvaluateNextFrameUt = 0.0;
        internal const int PostSwitchManifestDeltaSkipped = -1;
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
        private string pendingWatchAfterFFId;

        // Manual playback state (preview of current recording)
        private bool isPlaying = false;
        private double playbackStartUT;
        private double recordingStartUT;
        private GameObject ghostObject;
        private List<Material> previewGhostMaterials = new List<Material>();
        private GhostPlaybackState previewGhostState;
        private Recording previewRecording;
        internal int lastPlaybackIndex = 0;

        // Ghost state is owned by the engine (T25). This property forwards to the engine field
        // so existing ParsekFlight code accesses engine-owned collections transparently.
        private Dictionary<int, GhostPlaybackState> ghostStates => engine.ghostStates;

        // Cached TimelineGhosts dictionary — rebuilt once per frame on first access (T20)
        private Dictionary<int, GameObject> cachedTimelineGhosts = new Dictionary<int, GameObject>();
        private int cachedTimelineGhostsFrame = -1;

        // Real Spawn Control: proximity-based nearby craft detection
        private readonly List<NearbySpawnCandidate> nearbySpawnCandidates = new List<NearbySpawnCandidate>();
        private float nextProximityCheckTime;
        // Real Spawn Control: the last Warp-to-spawn whose outcome is still owed a word when
        // the vessel does not appear (SpawnWarpOutcomeWatch). Null id = nothing owed.
        private string spawnWarpWatchRecordingId;
        private string spawnWarpWatchVesselName;
        private double spawnWarpWatchTargetUT;
        private float spawnWarpWatchArmedRealtime;
        private bool spawnWarpWatchWalkbackAtArm;
        private int spawnWarpWatchCollisionCountAtArm;
        private const float ProximityCheckIntervalSec = 1.5f;
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
        private struct ProximityVelocitySample
        {
            public Vector3d activePos;
            public Vector3d ghostPos;
            public float time;
        }

        private struct RelativeAnchorPose
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

        private sealed class RelativeAnchorPoseSnapshot
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

        private readonly Dictionary<string, ProximityVelocitySample> proximityVelocitySamples =
            new Dictionary<string, ProximityVelocitySample>();
        // Sample is trustworthy when 0.5s <= dt <= 5s. Below: jitter dominates.
        // Above: sample is stale (time warp, scene change, paused) — discard.
        private const float ProximityVelocitySampleMinDt = 0.5f;
        private const float ProximityVelocitySampleMaxDt = 5.0f;
        // Tracks the active vessel pid the proximity samples were captured against; on
        // vessel switch the cached activePos belongs to a different craft and must be
        // discarded (otherwise the next scan would see a teleport-sized delta).
        private uint lastProximityActiveVesselPid;
        private readonly HashSet<string> notifiedSpawnRecordingIds = new HashSet<string>();
        private int proximityCheckGeneration;
        internal int ProximityCheckGeneration => proximityCheckGeneration;
        internal List<NearbySpawnCandidate> NearbySpawnCandidates => nearbySpawnCandidates;

        // --- Floating-origin correction ---
        // Ghosts are plain GameObjects not registered with KSP's FloatingOrigin.
        // FloatingOrigin shifts all registered objects in LateUpdate(), but ghosts
        // positioned in Update() would be left behind, causing one-frame jumps.
        // We store each ghost's last positioning inputs and re-apply in LateUpdate()
        // so the position is correct in the post-shift frame that actually renders.

        private enum GhostPosMode { PointInterp, SinglePoint, Orbit, Surface, Relative, CheckpointPoint }

        internal enum GhostPositionReapplyPhase { LateUpdate, CameraPreCull }

        private struct GhostPosEntry
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

        private readonly List<GhostPosEntry> ghostPosEntries = new List<GhostPosEntry>();
        private int ghostPreCullReapplyFrame = -1;
        private int ghostLateUpdateReapplyFrame = -1;

        private void AddOrReplaceGhostPosEntry(GhostPosEntry entry)
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
                    + ShortRecordingId(entry.recordingId)
                    + " mode=" + entry.mode
                    + " removed=" + removed.ToString(CultureInfo.InvariantCulture)
                    + " reason=same-frame-latest-position-wins",
                    5.0);
            }

            ghostPosEntries.Add(entry);
        }

        // Auto-record: EVA from pad triggers recording after vessel switch completes
        private bool pendingAutoRecord = false;
        private PostSwitchAutoRecordState postSwitchAutoRecord;

        // Boarding confirmation via onCrewBoardVessel
        private uint pendingBoardingTargetPid; // vessel PID from onCrewBoardVessel, 0 = none
        private int boardingConfirmFrames;     // frames since boarding event (auto-clear after 10)

        // Pending dock transition (set by OnPartCouple, consumed by HandleTreeDockMerge)
        private uint pendingDockMergedPid;          // merged vessel pid, 0 = no pending dock
        private int dockConfirmFrames;              // frame counter for confirmation window (auto-clear after 5)

        // Recording tree mode (null when not in tree mode)
        private RecordingTree activeTree;

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
                RecordingStore.PendingTree,
                RecordingStore.PendingTreeStateValue,
                null, // standalone pending removed (T56)
                pendingSplitRecorder,
                pendingSplitInProgress,
                Planetarium.GetUniversalTime(),
                HighLogic.LoadedScene);

        internal bool HasArmedPostSwitchAutoRecordWatch => postSwitchAutoRecord != null;

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
        private BackgroundRecorder backgroundRecorder;

        // #267: reentrancy guard for RestoreActiveTreeFromPending / ForVesselSwitch.
        // Set at coroutine entry, cleared in finally. Guards OnVesselSwitchComplete,
        // OnVesselWillDestroy, and FinalizeTreeOnSceneChange from mutating activeTree /
        // recorder / backgroundRecorder while the restore coroutine is mid-yield.
        internal static bool restoringActiveTree;

        // Split event detection: deduplication and race-condition guard
        private double lastBranchUT = -1;
        private HashSet<uint> lastBranchVesselPids = new HashSet<uint>();
        private bool pendingSplitInProgress;
        private FlightRecorder pendingSplitRecorder;
        private DeferredSplitCheckTrigger pendingDeferredSplitCheckTrigger;
        private double pendingSplitTriggerUT = double.NaN;
        // Vessel PIDs that existed before a joint break (for filtering pre-existing vessels)
        private HashSet<uint> preBreakVesselPids;
        // Vessels created by decouple during recording (caught via onPartDeCoupleNewVesselComplete
        // synchronously during Part.decouple(), before KSP's debris cleanup can destroy them).
        // Subscribed for the entire recording session, consumed by the next deferred split check.
        private List<Vessel> decoupleCreatedVessels;
        // Controller status captured at creation time (vessel parts may be cleared before deferred check)
        private Dictionary<uint, bool> decoupleControllerStatus;
        // Exact split-time seed points captured from onPartDeCoupleNewVesselComplete.
        // Used to avoid seeding breakup children from the later deferred-check frame.
        private Dictionary<uint, TrajectoryPoint> decoupleCreatedTrajectoryPoints;
        // Every onPartDeCoupleNewVesselComplete seen while the listener is subscribed, new
        // vessel pid -> the vessel it came off (foreign origins included). The deferred
        // joint-break scan drops a candidate whose origin chain never reaches the recorded
        // vessel (SegmentBoundaryLogic.IsForeignDecoupleOrigin). Cleared by the deferred
        // check and whenever the listener is torn down.
        private readonly Dictionary<uint, uint> decoupleOriginByNewPid = new Dictionary<uint, uint>();

        // Docking-port undock handoff (onPartUndock -> onVesselsUndocking, same frame).
        // onPartUndock fires FIRST inside Part.Undock(), while the combined vessel is still
        // intact (ideal moment to capture the departing root part's pre-split origin seed and
        // the structural-event snapshot), but the new vessel's persistentId is not assigned
        // until KSP finishes the split and fires onVesselsUndocking at the end of the same
        // call. We stash the pre-split seed here and let OnVesselsUndocking author the actual
        // Undock branch with the final pids.
        private TrajectoryPoint? pendingUndockRootPartSeed;

        // Crash coalescer: groups rapid structural splits into single BREAKUP events
        private CrashCoalescer crashCoalescer = new CrashCoalescer();

        // Merge event detection (tree mode)
        private bool pendingTreeDockMerge;           // true when a tree dock merge is pending
        private uint pendingDockAbsorbedPid;         // PID of absorbed vessel in dock merge
        // Route partner PID resolved from the couple event (independent of BackgroundMap).
        // 0 means partner was foreign or unresolved; non-zero means the merge child should
        // carry a route window. Cleared when the pending dock state is cleared.
        private uint pendingDockRouteTargetPid;
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
        private uint pendingDockPartnerPid;
        // Launch guid (Vessel.id) of the vessel pendingDockPartnerPid names, read from the
        // live couple event. Feeds BranchPoint.TargetVesselGuid only; set and cleared with
        // pendingDockPartnerPid. Null when the partner stamp is 0 or its vessel had no id.
        private string pendingDockPartnerGuid;
        // Connection producer that made the pending couple, classified from the live
        // onPartCouple parts (DockingPort / Grapple / Unknown). Stamped on the merge
        // branch data and the route connection window so admission can gate by
        // producer. None when no couple is pending.
        private RouteConnectionKind pendingDockTransferKind;
        // Pre-couple partner vessel snapshot, captured in OnPartCouple while data.from
        // and data.to still reference DISTINCT vessels. Used by CreateMergeBranch to
        // build the route window's endpoint baseline from the partner's pre-dock state,
        // instead of inflating it with the post-couple merged vessel (which includes
        // transport parts and gives DOCK_ENDPOINT_RESOURCES = transport + endpoint).
        private ConfigNode pendingDockPartnerSnapshot;
        private uint pendingDockPartnerSnapshotPid;
        // Pre-couple SELF (transport / recorded vessel) snapshot, captured in OnPartCouple
        // while data.from and data.to still reference DISTINCT vessels. Mirror of
        // pendingDockPartnerSnapshot for the transport side: CreateMergeBranch uses it to
        // build the route window's DOCK_TRANSPORT_RESOURCES baseline from the transport's
        // pre-dock state, so a same-frame stock crossfeed equalisation that drained the
        // transport tank into the depot does not deflate the baseline and trip the strict
        // MixedPickupDelivery pickup gate on a clean delivery run. Cleared with the partner
        // snapshot.
        private ConfigNode pendingDockSelfSnapshot;
        private uint pendingDockSelfSnapshotPid;
        private RouteEndpoint? pendingDockRouteEndpointAtDock;
        private int pendingDockRouteEndpointSituation = -1;
        private bool pendingBoardingTargetInTree;    // true if boarding target is in the tree

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
        private HashSet<long> loggedOrbitSegments = new HashSet<long>();
        private HashSet<long> loggedOrbitRotationSegments = new HashSet<long>();

        // Anchor vessel tracking moved to engine (T25).
        private HashSet<uint> loadedAnchorVessels => engine.loadedAnchorVessels;

        // Phase 6b: Ghost chain evaluation + vessel ghosting
        private VesselGhoster vesselGhoster;
        private Dictionary<uint, GhostChain> activeGhostChains;

        // Deferred spawn queue moved to ParsekPlaybackPolicy (#132)
        // Phase F: timelineResourceReplayPausedLogged removed alongside ApplyResourceDeltas.
        private bool wasWarpActive = false; // tracks previous warp state for exit detection
        private double warpStartUT = 0.0;  // UT when warp began — used for facility visual updates
        private bool hasLastWarpCheckpointEvent;
        private RecordingTree lastWarpCheckpointTree;
        private float lastWarpCheckpointRate;
        private double lastWarpCheckpointUT;
        // Camera follow (watch mode) — extracted to WatchModeController
        private WatchModeController watchMode;
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
        private GhostPlaybackEngine engine;
        internal GhostPlaybackEngine Engine => engine;
        private ParsekPlaybackPolicy policy;

        // Accessors for WatchModeController (delegates need host services)
        internal bool ShouldLoopPlaybackForWatch(Recording rec) => GhostPlaybackEngine.ShouldLoopPlayback(rec);
        internal double GetLoopIntervalSecondsForWatch(Recording rec, int recIdx = -1)
        {
            return GetLoopIntervalSeconds(rec, recIdx);
        }
        internal bool TryGetLoopScheduleForWatch(
            Recording rec,
            int recIdx,
            out double playbackStartUT,
            out double scheduleStartUT,
            out double duration,
            out double intervalSeconds)
            => TryGetLoopSchedule(rec, recIdx,
                out playbackStartUT, out scheduleStartUT, out duration, out intervalSeconds);
        internal bool TryComputeLoopPlaybackUTForWatch(
            Recording rec, double currentUT,
            out double loopUT, out long cycleIndex, out bool inPauseWindow,
            int recIdx = -1)
        {
            return TryComputeLoopPlaybackUT(rec, currentUT,
                out loopUT, out cycleIndex, out inPauseWindow, recIdx);
        }
        // Mission loop-unit watch sync (Phase D2): resolve a watched member's span loopUT so
        // watch entry syncs ghost visuals at the same clock the engine renders the member at.
        // Returns false (loopUT = currentUT) for non-unit-member / non-looping watch.
        internal bool TryResolveUnitMemberPlaybackUTForWatch(
            int recordingIndex, double currentUT, double memberStartUT, double memberEndUT, out double loopUT)
            => engine.TryResolveUnitMemberPlaybackUT(
                recordingIndex, currentUT, memberStartUT, memberEndUT, out loopUT);
        internal void PositionGhostAtForWatch(GameObject ghost, TrajectoryPoint point) => PositionGhostAt(ghost, point);

        // Cached per-frame allocations for engine path (avoid GC pressure)
        private readonly List<IPlaybackTrajectory> cachedTrajectories = new List<IPlaybackTrajectory>();

        // Mission loop-unit drive cache (Phase D2). The LoopUnitSet only changes when the looping
        // mission, its tree/selection/cadence, or the committed list changes, so the (allocating,
        // Verbose-logging) MissionLoopUnitBuilder.Build is gated behind a cheap signature compare.
        // SetLoopUnits still runs every frame with the cached value so the engine always sees the
        // current set. builtLoopUnits is the signature-cached build; cachedLoopUnits is the view
        // every playback consumer reads, the build minus the units still before their first loop
        // instance (MissionLoopUnitBuilder.ResolveLiveUnits), so a looped mission's first run plays
        // as an ordinary recording until its anchor.
        private string lastLoopUnitSignature;
        private GhostPlaybackLogic.LoopUnitSet builtLoopUnits = GhostPlaybackLogic.LoopUnitSet.Empty;
        private GhostPlaybackLogic.LoopUnitSet cachedLoopUnits = GhostPlaybackLogic.LoopUnitSet.Empty;

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
        private readonly MapRender.MapViewScene mapViewScene = new MapRender.MapViewScene();
        private readonly List<RecordingAnchorCandidate> cachedGhostRecordingAnchorCandidates =
            new List<RecordingAnchorCandidate>();
        private TrajectoryPlaybackFlags[] cachedFlags;
        // Per-committed-index relay window inputs, filled by ComputePlaybackFlags.
        private GhostCommNetEligibilityInput[] ghostCommNetInputs = new GhostCommNetEligibilityInput[0];
        private readonly List<GhostCommNetCandidate> ghostCommNetCandidates = new List<GhostCommNetCandidate>();
        private readonly List<double> ghostCommNetClaimScratch = new List<double>();
        private readonly List<KeyValuePair<double, double>> ghostCommNetCarrierScratch =
            new List<KeyValuePair<double, double>>();
        private GhostCommNetManager ghostCommNet;
        private readonly HashSet<string> activeGhostSkipReasonLogIdentities = new HashSet<string>();
        private readonly Dictionary<string, int> reFlyAnchorHoldStartFrameByAnchor =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> reFlyAnchorHoldCountsThisFrame =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> reFlyAnchorHoldReasonsThisFrame =
            new Dictionary<string, string>(StringComparer.Ordinal);
        // Reused per-frame scratch for the batched spawn-suppression summary (see
        // ComputePlaybackFlags). Cleared and refilled each frame; never read across frames.
        private readonly Dictionary<string, int> spawnSuppressedReasonScratch =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> reFlyAnchorHoldReleaseScratch = new List<string>();
        private readonly Dictionary<string, ReFlyDenseAbsolutePlaybackFrameCacheEntry> reFlyDenseAbsolutePlaybackFrameCache =
            new Dictionary<string, ReFlyDenseAbsolutePlaybackFrameCacheEntry>(StringComparer.Ordinal);
        private readonly HashSet<string> loggedReFlyDenseAbsolutePlaybackFrameSelections =
            new HashSet<string>(StringComparer.Ordinal);
        private int reFlySettlePoseLogActiveFrame = -1;
        // Scene-scoped; cleared alongside TerrainCacheBuckets.Clear() in OnSceneChangeRequested.
        private static readonly Dictionary<string, TerrainCorrector.TailLiftPlan> s_tailLiftPlanCache =
            new Dictionary<string, TerrainCorrector.TailLiftPlan>();
        private static readonly Dictionary<string, string> s_tailLiftPlanTerminalBodyCache =
            new Dictionary<string, string>();
        // P2 review (PR #706): the manual-preview Recording (recorder.CaptureAtStop)
        // is never added to RecordingStore.CommittedRecordings, so the tail-lift
        // resolver would log recording-missing every frame and skip the lift.
        // ParsekFlight registers/clears the preview source in StartPlayback /
        // StopPlayback so ResolveTailLiftPlan can find it.
        private static Recording s_previewRecordingForTailLift;

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
            engine = new GhostPlaybackEngine(this);
            // Tracing-only attitude residual source (GhostRenderTrace AfterUpdate dRotDeg=).
            GhostRenderTrace.ExpectedAttitudeSource = TryResolveRecordedAttitudeForTrace;
            engine.OnLoopCameraAction += watchMode.HandleLoopCameraAction;
            engine.OnOverlapCameraAction += watchMode.HandleOverlapCameraAction;
            engine.IsWatchedGhostStateResolver = (recordingIndex, state) =>
                watchMode != null && watchMode.IsWatchedGhostState(recordingIndex, state);
            engine.ResolvePlaybackDistanceOverride = ResolvePlaybackDistanceForEngine;
            engine.ResolvePlaybackActiveVesselDistanceOverride = ResolvePlaybackActiveVesselDistanceForEngine;
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

            HandleTreeDockMerge();
            HandleTreeBoardMerge();
            HandleUnconfirmedBoardingStop();

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

            HandleJointBreakDeferredCheck();
            TickCrashCoalescer();
            SpawnedHarvesterRestore.Tick();

            // Skip auto-record and chain handlers while a split is being processed
            if (pendingSplitInProgress)
            {
                if (ParsekLog.IsVerboseEnabled)
                    ParsekLog.VerboseRateLimited("Flight", "update_split_suppressed", "Update suppressed: pendingSplitInProgress");
                // Still run playback and input
                HandleInput();
                if (isPlaying) UpdatePlayback();
                UpdateTimelinePlaybackViaEngine();
                TimeJumpManager.TickPlacementWatch();
                TickSpawnWarpWatch();
                return;
            }

            HandleDeferredAutoRecordEva();

            HandleInput();

            if (isPlaying)
            {
                UpdatePlayback();
            }

            UpdateTimelinePlaybackViaEngine();
            TimeJumpManager.TickPlacementWatch();
            TickSpawnWarpWatch();
            UpdateProximityCheck();
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

            ApplyGhostPosEntries(GhostPositionReapplyPhase.LateUpdate);

            ClampGhostsToTerrain(GhostPositionReapplyPhase.LateUpdate);
            ghostLateUpdateReapplyFrame = Time.frameCount;

            LogReFlySettleActiveVesselPoseIfArmed("late-update", Time.frameCount);

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
            ApplyGhostPosEntries(GhostPositionReapplyPhase.CameraPreCull);
            ClampGhostsToTerrain(GhostPositionReapplyPhase.CameraPreCull);
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
            PositionGhostAtSurface(ghost, surfPos, allowActivation: false, recordingId: recordingId);
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
            DrawGhostLabels();

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
            DisarmPostSwitchAutoRecord("ParsekFlight destroyed");

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
            StopPlayback();
            watchMode.ExitWatchMode();
            InputLockManager.RemoveControlLock(WatchModeController.WatchModeLockId); // safety net

            // Phase 6b: Clean up ghost chain state
            ClearClaimedVesselHoldGhosts("scene-teardown");
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
            ClearOrbitPlaybackCaches();
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
            ClearGhostSkipReasonLogState();
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
                FinalizeTreeOnSceneChange(scene);
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
            StopPlayback();
            DestroyAllTimelineGhosts();
            GhostVisualBuilder.ClearCanopyCache();
            GhostVisualBuilder.ClearAnimationSampleCache();
            GhostVisualBuilder.ClearFxPrefabCache();
            GhostVisualBuilder.ClearAnimateHeatCache();
        }

        private void FinalizeTreeOnSceneChange(GameScenes scene)
        {
            FinalizeTreeOnSceneChangeCore(
                scene,
                Planetarium.GetUniversalTime(),
                logRecorderState: true);
        }

        internal void FinalizeTreeOnSceneChangeForTesting(GameScenes scene, double commitUT)
        {
            FinalizeTreeOnSceneChangeCore(scene, commitUT, logRecorderState: false);
        }

        /// <summary>
        /// Pre-transition finalize entry for the in-flight merge dialog
        /// (<see cref="MergeDialog.ShowTreeDialog"/>'s
        /// <c>preCommitFinalize</c> callback). Identical to what
        /// <see cref="OnSceneChangeRequested"/> would have run a few frames
        /// later. The dialog button handler invokes this BEFORE
        /// <see cref="MergeDialog.MergeCommit"/> /
        /// <see cref="MergeDialog.MergeDiscard"/> so those operate on the
        /// just-stashed pending tree.
        ///
        /// <para>Mirrors the recorder-state prep that
        /// <see cref="OnSceneChangeRequested"/> runs immediately before
        /// <see cref="FinalizeTreeOnSceneChange"/>: clear scene-change
        /// transient state before <c>MergeCommit</c> and the optimization
        /// pass run.</para>
        /// </summary>
        internal void FinalizeTreeOnSceneChangeForCallback(GameScenes scene)
        {
            ClearSceneChangeTransientState();
            FinalizeTreeOnSceneChangeCore(
                scene,
                Planetarium.GetUniversalTime(),
                logRecorderState: true);
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

        /// <summary>
        /// The active tree's recording that the live recorder is writing for the vessel
        /// <paramref name="vesselPid"/> right now, or null. The live binding is the identity
        /// proof for an in-flight Recover: a resumed spawned vessel whose spawn regenerated
        /// its identity carries the original launch guid on its recording but a fresh one on
        /// the vessel, so a launch-identity match alone cannot find its recording.
        /// </summary>
        internal string LiveRecordingIdForVessel(uint vesselPid)
        {
            if (vesselPid == 0 || activeTree?.Recordings == null || recorder == null)
                return null;
            if (!recorder.IsRecording || recorder.RecordingVesselId != vesselPid)
                return null;
            string id = activeTree.ActiveRecordingId;
            if (string.IsNullOrEmpty(id)
                || !activeTree.Recordings.TryGetValue(id, out Recording rec)
                || rec == null
                || rec.VesselPersistentId != vesselPid)
                return null;
            return id;
        }

        /// <summary>
        /// Live-state idle-on-pad check used by the
        /// <c>HighLogic.LoadScene</c> prefix's fast path. Returns true if
        /// every recording in the active tree is idle-on-pad (max
        /// distance from launch &lt; pad-localized threshold) computed
        /// from live data via
        /// <see cref="LiveVesselSnapshot.BackfillMaxDistanceFromBodyFixedSurfaces(Recording)"/>,
        /// which reads the body-fixed surface of an Absolute section
        /// (<c>frames</c>) and of a Relative section (<c>bodyFixedFrames</c>)
        /// and never a Relative section's anchor-local <c>frames</c>.
        ///
        /// <para>This method MUTATES: it calls
        /// <see cref="FlushRecorderIntoActiveTreeForSerialization"/> first
        /// so the live recorder's <c>Recording</c> + open
        /// <c>TrackSections</c> become populated on the tree recordings
        /// (<see cref="LiveVesselSnapshot.BackfillMaxDistanceFromBodyFixedSurfaces(Recording)"/>
        /// reads <c>rec.TrackSections</c> only). Without this, atmospheric
        /// flights that haven't crossed an env boundary or saved yet have
        /// empty <c>TrackSections</c> and the helper would falsely report
        /// idle (Opus pass-7 review P1). The flush is non-destructive: the
        /// recorder stays IsRecording=true and the only side effect is one
        /// extra TrackSection boundary at the current UT - identical to
        /// what every <c>OnSave</c> tick produces.</para>
        ///
        /// <para>Mirrors the <see cref="IsTreeIdleOnPad(RecordingTree)"/>
        /// data-loss safeguard (in the <c>anyHasPoints</c> block of that
        /// function): a tree where no recording has any trajectory points
        /// is data-loss, not idle. We refuse to classify and return false
        /// so the dialog still shows.</para>
        /// </summary>
        internal bool IsActiveTreeIdleOnPad()
        {
            if (activeTree == null) return false;
            if (activeTree.Recordings == null || activeTree.Recordings.Count == 0)
                return false;
            // Mirror the existing defensive guard pattern used by other
            // mutating paths in this file (e.g. line 2727 in
            // FinalizeTreeOnSceneChangeCore): if a restore coroutine owns
            // the recorder/activeTree, do not flush.
            if (restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    "IsActiveTreeIdleOnPad: skipped - restore coroutine in progress");
                return false;
            }

            if (TreeHasPlacedGroundPartsForIdle(activeTree, "IsActiveTreeIdleOnPad"))
                return false;

            // Flush live recorder data into the tree so subsequent walks
            // over rec.TrackSections / rec.Points see the in-flight data.
            FlushRecorderIntoActiveTreeForSerialization();

            // After the flush: a flag planted this flight is still in the recorder buffer before it.
            if (TreeHasPlantedFlagsForIdle(activeTree, "IsActiveTreeIdleOnPad")
                || TreeMovesHeldKerbalForIdle(activeTree, "IsActiveTreeIdleOnPad")
                || TreeInteractsWithForeignVesselForIdle(activeTree, "IsActiveTreeIdleOnPad")
                || TreeHasConverterWorkForIdle(activeTree, "IsActiveTreeIdleOnPad")
                || TreeHasLabPayoutsForIdle(activeTree, "IsActiveTreeIdleOnPad")
                || TreeHasPendingLabRecoveryForIdle(activeTree, "IsActiveTreeIdleOnPad"))
                return false;

            bool anyHasPoints = false;
            foreach (var rec in activeTree.Recordings.Values)
            {
                if (rec == null) continue;
                LiveVesselSnapshot.BackfillMaxDistanceFromBodyFixedSurfaces(rec);
                if (rec.Points != null && rec.Points.Count > 0)
                    anyHasPoints = true;
                if (!IsIdleOnPad(rec))
                {
                    ParsekLog.Verbose("Flight",
                        $"IsActiveTreeIdleOnPad: '{rec.VesselName}' maxDist=" +
                        $"{rec.MaxDistanceFromLaunch:F1}m - not idle");
                    return false;
                }
            }

            // Bug #290d safeguard: a tree where no recording has trajectory
            // points is data-loss, not idle-on-pad. Don't auto-discard.
            if (!anyHasPoints)
            {
                ParsekLog.Verbose("Flight",
                    $"IsActiveTreeIdleOnPad: all {activeTree.Recordings.Count} recordings " +
                    "have 0 points - cannot determine idle, returning false");
                return false;
            }

            // The flush above copied the live root's StartSituation / LaunchSiteName onto
            // its tree recording (FlushRecorderIntoActiveTreeForSerialization), so the
            // evidence check sees the live state.
            if (!TreeHasPadEvidenceForIdle(activeTree, "IsActiveTreeIdleOnPad"))
                return false;

            ParsekLog.Verbose("Flight",
                $"IsActiveTreeIdleOnPad: all {activeTree.Recordings.Count} recordings " +
                "within 30m - idle on pad");
            return true;
        }

        /// <summary>
        /// Pre-transition idle-on-pad fast path: tear down the live
        /// recorder / activeTree without going through finalize / stash.
        /// Mirrors the suppressed-discard cleanup at
        /// <see cref="DiscardActiveTreeForSuppressedSceneExit"/>: stop
        /// the recorder, clear the
        /// <see cref="Patches.PhysicsFramePatch.ActiveRecorder"/> and
        /// <see cref="Patches.PhysicsFramePatch.BackgroundRecorderInstance"/>
        /// references, then null the fields. Also runs the same prep as
        /// <see cref="OnSceneChangeRequested"/> (transient state)
        /// since the prefix returns true after this and
        /// <see cref="OnSceneChangeRequested"/> only re-runs that prep
        /// once it sees <c>activeTree == null</c>.
        ///
        /// <para>After this returns, the prefix's blocked
        /// <c>HighLogic.LoadScene</c> proceeds and
        /// <see cref="OnSceneChangeRequested"/> sees
        /// <c>activeTree == null</c>, so no pending tree is stashed and
        /// the destination scene's OnLoad does not show a deferred merge
        /// dialog.</para>
        /// </summary>
        internal void AutoDiscardIdleActiveTree(string reason)
        {
            AutoDiscardActiveTreeCore(
                reason: reason,
                screenMessage: "Recording discarded - idle on pad",
                ledgerRecalcReason: "suppressed-scene-exit-discard",
                reapSidecars: true);
        }

        /// <summary>
        /// Reason-aware variant of <see cref="AutoDiscardIdleActiveTree"/>
        /// that lets callers override the on-screen toast and the
        /// ledger-recalc reason string. Same teardown body as the original
        /// entry point; introduced for the pre-switch-dialog Case B
        /// no-session Discard handler (PR #876 round-6 review) where the
        /// "idle on pad" toast and "suppressed-scene-exit-discard" reason
        /// are wrong-context — the player has been actively flying out of
        /// the bubble, not sitting on the pad.
        /// </summary>
        /// <param name="reason">Internal log reason (subsystem trace).</param>
        /// <param name="screenMessage">On-screen toast shown to the player.</param>
        /// <param name="ledgerRecalcReason">Reason string passed to
        /// <see cref="LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions"/>;
        /// grep-able from ledger-recalc log lines.</param>
        /// <param name="reapSidecars">
        /// Whether this teardown also reaps the discarded tree's on-disk sidecars
        /// (see <see cref="DiscardSidecarReap"/>). True for every gameplay path;
        /// false only for the M-A2 <c>DiscardTree</c> test-command verb, which owns
        /// its own reap and its own summary line (pinned verbatim by the S0.5
        /// harness spec) and would otherwise report <c>reaped=0</c> on a
        /// double pass.
        /// </param>
        internal void AutoDiscardActiveTreeWithMessage(
            string reason,
            string screenMessage,
            string ledgerRecalcReason,
            bool reapSidecars = true)
        {
            AutoDiscardActiveTreeCore(
                reason: reason,
                screenMessage: screenMessage,
                ledgerRecalcReason: ledgerRecalcReason,
                reapSidecars: reapSidecars);
        }

        /// <summary>
        /// Evaluates whether the armed <see cref="SwitchSegmentSession"/>'s
        /// resumed segment (Tracking-Station Fly / KSC marker Fly / map Switch-To)
        /// changed nothing meaningful and can be auto-discarded so we do not
        /// prolong the ghost state for a segment that did nothing. Same spirit as
        /// <see cref="RecordingOptimizer.TrimBoringTail"/> ("so the ghost finishes
        /// quickly and the real vessel spawns promptly") and the idle-on-pad
        /// auto-discard.
        ///
        /// <para>This method MUTATES like <see cref="IsActiveTreeIdleOnPad"/>: it
        /// flushes the live recorder into the active tree
        /// (<see cref="FlushRecorderIntoActiveTreeForSerialization"/>) so the
        /// segment recording carries its in-flight payload before the pure
        /// classifier reads it. Guards out the restore-coroutine,
        /// Re-Fly-session, and active-merge-journal states (never auto-discard
        /// across those), then delegates resolution + the no-op decision to
        /// <see cref="RecordingStore.TryClassifyActiveSwitchSegmentNoOp"/>.</para>
        /// </summary>
        internal bool TryEvaluateActiveSwitchSegmentNoOp(
            out string reason, out SwitchSegmentDisposition disposition)
        {
            reason = null;
            disposition = SwitchSegmentDisposition.None;

            if (restoringActiveTree)
            {
                reason = "restoring-active-tree";
                return false;
            }

            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario)
                || scenario.ActiveSwitchSegmentSession == null)
            {
                reason = "no-session";
                return false;
            }
            if (scenario.ActiveReFlySessionMarker != null)
            {
                reason = "refly-active";
                return false;
            }
            if (scenario.ActiveMergeJournal != null)
            {
                reason = "merge-journal-active";
                return false;
            }

            // B1: populate the segment recording from the live recorder buffers
            // before the classifier reads it (mirrors IsActiveTreeIdleOnPad). The
            // flush only populates activeTree.ActiveRecordingId; the classifier
            // verifies the segment IS that recording.
            //
            // Both callers run inside a Harmony prefix (the HighLogic.LoadScene
            // scene-exit prefix and the map OnSelect prefix). An exception here
            // would propagate out and abort the scene transition / the menu
            // click, so fail closed: log and return keep (no auto-discard).
            try
            {
                FlushRecorderIntoActiveTreeForSerialization();
                // A committed-restore clone is discarded whole on a no-op, so the
                // classifier also tail-checks every other member. Close the on-rails
                // background members' open segments first so that check sees them;
                // a LOADED background member accrues per-frame data the checkpoint
                // does not flush, so keep (the no-session resume's guard).
                bool committedClone = activeTree != null
                    && CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptTree(activeTree.Id);
                if (committedClone && backgroundRecorder != null)
                {
                    if (backgroundRecorder.HasLoadedBackgroundMembers)
                    {
                        reason = "loaded-background-member";
                        ParsekLog.Verbose("SwitchSegment",
                            $"TryEvaluateActiveSwitchSegmentNoOp: treeId={activeTree.Id} " +
                            "keep reason=loaded-background-member (committed-restore clone)");
                        return false;
                    }
                    backgroundRecorder.CheckpointAllVessels(Planetarium.GetUniversalTime());
                }
                return RecordingStore.TryClassifyActiveSwitchSegmentNoOp(out reason, out disposition);
            }
            catch (System.Exception ex)
            {
                reason = "exception:" + ex.GetType().Name;
                disposition = SwitchSegmentDisposition.None;
                ParsekLog.Warn("SwitchSegment",
                    $"TryEvaluateActiveSwitchSegmentNoOp threw {ex.GetType().Name}: {ex.Message} " +
                    "- treating as keep (no auto-discard)");
                return false;
            }
        }

        /// <summary>
        /// Tears down a no-op STANDALONE resumed switch segment (the whole live
        /// active tree IS the no-op segment — a fresh tree with no committed
        /// history). Reuses the proven <see cref="AutoDiscardActiveTreeCore"/>
        /// body — identical to the idle-on-pad whole-tree teardown — which also
        /// clears any armed <see cref="SwitchSegmentSession"/>. CommittedRestoreClone
        /// is handled separately by
        /// <see cref="DiscardActiveSwitchSegmentAttemptRevertingLiveClone"/>
        /// (revert to the committed original); BgMemberOrMixed defers.
        /// </summary>
        internal void AutoDiscardNoOpStandaloneSwitchSegment(string reason)
        {
            AutoDiscardActiveTreeCore(
                reason: reason,
                screenMessage: "Recording discarded - vessel unchanged after switch",
                ledgerRecalcReason: "noop-switch-segment-discard",
                reapSidecars: true);
        }

        /// <summary>
        /// Scoped-discards the active switch segment and, when the segment lives
        /// in a still-live in-flight COMMITTED-RESTORE CLONE, tears the clone down
        /// so it is never serialized as the active tree — reverting cleanly to the
        /// committed original (which stays in <c>committedTrees</c> the whole
        /// in-flight session, copy-on-write). This is the fix for the data-loss
        /// path where an in-flight committed-clone discard left the clone live,
        /// it stranded into the Limbo pending slot on the next switch, and a later
        /// whole-tree discard wiped the committed mission.
        ///
        /// <para>Used by every PRE-STASH discard path (the in-flight pre-switch
        /// Discard handler and scene-exit Hook 1's committed-clone branch). The
        /// scene-exit MANUAL merge-dialog Discard is POST-stash and stays on its
        /// own (already-safe) path.</para>
        ///
        /// <para>The clone tree id is captured BEFORE the scoped discard, because
        /// <see cref="RecordingStore.TryDiscardActiveSwitchSegmentAttempt"/>
        /// clears the restore attempt; the teardown then fires only when the
        /// captured clone is still the live <c>activeTree</c> afterward, so an
        /// unrelated active tree is never nulled. Standalone segments have a null
        /// clone id and keep their existing (untorn-down) behavior here — the
        /// caller routes Standalone to
        /// <see cref="AutoDiscardNoOpStandaloneSwitchSegment"/> when a full
        /// teardown is wanted.</para>
        /// </summary>
        internal RecordingStore.SwitchSegmentDiscardDisposition
            DiscardActiveSwitchSegmentAttemptRevertingLiveClone(
                string reason, string screenMessage, string ledgerRecalcReason,
                out string discardReason)
        {
            string cloneTreeId =
                activeTree != null
                && CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptTree(activeTree.Id)
                    ? activeTree.Id
                    : null;

            var disposition = RecordingStore.TryDiscardActiveSwitchSegmentAttempt(
                out discardReason);

            // In-flight (pre-stash) the committed clone is the live activeTree, not
            // the pending slot, so the scoped discard leaves it intact
            // (droppedPendingClone=False). Tear it down so it is never serialized
            // isActive; the committed original survives in committedTrees.
            if (cloneTreeId != null
                && activeTree != null
                && string.Equals(activeTree.Id, cloneTreeId, StringComparison.Ordinal))
            {
                ParsekLog.Info("SwitchSegment",
                    $"DiscardActiveSwitchSegmentAttemptRevertingLiveClone: tearing down live " +
                    $"committed-restore clone treeId={cloneTreeId} to revert to the committed " +
                    $"original (reason={reason})");
                AutoDiscardActiveTreeWithMessage(reason, screenMessage, ledgerRecalcReason);
            }

            return disposition;
        }

        /// <summary>
        /// Evaluates the NO-SESSION committed-restore resume path: when you Fly /
        /// Switch-To a vessel that belongs to a committed Parsek mission via a
        /// scene reload, OnLoad resumes the committed tree in-place
        /// (<c>ResumeCommittedActiveRecording</c>) as a copy-on-write clone
        /// WITHOUT arming a <see cref="SwitchSegmentSession"/> (it sets
        /// <see cref="liveResumeSessionStartUT"/> instead). The session-based
        /// no-op auto-discard never sees this, so a do-nothing revisit of a
        /// tracked mission used to fall through to the merge dialog. This returns
        /// true when the resume TAIL (since the live-resume anchor) changed
        /// nothing meaningful, so the caller can revert the clone to the committed
        /// original via <see cref="AutoDiscardNoOpNoSessionCommittedResume"/>.
        ///
        /// <para>Guards out: an armed <see cref="SwitchSegmentSession"/> (the
        /// session path owns that), restore-coroutine, Re-Fly, and merge-journal.
        /// Requires the active tree to be the armed committed-restore clone, a
        /// finite resume anchor, and a resolvable active recording. MUTATES like
        /// <see cref="IsActiveTreeIdleOnPad"/>: flushes the recorder so the tail
        /// is readable. Fails closed (keep) on exception.</para>
        /// </summary>
        internal bool TryEvaluateNoSessionCommittedResumeNoOp(out string reason)
        {
            reason = null;

            if (restoringActiveTree)
            {
                reason = "restoring-active-tree";
                return false;
            }

            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario))
            {
                reason = "no-scenario";
                return false;
            }
            if (scenario.ActiveSwitchSegmentSession != null)
            {
                reason = "has-session"; // the session path owns this
                return false;
            }
            if (scenario.ActiveReFlySessionMarker != null)
            {
                reason = "refly-active";
                return false;
            }
            if (scenario.ActiveMergeJournal != null)
            {
                reason = "merge-journal-active";
                return false;
            }

            if (activeTree == null)
            {
                reason = "no-active-tree";
                return false;
            }
            if (!CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptTree(activeTree.Id))
            {
                reason = "not-committed-restore-clone";
                return false;
            }
            if (double.IsNaN(liveResumeSessionStartUT))
            {
                reason = "no-resume-anchor";
                return false;
            }
            if (activeTree.Recordings == null)
            {
                reason = "no-recordings";
                return false;
            }
            // The resume must be the live active recording (the in-place resume),
            // else we cannot reason about what the resume did.
            if (string.IsNullOrEmpty(activeTree.ActiveRecordingId)
                || !activeTree.Recordings.ContainsKey(activeTree.ActiveRecordingId))
            {
                reason = "no-active-recording";
                return false;
            }

            try
            {
                // Flush the foreground recorder, then checkpoint the background
                // recorder so any open on-rails orbit segments are closed into the
                // tree recordings — so the tail check below sees a background
                // member's real trajectory change (e.g. an SOI crossing flushes a
                // body-changed segment) instead of missing it.
                FlushRecorderIntoActiveTreeForSerialization();
                if (backgroundRecorder != null)
                {
                    // LOADED (physics-bubble) background members accrue per-frame
                    // data that CheckpointAllVessels does NOT flush, so they can't
                    // be cleanly tail-checked — defer (the BgMemberOrMixed analog,
                    // data-loss guard). On-rails members ARE flushed and evaluated.
                    if (backgroundRecorder.HasLoadedBackgroundMembers)
                    {
                        reason = "loaded-background-member";
                        return false;
                    }
                    backgroundRecorder.CheckpointAllVessels(Planetarium.GetUniversalTime());
                }

                // Tree-wide tail check: the resume is a no-op only if NO recording
                // in the clone (the foreground resumed recording AND every
                // on-rails background member) changed anything since the resume
                // anchor. Committed history (all before the anchor) passes
                // trivially. If any member's tail is meaningful, defer so a revert
                // cannot drop it.
                foreach (var kvp in activeTree.Recordings)
                {
                    Recording rec = kvp.Value;
                    if (rec == null) continue;
                    if (!SwitchSegmentNoOpClassifier.IsNoOpResumeTail(
                            rec, liveResumeSessionStartUT, out string keepReason))
                    {
                        reason = "member:" + (kvp.Key ?? "<null>") + ":" + (keepReason ?? "<none>");
                        ParsekLog.Verbose("SwitchSegment",
                            $"TryEvaluateNoSessionCommittedResumeNoOp: treeId={activeTree.Id} " +
                            $"keep reason={reason}");
                        return false;
                    }
                }

                reason = "no-op";
                ParsekLog.Verbose("SwitchSegment",
                    $"TryEvaluateNoSessionCommittedResumeNoOp: treeId={activeTree.Id} " +
                    $"sinceUT={liveResumeSessionStartUT.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"recordings={activeTree.Recordings.Count} noOp=True");
                return true;
            }
            catch (System.Exception ex)
            {
                reason = "exception:" + ex.GetType().Name;
                ParsekLog.Warn("SwitchSegment",
                    $"TryEvaluateNoSessionCommittedResumeNoOp threw {ex.GetType().Name}: " +
                    $"{ex.Message} - treating as keep");
                return false;
            }
        }

        /// <summary>
        /// Reverts a no-op no-session committed-restore resume: discards the
        /// committed-tree restore attempt (purging its same-id post-cutoff event
        /// tails, then clearing it, so the clone's suppressed events cannot become
        /// durable after the clear) and tears the live clone down via
        /// <see cref="AutoDiscardActiveTreeCore"/> (in-memory only - deletes no
        /// files, never touches <c>committedTrees</c>), so the committed original
        /// survives and the clone is never serialized active. The session path's
        /// restore-attempt discard is done by the scoped discard; here there is no
        /// session/segment to scoped-discard, so it is explicit. After this call
        /// the drop-site clear in AutoDiscardActiveTreeCore no-ops (the attempt is
        /// already gone).
        /// </summary>
        internal void AutoDiscardNoOpNoSessionCommittedResume(string reason)
        {
            CommittedTreeRestoreAttempt.DiscardCommittedTreeRestoreAttempt(
                "noop-no-session-committed-resume-revert",
                preserveIrreversibleTails: true);
            AutoDiscardActiveTreeWithMessage(
                reason: reason,
                screenMessage: "Recording discarded - vessel unchanged after switch",
                ledgerRecalcReason: "noop-no-session-committed-resume-discard");
        }

        /// <summary>
        /// Shared teardown body for <see cref="AutoDiscardIdleActiveTree"/>
        /// and <see cref="AutoDiscardActiveTreeWithMessage"/>. The screen
        /// message and ledger-recalc reason are parameterized so the
        /// existing idle-on-pad entry point keeps its toast/reason and the
        /// new reason-aware entry point can supply context-appropriate
        /// strings (e.g. the Case B Switch-To Discard handler).
        ///
        /// <para><b>Sidecar reap.</b> This teardown is in-memory-only by
        /// design, but <c>StartRecording</c>'s quickload-resume OnSave may
        /// already have written the tree's <c>.prec</c> / <c>.pann</c> /
        /// <c>_ghost.craft</c> sidecars to disk. A discard that empties the
        /// store used to strand them permanently (the sweeper's zero-known
        /// guard correctly refuses to touch the directory), so the tree's
        /// recordings and the load-shape gate are sampled BEFORE the teardown
        /// and <see cref="DiscardSidecarReap"/> reaps them AFTER, by explicit
        /// id, never as a directory sweep. <paramref name="reapSidecars"/>
        /// is false only for callers that own their own reap + summary line
        /// (the M-A2 <c>DiscardTree</c> test-command verb, whose line the S0.5
        /// harness spec pins verbatim).</para>
        /// </summary>
        private void AutoDiscardActiveTreeCore(
            string reason,
            string screenMessage,
            string ledgerRecalcReason,
            bool reapSidecars)
        {
            ParsekLog.Info("Flight",
                $"AutoDiscardActiveTreeCore: discarding live tree reason='{reason}' " +
                $"ledgerRecalcReason='{ledgerRecalcReason}'");
            if (!string.IsNullOrEmpty(screenMessage))
                ScreenMessage(screenMessage, 3f);

            // Sidecar-reap capture: BOTH inputs must be read before the teardown
            // nulls activeTree (the recordings) and while the load shape is still
            // observable (the skip gate). The reap itself runs after the teardown,
            // when the store's known-id set no longer owns the discarded ids.
            //
            // Wrapped for the same reason TryEvaluateActiveSwitchSegmentNoOp is: two of
            // this method's callers run inside a Harmony prefix (the HighLogic.LoadScene
            // scene-exit prefix and the map OnSelect prefix), so a throw here would
            // propagate out and abort the scene transition / the menu click. Fails
            // CLOSED - a capture we could not take reaps nothing, which is the pre-fix
            // status quo, never an unguarded delete.
            List<Recording> reapCapturedRecordings = null;
            string reapSkipReason = null;
            string reapTreeId = null;
            string reapTreeName = null;
            if (reapSidecars)
            {
                try
                {
                    reapCapturedRecordings = DiscardSidecarReap.CaptureTreeRecordings(activeTree);
                    reapTreeId = activeTree?.Id;
                    reapTreeName = activeTree?.TreeName;
                    var reapScenario = ParsekScenario.Instance;
                    reapSkipReason = DiscardSidecarReap.SkipReason(
                        reFlyMarkerActive: !object.ReferenceEquals(null, reapScenario)
                            && reapScenario.ActiveReFlySessionMarker != null,
                        mergeJournalActive: !object.ReferenceEquals(null, reapScenario)
                            && reapScenario.ActiveMergeJournal != null,
                        restoringActiveTree: restoringActiveTree);
                }
                catch (System.Exception ex)
                {
                    reapCapturedRecordings = null;
                    reapSkipReason = "capture-threw:" + ex.GetType().Name;
                    ParsekLog.Warn("Flight",
                        $"AutoDiscardActiveTreeCore: sidecar-reap capture threw " +
                        $"{ex.GetType().Name}: {ex.Message} - reap skipped, discard " +
                        $"continues (orphans retained) reason='{reason}'");
                }
            }

            // Preserve irreversible live-gameplay economy before tearing the tree down.
            // This (uncommitted) tree's contract completions / milestones / science are
            // tagged-but-uncommitted, so they were never ledger actions; the teardown
            // drops them while the recalc below would re-list an externally-accepted
            // contract as active (KSP keeps it finished -> dual-listing + duplicate
            // reward). Re-home the irreversible bits to the direct ledger first. Collect
            // ids BEFORE activeTree is nulled. This core does not purge the tree's own
            // store events (orphans; the direct ledger actions are the truth and the
            // orphaned tagged events are not recalc inputs). The one purge here is the
            // drop-site clear below, which removes a committed-restore attempt's same-id
            // post-cutoff tails only.
            if (activeTree?.Recordings != null && activeTree.Recordings.Count > 0)
            {
                var rehomeIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var rid in activeTree.Recordings.Keys)
                    if (!string.IsNullOrEmpty(rid))
                        rehomeIds.Add(rid);
                if (rehomeIds.Count > 0)
                    LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(
                        rehomeIds, $"AutoDiscardActiveTreeCore: {reason}");
            }

            // Rec-3 observability (see RecordingStore.DiscardPendingTree): report any
            // supply-route physical mutation that fired inside this discarded active-tree
            // window and persists un-reversed (funds+cargo both kept). Behavior-neutral;
            // rewind/RP-backed sessions are skipped inside the reporter. Read BEFORE
            // activeTree is nulled below.
            if (activeTree?.Recordings != null && activeTree.Recordings.Count > 0)
                Parsek.Logistics.RouteDiscardObservability.ReportDiscardLeakForRecordings(
                    activeTree.Recordings.Values,
                    Parsek.Logistics.RouteEventLog.Events,
                    ParsekScenario.IsReFlySessionActiveForQuickloadDiscard(),
                    $"AutoDiscardActiveTreeCore: {reason}");

            // Mirror OnSceneChangeRequested's pre-finalize prep so any
            // transient state is cleaned up before we drop the recorder.
            ClearSceneChangeTransientState();

            // Mirror DiscardActiveTreeForSuppressedSceneExit's recorder
            // teardown: ForceStop + clear ActiveRecorder reference before
            // nulling. Without ForceStop the recorder may still hold
            // buffered data; without clearing ActiveRecorder, the physics
            // patch retains a stale reference (regression-checked by
            // ExtendedRuntimeTests.cs:919's
            // PhysicsFramePatch_ActiveRecorder_NullWhenNotRecording).
            if (recorder != null)
            {
                if (recorder.IsRecording)
                    recorder.ForceStop();
                Patches.PhysicsFramePatch.ActiveRecorder = null;
                recorder = null;
            }

            if (backgroundRecorder != null)
            {
                backgroundRecorder.DiscardWithoutPersist(reason);
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                backgroundRecorder = null;
            }

            // This teardown drops the tree WITHOUT stashing it as pending, so its armed
            // committed-tree restore attempt must go with it: while the attempt outlives
            // its clone every OnSave defers the pending-event milestone flush, and its
            // same-id post-cutoff event tails must be purged with it or they become
            // durable on the next save. The helper purges first, then clears, and is
            // id-matched: it no-ops when a caller already discarded the attempt (the
            // no-op revert, the switch-segment discard, the pre-switch dialog discard)
            // or when the tree is another attempt's clone.
            CommittedTreeRestoreAttempt.ClearCommittedTreeRestoreAttemptForDroppedTree(
                activeTree?.Id, "auto-discard dropped the restore clone",
                preserveIrreversibleTails: true);
            activeTree = null;

            // A discarded active tree must not leave an armed switch-segment
            // session pointing at it. Without this, the idle-on-pad scene-exit
            // fast path (SceneExitInterceptor) tears the tree down but the
            // session (already persisted by the pre-exit OnSave) survives
            // into the save and resurfaces as a deferred merge dialog on the
            // next load. ClearSwitchSegmentSession no-ops when none is armed
            // (e.g. the Case B no-session Switch-To discard).
            ParsekScenario.Instance?.ClearSwitchSegmentSession(
                $"active-tree-auto-discard: {reason}");

            // Explicit-id sidecar reap for the ids this teardown just dropped.
            // Runs AFTER the in-memory removal so RecordingStore's known-id set is
            // the authority on what survives; failures inside are counted and
            // logged, never thrown, so a locked file cannot abort the discard.
            if (reapSidecars)
            {
                try
                {
                    DiscardSidecarReap.ReapDiscardedTreeSidecars(
                        reapCapturedRecordings, reapSkipReason, reason,
                        reapTreeId, reapTreeName);
                }
                catch (System.Exception ex)
                {
                    ParsekLog.Warn("Flight",
                        $"AutoDiscardActiveTreeCore: sidecar reap threw " +
                        $"{ex.GetType().Name}: {ex.Message} - discard continues " +
                        $"(orphans retained) reason='{reason}'");
                }
            }

            // No pending tree was stashed (we discard pre-finalize).
            // Roll back any in-flight ledger entries from the aborted
            // recording.
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions(
                ParsekScenario.GetCurrentTimelineUTForLedgerRecalc(),
                ledgerRecalcReason);
        }

        /// <summary>
        /// Pure: true when a scene exit out of flight to a non-flight scene belongs to a save-file
        /// load stock announced (<see cref="SaveFileLoadIntent"/>) that is not a revert, so the live
        /// tree is stashed (and later dropped by the cold load's reset) rather than committed.
        /// A FLIGHT destination already stashes; a revert keeps today's scene-exit handling.
        /// </summary>
        internal static bool ShouldStashLiveTreeForSaveFileLoad(
            GameScenes destination, bool saveFileLoadIntentArmed, bool revertPending)
        {
            return destination != GameScenes.FLIGHT && saveFileLoadIntentArmed && !revertPending;
        }

        private void FinalizeTreeOnSceneChangeCore(
            GameScenes scene,
            double commitUT,
            bool logRecorderState)
        {
            // #267: skip if restore coroutine is mid-yield — it owns activeTree/recorder
            if (restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    $"FinalizeTreeOnSceneChange: skipped — restore coroutine in progress (dest={scene})");
                return;
            }

            if (PendingTreeSlot.TryConsumeNextTreeSceneExitCommitSuppression(scene, out string suppressReason))
            {
                DiscardActiveTreeForSuppressedSceneExit(
                    scene,
                    commitUT,
                    suppressReason,
                    logRecorderState);
                return;
            }

            if (logRecorderState)
                RecorderStateLog.RecState("FinalizeTreeOnSceneChange:entry", CaptureRecorderState());

            // Checkpoint all background vessels before finalization.
            // This captures clean orbital reference points at the scene-change boundary.
            if (backgroundRecorder != null)
            {
                backgroundRecorder.CheckpointAllVessels(commitUT);
                ParsekLog.Info("Checkpoint",
                    "Scene change: checkpointed all background vessels before finalization");
            }

            if (scene == GameScenes.FLIGHT)
            {
                // FLIGHT→FLIGHT: the player quickloaded, reverted, or vessel-switched
                // into a scene reload. We can't fully tell which until OnLoad runs,
                // but we CAN check the live vesselSwitchPending flag (set from KSP's
                // onVesselSwitching event once the scene is flight-ready). CAVEAT: a far
                // Switch-To does NOT set it - stock FlightGlobals.setActiveVessel fires
                // onVesselSwitchingToUnloaded and returns into StartAndFocusVessel before
                // onVesselSwitching - so a real far switch lands on the Limbo stash below
                // (todo VESSEL-SWITCH-266-UNREACHABLE-FOR-FAR-SWITCH).
                //
                // Bug #266: when this is a vessel switch, pre-transition the tree at
                // stash time — flush the recorder, move the active recording's PID
                // into BackgroundMap, null ActiveRecordingId — and stash as
                // LimboVesselSwitch. The restore coroutine just reinstalls and
                // optionally promotes the new vessel from BackgroundMap. This
                // preserves the in-progress mission across the scene reload, exactly
                // matching the in-session OnVesselSwitchComplete behavior.
                //
                // Otherwise (quickload, revert, or any case where we can't safely
                // pre-transition — see CanPreTransitionForVesselSwitch), fall back
                // to the legacy Limbo stash. OnLoad's revert detection dispatch will
                // then either restore-and-resume (quickload) or finalize-and-commit
                // (revert). See docs/dev/plans/quickload-resume-recording.md (Bug C).
                bool isVesselSwitch = ParsekScenario.IsVesselSwitchPendingFresh();
                if (isVesselSwitch && CanPreTransitionForVesselSwitch())
                {
                    StashActiveTreeForVesselSwitch(commitUT);
                }
                else
                {
                    if (isVesselSwitch)
                    {
                        ParsekLog.Info("Flight",
                            "FinalizeTreeOnSceneChange: vessel switch detected but " +
                            "pre-transition is unsafe (pendingTreeDockMerge / " +
                            "pendingSplit) — falling back to legacy Limbo stash; " +
                            "OnLoad safety net will finalize");
                    }
                    StashActiveTreeAsPendingLimbo(commitUT);
                }
            }
            else if (ShouldStashLiveTreeForSaveFileLoad(
                scene,
                SaveFileLoadIntent.IsArmed || NoSaveSceneExit.IsArmedFor(scene),
                RevertDetector.PendingKind != RevertKind.None))
            {
                // Also stock's quit without saving (NoSaveSceneExit, owner ruling 2026-11-01):
                // the destination reloads the older persistent.sfs, so the flight since that
                // save is dropped with the world, as for a save-file load.
                // A save-file load out of flight into a non-flight save (F9 or the load dialog
                // into a Space Center quicksave): the next OnLoad is Cold and rebuilds Parsek from
                // that save (design-load-restores-the-save.md 4.1), so the live flight is the
                // abandoned future, not a finished one. Stash it as Limbo, as an F9 into a flight
                // save does; the cold load's session reset drops it from memory only. Committing
                // it here would run a commit for a flight the load is about to forget.
                string stashCause = NoSaveSceneExit.IsArmedFor(scene)
                    ? "quit without saving"
                    : "save-file load";
                ParsekLog.Info("Flight",
                    $"FinalizeTreeOnSceneChange: {stashCause} out of flight (dest={scene}) - stashing the "
                    + $"live tree '{activeTree.TreeName}' as pending Limbo instead of committing it; the "
                    + "cold load replaces it from the save");
                if (NoSaveSceneExit.IsArmedFor(scene))
                    NoSaveSceneExit.NoteStashedTree(activeTree.Id, NoSaveSceneExit.CurrentSaveFolder());
                StashActiveTreeAsPendingLimbo(commitUT);
            }
            else if (!SceneExitInterceptor.IsAutoMergeForSceneExitLive(
                scene, "FinalizeTreeOnSceneChange"))
            {
                // autoMerge OFF: safe finalization but preserve snapshots for dialog in KSC/TS.
                // A rewind's own Space Center load never takes this branch: it commits like
                // auto-merge on (SceneExitInterceptor.ResolveSceneExitAutoMerge).
                // Uses isSceneExit: true; FinalizeIndividualRecording's #289 re-snapshot path
                // updates VesselSnapshot for stable-terminal recordings (Landed/Splashed/Orbiting)
                // so the dialog and spawn-at-end safety check see the correct sit field.
                FinalizeTreeRecordings(activeTree, commitUT, isSceneExit: true);

                // In-flight Recover (operator ruling 2026-10-01): the merge dialog commits
                // before stock recovers the vessel at the Space Center, so stamp the
                // recovered launch's tip Recovered here, while the tree is not committed.
                InFlightRecoveryRequest.ApplyAtSceneExit(
                    activeTree, scene, "scene-exit autoMerge-off");

                // #289: KSP's auto-save fired BEFORE this code path runs (it ran inside
                // OnSceneChangeRequested, before our scene-exit cleanup). Any sidecar files
                // dirtied by FinalizeTreeRecordings (e.g. fresh snapshots from the #289
                // re-snapshot) won't reach disk via the normal OnSave→FlushDirtyFiles path
                // before the next OnLoad runs TryRestoreActiveTreeNode, which hydrates the
                // restored tree from those sidecars. Force-write them now so the post-finalize
                // state survives the round-trip.
                int forcedWrites = 0;
                foreach (var rec in activeTree.Recordings.Values)
                {
                    if (rec.FilesDirty)
                    {
                        if (RecordingSidecarStore.SaveRecordingFiles(rec, incrementEpoch: false))
                            forcedWrites++;
                    }
                }
                if (forcedWrites > 0)
                {
                    ParsekLog.Info("Flight",
                        $"CommitTreeSceneExit (autoMerge off): force-wrote {forcedWrites} dirty sidecar(s) " +
                        $"after finalize so post-finalize state survives the next OnLoad [#289]");
                }

                // Default state = Finalized. TryRestoreActiveTreeNode relies on this to
                // skip replacing the in-memory tree with the stale .sfs version (which was
                // written before FinalizeTreeRecordings and lacks MaxDistanceFromLaunch,
                // terminal states, and re-snapshotted vessels). See bug #290d.
                PendingTreeSlot.StashPendingTree(activeTree);
                ParsekLog.Info("Flight",
                    $"CommitTreeSceneExit (autoMerge off): stashed tree '{activeTree.TreeName}' with snapshots preserved");
            }
            else
            {
                // Scene exit: null snapshots (ghost-only, no spawning outside Flight)
                CommitTreeSceneExit(commitUT, scene);
            }

            // A recovery request this exit did not consume (FLIGHT destination, or no
            // tree finalized above) must not outlive the transition.
            InFlightRecoveryRequest.Clear("scene-exit finalize dest=" + scene);

            // Clean up tree state
            recorder = null;
            if (backgroundRecorder != null)
            {
                backgroundRecorder.Shutdown();
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                backgroundRecorder = null;
            }
            activeTree = null;
        }

        private void DiscardActiveTreeForSuppressedSceneExit(
            GameScenes scene,
            double commitUT,
            string suppressReason,
            bool logRecorderState)
        {
            string treeName = activeTree.TreeName;
            int recordingCount = activeTree.Recordings.Count;
            string activeRecordingId = activeTree.ActiveRecordingId ?? "<none>";
            ParsekLog.Info("Flight",
                $"FinalizeTreeOnSceneChange: suppressed tree scene-exit commit " +
                $"dest={scene} UT={commitUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"tree='{treeName}' recordings={recordingCount} active='{activeRecordingId}' " +
                $"reason='{suppressReason ?? "<unspecified>"}' - discarding in-memory tree without STASH");
            if (logRecorderState)
                RecorderStateLog.RecState("FinalizeTreeOnSceneChange:suppressed-entry", CaptureRecorderState());

            if (recorder != null)
            {
                if (recorder.IsRecording)
                    recorder.ForceStop();
                Patches.PhysicsFramePatch.ActiveRecorder = null;
                recorder = null;
            }

            // The Re-Fly restore path installs a loaded/detached active tree:
            // TryRestoreActiveTreeNode clones/reconciles committed recordings into
            // that tree, so background recorder mutations from the attempt are
            // discarded here instead of leaking into CommittedRecordings.
            if (backgroundRecorder != null)
            {
                backgroundRecorder.DiscardWithoutPersist(suppressReason);
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                backgroundRecorder = null;
            }

            // Discarded without a pending stash, so the armed committed-tree restore
            // attempt must be cleared with the tree (id-matched, reads the id before the
            // null below): a stale attempt keeps deferring the OnSave milestone flush.
            // A suppressed scene exit is the Discard Re-fly / rewind load: the loaded save
            // reverts KSP, so nothing of the attempt is preserved.
            CommittedTreeRestoreAttempt.ClearCommittedTreeRestoreAttemptForDroppedTree(
                activeTree?.Id, "suppressed scene exit dropped the restore clone",
                preserveIrreversibleTails: false);
            activeTree = null;
            if (logRecorderState)
                RecorderStateLog.RecState("FinalizeTreeOnSceneChange:suppressed-post", CaptureRecorderState());
        }

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
                    TryCaptureFlagPickUp(v, FlagPickUp.PendingPickerPid);
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

        /// <summary>
        /// Coroutine that checks whether all tree leaves are terminal after the active vessel
        /// is destroyed. If so, finalizes the tree and shows the tree merge dialog.
        /// Waits for any deferred split/coalescer work still in progress so false-alarm
        /// split data or real breakup children are attached to the tree before finalize.
        /// </summary>
        IEnumerator ShowPostDestructionTreeMergeDialog()
        {
            RecorderStateLog.RecState("ShowPostDestructionTreeMergeDialog:entry", CaptureRecorderState());

            // Wait one frame for destruction events to settle
            yield return null;

            // Guard: tree cleaned up during wait (scene change, etc.)
            if (activeTree == null)
            {
                if (sceneChangeInProgress)
                {
                    ParsekLog.Info("Flight",
                        "ShowPostDestructionTreeMergeDialog: activeTree is null during scene change — aborting in-flight merge");
                }
                else
                {
                    ParsekLog.Verbose("Flight",
                        "ShowPostDestructionTreeMergeDialog: activeTree is null — aborting");
                }
                treeDestructionDialogPending = false;
                yield break;
            }

            // Guard: someone else handled it
            if (!treeDestructionDialogPending)
            {
                ParsekLog.Verbose("Flight", "ShowPostDestructionTreeMergeDialog: flag already cleared — aborting");
                yield break;
            }

            // Guard: active vessel still alive
            if (recorder != null && recorder.IsRecording && !recorder.VesselDestroyedDuringRecording)
            {
                ParsekLog.Verbose("Flight", "ShowPostDestructionTreeMergeDialog: active vessel still alive — aborting");
                treeDestructionDialogPending = false;
                yield break;
            }

            bool ignorePendingCrashWait = false;
            float pendingCrashWaitStart = 0f;
            while (true)
            {
                bool activeDestroyed = recorder == null || !recorder.IsRecording
                    || recorder.VesselDestroyedDuringRecording;
                bool allLeavesTerminal = RecordingTree.AreAllLeavesTerminal(
                    activeTree,
                    activeDestroyed);
                bool onlyDebrisBlockersRemain = RecordingTree.AreAllActiveCrashBlockersDebris(
                    activeTree);
                bool pendingCrashResolution = !ignorePendingCrashWait
                    && HasPendingPostDestructionCrashResolution(
                        activeDestroyed,
                        pendingSplitInProgress,
                        crashCoalescer != null && crashCoalescer.HasPendingBreakup);

                switch (ClassifyPostDestructionMergeResolution(
                    activeDestroyed,
                    allLeavesTerminal,
                    onlyDebrisBlockersRemain,
                    pendingCrashResolution))
                {
                    case PostDestructionMergeResolution.WaitForPendingCrashResolution:
                        if (pendingCrashWaitStart <= 0f)
                        {
                            pendingCrashWaitStart = Time.unscaledTime;
                            ParsekLog.Info("Flight",
                                "ShowPostDestructionTreeMergeDialog: waiting for pending split/coalescer resolution before finalizing tree");
                        }
                        else if (Time.unscaledTime - pendingCrashWaitStart > 5f)
                        {
                            ParsekLog.Warn("Flight",
                                "ShowPostDestructionTreeMergeDialog: pending split/coalescer wait timed out (5s real-time) — continuing with current tree state");
                            ignorePendingCrashWait = true;
                            continue;
                        }

                        yield return null;

                        // Re-run the guards after yielding while the deferred split resolves.
                        if (activeTree == null)
                        {
                            if (sceneChangeInProgress)
                            {
                                ParsekLog.Info("Flight",
                                    "ShowPostDestructionTreeMergeDialog: activeTree is null during scene change — aborting in-flight merge");
                            }
                            else
                            {
                                ParsekLog.Verbose("Flight",
                                    "ShowPostDestructionTreeMergeDialog: activeTree is null — aborting");
                            }
                            treeDestructionDialogPending = false;
                            yield break;
                        }

                        if (!treeDestructionDialogPending)
                        {
                            ParsekLog.Verbose("Flight",
                                "ShowPostDestructionTreeMergeDialog: flag cleared while waiting for pending crash resolution — aborting");
                            yield break;
                        }

                        if (recorder != null && recorder.IsRecording && !recorder.VesselDestroyedDuringRecording)
                        {
                            ParsekLog.Verbose("Flight",
                                "ShowPostDestructionTreeMergeDialog: active vessel still alive — aborting");
                            treeDestructionDialogPending = false;
                            yield break;
                        }

                        continue;

                    case PostDestructionMergeResolution.AbortAndKeepRecording:
                        ParsekLog.Info("Flight",
                            "ShowPostDestructionTreeMergeDialog: not all leaves terminal — other vessels still alive");
                        treeDestructionDialogPending = false;
                        yield break;

                    case PostDestructionMergeResolution.FinalizeNow:
                        if (allLeavesTerminal)
                        {
                            ParsekLog.Info("Flight",
                                "ShowPostDestructionTreeMergeDialog: all leaves terminal — finalizing tree");
                        }
                        else
                        {
                            ParsekLog.Info("Flight",
                                "ShowPostDestructionTreeMergeDialog: only debris leaves still block same-scene merge — finalizing tree in flight");
                        }
                        break;
                }

                break;
            }

            double commitUT = Planetarium.GetUniversalTime();

            // FinalizeTreeRecordings handles ForceStop + FlushRecorderToTreeRecording
            FinalizeTreeRecordings(activeTree, commitUT, isSceneExit: false);
            ParsekLog.Info("Flight",
                $"ShowPostDestructionTreeMergeDialog: finalized tree '{activeTree.TreeName}'");

            PendingTreeSlot.StashPendingTree(activeTree);
            ParsekLog.Info("Flight",
                $"ShowPostDestructionTreeMergeDialog: stashed pending tree '{activeTree.TreeName}'");

            string autoDiscardReason = null;
            // Auto-discard pad failures: if every recording in the tree is a pad failure
            // (< 10s duration AND < 30m from launch), discard the whole tree.
            // Also discard if every recording is idle-on-pad (< 30m, any duration).
            if (IsTreePadFailure(activeTree) || IsTreeIdleOnPad(activeTree))
            {
                autoDiscardReason = IsTreePadFailure(activeTree) ? "pad failure" : "idle on pad";
                ParsekLog.Info("Flight",
                    $"ShowPostDestructionTreeMergeDialog: tree {autoDiscardReason} — auto-discarding");
                ScreenMessage($"Recording discarded - {autoDiscardReason}", 3f);
            }

            // Clean up flight state
            recorder = null;
            if (backgroundRecorder != null)
            {
                backgroundRecorder.Shutdown();
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                backgroundRecorder = null;
            }
            activeTree = null;
            treeDestructionDialogPending = false;

            if (!string.IsNullOrEmpty(autoDiscardReason))
            {
                ParsekScenario.DiscardPendingTreeAndRecalculate(
                    $"post-destruction {autoDiscardReason} auto-discard");
                yield break;
            }

            // #434: do NOT show the in-flight merge dialog or commit via auto-merge here.
            // The tree is stashed; the stock KSP crash report shows first (with Revert /
            // Space Center). If the player reverts, ParsekScenario.OnLoad's isRevert branch
            // auto-discards the pending tree via RecordingStore.DiscardPendingTree (see #431
            // for the purge semantics). Otherwise the scene-exit path surfaces the merge
            // dialog (autoMerge off) or auto-commits (autoMerge on) in the destination scene
            // via ParsekScenario.ShowDeferredMergeDialog / the OnLoad auto-commit branch.
            ParsekLog.Info("Flight",
                "ShowPostDestructionTreeMergeDialog: pending tree stashed — deferring to post-report scene transition");
        }

        /// <summary>
        /// Primary detection mechanism for vessel switches in tree mode.
        /// Handles the old-vessel transition to background and, when Parsek is idle,
        /// arms the post-switch watcher for the new active vessel instead of starting
        /// immediately on focus change alone. Fires regardless of on-rails state,
        /// unlike OnPhysicsFrame.
        /// </summary>
        void OnVesselSwitchComplete(Vessel newVessel)
        {
            bool newVesselIsGhost =
                newVessel != null && GhostMapPresence.IsGhostMapVessel(newVessel.persistentId);
            if (newVesselIsGhost) return;

            // #267: skip if restore coroutine is mid-yield — it owns activeTree/recorder
            if (restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    $"OnVesselSwitchComplete: skipped — restore coroutine in progress " +
                    $"(vessel '{newVessel?.vesselName}')");
                return;
            }

            LogOnVesselSwitchCompleteRecState(
                "OnVesselSwitchComplete:entry",
                CaptureRecorderState(),
                currentVesselSwitchRecoveryDiagnosticContext);

            // Watch mode: re-target camera to ghost — KSP reparents pivot on vessel switch.
            // Don't early-return: tree vessel-switch logic below must still run so that
            // programmatic switches (e.g. docking absorbs a vessel) properly transition
            // the recorder state and background mapping.
            watchMode.OnVesselSwitchComplete();

            // Seed lastLandedUT when switching to a vessel already on the surface.
            // Spawned vessels (from recordings) are created directly in LANDED — no
            // onVesselSituationChange fires, so the settle timer is never initialized.
            // Without this, auto-record fails because settledTime computes as 0.
            if (newVessel != null &&
                (newVessel.situation == Vessel.Situations.LANDED ||
                 newVessel.situation == Vessel.Situations.SPLASHED))
            {
                lastLandedUT = Planetarium.GetUniversalTime();
                // Rate-limit per vessel pid: the per-frame missed-switch recovery
                // (Update → ReplayVesselSwitchCompleteForMissedSwitchRecovery)
                // re-invokes this path every frame when the recovery loop is
                // stuck, and the unrate-limited Verbose used to emit ~120 lines
                // per second per landed vessel.
                ParsekLog.VerboseRateLimited("Flight",
                    $"seeded-landed-ut-{newVessel.persistentId}",
                    $"OnVesselSwitchComplete: seeded lastLandedUT={lastLandedUT:F1} (vessel '{newVessel.vesselName}' already {newVessel.situation})");
            }

            if (newVessel == null)
            {
                DisarmPostSwitchAutoRecord("active vessel became null");
                return;
            }

            string freshStartParentRecordingId = activeTree != null
                ? activeTree.ActiveRecordingId
                : null;

            // Fix 1 (CRITICAL): Don't interfere with pending merge processing.
            // Dock-induced vessel switches must not arm/start before Update() runs the merge handler.
            if (pendingTreeDockMerge)
            {
                DisarmPostSwitchAutoRecord("pending tree dock merge");
                return;
            }

            // Don't arm/start during pending board merge — the board handler in Update() owns this.
            if (recorder != null && recorder.ChainToVesselPending)
            {
                DisarmPostSwitchAutoRecord("pending boarding merge");
                return;
            }

            // Boarding detection: onCrewBoardVessel fires before onVesselChange, but no
            // physics frame runs in between — so DecideOnVesselSwitch (in OnPhysicsFrame)
            // never gets a chance to set ChainToVesselPending. Detect this case here and
            // set the flag so HandleTreeBoardMerge runs in Update() (#248).
            if (recorder != null && recorder.IsRecording
                && pendingBoardingTargetPid != 0
                && recorder.RecordingStartedAsEva
                && newVessel.persistentId == pendingBoardingTargetPid)
            {
                recorder.ChainToVesselPending = true;
                ParsekLog.Info("Flight",
                    $"OnVesselSwitchComplete: detected boarding from EVA " +
                    $"(evaPid={recorder.RecordingVesselId}, targetPid={pendingBoardingTargetPid}) " +
                    $"— set ChainToVesselPending");
                return;
            }

            // If the current recorder is still active (OnPhysicsFrame didn't catch the switch,
            // e.g. because isOnRails was true), transition it to background now
            if (activeTree != null
                && recorder != null && recorder.IsRecording && !recorder.IsBackgrounded
                && recorder.RecordingVesselId != newVessel.persistentId)
            {
                EmitPendingSplitBeforeFocusLeaves("OnVesselSwitchComplete(active)");
                recorder.TransitionToBackground();
                FlushRecorderToTreeRecording(recorder, activeTree);
                CopyRewindSaveToRoot(activeTree, recorder,
                    logTag: "OnVesselSwitch(active)");

                // Add old vessel to BackgroundMap
                string oldRecId = activeTree.ActiveRecordingId;
                Recording oldRec = null;
                if (oldRecId != null && activeTree.Recordings.TryGetValue(oldRecId, out oldRec)
                    && oldRec.VesselPersistentId != 0)
                {
                    activeTree.BackgroundMap[oldRec.VesselPersistentId] = oldRecId;
                }
                activeTree.ActiveRecordingId = null;
                recorder.IsRecording = false;
                uint bgPid1 = recorder.RecordingVesselId;
                RecordingFinalizationCache bgCache1 = oldRec != null
                    ? recorder.GetFinalizationCacheForRecording(oldRec)
                    : recorder.FinalizationCache;
                recorder = null;
                backgroundRecorder?.OnVesselBackgrounded(bgPid1, inheritedFinalizationCache: bgCache1);
                Log($"Tree: onVesselSwitchComplete transitioned old recorder to background");
            }
            // OnPhysicsFrame already backgrounded the recorder (TransitionToBackgroundPending),
            // but Update() hasn't flushed it yet. Flush now so the post-switch watcher can
            // evaluate against committed recorder data and an up-to-date BackgroundMap.
            else if (activeTree != null
                && recorder != null && recorder.IsBackgrounded && recorder.TransitionToBackgroundPending)
            {
                EmitPendingSplitBeforeFocusLeaves("OnVesselSwitchComplete(bg)");
                FlushRecorderToTreeRecording(recorder, activeTree);
                CopyRewindSaveToRoot(activeTree, recorder,
                    logTag: "OnVesselSwitch(bg)");

                string oldRecId = activeTree.ActiveRecordingId;
                Recording oldRec = null;
                uint bgPid2 = 0;
                if (oldRecId != null && activeTree.Recordings.TryGetValue(oldRecId, out oldRec)
                    && oldRec.VesselPersistentId != 0)
                {
                    activeTree.BackgroundMap[oldRec.VesselPersistentId] = oldRecId;
                    bgPid2 = oldRec.VesselPersistentId;
                }
                activeTree.ActiveRecordingId = null;
                RecordingFinalizationCache bgCache2 = oldRec != null
                    ? recorder.GetFinalizationCacheForRecording(oldRec)
                    : recorder.FinalizationCache;
                recorder = null;
                if (bgPid2 != 0) backgroundRecorder?.OnVesselBackgrounded(bgPid2, inheritedFinalizationCache: bgCache2);
                Log($"Tree: onVesselSwitchComplete flushed backgrounded recorder (recId={oldRecId})");
            }

            uint newPid = newVessel.persistentId;

            // Phase C consume: validate any armed stock-action intent marker
            // BEFORE the first-modification watcher arms, so a successful
            // immediate-start (committed-clone, BG-member, or standalone
            // continuation) disarms the watcher and the player gets exactly
            // one immediate-start path per UI click. The consume helper
            // returns NoIntent for `[`/`]` cycling, EVA, dock/undock — those
            // continue through the existing arm-decision flow below.
            var consumeResult = TryConsumeStockActionIntent(newVessel);
            if (consumeResult.StartedSegment)
            {
                DisarmPostSwitchAutoRecord("consumed-by-stock-action-segment");
                LogOnVesselSwitchCompleteRecState(
                    "OnVesselSwitchComplete:post",
                    CaptureRecorderState(),
                    currentVesselSwitchRecoveryDiagnosticContext);
                return;
            }

            bool trackedInActiveTree = activeTree != null
                && activeTree.BackgroundMap.ContainsKey(newPid);
            var armDecision = EvaluatePostSwitchAutoRecordArmDecision(
                ShouldArmPostSwitchAutoRecord(
                    ParsekSettings.Current?.autoRecordOnFirstModificationAfterSwitch != false,
                    IsRecording,
                    hasNewVessel: true,
                    newVesselIsGhost),
                trackedInActiveTree,
                newVessel.isEVA);

            // Bug #585: in-place continuation Re-Fly suppression. When the live
            // marker pins the new active vessel as the in-place continuation
            // target, the restore coroutine fired by OnFlightReady will resume
            // into the marker's recording. Arming the post-switch outsider
            // watcher here would log a misleading "outsider while idle" line
            // and risk a brief race where the watcher's first physics frame
            // captures a baseline before the coroutine binds the recorder.
            // The watcher already gates on IsRecording / restoringActiveTree
            // via EvaluatePostSwitchAutoRecordSuppression, so this is mostly a
            // diagnostics-clarity fix: we explicitly skip arming and log the
            // marker reason so the post-mortem in KSP.log reads as expected.
            if (armDecision != PostSwitchAutoRecordArmDecision.None
                && IsInPlaceContinuationArrivalForMarker(newPid))
            {
                ParsekLog.Info("Flight",
                    $"Post-switch auto-record suppressed: vessel='{newVessel.vesselName ?? "<unnamed>"}' " +
                    $"pid={newPid} reason=marker-in-place-continuation " +
                    $"sess={ParsekScenario.Instance?.ActiveReFlySessionMarker?.SessionId ?? "<no-id>"}");
                RecorderStateLog.RecState("OnVesselSwitchComplete:post", CaptureRecorderState());
                return;
            }

            switch (armDecision)
            {
                case PostSwitchAutoRecordArmDecision.ArmTrackedBackgroundMember:
                    ArmPostSwitchAutoRecord(
                        newVessel,
                        "vessel switch to tracked background member while idle",
                        trackedInActiveTree: true,
                        freshStartParentRecordingId);
                    break;

                case PostSwitchAutoRecordArmDecision.ArmOutsider:
                    ArmPostSwitchAutoRecord(
                        newVessel,
                        "vessel switch to outsider while idle",
                        trackedInActiveTree: false,
                        freshStartParentRecordingId);
                    break;
            }
            LogOnVesselSwitchCompleteRecState(
                "OnVesselSwitchComplete:post",
                CaptureRecorderState(),
                currentVesselSwitchRecoveryDiagnosticContext);
        }

        private void ReplayVesselSwitchCompleteForMissedSwitchRecovery(
            Vessel activeVessel,
            MissedVesselSwitchRecoveryDiagnosticContext recoveryDiagnosticContext)
        {
            var previousContext = currentVesselSwitchRecoveryDiagnosticContext;
            currentVesselSwitchRecoveryDiagnosticContext = recoveryDiagnosticContext;
            try
            {
                OnVesselSwitchComplete(activeVessel);
            }
            finally
            {
                currentVesselSwitchRecoveryDiagnosticContext = previousContext;
            }
        }

        internal static void LogOnVesselSwitchCompleteRecState(
            string phase,
            RecorderStateSnapshot snapshot,
            MissedVesselSwitchRecoveryDiagnosticContext recoveryDiagnosticContext)
        {
            if (!recoveryDiagnosticContext.IsRecovery)
            {
                RecorderStateLog.RecState(phase, snapshot);
                return;
            }

            RecorderStateLog.RecStateRateLimited(
                phase,
                snapshot,
                recoveryDiagnosticContext.BuildRecStateRateLimitKey(),
                MissedVesselSwitchRecoveryRecStateIntervalSeconds);
        }

        /// <summary>
        /// Bug #585: returns true when the live <see cref="ReFlySessionMarker"/>
        /// indicates an in-place continuation Re-Fly AND the just-activated
        /// vessel's <c>persistentId</c> matches the marker's active recording.
        /// In that state, post-switch auto-record arming is a no-op at best and
        /// a diagnostics nuisance at worst -- the
        /// <see cref="RestoreActiveTreeFromPending"/> coroutine will bind the
        /// recorder to the marker's recording once <see cref="OnFlightReady"/>
        /// fires.
        /// </summary>
        private bool IsInPlaceContinuationArrivalForMarker(uint newPid)
        {
            return IsInPlaceContinuationArrivalForMarker(
                newPid,
                ParsekScenario.Instance?.ActiveReFlySessionMarker,
                RecordingStore.CommittedRecordings);
        }

        /// <summary>
        /// Pure-static overload for unit testing: caller injects the marker
        /// and committed-recordings list rather than reading from
        /// <see cref="ParsekScenario.Instance"/> + <see cref="RecordingStore.CommittedRecordings"/>.
        /// Use raw committed-recordings read because the marker resolution at
        /// this point is a physical-identity correlation, not a supersede-aware
        /// ERS query (RewindInvoker.cs has the same ERS-exempt pattern in its
        /// FindRecordingById helper).
        /// </summary>
        internal static bool IsInPlaceContinuationArrivalForMarker(
            uint newPid,
            ReFlySessionMarker marker,
            System.Collections.Generic.IReadOnlyList<Recording> committedRecordings)
        {
            if (newPid == 0u) return false;
            if (marker == null) return false;
            // Accept both legacy in-place (active == origin) and the
            // post-#734 fork shape (InPlaceContinuation flag, active != origin).
            // Both keep the player on the SAME physical vessel; the
            // post-switch auto-record arming we want to suppress is the
            // same in either case.
            if (!ReFlySessionMarker.IsInPlaceContinuation(marker))
                return false;
            if (committedRecordings == null) return false;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (rec == null) continue;
                if (!string.Equals(rec.RecordingId, marker.ActiveReFlyRecordingId, StringComparison.Ordinal))
                    continue;
                return rec.VesselPersistentId == newPid;
            }
            return false;
        }

        /// <summary>
        /// Appends the current recorder's accumulated data (points, orbit segments, part events)
        /// to the tree's Recording object. Sorts part events by UT. Sets IsRecording = false
        /// to prevent dangling recorders. Populates VesselPersistentId on the tree Recording.
        /// </summary>
        void FlushRecorderToTreeRecording(FlightRecorder rec, RecordingTree tree)
        {
            if (rec == null || tree == null) return;

            string recId = tree.ActiveRecordingId;
            if (string.IsNullOrEmpty(recId))
            {
                ParsekLog.Warn("Flight",
                    "FlushRecorderToTreeRecording: dropping recorder data because active recording id is missing; " +
                    FormatRecorderDropDiagnostics(rec, tree, recId));
                rec.IsRecording = false;
                return;
            }

            Recording treeRec;
            if (!tree.Recordings.TryGetValue(recId, out treeRec))
            {
                ParsekLog.Warn("Flight",
                    $"FlushRecorderToTreeRecording: dropping recorder data because recording id '{recId}' " +
                    $"is not found in tree; {FormatRecorderDropDiagnostics(rec, tree, recId)}");
                rec.IsRecording = false;
                return;
            }

            // Close any open orbit segment (e.g. background phase) before copying data
            rec.FinalizeOpenOrbitSegment();

            // Append trajectory data
            treeRec.Points.AddRange(rec.Recording);
            treeRec.OrbitSegments.AddRange(rec.OrbitSegments);
            treeRec.PartEvents.AddRange(rec.PartEvents);
            treeRec.FlagEvents.AddRange(rec.FlagEvents);
            treeRec.SegmentEvents.AddRange(rec.SegmentEvents);
            treeRec.TrackSections.AddRange(rec.TrackSections);

            // Sort part/flag/segment events chronologically. PartEvents MUST use a STABLE sort so
            // same-UT events retain their insertion order — critical for terminal-vs-ignited
            // ordering across tree promotion boundaries (#287). FlagEvents and SegmentEvents use
            // the same stable pattern for consistency across every flush/merge site.
            var sortedPartEventsFlush = FlightRecorder.StableSortPartEventsByUT(treeRec.PartEvents);
            treeRec.PartEvents.Clear();
            treeRec.PartEvents.AddRange(sortedPartEventsFlush);
            var sortedFlagEventsFlush = FlightRecorder.StableSortByUT(treeRec.FlagEvents, e => e.ut);
            treeRec.FlagEvents.Clear();
            treeRec.FlagEvents.AddRange(sortedFlagEventsFlush);
            var sortedSegEventsFlush = FlightRecorder.StableSortByUT(treeRec.SegmentEvents, e => e.ut);
            treeRec.SegmentEvents.Clear();
            treeRec.SegmentEvents.AddRange(sortedSegEventsFlush);

            // Mark dirty so the next OnSave persists the flushed data to disk.
            // Without this, the in-memory points are lost on scene reload.
            treeRec.MarkFilesDirty();

            // Populate VesselPersistentId (required for RebuildBackgroundMap after save/load)
            if (treeRec.VesselPersistentId == 0 && rec.RecordingVesselId != 0)
                treeRec.VesselPersistentId = rec.RecordingVesselId;

            // Copy Phase 10 location fields if not already set on the tree recording
            if (string.IsNullOrEmpty(treeRec.StartBodyName))
                treeRec.StartBodyName = rec.StartBodyName;
            if (string.IsNullOrEmpty(treeRec.StartBiome))
                treeRec.StartBiome = rec.StartBiome;
            if (string.IsNullOrEmpty(treeRec.StartSituation))
                treeRec.StartSituation = rec.StartSituation;
            if (string.IsNullOrEmpty(treeRec.LaunchSiteName))
                treeRec.LaunchSiteName = rec.LaunchSiteName;

            // Forward start-of-recording controller identity onto the tree recording
            // if it does not already carry it. Critical for the BG go-on-rails
            // identity-loss override: if the always-tree root was created without
            // a live vessel and the recorder.StartRecording backstop did not fire
            // (e.g. the tree ActiveRecordingId was not yet pointed at this rec at
            // the time), this flush is the final chance to persist the identity
            // before the recording can be backgrounded.
            if (treeRec.AdoptControllersIfEmpty(rec.StartControllers))
            {
                ParsekLog.Verbose("Flight",
                    $"FlushRecorderToTreeRecording: forwarded {treeRec.Controllers.Count} controller part(s) " +
                    $"from recorder to tree recording '{recId}'");
            }

            ApplyCapturedLogisticsMetadataToRecording(
                treeRec,
                rec.CaptureAtStop,
                "FlushRecorderToTreeRecording");

            ApplyFinalSegmentPhaseFromCapture(
                treeRec,
                tree.ActiveRecordingId,
                rec.CaptureAtStop,
                tree);

            // Set IsRecording = false to prevent dangling active state
            rec.IsRecording = false;
            rec.TransitionToBackgroundPending = false;

            ParsekLog.Info("Flight", $"Flushed recorder to tree recording '{recId}': " +
                $"{rec.Recording.Count} points, {rec.OrbitSegments.Count} orbit segments, " +
                $"{rec.PartEvents.Count} part events");
        }

        internal static string FormatRecorderDropDiagnostics(FlightRecorder rec, RecordingTree tree, string attemptedRecordingId)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "tree={0} activeRec={1} attemptedRec={2} recVesselPid={3} " +
                "points={4} orbitSegments={5} partEvents={6} flagEvents={7} segmentEvents={8} trackSections={9}",
                string.IsNullOrEmpty(tree?.Id) ? "null" : tree.Id,
                string.IsNullOrEmpty(tree?.ActiveRecordingId) ? "null" : tree.ActiveRecordingId,
                string.IsNullOrEmpty(attemptedRecordingId) ? "null" : attemptedRecordingId,
                rec?.RecordingVesselId ?? 0u,
                rec?.Recording?.Count ?? 0,
                rec?.OrbitSegments?.Count ?? 0,
                rec?.PartEvents?.Count ?? 0,
                rec?.FlagEvents?.Count ?? 0,
                rec?.SegmentEvents?.Count ?? 0,
                rec?.TrackSections?.Count ?? 0);
        }

        internal static bool ApplyFinalSegmentPhaseFromCapture(
            Recording treeRec,
            string activeRecordingId,
            Recording captureAtStop,
            RecordingTree treeContext = null)
        {
            if (!ShouldApplyFinalSegmentPhaseFromCapture(treeRec, activeRecordingId, captureAtStop, treeContext))
                return false;

            return SetFinalSegmentPhase(
                treeRec,
                captureAtStop.SegmentPhase,
                captureAtStop.SegmentBodyName,
                "CaptureAtStop",
                captureAtStop.RecordingId);
        }

        internal static bool ShouldApplyFinalSegmentPhaseFromCapture(
            Recording treeRec,
            string activeRecordingId,
            Recording captureAtStop,
            RecordingTree treeContext = null)
        {
            return treeRec != null
                && captureAtStop != null
                && !string.IsNullOrEmpty(captureAtStop.SegmentPhase)
                && ShouldAllowFinalEndpointSegmentPhase(treeRec, activeRecordingId, treeContext);
        }

        /// <param name="treeContext">The recording's tree. With it, a recording whose child
        /// branch point leaves its vessel line open (a breakup-continuous flight that staged
        /// and kept flying) is still the endpoint row; without it any child branch point
        /// refuses.</param>
        internal static bool ShouldAllowFinalEndpointSegmentPhase(
            Recording rec,
            string activeRecordingId,
            RecordingTree treeContext = null)
        {
            if (rec == null)
                return false;
            if (string.IsNullOrEmpty(activeRecordingId)
                || !string.Equals(activeRecordingId, rec.RecordingId, StringComparison.Ordinal))
                return false;
            if (!string.IsNullOrEmpty(rec.ChildBranchPointId)
                && (treeContext == null
                    || !RecordingTree.ChildBranchLeavesVesselLineOpen(
                        rec, treeContext.Recordings, treeContext.BranchPoints)))
                return false;
            if (HasCommittedChainSegmentOwnership(rec))
                return false;

            return string.IsNullOrEmpty(rec.ChainId)
                || HasExplicitReFlyProvisionalIdentity(rec);
        }

        internal static bool HasCommittedChainSegmentOwnership(Recording rec)
        {
            return rec != null
                && !string.IsNullOrEmpty(rec.ChainId)
                && rec.ChainIndex >= 0;
        }

        private static bool HasExplicitReFlyProvisionalIdentity(Recording rec)
        {
            return rec != null
                && (!string.IsNullOrEmpty(rec.ProvisionalForRpId)
                    || !string.IsNullOrEmpty(rec.CreatingSessionId));
        }

        private static bool SetFinalSegmentPhase(
            Recording rec,
            string phase,
            string bodyName,
            string source,
            string sourceRecordingId = null)
        {
            if (rec == null || string.IsNullOrEmpty(phase))
                return false;

            string oldPhase = rec.SegmentPhase;
            string oldBody = rec.SegmentBodyName;
            if (string.Equals(oldPhase, phase, StringComparison.Ordinal)
                && string.Equals(oldBody, bodyName, StringComparison.Ordinal))
            {
                return false;
            }

            rec.SegmentPhase = phase;
            rec.SegmentBodyName = bodyName;
            rec.InvalidateSegmentBodyDisplayLabelCache();

            ParsekLog.Verbose("Flight",
                $"Final SegmentPhase applied from {source}: rec={rec.RecordingId ?? "<none>"} " +
                $"old={FormatSegmentPhaseForLog(oldBody, oldPhase)} " +
                $"new={FormatSegmentPhaseForLog(bodyName, phase)} " +
                $"sourceRec={sourceRecordingId ?? "<none>"}");
            return true;
        }

        private static string FormatSegmentPhaseForLog(string bodyName, string phase)
        {
            if (string.IsNullOrEmpty(phase))
                return "<none>";
            return string.IsNullOrEmpty(bodyName)
                ? phase
                : bodyName + " " + phase;
        }

        /// <summary>
        /// Copies rewind metadata from a live recorder onto the tree root using its
        /// CaptureAtStop when present, otherwise the recorder's in-memory fallback fields.
        /// Centralized so every recorder-backed commit path keeps the same rewind budget
        /// and pre-launch baseline semantics.
        /// </summary>
        internal static void CopyRewindSaveToRoot(RecordingTree tree, FlightRecorder sourceRecorder,
            string logTag = null)
        {
            if (sourceRecorder == null) return;

            CopyRewindSaveToRoot(
                tree,
                sourceRecorder.CaptureAtStop,
                recorderFallbackSave: sourceRecorder.RewindSaveFileName,
                recorderFallbackReservedFunds: sourceRecorder.RewindReservedFunds,
                recorderFallbackReservedScience: sourceRecorder.RewindReservedScience,
                recorderFallbackReservedRep: sourceRecorder.RewindReservedRep,
                recorderFallbackPreLaunchFunds: sourceRecorder.PreLaunchFunds,
                recorderFallbackPreLaunchScience: sourceRecorder.PreLaunchScience,
                recorderFallbackPreLaunchRep: sourceRecorder.PreLaunchReputation,
                logTag: logTag);
        }

        /// <summary>
        /// Copies the rewind save filename and reserved budget from a CaptureAtStop (or
        /// recorder fallback) to the tree's root recording. Called from every flush/commit
        /// site so the R button and rewind budget resolve correctly regardless of which
        /// child recorder is active at commit time.
        /// T59: without this, EVA branch recordings lose the rewind save because the EVA
        /// recorder never captures one — the parent's CaptureAtStop is the only source.
        /// </summary>
        internal static void CopyRewindSaveToRoot(RecordingTree tree, Recording captureAtStop,
            string recorderFallbackSave = null,
            double recorderFallbackReservedFunds = 0,
            double recorderFallbackReservedScience = 0,
            float recorderFallbackReservedRep = 0,
            double recorderFallbackPreLaunchFunds = 0,
            double recorderFallbackPreLaunchScience = 0,
            float recorderFallbackPreLaunchRep = 0,
            string logTag = null)
        {
            if (tree == null || string.IsNullOrEmpty(tree.RootRecordingId)) return;

            string rewindSave = captureAtStop?.RewindSaveFileName ?? recorderFallbackSave;
            if (string.IsNullOrEmpty(rewindSave)) return;

            Recording rootRec;
            if (!tree.Recordings.TryGetValue(tree.RootRecordingId, out rootRec)) return;

            // Only set if root doesn't already have a rewind save (first recorder wins)
            if (string.IsNullOrEmpty(rootRec.RewindSaveFileName))
            {
                rootRec.RewindSaveFileName = rewindSave;
                ParsekLog.Info("Flight",
                    $"{logTag ?? "CopyRewindSaveToRoot"}: copied rewind save '{rewindSave}' " +
                    $"to root recording '{tree.RootRecordingId}'");
            }

            // Copy reserved budget if not already set (same first-wins rule).
            // InitiateRewind reads these from the root recording via GetRewindRecording.
            if (rootRec.RewindReservedFunds == 0
                && rootRec.RewindReservedScience == 0 && rootRec.RewindReservedRep == 0)
            {
                rootRec.RewindReservedFunds = captureAtStop?.RewindReservedFunds ?? recorderFallbackReservedFunds;
                rootRec.RewindReservedScience = captureAtStop?.RewindReservedScience ?? recorderFallbackReservedScience;
                rootRec.RewindReservedRep = captureAtStop?.RewindReservedRep ?? recorderFallbackReservedRep;
            }

            // Copy pre-launch budget if not already set.
            if (rootRec.PreLaunchFunds == 0
                && rootRec.PreLaunchScience == 0 && rootRec.PreLaunchReputation == 0)
            {
                rootRec.PreLaunchFunds = captureAtStop?.PreLaunchFunds ?? recorderFallbackPreLaunchFunds;
                rootRec.PreLaunchScience = captureAtStop?.PreLaunchScience ?? recorderFallbackPreLaunchScience;
                rootRec.PreLaunchReputation = captureAtStop?.PreLaunchReputation ?? recorderFallbackPreLaunchRep;
            }
        }

        /// <summary>
        /// Creates a new FlightRecorder for the promoted vessel, starts recording with
        /// isPromotion=true, updates activeTree state.
        /// </summary>
        void PromoteRecordingFromBackground(
            string backgroundRecordingId,
            Vessel newVessel)
        {
            if (activeTree == null || newVessel == null) return;

            RecorderStateLog.RecState("PromoteFromBackground:entry", CaptureRecorderState());

            // Callers pass the target recording id explicitly; BackgroundMap membership is
            // not a hard precondition here. The committed-tree restore path may already have
            // removed stale entries for this recording before the live promotion runs.
            activeTree.BackgroundMap.Remove(newVessel.persistentId);

            // Set as the active recording
            activeTree.ActiveRecordingId = backgroundRecordingId;

            // Create a new recorder and start recording with isPromotion
            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;
            recorder.StartRecording(isPromotion: true);

            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight", $"PromoteRecordingFromBackground: StartRecording failed for pid={newVessel.persistentId}");
                recorder = null;
                activeTree.ActiveRecordingId = null;
                // Put it back in the background map
                activeTree.BackgroundMap[newVessel.persistentId] = backgroundRecordingId;
                backgroundRecorder?.OnVesselBackgrounded(newVessel.persistentId);
                return;
            }

            PrepareSessionStateForRecorderStart("PromoteRecordingFromBackground");

            // BUG #2: same live-resume anchor capture as ResumeCommittedActiveRecording —
            // a promoted background recording can also carry an old StartUT, so anchor on
            // the UT at which this recorder session began appending.
            liveResumeSessionStartUT = SafeCurrentUtForResumeAnchor();
            ParsekLog.Verbose("Flight",
                $"liveResumeSessionStartUT set reason=PromoteRecordingFromBackground " +
                $"recId={backgroundRecordingId} " +
                $"ut={liveResumeSessionStartUT.ToString("R", CultureInfo.InvariantCulture)}");

            ParsekLog.Info("Flight", $"Promoted recording '{backgroundRecordingId}' from background " +
                $"(pid={newVessel.persistentId})");
            RecorderStateLog.RecState("PromoteFromBackground:exit", CaptureRecorderState());
        }

        /// <summary>
        /// Takes back the committed tree of the active vessel when a committed recording
        /// matches it (copy-on-write clone) and binds a recorder. When the target recording's
        /// snapshot does not hold the live vessel's part persistentIds (a Parsek spawn
        /// regenerates them), the flight continues in a new switch-continuation segment under
        /// the target instead of recording into it, because ghost playback resolves part events
        /// against the recording's own snapshot. <paramref name="callerBuildsOwnSegment"/> is
        /// true only for <see cref="TryRouteCommittedSpawnedClone"/>, which attaches its own
        /// segment right after this returns.
        /// </summary>
        private bool TryRestoreCommittedTreeForSpawnedActiveVessel(bool callerBuildsOwnSegment = false)
        {
            if (activeTree != null || recorder != null || restoringActiveTree)
                return false;
            if (RecordingStore.HasPendingTree
                || ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady
                    != ParsekScenario.ActiveTreeRestoreMode.None)
            {
                return false;
            }

            Vessel activeVessel = FlightGlobals.ActiveVessel;
            uint activeVesselPid = activeVessel != null ? activeVessel.persistentId : 0;
            if (activeVesselPid == 0 || GhostMapPresence.IsGhostMapVessel(activeVesselPid))
                return false;

            if (IsCommittedSpawnedRestoreSuppressedForVessel(
                    committedSpawnedRestoreSuppressedPid, activeVesselPid))
            {
                // Standing condition re-reached by the 1 Hz Update retry: rate-limited
                // per pid. Returning before TryTakeCommittedTreeForSpawnedVesselRestore
                // keeps the copy-on-write clone and its restore attempt unarmed.
                ParsekLog.VerboseRateLimited("Flight",
                    "committed-spawned-restore-suppressed-" + activeVesselPid,
                    $"TryRestoreCommittedTreeForSpawnedActiveVessel: skipping vessel " +
                    $"'{activeVessel.vesselName}' pid={activeVesselPid} - committed-tree " +
                    "restore suppressed (a test-command DiscardTree, or its committed recording " +
                    "runs past the live clock)");
                return false;
            }

            if (ShouldSkipCommittedTreeRestoreForFreshLaunch(activeVesselPid, freshRolloutVesselPid))
            {
                // Standing condition, not an event: the per-frame Update retry re-reaches
                // this branch for the life of the scene (111 identical Info lines in one
                // collected session). Per the batch-counting convention it belongs on the
                // rate-limited channel with a per-pid key - the pid identity matters, so
                // one line per fresh-rollout vessel rather than one shared key.
                ParsekLog.VerboseRateLimited("Flight",
                    "fresh-rollout-skip-" + activeVesselPid,
                    $"TryRestoreCommittedTreeForSpawnedActiveVessel: skipping fresh-rollout " +
                    $"vessel '{activeVessel.vesselName}' pid={activeVesselPid} " +
                    "(matches captured scene-entry pid) - new mission gets its own tree");
                return false;
            }

            string activeVesselGuid = activeVessel.id != Guid.Empty
                ? activeVessel.id.ToString("N")
                : null;
            double liveUT = SafeCurrentUtForResumeAnchor();
            if (!TryTakeCommittedTreeForSpawnedVesselRestore(
                    activeVesselPid,
                    out RecordingTree committedTree,
                    out string targetRecordingId,
                    out CommittedSpawnedVesselRestoreAction action,
                    activeVesselGuid,
                    liveUT))
            {
                // A match refused only because its recording runs past the live clock (a
                // stock revert or a load put the vessel back before its committed future):
                // leave the vessel idle for this scene, as a DiscardTree does, so the 1 Hz
                // retry does not resume it the moment the clock passes the recording's end.
                if (!double.IsNaN(liveUT)
                    && !TryFindCommittedTreeForSpawnedVessel(
                        RecordingStore.CommittedTrees,
                        activeVesselPid,
                        out _,
                        out _,
                        activeVesselGuid,
                        liveUT)
                    && TryFindCommittedTreeForSpawnedVessel(
                        RecordingStore.CommittedTrees,
                        activeVesselPid,
                        out _,
                        out _,
                        activeVesselGuid))
                {
                    BeginCommittedSpawnedRestoreSuppression(
                        ref committedSpawnedRestoreSuppressedPid,
                        activeVesselPid,
                        activeVessel.vesselName,
                        hasActiveTree: activeTree != null,
                        hasRecorder: recorder != null,
                        reason: "committed-recording-ahead-of-clock");
                }
                return false;
            }

            activeTree = committedTree;
            EnsureBackgroundRecorderAttached("TryRestoreCommittedTreeForSpawnedActiveVessel");

            bool restored = false;
            bool continuedInSegment = false;
            if (action == CommittedSpawnedVesselRestoreAction.PromoteFromBackground)
                backgroundRecorder?.OnVesselRemovedFromBackground(activeVesselPid);
            if (!callerBuildsOwnSegment)
            {
                activeTree.Recordings.TryGetValue(targetRecordingId, out Recording targetRec);
                ResumedPartCoverage coverage = ResumedSpawnPartCoverage.Evaluate(
                    targetRec, CollectLivePartPersistentIds(activeVessel));
                if (coverage.Covered)
                {
                    ParsekLog.Verbose("Flight",
                        $"TryRestoreCommittedTreeForSpawnedActiveVessel: rec={targetRecordingId} " +
                        $"pid={activeVesselPid} resumes in place (parts {coverage.Reason}, " +
                        $"livePids={coverage.LivePartCount} snapshotPids={coverage.SnapshotPartCount} " +
                        $"snapshot={coverage.SnapshotSource})");
                }
                else
                {
                    continuedInSegment = TryContinueRestoredSpawnInNewSegment(
                        targetRecordingId, activeVessel, action, coverage, out restored);
                }
            }

            if (!continuedInSegment)
            {
                if (action == CommittedSpawnedVesselRestoreAction.ResumeActiveRecording)
                {
                    restored = ResumeCommittedActiveRecording(targetRecordingId, activeVessel);
                }
                else
                {
                    PromoteRecordingFromBackground(targetRecordingId, activeVessel);
                    restored = recorder != null && recorder.IsRecording;
                }
            }

            if (!restored)
            {
                ParsekLog.Warn("Flight",
                    $"TryRestoreCommittedTreeForSpawnedActiveVessel: tree '{activeTree.TreeName}' " +
                    $"matched vessel '{activeVessel.vesselName}' pid={activeVesselPid} via {action}, " +
                    "but recorder attach failed; tree remains restored for late recovery");
                RecorderStateLog.RecState("CommittedSpawnedRestore:failed", CaptureRecorderState());
                return false;
            }

            ParsekLog.Info("Flight",
                $"TryRestoreCommittedTreeForSpawnedActiveVessel: restored tree '{activeTree.TreeName}' " +
                $"for vessel '{activeVessel.vesselName}' pid={activeVesselPid} via {action}" +
                (continuedInSegment
                    ? $" (continues in new segment '{activeTree.ActiveRecordingId}')"
                    : ""));
            // Surface a screen message mirroring the launch and post-switch auto-record
            // paths. OnFlightReady fires before KSP's ScreenMessages UI queue is ready
            // to display messages; calling ScreenMessage directly here gets the toast
            // swallowed by the scene-load transition. Defer to a coroutine that waits
            // a short duration so the UI has settled — the launch path's (auto) toast
            // works because it posts from OnVesselSituationChange after physics starts,
            // well after scene-load.
            StartCoroutine(DeferredResumeScreenMessage());
            RecorderStateLog.RecState("CommittedSpawnedRestore:post", CaptureRecorderState());
            return true;
        }

        /// <summary>
        /// Captures the active vessel's pose after every physics step while a foreground
        /// recorder runs, so a physics callback that fires inside FixedUpdate (before that
        /// step's PhysX simulate) can still sample a UT and position from one step. See
        /// <see cref="PostPhysicsPoseCache"/>.
        /// </summary>
        private IEnumerator PostPhysicsPoseCaptureLoop()
        {
            var waitForFixedUpdate = new WaitForFixedUpdate();
            ParsekLog.Verbose("Recorder", "Post-physics pose capture loop started");
            while (true)
            {
                yield return waitForFixedUpdate;
                if (Patches.PhysicsFramePatch.ActiveRecorder == null)
                {
                    PostPhysicsPoseCache.Clear();
                    continue;
                }

                try
                {
                    PostPhysicsPoseCache.Capture(FlightGlobals.ActiveVessel);
                }
                catch (Exception ex)
                {
                    PostPhysicsPoseCache.Clear();
                    ParsekLog.WarnRateLimited("Recorder", "post-physics-pose-capture-failed",
                        $"Post-physics pose capture failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private System.Collections.IEnumerator DeferredResumeScreenMessage()
        {
            yield return new WaitForSeconds(0.5f);
            ScreenMessage("Recording STARTED (resume)", 4f);
        }

        /// <summary>
        /// The live vessel's part persistentIds (zero skipped), or an empty list when the
        /// vessel or its part list is unavailable.
        /// </summary>
        private static List<uint> CollectLivePartPersistentIds(Vessel vessel)
        {
            var pids = new List<uint>();
            if (vessel == null || vessel.parts == null)
                return pids;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part part = vessel.parts[i];
                if (part != null && part.persistentId != 0)
                    pids.Add(part.persistentId);
            }
            return pids;
        }

        /// <summary>
        /// The restore matched a committed recording whose snapshot does not hold the live
        /// vessel's part persistentIds (a Parsek spawn regenerated them). Recording into it
        /// would write part events its ghost cannot resolve, so attach a new
        /// switch-continuation segment under it (the plan's step 4 for flying a committed
        /// spawned vessel), bind the recorder to the segment and arm a session for it, which
        /// gives the segment scoped Discard and the scene-exit no-op auto-discard. Returns
        /// false (nothing changed, the caller resumes in place as before) when the segment
        /// cannot be attached; <paramref name="restored"/> reports the recorder bind.
        /// </summary>
        private bool TryContinueRestoredSpawnInNewSegment(
            string targetRecordingId,
            Vessel activeVessel,
            CommittedSpawnedVesselRestoreAction action,
            ResumedPartCoverage coverage,
            out bool restored)
        {
            restored = false;
            if (activeTree == null || activeVessel == null || string.IsNullOrEmpty(targetRecordingId))
                return false;

            uint livePid = activeVessel.persistentId;
            double ut = SafeCurrentUtForResumeAnchor();
            if (double.IsNaN(ut))
            {
                ParsekLog.Warn("Flight",
                    $"Resumed spawn segment refused: rec={targetRecordingId} pid={livePid} " +
                    "reason=no-clock - recording into the target as before");
                return false;
            }

            Guid sessionId = Guid.NewGuid();
            string newRecordingId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
            string newBranchPointId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);

            var creation = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                activeTree,
                targetRecordingId,
                livePid,
                activeVessel.vesselName ?? "Unknown",
                ut,
                SwitchSegmentEntryReason.ResumedSpawnedVessel,
                Guid.Empty,
                sessionId,
                newRecordingId,
                newBranchPointId,
                u => BuildSwitchSegmentBoundaryPoint(activeVessel, u),
                sourceVesselPersistentId: 0u);
            if (!creation.Created)
            {
                ParsekLog.Warn("Flight",
                    $"Resumed spawn segment refused: rec={targetRecordingId} pid={livePid} " +
                    $"reason={creation.FailureReason ?? "<null>"} - recording into the target as before");
                return false;
            }

            // The promote path removes the live pid from the background map; the segment
            // replaces that promotion, so do the same here.
            activeTree.BackgroundMap?.Remove(livePid);

            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "Resumed spawn continues in a new segment: rec={0} segment={1} vessel='{2}' pid={3} " +
                    "action={4} livePids={5} snapshotPids={6} uncovered={7} snapshot={8} ut={9}",
                    targetRecordingId,
                    newRecordingId,
                    activeVessel.vesselName ?? "<unnamed>",
                    livePid,
                    action,
                    coverage.LivePartCount,
                    coverage.SnapshotPartCount,
                    coverage.UncoveredPartCount,
                    coverage.SnapshotSource ?? "none",
                    ut.ToString("R", CultureInfo.InvariantCulture)));

            // No recorder is live here (the restore runs only when none is), so the bind
            // starts the segment's recorder without flushing anything into the target.
            restored = BindLiveRecorderToSwitchSegment(
                activeVessel, targetRecordingId, newRecordingId,
                routeTag: "resumed-spawn", switchUT: ut);

            ArmSwitchSegmentSession(
                SwitchSegmentEntryReason.ResumedSpawnedVessel,
                Guid.Empty,
                sessionId,
                activeTree.Id,
                creation.ParentRecordingId,
                newRecordingId,
                ut,
                livePid,
                sourcePid: 0u,
                committedTreeId: activeTree.Id);
            return true;
        }

        private bool ResumeCommittedActiveRecording(string recordingId, Vessel activeVessel)
        {
            if (activeTree == null || activeVessel == null || string.IsNullOrEmpty(recordingId))
                return false;

            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;
            recorder.StartRecording(isPromotion: true);
            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight",
                    $"ResumeCommittedActiveRecording: StartRecording returned IsRecording=false for " +
                    $"recording '{recordingId}' pid={activeVessel.persistentId}");
                recorder = null;
                activeTree.ActiveRecordingId = null;
                activeTree.BackgroundMap[activeVessel.persistentId] = recordingId;
                backgroundRecorder?.OnVesselBackgrounded(activeVessel.persistentId);
                return false;
            }

            PrepareSessionStateForRecorderStart("ResumeCommittedActiveRecording");

            // BUG #2: anchor the live-segment duration the pre-switch dialog will show.
            // The resumed committed recording's Points are appended in place, so its
            // StartUT is the original (multi-year-ago) launch UT — currentUT - StartUT
            // would still render the whole span. Capture the UT at which THIS recorder
            // session began appending so currentUT - liveResumeSessionStartUT yields the
            // short segment the player just flew.
            liveResumeSessionStartUT = SafeCurrentUtForResumeAnchor();
            ParsekLog.Verbose("Flight",
                $"liveResumeSessionStartUT set reason=ResumeCommittedActiveRecording " +
                $"recId={recordingId} " +
                $"ut={liveResumeSessionStartUT.ToString("R", CultureInfo.InvariantCulture)}");

            ParsekLog.Info("Flight",
                $"ResumeCommittedActiveRecording: resumed committed recording '{recordingId}' " +
                $"on vessel '{activeVessel.vesselName}' pid={activeVessel.persistentId}");
            return true;
        }

        private void EnsureBackgroundRecorderAttached(string reason)
        {
            if (activeTree == null || backgroundRecorder != null)
                return;

            backgroundRecorder = new BackgroundRecorder(activeTree);
            backgroundRecorder.SubscribePartEvents();
            Patches.PhysicsFramePatch.BackgroundRecorderInstance = backgroundRecorder;
            ParsekLog.Verbose("Flight",
                $"{reason}: attached BackgroundRecorder for tree '{activeTree.TreeName}' " +
                $"({activeTree.BackgroundMap.Count} background entry(ies))");

            // Callers attach either to a fresh tree (empty map, nothing to check) or to one
            // restored from a save or the committed list, so this is the load-time point at
            // which a background member stock dropped from the save (vessel budget / KSC
            // declutter) is first seen without a vessel. It is closed here, before the first
            // UpdateOnRails tick could advance its end UT past the save.
            HashSet<uint> livePids = TryBuildLiveVesselPidSet();
            if (livePids != null && activeTree.BackgroundMap.Count > 0)
                backgroundRecorder.CloseMissingBackgroundMembersAtLoad(livePids.Contains, reason);
        }

        /// <summary>
        /// The persistentIds of every vessel in <c>FlightGlobals.Vessels</c>, or null when the
        /// vessel list cannot be read (xUnit, no FlightGlobals), so a caller never mistakes an
        /// unreadable scene for an empty one.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static HashSet<uint> TryBuildLiveVesselPidSet()
        {
            List<Vessel> vessels;
            try { vessels = FlightGlobals.Vessels; }
            catch (TypeInitializationException) { return null; }
            catch (MissingMethodException) { return null; }
            catch (System.Security.SecurityException) { return null; }
            if (vessels == null)
                return null;
            var pids = new HashSet<uint>();
            for (int i = 0; i < vessels.Count; i++)
            {
                if (vessels[i] != null)
                    pids.Add(vessels[i].persistentId);
            }
            return pids;
        }

        /// <summary>
        /// Checks whether a vessel type alone qualifies as trackable.
        /// SpaceObject (asteroids, comets) and EVA kerbals are always trackable
        /// purely from the type. Other types require a module check
        /// (see <see cref="IsTrackableVessel"/>).
        /// </summary>
        internal static bool IsTrackableVesselType(VesselType vesselType)
        {
            return vesselType == VesselType.SpaceObject
                || vesselType == VesselType.EVA;
        }

        /// <summary>
        /// Determines whether a vessel is trackable (should get its own tree branch).
        /// Trackable = SpaceObject, EVA kerbal, OR has ModuleCommand
        /// (covers crewed pods and probe cores). Debris, spent boosters, fairings,
        /// etc. are NOT trackable.
        ///
        /// <para>
        /// EVA kerbals carry <c>KerbalEVA</c> rather than <c>ModuleCommand</c>, but
        /// they are directly controllable by the player — so for split-event
        /// classification they must count as a controllable output. Without this,
        /// an EVA split (mother vessel + kerbal) classifies as single-controllable,
        /// no <see cref="RewindPoint"/> is authored, and a destroyed EVA kerbal
        /// recording cannot satisfy <see cref="EffectiveState.IsUnfinishedFlight"/>
        /// (no RP back-reference for it to match against).
        /// </para>
        /// </summary>
        internal static bool IsTrackableVessel(Vessel v)
        {
            if (v == null) return false;

            // Space objects (asteroids, comets) are always trackable
            if (v.vesselType == VesselType.SpaceObject) return true;

            // EVA kerbals are controllable by the player even though their part
            // carries KerbalEVA rather than ModuleCommand.
            if (v.isEVA || v.vesselType == VesselType.EVA) return true;

            // Check for command capability (ModuleCommand covers both crewed pods and probe cores)
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p.FindModuleImplementing<ModuleCommand>() != null) return true;
            }
            return false;
        }

        /// <summary>
        /// Does a vessel that separated from a recorded vessel get its own recorded branch (a
        /// child recording with no debris lifetime that spawns at its end), rather than being
        /// treated as debris? Every trackable vessel does (<see cref="IsTrackableVessel"/>), and so
        /// does any vessel with a kerbal aboard: a crew cabin with no command module (a
        /// Hitchhiker, a Mk1 crew cabin) cannot be flown, but its kerbal is alive in a real
        /// vessel and must not read as lost (SPLIT-OF-A-CREWED-COMMANDLESS-PART-READS-AS-DEBRIS).
        /// Recording only: Rewind Points and Re-Fly slots still need a controllable vessel and
        /// keep reading <see cref="IsTrackableVessel"/>.
        /// </summary>
        internal static bool ShouldRecordAsOwnBranch(Vessel v)
        {
            if (v == null) return false;
            bool trackable = IsTrackableVessel(v);
            int crew = trackable ? 0 : SafeCrewCount(v);
            bool own = ShouldRecordAsOwnBranch(trackable, crew);
            if (own && !trackable)
            {
                ParsekLog.Info("Flight",
                    $"Separated vessel pid={v.persistentId} name='{v.vesselName ?? "<unnamed>"}' has no " +
                    $"command module but carries crew={crew.ToString(CultureInfo.InvariantCulture)}: " +
                    "recorded as its own branch, not debris (no Re-Fly slot)");
            }
            return own;
        }

        /// <summary>Pure half of <see cref="ShouldRecordAsOwnBranch(Vessel)"/>.</summary>
        internal static bool ShouldRecordAsOwnBranch(bool trackable, int crewCount)
        {
            return trackable || crewCount > 0;
        }

        private static int SafeCrewCount(Vessel v)
        {
            try { return v.GetCrewCount(); }
            catch (Exception ex)
            {
                ParsekLog.Warn("Flight",
                    $"ShouldRecordAsOwnBranch: crew count unreadable for pid={v.persistentId} " +
                    $"({ex.GetType().Name}); treating it as crewless");
                return 0;
            }
        }

        internal static bool ShouldIncludeVesselInDeferredBreakupScan(Vessel v, out string rejectReason)
        {
            rejectReason = null;
            if (v == null)
            {
                rejectReason = "null-vessel";
                return false;
            }

            if (IsSpaceObjectLikeBreakupScanReject(v.vesselType, EnumeratePartNames(v), EnumerateModuleNames(v),
                out rejectReason))
                return false;

            return true;
        }

        internal static bool IsSpaceObjectLikeBreakupScanReject(
            VesselType vesselType,
            IEnumerable<string> partNames,
            IEnumerable<string> moduleNames,
            out string rejectReason)
        {
            rejectReason = null;

            if (vesselType == VesselType.SpaceObject)
            {
                rejectReason = "space-object-type";
                return true;
            }

            if (ContainsAsteroidOrCometPart(partNames))
            {
                rejectReason = "asteroid-comet-part";
                return true;
            }

            if (ContainsAsteroidOrCometModule(moduleNames))
            {
                rejectReason = "asteroid-comet-module";
                return true;
            }

            return false;
        }

        private static IEnumerable<string> EnumeratePartNames(Vessel v)
        {
            if (v == null || v.parts == null)
                yield break;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                if (p.partInfo != null && !string.IsNullOrEmpty(p.partInfo.name))
                {
                    yield return p.partInfo.name;
                    continue;
                }
                if (!string.IsNullOrEmpty(p.partName))
                {
                    yield return p.partName;
                    continue;
                }
                if (!string.IsNullOrEmpty(p.name))
                    yield return p.name;
            }
        }

        private static IEnumerable<string> EnumerateModuleNames(Vessel v)
        {
            if (v == null || v.parts == null)
                yield break;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null || p.Modules == null)
                    continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.IsNullOrEmpty(module.moduleName))
                        yield return module.moduleName;
                    Type moduleType = module.GetType();
                    if (moduleType != null && !string.IsNullOrEmpty(moduleType.Name))
                        yield return moduleType.Name;
                }
            }
        }

        private static bool ContainsAsteroidOrCometPart(IEnumerable<string> partNames)
        {
            if (partNames == null)
                return false;

            foreach (string partName in partNames)
            {
                if (string.IsNullOrEmpty(partName))
                    continue;
                // Part cfg/runtime names can carry expansion or variant suffixes,
                // while module names below are stable C# class identifiers.
                if (partName.IndexOf("PotatoRoid", StringComparison.OrdinalIgnoreCase) >= 0
                    || partName.IndexOf("PotatoComet", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static bool ContainsAsteroidOrCometModule(IEnumerable<string> moduleNames)
        {
            if (moduleNames == null)
                return false;

            foreach (string moduleName in moduleNames)
            {
                if (string.IsNullOrEmpty(moduleName))
                    continue;
                if (string.Equals(moduleName, "ModuleAsteroid", StringComparison.Ordinal)
                    || string.Equals(moduleName, "ModuleComet", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Determines whether a debris vessel is significant enough to record.
        /// Filters out trivial crash fragments (single struts, panels, shroud pieces)
        /// while keeping meaningful debris like spent boosters and stages.
        /// </summary>
        internal static bool ShouldRecordDebris(Vessel v)
        {
            if (v == null) return false;
            int partCount = v.parts?.Count ?? 0;
            float mass = (float)v.totalMass;
            return ShouldRecordDebris(partCount, mass);
        }

        /// <summary>
        /// Pure testable overload: debris is significant if it has at least
        /// 3 parts OR at least 0.5 tons of mass.
        /// </summary>
        internal static bool ShouldRecordDebris(int partCount, float mass)
        {
            const int MinPartCount = 3;
            const float MinMassTons = 0.5f;
            return partCount >= MinPartCount || mass >= MinMassTons;
        }

        /// <summary>
        /// Appends captured trajectory data (points, orbit segments, part events, flag events,
        /// segment events, track sections) from a source recording into the target, sorts
        /// part/flag/segment events by UT, and sets the target's ExplicitEndUT.
        /// If source is null, only ExplicitEndUT is set.
        /// </summary>
        internal static void AppendCapturedDataToRecording(Recording target, Recording source, double endUT)
        {
            if (source != null)
            {
                target.Points.AddRange(source.Points);
                target.OrbitSegments.AddRange(source.OrbitSegments);
                target.PartEvents.AddRange(source.PartEvents);
                target.FlagEvents.AddRange(source.FlagEvents);
                target.SegmentEvents.AddRange(source.SegmentEvents);
                target.TrackSections.AddRange(source.TrackSections);
                // STABLE sort: same-UT events preserve insertion order (#287).
                var sortedAppend = FlightRecorder.StableSortPartEventsByUT(target.PartEvents);
                target.PartEvents.Clear();
                target.PartEvents.AddRange(sortedAppend);
                var sortedFlagAppend = FlightRecorder.StableSortByUT(target.FlagEvents, e => e.ut);
                target.FlagEvents.Clear();
                target.FlagEvents.AddRange(sortedFlagAppend);
                var sortedSegAppend = FlightRecorder.StableSortByUT(target.SegmentEvents, e => e.ut);
                target.SegmentEvents.Clear();
                target.SegmentEvents.AddRange(sortedSegAppend);
                ApplyCapturedLogisticsMetadataToRecording(
                    target,
                    source,
                    "AppendCapturedDataToRecording");
                // Mark dirty so the next OnSave persists the appended data to
                // the .prec sidecar. Without this, data lives only in memory
                // and is lost on scene reload (see Recording.MarkFilesDirty
                // docstring and CHANGELOG 2026-04-09 bug #273).
                target.MarkFilesDirty();
            }
            target.ExplicitEndUT = endUT;
        }

        internal static bool ApplyCapturedLogisticsMetadataToRecording(
            Recording target,
            Recording source,
            string sourcePath)
        {
            if (target == null || source == null)
                return false;

            bool changed = false;

            if (target.StartResources == null && HasEntries(source.StartResources))
            {
                target.StartResources = RouteProofMetadata.CloneResourceManifest(source.StartResources);
                changed = true;
            }
            if (HasEntries(source.EndResources))
            {
                target.EndResources = RouteProofMetadata.CloneResourceManifest(source.EndResources);
                changed = true;
            }

            if (target.StartInventory == null && HasEntries(source.StartInventory))
            {
                target.StartInventory = RouteProofMetadata.CloneInventoryManifest(source.StartInventory);
                changed = true;
            }
            if (HasEntries(source.EndInventory))
            {
                target.EndInventory = RouteProofMetadata.CloneInventoryManifest(source.EndInventory);
                changed = true;
            }
            if (target.StartInventorySlots == 0 && source.StartInventorySlots != 0)
            {
                target.StartInventorySlots = source.StartInventorySlots;
                changed = true;
            }
            if (source.EndInventorySlots != 0)
            {
                target.EndInventorySlots = source.EndInventorySlots;
                changed = true;
            }

            if (target.StartCrew == null && HasEntries(source.StartCrew))
            {
                target.StartCrew = RouteProofMetadata.CloneCrewManifest(source.StartCrew);
                changed = true;
            }
            if (HasEntries(source.EndCrew))
            {
                target.EndCrew = RouteProofMetadata.CloneCrewManifest(source.EndCrew);
                changed = true;
            }

            if (source.DockTargetVesselPid != 0)
            {
                target.DockTargetVesselPid = source.DockTargetVesselPid;
                changed = true;
            }

            // M2 run manifest (plan D14): adopt-once semantics for the START
            // half, unconditional adopt for the END half. The START half is
            // write-once (captured at the recording's birth and usually already
            // written onto the tree recording by the recorder); the END half is
            // overwrite-per-active-stop - a chain-boundary stop abandoned by
            // ResumeAfterFalseAlarm leaves a stale END that the eventual real
            // stop's capture must replace.
            if (source.RouteRunManifest != null && !target.RunManifestVoided)
            {
                if (target.RouteRunManifest == null || !target.RouteRunManifest.HasStartHalf)
                {
                    target.RouteRunManifest = source.RouteRunManifest.DeepClone();
                }
                else
                {
                    target.RouteRunManifest.EndTransportResources =
                        RouteProofMetadata.CloneResourceManifest(
                            source.RouteRunManifest.EndTransportResources);
                    target.RouteRunManifest.EndCaptured = source.RouteRunManifest.EndCaptured;
                }
                changed = true;
            }
            else if (source.RouteRunManifest != null)
            {
                // Sticky void tombstone wins (M2 review follow-up): a voided
                // leg never re-adopts a manifest - the void is the fail-closed
                // verdict for the whole leg.
                ParsekLog.Verbose("Flight",
                    $"Logistics metadata: run manifest NOT adopted (target voided) " +
                    $"target={target.RecordingId ?? "<none>"} source={source.RecordingId ?? "<none>"}");
            }

            // Propagate the void tombstone itself (sticky, one-way).
            if (source.RunManifestVoided && !target.RunManifestVoided)
            {
                target.RunManifestVoided = true;
                changed = true;
            }

            // M2 harvest windows (plan D4/D14): the capture carries the leg's
            // FULL window list (recorder-side until stop forwarding), so adopt
            // it wholesale per active stop - same overwrite-per-active-stop
            // semantics as the run-manifest END half.
            if (source.RouteHarvestWindows != null && source.RouteHarvestWindows.Count > 0)
            {
                target.RouteHarvestWindows =
                    RouteProofMetadata.CloneHarvestWindows(source.RouteHarvestWindows);
                changed = true;
            }

            // START-DOCKED ORIGIN PROOF: WRITE-ONCE ADOPTION.
            // FlightRecorder.BuildCaptureRecording attaches the start-time proof to the
            // CAPTURE recording (RouteProofCapture.AttachEndManifestsAndForwardToCapture).
            // In always-tree mode that capture is never committed as-is, so before this the
            // proof died here on every path a flight has ever taken
            // (ROUTE-ORIGIN-PROOF-NEVER-REACHES-A-TREE-RECORDING, measured by H57's first
            // flight and corroborated by ROUTE_ORIGIN_PROOF=0 in H56's and H57's produced
            // saves).
            //
            // WHICH CALL WINS: THE FIRST ONE THAT CARRIES A PROOF. This helper is shared by
            // the ordinary tree-mode stop flush (FlushRecorderToTreeRecording, the path H57
            // red on), the undock split and the dock merge (both through
            // AppendCapturedDataToRecording). The origin is a BIRTH fact of the run, so a
            // later call must not restate it; and - the reason a bare assignment would be
            // wrong - FlightRecorder nulls pendingRouteOriginProof at EVERY recorder start,
            // so a split-then-stop sequence would otherwise blank a real proof back out.
            // Adopt only into a null slot; never clear, never overwrite.
            if (source.RouteOriginProof != null && target.RouteOriginProof == null)
            {
                target.RouteOriginProof = source.RouteOriginProof.DeepClone();
                changed = true;
                ParsekLog.Info("Flight",
                    $"Logistics metadata: RouteOriginProof adopted (write-once) " +
                    $"path={sourcePath ?? "<unknown>"} " +
                    $"target={target.RecordingId ?? "<none>"} source={source.RecordingId ?? "<none>"} " +
                    $"originRoot={target.RouteOriginProof.StartDockedOriginRootPartUId.ToString(CultureInfo.InvariantCulture)} " +
                    $"originName='{target.RouteOriginProof.StartDockedOriginVesselName ?? "<none>"}' " +
                    $"originType={target.RouteOriginProof.StartDockedOriginVesselType.ToString(CultureInfo.InvariantCulture)}");
            }
            else if (source.RouteOriginProof != null)
            {
                ParsekLog.Verbose("Flight",
                    $"Logistics metadata: RouteOriginProof NOT adopted (target already carries one) " +
                    $"path={sourcePath ?? "<unknown>"} " +
                    $"target={target.RecordingId ?? "<none>"} source={source.RecordingId ?? "<none>"} " +
                    $"targetOriginRoot={target.RouteOriginProof.StartDockedOriginRootPartUId.ToString(CultureInfo.InvariantCulture)} " +
                    $"sourceOriginRoot={source.RouteOriginProof.StartDockedOriginRootPartUId.ToString(CultureInfo.InvariantCulture)}");
            }

            // Route-window / transfer fields are NOT forwarded here: dock route windows are
            // written directly onto the merged child in CreateMergeBranch, and forwarding
            // them here too would double up that path.

            if (changed)
            {
                ParsekLog.Verbose("Flight",
                    $"Logistics metadata copied: path={sourcePath ?? "<unknown>"} " +
                    $"target={target.RecordingId ?? "<none>"} source={source.RecordingId ?? "<none>"} " +
                    $"startRes={(target.StartResources?.Count ?? 0)} endRes={(target.EndResources?.Count ?? 0)} " +
                    $"startInv={(target.StartInventory?.Count ?? 0)} endInv={(target.EndInventory?.Count ?? 0)} " +
                    $"dockTargetPid={target.DockTargetVesselPid} " +
                    $"runManifest={(target.RouteRunManifest != null ? (target.RouteRunManifest.IsComplete ? "complete" : "start-only") : "none")} " +
                    $"harvestWindows={(target.RouteHarvestWindows?.Count ?? 0)}");
            }

            return changed;
        }

        private static bool HasEntries<TKey, TValue>(Dictionary<TKey, TValue> dict)
        {
            return dict != null && dict.Count > 0;
        }

        /// <summary>
        /// When in tree mode, appends captured split data to the active tree recording
        /// instead of creating a standalone recording. Returns true if the data was
        /// successfully appended, false to fall through to the standalone path.
        /// Bug #297: FallbackCommitSplitRecorder was tree-mode-unaware and orphaned
        /// continuation data as ungrouped standalone recordings.
        /// </summary>
        internal static bool TryAppendCapturedToTree(RecordingTree tree, Recording captured)
        {
            if (tree == null || captured == null || captured.Points.Count < 2)
                return false;

            string targetId = tree.ActiveRecordingId ?? tree.RootRecordingId;
            if (targetId == null)
            {
                ParsekLog.Warn("Flight",
                    "TryAppendCapturedToTree: no active or root recording id — falling through to standalone");
                return false;
            }

            Recording targetRec;
            if (!tree.Recordings.TryGetValue(targetId, out targetRec))
            {
                ParsekLog.Warn("Flight",
                    $"TryAppendCapturedToTree: recording '{targetId}' not found in tree — falling through to standalone");
                return false;
            }

            double endUT = captured.Points[captured.Points.Count - 1].ut;
            AppendCapturedDataToRecording(targetRec, captured, endUT);

            if (captured.VesselDestroyed)
            {
                targetRec.VesselDestroyed = true;
                targetRec.StampTerminalState(TerminalState.Destroyed, "TryAppendCapturedToTree");
            }

            if (captured.MaxDistanceFromLaunch > targetRec.MaxDistanceFromLaunch)
                targetRec.MaxDistanceFromLaunch = captured.MaxDistanceFromLaunch;

            targetRec.EndBiome = captured.EndBiome;

            // #416 R-button: bridge the captured rewind save / pre-launch budget / reserved
            // budget onto the tree root. FinalizeTreeRecordings' own CopyRewindSaveToRoot is
            // gated on this.recorder != null, but when a joint break on crash moves the
            // recorder into pendingSplitRecorder the main recorder is null by the time
            // FinalizeTreeRecordings runs — so without this call, crashed-vessel recordings
            // keep the on-disk parsek_rw_*.sfs but no recording ever references it and the
            // R button disappears. First-wins semantics inside CopyRewindSaveToRoot preserve
            // any legitimate pre-existing root data.
            CopyRewindSaveToRoot(tree, captured, logTag: "TryAppendCapturedToTree");

            ParsekLog.Info("Flight",
                $"TryAppendCapturedToTree: appended {captured.Points.Count} points to " +
                $"tree recording '{targetId}' (destroyed={captured.VesselDestroyed})");
            return true;
        }

        /// <summary>
        /// Applies a late destruction callback to a stopped recorder capture. Leaves
        /// <see cref="Recording.TerminalStateValue"/> as the pre-override diagnostic
        /// value; commit paths derive the final terminal state from VesselDestroyed.
        /// </summary>
        internal static bool ApplyPendingSplitDestructionToCapturedRecording(
            Recording captured,
            bool vesselDestroyedDuringRecording,
            string source)
        {
            if (captured == null || !vesselDestroyedDuringRecording)
                return false;
            if (captured.VesselDestroyed)
                return false;

            TerminalState? priorTerminal = captured.TerminalStateValue;
            captured.VesselDestroyed = true;

            ParsekLog.Info("Flight",
                $"Pending split destruction override: capture='{captured.RecordingId ?? "(null)"}' " +
                $"source={source ?? "(null)"} priorTerminal={priorTerminal?.ToString() ?? "null"} " +
                $"points={captured.Points?.Count ?? 0} snapshot={captured.VesselSnapshot != null}");
            return true;
        }

        /// <summary>
        /// Copies a stopped split capture onto the standalone fallback recording when no
        /// active tree accepts the capture.
        /// <para>The recorded vessel pid is stamped too. The capture itself does not carry
        /// it (<c>FlightRecorder.BuildCaptureRecording</c> leaves
        /// <c>VesselPersistentId</c> at 0), and every launch-identity guard keys on it: the
        /// rewind strip scope's same-vessel route, spawn adoption in FLIGHT / KSC /
        /// Tracking Station (<c>VesselSpawner.MaterializedSourceVesselExists(Recording)</c>)
        /// and the live-vessel ghost skip. A standalone recording without it reads as "no
        /// vessel", so a rewind keeps the live vessel it records and its terminal spawn
        /// builds a second copy of that vessel on top of it.</para>
        /// </summary>
        /// <param name="recordedVesselPid">The recorder's <c>RecordingVesselId</c>; 0 falls
        /// back to the capture snapshot's <c>persistentId</c>.</param>
        internal static void ApplyCapturedSplitStateToStandaloneRecording(
            Recording rec, Recording captured, uint recordedVesselPid = 0)
        {
            if (rec == null || captured == null)
                return;

            rec.VesselPersistentId = ResolveStandaloneRecordedVesselPid(
                recordedVesselPid, captured);
            rec.VesselSnapshot = captured.VesselSnapshot;
            rec.GhostVisualSnapshot = captured.GhostVisualSnapshot;
            // The capture already carries the launch guid (FlightRecorder stamps it in
            // BuildCaptureRecording); forwarding it here means the standalone fallback
            // recording does not have to wait for the load-time snapshot backfill to learn
            // which launch it recorded.
            rec.RecordedVesselGuid = captured.RecordedVesselGuid;
            rec.VesselDestroyed = captured.VesselDestroyed;
            rec.VesselSituation = captured.VesselSituation;
            rec.DistanceFromLaunch = captured.DistanceFromLaunch;
            rec.MaxDistanceFromLaunch = captured.MaxDistanceFromLaunch;
            rec.PreLaunchFunds = captured.PreLaunchFunds;
            rec.PreLaunchScience = captured.PreLaunchScience;
            rec.PreLaunchReputation = captured.PreLaunchReputation;
            rec.RewindSaveFileName = captured.RewindSaveFileName;
            rec.RewindReservedFunds = captured.RewindReservedFunds;
            rec.RewindReservedScience = captured.RewindReservedScience;
            rec.RewindReservedRep = captured.RewindReservedRep;
            rec.CopyStartLocationFrom(captured);
            rec.EndBiome = captured.EndBiome;
            rec.Controllers = captured.Controllers != null
                ? new List<ControllerInfo>(captured.Controllers)
                : null;

            if (captured.VesselDestroyed)
            {
                rec.StampTerminalState(TerminalState.Destroyed, "FallbackCommitSplitRecorder");
                ParsekLog.Verbose("Flight", "FallbackCommitSplitRecorder: set TerminalState=Destroyed");
            }
        }

        /// <summary>
        /// The recorded vessel pid for a standalone fallback recording: the recorder's own
        /// <c>RecordingVesselId</c> when known, else the capture snapshot's top-level
        /// <c>persistentId</c>, else 0.
        /// </summary>
        internal static uint ResolveStandaloneRecordedVesselPid(uint recordedVesselPid, Recording captured)
        {
            if (recordedVesselPid != 0)
                return recordedVesselPid;
            if (captured?.VesselSnapshot == null)
                return 0u;
            uint snapshotPid;
            string raw = captured.VesselSnapshot.GetValue("persistentId");
            if (!string.IsNullOrEmpty(raw)
                && uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out snapshotPid))
                return snapshotPid;
            return 0u;
        }

        /// <summary>
        /// Always-tree mode: every recorder is bound to a tree (<c>StartRecording</c> wraps a
        /// lone recording in a single-node tree and every resume sets
        /// <c>recorder.ActiveTree</c>). A stopped capture whose recorder was bound to a tree
        /// while NO tree is active belongs to a tree the scene already dropped. Every
        /// tree-drop site except <c>ResetFlightReadyState</c> drops the recorder with it;
        /// that one keeps a stopped recorder, and the next <c>StartRecording</c> resolves its
        /// capture here. A standalone commit of it is never written by OnSave (only
        /// RECORDING_TREE nodes are saved), so it would live until the next cold load only
        /// while replaying the dropped session's tail: after a Rewind-to-Launch that kept
        /// the vessel it records, its terminal spawn would build a second copy of that
        /// vessel on the same spot. Returns true (and logs) when the capture must be
        /// discarded instead of committed.
        /// </summary>
        internal static bool TryDiscardCaptureOfDroppedTree(
            RecordingTree liveActiveTree,
            RecordingTree recorderTree,
            Recording captured,
            uint recordedVesselPid)
        {
            if (liveActiveTree != null || recorderTree == null)
                return false;

            ParsekLog.Info("Flight", string.Format(CultureInfo.InvariantCulture,
                "FallbackCommitSplitRecorder: discarded capture of dropped tree '{0}' (id={1}) " +
                "vessel='{2}' pid={3} points={4} - no standalone commit outside a tree",
                recorderTree.TreeName ?? "<unnamed>",
                recorderTree.Id ?? "<no-id>",
                captured?.VesselName ?? "<unnamed>",
                recordedVesselPid,
                captured?.Points?.Count ?? 0));
            return true;
        }

        /// <summary>
        /// Tags the recording's SegmentPhase and SegmentBodyName based on the vessel's current
        /// body and altitude, if not already set. No-op if SegmentPhase is already non-empty
        /// or vessel/mainBody is null.
        /// </summary>
        internal static void TagSegmentPhaseIfMissing(Recording pending, Vessel v)
        {
            if (pending != null
                && string.IsNullOrEmpty(pending.SegmentPhase)
                && SegmentPhaseClassifier.TryClassify(v, out string phase, out string bodyName))
            {
                pending.SegmentBodyName = bodyName;
                pending.SegmentPhase = phase;
            }
        }

        /// <summary>
        /// Pure data-model method: creates the BranchPoint and two child Recording objects
        /// for a vessel split. Testable without Unity.
        /// For EVA branches, evaVesselPid identifies which child is the kerbal (receives
        /// EvaCrewName and ParentRecordingId). If 0, defaults to activeVesselPid.
        /// </summary>
        internal static (BranchPoint bp, Recording activeChild, Recording backgroundChild)
            BuildSplitBranchData(
                string parentRecordingId, string treeId, double branchUT,
                BranchPointType branchType, uint activeVesselPid, string activeVesselName,
                uint backgroundVesselPid, string backgroundVesselName,
                string evaCrewName = null, uint evaVesselPid = 0,
                int parentGeneration = 0)
        {
            string activeChildId = Guid.NewGuid().ToString("N");
            string backgroundChildId = Guid.NewGuid().ToString("N");
            string bpId = Guid.NewGuid().ToString("N");

            var bp = new BranchPoint
            {
                Id = bpId,
                UT = branchUT,
                Type = branchType,
                ParentRecordingIds = new List<string> { parentRecordingId },
                ChildRecordingIds = new List<string> { activeChildId, backgroundChildId }
            };

            // activeChild continues the same logical vessel — keep parent generation.
            // backgroundChild is the new spinoff — gets parentGeneration + 1.
            var activeChild = new Recording
            {
                RecordingId = activeChildId,
                TreeId = treeId,
                VesselPersistentId = activeVesselPid,
                VesselName = activeVesselName,
                ParentBranchPointId = bpId,
                ExplicitStartUT = branchUT,
                Generation = parentGeneration
            };

            var backgroundChild = new Recording
            {
                RecordingId = backgroundChildId,
                TreeId = treeId,
                VesselPersistentId = backgroundVesselPid,
                VesselName = backgroundVesselName,
                ParentBranchPointId = bpId,
                ExplicitStartUT = branchUT,
                Generation = parentGeneration + 1
            };

            // For EVA: kerbal child gets EvaCrewName and ParentRecordingId.
            // Identify kerbal by PID match (handles both active and background kerbal cases).
            if (branchType == BranchPointType.EVA && !string.IsNullOrEmpty(evaCrewName))
            {
                uint kerbalPid = evaVesselPid != 0 ? evaVesselPid : activeVesselPid;
                if (kerbalPid == activeVesselPid)
                {
                    activeChild.EvaCrewName = evaCrewName;
                    activeChild.ParentRecordingId = backgroundChildId;
                }
                else
                {
                    backgroundChild.EvaCrewName = evaCrewName;
                    backgroundChild.ParentRecordingId = activeChildId;
                }
            }

            return (bp, activeChild, backgroundChild);
        }

        internal static SegmentEnvironment? GetEvaBackgroundInitialEnvironmentOverride(
            BranchPointType branchType,
            bool backgroundChildIsEva,
            int activeSituation,
            double backgroundSrfSpeed)
        {
            if (branchType != BranchPointType.EVA || !backgroundChildIsEva)
                return null;

            if (activeSituation == (int)Vessel.Situations.LANDED ||
                activeSituation == (int)Vessel.Situations.SPLASHED ||
                activeSituation == (int)Vessel.Situations.PRELAUNCH)
            {
                return backgroundSrfSpeed > 0.1
                    ? SegmentEnvironment.SurfaceMobile
                    : SegmentEnvironment.SurfaceStationary;
            }

            return null;
        }

        internal static bool TryResolveTrackedBackgroundParentRecording(
            RecordingTree tree,
            uint sourceVesselPid,
            out string recordingId,
            out string diagnostic)
        {
            recordingId = null;

            if (tree == null)
            {
                diagnostic = "tree-null";
                return false;
            }

            if (sourceVesselPid == 0)
            {
                diagnostic = "source-pid-zero";
                return false;
            }

            if (tree.BackgroundMap != null
                && tree.BackgroundMap.TryGetValue(sourceVesselPid, out recordingId))
            {
                diagnostic = "background-map-hit";
                return true;
            }

            if (string.IsNullOrEmpty(tree.ActiveRecordingId))
            {
                tree.RebuildBackgroundMap();
                if (tree.BackgroundMap != null
                    && tree.BackgroundMap.TryGetValue(sourceVesselPid, out recordingId))
                {
                    diagnostic = "background-map-hit-after-rebuild";
                    return true;
                }

                diagnostic = "background-map-miss-after-rebuild";
                return false;
            }

            diagnostic = "background-map-miss-active-head-present";
            return false;
        }

        internal static bool CanStartRecorderWithActiveTreeHead(
            RecordingTree tree,
            uint activeVesselPid,
            out string reason)
        {
            reason = null;

            if (tree == null)
                return true;

            if (string.IsNullOrEmpty(tree.ActiveRecordingId))
            {
                reason = "active-recording-id-missing";
                return false;
            }

            Recording activeRecording;
            if (tree.Recordings == null
                || !tree.Recordings.TryGetValue(tree.ActiveRecordingId, out activeRecording)
                || activeRecording == null)
            {
                reason = $"active-recording-not-found:{tree.ActiveRecordingId}";
                return false;
            }

            uint activeRecordingPid = ResolveLiveTreeRecordingPidForRestore(activeRecording);
            if (activeVesselPid != 0 && activeRecordingPid != 0 && activeRecordingPid != activeVesselPid)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "active-recording-pid-mismatch:{0}!={1}",
                    activeRecordingPid,
                    activeVesselPid);
                return false;
            }

            return true;
        }

        internal static EvaBackgroundParentRouteDecision DecideEvaBackgroundParentRoute(
            bool isRecording,
            bool pendingSplitInProgress,
            bool hasActiveTree,
            bool hasSourceVessel,
            bool trackedParentResolved,
            bool mappedRecordingExists,
            bool hasCrewName)
        {
            if (isRecording || !hasActiveTree || !hasSourceVessel)
                return EvaBackgroundParentRouteDecision.NotHandled;

            if (pendingSplitInProgress)
                return EvaBackgroundParentRouteDecision.HandledSkipPendingSplit;

            if (!trackedParentResolved)
                return EvaBackgroundParentRouteDecision.HandledUntrackedSource;

            if (!mappedRecordingExists)
                return EvaBackgroundParentRouteDecision.HandledInvalidMapEntry;

            if (!hasCrewName)
                return EvaBackgroundParentRouteDecision.HandledMissingCrewName;

            return EvaBackgroundParentRouteDecision.StartDeferredBackgroundParentBranch;
        }

        internal static EvaBackgroundParentFocusDecision DecideEvaBackgroundParentFocus(
            uint currentActivePid,
            uint evaPid,
            uint sourceVesselPid)
        {
            if (currentActivePid != 0 && evaPid != 0 && currentActivePid == evaPid)
                return EvaBackgroundParentFocusDecision.EvaKerbalActive;
            if (currentActivePid != 0 && sourceVesselPid != 0 && currentActivePid == sourceVesselPid)
                return EvaBackgroundParentFocusDecision.SourceVesselActive;
            return EvaBackgroundParentFocusDecision.Invalid;
        }

        /// <summary>
        /// Decides whether an EVA branch recording that is still parked in the tree's
        /// BackgroundMap must be promoted to the live recorder at the moment its kerbal
        /// boards a vessel, so the board merge can run.
        /// </summary>
        /// <remarks>
        /// Ordering matters. The live wrapper runs inside <c>GameEvents.onCrewBoardVessel</c>,
        /// which KSP fires BEFORE <c>FlightGlobals.SetActiveVessel</c>, so the EVA kerbal is
        /// still the active vessel; <see cref="FlightRecorder.StartRecording"/> binds to
        /// <c>FlightGlobals.ActiveVessel</c>, which is why
        /// <paramref name="activeVesselPid"/> must still equal the EVA kerbal.
        /// </remarks>
        internal static EvaBoardPromotionDecision DecideEvaBoardPromotion(
            bool hasActiveTree,
            bool restoringActiveTree,
            bool sourceIsEva,
            bool sourceIsGhostVessel,
            uint sourceVesselPid,
            bool hasLiveRecorder,
            uint liveRecorderVesselPid,
            uint activeVesselPid,
            bool sourceTrackedInBackground,
            string trackedRecordingId,
            string treeActiveRecordingId)
        {
            if (!hasActiveTree)
                return EvaBoardPromotionDecision.SkipNoTree;

            if (restoringActiveTree)
                return EvaBoardPromotionDecision.SkipRestoring;

            if (sourceVesselPid == 0 || !sourceIsEva)
                return EvaBoardPromotionDecision.SkipNotEva;

            if (sourceIsGhostVessel)
                return EvaBoardPromotionDecision.SkipGhostVessel;

            // Green path: the first-modification watcher already promoted this EVA
            // recording (or it never left the live recorder). Leave it alone.
            if (hasLiveRecorder && liveRecorderVesselPid == sourceVesselPid)
                return EvaBoardPromotionDecision.NotNeeded;

            // A live recorder on some OTHER vessel owns the tree's active recording;
            // stealing it here would corrupt that recording's continuity.
            if (hasLiveRecorder)
                return EvaBoardPromotionDecision.SkipRecorderBusy;

            if (activeVesselPid != sourceVesselPid)
                return EvaBoardPromotionDecision.SkipEvaNotActiveVessel;

            if (!sourceTrackedInBackground)
                return EvaBoardPromotionDecision.SkipNoTrackedRecording;

            // The tree still names another recording as active while no live recorder owns it.
            // On the normal EVA-board path this is null: both OnVesselSwitchComplete
            // backgrounding branches park the old recording into BackgroundMap and clear
            // ActiveRecordingId before the kerbal ever boards. A non-null, different id here
            // means an earlier start failed midway (CreateMergeBranch step 10 nulls the recorder
            // but keeps its ActiveRecordingId), and PromoteRecordingFromBackground would
            // overwrite that id without parking it back into BackgroundMap - orphaning it.
            // Fail closed: skip the promotion (the board merge is then skipped and logged)
            // rather than trade one dropped merge for a lost recording.
            if (!string.IsNullOrEmpty(treeActiveRecordingId)
                && !string.Equals(treeActiveRecordingId, trackedRecordingId, StringComparison.Ordinal))
            {
                return EvaBoardPromotionDecision.SkipTreeActiveRecordingBusy;
            }

            return EvaBoardPromotionDecision.Promote;
        }

        internal static string BuildInvalidActiveTreeHeadRateLimitKey(
            string source,
            RecordingTree tree,
            uint activeVesselPid,
            string reason)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0}-invalid-active-tree-head-tree={1}-activeRec={2}-activePid={3}-reason={4}",
                string.IsNullOrEmpty(source) ? "unknown" : source,
                string.IsNullOrEmpty(tree?.Id) ? "none" : tree.Id,
                string.IsNullOrEmpty(tree?.ActiveRecordingId) ? "none" : tree.ActiveRecordingId,
                activeVesselPid,
                string.IsNullOrEmpty(reason) ? "unspecified" : reason);
        }

        internal struct BackgroundMapEntrySnapshot
        {
            public uint VesselPid;
            public bool HadEntry;
            public string RecordingId;
        }

        internal static List<BackgroundMapEntrySnapshot> CaptureBackgroundMapEntries(
            RecordingTree tree,
            params uint[] vesselPids)
        {
            var snapshots = new List<BackgroundMapEntrySnapshot>();
            if (tree?.BackgroundMap == null || vesselPids == null)
                return snapshots;

            var seen = new HashSet<uint>();
            for (int i = 0; i < vesselPids.Length; i++)
            {
                uint pid = vesselPids[i];
                if (pid == 0 || !seen.Add(pid))
                    continue;

                string recordingId;
                bool hadEntry = tree.BackgroundMap.TryGetValue(pid, out recordingId);
                snapshots.Add(new BackgroundMapEntrySnapshot
                {
                    VesselPid = pid,
                    HadEntry = hadEntry,
                    RecordingId = recordingId
                });
            }

            return snapshots;
        }

        internal static void RestoreBackgroundMapEntries(
            RecordingTree tree,
            List<BackgroundMapEntrySnapshot> snapshots)
        {
            if (tree?.BackgroundMap == null || snapshots == null)
                return;

            for (int i = 0; i < snapshots.Count; i++)
            {
                var snapshot = snapshots[i];
                if (snapshot.VesselPid == 0)
                    continue;

                if (snapshot.HadEntry)
                    tree.BackgroundMap[snapshot.VesselPid] = snapshot.RecordingId;
                else
                    tree.BackgroundMap.Remove(snapshot.VesselPid);
            }
        }

        internal static void RollbackBackgroundParentBranchMutation(
            RecordingTree tree,
            string previousActiveRecordingId,
            string parentRecordingId,
            string previousParentChildBranchPointId,
            BranchPoint bp,
            Recording activeChild,
            Recording backgroundChild,
            List<BackgroundMapEntrySnapshot> previousBackgroundMapEntries)
        {
            if (tree == null)
                return;

            tree.ActiveRecordingId = previousActiveRecordingId;

            if (!string.IsNullOrEmpty(parentRecordingId)
                && tree.Recordings.TryGetValue(parentRecordingId, out Recording parentRecording)
                && parentRecording != null)
            {
                parentRecording.ChildBranchPointId = previousParentChildBranchPointId;
            }

            if (bp != null && !string.IsNullOrEmpty(bp.Id))
                tree.BranchPoints.RemoveAll(candidate => candidate != null && candidate.Id == bp.Id);

            if (activeChild != null && !string.IsNullOrEmpty(activeChild.RecordingId))
                tree.Recordings.Remove(activeChild.RecordingId);
            if (backgroundChild != null && !string.IsNullOrEmpty(backgroundChild.RecordingId))
                tree.Recordings.Remove(backgroundChild.RecordingId);

            RestoreBackgroundMapEntries(tree, previousBackgroundMapEntries);
        }

        /// <summary>
        /// Pure data-model method: creates the BranchPoint and one child Recording object
        /// for a vessel merge (dock or board). Testable without Unity.
        /// Takes a list of parent recording IDs (1 for foreign vessel merge, 2 for two-tree merge)
        /// and returns a single child recording for the merged vessel.
        /// </summary>
        internal static (BranchPoint bp, Recording mergedChild)
            BuildMergeBranchData(
                List<string> parentRecordingIds, string treeId, double mergeUT,
                BranchPointType branchType, uint mergedVesselPid, string mergedVesselName,
                uint targetVesselPersistentId = 0,
                RouteConnectionKind transferKind = RouteConnectionKind.None,
                uint branchPartnerPid = 0,
                string branchPartnerGuid = null)
        {
            string childId = Guid.NewGuid().ToString("N");
            string bpId = Guid.NewGuid().ToString("N");
            uint routeTargetPid = targetVesselPersistentId;
            // Branch stamp decoupling (design-dock-event-graph.md 6.1): the branch
            // point carries the UNGATED couple-event partner identity when the caller
            // supplies one; legacy callers that pass only the gated route pid keep the
            // historical coupling (fallback). Route surfaces below read routeTargetPid
            // exclusively, so a stamp without route eligibility never invents proof.
            uint branchTargetPid = branchPartnerPid != 0
                ? branchPartnerPid : targetVesselPersistentId;

            var bp = new BranchPoint
            {
                Id = bpId,
                UT = mergeUT,
                Type = branchType,
                ParentRecordingIds = new List<string>(parentRecordingIds),
                ChildRecordingIds = new List<string> { childId },
                MergeCause = GetMergeCauseForBranchType(branchType),
                TargetVesselPersistentId = branchTargetPid,
                // The guid names the same vessel as the pid, so it never outlives a zero pid.
                TargetVesselGuid = branchTargetPid != 0 && !string.IsNullOrEmpty(branchPartnerGuid)
                    ? branchPartnerGuid : null
            };

            var mergedChild = new Recording
            {
                RecordingId = childId,
                TreeId = treeId,
                VesselPersistentId = mergedVesselPid,
                VesselName = mergedVesselName,
                ParentBranchPointId = bpId,
                ExplicitStartUT = mergeUT,
                TransferTargetVesselPid = branchType == BranchPointType.Dock ? routeTargetPid : 0,
                TransferKind = branchType == BranchPointType.Dock && routeTargetPid != 0
                    ? (transferKind != RouteConnectionKind.None ? transferKind : RouteConnectionKind.DockingPort)
                    : RouteConnectionKind.None
            };

            return (bp, mergedChild);
        }

        /// <summary>
        /// Claw producer (design-logistics-claw-producer.md 2.1): an EVA kerbal
        /// grab fires a real Part.Couple (findings 1.4) but is never a logistics
        /// transfer, so its route window is suppressed at capture by dropping
        /// the route target. Shared by both OnPartCouple paths (live and
        /// retroactive); pure except for the Info log, so unit tests pin the
        /// decision through the log sink.
        /// </summary>
        internal static uint SuppressRouteWindowForEvaGrab(
            uint routeTargetPid,
            bool involvesEva,
            RouteConnectionKind kind,
            string pathLabel)
        {
            if (!involvesEva || routeTargetPid == 0u)
                return routeTargetPid;
            ParsekLog.Info("Flight",
                $"OnPartCouple{pathLabel}: EVA grab, route window suppressed " +
                $"(kind={kind}, partnerPid={routeTargetPid})");
            return 0u;
        }

        /// <summary>
        /// The branch-point partner stamp: the couple-event partner identity, decoupled
        /// from route eligibility (design-dock-event-graph.md 6.1). Applies the same
        /// self/zero filtering as the route pid but NOT the snapshot/known-recording
        /// disjunct, so a route-ineligible dock still records who it docked with
        /// (derivable retroactively once the partner's tree commits).
        ///
        /// <para>
        /// The EVA suppression keys on the PARTNER side only
        /// (<paramref name="partnerIsEva"/>, from
        /// <see cref="ConnectionProducerClassifier.PartnerIsEvaVessel"/>): a claw grabbing
        /// a kerbal must not mint a claim keyed on the kerbal's pid, but a kerbal taking a
        /// stock external command seat (<c>KerbalEVA</c> <c>On_seatBoard</c> couples the
        /// kerbal's own part onto the seat part) is an EVA recorder merging into the
        /// partner vessel, and that vessel's stamp is the claim the chain walker and the
        /// rewind hold read. Pure; both OnPartCouple paths log the result on their
        /// resolve line.
        /// </para>
        /// </summary>
        internal static uint ResolveBranchPartnerStampPid(
            uint partnerPidFromEvent, uint selfVesselPid, bool partnerIsEva)
        {
            if (partnerIsEva || partnerPidFromEvent == 0u
                || partnerPidFromEvent == selfVesselPid)
                return 0u;
            return partnerPidFromEvent;
        }

        /// <summary>
        /// The branch-point partner stamp of a Board merge: the boarded vessel's pid and live
        /// launch guid when that vessel is outside the tree (another mission's vessel, or one
        /// no recording carries), so the chain walker and the rewind hold see the boarding as
        /// a claim on it, as they do a Dock onto it. A board onto a vessel of the same tree
        /// (its <c>BackgroundMap</c> holds it) is a two-parent merge that already closes that
        /// vessel's own line, so it stays unstamped. Pure.
        /// </summary>
        internal static uint ResolveBoardPartnerStamp(
            bool targetInTree, uint boardedVesselPid, string boardedVesselGuid, out string partnerGuid)
        {
            partnerGuid = null;
            if (targetInTree || boardedVesselPid == 0u)
                return 0u;
            partnerGuid = string.IsNullOrEmpty(boardedVesselGuid) ? null : boardedVesselGuid;
            return boardedVesselPid;
        }

        /// <summary>
        /// The launch guid to stamp beside the branch-point partner pid: the live
        /// <c>Vessel.id</c> of whichever couple-event side carries
        /// <paramref name="partnerStampPid"/>, read while both vessels still exist. The pid
        /// alone is craft-baked and freed when the partner is absorbed, so a relaunch of the
        /// same craft can reuse it; the guid keeps the chain walker and the cross-tree dock
        /// links from pooling the two launches when no recording in the claiming tree
        /// carries the partner's pid. Null when the stamp is 0 or neither side matches /
        /// has an id (the consumers then keep the pid-only resolution). Pure.
        /// </summary>
        internal static string ResolveDockPartnerGuidFromEvent(
            uint partnerStampPid, uint fromVesselPid, string fromVesselGuid,
            uint toVesselPid, string toVesselGuid)
        {
            if (partnerStampPid == 0u)
                return null;
            if (fromVesselPid == partnerStampPid && !string.IsNullOrEmpty(fromVesselGuid))
                return fromVesselGuid;
            if (toVesselPid == partnerStampPid && !string.IsNullOrEmpty(toVesselGuid))
                return toVesselGuid;
            return null;
        }

        /// <summary>
        /// PHANTOM-SUPERSEDE-RIDES-GATED-PID: resolves the pid whose committed terminal
        /// spawn a dock merge must suppress, returning 0 when the suppression does not
        /// apply.
        ///
        /// <para>
        /// The absorbed-vessel spawn suppression is a PARTNER-IDENTITY concern, not a
        /// route concern, so it reads the branch point's ungated couple-event partner
        /// stamp (<see cref="BranchPoint.TargetVesselPersistentId"/>, produced by
        /// <see cref="BuildMergeBranchData"/> from
        /// <see cref="ResolveBranchPartnerStampPid"/> with a legacy fallback to the gated
        /// route pid) rather than the route-eligibility-gated
        /// <c>routeTargetVesselPid</c>.
        /// </para>
        ///
        /// <para>
        /// <b>The newly-admitted population, stated precisely</b> — route eligibility is
        /// a DISJUNCTION (<c>pid resolvable &amp;&amp; (partnerSnapshotCaptured ||
        /// partnerKnown)</c>, both <c>OnPartCouple</c> paths), and
        /// <c>partnerSnapshotCaptured</c> is true for any partner still loaded with parts
        /// at couple time. So an ordinary physically-docking Parsek spawn WAS already
        /// eligible; "Parsek spawns were route-unknown" is too broad a claim and is not
        /// the mechanism. What the gated pid actually loses is the dock where the pid
        /// resolves but BOTH disjuncts fail: no usable pre-couple snapshot (KSP already
        /// reparented <c>data.from.vessel</c> onto <c>data.to.vessel</c> — the retroactive
        /// path's own §5.1 note — or the partner has no parts, or
        /// <c>FlightRecorder.TryBackupSnapshot</c> returned null) AND
        /// <c>IsKnownDockPartnerForRoute</c> finds nothing, since it compares ONLY against
        /// <see cref="Recording.VesselPersistentId"/> and a Parsek-spawned vessel is live
        /// under its KSP-unique <c>SpawnedVesselPersistentId</c>. That narrow set is
        /// nonetheless exactly what
        /// <see cref="RecordingStore.MarkTerminalSpawnSupersededByDockMerge"/> matches on
        /// its spawn-pid route, so it is real phantom exposure — the change is a strict
        /// widening, never a re-aim.
        /// </para>
        ///
        /// <para>
        /// <b>Read the guid gate correctly for that population.</b> The
        /// <c>absorbedLaunchGuid</c> the call site passes has exactly one source: the
        /// pre-couple partner snapshot. For a newly-admitted dock that snapshot disjunct
        /// was false by construction, so the guid is ALWAYS null there and the
        /// <c>GuidsConclusivelyDiffer</c> gate is inert on 100% of the new population.
        /// Safety comes from a different fact: <c>!partnerKnown</c> means no committed
        /// recording carries <c>VesselPersistentId == absorbedPid</c>, so the guid-gated
        /// <c>bakedPidMatch</c> route cannot fire at all — only the collision-free
        /// <c>uniqueSpawnMatch</c> route can, and CLAUDE.md's identity rule sanctions that
        /// one as pid-only precisely because a KSP-unique spawn pid cannot collide.
        /// </para>
        ///
        /// <para>
        /// The EVA-grab carve-out survives the switch: both
        /// <see cref="ResolveBranchPartnerStampPid"/> (when the partner is the EVA kerbal)
        /// and <see cref="SuppressRouteWindowForEvaGrab"/> zero their pid, so a claw grab of
        /// a kerbal still suppresses nothing. A kerbal taking an external seat stamps the
        /// seat's vessel, which survives as the merged vessel, so it suppresses nothing
        /// either (the merged-pid check below).
        /// </para>
        /// </summary>
        internal static uint ResolveDockMergeSpawnSuppressionPid(
            BranchPointType branchType, uint branchStampPid, uint mergedVesselPid)
        {
            // Dock-only: boarding does not absorb a separate spawned / adopted committed
            // leaf (a foreign Board's stamp names the vessel that flies on as the merged child).
            if (branchType != BranchPointType.Dock) return 0u;
            // 0 = no resolvable partner; == merged means the partner survived AS the
            // merged vessel, so its terminal spawn is owned by the live continuation.
            if (branchStampPid == 0u || branchStampPid == mergedVesselPid) return 0u;
            return branchStampPid;
        }

        internal static uint ResolveDockRouteTargetPid(
            bool activeWasDockTarget,
            uint mergedVesselPid,
            uint absorbedVesselPid)
        {
            // Returns the docking partner's persistentId -- the OTHER vessel from the
            // active recording's point of view. When the active recording survived as
            // the dock target (active.pid == mergedVesselPid), the partner is the
            // absorbed vessel; when the active recording was absorbed, the partner is
            // the surviving merged vessel. Runtime coverage must verify both paths
            // before route creation consumes this as endpoint proof.
            return activeWasDockTarget ? absorbedVesselPid : mergedVesselPid;
        }

        private static RouteEndpoint? BuildRouteEndpointFromVessel(
            Vessel vessel,
            uint expectedVesselPersistentId)
        {
            if (vessel == null || vessel.persistentId != expectedVesselPersistentId)
                return null;

            return new RouteEndpoint
            {
                VesselPersistentId = vessel.persistentId,
                // THE ENDPOINT'S LAUNCH-UNIQUE IDENTITY, STAMPED AT CAPTURE (2026-09-06,
                // ROUTE-ENDPOINT-TRANSFER-DOCKED-DOMINANT-PARTNER). Without it a destination
                // stop's resolution walk is pid -> proximity with the root-part step
                // unreachable BY CONSTRUCTION - and a base with any visitor docked to it
                // presents whichever half stock made dominant, so a DOMINANT VISITOR made the
                // pid step miss, the proximity step land on the composite, and the transfer
                // rebind the route to the visitor, which then flew away with it. With the
                // root id the walk starts at identity and resolves back to the base whichever
                // half dominates. Read HERE because this runs pre-couple, while the endpoint
                // still has its own Vessel: after Part.Couple the merged craft's root is the
                // DOMINANT half's, so a later read could name the wrong vessel.
                RootPartUId = Logistics.RouteEndpointResolver.ResolveRootPartFlightId(vessel),
                // AND ITS LAUNCH GUID, for symmetry with the origin path. The origin
                // endpoint carries the guid its undock bind READ
                // (RouteProofCapture's Stamped arm, RESOLVER-PID-STEP-NOT-GUID-GATED); a
                // destination captured here has the live vessel in hand, so the same key is
                // free and read at the same pre-couple instant as the root id. It only ever
                // NARROWS the resolver's pid step - the gate refuses a match whose guid
                // conclusively differs and falls through to proximity, and an unreadable
                // guid stays null, which is the ungated behaviour every pre-key route has.
                // Not hashed (RouteProofHasher excludes it deliberately), so stamping it
                // moves no existing route to SourceChanged.
                LaunchGuid = Logistics.RouteEndpointTransfer.TryReadLaunchGuid(vessel),
                BodyName = vessel.mainBody != null ? vessel.mainBody.bodyName : null,
                Latitude = vessel.latitude,
                Longitude = vessel.longitude,
                Altitude = vessel.altitude,
                IsSurface = IsRouteEndpointSurfaceSituation(vessel.situation)
            };
        }

        internal static bool TryBuildRouteEndpointFromSnapshot(
            ConfigNode snapshot,
            uint expectedVesselPersistentId,
            out RouteEndpoint endpoint,
            out int endpointSituation)
        {
            endpoint = default(RouteEndpoint);
            endpointSituation = -1;

            if (snapshot == null || expectedVesselPersistentId == 0)
                return false;

            string pidStr = snapshot.GetValue("persistentId");
            if (pidStr == null ||
                !uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pid) ||
                pid != expectedVesselPersistentId)
            {
                return false;
            }

            if (!TryParseRouteEndpointSituation(snapshot, out Vessel.Situations situation))
                return false;

            if (!TryGetSnapshotRouteCoordinate(snapshot, "lat", "latitude", out double latitude) ||
                !TryGetSnapshotRouteCoordinate(snapshot, "lon", "longitude", out double longitude) ||
                !TryGetSnapshotRouteCoordinate(snapshot, "alt", "altitude", out double altitude))
            {
                return false;
            }

            LiveVesselSnapshot.TryGetSnapshotReferenceBodyName(snapshot, out string bodyName);

            // Same stamp from the SNAPSHOT path: the background parent's own VESSEL node is
            // a pre-couple record of the endpoint, so its `root` index names the launch-unique
            // part flightID. An unreadable root leaves 0, which degrades this endpoint to the
            // pid + proximity walk it always had rather than refusing it.
            VesselSnapshotOps.TryReadRootPartFlightId(snapshot, out uint snapshotRootPartUId);

            endpoint = new RouteEndpoint
            {
                VesselPersistentId = pid,
                RootPartUId = snapshotRootPartUId,
                BodyName = bodyName,
                Latitude = latitude,
                Longitude = longitude,
                Altitude = altitude,
                IsSurface = IsRouteEndpointSurfaceSituation(situation)
            };
            endpointSituation = (int)situation;
            return true;
        }

        private static bool TryParseRouteEndpointSituation(
            ConfigNode snapshot,
            out Vessel.Situations situation)
        {
            situation = default(Vessel.Situations);
            string raw = snapshot?.GetValue("sit");
            if (string.IsNullOrEmpty(raw))
                return false;

            if (Enum.TryParse(raw, ignoreCase: true, out situation))
                return true;

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric) &&
                Enum.IsDefined(typeof(Vessel.Situations), numeric))
            {
                situation = (Vessel.Situations)numeric;
                return true;
            }

            return false;
        }

        private static bool TryGetSnapshotRouteCoordinate(
            ConfigNode snapshot,
            string primaryKey,
            string fallbackKey,
            out double value)
        {
            return VesselSnapshotOps.TryGetSnapshotDouble(snapshot, primaryKey, out value)
                || VesselSnapshotOps.TryGetSnapshotDouble(snapshot, fallbackKey, out value);
        }

        private static bool IsRouteEndpointSurfaceSituation(Vessel.Situations situation)
        {
            return situation == Vessel.Situations.LANDED ||
                situation == Vessel.Situations.SPLASHED ||
                situation == Vessel.Situations.PRELAUNCH;
        }

        private void CapturePendingDockRouteEndpointProof(
            GameEvents.FromToAction<Part, Part> data,
            uint routeTargetPid)
        {
            pendingDockRouteEndpointAtDock = null;
            pendingDockRouteEndpointSituation = -1;

            if (routeTargetPid == 0)
                return;

            Vessel endpointVessel = ResolveDockRouteEndpointVesselFromEvent(data, routeTargetPid);
            RouteEndpoint? endpoint = BuildRouteEndpointFromVessel(endpointVessel, routeTargetPid);
            if (endpoint.HasValue)
            {
                pendingDockRouteEndpointAtDock = endpoint;
                pendingDockRouteEndpointSituation = endpointVessel != null
                    ? (int)endpointVessel.situation
                    : -1;
                ParsekLog.Verbose("Flight",
                    $"CapturePendingDockRouteEndpointProof: captured endpoint proof targetPid={routeTargetPid} " +
                    $"situation={pendingDockRouteEndpointSituation}");
                return;
            }

            ParsekLog.Verbose("Flight",
                $"CapturePendingDockRouteEndpointProof: endpoint vessel unavailable at dock targetPid={routeTargetPid}; " +
                "will try background snapshot fallback");
        }

        private static Vessel ResolveDockRouteEndpointVesselFromEvent(
            GameEvents.FromToAction<Part, Part> data,
            uint routeTargetPid)
        {
            Vessel fromVessel = data.from != null ? data.from.vessel : null;
            if (fromVessel != null && fromVessel.persistentId == routeTargetPid)
                return fromVessel;

            Vessel toVessel = data.to != null ? data.to.vessel : null;
            if (toVessel != null && toVessel.persistentId == routeTargetPid)
                return toVessel;

            return FlightRecorder.FindVesselByPid(routeTargetPid);
        }

        private static string GetMergeCauseForBranchType(BranchPointType branchType)
        {
            switch (branchType)
            {
                case BranchPointType.Dock:
                    return "DOCK";
                case BranchPointType.Board:
                    return "BOARD";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Creates a tree branch for a vessel split event.
        /// Requires an active tree (always present in always-tree mode).
        /// Creates two child recordings (active + background), wires up BackgroundRecorder,
        /// and starts a new FlightRecorder for the active child.
        /// </summary>
        void CreateSplitBranch(BranchPointType branchType, Vessel activeVessel, Vessel backgroundVessel,
            double branchUT, string evaCrewName = null, uint evaVesselPid = 0,
            TrajectoryPoint? backgroundInitialTrajectoryPoint = null)
        {
            RecorderStateLog.RecState("CreateSplitBranch:entry", CaptureRecorderState());
            if (pendingSplitRecorder == null)
            {
                ParsekLog.Warn("Flight", "CreateSplitBranch: no pending split recorder — aborting");
                return;
            }

            var splitRecorder = pendingSplitRecorder;

            // Use the current active recording as parent
            string parentRecordingId = activeTree.ActiveRecordingId;

            if (parentRecordingId == null)
            {
                ParsekLog.Warn("Flight", "CreateSplitBranch: no active recording in existing tree — aborting");
                FallbackCommitSplitRecorder(splitRecorder);
                return;
            }

            // Flush captured data into the existing tree recording
            Recording parentRec;
            if (activeTree.Recordings.TryGetValue(parentRecordingId, out parentRec))
            {
                AppendCapturedDataToRecording(parentRec, splitRecorder.CaptureAtStop, branchUT);

                // Bug #271: in always-tree mode, the root recording was created without
                // a snapshot. Copy from CaptureAtStop on the first split so the ghost
                // mesh has vessel geometry instead of the sphere fallback.
                if (parentRec.VesselSnapshot == null && splitRecorder.CaptureAtStop?.VesselSnapshot != null)
                {
                    parentRec.VesselSnapshot = splitRecorder.CaptureAtStop.VesselSnapshot.CreateCopy();
                    parentRec.MarkFilesDirty();
                    ParsekLog.Info("Flight",
                        $"CreateSplitBranch: copied VesselSnapshot to parent '{parentRecordingId}' " +
                        "from CaptureAtStop (always-tree root had no snapshot)");
                }

                // T59: Copy rewind save from split recorder's CaptureAtStop to the root
                // recording. BuildCaptureRecording (FlightRecorder line 4562) copies the
                // filename into CaptureAtStop then clears it on the recorder (line 4573).
                // After the split, the new child recorder never has the rewind save, so
                // FinalizeTreeRecordings / StashActiveTreeAsPendingLimbo can't find it.
                // Copying here preserves it for the R button and rewind budget.
                CopyRewindSaveToRoot(activeTree, splitRecorder);
            }

            // Set ChildBranchPointId on parent
            Recording parentRecording;
            if (activeTree.Recordings.TryGetValue(parentRecordingId, out parentRecording))
            {
                // Will be set after we know the BP id
            }

            // Take snapshots for both child vessels
            ConfigNode activeSnapshot = FlightRecorder.TryBackupSnapshot(activeVessel);
            ConfigNode bgSnapshot = FlightRecorder.TryBackupSnapshot(backgroundVessel);

            // BOTH ROUTE CONSUMERS BELOW ARE UNDOCK-ONLY, AND THAT IS THE CONTRACT.
            // A joint break, a stack decoupler or a radial decoupler also reaches
            // CreateSplitBranch, with a DIFFERENT branchType, and neither completes a route
            // window nor binds a start-docked origin: the pair stays PENDING and the proof
            // stays a non-origin. Deliberate, not an oversight - the note under design
            // 19.2.2's provenance taxonomy says a tanker that DECOUPLES from a base is
            // witnessable "in principle" but is explicitly out of scope for every milestone,
            // and the revisit path it names is a separate "separated-with" loading variant
            // anchored to the controlled-decouple event, NOT a widening of this gate.
            if (branchType == BranchPointType.Undock && parentRec != null)
            {
                bool routeWindowCompleted = RouteProofCapture.TryCompleteLatestRouteConnectionWindow(
                    parentRec,
                    branchUT,
                    activeSnapshot,
                    bgSnapshot);
                if (routeWindowCompleted)
                {
                    ParsekLog.Info("Flight",
                        $"Route proof dock window completed on undock: parent={parentRecordingId} " +
                        $"ut={branchUT.ToString("R", CultureInfo.InvariantCulture)}");
                }

                // THE START-DOCKED ORIGIN BIND (P12). The recording started on a settled
                // docked pair; capture recorded BOTH halves and deferred the origin choice to
                // here, where the split has actually happened. The TRANSPORT is the half the
                // RUN was flying - named by the connection window that witnessed the dock, or
                // by which half's cargo rose across the docked span, with the focused side as
                // the last resort - and the origin is the other half. Ordering matters: the
                // window above is completed FIRST, so the binder can read it.
                //
                // THE PROOF WAS ADOPTED ONTO parentRec above by AppendCapturedDataToRecording
                // (write-once), so this binds the copy that will actually be persisted.
                TryBindStartDockedOriginOnUndock(
                    parentRec, parentRecordingId, activeVessel, backgroundVessel,
                    activeSnapshot, bgSnapshot, branchUT);
            }

            // Look up the parent's Generation so the children inherit correctly.
            // First-split path: parentRecording is the freshly-wrapped rootRec at gen=0.
            // Subsequent-split path: parent is whatever the active recording's gen is.
            int parentGen = parentRecording != null ? parentRecording.Generation : 0;

            // Build the branch data
            var (bp, activeChild, bgChild) = BuildSplitBranchData(
                parentRecordingId, activeTree.Id, branchUT, branchType,
                activeVessel != null ? activeVessel.persistentId : 0,
                activeVessel != null ? Recording.ResolveLocalizedName(activeVessel.vesselName) : "Unknown",
                backgroundVessel != null ? backgroundVessel.persistentId : 0,
                backgroundVessel != null ? Recording.ResolveLocalizedName(backgroundVessel.vesselName) : "Unknown",
                evaCrewName, evaVesselPid,
                parentGeneration: parentGen);

            // Set snapshots on child recordings
            activeChild.GhostVisualSnapshot = activeSnapshot;
            activeChild.VesselSnapshot = activeSnapshot != null ? activeSnapshot.CreateCopy() : null;
            bgChild.GhostVisualSnapshot = bgSnapshot;
            bgChild.VesselSnapshot = bgSnapshot != null ? bgSnapshot.CreateCopy() : null;

            // Pin the start-of-recording controller identity for each child from the
            // live split-moment vessel. Pure-factory boundary kept clean by capturing
            // here rather than threading Vessel into BuildSplitBranchData.
            activeChild.Controllers = ControllerInfo.CaptureFromVessel(activeVessel);
            bgChild.Controllers = ControllerInfo.CaptureFromVessel(backgroundVessel);

            // Same boundary, same reason, for the launch-unique identity: EACH child is
            // stamped from ITS OWN live vessel (never the other half's, and never the
            // parent's), falling back to that child's own snapshot `pid` - the value the
            // load-time backfill would read one save later. The active child would also pick
            // one up from the recorder-start backstop; the background child has no such path,
            // and a guid-less segment chained to a stamped one is the MIXED survivor set the
            // recovery correlator cannot corroborate
            // (KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY stage 2).
            activeChild.RecordedVesselGuid = BackgroundRecorder.ResolveSplitRecordingLaunchGuid(
                AnchorDetector.TryReadLiveVesselGuid(activeVessel),
                VesselLaunchIdentity.TryReadVesselGuid(activeChild.VesselSnapshot));
            bgChild.RecordedVesselGuid = BackgroundRecorder.ResolveSplitRecordingLaunchGuid(
                AnchorDetector.TryReadLiveVesselGuid(backgroundVessel),
                VesselLaunchIdentity.TryReadVesselGuid(bgChild.VesselSnapshot));

            // Set ChildBranchPointId on parent recording
            if (parentRecording != null)
                parentRecording.ChildBranchPointId = bp.Id;

            // Add to tree
            activeTree.BranchPoints.Add(bp);
            activeTree.AddOrReplaceRecording(activeChild);
            activeTree.AddOrReplaceRecording(bgChild);

            // Set active recording
            activeTree.ActiveRecordingId = activeChild.RecordingId;

            // Add background child to BackgroundMap FIRST, then notify BackgroundRecorder
            if (backgroundVessel != null && backgroundVessel.persistentId != 0)
            {
                var initialBackgroundEnvOverride = GetEvaBackgroundInitialEnvironmentOverride(
                    branchType,
                    backgroundVessel.isEVA,
                    activeVessel != null ? (int)activeVessel.situation : 0,
                    backgroundVessel.srfSpeed);

                if (initialBackgroundEnvOverride.HasValue)
                {
                    ParsekLog.Verbose("Flight",
                        $"CreateSplitBranch: forcing initial background env {initialBackgroundEnvOverride.Value} " +
                        $"for pid={backgroundVessel.persistentId}");
                }

                activeTree.BackgroundMap[backgroundVessel.persistentId] = bgChild.RecordingId;
                backgroundRecorder?.OnVesselBackgrounded(
                    backgroundVessel.persistentId,
                    initialEnvironmentOverride: initialBackgroundEnvOverride,
                    initialTrajectoryPoint: backgroundInitialTrajectoryPoint);
            }

            // Create new FlightRecorder for active child
            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;
            recorder.StartRecording(isPromotion: true);

            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight", $"CreateSplitBranch: StartRecording failed for active child");
                recorder = null;
            }
            else
            {
                PrepareSessionStateForRecorderStart("CreateSplitBranch");
            }

            ParsekLog.Info("Flight", $"Tree branch created: type={branchType}, " +
                $"bp={bp.Id}, activeChild={activeChild.RecordingId} (pid={activeChild.VesselPersistentId}), " +
                $"bgChild={bgChild.RecordingId} (pid={bgChild.VesselPersistentId})" +
                (backgroundInitialTrajectoryPoint.HasValue ? ", bgInitialSeed=part-origin" : "") +
                (evaCrewName != null ? $", evaCrew={evaCrewName}" : ""));
            RecorderStateLog.RecState("CreateSplitBranch:exit", CaptureRecorderState());

            // Phase 4 (Rewind-to-Staging): Undock + EVA paths call CreateSplitBranch
            // directly. Both are multi-controllable when both outputs carry command
            // authority (EVA kerbals and probe-cored vessels pass IsTrackableVessel).
            // JointBreak flows through ProcessBreakupEvent; its RP hook lives there.
            if (branchType == BranchPointType.Undock || branchType == BranchPointType.EVA)
            {
                TryAuthorRewindPointForSplit(bp, branchType, activeVessel, activeChild, backgroundVessel, bgChild);
            }
        }

        /// <summary>
        /// Live half of the start-docked origin BIND (P12). Reduces the two post-split
        /// snapshots to part-pid sets, reads the background vessel's live pid + launch guid
        /// and the recorded launch's, and hands the whole decision to
        /// <see cref="RouteProofCapture.TryBindStartDockedOriginAtUndock"/> - which owns the
        /// binding rule, the guid gate, the manifest re-scoping and the pickup validation, and
        /// is therefore drivable headlessly.
        ///
        /// <para>WHICH HALF IS WHICH is NOT decided here and no longer follows focus. The
        /// recorder does follow the FOCUSED vessel across a split (see
        /// <see cref="DeferredUndockBranch"/>), but focus is only a proxy for "which half the
        /// run is flying" and it is wrong whenever the merge made the PARTNER dominant - the
        /// operator's 2026-09-03 relay, where rover C took fuel off rover B and KSP held focus
        /// on B, so the binder named C its own origin. This method now supplies BOTH sides'
        /// snapshots, part sets and live identities plus the recording's connection windows,
        /// and <see cref="RouteProofCapture.ResolveTransportHalfAtUndock"/> picks the transport
        /// from the run's ACTIONS with focus as the last resort.</para>
        /// </summary>
        private void TryBindStartDockedOriginOnUndock(
            Recording parentRec,
            string parentRecordingId,
            Vessel activeVessel,
            Vessel backgroundVessel,
            ConfigNode activeSnapshot,
            ConfigNode bgSnapshot,
            double branchUT)
        {
            RouteOriginProof proof = parentRec?.RouteOriginProof;
            if (proof == null)
                return;

            List<uint> activePids = VesselSnapshotOps.CollectPartPersistentIds(activeSnapshot);
            List<uint> backgroundPids = VesselSnapshotOps.CollectPartPersistentIds(bgSnapshot);

            uint backgroundLivePid = backgroundVessel != null ? backgroundVessel.persistentId : 0u;
            string backgroundLiveGuid = (backgroundVessel != null && backgroundVessel.id != Guid.Empty)
                ? backgroundVessel.id.ToString("N")
                : null;
            uint activeLivePid = activeVessel != null ? activeVessel.persistentId : 0u;
            string activeLiveGuid = (activeVessel != null && activeVessel.id != Guid.Empty)
                ? activeVessel.id.ToString("N")
                : null;

            // THE PREVIOUS RECORDING OF THIS VESSEL
            // (ROUTE-ORIGIN-PROOF-PICKUP-PREDATING-THE-RECORDING). This recording opened
            // already docked, so it cannot have witnessed the load that put the cargo aboard
            // - but the recording BEFORE it can, and its connection windows are persisted.
            // The traversal is live because only here are the tree and its branch points in
            // hand; the DECISION is the pure ClassifyPredecessorPickup, which sees only the
            // window list. The walk is guid-gated and never leaves activeTree; see
            // docs/dev/research/pickup-predating-the-recording.md.
            Recording predecessorRec;
            string predecessorReason;
            bool havePredecessor = RouteProofCapture.TryResolveOriginProofPredecessor(
                activeTree, parentRec, out predecessorRec, out predecessorReason);
            ParsekLog.Verbose("Flight",
                $"RouteOriginProof predecessor walk: recording={parentRecordingId ?? "<none>"} " +
                $"resolved={(havePredecessor ? "1" : "0")} reason={predecessorReason} " +
                $"predecessor={(havePredecessor ? predecessorRec.RecordingId : "<none>")} " +
                $"predecessorWindows={(havePredecessor ? (predecessorRec.RouteConnectionWindows?.Count ?? 0) : 0)}");

            bool bound = RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof,
                activePids,
                backgroundPids,
                activeSnapshot,
                parentRec.VesselSnapshot,
                backgroundLivePid,
                backgroundLiveGuid,
                parentRec.VesselPersistentId,
                parentRec.RecordedVesselGuid,
                branchUT,
                parentRecordingId,
                backgroundSideSnapshot: bgSnapshot,
                recordingConnectionWindows: parentRec.RouteConnectionWindows,
                activeSideLiveVesselPid: activeLivePid,
                activeSideLiveVesselGuid: activeLiveGuid,
                predecessorConnectionWindows: havePredecessor ? predecessorRec.RouteConnectionWindows : null,
                recordingStartUT: parentRec.StartUT,
                predecessorContext: havePredecessor ? predecessorRec.RecordingId : null);

            if (bound)
                parentRec.MarkFilesDirty();

            ParsekLog.Verbose("Flight",
                $"RouteOriginProof bind attempt: parent={parentRecordingId ?? "<none>"} " +
                $"bound={(bound ? "1" : "0")} bindState={proof.StartDockedOriginBindState} " +
                $"activeVessel='{activeVessel?.vesselName ?? "<none>"}' " +
                $"backgroundVessel='{backgroundVessel?.vesselName ?? "<none>"}' " +
                $"activeParts={activePids?.Count ?? 0} backgroundParts={backgroundPids?.Count ?? 0}");
        }

        private void CreateSplitBranchFromBackgroundParent(
            string parentRecordingId,
            uint sourceVesselPid,
            Vessel activeVessel,
            Vessel backgroundVessel,
            double branchUT,
            string evaCrewName,
            uint evaVesselPid,
            string branchPath)
        {
            RecorderStateLog.RecState("CreateSplitBranchFromBackgroundParent:entry", CaptureRecorderState());

            if (activeTree == null)
            {
                ParsekLog.Warn("Flight", "CreateSplitBranchFromBackgroundParent: no active tree - aborting");
                return;
            }

            if (string.IsNullOrEmpty(parentRecordingId))
            {
                ParsekLog.Warn("Flight", "CreateSplitBranchFromBackgroundParent: no parent recording id - aborting");
                return;
            }

            Recording parentRecording;
            if (!activeTree.Recordings.TryGetValue(parentRecordingId, out parentRecording) || parentRecording == null)
            {
                ParsekLog.Warn("Flight",
                    $"CreateSplitBranchFromBackgroundParent: parent recording '{parentRecordingId}' not found - aborting");
                return;
            }

            if (BackgroundParentAlreadySplit(parentRecording, activeTree))
            {
                ParsekLog.Warn("Flight",
                    $"CreateSplitBranchFromBackgroundParent: parent recording '{parentRecordingId}' " +
                    $"already has child branch '{parentRecording.ChildBranchPointId}' - aborting");
                return;
            }
            if (!string.IsNullOrEmpty(parentRecording.ChildBranchPointId))
            {
                ParsekLog.Verbose("Flight",
                    $"CreateSplitBranchFromBackgroundParent: parent recording '{parentRecordingId}' " +
                    $"carries child branch '{parentRecording.ChildBranchPointId}' with no same-PID child " +
                    "(breakup-continuous) - splitting it; the new branch point takes the link");
            }

            string mappedParentRecordingId = null;
            if (sourceVesselPid == 0
                || !activeTree.BackgroundMap.TryGetValue(sourceVesselPid, out mappedParentRecordingId)
                || !string.Equals(mappedParentRecordingId, parentRecordingId, StringComparison.Ordinal))
            {
                ParsekLog.Warn("Flight",
                    $"CreateSplitBranchFromBackgroundParent: source background map entry changed before branch " +
                    $"(sourcePid={sourceVesselPid}, expectedParent={parentRecordingId}, " +
                    $"mappedParent={mappedParentRecordingId ?? "null"}) - aborting");
                return;
            }

            if (activeVessel == null || backgroundVessel == null)
            {
                ParsekLog.Warn("Flight",
                    $"CreateSplitBranchFromBackgroundParent: child vessel missing " +
                    $"(active={(activeVessel != null ? activeVessel.persistentId.ToString(CultureInfo.InvariantCulture) : "null")}, " +
                    $"background={(backgroundVessel != null ? backgroundVessel.persistentId.ToString(CultureInfo.InvariantCulture) : "null")}) - aborting");
                return;
            }

            uint currentActivePid = FlightGlobals.ActiveVessel?.persistentId ?? 0u;
            if (currentActivePid == 0 || activeVessel.persistentId != currentActivePid)
            {
                ParsekLog.Warn("Flight",
                    $"CreateSplitBranchFromBackgroundParent: active child does not match FlightGlobals.ActiveVessel " +
                    $"(activeChildPid={activeVessel.persistentId}, flightActivePid={currentActivePid}) - aborting");
                return;
            }

            ConfigNode activeSnapshot = FlightRecorder.TryBackupSnapshot(activeVessel);
            ConfigNode bgSnapshot = FlightRecorder.TryBackupSnapshot(backgroundVessel);
            int parentGen = parentRecording.Generation;

            var (bp, activeChild, bgChild) = BuildSplitBranchData(
                parentRecordingId, activeTree.Id, branchUT, BranchPointType.EVA,
                activeVessel.persistentId,
                Recording.ResolveLocalizedName(activeVessel.vesselName) ?? "Unknown",
                backgroundVessel.persistentId,
                Recording.ResolveLocalizedName(backgroundVessel.vesselName) ?? "Unknown",
                evaCrewName, evaVesselPid,
                parentGeneration: parentGen);

            activeChild.GhostVisualSnapshot = activeSnapshot;
            activeChild.VesselSnapshot = activeSnapshot != null ? activeSnapshot.CreateCopy() : null;
            bgChild.GhostVisualSnapshot = bgSnapshot;
            bgChild.VesselSnapshot = bgSnapshot != null ? bgSnapshot.CreateCopy() : null;

            // Pin the start-of-recording controller identity for each child from the
            // live split-moment vessel (background-parent EVA branch path).
            activeChild.Controllers = ControllerInfo.CaptureFromVessel(activeVessel);
            bgChild.Controllers = ControllerInfo.CaptureFromVessel(backgroundVessel);

            // Launch-unique identity, same contract as the sibling split path above: each
            // child from its OWN live vessel, snapshot `pid` as the fallback.
            activeChild.RecordedVesselGuid = BackgroundRecorder.ResolveSplitRecordingLaunchGuid(
                AnchorDetector.TryReadLiveVesselGuid(activeVessel),
                VesselLaunchIdentity.TryReadVesselGuid(activeChild.VesselSnapshot));
            bgChild.RecordedVesselGuid = BackgroundRecorder.ResolveSplitRecordingLaunchGuid(
                AnchorDetector.TryReadLiveVesselGuid(backgroundVessel),
                VesselLaunchIdentity.TryReadVesselGuid(bgChild.VesselSnapshot));

            string previousActiveRecordingId = activeTree.ActiveRecordingId;
            string previousParentChildBranchPointId = parentRecording.ChildBranchPointId;
            var previousBackgroundMapEntries = CaptureBackgroundMapEntries(
                activeTree,
                sourceVesselPid,
                activeVessel.persistentId,
                backgroundVessel.persistentId);

            parentRecording.ChildBranchPointId = bp.Id;
            activeTree.BranchPoints.Add(bp);
            activeTree.AddOrReplaceRecording(activeChild);
            activeTree.AddOrReplaceRecording(bgChild);
            activeTree.ActiveRecordingId = activeChild.RecordingId;

            // The source vessel is now either the active or background child, depending on KSP focus.
            activeTree.BackgroundMap.Remove(activeVessel.persistentId);
            activeTree.BackgroundMap.Remove(backgroundVessel.persistentId);
            SegmentEnvironment? initialBackgroundEnvOverride = null;
            if (backgroundVessel.persistentId != 0)
            {
                initialBackgroundEnvOverride = GetEvaBackgroundInitialEnvironmentOverride(
                    BranchPointType.EVA,
                    backgroundVessel.isEVA,
                    (int)activeVessel.situation,
                    backgroundVessel.srfSpeed);

                if (initialBackgroundEnvOverride.HasValue)
                {
                    ParsekLog.Verbose("Flight",
                        $"CreateSplitBranchFromBackgroundParent: forcing initial background env " +
                        $"{initialBackgroundEnvOverride.Value} for pid={backgroundVessel.persistentId}");
                }

                activeTree.BackgroundMap[backgroundVessel.persistentId] = bgChild.RecordingId;
            }

            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;
            recorder.StartRecording(isPromotion: true);

            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight",
                    $"CreateSplitBranchFromBackgroundParent: StartRecording failed for active child " +
                    $"pid={activeChild.VesselPersistentId}");
                recorder = null;
                RollbackBackgroundParentBranchMutation(
                    activeTree,
                    previousActiveRecordingId,
                    parentRecordingId,
                    previousParentChildBranchPointId,
                    bp,
                    activeChild,
                    bgChild,
                    previousBackgroundMapEntries);
                return;
            }

            if (recorder.RecordingVesselId != activeChild.VesselPersistentId)
            {
                ParsekLog.Warn("Flight",
                    $"CreateSplitBranchFromBackgroundParent: recorder vessel mismatch after start " +
                    $"(recorderPid={recorder.RecordingVesselId}, activeChildPid={activeChild.VesselPersistentId})");
                recorder.StopRecordingForChainBoundary();
                recorder = null;
                RollbackBackgroundParentBranchMutation(
                    activeTree,
                    previousActiveRecordingId,
                    parentRecordingId,
                    previousParentChildBranchPointId,
                    bp,
                    activeChild,
                    bgChild,
                    previousBackgroundMapEntries);
                return;
            }

            backgroundRecorder?.OnVesselRemovedFromBackground(sourceVesselPid);
            if (backgroundVessel.persistentId != 0)
            {
                backgroundRecorder?.OnVesselBackgrounded(
                    backgroundVessel.persistentId,
                    initialEnvironmentOverride: initialBackgroundEnvOverride);
            }

            ClearPendingEvaAutoRecordState();
            PrepareSessionStateForRecorderStart("CreateSplitBranchFromBackgroundParent");

            ParsekLog.Info("Flight", $"Tree branch created: type=EVA, " +
                $"bp={bp.Id}, activeChild={activeChild.RecordingId} (pid={activeChild.VesselPersistentId}), " +
                $"bgChild={bgChild.RecordingId} (pid={bgChild.VesselPersistentId}), " +
                $"path={branchPath}" +
                (evaCrewName != null ? $", evaCrew={evaCrewName}" : ""));
            RecorderStateLog.RecState("CreateSplitBranchFromBackgroundParent:exit", CaptureRecorderState());

            TryAuthorRewindPointForSplit(bp, BranchPointType.EVA, activeVessel, activeChild, backgroundVessel, bgChild);
        }

        /// <summary>
        /// Phase 4 (Rewind-to-Staging, design §5.1 + §7.1 + §7.2 + §7.19): if the
        /// split has >=2 controllable children, builds a <see cref="ChildSlot"/>
        /// list from the freshly-created child recordings and hands it to
        /// <see cref="RewindPointAuthor.Begin"/>. Called from both the Undock/EVA
        /// side of <see cref="CreateSplitBranch"/> and from the structural
        /// <c>ProcessBreakupEvent</c> path. An EVA split reaches Begin and is
        /// refused there (<see cref="RewindPointAuthor.IsReFlySplitType"/>): Re-Fly
        /// is for vessel separations only.
        ///
        /// <para>
        /// The resolver returned here looks up the child PID by recording id in
        /// <c>activeTree.Recordings</c>, because at split time the newly-created
        /// child recordings are in the active tree but not yet committed.
        /// </para>
        /// </summary>
        void TryAuthorRewindPointForSplit(
            BranchPoint bp,
            BranchPointType branchType,
            Vessel activeVessel, Recording activeChild,
            Vessel backgroundVessel, Recording backgroundChild)
        {
            if (bp == null)
            {
                ParsekLog.Warn("Rewind", $"TryAuthorRewindPointForSplit: bp is null ({branchType})");
                return;
            }

            var candidatePids = new List<uint>();
            if (activeVessel != null) candidatePids.Add(activeVessel.persistentId);
            if (backgroundVessel != null) candidatePids.Add(backgroundVessel.persistentId);

            uint origPid = activeVessel != null ? activeVessel.persistentId : 0;
            var controllable = SegmentBoundaryLogic.IdentifyControllableChildren(origPid, candidatePids);

            if (!SegmentBoundaryLogic.IsMultiControllableSplit(controllable.Count))
            {
                ParsekLog.Info("Rewind",
                    $"Single-controllable split: no RP (bp={bp.Id} type={branchType} " +
                    $"controllable={controllable.Count})");
                return;
            }

            AuthorRewindPointFromVesselRecordings(
                bp,
                controllable,
                new (Vessel vessel, Recording rec)[] {
                    (activeVessel, activeChild),
                    (backgroundVessel, backgroundChild)
                });
        }

        /// <summary>
        /// Phase 4: shared back-end for both Undock/EVA and Breakup RP authoring.
        /// Builds <see cref="ChildSlot"/> entries for each controllable (vessel, recording)
        /// pair whose PID is present in <paramref name="controllablePids"/>, then
        /// delegates to <see cref="RewindPointAuthor.Begin"/>. The resolver returned
        /// here maps <c>OriginChildRecordingId</c> -> <c>Vessel.persistentId</c> using
        /// the just-built map so the deferred coroutine does not need to consult the
        /// active tree or <c>RecordingStore.CommittedRecordings</c>.
        /// </summary>
        void AuthorRewindPointFromVesselRecordings(
            BranchPoint bp,
            List<uint> controllablePids,
            IReadOnlyList<(Vessel vessel, Recording rec)> pairs)
        {
            var slots = new List<ChildSlot>();
            var recIdToPid = new Dictionary<string, uint>(StringComparer.Ordinal);

            int slotIndex = 0;
            for (int i = 0; i < pairs.Count; i++)
            {
                var (vessel, rec) = pairs[i];
                if (vessel == null || rec == null) continue;
                if (!controllablePids.Contains(vessel.persistentId)) continue;
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
                    $"Multi-controllable classifier passed ({controllablePids.Count}) but slot build " +
                    $"produced only {slots.Count} entries (bp={bp.Id}); skipping RP");
                return;
            }

            var ctx = new RewindPointAuthorContext
            {
                RecordingResolver = recId =>
                    recIdToPid.TryGetValue(recId ?? "", out var pid) ? (uint?)pid : null
            };

            RewindPointAuthor.Begin(bp, slots, controllablePids, ctx);
        }

        /// <summary>
        /// Phase 4: RP hook for structural joint-break (Breakup) branch points.
        /// Treats the surviving parent + each controllable child fragment as
        /// candidates; if at least 2 pass <see cref="SegmentBoundaryLogic.IdentifyControllableChildren"/>,
        /// delegates to <see cref="AuthorRewindPointFromVesselRecordings"/>.
        /// </summary>
        void TryAuthorRewindPointForBreakup(
            BranchPoint breakupBp,
            IReadOnlyList<(Vessel vessel, Recording rec)> pairs)
        {
            if (breakupBp == null || pairs == null || pairs.Count == 0)
                return;

            var candidatePids = new List<uint>();
            for (int i = 0; i < pairs.Count; i++)
            {
                var v = pairs[i].vessel;
                if (v != null) candidatePids.Add(v.persistentId);
            }

            uint origPid = pairs.Count > 0 && pairs[0].vessel != null ? pairs[0].vessel.persistentId : 0;
            var controllable = SegmentBoundaryLogic.IdentifyControllableChildren(origPid, candidatePids);

            if (!SegmentBoundaryLogic.IsMultiControllableSplit(controllable.Count))
            {
                ParsekLog.Info("Rewind",
                    $"Single-controllable split: no RP (bp={breakupBp.Id} type={breakupBp.Type} " +
                    $"controllable={controllable.Count})");
                return;
            }

            AuthorRewindPointFromVesselRecordings(breakupBp, controllable, pairs);
        }

        /// <summary>
        /// Pure helper: resolves the dock partner's vessel persistentId directly from
        /// the KSP couple event, independent of <c>activeTree.BackgroundMap</c>. Returns
        /// the side of the dock pair that is NOT the recorder's own vessel, or 0 when
        /// neither side qualifies. Used by the route-proof path so cross-tree partners
        /// (committed recording in a previous tree, not in the current
        /// <c>BackgroundMap</c>) still resolve correctly.
        /// </summary>
        internal static uint ResolveDockPartnerPidFromEvent(
            uint fromVesselPid,
            uint toVesselPid,
            uint selfVesselPid)
        {
            if (fromVesselPid != 0 && fromVesselPid != selfVesselPid) return fromVesselPid;
            if (toVesselPid != 0 && toVesselPid != selfVesselPid) return toVesselPid;
            return 0;
        }

        /// <summary>
        /// Returns whichever of <c>data.from.vessel</c> / <c>data.to.vessel</c> is the
        /// recorder's OWN (transport) vessel, matched by <paramref name="recordingVesselId"/>,
        /// INCLUDING when that side is the dock survivor (<c>data.to.vessel</c>, whose pid
        /// becomes the merged pid). Used to capture a pre-couple transport snapshot for the
        /// route window's DOCK_TRANSPORT_RESOURCES baseline. Returns null when neither side
        /// matches (the recorder is observing a third vessel), in which case the caller
        /// falls back to the post-couple merged snapshot (current behaviour). Unlike a dock
        /// PARTNER, the transport often IS the survivor, so this must NOT pick the
        /// non-survivor side the way <see cref="ResolveDockPartnerPidFromEvent"/> does.
        /// </summary>
        private static Vessel ResolveSelfVessel(
            GameEvents.FromToAction<Part, Part> data, uint recordingVesselId)
        {
            if (recordingVesselId == 0) return null;
            if (data.from?.vessel != null && data.from.vessel.persistentId == recordingVesselId)
                return data.from.vessel;
            if (data.to?.vessel != null && data.to.vessel.persistentId == recordingVesselId)
                return data.to.vessel;
            return null;
        }

        /// <summary>
        /// Validation gate for route-eligibility of a dock partner. Returns true when
        /// the partner has a known Parsek recording — either an entry in the current
        /// <paramref name="activeTreeRecordings"/> (excluding the recorder's own
        /// vessel) or in <paramref name="committedRecordings"/>. Returns false for
        /// truly foreign vessels (KSP-visible but no Parsek recording), keeping them
        /// out of the route-proof path while still allowing the dock to merge normally.
        /// </summary>
        internal static bool IsKnownDockPartnerForRoute(
            uint partnerPid,
            uint selfVesselPid,
            IEnumerable<Recording> activeTreeRecordings,
            IReadOnlyList<Recording> committedRecordings)
        {
            if (partnerPid == 0 || partnerPid == selfVesselPid) return false;

            if (activeTreeRecordings != null)
            {
                foreach (Recording r in activeTreeRecordings)
                {
                    if (r != null && r.VesselPersistentId == partnerPid) return true;
                }
            }

            if (committedRecordings != null)
            {
                for (int i = 0; i < committedRecordings.Count; i++)
                {
                    Recording r = committedRecordings[i];
                    if (r != null && r.VesselPersistentId == partnerPid) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Pure helper: the absorbed partner of a TARGET-side dock (our recorder's vessel
        /// survives as the merged vessel). The couple event names the partner directly
        /// (<paramref name="partnerPidFromEvent"/>); when that pid is a member of the active
        /// tree's <c>BackgroundMap</c> it is the absorbed same-tree vessel and becomes the
        /// merge's second parent. Otherwise the partner is foreign or cross-tree and the
        /// answer is 0 (single-parent merge). No shape heuristic over the map: onPartCouple
        /// fires BEFORE KSP merges the vessels, so the real partner still has all its parts,
        /// and a "vessel gone or partless" scan could only match a DIFFERENT member (an
        /// unloaded on-rails background stage reads zero parts) and dock it by mistake.
        /// </summary>
        internal static uint ResolveTargetSideAbsorbedPid(
            uint partnerPidFromEvent,
            uint mergedPid,
            ICollection<uint> backgroundMapPids)
        {
            if (partnerPidFromEvent != 0
                && partnerPidFromEvent != mergedPid
                && backgroundMapPids != null
                && backgroundMapPids.Contains(partnerPidFromEvent))
            {
                return partnerPidFromEvent;
            }
            return 0;
        }

        /// <summary>
        /// Cross-tree partner fallback for the dock-route window's
        /// <c>EndpointPartPersistentIds</c>. Tries (1) a live KSP vessel snapshot,
        /// (2) any matching recording in <c>activeTree.Recordings</c>, (3) any
        /// matching recording in <see cref="RecordingStore.CommittedRecordings"/>.
        /// Returns null when no usable snapshot can be located; callers preserve any
        /// existing list rather than overwriting it with null.
        /// </summary>
        private List<uint> TryDeriveEndpointPartPidsFromPartner(uint partnerPid)
        {
            if (partnerPid == 0) return null;

            Vessel partnerVessel = FlightRecorder.FindVesselByPid(partnerPid);
            if (partnerVessel != null)
            {
                ConfigNode partnerSnapshot = FlightRecorder.TryBackupSnapshot(partnerVessel);
                if (partnerSnapshot != null)
                {
                    List<uint> pids = VesselSnapshotOps.CollectPartPersistentIds(partnerSnapshot);
                    if (pids != null && pids.Count > 0)
                    {
                        ParsekLog.Verbose("Flight",
                            $"TryDeriveEndpointPartPidsFromPartner: live-vessel snapshot " +
                            $"partnerPid={partnerPid} parts={pids.Count}");
                        return pids;
                    }
                }
            }

            if (activeTree?.Recordings != null)
            {
                foreach (var kvp in activeTree.Recordings)
                {
                    Recording r = kvp.Value;
                    if (r != null && r.VesselPersistentId == partnerPid && r.VesselSnapshot != null)
                    {
                        List<uint> pids = VesselSnapshotOps.CollectPartPersistentIds(r.VesselSnapshot);
                        if (pids != null && pids.Count > 0)
                        {
                            ParsekLog.Verbose("Flight",
                                $"TryDeriveEndpointPartPidsFromPartner: active-tree recording " +
                                $"partnerPid={partnerPid} recId={r.RecordingId} parts={pids.Count}");
                            return pids;
                        }
                    }
                }
            }

            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    Recording r = committed[i];
                    if (r != null && r.VesselPersistentId == partnerPid && r.VesselSnapshot != null)
                    {
                        List<uint> pids = VesselSnapshotOps.CollectPartPersistentIds(r.VesselSnapshot);
                        if (pids != null && pids.Count > 0)
                        {
                            ParsekLog.Verbose("Flight",
                                $"TryDeriveEndpointPartPidsFromPartner: committed recording " +
                                $"partnerPid={partnerPid} recId={r.RecordingId} parts={pids.Count}");
                            return pids;
                        }
                    }
                }
            }

            ParsekLog.Verbose("Flight",
                $"TryDeriveEndpointPartPidsFromPartner: no snapshot available partnerPid={partnerPid}");
            return null;
        }

        /// <summary>
        /// Step 2 of <see cref="CreateMergeBranch"/>: flushes the stopped recorder's captured
        /// data into the active parent recording, stamps its Docked / Boarded terminal state,
        /// and copies the recorder's rewind save to the tree root. The rewind save lives only
        /// in CaptureAtStop once the recorder stops, and the merged child's recorder starts as
        /// a promotion without one, so a tree whose first boundary is a dock or board merge
        /// keeps its R button only through this copy (the split path copies at the same seam).
        /// Returns the parent recording, or null when the tree does not hold it.
        /// </summary>
        internal static Recording FlushStoppedRecorderIntoMergeParent(
            RecordingTree tree,
            string activeParentRecordingId,
            FlightRecorder stoppedRecorder,
            BranchPointType branchType,
            double mergeUT)
        {
            if (tree == null || string.IsNullOrEmpty(activeParentRecordingId)) return null;
            Recording activeParentRec;
            if (!tree.Recordings.TryGetValue(activeParentRecordingId, out activeParentRec))
            {
                ParsekLog.Warn("Flight",
                    $"CreateMergeBranch: active parent '{activeParentRecordingId}' not in tree " +
                    $"'{tree.Id}'; captured data not flushed");
                return null;
            }

            AppendCapturedDataToRecording(activeParentRec, stoppedRecorder?.CaptureAtStop, mergeUT);
            activeParentRec.StampTerminalState(
                (branchType == BranchPointType.Dock)
                    ? TerminalState.Docked : TerminalState.Boarded,
                "MergeBranch.activeParent");
            CopyRewindSaveToRoot(tree, stoppedRecorder, logTag: "CreateMergeBranch");
            return activeParentRec;
        }

        /// <summary>
        /// Creates a tree merge branch point for a dock or board event.
        /// Ends parent recordings, creates child recording for the merged vessel,
        /// and starts a new FlightRecorder for the child.
        /// Per Orchestrator Fix 3: absorbedVesselPid is NOT a parent-selection
        /// parameter -- background vessel PID is derived from the background parent
        /// recording. routeTargetVesselPid is only the route-proof endpoint PID.
        /// </summary>
        void CreateMergeBranch(
            BranchPointType branchType,
            uint mergedVesselPid,
            string activeParentRecordingId,      // from the active recorder (always present)
            string backgroundParentRecordingId,  // from BackgroundMap lookup (null for foreign vessel)
            double mergeUT,
            FlightRecorder stoppedRecorder,
            uint routeTargetVesselPid = 0,
            RouteConnectionKind transferKind = RouteConnectionKind.None,
            uint branchPartnerPid = 0,
            string branchPartnerGuid = null)
        {
            // 1. Build parent recording ID list
            var parentIds = new List<string>();
            parentIds.Add(activeParentRecordingId);
            if (backgroundParentRecordingId != null)
                parentIds.Add(backgroundParentRecordingId);

            // 2. Flush captured data from stopped recorder into the active parent recording
            Recording activeParentRec = FlushStoppedRecorderIntoMergeParent(
                activeTree, activeParentRecordingId, stoppedRecorder, branchType, mergeUT);

            // 3. End background parent recording (if two-parent merge)
            // Per Fix 3: derive background vessel PID from the recording's VesselPersistentId
            Recording bgParentRec = null;
            if (backgroundParentRecordingId != null)
            {
                if (activeTree.Recordings.TryGetValue(backgroundParentRecordingId, out bgParentRec))
                {
                    bgParentRec.ExplicitEndUT = mergeUT;
                    bgParentRec.StampTerminalState(
                        (branchType == BranchPointType.Dock)
                            ? TerminalState.Docked : TerminalState.Boarded,
                        "MergeBranch.backgroundParent");

                    // Remove background vessel from BackgroundMap and notify BackgroundRecorder
                    uint bgVesselPid = bgParentRec.VesselPersistentId;
                    backgroundRecorder?.OnVesselRemovedFromBackground(bgVesselPid);
                    activeTree.BackgroundMap.Remove(bgVesselPid);
                }
            }

            // 4. Get merged vessel name
            Vessel mergedVessel = FlightRecorder.FindVesselByPid(mergedVesselPid);
            string mergedVesselName = Recording.ResolveLocalizedName(mergedVessel?.vesselName) ?? "Merged Vessel";

            // 5. Build merge branch data
            var (bp, mergedChild) = BuildMergeBranchData(
                parentIds, activeTree.Id, mergeUT, branchType,
                mergedVesselPid, mergedVesselName,
                routeTargetVesselPid,
                routeTargetVesselPid != 0 ? transferKind : RouteConnectionKind.None,
                branchPartnerPid,
                branchPartnerGuid);

            // 6. Take snapshot of merged vessel
            ConfigNode mergedSnapshot = (mergedVessel != null)
                ? FlightRecorder.TryBackupSnapshot(mergedVessel) : null;
            mergedChild.GhostVisualSnapshot = mergedSnapshot;
            mergedChild.VesselSnapshot = mergedSnapshot != null ? mergedSnapshot.CreateCopy() : null;

            if (branchType == BranchPointType.Dock && routeTargetVesselPid != 0)
            {
                ConfigNode transportSnapshot =
                    stoppedRecorder?.CaptureAtStop?.VesselSnapshot ?? activeParentRec?.VesselSnapshot;
                List<uint> transportPartPids = VesselSnapshotOps.CollectPartPersistentIds(transportSnapshot);

                // Prefer the pre-couple partner snapshot captured in OnPartCouple
                // (before KSP reparented data.to.vessel to include transport parts).
                // It carries the partner's pre-dock part PID set and pre-dock
                // resources/inventory, so DOCK_ENDPOINT_RESOURCES isn't inflated by
                // the transport's tank.
                ConfigNode endpointPreCoupleSnapshot = null;
                if (pendingDockPartnerSnapshot != null
                    && pendingDockPartnerSnapshotPid == routeTargetVesselPid)
                {
                    endpointPreCoupleSnapshot = pendingDockPartnerSnapshot;
                }

                // Pre-couple transport baseline captured in OnPartCouple (before the
                // same-frame stock crossfeed equalisation that the deferred merged
                // snapshot would otherwise bake in, deflating DOCK_TRANSPORT_RESOURCES and
                // tripping the MixedPickupDelivery gate). BuildDockRouteConnectionWindow
                // self-validates it against the transport PID set and falls back to the
                // merged snapshot when absent / mismatched, so this is unconditional.
                ConfigNode transportPreCoupleSnapshot = pendingDockSelfSnapshot;

                List<uint> endpointPartPids = null;
                if (endpointPreCoupleSnapshot != null)
                {
                    endpointPartPids = VesselSnapshotOps.CollectPartPersistentIds(endpointPreCoupleSnapshot);
                }
                if ((endpointPartPids == null || endpointPartPids.Count == 0)
                    && bgParentRec?.VesselSnapshot != null)
                {
                    endpointPartPids = VesselSnapshotOps.CollectPartPersistentIds(bgParentRec.VesselSnapshot);
                }

                // Cross-tree partner fallback: when the partner has a committed
                // recording from a prior tree (or is otherwise not in
                // activeTree.BackgroundMap), bgParentRec is null and the pre-couple
                // snapshot was not captured (e.g. retroactive merge path). Derive
                // part PIDs from any known recording for the partner PID.
                if ((endpointPartPids == null || endpointPartPids.Count == 0)
                    && routeTargetVesselPid != 0)
                {
                    endpointPartPids = TryDeriveEndpointPartPidsFromPartner(routeTargetVesselPid)
                        ?? endpointPartPids;
                }

                RouteEndpoint? endpointAtDock = pendingDockRouteEndpointAtDock;
                int endpointSituation = pendingDockRouteEndpointSituation;

                if ((!endpointAtDock.HasValue || endpointSituation < 0) &&
                    bgParentRec?.VesselSnapshot != null &&
                    bgParentRec.VesselPersistentId == routeTargetVesselPid &&
                    TryBuildRouteEndpointFromSnapshot(
                        bgParentRec.VesselSnapshot,
                        routeTargetVesselPid,
                        out RouteEndpoint snapshotEndpoint,
                        out int snapshotSituation))
                {
                    endpointAtDock = snapshotEndpoint;
                    endpointSituation = snapshotSituation;
                    ParsekLog.Verbose("Flight",
                        $"Route proof endpoint restored from background snapshot targetPid={routeTargetVesselPid} " +
                        $"situation={endpointSituation}");
                }

                if (!endpointAtDock.HasValue || endpointSituation < 0)
                {
                    Vessel endpointVessel = FlightRecorder.FindVesselByPid(routeTargetVesselPid);
                    endpointAtDock = BuildRouteEndpointFromVessel(
                        endpointVessel,
                        routeTargetVesselPid);
                    endpointSituation = endpointVessel != null ? (int)endpointVessel.situation : -1;
                }

                if (!endpointAtDock.HasValue || endpointSituation < 0)
                {
                    ParsekLog.Verbose("Flight",
                        $"Route proof endpoint unavailable at dock targetPid={routeTargetVesselPid}; " +
                        "route analysis will reject this candidate");
                }

                // THE ENDPOINT'S LAUNCH-UNIQUE IDENTITY, read while it is still readable.
                // Preferred source is the PRE-COUPLE partner snapshot: after Part.Couple the
                // endpoint's own Vessel is destroyed and every id left on the merged craft is
                // craft-baked, so a root flightID taken later would be the TRANSPORT's. The
                // background parent's snapshot is the same shape and serves as the fallback;
                // an unreadable root leaves 0, which degrades the predecessor-window partner
                // match to the part-pid overlap rather than refusing it.
                uint endpointRootPartUId = 0u;
                if (endpointPreCoupleSnapshot == null
                    || !VesselSnapshotOps.TryReadRootPartFlightId(endpointPreCoupleSnapshot, out endpointRootPartUId))
                {
                    if (bgParentRec?.VesselSnapshot == null
                        || bgParentRec.VesselPersistentId != routeTargetVesselPid
                        || !VesselSnapshotOps.TryReadRootPartFlightId(bgParentRec.VesselSnapshot, out endpointRootPartUId))
                    {
                        endpointRootPartUId = 0u;
                    }
                }

                // BACKFILL THE ENDPOINT DESCRIPTOR from the same reading, when the descriptor
                // itself could not supply one. The window's endpoint IS what a delivery stop
                // persists verbatim (RouteBuilder), and its root id is what keeps the stop
                // resolving by identity while a visitor is docked to the destination
                // (ROUTE-ENDPOINT-TRANSFER-DOCKED-DOMINANT-PARTNER). The two readings have the
                // same source and the same pre-couple timing, so this cannot disagree with the
                // descriptor - it only fills a gap the descriptor's own path left (a live
                // vessel with no instantiated rootPart, or a snapshot with no readable root).
                //
                // THE POST-COUPLE FALLBACK ABOVE IS SAFE AND NOT THE REASON THIS EXISTS,
                // re-derived rather than assumed: it calls FindVesselByPid on the ENDPOINT's
                // pid AFTER the couple, so it resolves at all only when the endpoint half was
                // DOMINANT and survived as the merged vessel - in which case the merged
                // vessel's root part IS the endpoint's own. An absorbed endpoint's pid is gone
                // from FlightGlobals and that path yields no endpoint at all.
                if (endpointAtDock.HasValue
                    && endpointAtDock.Value.RootPartUId == 0u
                    && endpointRootPartUId != 0u)
                {
                    RouteEndpoint backfilled = endpointAtDock.Value;
                    backfilled.RootPartUId = endpointRootPartUId;
                    endpointAtDock = backfilled;
                    ParsekLog.Verbose("Flight",
                        "Route proof endpoint root backfilled from the pre-couple snapshot: "
                        + "targetPid=" + routeTargetVesselPid.ToString(CultureInfo.InvariantCulture)
                        + " rootPartUId=" + endpointRootPartUId.ToString(CultureInfo.InvariantCulture));
                }

                RouteConnectionWindow window = RouteProofCapture.BuildDockRouteConnectionWindow(
                    mergeUT,
                    routeTargetVesselPid,
                    transferKind,
                    mergedSnapshot,
                    transportPartPids,
                    endpointPartPids,
                    endpointAtDock,
                    endpointSituation,
                    endpointPreCoupleSnapshot,
                    transportPreCoupleSnapshot,
                    endpointRootPartUId);

                if (window != null)
                {
                    mergedChild.RouteConnectionWindows = new List<RouteConnectionWindow> { window };
                    ParsekLog.Info("Flight",
                        $"Route proof dock window captured: child={mergedChild.RecordingId} " +
                        $"window={window.WindowId} targetPid={routeTargetVesselPid} " +
                        $"kind={window.TransferKind} " +
                        $"transportParts={window.TransportPartPersistentIds?.Count ?? 0} " +
                        $"endpointParts={window.EndpointPartPersistentIds?.Count ?? 0} " +
                        $"endpointRoot={window.EndpointRootPartUId.ToString(CultureInfo.InvariantCulture)}");
                }
                else
                {
                    ParsekLog.Warn("Flight",
                        $"Route proof dock window capture skipped: child={mergedChild.RecordingId} " +
                        $"targetPid={routeTargetVesselPid} snapshot={mergedSnapshot != null} " +
                        $"transportParts={transportPartPids?.Count ?? 0} " +
                        $"endpointParts={endpointPartPids?.Count ?? 0}");
                }
            }

            // 7. Set ChildBranchPointId on all parent recordings
            if (activeParentRec != null)
                activeParentRec.ChildBranchPointId = bp.Id;
            if (backgroundParentRecordingId != null)
            {
                Recording bgParentRec2;
                if (activeTree.Recordings.TryGetValue(backgroundParentRecordingId, out bgParentRec2))
                    bgParentRec2.ChildBranchPointId = bp.Id;
            }

            // 8. Add to tree
            activeTree.BranchPoints.Add(bp);
            activeTree.AddOrReplaceRecording(mergedChild);

            // 8b. Phantom-rover fix: when this DOCK absorbed a Parsek-spawned / adopted
            // vessel that has its own committed terminal leaf (cross-tree, e.g. a landed
            // rover a logistics transport docked into), that leaf's live vessel just
            // disappeared into the merged vessel. Suppress its terminal spawn so the
            // spawn-death check + KSCSpawn don't later materialise a duplicate at the
            // runway.
            //
            // PHANTOM-SUPERSEDE-RIDES-GATED-PID: the absorbed pid is the branch point's
            // UNGATED couple-event partner stamp (bp.TargetVesselPersistentId), not the
            // route-eligibility-gated routeTargetVesselPid. Suppression is a partner-
            // identity question; a dock that absorbs a Parsek-recorded vessel but fails
            // route eligibility absorbs it just as physically. See
            // ResolveDockMergeSpawnSuppressionPid for the mechanism and the EVA carve-out.
            //
            // Marked at MERGE time (not commit time) and never reverted: the dock physically
            // merges the two vessels, which a later active-tree discard does NOT undo, so the
            // absorbed leaf must stay suppressed even if mergedChild is never committed (and
            // commit-time-only marking would re-expose the phantom on discard). A dangling
            // supersede id is harmless because ShouldSpawnAtRecordingEnd uses the field only as
            // a non-empty gate.
            uint spawnSuppressionPid = ResolveDockMergeSpawnSuppressionPid(
                branchType, bp.TargetVesselPersistentId, mergedVesselPid);
            if (spawnSuppressionPid != 0)
            {
                string absorbedLaunchGuid =
                    (pendingDockPartnerSnapshot != null
                     && pendingDockPartnerSnapshotPid == spawnSuppressionPid)
                        ? VesselLaunchIdentity.TryReadVesselGuid(pendingDockPartnerSnapshot)
                        : null;
                int superseded = RecordingStore.MarkTerminalSpawnSupersededByDockMerge(
                    spawnSuppressionPid,
                    absorbedLaunchGuid,
                    mergedVesselPid,
                    mergedChild.RecordingId,
                    "CreateMergeBranch");
                ParsekLog.Info("Flight",
                    $"Dock-merge superseded {superseded} committed terminal spawn(s): " +
                    $"absorbedPid={spawnSuppressionPid} routeTargetPid={routeTargetVesselPid} " +
                    $"mergedPid={mergedVesselPid} continuation={mergedChild.RecordingId}");
            }

            // 9. Set active recording
            activeTree.ActiveRecordingId = mergedChild.RecordingId;

            // 10. Start new FlightRecorder for merged child
            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;
            recorder.StartRecording(isPromotion: true);

            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight", "CreateMergeBranch: StartRecording failed for merged child");
                recorder = null;
            }
            else
            {
                PrepareSessionStateForRecorderStart("CreateMergeBranch");
            }

            // 11. Log
            ParsekLog.Info("Flight", $"Tree merge created: type={branchType}, " +
                $"bp={bp.Id}, parents=[{string.Join(",", parentIds)}], " +
                $"child={mergedChild.RecordingId} (pid={mergedVesselPid})");
        }

        /// <summary>
        /// Pure: the name a leg takes at its close. Only the RECORDED vessel itself may refresh
        /// it, and only on a positive identity match: the live vessel's pid equals the recorded
        /// pid AND both launch guids are known and equal. Otherwise (the vessel is gone, never
        /// resolved, or a guid is unknown or differs) the leg keeps <paramref name="existingName"/>.
        /// A name of another vessel (the stop-time active vessel) is never an input.
        /// <paramref name="source"/> is <c>recorded-vessel</c>, <c>kept-existing</c> or
        /// <c>none</c> (no live match and no existing name: the caller decides).
        /// </summary>
        internal static string ResolveRecordedVesselNameAtClose(
            string existingName,
            uint recordedPid,
            string recordedGuid,
            uint liveVesselPid,
            string liveVesselGuid,
            string liveVesselName,
            out string source)
        {
            string rg = VesselLaunchIdentity.NormalizeGuid(recordedGuid);
            string lg = VesselLaunchIdentity.NormalizeGuid(liveVesselGuid);
            bool positive = recordedPid != 0
                && liveVesselPid == recordedPid
                && rg != null && lg != null
                && string.Equals(rg, lg, StringComparison.OrdinalIgnoreCase);
            if (positive && !string.IsNullOrEmpty(liveVesselName))
            {
                source = "recorded-vessel";
                return liveVesselName;
            }
            if (!string.IsNullOrEmpty(existingName))
            {
                source = "kept-existing";
                return existingName;
            }
            source = "none";
            return null;
        }

        /// <summary>
        /// The name of a standalone fallback leg (<see cref="FallbackCommitSplitRecorder"/>):
        /// the recorded vessel's live name on a positive pid + guid match, else the name the
        /// recorded vessel had when this leg started (the start-of-leg ghost snapshot), else
        /// "Unknown Vessel". Logs the source.
        /// </summary>
        static string ResolveStandaloneFallbackLegName(FlightRecorder splitRec, Recording captured)
        {
            uint recordedPid = splitRec != null ? splitRec.RecordingVesselId : 0u;
            string startName = Recording.ResolveLocalizedName(
                captured?.GhostVisualSnapshot?.GetValue("name"));
            Vessel recordedVessel = recordedPid != 0 ? FlightRecorder.FindVesselByPid(recordedPid) : null;
            string name = ResolveRecordedVesselNameAtClose(
                startName,
                recordedPid,
                captured?.RecordedVesselGuid,
                recordedVessel != null ? recordedVessel.persistentId : 0u,
                AnchorDetector.TryReadLiveVesselGuid(recordedVessel),
                recordedVessel != null ? Recording.ResolveLocalizedName(recordedVessel.vesselName) : null,
                out string source);
            if (string.IsNullOrEmpty(name))
            {
                name = "Unknown Vessel";
                source = "unknown";
            }
            ParsekLog.Verbose("Flight",
                $"FallbackCommitSplitRecorder: leg name '{name}' source={source} pid={recordedPid} " +
                $"(stop-time active-vessel name '{captured?.VesselName ?? "(null)"}' is not used)");
            return name;
        }

        /// <summary>
        /// Fallback: if tree append fails, commit captured data directly.
        /// In always-tree mode the tree path should succeed for the active vessel;
        /// this fallback handles edge cases (no active tree, debris).
        /// </summary>
        void FallbackCommitSplitRecorder(FlightRecorder splitRec)
        {
            RecorderStateLog.RecState("FallbackCommitSplitRecorder:entry", CaptureRecorderState());
            if (splitRec?.CaptureAtStop == null) return;

            var captured = splitRec.CaptureAtStop;
            ApplyPendingSplitDestructionToCapturedRecording(
                captured,
                splitRec.VesselDestroyedDuringRecording,
                "FallbackCommitSplitRecorder.entry");

            // Bug #297: when in tree mode, append to the active tree recording
            // instead of orphaning data as a standalone recording.
            if (TryAppendCapturedToTree(activeTree, captured))
                return;

            if (TryDiscardCaptureOfDroppedTree(
                    activeTree, splitRec.ActiveTree, captured, splitRec.RecordingVesselId))
                return;

            // The leg's name comes from the RECORDED vessel, never from the stop-time
            // capture's VesselName (the ACTIVE vessel's name at stop, which after a split
            // may already be the new pod).
            string legName = ResolveStandaloneFallbackLegName(splitRec, captured);
            var rec = RecordingStore.CreateRecordingFromFlightData(
                captured.Points, legName,
                orbitSegments: captured.OrbitSegments,
                partEvents: captured.PartEvents,
                flagEvents: captured.FlagEvents);

            if (rec == null)
            {
                ParsekLog.Warn("Flight", "Split branch failed — recording too short to commit, data lost");
                return;
            }

            // Copy snapshot/vessel state and location context to the recording.
            ApplyCapturedSplitStateToStandaloneRecording(rec, captured, splitRec.RecordingVesselId);

            // Tag segment phase if untagged
            TagSegmentPhaseIfMissing(rec, FlightGlobals.ActiveVessel);

            // Destroyed vessels: discard pad failures or idle-on-pad, otherwise commit directly.
            // Both discards require pad evidence on the recording itself (this branch handles
            // a single recording): a one-sample recording has duration 0 and maxDist 0, so the
            // thresholds alone would discard an orbiting recording destroyed within one
            // sample interval.
            if (rec.VesselDestroyed)
            {
                double dur = rec.EndUT - rec.StartUT;
                double maxDist = rec.MaxDistanceFromLaunch;
                string discardReason = DecideDestroyedSplitRecordingDiscard(rec);
                if (discardReason == "pad failure")
                {
                    ParsekLog.Info("Flight",
                        $"Vessel destroyed during split — pad failure ({dur:F1}s, {maxDist:F0}m), discarding");
                    return;
                }
                if (discardReason == "idle on pad")
                {
                    ParsekLog.Info("Flight",
                        $"Vessel destroyed during split — idle on pad (maxDist={maxDist:F1}m), discarding");
                    return;
                }
                if (discardReason == null)
                {
                    // Log WHY this destroyed recording commits, once, from values the
                    // decision already produced: HasPadEvidence is the decision's own
                    // gate (no thresholds recomputed here), so evidence with a null
                    // decision means the thresholds did not match.
                    bool hasPadEvidence = HasPadEvidence(rec);
                    string commitWhy = hasPadEvidence
                        ? "outside pad thresholds - committing"
                        : "no pad evidence - committing";
                    string commitReason = hasPadEvidence
                        ? "outside-pad-thresholds"
                        : "no-pad-evidence";
                    ParsekLog.Info("Flight",
                        $"Vessel destroyed during split - {rec.RecordingId} ({dur:F1}s, {maxDist:F0}m) " +
                        $"{commitWhy} reason={commitReason}");
                }
            }

            string recId = rec.RecordingId;
            double sUT = rec.StartUT;
            double eUT = rec.EndUT;
            RecordingStore.CommitRecordingDirect(rec);
            double ledgerStartUT = LedgerOrchestrator.ResolveStandaloneCommitWindowStartUt(rec, sUT);
            int pendingBefore = GameStateRecorder.PendingScienceSubjects.Count;
            IReadOnlyList<PendingScienceSubject> pendingForCommit =
                LedgerOrchestrator.BuildPendingScienceSubsetForRecording(
                    GameStateRecorder.PendingScienceSubjects,
                    recId,
                    ledgerStartUT,
                    eUT);
            bool commitSucceeded = false;
            bool scienceAddedToLedger = false;
            try
            {
                LedgerOrchestrator.OnRecordingCommitted(
                    recId,
                    ledgerStartUT,
                    eUT,
                    pendingForCommit,
                    ref scienceAddedToLedger);
                commitSucceeded = true;
            }
            finally
            {
                LedgerOrchestrator.FinalizeScopedPendingScienceCommit(
                    "Flight",
                    "FallbackCommitSplitRecorder",
                    pendingBefore,
                    pendingForCommit,
                    commitSucceeded,
                    scienceAddedToLedger);
            }
            // #390: prune consumed events after milestone creation + ledger conversion
            GameStateStore.PruneProcessedEvents();

            ParsekLog.Info("Flight", "FallbackCommitSplitRecorder: recording committed");
        }

        /// <summary>
        /// Resumes recording after a false-alarm split detection (debris undock, joint break
        /// that didn't produce a trackable vessel, deduplication skip). Re-enables the
        /// stopped recorder and restores it as the active recorder.
        /// </summary>
        void ResumeSplitRecorder(FlightRecorder splitRec, string reason, bool keepRecordedVessel = false)
        {
            RecorderStateLog.RecState("ResumeSplitRecorder:entry", CaptureRecorderState());
            if (splitRec == null)
            {
                ParsekLog.Warn("Flight", $"ResumeSplitRecorder: no recorder to resume ({reason})");
                return;
            }

            // Don't resume on a destroyed vessel — fall back to commit
            if (splitRec.VesselDestroyedDuringRecording)
            {
                ParsekLog.Info("Flight", $"ResumeSplitRecorder: vessel was destroyed ({reason}) — committing instead of resuming");
                FallbackCommitSplitRecorder(splitRec);
                return;
            }

            splitRec.ResumeAfterFalseAlarm(keepRecordedVessel);

            if (splitRec.IsRecording)
            {
                recorder = splitRec;
                // Restore tree mode if tree is active
                if (activeTree != null)
                    recorder.ActiveTree = activeTree;
                ParsekLog.Info("Flight", $"Recording resumed after false alarm: {reason}");
            }
            else
            {
                // ResumeAfterFalseAlarm failed (no active vessel?) — fall back to commit
                ParsekLog.Warn("Flight", $"ResumeSplitRecorder: resume failed ({reason}) — falling back to commit");
                FallbackCommitSplitRecorder(splitRec);
            }
        }

        /// <summary>
        /// Checks the deduplication guard. Returns true if this branch should proceed.
        /// </summary>
        bool CheckBranchDeduplication(double branchUT, uint newVesselPid)
        {
            if (Math.Abs(branchUT - lastBranchUT) > 0.1)
            {
                lastBranchVesselPids.Clear();
                lastBranchUT = branchUT;
            }
            if (lastBranchVesselPids.Contains(newVesselPid))
            {
                ParsekLog.Verbose("Flight", $"Skipping duplicate branch for pid={newVesselPid} at same UT");
                return false;
            }
            lastBranchVesselPids.Add(newVesselPid);
            return true;
        }

        IEnumerator DeferredUndockBranch(
            uint oldVesselPid,
            uint newVesselPid,
            TrajectoryPoint? backgroundInitialTrajectoryPoint = null)
        {
            yield return null; // defer one frame for KSP to finalize the split

            try
            {
                if (FlightGlobals.ActiveVessel == null || pendingSplitRecorder == null)
                {
                    ParsekLog.Warn("Flight", "DeferredUndockBranch: invalid state after deferral — aborting");
                    FallbackCommitSplitRecorder(pendingSplitRecorder);
                    yield break;
                }

                double branchUT = Planetarium.GetUniversalTime();

                // Resolve which side KSP gave focus to AFTER the one-frame yield. KSP keeps
                // focus on oldVessel for a normal undock, but if the player kept controlling
                // the pod that departed into the new vessel, KSP moves focus to it one frame
                // later (Vessel.WaitAndSwitchFocus). Mirror DeferredEvaBranch: the recorder
                // follows the focused side (active child), the OTHER side is backgrounded.
                Vessel activeVessel = FlightGlobals.ActiveVessel;
                uint activePid = activeVessel.persistentId;
                uint backgroundPid = SegmentBoundaryLogic.ResolveUndockBackgroundPid(
                    activePid, oldVesselPid, newVesselPid);

                if (activePid != oldVesselPid && activePid != newVesselPid)
                {
                    ParsekLog.Verbose("Flight",
                        $"DeferredUndockBranch: active vessel pid={activePid} is neither undock side " +
                        $"(old={oldVesselPid}, new={newVesselPid}); defaulting to background new, active stays current");
                }
                else
                {
                    ParsekLog.Verbose("Flight",
                        $"DeferredUndockBranch: active vessel pid={activePid} (old={oldVesselPid}, " +
                        $"new={newVesselPid}); backgrounding pid={backgroundPid}");
                }

                // Deduplication check keys on the backgrounded vessel (the new tree child).
                if (!CheckBranchDeduplication(branchUT, backgroundPid))
                {
                    ResumeSplitRecorder(pendingSplitRecorder, "undock dedup skip");
                    yield break;
                }

                // Find the vessel that goes to background.
                Vessel backgroundVessel = FlightRecorder.FindVesselByPid(backgroundPid);
                if (backgroundVessel == null)
                {
                    ParsekLog.Verbose("Flight", $"DeferredUndockBranch: vessel pid={backgroundPid} not found, debris destroyed, resuming recording");
                    ResumeSplitRecorder(pendingSplitRecorder, "undocked vessel not found");
                    yield break;
                }

                // Debris filter: a crewed vessel with no command module still gets its own
                // branch (ShouldRecordAsOwnBranch); only crewless commandless halves are debris.
                if (!ShouldRecordAsOwnBranch(backgroundVessel))
                {
                    ParsekLog.Verbose("Flight",
                        $"DeferredUndockBranch: vessel pid={backgroundPid} is not trackable (debris), resuming recording " +
                        $"capturedSeed={backgroundInitialTrajectoryPoint.HasValue}");
                    ResumeSplitRecorder(pendingSplitRecorder, "undocked vessel is debris");
                    yield break;
                }

                // Recorder follows the focused vessel (active child); the other goes to background.
                CreateSplitBranch(
                    BranchPointType.Undock,
                    activeVessel,
                    backgroundVessel,
                    branchUT,
                    backgroundInitialTrajectoryPoint: backgroundInitialTrajectoryPoint);
            }
            finally
            {
                pendingSplitInProgress = false;
                pendingSplitRecorder = null;
            }
        }

        IEnumerator DeferredEvaBranch(string kerbalName, GameEvents.FromToAction<Part, Part> evaData)
        {
            yield return null; // defer one frame for vessel switch to complete

            try
            {
                if (FlightGlobals.ActiveVessel == null || pendingSplitRecorder == null)
                {
                    ParsekLog.Warn("Flight", "DeferredEvaBranch: invalid state after deferral — aborting");
                    FallbackCommitSplitRecorder(pendingSplitRecorder);
                    yield break;
                }

                double branchUT = Planetarium.GetUniversalTime();

                // Get the EVA kerbal vessel and the ship vessel
                Vessel evaVessel = evaData.to?.vessel;
                Vessel shipVessel = evaData.from?.vessel;

                if (evaVessel == null)
                {
                    ParsekLog.Warn("Flight", "DeferredEvaBranch: EVA vessel is null — aborting");
                    FallbackCommitSplitRecorder(pendingSplitRecorder);
                    yield break;
                }

                uint evaPid = evaVessel.persistentId;
                if (!CheckBranchDeduplication(branchUT, evaPid))
                {
                    ResumeSplitRecorder(pendingSplitRecorder, "EVA dedup skip");
                    yield break;
                }

                // Determine which vessel is active by checking FlightGlobals.ActiveVessel
                Vessel currentActive = FlightGlobals.ActiveVessel;
                Vessel activeChild;
                Vessel backgroundChild;

                if (currentActive != null && currentActive.persistentId == evaPid)
                {
                    // KSP switched to the EVA kerbal (expected)
                    activeChild = evaVessel;
                    backgroundChild = shipVessel;
                    ParsekLog.Verbose("Flight", $"DeferredEvaBranch: EVA kerbal is active (expected path), evaPid={evaPid}");
                }
                else if (currentActive != null && shipVessel != null &&
                         currentActive.persistentId == shipVessel.persistentId)
                {
                    // KSP hasn't switched yet — ship is still active
                    activeChild = shipVessel;
                    backgroundChild = evaVessel;
                    // Note: tree vessel switch handling will sort out promotion when KSP switches
                    ParsekLog.Verbose("Flight", $"DeferredEvaBranch: ship still active (KSP hasn't switched), shipPid={shipVessel.persistentId}");
                }
                else
                {
                    // Ambiguous — default to EVA kerbal as active (most common case)
                    activeChild = evaVessel;
                    backgroundChild = shipVessel;
                    ParsekLog.Verbose("Flight", $"DeferredEvaBranch: ambiguous active vessel, defaulting to EVA kerbal, activePid={currentActive?.persistentId}");
                }

                CreateSplitBranch(BranchPointType.EVA, activeChild, backgroundChild, branchUT, kerbalName, evaPid);
            }
            finally
            {
                pendingSplitInProgress = false;
                pendingSplitRecorder = null;
            }
        }

        IEnumerator DeferredEvaBranchFromBackgroundParent(
            string parentRecordingId,
            uint sourceVesselPid,
            string kerbalName,
            GameEvents.FromToAction<Part, Part> evaData)
        {
            yield return null; // defer one frame for vessel switch to complete

            try
            {
                if (activeTree == null)
                {
                    ParsekLog.Warn("Flight", "DeferredEvaBranchFromBackgroundParent: no active tree after deferral - aborting");
                    yield break;
                }

                if (string.IsNullOrEmpty(parentRecordingId)
                    || !activeTree.Recordings.ContainsKey(parentRecordingId))
                {
                    ParsekLog.Warn("Flight",
                        $"DeferredEvaBranchFromBackgroundParent: parent recording '{parentRecordingId ?? "null"}' " +
                        "is missing after deferral - aborting");
                    yield break;
                }

                double branchUT = Planetarium.GetUniversalTime();
                Vessel evaVessel = evaData.to?.vessel;
                Vessel shipVessel = evaData.from?.vessel;

                if (evaVessel == null || shipVessel == null)
                {
                    ParsekLog.Warn("Flight",
                        $"DeferredEvaBranchFromBackgroundParent: EVA data missing vessel " +
                        $"(eva={(evaVessel != null ? evaVessel.persistentId.ToString(CultureInfo.InvariantCulture) : "null")}, " +
                        $"ship={(shipVessel != null ? shipVessel.persistentId.ToString(CultureInfo.InvariantCulture) : "null")}) - aborting");
                    yield break;
                }

                uint evaPid = evaVessel.persistentId;
                if (!CheckBranchDeduplication(branchUT, evaPid))
                {
                    ParsekLog.Verbose("Flight",
                        $"DeferredEvaBranchFromBackgroundParent: skipping duplicate EVA branch for pid={evaPid}");
                    yield break;
                }

                Vessel currentActive = FlightGlobals.ActiveVessel;
                Vessel activeChild;
                Vessel backgroundChild;
                uint currentActivePid = currentActive?.persistentId ?? 0u;
                switch (DecideEvaBackgroundParentFocus(currentActivePid, evaPid, sourceVesselPid))
                {
                    case EvaBackgroundParentFocusDecision.EvaKerbalActive:
                        activeChild = evaVessel;
                        backgroundChild = shipVessel;
                        ParsekLog.Verbose("Flight",
                            $"DeferredEvaBranchFromBackgroundParent: EVA kerbal is active, evaPid={evaPid}");
                        break;

                    case EvaBackgroundParentFocusDecision.SourceVesselActive:
                        activeChild = shipVessel;
                        backgroundChild = evaVessel;
                        ParsekLog.Verbose("Flight",
                            $"DeferredEvaBranchFromBackgroundParent: source vessel is still active, sourcePid={sourceVesselPid}");
                        break;

                    default:
                        ParsekLog.Warn("Flight",
                            $"DeferredEvaBranchFromBackgroundParent: refusing to branch because active vessel is neither " +
                            $"EVA nor source (flightActivePid={currentActivePid}, evaPid={evaPid}, sourcePid={sourceVesselPid})");
                        yield break;
                }

                CreateSplitBranchFromBackgroundParent(
                    parentRecordingId,
                    sourceVesselPid,
                    activeChild,
                    backgroundChild,
                    branchUT,
                    kerbalName,
                    evaPid,
                    branchPath: "background-parent");
            }
            finally
            {
                pendingSplitInProgress = false;
            }
        }

        IEnumerator DeferredJointBreakCheck()
        {
            yield return null; // defer one frame for vessel split to finalize

            try
            {
                if (FlightGlobals.ActiveVessel == null)
                {
                    ParsekLog.Verbose("Flight", "DeferredJointBreakCheck: no active vessel after deferral");
                    FallbackCommitSplitRecorder(pendingSplitRecorder);
                    yield break;
                }

                // If there's no pendingSplitRecorder, the recorder was re-established already
                if (pendingSplitRecorder == null)
                {
                    ParsekLog.Verbose("Flight", "DeferredJointBreakCheck: no pending split recorder");
                    yield break;
                }

                double currentUT = Planetarium.GetUniversalTime();

                // Scan for NEW vessels created by the joint break.
                // Only consider vessels whose PID was NOT in the pre-break snapshot,
                // to avoid false matches with unrelated vessels (e.g., space stations).
                uint recordedPid = pendingSplitRecorder.RecordingVesselId;

                // Collect new vessel PIDs for classification, tracking per-vessel controller status
                var newVesselPids = new List<uint>();
                var newVesselHasController = new Dictionary<uint, bool>();
                bool anyNewVesselHasController = false;

                // First, include vessels caught by onPartDeCoupleNewVesselComplete during recording.
                // Iterate the PID-keyed decoupleControllerStatus dict rather than the
                // decoupleCreatedVessels List<Vessel>, because at terminal crash time
                // KSP has already destroyed those GameObjects and Unity's overloaded ==
                // makes every element compare equal to null — the old path filtered
                // out every fragment and classified the event as WithinSegment (#362).
                if (decoupleControllerStatus != null && decoupleControllerStatus.Count > 0)
                {
                    ParsekLog.Info("Flight",
                        $"DeferredJointBreakCheck: {decoupleControllerStatus.Count} vessel(s) caught by onPartDeCouple");

                    SegmentBoundaryLogic.CollectSynchronouslyCapturedNewVesselPids(
                        recordedPid,
                        decoupleControllerStatus,
                        activeTree?.BackgroundMap,
                        newVesselPids,
                        newVesselHasController,
                        out bool anyNewFromDecouple);

                    if (anyNewFromDecouple)
                        anyNewVesselHasController = true;

                    // Log per-fragment when the live Vessel reference is gone — the fragment
                    // was captured synchronously but terminal destruction already wiped the
                    // GameObject. Classification continues on PID-only data for #362.
                    foreach (var kvp in decoupleControllerStatus)
                    {
                        uint pid = kvp.Key;
                        if (pid == recordedPid) continue;
                        if (activeTree != null && activeTree.BackgroundMap.ContainsKey(pid))
                            continue;

                        Vessel live = FlightRecorder.FindVesselByPid(pid);
                        if (live == null)
                        {
                            ParsekLog.Verbose("Flight",
                                $"DeferredJointBreakCheck: pid={pid} captured synchronously but Vessel reference " +
                                $"is destroyed at deferred-check frame — using PID-only classification (#362)");
                        }
                    }
                }

                // Only joint-break-triggered checks have a meaningful pre-break vessel snapshot.
                // For decouple-only fallback, the synchronously-captured decoupleCreatedVessels list
                // is the authoritative source of new vessels. Scanning all live vessels here would
                // misclassify unrelated or stale debris as part of the current split.
                int foreignOriginSkipped = 0;
                if (pendingDeferredSplitCheckTrigger == DeferredSplitCheckTrigger.JointBreak
                    && FlightGlobals.Vessels != null)
                {
                    for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
                    {
                        Vessel v = FlightGlobals.Vessels[i];
                        if (v == null || v.persistentId == recordedPid) continue;
                        if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;

                        // Only consider vessels that didn't exist before the break
                        if (preBreakVesselPids != null && preBreakVesselPids.Contains(v.persistentId))
                            continue;

                        // Skip vessels we're already tracking in the tree
                        if (activeTree != null && activeTree.BackgroundMap.ContainsKey(v.persistentId))
                            continue;

                        // Skip if already captured by onVesselCreate
                        if (newVesselPids.Contains(v.persistentId))
                            continue;

                        // Skip a fragment that came off ANOTHER vessel in this window (e.g. a
                        // spawned copy breaking on its first unpacked frame): it is not a
                        // child of the recording.
                        if (SegmentBoundaryLogic.IsForeignDecoupleOrigin(
                                v.persistentId, recordedPid, decoupleOriginByNewPid))
                        {
                            foreignOriginSkipped++;
                            continue;
                        }

                        if (!ShouldIncludeVesselInDeferredBreakupScan(v, out string rejectReason))
                        {
                            ParsekLog.Verbose("Flight",
                                $"DeferredJointBreakCheck: ignoring unrelated new vessel pid={v.persistentId} " +
                                $"name='{v.vesselName ?? "<unnamed>"}' type={v.vesselType} reason={rejectReason}");
                            continue;
                        }

                        newVesselPids.Add(v.persistentId);

                        bool hasController = ShouldRecordAsOwnBranch(v);
                        newVesselHasController[v.persistentId] = hasController;
                        if (hasController)
                        {
                            anyNewVesselHasController = true;
                        }
                    }
                }
                if (foreignOriginSkipped > 0)
                {
                    ParsekLog.Info("Flight",
                        $"DeferredJointBreakCheck: skipped {foreignOriginSkipped} new vessel(s) " +
                        $"that came off another vessel (decouple origin chain never reaches " +
                        $"recordedPid={recordedPid})");
                }

                string splitCheckSource =
                    pendingDeferredSplitCheckTrigger == DeferredSplitCheckTrigger.DecoupleCreatedVessel
                        ? "decouple-only"
                        : "joint-break";

                // Classify the split result
                var classification = SegmentBoundaryLogic.ClassifyJointBreakResult(
                    recordedPid, newVesselPids, anyNewVesselHasController);

                double branchUT = ResolveDeferredSplitBranchUT(
                    currentUT,
                    pendingSplitTriggerUT,
                    newVesselPids,
                    decoupleCreatedTrajectoryPoints);

                ParsekLog.Info("Coalescer",
                    $"Deferred split classified: source={splitCheckSource}, result={classification}, " +
                    $"newVessels={newVesselPids.Count}, hasController={anyNewVesselHasController}, " +
                    $"recordedPid={recordedPid}");

                if (classification == JointBreakResult.WithinSegment)
                {
                    // No vessel split occurred — emit segment events and resume recording.
                    // The Decoupled PartEvent was already recorded by FlightRecorder.OnPartJointBreak.
                    // Now emit a SegmentEvent to mark this as within-segment breakage (not a DAG branch).
                    ParsekLog.Info("Coalescer",
                        "Within-segment breakage — emitting SegmentEvents and resuming recording");

                    // Emit SegmentEvent.PartDestroyed for parts that broke off but didn't split
                    // the vessel. Scan recent Decoupled PartEvents to find the broken parts.
                    if (pendingSplitRecorder != null)
                    {
                        var recentDecoupled = pendingSplitRecorder.PartEvents;
                        for (int i = recentDecoupled.Count - 1; i >= 0 && i >= recentDecoupled.Count - 5; i--)
                        {
                            if (recentDecoupled[i].eventType == PartEventType.Decoupled)
                            {
                                SegmentBoundaryLogic.EmitBreakageSegmentEvents(
                                    pendingSplitRecorder.SegmentEvents,
                                    branchUT,
                                    recentDecoupled[i].partName,
                                    recentDecoupled[i].partPersistentId,
                                    false, // wasController — full controller monitoring is Phase 1 low-priority
                                    null);
                            }
                        }
                    }

                    ResumeSplitRecorder(pendingSplitRecorder, "joint break was within-segment (no vessel split)");
                }
                else if (classification == JointBreakResult.StructuralSplit ||
                         classification == JointBreakResult.DebrisSplit)
                {
                    // Vessel physically split — feed ALL new vessels to crash coalescer.
                    // A single joint break can produce multiple new vessels (e.g., 3-way split),
                    // and each needs to be counted individually.
                    ParsekLog.Info("Coalescer",
                        $"Feeding {newVesselPids.Count} new vessel(s) to coalescer: classification={classification}");

                    foreach (var newPid in newVesselPids)
                    {
                        bool hasController = newVesselHasController.ContainsKey(newPid) && newVesselHasController[newPid];

                        // Pre-capture snapshot while the vessel is still alive (#157).
                        // By coalescer emission time (0.5s later), debris may be destroyed.
                        ConfigNode preSnapshot = null;
                        TrajectoryPoint? preTrajectoryPoint = null;
                        string trajectoryPointSource = "none";
                        Vessel childVessel = FlightRecorder.FindVesselByPid(newPid);
                        if (childVessel != null)
                            preSnapshot = FlightRecorder.TryBackupSnapshot(childVessel);
                        TrajectoryPoint capturedPoint = default;
                        bool hasCapturedPoint = decoupleCreatedTrajectoryPoints != null
                            && decoupleCreatedTrajectoryPoints.TryGetValue(newPid, out capturedPoint);
                        if (hasCapturedPoint)
                        {
                            preTrajectoryPoint = capturedPoint;
                            trajectoryPointSource = "decouple-callback";
                        }
                        else if (childVessel != null)
                        {
                            preTrajectoryPoint = BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                                childVessel, currentUT, preferRootPartSurfacePose: true);
                            trajectoryPointSource = "deferred-live-sample";
                        }
                        else if (childVessel == null)
                        {
                            trajectoryPointSource = "destroyed-before-capture";
                        }

                        // Classify decoupler-initiated (intentional staging / decoupler fire)
                        // vs force-driven structural break. A child caught by
                        // onPartDeCoupleNewVesselComplete (present in decoupleControllerStatus)
                        // came off through Part.decouple(); a decouple-only trigger means the
                        // whole split was detected via that callback. Everything else stays a
                        // breakup so a genuine crash is never relabelled a decouple.
                        bool childWasDecoupleCreated = decoupleControllerStatus != null
                            && decoupleControllerStatus.ContainsKey(newPid);
                        bool triggerWasDecoupleOnly =
                            pendingDeferredSplitCheckTrigger == DeferredSplitCheckTrigger.DecoupleCreatedVessel;
                        uint childRootPartPid = childVessel?.rootPart?.persistentId ?? 0u;
                        string childSplitCause = SegmentBoundaryLogic.ClassifyForegroundSplitChildCause(
                            childWasDecoupleCreated, triggerWasDecoupleOnly,
                            childBrokeUnderForce: pendingSplitRecorder != null
                                && pendingSplitRecorder.JointChildBrokeUnderForce(childRootPartPid),
                            childWasPreAttachedUndock: pendingSplitRecorder != null
                                && pendingSplitRecorder.PartLeftThroughPreAttachedUndock(childRootPartPid),
                            childRootPartPid: childRootPartPid);
                        // Best-available proxy for BranchPoint.DecouplerPartId: the separated
                        // child's root part (the part that came off through the decoupler). KSP's
                        // decouple callback does not hand us the parent-side decoupler module, and
                        // this field has no behavioral consumer (serialized / logged only), so the
                        // child root PID is informative without being load-bearing. 0 when unknown,
                        // matching the background-recorder decouple path.
                        uint childDecouplerPartId = childWasDecoupleCreated
                            ? (childVessel?.rootPart?.persistentId ?? 0u)
                            : 0u;

                        ParsekLog.Info("Coalescer",
                            $"Feeding split to coalescer: childPid={newPid}, childHasController={hasController}" +
                            $", cause={childSplitCause}, decoupleCreated={childWasDecoupleCreated}" +
                            $", triggerDecoupleOnly={triggerWasDecoupleOnly}, decouplerPartId={childDecouplerPartId}" +
                            $", preSnapshot={preSnapshot != null}, preTrajectoryPoint={preTrajectoryPoint.HasValue} " +
                            $"pointSource={trajectoryPointSource} " +
                            $"branchUT={branchUT.ToString("F2", CultureInfo.InvariantCulture)}" +
                            (preTrajectoryPoint.HasValue
                                ? $" seedUT={preTrajectoryPoint.Value.ut.ToString("F2", CultureInfo.InvariantCulture)}"
                                : string.Empty));
                        crashCoalescer.OnSplitEvent(
                            branchUT, newPid, hasController,
                            splitCause: childSplitCause,
                            preSnapshot: preSnapshot,
                            preTrajectoryPoint: preTrajectoryPoint,
                            decouplerPartId: childDecouplerPartId);
                        pendingSplitRecorder?.RegisterHighFidelityProximityVessel(
                            newPid,
                            branchUT,
                            "split-child-proximity");
                    }

                    // Stock may already have moved focus to a vessel that came off (the
                    // control part left on it: ModuleDecouple.WaitAndSwitchFocus runs before
                    // this deferred check). The recording still belongs to the vessel it was
                    // recording, so resume on that vessel, not on the active one.
                    Vessel activeAfterSplit = FlightGlobals.ActiveVessel;
                    uint activePidAfterSplit = activeAfterSplit != null ? activeAfterSplit.persistentId : 0u;
                    bool focusLeftRecordedVessel = ShouldResumeSplitOnRecordedVessel(
                        recordedPid,
                        recordedVesselAlive: FlightRecorder.FindVesselByPid(recordedPid) != null,
                        activePidAfterSplit);
                    if (focusLeftRecordedVessel)
                    {
                        ParsekLog.Info("Flight",
                            $"DeferredJointBreakCheck: focus moved off the recorded vessel during the split " +
                            $"(recordedPid={recordedPid}, activePid={activePidAfterSplit}, " +
                            $"activeIsSplitChild={newVesselPids.Contains(activePidAfterSplit)}) - " +
                            $"resuming on the recorded vessel");
                    }

                    // Resume the recorder — the coalescer's Tick will emit the BREAKUP
                    // branch point after the coalescing window expires. The recorder needs
                    // to keep running to capture continued trajectory data.
                    ResumeSplitRecorder(pendingSplitRecorder,
                        $"joint break fed to coalescer (classification={classification})",
                        keepRecordedVessel: focusLeftRecordedVessel);

                    // Bug #263 fallback: ensure a Decoupled event exists for every new
                    // vessel's root part. When a symmetry group of radial decouplers fires,
                    // individual onPartJointBreak events race with KSP's vessel-split
                    // processing and some can be dropped mid-pipeline, leaving decoupled
                    // subtrees visible on the ghost during playback. Scanning new vessel
                    // roots post-split is deterministic — every new debris vessel has
                    // exactly one root part, and that root is the part that separated
                    // from the recording vessel. Must run AFTER ResumeSplitRecorder so
                    // the events survive ResumeAfterFalseAlarm's terminal-event trim.
                    if (recorder != null && recorder.IsRecording)
                    {
                        int fallbackAdded = EmitFallbackDecoupleEventsForNewVessels(
                            recorder, newVesselPids, branchUT);
                        if (fallbackAdded > 0)
                        {
                            ParsekLog.Info("Flight",
                                $"DeferredJointBreakCheck: added {fallbackAdded} fallback " +
                                $"Decoupled event(s) for new vessel roots at " +
                                $"UT={branchUT.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
                        }
                    }

                    // The recorder resumed on a vessel that is no longer the active one: run
                    // the ordinary vessel-switch hand-off now (the stock switch already fired
                    // while the recorder was stopped, so nothing else would). It emits the
                    // pending split first, while this recording is still the active one, so
                    // the new vessel gets its own child recording; then the recorded vessel
                    // goes to the background recorder and the active vessel is handled as a
                    // switch to a tracked background member.
                    if (focusLeftRecordedVessel
                        && recorder != null && recorder.IsRecording
                        && recorder.RecordingVesselId == recordedPid
                        && FlightGlobals.ActiveVessel != null
                        && FlightGlobals.ActiveVessel.persistentId != recordedPid)
                    {
                        ParsekLog.Info("Flight",
                            $"DeferredJointBreakCheck: handing focus over after split " +
                            $"(recordedPid={recordedPid}, activePid={FlightGlobals.ActiveVessel.persistentId})");
                        OnVesselSwitchComplete(FlightGlobals.ActiveVessel);
                    }
                }
            }
            finally
            {
                pendingSplitInProgress = false;
                pendingSplitRecorder = null;
                pendingDeferredSplitCheckTrigger = DeferredSplitCheckTrigger.None;
                pendingSplitTriggerUT = double.NaN;
                preBreakVesselPids = null;
                // Clear consumed decouple vessels (list stays alive for session, just emptied)
                decoupleCreatedVessels?.Clear();
                decoupleControllerStatus?.Clear();
                decoupleCreatedTrajectoryPoints?.Clear();
                decoupleOriginByNewPid.Clear();
            }
        }

        /// <summary>
        /// Bug #263 safety net: for each new vessel created by a joint break, emits a fallback
        /// <c>Decoupled</c> PartEvent on the recorder for the vessel's root part (if not already
        /// decoupled). This catches decouple events that raced past the recorder's joint break
        /// handler when a symmetry group of decouplers fires.
        ///
        /// Resolves the new vessel from live <see cref="FlightGlobals.Vessels"/> first, falling
        /// back to <see cref="decoupleCreatedVessels"/> (captured synchronously during the split)
        /// for debris that KSP may have already cleaned up by the deferred-check frame.
        /// </summary>
        /// <param name="rec">The (resumed) recorder to append events to.</param>
        /// <param name="newVesselPids">Vessel PIDs that appeared as a result of the joint break.</param>
        /// <param name="branchUT">Universal time to stamp on the fallback events.</param>
        /// <returns>Number of new events added (0 if all pids were already in the recorder's decoupled set).</returns>
        internal int EmitFallbackDecoupleEventsForNewVessels(
            FlightRecorder rec, List<uint> newVesselPids, double branchUT)
        {
            if (rec == null || newVesselPids == null || newVesselPids.Count == 0)
                return 0;

            int added = 0;
            for (int i = 0; i < newVesselPids.Count; i++)
            {
                uint newPid = newVesselPids[i];

                // Resolve the new vessel. Prefer live FlightGlobals lookup; fall back to
                // decoupleCreatedVessels captured synchronously during the split event.
                Vessel newVessel = FlightRecorder.FindVesselByPid(newPid);
                if (newVessel == null && decoupleCreatedVessels != null)
                {
                    for (int j = 0; j < decoupleCreatedVessels.Count; j++)
                    {
                        Vessel dv = decoupleCreatedVessels[j];
                        if (dv != null && dv.persistentId == newPid)
                        {
                            newVessel = dv;
                            break;
                        }
                    }
                }

                if (newVessel == null)
                {
                    ParsekLog.Verbose("Flight",
                        $"EmitFallbackDecoupleEventsForNewVessels: vessel pid={newPid} not found, skipping");
                    continue;
                }

                Part rootPart = newVessel.rootPart;
                if (rootPart == null)
                {
                    ParsekLog.Verbose("Flight",
                        $"EmitFallbackDecoupleEventsForNewVessels: vessel pid={newPid} has no rootPart, skipping");
                    continue;
                }

                added += rec.RecordFallbackDecoupleEvent(
                    rootPart.persistentId,
                    rootPart.partInfo?.name,
                    branchUT);
            }

            return added;
        }

        /// <summary>
        /// Called each frame from Update() to check the crash coalescer for pending breakup events.
        /// When the coalescing window expires, emits a BREAKUP branch point on the active tree.
        /// </summary>
        void TickCrashCoalescer()
        {
            if (crashCoalescer == null || !crashCoalescer.HasPendingBreakup)
                return;

            double currentUT = Planetarium.GetUniversalTime();
            var breakupBp = crashCoalescer.Tick(currentUT);
            if (breakupBp == null)
                return;

            ParsekLog.Info("Coalescer",
                $"Coalescer emitted split branch point: ut={breakupBp.UT:F2}, " +
                $"type={breakupBp.Type}, cause={breakupBp.BreakupCause ?? breakupBp.SplitCause ?? "?"}, " +
                $"debris={breakupBp.DebrisCount}, duration={breakupBp.BreakupDuration:F3}s");

            ProcessBreakupEvent(breakupBp);
        }

        /// <summary>
        /// Emits a split the crash coalescer is still holding, before the active recording
        /// stops being active (its vessel goes to the background recorder on a focus
        /// change). <see cref="ProcessBreakupEvent"/> attaches the branch point to
        /// <c>activeTree.ActiveRecordingId</c>, which a focus change clears, so a split
        /// emitted after the hand-off would be dropped and the new vessel would get no
        /// child recording. Emitting it early only shortens the coalescing window.
        /// </summary>
        void EmitPendingSplitBeforeFocusLeaves(string reason)
        {
            if (crashCoalescer == null || !crashCoalescer.HasPendingBreakup)
                return;
            if (activeTree == null || string.IsNullOrEmpty(activeTree.ActiveRecordingId))
                return;

            double windowStartUT = crashCoalescer.WindowStartUT;
            var breakupBp = crashCoalescer.EmitNow();
            if (breakupBp == null)
                return;

            ParsekLog.Info("Coalescer",
                $"Coalescer emitted split branch point early (focus leaving the active recording, {reason}): " +
                $"ut={breakupBp.UT.ToString("F2", CultureInfo.InvariantCulture)}, type={breakupBp.Type}, " +
                $"cause={breakupBp.BreakupCause ?? breakupBp.SplitCause ?? "?"}, debris={breakupBp.DebrisCount}, " +
                $"windowAgeSeconds={(Planetarium.GetUniversalTime() - windowStartUT).ToString("F2", CultureInfo.InvariantCulture)}");

            ProcessBreakupEvent(breakupBp);
        }

        /// <summary>
        /// Creates a child recording for a breakup branch point. Handles snapshot capture,
        /// terminal state marking for destroyed vessels, and tree wiring.
        /// Caller is responsible for BackgroundMap registration and logging.
        /// The child inherits Generation = parentGeneration + 1, where parentGeneration
        /// is the breakup parent's recording generation (the active recording or rootRec).
        /// </summary>
        internal static void SeedBreakupChildSnapshots(
            Recording childRec,
            uint pid,
            ConfigNode liveSnapshot,
            ConfigNode preCapturedSnapshot)
        {
            if (childRec == null) return;

            // Prefer the pre-captured split-time snapshot for ghost visuals and start
            // manifests so fast-mutating debris does not spawn from its terminal wreck
            // state on playback. Keep the live snapshot for vessel-spawn state when
            // the child is still alive.
            childRec.GhostVisualSnapshot = preCapturedSnapshot != null
                ? preCapturedSnapshot.CreateCopy()
                : (liveSnapshot != null ? liveSnapshot.CreateCopy() : null);
            childRec.VesselSnapshot = liveSnapshot != null
                ? liveSnapshot.CreateCopy()
                : (preCapturedSnapshot != null ? preCapturedSnapshot.CreateCopy() : null);

            ConfigNode manifestSnapshot = childRec.GhostVisualSnapshot ?? childRec.VesselSnapshot;
            childRec.StartResources = VesselSnapshotOps.ExtractResourceManifest(manifestSnapshot);
            int childInvSlots;
            childRec.StartInventory = VesselSnapshotOps.ExtractInventoryManifest(manifestSnapshot, out childInvSlots);
            childRec.StartInventorySlots = childInvSlots;
            childRec.StartCrew = VesselSpawner.ExtractCrewManifest(manifestSnapshot);

            string snapshotSource = preCapturedSnapshot != null
                ? (liveSnapshot != null ? "pre-captured-ghost + live-vessel" : "pre-captured")
                : (liveSnapshot != null ? "live" : "none");
            ParsekLog.Info("Coalescer",
                $"CreateBreakupChildRecording: pid={pid} snapshotSource={snapshotSource} " +
                $"hasGhostSnapshot={childRec.GhostVisualSnapshot != null} " +
                $"hasVesselSnapshot={childRec.VesselSnapshot != null}");
            ParsekLog.Verbose("Coalescer",
                $"CreateBreakupChildRecording: captured {childRec.StartResources?.Count ?? 0} start resource type(s) for pid={pid}");
            ParsekLog.Verbose("Coalescer",
                $"CreateBreakupChildRecording: captured {childRec.StartInventory?.Count ?? 0} start inventory item type(s) for pid={pid}");
            ParsekLog.Verbose("Coalescer",
                $"CreateBreakupChildRecording: captured {childRec.StartCrew?.Count ?? 0} start crew trait(s) for pid={pid}");
        }

        internal static Recording CreateBreakupChildRecording(
            RecordingTree tree, BranchPoint breakupBp,
            uint pid, Vessel vessel, bool isDebris, string fallbackName,
            ConfigNode fallbackSnapshot = null,
            TrajectoryPoint? fallbackTrajectoryPoint = null,
            int parentGeneration = 0,
            string parentRecordingId = null)
        {
            string childRecId = Guid.NewGuid().ToString("N");
            string vesselName = Recording.ResolveLocalizedName(vessel?.vesselName) ?? fallbackName;

            var childRec = new Recording
            {
                RecordingId = childRecId,
                TreeId = tree.Id,
                VesselPersistentId = pid,
                VesselName = vesselName,
                ParentBranchPointId = breakupBp.Id,
                ExplicitStartUT = breakupBp.UT,
                IsDebris = isDebris,
                Generation = parentGeneration + 1,
                // Pin start-of-recording controller identity from the live breakup-moment
                // vessel. Null when the vessel was destroyed during the coalescing
                // window (no controllable identity to lose anyway — the breakup child
                // already carries TerminalStateValue=Destroyed in that path).
                Controllers = ControllerInfo.CaptureFromVessel(vessel),
                // Launch-unique identity from the same live breakup-moment vessel. Null when
                // it was destroyed during the coalescing window; the snapshot-seeded fallback
                // below covers that case whenever a snapshot exists at all.
                RecordedVesselGuid = AnchorDetector.TryReadLiveVesselGuid(vessel)
            };
            // Stamp the parent-anchor contract on the new child Recording. The breakup
            // branch point's ParentRecordingIds list can have multiple parents in
            // chain-merge cases, so passing parentRecordingId explicitly avoids
            // ambiguity; the focused recording is the right anchor. Caller-decides
            // helper: both debris (IsDebris=true) and controlled-decoupled children
            // (IsDebris=false) receive the contract here.
            Recording.ApplyParentAnchorContract(childRec, parentRecordingId);
            if (!string.IsNullOrEmpty(childRec.ParentAnchorRecordingId))
            {
                string population = childRec.IsDebris ? "debris" : "controlled-child";
                ParsekLog.Verbose("Coalescer",
                    $"Parent-anchor contract applied at breakup: childRecId={childRecId} " +
                    $"childPid={pid} population={population} " +
                    $"parentRecId={parentRecordingId ?? "(none)"}");
            }

            // Bug #419 — invariant-enforcement: before any seed-point or snapshot code
            // runs, trim any pre-existing trajectory points at or after the breakup UT.
            // Debris recordings start empty in the normal path (CreateBreakupChildRecording
            // allocates a fresh Recording via `new Recording { ... }`), so this is a no-op
            // under current callers; but if any future inheritance path grafts parent
            // samples into the child Recording before this function runs, those samples
            // would stitch onto the post-boundary sampler stream and produce the
            // Points[n].ut < Points[n-1].ut failure signature that
            // `CommittedRecordingsHaveValidData` flags. Inclusive trim at breakupBp.UT
            // guarantees the authoritative seed at breakupBp.UT (either fallbackTrajectoryPoint
            // below, or OnVesselBackgrounded's pendingInitialTrajectoryPoint) becomes
            // Points[0] cleanly. The fallbackTrajectoryPoint path supplies its own
            // post-boundary seed (captured during the coalescing window, UT > breakupBp.UT
            // in the vessel-destroyed-during-window scenario) and is applied AFTER the
            // trim, so it is never mistakenly removed.
            // Defensive: under current callers childRec.Points is empty when we reach this line,
            // so the trim is a no-op and the warn below will not fire. Kept to lock the invariant
            // against future inheritance paths that might pre-seed post-boundary samples.
            int trimmedAtBoundary = FlightRecorder.TrimPointsAtOrAfterUT(childRec.Points, breakupBp.UT);
            if (trimmedAtBoundary > 0)
            {
                ParsekLog.Warn("Coalescer",
                    $"CreateBreakupChildRecording: trimmed {trimmedAtBoundary} inherited point(s) at/after " +
                    $"breakup UT={breakupBp.UT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"from child recId={childRec.RecordingId} (pid={pid}) to preserve monotonicity (#419)");
            }

            TrajectoryPoint? structuralSeedPoint = ApplyStructuralEventFlagToChildSeed(
                fallbackTrajectoryPoint,
                breakupBp.UT);

            if (vessel != null)
            {
                ConfigNode liveSnapshot = FlightRecorder.TryBackupSnapshot(vessel);
                SeedBreakupChildSnapshots(childRec, pid, liveSnapshot, fallbackSnapshot);
            }
            else if (fallbackSnapshot != null)
            {
                // Vessel destroyed during coalescing window — use pre-captured snapshot (#157)
                SeedBreakupChildSnapshots(childRec, pid, liveSnapshot: null, preCapturedSnapshot: fallbackSnapshot);
                childRec.StampTerminalState(
                    TerminalState.Destroyed, "CreateBreakupChildRecording.preCapturedSnapshot");
                childRec.ExplicitEndUT = breakupBp.UT;
                if (structuralSeedPoint.HasValue)
                    BackgroundRecorder.ApplyTrajectoryPointToRecording(childRec, structuralSeedPoint.Value);
                ParsekLog.Info("Coalescer",
                    $"CreateBreakupChildRecording: using pre-captured snapshot for pid={pid} (vessel destroyed)");
            }
            else
            {
                childRec.StampTerminalState(
                    TerminalState.Destroyed, "CreateBreakupChildRecording.noSnapshot");
                childRec.ExplicitEndUT = breakupBp.UT;
                if (structuralSeedPoint.HasValue)
                {
                    BackgroundRecorder.ApplyTrajectoryPointToRecording(childRec, structuralSeedPoint.Value);
                    ParsekLog.Info("Coalescer",
                        $"CreateBreakupChildRecording: using pre-captured trajectory point for pid={pid} (vessel destroyed)");
                }
            }

            // Destroyed-vessel arms: the live read above returned nothing, so take the guid
            // from whichever snapshot was seeded (its top-level `pid` IS the Vessel.id) -
            // the same value the load-time backfill reads, applied before the first save.
            // The no-vessel-AND-no-snapshot arm keeps no launch identity at all; there is
            // nothing left to read it from, and that child is stamped Destroyed at the
            // breakup UT with no trajectory of its own.
            if (childRec.AdoptRecordedVesselGuidIfEmpty(
                    VesselLaunchIdentity.TryReadVesselGuid(childRec.VesselSnapshot)
                    ?? VesselLaunchIdentity.TryReadVesselGuid(childRec.GhostVisualSnapshot)))
            {
                ParsekLog.Verbose("Coalescer",
                    $"CreateBreakupChildRecording: launch guid taken from the seeded snapshot " +
                    $"for pid={pid} recId={childRecId} guid={childRec.RecordedVesselGuid}");
            }

            tree.AddOrReplaceRecording(childRec);
            breakupBp.ChildRecordingIds.Add(childRecId);

            return childRec;
        }

        private static string DescribeBreakupChildSeedPose(
            TrajectoryPoint? seedPoint,
            Vessel liveVessel,
            double liveUT)
        {
            if (!seedPoint.HasValue)
                return "seedPoint=none";

            var ic = CultureInfo.InvariantCulture;
            TrajectoryPoint point = seedPoint.Value;
            string summary =
                $"seedPoint@{point.ut.ToString("F2", ic)} body={point.bodyName} " +
                $"flags={point.flags} " +
                $"lla=({point.latitude.ToString("F2", ic)}," +
                $"{point.longitude.ToString("F2", ic)}," +
                $"{point.altitude.ToString("F2", ic)}) " +
                $"vel={FormatVector3(point.velocity)}";

            Part rootPart = liveVessel?.rootPart;
            if (rootPart == null)
                return summary;

            CelestialBody body = liveVessel.mainBody;
            if ((body == null || !string.Equals(body.name, point.bodyName, StringComparison.Ordinal))
                && FlightGlobals.Bodies != null)
            {
                body = FlightGlobals.Bodies.Find(b =>
                    b != null && string.Equals(b.name, point.bodyName, StringComparison.Ordinal));
            }

            if (body == null || rootPart.transform == null)
                return summary;

            Vector3d seedWorld = body.GetWorldSurfacePosition(
                point.latitude,
                point.longitude,
                point.altitude);
            Vector3d rootWorld = rootPart.transform.position;
            Vector3d seedRootDelta = seedWorld - rootWorld;

            return summary + " " +
                $"liveRootUT={liveUT.ToString("F2", ic)} " +
                $"rootPart={rootPart.partInfo?.name ?? rootPart.name} pid={rootPart.persistentId} " +
                $"rootLla=({body.GetLatitude(rootWorld).ToString("F2", ic)}," +
                $"{body.GetLongitude(rootWorld).ToString("F2", ic)}," +
                $"{body.GetAltitude(rootWorld).ToString("F2", ic)}) " +
                $"seed-liveRoot={FormatVector3d(seedRootDelta)} " +
                $"seedLiveRootDist={seedRootDelta.magnitude.ToString("F2", ic)}m";
        }

        private static TrajectoryPoint? ResolveControlledChildInitialTrajectoryPoint(
            uint pid,
            TrajectoryPoint? capturedPoint,
            Vessel liveVessel,
            double liveUT)
        {
            if (liveVessel == null)
            {
                if (capturedPoint.HasValue)
                {
                    ParsekLog.Verbose("Coalescer",
                        $"Controlled child initial seed: pid={pid} source=decouple-callback " +
                        $"capturedUT={capturedPoint.Value.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                        $"liveUT={liveUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                        "reason=live-vessel-missing");
                }
                return capturedPoint;
            }

            TrajectoryPoint livePoint = BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                liveVessel,
                liveUT,
                preferRootPartSurfacePose: true);

            if (!capturedPoint.HasValue)
            {
                ParsekLog.Info("Coalescer",
                    $"Controlled child initial seed: pid={pid} source=live-current " +
                    $"liveUT={liveUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    "reason=no-captured-seed");
                return livePoint;
            }

            double seedLiveRootDistance;
            double propagatedSeedResidual;
            bool hasDistance = TryComputeBreakupSeedLiveRootDistance(
                capturedPoint.Value,
                liveVessel,
                liveUT,
                out seedLiveRootDistance,
                out propagatedSeedResidual);
            bool preferLive = ShouldPreferLiveBreakupChildSeed(
                childHasController: true,
                liveVesselAvailable: true,
                capturedSeedAvailable: true,
                seedLiveRootDistanceMeters: hasDistance ? seedLiveRootDistance : double.NaN,
                propagatedSeedLiveRootResidualMeters: hasDistance ? propagatedSeedResidual : double.NaN);

            if (!preferLive)
            {
                ParsekLog.Verbose("Coalescer",
                    $"Controlled child initial seed: pid={pid} source=decouple-callback " +
                    $"capturedUT={capturedPoint.Value.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"liveUT={liveUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"seedLiveRootDist={(hasDistance ? seedLiveRootDistance.ToString("F2", CultureInfo.InvariantCulture) : "n/a")}m " +
                    $"propagatedResidual={(hasDistance ? propagatedSeedResidual.ToString("F2", CultureInfo.InvariantCulture) : "n/a")}m " +
                    $"threshold={ControlledChildLiveSeedResidualToleranceMeters.ToString("F2", CultureInfo.InvariantCulture)}m");
                return capturedPoint;
            }

            bool directSeedMissesLiveRoot =
                IsFinite(seedLiveRootDistance)
                && seedLiveRootDistance > ControlledChildLiveSeedResidualToleranceMeters;
            string replacementReason = directSeedMissesLiveRoot
                ? "seed-misses-live-root"
                : "propagated-seed-misses-live-root";
            ParsekLog.Info("Coalescer",
                $"Controlled child initial seed replaced with live sample: pid={pid} " +
                $"capturedUT={capturedPoint.Value.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"liveUT={liveUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"seedLiveRootDist={seedLiveRootDistance.ToString("F2", CultureInfo.InvariantCulture)}m " +
                $"propagatedResidual={propagatedSeedResidual.ToString("F2", CultureInfo.InvariantCulture)}m " +
                $"threshold={ControlledChildLiveSeedResidualToleranceMeters.ToString("F2", CultureInfo.InvariantCulture)}m " +
                $"source=live-root-part reason={replacementReason}");
            return livePoint;
        }

        private static bool TryComputeBreakupSeedLiveRootDistance(
            TrajectoryPoint seedPoint,
            Vessel liveVessel,
            double liveUT,
            out double distanceMeters,
            out double propagatedResidualMeters)
        {
            distanceMeters = double.NaN;
            propagatedResidualMeters = double.NaN;

            Part rootPart = liveVessel?.rootPart;
            CelestialBody body = liveVessel?.mainBody;
            if (rootPart == null || rootPart.transform == null || body == null)
                return false;

            if (!string.Equals(body.name, seedPoint.bodyName, StringComparison.Ordinal)
                && FlightGlobals.Bodies != null)
            {
                body = FlightGlobals.Bodies.Find(b =>
                    b != null && string.Equals(b.name, seedPoint.bodyName, StringComparison.Ordinal));
            }

            if (body == null)
                return false;

            Vector3d seedWorld = body.GetWorldSurfacePosition(
                seedPoint.latitude,
                seedPoint.longitude,
                seedPoint.altitude);
            Vector3d rootWorld = rootPart.transform.position;
            distanceMeters = (seedWorld - rootWorld).magnitude;
            propagatedResidualMeters = ComputeBreakupSeedPropagatedResidualMeters(
                seedWorld,
                seedPoint.velocity,
                seedPoint.ut,
                rootWorld,
                liveUT);
            return IsFinite(distanceMeters) && IsFinite(propagatedResidualMeters);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string FormatVector3(Vector3 value)
        {
            var ic = CultureInfo.InvariantCulture;
            return $"({value.x.ToString("F2", ic)}," +
                   $"{value.y.ToString("F2", ic)}," +
                   $"{value.z.ToString("F2", ic)})";
        }

        private static string FormatVector3d(Vector3d value)
        {
            var ic = CultureInfo.InvariantCulture;
            return $"({value.x.ToString("F2", ic)}," +
                   $"{value.y.ToString("F2", ic)}," +
                   $"{value.z.ToString("F2", ic)})";
        }

        /// <summary>
        /// True when a background parent must not be split again: it carries a child branch
        /// point that already continued its vessel (a same-PID child, or a merge). A
        /// breakup-continuous parent (a decouple or breakup whose children are only the stages
        /// and debris it dropped) is still its vessel's line and may be split, as the
        /// foreground <c>CreateSplitBranch</c> already does.
        /// </summary>
        internal static bool BackgroundParentAlreadySplit(Recording parentRecording, RecordingTree tree)
        {
            if (parentRecording == null || string.IsNullOrEmpty(parentRecording.ChildBranchPointId))
                return false;
            return tree == null
                || !RecordingTree.ChildBranchLeavesVesselLineOpen(
                    parentRecording, tree.Recordings, tree.BranchPoints);
        }

        /// <summary>
        /// Wires a coalescer-emitted BREAKUP branch point into the tree: the active
        /// recording becomes its parent, the tree gains the branch point, and the
        /// recording's ChildBranchPointId links to it.
        /// </summary>
        internal static void WireBreakupIntoTree(
            RecordingTree tree, string activeRecId, Recording activeRec, BranchPoint breakupBp)
        {
            breakupBp.ParentRecordingIds.Add(activeRecId);
            tree.BranchPoints.Add(breakupBp);
            activeRec.ChildBranchPointId = breakupBp.Id;
        }

        /// <summary>
        /// Processes a BREAKUP branch point emitted by the crash coalescer.
        /// Adds the BREAKUP event as a branch point on the active tree recording and
        /// creates child recordings for debris and controlled vessels.
        /// If no active tree exists, the breakup is not recorded.
        /// </summary>
        void ProcessBreakupEvent(BranchPoint breakupBp)
        {
            if (activeTree == null)
            {
                ParsekLog.Info("Coalescer",
                    "ProcessBreakupEvent: no active tree — breakup not recorded. " +
                    $"cause={breakupBp.BreakupCause}, debris={breakupBp.DebrisCount}");
                return;
            }

            string activeRecId = activeTree.ActiveRecordingId;
            if (string.IsNullOrEmpty(activeRecId))
            {
                ParsekLog.Warn("Coalescer",
                    "ProcessBreakupEvent: active tree has no active recording — cannot attach breakup event");
                return;
            }

            Recording activeRec;
            if (!activeTree.Recordings.TryGetValue(activeRecId, out activeRec))
            {
                ParsekLog.Warn("Coalescer",
                    $"ProcessBreakupEvent: active recording id={activeRecId} not found in tree");
                return;
            }

            WireBreakupIntoTree(activeTree, activeRecId, activeRec, breakupBp);

            // Refresh the vessel snapshot to reflect post-breakup state.
            // The recording continues past breakup (breakup-continuous design), so the
            // spawn snapshot must show the surviving vessel, not the pre-breakup config.
            // Without this, spawning materializes the full pre-breakup rocket. (#224)
            //
            // Bug #271: in always-tree mode, the tree recording's trajectory points are
            // still in the recorder buffer (not flushed to the Recording object), so
            // SnapshotVessel's Points.Count==0 guard would skip the snapshot. Use
            // TryBackupSnapshot directly — the distance calculation is not needed for
            // a mid-flight snapshot refresh.
            //
            // The surviving parent is the vessel the active recorder records. It is not
            // always the active vessel: a split can move focus to the vessel that came off
            // before the hand-off to the background recorder (EmitPendingSplitBeforeFocusLeaves).
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            uint parentVesselPid = ResolveBreakupParentVesselPid(
                recorder != null ? recorder.RecordingVesselId : 0u,
                activeVessel != null ? activeVessel.persistentId : 0u);
            Vessel parentVessel = activeVessel != null && activeVessel.persistentId == parentVesselPid
                ? activeVessel
                : FlightRecorder.FindVesselByPid(parentVesselPid);
            if (parentVessel != null && parentVessel.persistentId == activeRec.VesselPersistentId)
            {
                var snap = FlightRecorder.TryBackupSnapshot(parentVessel);
                if (snap != null)
                    activeRec.VesselSnapshot = snap;
                ParsekLog.Info("Coalescer",
                    $"ProcessBreakupEvent: refreshed VesselSnapshot post-breakup " +
                    $"(parts={parentVessel.parts?.Count ?? 0}, snapshot={snap != null})");
            }

            // Bug #298: snapshot active recorder's engine/RCS state for child recordings.
            // The recorder is still running (breakup-continuous design), so its state is live.
            InheritedEngineState? breakupEngineState = recorder != null
                ? InheritedEngineState.FromRecorder(recorder) : null;

            TrajectoryPoint? focusedParentSeedAnchorPoint = null;
            if (recorder != null
                && !recorder.CurrentTrackSectionUsesRelativeFrame
                && FlightRecorder.TryFindStructuralEventSnapshotPointForUT(
                    recorder?.Recording,
                    breakupBp.UT,
                    out TrajectoryPoint parentSeedAnchorPoint))
            {
                focusedParentSeedAnchorPoint = parentSeedAnchorPoint;
                ParsekLog.Verbose("Coalescer",
                    $"ProcessBreakupEvent: focused parent seed anchor point available: " +
                    $"parentRec={activeRecId} ut={parentSeedAnchorPoint.ut.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"flags={parentSeedAnchorPoint.flags}");
            }
            else
            {
                ParsekLog.Verbose("Coalescer",
                    $"ProcessBreakupEvent: focused parent seed anchor point unavailable: " +
                    $"parentRec={activeRecId} breakupUT={breakupBp.UT.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"recorderPoints={recorder?.Recording?.Count ?? 0} " +
                    $"currentSectionRelative={recorder?.CurrentTrackSectionUsesRelativeFrame ?? false}");
            }

            // Create child recording segments for controlled children (vessels with probe
            // cores that survive the breakup).
            // These are NOT debris — they record indefinitely (no TTL) and can serve as
            // RELATIVE anchors during playback.
            var controlledChildren = crashCoalescer.LastEmittedControlledChildPids;

            // Phase 4 (Rewind-to-Staging): collect (vessel, child recording) pairs
            // for the controllable outputs of the breakup. The parent recording
            // carrying the active vessel is also a controllable output (it survives
            // the breakup in breakup-continuous design), so it participates in the
            // multi-controllable classifier alongside the children.
            var breakupControllablePairs = new List<(Vessel vessel, Recording rec)>();
            if (parentVessel != null)
                breakupControllablePairs.Add((parentVessel, activeRec));

            if (controlledChildren != null && controlledChildren.Count > 0)
            {
                for (int i = 0; i < controlledChildren.Count; i++)
                {
                    uint pid = controlledChildren[i];
                    Vessel childVessel = FlightRecorder.FindVesselByPid(pid);
                    // Keep the split-time ghost snapshot even when the child vessel is
                    // still alive 0.5s later; otherwise the ghost builds from a later
                    // post-split snapshot and can appear spatially ahead on frame 1.
                    ConfigNode ctrlSnap = crashCoalescer.GetPreCapturedSnapshot(pid);
                    TrajectoryPoint? breakupChildPoint = ApplyStructuralEventFlagToChildSeed(
                        crashCoalescer.GetPreCapturedTrajectoryPoint(pid),
                        breakupBp.UT);

                    // Skip dead-on-arrival controlled children: once the live
                    // Vessel is gone before the coalescer window expires there
                    // is no controlled continuation to sample. A pre-captured
                    // snapshot only turns that into a destroyed one-point
                    // "Unknown" row, so leave the split represented by the
                    // parent BREAKUP branch point.
                    if (ShouldSkipDeadOnArrivalControlledChild(
                            childVesselIsAlive: childVessel != null,
                            hasPreCapturedSnapshot: ctrlSnap != null))
                    {
                        ParsekLog.Info("Coalescer",
                            BuildDeadOnArrivalControlledChildSkipLog(
                                pid,
                                hasPreCapturedSnapshot: ctrlSnap != null));
                        continue;
                    }

                    TrajectoryPoint? initialPoint = ResolveControlledChildInitialTrajectoryPoint(
                        pid,
                        breakupChildPoint,
                        childVessel,
                        Planetarium.GetUniversalTime());
                    initialPoint = ApplyStructuralEventFlagToChildSeed(initialPoint, breakupBp.UT);

                    var childRec = CreateBreakupChildRecording(activeTree, breakupBp, pid, childVessel, false, "Unknown",
                        ctrlSnap, initialPoint, parentGeneration: activeRec.Generation,
                        parentRecordingId: activeRec.RecordingId);

                    // Add to BackgroundRecorder for trajectory sampling (no TTL - records indefinitely)
                    if (childVessel != null && backgroundRecorder != null)
                    {
                        activeTree.BackgroundMap[pid] = childRec.RecordingId;
                        // Parent-anchored controlled-decoupled children carry the same
                        // structural-event parent-anchor seed as debris siblings so the
                        // first BG sample after split lands on the deterministic split-
                        // moment parent pose rather than relying on a post-split live
                        // resolve. The debris loop below queues the same seed at line 6470.
                        if (focusedParentSeedAnchorPoint.HasValue)
                        {
                            backgroundRecorder.QueueDebrisSeedParentAnchorPoint(
                                pid,
                                activeRecId,
                                focusedParentSeedAnchorPoint.Value);
                        }
                        backgroundRecorder.OnVesselBackgrounded(
                            pid,
                            breakupEngineState,
                            initialTrajectoryPoint: initialPoint);
                    }

                    if (childVessel != null && childRec != null)
                        breakupControllablePairs.Add((childVessel, childRec));

                    ParsekLog.Info("Coalescer",
                        $"ProcessBreakupEvent: controlled child created: pid={pid}, " +
                        $"name='{childRec.VesselName}', recId={childRec.RecordingId}, " +
                        $"alive={childVessel != null}, bgRecorder={backgroundRecorder != null} " +
                        $"{DescribeBreakupChildSeedPose(initialPoint, childVessel, Planetarium.GetUniversalTime())}");
                }
            }

            // Phase 4 (design §5.1 + §7.1 + §7.2 + §7.19): if this breakup produced
            // >=2 controllable outputs (counting the surviving parent), author a
            // RewindPoint. The author computes PidSlotMap / RootPartPidMap on the
            // deferred frame and writes the quicksave atomically. §7.1 prunes the
            // single-controllable case (no RP for debris-only breakups).
            TryAuthorRewindPointForBreakup(breakupBp, breakupControllablePairs);

            // Also create debris child recordings for new debris from this breakup
            int skippedDebris = 0;
            var debrisPids = crashCoalescer.LastEmittedDebrisPids;
            if (debrisPids != null && debrisPids.Count > 0)
            {
                double debrisExpiryUT = breakupBp.UT + BackgroundRecorder.DebrisTTLSeconds;
                for (int i = 0; i < debrisPids.Count; i++)
                {
                    uint pid = debrisPids[i];
                    Vessel debrisVessel = FlightRecorder.FindVesselByPid(pid);

                    if (debrisVessel != null && !ShouldRecordDebris(debrisVessel))
                    {
                        skippedDebris++;
                        continue;
                    }

                    // Use the coalescer's split-time snapshot for ghost visuals even when
                    // the debris vessel survives the coalescing window; VesselSnapshot still
                    // comes from the later live state for spawning.
                    ConfigNode preSnap = crashCoalescer.GetPreCapturedSnapshot(pid);
                    TrajectoryPoint? breakupChildPoint = ApplyStructuralEventFlagToChildSeed(
                        crashCoalescer.GetPreCapturedTrajectoryPoint(pid),
                        breakupBp.UT);
                    var childRec = CreateBreakupChildRecording(activeTree, breakupBp, pid, debrisVessel, true, "Debris",
                        preSnap, breakupChildPoint, parentGeneration: activeRec.Generation,
                        parentRecordingId: activeRec.RecordingId);

                    if (debrisVessel != null && backgroundRecorder != null)
                    {
                        TrajectoryPoint? initialPoint = breakupChildPoint;
                        if (!initialPoint.HasValue)
                        {
                            double sampleUT = Planetarium.GetUniversalTime();
                            initialPoint = BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                                debrisVessel, sampleUT, preferRootPartSurfacePose: true);
                        }
                        initialPoint = ApplyStructuralEventFlagToChildSeed(initialPoint, breakupBp.UT);
                        activeTree.BackgroundMap[pid] = childRec.RecordingId;
                        if (focusedParentSeedAnchorPoint.HasValue)
                        {
                            backgroundRecorder.QueueDebrisSeedParentAnchorPoint(
                                pid,
                                activeRecId,
                                focusedParentSeedAnchorPoint.Value);
                        }
                        backgroundRecorder.OnVesselBackgrounded(
                            pid,
                            breakupEngineState,
                            initialTrajectoryPoint: initialPoint);
                        backgroundRecorder.SetDebrisExpiry(pid, debrisExpiryUT);
                    }

                    ParsekLog.Info("Coalescer",
                        $"ProcessBreakupEvent: debris child created: pid={pid}, " +
                        $"name='{childRec.VesselName}', recId={childRec.RecordingId}, " +
                        $"alive={debrisVessel != null} " +
                        $"{DescribeBreakupChildSeedPose(breakupChildPoint, debrisVessel, Planetarium.GetUniversalTime())}");
                }
            }

            ParsekLog.Info("Coalescer",
                $"ProcessBreakupEvent: {breakupBp.Type} attached to tree={activeTree.Id}, " +
                $"parentRec={activeRecId}, bpId={breakupBp.Id}, " +
                $"cause={breakupBp.BreakupCause ?? breakupBp.SplitCause ?? "?"}, debris={breakupBp.DebrisCount}, " +
                $"skippedTrivial={skippedDebris}, " +
                $"duration={breakupBp.BreakupDuration:F3}s");
        }

        void OnVesselSituationChange(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> data)
        {
            if (data.host != null && GhostMapPresence.IsGhostMapVessel(data.host.persistentId)) return;
            double currentUT = Planetarium.GetUniversalTime();
            bool suppressLaunchAutoRecordForTimeJump =
                TimeJumpManager.IsTimeJumpLaunchAutoRecordSuppressed(
                    TimeJumpManager.IsTimeJumpLaunchAutoRecordInProgress,
                    Time.frameCount,
                    TimeJumpManager.TimeJumpLaunchAutoRecordSuppressUntilFrame);

            // Track when the active vessel enters LANDED/SPLASHED for the settle timer
            if (data.host == FlightGlobals.ActiveVessel &&
                (data.to == Vessel.Situations.LANDED || data.to == Vessel.Situations.SPLASHED))
            {
                lastLandedUT = currentUT;
            }

            var launchDecision = EvaluateAutoRecordLaunchDecision(
                isRecording: IsRecording,
                isActiveVessel: data.host == FlightGlobals.ActiveVessel,
                fromSituation: data.from,
                autoRecordOnLaunchEnabled: ParsekSettings.Current?.autoRecordOnLaunch != false,
                lastLandedUt: lastLandedUT,
                currentUt: currentUT,
                landedSettleThreshold: LandedSettleThreshold,
                suppressForTimeJumpTransient: suppressLaunchAutoRecordForTimeJump);

            switch (launchDecision)
            {
                case AutoRecordLaunchDecision.SkipAlreadyRecording:
                    return;

                case AutoRecordLaunchDecision.SkipInactiveVessel:
                    ParsekLog.VerboseRateLimited("Flight", "sit-change-other",
                        $"OnVesselSituationChange: ignoring non-active vessel " +
                        $"'{data.host?.vesselName ?? "null"}' pid={data.host?.persistentId ?? 0u} " +
                        $"({data.from} → {data.to})");
                    return;

                case AutoRecordLaunchDecision.SkipBounce:
                    double settledTime = lastLandedUT >= 0
                        ? currentUT - lastLandedUT
                        : 0;
                    ParsekLog.VerboseRateLimited("Flight", "sit-change-bounce",
                        $"OnVesselSituationChange: LANDED → {data.to} after {settledTime:F1}s (< {LandedSettleThreshold}s settle threshold)");
                    return;

                case AutoRecordLaunchDecision.SkipNotLaunchTransition:
                    ParsekLog.VerboseRateLimited("Flight", "sit-change-not-launch",
                        $"OnVesselSituationChange: not a launch transition ({data.from} → {data.to})");
                    return;

                case AutoRecordLaunchDecision.SkipDisabled:
                    ParsekLog.Verbose("Flight", "OnVesselSituationChange: auto-record disabled in settings");
                    return;

                case AutoRecordLaunchDecision.SkipTimeJumpTransient:
                    ParsekLog.Info("Flight",
                        $"OnVesselSituationChange: suppressing time-jump transient ({data.from} → {data.to}) " +
                        $"for '{data.host?.vesselName ?? "null"}' frame={Time.frameCount} " +
                        $"suppressUntilFrame={TimeJumpManager.TimeJumpLaunchAutoRecordSuppressUntilFrame}");
                    return;
            }

            StartRecording(suppressStartScreenMessage: true,
                fromPrelaunchTransition: launchDecision == AutoRecordLaunchDecision.StartFromPrelaunch);
            Log($"Auto-record started ({data.from} → {data.to})");
            ScreenMessage("Recording STARTED (auto)", 2f);
        }

        // First-staging-on-pad auto-record trigger. A stage activated while the active
        // vessel is still PRELAUNCH (e.g. igniting SRBs that then decouple and fly off
        // the pad before the main stage lifts off) must start the recorder NOW so the
        // subsequent decouple is captured as a debris branch of the recorded vessel.
        // Without this the recorder only starts on the main stage's PRELAUNCH->FLYING
        // transition, by which point the separated debris is already a foreign vessel
        // and is dropped by the decouple capture gate.
        void OnStageActivate(int stage)
        {
            Vessel av = FlightGlobals.ActiveVessel;
            if (av != null && GhostMapPresence.IsGhostMapVessel(av.persistentId)) return;

            bool suppressLaunchAutoRecordForTimeJump =
                TimeJumpManager.IsTimeJumpLaunchAutoRecordSuppressed(
                    TimeJumpManager.IsTimeJumpLaunchAutoRecordInProgress,
                    Time.frameCount,
                    TimeJumpManager.TimeJumpLaunchAutoRecordSuppressUntilFrame);

            var stagingDecision = EvaluateAutoRecordStagingDecision(
                isRecording: IsRecording,
                hasActiveVessel: av != null,
                activeVesselIsPrelaunch: av != null && av.situation == Vessel.Situations.PRELAUNCH,
                autoRecordOnLaunchEnabled: ParsekSettings.Current?.autoRecordOnLaunch != false,
                suppressForTimeJumpTransient: suppressLaunchAutoRecordForTimeJump);

            switch (stagingDecision)
            {
                case AutoRecordStagingDecision.SkipAlreadyRecording:
                    return;

                case AutoRecordStagingDecision.SkipTimeJumpTransient:
                    ParsekLog.Info("Flight",
                        $"OnStageActivate: suppressing time-jump transient (stage={stage}) " +
                        $"for '{av?.vesselName ?? "null"}' frame={Time.frameCount} " +
                        $"suppressUntilFrame={TimeJumpManager.TimeJumpLaunchAutoRecordSuppressUntilFrame}");
                    return;

                case AutoRecordStagingDecision.SkipNoActiveVessel:
                    ParsekLog.VerboseRateLimited("Flight", "stage-no-active",
                        $"OnStageActivate: no active vessel (stage={stage})");
                    return;

                case AutoRecordStagingDecision.SkipNotOnPad:
                    ParsekLog.VerboseRateLimited("Flight", "stage-not-prelaunch",
                        $"OnStageActivate: active vessel '{av?.vesselName ?? "null"}' not PRELAUNCH " +
                        $"(stage={stage}, situation={av?.situation}): in-flight staging left to recorder/launch trigger");
                    return;

                case AutoRecordStagingDecision.SkipDisabled:
                    ParsekLog.Verbose("Flight", "OnStageActivate: auto-record disabled in settings");
                    return;
            }

            StartRecording(suppressStartScreenMessage: true);
            Log($"Auto-record started (first staging on pad, stage={stage})");
            ScreenMessage("Recording STARTED (auto)", 2f);
        }

        void OnCrewBoardVessel(GameEvents.FromToAction<Part, Part> data)
        {
            RecorderStateLog.RecState("OnCrewBoardVessel:entry", CaptureRecorderState());
            if (activeTree == null)
            {
                ParsekLog.Verbose("Flight", "OnCrewBoardVessel: no active tree — ignoring");
                return;
            }
            if (pendingSplitInProgress)
            {
                LogSplitSkip(
                    "OnCrewBoardVessel",
                    "pendingSplitInProgress",
                    data.from?.vessel?.persistentId ?? 0u,
                    data.to?.vessel?.persistentId ?? 0u);
                return; // split must complete first
            }
            if (data.to?.vessel == null)
            {
                LogSplitSkip(
                    "OnCrewBoardVessel",
                    "target-vessel-null",
                    data.from?.vessel?.persistentId ?? 0u,
                    0u);
                ParsekLog.Verbose("Flight", "OnCrewBoardVessel: target vessel is null — ignoring");
                return;
            }

            pendingBoardingTargetPid = data.to.vessel.persistentId;
            boardingConfirmFrames = 0;

            // Store whether the boarding target is in the tree
            pendingBoardingTargetInTree = (activeTree != null &&
                activeTree.BackgroundMap.ContainsKey(data.to.vessel.persistentId));

            Log($"onCrewBoardVessel: target vessel pid={pendingBoardingTargetPid}" +
                (activeTree != null ? $", inTree={pendingBoardingTargetInTree}" : ""));

            TryPromoteEvaRecordingForBoardMerge(data.from?.vessel);
        }

        /// <summary>
        /// Rebinds the live recorder to a boarding kerbal's EVA branch recording when that
        /// recording is still only background-tracked, so the board merge can run.
        /// </summary>
        /// <remarks>
        /// An EVA branch parks the kerbal's recording in <c>BackgroundMap</c> and leaves the
        /// promotion to the post-switch first-modification watcher. A kerbal that exits and
        /// re-boards without moving (hatch/ladder hop, no release) never trips a trigger, so
        /// the watcher is still armed when the board arrives: the boarding detection in
        /// <see cref="OnVesselSwitchComplete"/> requires <c>recorder.IsRecording</c> and
        /// <see cref="HandleTreeBoardMerge"/> requires the EVA recording to be
        /// <c>activeTree.ActiveRecordingId</c>, so without this rebind the Board branch
        /// point is silently dropped and the EVA recording terminates as Destroyed instead
        /// of Boarded. onCrewBoardVessel fires before KSP's focus switch, so the EVA kerbal
        /// is still the active vessel here and the normal promotion path applies.
        /// </remarks>
        private void TryPromoteEvaRecordingForBoardMerge(Vessel evaVessel)
        {
            uint evaPid = evaVessel != null ? evaVessel.persistentId : 0u;
            uint activePid = FlightGlobals.ActiveVessel != null
                ? FlightGlobals.ActiveVessel.persistentId
                : 0u;
            string trackedRecordingId = null;
            bool trackedInBackground = activeTree != null && evaPid != 0
                && activeTree.BackgroundMap.TryGetValue(evaPid, out trackedRecordingId);

            var decision = DecideEvaBoardPromotion(
                hasActiveTree: activeTree != null,
                restoringActiveTree: restoringActiveTree,
                sourceIsEva: evaVessel != null && evaVessel.isEVA,
                sourceIsGhostVessel: evaPid != 0 && GhostMapPresence.IsGhostMapVessel(evaPid),
                sourceVesselPid: evaPid,
                hasLiveRecorder: recorder != null,
                liveRecorderVesselPid: recorder != null ? recorder.RecordingVesselId : 0u,
                activeVesselPid: activePid,
                sourceTrackedInBackground: trackedInBackground,
                trackedRecordingId: trackedRecordingId,
                treeActiveRecordingId: activeTree != null ? activeTree.ActiveRecordingId : null);

            if (decision != EvaBoardPromotionDecision.Promote)
            {
                ParsekLog.Verbose("Flight",
                    $"EVA board promotion skipped: decision={decision} evaPid={evaPid} " +
                    $"activePid={activePid} targetPid={pendingBoardingTargetPid} " +
                    $"tracked={trackedInBackground} " +
                    $"treeActiveRec={activeTree?.ActiveRecordingId ?? "-"} " +
                    $"trackedRec={trackedRecordingId ?? "-"}");
                return;
            }

            ParsekLog.Info("Flight",
                $"EVA board promotion: rebinding background EVA recording " +
                $"'{trackedRecordingId}' to the live recorder before the board merge " +
                $"(evaPid={evaPid}, targetPid={pendingBoardingTargetPid})");

            backgroundRecorder?.OnVesselRemovedFromBackground(evaPid);
            PromoteRecordingFromBackground(trackedRecordingId, evaVessel);

            if (recorder != null && recorder.IsRecording)
                DisarmPostSwitchAutoRecord("eva board promotion");
            else
            {
                ParsekLog.Warn("Flight",
                    $"EVA board promotion failed to start a recorder for evaPid={evaPid} - " +
                    $"board merge will be skipped");
            }
        }

        private bool TryStartEvaBranchFromBackgroundParent(GameEvents.FromToAction<Part, Part> data)
        {
            uint sourcePid = data.from?.vessel?.persistentId ?? 0u;
            uint targetPid = data.to?.vessel?.persistentId ?? 0u;

            // Only the structural preconditions matter before we can safely dereference activeTree/sourceVessel.
            var initialDecision = DecideEvaBackgroundParentRoute(
                isRecording: IsRecording,
                pendingSplitInProgress: pendingSplitInProgress,
                hasActiveTree: activeTree != null,
                hasSourceVessel: data.from?.vessel != null,
                trackedParentResolved: false,
                mappedRecordingExists: false,
                hasCrewName: false);

            if (initialDecision == EvaBackgroundParentRouteDecision.NotHandled)
                return false;

            if (initialDecision == EvaBackgroundParentRouteDecision.HandledSkipPendingSplit)
            {
                LogSplitSkip(
                    "OnCrewOnEva",
                    "pendingSplitInProgress",
                    sourcePid,
                    targetPid);
                ClearPendingEvaAutoRecordState();
                return true;
            }

            Vessel sourceVessel = data.from?.vessel;
            string parentRecordingId;
            string diagnostic;
            bool trackedParentResolved = TryResolveTrackedBackgroundParentRecording(
                    activeTree,
                    sourceVessel.persistentId,
                    out parentRecordingId,
                    out diagnostic);
            bool mappedRecordingExists = !string.IsNullOrEmpty(parentRecordingId)
                && activeTree.Recordings != null
                && activeTree.Recordings.ContainsKey(parentRecordingId);
            string kerbalName = ExtractEvaKerbalName(data);

            var decision = DecideEvaBackgroundParentRoute(
                isRecording: false,
                pendingSplitInProgress: false,
                hasActiveTree: true,
                hasSourceVessel: true,
                trackedParentResolved: trackedParentResolved,
                mappedRecordingExists: mappedRecordingExists,
                hasCrewName: !string.IsNullOrEmpty(kerbalName));

            if (decision == EvaBackgroundParentRouteDecision.HandledUntrackedSource)
            {
                ParsekLog.Warn("Flight",
                    $"OnCrewOnEva: source vessel pid={sourceVessel.persistentId} is not the live recorder " +
                    $"and is not a tracked background parent ({diagnostic}); suppressing deferred auto-record");
                ScreenMessage("Recording blocked (EVA source not tracked)", 2f);
                ClearPendingEvaAutoRecordState();
                return true;
            }

            if (decision == EvaBackgroundParentRouteDecision.HandledInvalidMapEntry)
            {
                ParsekLog.Warn("Flight",
                    $"OnCrewOnEva: background parent map entry is invalid " +
                    $"(sourcePid={sourceVessel.persistentId}, parentRec={parentRecordingId ?? "null"}, " +
                    $"diagnostic={diagnostic}); suppressing deferred auto-record");
                ScreenMessage("Recording blocked (invalid EVA tree state)", 2f);
                ClearPendingEvaAutoRecordState();
                return true;
            }

            if (decision == EvaBackgroundParentRouteDecision.HandledMissingCrewName)
            {
                ParsekLog.Warn("Flight",
                    $"OnCrewOnEva: source vessel pid={sourceVessel.persistentId} is tracked in background " +
                    "but EVA kerbal name could not be extracted; suppressing deferred auto-record");
                ScreenMessage("Recording blocked (EVA crew unknown)", 2f);
                ClearPendingEvaAutoRecordState();
                return true;
            }

            if (decision != EvaBackgroundParentRouteDecision.StartDeferredBackgroundParentBranch)
                return false;

            ClearPendingEvaAutoRecordState();
            pendingSplitInProgress = true;
            Log($"Background-parent EVA detected: '{kerbalName}' from pid={sourceVessel.persistentId} " +
                $"parentRec={parentRecordingId} - starting deferred tree branch");
            StartCoroutine(DeferredEvaBranchFromBackgroundParent(
                parentRecordingId,
                sourceVessel.persistentId,
                kerbalName,
                data));
            return true;
        }

        private void ClearPendingEvaAutoRecordState()
        {
            pendingAutoRecord = false;
        }

        void OnCrewOnEva(GameEvents.FromToAction<Part, Part> data)
        {
            RecorderStateLog.RecState("OnCrewOnEva:entry", CaptureRecorderState());
            // Mid-recording EVA: tree branch (replaces legacy chain continuation)
            if (IsRecording)
            {
                if (pendingSplitInProgress)
                {
                    LogSplitSkip(
                        "OnCrewOnEva",
                        "pendingSplitInProgress",
                        data.from?.vessel?.persistentId ?? 0u,
                        data.to?.vessel?.persistentId ?? 0u);
                    return; // another split is being processed
                }

                double evaEventUT = Planetarium.GetUniversalTime();
                var evaInvolved = new List<Vessel>(2);
                if (data.from?.vessel != null) evaInvolved.Add(data.from.vessel);
                if (data.to?.vessel != null && data.to.vessel != data.from?.vessel)
                    evaInvolved.Add(data.to.vessel);
                backgroundRecorder?.AppendStructuralEventSnapshot(evaEventUT, evaInvolved, "EVA");

                // Only trigger if EVA is from the vessel we're recording
                if (data.from?.vessel == null ||
                    data.from.vessel.persistentId != recorder.RecordingVesselId)
                {
                    LogSplitSkip(
                        "OnCrewOnEva",
                        data.from?.vessel == null ? "source-vessel-null" : "source-vessel-mismatch",
                        data.from?.vessel?.persistentId ?? 0u,
                        data.to?.vessel?.persistentId ?? 0u);
                    return;
                }

                string kerbalName = ExtractEvaKerbalName(data);
                if (string.IsNullOrEmpty(kerbalName))
                {
                    Log("EVA during recording but could not extract kerbal name — ignoring");
                    return;
                }

                // Phase 9 (design doc §12, §18 Phase 9): structural-event snapshot at
                // the exact EVA UT for the recorded vessel (the parent capsule). The
                // EVA kerbal-as-vessel is also "involved" per §12; AppendStructuralEventSnapshot
                // dedups by RecordingVesselId so only the parent capsule's snapshot is
                // committed in the typical case where the parent is the recorded vessel.
                recorder.AppendStructuralEventSnapshot(evaEventUT, evaInvolved, "EVA");

                // Tree mode: stop recorder synchronously, defer tree branch creation
                recorder.StopRecordingForChainBoundary();
                pendingSplitRecorder = recorder;
                recorder = null;
                pendingSplitInProgress = true;
                Log($"Mid-recording EVA detected: '{kerbalName}' — starting deferred tree branch");
                StartCoroutine(DeferredEvaBranch(kerbalName, data));
                return;
            }

            if (!IsRecording && TryStartEvaBranchFromBackgroundParent(data))
                return;

            bool hasSourceVessel = data.from?.vessel != null;
            if (!ShouldQueueAutoRecordOnEva(
                hasSourceVessel,
                autoRecordOnEvaEnabled: ParsekSettings.Current?.autoRecordOnEva != false))
            {
                if (!hasSourceVessel)
                {
                    LogSplitSkip(
                        "OnCrewOnEva",
                        "source-vessel-null",
                        0u,
                        data.to?.vessel?.persistentId ?? 0u);
                    ParsekLog.Verbose("Flight", "OnCrewOnEva: source vessel is null — ignoring");
                }
                else
                    ParsekLog.Verbose("Flight", "OnCrewOnEva: auto-record on EVA disabled in settings");
                return;
            }

            // The EVA kerbal may not yet be the active vessel, defer to Update()
            pendingAutoRecord = true;
            Log($"EVA detected (sit={data.from.vessel.situation}) — pending auto-record");
        }

        private void LogSplitSkip(string source, string reason, uint sourcePid, uint targetPid)
        {
            string activeRecordingId = activeTree?.ActiveRecordingId;
            string identity = string.Format(CultureInfo.InvariantCulture,
                "split-skip-{0}-{1}-{2}-{3}",
                string.IsNullOrEmpty(source) ? "unknown" : source,
                string.IsNullOrEmpty(activeRecordingId) ? "none" : activeRecordingId,
                sourcePid,
                targetPid);
            string stateKey = string.Format(CultureInfo.InvariantCulture,
                "{0}|pending={1}",
                string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                pendingSplitInProgress ? 1 : 0);
            ParsekLog.VerboseOnChange("Flight",
                identity,
                stateKey,
                FormatSplitSkipSummary(
                    source,
                    reason,
                    activeRecordingId,
                    sourcePid,
                    targetPid,
                    pendingSplitInProgress));
        }

        internal static string ExtractEvaKerbalName(GameEvents.FromToAction<Part, Part> data)
        {
            if (data.to == null) return null;
            var crew = data.to.protoModuleCrew;
            if (crew != null && crew.Count > 0) return crew[0].name;
            if (data.to.vessel != null) return data.to.vessel.vesselName;
            return null;
        }

        internal static AutoRecordLaunchDecision EvaluateAutoRecordLaunchDecision(
            bool isRecording,
            bool isActiveVessel,
            Vessel.Situations fromSituation,
            bool autoRecordOnLaunchEnabled,
            double lastLandedUt,
            double currentUt,
            double landedSettleThreshold,
            bool suppressForTimeJumpTransient)
        {
            if (isRecording)
                return AutoRecordLaunchDecision.SkipAlreadyRecording;

            // Time-jump transient suppression is checked before the active-vessel guard
            // so the skip decision (and its INFO log) fires for any vessel whose situation
            // changes during the jump window — including synthetic spawn vessels that the
            // playback policy materializes during a Real Spawn Control / Timeline FF jump.
            // The original #526 bug was an active-vessel `PRELAUNCH -> FLYING` flicker, but
            // labelling non-active flickers as `SkipInactiveVessel` hid the time-jump origin
            // and made the in-game canaries unable to confirm the suppression branch fired.
            if (suppressForTimeJumpTransient)
                return AutoRecordLaunchDecision.SkipTimeJumpTransient;

            if (!isActiveVessel)
                return AutoRecordLaunchDecision.SkipInactiveVessel;

            if (fromSituation == Vessel.Situations.PRELAUNCH)
            {
                return autoRecordOnLaunchEnabled
                    ? AutoRecordLaunchDecision.StartFromPrelaunch
                    : AutoRecordLaunchDecision.SkipDisabled;
            }

            if (fromSituation == Vessel.Situations.LANDED)
            {
                double settledTime = lastLandedUt >= 0
                    ? currentUt - lastLandedUt
                    : 0;
                if (settledTime < landedSettleThreshold)
                    return AutoRecordLaunchDecision.SkipBounce;

                return autoRecordOnLaunchEnabled
                    ? AutoRecordLaunchDecision.StartFromSettledLanded
                    : AutoRecordLaunchDecision.SkipDisabled;
            }

            return AutoRecordLaunchDecision.SkipNotLaunchTransition;
        }

        // Decision for the first-staging-on-pad auto-record trigger (OnStageActivate).
        // Mirrors EvaluateAutoRecordLaunchDecision's guard ordering: an already-running
        // recorder short-circuits first, then the time-jump transient suppression, so a
        // synthetic spawn vessel staging during an FF / warp-to-time window cannot
        // spuriously start a recording. Only fires while the active vessel is PRELAUNCH
        // (still on the pad); in-flight staging is left to the existing recorder, and the
        // normal clamp-release launch is still covered by the PRELAUNCH->FLYING trigger
        // (whichever fires first wins via the isRecording guard).
        internal static AutoRecordStagingDecision EvaluateAutoRecordStagingDecision(
            bool isRecording,
            bool hasActiveVessel,
            bool activeVesselIsPrelaunch,
            bool autoRecordOnLaunchEnabled,
            bool suppressForTimeJumpTransient)
        {
            if (isRecording)
                return AutoRecordStagingDecision.SkipAlreadyRecording;

            if (suppressForTimeJumpTransient)
                return AutoRecordStagingDecision.SkipTimeJumpTransient;

            if (!hasActiveVessel)
                return AutoRecordStagingDecision.SkipNoActiveVessel;

            if (!activeVesselIsPrelaunch)
                return AutoRecordStagingDecision.SkipNotOnPad;

            if (!autoRecordOnLaunchEnabled)
                return AutoRecordStagingDecision.SkipDisabled;

            return AutoRecordStagingDecision.StartFromPrelaunchStaging;
        }

        internal static bool ShouldQueueAutoRecordOnEva(
            bool hasSourceVessel,
            bool autoRecordOnEvaEnabled)
        {
            return hasSourceVessel && autoRecordOnEvaEnabled;
        }

        internal static bool ShouldStartDeferredAutoRecordEva(
            bool pendingAutoRecord,
            bool isRecording,
            bool hasActiveVessel,
            bool activeVesselIsEva)
        {
            return pendingAutoRecord && !isRecording && hasActiveVessel && activeVesselIsEva;
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

        internal static bool ShouldArmPostSwitchAutoRecord(
            bool autoRecordOnFirstModificationAfterSwitchEnabled,
            bool isRecording,
            bool hasNewVessel,
            bool newVesselIsGhost)
        {
            return autoRecordOnFirstModificationAfterSwitchEnabled
                && !isRecording
                && hasNewVessel
                && !newVesselIsGhost;
        }

        internal static PostSwitchAutoRecordArmDecision EvaluatePostSwitchAutoRecordArmDecision(
            bool shouldArm,
            bool trackedInActiveTree,
            bool newVesselIsEva)
        {
            if (!shouldArm)
                return PostSwitchAutoRecordArmDecision.None;

            // A tracked background member is an existing tree recording: switching back
            // to it should promote it to foreground on the first modification, whether
            // or not it is an EVA kerbal. (An EVA kerbal re-controlled after a vessel
            // switch lives here; without this it stays background-tracked and the
            // missed-vessel-switch recovery loops every frame, never promoting.)
            if (trackedInActiveTree)
                return PostSwitchAutoRecordArmDecision.ArmTrackedBackgroundMember;

            // Outsider (not in any tree): a fresh EVA kerbal is owned by the dedicated
            // OnCrewOnEva / EVA-branch + "Auto-record on EVA" path, so do not also arm
            // an outsider standalone recording for it. Non-EVA outsiders still arm.
            return newVesselIsEva
                ? PostSwitchAutoRecordArmDecision.None
                : PostSwitchAutoRecordArmDecision.ArmOutsider;
        }

        internal static PostSwitchAutoRecordSuppressionReason EvaluatePostSwitchAutoRecordSuppression(
            bool autoRecordOnFirstModificationAfterSwitchEnabled,
            bool isRecording,
            bool hasActiveVessel,
            uint activeVesselPid,
            uint armedVesselPid,
            bool activeVesselIsGhost,
            bool isRestoringActiveTree,
            bool hasPendingTransition,
            bool isWarpActive,
            bool activeVesselPacked)
        {
            if (!autoRecordOnFirstModificationAfterSwitchEnabled)
                return PostSwitchAutoRecordSuppressionReason.Disabled;
            if (isRecording)
                return PostSwitchAutoRecordSuppressionReason.AlreadyRecording;
            if (!hasActiveVessel)
                return PostSwitchAutoRecordSuppressionReason.NoActiveVessel;
            if (activeVesselPid == 0 || activeVesselPid != armedVesselPid)
                return PostSwitchAutoRecordSuppressionReason.ActiveVesselMismatch;
            if (activeVesselIsGhost)
                return PostSwitchAutoRecordSuppressionReason.GhostMapVessel;
            if (isRestoringActiveTree)
                return PostSwitchAutoRecordSuppressionReason.RestoreInProgress;
            if (hasPendingTransition)
                return PostSwitchAutoRecordSuppressionReason.PendingTransition;
            if (isWarpActive)
                return PostSwitchAutoRecordSuppressionReason.WarpActive;
            if (activeVesselPacked)
                return PostSwitchAutoRecordSuppressionReason.PackedOrOnRails;
            return PostSwitchAutoRecordSuppressionReason.None;
        }

        /// <summary>
        /// The post-switch module-cache INVALIDATION signal: the caches are stale when a
        /// module edit marked them dirty, or when the vessel part count no longer matches
        /// the count the caches were built from (undock / decouple / EVA). Extracted from
        /// <c>EvaluatePostSwitchAutoRecordTrigger</c> so the derivation itself is testable
        /// without a live Vessel; the call site is byte-identical to the inline expression.
        /// </summary>
        internal static bool NeedsPostSwitchModuleCacheRefresh(
            bool moduleCachesDirty,
            int cachedPartCount,
            int currentPartCount)
        {
            return moduleCachesDirty || cachedPartCount != currentPartCount;
        }

        internal static bool ShouldEvaluatePostSwitchManifestDiff(
            double currentUT,
            double nextManifestEvaluationUt,
            bool needsCacheRefresh)
        {
            return needsCacheRefresh || currentUT >= nextManifestEvaluationUt;
        }

        internal static string BuildPostSwitchAutoRecordDecisionStateKey(
            PostSwitchAutoRecordSuppressionReason suppression,
            bool baselineCaptured,
            bool waitingForSettle,
            PostSwitchAutoRecordTrigger trigger)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "suppression={0}|baseline={1}|settle={2}|trigger={3}",
                suppression,
                baselineCaptured ? 1 : 0,
                waitingForSettle ? 1 : 0,
                trigger);
        }

        internal static string BuildPostSwitchManifestDeltaStateKey(
            int crewDeltaKeys,
            int resourceDeltaKeys,
            int inventoryDeltaKeys,
            int partStateTokenDelta,
            bool crewChanged,
            bool resourceChanged,
            bool partStateChanged)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "crew={0}|resource={1}|inventory={2}|partState={3}|crewChanged={4}|resourceChanged={5}|partStateChanged={6}",
                FormatPostSwitchManifestDeltaCount(crewDeltaKeys),
                FormatPostSwitchManifestDeltaCount(resourceDeltaKeys),
                FormatPostSwitchManifestDeltaCount(inventoryDeltaKeys),
                FormatPostSwitchManifestDeltaCount(partStateTokenDelta),
                crewChanged ? 1 : 0,
                resourceChanged ? 1 : 0,
                partStateChanged ? 1 : 0);
        }

        private static string FormatPostSwitchManifestDeltaCount(int count)
        {
            return count == PostSwitchManifestDeltaSkipped
                ? "skipped"
                : count.ToString(CultureInfo.InvariantCulture);
        }

        internal static string FormatPostSwitchAutoRecordDecisionSummary(
            uint armedPid,
            uint activePid,
            bool baselineCaptured,
            PostSwitchAutoRecordSuppressionReason suppression,
            bool waitingForSettle,
            double comparisonsReadyUt,
            double currentUt,
            PostSwitchAutoRecordTrigger trigger)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Post-switch watch decision: pid={0} activePid={1} baseline={2} suppression={3} waitingForSettle={4} readyAt={5:F1} now={6:F1} trigger={7}",
                armedPid,
                activePid,
                baselineCaptured,
                suppression,
                waitingForSettle,
                comparisonsReadyUt,
                currentUt,
                trigger);
        }

        private static void LogPostSwitchAutoRecordDecision(
            PostSwitchAutoRecordState state,
            uint activePid,
            PostSwitchAutoRecordSuppressionReason suppression,
            bool waitingForSettle,
            double currentUt,
            PostSwitchAutoRecordTrigger trigger)
        {
            if (state == null)
                return;

            string stateKey = BuildPostSwitchAutoRecordDecisionStateKey(
                suppression,
                state.BaselineCaptured,
                waitingForSettle,
                trigger);
            ParsekLog.VerboseOnChange("Flight",
                "post-switch-watch-" + state.VesselPid.ToString(CultureInfo.InvariantCulture),
                stateKey,
                FormatPostSwitchAutoRecordDecisionSummary(
                    state.VesselPid,
                    activePid,
                    state.BaselineCaptured,
                    suppression,
                    waitingForSettle,
                    state.ComparisonsReadyUt,
                    currentUt,
                    trigger));
        }

        private bool HasPendingPostSwitchAutoRecordTransition()
        {
            return pendingTreeDockMerge
                || pendingSplitRecorder != null
                || pendingSplitInProgress
                || pendingBoardingTargetPid != 0
                || pendingDockMergedPid != 0
                || (recorder != null && recorder.ChainToVesselPending);
        }

        private void ArmPostSwitchAutoRecord(
            Vessel vessel,
            string armReason,
            bool trackedInActiveTree,
            string freshStartParentRecordingId)
        {
            if (vessel == null)
                return;

            postSwitchAutoRecord = new PostSwitchAutoRecordState
            {
                VesselPid = vessel.persistentId,
                VesselName = vessel.vesselName ?? "Unknown",
                ArmedAtUt = Planetarium.GetUniversalTime(),
                ArmedReason = armReason,
                TrackedInActiveTree = trackedInActiveTree,
                FreshStartParentRecordingId = freshStartParentRecordingId
            };

            ParsekLog.Info("Flight",
                $"Post-switch auto-record armed: vessel='{postSwitchAutoRecord.VesselName}' " +
                $"pid={postSwitchAutoRecord.VesselPid} tracked={trackedInActiveTree} " +
                $"reason={armReason}");
        }

        private void DisarmPostSwitchAutoRecord(string reason)
        {
            if (postSwitchAutoRecord == null)
                return;

            ParsekLog.Info("Flight",
                $"Post-switch auto-record disarmed: vessel='{postSwitchAutoRecord.VesselName}' " +
                $"pid={postSwitchAutoRecord.VesselPid} reason={reason}");
            postSwitchAutoRecord = null;
        }

        // ---------------------------------------------------------------------
        // Phase C: stock-action intent consume — validates an armed
        // StockActionIntentMarker on a confirmed focus change, picks one of
        // three entry branches (committed clone, BG member, standalone),
        // mutates the live tree through SwitchSegmentBuilder, arms a fresh
        // SwitchSegmentSession, clears the consumed marker. The
        // OnVesselSwitchComplete and OnFlightReady call sites disarm the
        // first-modification watcher on every Started* route so the watcher
        // doesn't fire a second immediate-start path. See
        // docs/dev/plans/segment-scoped-switch-fly-autorecord.md.
        // ---------------------------------------------------------------------

        /// <summary>
        /// 1:1 mapping from the stock UI action that armed the intent to the
        /// segment session's entry reason. The two enums are kept separate so
        /// the type system reflects "intent fired" vs "session started", but
        /// the value mapping is direct.
        /// </summary>
        internal static SwitchSegmentEntryReason MapIntentActionToEntryReason(StockActionType action)
        {
            switch (action)
            {
                case StockActionType.TrackingStationFly: return SwitchSegmentEntryReason.TrackingStationFly;
                case StockActionType.KscMarkerFly: return SwitchSegmentEntryReason.KscMarkerFly;
                case StockActionType.MapSwitchTo: return SwitchSegmentEntryReason.MapSwitchTo;
                default: return SwitchSegmentEntryReason.MapSwitchTo;
            }
        }

        /// <summary>
        /// Captures an initial boundary <see cref="TrajectoryPoint"/> for the
        /// just-activated vessel at the consume UT, delegating to the
        /// background recorder's well-tested absolute-point capture path.
        /// Returns a minimal stub when <paramref name="vessel"/> lacks a body
        /// (test fixtures, scene-load race) so the helper never throws.
        /// </summary>
        internal static TrajectoryPoint BuildSwitchSegmentBoundaryPoint(Vessel vessel, double ut)
        {
            if (vessel == null || vessel.mainBody == null)
            {
                return new TrajectoryPoint
                {
                    ut = ut,
                    bodyName = vessel?.mainBody?.name ?? "Unknown",
                    recordedGroundClearance = double.NaN,
                };
            }
            return BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(vessel, ut);
        }

        /// <summary>
        /// Phase C entry point: validates an armed
        /// <see cref="StockActionIntentMarker"/>, dispatches to one of three
        /// branch routines (committed-tree clone, BG-member continuation,
        /// standalone), or refuses with a logged reason. The caller is
        /// responsible for disarming the first-modification watcher on every
        /// <see cref="SwitchSegmentEntryRoute.StartedCommittedSpawnedClone"/>
        /// / <see cref="SwitchSegmentEntryRoute.StartedBgMemberContinuation"/>
        /// / <see cref="SwitchSegmentEntryRoute.StartedStandalone"/>
        /// / <see cref="SwitchSegmentEntryRoute.FellThroughBgLookupFailed"/> route.
        ///
        /// <para>Plan §"Behavior by Entry Path", §"Segment Creation" steps 2 and 9,
        /// and §"Two rapid switches semantics".</para>
        /// </summary>
        internal StockActionIntentConsumeResult TryConsumeStockActionIntent(Vessel newVessel)
        {
            var result = new StockActionIntentConsumeResult
            {
                Route = SwitchSegmentEntryRoute.NoIntent,
                DiagnosticReason = null,
            };
            var scenario = ParsekScenario.Instance;
            if (scenario == null)
                return result;

            StockActionIntentMarker marker = scenario.CurrentStockActionIntent;
            if (marker == null)
                return result;

            if (newVessel == null)
            {
                // Defensive: a null newVessel cannot match any target. Clear
                // with a logged reason so the marker doesn't survive into a
                // later spurious consume.
                // MED 6 (PR #876 review): use the dedicated Refused_NullVessel
                // route instead of Refused_TargetMismatch so log/test
                // observers can distinguish "no vessel at all" from
                // "wrong vessel" (which is a real PID divergence diagnostic).
                scenario.ClearStockActionIntent("consume-null-vessel");
                ParsekLog.Info("SwitchIntent",
                    $"refused: reason=consume-null-vessel intentId={marker.IntentId:D} " +
                    $"action={marker.Action} markerTargetPid={marker.TargetVesselPersistentId}");
                result.Route = SwitchSegmentEntryRoute.Refused_NullVessel;
                result.DiagnosticReason = "consume-null-vessel";
                return result;
            }

            uint newPid = newVessel.persistentId;

            bool missedSwitchRecoveryActive =
                currentVesselSwitchRecoveryDiagnosticContext.IsRecovery;

            uint activeSessionFocusedPid =
                scenario.ActiveSwitchSegmentSession?.FocusedVesselPersistentId ?? 0u;

            float currentRealtime = Time.realtimeSinceStartup;
            double currentUT = Planetarium.fetch != null
                ? Planetarium.GetUniversalTime()
                : marker.CapturedUT;

            // On-surface targets defer to the normal auto-record trigger instead
            // of starting a switch-fly segment the instant you fly to them. A
            // vessel that is PRELAUNCH (on the pad), LANDED (on the ground), or
            // SPLASHED (on the water) has not started flying yet. Without the
            // LANDED/SPLASHED arms, flying to a vessel already sitting on the
            // ground (a Parsek-spawned committed vessel, reported LANDED, or a
            // saved landed craft) started a recording immediately, which was then
            // discarded as idle-on-pad on scene exit — the "Jumping Flea on the
            // pad" report whose PRELAUNCH-only fix missed the LANDED case.
            bool targetIsOnSurface =
                newVessel.situation == Vessel.Situations.PRELAUNCH
                || newVessel.situation == Vessel.Situations.LANDED
                || newVessel.situation == Vessel.Situations.SPLASHED;

            var outcome = StockActionIntentConsumeDecision.Evaluate(
                marker,
                newPid,
                ParsekProcess.ProcessSessionId,
                currentRealtime,
                currentUT,
                missedSwitchRecoveryActive,
                activeSessionFocusedPid,
                targetIsOnSurface);

            if (outcome != StockActionIntentConsumeDecision.Outcome.Authorized
                && outcome != StockActionIntentConsumeDecision.Outcome.NoIntent)
            {
                string clearReason = StockActionIntentConsumeDecision.ClearReasonFor(outcome);
                string diag = StockActionIntentConsumeDecision.FormatRefusalDiagnostic(
                    outcome, marker, newPid);
                ParsekLog.Info("SwitchIntent", diag);
                if (!string.IsNullOrEmpty(clearReason))
                    scenario.ClearStockActionIntent(clearReason);
                result.Route = StockActionIntentConsumeDecision.RouteForRefusal(outcome);
                result.DiagnosticReason = clearReason;
                return result;
            }

            if (outcome == StockActionIntentConsumeDecision.Outcome.NoIntent)
                return result;

            // Authorized — pick the entry branch. Re-snapshot context now,
            // after the decision predicate, so any rapid second click that
            // raced the consume site still gets the up-to-date activeTree.
            double switchUT = currentUT;
            uint sourcePid = TryResolvePreSwitchVesselPid(newPid);

            // Two-rapid-switches different-target: close prior session.
            var priorSession = scenario.ActiveSwitchSegmentSession;
            if (priorSession != null
                && priorSession.FocusedVesselPersistentId != newPid)
            {
                ParsekLog.Info("SwitchSegment",
                    $"superseded-by-new-switch: prior sessionId={priorSession.SessionId:D} " +
                    $"priorFocusedPid={priorSession.FocusedVesselPersistentId} " +
                    $"newFocusedPid={newPid}");
                scenario.ClearSwitchSegmentSession("superseded-by-new-switch");
            }

            // Branch selection. Order matters: the committed-spawned-vessel
            // clone path takes precedence (plan §"Fly / Switch-To a committed
            // spawned vessel"), then BG-member (§"Goal 4"), then standalone
            // (§"Goal 5"). The clone branch may install activeTree if it
            // hasn't run yet for this scene.
            if (TryRouteCommittedSpawnedClone(newVessel, marker, switchUT, sourcePid, out result))
                return result;

            // After the clone branch refuses, activeTree may still be null
            // (e.g. unrelated vessel with no committed match). The BG-member
            // branch only fires when there IS a live activeTree containing
            // the focused PID in BackgroundMap.
            if (activeTree != null
                && activeTree.BackgroundMap != null
                && activeTree.BackgroundMap.ContainsKey(newPid))
            {
                return StartBgMemberContinuationSegment(newVessel, marker, switchUT, sourcePid);
            }

            return StartStandaloneContinuationSegment(newVessel, marker, switchUT, sourcePid,
                fallthroughFromBgLookup: false,
                fallthroughReason: null);
        }

        /// <summary>
        /// Best-effort pre-switch vessel PID. For Map Switch-To the prior
        /// active recorder still owns the source vessel; for TS Fly / KSC Fly
        /// we're entering FLIGHT fresh and no source exists, so 0 is returned.
        /// </summary>
        private uint TryResolvePreSwitchVesselPid(uint newPid)
        {
            if (recorder != null && recorder.RecordingVesselId != 0
                && recorder.RecordingVesselId != newPid)
            {
                return recorder.RecordingVesselId;
            }
            if (activeTree != null && !string.IsNullOrEmpty(activeTree.ActiveRecordingId))
            {
                Recording active;
                if (activeTree.Recordings.TryGetValue(activeTree.ActiveRecordingId, out active)
                    && active != null
                    && active.VesselPersistentId != 0
                    && active.VesselPersistentId != newPid)
                {
                    return active.VesselPersistentId;
                }
            }
            return 0u;
        }

        /// <summary>
        /// Attempts the committed-spawned-vessel clone branch (plan §"Fly /
        /// Switch-To a committed spawned vessel"). Returns true and writes
        /// <paramref name="result"/> when this branch fires (whether a segment
        /// is started or the helper refuses with a logged reason that should
        /// NOT fall through to BG / standalone). Returns false when the
        /// vessel does not match any committed tree — caller continues to the
        /// BG / standalone branches.
        ///
        /// <para>The #866 copy-on-write clone path may not have run yet (e.g.
        /// when the consume site fires from <see cref="OnVesselSwitchComplete"/>
        /// in a Map Switch-To inside the same FLIGHT scene). When activeTree
        /// is null and a committed tree matches the focused vessel, this
        /// helper invokes <see cref="TryRestoreCommittedTreeForSpawnedActiveVessel"/>
        /// to install the clone, then attaches the continuation under it.</para>
        /// </summary>
        private bool TryRouteCommittedSpawnedClone(
            Vessel newVessel,
            StockActionIntentMarker marker,
            double switchUT,
            uint sourcePid,
            out StockActionIntentConsumeResult result)
        {
            result = default;
            uint newPid = newVessel.persistentId;

            // Path A: activeTree is already the #866 clone (e.g. consume fires
            // on OnFlightReady after restore installed it). Detect by tree
            // identity matching a committed tree's id but recordings list
            // matching the live mutable state.
            bool activeIsCommittedClone = activeTree != null
                && !string.IsNullOrEmpty(activeTree.Id)
                && RecordingStore.CommittedTrees != null
                && IsTreeIdInCommittedTrees(activeTree.Id);

            // Path B: activeTree is null but a committed tree matches the
            // focused PID. Pull the #866 clone through the same code path
            // the FlightReady seam uses (TryRestoreCommittedTreeForSpawnedActiveVessel)
            // and then attach the continuation.
            //
            // Bug 5 (post-#876 playtest 2026-05-17): the PID match has two
            // shapes — direct VesselPersistentId match (an already-live
            // committed vessel) and SpawnedVesselPersistentId match (a
            // Parsek-spawned vessel whose live PID was minted at spawn time
            // and stored on the committed recording by the spawn-at-end
            // pipeline). Before this fix, the lookup checked only
            // VesselPersistentId, so a Switch-To on a Parsek-spawned vessel
            // fell through to standalone routing — wrong root cause for the
            // segment created during the bug 5 playtest. We now also check
            // SpawnedVesselPersistentId. The downstream
            // TryRestoreCommittedTreeForSpawnedActiveVessel /
            // TryFindCommittedTreeForSpawnedVessel pipeline already keys off
            // SpawnedVesselPersistentId, so the restore + clone path is
            // unchanged once we route this way.
            if (!activeIsCommittedClone && activeTree == null)
            {
                bool committedMatchExists = TryFindCommittedTreeMatchingVessel(
                    newPid,
                    newVessel.id != Guid.Empty ? newVessel.id.ToString("N") : null);
                if (committedMatchExists)
                {
                    // L5 (PR #876 final review): only emit the
                    // `route=committed-spawned-clone` success log AFTER the
                    // restore actually succeeds. Previously the route log
                    // fired before the call and could be followed by
                    // `committed-spawned-clone-restore-failed-start-standalone`,
                    // making KSP.log readers see an aspirational intermediate
                    // route that never took effect. Emit the failure log
                    // directly on the restore-failure path without an earlier
                    // route line so the FINAL route is always the only one
                    // logged.
                    bool restored = TryRestoreCommittedTreeForSpawnedActiveVessel(
                        callerBuildsOwnSegment: true);
                    if (!restored || activeTree == null)
                    {
                        ParsekLog.Warn("SwitchSegment",
                            $"committed-spawned-clone-restore-failed-start-standalone " +
                            $"focusedPid={newPid} intentId={marker.IntentId:D}");
                        result = StartStandaloneContinuationSegment(
                            newVessel, marker, switchUT, sourcePid,
                            fallthroughFromBgLookup: false,
                            fallthroughReason: "committed-clone-restore-failed");
                        return true;
                    }
                    ParsekLog.Info("SwitchSegment",
                        $"route=committed-spawned-clone reason=focused-pid-matched-committed-tree " +
                        $"pid={newPid} intentId={marker.IntentId:D}");
                    activeIsCommittedClone = true;
                }
            }

            if (!activeIsCommittedClone)
                return false;

            // Path A after a restore that already continued in a new segment (the 1 Hz
            // restore retry can run before OnFlightReady's consume): the click takes over
            // that segment instead of stacking a second one under it.
            if (TryAdoptResumedSpawnSegmentForIntent(newVessel, marker, switchUT, sourcePid, out result))
                return true;

            // Walk to the terminal-leaf parent inside the clone (the resolver
            // is happy to walk any tree; the clone shares structure with the
            // committed parent except for the in-flight mutations).
            var resolution = SwitchSegmentBuilder.ResolveSwitchContinuationParent(
                activeTree, newPid);
            string parentRecId = resolution.Status
                == SwitchContinuationParentStatus.UniqueTerminalLeafFound
                    ? resolution.TerminalLeafRecordingId
                    : null;

            Guid sessionId = Guid.NewGuid();
            string newRecordingId = Guid.NewGuid().ToString("D",
                CultureInfo.InvariantCulture);
            string newBranchPointId = Guid.NewGuid().ToString("D",
                CultureInfo.InvariantCulture);

            // Capture prior ActiveRecordingId BEFORE the helper mutates it so
            // BindLiveRecorderToSwitchSegment can flush a prior live recorder
            // (path A or B's restore left one bound to the parent leaf) into
            // the parent recording, not the new continuation.
            string priorActiveRecordingId = activeTree.ActiveRecordingId;

            // LOW 10 (PR #876 review): focusedRootPartPid parameter removed
            // — see SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            // VesselName resolution (#autoLOC token -> "Jumping Flea") is
            // centralized inside SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            var creation = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                activeTree,
                parentRecId,
                newPid,
                newVessel.vesselName ?? "Unknown",
                switchUT,
                MapIntentActionToEntryReason(marker.Action),
                marker.IntentId,
                sessionId,
                newRecordingId,
                newBranchPointId,
                ut => BuildSwitchSegmentBoundaryPoint(newVessel, ut),
                sourcePid);

            if (!creation.Created)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"committed-clone-creation-refused-start-standalone " +
                    $"failureReason={creation.FailureReason ?? "<null>"} " +
                    $"focusedPid={newPid} intentId={marker.IntentId:D}");
                result = StartStandaloneContinuationSegment(
                    newVessel, marker, switchUT, sourcePid,
                    fallthroughFromBgLookup: false,
                    fallthroughReason: "committed-clone-helper-refused");
                return true;
            }

            // Phase C.1: bind the live recorder to the new continuation
            // recording id so trajectory samples flow into it immediately.
            BindLiveRecorderToSwitchSegment(
                newVessel, priorActiveRecordingId, newRecordingId,
                routeTag: "committed-spawned-clone", switchUT: switchUT);

            ArmSwitchSegmentSessionForRoute(
                marker, sessionId, activeTree.Id, parentRecId,
                newRecordingId, switchUT, newPid, sourcePid,
                committedTreeId: activeTree.Id,
                entryRoute: SwitchSegmentEntryRoute.StartedCommittedSpawnedClone);

            ParsekScenario.Instance.ClearStockActionIntent("consumed-into-segment");
            ParsekLog.Info("SwitchSegment",
                $"route=committed-spawned-clone intentId={marker.IntentId:D} " +
                $"sessionId={sessionId:D} treeId={activeTree.Id ?? "<null>"} " +
                $"parentRecId={parentRecId ?? "<standalone>"} segmentRecId={newRecordingId} " +
                $"sourcePid={sourcePid} focusedPid={newPid}");

            result = new StockActionIntentConsumeResult
            {
                Route = SwitchSegmentEntryRoute.StartedCommittedSpawnedClone,
                NewRecordingId = newRecordingId,
                NewBranchPointId = newBranchPointId,
                SessionId = sessionId,
                DiagnosticReason = "consumed-into-segment",
            };
            return true;
        }

        /// <summary>
        /// Pure: true when <paramref name="session"/> is the session
        /// <see cref="TryContinueRestoredSpawnInNewSegment"/> armed for the live tree's
        /// current active recording on <paramref name="focusedPid"/>, so a stock Fly /
        /// Switch-To consumed for the same vessel adopts that segment.
        /// </summary>
        internal static bool IsAdoptableResumedSpawnSession(
            SwitchSegmentSession session,
            string activeTreeId,
            string activeRecordingId,
            uint focusedPid)
        {
            return session != null
                && session.EntryReason == SwitchSegmentEntryReason.ResumedSpawnedVessel
                && focusedPid != 0
                && session.FocusedVesselPersistentId == focusedPid
                && !string.IsNullOrEmpty(activeTreeId)
                && string.Equals(session.TreeId, activeTreeId, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(activeRecordingId)
                && string.Equals(session.ActiveSegmentRecordingId, activeRecordingId,
                    StringComparison.Ordinal);
        }

        private bool TryAdoptResumedSpawnSegmentForIntent(
            Vessel newVessel,
            StockActionIntentMarker marker,
            double switchUT,
            uint sourcePid,
            out StockActionIntentConsumeResult result)
        {
            result = default;
            ParsekScenario scenario = ParsekScenario.Instance;
            SwitchSegmentSession session = scenario?.ActiveSwitchSegmentSession;
            if (newVessel == null || activeTree == null
                || !IsAdoptableResumedSpawnSession(
                    session, activeTree.Id, activeTree.ActiveRecordingId, newVessel.persistentId))
            {
                return false;
            }

            string segmentId = activeTree.ActiveRecordingId;
            string branchPointId = null;
            if (activeTree.Recordings.TryGetValue(segmentId, out Recording segmentRec)
                && segmentRec != null)
            {
                branchPointId = segmentRec.ParentBranchPointId;
            }

            // Same session id (the segment carries it as its SwitchSegmentSessionId stamp),
            // now owned by the stock click.
            scenario.ArmSwitchSegmentSession(new SwitchSegmentSession
            {
                SessionId = session.SessionId,
                TreeId = session.TreeId,
                ParentRecordingId = session.ParentRecordingId,
                ActiveSegmentRecordingId = session.ActiveSegmentRecordingId,
                SourceVesselPersistentId = sourcePid,
                FocusedVesselPersistentId = session.FocusedVesselPersistentId,
                SwitchUT = session.SwitchUT,
                EntryReason = MapIntentActionToEntryReason(marker.Action),
                IntentId = marker.IntentId,
                CommittedTreeId = session.CommittedTreeId,
                PreSessionBranchPointIds = session.PreSessionBranchPointIds != null
                    ? new List<string>(session.PreSessionBranchPointIds)
                    : new List<string>(),
            });
            scenario.ClearStockActionIntent("consumed-into-segment");
            ParsekLog.Info("SwitchSegment",
                $"route=committed-spawned-clone adopted-resumed-spawn-segment " +
                $"intentId={marker.IntentId:D} sessionId={session.SessionId:D} " +
                $"treeId={activeTree.Id ?? "<null>"} parentRecId={session.ParentRecordingId ?? "<standalone>"} " +
                $"segmentRecId={segmentId} sourcePid={sourcePid} focusedPid={newVessel.persistentId} " +
                $"switchUT={switchUT.ToString("R", CultureInfo.InvariantCulture)}");

            result = new StockActionIntentConsumeResult
            {
                Route = SwitchSegmentEntryRoute.StartedCommittedSpawnedClone,
                NewRecordingId = segmentId,
                NewBranchPointId = branchPointId,
                SessionId = session.SessionId,
                DiagnosticReason = "consumed-into-segment",
            };
            return true;
        }

        private static bool IsTreeIdInCommittedTrees(string treeId)
        {
            if (string.IsNullOrEmpty(treeId)) return false;
            var trees = RecordingStore.CommittedTrees;
            if (trees == null) return false;
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i] != null
                    && string.Equals(trees[i].Id, treeId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Bug 5 (post-#876 playtest 2026-05-17): probes both the direct
        /// <see cref="Recording.VesselPersistentId"/> (live committed vessel)
        /// and <see cref="Recording.SpawnedVesselPersistentId"/> (Parsek-
        /// spawned vessel whose live PID was minted at spawn time and stored
        /// on the committed recording by the spawn-at-end pipeline). The
        /// downstream <see cref="TryRestoreCommittedTreeForSpawnedActiveVessel"/>
        /// pipeline already keys off <c>SpawnedVesselPersistentId</c> via
        /// <see cref="TryFindCommittedTreeForSpawnedVessel"/>, so a positive
        /// answer here lands in the same restore + clone path regardless of
        /// which field matched.
        ///
        /// <para>Before this fix, the lookup checked only
        /// <c>VesselPersistentId</c>, so a Switch-To on a Parsek-spawned
        /// vessel fell through to standalone routing rather than the
        /// committed-clone branch.</para>
        /// </summary>
        internal static bool TryFindCommittedTreeMatchingVessel(uint pid)
            => TryFindCommittedTreeMatchingVessel(pid, null, out _);

        // liveGuid is the focused live vessel's Vessel.id ("N" form, null = unknown). #976-class:
        // the VesselPersistentId branch matches the craft-baked pid, which a relaunch of the same
        // craft reuses, so it additionally requires the same launch (guid). The SpawnedVesselPersistentId
        // branch goes through VesselLaunchIdentity.LiveVesselIsRecordedSpawn: a genuine Parsek
        // spawn pid is KSP-unique and matches on the pid alone, but an adoption stamp
        // (spawned pid == recorded pid) is the craft-baked pid again and needs the same launch.
        // A null/unknown guid falls back to today's pid-only behavior.
        internal static bool TryFindCommittedTreeMatchingVessel(uint pid, string liveGuid)
            => TryFindCommittedTreeMatchingVessel(pid, liveGuid, out _);

        /// <summary>
        /// Single implementation backing both bool overloads. Returns the
        /// matched committed <see cref="RecordingTree"/> via
        /// <paramref name="matchedTree"/> (null when no match), and the bool
        /// result is exactly <c>matchedTree != null</c>. The matching semantics
        /// are identical to the bool overloads: a direct
        /// <see cref="Recording.VesselPersistentId"/> match (gated by
        /// <see cref="VesselLaunchIdentity.LiveVesselIsRecordedLaunch"/> so a
        /// relaunch of the same craft does not collide on the baked pid), plus
        /// the <see cref="Recording.VesselSpawned"/> +
        /// <see cref="Recording.SpawnedVesselPersistentId"/> match through
        /// <see cref="VesselLaunchIdentity.LiveVesselIsRecordedSpawn"/> (a
        /// genuine Parsek spawn pid is KSP-unique; an adoption stamp is the
        /// craft-baked pid and also needs the same launch guid). The first
        /// matching tree wins.
        ///
        /// <para>The out-param overload lets the Map Switch-To Prefix compare
        /// the matched committed tree id against the live active tree id, so a
        /// re-select of the vessel you are already flying (its own committed
        /// clone) does not trigger a pre-switch dialog.</para>
        /// </summary>
        internal static bool TryFindCommittedTreeMatchingVessel(
            uint pid, string liveGuid, out RecordingTree matchedTree)
        {
            matchedTree = null;
            if (pid == 0u) return false;
            var trees = RecordingStore.CommittedTrees;
            if (trees == null) return false;
            int refusedOtherLaunchStamps = 0;
            for (int i = 0; i < trees.Count; i++)
            {
                var tree = trees[i];
                if (tree == null || tree.Recordings == null) continue;
                foreach (var rec in tree.Recordings.Values)
                {
                    if (rec == null) continue;
                    if (rec.VesselPersistentId == pid
                        && VesselLaunchIdentity.LiveVesselIsRecordedLaunch(rec, pid, liveGuid))
                    {
                        matchedTree = tree;
                        LogRefusedOtherLaunchSpawnStamps(
                            "TryFindCommittedTreeMatchingVessel", pid, liveGuid, refusedOtherLaunchStamps);
                        return true;
                    }
                    // Bug 5: also match recordings whose Parsek-spawned vessel
                    // is the focused one. SpawnedVesselPersistentId is set by
                    // the spawn-at-end pipeline at the new live PID minted on
                    // spawn; VesselPersistentId still carries the original
                    // recording PID.
                    if (IsCommittedSpawnStampMatch(rec, pid, liveGuid))
                    {
                        matchedTree = tree;
                        LogRefusedOtherLaunchSpawnStamps(
                            "TryFindCommittedTreeMatchingVessel", pid, liveGuid, refusedOtherLaunchStamps);
                        return true;
                    }
                    if (IsOtherLaunchSpawnStamp(rec, pid, liveGuid))
                        refusedOtherLaunchStamps++;
                }
            }
            LogRefusedOtherLaunchSpawnStamps(
                "TryFindCommittedTreeMatchingVessel", pid, liveGuid, refusedOtherLaunchStamps);
            return false;
        }

        /// <summary>
        /// Does the committed recording's spawn stamp name this live vessel? Requires
        /// <see cref="Recording.VesselSpawned"/> and
        /// <see cref="VesselLaunchIdentity.SpawnStampHeldBy"/>: a genuine Parsek spawn pid
        /// matches on the pid alone, an adoption stamp (the craft-baked pid) only when the
        /// launch guids do not conclusively differ or the live guid is linked to the
        /// recording's (a resumed spawned vessel), so a relaunch of the same craft never
        /// resumes the older launch's committed tree.
        /// </summary>
        internal static bool IsCommittedSpawnStampMatch(Recording rec, uint livePid, string liveGuid)
        {
            if (rec == null || !rec.VesselSpawned || livePid == 0u) return false;
            return VesselLaunchIdentity.SpawnStampHeldBy(rec, livePid, liveGuid);
        }

        /// <summary>
        /// Pure: the refusal half of <see cref="IsCommittedSpawnStampMatch"/>, counted for the
        /// summary line: the spawn pid equals the live pid but the stamp is an adoption stamp of
        /// a conclusively different launch.
        /// </summary>
        internal static bool IsOtherLaunchSpawnStamp(Recording rec, uint livePid, string liveGuid)
        {
            return rec != null
                && rec.VesselSpawned
                && livePid != 0u
                && rec.SpawnedVesselPersistentId == livePid
                && !VesselLaunchIdentity.SpawnStampHeldBy(rec, livePid, liveGuid);
        }

        private static void LogRefusedOtherLaunchSpawnStamps(
            string site, uint pid, string liveGuid, int refused)
        {
            if (refused <= 0) return;
            // Re-reached by the Update retry of the committed-tree restore: rate-limited,
            // with the live identity and the count in the key so a change prints at once.
            ParsekLog.InfoRateLimited("Flight",
                site + "|other-launch-stamp|" + pid.ToString(CultureInfo.InvariantCulture)
                    + "|" + (liveGuid ?? "(none)") + "|" + refused.ToString(CultureInfo.InvariantCulture),
                $"{site}: refused {refused.ToString(CultureInfo.InvariantCulture)} adoption-stamped " +
                $"recording(s) with spawn pid={pid.ToString(CultureInfo.InvariantCulture)} " +
                $"liveGuid={liveGuid ?? "(none)"}: another launch of the craft reuses the baked pid");
        }

        /// <summary>
        /// Goal-4 branch (plan §"Fly / Switch-To a vessel that is a tracked
        /// background member"). Flushes the BG parent via
        /// <see cref="BackgroundRecorder.OnVesselRemovedFromBackground"/>,
        /// resolves the terminal-leaf parent, attaches the continuation, and
        /// removes the focused vessel from
        /// <see cref="RecordingTree.BackgroundMap"/>. On any failure, falls
        /// through to <see cref="StartStandaloneContinuationSegment"/> with a
        /// <c>bg-lookup-failed-start-standalone</c> warn log.
        /// </summary>
        private StockActionIntentConsumeResult StartBgMemberContinuationSegment(
            Vessel newVessel,
            StockActionIntentMarker marker,
            double switchUT,
            uint sourcePid)
        {
            uint newPid = newVessel.persistentId;
            var scenario = ParsekScenario.Instance;

            // 1. PID-to-recording-id lookup inside activeTree.
            string bgRecId;
            if (activeTree.BackgroundMap == null
                || !activeTree.BackgroundMap.TryGetValue(newPid, out bgRecId)
                || string.IsNullOrEmpty(bgRecId))
            {
                ParsekLog.Warn("SwitchSegment",
                    $"bg-lookup-failed-start-standalone reason=bg-entry-missing " +
                    $"focusedPid={newPid} intentId={marker.IntentId:D}");
                return StartStandaloneContinuationSegment(newVessel, marker, switchUT,
                    sourcePid, fallthroughFromBgLookup: true,
                    fallthroughReason: "bg-entry-missing");
            }

            Recording bgRec;
            if (activeTree.Recordings == null
                || !activeTree.Recordings.TryGetValue(bgRecId, out bgRec)
                || bgRec == null)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"bg-lookup-failed-start-standalone reason=recording-missing " +
                    $"focusedPid={newPid} bgRecId={bgRecId} intentId={marker.IntentId:D}");
                return StartStandaloneContinuationSegment(newVessel, marker, switchUT,
                    sourcePid, fallthroughFromBgLookup: true,
                    fallthroughReason: "recording-missing");
            }

            if (bgRec.VesselPersistentId != newPid)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"bg-lookup-failed-start-standalone reason=pid-collision " +
                    $"focusedPid={newPid} bgRecId={bgRecId} " +
                    $"recPid={bgRec.VesselPersistentId} intentId={marker.IntentId:D}");
                return StartStandaloneContinuationSegment(newVessel, marker, switchUT,
                    sourcePid, fallthroughFromBgLookup: true,
                    fallthroughReason: "pid-collision");
            }

            // 2. Plan §"Segment Creation" step 2: close+flush BG parent at switchUT.
            // The public OnVesselRemovedFromBackground samples the current UT
            // internally; that is acceptable here because consume runs in the
            // same frame as the vessel switch.
            try
            {
                backgroundRecorder?.OnVesselRemovedFromBackground(newPid);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"bg-flush-failed-start-standalone focusedPid={newPid} " +
                    $"intentId={marker.IntentId:D} ex={ex.Message}");
                return StartStandaloneContinuationSegment(newVessel, marker, switchUT,
                    sourcePid, fallthroughFromBgLookup: true,
                    fallthroughReason: "bg-flush-threw");
            }

            // 2b. GS3-NUDGE-DROPS-UNFINISHED-FLIGHT: the BG close above flushes
            // and boundary-samples but never CLASSIFIES a terminal, unlike every
            // ordinary background close (EndDebrisRecording, deferred destruction,
            // scene-exit finalization). A recording must not end terminal-less
            // because the player switched onto it.
            StampTerminalOnSwitchClosedBgOrigin(bgRec, newVessel, newPid, switchUT);

            // 3. Resolve terminal leaf in the active tree (walks from any PID
            // match — the BG recording itself or any downstream continuation).
            var resolution = SwitchSegmentBuilder.ResolveSwitchContinuationParent(
                activeTree, newPid, focusedVesselRecordingIdHint: null);
            string parentRecId = resolution.Status
                == SwitchContinuationParentStatus.UniqueTerminalLeafFound
                    ? resolution.TerminalLeafRecordingId
                    : null;

            if (resolution.Status == SwitchContinuationParentStatus.AmbiguousStartStandalone)
            {
                // Ambiguous-match: resolver already logged Warn with candidates.
                // Per plan §"Parent Selection Risk" item 4, start standalone.
                return StartStandaloneContinuationSegment(newVessel, marker, switchUT,
                    sourcePid, fallthroughFromBgLookup: false,
                    fallthroughReason: "ambiguous-parent-bg-fallthrough");
            }

            Guid sessionId = Guid.NewGuid();
            string newRecordingId = Guid.NewGuid().ToString("D",
                CultureInfo.InvariantCulture);
            string newBranchPointId = Guid.NewGuid().ToString("D",
                CultureInfo.InvariantCulture);

            // Capture prior ActiveRecordingId before CreateSwitchContinuationSegment
            // mutates it (BG-member entry typically has activeTree.ActiveRecordingId
            // already null — the parent was backgrounded — but be defensive).
            string priorActiveRecordingId = activeTree.ActiveRecordingId;

            // LOW 10 (PR #876 review): focusedRootPartPid parameter removed
            // — see SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            // VesselName resolution (#autoLOC token -> "Jumping Flea") is
            // centralized inside SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            var creation = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                activeTree,
                parentRecId,
                newPid,
                newVessel.vesselName ?? "Unknown",
                switchUT,
                MapIntentActionToEntryReason(marker.Action),
                marker.IntentId,
                sessionId,
                newRecordingId,
                newBranchPointId,
                ut => BuildSwitchSegmentBoundaryPoint(newVessel, ut),
                sourcePid);

            if (!creation.Created)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"continuation-refused-start-standalone failureReason={creation.FailureReason ?? "<null>"} " +
                    $"focusedPid={newPid} intentId={marker.IntentId:D}");
                return StartStandaloneContinuationSegment(newVessel, marker, switchUT,
                    sourcePid, fallthroughFromBgLookup: false,
                    fallthroughReason: "helper-refused");
            }

            // 4. Plan §"Segment Creation" step 9: remove from BG map AFTER the
            // flush so any concurrent observer of BackgroundMap still saw the
            // entry during the close.
            activeTree.BackgroundMap.Remove(newPid);

            // Phase C.1: bind the live recorder to the new continuation
            // recording id so trajectory samples flow into it immediately.
            BindLiveRecorderToSwitchSegment(
                newVessel, priorActiveRecordingId, newRecordingId,
                routeTag: "bg-member-continuation", switchUT: switchUT);

            ArmSwitchSegmentSessionForRoute(
                marker, sessionId, activeTree.Id, parentRecId,
                newRecordingId, switchUT, newPid, sourcePid,
                committedTreeId: null,
                entryRoute: SwitchSegmentEntryRoute.StartedBgMemberContinuation);

            scenario.ClearStockActionIntent("consumed-into-segment");

            ParsekLog.Info("SwitchSegment",
                $"route=bg-member-continuation intentId={marker.IntentId:D} " +
                $"sessionId={sessionId:D} treeId={activeTree.Id ?? "<null>"} " +
                $"parentRecId={parentRecId ?? "<standalone>"} segmentRecId={newRecordingId} " +
                $"bgRecId={bgRecId} sourcePid={sourcePid} focusedPid={newPid}");

            return new StockActionIntentConsumeResult
            {
                Route = SwitchSegmentEntryRoute.StartedBgMemberContinuation,
                NewRecordingId = newRecordingId,
                NewBranchPointId = newBranchPointId,
                SessionId = sessionId,
                DiagnosticReason = "consumed-into-segment",
            };
        }

        /// <summary>
        /// GS3-NUDGE-DROPS-UNFINISHED-FLIGHT, half 1 (the DATA half). Live-side
        /// wrapper around the pure
        /// <see cref="SwitchSegmentBuilder.ClassifySwitchCloseTerminalStamp"/>
        /// decision: when a stock Fly / Switch-To click consumes a BG-recorded
        /// member, <see cref="BackgroundRecorder.OnVesselRemovedFromBackground(uint)"/>
        /// flushes and boundary-samples the member's recording but never
        /// classifies a terminal state — unlike <c>EndDebrisRecording</c>, the
        /// deferred-destruction path, and scene-exit finalization, which all end
        /// with "apply the finalization cache, else classify from the live
        /// vessel". This reuses that exact pair rather than inventing a parallel
        /// classification, so a recording never ends terminal-less merely because
        /// the player glanced at it.
        /// <para>Ordering contract: call AFTER the BG close (the flush seals the
        /// authored data and refreshes the finalization cache) and BEFORE the
        /// continuation segment is attached.</para>
        /// </summary>
        private void StampTerminalOnSwitchClosedBgOrigin(
            Recording bgRec,
            Vessel focusedVessel,
            uint focusedPid,
            double switchUT)
        {
            var decision = SwitchSegmentBuilder.ClassifySwitchCloseTerminalStamp(bgRec);
            if (decision != SwitchCloseTerminalStampDecision.Stamp)
            {
                ParsekLog.Verbose("SwitchSegment",
                    $"origin-terminal-stamp skipped decision={decision} " +
                    $"recId={bgRec?.RecordingId ?? "<none>"} focusedPid={focusedPid} " +
                    $"terminal={bgRec?.TerminalStateValue?.ToString() ?? "<none>"}");
                return;
            }

            string source = "live";
            if (backgroundRecorder != null)
            {
                try
                {
                    // PR #1427 review F1: peek the cache's verdict BEFORE applying
                    // it. This site is the only background close that knows the
                    // vessel is alive (the player just switched TO it), so a
                    // PREDICTED Destroyed from a ballistic-extrapolator cache must
                    // be vetoed here — the scoping clamp pulls it back to switchUT,
                    // so the applier's RejectedTerminalBeforeLastSample guard cannot.
                    TerminalState? cacheTerminal;
                    backgroundRecorder.TryPeekFinalizationCacheTerminalForBackgroundEnd(
                        bgRec, focusedPid, switchUT, out cacheTerminal);

                    var cacheDecision = SwitchSegmentBuilder.ClassifySwitchCloseCacheApply(
                        focusedVessel != null, cacheTerminal);

                    if (cacheDecision == SwitchCloseCacheApplyDecision.SkipPredictedDestroyedWhileAlive)
                    {
                        ParsekLog.Verbose("SwitchSegment",
                            $"origin-terminal-stamp cache-skipped " +
                            $"reason=predicted-destroyed-while-alive " +
                            $"rejectedCacheTerminal={cacheTerminal?.ToString() ?? "<none>"} " +
                            $"recId={bgRec.RecordingId ?? "<no-id>"} focusedPid={focusedPid} " +
                            $"ut={switchUT.ToString("R", CultureInfo.InvariantCulture)} " +
                            "— falling through to live classification");
                    }
                    else
                    {
                        RecordingFinalizationCacheApplyResult cacheResult;
                        if (backgroundRecorder.TryApplyFinalizationCacheForBackgroundEnd(
                                bgRec,
                                focusedPid,
                                switchUT,
                                "SwitchSegment.BgMemberContinuation",
                                allowStale: true,
                                requireDestroyedTerminal: false,
                                confirmedDestroyed: false,
                                out cacheResult))
                        {
                            source = "cache";
                        }
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("SwitchSegment",
                        $"origin-terminal-stamp cache-apply-threw recId={bgRec.RecordingId ?? "<no-id>"} " +
                        $"focusedPid={focusedPid} ex={ex.Message}");
                }
            }

            if (!bgRec.TerminalStateValue.HasValue)
            {
                source = "live";
                if (focusedVessel != null)
                {
                    bgRec.StampTerminalState(
                        RecordingTree.DetermineTerminalState((int)focusedVessel.situation, focusedVessel),
                        "StampTerminalOnSwitchClosedBgOrigin.live");
                    CaptureTerminalOrbit(bgRec, focusedVessel);
                }
                else
                {
                    // No live vessel to classify against and no cache — leave the
                    // recording terminal-less rather than inventing a verdict.
                    ParsekLog.Warn("SwitchSegment",
                        $"origin-terminal-stamp unresolved recId={bgRec.RecordingId ?? "<no-id>"} " +
                        $"focusedPid={focusedPid} reason=no-vessel-no-cache");
                    return;
                }
            }

            bgRec.FilesDirty = true;
            ParsekLog.Verbose("SwitchSegment",
                $"origin-terminal-stamped recId={bgRec.RecordingId ?? "<no-id>"} " +
                $"terminal={bgRec.TerminalStateValue?.ToString() ?? "<none>"} source={source} " +
                $"focusedPid={focusedPid} ut={switchUT.ToString("R", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Goal-5 branch (plan §"Fly / Switch-To a vessel unrelated to any
        /// Parsek tree"). Creates a fresh <see cref="RecordingTree"/> when
        /// none exists, then attaches a standalone continuation segment as
        /// the root recording. Also serves as the fall-through target for
        /// every other branch's refusal (committed-clone restore fail,
        /// BG-lookup miss, ambiguous parent, helper refusal).
        /// </summary>
        private StockActionIntentConsumeResult StartStandaloneContinuationSegment(
            Vessel newVessel,
            StockActionIntentMarker marker,
            double switchUT,
            uint sourcePid,
            bool fallthroughFromBgLookup,
            string fallthroughReason)
        {
            uint newPid = newVessel.persistentId;
            var scenario = ParsekScenario.Instance;

            // Spin up a fresh tree if one isn't live. The committed-clone
            // branch already installed activeTree when it took its path, so
            // by here a null activeTree means we're genuinely standalone.
            bool createdFreshTree = false;
            if (activeTree == null)
            {
                activeTree = new RecordingTree
                {
                    Id = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                    TreeName = Recording.ResolveLocalizedName(newVessel.vesselName) ?? "Standalone",
                    BranchPoints = new List<BranchPoint>(),
                };
                EnsureBackgroundRecorderAttached("StartStandaloneContinuationSegment");
                createdFreshTree = true;
            }

            Guid sessionId = Guid.NewGuid();
            string newRecordingId = Guid.NewGuid().ToString("D",
                CultureInfo.InvariantCulture);

            // Capture prior ActiveRecordingId before CreateSwitchContinuationSegment
            // mutates it (a freshly-created tree has it null; the fall-through
            // path may have a non-null prior id from the upstream branch).
            string priorActiveRecordingId = activeTree.ActiveRecordingId;

            // Standalone path: parent is null, no branch-point id needed.
            // LOW 10 (PR #876 review): focusedRootPartPid parameter removed
            // — see SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            // VesselName resolution (#autoLOC token -> "Jumping Flea") is
            // centralized inside SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            var creation = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                activeTree,
                parentRecordingIdOrNull: null,
                newPid,
                newVessel.vesselName ?? "Unknown",
                switchUT,
                MapIntentActionToEntryReason(marker.Action),
                marker.IntentId,
                sessionId,
                newRecordingId,
                newBranchPointId: null,
                ut => BuildSwitchSegmentBoundaryPoint(newVessel, ut),
                sourcePid);

            if (!creation.Created)
            {
                // Both branches refused (committed clone tried first, then
                // standalone). Clear the intent so the player isn't surprised
                // by a delayed retry on the next focus change.
                ParsekLog.Warn("SwitchSegment",
                    $"standalone-creation-refused failureReason={creation.FailureReason ?? "<null>"} " +
                    $"focusedPid={newPid} intentId={marker.IntentId:D}");
                scenario.ClearStockActionIntent("consume-helper-refused");
                return new StockActionIntentConsumeResult
                {
                    Route = SwitchSegmentEntryRoute.Refused_TargetMismatch,
                    DiagnosticReason = creation.FailureReason ?? "standalone-helper-refused",
                };
            }

            if (createdFreshTree
                && string.IsNullOrEmpty(activeTree.RootRecordingId))
            {
                activeTree.RootRecordingId = newRecordingId;
            }

            SwitchSegmentEntryRoute route = fallthroughFromBgLookup
                ? SwitchSegmentEntryRoute.FellThroughBgLookupFailed
                : SwitchSegmentEntryRoute.StartedStandalone;

            // Phase C.1: bind the live recorder to the new continuation
            // recording id so trajectory samples flow into it immediately.
            BindLiveRecorderToSwitchSegment(
                newVessel, priorActiveRecordingId, newRecordingId,
                routeTag: fallthroughFromBgLookup
                    ? "bg-lookup-failed-standalone"
                    : "standalone",
                switchUT: switchUT);

            ArmSwitchSegmentSessionForRoute(
                marker, sessionId, activeTree.Id, parentRecordingId: null,
                newRecordingId, switchUT, newPid, sourcePid,
                committedTreeId: null,
                entryRoute: route);

            scenario.ClearStockActionIntent("consumed-into-segment");

            ParsekLog.Info("SwitchSegment",
                $"route={(fallthroughFromBgLookup ? "bg-lookup-failed-standalone" : "standalone")} " +
                $"intentId={marker.IntentId:D} sessionId={sessionId:D} " +
                $"treeId={activeTree.Id ?? "<null>"} segmentRecId={newRecordingId} " +
                $"sourcePid={sourcePid} focusedPid={newPid} " +
                $"fallthroughReason={fallthroughReason ?? "<none>"} createdFreshTree={createdFreshTree}");

            return new StockActionIntentConsumeResult
            {
                Route = route,
                NewRecordingId = newRecordingId,
                NewBranchPointId = null,
                SessionId = sessionId,
                DiagnosticReason = "consumed-into-segment",
            };
        }

        /// <summary>
        /// Builds and arms the <see cref="SwitchSegmentSession"/> for a
        /// successfully-created segment. Centralized so every Started* route
        /// produces identically-shaped sessions.
        /// </summary>
        private void ArmSwitchSegmentSessionForRoute(
            StockActionIntentMarker marker,
            Guid sessionId,
            string treeId,
            string parentRecordingId,
            string newRecordingId,
            double switchUT,
            uint focusedPid,
            uint sourcePid,
            string committedTreeId,
            SwitchSegmentEntryRoute entryRoute)
        {
            ArmSwitchSegmentSession(
                MapIntentActionToEntryReason(marker.Action),
                marker.IntentId,
                sessionId, treeId, parentRecordingId, newRecordingId,
                switchUT, focusedPid, sourcePid, committedTreeId);
        }

        /// <summary>
        /// Arms the session for a created segment. The marker-free form serves
        /// <see cref="TryContinueRestoredSpawnInNewSegment"/>, which has no stock intent.
        /// </summary>
        private void ArmSwitchSegmentSession(
            SwitchSegmentEntryReason entryReason,
            Guid intentId,
            Guid sessionId,
            string treeId,
            string parentRecordingId,
            string newRecordingId,
            double switchUT,
            uint focusedPid,
            uint sourcePid,
            string committedTreeId)
        {
            var preSessionBpIds = new List<string>();
            if (activeTree != null && activeTree.BranchPoints != null)
            {
                for (int i = 0; i < activeTree.BranchPoints.Count; i++)
                {
                    BranchPoint bp = activeTree.BranchPoints[i];
                    if (bp == null || string.IsNullOrEmpty(bp.Id)) continue;
                    // Skip the just-attached continuation branch point so the
                    // pre-session list only contains historical BPs.
                    if (!string.IsNullOrEmpty(parentRecordingId)
                        && bp.ChildRecordingIds != null
                        && bp.ChildRecordingIds.Contains(newRecordingId))
                    {
                        continue;
                    }
                    preSessionBpIds.Add(bp.Id);
                }
            }

            var session = new SwitchSegmentSession
            {
                SessionId = sessionId,
                TreeId = treeId,
                ParentRecordingId = parentRecordingId,
                ActiveSegmentRecordingId = newRecordingId,
                SourceVesselPersistentId = sourcePid,
                FocusedVesselPersistentId = focusedPid,
                SwitchUT = switchUT,
                EntryReason = entryReason,
                IntentId = intentId,
                CommittedTreeId = committedTreeId,
                PreSessionBranchPointIds = preSessionBpIds,
            };
            ParsekScenario scenario = ParsekScenario.Instance;
            if (scenario == null)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"session not armed: no scenario sessionId={sessionId:D} " +
                    $"segmentRecId={newRecordingId ?? "<null>"} entryReason={entryReason}");
                return;
            }
            scenario.ArmSwitchSegmentSession(session);
        }

        /// <summary>
        /// Phase C.1: after <see cref="SwitchSegmentBuilder.CreateSwitchContinuationSegment"/>
        /// mutates the tree (sets <see cref="RecordingTree.ActiveRecordingId"/>
        /// to the new continuation), this helper binds a live
        /// <see cref="FlightRecorder"/> to the new recording so trajectory
        /// samples flow into it immediately. Mirrors the canonical
        /// <see cref="PromoteRecordingFromBackground"/> / <see cref="ResumeCommittedActiveRecording"/>
        /// pattern (new <see cref="FlightRecorder"/>, <c>ActiveTree</c> set,
        /// <see cref="FlightRecorder.StartRecording"/> with
        /// <c>isPromotion: true</c>, <see cref="PrepareSessionStateForRecorderStart"/>).
        ///
        /// <para>If a prior live recorder is still bound to the now-stale
        /// previous <see cref="RecordingTree.ActiveRecordingId"/> (committed-clone
        /// path A or B in OnFlightReady where <c>TryRestoreCommittedTreeForSpawnedActiveVessel</c>
        /// already installed a recorder for the parent leaf), we flush it
        /// against the prior id first via a temporary
        /// <see cref="RecordingTree.ActiveRecordingId"/> swap so the prior
        /// recording keeps its accumulated samples and the new continuation
        /// starts clean.</para>
        /// </summary>
        private bool BindLiveRecorderToSwitchSegment(
            Vessel newVessel,
            string priorActiveRecordingId,
            string newRecordingId,
            string routeTag,
            double switchUT)
        {
            if (newVessel == null || activeTree == null
                || string.IsNullOrEmpty(newRecordingId))
            {
                ParsekLog.Warn("SwitchSegment",
                    $"recorder-bind-skipped reason=null-input route={routeTag} " +
                    $"hasVessel={newVessel != null} hasTree={activeTree != null} " +
                    $"newRecordingId={newRecordingId ?? "<null>"}");
                return false;
            }

            // If a prior recorder is live and bound to a different recording
            // (e.g. OnFlightReady's TryRestoreCommittedTreeForSpawnedActiveVessel
            // bound it to the parent leaf), flush it into the prior recording
            // before the new continuation takes over. CreateSwitchContinuationSegment
            // already overwrote tree.ActiveRecordingId — temporarily swap it back
            // so FlushRecorderToTreeRecording targets the parent.
            //
            // LOW 13 (PR #876 review) INVARIANT: this swap-back relies on the
            // entire flush completing synchronously on the Unity main thread.
            // Do NOT introduce a `yield return` or `await` between the swap-to-
            // prior and the swap-back -- a coroutine yield here would leak a
            // stale ActiveRecordingId to any callsite that reads it next frame
            // (including the recorder's own sampling loop). If you need async
            // work in the flush, capture the prior id into a local, do the
            // async, then re-acquire the tree state and recompute the swap.
            if (recorder != null && recorder.IsRecording
                && !string.IsNullOrEmpty(priorActiveRecordingId)
                && !string.Equals(priorActiveRecordingId, newRecordingId,
                    StringComparison.Ordinal))
            {
                string savedActiveId = activeTree.ActiveRecordingId;
                try
                {
                    activeTree.ActiveRecordingId = priorActiveRecordingId;
                    FlushRecorderToTreeRecording(recorder, activeTree);
                }
                finally
                {
                    activeTree.ActiveRecordingId = savedActiveId;
                }
                ParsekLog.Info("SwitchSegment",
                    $"recorder-prior-flushed route={routeTag} " +
                    $"priorRecordingId={priorActiveRecordingId} " +
                    $"newRecordingId={newRecordingId} vesselPid={newVessel.persistentId}");
                recorder.IsRecording = false;
                recorder = null;
            }
            else if (recorder != null)
            {
                // Recorder exists but not recording (e.g. captured-stop holding
                // CaptureAtStop). Drop it — the new segment owns the live state.
                ParsekLog.Info("SwitchSegment",
                    $"recorder-prior-dropped route={routeTag} " +
                    $"vesselPid={newVessel.persistentId} " +
                    $"wasRecording={recorder.IsRecording}");
                recorder = null;
            }

            // Canonical bind: mirrors PromoteRecordingFromBackground / ResumeCommittedActiveRecording.
            // MED 4 (PR #876 review): wrap the bind in try/catch so a
            // FlightRecorder ctor or StartRecording exception does not leave
            // `recorder` in a partial-construction state while
            // tree.ActiveRecordingId has already been advanced to the new
            // segment by SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            // We do NOT roll back tree.ActiveRecordingId: the segment was
            // created and the marker is armed; the recorder failure is a
            // sampling problem the next frame may recover from. Setting
            // `recorder = null` lets the next frame's recorder-binding pass
            // re-create cleanly.
            try
            {
                recorder = new FlightRecorder();
                recorder.ActiveTree = activeTree;
                recorder.StartRecording(isPromotion: true);
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"recorder-bind-threw route={routeTag} " +
                    $"newRecordingId={newRecordingId} vesselPid={newVessel.persistentId} " +
                    $"{ex.GetType().Name}: {ex.Message}; recorder reset to null - " +
                    "tree.ActiveRecordingId left at new segment, next frame may rebind");
                recorder = null;
                return false;
            }

            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"recorder-bind-failed route={routeTag} " +
                    $"newRecordingId={newRecordingId} vesselPid={newVessel.persistentId} " +
                    "StartRecording returned IsRecording=false; new segment will have no samples");
                recorder = null;
                return false;
            }

            PrepareSessionStateForRecorderStart("BindLiveRecorderToSwitchSegment:" + routeTag);

            ParsekLog.Info("SwitchSegment",
                string.Format(CultureInfo.InvariantCulture,
                    "recorder-bound route={0} new-recording-id={1} vessel-pid={2} vessel-name='{3}' ut={4}",
                    routeTag,
                    newRecordingId,
                    newVessel.persistentId,
                    newVessel.vesselName ?? "<unnamed>",
                    switchUT.ToString("R", CultureInfo.InvariantCulture)));
            return true;
        }

        // ---------------------------------------------------------------------
        // End Phase C: stock-action intent consume.
        // ---------------------------------------------------------------------

        private static double NormalizeAngleDeltaDegrees(double a, double b)
        {
            double delta = Math.Abs(a - b) % 360.0;
            return delta > 180.0 ? 360.0 - delta : delta;
        }

        private static PostSwitchOrbitSnapshot CapturePostSwitchOrbitSnapshot(Vessel v)
        {
            if (v == null || v.orbit == null || v.mainBody == null)
                return default;

            return new PostSwitchOrbitSnapshot
            {
                IsValid = true,
                BodyName = v.mainBody.name,
                SemiMajorAxis = v.orbit.semiMajorAxis,
                Eccentricity = v.orbit.eccentricity,
                Inclination = v.orbit.inclination,
                LongitudeOfAscendingNode = v.orbit.LAN,
                ArgumentOfPeriapsis = v.orbit.argumentOfPeriapsis
            };
        }

        private static void RefreshPostSwitchAutoRecordModuleCaches(
            PostSwitchAutoRecordState state,
            Vessel v)
        {
            if (state == null || v == null)
                return;

            state.CachedPartCount = v.parts != null ? v.parts.Count : 0;

            state.CachedEngines = FlightRecorder.CacheEngineModules(v);
            state.ActiveEngineKeys.Clear();
            state.EngineThrottleMap.Clear();

            for (int i = 0; i < state.CachedEngines.Count; i++)
            {
                var (part, engine, moduleIndex) = state.CachedEngines[i];
                if (part == null || engine == null)
                    continue;

                if (!engine.EngineIgnited || !engine.isOperational)
                    continue;

                ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);
                state.ActiveEngineKeys.Add(key);
                state.EngineThrottleMap[key] = engine.currentThrottle;
            }

            state.CachedRcsModules = FlightRecorder.CacheRcsModules(v);
            state.RcsFrameCountMap.Clear();
            state.ActiveRcsKeys.Clear();
            state.RcsThrottleMap.Clear();

            for (int i = 0; i < state.CachedRcsModules.Count; i++)
            {
                var (part, rcs, moduleIndex) = state.CachedRcsModules[i];
                if (part == null || rcs == null)
                    continue;

                if (!rcs.rcs_active || !rcs.rcsEnabled)
                    continue;

                ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);
                state.RcsFrameCountMap[key] = FlightRecorder.RcsDebounceFrameThreshold;
                state.ActiveRcsKeys.Add(key);
                state.RcsThrottleMap[key] =
                    FlightRecorder.ComputeRcsPower(rcs.thrustForces, rcs.thrusterPower);
            }

            state.ModuleCachesDirty = false;
        }

        private bool CapturePostSwitchAutoRecordBaseline(
            PostSwitchAutoRecordState state,
            Vessel v,
            double currentUT)
        {
            if (state == null || v == null)
                return false;

            ConfigNode vesselSnapshot = FlightRecorder.TryBackupSnapshot(v);
            state.BaselineCaptured = true;
            state.BaselineCapturedUt = currentUT;
            state.ComparisonsReadyUt =
                v.situation == Vessel.Situations.LANDED || v.situation == Vessel.Situations.SPLASHED
                    ? currentUT + LandedSettleThreshold
                    : currentUT;
            state.NextManifestEvaluationUt = currentUT;
            state.BaselineWorldPosition = v.GetWorldPos3D();
            state.BaselineWorldRotation = TrajectoryMath.SanitizeQuaternion(v.transform.rotation);
            state.BaselineSeedPoint = FlightRecorder.BuildTrajectoryPoint(
                v,
                FlightRecorder.SampleCurrentVelocity(v),
                currentUT);
            state.AttitudeThresholdExceededAtUt = double.NaN;
            state.BaselineOrbit = CapturePostSwitchOrbitSnapshot(v);
            state.BaselineResources = VesselSnapshotOps.ExtractResourceManifest(vesselSnapshot);
            state.BaselineInventory = VesselSnapshotOps.ExtractInventoryManifest(vesselSnapshot, out _);
            state.BaselineCrew = VesselSpawner.ExtractCrewManifest(vesselSnapshot);
            state.BaselinePartStateTokens = CapturePostSwitchPartStateTokens(v);
            RefreshPostSwitchAutoRecordModuleCaches(state, v);

            ParsekLog.Info("Flight",
                $"Post-switch baseline captured: vessel='{state.VesselName}' pid={state.VesselPid} " +
                $"tracked={state.TrackedInActiveTree} settleUntil={state.ComparisonsReadyUt:F1}");
            return true;
        }

        private static HashSet<string> CapturePostSwitchPartStateTokens(Vessel v)
        {
            var tokens = new HashSet<string>();
            if (v == null || v.parts == null)
                return tokens;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null)
                    continue;

                ModuleDeployablePart deployable = p.FindModuleImplementing<ModuleDeployablePart>();
                if (deployable != null)
                {
                    ModuleDeployablePart.DeployState ds = deployable.deployState;
                    if (ds == ModuleDeployablePart.DeployState.EXTENDED)
                        tokens.Add($"deployable:{p.persistentId}");
                }

                var fairing = p.FindModuleImplementing<ModuleProceduralFairing>();
                if (fairing != null)
                {
                    try
                    {
                        if (fairing.GetScalar >= 0.5f)
                            tokens.Add($"fairing:{p.persistentId}");
                    }
                    catch
                    {
                    }
                }

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null)
                        continue;

                    if (module is ModuleWheels.ModuleWheelDeployment wheel)
                    {
                        FlightRecorder.ClassifyGearState(
                            wheel.stateString,
                            out bool isDeployed,
                            out bool isRetracted);
                        if (isDeployed && !isRetracted)
                            tokens.Add($"gear:{p.persistentId}:{m}");
                        continue;
                    }

                    if (module is ModuleCargoBay cargoBay)
                    {
                        int deployIdx = cargoBay.DeployModuleIndex;
                        if (deployIdx >= 0 && deployIdx < p.Modules.Count
                            && p.Modules[deployIdx] is ModuleAnimateGeneric animate)
                        {
                            FlightRecorder.ClassifyCargoBayState(
                                animate.animTime,
                                cargoBay.closedPosition,
                                FlightRecorder.ResolveCargoBayDeployLimitStop(
                                    animate, cargoBay.closedPosition),
                                out bool isOpen,
                                out bool isClosed);
                            if (isOpen && !isClosed)
                                tokens.Add($"cargo:{p.persistentId}:{m}");
                        }
                        continue;
                    }

                    if (string.Equals(module.moduleName, "RetractableLadder", StringComparison.Ordinal))
                    {
                        if (FlightRecorder.TryClassifyRetractableLadderState(
                                module,
                                out bool isDeployed,
                                out bool isRetracted)
                            && isDeployed && !isRetracted)
                        {
                            tokens.Add($"ladder:{p.persistentId}:{m}");
                        }
                        continue;
                    }

                    if (string.Equals(module.moduleName, "ModuleAnimationGroup", StringComparison.Ordinal))
                    {
                        if (FlightRecorder.TryClassifyAnimationGroupState(
                                module,
                                out bool isDeployed,
                                out bool isRetracted)
                            && isDeployed && !isRetracted)
                        {
                            tokens.Add($"animation-group:{p.persistentId}:{m}");
                        }
                        continue;
                    }

                    if (string.Equals(module.moduleName, "ModuleAeroSurface", StringComparison.Ordinal))
                    {
                        if (FlightRecorder.TryClassifyAeroSurfaceState(
                                module,
                                out bool isDeployed,
                                out bool isRetracted)
                            && isDeployed && !isRetracted)
                        {
                            tokens.Add($"aero-surface:{p.persistentId}:{m}");
                        }
                        continue;
                    }

                    if (string.Equals(module.moduleName, "ModuleControlSurface", StringComparison.Ordinal))
                    {
                        if (FlightRecorder.TryClassifyControlSurfaceState(
                                module,
                                out bool isDeployed,
                                out bool isRetracted)
                            && isDeployed && !isRetracted)
                        {
                            tokens.Add($"control-surface:{p.persistentId}:{m}");
                        }
                        continue;
                    }

                    if (string.Equals(module.moduleName, "ModuleRobotArmScanner", StringComparison.Ordinal))
                    {
                        if (FlightRecorder.TryClassifyRobotArmScannerState(
                                module,
                                out bool isDeployed,
                                out bool isRetracted)
                            && isDeployed && !isRetracted)
                        {
                            tokens.Add($"robot-arm:{p.persistentId}:{m}");
                        }
                    }
                }
            }

            return tokens;
        }

        private PostSwitchAutoRecordTrigger EvaluatePostSwitchAutoRecordTrigger(
            Vessel v,
            PostSwitchAutoRecordState state,
            double currentUT)
        {
            int currentPartCount = v != null && v.parts != null ? v.parts.Count : 0;
            bool needsCacheRefresh = NeedsPostSwitchModuleCacheRefresh(
                state.ModuleCachesDirty, state.CachedPartCount, currentPartCount);
            bool evaluateManifestDiff = ShouldEvaluatePostSwitchManifestDiff(
                currentUT,
                state.NextManifestEvaluationUt,
                needsCacheRefresh);
            RefreshPostSwitchAutoRecordModuleCachesIfNeeded(state, v, needsCacheRefresh);

            bool engineTriggered = EvaluatePostSwitchEngineActivity(state, currentUT);
            bool rcsTriggered = EvaluatePostSwitchRcsActivity(state, currentUT);
            bool attitudeChanged = EvaluatePostSwitchAttitudeChange(v, state, currentUT);

            bool crewChanged = false;
            bool resourceChanged = false;
            bool partStateChanged = false;
            EvaluatePostSwitchManifestChanges(
                v,
                state,
                currentUT,
                evaluateManifestDiff,
                out crewChanged,
                out resourceChanged,
                out partStateChanged);

            bool landedMotionChanged = EvaluatePostSwitchLandedMotion(v, state);
            bool orbitChanged = EvaluatePostSwitchOrbitChange(v, state);

            return EvaluatePostSwitchAutoRecordTrigger(
                engineTriggered,
                rcsTriggered,
                attitudeChanged,
                crewChanged,
                resourceChanged,
                partStateChanged,
                landedMotionChanged,
                orbitChanged);
        }

        private void RefreshPostSwitchAutoRecordModuleCachesIfNeeded(
            PostSwitchAutoRecordState state,
            Vessel v,
            bool needsCacheRefresh)
        {
            if (needsCacheRefresh)
            {
                RefreshPostSwitchAutoRecordModuleCaches(state, v);
                ParsekLog.VerboseRateLimited("Flight", "post-switch-auto-record-cache-refresh",
                    $"Post-switch watch refreshed module caches: pid={state.VesselPid} " +
                    $"parts={state.CachedPartCount}", 1.0);
            }
        }

        private static bool EvaluatePostSwitchEngineActivity(
            PostSwitchAutoRecordState state,
            double currentUT)
        {
            bool engineTriggered = false;
            for (int i = 0; i < state.CachedEngines.Count && !engineTriggered; i++)
            {
                var (part, engine, moduleIndex) = state.CachedEngines[i];
                if (part == null || engine == null)
                    continue;

                var engineEvents = new List<PartEvent>();
                FlightRecorder.CheckEngineTransition(
                    FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex),
                    part.persistentId,
                    moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    engine.EngineIgnited && engine.isOperational,
                    engine.currentThrottle,
                    state.ActiveEngineKeys,
                    state.EngineThrottleMap,
                    currentUT,
                    engineEvents);
                engineTriggered = engineEvents.Count > 0;
            }

            return engineTriggered;
        }

        private static bool EvaluatePostSwitchRcsActivity(
            PostSwitchAutoRecordState state,
            double currentUT)
        {
            bool rcsTriggered = false;
            for (int i = 0; i < state.CachedRcsModules.Count && !rcsTriggered; i++)
            {
                var (part, rcs, moduleIndex) = state.CachedRcsModules[i];
                if (part == null || rcs == null)
                    continue;

                var rcsEvents = new List<PartEvent>();
                FlightRecorder.ProcessRcsDebounce(
                    FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex),
                    part.persistentId,
                    moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    rcs.rcs_active && rcs.rcsEnabled,
                    rcs.thrustForces,
                    rcs.thrusterPower,
                    currentUT,
                    state.RcsFrameCountMap,
                    state.ActiveRcsKeys,
                    state.RcsThrottleMap,
                    rcsEvents);
                rcsTriggered = rcsEvents.Count > 0;
            }

            return rcsTriggered;
        }

        private static bool EvaluatePostSwitchAttitudeChange(
            Vessel v,
            PostSwitchAutoRecordState state,
            double currentUT)
        {
            Quaternion currentWorldRotation = TrajectoryMath.SanitizeQuaternion(v.transform.rotation);
            float attitudeDeltaDegrees = ComputePostSwitchAttitudeDeltaDegrees(
                state.BaselineWorldRotation,
                currentWorldRotation);
            bool attitudeChanged = HasMeaningfulAttitudeChange(
                state.VesselPid,
                v.persistentId,
                state.BaselineCaptured,
                state.BaselineWorldRotation,
                currentWorldRotation);
            if (!attitudeChanged)
            {
                if (!double.IsNaN(state.AttitudeThresholdExceededAtUt))
                {
                    ParsekLog.Verbose("Flight",
                        $"Post-switch attitude trigger ignored: pid={state.VesselPid} " +
                        $"angle={attitudeDeltaDegrees:F2} threshold={PostSwitchAttitudeChangeThresholdDegrees:F2} " +
                        $"reason=debounce-reset");
                    state.AttitudeThresholdExceededAtUt = double.NaN;
                }
            }
            else if (double.IsNaN(state.AttitudeThresholdExceededAtUt))
            {
                state.AttitudeThresholdExceededAtUt = currentUT;
                attitudeChanged = false;
                ParsekLog.Verbose("Flight",
                    $"Post-switch attitude trigger pending: pid={state.VesselPid} " +
                    $"angle={attitudeDeltaDegrees:F2} threshold={PostSwitchAttitudeChangeThresholdDegrees:F2} " +
                    $"debounce={PostSwitchAttitudeChangeDebounceSeconds:F2}s");
            }
            else if (currentUT - state.AttitudeThresholdExceededAtUt
                < PostSwitchAttitudeChangeDebounceSeconds)
            {
                attitudeChanged = false;
            }
            else
            {
                ParsekLog.Info("Flight",
                    $"Post-switch attitude trigger accepted: pid={state.VesselPid} " +
                    $"angle={attitudeDeltaDegrees:F2} threshold={PostSwitchAttitudeChangeThresholdDegrees:F2} " +
                    $"debounce={currentUT - state.AttitudeThresholdExceededAtUt:F2}s");
            }

            return attitudeChanged;
        }

        private static void EvaluatePostSwitchManifestChanges(
            Vessel v,
            PostSwitchAutoRecordState state,
            double currentUT,
            bool evaluateManifestDiff,
            out bool crewChanged,
            out bool resourceChanged,
            out bool partStateChanged)
        {
            crewChanged = false;
            resourceChanged = false;
            partStateChanged = false;
            if (evaluateManifestDiff)
            {
                ConfigNode currentSnapshot = FlightRecorder.TryBackupSnapshot(v);
                var currentCrew = VesselSpawner.ExtractCrewManifest(currentSnapshot);
                var crewDelta = CrewManifest.ComputeCrewDelta(state.BaselineCrew, currentCrew);
                int crewDeltaKeys = CountMeaningfulCrewDelta(crewDelta);
                int resourceDeltaKeys = PostSwitchManifestDeltaSkipped;
                int inventoryDeltaKeys = PostSwitchManifestDeltaSkipped;
                int partStateTokenDelta = PostSwitchManifestDeltaSkipped;
                crewChanged = crewDeltaKeys > 0;
                if (!crewChanged)
                {
                    var currentResources = VesselSnapshotOps.ExtractResourceManifest(currentSnapshot);
                    var resourceDelta = ResourceManifest.ComputeResourceDelta(
                        state.BaselineResources,
                        currentResources);
                    resourceDeltaKeys = CountMeaningfulResourceDelta(resourceDelta);
                    resourceChanged = resourceDeltaKeys > 0;
                }

                if (!crewChanged && !resourceChanged)
                {
                    var currentPartStateTokens = CapturePostSwitchPartStateTokens(v);
                    partStateTokenDelta = CountPartStateTokenDelta(
                        state.BaselinePartStateTokens,
                        currentPartStateTokens);
                    if (partStateTokenDelta > 0)
                    {
                        partStateChanged = true;
                    }
                    else
                    {
                        var currentInventory = VesselSnapshotOps.ExtractInventoryManifest(currentSnapshot, out _);
                        var inventoryDelta = InventoryManifest.ComputeInventoryDelta(
                            state.BaselineInventory,
                            currentInventory);
                        inventoryDeltaKeys = CountMeaningfulInventoryDelta(inventoryDelta);
                        partStateChanged = inventoryDeltaKeys > 0;
                    }
                }

                state.NextManifestEvaluationUt =
                    currentUT + PostSwitchManifestEvaluationIntervalSeconds;
                ParsekLog.VerboseOnChange("Flight",
                    "post-switch-manifest-delta-" + state.VesselPid.ToString(CultureInfo.InvariantCulture),
                    BuildPostSwitchManifestDeltaStateKey(
                        crewDeltaKeys,
                        resourceDeltaKeys,
                        inventoryDeltaKeys,
                        partStateTokenDelta,
                        crewChanged,
                        resourceChanged,
                        partStateChanged),
                    FormatPostSwitchManifestDeltaSummary(
                        state.VesselPid,
                        crewDeltaKeys,
                        resourceDeltaKeys,
                        inventoryDeltaKeys,
                        partStateTokenDelta,
                        crewChanged,
                        resourceChanged,
                        partStateChanged,
                        state.NextManifestEvaluationUt));
            }
        }

        private static bool EvaluatePostSwitchLandedMotion(
            Vessel v,
            PostSwitchAutoRecordState state)
        {
            double distanceDelta = Vector3d.Distance(state.BaselineWorldPosition, v.GetWorldPos3D());
            return (v.situation == Vessel.Situations.LANDED || v.situation == Vessel.Situations.SPLASHED)
                && HasMeaningfulLandedMotionChange(distanceDelta, v.srfSpeed);
        }

        private static bool EvaluatePostSwitchOrbitChange(
            Vessel v,
            PostSwitchAutoRecordState state)
        {
            return v.situation != Vessel.Situations.LANDED
                && v.situation != Vessel.Situations.SPLASHED
                && v.mainBody != null
                && (!v.mainBody.atmosphere || v.altitude > v.mainBody.atmosphereDepth)
                && HasMeaningfulOrbitChange(
                    state.BaselineOrbit,
                    CapturePostSwitchOrbitSnapshot(v));
        }

        private void PrepareActiveTreeForFreshPostSwitchRecording(
            Vessel v,
            PostSwitchAutoRecordState state,
            double currentUT)
        {
            if (activeTree == null || v == null)
                return;

            string recordingId = Guid.NewGuid().ToString("N");
            var recording = new Recording
            {
                RecordingId = recordingId,
                TreeId = activeTree.Id,
                VesselPersistentId = v.persistentId,
                VesselName = Recording.ResolveLocalizedName(v.vesselName) ?? v.vesselName ?? "Unknown",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                // Pin start-of-recording controller identity for the fresh post-switch
                // root so a later BG go-on-rails identity-loss check has the original
                // controllable-part PIDs to compare against.
                Controllers = ControllerInfo.CaptureFromVessel(v)
            };

            ConfigNode startSnapshot = FlightRecorder.TryBackupSnapshot(v);
            if (startSnapshot != null)
                recording.GhostVisualSnapshot = startSnapshot;

            activeTree.AddOrReplaceRecording(recording);
            activeTree.ActiveRecordingId = recordingId;
            activeTree.BackgroundMap.Remove(v.persistentId);

            if (!string.IsNullOrEmpty(state.FreshStartParentRecordingId)
                && activeTree.Recordings.TryGetValue(state.FreshStartParentRecordingId, out var parent))
            {
                string branchPointId = Guid.NewGuid().ToString("N");
                var branchPoint = new BranchPoint
                {
                    Id = branchPointId,
                    UT = currentUT,
                    Type = BranchPointType.Launch,
                    ParentRecordingIds = new List<string> { state.FreshStartParentRecordingId },
                    ChildRecordingIds = new List<string> { recordingId }
                };
                parent.ChildBranchPointId = branchPointId;
                recording.ParentBranchPointId = branchPointId;
                activeTree.BranchPoints.Add(branchPoint);
            }
        }

        private void SeedPostSwitchAlignmentWindow(Vessel v, PostSwitchAutoRecordState state)
        {
            if (v == null || state == null || recorder == null || !recorder.IsRecording)
                return;

            if (state.BaselineSeedPoint.HasValue)
            {
                recorder.SeedTrajectoryPoint(
                    state.BaselineSeedPoint.Value,
                    v,
                    "post-switch attitude baseline");
            }

            recorder.SamplePosition(v);
            ParsekLog.Verbose("Flight",
                $"Post-switch alignment window seeded: pid={state.VesselPid} " +
                $"baselineUT={state.BaselineCapturedUt:F2} currentUT={Planetarium.GetUniversalTime():F2}");
        }

        private bool TryStartPostSwitchAutoRecord(
            Vessel v,
            PostSwitchAutoRecordState state,
            PostSwitchAutoRecordTrigger trigger,
            double currentUT)
        {
            bool activeVesselTrackedInBackground = activeTree != null
                && activeTree.BackgroundMap.ContainsKey(v.persistentId);
            var decision = EvaluatePostSwitchAutoRecordStartDecision(
                state.VesselPid,
                v.persistentId,
                activeTree != null,
                activeVesselTrackedInBackground,
                canRestorePendingTrackedTree: false,
                suppressStart: false);

            ParsekLog.Info("Flight",
                $"Post-switch trigger accepted: vessel='{v.vesselName}' pid={v.persistentId} " +
                $"trigger={trigger} decision={decision}");

            switch (decision)
            {
                case PostSwitchAutoRecordStartDecision.PromoteTrackedRecording:
                    if (activeTree != null
                        && activeTree.BackgroundMap.TryGetValue(v.persistentId, out string recordingId))
                    {
                        backgroundRecorder?.OnVesselRemovedFromBackground(v.persistentId);
                        PromoteRecordingFromBackground(recordingId, v);
                    }
                    break;

                case PostSwitchAutoRecordStartDecision.RestoreAndPromoteTrackedRecording:
                    ParsekLog.Verbose("Flight",
                        "Post-switch trigger hit restore-and-promote seam, but #534 restore wiring is gated on this branch");
                    return false;

                case PostSwitchAutoRecordStartDecision.StartFreshRecording:
                    PrepareActiveTreeForFreshPostSwitchRecording(v, state, currentUT);
                    StartRecording(suppressStartScreenMessage: true);
                    if (IsRecording)
                    {
                        Log($"Auto-record started (post-switch {trigger})");
                        ScreenMessage("Recording STARTED (auto - post switch)", 2f);
                    }
                    break;
            }

            if (!IsRecording)
                return false;

            if (trigger == PostSwitchAutoRecordTrigger.AttitudeChange)
                SeedPostSwitchAlignmentWindow(v, state);

            DisarmPostSwitchAutoRecord($"recording started via {trigger}");
            return true;
        }

        internal void OnPostSwitchAutoRecordPhysicsFrame(Vessel v)
        {
            PostSwitchAutoRecordState state = postSwitchAutoRecord;
            if (state == null)
                return;

            uint activePid = v != null ? v.persistentId : 0;
            double currentUT = Planetarium.GetUniversalTime();
            var suppression = EvaluatePostSwitchAutoRecordSuppression(
                ParsekSettings.Current?.autoRecordOnFirstModificationAfterSwitch != false,
                IsRecording,
                v != null,
                activePid,
                state.VesselPid,
                v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId),
                restoringActiveTree,
                HasPendingPostSwitchAutoRecordTransition(),
                IsAnyWarpActive(),
                v != null && v.packed);

            if (suppression == PostSwitchAutoRecordSuppressionReason.Disabled
                || suppression == PostSwitchAutoRecordSuppressionReason.AlreadyRecording)
            {
                LogPostSwitchAutoRecordDecision(
                    state,
                    activePid,
                    suppression,
                    waitingForSettle: false,
                    currentUt: currentUT,
                    trigger: PostSwitchAutoRecordTrigger.None);
                DisarmPostSwitchAutoRecord($"suppressed by {suppression}");
                return;
            }

            if (suppression != PostSwitchAutoRecordSuppressionReason.None)
            {
                LogPostSwitchAutoRecordDecision(
                    state,
                    activePid,
                    suppression,
                    waitingForSettle: false,
                    currentUt: currentUT,
                    trigger: PostSwitchAutoRecordTrigger.None);
                return;
            }

            if (!state.BaselineCaptured)
            {
                LogPostSwitchAutoRecordDecision(
                    state,
                    activePid,
                    suppression,
                    waitingForSettle: false,
                    currentUt: currentUT,
                    trigger: PostSwitchAutoRecordTrigger.None);
                CapturePostSwitchAutoRecordBaseline(state, v, currentUT);
                return;
            }

            if (currentUT < state.ComparisonsReadyUt)
            {
                LogPostSwitchAutoRecordDecision(
                    state,
                    activePid,
                    suppression,
                    waitingForSettle: true,
                    currentUt: currentUT,
                    trigger: PostSwitchAutoRecordTrigger.None);
                return;
            }

            PostSwitchAutoRecordTrigger trigger =
                EvaluatePostSwitchAutoRecordTrigger(v, state, currentUT);
            LogPostSwitchAutoRecordDecision(
                state,
                activePid,
                suppression,
                waitingForSettle: false,
                currentUt: currentUT,
                trigger: trigger);
            if (trigger == PostSwitchAutoRecordTrigger.None)
                return;

            TryStartPostSwitchAutoRecord(v, state, trigger, currentUT);
        }

        void OnVesselWasModified(Vessel v)
        {
            if (v == null || GhostMapPresence.IsGhostMapVessel(v.persistentId))
                return;

            // M5: the recorder's engine/RCS/robotic caches are built once at StartRecording and
            // hold direct Part/PartModule references. Rebuild them here so a staged-away booster
            // stops being polled into this recording and a welded-on / newly docked module starts
            // being polled at all. No-ops unless v IS the recorded vessel.
            recorder?.OnVesselWasModified(v);

            PostSwitchAutoRecordState state = postSwitchAutoRecord;
            if (state == null || state.VesselPid != v.persistentId)
                return;

            state.ModuleCachesDirty = true;
            state.NextManifestEvaluationUt = PostSwitchManifestEvaluateNextFrameUt;
            ParsekLog.VerboseRateLimited("Flight", "post-switch-auto-record-invalidated",
                $"Post-switch watch invalidated module caches: pid={state.VesselPid} " +
                $"vessel='{state.VesselName}'", 1.0);
        }

        void OnVesselGoOnRails(Vessel v)
        {
            if (v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId)) return;
            // Track pre-pack vessel state for phantom terrain crash detection
            if (v != null)
                packStates[v.persistentId] = (Planetarium.GetUniversalTime(), v.situation);
            recorder?.OnVesselGoOnRails(v);
            backgroundRecorder?.OnBackgroundVesselGoOnRails(v);
        }

        void OnVesselGoOffRails(Vessel v)
        {
            if (v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId)) return;
            if (v != null) packStates.Remove(v.persistentId);
            recorder?.OnVesselGoOffRails(v);
            backgroundRecorder?.OnBackgroundVesselGoOffRails(v);
        }

        void OnVesselLoaded(Vessel v)
        {
            if (v == null) return;
            if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) return;

            uint pid = v.persistentId;
            loadedAnchorVessels.Add(pid);

            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec.LoopAnchorVesselId != pid || !rec.LoopPlayback)
                    continue;

                string anchorBody = v.mainBody?.name;
                if (!GhostPlaybackLogic.ShouldSpawnLoopedGhost(rec, true, anchorBody, rec.LoopAnchorBodyName))
                    continue;

                double currentUT = Planetarium.GetUniversalTime();
                if (TryComputeLoopPlaybackUT(rec, currentUT,
                        out double loopUT, out long cycleIndex, out bool isInPause, i))
                {
                    ParsekLog.Info("Loop",
                        $"Anchor vessel loaded: pid={pid}, rec #{i} '{rec.VesselName}' " +
                        $"phase cycle={cycleIndex} loopUT={loopUT:F2} paused={isInPause}");
                }
                else if (TryGetLoopSchedule(
                             rec, i,
                             out _,
                             out double scheduleStartUT,
                             out _,
                             out double intervalSeconds))
                {
                    ParsekLog.Info("Loop",
                        $"Anchor vessel loaded: pid={pid}, rec #{i} '{rec.VesselName}' " +
                        $"queued launch pending scheduleStartUT={scheduleStartUT:F2} interval={intervalSeconds:F2}");
                }

                // The loop playback system in GhostPlaybackEngine.UpdateLoopingPlayback
                // will pick up this recording on the next Update frame and spawn the
                // ghost at the computed phase — no direct spawn here avoids duplicate spawning.
            }
        }

        /// <summary>
        /// Pure: adds every already-loaded vessel pid to the loaded-anchor set, skipping ghost
        /// map vessels the same way <see cref="OnVesselLoaded"/> does. The vessel being flown
        /// is loaded by <c>FlightDriver.Start</c> before this scene subscribes to
        /// <c>onVesselLoaded</c>, so without the seed a loop anchored to it never plays
        /// (LOOP-ANCHOR-ACTIVE-VESSEL-NEVER-MARKED-LOADED). Returns how many pids were added.
        /// </summary>
        internal static int SeedLoadedAnchorVessels(
            HashSet<uint> loadedAnchors, IList<uint> loadedVesselPids,
            Func<uint, bool> isGhostMapVessel, out int ghostSkipped)
        {
            ghostSkipped = 0;
            if (loadedAnchors == null || loadedVesselPids == null)
                return 0;
            int added = 0;
            for (int i = 0; i < loadedVesselPids.Count; i++)
            {
                uint pid = loadedVesselPids[i];
                if (pid == 0)
                    continue;
                if (isGhostMapVessel != null && isGhostMapVessel(pid))
                {
                    ghostSkipped++;
                    continue;
                }
                if (loadedAnchors.Add(pid))
                    added++;
            }
            return added;
        }

        private void SeedLoadedAnchorVesselsFromScene(string reason)
        {
            if (engine == null)
                return;
            List<uint> pids = ReadLoadedVesselPids();
            int added = SeedLoadedAnchorVessels(
                loadedAnchorVessels, pids, GhostMapPresence.IsGhostMapVessel, out int ghostSkipped);
            ParsekLog.Verbose("Loop",
                $"Loaded-anchor set seeded: reason={reason} loaded={pids.Count} added={added} " +
                $"ghostSkipped={ghostSkipped} total={loadedAnchorVessels.Count}");
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static List<uint> ReadLoadedVesselPids()
        {
            var pids = new List<uint>();
            var loaded = FlightGlobals.VesselsLoaded;
            if (loaded == null)
                return pids;
            for (int i = 0; i < loaded.Count; i++)
            {
                Vessel v = loaded[i];
                if (v != null)
                    pids.Add(v.persistentId);
            }
            return pids;
        }

        void OnVesselUnloaded(Vessel v)
        {
            if (v == null) return;
            if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) return;
            uint pid = v.persistentId;
            loadedAnchorVessels.Remove(pid);

            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec.LoopAnchorVesselId != pid || !rec.LoopPlayback)
                    continue;

                if (ghostStates.ContainsKey(i))
                {
                    ParsekLog.Info("Loop",
                        $"Anchor vessel unloaded: pid={pid}, destroying looped ghost #{i} '{rec.VesselName}'");
                    DestroyAllOverlapGhosts(i);
                    engine.DestroyGhost(i);
                }
            }
        }

        void OnVesselSOIChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> data)
        {
            if (data.host != null && GhostMapPresence.IsGhostMapVessel(data.host.persistentId)) return;

            recorder?.OnVesselSOIChanged(data);
            backgroundRecorder?.OnBackgroundVesselSOIChanged(data.host, data.from);
        }

        /// <summary>
        /// Stock Alt+F12 teleport (Set Orbit / Rendezvous / Set Position) finished
        /// <c>FlightGlobals.PostOrbitSet</c> for the active vessel; forwarded by
        /// <see cref="Patches.CheatTeleportPatch"/>. Returns false when no recorder exists,
        /// so the patch writes the log line itself.
        /// </summary>
        internal bool HandleCheatTeleport(Vessel v, CelestialBody oldBody)
        {
            if (v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId)) return true;
            if (recorder == null)
                return false;
            recorder.OnCheatTeleport(v, oldBody);
            return true;
        }

        void OnTimeWarpRateChanged()
        {
            bool isWarpNow = IsAnyWarpActive();

            // Detect warp start: was not warping, now warping — patch facility visuals
            // so the player sees the correct state during warp. Full recalculation
            // happens on warp exit; this is a lightweight visual-only update.
            // NOTE: True real-time per-frame facility visual updates during warp would
            // require a partial recalculation walk (processing actions only up to the
            // current UT). The current approach patches to the final derived state at
            // warp start and again at warp end, which covers the common case of warping
            // past a facility event.
            if (!wasWarpActive && isWarpNow)
            {
                warpStartUT = Planetarium.GetUniversalTime();
                if (LedgerOrchestrator.IsInitialized && !Ledger.IsHeldFromNewerVersion)
                {
                    // Same suppression PatchAll wraps it in: the patch's SetLevel /
                    // Demolish / Repair fire the stock facility and building events, which
                    // must not read back as player upgrades, collapses or repairs.
                    using (SuppressionGuard.ResourcesAndReplay())
                        KspStatePatcher.PatchFacilities(LedgerOrchestrator.Facilities);
                    ParsekLog.Info("WarpFacilities",
                        "Warp start at UT="
                        + warpStartUT.ToString("R", CultureInfo.InvariantCulture)
                        + " — patched facility visuals");
                }
                else if (Ledger.IsHeldFromNewerVersion)
                {
                    ParsekLog.Verbose("WarpFacilities",
                        "Warp start: facility patch skipped (ledger held from a newer Parsek version)");
                }
            }

            // Detect warp exit: was warping, now at 1x — recalculate ledger
            if (wasWarpActive && !isWarpNow)
            {
                // Snapshot the handler-fire UT. Exact alignment with KSP's internal
                // warp boundary is out of scope for this cutoff seam.
                double warpEndUT = Planetarium.GetUniversalTime();
                RecalculateLedgerAfterWarpExit(warpEndUT);

                // Log whether any facility actions were crossed during this warp session
                if (LedgerOrchestrator.IsInitialized &&
                    LedgerOrchestrator.HasFacilityActionsInRange(warpStartUT, warpEndUT))
                {
                    ParsekLog.Info("WarpFacilities",
                        "Warp exit at UT="
                        + warpEndUT.ToString("R", CultureInfo.InvariantCulture)
                        + " — facility actions crossed in range ("
                        + warpStartUT.ToString("R", CultureInfo.InvariantCulture)
                        + ", "
                        + warpEndUT.ToString("R", CultureInfo.InvariantCulture)
                        + "]");
                }
            }
            wasWarpActive = isWarpNow;

            if (activeTree == null || backgroundRecorder == null) return;

            double ut = Planetarium.GetUniversalTime();
            float warpRate = TimeWarp.CurrentRate;
            string warpRateKey = warpRate.ToString("F1",
                System.Globalization.CultureInfo.InvariantCulture);

            bool sameCheckpointTree = ReferenceEquals(lastWarpCheckpointTree, activeTree);
            if (ShouldSkipDuplicateWarpCheckpointEvent(
                    warpRate,
                    ut,
                    hasLastWarpCheckpointEvent && sameCheckpointTree,
                    lastWarpCheckpointRate,
                    lastWarpCheckpointUT))
            {
                ParsekLog.VerboseRateLimited("Checkpoint",
                    $"warp-rate-duplicate-{warpRateKey}",
                    $"Duplicate time warp checkpoint ignored at {warpRateKey}x UT={ut:F2}");
                return;
            }

            hasLastWarpCheckpointEvent = true;
            lastWarpCheckpointTree = activeTree;
            lastWarpCheckpointRate = warpRate;
            lastWarpCheckpointUT = ut;

            // Bug #592: KSP fires onTimeWarpRateChanged very chattily — a single
            // playtest produced ~1090 events at 1.0x without any actual rate change
            // (scene transitions, warp-to-here, etc. retrigger the GameEvent).
            // Rate-limit by warpRate so transitions between distinct rates still log
            // immediately, but a burst of redundant 1x->1x fires collapses into one
            // line per window. Bug #597 additionally skips exact duplicate
            // same-tree/same-rate/same-UT checkpoint work before it reaches the recorder.
            ParsekLog.VerboseRateLimited("Checkpoint",
                $"warp-rate-changed-{warpRateKey}",
                $"Time warp rate changed to {warpRateKey}x " +
                $"at UT={ut:F2} — checkpointing all background vessels");

            // Checkpoint background vessels first (before any state changes)
            backgroundRecorder.CheckpointAllVessels(ut);

            // The active vessel's orbit segments are already handled by
            // onVesselGoOnRails/onVesselGoOffRails events which fire when
            // time warp transitions between physics and rails modes.
            ParsekLog.VerboseRateLimited("Checkpoint",
                $"warp-rate-changed-on-rails-{warpRateKey}",
                "Active vessel orbit segments handled by on-rails events");
        }

        internal static void RecalculateLedgerAfterWarpExit(double warpEndUT)
        {
            RecalculateLedgerAfterWarpExit(
                warpEndUT,
                LedgerOrchestrator.RecalculateAndPatchForTimeJump);
        }

        internal static void RecalculateLedgerAfterWarpExit(
            double warpEndUT,
            Action<double> recalculateAndPatchForTimeJump)
        {
            if (recalculateAndPatchForTimeJump == null)
                throw new ArgumentNullException(nameof(recalculateAndPatchForTimeJump));

            try
            {
                ParsekLog.Info("LedgerOrchestrator",
                    "Warp exit ledger recalculation started: cutoffUT="
                    + warpEndUT.ToString("R", CultureInfo.InvariantCulture));
                recalculateAndPatchForTimeJump(warpEndUT);
                ParsekLog.Info("LedgerOrchestrator",
                    "Warp exit ledger recalculation complete: cutoffUT="
                    + warpEndUT.ToString("R", CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("LedgerOrchestrator",
                    "Warp exit ledger recalculation failed: cutoffUT="
                    + warpEndUT.ToString("R", CultureInfo.InvariantCulture)
                    + " error="
                    + ex.GetType().Name
                    + ": "
                    + ex.Message);
            }
        }

        internal static bool ShouldSkipDuplicateWarpCheckpointEvent(
            float currentWarpRate,
            double currentUT,
            bool hasLastEvent,
            float lastWarpRate,
            double lastUT)
        {
            if (!hasLastEvent)
                return false;

            if (float.IsNaN(currentWarpRate) || float.IsInfinity(currentWarpRate) ||
                double.IsNaN(currentUT) || double.IsInfinity(currentUT) ||
                float.IsNaN(lastWarpRate) || float.IsInfinity(lastWarpRate) ||
                double.IsNaN(lastUT) || double.IsInfinity(lastUT))
                return false;

            const double RateEpsilon = 0.0001;
            const double UTEpsilon = 0.000001;
            return Math.Abs((double)currentWarpRate - lastWarpRate) <= RateEpsilon
                && Math.Abs(currentUT - lastUT) <= UTEpsilon;
        }

        void OnPartCouple(GameEvents.FromToAction<Part, Part> data)
        {
            // Vessel names (design 7.7): stock fires onPartCouple from Part.Couple before the
            // merge, with data.from on the vessel being absorbed (ModuleDockingNode.DockToVessel
            // couples its own part to the dominant's). Recorded whatever the recorder state.
            VesselNameLiveSync.OnDockCouple(data.from?.vessel, data.to?.vessel);

            // #267: skip if restore coroutine is mid-yield — it owns activeTree/recorder.
            // OnPartCouple's own null checks (L3784, L3845) already prevent most interference,
            // but the narrow window after recorder.StartRecording in the coroutine could allow
            // a dock event on the just-restored recorder before the coroutine completes.
            if (restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    "OnPartCouple: skipped — restore coroutine in progress");
                return;
            }

            RecorderStateLog.RecState("OnPartCouple:entry", CaptureRecorderState());
            if (data.to?.vessel == null) return;
            uint mergedPid = data.to.vessel.persistentId;

            // Classify the connection producer NOW, while both event parts still
            // resolve (design-logistics-claw-producer.md 2.1). A claw couple fires
            // the same onPartCouple as a dock (findings 1.2) but must be stamped
            // Grapple, and an unrecognized (modded) coupling producer is stamped
            // Unknown so route admission fails closed instead of treating it as a
            // dock. An EVA kerbal grab is a real couple but never a logistics
            // transfer: suppress its route window by dropping the route target.
            RouteConnectionKind coupleTransferKind =
                ConnectionProducerClassifier.Classify(data.from, data.to);
            bool coupleInvolvesEva =
                ConnectionProducerClassifier.InvolvesEvaVessel(data.from, data.to);
            ParsekLog.Info("Flight",
                $"OnPartCouple producer classified: kind={coupleTransferKind} " +
                $"fromPart={data.from?.partInfo?.name ?? "<null>"} " +
                $"toPart={data.to?.partInfo?.name ?? "<null>"} involvesEva={coupleInvolvesEva}");

            // Phase 9 (design doc §12, §18 Phase 9): structural-event snapshot at the
            // exact dock UT for the recorded vessel. Done BEFORE
            // StopRecordingForChainBoundary so the snapshot lands in the active section
            // (the helper no-ops once IsRecording flips false). Both vessels in the
            // dock pair are passed; the focused recorder filters by RecordingVesselId
            // and the background recorder filters by BackgroundMap membership.
            //
            // Skip the snapshot when this couple is the abort half of a split-in-
            // progress: the tree-mode path below early-returns at the
            // `pendingSplitInProgress` guard without committing the merge, and a
            // synthetic flagged point would otherwise land in a recording whose
            // section is about to be torn down by the in-flight split. Mirror the
            // exact same guard so the snapshot only fires on couples that the
            // downstream merge path will actually act on.
            if (recorder != null && recorder.IsRecording && !pendingSplitInProgress)
            {
                double dockEventUT = Planetarium.GetUniversalTime();
                var dockInvolved = new List<Vessel>(2);
                if (data.from?.vessel != null) dockInvolved.Add(data.from.vessel);
                if (data.to?.vessel != null && data.to.vessel != data.from?.vessel)
                    dockInvolved.Add(data.to.vessel);
                recorder.AppendStructuralEventSnapshot(dockEventUT, dockInvolved, "Dock");
                backgroundRecorder?.AppendStructuralEventSnapshot(dockEventUT, dockInvolved, "Dock");

                // Capture partner pre-couple snapshot for route window endpoint baseline.
                // When data.from / data.to still reference distinct vessels (KSP has not
                // reparented yet), the partner is the side that isn't the recorder. The
                // route window's DOCK_ENDPOINT_RESOURCES must capture the partner's
                // pre-dock state; falling back to the post-couple merged-vessel snapshot
                // inflates the baseline with the transport's tank.
                pendingDockPartnerSnapshot = null;
                pendingDockPartnerSnapshotPid = 0u;
                pendingDockSelfSnapshot = null;
                pendingDockSelfSnapshotPid = 0u;
                if (data.from?.vessel != null && data.to?.vessel != null &&
                    data.from.vessel != data.to.vessel)
                {
                    Vessel partner = null;
                    if (data.from.vessel.persistentId == recorder.RecordingVesselId
                        && data.to.vessel.persistentId != recorder.RecordingVesselId)
                    {
                        partner = data.to.vessel;
                    }
                    else if (data.to.vessel.persistentId == recorder.RecordingVesselId
                        && data.from.vessel.persistentId != recorder.RecordingVesselId)
                    {
                        partner = data.from.vessel;
                    }
                    else
                    {
                        // Neither side matches the recorder's vessel (rare; e.g. the
                        // recorder was on a third vessel observing). Prefer the side
                        // whose pid will not be the surviving merged pid so the snapshot
                        // captures pre-couple state. data.to is the survivor in KSP.
                        partner = data.from.vessel.persistentId != mergedPid
                            ? data.from.vessel
                            : data.to.vessel;
                    }

                    if (partner != null && partner.parts != null && partner.parts.Count > 0)
                    {
                        pendingDockPartnerSnapshot = FlightRecorder.TryBackupSnapshot(partner);
                        pendingDockPartnerSnapshotPid = partner.persistentId;
                        ParsekLog.Verbose("Flight",
                            $"OnPartCouple: captured pre-couple partner snapshot " +
                            $"partnerPid={pendingDockPartnerSnapshotPid} parts={partner.parts.Count}");
                    }

                    // Capture the recorder's own (transport) pre-couple snapshot for the
                    // route window transport baseline (mirror of the partner snapshot above).
                    Vessel self = ResolveSelfVessel(data, recorder.RecordingVesselId);
                    if (self != null && self.parts != null && self.parts.Count > 0)
                    {
                        pendingDockSelfSnapshot = FlightRecorder.TryBackupSnapshot(self);
                        pendingDockSelfSnapshotPid = self.persistentId;
                        ParsekLog.Verbose("Flight",
                            $"OnPartCouple: captured pre-couple self snapshot " +
                            $"selfPid={pendingDockSelfSnapshotPid} parts={self.parts.Count}");
                    }
                }
            }

            // --- TREE MODE: create merge branch ---
            if (activeTree != null && recorder != null)
            {
                if (pendingSplitInProgress) return; // split must complete first

                if (recorder.IsRecording)
                {
                    bool isTarget = (recorder.RecordingVesselId == mergedPid);
                    uint absorbedPid;
                    uint fromPid = data.from?.vessel != null ? data.from.vessel.persistentId : 0u;
                    uint toPid = data.to?.vessel != null ? data.to.vessel.persistentId : 0u;
                    uint partnerPidFromEvent = ResolveDockPartnerPidFromEvent(
                        fromPid, toPid, recorder.RecordingVesselId);

                    if (isTarget)
                    {
                        // We are the TARGET -- the absorbed partner is the couple event's
                        // partner when it is a BackgroundMap member of this tree, else 0.
                        absorbedPid = ResolveTargetSideAbsorbedPid(
                            partnerPidFromEvent, mergedPid, activeTree.BackgroundMap.Keys);
                        ParsekLog.Verbose("Flight",
                            $"OnPartCouple target-side absorbed partner: absorbedPid={absorbedPid} " +
                            $"partnerPidFromEvent={partnerPidFromEvent} " +
                            $"partnerInBackgroundMap={activeTree.BackgroundMap.ContainsKey(partnerPidFromEvent)}");
                    }
                    else
                    {
                        // We are the INITIATOR -- we are being absorbed
                        absorbedPid = recorder.RecordingVesselId;
                    }

                    // Resolve the route-partner PID from the couple event directly.
                    // BackgroundMap is intra-tree only, so a partner with a committed
                    // recording from a prior tree is not a BackgroundMap member.
                    //
                    // Accept the partner as route-eligible when EITHER:
                    //  (a) the pre-couple partner snapshot was captured successfully
                    //      (the partner is a real KSP vessel with parts that was
                    //      distinct from the recorder at couple time — third-playtest
                    //      scenario where the destination is loaded from save state
                    //      but has no prior Parsek recording), OR
                    //  (b) the partner has a known Parsek recording in the active tree
                    //      or in committed recordings (yesterday's cross-tree case).
                    // Both paths trust real evidence; the stricter "must have prior
                    // recording" gate from the first iteration was over-conservative
                    // for the destination-loaded-from-save workflow.
                    bool partnerSnapshotCaptured = pendingDockPartnerSnapshot != null
                        && pendingDockPartnerSnapshotPid == partnerPidFromEvent
                        && partnerPidFromEvent != 0u;
                    bool partnerKnown = IsKnownDockPartnerForRoute(
                        partnerPidFromEvent,
                        recorder.RecordingVesselId,
                        activeTree?.Recordings?.Values,
                        RecordingStore.CommittedRecordings);
                    bool partnerEligible = partnerPidFromEvent != 0u
                        && partnerPidFromEvent != recorder.RecordingVesselId
                        && (partnerSnapshotCaptured || partnerKnown);
                    uint routeTargetPid = SuppressRouteWindowForEvaGrab(
                        partnerEligible ? partnerPidFromEvent : 0u,
                        coupleInvolvesEva, coupleTransferKind, pathLabel: "");
                    bool partnerIsEva = ConnectionProducerClassifier.PartnerIsEvaVessel(
                        data.from, data.to, partnerPidFromEvent);
                    uint partnerStampPid = ResolveBranchPartnerStampPid(
                        partnerPidFromEvent, recorder.RecordingVesselId, partnerIsEva);
                    string partnerStampGuid = ResolveDockPartnerGuidFromEvent(
                        partnerStampPid,
                        fromPid, AnchorDetector.TryReadLiveVesselGuid(data.from?.vessel),
                        toPid, AnchorDetector.TryReadLiveVesselGuid(data.to?.vessel));

                    ParsekLog.Verbose("Flight",
                        $"OnPartCouple route-partner resolve: selfPid={recorder.RecordingVesselId} " +
                        $"mergedPid={mergedPid} isTarget={isTarget} absorbedPid={absorbedPid} " +
                        $"partnerPidFromEvent={partnerPidFromEvent} " +
                        $"partnerSnapshotCaptured={partnerSnapshotCaptured} partnerKnown={partnerKnown} " +
                        $"partnerEligible={partnerEligible} routeTargetPid={routeTargetPid} " +
                        $"partnerIsEva={partnerIsEva} partnerPidStamped={partnerStampPid} " +
                        $"partnerGuidStamped={partnerStampGuid ?? "(none)"}");

                    CapturePendingDockRouteEndpointProof(data, routeTargetPid);

                    // Stop recorder synchronously
                    recorder.StopRecordingForChainBoundary();

                    // Set pending state for tree merge
                    pendingTreeDockMerge = true;
                    pendingDockMergedPid = mergedPid;
                    pendingDockAbsorbedPid = absorbedPid;
                    pendingDockRouteTargetPid = routeTargetPid;
                    pendingDockPartnerPid = partnerStampPid;
                    pendingDockPartnerGuid = partnerStampGuid;
                    pendingDockTransferKind = coupleTransferKind;
                    dockConfirmFrames = 0;

                    // Race condition guard: prevent Task 6 from misclassifying absorption as destruction
                    if (absorbedPid != 0)
                        dockingInProgress.Add(absorbedPid);

                    Log($"onPartCouple (tree): dock merge pending " +
                        $"(merged={mergedPid}, absorbed={absorbedPid}, isTarget={isTarget})");
                    return;
                }
                else if (!recorder.IsRecording && recorder.CaptureAtStop != null)
                {
                    // OnPhysicsFrame already stopped us (initiator, late event).
                    // Target-side recorders are stopped synchronously by us at the top of
                    // this handler, never by OnPhysicsFrame, so reaching here with a
                    // target-side recorder (recorder PID still equal to mergedPid) means
                    // some other code path got there first -- bail out of route capture
                    // rather than misroute the endpoint.
                    uint absorbedPid = recorder.RecordingVesselId;
                    if (absorbedPid == mergedPid)
                    {
                        ParsekLog.Warn("Flight",
                            $"onPartCouple (tree, retroactive): unexpected target-side pre-stop " +
                            $"mergedPid={mergedPid}; skipping route endpoint capture");
                        return;
                    }
                    // Mirror the recording-path resolver: derive partner from event,
                    // validate via known-recordings registry, and emit the same diagnostic.
                    uint fromPidR = data.from?.vessel != null ? data.from.vessel.persistentId : 0u;
                    uint toPidR = data.to?.vessel != null ? data.to.vessel.persistentId : 0u;
                    uint partnerPidFromEventR = ResolveDockPartnerPidFromEvent(
                        fromPidR, toPidR, recorder.RecordingVesselId);

                    // Gap §5.1: also try to capture a pre-couple partner snapshot in
                    // the retroactive path. By the time we reach this branch
                    // OnPhysicsFrame has already stopped the recorder, so KSP may
                    // have ALREADY reparented data.from.vessel onto data.to.vessel
                    // and the snapshot would be the merged vessel (over-counted). We
                    // only capture when data.from.vessel and data.to.vessel are
                    // still distinct; if they aren't, fallbacks downstream
                    // (TryDeriveEndpointPartPidsFromPartner) carry the accepted
                    // baseline-inflation risk noted in §5.1. Captured BEFORE the
                    // eligibility check so partnerSnapshotCaptured can vote yes.
                    pendingDockPartnerSnapshot = null;
                    pendingDockPartnerSnapshotPid = 0u;
                    pendingDockSelfSnapshot = null;
                    pendingDockSelfSnapshotPid = 0u;
                    if (data.from?.vessel != null && data.to?.vessel != null &&
                        data.from.vessel != data.to.vessel)
                    {
                        Vessel partnerR = data.from.vessel.persistentId != absorbedPid
                            ? data.from.vessel
                            : (data.to.vessel.persistentId != absorbedPid ? data.to.vessel : null);
                        if (partnerR != null && partnerR.parts != null && partnerR.parts.Count > 0)
                        {
                            pendingDockPartnerSnapshot = FlightRecorder.TryBackupSnapshot(partnerR);
                            pendingDockPartnerSnapshotPid = partnerR.persistentId;
                            ParsekLog.Verbose("Flight",
                                $"OnPartCouple (retroactive): captured pre-couple partner snapshot " +
                                $"partnerPid={pendingDockPartnerSnapshotPid} parts={partnerR.parts.Count}");
                        }

                        // Mirror: the recorder's own (transport) pre-couple snapshot. In the
                        // retroactive path the recorder is already stopped and KSP may have
                        // reparented; this only fires while from/to are still distinct, else
                        // it stays null and the merged snapshot is used (accepted §5.1 risk).
                        Vessel selfR = ResolveSelfVessel(data, recorder.RecordingVesselId);
                        if (selfR != null && selfR.parts != null && selfR.parts.Count > 0)
                        {
                            pendingDockSelfSnapshot = FlightRecorder.TryBackupSnapshot(selfR);
                            pendingDockSelfSnapshotPid = selfR.persistentId;
                            ParsekLog.Verbose("Flight",
                                $"OnPartCouple (retroactive): captured pre-couple self snapshot " +
                                $"selfPid={pendingDockSelfSnapshotPid} parts={selfR.parts.Count}");
                        }
                    }

                    // Eligibility: same dual-evidence test as the live-recording path.
                    // Accept the partner when EITHER the pre-couple snapshot was
                    // captured (real KSP vessel) OR it has a known Parsek recording.
                    bool partnerSnapshotCapturedR = pendingDockPartnerSnapshot != null
                        && pendingDockPartnerSnapshotPid == partnerPidFromEventR
                        && partnerPidFromEventR != 0u;
                    bool partnerKnownR = IsKnownDockPartnerForRoute(
                        partnerPidFromEventR,
                        recorder.RecordingVesselId,
                        activeTree?.Recordings?.Values,
                        RecordingStore.CommittedRecordings);
                    bool partnerEligibleR = partnerPidFromEventR != 0u
                        && partnerPidFromEventR != recorder.RecordingVesselId
                        && (partnerSnapshotCapturedR || partnerKnownR);
                    uint routeTargetPid = SuppressRouteWindowForEvaGrab(
                        partnerEligibleR ? partnerPidFromEventR : 0u,
                        coupleInvolvesEva, coupleTransferKind, pathLabel: " (retroactive)");
                    bool partnerIsEvaR = ConnectionProducerClassifier.PartnerIsEvaVessel(
                        data.from, data.to, partnerPidFromEventR);
                    uint partnerStampPidR = ResolveBranchPartnerStampPid(
                        partnerPidFromEventR, recorder.RecordingVesselId, partnerIsEvaR);
                    string partnerStampGuidR = ResolveDockPartnerGuidFromEvent(
                        partnerStampPidR,
                        fromPidR, AnchorDetector.TryReadLiveVesselGuid(data.from?.vessel),
                        toPidR, AnchorDetector.TryReadLiveVesselGuid(data.to?.vessel));

                    ParsekLog.Verbose("Flight",
                        $"OnPartCouple route-partner resolve (retroactive): " +
                        $"selfPid={recorder.RecordingVesselId} mergedPid={mergedPid} " +
                        $"absorbedPid={absorbedPid} partnerPidFromEvent={partnerPidFromEventR} " +
                        $"partnerSnapshotCaptured={partnerSnapshotCapturedR} partnerKnown={partnerKnownR} " +
                        $"partnerEligible={partnerEligibleR} routeTargetPid={routeTargetPid} " +
                        $"partnerIsEva={partnerIsEvaR} partnerPidStamped={partnerStampPidR} " +
                        $"partnerGuidStamped={partnerStampGuidR ?? "(none)"}");

                    CapturePendingDockRouteEndpointProof(data, routeTargetPid);

                    pendingTreeDockMerge = true;
                    pendingDockMergedPid = mergedPid;
                    pendingDockAbsorbedPid = absorbedPid;
                    pendingDockRouteTargetPid = routeTargetPid;
                    pendingDockPartnerPid = partnerStampPidR;
                    pendingDockPartnerGuid = partnerStampGuidR;
                    pendingDockTransferKind = coupleTransferKind;
                    dockConfirmFrames = 0;

                    if (absorbedPid != 0)
                        dockingInProgress.Add(absorbedPid);

                    Log($"onPartCouple (tree, retroactive): dock merge pending " +
                        $"(merged={mergedPid}, absorbed={absorbedPid})");
                    return;
                }
            }

            // No live tree recorder in a state a dock merge applies to: no tree, no
            // recorder, or a recorder stopped without a capture. Every recording runs
            // inside a tree, so nothing else consumes the couple.
            ParsekLog.Verbose("Flight",
                $"onPartCouple: no dock merge armed (mergedPid={mergedPid}, hasTree={activeTree != null}, " +
                $"hasRecorder={recorder != null}, recording={recorder != null && recorder.IsRecording}, " +
                $"hasCapture={recorder != null && recorder.CaptureAtStop != null})");
        }

        void OnPartUndock(Part undockedPart)
        {
            // #267: skip if restore coroutine is mid-yield — it owns activeTree/recorder
            if (restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    $"OnPartUndock: skipped — restore coroutine in progress " +
                    $"(part '{undockedPart?.partInfo?.name}')");
                ClearPendingUndockSeed();
                return;
            }

            RecorderStateLog.RecState("OnPartUndock:entry", CaptureRecorderState());
            ClearPendingUndockSeed();
            if (recorder == null || !recorder.IsRecording) return;
            if (pendingSplitInProgress) return; // another split is already being processed
            if (undockedPart?.vessel == null)
            {
                ParsekLog.Verbose("Flight", "OnPartUndock: undocked part or vessel is null — ignoring");
                return;
            }

            // onPartUndock fires FIRST inside KSP's Part.Undock(), BEFORE the vessel is
            // split (the part still reports the OLD combined vessel persistentId here, so
            // we deliberately do NOT branch off this pid). KSP creates the new vessel and
            // fires onVesselsUndocking(oldVessel, newVessel) at the END of the same call,
            // where both pids are final. We capture the structural-event snapshot and the
            // departing root part's pre-split origin seed NOW (combined vessel intact, part
            // still attached, the ideal moment) and stash the seed for OnVesselsUndocking,
            // which authors the actual Undock branch (and, via CreateSplitBranch, completes
            // any open logistics route-connection window). See the undock-not-recorded fix.
            double undockEventUT = Planetarium.GetUniversalTime();
            var undockInvolved = new List<Vessel>(1) { undockedPart.vessel };
            recorder.AppendStructuralEventSnapshot(undockEventUT, undockInvolved, "Undock");
            backgroundRecorder?.AppendStructuralEventSnapshot(undockEventUT, undockInvolved, "Undock");

            uint undockedPartPid = undockedPart.persistentId;
            uint undockedRootPartPid = undockedPart.vessel.rootPart?.persistentId ?? 0u;
            if (ShouldUseUndockPartOriginSeed(undockedPartPid, undockedRootPartPid)
                && FlightRecorder.TryCreateAbsoluteTrajectoryPointFromPartOrigin(
                    undockedPart,
                    undockEventUT,
                    out TrajectoryPoint capturedUndockSeed))
            {
                pendingUndockRootPartSeed = capturedUndockSeed;
                ParsekLog.Verbose("Flight",
                    $"Undock root part-origin seed captured: part='{undockedPart.partInfo?.name}' " +
                    $"pid={undockedPartPid} preSplitVesselPid={undockedPart.vessel.persistentId} " +
                    $"ut={undockEventUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"(deferring branch to onVesselsUndocking)");
            }
            else
            {
                string reason = ShouldUseUndockPartOriginSeed(undockedPartPid, undockedRootPartPid)
                    ? "capture-failed"
                    : "undocked-part-not-root";
                ParsekLog.Verbose("Flight",
                    $"Undock part-origin seed skipped: reason={reason} " +
                    $"undockedPartPid={undockedPartPid} rootPartPid={undockedRootPartPid} " +
                    $"preSplitVesselPid={undockedPart.vessel.persistentId} " +
                    $"(deferring branch to onVesselsUndocking)");
            }
        }

        /// <summary>
        /// Authoritative docking-port undock handler. KSP fires
        /// <c>onVesselsUndocking(oldVessel, newVessel)</c> at the end of <c>Part.Undock()</c>
        /// with both vessels fully split and carrying their final persistentIds, so unlike a
        /// stack/radial decoupler (which fires <c>onPartDeCoupleNewVesselComplete</c> and is
        /// caught by the joint-break fallback) we can map the undock directly. This is the
        /// fix for docking-port undocks being mis-recorded as within-segment part loss: the
        /// joint-break fallback can never see the new vessel for an undock, so it classifies
        /// the departing pod as PartDestroyed instead of branching an Undock.
        /// </summary>
        void OnVesselsUndocking(Vessel oldVessel, Vessel newVessel)
        {
            // Vessel names (design 7.7): the leaving vessel (fresh guid, re-rooted on its
            // pre-dock root part) continues the absorbed identity when a dock-absorb row names
            // its root. Linked whatever the recorder state.
            VesselNameLiveSync.OnUndockSplit(newVessel);

            // #267: skip if restore coroutine is mid-yield; it owns activeTree/recorder.
            if (restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    "OnVesselsUndocking: skipped, restore coroutine in progress");
                ClearPendingUndockSeed();
                return;
            }

            RecorderStateLog.RecState("OnVesselsUndocking:entry", CaptureRecorderState());

            // Snapshot + clear the pre-split seed stashed by OnPartUndock this same frame.
            TrajectoryPoint? undockRootPartSeed = pendingUndockRootPartSeed;
            ClearPendingUndockSeed();

            // A vehicle and a kerbal at the end is an EVA, never an Undock (owner ruling
            // 2026-10-24): stock's seat dismount and claw release of a kerbal reach here
            // through Part.Undock and fire no onCrewOnEva.
            if (oldVessel != null && newVessel != null
                && SegmentBoundaryLogic.ClassifyUndockShape(oldVessel.isEVA, newVessel.isEVA)
                    == UndockShape.KerbalLeftVehicle)
            {
                HandleKerbalLeftVehicleUndock(oldVessel, newVessel);
                return;
            }

            if (recorder == null || !recorder.IsRecording)
            {
                ParsekLog.Verbose("Flight",
                    "OnVesselsUndocking: not recording, ignoring");
                return;
            }
            if (pendingSplitInProgress)
            {
                ParsekLog.Verbose("Flight",
                    "OnVesselsUndocking: another split is already being processed, ignoring");
                return;
            }
            if (oldVessel == null || newVessel == null)
            {
                ParsekLog.Verbose("Flight",
                    $"OnVesselsUndocking: null vessel (old={(oldVessel != null ? "set" : "null")}, " +
                    $"new={(newVessel != null ? "set" : "null")}), ignoring");
                return;
            }

            uint recordedPid = recorder.RecordingVesselId;
            uint oldPid = oldVessel.persistentId;
            uint newPid = newVessel.persistentId;

            UndockSplitDecision decision =
                SegmentBoundaryLogic.ClassifyUndockSplit(recordedPid, oldPid, newPid);

            ParsekLog.Info("Flight",
                $"OnVesselsUndocking: recordedPid={recordedPid} oldPid={oldPid} " +
                $"(name='{oldVessel.vesselName}') newPid={newPid} (name='{newVessel.vesselName}') " +
                $"decision={decision}");

            if (decision == UndockSplitDecision.NotRecordedVessel)
            {
                ParsekLog.Verbose("Flight",
                    $"OnVesselsUndocking: undock does not involve the recorded vessel " +
                    $"(recordedPid={recordedPid}), leaving recorder running");
                return;
            }

            // The recorder maps to KSP's oldVessel (which keeps its persistentId); the new
            // vessel is the freshly split-off side. The deferred branch resolves which side
            // KSP gives focus to and backgrounds the OTHER, so we pass both pids rather than
            // pre-deciding the background here (focus can move to the departing vessel one
            // frame later via WaitAndSwitchFocus when the player kept controlling that pod).

            // The undock event IS the authoritative split signal. Consume the recorder's
            // pending joint-break check (set by the synchronous onPartJointBreak that
            // KSP's attachJoint.DestroyJoint() fired earlier in the same Part.Undock() call)
            // so HandleJointBreakDeferredCheck does NOT also run next frame and misclassify
            // the departing pod as within-segment PartDestroyed. Note: a genuine within-
            // segment break in the SAME physics frame as the undock would also be swallowed
            // here, but the broken part is still hidden on playback via its Decoupled
            // PartEvent (recorded by FlightRecorder.OnPartJointBreak); only the PartDestroyed
            // SegmentEvent label is lost, an acceptable trade for not double-processing.
            bool consumedJointBreak = recorder.ConsumePendingJointBreakCheck(out double jointBreakUT);
            if (consumedJointBreak)
            {
                ParsekLog.Verbose("Flight",
                    $"OnVesselsUndocking: consumed recorder pending joint-break check " +
                    $"(ut={jointBreakUT.ToString("F2", CultureInfo.InvariantCulture)}); " +
                    "undock owns this split, suppressing within-segment fallback");
            }

            // Tree mode: stop recorder synchronously to prevent OnPhysicsFrame interference,
            // then defer one frame so KSP fully settles the freshly created vessel before we
            // snapshot it (mirrors the proven decouple/EVA deferred-branch pattern).
            recorder.StopRecordingForChainBoundary();
            pendingSplitRecorder = recorder;
            recorder = null;
            pendingSplitInProgress = true;
            Log($"onVesselsUndocking: vessel split off (recordedPid={recordedPid}, " +
                $"oldPid={oldPid}, newPid={newPid}); starting deferred Undock branch");
            StartCoroutine(DeferredUndockBranch(oldPid, newPid, undockRootPartSeed));
        }

        private void ClearPendingUndockSeed()
        {
            pendingUndockRootPartSeed = null;
        }

        /// <summary>
        /// Not-recording half of <see cref="HandleKerbalLeftVehicleUndock"/>: the same routes
        /// as a hatch EVA (a tracked background parent branch, else the deferred auto-record),
        /// but only once the kerbal holds the focus. A seat dismount moves focus to the kerbal
        /// right after its undock (<c>ForceSetActiveVessel</c>); a claw release keeps it on the
        /// claw's vessel, and an auto-record armed there would wait for, and start on, whatever
        /// EVA kerbal the player next switches to.
        /// </summary>
        private IEnumerator DeferredKerbalLeftVehicleNotRecording(
            GameEvents.FromToAction<Part, Part> evaData, Vessel kerbalVessel)
        {
            yield return null;
            bool kerbalActive = ShouldRouteKerbalLeftVehicleNotRecording(
                isRecording: IsRecording,
                kerbalVesselAlive: kerbalVessel != null,
                kerbalIsActive: kerbalVessel != null
                    && ReferenceEquals(FlightGlobals.ActiveVessel, kerbalVessel));
            if (!kerbalActive)
            {
                ParsekLog.Verbose("Flight",
                    $"OnVesselsUndocking: kerbal-left-vehicle not routed as a not-recording EVA " +
                    $"(recording={IsRecording} kerbalAlive={kerbalVessel != null} " +
                    $"activePid={FlightGlobals.ActiveVessel?.persistentId ?? 0u})");
                yield break;
            }
            OnCrewOnEva(evaData);
        }

        /// <summary>Pure gate of <see cref="DeferredKerbalLeftVehicleNotRecording"/>.</summary>
        internal static bool ShouldRouteKerbalLeftVehicleNotRecording(
            bool isRecording, bool kerbalVesselAlive, bool kerbalIsActive)
        {
            return !isRecording && kerbalVesselAlive && kerbalIsActive;
        }

        /// <summary>
        /// An undock whose two ends are a vehicle and an EVA kerbal (a kerbal leaving an
        /// external command seat, a claw releasing a kerbal) is recorded as an EVA: the same
        /// deferred EVA branch a hatch EVA takes (kerbal name on the child, no Rewind Point,
        /// the vehicle keeps its own background leg whatever its crew or command state), and
        /// when nothing is recording, the same auto-record. The stock-EVA event shape is
        /// rebuilt from the two vessels (from = the vehicle, to = the kerbal's part); every
        /// consumer reads only the two parts' vessels and the kerbal part's crew.
        /// </summary>
        private void HandleKerbalLeftVehicleUndock(Vessel oldVessel, Vessel newVessel)
        {
            Vessel kerbalVessel = newVessel.isEVA ? newVessel : oldVessel;
            Vessel vehicleVessel = newVessel.isEVA ? oldVessel : newVessel;
            var evaData = new GameEvents.FromToAction<Part, Part>(
                vehicleVessel.rootPart, kerbalVessel.rootPart);
            string kerbalName = ExtractEvaKerbalName(evaData);
            uint recordedPid = recorder != null ? recorder.RecordingVesselId : 0u;

            ParsekLog.Info("Flight",
                $"OnVesselsUndocking: kerbal left vehicle, recording as EVA " +
                $"(kerbal='{kerbalName ?? "<none>"}' kerbalPid={kerbalVessel.persistentId} " +
                $"vehiclePid={vehicleVessel.persistentId} (name='{vehicleVessel.vesselName}') " +
                $"kerbalIsNewVessel={newVessel.isEVA} recording={IsRecording} recordedPid={recordedPid})");

            if (!IsRecording)
            {
                StartCoroutine(DeferredKerbalLeftVehicleNotRecording(evaData, kerbalVessel));
                return;
            }
            if (pendingSplitInProgress)
            {
                LogSplitSkip(
                    "OnVesselsUndocking-EVA",
                    "pendingSplitInProgress",
                    vehicleVessel.persistentId,
                    kerbalVessel.persistentId);
                return;
            }
            if (SegmentBoundaryLogic.ClassifyUndockSplit(
                    recordedPid, oldVessel.persistentId, newVessel.persistentId)
                == UndockSplitDecision.NotRecordedVessel)
            {
                ParsekLog.Verbose("Flight",
                    $"OnVesselsUndocking: kerbal-left-vehicle does not involve the recorded vessel " +
                    $"(recordedPid={recordedPid}), leaving recorder running");
                return;
            }
            if (string.IsNullOrEmpty(kerbalName))
            {
                ParsekLog.Warn("Flight",
                    $"OnVesselsUndocking: kerbal-left-vehicle has no crew name " +
                    $"(kerbalPid={kerbalVessel.persistentId}); not branching");
                return;
            }

            // Part.Undock destroyed the attach joint first (synchronous onPartJointBreak); the
            // EVA branch owns this split, so the within-segment fallback must not run too.
            if (recorder.ConsumePendingJointBreakCheck(out double jointBreakUT))
            {
                ParsekLog.Verbose("Flight",
                    $"OnVesselsUndocking: consumed recorder pending joint-break check " +
                    $"(ut={jointBreakUT.ToString("F2", CultureInfo.InvariantCulture)}); " +
                    "kerbal-left-vehicle EVA owns this split");
            }

            double evaEventUT = Planetarium.GetUniversalTime();
            var evaInvolved = new List<Vessel>(2) { vehicleVessel, kerbalVessel };
            recorder.AppendStructuralEventSnapshot(evaEventUT, evaInvolved, "EVA");
            backgroundRecorder?.AppendStructuralEventSnapshot(evaEventUT, evaInvolved, "EVA");

            recorder.StopRecordingForChainBoundary();
            pendingSplitRecorder = recorder;
            recorder = null;
            pendingSplitInProgress = true;
            Log($"Kerbal left vehicle through an undock: '{kerbalName}' - starting deferred EVA branch");
            StartCoroutine(DeferredEvaBranch(kerbalName, evaData));
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

        void OnAfterFlagPlanted(FlagSite flagSite)
        {
            if (flagSite == null || flagSite.vessel == null || flagSite.part == null) return;

            // Always stamp the planting date onto the plaque text
            double ut = Planetarium.GetUniversalTime();
            string dateStr = KSPUtil.PrintDate(ut, includeTime: true);
            flagSite.PlaqueText = FormatPlaqueWithDate(flagSite.PlaqueText, dateStr);

            ParsekLog.Verbose("Flight",
                $"Flag planted: '{flagSite.vessel.vesselName}' by '{flagSite.placedBy}' — date stamped");

            // Recording-specific: capture FlagEvent. The planting kerbal may be the
            // foreground recorder's vessel OR a background-tracked tree member: an EVA
            // kerbal re-controlled after a vessel switch stays in the tree's
            // BackgroundMap with the foreground recorder idle, yet can still plant a
            // flag. Capture into whichever recording owns the planting vessel so the
            // flag persists to the sidecar and replays during watch/playback.
            string placedBy = flagSite.placedBy ?? "";

            Vessel flagVessel = flagSite.vessel;
            CelestialBody body = flagVessel.mainBody;

            // Compute surface-relative rotation
            Quaternion worldRot = flagVessel.transform.rotation;
            Quaternion surfRot = Quaternion.Inverse(body.bodyTransform.rotation) * worldRot;

            var fe = new FlagEvent
            {
                ut = ut,
                flagSiteName = flagVessel.vesselName ?? "",
                placedBy = placedBy,
                plaqueText = flagSite.PlaqueText ?? "",
                flagURL = flagSite.part.flagURL ?? "",
                latitude = flagVessel.latitude,
                longitude = flagVessel.longitude,
                altitude = flagVessel.altitude,
                rotX = surfRot.x,
                rotY = surfRot.y,
                rotZ = surfRot.z,
                rotW = surfRot.w,
                bodyName = body.name
            };

            // 1) Foreground recorder owns the planting vessel.
            if (recorder != null && recorder.IsRecording
                && ShouldRecordFlagEvent(placedBy,
                    FlightRecorder.FindVesselByPid(recorder.RecordingVesselId)))
            {
                recorder.FlagEvents.Add(fe);
                ParsekLog.Info("Flight",
                    $"Flag event captured (foreground recorder pid={recorder.RecordingVesselId}): " +
                    $"'{fe.flagSiteName}' by '{fe.placedBy}' at " +
                    $"({fe.latitude:F4},{fe.longitude:F4},{fe.altitude:F1}) on {fe.bodyName}");
                return;
            }

            // 2) A background-tracked tree member owns the planting vessel.
            uint bgOwnerPid = ResolveBackgroundFlagOwnerPid(
                placedBy,
                activeTree?.BackgroundMap,
                pid => FlightRecorder.FindVesselByPid(pid)?.GetVesselCrew());
            if (bgOwnerPid != 0
                && activeTree != null
                && activeTree.BackgroundMap.TryGetValue(bgOwnerPid, out string bgRecId)
                && activeTree.Recordings.TryGetValue(bgRecId, out Recording bgRec)
                && bgRec != null)
            {
                AppendFlagEventToTreeRecording(bgRec, fe);
                ParsekLog.Info("Flight",
                    $"Flag event captured (background tree member pid={bgOwnerPid} rec={bgRecId}): " +
                    $"'{fe.flagSiteName}' by '{fe.placedBy}' at " +
                    $"({fe.latitude:F4},{fe.longitude:F4},{fe.altitude:F1}) on {fe.bodyName}");
                return;
            }

            ParsekLog.Verbose("Flight",
                $"Flag planted by '{placedBy}' but no foreground or background recording owns " +
                $"the planting vessel - skipping (recorderPid={(recorder != null ? recorder.RecordingVesselId : 0)}, " +
                $"hasTree={activeTree != null})");
        }

        /// <summary>
        /// A kerbal took a flag down (<see cref="FlagPickUp"/>): record it on the recording that
        /// owns the picking kerbal, the foreground recorder's vessel or a background tree member,
        /// so a replay takes the flag down at the same moment.
        /// </summary>
        void TryCaptureFlagPickUp(Vessel flag, uint pickerPid)
        {
            if (flag == null || pickerPid == 0u)
                return;
            CelestialBody body = flag.mainBody;
            double ut = Planetarium.GetUniversalTime();
            SegmentEvent evt = FlagPickUp.BuildEvent(
                ut, body != null ? body.name : null, flag.latitude, flag.longitude, flag.altitude, flag.vesselName);

            if (recorder != null && recorder.IsRecording && recorder.RecordingVesselId == pickerPid)
            {
                // Straight onto the active tree recording when it exists: the mid-flight
                // serialization flush (FlushRecorderIntoActiveTreeForSerialization) does not move
                // recorder.SegmentEvents, so an event left there would miss an F5 and be lost on F9.
                string activeRecId = activeTree?.ActiveRecordingId;
                if (activeRecId != null
                    && activeTree.Recordings.TryGetValue(activeRecId, out Recording activeRec)
                    && activeRec != null)
                {
                    AppendSegmentEventToTreeRecording(activeRec, evt);
                }
                else
                {
                    recorder.SegmentEvents.Add(evt);
                }
                ParsekLog.Info("Flight",
                    $"Flag pick-up captured (foreground recorder pid={pickerPid} rec={activeRecId ?? "(recorder buffer)"}): " +
                    $"'{flag.vesselName}' on {(body != null ? body.name : "?")} ut={ut.ToString("R", CultureInfo.InvariantCulture)}");
                return;
            }

            if (activeTree != null
                && activeTree.BackgroundMap.TryGetValue(pickerPid, out string bgRecId)
                && activeTree.Recordings.TryGetValue(bgRecId, out Recording bgRec)
                && bgRec != null)
            {
                AppendSegmentEventToTreeRecording(bgRec, evt);
                ParsekLog.Info("Flight",
                    $"Flag pick-up captured (background tree member pid={pickerPid} rec={bgRecId}): " +
                    $"'{flag.vesselName}' ut={ut.ToString("R", CultureInfo.InvariantCulture)}");
                return;
            }

            ParsekLog.Verbose("Flight",
                $"Flag '{flag.vesselName}' picked up by pid={pickerPid} but no foreground or background " +
                $"recording owns the kerbal - not recorded (recorderPid={(recorder != null ? recorder.RecordingVesselId : 0)}, " +
                $"hasTree={activeTree != null})");
        }

        /// <summary>
        /// Appends a segment event to a tree recording in stable UT order and marks its sidecar
        /// dirty, the segment-event twin of <see cref="AppendFlagEventToTreeRecording"/>.
        /// </summary>
        internal static void AppendSegmentEventToTreeRecording(Recording rec, SegmentEvent evt)
        {
            if (rec == null)
                return;
            rec.SegmentEvents.Add(evt);
            var sorted = FlightRecorder.StableSortByUT(rec.SegmentEvents, e => e.ut);
            rec.SegmentEvents.Clear();
            rec.SegmentEvents.AddRange(sorted);
            rec.MarkFilesDirty();
        }

        /// <summary>
        /// Appends the planting date to the plaque text, right-aligned via TMP rich text.
        /// </summary>
        internal static string FormatPlaqueWithDate(string originalText, string formattedDate)
        {
            if (string.IsNullOrEmpty(formattedDate))
                return originalText ?? "";

            if (string.IsNullOrEmpty(originalText))
                return formattedDate;

            return originalText + " - " + formattedDate;
        }

        /// <summary>
        /// Checks whether a flag planting event should be recorded for the given vessel.
        /// The planting kerbal must be the recorded vessel (EVA kerbal) or crew on the recorded vessel.
        /// </summary>
        internal static bool ShouldRecordFlagEvent(string placedBy, Vessel recordedVessel)
        {
            if (string.IsNullOrEmpty(placedBy) || recordedVessel == null) return false;

            return CrewContainsKerbalNamed(recordedVessel.GetVesselCrew(), placedBy);
        }

        internal static bool CrewContainsKerbalNamed(List<ProtoCrewMember> crew, string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName) || crew == null)
                return false;

            for (int i = 0; i < crew.Count; i++)
            {
                if (crew[i]?.name == kerbalName)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Finds the background-tracked vessel pid whose live crew contains the flag
        /// planter, so a flag planted by a background tree member (e.g. an EVA kerbal
        /// re-controlled after a vessel switch) can be captured into its recording.
        /// Returns 0 when no background vessel matches. Pure decision helper (crew
        /// lookup injected) so it is unit-testable without a live tree.
        /// </summary>
        internal static uint ResolveBackgroundFlagOwnerPid(
            string placedBy,
            IEnumerable<KeyValuePair<uint, string>> backgroundMap,
            Func<uint, List<ProtoCrewMember>> crewLookup)
        {
            if (string.IsNullOrEmpty(placedBy) || backgroundMap == null || crewLookup == null)
                return 0;

            foreach (var entry in backgroundMap)
            {
                if (entry.Key == 0)
                    continue;
                if (CrewContainsKerbalNamed(crewLookup(entry.Key), placedBy))
                    return entry.Key;
            }

            return 0;
        }

        /// <summary>
        /// Appends a captured flag to a tree recording's FlagEvents, keeping the list
        /// in stable UT order and marking the recording's sidecar dirty so the new
        /// event persists. Used for background-tracked tree members whose FlagEvents
        /// are written straight to the recording; the foreground recorder sorts and
        /// marks dirty at flush time instead. The MarkFilesDirty call is load-bearing:
        /// without it the .prec sidecar is skipped on the next OnSave and the flag is
        /// lost on scene reload (see Recording.MarkFilesDirty).
        /// </summary>
        internal static void AppendFlagEventToTreeRecording(Recording rec, FlagEvent fe)
        {
            if (rec == null)
                return;

            rec.FlagEvents.Add(fe);

            // Stable UT order: ApplyFlagEvents/SpawnFlagVesselsUpToUT cursor-walk the
            // list and break on the first future-UT event, so a recording reused
            // across rewind/re-fly that already holds a higher-UT flag would otherwise
            // shadow the new one. Mirrors the foreground flush sort (#287).
            var sorted = FlightRecorder.StableSortByUT(rec.FlagEvents, e => e.ut);
            rec.FlagEvents.Clear();
            rec.FlagEvents.AddRange(sorted);

            rec.MarkFilesDirty();
        }

        // Captures the scene-entry vessel pid for the fresh-launch restore guard.
        // Runs once during Start; the captured pid is then the only one the guard
        // rejects, so vessel switches to other already-spawned committed vessels
        // in the same scene keep resuming their recordings.
        private void CaptureFreshRolloutVesselPidIfApplicable()
        {
            // Default to 0 every scene so a non-fresh startup (resumed save / revert)
            // leaves the shared static cleared and the crew-swap call sites keep
            // running orphan placement. The instance field defaults to 0 per new
            // ParsekFlight; the static must be reset explicitly.
            freshRolloutVesselPid = 0;
            SceneEntryVesselScope.SceneEntryFreshRolloutVesselPid = 0;

            FlightDriver.StartupBehaviours startup = FlightDriver.StartupBehaviour;
            if (!IsFreshLaunchStartupBehaviour(startup))
                return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null)
            {
                ParsekLog.Warn("Flight",
                    $"FreshRollout: cannot capture scene-entry vessel pid — " +
                    $"FlightGlobals.ActiveVessel is null at Start (StartupBehaviour={startup}); " +
                    "fresh-launch restore guard is inactive for this scene");
                return;
            }
            freshRolloutVesselPid = v.persistentId;
            // Publish to the shared static so SwapReservedCrewInFlight can suppress
            // Pass-2 orphan placement for this fresh launch from any call site
            // (flight-ready, chain-commit, tree-commit) without a ParsekFlight handle.
            SceneEntryVesselScope.SceneEntryFreshRolloutVesselPid = freshRolloutVesselPid;
            ParsekLog.Info("Flight",
                $"FreshRollout: captured scene-entry vessel pid={freshRolloutVesselPid} " +
                $"('{v.vesselName}', StartupBehaviour={startup}) — committed-tree restore " +
                "will skip only this pid for the lifetime of this scene");
        }

        /// <summary>
        /// Bug 1 (post-#876 playtest 2026-05-17): single dispatch point for the
        /// stock-action intent consume helper. Called at the top of
        /// <see cref="OnFlightReady"/> after the in-progress-restore-coroutine
        /// skip but before <see cref="ShouldIgnoreFlightReadyReset"/>, so the
        /// consume runs on EVERY OnFlightReady path that has a valid live active
        /// vessel — both the "live recorder/tree already own this flight"
        /// early-return path (TS Fly / KSC Fly after
        /// <see cref="TryRestoreCommittedTreeForSpawnedActiveVessel"/> pre-attached
        /// the recorder) and the full-reset path.
        ///
        /// <para>The consume helper's own gates (intent != null, freshness, target-
        /// PID match) keep this a no-op when no intent is armed.</para>
        ///
        /// <para>Skipped intentionally when <c>restoringActiveTree=true</c> because
        /// the restore coroutine is rebuilding <c>activeTree</c> and consume mutating
        /// tree state mid-restore would race that coroutine. The intent marker
        /// stays armed; the TTL handles the leak.</para>
        ///
        /// <para>M1 (PR #876 final review): also skipped when
        /// <see cref="ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady"/>
        /// is non-<c>None</c> — i.e. when <c>OnLoad</c> has scheduled a restore
        /// coroutine that has not started yet. If the consume runs here it can
        /// install a fresh standalone <c>activeTree</c> and bind the recorder,
        /// then <see cref="ResetFlightReadyState"/> a few lines down nulls
        /// both, leaving <see cref="SwitchSegmentSession.TreeId"/> pointing at
        /// a dead tree. The restore coroutines themselves invoke this helper
        /// once they successfully install the restored tree, so the consume
        /// still runs — just deferred until the tree is alive. If the restore
        /// fails or aborts, the marker's TTL handles the leak.</para>
        /// </summary>
        private void DispatchConsumeIntentIfArmed()
        {
            // M1: defer the consume when a restore is pending — the restore
            // coroutines re-invoke this helper on success. Without this gate
            // the consume could install a fresh standalone tree right before
            // ResetFlightReadyState nulls it, leaving SwitchSegmentSession
            // pointing at a dead TreeId.
            var pendingRestoreMode =
                ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady;
            if (pendingRestoreMode != ParsekScenario.ActiveTreeRestoreMode.None)
            {
                var pendingScenario = ParsekScenario.Instance;
                var pendingMarker = !object.ReferenceEquals(null, pendingScenario)
                    ? pendingScenario.CurrentStockActionIntent
                    : null;
                string pendingIntentId = pendingMarker != null
                    ? pendingMarker.IntentId.ToString("D",
                        System.Globalization.CultureInfo.InvariantCulture)
                    : "<none>";
                ParsekLog.Verbose("SwitchIntent",
                    $"consume-deferred-pending-restore restoreMode={pendingRestoreMode} " +
                    $"intentId={pendingIntentId}");
                return;
            }

            var activeVesselForConsume = FlightGlobals.ActiveVessel;
            if (activeVesselForConsume == null
                || GhostMapPresence.IsGhostMapVessel(activeVesselForConsume.persistentId))
            {
                return;
            }
            var consumeResult = TryConsumeStockActionIntent(activeVesselForConsume);
            if (consumeResult.StartedSegment)
            {
                DisarmPostSwitchAutoRecord("consumed-by-stock-action-segment");
            }
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
                hasPendingTree: RecordingStore.HasPendingTree,
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
                hasPendingTree: RecordingStore.HasPendingTree,
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
                    hasPendingTree: RecordingStore.HasPendingTree,
                    pendingTreeIsFinalized:
                        RecordingStore.PendingTreeStateValue == PendingTreeState.Finalized,
                    reFlyInPlaceContinuationActive:
                        ParsekScenario.IsReFlyInPlaceContinuationActive()))
            {
                ParsekLog.Info("Flight",
                    "OnFlightReady: scheduling Quickload restore for Re-Fly retry " +
                    $"(pending tree '{RecordingStore.PendingTree?.TreeName}' is Finalized; " +
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
            if (RecordingStore.ShouldRunPendingCleanupOnFlightReady())
            {
                // BUG-H: the live-vessel cleanup is launch-identity-aware (pid + Vessel.id Guid) and
                // scoped to the reverted flight (skips vessels in the revert-target launch quicksave).
                // The PendingCleanupPids/Names sets are only the "cleanup armed" trigger now — the
                // actual recover decision derives from the committed recordings + the pre-existing
                // whitelist so a real, separately-launched craft is never recovered by name.
                CleanupOrphanedSpawnedVessels(
                    RecordingStore.CollectAllCommittedRecordings(),
                    RecordingStore.PendingRevertPreExistingPids);
                RecordingStore.PendingCleanupPids = null;
                RecordingStore.PendingCleanupNames = null;
                RecordingStore.PendingRevertPreExistingPids = null;
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
            EvaluateAndApplyGhostChains();
            RestoreReFlyInvokeHoldsFromMarker(Planetarium.GetUniversalTime());
            RestoreRewindVesselHolds(Planetarium.GetUniversalTime());

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
        private void ClearSceneChangeTransientState()
        {
            DisarmPostSwitchAutoRecord("scene change transient reset");

            // Clear dock/undock pending state
            ClearDockUndockState();

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
        /// Flight-ready consume of the fresh recording OnLoad armed after declining a saved
        /// active tree this session discarded (QUICKLOAD-INTO-DISCARDED-FLIGHT-RECORDS-AGAIN).
        /// Starts through <see cref="StartRecording"/>, the auto-record entry, which builds a new
        /// tree with fresh ids because no tree is active here. Called on every OnFlightReady path
        /// so the request never outlives this scene.
        /// </summary>
        private void ConsumeFreshRecordingAfterDiscardedRestore(bool restoreScheduled)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            ParsekScenario.ConsumeFreshRecordingAfterDiscard(
                restoreScheduled,
                recorderLive: recorder != null && recorder.IsRecording,
                hasActiveTree: activeTree != null,
                hasActiveVessel: v != null && !GhostMapPresence.IsGhostMapVessel(v.persistentId),
                startRecording: () =>
                {
                    StartRecording(suppressStartScreenMessage: true);
                    if (IsRecording)
                        Log("Auto-record started (quickload into a discarded flight)");
                    return IsRecording;
                });
        }

        /// <summary>
        /// Resets all transient state on flight ready: background recorder, tree, chain,
        /// split detection, dock/undock, merge detection. Called at the start of OnFlightReady.
        /// </summary>
        private void ResetFlightReadyState()
        {
            ParsekLog.Info("Flight", "Resetting flight-ready state");
            RecorderStateLog.RecState("ResetFlightReady:entry", CaptureRecorderState());
            DisarmPostSwitchAutoRecord("flight ready reset");

            // Phase 6b: Clean up ghost chain state from previous flight scene
            ClearClaimedVesselHoldGhosts("flight-ready-reset");
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
            ClearDockUndockState();

            // Clear merge event detection state
            pendingTreeDockMerge = false;
            pendingBoardingTargetInTree = false;
            dockingInProgress.Clear();
            treeDestructionDialogPending = false;
        }

        /// <summary>
        /// Called when the merge dialog commits a tree. Re-evaluates ghost chains
        /// so newly committed recordings get ghost map ProtoVessels immediately.
        /// Route creation is no longer offered here: eligible Supply Runs surface
        /// as derived candidates in the Logistics window (see
        /// <see cref="Logistics.RouteCandidateFinder"/>) instead of a commit-time popup.
        /// </summary>
        private void OnTreeCommittedFromMergeDialog(RecordingTree tree)
        {
            ParsekLog.Info("GhostMap", "Tree committed from merge dialog - re-evaluating ghost chains");
            EvaluateAndApplyGhostChains();
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

        /// <summary>
        /// Safety net for vessel switches that change <see cref="FlightGlobals.ActiveVessel"/>
        /// without reaching either the physics-frame recorder path or onVesselChange.
        /// Replays the normal <see cref="OnVesselSwitchComplete"/> transition once the
        /// frame loop can prove Parsek's active recorder/tree state is out of sync.
        /// </summary>
        private void HandleMissedVesselSwitchRecovery()
        {
            if (committedSpawnedRestoreSuppressedPid != 0)
            {
                Vessel suppressionActiveVessel = FlightGlobals.ActiveVessel;
                TickCommittedSpawnedRestoreSuppression(
                    ref committedSpawnedRestoreSuppressedPid,
                    suppressionActiveVessel != null ? suppressionActiveVessel.persistentId : 0u,
                    hasActiveTree: activeTree != null,
                    hasRecorder: recorder != null);
            }

            if (ShouldAttemptCommittedSpawnedRestoreInUpdate(
                    activeTree != null,
                    recorder != null,
                    restoringActiveTree,
                    RecordingStore.HasPendingTree,
                    ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady,
                    Time.unscaledTime,
                    nextCommittedSpawnedRestoreRetryAt))
            {
                nextCommittedSpawnedRestoreRetryAt =
                    Time.unscaledTime + CommittedSpawnedRestoreRetryIntervalSeconds;
                if (TryRestoreCommittedTreeForSpawnedActiveVessel())
                {
                    nextCommittedSpawnedRestoreRetryAt = 0f;
                    return;
                }

                // A matched committed-tree restore may already have installed the tree,
                // detached it from committed storage, and put the returned vessel back in
                // BackgroundMap before the recorder attach failed. Falling through here is
                // intentional: the existing missed-switch recovery path can retry the final
                // promotion/resume from that live tree shape without rolling the tree back.
            }
            else if (activeTree != null || recorder != null)
            {
                // Only reset once a live tree/recorder takes over ownership. The other
                // transient blockers (restore coroutine, pending tree, restore mode) are
                // allowed to age out naturally against the 1s retry window.
                nextCommittedSpawnedRestoreRetryAt = 0f;
            }

            Vessel activeVessel = FlightGlobals.ActiveVessel;
            uint activeVesselPid = activeVessel != null ? activeVessel.persistentId : 0;
            bool hasRecorder = recorder != null;
            bool recorderIsRecording = hasRecorder && recorder.IsRecording;
            bool recorderChainToVesselPending = hasRecorder && recorder.ChainToVesselPending;
            uint recorderPid = hasRecorder ? recorder.RecordingVesselId : 0;
            bool activeVesselTrackedInBackground = activeTree != null
                && activeVesselPid != 0
                && activeTree.BackgroundMap.ContainsKey(activeVesselPid);
            bool activeVesselAlreadyArmedForPostSwitchAutoRecord =
                IsPostSwitchAutoRecordArmedForPid(activeVesselPid);

            if (!ShouldRecoverMissedVesselSwitch(
                    restoringActiveTree,
                    activeTree != null,
                    pendingTreeDockMerge,
                    pendingSplitRecorder != null,
                    pendingSplitInProgress,
                    hasRecorder,
                    recorderIsRecording,
                    recorderChainToVesselPending,
                    recorderPid,
                    activeVesselPid,
                    activeVesselTrackedInBackground,
                    activeVesselAlreadyArmedForPostSwitchAutoRecord))
            {
                return;
            }

            if (GhostMapPresence.IsGhostMapVessel(activeVesselPid)) return;

            var recoveryDiagnosticContext = new MissedVesselSwitchRecoveryDiagnosticContext
            {
                IsRecovery = true,
                ActiveVesselPid = activeVesselPid,
                RecorderVesselPid = recorderPid,
                HasRecorder = hasRecorder,
                RecorderIsRecording = recorderIsRecording,
                RecorderIsBackgrounded = hasRecorder && recorder.IsBackgrounded,
                RecorderChainToVesselPending = recorderChainToVesselPending,
                ActiveVesselTrackedInBackground = activeVesselTrackedInBackground,
                ActiveVesselAlreadyArmedForPostSwitchAutoRecord =
                    activeVesselAlreadyArmedForPostSwitchAutoRecord,
                ActiveTreeRecordingId = activeTree != null ? activeTree.ActiveRecordingId : null,
                ActiveTreeBackgroundMapCount = activeTree != null && activeTree.BackgroundMap != null
                    ? activeTree.BackgroundMap.Count
                    : 0
            };

            // Update() runs every frame; if the recovery handler does not clear
            // the predicate immediately (e.g. background recorder still pending),
            // this branch fires repeatedly for the same vessel. Rate-limit by
            // activePid so each vessel logs at most once per window — visibility
            // is preserved (still a WARN), spam is bounded. The recovery context
            // applies the same cadence to the nested RecState entry/post snapshots
            // without affecting real onVesselChange boundaries.
            ParsekLog.WarnRateLimited("Flight",
                $"missed-vessel-switch-{activeVesselPid}",
                $"Update: recovering missed vessel switch for active vessel '{activeVessel.vesselName}' " +
                $"(activePid={activeVesselPid}, recorderPid={recorderPid}, " +
                $"trackedInBackground={activeVesselTrackedInBackground})");
            ReplayVesselSwitchCompleteForMissedSwitchRecovery(
                activeVessel,
                recoveryDiagnosticContext);
        }

        /// <summary>
        /// Zeros all dock pending-transition fields.
        /// Called from scene change and flight ready.
        /// </summary>
        private void ClearDockUndockState()
        {
            pendingDockMergedPid = 0;
            dockConfirmFrames = 0;
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
        }

        /// <summary>
        /// Handles tree dock merge: creates merge branch point when recorder is stopped
        /// and dock merge is pending. Consumes pending dock state.
        /// </summary>
        private void HandleTreeDockMerge()
        {
            if (activeTree == null || !pendingTreeDockMerge || recorder == null
                || recorder.IsRecording || recorder.CaptureAtStop == null)
                return;

            double mergeUT = Planetarium.GetUniversalTime();
            if (recorder.CaptureAtStop.Points.Count > 0)
                mergeUT = recorder.CaptureAtStop.Points[recorder.CaptureAtStop.Points.Count - 1].ut;

            // Determine parent recording IDs
            string activeParentId = activeTree.ActiveRecordingId;
            string bgParentId = null;
            // The background partner is whichever vessel (absorbed OR merged) is in BackgroundMap.
            // When we're the TARGET: absorbed vessel is in BackgroundMap.
            // When we're the INITIATOR: merged (surviving) vessel is in BackgroundMap.
            if (pendingDockAbsorbedPid != 0)
                activeTree.BackgroundMap.TryGetValue(pendingDockAbsorbedPid, out bgParentId);
            if (bgParentId == null && pendingDockMergedPid != 0)
                activeTree.BackgroundMap.TryGetValue(pendingDockMergedPid, out bgParentId);

            var stoppedRecorder = recorder;
            recorder = null;
            // Route target was already resolved from the couple event in OnPartCouple
            // and persisted on pendingDockRouteTargetPid. Falling back to the legacy
            // BackgroundMap-derived ResolveDockRouteTargetPid would drop the partner
            // for cross-tree dock partners (committed-recording case). The ungated
            // partner stamp rides alongside and feeds ONLY the branch point.
            uint routeTargetPid = pendingDockRouteTargetPid;
            uint branchPartnerPid = pendingDockPartnerPid;
            string branchPartnerGuid = pendingDockPartnerGuid;

            CreateMergeBranch(
                BranchPointType.Dock,
                pendingDockMergedPid,
                activeParentId,
                bgParentId,
                mergeUT,
                stoppedRecorder,
                routeTargetPid,
                pendingDockTransferKind,
                branchPartnerPid,
                branchPartnerGuid);

            // Clean up
            if (pendingDockAbsorbedPid != 0)
                dockingInProgress.Remove(pendingDockAbsorbedPid);

            pendingTreeDockMerge = false;
            pendingDockMergedPid = 0;
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

            Log("Tree dock merge completed");
        }

        /// <summary>
        /// Handles tree board merge: creates merge branch point when boarding is detected
        /// in tree mode and the recorder is stopped with ChainToVesselPending.
        /// </summary>
        private void HandleTreeBoardMerge()
        {
            if (activeTree == null || recorder == null || !recorder.ChainToVesselPending
                || recorder.IsRecording || recorder.CaptureAtStop == null
                || pendingBoardingTargetPid == 0
                || FlightGlobals.ActiveVessel == null
                || FlightGlobals.ActiveVessel.persistentId != pendingBoardingTargetPid)
                return;

            double mergeUT = Planetarium.GetUniversalTime();
            if (recorder.CaptureAtStop.Points.Count > 0)
                mergeUT = recorder.CaptureAtStop.Points[recorder.CaptureAtStop.Points.Count - 1].ut;

            uint mergedPid = pendingBoardingTargetPid;

            string activeParentId = activeTree.ActiveRecordingId;
            string bgParentId = null;
            if (pendingBoardingTargetInTree)
                activeTree.BackgroundMap.TryGetValue(mergedPid, out bgParentId);

            string boardPartnerGuid;
            uint boardPartnerPid = ResolveBoardPartnerStamp(
                pendingBoardingTargetInTree, mergedPid,
                AnchorDetector.TryReadLiveVesselGuid(FlightGlobals.ActiveVessel),
                out boardPartnerGuid);
            ParsekLog.Verbose("Flight",
                $"Board merge partner stamp: targetPid={mergedPid} inTree={pendingBoardingTargetInTree} " +
                $"partnerPidStamped={boardPartnerPid} partnerGuidStamped={boardPartnerGuid ?? "(none)"}");

            // Fix 5: clear ChainToVesselPending before CreateMergeBranch
            recorder.ChainToVesselPending = false;
            var stoppedRecorder = recorder;
            recorder = null;

            CreateMergeBranch(
                BranchPointType.Board,
                mergedPid,
                activeParentId,
                bgParentId,
                mergeUT,
                stoppedRecorder,
                branchPartnerPid: boardPartnerPid,
                branchPartnerGuid: boardPartnerGuid);

            pendingBoardingTargetPid = 0;
            boardingConfirmFrames = 0;
            pendingBoardingTargetInTree = false;

            Log("Tree board merge completed");
        }

        /// <summary>
        /// An EVA recording stopped on boarding (ChainToVesselPending) that
        /// <see cref="HandleTreeBoardMerge"/> did not consume, because the boarding
        /// confirmation is missing or names another vessel: treat it as a normal stop and
        /// leave CaptureAtStop for the ordinary revert/merge handling.
        /// </summary>
        private void HandleUnconfirmedBoardingStop()
        {
            if (recorder == null || !recorder.ChainToVesselPending
                || recorder.IsRecording || recorder.CaptureAtStop == null)
                return;

            recorder.ChainToVesselPending = false;
            pendingBoardingTargetPid = 0;
            boardingConfirmFrames = 0;
            Log("ChainToVessel without boarding confirmation \u2014 treating as normal stop");
            ParsekLog.ScreenMessage("Recording stopped - vessel changed", 3f);
        }

        /// <summary>
        /// Returns true if the current recording mode suppresses environment boundary
        /// splits. Tree mode does, and every recording runs in a tree: the recorder keeps
        /// one recording across the boundary and the optimizer splits it afterwards.
        /// </summary>
        internal static bool ShouldSuppressBoundarySplit(RecordingTree activeTree)
        {
            return activeTree != null;
        }

        /// <summary>
        /// Handles atmosphere boundary auto-split when crossing the atmosphere edge.
        /// In tree mode the recording continues across the edge and only the flags clear.
        /// </summary>
        private void HandleAtmosphereBoundarySplit()
        {
            if (recorder == null || !recorder.IsRecording || !recorder.AtmosphereBoundaryCrossed)
                return;

            // Bug #87: In tree mode, boundary splits must not commit standalone chain segments.
            // The recorder keeps accumulating data into the tree recording — just clear the
            // boundary flags so they don't re-trigger.
            if (ShouldSuppressBoundarySplit(activeTree))
            {
                string skipPhase = recorder.EnteredAtmosphere ? "atmo" : "exo";
                string skipBody = FlightGlobals.ActiveVessel?.mainBody?.name ?? "Unknown";
                ParsekLog.Info("Flight", $"Atmosphere boundary suppressed in tree mode: " +
                    $"{skipBody} entering {skipPhase} (tree={activeTree.Id}, " +
                    $"activeRec={activeTree.ActiveRecordingId})");
                recorder.ClearBoundaryFlags();
                return;
            }

            ParsekLog.Warn("Flight", "Atmosphere boundary crossed with no active tree - clearing boundary flags");
            recorder.ClearBoundaryFlags();
        }

        /// <summary>
        /// Handles SOI change auto-split when transitioning between celestial bodies.
        /// In tree mode the recording continues across the SOI change and only the flags clear.
        /// </summary>
        private void HandleSoiChangeSplit()
        {
            if (recorder == null || !recorder.IsRecording || !recorder.SoiChangePending)
                return;

            // Bug #87: In tree mode, boundary splits must not commit standalone chain segments.
            // The recorder keeps accumulating data into the tree recording — just clear the
            // boundary flags so they don't re-trigger.
            if (ShouldSuppressBoundarySplit(activeTree))
            {
                string skipFrom = recorder.SoiChangeFromBody ?? "Unknown";
                string skipTo = FlightGlobals.ActiveVessel?.mainBody?.name ?? "Unknown";
                ParsekLog.Info("Flight", $"SOI change boundary suppressed in tree mode: " +
                    $"{skipFrom} to {skipTo} (tree={activeTree.Id}, " +
                    $"activeRec={activeTree.ActiveRecordingId})");
                recorder.ClearBoundaryFlags();
                return;
            }

            ParsekLog.Warn("Flight", "SOI change with no active tree - clearing boundary flags");
            recorder.ClearBoundaryFlags();
        }

        /// <summary>
        /// Handles altitude boundary auto-split when crossing the approach altitude threshold
        /// on an airless body. Mirrors HandleAtmosphereBoundarySplit.
        /// </summary>
        private void HandleAltitudeBoundarySplit()
        {
            if (recorder == null || !recorder.IsRecording || !recorder.AltitudeBoundaryCrossed)
                return;

            // Bug #87: In tree mode, boundary splits must not commit standalone chain segments.
            if (ShouldSuppressBoundarySplit(activeTree))
            {
                string skipPhase = recorder.DescendedBelowThreshold ? "approach" : "exo";
                string skipBody = FlightGlobals.ActiveVessel?.mainBody?.name ?? "Unknown";
                ParsekLog.Info("Flight", $"Altitude boundary suppressed in tree mode: " +
                    $"{skipBody} entering {skipPhase} (tree={activeTree.Id}, " +
                    $"activeRec={activeTree.ActiveRecordingId})");
                recorder.ClearBoundaryFlags();
                return;
            }

            ParsekLog.Warn("Flight", "Altitude boundary crossed with no active tree - clearing boundary flags");
            recorder.ClearBoundaryFlags();
        }

        /// <summary>
        /// Flushes the backgrounded recorder to its tree recording and updates BackgroundMap.
        /// Called from Update when TransitionToBackgroundPending and IsBackgrounded are both true.
        /// </summary>
        private void HandleTreeBackgroundFlush()
        {
            if (activeTree == null || recorder == null || !recorder.TransitionToBackgroundPending
                || !recorder.IsBackgrounded)
                return;

            EmitPendingSplitBeforeFocusLeaves("HandleTreeBackgroundFlush");
            FlushRecorderToTreeRecording(recorder, activeTree);

            // Add old vessel to BackgroundMap
            string oldRecId = activeTree.ActiveRecordingId;
            Recording oldRec = null;
            uint bgPidUpdate = 0;
            if (oldRecId != null && activeTree.Recordings.TryGetValue(oldRecId, out oldRec)
                && oldRec.VesselPersistentId != 0)
            {
                activeTree.BackgroundMap[oldRec.VesselPersistentId] = oldRecId;
                bgPidUpdate = oldRec.VesselPersistentId;
            }
            activeTree.ActiveRecordingId = null;
            RecordingFinalizationCache bgCacheUpdate = oldRec != null
                ? recorder.GetFinalizationCacheForRecording(oldRec)
                : recorder.FinalizationCache;
            recorder = null;
            if (bgPidUpdate != 0) backgroundRecorder?.OnVesselBackgrounded(
                bgPidUpdate,
                inheritedFinalizationCache: bgCacheUpdate);
            Log($"Tree: recorder flushed to background (recId={oldRecId})");
        }

        /// <summary>
        /// Handles deferred split checks: stops the recorder and starts a deferred vessel-creation
        /// check when FlightRecorder signals a joint break or a decouple-created vessel indicates
        /// a split without a matching joint-break callback.
        /// </summary>
        internal static DeferredSplitCheckTrigger ResolveDeferredSplitCheckTrigger(
            bool pendingSplitInProgress,
            bool recorderIsRecording,
            bool jointBreakPending,
            bool decoupleCreatedVesselsPending)
        {
            if (pendingSplitInProgress || !recorderIsRecording)
                return DeferredSplitCheckTrigger.None;

            if (jointBreakPending)
                return DeferredSplitCheckTrigger.JointBreak;

            if (decoupleCreatedVesselsPending)
                return DeferredSplitCheckTrigger.DecoupleCreatedVessel;

            return DeferredSplitCheckTrigger.None;
        }

        internal static bool ShouldCaptureDecoupleCreatedVessel(
            uint recordedVesselPid,
            uint originalVesselPid)
        {
            return recordedVesselPid != 0 && originalVesselPid == recordedVesselPid;
        }

        internal delegate bool TryResolveTrajectoryPoint(uint persistentId, out TrajectoryPoint point);

        internal static bool TrySelectDecouplePartOriginSeed(
            uint rootPartPersistentId,
            TryResolveTrajectoryPoint resolvePartOriginSeed,
            out TrajectoryPoint seed)
        {
            seed = default;
            if (rootPartPersistentId == 0u || resolvePartOriginSeed == null)
                return false;

            return resolvePartOriginSeed(rootPartPersistentId, out seed);
        }

        internal static bool ShouldUseUndockPartOriginSeed(
            uint undockedPartPersistentId,
            uint rootPartPersistentId)
        {
            return undockedPartPersistentId != 0u
                && rootPartPersistentId != 0u
                && undockedPartPersistentId == rootPartPersistentId;
        }

        private static string DescribeDecouplePartForDiagnostics(string label, Part part)
        {
            if (part == null)
                return label + "=null";

            string name = part.partInfo?.name ?? part.name ?? "unknown";
            uint parentPid = part.parent?.persistentId ?? 0u;
            string origin = part.transform != null
                ? DiagnosticFormatters.FormatVector3d(part.transform.position)
                : "no-transform";
            string rotation = part.transform != null
                ? DiagnosticFormatters.FormatQuaternion(part.transform.rotation)
                : "no-transform";
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}='{1}' pid={2} parentPid={3} origin={4} rot={5} srfAttach={6}",
                label,
                name,
                part.persistentId,
                parentPid,
                origin,
                rotation,
                DiagnosticFormatters.DescribeSurfaceAttachNode(part));
        }

        private static Part FindFirstDirectChildPart(Part rootPart, IList<Part> parts)
        {
            if (rootPart == null || parts == null)
                return null;

            for (int i = 0; i < parts.Count; i++)
            {
                Part part = parts[i];
                if (part != null && part.parent == rootPart)
                    return part;
            }

            return null;
        }

        internal static double ResolveDeferredSplitBranchUT(
            double fallbackUT,
            double exactTriggerUT,
            IEnumerable<uint> newVesselPids,
            IReadOnlyDictionary<uint, TrajectoryPoint> capturedTrajectoryPoints)
        {
            double branchUT = !double.IsNaN(exactTriggerUT) ? exactTriggerUT : fallbackUT;
            if (newVesselPids == null || capturedTrajectoryPoints == null)
                return branchUT;

            foreach (uint pid in newVesselPids)
            {
                TrajectoryPoint point;
                if (!capturedTrajectoryPoints.TryGetValue(pid, out point) || double.IsNaN(point.ut))
                    continue;

                if (double.IsNaN(branchUT) || point.ut < branchUT)
                    branchUT = point.ut;
            }

            return double.IsNaN(branchUT) ? fallbackUT : branchUT;
        }

        private void HandleJointBreakDeferredCheck()
        {
            if (pendingSplitInProgress || recorder == null || !recorder.IsRecording)
                return;

            double pendingJointBreakUT;
            bool jointBreakPending = recorder.ConsumePendingJointBreakCheck(out pendingJointBreakUT);
            bool decoupleCreatedVesselsPending =
                pendingDeferredSplitCheckTrigger == DeferredSplitCheckTrigger.DecoupleCreatedVessel
                && decoupleCreatedVessels != null
                && decoupleCreatedVessels.Count > 0;

            var trigger = ResolveDeferredSplitCheckTrigger(
                pendingSplitInProgress,
                recorderIsRecording: true,
                jointBreakPending,
                decoupleCreatedVesselsPending);

            if (trigger == DeferredSplitCheckTrigger.None)
                return;

            pendingDeferredSplitCheckTrigger = trigger;
            pendingSplitTriggerUT = jointBreakPending ? pendingJointBreakUT : double.NaN;

            // Snapshot existing vessel PIDs so the deferred check can identify NEW vessels.
            // This only makes sense for true joint-break-triggered checks.
            preBreakVesselPids = null;
            if (trigger == DeferredSplitCheckTrigger.JointBreak && FlightGlobals.Vessels != null)
            {
                preBreakVesselPids = new HashSet<uint>();
                for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
                {
                    Vessel v = FlightGlobals.Vessels[i];
                    if (v != null) preBreakVesselPids.Add(v.persistentId);
                }
            }
            recorder.StopRecordingForChainBoundary();
            pendingSplitRecorder = recorder;
            recorder = null;
            pendingSplitInProgress = true;

            if (trigger == DeferredSplitCheckTrigger.JointBreak)
                Log("Joint break detected \u2014 starting deferred vessel split check");
            else
                Log("Decouple-created vessel detected without joint-break callback \u2014 starting deferred vessel split check");
            StartCoroutine(DeferredJointBreakCheck());
        }

        /// <summary>
        /// Callback for GameEvents.onVesselCreate during the split check window.
        /// Captures vessels immediately at creation, before KSP's debris cleanup can destroy them.
        /// </summary>
        /// <summary>
        /// Callback for GameEvents.onPartDeCoupleNewVesselComplete during active recording.
        /// Fires synchronously inside Part.decouple(), before KSP's debris cleanup can destroy the vessel.
        /// If no structural onPartJointBreak callback reaches the recorder for this split,
        /// arm a decouple-only deferred split check so these captured vessels are processed
        /// promptly instead of lingering until a later unrelated joint break.
        /// </summary>
        private void OnDecoupleNewVesselDuringSplitCheck(Vessel originalVessel, Vessel newVessel)
        {
            if (newVessel == null) return;
            uint recordedVesselPid = recorder?.RecordingVesselId
                ?? pendingSplitRecorder?.RecordingVesselId
                ?? 0;
            uint originalVesselPid = originalVessel?.persistentId ?? 0;
            if (originalVesselPid != 0u)
                decoupleOriginByNewPid[newVessel.persistentId] = originalVesselPid;
            if (!ShouldCaptureDecoupleCreatedVessel(recordedVesselPid, originalVesselPid))
            {
                ParsekLog.VerboseRateLimited("Flight",
                    $"decouple-created-foreign-{originalVesselPid}-{newVessel.persistentId}",
                    $"Ignoring decouple-created vessel pid={newVessel.persistentId}: " +
                    $"originalPid={originalVesselPid}, recordedPid={recordedVesselPid}");
                return;
            }
            // Only capture vessels that didn't exist before the break
            if (preBreakVesselPids != null && preBreakVesselPids.Contains(newVessel.persistentId))
                return;
            decoupleCreatedVessels?.Add(newVessel);
            if (!pendingSplitInProgress && preBreakVesselPids == null)
                pendingDeferredSplitCheckTrigger = DeferredSplitCheckTrigger.DecoupleCreatedVessel;
            // Capture controller status NOW — vessel parts may be cleared by the deferred check.
            // A crewed vessel with no command module counts: it records as its own branch.
            bool hasController = ShouldRecordAsOwnBranch(newVessel);
            if (decoupleControllerStatus != null)
                decoupleControllerStatus[newVessel.persistentId] = hasController;
            double splitUT = Planetarium.GetUniversalTime();
            uint rootPartPersistentId = newVessel.rootPart?.persistentId ?? 0u;
            string seedSource = "none";
            string pendingJointChildSeedPids = "none";
            bool rootMatchesPendingJointChildSeed = false;
            if (decoupleCreatedTrajectoryPoints != null
                && !decoupleCreatedTrajectoryPoints.ContainsKey(newVessel.persistentId))
            {
                FlightRecorder seedRecorder = recorder ?? pendingSplitRecorder;
                TryResolveTrajectoryPoint resolvePartOriginSeed = null;
                if (seedRecorder != null)
                {
                    rootMatchesPendingJointChildSeed =
                        seedRecorder.ContainsPendingJointChildPartOriginSeed(rootPartPersistentId);
                    resolvePartOriginSeed = seedRecorder.TryConsumePendingJointChildPartOriginSeed;
                    pendingJointChildSeedPids =
                        seedRecorder.DescribePendingJointChildPartOriginSeedIdsForDiagnostics();
                }
                // On stock decoupler splits, KSP roots the new debris vessel at
                // joint.Child; if it chooses a different root, this misses cleanly and
                // falls back to the post-split root-part sample below.
                if (TrySelectDecouplePartOriginSeed(
                        rootPartPersistentId,
                        resolvePartOriginSeed,
                        out TrajectoryPoint partOriginSeed))
                {
                    decoupleCreatedTrajectoryPoints[newVessel.persistentId] = partOriginSeed;
                    seedSource = "joint-child-part-origin";
                }
                else
                {
                    decoupleCreatedTrajectoryPoints[newVessel.persistentId] =
                        BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                            newVessel, splitUT, preferRootPartSurfacePose: true);
                    seedSource = "new-vessel-root-part";
                }
                // ---- Trace-Sep: log seed source + new-vessel state at split ----
                if (TraceSeparation.RecordingWindowActive)
                {
                    Vessel original = originalVessel;
                    CelestialBody body = newVessel.mainBody ?? original?.mainBody;
                    Vector3d newLlaWorld = body != null
                        ? body.GetWorldSurfacePosition(newVessel.latitude, newVessel.longitude, newVessel.altitude)
                        : Vector3d.zero;
                    Vector3d newTransformPos = newVessel.transform != null
                        ? (Vector3d)newVessel.transform.position : Vector3d.zero;
                    Vector3d originalLlaWorld = body != null && original != null
                        ? body.GetWorldSurfacePosition(original.latitude, original.longitude, original.altitude)
                        : Vector3d.zero;
                    Vector3 newSrfVel = newVessel.srf_velocity;
                    Vector3 originalSrfVel = original != null ? original.srf_velocity : Vector3.zero;
                    float dt = Time.fixedDeltaTime;
                    Vector3d newWorldDelta = newTransformPos - newLlaWorld;
                    TraceSeparation.RecordLog("DecoupleSeed",
                        "newPid=" + newVessel.persistentId +
                        " name='" + newVessel.vesselName + "'" +
                        " seedSource=" + seedSource +
                        " splitUT=" + splitUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        " newLLA=(" + newVessel.latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        "," + newVessel.longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        "," + newVessel.altitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")" +
                        " newLLAWorld=" + TraceSeparation.FormatVector3d(newLlaWorld) +
                        " newTransformPos=" + TraceSeparation.FormatVector3d(newTransformPos) +
                        " newTransformVsLLA=" + TraceSeparation.FormatVector3d(newWorldDelta) +
                        " |newDelta|=" + newWorldDelta.magnitude.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                        " originalLLAWorld=" + TraceSeparation.FormatVector3d(originalLlaWorld) +
                        " newVsOriginalLLA=" + TraceSeparation.FormatVector3d(newLlaWorld - originalLlaWorld) +
                        " newSrfVel=" + TraceSeparation.FormatVector3(newSrfVel) + " |vs|=" + newSrfVel.magnitude.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                        " predictedNewSrfStep=" + (newSrfVel.magnitude * dt).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                        " originalSrfVel=" + TraceSeparation.FormatVector3(originalSrfVel) + " |ovs|=" + originalSrfVel.magnitude.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                }
                // ---- /Trace-Sep ----
            }
            else if (decoupleCreatedTrajectoryPoints != null
                     && decoupleCreatedTrajectoryPoints.ContainsKey(newVessel.persistentId))
            {
                seedSource = "already-captured";
            }
            ParsekLog.Info("Flight",
                $"Decouple created vessel during recording: pid={newVessel.persistentId} " +
                $"name='{newVessel.vesselName}' type={newVessel.vesselType} " +
                $"parts={newVessel.parts?.Count ?? 0} hasController={hasController} " +
                $"rootPartPid={rootPartPersistentId} seedSource={seedSource} " +
                $"pendingJointChildSeedPids={pendingJointChildSeedPids} " +
                $"splitUT={splitUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"capturedSeed={(decoupleCreatedTrajectoryPoints != null && decoupleCreatedTrajectoryPoints.ContainsKey(newVessel.persistentId) ? "T" : "F")}");
            Part rootPart = newVessel.rootPart;
            ParsekLog.Verbose("Flight",
                $"Decouple created vessel diagnostics: pid={newVessel.persistentId} " +
                $"rootMatchesPendingJointChildSeed={(rootMatchesPendingJointChildSeed ? "T" : "F")} " +
                $"{DescribeDecouplePartForDiagnostics("root", rootPart)} " +
                $"{DescribeDecouplePartForDiagnostics("firstChild", FindFirstDirectChildPart(rootPart, newVessel.parts))}");
        }

        /// <summary>
        /// Handles deferred auto-record for EVA: starts recording when vessel switch
        /// to the EVA kerbal is complete.
        /// </summary>
        private void HandleDeferredAutoRecordEva()
        {
            bool hasActiveVessel = FlightGlobals.ActiveVessel != null;
            if (!ShouldStartDeferredAutoRecordEva(
                pendingAutoRecord,
                IsRecording,
                hasActiveVessel,
                activeVesselIsEva: hasActiveVessel && FlightGlobals.ActiveVessel.isEVA))
                return;

            uint activePid = FlightGlobals.ActiveVessel?.persistentId ?? 0u;
            string invalidHeadReason;
            if (!CanStartRecorderWithActiveTreeHead(activeTree, activePid, out invalidHeadReason))
            {
                ParsekLog.WarnRateLimited(
                    "Flight",
                    BuildInvalidActiveTreeHeadRateLimitKey(
                        "deferred-eva",
                        activeTree,
                        activePid,
                        invalidHeadReason),
                    $"HandleDeferredAutoRecordEva: clearing pending auto-record because active tree head " +
                    $"cannot accept a recorder (reason={invalidHeadReason}, activePid={activePid}, " +
                    $"tree={activeTree?.Id ?? "null"}, activeRec={activeTree?.ActiveRecordingId ?? "null"})");
                ClearPendingEvaAutoRecordState();
                ScreenMessage("Recording blocked (invalid tree head)", 2f);
                return;
            }

            StartRecording(suppressStartScreenMessage: true);

            if (IsRecording)
            {
                pendingAutoRecord = false;
                Log("Auto-record started (EVA from pad)");
                ScreenMessage("Recording STARTED (auto - EVA from pad)", 2f);
            }
            // else: StartRecording failed (paused game or no active vessel).
            // Keep pending flags so we retry next frame; fall through to
            // HandleInput/playback so they aren't starved.
        }

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

        // Pass suppressStartScreenMessage=true for fresh starts where the caller posts
        // its own custom screen message.
        public void StartRecording(bool suppressStartScreenMessage = false,
            bool fromPrelaunchTransition = false)
        {
            // Commit orphaned CaptureAtStop from a previous recorder that was stopped
            // by vessel switch but never committed (e.g., auto-record started on new
            // vessel before scene change). Without this, the old recording data is lost.
            // A capture of a tree the scene already dropped is discarded, not committed
            // (TryDiscardCaptureOfDroppedTree).
            if (recorder != null && !recorder.IsRecording && recorder.CaptureAtStop != null
                && activeTree == null)
            {
                FallbackCommitSplitRecorder(recorder);
                ParsekLog.Info("Flight", "Resolved orphaned recorder capture before starting new one");
            }

            uint activePid = FlightGlobals.ActiveVessel?.persistentId ?? 0u;
            string invalidHeadReason;
            if (!CanStartRecorderWithActiveTreeHead(activeTree, activePid, out invalidHeadReason))
            {
                ParsekLog.WarnRateLimited(
                    "Flight",
                    BuildInvalidActiveTreeHeadRateLimitKey(
                        "start-recording",
                        activeTree,
                        activePid,
                        invalidHeadReason),
                    $"StartRecording: refused to bind recorder to invalid active tree head " +
                    $"(reason={invalidHeadReason}, activePid={activePid}, " +
                    $"tree={activeTree?.Id ?? "null"}, activeRec={activeTree?.ActiveRecordingId ?? "null"})");
                return;
            }

            recorder = new FlightRecorder();

            // Bug #271: always-tree mode. When no tree exists, wrap the new recording
            // in a single-node tree. This eliminates the standalone/tree dual-path
            // architecture — every recording is a tree recording, even single-vessel
            // flights.
            if (activeTree == null)
            {
                string treeId = Guid.NewGuid().ToString("N");
                string rootRecId = Guid.NewGuid().ToString("N");
                uint vesselPid = FlightGlobals.ActiveVessel?.persistentId ?? 0;

                activeTree = new RecordingTree
                {
                    Id = treeId,
                    TreeName = Recording.ResolveLocalizedName(
                        FlightGlobals.ActiveVessel?.vesselName) ?? "Unknown",
                    RootRecordingId = rootRecId,
                    ActiveRecordingId = rootRecId
                };

                // Phase F: PreTree* capture removed. The ledger is now the single
                // source of truth for funds/science/reputation; trees no longer
                // snapshot pre-launch career resources for lump-sum reconciliation.

                var rootRec = new Recording
                {
                    RecordingId = rootRecId,
                    TreeId = treeId,
                    VesselPersistentId = vesselPid,
                    VesselName = activeTree.TreeName,
                    RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                    // Pin start-of-recording controller identity directly on the
                    // always-tree root. Without this, a switch-away that backgrounds
                    // the root before recorder.StartRecording's tree-forward backstop
                    // runs would leave Controllers=null on the BG-tracked record and
                    // the identity-loss override at OnBackgroundVesselGoOnRails would
                    // never fire.
                    Controllers = ControllerInfo.CaptureFromVessel(FlightGlobals.ActiveVessel)
                };
                // Capture GhostVisualSnapshot at recording start — the FULL vessel
                // before any staging or breakup events. This is the snapshot used for
                // ghost mesh rendering. VesselSnapshot is captured later at stop/split
                // time and may reflect a reduced part count.
                if (FlightGlobals.ActiveVessel != null)
                {
                    var startSnap = FlightRecorder.TryBackupSnapshot(FlightGlobals.ActiveVessel);
                    if (startSnap != null)
                        rootRec.GhostVisualSnapshot = startSnap;
                }

                activeTree.AddOrReplaceRecording(rootRec);

                backgroundRecorder = new BackgroundRecorder(activeTree);
                backgroundRecorder.SubscribePartEvents();
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = backgroundRecorder;

                ParsekLog.Info("Flight",
                    $"Always-tree: created single-node tree id={treeId}, root={rootRecId}, " +
                    $"vessel={activeTree.TreeName} (pid={vesselPid}), " +
                    $"ghostSnap={rootRec.GhostVisualSnapshot != null}");
            }

            // Propagate tree mode to new recorder so DecideOnVesselSwitch uses tree decisions
            if (activeTree != null)
                recorder.ActiveTree = activeTree;
            recorder.StartedFromPrelaunchTransition = fromPrelaunchTransition;
            recorder.StartRecording(
                isPromotion: false,
                suppressStartScreenMessage: suppressStartScreenMessage);
            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight", $"StartRecording blocked: {DetermineRecordingBlockReason()}");
                // Bug #271: clean up single-node tree created above if StartRecording failed
                if (activeTree != null && activeTree.Recordings.Count <= 1
                    && backgroundRecorder != null)
                {
                    backgroundRecorder.Shutdown();
                    Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                    backgroundRecorder = null;
                    // The failed start drops this tree (the single-node tree created
                    // above, or a pre-existing one-recording tree) without stashing it as
                    // pending, so its armed committed-tree restore attempt must be
                    // cleared with it; the clear is id-matched and reads the id here,
                    // before the null.
                    CommittedTreeRestoreAttempt.ClearCommittedTreeRestoreAttemptForDroppedTree(
                        activeTree?.Id, "start-recording cleanup dropped the restore clone",
                        preserveIrreversibleTails: true);
                    activeTree = null;
                    ParsekLog.Info("Flight", "Cleaned up single-node tree after StartRecording failure");
                }
                return;
            }

            PrepareSessionStateForRecorderStart("StartRecording");

            uint pid = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.persistentId : 0;
            ParsekLog.Info("Flight", $"StartRecording succeeded: pid={pid}, tree={activeTree != null}");
            RecorderStateLog.RecState("StartRecording:post", CaptureRecorderState());
        }

        private void PrepareSessionStateForRecorderStart(string reason)
        {
            DisarmPostSwitchAutoRecord(reason);

            // BUG #2: every recorder start resets the live-resume anchor to NaN; only
            // the no-session resume / promote paths re-arm it (after this call returns)
            // with the current UT. A fresh launch / split / switch-segment start leaves
            // it NaN, so the pre-switch dialog falls back to tree-wide (fresh launch) or
            // the SwitchSegmentSession-armed path (Case A) takes precedence. Resetting
            // here — rather than at every recorder/activeTree teardown site — guarantees
            // a prior craft's resume UT cannot leak into a later unrelated tree's dialog.
            if (!double.IsNaN(liveResumeSessionStartUT))
            {
                ParsekLog.Verbose("Flight",
                    $"liveResumeSessionStartUT cleared reason={reason} " +
                    $"prevUt={liveResumeSessionStartUT.ToString("R", CultureInfo.InvariantCulture)}");
            }
            liveResumeSessionStartUT = double.NaN;

            // Reset crash coalescer on every recorder start so a fresh recorder cannot
            // inherit a stale coalescing window from the previous segment or scene.
            if (crashCoalescer != null)
            {
                crashCoalescer.Reset();
                ParsekLog.Verbose("Coalescer", "Coalescer reset on recording start");
            }

            // A newly-started recorder owns split detection from a clean slate.
            pendingDeferredSplitCheckTrigger = DeferredSplitCheckTrigger.None;
            pendingSplitTriggerUT = double.NaN;
            preBreakVesselPids = null;

            bool subscribedDecoupleListener = PrepareRecorderStartCollections(
                ref decoupleCreatedVessels,
                ref decoupleControllerStatus,
                ref decoupleCreatedTrajectoryPoints);
            if (subscribedDecoupleListener)
                GameEvents.onPartDeCoupleNewVesselComplete.Add(OnDecoupleNewVesselDuringSplitCheck);

            ParsekLog.Verbose("Flight",
                $"PrepareSessionStateForRecorderStart: reason={reason}, " +
                $"decoupleListener={(subscribedDecoupleListener ? "subscribed" : "reused")}");
        }

        internal static bool PrepareRecorderStartCollections<TCapturedVessel, TTrajectorySeed>(
            ref List<TCapturedVessel> createdVessels,
            ref Dictionary<uint, bool> controllerStatus,
            ref Dictionary<uint, TTrajectorySeed> trajectorySeeds)
        {
            bool shouldSubscribeDecoupleListener = false;

            if (createdVessels == null)
            {
                createdVessels = new List<TCapturedVessel>();
                shouldSubscribeDecoupleListener = true;
            }
            else
            {
                createdVessels.Clear();
            }

            if (controllerStatus == null)
                controllerStatus = new Dictionary<uint, bool>();
            else
                controllerStatus.Clear();

            if (trajectorySeeds == null)
                trajectorySeeds = new Dictionary<uint, TTrajectorySeed>();
            else
                trajectorySeeds.Clear();

            return shouldSubscribeDecoupleListener;
        }

        public void StopRecording()
        {
            ParsekLog.Info("Flight", "StopRecording called");
            // Unsubscribe decouple listener
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
            recorder?.StopRecording();

            // Tag final segment phase if untagged
            if (recorder?.CaptureAtStop != null
                && string.IsNullOrEmpty(recorder.CaptureAtStop.SegmentPhase)
                && SegmentPhaseClassifier.TryClassify(
                    FlightGlobals.ActiveVessel,
                    out string phase,
                    out string bodyName))
            {
                recorder.CaptureAtStop.SegmentBodyName = bodyName;
                recorder.CaptureAtStop.SegmentPhase = phase;
                ParsekLog.Verbose("Flight", $"Final segment tagged (StopRecording): " +
                    $"{recorder.CaptureAtStop.SegmentBodyName} {recorder.CaptureAtStop.SegmentPhase}");
            }
        }

        /// <summary>
        /// Commits the active recording tree IN FLIGHT: finalizes all recordings, spawns
        /// leaf vessels, reserves crew. The active vessel stays live (VesselSpawned=true).
        /// <para>Caller set, re-derived 2026-09-11 (finding P22): the "Commit Flight" button
        /// this docstring used to name was removed, and the live callers are now the
        /// pre-switch decision dialog's two Merge handlers
        /// (<c>Patches/MapFocusObjectOnSelectPatch.cs:624</c> and <c>:813</c> - case A's
        /// prior-session commit and case B's active-tree commit) plus the harness
        /// <c>CommitTree</c> seam verb (<c>TestCommands/ParsekTestCommandAddon.cs:2016</c>).
        /// The merge DIALOG's own commit is a different path
        /// (<c>MergeDialog.MergeCommit</c>) and does not come through here.</para>
        /// <para>Both paths run <see cref="RecordingStore.RunOptimizationPass"/> after the
        /// commit. This one spawns / adopts the leaves AFTER the pass, so the spawn stamps land
        /// on the post-split chain tips, and moves the live active vessel's stamp onto its tip
        /// (<see cref="InFlightCommitOptimization"/>).</para>
        /// </summary>
        public void CommitTreeFlight()
        {
            if (activeTree == null)
            {
                ParsekLog.ScreenMessage("No active tree to commit", 2f);
                return;
            }

            double commitUT = Planetarium.GetUniversalTime();
            ParsekLog.Info("Flight", $"CommitTreeFlight: starting tree commit at UT={commitUT:F1}");

            // Finalize all recordings (active + background)
            FinalizeTreeRecordings(activeTree, commitUT, isSceneExit: false);

            // Name the finalized terminal verdict for every recording the commit is
            // about to consume. "starting tree commit at UT=" above only proves the
            // path RAN; it is emitted unconditionally and says nothing about WHERE
            // the tree ended, so a commit made while parked in a FOREIGN body's SOI
            // is indistinguishable in the log from one made at Kerbin. These lines
            // are the log witness for the terminal classification and the terminal
            // orbit body, which is what the autotest ORBIT lane (B11/B12) asserts.
            LogCommitTerminalVerdicts(activeTree);

            // Identify the active vessel's recording
            string activeRecId = activeTree.ActiveRecordingId;
            Recording activeRec = null;
            if (activeRecId != null)
                activeTree.Recordings.TryGetValue(activeRecId, out activeRec);

            // Mark active vessel's recording as already spawned
            if (activeRec != null)
            {
                activeRec.VesselSpawned = true;
                activeRec.SpawnedVesselPersistentId = recorder != null ? recorder.RecordingVesselId :
                    (FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.persistentId : 0u);
                activeRec.LastAppliedResourceIndex = activeRec.Points.Count - 1;
            }

            // Tree resources are already live in the game — mark as applied via the
            // shared tree-scoped primitive. Phase C of the ledger / lump-sum reconciliation
            // fix (`docs/dev/done/plans/fix-ledger-lump-sum-reconciliation.md`) replaced the
            // inline equivalent with this call so the in-flight commit and merge-dialog
            // commit paths stay in lockstep.
            int markedCount = RecordingStore.MarkTreeAsApplied(activeTree);
            ParsekLog.Info("Flight",
                $"CommitTreeFlight: marked {markedCount}/{activeTree.Recordings.Count} recordings as fully applied");

            // Get spawnable leaves before commit (tree is consumed by CommitTree)
            var spawnableLeaves = activeTree.GetSpawnableLeaves();

            // Commit tree to storage
            RecordingStore.CommitTree(activeTree);

            // Same position as MergeDialog.MergeCommit: after the commit + MarkTreeAsApplied,
            // before the ledger notify. Without it a tree committed in flight kept its idle
            // tails (and unsplit / unmerged segments) until the next cold load.
            string activeTipId = RunInFlightCommitOptimizationPass(activeRec, activeRecId);

            // Recalculate crew reservations (replaces ReserveCrewForLeaves)
            LedgerOrchestrator.NotifyLedgerTreeCommitted(activeTree);

            // Spawn all non-active leaf vessels
            SpawnTreeLeaves(activeTree, activeTipId);

            // Crew swap on active vessel
            int swapped = CrewReservationManager.SwapReservedCrewInFlight();
            if (swapped > 0)
                ParsekLog.Info("Flight", $"CommitTreeFlight: swapped {swapped} crew on active vessel");

            int spawnCount = InFlightCommitOptimization.CountSpawnedLeafTips(
                spawnableLeaves, RecordingStore.CommittedRecordings, activeTipId);

            // Clear state
            var treeName = activeTree.TreeName;
            var committedTreeForPrompt = activeTree;
            activeTree = null;
            if (backgroundRecorder != null)
            {
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                backgroundRecorder = null;
            }
            recorder = null;
            lastPlaybackIndex = 0;

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions(
                ParsekScenario.GetCurrentTimelineUTForLedgerRecalc(),
                "flight-tree-commit");

            Log($"CommitTreeFlight: committed tree \"{treeName}\" — {spawnCount} vessel(s) spawned");
            if (spawnCount > 0)
                ParsekLog.ScreenMessage($"Tree merged to timeline! {spawnCount} vessel(s) spawned.", 3f);
            else
                ParsekLog.ScreenMessage("Tree merged to timeline!", 3f);

            // M6 Record-Supply-Run helper: one-time non-blocking prompt when
            // this commit produced an eligible route candidate. Covers the
            // in-flight commit path AND the pre-switch-dialog patch paths
            // (both route through CommitTreeFlight); the merge-dialog path
            // hooks in MergeDialog.MergeCommit. Internally gated (test batch,
            // restore window, prompted-once, dismissed) and never throws.
            Logistics.RouteRunPrompt.NotifyTreeCommitted(committedTreeForPrompt);
        }

        /// <summary>
        /// Runs <see cref="RecordingStore.RunOptimizationPass"/> for an in-flight tree commit
        /// (boring-tail trim, env splits, chain merges) and moves the live active vessel's
        /// spawn stamp onto its post-pass chain tip. Returns the id the rest of the commit
        /// must treat as the active vessel's recording. Skipped (the next cold load optimizes
        /// instead) while a Re-Fly session or a merge journal is live; see
        /// <see cref="InFlightCommitOptimization.DecideSkipReason"/>.
        /// </summary>
        private string RunInFlightCommitOptimizationPass(Recording activeRec, string activeRecId)
        {
            var scenario = ParsekScenario.Instance;
            bool reFly = !object.ReferenceEquals(null, scenario)
                && scenario.ActiveReFlySessionMarker != null;
            bool journal = !object.ReferenceEquals(null, scenario)
                && scenario.ActiveMergeJournal != null;
            string skipReason = InFlightCommitOptimization.DecideSkipReason(reFly, journal);
            if (skipReason != null)
            {
                ParsekLog.Info("Flight",
                    $"CommitTreeFlight: optimization pass skipped reason={skipReason} " +
                    "(the next cold load optimizes this tree)");
                return activeRecId;
            }

            int before = RecordingStore.CommittedRecordings.Count;
            bool stampMoved = false;
            // Guarded: a throw is logged as an Error and the commit continues to the ledger
            // notify and leaf spawn with whatever the committed list holds.
            string activeTipId = InFlightCommitOptimization.RunPassAndResolveActiveTip(
                RecordingStore.RunOptimizationPass,
                () =>
                {
                    Recording activeTip = InFlightCommitOptimization.ResolveChainTip(
                        activeRec, RecordingStore.CommittedRecordings);
                    stampMoved = InFlightCommitOptimization.CarrySpawnStampToTip(activeRec, activeTip);
                    return activeTip?.RecordingId;
                },
                activeRecId,
                out bool passThrew,
                out bool stampMoveThrew);
            int after = RecordingStore.CommittedRecordings.Count;
            ParsekLog.Info("Flight",
                $"CommitTreeFlight: optimization pass ran committed={before}->{after} " +
                $"activeRec={activeRecId ?? "<none>"} activeTip={activeTipId ?? "<none>"} " +
                $"spawnStampMoved={stampMoved} passThrew={passThrew} stampMoveThrew={stampMoveThrew}");
            return activeTipId;
        }

        /// <summary>
        /// Per-recording terminal lines emitted by <see cref="LogCommitTerminalVerdicts"/>
        /// before it collapses to a single summary. Matches the repo batch-counting
        /// convention: per-item lines only while the item count is bounded (~20).
        /// A tree with more leaves than this is a debris storm, not a mission.
        /// </summary>
        internal const int CommitTerminalLogLimit = 20;

        /// <summary>
        /// Formats one committed recording's terminal verdict. Pure and
        /// <c>internal static</c> so the exact wording is unit-testable without a
        /// live flight scene (the live call site is <see cref="CommitTreeFlight"/>).
        /// </summary>
        internal static string FormatCommitTerminalLine(Recording rec)
        {
            string recId = string.IsNullOrEmpty(rec?.RecordingId) ? "(null)" : rec.RecordingId;
            string terminalState = rec?.TerminalStateValue?.ToString() ?? "(null)";
            // A LANDED / SPLASHED recording never carries TerminalOrbitBody:
            // CaptureTerminalOrbit returns early for surface situations, and the
            // finalizer's refresh is gated on UsesTerminalOrbitMetadata, which
            // excludes Landed. Its body lives on TerminalPosition instead. Without
            // a fallback the ONE fact a landed-on-another-body recording exists
            // to prove - WHICH body it is sitting on - appears in no log line at
            // all, which is exactly the gap that let the orbit lane's commit claim
            // ship unverified.
            //
            // The precedence is TERMINAL-STATE-AWARE, not "orbit body if non-empty"
            // (review 2026-07-26). NOTHING ever CLEARS TerminalOrbitBody: it is
            // write-only from CaptureTerminalOrbit, CopyTerminalOrbitFromSegment
            // and the ballistic scene-exit finalizer. So a craft that orbited the
            // Mun, came home and landed on KERBIN still carries
            // TerminalOrbitBody="Mun" alongside TerminalPosition.body="Kerbin",
            // and an orbit-body-first rule would print
            // `terminalState=Landed terminalOrbitBody=Mun` - confidently wrong,
            // and wrong in the one line an operator uses to answer "where did
            // this recording end". For a SURFACE terminal the vessel's position
            // IS the authority; for every other terminal the orbit metadata is.
            string terminalBody = IsSurfaceTerminalState(rec?.TerminalStateValue)
                ? FirstNonEmpty(rec?.TerminalPosition?.body, rec?.TerminalOrbitBody)
                : FirstNonEmpty(rec?.TerminalOrbitBody, rec?.TerminalPosition?.body);
            if (string.IsNullOrEmpty(terminalBody))
            {
                terminalBody = "(null)";
            }
            return $"CommitTreeFlight terminal: rec={recId} " +
                $"terminalState={terminalState} terminalOrbitBody={terminalBody}";
        }

        /// <summary>
        /// True when the terminal classification puts the vessel ON a body's surface,
        /// so <see cref="Recording.TerminalPosition"/> is the authoritative body and
        /// any stale <c>TerminalOrbitBody</c> from an earlier orbital phase is not.
        /// The complement of <c>UsesTerminalOrbitMetadata</c> plus the states that
        /// carry no body at all (Destroyed / Recovered / Boarded fall through to the
        /// orbit-first branch, where both fields are usually empty anyway).
        /// </summary>
        internal static bool IsSurfaceTerminalState(TerminalState? state)
        {
            return state == TerminalState.Landed || state == TerminalState.Splashed;
        }

        /// <summary>
        /// The first non-empty of two candidate body names, or <c>null</c>.
        /// </summary>
        private static string FirstNonEmpty(string first, string second)
        {
            return string.IsNullOrEmpty(first) ? second : first;
        }

        /// <summary>
        /// Formats the summary emitted instead of per-recording lines when a tree
        /// carries more than <see cref="CommitTerminalLogLimit"/> recordings. Names
        /// the ROOT recording's verdict so the line still identifies where the tree
        /// ended. The line LEADS with the exact per-recording token shape
        /// (<c>CommitTreeFlight terminal: rec=... terminalState=...
        /// terminalOrbitBody=...</c>) because the harness logContract specs
        /// (B11/B12/B13/B14/B16) pin that shape as required commit evidence; a
        /// debris-heavy tree above the cap must not red a green flight with
        /// "required pattern not matched" reading as "commit never happened".
        /// Residual: only the ROOT recording's tokens survive the cap, so specs
        /// pinning token classes for NON-root recordings (the B11/B12 Kerbin-core
        /// and Destroyed rows) still rely on the tree staying under the cap.
        /// </summary>
        internal static string FormatCommitTerminalSummaryLine(RecordingTree tree)
        {
            int total = tree?.Recordings?.Count ?? 0;
            Recording root = null;
            if (tree?.Recordings != null && !string.IsNullOrEmpty(tree.RootRecordingId))
                tree.Recordings.TryGetValue(tree.RootRecordingId, out root);

            string rootId = string.IsNullOrEmpty(root?.RecordingId)
                ? (string.IsNullOrEmpty(tree?.RootRecordingId) ? "(null)" : tree.RootRecordingId)
                : root.RecordingId;
            string rootState = root?.TerminalStateValue?.ToString() ?? "(null)";
            // Same terminal-state-aware precedence as the per-recording line: this
            // summary replaces those lines above the cap, so it must not report a
            // DIFFERENT body for the same root recording.
            string rootBody = IsSurfaceTerminalState(root?.TerminalStateValue)
                ? FirstNonEmpty(root?.TerminalPosition?.body, root?.TerminalOrbitBody)
                : FirstNonEmpty(root?.TerminalOrbitBody, root?.TerminalPosition?.body);
            if (string.IsNullOrEmpty(rootBody))
            {
                rootBody = "(null)";
            }

            return $"CommitTreeFlight terminal: rec={rootId} " +
                $"terminalState={rootState} terminalOrbitBody={rootBody} " +
                $"(root; summary of {total} recordings over the " +
                $"{CommitTerminalLogLimit} per-line cap)";
        }

        /// <summary>
        /// Logs the terminal classification + terminal orbit body of every recording
        /// in a tree that is about to be committed. One line per recording while the
        /// set is bounded; a single summary naming the root recording above the cap.
        /// </summary>
        internal static void LogCommitTerminalVerdicts(RecordingTree tree)
        {
            if (tree?.Recordings == null || tree.Recordings.Count == 0)
            {
                ParsekLog.Info("Flight",
                    "CommitTreeFlight terminal: no recordings in tree " +
                    $"'{tree?.TreeName ?? "(null)"}'");
                return;
            }

            if (tree.Recordings.Count > CommitTerminalLogLimit)
            {
                ParsekLog.Info("Flight", FormatCommitTerminalSummaryLine(tree));
                return;
            }

            foreach (var kvp in tree.Recordings)
                ParsekLog.Info("Flight", FormatCommitTerminalLine(kvp.Value));
        }

        private void ReserveCrewForLeaves(List<Recording> spawnableLeaves)
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster != null)
            {
                using (SuppressionGuard.Crew())
                {
                    foreach (var leaf in spawnableLeaves)
                    {
                        if (leaf.VesselSpawned) continue;
                        if (leaf.VesselSnapshot == null) continue;
                        CrewReservationManager.ReserveCrewIn(leaf.VesselSnapshot, false, roster);
                    }
                }
            }
        }

        /// <summary>
        /// Finalizes the active tree for a revert (Flight -> Flight).
        /// Preserves vessel snapshots so the merge dialog can offer spawning.
        /// </summary>
        void CommitTreeRevert(double commitUT)
        {
            if (activeTree == null) return;

            ParsekLog.Info("Flight", $"CommitTreeRevert: finalizing tree at UT={commitUT:F1}");
            RecorderStateLog.RecState("CommitTreeRevert:entry", CaptureRecorderState());

            // Finalize all recordings (active + background)
            FinalizeTreeRecordings(activeTree, commitUT, isSceneExit: false);

            // Stash as pending tree -- snapshots preserved for merge dialog
            PendingTreeSlot.StashPendingTree(activeTree);

            ParsekLog.Info("Flight",
                $"CommitTreeRevert: stashed pending tree '{activeTree.TreeName}' with snapshots preserved");
            RecorderStateLog.RecState("CommitTreeRevert:post", CaptureRecorderState());
        }

        /// <summary>
        /// Quickload-resume path (Bug C fix). Waits for the active vessel to load,
        /// matches it by name against the pending-Limbo tree's active recording,
        /// updates the tree's vessel PID (KSP can regenerate PIDs across quickload),
        /// constructs a fresh <see cref="FlightRecorder"/>, and calls
        /// <c>StartRecording(isPromotion: true)</c> so recording resumes appending to
        /// the same chain segment that was active at quicksave time.
        ///
        /// <para>The <c>isPromotion: true</c> flag invokes bug A's seed-event skip
        /// so no duplicate DeployableExtended / LightOn / etc. events fire at the
        /// quickload UT.</para>
        ///
        /// <para>If the vessel can't be found within 3 seconds (name change, EVA
        /// boarded between save and load, etc.), the tree is left in pending-Limbo
        /// and a warning logged. The player can manually trigger the merge dialog
        /// via scene exit if they want to commit it as-is.</para>
        /// </summary>
        IEnumerator RestoreActiveTreeFromPending()
        {
            // #267: reentrancy guard — prevents OnVesselSwitchComplete, OnVesselWillDestroy,
            // FinalizeTreeOnSceneChange from mutating activeTree/recorder during the yield window.
            restoringActiveTree = true;
            try
            {
            // NOTE: body intentionally not re-indented to minimize diff
            RecorderStateLog.RecState("Restore:start", CaptureRecorderState());
            // Re-Fly retry carve-out: when the previous Re-Fly attempt was
            // post-destruction finalized before the player clicked Esc >
            // Revert > Retry from Rewind Point, the in-memory pending tree
            // is in Finalized state (set by ShowPostDestructionTreeMergeDialog
            // / CommitTreeSceneExit). TryRestoreActiveTreeNode keeps the
            // Finalized tree as-is (#290d) and no Quickload restore gets
            // scheduled, so the new in-place fork that AtomicMarkerWrite
            // just attached to the same tree has no recorder bound. Accept
            // the Finalized state here when a Re-Fly session is active so
            // the marker-swap path below picks up the new fork and binds a
            // recorder to it. Without this carve-out, the OnFlightReady
            // merge-dialog skip leaves the player in a "fresh" attempt with
            // no recording happening at all.
            // The coroutine is called both from the existing Limbo dispatch
            // and from the OnFlightReady Re-Fly-retry carve-out (which
            // upgrades restoreMode based on ShouldUpgradeRestoreModeForReFlyRetry).
            // Re-read InPlaceContinuation here rather than trusting a
            // dispatch-time snapshot — between StartCoroutine and the body
            // executing, other code paths could in theory mutate the marker.
            // The synchronous flow has the marker stable in practice, but
            // the explicit re-read keeps this independent of dispatcher
            // assumptions.
            bool acceptFinalizedForReFly = ShouldAcceptFinalizedPendingTreeForReFlyRetry(
                hasPendingTree: RecordingStore.HasPendingTree,
                pendingTreeIsFinalized:
                    RecordingStore.PendingTreeStateValue == PendingTreeState.Finalized,
                reFlyInPlaceContinuationActive:
                    ParsekScenario.IsReFlyInPlaceContinuationActive());
            if (!RecordingStore.HasPendingTree
                || (RecordingStore.PendingTreeStateValue != PendingTreeState.Limbo
                    && !acceptFinalizedForReFly))
            {
                ParsekLog.Verbose("Flight",
                    "RestoreActiveTreeFromPending: no pending-Limbo tree, skipping");
                yield break;
            }
            if (acceptFinalizedForReFly)
            {
                ParsekLog.Info("Flight",
                    $"RestoreActiveTreeFromPending: accepting Finalized pending tree " +
                    $"'{RecordingStore.PendingTree?.TreeName}' — active Re-Fly session " +
                    "needs a recorder bound to the freshly-attached fork");
            }

            var tree = RecordingStore.PendingTree;
            string activeRecId = tree.ActiveRecordingId;

            // Bug #585 follow-up (PR #558 P1 review): gate marker read on
            // RewindInvokeContext consumption. In the async-FLIGHT-load path,
            // ParsekScenario.OnLoad schedules our restore for OnFlightReady
            // BEFORE RewindInvoker.RunStripActivateMarker has had a chance to
            // run AtomicMarkerWrite -- RunStripActivateMarker is itself
            // deferred via WaitForFlightReadyAndInvoke until
            // FlightGlobals.ready flips, which races with the
            // GameEvents.onFlightReady fire that triggers us. If we read the
            // marker before AtomicMarkerWrite completes we see it as null,
            // fall through to no-swap, and the wait loop targets the stale
            // pre-rewind ActiveRecordingId -- exactly the bug #585 race.
            // Yield until the pending invocation context clears (or a bounded
            // timeout) so the marker write is guaranteed to have completed.
            if (RewindInvokeContext.Pending)
            {
                int markerWaitFrame = 0;
                const int MaxMarkerWaitFrames = 300;
                while (RewindInvokeContext.Pending && markerWaitFrame < MaxMarkerWaitFrames)
                {
                    markerWaitFrame++;
                    yield return null;
                }
                if (RewindInvokeContext.Pending)
                {
                    ParsekLog.Warn("Flight",
                        $"RestoreActiveTreeFromPending: timed out waiting for RewindInvokeContext " +
                        $"to clear after {MaxMarkerWaitFrames} frame(s); proceeding without marker swap " +
                        $"(bug #585 follow-up: marker write race)");
                }
                else
                {
                    ParsekLog.Verbose("Flight",
                        $"RestoreActiveTreeFromPending: waited {markerWaitFrame} frame(s) for " +
                        $"RewindInvokeContext to clear before reading ActiveReFlySessionMarker");
                }
            }

            // Bug #585: in-place continuation Re-Fly carve-out. The rewind
            // quicksave's ActiveRecordingId still points at the pre-rewind
            // active vessel (just killed by PostLoadStripper). The live
            // ReFlySessionMarker pins the recording the player wants to keep
            // recording into; for an in-place continuation
            // (OriginChildRecordingId == ActiveReFlyRecordingId) we must swap
            // the wait target to the marker's recording. Without the swap the
            // wait loop targets a dead pid, times out at 3s, and the tree
            // stays in Limbo for the rest of the session.
            string preMarkerActiveRecId = activeRecId;
            string preMarkerActiveName = activeRecId != null
                && tree.Recordings.TryGetValue(activeRecId, out var preMarkerActiveRec)
                ? preMarkerActiveRec?.VesselName : null;
            uint preMarkerActivePid = activeRecId != null
                && tree.Recordings.TryGetValue(activeRecId, out var preMarkerActiveRec2)
                ? (preMarkerActiveRec2?.VesselPersistentId ?? 0u) : 0u;
            var marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            // Issue #734 race fix: AtomicMarkerWrite usually attaches the
            // in-place fork to the pending tree at marker-write time, but
            // on async FLIGHT loads both AtomicMarkerWrite and this restore
            // coroutine defer to onFlightReady; if this coroutine fires
            // first (or AtomicMarkerWrite ran on a different tree handle),
            // the fork lives in CommittedRecordings but is not yet in
            // tree.Recordings. ResolveInPlaceContinuationTarget would then
            // refuse the swap with reason="marker-recording-missing-from-tree"
            // and the recorder's flush would warn / data-loss. Reconcile
            // here from the committed list before the swap runs. The
            // helper is idempotent so it is a no-op when the eager path
            // already attached.
            ReconcileInPlaceForkIntoTreeIfNeeded(tree, marker);

            var markerSwap = ReFlySessionMarker.ResolveInPlaceContinuationTarget(
                marker,
                tree.Id,
                activeRecId,
                recId =>
                {
                    if (string.IsNullOrEmpty(recId)) return null;
                    if (!tree.Recordings.TryGetValue(recId, out var rec) || rec == null)
                        return null;
                    return (rec.VesselName, rec.VesselPersistentId);
                });
            if (markerSwap.ShouldSwap)
            {
                tree.ActiveRecordingId = markerSwap.TargetRecordingId;
                activeRecId = markerSwap.TargetRecordingId;
                // Bug #585 follow-up (PR #558 P2 review): the tree was loaded
                // with BackgroundMap rebuilt against the OLD ActiveRecordingId,
                // so the NEW active recording may still appear as a background
                // entry in tree.BackgroundMap. EnsureBackgroundRecorderAttached
                // later seeds the BackgroundRecorder from this map -- without
                // a rebuild, the recording would be tracked as both active
                // (live recorder) and background (BackgroundRecorder).
                // RebuildBackgroundMap re-runs IsBackgroundMapEligible against
                // the swapped ActiveRecordingId and excludes it from the map.
                int bgEntriesBefore = tree.BackgroundMap.Count;
                tree.RebuildBackgroundMap();
                int bgEntriesAfter = tree.BackgroundMap.Count;
                ParsekLog.Info("Flight",
                    $"RestoreActiveTreeFromPending: in-place continuation marker swapped target " +
                    $"rec='{preMarkerActiveRecId ?? "<null>"}'->\'{markerSwap.TargetRecordingId}\' " +
                    $"vessel='{preMarkerActiveName ?? "<null>"}'->\'{markerSwap.TargetVesselName ?? "<null>"}\' " +
                    $"pid={preMarkerActivePid}->{markerSwap.TargetVesselPersistentId} " +
                    $"sess={marker?.SessionId ?? "<no-id>"} " +
                    $"bgMapEntries={bgEntriesBefore}->{bgEntriesAfter}");
            }
            else if (marker != null)
            {
                ParsekLog.Verbose("Flight",
                    $"RestoreActiveTreeFromPending: marker present but no swap " +
                    $"(reason={markerSwap.Reason ?? "<none>"} sess={marker.SessionId ?? "<no-id>"})");
            }

            if (string.IsNullOrEmpty(activeRecId)
                || !tree.Recordings.TryGetValue(activeRecId, out var activeRec))
            {
                ParsekLog.Warn("Flight",
                    $"RestoreActiveTreeFromPending: pending tree '{tree.TreeName}' has no resolvable " +
                    $"activeRecordingId — leaving in Limbo");
                yield break;
            }

            string targetName = activeRec.VesselName;
            uint targetPid = activeRec.VesselPersistentId;
            ParsekLog.Info("Flight",
                $"RestoreActiveTreeFromPending: waiting for vessel '{targetName}' (pid={targetPid}) to load " +
                $"(activeRecId={activeRecId})");

            // Wait up to 3 seconds for the current active vessel to match.
            // Primary match = PID (locale-proof, available immediately even when
            // vesselName is still a raw #autoLOC key). Bug #290b.
            // Secondary match = vessel name, only for identity-less legacy recordings (no PID
            // and no launch guid; QuickloadResumeMatchGuard.MayMatchByNameFallback).
            // Tertiary (edge case 3): if the active recording was an EVA kerbal who got
            // boarded between quicksave and quickload, match against the parent recording's
            // vessel by walking ParentRecordingId up the EVA chain.
            //
            // BDOCK-1 flight-16 guard (INV4-PARTEVENT-PID red): every acceptance is
            // gated through QuickloadResumeMatchGuard. A same-session launch_vessel
            // FLIGHT->FLIGHT reload can be misclassified as a quickload (stale
            // vesselSwitchPending flag); the new scene's active vessel is then a FRESH
            // ROLLOUT of (typically) the same craft file, and the name fallback happily
            // matches it — remapping the stashed tree's recording onto a different
            // physical launch and corrupting it with foreign-pid part events. A fresh
            // rollout is rejected outright (recording-independent, break immediately);
            // a conclusive RecordedVesselGuid-vs-Vessel.id mismatch skips the pid/name
            // match for that recording while still allowing the EVA-parent walk to
            // match a parent whose guid agrees.
            Recording matchedRec = activeRec;
            string matchedRecId = activeRecId;
            float deadline = UnityEngine.Time.time + 3f;
            Vessel matched = null;
            bool loggedGuidReject = false;
            var loggedParentGuidRejects = new HashSet<string>();
            uint refusedFreshRolloutPid = 0;
            while (UnityEngine.Time.time < deadline)
            {
                var v = FlightGlobals.ActiveVessel;
                if (v != null)
                {
                    string liveGuid = v.id != Guid.Empty ? v.id.ToString("N") : null;

                    if (QuickloadResumeMatchGuard.IsFreshRolloutCandidate(
                            v.persistentId, SceneEntryVesselScope.SceneEntryFreshRolloutVesselPid))
                    {
                        ParsekLog.Info("Flight",
                            $"RestoreActiveTreeFromPending: refusing to adopt fresh-rollout vessel " +
                            $"'{v.vesselName}' pid={v.persistentId} liveGuid={liveGuid ?? "<none>"} " +
                            $"for recording '{activeRecId}' (recordedPid={targetPid}, " +
                            $"recordedGuid={activeRec.RecordedVesselGuid ?? "<none>"}) — a fresh " +
                            "launch is a different physical launch; not adopting");
                        refusedFreshRolloutPid = v.persistentId;
                        break;
                    }

                    bool activeRecGuidRejected = QuickloadResumeMatchGuard.LaunchGuidConclusivelyDiffers(
                        activeRec.RecordedVesselGuid, liveGuid);
                    if (activeRecGuidRejected && !loggedGuidReject)
                    {
                        loggedGuidReject = true;
                        ParsekLog.Info("Flight",
                            $"RestoreActiveTreeFromPending: active vessel '{v.vesselName}' " +
                            $"pid={v.persistentId} liveGuid={liveGuid ?? "<none>"} conclusively " +
                            $"differs from recording '{activeRecId}' recordedGuid=" +
                            $"{activeRec.RecordedVesselGuid ?? "<none>"} — skipping pid/name match " +
                            "(EVA-parent fallback still evaluated)");
                    }

                    // Primary: PID match (locale-proof, available immediately)
                    if (!activeRecGuidRejected && targetPid != 0 && v.persistentId == targetPid)
                    {
                        matched = v;
                        ParsekLog.Verbose("Flight",
                            $"RestoreActiveTreeFromPending: PID match pid={targetPid}");
                        break;
                    }
                    // Secondary: name match, ONLY for an identity-less legacy recording (no
                    // pid and no launch guid). A recording with a pid or guid is matched by
                    // identity alone: a renamed vessel keeps both, and a same-named vessel of
                    // another launch must never be adopted.
                    if (!activeRecGuidRejected
                        && QuickloadResumeMatchGuard.MayMatchByNameFallback(
                            targetPid, activeRec.RecordedVesselGuid)
                        && v.vesselName == targetName)
                    {
                        matched = v;
                        ParsekLog.Verbose("Flight",
                            $"RestoreActiveTreeFromPending: name match '{targetName}'");
                        break;
                    }
                    // Tertiary: walk ParentRecordingId chain and try each parent's PID/name
                    Recording probe = activeRec;
                    int walkDepth = 0;
                    while (walkDepth < 5 && !string.IsNullOrEmpty(probe?.ParentRecordingId))
                    {
                        if (!tree.Recordings.TryGetValue(probe.ParentRecordingId, out probe))
                            break;
                        if (probe != null &&
                            QuickloadResumeMatchGuard.LaunchGuidConclusivelyDiffers(
                                probe.RecordedVesselGuid, liveGuid))
                        {
                            // Guard skip must be logged (review of PR #1345): a
                            // guid-rejected parent otherwise declines silently and
                            // the coroutine ends in the generic not-active Warn
                            // with no trace this gate fired. Once per parent, not
                            // per polling frame (review of PR #1371) - this walk
                            // re-runs every frame of the 3 s wait, mirroring the
                            // loggedGuidReject latch on the active-rec reject above.
                            if (loggedParentGuidRejects.Add(probe.RecordingId ?? string.Empty))
                                ParsekLog.Info("Flight",
                                    $"RestoreActiveTreeFromPending: parent-walk guid gate " +
                                    $"rejected rec={ShortRecordingId(probe.RecordingId)} " +
                                    $"(recordedGuid differs conclusively from live vessel)");
                            walkDepth++;
                            continue;
                        }
                        if (probe != null && (
                            (probe.VesselPersistentId != 0 && v.persistentId == probe.VesselPersistentId) ||
                            (QuickloadResumeMatchGuard.MayMatchByNameFallback(
                                probe.VesselPersistentId, probe.RecordedVesselGuid)
                             && v.vesselName == probe.VesselName)))
                        {
                            matched = v;
                            matchedRec = probe;
                            matchedRecId = probe.RecordingId;
                            ParsekLog.Info("Flight",
                                $"RestoreActiveTreeFromPending: fallback match — active vessel pid={v.persistentId} " +
                                $"matches parent recording '{matchedRecId}' (original active was '{targetName}', " +
                                $"EVA kerbal likely boarded between F5 and F9)");
                            // Flip the tree's active-recording pointer to the parent
                            tree.ActiveRecordingId = matchedRecId;
                            break;
                        }
                        walkDepth++;
                    }
                    if (matched != null) break;
                }
                yield return null;
            }

            // FRESH-LAUNCH-REFUSED-TREE-PENDING-LIFETIME / -KEPT-TREE-PENDING: a tree refused
            // for the fresh rollout is resolved here so nothing parked lingers for a later
            // stash to overwrite or a merge dialog to offer over committed recordings: an
            // idle resumed copy of a committed tree (BDOCK-1's re-adopted station) is
            // discarded, every other tree is committed. A Re-Fly-owned tree, one under an
            // active merge journal or a replaced slot falls through to the Limbo give-up.
            if (matched == null
                && refusedFreshRolloutPid != 0
                && IsFreshRolloutRefusedTreeResolved(DisposeFreshRolloutRefusedPendingTree(
                        "quickload-restore", tree, refusedFreshRolloutPid)))
            {
                yield break;
            }

            if (matched == null)
            {
                ParsekLog.Warn("Flight",
                    $"RestoreActiveTreeFromPending: vessel '{targetName}' (and no EVA parent fallback) " +
                    "not active within 3s — leaving tree in Limbo (user can trigger merge dialog via scene exit)");

                // R1-EMPTY-PROVISIONAL detection point 1. The generic Limbo warn
                // above says nothing about the Re-Fly session, so a give-up here
                // has always been indistinguishable from an ordinary failed
                // quickload resume. If a session IS live, no recorder is bound to
                // its provisional at this moment, and unless something binds one
                // later the merge will have nothing to supersede the origin with.
                // Observation only: raise loudly, do not change control flow.
                var giveUpMarker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
                UnboundProvisionalRaise giveUp =
                    ReFlyProvisionalBinding.EvaluateRestoreGiveUp(giveUpMarker, tree?.Id);
                if (giveUp.ShouldRaise)
                {
                    ParsekLog.Warn("ReFlySession",
                        $"outcome=unbound-refly-provisional reason={giveUp.Reason} " +
                        $"sess={giveUpMarker.SessionId ?? "<no-id>"} " +
                        $"provisional={giveUpMarker.ActiveReFlyRecordingId ?? "<none>"} " +
                        $"markerTree={giveUpMarker.TreeId ?? "<none>"} " +
                        $"attemptedTree={tree?.Id ?? "<none>"} " +
                        $"attemptedRec={activeRecId ?? "<none>"} " +
                        $"attemptedVessel='{targetName}' attemptedPid={targetPid} — the re-fly " +
                        "restore gave up, so NOTHING is recording into this session's " +
                        "provisional. Unless a recorder binds to it before the merge, the " +
                        "re-fly will supersede nothing and the origin branch stays effective");
                    try
                    {
                        ParsekLog.ScreenMessage(
                            "Parsek: re-fly attempt is not recording; it will not replace "
                            + "the original", 8f);
                    }
                    catch (System.NullReferenceException) { /* xUnit: no KSP UI */ }
                    catch (System.MissingMethodException) { /* xUnit: no KSP UI */ }
                    catch (System.TypeInitializationException) { /* xUnit: no KSP UI */ }
                }
                yield break;
            }

            // After a parent-fallback match, the rest of the coroutine uses matchedRec/matchedRecId
            activeRec = matchedRec;
            activeRecId = matchedRecId;
            RecorderStateLog.RecState("Restore:matched", CaptureRecorderState());

            // PID remap: KSP may have regenerated the persistentId across quickload.
            // Tree.BackgroundMap is keyed by PID, so update any old-PID entries too.
            uint oldPid = activeRec.VesselPersistentId;
            uint newPid = matched.persistentId;
            if (oldPid != newPid)
            {
                activeRec.VesselPersistentId = newPid;
                if (oldPid != 0 && tree.BackgroundMap.TryGetValue(oldPid, out var bgRecId))
                {
                    tree.BackgroundMap.Remove(oldPid);
                    if (bgRecId != activeRecId)
                        tree.BackgroundMap[newPid] = bgRecId;
                }
                ParsekLog.Info("Flight",
                    $"RestoreActiveTreeFromPending: PID remap {oldPid} → {newPid} for '{targetName}'");
            }

            // Pop the tree out of pending-Limbo (non-destructive — preserves sidecar files)
            // and re-install as the live active tree.
            activeTree = PendingTreeSlot.PopPendingTree();
            if (activeTree == null)
            {
                ParsekLog.Warn("Flight",
                    "RestoreActiveTreeFromPending: PopPendingTree returned null after we verified the tree exists");
                yield break;
            }
            // PopPendingTree just emptied the main pending slot; a saved pending
            // tree from the same save can now move back unless another path filled it.
            PendingTreeSlot.PromoteSavedPendingTreeAfterActiveRestore(
                "RestoreActiveTreeFromPending");

            // Construct a fresh FlightRecorder pointed at the restored tree.
            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;

            // Restore recorder state persisted in the PARSEK_ACTIVE_TREE node
            if (!string.IsNullOrEmpty(ParsekScenario.pendingActiveTreeResumeRewindSave))
            {
                recorder.SetRewindSaveFileNameForRestore(
                    ParsekScenario.pendingActiveTreeResumeRewindSave,
                    "quickload-resume from PARSEK_ACTIVE_TREE");
                ParsekScenario.pendingActiveTreeResumeRewindSave = null;
            }

            // Bug fix: clear any stale Destroyed terminal verdict on the
            // recording we're about to resume. A transient NullSolver event
            // earlier in the recording (e.g. stage decouple) may have made
            // TryFinalizeRecording fall back to live-orbit and stamp Destroyed
            // via the sub-surface path. Without this clear, FinalizerCache's
            // already-classified-destroyed short-circuit declines every refresh
            // for the rest of the session, and the vessel stays labelled
            // "Destroyed" in the STASH UI even though the player is flying it
            // again post-rewind. The stamp lives on the Recording, so resetting
            // the in-memory FinalizationCache via StartRecording is not enough.
            IncompleteBallisticSceneExitFinalizer.ClearStaleDestroyedTerminalForResume(
                activeRec,
                "RestoreActiveTreeFromPending");

            // Start recording as a promotion (isPromotion: true) so the Bug A seed-event
            // skip kicks in — no duplicate DeployableExtended / LightOn / etc. events at
            // the quickload UT. The new recorder's buffers start empty and will append
            // to activeTree.Recordings[activeRecId] on the next chain boundary or
            // scene exit flush.
            recorder.StartRecording(isPromotion: true);
            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight",
                    $"RestoreActiveTreeFromPending: StartRecording returned IsRecording=false for " +
                    $"'{targetName}' — restore incomplete");
                yield break;
            }

            PrepareSessionStateForRecorderStart("RestoreActiveTreeFromPending");

            // Re-attach the BackgroundRecorder for the tree.
            EnsureBackgroundRecorderAttached("RestoreActiveTreeFromPending");

            ParsekLog.Info("Flight",
                $"RestoreActiveTreeFromPending: resumed recording tree '{activeTree.TreeName}' " +
                $"activeRec='{activeRecId}' vessel='{targetName}' pid={newPid} at UT={Planetarium.GetUniversalTime():F1}");
            RecorderStateLog.RecState("Restore:after-start", CaptureRecorderState());
            } // try
            finally
            {
                restoringActiveTree = false;
            }

            // M1 (PR #876 final review): dispatch any deferred consume now that
            // the restored tree is live. DispatchConsumeIntentIfArmed at the
            // top of OnFlightReady skips when ScheduleActiveTreeRestoreOnFlightReady
            // is non-None to avoid racing the restore. Re-invoking here picks
            // up an intent that survived the F5 -> reload round-trip and
            // attaches the new switch segment to the just-restored tree.
            DispatchConsumeIntentIfArmed();
        }

        /// <summary>
        /// Pure guard for <see cref="RestoreActiveTreeFromPendingForVesselSwitch"/>: true
        /// when the new scene's active vessel is the scene-entry fresh rollout, which a
        /// vessel-switch reload can never produce.
        /// </summary>
        internal static bool ShouldRefuseVesselSwitchRestoreForFreshRollout(
            uint activeVesselPid, uint sceneEntryFreshRolloutPid)
        {
            return QuickloadResumeMatchGuard.IsFreshRolloutCandidate(
                activeVesselPid, sceneEntryFreshRolloutPid);
        }

        /// <summary>
        /// What happens to the pending tree after a restore coroutine refused to put it
        /// on the scene-entry fresh rollout (todo FRESH-LAUNCH-REFUSED-TREE-PENDING-LIFETIME).
        /// </summary>
        internal enum FreshRolloutRefusedTreeDisposition
        {
            /// <summary>No pending tree; nothing to do.</summary>
            NoPendingTree = 0,
            /// <summary>A resumed copy of a committed tree with meaningless post-load content: discard it.</summary>
            DiscardResumedCommittedCopy = 1,
            /// <summary>
            /// No committed tree shares its id: a genuinely new tree, or a clean resumed
            /// copy whose committed original a save-loaded active node detached (the
            /// pending tree is then the only holder of that history). Commit it.
            /// </summary>
            CommitNotACommittedCopy = 2,
            /// <summary>A Re-Fly session or invocation owns the pending tree: keep it.</summary>
            KeepReFlyOwned = 3,
            /// <summary>
            /// A resumed copy that recorded something meaningful after the load: commit it
            /// over its committed original (the normal same-id replace commit).
            /// </summary>
            CommitMeaningfulResumedCopy = 4,
            /// <summary>A merge journal is active (the finisher owns tree state): keep it.</summary>
            KeepMergeJournalActive = 5,
            /// <summary>The pending slot no longer holds the tree the coroutine refused: keep it.</summary>
            KeepSlotReplaced = 6,
            /// <summary>
            /// The commit ran but the pending slot still holds the tree (the commit threw
            /// or refused): kept pending, as before the operator ruling.
            /// </summary>
            KeepCommitFailed = 7,
        }

        /// <summary>
        /// True when the refusal resolved the refused tree (discarded or committed), so
        /// the calling coroutine has nothing left to do with it. Every Keep outcome
        /// leaves the tree pending and the caller keeps its pre-ruling behaviour.
        /// </summary>
        internal static bool IsFreshRolloutRefusedTreeResolved(
            FreshRolloutRefusedTreeDisposition disposition)
        {
            return disposition == FreshRolloutRefusedTreeDisposition.DiscardResumedCommittedCopy
                || disposition == FreshRolloutRefusedTreeDisposition.CommitNotACommittedCopy
                || disposition == FreshRolloutRefusedTreeDisposition.CommitMeaningfulResumedCopy;
        }

        /// <summary>
        /// Pure decision for a pending tree refused for a fresh rollout. The guards come
        /// first: a replaced slot, a Re-Fly session or invocation, or an active merge journal
        /// keeps the tree pending exactly as before. Past the guards a RESUMED copy of a
        /// committed tree (the copy-on-write clone a committed-tree restore made, identified
        /// by its id still being in the committed store) whose post-load content is
        /// meaningless (<see cref="RefusedResumedCopyTail"/>) is discarded; every other
        /// tree is committed at the refusal (operator ruling 2026-09-26, todo
        /// FRESH-LAUNCH-REFUSED-KEPT-TREE-PENDING).
        /// </summary>
        internal static FreshRolloutRefusedTreeDisposition ClassifyFreshRolloutRefusedTree(
            RecordingTree pendingTree,
            bool slotHoldsRefusedTree,
            bool committedTreeWithSameIdPresent,
            bool reFlyOwnsPendingTree,
            bool mergeJournalActive,
            bool postLoadContentNoOp)
        {
            if (pendingTree == null)
                return FreshRolloutRefusedTreeDisposition.NoPendingTree;
            if (!slotHoldsRefusedTree)
                return FreshRolloutRefusedTreeDisposition.KeepSlotReplaced;
            if (reFlyOwnsPendingTree)
                return FreshRolloutRefusedTreeDisposition.KeepReFlyOwned;
            if (mergeJournalActive)
                return FreshRolloutRefusedTreeDisposition.KeepMergeJournalActive;
            if (string.IsNullOrEmpty(pendingTree.Id) || !committedTreeWithSameIdPresent)
                return FreshRolloutRefusedTreeDisposition.CommitNotACommittedCopy;
            if (!postLoadContentNoOp)
                return FreshRolloutRefusedTreeDisposition.CommitMeaningfulResumedCopy;
            return FreshRolloutRefusedTreeDisposition.DiscardResumedCommittedCopy;
        }

        /// <summary>
        /// Shared tail of both fresh-rollout refusals (<see cref="RestoreActiveTreeFromPending"/>
        /// and <see cref="RestoreActiveTreeFromPendingForVesselSwitch"/>). For a discardable
        /// resumed copy it runs the merge dialog's whole-tree Discard steps: the discard
        /// primitive <see cref="ParsekScenario.DiscardPendingTreeAndRecalculate"/> (keeps every
        /// committed-overlap recording's sidecars and events, deletes only pending-only files,
        /// purges same-id event tails past the committed cutoffs, clears the restore attempt;
        /// its <c>TreeDiscardPurge</c> clears a merge journal scoped to the tree), then clears a
        /// switch-segment session bound to the tree, then refreshes <c>persistent.sfs</c> when
        /// the copy was already serialized. The committed original is never touched. Every
        /// other unguarded tree is committed through
        /// <see cref="CommitFreshRolloutRefusedPendingTree"/>. Returns the disposition; the
        /// caller keeps its existing behaviour for every Keep outcome.
        /// </summary>
        internal static FreshRolloutRefusedTreeDisposition DisposeFreshRolloutRefusedPendingTree(
            string refusalSite, RecordingTree refusedTree, uint refusedVesselPid)
        {
            RecordingTree pending = RecordingStore.PendingTree;
            bool slotMatches = refusedTree != null && ReferenceEquals(pending, refusedTree);
            RecordingTree committedTree = RecordingStore.FindCommittedTreeById(pending?.Id);
            RefusedResumedCopyTail.Result tail = pending != null && committedTree != null
                ? RefusedResumedCopyTail.Evaluate(pending, committedTree)
                : new RefusedResumedCopyTail.Result { TailFromUT = double.NaN, TailToUT = double.NaN };
            bool reFlyOwns = ParsekScenario.IsReFlySessionActiveForQuickloadDiscard();
            var scenario = ParsekScenario.Instance;
            bool journalActive = !object.ReferenceEquals(null, scenario)
                && scenario.ActiveMergeJournal != null;
            var disposition = ClassifyFreshRolloutRefusedTree(
                pending, slotMatches, committedTree != null, reFlyOwns, journalActive, tail.IsNoOp);
            string site = refusalSite ?? "<unknown>";
            var ic = CultureInfo.InvariantCulture;
            string span = double.IsNaN(tail.TailFromUT)
                ? "none"
                : tail.TailFromUT.ToString("F1", ic) + ".." + tail.TailToUT.ToString("F1", ic) +
                  " (" + (tail.TailToUT - tail.TailFromUT).ToString("F1", ic) + "s)";

            switch (disposition)
            {
                case FreshRolloutRefusedTreeDisposition.DiscardResumedCommittedCopy:
                {
                    string treeId = pending.Id;
                    int total = pending.Recordings?.Count ?? 0;
                    bool serialized = PendingTreeSlot.PendingTreeSerializedForSave;
                    ParsekLog.Info("Flight",
                        $"Fresh-rollout refusal ({site}): discarding resumed copy of committed tree " +
                        $"'{pending.TreeName}' (id={treeId}, state={RecordingStore.PendingTreeStateValue}, " +
                        $"recordings={total}, droppedSpan={span}, droppedPoints={tail.TailPointCount}, " +
                        $"restoreAttemptArmed={CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptTree(treeId)}, " +
                        $"serialized={serialized}, refusedPid={refusedVesselPid}) - committed history " +
                        "stays as committed, only the idle resumed seconds after the load are dropped");
                    ParsekScenario.DiscardPendingTreeAndRecalculate(
                        "fresh-rollout refusal discarded resumed committed copy tree=" + treeId +
                        " site=" + site);

                    // MergeDiscard's scoped switch-segment step is not run here, so a session
                    // bound to this tree would outlive it and resolve the committed tree on
                    // the next load (deferred merge dialog). Clear it.
                    var session = object.ReferenceEquals(null, scenario)
                        ? null : scenario.ActiveSwitchSegmentSession;
                    if (session != null && string.Equals(session.TreeId, treeId, StringComparison.Ordinal))
                        scenario.ClearSwitchSegmentSession(
                            "fresh-rollout refusal discarded resumed committed copy tree=" + treeId);

                    // A serialized copy sits in persistent.sfs as a resume node; rewrite it.
                    // quicksave.sfs is deliberately NOT refreshed (MergeDiscard does both): this
                    // discard is automatic, and overwriting the player's quicksave without a
                    // player action would destroy their F5 point.
                    if (serialized)
                        RecordingStore.RefreshPersistentSaveAfterDiscard(
                            "fresh-rollout refusal discard", total);
                    break;
                }
                case FreshRolloutRefusedTreeDisposition.CommitMeaningfulResumedCopy:
                    disposition = CommitFreshRolloutRefusedPendingTree(
                        site, pending, disposition, refusedVesselPid,
                        "meaningful-resumed-copy postLoadSpan=" + span +
                        " postLoadPoints=" + tail.TailPointCount.ToString(ic) +
                        " reason=" + (tail.KeepReason ?? "<none>"));
                    break;
                case FreshRolloutRefusedTreeDisposition.CommitNotACommittedCopy:
                    disposition = CommitFreshRolloutRefusedPendingTree(
                        site, pending, disposition, refusedVesselPid,
                        "not-a-committed-copy (a genuinely new tree, or a resumed copy whose " +
                        "committed original a save-loaded active node detached)");
                    break;
                case FreshRolloutRefusedTreeDisposition.KeepReFlyOwned:
                    ParsekLog.Info("Flight",
                        $"Fresh-rollout refusal ({site}): keeping pending tree '{pending.TreeName}' " +
                        $"(id={pending.Id ?? "<none>"}) - a Re-Fly session owns it; not discarded");
                    break;
                case FreshRolloutRefusedTreeDisposition.KeepMergeJournalActive:
                    ParsekLog.Info("Flight",
                        $"Fresh-rollout refusal ({site}): keeping pending tree '{pending.TreeName}' " +
                        $"(id={pending.Id ?? "<none>"}) - merge journal " +
                        $"{scenario.ActiveMergeJournal.JournalId ?? "<no-id>"} is active; not discarded");
                    break;
                case FreshRolloutRefusedTreeDisposition.KeepSlotReplaced:
                    ParsekLog.Warn("Flight",
                        $"Fresh-rollout refusal ({site}): pending slot now holds '{pending.TreeName}' " +
                        $"(id={pending.Id ?? "<none>"}), not the refused tree " +
                        $"'{refusedTree?.TreeName ?? "<null>"}' (id={refusedTree?.Id ?? "<none>"}); not discarded");
                    break;
                default:
                    ParsekLog.Verbose("Flight",
                        $"Fresh-rollout refusal ({site}): no pending tree to dispose");
                    break;
            }
            return disposition;
        }

        /// <summary>
        /// Commits a refused pending tree at the refusal (operator ruling 2026-09-26, todo
        /// FRESH-LAUNCH-REFUSED-KEPT-TREE-PENDING) instead of leaving it pending for the next
        /// <see cref="PendingTreeSlot.StashPendingTree"/> to overwrite. It takes the same route
        /// a Limbo stash takes when FLIGHT is left under autoMerge
        /// (<c>ParsekScenario.AutoCommitPendingTreeOutsideFlight</c>'s Limbo-preserving
        /// full-fidelity branch): <see cref="MergeDialog.BuildDefaultVesselDecisions(RecordingTree)"/>
        /// then <see cref="MergeDialog.MergeCommit"/>, which runs CommitPendingTree +
        /// MarkTreeAsApplied, the optimizer, the ledger commit, the crew swap, the
        /// switch-segment / restore-attempt clear and OnTreeCommitted. A resumed copy that
        /// shares its tree id with a committed tree goes through
        /// <see cref="RecordingStore.CommitTree"/>'s same-id replace branch, exactly as a
        /// resumed committed tree does when it is committed at scene exit: the committed
        /// tree slot and each shared recording slot are replaced in place, so no second tree
        /// with that id and no duplicate recording is created. The quicksave is not
        /// refreshed: this commit is automatic, and overwriting the player's quicksave with
        /// no player action would destroy their F5 point. <c>persistent.sfs</c> is written
        /// by the next ordinary save. Returns <paramref name="disposition"/> when the pending
        /// slot no longer holds the tree, else <see cref="FreshRolloutRefusedTreeDisposition.KeepCommitFailed"/>.
        /// </summary>
        internal static FreshRolloutRefusedTreeDisposition CommitFreshRolloutRefusedPendingTree(
            string site,
            RecordingTree pending,
            FreshRolloutRefusedTreeDisposition disposition,
            uint refusedVesselPid,
            string kindDetail)
        {
            string treeId = pending.Id ?? "<none>";
            int total = pending.Recordings?.Count ?? 0;
            PendingTreeState stateAtCommit = RecordingStore.PendingTreeStateValue;
            bool serialized = PendingTreeSlot.PendingTreeSerializedForSave;
            RecordingTree committedTwin = RecordingStore.FindCommittedTreeById(pending.Id);
            int committedTwinRecordings = committedTwin?.Recordings?.Count ?? 0;
            try
            {
                var decisions = MergeDialog.BuildDefaultVesselDecisions(pending);
                int spawnCount = 0;
                foreach (var v in decisions.Values)
                    if (v) spawnCount++;
                ParsekLog.Info("Flight",
                    $"Fresh-rollout refusal ({site}): committing kept tree '{pending.TreeName}' " +
                    $"(id={treeId}, state={stateAtCommit}, recordings={total}, spawnable={spawnCount}, " +
                    $"committedTwinRecordings={(committedTwin != null ? committedTwinRecordings.ToString(CultureInfo.InvariantCulture) : "none")}, " +
                    $"serialized={serialized}, refusedPid={refusedVesselPid}, kind={kindDetail}) - " +
                    "committed at the refusal as a scene exit would, instead of left pending");
                MergeDialog.MergeCommit(pending, decisions, spawnCount,
                    refreshQuicksaveAfterCommit: false);
            }
            catch (Exception ex)
            {
                ParsekLog.Error("Flight",
                    $"Fresh-rollout refusal ({site}): commit of kept tree '{pending.TreeName}' " +
                    $"(id={treeId}) threw {ex.GetType().Name}: {ex.Message}");
            }

            if (ReferenceEquals(RecordingStore.PendingTree, pending))
            {
                ParsekLog.Warn("Flight",
                    $"Fresh-rollout refusal ({site}): kept tree '{pending.TreeName}' (id={treeId}) is " +
                    "still pending after the commit attempt; left pending");
                return FreshRolloutRefusedTreeDisposition.KeepCommitFailed;
            }

            RecordingTree committedNow = RecordingStore.FindCommittedTreeById(pending.Id);
            if (committedNow != null && !ReferenceEquals(committedNow, pending))
            {
                // CommitTree's duplicate-skip: the committed original stays as it was and
                // the copy's post-load content is not in the committed store.
                ParsekLog.Warn("Flight",
                    $"Fresh-rollout refusal ({site}): committed store kept its original tree " +
                    $"'{committedNow.TreeName}' (id={treeId}) over the refused copy " +
                    "(CommitTree duplicate-skip); the copy's post-load content was not committed");
            }
            else
            {
                ParsekLog.Info("Flight",
                    $"Fresh-rollout refusal ({site}): committed kept tree '{pending.TreeName}' " +
                    $"(id={treeId}, {(committedTwin != null ? "replaced committed original" : "added as a committed tree")}, " +
                    $"committedTrees={RecordingStore.CommittedTrees.Count}, " +
                    $"committedRecordings={RecordingStore.CommittedRecordings.Count})");
            }
            return disposition;
        }

        /// <summary>
        /// Undo <see cref="ApplyPreTransitionForVesselSwitch"/> on a stashed tree: put the
        /// recording the stash moved into <c>BackgroundMap</c> back as
        /// <c>ActiveRecordingId</c>, which leaves the tree in exactly the shape the plain
        /// Limbo stash (<see cref="StashActiveTreeAsPendingLimbo"/>) produces. Used when the
        /// vessel-switch restore refuses a fresh rollout, so the refused tree continues on
        /// the name- and launch-guid-gated quickload-restore path instead of staying a
        /// <c>LimboVesselSwitch</c> that a later load would reinstall on whatever vessel is
        /// active. Returns false (tree untouched) when there is no in-memory record of the
        /// move or the tree no longer matches it (outsider-chain stash, a tree reloaded
        /// from disk, or a BackgroundMap entry that changed since).
        /// </summary>
        internal static bool TryRevertPreTransitionForVesselSwitch(
            RecordingTree tree, out string restoredActiveRecordingId)
        {
            restoredActiveRecordingId = null;
            if (tree == null || !string.IsNullOrEmpty(tree.ActiveRecordingId))
                return false;
            string recId = tree.VesselSwitchPreTransitionActiveRecordingId;
            uint pid = tree.VesselSwitchPreTransitionVesselPid;
            if (string.IsNullOrEmpty(recId) || pid == 0 || !tree.Recordings.ContainsKey(recId))
                return false;
            if (!tree.BackgroundMap.TryGetValue(pid, out string mapped) || mapped != recId)
                return false;
            tree.BackgroundMap.Remove(pid);
            tree.ActiveRecordingId = recId;
            tree.VesselSwitchPreTransitionActiveRecordingId = null;
            tree.VesselSwitchPreTransitionVesselPid = 0;
            restoredActiveRecordingId = recId;
            return true;
        }

        /// <summary>
        /// Bug #266: vessel-switch restore coroutine. The pending tree was pre-transitioned
        /// at stash time (recorder flushed, old active recording moved into BackgroundMap,
        /// ActiveRecordingId nulled), so the restore is much simpler than the quickload
        /// path: pop the tree, install as <c>activeTree</c>, attach a fresh
        /// <c>BackgroundRecorder</c>, and inspect the new active vessel:
        /// <list type="bullet">
        ///   <item>If its PID is in <c>tree.BackgroundMap</c> (round-trip — the player
        ///   returned to a vessel that's part of this tree), arm the watcher so the
        ///   tracked recording is only promoted after the first meaningful physical
        ///   modification.</item>
        ///   <item>Otherwise leave <c>recorder = null</c> and arm the same watcher for
        ///   outsider/start-fresh follow-up. The tree remains installed, but focus switch
        ///   alone still does not start recording.</item>
        /// </list>
        /// </summary>
        IEnumerator RestoreActiveTreeFromPendingForVesselSwitch()
        {
            // #267: reentrancy guard — same pattern as RestoreActiveTreeFromPending
            restoringActiveTree = true;
            try
            {
            // NOTE: body intentionally not re-indented to minimize diff
            RecorderStateLog.RecState("RestoreSwitch:start", CaptureRecorderState());
            if (!RecordingStore.HasPendingTree
                || RecordingStore.PendingTreeStateValue != PendingTreeState.LimboVesselSwitch)
            {
                ParsekLog.Verbose("Flight",
                    "RestoreActiveTreeFromPendingForVesselSwitch: no pending-LimboVesselSwitch tree, skipping");
                yield break;
            }

            // The tree this coroutine is about to act on; the fresh-rollout refusal
            // discards only if the pending slot still holds exactly this tree.
            RecordingTree stashedTree = RecordingStore.PendingTree;

            // Wait up to ~3 seconds for FlightGlobals.ActiveVessel to populate.
            // Aligned with the existing quickload-resume coroutine
            // (RestoreActiveTreeFromPending, ~5470) so both restore paths use the
            // same wait budget — easier to reason about and tune in playtest.
            // Unlike the quickload path we don't name-match: we just need a
            // non-null active vessel so we can check whether it's in BackgroundMap.
            const float WaitBudgetSeconds = 3f;
            float deadline = UnityEngine.Time.time + WaitBudgetSeconds;
            while (UnityEngine.Time.time < deadline && FlightGlobals.ActiveVessel == null)
                yield return null;
            bool waitTimedOut = FlightGlobals.ActiveVessel == null;

            // FRESH-LAUNCH-JOINS-RESTORED-COMMITTED-TREE: a vessel-switch reload never
            // lands on a NEW_FROM_FILE / NEW_FROM_CRAFT_NODE rollout, so an active
            // vessel that IS the scene-entry fresh rollout means this FLIGHT->FLIGHT
            // reload was a new launch misread as a switch. Installing the stashed tree
            // here would record the launch as a parentless root inside that tree (a
            // committed tree's clone, when the stash came from a committed-tree
            // restore) and author its later dock as a same-tree merge. Refuse, exactly
            // as the quickload restore refuses a fresh rollout: the tree stays pending
            // and the launch gets its own tree on its first auto-record.
            var candidate = FlightGlobals.ActiveVessel;
            if (candidate != null
                && ShouldRefuseVesselSwitchRestoreForFreshRollout(
                    candidate.persistentId, SceneEntryVesselScope.SceneEntryFreshRolloutVesselPid))
            {
                ParsekLog.Info("Flight",
                    $"RestoreActiveTreeFromPendingForVesselSwitch: refusing to reinstall tree " +
                    $"'{RecordingStore.PendingTree?.TreeName}' " +
                    $"(id={RecordingStore.PendingTree?.Id ?? "<none>"}) for fresh-rollout vessel " +
                    $"'{candidate.vesselName}' pid={candidate.persistentId} - a new launch is not a " +
                    "vessel switch; the launch starts its own tree");
                // The refused tree is discarded (idle resumed committed copy) or committed
                // (every other tree) here, with or without a revertible pre-transition, so
                // nothing is left pending for a later load or stash to reinstall or overwrite.
                if (!IsFreshRolloutRefusedTreeResolved(DisposeFreshRolloutRefusedPendingTree(
                        "vessel-switch-restore", stashedTree, candidate.persistentId)))
                {
                    // A guarded tree stays pending. Make the refusal durable: a
                    // LimboVesselSwitch tree is written as an isActive node and a later load
                    // would route it back here, where the fresh-rollout pid is no longer
                    // captured, and reinstall it on the launched craft. Undo the stash's
                    // pre-transition so the tree is a plain Limbo stash that the guid-gated
                    // quickload restore owns.
                    if (TryRevertPreTransitionForVesselSwitch(
                            RecordingStore.PendingTree, out string restoredRecId))
                    {
                        PendingTreeSlot.ConvertPendingVesselSwitchStashToLimbo(
                            "fresh-rollout refusal restored activeRecId=" + restoredRecId);
                    }
                    else
                    {
                        ParsekLog.Warn("Flight",
                            "RestoreActiveTreeFromPendingForVesselSwitch: refused tree has no " +
                            "revertible pre-transition (outsider-chain stash or reloaded tree) - it " +
                            "stays LimboVesselSwitch and is protected for this scene only " +
                            "(todo FRESH-LAUNCH-REFUSED-KEPT-TREE-PENDING)");
                    }
                }
                yield break;
            }

            // Pop the tree (non-destructive — preserves sidecar files) and install
            // as the live active tree.
            activeTree = PendingTreeSlot.PopPendingTree();
            if (activeTree == null)
            {
                ParsekLog.Warn("Flight",
                    "RestoreActiveTreeFromPendingForVesselSwitch: PopPendingTree returned null after " +
                    "we verified the tree exists");
                yield break;
            }
            // PopPendingTree just emptied the main pending slot; a saved pending
            // tree from the same save can now move back unless another path filled it.
            PendingTreeSlot.PromoteSavedPendingTreeAfterActiveRestore(
                "RestoreActiveTreeFromPendingForVesselSwitch");

            // Re-attach the BackgroundRecorder for the tree (the previous instance was
            // shut down in FinalizeTreeOnSceneChange before the scene reload).
            EnsureBackgroundRecorderAttached("RestoreActiveTreeFromPendingForVesselSwitch");

            // Inspect the new active vessel for promotion.
            var newActive = FlightGlobals.ActiveVessel;
            if (newActive == null)
            {
                // Wait budget exhausted with no ActiveVessel: log Warn so playtest
                // logs surface the missed arming opportunity. The tree is reinstalled but
                // any potential round-trip watcher cannot be tested at this point.
                // Subsequent OnVesselSwitchComplete events from the live game
                // will still drive the in-session watcher path.
                ParsekLog.Warn("Flight",
                    $"RestoreActiveTreeFromPendingForVesselSwitch: tree '{activeTree.TreeName}' reinstalled, " +
                    $"but no active vessel resolved within {WaitBudgetSeconds:F0}s wait window " +
                    $"(timedOut={waitTimedOut}) — outsider state, recorder stays null. Round-trip " +
                    "arming (if any) will be missed; subsequent OnVesselSwitchComplete will recover.");
            }
            else
            {
                // PID-stability assumption: TS-mediated FLIGHT→FLIGHT vessel switches
                // do NOT regenerate persistentId. The protoVessel persists through the
                // scene reload with the same PID it had at stash time, so we can look
                // up BackgroundMap directly without the PID-remap dance the quickload
                // path needs (RestoreActiveTreeFromPending ~5520-5535). Verified in
                // logs/2026-04-09_f5f9_verify/KSP.log: pid 2708531065 survives the
                // reload unchanged. If a future KSP version regenerates PIDs on TS
                // switch, the round-trip arming will silently fail and the player
                // will land in the outsider branch — visible as a missing
                // post-switch armed log line.
                uint newPid = newActive.persistentId;
                if (activeTree.BackgroundMap.TryGetValue(newPid, out string bgRecId))
                {
                    // Round-trip case: the player returned to a vessel that this tree
                    // is already tracking, but post-switch auto-record now arms and waits
                    // for the first meaningful modification instead of promoting on focus
                    // change alone.
                    ArmPostSwitchAutoRecord(
                        newActive,
                        "restore completed on tracked tree member while idle",
                        trackedInActiveTree: true,
                        freshStartParentRecordingId: null);
                    ParsekLog.Info("Flight",
                        $"RestoreActiveTreeFromPendingForVesselSwitch: armed tracked vessel '{newActive.vesselName}' " +
                        $"(pid={newPid}) from BackgroundMap — waiting for first modification after TS / scene-reload switch");
                }
                else
                {
                    // Outsider case: the new active vessel has no recording context in
                    // this tree. recorder stays null. Future switches back to a tree
                    // member will arm the tracked path and later promote on trigger.
                    ParsekLog.Info("Flight",
                        $"RestoreActiveTreeFromPendingForVesselSwitch: tree '{activeTree.TreeName}' reinstalled " +
                        $"as outsider for active vessel '{newActive.vesselName}' (pid={newPid}, not in " +
                        $"BackgroundMap) — recorder stays null until a post-switch trigger starts fresh or a later tracked switch promotes on trigger");
                    ArmPostSwitchAutoRecord(
                        newActive,
                        "restore completed on outsider while idle",
                        trackedInActiveTree: false,
                        freshStartParentRecordingId: null);
                }
            }

            RecorderStateLog.RecState("RestoreSwitch:after-install", CaptureRecorderState());
            } // try
            finally
            {
                restoringActiveTree = false;
            }

            // M1 (PR #876 final review): dispatch any deferred consume now that
            // the restored tree is live. DispatchConsumeIntentIfArmed at the
            // top of OnFlightReady skips when ScheduleActiveTreeRestoreOnFlightReady
            // is non-None to avoid racing the restore. Re-invoking here picks
            // up an intent that survived the F5 -> reload round-trip and
            // attaches the new switch segment to the just-restored tree.
            DispatchConsumeIntentIfArmed();
        }

        /// <summary>
        /// Flight→Flight stash path (Bug C fix): stashes the active tree into the
        /// pending-Limbo slot WITHOUT finalizing terminal state. ParsekScenario.OnLoad
        /// decides, based on revert vs quickload detection, whether to finalize-and-commit
        /// (real revert) or restore-and-resume (quickload / vessel switch).
        ///
        /// <para>Flushes recorder buffers into the tree so no trajectory data is lost.
        /// Closes background orbit segments. Aborts any pending split (continuation
        /// window state cannot survive a scene reload cleanly — edge case #4 in the
        /// design doc).</para>
        /// </summary>
        void StashActiveTreeAsPendingLimbo(double commitUT)
        {
            if (activeTree == null) return;

            ParsekLog.Info("Flight",
                $"StashActiveTreeAsPendingLimbo: stashing tree '{activeTree.TreeName}' at UT={commitUT:F1} " +
                $"(activeRecId={activeTree.ActiveRecordingId ?? "<null>"})");
            RecorderStateLog.RecState("StashTreeLimbo:entry", CaptureRecorderState());

            // Flush active recorder's buffered points/events into the active tree's
            // current recording, without setting terminal state. FinalizeRecorderForLimbo
            // is a minimal version of FinalizeTreeRecordings that stops at flushing.
            if (recorder != null)
            {
                if (recorder.IsRecording)
                {
                    if (recorder.IsBackgrounded)
                        recorder.IsRecording = false;
                    else
                        recorder.ForceStop();
                }
                FlushRecorderToTreeRecording(recorder, activeTree);

                // Copy rewind save to the root recording so the R button works after revert.
                CopyRewindSaveToRoot(activeTree, recorder,
                    logTag: "StashTreeLimbo");
            }

            // Close background recorder orbit segments and flush their data into tree
            // recordings. Does NOT set terminal state.
            if (backgroundRecorder != null)
                backgroundRecorder.FinalizeAllForCommit(commitUT);

            // Abort any pending split (undock / EVA / joint break continuation window).
            // The continuation state cannot survive a scene reload cleanly — if the
            // split was real, the debris vessel exists in the saved scene and will be
            // rediscovered via normal OnVesselCreate events on resume.
            if (pendingSplitRecorder != null)
            {
                ParsekLog.Info("Flight",
                    $"StashActiveTreeAsPendingLimbo: discarding pendingSplitRecorder " +
                    $"(recId={pendingSplitRecorder.CaptureAtStop?.RecordingId ?? "<null>"}) — " +
                    "continuation window cannot survive scene reload");
                pendingSplitRecorder = null;
                pendingSplitInProgress = false;
            }

            // #268: Belt-and-braces snapshot capture while vessels are still alive.
            // FinalizePendingLimboTreeForRevert relies on FindVesselByPid at OnLoad time
            // to re-snapshot via the #289 block. If vessels are unloaded before that
            // (edge case: late OnLoad timing, non-FLIGHT destination), this pre-capture
            // ensures the merge dialog can still offer respawn for stable-terminal leaves.
            // Only fills null snapshots — does NOT overwrite existing ones.
            //
            // Bug #271: also capture for the active recording even if non-leaf. In
            // always-tree mode with breakup-continuous design, the active recording
            // may have ChildBranchPointId set but still needs a snapshot for spawn-at-end.
            int snapshotsCaptured = 0;
            string activeRecId = activeTree.ActiveRecordingId;
            foreach (var kvp in activeTree.Recordings)
            {
                var rec = kvp.Value;
                bool isActiveRec = rec.RecordingId == activeRecId;
                if (rec.ChildBranchPointId != null && !isActiveRec
                    && !RecordingTree.ChildBranchLeavesVesselLineOpen(
                        rec, activeTree.Recordings, activeTree.BranchPoints))
                    continue;
                if (rec.VesselPersistentId == 0) continue;

                Vessel v = FlightRecorder.FindVesselByPid(rec.VesselPersistentId);
                if (v != null && rec.VesselSnapshot == null)
                {
                    ConfigNode freshSnapshot = FlightRecorder.TryBackupSnapshot(v);
                    if (freshSnapshot != null)
                    {
                        rec.VesselSnapshot = freshSnapshot;
                        if (rec.GhostVisualSnapshot == null)
                            rec.GhostVisualSnapshot = freshSnapshot.CreateCopy();
                        snapshotsCaptured++;
                    }
                }
            }
            if (snapshotsCaptured > 0)
            {
                ParsekLog.Info("Flight",
                    $"StashActiveTreeAsPendingLimbo: captured {snapshotsCaptured} snapshot(s) " +
                    $"for null-snapshot leaves");
            }

            // Stash as pending-Limbo. NO FinalizeTreeRecordings — terminal state deferred
            // to OnLoad's dispatch once it knows whether this is a revert or a quickload.
            PendingTreeSlot.StashPendingTree(activeTree, PendingTreeState.Limbo);

            ParsekLog.Info("Flight",
                $"StashActiveTreeAsPendingLimbo: stashed tree '{activeTree.TreeName}' as Limbo " +
                $"({activeTree.Recordings.Count} recording(s)) — OnLoad will dispatch");
            RecorderStateLog.RecState("StashTreeLimbo:post", CaptureRecorderState());
        }

        /// <summary>
        /// Bug #266: returns true if the active tree can safely be pre-transitioned
        /// for a vessel-switch stash. Bails out on the same conditions that the
        /// in-session <see cref="OnVesselSwitchComplete"/> path bails on
        /// (<c>pendingTreeDockMerge</c>, pending split / chain-to-vessel windows),
        /// because those states have continuation logic that runs in <c>Update()</c>
        /// after the switch completes — and <c>Update()</c> never runs across a
        /// scene reload. Falling back to the legacy Limbo stash on these is the
        /// safer option (the safety net in <c>OnLoad</c> will finalize, matching
        /// pre-#266 behavior for these edge cases).
        /// </summary>
        bool CanPreTransitionForVesselSwitch()
        {
            if (activeTree == null) return false;
            if (pendingTreeDockMerge) return false;
            if (pendingSplitRecorder != null || pendingSplitInProgress) return false;
            if (recorder != null && recorder.ChainToVesselPending) return false;
            return true;
        }

        /// <summary>
        /// Pure decision for <see cref="DeferredJointBreakCheck"/>: true when a split
        /// recorder must resume on the vessel it was recording rather than on the active
        /// vessel, because focus moved off the recorded vessel (which still exists) while
        /// the recorder was stopped at the split. Resuming on the active vessel would record
        /// a different vessel into this recording (todo
        /// VN8-FOCUS-SWITCH-SPLIT-PARENT-LEG-RECORDS-NEW-VESSEL).
        /// </summary>
        internal static bool ShouldResumeSplitOnRecordedVessel(
            uint recordedVesselPid,
            bool recordedVesselAlive,
            uint activeVesselPid)
        {
            return recordedVesselPid != 0u
                && recordedVesselAlive
                && activeVesselPid != 0u
                && activeVesselPid != recordedVesselPid;
        }

        /// <summary>
        /// Pure: the pid of the vessel that survives a split as the parent recording's
        /// vessel. That is the vessel the active recorder records, which is not the active
        /// vessel when focus moved to a split child; the active vessel only stands in when
        /// no recorder is bound.
        /// </summary>
        internal static uint ResolveBreakupParentVesselPid(uint recorderVesselPid, uint activeVesselPid)
        {
            return recorderVesselPid != 0u ? recorderVesselPid : activeVesselPid;
        }

        /// <summary>
        /// Pure guard for the Update() safety net that replays missed vessel switches.
        /// Runs only when tree state is active and stable enough that the normal
        /// <see cref="OnVesselSwitchComplete"/> path is safe to reuse.
        /// </summary>
        internal static bool ShouldRecoverMissedVesselSwitch(
            bool isRestoringActiveTree,
            bool hasActiveTree,
            bool pendingTreeDockMerge,
            bool hasPendingSplitRecorder,
            bool pendingSplitInProgress,
            bool hasRecorder,
            bool recorderIsRecording,
            bool recorderChainToVesselPending,
            uint recorderVesselPid,
            uint activeVesselPid,
            bool activeVesselTrackedInBackground,
            bool activeVesselAlreadyArmedForPostSwitchAutoRecord)
        {
            if (isRestoringActiveTree) return false;
            if (!hasActiveTree) return false;
            if (pendingTreeDockMerge) return false;
            if (hasPendingSplitRecorder || pendingSplitInProgress) return false;
            if (activeVesselPid == 0) return false;

            if (hasRecorder)
            {
                if (!recorderIsRecording) return false;
                if (recorderChainToVesselPending) return false;
                if (recorderVesselPid == 0) return false;
                return recorderVesselPid != activeVesselPid;
            }

            return activeVesselTrackedInBackground
                && !activeVesselAlreadyArmedForPostSwitchAutoRecord;
        }

        /// <summary>
        /// Pure decision for the OnFlightReady fallback that opens the tree
        /// merge dialog. Returns <c>true</c> when the dialog should be shown:
        /// a pending tree exists, no restore coroutine owns it (#293), and
        /// no Re-Fly attempt owns it (in-place continuation marker or
        /// pending invoke). The Re-Fly guard prevents the fallback from
        /// firing immediately after an initial Re-Fly invocation or a
        /// Retry-from-Rewind-Point — those cases have a fresh fork attached
        /// to the pending tree, and the merge decision belongs to the
        /// scene-exit path once the attempt actually finishes.
        ///
        /// <para>
        /// Placeholder-mode Re-Fly markers (PID changed or chain orphaned
        /// at <c>AtomicMarkerWrite</c> time) deliberately do NOT skip — the
        /// recorder-restore carve-out cannot bind a recorder for that
        /// marker shape, so the merge-dialog fallback is the player's only
        /// recovery path. The caller passes
        /// <c>ParsekScenario.IsReFlyInPlaceContinuationActive() || RewindInvokeContext.Pending</c>
        /// to <paramref name="reFlyInPlaceContinuationActive"/>; the
        /// parameter name reflects the dominant case (the brief pending
        /// invoke window is a corner included for flicker safety).
        /// </para>
        /// </summary>
        internal static bool ShouldShowOnFlightReadyMergeDialog(
            bool hasPendingTree,
            bool restoringActiveTree,
            bool reFlyInPlaceContinuationActive)
        {
            return hasPendingTree
                && !restoringActiveTree
                && !reFlyInPlaceContinuationActive;
        }

        /// <summary>
        /// Pure decision: should OnFlightReady upgrade
        /// <paramref name="restoreMode"/> from None to Quickload because a
        /// Re-Fly retry left a freshly-attached fork on a Finalized pending
        /// tree that no other code path will bind a recorder to.
        ///
        /// <para>
        /// The full Re-Fly retry path: the previous attempt was finalized
        /// post-destruction (via <c>ShowPostDestructionTreeMergeDialog</c>
        /// or <c>CommitTreeSceneExit</c>) before the player clicked Retry
        /// from Rewind Point. <c>TryRestoreActiveTreeNode</c> sees the
        /// in-memory Finalized tree and skips the .sfs replacement (#290d),
        /// so no Quickload schedule lands on <see cref="ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady"/>.
        /// <c>AtomicMarkerWrite</c> still attaches the new fork to that
        /// tree and sets <see cref="ParsekScenario.ActiveReFlySessionMarker"/>,
        /// but without a Quickload schedule the live vessel has no recorder
        /// bound to the new fork — the OnFlightReady merge-dialog skip then
        /// hides the symptom (no dialog) without resolving the underlying
        /// state.
        /// </para>
        ///
        /// <para>
        /// The Re-Fly check is the stricter
        /// <see cref="ParsekScenario.IsReFlyInPlaceContinuationActive"/>
        /// (marker set AND <c>InPlaceContinuation == true</c>), not the
        /// broader <see cref="ParsekScenario.IsReFlySessionActiveForQuickloadDiscard"/>.
        /// In placeholder mode (PID changed or chain orphaned, see
        /// <c>RewindInvoker.AtomicMarkerWrite</c> line 1096-1099), the
        /// marker swap in the restore coroutine returns
        /// <c>placeholder-pattern</c> with no swap; the wait loop then
        /// targets the pre-rewind PID, times out at 3 s, and yield-breaks.
        /// Firing the carve-out in that mode leaves the player stuck with
        /// no recorder AND no dialog. Refusing to upgrade keeps the original
        /// merge-dialog fallback as the recovery path.
        /// </para>
        /// </summary>
        internal static bool ShouldUpgradeRestoreModeForReFlyRetry(
            ParsekScenario.ActiveTreeRestoreMode restoreMode,
            bool hasPendingTree,
            bool pendingTreeIsFinalized,
            bool reFlyInPlaceContinuationActive)
        {
            return restoreMode == ParsekScenario.ActiveTreeRestoreMode.None
                && hasPendingTree
                && pendingTreeIsFinalized
                && reFlyInPlaceContinuationActive;
        }

        /// <summary>
        /// Pure decision: should <see cref="RestoreActiveTreeFromPending"/>
        /// accept a Finalized pending tree (its usual gate is Limbo only)
        /// because the active Re-Fly session needs the recorder bound to
        /// the freshly-attached fork. Paired with
        /// <see cref="ShouldUpgradeRestoreModeForReFlyRetry"/> in the
        /// OnFlightReady dispatch — both gate on
        /// <see cref="ParsekScenario.IsReFlyInPlaceContinuationActive"/>
        /// (marker + InPlaceContinuation flag) rather than the broader
        /// invoke-pending helper, because the coroutine's marker swap
        /// (<see cref="ReFlySessionMarker.ResolveInPlaceContinuationTarget"/>)
        /// is the load-bearing path that redirects the wait target to the
        /// new fork's vessel and PID. Placeholder-mode markers cannot
        /// satisfy that swap.
        /// </summary>
        internal static bool ShouldAcceptFinalizedPendingTreeForReFlyRetry(
            bool hasPendingTree,
            bool pendingTreeIsFinalized,
            bool reFlyInPlaceContinuationActive)
        {
            return hasPendingTree
                && pendingTreeIsFinalized
                && reFlyInPlaceContinuationActive;
        }

        /// <summary>
        /// Asks the committed-spawned restore (the 1 Hz Update retry and every other
        /// <see cref="TryRestoreCommittedTreeForSpawnedActiveVessel"/> entry point) to
        /// leave the CURRENT active vessel idle. Called only by the M-A2
        /// <c>DiscardTree</c> test-command verb: without it, discarding the tree a
        /// recorded-fixture boot restored for its spawned vessel is undone within a second
        /// (the committed original is still in committed storage, so the retry re-clones
        /// it), and a lane's later <c>StartRecording</c> is refused. Player discard paths
        /// never call this: each is followed by a vessel switch or a scene change, and a
        /// live committed vessel re-adopting its tree is the intended product rule.
        /// </summary>
        internal void SuppressCommittedSpawnedRestoreForActiveVessel(string reason)
        {
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            BeginCommittedSpawnedRestoreSuppression(
                ref committedSpawnedRestoreSuppressedPid,
                activeVessel != null ? activeVessel.persistentId : 0u,
                activeVessel != null ? activeVessel.vesselName : null,
                hasActiveTree: activeTree != null,
                hasRecorder: recorder != null,
                reason: reason);
        }

        /// <summary>
        /// Pure: true when the committed-spawned restore must skip
        /// <paramref name="activeVesselPid"/> because a DiscardTree suppression names it.
        /// A zero pid on either side never matches.
        /// </summary>
        internal static bool IsCommittedSpawnedRestoreSuppressedForVessel(
            uint suppressedPid, uint activeVesselPid)
        {
            return suppressedPid != 0
                && activeVesselPid != 0
                && suppressedPid == activeVesselPid;
        }

        /// <summary>
        /// Pure: why an armed suppression ends, or null to keep it. It ends once anything
        /// owns a live tree or recorder (a StartRecording, a restore coroutine, an
        /// adoption), because the idle request is then over, and once the active vessel is
        /// a DIFFERENT vessel (an EVA, a switch), so a later legitimate restore of another
        /// vessel, or of this one after switching back, still runs. A zero active pid is a
        /// transient mid-switch read and keeps the suppression.
        /// </summary>
        internal static string ResolveCommittedSpawnedRestoreSuppressionClearReason(
            uint suppressedPid, uint activeVesselPid, bool hasActiveTree, bool hasRecorder)
        {
            if (suppressedPid == 0) return null;
            if (hasActiveTree || hasRecorder) return "live-tree-or-recorder";
            if (activeVesselPid != 0 && activeVesselPid != suppressedPid)
                return "active-vessel-changed";
            return null;
        }

        /// <summary>
        /// Arms the suppression for <paramref name="activeVesselPid"/> and logs it. Refuses
        /// (and logs why) when there is no active vessel or something still owns a live
        /// tree or recorder, since the per-frame clear would drop it on the next frame.
        /// Returns whether it armed.
        /// </summary>
        internal static bool BeginCommittedSpawnedRestoreSuppression(
            ref uint suppressedPid,
            uint activeVesselPid,
            string activeVesselName,
            bool hasActiveTree,
            bool hasRecorder,
            string reason)
        {
            if (activeVesselPid == 0)
            {
                ParsekLog.Info("Flight",
                    $"CommittedSpawnedRestoreSuppression: not armed reason={reason} " +
                    "(no active vessel)");
                return false;
            }
            if (hasActiveTree || hasRecorder)
            {
                ParsekLog.Info("Flight",
                    $"CommittedSpawnedRestoreSuppression: not armed reason={reason} " +
                    $"pid={activeVesselPid} (live tree={hasActiveTree} recorder={hasRecorder})");
                return false;
            }
            uint previous = suppressedPid;
            suppressedPid = activeVesselPid;
            ParsekLog.Info("Flight",
                $"CommittedSpawnedRestoreSuppression: armed pid={activeVesselPid} " +
                $"vessel='{activeVesselName ?? "<null>"}' reason={reason} " +
                $"previousPid={previous} - committed-tree restore skips this vessel until a " +
                "live tree or recorder exists, the active vessel changes, or the scene ends");
            return true;
        }

        /// <summary>
        /// Per-frame clear for the suppression (no-op when none is armed). Logs once when
        /// it clears. Returns the clear reason, or null when the suppression stays.
        /// </summary>
        internal static string TickCommittedSpawnedRestoreSuppression(
            ref uint suppressedPid,
            uint activeVesselPid,
            bool hasActiveTree,
            bool hasRecorder)
        {
            string clearReason = ResolveCommittedSpawnedRestoreSuppressionClearReason(
                suppressedPid, activeVesselPid, hasActiveTree, hasRecorder);
            if (clearReason == null) return null;
            ParsekLog.Info("Flight",
                $"CommittedSpawnedRestoreSuppression: cleared pid={suppressedPid} " +
                $"reason={clearReason} activePid={activeVesselPid}");
            suppressedPid = 0;
            return clearReason;
        }

        internal static bool ShouldAttemptCommittedSpawnedRestoreInUpdate(
            bool hasActiveTree,
            bool hasRecorder,
            bool isRestoringActiveTree,
            bool hasPendingTree,
            ParsekScenario.ActiveTreeRestoreMode restoreMode,
            float currentUnscaledTime,
            float nextRetryAt)
        {
            if (hasActiveTree || hasRecorder) return false;
            if (isRestoringActiveTree) return false;
            if (hasPendingTree) return false;
            if (restoreMode != ParsekScenario.ActiveTreeRestoreMode.None) return false;
            return currentUnscaledTime >= nextRetryAt;
        }

        // KSP's craft-file-derived persistentId is deterministic enough that
        // re-launching the same .craft tends to recycle the previous mission's pid,
        // and Re-Fly merges preserve the prior SpawnedVesselPersistentId
        // (RecordingStore.PreserveLiveRuntimeFieldsOnReplace). Without this gate,
        // a fresh VAB/SPH launch would silently attach to the prior committed tree
        // and merge two distinct missions under one auto-generated group.
        //
        // The guard has two parts:
        //   1. IsFreshLaunchStartupBehaviour gates the Start-time capture of
        //      freshRolloutVesselPid on FlightDriver.StartupBehaviour. KSP sets
        //      StartupBehaviour by the editor's Launch handler / save-loader /
        //      revert path before the FLIGHT scene transitions in, and the value
        //      is stable for the entire scene (no missionTime expiry, no event
        //      race). NEW_FROM_FILE and NEW_FROM_CRAFT_NODE are both fresh
        //      rollouts; RESUME_SAVED_FILE and RESUME_SAVED_CACHE leave the
        //      capture pid at 0 so this guard does nothing.
        //   2. ShouldSkipCommittedTreeRestoreForFreshLaunch is a pure pid-match
        //      against the captured rollout pid. This identity component is what
        //      lets a mid-scene switch to another already-spawned committed
        //      vessel (with a different pid) still resume its recording.
        // Verified against the repro at logs/2026-05-13_1850_kerbal-x-merge-bug:
        // KSP.log line 53466-53467 shows the VAB craft loader running ("Loading
        // ship from file: ...Auto-Saved Ship.craft"), which is FlightDriver's
        // NEW_FROM_FILE branch (Assembly-CSharp/FlightDriver.cs:334-345).
        internal static bool IsFreshLaunchStartupBehaviour(
            FlightDriver.StartupBehaviours startupBehaviour)
        {
            return startupBehaviour == FlightDriver.StartupBehaviours.NEW_FROM_FILE
                || startupBehaviour == FlightDriver.StartupBehaviours.NEW_FROM_CRAFT_NODE;
        }

        internal static bool ShouldSkipCommittedTreeRestoreForFreshLaunch(
            uint activeVesselPid, uint freshRolloutVesselPid)
        {
            return activeVesselPid != 0 && activeVesselPid == freshRolloutVesselPid;
        }

        // Bug (post-#876 playtest 2026-05-17): the kerbal-x-grouping-bug repro.
        // The matcher historically only recognised Parsek-spawned recordings
        // (VesselSpawned=true with SpawnedVesselPersistentId equal to the live
        // PID). Fresh-launched vessels (NEW_FROM_FILE etc.) commit recordings
        // with VesselSpawned=false and SpawnedVesselPersistentId=0 but their
        // VesselPersistentId equals the live PID of the vessel currently
        // sitting in the save. When the player Map-Switch-To'd a far Kerbal X
        // (committed mission, never Parsek-spawned), TryFindCommittedTree-
        // MatchingVessel's broader gate routed into the committed-clone
        // branch but this helper rejected the recording, so the consume path
        // fell through to StartStandaloneContinuationSegment and authored a
        // brand-new "Kerbal X" tree disjoint from the original mission tree.
        // The new tree got its own auto-group, so the segment appeared
        // outside the mission group in the UI.
        //
        // The fix: also match recordings whose VesselPersistentId equals the
        // live PID when the recording was not Parsek-spawned. ResolveLive-
        // TreeRecordingPidForRestore already falls back to VesselPersistentId
        // when SpawnedVesselPersistentId is zero, and PrepareCommittedTree-
        // RestoreForSpawnedVessel uses that helper, so the downstream
        // restore path resumes the live recording correctly. The clear-
        // prior-spawn-flags block in TryTakeCommittedTreeForSpawnedVessel-
        // Restore is gated on VesselSpawned || SpawnedVesselPersistentId!=0,
        // so the direct-PID match leaves the recording's identity fields
        // untouched.
        internal static bool TryFindCommittedTreeForSpawnedVessel(
            IReadOnlyList<RecordingTree> committedTrees,
            uint activeVesselPid,
            out RecordingTree tree,
            out string recordingId,
            string activeVesselGuid = null,
            double liveUT = double.NaN)
        {
            tree = null;
            recordingId = null;
            if (committedTrees == null || activeVesselPid == 0)
                return false;

            int refusedOtherLaunchStamps = 0;
            int refusedAheadOfClock = 0;
            for (int t = committedTrees.Count - 1; t >= 0; t--)
            {
                RecordingTree candidate = committedTrees[t];
                if (candidate?.Recordings == null || candidate.Recordings.Count == 0)
                    continue;

                Recording bestMatch = null;
                foreach (Recording rec in candidate.Recordings.Values)
                {
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                        continue;

                    // A genuine Parsek spawn pid is KSP-unique, but an adoption stamp is the
                    // craft-baked pid, so the spawn match goes through the launch-identity gate.
                    bool spawnedMatch = IsCommittedSpawnStampMatch(rec, activeVesselPid, activeVesselGuid);
                    if (!spawnedMatch && IsOtherLaunchSpawnStamp(rec, activeVesselPid, activeVesselGuid))
                        refusedOtherLaunchStamps++;
                    // #976-class: the direct VesselPersistentId match is the craft-baked pid, which
                    // a relaunch of the same craft reuses (notably on RESUME/quickload where the
                    // editor-only fresh-rollout guard above is inactive). Require the same launch
                    // (guid); a null/unknown guid falls back to today's pid-only behavior.
                    bool directMatch = !rec.VesselSpawned
                        && rec.SpawnedVesselPersistentId == 0
                        && rec.VesselPersistentId != 0
                        && rec.VesselPersistentId == activeVesselPid
                        && VesselLaunchIdentity.LiveVesselIsRecordedLaunch(rec, activeVesselPid, activeVesselGuid);
                    if (!spawnedMatch && !directMatch)
                        continue;

                    if (!IsCommittedSpawnedRecordingRestorable(candidate, rec))
                        continue;

                    if (IsCommittedRecordingAheadOfLiveClock(rec, liveUT))
                    {
                        refusedAheadOfClock++;
                        continue;
                    }

                    if (bestMatch == null
                        || IsPreferredCommittedSpawnedRestoreCandidate(candidate, rec, bestMatch))
                    {
                        bestMatch = rec;
                    }
                }

                if (bestMatch != null)
                {
                    tree = candidate;
                    recordingId = bestMatch.RecordingId;
                    LogRefusedOtherLaunchSpawnStamps(
                        "TryFindCommittedTreeForSpawnedVessel", activeVesselPid, activeVesselGuid,
                        refusedOtherLaunchStamps);
                    LogRefusedAheadOfClockRestores(activeVesselPid, liveUT, refusedAheadOfClock);
                    return true;
                }
            }

            LogRefusedOtherLaunchSpawnStamps(
                "TryFindCommittedTreeForSpawnedVessel", activeVesselPid, activeVesselGuid,
                refusedOtherLaunchStamps);
            LogRefusedAheadOfClockRestores(activeVesselPid, liveUT, refusedAheadOfClock);
            return false;
        }

        /// <summary>
        /// Slack for <see cref="IsCommittedRecordingAheadOfLiveClock"/>: a save written in the
        /// frame that ended the recording reads the same UT up to float noise.
        /// </summary>
        internal const double CommittedRestoreAheadOfClockToleranceSeconds = 0.01;

        /// <summary>
        /// Pure: true when <paramref name="rec"/> already holds recorded data later than
        /// <paramref name="liveUT"/>, so the live vessel has not yet reached the end of the
        /// recording it would continue. Resuming there writes samples behind the recording's
        /// own end and double-covers it (VN-5: Revert to Launch put the pad craft back at
        /// UT 25.64 and it resumed its committed pad recording, which ran to 26.88, at 25.68).
        /// Measured on recorded data only (points, track sections, non-predicted orbit
        /// segments): a predicted tail is extrapolation, not something the vessel did. A NaN
        /// clock (headless callers) never refuses.
        /// </summary>
        internal static bool IsCommittedRecordingAheadOfLiveClock(Recording rec, double liveUT)
        {
            if (rec == null || double.IsNaN(liveUT) || double.IsInfinity(liveUT))
                return false;
            if (!TryGetLatestRecordedUT(rec, out double latestUT))
                return false;
            return latestUT > liveUT + CommittedRestoreAheadOfClockToleranceSeconds;
        }

        /// <summary>
        /// Pure: the latest UT a recording holds recorded (not predicted) data for.
        /// </summary>
        internal static bool TryGetLatestRecordedUT(Recording rec, out double latestUT)
        {
            latestUT = double.NegativeInfinity;
            bool found = false;
            if (rec.Points != null && rec.Points.Count > 0)
            {
                latestUT = rec.Points[rec.Points.Count - 1].ut;
                found = true;
            }
            if (rec.TrackSections != null)
            {
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    // A section with no samples of its own (flat-fallback) is metadata: a
                    // frameless tail trim leaves its endUT at the trim cut, past the last
                    // flat point, and the flat points already carry its data.
                    if (!TrackSectionHoldsRecordedSamples(rec.TrackSections[i]))
                        continue;
                    double end = rec.TrackSections[i].endUT;
                    if (double.IsNaN(end) || double.IsInfinity(end))
                        continue;
                    if (!found || end > latestUT)
                        latestUT = end;
                    found = true;
                }
            }
            if (rec.OrbitSegments != null)
            {
                for (int i = 0; i < rec.OrbitSegments.Count; i++)
                {
                    OrbitSegment seg = rec.OrbitSegments[i];
                    if (seg.isPredicted || double.IsNaN(seg.endUT) || double.IsInfinity(seg.endUT))
                        continue;
                    if (!found || seg.endUT > latestUT)
                        latestUT = seg.endUT;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>
        /// Pure: true when a track section carries recorded samples of its own (frames,
        /// body-fixed frames or a non-predicted orbit checkpoint).
        /// </summary>
        internal static bool TrackSectionHoldsRecordedSamples(TrackSection section)
        {
            if (section.frames != null && section.frames.Count > 0)
                return true;
            if (section.bodyFixedFrames != null && section.bodyFixedFrames.Count > 0)
                return true;
            if (section.checkpoints != null)
            {
                for (int i = 0; i < section.checkpoints.Count; i++)
                {
                    if (!section.checkpoints[i].isPredicted)
                        return true;
                }
            }
            return false;
        }

        private static void LogRefusedAheadOfClockRestores(uint activeVesselPid, double liveUT, int refused)
        {
            if (refused <= 0)
                return;
            // Re-reached by the 1 Hz Update retry until the suppression the caller arms
            // takes over: rate-limited per pid.
            ParsekLog.VerboseRateLimited("Flight",
                "committed-restore-ahead-of-clock-" + activeVesselPid.ToString(CultureInfo.InvariantCulture),
                $"TryFindCommittedTreeForSpawnedVessel: refused {refused.ToString(CultureInfo.InvariantCulture)} " +
                $"committed recording(s) for pid={activeVesselPid.ToString(CultureInfo.InvariantCulture)} " +
                $"whose recorded data runs past the live clock " +
                $"ut={liveUT.ToString("F2", CultureInfo.InvariantCulture)} (the vessel is behind its own " +
                "recorded future; resuming would record over it)");
        }

        internal static bool TryTakeCommittedTreeForSpawnedVesselRestore(
            uint activeVesselPid,
            out RecordingTree tree,
            out string recordingId,
            out CommittedSpawnedVesselRestoreAction action,
            string activeVesselGuid = null,
            double liveUT = double.NaN)
        {
            tree = null;
            recordingId = null;
            action = CommittedSpawnedVesselRestoreAction.None;

            if (!TryFindCommittedTreeForSpawnedVessel(
                    RecordingStore.CommittedTrees,
                    activeVesselPid,
                    out RecordingTree committedTree,
                    out string targetRecordingId,
                    activeVesselGuid,
                    liveUT))
            {
                return false;
            }

            RecordingTree liveTree = RecordingTree.DeepClone(committedTree);
            if (liveTree == null)
            {
                ParsekLog.Warn("Flight",
                    $"TryTakeCommittedTreeForSpawnedVesselRestore: matched tree '{committedTree.TreeName}' " +
                    $"recording '{targetRecordingId}' for pid={activeVesselPid}, but could not clone the tree");
                return false;
            }

            CommittedSpawnedVesselRestoreAction preparedAction =
                PrepareCommittedTreeRestoreForSpawnedVessel(
                    liveTree,
                    targetRecordingId,
                    activeVesselPid);
            if (preparedAction == CommittedSpawnedVesselRestoreAction.None)
            {
                ParsekLog.Warn("Flight",
                    $"TryTakeCommittedTreeForSpawnedVesselRestore: matched tree '{committedTree.TreeName}' " +
                    $"recording '{targetRecordingId}' for pid={activeVesselPid}, but could not prepare a restore action");
                return false;
            }

            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(
                committedTree,
                "TryTakeCommittedTreeForSpawnedVesselRestore copy-on-write");
            // A save made inside an attempt that resumed in place holds no active tree, so its
            // held-back events wait for this arm (design-load-restores-the-save.md 4.3).
            ParsekScenario.ResolveHeldSaveAttemptEventsOnArm(committedTree.Id);

            // The recording we're about to resume carries VesselSpawned=true and a
            // non-zero SpawnedVesselPersistentId from its prior commit (set by the KSC
            // adoption path). While the tree was committed, those flags correctly meant
            // "a real vessel already represents this recording". Now the tree is live
            // again and will be re-committed at next scene exit; the merge dialog's
            // CanPersistVessel → ShouldSpawnAtRecordingEnd chain reads VesselSpawned
            // as "already spawned, don't persist again" and defaults the leaf to
            // ghost-only, which nulls the VesselSnapshot at commit time. Subsequent
            // KSC spawns then skip with "no vessel snapshot". Clearing the flags here
            // lets the next commit re-evaluate spawn eligibility from scratch. Before
            // clearing the spawned PID, intentionally promote the matched live vessel PID
            // into the recording's normal VesselPersistentId. Downstream tree-finalization,
            // snapshot refresh, and rollout-adoption paths key off VesselPersistentId and
            // must follow the vessel the player actually re-entered here, not the older
            // source PID that originally produced the committed recording.
            // Vessel names (design 6.2): the live vessel (a Parsek spawn, fresh guid) continues
            // the resumed recording's identity. Linked BEFORE the spawn pid is cleared below:
            // after that nothing but this link joins the two guids. Only through the
            // genuine-spawn branch (the backfill's rule): an adoption-stamp match is a pid the
            // craft bakes, never proof enough for a permanent link.
            if (liveTree.Recordings != null
                && liveTree.Recordings.TryGetValue(targetRecordingId, out Recording linkRec)
                && linkRec != null
                && VesselLaunchIdentity.IsGenuineSpawnPidMatch(linkRec, activeVesselPid))
            {
                VesselNameStore.AddLink(activeVesselGuid, linkRec.RecordedVesselGuid,
                    VesselIdentityLinkKind.Resume,
                    "resume rec=" + targetRecordingId + " pid=" + activeVesselPid.ToString(CultureInfo.InvariantCulture));
            }

            if (liveTree.Recordings != null
                && liveTree.Recordings.TryGetValue(targetRecordingId, out Recording resumedRec)
                && resumedRec != null
                && (resumedRec.VesselSpawned || resumedRec.SpawnedVesselPersistentId != 0))
            {
                uint priorSpawnedPid = resumedRec.SpawnedVesselPersistentId;
                uint priorVesselPid = resumedRec.VesselPersistentId;
                resumedRec.VesselPersistentId = activeVesselPid;
                resumedRec.VesselSpawned = false;
                resumedRec.SpawnedVesselPersistentId = 0;
                ParsekLog.Info("Flight",
                    $"TryTakeCommittedTreeForSpawnedVesselRestore: cleared prior spawn flags on " +
                    $"resumed recording '{targetRecordingId}' (wasSpawnedPid={priorSpawnedPid}, " +
                    $"vesselPid={priorVesselPid}->{activeVesselPid}) so the next commit re-evaluates " +
                    $"persist eligibility instead of defaulting to ghost-only");
            }

            tree = liveTree;
            recordingId = targetRecordingId;
            action = preparedAction;
            return true;
        }

        internal static CommittedSpawnedVesselRestoreAction PrepareCommittedTreeRestoreForSpawnedVessel(
            RecordingTree tree,
            string targetRecordingId,
            uint activeVesselPid)
        {
            if (tree == null || string.IsNullOrEmpty(targetRecordingId) || activeVesselPid == 0)
                return CommittedSpawnedVesselRestoreAction.None;
            if (!tree.Recordings.TryGetValue(targetRecordingId, out Recording targetRec) || targetRec == null)
                return CommittedSpawnedVesselRestoreAction.None;
            if (ResolveLiveTreeRecordingPidForRestore(targetRec) != activeVesselPid)
                return CommittedSpawnedVesselRestoreAction.None;
            if (!IsCommittedSpawnedRecordingRestorable(tree, targetRec))
                return CommittedSpawnedVesselRestoreAction.None;

            if (string.Equals(tree.ActiveRecordingId, targetRecordingId, StringComparison.Ordinal))
            {
                RemoveRecordingIdFromBackgroundMap(tree, targetRecordingId);
                return CommittedSpawnedVesselRestoreAction.ResumeActiveRecording;
            }

            if (!string.IsNullOrEmpty(tree.ActiveRecordingId)
                && tree.Recordings.TryGetValue(tree.ActiveRecordingId, out Recording oldActiveRec))
            {
                RemoveRecordingIdFromBackgroundMap(tree, tree.ActiveRecordingId);
                ApplyPreTransitionForVesselSwitch(
                    tree,
                    ResolveLiveTreeRecordingPidForRestore(oldActiveRec));
            }

            RemoveRecordingIdFromBackgroundMap(tree, targetRecordingId);
            return CommittedSpawnedVesselRestoreAction.PromoteFromBackground;
        }

        internal static uint ResolveLiveTreeRecordingPidForRestore(Recording rec)
        {
            if (rec == null)
                return 0;
            if (rec.SpawnedVesselPersistentId != 0)
                return rec.SpawnedVesselPersistentId;
            return rec.VesselPersistentId;
        }

        internal static bool IsCommittedSpawnedRecordingRestorable(RecordingTree tree, Recording rec)
        {
            if (tree == null || rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return false;
            if (string.Equals(tree.ActiveRecordingId, rec.RecordingId, StringComparison.Ordinal))
                return true;
            if (HasNextChainSegmentInTree(tree, rec))
                return false;
            if (rec.TerminalStateValue.HasValue)
            {
                TerminalState terminalState = rec.TerminalStateValue.Value;
                if (terminalState == TerminalState.Destroyed
                    || terminalState == TerminalState.Recovered
                    || terminalState == TerminalState.Docked
                    || terminalState == TerminalState.Boarded)
                {
                    return false;
                }
            }

            return string.IsNullOrEmpty(rec.ChildBranchPointId)
                || GhostPlaybackLogic.IsEffectiveLeafForVessel(rec, tree);
        }

        internal static bool HasNextChainSegmentInTree(RecordingTree tree, Recording rec)
        {
            if (tree == null || rec == null || string.IsNullOrEmpty(rec.ChainId))
                return false;

            int nextIdx = rec.ChainIndex + 1;
            foreach (Recording other in tree.Recordings.Values)
            {
                if (other == null || ReferenceEquals(other, rec))
                    continue;
                if (other.ChainId == rec.ChainId
                    && other.ChainIndex == nextIdx
                    && other.ChainBranch == rec.ChainBranch)
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsPreferredCommittedSpawnedRestoreCandidate(
            RecordingTree tree,
            Recording candidate,
            Recording currentBest)
        {
            bool candidateIsActive = tree != null
                && string.Equals(tree.ActiveRecordingId, candidate?.RecordingId, StringComparison.Ordinal);
            bool currentBestIsActive = tree != null
                && string.Equals(tree.ActiveRecordingId, currentBest?.RecordingId, StringComparison.Ordinal);
            if (candidateIsActive != currentBestIsActive)
                return candidateIsActive;

            int candidateChainIndex = candidate != null ? candidate.ChainIndex : int.MinValue;
            int currentBestChainIndex = currentBest != null ? currentBest.ChainIndex : int.MinValue;
            if (candidateChainIndex != currentBestChainIndex)
                return candidateChainIndex > currentBestChainIndex;

            int candidateOrder = candidate != null && candidate.TreeOrder >= 0 ? candidate.TreeOrder : int.MinValue;
            int currentBestOrder = currentBest != null && currentBest.TreeOrder >= 0 ? currentBest.TreeOrder : int.MinValue;
            if (candidateOrder != currentBestOrder)
                return candidateOrder > currentBestOrder;

            double candidateEnd = candidate != null ? candidate.EndUT : double.NegativeInfinity;
            double currentBestEnd = currentBest != null ? currentBest.EndUT : double.NegativeInfinity;
            if (candidateEnd != currentBestEnd)
                return candidateEnd > currentBestEnd;

            return string.CompareOrdinal(candidate?.RecordingId, currentBest?.RecordingId) > 0;
        }

        internal static int RemoveRecordingIdFromBackgroundMap(RecordingTree tree, string recordingId)
        {
            if (tree?.BackgroundMap == null || string.IsNullOrEmpty(recordingId))
                return 0;

            List<uint> keysToRemove = null;
            foreach (KeyValuePair<uint, string> entry in tree.BackgroundMap)
            {
                if (!string.Equals(entry.Value, recordingId, StringComparison.Ordinal))
                    continue;

                if (keysToRemove == null)
                    keysToRemove = new List<uint>();
                keysToRemove.Add(entry.Key);
            }

            if (keysToRemove == null)
                return 0;

            for (int i = 0; i < keysToRemove.Count; i++)
                tree.BackgroundMap.Remove(keysToRemove[i]);
            return keysToRemove.Count;
        }

        /// <summary>
        /// Bug #266: pure helper that mirrors the in-session
        /// <see cref="OnVesselSwitchComplete"/> BackgroundMap transition. Moves the
        /// tree's active recording into <c>BackgroundMap</c> and nulls
        /// <c>ActiveRecordingId</c>. Returns <c>true</c> if a BackgroundMap entry
        /// was actually inserted (valid <c>ActiveRecordingId</c> + non-zero PID),
        /// <c>false</c> in degenerate cases where the tree is still nulled but no
        /// background entry was added — the restore path will then treat the new
        /// active vessel as an outsider.
        ///
        /// <para>PID source priority: <paramref name="recorderVesselPid"/> (the
        /// live recorder PID at stash time) takes precedence over the tree
        /// recording's persisted <c>VesselPersistentId</c>, which may lag behind
        /// across docking / boarding edge cases.</para>
        /// </summary>
        internal static bool ApplyPreTransitionForVesselSwitch(
            RecordingTree tree, uint recorderVesselPid)
        {
            if (tree == null) return false;
            string oldActiveRecId = tree.ActiveRecordingId;
            uint oldVesselPid = recorderVesselPid;

            if (oldVesselPid == 0
                && !string.IsNullOrEmpty(oldActiveRecId)
                && tree.Recordings.TryGetValue(oldActiveRecId, out var oldRec))
            {
                oldVesselPid = oldRec.VesselPersistentId;
            }

            bool moved = false;
            if (!string.IsNullOrEmpty(oldActiveRecId) && oldVesselPid != 0)
            {
                tree.BackgroundMap[oldVesselPid] = oldActiveRecId;
                moved = true;
            }
            // In-memory record of the move, read only by
            // TryRevertPreTransitionForVesselSwitch; never serialized.
            tree.VesselSwitchPreTransitionActiveRecordingId = moved ? oldActiveRecId : null;
            tree.VesselSwitchPreTransitionVesselPid = moved ? oldVesselPid : 0;
            tree.ActiveRecordingId = null;
            return moved;
        }

        /// <summary>
        /// Bug #266: stash the active tree for a vessel-switch-induced FLIGHT→FLIGHT
        /// scene reload. Pre-transitions the tree at stash time so the restore
        /// coroutine just has to reinstall it: flushes the recorder, moves the
        /// active recording's vessel PID into <c>BackgroundMap</c>, nulls
        /// <c>ActiveRecordingId</c>, then stashes as <see cref="PendingTreeState.LimboVesselSwitch"/>.
        ///
        /// <para>This mirrors the in-session <see cref="OnVesselSwitchComplete"/> path
        /// at lines 1493–1534, except the <c>activeTree</c> reference must cross the
        /// scene boundary via <see cref="RecordingStore.PendingTree"/> because the
        /// <see cref="ParsekFlight"/> singleton is destroyed on scene reload.</para>
        /// </summary>
        void StashActiveTreeForVesselSwitch(double commitUT)
        {
            if (activeTree == null) return;

            // Defensive: enforce the same invariants that CanPreTransitionForVesselSwitch
            // checks. This method is only safe to call when those guards are clear,
            // because the in-flight continuation states (dock merge / split / chain-to-
            // vessel) need Update() logic that won't run across a scene reload. If a
            // future change adds a new state to CanPreTransitionForVesselSwitch but
            // forgets the stash-side check, this re-validation logs a Warn and routes
            // to the legacy Limbo stash so we never silently ship a corrupt state.
            if (!CanPreTransitionForVesselSwitch())
            {
                ParsekLog.Warn("Flight",
                    "StashActiveTreeForVesselSwitch: pre-transition guards no longer hold " +
                    $"(pendingTreeDockMerge={pendingTreeDockMerge}, pendingSplitRecorder={pendingSplitRecorder != null}, " +
                    $"pendingSplitInProgress={pendingSplitInProgress}, " +
                    $"chainToVesselPending={(recorder != null && recorder.ChainToVesselPending)}) — " +
                    "falling back to legacy Limbo stash; OnLoad safety net will finalize");
                StashActiveTreeAsPendingLimbo(commitUT);
                return;
            }

            ParsekLog.Info("Flight",
                $"StashActiveTreeForVesselSwitch: pre-transitioning tree '{activeTree.TreeName}' " +
                $"at UT={commitUT:F1} (activeRecId={activeTree.ActiveRecordingId ?? "<null>"})");
            RecorderStateLog.RecState("StashTreeForSwitch:entry", CaptureRecorderState());

            // 1. Flush the recorder buffers into the active tree's current recording
            //    WITHOUT setting terminal state. Same shape as the in-session path.
            string oldActiveRecId = activeTree.ActiveRecordingId;
            uint oldVesselPid = 0;
            if (recorder != null)
            {
                if (recorder.IsRecording)
                {
                    if (recorder.IsBackgrounded)
                        recorder.IsRecording = false;
                    else
                        recorder.ForceStop();
                }
                FlushRecorderToTreeRecording(recorder, activeTree);

                // Copy rewind save + reserved budget to the root recording.
                CopyRewindSaveToRoot(activeTree, recorder,
                    logTag: "MergeCommitFlush");

                oldVesselPid = recorder.RecordingVesselId;
            }

            // 2. Close background recorder orbit segments and flush their data into
            //    tree recordings. Does NOT set terminal state.
            if (backgroundRecorder != null)
                backgroundRecorder.FinalizeAllForCommit(commitUT);

            // 3. Pre-transition: move the old active recording's PID into BackgroundMap,
            //    null ActiveRecordingId. From the BackgroundMap's point of view this is
            //    identical to the in-session OnVesselSwitchComplete transition at
            //    ParsekFlight.cs:1528–1536. Pure helper so unit tests can exercise the
            //    PID-priority logic without booting Unity.
            int bgPidsBefore = activeTree.BackgroundMap.Count;
            bool moved = ApplyPreTransitionForVesselSwitch(activeTree, oldVesselPid);
            if (moved)
            {
                ParsekLog.Info("Flight",
                    $"StashActiveTreeForVesselSwitch: moved active recording '{oldActiveRecId}' " +
                    $"to BackgroundMap (was {bgPidsBefore} entries, now " +
                    $"{activeTree.BackgroundMap.Count})");
            }
            else
            {
                // Expected, NOT a problem: this is the outsider-chain case where the
                // tree was already in outsider state at stash time (player switched
                // from an outsider vessel to another outsider, both via TS reload).
                // ActiveRecordingId is already null and there's no recording PID to
                // index. The tree restores cleanly with the existing BackgroundMap
                // entries from prior hops; the new active vessel is checked against
                // those in the restore coroutine.
                ParsekLog.Info("Flight",
                    $"StashActiveTreeForVesselSwitch: no active recording to move " +
                    $"(activeRecId='{oldActiveRecId ?? "<null>"}', oldPid={oldVesselPid}) — " +
                    "outsider-chain stash; tree carries existing BackgroundMap entries " +
                    $"({activeTree.BackgroundMap.Count}) across the reload");
            }

            // 4. Stash as LimboVesselSwitch. The OnLoad dispatch routes this state to
            //    the vessel-switch restore coroutine.
            PendingTreeSlot.StashPendingTree(activeTree, PendingTreeState.LimboVesselSwitch);

            ParsekLog.Info("Flight",
                $"StashActiveTreeForVesselSwitch: stashed tree '{activeTree.TreeName}' as " +
                $"LimboVesselSwitch ({activeTree.Recordings.Count} recording(s), " +
                $"{activeTree.BackgroundMap.Count} background entry(ies)) — OnLoad will dispatch");
            RecorderStateLog.RecState("StashTreeForSwitch:post", CaptureRecorderState());
        }

        /// <summary>
        /// Finalizes the active tree on scene exit and stashes it as pending for
        /// <see cref="ParsekScenario"/> OnLoad to commit. Preserves stable-terminal
        /// (Landed/Splashed/Orbiting) snapshots for spawn-at-end (the silent
        /// full-fidelity commit) and force-writes dirty sidecars so they are durable;
        /// nulls non-spawnable snapshots (ghost visual + crew reservation released).
        /// </summary>
        void CommitTreeSceneExit(double commitUT, GameScenes scene)
        {
            if (activeTree == null) return;

            ParsekLog.Info("Flight", $"CommitTreeSceneExit: finalizing tree at UT={commitUT:F1}");
            RecorderStateLog.RecState("CommitTreeSceneExit:entry", CaptureRecorderState());

            // Finalize all recordings
            FinalizeTreeRecordings(activeTree, commitUT, isSceneExit: true);

            // In-flight Recover (operator ruling 2026-10-01): the auto-merge commit runs in
            // the Space Center's OnLoad, before stock recovers the vessel there, so stamp
            // the recovered launch's tip Recovered now, while the tree is not committed.
            InFlightRecoveryRequest.ApplyAtSceneExit(
                activeTree, scene, "scene-exit autoMerge-on");

            // Null vessel snapshots for recordings that don't need spawn-at-end.
            // Preserve snapshots for stable-terminal recordings (Landed/Splashed/Orbiting)
            // so the snapshot's sit field survives the save→load round-trip and the
            // spawn-at-end safety check doesn't block with "snapshot situation unsafe". (#289)
            //
            // Bug #271: preserve GhostVisualSnapshot before nulling VesselSnapshot so ghost
            // rendering still has vessel geometry. Without this, the ghost mesh builder falls
            // back to the green sphere for any recording whose VesselSnapshot was nulled here.
            foreach (var rec in activeTree.Recordings.Values)
            {
                bool keepForSpawn = rec.TerminalStateValue.HasValue
                    && (rec.TerminalStateValue.Value == TerminalState.Landed
                        || rec.TerminalStateValue.Value == TerminalState.Splashed
                        || rec.TerminalStateValue.Value == TerminalState.Orbiting);
                if (!keepForSpawn)
                {
                    if (rec.GhostVisualSnapshot == null && rec.VesselSnapshot != null)
                        rec.GhostVisualSnapshot = rec.VesselSnapshot.CreateCopy();
                    // Unreserve crew before nulling, matching the dialog path's
                    // ApplyVesselDecisions ghost-only branch: a non-spawnable leaf must
                    // not keep its kerbals reserved (silent full-fidelity crew parity).
                    CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                    rec.VesselSnapshot = null;
                }
            }

            // #289 (silent full-fidelity auto-commit): force-write dirty sidecars now,
            // mirroring the autoMerge=OFF scene-exit branch. KSP's auto-save already
            // fired (inside OnSceneChangeRequested, before this cleanup), so the
            // finalized stable-terminal VesselSnapshot dirtied by FinalizeTreeRecordings
            // would not otherwise reach disk before the next OnLoad. Without this the
            // silent full-fidelity commit's spawn-at-end could not re-hydrate the
            // finalized _vessel.craft on a cold load — see
            // docs/dev/done/plans/silent-full-fidelity-autocommit.md.
            int forcedWrites = 0;
            foreach (var rec in activeTree.Recordings.Values)
            {
                if (rec.FilesDirty)
                {
                    if (RecordingSidecarStore.SaveRecordingFiles(rec, incrementEpoch: false))
                        forcedWrites++;
                }
            }
            if (forcedWrites > 0)
            {
                ParsekLog.Info("Flight",
                    $"CommitTreeSceneExit: force-wrote {forcedWrites} dirty sidecar(s) after " +
                    "finalize so the post-finalize snapshot survives the next OnLoad [#289]");
            }

            // Stash as pending tree -- committed by ParsekScenario.OnLoad
            // (full-fidelity MergeCommit when it qualifies, else ghost-only).
            PendingTreeSlot.StashPendingTree(activeTree);

            ParsekLog.Info("Flight", $"CommitTreeSceneExit: stashed pending tree '{activeTree.TreeName}'");
            RecorderStateLog.RecState("CommitTreeSceneExit:post", CaptureRecorderState());
        }

        /// <summary>
        /// Finalizes all recordings in the tree: stops active recorder, flushes data,
        /// captures terminal state/orbit/position for all recordings.
        /// </summary>
        void FinalizeTreeRecordings(RecordingTree tree, double commitUT, bool isSceneExit)
        {
            string activeRecordingId = tree?.ActiveRecordingId;
            bool activeRecorderDestroyedDuringRecording =
                recorder != null && recorder.VesselDestroyedDuringRecording;

            // 1. Stop and flush the active recorder
            if (recorder != null)
            {
                if (recorder.IsRecording)
                {
                    if (recorder.IsBackgrounded)
                        recorder.IsRecording = false;
                    else
                        recorder.ForceStop();
                }
                FlushRecorderToTreeRecording(recorder, tree);

                // Copy rewind save + reserved budget to the root recording.
                CopyRewindSaveToRoot(tree, recorder,
                    logTag: "FinalizeTreeRecordings");
            }

            // 2. Finalize background recorder (close orbit segments, flush data)
            if (backgroundRecorder != null)
                backgroundRecorder.FinalizeAllForCommit(commitUT);

            FinalizeTreeRecordingsAfterFlush(
                tree,
                commitUT,
                isSceneExit,
                resolveFinalizationCache: rec => ResolveFinalizationCacheForRecording(tree, rec),
                resolveDestroyedDuringRecording: rec =>
                    activeRecorderDestroyedDuringRecording
                    && !string.IsNullOrEmpty(activeRecordingId)
                    && string.Equals(rec?.RecordingId, activeRecordingId, StringComparison.Ordinal));
        }

        internal static void FinalizeTreeRecordingsAfterFlush(
            RecordingTree tree,
            double commitUT,
            bool isSceneExit,
            Func<Recording, RecordingFinalizationCache> resolveFinalizationCache = null,
            Func<Recording, bool> resolveDestroyedDuringRecording = null,
            Func<uint, Vessel> findVesselByPid = null,
            FinalizationLiveVesselAccess liveVesselAccess = null)
        {
            var sceneExitLifetimeExtendedIds = isSceneExit
                ? new HashSet<string>(StringComparer.Ordinal)
                : null;

            // 3. Process each recording in the tree
            RecordingFinalizationCache activeFinalizationCache = null;
            foreach (var kvp in tree.Recordings)
            {
                Recording recording = kvp.Value;
                RecordingFinalizationCache finalizationCache =
                    resolveFinalizationCache != null
                        ? resolveFinalizationCache(recording)
                        : null;
                if (!string.IsNullOrEmpty(tree.ActiveRecordingId)
                    && string.Equals(recording?.RecordingId, tree.ActiveRecordingId, StringComparison.Ordinal))
                {
                    activeFinalizationCache = finalizationCache;
                }

                bool vesselDestroyedDuringRecording =
                    resolveDestroyedDuringRecording != null
                    && resolveDestroyedDuringRecording(recording);

                if (FinalizeIndividualRecording(
                        recording,
                        commitUT,
                        isSceneExit,
                        finalizationCache,
                        tree,
                        vesselDestroyedDuringRecording,
                        findVesselByPid,
                        liveVesselAccess)
                    && sceneExitLifetimeExtendedIds != null
                    && !string.IsNullOrEmpty(recording?.RecordingId))
                {
                    sceneExitLifetimeExtendedIds.Add(recording.RecordingId);
                }
            }

            // 3b. Ensure active recording has terminalState only when it is a true
            // non-leaf. Active leaves already went through FinalizeIndividualRecording
            // above; running the active fallback again can consume stale scene-exit
            // evidence after the restored-record guard has already cleared.
            if (ShouldEnsureActiveRecordingTerminalState(tree)
                && EnsureActiveRecordingTerminalState(
                        tree,
                        isSceneExit,
                        commitUT,
                        activeFinalizationCache)
                && sceneExitLifetimeExtendedIds != null
                && !string.IsNullOrEmpty(tree.ActiveRecordingId))
            {
                sceneExitLifetimeExtendedIds.Add(tree.ActiveRecordingId);
            }
            RefreshActiveEffectiveLeafSnapshot(tree, isSceneExit, sceneExitLifetimeExtendedIds);

            // 4. Prune zero-point debris leaves (#173) — removes recordings with no
            // trajectory data that were created from same-frame destruction debris.
            PruneZeroPointLeaves(tree);
            PruneSinglePointDebrisLeaves(tree);

            // Phase F: tree-level resource delta capture removed. The ledger is
            // the single source of truth for funds/science/reputation; per-recording
            // ledger actions committed during the tree's lifetime cover the cost.
            ParsekLog.Info("Flight",
                $"FinalizeTreeRecordings: tree '{tree.TreeName}' finalized " +
                $"(resource accounting via ledger; no tree-level delta captured).");
        }

        private static bool IsActiveRecorderCacheCandidate(
            RecordingTree tree,
            Recording recording,
            FlightRecorder activeRecorder)
        {
            if (recording == null || activeRecorder == null)
                return false;

            if (tree != null
                && !string.IsNullOrEmpty(tree.ActiveRecordingId)
                && string.Equals(
                    tree.ActiveRecordingId,
                    recording.RecordingId,
                    StringComparison.Ordinal))
            {
                return true;
            }

            return recording.VesselPersistentId != 0
                && recording.VesselPersistentId == activeRecorder.RecordingVesselId;
        }

        private static bool CacheIdentityMatchesRecording(
            Recording recording,
            RecordingFinalizationCache cache)
        {
            if (recording == null || cache == null)
                return false;

            if (!string.IsNullOrEmpty(recording.RecordingId)
                && !string.IsNullOrEmpty(cache.RecordingId)
                && !string.Equals(
                    recording.RecordingId,
                    cache.RecordingId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            return recording.VesselPersistentId == 0
                || cache.VesselPersistentId == 0
                || recording.VesselPersistentId == cache.VesselPersistentId;
        }

        internal static bool HasFallbackCandidateCache(
            RecordingFinalizationCache cache,
            bool vesselMissing)
        {
            return cache != null && vesselMissing;
        }

        private static bool TryApplyFinalizationCacheFallback(
            Recording recording,
            RecordingFinalizationCache cache,
            string consumerPath,
            bool allowStale,
            out RecordingFinalizationCacheApplyResult result,
            bool allowAlreadyFinalizedRepair = false)
        {
            var options = new RecordingFinalizationCacheApplyOptions
            {
                ConsumerPath = consumerPath,
                AllowStale = allowStale,
                AllowAlreadyFinalizedRepair = allowAlreadyFinalizedRepair
            };

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                recording,
                cache,
                options,
                out result);

            if (applied)
            {
                ParsekLog.Info("Flight",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Finalization source=cache consumer={0} rec={1} terminal={2} " +
                        "terminalUT={3:F3} appendedSegments={4} staleAllowed={5}",
                        consumerPath ?? "(null)",
                        recording?.DebugName ?? "(null)",
                        result.TerminalState?.ToString() ?? "(null)",
                        result.TerminalUT,
                        result.AppendedSegmentCount,
                        allowStale));

                if (cache.Status == FinalizationCacheStatus.Stale)
                {
                    ParsekLog.Warn("Flight",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Finalization source=cache applied stale cache consumer={0} rec={1} " +
                            "terminal={2} terminalUT={3:F3} owner={4} cachedAtUT={5:F3}",
                            consumerPath ?? "(null)",
                            recording?.DebugName ?? "(null)",
                            result.TerminalState?.ToString() ?? "(null)",
                            result.TerminalUT,
                            cache.Owner,
                            cache.CachedAtUT));
                }
            }

            return applied;
        }

        internal static bool ShouldRepairExistingTerminalFromDestroyedCache(
            Recording recording,
            RecordingFinalizationCache cache,
            double commitUT)
        {
            if (recording == null || cache == null)
                return false;
            if (!recording.TerminalStateValue.HasValue)
                return false;
            if (!cache.TerminalState.HasValue
                || cache.TerminalState.Value != TerminalState.Destroyed)
                return false;

            TerminalState existing = recording.TerminalStateValue.Value;
            if (existing == TerminalState.Destroyed)
            {
                LogDestroyedCacheRepairRejected(recording, cache, commitUT, existing, "alreadyDestroyed");
                return false;
            }

            // The cache is allowed to correct the in-flight ballistic fallback
            // SubOrbital is the speculative state stamped when the vessel was
            // still on a ballistic path. Orbiting is deliberately excluded:
            // it requires stronger periapsis/situation evidence and is treated
            // as a stable spawn endpoint, not a prediction to repair.
            if (existing != TerminalState.SubOrbital)
            {
                LogDestroyedCacheRepairRejected(
                    recording,
                    cache,
                    commitUT,
                    existing,
                    "existingTerminalNotSubOrbital");
                return false;
            }

            if (!IsCacheTerminalAtOrBeforeCommit(cache, commitUT))
            {
                LogDestroyedCacheRepairRejected(
                    recording,
                    cache,
                    commitUT,
                    existing,
                    "futureOrInvalidTerminalUT");
                return false;
            }

            return true;
        }

        private static bool IsCacheTerminalAtOrBeforeCommit(
            RecordingFinalizationCache cache,
            double commitUT)
        {
            if (cache == null)
                return false;
            if (double.IsNaN(commitUT) || double.IsInfinity(commitUT))
                return false;
            if (double.IsNaN(cache.TerminalUT) || double.IsInfinity(cache.TerminalUT))
                return false;

            return cache.TerminalUT <= commitUT + 1e-6;
        }

        private static void LogDestroyedCacheRepairRejected(
            Recording recording,
            RecordingFinalizationCache cache,
            double commitUT,
            TerminalState existing,
            string reason)
        {
            ParsekLog.VerboseRateLimited(
                "Flight",
                $"finalization-cache-repair-rejected-{recording?.RecordingId ?? "null"}-{reason}",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Finalization cache repair skipped: rec={0} reason={1} " +
                    "existingTerminal={2} cacheTerminal={3} terminalUT={4:F3} commitUT={5:F3}",
                    recording?.DebugName ?? "(null)",
                    reason ?? "(null)",
                    existing,
                    cache?.TerminalState?.ToString() ?? "(null)",
                    cache != null ? cache.TerminalUT : double.NaN,
                    commitUT));
        }

        internal static bool ShouldRefreshActiveEffectiveLeafSnapshot(
            RecordingTree tree,
            Recording activeRec)
        {
            if (tree == null || activeRec == null)
                return false;
            if (string.IsNullOrEmpty(tree.ActiveRecordingId)
                || tree.ActiveRecordingId != activeRec.RecordingId)
                return false;
            if (string.IsNullOrEmpty(activeRec.ChildBranchPointId))
                return false;
            if (!activeRec.TerminalStateValue.HasValue
                || !GhostPlaybackLogic.IsSpawnableTerminal(activeRec.TerminalStateValue.Value))
                return false;

            return GhostPlaybackLogic.IsEffectiveLeafForVessel(activeRec, tree);
        }

        internal static void RefreshActiveEffectiveLeafSnapshot(
            RecordingTree tree,
            bool isSceneExit,
            ISet<string> sceneExitLifetimeExtendedIds = null)
        {
            if (tree == null || string.IsNullOrEmpty(tree.ActiveRecordingId))
                return;
            if (!tree.Recordings.TryGetValue(tree.ActiveRecordingId, out var activeRec))
                return;
            if (!ShouldRefreshActiveEffectiveLeafSnapshot(tree, activeRec))
                return;
            if (sceneExitLifetimeExtendedIds != null
                && sceneExitLifetimeExtendedIds.Contains(activeRec.RecordingId))
            {
                ParsekLog.Verbose("Flight",
                    $"FinalizeTreeRecordings: active effective leaf '{activeRec.RecordingId}' " +
                    "uses scene-exit extended lifetime — skipping live re-snapshot");
                return;
            }

            Vessel activeVessel = activeRec.VesselPersistentId != 0
                ? FlightRecorder.FindVesselByPid(activeRec.VesselPersistentId)
                : null;
            if (activeVessel == null)
            {
                ParsekLog.Verbose("Flight",
                    $"FinalizeTreeRecordings: active effective leaf '{activeRec.RecordingId}' " +
                    $"stable terminal={activeRec.TerminalStateValue} but vessel not found " +
                    $"(isSceneExit={isSceneExit}) — keeping existing snapshot");
                return;
            }

            CaptureTerminalOrbit(activeRec, activeVessel);
            CaptureTerminalPosition(activeRec, activeVessel);
            RecordingEndpointResolver.RefreshEndpointDecision(activeRec, "FinalizeTreeRecordings.StableLeaf");
            TryRefreshStableTerminalSnapshot(
                activeRec,
                activeVessel,
                isSceneExit,
                "FinalizeTreeRecordings: re-snapshotted active effective leaf");
        }

        internal static bool ApplyFinalEndpointSegmentPhase(
            Recording rec,
            RecordingTree treeContext,
            bool finalizeVesselFound,
            Vessel finalizeVessel)
        {
            string activeRecordingId = treeContext?.ActiveRecordingId;
            if (!ShouldAllowFinalEndpointSegmentPhase(rec, activeRecordingId, treeContext))
                return false;

            if (!TryResolveFinalEndpointSegmentPhase(
                rec,
                finalizeVesselFound,
                finalizeVessel,
                out string phase,
                out string bodyName,
                out string source,
                out string skipReason))
            {
                ParsekLog.Verbose("Flight",
                    $"Final SegmentPhase skipped: rec={rec?.RecordingId ?? "<none>"} " +
                    $"reason={skipReason ?? "unresolved"}");
                return false;
            }

            return SetFinalSegmentPhase(rec, phase, bodyName, source);
        }

        internal static bool TryResolveFinalEndpointSegmentPhase(
            Recording rec,
            bool finalizeVesselFound,
            Vessel finalizeVessel,
            out string phase,
            out string bodyName,
            out string source,
            out string skipReason)
        {
            phase = null;
            bodyName = null;
            source = null;
            skipReason = null;

            if (rec == null)
            {
                skipReason = "recording-null";
                return false;
            }

            if (finalizeVesselFound
                && SegmentPhaseClassifier.TryClassify(finalizeVessel, out phase, out bodyName))
            {
                source = "live-vessel";
                return true;
            }

            if (TryResolveTerminalStateSegmentPhase(rec, out phase, out bodyName, out source))
                return true;

            if (TryResolveTrackSectionEnvironmentSegmentPhase(
                rec,
                out phase,
                out bodyName,
                out source,
                out skipReason))
            {
                return true;
            }

            if (TryResolveAbsoluteEndpointPointSegmentPhase(
                rec,
                out phase,
                out bodyName,
                out source,
                out skipReason))
            {
                return true;
            }

            if (string.IsNullOrEmpty(skipReason))
                skipReason = "no-reliable-endpoint-evidence";
            return false;
        }

        private static bool TryResolveTerminalStateSegmentPhase(
            Recording rec,
            out string phase,
            out string bodyName,
            out string source)
        {
            phase = null;
            bodyName = null;
            source = null;

            if (rec == null || !rec.TerminalStateValue.HasValue)
                return false;

            TerminalState terminal = rec.TerminalStateValue.Value;
            if ((terminal == TerminalState.Orbiting || terminal == TerminalState.Docked)
                && !string.IsNullOrEmpty(rec.TerminalOrbitBody))
            {
                phase = "exo";
                bodyName = rec.TerminalOrbitBody;
                source = "terminal-orbit";
                return true;
            }

            if (terminal == TerminalState.Landed || terminal == TerminalState.Splashed)
            {
                if (!TryGetTerminalSurfaceBodyName(rec, out bodyName))
                {
                    bool finalEndpointIsRelative =
                        TryGetFinalTrackSection(rec, out TrackSection finalSection)
                        && finalSection.referenceFrame == ReferenceFrame.Relative;
                    if (!finalEndpointIsRelative)
                        bodyName = GetBestEndpointBodyName(rec, includeFlatPointBody: false);
                }

                if (!string.IsNullOrEmpty(bodyName))
                {
                    phase = "surface";
                    source = "terminal-surface";
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveTrackSectionEnvironmentSegmentPhase(
            Recording rec,
            out string phase,
            out string bodyName,
            out string source,
            out string skipReason)
        {
            phase = null;
            bodyName = null;
            source = null;
            skipReason = null;

            if (!TryGetFinalTrackSection(rec, out TrackSection section))
                return false;

            bool isRelative = section.referenceFrame == ReferenceFrame.Relative;
            if (isRelative)
            {
                if (!TryGetReliableRelativeEndpointBodyName(rec, section, out bodyName, out source))
                {
                    skipReason = "relative-endpoint-unresolved-no-terminal-body-or-absolute-shadow";
                    return false;
                }
            }
            else
            {
                bodyName = GetBestEndpointBodyName(rec, includeFlatPointBody: true);
            }

            phase = SegmentPhaseClassifier.EnvironmentToPhase(section.environment);
            if (string.IsNullOrEmpty(source))
                source = "track-section-environment";
            return true;
        }

        private static bool TryGetTerminalSurfaceBodyName(Recording rec, out string bodyName)
        {
            bodyName = null;
            if (rec == null)
                return false;

            if (rec.TerminalPosition.HasValue
                && !string.IsNullOrEmpty(rec.TerminalPosition.Value.body))
            {
                bodyName = rec.TerminalPosition.Value.body;
                return true;
            }

            if (rec.SurfacePos.HasValue
                && !string.IsNullOrEmpty(rec.SurfacePos.Value.body))
            {
                bodyName = rec.SurfacePos.Value.body;
                return true;
            }

            return false;
        }

        private static bool TryGetReliableRelativeEndpointBodyName(
            Recording rec,
            TrackSection section,
            out string bodyName,
            out string source)
        {
            bodyName = null;
            source = null;

            if (rec != null)
            {
                if (!string.IsNullOrEmpty(rec.TerminalOrbitBody))
                {
                    bodyName = rec.TerminalOrbitBody;
                    source = "terminal-orbit-body-environment";
                    return true;
                }

                if (TryGetTerminalSurfaceBodyName(rec, out bodyName))
                {
                    source = "terminal-position-body-environment";
                    return true;
                }
            }

            if (TryGetLastBodyFixedPrimaryPoint(section, out TrajectoryPoint bodyFixedPrimaryPoint)
                && !string.IsNullOrEmpty(bodyFixedPrimaryPoint.bodyName))
            {
                bodyName = bodyFixedPrimaryPoint.bodyName;
                source = "relative-absolute-shadow-environment";
                return true;
            }

            return false;
        }

        private static bool TryResolveAbsoluteEndpointPointSegmentPhase(
            Recording rec,
            out string phase,
            out string bodyName,
            out string source,
            out string skipReason)
        {
            phase = null;
            bodyName = null;
            source = null;
            skipReason = null;

            if (!TryGetFinalAbsoluteEndpointPoint(
                rec,
                out TrajectoryPoint point,
                out source,
                out skipReason))
            {
                return false;
            }

            bodyName = point.bodyName;
            if (string.IsNullOrEmpty(bodyName))
            {
                skipReason = "absolute-endpoint-point-has-no-body";
                return false;
            }

            CelestialBody body = null;
            try
            {
                body = FlightGlobals.GetBodyByName(bodyName);
            }
            catch (Exception ex)
            {
                skipReason = "body-lookup-failed-" + ex.GetType().Name;
                return false;
            }

            if (body == null)
            {
                skipReason = "body-lookup-returned-null";
                return false;
            }

            double approachAltitude = FlightRecorder.ComputeApproachAltitude(body);
            phase = SegmentPhaseClassifier.ClassifyFromValues(
                Vessel.Situations.FLYING,
                body.atmosphere,
                point.altitude,
                body.atmosphereDepth,
                approachAltitude);
            return true;
        }

        private static bool TryGetFinalTrackSection(Recording rec, out TrackSection section)
        {
            section = default(TrackSection);
            if (rec?.TrackSections == null || rec.TrackSections.Count == 0)
                return false;

            int sectionIndex = -1;
            if (!double.IsNaN(rec.ExplicitEndUT))
                sectionIndex = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, rec.ExplicitEndUT);
            if (sectionIndex < 0 && rec.Points != null && rec.Points.Count > 0)
                sectionIndex = TrajectoryMath.FindTrackSectionForUT(
                    rec.TrackSections,
                    rec.Points[rec.Points.Count - 1].ut);
            if (sectionIndex < 0)
                sectionIndex = rec.TrackSections.Count - 1;

            section = rec.TrackSections[sectionIndex];
            return true;
        }

        private static bool TryGetLastBodyFixedPrimaryPoint(
            TrackSection section,
            out TrajectoryPoint point)
        {
            point = default(TrajectoryPoint);
            if (section.bodyFixedFrames == null || section.bodyFixedFrames.Count == 0)
                return false;

            point = section.bodyFixedFrames[section.bodyFixedFrames.Count - 1];
            return true;
        }

        private static bool TryGetFinalAbsoluteEndpointPoint(
            Recording rec,
            out TrajectoryPoint point,
            out string source,
            out string skipReason)
        {
            point = default(TrajectoryPoint);
            source = null;
            skipReason = null;

            if (rec == null)
            {
                skipReason = "recording-null";
                return false;
            }

            if (rec.TrackSections == null || rec.TrackSections.Count == 0)
            {
                if (rec.Points != null && rec.Points.Count > 0)
                {
                    point = rec.Points[rec.Points.Count - 1];
                    source = "legacy-flat-point";
                    return true;
                }

                skipReason = "no-track-sections-or-points";
                return false;
            }

            if (!TryGetFinalTrackSection(rec, out TrackSection section))
            {
                skipReason = "no-final-track-section";
                return false;
            }

            if (section.referenceFrame == ReferenceFrame.Relative)
            {
                if (TryGetLastBodyFixedPrimaryPoint(section, out point))
                {
                    source = "relative-absolute-shadow-point";
                    return true;
                }

                skipReason = "relative-endpoint-unresolved-no-absolute-shadow";
                return false;
            }

            if (section.referenceFrame != ReferenceFrame.Absolute)
            {
                skipReason = "unsupported-reference-frame-" + section.referenceFrame;
                return false;
            }

            if (section.frames != null && section.frames.Count > 0)
            {
                point = section.frames[section.frames.Count - 1];
                source = "absolute-section-point";
                return true;
            }

            if (rec.Points != null && rec.Points.Count > 0)
            {
                point = rec.Points[rec.Points.Count - 1];
                source = "absolute-flat-point";
                return true;
            }

            skipReason = "absolute-section-has-no-points";
            return false;
        }

        private static string GetBestEndpointBodyName(
            Recording rec,
            bool includeFlatPointBody)
        {
            if (rec == null)
                return null;
            if (!string.IsNullOrEmpty(rec.EndpointBodyName))
                return rec.EndpointBodyName;
            if (!string.IsNullOrEmpty(rec.TerminalOrbitBody))
                return rec.TerminalOrbitBody;
            if (rec.TerminalPosition.HasValue
                && !string.IsNullOrEmpty(rec.TerminalPosition.Value.body))
                return rec.TerminalPosition.Value.body;
            if (rec.SurfacePos.HasValue
                && !string.IsNullOrEmpty(rec.SurfacePos.Value.body))
                return rec.SurfacePos.Value.body;
            if (!string.IsNullOrEmpty(rec.SegmentBodyName))
                return rec.SegmentBodyName;
            if (!string.IsNullOrEmpty(rec.StartBodyName))
                return rec.StartBodyName;
            if (includeFlatPointBody && rec.Points != null && rec.Points.Count > 0)
                return rec.Points[rec.Points.Count - 1].bodyName;
            return null;
        }

        /// <summary>
        /// Infers terminal state from a recording's last trajectory point when the
        /// vessel is no longer findable (e.g., unloaded during scene exit). Uses the
        /// last point's altitude to distinguish landed/splashed from in-flight.
        /// Returns SubOrbital as a safe default if no trajectory data is available.
        /// </summary>
        internal static TerminalState InferTerminalStateFromTrajectory(Recording rec)
        {
            if (rec.Points == null || rec.Points.Count == 0)
                return TerminalState.SubOrbital;

            var lastPt = rec.Points[rec.Points.Count - 1];

            // Very low altitude → likely landed or about to land
            if (lastPt.altitude < 50.0)
                return TerminalState.Landed;

            // Check last track section environment for surface indication
            if (rec.TrackSections != null && rec.TrackSections.Count > 0)
            {
                var lastEnv = rec.TrackSections[rec.TrackSections.Count - 1].environment;
                if (lastEnv == SegmentEnvironment.SurfaceMobile
                    || lastEnv == SegmentEnvironment.SurfaceStationary)
                    return TerminalState.Landed;
            }

            // Check orbit segments for stable orbit (ecc < 1, periapsis above body surface).
            if (rec.OrbitSegments != null && rec.OrbitSegments.Count > 0)
            {
                var lastOrbit = rec.OrbitSegments[rec.OrbitSegments.Count - 1];
                if (HasStableOrbitEvidenceForTerminalInference(lastOrbit))
                    return TerminalState.Orbiting;
            }

            // Default: vessel was in flight (atmospheric descent, suborbital, etc.)
            return TerminalState.SubOrbital;
        }

        internal static Func<string, double?> TerminalInferenceBodyRadiusResolverForTesting;

        /// <summary>
        /// Test seam for the periapsis floor (<see cref="OrbitClearance"/>) the inference reads.
        /// When only the radius seam is installed the floor defaults to sea level.
        /// </summary>
        internal static Func<string, double?> TerminalInferencePeriapsisFloorResolverForTesting;

        /// <summary>
        /// Orbiting evidence for a recording whose vessel is gone: a bound last orbit segment
        /// whose periapsis clears the body's periapsis floor (the atmosphere top, or the
        /// highest terrain on an airless body), the same line commit classification and the
        /// terminal-orbit spawn-safety check use. A periapsis inside the atmosphere is a
        /// decaying flight, so it is not Orbiting evidence and the caller falls back to
        /// SubOrbital.
        /// </summary>
        internal static bool HasStableOrbitEvidenceForTerminalInference(OrbitSegment lastOrbit)
        {
            if (lastOrbit.eccentricity >= 1.0 || string.IsNullOrEmpty(lastOrbit.bodyName))
                return false;
            if (!TryResolveTerminalInferenceBody(
                    lastOrbit.bodyName, out double bodyRadius, out double periapsisFloor)
                || bodyRadius <= 0.0)
                return false;

            double periapsisRadius = lastOrbit.semiMajorAxis * (1.0 - lastOrbit.eccentricity);
            bool clear = OrbitClearance.IsBoundOrbitClear(
                lastOrbit.eccentricity,
                periapsisRadius,
                bodyRadius,
                periapsisFloor);
            if (!clear
                && OrbitClearance.IsFinite(periapsisRadius)
                && periapsisRadius > bodyRadius)
            {
                ParsekLog.Verbose("Flight", string.Format(CultureInfo.InvariantCulture,
                    "HasStableOrbitEvidenceForTerminalInference: periapsis {0:F0}m on {1} is under " +
                    "the periapsis floor {2:F0}m - not Orbiting evidence",
                    periapsisRadius - bodyRadius,
                    lastOrbit.bodyName,
                    periapsisFloor));
            }
            return clear;
        }

        private static bool TryResolveTerminalInferenceBody(
            string bodyName,
            out double bodyRadius,
            out double periapsisFloor)
        {
            bodyRadius = 0.0;
            periapsisFloor = 0.0;
            if (string.IsNullOrEmpty(bodyName))
                return false;

            Func<string, double?> resolver = TerminalInferenceBodyRadiusResolverForTesting;
            if (resolver != null)
            {
                double? resolved = resolver(bodyName);
                if (resolved.HasValue && resolved.Value > 0.0)
                {
                    bodyRadius = resolved.Value;
                    Func<string, double?> floorResolver = TerminalInferencePeriapsisFloorResolverForTesting;
                    double? floor = floorResolver != null ? floorResolver(bodyName) : null;
                    periapsisFloor = floor.HasValue ? floor.Value : 0.0;
                    return true;
                }

                return false;
            }

            return TryResolveTerminalInferenceBodyLive(bodyName, out bodyRadius, out periapsisFloor);
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool TryResolveTerminalInferenceBodyLive(
            string bodyName,
            out double bodyRadius,
            out double periapsisFloor)
        {
            bodyRadius = 0.0;
            periapsisFloor = 0.0;
            try
            {
                var body = FlightGlobals.GetBodyByName(bodyName);
                if (body != null && body.Radius > 0.0)
                {
                    bodyRadius = body.Radius;
                    periapsisFloor = OrbitClearance.ResolvePeriapsisFloorAltitude(body);
                    return true;
                }
            }
            catch
            {
                // FlightGlobals is unavailable in headless tests.
            }

            return false;
        }

        /// <summary>
        /// Decides whether
        /// <see cref="FinalizeIndividualRecording"/> /
        /// <see cref="EnsureActiveRecordingTerminalState"/> should skip the
        /// scene-exit "vessel was alive when unloaded → infer Landed/Splashed
        /// from last trajectory point" branch.
        ///
        /// <para>
        /// PR #572 follow-up — the gate fires when the recording was just
        /// repaired from the committed tree this frame
        /// (<see cref="Recording.RestoredFromCommittedTreeThisFrame"/>).
        /// The trajectory is a copy of a recording that was committed
        /// mid-flight without a terminal state — typically because the
        /// missing live pid is a deliberate Re-Fly strip casualty, not a
        /// natural unload — so the surface inference's "vessel was alive
        /// when unloaded" assumption does not apply.
        /// </para>
        ///
        /// <para>
        /// Bug 2 follow-up (2026-04-30 STASH probe regression) — the gate
        /// also fires when the recording's trajectory has fewer than 2
        /// points. After an optimizer split nulls the original half's
        /// terminal state and a subsequent quickload-resume tree trim
        /// removes everything past the cutoff, the surviving payload is a
        /// single ascending start-point. <see cref="InferTerminalStateFromTrajectory"/>
        /// then defaults to <see cref="TerminalState.SubOrbital"/>, silently
        /// overwriting what was Splashed on a sibling chain segment that no
        /// longer exists. Skipping the inference leaves the terminal unset
        /// so <see cref="EffectiveState.ResolveChainTerminalRecording"/> /
        /// <see cref="UnfinishedFlightClassifier"/> see "no terminal" and
        /// the recording falls out of UF/STASH naturally.
        /// </para>
        ///
        /// <para>
        /// An additional "orbital evidence" clause (high
        /// <see cref="Recording.MaxDistanceFromLaunch"/> and/or a stable
        /// orbit segment) was considered as Option D in the design plan
        /// (<c>docs/dev/plans/refly-finalize-stripped-vessel-landed-fix.md</c>)
        /// and rejected: the legitimate "orbit-then-land" case shares the
        /// same shape (high MaxDistanceFromLaunch + stable orbit segment
        /// alongside a low-altitude last point), so any threshold that
        /// captures the user's strip-casualty case also breaks the existing
        /// pinned tests
        /// <c>EnsureActiveRecordingTerminalState_NoLiveVesselOnSceneExit_InfersFromTrajectory</c>
        /// and <c>SceneExitInferredActiveNonLeaf_DefaultsToPersistInMergeDialog</c>.
        /// Future readers: do not add an orbital-evidence clause here
        /// without first revisiting those two tests.
        /// </para>
        ///
        /// Returns true with a human-readable <paramref name="reason"/>
        /// when the inference must be skipped; false (with reason=null)
        /// when the normal inference path should proceed.
        /// </summary>
        internal static bool ShouldSkipSceneExitSurfaceInferenceForRestoredRecording(
            Recording rec,
            out string reason)
        {
            reason = null;
            if (rec == null)
                return false;

            if (rec.RestoredFromCommittedTreeThisFrame)
            {
                reason = "recording was repaired from committed tree this frame " +
                    "(PR #572 follow-up: trajectory came from a non-authoritative committed copy)";
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true when the only outcome
        /// <see cref="InferTerminalStateFromTrajectory"/> can produce for this
        /// recording is the <see cref="TerminalState.SubOrbital"/> default (i.e.
        /// no altitude&lt;50m point, no SurfaceMobile/Stationary terminal section,
        /// no stable orbit segment) AND the trajectory carries fewer than 2
        /// points. A single high-altitude point is typically a quickload-trim
        /// or optimizer-split survivor whose actual terminal data lived on a
        /// now-removed sibling chain segment — the SubOrbital default would be
        /// a fabricated guess, not measurement.
        /// </summary>
        internal static bool HasOnlySubOrbitalFallbackEvidence(Recording rec)
        {
            string reason;
            return HasOnlySubOrbitalFallbackEvidence(rec, out reason);
        }

        internal static bool HasOnlySubOrbitalFallbackEvidence(Recording rec, out string reason)
        {
            reason = null;
            if (rec == null) return false;
            // Need at least 2 points to corroborate "vessel was actually mid-flight"
            // rather than "single trim-survivor start point".
            if (rec.Points != null && rec.Points.Count >= 2)
                return false;

            // Single low-altitude point = real landed evidence; let the inference run.
            if (rec.Points != null && rec.Points.Count == 1
                && rec.Points[0].altitude < 50.0)
                return false;

            // SurfaceMobile/Stationary last section = real landed evidence.
            if (rec.TrackSections != null && rec.TrackSections.Count > 0)
            {
                var lastEnv = rec.TrackSections[rec.TrackSections.Count - 1].environment;
                if (lastEnv == SegmentEnvironment.SurfaceMobile
                    || lastEnv == SegmentEnvironment.SurfaceStationary)
                    return false;
            }

            // Stable closed orbit = real orbiting evidence, but only for a
            // one-point payload. With zero points, InferTerminalStateFromTrajectory
            // returns SubOrbital before checking orbit segments.
            if (rec.Points != null && rec.Points.Count == 1
                && rec.OrbitSegments != null && rec.OrbitSegments.Count > 0)
            {
                var lastOrbit = rec.OrbitSegments[rec.OrbitSegments.Count - 1];
                if (HasStableOrbitEvidenceForTerminalInference(lastOrbit))
                    return false;
            }

            reason = "trajectory has insufficient points for inference " +
                $"(points={(rec.Points?.Count ?? 0)} < 2 with no Landed/Orbiting signal; " +
                "likely quickload-trim or split survivor whose terminal lived on a " +
                "now-removed sibling chain segment)";
            return true;
        }

        /// <summary>
        /// Returns true when scene-exit terminal finalization must preserve the
        /// committed-tree repair instead of applying cache/finalizer/inference
        /// evidence. Requires a missing live vessel so normal live-vessel
        /// finalization remains authoritative.
        /// </summary>
        internal static bool ShouldPreserveRestoredSceneExitTerminalState(
            Recording rec,
            bool isSceneExit,
            bool vesselMissing,
            out string reason)
        {
            reason = null;
            if (!isSceneExit || !vesselMissing)
                return false;

            return ShouldSkipSceneExitSurfaceInferenceForRestoredRecording(rec, out reason);
        }

        internal static void PopulateTerminalPositionFromLastPoint(Recording rec, TerminalState inferredState)
        {
            if (rec?.Points == null || rec.Points.Count == 0)
                return;
            if (inferredState != TerminalState.Landed && inferredState != TerminalState.Splashed)
                return;

            var lastPt = rec.Points[rec.Points.Count - 1];
            rec.TerminalPosition = new SurfacePosition
            {
                body = string.IsNullOrEmpty(lastPt.bodyName) ? "Kerbin" : lastPt.bodyName,
                latitude = lastPt.latitude,
                longitude = lastPt.longitude,
                altitude = lastPt.altitude,
                rotation = lastPt.rotation,
                rotationRecorded = true,
                situation = inferredState == TerminalState.Splashed
                    ? SurfaceSituation.Splashed
                    : SurfaceSituation.Landed
            };
        }

        static void TryCaptureTerrainHeightFromLastTrajectoryPoint(Recording rec)
        {
            if (rec?.Points == null || rec.Points.Count == 0)
                return;

            var lastPt = rec.Points[rec.Points.Count - 1];
            try
            {
                CelestialBody body = FlightGlobals.GetBodyByName(lastPt.bodyName);
                if (body != null)
                {
                    rec.TerrainHeightAtEnd = body.TerrainAltitude(lastPt.latitude, lastPt.longitude);
                    ParsekLog.Verbose("TerrainCorrect",
                        $"Captured terrain height from trajectory for unloaded vessel: " +
                        $"{rec.TerrainHeightAtEnd:F1}m (lastPt alt={lastPt.altitude:F1}m, " +
                        $"rec={rec.RecordingId})");
                }
            }
            catch
            {
                // FlightGlobals not available (unit tests)
            }
        }

        private static bool UsesTerminalOrbitMetadata(TerminalState state)
        {
            return state == TerminalState.Orbiting
                || state == TerminalState.SubOrbital
                || state == TerminalState.Docked;
        }

        private static bool DidSceneExitUpdateTerminalOrbitMetadata(
            TerminalOrbitMetadataSnapshot before,
            Recording rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.TerminalOrbitBody))
                return false;

            return !before.HasMetadata
                || !string.Equals(before.body, rec.TerminalOrbitBody, StringComparison.Ordinal)
                || before.inclination != rec.TerminalOrbitInclination
                || before.eccentricity != rec.TerminalOrbitEccentricity
                || before.semiMajorAxis != rec.TerminalOrbitSemiMajorAxis
                || before.lan != rec.TerminalOrbitLAN
                || before.argumentOfPeriapsis != rec.TerminalOrbitArgumentOfPeriapsis
                || before.meanAnomalyAtEpoch != rec.TerminalOrbitMeanAnomalyAtEpoch
                || before.epoch != rec.TerminalOrbitEpoch;
        }

        private static bool TryRefreshStableTerminalSnapshot(
            Recording rec,
            Vessel vessel,
            bool isSceneExit,
            string logPrefix)
        {
            if (rec == null || vessel == null || !rec.TerminalStateValue.HasValue)
                return false;

            var ts = rec.TerminalStateValue.Value;
            ConfigNode freshSnapshot = FlightRecorder.TryBackupSnapshot(vessel);
            if (freshSnapshot == null)
            {
                ParsekLog.Verbose("Flight",
                    $"{logPrefix}: re-snapshot returned null for '{rec.RecordingId}' " +
                    $"(terminal={ts}, isSceneExit={isSceneExit}) — keeping stale snapshot");
                return false;
            }

            rec.VesselSnapshot = freshSnapshot;
            if (rec.GhostVisualSnapshot == null)
                rec.GhostVisualSnapshot = freshSnapshot.CreateCopy();
            rec.MarkFilesDirty();
            string orbitInfo = "";
            if ((ts == TerminalState.Orbiting || ts == TerminalState.SubOrbital)
                && vessel.orbit != null)
            {
                orbitInfo = string.Format(CultureInfo.InvariantCulture,
                    " orbit[body={0} sma={1:F1} ecc={2:F4} periAlt={3:F1} apoAlt={4:F1} inc={5:F2}]",
                    vessel.orbit.referenceBody?.name ?? vessel.mainBody?.name ?? "?",
                    vessel.orbit.semiMajorAxis,
                    vessel.orbit.eccentricity,
                    vessel.orbit.PeA,
                    vessel.orbit.ApA,
                    vessel.orbit.inclination);
            }
            ParsekLog.Info("Flight",
                $"{logPrefix} '{rec.RecordingId}' with stable terminal state {ts} " +
                $"(vessel.situation={vessel.situation}, isSceneExit={isSceneExit}){orbitInfo} [#289]");
            return true;
        }

        internal static ConfigNode NormalizeStableTerminalSnapshotForPersistence(
            ConfigNode freshSnapshot,
            TerminalState terminalState,
            CelestialBody body = null)
        {
            return LiveVesselSnapshot.NormalizeStableSnapshotForPersistence(
                freshSnapshot,
                terminalState,
                body,
                "stable-terminal snapshot persistence");
        }

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
            DestroyAllOverlapGhosts(index);
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
            ClearOrbitPlaybackCaches();
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
            ClearOrbitPlaybackCaches();
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

        /// <summary>
        /// Returns true if the recording should be discarded as a pad failure:
        /// duration &lt; 10s AND max distance from launch &lt; 30m.
        /// </summary>
        internal static bool IsPadFailure(double duration, double maxDistanceFromLaunch)
        {
            return duration < PadFailureDurationThresholdSeconds
                && maxDistanceFromLaunch < PadLocalizedDistanceThresholdMeters;
        }

        /// <summary>
        /// Recording-aware pad failure check. Keeps the legacy 3D distance rule, but
        /// also treats topple/drop launches as pad failures when their surface range
        /// and vertical displacement from launch both stayed within pad-local thresholds.
        /// </summary>
        internal static bool IsPadFailure(Recording rec)
        {
            if (rec == null)
                return false;

            double duration = rec.EndUT - rec.StartUT;
            return IsPadFailure(duration, rec.MaxDistanceFromLaunch)
                || (duration < PadFailureDurationThresholdSeconds && HasPadLocalizedMotionOverride(rec));
        }

        /// <summary>
        /// Returns true if the vessel never moved meaningfully from its launch position,
        /// regardless of duration. Covers vessels that sat idle on the pad while the
        /// player did other activities (EVA, vehicle switch, etc.).
        /// </summary>
        internal static bool IsIdleOnPad(double maxDistanceFromLaunch)
        {
            return maxDistanceFromLaunch < PadLocalizedDistanceThresholdMeters;
        }

        /// <summary>
        /// Recording-aware idle-on-pad check. Extends the legacy 3D max-distance rule
        /// with a pad-drop override so local collapse or topple noise does not
        /// promote a pad-local failure into merge UI. Pad evidence is a tree-level
        /// requirement (<see cref="TreePadEvidence"/>), not a per-recording one: EVA
        /// kerbals and background-only children carry no launch situation of their own.
        /// </summary>
        internal static bool IsIdleOnPad(Recording rec)
        {
            if (rec == null)
                return false;

            return IsIdleOnPad(rec.MaxDistanceFromLaunch)
                || HasPadLocalizedMotionOverride(rec);
        }
        /// <summary>
        /// Pure decision: the pad evidence a recording shows, or null when it shows none.
        /// The launch site is checked first and wins over any start situation: a normal
        /// launch recording starts airborne ("Flying") with a site, because the auto-record
        /// trigger fires when the vessel leaves PRELAUNCH, so a fizzled launch that never
        /// left the pad is still an idle-on-pad recording. Otherwise the evidence is a
        /// Prelaunch / Landed / Splashed start, then a Landed / Splashed terminal. An
        /// orbiting or flying start with no site and no surface terminal shows no evidence
        /// (the one-sample orbiting case). Returns the first matching reason: "launch-site",
        /// "start-prelaunch", "start-landed", "start-splashed", "terminal-landed" or
        /// "terminal-splashed".
        /// </summary>
        internal static string PadEvidenceReason(string startSituation, string launchSiteName, TerminalState? terminal)
        {
            if (!string.IsNullOrEmpty(launchSiteName))
                return "launch-site";
            if (string.Equals(startSituation, "Prelaunch", StringComparison.OrdinalIgnoreCase))
                return "start-prelaunch";
            if (string.Equals(startSituation, "Landed", StringComparison.OrdinalIgnoreCase))
                return "start-landed";
            if (string.Equals(startSituation, "Splashed", StringComparison.OrdinalIgnoreCase))
                return "start-splashed";
            if (terminal == TerminalState.Landed)
                return "terminal-landed";
            if (terminal == TerminalState.Splashed)
                return "terminal-splashed";
            return null;
        }

        /// <summary>
        /// Pure decision: true when <see cref="PadEvidenceReason(string, string, TerminalState?)"/> finds evidence.
        /// </summary>
        internal static bool HasPadEvidence(string startSituation, string launchSiteName, TerminalState? terminal)
        {
            return PadEvidenceReason(startSituation, launchSiteName, terminal) != null;
        }

        /// <summary>
        /// Pure decision: true when the recording's own start / site / terminal fields show pad evidence.
        /// </summary>
        internal static bool HasPadEvidence(Recording rec)
        {
            return rec != null
                && HasPadEvidence(rec.StartSituation, rec.LaunchSiteName, rec.TerminalStateValue);
        }

        /// <summary>
        /// Pure decision: the pad evidence of a flight's recordings, or null when none shows
        /// any. The first recording (in enumeration order) with evidence supplies the reason.
        /// Null entries are skipped.
        /// </summary>
        internal static string TreePadEvidence(IEnumerable<Recording> recordings)
        {
            if (recordings == null)
                return null;

            foreach (Recording rec in recordings)
            {
                if (rec == null)
                    continue;
                string reason = PadEvidenceReason(rec.StartSituation, rec.LaunchSiteName, rec.TerminalStateValue);
                if (reason != null)
                    return reason;
            }
            return null;
        }

        /// <summary>
        /// Tree-level pad-evidence gate shared by <see cref="IsTreeIdleOnPad(RecordingTree)"/>
        /// and <see cref="IsActiveTreeIdleOnPad"/>. Logs the verdict (tree, reason or "none")
        /// and returns true when at least one recording shows pad evidence.
        /// </summary>
        internal static bool TreeHasPadEvidenceForIdle(RecordingTree tree, string caller)
        {
            string reason = TreePadEvidence(tree?.Recordings?.Values);
            ParsekLog.Verbose("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}: tree='{1}' id={2} pad evidence={3}",
                    caller, tree?.TreeName ?? "<unnamed>", tree?.Id ?? "(none)", reason ?? "none"));
            return reason != null;
        }

        /// <summary>
        /// Pure decision for the destroyed-during-split single-recording path
        /// (<c>FallbackCommitSplitRecorder</c>): "pad failure" or "idle on pad" when the
        /// recording is within the pad thresholds AND shows pad evidence, else null. The pad
        /// rules stay tree-level wherever a tree is available (EVA and debris children carry
        /// no launch situation); this branch handles one recording, so the recording's own
        /// evidence is the gate. A one-sample recording has duration 0 and maxDist 0, so
        /// without the evidence check an orbiting recording destroyed within one sample
        /// interval is discarded.
        /// </summary>
        internal static string DecideDestroyedSplitRecordingDiscard(Recording rec)
        {
            if (rec == null || !HasPadEvidence(rec))
                return null;
            if (IsPadFailure(rec))
                return "pad failure";
            if (IsIdleOnPad(rec))
                return "idle on pad";
            return null;
        }

        private static bool HasPadLocalizedMotionOverride(Recording rec)
        {
            if (rec == null
                || rec.Points == null
                || rec.Points.Count < 2
                || rec.MaxDistanceFromLaunch < PadLocalizedDistanceThresholdMeters)
            {
                return false;
            }

            if (!TryGetPadLocalizedMotionMetrics(
                rec,
                out double maxSurfaceRangeFromLaunch,
                out double maxAbsoluteAltitudeDeltaFromLaunch))
            {
                return false;
            }

            bool localized =
                maxSurfaceRangeFromLaunch < PadLocalizedDistanceThresholdMeters &&
                maxAbsoluteAltitudeDeltaFromLaunch < PadLocalizedAltitudeDisplacementThresholdMeters;
            if (localized)
            {
                ParsekLog.Verbose("Flight",
                    $"Pad-localized motion override: rec='{rec.RecordingId ?? "(unknown)"}' " +
                    $"surfaceRange={maxSurfaceRangeFromLaunch:F1}m " +
                    $"maxAbsAltDelta={maxAbsoluteAltitudeDeltaFromLaunch:F1}m " +
                    $"maxDist3D={rec.MaxDistanceFromLaunch:F1}m");
            }
            return localized;
        }

        private static bool TryGetPadLocalizedMotionMetrics(
            Recording rec,
            out double maxSurfaceRangeFromLaunch,
            out double maxAbsoluteAltitudeDeltaFromLaunch)
        {
            maxSurfaceRangeFromLaunch = 0.0;
            maxAbsoluteAltitudeDeltaFromLaunch = 0.0;

            if (rec == null || rec.Points == null || rec.Points.Count < 2)
                return false;

            if (rec.TrackSections != null)
            {
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    if (rec.TrackSections[i].referenceFrame != ReferenceFrame.Absolute)
                        return false;
                }
            }

            var launchPoint = rec.Points[0];
            string launchBody = launchPoint.bodyName ?? "Kerbin";
            double bodyRadius = GetPadHeuristicBodyRadius(launchBody);
            double launchAltitude = launchPoint.altitude;

            for (int i = 1; i < rec.Points.Count; i++)
            {
                var pt = rec.Points[i];
                string pointBody = pt.bodyName ?? launchBody;
                if (!string.Equals(pointBody, launchBody, StringComparison.Ordinal))
                {
                    maxSurfaceRangeFromLaunch = double.PositiveInfinity;
                    maxAbsoluteAltitudeDeltaFromLaunch = double.PositiveInfinity;
                    return true;
                }

                double effectiveRadius = bodyRadius + Math.Max(0.0, (launchAltitude + pt.altitude) * 0.5);
                double surfaceRange = SpawnCollisionDetector.SurfaceDistance(
                    launchPoint.latitude,
                    launchPoint.longitude,
                    pt.latitude,
                    pt.longitude,
                    effectiveRadius);
                if (surfaceRange > maxSurfaceRangeFromLaunch)
                    maxSurfaceRangeFromLaunch = surfaceRange;

                double absoluteAltitudeDeltaFromLaunch = Math.Abs(pt.altitude - launchAltitude);
                if (absoluteAltitudeDeltaFromLaunch > maxAbsoluteAltitudeDeltaFromLaunch)
                    maxAbsoluteAltitudeDeltaFromLaunch = absoluteAltitudeDeltaFromLaunch;
            }

            return true;
        }

        private static double GetPadHeuristicBodyRadius(string bodyName)
        {
            try
            {
                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                if (body != null)
                    return body.Radius;
            }
            catch
            {
                // FlightGlobals is unavailable in unit tests; fall back to stock radii.
            }

            if (!string.IsNullOrEmpty(bodyName)
                && PadHeuristicBodyRadii.TryGetValue(bodyName, out double radius))
            {
                return radius;
            }

            return 600000.0;
        }

        /// <summary>
        /// Computes terrain clearance based on distance from the active vessel.
        /// Inside the physics bubble (2.3km): 0.5m (full-LOD terrain matches PQS).
        /// Outside: lerps from 2m at the bubble edge to 5m at 120km to compensate
        /// for low-LOD terrain mesh overshooting PQS height at ridges.
        /// </summary>
        internal static double ComputeTerrainClearance(double distanceToVessel)
        {
            return DistanceThresholds.GhostFlight.ComputeTerrainClearance(distanceToVessel);
        }

        /// <summary>
        /// Returns true if every recording in the tree qualifies as a pad failure AND the
        /// tree shows pad evidence. A tree with any non-pad-failure recording is not
        /// discarded. The evidence gate matters because a one-sample recording has duration 0
        /// and MaxDistanceFromLaunch 0 by construction, so the thresholds alone would read an
        /// orbiting tree destroyed within one sample interval as a pad failure.
        /// </summary>
        internal static bool IsTreePadFailure(RecordingTree tree)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return false;

            if (TreeHasPlacedGroundPartsForIdle(tree, "IsTreePadFailure")
                || TreeHasPlantedFlagsForIdle(tree, "IsTreePadFailure")
                || TreeMovesHeldKerbalForIdle(tree, "IsTreePadFailure")
                || TreeInteractsWithForeignVesselForIdle(tree, "IsTreePadFailure"))
                return false;

            foreach (var rec in tree.Recordings.Values)
            {
                if (!IsPadFailure(rec))
                    return false;
            }
            return TreeHasPadEvidenceForIdle(tree, "IsTreePadFailure");
        }

        /// <summary>
        /// Counts the <see cref="BranchPointType.GroundPartPlaced"/> branch points of
        /// <paramref name="tree"/>: each one is a vessel an EVA kerbal created by placing
        /// a ground part (Breaking Ground science, deployables). Pure, null-safe.
        /// </summary>
        internal static int CountPlacedGroundParts(RecordingTree tree)
        {
            if (tree?.BranchPoints == null) return 0;
            int count = 0;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                BranchPoint bp = tree.BranchPoints[i];
                if (bp != null && bp.Type == BranchPointType.GroundPartPlaced)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Counts the flags planted across every recording of <paramref name="tree"/>
        /// (<see cref="Recording.FlagEvents"/>). Pure, null-safe.
        /// </summary>
        internal static int CountPlantedFlags(RecordingTree tree)
        {
            if (tree?.Recordings == null) return 0;
            int count = 0;
            foreach (Recording rec in tree.Recordings.Values)
            {
                if (rec?.FlagEvents != null)
                    count += rec.FlagEvents.Count;
            }
            return count;
        }

        /// <summary>A converter event within this many seconds of its recording's start is a
        /// start seed (<see cref="PartStateSeeder"/>), not something the player did.</summary>
        internal const double ConverterSeedEpsilonSeconds = 1.0;

        /// <summary>
        /// Counts the converter part events (drills, ISRU, any BaseConverter) the player caused
        /// across every recording of <paramref name="tree"/>: ConverterActivated /
        /// ConverterDeactivated after the recording's start seed. Pure, null-safe.
        /// </summary>
        internal static int CountPlayerConverterToggles(RecordingTree tree)
        {
            if (tree?.Recordings == null) return 0;
            int count = 0;
            foreach (Recording rec in tree.Recordings.Values)
            {
                if (rec?.PartEvents == null || rec.PartEvents.Count == 0) continue;
                double seedUT = rec.StartUT + ConverterSeedEpsilonSeconds;
                for (int i = 0; i < rec.PartEvents.Count; i++)
                {
                    PartEvent ev = rec.PartEvents[i];
                    if ((ev.eventType == PartEventType.ConverterActivated
                            || ev.eventType == PartEventType.ConverterDeactivated)
                        && ev.ut > seedUT)
                        count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Idle-on-pad veto for converter work: a stationary miner or ISRU run is player work
        /// even when nothing moved 30 m. Logs the veto; returns true when the tree must not be
        /// classified idle. The live predicate calls this after flushing the recorder.
        /// </summary>
        internal static bool TreeHasConverterWorkForIdle(RecordingTree tree, string caller)
        {
            int toggles = CountPlayerConverterToggles(tree);
            if (toggles <= 0) return false;
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}: not idle - tree has {1} converter toggle(s) tree='{2}'",
                    caller, toggles, tree?.TreeName ?? "<unnamed>"));
            return true;
        }

        /// <summary>
        /// Counts the science-lab payouts (<see cref="GameStateEventType.ScienceLabCredited"/>:
        /// a lab transmission or a lab recovery) in <paramref name="events"/> tagged to any
        /// recording of <paramref name="tree"/>. Pure, null-safe.
        /// </summary>
        internal static int CountTaggedLabPayouts(
            RecordingTree tree, IReadOnlyList<GameStateEvent> events)
        {
            if (tree?.Recordings == null || tree.Recordings.Count == 0
                || events == null || events.Count == 0)
                return 0;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Recording> kv in tree.Recordings)
            {
                if (!string.IsNullOrEmpty(kv.Key)) ids.Add(kv.Key);
                if (!string.IsNullOrEmpty(kv.Value?.RecordingId)) ids.Add(kv.Value.RecordingId);
            }
            int count = 0;
            for (int i = 0; i < events.Count; i++)
            {
                GameStateEvent e = events[i];
                if (e.eventType == GameStateEventType.ScienceLabCredited
                    && !string.IsNullOrEmpty(e.recordingId)
                    && ids.Contains(e.recordingId))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Idle-on-pad veto for a science-lab payout (owner ruling 2026-10-17): the lab's
        /// science is banked in the R&amp;D pool the moment it pays out, so the flight is
        /// player work even when nothing moved 30 m. Reads the live event store, the same
        /// source a discard re-homes lab credits from. Logs the veto; returns true when the
        /// tree must not be classified idle.
        /// </summary>
        internal static bool TreeHasLabPayoutsForIdle(RecordingTree tree, string caller)
        {
            return TreeHasLabPayoutsForIdle(tree, caller, GameStateStore.Events);
        }

        internal static bool TreeHasLabPayoutsForIdle(
            RecordingTree tree, string caller, IReadOnlyList<GameStateEvent> events)
        {
            int payouts = CountTaggedLabPayouts(tree, events);
            if (payouts <= 0) return false;
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}: not idle - tree has {1} lab payout(s) tree='{2}'",
                    caller, payouts, tree?.TreeName ?? "<unnamed>"));
            return true;
        }

        /// <summary>
        /// Idle-on-pad veto for the recovery half of the lab ruling (owner ruling 2026-10-17):
        /// a flight Recover pays the lab's stored science at the Space Center, AFTER every
        /// idle-on-pad check has run (the LoadScene-prefix fast path and the Space Center
        /// OnLoad), and that payout is stored untagged, so
        /// <see cref="TreeHasLabPayoutsForIdle(RecordingTree, string)"/> can never see it.
        /// Reads the armed <see cref="InFlightRecoveryRequest"/> (lab science captured from
        /// the live vessel) or, after the finalize stamped the tip Recovered, the request's
        /// per-recording lab mark. Logs the veto; returns true when the tree must not be
        /// classified idle.
        /// </summary>
        internal static bool TreeHasPendingLabRecoveryForIdle(RecordingTree tree, string caller)
        {
            return TreeHasPendingLabRecoveryForIdle(
                tree, caller, InFlightRecoveryRequest.Armed,
                InFlightRecoveryRequest.LabScienceAwaitingRecovery);
        }

        internal static bool TreeHasPendingLabRecoveryForIdle(
            RecordingTree tree, string caller,
            InFlightRecoveryRequest.Request armedRequest,
            IReadOnlyDictionary<string, float> stampedLabScience)
        {
            float science = InFlightRecoveryRequest.PendingRecoveryLabScience(
                tree, armedRequest, stampedLabScience);
            if (!(science > 0f)) return false;
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}: not idle - recovery pending with lab science {1:F2} tree='{2}'",
                    caller, science, tree?.TreeName ?? "<unnamed>"));
            return true;
        }

        /// <summary>
        /// Idle-on-pad veto for a planted flag: the flag is a vessel the player made,
        /// so the flight is player work even when nothing moved 30 m. Logs the veto;
        /// returns true when the tree must not be classified idle. The live predicate
        /// must call this after flushing the recorder, which buffers flag events.
        /// </summary>
        internal static bool TreeHasPlantedFlagsForIdle(RecordingTree tree, string caller)
        {
            int flags = CountPlantedFlags(tree);
            if (flags <= 0) return false;
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}: not idle - tree has {1} planted flag(s) tree='{2}'",
                    caller, flags, tree?.TreeName ?? "<unnamed>"));
            return true;
        }

        /// <summary>
        /// Test seam for <see cref="TreeMovesHeldKerbalForIdle"/>: replaces the "does the
        /// committed timeline hold this kerbal now" read (default
        /// <see cref="KerbalsModule.IsReservedNow"/> on <see cref="LedgerOrchestrator.Kerbals"/>).
        /// </summary>
        internal static Func<string, bool> HeldKerbalProbeForTesting;

        /// <summary>
        /// The first EVA recording of <paramref name="tree"/> whose kerbal
        /// <paramref name="isHeld"/> reports held, or null. Pure, null-safe.
        /// </summary>
        internal static Recording FindEvaOfHeldKerbal(RecordingTree tree, Func<string, bool> isHeld)
        {
            if (tree?.Recordings == null || isHeld == null) return null;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null || string.IsNullOrEmpty(rec.EvaCrewName)) continue;
                if (isHeld(rec.EvaCrewName)) return rec;
            }
            return null;
        }

        /// <summary>
        /// Idle-on-pad veto for a held kerbal leaving his vessel: a kerbal the committed
        /// timeline holds (an earlier mission left him aboard, so his hold is open-ended)
        /// stepped out on EVA in this flight. Only this flight's recordings can end that
        /// hold (the joined-vessel claim, then the EVA's own recovery or end), and stock has
        /// already moved him, so discarding it would leave him held forever (owner ruling
        /// 2026-10-25, todo CX4-IDLE-DISCARDED-FLIGHT-LEAVES-THE-HOLD-OPEN). Logs the veto;
        /// returns true when the tree must not be classified idle.
        /// </summary>
        internal static bool TreeMovesHeldKerbalForIdle(RecordingTree tree, string caller)
        {
            Func<string, bool> isHeld = HeldKerbalProbeForTesting
                ?? (name => LedgerOrchestrator.Kerbals?.IsReservedNow(name) ?? false);
            Recording eva = FindEvaOfHeldKerbal(tree, isHeld);
            if (eva == null) return false;
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}: not idle - held kerbal '{1}' left his vessel on EVA (recording {2}) tree='{3}'",
                    caller, eva.EvaCrewName, eva.RecordingId ?? "(none)", tree?.TreeName ?? "<unnamed>"));
            return true;
        }

        /// <summary>
        /// The first branch point of <paramref name="tree"/> at which the flight interacted
        /// with a vessel from outside the tree, or null: an InventoryTransfer (always a foreign
        /// partner), or a Dock / Board whose stamped target is no vessel the tree carried
        /// before the merge (no recording outside the branch point's own children has its
        /// pid). A board or dock back onto the tree's own vessel stamps no target or names a
        /// tree vessel, so it is not an interaction. Pure, null-safe.
        /// </summary>
        internal static BranchPoint FindForeignVesselInteraction(RecordingTree tree)
        {
            if (tree?.BranchPoints == null) return null;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                BranchPoint bp = tree.BranchPoints[i];
                if (bp == null) continue;
                if (bp.Type == BranchPointType.InventoryTransfer)
                    return bp;
                if (bp.Type != BranchPointType.Dock && bp.Type != BranchPointType.Board)
                    continue;
                if (bp.TargetVesselPersistentId == 0)
                    continue;
                if (!TreeCarriesVesselOutsideChildren(tree, bp, bp.TargetVesselPersistentId))
                    return bp;
            }
            return null;
        }

        private static bool TreeCarriesVesselOutsideChildren(RecordingTree tree, BranchPoint bp, uint pid)
        {
            if (tree.Recordings == null) return false;
            foreach (var kvp in tree.Recordings)
            {
                Recording rec = kvp.Value;
                if (rec == null || rec.VesselPersistentId != pid) continue;
                if (bp.ChildRecordingIds != null && bp.ChildRecordingIds.Contains(kvp.Key)) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Idle-on-pad veto for an interaction with another vessel: a kerbal of this flight
        /// boarded or took a seat on, docked with, or moved cargo to or from a vessel outside
        /// the tree (owner ruling 2026-10-25: the interaction is player work even when nothing
        /// moved 30 m; the vessel the kerbal came from stays recorded as his origin). Logs the
        /// veto; returns true when the tree must not be classified idle.
        /// </summary>
        internal static bool TreeInteractsWithForeignVesselForIdle(RecordingTree tree, string caller)
        {
            BranchPoint bp = FindForeignVesselInteraction(tree);
            if (bp == null) return false;
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}: not idle - {1} with a vessel outside the flight (bp={2} targetPid={3}) tree='{4}'",
                    caller, bp.Type, bp.Id ?? "(none)", bp.TargetVesselPersistentId,
                    tree?.TreeName ?? "<unnamed>"));
            return true;
        }

        /// <summary>
        /// Idle-on-pad veto shared by <see cref="IsTreeIdleOnPad(RecordingTree)"/> and
        /// <see cref="IsActiveTreeIdleOnPad"/>: a tree whose kerbal placed ground parts
        /// created vessels, so it is player work even when nothing moved 30 m (a
        /// science cluster set up beside a lander). Logs the veto; returns true when
        /// the tree must not be classified idle.
        /// </summary>
        internal static bool TreeHasPlacedGroundPartsForIdle(RecordingTree tree, string caller)
        {
            int placed = CountPlacedGroundParts(tree);
            if (placed <= 0) return false;
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}: not idle - tree has {1} placed ground part(s) tree='{2}'",
                    caller, placed, tree?.TreeName ?? "<unnamed>"));
            return true;
        }

        /// <summary>
        /// Returns true if every recording in the tree is idle-on-pad (never moved
        /// more than 30m from launch, and shows pad evidence per <see cref="IsIdleOnPad(Recording)"/>).
        /// A tree with any non-idle recording is not discarded.
        /// </summary>
        internal static bool IsTreeIdleOnPad(RecordingTree tree)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return false;

            if (TreeHasPlacedGroundPartsForIdle(tree, "IsTreeIdleOnPad")
                || TreeHasPlantedFlagsForIdle(tree, "IsTreeIdleOnPad")
                || TreeMovesHeldKerbalForIdle(tree, "IsTreeIdleOnPad")
                || TreeInteractsWithForeignVesselForIdle(tree, "IsTreeIdleOnPad")
                || TreeHasConverterWorkForIdle(tree, "IsTreeIdleOnPad")
                || TreeHasLabPayoutsForIdle(tree, "IsTreeIdleOnPad")
                || TreeHasPendingLabRecoveryForIdle(tree, "IsTreeIdleOnPad"))
                return false;

            bool anyHasPoints = false;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec.Points != null && rec.Points.Count > 0)
                    anyHasPoints = true;
                if (!IsIdleOnPad(rec))
                {
                    ParsekLog.Verbose("Flight",
                        string.Format(CultureInfo.InvariantCulture,
                            "IsTreeIdleOnPad: '{0}' maxDist={1:F1}m — not idle",
                            rec.VesselName, rec.MaxDistanceFromLaunch));
                    return false;
                }
            }
            // Bug #290d: a tree where no recording has trajectory points is data-loss
            // (e.g., sidecar epoch mismatch), not idle-on-pad. Don't auto-discard.
            if (!anyHasPoints)
            {
                ParsekLog.Verbose("Flight",
                    $"IsTreeIdleOnPad: all {tree.Recordings.Count} recordings have 0 points — " +
                    "cannot determine idle, returning false");
                return false;
            }
            if (!TreeHasPadEvidenceForIdle(tree, "IsTreeIdleOnPad"))
                return false;
            ParsekLog.Verbose("Flight",
                $"IsTreeIdleOnPad: all {tree.Recordings.Count} recordings within 30m — idle on pad");
            return true;
        }

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

        // M4b phasing-knob body-fixed derotation: the time shift (seconds) of the positioning call
        // currently on the stack, read from GhostPlaybackState.bodyFixedShiftSeconds at the
        // IGhostPositioner entries (InterpolateAndPosition / PositionAtPoint / PositionLoop) and
        // consumed by the private InterpolateAndPosition / PositionGhostAt when resolving Absolute
        // body-fixed points (vacuum burn arcs replayed off the rotation-aligned grid render rotated
        // with the planet otherwise; the 2026-06-11 playtest's 46-degree teleports). Per-call
        // ambient with try/finally reset rather than threading a parameter through the six-deep
        // positioning chain; single-threaded Unity main loop, and 0 (the identity) for every
        // non-knob caller.
        private double activeBodyFixedShiftSeconds;

        // Literal floor returned by the immediate-clearance resolver when it
        // cannot compute a distance-aware value. Matches the legacy pre-#262
        // hardcoded floor — callers that hit this branch silently regress to
        // the old behavior, so ResolveImmediateLandedGhostClearanceMeters
        // emits a rate-limited warning naming the offending ghost (#373).
        internal const double ImmediateLandedGhostClearanceFallbackMeters = 0.5;

        // Per-member last re-aim ghost frame, for seam-teleport detection (segment change -> log the jump).
        private readonly Dictionary<int, (int segIdx, Vector3 pos, double loopUT)> reaimLastGhostFrame
            = new Dictionary<int, (int, Vector3, double)>();

        // Per-member last orbit-positioned ghost frame for the mode-agnostic
        // loop-seam teleport instrument (Tier-C, ghostRenderTracing-gated;
        // faithful AND re-aimed members). Unity frame number rides along so a
        // hide/show gap suppresses the stale-prev comparison.
        private readonly Dictionary<int, (int segIdx, Vector3 pos, double loopUT, double currentUT, int frame)>
            loopSeamLastGhostFrame
                = new Dictionary<int, (int, Vector3, double, double, int)>();

        // Generous upper bound on a loop ghost's own speed (m/s), added to the
        // segment body's orbital speed for the warp-scaled expected-motion term
        // of the teleport threshold. Interplanetary coast speeds run ~3-12 km/s.
        internal const double LoopSeamGhostSpeedAllowanceMps = 15_000.0;

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
        private Dictionary<long, Orbit> orbitCache = new Dictionary<long, Orbit>();
        private readonly Dictionary<string, PredictedOrbitTailContinuityCacheEntry> predictedOrbitTailContinuityCache =
            new Dictionary<string, PredictedOrbitTailContinuityCacheEntry>();

        // Tracks which (recording index, anchor PID) combos have been logged for loop-relative playback start.
        private readonly HashSet<long> loggedRelativeStart = new HashSet<long>();
        private readonly HashSet<string> loggedRecordedRelativeStart =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> loggedRelativeBodyFixedPrimaryStart =
            new HashSet<string>(StringComparer.Ordinal);
        // Tracks which anchor-not-found warnings have been logged
        private readonly HashSet<long> loggedAnchorNotFound = new HashSet<long>();
        private readonly HashSet<string> recordedAnchorSeenRecordingIds =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> recordedRelativeAnchorVisited =
            new HashSet<string>(StringComparer.Ordinal);
        // Phase 4 HR-9: per-(recordingId, sectionIndex) dedup so an unrecognised
        // FrameTag warns once per session instead of every per-frame dispatch.
        // Cleared in the same Cleanup hooks as the other per-session log state.
        private readonly HashSet<string> unknownFrameTagWarned =
            new HashSet<string>(StringComparer.Ordinal);

        // ===== Recorded-coordinate playback helpers =====
        //
        private const double PointHermiteMaxDeviationCapMeters = 250.0;
        private const double PointHermiteMinDeviationMeters = 10.0;
        private const double PointHermiteDeviationFraction = 0.05;
        private const bool NormalPlaybackSplinePositioningEnabled = false;
        private const bool NormalPlaybackPointHermiteEnabled = false;

        private const long CheckpointOrbitCacheBase = long.MinValue;
        private const long CheckpointOrbitCacheRecordingStride = 1000000000000L;
        private const long CheckpointOrbitCacheSectionStride = 1000000L;
        private const long CheckpointOrbitCacheMaxRecordingIndex =
            (long.MaxValue - CheckpointOrbitCacheRecordingStride + 1) / CheckpointOrbitCacheRecordingStride;

        private const double RecordedAnchorFallbackGapWarnThresholdSeconds = 5.0;

        internal const double RecordedRelativeOffsetSpikeNeighborMaxMeters = 5.0;
        internal const double RecordedRelativeOffsetSpikeMinDeviationMeters = 15.0;

        private static bool IsFinite(Vector3d value)
        {
            return !double.IsNaN(value.x) && !double.IsInfinity(value.x)
                && !double.IsNaN(value.y) && !double.IsInfinity(value.y)
                && !double.IsNaN(value.z) && !double.IsInfinity(value.z);
        }

        // ────────────────────────────────────────────────────────────
        //  Phase 6d: Ghost labels, spawn warnings, chain status
        // ────────────────────────────────────────────────────────────

        private GUIStyle ghostLabelStyle;

        /// <summary>
        /// Accessor for active ghost chains — used by ParsekUI for chain status display.
        /// Returns null when no chains are active.
        /// </summary>
        internal Dictionary<uint, GhostChain> ActiveGhostChains => activeGhostChains;

        private int ghostChainEvaluationCount;

        /// <summary>
        /// How many times THIS flight-scene instance has run
        /// <see cref="EvaluateAndApplyGhostChains"/>, on any of its exits. Zero means the
        /// scene never derived its chain set (the component is re-created per scene, so a
        /// load always starts at zero). Read by the automation seam's
        /// <c>ListHandles kind=chains</c> to tell "derived an empty set" from "never derived".
        /// </summary>
        internal int GhostChainEvaluationCount => ghostChainEvaluationCount;

        void Log(string message) => ParsekLog.Verbose("Flight", message);
        void ScreenMessage(string message, float duration) => ParsekLog.ScreenMessage(message, duration);
    }
}
