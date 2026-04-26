using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using ClickThroughFix;
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
    // off the raw list to WatchModeController / ChainSegmentManager / policy
    // events that share the same index space. Routing through
    // EffectiveState.ComputeERS() file-wide would require simultaneous
    // refactors across GhostPlaybackEngine, ghostStates, and every ParsekFlight
    // -> policy event handoff; deferred to a recording-id-keyed pass.
    // TODO(phase 6+): migrate ParsekFlight ghost-state dicts + event indices
    // to recording-id keying and route every read here through ComputeERS().
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ParsekFlight : MonoBehaviour, IGhostPositioner
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

        internal enum CommittedSpawnedVesselRestoreAction
        {
            None,
            PromoteFromBackground,
            ResumeActiveRecording
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
        /// #431: true when the flight scene has a live tree with a recorder attached.
        /// Used by <see cref="GameStateRecorder.Emit"/> to decide whether an in-flight
        /// untagged event is a drift signal worth a warn log.
        /// </summary>
        internal static bool HasLiveRecorderForTagging()
            => Instance != null && Instance.activeTree != null && Instance.recorder != null && Instance.recorder.IsRecording;

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
        /// Returns true when <paramref name="recordingId"/> belongs to the currently-live
        /// active tree. Used by the post-epoch timeline visibility helpers so events from
        /// a reverted-away flight stay hidden until the matching quickload-resume tree is
        /// restored.
        /// </summary>
        internal static bool IsActiveTreeRecordingId(string recordingId)
            => !string.IsNullOrEmpty(recordingId)
                && Instance?.activeTree?.Recordings != null
                && Instance.activeTree.Recordings.ContainsKey(recordingId);

        internal const string MODID = "Parsek_NS";
        internal const string MODNAME = "Parsek";

        /// <summary>Above this altitude, skip the per-frame terrain clamp (no terrain risk in flight).</summary>
        const double TerrainClampAltitudeLimit = 10000;

        #region State

        // Recording (delegated to FlightRecorder)
        private FlightRecorder recorder;
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
        private MissedVesselSwitchRecoveryDiagnosticContext currentVesselSwitchRecoveryDiagnosticContext;

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
        private const float ProximityCheckIntervalSec = 1.5f;
        internal const double NearbySpawnRadius = 250.0;
        // Real Spawn Control: maximum relative speed (m/s) between the active vessel and a
        // ghost for the ghost to qualify as a spawn candidate. Tight enough to require a
        // deliberate rendezvous (matched orbits, station-keeping) before fast-forward is offered.
        // Computed frame-agnostically as d(active_pos - ghost_pos)/dt across consecutive
        // proximity scans — see CollectNearbySpawnCandidates.
        internal const double MaxRelativeSpeed = 2.0;
        // Real Spawn Control: outer "show in list" bounds. Ghosts within these — but outside
        // NearbySpawnRadius / MaxRelativeSpeed — appear in the window with the FF button
        // disabled and red distance/speed text, so the player can see what is blocking warp
        // (closing too fast, still too far) before they even reach the inner gate.
        internal const double NearbySpawnListRadius = 1000.0;       // 4× FF radius
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

        private struct GhostPosEntry
        {
            public GameObject ghost;
            public GhostPosMode mode;

            // PointInterp fields
            public CelestialBody bodyBefore;
            public CelestialBody bodyAfter;
            public double latBefore, lonBefore, altBefore;
            public double latAfter, lonAfter, altAfter;
            public float t;
            public double pointUT;
            public Quaternion interpolatedRot;

            // SinglePoint fields (also used body/lat/lon/alt from "before")
            // (uses bodyBefore, latBefore, lonBefore, altBefore, pointUT, interpolatedRot)


            // Orbit fields
            public long orbitCacheKey;
            public double orbitUT;

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
            public int relativeRecordingFormatVersion;
        }

        private readonly List<GhostPosEntry> ghostPosEntries = new List<GhostPosEntry>();

        // Auto-record: EVA from pad triggers recording after vessel switch completes
        private bool pendingAutoRecord = false;
        private PostSwitchAutoRecordState postSwitchAutoRecord;

        // Chain segment management (T26 extraction)
        private ChainSegmentManager chainManager;

        // Boarding confirmation via onCrewBoardVessel
        private uint pendingBoardingTargetPid; // vessel PID from onCrewBoardVessel, 0 = none
        private int boardingConfirmFrames;     // frames since boarding event (auto-clear after 10)

        // Pending dock/undock transitions (set by event handlers, consumed by Update)
        private uint pendingDockMergedPid;          // merged vessel pid, 0 = no pending dock
        private bool pendingDockAsTarget;           // true if our vessel was the dock target (no pid change)
        private int dockConfirmFrames;              // frame counter for confirmation window (auto-clear after 5)
        private uint pendingUndockOtherPid;         // pid of vessel that split off, 0 = no pending undock
        private int undockConfirmFrames;            // frame counter for confirmation window

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
        /// Captures a structured snapshot of every recorder-relevant field at the
        /// current instant. Used by <see cref="ParsekLog.RecState"/> emit sites for
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
                chainManager,
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

            ParsekLog.RecState("FlushRecorderIntoActiveTree:entry", CaptureRecorderState());
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
            ParsekLog.RecState("FlushRecorderIntoActiveTree:post", CaptureRecorderState());
        }

        // Debris persistence enforcement: saved original value when Parsek overrides it
        private const int MinDebrisForRecording = 10;
        private int savedMaxPersistentDebris = -1;
        private bool debrisOverrideActive;

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

        // Crash coalescer: groups rapid structural splits into single BREAKUP events
        private CrashCoalescer crashCoalescer = new CrashCoalescer();

        // Merge event detection (tree mode)
        private bool pendingTreeDockMerge;           // true when a tree dock merge is pending
        private uint pendingDockAbsorbedPid;         // PID of absorbed vessel in dock merge
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
        // Camera follow (watch mode) — extracted to WatchModeController
        private WatchModeController watchMode;

        // UI
        private Rect windowRect = new Rect(20, 100, 250, 250);
        private bool showUI = false;
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
        internal void PositionGhostAtForWatch(GameObject ghost, TrajectoryPoint point) => PositionGhostAt(ghost, point);

        // Cached per-frame allocations for engine path (avoid GC pressure)
        private readonly List<IPlaybackTrajectory> cachedTrajectories = new List<IPlaybackTrajectory>();
        private TrajectoryPlaybackFlags[] cachedFlags;
        private readonly HashSet<string> activeGhostSkipReasonLogIdentities = new HashSet<string>();

        #endregion

        #region Public Accessors (for ParsekUI)

        public bool IsRecording => recorder?.IsRecording ?? false;
        public bool IsPlaying => isPlaying;
        public int TimelineGhostCount => engine.GhostCount;
        public GameObject PreviewGhost => ghostObject;
        public Dictionary<int, GameObject> TimelineGhosts
        {
            get
            {
                int currentFrame = Time.frameCount;
                if (cachedTimelineGhostsFrame != currentFrame)
                {
                    cachedTimelineGhosts.Clear();
                    foreach (var kv in ghostStates)
                        cachedTimelineGhosts[kv.Key] = kv.Value.ghost;
                    cachedTimelineGhostsFrame = currentFrame;
                    ParsekLog.VerboseRateLimited("Flight", "timeline-ghosts-cache",
                        $"TimelineGhosts: rebuilt cache, {cachedTimelineGhosts.Count} ghosts (frame {currentFrame})");
                }
                return cachedTimelineGhosts;
            }
        }
        public bool HasActiveChain => chainManager?.HasActiveChain ?? false;
        public bool HasActiveTree => activeTree != null;

        // Camera follow (watch mode) — forwarded from WatchModeController
        internal bool IsWatchingGhost => watchMode.IsWatchingGhost;
        internal int WatchedRecordingIndexForDiagnostics => watchMode != null
            ? watchMode.WatchedRecordingIndex
            : -1;
        internal long WatchedLoopCycleIndexForDiagnostics => watchMode != null
            ? watchMode.WatchedLoopCycleIndex
            : -1;
        internal int WatchedRecordingIndex => watchMode.WatchedRecordingIndex;
        internal string DescribeWatchFocusForLogs() => watchMode != null
            ? watchMode.DescribeWatchFocusForLogs()
            : "watch=uninitialized";
        internal string DescribeWatchEligibilityForLogs(int index) => watchMode != null
            ? watchMode.DescribeWatchEligibilityForLogs(index)
            : $"watchEval(rec=#{index} unavailable=watchMode-null)";
        internal bool ExitWatchModeBeforeTimelineGhostCleanup(string context)
        {
            bool isWatchingGhost = watchMode != null && watchMode.IsWatchingGhost;
            string focus = watchMode != null
                ? watchMode.DescribeWatchFocusForLogs()
                : "watch=uninitialized";
            return ExitWatchModeBeforeTimelineGhostCleanup(
                isWatchingGhost,
                watchMode != null
                    ? new Action<bool>(skipCameraRestore => watchMode.ExitWatchMode(skipCameraRestore))
                    : null,
                isWatchingGhost
                    ? new Action(() => RetargetStockCamerasBeforeTimelineGhostCleanup(context, focus))
                    : null,
                context,
                focus);
        }

        internal static bool ExitWatchModeBeforeTimelineGhostCleanup(
            bool isWatchingGhost,
            Action exitWatchMode,
            Action detachStockCameraTarget,
            string context,
            string watchFocusForLogs = null)
        {
            return ExitWatchModeBeforeTimelineGhostCleanup(
                isWatchingGhost,
                exitWatchMode != null
                    ? new Action<bool>(_ => exitWatchMode())
                    : null,
                detachStockCameraTarget,
                context,
                watchFocusForLogs);
        }

        internal static bool ExitWatchModeBeforeTimelineGhostCleanup(
            bool isWatchingGhost,
            Action<bool> exitWatchMode,
            Action detachStockCameraTarget,
            string context,
            string watchFocusForLogs = null)
        {
            string cleanupContext = string.IsNullOrEmpty(context)
                ? "timeline-ghost-cleanup"
                : context;
            string focus = string.IsNullOrEmpty(watchFocusForLogs)
                ? "watch=unknown"
                : watchFocusForLogs;

            if (!isWatchingGhost)
            {
                ParsekLog.Verbose("CameraFollow",
                    $"Watch cleanup guard: no active watch mode before {cleanupContext} ({focus})");
                return false;
            }

            if (exitWatchMode == null)
            {
                ParsekLog.Warn("CameraFollow",
                    $"Watch cleanup guard: active watch mode could not be exited before {cleanupContext} ({focus})");
                return false;
            }

            ParsekLog.Info("CameraFollow",
                $"Exiting watch mode before timeline ghost cleanup: context={cleanupContext} skipCameraRestore=true {focus}");
            detachStockCameraTarget?.Invoke();
            exitWatchMode(true);
            return true;
        }

        private void RetargetStockCamerasBeforeTimelineGhostCleanup(
            string context,
            string watchFocusForLogs)
        {
            string cleanupContext = string.IsNullOrEmpty(context)
                ? "timeline-ghost-cleanup"
                : context;
            string focus = string.IsNullOrEmpty(watchFocusForLogs)
                ? "watch=unknown"
                : watchFocusForLogs;
            FlightCamera flightCamera = FlightCamera.fetch;
            Vessel activeVessel = FlightGlobals.ActiveVessel;

            if (flightCamera != null
                && activeVessel != null
                && activeVessel.gameObject != null)
            {
                flightCamera.SetTargetVessel(activeVessel);
                ParsekLog.Verbose("CameraFollow",
                    $"Watch cleanup guard: FlightCamera retargeted to active vessel '{activeVessel.vesselName}' before {cleanupContext} ({focus})");
            }
            else
            {
                ParsekLog.Warn("CameraFollow",
                    $"Watch cleanup guard: could not retarget FlightCamera before {cleanupContext} ({focus}) " +
                    $"camera={(flightCamera != null)} activeVessel={(activeVessel != null)} " +
                    $"activeVesselGo={(activeVessel != null && activeVessel.gameObject != null)}");
            }

            if (!MapView.MapIsEnabled)
                return;

            if (PlanetariumCamera.fetch != null
                && activeVessel != null
                && activeVessel.mapObject != null)
            {
                PlanetariumCamera.fetch.SetTarget(activeVessel.mapObject);
                ParsekLog.Verbose("GhostMap",
                    $"Watch cleanup guard: PlanetariumCamera retargeted to active vessel '{activeVessel.vesselName}' before {cleanupContext}");
                return;
            }

            ParsekLog.Verbose("GhostMap",
                $"Watch cleanup guard: skipped PlanetariumCamera retarget before {cleanupContext} " +
                $"map={MapView.MapIsEnabled} camera={(PlanetariumCamera.fetch != null)} " +
                $"activeVessel={(activeVessel != null)} mapObject={(activeVessel != null && activeVessel.mapObject != null)}");
        }

        #endregion

        #region Unity Lifecycle

        void Start()
        {
            Instance = this;
            Log("Parsek Flight loaded.");

            GameEvents.onGameSceneLoadRequested.Add(OnSceneChangeRequested);
            GameEvents.onFlightReady.Add(OnFlightReady);
            GameEvents.onVesselWillDestroy.Add(OnVesselWillDestroy);
            GameEvents.onVesselSituationChange.Add(OnVesselSituationChange);
            GameEvents.onCrewOnEva.Add(OnCrewOnEva);
            GameEvents.onCrewBoardVessel.Add(OnCrewBoardVessel);
            GameEvents.onVesselGoOnRails.Add(OnVesselGoOnRails);
            GameEvents.onVesselGoOffRails.Add(OnVesselGoOffRails);
            GameEvents.onVesselLoaded.Add(OnVesselLoaded);
            GameEvents.onVesselUnloaded.Add(OnVesselUnloaded);
            GameEvents.onVesselSOIChanged.Add(OnVesselSOIChanged);
            GameEvents.onPartCouple.Add(OnPartCouple);
            GameEvents.onPartUndock.Add(OnPartUndock);
            GameEvents.onGroundSciencePartDeployed.Add(OnGroundSciencePartDeployed);
            GameEvents.onGroundSciencePartRemoved.Add(OnGroundSciencePartRemoved);
            GameEvents.onVesselChange.Add(OnVesselSwitchComplete);
            GameEvents.onVesselWasModified.Add(OnVesselWasModified);
            GameEvents.onTimeWarpRateChanged.Add(OnTimeWarpRateChanged);
            MergeDialog.OnTreeCommitted += OnTreeCommittedFromMergeDialog;
            GameEvents.afterFlagPlanted.Add(OnAfterFlagPlanted);
            GameEvents.onGamePause.Add(OnGamePause);
            GameEvents.onGameUnpause.Add(OnGameUnpause);

            ui = new ParsekUI(this);

            // Chain segment management (T26 extraction)
            chainManager = new ChainSegmentManager();

            // Camera follow (watch mode)
            watchMode = new WatchModeController(this);

            // Ghost playback engine + policy (T25 extraction)
            engine = new GhostPlaybackEngine(this);
            engine.OnLoopCameraAction += watchMode.HandleLoopCameraAction;
            engine.OnOverlapCameraAction += watchMode.HandleOverlapCameraAction;
            engine.IsWatchedGhostStateResolver = (recordingIndex, state) =>
                watchMode != null && watchMode.IsWatchedGhostState(recordingIndex, state);
            engine.ResolvePlaybackDistanceOverride = ResolvePlaybackDistanceForEngine;
            engine.ResolvePlaybackActiveVesselDistanceOverride = ResolvePlaybackActiveVesselDistanceForEngine;
            policy = new ParsekPlaybackPolicy(engine, this);

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
            // After OnSceneChangeRequested, the scene is tearing down — skip all processing
            // to prevent ghost spawns and other work into the dying scene.
            if (sceneChangeInProgress) return;

            ClearStaleConfirmations();
            HandleMissedVesselSwitchRecovery();

            // Gloops: auto-commit if vessel switch auto-stopped the recorder
            CheckGloopsAutoStoppedByVesselSwitch();

            HandleTreeDockMerge();
            HandleDockUndockCommitRestart();

            // Chain: auto-commit previous segment when recording stopped (EVA exit)
            // VesselSnapshot nulled in CommitChainSegment — mid-chain segments are ghost-only.
            if (chainManager.PendingContinuation && !chainManager.PendingIsBoarding &&
                recorder != null && !recorder.IsRecording && recorder.CaptureAtStop != null)
            {
                if (!chainManager.CommitChainSegment(recorder, chainManager.PendingEvaName))
                    pendingAutoRecord = false;
                recorder = null;
                // pendingAutoRecord is still true on success — will start child EVA recording next
            }

            HandleTreeBoardMerge();
            HandleChainBoardingTransition();

            // Bug #51: vessel-switch auto-stop loses chain ID.
            // HandleVesselSwitchDuringRecording (in FlightRecorder) builds CaptureAtStop
            // but cannot access ParsekFlight's chain fields. Commit the segment as a
            // proper chain member and terminate the chain (vessel was switched away).
            HandleVesselSwitchChainTermination();

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

            // Skip auto-record and chain handlers while a split is being processed
            if (pendingSplitInProgress)
            {
                if (ParsekLog.IsVerboseEnabled)
                    ParsekLog.VerboseRateLimited("Flight", "update_split_suppressed", "Update suppressed: pendingSplitInProgress");
                // Still run playback, input, and continuation sampling
                chainManager.UpdateContinuationSampling();
                chainManager.UpdateUndockContinuationSampling();
                HandleInput();
                if (isPlaying) UpdatePlayback();
                UpdateTimelinePlaybackViaEngine();
                return;
            }

            HandleDeferredAutoRecordEva();

            chainManager.UpdateContinuationSampling();
            chainManager.UpdateUndockContinuationSampling();

            HandleInput();

            if (isPlaying)
            {
                UpdatePlayback();
            }

            UpdateTimelinePlaybackViaEngine();
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

            for (int i = 0; i < ghostPosEntries.Count; i++)
            {
                var e = ghostPosEntries[i];
                if (e.ghost == null || !e.ghost.activeSelf) continue;

                switch (e.mode)
                {
                    case GhostPosMode.PointInterp:
                    {
                        if (e.bodyBefore == null || e.bodyAfter == null) break;
                        Vector3d posBefore = e.bodyBefore.GetWorldSurfacePosition(
                            e.latBefore, e.lonBefore, e.altBefore);
                        Vector3d posAfter = e.bodyAfter.GetWorldSurfacePosition(
                            e.latAfter, e.lonAfter, e.altAfter);
                        Vector3d pos = Vector3d.Lerp(posBefore, posAfter, e.t);
                        e.ghost.transform.position = pos;
                        e.ghost.transform.rotation = e.bodyBefore.bodyTransform.rotation * e.interpolatedRot;
                        break;
                    }
                    case GhostPosMode.SinglePoint:
                    {
                        if (e.bodyBefore == null) break;
                        Vector3d pos = e.bodyBefore.GetWorldSurfacePosition(
                            e.latBefore, e.lonBefore, e.altBefore);
                        e.ghost.transform.position = pos;
                        e.ghost.transform.rotation = e.bodyBefore.bodyTransform.rotation * e.interpolatedRot;
                        break;
                    }
                    case GhostPosMode.Orbit:
                    {
                        Orbit orbit;
                        if (orbitCache.TryGetValue(e.orbitCacheKey, out orbit))
                        {
                            Vector3d pos = orbit.getPositionAtUT(e.orbitUT);
                            e.ghost.transform.position = pos;

                            Vector3d vel = orbit.getOrbitalVelocityAtUT(e.orbitUT);

                            if (e.isSpinning)
                            {
                                // Spin-forward: recompute boundary world rotation (positions may have shifted by FloatingOrigin)
                                Vector3d velAtStart = orbit.getOrbitalVelocityAtUT(e.orbitSegmentStartUT);
                                Vector3d posAtStart = orbit.getPositionAtUT(e.orbitSegmentStartUT);

                                if (e.orbitBody != null)
                                {
                                    Vector3d radialAtStart = (posAtStart - (Vector3d)e.orbitBody.position).normalized;
                                    Quaternion orbFrameAtStart;
                                    if (Mathf.Abs(Vector3.Dot(((Vector3)velAtStart).normalized, ((Vector3)radialAtStart).normalized)) > 0.99f)
                                        orbFrameAtStart = Quaternion.LookRotation(velAtStart, Vector3.up);
                                    else
                                        orbFrameAtStart = Quaternion.LookRotation(velAtStart, radialAtStart);

                                    Quaternion bwRot = orbFrameAtStart * e.orbitFrameRot;
                                    double dt = e.orbitUT - e.orbitSegmentStartUT;
                                    Vector3 worldAxis = bwRot * e.orbitAngularVelocity;
                                    float angle = (float)((double)e.orbitAngularVelocity.magnitude * dt * Mathf.Rad2Deg);
                                    e.ghost.transform.rotation = Quaternion.AngleAxis(angle, worldAxis) * bwRot;
                                }
                                else
                                {
                                    // Body null fallback
                                    if (vel.sqrMagnitude > 0.001)
                                        e.ghost.transform.rotation = Quaternion.LookRotation(vel);
                                    ParsekLog.VerboseRateLimited("Playback", $"orbit-late-body-null-{e.orbitCacheKey}",
                                        $"Orbit LateUpdate: orbitBody null for cache={e.orbitCacheKey}, velocity fallback");
                                }
                            }
                            else if (e.hasOrbitFrameRot && vel.sqrMagnitude > 0.001)
                            {
                                // Orbital-frame-relative path
                                if (e.orbitBody != null)
                                {
                                    Vector3d radialOut = (pos - (Vector3d)e.orbitBody.position).normalized;
                                    Quaternion orbFrame;
                                    if (Mathf.Abs(Vector3.Dot(((Vector3)vel).normalized, ((Vector3)radialOut).normalized)) > 0.99f)
                                        orbFrame = Quaternion.LookRotation(vel, Vector3.up);
                                    else
                                        orbFrame = Quaternion.LookRotation(vel, radialOut);

                                    e.ghost.transform.rotation = orbFrame * e.orbitFrameRot;
                                }
                                else
                                {
                                    e.ghost.transform.rotation = Quaternion.LookRotation(vel);
                                    ParsekLog.VerboseRateLimited("Playback", $"orbit-late-body-null-{e.orbitCacheKey}",
                                        $"Orbit LateUpdate: orbitBody null for cache={e.orbitCacheKey}, velocity fallback");
                                }
                            }
                            else if (vel.sqrMagnitude > 0.001)
                            {
                                // Prograde fallback (old recordings)
                                e.ghost.transform.rotation = Quaternion.LookRotation(vel);
                            }
                        }
                        break;
                    }
                    case GhostPosMode.CheckpointPoint:
                    {
                        if (e.bodyBefore == null || e.bodyAfter == null) break;
                        Vector3d posBefore = e.bodyBefore.GetWorldSurfacePosition(
                            e.latBefore, e.lonBefore, e.altBefore);
                        Vector3d posAfter = e.bodyAfter.GetWorldSurfacePosition(
                            e.latAfter, e.lonAfter, e.altAfter);
                        Vector3d pos = Vector3d.Lerp(posBefore, posAfter, e.t);
                        e.ghost.transform.position = pos;

                        Orbit orbit;
                        if (orbitCache.TryGetValue(e.orbitCacheKey, out orbit))
                        {
                            Vector3d vel = orbit.getOrbitalVelocityAtUT(e.orbitUT);

                            if (e.isSpinning)
                            {
                                Vector3d velAtStart = orbit.getOrbitalVelocityAtUT(e.orbitSegmentStartUT);
                                Vector3d posAtStart = orbit.getPositionAtUT(e.orbitSegmentStartUT);

                                if (e.orbitBody != null)
                                {
                                    Vector3d radialAtStart = (posAtStart - (Vector3d)e.orbitBody.position).normalized;
                                    Quaternion orbFrameAtStart;
                                    if (Mathf.Abs(Vector3.Dot(((Vector3)velAtStart).normalized, ((Vector3)radialAtStart).normalized)) > 0.99f)
                                        orbFrameAtStart = Quaternion.LookRotation(velAtStart, Vector3.up);
                                    else
                                        orbFrameAtStart = Quaternion.LookRotation(velAtStart, radialAtStart);

                                    Quaternion bwRot = orbFrameAtStart * e.orbitFrameRot;
                                    double dt = e.orbitUT - e.orbitSegmentStartUT;
                                    Vector3 worldAxis = bwRot * e.orbitAngularVelocity;
                                    float angle = (float)((double)e.orbitAngularVelocity.magnitude * dt * Mathf.Rad2Deg);
                                    e.ghost.transform.rotation = Quaternion.AngleAxis(angle, worldAxis) * bwRot;
                                }
                                else if (vel.sqrMagnitude > 0.001)
                                {
                                    e.ghost.transform.rotation = Quaternion.LookRotation(vel);
                                }
                            }
                            else if (e.hasOrbitFrameRot && vel.sqrMagnitude > 0.001)
                            {
                                if (e.orbitBody != null)
                                {
                                    Vector3d radialOut = (pos - (Vector3d)e.orbitBody.position).normalized;
                                    Quaternion orbFrame;
                                    if (Mathf.Abs(Vector3.Dot(((Vector3)vel).normalized, ((Vector3)radialOut).normalized)) > 0.99f)
                                        orbFrame = Quaternion.LookRotation(vel, Vector3.up);
                                    else
                                        orbFrame = Quaternion.LookRotation(vel, radialOut);

                                    e.ghost.transform.rotation = orbFrame * e.orbitFrameRot;
                                }
                                else
                                {
                                    e.ghost.transform.rotation = Quaternion.LookRotation(vel);
                                }
                            }
                            else if (vel.sqrMagnitude > 0.001)
                            {
                                e.ghost.transform.rotation = Quaternion.LookRotation(vel);
                            }
                        }
                        else
                        {
                            e.ghost.transform.rotation = e.bodyBefore.bodyTransform.rotation * e.interpolatedRot;
                        }
                        break;
                    }
                    case GhostPosMode.Surface:
                    {
                        if (e.bodyBefore == null) break;
                        e.ghost.transform.position = e.bodyBefore.GetWorldSurfacePosition(
                            e.latBefore, e.lonBefore, e.altBefore);
                        e.ghost.transform.rotation = e.bodyBefore.bodyTransform.rotation * e.surfaceRot;
                        break;
                    }
                    case GhostPosMode.Relative:
                    {
                        // Re-compute ghost position from anchor's current world position + stored offset.
                        // This handles FloatingOrigin shifts because anchor vessel positions are
                        // already corrected by KSP before LateUpdate.
                        Vessel anchor = FlightRecorder.FindVesselByPid(e.anchorVesselId);
                        if (anchor != null)
                        {
                            Vector3d anchorPos = anchor.GetWorldPos3D();
                            Vector3d ghostPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                                anchorPos,
                                anchor.transform.rotation,
                                e.relDx,
                                e.relDy,
                                e.relDz,
                                e.relativeRecordingFormatVersion);
                            e.ghost.transform.position = ghostPos;
                            e.ghost.transform.rotation = TrajectoryMath.ResolveRelativePlaybackRotation(
                                anchor.transform.rotation,
                                e.relativeRot);
                        }
                        else
                        {
                            // Anchor not loaded — hide ghost (offsets are meters, not lat/lon/alt)
                            e.ghost.SetActive(false);
                        }
                        break;
                    }
                }
            }

            ClampGhostsToTerrain();

            ghostPosEntries.Clear();

            watchMode.UpdateWatchCamera();
        }

        /// <summary>
        /// Terrain clamp: prevent ghosts from appearing below terrain surface.
        /// Sub-orbital orbit reconstruction can place ghosts underground (periapsis
        /// below surface), and procedural terrain height varies between sessions.
        /// </summary>
        private void ClampGhostsToTerrain()
        {
            Vector3d activeVesselPos = FlightGlobals.ActiveVessel != null
                ? FlightGlobals.ActiveVessel.GetWorldPos3D()
                : Vector3d.zero;

            for (int i = 0; i < ghostPosEntries.Count; i++)
            {
                var e = ghostPosEntries[i];
                if (e.ghost == null || !e.ghost.activeSelf) continue;
                if (e.mode == GhostPosMode.Relative) continue;

                CelestialBody body = (e.mode == GhostPosMode.Orbit) ? e.orbitBody : e.bodyBefore;
                if (body == null || body.pqsController == null) continue;

                Vector3d pos = e.ghost.transform.position;
                double alt = body.GetAltitude(pos);
                if (alt > TerrainClampAltitudeLimit) continue;

                double lat = body.GetLatitude(pos);
                double lon = body.GetLongitude(pos);
                double terrainHeight = body.TerrainAltitude(lat, lon, true);

                // Outside the physics bubble, the rendered terrain mesh uses lower
                // LOD and can overshoot the PQS height at ridges/transitions. Use a
                // larger clearance to prevent the ghost from clipping underground.
                double distToVessel = Vector3d.Distance(pos, activeVesselPos);
                double clearance = ComputeTerrainClearance(distToVessel);

                double clamped = TerrainCorrector.ClampAltitude(alt, terrainHeight, clearance);
                if (clamped > alt)
                {
                    e.ghost.transform.position = body.GetWorldSurfacePosition(lat, lon, clamped);
                    ParsekLog.VerboseRateLimited("TerrainCorrect", "clamp-ghost",
                        string.Format(CultureInfo.InvariantCulture,
                            "Ghost terrain clamp: alt={0:F1} terrain={1:F1} -> {2:F1} (clearance={3:F1}m)",
                            alt, terrainHeight, clamped, clearance));
                }
            }
        }

        void OnGUI()
        {
            if (MapView.MapIsEnabled)
                ui.DrawMapMarkers();

            if (watchMode.IsWatchingGhost)
                watchMode.DrawWatchModeOverlay();

            // Phase 6d: Ghost labels on chain ghost GOs (no-op when ghost GOs are null)
            DrawGhostLabels();

            if (showUI)
            {
                windowRect.height = 0f;
                var opaqueWindowStyle = ui.GetOpaqueWindowStyle();
                if (opaqueWindowStyle == null)
                    return;

                ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
                try
                {
                    windowRect = ClickThruBlocker.GUILayoutWindow(
                        GetInstanceID(),
                        windowRect,
                        ui.DrawWindow,
                        "Parsek",
                        opaqueWindowStyle,
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
                ui.DrawCareerStateWindowIfOpen(windowRect);
                ui.DrawSettingsWindowIfOpen(windowRect);
                ui.DrawSpawnControlWindowIfOpen(windowRect);
                ui.DrawGloopsRecorderWindowIfOpen(windowRect);
                ui.DrawTestRunnerWindowIfOpen(windowRect, this);
            }
        }

        void OnDestroy()
        {
            Instance = null;
            ParsekLog.Info("Flight", "OnDestroy: cleaning up ParsekFlight");
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

            // Restore debris persistence if still overridden
            RestoreDebrisPersistence();

            // Clean up decouple listener if still active
            if (decoupleCreatedVessels != null)
            {
                GameEvents.onPartDeCoupleNewVesselComplete.Remove(OnDecoupleNewVesselDuringSplitCheck);
                decoupleCreatedVessels = null;
                decoupleControllerStatus = null;
                decoupleCreatedTrajectoryPoints = null;
            }
            pendingDeferredSplitCheckTrigger = DeferredSplitCheckTrigger.None;
            pendingSplitTriggerUT = double.NaN;

            // Clean up background recorder if active
            if (backgroundRecorder != null)
            {
                ParsekLog.Info("Flight", "OnDestroy: cleaning up background recorder");
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                backgroundRecorder = null;
            }

            // Clean up Gloops recorder if active
            CleanupGloopsRecorder();

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
            vesselGhoster?.CleanupAll();
            vesselGhoster = null;
            activeGhostChains = null;

            // Remove ghost map ProtoVessels on scene teardown
            GhostMapPresence.RemoveAllGhostVessels("scene-cleanup");

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
            orbitCache.Clear();
            loggedOrbitSegments.Clear();
            loggedOrbitRotationSegments.Clear();
            nearbySpawnCandidates.Clear();
            proximityVelocitySamples.Clear();
            notifiedSpawnRecordingIds.Clear();
            loggedRelativeStart.Clear();
            loggedAnchorNotFound.Clear();
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
            GameEvents.onCrewOnEva.Remove(OnCrewOnEva);
            GameEvents.onCrewBoardVessel.Remove(OnCrewBoardVessel);
            GameEvents.onVesselGoOnRails.Remove(OnVesselGoOnRails);
            GameEvents.onVesselGoOffRails.Remove(OnVesselGoOffRails);
            GameEvents.onVesselLoaded.Remove(OnVesselLoaded);
            GameEvents.onVesselUnloaded.Remove(OnVesselUnloaded);
            GameEvents.onVesselSOIChanged.Remove(OnVesselSOIChanged);
            GameEvents.onPartCouple.Remove(OnPartCouple);
            GameEvents.onPartUndock.Remove(OnPartUndock);
            GameEvents.onGroundSciencePartDeployed.Remove(OnGroundSciencePartDeployed);
            GameEvents.onGroundSciencePartRemoved.Remove(OnGroundSciencePartRemoved);
            GameEvents.onVesselChange.Remove(OnVesselSwitchComplete);
            GameEvents.onVesselWasModified.Remove(OnVesselWasModified);
            GameEvents.onTimeWarpRateChanged.Remove(OnTimeWarpRateChanged);
            MergeDialog.OnTreeCommitted -= OnTreeCommittedFromMergeDialog;
            GameEvents.afterFlagPlanted.Remove(OnAfterFlagPlanted);
            GameEvents.onGamePause.Remove(OnGamePause);
            GameEvents.onGameUnpause.Remove(OnGameUnpause);
        }

        #endregion

        #region Scene Change Handling

        void OnSceneChangeRequested(GameScenes scene)
        {
            sceneChangeInProgress = true;
            RecordingStore.PendingDestinationScene = scene;

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
            ParsekLog.RecState("OnSceneChangeRequested", CaptureRecorderState());

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

            // Finalize continuation sampling before anything else
            chainManager.StopAllContinuations("scene change");

            // Clean up Gloops recorder on scene change (discard in-progress)
            CleanupGloopsRecorder();

            ClearSceneChangeTransientState();

            // Tree mode: finalize and commit tree on scene exit.
            // Background recorder must be finalized BEFORE the tree commit so
            // its data is flushed to the tree recordings.
            if (activeTree != null)
                FinalizeTreeOnSceneChange(scene);

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
            // #267: skip if restore coroutine is mid-yield — it owns activeTree/recorder
            if (restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    $"FinalizeTreeOnSceneChange: skipped — restore coroutine in progress (dest={scene})");
                return;
            }

            double commitUT = Planetarium.GetUniversalTime();
            ParsekLog.RecState("FinalizeTreeOnSceneChange:entry", CaptureRecorderState());

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
                // but we CAN check the live vesselSwitchPending flag (set by KSP's
                // onVesselSwitching event, which fires before OnSceneChangeRequested).
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
            else if (!ParsekScenario.IsAutoMerge)
            {
                // autoMerge OFF: safe finalization but preserve snapshots for dialog in KSC/TS.
                // Uses isSceneExit: true; FinalizeIndividualRecording's #289 re-snapshot path
                // updates VesselSnapshot for stable-terminal recordings (Landed/Splashed/Orbiting)
                // so the dialog and spawn-at-end safety check see the correct sit field.
                FinalizeTreeRecordings(activeTree, commitUT, isSceneExit: true);

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
                        if (RecordingStore.SaveRecordingFiles(rec, incrementEpoch: false))
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
                RecordingStore.StashPendingTree(activeTree);
                ParsekLog.Info("Flight",
                    $"CommitTreeSceneExit (autoMerge off): stashed tree '{activeTree.TreeName}' with snapshots preserved");
            }
            else
            {
                // Scene exit: null snapshots (ghost-only, no spawning outside Flight)
                CommitTreeSceneExit(commitUT);
            }

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

        void OnVesselWillDestroy(Vessel v)
        {
            if (v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId)) return;

            // #267: skip tree-related processing if restore coroutine is mid-yield
            if (restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    $"OnVesselWillDestroy: skipped — restore coroutine in progress " +
                    $"(vessel '{v?.vesselName}')");
                return;
            }

            ParsekLog.RecState("OnVesselWillDestroy:entry", CaptureRecorderState());

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

            // If the continuation vessel is destroyed, mark the recording and stop tracking
            if (chainManager.IsTrackingContinuation && v.persistentId == chainManager.ContinuationVesselPid)
            {
                if (chainManager.TryGetContinuationRecording(out var contRec))
                {
                    contRec.VesselDestroyed = true;
                    // Bug #95: Do NOT null VesselSnapshot on committed recordings.
                    // VesselDestroyed already gates spawn via ShouldSpawnAtRecordingEnd.
                    // Nulling the snapshot permanently prevents re-spawn after revert.
                    Log($"Continuation vessel destroyed (pid={chainManager.ContinuationVesselPid}), " +
                        $"VesselDestroyed=true, VesselSnapshot preserved={contRec.VesselSnapshot != null}");
                }
                chainManager.StopContinuation("vessel destroyed");
            }

            // If the undock continuation vessel is destroyed, stop tracking
            if (chainManager.IsTrackingUndockContinuation && v.persistentId == chainManager.UndockContinuationPid)
            {
                if (chainManager.TryGetUndockContinuationRecording(out var undockRec))
                {
                    undockRec.VesselDestroyed = true;
                    Log($"Undock continuation vessel destroyed (pid={chainManager.UndockContinuationPid})");
                }
                chainManager.StopUndockContinuation("vessel destroyed");
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
            ParsekLog.RecState("ShowPostDestructionTreeMergeDialog:entry", CaptureRecorderState());

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
                    activeTree.Recordings,
                    activeTree.ActiveRecordingId,
                    activeDestroyed);
                bool onlyDebrisBlockersRemain = RecordingTree.AreAllActiveCrashBlockersDebris(
                    activeTree.Recordings,
                    activeTree.ActiveRecordingId);
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

            RecordingStore.StashPendingTree(activeTree);
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
                ParsekLog.Verbose("Flight",
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
            bool trackedInActiveTree = activeTree != null
                && activeTree.BackgroundMap.ContainsKey(newPid);
            var armDecision = EvaluatePostSwitchAutoRecordArmDecision(
                ShouldArmPostSwitchAutoRecord(
                    ParsekSettings.Current?.autoRecordOnFirstModificationAfterSwitch != false,
                    IsRecording,
                    hasNewVessel: true,
                    newVesselIsGhost,
                    newVessel.isEVA),
                trackedInActiveTree);

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
                ParsekLog.RecState("OnVesselSwitchComplete:post", CaptureRecorderState());
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
                ParsekLog.RecState(phase, snapshot);
                return;
            }

            ParsekLog.RecStateRateLimited(
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
            if (string.IsNullOrEmpty(marker.ActiveReFlyRecordingId)
                || string.IsNullOrEmpty(marker.OriginChildRecordingId))
                return false;
            if (!string.Equals(
                    marker.ActiveReFlyRecordingId,
                    marker.OriginChildRecordingId,
                    StringComparison.Ordinal))
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
            if (recId == null)
            {
                ParsekLog.Warn("Flight", "FlushRecorderToTreeRecording: no active recording id in tree");
                rec.IsRecording = false;
                return;
            }

            Recording treeRec;
            if (!tree.Recordings.TryGetValue(recId, out treeRec))
            {
                ParsekLog.Warn("Flight", $"FlushRecorderToTreeRecording: recording id '{recId}' not found in tree");
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

            // Set IsRecording = false to prevent dangling active state
            rec.IsRecording = false;
            rec.TransitionToBackgroundPending = false;

            ParsekLog.Info("Flight", $"Flushed recorder to tree recording '{recId}': " +
                $"{rec.Recording.Count} points, {rec.OrbitSegments.Count} orbit segments, " +
                $"{rec.PartEvents.Count} part events");
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

            ParsekLog.RecState("PromoteFromBackground:entry", CaptureRecorderState());

            // Callers pass the target recording id explicitly; BackgroundMap membership is
            // not a hard precondition here. The committed-tree restore path may already have
            // removed stale entries for this recording before the live promotion runs.
            activeTree.BackgroundMap.Remove(newVessel.persistentId);

            // Set as the active recording
            activeTree.ActiveRecordingId = backgroundRecordingId;

            // Create a new recorder and start recording with isPromotion
            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;
            if (chainManager.PendingBoundaryAnchor.HasValue)
            {
                recorder.BoundaryAnchor = chainManager.PendingBoundaryAnchor;
                chainManager.PendingBoundaryAnchor = null;
            }
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

            ParsekLog.Info("Flight", $"Promoted recording '{backgroundRecordingId}' from background " +
                $"(pid={newVessel.persistentId})");
            ParsekLog.RecState("PromoteFromBackground:exit", CaptureRecorderState());
        }

        private bool TryRestoreCommittedTreeForSpawnedActiveVessel()
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

            if (!TryTakeCommittedTreeForSpawnedVesselRestore(
                    activeVesselPid,
                    out RecordingTree committedTree,
                    out string targetRecordingId,
                    out CommittedSpawnedVesselRestoreAction action))
                return false;

            activeTree = committedTree;
            chainManager.ActiveTreeId = activeTree.Id;
            EnsureBackgroundRecorderAttached("TryRestoreCommittedTreeForSpawnedActiveVessel");

            bool restored = false;
            if (action == CommittedSpawnedVesselRestoreAction.ResumeActiveRecording)
            {
                restored = ResumeCommittedActiveRecording(targetRecordingId, activeVessel);
            }
            else
            {
                backgroundRecorder?.OnVesselRemovedFromBackground(activeVesselPid);
                PromoteRecordingFromBackground(targetRecordingId, activeVessel);
                restored = recorder != null && recorder.IsRecording;
            }

            if (!restored)
            {
                ParsekLog.Warn("Flight",
                    $"TryRestoreCommittedTreeForSpawnedActiveVessel: tree '{activeTree.TreeName}' " +
                    $"matched vessel '{activeVessel.vesselName}' pid={activeVesselPid} via {action}, " +
                    "but recorder attach failed; tree remains restored for late recovery");
                ParsekLog.RecState("CommittedSpawnedRestore:failed", CaptureRecorderState());
                return false;
            }

            ParsekLog.Info("Flight",
                $"TryRestoreCommittedTreeForSpawnedActiveVessel: restored tree '{activeTree.TreeName}' " +
                $"for vessel '{activeVessel.vesselName}' pid={activeVesselPid} via {action}");
            // Surface a screen message mirroring the launch and post-switch auto-record
            // paths. OnFlightReady fires before KSP's ScreenMessages UI queue is ready
            // to display messages; calling ScreenMessage directly here gets the toast
            // swallowed by the scene-load transition. Defer to a coroutine that waits
            // a short duration so the UI has settled — the launch path's (auto) toast
            // works because it posts from OnVesselSituationChange after physics starts,
            // well after scene-load.
            StartCoroutine(DeferredResumeScreenMessage());
            ParsekLog.RecState("CommittedSpawnedRestore:post", CaptureRecorderState());
            return true;
        }

        private System.Collections.IEnumerator DeferredResumeScreenMessage()
        {
            yield return new WaitForSeconds(0.5f);
            ScreenMessage("Recording STARTED (resume)", 4f);
        }

        private bool ResumeCommittedActiveRecording(string recordingId, Vessel activeVessel)
        {
            if (activeTree == null || activeVessel == null || string.IsNullOrEmpty(recordingId))
                return false;

            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;
            if (chainManager.PendingBoundaryAnchor.HasValue)
            {
                recorder.BoundaryAnchor = chainManager.PendingBoundaryAnchor;
                chainManager.PendingBoundaryAnchor = null;
            }
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
        }

        #region Split Event Detection (Tree Branching)

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
                // Mark dirty so the next OnSave persists the appended data to
                // the .prec sidecar. Without this, data lives only in memory
                // and is lost on scene reload (see Recording.MarkFilesDirty
                // docstring and CHANGELOG 2026-04-09 bug #273).
                target.MarkFilesDirty();
            }
            target.ExplicitEndUT = endUT;
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
                targetRec.TerminalStateValue = TerminalState.Destroyed;
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
        /// Tags the recording's SegmentPhase and SegmentBodyName based on the vessel's current
        /// body and altitude, if not already set. No-op if SegmentPhase is already non-empty
        /// or vessel/mainBody is null.
        /// </summary>
        internal static void TagSegmentPhaseIfMissing(Recording pending, Vessel v)
        {
            if (string.IsNullOrEmpty(pending.SegmentPhase))
            {
                if (v != null && v.mainBody != null)
                {
                    pending.SegmentBodyName = v.mainBody.name;
                    if (v.situation == Vessel.Situations.LANDED
                        || v.situation == Vessel.Situations.SPLASHED
                        || v.situation == Vessel.Situations.PRELAUNCH)
                    {
                        pending.SegmentPhase = "surface";
                    }
                    else if (v.mainBody.atmosphere)
                        pending.SegmentPhase = v.altitude < v.mainBody.atmosphereDepth ? "atmo" : "exo";
                    else
                    {
                        double threshold = FlightRecorder.ComputeApproachAltitude(v.mainBody);
                        pending.SegmentPhase = v.altitude < threshold ? "approach" : "exo";
                    }
                }
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

        /// <summary>
        /// Pure data-model method: creates the BranchPoint and one child Recording object
        /// for a vessel merge (dock or board). Testable without Unity.
        /// Takes a list of parent recording IDs (1 for foreign vessel merge, 2 for two-tree merge)
        /// and returns a single child recording for the merged vessel.
        /// </summary>
        internal static (BranchPoint bp, Recording mergedChild)
            BuildMergeBranchData(
                List<string> parentRecordingIds, string treeId, double mergeUT,
                BranchPointType branchType, uint mergedVesselPid, string mergedVesselName)
        {
            string childId = Guid.NewGuid().ToString("N");
            string bpId = Guid.NewGuid().ToString("N");

            var bp = new BranchPoint
            {
                Id = bpId,
                UT = mergeUT,
                Type = branchType,
                ParentRecordingIds = new List<string>(parentRecordingIds),
                ChildRecordingIds = new List<string> { childId }
            };

            var mergedChild = new Recording
            {
                RecordingId = childId,
                TreeId = treeId,
                VesselPersistentId = mergedVesselPid,
                VesselName = mergedVesselName,
                ParentBranchPointId = bpId,
                ExplicitStartUT = mergeUT
            };

            return (bp, mergedChild);
        }

        /// <summary>
        /// Creates a tree branch for a vessel split event.
        /// Requires an active tree (always present in always-tree mode).
        /// Creates two child recordings (active + background), wires up BackgroundRecorder,
        /// and starts a new FlightRecorder for the active child.
        /// </summary>
        void CreateSplitBranch(BranchPointType branchType, Vessel activeVessel, Vessel backgroundVessel,
            double branchUT, string evaCrewName = null, uint evaVesselPid = 0)
        {
            ParsekLog.RecState("CreateSplitBranch:entry", CaptureRecorderState());
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
            ConfigNode activeSnapshot = VesselSpawner.TryBackupSnapshot(activeVessel);
            ConfigNode bgSnapshot = VesselSpawner.TryBackupSnapshot(backgroundVessel);

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
                    initialEnvironmentOverride: initialBackgroundEnvOverride);
            }

            // Stop any existing undock continuation and vessel continuation (tree handles them)
            // Bug #95: bake before stop — continuation data is canonical (tree takes over)
            if (chainManager.IsTrackingUndockContinuation)
            {
                if (chainManager.TryGetUndockContinuationRecording(out var undockRec))
                    ChainSegmentManager.BakeContinuationData(undockRec);
                chainManager.StopUndockContinuation("tree branch");
            }
            if (chainManager.IsTrackingContinuation)
            {
                if (chainManager.TryGetContinuationRecording(out var contRec))
                    ChainSegmentManager.BakeContinuationData(contRec);
                chainManager.StopContinuation("tree branch");
            }

            // Create new FlightRecorder for active child
            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;
            recorder.StartRecording(isPromotion: true);
            recorder.UndockSiblingPid = 0; // tree handles vessel tracking

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
                (evaCrewName != null ? $", evaCrew={evaCrewName}" : ""));
            ParsekLog.RecState("CreateSplitBranch:exit", CaptureRecorderState());

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
        /// Phase 4 (Rewind-to-Staging, design §5.1 + §7.1 + §7.2 + §7.19): if the
        /// split has >=2 controllable children, builds a <see cref="ChildSlot"/>
        /// list from the freshly-created child recordings and hands it to
        /// <see cref="RewindPointAuthor.Begin"/>. Called from both the Undock/EVA
        /// side of <see cref="CreateSplitBranch"/> and from the structural
        /// <c>ProcessBreakupEvent</c> path.
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
        /// When we are the dock target (our PID unchanged), finds the PID of the vessel
        /// that was absorbed into us. Scans BackgroundMap for a vessel that is no longer
        /// a separate entity (its Vessel object is gone or merged into ours).
        /// Returns 0 if the partner is not in the tree (foreign vessel).
        /// </summary>
        uint FindAbsorbedDockPartnerPid(uint mergedPid, Vessel mergedVessel)
        {
            if (activeTree == null) return 0;

            foreach (var kvp in activeTree.BackgroundMap)
            {
                uint bgPid = kvp.Key;
                if (bgPid == mergedPid) continue; // skip ourselves

                Vessel bgVessel = FlightRecorder.FindVesselByPid(bgPid);

                // After coupling, the absorbed vessel either:
                // (a) has its Vessel object destroyed (null)
                // (b) has its Vessel reference pointing to the merged vessel (reparented parts)
                // (c) still exists but with 0 parts (about to be destroyed)
                if (bgVessel == null || bgVessel == mergedVessel
                    || bgVessel.parts == null || bgVessel.parts.Count == 0)
                {
                    return bgPid;
                }
            }

            // No background vessel was absorbed -- partner is a foreign vessel
            return 0;
        }

        /// <summary>
        /// Creates a tree merge branch point for a dock or board event.
        /// Ends parent recordings, creates child recording for the merged vessel,
        /// and starts a new FlightRecorder for the child.
        /// Per Orchestrator Fix 3: absorbedVesselPid is NOT a parameter --
        /// background vessel PID is derived from the background parent recording.
        /// </summary>
        void CreateMergeBranch(
            BranchPointType branchType,
            uint mergedVesselPid,
            string activeParentRecordingId,      // from the active recorder (always present)
            string backgroundParentRecordingId,  // from BackgroundMap lookup (null for foreign vessel)
            double mergeUT,
            FlightRecorder stoppedRecorder)
        {
            // 1. Build parent recording ID list
            var parentIds = new List<string>();
            parentIds.Add(activeParentRecordingId);
            if (backgroundParentRecordingId != null)
                parentIds.Add(backgroundParentRecordingId);

            // 2. Flush captured data from stopped recorder into the active parent recording
            Recording activeParentRec = null;
            if (activeTree.Recordings.TryGetValue(activeParentRecordingId, out activeParentRec))
            {
                AppendCapturedDataToRecording(activeParentRec, stoppedRecorder.CaptureAtStop, mergeUT);
                // Set terminal state based on merge type
                activeParentRec.TerminalStateValue = (branchType == BranchPointType.Dock)
                    ? TerminalState.Docked : TerminalState.Boarded;
            }

            // 3. End background parent recording (if two-parent merge)
            // Per Fix 3: derive background vessel PID from the recording's VesselPersistentId
            if (backgroundParentRecordingId != null)
            {
                Recording bgParentRec;
                if (activeTree.Recordings.TryGetValue(backgroundParentRecordingId, out bgParentRec))
                {
                    bgParentRec.ExplicitEndUT = mergeUT;
                    bgParentRec.TerminalStateValue = (branchType == BranchPointType.Dock)
                        ? TerminalState.Docked : TerminalState.Boarded;

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
                mergedVesselPid, mergedVesselName);

            // 6. Take snapshot of merged vessel
            ConfigNode mergedSnapshot = (mergedVessel != null)
                ? VesselSpawner.TryBackupSnapshot(mergedVessel) : null;
            mergedChild.GhostVisualSnapshot = mergedSnapshot;
            mergedChild.VesselSnapshot = mergedSnapshot != null ? mergedSnapshot.CreateCopy() : null;

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
        /// Fallback: if tree append fails, commit captured data directly.
        /// In always-tree mode the tree path should succeed for the active vessel;
        /// this fallback handles edge cases (no active tree, debris).
        /// </summary>
        void FallbackCommitSplitRecorder(FlightRecorder splitRec)
        {
            ParsekLog.RecState("FallbackCommitSplitRecorder:entry", CaptureRecorderState());
            if (splitRec?.CaptureAtStop == null) return;

            var captured = splitRec.CaptureAtStop;

            // Bug #297: when in tree mode, append to the active tree recording
            // instead of orphaning data as a standalone recording.
            if (TryAppendCapturedToTree(activeTree, captured))
                return;

            var rec = RecordingStore.CreateRecordingFromFlightData(
                captured.Points, captured.VesselName,
                orbitSegments: captured.OrbitSegments,
                partEvents: captured.PartEvents,
                flagEvents: captured.FlagEvents);

            if (rec == null)
            {
                ParsekLog.Warn("Flight", "Split branch failed — recording too short to commit, data lost");
                return;
            }

            // Copy snapshot/vessel state to the recording
            rec.VesselSnapshot = captured.VesselSnapshot;
            rec.GhostVisualSnapshot = captured.GhostVisualSnapshot;
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

            // Location context (Phase 10)
            rec.CopyStartLocationFrom(captured);
            rec.EndBiome = captured.EndBiome;

            // Preserve chain membership if this segment was part of a chain
            chainManager.ApplyChainMetadataTo(rec);

            // Set terminal state for destroyed vessels
            if (captured.VesselDestroyed)
            {
                rec.TerminalStateValue = TerminalState.Destroyed;
                ParsekLog.Verbose("Flight", "FallbackCommitSplitRecorder: set TerminalState=Destroyed");
            }

            // Tag segment phase if untagged
            TagSegmentPhaseIfMissing(rec, FlightGlobals.ActiveVessel);

            // Destroyed vessels: discard pad failures or idle-on-pad, otherwise commit directly.
            if (rec.VesselDestroyed)
            {
                double dur = rec.EndUT - rec.StartUT;
                double maxDist = rec.MaxDistanceFromLaunch;
                if (IsPadFailure(rec))
                {
                    ParsekLog.Info("Flight",
                        $"Vessel destroyed during split — pad failure ({dur:F1}s, {maxDist:F0}m), discarding");
                    return;
                }
                if (IsIdleOnPad(rec))
                {
                    ParsekLog.Info("Flight",
                        $"Vessel destroyed during split — idle on pad (maxDist={maxDist:F1}m), discarding");
                    return;
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

            string chainInfo = chainManager.ActiveChainId != null
                ? $" (chain={chainManager.ActiveChainId}, idx={chainManager.ActiveChainNextIndex})"
                : "";
            ParsekLog.Info("Flight", $"FallbackCommitSplitRecorder: recording committed{chainInfo}");
        }

        /// <summary>
        /// Resumes recording after a false-alarm split detection (debris undock, joint break
        /// that didn't produce a trackable vessel, deduplication skip). Re-enables the
        /// stopped recorder and restores it as the active recorder.
        /// </summary>
        void ResumeSplitRecorder(FlightRecorder splitRec, string reason)
        {
            ParsekLog.RecState("ResumeSplitRecorder:entry", CaptureRecorderState());
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

            splitRec.ResumeAfterFalseAlarm();

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

        IEnumerator DeferredUndockBranch(uint newVesselPid)
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

                // Deduplication check
                if (!CheckBranchDeduplication(branchUT, newVesselPid))
                {
                    ResumeSplitRecorder(pendingSplitRecorder, "undock dedup skip");
                    yield break;
                }

                // Find the new vessel
                Vessel newVessel = FlightRecorder.FindVesselByPid(newVesselPid);
                if (newVessel == null)
                {
                    ParsekLog.Verbose("Flight", $"DeferredUndockBranch: vessel pid={newVesselPid} not found — debris destroyed, resuming recording");
                    ResumeSplitRecorder(pendingSplitRecorder, "undocked vessel not found");
                    yield break;
                }

                // Debris filter
                if (!IsTrackableVessel(newVessel))
                {
                    ParsekLog.Verbose("Flight", $"DeferredUndockBranch: vessel pid={newVesselPid} is not trackable (debris) — resuming recording");
                    ResumeSplitRecorder(pendingSplitRecorder, "undocked vessel is debris");
                    yield break;
                }

                // Player stays on remaining vessel (active), undocked vessel goes to background
                Vessel activeVessel = FlightGlobals.ActiveVessel;
                CreateSplitBranch(BranchPointType.Undock, activeVessel, newVessel, branchUT);
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

                        newVesselPids.Add(v.persistentId);

                        bool hasController = IsTrackableVessel(v);
                        newVesselHasController[v.persistentId] = hasController;
                        if (hasController)
                        {
                            anyNewVesselHasController = true;
                        }
                    }
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
                            preSnapshot = VesselSpawner.TryBackupSnapshot(childVessel);
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

                        ParsekLog.Info("Coalescer",
                            $"Feeding split to coalescer: childPid={newPid}, childHasController={hasController}" +
                            $", preSnapshot={preSnapshot != null}, preTrajectoryPoint={preTrajectoryPoint.HasValue} " +
                            $"pointSource={trajectoryPointSource} " +
                            $"branchUT={branchUT.ToString("F2", CultureInfo.InvariantCulture)}" +
                            (preTrajectoryPoint.HasValue
                                ? $" seedUT={preTrajectoryPoint.Value.ut.ToString("F2", CultureInfo.InvariantCulture)}"
                                : string.Empty));
                        crashCoalescer.OnSplitEvent(
                            branchUT, newPid, hasController,
                            preSnapshot: preSnapshot,
                            preTrajectoryPoint: preTrajectoryPoint);
                    }

                    // Resume the recorder — the coalescer's Tick will emit the BREAKUP
                    // branch point after the coalescing window expires. The recorder needs
                    // to keep running to capture continued trajectory data.
                    ResumeSplitRecorder(pendingSplitRecorder,
                        $"joint break fed to coalescer (classification={classification})");

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
                $"Coalescer emitted BREAKUP: ut={breakupBp.UT:F2}, " +
                $"cause={breakupBp.BreakupCause}, debris={breakupBp.DebrisCount}, " +
                $"duration={breakupBp.BreakupDuration:F3}s");

            ProcessBreakupEvent(breakupBp);
        }

        /// <summary>
        /// Pure decision: should the controlled-child loop in
        /// <see cref="ProcessBreakupEvent"/> skip creating a recording for a
        /// breakup child whose live <see cref="Vessel"/> is gone and which
        /// has no pre-captured snapshot? Such a child would produce a
        /// 1-point "Unknown" 0s row with no playback or replay value (no
        /// ghost visuals, no spawn snapshot, no events). The parent
        /// recording's BREAKUP branch point already records the split.
        /// </summary>
        internal static bool ShouldSkipDeadOnArrivalControlledChild(
            bool childVesselIsAlive,
            bool hasPreCapturedSnapshot)
        {
            return !childVesselIsAlive && !hasPreCapturedSnapshot;
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
            childRec.StartResources = VesselSpawner.ExtractResourceManifest(manifestSnapshot);
            int childInvSlots;
            childRec.StartInventory = VesselSpawner.ExtractInventoryManifest(manifestSnapshot, out childInvSlots);
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
            int parentGeneration = 0)
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
                Generation = parentGeneration + 1
            };

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

            if (vessel != null)
            {
                ConfigNode liveSnapshot = VesselSpawner.TryBackupSnapshot(vessel);
                SeedBreakupChildSnapshots(childRec, pid, liveSnapshot, fallbackSnapshot);
            }
            else if (fallbackSnapshot != null)
            {
                // Vessel destroyed during coalescing window — use pre-captured snapshot (#157)
                SeedBreakupChildSnapshots(childRec, pid, liveSnapshot: null, preCapturedSnapshot: fallbackSnapshot);
                childRec.TerminalStateValue = TerminalState.Destroyed;
                childRec.ExplicitEndUT = breakupBp.UT;
                if (fallbackTrajectoryPoint.HasValue)
                    BackgroundRecorder.ApplyTrajectoryPointToRecording(childRec, fallbackTrajectoryPoint.Value);
                ParsekLog.Info("Coalescer",
                    $"CreateBreakupChildRecording: using pre-captured snapshot for pid={pid} (vessel destroyed)");
            }
            else
            {
                childRec.TerminalStateValue = TerminalState.Destroyed;
                childRec.ExplicitEndUT = breakupBp.UT;
                if (fallbackTrajectoryPoint.HasValue)
                {
                    BackgroundRecorder.ApplyTrajectoryPointToRecording(childRec, fallbackTrajectoryPoint.Value);
                    ParsekLog.Info("Coalescer",
                        $"CreateBreakupChildRecording: using pre-captured trajectory point for pid={pid} (vessel destroyed)");
                }
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

            // Wire the breakup BP into the tree: parent is the active recording
            breakupBp.ParentRecordingIds.Add(activeRecId);
            activeTree.BranchPoints.Add(breakupBp);

            // Set ChildBranchPointId on the active recording to link to this breakup
            activeRec.ChildBranchPointId = breakupBp.Id;

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
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel != null && activeVessel.persistentId == activeRec.VesselPersistentId)
            {
                var snap = VesselSpawner.TryBackupSnapshot(activeVessel);
                if (snap != null)
                    activeRec.VesselSnapshot = snap;
                ParsekLog.Info("Coalescer",
                    $"ProcessBreakupEvent: refreshed VesselSnapshot post-breakup " +
                    $"(parts={activeVessel.parts?.Count ?? 0}, snapshot={snap != null})");
            }

            // Bug #298: snapshot active recorder's engine/RCS state for child recordings.
            // The recorder is still running (breakup-continuous design), so its state is live.
            InheritedEngineState? breakupEngineState = recorder != null
                ? InheritedEngineState.FromRecorder(recorder) : null;

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
            Vessel activeVesselForBreakup = FlightGlobals.ActiveVessel;
            if (activeVesselForBreakup != null)
                breakupControllablePairs.Add((activeVesselForBreakup, activeRec));

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
                    TrajectoryPoint? breakupChildPoint = crashCoalescer.GetPreCapturedTrajectoryPoint(pid);

                    // Skip dead-on-arrival controlled children: when the live
                    // vessel is gone AND no pre-captured snapshot exists, the
                    // recording would land in the table as a 1-point "Unknown"
                    // 0s row with no playback or replay value (no ghost
                    // visuals, no spawn snapshot, no events). The decoupled
                    // probe immediately Punch-Through'd subsurface and Unity
                    // tore the Vessel down before the coalescer window
                    // expired — there is nothing useful to record. The parent
                    // recording's BREAKUP branch point already captures that
                    // the split happened.
                    if (ShouldSkipDeadOnArrivalControlledChild(
                            childVesselIsAlive: childVessel != null,
                            hasPreCapturedSnapshot: ctrlSnap != null))
                    {
                        ParsekLog.Info("Coalescer",
                            $"ProcessBreakupEvent: skipping dead-on-arrival controlled child " +
                            $"pid={pid} (vessel destroyed before window expired, no pre-captured snapshot) — " +
                            "would produce an 'Unknown' 0s row with no playback value");
                        continue;
                    }

                    var childRec = CreateBreakupChildRecording(activeTree, breakupBp, pid, childVessel, false, "Unknown",
                        ctrlSnap, breakupChildPoint, parentGeneration: activeRec.Generation);

                    // Add to BackgroundRecorder for trajectory sampling (no TTL — records indefinitely)
                    if (childVessel != null && backgroundRecorder != null)
                    {
                        TrajectoryPoint? initialPoint = breakupChildPoint;
                        if (!initialPoint.HasValue)
                        {
                            double sampleUT = Planetarium.GetUniversalTime();
                            initialPoint = BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                                childVessel, sampleUT, preferRootPartSurfacePose: true);
                        }
                        activeTree.BackgroundMap[pid] = childRec.RecordingId;
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
                        $"{DescribeBreakupChildSeedPose(breakupChildPoint, childVessel, Planetarium.GetUniversalTime())}");
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
                    TrajectoryPoint? breakupChildPoint = crashCoalescer.GetPreCapturedTrajectoryPoint(pid);
                    var childRec = CreateBreakupChildRecording(activeTree, breakupBp, pid, debrisVessel, true, "Debris",
                        preSnap, breakupChildPoint, parentGeneration: activeRec.Generation);

                    if (debrisVessel != null && backgroundRecorder != null)
                    {
                        TrajectoryPoint? initialPoint = breakupChildPoint;
                        if (!initialPoint.HasValue)
                        {
                            double sampleUT = Planetarium.GetUniversalTime();
                            initialPoint = BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                                debrisVessel, sampleUT, preferRootPartSurfacePose: true);
                        }
                        activeTree.BackgroundMap[pid] = childRec.RecordingId;
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
                $"ProcessBreakupEvent: BREAKUP attached to tree={activeTree.Id}, " +
                $"parentRec={activeRecId}, bpId={breakupBp.Id}, " +
                $"cause={breakupBp.BreakupCause}, debris={breakupBp.DebrisCount}, " +
                $"skippedTrivial={skippedDebris}, " +
                $"duration={breakupBp.BreakupDuration:F3}s");
        }

        #endregion

        #region Terminal Event Detection (Destruction)

        /// <summary>
        /// Pure decision method: determines whether a deferred destruction check should be started.
        /// Returns true if the vessel is in the BackgroundMap, not in dockingInProgress, and a tree exists.
        /// </summary>
        internal static bool ShouldDeferDestructionCheck(
            uint vesselPid,
            bool hasTree,
            HashSet<uint> dockingInProgress,
            Dictionary<uint, string> backgroundMap)
        {
            if (!hasTree) return false;
            if (dockingInProgress.Contains(vesselPid)) return false;
            return backgroundMap.ContainsKey(vesselPid);
        }

        internal enum DestructionMode { None, TreeDeferred, TreeAllLeavesCheck }

        /// <summary>
        /// Pure classification of vessel destruction handling mode.
        /// Matches the branching order in OnVesselWillDestroy: TreeDeferred is checked first,
        /// then TreeAllLeavesCheck (tree with active vessel).
        ///
        /// TreeDeferred requires shouldDeferForTree (vessel in BackgroundMap),
        /// while TreeAllLeavesCheck requires isActiveVessel. The active vessel
        /// is never in BackgroundMap, so at most one branch fires.
        ///
        /// TreeAllLeavesCheck intentionally does not require isRecording — the original checked
        /// recorder != null (embedded in vesselDestroyedDuringRecording) but not IsRecording.
        /// </summary>
        internal static DestructionMode ClassifyVesselDestruction(
            bool hasActiveTree,
            bool isRecording,
            bool vesselDestroyedDuringRecording,
            bool isActiveVessel,
            bool shouldDeferForTree,
            bool treeDestructionDialogPending)
        {
            if (hasActiveTree && shouldDeferForTree)
                return DestructionMode.TreeDeferred;

            if (hasActiveTree && vesselDestroyedDuringRecording && isActiveVessel
                && !treeDestructionDialogPending)
                return DestructionMode.TreeAllLeavesCheck;

            return DestructionMode.None;
        }

        internal enum PostDestructionMergeResolution
        {
            FinalizeNow,
            WaitForPendingCrashResolution,
            AbortAndKeepRecording,
        }

        internal static bool HasPendingPostDestructionCrashResolution(
            bool activeDestroyed,
            bool pendingSplitInProgress,
            bool hasPendingBreakup)
        {
            return activeDestroyed && (pendingSplitInProgress || hasPendingBreakup);
        }

        internal static PostDestructionMergeResolution ClassifyPostDestructionMergeResolution(
            bool activeDestroyed,
            bool allLeavesTerminal,
            bool onlyDebrisBlockersRemain,
            bool pendingCrashResolution)
        {
            if (activeDestroyed && pendingCrashResolution)
                return PostDestructionMergeResolution.WaitForPendingCrashResolution;

            if (allLeavesTerminal || (activeDestroyed && onlyDebrisBlockersRemain))
                return PostDestructionMergeResolution.FinalizeNow;

            return PostDestructionMergeResolution.AbortAndKeepRecording;
        }

        /// <summary>
        /// Pure decision method: determines whether a vessel is truly destroyed (not just unloaded
        /// or absorbed by docking) after the one-frame deferral.
        /// </summary>
        internal static bool IsTrulyDestroyed(
            uint vesselPid,
            HashSet<uint> dockingInProgress,
            bool vesselStillExists)
        {
            return ClassifyDeferredDestruction(vesselPid, dockingInProgress, vesselStillExists)
                == DeferredDestructionOutcome.ConfirmedDestroyed;
        }

        internal static DeferredDestructionOutcome ClassifyDeferredDestruction(
            uint vesselPid,
            HashSet<uint> dockingInProgress,
            bool vesselStillExists)
        {
            if (dockingInProgress != null && dockingInProgress.Contains(vesselPid))
                return DeferredDestructionOutcome.DockingInProgress;
            return vesselStillExists
                ? DeferredDestructionOutcome.FalseDestroyReattach
                : DeferredDestructionOutcome.ConfirmedDestroyed;
        }

        internal static bool ShouldReattachBackgroundRecorderAfterDeferredDestruction(
            DeferredDestructionOutcome outcome)
        {
            return outcome == DeferredDestructionOutcome.FalseDestroyReattach;
        }

        internal static bool TryHandleDeferredDestructionAbort(
            uint vesselPid,
            DeferredDestructionOutcome outcome,
            Action<uint> reattachBackgroundRecorder,
            Action<string> debugLog,
            Action<string> infoLog)
        {
            if (outcome == DeferredDestructionOutcome.ConfirmedDestroyed)
                return false;

            if (ShouldReattachBackgroundRecorderAfterDeferredDestruction(outcome))
            {
                debugLog?.Invoke(
                    $"DeferredDestructionCheck: pid={vesselPid} still exists — vessel unloaded, not destroyed");
                reattachBackgroundRecorder?.Invoke(vesselPid);
                infoLog?.Invoke(
                    $"DeferredDestructionCheck: reattached background recorder state for " +
                    $"pid={vesselPid} after false destruction signal");
            }
            else
            {
                debugLog?.Invoke(
                    $"DeferredDestructionCheck: pid={vesselPid} now in dockingInProgress — aborting");
            }

            return true;
        }

        /// <summary>
        /// Pure decision method: detects phantom terrain crashes. KSP sometimes crashes
        /// EVA vessels through terrain during pack/unload. Returns true if the vessel was
        /// in a safe situation (LANDED/SPLASHED) when packed and was destroyed within 5s.
        /// </summary>
        internal static bool IsPhantomTerrainCrash(
            string evaCrewName, double packUT, double destructionUT, Vessel.Situations prePackSituation)
        {
            if (string.IsNullOrEmpty(evaCrewName)) return false;
            bool wasSafe = prePackSituation == Vessel.Situations.LANDED
                        || prePackSituation == Vessel.Situations.SPLASHED;
            if (!wasSafe) return false;
            double elapsed = destructionUT - packUT;
            return elapsed >= 0 && elapsed < PhantomCrashWindowSeconds;
        }

        /// <summary>
        /// Pure decision method: if the vessel was destroyed during recording, ensures
        /// TerminalStateValue is Destroyed regardless of what situation-based inference produced.
        /// Returns true if the terminal state was overridden.
        /// </summary>
        internal static bool ApplyDestroyedFallback(
            bool wasDestroyed, Recording rec)
        {
            if (!wasDestroyed) return false;
            if (rec.TerminalStateValue == TerminalState.Destroyed) return false;

            var prev = rec.TerminalStateValue;
            rec.TerminalStateValue = TerminalState.Destroyed;
            ParsekLog.Info("Flight",
                $"Scene-exit fallback: vessel destroyed during recording — overriding TerminalState " +
                $"from {prev?.ToString() ?? "null"} to Destroyed");
            return true;
        }

        /// <summary>
        /// Pure static method: applies terminal destruction state to a recording.
        /// Sets TerminalStateValue = Destroyed, ExplicitEndUT, and copies orbital/surface data.
        /// </summary>
        internal static void ApplyTerminalDestruction(
            PendingDestruction pending,
            Recording rec)
        {
            rec.TerminalStateValue = TerminalState.Destroyed;
            rec.ExplicitEndUT = pending.capturedUT;
            ApplyTerminalData(pending, rec);
            ParsekLog.Verbose("Flight", $"Applied terminal destruction to recording {rec.RecordingId}: Destroyed at UT={pending.capturedUT:F1}");
        }

        /// <summary>
        /// Pure static helper: copies orbital/surface data from a PendingDestruction to a recording.
        /// </summary>
        internal static void ApplyTerminalData(
            PendingDestruction data,
            Recording rec)
        {
            if (data.hasOrbit)
            {
                rec.TerminalOrbitInclination = data.inclination;
                rec.TerminalOrbitEccentricity = data.eccentricity;
                rec.TerminalOrbitSemiMajorAxis = data.semiMajorAxis;
                rec.TerminalOrbitLAN = data.lan;
                rec.TerminalOrbitArgumentOfPeriapsis = data.argumentOfPeriapsis;
                rec.TerminalOrbitMeanAnomalyAtEpoch = data.meanAnomalyAtEpoch;
                rec.TerminalOrbitEpoch = data.epoch;
                rec.TerminalOrbitBody = data.bodyName;
            }
            if (data.hasSurface)
            {
                rec.TerminalPosition = data.surfacePosition;
            }
            ParsekLog.Verbose("Flight", $"Applied terminal data to recording {rec.RecordingId}: orbit={data.hasOrbit}, surface={data.hasSurface}");
        }

        /// <summary>
        /// Phase 1 capture: extracts vessel state before destruction for deferred processing.
        /// Called synchronously from OnVesselWillDestroy while the Vessel object is still valid.
        /// Handles ORBITING/SUB_ORBITAL/ESCAPING (orbit capture), LANDED/SPLASHED/PRELAUNCH
        /// (surface capture), and FLYING (orbit as best-effort approximation).
        /// </summary>
        PendingDestruction CaptureVesselStateForTerminal(Vessel v, string recordingId)
        {
            var pending = new PendingDestruction
            {
                vesselPid = v.persistentId,
                recordingId = recordingId,
                capturedUT = Planetarium.GetUniversalTime(),
                situation = v.situation
            };

            switch (v.situation)
            {
                case Vessel.Situations.ORBITING:
                case Vessel.Situations.SUB_ORBITAL:
                case Vessel.Situations.ESCAPING:
                case Vessel.Situations.FLYING:
                    // Capture orbit data (FLYING uses orbit as best-effort approximation)
                    if (v.orbit != null)
                    {
                        pending.hasOrbit = true;
                        pending.inclination = v.orbit.inclination;
                        pending.eccentricity = v.orbit.eccentricity;
                        pending.semiMajorAxis = v.orbit.semiMajorAxis;
                        pending.lan = v.orbit.LAN;
                        pending.argumentOfPeriapsis = v.orbit.argumentOfPeriapsis;
                        pending.meanAnomalyAtEpoch = v.orbit.meanAnomalyAtEpoch;
                        pending.epoch = v.orbit.epoch;
                        pending.bodyName = v.mainBody?.name ?? "Kerbin";
                    }
                    break;

                case Vessel.Situations.LANDED:
                case Vessel.Situations.SPLASHED:
                case Vessel.Situations.PRELAUNCH:
                    // Capture surface position (PRELAUNCH treated as LANDED)
                    pending.hasSurface = true;
                    pending.surfacePosition = new SurfacePosition
                    {
                        body = v.mainBody?.name ?? "Kerbin",
                        latitude = v.latitude,
                        longitude = v.longitude,
                        altitude = v.altitude,
                        rotation = v.srfRelRotation,
                        situation = v.situation == Vessel.Situations.SPLASHED
                            ? SurfaceSituation.Splashed
                            : SurfaceSituation.Landed
                    };
                    break;
            }

            ParsekLog.Verbose("Flight", $"Captured terminal state for {v.vesselName}: situation={v.situation}, body={v.mainBody?.name ?? "unknown"}");
            return pending;
        }

        /// <summary>
        /// Deferred destruction check coroutine. After one frame, verifies whether the vessel
        /// was truly destroyed (not just unloaded or absorbed by docking). If confirmed destroyed,
        /// applies terminal state and removes from BackgroundMap.
        /// </summary>
        IEnumerator DeferredDestructionCheck(PendingDestruction pending)
        {
            yield return null; // defer one frame for KSP to finalize the destroy

            // Fix 2: activeTree could become null during scene change
            if (activeTree == null) yield break;

            // Fix 3: another handler (merge, promotion) may have already processed this vessel
            if (!activeTree.BackgroundMap.ContainsKey(pending.vesselPid)) yield break;

            // Use pure decision method to determine if vessel is truly destroyed
            bool vesselStillExists = FlightRecorder.FindVesselByPid(pending.vesselPid) != null;
            DeferredDestructionOutcome destructionOutcome = ClassifyDeferredDestruction(
                pending.vesselPid,
                dockingInProgress,
                vesselStillExists);
            if (TryHandleDeferredDestructionAbort(
                    pending.vesselPid,
                    destructionOutcome,
                    pid => backgroundRecorder?.OnVesselBackgrounded(pid),
                    Log,
                    message => ParsekLog.Info("Flight", message)))
            {
                yield break;
            }

            // Vessel is truly destroyed — apply terminal state
            Recording rec;
            if (!activeTree.Recordings.TryGetValue(pending.recordingId, out rec))
            {
                ParsekLog.Warn("Flight", $"DeferredDestructionCheck: recording '{pending.recordingId}' not found in tree");
                yield break;
            }

            // Phantom terrain crash detection: KSP sometimes crashes EVA vessels
            // through terrain during pack/unload. Detect by checking if the vessel
            // was in a safe situation (LANDED/SPLASHED) when packed and was destroyed
            // within a short time window. Override Destroyed → Landed/Splashed.
            bool isPhantomCrash = false;
            if (!string.IsNullOrEmpty(rec.EvaCrewName))
            {
                (double packUT, Vessel.Situations preSit) packState;
                if (packStates.TryGetValue(pending.vesselPid, out packState))
                {
                    isPhantomCrash = IsPhantomTerrainCrash(
                        rec.EvaCrewName, packState.packUT, pending.capturedUT, packState.preSit);
                    if (isPhantomCrash)
                    {
                        var safeTerm = packState.preSit == Vessel.Situations.LANDED
                            ? TerminalState.Landed : TerminalState.Splashed;
                        ParsekLog.Warn("Flight",
                            $"Suspected phantom terrain crash for EVA '{rec.VesselName}': " +
                            $"was {packState.preSit}, packed {pending.capturedUT - packState.packUT:F1}s " +
                            $"before destruction. Using {safeTerm} instead of Destroyed");
                        rec.TerminalStateValue = safeTerm;
                        rec.ExplicitEndUT = pending.capturedUT;
                        ApplyTerminalData(pending, rec);
                    }
                }
            }

            bool cacheApplied = false;
            if (!isPhantomCrash && backgroundRecorder != null)
            {
                RecordingFinalizationCacheApplyResult cacheResult;
                cacheApplied = backgroundRecorder.TryApplyFinalizationCacheForBackgroundEnd(
                    rec,
                    pending.vesselPid,
                    pending.capturedUT,
                    "DeferredDestructionCheck",
                    allowStale: true,
                    requireDestroyedTerminal: true,
                    out cacheResult);
            }

            if (!isPhantomCrash && !cacheApplied)
                ApplyTerminalDestruction(pending, rec);

            packStates.Remove(pending.vesselPid);
            activeTree.BackgroundMap.Remove(pending.vesselPid);

            BackgroundRecorder.PersistFinalizedRecording(
                rec,
                $"DeferredDestructionCheck pid={pending.vesselPid}");
            backgroundRecorder?.ForgetFinalizationCache(pending.vesselPid);

            if (!string.IsNullOrEmpty(rec.EvaCrewName))
                ParsekLog.Info("Flight", $"Background EVA vessel ended: pid={pending.vesselPid} recId={pending.recordingId}");
            else
                ParsekLog.Warn("Flight", $"Background vessel destroyed: pid={pending.vesselPid} recId={pending.recordingId}");

            // Check if all tree vessels are now destroyed — trigger merge dialog
            if (!treeDestructionDialogPending)
            {
                bool activeDestroyed = recorder == null || !recorder.IsRecording
                    || recorder.VesselDestroyedDuringRecording;
                if (RecordingTree.AreAllLeavesTerminal(activeTree.Recordings,
                    activeTree.ActiveRecordingId, activeDestroyed))
                {
                    treeDestructionDialogPending = true;
                    ParsekLog.Info("Flight",
                        "All tree leaves now terminal after background destruction — triggering tree merge");
                    StartCoroutine(ShowPostDestructionTreeMergeDialog());
                }
            }
        }

        #endregion

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
                        $"OnVesselSituationChange: ignoring non-active vessel ({data.from} → {data.to})");
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

            StartRecording(suppressStartScreenMessage: true);
            Log($"Auto-record started ({data.from} → {data.to})");
            ScreenMessage("Recording STARTED (auto)", 2f);
        }

        void OnCrewBoardVessel(GameEvents.FromToAction<Part, Part> data)
        {
            ParsekLog.RecState("OnCrewBoardVessel:entry", CaptureRecorderState());
            if (chainManager.ActiveChainId == null && activeTree == null)
            {
                ParsekLog.Verbose("Flight", "OnCrewBoardVessel: no active chain or tree — ignoring");
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
        }

        void OnCrewOnEva(GameEvents.FromToAction<Part, Part> data)
        {
            ParsekLog.RecState("OnCrewOnEva:entry", CaptureRecorderState());
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

                // Tree mode: stop recorder synchronously, defer tree branch creation
                recorder.StopRecordingForChainBoundary();
                pendingSplitRecorder = recorder;
                recorder = null;
                pendingSplitInProgress = true;
                Log($"Mid-recording EVA detected: '{kerbalName}' — starting deferred tree branch");
                StartCoroutine(DeferredEvaBranch(kerbalName, data));
                return;
            }

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

        internal static bool ShouldArmPostSwitchAutoRecord(
            bool autoRecordOnFirstModificationAfterSwitchEnabled,
            bool isRecording,
            bool hasNewVessel,
            bool newVesselIsGhost,
            bool newVesselIsEva)
        {
            return autoRecordOnFirstModificationAfterSwitchEnabled
                && !isRecording
                && hasNewVessel
                && !newVesselIsGhost
                && !newVesselIsEva;
        }

        internal static PostSwitchAutoRecordArmDecision EvaluatePostSwitchAutoRecordArmDecision(
            bool shouldArm,
            bool trackedInActiveTree)
        {
            if (!shouldArm)
                return PostSwitchAutoRecordArmDecision.None;

            return trackedInActiveTree
                ? PostSwitchAutoRecordArmDecision.ArmTrackedBackgroundMember
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

        internal static bool HasMeaningfulLandedMotionChange(
            double distanceDeltaMeters,
            double speedMetersPerSecond)
        {
            return distanceDeltaMeters >= PostSwitchLandedMotionDistanceThreshold
                || speedMetersPerSecond >= PostSwitchLandedMotionSpeedThreshold;
        }

        internal static float ComputePostSwitchAttitudeDeltaDegrees(
            Quaternion baselineWorldRotation,
            Quaternion currentWorldRotation)
        {
            return Quaternion.Angle(
                TrajectoryMath.NormalizeQuaternionForComparison(baselineWorldRotation),
                TrajectoryMath.NormalizeQuaternionForComparison(currentWorldRotation));
        }

        internal static bool HasMeaningfulAttitudeChange(
            uint armedVesselPid,
            uint activeVesselPid,
            bool hasBaselineRotation,
            Quaternion baselineWorldRotation,
            Quaternion currentWorldRotation,
            float thresholdDegrees = PostSwitchAttitudeChangeThresholdDegrees)
        {
            if (!hasBaselineRotation || armedVesselPid == 0 || activeVesselPid == 0
                || armedVesselPid != activeVesselPid)
            {
                return false;
            }

            return ComputePostSwitchAttitudeDeltaDegrees(
                baselineWorldRotation,
                currentWorldRotation) >= thresholdDegrees;
        }

        internal static bool HasMeaningfulOrbitChange(
            PostSwitchOrbitSnapshot baseline,
            PostSwitchOrbitSnapshot current)
        {
            if (!baseline.IsValid || !current.IsValid)
                return false;

            if (!string.Equals(baseline.BodyName, current.BodyName, StringComparison.Ordinal))
                return true;

            if (Math.Abs(current.SemiMajorAxis - baseline.SemiMajorAxis) >=
                PostSwitchOrbitSemiMajorAxisThresholdMeters)
                return true;
            if (Math.Abs(current.Eccentricity - baseline.Eccentricity) >=
                PostSwitchOrbitEccentricityThreshold)
                return true;
            if (NormalizeAngleDeltaDegrees(
                    current.Inclination,
                    baseline.Inclination) >= PostSwitchOrbitAngleThresholdDegrees)
                return true;
            if (NormalizeAngleDeltaDegrees(
                    current.LongitudeOfAscendingNode,
                    baseline.LongitudeOfAscendingNode) >= PostSwitchOrbitAngleThresholdDegrees)
                return true;
            return NormalizeAngleDeltaDegrees(
                current.ArgumentOfPeriapsis,
                baseline.ArgumentOfPeriapsis) >= PostSwitchOrbitAngleThresholdDegrees;
        }

        internal static bool HasMeaningfulCrewDelta(Dictionary<string, int> delta)
        {
            if (delta == null) return false;
            foreach (var entry in delta)
            {
                if (entry.Value != 0)
                    return true;
            }
            return false;
        }

        internal static int CountMeaningfulCrewDelta(Dictionary<string, int> delta)
        {
            if (delta == null) return 0;
            int count = 0;
            foreach (var entry in delta)
            {
                if (entry.Value != 0)
                    count++;
            }
            return count;
        }

        internal static bool HasMeaningfulResourceDelta(
            Dictionary<string, double> delta,
            double epsilon = PostSwitchResourceDeltaEpsilon)
        {
            if (delta == null) return false;
            foreach (var entry in delta)
            {
                if (Math.Abs(entry.Value) > epsilon)
                    return true;
            }
            return false;
        }

        internal static int CountMeaningfulResourceDelta(
            Dictionary<string, double> delta,
            double epsilon = PostSwitchResourceDeltaEpsilon)
        {
            if (delta == null) return 0;
            int count = 0;
            foreach (var entry in delta)
            {
                if (Math.Abs(entry.Value) > epsilon)
                    count++;
            }
            return count;
        }

        internal static bool HasMeaningfulInventoryDelta(Dictionary<string, InventoryItem> delta)
        {
            if (delta == null) return false;
            foreach (var entry in delta)
            {
                if (entry.Value.count != 0 || entry.Value.slotsTaken != 0)
                    return true;
            }
            return false;
        }

        internal static int CountMeaningfulInventoryDelta(Dictionary<string, InventoryItem> delta)
        {
            if (delta == null) return 0;
            int count = 0;
            foreach (var entry in delta)
            {
                if (entry.Value.count != 0 || entry.Value.slotsTaken != 0)
                    count++;
            }
            return count;
        }

        internal static bool HasMeaningfulPartStateTokenChange(
            ICollection<string> baselineTokens,
            ICollection<string> currentTokens)
        {
            int baselineCount = baselineTokens != null ? baselineTokens.Count : 0;
            int currentCount = currentTokens != null ? currentTokens.Count : 0;
            if (baselineCount != currentCount)
                return true;
            if (baselineCount == 0)
                return false;

            var baselineSet = baselineTokens as HashSet<string> ?? new HashSet<string>(baselineTokens);
            foreach (string token in currentTokens)
            {
                if (!baselineSet.Contains(token))
                    return true;
            }
            return false;
        }

        internal static int CountPartStateTokenDelta(
            ICollection<string> baselineTokens,
            ICollection<string> currentTokens)
        {
            int baselineCount = baselineTokens != null ? baselineTokens.Count : 0;
            int currentCount = currentTokens != null ? currentTokens.Count : 0;
            if (baselineCount == 0)
                return currentCount;
            if (currentCount == 0)
                return baselineCount;

            var baselineSet = baselineTokens as HashSet<string> ?? new HashSet<string>(baselineTokens);
            var currentSet = currentTokens as HashSet<string> ?? new HashSet<string>(currentTokens);
            int delta = 0;
            foreach (string token in currentSet)
            {
                if (!baselineSet.Contains(token))
                    delta++;
            }
            foreach (string token in baselineSet)
            {
                if (!currentSet.Contains(token))
                    delta++;
            }
            return delta;
        }

        internal static string FormatPostSwitchManifestDeltaSummary(
            uint vesselPid,
            int crewDeltaKeys,
            int resourceDeltaKeys,
            int inventoryDeltaKeys,
            int partStateTokenDelta,
            bool crewChanged,
            bool resourceChanged,
            bool partStateChanged,
            double nextEvaluationUt)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Post-switch manifest delta: pid={0} crewChanged={1} resourceChanged={2} partStateChanged={3} crewDeltaKeys={4} resourceDeltaKeys={5} inventoryDeltaKeys={6} partStateTokenDelta={7} nextEvalUT={8:F1}",
                vesselPid,
                crewChanged,
                resourceChanged,
                partStateChanged,
                FormatPostSwitchManifestDeltaCount(crewDeltaKeys),
                FormatPostSwitchManifestDeltaCount(resourceDeltaKeys),
                FormatPostSwitchManifestDeltaCount(inventoryDeltaKeys),
                FormatPostSwitchManifestDeltaCount(partStateTokenDelta),
                nextEvaluationUt);
        }

        internal static string FormatSplitSkipSummary(
            string source,
            string reason,
            string activeRecordingId,
            uint sourcePid,
            uint targetPid,
            bool pendingSplitInProgress)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0}: split path skipped reason={1} activeRec={2} sourcePid={3} targetPid={4} pendingSplitInProgress={5}",
                string.IsNullOrEmpty(source) ? "(unknown)" : source,
                string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                string.IsNullOrEmpty(activeRecordingId) ? "(none)" : activeRecordingId,
                sourcePid,
                targetPid,
                pendingSplitInProgress);
        }

        internal static bool HasMeaningfulPartStateChange(IEnumerable<PartEventType> changedTypes)
        {
            if (changedTypes == null) return false;
            foreach (PartEventType type in changedTypes)
            {
                if (GhostingTriggerClassifier.IsGhostingTrigger(type))
                    return true;
            }
            return false;
        }

        internal static PostSwitchAutoRecordTrigger EvaluatePostSwitchAutoRecordTrigger(
            bool engineTriggered,
            bool rcsTriggered,
            bool attitudeChanged,
            bool crewChanged,
            bool resourceChanged,
            bool partStateChanged,
            bool landedMotionChanged,
            bool orbitChanged)
        {
            if (engineTriggered)
                return PostSwitchAutoRecordTrigger.EngineActivity;
            if (rcsTriggered)
                return PostSwitchAutoRecordTrigger.SustainedRcsActivity;
            if (attitudeChanged)
                return PostSwitchAutoRecordTrigger.AttitudeChange;
            if (crewChanged)
                return PostSwitchAutoRecordTrigger.CrewChange;
            if (resourceChanged)
                return PostSwitchAutoRecordTrigger.ResourceChange;
            if (partStateChanged)
                return PostSwitchAutoRecordTrigger.PartStateChange;
            if (landedMotionChanged)
                return PostSwitchAutoRecordTrigger.LandedMotion;
            if (orbitChanged)
                return PostSwitchAutoRecordTrigger.OrbitChange;
            return PostSwitchAutoRecordTrigger.None;
        }

        internal static PostSwitchAutoRecordStartDecision EvaluatePostSwitchAutoRecordStartDecision(
            uint armedVesselPid,
            uint activeVesselPid,
            bool hasActiveTree,
            bool activeVesselTrackedInBackground,
            bool canRestorePendingTrackedTree,
            bool suppressStart)
        {
            if (suppressStart || armedVesselPid == 0 || activeVesselPid == 0
                || armedVesselPid != activeVesselPid)
                return PostSwitchAutoRecordStartDecision.None;
            if (hasActiveTree && activeVesselTrackedInBackground)
                return PostSwitchAutoRecordStartDecision.PromoteTrackedRecording;
            if (canRestorePendingTrackedTree)
                return PostSwitchAutoRecordStartDecision.RestoreAndPromoteTrackedRecording;
            return PostSwitchAutoRecordStartDecision.StartFreshRecording;
        }

        private bool HasPendingPostSwitchAutoRecordTransition()
        {
            return pendingTreeDockMerge
                || pendingSplitRecorder != null
                || pendingSplitInProgress
                || pendingBoardingTargetPid != 0
                || pendingDockMergedPid != 0
                || pendingUndockOtherPid != 0
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

            ConfigNode vesselSnapshot = VesselSpawner.TryBackupSnapshot(v);
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
            state.BaselineResources = VesselSpawner.ExtractResourceManifest(vesselSnapshot);
            state.BaselineInventory = VesselSpawner.ExtractInventoryManifest(vesselSnapshot, out _);
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
            bool needsCacheRefresh =
                state.ModuleCachesDirty || state.CachedPartCount != currentPartCount;
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
                ConfigNode currentSnapshot = VesselSpawner.TryBackupSnapshot(v);
                var currentCrew = VesselSpawner.ExtractCrewManifest(currentSnapshot);
                var crewDelta = CrewManifest.ComputeCrewDelta(state.BaselineCrew, currentCrew);
                int crewDeltaKeys = CountMeaningfulCrewDelta(crewDelta);
                int resourceDeltaKeys = PostSwitchManifestDeltaSkipped;
                int inventoryDeltaKeys = PostSwitchManifestDeltaSkipped;
                int partStateTokenDelta = PostSwitchManifestDeltaSkipped;
                crewChanged = crewDeltaKeys > 0;
                if (!crewChanged)
                {
                    var currentResources = VesselSpawner.ExtractResourceManifest(currentSnapshot);
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
                        var currentInventory = VesselSpawner.ExtractInventoryManifest(currentSnapshot, out _);
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
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
            };

            ConfigNode startSnapshot = VesselSpawner.TryBackupSnapshot(v);
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
                if (LedgerOrchestrator.IsInitialized)
                {
                    KspStatePatcher.PatchFacilities(LedgerOrchestrator.Facilities);
                    ParsekLog.Info("WarpFacilities",
                        $"Warp start at UT={warpStartUT:F2} — patched facility visuals");
                }
            }

            // Detect warp exit: was warping, now at 1x — recalculate ledger
            if (wasWarpActive && !isWarpNow)
            {
                double warpEndUT = Planetarium.GetUniversalTime();
                ParsekLog.Info("LedgerOrchestrator",
                    "Warp exit detected — recalculating ledger");
                LedgerOrchestrator.RecalculateAndPatch();

                // Log whether any facility actions were crossed during this warp session
                if (LedgerOrchestrator.IsInitialized &&
                    LedgerOrchestrator.HasFacilityActionsInRange(warpStartUT, warpEndUT))
                {
                    ParsekLog.Info("WarpFacilities",
                        $"Warp exit at UT={warpEndUT:F2} — facility actions crossed " +
                        $"in range ({warpStartUT:F2}, {warpEndUT:F2}]");
                }
            }
            wasWarpActive = isWarpNow;

            if (activeTree == null || backgroundRecorder == null) return;

            double ut = Planetarium.GetUniversalTime();
            float warpRate = TimeWarp.CurrentRate;

            // Bug #592: KSP fires onTimeWarpRateChanged very chattily — a single
            // playtest produced ~1090 events at 1.0x without any actual rate change
            // (scene transitions, warp-to-here, etc. retrigger the GameEvent).
            // Rate-limit by warpRate so transitions between distinct rates still log
            // immediately, but a burst of redundant 1x->1x fires collapses into one
            // line per window.
            string warpRateKey = warpRate.ToString("F1",
                System.Globalization.CultureInfo.InvariantCulture);
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

        void OnPartCouple(GameEvents.FromToAction<Part, Part> data)
        {
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

            ParsekLog.RecState("OnPartCouple:entry", CaptureRecorderState());
            if (data.to?.vessel == null) return;
            uint mergedPid = data.to.vessel.persistentId;

            // --- TREE MODE: create merge branch instead of chain segment ---
            if (activeTree != null && recorder != null)
            {
                if (pendingSplitInProgress) return; // split must complete first

                if (recorder.IsRecording)
                {
                    bool isTarget = (recorder.RecordingVesselId == mergedPid);
                    uint absorbedPid;

                    if (isTarget)
                    {
                        // We are the TARGET -- find the absorbed partner (Fix 2: pass Vessel)
                        absorbedPid = FindAbsorbedDockPartnerPid(mergedPid, data.to.vessel);
                    }
                    else
                    {
                        // We are the INITIATOR -- we are being absorbed
                        absorbedPid = recorder.RecordingVesselId;
                    }

                    // Stop recorder synchronously
                    recorder.StopRecordingForChainBoundary();

                    // Set pending state for tree merge
                    pendingTreeDockMerge = true;
                    pendingDockMergedPid = mergedPid;
                    pendingDockAbsorbedPid = absorbedPid;
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
                    // OnPhysicsFrame already stopped us (initiator, late event)
                    uint absorbedPid = recorder.RecordingVesselId;

                    pendingTreeDockMerge = true;
                    pendingDockMergedPid = mergedPid;
                    pendingDockAbsorbedPid = absorbedPid;
                    dockConfirmFrames = 0;

                    // Clear DockMergePending to prevent chain handler
                    recorder.DockMergePending = false;

                    if (absorbedPid != 0)
                        dockingInProgress.Add(absorbedPid);

                    Log($"onPartCouple (tree, retroactive): dock merge pending " +
                        $"(merged={mergedPid}, absorbed={absorbedPid})");
                    return;
                }
                // else: recorder exists but in some other state -- fall through to legacy
            }

            // --- LEGACY (non-tree) chain mode: unchanged ---
            if (recorder != null && recorder.IsRecording)
            {
                // We're actively recording
                if (mergedPid == recorder.RecordingVesselId)
                {
                    // We're the TARGET — no pid change will happen
                    pendingDockAsTarget = true;
                    pendingDockMergedPid = mergedPid;
                    dockConfirmFrames = 0;
                    // Stop recording silently — Update() will commit and restart
                    recorder.StopRecordingForChainBoundary();
                    Log($"onPartCouple: target dock detected (mergedPid={mergedPid})");
                }
                else
                {
                    // We're the INITIATOR — pid will change, OnPhysicsFrame will stop us
                    recorder.DockMergePending = true;
                    pendingDockMergedPid = mergedPid;
                    dockConfirmFrames = 0;
                    Log($"onPartCouple: initiator dock detected (mergedPid={mergedPid})");
                }
            }
            else if (recorder != null && !recorder.IsRecording && recorder.CaptureAtStop != null)
            {
                // OnPhysicsFrame already stopped us (initiator, pid changed before event fired)
                recorder.DockMergePending = true;
                pendingDockMergedPid = mergedPid;
                dockConfirmFrames = 0;
                Log($"onPartCouple: retroactive initiator dock (mergedPid={mergedPid})");
            }
        }

        void OnPartUndock(Part undockedPart)
        {
            // #267: skip if restore coroutine is mid-yield — it owns activeTree/recorder
            if (restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    $"OnPartUndock: skipped — restore coroutine in progress " +
                    $"(part '{undockedPart?.partInfo?.name}')");
                return;
            }

            ParsekLog.RecState("OnPartUndock:entry", CaptureRecorderState());
            if (recorder == null || !recorder.IsRecording) return;
            if (pendingSplitInProgress) return; // another split is already being processed
            if (undockedPart?.vessel == null)
            {
                ParsekLog.Verbose("Flight", "OnPartUndock: undocked part or vessel is null — ignoring");
                return;
            }

            uint newPid = undockedPart.vessel.persistentId;
            if (newPid == recorder.RecordingVesselId) return; // transient state, ignore

            // Tree mode: create branch instead of chain segment.
            // Stop recorder synchronously to prevent OnPhysicsFrame interference.
            recorder.StopRecordingForChainBoundary();
            pendingSplitRecorder = recorder;
            recorder = null;
            pendingSplitInProgress = true;
            Log($"onPartUndock: vessel split off (otherPid={newPid}) — starting deferred tree branch");
            StartCoroutine(DeferredUndockBranch(newPid));
        }

        void OnGroundSciencePartDeployed(ModuleGroundSciencePart deployedPart)
        {
            RecordInventoryPlacementEvent(
                deployedPart,
                PartEventType.InventoryPartPlaced,
                "onGroundSciencePartDeployed");
        }

        void OnGroundSciencePartRemoved(ModuleGroundSciencePart removedPart)
        {
            RecordInventoryPlacementEvent(
                removedPart,
                PartEventType.InventoryPartRemoved,
                "onGroundSciencePartRemoved");
        }

        void RecordInventoryPlacementEvent(
            ModuleGroundSciencePart module,
            PartEventType eventType,
            string sourceEvent)
        {
            if (recorder == null || !recorder.IsRecording) return;
            Part p = module?.part;
            if (p == null) return;

            var evt = new PartEvent
            {
                ut = Planetarium.GetUniversalTime(),
                partPersistentId = p.persistentId,
                eventType = eventType,
                partName = p.partInfo?.name ?? "unknown",
                moduleIndex = 0
            };
            recorder.PartEvents.Add(evt);

            Log($"Part event captured: {eventType} '{evt.partName}' pid={evt.partPersistentId} via {sourceEvent}");
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

            // Recording-specific: capture FlagEvent
            if (recorder == null || !recorder.IsRecording) return;

            string placedBy = flagSite.placedBy ?? "";
            Vessel recordedVessel = FlightRecorder.FindVesselByPid(recorder.RecordingVesselId);
            if (!ShouldRecordFlagEvent(placedBy, recordedVessel))
            {
                ParsekLog.Verbose("Flight",
                    $"Flag planted by '{placedBy}' but recorded vessel is '{recordedVessel?.vesselName}' — skipping");
                return;
            }

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
            recorder.FlagEvents.Add(fe);

            Log($"Flag event captured: '{fe.flagSiteName}' by '{fe.placedBy}' at " +
                $"({fe.latitude:F4},{fe.longitude:F4},{fe.altitude:F1}) on {fe.bodyName}");
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
        /// Bug #51 fix: when vessel-switch auto-stop fires during an active chain,
        /// commit the segment as a proper chain member and terminate the chain.
        /// Guards stay on ParsekFlight; commit logic delegates to chainManager.
        /// recorder = null on both success and abort paths.
        /// </summary>
        void HandleVesselSwitchChainTermination()
        {
            if (recorder == null || recorder.IsRecording || recorder.CaptureAtStop == null)
                return;
            if (chainManager.ActiveChainId == null)
                return;
            if (chainManager.PendingContinuation || recorder.ChainToVesselPending
                || recorder.DockMergePending || recorder.UndockSwitchPending)
                return;

            // Delegate commit to chain manager (handles both success and abort chain cleanup)
            chainManager.CommitVesselSwitchTermination(recorder);
            recorder = null;
        }

        /// <summary>
        /// Commits the current segment as an atmosphere boundary chain split.
        /// Delegates to chainManager.CommitBoundarySplit.
        /// </summary>
        void CommitBoundarySplit(string completedPhase, string bodyName)
        {
            chainManager.CommitBoundarySplit(recorder, completedPhase, bodyName);
        }

        void OnFlightReady()
        {
            Log("Flight ready. Checking for pending recordings...");
            ParsekLog.RecState("OnFlightReady", CaptureRecorderState());

            // #267: if a restore coroutine is already running (double OnFlightReady fire),
            // skip ResetFlightReadyState and the dispatch — the running coroutine owns
            // activeTree/recorder and ResetFlightReadyState would null them.
            if (restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    "OnFlightReady: restore coroutine already in progress — skipping reset and dispatch");
                return;
            }

            var restoreMode = ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady;
            if (ShouldIgnoreFlightReadyReset(
                hasActiveRecorder: recorder != null && recorder.IsRecording,
                hasActiveTree: activeTree != null,
                hasPendingTree: RecordingStore.HasPendingTree,
                restoreMode: restoreMode))
            {
                ParsekLog.Info("Flight",
                    "OnFlightReady: live recorder/tree already own the current flight — skipping reset");
                return;
            }

            // Reset scene-scoped state from the previous flight BEFORE the restore
            // coroutine runs. ResetFlightReadyState clears activeTree, backgroundRecorder,
            // chainManager, pendingSplitRecorder, etc. — all scene-scoped state that must
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
            }
            else
            {
                TryRestoreCommittedTreeForSpawnedActiveVessel();
            }
            // Belt-and-suspenders: recover orphaned spawned vessels that survived
            // the protoVessel stripping in OnLoad (e.g., FLIGHT→FLIGHT revert where
            // SpaceCenter was never visited, or name change edge cases).
            // Here FlightGlobals.Vessels is populated and vessel.persistentId is reliable.
            if (RecordingStore.PendingCleanupPids != null || RecordingStore.PendingCleanupNames != null)
            {
                CleanupOrphanedSpawnedVessels(
                    RecordingStore.PendingCleanupPids,
                    RecordingStore.PendingCleanupNames);
                RecordingStore.PendingCleanupPids = null;
                RecordingStore.PendingCleanupNames = null;
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

            // Capture active vessel PID at scene entry for stateless spawn dedup bypass.
            // After revert, the pad vessel shares the same PID as recorded vessels — at
            // spawn time, SpawnVesselOrChainTip checks this static PID instead of relying
            // on per-recording transient flags that could be lost on Recording recreation.
            RecordingStore.SceneEntryActiveVesselPid =
                (activeVessel != null) ? activeVessel.persistentId : 0;
            if (RecordingStore.SceneEntryActiveVesselPid != 0)
                ParsekLog.Verbose("Flight",
                    $"Scene entry active vessel pid={RecordingStore.SceneEntryActiveVesselPid}");

            // Handle pending tree: show tree merge dialog.
            // On non-revert scene changes, pending trees are auto-committed by ParsekScenario.
            // Reaching here means either a revert or a fallback (auto-commit missed).
            // #293: skip when restore coroutine is running — it owns the pending tree and
            // will either resume recording or leave it in Limbo. Without this guard, the
            // fallback fires in the same frame as StartCoroutine (before the coroutine pops
            // the tree), auto-merging it and leaving the vessel with no active recorder.
            if (RecordingStore.HasPendingTree && !restoringActiveTree)
            {
                var pt = RecordingStore.PendingTree;
                ParsekLog.Warn("Flight", $"Pending tree '{pt.TreeName}' reached OnFlightReady — showing tree merge dialog (fallback)");
                MergeDialog.ShowTreeDialog(pt);
            }
            else if (RecordingStore.HasPendingTree && restoringActiveTree)
            {
                ParsekLog.Info("Flight",
                    $"Pending tree '{RecordingStore.PendingTree.TreeName}' skipped — " +
                    "restore coroutine in progress (#293)");
            }

            // Swap reserved crew out of the active vessel so the player
            // can't record with them again (they belong to deferred-spawn vessels)
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

        #endregion

        #region Scene Change Helpers

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

        #endregion

        #region Flight Ready Helpers

        /// <summary>
        /// Resets all transient state on flight ready: background recorder, tree, chain,
        /// split detection, dock/undock, merge detection. Called at the start of OnFlightReady.
        /// </summary>
        private void ResetFlightReadyState()
        {
            ParsekLog.Info("Flight", "Resetting flight-ready state");
            ParsekLog.RecState("ResetFlightReady:entry", CaptureRecorderState());
            DisarmPostSwitchAutoRecord("flight ready reset");

            // Phase 6b: Clean up ghost chain state from previous flight scene
            vesselGhoster?.CleanupAll();
            vesselGhoster = null;
            activeGhostChains = null;


            // Shutdown background recorder before clearing tree
            if (backgroundRecorder != null)
            {
                backgroundRecorder.Shutdown();
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                backgroundRecorder = null;
            }

            // Clear tree state on flight ready (revert resets everything)
            activeTree = null;

            // Clear split event detection state
            lastBranchUT = -1;
            lastBranchVesselPids.Clear();
            pendingSplitInProgress = false;
            pendingSplitRecorder = null;
            preBreakVesselPids = null;

            // Clear chain state on flight ready (revert resets everything)
            chainManager.ClearAll();
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
        /// </summary>
        private void OnTreeCommittedFromMergeDialog()
        {
            ParsekLog.Info("GhostMap", "Tree committed from merge dialog — re-evaluating ghost chains");
            EvaluateAndApplyGhostChains();
        }

        /// <summary>
        /// Evaluates ghost chains from committed trees and ghosts claimed vessels.
        /// Called during OnFlightReady after initialization/migration but before
        /// pending tree dialog. Ghost chain state is NOT persisted — it is re-derived
        /// from committed recordings on every scene load.
        /// </summary>
        private void EvaluateAndApplyGhostChains()
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees == null || trees.Count == 0)
            {
                activeGhostChains = null;
    
                ParsekLog.Verbose("Ghoster", "EvaluateAndApplyGhostChains: no committed trees");
                return;
            }

            double currentUT = Planetarium.GetUniversalTime();
            var chains = GhostChainWalker.ComputeAllGhostChains(trees, currentUT);

            if (chains == null || chains.Count == 0)
            {
                activeGhostChains = null;
    
                ParsekLog.Verbose("Ghoster", "EvaluateAndApplyGhostChains: no ghost chains found");
                return;
            }

            if (vesselGhoster == null)
                vesselGhoster = new VesselGhoster();

            // Only store and ghost chains whose spawn UT is in the future.
            // Past-UT chains are already resolved — storing them would cause
            // spurious spawn suppression and duplicate chain-tip spawn attempts.
            int ghostedCount = FilterAndGhostChains(chains, currentUT, out var activeChains);

            activeGhostChains = activeChains;

            // Wire the ghosted-vessel check for ShouldSkipExternalVesselGhost (Task 6b-3b)
            GhostPlaybackLogic.SetIsGhostedOverride(
                pid => vesselGhoster != null && vesselGhoster.IsGhosted(pid));

            ParsekLog.Info("Ghoster",
                string.Format(CultureInfo.InvariantCulture,
                    "Ghost chains evaluated: {0} chain(s), {1} vessel(s) ghosted",
                    chains.Count, ghostedCount));
        }

        /// <summary>
        /// Filters chains to only future-UT non-terminated chains, creates ghost map vessels
        /// for orbital tips, and ghosts real vessels in the scene.
        /// Returns the number of vessels successfully ghosted.
        /// </summary>
        private int FilterAndGhostChains(Dictionary<uint, GhostChain> chains, double currentUT,
            out Dictionary<uint, GhostChain> activeChains)
        {
            activeChains = new Dictionary<uint, GhostChain>();
            int ghostedCount = 0;
            foreach (var kvp in chains)
            {
                uint pid = kvp.Key;
                GhostChain chain = kvp.Value;

                if (currentUT >= chain.SpawnUT)
                {
                    ParsekLog.Verbose("Ghoster",
                        string.Format(CultureInfo.InvariantCulture,
                            "Skipping chain for pid={0}: currentUT={1:F1} >= spawnUT={2:F1}",
                            pid, currentUT, chain.SpawnUT));
                    continue;
                }

                // #174: Skip terminated chains — vessel destroyed/recovered, can never produce ghost
                if (chain.IsTerminated)
                {
                    ParsekLog.Verbose("Ghoster",
                        string.Format(CultureInfo.InvariantCulture,
                            "Skipping terminated chain for pid={0}", pid));
                    continue;
                }

                activeChains[pid] = chain;

                // Create ghost map vessel for chains ending in stable orbit
                // (terminated chains already filtered by the continue above)
                if (!string.IsNullOrEmpty(chain.TipRecordingId))
                {
                    Recording tipRecording = FindTipRecordingById(chain.TipRecordingId);
                    if (tipRecording != null
                        && (!tipRecording.TerminalStateValue.HasValue
                            || tipRecording.TerminalStateValue == TerminalState.Orbiting
                            || tipRecording.TerminalStateValue == TerminalState.Docked))
                        GhostMapPresence.CreateGhostVessel(chain, tipRecording);
                }

                // Only attempt ghosting if the vessel actually exists in the scene.
                // Tree recordings may reference vessels that were never spawned (e.g.,
                // synthetic test data or vessels in a different SOI/scene).
                Vessel chainVessel = FlightRecorder.FindVesselByPid(pid);
                if (chainVessel == null)
                {
                    ParsekLog.Verbose("Ghoster", string.Format(CultureInfo.InvariantCulture,
                        "Chain vessel pid={0} not loaded in scene — nothing to ghost", pid));
                }
                else if (vesselGhoster.GhostVessel(pid))
                    ghostedCount++;
                else
                {
                    ParsekLog.Warn("Ghoster", string.Format(CultureInfo.InvariantCulture,
                        "Failed to ghost vessel pid={0} for chain — vessel may remain real", pid));
                }
            }
            return ghostedCount;
        }

        #endregion

        #region Update Helpers

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
                    pendingDockAsTarget = false;
                    pendingTreeDockMerge = false;
                    pendingDockAbsorbedPid = 0;
                    dockConfirmFrames = 0;
                }
            }
            if (pendingUndockOtherPid != 0)
            {
                undockConfirmFrames++;
                if (undockConfirmFrames > 5)
                {
                    ParsekLog.Info("Flight", $"Undock confirmation expired (otherPid={pendingUndockOtherPid})");
                    pendingUndockOtherPid = 0;
                    undockConfirmFrames = 0;
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
        /// Zeros all dock/undock pending-transition fields.
        /// Called from scene change, flight ready, and after dock/undock commit/restart.
        /// </summary>
        private void ClearDockUndockState()
        {
            pendingDockMergedPid = 0;
            pendingDockAsTarget = false;
            dockConfirmFrames = 0;
            pendingUndockOtherPid = 0;
            undockConfirmFrames = 0;
            pendingDockAbsorbedPid = 0;
        }

        /// <summary>
        /// Restarts recording after a dock/undock chain boundary: nulls the old recorder,
        /// starts a new recording, sets UndockSiblingPid, and logs/screen-messages the result.
        /// Optional <paramref name="onRecordingStarted"/> callback runs inside the IsRecording
        /// guard before the sibling PID assignment (used by undock paths to start continuation).
        /// </summary>
        private void RestartRecordingAfterDockUndock(string logReason, string screenReason, Action onRecordingStarted = null)
        {
            recorder = null;
            StartRecording();
            if (IsRecording)
            {
                onRecordingStarted?.Invoke();
                recorder.UndockSiblingPid = chainManager.UndockContinuationPid;
                Log($"Recording continues after {logReason} (chain={chainManager.ActiveChainId}, idx={chainManager.ActiveChainNextIndex})");
                ScreenMessage($"Recording continues ({screenReason})", 2f);
            }
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

            CreateMergeBranch(
                BranchPointType.Dock,
                pendingDockMergedPid,
                activeParentId,
                bgParentId,
                mergeUT,
                stoppedRecorder);

            // Clean up
            if (pendingDockAbsorbedPid != 0)
                dockingInProgress.Remove(pendingDockAbsorbedPid);

            pendingTreeDockMerge = false;
            pendingDockMergedPid = 0;
            pendingDockAbsorbedPid = 0;
            dockConfirmFrames = 0;
            pendingDockAsTarget = false;

            Log("Tree dock merge completed");
        }

        /// <summary>
        /// Handles dock/undock commit and restart: commits the stopped recorder segment,
        /// then restarts recording on the merged/remaining vessel.
        /// </summary>
        private void HandleDockUndockCommitRestart()
        {
            // Dock: initiator (pid changed, recorder already stopped by OnPhysicsFrame)
            if (recorder != null && recorder.DockMergePending &&
                !recorder.IsRecording && recorder.CaptureAtStop != null)
            {
                if (!chainManager.CommitDockUndockSegment(recorder, PartEventType.Docked, pendingDockMergedPid))
                    ClearDockUndockState();
                RestartRecordingAfterDockUndock("dock", "docked");
                ClearDockUndockState();
            }

            // Dock: target (pid unchanged, recorder already stopped by OnPartCouple)
            if (pendingDockAsTarget && recorder != null &&
                !recorder.IsRecording && recorder.CaptureAtStop != null)
            {
                if (!chainManager.CommitDockUndockSegment(recorder, PartEventType.Docked, pendingDockMergedPid))
                    ClearDockUndockState();
                RestartRecordingAfterDockUndock("dock as target", "docked");
                ClearDockUndockState();
            }

            // Undock: player stays on remaining vessel (same pid, recorder stopped by StopRecordingForChainBoundary)
            if (pendingUndockOtherPid != 0 && recorder != null &&
                !recorder.IsRecording && recorder.CaptureAtStop != null &&
                !recorder.UndockSwitchPending)
            {
                if (!chainManager.CommitDockUndockSegment(recorder, PartEventType.Undocked, 0))
                    ClearDockUndockState();
                uint undockPid = pendingUndockOtherPid;
                RestartRecordingAfterDockUndock("undock", "undocked",
                    () => chainManager.StartUndockContinuation(undockPid));
                ClearDockUndockState();
            }

            // Undock: player switched to undocked vessel (pid changed, recorder stopped by OnPhysicsFrame)
            if (recorder != null && recorder.UndockSwitchPending &&
                !recorder.IsRecording && recorder.CaptureAtStop != null)
            {
                uint oldPid = recorder.RecordingVesselId;
                if (!chainManager.CommitDockUndockSegment(recorder, PartEventType.Undocked, 0))
                    ClearDockUndockState();
                recorder = null;

                // Stop existing undock continuation (we're switching roles)
                // Bug #95: bake before stop — old trajectory is real
                if (chainManager.UndockContinuationPid != 0)
                {
                    if (chainManager.TryGetUndockContinuationRecording(out var undockRec))
                        ChainSegmentManager.BakeContinuationData(undockRec);
                    chainManager.StopUndockContinuation("sibling switch");
                }

                RestartRecordingAfterDockUndock("undock switch", "undocked",
                    () => chainManager.StartUndockContinuation(oldPid));
                ClearDockUndockState();
            }
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
                stoppedRecorder);

            pendingBoardingTargetPid = 0;
            boardingConfirmFrames = 0;
            pendingBoardingTargetInTree = false;
            chainManager.ActiveChainCrewName = null;

            Log("Tree board merge completed");
        }

        /// <summary>
        /// Handles chain boarding transition: auto-commits EVA segment when boarding
        /// detected (EVA to vessel), or treats as normal stop if not in a chain.
        /// </summary>
        private void HandleChainBoardingTransition()
        {
            if (recorder == null || !recorder.ChainToVesselPending
                || recorder.IsRecording || recorder.CaptureAtStop == null)
                return;

            // Only continue the chain if we're already in one AND the boarding event confirmed
            if (chainManager.ActiveChainId != null &&
                pendingBoardingTargetPid != 0 &&
                FlightGlobals.ActiveVessel != null &&
                FlightGlobals.ActiveVessel.persistentId == pendingBoardingTargetPid)
            {
                Log($"Chain boarding confirmed: EVA\u2192vessel pid={pendingBoardingTargetPid}");
                pendingBoardingTargetPid = 0;
                boardingConfirmFrames = 0;

                chainManager.CommitChainSegment(recorder, chainManager.ActiveChainCrewName);
                recorder = null;

                // Start new vessel recording on the boarded vessel
                chainManager.ActiveChainCrewName = null; // vessel segment, not EVA
                StartRecording();
                if (IsRecording)
                {
                    Log($"Chain vessel recording started (chain={chainManager.ActiveChainId}, idx={chainManager.ActiveChainNextIndex})");
                    ScreenMessage("Recording STARTED (boarded vessel)", 2f);
                }
            }
            else
            {
                // Not in a chain or boarding not confirmed — treat as normal stop
                recorder.ChainToVesselPending = false;
                pendingBoardingTargetPid = 0;
                boardingConfirmFrames = 0;
                Log("ChainToVessel without active chain or boarding confirmation \u2014 treating as normal stop");
                ParsekLog.ScreenMessage("Recording stopped - vessel changed", 3f);
                // Leave CaptureAtStop intact for normal revert/merge handling
            }
        }

        /// <summary>
        /// Shared tail for boundary splits: stops recording, commits the segment,
        /// restarts recording and restores undock continuation PID.
        /// </summary>
        private void CommitBoundaryAndRestart(string phase, string bodyName,
            string logMessage, string screenMessage)
        {
            recorder.StopRecordingForChainBoundary();
            CommitBoundarySplit(phase, bodyName);
            recorder = null;
            StartRecording();
            if (IsRecording)
            {
                recorder.UndockSiblingPid = chainManager.UndockContinuationPid;
                ParsekLog.Info("Flight", logMessage);
                ParsekLog.ScreenMessage(screenMessage, 2f);
            }
        }

        /// <summary>
        /// Returns true if the current recording mode suppresses environment boundary
        /// splits (tree mode does — chain mode does not).
        /// </summary>
        internal static bool ShouldSuppressBoundarySplit(RecordingTree activeTree)
        {
            return activeTree != null;
        }

        /// <summary>
        /// Handles atmosphere boundary auto-split when crossing the atmosphere edge.
        /// Commits the current segment, restarts recording in the new phase.
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

            string phase = recorder.EnteredAtmosphere ? "exo" : "atmo";
            string newPhase = recorder.EnteredAtmosphere ? "atmo" : "exo";
            string bodyName = FlightGlobals.ActiveVessel?.mainBody?.name ?? "Unknown";
            ParsekLog.Info("Flight", $"Atmosphere auto-split triggered: {bodyName} {phase}\u2192{newPhase} " +
                $"(chain={chainManager.ActiveChainId ?? "(new)"}, points={recorder.Recording.Count})");
            CommitBoundaryAndRestart(phase, bodyName,
                $"Recording continues after atmosphere boundary " +
                    $"(chain={chainManager.ActiveChainId}, idx={chainManager.ActiveChainNextIndex}, {bodyName} {phase}\u2192{newPhase})",
                $"Recording continues ({(newPhase == "atmo" ? "entering" : "exiting")} atmosphere)");
        }

        /// <summary>
        /// Handles SOI change auto-split when transitioning between celestial bodies.
        /// Commits the current segment, restarts recording in the new SOI.
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

            string fromBody = recorder.SoiChangeFromBody ?? "Unknown";
            string toBody = FlightGlobals.ActiveVessel?.mainBody?.name ?? "Unknown";
            // Completed segment was at fromBody — tag based on atmosphere/altitude
            CelestialBody fromCB = FlightGlobals.Bodies?.Find(b => b.name == fromBody);
            string fromPhase;
            if (fromCB != null && fromCB.atmosphere)
                fromPhase = "exo"; // was in space around atmospheric body
            else if (fromCB != null)
            {
                // Use LastRecordedAltitude (cached field) instead of peeking at the
                // Recording buffer. The buffer can legitimately be empty here because
                // FlushRecorderIntoActiveTreeForSerialization clears it on every OnSave
                // while IsRecording stays true. The cached field is updated every
                // CommitRecordedPoint / SamplePosition, so it reflects the true last
                // altitude regardless of whether a flush happened since.
                double threshold = FlightRecorder.ComputeApproachAltitude(fromCB);
                fromPhase = !double.IsNaN(recorder.LastRecordedAltitude)
                    && recorder.LastRecordedAltitude < threshold
                    ? "approach" : "exo";
            }
            else
                fromPhase = "exo";
            ParsekLog.Info("Flight", $"SOI auto-split triggered: {fromBody} ({fromPhase}) \u2192 {toBody} " +
                $"(chain={chainManager.ActiveChainId ?? "(new)"}, points={recorder.Recording.Count})");
            CommitBoundaryAndRestart(fromPhase, fromBody,
                $"Recording continues after SOI change " +
                    $"({fromBody} \u2192 {toBody}, chain={chainManager.ActiveChainId}, idx={chainManager.ActiveChainNextIndex})",
                $"Recording continues (entering {toBody} SOI)");
        }

        /// <summary>
        /// Handles altitude boundary auto-split when crossing the approach altitude threshold
        /// on an airless body. Commits the current segment, restarts recording in the new phase.
        /// Mirrors HandleAtmosphereBoundarySplit.
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

            // Completed phase is the one we just left:
            // descended below threshold = completed "exo" segment
            // ascended above threshold = completed "approach" segment
            string phase = recorder.DescendedBelowThreshold ? "exo" : "approach";
            string newPhase = recorder.DescendedBelowThreshold ? "approach" : "exo";
            string bodyName = FlightGlobals.ActiveVessel?.mainBody?.name ?? "Unknown";
            ParsekLog.Info("Flight", $"Altitude auto-split triggered: {bodyName} {phase}\u2192{newPhase} " +
                $"(chain={chainManager.ActiveChainId ?? "(new)"}, points={recorder.Recording.Count})");
            CommitBoundaryAndRestart(phase, bodyName,
                $"Recording continues after altitude boundary " +
                    $"(chain={chainManager.ActiveChainId}, idx={chainManager.ActiveChainNextIndex}, {bodyName} {phase}\u2192{newPhase})",
                $"Recording continues ({(newPhase == "approach" ? "entering approach zone" : "leaving approach zone")})");
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
            // Capture controller status NOW — vessel parts may be cleared by the deferred check
            bool hasController = IsTrackableVessel(newVessel);
            if (decoupleControllerStatus != null)
                decoupleControllerStatus[newVessel.persistentId] = hasController;
            double splitUT = Planetarium.GetUniversalTime();
            if (decoupleCreatedTrajectoryPoints != null
                && !decoupleCreatedTrajectoryPoints.ContainsKey(newVessel.persistentId))
            {
                decoupleCreatedTrajectoryPoints[newVessel.persistentId] =
                    BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                        newVessel, splitUT, preferRootPartSurfacePose: true);
            }
            ParsekLog.Info("Flight",
                $"Decouple created vessel during recording: pid={newVessel.persistentId} " +
                $"name='{newVessel.vesselName}' type={newVessel.vesselType} " +
                $"parts={newVessel.parts?.Count ?? 0} hasController={hasController} " +
                $"splitUT={splitUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"capturedSeed={(decoupleCreatedTrajectoryPoints != null && decoupleCreatedTrajectoryPoints.ContainsKey(newVessel.persistentId) ? "T" : "F")}");
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

            StartRecording(suppressStartScreenMessage: true);

            if (IsRecording)
            {
                pendingAutoRecord = false;

                if (chainManager.PendingContinuation)
                {
                    chainManager.PendingContinuation = false;
                    chainManager.PendingEvaName = null;
                    Log($"Chain EVA recording started (chain={chainManager.ActiveChainId}, idx={chainManager.ActiveChainNextIndex}, crew={chainManager.ActiveChainCrewName})");
                    ScreenMessage("Recording STARTED (EVA chain)", 2f);
                }
                else
                {
                    Log("Auto-record started (EVA from pad)");
                    ScreenMessage("Recording STARTED (auto - EVA from pad)", 2f);
                }
            }
            // else: StartRecording failed (paused game or no active vessel).
            // Keep pending flags so we retry next frame; fall through to
            // HandleInput/playback so they aren't starved.
        }

        #endregion

        #region Input Handling

        void HandleInput()
        {
            // [ or ] — Exit watch mode (return to vessel)
            // Avoids Backspace which is KSP's Abort action group key
            if (watchMode.IsWatchingGhost
                && (Input.GetKeyDown(KeyCode.LeftBracket) || Input.GetKeyDown(KeyCode.RightBracket)))
            {
                watchMode.ExitWatchMode();
            }

            // V — Toggle watch camera mode (Free / Horizon-Locked)
            // Stock V (camera mode switch) is blocked by CAMERAMODES control lock during watch
            if (watchMode.IsWatchingGhost && Input.GetKeyDown(KeyCode.V))
            {
                watchMode.ToggleCameraMode();
            }

        }

        #endregion

        #region Recording

        // Pass suppressStartScreenMessage=true for fresh, non-continuation starts where
        // the caller posts its own custom screen message.
        public void StartRecording(bool suppressStartScreenMessage = false)
        {
            // Always-tree mode makes a chain continuation without a live tree impossible.
            // If we reach StartRecording with orphaned chain/transient state, treat it as
            // stale session residue and start a fresh tree-backed recording instead of
            // silently creating a live recorder with no ActiveRecordingId.
            if (activeTree == null
                && (chainManager.ActiveTreeId != null
                    || chainManager.ActiveChainId != null
                    || chainManager.PendingContinuation
                    || chainManager.PendingBoundaryAnchor.HasValue))
            {
                ParsekLog.Warn("Flight",
                    $"StartRecording: clearing stale chain state without active tree " +
                    $"(treeId={chainManager.ActiveTreeId ?? "null"}, " +
                    $"chainId={chainManager.ActiveChainId ?? "null"}, " +
                    $"pendingContinuation={chainManager.PendingContinuation}, " +
                    $"hasBoundaryAnchor={chainManager.PendingBoundaryAnchor.HasValue})");
                chainManager.ClearAll();
            }

            // Chain continuations (atmosphere/SOI splits, dock/undock, boarding) are NOT
            // new launches — they must not capture a fresh rewind save.  The rewind save
            // belongs to the chain root only.  chainManager.ActiveChainId is set by
            // CommitBoundarySplit / CommitChainSegment / CommitDockUndockSegment *before*
            // this method is called, so a non-null value reliably indicates a continuation.
            bool isContinuation = chainManager.ActiveChainId != null;

            // Commit orphaned CaptureAtStop from a previous recorder that was stopped
            // by vessel switch but never committed (e.g., auto-record started on new
            // vessel before scene change). Without this, the old recording data is lost.
            if (recorder != null && !recorder.IsRecording && recorder.CaptureAtStop != null
                && chainManager.ActiveChainId == null && activeTree == null)
            {
                FallbackCommitSplitRecorder(recorder);
                ParsekLog.Info("Flight", "Committed orphaned recording before starting new one");
            }

            recorder = new FlightRecorder();
            if (chainManager.PendingBoundaryAnchor.HasValue)
            {
                recorder.BoundaryAnchor = chainManager.PendingBoundaryAnchor;
                chainManager.PendingBoundaryAnchor = null;
            }

            // Bug #271: always-tree mode. When no tree exists and this is not a
            // chain continuation, wrap the new recording in a single-node tree.
            // This eliminates the standalone/tree dual-path architecture — every
            // recording is a tree recording, even single-vessel flights.
            if (activeTree == null && !isContinuation)
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
                    RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
                };
                // Capture GhostVisualSnapshot at recording start — the FULL vessel
                // before any staging or breakup events. This is the snapshot used for
                // ghost mesh rendering. VesselSnapshot is captured later at stop/split
                // time and may reflect a reduced part count.
                if (FlightGlobals.ActiveVessel != null)
                {
                    var startSnap = VesselSpawner.TryBackupSnapshot(FlightGlobals.ActiveVessel);
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
            {
                recorder.ActiveTree = activeTree;
                chainManager.ActiveTreeId = activeTree.Id;
            }
            recorder.StartRecording(
                isPromotion: isContinuation,
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
                    activeTree = null;
                    ParsekLog.Info("Flight", "Cleaned up single-node tree after StartRecording failure");
                }
                return;
            }

            PrepareSessionStateForRecorderStart("StartRecording");

            uint pid = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.persistentId : 0;
            ParsekLog.Info("Flight", $"StartRecording succeeded: pid={pid}, chainActive={chainManager.ActiveChainId != null}, tree={activeTree != null}");
            ParsekLog.RecState("StartRecording:post", CaptureRecorderState());
        }

        private void PrepareSessionStateForRecorderStart(string reason)
        {
            DisarmPostSwitchAutoRecord(reason);

            // Reset crash coalescer on every recorder start so a fresh recorder cannot
            // inherit a stale coalescing window from the previous segment or scene.
            if (crashCoalescer != null)
            {
                crashCoalescer.Reset();
                ParsekLog.Verbose("Coalescer", "Coalescer reset on recording start");
            }

            // Enforce minimum debris persistence so decoupled stages survive long enough
            // to be detected as vessel splits and recorded as background vessels.
            EnforceMinDebrisPersistence();

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

        /// <summary>
        /// Ensures KSP's MAX_PERSISTENT_DEBRIS is at least MinDebrisForRecording during recording.
        /// Without this, decoupled stages (boosters, fairings) are destroyed before Parsek can
        /// detect them as vessel splits and create background recordings.
        /// </summary>
        // Cached reflection field for KSP's maxPersistentDebris setting
        private static System.Reflection.FieldInfo debrisField;
        private static bool debrisFieldSearched;

        void EnforceMinDebrisPersistence()
        {
            if (debrisOverrideActive) return;

            try
            {
                int? current = GetMaxPersistentDebris();
                if (current.HasValue && current.Value < MinDebrisForRecording)
                {
                    savedMaxPersistentDebris = current.Value;
                    SetMaxPersistentDebris(MinDebrisForRecording);
                    debrisOverrideActive = true;
                    ParsekLog.Info("Flight",
                        $"Debris persistence overridden: {current.Value} -> {MinDebrisForRecording} " +
                        "(will restore when recording stops)");
                }
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("Flight",
                    $"Could not check/set debris persistence: {ex.GetType().Name}");
            }
        }

        void RestoreDebrisPersistence()
        {
            if (!debrisOverrideActive) return;

            try
            {
                SetMaxPersistentDebris(savedMaxPersistentDebris);
                ParsekLog.Info("Flight",
                    $"Debris persistence restored: {savedMaxPersistentDebris}");
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("Flight",
                    $"Could not restore debris persistence: {ex.GetType().Name}");
            }
            debrisOverrideActive = false;
        }

        /// <summary>
        /// Gets KSP's maxPersistentDebris value via reflection.
        /// KSP stores this in GameSettings as a field (name varies by version).
        /// </summary>
        static int? GetMaxPersistentDebris()
        {
            var field = FindDebrisField();
            if (field != null)
                return (int)field.GetValue(null);
            return null;
        }

        static void SetMaxPersistentDebris(int value)
        {
            var field = FindDebrisField();
            if (field != null)
                field.SetValue(null, value);
        }

        static System.Reflection.FieldInfo FindDebrisField()
        {
            if (debrisFieldSearched) return debrisField;
            debrisFieldSearched = true;

            // Search GameSettings for a static int field containing "debris" (case-insensitive)
            var gsType = typeof(GameSettings);
            var fields = gsType.GetFields(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Static);
            foreach (var f in fields)
            {
                if (f.FieldType == typeof(int) &&
                    f.Name.IndexOf("debris", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    debrisField = f;
                    ParsekLog.Info("Flight", $"Found debris persistence field: GameSettings.{f.Name}");
                    return debrisField;
                }
            }

            ParsekLog.Verbose("Flight", "No debris persistence field found in GameSettings");
            return null;
        }

        public void StopRecording()
        {
            ParsekLog.Info("Flight", "StopRecording called");
            RestoreDebrisPersistence();
            // Unsubscribe decouple listener
            if (decoupleCreatedVessels != null)
            {
                GameEvents.onPartDeCoupleNewVesselComplete.Remove(OnDecoupleNewVesselDuringSplitCheck);
                decoupleCreatedVessels = null;
                decoupleControllerStatus = null;
                decoupleCreatedTrajectoryPoints = null;
            }
            pendingDeferredSplitCheckTrigger = DeferredSplitCheckTrigger.None;
            pendingSplitTriggerUT = double.NaN;
            recorder?.StopRecording();

            // Tag the final segment with chain metadata if in a chain
            if (recorder?.CaptureAtStop != null)
                chainManager.ApplyChainMetadataTo(recorder.CaptureAtStop);

            // Tag final segment phase if untagged
            if (recorder?.CaptureAtStop != null && string.IsNullOrEmpty(recorder.CaptureAtStop.SegmentPhase))
            {
                var v = FlightGlobals.ActiveVessel;
                if (v != null && v.mainBody != null)
                {
                    recorder.CaptureAtStop.SegmentBodyName = v.mainBody.name;
                    if (v.situation == Vessel.Situations.LANDED
                        || v.situation == Vessel.Situations.SPLASHED
                        || v.situation == Vessel.Situations.PRELAUNCH)
                    {
                        recorder.CaptureAtStop.SegmentPhase = "surface";
                    }
                    else if (v.mainBody.atmosphere)
                        recorder.CaptureAtStop.SegmentPhase = v.altitude < v.mainBody.atmosphereDepth ? "atmo" : "exo";
                    else
                    {
                        double threshold = FlightRecorder.ComputeApproachAltitude(v.mainBody);
                        recorder.CaptureAtStop.SegmentPhase = v.altitude < threshold ? "approach" : "exo";
                    }
                    ParsekLog.Verbose("Flight", $"Final segment tagged (StopRecording): " +
                        $"{recorder.CaptureAtStop.SegmentBodyName} {recorder.CaptureAtStop.SegmentPhase}");
                }
            }
        }

        public void ClearRecording()
        {
            recorder = null;
            lastPlaybackIndex = 0;
            GameStateRecorder.PendingScienceSubjects.Clear();
            Log("Recording cleared");
        }

        /// <summary>
        /// Commits the active recording tree from the Commit Flight button.
        /// Finalizes all recordings, spawns leaf vessels, reserves crew.
        /// The active vessel stays live (VesselSpawned=true).
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

            // Stop continuations
            chainManager.StopAllContinuations("tree commit");

            // Finalize all recordings (active + background)
            FinalizeTreeRecordings(activeTree, commitUT, isSceneExit: false);

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

            // Recalculate crew reservations (replaces ReserveCrewForLeaves)
            LedgerOrchestrator.NotifyLedgerTreeCommitted(activeTree);

            // Spawn all non-active leaf vessels
            SpawnTreeLeaves(activeTree, activeRecId);

            // Crew swap on active vessel
            int swapped = CrewReservationManager.SwapReservedCrewInFlight();
            if (swapped > 0)
                ParsekLog.Info("Flight", $"CommitTreeFlight: swapped {swapped} crew on active vessel");

            int spawnCount = 0;
            foreach (var leaf in spawnableLeaves)
            {
                if (leaf.RecordingId != activeRecId && leaf.VesselSpawned)
                    spawnCount++;
            }

            // Clear state
            var treeName = activeTree.TreeName;
            activeTree = null;
            if (backgroundRecorder != null)
            {
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                backgroundRecorder = null;
            }
            recorder = null;
            lastPlaybackIndex = 0;

            Log($"CommitTreeFlight: committed tree \"{treeName}\" — {spawnCount} vessel(s) spawned");
            if (spawnCount > 0)
                ParsekLog.ScreenMessage($"Tree committed to timeline! {spawnCount} vessel(s) spawned.", 3f);
            else
                ParsekLog.ScreenMessage("Tree committed to timeline!", 3f);
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
            ParsekLog.RecState("CommitTreeRevert:entry", CaptureRecorderState());

            // Finalize all recordings (active + background)
            FinalizeTreeRecordings(activeTree, commitUT, isSceneExit: false);

            // Stash as pending tree -- snapshots preserved for merge dialog
            RecordingStore.StashPendingTree(activeTree);

            ParsekLog.Info("Flight",
                $"CommitTreeRevert: stashed pending tree '{activeTree.TreeName}' with snapshots preserved");
            ParsekLog.RecState("CommitTreeRevert:post", CaptureRecorderState());
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
            ParsekLog.RecState("Restore:start", CaptureRecorderState());
            if (!RecordingStore.HasPendingTree
                || RecordingStore.PendingTreeStateValue != PendingTreeState.Limbo)
            {
                ParsekLog.Verbose("Flight",
                    "RestoreActiveTreeFromPending: no pending-Limbo tree, skipping");
                yield break;
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
            // Secondary match = vessel name (fallback for old recordings without PID).
            // Tertiary (edge case 3): if the active recording was an EVA kerbal who got
            // boarded between quicksave and quickload, match against the parent recording's
            // vessel by walking ParentRecordingId up the EVA chain.
            Recording matchedRec = activeRec;
            string matchedRecId = activeRecId;
            float deadline = UnityEngine.Time.time + 3f;
            Vessel matched = null;
            while (UnityEngine.Time.time < deadline)
            {
                var v = FlightGlobals.ActiveVessel;
                if (v != null)
                {
                    // Primary: PID match (locale-proof, available immediately)
                    if (targetPid != 0 && v.persistentId == targetPid)
                    {
                        matched = v;
                        ParsekLog.Verbose("Flight",
                            $"RestoreActiveTreeFromPending: PID match pid={targetPid}");
                        break;
                    }
                    // Secondary: name match (fallback for old recordings without PID)
                    if (v.vesselName == targetName)
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
                        if (probe != null && (
                            (probe.VesselPersistentId != 0 && v.persistentId == probe.VesselPersistentId) ||
                            v.vesselName == probe.VesselName))
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

            if (matched == null)
            {
                ParsekLog.Warn("Flight",
                    $"RestoreActiveTreeFromPending: vessel '{targetName}' (and no EVA parent fallback) " +
                    "not active within 3s — leaving tree in Limbo (user can trigger merge dialog via scene exit)");
                yield break;
            }

            // After a parent-fallback match, the rest of the coroutine uses matchedRec/matchedRecId
            activeRec = matchedRec;
            activeRecId = matchedRecId;
            ParsekLog.RecState("Restore:matched", CaptureRecorderState());

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
            activeTree = RecordingStore.PopPendingTree();
            if (activeTree == null)
            {
                ParsekLog.Warn("Flight",
                    "RestoreActiveTreeFromPending: PopPendingTree returned null after we verified the tree exists");
                yield break;
            }

            // Construct a fresh FlightRecorder pointed at the restored tree.
            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;
            chainManager.ActiveTreeId = activeTree.Id;

            // Restore recorder state persisted in the PARSEK_ACTIVE_TREE node
            if (!string.IsNullOrEmpty(ParsekScenario.pendingActiveTreeResumeRewindSave))
            {
                recorder.SetRewindSaveFileNameForRestore(
                    ParsekScenario.pendingActiveTreeResumeRewindSave,
                    "quickload-resume from PARSEK_ACTIVE_TREE");
                ParsekScenario.pendingActiveTreeResumeRewindSave = null;
            }
            // BoundaryAnchor is NOT restored — only the UT could round-trip through the
            // save node, and reconstructing a TrajectoryPoint from a bare UT is not
            // possible (needs lat/lon/alt/rotation/velocity). Leaving BoundaryAnchor
            // unset produces one extra boundary point on the next chain continuation,
            // which is benign. See SaveActiveTreeIfAny for the write-side comment.

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
            ParsekLog.RecState("Restore:after-start", CaptureRecorderState());
            } // try
            finally
            {
                restoringActiveTree = false;
            }
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
            ParsekLog.RecState("RestoreSwitch:start", CaptureRecorderState());
            if (!RecordingStore.HasPendingTree
                || RecordingStore.PendingTreeStateValue != PendingTreeState.LimboVesselSwitch)
            {
                ParsekLog.Verbose("Flight",
                    "RestoreActiveTreeFromPendingForVesselSwitch: no pending-LimboVesselSwitch tree, skipping");
                yield break;
            }

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

            // Pop the tree (non-destructive — preserves sidecar files) and install
            // as the live active tree.
            activeTree = RecordingStore.PopPendingTree();
            if (activeTree == null)
            {
                ParsekLog.Warn("Flight",
                    "RestoreActiveTreeFromPendingForVesselSwitch: PopPendingTree returned null after " +
                    "we verified the tree exists");
                yield break;
            }

            chainManager.ActiveTreeId = activeTree.Id;

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

            ParsekLog.RecState("RestoreSwitch:after-install", CaptureRecorderState());
            } // try
            finally
            {
                restoringActiveTree = false;
            }
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
            ParsekLog.RecState("StashTreeLimbo:entry", CaptureRecorderState());

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
                if (rec.ChildBranchPointId != null && !isActiveRec) continue;
                if (rec.VesselPersistentId == 0) continue;

                Vessel v = FlightRecorder.FindVesselByPid(rec.VesselPersistentId);
                if (v != null && rec.VesselSnapshot == null)
                {
                    ConfigNode freshSnapshot = VesselSpawner.TryBackupSnapshot(v);
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
            RecordingStore.StashPendingTree(activeTree, PendingTreeState.Limbo);

            ParsekLog.Info("Flight",
                $"StashActiveTreeAsPendingLimbo: stashed tree '{activeTree.TreeName}' as Limbo " +
                $"({activeTree.Recordings.Count} recording(s)) — OnLoad will dispatch");
            ParsekLog.RecState("StashTreeLimbo:post", CaptureRecorderState());
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

        internal static bool TryFindCommittedTreeForSpawnedVessel(
            IReadOnlyList<RecordingTree> committedTrees,
            uint activeVesselPid,
            out RecordingTree tree,
            out string recordingId)
        {
            tree = null;
            recordingId = null;
            if (committedTrees == null || activeVesselPid == 0)
                return false;

            for (int t = committedTrees.Count - 1; t >= 0; t--)
            {
                RecordingTree candidate = committedTrees[t];
                if (candidate?.Recordings == null || candidate.Recordings.Count == 0)
                    continue;

                Recording bestMatch = null;
                foreach (Recording rec in candidate.Recordings.Values)
                {
                    if (rec == null
                        || !rec.VesselSpawned
                        || rec.SpawnedVesselPersistentId == 0
                        || rec.SpawnedVesselPersistentId != activeVesselPid
                        || string.IsNullOrEmpty(rec.RecordingId))
                    {
                        continue;
                    }

                    if (!IsCommittedSpawnedRecordingRestorable(candidate, rec))
                        continue;

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
                    return true;
                }
            }

            return false;
        }

        internal static bool TryTakeCommittedTreeForSpawnedVesselRestore(
            uint activeVesselPid,
            out RecordingTree tree,
            out string recordingId,
            out CommittedSpawnedVesselRestoreAction action)
        {
            tree = null;
            recordingId = null;
            action = CommittedSpawnedVesselRestoreAction.None;

            if (!TryFindCommittedTreeForSpawnedVessel(
                    RecordingStore.CommittedTrees,
                    activeVesselPid,
                    out RecordingTree committedTree,
                    out string targetRecordingId))
            {
                return false;
            }

            CommittedSpawnedVesselRestoreAction preparedAction =
                PrepareCommittedTreeRestoreForSpawnedVessel(
                    committedTree,
                    targetRecordingId,
                    activeVesselPid);
            if (preparedAction == CommittedSpawnedVesselRestoreAction.None)
            {
                ParsekLog.Warn("Flight",
                    $"TryTakeCommittedTreeForSpawnedVesselRestore: matched tree '{committedTree.TreeName}' " +
                    $"recording '{targetRecordingId}' for pid={activeVesselPid}, but could not prepare a restore action");
                return false;
            }

            // Detach the tree from committed storage before making it live again. The
            // active-flight path depends on "live tree != committed tree" for patch
            // deferral and for the later commit to run its full side effects.
            if (!RecordingStore.RemoveCommittedTreeById(
                    committedTree.Id,
                    logContext: "TryTakeCommittedTreeForSpawnedVesselRestore"))
            {
                ParsekLog.Warn("Flight",
                    $"TryTakeCommittedTreeForSpawnedVesselRestore: matched tree '{committedTree.TreeName}' " +
                    $"(id={committedTree.Id}) but could not detach it from committed storage");
                return false;
            }

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
            if (committedTree.Recordings != null
                && committedTree.Recordings.TryGetValue(targetRecordingId, out Recording resumedRec)
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

            tree = committedTree;
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
            ParsekLog.RecState("StashTreeForSwitch:entry", CaptureRecorderState());

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
            RecordingStore.StashPendingTree(activeTree, PendingTreeState.LimboVesselSwitch);

            ParsekLog.Info("Flight",
                $"StashActiveTreeForVesselSwitch: stashed tree '{activeTree.TreeName}' as " +
                $"LimboVesselSwitch ({activeTree.Recordings.Count} recording(s), " +
                $"{activeTree.BackgroundMap.Count} background entry(ies)) — OnLoad will dispatch");
            ParsekLog.RecState("StashTreeForSwitch:post", CaptureRecorderState());
        }

        /// <summary>
        /// Commits the active tree on scene exit (ghost-only, no spawning).
        /// Finalizes all recordings and stashes the tree as pending.
        /// </summary>
        void CommitTreeSceneExit(double commitUT)
        {
            if (activeTree == null) return;

            ParsekLog.Info("Flight", $"CommitTreeSceneExit: finalizing tree at UT={commitUT:F1}");
            ParsekLog.RecState("CommitTreeSceneExit:entry", CaptureRecorderState());

            // Finalize all recordings
            FinalizeTreeRecordings(activeTree, commitUT, isSceneExit: true);

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
                    rec.VesselSnapshot = null;
                }
            }

            // Stash as pending tree -- auto-committed ghost-only by ParsekScenario.OnLoad
            RecordingStore.StashPendingTree(activeTree);

            ParsekLog.Info("Flight", $"CommitTreeSceneExit: stashed pending tree '{activeTree.TreeName}'");
            ParsekLog.RecState("CommitTreeSceneExit:post", CaptureRecorderState());
        }

        /// <summary>
        /// Finalizes all recordings in the tree: stops active recorder, flushes data,
        /// captures terminal state/orbit/position for all recordings.
        /// </summary>
        void FinalizeTreeRecordings(RecordingTree tree, double commitUT, bool isSceneExit)
        {
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
                rec => ResolveFinalizationCacheForRecording(tree, rec));
        }

        internal static void FinalizeTreeRecordingsAfterFlush(
            RecordingTree tree,
            double commitUT,
            bool isSceneExit,
            Func<Recording, RecordingFinalizationCache> resolveFinalizationCache = null)
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

                if (FinalizeIndividualRecording(recording, commitUT, isSceneExit, finalizationCache)
                    && sceneExitLifetimeExtendedIds != null
                    && !string.IsNullOrEmpty(recording?.RecordingId))
                {
                    sceneExitLifetimeExtendedIds.Add(recording.RecordingId);
                }
            }

            // 3b. Ensure active recording has terminalState even if non-leaf.
            // In tree mode, the active recording may have debris branches (non-leaf)
            // so FinalizeIndividualRecording skips its terminalState. The optimizer
            // will propagate this to the chain tip via SplitAtSection.
            if (EnsureActiveRecordingTerminalState(
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

        private RecordingFinalizationCache ResolveFinalizationCacheForRecording(
            RecordingTree tree,
            Recording recording)
        {
            if (recording == null)
                return null;

            RecordingFinalizationCache cache = null;
            if (recorder != null
                && IsActiveRecorderCacheCandidate(tree, recording, recorder))
            {
                cache = recorder.GetFinalizationCacheForRecording(recording);
                if (CacheIdentityMatchesRecording(recording, cache))
                    return cache;
            }

            if (backgroundRecorder != null)
            {
                cache = backgroundRecorder.GetFinalizationCacheForRecording(recording);
                if (CacheIdentityMatchesRecording(recording, cache))
                    return cache;
            }

            return null;
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
            out RecordingFinalizationCacheApplyResult result)
        {
            var options = new RecordingFinalizationCacheApplyOptions
            {
                ConsumerPath = consumerPath,
                AllowStale = allowStale
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

        /// <summary>
        /// Sets terminal state on the active recording even if it is a non-leaf node.
        /// FinalizeIndividualRecording skips non-leaves, but the active recording needs
        /// terminal state for the optimizer's SplitAtSection propagation. On scene-exit
        /// paths, falls back to trajectory-based inference when the live vessel is no
        /// longer available.
        /// </summary>
        internal static bool EnsureActiveRecordingTerminalState(
            RecordingTree tree,
            bool isSceneExit = false,
            double commitUT = double.NaN,
            RecordingFinalizationCache finalizationCache = null)
        {
            if (string.IsNullOrEmpty(tree.ActiveRecordingId))
                return false;

            Recording activeRec;
            if (!tree.Recordings.TryGetValue(tree.ActiveRecordingId, out activeRec))
                return false;

            if (activeRec.TerminalStateValue.HasValue)
            {
                ParsekLog.Verbose("Flight",
                    $"FinalizeTreeRecordings: active recording '{activeRec.RecordingId}' " +
                    $"already has terminalState={activeRec.TerminalStateValue} — skipping");
                return false;
            }

            Vessel v = activeRec.VesselPersistentId != 0
                ? FlightRecorder.FindVesselByPid(activeRec.VesselPersistentId)
                : null;
            bool sceneExitLifetimeExtended = false;
            bool sceneExitSuppliedSnapshots = false;
            bool sceneExitSuppliedTerminalOrbit = false;
            if (isSceneExit)
            {
                ConfigNode vesselSnapshotBefore = activeRec.VesselSnapshot;
                TerminalOrbitMetadataSnapshot terminalOrbitBefore =
                    CaptureTerminalOrbitMetadataSnapshot(activeRec);
                sceneExitLifetimeExtended = IncompleteBallisticSceneExitFinalizer.TryApply(
                    activeRec,
                    v,
                    commitUT,
                    "EnsureActiveRecordingTerminalState");
                sceneExitSuppliedSnapshots =
                    !ReferenceEquals(vesselSnapshotBefore, activeRec.VesselSnapshot)
                    && activeRec.VesselSnapshot != null;
                sceneExitSuppliedTerminalOrbit =
                    sceneExitLifetimeExtended
                    && DidSceneExitUpdateTerminalOrbitMetadata(terminalOrbitBefore, activeRec);
                if (sceneExitLifetimeExtended)
                {
                    if (activeRec.TerminalStateValue.HasValue
                        && UsesTerminalOrbitMetadata(activeRec.TerminalStateValue.Value))
                    {
                        if (sceneExitSuppliedTerminalOrbit)
                        {
                            ParsekLog.Verbose("Flight",
                                $"EnsureActiveRecordingTerminalState: preserving scene-exit terminal orbit for " +
                                $"'{activeRec.RecordingId}' (body={activeRec.TerminalOrbitBody}, " +
                                $"terminal={activeRec.TerminalStateValue})");
                        }
                        else
                        {
                            PopulateTerminalOrbitFromLastSegment(activeRec);
                            if (string.IsNullOrEmpty(activeRec.TerminalOrbitBody))
                            {
                                ParsekLog.Warn("Flight",
                                    $"EnsureActiveRecordingTerminalState: scene-exit terminal orbit remains empty for " +
                                    $"'{activeRec.RecordingId}' (terminal={activeRec.TerminalStateValue}, " +
                                    $"orbitSegments={activeRec.OrbitSegments?.Count ?? 0})");
                            }
                        }
                    }
                    return sceneExitSuppliedSnapshots;
                }
            }

            if (v != null)
            {
                activeRec.TerminalStateValue =
                    RecordingTree.DetermineTerminalState((int)v.situation, v);
                RecordingEndpointResolver.RefreshEndpointDecision(activeRec, "FinalizeTreeRecordings.LiveNonLeaf");
                ParsekLog.Info("Flight",
                    $"FinalizeTreeRecordings: set terminalState=" +
                    $"{activeRec.TerminalStateValue} on active recording " +
                    $"'{activeRec.RecordingId}' (non-leaf, vessel situation={v.situation})");
                return false;
            }

            if (!activeRec.TerminalStateValue.HasValue
                && HasFallbackCandidateCache(finalizationCache, v == null)
                && TryApplyFinalizationCacheFallback(
                    activeRec,
                    finalizationCache,
                    "EnsureActiveRecordingTerminalState",
                    allowStale: v == null,
                    out _))
            {
                return false;
            }

            if (isSceneExit)
            {
                // PR #572 follow-up: same gate as the leaf path — when the
                // active recording was just repaired from the committed tree
                // this frame, do not overwrite its (intentionally unset)
                // terminal state with a Landed/Splashed inference based on
                // the last trajectory point. The gate's only clause is
                // RestoredFromCommittedTreeThisFrame; an additional
                // orbital-evidence clause was considered (Option D in the
                // design plan) and rejected because the legitimate
                // orbit-then-land case shares the same shape (high
                // MaxDistanceFromLaunch + stable orbit segment + low-altitude
                // last point) — see ShouldSkipSceneExitSurfaceInferenceForRestoredRecording's
                // doc comment.
                if (ShouldSkipSceneExitSurfaceInferenceForRestoredRecording(
                        activeRec, out string skipReason))
                {
                    activeRec.RestoredFromCommittedTreeThisFrame = false;
                    PopulateTerminalOrbitFromLastSegment(activeRec);
                    ParsekLog.Info("Flight",
                        $"FinalizeTreeRecordings: skipping Landed/Splashed inference " +
                        $"for active recording '{activeRec.RecordingId}' " +
                        $"(vessel pid={activeRec.VesselPersistentId}) — {skipReason} " +
                        $"(lastPtAlt={(activeRec.Points.Count > 0 ? activeRec.Points[activeRec.Points.Count - 1].altitude : double.NaN):F1}m " +
                        $"maxDist={activeRec.MaxDistanceFromLaunch:F0}m " +
                        $"orbitSegs={activeRec.OrbitSegments?.Count ?? 0})");
                    RecordingEndpointResolver.RefreshEndpointDecision(activeRec, "FinalizeTreeRecordings.SceneExitNonLeafSkipInfer");
                    return false;
                }

                var inferredState = InferTerminalStateFromTrajectory(activeRec);
                activeRec.TerminalStateValue = inferredState;
                ParsekLog.Info("Flight",
                    $"FinalizeTreeRecordings: active recording '{activeRec.RecordingId}' " +
                    $"vessel pid={activeRec.VesselPersistentId} not found on scene exit — " +
                    $"inferred {inferredState} from trajectory");
                PopulateTerminalOrbitFromLastSegment(activeRec);
                if (inferredState == TerminalState.Landed || inferredState == TerminalState.Splashed)
                {
                    PopulateTerminalPositionFromLastPoint(activeRec, inferredState);
                    TryCaptureTerrainHeightFromLastTrajectoryPoint(activeRec);
                }
                RecordingEndpointResolver.RefreshEndpointDecision(activeRec, "FinalizeTreeRecordings.SceneExitNonLeaf");
                return false;
            }

            ParsekLog.Verbose("Flight",
                $"FinalizeTreeRecordings: active recording '{activeRec.RecordingId}' " +
                $"vessel pid={activeRec.VesselPersistentId} not found before terminal-state " +
                $"assignment (isSceneExit={isSceneExit})");
            return false;
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
                || !IsStableSpawnTerminal(activeRec.TerminalStateValue.Value))
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

        internal static bool FinalizeIndividualRecording(
            Recording rec,
            double commitUT,
            bool isSceneExit,
            RecordingFinalizationCache finalizationCache = null)
        {
            // Set ExplicitStartUT if not already set
            if (double.IsNaN(rec.ExplicitStartUT))
            {
                if (rec.Points.Count > 0)
                    rec.ExplicitStartUT = rec.Points[0].ut;
                else if (rec.OrbitSegments.Count > 0)
                    rec.ExplicitStartUT = rec.OrbitSegments[0].startUT;
            }

            // Set ExplicitEndUT on leaf recordings without one
            if (double.IsNaN(rec.ExplicitEndUT))
            {
                if (rec.Points.Count > 0)
                    rec.ExplicitEndUT = rec.Points[rec.Points.Count - 1].ut;
                else
                    rec.ExplicitEndUT = commitUT;
            }

            // Look up the live vessel once — shared by the terminal-determination block below
            // and the #289 re-snapshot block that follows. Avoids a double FindVesselByPid
            // for recordings that enter !HasValue, get terminal set, then hit the re-snapshot path.
            bool isLeaf = rec.ChildBranchPointId == null;
            Vessel finalizeVessel = (isLeaf && rec.VesselPersistentId != 0)
                ? FlightRecorder.FindVesselByPid(rec.VesselPersistentId)
                : null;
            bool sceneExitLifetimeExtended = false;
            bool sceneExitSuppliedSnapshots = false;
            bool sceneExitSuppliedTerminalOrbit = false;
            bool cacheFinalizationApplied = false;
            bool cacheSuppliedTerminalOrbit = false;

            if (isLeaf && rec.VesselPersistentId != 0 && finalizeVessel == null)
                ParsekLog.Verbose("Flight",
                    $"FinalizeIndividualRecording: vessel pid={rec.VesselPersistentId} not found " +
                    $"for '{rec.RecordingId}' (isSceneExit={isSceneExit}) — re-snapshot will be skipped");

            if (isLeaf && isSceneExit && !rec.TerminalStateValue.HasValue)
            {
                ConfigNode vesselSnapshotBefore = rec.VesselSnapshot;
                TerminalOrbitMetadataSnapshot terminalOrbitBefore =
                    CaptureTerminalOrbitMetadataSnapshot(rec);
                sceneExitLifetimeExtended = IncompleteBallisticSceneExitFinalizer.TryApply(
                    rec,
                    finalizeVessel,
                    commitUT,
                    "FinalizeIndividualRecording");
                sceneExitSuppliedSnapshots =
                    !ReferenceEquals(vesselSnapshotBefore, rec.VesselSnapshot)
                    && rec.VesselSnapshot != null;
                sceneExitSuppliedTerminalOrbit =
                    sceneExitLifetimeExtended
                    && DidSceneExitUpdateTerminalOrbitMetadata(terminalOrbitBefore, rec);
            }

            // Determine terminal state for recordings that don't have one yet
            if (isLeaf && !rec.TerminalStateValue.HasValue)
            {
                if (finalizeVessel != null)
                {
                    rec.TerminalStateValue = RecordingTree.DetermineTerminalState((int)finalizeVessel.situation, finalizeVessel);
                    CaptureTerminalOrbit(rec, finalizeVessel);
                    CaptureTerminalPosition(rec, finalizeVessel);

                    // Re-snapshot live vessels for Commit Flight path (fresh state)
                    if (!isSceneExit)
                    {
                        ConfigNode freshSnapshot = VesselSpawner.TryBackupSnapshot(finalizeVessel);
                        if (freshSnapshot != null)
                        {
                            rec.VesselSnapshot = freshSnapshot;
                            if (rec.GhostVisualSnapshot == null)
                                rec.GhostVisualSnapshot = freshSnapshot.CreateCopy();
                        }
                    }
                }
                else if (HasFallbackCandidateCache(
                    finalizationCache,
                    vesselMissing: true)
                    && TryApplyFinalizationCacheFallback(
                        rec,
                        finalizationCache,
                        "FinalizeIndividualRecording",
                        allowStale: true,
                        out _))
                {
                    cacheFinalizationApplied = true;
                    cacheSuppliedTerminalOrbit =
                        rec.TerminalStateValue.HasValue
                        && UsesTerminalOrbitMetadata(rec.TerminalStateValue.Value)
                        && !string.IsNullOrEmpty(rec.TerminalOrbitBody);
                }
                else if (isSceneExit)
                {
                    // Scene exit: vessel unloaded (alive) but not findable. Recordings
                    // for truly destroyed vessels already have TerminalStateValue set by
                    // DeferredDestructionCheck / ApplyDestroyedFallback before finalization.
                    // Reaching here without a terminal state means the vessel was alive
                    // when unloaded. Infer terminal state from the last trajectory point.
                    //
                    // PR #572 follow-up: skip the surface inference when the recording was
                    // just repaired from the committed tree this frame (the trajectory
                    // came from a copy that already lacked a terminal state, so the
                    // "vessel was alive when unloaded" heuristic does not apply — typically
                    // means the live pid was a deliberate Re-Fly strip casualty). The gate's
                    // only clause is RestoredFromCommittedTreeThisFrame; an additional
                    // orbital-evidence clause was considered (Option D in the design plan)
                    // and rejected because the legitimate orbit-then-land case shares the
                    // same shape (high MaxDistanceFromLaunch + stable orbit segment + low-
                    // altitude last point) and adding such a clause would regress
                    // EnsureActiveRecordingTerminalState_NoLiveVesselOnSceneExit_InfersFromTrajectory
                    // and SceneExitInferredActiveNonLeaf_DefaultsToPersistInMergeDialog.
                    if (ShouldSkipSceneExitSurfaceInferenceForRestoredRecording(
                            rec, out string skipReason))
                    {
                        rec.RestoredFromCommittedTreeThisFrame = false;
                        // Recover terminal orbit metadata from the last orbit segment if
                        // available — preserves the orbital fingerprint for ghost-map
                        // playback even though the terminal state remains unset.
                        PopulateTerminalOrbitFromLastSegment(rec);
                        ParsekLog.Info("Flight",
                            $"FinalizeTreeRecordings: skipping Landed/Splashed inference " +
                            $"for '{rec.RecordingId}' (vessel pid={rec.VesselPersistentId}) — " +
                            $"{skipReason} " +
                            $"(lastPtAlt={(rec.Points.Count > 0 ? rec.Points[rec.Points.Count - 1].altitude : double.NaN):F1}m " +
                            $"maxDist={rec.MaxDistanceFromLaunch:F0}m " +
                            $"orbitSegs={rec.OrbitSegments?.Count ?? 0})");
                    }
                    else
                    {
                        var inferredState = InferTerminalStateFromTrajectory(rec);
                        rec.TerminalStateValue = inferredState;
                        ParsekLog.Info("Flight", $"FinalizeTreeRecordings: vessel pid={rec.VesselPersistentId} " +
                            $"not found on scene exit for recording '{rec.RecordingId}' — " +
                            $"inferred {inferredState} from trajectory (vessel was alive when unloaded)");
                        PopulateTerminalOrbitFromLastSegment(rec);

                        // Bug #290d: capture terrain height from last trajectory point for
                        // landed/splashed recordings whose vessel was unloaded at scene exit.
                        // Without this, TerrainHeightAtEnd stays NaN and the spawn safety net
                        // uses PQS terrain height, which is below KSP static structures (runway,
                        // launchpad), causing the spawned vessel to clip through and explode.
                        if (inferredState == TerminalState.Landed || inferredState == TerminalState.Splashed)
                        {
                            PopulateTerminalPositionFromLastPoint(rec, inferredState);
                            TryCaptureTerrainHeightFromLastTrajectoryPoint(rec);
                        }
                    }
                }
                else
                {
                    // Not a scene exit — vessel genuinely missing. Mark as destroyed.
                    rec.TerminalStateValue = TerminalState.Destroyed;
                    rec.VesselSnapshot = null;
                    ParsekLog.Warn("Flight", $"FinalizeTreeRecordings: vessel pid={rec.VesselPersistentId} " +
                        $"not found for recording '{rec.RecordingId}' — marking Destroyed");

                    // Recover terminal orbit from last orbit segment if available (#219).
                    // Orbital debris often has orbit segments from BackgroundRecorder sampling
                    // but the vessel is destroyed by finalization time.
                    PopulateTerminalOrbitFromLastSegment(rec);
                }
            }

            // #289: Re-snapshot the vessel whenever the recording has reached a stable terminal
            // state (Landed/Splashed/Orbiting) AND the live vessel is still findable. Without
            // this, the snapshot's `sit` field stays stale from recording-start (FLYING/SUB_ORBITAL),
            // and after the OnSave→scene-change→OnLoad round-trip the spawn-at-end safety check
            // blocks vessel materialization at "snapshot situation unsafe (FLYING/SUB_ORBITAL)".
            //
            // Runs OUTSIDE the !TerminalStateValue.HasValue gate above because the user's case
            // is precisely "terminal state was already set (e.g. by ChainSegmentManager during
            // active recording) so the gate above is skipped — but the snapshot is still stale".
            //
            // Reuses finalizeVessel from the lookup above — no double FindVesselByPid.
            if (!(sceneExitLifetimeExtended && sceneExitSuppliedSnapshots)
                && !cacheFinalizationApplied
                && isLeaf
                && rec.TerminalStateValue.HasValue
                && finalizeVessel != null)
            {
                var ts = rec.TerminalStateValue.Value;
                if (IsStableSpawnTerminal(ts))
                    TryRefreshStableTerminalSnapshot(rec, finalizeVessel, isSceneExit, "FinalizeIndividualRecording");
            }

            // Refresh terminal orbit for orbital leaf recordings even if a body was
            // captured earlier. A mid-transition capture can stamp the wrong SOI body,
            // so TerminalOrbit* behaves as a healable cache. Explicit point/surface
            // endpoint data still wins when it already anchors the recording; otherwise
            // we only preserve cached orbit data when the full tuple already matches the
            // endpoint-aligned last orbit segment. (#475/#484)
            if (isLeaf && rec.TerminalStateValue.HasValue
                && UsesTerminalOrbitMetadata(rec.TerminalStateValue.Value))
            {
                bool preserveSceneExitTerminalOrbit =
                    sceneExitLifetimeExtended && sceneExitSuppliedTerminalOrbit;
                bool preserveFinalizationCacheTerminalOrbit =
                    cacheFinalizationApplied && cacheSuppliedTerminalOrbit;
                bool preserveFinalizerSuppliedTerminalOrbit =
                    preserveSceneExitTerminalOrbit
                    || preserveFinalizationCacheTerminalOrbit;
                string bodyBeforeRefresh = rec.TerminalOrbitBody;
                if (!sceneExitLifetimeExtended && !cacheFinalizationApplied && finalizeVessel != null)
                    CaptureTerminalOrbit(rec, finalizeVessel);
                else if (preserveFinalizerSuppliedTerminalOrbit)
                    ParsekLog.Verbose("Flight",
                        $"FinalizeIndividualRecording: preserving " +
                        $"{(preserveFinalizationCacheTerminalOrbit ? "cache" : "scene-exit")} " +
                        $"terminal orbit for '{rec.RecordingId}' (body={rec.TerminalOrbitBody}, " +
                        $"terminal={rec.TerminalStateValue})");

                if (!string.IsNullOrEmpty(rec.TerminalOrbitBody)
                    && !string.Equals(rec.TerminalOrbitBody, bodyBeforeRefresh, StringComparison.Ordinal))
                {
                    ParsekLog.Info("Flight",
                        $"FinalizeIndividualRecording: refreshed TerminalOrbitBody {bodyBeforeRefresh ?? "(empty)"} " +
                        $"-> {rec.TerminalOrbitBody} for '{rec.RecordingId}' from live vessel " +
                        $"(terminal={rec.TerminalStateValue})");
                }

                // Evaluate the same-UT point-anchor guard after any live-vessel
                // refresh. If CaptureTerminalOrbit just rewrote TerminalOrbitBody,
                // that refreshed body should stay authoritative instead of letting
                // stale point metadata from a prior SOI suppress the finalize path.
                bool handledSameUtPointAnchor = !preserveFinalizerSuppliedTerminalOrbit
                    && TryHandleFinalizeSameUtPointAnchoredTerminalOrbit(rec);
                if (!handledSameUtPointAnchor
                    && !preserveFinalizerSuppliedTerminalOrbit
                    && ShouldPopulateTerminalOrbitFromLastSegment(rec))
                {
                    string bodyBeforeFallback = rec.TerminalOrbitBody;
                    PopulateTerminalOrbitFromLastSegment(rec);
                    if (!string.Equals(rec.TerminalOrbitBody, bodyBeforeFallback, StringComparison.Ordinal))
                    {
                        ParsekLog.Info("Flight",
                            $"FinalizeIndividualRecording: backfilled TerminalOrbitBody={rec.TerminalOrbitBody} " +
                            $"for '{rec.RecordingId}' via orbit-segment fallback " +
                            $"(terminal={rec.TerminalStateValue}, vesselFound={finalizeVessel != null}, " +
                            $"previousBody={bodyBeforeFallback ?? "(empty)"})");
                    }
                }

                if (string.IsNullOrEmpty(rec.TerminalOrbitBody))
                {
                    ParsekLog.Warn("Flight",
                        $"FinalizeIndividualRecording: terminal orbit refresh declined for '{rec.RecordingId}' " +
                        $"(terminal={rec.TerminalStateValue}, vesselFound={finalizeVessel != null}, " +
                        $"orbitSegments={rec.OrbitSegments?.Count ?? 0}) — TerminalOrbitBody remains empty");
                }
            }

            RecordingEndpointResolver.RefreshEndpointDecision(rec, "FinalizeIndividualRecording");

            // Bug #290d: backfill MaxDistanceFromLaunch if not yet computed.
            // Tree recordings reach finalization via ForceStop which skips BuildCaptureRecording
            // (where MaxDistanceFromLaunch is normally computed). Without this, all recordings
            // have maxDist=0.0 and IsTreeIdleOnPad falsely discards the entire tree.
            if (rec.MaxDistanceFromLaunch <= 0.0 && rec.Points.Count >= 2)
            {
                VesselSpawner.BackfillMaxDistance(rec);
            }

            // Warn if leaf has no playback data
            if (isLeaf && rec.Points.Count == 0 && rec.OrbitSegments.Count == 0 && !rec.SurfacePos.HasValue)
            {
                if (rec.SidecarLoadFailed)
                {
                    ParsekLog.Warn("Flight",
                        $"FinalizeTreeRecordings: leaf '{rec.RecordingId}' has no playback data " +
                        $"because sidecar hydration failed ({rec.SidecarLoadFailureReason ?? "unknown"})");
                }
                else
                {
                    ParsekLog.Warn("Flight", $"FinalizeTreeRecordings: leaf '{rec.RecordingId}' has no playback data");
                }
            }

            ParsekLog.Verbose("Flight",
                $"FinalizeTreeRecordings: rec='{rec.RecordingId}' vessel='{rec.VesselName}' " +
                $"points={rec.Points.Count} orbitSegs={rec.OrbitSegments.Count} " +
                $"terminal={rec.TerminalStateValue?.ToString() ?? "none"} " +
                $"maxDist={rec.MaxDistanceFromLaunch:F0}m " +
                $"snapshot={rec.VesselSnapshot != null} leaf={isLeaf}");
            return sceneExitLifetimeExtended && sceneExitSuppliedSnapshots;
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
            // FlightGlobals is unavailable in unit tests — guard with try/catch.
            if (rec.OrbitSegments != null && rec.OrbitSegments.Count > 0)
            {
                var lastOrbit = rec.OrbitSegments[rec.OrbitSegments.Count - 1];
                if (lastOrbit.eccentricity < 1.0 && !string.IsNullOrEmpty(lastOrbit.bodyName))
                {
                    double bodyRadius = 0;
                    try
                    {
                        var body = FlightGlobals.GetBodyByName(lastOrbit.bodyName);
                        if (body != null) bodyRadius = body.Radius;
                    }
                    catch { /* FlightGlobals unavailable outside KSP */ }
                    if (bodyRadius > 0 && lastOrbit.semiMajorAxis * (1 - lastOrbit.eccentricity) > bodyRadius)
                        return TerminalState.Orbiting;
                }
            }

            // Default: vessel was in flight (atmospheric descent, suborbital, etc.)
            return TerminalState.SubOrbital;
        }

        /// <summary>
        /// PR #572 second-order data-loss companion. Decides whether
        /// <see cref="FinalizeIndividualRecording"/> /
        /// <see cref="EnsureActiveRecordingTerminalState"/> should skip the
        /// scene-exit "vessel was alive when unloaded → infer Landed/Splashed
        /// from last trajectory point" branch.
        ///
        /// <para>
        /// The gate fires when the recording was just repaired from the
        /// committed tree this frame
        /// (<see cref="Recording.RestoredFromCommittedTreeThisFrame"/>).
        /// The trajectory is a copy of a recording that was committed
        /// mid-flight without a terminal state — typically because the
        /// missing live pid is a deliberate Re-Fly strip casualty, not a
        /// natural unload — so the surface inference's "vessel was alive
        /// when unloaded" assumption does not apply.
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
        /// The user's 2026-04-25 case is solved by the restore flag alone
        /// because the committed copy carried no terminal state. Future
        /// readers: do not add an orbital-evidence clause here without
        /// first revisiting those two tests.
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

        static void PopulateTerminalPositionFromLastPoint(Recording rec, TerminalState inferredState)
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

        private static bool IsStableSpawnTerminal(TerminalState state)
        {
            return state == TerminalState.Landed
                || state == TerminalState.Splashed
                || state == TerminalState.Orbiting;
        }

        private static bool UsesTerminalOrbitMetadata(TerminalState state)
        {
            return state == TerminalState.Orbiting
                || state == TerminalState.SubOrbital
                || state == TerminalState.Docked;
        }

        private struct TerminalOrbitMetadataSnapshot
        {
            public string body;
            public double inclination;
            public double eccentricity;
            public double semiMajorAxis;
            public double lan;
            public double argumentOfPeriapsis;
            public double meanAnomalyAtEpoch;
            public double epoch;

            public bool HasMetadata => !string.IsNullOrEmpty(body);
        }

        private static TerminalOrbitMetadataSnapshot CaptureTerminalOrbitMetadataSnapshot(Recording rec)
        {
            if (rec == null)
                return default(TerminalOrbitMetadataSnapshot);

            return new TerminalOrbitMetadataSnapshot
            {
                body = rec.TerminalOrbitBody,
                inclination = rec.TerminalOrbitInclination,
                eccentricity = rec.TerminalOrbitEccentricity,
                semiMajorAxis = rec.TerminalOrbitSemiMajorAxis,
                lan = rec.TerminalOrbitLAN,
                argumentOfPeriapsis = rec.TerminalOrbitArgumentOfPeriapsis,
                meanAnomalyAtEpoch = rec.TerminalOrbitMeanAnomalyAtEpoch,
                epoch = rec.TerminalOrbitEpoch
            };
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
            ConfigNode freshSnapshot = VesselSpawner.TryBackupSnapshot(vessel);
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
            ParsekLog.Info("Flight",
                $"{logPrefix} '{rec.RecordingId}' with stable terminal state {ts} " +
                $"(vessel.situation={vessel.situation}, isSceneExit={isSceneExit}) [#289]");
            return true;
        }

        internal static ConfigNode NormalizeStableTerminalSnapshotForPersistence(
            ConfigNode freshSnapshot,
            TerminalState terminalState,
            CelestialBody body = null)
        {
            return VesselSpawner.NormalizeStableSnapshotForPersistence(
                freshSnapshot,
                terminalState,
                body,
                "stable-terminal snapshot persistence");
        }

        /// <summary>
        /// Removes zero-point leaf recordings from the tree (#173). These empty leaves have no
        /// trajectory data, no orbit segments, and no surface position. They can be same-frame
        /// debris fragments or transient placeholders left behind by split/finalize edge cases.
        /// </summary>
        internal static void PruneZeroPointLeaves(RecordingTree tree)
        {
            var toPrune = CollectZeroPointLeafIds(tree);
            if (toPrune == null || toPrune.Count == 0)
                return;

            PruneLeafRecordings(
                tree,
                toPrune,
                "PruneZeroPointLeaves",
                "zero-point leaf recording(s)");
        }

        internal static void PruneSinglePointDebrisLeaves(RecordingTree tree)
        {
            var toPrune = CollectSinglePointDebrisLeafIds(tree);
            if (toPrune == null || toPrune.Count == 0)
                return;

            // Same-frame breakup debris can die inside the coalescing window and only retain
            // the split seed point. These stubs are non-playable, fail the data-health
            // contract, and never contribute a meaningful ghost or spawn path. #447 widened
            // this beyond Destroyed, but only for debris that has already fully ended
            // (Landed / Recovered / Splashed / Destroyed). Scene-exit commits can still
            // produce one-point SubOrbital / Orbiting debris leaves; keep those for
            // diagnosis instead of silently deleting non-terminal recordings.

            // Terminal-state breakdown for the summary log (#447 follow-up): aggregate before
            // PruneLeafRecordings removes the recordings from the tree.
            var breakdown = new Dictionary<TerminalState, int>();
            for (int i = 0; i < toPrune.Count; i++)
            {
                if (!tree.Recordings.TryGetValue(toPrune[i], out var rec) || rec == null) continue;
                var state = rec.TerminalStateValue.Value;
                breakdown[state] = (breakdown.TryGetValue(state, out var n) ? n : 0) + 1;
            }
            string summarySuffix = FormatTerminalStateBreakdown(breakdown);

            PruneLeafRecordings(
                tree,
                toPrune,
                "PruneSinglePointDebrisLeaves",
                "single-point debris stub(s)",
                summarySuffix);
        }

        // #447 follow-up: render a "(Landed=2, Destroyed=1)" suffix for the prune summary
        // log. Returns empty string when nothing to break out.
        internal static string FormatTerminalStateBreakdown(
            Dictionary<TerminalState, int> breakdown)
        {
            if (breakdown == null || breakdown.Count == 0)
                return string.Empty;
            var parts = new List<string>();
            // Iterate enum values in their declared order so the suffix is deterministic
            // regardless of dictionary insertion order.
            foreach (TerminalState state in Enum.GetValues(typeof(TerminalState)))
            {
                if (breakdown.TryGetValue(state, out var count) && count > 0)
                    parts.Add($"{state}={count}");
            }
            return parts.Count == 0 ? string.Empty : " (" + string.Join(", ", parts) + ")";
        }

        private static void PruneLeafRecordings(
            RecordingTree tree,
            List<string> toPrune,
            string logTag,
            string description,
            string summarySuffix = null)
        {
            for (int i = 0; i < toPrune.Count; i++)
            {
                string id = toPrune[i];
                tree.Recordings.Remove(id);

                // Remove from parent branch point's children list
                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    tree.BranchPoints[b].ChildRecordingIds.Remove(id);
                }
            }

            // Clean up branch points with no remaining children
            int prunedBPs = 0;
            for (int b = tree.BranchPoints.Count - 1; b >= 0; b--)
            {
                if (tree.BranchPoints[b].ChildRecordingIds.Count == 0)
                {
                    // Clear the parent recording's ChildBranchPointId reference
                    string bpId = tree.BranchPoints[b].Id;
                    foreach (var kvp in tree.Recordings)
                    {
                        if (kvp.Value.ChildBranchPointId == bpId)
                            kvp.Value.ChildBranchPointId = null;
                    }
                    tree.BranchPoints.RemoveAt(b);
                    prunedBPs++;
                }
            }

            ParsekLog.Info("Flight",
                $"{logTag}: removed {toPrune.Count} {description}" +
                (string.IsNullOrEmpty(summarySuffix) ? "" : summarySuffix) +
                (prunedBPs > 0 ? $" and {prunedBPs} empty branch point(s)" : "") +
                $" from tree '{tree.TreeName}'");
        }

        /// <summary>
        /// Pure static helper: collects IDs of empty leaf recordings with no playback data.
        /// A leaf has no ChildBranchPointId and zero points + zero orbit segments + no surface pos.
        /// </summary>
        internal static List<string> CollectZeroPointLeafIds(RecordingTree tree)
        {
            List<string> result = null;
            foreach (var kvp in tree.Recordings)
            {
                var rec = kvp.Value;
                if (IsZeroPointLeaf(rec))
                {
                    if (result == null) result = new List<string>();
                    result.Add(kvp.Key);
                }
            }
            return result;
        }

        internal static List<string> CollectSinglePointDebrisLeafIds(RecordingTree tree)
        {
            List<string> result = null;
            foreach (var kvp in tree.Recordings)
            {
                if (kvp.Key == tree.RootRecordingId || kvp.Key == tree.ActiveRecordingId)
                    continue;

                var rec = kvp.Value;
                if (IsSinglePointDebrisLeaf(rec))
                {
                    if (result == null) result = new List<string>();
                    result.Add(kvp.Key);
                }
            }
            return result;
        }

        /// <summary>
        /// Returns true if a recording is a leaf with no playback data (zero points,
        /// no orbit segments, no surface position).
        /// </summary>
        internal static bool IsZeroPointLeaf(Recording rec)
        {
            if (rec.ChildBranchPointId != null) return false; // not a leaf
            if (rec.SidecarLoadFailed) return false; // keep explicit hydration failures for later recovery/inspection
            return rec.Points.Count == 0
                && rec.OrbitSegments.Count == 0
                && !rec.SurfacePos.HasValue;
        }

        internal static bool IsSinglePointDebrisLeaf(Recording rec)
        {
            return IsSinglePointDebrisLeafForState(rec, IsTerminalSinglePointDebrisStubState);
        }

        internal static bool IsStopMetricsExemptSinglePointDebrisLeaf(Recording rec)
        {
            return IsSinglePointDebrisLeafForState(rec, IsPreservedSinglePointInFlightDebrisState);
        }

        private static bool IsSinglePointDebrisLeafForState(
            Recording rec, Func<TerminalState, bool> statePredicate)
        {
            if (rec == null || rec.ChildBranchPointId != null) return false;
            if (rec.SidecarLoadFailed) return false;
            if (!rec.IsDebris) return false;
            if (rec.Points.Count != 1) return false;
            if (!rec.TerminalStateValue.HasValue
                || !statePredicate(rec.TerminalStateValue.Value))
                return false;
            if (rec.OrbitSegments.Count > 0 || rec.SurfacePos.HasValue) return false;
            return HasOnlyMirroredSinglePointTrackSection(rec);
        }

        private static bool IsTerminalSinglePointDebrisStubState(TerminalState state)
        {
            switch (state)
            {
                case TerminalState.Landed:
                case TerminalState.Splashed:
                case TerminalState.Destroyed:
                case TerminalState.Recovered:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsPreservedSinglePointInFlightDebrisState(TerminalState state)
        {
            switch (state)
            {
                case TerminalState.SubOrbital:
                case TerminalState.Orbiting:
                    return true;
                default:
                    return false;
            }
        }

        private static bool HasOnlyMirroredSinglePointTrackSection(Recording rec)
        {
            // #447: accept section-backed single-point debris only when the section is just
            // the mirrored seed point with no checkpoints. Anything richer than that is real
            // data and must stay visible to either pruning or REC-002.
            if (rec.TrackSections == null || rec.TrackSections.Count == 0) return true;
            if (rec.TrackSections.Count > 1) return false;
            var section = rec.TrackSections[0];
            int frames = section.frames?.Count ?? 0;
            int checkpoints = section.checkpoints?.Count ?? 0;
            return checkpoints == 0
                && frames == 1
                && TrajectoryPointEquals(section.frames[0], rec.Points[0]);
        }

        private static bool TrajectoryPointEquals(TrajectoryPoint a, TrajectoryPoint b)
        {
            return a.ut == b.ut
                && a.latitude == b.latitude
                && a.longitude == b.longitude
                && a.altitude == b.altitude
                && a.rotation.x == b.rotation.x
                && a.rotation.y == b.rotation.y
                && a.rotation.z == b.rotation.z
                && a.rotation.w == b.rotation.w
                && a.velocity.x == b.velocity.x
                && a.velocity.y == b.velocity.y
                && a.velocity.z == b.velocity.z
                && a.bodyName == b.bodyName
                && a.funds == b.funds
                && a.science == b.science
                && a.reputation == b.reputation;
        }

        // Phase F: ComputeTreeDeltaFunds/Science/Reputation removed.
        // Tree-level snapshot reconciliation replaced by per-action ledger walk
        // (see GameActions/RecalculationEngine + LedgerOrchestrator).

        /// <summary>
        /// Spawns all leaf vessels from a committed tree (except the active vessel).
        /// Handles crew reservation (already done by caller), proximity offsets.
        /// </summary>
        void SpawnTreeLeaves(RecordingTree tree, string activeRecordingId)
        {
            var spawnableLeaves = tree.GetSpawnableLeaves();
            if (spawnableLeaves.Count == 0)
            {
                ParsekLog.Info("Flight", "SpawnTreeLeaves: no spawnable leaves");
                return;
            }

            ParsekLog.Info("Flight", $"SpawnTreeLeaves: {spawnableLeaves.Count} spawnable leaf/leaves");

            foreach (var leaf in spawnableLeaves)
            {
                // Skip the active vessel — it's already in-game
                if (leaf.RecordingId == activeRecordingId)
                    continue;

                if (leaf.VesselSnapshot == null)
                {
                    ParsekLog.Warn("Flight", $"SpawnTreeLeaves: leaf '{leaf.RecordingId}' has null snapshot — skipping");
                    continue;
                }

                if (VesselSpawner.TryAdoptExistingSourceVesselForSpawn(
                    leaf,
                    "Flight",
                    $"SpawnTreeLeaves leaf '{leaf.VesselName ?? leaf.RecordingId}'"))
                {
                    leaf.LastAppliedResourceIndex = leaf.Points.Count - 1;
                    continue;
                }

                // Before spawning, recover any timeline-spawned vessel with the same name
                // to prevent overlap collisions (e.g. an older committed recording already
                // spawned the same vessel on the pad).
                RecoverTimelineSpawnedVessel(leaf.VesselName);

                try
                {
                    // Route scene-load leaf spawns through the shared end-of-recording helper
                    // so EVA/orbital paths rebuild endpoint-aligned ORBIT data the same way
                    // the in-flight spawn path does.
                    if (leaf.SpawnAttempts > 0)
                    {
                        ParsekLog.Verbose("Flight",
                            $"SpawnTreeLeaves: leaf '{leaf.VesselName}' entering shared spawn path " +
                            $"with prior attempts={leaf.SpawnAttempts} (shared helper caps retries at 3)");
                    }

                    VesselSpawner.SpawnOrRecoverIfTooClose(leaf, -1);
                    if (leaf.SpawnedVesselPersistentId != 0)
                    {
                        leaf.LastAppliedResourceIndex = leaf.Points.Count - 1;
                        ParsekLog.Info("Flight",
                            $"SpawnTreeLeaves: spawned leaf '{leaf.VesselName}' pid={leaf.SpawnedVesselPersistentId}");
                    }
                    else if (leaf.SpawnAbandoned)
                    {
                        ParsekLog.Warn("Flight",
                            $"SpawnTreeLeaves: abandoned leaf '{leaf.VesselName}' without a spawned vessel");
                    }
                    else
                    {
                        ParsekLog.Warn("Flight", $"SpawnTreeLeaves: failed to spawn leaf '{leaf.VesselName}'");
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("Flight", $"SpawnTreeLeaves: exception spawning '{leaf.VesselName}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Recovers orphaned spawned vessels after a revert/rewind. Matches by
        /// persistentId (reliable in Flight) and vessel name (fallback).
        /// Skips the active vessel.
        /// </summary>
        void CleanupOrphanedSpawnedVessels(HashSet<uint> pids, HashSet<string> names)
        {
            ParsekLog.Info("Flight",
                $"CleanupOrphanedSpawnedVessels: starting with " +
                $"{pids?.Count ?? 0} pid(s), {names?.Count ?? 0} name(s), " +
                $"{FlightGlobals.Vessels.Count} loaded vessel(s)");
            var activeVessel = FlightGlobals.ActiveVessel;
            uint activePid = activeVessel != null ? activeVessel.persistentId : 0;
            bool hasPids = pids != null && pids.Count > 0;
            bool hasNames = names != null && names.Count > 0;
            int recovered = 0;

            for (int i = FlightGlobals.Vessels.Count - 1; i >= 0; i--)
            {
                var vessel = FlightGlobals.Vessels[i];
                if (vessel.persistentId == activePid)
                {
                    ParsekLog.Verbose("Flight",
                        $"CleanupOrphanedSpawnedVessels: skipping active vessel '{vessel.vesselName}' pid={vessel.persistentId}");
                    continue;
                }
                if (GhostMapPresence.IsGhostMapVessel(vessel.persistentId))
                {
                    ParsekLog.Verbose("Flight",
                        $"CleanupOrphanedSpawnedVessels: skipping ghost map vessel pid={vessel.persistentId}");
                    continue;
                }

                string resolvedName = Recording.ResolveLocalizedName(vessel.vesselName);
                bool matchByPid = hasPids && pids.Contains(vessel.persistentId);
                bool matchByName = hasNames && names.Contains(resolvedName);

                if (matchByPid || matchByName)
                {
                    string matchMethod = matchByPid ? "PID" : "name";
                    ParsekLog.Info("Flight",
                        $"CleanupOrphanedSpawnedVessels: recovering '{vessel.vesselName}' " +
                        $"pid={vessel.persistentId} (matched by {matchMethod})");
                    ShipConstruction.RecoverVesselFromFlight(
                        vessel.protoVessel, HighLogic.CurrentGame.flightState, true);
                    recovered++;
                }
                else
                {
                    ParsekLog.Verbose("Flight",
                        $"CleanupOrphanedSpawnedVessels: no match for '{vessel.vesselName}' " +
                        $"pid={vessel.persistentId} resolved='{resolvedName}'");
                }
            }

            if (recovered > 0)
                ParsekLog.Info("Flight",
                    $"CleanupOrphanedSpawnedVessels: recovered {recovered} orphaned vessel(s)");
        }

        /// <summary>
        /// Finds and recovers any in-scene vessel that was timeline-spawned from a
        /// committed recording with the given name. Prevents overlap collisions when
        /// a tree commit spawns a vessel on top of an already-spawned timeline vessel.
        /// </summary>
        void RecoverTimelineSpawnedVessel(string vesselName)
        {
            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (!rec.VesselSpawned || rec.SpawnedVesselPersistentId == 0) continue;
                if (rec.VesselName != vesselName) continue;

                // Find the loaded vessel by persistent ID
                Vessel vessel = null;
                for (int v = 0; v < FlightGlobals.Vessels.Count; v++)
                {
                    if (FlightGlobals.Vessels[v].persistentId == rec.SpawnedVesselPersistentId)
                    {
                        vessel = FlightGlobals.Vessels[v];
                        break;
                    }
                }
                if (vessel != null && vessel.loaded)
                {
                    ParsekLog.Info("Flight",
                        $"RecoverTimelineSpawnedVessel: recovering '{vesselName}' pid={rec.SpawnedVesselPersistentId} " +
                        $"(from recording #{i}) to make room for tree leaf spawn");
                    ShipConstruction.RecoverVesselFromFlight(vessel.protoVessel, HighLogic.CurrentGame.flightState, true);
                }

                // Mark recording as no longer having a live vessel
                rec.VesselSpawned = false;
                rec.SpawnedVesselPersistentId = 0;
            }
        }

        /// <summary>
        /// Captures terminal orbit parameters from a vessel's current orbit.
        /// </summary>
        internal static void CaptureTerminalOrbit(Recording rec, Vessel vessel)
        {
            if (vessel == null || vessel.orbit == null) return;

            var sit = vessel.situation;
            if (sit == Vessel.Situations.ORBITING || sit == Vessel.Situations.SUB_ORBITAL
                || sit == Vessel.Situations.FLYING || sit == Vessel.Situations.ESCAPING)
            {
                var orb = vessel.orbit;
                string bodyName = orb.referenceBody?.name;
                if (string.IsNullOrEmpty(bodyName))
                {
                    ParsekLog.Warn("Flight",
                        $"CaptureTerminalOrbit: skipped '{rec?.RecordingId ?? "(null)"}' " +
                        $"because orbit reference body was null (vessel='{vessel.vesselName}')");
                    return;
                }

                rec.TerminalOrbitInclination = orb.inclination;
                rec.TerminalOrbitEccentricity = orb.eccentricity;
                rec.TerminalOrbitSemiMajorAxis = orb.semiMajorAxis;
                rec.TerminalOrbitLAN = orb.LAN;
                rec.TerminalOrbitArgumentOfPeriapsis = orb.argumentOfPeriapsis;
                rec.TerminalOrbitMeanAnomalyAtEpoch = orb.meanAnomalyAtEpoch;
                rec.TerminalOrbitEpoch = orb.epoch;
                rec.TerminalOrbitBody = bodyName;
            }
        }

        private const double TerminalOrbitNumericTolerance = 1e-6;

        private static bool TerminalOrbitScalarMatches(double cachedValue, double segmentValue)
            => Math.Abs(cachedValue - segmentValue) <= TerminalOrbitNumericTolerance;

        private static bool CachedTerminalOrbitMatchesSegment(Recording rec, OrbitSegment seg)
        {
            if (rec == null || string.IsNullOrEmpty(rec.TerminalOrbitBody))
                return false;

            return string.Equals(rec.TerminalOrbitBody, seg.bodyName, StringComparison.Ordinal)
                && TerminalOrbitScalarMatches(rec.TerminalOrbitInclination, seg.inclination)
                && TerminalOrbitScalarMatches(rec.TerminalOrbitEccentricity, seg.eccentricity)
                && TerminalOrbitScalarMatches(rec.TerminalOrbitSemiMajorAxis, seg.semiMajorAxis)
                && TerminalOrbitScalarMatches(rec.TerminalOrbitLAN, seg.longitudeOfAscendingNode)
                && TerminalOrbitScalarMatches(rec.TerminalOrbitArgumentOfPeriapsis, seg.argumentOfPeriapsis)
                && TerminalOrbitScalarMatches(rec.TerminalOrbitMeanAnomalyAtEpoch, seg.meanAnomalyAtEpoch)
                && TerminalOrbitScalarMatches(rec.TerminalOrbitEpoch, seg.epoch);
        }

        private static void CopyTerminalOrbitFromSegment(Recording rec, OrbitSegment seg)
        {
            rec.TerminalOrbitInclination = seg.inclination;
            rec.TerminalOrbitEccentricity = seg.eccentricity;
            rec.TerminalOrbitSemiMajorAxis = seg.semiMajorAxis;
            rec.TerminalOrbitLAN = seg.longitudeOfAscendingNode;
            rec.TerminalOrbitArgumentOfPeriapsis = seg.argumentOfPeriapsis;
            rec.TerminalOrbitMeanAnomalyAtEpoch = seg.meanAnomalyAtEpoch;
            rec.TerminalOrbitEpoch = seg.epoch;
            rec.TerminalOrbitBody = seg.bodyName;
        }

        private static bool TryGetSameUtPointAnchoredTerminalOrbitBody(
            Recording rec,
            OrbitSegment seg,
            out string pointBody,
            out double pointUT)
        {
            pointBody = null;
            pointUT = 0.0;

            if (rec?.Points == null || rec.Points.Count == 0
                || string.IsNullOrEmpty(rec.TerminalOrbitBody))
            {
                return false;
            }

            // Only the terminal point is authoritative for this finalize-only
            // same-UT check; earlier points may legitimately belong to a prior SOI.
            TrajectoryPoint lastPoint = rec.Points[rec.Points.Count - 1];
            pointBody = lastPoint.bodyName;
            pointUT = lastPoint.ut;
            if (string.IsNullOrEmpty(pointBody)
                || !string.Equals(pointBody, rec.TerminalOrbitBody, StringComparison.Ordinal)
                || string.Equals(pointBody, seg.bodyName, StringComparison.Ordinal))
            {
                return false;
            }

            return Math.Abs(seg.startUT - pointUT) <= TerminalOrbitNumericTolerance;
        }

        private static bool TryGetLastMatchingBodyOrbitSegment(
            Recording rec,
            string bodyName,
            out OrbitSegment matchingSeg)
        {
            matchingSeg = default;
            if (rec?.OrbitSegments == null || string.IsNullOrEmpty(bodyName))
                return false;

            // Skip the conflicting last segment and walk backward through earlier
            // orbit evidence on the point-anchored body.
            for (int i = rec.OrbitSegments.Count - 2; i >= 0; i--)
            {
                OrbitSegment candidate = rec.OrbitSegments[i];
                if (candidate.semiMajorAxis > 0.0
                    && string.Equals(candidate.bodyName, bodyName, StringComparison.Ordinal))
                {
                    matchingSeg = candidate;
                    return true;
                }
            }

            return false;
        }

        private static void LogPreservedSameUtPointAnchor(
            Recording rec,
            OrbitSegment conflictingSeg,
            string pointBody,
            double pointUT)
        {
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "FinalizeIndividualRecording: preserved same-UT point-anchored terminal orbit for '{0}' pointBody={1} conflictingSegmentBody={2} conflictingSegmentStartUT={3:F3} pointUT={4:F3}",
                    rec?.RecordingId ?? "(null)",
                    pointBody,
                    conflictingSeg.bodyName,
                    conflictingSeg.startUT,
                    pointUT));
        }

        private static void LogSuspiciousSameUtPointAnchorPreserve(
            Recording rec,
            OrbitSegment conflictingSeg,
            string pointBody,
            double pointUT)
        {
            ParsekLog.Warn("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "FinalizeIndividualRecording: preserved same-UT point-anchored terminal orbit for '{0}' without matching-body heal evidence despite cachedSma={1:F1} pointBody={2} conflictingSegmentBody={3} conflictingSegmentStartUT={4:F3} pointUT={5:F3}",
                    rec?.RecordingId ?? "(null)",
                    rec?.TerminalOrbitSemiMajorAxis ?? double.NaN,
                    pointBody,
                    conflictingSeg.bodyName,
                    conflictingSeg.startUT,
                    pointUT));
        }

        private static bool TryHandleFinalizeSameUtPointAnchoredTerminalOrbit(Recording rec)
        {
            if (rec?.OrbitSegments == null || rec.OrbitSegments.Count == 0)
                return false;

            OrbitSegment conflictingSeg = rec.OrbitSegments[rec.OrbitSegments.Count - 1];
            if (!TryGetSameUtPointAnchoredTerminalOrbitBody(
                rec,
                conflictingSeg,
                out string pointBody,
                out double pointUT))
            {
                return false;
            }

            if (TryGetLastMatchingBodyOrbitSegment(rec, pointBody, out OrbitSegment matchingSeg))
            {
                if (CachedTerminalOrbitMatchesSegment(rec, matchingSeg))
                {
                    LogPreservedSameUtPointAnchor(rec, conflictingSeg, pointBody, pointUT);
                }
                else
                {
                    string previousBody = rec.TerminalOrbitBody;
                    double previousSemiMajorAxis = rec.TerminalOrbitSemiMajorAxis;
                    CopyTerminalOrbitFromSegment(rec, matchingSeg);
                    ParsekLog.Warn("Flight",
                        string.Format(CultureInfo.InvariantCulture,
                            "FinalizeIndividualRecording: healed same-UT point-anchored terminal orbit for '{0}' previousBody={1} previousSma={2:F1} healedBody={3} healedSma={4:F1} matchingSegmentEndUT={5:F3} conflictingSegmentBody={6} conflictingSegmentStartUT={7:F3} pointUT={8:F3}",
                            rec.RecordingId ?? "(null)",
                            previousBody ?? "(empty)",
                            previousSemiMajorAxis,
                            matchingSeg.bodyName,
                            matchingSeg.semiMajorAxis,
                            matchingSeg.endUT,
                            conflictingSeg.bodyName,
                            conflictingSeg.startUT,
                            pointUT));
                }

                return true;
            }

            if (rec.TerminalOrbitSemiMajorAxis <= 0.0)
                LogSuspiciousSameUtPointAnchorPreserve(rec, conflictingSeg, pointBody, pointUT);
            else
                LogPreservedSameUtPointAnchor(rec, conflictingSeg, pointBody, pointUT);
            return true;
        }

        /// <summary>
        /// Returns whether the last endpoint-aligned OrbitSegment should repopulate
        /// terminal orbit fields, either for unloaded/destroyed vessels or to heal
        /// a stale cached terminal-orbit tuple on finalize/load. Explicit point/surface
        /// endpoint data keeps already-populated terminal orbit fields authoritative;
        /// otherwise, already-populated values are preserved only when the full cached
        /// tuple already matches the endpoint-aligned segment.
        /// (#219/#475/#484)
        /// </summary>
        internal static bool ShouldPopulateTerminalOrbitFromLastSegment(Recording rec)
        {
            if (rec?.OrbitSegments == null || rec.OrbitSegments.Count == 0)
                return false;

            OrbitSegment seg = rec.OrbitSegments[rec.OrbitSegments.Count - 1];
            if (string.IsNullOrEmpty(seg.bodyName))
                return false;

            bool hasEndpointBody = RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out string endpointBody);
            bool endpointAligned = hasEndpointBody
                && string.Equals(seg.bodyName, endpointBody, StringComparison.Ordinal);
            if (string.IsNullOrEmpty(rec.TerminalOrbitBody))
            {
                return !hasEndpointBody || endpointAligned;
            }

            bool hasExplicitEndpointBody = RecordingEndpointResolver.TryGetExplicitEndpointBodyName(
                rec,
                out string explicitEndpointBody);
            if (hasExplicitEndpointBody
                && string.Equals(rec.TerminalOrbitBody, explicitEndpointBody, StringComparison.Ordinal))
            {
                ParsekLog.Info("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "ShouldPopulateTerminalOrbitFromLastSegment: preserved cached terminal orbit for '{0}' because explicit endpoint body={1} keeps cached orbit authoritative over later segment body={2} sma={3:F1}",
                        rec.RecordingId ?? "(null)",
                        explicitEndpointBody,
                        seg.bodyName,
                        seg.semiMajorAxis));
                return false;
            }

            bool cachedTupleMatchesLastSegment = CachedTerminalOrbitMatchesSegment(rec, seg);
            if (cachedTupleMatchesLastSegment)
            {
                if (endpointAligned)
                {
                    ParsekLog.Info("Flight",
                        string.Format(CultureInfo.InvariantCulture,
                            "ShouldPopulateTerminalOrbitFromLastSegment: preserved cached terminal orbit for '{0}' because cached tuple already matches endpoint-aligned segment body={1} sma={2:F1}",
                            rec.RecordingId ?? "(null)",
                            seg.bodyName,
                            seg.semiMajorAxis));
                }

                return false;
            }

            if (hasExplicitEndpointBody)
            {
                return string.Equals(seg.bodyName, explicitEndpointBody, StringComparison.Ordinal)
                    && !string.Equals(rec.TerminalOrbitBody, explicitEndpointBody, StringComparison.Ordinal);
            }

            if (!hasEndpointBody)
                return false;

            return endpointAligned;
        }

        internal static void PopulateTerminalOrbitFromLastSegment(Recording rec)
        {
            if (!ShouldPopulateTerminalOrbitFromLastSegment(rec)) return;

            var seg = rec.OrbitSegments[rec.OrbitSegments.Count - 1];
            string previousBody = rec.TerminalOrbitBody;
            double previousSemiMajorAxis = rec.TerminalOrbitSemiMajorAxis;
            bool healingStaleCachedTuple = !string.IsNullOrEmpty(previousBody)
                && !CachedTerminalOrbitMatchesSegment(rec, seg);
            CopyTerminalOrbitFromSegment(rec, seg);

            if (healingStaleCachedTuple)
            {
                ParsekLog.Warn("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "PopulateTerminalOrbitFromLastSegment: healed stale cached terminal orbit for '{0}' previousBody={1} previousSma={2:F1} newBody={3} newSma={4:F1}",
                        rec.RecordingId ?? "(null)",
                        previousBody ?? "(empty)",
                        previousSemiMajorAxis,
                        seg.bodyName,
                        seg.semiMajorAxis));
                return;
            }

            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "PopulateTerminalOrbitFromLastSegment: recovered orbit for '{0}' from segment body={1} sma={2:F1}",
                    rec.RecordingId ?? "(null)",
                    seg.bodyName,
                    seg.semiMajorAxis));
        }

        /// <summary>
        /// Captures terminal surface position from a vessel's current state.
        /// </summary>
        static void CaptureTerminalPosition(Recording rec, Vessel vessel)
        {
            if (vessel == null) return;

            var sit = vessel.situation;
            if (sit == Vessel.Situations.LANDED || sit == Vessel.Situations.SPLASHED
                || sit == Vessel.Situations.PRELAUNCH)
            {
                rec.TerminalPosition = new SurfacePosition
                {
                    body = vessel.mainBody?.name ?? "Kerbin",
                    latitude = vessel.latitude,
                    longitude = vessel.longitude,
                    altitude = vessel.altitude,
                    rotation = vessel.srfRelRotation,
                    rotationRecorded = true,
                    situation = sit == Vessel.Situations.SPLASHED
                        ? SurfaceSituation.Splashed
                        : SurfaceSituation.Landed
                };

                // Capture TRUE surface height (including buildings/mesh objects like
                // the Island Airfield, launchpad, KSC facilities). vessel.terrainAltitude
                // is computed by KSP from Physics.Raycast in CheckGroundCollision, so it
                // accounts for placed colliders — unlike body.TerrainAltitude() which is
                // PQS-only and returns the raw planetary surface UNDER any mesh object.
                //
                // CAVEAT (review finding): KSP's Vessel.UpdatePosVel sets
                //   terrainAltitude = altitude - heightFromTerrain   // raycast path, includes buildings
                // only when vessel.heightFromTerrain != -1f, which requires the vessel to
                // be loaded AND unpacked. For a packed vessel it falls through to
                //   terrainAltitude = pqsAltitude                     // PQS-only path
                // which is exactly the value we're trying to escape. So if the vessel is
                // packed at capture time (commit-tree-later flow with a backgrounded
                // airfield vessel), fall back to firing our own raycast against the same
                // layer mask KSP uses in GetHeightFromSurface. Covers the mesh-object
                // case without requiring the vessel to be active.
                if (vessel.mainBody != null)
                {
                    double captured;
                    string source;
                    if (!vessel.packed)
                    {
                        captured = vessel.terrainAltitude;
                        source = "vessel.terrainAltitude";
                    }
                    else
                    {
                        double raycastSurface = VesselSpawner.TryFindSurfaceAltitudeViaRaycast(
                            vessel.mainBody, vessel.latitude, vessel.longitude, vessel.altitude);
                        if (!double.IsNaN(raycastSurface))
                        {
                            captured = raycastSurface;
                            source = "packed-vessel raycast";
                        }
                        else
                        {
                            // Last-resort fallback: PQS terrain. Logs a warning so it's visible
                            // if the mesh-object fix silently degrades on a future recording.
                            captured = vessel.mainBody.TerrainAltitude(vessel.latitude, vessel.longitude);
                            source = "PQS fallback";
                            ParsekLog.Warn("TerrainCorrect",
                                $"CaptureTerminalPosition: vessel '{vessel.vesselName}' is packed and " +
                                $"raycast missed — falling back to PQS terrain (may bury mesh-object spawns)");
                        }
                    }

                    rec.TerrainHeightAtEnd = captured;
                    double pqs = vessel.mainBody.TerrainAltitude(vessel.latitude, vessel.longitude);
                    ParsekLog.Verbose("TerrainCorrect",
                        $"Captured surface height at recording end: {rec.TerrainHeightAtEnd:F1}m " +
                        $"(source={source}, vessel alt={vessel.altitude:F1}m, " +
                        $"clearance={vessel.altitude - rec.TerrainHeightAtEnd:F1}m, " +
                        $"pqsTerrain={pqs:F1}m, meshOffset={rec.TerrainHeightAtEnd - pqs:F1}m)");
                }
            }
        }

        #endregion

        #region Gloops Flight Recorder (parallel ghost-only recording)

        private FlightRecorder gloopsRecorder;
        private Recording lastGloopsRecording;

        /// <summary>True when the Gloops ghost-only recorder is actively sampling.</summary>
        internal bool IsGloopsRecording => gloopsRecorder?.IsRecording ?? false;

        /// <summary>
        /// The most recent Gloops recording (committed or in-progress).
        /// Used by GloopsRecorderUI for preview and discard.
        /// </summary>
        internal Recording LastGloopsRecording => lastGloopsRecording;

        /// <summary>Access to in-progress Gloops recorder for point count display.</summary>
        internal FlightRecorder GloopsRecorderForUI => gloopsRecorder;

        /// <summary>
        /// Starts a parallel Gloops ghost-only recording on the active vessel.
        /// Can run alongside the main auto-recording.
        /// </summary>
        internal void StartGloopsRecording()
        {
            if (IsGloopsRecording)
            {
                ParsekLog.Warn("Flight", "StartGloopsRecording called while already recording");
                return;
            }

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null)
            {
                ParsekLog.Warn("Flight", "StartGloopsRecording: no active vessel");
                return;
            }

            gloopsRecorder = new FlightRecorder { IsGloopsMode = true };
            gloopsRecorder.StartRecording(isPromotion: false);

            if (!gloopsRecorder.IsRecording)
            {
                ParsekLog.Warn("Flight", "StartGloopsRecording blocked (paused or no vessel)");
                gloopsRecorder = null;
                return;
            }

            // Capture ghost visual snapshot at recording start (full vessel before staging)
            ConfigNode ghostSnap = VesselSpawner.TryBackupSnapshot(v);

            // Stash it for later use when building the recording
            gloopsRecorder.GloopsGhostVisualSnapshot = ghostSnap;

            lastGloopsRecording = null;

            ParsekLog.Info("Flight",
                $"Gloops recording started: vessel={v.vesselName}, pid={v.persistentId}, " +
                $"ghostSnap={ghostSnap != null}");
            ParsekLog.ScreenMessage("Gloops Recording STARTED", 2f);
        }

        /// <summary>
        /// Stops the Gloops recorder, builds a Recording, and commits it as ghost-only
        /// with looping off by default and the loop period initialized to auto.
        /// </summary>
        internal void StopGloopsRecording()
        {
            if (gloopsRecorder == null)
            {
                ParsekLog.Warn("Flight", "StopGloopsRecording: no Gloops recorder");
                return;
            }

            // Normal stop: recorder still running
            if (gloopsRecorder.IsRecording)
                gloopsRecorder.StopRecording();

            CommitGloopsRecorderData("StopGloopsRecording");
        }

        /// <summary>
        /// Called each frame to detect Gloops recordings auto-stopped by vessel switch
        /// and commit them automatically so recording data is not lost.
        /// </summary>
        private void CheckGloopsAutoStoppedByVesselSwitch()
        {
            if (gloopsRecorder == null) return;
            if (!gloopsRecorder.GloopsAutoStoppedByVesselSwitch) return;

            gloopsRecorder.GloopsAutoStoppedByVesselSwitch = false;
            ParsekLog.Info("Flight", "Gloops recorder was auto-stopped by vessel switch — committing");
            CommitGloopsRecorderData("GloopsAutoCommit");
            ParsekLog.ScreenMessage("Gloops recording auto-saved (vessel switched)", 3f);
        }

        /// <summary>
        /// Shared commit path for Gloops recordings: builds a Recording from the
        /// FlightRecorder's buffers, marks it ghost-only, defaults looping off,
        /// initializes the period to auto, and commits.
        /// Used by both manual StopGloopsRecording and vessel-switch auto-commit.
        /// </summary>
        private static string GetActiveVesselNameOrDefault(string fallbackName)
        {
            try
            {
                return Recording.ResolveLocalizedName(FlightGlobals.ActiveVessel?.vesselName)
                    ?? fallbackName;
            }
            catch (TypeInitializationException)
            {
                return fallbackName;
            }
        }

        private void CommitGloopsRecorderData(string logTag)
        {
            Recording rec = RecordingStore.CreateRecordingFromFlightData(
                gloopsRecorder.Recording,
                GetActiveVesselNameOrDefault("Gloops Recording"),
                gloopsRecorder.OrbitSegments,
                partEvents: gloopsRecorder.PartEvents,
                flagEvents: gloopsRecorder.FlagEvents,
                segmentEvents: gloopsRecorder.SegmentEvents,
                trackSections: gloopsRecorder.TrackSections);

            if (rec == null)
            {
                ParsekLog.Warn("Flight", $"{logTag}: not enough points (< 2)");
                gloopsRecorder = null;
                ParsekLog.ScreenMessage("Gloops recording too short - discarded", 2f);
                return;
            }

            rec.IsGhostOnly = true;
            rec.LoopPlayback = false;
            rec.LoopIntervalSeconds = 0;
            rec.LoopTimeUnit = LoopTimeUnit.Auto;

            // Apply snapshots
            rec.GhostVisualSnapshot = gloopsRecorder.GloopsGhostVisualSnapshot;
            var captureAtStop = gloopsRecorder.CaptureAtStop;
            if (captureAtStop != null)
            {
                rec.VesselSnapshot = captureAtStop.VesselSnapshot;
                rec.TerminalStateValue = captureAtStop.TerminalStateValue;
                rec.TerminalPosition = captureAtStop.TerminalPosition;
            }

            RecordingStore.CommitGloopsRecording(rec);
            lastGloopsRecording = rec;
            gloopsRecorder = null;

            ParsekLog.Info("Flight",
                $"{logTag}: Gloops recording committed \"{rec.VesselName}\" " +
                $"({rec.Points.Count} points, id={rec.RecordingId})");
        }

        /// <summary>
        /// Discards the in-progress Gloops recording without committing.
        /// </summary>
        internal void DiscardGloopsInProgress()
        {
            if (gloopsRecorder == null)
            {
                ParsekLog.Warn("Flight", "DiscardGloopsInProgress: no Gloops recorder");
                return;
            }

            if (gloopsRecorder.IsRecording)
                gloopsRecorder.ForceStop();
            gloopsRecorder = null;

            ParsekLog.Info("Flight", "Gloops in-progress recording discarded");
            ParsekLog.ScreenMessage("Gloops recording discarded", 2f);
        }

        /// <summary>
        /// Deletes the most recently committed Gloops recording.
        /// Delegates to DeleteGhostOnlyRecording for full cleanup (ghost destroy,
        /// engine reindex, orbit cache clear, etc.).
        /// </summary>
        internal void DiscardLastGloopsRecording()
        {
            if (lastGloopsRecording == null)
            {
                ParsekLog.Warn("Flight", "DiscardLastGloopsRecording: nothing to discard");
                return;
            }

            var committed = RecordingStore.CommittedRecordings;
            int idx = -1;
            if (!string.IsNullOrEmpty(lastGloopsRecording.RecordingId))
                idx = GhostPlaybackLogic.FindRecordingIndexById(committed, lastGloopsRecording.RecordingId);

            if (idx < 0)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    if (object.ReferenceEquals(committed[i], lastGloopsRecording))
                    {
                        idx = i;
                        break;
                    }
                }
            }

            if (idx >= 0)
            {
                DeleteGhostOnlyRecording(idx);
            }
            else
            {
                ParsekLog.Warn("Flight",
                    $"DiscardLastGloopsRecording: recording not found in committed list " +
                    $"(id={lastGloopsRecording.RecordingId})");
            }

            lastGloopsRecording = null;
        }

        /// <summary>
        /// Starts preview playback of the last Gloops recording and enters watch mode.
        /// Reuses the existing manual playback system.
        /// </summary>
        internal void PreviewGloopsRecording()
        {
            if (lastGloopsRecording == null || lastGloopsRecording.Points.Count < 2)
            {
                ParsekLog.Warn("Flight", "PreviewGloopsRecording: no recording to preview");
                return;
            }

            // Stop any existing preview
            if (isPlaying)
                StopPlayback();

            // Use the committed Gloops recording for preview
            // Set up the preview using the recording's snapshot
            previewRecording = lastGloopsRecording;
            previewGhostState = null;
            GameObject ghost = null;
            bool builtFromSnapshot = false;

            var buildResult = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                previewRecording, "Parsek_Ghost_GloopsPreview");
            if (buildResult != null)
                ghost = buildResult.root;
            builtFromSnapshot = ghost != null;

            if (builtFromSnapshot)
            {
                previewGhostState = new GhostPlaybackState
                {
                    ghost = ghost,
                    playbackIndex = 0,
                    partEventIndex = 0,
                    partTree = GhostVisualBuilder.BuildPartSubtreeMap(
                        GhostVisualBuilder.GetGhostSnapshot(previewRecording)),
                    logicalPartIds = GhostVisualBuilder.BuildSnapshotPartIdSet(
                        GhostVisualBuilder.GetGhostSnapshot(previewRecording))
                };
                previewGhostState.materials = new List<Material>();

                GhostPlaybackLogic.PopulateGhostInfoDictionaries(previewGhostState, buildResult);
                GhostPlaybackLogic.InitializeInventoryPlacementVisibility(previewRecording, previewGhostState);
                GhostPlaybackLogic.RefreshCompoundPartVisibility(previewGhostState);

                if (TrajectoryMath.HasReentryPotential(previewRecording))
                {
                    previewGhostState.reentryFxInfo = GhostVisualBuilder.TryBuildReentryFx(
                        ghost, previewGhostState.heatInfos, -1, previewRecording.VesselName);
                }
                else
                {
                    previewGhostState.reentryFxInfo = null;
                }

                GhostPlaybackLogic.InitializeFlagVisibility(previewRecording, previewGhostState);
                previewGhostMaterials = previewGhostState.materials;
            }
            else
            {
                Color previewColor = Color.green;
                ghost = GhostVisualBuilder.CreateGhostSphere("Parsek_Ghost_GloopsPreview", previewColor);
                var m = ghost.GetComponent<Renderer>()?.material;
                previewGhostMaterials = m != null ? new List<Material> { m } : new List<Material>();
                previewGhostState = null;
            }

            ghostObject = ghost;
            playbackStartUT = Planetarium.GetUniversalTime();
            recordingStartUT = previewRecording.Points[0].ut;
            lastPlaybackIndex = 0;
            isPlaying = true;

            ParsekLog.Info("Flight",
                $"Gloops preview started: \"{previewRecording.VesselName}\" " +
                $"({previewRecording.Points.Count} points)");
            ParsekLog.ScreenMessage("Gloops Preview STARTED", 2f);
        }

        /// <summary>
        /// Cleans up the Gloops recorder on scene change or destroy.
        /// </summary>
        private void CleanupGloopsRecorder()
        {
            if (gloopsRecorder != null)
            {
                if (gloopsRecorder.IsRecording)
                {
                    gloopsRecorder.ForceStop();
                    ParsekLog.Info("Flight", "Gloops recorder force-stopped on cleanup");
                }
                Patches.PhysicsFramePatch.GloopsRecorderInstance = null;
                gloopsRecorder = null;
            }
        }

        #endregion

        #region Manual Playback (preview)

        public void StartPlayback()
        {
            if (recording.Count < 2)
            {
                Log("Not enough recording points for playback!");
                return;
            }

            ExitAllWarpForPlaybackStart("Preview");

            // Build preview ghost from CaptureAtStop snapshot (same as timeline ghosts)
            // so that part events play back identically.
            previewRecording = null;
            previewGhostState = null;
            GameObject ghost = null;
            bool builtFromSnapshot = false;

            var capture = recorder?.CaptureAtStop;
            if (capture != null)
            {
                previewRecording = capture;
                var buildResult = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                    previewRecording, "Parsek_Ghost_Preview");
                if (buildResult != null)
                    ghost = buildResult.root;
                builtFromSnapshot = ghost != null;

                if (builtFromSnapshot)
                {
                    previewGhostState = new GhostPlaybackState
                    {
                        ghost = ghost,
                        playbackIndex = 0,
                        partEventIndex = 0,
                        partTree = GhostVisualBuilder.BuildPartSubtreeMap(
                            GhostVisualBuilder.GetGhostSnapshot(previewRecording)),
                        logicalPartIds = GhostVisualBuilder.BuildSnapshotPartIdSet(
                            GhostVisualBuilder.GetGhostSnapshot(previewRecording))
                    };

                    previewGhostState.materials = new List<Material>();

                    GhostPlaybackLogic.PopulateGhostInfoDictionaries(previewGhostState, buildResult);

                    GhostPlaybackLogic.InitializeInventoryPlacementVisibility(previewRecording, previewGhostState);
                    GhostPlaybackLogic.RefreshCompoundPartVisibility(previewGhostState);

                    // Same reentry-potential gate as the timeline engine path (#406):
                    // skip the mesh-combine / ParticleSystem build for trajectories that
                    // cannot possibly produce reentry visuals.
                    if (TrajectoryMath.HasReentryPotential(previewRecording))
                    {
                        previewGhostState.reentryFxInfo = GhostVisualBuilder.TryBuildReentryFx(
                            ghost, previewGhostState.heatInfos, -1, previewRecording.VesselName);
                    }
                    else
                    {
                        previewGhostState.reentryFxInfo = null;
                        ParsekLog.Verbose("ReentryFx",
                            $"Skipped reentry FX build for preview ghost \"{previewRecording.VesselName}\" " +
                            $"— trajectory peak speed below {TrajectoryMath.ReentryPotentialSpeedFloor:F0} m/s and no orbit segments");
                    }

                    // Initialize flag event index for preview playback
                    GhostPlaybackLogic.InitializeFlagVisibility(previewRecording, previewGhostState);

                    Log("Manual preview ghost: built from recording-start snapshot (with part events)");
                }
            }

            if (!builtFromSnapshot)
            {
                Color previewColor = Color.green;
                ghost = GhostVisualBuilder.CreateGhostSphere("Parsek_Ghost_Preview", previewColor);
                var m = ghost.GetComponent<Renderer>()?.material;
                previewGhostMaterials = m != null ? new List<Material> { m } : new List<Material>();
                previewGhostState = null;
                previewRecording = null;
                Log("Manual preview ghost: using sphere fallback");
            }
            else
            {
                previewGhostMaterials = previewGhostState.materials;
            }

            ghostObject = ghost;

            // Replay from current time (ghost starts at recording start position)
            playbackStartUT = Planetarium.GetUniversalTime();
            recordingStartUT = recording[0].ut;
            lastPlaybackIndex = 0;

            isPlaying = true;

            Log($"Manual playback started. Duration: {recording[recording.Count - 1].ut - recording[0].ut:F1}s");
            ScreenMessage("Preview Playback STARTED", 2f);
        }

        public void StopPlayback()
        {
            if (isPlaying)
                Log("Manual playback stopped");
            isPlaying = false;

            if (previewGhostState != null)
            {
                if (previewGhostState.materials != null)
                {
                    for (int i = 0; i < previewGhostState.materials.Count; i++)
                    {
                        if (previewGhostState.materials[i] != null)
                            Destroy(previewGhostState.materials[i]);
                    }
                }

                if (previewGhostState.engineInfos != null)
                {
                    foreach (var info in previewGhostState.engineInfos.Values)
                        for (int i = 0; i < info.particleSystems.Count; i++)
                            if (info.particleSystems[i] != null)
                                Destroy(info.particleSystems[i].gameObject);
                }

                if (previewGhostState.rcsInfos != null)
                {
                    foreach (var info in previewGhostState.rcsInfos.Values)
                        for (int i = 0; i < info.particleSystems.Count; i++)
                            if (info.particleSystems[i] != null)
                                Destroy(info.particleSystems[i].gameObject);
                }

                engine.DestroyReentryFxResources(previewGhostState.reentryFxInfo);

                GhostPlaybackLogic.DestroyAllFakeCanopies(previewGhostState);
                previewGhostState = null;
            }
            else if (previewGhostMaterials != null)
            {
                // Sphere fallback path — no GhostPlaybackState
                for (int i = 0; i < previewGhostMaterials.Count; i++)
                {
                    if (previewGhostMaterials[i] != null)
                        Destroy(previewGhostMaterials[i]);
                }
            }
            previewGhostMaterials.Clear();
            previewRecording = null;

            if (ghostObject != null)
            {
                Destroy(ghostObject);
                ghostObject = null;
            }

            orbitCache.Clear();
            loggedOrbitSegments.Clear();
            loggedOrbitRotationSegments.Clear();
        }

        void UpdatePlayback()
        {
            if (ghostObject == null || recording.Count < 2)
            {
                StopPlayback();
                return;
            }

            double currentUT = Planetarium.GetUniversalTime();
            double elapsedSinceStart = currentUT - playbackStartUT;
            double recordingTime = recordingStartUT + elapsedSinceStart;

            if (recordingTime > recording[recording.Count - 1].ut)
            {
                Log("Manual playback complete — reached end of recording");
                // explosionAlreadyFired=false is safe: preview is one-shot (StopPlayback called immediately after)
                if (previewRecording != null && ghostObject != null
                    && !GhostPlaybackLogic.ShouldSuppressVisualFx(TimeWarp.CurrentRate)
                    && GhostPlaybackLogic.ShouldTriggerExplosion(false, previewRecording.TerminalStateValue,
                           true, previewRecording.VesselName, -1))
                {
                    Vector3 pos = ghostObject.transform.position;
                    float len = GhostVisualBuilder.ComputeGhostLength(ghostObject);
                    double power = Mathf.Clamp01(len / 20f);
                    ParsekLog.Info("ExplosionFx",
                        $"Stock FXMonger.Explode for manual preview \"{previewRecording.VesselName}\" " +
                        $"at ({pos.x:F1},{pos.y:F1},{pos.z:F1}) vesselLength={len:F1}m " +
                        $"power={power.ToString("F2", CultureInfo.InvariantCulture)}");
                    // Hide ghost parts before explosion renders
                    if (previewGhostState != null)
                        GhostPlaybackLogic.HideAllGhostParts(previewGhostState);
                    else
                    {
                        var t = ghostObject.transform;
                        for (int c = 0; c < t.childCount; c++)
                            t.GetChild(c).gameObject.SetActive(false);
                    }
                    if (!GhostVisualBuilder.TryTriggerStockExplosionFx(pos, power, out string stockFxFailure))
                    {
                        ParsekLog.Warn("ExplosionFx",
                            $"FXMonger.Explode did not queue stock FX for manual preview " +
                            $"\"{previewRecording.VesselName}\"; falling back to custom FX: {stockFxFailure}");
                        GhostVisualBuilder.SpawnExplosionFx(pos, len);
                    }
                }
                StopPlayback();
                ScreenMessage("Preview playback complete", 2f);
                return;
            }

            InterpolationResult interpResult;
            bool surfaceSkip = previewRecording != null &&
                TrajectoryMath.IsSurfaceAtUT(previewRecording.TrackSections, recordingTime);
            InterpolateAndPosition(ghostObject, recording, orbitSegments,
                ref lastPlaybackIndex, recordingTime, 10000, out interpResult,
                skipOrbitSegments: surfaceSkip);

            if (previewGhostState != null && previewRecording != null)
            {
                previewGhostState.SetInterpolated(interpResult);
                GhostPlaybackLogic.ApplyPartEvents(-1, previewRecording, recordingTime, previewGhostState);
                GhostPlaybackLogic.ApplyFlagEvents(previewGhostState, previewRecording, recordingTime);
                engine.UpdateReentryFx(-1, previewGhostState, previewRecording.VesselName ?? "Preview", TimeWarp.CurrentRate);
            }
        }

        #endregion

        #region Timeline Auto-Playback

        /// <summary>
        /// Returns the GhostChain if this recording is a chain tip that should spawn via VesselGhoster.
        /// Returns null for non-chain recordings, intermediate links, or terminated chains.
        /// </summary>
        internal static GhostChain FindChainTipForRecording(
            Dictionary<uint, GhostChain> chains, Recording rec)
        {
            if (chains == null || chains.Count == 0)
                return null;

            foreach (var kvp in chains)
            {
                if (kvp.Value.TipRecordingId == rec.RecordingId && !kvp.Value.IsTerminated)
                    return kvp.Value;
            }

            return null;
        }

        /// <summary>
        /// Finds the first committed recording that covers the given UT for a vessel PID.
        /// This is a global PID-only lookup. Chain playback must use the chain-scoped
        /// helper below to avoid selecting a different alternate-history tree that
        /// happens to reuse the same PID.
        /// </summary>
        internal static Recording FindBackgroundRecordingForVessel(
            IReadOnlyList<Recording> committedRecordings,
            uint vesselPid,
            double currentUT,
            ISet<string> excludedTreeIds = null)
        {
            if (committedRecordings == null) return null;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (excludedTreeIds != null
                    && !string.IsNullOrEmpty(rec.TreeId)
                    && excludedTreeIds.Contains(rec.TreeId))
                    continue;
                if (rec.VesselPersistentId == vesselPid &&
                    rec.Points != null && rec.Points.Count > 0 &&
                    currentUT >= rec.StartUT && currentUT <= rec.EndUT)
                    return rec;
            }
            return null;
        }

        /// <summary>
        /// Finds the first committed recording with non-point playback data that covers
        /// the given UT for a vessel PID. Used by chain ghost fallback positioning.
        /// </summary>
        internal static Recording FindPlaybackFallbackRecordingForVessel(
            IReadOnlyList<Recording> committedRecordings,
            uint vesselPid,
            double currentUT,
            ISet<string> excludedTreeIds = null)
        {
            if (committedRecordings == null) return null;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (excludedTreeIds != null
                    && !string.IsNullOrEmpty(rec.TreeId)
                    && excludedTreeIds.Contains(rec.TreeId))
                    continue;
                if (rec.VesselPersistentId != vesselPid)
                    continue;
                if (!RecordingHasUsableNonPointPlaybackDataAtUT(rec, currentUT))
                    continue;

                return rec;
            }

            return null;
        }

        /// <summary>
        /// Finds a point-backed trajectory recording for a chain at the given UT.
        /// Uses the exact chain path within committed trees when available, and only
        /// falls back to global PID lookup before the first claim.
        /// </summary>
        internal static Recording FindBackgroundRecordingForChain(
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<RecordingTree> committedTrees,
            GhostChain chain,
            double currentUT)
        {
            if (committedRecordings == null || chain == null)
                return null;

            var chainRec = FindExactChainRecordingAtUT(committedTrees, chain, currentUT);
            if (chainRec != null)
                return chainRec.Points != null && chainRec.Points.Count > 0 ? chainRec : null;

            if (ChainHasStarted(chain, currentUT))
                return null;

            var preClaimRec = FindPreClaimChainRecordingAtUT(
                committedTrees, chain, currentUT,
                out bool hasPreClaimCoverage, out bool preClaimAmbiguous);
            if (hasPreClaimCoverage && !preClaimAmbiguous)
                return preClaimRec != null && preClaimRec.Points != null && preClaimRec.Points.Count > 0
                    ? preClaimRec : null;

            return FindBackgroundRecordingForVessel(
                committedRecordings, chain.OriginalVesselPid, currentUT,
                preClaimAmbiguous ? GetFirstClaimTreeIds(chain) : null);
        }

        internal static Recording FindPreClaimChainRecordingAtUT(
            IReadOnlyList<RecordingTree> committedTrees,
            GhostChain chain,
            double currentUT,
            out bool hasChainLocalCoverage,
            out bool isAmbiguous)
        {
            hasChainLocalCoverage = false;
            isAmbiguous = false;
            if (committedTrees == null || chain == null || chain.Links == null || chain.Links.Count == 0)
                return null;

            var firstClaimTreeIds = GetFirstClaimTreeIds(chain);
            if (firstClaimTreeIds.Count == 0)
                return null;

            Recording unique = null;
            foreach (var treeId in firstClaimTreeIds)
            {
                RecordingTree firstClaimTree = FindCommittedTree(committedTrees, treeId);
                if (firstClaimTree == null)
                    continue;

                foreach (var rec in firstClaimTree.Recordings.Values)
                {
                    if (rec.VesselPersistentId != chain.OriginalVesselPid)
                        continue;
                    if (currentUT < rec.StartUT || currentUT > rec.EndUT)
                        continue;
                    if (!RecordingHasUsablePlaybackDataAtUT(rec, currentUT))
                        continue;

                    if (unique != null)
                    {
                        isAmbiguous = true;
                        hasChainLocalCoverage = true;
                        return null;
                    }

                    unique = rec;
                }
            }

            hasChainLocalCoverage = unique != null;
            return unique;
        }

        internal static Recording FindExactChainRecordingAtUT(
            IReadOnlyList<RecordingTree> committedTrees, GhostChain chain, double currentUT)
        {
            if (committedTrees == null || chain == null || chain.Links == null)
                return null;

            for (int i = chain.Links.Count - 1; i >= 0; i--)
            {
                var link = chain.Links[i];
                if (link.ut > currentUT + 0.001)
                    continue;

                return FindChainPathRecordingAtUT(
                    committedTrees, link, chain.OriginalVesselPid, currentUT);
            }

            return null;
        }

        /// <summary>
        /// Finds the best non-point playback source for positioning a chain ghost when
        /// point-backed trajectory playback is unavailable.
        /// </summary>
        internal static Recording FindPositionFallbackRecordingForChain(
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<RecordingTree> committedTrees,
            GhostChain chain,
            double currentUT)
        {
            if (chain == null)
                return null;

            var chainRec = FindExactChainRecordingAtUT(committedTrees, chain, currentUT);
            if (RecordingHasUsableNonPointPlaybackDataAtUT(chainRec, currentUT))
                return chainRec;

            if (ChainHasStarted(chain, currentUT))
                return null;

            var preClaimRec = FindPreClaimChainRecordingAtUT(
                committedTrees, chain, currentUT,
                out bool hasPreClaimCoverage, out bool preClaimAmbiguous);
            if (hasPreClaimCoverage && !preClaimAmbiguous)
                return RecordingHasUsableNonPointPlaybackDataAtUT(preClaimRec, currentUT)
                    ? preClaimRec
                    : null;

            return FindPlaybackFallbackRecordingForVessel(
                committedRecordings, chain.OriginalVesselPid, currentUT,
                preClaimAmbiguous ? GetFirstClaimTreeIds(chain) : null);
        }

        private static HashSet<string> GetFirstClaimTreeIds(GhostChain chain)
        {
            var firstClaimTreeIds = new HashSet<string>();
            if (chain == null || chain.Links == null || chain.Links.Count == 0)
                return firstClaimTreeIds;

            double firstClaimUT = double.MaxValue;
            for (int i = 0; i < chain.Links.Count; i++)
            {
                var link = chain.Links[i];
                if (link.ut < firstClaimUT)
                    firstClaimUT = link.ut;
            }

            if (firstClaimUT == double.MaxValue)
                return firstClaimTreeIds;

            for (int i = 0; i < chain.Links.Count; i++)
            {
                var link = chain.Links[i];
                if (string.IsNullOrEmpty(link.treeId))
                    continue;
                if (Math.Abs(link.ut - firstClaimUT) > 0.001)
                    continue;

                firstClaimTreeIds.Add(link.treeId);
            }

            return firstClaimTreeIds;
        }

        private static Recording FindChainPathRecordingAtUT(
            IReadOnlyList<RecordingTree> committedTrees,
            ChainLink link,
            uint vesselPid,
            double currentUT)
        {
            if (committedTrees == null || string.IsNullOrEmpty(link.treeId))
                return null;

            RecordingTree tree = FindCommittedTree(committedTrees, link.treeId);
            if (tree == null)
                return null;

            Recording startRec;
            if (!tree.Recordings.TryGetValue(link.recordingId, out startRec))
                return null;

            return FindChainPathRecordingAtUT(tree, startRec, vesselPid, currentUT);
        }

        private static Recording FindChainPathRecordingAtUT(
            RecordingTree tree,
            Recording startRec,
            uint vesselPid,
            double currentUT)
        {
            if (tree == null || startRec == null)
                return null;

            var visited = new HashSet<string>();
            var current = startRec;
            while (current != null && !visited.Contains(current.RecordingId))
            {
                visited.Add(current.RecordingId);

                if (current.VesselPersistentId == vesselPid
                    && currentUT >= current.StartUT && currentUT <= current.EndUT)
                {
                    return current;
                }

                var nextChainRec = FindNextChainRecording(tree, current);
                if (nextChainRec != null)
                {
                    current = nextChainRec;
                    continue;
                }

                current = FindPreferredChildRecording(tree, current);
            }

            return null;
        }

        private static Recording FindNextChainRecording(RecordingTree tree, Recording rec)
        {
            if (tree == null || rec == null || string.IsNullOrEmpty(rec.ChainId))
                return null;

            int nextIdx = rec.ChainIndex + 1;
            foreach (var kvp in tree.Recordings)
            {
                if (kvp.Value.ChainId == rec.ChainId
                    && kvp.Value.ChainIndex == nextIdx
                    && kvp.Value.ChainBranch == rec.ChainBranch)
                {
                    return kvp.Value;
                }
            }

            return null;
        }

        private static Recording FindPreferredChildRecording(RecordingTree tree, Recording rec)
        {
            if (tree == null || rec == null || string.IsNullOrEmpty(rec.ChildBranchPointId))
                return null;

            BranchPoint bp = null;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                if (tree.BranchPoints[i].Id == rec.ChildBranchPointId)
                {
                    bp = tree.BranchPoints[i];
                    break;
                }
            }

            if (bp == null || bp.ChildRecordingIds.Count == 0)
                return null;

            string bestChildId = bp.ChildRecordingIds[0];
            if (rec.VesselPersistentId != 0)
            {
                for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                {
                    Recording candidate;
                    if (tree.Recordings.TryGetValue(bp.ChildRecordingIds[c], out candidate)
                        && candidate.VesselPersistentId == rec.VesselPersistentId)
                    {
                        bestChildId = bp.ChildRecordingIds[c];
                        break;
                    }
                }
            }

            Recording child;
            return tree.Recordings.TryGetValue(bestChildId, out child) ? child : null;
        }

        private static RecordingTree FindCommittedTree(
            IReadOnlyList<RecordingTree> committedTrees, string treeId)
        {
            if (committedTrees == null || string.IsNullOrEmpty(treeId))
                return null;

            for (int i = 0; i < committedTrees.Count; i++)
            {
                if (committedTrees[i].Id == treeId)
                    return committedTrees[i];
            }

            return null;
        }

        private static bool ChainHasStarted(GhostChain chain, double currentUT)
        {
            if (chain == null || chain.Links == null)
                return false;

            for (int i = 0; i < chain.Links.Count; i++)
            {
                if (chain.Links[i].ut <= currentUT + 0.001)
                    return true;
            }

            return false;
        }

        private bool TryPositionChainFallbackFromRecording(
            GameObject ghostGO, GhostChain chain, Recording rec, double currentUT)
        {
            if (ghostGO == null || chain == null || rec == null)
                return false;

            if (rec.HasOrbitSegments)
            {
                if (!HasOrbitCoverageAtUT(rec, currentUT))
                    return false;

                PositionGhostFromOrbitOnly(ghostGO, rec, currentUT,
                    (int)(chain.OriginalVesselPid * 10000), allowActivation: true);
                UpdateChainGhostOrbitIfNeeded(chain, rec.OrbitSegments, currentUT);
                ParsekLog.VerboseRateLimited("Flight",
                    "chain-ghost-orbit-" + chain.OriginalVesselPid,
                    string.Format(CultureInfo.InvariantCulture,
                        "Chain ghost positioned from orbit: pid={0} rec={1} tree={2} UT={3:F1}",
                        chain.OriginalVesselPid, rec.RecordingId, rec.TreeId, currentUT));
                return true;
            }

            if (HasSurfaceCoverageAtUT(rec, currentUT))
            {
                PositionGhostAtSurface(ghostGO, rec.SurfacePos.Value, allowActivation: true);
                ParsekLog.VerboseRateLimited("Flight",
                    "chain-ghost-surface-" + chain.OriginalVesselPid,
                    string.Format(CultureInfo.InvariantCulture,
                        "Chain ghost positioned at surface: pid={0} rec={1} tree={2} body={3}",
                        chain.OriginalVesselPid, rec.RecordingId, rec.TreeId,
                        rec.SurfacePos.Value.body));
                return true;
            }

            return false;
        }

        private static bool RecordingHasUsablePlaybackDataAtUT(Recording rec, double currentUT)
        {
            if (rec == null)
                return false;

            if (rec.Points != null && rec.Points.Count > 0)
                return true;

            if (HasOrbitCoverageAtUT(rec, currentUT))
                return true;

            return HasSurfaceCoverageAtUT(rec, currentUT);
        }

        private static bool RecordingHasUsableNonPointPlaybackDataAtUT(
            Recording rec, double currentUT)
        {
            if (rec == null)
                return false;

            if (HasOrbitCoverageAtUT(rec, currentUT))
                return true;

            return HasSurfaceCoverageAtUT(rec, currentUT);
        }

        private static bool HasOrbitCoverageAtUT(Recording rec, double currentUT)
        {
            if (rec == null || !rec.HasOrbitSegments)
                return false;

            for (int s = 0; s < rec.OrbitSegments.Count; s++)
            {
                if (currentUT >= rec.OrbitSegments[s].startUT && currentUT <= rec.OrbitSegments[s].endUT)
                    return true;
            }

            return false;
        }

        private static bool HasSurfaceCoverageAtUT(Recording rec, double currentUT)
        {
            return rec != null
                && rec.SurfacePos.HasValue
                && currentUT >= rec.StartUT
                && currentUT <= rec.EndUT;
        }

        /// <summary>
        /// Positions all chain ghosts each frame using background recording trajectory data.
        /// For UTs outside recorded range, falls back to orbital propagation or surface hold.
        /// For spawn-blocked chains, the ghost continues at its propagated position
        /// (ghost GO creation deferred to 6b-4 — currently a no-op for visual positioning,
        /// but the blocked chain retry logic runs in SpawnVesselOrChainTip).
        /// </summary>
        private void PositionChainGhosts(double currentUT)
        {
            if (vesselGhoster == null || activeGhostChains == null || activeGhostChains.Count == 0)
                return;

            foreach (var kvp in activeGhostChains)
            {
                GhostChain chain = kvp.Value;

                // Spawn-blocked chains: ghost should continue to be visible at propagated position.
                // Ghost GO is currently null (deferred to 6b-4), so visual positioning is a no-op.
                // The blocked chain spawn retry happens in SpawnVesselOrChainTip during the
                // per-recording iteration. Here we just log the blocked state for diagnostics.
                if (chain.SpawnBlocked)
                {
                    ParsekLog.VerboseRateLimited("Flight", "chain-ghost-blocked-" + chain.OriginalVesselPid,
                        string.Format(CultureInfo.InvariantCulture,
                            "Chain ghost spawn-blocked: pid={0} blockedSince={1:F1} UT={2:F1}",
                            chain.OriginalVesselPid, chain.BlockedSinceUT, currentUT));
                    // When ghost GO creation is implemented (6b-4), position the ghost GO
                    // at the propagated position here using GhostExtender.
                    continue;
                }

                var info = vesselGhoster.GetGhostedInfo(chain.OriginalVesselPid);
                if (info == null || info.ghostGO == null) continue;

                // Find background recording covering current UT within the chain's
                // own tree sequence, not by global PID-only lookup.
                Recording bgRec = FindBackgroundRecordingForChain(
                    RecordingStore.CommittedRecordings, RecordingStore.CommittedTrees, chain, currentUT);

                if (bgRec != null && bgRec.Points.Count > 0)
                {
                    // Position from trajectory points (same interpolation as existing ghost positioning)
                    bool surfaceSkip = TrajectoryMath.IsSurfaceAtUT(bgRec.TrackSections, currentUT);
                    InterpolateAndPosition(info.ghostGO, bgRec.Points, bgRec.OrbitSegments,
                        ref chain.CachedTrajectoryIndex, currentUT, (int)(chain.OriginalVesselPid * 10000),
                        out _, skipOrbitSegments: surfaceSkip);

                    // Update ghost map ProtoVessel orbit if segment changed
                    UpdateChainGhostOrbitIfNeeded(chain, bgRec.OrbitSegments, currentUT);

                    ParsekLog.VerboseRateLimited("Flight", "chain-ghost-trajectory-" + chain.OriginalVesselPid,
                        string.Format(CultureInfo.InvariantCulture,
                            "Chain ghost positioned from trajectory: pid={0} rec={1} UT={2:F1}",
                            chain.OriginalVesselPid, bgRec.RecordingId, currentUT));
                }
                else
                {
                    PositionChainGhostFallback(info.ghostGO, chain, currentUT);
                }
            }
        }

        /// <summary>
        /// Fallback positioning for chain ghosts: searches committed recordings for any
        /// orbit segment or surface position matching the chain's vessel PID.
        /// </summary>
        private void PositionChainGhostFallback(GameObject ghostGO, GhostChain chain, double currentUT)
        {
            bool positioned = false;
            var committed = RecordingStore.CommittedRecordings;
            var committedTrees = RecordingStore.CommittedTrees;
            if (committed != null)
            {
                var fallbackRec = FindPositionFallbackRecordingForChain(
                    committed, committedTrees, chain, currentUT);
                positioned = TryPositionChainFallbackFromRecording(
                    ghostGO, chain, fallbackRec, currentUT);
            }

            if (!positioned)
            {
                ParsekLog.VerboseRateLimited("Flight",
                    "chain-ghost-no-data-" + chain.OriginalVesselPid,
                    string.Format(CultureInfo.InvariantCulture,
                        "Chain ghost has no positioning data: pid={0} UT={1:F1}",
                        chain.OriginalVesselPid, currentUT));
            }
        }

        /// <summary>
        /// Check if the chain ghost's orbit segment changed and update the ghost map ProtoVessel.
        /// Called each frame after positioning a chain ghost to keep the map orbit line in sync.
        /// </summary>
        private static void UpdateChainGhostOrbitIfNeeded(
            GhostChain chain, List<OrbitSegment> segments, double currentUT)
        {
            if (segments == null || segments.Count == 0) return;

            OrbitSegment? seg = TrajectoryMath.FindOrbitSegment(segments, currentUT);
            if (!seg.HasValue) return;

            // Detect change: body, SMA, or eccentricity shifted (covers SOI transitions,
            // orbit changes, and inclination maneuvers at constant altitude).
            // Exact equality is intentional: segment values are stored doubles that don't drift.
            // A change means a different OrbitSegment was found, not floating-point accumulation.
            if (seg.Value.bodyName == chain.LastMapOrbitBodyName
                && seg.Value.semiMajorAxis == chain.LastMapOrbitSma
                && seg.Value.eccentricity == chain.LastMapOrbitEcc)
                return;

            chain.LastMapOrbitBodyName = seg.Value.bodyName;
            chain.LastMapOrbitSma = seg.Value.semiMajorAxis;
            chain.LastMapOrbitEcc = seg.Value.eccentricity;

            GhostMapPresence.UpdateGhostOrbit(chain.OriginalVesselPid, seg.Value);
        }

        /// <summary>
        /// Routes spawn through VesselGhoster for chain tips, or existing
        /// SpawnOrRecoverIfTooClose for normal recordings.
        /// Handles collision-blocked chains: if spawn is blocked, chain stays active.
        /// If chain was previously blocked, tries to spawn at propagated position.
        /// Called by ParsekPlaybackPolicy when engine fires PlaybackCompleted with needsSpawn.
        /// </summary>
        internal void SpawnVesselOrChainTipFromPolicy(Recording rec, int index)
        {
            SpawnVesselOrChainTip(rec, index);
        }

        /// <summary>
        /// Remove the ghost map vessel for a chain on spawn and transfer nav target
        /// to the newly spawned real vessel if the ghost was the current target.
        /// </summary>
        private void ResolveChainGhostOnSpawn(GhostChain chain, uint spawnedPid)
        {
            bool wasTarget = GhostMapPresence.RemoveGhostVessel(
                chain.OriginalVesselPid, "chain-tip-spawn");
            if (wasTarget)
            {
                Vessel spawned = FlightRecorder.FindVesselByPid(spawnedPid);
                if (spawned != null)
                    FlightGlobals.fetch?.SetVesselTarget(spawned);
            }
        }

        /// <summary>Called by policy when a mid-chain segment ends while watched.</summary>
        internal int FindNextWatchTargetFromPolicy(int currentIndex, Recording currentRec) =>
            watchMode.FindNextWatchTarget(currentIndex, currentRec);

        /// <summary>Called by policy to transfer watch to the next chain segment.</summary>
        internal void TransferWatchToNextSegmentFromPolicy(int nextIndex) =>
            watchMode.TransferWatchToNextSegment(nextIndex);

        /// <summary>Called by policy to exit watch mode.</summary>
        internal void ExitWatchModeFromPolicy() => watchMode.ExitWatchModePreservingLineage();

        internal void ExitWatchModePreservingLineage(bool skipCameraRestore = false) =>
            watchMode.ExitWatchModePreservingLineage(skipCameraRestore);

        /// <summary>Called by policy to switch camera to a spawned vessel after watch-mode spawn.</summary>
        internal void DeferredActivateVesselFromPolicy(uint vesselPid) =>
            StartCoroutine(DeferredActivateVessel(vesselPid));

        /// <summary>Called by policy to start the watch-mode hold timer at recording end.</summary>
        internal void StartWatchHoldFromPolicy(
            float holdUntilRealTime,
            double pendingActivationUT = double.NaN,
            float holdMaxRealTime = -1f) =>
            watchMode.StartWatchHold(holdUntilRealTime, pendingActivationUT, holdMaxRealTime);

        private static float GetCurrentWarpRateSafe()
        {
            try
            {
                return TimeWarp.CurrentRate;
            }
            catch (System.Security.SecurityException)
            {
                // Unit-test host does not provide the live KSP warp singleton.
                return 1f;
            }
            catch (MethodAccessException)
            {
                return 1f;
            }
        }

        private void SpawnVesselOrChainTip(Recording rec, int index)
        {
            // Existing source-vessel adoption is enforced by VesselSpawner. The #226
            // replay/revert path remains an explicit duplicate-spawn exception for the
            // scene-entry/current active vessel only.
            bool allowExistingSourceDuplicate =
                VesselSpawner.ShouldAllowExistingSourceDuplicateForCurrentFlight(
                    rec.VesselPersistentId);

            GhostChain chain = FindChainTipForRecording(activeGhostChains, rec);
            if (chain != null && vesselGhoster != null)
            {
                if (chain.SpawnBlocked)
                {
                    // Chain was previously blocked — retry at propagated position
                    double currentUT = Planetarium.GetUniversalTime();
                    uint spawnedPid = vesselGhoster.TrySpawnBlockedChain(
                        chain,
                        currentUT,
                        allowExistingSourceDuplicate);
                    if (spawnedPid != 0)
                    {
                        rec.SpawnedVesselPersistentId = spawnedPid;
                        rec.VesselSpawned = true;
                        activeGhostChains.Remove(chain.OriginalVesselPid);
                        ResolveChainGhostOnSpawn(chain, spawnedPid);

                        ParsekLog.Info("Flight",
                            $"Blocked chain tip spawn resolved: #{index} \"{rec.VesselName}\" pid={spawnedPid}");
                    }
                    // If still blocked: chain stays in activeGhostChains, no spawn
                }
                else
                {
                    uint spawnedPid = vesselGhoster.SpawnAtChainTip(
                        chain,
                        allowExistingSourceDuplicate);
                    if (spawnedPid != 0)
                    {
                        rec.SpawnedVesselPersistentId = spawnedPid;
                        rec.VesselSpawned = true;
                        activeGhostChains.Remove(chain.OriginalVesselPid);
                        ResolveChainGhostOnSpawn(chain, spawnedPid);

                        ParsekLog.Info("Flight",
                            $"Chain tip spawn complete: #{index} \"{rec.VesselName}\" pid={spawnedPid}");
                    }
                    else if (chain.SpawnBlocked)
                    {
                        // SpawnAtChainTip detected overlap — chain stays active, not removed
                        ParsekLog.Info("Flight",
                            $"Chain tip spawn blocked by collision: #{index} \"{rec.VesselName}\" — chain stays active");
                    }
                    else
                    {
                        ParsekLog.Warn("Flight",
                            $"Chain tip spawn failed: #{index} \"{rec.VesselName}\"");
                    }
                }
            }
            else
            {
                VesselSpawner.SpawnOrRecoverIfTooClose(
                    rec,
                    index,
                    preserveIdentity: false,
                    allowExistingSourceDuplicate: allowExistingSourceDuplicate);
            }
        }

        internal static GhostPlaybackSkipReason ResolveGhostPlaybackSkipReason(
            bool hasRenderableData,
            bool playbackEnabled,
            bool externalVesselSuppressed)
        {
            if (!hasRenderableData)
                return GhostPlaybackSkipReason.NoRenderableData;
            if (!playbackEnabled)
                return GhostPlaybackSkipReason.PlaybackDisabled;
            if (externalVesselSuppressed)
                return GhostPlaybackSkipReason.ExternalVesselSuppressed;
            return GhostPlaybackSkipReason.None;
        }

        private static string BuildGhostSkipReasonIdentity(int index, string recordingId)
        {
            return !string.IsNullOrEmpty(recordingId)
                ? recordingId
                : "idx-" + index.ToString(CultureInfo.InvariantCulture);
        }

        internal static string BuildGhostSkipReasonMessage(
            int index,
            string recordingId,
            string vesselName,
            GhostPlaybackSkipReason reason,
            bool hasRenderableData,
            bool playbackEnabled,
            bool externalVesselSuppressed)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Ghost playback skip state: #{0} id={1} vessel=\"{2}\" skip={3} reason={4} " +
                "hasRenderableData={5} playbackEnabled={6} externalVesselSuppressed={7}",
                index,
                string.IsNullOrEmpty(recordingId) ? "(none)" : recordingId,
                vesselName ?? "?",
                reason != GhostPlaybackSkipReason.None,
                reason.ToLogToken(),
                hasRenderableData,
                playbackEnabled,
                externalVesselSuppressed);
        }

        private static void LogGhostSkipReasonChangeCore(
            int index,
            string recordingId,
            string vesselName,
            GhostPlaybackSkipReason reason,
            bool hasRenderableData,
            bool playbackEnabled,
            bool externalVesselSuppressed)
        {
            string identity = "ghost-skip|" + BuildGhostSkipReasonIdentity(index, recordingId);
            ParsekLog.VerboseOnChange(
                "Flight",
                identity,
                reason.ToLogToken(),
                BuildGhostSkipReasonMessage(
                    index,
                    recordingId,
                    vesselName,
                    reason,
                    hasRenderableData,
                    playbackEnabled,
                    externalVesselSuppressed));
        }

        private void ClearGhostSkipReasonLogState()
        {
            activeGhostSkipReasonLogIdentities.Clear();
            ParsekLog.ClearVerboseOnChangeIdentitiesWithPrefix("Flight", "ghost-skip|");
        }

        internal void ClearGhostSkipReasonLogStateForTesting()
        {
            ClearGhostSkipReasonLogState();
        }

        internal static void LogGhostSkipReasonChangeForTesting(
            int index,
            string recordingId,
            string vesselName,
            GhostPlaybackSkipReason reason,
            bool hasRenderableData,
            bool playbackEnabled,
            bool externalVesselSuppressed)
        {
            LogGhostSkipReasonChangeCore(
                index,
                recordingId,
                vesselName,
                reason,
                hasRenderableData,
                playbackEnabled,
                externalVesselSuppressed);
        }

        private void LogGhostSkipReasonChangeIfNeeded(
            int index,
            Recording rec,
            GhostPlaybackSkipReason reason,
            bool hasRenderableData,
            bool externalVesselSuppressed)
        {
            string identity = BuildGhostSkipReasonIdentity(index, rec?.RecordingId);
            bool hasLoggedActiveSkip = activeGhostSkipReasonLogIdentities.Contains(identity);
            if (reason == GhostPlaybackSkipReason.None && !hasLoggedActiveSkip)
                return;

            LogGhostSkipReasonChangeCore(
                index,
                rec?.RecordingId,
                rec?.VesselName,
                reason,
                hasRenderableData,
                rec?.PlaybackEnabled ?? false,
                externalVesselSuppressed);

            if (reason == GhostPlaybackSkipReason.None)
                activeGhostSkipReasonLogIdentities.Remove(identity);
            else
                activeGhostSkipReasonLogIdentities.Add(identity);
        }

        internal struct FastForwardWatchHandoffDecision
        {
            public bool canEnterWatch;
            public bool warn;
            public int index;
            public bool recordingExists;
            public bool playbackEnabled;
            public bool activeGhost;
            public GhostPlaybackSkipReason skipReason;
            public string reason;
            public string recordingId;
            public string vesselName;
        }

        internal static FastForwardWatchHandoffDecision ClassifyFastForwardWatchHandoff(
            string pendingRecordingId,
            IReadOnlyList<Recording> committed,
            TrajectoryPlaybackFlags[] flags,
            Func<int, bool> hasActiveGhost)
        {
            var stale = new FastForwardWatchHandoffDecision
            {
                canEnterWatch = false,
                warn = false,
                index = -1,
                recordingExists = false,
                playbackEnabled = false,
                activeGhost = false,
                skipReason = GhostPlaybackSkipReason.None,
                reason = "stale-recording-id",
                recordingId = pendingRecordingId,
                vesselName = "?"
            };

            if (string.IsNullOrEmpty(pendingRecordingId) || committed == null)
                return stale;

            for (int i = 0; i < committed.Count; i++)
            {
                Recording rec = committed[i];
                if (rec == null || rec.RecordingId != pendingRecordingId)
                    continue;

                bool activeGhost = hasActiveGhost != null && hasActiveGhost(i);
                GhostPlaybackSkipReason skipReason = flags != null && i < flags.Length
                    ? flags[i].skipReason
                    : ResolveGhostPlaybackSkipReason(
                        GhostPlaybackEngine.HasRenderableGhostData(rec),
                        rec.PlaybackEnabled,
                        externalVesselSuppressed: false);
                string reason = activeGhost
                    ? "active-ghost"
                    : (skipReason != GhostPlaybackSkipReason.None
                        ? skipReason.ToLogToken()
                        : "active-ghost-missing");
                return new FastForwardWatchHandoffDecision
                {
                    canEnterWatch = activeGhost,
                    warn = !activeGhost,
                    index = i,
                    recordingExists = true,
                    playbackEnabled = rec.PlaybackEnabled,
                    activeGhost = activeGhost,
                    skipReason = skipReason,
                    reason = reason,
                    recordingId = rec.RecordingId,
                    vesselName = rec.VesselName
                };
            }

            return stale;
        }

        internal static string BuildFastForwardWatchHandoffMessage(
            FastForwardWatchHandoffDecision decision,
            double currentUT)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Deferred FF watch handoff {0}: id={1} index={2} vessel=\"{3}\" " +
                "committedExists={4} playbackEnabled={5} currentUT={6:F1} activeGhost={7} " +
                "skipReason={8} reason={9}",
                decision.canEnterWatch ? "ready" : "failed",
                string.IsNullOrEmpty(decision.recordingId) ? "(none)" : decision.recordingId,
                decision.index,
                decision.vesselName ?? "?",
                decision.recordingExists,
                decision.playbackEnabled,
                currentUT,
                decision.activeGhost,
                decision.skipReason.ToLogToken(),
                decision.reason ?? "(none)");
        }

        private static void LogFastForwardWatchHandoff(
            FastForwardWatchHandoffDecision decision,
            double currentUT)
        {
            string message = BuildFastForwardWatchHandoffMessage(decision, currentUT);
            if (decision.warn)
                ParsekLog.Warn("CameraFollow", message);
            else
                ParsekLog.Verbose("CameraFollow", message);
        }

        /// <summary>
        /// Pre-computes policy flags for all committed recordings. Called once per frame
        /// before the engine's update loop. Eliminates per-recording RecordingStore queries
        /// from the hot path (was O(n^2), now O(n)).
        /// </summary>
        private TrajectoryPlaybackFlags[] ComputePlaybackFlags(
            IReadOnlyList<Recording> committed, double currentUT)
        {
            var flags = new TrajectoryPlaybackFlags[committed.Count];
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                bool hasData = GhostPlaybackEngine.HasRenderableGhostData(rec);

                bool isActiveChain = chainManager.ActiveChainId != null && rec.ChainId == chainManager.ActiveChainId;
                bool chainLooping = rec.IsChainRecording &&
                    RecordingStore.IsChainLooping(rec.ChainId);

                bool externalVesselSuppressed = GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                    rec.TreeId, rec.VesselPersistentId, IsActiveTreeRecording(rec));
                GhostPlaybackSkipReason skipReason = ResolveGhostPlaybackSkipReason(
                    hasData,
                    rec.PlaybackEnabled,
                    externalVesselSuppressed);
                LogGhostSkipReasonChangeIfNeeded(
                    i,
                    rec,
                    skipReason,
                    hasData,
                    externalVesselSuppressed);

                var spawnResult = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                    rec, isActiveChain, chainLooping);

                var chainSuppressed = activeGhostChains != null
                    ? GhostPlaybackLogic.ShouldSuppressSpawnForChain(activeGhostChains, rec)
                    : (suppressed: false, reason: "");

                bool finalNeedsSpawn = spawnResult.needsSpawn && !chainSuppressed.suppressed;

                // Log spawn suppression reason for non-debris recordings (diagnostic).
                // Emit only when the suppression reason flips for this recording —
                // stable per-frame repeats (e.g., "no vessel snapshot" for the entire
                // session) are coalesced into the suppressed counter and surfaced
                // on the next reason change. Identity is keyed on the stable
                // RecordingId rather than the bare list index because the committed
                // list is dense — when a recording is discarded, later recordings
                // shift down and the same index gets reused for a different
                // recording. Keying on index alone would let the new occupant
                // inherit the prior recording's cached state and mask its first
                // emission (or surface a stale suppressed counter on the next
                // flip). The index still appears in the message body so audits
                // can resolve recordings post-hoc.
                if (!finalNeedsSpawn && !rec.IsDebris)
                {
                    string reason = !spawnResult.needsSpawn ? spawnResult.reason : chainSuppressed.reason;
                    string identity = "spawn-suppressed|"
                        + (!string.IsNullOrEmpty(rec.RecordingId) ? rec.RecordingId : "idx-" + i);
                    ParsekLog.VerboseOnChange(
                        "Spawner",
                        identity,
                        reason ?? "(none)",
                        $"Spawn suppressed for #{i} \"{rec.VesselName}\": {reason}");
                }

                flags[i] = new TrajectoryPlaybackFlags
                {
                    skipGhost = skipReason != GhostPlaybackSkipReason.None,
                    skipReason = skipReason,
                    skipReasonDetail = skipReason == GhostPlaybackSkipReason.None
                        ? null
                        : BuildGhostSkipReasonMessage(
                            i,
                            rec.RecordingId,
                            rec.VesselName,
                            skipReason,
                            hasData,
                            rec.PlaybackEnabled,
                            externalVesselSuppressed),
                    isMidChain = RecordingStore.IsChainMidSegment(rec),
                    chainEndUT = RecordingStore.GetChainEndUT(rec),
                    needsSpawn = finalNeedsSpawn,
                    isActiveChainMember = isActiveChain,
                    isChainLooping = chainLooping,
                    segmentLabel = RecordingStore.GetSegmentPhaseLabel(rec),
                    recordingId = rec.RecordingId,
                    vesselPersistentId = rec.VesselPersistentId,
                };
            }
            return flags;
        }

        /// <summary>
        /// Engine-based timeline playback. Builds FrameContext and TrajectoryPlaybackFlags,
        /// delegates per-frame ghost positioning to GhostPlaybackEngine, then applies
        /// resource deltas and watch-mode validity checks.
        /// </summary>
        void UpdateTimelinePlaybackViaEngine()
        {
            GhostPlaybackLogic.InvalidateVesselCache();
            if (GhostPlaybackLogic.ShouldSkipTimelinePlaybackForPendingReFlyInvoke(RewindInvokeContext.Pending))
            {
                ParsekLog.VerboseRateLimited("Flight", "timeline-playback-skip-refly-invoke",
                    "UpdateTimelinePlaybackViaEngine: skipped while re-fly post-load invocation is pending");
                return;
            }

            var committed = RecordingStore.CommittedRecordings;
            double currentUT = Planetarium.GetUniversalTime();

            // Chain ghost positioning (independent of engine)
            PositionChainGhosts(currentUT);

            // Pre-engine: detect spawned vessels that died since last frame (#132)
            policy.RunSpawnDeathChecks();

            // Flush deferred spawns from warp (policy owns the queue)
            policy.FlushDeferredSpawns();

            if (committed.Count == 0) return;

            // Pre-compute flags
            cachedFlags = ComputePlaybackFlags(committed, currentUT);
            var flags = cachedFlags;

            // Build frame context
            var ctx = new FrameContext
            {
                currentUT = currentUT,
                warpRate = TimeWarp.CurrentRate,
                warpRateIndex = TimeWarp.CurrentRateIndex,
                activeVesselPos = FlightGlobals.ActiveVessel != null
                    ? (Vector3d)FlightGlobals.ActiveVessel.transform.position
                    : Vector3d.zero,
                protectedIndex = watchMode.WatchedRecordingIndex,
                protectedLoopCycleIndex = watchMode.WatchedLoopCycleIndex,
                externalGhostCount = activeGhostChains?.Count ?? 0,
                mapViewEnabled = MapView.MapIsEnabled,
                autoLoopIntervalSeconds = ParsekSettings.Current?.autoLoopIntervalSeconds
                    ?? LoopTiming.DefaultLoopIntervalSeconds,
            };

            // Build trajectory list (Recording implements IPlaybackTrajectory)
            cachedTrajectories.Clear();
            for (int i = 0; i < committed.Count; i++)
                cachedTrajectories.Add(committed[i]);

            // Run engine rendering
            engine.UpdatePlayback(cachedTrajectories, flags, ctx);

            // Deferred watch after fast-forward: enter watch mode on the FF target
            // now that the engine has positioned ghosts for the new UT.
            if (pendingWatchAfterFFId != null)
            {
                string ffId = pendingWatchAfterFFId;
                pendingWatchAfterFFId = null;
                FastForwardWatchHandoffDecision handoff =
                    ClassifyFastForwardWatchHandoff(
                        ffId,
                        committed,
                        flags,
                        idx => watchMode.HasActiveGhost(idx));
                if (handoff.canEnterWatch)
                {
                    watchMode.EnterWatchMode(handoff.index);
                    if (watchMode.IsWatchingGhost && watchMode.WatchedRecordingIndex == handoff.index)
                    {
                        ParsekLog.Info("CameraFollow",
                            $"Deferred FF watch entered: #{handoff.index}");
                    }
                    else
                    {
                        handoff.canEnterWatch = false;
                        handoff.warn = true;
                        handoff.reason = "enter-watch-refused";
                        LogFastForwardWatchHandoff(handoff, currentUT);
                    }
                }
                else
                {
                    LogFastForwardWatchHandoff(handoff, currentUT);
                }
            }

            // Retry held ghost spawns (#96): ghosts held at final position while
            // waiting for a blocked/deferred spawn to resolve
            policy.RetryHeldGhostSpawns();

            // Create deferred ghost map ProtoVessels when ghosts enter orbital segments
            policy.CheckPendingMapVessels(Planetarium.GetUniversalTime());

            // Phase F: per-frame resource delta application removed.
            // The standalone applier (ApplyResourceDeltas, gated by ManagesOwnResources)
            // and the tree lump-sum applier (ApplyTreeResourceDeltas/ApplyTreeLumpSum)
            // were the dual source of truth alongside the ledger. The ledger is now
            // the single source of truth: GameActions/RecalculationEngine drives
            // Funding.Instance.Funds, ResearchAndDevelopment.Instance.Science, and
            // Reputation.Instance.reputation via RecalculateAndPatch.

            // Watch-mode ghost validity check
            watchMode.ValidateWatchedGhostStillActive();
        }

        // UpdateTimelinePlayback removed (T25 Phase 9 — engine is primary path)
        // ValidateWatchedGhostStillActive moved to WatchModeController

        // FlushDeferredSpawns moved to ParsekPlaybackPolicy (#132)

        private bool ShouldLoopPlayback(Recording rec) => GhostPlaybackEngine.ShouldLoopPlayback(rec);

        // IsActiveTreeRecording stays in ParsekFlight (reads RecordingStore.CommittedTrees)
        private bool IsActiveTreeRecording(Recording rec)
        {
            if (!rec.IsTreeRecording) return false;
            var trees = RecordingStore.CommittedTrees;
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i].Id == rec.TreeId)
                    return trees[i].ActiveRecordingId != null
                        && trees[i].ActiveRecordingId == rec.RecordingId;
            }
            return false;
        }

        private bool TryGetLoopSchedule(
            Recording rec,
            int recIdx,
            out double playbackStartUT,
            out double scheduleStartUT,
            out double duration,
            out double intervalSeconds)
        {
            playbackStartUT = 0.0;
            scheduleStartUT = 0.0;
            duration = 0.0;
            intervalSeconds = 0.0;
            if (rec == null || !GhostPlaybackEngine.ShouldLoopPlayback(rec))
                return false;

            playbackStartUT = GhostPlaybackEngine.EffectiveLoopStartUT(rec);
            duration = GhostPlaybackEngine.EffectiveLoopDuration(rec);
            if (duration <= LoopTiming.MinLoopDurationSeconds)
                return false;

            double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                    ?? LoopTiming.DefaultLoopIntervalSeconds;
            double baseIntervalSeconds = engine.GetLoopIntervalSeconds(rec, globalInterval);
            scheduleStartUT = playbackStartUT;
            intervalSeconds = baseIntervalSeconds;

            if (recIdx >= 0)
            {
                cachedTrajectories.Clear();
                var committed = RecordingStore.CommittedRecordings;
                for (int i = 0; i < committed.Count; i++)
                    cachedTrajectories.Add(committed[i]);

                if (GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule(
                        cachedTrajectories,
                        recIdx,
                        baseIntervalSeconds,
                        out var autoSchedule))
                {
                    scheduleStartUT = autoSchedule.LaunchStartUT;
                    intervalSeconds = autoSchedule.LaunchCadenceSeconds;
                }
            }

            return true;
        }

        private double GetLoopIntervalSeconds(Recording rec, int recIdx = -1)
        {
            if (!TryGetLoopSchedule(
                    rec, recIdx,
                    out _, out _, out _, out double intervalSeconds))
            {
                double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                        ?? LoopTiming.DefaultLoopIntervalSeconds;
                return engine.GetLoopIntervalSeconds(rec, globalInterval);
            }

            return intervalSeconds;
        }

        private bool TryComputeLoopPlaybackUT(
            Recording rec, double currentUT,
            out double loopUT, out long cycleIndex, out bool inPauseWindow,
            int recIdx = -1)
        {
            loopUT = 0.0;
            cycleIndex = 0;
            inPauseWindow = false;
            if (!TryGetLoopSchedule(
                    rec, recIdx,
                    out double playbackStartUT,
                    out double scheduleStartUT,
                    out double duration,
                    out double intervalSeconds))
                return false;

            double phaseOffset;
            if (recIdx >= 0 && engine.loopPhaseOffsets.TryGetValue(recIdx, out phaseOffset))
            {
                ParsekLog.Verbose("Engine", $"TryComputeLoopPlaybackUT: applying phase offset {phaseOffset:F2}s for recIdx={recIdx}");
                scheduleStartUT -= phaseOffset;
            }

            if (!GhostPlaybackLogic.TryComputeLoopPlaybackPhase(
                    currentUT, scheduleStartUT, duration, intervalSeconds,
                    out double playbackPhase, out cycleIndex, out inPauseWindow))
            {
                return false;
            }

            loopUT = playbackStartUT + playbackPhase;
            return true;
        }


        // UpdateLoopingTimelinePlayback removed (T25 Phase 9)

        /// <summary>
        /// Multi-cycle overlap path for negative intervals. Multiple ghosts from
        /// different cycles may be visible simultaneously.
        /// </summary>
        // UpdateOverlapLoopPlayback removed (T25 Phase 9)

        // Phase F: ApplyResourceDeltas, ApplyTreeResourceDeltas, ApplyTreeLumpSum
        // removed. The ledger (GameActions/RecalculationEngine) is the single source
        // of truth for funds/science/reputation; per-recording lump-sum replay and
        // tree-level snapshot reconciliation are no longer used.

        RecordingTree FindCommittedTree(string treeId)
        {
            var trees = RecordingStore.CommittedTrees;
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i].Id == treeId) return trees[i];
            }
            Log($"FindCommittedTree: treeId='{treeId}' not found (checked {trees.Count} committed trees)");
            return null;
        }

        /// <summary>
        /// Finds a committed recording by its RecordingId. Searches all committed recordings.
        /// Used for ghost map vessel creation (looking up chain tip recordings).
        /// </summary>
        private static Recording FindTipRecordingById(string recordingId)
        {
            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                if (committed[i].RecordingId == recordingId)
                    return committed[i];
            }
            return null;
        }

        internal void DestroyTimelineGhost(int index)
        {
            engine.DestroyGhost(index);
        }

        public void DestroyAllTimelineGhosts()
        {
            ExitWatchModeBeforeTimelineGhostCleanup("DestroyAllTimelineGhosts");

            // Remove ghost map ProtoVessels before engine cleanup
            GhostMapPresence.RemoveAllGhostVessels("rewind");

            // Engine destroys all ghost GOs and clears engine-owned state.
            // Fires OnAllGhostsDestroying so policy clears its own state.
            engine.DestroyAllGhosts();

            // Clear ParsekFlight-local state (positioning, proximity, diagnostics)
            orbitCache.Clear();
            loggedOrbitSegments.Clear();
            loggedOrbitRotationSegments.Clear();
            nearbySpawnCandidates.Clear();
            proximityVelocitySamples.Clear();
            notifiedSpawnRecordingIds.Clear();
            loggedRelativeStart.Clear();
            loggedAnchorNotFound.Clear();
        }

        /// <summary>
        /// Checks whether the recording ended with destruction and spawns an explosion FX if so.
        /// Pure decision logic is in ShouldTriggerExplosion; this method handles the side effects.
        /// </summary>

        /// <summary>
        /// Destroys all overlap ghosts for a recording index.
        /// Handles camera state reset if the watched recording's overlap tracking is affected.
        /// </summary>
        private void DestroyAllOverlapGhosts(int recIdx)
        {
            engine.DestroyAllOverlapGhosts(recIdx);

            // Camera state cleanup — engine doesn't know about watch mode
            watchMode.OnOverlapGhostsDestroyed(recIdx);
        }

        public bool CanDeleteRecording =>
            !IsRecording && !chainManager.IsTrackingContinuation && !chainManager.IsTrackingUndockContinuation;

        public void DeleteRecording(int index)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (index < 0 || index >= committed.Count)
            {
                ParsekLog.Warn("Flight", $"DeleteRecording ignored: index={index} out of range (count={committed.Count})");
                return;
            }

            if (!CanDeleteRecording)
            {
                ParsekLog.Warn("Flight",
                    $"DeleteRecording blocked: index={index}, isRecording={IsRecording}, " +
                    $"chainManager.ContinuationRecordingIdx={chainManager.ContinuationRecordingIdx}, chainManager.UndockContinuationRecIdx={chainManager.UndockContinuationRecIdx}");
                return;
            }

            var rec = committed[index];
            Log($"Deleting recording '{rec.VesselName}' at index {index}");

            // Unreserve crew
            CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);

            // Destroy ghost if active (primary + overlap)
            DestroyAllOverlapGhosts(index);
            engine.DestroyGhost(index);

            // Remove from store (handles chain degradation + file deletion)
            RecordingStore.RemoveRecordingAt(index);

            // Update watch mode index if watching a ghost
            watchMode.OnRecordingDeleted(index);

            // Reindex all engine-owned ghost state (dicts + sets)
            engine.ReindexAfterDelete(index);

            // Clear orbit cache — keys are index-derived (i * 10000 + segIdx),
            // so after reindexing they would map to wrong recordings
            orbitCache.Clear();

            // Clear map marker waypoint cache (index-keyed, stale after reindex)
            ui?.ClearMapMarkerCache();

            // Clear ParsekFlight-local diagnostic guards since indices shifted
            loggedOrbitSegments.Clear();
            loggedOrbitRotationSegments.Clear();
            policy.RemovePendingSpawn(rec.RecordingId);

            ParsekLog.ScreenMessage($"Recording '{rec.VesselName}' deleted", 2f);
        }

        /// <summary>
        /// Deletes a ghost-only recording without the CanDeleteRecording guard.
        /// Ghost-only recordings are independent of the auto-recording system,
        /// so they can safely be deleted even while auto-recording is active.
        /// </summary>
        internal void DeleteGhostOnlyRecording(int index)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (index < 0 || index >= committed.Count)
            {
                ParsekLog.Warn("Flight", $"DeleteGhostOnlyRecording: index={index} out of range");
                return;
            }

            var rec = committed[index];
            if (!rec.IsGhostOnly)
            {
                ParsekLog.Warn("Flight", $"DeleteGhostOnlyRecording: recording at index {index} is not ghost-only");
                return;
            }

            ParsekLog.Info("Flight", $"Deleting ghost-only recording '{rec.VesselName}' at index {index}");

            // Destroy ghost if active (primary + overlap)
            DestroyAllOverlapGhosts(index);
            engine.DestroyGhost(index);

            RecordingStore.RemoveRecordingAt(index);

            watchMode.OnRecordingDeleted(index);
            engine.ReindexAfterDelete(index);
            orbitCache.Clear();
            ui?.ClearMapMarkerCache();
            loggedOrbitSegments.Clear();
            loggedOrbitRotationSegments.Clear();
            policy.RemovePendingSpawn(rec.RecordingId);

            ParsekLog.ScreenMessage($"Ghost recording '{rec.VesselName}' deleted", 2f);
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
        /// promote a pad-local failure into merge UI.
        /// </summary>
        internal static bool IsIdleOnPad(Recording rec)
        {
            if (rec == null)
                return false;

            return IsIdleOnPad(rec.MaxDistanceFromLaunch)
                || HasPadLocalizedMotionOverride(rec);
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
        /// Returns true if every recording in the tree qualifies as a pad failure.
        /// A tree with any non-pad-failure recording is not discarded.
        /// </summary>
        internal static bool IsTreePadFailure(RecordingTree tree)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return false;

            foreach (var rec in tree.Recordings.Values)
            {
                if (!IsPadFailure(rec))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Returns true if every recording in the tree is idle-on-pad (never moved
        /// more than 30m from launch). A tree with any non-idle recording is not discarded.
        /// </summary>
        internal static bool IsTreeIdleOnPad(RecordingTree tree)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
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
            var old = new Dictionary<int, T>(dict);
            dict.Clear();
            foreach (var kvp in old)
            {
                if (kvp.Key < removedIndex)
                    dict[kvp.Key] = kvp.Value;
                else if (kvp.Key > removedIndex)
                    dict[kvp.Key - 1] = kvp.Value;
            }
        }


        // UpdateWatchCamera, ProcessWatchEndHoldTimer, FindWatchedGhostState,
        // TryResolveOverlapRetarget moved to WatchModeController

        bool IsAnyWarpActive() => GhostPlaybackEngine.IsAnyWarpActiveFromGlobals();

        void ExitAllWarpForPlaybackStart(string context)
        {
            if (!IsAnyWarpActive()) return;
            TimeWarp.SetRate(0, true);
            Log($"Warp reset for playback start ({context})");
        }

        #endregion

        // Camera Follow (Watch Mode) region moved to WatchModeController

        // Forwarding methods for external callers (RecordingsTableUI, GhostVesselLoadPatch, etc.)
        internal bool HasActiveGhost(int index) => watchMode.HasActiveGhost(index);
        internal bool IsGhostWithinVisualRange(int index) => watchMode.IsGhostWithinVisualRange(index);
        internal bool IsGhostOnSameBody(int index) => watchMode.IsGhostOnSameBody(index);
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
        internal static Vector3 ComputeSurfaceRelativeVelocity(
            Vector3 playbackVelocity, Vector3 rotatingFrameVelocity) =>
            WatchModeController.ComputeSurfaceRelativeVelocity(playbackVelocity, rotatingFrameVelocity);
        internal static (Vector3 forward, Vector3 horizonVelocity, Vector3 headingVelocity,
            HorizonForwardSource source) ComputeWatchHorizonForward(
                Vector3 up, Vector3 playbackVelocity, Vector3 rotatingFrameVelocity,
                Vector3 lastForward) =>
            WatchModeController.ComputeWatchHorizonForward(
                up, playbackVelocity, rotatingFrameVelocity, lastForward);
        internal static (Vector3 forward, Vector3 horizonVelocity, Vector3 headingVelocity,
            Vector3 appliedFrameVelocity, HorizonForwardSource source)
            ComputeWatchHorizonBasis(
                bool hasAtmosphere, double atmosphereDepth, double altitude,
                Vector3 up, Vector3 playbackVelocity, Vector3 rotatingFrameVelocity,
                Vector3 lastForward) =>
            WatchModeController.ComputeWatchHorizonBasis(
                hasAtmosphere, atmosphereDepth, altitude,
                up, playbackVelocity, rotatingFrameVelocity, lastForward);
        internal static Vector3 ComputeHorizonForward(Vector3 up, Vector3 velocity, Vector3 lastForward) =>
            WatchModeController.ComputeHorizonForward(up, velocity, lastForward);
        internal static (Quaternion rotation, Vector3 forward) ComputeHorizonRotation(
            Vector3 up, Vector3 velocity, Vector3 lastForward) =>
            WatchModeController.ComputeHorizonRotation(up, velocity, lastForward);
        internal static (float pitch, float hdg) CompensateCameraAngles(
            Quaternion oldTargetRot, Quaternion newTargetRot, float pitch, float hdg) =>
            WatchModeController.CompensateCameraAngles(oldTargetRot, newTargetRot, pitch, hdg);

        IEnumerator DeferredActivateVessel(uint pid)
        {
            // Wait up to 5 seconds for the vessel to appear and load.
            // Distant vessels (e.g. splashdown site 37km away) take much longer
            // than 10 frames to load — KSP must range-load them first.
            float deadline = UnityEngine.Time.time + 5f;
            while (UnityEngine.Time.time < deadline)
            {
                yield return null;
                Vessel v = FlightGlobals.Vessels?.Find(vessel => vessel.persistentId == pid
                    && !GhostMapPresence.IsGhostMapVessel(vessel.persistentId));
                if (v != null)
                {
                    // Only activate if within physics range. ForceSetActiveVessel on an
                    // unloaded vessel triggers a full FLIGHT→FLIGHT scene reload (black
                    // screen flash). Better to stay on the pad and let the player switch
                    // manually via tracking station or map view.
                    if (!v.loaded)
                    {
                        Log($"Spawned vessel pid={pid} not loaded — staying on current vessel");
                        yield break;
                    }
                    v.IgnoreGForces(240);
                    FlightGlobals.ForceSetActiveVessel(v);
                    Log($"Activated spawned vessel pid={pid} ({v.vesselName})");
                    yield break;
                }
            }
            Log($"WARNING: Could not find spawned vessel pid={pid} within 5 seconds");
        }

        #region Zone Rendering (shared by normal, background, and looped ghosts)

        /// <summary>
        // ZoneRenderingResult struct is now in IGhostPositioner.cs (namespace-level, shared with engine)

        /// <summary>
        /// Evaluates zone-based rendering for a ghost: computes distance, classifies zone,
        /// detects transitions, hides/shows mesh, exits watch mode if needed.
        /// Shared by normal, background, and looped ghost update paths.
        /// </summary>
        ZoneRenderingResult ApplyZoneRenderingImpl(int recIdx, GhostPlaybackState state, IPlaybackTrajectory rec,
            double renderDistance, int protectedIndex)
        {
            double activeVesselDistance = state != null ? state.lastDistance : renderDistance;
            var zone = RenderingZoneManager.ClassifyDistance(renderDistance);
            var (isWatchedGhost, _, isWatchProtectedRecording) =
                ResolveZoneWatchState(recIdx, state, protectedIndex);

            // Cache render distance separately from active-vessel safety distance.
            if (state != null)
                state.lastRenderDistance = renderDistance;

            // Detect zone transition
            if (state != null)
            {
                string transDesc;
                if (GhostPlaybackLogic.DetectZoneTransition(state.currentZone, zone, out transDesc))
                {
                    RenderingZoneManager.LogZoneTransition(
                        $"#{recIdx} \"{rec.VesselName}\"", state.currentZone, zone, renderDistance);
                    state.currentZone = zone;
                }
            }

            var (shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning) =
                GhostPlaybackLogic.GetZoneRenderingPolicy(zone);

            // Ghost camera cutoff: exit watch mode when the watched ghost reaches or exceeds
            // the fixed watch cutoff distance. The cutoff applies uniformly to all ghosts.
            bool forceWatchedFullFidelity = false;
            if (isWatchedGhost || isWatchProtectedRecording)
            {
                float cutoffKm = DistanceThresholds.GhostFlight.GetWatchCameraCutoffKm();
                if (isWatchedGhost && GhostPlaybackLogic.ShouldExitWatchForCutoff(activeVesselDistance, cutoffKm))
                {
                    ParsekLog.Info("Zone",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" exceeded ghost camera cutoff " +
                        $"({activeVesselDistance.ToString("F0", CultureInfo.InvariantCulture)}m from active vessel >= " +
                        $"{(cutoffKm * 1000.0).ToString("F0", CultureInfo.InvariantCulture)}m; " +
                        $"render={renderDistance.ToString("F0", CultureInfo.InvariantCulture)}m) — exiting watch mode");
                    ExitWatchModePreservingLineage();
                    // Don't return — let zone rendering continue (ghost will be hidden if Beyond)
                }
                else
                {
                    forceWatchedFullFidelity = isWatchProtectedRecording || GhostPlaybackLogic.ShouldForceWatchedFullFidelity(
                        isWatchedGhost, activeVesselDistance, cutoffKm);
                    (shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning) =
                        GhostPlaybackLogic.ApplyWatchedFullFidelityOverride(
                            shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning,
                            forceWatchedFullFidelity);

                    if (forceWatchedFullFidelity)
                    {
                        GhostPlaybackLogic.RestoreWatchedFullFidelityState(state);
                        if (ShouldAutoActivateGhost(state)
                            && state != null && state.ghost != null && !state.ghost.activeSelf)
                            state.ghost.SetActive(true);
                        if (zone == RenderingZone.Beyond)
                        {
                            string reason = isWatchedGhost
                                ? (rec.HasOrbitSegments ? "watched orbital ghost" : "watched")
                                : "watch-protected debris";
                            ParsekLog.VerboseRateLimited("Zone", $"watch-protected-zone-exempt-{recIdx}",
                                $"Ghost #{recIdx} \"{rec.VesselName}\" beyond visual range " +
                                $"({renderDistance.ToString("F0", CultureInfo.InvariantCulture)}m render distance) but {reason} — exempt from full-fidelity LOD suppression",
                                5.0);
                        }
                    }
                }
            }

            var (distanceHideMesh, distanceSkipPartEvents, distanceSkipPositioning,
                distanceSuppressVisualFx, distanceReduceFidelity) =
                GhostPlaybackLogic.ApplyDistanceLodPolicy(
                    shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning,
                    renderDistance, forceWatchedFullFidelity);
            shouldHideMesh = distanceHideMesh;
            shouldSkipPartEvents = distanceSkipPartEvents;
            shouldSkipPositioning = distanceSkipPositioning;

            // #171: During warp, exempt orbital ghosts from zone hiding — they travel far
            // from the player and would complete playback while invisible.
            if (GhostPlaybackLogic.ShouldApplyWarpZoneHideExemption(
                    shouldHideMesh, zone, GetCurrentWarpRateSafe(), rec.HasOrbitSegments))
            {
                shouldHideMesh = false;
                ParsekLog.VerboseRateLimited("Zone", $"warp-zone-exempt-{recIdx}",
                    $"Ghost #{recIdx} \"{rec.VesselName}\" beyond visual range " +
                    $"({renderDistance.ToString("F0", CultureInfo.InvariantCulture)}m render distance) " +
                    $"but exempt during warp (orbital ghost)", 5.0);
            }

            if (shouldHideMesh)
            {
                if (state != null && state.ghost != null && state.ghost.activeSelf)
                {
                    state.ghost.SetActive(false);
                    ParsekLog.VerboseRateLimited("Zone", "zone-hide",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" hidden by distance LOD " +
                        $"({renderDistance.ToString("F0", CultureInfo.InvariantCulture)}m render distance)");
                }
                return new ZoneRenderingResult
                {
                    hiddenByZone = true,
                    skipPartEvents = true,
                    suppressVisualFx = true,
                    reduceFidelity = false
                };
            }

            // Zone 1 or 2: ensure ghost is visible
            if (ShouldAutoActivateGhost(state)
                && state != null && state.ghost != null && !state.ghost.activeSelf)
            {
                state.ghost.SetActive(true);
                ParsekLog.VerboseRateLimited("Zone", "zone-show",
                    $"Ghost #{recIdx} \"{rec.VesselName}\" re-shown: entered visible distance tier " +
                    $"({renderDistance.ToString("F0", CultureInfo.InvariantCulture)}m render distance)");
            }

            return new ZoneRenderingResult
            {
                hiddenByZone = false,
                skipPartEvents = shouldSkipPartEvents,
                suppressVisualFx = distanceSuppressVisualFx,
                reduceFidelity = distanceReduceFidelity
            };
        }

        /// <summary>
        /// Contract for the `allowActivation` parameter threaded through
        /// IGhostPositioner / ParsekFlight positioning methods (bug #258 / #375):
        ///
        /// - `true` = positioner may call <c>ghost.SetActive(true)</c> after
        ///   world-space placement. Default for routine playback frames.
        /// - `false` = positioner leaves visibility untouched. Only use when a
        ///   downstream step (e.g. <c>ActivateGhostVisualsIfNeeded</c> during loop
        ///   sync or deferred-FF handoff) owns the SetActive decision and must
        ///   run AFTER world-space positioning so the first visible frame is
        ///   already aligned with playback.
        ///
        /// Every new positioning path must decide explicitly. Forgetting to thread
        /// the flag (and defaulting to <c>true</c> blind) can re-introduce the
        /// "first-visible-frame shows stale initial pose" regression that #258
        /// fixed. <c>ShouldAutoActivateGhost</c> is the canonical source for the
        /// value.
        /// </summary>
        private static bool ShouldAutoActivateGhost(GhostPlaybackState state)
        {
            return state == null || !state.deferVisibilityUntilPlaybackSync;
        }

        internal (bool isWatchedGhost, int watchProtectionIndex, bool isWatchProtectedRecording)
            ResolveZoneWatchState(int recIdx, GhostPlaybackState state, int protectedIndex)
        {
            bool isWatchedGhost = watchMode != null
                ? watchMode.IsWatchedGhostState(recIdx, state)
                : GhostPlaybackLogic.IsProtectedGhost(protectedIndex, recIdx);
            int watchProtectionIndex = watchMode != null
                ? watchMode.WatchProtectionRecordingIndex
                : protectedIndex;
            bool isWatchProtectedRecording = GhostPlaybackLogic.IsWatchProtectedRecording(
                RecordingStore.CommittedRecordings, RecordingStore.CommittedTrees, watchProtectionIndex, recIdx);
            return (isWatchedGhost, watchProtectionIndex, isWatchProtectedRecording);
        }

        #endregion

        #region IGhostPositioner implementation

        void IGhostPositioner.InterpolateAndPosition(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut, bool suppressFx)
        {
            if (state?.ghost == null || traj?.Points == null) return;
            int playbackIdx = state.playbackIndex;
            InterpolationResult interpResult;

            if (TryInterpolateAndPositionCheckpointSection(
                index,
                traj,
                state.ghost,
                ref playbackIdx,
                ut,
                ShouldAutoActivateGhost(state),
                out interpResult))
            {
                state.SetInterpolated(interpResult);
                state.playbackIndex = playbackIdx;
                return;
            }

            bool surfaceSkip = TrajectoryMath.IsSurfaceAtUT(traj.TrackSections, ut);
            InterpolateAndPosition(state.ghost, traj.Points, traj.OrbitSegments,
                ref playbackIdx, ut, index * 10000, out interpResult,
                allowActivation: ShouldAutoActivateGhost(state), skipOrbitSegments: surfaceSkip);
            state.SetInterpolated(interpResult);
            state.playbackIndex = playbackIdx;
        }

        void IGhostPositioner.InterpolateAndPositionRelative(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut, bool suppressFx, uint anchorVesselId)
        {
            if (state?.ghost == null || traj?.Points == null) return;
            // Find the relative TrackSection to get section-local frames
            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, ut);
            var sectionFrames = (sectionIdx >= 0 && traj.TrackSections[sectionIdx].frames != null)
                ? traj.TrackSections[sectionIdx].frames
                : traj.Points;
            long relKey = ((long)index << 32) | anchorVesselId;
            if (loggedRelativeStart.Add(relKey))
            {
                string sectionUt = sectionIdx >= 0
                    ? $"[{traj.TrackSections[sectionIdx].startUT:F1},{traj.TrackSections[sectionIdx].endUT:F1}]"
                    : "[fallback]";
                ParsekLog.Info("Playback",
                    $"RELATIVE playback started: recording #{index} \"{traj.VesselName}\" " +
                    $"anchorPid={anchorVesselId} contract={RecordingStore.DescribeRelativeFrameContract(traj.RecordingFormatVersion)} " +
                    $"version={traj.RecordingFormatVersion} sectionUT={sectionUt}");
            }

            int playbackIdx = state.playbackIndex;
            InterpolationResult interpResult;
            InterpolateAndPositionRelative(state.ghost, sectionFrames, ref playbackIdx,
                ut, anchorVesselId, traj.RecordingFormatVersion,
                ShouldAutoActivateGhost(state), out interpResult);
            state.SetInterpolated(interpResult);
            state.playbackIndex = playbackIdx;
        }

        void IGhostPositioner.PositionAtPoint(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, TrajectoryPoint point)
        {
            if (state?.ghost == null) return;

            // Bug #282: for Landed/Splashed recordings, clamp the rendered
            // altitude up so the ghost mesh sits above terrain. The recording's
            // raw altitude is the root-part origin — for a Mk1-3 pod the lowest
            // mesh vertex is ~1.77 m below the root, so any recorded clearance
            // under ~2 m buries the pod. Applies only to the PositionAtPoint
            // call sites (past-end hold, loop cycle boundary, loop pause hold,
            // overlap expired): these are the "stuck at final point" paths.
            // Normal in-flight playback already gets the distance-aware terrain
            // floor from ClampGhostsToTerrain in LateUpdate; mirror that same
            // minimum here so long-range watched landed ghosts do not sink below
            // the visual terrain right as playback reaches its final point.
            TrajectoryPoint positioned = point;
            if (traj != null)
            {
                var term = traj.TerminalStateValue;
                if (term == TerminalState.Landed || term == TerminalState.Splashed)
                {
                    positioned = ApplyLandedGhostClearance(
                        point, index, traj.VesselName, traj.TerrainHeightAtEnd,
                        ResolveImmediateLandedGhostClearanceMeters(
                            index, traj.VesselName,
                            point.bodyName, point.latitude, point.longitude, point.altitude));
                }
            }

            PositionGhostAt(state.ghost, positioned);

            // Keep the watch-mode overlay + diagnostics consistent with the
            // clamped position. WatchModeController reads state.lastInterpolatedAltitude
            // for its "ghost at alt N m on Kerbin" line — if we don't update it,
            // the log reports the buried raw altitude even after the visual is fixed.
            state.SetInterpolated(new InterpolationResult(
                positioned.velocity,
                positioned.bodyName,
                positioned.altitude));
        }

        bool IGhostPositioner.TryResolveExplosionAnchorPosition(int index,
            IPlaybackTrajectory traj, GhostPlaybackState state, out Vector3 worldPosition)
        {
            if (state?.ghost == null)
            {
                worldPosition = Vector3.zero;
                string missingGhostVesselName = traj != null && !string.IsNullOrEmpty(traj.VesselName)
                    ? traj.VesselName
                    : state?.vesselName ?? "?";
                ParsekLog.Warn("TerrainCorrect",
                    $"Explosion anchor resolve #{index} (\"{missingGhostVesselName}\") skipped: ghost root is null");
                return false;
            }

            Vector3 rawWorldPos = state.ghost.transform.position;
            worldPosition = rawWorldPos;
            string safeVesselName = traj != null && !string.IsNullOrEmpty(traj.VesselName)
                ? traj.VesselName
                : state.vesselName ?? "?";
            string bodyName = ResolveExplosionAnchorBodyName(
                state.lastInterpolatedBodyName, traj);
            if (string.IsNullOrEmpty(bodyName))
                return true;

            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
            if (body == null || body.pqsController == null)
                return true;

            double latitude = body.GetLatitude(rawWorldPos);
            double longitude = body.GetLongitude(rawWorldPos);
            double rawAltitude = body.GetAltitude(rawWorldPos);
            double terrainHeight = body.TerrainAltitude(latitude, longitude, true);

            double minClearance = ImmediateLandedGhostClearanceFallbackMeters;
            if (FlightGlobals.ActiveVessel != null)
            {
                double distanceToVessel = Vector3d.Distance(
                    rawWorldPos, FlightGlobals.ActiveVessel.GetWorldPos3D());
                minClearance = ComputeTerrainClearance(distanceToVessel);
            }
            else
            {
                ParsekLog.Verbose("TerrainCorrect",
                    $"Explosion anchor clearance fallback #{index} (\"{safeVesselName}\"): " +
                    $"active vessel unavailable — using legacy floor " +
                    $"{ImmediateLandedGhostClearanceFallbackMeters.ToString("F1", CultureInfo.InvariantCulture)}m");
            }

            double clampedAltitude = TerrainCorrector.ClampAltitude(
                rawAltitude, terrainHeight, minClearance);
            double resolvedAltitude = clampedAltitude > rawAltitude ? clampedAltitude : rawAltitude;
            worldPosition = clampedAltitude > rawAltitude
                ? body.GetWorldSurfacePosition(latitude, longitude, clampedAltitude)
                : rawWorldPos;

            // Keep the watch overlay / diagnostics aligned with the anchor we
            // actually returned, not just the "had to clamp" branch.
            state.lastInterpolatedBodyName = body.name;
            state.lastInterpolatedAltitude = resolvedAltitude;

            if (clampedAltitude <= rawAltitude)
                return true;

            string cycleText = state.loopCycleIndex >= 0
                ? $" cycle={state.loopCycleIndex}"
                : "";
            ParsekLog.Verbose("TerrainCorrect",
                $"Explosion anchor clamp #{index} (\"{safeVesselName}\"): " +
                $"alt={rawAltitude.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"terrain={terrainHeight.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"-> {clampedAltitude.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"(clearance={minClearance.ToString("F1", CultureInfo.InvariantCulture)}m){cycleText}");
            return true;
        }

        // Literal floor returned by the immediate-clearance resolver when it
        // cannot compute a distance-aware value. Matches the legacy pre-#262
        // hardcoded floor — callers that hit this branch silently regress to
        // the old behavior, so ResolveImmediateLandedGhostClearanceMeters
        // emits a rate-limited warning naming the offending ghost (#373).
        internal const double ImmediateLandedGhostClearanceFallbackMeters = 0.5;

        /// <summary>
        /// Pure decision helper: given whether a <see cref="CelestialBody"/>
        /// resolved for the ghost's body name and whether an active vessel is
        /// present, returns the reason the fallback fires (for logging), or
        /// <c>null</c> when both inputs are live and the distance-aware path
        /// can run. Extracted for unit testing without KSP singletons (#373).
        /// </summary>
        internal static string ResolveImmediateLandedGhostClearanceFallbackReason(
            bool hasBody, bool hasActiveVessel)
        {
            if (!hasBody && !hasActiveVessel) return "no-body-and-no-active-vessel";
            if (!hasBody) return "no-body";
            if (!hasActiveVessel) return "no-active-vessel";
            return null;
        }

        internal static string ResolveExplosionAnchorBodyName(
            string lastInterpolatedBodyName, IPlaybackTrajectory traj)
        {
            if (!string.IsNullOrEmpty(lastInterpolatedBodyName))
                return lastInterpolatedBodyName;
            return RecordingEndpointResolver.TryGetPreferredEndpointBodyName(
                traj, out string bodyName)
                ? bodyName
                : null;
        }

        private static double ResolveImmediateLandedGhostClearanceMeters(
            int index, string vesselName,
            string bodyName, double latitude, double longitude, double altitude)
        {
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
            bool hasBody = body != null;
            bool hasActiveVessel = FlightGlobals.ActiveVessel != null;
            string fallbackReason = ResolveImmediateLandedGhostClearanceFallbackReason(
                hasBody, hasActiveVessel);
            if (fallbackReason != null)
            {
                // #373: Silent regression to the legacy 0.5 m floor when the
                // distance-aware resolver cannot run. Rate-limit per
                // ghost+reason so scene-transition cold-start paths surface
                // without spamming the log on every frame.
                string safeVessel = string.IsNullOrEmpty(vesselName) ? "?" : vesselName;
                string safeBody = string.IsNullOrEmpty(bodyName) ? "?" : bodyName;
                ParsekLog.WarnRateLimited("TerrainCorrect",
                    $"landed-ghost-clearance-fallback-{index}-{fallbackReason}",
                    $"Landed ghost clearance fallback #{index} (\"{safeVessel}\") on body='{safeBody}': " +
                    $"{fallbackReason} — using legacy floor " +
                    $"{ImmediateLandedGhostClearanceFallbackMeters.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}m " +
                    "(distance-aware #262 path skipped)");
                return ImmediateLandedGhostClearanceFallbackMeters;
            }

            Vector3d pointWorld = body.GetWorldSurfacePosition(latitude, longitude, altitude);
            double distToVessel = Vector3d.Distance(pointWorld, FlightGlobals.ActiveVessel.GetWorldPos3D());
            return ComputeTerrainClearance(distToVessel);
        }

        internal static double ResolveNaNFallbackLandedGhostClearanceMeters(double minClearanceMeters)
        {
            return System.Math.Max(
                VesselSpawner.LandedGhostClearanceMeters,
                System.Math.Max(0.5, minClearanceMeters));
        }

        internal static bool ShouldApplyImmediateSurfacePositionClearance(double recordedTerrainHeight)
        {
            return !double.IsNaN(recordedTerrainHeight);
        }

        /// <summary>
        /// Position a Landed/Splashed ghost. The recorded altitude is the TRUTH —
        /// the vessel was literally at that world-space position when the terminal
        /// state was determined. For vessels recorded on mesh objects (Island
        /// Airfield, launchpad, KSC buildings), the altitude encodes the airfield
        /// surface height, which <c>body.TerrainAltitude()</c> cannot see (PQS-only).
        /// The old code applied terrain-relative correction via
        /// <c>ComputeCorrectedAltitude</c> which pulled mesh-object ghosts down to
        /// raw PQS terrain, burying them under the runway.
        ///
        /// <para>New behavior: preserve the recorded altitude. The only correction
        /// is an underground safety floor — push up if the recorded altitude is
        /// below the current PQS terrain floor. The caller supplies the minimum
        /// clearance to use for the current playback context; long-range watched
        /// ghosts use the same distance-aware floor as <c>ClampGhostsToTerrain</c>.
        /// For NaN-recorded-terrain legacy recordings we keep the historical 4 m
        /// minimum clearance above PQS, but allow callers to request a larger
        /// distance-aware floor at extreme watch ranges.</para>
        ///
        /// <para>Ghosts are kinematic — no physics damage concern — so the safety
        /// floor defaults to 0.5 m when the caller has no better context.
        /// <c>internal static</c> so in-game tests can exercise it without
        /// needing a full <c>IGhostPositioner</c> chain.</para>
        /// </summary>
        internal static TrajectoryPoint ApplyLandedGhostClearance(
            TrajectoryPoint p, int index, string vesselName, double recordedTerrainHeight,
            double minClearanceMeters = 0.5)
        {
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == p.bodyName);
            if (body == null)
                return p;
            if (body.pqsController == null)
            {
                ParsekLog.WarnRateLimited("TerrainCorrect",
                    $"pqs-unready-{body.name}",
                    $"Landed ghost clamp #{index} (\"{vesselName}\"): PQS not spun up " +
                    $"for body '{body.name}' — leaving recorded alt unchanged");
                return p;
            }

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            double pqsTerrain = body.TerrainAltitude(p.latitude, p.longitude, true);

            double floorClearance = System.Math.Max(0.5, minClearanceMeters);

            // Legacy (NaN) path: no recorded surface height — keep the historical
            // 4 m fallback floor, but honor a larger caller-requested clearance so
            // long-range watched ghosts do not clip near the edge of visual range.
            if (double.IsNaN(recordedTerrainHeight))
            {
                double legacyClearance = ResolveNaNFallbackLandedGhostClearanceMeters(minClearanceMeters);
                double legacyTarget = pqsTerrain + legacyClearance;
                if (p.altitude >= legacyTarget)
                    return p;
                // delta is always > 0 here because we already verified p.altitude < legacyTarget.
                double delta = legacyTarget - p.altitude;
                ParsekLog.VerboseRateLimited("TerrainCorrect", $"landed-ghost-{index}",
                    $"Landed ghost clamp #{index} (\"{vesselName}\"): " +
                    $"alt={p.altitude.ToString("F1", ic)} pqsTerrain={pqsTerrain.ToString("F1", ic)} " +
                    $"-> {legacyTarget.ToString("F1", ic)} (delta=+{delta.ToString("F2", ic)}m, " +
                    $"body={body.name}, NaN fallback, clearance={legacyClearance.ToString("F1", ic)}m)");
                p.altitude = legacyTarget;
                return p;
            }

            // Primary path: trust the recorded altitude. Only push up if the
            // recorded altitude is below the current PQS terrain floor — which
            // only happens when PQS terrain has shifted UP since recording.
            double safetyFloor = pqsTerrain + floorClearance;
            if (p.altitude >= safetyFloor)
                return p;

            double upDelta = safetyFloor - p.altitude;
            ParsekLog.VerboseRateLimited("TerrainCorrect", $"landed-ghost-{index}",
                $"Landed ghost clamp #{index} (\"{vesselName}\"): " +
                $"alt={p.altitude.ToString("F1", ic)} pqsTerrain={pqsTerrain.ToString("F1", ic)} " +
                $"-> {safetyFloor.ToString("F1", ic)} (delta=+{upDelta.ToString("F2", ic)}m, " +
                $"body={body.name}, below-pqs-floor, clearance={floorClearance.ToString("F1", ic)}m, " +
                $"recSurface={recordedTerrainHeight.ToString("F1", ic)})");
            p.altitude = safetyFloor;
            return p;
        }

        void IGhostPositioner.PositionAtSurface(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state)
        {
            if (state?.ghost == null || traj?.SurfacePos == null) return;

            SurfacePosition positioned = traj.SurfacePos.Value;
            if (ShouldApplyImmediateSurfacePositionClearance(traj.TerrainHeightAtEnd))
            {
                var syntheticPoint = new TrajectoryPoint
                {
                    latitude = positioned.latitude,
                    longitude = positioned.longitude,
                    altitude = positioned.altitude,
                    rotation = positioned.rotation,
                    bodyName = positioned.body,
                };
                syntheticPoint = ApplyLandedGhostClearance(
                    syntheticPoint, index, traj.VesselName, traj.TerrainHeightAtEnd,
                    ResolveImmediateLandedGhostClearanceMeters(
                        index, traj.VesselName,
                        positioned.body, positioned.latitude, positioned.longitude, positioned.altitude));
                positioned.altitude = syntheticPoint.altitude;
            }

            PositionGhostAtSurface(state.ghost, positioned, ShouldAutoActivateGhost(state));
            state.lastInterpolatedBodyName = positioned.body;
            state.lastInterpolatedAltitude = positioned.altitude;
        }

        void IGhostPositioner.PositionFromOrbit(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut)
        {
            if (state?.ghost == null || traj?.OrbitSegments == null) return;
            // Find the orbit segment covering this UT
            OrbitSegment? seg = FindOrbitSegment(traj.OrbitSegments, ut);
            if (seg.HasValue)
            {
                if (PlaybackOrbitDiagnostics.TryBuildPlaybackPredictedTailLog(
                    index, traj, seg.Value, ut, out string logKey, out string logMessage))
                {
                    ParsekLog.VerboseRateLimited("Playback", logKey, logMessage, 1.0);
                }

                PositionGhostFromOrbit(state.ghost, seg.Value, ut, index * 10000);
            }
        }

        void IGhostPositioner.PositionLoop(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut, bool suppressFx)
        {
            if (state?.ghost == null || traj == null) return;
            int playbackIdx = state.playbackIndex;
            bool useAnchor = GhostPlaybackLogic.ShouldUseLoopAnchor(traj);
            if (useAnchor && !GhostPlaybackLogic.ValidateLoopAnchor(traj.LoopAnchorVesselId))
            {
                long anchorKey = ((long)index << 32) | traj.LoopAnchorVesselId;
                if (loggedAnchorNotFound.Add(anchorKey))
                    ParsekLog.Warn("Loop",
                        $"Loop anchor vessel pid={traj.LoopAnchorVesselId} not found for " +
                        $"recording #{index} \"{traj.VesselName}\" — falling back to absolute positioning");
                useAnchor = false;
            }
            InterpolationResult interpResult;
            PositionLoopGhost(state.ghost, traj, ref playbackIdx, ut,
                index, index * 10000, useAnchor,
                ShouldAutoActivateGhost(state), out interpResult);
            state.SetInterpolated(interpResult);
            state.playbackIndex = playbackIdx;
        }

        ZoneRenderingResult IGhostPositioner.ApplyZoneRendering(int index, GhostPlaybackState state,
            IPlaybackTrajectory traj, double distance, int protectedIndex)
        {
            // Delegates to the private ApplyZoneRendering which has the same signature
            return ApplyZoneRenderingImpl(index, state, traj, distance, protectedIndex);
        }

        void IGhostPositioner.ClearOrbitCache()
        {
            orbitCache.Clear();
        }

        #endregion

        #region Ghost Positioning (shared by manual + timeline playback)

        // CreateGhostSphere moved to GhostVisualBuilder.CreateGhostSphere (T25 Phase 4 prep)

        void InterpolateAndPosition(GameObject ghost, List<TrajectoryPoint> points, ref int cachedIndex,
            double targetUT, bool allowActivation, out InterpolationResult interpResult)
        {
            TrajectoryPoint before, after;
            float t;
            bool hasSegment = TrajectoryMath.InterpolatePoints(
                points, ref cachedIndex, targetUT, out before, out after, out t);

            if (!hasSegment)
            {
                // Either empty list or before recording start
                if (points == null || points.Count == 0)
                {
                    interpResult = InterpolationResult.Zero;
                    ghost.SetActive(false);
                    return;
                }
                PositionGhostAt(ghost, before);
                interpResult = new InterpolationResult(before.velocity, before.bodyName, before.altitude);
                return;
            }

            // Degenerate segment (t == 0 from zero-duration segment)
            if (t == 0f && before.ut == after.ut)
            {
                PositionGhostAt(ghost, before);
                interpResult = new InterpolationResult(before.velocity, before.bodyName, before.altitude);
                return;
            }

            CelestialBody bodyBefore = FlightGlobals.Bodies.Find(b => b.name == before.bodyName);
            CelestialBody bodyAfter = FlightGlobals.Bodies.Find(b => b.name == after.bodyName);
            if (bodyBefore == null || bodyAfter == null)
            {
                Log($"Could not find body: {(bodyBefore == null ? before.bodyName : after.bodyName)}");
                interpResult = InterpolationResult.Zero;
                ghost.SetActive(false);
                return;
            }
            if (allowActivation && !ghost.activeSelf) ghost.SetActive(true);

            Vector3d posBefore = bodyBefore.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3d posAfter = bodyAfter.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);

            Vector3d interpolatedPos = Vector3d.Lerp(posBefore, posAfter, t);
            Quaternion interpolatedRot = Quaternion.Slerp(before.rotation, after.rotation, t);

            interpolatedRot = TrajectoryMath.SanitizeQuaternion(interpolatedRot);

            if (double.IsNaN(interpolatedPos.x) || double.IsNaN(interpolatedPos.y) || double.IsNaN(interpolatedPos.z))
            {
                Log("Warning: NaN in interpolated position, using 'before' position");
                interpolatedPos = posBefore;
            }

            ghost.transform.position = interpolatedPos;
            ghost.transform.rotation = bodyBefore.bodyTransform.rotation * interpolatedRot;

            // Register for LateUpdate re-positioning after FloatingOrigin shift
            ghostPosEntries.Add(new GhostPosEntry
            {
                ghost = ghost,
                mode = GhostPosMode.PointInterp,
                bodyBefore = bodyBefore,
                bodyAfter = bodyAfter,
                latBefore = before.latitude, lonBefore = before.longitude, altBefore = before.altitude,
                latAfter = after.latitude, lonAfter = after.longitude, altAfter = after.altitude,
                t = t,
                pointUT = targetUT,
                interpolatedRot = interpolatedRot,
            });

            interpResult = new InterpolationResult(
                Vector3.Lerp(before.velocity, after.velocity, t),
                after.bodyName,
                TrajectoryMath.InterpolateAltitude(before.altitude, after.altitude, t));
        }

        // Keep the old signature for backward compat with tests
        internal int FindWaypointIndex(double targetUT)
        {
            return TrajectoryMath.FindWaypointIndex(recording, ref lastPlaybackIndex, targetUT);
        }


        void PositionGhostAt(GameObject ghost, TrajectoryPoint point)
        {
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.name == point.bodyName);
            if (body == null)
            {
                ParsekLog.VerboseRateLimited("Flight", "position-ghost-no-body",
                    $"PositionGhostAt: body '{point.bodyName}' not found — ghost position not updated");
                return;
            }

            Vector3d worldPos = body.GetWorldSurfacePosition(
                point.latitude, point.longitude, point.altitude);

            Quaternion sanitized = TrajectoryMath.SanitizeQuaternion(point.rotation);
            ghost.transform.position = worldPos;
            ghost.transform.rotation = body.bodyTransform.rotation * sanitized;

            // Register for LateUpdate re-positioning after FloatingOrigin shift
            ghostPosEntries.Add(new GhostPosEntry
            {
                ghost = ghost,
                mode = GhostPosMode.SinglePoint,
                bodyBefore = body,
                latBefore = point.latitude, lonBefore = point.longitude, altBefore = point.altitude,
                pointUT = point.ut,
                interpolatedRot = sanitized,
            });
        }

        // Delegates to TrajectoryMath — kept for backward compatibility
        internal static OrbitSegment? FindOrbitSegment(List<OrbitSegment> segments, double ut)
        {
            return TrajectoryMath.FindOrbitSegment(segments, ut);
        }

        // Cache to avoid reconstructing Orbit objects every frame
        private Dictionary<long, Orbit> orbitCache = new Dictionary<long, Orbit>();

        void PositionGhostFromOrbit(GameObject ghost, OrbitSegment segment, double ut, long cacheKey)
        {
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == segment.bodyName);
            if (body == null)
            {
                Log($"Could not find body '{segment.bodyName}' for orbit playback");
                return;
            }

            Orbit orbit;
            if (!orbitCache.TryGetValue(cacheKey, out orbit))
            {
                orbit = new Orbit(
                    segment.inclination,
                    segment.eccentricity,
                    segment.semiMajorAxis,
                    segment.longitudeOfAscendingNode,
                    segment.argumentOfPeriapsis,
                    segment.meanAnomalyAtEpoch,
                    segment.epoch,
                    body);
                orbitCache[cacheKey] = orbit;
            }

            Vector3d worldPos = orbit.getPositionAtUT(ut);

            // Surface clamp: if the Keplerian orbit goes underground (e.g., deorbit
            // orbit with periapsis below surface, or impact trajectory on airless body),
            // clamp to surface altitude. Prevents the ghost mesh from tunneling through
            // the planet during orbit-only recording sections where no trajectory points exist.
            double orbitAlt = body.GetAltitude(worldPos);
            if (orbitAlt < 0)
            {
                double lat = body.GetLatitude(worldPos);
                double lon = body.GetLongitude(worldPos);
                worldPos = body.GetWorldSurfacePosition(lat, lon, 0);
            }

            ghost.transform.position = worldPos;

            Vector3d velocity = orbit.getOrbitalVelocityAtUT(ut);

            // Orbital rotation: 3-way branch
            bool hasOfr = TrajectoryMath.HasOrbitalFrameRotation(segment);
            bool spinning = TrajectoryMath.IsSpinning(segment);

            var (ghostRot, boundaryWorldRot) = ComputeOrbitalRotation(
                segment, orbit, ut, velocity, worldPos, body.position,
                ghost.transform.rotation, cacheKey, hasOfr, spinning);

            ghost.transform.rotation = ghostRot;

            // First-activation logging (once per segment)
            if (!loggedOrbitRotationSegments.Contains(cacheKey))
            {
                loggedOrbitRotationSegments.Add(cacheKey);
                if (spinning)
                    ParsekLog.Verbose("Playback", $"Orbit segment {cacheKey}: spin-forward (|angVel|={segment.angularVelocity.magnitude:F4})");
                else if (hasOfr)
                    ParsekLog.Verbose("Playback", $"Orbit segment {cacheKey}: orbital-frame rotation");
                else
                    ParsekLog.Verbose("Playback", $"Orbit segment {cacheKey}: velocity-derived prograde (no ofr data)");
            }

            // Register for LateUpdate re-positioning after FloatingOrigin shift
            ghostPosEntries.Add(new GhostPosEntry
            {
                ghost = ghost,
                mode = GhostPosMode.Orbit,
                orbitCacheKey = cacheKey,
                orbitUT = ut,
                orbitFrameRot = segment.orbitalFrameRotation,
                hasOrbitFrameRot = hasOfr,
                orbitBody = body,
                orbitAngularVelocity = segment.angularVelocity,
                isSpinning = spinning,
                orbitSegmentStartUT = segment.startUT,
                boundaryWorldRot = boundaryWorldRot
            });
        }

        internal static (Quaternion ghostRot, Quaternion boundaryWorldRot) ComputeOrbitalRotation(
            OrbitSegment segment, Orbit orbit, double ut, Vector3d velocity, Vector3d worldPos,
            Vector3d bodyPosition, Quaternion currentRotation, long cacheKey, bool hasOfr, bool spinning)
        {
            Quaternion ghostRot = currentRotation; // preserve current if no update
            Quaternion boundaryWorldRot = Quaternion.identity;

            if (spinning)
            {
                // Spin-forward path: reconstruct boundary world rotation from orbit at startUT
                Vector3d velAtStart = orbit.getOrbitalVelocityAtUT(segment.startUT);
                Vector3d posAtStart = orbit.getPositionAtUT(segment.startUT);
                Vector3d radialAtStart = (posAtStart - bodyPosition).normalized;

                Quaternion orbFrameAtStart = SafeOrbitalLookRotation(
                    velAtStart, radialAtStart, cacheKey, "start");

                boundaryWorldRot = orbFrameAtStart * segment.orbitalFrameRotation;

                double dt = ut - segment.startUT;
                Vector3 worldAxis = boundaryWorldRot * segment.angularVelocity;
                float angle = (float)((double)segment.angularVelocity.magnitude * dt * Mathf.Rad2Deg);
                ghostRot = Quaternion.AngleAxis(angle, worldAxis) * boundaryWorldRot;
            }
            else if (hasOfr && velocity.sqrMagnitude > 0.001)
            {
                // Orbital-frame-relative path
                Vector3d radialOut = (worldPos - bodyPosition).normalized;

                Quaternion orbFrame = SafeOrbitalLookRotation(
                    velocity, radialOut, cacheKey, null);

                ghostRot = orbFrame * segment.orbitalFrameRotation;
            }
            else if (velocity.sqrMagnitude > 0.001)
            {
                // Prograde fallback (old recordings)
                ghostRot = Quaternion.LookRotation(velocity);
            }

            return (ghostRot, boundaryWorldRot);
        }

        /// <summary>
        /// Computes Quaternion.LookRotation(velocity, radialOut) with a fallback to Vector3.up
        /// when velocity and radialOut are nearly parallel (dot > 0.99). Logs rate-limited
        /// diagnostic when the fallback is used. Pass suffix "start" for startUT context or
        /// null for current-time context.
        /// </summary>
        private static Quaternion SafeOrbitalLookRotation(
            Vector3 velocity, Vector3 radialOut, long cacheKey, string suffix)
        {
            if (Mathf.Abs(Vector3.Dot(velocity.normalized, radialOut.normalized)) > 0.99f)
            {
                string tagSuffix = suffix != null ? $"-{suffix}" : "";
                string msgSuffix = suffix != null ? $" at {suffix}UT" : "";
                ParsekLog.VerboseRateLimited("Playback", $"orbit-near-parallel{tagSuffix}-{cacheKey}",
                    $"Orbit segment {cacheKey}: velocity/radialOut near-parallel{msgSuffix}, LookRotation fallback");
                return Quaternion.LookRotation(velocity, Vector3.up);
            }
            return Quaternion.LookRotation(velocity, radialOut);
        }

        /// <summary>
        /// Positions a ghost using only orbit segments (no trajectory points).
        /// Used for background-only recordings (vessels that stayed on rails).
        /// </summary>
        void PositionGhostFromOrbitOnly(GameObject ghost, Recording rec, double ut, int orbitCacheBase,
            bool allowActivation)
        {
            for (int s = 0; s < rec.OrbitSegments.Count; s++)
            {
                var seg = rec.OrbitSegments[s];
                if (ut >= seg.startUT && ut <= seg.endUT)
                {
                    int cacheKey = orbitCacheBase + s;
                    if (loggedOrbitSegments.Add(cacheKey))
                        Log($"Orbit-only segment activated: cache={cacheKey}, body={seg.bodyName}, " +
                            $"sma={seg.semiMajorAxis:F0}, UT {seg.startUT:F0}-{seg.endUT:F0}");
                    PositionGhostFromOrbit(ghost, seg, ut, cacheKey);
                    if (allowActivation)
                        ghost.SetActive(true);
                    return;
                }
            }
            // UT falls in a gap between orbit segments — hide ghost
            ghost.SetActive(false);
        }

        /// <summary>
        /// Positions a ghost at a fixed surface position (body, lat, lon, alt, rotation).
        /// Used for background vessels that are landed/splashed.
        /// </summary>
        void PositionGhostAtSurface(GameObject ghost, SurfacePosition surfPos, bool allowActivation)
        {
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.name == surfPos.body);
            if (body == null)
            {
                ParsekLog.Warn("Flight", $"PositionGhostAtSurface: body '{surfPos.body}' not found");
                ghost.SetActive(false);
                return;
            }
            ghost.transform.position = body.GetWorldSurfacePosition(surfPos.latitude, surfPos.longitude, surfPos.altitude);
            ghost.transform.rotation = body.bodyTransform.rotation * surfPos.rotation;
            if (allowActivation)
                ghost.SetActive(true);

            // Register for LateUpdate re-positioning after FloatingOrigin shift
            ghostPosEntries.Add(new GhostPosEntry
            {
                ghost = ghost,
                mode = GhostPosMode.Surface,
                bodyBefore = body,
                latBefore = surfPos.latitude, lonBefore = surfPos.longitude, altBefore = surfPos.altitude,
                surfaceRot = surfPos.rotation
            });

            ParsekLog.VerboseRateLimited("Flight", "surface-ghost-positioned",
                $"PositionGhostAtSurface: body={surfPos.body} lat={surfPos.latitude:F4} lon={surfPos.longitude:F4} alt={surfPos.altitude:F1}");
        }

        // Tracks which (recording index, anchor PID) combos have been logged for relative playback start
        private readonly HashSet<long> loggedRelativeStart = new HashSet<long>();
        // Tracks which anchor-not-found warnings have been logged
        private readonly HashSet<long> loggedAnchorNotFound = new HashSet<long>();

        internal static bool TryResolvePlaybackDistanceReferencePosition(
            bool mapViewEnabled,
            Vector3d? cameraWorldPosition,
            Vector3d? activeVesselWorldPosition,
            out Vector3d referencePosition)
        {
            referencePosition = Vector3d.zero;

            if (!mapViewEnabled
                && cameraWorldPosition.HasValue
                && IsFiniteVector3d(cameraWorldPosition.Value))
            {
                referencePosition = cameraWorldPosition.Value;
                return true;
            }

            if (activeVesselWorldPosition.HasValue
                && IsFiniteVector3d(activeVesselWorldPosition.Value))
            {
                referencePosition = activeVesselWorldPosition.Value;
                return true;
            }

            if (cameraWorldPosition.HasValue
                && IsFiniteVector3d(cameraWorldPosition.Value))
            {
                referencePosition = cameraWorldPosition.Value;
                return true;
            }

            return false;
        }

        private static bool IsFiniteVector3d(Vector3d value)
        {
            return !double.IsNaN(value.x) && !double.IsInfinity(value.x)
                && !double.IsNaN(value.y) && !double.IsInfinity(value.y)
                && !double.IsNaN(value.z) && !double.IsInfinity(value.z);
        }

        double ResolvePlaybackDistanceForEngine(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            Camera sceneCamera = FlightCamera.fetch?.mainCamera;
            Vector3d? cameraWorldPosition = sceneCamera != null
                ? (Vector3d?)sceneCamera.transform.position
                : null;
            Vector3d? activeVesselWorldPosition = FlightGlobals.ActiveVessel != null
                ? (Vector3d?)FlightGlobals.ActiveVessel.transform.position
                : null;
            Vector3d referencePosition;
            if (!TryResolvePlaybackDistanceReferencePosition(
                    MapView.MapIsEnabled,
                    cameraWorldPosition,
                    activeVesselWorldPosition,
                    out referencePosition))
            {
                return double.NaN;
            }

            return ResolvePlaybackDistanceFromReferencePosition(
                index, traj, state, playbackUT, referencePosition);
        }

        double ResolvePlaybackActiveVesselDistanceForEngine(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            Vector3d? activeVesselWorldPosition = FlightGlobals.ActiveVessel != null
                ? (Vector3d?)FlightGlobals.ActiveVessel.transform.position
                : null;
            if (!activeVesselWorldPosition.HasValue
                || !IsFiniteVector3d(activeVesselWorldPosition.Value))
            {
                return ResolvePlaybackDistanceForEngine(index, traj, state, playbackUT);
            }

            return ResolvePlaybackDistanceFromReferencePosition(
                index, traj, state, playbackUT, activeVesselWorldPosition.Value);
        }

        double ResolvePlaybackDistanceFromReferencePosition(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, Vector3d referencePosition)
        {
            Vector3d worldPos;
            if (TryResolvePlaybackWorldPosition(index, traj, state, playbackUT, out worldPos))
            {
                return Vector3d.Distance(worldPos, referencePosition);
            }

            if (state != null && state.ghost != null)
            {
                return Vector3d.Distance(
                    (Vector3d)state.ghost.transform.position,
                    referencePosition);
            }

            return double.NaN;
        }

        bool TryInterpolateAndPositionCheckpointSection(
            int recordingIndex,
            IPlaybackTrajectory traj,
            GameObject ghost,
            ref int playbackIdx,
            double playbackUT,
            bool allowActivation,
            out InterpolationResult interpResult)
        {
            interpResult = InterpolationResult.Zero;
            if (!TryGetCheckpointTrackSection(traj, playbackUT, out int sectionIdx, out TrackSection section))
                return false;

            if (!TryInterpolateAndPositionCheckpointSectionWithOrbitRotation(
                    recordingIndex,
                    sectionIdx,
                    section,
                    ghost,
                    ref playbackIdx,
                    playbackUT,
                    allowActivation,
                    out interpResult))
            {
                InterpolateAndPosition(
                    ghost,
                    section.frames,
                    ref playbackIdx,
                    playbackUT,
                    allowActivation,
                    out interpResult);
            }

            int logIndex = playbackIdx;
            if (TryResolvePointWorldPosition(section.frames, ref logIndex, playbackUT, out Vector3d worldPos))
                LogCheckpointPointPlayback(traj, sectionIdx, section, playbackUT, worldPos);

            return true;
        }

        bool TryInterpolateAndPositionCheckpointSectionWithOrbitRotation(
            int recordingIndex,
            int sectionIdx,
            TrackSection section,
            GameObject ghost,
            ref int playbackIdx,
            double playbackUT,
            bool allowActivation,
            out InterpolationResult interpResult)
        {
            interpResult = InterpolationResult.Zero;
            if (!TryFindCheckpointOrbitSegment(section, playbackUT, out OrbitSegment segment, out int checkpointIdx))
                return false;

            long cacheKey = BuildCheckpointOrbitCacheKey(recordingIndex, sectionIdx, checkpointIdx);
            if (!TryGetOrbitForSegment(segment, cacheKey, out Orbit orbit, out CelestialBody orbitBody))
                return false;

            bool hasPointInterpolation = TryResolveCheckpointPointInterpolation(
                section.frames,
                ref playbackIdx,
                playbackUT,
                out TrajectoryPoint before,
                out TrajectoryPoint after,
                out float t);
            if (!hasPointInterpolation)
            {
                if (section.frames == null || section.frames.Count == 0)
                {
                    ghost.SetActive(false);
                    return true;
                }

                before = section.frames[0];
                after = before;
                t = 0f;
            }

            CelestialBody bodyBefore = FlightGlobals.Bodies?.Find(b => b.name == before.bodyName);
            CelestialBody bodyAfter = FlightGlobals.Bodies?.Find(b => b.name == after.bodyName);
            if (bodyBefore == null || bodyAfter == null)
                return false;

            if (allowActivation && !ghost.activeSelf)
                ghost.SetActive(true);

            Vector3d posBefore = bodyBefore.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3d posAfter = bodyAfter.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);
            Vector3d interpolatedPos = Vector3d.Lerp(posBefore, posAfter, t);
            if (double.IsNaN(interpolatedPos.x) || double.IsNaN(interpolatedPos.y) || double.IsNaN(interpolatedPos.z))
                interpolatedPos = posBefore;

            Vector3d velocity = orbit.getOrbitalVelocityAtUT(playbackUT);
            bool hasOfr = TrajectoryMath.HasOrbitalFrameRotation(segment);
            bool spinning = TrajectoryMath.IsSpinning(segment);
            var (ghostRot, _) = ComputeOrbitalRotation(
                segment,
                orbit,
                playbackUT,
                velocity,
                interpolatedPos,
                orbitBody.position,
                ghost.transform.rotation,
                cacheKey,
                hasOfr,
                spinning);

            ghost.transform.position = interpolatedPos;
            ghost.transform.rotation = ghostRot;

            Quaternion pointRot = Quaternion.Slerp(before.rotation, after.rotation, t);
            pointRot = TrajectoryMath.SanitizeQuaternion(pointRot);
            ghostPosEntries.Add(new GhostPosEntry
            {
                ghost = ghost,
                mode = GhostPosMode.CheckpointPoint,
                bodyBefore = bodyBefore,
                bodyAfter = bodyAfter,
                latBefore = before.latitude,
                lonBefore = before.longitude,
                altBefore = before.altitude,
                latAfter = after.latitude,
                lonAfter = after.longitude,
                altAfter = after.altitude,
                t = t,
                pointUT = playbackUT,
                interpolatedRot = pointRot,
                orbitCacheKey = cacheKey,
                orbitUT = playbackUT,
                orbitFrameRot = segment.orbitalFrameRotation,
                hasOrbitFrameRot = hasOfr,
                orbitBody = orbitBody,
                orbitAngularVelocity = segment.angularVelocity,
                isSpinning = spinning,
                orbitSegmentStartUT = segment.startUT
            });

            interpResult = new InterpolationResult(
                (Vector3)velocity,
                segment.bodyName,
                TrajectoryMath.InterpolateAltitude(before.altitude, after.altitude, t));
            return true;
        }

        private bool TryGetOrbitForSegment(
            OrbitSegment segment,
            long cacheKey,
            out Orbit orbit,
            out CelestialBody body)
        {
            orbit = null;
            body = FlightGlobals.Bodies?.Find(b => b.name == segment.bodyName);
            if (body == null)
                return false;

            if (!orbitCache.TryGetValue(cacheKey, out orbit))
            {
                orbit = new Orbit(
                    segment.inclination,
                    segment.eccentricity,
                    segment.semiMajorAxis,
                    segment.longitudeOfAscendingNode,
                    segment.argumentOfPeriapsis,
                    segment.meanAnomalyAtEpoch,
                    segment.epoch,
                    body);
                orbitCache[cacheKey] = orbit;
            }

            return true;
        }

        private static bool TryFindCheckpointOrbitSegment(
            TrackSection section,
            double playbackUT,
            out OrbitSegment segment,
            out int checkpointIdx)
        {
            segment = default(OrbitSegment);
            checkpointIdx = -1;
            if (section.checkpoints == null || section.checkpoints.Count == 0)
                return false;

            for (int i = 0; i < section.checkpoints.Count; i++)
            {
                OrbitSegment candidate = section.checkpoints[i];
                if (playbackUT >= candidate.startUT && playbackUT <= candidate.endUT)
                {
                    segment = candidate;
                    checkpointIdx = i;
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveCheckpointPointInterpolation(
            List<TrajectoryPoint> frames,
            ref int playbackIdx,
            double playbackUT,
            out TrajectoryPoint before,
            out TrajectoryPoint after,
            out float t)
        {
            return TrajectoryMath.InterpolatePoints(
                frames,
                ref playbackIdx,
                playbackUT,
                out before,
                out after,
                out t);
        }

        private const long CheckpointOrbitCacheBase = long.MinValue;
        private const long CheckpointOrbitCacheRecordingStride = 1000000000000L;
        private const long CheckpointOrbitCacheSectionStride = 1000000L;
        private const long CheckpointOrbitCacheMaxRecordingIndex =
            (long.MaxValue - CheckpointOrbitCacheRecordingStride + 1) / CheckpointOrbitCacheRecordingStride;

        private static long BuildCheckpointOrbitCacheKey(
            int recordingIndex,
            int sectionIdx,
            int checkpointIdx)
        {
            // Regular orbit playback cache keys are non-negative int-derived values.
            // Checkpoint keys live in a negative long namespace with explicit strides
            // so section/checkpoint growth cannot alias the ordinary segment cache.
            long rec = Math.Min(CheckpointOrbitCacheMaxRecordingIndex, Math.Max(0, (long)recordingIndex));
            long section = Math.Min(CheckpointOrbitCacheSectionStride - 1, Math.Max(0, (long)sectionIdx));
            long checkpoint = Math.Min(CheckpointOrbitCacheSectionStride - 1, Math.Max(0, (long)checkpointIdx));
            return unchecked(
                CheckpointOrbitCacheBase
                + rec * CheckpointOrbitCacheRecordingStride
                + section * CheckpointOrbitCacheSectionStride
                + checkpoint);
        }

        bool TryGetCheckpointTrackSection(
            IPlaybackTrajectory traj,
            double playbackUT,
            out int sectionIdx,
            out TrackSection section)
        {
            sectionIdx = -1;
            section = default(TrackSection);
            if (traj?.TrackSections == null || traj.TrackSections.Count == 0)
                return false;

            sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, playbackUT);
            if (sectionIdx < 0)
                return false;

            section = traj.TrackSections[sectionIdx];
            return section.referenceFrame == ReferenceFrame.OrbitalCheckpoint
                && section.frames != null
                && section.frames.Count > 0;
        }

        void LogCheckpointPointPlayback(
            IPlaybackTrajectory traj,
            int sectionIdx,
            TrackSection section,
            double playbackUT,
            Vector3d worldPos)
        {
            int pointIdx = 0;
            TrajectoryPoint? point = TrajectoryMath.BracketPointAtUT(
                section.frames,
                playbackUT,
                ref pointIdx);
            if (!point.HasValue && section.frames != null && section.frames.Count > 0)
                point = section.frames[0];

            string recId = string.IsNullOrEmpty(traj?.RecordingId) ? "(null)" : traj.RecordingId;
            string pointDetail = point.HasValue
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "pointUT={0:F1} body={1} alt={2:F0} speed={3:F1}",
                    point.Value.ut,
                    point.Value.bodyName ?? "(null)",
                    point.Value.altitude,
                    point.Value.velocity.magnitude)
                : "pointUT=(none) body=(none) alt=0 speed=0";

            // Bug #595: the previous 1.0s rate-limit window was tight enough that
            // a long-playing OrbitalCheckpoint section still emitted ~14
            // lines/min per (rec, section) pair (413 lines in a single 30-min
            // session). Use the default 5s window to keep one line per section
            // per (typical) rate-limit window without losing the section-change
            // signal — the key is per-(recId, sectionIdx) so a new section
            // still logs immediately on first frame.
            ParsekLog.VerboseRateLimited(
                "Playback",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "orbital-checkpoint-point-{0}-{1}",
                    recId,
                    sectionIdx),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "OrbitalCheckpoint point playback: rec={0} currentUT={1:F1} section[{2}] sectionUT={3:F1}-{4:F1} {5} world={6} frames={7}",
                    recId,
                    playbackUT,
                    sectionIdx,
                    section.startUT,
                    section.endUT,
                    pointDetail,
                    FormatVector3d(worldPos),
                    section.frames?.Count ?? 0));
        }

        bool TryResolvePlaybackWorldPosition(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (traj == null)
                return false;

            int cachedIndex = state != null ? state.playbackIndex : 0;
            if (traj.TrackSections != null && traj.TrackSections.Count > 0)
            {
                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, playbackUT);
                if (sectionIdx >= 0)
                {
                    var section = traj.TrackSections[sectionIdx];
                    if (section.referenceFrame == ReferenceFrame.Relative)
                    {
                        uint anchorPid = section.anchorVesselId != 0
                            ? section.anchorVesselId
                            : traj.LoopAnchorVesselId;
                        if (anchorPid != 0 && TryResolveRelativeWorldPosition(
                                section.frames ?? traj.Points,
                                playbackUT,
                                anchorPid,
                                traj.RecordingFormatVersion,
                                out worldPos))
                        {
                            return true;
                        }
                    }
                    else if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint
                        && section.frames != null
                        && section.frames.Count > 0
                        && TryResolvePointWorldPosition(
                            section.frames,
                            ref cachedIndex,
                            playbackUT,
                            out worldPos))
                    {
                        LogCheckpointPointPlayback(traj, sectionIdx, section, playbackUT, worldPos);
                        return true;
                    }
                }
            }

            bool surfaceSkip = TrajectoryMath.IsSurfaceAtUT(traj.TrackSections, playbackUT);
            if (TryResolveInterpolatedWorldPosition(
                    traj.Points, traj.OrbitSegments, ref cachedIndex,
                    playbackUT, index * 10000, surfaceSkip, out worldPos))
            {
                return true;
            }

            if (traj.SurfacePos.HasValue)
                return TryResolveSurfaceWorldPosition(traj.SurfacePos.Value, out worldPos);

            return false;
        }

        bool TryResolveInterpolatedWorldPosition(
            List<TrajectoryPoint> points,
            List<OrbitSegment> segments,
            ref int cachedIndex,
            double targetUT,
            int orbitCacheBase,
            bool skipOrbitSegments,
            out Vector3d worldPos)
        {
            if (!skipOrbitSegments
                && TryResolveOrbitWorldPosition(segments, targetUT, orbitCacheBase, out worldPos))
            {
                return true;
            }

            return TryResolvePointWorldPosition(points, ref cachedIndex, targetUT, out worldPos);
        }

        bool TryResolvePointWorldPosition(
            List<TrajectoryPoint> points,
            ref int cachedIndex,
            double targetUT,
            out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (points == null || points.Count == 0)
                return false;
            if (points.Count == 1)
                return TryResolveTrajectoryPointWorldPosition(points[0], out worldPos);

            TrajectoryPoint before;
            TrajectoryPoint after;
            float t;
            bool hasSegment = TrajectoryMath.InterpolatePoints(
                points, ref cachedIndex, targetUT, out before, out after, out t);
            if (!hasSegment)
            {
                return points != null
                    && points.Count > 0
                    && TryResolveTrajectoryPointWorldPosition(before, out worldPos);
            }

            if (t == 0f && before.ut == after.ut)
                return TryResolveTrajectoryPointWorldPosition(before, out worldPos);

            CelestialBody bodyBefore = FlightGlobals.Bodies?.Find(b => b.name == before.bodyName);
            CelestialBody bodyAfter = FlightGlobals.Bodies?.Find(b => b.name == after.bodyName);
            if (bodyBefore == null || bodyAfter == null)
                return false;

            Vector3d posBefore = bodyBefore.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3d posAfter = bodyAfter.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);

            worldPos = Vector3d.Lerp(posBefore, posAfter, t);
            if (double.IsNaN(worldPos.x) || double.IsNaN(worldPos.y) || double.IsNaN(worldPos.z))
                worldPos = posBefore;
            return true;
        }

        bool TryResolveTrajectoryPointWorldPosition(TrajectoryPoint point, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == point.bodyName);
            if (body == null)
                return false;

            worldPos = body.GetWorldSurfacePosition(
                point.latitude, point.longitude, point.altitude);
            return true;
        }

        bool TryResolveOrbitWorldPosition(
            List<OrbitSegment> segments, double targetUT, int orbitCacheBase, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (segments == null || segments.Count == 0)
                return false;

            for (int i = 0; i < segments.Count; i++)
            {
                OrbitSegment seg = segments[i];
                if (targetUT < seg.startUT || targetUT > seg.endUT)
                    continue;

                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == seg.bodyName);
                if (body == null)
                    return false;

                double bodyRadius = body.Radius;
                double absSma = System.Math.Abs(seg.semiMajorAxis);
                if (absSma < bodyRadius * 0.9)
                    return false;

                Orbit orbit;
                int cacheKey = orbitCacheBase + i;
                if (!orbitCache.TryGetValue(cacheKey, out orbit))
                {
                    orbit = new Orbit(
                        seg.inclination,
                        seg.eccentricity,
                        seg.semiMajorAxis,
                        seg.longitudeOfAscendingNode,
                        seg.argumentOfPeriapsis,
                        seg.meanAnomalyAtEpoch,
                        seg.epoch,
                        body);
                    orbitCache[cacheKey] = orbit;
                }

                worldPos = orbit.getPositionAtUT(targetUT);
                if (body.GetAltitude(worldPos) < 0)
                {
                    double lat = body.GetLatitude(worldPos);
                    double lon = body.GetLongitude(worldPos);
                    worldPos = body.GetWorldSurfacePosition(lat, lon, 0);
                }

                return true;
            }

            return false;
        }

        bool TryResolveRelativeWorldPosition(
            List<TrajectoryPoint> frames,
            double targetUT,
            uint anchorVesselId,
            int recordingFormatVersion,
            out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (frames == null || frames.Count == 0)
                return false;
            if (frames.Count == 1)
            {
                return TryResolveRelativeOffsetWorldPosition(
                    frames[0].latitude, frames[0].longitude, frames[0].altitude,
                    anchorVesselId, recordingFormatVersion, out worldPos);
            }

            int cachedIndex = 0;
            int indexBefore = TrajectoryMath.FindWaypointIndex(frames, ref cachedIndex, targetUT);
            if (indexBefore < 0)
                return TryResolveRelativeOffsetWorldPosition(
                    frames[0].latitude, frames[0].longitude, frames[0].altitude,
                    anchorVesselId, recordingFormatVersion, out worldPos);
            if (indexBefore >= frames.Count - 1)
            {
                return TryResolveRelativeOffsetWorldPosition(
                    frames[frames.Count - 1].latitude,
                    frames[frames.Count - 1].longitude,
                    frames[frames.Count - 1].altitude,
                    anchorVesselId, recordingFormatVersion, out worldPos);
            }

            TrajectoryPoint before = frames[indexBefore];
            TrajectoryPoint after = frames[indexBefore + 1];
            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                return TryResolveRelativeOffsetWorldPosition(
                    before.latitude, before.longitude, before.altitude,
                    anchorVesselId, recordingFormatVersion, out worldPos);
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

            double dx = before.latitude + (after.latitude - before.latitude) * t;
            double dy = before.longitude + (after.longitude - before.longitude) * t;
            double dz = before.altitude + (after.altitude - before.altitude) * t;
            return TryResolveRelativeOffsetWorldPosition(
                dx,
                dy,
                dz,
                anchorVesselId,
                recordingFormatVersion,
                out worldPos);
        }

        bool TryResolveRelativeOffsetWorldPosition(
            double dx,
            double dy,
            double dz,
            uint anchorVesselId,
            int recordingFormatVersion,
            out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            Vessel anchor = FlightRecorder.FindVesselByPid(anchorVesselId);
            if (anchor == null)
                return false;

            Vector3d anchorPos = anchor.GetWorldPos3D();
            worldPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                anchorPos,
                anchor.transform.rotation,
                dx,
                dy,
                dz,
                recordingFormatVersion);
            if (double.IsNaN(worldPos.x) || double.IsNaN(worldPos.y) || double.IsNaN(worldPos.z))
                worldPos = anchorPos;
            return true;
        }

        bool TryResolveSurfaceWorldPosition(SurfacePosition surfacePos, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == surfacePos.body);
            if (body == null)
                return false;

            worldPos = body.GetWorldSurfacePosition(
                surfacePos.latitude, surfacePos.longitude, surfacePos.altitude);
            return true;
        }

        /// <summary>
        /// Unified positioning method for loop ghosts. Selects between anchor-relative
        /// (InterpolateAndPositionRelative) and absolute (InterpolateAndPosition) based
        /// on the useAnchor flag and the TrackSection at the given loopUT.
        /// </summary>
        void PositionLoopGhost(
            GameObject ghost,
            IPlaybackTrajectory rec,
            ref int playbackIdx,
            double loopUT,
            int recIdx,
            int ghostIdSalt,
            bool useAnchor,
            bool allowActivation,
            out InterpolationResult interpResult)
        {
            if (TryInterpolateAndPositionCheckpointSection(
                recIdx,
                rec,
                ghost,
                ref playbackIdx,
                loopUT,
                allowActivation,
                out interpResult))
            {
                return;
            }

            if (useAnchor)
            {
                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, loopUT);
                if (sectionIdx >= 0 && rec.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.Relative)
                {
                    var section = rec.TrackSections[sectionIdx];
                    var sectionFrames = section.frames ?? rec.Points;

                    long relKey = ((long)recIdx << 32) | rec.LoopAnchorVesselId;
                    if (loggedRelativeStart.Add(relKey))
                        ParsekLog.Info("Loop",
                            $"Anchor-relative loop playback started: recording #{recIdx} " +
                            $"\"{rec.VesselName}\" anchorPid={rec.LoopAnchorVesselId} " +
                            $"contract={RecordingStore.DescribeRelativeFrameContract(rec.RecordingFormatVersion)} " +
                            $"version={rec.RecordingFormatVersion} " +
                            $"sectionUT=[{section.startUT:F1},{section.endUT:F1}]");

                    InterpolateAndPositionRelative(
                        ghost, sectionFrames, ref playbackIdx, loopUT,
                        rec.LoopAnchorVesselId, rec.RecordingFormatVersion,
                        allowActivation, out interpResult);
                    return;
                }
            }

            // Absolute positioning fallback
            InterpolateAndPosition(ghost, rec.Points, rec.OrbitSegments,
                ref playbackIdx, loopUT, ghostIdSalt, out interpResult,
                allowActivation: allowActivation);
        }

        /// <summary>
        /// Positions a ghost using RELATIVE reference frame data.
        /// Ghost position/rotation resolve through the recording's versioned RELATIVE
        /// contract (legacy world-offset v5-and-older, anchor-local v6+).
        /// </summary>
        void InterpolateAndPositionRelative(
            GameObject ghost,
            List<TrajectoryPoint> frames,
            ref int cachedIndex,
            double targetUT,
            uint anchorVesselId,
            int recordingFormatVersion,
            bool allowActivation,
            out InterpolationResult interpResult)
        {
            if (frames == null || frames.Count == 0)
            {
                interpResult = InterpolationResult.Zero;
                ghost.SetActive(false);
                return;
            }

            int indexBefore = TrajectoryMath.FindWaypointIndex(frames, ref cachedIndex, targetUT);

            if (indexBefore < 0)
            {
                // Before first frame — position at first frame's offset
                PositionGhostRelativeAt(
                    ghost,
                    frames[0],
                    anchorVesselId,
                    recordingFormatVersion,
                    allowActivation);
                interpResult = new InterpolationResult(frames[0].velocity, frames[0].bodyName, 0);
                return;
            }

            TrajectoryPoint before = frames[indexBefore];
            TrajectoryPoint after = frames[indexBefore + 1];

            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                PositionGhostRelativeAt(
                    ghost,
                    before,
                    anchorVesselId,
                    recordingFormatVersion,
                    allowActivation);
                interpResult = new InterpolationResult(before.velocity, before.bodyName, 0);
                return;
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

            // Interpolate offset (dx, dy, dz stored in lat/lon/alt)
            double dx = before.latitude + (after.latitude - before.latitude) * t;
            double dy = before.longitude + (after.longitude - before.longitude) * t;
            double dz = before.altitude + (after.altitude - before.altitude) * t;

            // Interpolate rotation in the recording's stored RELATIVE frame.
            Quaternion interpolatedRot = Quaternion.Slerp(before.rotation, after.rotation, t);
            interpolatedRot = TrajectoryMath.SanitizeQuaternion(interpolatedRot);

            if (allowActivation && !ghost.activeSelf) ghost.SetActive(true);

            // Find anchor vessel
            Vessel anchor = FlightRecorder.FindVesselByPid(anchorVesselId);
            string bodyName = before.bodyName ?? "Kerbin";

            if (anchor != null)
            {
                // Primary path: ghost position = anchor world position + interpolated offset
                Vector3d anchorPos = anchor.GetWorldPos3D();
                Vector3d ghostPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                    anchorPos,
                    anchor.transform.rotation,
                    dx,
                    dy,
                    dz,
                    recordingFormatVersion);

                if (double.IsNaN(ghostPos.x) || double.IsNaN(ghostPos.y) || double.IsNaN(ghostPos.z))
                {
                    ParsekLog.Warn("Flight", "InterpolateAndPositionRelative: NaN in ghost position, using anchor position");
                    ghostPos = anchorPos;
                }

                ghost.transform.position = ghostPos;
                ghost.transform.rotation = TrajectoryMath.ResolveRelativePlaybackRotation(
                    anchor.transform.rotation,
                    interpolatedRot);

                ParsekLog.VerboseRateLimited("Flight", "relative-offset-applied",
                    $"RELATIVE playback: contract={RecordingStore.DescribeRelativeFrameContract(recordingFormatVersion)} " +
                    $"version={recordingFormatVersion} dx={dx:F2} dy={dy:F2} dz={dz:F2} " +
                    $"|offset|={System.Math.Sqrt(dx * dx + dy * dy + dz * dz):F2}m anchor={anchorVesselId}",
                    2.0);

                // Compute altitude for InterpolationResult
                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                double alt = body != null ? body.GetAltitude(ghostPos) : 0;

                // Register for LateUpdate re-positioning after FloatingOrigin shift
                ghostPosEntries.Add(new GhostPosEntry
                {
                    ghost = ghost,
                    mode = GhostPosMode.Relative,
                    anchorVesselId = anchorVesselId,
                    relDx = dx, relDy = dy, relDz = dz,
                    relativeRot = interpolatedRot,
                    relativeBodyName = bodyName,
                    relativeRecordingFormatVersion = recordingFormatVersion,
                    bodyBefore = body, // for fallback in LateUpdate
                    latBefore = before.latitude, lonBefore = before.longitude, altBefore = before.altitude
                });

                interpResult = new InterpolationResult(
                    Vector3.Lerp(before.velocity, after.velocity, t),
                    bodyName, alt);
            }
            else
            {
                // Anchor not found — keep ghost at its last position instead of hiding.
                // RELATIVE frames store dx/dy/dz meter offsets, not geographic coordinates,
                // so we can't position the ghost. But hiding it during watch mode is worse
                // than freezing it in place. The ghost stays visible at its last known
                // position from the previous ABSOLUTE section.
                long key = ((long)anchorVesselId << 32);
                if (loggedAnchorNotFound.Add(key))
                    ParsekLog.Warn("Anchor",
                        $"RELATIVE playback: anchor vessel pid={anchorVesselId} not found — " +
                        $"ghost frozen at last known position (RELATIVE offsets unusable without anchor)");

                // Return zero result — the ghost stays where it was positioned last frame
                interpResult = InterpolationResult.Zero;
            }
        }

        /// <summary>
        /// Positions a ghost at a single RELATIVE frame point (no interpolation).
        /// Used for edge cases (before first frame, zero-duration segments).
        /// </summary>
        void PositionGhostRelativeAt(
            GameObject ghost,
            TrajectoryPoint point,
            uint anchorVesselId,
            int recordingFormatVersion,
            bool allowActivation)
        {
            if (allowActivation && !ghost.activeSelf) ghost.SetActive(true);

            Quaternion sanitized = TrajectoryMath.SanitizeQuaternion(point.rotation);
            double dx = point.latitude;
            double dy = point.longitude;
            double dz = point.altitude;
            string bodyName = point.bodyName ?? "Kerbin";

            Vessel anchor = FlightRecorder.FindVesselByPid(anchorVesselId);
            if (anchor != null)
            {
                Vector3d anchorPos = anchor.GetWorldPos3D();
                Vector3d ghostPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                    anchorPos,
                    anchor.transform.rotation,
                    dx,
                    dy,
                    dz,
                    recordingFormatVersion);
                ghost.transform.position = ghostPos;
                ghost.transform.rotation = TrajectoryMath.ResolveRelativePlaybackRotation(
                    anchor.transform.rotation,
                    sanitized);

                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                ghostPosEntries.Add(new GhostPosEntry
                {
                    ghost = ghost,
                    mode = GhostPosMode.Relative,
                    anchorVesselId = anchorVesselId,
                    relDx = dx, relDy = dy, relDz = dz,
                    relativeRot = sanitized,
                    relativeBodyName = bodyName,
                    relativeRecordingFormatVersion = recordingFormatVersion,
                    bodyBefore = body,
                    latBefore = dx, lonBefore = dy, altBefore = dz
                });
            }
            else
            {
                // Anchor not found — keep ghost at its last position instead of hiding.
                // Same rationale as InterpolateAndPositionRelative: freezing in place is
                // better than disappearing during watch mode.
                long key = ((long)anchorVesselId << 32);
                if (loggedAnchorNotFound.Add(key))
                    ParsekLog.Warn("Anchor",
                        $"PositionGhostRelativeAt: anchor vessel pid={anchorVesselId} not found — " +
                        $"ghost frozen at last known position");
            }
        }

        void InterpolateAndPosition(GameObject ghost, List<TrajectoryPoint> points,
            List<OrbitSegment> segments, ref int cachedIndex, double targetUT, int orbitCacheBase,
            out InterpolationResult interpResult, bool allowActivation = true, bool skipOrbitSegments = false)
        {
            // Check orbit segments first (unless suppressed for surface vehicles)
            if (!skipOrbitSegments && segments != null && segments.Count > 0)
            {
                OrbitSegment? seg = FindOrbitSegment(segments, targetUT);
                if (seg.HasValue)
                {
                    // Layer 3: SMA sanity check — reject orbit segments where the orbit
                    // is mostly sub-surface (SMA < 90% of body radius). This catches
                    // old recordings that have invalid orbit segments for surface vessels.
                    // Use absolute value: hyperbolic escape orbits have negative SMA but
                    // are valid trajectories that should be rendered.
                    CelestialBody segBody = FlightGlobals.Bodies?.Find(b => b.name == seg.Value.bodyName);
                    double bodyRadius = segBody?.Radius ?? 600000;
                    double absSma = System.Math.Abs(seg.Value.semiMajorAxis);
                    if (absSma < bodyRadius * 0.9)
                    {
                        int smaKey = ~orbitCacheBase; // bitwise complement — guaranteed no collision with positive cache keys
                        if (loggedOrbitSegments.Add(smaKey))
                            Log($"Orbit segment rejected (sub-surface): |sma|={absSma:F0} < " +
                                $"bodyRadius*0.9={bodyRadius * 0.9:F0}, falling through to point interpolation");
                    }
                    else
                    {
                        // Use segment index as cache key offset
                        int segIdx = segments.IndexOf(seg.Value);
                        int cacheKey = orbitCacheBase + segIdx;
                        if (loggedOrbitSegments.Add(cacheKey))
                            Log($"Orbit segment activated: cache={cacheKey}, body={seg.Value.bodyName}, " +
                                $"sma={seg.Value.semiMajorAxis:F0}, UT {seg.Value.startUT:F0}-{seg.Value.endUT:F0}");
                        PositionGhostFromOrbit(ghost, seg.Value, targetUT, cacheKey);
                        Vector3 vel = Vector3.zero;
                        Orbit orbit;
                        if (orbitCache.TryGetValue(cacheKey, out orbit))
                            vel = orbit.getOrbitalVelocityAtUT(targetUT);
                        double segAlt = segBody != null ? segBody.GetAltitude(ghost.transform.position) : 0;
                        interpResult = new InterpolationResult(vel, seg.Value.bodyName, segAlt);
                        return;
                    }
                }
            }

            // Fall through to point-based interpolation
            InterpolateAndPosition(ghost, points, ref cachedIndex, targetUT, allowActivation, out interpResult);
        }

        #endregion

        #region Utilities

        private static string DetermineRecordingBlockReason()
        {
            if (Time.timeScale < 0.01f)
                return "game paused";
            if (FlightGlobals.ActiveVessel == null)
                return "no active vessel";
            return "unknown guard in FlightRecorder.StartRecording";
        }

        // ────────────────────────────────────────────────────────────
        //  Phase 6d: Ghost labels, spawn warnings, chain status
        // ────────────────────────────────────────────────────────────

        private GUIStyle ghostLabelStyle;

        /// <summary>
        /// Draws floating labels on all ghost GOs from active ghost chains.
        /// Called from OnGUI. If ghost GOs are null (deferred from 6b-4), this is a no-op.
        /// </summary>
        private void DrawGhostLabels()
        {
            if (activeGhostChains == null || activeGhostChains.Count == 0)
                return;

            Camera cam = FlightCamera.fetch?.mainCamera;
            if (cam == null)
                return;

            if (ghostLabelStyle == null)
            {
                ghostLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.UpperCenter,
                    normal = { textColor = new Color(1f, 0.9f, 0.5f, 0.9f) }
                };
            }

            foreach (var kvp in activeGhostChains)
            {
                GhostChain chain = kvp.Value;
                if (vesselGhoster == null) continue;

                GameObject ghostGO = vesselGhoster.GetGhostGO(chain.OriginalVesselPid);
                if (ghostGO == null) continue;

                // Convert ghost world position to screen position
                Vector3 screenPos = cam.WorldToScreenPoint(ghostGO.transform.position);

                // Behind camera check
                if (screenPos.z < 0) continue;

                // Off-screen check
                if (screenPos.x < 0 || screenPos.x > Screen.width ||
                    screenPos.y < 0 || screenPos.y > Screen.height)
                    continue;

                // Get vessel name from ghosted info
                var info = vesselGhoster.GetGhostedInfo(chain.OriginalVesselPid);
                string vesselName = info != null ? info.vesselName : "(unknown)";

                string labelText = SpawnWarningUI.ComputeGhostLabelText(
                    vesselName, chain.SpawnUT, chain.IsTerminated, chain.SpawnBlocked,
                    chain.WalkbackExhausted);

                // Unity screen coordinates have Y=0 at bottom; GUI has Y=0 at top
                float guiY = Screen.height - screenPos.y;
                float labelWidth = 250f;
                float labelHeight = 40f;
                Rect labelRect = new Rect(
                    screenPos.x - labelWidth / 2f,
                    guiY - labelHeight,
                    labelWidth,
                    labelHeight);

                GUI.Label(labelRect, labelText, ghostLabelStyle);
            }
        }

        /// <summary>
        /// Static query: returns chain status text for a recording, or null if not in a chain.
        /// Checks whether the recording's vessel PID matches any active ghost chain,
        /// or whether the recording is the tip recording for any chain.
        /// Callable from ParsekUI without needing an instance reference.
        /// </summary>
        internal static string GetChainStatusForRecording(
            Dictionary<uint, GhostChain> chains, Recording rec)
        {
            if (chains == null || chains.Count == 0 || rec == null)
                return null;

            // Check if this recording's vessel is ghosted (vessel PID match)
            GhostChain vesselChain;
            if (rec.VesselPersistentId != 0 &&
                chains.TryGetValue(rec.VesselPersistentId, out vesselChain))
            {
                var info = vesselChain;
                return SpawnWarningUI.FormatChainStatus(info, rec.VesselName);
            }

            // Check if this recording is the tip recording for any chain
            foreach (var kvp in chains)
            {
                if (kvp.Value.TipRecordingId == rec.RecordingId)
                {
                    return string.Format(CultureInfo.InvariantCulture,
                        "Chain tip -- will spawn vessel at UT={0}",
                        kvp.Value.SpawnUT.ToString("F0", CultureInfo.InvariantCulture));
                }
            }

            return null;
        }

        /// <summary>
        /// Accessor for active ghost chains — used by ParsekUI for chain status display.
        /// Returns null when no chains are active.
        /// </summary>
        internal Dictionary<uint, GhostChain> ActiveGhostChains => activeGhostChains;

        // ════════════════════════════════════════════════════════════════
        //  Real Spawn Control: proximity check and warp
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Periodic proximity scan: finds nearby ghost craft whose recording ends
        /// in the future and would spawn a real vessel. Runs every 1.5s from Update().
        /// Fires a one-time screen notification when a new candidate enters range.
        /// </summary>
        private void UpdateProximityCheck()
        {
            if (Time.time < nextProximityCheckTime) return;
            nextProximityCheckTime = Time.time + ProximityCheckIntervalSec;

            nearbySpawnCandidates.Clear();

            if (FlightGlobals.ActiveVessel == null) return;

            Vector3d activePos = FlightGlobals.ActiveVessel.GetWorldPos3D();
            uint activePid = FlightGlobals.ActiveVessel.persistentId;
            if (activePid != lastProximityActiveVesselPid)
            {
                if (lastProximityActiveVesselPid != 0 && proximityVelocitySamples.Count > 0)
                    ParsekLog.Verbose("Flight",
                        string.Format(CultureInfo.InvariantCulture,
                            "Proximity check: active vessel changed pid {0} -> {1}, dropped {2} velocity sample(s)",
                            lastProximityActiveVesselPid, activePid, proximityVelocitySamples.Count));
                proximityVelocitySamples.Clear();
                lastProximityActiveVesselPid = activePid;
            }
            double currentUT = Planetarium.GetUniversalTime();
            var committed = RecordingStore.CommittedRecordings;

            CollectNearbySpawnCandidates(activePos, currentUT, committed);

            // Sort by distance (closest first)
            nearbySpawnCandidates.Sort((a, b) => a.distance.CompareTo(b.distance));
            proximityCheckGeneration++;

            NotifyNewProximityCandidates(currentUT);
        }

        /// <summary>
        /// Scans active ghosts for spawn-eligible recordings within proximity range.
        /// Populates nearbySpawnCandidates with qualifying entries.
        /// </summary>
        private void CollectNearbySpawnCandidates(Vector3d activePos, double currentUT, IReadOnlyList<Recording> committed)
        {
            int admittedOverSpeed = 0;
            int skippedSeeding = 0;
            float now = Time.time;
            // Track which recordings still have an active ghost so we can prune stale samples.
            var seenRecordingIds = new HashSet<string>();

            foreach (var kvp in ghostStates)
            {
                int i = kvp.Key;
                GhostPlaybackState state = kvp.Value;
                if (state == null || state.ghost == null || !state.ghost.activeSelf)
                    continue;
                if (i < 0 || i >= committed.Count)
                    continue;

                Recording rec = committed[i];
                if (rec.EndUT <= currentUT)
                    continue;

                // Check spawn eligibility
                bool isActiveChainMember = chainManager.ActiveChainId != null && rec.ChainId == chainManager.ActiveChainId;
                bool isChainLooping = !string.IsNullOrEmpty(rec.ChainId) &&
                    RecordingStore.IsChainLooping(rec.ChainId);

                var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                    rec, isActiveChainMember, isChainLooping);
                if (!needsSpawn)
                    continue;

                // Check chain suppression
                if (activeGhostChains != null)
                {
                    var (suppressed, _) = GhostPlaybackLogic.ShouldSuppressSpawnForChain(
                        activeGhostChains, rec);
                    if (suppressed)
                        continue;
                }

                Vector3d ghostPos = state.ghost.transform.position;
                double dist = Vector3d.Distance(activePos, ghostPos);
                // Outer "show in list" radius: ghosts inside this — but outside the inner
                // FF radius — appear in the window with the FF button disabled.
                if (dist > NearbySpawnListRadius)
                    continue;

                // Frame-agnostic relative-speed sample. We compute d(active_pos - ghost_pos)/dt
                // across consecutive proximity scans rather than subtracting velocity vectors
                // from KSP and ghost playback (those are not in a guaranteed common frame —
                // TrajectoryPoint.velocity is "not guaranteed surface-relative" and orbit-only
                // ghosts never seed lastInterpolatedVelocity at all). Both samples are read
                // from transform.position in the same Update tick, so floating-origin and
                // krakensbane shifts cancel in the per-sample relative vector.
                //
                // Two-tier gating:
                //   • outer (this method) — ghosts beyond NearbySpawnListRadius / faster than
                //     MaxListRelativeSpeed are dropped from the list entirely
                //   • inner (SpawnControlPresentation.BuildRowPresentation) — ghosts within
                //     the outer bounds but beyond NearbySpawnRadius / MaxRelativeSpeed appear
                //     in the list with the FF button disabled, so the player can see what is
                //     blocking the warp ("closing too fast", "still too far") at a glance.
                seenRecordingIds.Add(rec.RecordingId);
                bool hasPrev = proximityVelocitySamples.TryGetValue(rec.RecordingId, out var prev);
                // Always overwrite the sample so the next scan can compute against this one.
                proximityVelocitySamples[rec.RecordingId] = new ProximityVelocitySample
                {
                    activePos = activePos,
                    ghostPos = ghostPos,
                    time = now
                };

                if (!hasPrev)
                {
                    // First sighting in range — seed only, defer admission until we have
                    // a second sample to derive a velocity from.
                    skippedSeeding++;
                    continue;
                }

                float dt = now - prev.time;
                double relSpeed = SelectiveSpawnUI.ComputeRelativeSpeed(
                    activePos, ghostPos, prev.activePos, prev.ghostPos, dt,
                    ProximityVelocitySampleMinDt, ProximityVelocitySampleMaxDt);
                if (relSpeed > MaxListRelativeSpeed)
                    continue;
                if (relSpeed > MaxRelativeSpeed)
                    admittedOverSpeed++;

                var depInfo = SelectiveSpawnUI.ComputeDepartureInfo(rec, currentUT);
                nearbySpawnCandidates.Add(new NearbySpawnCandidate
                {
                    recordingIndex = i,
                    vesselName = rec.VesselName,
                    endUT = rec.EndUT,
                    distance = dist,
                    relativeSpeed = relSpeed,
                    recordingId = rec.RecordingId,
                    willDepart = depInfo.willDepart,
                    departureUT = depInfo.departureUT,
                    destination = depInfo.destination
                });
            }

            // Drop samples for recordings that are no longer in-range / ghosted, so the cache
            // doesn't grow unboundedly across a long session.
            if (proximityVelocitySamples.Count > seenRecordingIds.Count)
            {
                var stale = new List<string>();
                foreach (var key in proximityVelocitySamples.Keys)
                    if (!seenRecordingIds.Contains(key)) stale.Add(key);
                for (int s = 0; s < stale.Count; s++)
                    proximityVelocitySamples.Remove(stale[s]);
            }

            if ((admittedOverSpeed > 0 || skippedSeeding > 0) && ParsekLog.IsVerboseEnabled)
                ParsekLog.Verbose("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "Proximity check: admitted {0} over rel-speed > {1:F1} m/s (FF gated) + {2} seeding first sample",
                        admittedOverSpeed, MaxRelativeSpeed, skippedSeeding));
        }

        /// <summary>
        /// Fires one-time screen notifications for newly discovered proximity candidates.
        /// </summary>
        private void NotifyNewProximityCandidates(double currentUT)
        {
            for (int c = 0; c < nearbySpawnCandidates.Count; c++)
            {
                var cand = nearbySpawnCandidates[c];
                // The list now extends to NearbySpawnListRadius / MaxListRelativeSpeed for
                // visibility, but the screen-message alert promises "fast forward and interact"
                // so only fire it once the ghost is actually within the FF-enable gates.
                if (cand.distance > NearbySpawnRadius || cand.relativeSpeed > MaxRelativeSpeed)
                    continue;
                if (notifiedSpawnRecordingIds.Add(cand.recordingId))
                {
                    string notifyMsg;
                    if (cand.willDepart)
                    {
                        double depDelta = cand.departureUT - currentUT;
                        notifyMsg = string.Format(CultureInfo.InvariantCulture,
                            "Nearby craft: {0} (departs to {1} in {2}). Open Real Spawn Control.",
                            cand.vesselName, cand.destination,
                            SelectiveSpawnUI.FormatTimeDelta(depDelta));
                    }
                    else
                    {
                        notifyMsg = string.Format(CultureInfo.InvariantCulture,
                            "Nearby craft: {0}. Open the Real Spawn Control window to fast forward and interact.",
                            cand.vesselName);
                    }
                    ParsekLog.ScreenMessage(notifyMsg, 10f);
                    ParsekLog.Info("Flight",
                        string.Format(CultureInfo.InvariantCulture,
                            "Proximity notification: '{0}' recording #{1} distance={2:F0}m endUT={3:F1}",
                            cand.vesselName, cand.recordingIndex, cand.distance, cand.endUT));
                }
            }

            if (ParsekLog.IsVerboseEnabled && nearbySpawnCandidates.Count > 0)
                ParsekLog.Verbose("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "Proximity check: {0} candidate(s) within list bounds {1:F0}m / {2:F1} m/s (FF gated by {3:F0}m / {4:F1} m/s)",
                        nearbySpawnCandidates.Count,
                        NearbySpawnListRadius, MaxListRelativeSpeed,
                        NearbySpawnRadius, MaxRelativeSpeed));
        }

        /// <summary>
        /// Executes a time jump to a recording's EndUT so it becomes a real craft.
        /// Called from ParsekUI when the player clicks a "Warp" button.
        /// Passes null chains to ExecuteJump — spawning is handled by the engine playback loop.
        /// </summary>
        internal void WarpToRecordingEnd(int recordingIndex)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (recordingIndex < 0 || recordingIndex >= committed.Count)
            {
                ParsekLog.Warn("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "WarpToRecordingEnd: invalid index {0} — aborted", recordingIndex));
                return;
            }

            Recording rec = committed[recordingIndex];
            double targetUT = rec.EndUT;
            double currentUT = Planetarium.GetUniversalTime();

            if (!TimeJumpManager.IsValidJump(currentUT, targetUT))
            {
                ParsekLog.Warn("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "WarpToRecordingEnd: invalid jump current={0:F1} target={1:F1} — aborted",
                        currentUT, targetUT));
                return;
            }

            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "WarpToRecordingEnd: jumping to UT={0:F1} for '{1}' (delta={2:F1}s)",
                    targetUT, rec.VesselName, targetUT - currentUT));

            TimeJumpManager.NotifyRecorder(recorder, currentUT, targetUT);
            // Pass null chains — let the engine playback loop handle spawn naturally
            TimeJumpManager.ExecuteJump(targetUT, null, vesselGhoster);
        }

        /// <summary>
        /// Epoch-shifted warp to a ghost's departure UT (when it leaves its current orbit).
        /// Used by Real Spawn Control when a ghost will depart before its EndUT.
        /// Preserves rendezvous geometry — both player and ghost stay at current positions.
        /// </summary>
        internal void WarpToDeparture(int recordingIndex, double departureUT)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (recordingIndex < 0 || recordingIndex >= committed.Count)
            {
                ParsekLog.Warn("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "WarpToDeparture: invalid index {0} — aborted", recordingIndex));
                return;
            }

            Recording rec = committed[recordingIndex];
            double currentUT = Planetarium.GetUniversalTime();

            if (!TimeJumpManager.IsValidJump(currentUT, departureUT))
            {
                ParsekLog.Warn("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "WarpToDeparture: invalid jump current={0:F1} target={1:F1} — aborted",
                        currentUT, departureUT));
                return;
            }

            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "WarpToDeparture: jumping to UT={0:F1} for '{1}' (delta={2:F1}s, endUT={3:F1})",
                    departureUT, rec.VesselName, departureUT - currentUT, rec.EndUT));

            TimeJumpManager.NotifyRecorder(recorder, currentUT, departureUT);
            // Epoch-shifted jump: preserves rendezvous geometry
            TimeJumpManager.ExecuteJump(departureUT, null, vesselGhoster);

            ParsekLog.ScreenMessage(
                string.Format(CultureInfo.InvariantCulture,
                    "Warped to departure of \"{0}\" ({1:F0}s)",
                    rec.VesselName, departureUT - currentUT), 5f);
        }

        /// <summary>
        /// Fast-forwards to a recording's StartUT by advancing UT without epoch-shifting.
        /// Unlike WarpToRecordingEnd (which epoch-shifts for Real Spawn Control),
        /// this lets orbits propagate naturally — a regular instant time warp.
        /// Keeps the current view (KSC stays KSC, flight stays on current vessel).
        /// </summary>
        internal void FastForwardToRecording(Recording rec)
        {
            if (rec == null)
            {
                ParsekLog.Warn("Flight", "FastForwardToRecording: null recording — aborted");
                return;
            }

            double targetUT = rec.StartUT;
            double currentUT = Planetarium.GetUniversalTime();

            if (!TimeJumpManager.IsValidJump(currentUT, targetUT))
            {
                ParsekLog.Warn("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "FastForwardToRecording: invalid jump current={0:F1} target={1:F1} — aborted",
                        currentUT, targetUT));
                return;
            }

            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "FastForwardToRecording: jumping to UT={0:F1} for '{1}' (delta={2:F1}s)",
                    targetUT, rec.VesselName, targetUT - currentUT));

            // If watching, transfer watch to the FF target recording.
            // The current watched ghost may be far away after the time jump.
            if (watchMode.IsWatchingGhost)
            {
                var committed = RecordingStore.CommittedRecordings;
                int ffTargetIdx = -1;
                for (int i = 0; i < committed.Count; i++)
                {
                    if (committed[i].RecordingId == rec.RecordingId)
                    {
                        ffTargetIdx = i;
                        break;
                    }
                }
                if (ffTargetIdx >= 0 && ffTargetIdx != watchMode.WatchedRecordingIndex)
                {
                    ParsekLog.Info("CameraFollow",
                        $"FF transfer: watch #{watchMode.WatchedRecordingIndex} \u2192 #{ffTargetIdx} \"{rec.VesselName}\"");
                    // Exit current watch, ghost will be positioned by engine after time jump
                    ExitWatchMode();
                    // Defer entering watch on the target — the ghost needs to be positioned
                    // after the time jump. Store the target for post-jump pickup.
                    pendingWatchAfterFFId = rec.RecordingId;
                }
            }

            TimeJumpManager.NotifyRecorder(recorder, currentUT, targetUT);
            TimeJumpManager.ExecuteForwardJump(targetUT);

            ParsekLog.ScreenMessage(
                string.Format(CultureInfo.InvariantCulture,
                    "Fast-forwarded to \"{0}\" ({1:F0}s)",
                    rec.VesselName, targetUT - currentUT), 3f);
        }

        /// <summary>
        /// Warps to the earliest nearby spawn candidate.
        /// If the candidate will depart, warps to departure UT instead of EndUT.
        /// </summary>
        internal void WarpToNextCraftSpawn()
        {
            double currentUT = Planetarium.GetUniversalTime();
            var next = SelectiveSpawnUI.FindNextSpawnCandidate(nearbySpawnCandidates, currentUT, NearbySpawnRadius, MaxRelativeSpeed);
            if (next == null)
            {
                ParsekLog.Verbose("Flight", "WarpToNextCraftSpawn: no nearby candidates");
                return;
            }
            if (next.Value.willDepart)
                WarpToDeparture(next.Value.recordingIndex, next.Value.departureUT);
            else
                WarpToRecordingEnd(next.Value.recordingIndex);
        }

        void Log(string message) => ParsekLog.Verbose("Flight", message);
        void ScreenMessage(string message, float duration) => ParsekLog.ScreenMessage(message, duration);

        #endregion
    }
}
