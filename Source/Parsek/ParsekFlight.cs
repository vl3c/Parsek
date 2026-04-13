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
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ParsekFlight : MonoBehaviour, IGhostPositioner
    {
        internal static ParsekFlight Instance { get; private set; }

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

        // Set true in OnSceneChangeRequested — suppresses Update() to prevent
        // ghost spawns and other processing into the dying scene.
        private bool sceneChangeInProgress;

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

        // Ghost state is owned by the engine (T25). These properties forward to engine fields
        // so existing ParsekFlight code accesses engine-owned collections transparently.
        private Dictionary<int, GhostPlaybackState> ghostStates => engine.ghostStates;
        private List<GameObject> activeExplosions => engine.activeExplosions;

        // Cached TimelineGhosts dictionary — rebuilt once per frame on first access (T20)
        private Dictionary<int, GameObject> cachedTimelineGhosts = new Dictionary<int, GameObject>();
        private int cachedTimelineGhostsFrame = -1;

        // Real Spawn Control: proximity-based nearby craft detection
        private readonly List<NearbySpawnCandidate> nearbySpawnCandidates = new List<NearbySpawnCandidate>();
        private float nextProximityCheckTime;
        private const float ProximityCheckIntervalSec = 1.5f;
        internal const double NearbySpawnRadius = 500.0;
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

        private enum GhostPosMode { PointInterp, SinglePoint, Orbit, Surface, Relative }

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
            public int orbitCacheKey;
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
        }

        private readonly List<GhostPosEntry> ghostPosEntries = new List<GhostPosEntry>();

        // Auto-record: EVA from pad triggers recording after vessel switch completes
        private bool pendingAutoRecord = false;

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
        // Vessel PIDs that existed before a joint break (for filtering pre-existing vessels)
        private HashSet<uint> preBreakVesselPids;
        // Vessels created by decouple during recording (caught via onPartDeCoupleNewVesselComplete
        // synchronously during Part.decouple(), before KSP's debris cleanup can destroy them).
        // Subscribed for the entire recording session, consumed by DeferredJointBreakCheck.
        private List<Vessel> decoupleCreatedVessels;
        // Controller status captured at creation time (vessel parts may be cleared before deferred check)
        private Dictionary<uint, bool> decoupleControllerStatus;

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

        // Diagnostic logging guards (log once per state transition, not per frame)
        private HashSet<int> loggedOrbitSegments = new HashSet<int>();
        private HashSet<int> loggedOrbitRotationSegments = new HashSet<int>();

        // Anchor vessel tracking moved to engine (T25).
        private HashSet<uint> loadedAnchorVessels => engine.loadedAnchorVessels;

        // Phase 6b: Ghost chain evaluation + vessel ghosting
        private VesselGhoster vesselGhoster;
        private Dictionary<uint, GhostChain> activeGhostChains;

        // Deferred spawn queue moved to ParsekPlaybackPolicy (#132)
        private bool timelineResourceReplayPausedLogged = false;
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
        internal double GetLoopIntervalSecondsForWatch(Recording rec)
        {
            double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                    ?? GhostPlaybackLogic.DefaultLoopIntervalSeconds;
            return engine.GetLoopIntervalSeconds(rec, globalInterval);
        }
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
        internal bool HasDeferredWatchAfterFastForward => !string.IsNullOrEmpty(pendingWatchAfterFFId);

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
        }

        void Update()
        {
            // After OnSceneChangeRequested, the scene is tearing down — skip all processing
            // to prevent ghost spawns and other work into the dying scene.
            if (sceneChangeInProgress) return;

            ClearStaleConfirmations();

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
                            Vector3d ghostPos = TrajectoryMath.ApplyRelativeOffset(anchorPos, e.relDx, e.relDy, e.relDz);
                            e.ghost.transform.position = ghostPos;
                            // Rotation: apply anchor's current rotation * relative rotation
                            e.ghost.transform.rotation = anchor.transform.rotation * e.relativeRot;
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
                windowRect = ClickThruBlocker.GUILayoutWindow(
                    GetInstanceID(),
                    windowRect,
                    ui.DrawWindow,
                    "Parsek",
                    ui.GetOpaqueWindowStyle(),
                    GUILayout.Width(250)
                );
                ui.LogMainWindowPosition(windowRect);
                ui.DrawRecordingsWindowIfOpen(windowRect);
                ui.DrawTimelineWindowIfOpen(windowRect);
                ui.DrawSettingsWindowIfOpen(windowRect);
                ui.DrawSpawnControlWindowIfOpen(windowRect);
                ui.DrawTestRunnerWindowIfOpen(windowRect, this);
            }
        }

        void OnDestroy()
        {
            Instance = null;
            ParsekLog.Info("Flight", "OnDestroy: cleaning up ParsekFlight");

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
            }

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
            notifiedSpawnRecordingIds.Clear();
            loggedRelativeStart.Clear();
            loggedAnchorNotFound.Clear();

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

            // Finalize continuation sampling before anything else
            chainManager.StopAllContinuations("scene change");

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
                    Patches.FlightResultsPatch.ArmForDeferredMerge(
                        "active vessel destroyed in tree mode");
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
                    Patches.FlightResultsPatch.CancelDeferredMerge(
                        "tree destruction dialog aborted: activeTree null");
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
                Patches.FlightResultsPatch.CancelDeferredMerge(
                    "tree destruction dialog aborted: active vessel survived");
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
                                Patches.FlightResultsPatch.CancelDeferredMerge(
                                    "tree destruction dialog aborted: activeTree null");
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
                            Patches.FlightResultsPatch.CancelDeferredMerge(
                                "tree destruction dialog aborted: active vessel survived");
                            ParsekLog.Verbose("Flight",
                                "ShowPostDestructionTreeMergeDialog: active vessel still alive — aborting");
                            treeDestructionDialogPending = false;
                            yield break;
                        }

                        continue;

                    case PostDestructionMergeResolution.CancelDeferredMerge:
                        Patches.FlightResultsPatch.CancelDeferredMerge(
                            "tree destruction dialog aborted: not all leaves terminal");
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

            // Auto-discard pad failures: if every recording in the tree is a pad failure
            // (< 10s duration AND < 30m from launch), discard the whole tree.
            // Also discard if every recording is idle-on-pad (< 30m, any duration).
            if (IsTreePadFailure(activeTree) || IsTreeIdleOnPad(activeTree))
            {
                string reason = IsTreePadFailure(activeTree) ? "pad failure" : "idle on pad";
                ParsekLog.Info("Flight",
                    $"ShowPostDestructionTreeMergeDialog: tree {reason} — auto-discarding");
                ScreenMessage($"Recording discarded — {reason}", 3f);
                RecordingStore.DiscardPendingTree();
                Patches.FlightResultsPatch.CancelDeferredMerge(
                    $"tree destruction auto-discarded ({reason})");
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
                yield break;
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

            // Show dialog or auto-merge
            if (ParsekScenario.IsAutoMerge)
            {
                ParsekLog.Info("Flight", "ShowPostDestructionTreeMergeDialog: auto-merge enabled — committing tree");
                var treeToCommit = RecordingStore.PendingTree;
                RecordingStore.CommitPendingTree();
                LedgerOrchestrator.NotifyLedgerTreeCommitted(treeToCommit);
                MergeDialog.ResolveDeferredFlightResults();
            }
            else
            {
                ParsekLog.Info("Flight", "ShowPostDestructionTreeMergeDialog: showing tree merge dialog");
                MergeDialog.ShowTreeDialog(RecordingStore.PendingTree);
            }
        }

        /// <summary>
        /// Primary detection mechanism for vessel switches in tree mode.
        /// Handles both transition (old vessel → background) AND promotion (new vessel → active).
        /// Fires regardless of on-rails state, unlike OnPhysicsFrame.
        /// </summary>
        void OnVesselSwitchComplete(Vessel newVessel)
        {
            if (newVessel != null && GhostMapPresence.IsGhostMapVessel(newVessel.persistentId)) return;

            // #267: skip if restore coroutine is mid-yield — it owns activeTree/recorder
            if (restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    $"OnVesselSwitchComplete: skipped — restore coroutine in progress " +
                    $"(vessel '{newVessel?.vesselName}')");
                return;
            }

            ParsekLog.RecState("OnVesselSwitchComplete:entry", CaptureRecorderState());

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

            if (activeTree == null) return;
            if (newVessel == null) return;

            // Fix 1 (CRITICAL): Don't interfere with pending merge processing.
            // Dock-induced vessel switches must not promote before Update() runs the merge handler.
            if (pendingTreeDockMerge) return;

            // Don't promote during pending board merge — the board handler in Update() owns this.
            if (recorder != null && recorder.ChainToVesselPending) return;

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
            if (recorder != null && recorder.IsRecording && !recorder.IsBackgrounded
                && recorder.RecordingVesselId != newVessel.persistentId)
            {
                recorder.TransitionToBackground();
                FlushRecorderToTreeRecording(recorder, activeTree);
                CopyRewindSaveToRoot(activeTree, recorder.CaptureAtStop,
                    recorderFallbackSave: recorder.RewindSaveFileName,
                    logTag: "OnVesselSwitch(active)");

                // Add old vessel to BackgroundMap
                string oldRecId = activeTree.ActiveRecordingId;
                Recording oldRec;
                if (oldRecId != null && activeTree.Recordings.TryGetValue(oldRecId, out oldRec)
                    && oldRec.VesselPersistentId != 0)
                {
                    activeTree.BackgroundMap[oldRec.VesselPersistentId] = oldRecId;
                }
                activeTree.ActiveRecordingId = null;
                recorder.IsRecording = false;
                uint bgPid1 = recorder.RecordingVesselId;
                recorder = null;
                backgroundRecorder?.OnVesselBackgrounded(bgPid1);
                Log($"Tree: onVesselSwitchComplete transitioned old recorder to background");
            }
            // OnPhysicsFrame already backgrounded the recorder (TransitionToBackgroundPending),
            // but Update() hasn't flushed it yet. Flush now so promotion below can proceed
            // with the old recorder data committed and BackgroundMap up to date.
            else if (recorder != null && recorder.IsBackgrounded && recorder.TransitionToBackgroundPending)
            {
                FlushRecorderToTreeRecording(recorder, activeTree);
                CopyRewindSaveToRoot(activeTree, recorder.CaptureAtStop,
                    recorderFallbackSave: recorder.RewindSaveFileName,
                    logTag: "OnVesselSwitch(bg)");

                string oldRecId = activeTree.ActiveRecordingId;
                Recording oldRec;
                uint bgPid2 = 0;
                if (oldRecId != null && activeTree.Recordings.TryGetValue(oldRecId, out oldRec)
                    && oldRec.VesselPersistentId != 0)
                {
                    activeTree.BackgroundMap[oldRec.VesselPersistentId] = oldRecId;
                    bgPid2 = oldRec.VesselPersistentId;
                }
                activeTree.ActiveRecordingId = null;
                recorder = null;
                if (bgPid2 != 0) backgroundRecorder?.OnVesselBackgrounded(bgPid2);
                Log($"Tree: onVesselSwitchComplete flushed backgrounded recorder (recId={oldRecId})");
            }

            // Promote the new vessel if it's in the tree
            uint newPid = newVessel.persistentId;
            string backgroundRecordingId;
            if (activeTree.BackgroundMap.TryGetValue(newPid, out backgroundRecordingId))
            {
                backgroundRecorder?.OnVesselRemovedFromBackground(newPid);
                PromoteRecordingFromBackground(backgroundRecordingId, newVessel);
            }
            ParsekLog.RecState("OnVesselSwitchComplete:post", CaptureRecorderState());
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
        /// Copies the rewind save filename and reserved budget from a CaptureAtStop (or
        /// recorder fallback) to the tree's root recording. Called from every flush/commit
        /// site so the R button and rewind budget resolve correctly regardless of which
        /// child recorder is active at commit time.
        /// T59: without this, EVA branch recordings lose the rewind save because the EVA
        /// recorder never captures one — the parent's CaptureAtStop is the only source.
        /// </summary>
        internal static void CopyRewindSaveToRoot(RecordingTree tree, Recording captureAtStop,
            string recorderFallbackSave = null, string logTag = null)
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
            if (captureAtStop != null && rootRec.RewindReservedFunds == 0
                && rootRec.RewindReservedScience == 0 && rootRec.RewindReservedRep == 0)
            {
                rootRec.RewindReservedFunds = captureAtStop.RewindReservedFunds;
                rootRec.RewindReservedScience = captureAtStop.RewindReservedScience;
                rootRec.RewindReservedRep = captureAtStop.RewindReservedRep;
            }

            // Copy pre-launch budget if not already set.
            if (captureAtStop != null && rootRec.PreLaunchFunds == 0
                && rootRec.PreLaunchScience == 0 && rootRec.PreLaunchReputation == 0)
            {
                rootRec.PreLaunchFunds = captureAtStop.PreLaunchFunds;
                rootRec.PreLaunchScience = captureAtStop.PreLaunchScience;
                rootRec.PreLaunchReputation = captureAtStop.PreLaunchReputation;
            }
        }

        /// <summary>
        /// Creates a new FlightRecorder for the promoted vessel, starts recording with
        /// isPromotion=true, updates activeTree state.
        /// </summary>
        void PromoteRecordingFromBackground(string backgroundRecordingId, Vessel newVessel)
        {
            if (activeTree == null || newVessel == null) return;

            ParsekLog.RecState("PromoteFromBackground:entry", CaptureRecorderState());

            // Remove from BackgroundMap
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
                return;
            }

            ParsekLog.Info("Flight", $"Promoted recording '{backgroundRecordingId}' from background " +
                $"(pid={newVessel.persistentId})");
            ParsekLog.RecState("PromoteFromBackground:exit", CaptureRecorderState());
        }

        #region Split Event Detection (Tree Branching)

        /// <summary>
        /// Checks whether a vessel type alone qualifies as trackable.
        /// SpaceObject (asteroids, comets) are always trackable.
        /// Other types require a module check (see IsTrackableVessel).
        /// </summary>
        internal static bool IsTrackableVesselType(VesselType vesselType)
        {
            return vesselType == VesselType.SpaceObject;
        }

        /// <summary>
        /// Determines whether a vessel is trackable (should get its own tree branch).
        /// Trackable = SpaceObject OR has ModuleCommand (covers crewed pods and probe cores).
        /// Debris, spent boosters, fairings, etc. are NOT trackable.
        /// </summary>
        internal static bool IsTrackableVessel(Vessel v)
        {
            if (v == null) return false;

            // Space objects (asteroids, comets) are always trackable
            if (v.vesselType == VesselType.SpaceObject) return true;

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
                CopyRewindSaveToRoot(activeTree, splitRecorder.CaptureAtStop);
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

            ParsekLog.Info("Flight", $"Tree branch created: type={branchType}, " +
                $"bp={bp.Id}, activeChild={activeChild.RecordingId} (pid={activeChild.VesselPersistentId}), " +
                $"bgChild={bgChild.RecordingId} (pid={bgChild.VesselPersistentId})" +
                (evaCrewName != null ? $", evaCrew={evaCrewName}" : ""));
            ParsekLog.RecState("CreateSplitBranch:exit", CaptureRecorderState());
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
            LedgerOrchestrator.OnRecordingCommitted(recId, sUT, eUT);
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

                double branchUT = Planetarium.GetUniversalTime();

                // Scan for NEW vessels created by the joint break.
                // Only consider vessels whose PID was NOT in the pre-break snapshot,
                // to avoid false matches with unrelated vessels (e.g., space stations).
                uint recordedPid = pendingSplitRecorder.RecordingVesselId;

                // Collect new vessel PIDs for classification, tracking per-vessel controller status
                var newVesselPids = new List<uint>();
                var newVesselHasController = new Dictionary<uint, bool>();
                bool anyNewVesselHasController = false;

                // First, include vessels caught by onPartDeCoupleNewVesselComplete during recording.
                // These may have been destroyed by KSP's debris cleanup before this scan,
                // so we use the controller status captured at creation time (parts may be cleared).
                if (decoupleCreatedVessels != null && decoupleCreatedVessels.Count > 0)
                {
                    ParsekLog.Info("Flight",
                        $"DeferredJointBreakCheck: {decoupleCreatedVessels.Count} vessel(s) caught by onPartDeCouple");

                    for (int i = 0; i < decoupleCreatedVessels.Count; i++)
                    {
                        Vessel v = decoupleCreatedVessels[i];
                        if (v == null || v.persistentId == recordedPid) continue;

                        if (activeTree != null && activeTree.BackgroundMap.ContainsKey(v.persistentId))
                            continue;

                        if (!newVesselPids.Contains(v.persistentId))
                        {
                            newVesselPids.Add(v.persistentId);

                            // Use pre-captured controller status (vessel parts may be gone now)
                            bool hasController = decoupleControllerStatus != null
                                && decoupleControllerStatus.ContainsKey(v.persistentId)
                                && decoupleControllerStatus[v.persistentId];
                            newVesselHasController[v.persistentId] = hasController;
                            if (hasController)
                                anyNewVesselHasController = true;
                        }
                    }
                }

                // Also scan FlightGlobals.Vessels for any new vessels still alive
                if (FlightGlobals.Vessels != null)
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

                // Classify the joint break result
                var classification = SegmentBoundaryLogic.ClassifyJointBreakResult(
                    recordedPid, newVesselPids, anyNewVesselHasController);

                ParsekLog.Info("Coalescer",
                    $"Joint break classified: result={classification}, " +
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
                        Vessel childVessel = FlightRecorder.FindVesselByPid(newPid);
                        if (childVessel != null)
                            preSnapshot = VesselSpawner.TryBackupSnapshot(childVessel);

                        ParsekLog.Info("Coalescer",
                            $"Feeding split to coalescer: childPid={newPid}, childHasController={hasController}" +
                            $", preSnapshot={preSnapshot != null}");
                        crashCoalescer.OnSplitEvent(branchUT, newPid, hasController, preSnapshot: preSnapshot);
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
                preBreakVesselPids = null;
                // Clear consumed decouple vessels (list stays alive for session, just emptied)
                decoupleCreatedVessels?.Clear();
                decoupleControllerStatus?.Clear();
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
                ParsekLog.Info("Coalescer",
                    $"CreateBreakupChildRecording: using pre-captured snapshot for pid={pid} (vessel destroyed)");
            }
            else
            {
                childRec.TerminalStateValue = TerminalState.Destroyed;
                childRec.ExplicitEndUT = breakupBp.UT;
            }

            tree.AddOrReplaceRecording(childRec);
            breakupBp.ChildRecordingIds.Add(childRecId);

            return childRec;
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
            if (controlledChildren != null && controlledChildren.Count > 0)
            {
                for (int i = 0; i < controlledChildren.Count; i++)
                {
                    uint pid = controlledChildren[i];
                    Vessel childVessel = FlightRecorder.FindVesselByPid(pid);
                    ConfigNode ctrlSnap = childVessel == null
                        ? crashCoalescer.GetPreCapturedSnapshot(pid)
                        : null;
                    var childRec = CreateBreakupChildRecording(activeTree, breakupBp, pid, childVessel, false, "Unknown", ctrlSnap, parentGeneration: activeRec.Generation);

                    // Add to BackgroundRecorder for trajectory sampling (no TTL — records indefinitely)
                    if (childVessel != null && backgroundRecorder != null)
                    {
                        activeTree.BackgroundMap[pid] = childRec.RecordingId;
                        backgroundRecorder.OnVesselBackgrounded(pid, breakupEngineState);
                    }

                    ParsekLog.Info("Coalescer",
                        $"ProcessBreakupEvent: controlled child created: pid={pid}, " +
                        $"name='{childRec.VesselName}', recId={childRec.RecordingId}, " +
                        $"alive={childVessel != null}, bgRecorder={backgroundRecorder != null}");
                }
            }

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

                    ConfigNode preSnap = debrisVessel == null
                        ? crashCoalescer.GetPreCapturedSnapshot(pid)
                        : null;
                    var childRec = CreateBreakupChildRecording(activeTree, breakupBp, pid, debrisVessel, true, "Debris", preSnap, parentGeneration: activeRec.Generation);

                    if (debrisVessel != null && backgroundRecorder != null)
                    {
                        activeTree.BackgroundMap[pid] = childRec.RecordingId;
                        backgroundRecorder.OnVesselBackgrounded(pid, breakupEngineState);
                        backgroundRecorder.SetDebrisExpiry(pid, debrisExpiryUT);
                    }

                    ParsekLog.Info("Coalescer",
                        $"ProcessBreakupEvent: debris child created: pid={pid}, " +
                        $"name='{childRec.VesselName}', recId={childRec.RecordingId}, " +
                        $"alive={debrisVessel != null}");
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
            CancelDeferredMerge,
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

            return PostDestructionMergeResolution.CancelDeferredMerge;
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
            if (dockingInProgress.Contains(vesselPid)) return false;
            return !vesselStillExists;
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
            if (!IsTrulyDestroyed(pending.vesselPid, dockingInProgress, vesselStillExists))
            {
                if (dockingInProgress.Contains(pending.vesselPid))
                    Log($"DeferredDestructionCheck: pid={pending.vesselPid} now in dockingInProgress — aborting");
                else
                    Log($"DeferredDestructionCheck: pid={pending.vesselPid} still exists — vessel unloaded, not destroyed");
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

            if (!isPhantomCrash)
                ApplyTerminalDestruction(pending, rec);

            packStates.Remove(pending.vesselPid);
            activeTree.BackgroundMap.Remove(pending.vesselPid);

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

            // Track when the active vessel enters LANDED/SPLASHED for the settle timer
            if (data.host == FlightGlobals.ActiveVessel &&
                (data.to == Vessel.Situations.LANDED || data.to == Vessel.Situations.SPLASHED))
            {
                lastLandedUT = Planetarium.GetUniversalTime();
            }

            if (IsRecording) return;
            if (data.host != FlightGlobals.ActiveVessel)
            {
                ParsekLog.VerboseRateLimited("Flight", "sit-change-other",
                    $"OnVesselSituationChange: ignoring non-active vessel ({data.from} → {data.to})");
                return;
            }

            bool isPrelaunch = data.from == Vessel.Situations.PRELAUNCH;
            bool isSettledLaunch = false;
            if (!isPrelaunch && data.from == Vessel.Situations.LANDED)
            {
                double settledTime = lastLandedUT >= 0
                    ? Planetarium.GetUniversalTime() - lastLandedUT
                    : 0;
                isSettledLaunch = settledTime >= LandedSettleThreshold;
                if (!isSettledLaunch)
                {
                    ParsekLog.VerboseRateLimited("Flight", "sit-change-bounce",
                        $"OnVesselSituationChange: LANDED → {data.to} after {settledTime:F1}s (< {LandedSettleThreshold}s settle threshold)");
                }
            }

            if (!isPrelaunch && !isSettledLaunch)
            {
                if (data.from != Vessel.Situations.LANDED) // already logged above for LANDED bounces
                    ParsekLog.VerboseRateLimited("Flight", "sit-change-not-launch",
                        $"OnVesselSituationChange: not a launch transition ({data.from} → {data.to})");
                return;
            }
            if (ParsekSettings.Current?.autoRecordOnLaunch == false)
            {
                ParsekLog.Verbose("Flight", "OnVesselSituationChange: auto-record disabled in settings");
                return;
            }

            StartRecording();
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
            if (pendingSplitInProgress) return; // split must complete first
            if (data.to?.vessel == null)
            {
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
                if (pendingSplitInProgress) return; // another split is being processed

                // Only trigger if EVA is from the vessel we're recording
                if (data.from?.vessel == null ||
                    data.from.vessel.persistentId != recorder.RecordingVesselId)
                {
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

            if (data.from?.vessel == null)
            {
                ParsekLog.Verbose("Flight", "OnCrewOnEva: source vessel is null — ignoring");
                return;
            }
            if (ParsekSettings.Current?.autoRecordOnEva == false)
            {
                ParsekLog.Verbose("Flight", "OnCrewOnEva: auto-record on EVA disabled in settings");
                return;
            }

            // The EVA kerbal may not yet be the active vessel, defer to Update()
            pendingAutoRecord = true;
            Log($"EVA detected (sit={data.from.vessel.situation}) — pending auto-record");
        }

        internal static string ExtractEvaKerbalName(GameEvents.FromToAction<Part, Part> data)
        {
            if (data.to == null) return null;
            var crew = data.to.protoModuleCrew;
            if (crew != null && crew.Count > 0) return crew[0].name;
            if (data.to.vessel != null) return data.to.vessel.vesselName;
            return null;
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
                double interval = GetLoopIntervalSeconds(rec);
                double effectiveStart = GhostPlaybackEngine.EffectiveLoopStartUT(rec);
                double effectiveEnd = GhostPlaybackEngine.EffectiveLoopEndUT(rec);
                var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                    currentUT, effectiveStart, effectiveEnd, interval);

                ParsekLog.Info("Loop",
                    $"Anchor vessel loaded: pid={pid}, rec #{i} '{rec.VesselName}' " +
                    $"phase cycle={cycleIndex} loopUT={loopUT:F2} paused={isInPause}");

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

            ParsekLog.Verbose("Checkpoint",
                $"Time warp rate changed to {warpRate.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}x " +
                $"at UT={ut:F2} — checkpointing all background vessels");

            // Checkpoint background vessels first (before any state changes)
            backgroundRecorder.CheckpointAllVessels(ut);

            // The active vessel's orbit segments are already handled by
            // onVesselGoOnRails/onVesselGoOffRails events which fire when
            // time warp transitions between physics and rails modes.
            ParsekLog.Verbose("Checkpoint",
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

            // Check crew roster — works for both EVA kerbals and crewed vessels
            var crew = recordedVessel.GetVesselCrew();
            if (crew != null)
            {
                for (int i = 0; i < crew.Count; i++)
                {
                    if (crew[i].name == placedBy)
                        return true;
                }
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
            //   - VesselSwitch (#266): tree was pre-transitioned at stash time, just
            //     reinstall it and (optionally) promote the new active vessel from
            //     BackgroundMap.
            var restoreMode = ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady;
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

            // Safety net: if FlightResultsPatch has a pending message that was never replayed
            // (e.g., tree destruction path didn't fire), replay it now.
            if (Patches.FlightResultsPatch.HasPendingResults())
            {
                ParsekLog.Warn("Flight", "FlightResults safety net: replaying suppressed results on OnFlightReady");
                Patches.FlightResultsPatch.ReplayFlightResults("OnFlightReady safety net");
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
            // Clear dock/undock pending state
            ClearDockUndockState();

            // Clear merge event detection state
            pendingTreeDockMerge = false;
            pendingBoardingTargetInTree = false;
            dockingInProgress.Clear();
            treeDestructionDialogPending = false;

            // Clear any suppressed flight results — don't carry stale crash reports across scenes.
            if (Patches.FlightResultsPatch.HasPendingResults())
            {
                ParsekLog.Info("Flight", "Clearing suppressed FlightResults on scene change");
                Patches.FlightResultsPatch.ClearPending("scene change with no merge owner");
            }
            else if (Patches.FlightResultsPatch.DeferredMergeArmed)
            {
                ParsekLog.Info("Flight",
                    "Clearing armed FlightResults suppression on scene change before any stock dialog was intercepted");
                Patches.FlightResultsPatch.ClearPending(
                    "scene change before stock FlightResults were intercepted");
            }

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
                ParsekLog.ScreenMessage("Recording stopped \u2014 vessel changed", 3f);
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
            Recording oldRec;
            uint bgPidUpdate = 0;
            if (oldRecId != null && activeTree.Recordings.TryGetValue(oldRecId, out oldRec)
                && oldRec.VesselPersistentId != 0)
            {
                activeTree.BackgroundMap[oldRec.VesselPersistentId] = oldRecId;
                bgPidUpdate = oldRec.VesselPersistentId;
            }
            activeTree.ActiveRecordingId = null;
            recorder = null;
            if (bgPidUpdate != 0) backgroundRecorder?.OnVesselBackgrounded(bgPidUpdate);
            Log($"Tree: recorder flushed to background (recId={oldRecId})");
        }

        /// <summary>
        /// Handles joint break deferred check: stops recorder and starts deferred vessel-creation check
        /// when FlightRecorder signals a joint break.
        /// </summary>
        private void HandleJointBreakDeferredCheck()
        {
            if (pendingSplitInProgress || recorder == null || !recorder.IsRecording
                || !recorder.ConsumePendingJointBreakCheck())
                return;

            // Snapshot existing vessel PIDs so the deferred check can identify NEW vessels
            preBreakVesselPids = new HashSet<uint>();
            if (FlightGlobals.Vessels != null)
            {
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

            Log("Joint break detected \u2014 starting deferred vessel split check");
            StartCoroutine(DeferredJointBreakCheck());
        }

        /// <summary>
        /// Callback for GameEvents.onVesselCreate during the split check window.
        /// Captures vessels immediately at creation, before KSP's debris cleanup can destroy them.
        /// </summary>
        /// <summary>
        /// Callback for GameEvents.onPartDeCoupleNewVesselComplete during the split check window.
        /// Fires synchronously inside Part.decouple(), before KSP's debris cleanup can destroy the vessel.
        /// </summary>
        private void OnDecoupleNewVesselDuringSplitCheck(Vessel originalVessel, Vessel newVessel)
        {
            if (newVessel == null) return;
            // Only capture vessels that didn't exist before the break
            if (preBreakVesselPids != null && preBreakVesselPids.Contains(newVessel.persistentId))
                return;
            decoupleCreatedVessels?.Add(newVessel);
            // Capture controller status NOW — vessel parts may be cleared by the deferred check
            bool hasController = IsTrackableVessel(newVessel);
            if (decoupleControllerStatus != null)
                decoupleControllerStatus[newVessel.persistentId] = hasController;
            ParsekLog.Info("Flight",
                $"Decouple created vessel during split check: pid={newVessel.persistentId} " +
                $"name='{newVessel.vesselName}' type={newVessel.vesselType} " +
                $"parts={newVessel.parts?.Count ?? 0} hasController={hasController}");
        }

        /// <summary>
        /// Handles deferred auto-record for EVA: starts recording when vessel switch
        /// to the EVA kerbal is complete.
        /// </summary>
        private void HandleDeferredAutoRecordEva()
        {
            if (!pendingAutoRecord || IsRecording ||
                FlightGlobals.ActiveVessel == null || !FlightGlobals.ActiveVessel.isEVA)
                return;

            StartRecording();

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
                    ScreenMessage("Recording STARTED (auto \u2014 EVA from pad)", 2f);
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

        public void StartRecording()
        {
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

                activeTree.PreTreeFunds = Funding.Instance != null ? Funding.Instance.Funds : 0;
                activeTree.PreTreeScience = ResearchAndDevelopment.Instance != null
                    ? ResearchAndDevelopment.Instance.Science : 0;
                activeTree.PreTreeReputation = Reputation.Instance != null
                    ? Reputation.Instance.reputation : 0;

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
            recorder.StartRecording(isPromotion: isContinuation);
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

            // Reset crash coalescer on new recording start to clear any stale state
            if (crashCoalescer != null)
            {
                crashCoalescer.Reset();
                ParsekLog.Verbose("Coalescer", "Coalescer reset on recording start");
            }

            // Enforce minimum debris persistence so decoupled stages survive long enough
            // to be detected as vessel splits and recorded as background vessels.
            EnforceMinDebrisPersistence();

            // Subscribe to decouple events for the entire recording session.
            // onPartDeCoupleNewVesselComplete fires synchronously in Part.decouple() (FixedUpdate),
            // which is the same frame as the joint break. Subscribing only during the split check
            // window (Update) misses the initial decouple because FixedUpdate runs before Update.
            decoupleCreatedVessels = new List<Vessel>();
            decoupleControllerStatus = new Dictionary<uint, bool>();
            GameEvents.onPartDeCoupleNewVesselComplete.Add(OnDecoupleNewVesselDuringSplitCheck);

            uint pid = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.persistentId : 0;
            ParsekLog.Info("Flight", $"StartRecording succeeded: pid={pid}, chainActive={chainManager.ActiveChainId != null}, tree={activeTree != null}");
            ParsekLog.RecState("StartRecording:post", CaptureRecorderState());
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
            }
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

            // Tree resources are already live in the game — mark as applied
            activeTree.ResourcesApplied = true;

            // Mark all tree recordings' resource indices as fully applied
            int markedCount = 0;
            foreach (var rec in activeTree.Recordings.Values)
            {
                if (rec.Points.Count > 0)
                {
                    rec.LastAppliedResourceIndex = rec.Points.Count - 1;
                    markedCount++;
                }
            }
            ParsekLog.Info("Flight",
                $"CommitTreeFlight: ResourcesApplied=true, marked {markedCount}/{activeTree.Recordings.Count} recordings as fully applied");

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

            // Re-attach the BackgroundRecorder for the tree
            if (backgroundRecorder == null)
            {
                backgroundRecorder = new BackgroundRecorder(activeTree);
                backgroundRecorder.SubscribePartEvents();
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = backgroundRecorder;
            }

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
        ///   returned to a vessel that's part of this tree), promote it via the existing
        ///   <see cref="PromoteRecordingFromBackground"/> path.</item>
        ///   <item>Otherwise leave <c>recorder = null</c> — the new vessel is an outsider
        ///   from this tree's perspective, identical to the in-session
        ///   <c>OnVesselSwitchComplete</c> outcome at lines 1539–1543 when
        ///   <c>BackgroundMap.TryGetValue</c> returns false.</item>
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
            if (backgroundRecorder == null)
            {
                backgroundRecorder = new BackgroundRecorder(activeTree);
                backgroundRecorder.SubscribePartEvents();
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = backgroundRecorder;
            }

            // Inspect the new active vessel for promotion.
            var newActive = FlightGlobals.ActiveVessel;
            if (newActive == null)
            {
                // Wait budget exhausted with no ActiveVessel: log Warn so playtest
                // logs surface the missed promotion. The tree is reinstalled but
                // any potential round-trip cannot be tested at this point.
                // Subsequent OnVesselSwitchComplete events from the live game
                // will still drive promotion via the in-session path.
                ParsekLog.Warn("Flight",
                    $"RestoreActiveTreeFromPendingForVesselSwitch: tree '{activeTree.TreeName}' reinstalled, " +
                    $"but no active vessel resolved within {WaitBudgetSeconds:F0}s wait window " +
                    $"(timedOut={waitTimedOut}) — outsider state, recorder stays null. Round-trip " +
                    "promotion (if any) will be missed; subsequent OnVesselSwitchComplete will recover.");
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
                // switch, the round-trip promotion will silently fail and the player
                // will land in the outsider branch — visible as a missing
                // "promoted vessel" log line.
                uint newPid = newActive.persistentId;
                if (activeTree.BackgroundMap.TryGetValue(newPid, out string bgRecId))
                {
                    // Round-trip case: the player returned to a vessel that this tree
                    // is already tracking. Promote it via the existing in-session path.
                    backgroundRecorder?.OnVesselRemovedFromBackground(newPid);
                    PromoteRecordingFromBackground(bgRecId, newActive);
                    ParsekLog.Info("Flight",
                        $"RestoreActiveTreeFromPendingForVesselSwitch: promoted vessel '{newActive.vesselName}' " +
                        $"(pid={newPid}) from BackgroundMap — round-trip via TS / scene-reload switch");
                }
                else
                {
                    // Outsider case: the new active vessel has no recording context in
                    // this tree. recorder stays null. Future switches back to a tree
                    // member will trigger OnVesselSwitchComplete → PromoteFromBackground.
                    ParsekLog.Info("Flight",
                        $"RestoreActiveTreeFromPendingForVesselSwitch: tree '{activeTree.TreeName}' reinstalled " +
                        $"as outsider for active vessel '{newActive.vesselName}' (pid={newPid}, not in " +
                        $"BackgroundMap) — recorder stays null until next switch promotes a tree member");
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
                CopyRewindSaveToRoot(activeTree, recorder.CaptureAtStop,
                    recorderFallbackSave: recorder.RewindSaveFileName,
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
                CopyRewindSaveToRoot(activeTree, recorder.CaptureAtStop,
                    recorderFallbackSave: recorder.RewindSaveFileName,
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
                CopyRewindSaveToRoot(tree, recorder.CaptureAtStop,
                    recorderFallbackSave: recorder.RewindSaveFileName,
                    logTag: "FinalizeTreeRecordings");
            }

            // 2. Finalize background recorder (close orbit segments, flush data)
            if (backgroundRecorder != null)
                backgroundRecorder.FinalizeAllForCommit(commitUT);

            // 3. Process each recording in the tree
            foreach (var kvp in tree.Recordings)
                FinalizeIndividualRecording(kvp.Value, commitUT, isSceneExit);

            // 3b. Ensure active recording has terminalState even if non-leaf.
            // In tree mode, the active recording may have debris branches (non-leaf)
            // so FinalizeIndividualRecording skips its terminalState. The optimizer
            // will propagate this to the chain tip via SplitAtSection.
            EnsureActiveRecordingTerminalState(tree);

            // 4. Prune zero-point debris leaves (#173) — removes recordings with no
            // trajectory data that were created from same-frame destruction debris.
            PruneZeroPointLeaves(tree);

            // Compute tree-level resource delta
            tree.DeltaFunds = ComputeTreeDeltaFunds(tree);
            tree.DeltaScience = ComputeTreeDeltaScience(tree);
            tree.DeltaReputation = ComputeTreeDeltaReputation(tree);

            ParsekLog.Info("Flight",
                $"FinalizeTreeRecordings: tree '{tree.TreeName}' resource delta: " +
                $"funds={tree.DeltaFunds:+0.0;-0.0}, science={tree.DeltaScience:+0.0;-0.0}, " +
                $"rep={tree.DeltaReputation:+0.0;-0.0}");
        }

        /// <summary>
        /// Sets terminal state on the active recording even if it is a non-leaf node.
        /// FinalizeIndividualRecording skips non-leaves, but the active recording needs
        /// terminal state for the optimizer's SplitAtSection propagation.
        /// </summary>
        internal static void EnsureActiveRecordingTerminalState(RecordingTree tree)
        {
            if (string.IsNullOrEmpty(tree.ActiveRecordingId))
                return;

            Recording activeRec;
            if (!tree.Recordings.TryGetValue(tree.ActiveRecordingId, out activeRec))
                return;

            if (activeRec.TerminalStateValue.HasValue)
            {
                ParsekLog.Verbose("Flight",
                    $"FinalizeTreeRecordings: active recording '{activeRec.RecordingId}' " +
                    $"already has terminalState={activeRec.TerminalStateValue} — skipping");
                return;
            }

            Vessel v = activeRec.VesselPersistentId != 0
                ? FlightRecorder.FindVesselByPid(activeRec.VesselPersistentId)
                : null;
            if (v != null)
            {
                activeRec.TerminalStateValue =
                    RecordingTree.DetermineTerminalState((int)v.situation, v);
                ParsekLog.Info("Flight",
                    $"FinalizeTreeRecordings: set terminalState=" +
                    $"{activeRec.TerminalStateValue} on active recording " +
                    $"'{activeRec.RecordingId}' (non-leaf, vessel situation={v.situation})");
            }
        }

        internal static void FinalizeIndividualRecording(Recording rec, double commitUT, bool isSceneExit)
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

            if (isLeaf && rec.VesselPersistentId != 0 && finalizeVessel == null)
                ParsekLog.Verbose("Flight",
                    $"FinalizeIndividualRecording: vessel pid={rec.VesselPersistentId} not found " +
                    $"for '{rec.RecordingId}' (isSceneExit={isSceneExit}) — re-snapshot will be skipped");

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
                else if (isSceneExit)
                {
                    // Scene exit: vessel unloaded (alive) but not findable. Recordings
                    // for truly destroyed vessels already have TerminalStateValue set by
                    // DeferredDestructionCheck / ApplyDestroyedFallback before finalization.
                    // Reaching here without a terminal state means the vessel was alive
                    // when unloaded. Infer terminal state from the last trajectory point.
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
                    if ((inferredState == TerminalState.Landed || inferredState == TerminalState.Splashed)
                        && rec.Points.Count > 0)
                    {
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
            if (isLeaf && rec.TerminalStateValue.HasValue && finalizeVessel != null)
            {
                var ts = rec.TerminalStateValue.Value;
                bool stableTerminal = ts == TerminalState.Landed
                    || ts == TerminalState.Splashed
                    || ts == TerminalState.Orbiting;
                if (stableTerminal)
                {
                    ConfigNode freshSnapshot = VesselSpawner.TryBackupSnapshot(finalizeVessel);
                    if (freshSnapshot != null)
                    {
                        rec.VesselSnapshot = freshSnapshot;
                        if (rec.GhostVisualSnapshot == null)
                            rec.GhostVisualSnapshot = freshSnapshot.CreateCopy();
                        rec.MarkFilesDirty();  // Critical: ensures next SaveRecordingFiles writes the fresh snapshot to disk
                        ParsekLog.Info("Flight",
                            $"FinalizeIndividualRecording: re-snapshotted '{rec.RecordingId}' " +
                            $"with stable terminal state {ts} " +
                            $"(vessel.situation={finalizeVessel.situation}, isSceneExit={isSceneExit}) [#289]");
                    }
                    else
                    {
                        ParsekLog.Verbose("Flight",
                            $"FinalizeIndividualRecording: re-snapshot returned null for " +
                            $"'{rec.RecordingId}' (terminal={ts}, isSceneExit={isSceneExit}) — keeping stale snapshot");
                    }
                }
            }

            // Backfill terminal orbit for recordings that had TerminalStateValue set early
            // (ChainSegmentManager, BackgroundRecorder) without
            // a corresponding CaptureTerminalOrbit call. (#203/#219 regression)
            if (isLeaf && string.IsNullOrEmpty(rec.TerminalOrbitBody)
                && rec.TerminalStateValue.HasValue
                && (rec.TerminalStateValue.Value == TerminalState.Orbiting
                    || rec.TerminalStateValue.Value == TerminalState.SubOrbital
                    || rec.TerminalStateValue.Value == TerminalState.Docked))
            {
                Vessel v = rec.VesselPersistentId != 0
                    ? FlightRecorder.FindVesselByPid(rec.VesselPersistentId)
                    : null;
                // Try live vessel first — CaptureTerminalOrbit silently no-ops if the
                // vessel's orbit is null or its situation doesn't match the accepted set
                // (ORBITING/SUB_ORBITAL/FLYING/ESCAPING). In that case we still need a
                // fallback, so we check TerminalOrbitBody AFTER the live capture attempt.
                if (v != null)
                    CaptureTerminalOrbit(rec, v);

                if (!string.IsNullOrEmpty(rec.TerminalOrbitBody))
                {
                    // Live capture succeeded
                    ParsekLog.Info("Flight",
                        $"FinalizeIndividualRecording: backfilled TerminalOrbitBody={rec.TerminalOrbitBody} " +
                        $"for '{rec.RecordingId}' from live vessel (terminal={rec.TerminalStateValue})");
                }
                else
                {
                    // Live capture either didn't happen (v == null) or silently declined.
                    // Try the last orbit segment from BackgroundRecorder sampling.
                    PopulateTerminalOrbitFromLastSegment(rec);
                    if (!string.IsNullOrEmpty(rec.TerminalOrbitBody))
                    {
                        ParsekLog.Info("Flight",
                            $"FinalizeIndividualRecording: backfilled TerminalOrbitBody={rec.TerminalOrbitBody} " +
                            $"for '{rec.RecordingId}' via orbit-segment fallback " +
                            $"(terminal={rec.TerminalStateValue}, vesselFound={v != null})");
                    }
                    else
                    {
                        // Both sources declined. Recording commits with terminal state set
                        // but no orbital metadata — ghost map presence will be degraded.
                        // Loud warn so we notice in the playtest logs.
                        ParsekLog.Warn("Flight",
                            $"FinalizeIndividualRecording: backfill declined for '{rec.RecordingId}' " +
                            $"(terminal={rec.TerminalStateValue}, vesselFound={v != null}, " +
                            $"orbitSegments={rec.OrbitSegments?.Count ?? 0}) — " +
                            "TerminalOrbitBody remains empty");
                    }
                }
            }

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
        /// Removes zero-point leaf recordings from the tree (#173). These empty leaves have no
        /// trajectory data, no orbit segments, and no surface position. They can be same-frame
        /// debris fragments or transient placeholders left behind by split/finalize edge cases.
        /// </summary>
        internal static void PruneZeroPointLeaves(RecordingTree tree)
        {
            var toPrune = CollectZeroPointLeafIds(tree);
            if (toPrune == null || toPrune.Count == 0)
                return;

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
                $"PruneZeroPointLeaves: removed {toPrune.Count} zero-point leaf recording(s)" +
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

        internal static double ComputeTreeDeltaFunds(RecordingTree tree)
        {
            double currentFunds = 0;
            try { if (Funding.Instance != null) currentFunds = Funding.Instance.Funds; } catch { }
            return currentFunds - tree.PreTreeFunds;
        }

        internal static double ComputeTreeDeltaScience(RecordingTree tree)
        {
            double currentScience = 0;
            try { if (ResearchAndDevelopment.Instance != null) currentScience = ResearchAndDevelopment.Instance.Science; } catch { }
            return currentScience - tree.PreTreeScience;
        }

        internal static float ComputeTreeDeltaReputation(RecordingTree tree)
        {
            float currentRep = 0;
            try { if (Reputation.Instance != null) currentRep = Reputation.Instance.reputation; } catch { }
            return currentRep - tree.PreTreeReputation;
        }

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

                // Before spawning, recover any timeline-spawned vessel with the same name
                // to prevent overlap collisions (e.g. an older committed recording already
                // spawned the same vessel on the pad).
                RecoverTimelineSpawnedVessel(leaf.VesselName);

                try
                {
                    // Correct unsafe snapshot situation before spawning (#169)
                    VesselSpawner.CorrectUnsafeSnapshotSituation(leaf.VesselSnapshot, leaf.TerminalStateValue);

                    // Clamp altitude for surface terminals (#231). The snapshot position may be
                    // from mid-flight. Without clamping, the vessel spawns in the air and crashes.
                    if (leaf.Points.Count > 0
                        && (leaf.TerminalStateValue == TerminalState.Landed
                            || leaf.TerminalStateValue == TerminalState.Splashed))
                    {
                        var lastPt = leaf.Points[leaf.Points.Count - 1];
                        double spawnLat, spawnLon, spawnAlt;
                        VesselSpawner.ResolveSpawnPosition(leaf, -1, lastPt,
                            out spawnLat, out spawnLon, out spawnAlt);
                        VesselSpawner.OverrideSnapshotPosition(leaf.VesselSnapshot, spawnLat, spawnLon, spawnAlt,
                            -1, leaf.VesselName, lastPt.rotation);
                    }

                    uint spawnedPid = VesselSpawner.RespawnVessel(leaf.VesselSnapshot);
                    if (spawnedPid != 0)
                    {
                        leaf.VesselSpawned = true;
                        leaf.SpawnedVesselPersistentId = spawnedPid;
                        leaf.LastAppliedResourceIndex = leaf.Points.Count - 1;
                        ParsekLog.Info("Flight", $"SpawnTreeLeaves: spawned leaf '{leaf.VesselName}' pid={spawnedPid}");
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
                rec.TerminalOrbitInclination = orb.inclination;
                rec.TerminalOrbitEccentricity = orb.eccentricity;
                rec.TerminalOrbitSemiMajorAxis = orb.semiMajorAxis;
                rec.TerminalOrbitLAN = orb.LAN;
                rec.TerminalOrbitArgumentOfPeriapsis = orb.argumentOfPeriapsis;
                rec.TerminalOrbitMeanAnomalyAtEpoch = orb.meanAnomalyAtEpoch;
                rec.TerminalOrbitEpoch = orb.epoch;
                rec.TerminalOrbitBody = orb.referenceBody?.name ?? "Kerbin";
            }
        }

        /// <summary>
        /// Populates terminal orbit fields from the last OrbitSegment when the vessel
        /// is already destroyed at finalization time. Enables ghost map presence for
        /// orbital debris whose vessel expired before CaptureTerminalOrbit could run. (#219)
        /// </summary>
        internal static void PopulateTerminalOrbitFromLastSegment(Recording rec)
        {
            if (!rec.HasOrbitSegments) return;
            if (!string.IsNullOrEmpty(rec.TerminalOrbitBody)) return; // already populated

            var seg = rec.OrbitSegments[rec.OrbitSegments.Count - 1];
            rec.TerminalOrbitInclination = seg.inclination;
            rec.TerminalOrbitEccentricity = seg.eccentricity;
            rec.TerminalOrbitSemiMajorAxis = seg.semiMajorAxis;
            rec.TerminalOrbitLAN = seg.longitudeOfAscendingNode;
            rec.TerminalOrbitArgumentOfPeriapsis = seg.argumentOfPeriapsis;
            rec.TerminalOrbitMeanAnomalyAtEpoch = seg.meanAnomalyAtEpoch;
            rec.TerminalOrbitEpoch = seg.epoch;
            rec.TerminalOrbitBody = seg.bodyName;

            ParsekLog.Info("Flight",
                $"PopulateTerminalOrbitFromLastSegment: recovered orbit for '{rec.RecordingId}' " +
                $"from segment body={seg.bodyName} sma={seg.semiMajorAxis:F1}");
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

                    previewGhostState.reentryFxInfo = GhostVisualBuilder.TryBuildReentryFx(
                        ghost, previewGhostState.heatInfos, -1, previewRecording.VesselName);

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
                    ParsekLog.Info("ExplosionFx",
                        $"Manual preview explosion for \"{previewRecording.VesselName}\" " +
                        $"at ({pos.x:F1},{pos.y:F1},{pos.z:F1}) vesselLength={len:F1}m");
                    // Hide ghost parts before explosion renders
                    if (previewGhostState != null)
                        GhostPlaybackLogic.HideAllGhostParts(previewGhostState);
                    else
                    {
                        var t = ghostObject.transform;
                        for (int c = 0; c < t.childCount; c++)
                            t.GetChild(c).gameObject.SetActive(false);
                    }
                    var explosion = GhostVisualBuilder.SpawnExplosionFx(pos, len);
                    if (explosion != null)
                        activeExplosions.Add(explosion);
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
        /// Physics-bubble scoping for deferred spawns: returns true if the recording's
        /// endpoint is more than 2300m from the active vessel (outside physics bubble).
        /// Pure decision method for testability.
        /// </summary>
        internal static bool ShouldDeferSpawnOutsideBubble(Recording rec, Vector3d activeVesselPos)
        {
            if (rec == null) return false;

            // Compute endpoint position from recording
            Vector3d endpointPos = ComputeEndpointWorldPosition(rec);
            if (endpointPos == Vector3d.zero) return false; // No position data — allow spawn

            double distance = Vector3d.Distance(activeVesselPos, endpointPos);
            return distance > PhysicsBubbleSpawnRadius;
        }

        /// <summary>Physics bubble radius for spawn scoping (meters).</summary>
        internal const double PhysicsBubbleSpawnRadius = DistanceThresholds.PhysicsBubbleMeters;

        /// <summary>
        /// Computes world position for a recording's endpoint using last trajectory point
        /// or terminal position. Requires KSP runtime (CelestialBody lookups).
        /// Returns Vector3d.zero if no position data is available.
        /// </summary>
        private static Vector3d ComputeEndpointWorldPosition(Recording rec)
        {
            // Terminal surface position
            if (rec.TerminalPosition.HasValue)
            {
                var tp = rec.TerminalPosition.Value;
                CelestialBody body = FlightGlobals.GetBodyByName(tp.body);
                if (body != null)
                    return body.GetWorldSurfacePosition(tp.latitude, tp.longitude, tp.altitude);
            }

            // Last trajectory point
            if (rec.Points != null && rec.Points.Count > 0)
            {
                var last = rec.Points[rec.Points.Count - 1];
                string bodyName = last.bodyName;
                if (string.IsNullOrEmpty(bodyName)) bodyName = "Kerbin";
                CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
                if (body != null)
                    return body.GetWorldSurfacePosition(last.latitude, last.longitude, last.altitude);
            }

            return Vector3d.zero;
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
            // Real vessel dedup: if a vessel with this PID still exists in the game world,
            // skip spawning a duplicate. This runs only at actual spawn time (pastChainEnd),
            // not every frame like ShouldSpawnAtRecordingEnd.
            // Exceptions that bypass dedup (spawn a fresh copy with new PID):
            //   - Recording PID matches scene-entry active vessel (stateless revert detection)
            //   - Current active vessel shares PID (covers mid-scene vessel switches)
            bool matchesSceneEntryVessel = rec.VesselPersistentId != 0 &&
                rec.VesselPersistentId == RecordingStore.SceneEntryActiveVesselPid;
            bool activeVesselSharesPid = FlightGlobals.ActiveVessel != null &&
                FlightGlobals.ActiveVessel.persistentId == rec.VesselPersistentId;
            if (!matchesSceneEntryVessel && !activeVesselSharesPid &&
                rec.VesselPersistentId != 0 && GhostPlaybackLogic.RealVesselExists(rec.VesselPersistentId))
            {
                rec.VesselSpawned = true;
                rec.SpawnedVesselPersistentId = rec.VesselPersistentId;
                ParsekLog.Info("Flight",
                    $"Spawn skipped: #{index} \"{rec.VesselName}\" — real vessel pid={rec.VesselPersistentId} already exists");
                return;
            }

            GhostChain chain = FindChainTipForRecording(activeGhostChains, rec);
            if (chain != null && vesselGhoster != null)
            {
                if (chain.SpawnBlocked)
                {
                    // Chain was previously blocked — retry at propagated position
                    double currentUT = Planetarium.GetUniversalTime();
                    uint spawnedPid = vesselGhoster.TrySpawnBlockedChain(chain, currentUT);
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
                    uint spawnedPid = vesselGhoster.SpawnAtChainTip(chain);
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
                VesselSpawner.SpawnOrRecoverIfTooClose(rec, index);
            }
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
                bool hasData = rec.Points.Count >= 2 || rec.OrbitSegments.Count > 0 || rec.SurfacePos.HasValue;

                bool isActiveChain = chainManager.ActiveChainId != null && rec.ChainId == chainManager.ActiveChainId;
                bool chainLoopOrDisabled = rec.IsChainRecording &&
                    (RecordingStore.IsChainLooping(rec.ChainId)
                     || RecordingStore.IsChainFullyDisabled(rec.ChainId));

                bool externalVesselSuppressed = GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                    rec.TreeId, rec.VesselPersistentId, IsActiveTreeRecording(rec));

                var spawnResult = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                    rec, isActiveChain, chainLoopOrDisabled);

                var chainSuppressed = activeGhostChains != null
                    ? GhostPlaybackLogic.ShouldSuppressSpawnForChain(activeGhostChains, rec)
                    : (suppressed: false, reason: "");

                bool finalNeedsSpawn = spawnResult.needsSpawn && !chainSuppressed.suppressed;

                // Log spawn suppression reason for non-debris recordings (diagnostic)
                if (!finalNeedsSpawn && !rec.IsDebris)
                {
                    string reason = !spawnResult.needsSpawn ? spawnResult.reason : chainSuppressed.reason;
                    ParsekLog.VerboseRateLimited("Spawner", "spawn-suppressed-" + i,
                        $"Spawn suppressed for #{i} \"{rec.VesselName}\": {reason}");
                }

                flags[i] = new TrajectoryPlaybackFlags
                {
                    skipGhost = !hasData || !rec.PlaybackEnabled || externalVesselSuppressed,
                    isMidChain = RecordingStore.IsChainMidSegment(rec),
                    chainEndUT = RecordingStore.GetChainEndUT(rec),
                    needsSpawn = finalNeedsSpawn,
                    isActiveChainMember = isActiveChain,
                    isChainLoopingOrDisabled = chainLoopOrDisabled,
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
                    ?? GhostPlaybackLogic.DefaultLoopIntervalSeconds,
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
                for (int fi = 0; fi < committed.Count; fi++)
                {
                    if (committed[fi].RecordingId == ffId && watchMode.HasActiveGhost(fi))
                    {
                        watchMode.EnterWatchMode(fi);
                        ParsekLog.Info("CameraFollow", $"Deferred FF watch entered: #{fi}");
                        break;
                    }
                }
            }

            // Retry held ghost spawns (#96): ghosts held at final position while
            // waiting for a blocked/deferred spawn to resolve
            policy.RetryHeldGhostSpawns();

            // Create deferred ghost map ProtoVessels when ghosts enter orbital segments
            policy.CheckPendingMapVessels(Planetarium.GetUniversalTime());

            // Per-frame resource deltas (policy concern, not engine).
            // Intentional: deltas accrue for both in-range AND past-end recordings
            // regardless of whether a ghost is visually active. Resource replay must
            // track the full timeline even when the ghost is disabled or suppressed,
            // so career funds/science/reputation stay consistent with recorded history.
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (!rec.ManagesOwnResources) continue; // tree recordings handle resources at tree level
                if (currentUT > rec.EndUT || (currentUT >= rec.StartUT && currentUT <= rec.EndUT))
                    ApplyResourceDeltas(rec, currentUT);
            }

            // Tree-level resource deltas
            ApplyTreeResourceDeltas(currentUT);

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

        private double GetLoopIntervalSeconds(Recording rec)
        {
            double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                    ?? GhostPlaybackLogic.DefaultLoopIntervalSeconds;
            return engine.GetLoopIntervalSeconds(rec, globalInterval);
        }

        private bool TryComputeLoopPlaybackUT(
            Recording rec, double currentUT,
            out double loopUT, out long cycleIndex, out bool inPauseWindow,
            int recIdx = -1)
        {
            double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                    ?? GhostPlaybackLogic.DefaultLoopIntervalSeconds;
            return engine.TryComputeLoopPlaybackUT(rec, currentUT, globalInterval,
                out loopUT, out cycleIndex, out inPauseWindow, recIdx);
        }


        // UpdateLoopingTimelinePlayback removed (T25 Phase 9)

        /// <summary>
        /// Multi-cycle overlap path for negative intervals. Multiple ghosts from
        /// different cycles may be visible simultaneously.
        /// </summary>
        // UpdateOverlapLoopPlayback removed (T25 Phase 9)

        /// <summary>
        /// Apply resource deltas (funds, science, reputation) for recorded points
        /// that we've passed since the last frame. Computes delta between consecutive
        /// points and adds it to the current career pools.
        /// </summary>
        void ApplyResourceDeltas(Recording rec, double currentUT)
        {
            if (GhostPlaybackLogic.ShouldPauseTimelineResourceReplay(IsRecording))
            {
                if (!timelineResourceReplayPausedLogged)
                {
                    Log("Timeline resource replay paused while a manual recording is active");
                    timelineResourceReplayPausedLogged = true;
                }
                return;
            }
            if (timelineResourceReplayPausedLogged)
            {
                Log("Timeline resource replay resumed after manual recording stopped");
                timelineResourceReplayPausedLogged = false;
            }

            var points = rec.Points;

            // Find the highest point index whose UT we've passed
            int targetIndex = GhostPlaybackLogic.ComputeTargetResourceIndex(points, rec.LastAppliedResourceIndex, currentUT);

            // Apply deltas for each point we've newly passed
            if (targetIndex > rec.LastAppliedResourceIndex)
            {
                int startIdx = Math.Max(rec.LastAppliedResourceIndex, 0);
                TrajectoryPoint fromPoint = points[startIdx];
                TrajectoryPoint toPoint = points[targetIndex];

                double fundsDelta = toPoint.funds - fromPoint.funds;
                float scienceDelta = toPoint.science - fromPoint.science;
                float repDelta = toPoint.reputation - fromPoint.reputation;

                // Suppress game state recording during replay — these are replayed
                // deltas from a previous recording, not new player actions
                using (SuppressionGuard.Resources())
                {
                    if (fundsDelta != 0 && Funding.Instance != null)
                    {
                        if (fundsDelta < 0 && Funding.Instance.Funds + fundsDelta < 0)
                            fundsDelta = -Funding.Instance.Funds;
                        Funding.Instance.AddFunds(fundsDelta, TransactionReasons.None);
                        Log($"Timeline resource: funds {fundsDelta:+0.0;-0.0}");
                    }

                    if (scienceDelta != 0 && ResearchAndDevelopment.Instance != null)
                    {
                        if (scienceDelta < 0 && ResearchAndDevelopment.Instance.Science + scienceDelta < 0)
                            scienceDelta = -ResearchAndDevelopment.Instance.Science;
                        ResearchAndDevelopment.Instance.AddScience(scienceDelta, TransactionReasons.None);
                        Log($"Timeline resource: science {scienceDelta:+0.0;-0.0}");
                    }

                    if (repDelta != 0 && Reputation.Instance != null)
                    {
                        if (repDelta < 0 && Reputation.CurrentRep + repDelta < 0)
                            repDelta = -Reputation.CurrentRep;
                        Reputation.Instance.AddReputation(repDelta, TransactionReasons.None);
                        Log($"Timeline resource: reputation {repDelta:+0.0;-0.0}");
                    }
                }

                rec.LastAppliedResourceIndex = targetIndex;

                // Log summary when all resource deltas have been applied
                if (targetIndex == points.Count - 1 && startIdx < targetIndex)
                {
                    Log($"Resource deltas complete for \"{rec.VesselName}\" (recorded totals): " +
                        $"funds={toPoint.funds - points[0].funds:+0.0;-0.0}, " +
                        $"science={toPoint.science - points[0].science:+0.0;-0.0}, " +
                        $"rep={toPoint.reputation - points[0].reputation:+0.0;-0.0}");
                }
            }
        }

        void ApplyTreeResourceDeltas(double currentUT)
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees.Count == 0) return;

            // Fast path: skip per-tree iteration if all trees are already applied.
            bool anyPending = false;
            for (int i = 0; i < trees.Count; i++)
            {
                if (!trees[i].ResourcesApplied) { anyPending = true; break; }
            }
            if (!anyPending) return;

            for (int i = 0; i < trees.Count; i++)
            {
                var tree = trees[i];
                if (tree.ResourcesApplied)
                    continue;

                double treeEndUT = tree.ComputeEndUT();

                // Skip degraded trees (all recordings have 0 points — trajectory files missing).
                // These have stale delta values from metadata but no actual playback data,
                // so applying their budget would incorrectly charge the player.
                if (treeEndUT == 0)
                {
                    tree.ResourcesApplied = true;
                    ParsekLog.Info("Flight",
                        $"ApplyTreeResourceDeltas: skipping degraded tree '{tree.TreeName}' — no trajectory data (0 points)");
                    continue;
                }

                if (currentUT <= treeEndUT)
                {
                    ParsekLog.VerboseRateLimited("Flight", "tree-res-wait-ut",
                        $"ApplyTreeResourceDeltas: waiting for tree '{tree.TreeName}' — currentUT={currentUT:F1} treeEndUT={treeEndUT:F1}");
                    continue;
                }

                // Apply lump sum delta (sets ResourcesApplied = true internally)
                ApplyTreeLumpSum(tree);
            }
        }

        void ApplyTreeLumpSum(RecordingTree tree)
        {
            if (GhostPlaybackLogic.ShouldPauseTimelineResourceReplay(IsRecording))
                return; // retry next frame

            using (SuppressionGuard.Resources())
            {
                if (tree.DeltaFunds != 0 && Funding.Instance != null)
                {
                    double delta = tree.DeltaFunds;
                    double originalFunds = delta;
                    if (delta < 0 && Funding.Instance.Funds + delta < 0)
                        delta = -Funding.Instance.Funds;
                    if (delta != originalFunds)
                        Log($"ApplyTreeLumpSum: funds clamped for '{tree.TreeName}': original={originalFunds:+0.0;-0.0} → clamped={delta:+0.0;-0.0} (balance={Funding.Instance.Funds:F0})");
                    Funding.Instance.AddFunds(delta, TransactionReasons.None);
                    Log($"Tree resource: funds {delta:+0.0;-0.0} (tree '{tree.TreeName}')");
                }

                if (tree.DeltaScience != 0 && ResearchAndDevelopment.Instance != null)
                {
                    double delta = tree.DeltaScience;
                    double originalScience = delta;
                    if (delta < 0 && ResearchAndDevelopment.Instance.Science + delta < 0)
                        delta = -ResearchAndDevelopment.Instance.Science;
                    if (delta != originalScience)
                        Log($"ApplyTreeLumpSum: science clamped for '{tree.TreeName}': original={originalScience:+0.0;-0.0} → clamped={delta:+0.0;-0.0} (balance={ResearchAndDevelopment.Instance.Science:F1})");
                    ResearchAndDevelopment.Instance.AddScience((float)delta, TransactionReasons.None);
                    Log($"Tree resource: science {delta:+0.0;-0.0} (tree '{tree.TreeName}')");
                }

                if (tree.DeltaReputation != 0 && Reputation.Instance != null)
                {
                    float delta = tree.DeltaReputation;
                    float originalRep = delta;
                    if (delta < 0 && Reputation.CurrentRep + delta < 0)
                        delta = -Reputation.CurrentRep;
                    if (delta != originalRep)
                        Log($"ApplyTreeLumpSum: reputation clamped for '{tree.TreeName}': original={originalRep:+0.0;-0.0} → clamped={delta:+0.0;-0.0} (balance={Reputation.CurrentRep:F1})");
                    Reputation.Instance.AddReputation(delta, TransactionReasons.None);
                    Log($"Tree resource: reputation {delta:+0.0;-0.0} (tree '{tree.TreeName}')");
                }
            }

            tree.ResourcesApplied = true;
            Log($"Tree resource lump sum applied for '{tree.TreeName}'");
        }

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

        internal void NormalizeAfterSyntheticScenarioLoad(string reason)
        {
            string cleanupReason = string.IsNullOrEmpty(reason)
                ? "synthetic scenario load"
                : reason;
            ParsekLog.Info("TestRunner",
                $"Normalizing FLIGHT runtime after {cleanupReason}");

            pendingWatchAfterFFId = null;
            watchMode.ClearAfterSyntheticScenarioLoad();

            StopPlayback();
            DestroyAllTimelineGhosts("synthetic-scenario-load");
        }

        public void DestroyAllTimelineGhosts(string ghostMapReason = "rewind")
        {
            // Remove ghost map ProtoVessels before engine cleanup
            GhostMapPresence.RemoveAllGhostVessels(ghostMapReason);

            // Engine destroys all ghost GOs and clears engine-owned state.
            // Fires OnAllGhostsDestroying so policy clears its own state.
            engine.DestroyAllGhosts();

            // Clear ParsekFlight-local state (positioning, proximity, diagnostics)
            orbitCache.Clear();
            loggedOrbitSegments.Clear();
            loggedOrbitRotationSegments.Clear();
            nearbySpawnCandidates.Clear();
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
            // the user-configured cutoff distance. The cutoff applies uniformly to all ghosts.
            bool forceWatchedFullFidelity = false;
            if (isWatchedGhost || isWatchProtectedRecording)
            {
                float cutoffKm = DistanceThresholds.GhostFlight.GetWatchCameraCutoffKm(ParsekSettings.Current);
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
            bool surfaceSkip = TrajectoryMath.IsSurfaceAtUT(traj.TrackSections, ut);
            InterpolationResult interpResult;
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

            int playbackIdx = state.playbackIndex;
            InterpolationResult interpResult;
            InterpolateAndPositionRelative(state.ghost, sectionFrames, ref playbackIdx,
                ut, anchorVesselId, ShouldAutoActivateGhost(state), out interpResult);
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
            // Normal in-flight playback goes through InterpolateAndPosition and
            // still relies on the existing ClampGhostsToTerrain LateUpdate pass
            // with its 0.5 m floor — mid-playback clamp is a follow-up tracked
            // in the #282 todo-and-known-bugs entry.
            TrajectoryPoint positioned = point;
            if (traj != null)
            {
                var term = traj.TerminalStateValue;
                if (term == TerminalState.Landed || term == TerminalState.Splashed)
                    positioned = ApplyLandedGhostClearance(point, index, traj.VesselName, traj.TerrainHeightAtEnd);
            }

            PositionGhostAt(state.ghost, positioned);

            // Keep the watch-mode overlay + diagnostics consistent with the
            // clamped position. WatchModeController reads state.lastInterpolatedAltitude
            // for its "ghost at alt N m on Kerbin" line — if we don't update it,
            // the log reports the buried raw altitude even after the visual is fixed.
            state.lastInterpolatedAltitude = positioned.altitude;
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
        /// below (current PQS terrain + 0.5 m), which only fires when PQS terrain
        /// has shifted UP since recording. For NaN-recorded-terrain legacy
        /// recordings we fall back to a fixed clearance above PQS.</para>
        ///
        /// <para>Ghosts are kinematic — no physics damage concern — so the safety
        /// floor is small (0.5 m). <c>internal static</c> so in-game tests can
        /// exercise it without needing a full <c>IGhostPositioner</c> chain.</para>
        /// </summary>
        internal static TrajectoryPoint ApplyLandedGhostClearance(
            TrajectoryPoint p, int index, string vesselName, double recordedTerrainHeight)
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

            // Legacy (NaN) path: no recorded surface height — use fixed clearance
            // above PQS terrain as before.
            if (double.IsNaN(recordedTerrainHeight))
            {
                double legacyTarget = pqsTerrain + VesselSpawner.LandedGhostClearanceMeters;
                if (p.altitude >= legacyTarget)
                    return p;
                // delta is always > 0 here because we already verified p.altitude < legacyTarget.
                double delta = legacyTarget - p.altitude;
                ParsekLog.VerboseRateLimited("TerrainCorrect", $"landed-ghost-{index}",
                    $"Landed ghost clamp #{index} (\"{vesselName}\"): " +
                    $"alt={p.altitude.ToString("F1", ic)} pqsTerrain={pqsTerrain.ToString("F1", ic)} " +
                    $"-> {legacyTarget.ToString("F1", ic)} (delta=+{delta.ToString("F2", ic)}m, " +
                    $"body={body.name}, NaN fallback)");
                p.altitude = legacyTarget;
                return p;
            }

            // Primary path: trust the recorded altitude. Only push up if the
            // recorded altitude is below the current PQS terrain floor — which
            // only happens when PQS terrain has shifted UP since recording.
            double safetyFloor = pqsTerrain + 0.5;
            if (p.altitude >= safetyFloor)
                return p;

            double upDelta = safetyFloor - p.altitude;
            ParsekLog.VerboseRateLimited("TerrainCorrect", $"landed-ghost-{index}",
                $"Landed ghost clamp #{index} (\"{vesselName}\"): " +
                $"alt={p.altitude.ToString("F1", ic)} pqsTerrain={pqsTerrain.ToString("F1", ic)} " +
                $"-> {safetyFloor.ToString("F1", ic)} (delta=+{upDelta.ToString("F2", ic)}m, " +
                $"body={body.name}, below-pqs-floor, " +
                $"recSurface={recordedTerrainHeight.ToString("F1", ic)})");
            p.altitude = safetyFloor;
            return p;
        }

        void IGhostPositioner.PositionAtSurface(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state)
        {
            if (state?.ghost == null || traj?.SurfacePos == null) return;
            PositionGhostAtSurface(state.ghost, traj.SurfacePos.Value, ShouldAutoActivateGhost(state));
        }

        void IGhostPositioner.PositionFromOrbit(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut)
        {
            if (state?.ghost == null || traj?.OrbitSegments == null) return;
            // Find the orbit segment covering this UT
            OrbitSegment? seg = FindOrbitSegment(traj.OrbitSegments, ut);
            if (seg.HasValue)
                PositionGhostFromOrbit(state.ghost, seg.Value, ut, index * 10000);
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
        private Dictionary<int, Orbit> orbitCache = new Dictionary<int, Orbit>();

        void PositionGhostFromOrbit(GameObject ghost, OrbitSegment segment, double ut, int cacheKey)
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
            Vector3d bodyPosition, Quaternion currentRotation, int cacheKey, bool hasOfr, bool spinning)
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
            Vector3 velocity, Vector3 radialOut, int cacheKey, string suffix)
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

        bool TryResolvePlaybackWorldPosition(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (traj == null)
                return false;

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
                                section.frames ?? traj.Points, playbackUT, anchorPid, out worldPos))
                        {
                            return true;
                        }
                    }
                }
            }

            bool surfaceSkip = TrajectoryMath.IsSurfaceAtUT(traj.TrackSections, playbackUT);
            int cachedIndex = state != null ? state.playbackIndex : 0;
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
            List<TrajectoryPoint> frames, double targetUT, uint anchorVesselId, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (frames == null || frames.Count == 0)
                return false;
            if (frames.Count == 1)
            {
                return TryResolveRelativeOffsetWorldPosition(
                    frames[0].latitude, frames[0].longitude, frames[0].altitude,
                    anchorVesselId, out worldPos);
            }

            int cachedIndex = 0;
            int indexBefore = TrajectoryMath.FindWaypointIndex(frames, ref cachedIndex, targetUT);
            if (indexBefore < 0)
                return TryResolveRelativeOffsetWorldPosition(
                    frames[0].latitude, frames[0].longitude, frames[0].altitude,
                    anchorVesselId, out worldPos);
            if (indexBefore >= frames.Count - 1)
            {
                return TryResolveRelativeOffsetWorldPosition(
                    frames[frames.Count - 1].latitude,
                    frames[frames.Count - 1].longitude,
                    frames[frames.Count - 1].altitude,
                    anchorVesselId, out worldPos);
            }

            TrajectoryPoint before = frames[indexBefore];
            TrajectoryPoint after = frames[indexBefore + 1];
            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                return TryResolveRelativeOffsetWorldPosition(
                    before.latitude, before.longitude, before.altitude,
                    anchorVesselId, out worldPos);
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

            double dx = before.latitude + (after.latitude - before.latitude) * t;
            double dy = before.longitude + (after.longitude - before.longitude) * t;
            double dz = before.altitude + (after.altitude - before.altitude) * t;
            return TryResolveRelativeOffsetWorldPosition(dx, dy, dz, anchorVesselId, out worldPos);
        }

        bool TryResolveRelativeOffsetWorldPosition(
            double dx, double dy, double dz, uint anchorVesselId, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            Vessel anchor = FlightRecorder.FindVesselByPid(anchorVesselId);
            if (anchor == null)
                return false;

            Vector3d anchorPos = anchor.GetWorldPos3D();
            worldPos = TrajectoryMath.ApplyRelativeOffset(anchorPos, dx, dy, dz);
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
                            $"sectionUT=[{section.startUT:F1},{section.endUT:F1}]");

                    InterpolateAndPositionRelative(
                        ghost, sectionFrames, ref playbackIdx, loopUT,
                        rec.LoopAnchorVesselId, allowActivation, out interpResult);
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
        /// Ghost position = anchor vessel's current world position + stored offset.
        /// Falls back to body-fixed absolute positioning if anchor not found.
        /// </summary>
        void InterpolateAndPositionRelative(
            GameObject ghost,
            List<TrajectoryPoint> frames,
            ref int cachedIndex,
            double targetUT,
            uint anchorVesselId,
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
                PositionGhostRelativeAt(ghost, frames[0], anchorVesselId, allowActivation);
                interpResult = new InterpolationResult(frames[0].velocity, frames[0].bodyName, 0);
                return;
            }

            TrajectoryPoint before = frames[indexBefore];
            TrajectoryPoint after = frames[indexBefore + 1];

            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                PositionGhostRelativeAt(ghost, before, anchorVesselId, allowActivation);
                interpResult = new InterpolationResult(before.velocity, before.bodyName, 0);
                return;
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

            // Interpolate offset (dx, dy, dz stored in lat/lon/alt)
            double dx = before.latitude + (after.latitude - before.latitude) * t;
            double dy = before.longitude + (after.longitude - before.longitude) * t;
            double dz = before.altitude + (after.altitude - before.altitude) * t;

            // Interpolate rotation (stored as relative rotation)
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
                Vector3d ghostPos = TrajectoryMath.ApplyRelativeOffset(anchorPos, dx, dy, dz);

                if (double.IsNaN(ghostPos.x) || double.IsNaN(ghostPos.y) || double.IsNaN(ghostPos.z))
                {
                    ParsekLog.Warn("Flight", "InterpolateAndPositionRelative: NaN in ghost position, using anchor position");
                    ghostPos = anchorPos;
                }

                ghost.transform.position = ghostPos;
                ghost.transform.rotation = anchor.transform.rotation * interpolatedRot;

                ParsekLog.VerboseRateLimited("Flight", "relative-offset-applied",
                    $"RELATIVE playback: dx={dx:F2} dy={dy:F2} dz={dz:F2} |offset|={System.Math.Sqrt(dx*dx+dy*dy+dz*dz):F2}m anchor={anchorVesselId}", 2.0);

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
        void PositionGhostRelativeAt(GameObject ghost, TrajectoryPoint point, uint anchorVesselId,
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
                Vector3d ghostPos = TrajectoryMath.ApplyRelativeOffset(anchorPos, dx, dy, dz);
                ghost.transform.position = ghostPos;
                ghost.transform.rotation = anchor.transform.rotation * sanitized;

                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                ghostPosEntries.Add(new GhostPosEntry
                {
                    ghost = ghost,
                    mode = GhostPosMode.Relative,
                    anchorVesselId = anchorVesselId,
                    relDx = dx, relDy = dy, relDz = dz,
                    relativeRot = sanitized,
                    relativeBodyName = bodyName,
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
                bool isChainLoopingOrDisabled = !string.IsNullOrEmpty(rec.ChainId) &&
                    (RecordingStore.IsChainLooping(rec.ChainId) ||
                     RecordingStore.IsChainFullyDisabled(rec.ChainId));

                var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                    rec, isActiveChainMember, isChainLoopingOrDisabled);
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

                double dist = Vector3d.Distance(activePos, state.ghost.transform.position);
                if (dist > NearbySpawnRadius)
                    continue;

                var depInfo = SelectiveSpawnUI.ComputeDepartureInfo(rec, currentUT);
                nearbySpawnCandidates.Add(new NearbySpawnCandidate
                {
                    recordingIndex = i,
                    vesselName = rec.VesselName,
                    endUT = rec.EndUT,
                    distance = dist,
                    recordingId = rec.RecordingId,
                    willDepart = depInfo.willDepart,
                    departureUT = depInfo.departureUT,
                    destination = depInfo.destination
                });
            }
        }

        /// <summary>
        /// Fires one-time screen notifications for newly discovered proximity candidates.
        /// </summary>
        private void NotifyNewProximityCandidates(double currentUT)
        {
            for (int c = 0; c < nearbySpawnCandidates.Count; c++)
            {
                var cand = nearbySpawnCandidates[c];
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
                        "Proximity check: {0} candidate(s) within {1:F0}m",
                        nearbySpawnCandidates.Count, NearbySpawnRadius));
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
            var next = SelectiveSpawnUI.FindNextSpawnCandidate(nearbySpawnCandidates, currentUT);
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
