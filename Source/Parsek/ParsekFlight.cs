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
        private Dictionary<int, List<GhostPlaybackState>> overlapGhosts => engine.overlapGhosts;
        private Dictionary<int, double> loopPhaseOffsets => engine.loopPhaseOffsets;

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

            // v5+: rotation is surface-relative (multiply by body rotation at playback)
            // v4:  rotation is world-space (use CorrectForBodyRotation time-delta correction)
            public bool surfaceRelativeRotation;

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

        // Debris persistence enforcement: saved original value when Parsek overrides it
        private const int MinDebrisForRecording = 10;
        private int savedMaxPersistentDebris = -1;
        private bool debrisOverrideActive;

        // Background recorder for tree mode (null when not in tree mode)
        private BackgroundRecorder backgroundRecorder;

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

        // Deferred spawn queue: recording IDs queued during warp, flushed when warp ends
        private HashSet<string> pendingSpawnRecordingIds = new HashSet<string>();
        private string pendingWatchRecordingId = null;  // recording ID that was in watch mode when deferred
        private bool timelineResourceReplayPausedLogged = false;
        private bool wasWarpActive = false; // tracks previous warp state for exit detection
        private double warpStartUT = 0.0;  // UT when warp began — used for facility visual updates
        // Camera follow (watch mode) — transient, never serialized
        private const string WatchModeLockId = "ParsekWatch";
        private const ControlTypes WatchModeLockMask =
            ControlTypes.STAGING | ControlTypes.THROTTLE |
            ControlTypes.VESSEL_SWITCHING | ControlTypes.EVA_INPUT |
            ControlTypes.CAMERAMODES;
        internal int watchedRecordingIndex = -1;       // -1 = not watching
        string watchedRecordingId = null;     // stable across index shifts
        float watchStartTime;                 // Time.time when watch mode was entered
        long watchedOverlapCycleIndex = -1;    // which overlap cycle the camera is following (-1 = ready for next, -2 = holding after explosion)
        double overlapRetargetAfterUT = -1;   // delay re-target after watched cycle explodes
        GameObject overlapCameraAnchor;       // temp anchor so FlightCamera doesn't reference destroyed ghost
        Vessel savedCameraVessel = null;
        float savedCameraDistance = 0f;
        float savedCameraPitch = 0f;
        float savedCameraHeading = 0f;
        float watchEndHoldUntilRealTime = -1;  // non-looped end hold timer (real time, warp-independent)
        float savedPivotSharpness = 0.5f;
        int watchNoTargetFrames = 0;          // consecutive frames with no valid camera target (safety net)

        // UI
        private Rect windowRect = new Rect(20, 100, 250, 250);
        private bool showUI = false;
        private static ToolbarControl toolbarControl;
        private ParsekUI ui;

        // Ghost playback engine + policy (T25 extraction)
        private GhostPlaybackEngine engine;
        private ParsekPlaybackPolicy policy;

        // Cached per-frame allocations for engine path (avoid GC pressure)
        private readonly List<IPlaybackTrajectory> cachedTrajectories = new List<IPlaybackTrajectory>();
        private TrajectoryPlaybackFlags[] cachedFlags;

        #endregion

        #region Public Accessors (for ParsekUI)

        public bool IsRecording => recorder?.IsRecording ?? false;
        public bool IsPlaying => isPlaying;
        public int TimelineGhostCount => ghostStates.Count;
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

        // Camera follow (watch mode)
        internal bool IsWatchingGhost => watchedRecordingIndex >= 0;
        internal int WatchedRecordingIndex => watchedRecordingIndex;

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

            ui = new ParsekUI(this);

            // Chain segment management (T26 extraction)
            chainManager = new ChainSegmentManager();

            // Ghost playback engine + policy (T25 extraction)
            engine = new GhostPlaybackEngine(this);
            engine.OnLoopCameraAction += HandleLoopCameraAction;
            engine.OnOverlapCameraAction += HandleOverlapCameraAction;
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
                "Parsek/Textures/parsek_38",
                "Parsek/Textures/parsek_24",
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
                        e.ghost.transform.rotation = e.surfaceRelativeRotation
                            ? e.bodyBefore.bodyTransform.rotation * e.interpolatedRot
                            : CorrectForBodyRotation(e.bodyBefore, e.pointUT, e.interpolatedRot);
                        break;
                    }
                    case GhostPosMode.SinglePoint:
                    {
                        if (e.bodyBefore == null) break;
                        Vector3d pos = e.bodyBefore.GetWorldSurfacePosition(
                            e.latBefore, e.lonBefore, e.altBefore);
                        e.ghost.transform.position = pos;
                        e.ghost.transform.rotation = e.surfaceRelativeRotation
                            ? e.bodyBefore.bodyTransform.rotation * e.interpolatedRot
                            : CorrectForBodyRotation(e.bodyBefore, e.pointUT, e.interpolatedRot);
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

            UpdateWatchCamera();
        }

        /// <summary>
        /// Terrain clamp: prevent ghosts from appearing below terrain surface.
        /// Sub-orbital orbit reconstruction can place ghosts underground (periapsis
        /// below surface), and procedural terrain height varies between sessions.
        /// </summary>
        private void ClampGhostsToTerrain()
        {
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
                double clamped = TerrainCorrector.ClampAltitude(alt, terrainHeight);
                if (clamped > alt)
                {
                    e.ghost.transform.position = body.GetWorldSurfacePosition(lat, lon, clamped);
                    ParsekLog.VerboseRateLimited("TerrainCorrect", "clamp-ghost",
                        $"Ghost terrain clamp: alt={alt:F1} terrain={terrainHeight:F1} -> {clamped:F1}");
                }
            }
        }

        void OnGUI()
        {
            if (MapView.MapIsEnabled)
                ui.DrawMapMarkers();

            if (watchedRecordingIndex >= 0)
                DrawWatchModeOverlay();

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
                ui.DrawActionsWindowIfOpen(windowRect);
                ui.DrawSettingsWindowIfOpen(windowRect);
                ui.DrawSpawnControlWindowIfOpen(windowRect);
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

            // Unregister events to prevent handler leaks
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
            ExitWatchMode();
            InputLockManager.RemoveControlLock(WatchModeLockId); // safety net

            // Phase 6b: Clean up ghost chain state
            vesselGhoster?.CleanupAll();
            vesselGhoster = null;
            activeGhostChains = null;

            // Remove ghost map ProtoVessels on scene teardown
            GhostMapPresence.RemoveAllGhostVessels("scene-cleanup");

            // Unsubscribe camera events before policy/engine disposal
            if (engine != null)
            {
                engine.OnLoopCameraAction -= HandleLoopCameraAction;
                engine.OnOverlapCameraAction -= HandleOverlapCameraAction;
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

        #endregion

        #region Scene Change Handling

        void OnSceneChangeRequested(GameScenes scene)
        {
            sceneChangeInProgress = true;
            RecordingStore.PendingDestinationScene = scene;
            ParsekLog.Info("Flight", $"Scene change requested: {scene}");

            // Exit watch mode on scene change
            if (watchedRecordingIndex >= 0)
                ParsekLog.Info("CameraFollow", "Watch mode cleared on scene change");
            ExitWatchMode();
            InputLockManager.RemoveControlLock(WatchModeLockId); // safety net

            // Finalize continuation sampling before anything else
            chainManager.StopAllContinuations("scene change");

            ClearSceneChangeTransientState();

            // Tree mode: finalize and commit tree on scene exit.
            // Background recorder must be finalized BEFORE the tree commit so
            // its data is flushed to the tree recordings.
            if (activeTree != null)
                FinalizeTreeOnSceneChange(scene);

            // If we have recording data and are leaving flight, stash it
            if (recorder != null && recorder.Recording.Count > 0)
                StashPendingOnSceneChange();

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
            double commitUT = Planetarium.GetUniversalTime();

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
                // Revert: preserve snapshots for merge dialog
                CommitTreeRevert(commitUT);
            }
            else if (!ParsekScenario.IsAutoMerge)
            {
                // autoMerge OFF: safe finalization but preserve snapshots for dialog in KSC/TS.
                // Uses isSceneExit: true (no live vessel re-snapshots during teardown)
                // but skips snapshot nulling so the dialog can offer vessel spawning.
                FinalizeTreeRecordings(activeTree, commitUT, isSceneExit: true);
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

        private void StashPendingOnSceneChange()
        {
            bool wasDestroyed = recorder.VesselDestroyedDuringRecording;

            // Stop recording if active
            if (recorder.IsRecording)
                recorder.ForceStop();

            string vesselName = FlightGlobals.ActiveVessel != null
                ? FlightGlobals.ActiveVessel.vesselName
                : "Unknown Vessel";

            var captured = recorder.CaptureAtStop;
            RecordingStore.StashPending(
                recorder.Recording,
                vesselName,
                recorder.OrbitSegments,
                recordingId: captured != null ? captured.RecordingId : null,
                recordingFormatVersion: captured != null ? (int?)captured.RecordingFormatVersion : null,

                partEvents: recorder.PartEvents,
                flagEvents: recorder.FlagEvents);

            // Use stop-time atomic capture when available; fallback to scene-change capture.
            // ApplyPersistenceArtifactsFrom copies chain/ParentRecordingId/EvaCrewName from
            // CaptureAtStop on the normal path (StopRecording tagged them there).
            if (captured != null)
            {
                var pending = RecordingStore.Pending;
                pending.ApplyPersistenceArtifactsFrom(captured);
                Log("Applied stop-time snapshot/geometry artifacts to pending recording");
            }
            else
            {
                // Snapshot vessel for persistence across revert.
                VesselSpawner.SnapshotVessel(
                    RecordingStore.Pending,
                    wasDestroyed,
                    destroyedFallbackSnapshot: recorder.InitialGhostVisualSnapshot ?? recorder.LastGoodVesselSnapshot);
                RecordingStore.Pending.GhostVisualSnapshot = recorder.InitialGhostVisualSnapshot != null
                    ? recorder.InitialGhostVisualSnapshot.CreateCopy()
                    : (RecordingStore.Pending.VesselSnapshot != null
                        ? RecordingStore.Pending.VesselSnapshot.CreateCopy()
                        : null);

                // Fallback path (ForceStop): CaptureAtStop is null, so
                // ApplyPersistenceArtifactsFrom didn't run. Apply chain
                // metadata directly from the active fields.
                if (RecordingStore.HasPending && chainManager.ActiveChainId != null)
                {
                    RecordingStore.Pending.ChainId = chainManager.ActiveChainId;
                    RecordingStore.Pending.ChainIndex = chainManager.ActiveChainNextIndex;
                    RecordingStore.Pending.ParentRecordingId = chainManager.ActiveChainPrevId;
                    RecordingStore.Pending.EvaCrewName = chainManager.ActiveChainCrewName;
                }

                // Copy rewind fields from recorder (ForceStop bypasses ApplyPersistenceArtifactsFrom)
                if (RecordingStore.HasPending)
                {
                    RecordingStore.Pending.RewindSaveFileName = recorder.RewindSaveFileName;
                    RecordingStore.Pending.RewindReservedFunds = recorder.RewindReservedFunds;
                    RecordingStore.Pending.RewindReservedScience = recorder.RewindReservedScience;
                    RecordingStore.Pending.RewindReservedRep = recorder.RewindReservedRep;
                }

                // Tag segment phase in fallback path
                if (RecordingStore.HasPending && string.IsNullOrEmpty(RecordingStore.Pending.SegmentPhase))
                {
                    TagSegmentPhaseIfMissing(RecordingStore.Pending, FlightGlobals.ActiveVessel);
                    if (!string.IsNullOrEmpty(RecordingStore.Pending.SegmentPhase))
                        ParsekLog.Verbose("Flight", $"Final segment tagged (ForceStop): " +
                            $"{RecordingStore.Pending.SegmentBodyName} {RecordingStore.Pending.SegmentPhase}");
                }
            }

            // Clear chain fields after both paths have consumed them
            chainManager.ClearChainIdentity();

            // Capture vessel situation + PersistentId on the pending recording.
            // VesselPersistentId: safety net for the fallback path where
            // ApplyPersistenceArtifactsFrom didn't run.
            // SceneExitSituation + TerminalState: enables auto-commit by
            // ParsekScenario on non-revert scene exits.
            if (RecordingStore.HasPending)
            {
                RecordingStore.Pending.VesselPersistentId = recorder.RecordingVesselId;

                if (FlightGlobals.ActiveVessel != null)
                {
                    var sit = FlightGlobals.ActiveVessel.situation;
                    RecordingStore.Pending.SceneExitSituation = (int)sit;
                    RecordingStore.Pending.TerminalStateValue =
                        RecordingTree.DetermineTerminalState((int)sit, FlightGlobals.ActiveVessel);
                }

                // Fallback: if the vessel was destroyed during recording,
                // override whatever situation-based terminal state was set.
                // Covers two cases:
                // 1. ActiveVessel null (building collision destroyed the only
                //    vessel, triggering scene change before
                //    ShowPostDestructionMergeDialog could run) — TerminalState is null.
                // 2. ActiveVessel switched to debris/other vessel with a
                //    non-Destroyed situation (e.g., LANDED) — TerminalState is
                //    wrong (Landed instead of Destroyed).
                ApplyDestroyedFallback(wasDestroyed, RecordingStore.Pending);
            }

            recorder = null;
            lastPlaybackIndex = 0;
        }

        void OnVesselWillDestroy(Vessel v)
        {
            if (v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId)) return;

            // Watch mode: null out saved camera vessel if it's being destroyed
            if (watchedRecordingIndex >= 0 && savedCameraVessel != null && v == savedCameraVessel)
                savedCameraVessel = null;

            // Watch mode: if the active vessel is destroyed while watching, exit watch mode.
            // Skip camera restore because ActiveVessel is the dying vessel — KSP will
            // assign a new active vessel and handle the camera itself.
            if (watchedRecordingIndex >= 0 && v == FlightGlobals.ActiveVessel)
            {
                ParsekLog.Warn("CameraFollow",
                    $"Active vessel destroyed while watching ghost #{watchedRecordingIndex} \u2014 skipping camera restore (dying vessel), KSP will reassign");
                ExitWatchMode(skipCameraRestore: true);
            }

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
                case DestructionMode.StandaloneMerge:
                    StartCoroutine(ShowPostDestructionMergeDialog());
                    break;
                case DestructionMode.TreeAllLeavesCheck:
                    treeDestructionDialogPending = true;
                    ParsekLog.Info("Flight", "Active vessel destroyed in tree mode — scheduling tree destruction check");
                    StartCoroutine(ShowPostDestructionTreeMergeDialog());
                    break;
            }

            // If the continuation vessel is destroyed, mark the recording and stop tracking
            if (chainManager.ContinuationVesselPid != 0 && v.persistentId == chainManager.ContinuationVesselPid)
            {
                if (chainManager.ContinuationRecordingIdx >= 0 &&
                    chainManager.ContinuationRecordingIdx < RecordingStore.CommittedRecordings.Count)
                {
                    var rec = RecordingStore.CommittedRecordings[chainManager.ContinuationRecordingIdx];
                    rec.VesselDestroyed = true;
                    // Bug #95: Do NOT null VesselSnapshot on committed recordings.
                    // VesselDestroyed already gates spawn via ShouldSpawnAtRecordingEnd.
                    // Nulling the snapshot permanently prevents re-spawn after revert.
                    Log($"Continuation vessel destroyed (pid={chainManager.ContinuationVesselPid}), " +
                        $"VesselDestroyed=true, VesselSnapshot preserved={rec.VesselSnapshot != null}");
                }
                chainManager.StopContinuation("vessel destroyed");
            }

            // If the undock continuation vessel is destroyed, stop tracking
            if (chainManager.UndockContinuationPid != 0 && v.persistentId == chainManager.UndockContinuationPid)
            {
                if (chainManager.UndockContinuationRecIdx >= 0 &&
                    chainManager.UndockContinuationRecIdx < RecordingStore.CommittedRecordings.Count)
                {
                    var rec = RecordingStore.CommittedRecordings[chainManager.UndockContinuationRecIdx];
                    rec.VesselDestroyed = true;
                    Log($"Undock continuation vessel destroyed (pid={chainManager.UndockContinuationPid})");
                }
                chainManager.StopUndockContinuation("vessel destroyed");
            }
        }

        /// <summary>
        /// Coroutine that shows a merge dialog after a vessel is destroyed during standalone recording.
        /// Waits 2 seconds to avoid overlapping with mission results / flight log.
        /// </summary>
        IEnumerator ShowPostDestructionMergeDialog()
        {
            // Wait one frame to let the destruction sequence complete.
            // The Harmony patch on FlightResultsDialog.Display suppresses KSP's
            // crash report until the merge dialog is resolved — no hardcoded delay needed.
            yield return null;

            // Guard: only proceed if recorder still exists and vessel was destroyed
            if (recorder == null || !recorder.VesselDestroyedDuringRecording)
            {
                ParsekLog.Warn("Flight", "ShowPostDestructionMergeDialog skipped: recorder null or vessel not destroyed");
                yield break;
            }

            // Guard: only for standalone recordings (tree mode has its own handling)
            if (activeTree != null)
            {
                ParsekLog.Warn("Flight", "ShowPostDestructionMergeDialog skipped: activeTree present (tree mode handles destruction)");
                yield break;
            }

            if (recorder.Recording.Count == 0)
            {
                ParsekLog.Warn("Flight", "ShowPostDestructionMergeDialog skipped: recording count is 0");
                yield break;
            }

            // Guard: if OnSceneChangeRequested already stashed a pending, don't double-stash
            if (RecordingStore.HasPending)
            {
                ParsekLog.Warn("Flight", "ShowPostDestructionMergeDialog skipped: HasPending already set");
                yield break;
            }

            Log("Post-destruction: stopping recorder and stashing pending recording");

            // Stop recording
            if (recorder.IsRecording)
                recorder.ForceStop();

            string vesselName = recorder.CaptureAtStop?.VesselName
                ?? (FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.vesselName : "Unknown Vessel");

            var captured = recorder.CaptureAtStop;
            RecordingStore.StashPending(
                recorder.Recording,
                vesselName,
                recorder.OrbitSegments,
                recordingId: captured != null ? captured.RecordingId : null,
                recordingFormatVersion: captured != null ? (int?)captured.RecordingFormatVersion : null,
                partEvents: recorder.PartEvents,
                flagEvents: recorder.FlagEvents);

            if (captured != null)
            {
                RecordingStore.Pending.ApplyPersistenceArtifactsFrom(captured);
            }
            else
            {
                VesselSpawner.SnapshotVessel(
                    RecordingStore.Pending,
                    true, // destroyed
                    destroyedFallbackSnapshot: recorder.InitialGhostVisualSnapshot ?? recorder.LastGoodVesselSnapshot);
                RecordingStore.Pending.GhostVisualSnapshot = recorder.InitialGhostVisualSnapshot != null
                    ? recorder.InitialGhostVisualSnapshot.CreateCopy()
                    : null;

                // Copy rewind fields from recorder (ForceStop bypasses ApplyPersistenceArtifactsFrom)
                RecordingStore.Pending.RewindSaveFileName = recorder.RewindSaveFileName;
                RecordingStore.Pending.RewindReservedFunds = recorder.RewindReservedFunds;
                RecordingStore.Pending.RewindReservedScience = recorder.RewindReservedScience;
                RecordingStore.Pending.RewindReservedRep = recorder.RewindReservedRep;
            }

            // Set terminal state
            if (RecordingStore.HasPending)
            {
                RecordingStore.Pending.VesselPersistentId = recorder.RecordingVesselId;
                RecordingStore.Pending.TerminalStateValue = TerminalState.Destroyed;
            }

            recorder = null;
            lastPlaybackIndex = 0;

            // Auto-discard pad failures: destroyed within 10s AND never moved far from spawn.
            // Real crashes travel 30+ meters; pad failures stay near the pad.
            if (RecordingStore.HasPending)
            {
                double flightDuration = RecordingStore.Pending.EndUT - RecordingStore.Pending.StartUT;
                double maxDist = RecordingStore.Pending.MaxDistanceFromLaunch;
                if (IsPadFailure(flightDuration, maxDist))
                {
                    Log($"Post-destruction: pad failure ({flightDuration:F1}s, {maxDist:F0}m) — auto-discarding");
                    ScreenMessage("Recording discarded — pad failure", 3f);
                    RecordingStore.DiscardPending();
                    yield break;
                }
            }

            // Show merge dialog
            if (RecordingStore.HasPending)
            {
                Log("Post-destruction: commit or dialog");
                CommitOrShowDialog(RecordingStore.Pending);
            }
        }

        /// <summary>
        /// Coroutine that checks whether all tree leaves are terminal after the active vessel
        /// is destroyed. If so, finalizes the tree and shows the tree merge dialog.
        /// </summary>
        IEnumerator ShowPostDestructionTreeMergeDialog()
        {
            // Wait one frame for destruction events to settle
            yield return null;

            // Guard: tree cleaned up during wait (scene change, etc.)
            if (activeTree == null)
            {
                ParsekLog.Verbose("Flight", "ShowPostDestructionTreeMergeDialog: activeTree is null — aborting");
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

            bool activeDestroyed = recorder == null || !recorder.IsRecording
                || recorder.VesselDestroyedDuringRecording;
            if (!RecordingTree.AreAllLeavesTerminal(activeTree.Recordings,
                activeTree.ActiveRecordingId, activeDestroyed))
            {
                ParsekLog.Info("Flight",
                    "ShowPostDestructionTreeMergeDialog: not all leaves terminal — other vessels still alive");
                treeDestructionDialogPending = false;
                yield break;
            }

            // All leaves are terminal — finalize and show dialog
            ParsekLog.Info("Flight", "ShowPostDestructionTreeMergeDialog: all leaves terminal — finalizing tree");

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
            if (IsTreePadFailure(activeTree))
            {
                ParsekLog.Info("Flight",
                    $"ShowPostDestructionTreeMergeDialog: tree pad failure — auto-discarding");
                ScreenMessage("Recording discarded — pad failure", 3f);
                RecordingStore.DiscardPendingTree();
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
                KerbalsModule.RecalculateAndApply();
                MergeDialog.ReplayFlightResultsIfPending();
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

            // Watch mode: re-target camera to ghost — KSP reparents pivot on vessel switch.
            // Don't early-return: tree vessel-switch logic below must still run so that
            // programmatic switches (e.g. docking absorbs a vessel) properly transition
            // the recorder state and background mapping.
            if (watchedRecordingIndex >= 0)
            {
                GhostPlaybackState ws;
                if (ghostStates.TryGetValue(watchedRecordingIndex, out ws) && ws != null && ws.ghost != null)
                {
                    var target = ws.cameraPivot ?? ws.ghost.transform;
                    FlightCamera.fetch.SetTargetTransform(target);
                    ParsekLog.Info("CameraFollow",
                        $"onVesselChange re-target: ghost #{watchedRecordingIndex}" +
                        $" target='{target.name}' localPos=({target.localPosition.x:F2},{target.localPosition.y:F2},{target.localPosition.z:F2})" +
                        $" worldPos=({target.position.x:F1},{target.position.y:F1},{target.position.z:F1})" +
                        $" camDist={FlightCamera.fetch.Distance:F1}");
                }
            }

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

            // If the current recorder is still active (OnPhysicsFrame didn't catch the switch,
            // e.g. because isOnRails was true), transition it to background now
            if (recorder != null && recorder.IsRecording && !recorder.IsBackgrounded
                && recorder.RecordingVesselId != newVessel.persistentId)
            {
                recorder.TransitionToBackground();
                FlushRecorderToTreeRecording(recorder, activeTree);

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
            treeRec.TrackSections.AddRange(rec.TrackSections);

            // Sort part/flag events chronologically (mixed event sources may produce non-chronological order)
            treeRec.PartEvents.Sort((a, b) => a.ut.CompareTo(b.ut));
            treeRec.FlagEvents.Sort((a, b) => a.ut.CompareTo(b.ut));

            // Populate VesselPersistentId (required for RebuildBackgroundMap after save/load)
            if (treeRec.VesselPersistentId == 0 && rec.RecordingVesselId != 0)
                treeRec.VesselPersistentId = rec.RecordingVesselId;

            // Set IsRecording = false to prevent dangling active state
            rec.IsRecording = false;
            rec.TransitionToBackgroundPending = false;

            ParsekLog.Info("Flight", $"Flushed recorder to tree recording '{recId}': " +
                $"{rec.Recording.Count} points, {rec.OrbitSegments.Count} orbit segments, " +
                $"{rec.PartEvents.Count} part events");
        }

        /// <summary>
        /// Creates a new FlightRecorder for the promoted vessel, starts recording with
        /// isPromotion=true, updates activeTree state.
        /// </summary>
        void PromoteRecordingFromBackground(string backgroundRecordingId, Vessel newVessel)
        {
            if (activeTree == null || newVessel == null) return;

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
        /// Appends captured trajectory data (points, orbit segments, part events, track sections)
        /// from a source recording into the target, sorts part events by UT, and sets the target's
        /// ExplicitEndUT. If source is null, only ExplicitEndUT is set.
        /// </summary>
        internal static void AppendCapturedDataToRecording(Recording target, Recording source, double endUT)
        {
            if (source != null)
            {
                target.Points.AddRange(source.Points);
                target.OrbitSegments.AddRange(source.OrbitSegments);
                target.PartEvents.AddRange(source.PartEvents);
                target.TrackSections.AddRange(source.TrackSections);
                target.PartEvents.Sort((a, b) => a.ut.CompareTo(b.ut));
            }
            target.ExplicitEndUT = endUT;
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
                    if (v.mainBody.atmosphere)
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
                string evaCrewName = null, uint evaVesselPid = 0)
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

            var activeChild = new Recording
            {
                RecordingId = activeChildId,
                TreeId = treeId,
                VesselPersistentId = activeVesselPid,
                VesselName = activeVesselName,
                ParentBranchPointId = bpId,
                ExplicitStartUT = branchUT
            };

            var backgroundChild = new Recording
            {
                RecordingId = backgroundChildId,
                TreeId = treeId,
                VesselPersistentId = backgroundVesselPid,
                VesselName = backgroundVesselName,
                ParentBranchPointId = bpId,
                ExplicitStartUT = branchUT
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
        /// If no tree exists yet, wraps the current recording as the root node.
        /// Creates two child recordings (active + background), wires up BackgroundRecorder,
        /// and starts a new FlightRecorder for the active child.
        /// </summary>
        void CreateSplitBranch(BranchPointType branchType, Vessel activeVessel, Vessel backgroundVessel,
            double branchUT, string evaCrewName = null, uint evaVesselPid = 0)
        {
            if (pendingSplitRecorder == null)
            {
                ParsekLog.Warn("Flight", "CreateSplitBranch: no pending split recorder — aborting");
                return;
            }

            var splitRecorder = pendingSplitRecorder;

            // Determine parent recording ID
            string parentRecordingId;

            // First split: create the tree and wrap current recording as root
            if (activeTree == null)
            {
                string treeId = Guid.NewGuid().ToString("N");
                string rootRecId = Guid.NewGuid().ToString("N");

                activeTree = new RecordingTree
                {
                    Id = treeId,
                    TreeName = splitRecorder.CaptureAtStop?.VesselName
                               ?? Recording.ResolveLocalizedName(activeVessel?.vesselName) ?? "Unknown",
                    RootRecordingId = rootRecId,
                    ActiveRecordingId = null // will be set below
                };

                // Capture pre-tree resources from the recorder
                activeTree.PreTreeFunds = splitRecorder.PreLaunchFunds;
                activeTree.PreTreeScience = splitRecorder.PreLaunchScience;
                activeTree.PreTreeReputation = splitRecorder.PreLaunchReputation;

                // Create root recording from captured data
                var rootRec = new Recording
                {
                    RecordingId = rootRecId,
                    TreeId = treeId,
                    VesselPersistentId = splitRecorder.RecordingVesselId,
                    VesselName = splitRecorder.CaptureAtStop?.VesselName ?? "Unknown",
                    ExplicitEndUT = branchUT
                };

                // Copy captured data into root recording
                if (splitRecorder.CaptureAtStop != null)
                {
                    rootRec.Points = new List<TrajectoryPoint>(splitRecorder.CaptureAtStop.Points);
                    rootRec.OrbitSegments = new List<OrbitSegment>(splitRecorder.CaptureAtStop.OrbitSegments);
                    rootRec.PartEvents = new List<PartEvent>(splitRecorder.CaptureAtStop.PartEvents);
                    rootRec.FlagEvents = new List<FlagEvent>(splitRecorder.CaptureAtStop.FlagEvents);
                    rootRec.SegmentEvents = new List<SegmentEvent>(splitRecorder.CaptureAtStop.SegmentEvents);
                    rootRec.TrackSections = new List<TrackSection>(splitRecorder.CaptureAtStop.TrackSections);
                    rootRec.GhostVisualSnapshot = splitRecorder.CaptureAtStop.GhostVisualSnapshot;
                    rootRec.VesselSnapshot = splitRecorder.CaptureAtStop.VesselSnapshot;
                    rootRec.RewindSaveFileName = splitRecorder.CaptureAtStop.RewindSaveFileName;
                    rootRec.RewindReservedFunds = splitRecorder.CaptureAtStop.RewindReservedFunds;
                    rootRec.RewindReservedScience = splitRecorder.CaptureAtStop.RewindReservedScience;
                    rootRec.RewindReservedRep = splitRecorder.CaptureAtStop.RewindReservedRep;
                    if (rootRec.Points.Count > 0)
                        rootRec.ExplicitStartUT = rootRec.Points[0].ut;
                }

                activeTree.Recordings[rootRecId] = rootRec;
                parentRecordingId = rootRecId;

                // Create BackgroundRecorder
                backgroundRecorder = new BackgroundRecorder(activeTree);
                backgroundRecorder.SubscribePartEvents();
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = backgroundRecorder;

                ParsekLog.Info("Flight", $"Tree created: id={treeId}, root={rootRecId}, " +
                    $"vesselPid={splitRecorder.RecordingVesselId}");
            }
            else
            {
                // Subsequent split: use the current active recording as parent
                parentRecordingId = activeTree.ActiveRecordingId;

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
                }
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

            // Build the branch data
            var (bp, activeChild, bgChild) = BuildSplitBranchData(
                parentRecordingId, activeTree.Id, branchUT, branchType,
                activeVessel != null ? activeVessel.persistentId : 0,
                activeVessel != null ? activeVessel.vesselName : "Unknown",
                backgroundVessel != null ? backgroundVessel.persistentId : 0,
                backgroundVessel != null ? backgroundVessel.vesselName : "Unknown",
                evaCrewName, evaVesselPid);

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
            activeTree.Recordings[activeChild.RecordingId] = activeChild;
            activeTree.Recordings[bgChild.RecordingId] = bgChild;

            // Set active recording
            activeTree.ActiveRecordingId = activeChild.RecordingId;

            // Add background child to BackgroundMap FIRST, then notify BackgroundRecorder
            if (backgroundVessel != null && backgroundVessel.persistentId != 0)
            {
                activeTree.BackgroundMap[backgroundVessel.persistentId] = bgChild.RecordingId;
                backgroundRecorder?.OnVesselBackgrounded(backgroundVessel.persistentId);
            }

            // Stop any existing undock continuation and vessel continuation (tree handles them)
            if (chainManager.UndockContinuationPid != 0)
                chainManager.StopUndockContinuation("tree branch");
            if (chainManager.ContinuationVesselPid != 0)
                chainManager.StopContinuation("tree branch");

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
            activeTree.Recordings[mergedChild.RecordingId] = mergedChild;

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
        /// Fallback: if tree creation fails, commit captured data as a standalone recording.
        /// </summary>
        void FallbackCommitSplitRecorder(FlightRecorder splitRec)
        {
            if (splitRec?.CaptureAtStop == null) return;

            var captured = splitRec.CaptureAtStop;
            if (captured.Points.Count >= 2)
            {
                RecordingStore.StashPending(
                    captured.Points, captured.VesselName,
                    orbitSegments: captured.OrbitSegments,
                    partEvents: captured.PartEvents,
                    flagEvents: captured.FlagEvents);
                // Copy snapshot/vessel state to the pending recording
                if (RecordingStore.HasPending)
                {
                    var pending = RecordingStore.Pending;
                    pending.VesselSnapshot = captured.VesselSnapshot;
                    pending.GhostVisualSnapshot = captured.GhostVisualSnapshot;
                    pending.VesselDestroyed = captured.VesselDestroyed;
                    pending.VesselSituation = captured.VesselSituation;
                    pending.DistanceFromLaunch = captured.DistanceFromLaunch;
                    pending.MaxDistanceFromLaunch = captured.MaxDistanceFromLaunch;
                    pending.PreLaunchFunds = captured.PreLaunchFunds;
                    pending.PreLaunchScience = captured.PreLaunchScience;
                    pending.PreLaunchReputation = captured.PreLaunchReputation;
                    pending.RewindSaveFileName = captured.RewindSaveFileName;
                    pending.RewindReservedFunds = captured.RewindReservedFunds;
                    pending.RewindReservedScience = captured.RewindReservedScience;
                    pending.RewindReservedRep = captured.RewindReservedRep;

                    // Preserve chain membership if this segment was part of a chain
                    if (chainManager.ActiveChainId != null)
                    {
                        pending.ChainId = chainManager.ActiveChainId;
                        pending.ChainIndex = chainManager.ActiveChainNextIndex;
                        pending.ParentRecordingId = chainManager.ActiveChainPrevId;
                        pending.EvaCrewName = chainManager.ActiveChainCrewName;
                    }

                    // Set terminal state for destroyed vessels
                    if (captured.VesselDestroyed)
                    {
                        pending.TerminalStateValue = TerminalState.Destroyed;
                        ParsekLog.Verbose("Flight", "FallbackCommitSplitRecorder: set TerminalState=Destroyed");
                    }

                    // Tag segment phase if untagged
                    TagSegmentPhaseIfMissing(pending, FlightGlobals.ActiveVessel);
                }

                // Destroyed vessels: discard pad failures, otherwise commit or defer to dialog.
                // Dialog is deferred via coroutine so it appears AFTER KSP's crash report.
                if (RecordingStore.Pending.VesselDestroyed)
                {
                    double dur = RecordingStore.Pending.EndUT - RecordingStore.Pending.StartUT;
                    double maxDist = RecordingStore.Pending.MaxDistanceFromLaunch;
                    if (IsPadFailure(dur, maxDist))
                    {
                        ParsekLog.Info("Flight",
                            $"Vessel destroyed during split — pad failure ({dur:F1}s, {maxDist:F0}m), discarding");
                        RecordingStore.DiscardPending();
                        return;
                    }
                    if (ParsekScenario.IsAutoMerge)
                    {
                        string amRecId = RecordingStore.Pending.RecordingId;
                        double amStart = RecordingStore.Pending.StartUT;
                        double amEnd = RecordingStore.Pending.EndUT;
                        RecordingStore.CommitPending();
                        LedgerOrchestrator.OnRecordingCommitted(amRecId, amStart, amEnd);
                        KerbalsModule.RecalculateAndApply();
                        ParsekLog.Info("Flight",
                            $"Vessel destroyed during split — auto-merged ({dur:F1}s)");
                    }
                    else
                    {
                        // Defer dialog — pending stays stashed, coroutine shows it after 2s
                        ParsekLog.Info("Flight",
                            $"Vessel destroyed during split — deferring dialog ({dur:F1}s)");
                        StartCoroutine(ShowDeferredSplitMergeDialog());
                    }
                    return;
                }

                string recId = RecordingStore.Pending.RecordingId;
                double sUT = RecordingStore.Pending.StartUT;
                double eUT = RecordingStore.Pending.EndUT;
                RecordingStore.CommitPending();
                LedgerOrchestrator.OnRecordingCommitted(recId, sUT, eUT);
                KerbalsModule.RecalculateAndApply();
                string chainInfo = chainManager.ActiveChainId != null
                    ? $" (chain={chainManager.ActiveChainId}, idx={chainManager.ActiveChainNextIndex})"
                    : " (standalone)";
                ParsekLog.Info("Flight", $"Vessel destroyed during split — recording committed{chainInfo}");
            }
            else
            {
                ParsekLog.Warn("Flight", "Split branch failed — recording too short to commit, data lost");
            }
        }

        /// <summary>
        /// Resumes recording after a false-alarm split detection (debris undock, joint break
        /// that didn't produce a trackable vessel, deduplication skip). Re-enables the
        /// stopped recorder and restores it as the active recorder.
        /// </summary>
        void ResumeSplitRecorder(FlightRecorder splitRec, string reason)
        {
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
                        ParsekLog.Info("Coalescer",
                            $"Feeding split to coalescer: childPid={newPid}, childHasController={hasController}");
                        crashCoalescer.OnSplitEvent(branchUT, newPid, hasController);
                    }

                    // Resume the recorder — the coalescer's Tick will emit the BREAKUP
                    // branch point after the coalescing window expires. The recorder needs
                    // to keep running to capture continued trajectory data.
                    ResumeSplitRecorder(pendingSplitRecorder,
                        $"joint break fed to coalescer (classification={classification})");
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
        /// Processes a BREAKUP branch point emitted by the crash coalescer.
        /// If an active tree exists, adds the BREAKUP event as a terminal-like marker
        /// on the current recording segment. If no tree exists but a recorder is active,
        /// promotes the standalone recording into a tree to enable debris tracking.
        /// </summary>
        void ProcessBreakupEvent(BranchPoint breakupBp)
        {
            if (activeTree == null)
            {
                if (recorder != null && recorder.IsRecording)
                {
                    PromoteToTreeForBreakup(breakupBp);
                    return; // promotion handled everything including BREAKUP wiring
                }
                ParsekLog.Info("Coalescer",
                    "ProcessBreakupEvent: no active tree and no active recorder — breakup not recorded. " +
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

            // Create child recording segments for controlled children (vessels with probe
            // cores that survive the breakup). Same pattern as PromoteToTreeForBreakup step 8.
            // These are NOT debris — they record indefinitely (no TTL) and can serve as
            // RELATIVE anchors during playback.
            var controlledChildren = crashCoalescer.LastEmittedControlledChildPids;
            if (controlledChildren != null && controlledChildren.Count > 0)
            {
                for (int i = 0; i < controlledChildren.Count; i++)
                {
                    uint pid = controlledChildren[i];
                    Vessel childVessel = FlightRecorder.FindVesselByPid(pid);
                    string childRecId = Guid.NewGuid().ToString("N");
                    string vesselName = Recording.ResolveLocalizedName(childVessel?.vesselName) ?? "Unknown";

                    var childRec = new Recording
                    {
                        RecordingId = childRecId,
                        TreeId = activeTree.Id,
                        VesselPersistentId = pid,
                        VesselName = vesselName,
                        ParentBranchPointId = breakupBp.Id,
                        ExplicitStartUT = breakupBp.UT,
                        IsDebris = false
                    };

                    if (childVessel != null)
                    {
                        ConfigNode childSnapshot = VesselSpawner.TryBackupSnapshot(childVessel);
                        childRec.GhostVisualSnapshot = childSnapshot;
                        childRec.VesselSnapshot = childSnapshot != null ? childSnapshot.CreateCopy() : null;
                    }
                    else
                    {
                        childRec.TerminalStateValue = TerminalState.Destroyed;
                        childRec.ExplicitEndUT = breakupBp.UT;
                    }

                    activeTree.Recordings[childRecId] = childRec;
                    breakupBp.ChildRecordingIds.Add(childRecId);

                    // Add to BackgroundRecorder for trajectory sampling (no TTL — records indefinitely)
                    if (childVessel != null && backgroundRecorder != null)
                    {
                        activeTree.BackgroundMap[pid] = childRecId;
                        backgroundRecorder.OnVesselBackgrounded(pid);
                    }

                    ParsekLog.Info("Coalescer",
                        $"ProcessBreakupEvent: controlled child created: pid={pid}, " +
                        $"name='{vesselName}', recId={childRecId}, " +
                        $"alive={childVessel != null}, bgRecorder={backgroundRecorder != null}");
                }
            }

            // Also create debris child recordings for new debris from this breakup
            var debrisPids = crashCoalescer.LastEmittedDebrisPids;
            if (debrisPids != null && debrisPids.Count > 0)
            {
                double debrisExpiryUT = breakupBp.UT + BackgroundRecorder.DebrisTTLSeconds;
                for (int i = 0; i < debrisPids.Count; i++)
                {
                    uint pid = debrisPids[i];
                    Vessel debrisVessel = FlightRecorder.FindVesselByPid(pid);
                    string childRecId = Guid.NewGuid().ToString("N");
                    string vesselName = Recording.ResolveLocalizedName(debrisVessel?.vesselName) ?? "Debris";

                    var childRec = new Recording
                    {
                        RecordingId = childRecId,
                        TreeId = activeTree.Id,
                        VesselPersistentId = pid,
                        VesselName = vesselName,
                        ParentBranchPointId = breakupBp.Id,
                        ExplicitStartUT = breakupBp.UT,
                        IsDebris = true
                    };

                    if (debrisVessel != null)
                    {
                        ConfigNode debrisSnapshot = VesselSpawner.TryBackupSnapshot(debrisVessel);
                        childRec.GhostVisualSnapshot = debrisSnapshot;
                        childRec.VesselSnapshot = debrisSnapshot != null ? debrisSnapshot.CreateCopy() : null;
                    }
                    else
                    {
                        childRec.TerminalStateValue = TerminalState.Destroyed;
                        childRec.ExplicitEndUT = breakupBp.UT;
                    }

                    activeTree.Recordings[childRecId] = childRec;
                    breakupBp.ChildRecordingIds.Add(childRecId);

                    if (debrisVessel != null && backgroundRecorder != null)
                    {
                        activeTree.BackgroundMap[pid] = childRecId;
                        backgroundRecorder.OnVesselBackgrounded(pid);
                        backgroundRecorder.SetDebrisExpiry(pid, debrisExpiryUT);
                    }

                    ParsekLog.Info("Coalescer",
                        $"ProcessBreakupEvent: debris child created: pid={pid}, " +
                        $"name='{vesselName}', recId={childRecId}, " +
                        $"alive={debrisVessel != null}");
                }
            }

            ParsekLog.Info("Coalescer",
                $"ProcessBreakupEvent: BREAKUP attached to tree={activeTree.Id}, " +
                $"parentRec={activeRecId}, bpId={breakupBp.Id}, " +
                $"cause={breakupBp.BreakupCause}, debris={breakupBp.DebrisCount}, " +
                $"duration={breakupBp.BreakupDuration:F3}s");
        }

        /// <summary>
        /// Promotes a standalone recording to a tree when a BREAKUP fires without an active tree.
        /// Stops the current recorder, creates a tree with root recording from CaptureAtStop,
        /// creates a continuation recording for the active vessel, creates child recordings for
        /// debris and controlled children, and starts a new FlightRecorder in tree mode.
        /// </summary>
        void PromoteToTreeForBreakup(BranchPoint breakupBp)
        {
            // 1. Stop recorder to capture all accumulated data
            recorder.StopRecordingForChainBoundary();
            var splitRecorder = recorder;
            recorder = null;

            if (splitRecorder.CaptureAtStop == null)
            {
                ParsekLog.Warn("Coalescer",
                    "PromoteToTreeForBreakup: no CaptureAtStop data — cannot promote");
                FallbackCommitSplitRecorder(splitRecorder);
                return;
            }

            // 2. Create tree (same pattern as CreateSplitBranch lines 1483-1537)
            string treeId = Guid.NewGuid().ToString("N");
            string rootRecId = Guid.NewGuid().ToString("N");

            activeTree = new RecordingTree
            {
                Id = treeId,
                TreeName = splitRecorder.CaptureAtStop.VesselName
                           ?? Recording.ResolveLocalizedName(FlightGlobals.ActiveVessel?.vesselName) ?? "Unknown",
                RootRecordingId = rootRecId,
                ActiveRecordingId = null // set below
            };

            activeTree.PreTreeFunds = splitRecorder.PreLaunchFunds;
            activeTree.PreTreeScience = splitRecorder.PreLaunchScience;
            activeTree.PreTreeReputation = splitRecorder.PreLaunchReputation;

            ParsekLog.Info("Coalescer",
                $"PromoteToTreeForBreakup: tree created: id={treeId}, name='{activeTree.TreeName}', " +
                $"vesselPid={splitRecorder.RecordingVesselId}");

            // 3. Create root recording from CaptureAtStop
            var cap = splitRecorder.CaptureAtStop;
            var rootRec = new Recording
            {
                RecordingId = rootRecId,
                TreeId = treeId,
                VesselPersistentId = splitRecorder.RecordingVesselId,
                VesselName = cap.VesselName ?? "Unknown",
                ExplicitEndUT = breakupBp.UT
            };

            rootRec.Points = new List<TrajectoryPoint>(cap.Points);
            rootRec.OrbitSegments = new List<OrbitSegment>(cap.OrbitSegments);
            rootRec.PartEvents = new List<PartEvent>(cap.PartEvents);
            rootRec.TrackSections = new List<TrackSection>(cap.TrackSections);
            rootRec.FlagEvents = new List<FlagEvent>(cap.FlagEvents);
            rootRec.SegmentEvents = new List<SegmentEvent>(cap.SegmentEvents);
            rootRec.GhostVisualSnapshot = cap.GhostVisualSnapshot;
            rootRec.VesselSnapshot = cap.VesselSnapshot;
            rootRec.RewindSaveFileName = cap.RewindSaveFileName;
            rootRec.RewindReservedFunds = cap.RewindReservedFunds;
            rootRec.RewindReservedScience = cap.RewindReservedScience;
            rootRec.RewindReservedRep = cap.RewindReservedRep;
            if (rootRec.Points.Count > 0)
                rootRec.ExplicitStartUT = rootRec.Points[0].ut;

            activeTree.Recordings[rootRecId] = rootRec;

            ParsekLog.Info("Coalescer",
                $"PromoteToTreeForBreakup: root recording created: id={rootRecId}, " +
                $"points={rootRec.Points.Count}, partEvents={rootRec.PartEvents.Count}, " +
                $"endUT={breakupBp.UT:F2}, rewind={!string.IsNullOrEmpty(rootRec.RewindSaveFileName)}");

            // 4. Capture active vessel snapshot for continuation
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            ConfigNode activeSnapshot = VesselSpawner.TryBackupSnapshot(activeVessel);

            // 5. Create continuation recording (active vessel post-split)
            string contRecId = Guid.NewGuid().ToString("N");
            var contRec = new Recording
            {
                RecordingId = contRecId,
                TreeId = treeId,
                VesselPersistentId = splitRecorder.RecordingVesselId,
                VesselName = Recording.ResolveLocalizedName(activeVessel?.vesselName) ?? cap.VesselName ?? "Unknown",
                ParentBranchPointId = breakupBp.Id,
                ExplicitStartUT = breakupBp.UT,
                GhostVisualSnapshot = activeSnapshot,
                VesselSnapshot = activeSnapshot != null ? activeSnapshot.CreateCopy() : null
            };
            // Seed continuation with post-breakup points from root recording.
            // The root's Points extend ~0.5s past breakupBp.UT (coalescing window).
            // Without this, the continuation has 0 points if the vessel is destroyed
            // before FlushRecorderToTreeRecording runs — causing ghost spawn skip
            // and watch mode camera falling back to a debris child (#106).
            double breakupUT = breakupBp.UT;
            for (int i = 0; i < rootRec.Points.Count; i++)
            {
                if (rootRec.Points[i].ut >= breakupUT)
                    contRec.Points.Add(rootRec.Points[i]);
            }

            activeTree.Recordings[contRecId] = contRec;

            ParsekLog.Info("Coalescer",
                $"PromoteToTreeForBreakup: continuation recording created: id={contRecId}, " +
                $"vesselPid={contRec.VesselPersistentId}, snapshot={activeSnapshot != null}, " +
                $"seededPoints={contRec.Points.Count}");

            // 6. Wire BREAKUP into tree
            breakupBp.ParentRecordingIds.Add(rootRecId);
            breakupBp.ChildRecordingIds.Add(contRecId);
            activeTree.BranchPoints.Add(breakupBp);
            rootRec.ChildBranchPointId = breakupBp.Id;

            // 7. Create debris child recordings
            var debrisPids = crashCoalescer.LastEmittedDebrisPids;
            if (debrisPids != null)
            {
                for (int i = 0; i < debrisPids.Count; i++)
                {
                    uint pid = debrisPids[i];
                    Vessel debrisVessel = FlightRecorder.FindVesselByPid(pid);
                    string childRecId = Guid.NewGuid().ToString("N");
                    string vesselName = Recording.ResolveLocalizedName(debrisVessel?.vesselName) ?? "Debris";

                    var childRec = new Recording
                    {
                        RecordingId = childRecId,
                        TreeId = treeId,
                        VesselPersistentId = pid,
                        VesselName = vesselName,
                        ParentBranchPointId = breakupBp.Id,
                        ExplicitStartUT = breakupBp.UT,
                        IsDebris = true
                    };

                    if (debrisVessel != null)
                    {
                        ConfigNode debrisSnapshot = VesselSpawner.TryBackupSnapshot(debrisVessel);
                        childRec.GhostVisualSnapshot = debrisSnapshot;
                        childRec.VesselSnapshot = debrisSnapshot != null ? debrisSnapshot.CreateCopy() : null;
                    }
                    else
                    {
                        // Vessel already destroyed — mark as terminated
                        childRec.TerminalStateValue = TerminalState.Destroyed;
                        childRec.ExplicitEndUT = breakupBp.UT;
                    }

                    activeTree.Recordings[childRecId] = childRec;
                    breakupBp.ChildRecordingIds.Add(childRecId);

                    ParsekLog.Info("Coalescer",
                        $"PromoteToTreeForBreakup: debris child created: pid={pid}, " +
                        $"name='{vesselName}', recId={childRecId}, " +
                        $"alive={debrisVessel != null}");
                }
            }

            // 8. Create controlled child recordings (same pattern, IsDebris = false, no TTL)
            var controlledPids = crashCoalescer.LastEmittedControlledChildPids;
            if (controlledPids != null)
            {
                for (int i = 0; i < controlledPids.Count; i++)
                {
                    uint pid = controlledPids[i];
                    Vessel childVessel = FlightRecorder.FindVesselByPid(pid);
                    string childRecId = Guid.NewGuid().ToString("N");
                    string vesselName = Recording.ResolveLocalizedName(childVessel?.vesselName) ?? "Unknown";

                    var childRec = new Recording
                    {
                        RecordingId = childRecId,
                        TreeId = treeId,
                        VesselPersistentId = pid,
                        VesselName = vesselName,
                        ParentBranchPointId = breakupBp.Id,
                        ExplicitStartUT = breakupBp.UT,
                        IsDebris = false
                    };

                    if (childVessel != null)
                    {
                        ConfigNode childSnapshot = VesselSpawner.TryBackupSnapshot(childVessel);
                        childRec.GhostVisualSnapshot = childSnapshot;
                        childRec.VesselSnapshot = childSnapshot != null ? childSnapshot.CreateCopy() : null;
                    }
                    else
                    {
                        childRec.TerminalStateValue = TerminalState.Destroyed;
                        childRec.ExplicitEndUT = breakupBp.UT;
                    }

                    activeTree.Recordings[childRecId] = childRec;
                    breakupBp.ChildRecordingIds.Add(childRecId);

                    ParsekLog.Info("Coalescer",
                        $"PromoteToTreeForBreakup: controlled child created: pid={pid}, " +
                        $"name='{vesselName}', recId={childRecId}, " +
                        $"alive={childVessel != null}");
                }
            }

            // 9. Set ActiveRecordingId BEFORE RebuildBackgroundMap so the continuation
            // is excluded from BackgroundMap (it's the active vessel, not a background one).
            activeTree.ActiveRecordingId = contRecId;

            // Create BackgroundRecorder
            activeTree.RebuildBackgroundMap();
            backgroundRecorder = new BackgroundRecorder(activeTree);
            backgroundRecorder.SubscribePartEvents();
            Patches.PhysicsFramePatch.BackgroundRecorderInstance = backgroundRecorder;

            ParsekLog.Info("Coalescer",
                $"PromoteToTreeForBreakup: BackgroundRecorder created, " +
                $"backgroundMap={activeTree.BackgroundMap.Count} vessels");

            // 10. Set debris TTL and notify BackgroundRecorder
            double debrisExpiryUT = breakupBp.UT + BackgroundRecorder.DebrisTTLSeconds;
            foreach (var kvp in activeTree.BackgroundMap)
            {
                uint bgPid = kvp.Key;
                backgroundRecorder.OnVesselBackgrounded(bgPid);

                // Set TTL for debris recordings only
                Recording bgRec;
                if (activeTree.Recordings.TryGetValue(kvp.Value, out bgRec) && bgRec.IsDebris)
                {
                    backgroundRecorder.SetDebrisExpiry(bgPid, debrisExpiryUT);
                }
            }

            // 11. Clear stale standalone state
            chainManager.ClearChainIdentity();
            if (chainManager.UndockContinuationPid != 0)
                chainManager.StopUndockContinuation("tree promotion");
            if (chainManager.ContinuationVesselPid != 0)
                chainManager.StopContinuation("tree promotion");

            // 12. Start new FlightRecorder for continuation
            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;
            recorder.StartRecording(isPromotion: true);

            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Coalescer",
                    "PromoteToTreeForBreakup: StartRecording failed for continuation");
                recorder = null;
            }

            // 13. Log the promotion
            int debrisAlive = 0;
            if (debrisPids != null)
            {
                for (int i = 0; i < debrisPids.Count; i++)
                {
                    if (FlightRecorder.FindVesselByPid(debrisPids[i]) != null) debrisAlive++;
                }
            }
            ParsekLog.Info("Coalescer",
                $"PromoteToTreeForBreakup: standalone recording promoted to tree. " +
                $"tree={treeId}, root={rootRecId}, continuation={contRecId}, " +
                $"debris={debrisPids?.Count ?? 0} (alive={debrisAlive}), " +
                $"controlled={controlledPids?.Count ?? 0}, " +
                $"breakup={breakupBp.Id}");
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

        internal enum DestructionMode { None, TreeDeferred, StandaloneMerge, TreeAllLeavesCheck }

        /// <summary>
        /// Pure classification of vessel destruction handling mode.
        /// Matches the branching order in OnVesselWillDestroy: TreeDeferred is checked first,
        /// then StandaloneMerge (non-tree), then TreeAllLeavesCheck (tree with active vessel).
        ///
        /// The original code had three independent if-blocks, but the branches are mutually
        /// exclusive by design: TreeDeferred requires shouldDeferForTree (vessel in BackgroundMap),
        /// while StandaloneMerge/TreeAllLeavesCheck require isActiveVessel. The active vessel
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

            if (!hasActiveTree && isRecording && vesselDestroyedDuringRecording && isActiveVessel)
                return DestructionMode.StandaloneMerge;

            if (hasActiveTree && vesselDestroyedDuringRecording && isActiveVessel
                && !treeDestructionDialogPending)
                return DestructionMode.TreeAllLeavesCheck;

            return DestructionMode.None;
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

            ApplyTerminalDestruction(pending, rec);
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
            recorder?.OnVesselGoOnRails(v);
            backgroundRecorder?.OnBackgroundVesselGoOnRails(v);
        }

        void OnVesselGoOffRails(Vessel v)
        {
            if (v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId)) return;

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

            // Compute surface-relative rotation (v5 format)
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

            // Safety net: if FlightResultsPatch has a pending message that was never replayed
            // (e.g., tree destruction path didn't fire), replay it now
            if (Patches.FlightResultsPatch.HasPendingResults())
            {
                ParsekLog.Warn("Flight", "FlightResults safety net: replaying suppressed results on OnFlightReady");
                Patches.FlightResultsPatch.ReplayFlightResults();
            }

            // Auto-migrate v4 recordings to v5 (world-space → surface-relative rotation)
            if (FlightGlobals.Bodies != null && FlightGlobals.Bodies.Count > 0)
            {
                foreach (var rec in RecordingStore.CommittedRecordings)
                {
                    if (rec.RecordingFormatVersion < 5 && rec.Points.Count > 0)
                        RecordingStore.MigrateV4ToV5(rec);
                }
            }

            ResetFlightReadyState();

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

            // Apply ghost soft cap settings from persisted game parameters
            var capSettings = ParsekSettings.Current;
            if (capSettings != null)
            {
                GhostSoftCapManager.Enabled = capSettings.ghostCapEnabled;
                GhostSoftCapManager.ApplySettings(
                    capSettings.ghostCapZone1Reduce,
                    capSettings.ghostCapZone1Despawn,
                    capSettings.ghostCapZone2Simplify);
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

            // Handle pending tree: show tree merge dialog.
            // On non-revert scene changes, pending trees are auto-committed by ParsekScenario.
            // Reaching here means either a revert or a fallback (auto-commit missed).
            if (RecordingStore.HasPendingTree)
            {
                var pt = RecordingStore.PendingTree;
                // Belt-and-suspenders: mark tree recordings for force-spawn before dialog.
                // The merge dialog callbacks also call MarkForceSpawnOnTreeRecordings,
                // but this covers the case where the tree is committed without a dialog
                // (e.g., auto-merge setting, or future code paths).
                if (activeVessel != null && activeVessel.persistentId != 0)
                    MergeDialog.MarkForceSpawnOnTreeRecordings(pt, activeVessel.persistentId);
                ParsekLog.Warn("Flight", $"Pending tree '{pt.TreeName}' reached OnFlightReady — showing tree merge dialog (fallback)");
                MergeDialog.ShowTreeDialog(pt);
            }

            // Handle pending standalone recording.
            // On non-revert scene changes, pending recordings are auto-committed by ParsekScenario.
            // On reverts, the merge dialog is expected here (player chose to revert).
            if (RecordingStore.HasPending)
            {
                var pending = RecordingStore.Pending;

                // After revert, the original vessel exists at its reverted position (e.g., on the pad),
                // not at the recording's end position. Mark for fresh spawn with a new PID so the
                // dedup check doesn't skip it. Also mark committed chain siblings.
                pending.ForceSpawnNewVessel = true;
                if (!string.IsNullOrEmpty(pending.ChainId))
                {
                    var chainSiblings = RecordingStore.GetChainRecordings(pending.ChainId);
                    if (chainSiblings != null)
                    {
                        for (int cs = 0; cs < chainSiblings.Count; cs++)
                            chainSiblings[cs].ForceSpawnNewVessel = true;
                    }
                }
                ParsekLog.Verbose("Flight", $"Pending recording on flight ready: ForceSpawnNewVessel=true for '{pending.VesselName}'");

                // Check if this is part of a chain with committed siblings
                bool hasChainSiblings = !string.IsNullOrEmpty(pending.ChainId) &&
                    RecordingStore.GetChainRecordings(pending.ChainId) != null;

                if (hasChainSiblings)
                {
                    // Chain-level merge dialog (covers all segments)
                    Log($"Found pending chain recording (chain={pending.ChainId}, idx={pending.ChainIndex})");
                    CommitOrShowDialog(pending);
                }
                else if (!string.IsNullOrEmpty(pending.ParentRecordingId))
                {
                    // Legacy: auto-commit EVA child recordings (skip merge dialog)
                    bool parentFound = false;
                    foreach (var rec in RecordingStore.CommittedRecordings)
                    {
                        if (rec.RecordingId == pending.ParentRecordingId)
                        {
                            parentFound = true;
                            break;
                        }
                    }
                    if (parentFound)
                    {
                        Log($"Auto-committing EVA child recording (parent={pending.ParentRecordingId}, crew={pending.EvaCrewName})");
                        string recId = pending.RecordingId;
                        double startUT = pending.StartUT;
                        double endUT = pending.EndUT;
                        RecordingStore.CommitPending();
                        LedgerOrchestrator.OnRecordingCommitted(recId, startUT, endUT);
                        KerbalsModule.RecalculateAndApply();
                    }
                    else
                    {
                        Log($"EVA child recording has no matching parent — commit or dialog");
                        CommitOrShowDialog(pending);
                    }
                }
                else
                {
                    Log($"Found pending recording from {pending.VesselName} ({pending.Points.Count} points)");
                    CommitOrShowDialog(pending);
                }
            }

            // Mark all committed recordings that share the active vessel's PID for fresh spawn.
            // After revert, the pad vessel has the same PID as previously-recorded vessels.
            // Without this, the PID dedup in SpawnVesselOrChainTip would skip spawning
            // because it finds the pad vessel and thinks the recorded vessel already exists.
            // ForceSpawnNewVessel is transient (not serialized), so this must run every flight entry.
            if (activeVessel != null && activeVessel.persistentId != 0)
            {
                uint activePid = activeVessel.persistentId;
                var allCommitted = RecordingStore.CommittedRecordings;
                int forceCount = 0;
                for (int ci = 0; ci < allCommitted.Count; ci++)
                {
                    if (allCommitted[ci].VesselPersistentId == activePid && !allCommitted[ci].VesselSpawned)
                    {
                        allCommitted[ci].ForceSpawnNewVessel = true;
                        forceCount++;
                    }
                }
                if (forceCount > 0)
                    ParsekLog.Verbose("Flight",
                        $"Marked {forceCount} committed recording(s) with ForceSpawnNewVessel " +
                        $"(active vessel pid={activePid})");
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

            // Clear any suppressed flight results — don't carry stale crash reports across scenes
            if (Patches.FlightResultsPatch.HasPendingResults())
            {
                ParsekLog.Info("Flight", "Clearing suppressed FlightResults on scene change");
                Patches.FlightResultsPatch.ClearPending();
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
        /// split detection, dock/undock, merge detection. Called at the start of OnFlightReady
        /// after v4-to-v5 migration.
        /// </summary>
        private void ResetFlightReadyState()
        {
            ParsekLog.Info("Flight", "Resetting flight-ready state");

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
            var activeChains = new Dictionary<uint, GhostChain>();
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

                // Create ghost map vessel for non-terminated chains ending in stable orbit
                if (!chain.IsTerminated && !string.IsNullOrEmpty(chain.TipRecordingId))
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

            activeGhostChains = activeChains;


            // Wire the ghosted-vessel check for ShouldSkipExternalVesselGhost (Task 6b-3b)
            GhostPlaybackLogic.SetIsGhostedOverride(
                pid => vesselGhoster != null && vesselGhoster.IsGhosted(pid));

            ParsekLog.Info("Ghoster",
                string.Format(CultureInfo.InvariantCulture,
                    "Ghost chains evaluated: {0} chain(s), {1} vessel(s) ghosted",
                    chains.Count, ghostedCount));
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
                if (chainManager.UndockContinuationPid != 0)
                    chainManager.StopUndockContinuation("sibling switch");

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
                double threshold = FlightRecorder.ComputeApproachAltitude(fromCB);
                fromPhase = recorder.Recording.Count > 0 &&
                    recorder.Recording[recorder.Recording.Count - 1].altitude < threshold
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
            if (watchedRecordingIndex >= 0
                && (Input.GetKeyDown(KeyCode.LeftBracket) || Input.GetKeyDown(KeyCode.RightBracket)))
            {
                ExitWatchMode();
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
            // Propagate tree mode to new recorder so DecideOnVesselSwitch uses tree decisions
            if (activeTree != null)
                recorder.ActiveTree = activeTree;
            recorder.StartRecording(isPromotion: isContinuation);
            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight", $"StartRecording blocked: {DetermineRecordingBlockReason()}");
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
            if (recorder?.CaptureAtStop != null && chainManager.ActiveChainId != null)
            {
                recorder.CaptureAtStop.ChainId = chainManager.ActiveChainId;
                recorder.CaptureAtStop.ChainIndex = chainManager.ActiveChainNextIndex;
                recorder.CaptureAtStop.ParentRecordingId = chainManager.ActiveChainPrevId;
                recorder.CaptureAtStop.EvaCrewName = chainManager.ActiveChainCrewName;
            }

            // Tag final segment phase if untagged
            if (recorder?.CaptureAtStop != null && string.IsNullOrEmpty(recorder.CaptureAtStop.SegmentPhase))
            {
                var v = FlightGlobals.ActiveVessel;
                if (v != null && v.mainBody != null)
                {
                    recorder.CaptureAtStop.SegmentBodyName = v.mainBody.name;
                    if (v.mainBody.atmosphere)
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

        public void CommitFlight()
        {
            ParsekLog.Info("Flight", $"CommitFlight called with {recorder?.Recording?.Count ?? 0} points");

            // Guard: no recording data
            if (recorder == null || recorder.Recording.Count < 2)
            {
                ParsekLog.ScreenMessage("No recording to commit", 2f);
                return;
            }

            // Guard: mid-chain recordings can't be committed standalone
            if (chainManager.ActiveChainId != null)
            {
                ParsekLog.ScreenMessage("Cannot commit mid-chain \u2014 finish or revert first", 2f);
                Log("CommitFlight blocked: active chain in progress");
                return;
            }

            // Stop recording if still active
            if (recorder.IsRecording)
                StopRecording();

            // Stop any continuation sampling
            chainManager.StopAllContinuations("commit flight");

            string vesselName = FlightGlobals.ActiveVessel != null
                ? FlightGlobals.ActiveVessel.vesselName
                : "Unknown Vessel";

            var captured = recorder.CaptureAtStop;

            // Stash as pending recording (reuses OnSceneChangeRequested pattern)
            RecordingStore.StashPending(
                recorder.Recording,
                vesselName,
                recorder.OrbitSegments,
                recordingId: captured != null ? captured.RecordingId : null,
                recordingFormatVersion: captured != null ? (int?)captured.RecordingFormatVersion : null,
                partEvents: recorder.PartEvents,
                flagEvents: recorder.FlagEvents);

            if (!RecordingStore.HasPending)
            {
                Log("CommitFlight: StashPending rejected recording (too short?)");
                return;
            }

            // Apply snapshot/geometry artifacts from stop-time capture
            if (captured != null)
            {
                RecordingStore.Pending.ApplyPersistenceArtifactsFrom(captured);
                Log("CommitFlight: applied stop-time artifacts");
            }
            else
            {
                // Fallback: capture vessel now
                bool wasDestroyed = recorder.VesselDestroyedDuringRecording;
                VesselSpawner.SnapshotVessel(
                    RecordingStore.Pending,
                    wasDestroyed,
                    destroyedFallbackSnapshot: recorder.InitialGhostVisualSnapshot
                        ?? recorder.LastGoodVesselSnapshot);
                RecordingStore.Pending.GhostVisualSnapshot =
                    recorder.InitialGhostVisualSnapshot != null
                        ? recorder.InitialGhostVisualSnapshot.CreateCopy()
                        : (RecordingStore.Pending.VesselSnapshot != null
                            ? RecordingStore.Pending.VesselSnapshot.CreateCopy()
                            : null);
                Log("CommitFlight: captured vessel snapshot (fallback path)");
            }

            // Mark as non-revert commit:
            // - VesselSpawned=true prevents duplicate spawn while vessel is in-game
            // - SpawnedVesselPersistentId enables duplicate detection on save/load
            // - LastAppliedResourceIndex prevents double-applying deltas
            // All three reset naturally on "Revert to Launch" (launch quicksave has no RECORDING nodes)
            var pending = RecordingStore.Pending;
            pending.VesselSpawned = true;
            pending.SpawnedVesselPersistentId = recorder.RecordingVesselId;
            pending.LastAppliedResourceIndex = pending.Points.Count - 1;

            // Commit to timeline
            string recId = pending.RecordingId;
            double startUT = pending.StartUT;
            double endUT = pending.EndUT;
            RecordingStore.CommitPending();
            LedgerOrchestrator.OnRecordingCommitted(recId, startUT, endUT);
            KerbalsModule.RecalculateAndApply();

            // Clear recorder state
            recorder = null;
            lastPlaybackIndex = 0;

            Log($"CommitFlight: committed \"{vesselName}\" to timeline");
            ParsekLog.ScreenMessage("Flight committed to timeline!", 3f);
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
            KerbalsModule.RecalculateAndApply();

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
                GameStateRecorder.SuppressCrewEvents = true;
                try
                {
                    foreach (var leaf in spawnableLeaves)
                    {
                        if (leaf.VesselSpawned) continue;
                        if (leaf.VesselSnapshot == null) continue;
                        CrewReservationManager.ReserveCrewIn(leaf.VesselSnapshot, false, roster);
                    }
                }
                finally
                {
                    GameStateRecorder.SuppressCrewEvents = false;
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

            // Finalize all recordings (active + background)
            FinalizeTreeRecordings(activeTree, commitUT, isSceneExit: false);

            // Stash as pending tree -- snapshots preserved for merge dialog
            RecordingStore.StashPendingTree(activeTree);

            ParsekLog.Info("Flight",
                $"CommitTreeRevert: stashed pending tree '{activeTree.TreeName}' with snapshots preserved");
        }

        /// <summary>
        /// Commits the active tree on scene exit (ghost-only, no spawning).
        /// Finalizes all recordings and stashes the tree as pending.
        /// </summary>
        void CommitTreeSceneExit(double commitUT)
        {
            if (activeTree == null) return;

            ParsekLog.Info("Flight", $"CommitTreeSceneExit: finalizing tree at UT={commitUT:F1}");

            // Finalize all recordings
            FinalizeTreeRecordings(activeTree, commitUT, isSceneExit: true);

            // Null all vessel snapshots (no spawning on scene exit)
            foreach (var rec in activeTree.Recordings.Values)
            {
                rec.VesselSnapshot = null;
            }

            // Stash as pending tree -- auto-committed ghost-only by ParsekScenario.OnLoad
            RecordingStore.StashPendingTree(activeTree);

            ParsekLog.Info("Flight", $"CommitTreeSceneExit: stashed pending tree '{activeTree.TreeName}'");
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

                // Copy rewind save filename to the tree's root recording.
                // BuildCaptureRecording clears recorder.RewindSaveFileName after capture,
                // so we check CaptureAtStop first (has the captured copy), then recorder.
                string rewindSave = recorder.CaptureAtStop?.RewindSaveFileName
                    ?? recorder.RewindSaveFileName;
                if (!string.IsNullOrEmpty(rewindSave)
                    && !string.IsNullOrEmpty(tree.RootRecordingId))
                {
                    Recording rootRec;
                    if (tree.Recordings.TryGetValue(tree.RootRecordingId, out rootRec))
                    {
                        rootRec.RewindSaveFileName = rewindSave;
                        ParsekLog.Info("Flight",
                            $"FinalizeTreeRecordings: copied rewind save '{rewindSave}' to root recording '{tree.RootRecordingId}'");
                    }
                }
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
            if (!string.IsNullOrEmpty(tree.ActiveRecordingId))
            {
                Recording activeRec;
                if (tree.Recordings.TryGetValue(tree.ActiveRecordingId, out activeRec))
                {
                    if (activeRec.TerminalStateValue.HasValue)
                    {
                        Log($"FinalizeTreeRecordings: active recording '{activeRec.RecordingId}' " +
                            $"already has terminalState={activeRec.TerminalStateValue} — skipping");
                    }
                    else
                    {
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
                }
            }

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

        private void FinalizeIndividualRecording(Recording rec, double commitUT, bool isSceneExit)
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

            // Determine terminal state for recordings that don't have one yet
            bool isLeaf = rec.ChildBranchPointId == null;
            if (isLeaf && !rec.TerminalStateValue.HasValue)
            {
                // Try to find the vessel
                Vessel vessel = rec.VesselPersistentId != 0
                    ? FlightRecorder.FindVesselByPid(rec.VesselPersistentId)
                    : null;

                if (vessel != null)
                {
                    rec.TerminalStateValue = RecordingTree.DetermineTerminalState((int)vessel.situation, vessel);
                    CaptureTerminalOrbit(rec, vessel);
                    CaptureTerminalPosition(rec, vessel);

                    // Re-snapshot live vessels for Commit Flight path (fresh state)
                    if (!isSceneExit)
                    {
                        ConfigNode freshSnapshot = VesselSpawner.TryBackupSnapshot(vessel);
                        if (freshSnapshot != null)
                        {
                            rec.VesselSnapshot = freshSnapshot;
                            if (rec.GhostVisualSnapshot == null)
                                rec.GhostVisualSnapshot = freshSnapshot.CreateCopy();
                        }
                    }
                }
                else
                {
                    // Vessel not found — mark as destroyed defensively
                    rec.TerminalStateValue = TerminalState.Destroyed;
                    rec.VesselSnapshot = null;
                    ParsekLog.Warn("Flight", $"FinalizeTreeRecordings: vessel pid={rec.VesselPersistentId} " +
                        $"not found for recording '{rec.RecordingId}' — marking Destroyed");
                }
            }

            // Warn if leaf has no playback data
            if (isLeaf && rec.Points.Count == 0 && rec.OrbitSegments.Count == 0 && !rec.SurfacePos.HasValue)
                ParsekLog.Warn("Flight", $"FinalizeTreeRecordings: leaf '{rec.RecordingId}' has no playback data");

            Log($"FinalizeTreeRecordings: rec='{rec.RecordingId}' vessel='{rec.VesselName}' " +
                $"points={rec.Points.Count} orbitSegs={rec.OrbitSegments.Count} " +
                $"terminal={rec.TerminalStateValue?.ToString() ?? "none"} " +
                $"snapshot={rec.VesselSnapshot != null} leaf={isLeaf}");
        }

        /// <summary>
        /// Removes zero-point leaf recordings from the tree (#173). These are debris fragments
        /// that were destroyed within the same physics frame as their creation — they have no
        /// trajectory data, no snapshot, and serve no purpose.
        /// </summary>
        private static void PruneZeroPointLeaves(RecordingTree tree)
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
                $"PruneZeroPointLeaves: removed {toPrune.Count} zero-point debris leaf recording(s)" +
                (prunedBPs > 0 ? $" and {prunedBPs} empty branch point(s)" : "") +
                $" from tree '{tree.TreeName}'");
        }

        /// <summary>
        /// Pure static helper: collects IDs of leaf recordings with no playback data.
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
        static void CaptureTerminalOrbit(Recording rec, Vessel vessel)
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

                // Capture terrain height for terrain correction on spawn (v7+)
                if (vessel.mainBody != null)
                {
                    rec.TerrainHeightAtEnd = vessel.mainBody.TerrainAltitude(vessel.latitude, vessel.longitude);
                    ParsekLog.Verbose("TerrainCorrect",
                        $"Captured terrain height at recording end: {rec.TerrainHeightAtEnd:F1}m " +
                        $"(vessel alt={vessel.altitude:F1}m, clearance={vessel.altitude - rec.TerrainHeightAtEnd:F1}m)");
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
                            GhostVisualBuilder.GetGhostSnapshot(previewRecording))
                    };

                    previewGhostState.materials = new List<Material>();

                    GhostPlaybackLogic.PopulateGhostInfoDictionaries(previewGhostState, buildResult);

                    GhostPlaybackLogic.InitializeInventoryPlacementVisibility(previewRecording, previewGhostState);

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
            bool srfRel = previewRecording != null && previewRecording.RecordingFormatVersion >= 5;
            bool surfaceSkip = previewRecording != null &&
                TrajectoryMath.IsSurfaceAtUT(previewRecording.TrackSections, recordingTime);
            InterpolateAndPosition(ghostObject, recording, orbitSegments,
                ref lastPlaybackIndex, recordingTime, 10000, out interpResult,
                surfaceRelativeRotation: srfRel, skipOrbitSegments: surfaceSkip);

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
        /// Finds a committed recording that covers the given UT for a vessel PID.
        /// Used to find background recording trajectory data for chain ghosts.
        /// Returns null if no recording covers this UT for this vessel.
        /// </summary>
        internal static Recording FindBackgroundRecordingForVessel(
            List<Recording> committedRecordings, uint vesselPid, double currentUT)
        {
            if (committedRecordings == null) return null;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (rec.VesselPersistentId == vesselPid &&
                    rec.Points != null && rec.Points.Count > 0 &&
                    currentUT >= rec.StartUT && currentUT <= rec.EndUT)
                    return rec;
            }
            return null;
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
        internal const double PhysicsBubbleSpawnRadius = 2300.0;

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

                // Find background recording covering current UT
                Recording bgRec = FindBackgroundRecordingForVessel(
                    RecordingStore.CommittedRecordings, chain.OriginalVesselPid, currentUT);

                if (bgRec != null && bgRec.Points.Count > 0)
                {
                    // Position from trajectory points (same interpolation as existing ghost positioning)
                    bool srfRel = bgRec.RecordingFormatVersion >= 5;
                    bool surfaceSkip = TrajectoryMath.IsSurfaceAtUT(bgRec.TrackSections, currentUT);
                    InterpolateAndPosition(info.ghostGO, bgRec.Points, bgRec.OrbitSegments,
                        ref chain.CachedTrajectoryIndex, currentUT, (int)(chain.OriginalVesselPid * 10000),
                        out _, surfaceRelativeRotation: srfRel, skipOrbitSegments: surfaceSkip);

                    // Update ghost map ProtoVessel orbit if segment changed
                    UpdateChainGhostOrbitIfNeeded(chain, bgRec.OrbitSegments, currentUT);

                    ParsekLog.VerboseRateLimited("Flight", "chain-ghost-trajectory-" + chain.OriginalVesselPid,
                        string.Format(CultureInfo.InvariantCulture,
                            "Chain ghost positioned from trajectory: pid={0} rec={1} UT={2:F1}",
                            chain.OriginalVesselPid, bgRec.RecordingId, currentUT));
                }
                else
                {
                    // Fallback: check for orbit segments or surface position on any committed recording
                    // for this vessel that has orbital/surface data
                    bool positioned = false;
                    var committed = RecordingStore.CommittedRecordings;
                    if (committed != null)
                    {
                        for (int i = 0; i < committed.Count; i++)
                        {
                            var rec = committed[i];
                            if (rec.VesselPersistentId != chain.OriginalVesselPid) continue;

                            if (rec.OrbitSegments != null && rec.OrbitSegments.Count > 0)
                            {
                                PositionGhostFromOrbitOnly(info.ghostGO, rec, currentUT,
                                    (int)(chain.OriginalVesselPid * 10000));
                                UpdateChainGhostOrbitIfNeeded(chain, rec.OrbitSegments, currentUT);
                                positioned = true;
                                ParsekLog.VerboseRateLimited("Flight",
                                    "chain-ghost-orbit-" + chain.OriginalVesselPid,
                                    string.Format(CultureInfo.InvariantCulture,
                                        "Chain ghost positioned from orbit: pid={0} rec={1} UT={2:F1}",
                                        chain.OriginalVesselPid, rec.RecordingId, currentUT));
                                break;
                            }

                            if (rec.SurfacePos.HasValue)
                            {
                                PositionGhostAtSurface(info.ghostGO, rec.SurfacePos.Value);
                                positioned = true;
                                ParsekLog.VerboseRateLimited("Flight",
                                    "chain-ghost-surface-" + chain.OriginalVesselPid,
                                    string.Format(CultureInfo.InvariantCulture,
                                        "Chain ghost positioned at surface: pid={0} rec={1} body={2}",
                                        chain.OriginalVesselPid, rec.RecordingId,
                                        rec.SurfacePos.Value.body));
                                break;
                            }
                        }
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
            FindNextWatchTarget(currentIndex, currentRec);

        /// <summary>Called by policy to transfer watch to the next chain segment.</summary>
        internal void TransferWatchToNextSegmentFromPolicy(int nextIndex) =>
            TransferWatchToNextSegment(nextIndex);

        /// <summary>Called by policy to exit watch mode.</summary>
        internal void ExitWatchModeFromPolicy() => ExitWatchMode();

        /// <summary>Called by policy to switch camera to a spawned vessel after watch-mode spawn.</summary>
        internal void DeferredActivateVesselFromPolicy(uint vesselPid) =>
            StartCoroutine(DeferredActivateVessel(vesselPid));

        /// <summary>Called by policy to start the watch-mode hold timer at recording end.</summary>
        internal void StartWatchHoldFromPolicy(float holdUntilRealTime)
        {
            watchEndHoldUntilRealTime = holdUntilRealTime;
            ParsekLog.Info("CameraFollow",
                $"Watch hold timer set: holdUntilRealTime={holdUntilRealTime:F1} (watched #{watchedRecordingIndex})");
        }

        private void SpawnVesselOrChainTip(Recording rec, int index)
        {
            // Real vessel dedup: if a vessel with this PID still exists in the game world,
            // skip spawning a duplicate. This runs only at actual spawn time (pastChainEnd),
            // not every frame like ShouldSpawnAtRecordingEnd.
            // Exception: ForceSpawnNewVessel is set after revert — the existing vessel is at the
            // reverted position, not the recording's end position. Spawn a fresh copy with a new PID.
            if (!rec.ForceSpawnNewVessel &&
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
            List<Recording> committed, double currentUT)
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

                flags[i] = new TrajectoryPlaybackFlags
                {
                    skipGhost = !hasData || !rec.PlaybackEnabled || externalVesselSuppressed,
                    isStandalone = !rec.IsTreeRecording,
                    isMidChain = RecordingStore.IsChainMidSegment(rec),
                    chainEndUT = RecordingStore.GetChainEndUT(rec),
                    needsSpawn = spawnResult.needsSpawn && !chainSuppressed.suppressed,
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

            // Flush deferred spawns from warp
            FlushDeferredSpawns(committed);

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
                protectedIndex = watchedRecordingIndex,
                externalGhostCount = activeGhostChains?.Count ?? 0,
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
                    if (committed[fi].RecordingId == ffId && HasActiveGhost(fi))
                    {
                        EnterWatchMode(fi);
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
            if (watchedRecordingIndex >= 0)
            {
                if (!engine.HasActiveGhost(watchedRecordingIndex))
                {
                    // Check overlap ghosts
                    bool hasOverlap = false;
                    if (watchedOverlapCycleIndex >= 0)
                    {
                        List<GhostPlaybackState> overlaps;
                        if (engine.TryGetOverlapGhosts(watchedRecordingIndex, out overlaps))
                        {
                            for (int i = 0; i < overlaps.Count; i++)
                            {
                                if (overlaps[i]?.loopCycleIndex == watchedOverlapCycleIndex)
                                { hasOverlap = true; break; }
                            }
                        }
                    }
                    if (!hasOverlap && watchedOverlapCycleIndex != -2) // -2 = holding after explosion
                    {
                        ParsekLog.Info("CameraFollow",
                            $"Watched ghost #{watchedRecordingIndex} no longer active — exiting watch mode");
                        ExitWatchMode();
                    }
                }
            }
        }

        // UpdateTimelinePlayback removed (T25 Phase 9 — engine is primary path)


        private void FlushDeferredSpawns(List<Recording> committed)
        {
            if (!GhostPlaybackLogic.ShouldFlushDeferredSpawns(pendingSpawnRecordingIds.Count, IsAnyWarpActive()))
                return;

            int spawnedCount = 0;
            int deferredOutOfBubble = 0;
            var flushedIds = new List<string>();
            Vector3d activeVesselPos = FlightGlobals.ActiveVessel != null
                ? FlightGlobals.ActiveVessel.GetWorldPos3D()
                : Vector3d.zero;

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (!pendingSpawnRecordingIds.Contains(rec.RecordingId)) continue;
                if (GhostPlaybackLogic.ShouldSkipDeferredSpawn(rec.VesselSpawned, rec.VesselSnapshot != null))
                {
                    ParsekLog.Verbose("Flight", $"Deferred spawn skipped — #{i} \"{rec.VesselName}\" already spawned or no snapshot");
                    flushedIds.Add(rec.RecordingId);
                    continue;
                }

                // Physics-bubble scoping: skip out-of-bubble spawns (keep in queue)
                if (FlightGlobals.ActiveVessel != null &&
                    ShouldDeferSpawnOutsideBubble(rec, activeVesselPos))
                {
                    deferredOutOfBubble++;
                    ParsekLog.Verbose("Flight",
                        $"Deferred spawn kept in queue (outside physics bubble): #{i} \"{rec.VesselName}\"");
                    continue;
                }

                ParsekLog.Info("Flight", $"Deferred spawn executing: #{i} \"{rec.VesselName}\" id={rec.RecordingId}");
                SpawnVesselOrChainTip(rec, i);
                spawnedCount++;
                flushedIds.Add(rec.RecordingId);

                // Restore camera follow if this recording was being watched when deferred
                if (GhostPlaybackLogic.ShouldRestoreWatchMode(pendingWatchRecordingId, rec.RecordingId, rec.SpawnedVesselPersistentId))
                {
                    ParsekLog.Info("CameraFollow",
                        $"Deferred watch: switching to spawned vessel pid={rec.SpawnedVesselPersistentId}");
                    StartCoroutine(DeferredActivateVessel(rec.SpawnedVesselPersistentId));
                }
            }

            // Remove flushed IDs, keep out-of-bubble spawns in queue
            for (int j = 0; j < flushedIds.Count; j++)
                pendingSpawnRecordingIds.Remove(flushedIds[j]);

            if (deferredOutOfBubble > 0)
            {
                ParsekLog.Info("Flight",
                    $"Warp ended — flushed {spawnedCount} deferred spawn(s), {deferredOutOfBubble} kept (outside bubble), {pendingSpawnRecordingIds.Count} remaining");
            }
            else
            {
                ParsekLog.Info("Flight", $"Warp ended — flushed {spawnedCount}/{spawnedCount + flushedIds.Count - spawnedCount} deferred spawn(s)");
                pendingSpawnRecordingIds.Clear();
            }

            if (pendingSpawnRecordingIds.Count == 0)
                pendingWatchRecordingId = null;
        }

        // EvaluateGhostSoftCaps removed (T25 Phase 9 — engine is primary path)


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
                GameStateRecorder.SuppressResourceEvents = true;
                try
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
                finally
                {
                    GameStateRecorder.SuppressResourceEvents = false;
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

            GameStateRecorder.SuppressResourceEvents = true;
            try
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
            finally
            {
                GameStateRecorder.SuppressResourceEvents = false;
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

        public void DestroyAllTimelineGhosts()
        {
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
            notifiedSpawnRecordingIds.Clear();
            loggedRelativeStart.Clear();
            loggedAnchorNotFound.Clear();

            // Policy state (still on ParsekFlight until Phase 6 moves it)
            pendingSpawnRecordingIds.Clear();
            pendingWatchRecordingId = null;
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
            if (watchedRecordingIndex == recIdx && watchedOverlapCycleIndex != -1)
            {
                ParsekLog.Info("CameraFollow",
                    $"Overlap ghosts destroyed for watched recording #{recIdx} — resetting overlap cycle tracking from {watchedOverlapCycleIndex}");
                watchedOverlapCycleIndex = -1;
                overlapRetargetAfterUT = -1;
            }
        }

        public bool CanDeleteRecording =>
            !IsRecording && chainManager.ContinuationRecordingIdx < 0 && chainManager.UndockContinuationRecIdx < 0;

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
            if (watchedRecordingIndex >= 0)
            {
                var result = ComputeWatchIndexAfterDelete(
                    watchedRecordingIndex, watchedRecordingId, index,
                    RecordingStore.CommittedRecordings);
                if (result.newIndex < 0)
                {
                    ParsekLog.Warn("CameraFollow",
                        $"Watched recording \"{watchedRecordingId}\" deleted \u2014 auto-exiting watch mode");
                    ExitWatchMode();
                }
                else
                {
                    int oldIdx = watchedRecordingIndex;
                    watchedRecordingIndex = result.newIndex;
                    watchedRecordingId = result.newId;
                    if (oldIdx != result.newIndex)
                        ParsekLog.Info("CameraFollow",
                            $"Recording deleted at #{index} \u2014 watchedRecordingIndex adjusted from {oldIdx} to {result.newIndex}");
                }
            }

            // Reindex all engine-owned ghost state (dicts + sets)
            engine.ReindexAfterDelete(index);

            // Clear orbit cache — keys are index-derived (i * 10000 + segIdx),
            // so after reindexing they would map to wrong recordings
            orbitCache.Clear();

            // Clear ParsekFlight-local diagnostic guards since indices shifted
            loggedOrbitSegments.Clear();
            loggedOrbitRotationSegments.Clear();
            pendingSpawnRecordingIds.Remove(rec.RecordingId);

            ParsekLog.ScreenMessage($"Recording '{rec.VesselName}' deleted", 2f);
        }

        /// <summary>
        /// Returns true if the recording should be discarded as a pad failure:
        /// duration &lt; 10s AND max distance from launch &lt; 30m.
        /// </summary>
        internal static bool IsPadFailure(double duration, double maxDistanceFromLaunch)
        {
            return duration < 10.0 && maxDistanceFromLaunch < 30.0;
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
                double duration = rec.EndUT - rec.StartUT;
                if (!IsPadFailure(duration, rec.MaxDistanceFromLaunch))
                    return false;
            }
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


        /// <summary>
        /// Drive the camera pivot position every frame during watch mode.
        /// pivotTranslateSharpness is zeroed for the entire watch session to prevent
        /// KSP from pulling the camera back toward the active vessel.
        /// </summary>
        void UpdateWatchCamera()
        {
            if (watchedRecordingIndex < 0) return;

            // Watch-end hold timer: after non-looped playback completes, the ghost
            // is held visible for a few seconds so the user sees the terminal state.
            // During the hold, try to auto-follow to a continuation ghost each frame
            // (continuation may not exist yet at completion time — spawn race condition).
            // Once expired, destroy the ghost and exit watch mode.
            if (watchEndHoldUntilRealTime > 0)
            {
                int idx = watchedRecordingIndex;
                var committed = RecordingStore.CommittedRecordings;

                // Try auto-follow each frame during the hold — continuation ghost
                // may have spawned since the initial check in HandlePlaybackCompleted
                if (idx >= 0 && idx < committed.Count)
                {
                    int nextTarget = FindNextWatchTarget(idx, committed[idx]);
                    if (nextTarget >= 0)
                    {
                        ParsekLog.Info("CameraFollow",
                            $"Watch hold auto-follow: #{idx} → #{nextTarget} (during hold period)");
                        watchEndHoldUntilRealTime = -1;
                        GhostPlaybackState held;
                        if (ghostStates.TryGetValue(idx, out held) && held != null)
                        {
                            var traj = committed[idx] as IPlaybackTrajectory;
                            engine.DestroyGhost(idx, traj, default(TrajectoryPlaybackFlags),
                                reason: "auto-followed during hold");
                        }
                        TransferWatchToNextSegment(nextTarget);
                        return;
                    }
                }

                // Hold expired — no continuation found, destroy and exit
                if (Time.time >= watchEndHoldUntilRealTime)
                {
                    ParsekLog.Info("CameraFollow",
                        $"Watch hold expired for #{idx} at t={watchEndHoldUntilRealTime:F1} — destroying ghost and exiting watch");
                    watchEndHoldUntilRealTime = -1;
                    GhostPlaybackState held;
                    if (ghostStates.TryGetValue(idx, out held) && held != null)
                    {
                        if (idx >= 0 && idx < committed.Count)
                        {
                            var traj = committed[idx] as IPlaybackTrajectory;
                            engine.DestroyGhost(idx, traj, default(TrajectoryPlaybackFlags), reason: "watch hold expired");
                        }
                    }
                    ExitWatchMode();
                    return;
                }

                // Still in hold period — don't process further (ghost is frozen at final pos)
                return;
            }

            // Safety net: orphaned -2 state with invalid timer — clear to -1 so
            // normal logic can take over (or exit watch mode via safety net below)
            if (watchedOverlapCycleIndex == -2 && overlapRetargetAfterUT <= 0)
            {
                ParsekLog.Warn("CameraFollow",
                    $"Orphaned hold state (overlapRetargetAfterUT={overlapRetargetAfterUT:F1}) — clearing");
                watchedOverlapCycleIndex = -1;
                if (overlapCameraAnchor != null) { Destroy(overlapCameraAnchor); overlapCameraAnchor = null; }
            }

            // Delayed re-target after watched overlap cycle exploded
            if (TryResolveOverlapRetarget())
                return;

            // Find the ghost we're actually following — may be an overlap ghost, not the primary
            GhostPlaybackState state = null;
            if (watchedOverlapCycleIndex >= 0)
            {
                // Look for the watched cycle in the overlap list
                List<GhostPlaybackState> overlaps;
                if (overlapGhosts.TryGetValue(watchedRecordingIndex, out overlaps))
                {
                    for (int i = 0; i < overlaps.Count; i++)
                    {
                        if (overlaps[i] != null && overlaps[i].loopCycleIndex == watchedOverlapCycleIndex)
                        {
                            state = overlaps[i];
                            break;
                        }
                    }
                }
                // Also check if the primary IS the watched cycle
                if (state == null)
                {
                    GhostPlaybackState primary;
                    if (ghostStates.TryGetValue(watchedRecordingIndex, out primary)
                        && primary != null && primary.loopCycleIndex == watchedOverlapCycleIndex)
                        state = primary;
                }

                // Fallback: tracked cycle not found — switch to primary if available
                if (state == null)
                {
                    GhostPlaybackState primary;
                    if (ghostStates.TryGetValue(watchedRecordingIndex, out primary)
                        && primary != null && primary.ghost != null
                        && primary.cameraPivot != null)
                    {
                        state = primary;
                        watchedOverlapCycleIndex = primary.loopCycleIndex;
                        if (FlightCamera.fetch != null)
                            FlightCamera.fetch.SetTargetTransform(primary.cameraPivot);
                        ParsekLog.Info("CameraFollow",
                            $"Watched cycle lost — falling back to primary cycle={primary.loopCycleIndex}");
                    }
                }
            }
            else
            {
                ghostStates.TryGetValue(watchedRecordingIndex, out state);
            }

            if (FlightCamera.fetch == null || FlightCamera.fetch.transform == null
                || FlightCamera.fetch.transform.parent == null)
                return;

            if (state == null || state.cameraPivot == null)
            {
                // No valid target — count frames and exit if persistent
                watchNoTargetFrames++;
                if (watchNoTargetFrames >= 3)
                {
                    ParsekLog.Warn("CameraFollow",
                        $"No valid camera target for {watchNoTargetFrames} frames — exiting watch mode");
                    ExitWatchMode();
                }
                return;
            }

            // Valid target found — reset safety counter
            watchNoTargetFrames = 0;

            // Keep sharpness zeroed — KSP resets it on various events
            FlightCamera.fetch.pivotTranslateSharpness = 0f;

            // Drive camera orbit center to the cameraPivot's world position
            FlightCamera.fetch.transform.parent.position = state.cameraPivot.position;
        }

        private bool TryResolveOverlapRetarget()
        {
            if (watchedOverlapCycleIndex != -2 || overlapRetargetAfterUT <= 0)
                return false;

            if (Planetarium.GetUniversalTime() >= overlapRetargetAfterUT)
            {
                // Destroy temp camera anchor
                if (overlapCameraAnchor != null)
                {
                    Destroy(overlapCameraAnchor);
                    overlapCameraAnchor = null;
                }
                overlapRetargetAfterUT = -1;

                // Immediately target the current primary ghost so FlightCamera
                // doesn't reference the destroyed anchor
                GhostPlaybackState primary;
                if (FlightCamera.fetch != null
                    && ghostStates.TryGetValue(watchedRecordingIndex, out primary)
                    && primary != null && primary.ghost != null)
                {
                    var target = primary.cameraPivot ?? primary.ghost.transform;
                    FlightCamera.fetch.SetTargetTransform(target);
                    watchedOverlapCycleIndex = primary.loopCycleIndex;
                    watchNoTargetFrames = 0;
                    ParsekLog.Info("CameraFollow",
                        $"Overlap: hold ended, now following cycle={primary.loopCycleIndex}");
                }
                else
                {
                    // No primary available yet — set -1, let safety net below
                    // exit watch mode if no ghost appears within a few frames
                    watchedOverlapCycleIndex = -1;
                    ParsekLog.Info("CameraFollow",
                        $"Overlap: hold ended, no primary ghost available — waiting for next spawn");
                }
                return false;
            }
            else
            {
                // During hold, keep camera where it is (don't update position)
                if (FlightCamera.fetch != null)
                    FlightCamera.fetch.pivotTranslateSharpness = 0f;
                watchNoTargetFrames = 0;
                return true;
            }
        }

        bool IsAnyWarpActive() => GhostPlaybackEngine.IsAnyWarpActiveFromGlobals();

        void ExitAllWarpForPlaybackStart(string context)
        {
            if (!IsAnyWarpActive()) return;
            TimeWarp.SetRate(0, true);
            Log($"Warp reset for playback start ({context})");
        }

        #endregion

        #region Camera Follow (Watch Mode)

        // === Camera event handlers for engine loop/overlap cycle transitions ===

        private void HandleLoopCameraAction(CameraActionEvent evt)
        {
            if (watchedRecordingIndex != evt.Index) return; // not watching this recording

            switch (evt.Action)
            {
                case CameraActionType.ExitWatch:
                    ExitWatchMode();
                    break;

                case CameraActionType.ExplosionHoldStart:
                    if (overlapCameraAnchor != null) Destroy(overlapCameraAnchor);
                    overlapCameraAnchor = new UnityEngine.GameObject("ParsekLoopCameraAnchor");
                    overlapCameraAnchor.transform.position = evt.AnchorPosition;
                    if (FlightCamera.fetch != null)
                        FlightCamera.fetch.SetTargetTransform(overlapCameraAnchor.transform);
                    overlapRetargetAfterUT = evt.HoldUntilUT;
                    watchedOverlapCycleIndex = -2;
                    ParsekLog.Info("CameraFollow",
                        $"Loop: camera holding at explosion for #{evt.Index}");
                    break;

                case CameraActionType.ExplosionHoldEnd:
                    // Non-destroyed loop boundary: ghost will be destroyed and respawned.
                    // Create a temporary camera anchor at the ghost's last position to bridge
                    // the gap between destroy and respawn — without this, FlightCamera detects
                    // a null target and snaps back to the active vessel.
                    watchedOverlapCycleIndex = -1;
                    overlapRetargetAfterUT = -1;
                    if (overlapCameraAnchor != null) Destroy(overlapCameraAnchor);
                    overlapCameraAnchor = new UnityEngine.GameObject("ParsekLoopCameraBridge");
                    overlapCameraAnchor.transform.position = evt.AnchorPosition;
                    if (FlightCamera.fetch != null)
                        FlightCamera.fetch.SetTargetTransform(overlapCameraAnchor.transform);
                    ParsekLog.Info("CameraFollow",
                        $"Loop: camera bridged at cycle boundary for #{evt.Index}");
                    break;

                case CameraActionType.RetargetToNewGhost:
                    if (watchedOverlapCycleIndex == -1 && evt.GhostPivot != null && FlightCamera.fetch != null)
                    {
                        FlightCamera.fetch.SetTargetTransform(evt.GhostPivot);
                        watchedOverlapCycleIndex = evt.NewCycleIndex;
                        // Clean up the bridge anchor now that camera is on the new ghost
                        if (overlapCameraAnchor != null) { Destroy(overlapCameraAnchor); overlapCameraAnchor = null; }
                        ParsekLog.Info("CameraFollow",
                            $"Loop: camera retargeted to ghost #{evt.Index} cycle={evt.NewCycleIndex}");
                    }
                    break;
            }
        }

        private void HandleOverlapCameraAction(CameraActionEvent evt)
        {
            if (watchedRecordingIndex != evt.Index) return;

            switch (evt.Action)
            {
                case CameraActionType.RetargetToNewGhost:
                    if (watchedOverlapCycleIndex == -1 && evt.GhostPivot != null && FlightCamera.fetch != null)
                    {
                        FlightCamera.fetch.SetTargetTransform(evt.GhostPivot);
                        watchedOverlapCycleIndex = evt.NewCycleIndex;
                        if (overlapCameraAnchor != null) { Destroy(overlapCameraAnchor); overlapCameraAnchor = null; }
                        ParsekLog.Info("CameraFollow",
                            $"Overlap: camera retargeted to ghost #{evt.Index} cycle={evt.NewCycleIndex}");
                    }
                    break;

                case CameraActionType.ExplosionHoldStart:
                    overlapRetargetAfterUT = evt.HoldUntilUT;
                    watchedOverlapCycleIndex = -2;
                    if (overlapCameraAnchor != null) Destroy(overlapCameraAnchor);
                    overlapCameraAnchor = new UnityEngine.GameObject("ParsekOverlapCameraAnchor");
                    overlapCameraAnchor.transform.position = evt.AnchorPosition;
                    if (FlightCamera.fetch != null)
                        FlightCamera.fetch.SetTargetTransform(overlapCameraAnchor.transform);
                    ParsekLog.Info("CameraFollow",
                        $"Overlap: camera holding at explosion for #{evt.Index} cycle={evt.NewCycleIndex}");
                    break;
            }
        }

        // Lazy-initialized styles for the watch mode overlay
        private GUIStyle watchOverlayStyle;
        private GUIStyle watchOverlayHintStyle;

        /// <summary>
        /// Returns true if recording at index has an active ghost (exists and not null).
        /// </summary>
        internal bool HasActiveGhost(int index)
        {
            GhostPlaybackState s;
            return ghostStates.TryGetValue(index, out s) && s != null && s.ghost != null;
        }

        /// <summary>
        /// Returns true if the ghost at index is within the camera cutoff distance.
        /// </summary>
        internal bool IsGhostWithinVisualRange(int index)
        {
            GhostPlaybackState s;
            if (!ghostStates.TryGetValue(index, out s) || s == null) return false;
            float cutoffKm = ParsekSettings.Current?.ghostCameraCutoffKm ?? 300f;
            return GhostPlaybackLogic.IsWithinWatchRange(s.currentZone, s.lastDistance, cutoffKm);
        }

        /// <summary>
        /// Returns true if the ghost at index is on the same celestial body as the active vessel.
        /// </summary>
        internal bool IsGhostOnSameBody(int index)
        {
            return engine.IsGhostOnBody(index, FlightGlobals.ActiveVessel?.mainBody?.name);
        }

        /// <summary>
        /// Draws the on-screen overlay when in watch mode: vessel name and return hint.
        /// Called from OnGUI when watchedRecordingIndex >= 0.
        /// </summary>
        void DrawWatchModeOverlay()
        {
            if (watchOverlayStyle == null)
            {
                watchOverlayStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 16,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.UpperCenter,
                    normal = { textColor = Color.white }
                };
                watchOverlayHintStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    alignment = TextAnchor.UpperCenter,
                    normal = { textColor = new Color(1f, 1f, 1f, 0.7f) }
                };
            }

            string vesselName = "";
            var committed = RecordingStore.CommittedRecordings;
            if (watchedRecordingIndex >= 0 && watchedRecordingIndex < committed.Count)
                vesselName = committed[watchedRecordingIndex].VesselName;

            // Compute distance from ghost to active vessel
            string distText = "";
            GhostPlaybackState watchState;
            if (ghostStates.TryGetValue(watchedRecordingIndex, out watchState)
                && watchState?.ghost != null && FlightGlobals.ActiveVessel != null)
            {
                double dist = Vector3d.Distance(
                    (Vector3d)watchState.ghost.transform.position,
                    (Vector3d)FlightGlobals.ActiveVessel.transform.position);
                if (dist < 1000)
                    distText = dist.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " m";
                else
                    distText = (dist / 1000).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " km";
            }

            float boxW = 300f, boxH = 50f;
            float x = (Screen.width * 0.5f - boxW) / 2f; // centered in the left half of the screen
            float y = 10f;
            Rect bgRect = new Rect(x, y, boxW, boxH);

            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(bgRect, Texture2D.whiteTexture);
            GUI.color = Color.white;

            string title = string.IsNullOrEmpty(distText)
                ? "Watching: " + vesselName
                : "Watching: " + vesselName + "  (" + distText + ")";
            GUI.Label(new Rect(x, y + 5, boxW, 22f), title, watchOverlayStyle);
            GUI.Label(new Rect(x, y + 27, boxW, 18f), "Press [ or ] to return to vessel", watchOverlayHintStyle);
        }

        /// <summary>
        /// <summary>
        /// Enter watch mode: point the camera at a ghost vessel.
        /// If already watching the same recording, toggles off (exits watch mode).
        /// If watching a different recording, switches to the new one (preserves camera state).
        /// </summary>
        internal void EnterWatchMode(int index)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (index < 0 || index >= committed.Count) return;

            // Toggle off: if already watching this recording, exit
            if (watchedRecordingIndex == index)
            {
                ExitWatchMode();
                return;
            }

            // Ghost must exist
            GhostPlaybackState gs;
            if (!ghostStates.TryGetValue(index, out gs) || gs == null || gs.ghost == null)
                return;

            // Ghost must be on the same body as the active vessel
            string ghostBody = gs.lastInterpolatedBodyName;
            string activeBody = FlightGlobals.ActiveVessel?.mainBody?.name;
            if (string.IsNullOrEmpty(ghostBody) || string.IsNullOrEmpty(activeBody) || ghostBody != activeBody)
                return;

            // Distance guard: KSP rendering breaks when camera is far from the active vessel
            // (FloatingOrigin, terrain, atmosphere, skybox are all anchored to active vessel).
            // Refuse watch if ghost is beyond the user's camera cutoff distance setting.
            float maxWatchKm = ParsekSettings.Current?.ghostCameraCutoffKm ?? 300f;
            if (FlightGlobals.ActiveVessel != null && gs.ghost != null)
            {
                float distKm = (float)(Vector3d.Distance(
                    gs.ghost.transform.position, FlightGlobals.ActiveVessel.transform.position) / 1000.0);
                if (distKm > maxWatchKm)
                {
                    ParsekLog.Info("CameraFollow",
                        $"EnterWatchMode refused: ghost #{index} \"{committed[index].VesselName}\" " +
                        $"is {distKm.ToString("F0", CultureInfo.InvariantCulture)}km from active vessel (max {maxWatchKm.ToString("F0", CultureInfo.InvariantCulture)}km)");
                    ScreenMessage($"Ghost too far to watch ({distKm:F0}km, max {maxWatchKm:F0}km)", 3f);
                    return;
                }
            }

            // Flight warning: if active vessel is in an unsafe state, show a brief screen message
            var av = FlightGlobals.ActiveVessel;
            if (av != null)
            {
                double pe = av.orbit != null ? av.orbit.PeA : 0;
                double atmoHeight = av.mainBody != null ? av.mainBody.atmosphereDepth : 0;
                if (!IsVesselSituationSafe(av.situation, pe, atmoHeight))
                {
                    ParsekLog.Verbose("CameraFollow", $"Showing flight warning \u2014 active vessel situation: {av.situation}");
                    ScreenMessage("Your vessel continues unattended", 3f);
                }
            }

            // If already watching a different recording, exit first (switch case — preserve camera state)
            bool switching = watchedRecordingIndex >= 0 && watchedRecordingIndex != index;
            if (switching)
            {
                ParsekLog.Info("CameraFollow", $"Switching watch from #{watchedRecordingIndex} to #{index} \"{committed[index].VesselName}\"");
                ExitWatchMode(skipCameraRestore: true);
            }

            watchedRecordingIndex = index;
            watchedRecordingId = committed[index].RecordingId;
            watchStartTime = Time.time;

            // If the ghost is currently beyond visual range and the recording loops,
            // reset the loop phase so the ghost starts from the beginning of the recording
            // (at the pad) instead of wherever it is mid-flight (e.g. near the Mun).
            var rec = committed[index];
            if (gs.currentZone == RenderingZone.Beyond && ShouldLoopPlayback(rec))
            {
                double currentUT = Planetarium.GetUniversalTime();
                double duration = rec.EndUT - rec.StartUT;
                double intervalSeconds = GetLoopIntervalSeconds(rec);
                double cycleDuration = duration + intervalSeconds;
                if (cycleDuration <= GhostPlaybackLogic.MinLoopDurationSeconds)
                    cycleDuration = duration;

                // Current elapsed time (with any existing offset)
                double elapsed = currentUT - rec.StartUT;
                double existingOffset;
                if (loopPhaseOffsets.TryGetValue(index, out existingOffset))
                    elapsed += existingOffset;

                // Compute where we are in the current cycle
                int curCycle = (int)Math.Floor(elapsed / cycleDuration);
                if (curCycle < 0) curCycle = 0;
                double cycleTime = elapsed - (curCycle * cycleDuration);

                // Shift elapsed so cycleTime becomes 0 (= recording start)
                double newOffset = (existingOffset) - cycleTime;
                loopPhaseOffsets[index] = newOffset;

                // Re-show the ghost so the camera has something to target
                gs.ghost.SetActive(true);
                gs.currentZone = RenderingZone.Physics;
                gs.playbackIndex = 0;

                // Position ghost at first trajectory point so camera targets the pad
                if (rec.Points != null && rec.Points.Count > 0)
                    PositionGhostAt(gs.ghost, rec.Points[0], rec.RecordingFormatVersion >= 5);

                ParsekLog.Info("CameraFollow",
                    string.Format(CultureInfo.InvariantCulture,
                        "Watch mode loop reset: ghost #{0} \"{1}\" cycleTime={2:F1}s -> offset={3:F1}s (ghost repositioned to recording start)",
                        index, rec.VesselName, cycleTime, newOffset));
            }

            // Save camera state only when entering fresh (not switching between ghosts)
            if (!switching)
            {
                savedCameraVessel = FlightGlobals.ActiveVessel;
                savedCameraDistance = FlightCamera.fetch.Distance;
                savedCameraPitch = FlightCamera.fetch.camPitch;
                savedCameraHeading = FlightCamera.fetch.camHdg;
                savedPivotSharpness = FlightCamera.fetch.pivotTranslateSharpness;
            }

            // Disable KSP's internal pivot tracking — we drive the camera manually
            FlightCamera.fetch.pivotTranslateSharpness = 0f;

            // Point camera at ghost (use cameraPivot — centroid of active parts)
            var watchTarget = gs.cameraPivot ?? gs.ghost.transform;
            FlightCamera.fetch.SetTargetTransform(watchTarget);
            FlightCamera.fetch.SetDistance(50f);  // override [75,400] entry clamp
            watchedOverlapCycleIndex = gs.loopCycleIndex; // track which cycle we're following
            ParsekLog.Info("CameraFollow",
                $"EnterWatchMode: ghost #{index} \"{committed[index].VesselName}\"" +
                $" target='{watchTarget.name}' pivotLocal=({watchTarget.localPosition.x:F2},{watchTarget.localPosition.y:F2},{watchTarget.localPosition.z:F2})" +
                $" ghostPos=({gs.ghost.transform.position.x:F1},{gs.ghost.transform.position.y:F1},{gs.ghost.transform.position.z:F1})" +
                $" camDist={FlightCamera.fetch.Distance.ToString("F1", CultureInfo.InvariantCulture)}");

            // Block inputs that could affect the active vessel
            InputLockManager.SetControlLock(WatchModeLockMask, WatchModeLockId);
            ParsekLog.Verbose("CameraFollow", $"InputLockManager control lock \"{WatchModeLockId}\" set: {WatchModeLockMask}");

            // Clear hold timer and safety counter
            watchEndHoldUntilRealTime = -1;
            watchNoTargetFrames = 0;

            string body = gs.lastInterpolatedBodyName ?? "?";
            string altStr = gs.lastInterpolatedAltitude.ToString("F0", CultureInfo.InvariantCulture);
            ParsekLog.Info("CameraFollow",
                $"Entering watch mode for recording #{index} \"{committed[index].VesselName}\" \u2014 ghost at alt {altStr}m on {body}");
        }

        /// <summary>
        /// Exit watch mode: return camera to the active vessel.
        /// When skipCameraRestore is true (switching between ghosts), the camera is not restored.
        /// </summary>
        internal void ExitWatchMode(bool skipCameraRestore = false)
        {
            if (watchedRecordingIndex < 0) return;
            if (FlightCamera.fetch != null)
                FlightCamera.fetch.pivotTranslateSharpness = savedPivotSharpness;

            // Restore camera to the active vessel (unless switching between ghosts)
            if (!skipCameraRestore)
            {
                var committed = RecordingStore.CommittedRecordings;
                string recVesselName = watchedRecordingIndex < committed.Count
                    ? committed[watchedRecordingIndex].VesselName : "?";
                string targetName = savedCameraVessel != null
                    ? savedCameraVessel.vesselName
                    : (FlightGlobals.ActiveVessel?.vesselName ?? "unknown");
                ParsekLog.Info("CameraFollow",
                    $"Exiting watch mode for recording #{watchedRecordingIndex} \"{recVesselName}\" \u2014 returning to {targetName}");

                if (FlightCamera.fetch != null)
                {
                    if (savedCameraVessel != null && savedCameraVessel.gameObject != null)
                    {
                        FlightCamera.fetch.SetTargetVessel(savedCameraVessel);
                        FlightCamera.fetch.SetDistance(savedCameraDistance);
                        FlightCamera.fetch.camPitch = savedCameraPitch;
                        FlightCamera.fetch.camHdg = savedCameraHeading;
                        ParsekLog.Verbose("CameraFollow",
                            $"FlightCamera.SetTargetVessel restored to {savedCameraVessel.vesselName}, distance={savedCameraDistance.ToString("F1", CultureInfo.InvariantCulture)}");
                    }
                    else if (FlightGlobals.ActiveVessel != null)
                    {
                        FlightCamera.fetch.SetTargetVessel(FlightGlobals.ActiveVessel);
                        ParsekLog.Verbose("CameraFollow",
                            $"FlightCamera.SetTargetVessel restored to {FlightGlobals.ActiveVessel.vesselName}, distance={savedCameraDistance.ToString("F1", CultureInfo.InvariantCulture)}");
                    }
                }
            }

            // Remove input locks
            InputLockManager.RemoveControlLock(WatchModeLockId);
            ParsekLog.Verbose("CameraFollow", $"InputLockManager control lock \"{WatchModeLockId}\" removed");

            watchedRecordingIndex = -1;
            watchedRecordingId = null;
            watchedOverlapCycleIndex = -1;
            overlapRetargetAfterUT = -1;
            if (overlapCameraAnchor != null) { Destroy(overlapCameraAnchor); overlapCameraAnchor = null; }
            savedCameraVessel = null;
            savedCameraDistance = 0f;
            savedCameraPitch = 0f;
            savedCameraHeading = 0f;
            watchEndHoldUntilRealTime = -1;
            watchNoTargetFrames = 0;
        }

        /// <summary>
        /// Determines whether a vessel's flight situation is safe for unattended flight.
        /// Safe means the vessel will not crash or deorbit while the player watches a ghost.
        /// </summary>
        internal static bool IsVesselSituationSafe(Vessel.Situations situation, double periapsis, double atmosphereAltitude)
        {
            switch (situation)
            {
                case Vessel.Situations.LANDED:
                case Vessel.Situations.SPLASHED:
                case Vessel.Situations.PRELAUNCH:
                case Vessel.Situations.DOCKED:
                    return true;

                case Vessel.Situations.ORBITING:
                    return periapsis > atmosphereAltitude;

                case Vessel.Situations.FLYING:
                case Vessel.Situations.SUB_ORBITAL:
                case Vessel.Situations.ESCAPING:
                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Pure static helper: computes the new watch index after a recording is deleted.
        /// Returns (newIndex, newId) where newIndex=-1 means watch mode should exit.
        /// </summary>
        internal static (int newIndex, string newId) ComputeWatchIndexAfterDelete(
            int watchedIndex, string watchedId, int deletedIndex,
            List<Recording> recordings)
        {
            if (deletedIndex == watchedIndex)
                return (-1, null);

            int newIndex = watchedIndex;
            if (deletedIndex < watchedIndex)
                newIndex = watchedIndex - 1;

            // Verify by ID — the recording at the new index should match
            if (newIndex >= 0 && newIndex < recordings.Count &&
                recordings[newIndex].RecordingId == watchedId)
            {
                return (newIndex, watchedId);
            }

            // ID mismatch — scan for correct index
            for (int j = 0; j < recordings.Count; j++)
            {
                if (recordings[j].RecordingId == watchedId)
                    return (j, watchedId);
            }

            // Not found — exit watch mode
            return (-1, null);
        }

        /// <summary>
        /// Finds the next recording to auto-follow when the watched recording ends.
        /// Handles two cases:
        /// 1. Chain continuation: next segment with same ChainId, ChainBranch 0, ChainIndex + 1
        /// 2. Tree branching: child recording via ChildBranchPointId, preferring same VesselPersistentId
        /// Returns the committed-list index of the next recording, or -1 if none found.
        /// Only returns a target if its ghost is already active (spawned and playing).
        /// </summary>
        int FindNextWatchTarget(int currentIndex, Recording currentRec)
        {
            ParsekLog.VerboseRateLimited("Watch", "findNextWatch",
                $"FindNextWatchTarget: currentIndex={currentIndex}, chainId={currentRec.ChainId ?? "null"}, chainIndex={currentRec.ChainIndex}, treeId={currentRec.TreeId ?? "null"}, childBpId={currentRec.ChildBranchPointId ?? "null"}");

            int result = GhostPlaybackLogic.FindNextWatchTarget(
                currentRec,
                RecordingStore.CommittedRecordings,
                RecordingStore.CommittedTrees,
                HasActiveGhost);

            if (result >= 0)
                ParsekLog.Info("Watch", $"FindNextWatchTarget: found target at index {result}");

            return result;
        }

        /// <summary>
        /// Transfers watch mode from the current recording to the next segment.
        /// Preserves camera state (no restore to player vessel) since we're switching between ghosts.
        /// </summary>
        void TransferWatchToNextSegment(int nextIndex)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (nextIndex < 0 || nextIndex >= committed.Count) return;

            string oldName = watchedRecordingIndex >= 0 && watchedRecordingIndex < committed.Count
                ? committed[watchedRecordingIndex].VesselName : "?";
            string newName = committed[nextIndex].VesselName;

            ParsekLog.Info("CameraFollow",
                $"Auto-following: #{watchedRecordingIndex} \"{oldName}\" -> #{nextIndex} \"{newName}\"");

            // Verify the target ghost exists before transferring
            GhostPlaybackState gs;
            if (!ghostStates.TryGetValue(nextIndex, out gs) || gs == null || gs.ghost == null)
            {
                ParsekLog.Warn("CameraFollow",
                    $"Auto-follow target #{nextIndex} has no active ghost — staying on current");
                return;
            }

            // Preserve original camera state across the transition
            // (ExitWatchMode clears these, but we want Backspace to restore to the original vessel)
            Vessel preservedVessel = savedCameraVessel;
            float preservedDistance = savedCameraDistance;
            float preservedPitch = savedCameraPitch;
            float preservedHeading = savedCameraHeading;

            // Switch watch mode: exit old (preserving camera position), enter new
            ExitWatchMode(skipCameraRestore: true);

            // Set up new watch state
            watchedRecordingIndex = nextIndex;
            watchedRecordingId = committed[nextIndex].RecordingId;

            // Restore saved camera state so Backspace returns to original vessel
            savedCameraVessel = preservedVessel;
            savedCameraDistance = preservedDistance;
            savedCameraPitch = preservedPitch;
            savedCameraHeading = preservedHeading;

            var segTarget = gs.cameraPivot ?? gs.ghost.transform;
            FlightCamera.fetch.SetTargetTransform(segTarget);
            InputLockManager.SetControlLock(WatchModeLockMask, WatchModeLockId);
            ParsekLog.Verbose("CameraFollow",
                $"InputLockManager control lock \"{WatchModeLockId}\" re-set after transfer");

            watchEndHoldUntilRealTime = -1;

            // Reset watch start time so the zone-exemption logging starts fresh
            // for the new segment (no stale elapsed time from the previous segment)
            watchStartTime = Time.time;

            ParsekLog.Info("CameraFollow",
                $"TransferWatch re-target: ghost #{nextIndex} \"{newName}\"" +
                $" target='{segTarget.name}' pivotLocal=({segTarget.localPosition.x:F2},{segTarget.localPosition.y:F2},{segTarget.localPosition.z:F2})" +
                $" ghostPos=({gs.ghost.transform.position.x:F1},{gs.ghost.transform.position.y:F1},{gs.ghost.transform.position.z:F1})" +
                $" camDist={FlightCamera.fetch.Distance:F1}" +
                $" watchStartTime={watchStartTime.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        #endregion

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
                    v.IgnoreGForces(240);
                    FlightGlobals.ForceSetActiveVessel(v);
                    Log($"Activated vessel pid={pid} (loaded={v.loaded})");
                    yield break;
                }
            }
            Log($"WARNING: Could not activate vessel pid={pid} within 5 seconds");
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
            double ghostDistance, int protectedIndex)
        {
            var zone = RenderingZoneManager.ClassifyDistance(ghostDistance);
            bool isWatchedGhost = protectedIndex == recIdx;

            // Cache distance on state for use by IsGhostWithinVisualRange
            if (state != null)
                state.lastDistance = ghostDistance;

            // Detect zone transition
            if (state != null)
            {
                string transDesc;
                if (GhostPlaybackLogic.DetectZoneTransition(state.currentZone, zone, out transDesc))
                {
                    RenderingZoneManager.LogZoneTransition(
                        $"#{recIdx} \"{rec.VesselName}\"", state.currentZone, zone, ghostDistance);
                    state.currentZone = zone;
                }
            }

            var (shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning) =
                GhostPlaybackLogic.GetZoneRenderingPolicy(zone);

            // Ghost camera cutoff: exit watch mode when the watched ghost exceeds the
            // user-configured cutoff distance, regardless of zone classification.
            if (isWatchedGhost)
            {
                double cutoffMeters = (ParsekSettings.Current?.ghostCameraCutoffKm ?? 300f) * 1000.0;
                if (ghostDistance >= cutoffMeters)
                {
                    ParsekLog.Info("Zone",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" exceeded ghost camera cutoff " +
                        $"({ghostDistance.ToString("F0", CultureInfo.InvariantCulture)}m > " +
                        $"{cutoffMeters.ToString("F0", CultureInfo.InvariantCulture)}m) — exiting watch mode");
                    ExitWatchMode();
                    // Don't return — let zone rendering continue (ghost will be hidden if Beyond)
                }
                else if (shouldHideMesh)
                {
                    // Within cutoff but in Beyond zone — exempt from hide (camera is at ghost)
                    shouldHideMesh = false;
                    ParsekLog.VerboseRateLimited("Zone", $"watched-zone-exempt-{recIdx}",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" beyond visual range " +
                        $"({ghostDistance.ToString("F0", CultureInfo.InvariantCulture)}m) but watched — exempt from zone hide",
                        5.0);
                }
            }

            // #171: During warp, exempt orbital ghosts from zone hiding — they travel far
            // from the player and would complete playback while invisible.
            if (shouldHideMesh && GhostPlaybackLogic.ShouldExemptFromZoneHide(
                    TimeWarp.CurrentRate, rec.OrbitSegments != null && rec.OrbitSegments.Count > 0))
            {
                shouldHideMesh = false;
                ParsekLog.VerboseRateLimited("Zone", $"warp-zone-exempt-{recIdx}",
                    $"Ghost #{recIdx} \"{rec.VesselName}\" beyond visual range " +
                    $"({ghostDistance.ToString("F0", CultureInfo.InvariantCulture)}m) " +
                    $"but exempt during warp (orbital ghost)", 5.0);
            }

            if (shouldHideMesh)
            {
                if (state != null && state.ghost != null && state.ghost.activeSelf)
                {
                    state.ghost.SetActive(false);
                    ParsekLog.VerboseRateLimited("Zone", "zone-hide",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" hidden: beyond visual range " +
                        $"({ghostDistance.ToString("F0", CultureInfo.InvariantCulture)}m)");
                }
                return new ZoneRenderingResult { hiddenByZone = true, skipPartEvents = true };
            }

            // Zone 1 or 2: ensure ghost is visible
            if (state != null && state.ghost != null && !state.ghost.activeSelf)
            {
                state.ghost.SetActive(true);
                ParsekLog.VerboseRateLimited("Zone", "zone-show",
                    $"Ghost #{recIdx} \"{rec.VesselName}\" re-shown: entered visual range " +
                    $"({ghostDistance.ToString("F0", CultureInfo.InvariantCulture)}m)");
            }

            return new ZoneRenderingResult { hiddenByZone = false, skipPartEvents = shouldSkipPartEvents };
        }

        #endregion

        #region IGhostPositioner implementation

        void IGhostPositioner.InterpolateAndPosition(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut, bool suppressFx)
        {
            if (state?.ghost == null || traj?.Points == null) return;
            int playbackIdx = state.playbackIndex;
            bool srfRel = traj.RecordingFormatVersion >= 5;
            bool surfaceSkip = TrajectoryMath.IsSurfaceAtUT(traj.TrackSections, ut);
            InterpolationResult interpResult;
            InterpolateAndPosition(state.ghost, traj.Points, traj.OrbitSegments,
                ref playbackIdx, ut, index * 10000, out interpResult, srfRel, surfaceSkip);
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
                ut, anchorVesselId, out interpResult);
            state.SetInterpolated(interpResult);
            state.playbackIndex = playbackIdx;
        }

        void IGhostPositioner.PositionAtPoint(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, TrajectoryPoint point)
        {
            if (state?.ghost == null) return;
            bool srfRel = traj != null && traj.RecordingFormatVersion >= 5;
            PositionGhostAt(state.ghost, point, srfRel);
        }

        void IGhostPositioner.PositionAtSurface(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state)
        {
            if (state?.ghost == null || traj?.SurfacePos == null) return;
            PositionGhostAtSurface(state.ghost, traj.SurfacePos.Value);
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
            bool srfRel = traj.RecordingFormatVersion >= 5;
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
                index, index * 10000, srfRel, useAnchor, out interpResult);
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

        void InterpolateAndPosition(GameObject ghost, List<TrajectoryPoint> points, ref int cachedIndex, double targetUT, out InterpolationResult interpResult, bool surfaceRelativeRotation = false)
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
                PositionGhostAt(ghost, before, surfaceRelativeRotation);
                interpResult = new InterpolationResult(before.velocity, before.bodyName, before.altitude);
                return;
            }

            // Degenerate segment (t == 0 from zero-duration segment)
            if (t == 0f && before.ut == after.ut)
            {
                PositionGhostAt(ghost, before, surfaceRelativeRotation);
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
            if (!ghost.activeSelf) ghost.SetActive(true);

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
            ghost.transform.rotation = surfaceRelativeRotation
                ? bodyBefore.bodyTransform.rotation * interpolatedRot
                : CorrectForBodyRotation(bodyBefore, targetUT, interpolatedRot);

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
                surfaceRelativeRotation = surfaceRelativeRotation
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

        /// <summary>
        /// Legacy v4 fallback — returns storedRot unchanged.
        /// KSP's world frame co-rotates with the body surface, so world-space
        /// rotations are already valid at any UT. The old time-delta correction
        /// was incorrect and has been removed. All v4 recordings are auto-migrated
        /// to v5 at flight-scene load; this path only executes if migration failed.
        /// </summary>
        static Quaternion CorrectForBodyRotation(CelestialBody body, double pointUT, Quaternion storedRot)
        {
            return storedRot;
        }

        void PositionGhostAt(GameObject ghost, TrajectoryPoint point, bool surfaceRelativeRotation = false)
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
            ghost.transform.rotation = surfaceRelativeRotation
                ? body.bodyTransform.rotation * sanitized
                : CorrectForBodyRotation(body, point.ut, sanitized);

            // Register for LateUpdate re-positioning after FloatingOrigin shift
            ghostPosEntries.Add(new GhostPosEntry
            {
                ghost = ghost,
                mode = GhostPosMode.SinglePoint,
                bodyBefore = body,
                latBefore = point.latitude, lonBefore = point.longitude, altBefore = point.altitude,
                pointUT = point.ut,
                interpolatedRot = sanitized,
                surfaceRelativeRotation = surfaceRelativeRotation
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
        void PositionGhostFromOrbitOnly(GameObject ghost, Recording rec, double ut, int orbitCacheBase)
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
        void PositionGhostAtSurface(GameObject ghost, SurfacePosition surfPos)
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
            bool srfRel,
            bool useAnchor,
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
                        rec.LoopAnchorVesselId, out interpResult);
                    return;
                }
            }

            // Absolute positioning fallback
            InterpolateAndPosition(ghost, rec.Points, rec.OrbitSegments,
                ref playbackIdx, loopUT, ghostIdSalt, out interpResult,
                surfaceRelativeRotation: srfRel);
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
                PositionGhostRelativeAt(ghost, frames[0], anchorVesselId);
                interpResult = new InterpolationResult(frames[0].velocity, frames[0].bodyName, 0);
                return;
            }

            TrajectoryPoint before = frames[indexBefore];
            TrajectoryPoint after = frames[indexBefore + 1];

            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                PositionGhostRelativeAt(ghost, before, anchorVesselId);
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

            if (!ghost.activeSelf) ghost.SetActive(true);

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
        void PositionGhostRelativeAt(GameObject ghost, TrajectoryPoint point, uint anchorVesselId)
        {
            if (!ghost.activeSelf) ghost.SetActive(true);

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
            out InterpolationResult interpResult, bool surfaceRelativeRotation = false,
            bool skipOrbitSegments = false)
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
            InterpolateAndPosition(ghost, points, ref cachedIndex, targetUT, out interpResult, surfaceRelativeRotation);
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

        /// <summary>
        /// Commits or shows merge dialog for the pending recording, depending on the autoMerge setting.
        /// </summary>
        void CommitOrShowDialog(Recording pending)
        {
            if (ParsekScenario.IsAutoMerge)
            {
                string recId = pending.RecordingId;
                double startUT = pending.StartUT;
                double endUT = pending.EndUT;
                RecordingStore.CommitPending();
                LedgerOrchestrator.OnRecordingCommitted(recId, startUT, endUT);
                KerbalsModule.RecalculateAndApply();
                Log($"Auto-merged recording from {pending.VesselName} ({pending.Points.Count} points)");
            }
            else
            {
                Log($"Showing merge dialog for {pending.VesselName} ({pending.Points.Count} points)");
                MergeDialog.Show(pending);
            }
        }

        /// <summary>
        /// Deferred merge dialog for destroyed vessels in the split path.
        /// Waits one frame to let the destruction sequence complete. The Harmony
        /// patch on FlightResultsDialog.Display handles ordering with KSP's report.
        /// </summary>
        IEnumerator ShowDeferredSplitMergeDialog()
        {
            yield return null;

            if (!RecordingStore.HasPending)
            {
                Log("Deferred split merge dialog: pending consumed during wait — aborting");
                yield break;
            }

            Log($"Showing deferred split merge dialog for {RecordingStore.Pending.VesselName}");
            MergeDialog.Show(RecordingStore.Pending);
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

            // Sort by distance (closest first)
            nearbySpawnCandidates.Sort((a, b) => a.distance.CompareTo(b.distance));
            proximityCheckGeneration++;

            // Notify for new candidates
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
            if (watchedRecordingIndex >= 0)
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
                if (ffTargetIdx >= 0 && ffTargetIdx != watchedRecordingIndex)
                {
                    ParsekLog.Info("CameraFollow",
                        $"FF transfer: watch #{watchedRecordingIndex} → #{ffTargetIdx} \"{rec.VesselName}\"");
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
