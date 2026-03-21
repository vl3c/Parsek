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
    public class ParsekFlight : MonoBehaviour
    {
        internal const string MODID = "Parsek_NS";
        internal const string MODNAME = "Parsek";

        #region State

        // Recording (delegated to FlightRecorder)
        private FlightRecorder recorder;
        internal List<TrajectoryPoint> recording => recorder?.Recording ?? noRecording;
        private List<OrbitSegment> orbitSegments => recorder?.OrbitSegments ?? noOrbitSegments;
        private static readonly List<TrajectoryPoint> noRecording = new List<TrajectoryPoint>();
        private static readonly List<OrbitSegment> noOrbitSegments = new List<OrbitSegment>();

        // Manual playback state (preview of current recording)
        private bool isPlaying = false;
        private double playbackStartUT;
        private double recordingStartUT;
        private GameObject ghostObject;
        private List<Material> previewGhostMaterials = new List<Material>();
        private GhostPlaybackState previewGhostState;
        private Recording previewRecording;
        internal int lastPlaybackIndex = 0;

        private Dictionary<int, GhostPlaybackState> ghostStates = new Dictionary<int, GhostPlaybackState>();
        private readonly MaterialPropertyBlock reentryShellMpb = new MaterialPropertyBlock();
        private readonly List<GameObject> activeExplosions = new List<GameObject>();

        // Older cycle ghosts still alive due to negative interval overlap
        private Dictionary<int, List<GhostPlaybackState>> overlapGhosts = new Dictionary<int, List<GhostPlaybackState>>();
        private const int MaxOverlapGhostsPerRecording = 5;

        // Loop phase offsets: when Watch mode starts on a ghost that's currently beyond
        // visual range, we shift the loop phase so the ghost starts from the beginning
        // of its recording (at the pad). This avoids targeting a ghost 300km away.
        private Dictionary<int, double> loopPhaseOffsets = new Dictionary<int, double>();

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

        // Active chain being built (persists across segments)
        private string activeChainId;          // null if not building a chain
        private int activeChainNextIndex;      // next segment's ChainIndex
        private string activeChainPrevId;      // previous segment's RecordingId (for ParentRecordingId)
        private string activeChainCrewName;    // EVA crew name for current segment (null if vessel)

        // Pending chain transition
        private bool pendingChainContinuation; // true when a segment ended and next should start
        private bool pendingChainIsBoarding;   // true = boarding (EVA→vessel), false = EVA exit
        private string pendingChainEvaName;    // kerbal name for EVA transitions

        // Boarding confirmation via onCrewBoardVessel
        private uint pendingBoardingTargetPid; // vessel PID from onCrewBoardVessel, 0 = none
        private int boardingConfirmFrames;     // frames since boarding event (auto-clear after 3)

        // Boundary anchor for chain continuation (copied from previous segment's last point)
        private TrajectoryPoint? pendingBoundaryAnchor;

        // Pending dock/undock transitions (set by event handlers, consumed by Update)
        private uint pendingDockMergedPid;          // merged vessel pid, 0 = no pending dock
        private bool pendingDockAsTarget;           // true if our vessel was the dock target (no pid change)
        private int dockConfirmFrames;              // frame counter for confirmation window (auto-clear after 5)
        private uint pendingUndockOtherPid;         // pid of vessel that split off, 0 = no pending undock
        private int undockConfirmFrames;            // frame counter for confirmation window

        // Undock continuation (ghost-only recording for the other vessel)
        private uint undockContinuationPid;         // 0 = not tracking
        private int undockContinuationRecIdx = -1;
        private Vector3 undockContinuationLastVel;
        private double undockContinuationLastUT = -1;

        // Continuation sampling: after a vessel chain segment commits (V→EVA),
        // keeps tracking the original vessel so its trajectory extends beyond the EVA point.
        private uint continuationVesselPid;        // 0 = not tracking
        private int continuationRecordingIdx = -1; // index into CommittedRecordings
        private Vector3 continuationLastVelocity;
        private double continuationLastUT = -1;

        // Continuation adaptive sampling thresholds (read from settings, same as FlightRecorder)
        private static float continuationMaxInterval =>
            ParsekSettings.Current?.maxSampleInterval ?? 3.0f;
        private static float continuationVelDirThreshold =>
            ParsekSettings.Current?.velocityDirThreshold ?? 2.0f;
        private static float continuationSpeedThreshold =>
            (ParsekSettings.Current?.speedChangeThreshold ?? 5.0f) / 100f;

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
        private HashSet<int> loggedGhostEnter = new HashSet<int>();
        private HashSet<int> loggedOrbitSegments = new HashSet<int>();
        private HashSet<int> loggedOrbitRotationSegments = new HashSet<int>();
        private HashSet<int> loggedReshow = new HashSet<int>();

        // Anchor vessel tracking: which anchor vessels are currently loaded (for looped ghost lifecycle)
        private readonly HashSet<uint> loadedAnchorVessels = new HashSet<uint>();

        // Phase 6b: Ghost chain evaluation + vessel ghosting
        private VesselGhoster vesselGhoster;
        private Dictionary<uint, GhostChain> activeGhostChains;

        // Deferred spawn queue: recording IDs queued during warp, flushed when warp ends
        private HashSet<string> pendingSpawnRecordingIds = new HashSet<string>();
        private string pendingWatchRecordingId = null;  // recording ID that was in watch mode when deferred
        private bool timelineResourceReplayPausedLogged = false;
        private const double DefaultLoopIntervalSeconds = 10.0;
        private const double MinLoopDurationSeconds = 1.0;
        // MinCycleDuration is now in GhostPlaybackLogic
        private const double OverlapExplosionHoldSeconds = 3.0;

        // Soft cap evaluation — cached lists to avoid per-frame allocation
        private readonly List<(int recordingIndex, GhostPriority priority)> cachedZone1Ghosts =
            new List<(int, GhostPriority)>();
        private readonly List<(int recordingIndex, GhostPriority priority)> cachedZone2Ghosts =
            new List<(int, GhostPriority)>();
        private bool softCapTriggeredThisFrame; // rate-limit logging
        // Ghosts despawned by soft cap are suppressed until ghost count drops below threshold
        // to prevent spawn-despawn-spawn loops every frame
        private readonly HashSet<int> softCapSuppressed = new HashSet<int>();

        // Camera follow (watch mode) — transient, never serialized
        private const string WatchModeLockId = "ParsekWatch";
        private const ControlTypes WatchModeLockMask =
            ControlTypes.STAGING | ControlTypes.THROTTLE |
            ControlTypes.VESSEL_SWITCHING | ControlTypes.EVA_INPUT |
            ControlTypes.CAMERAMODES;
        int watchedRecordingIndex = -1;       // -1 = not watching
        string watchedRecordingId = null;     // stable across index shifts
        float watchStartTime;                 // Time.time when watch mode was entered
        int watchedOverlapCycleIndex = -1;    // which overlap cycle the camera is following (-1 = ready for next, -2 = holding after explosion)
        double overlapRetargetAfterUT = -1;   // delay re-target after watched cycle explodes
        GameObject overlapCameraAnchor;       // temp anchor so FlightCamera doesn't reference destroyed ghost
        Vessel savedCameraVessel = null;
        float savedCameraDistance = 0f;
        float savedCameraPitch = 0f;
        float savedCameraHeading = 0f;
        double watchEndHoldUntilUT = -1;      // non-looped end hold timer
        float savedPivotSharpness = 0.5f;
        int watchNoTargetFrames = 0;          // consecutive frames with no valid camera target (safety net)

        // UI
        private Rect windowRect = new Rect(20, 100, 250, 250);
        private bool showUI = false;
        private static ToolbarControl toolbarControl;
        private ParsekUI ui;

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
                var result = new Dictionary<int, GameObject>();
                foreach (var kv in ghostStates)
                    result[kv.Key] = kv.Value.ghost;
                return result;
            }
        }
        public bool HasActiveChain => activeChainId != null;
        public bool HasActiveTree => activeTree != null;

        // Camera follow (watch mode)
        internal bool IsWatchingGhost => watchedRecordingIndex >= 0;
        internal int WatchedRecordingIndex => watchedRecordingIndex;

        #endregion

        #region Unity Lifecycle

        void Start()
        {
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
            GameEvents.afterFlagPlanted.Add(OnAfterFlagPlanted);

            ui = new ParsekUI(this);

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
            ClearStaleConfirmations();

            HandleTreeDockMerge();
            HandleDockUndockCommitRestart();

            // Chain: auto-commit previous segment when recording stopped (EVA exit)
            // VesselSnapshot nulled in CommitChainSegment — mid-chain segments are ghost-only.
            if (pendingChainContinuation && !pendingChainIsBoarding &&
                recorder != null && !recorder.IsRecording && recorder.CaptureAtStop != null)
            {
                CommitChainSegment(recorder, pendingChainEvaName);
                recorder = null;
                // pendingAutoRecord is still true — will start child EVA recording next
            }

            HandleTreeBoardMerge();
            HandleChainBoardingTransition();

            // Bug #51: vessel-switch auto-stop loses chain ID.
            // HandleVesselSwitchDuringRecording (in FlightRecorder) builds CaptureAtStop
            // but cannot access ParsekFlight's chain fields. Commit the segment as a
            // proper chain member and terminate the chain (vessel was switched away).
            HandleVesselSwitchChainTermination();

            HandleAtmosphereBoundarySplit();
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
                // Still run playback, input, and continuation sampling
                UpdateContinuationSampling();
                UpdateUndockContinuationSampling();
                HandleInput();
                if (isPlaying) UpdatePlayback();
                UpdateTimelinePlayback();
                return;
            }

            HandleDeferredAutoRecordEva();

            UpdateContinuationSampling();
            UpdateUndockContinuationSampling();

            HandleInput();

            if (isPlaying)
            {
                UpdatePlayback();
            }

            UpdateTimelinePlayback();
        }

        /// <summary>
        /// Re-applies ghost positions after FloatingOrigin has shifted all registered
        /// objects. Without this, ghosts would be displaced by one frame each time
        /// the floating origin shifts, causing erratic visual behavior during flight.
        /// </summary>
        void LateUpdate()
        {
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
            ghostPosEntries.Clear();

            UpdateWatchCamera();
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
            }
        }

        void OnDestroy()
        {
            if (toolbarControl != null)
            {
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
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                backgroundRecorder = null;
            }

            // Clear tree destruction dialog guard
            treeDestructionDialogPending = false;

            // Clean up recording if active
            if (IsRecording)
            {
                recorder.ForceStop();
            }
            StopPlayback();
            ExitWatchMode();
            InputLockManager.RemoveControlLock(WatchModeLockId); // safety net
            DestroyAllTimelineGhosts();

            // Phase 6b: Clean up ghost chain state
            vesselGhoster?.CleanupAll();
            vesselGhoster = null;
            activeGhostChains = null;

            ui?.Cleanup();
        }

        #endregion

        #region Scene Change Handling

        void OnSceneChangeRequested(GameScenes scene)
        {
            Log($"Scene change requested: {scene}");

            // Exit watch mode on scene change
            if (watchedRecordingIndex >= 0)
                ParsekLog.Info("CameraFollow", "Watch mode cleared on scene change");
            ExitWatchMode();
            InputLockManager.RemoveControlLock(WatchModeLockId); // safety net

            // Finalize continuation sampling before anything else
            if (continuationVesselPid != 0)
            {
                RefreshContinuationSnapshot();
                StopContinuation("scene change");
            }
            if (undockContinuationPid != 0)
            {
                RefreshUndockContinuationSnapshot();
                StopUndockContinuation("scene change");
            }

            ClearSceneChangeTransientState();

            // Tree mode: finalize and commit tree on scene exit.
            // Background recorder must be finalized BEFORE the tree commit so
            // its data is flushed to the tree recordings.
            if (activeTree != null)
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

            // If we have recording data and are leaving flight, stash it
            if (recorder != null && recorder.Recording.Count > 0)
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
                    if (RecordingStore.HasPending && activeChainId != null)
                    {
                        RecordingStore.Pending.ChainId = activeChainId;
                        RecordingStore.Pending.ChainIndex = activeChainNextIndex;
                        RecordingStore.Pending.ParentRecordingId = activeChainPrevId;
                        RecordingStore.Pending.EvaCrewName = activeChainCrewName;
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
                        var v = FlightGlobals.ActiveVessel;
                        if (v != null && v.mainBody != null)
                        {
                            RecordingStore.Pending.SegmentBodyName = v.mainBody.name;
                            if (v.mainBody.atmosphere)
                                RecordingStore.Pending.SegmentPhase = v.altitude < v.mainBody.atmosphereDepth ? "atmo" : "exo";
                            else
                                RecordingStore.Pending.SegmentPhase = "space";
                            ParsekLog.Verbose("Flight", $"Final segment tagged (ForceStop): " +
                                $"{RecordingStore.Pending.SegmentBodyName} {RecordingStore.Pending.SegmentPhase}");
                        }
                    }
                }

                // Clear chain fields after both paths have consumed them
                activeChainId = null;
                activeChainNextIndex = 0;
                activeChainPrevId = null;
                activeChainCrewName = null;

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
                            RecordingTree.DetermineTerminalState((int)sit);
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

            // Stop manual playback
            StopPlayback();
            DestroyAllTimelineGhosts();
            GhostVisualBuilder.ClearCanopyCache();
            GhostVisualBuilder.ClearDeployableCache();
            GhostVisualBuilder.ClearFxPrefabCache();
            GhostVisualBuilder.ClearGearCache();
            GhostVisualBuilder.ClearLadderCache();
            GhostVisualBuilder.ClearCargoBayCache();
            GhostVisualBuilder.ClearAnimateHeatCache();
        }

        void OnVesselWillDestroy(Vessel v)
        {
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

            // Deferred destruction check for tree background vessels.
            // Captures vessel state now (Phase 1) and defers the actual destruction confirmation
            // by one frame (Phase 3) to distinguish real destruction from vessel unloading.
            if (activeTree != null && ShouldDeferDestructionCheck(v.persistentId, true,
                dockingInProgress, activeTree.BackgroundMap))
            {
                string recordingId = activeTree.BackgroundMap[v.persistentId];
                var pending = CaptureVesselStateForTerminal(v, recordingId);
                StartCoroutine(DeferredDestructionCheck(pending));
            }

            // Standalone mode: if the active recording vessel is destroyed, schedule a merge dialog.
            // In tree mode, the deferred destruction check handles this already.
            if (activeTree == null && recorder != null && recorder.IsRecording
                && recorder.VesselDestroyedDuringRecording && v == FlightGlobals.ActiveVessel)
            {
                StartCoroutine(ShowPostDestructionMergeDialog());
            }

            // Tree mode: if the active vessel is destroyed, check if all tree vessels are now dead
            if (activeTree != null && recorder != null
                && recorder.VesselDestroyedDuringRecording && v == FlightGlobals.ActiveVessel
                && !treeDestructionDialogPending)
            {
                treeDestructionDialogPending = true;
                ParsekLog.Info("Flight", "Active vessel destroyed in tree mode — scheduling tree destruction check");
                StartCoroutine(ShowPostDestructionTreeMergeDialog());
            }

            // If the continuation vessel is destroyed, mark the recording and stop tracking
            if (continuationVesselPid != 0 && v.persistentId == continuationVesselPid)
            {
                if (continuationRecordingIdx >= 0 &&
                    continuationRecordingIdx < RecordingStore.CommittedRecordings.Count)
                {
                    var rec = RecordingStore.CommittedRecordings[continuationRecordingIdx];
                    rec.VesselDestroyed = true;
                    rec.VesselSnapshot = null;
                    Log($"Continuation vessel destroyed (pid={continuationVesselPid})");
                }
                StopContinuation("vessel destroyed");
            }

            // If the undock continuation vessel is destroyed, stop tracking
            if (undockContinuationPid != 0 && v.persistentId == undockContinuationPid)
            {
                if (undockContinuationRecIdx >= 0 &&
                    undockContinuationRecIdx < RecordingStore.CommittedRecordings.Count)
                {
                    var rec = RecordingStore.CommittedRecordings[undockContinuationRecIdx];
                    rec.VesselDestroyed = true;
                    Log($"Undock continuation vessel destroyed (pid={undockContinuationPid})");
                }
                StopUndockContinuation("vessel destroyed");
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
                yield break;

            // Guard: only for standalone recordings (tree mode has its own handling)
            if (activeTree != null)
                yield break;

            if (recorder.Recording.Count == 0)
                yield break;

            // Guard: if OnSceneChangeRequested already stashed a pending, don't double-stash
            if (RecordingStore.HasPending)
                yield break;

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
                if (flightDuration < 10.0 && maxDist < 30.0)
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
                RecordingStore.CommitPendingTree();
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
            treeRec.TrackSections.AddRange(rec.TrackSections);

            // Sort part events chronologically (mixed event sources may produce non-chronological order)
            treeRec.PartEvents.Sort((a, b) => a.ut.CompareTo(b.ut));

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
            if (pendingBoundaryAnchor.HasValue)
            {
                recorder.BoundaryAnchor = pendingBoundaryAnchor;
                pendingBoundaryAnchor = null;
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
                               ?? activeVessel?.vesselName ?? "Unknown",
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
                    if (splitRecorder.CaptureAtStop != null)
                    {
                        parentRec.Points.AddRange(splitRecorder.CaptureAtStop.Points);
                        parentRec.OrbitSegments.AddRange(splitRecorder.CaptureAtStop.OrbitSegments);
                        parentRec.PartEvents.AddRange(splitRecorder.CaptureAtStop.PartEvents);
                        parentRec.TrackSections.AddRange(splitRecorder.CaptureAtStop.TrackSections);
                        parentRec.PartEvents.Sort((a, b) => a.ut.CompareTo(b.ut));
                    }
                    parentRec.ExplicitEndUT = branchUT;
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
            if (undockContinuationPid != 0)
                StopUndockContinuation("tree branch");
            if (continuationVesselPid != 0)
                StopContinuation("tree branch");

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
                if (stoppedRecorder.CaptureAtStop != null)
                {
                    activeParentRec.Points.AddRange(stoppedRecorder.CaptureAtStop.Points);
                    activeParentRec.OrbitSegments.AddRange(stoppedRecorder.CaptureAtStop.OrbitSegments);
                    activeParentRec.PartEvents.AddRange(stoppedRecorder.CaptureAtStop.PartEvents);
                    activeParentRec.TrackSections.AddRange(stoppedRecorder.CaptureAtStop.TrackSections);
                    activeParentRec.PartEvents.Sort((a, b) => a.ut.CompareTo(b.ut));
                }
                activeParentRec.ExplicitEndUT = mergeUT;
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
            string mergedVesselName = mergedVessel?.vesselName ?? "Merged Vessel";

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
                    if (activeChainId != null)
                    {
                        pending.ChainId = activeChainId;
                        pending.ChainIndex = activeChainNextIndex;
                        pending.ParentRecordingId = activeChainPrevId;
                        pending.EvaCrewName = activeChainCrewName;
                    }

                    // Set terminal state for destroyed vessels
                    if (captured.VesselDestroyed)
                    {
                        pending.TerminalStateValue = TerminalState.Destroyed;
                        ParsekLog.Verbose("Flight", "FallbackCommitSplitRecorder: set TerminalState=Destroyed");
                    }

                    // Tag segment phase if untagged
                    if (string.IsNullOrEmpty(pending.SegmentPhase))
                    {
                        var v = FlightGlobals.ActiveVessel;
                        if (v != null && v.mainBody != null)
                        {
                            pending.SegmentBodyName = v.mainBody.name;
                            if (v.mainBody.atmosphere)
                                pending.SegmentPhase = v.altitude < v.mainBody.atmosphereDepth ? "atmo" : "exo";
                            else
                                pending.SegmentPhase = "space";
                        }
                    }
                }

                // Destroyed vessels: discard pad failures, otherwise commit or defer to dialog.
                // Dialog is deferred via coroutine so it appears AFTER KSP's crash report.
                if (RecordingStore.Pending.VesselDestroyed)
                {
                    double dur = RecordingStore.Pending.EndUT - RecordingStore.Pending.StartUT;
                    double maxDist = RecordingStore.Pending.MaxDistanceFromLaunch;
                    if (dur < 10.0 && maxDist < 30.0)
                    {
                        ParsekLog.Info("Flight",
                            $"Vessel destroyed during split — pad failure ({dur:F1}s, {maxDist:F0}m), discarding");
                        RecordingStore.DiscardPending();
                        return;
                    }
                    if (ParsekScenario.IsAutoMerge)
                    {
                        RecordingStore.CommitPending();
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

                RecordingStore.CommitPending();
                string chainInfo = activeChainId != null
                    ? $" (chain={activeChainId}, idx={activeChainNextIndex})"
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
                }
                else if (currentActive != null && shipVessel != null &&
                         currentActive.persistentId == shipVessel.persistentId)
                {
                    // KSP hasn't switched yet — ship is still active
                    activeChild = shipVessel;
                    backgroundChild = evaVessel;
                    // Note: tree vessel switch handling will sort out promotion when KSP switches
                }
                else
                {
                    // Ambiguous — default to EVA kerbal as active (most common case)
                    activeChild = evaVessel;
                    backgroundChild = shipVessel;
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
        /// on the current recording segment. If no tree exists, logs the event
        /// for diagnostic purposes only (no tree to record to).
        /// </summary>
        void ProcessBreakupEvent(BranchPoint breakupBp)
        {
            if (activeTree == null)
            {
                ParsekLog.Info("Coalescer",
                    "ProcessBreakupEvent: no active tree — breakup event logged but not recorded. " +
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

            // TODO Phase 2: Create child recording segments for controlled children.
            // Currently, controlled children survive the breakup but have no recording
            // segments created for them. Full implementation requires the background
            // recording infrastructure for multiple vessels (Phase 2 multi-vessel sessions).
            var controlledChildren = crashCoalescer.LastEmittedControlledChildPids;
            if (controlledChildren != null && controlledChildren.Count > 0)
            {
                for (int i = 0; i < controlledChildren.Count; i++)
                {
                    ParsekLog.Warn("Coalescer",
                        $"BREAKUP controlled child pid={controlledChildren[i]} — child segment creation deferred to Phase 2");
                }
            }

            ParsekLog.Info("Coalescer",
                $"ProcessBreakupEvent: BREAKUP attached to tree={activeTree.Id}, " +
                $"parentRec={activeRecId}, bpId={breakupBp.Id}, " +
                $"cause={breakupBp.BreakupCause}, debris={breakupBp.DebrisCount}, " +
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
            if (IsRecording) return;
            if (data.host != FlightGlobals.ActiveVessel)
            {
                ParsekLog.VerboseRateLimited("Flight", "sit-change-other",
                    $"OnVesselSituationChange: ignoring non-active vessel ({data.from} → {data.to})");
                return;
            }
            if (data.from != Vessel.Situations.PRELAUNCH)
            {
                ParsekLog.VerboseRateLimited("Flight", "sit-change-not-prelaunch",
                    $"OnVesselSituationChange: not from PRELAUNCH ({data.from} → {data.to})");
                return;
            }
            if (ParsekSettings.Current?.autoRecordOnLaunch == false)
            {
                ParsekLog.Verbose("Flight", "OnVesselSituationChange: auto-record disabled in settings");
                return;
            }

            StartRecording();
            Log("Auto-record started (vessel left pad/runway)");
            ScreenMessage("Recording STARTED (auto)", 2f);
        }

        void OnCrewBoardVessel(GameEvents.FromToAction<Part, Part> data)
        {
            if (activeChainId == null && activeTree == null)
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

        /// <summary>
        /// Commits the current chain segment and advances chain state.
        /// Sets up boundary anchor for the next segment.
        /// Mid-chain segments have VesselSnapshot nulled (ghost-only) because the recording
        /// ends at EVA, not at the vessel's actual final position.
        /// </summary>
        void CommitChainSegment(FlightRecorder segmentRecorder, string evaCrewName)
        {
            string segmentId = segmentRecorder.CaptureAtStop.RecordingId;
            Log($"Chain: committing segment (id={segmentId}, chainIdx={activeChainNextIndex})");

            RecordingStore.StashPending(
                segmentRecorder.Recording,
                segmentRecorder.CaptureAtStop.VesselName,
                segmentRecorder.OrbitSegments,
                recordingId: segmentId,
                recordingFormatVersion: segmentRecorder.CaptureAtStop.RecordingFormatVersion,
                partEvents: segmentRecorder.PartEvents,
                flagEvents: segmentRecorder.FlagEvents);

            if (!RecordingStore.HasPending)
            {
                Log("Chain: segment too short to stash — aborting chain continuation");
                pendingChainContinuation = false;
                pendingChainEvaName = null;
                pendingAutoRecord = false;
                return;
            }

            RecordingStore.Pending.ApplyPersistenceArtifactsFrom(segmentRecorder.CaptureAtStop);
            Log($"Chain: segment has VesselSnapshot={RecordingStore.Pending.VesselSnapshot != null}, " +
                $"GhostVisualSnapshot={RecordingStore.Pending.GhostVisualSnapshot != null}");

            // First transition: initialize chain
            if (activeChainId == null)
            {
                activeChainId = System.Guid.NewGuid().ToString("N");
                activeChainNextIndex = 0;
                // Auto-group chain under a group named after the starting vessel
                string chainGroupName = RecordingStore.Pending.VesselName ?? "Chain";
                RecordingStore.Pending.RecordingGroups = new System.Collections.Generic.List<string> { chainGroupName };
                Log($"Chain: started new chain (id={activeChainId}, group='{chainGroupName}')");
            }

            // Tag segment with chain metadata
            RecordingStore.Pending.ChainId = activeChainId;
            RecordingStore.Pending.ChainIndex = activeChainNextIndex;
            RecordingStore.Pending.ParentRecordingId = activeChainPrevId;
            RecordingStore.Pending.EvaCrewName = evaCrewName;

            RecordingStore.CommitPending();
            ParsekScenario.ReserveSnapshotCrew();
            ParsekScenario.SwapReservedCrewInFlight();

            // Prepare boundary anchor from last point of committed segment
            if (segmentRecorder.Recording.Count > 0)
                pendingBoundaryAnchor = segmentRecorder.Recording[segmentRecorder.Recording.Count - 1];

            // Advance chain state
            activeChainNextIndex++;
            activeChainPrevId = segmentId;

            // Continuation sampling: track the vessel after mid-chain commit
            if (!segmentRecorder.RecordingStartedAsEva)
            {
                // Vessel segment committed (V→EVA): start continuation to extend trajectory
                continuationVesselPid = segmentRecorder.RecordingVesselId;
                continuationRecordingIdx = RecordingStore.CommittedRecordings.Count - 1;
                var lastPoints = RecordingStore.CommittedRecordings[continuationRecordingIdx].Points;
                if (lastPoints.Count > 0)
                {
                    continuationLastVelocity = lastPoints[lastPoints.Count - 1].velocity;
                    continuationLastUT = lastPoints[lastPoints.Count - 1].ut;
                }
                else
                {
                    continuationLastVelocity = Vector3.zero;
                    continuationLastUT = -1;
                }
                Log($"Continuation started: tracking vessel pid={continuationVesselPid} " +
                    $"in recording #{continuationRecordingIdx}");
            }
            else if (continuationVesselPid != 0)
            {
                // EVA segment committed during boarding (EVA→V): stop continuation,
                // null the vessel segment's VesselSnapshot (new vessel segment handles spawning)
                var vesselRec = RecordingStore.CommittedRecordings[continuationRecordingIdx];
                vesselRec.VesselSnapshot = null;
                Log($"Continuation stopped (boarding): nulled VesselSnapshot " +
                    $"on recording #{continuationRecordingIdx}");
                StopContinuation("boarding");
            }
        }

        /// <summary>
        /// Samples the continuation vessel's position each frame (adaptive sampling).
        /// Extends the committed recording's trajectory beyond the EVA point.
        /// </summary>
        void UpdateContinuationSampling()
        {
            if (continuationVesselPid == 0) return;

            // Guard against stale index (e.g. user wiped recordings from UI)
            if (continuationRecordingIdx < 0 ||
                continuationRecordingIdx >= RecordingStore.CommittedRecordings.Count)
            {
                StopContinuation("stale index");
                return;
            }

            Vessel v = FlightRecorder.FindVesselByPid(continuationVesselPid);
            if (v == null)
            {
                StopContinuation("vessel null");
                return;
            }

            double ut = Planetarium.GetUniversalTime();
            Vector3 velocity = v.packed
                ? (Vector3)v.obt_velocity
                : (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity());

            if (!TrajectoryMath.ShouldRecordPoint(velocity, continuationLastVelocity,
                ut, continuationLastUT, continuationMaxInterval,
                continuationVelDirThreshold, continuationSpeedThreshold))
                return;

            var rec = RecordingStore.CommittedRecordings[continuationRecordingIdx];

            // Carry forward resource values from the last point (vessel doesn't earn
            // resources while flying autonomously after EVA)
            var lastPoint = rec.Points.Count > 0
                ? rec.Points[rec.Points.Count - 1]
                : default(TrajectoryPoint);

            var point = new TrajectoryPoint
            {
                ut = ut,
                latitude = v.latitude,
                longitude = v.longitude,
                altitude = v.altitude,
                rotation = v.srfRelRotation,
                velocity = velocity,
                bodyName = v.mainBody.name,
                funds = lastPoint.funds,
                science = lastPoint.science,
                reputation = lastPoint.reputation
            };

            rec.Points.Add(point);
            continuationLastUT = ut;
            continuationLastVelocity = velocity;
        }

        void StopContinuation(string reason)
        {
            Log($"Continuation stopped ({reason}): was tracking pid={continuationVesselPid}, " +
                $"recording #{continuationRecordingIdx}");
            continuationVesselPid = 0;
            continuationRecordingIdx = -1;
        }

        /// <summary>
        /// Refreshes the continuation recording's VesselSnapshot before stopping.
        /// If the vessel is loaded, takes a fresh snapshot. If unloaded/null,
        /// updates the existing snapshot's position from the last trajectory point.
        /// </summary>
        void RefreshContinuationSnapshot()
        {
            if (continuationVesselPid == 0 || continuationRecordingIdx < 0) return;
            if (continuationRecordingIdx >= RecordingStore.CommittedRecordings.Count)
            {
                StopContinuation("stale index in snapshot refresh");
                return;
            }

            var rec = RecordingStore.CommittedRecordings[continuationRecordingIdx];
            Vessel v = FlightRecorder.FindVesselByPid(continuationVesselPid);

            if (v != null && v.loaded)
            {
                var snapshot = VesselSpawner.TryBackupSnapshot(v);
                if (snapshot != null)
                {
                    rec.VesselSnapshot = snapshot;
                    Log("Continuation: refreshed vessel snapshot from loaded vessel");
                }
            }
            else if (rec.VesselSnapshot != null && rec.Points.Count > 0)
            {
                var last = rec.Points[rec.Points.Count - 1];
                rec.VesselSnapshot.SetValue("lat",
                    last.latitude.ToString("R", CultureInfo.InvariantCulture), true);
                rec.VesselSnapshot.SetValue("lon",
                    last.longitude.ToString("R", CultureInfo.InvariantCulture), true);
                rec.VesselSnapshot.SetValue("alt",
                    last.altitude.ToString("R", CultureInfo.InvariantCulture), true);
                Log($"Continuation: updated snapshot position from last trajectory point " +
                    $"(lat={last.latitude:F4})");
            }
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
            if (data.from.vessel.situation != Vessel.Situations.PRELAUNCH)
            {
                ParsekLog.VerboseRateLimited("Flight", "eva-not-prelaunch",
                    $"OnCrewOnEva: vessel not on pad (sit={data.from.vessel.situation}) — ignoring");
                return;
            }
            if (ParsekSettings.Current?.autoRecordOnEva == false)
            {
                ParsekLog.Verbose("Flight", "OnCrewOnEva: auto-record on EVA disabled in settings");
                return;
            }

            // The EVA kerbal may not yet be the active vessel, defer to Update()
            pendingAutoRecord = true;
            Log("EVA from pad detected — pending auto-record");
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
            recorder?.OnVesselGoOnRails(v);
            backgroundRecorder?.OnBackgroundVesselGoOnRails(v);
        }

        void OnVesselGoOffRails(Vessel v)
        {
            recorder?.OnVesselGoOffRails(v);
            backgroundRecorder?.OnBackgroundVesselGoOffRails(v);
        }

        void OnVesselLoaded(Vessel v)
        {
            if (v == null) return;
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
                var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                    currentUT, rec.StartUT, rec.EndUT, interval);

                ParsekLog.Info("Loop",
                    $"Anchor vessel loaded: pid={pid}, rec #{i} '{rec.VesselName}' " +
                    $"phase cycle={cycleIndex} loopUT={loopUT:F2} paused={isInPause}");

                // The loop playback system in UpdateLoopingTimelinePlayback will pick up
                // this recording on the next Update frame and spawn the ghost at the
                // computed phase — no direct spawn here avoids duplicate spawning.
            }
        }

        void OnVesselUnloaded(Vessel v)
        {
            if (v == null) return;
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
                    DestroyTimelineGhost(i);
                }
            }
        }

        void OnVesselSOIChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> data)
        {
            recorder?.OnVesselSOIChanged(data);
            backgroundRecorder?.OnBackgroundVesselSOIChanged(data.host, data.from);
        }

        void OnTimeWarpRateChanged()
        {
            if (activeTree == null || backgroundRecorder == null) return;

            double ut = Planetarium.GetUniversalTime();
            float warpRate = TimeWarp.CurrentRate;

            ParsekLog.Info("Checkpoint",
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
            if (recorder == null || !recorder.IsRecording) return;
            if (flagSite == null || flagSite.vessel == null || flagSite.part == null) return;

            // Match: the planting kerbal must be on the recorded vessel (EVA kerbal recording)
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
                ut = Planetarium.GetUniversalTime(),
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
        /// Commits the current segment as a dock/undock chain boundary.
        /// Initializes the chain if needed, tags segment metadata, and advances chain state.
        /// </summary>
        void CommitDockUndockSegment(FlightRecorder segmentRecorder, PartEventType eventType, uint dockPortPid)
        {
            string segmentId = segmentRecorder.CaptureAtStop.RecordingId;
            Log($"Dock/Undock chain: committing segment (id={segmentId}, event={eventType})");

            RecordingStore.StashPending(
                segmentRecorder.Recording,
                segmentRecorder.CaptureAtStop.VesselName,
                segmentRecorder.OrbitSegments,
                recordingId: segmentId,
                recordingFormatVersion: segmentRecorder.CaptureAtStop.RecordingFormatVersion,
                partEvents: segmentRecorder.PartEvents,
                flagEvents: segmentRecorder.FlagEvents);

            if (!RecordingStore.HasPending)
            {
                Log("Dock/Undock chain: segment too short to stash — aborting");
                pendingDockMergedPid = 0;
                pendingDockAsTarget = false;
                pendingUndockOtherPid = 0;
                return;
            }

            RecordingStore.Pending.ApplyPersistenceArtifactsFrom(segmentRecorder.CaptureAtStop);

            // Add dock/undock part event to the segment
            if (segmentRecorder.Recording.Count > 0)
            {
                double lastUT = segmentRecorder.Recording[segmentRecorder.Recording.Count - 1].ut;
                RecordingStore.Pending.PartEvents.Add(new PartEvent
                {
                    ut = lastUT,
                    partPersistentId = dockPortPid,
                    eventType = eventType,
                    partName = eventType.ToString()
                });
            }

            // First transition: initialize chain
            if (activeChainId == null)
            {
                activeChainId = System.Guid.NewGuid().ToString("N");
                activeChainNextIndex = 0;
                string chainGroupName = RecordingStore.Pending.VesselName ?? "Chain";
                RecordingStore.Pending.RecordingGroups = new System.Collections.Generic.List<string> { chainGroupName };
                Log($"Dock/Undock chain: started new chain (id={activeChainId}, group='{chainGroupName}')");
            }

            // Tag segment with chain metadata
            RecordingStore.Pending.ChainId = activeChainId;
            RecordingStore.Pending.ChainIndex = activeChainNextIndex;
            RecordingStore.Pending.ChainBranch = 0; // always primary path
            RecordingStore.Pending.ParentRecordingId = activeChainPrevId;
            RecordingStore.Pending.EvaCrewName = null; // vessel segment, not EVA

            // Mid-chain segments are ghost-only (VesselSnapshot nulled)
            RecordingStore.Pending.VesselSnapshot = null;

            RecordingStore.CommitPending();
            ParsekScenario.ReserveSnapshotCrew();
            ParsekScenario.SwapReservedCrewInFlight();

            // Prepare boundary anchor from last point of committed segment
            if (segmentRecorder.Recording.Count > 0)
                pendingBoundaryAnchor = segmentRecorder.Recording[segmentRecorder.Recording.Count - 1];

            // Advance chain state
            activeChainNextIndex++;
            activeChainPrevId = segmentId;
        }

        /// <summary>
        /// Bug #51 fix: when vessel-switch auto-stop fires during an active chain,
        /// commit the segment as a proper chain member and terminate the chain.
        /// The vessel was switched away so no continuation recording can start.
        /// Modeled on CommitBoundarySplit but without starting a new segment.
        /// </summary>
        void HandleVesselSwitchChainTermination()
        {
            if (recorder == null || recorder.IsRecording || recorder.CaptureAtStop == null)
                return;
            if (activeChainId == null)
                return;
            if (pendingChainContinuation || recorder.ChainToVesselPending
                || recorder.DockMergePending || recorder.UndockSwitchPending)
                return;

            var captured = recorder.CaptureAtStop;
            string segmentId = captured.RecordingId;
            ParsekLog.Info("Flight", $"Vessel-switch chain termination: committing final segment " +
                $"(id={segmentId}, chain={activeChainId}, idx={activeChainNextIndex}, " +
                $"points={recorder.Recording.Count}, orbits={recorder.OrbitSegments.Count})");

            RecordingStore.StashPending(
                recorder.Recording,
                captured.VesselName ?? "Unknown",
                recorder.OrbitSegments,
                recordingId: segmentId,
                recordingFormatVersion: (int?)captured.RecordingFormatVersion,
                partEvents: recorder.PartEvents,
                flagEvents: recorder.FlagEvents);

            if (!RecordingStore.HasPending)
            {
                ParsekLog.Warn("Flight", "Vessel-switch chain termination: segment too short to stash — " +
                    $"aborting (points={recorder.Recording.Count})");
                // Still clear chain state to avoid blocking CommitFlight
                activeChainId = null;
                activeChainNextIndex = 0;
                activeChainPrevId = null;
                activeChainCrewName = null;
                recorder = null;
                return;
            }

            RecordingStore.Pending.ApplyPersistenceArtifactsFrom(captured);

            // Tag chain metadata
            RecordingStore.Pending.ChainId = activeChainId;
            RecordingStore.Pending.ChainIndex = activeChainNextIndex;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.Pending.ParentRecordingId = activeChainPrevId;
            RecordingStore.Pending.EvaCrewName = activeChainCrewName;
            RecordingStore.Pending.VesselPersistentId = recorder.RecordingVesselId;

            // Derive segment phase/body from the recorded vessel (not ActiveVessel which changed)
            Vessel recordedVessel = FlightRecorder.FindVesselByPid(recorder.RecordingVesselId);
            if (recordedVessel != null && recordedVessel.mainBody != null)
            {
                RecordingStore.Pending.SegmentBodyName = recordedVessel.mainBody.name;
                if (recordedVessel.mainBody.atmosphere)
                    RecordingStore.Pending.SegmentPhase = recordedVessel.altitude < recordedVessel.mainBody.atmosphereDepth ? "atmo" : "exo";
                else
                    RecordingStore.Pending.SegmentPhase = "space";

                // Terminal state from vessel situation
                RecordingStore.Pending.SceneExitSituation = (int)recordedVessel.situation;
                RecordingStore.Pending.TerminalStateValue =
                    RecordingTree.DetermineTerminalState((int)recordedVessel.situation);
            }

            // Final chain segment keeps VesselSnapshot for spawning (not ghost-only)

            RecordingStore.CommitPending();
            ParsekScenario.ReserveSnapshotCrew();
            ParsekScenario.SwapReservedCrewInFlight();

            // Clean up continuation sampling if active
            if (continuationVesselPid != 0)
            {
                RefreshContinuationSnapshot();
                StopContinuation("vessel-switch chain termination");
            }

            // Terminate chain — no continuation possible after vessel switch
            ParsekLog.Info("Flight", $"Chain terminated by vessel switch: chain={activeChainId}, " +
                $"finalIdx={activeChainNextIndex}, segment={segmentId}, " +
                $"totalRecordings={RecordingStore.CommittedRecordings.Count}");
            activeChainId = null;
            activeChainNextIndex = 0;
            activeChainPrevId = null;
            activeChainCrewName = null;
            recorder = null;
        }

        /// <summary>
        /// Commits the current segment as an atmosphere boundary chain split.
        /// Follows the same pattern as CommitDockUndockSegment.
        /// </summary>
        void CommitBoundarySplit(string completedPhase, string bodyName)
        {
            var captured = recorder.CaptureAtStop;
            string segmentId = captured != null ? captured.RecordingId : null;
            ParsekLog.Info("Flight", $"Boundary split: committing segment " +
                $"(id={segmentId}, phase={completedPhase}, body={bodyName}, " +
                $"points={recorder.Recording.Count}, orbits={recorder.OrbitSegments.Count})");

            RecordingStore.StashPending(
                recorder.Recording,
                captured != null ? captured.VesselName : (FlightGlobals.ActiveVessel?.vesselName ?? "Unknown"),
                recorder.OrbitSegments,
                recordingId: segmentId,
                recordingFormatVersion: captured != null ? (int?)captured.RecordingFormatVersion : null,
                partEvents: recorder.PartEvents,
                flagEvents: recorder.FlagEvents);

            if (!RecordingStore.HasPending)
            {
                ParsekLog.Warn("Flight", "Boundary split: segment too short to stash — aborting " +
                    $"(points={recorder.Recording.Count})");
                return;
            }

            if (captured != null)
                RecordingStore.Pending.ApplyPersistenceArtifactsFrom(captured);

            // Tag segment with phase and body
            RecordingStore.Pending.SegmentPhase = completedPhase;
            RecordingStore.Pending.SegmentBodyName = bodyName;

            // First transition: initialize chain
            if (activeChainId == null)
            {
                activeChainId = System.Guid.NewGuid().ToString("N");
                activeChainNextIndex = 0;
                string chainGroupName = RecordingStore.Pending.VesselName ?? "Chain";
                RecordingStore.Pending.RecordingGroups = new System.Collections.Generic.List<string> { chainGroupName };
                ParsekLog.Info("Flight", $"Boundary split: started new chain (id={activeChainId}, group='{chainGroupName}')");
            }

            // Tag segment with chain metadata
            RecordingStore.Pending.ChainId = activeChainId;
            RecordingStore.Pending.ChainIndex = activeChainNextIndex;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.Pending.ParentRecordingId = activeChainPrevId;
            RecordingStore.Pending.EvaCrewName = activeChainCrewName;

            // Mid-chain segments are ghost-only
            RecordingStore.Pending.VesselSnapshot = null;

            RecordingStore.CommitPending();
            ParsekScenario.ReserveSnapshotCrew();
            ParsekScenario.SwapReservedCrewInFlight();

            // Prepare boundary anchor from last point of committed segment
            if (recorder.Recording.Count > 0)
                pendingBoundaryAnchor = recorder.Recording[recorder.Recording.Count - 1];

            // Advance chain state
            activeChainNextIndex++;
            activeChainPrevId = segmentId;
            ParsekLog.Verbose("Flight", $"Boundary split committed: chain={activeChainId}, " +
                $"nextIdx={activeChainNextIndex}, segment={bodyName} {completedPhase}, " +
                $"totalRecordings={RecordingStore.CommittedRecordings.Count}");
        }

        /// <summary>
        /// Starts ghost-only continuation recording for the "other" vessel after undock.
        /// This vessel gets ChainBranch = 1 so it plays back as a ghost but never spawns.
        /// </summary>
        void StartUndockContinuation(uint otherPid)
        {
            // Stop any existing continuation first
            if (undockContinuationPid != 0)
                StopUndockContinuation("replaced by new undock");

            Vessel otherVessel = FlightRecorder.FindVesselByPid(otherPid);
            if (otherVessel == null)
            {
                Log($"Undock continuation: cannot find vessel pid={otherPid} — skipping");
                return;
            }

            // Take snapshot for ghost visuals
            ConfigNode ghostSnapshot = VesselSpawner.TryBackupSnapshot(otherVessel);

            // Create a committed recording for the continuation
            double ut = Planetarium.GetUniversalTime();
            Vector3 velocity = otherVessel.packed
                ? (Vector3)otherVessel.obt_velocity
                : (Vector3)(otherVessel.rb_velocityD + Krakensbane.GetFrameVelocity());

            var seedPoint = new TrajectoryPoint
            {
                ut = ut,
                latitude = otherVessel.latitude,
                longitude = otherVessel.longitude,
                altitude = otherVessel.altitude,
                rotation = otherVessel.srfRelRotation,
                velocity = velocity,
                bodyName = otherVessel.mainBody.name
            };

            var contRec = new Recording
            {
                VesselName = otherVessel.vesselName + " (undock continuation)",
                ChainId = activeChainId,
                ChainIndex = activeChainNextIndex - 1, // same index as the player's new segment
                ChainBranch = 1, // parallel branch — ghost-only, never spawns
                GhostVisualSnapshot = ghostSnapshot,
                RecordingId = System.Guid.NewGuid().ToString("N"),
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
            };
            contRec.Points.Add(seedPoint);

            RecordingStore.CommittedRecordings.Add(contRec);
            undockContinuationRecIdx = RecordingStore.CommittedRecordings.Count - 1;
            undockContinuationPid = otherPid;
            undockContinuationLastVel = velocity;
            undockContinuationLastUT = ut;

            Log($"Undock continuation started: tracking vessel pid={otherPid} " +
                $"in recording #{undockContinuationRecIdx} (chain={activeChainId}, branch=1)");
        }

        /// <summary>
        /// Samples the undock continuation vessel's position each frame (adaptive sampling).
        /// Mirrors UpdateContinuationSampling but for the undocked sibling vessel.
        /// </summary>
        void UpdateUndockContinuationSampling()
        {
            if (undockContinuationPid == 0) return;

            // Guard against stale index
            if (undockContinuationRecIdx < 0 ||
                undockContinuationRecIdx >= RecordingStore.CommittedRecordings.Count)
            {
                StopUndockContinuation("stale index");
                return;
            }

            Vessel v = FlightRecorder.FindVesselByPid(undockContinuationPid);
            if (v == null)
            {
                StopUndockContinuation("vessel null");
                return;
            }

            double ut = Planetarium.GetUniversalTime();
            Vector3 velocity = v.packed
                ? (Vector3)v.obt_velocity
                : (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity());

            if (!TrajectoryMath.ShouldRecordPoint(velocity, undockContinuationLastVel,
                ut, undockContinuationLastUT, continuationMaxInterval,
                continuationVelDirThreshold, continuationSpeedThreshold))
                return;

            var rec = RecordingStore.CommittedRecordings[undockContinuationRecIdx];

            // Carry forward resource values from the last point
            var lastPoint = rec.Points.Count > 0
                ? rec.Points[rec.Points.Count - 1]
                : default(TrajectoryPoint);

            var point = new TrajectoryPoint
            {
                ut = ut,
                latitude = v.latitude,
                longitude = v.longitude,
                altitude = v.altitude,
                rotation = v.srfRelRotation,
                velocity = velocity,
                bodyName = v.mainBody.name,
                funds = lastPoint.funds,
                science = lastPoint.science,
                reputation = lastPoint.reputation
            };

            rec.Points.Add(point);
            undockContinuationLastUT = ut;
            undockContinuationLastVel = velocity;
        }

        void StopUndockContinuation(string reason)
        {
            Log($"Undock continuation stopped ({reason}): was tracking pid={undockContinuationPid}, " +
                $"recording #{undockContinuationRecIdx}");
            undockContinuationPid = 0;
            undockContinuationRecIdx = -1;
            undockContinuationLastUT = -1;
        }

        /// <summary>
        /// Refreshes the undock continuation recording's ghost snapshot before stopping.
        /// </summary>
        void RefreshUndockContinuationSnapshot()
        {
            if (undockContinuationPid == 0 || undockContinuationRecIdx < 0) return;
            if (undockContinuationRecIdx >= RecordingStore.CommittedRecordings.Count)
            {
                StopUndockContinuation("stale index in snapshot refresh");
                return;
            }

            var rec = RecordingStore.CommittedRecordings[undockContinuationRecIdx];
            Vessel v = FlightRecorder.FindVesselByPid(undockContinuationPid);

            if (v != null && v.loaded)
            {
                var snapshot = VesselSpawner.TryBackupSnapshot(v);
                if (snapshot != null)
                {
                    rec.GhostVisualSnapshot = snapshot;
                    Log("Undock continuation: refreshed ghost snapshot from loaded vessel");
                }
            }
            else if (rec.GhostVisualSnapshot != null && rec.Points.Count > 0)
            {
                var last = rec.Points[rec.Points.Count - 1];
                rec.GhostVisualSnapshot.SetValue("lat",
                    last.latitude.ToString("R", CultureInfo.InvariantCulture), true);
                rec.GhostVisualSnapshot.SetValue("lon",
                    last.longitude.ToString("R", CultureInfo.InvariantCulture), true);
                rec.GhostVisualSnapshot.SetValue("alt",
                    last.altitude.ToString("R", CultureInfo.InvariantCulture), true);
                Log($"Undock continuation: updated snapshot position from last trajectory point");
            }
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

            // Phase 6b: Evaluate ghost chains and ghost claimed vessels
            EvaluateAndApplyGhostChains();

            // Handle pending tree: show tree merge dialog.
            // On non-revert scene changes, pending trees are auto-committed by ParsekScenario.
            // Reaching here means either a revert or a fallback (auto-commit missed).
            if (RecordingStore.HasPendingTree)
            {
                var pt = RecordingStore.PendingTree;
                ParsekLog.Warn("Flight", $"Pending tree '{pt.TreeName}' reached OnFlightReady — showing tree merge dialog (fallback)");
                MergeDialog.ShowTreeDialog(pt);
            }

            // Handle pending standalone recording.
            // On non-revert scene changes, pending recordings are auto-committed by ParsekScenario.
            // On reverts, the merge dialog is expected here (player chose to revert).
            if (RecordingStore.HasPending)
            {
                var pending = RecordingStore.Pending;

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
                        RecordingStore.CommitPending();
                        ParsekScenario.ReserveSnapshotCrew();
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

            // Swap reserved crew out of the active vessel so the player
            // can't record with them again (they belong to deferred-spawn vessels)
            int swapResult = ParsekScenario.SwapReservedCrewInFlight();
            if (swapResult > 0)
                Log($"Crew swap on flight ready: {swapResult} crew swapped out of active vessel");
            else if (ParsekScenario.CrewReplacements.Count > 0)
                Log($"Crew swap on flight ready: 0 swapped ({ParsekScenario.CrewReplacements.Count} reservations exist but no matches on active vessel)");

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
            pendingDockMergedPid = 0;
            pendingDockAsTarget = false;
            dockConfirmFrames = 0;
            pendingUndockOtherPid = 0;
            undockConfirmFrames = 0;

            // Clear merge event detection state
            pendingTreeDockMerge = false;
            pendingDockAbsorbedPid = 0;
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
            activeChainId = null;
            activeChainNextIndex = 0;
            activeChainPrevId = null;
            activeChainCrewName = null;
            pendingChainContinuation = false;
            pendingChainIsBoarding = false;
            pendingChainEvaName = null;
            pendingBoardingTargetPid = 0;
            pendingBoundaryAnchor = null;
            continuationVesselPid = 0;
            continuationRecordingIdx = -1;

            // Clear dock/undock state
            pendingDockMergedPid = 0;
            pendingDockAsTarget = false;
            dockConfirmFrames = 0;
            pendingUndockOtherPid = 0;
            undockConfirmFrames = 0;
            undockContinuationPid = 0;
            undockContinuationRecIdx = -1;
            undockContinuationLastUT = -1;

            // Clear merge event detection state
            pendingTreeDockMerge = false;
            pendingDockAbsorbedPid = 0;
            pendingBoardingTargetInTree = false;
            dockingInProgress.Clear();
            treeDestructionDialogPending = false;
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

                activeChains[pid] = chain;
                if (vesselGhoster.GhostVessel(pid))
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
            // Auto-clear stale boarding confirmation after 3 frames
            if (pendingBoardingTargetPid != 0)
            {
                boardingConfirmFrames++;
                if (boardingConfirmFrames > 3)
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
                CommitDockUndockSegment(recorder, PartEventType.Docked, pendingDockMergedPid);
                recorder = null;
                StartRecording();
                if (IsRecording)
                {
                    // Pass undock continuation pid so OnPhysicsFrame can detect sibling switch
                    recorder.UndockSiblingPid = undockContinuationPid;
                    Log($"Recording continues after dock (chain={activeChainId}, idx={activeChainNextIndex})");
                    ScreenMessage("Recording continues (docked)", 2f);
                }
                pendingDockMergedPid = 0;
                pendingDockAsTarget = false;
                dockConfirmFrames = 0;
            }

            // Dock: target (pid unchanged, recorder already stopped by OnPartCouple)
            if (pendingDockAsTarget && recorder != null &&
                !recorder.IsRecording && recorder.CaptureAtStop != null)
            {
                CommitDockUndockSegment(recorder, PartEventType.Docked, pendingDockMergedPid);
                recorder = null;
                StartRecording();
                if (IsRecording)
                {
                    recorder.UndockSiblingPid = undockContinuationPid;
                    Log($"Recording continues after dock as target (chain={activeChainId}, idx={activeChainNextIndex})");
                    ScreenMessage("Recording continues (docked)", 2f);
                }
                pendingDockAsTarget = false;
                pendingDockMergedPid = 0;
                dockConfirmFrames = 0;
            }

            // Undock: player stays on remaining vessel (same pid, recorder stopped by StopRecordingForChainBoundary)
            if (pendingUndockOtherPid != 0 && recorder != null &&
                !recorder.IsRecording && recorder.CaptureAtStop != null &&
                !recorder.UndockSwitchPending)
            {
                CommitDockUndockSegment(recorder, PartEventType.Undocked, 0);
                recorder = null;
                StartRecording();
                if (IsRecording)
                {
                    StartUndockContinuation(pendingUndockOtherPid);
                    recorder.UndockSiblingPid = undockContinuationPid;
                    Log($"Recording continues after undock (chain={activeChainId}, idx={activeChainNextIndex})");
                    ScreenMessage("Recording continues (undocked)", 2f);
                }
                pendingUndockOtherPid = 0;
                undockConfirmFrames = 0;
            }

            // Undock: player switched to undocked vessel (pid changed, recorder stopped by OnPhysicsFrame)
            if (recorder != null && recorder.UndockSwitchPending &&
                !recorder.IsRecording && recorder.CaptureAtStop != null)
            {
                uint oldPid = recorder.RecordingVesselId;
                CommitDockUndockSegment(recorder, PartEventType.Undocked, 0);
                recorder = null;

                // Stop existing undock continuation (we're switching roles)
                if (undockContinuationPid != 0)
                    StopUndockContinuation("sibling switch");

                StartRecording();
                if (IsRecording)
                {
                    // The "old" vessel (remaining) becomes continuation
                    StartUndockContinuation(oldPid);
                    recorder.UndockSiblingPid = undockContinuationPid;
                    Log($"Recording continues after undock switch (chain={activeChainId}, idx={activeChainNextIndex})");
                    ScreenMessage("Recording continues (undocked)", 2f);
                }
                pendingUndockOtherPid = 0;
                undockConfirmFrames = 0;
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
            activeChainCrewName = null;

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
            if (activeChainId != null &&
                pendingBoardingTargetPid != 0 &&
                FlightGlobals.ActiveVessel != null &&
                FlightGlobals.ActiveVessel.persistentId == pendingBoardingTargetPid)
            {
                Log($"Chain boarding confirmed: EVA\u2192vessel pid={pendingBoardingTargetPid}");
                pendingBoardingTargetPid = 0;
                boardingConfirmFrames = 0;

                CommitChainSegment(recorder, activeChainCrewName);
                recorder = null;

                // Start new vessel recording on the boarded vessel
                activeChainCrewName = null; // vessel segment, not EVA
                StartRecording();
                if (IsRecording)
                {
                    Log($"Chain vessel recording started (chain={activeChainId}, idx={activeChainNextIndex})");
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
        /// Handles atmosphere boundary auto-split when crossing the atmosphere edge.
        /// Commits the current segment, restarts recording in the new phase.
        /// </summary>
        private void HandleAtmosphereBoundarySplit()
        {
            if (recorder == null || !recorder.IsRecording || !recorder.AtmosphereBoundaryCrossed)
                return;

            string phase = recorder.EnteredAtmosphere ? "exo" : "atmo";
            string newPhase = recorder.EnteredAtmosphere ? "atmo" : "exo";
            string bodyName = FlightGlobals.ActiveVessel?.mainBody?.name ?? "Unknown";
            ParsekLog.Info("Flight", $"Atmosphere auto-split triggered: {bodyName} {phase}\u2192{newPhase} " +
                $"(chain={activeChainId ?? "(new)"}, points={recorder.Recording.Count})");
            recorder.StopRecordingForChainBoundary();
            CommitBoundarySplit(phase, bodyName);
            recorder = null;
            StartRecording();
            if (IsRecording)
            {
                recorder.UndockSiblingPid = undockContinuationPid;
                ParsekLog.Info("Flight", $"Recording continues after atmosphere boundary " +
                    $"(chain={activeChainId}, idx={activeChainNextIndex}, {bodyName} {phase}\u2192{newPhase})");
                ParsekLog.ScreenMessage($"Recording continues ({(newPhase == "atmo" ? "entering" : "exiting")} atmosphere)", 2f);
            }
        }

        /// <summary>
        /// Handles SOI change auto-split when transitioning between celestial bodies.
        /// Commits the current segment, restarts recording in the new SOI.
        /// </summary>
        private void HandleSoiChangeSplit()
        {
            if (recorder == null || !recorder.IsRecording || !recorder.SoiChangePending)
                return;

            string fromBody = recorder.SoiChangeFromBody ?? "Unknown";
            string toBody = FlightGlobals.ActiveVessel?.mainBody?.name ?? "Unknown";
            // Completed segment was at fromBody — tag as "exo" if it has atmosphere, "space" otherwise
            CelestialBody fromCB = FlightGlobals.Bodies?.Find(b => b.name == fromBody);
            string fromPhase = (fromCB != null && fromCB.atmosphere) ? "exo" : "space";
            ParsekLog.Info("Flight", $"SOI auto-split triggered: {fromBody} ({fromPhase}) \u2192 {toBody} " +
                $"(chain={activeChainId ?? "(new)"}, points={recorder.Recording.Count})");
            recorder.StopRecordingForChainBoundary();
            CommitBoundarySplit(fromPhase, fromBody);
            recorder = null;
            StartRecording();
            if (IsRecording)
            {
                recorder.UndockSiblingPid = undockContinuationPid;
                ParsekLog.Info("Flight", $"Recording continues after SOI change " +
                    $"({fromBody} \u2192 {toBody}, chain={activeChainId}, idx={activeChainNextIndex})");
                ParsekLog.ScreenMessage($"Recording continues (entering {toBody} SOI)", 2f);
            }
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

                if (pendingChainContinuation)
                {
                    pendingChainContinuation = false;
                    pendingChainEvaName = null;
                    Log($"Chain EVA recording started (chain={activeChainId}, idx={activeChainNextIndex}, crew={activeChainCrewName})");
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
            // Backspace - Exit watch mode (return to vessel)
            if (watchedRecordingIndex >= 0 && Input.GetKeyDown(KeyCode.Backspace))
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
            // belongs to the chain root only.  activeChainId is set by
            // CommitBoundarySplit / CommitChainSegment / CommitDockUndockSegment *before*
            // this method is called, so a non-null value reliably indicates a continuation.
            bool isContinuation = activeChainId != null;

            recorder = new FlightRecorder();
            if (pendingBoundaryAnchor.HasValue)
            {
                recorder.BoundaryAnchor = pendingBoundaryAnchor;
                pendingBoundaryAnchor = null;
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
            ParsekLog.Info("Flight", $"StartRecording succeeded: pid={pid}, chainActive={activeChainId != null}, tree={activeTree != null}");
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
            if (recorder?.CaptureAtStop != null && activeChainId != null)
            {
                recorder.CaptureAtStop.ChainId = activeChainId;
                recorder.CaptureAtStop.ChainIndex = activeChainNextIndex;
                recorder.CaptureAtStop.ParentRecordingId = activeChainPrevId;
                recorder.CaptureAtStop.EvaCrewName = activeChainCrewName;
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
                        recorder.CaptureAtStop.SegmentPhase = "space";
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
            // Guard: no recording data
            if (recorder == null || recorder.Recording.Count < 2)
            {
                ParsekLog.ScreenMessage("No recording to commit", 2f);
                return;
            }

            // Guard: mid-chain recordings can't be committed standalone
            if (activeChainId != null)
            {
                ParsekLog.ScreenMessage("Cannot commit mid-chain \u2014 finish or revert first", 2f);
                Log("CommitFlight blocked: active chain in progress");
                return;
            }

            // Stop recording if still active
            if (recorder.IsRecording)
                StopRecording();

            // Stop any continuation sampling
            if (continuationVesselPid != 0)
            {
                RefreshContinuationSnapshot();
                StopContinuation("commit flight");
            }
            if (undockContinuationPid != 0)
            {
                RefreshUndockContinuationSnapshot();
                StopUndockContinuation("commit flight");
            }

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
            RecordingStore.CommitPending();

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
            if (continuationVesselPid != 0)
            {
                RefreshContinuationSnapshot();
                StopContinuation("tree commit");
            }
            if (undockContinuationPid != 0)
            {
                RefreshUndockContinuationSnapshot();
                StopUndockContinuation("tree commit");
            }

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

            // Reserve crew for all leaf vessels (except active vessel)
            var spawnableLeaves = activeTree.GetSpawnableLeaves();
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
                        ParsekScenario.ReserveCrewIn(leaf.VesselSnapshot, false, roster);
                    }
                }
                finally
                {
                    GameStateRecorder.SuppressCrewEvents = false;
                }
            }

            // Commit tree to storage
            RecordingStore.CommitTree(activeTree);

            // Spawn all non-active leaf vessels
            SpawnTreeLeaves(activeTree, activeRecId);

            // Crew swap on active vessel
            int swapped = ParsekScenario.SwapReservedCrewInFlight();
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
            {
                var rec = kvp.Value;

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
                        rec.TerminalStateValue = RecordingTree.DetermineTerminalState((int)vessel.situation);
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

            // Compute tree-level resource delta
            tree.DeltaFunds = ComputeTreeDeltaFunds(tree);
            tree.DeltaScience = ComputeTreeDeltaScience(tree);
            tree.DeltaReputation = ComputeTreeDeltaReputation(tree);

            ParsekLog.Info("Flight",
                $"FinalizeTreeRecordings: tree '{tree.TreeName}' resource delta: " +
                $"funds={tree.DeltaFunds:+0.0;-0.0}, science={tree.DeltaScience:+0.0;-0.0}, " +
                $"rep={tree.DeltaReputation:+0.0;-0.0}");
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
                    uint spawnedPid = VesselSpawner.RespawnVessel(leaf.VesselSnapshot);
                    if (spawnedPid != 0)
                    {
                        leaf.VesselSpawned = true;
                        leaf.SpawnedVesselPersistentId = spawnedPid;
                        leaf.LastAppliedResourceIndex = leaf.Points.Count - 1;
                        ResourceBudget.Invalidate();
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

                    // Build flag ghosts for preview
                    if (previewRecording.FlagEvents != null && previewRecording.FlagEvents.Count > 0)
                    {
                        previewGhostState.flagGhosts = new List<GameObject>(previewRecording.FlagEvents.Count);
                        for (int fi = 0; fi < previewRecording.FlagEvents.Count; fi++)
                            previewGhostState.flagGhosts.Add(GhostVisualBuilder.BuildFlagGhost(previewRecording.FlagEvents[fi]));
                        GhostPlaybackLogic.InitializeFlagVisibility(previewRecording, previewGhostState);
                    }

                    Log("Manual preview ghost: built from recording-start snapshot (with part events)");
                }
            }

            if (!builtFromSnapshot)
            {
                Color previewColor = Color.green;
                ghost = CreateGhostSphere("Parsek_Ghost_Preview", previewColor);
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

                DestroyReentryFxResources(previewGhostState.reentryFxInfo);

                GhostPlaybackLogic.DestroyAllFakeCanopies(previewGhostState);
                GhostPlaybackLogic.DestroyAllFlagGhosts(previewGhostState);
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
            InterpolateAndPosition(ghostObject, recording, orbitSegments,
                ref lastPlaybackIndex, recordingTime, 10000, out interpResult,
                surfaceRelativeRotation: srfRel);

            if (previewGhostState != null && previewRecording != null)
            {
                previewGhostState.SetInterpolated(interpResult);
                GhostPlaybackLogic.ApplyPartEvents(-1, previewRecording, recordingTime, previewGhostState);
                GhostPlaybackLogic.ApplyFlagEvents(previewGhostState, previewRecording, recordingTime);
                UpdateReentryFx(-1, previewGhostState, previewRecording.VesselName ?? "Preview");
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
                    // TODO: cachedIdx resets to 0 each frame — when ghost GO creation is implemented
                    // (6b-4), cache this on the ghost state or chain for O(1) amortized lookup.
                    bool srfRel = bgRec.RecordingFormatVersion >= 5;
                    int cachedIdx = 0;
                    InterpolateAndPosition(info.ghostGO, bgRec.Points, bgRec.OrbitSegments,
                        ref cachedIdx, currentUT, (int)(chain.OriginalVesselPid * 10000),
                        out _, surfaceRelativeRotation: srfRel);

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
        /// Routes spawn through VesselGhoster for chain tips, or existing
        /// SpawnOrRecoverIfTooClose for normal recordings.
        /// Handles collision-blocked chains: if spawn is blocked, chain stays active.
        /// If chain was previously blocked, tries to spawn at propagated position.
        /// </summary>
        private void SpawnVesselOrChainTip(Recording rec, int index)
        {
            // Real vessel dedup: if a vessel with this PID still exists in the game world,
            // skip spawning a duplicate. This runs only at actual spawn time (pastChainEnd),
            // not every frame like ShouldSpawnAtRecordingEnd.
            if (rec.VesselPersistentId != 0 && GhostPlaybackLogic.RealVesselExists(rec.VesselPersistentId))
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

        void UpdateTimelinePlayback()
        {
            var committed = RecordingStore.CommittedRecordings;
            double currentUT = Planetarium.GetUniversalTime();

            // Phase 6b: Position chain ghosts (independent of per-recording iteration)
            PositionChainGhosts(currentUT);

            // Flush deferred spawns when warp ends — only flush in-bubble spawns
            if (GhostPlaybackLogic.ShouldFlushDeferredSpawns(pendingSpawnRecordingIds.Count, IsAnyWarpActive()))
            {
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

            if (committed.Count == 0) return;

            float warpRate = TimeWarp.CurrentRate;
            bool suppressGhosts = GhostPlaybackLogic.ShouldSuppressGhosts(warpRate);
            bool suppressVisualFx = GhostPlaybackLogic.ShouldSuppressVisualFx(warpRate);

            // Reset reshow dedup when entering warp suppression so the next warp-down
            // re-show gets logged once per ghost.
            if (suppressGhosts)
                loggedReshow.Clear();

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                bool hasPoints = rec.Points.Count >= 2;
                bool hasOrbitData = rec.OrbitSegments.Count > 0;
                bool hasSurfaceData = rec.SurfacePos.HasValue;
                if (!hasPoints && !hasOrbitData && !hasSurfaceData) continue;

                bool inRange = currentUT >= rec.StartUT && currentUT <= rec.EndUT;
                bool pastEnd = currentUT > rec.EndUT;
                GhostPlaybackState state;
                ghostStates.TryGetValue(i, out state);
                bool ghostActive = state != null && state.ghost != null;

                // Disabled segments: destroy ghost if active, still apply resource deltas
                if (!rec.PlaybackEnabled)
                {
                    DestroyAllOverlapGhosts(i);
                    if (ghostActive)
                    {
                        DestroyTimelineGhost(i);
                        ParsekLog.Verbose("Flight", $"Ghost #{i} destroyed — segment disabled " +
                            $"({rec.VesselName}, {RecordingStore.GetSegmentPhaseLabel(rec)})");
                    }
                    if (pastEnd && rec.TreeId == null)
                        ApplyResourceDeltas(rec, currentUT);
                    continue;
                }

                // External vessel ghost skip: if this is a background tree recording
                // whose real vessel still exists in the game world, skip ghost spawning.
                // Tree-owned vessels bypass this check (their ghost replays the recorded flight).
                bool externalVesselSuppressed = false;
                if (GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                    rec.TreeId, rec.VesselPersistentId, IsActiveTreeRecording(rec)))
                {
                    if (ghostActive)
                    {
                        DestroyTimelineGhost(i);
                        ParsekLog.Verbose("Flight",
                            $"Ghost #{i} destroyed — external vessel '{rec.VesselName}' " +
                            $"pid={rec.VesselPersistentId} real vessel present");
                    }
                    if (loggedGhostEnter.Add(i + 400000))
                        ParsekLog.Verbose("Flight",
                            $"Ghost #{i} suppressed — external vessel '{rec.VesselName}' " +
                            $"pid={rec.VesselPersistentId} exists (not tree-owned)");
                    externalVesselSuppressed = true;
                    // Fall through — spawn-at-end evaluation must still run
                }

                if (ShouldLoopPlayback(rec))
                {
                    // Anchor gating: looped recordings with an anchor vessel only play
                    // when the anchor is loaded. Unanchored loops (anchorPid=0) always play.
                    if (!GhostPlaybackLogic.IsAnchorLoaded(rec.LoopAnchorVesselId, loadedAnchorVessels))
                    {
                        if (ghostActive)
                        {
                            ParsekLog.Info("Loop",
                                $"Anchor not loaded: pid={rec.LoopAnchorVesselId}, " +
                                $"destroying ghost #{i} '{rec.VesselName}'");
                            DestroyAllOverlapGhosts(i);
                            DestroyTimelineGhost(i);
                        }
                        continue;
                    }

                    UpdateLoopingTimelinePlayback(i, rec, currentUT, state, ghostActive,
                        suppressGhosts, suppressVisualFx);
                    continue;
                }

                // Loop was disabled — clean up any leftover overlap ghosts
                DestroyAllOverlapGhosts(i);

                bool isMidChain = RecordingStore.IsChainMidSegment(rec);
                double chainEndUT = isMidChain ? RecordingStore.GetChainEndUT(rec) : rec.EndUT;
                bool pastChainEnd = currentUT > chainEndUT;

                // Phase 6b: Ghost chain spawn suppression — intermediate links and terminated
                // chains skip all spawn logic (both immediate and deferred).
                if (activeGhostChains != null && activeGhostChains.Count > 0)
                {
                    var (chainSuppressed, chainReason) = GhostPlaybackLogic.ShouldSuppressSpawnForChain(
                        activeGhostChains, rec);
                    if (chainSuppressed)
                    {
                        if (loggedGhostEnter.Add(i + 300000))
                            ParsekLog.Info("Flight",
                                $"Chain spawn suppressed: #{i} \"{rec.VesselName}\" — {chainReason}");
                        if (rec.TreeId == null)
                            ApplyResourceDeltas(rec, currentUT);
                        continue;
                    }
                }

                // Compute spawn-decision inputs that depend on external state
                bool isActiveChainMember = activeChainId != null && rec.ChainId == activeChainId;
                bool isChainLoopingOrDisabled = !string.IsNullOrEmpty(rec.ChainId) &&
                    (RecordingStore.IsChainLooping(rec.ChainId) ||
                     RecordingStore.IsChainFullyDisabled(rec.ChainId));

                var (needsSpawn, spawnReason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                    rec, isActiveChainMember, isChainLoopingOrDisabled);

                // One-time suppression logging (keyed per recording index + reason category)
                if (!needsSpawn && !string.IsNullOrEmpty(spawnReason) && loggedGhostEnter.Add(i + 200000))
                    ParsekLog.Verbose("Flight", $"Spawn suppressed for #{i} ({rec.VesselName}): {spawnReason}");

                // One-time chain spawn diagnostics when entering the spawn window
                if (pastChainEnd && !string.IsNullOrEmpty(rec.ChainId) && loggedGhostEnter.Add(i + 100000))
                    Log($"Chain spawn check #{i} \"{rec.VesselName}\": chainId={rec.ChainId}, idx={rec.ChainIndex}, " +
                        $"isMidChain={isMidChain}, hasSnapshot={rec.VesselSnapshot != null}, needsSpawn={needsSpawn}, " +
                        $"chainEndUT={chainEndUT:F1}, currentUT={currentUT:F1}");

                // Side effect: mark VesselSpawned=true when PID is set but VesselSpawned was false
                // (e.g., save/load restored PID but not the flag). This prevents re-evaluating
                // ShouldSpawnAtRecordingEnd every frame for this recording.
                if (!needsSpawn && !rec.VesselSpawned && rec.SpawnedVesselPersistentId != 0)
                {
                    rec.VesselSpawned = true;
                    Log($"Vessel already spawned (pid={rec.SpawnedVesselPersistentId}) — marked VesselSpawned=true for recording #{i}");
                }

                // External vessel suppression: skip ghost visuals while in-range,
                // but allow spawn-at-end evaluation when past-end.
                if (externalVesselSuppressed && inRange)
                {
                    if (rec.TreeId == null) ApplyResourceDeltas(rec, currentUT);
                    continue;
                }

                if (inRange)
                {
                    // High time warp: hide ghost visuals for performance
                    if (suppressGhosts)
                    {
                        if (ghostActive && state.ghost.activeSelf)
                        {
                            state.ghost.SetActive(false);
                            ParsekLog.Info("Flight",
                                $"Ghost #{i} \"{rec.VesselName}\" hidden: warp {warpRate.ToString("F1", CultureInfo.InvariantCulture)}x > {GhostPlaybackLogic.GhostHideWarpThreshold}x");
                            if (watchedRecordingIndex == i)
                                ExitWatchMode();
                        }
                        DestroyAllOverlapGhosts(i);
                        if (rec.TreeId == null) ApplyResourceDeltas(rec, currentUT);
                        continue;
                    }

                    if (hasPoints)
                    {
                        // Normal ghost playback (point-based, with optional orbit segments)
                        if (!ghostActive)
                        {
                            if (softCapSuppressed.Contains(i)) continue; // suppressed by soft cap
                            SpawnTimelineGhost(i, rec);
                            state = ghostStates[i];
                            if (loggedGhostEnter.Add(i))
                                Log($"Ghost ENTERED range: #{i} \"{rec.VesselName}\" at UT {currentUT:F1}");
                        }
                        else if (!state.ghost.activeSelf)
                        {
                            // Don't re-show if ghost is beyond visual range (zone system will handle it)
                            if (state.currentZone != RenderingZone.Beyond)
                            {
                                state.ghost.SetActive(true);
                                if (loggedReshow.Add(i))
                                    ParsekLog.Info("Flight",
                                        $"Ghost #{i} \"{rec.VesselName}\" re-shown after warp-down");
                            }
                        }

                        // --- Zone-based rendering ---
                        double ghostDistance = double.MaxValue;
                        if (FlightGlobals.ActiveVessel != null && state != null && state.ghost != null)
                        {
                            ghostDistance = Vector3d.Distance(
                                state.ghost.transform.position,
                                FlightGlobals.ActiveVessel.transform.position);
                        }
                        var zoneResult = ApplyZoneRendering(i, state, rec, ghostDistance);
                        if (zoneResult.hiddenByZone)
                        {
                            // Still apply resource deltas even when mesh is hidden
                            if (rec.TreeId == null)
                                ApplyResourceDeltas(rec, currentUT);
                            continue;
                        }
                        bool shouldSkipPartEvents = zoneResult.skipPartEvents;

                        int playbackIdx = state.playbackIndex;

                        InterpolationResult interpResult = InterpolationResult.Zero;
                        bool srfRel = rec.RecordingFormatVersion >= 5;

                        // Check if current UT falls within a RELATIVE TrackSection
                        bool usedRelative = false;
                        if (rec.TrackSections != null && rec.TrackSections.Count > 0)
                        {
                            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, currentUT);
                            if (sectionIdx >= 0 && rec.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.Relative)
                            {
                                var section = rec.TrackSections[sectionIdx];
                                var sectionFrames = section.frames ?? rec.Points;

                                // Log relative playback start (once per recording+anchor combo)
                                long relKey = ((long)i << 32) | section.anchorVesselId;
                                if (loggedRelativeStart.Add(relKey))
                                    ParsekLog.Info("Anchor",
                                        $"RELATIVE playback started: recording #{i} \"{rec.VesselName}\" " +
                                        $"anchorPid={section.anchorVesselId} " +
                                        $"sectionUT=[{section.startUT:F1},{section.endUT:F1}]");

                                InterpolateAndPositionRelative(
                                    state.ghost, sectionFrames, ref playbackIdx, currentUT,
                                    section.anchorVesselId, out interpResult);
                                usedRelative = true;
                            }
                        }

                        if (!usedRelative)
                        {
                            InterpolateAndPosition(state.ghost, rec.Points, rec.OrbitSegments,
                                ref playbackIdx, currentUT, i * 10000, out interpResult,
                                surfaceRelativeRotation: srfRel);
                        }

                        state.SetInterpolated(interpResult);
                        state.playbackIndex = playbackIdx;

                        // Part events and FX only in Physics zone
                        if (!shouldSkipPartEvents)
                        {
                            GhostPlaybackLogic.ApplyPartEvents(i, rec, currentUT, state);
                            GhostPlaybackLogic.ApplyFlagEvents(state, rec, currentUT);
                            UpdateReentryFx(i, state, rec.VesselName);
                            if (suppressVisualFx)
                                GhostPlaybackLogic.StopAllRcsEmissions(state);
                            else
                                GhostPlaybackLogic.RestoreAllRcsEmissions(state);
                        }
                        if (rec.TreeId == null)
                            ApplyResourceDeltas(rec, currentUT);
                    }
                    else
                    {
                        // Background-only recording: orbit or surface positioning
                        if (!ghostActive)
                        {
                            if (softCapSuppressed.Contains(i)) continue; // suppressed by soft cap
                            SpawnTimelineGhost(i, rec);
                            state = ghostStates[i];
                        }
                        else if (!state.ghost.activeSelf)
                        {
                            // Don't re-show if ghost is beyond visual range (zone system will handle it)
                            if (state.currentZone != RenderingZone.Beyond)
                            {
                                state.ghost.SetActive(true);
                                if (loggedReshow.Add(i))
                                    ParsekLog.Info("Flight",
                                        $"Ghost #{i} \"{rec.VesselName}\" (background) re-shown after warp-down");
                            }
                        }

                        // --- Zone-based rendering for background ghosts ---
                        double bgGhostDistance = double.MaxValue;
                        if (FlightGlobals.ActiveVessel != null && state != null && state.ghost != null)
                        {
                            bgGhostDistance = Vector3d.Distance(
                                state.ghost.transform.position,
                                FlightGlobals.ActiveVessel.transform.position);
                        }
                        var bgZoneResult = ApplyZoneRendering(i, state, rec, bgGhostDistance);
                        if (bgZoneResult.hiddenByZone)
                        {
                            if (rec.TreeId == null)
                                ApplyResourceDeltas(rec, currentUT);
                            continue;
                        }
                        bool bgSkipPartEvents = bgZoneResult.skipPartEvents;

                        if (state != null && state.ghost != null)
                        {
                            if (hasOrbitData)
                                PositionGhostFromOrbitOnly(state.ghost, rec, currentUT, i * 10000);
                            else if (hasSurfaceData)
                                PositionGhostAtSurface(state.ghost, rec.SurfacePos.Value);
                        }
                        if (loggedGhostEnter.Add(i))
                            ParsekLog.Verbose("Flight", $"Ghost ENTERED range (background): #{i} \"{rec.VesselName}\" " +
                                $"orbit={hasOrbitData} surface={hasSurfaceData}");

                        if (!bgSkipPartEvents)
                        {
                            GhostPlaybackLogic.ApplyPartEvents(i, rec, currentUT, state);
                            GhostPlaybackLogic.ApplyFlagEvents(state, rec, currentUT);
                            // Reentry FX: no InterpolationResult set for background-only path — bodyName stays null,
                            // so UpdateReentryFx will no-op. Intentional: on-rails vessels don't reenter.
                            UpdateReentryFx(i, state, rec.VesselName);
                            if (suppressVisualFx)
                                GhostPlaybackLogic.StopAllRcsEmissions(state);
                            else
                                GhostPlaybackLogic.RestoreAllRcsEmissions(state);
                        }
                        if (rec.TreeId == null)
                            ApplyResourceDeltas(rec, currentUT);
                    }
                }
                else if (pastChainEnd && needsSpawn && ghostActive)
                {
                    // Ghost was playing, UT crossed chain EndUT — spawn vessel, despawn ghost
                    Log($"Ghost EXITED range: #{i} \"{rec.VesselName}\" at UT {currentUT:F1} — spawning vessel");
                    if (rec.Points.Count > 0)
                        PositionGhostAt(state.ghost, rec.Points[rec.Points.Count - 1], rec.RecordingFormatVersion >= 5);

                    if (IsAnyWarpActive())
                    {
                        // Defer spawn until warp ends — ProtoVessel.Load unsafe during warp
                        if (watchedRecordingIndex == i)
                        {
                            // Don't set pendingWatchRecordingId — watch mode is ending because
                            // the recording finished, not pausing for later resumption.
                            // Setting it would cause the deferred spawn to switch the camera
                            // to the spawned vessel instead of staying on the player's vessel.
                            ParsekLog.Info("Flight", $"Watch mode ended at recording end (deferred spawn): #{i} \"{rec.VesselName}\"");
                            ExitWatchMode();
                        }
                        if (pendingSpawnRecordingIds.Add(rec.RecordingId))
                            ParsekLog.Info("Flight", $"Deferred spawn queued (ghost exit during warp): #{i} \"{rec.VesselName}\"");
                        DestroyTimelineGhost(i);
                    }
                    else
                    {
                        // Watch mode: exit and switch player to spawned vessel
                        if (watchedRecordingIndex == i)
                        {
                            ExitWatchMode();
                            SpawnVesselOrChainTip(rec, i);
                            DestroyTimelineGhost(i);
                            if (rec.SpawnedVesselPersistentId != 0)
                            {
                                ParsekLog.Info("CameraFollow",
                                    $"Recording #{i} spawned vessel pid={rec.SpawnedVesselPersistentId} \u2014 switching active vessel");
                                StartCoroutine(DeferredActivateVessel(rec.SpawnedVesselPersistentId));
                            }
                        }
                        else
                        {
                            SpawnVesselOrChainTip(rec, i);
                            DestroyTimelineGhost(i);
                        }
                    }
                    if (rec.TreeId == null)
                        ApplyResourceDeltas(rec, currentUT);
                }
                else if (pastChainEnd && needsSpawn && !ghostActive)
                {
                    if (IsAnyWarpActive())
                    {
                        // Defer spawn until warp ends — ProtoVessel.Load unsafe during warp
                        if (pendingSpawnRecordingIds.Add(rec.RecordingId))
                            ParsekLog.Info("Flight", $"Deferred spawn queued (past EndUT during warp): #{i} \"{rec.VesselName}\"");
                    }
                    else
                    {
                        // UT already past chain EndUT on scene load — spawn immediately, no ghost
                        Log($"Ghost SKIPPED (UT already past EndUT): #{i} \"{rec.VesselName}\" at UT {currentUT:F1} " +
                            $"(EndUT={rec.EndUT:F1}) — spawning vessel immediately");
                        SpawnVesselOrChainTip(rec, i);
                    }
                    if (rec.TreeId == null)
                        ApplyResourceDeltas(rec, currentUT);
                }
                else if (pastEnd && ghostActive && isMidChain && !pastChainEnd)
                {
                    // Mid-chain segment past its own EndUT but chain still playing — hold at final pos
                    if (rec.Points.Count > 0)
                        PositionGhostAt(state.ghost, rec.Points[rec.Points.Count - 1], rec.RecordingFormatVersion >= 5);

                    // Watch mode: auto-follow to next chain segment
                    if (watchedRecordingIndex == i)
                    {
                        int nextIdx = FindNextWatchTarget(i, rec);
                        if (nextIdx >= 0)
                            TransferWatchToNextSegment(nextIdx);
                    }

                    if (pastEnd && rec.TreeId == null)
                        ApplyResourceDeltas(rec, currentUT);
                }
                else
                {
                    // Outside time range, no spawn needed — despawn ghost if active
                    if (ghostActive)
                    {
                        // Watch mode: try auto-follow to next segment before holding/exiting
                        if (watchedRecordingIndex == i)
                        {
                            int nextIdx = FindNextWatchTarget(i, rec);
                            if (nextIdx >= 0)
                            {
                                // Found a successor segment with an active ghost — transfer immediately
                                TransferWatchToNextSegment(nextIdx);
                                // Fall through to destroy this ghost below (it's past its time range)
                            }
                            else if (watchEndHoldUntilUT < 0)
                            {
                                double holdSeconds = rec.TerminalStateValue == TerminalState.Destroyed ? 5.0 : 3.0;
                                watchEndHoldUntilUT = Planetarium.GetUniversalTime() + holdSeconds;
                                ParsekLog.Info("CameraFollow",
                                    $"Recording #{i} ended \u2014 holding camera at last position until UT {watchEndHoldUntilUT.ToString("F1", CultureInfo.InvariantCulture)}");
                                TriggerExplosionIfDestroyed(state, rec, i);
                                // Keep ghost alive during hold — skip destroy
                                if (pastEnd && rec.TreeId == null)
                                    ApplyResourceDeltas(rec, currentUT);
                                continue;
                            }
                            else if (Planetarium.GetUniversalTime() < watchEndHoldUntilUT)
                            {
                                // Still holding — try auto-follow each frame (next ghost may spawn during hold)
                                // (nextIdx was already checked above and was -1, so just keep holding)
                                if (pastEnd && rec.TreeId == null)
                                    ApplyResourceDeltas(rec, currentUT);
                                continue;
                            }
                            else
                            {
                                // Hold expired — exit watch mode and destroy ghost
                                ParsekLog.Info("CameraFollow",
                                    $"Hold expired for recording #{i} \u2014 returning to active vessel");
                                ExitWatchMode();
                            }
                        }

                        TriggerExplosionIfDestroyed(state, rec, i);
                        Log($"Ghost EXITED range: #{i} \"{rec.VesselName}\" at UT {currentUT:F1} — no vessel to spawn");
                        DestroyTimelineGhost(i);
                    }
                    // Catch up resource deltas for recordings we jumped past
                    if (pastEnd && rec.TreeId == null)
                        ApplyResourceDeltas(rec, currentUT);
                }
            }

            // --- Ghost soft cap evaluation ---
            // Collect per-zone ghost counts and priorities, then evaluate caps.
            // Uses cached lists to avoid per-frame allocation.
            cachedZone1Ghosts.Clear();
            cachedZone2Ghosts.Clear();

            foreach (var kvp in ghostStates)
            {
                int idx = kvp.Key;
                var capState = kvp.Value;
                if (capState == null || capState.ghost == null) continue;
                if (idx < 0 || idx >= committed.Count) continue;

                var capRec = committed[idx];
                var priority = GhostSoftCapManager.ClassifyPriority(capRec, capState.loopCycleIndex);

                if (capState.currentZone == RenderingZone.Physics)
                    cachedZone1Ghosts.Add((idx, priority));
                else if (capState.currentZone == RenderingZone.Visual)
                    cachedZone2Ghosts.Add((idx, priority));
            }

            if (cachedZone1Ghosts.Count > GhostSoftCapManager.Zone1ReduceThreshold ||
                cachedZone2Ghosts.Count > GhostSoftCapManager.Zone2SimplifyThreshold)
            {
                // Caps still active — keep suppression set
                var capActions = GhostSoftCapManager.EvaluateCaps(
                    cachedZone1Ghosts.Count, cachedZone2Ghosts.Count,
                    cachedZone1Ghosts, cachedZone2Ghosts);

                if (capActions.Count > 0)
                {
                    if (!softCapTriggeredThisFrame)
                    {
                        ParsekLog.Info("SoftCap",
                            $"Cap triggered: zone1={cachedZone1Ghosts.Count} zone2={cachedZone2Ghosts.Count} " +
                            $"actions={capActions.Count}");
                        softCapTriggeredThisFrame = true;
                    }

                    foreach (var capKvp in capActions)
                    {
                        int capIdx = capKvp.Key;
                        GhostCapAction action = capKvp.Value;

                        switch (action)
                        {
                            case GhostCapAction.Despawn:
                                ParsekLog.Info("SoftCap",
                                    $"Despawning ghost #{capIdx} \"{committed[capIdx].VesselName}\"");
                                DestroyTimelineGhost(capIdx);
                                softCapSuppressed.Add(capIdx); // prevent re-spawn next frame
                                break;
                            case GhostCapAction.SimplifyToOrbitLine:
                                GhostPlaybackState simplifyState;
                                if (ghostStates.TryGetValue(capIdx, out simplifyState) &&
                                    simplifyState?.ghost != null && simplifyState.ghost.activeSelf)
                                {
                                    simplifyState.ghost.SetActive(false);
                                    ParsekLog.Verbose("SoftCap",
                                        $"Simplified ghost #{capIdx} \"{committed[capIdx].VesselName}\" — mesh hidden");
                                }
                                break;
                            case GhostCapAction.ReduceFidelity:
                                // Placeholder — real fidelity reduction requires mesh part culling.
                                // For now, just suppress FX (which is already handled by zone policies).
                                ParsekLog.Verbose("SoftCap",
                                    $"ReduceFidelity ghost #{capIdx} \"{committed[capIdx].VesselName}\" (no-op placeholder)");
                                break;
                        }
                    }
                }
            }
            else
            {
                softCapTriggeredThisFrame = false;
                // Caps no longer active — clear suppression so ghosts can spawn again
                if (softCapSuppressed.Count > 0)
                {
                    ParsekLog.Verbose("SoftCap",
                        $"Caps resolved, clearing {softCapSuppressed.Count} suppressed ghosts");
                    softCapSuppressed.Clear();
                }
            }

            // Apply tree-level resource deltas (lump sum when UT passes tree EndUT)
            ApplyTreeResourceDeltas(currentUT);

            // Watch mode: verify ghost still exists (skip during explosion hold —
            // the ghost is intentionally destroyed while camera is parked on anchor)
            if (watchedRecordingIndex >= 0 && watchedOverlapCycleIndex != -2)
            {
                GhostPlaybackState ws;
                bool ghostOk = ghostStates.TryGetValue(watchedRecordingIndex, out ws)
                               && ws != null && ws.ghost != null;
                if (!ghostOk)
                {
                    ParsekLog.Warn("CameraFollow",
                        $"Watched ghost #{watchedRecordingIndex} destroyed \u2014 auto-exiting watch mode");
                    ExitWatchMode();
                }
            }
        }

        private bool ShouldLoopPlayback(Recording rec)
        {
            if (rec == null || !rec.LoopPlayback || rec.Points == null || rec.Points.Count < 2)
                return false;
            return rec.EndUT - rec.StartUT > MinLoopDurationSeconds;
        }

        /// <summary>
        /// Checks whether a recording is the active (player-controlled) recording within
        /// its tree. Returns false for non-tree recordings. Used to determine whether
        /// the external vessel ghost skip should apply (active recording is never skipped).
        /// </summary>
        private bool IsActiveTreeRecording(Recording rec)
        {
            if (string.IsNullOrEmpty(rec.TreeId)) return false;
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
                                    ?? DefaultLoopIntervalSeconds;
            return GhostPlaybackLogic.ResolveLoopInterval(rec, globalInterval, DefaultLoopIntervalSeconds, GhostPlaybackLogic.MinCycleDuration);
        }

        private bool TryComputeLoopPlaybackUT(
            Recording rec,
            double currentUT,
            out double loopUT,
            out int cycleIndex,
            out bool inPauseWindow,
            int recIdx = -1)
        {
            loopUT = rec != null ? rec.StartUT : 0;
            cycleIndex = 0;
            inPauseWindow = false;
            if (rec == null || rec.Points == null || rec.Points.Count < 2) return false;
            if (currentUT < rec.StartUT) return false;

            double duration = rec.EndUT - rec.StartUT;
            if (duration <= MinLoopDurationSeconds) return false;

            double intervalSeconds = GetLoopIntervalSeconds(rec);
            double cycleDuration = duration + intervalSeconds;
            if (cycleDuration <= MinLoopDurationSeconds)
                cycleDuration = duration;

            double elapsed = currentUT - rec.StartUT;

            // Apply loop phase offset (set by Watch mode to reset ghost to recording start)
            double phaseOffset;
            if (recIdx >= 0 && loopPhaseOffsets.TryGetValue(recIdx, out phaseOffset))
                elapsed += phaseOffset;

            cycleIndex = (int)Math.Floor(elapsed / cycleDuration);
            if (cycleIndex < 0) cycleIndex = 0;

            double cycleTime = elapsed - (cycleIndex * cycleDuration);
            if (intervalSeconds > 0 && cycleTime > duration)
            {
                inPauseWindow = true;
                loopUT = rec.EndUT;
                return true;
            }

            loopUT = rec.StartUT + Math.Min(cycleTime, duration);
            return true;
        }

        private void UpdateLoopingTimelinePlayback(
            int recIdx,
            Recording rec,
            double currentUT,
            GhostPlaybackState state,
            bool ghostActive,
            bool suppressGhosts,
            bool suppressVisualFx)
        {
            double intervalSeconds = GetLoopIntervalSeconds(rec);
            double duration = rec.EndUT - rec.StartUT;

            // High time warp: hide ghost, destroy overlaps
            if (suppressGhosts)
            {
                if (ghostActive && state.ghost.activeSelf)
                {
                    state.ghost.SetActive(false);
                    ParsekLog.Info("Flight",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" (loop) hidden: warp > {GhostPlaybackLogic.GhostHideWarpThreshold}x");
                    if (watchedRecordingIndex == recIdx)
                        ExitWatchMode();
                }
                DestroyAllOverlapGhosts(recIdx);
                return;
            }

            // For negative intervals: use multi-cycle overlap path
            if (intervalSeconds < 0)
            {
                UpdateOverlapLoopPlayback(recIdx, rec, currentUT, state, ghostActive,
                    intervalSeconds, duration, suppressVisualFx);
                return;
            }

            // --- Positive/zero interval: single ghost path (no overlap) ---
            // Clean up any leftover overlap ghosts from a previous negative interval
            DestroyAllOverlapGhosts(recIdx);
            double loopUT;
            int cycleIndex;
            bool inPauseWindow;
            if (!TryComputeLoopPlaybackUT(rec, currentUT, out loopUT, out cycleIndex, out inPauseWindow, recIdx))
            {
                if (ghostActive)
                    DestroyTimelineGhost(recIdx);
                return;
            }

            // Rebuild once per loop cycle to guarantee clean visual state and event indices.
            bool cycleChanged = !ghostActive || state == null || state.loopCycleIndex != cycleIndex;
            if (cycleChanged && ghostActive)
            {
                // Position at final point so explosion appears at crash site, not mid-air
                if (rec.Points.Count > 0 && state != null && state.ghost != null)
                    PositionGhostAt(state.ghost, rec.Points[rec.Points.Count - 1],
                        rec.RecordingFormatVersion >= 5);

                bool isWatching = watchedRecordingIndex == recIdx;
                bool needsExplosion = state != null
                    && rec.TerminalStateValue == TerminalState.Destroyed
                    && !state.explosionFired;

                TriggerExplosionIfDestroyed(state, rec, recIdx);

                // Determine camera transition: -2 = hold at explosion, -1 = ready for re-target
                int newWatchCycle = GhostPlaybackLogic.ComputeWatchCycleOnLoopRebuild(
                    watchedOverlapCycleIndex, isWatching, needsExplosion, inPauseWindow);

                if (newWatchCycle == -2)
                {
                    // Hold camera at explosion site
                    if (overlapCameraAnchor != null) Destroy(overlapCameraAnchor);
                    overlapCameraAnchor = new GameObject("ParsekLoopCameraAnchor");
                    if (state != null && state.ghost != null)
                        overlapCameraAnchor.transform.position = state.ghost.transform.position;
                    if (FlightCamera.fetch != null)
                        FlightCamera.fetch.SetTargetTransform(overlapCameraAnchor.transform);
                    overlapRetargetAfterUT = Planetarium.GetUniversalTime() + OverlapExplosionHoldSeconds;
                    watchedOverlapCycleIndex = -2;
                    ParsekLog.Info("CameraFollow",
                        $"Loop: watched cycle={state?.loopCycleIndex} exploded, holding camera for {OverlapExplosionHoldSeconds:F0}s");
                }
                else if (newWatchCycle == -1 && isWatching)
                {
                    // Explosion already fired (e.g. during pause window with time warp) or
                    // non-explosion terminal state — mark ready for immediate re-target
                    // when the new ghost spawns below.
                    // Clean up any active hold state (anchor, timer) from a previous cycle.
                    watchedOverlapCycleIndex = -1;
                    overlapRetargetAfterUT = -1;
                    if (overlapCameraAnchor != null) { Destroy(overlapCameraAnchor); overlapCameraAnchor = null; }
                    ParsekLog.Info("CameraFollow",
                        $"Loop: watched cycle={state?.loopCycleIndex} ended (no hold needed), ready for re-target");
                }

                GhostPlaybackLogic.ResetReentryFx(state, recIdx);
                DestroyTimelineGhost(recIdx);
                ghostActive = false;
                state = null;
            }

            // Looped ghost distance gating: check whether ghost should spawn at this distance
            double loopGhostDistance = double.MaxValue;
            if (FlightGlobals.ActiveVessel != null)
            {
                // For distance check before ghost exists, estimate from first trajectory point
                if (ghostActive && state != null && state.ghost != null)
                {
                    loopGhostDistance = Vector3d.Distance(
                        state.ghost.transform.position,
                        FlightGlobals.ActiveVessel.transform.position);
                }
                else if (rec.Points.Count > 0)
                {
                    // Estimate distance from first point's body-relative position
                    // (imprecise but sufficient for spawn gating)
                    loopGhostDistance = double.MaxValue; // will be computed after spawn
                }
            }

            var (loopShouldSpawn, loopSimplified) =
                GhostPlaybackLogic.EvaluateLoopedGhostSpawn(loopGhostDistance);

            // If ghost is active, compute precise distance for zone classification
            if (ghostActive && state != null && state.ghost != null && FlightGlobals.ActiveVessel != null)
            {
                loopGhostDistance = Vector3d.Distance(
                    state.ghost.transform.position,
                    FlightGlobals.ActiveVessel.transform.position);
                var loopSpawnCheck = GhostPlaybackLogic.EvaluateLoopedGhostSpawn(loopGhostDistance);
                loopShouldSpawn = loopSpawnCheck.shouldSpawn;
                loopSimplified = loopSpawnCheck.simplified;
            }

            // Suppress ghost beyond looped ghost spawn threshold (but not if player is watching it)
            if (!loopShouldSpawn && ghostActive && watchedRecordingIndex != recIdx)
            {
                RenderingZoneManager.LogLoopedGhostSpawnDecision(
                    $"#{recIdx} \"{rec.VesselName}\"", loopGhostDistance, false, false);
                if (state.ghost.activeSelf)
                    state.ghost.SetActive(false);
                return;
            }

            if (!ghostActive)
            {
                SpawnTimelineGhost(recIdx, rec);
                if (!ghostStates.TryGetValue(recIdx, out state) || state == null)
                    return;
                state.loopCycleIndex = cycleIndex;
                if (loggedGhostEnter.Add(recIdx))
                    Log($"Ghost ENTERED range: #{recIdx} \"{rec.VesselName}\" at UT {currentUT:F1} (loop cycle={cycleIndex})");

                // Ghost was rebuilt for new loop cycle — re-target camera
                if (watchedRecordingIndex == recIdx && watchedOverlapCycleIndex == -1
                    && state.ghost != null && FlightCamera.fetch != null)
                {
                    var target = state.cameraPivot ?? state.ghost.transform;
                    FlightCamera.fetch.SetTargetTransform(target);
                    watchedOverlapCycleIndex = cycleIndex;
                    ParsekLog.Info("CameraFollow",
                        $"Loop rebuild re-target: ghost #{recIdx} cycle={cycleIndex}" +
                        $" target='{target.name}' localPos=({target.localPosition.x:F2},{target.localPosition.y:F2},{target.localPosition.z:F2})" +
                        $" camDist={FlightCamera.fetch.Distance:F1}");
                }
            }
            else if (ghostActive && !state.ghost.activeSelf)
            {
                // Don't re-show if ghost is beyond visual range (zone system will handle it)
                if (state.currentZone != RenderingZone.Beyond)
                {
                    state.ghost.SetActive(true);
                    if (loggedReshow.Add(recIdx))
                        ParsekLog.Info("Flight",
                            $"Ghost #{recIdx} \"{rec.VesselName}\" (loop) re-shown after warp-down");
                }
            }

            if (state == null || state.ghost == null)
                return;

            // --- Zone-based rendering for looped ghosts ---
            double loopZoneDistance = double.MaxValue;
            if (FlightGlobals.ActiveVessel != null)
            {
                loopZoneDistance = Vector3d.Distance(
                    state.ghost.transform.position,
                    FlightGlobals.ActiveVessel.transform.position);
            }
            var loopZoneResult = ApplyZoneRendering(recIdx, state, rec, loopZoneDistance);
            if (loopZoneResult.hiddenByZone)
                return;

            // Simplified looped ghost: skip part events even in Physics zone
            bool skipLoopPartEvents = loopZoneResult.skipPartEvents || loopSimplified;

            if (inPauseWindow)
            {
                var lastPt = rec.Points[rec.Points.Count - 1];
                PositionGhostAt(state.ghost, lastPt, rec.RecordingFormatVersion >= 5);
                // Ensure body/altitude are set even during pause — needed for
                // IsGhostOnSameBody (watch button) after a cycle-change respawn.
                if (string.IsNullOrEmpty(state.lastInterpolatedBodyName))
                {
                    state.lastInterpolatedBodyName = lastPt.bodyName;
                    state.lastInterpolatedAltitude = lastPt.altitude;
                }
                // Destroyed recordings get an explosion; all recordings hide during pause
                TriggerExplosionIfDestroyed(state, rec, recIdx);
                if (!state.pauseHidden)
                {
                    state.pauseHidden = true;
                    GhostPlaybackLogic.HideAllGhostParts(state);
                    ParsekLog.Verbose("Flight",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" hidden during loop pause window");
                }
                if (state.reentryFxInfo != null)
                {
                    // Only zero velocity — keep body/altitude from last frame for smooth intensity decay
                    state.lastInterpolatedVelocity = Vector3.zero;
                    UpdateReentryFx(recIdx, state, rec.VesselName);
                }
                return;
            }

            int playbackIdx = state.playbackIndex;
            InterpolationResult interpResult;
            bool srfRel = rec.RecordingFormatVersion >= 5;

            // Anchor-relative loop playback: use InterpolateAndPositionRelative
            // when the recording has a loop anchor and RELATIVE TrackSections.
            bool useAnchor = GhostPlaybackLogic.ShouldUseLoopAnchor(rec);
            if (useAnchor && !GhostPlaybackLogic.ValidateLoopAnchor(rec.LoopAnchorVesselId))
            {
                long anchorKey = ((long)recIdx << 32) | rec.LoopAnchorVesselId;
                if (loggedAnchorNotFound.Add(anchorKey))
                    ParsekLog.Warn("Loop",
                        $"Loop anchor vessel pid={rec.LoopAnchorVesselId} not found for " +
                        $"recording #{recIdx} \"{rec.VesselName}\" — falling back to absolute positioning");
                useAnchor = false;
            }

            PositionLoopGhost(state.ghost, rec, ref playbackIdx, loopUT,
                recIdx, recIdx * 10000, srfRel, useAnchor, out interpResult);
            state.SetInterpolated(interpResult);
            state.playbackIndex = playbackIdx;

            if (!skipLoopPartEvents)
            {
                GhostPlaybackLogic.ApplyPartEvents(recIdx, rec, loopUT, state);
                GhostPlaybackLogic.ApplyFlagEvents(state, rec, loopUT);
                UpdateReentryFx(recIdx, state, rec.VesselName);
                if (suppressVisualFx)
                    GhostPlaybackLogic.StopAllRcsEmissions(state);
                else
                    GhostPlaybackLogic.RestoreAllRcsEmissions(state);
            }
        }

        /// <summary>
        /// Multi-cycle overlap path for negative intervals. Multiple ghosts from
        /// different cycles may be visible simultaneously.
        /// </summary>
        private void UpdateOverlapLoopPlayback(
            int recIdx,
            Recording rec,
            double currentUT,
            GhostPlaybackState primaryState,
            bool primaryActive,
            double intervalSeconds,
            double duration,
            bool suppressVisualFx)
        {
            if (currentUT < rec.StartUT)
            {
                if (primaryActive) DestroyTimelineGhost(recIdx);
                DestroyAllOverlapGhosts(recIdx);
                return;
            }

            double cycleDuration = duration + intervalSeconds;
            if (cycleDuration < GhostPlaybackLogic.MinCycleDuration) cycleDuration = GhostPlaybackLogic.MinCycleDuration;

            int firstCycle, lastCycle;
            GhostPlaybackLogic.GetActiveCycles(currentUT, rec.StartUT, rec.EndUT, intervalSeconds,
                MaxOverlapGhostsPerRecording, out firstCycle, out lastCycle);

            // Ensure overlap list exists
            List<GhostPlaybackState> overlaps;
            if (!overlapGhosts.TryGetValue(recIdx, out overlaps))
            {
                overlaps = new List<GhostPlaybackState>();
                overlapGhosts[recIdx] = overlaps;
            }

            bool srfRel = rec.RecordingFormatVersion >= 5;

            // Primary ghost always represents the newest (lastCycle)
            // Check if primary needs to be rebuilt for a new cycle
            bool primaryCycleChanged = !primaryActive || primaryState == null
                || primaryState.loopCycleIndex != lastCycle;

            if (primaryCycleChanged)
            {
                // Move old primary to overlap list if still alive
                if (primaryActive && primaryState != null && primaryState.ghost != null)
                {
                    // Detach from ghostStates (don't destroy) — move to overlap
                    ghostStates.Remove(recIdx);
                    overlaps.Add(primaryState);
                    ParsekLog.Verbose("Flight",
                        $"Ghost #{recIdx} cycle={primaryState.loopCycleIndex} moved to overlap list");
                }
                else if (primaryActive)
                {
                    DestroyTimelineGhost(recIdx);
                }

                // Spawn new primary for lastCycle
                SpawnTimelineGhost(recIdx, rec);
                if (!ghostStates.TryGetValue(recIdx, out primaryState) || primaryState == null)
                {
                    ParsekLog.Warn("Flight",
                        $"Overlap: SpawnTimelineGhost failed for #{recIdx} cycle={lastCycle} — skipping");
                    return;
                }
                primaryState.loopCycleIndex = lastCycle;
                ParsekLog.Info("Flight",
                    $"Ghost ENTERED range: #{recIdx} \"{rec.VesselName}\" cycle={lastCycle} at UT {currentUT:F1} (overlap)");

                // Re-target camera only if ready for a new launch (-1 = not following anyone)
                // -2 = holding after explosion, don't interrupt the hold
                if (watchedRecordingIndex == recIdx && watchedOverlapCycleIndex == -1
                    && primaryState.ghost != null && FlightCamera.fetch != null)
                {
                    var target = primaryState.cameraPivot ?? primaryState.ghost.transform;
                    FlightCamera.fetch.SetTargetTransform(target);
                    watchedOverlapCycleIndex = lastCycle;
                    ParsekLog.Info("CameraFollow",
                        $"Overlap: camera now following ghost #{recIdx} cycle={lastCycle}");
                }
            }

            // Determine if anchor-relative positioning should be used
            bool useAnchor = GhostPlaybackLogic.ShouldUseLoopAnchor(rec);
            if (useAnchor && !GhostPlaybackLogic.ValidateLoopAnchor(rec.LoopAnchorVesselId))
            {
                long anchorKey = ((long)recIdx << 32) | rec.LoopAnchorVesselId;
                if (loggedAnchorNotFound.Add(anchorKey))
                    ParsekLog.Warn("Loop",
                        $"Loop anchor vessel pid={rec.LoopAnchorVesselId} not found for " +
                        $"recording #{recIdx} \"{rec.VesselName}\" (overlap) — falling back to absolute");
                useAnchor = false;
            }

            // Update primary ghost position
            if (primaryState != null && primaryState.ghost != null)
            {
                double cycleStartUT = rec.StartUT + lastCycle * cycleDuration;
                double phase = currentUT - cycleStartUT;
                if (phase < 0) phase = 0;
                if (phase > duration) phase = duration;
                double loopUT = rec.StartUT + phase;

                int playbackIdx = primaryState.playbackIndex;
                InterpolationResult interpResult;
                PositionLoopGhost(primaryState.ghost, rec, ref playbackIdx, loopUT,
                    recIdx, recIdx * 10000, srfRel, useAnchor, out interpResult);
                primaryState.SetInterpolated(interpResult);
                primaryState.playbackIndex = playbackIdx;

                GhostPlaybackLogic.ApplyPartEvents(recIdx, rec, loopUT, primaryState);
                GhostPlaybackLogic.ApplyFlagEvents(primaryState, rec, loopUT);
                UpdateReentryFx(recIdx, primaryState, rec.VesselName);
                if (suppressVisualFx)
                    GhostPlaybackLogic.StopAllRcsEmissions(primaryState);
                else
                    GhostPlaybackLogic.RestoreAllRcsEmissions(primaryState);
            }

            // Update overlap ghosts (older cycles)
            for (int i = overlaps.Count - 1; i >= 0; i--)
            {
                var ovState = overlaps[i];
                if (ovState == null || ovState.ghost == null)
                {
                    overlaps.RemoveAt(i);
                    continue;
                }

                int cycle = ovState.loopCycleIndex;

                // Check if this cycle has expired (phase > duration)
                double cycleStart = rec.StartUT + cycle * cycleDuration;
                double phase = currentUT - cycleStart;
                if (phase > duration)
                {
                    // Position at final point so explosion appears at crash site
                    if (rec.Points.Count > 0 && ovState.ghost != null)
                        PositionGhostAt(ovState.ghost, rec.Points[rec.Points.Count - 1], srfRel);
                    TriggerExplosionIfDestroyed(ovState, rec, recIdx);

                    // If camera was following this cycle, park FlightCamera on a temp anchor
                    // at the explosion site so it doesn't reference the destroyed ghost
                    if (watchedRecordingIndex == recIdx && watchedOverlapCycleIndex == cycle)
                    {
                        overlapRetargetAfterUT = Planetarium.GetUniversalTime() + OverlapExplosionHoldSeconds;
                        watchedOverlapCycleIndex = -2; // sentinel: waiting to re-target

                        // Create temp anchor at ghost position before ghost is destroyed
                        if (overlapCameraAnchor != null) Destroy(overlapCameraAnchor);
                        overlapCameraAnchor = new GameObject("ParsekOverlapCameraAnchor");
                        if (ovState.ghost != null)
                            overlapCameraAnchor.transform.position = ovState.ghost.transform.position;
                        if (FlightCamera.fetch != null)
                            FlightCamera.fetch.SetTargetTransform(overlapCameraAnchor.transform);

                        ParsekLog.Info("CameraFollow",
                            $"Overlap: watched cycle={cycle} expired, holding camera at explosion site for {OverlapExplosionHoldSeconds:F0}s");
                    }

                    ParsekLog.Info("Flight",
                        $"Ghost EXITED range: #{recIdx} \"{rec.VesselName}\" cycle={cycle} (overlap expired)");
                    DestroyOverlapGhostState(ovState);
                    overlaps.RemoveAt(i);
                    continue;
                }

                if (ovState.ghost == null)
                {
                    overlaps.RemoveAt(i);
                    continue;
                }

                if (phase < 0) phase = 0;
                if (phase > duration) phase = duration;
                double loopUT = rec.StartUT + phase;

                int playbackIdx = ovState.playbackIndex;
                InterpolationResult interpResult;
                PositionLoopGhost(ovState.ghost, rec, ref playbackIdx, loopUT,
                    recIdx, recIdx * 10000 + (cycle + 1) * 100, srfRel, useAnchor, out interpResult);
                ovState.SetInterpolated(interpResult);
                ovState.playbackIndex = playbackIdx;

                GhostPlaybackLogic.ApplyPartEvents(recIdx, rec, loopUT, ovState);
                GhostPlaybackLogic.ApplyFlagEvents(ovState, rec, loopUT);
                UpdateReentryFx(recIdx, ovState, rec.VesselName);
                if (suppressVisualFx)
                    GhostPlaybackLogic.StopAllRcsEmissions(ovState);
                else
                    GhostPlaybackLogic.RestoreAllRcsEmissions(ovState);
            }
        }

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
                ResourceBudget.Invalidate();

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
            for (int i = 0; i < trees.Count; i++)
            {
                var tree = trees[i];
                if (tree.ResourcesApplied)
                {
                    ParsekLog.VerboseRateLimited("Flight", "tree-res-skip-applied",
                        $"ApplyTreeResourceDeltas: skipping tree '{tree.TreeName}' — already applied");
                    continue;
                }

                // Compute tree EndUT: max EndUT across all recordings
                double treeEndUT = 0;
                foreach (var rec in tree.Recordings.Values)
                {
                    double recEnd = rec.EndUT;
                    if (recEnd > treeEndUT) treeEndUT = recEnd;
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
            ResourceBudget.Invalidate();
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

        void SpawnTimelineGhost(int index, Recording rec)
        {
            Log($"Spawning timeline ghost #{index} for {rec.VesselName}");

            // Use a slightly different color to distinguish from manual preview
            Color ghostColor = new Color(0.2f, 1f, 0.4f, 0.8f); // bright green-cyan
            GhostBuildResult buildResult = null;
            GameObject ghost = null;
            bool builtFromSnapshot = false;

            // Skip expensive snapshot build when no snapshot exists — go straight to sphere fallback.
            // Avoids per-loop-cycle warning spam for snapshot-less recordings (bug #21).
            if (GhostVisualBuilder.GetGhostSnapshot(rec) != null)
            {
                buildResult = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                    rec, $"Parsek_Timeline_{index}");
                if (buildResult != null)
                    ghost = buildResult.root;
                builtFromSnapshot = ghost != null;
            }

            if (ghost == null)
            {
                ghost = CreateGhostSphere($"Parsek_Timeline_{index}", ghostColor);
                Log($"Timeline ghost #{index}: using sphere fallback");
            }
            else
            {
                bool usedStartSnapshot = rec.GhostVisualSnapshot != null;
                Log(usedStartSnapshot
                    ? $"Timeline ghost #{index}: built from recording-start snapshot"
                    : $"Timeline ghost #{index}: built from vessel snapshot");
            }

            var cameraPivotObj = new GameObject("cameraPivot");
            cameraPivotObj.transform.SetParent(ghost.transform, false);

            var state = new GhostPlaybackState
            {
                ghost = ghost,
                cameraPivot = cameraPivotObj.transform,
                playbackIndex = 0,
                partEventIndex = 0,
                partTree = GhostVisualBuilder.BuildPartSubtreeMap(GhostVisualBuilder.GetGhostSnapshot(rec))
            };

            if (builtFromSnapshot)
            {
                state.materials = new List<Material>();
            }
            else
            {
                var m = ghost.GetComponent<Renderer>()?.material;
                state.materials = m != null ? new List<Material> { m } : new List<Material>();
            }

            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, buildResult);

            GhostPlaybackLogic.InitializeInventoryPlacementVisibility(rec, state);

            state.reentryFxInfo = GhostVisualBuilder.TryBuildReentryFx(
                ghost,
                state.heatInfos,
                index,
                rec.VesselName);
            state.reentryMpb = new MaterialPropertyBlock();

            // Build flag ghosts (world-positioned, not child of vessel ghost)
            if (rec.FlagEvents != null && rec.FlagEvents.Count > 0)
            {
                state.flagGhosts = new List<GameObject>(rec.FlagEvents.Count);
                for (int fi = 0; fi < rec.FlagEvents.Count; fi++)
                {
                    var flagGo = GhostVisualBuilder.BuildFlagGhost(rec.FlagEvents[fi]);
                    state.flagGhosts.Add(flagGo);
                }
                GhostPlaybackLogic.InitializeFlagVisibility(rec, state);
            }

            ghostStates[index] = state;
        }

        internal void DestroyTimelineGhost(int index)
        {
            Log($"Despawning timeline ghost #{index}");

            GhostPlaybackState state;
            if (!ghostStates.TryGetValue(index, out state))
                return;

            if (state.materials != null)
            {
                for (int i = 0; i < state.materials.Count; i++)
                {
                    if (state.materials[i] != null)
                        Destroy(state.materials[i]);
                }
            }

            if (state.engineInfos != null)
            {
                foreach (var info in state.engineInfos.Values)
                    for (int i = 0; i < info.particleSystems.Count; i++)
                        if (info.particleSystems[i] != null)
                            Destroy(info.particleSystems[i].gameObject);
            }

            if (state.rcsInfos != null)
            {
                foreach (var info in state.rcsInfos.Values)
                    for (int i = 0; i < info.particleSystems.Count; i++)
                        if (info.particleSystems[i] != null)
                            Destroy(info.particleSystems[i].gameObject);
            }

            DestroyReentryFxResources(state.reentryFxInfo);

            if (state.ghost != null)
                Destroy(state.ghost);

            GhostPlaybackLogic.DestroyAllFakeCanopies(state);
            GhostPlaybackLogic.DestroyAllFlagGhosts(state);
            ghostStates.Remove(index);
            loopPhaseOffsets.Remove(index);
        }

        public void DestroyAllTimelineGhosts()
        {
            // Copy keys to avoid modifying during iteration
            var keys = new List<int>(ghostStates.Keys);
            foreach (int key in keys)
            {
                DestroyTimelineGhost(key);
            }

            // Destroy all overlap ghosts
            foreach (var kvp in overlapGhosts)
            {
                for (int i = 0; i < kvp.Value.Count; i++)
                    DestroyOverlapGhostState(kvp.Value[i]);
            }
            overlapGhosts.Clear();

            orbitCache.Clear();
            loopPhaseOffsets.Clear();
            loggedGhostEnter.Clear();
            loggedOrbitSegments.Clear();
            loggedOrbitRotationSegments.Clear();
            loggedReshow.Clear();
            pendingSpawnRecordingIds.Clear();
            pendingWatchRecordingId = null;
            loadedAnchorVessels.Clear();
            softCapSuppressed.Clear();
            loggedRelativeStart.Clear();
            loggedAnchorNotFound.Clear();
            CleanupActiveExplosions();
        }

        /// <summary>
        /// Checks whether the recording ended with destruction and spawns an explosion FX if so.
        /// Pure decision logic is in ShouldTriggerExplosion; this method handles the side effects.
        /// </summary>
        void TriggerExplosionIfDestroyed(GhostPlaybackState state, Recording rec, int recIdx)
        {
            if (state == null)
            {
                ParsekLog.Verbose("ExplosionFx", $"TriggerExplosionIfDestroyed: ghost #{recIdx} — skipped (state is null)");
                return;
            }
            if (!GhostPlaybackLogic.ShouldTriggerExplosion(state.explosionFired, rec.TerminalStateValue,
                    state.ghost != null, rec.VesselName, recIdx))
                return;

            if (GhostPlaybackLogic.ShouldSuppressVisualFx(TimeWarp.CurrentRate))
            {
                state.explosionFired = true;
                GhostPlaybackLogic.HideAllGhostParts(state);
                ParsekLog.VerboseRateLimited("ExplosionFx", $"explosion-suppress-{recIdx}",
                    $"Explosion suppressed for ghost #{recIdx} \"{rec.VesselName}\": " +
                    $"warp rate {TimeWarp.CurrentRate.ToString("F1", CultureInfo.InvariantCulture)}x > {GhostPlaybackLogic.FxSuppressWarpThreshold}x");
                return;
            }

            state.explosionFired = true;

            Vector3 worldPos = state.ghost.transform.position;
            float vesselLength = state.reentryFxInfo != null
                ? state.reentryFxInfo.vesselLength
                : GhostVisualBuilder.ComputeGhostLength(state.ghost);

            ParsekLog.Info("ExplosionFx",
                $"Triggering explosion for ghost #{recIdx} \"{rec.VesselName}\" " +
                $"at ({worldPos.x:F1},{worldPos.y:F1},{worldPos.z:F1}) vesselLength={vesselLength:F1}m");

            var explosion = GhostVisualBuilder.SpawnExplosionFx(worldPos, vesselLength);
            if (explosion != null)
            {
                // Auto-destroy after 6 seconds to prevent accumulation during looping
                Destroy(explosion, 6f);
                activeExplosions.Add(explosion);

                // Prune already-destroyed entries to keep list bounded
                if (activeExplosions.Count > 20)
                {
                    for (int e = activeExplosions.Count - 1; e >= 0; e--)
                    {
                        if (activeExplosions[e] == null)
                            activeExplosions.RemoveAt(e);
                    }
                }

                ParsekLog.Verbose("ExplosionFx",
                    $"Explosion GO created for ghost #{recIdx}, activeExplosions.Count={activeExplosions.Count}");
            }

            // Hide all ghost parts so the explosion plays over empty space
            GhostPlaybackLogic.HideAllGhostParts(state);
            ParsekLog.Verbose("ExplosionFx", $"Ghost #{recIdx} parts hidden after explosion");
        }

        void CleanupActiveExplosions()
        {
            if (activeExplosions.Count == 0) return;
            int destroyed = 0;
            for (int i = activeExplosions.Count - 1; i >= 0; i--)
            {
                if (activeExplosions[i] != null)
                {
                    Destroy(activeExplosions[i]);
                    destroyed++;
                }
            }
            ParsekLog.Verbose("ExplosionFx", $"CleanupActiveExplosions: destroyed {destroyed}/{activeExplosions.Count} explosion GOs");
            activeExplosions.Clear();
        }

        /// <summary>
        /// Destroys a single overlap ghost's resources (materials, FX, GameObject).
        /// Does NOT remove from any collection — caller handles that.
        /// </summary>
        private void DestroyOverlapGhostState(GhostPlaybackState state)
        {
            if (state == null) return;
            ParsekLog.Verbose("Flight",
                $"Destroying overlap ghost cycle={state.loopCycleIndex}");

            if (state.materials != null)
            {
                for (int i = 0; i < state.materials.Count; i++)
                    if (state.materials[i] != null)
                        Destroy(state.materials[i]);
            }

            if (state.engineInfos != null)
                foreach (var info in state.engineInfos.Values)
                    for (int i = 0; i < info.particleSystems.Count; i++)
                        if (info.particleSystems[i] != null)
                            Destroy(info.particleSystems[i].gameObject);

            if (state.rcsInfos != null)
                foreach (var info in state.rcsInfos.Values)
                    for (int i = 0; i < info.particleSystems.Count; i++)
                        if (info.particleSystems[i] != null)
                            Destroy(info.particleSystems[i].gameObject);

            DestroyReentryFxResources(state.reentryFxInfo);
            if (state.ghost != null) Destroy(state.ghost);
            GhostPlaybackLogic.DestroyAllFakeCanopies(state);
            GhostPlaybackLogic.DestroyAllFlagGhosts(state);
        }

        /// <summary>
        /// Destroys all overlap ghosts for a single recording index.
        /// </summary>
        private void DestroyAllOverlapGhosts(int recIdx)
        {
            List<GhostPlaybackState> list;
            if (!overlapGhosts.TryGetValue(recIdx, out list)) return;
            if (list.Count > 0)
                ParsekLog.Verbose("Flight",
                    $"Destroying all {list.Count} overlap ghost(s) for recording #{recIdx}");

            for (int i = 0; i < list.Count; i++)
                DestroyOverlapGhostState(list[i]);
            list.Clear();

            // If camera was following an overlap cycle for this recording, reset tracking
            // so we don't get stuck in the hold state for a destroyed ghost
            if (watchedRecordingIndex == recIdx && watchedOverlapCycleIndex != -1)
            {
                ParsekLog.Info("CameraFollow",
                    $"Overlap ghosts destroyed for watched recording #{recIdx} — resetting overlap cycle tracking from {watchedOverlapCycleIndex}");
                watchedOverlapCycleIndex = -1;
                overlapRetargetAfterUT = -1;
            }
        }

        public bool CanDeleteRecording =>
            !IsRecording && continuationRecordingIdx < 0 && undockContinuationRecIdx < 0;

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
                    $"continuationRecordingIdx={continuationRecordingIdx}, undockContinuationRecIdx={undockContinuationRecIdx}");
                return;
            }

            var rec = committed[index];
            Log($"Deleting recording '{rec.VesselName}' at index {index}");

            // Unreserve crew
            ParsekScenario.UnreserveCrewInSnapshot(rec.VesselSnapshot);

            // Destroy ghost if active (primary + overlap)
            DestroyAllOverlapGhosts(index);
            DestroyTimelineGhost(index);

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

            // Rebuild ghost state keys — indices above the removed one shift down by 1
            var oldStates = new Dictionary<int, GhostPlaybackState>(ghostStates);
            ghostStates.Clear();
            foreach (var kvp in oldStates)
            {
                if (kvp.Key < index)
                    ghostStates[kvp.Key] = kvp.Value;
                else if (kvp.Key > index)
                    ghostStates[kvp.Key - 1] = kvp.Value;
                // kvp.Key == index was already removed by DestroyTimelineGhost
            }

            // Reindex overlap ghosts the same way
            var oldOverlap = new Dictionary<int, List<GhostPlaybackState>>(overlapGhosts);
            overlapGhosts.Clear();
            foreach (var kvp in oldOverlap)
            {
                if (kvp.Key < index)
                    overlapGhosts[kvp.Key] = kvp.Value;
                else if (kvp.Key > index)
                    overlapGhosts[kvp.Key - 1] = kvp.Value;
                // kvp.Key == index was already destroyed by DestroyAllOverlapGhosts
            }

            // Reindex loop phase offsets the same way
            var oldPhaseOffsets = new Dictionary<int, double>(loopPhaseOffsets);
            loopPhaseOffsets.Clear();
            foreach (var kvp in oldPhaseOffsets)
            {
                if (kvp.Key < index)
                    loopPhaseOffsets[kvp.Key] = kvp.Value;
                else if (kvp.Key > index)
                    loopPhaseOffsets[kvp.Key - 1] = kvp.Value;
            }

            // Clear orbit cache — keys are index-derived (i * 10000 + segIdx),
            // so after reindexing they would map to wrong recordings
            orbitCache.Clear();

            // Clear diagnostic guards since indices shifted
            loggedGhostEnter.Clear();
            loggedOrbitSegments.Clear();
            loggedOrbitRotationSegments.Clear();
            loggedReshow.Clear();
            pendingSpawnRecordingIds.Remove(rec.RecordingId);

            ParsekLog.ScreenMessage($"Recording '{rec.VesselName}' deleted", 2f);
        }

        private void DriveReentryToZero(ReentryFxInfo info, int recIdx, string bodyName, double altitude, string vesselName,
            GhostPlaybackState state = null)
        {
            DriveReentryLayers(info, 0f, Vector3.zero, recIdx, bodyName, altitude, 0f, vesselName, state);
            info.lastIntensity = 0f;
        }

        private void UpdateReentryFx(int recIdx, GhostPlaybackState state, string vesselName)
        {
            var info = state.reentryFxInfo;
            if (info == null || state.ghost == null) return;

            if (GhostPlaybackLogic.ShouldSuppressVisualFx(TimeWarp.CurrentRate))
            {
                DriveReentryToZero(info, recIdx, state.lastInterpolatedBodyName,
                    state.lastInterpolatedAltitude, vesselName);
                return;
            }

            Vector3 interpolatedVel = state.lastInterpolatedVelocity;
            string bodyName = state.lastInterpolatedBodyName;
            double altitude = state.lastInterpolatedAltitude;

            if (string.IsNullOrEmpty(bodyName))
            {
                DriveReentryToZero(info, recIdx, bodyName, 0.0, vesselName, state);
                return;
            }

            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
            if (body == null)
            {
                ParsekLog.VerboseRateLimited("Flight", $"ghost-{recIdx}-nobody",
                    $"ReentryFx: body '{bodyName}' not found — skipping");
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            Vector3 surfaceVel = interpolatedVel - (Vector3)body.getRFrmVel(state.ghost.transform.position);
            float speed = surfaceVel.magnitude;

            if (!body.atmosphere)
            {
                ParsekLog.VerboseRateLimited("Flight", $"ghost-{recIdx}-noatmo",
                    $"ReentryFx: body {bodyName} has no atmosphere — skipping");
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            if (altitude >= body.atmosphereDepth)
            {
                ParsekLog.VerboseRateLimited("Flight", $"ghost-{recIdx}-aboveatmo",
                    $"ReentryFx: altitude {altitude:F0} above atmosphereDepth {body.atmosphereDepth:F0} — skipping");
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            double pressure = body.GetPressure(altitude);
            double temperature = body.GetTemperature(altitude);
            if (double.IsNaN(pressure) || pressure < 0 || double.IsNaN(temperature) || temperature < 0)
            {
                ParsekLog.VerboseRateLimited("Flight", $"ghost-{recIdx}-badatmo",
                    $"ReentryFx: GetPressure/GetTemperature returned invalid value for ghost #{recIdx} — density fallback to 0");
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            double density = body.GetDensity(pressure, temperature);
            if (double.IsNaN(density) || density < 0)
            {
                ParsekLog.VerboseRateLimited("Flight", $"ghost-{recIdx}-baddensity",
                    $"ReentryFx: GetDensity returned invalid value for ghost #{recIdx} — density fallback to 0");
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            // Compute Mach number for aeroFX threshold matching
            double speedOfSound = body.GetSpeedOfSound(pressure, density);
            float machNumber = (speedOfSound > 0) ? (float)(speed / speedOfSound) : 0f;

            float rawIntensity = GhostVisualBuilder.ComputeReentryIntensity(speed, (float)density, machNumber);

            float smoothedIntensity = Mathf.Lerp(info.lastIntensity, rawIntensity,
                1f - Mathf.Exp(-GhostVisualBuilder.ReentrySmoothingRate * Time.deltaTime));
            if (smoothedIntensity < 0.001f && rawIntensity == 0f)
                smoothedIntensity = 0f;

            DriveReentryLayers(info, smoothedIntensity, surfaceVel, recIdx, bodyName, altitude, machNumber, vesselName, state);
            info.lastIntensity = smoothedIntensity;
        }

        private void DriveReentryLayers(ReentryFxInfo info, float intensity, Vector3 surfaceVel,
            int recIdx, string bodyName, double altitude, float machNumber, string vesselName,
            GhostPlaybackState state = null)
        {
            bool wasActive = info.lastIntensity > 0f;
            bool isActive = intensity > 0f;

            if (isActive && !wasActive)
            {
                float speed = surfaceVel.magnitude;
                ParsekLog.Verbose("Flight",
                    $"ReentryFx: Activated for ghost #{recIdx} \"{vesselName}\" — intensity={intensity:F2}, Mach={machNumber:F2}, speed={speed:F0} m/s, alt={altitude:F0} m, body={bodyName}");
            }
            else if (!isActive && wasActive)
            {
                float speed = surfaceVel.magnitude;
                ParsekLog.Verbose("Flight",
                    $"ReentryFx: Deactivated for ghost #{recIdx} — intensity dropped to 0 (speed={speed:F0} m/s, alt={altitude:F0} m)");
            }

            if (isActive)
            {
                ParsekLog.VerboseRateLimited("Flight", $"ghost-{recIdx}-intensity",
                    $"ReentryFx: ghost #{recIdx} intensity={intensity:F2} speed={surfaceVel.magnitude:F0} alt={altitude:F0}");
            }

            // Layer A: Heat glow (material emission)
            if (info.glowMaterials != null)
            {
                for (int i = 0; i < info.glowMaterials.Count; i++)
                {
                    HeatMaterialState ms = info.glowMaterials[i];
                    if (ms.material == null) continue;

                    if (intensity <= GhostVisualBuilder.ReentryLayerAThreshold)
                    {
                        if (!string.IsNullOrEmpty(ms.emissiveProperty))
                            ms.material.SetColor(ms.emissiveProperty, ms.coldEmission);
                        if (!string.IsNullOrEmpty(ms.colorProperty))
                            ms.material.SetColor(ms.colorProperty, ms.coldColor);
                    }
                    else
                    {
                        float glowFraction = Mathf.InverseLerp(GhostVisualBuilder.ReentryLayerAThreshold, 1f, intensity);
                        Color targetEmission = Color.Lerp(GhostVisualBuilder.ReentryHotEmissionLow,
                            GhostVisualBuilder.ReentryHotEmissionHigh, glowFraction);
                        if (!string.IsNullOrEmpty(ms.emissiveProperty))
                            ms.material.SetColor(ms.emissiveProperty,
                                Color.Lerp(ms.coldEmission, ms.coldEmission + targetEmission, glowFraction));
                        if (!string.IsNullOrEmpty(ms.colorProperty))
                            ms.material.SetColor(ms.colorProperty,
                                Color.Lerp(ms.coldColor, ms.hotColor, glowFraction));
                    }
                }
            }

            // Apply ablation char to heat shield parts (Pattern B: _BurnColor)
            if (state != null)
                GhostPlaybackLogic.ApplyColorChangerCharState(state, intensity);

            // Fire envelope particles
            if (info.fireParticles != null)
            {
                if (intensity > GhostVisualBuilder.ReentryFireThreshold)
                {
                    float fireFraction = Mathf.InverseLerp(GhostVisualBuilder.ReentryFireThreshold, 1f, intensity);

                    var emissionMod = info.fireParticles.emission;
                    emissionMod.rateOverTimeMultiplier = Mathf.Lerp(
                        GhostVisualBuilder.ReentryFireEmissionMin,
                        GhostVisualBuilder.ReentryFireEmissionMax, fireFraction);

                    var mainMod = info.fireParticles.main;
                    mainMod.startSizeMultiplier = Mathf.Lerp(0.8f, 2.0f, fireFraction);

                    if (!info.fireParticles.isPlaying)
                        info.fireParticles.Play();
                }
                else
                {
                    if (info.fireParticles.isPlaying)
                    {
                        info.fireParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                    }
                }
            }

            // Fire shell overlay: re-draw ghost meshes offset along velocity with additive blending
            // Approximates KSP's FXCamera replacement shader that displaces vertex positions
            // along the airflow direction, creating the characteristic turbulent flame shell.
            if (info.fireShellMeshes != null && info.fireShellMaterial != null
                && intensity > GhostVisualBuilder.ReentryFireThreshold)
            {
                Vector3 velDir = surfaceVel.normalized;
                float maxOffset = info.vesselLength * GhostVisualBuilder.ReentryFireShellMaxOffset * intensity;
                float baseTint = Mathf.Lerp(0.026f, 0.156f, intensity);

                for (int pass = 0; pass < GhostVisualBuilder.ReentryFireShellPasses; pass++)
                {
                    float t = (pass + 1f) / GhostVisualBuilder.ReentryFireShellPasses;
                    Vector3 offset = -velDir * (maxOffset * t);
                    float alpha = baseTint * (1f - t * 0.7f); // closer passes are brighter
                    Color tint = GhostVisualBuilder.ReentryFireShellColor * alpha;

                    var mpb = state.reentryMpb ?? new MaterialPropertyBlock();
                    mpb.SetColor("_TintColor", tint);

                    for (int m = 0; m < info.fireShellMeshes.Count; m++)
                    {
                        FireShellMesh fsm = info.fireShellMeshes[m];
                        if (fsm.mesh == null || fsm.transform == null) continue;
                        // Skip meshes on decoupled/destroyed parts (SetActive(false))
                        if (!fsm.transform.gameObject.activeInHierarchy) continue;

                        Matrix4x4 matrix = Matrix4x4.Translate(offset) * fsm.transform.localToWorldMatrix;
                        Graphics.DrawMesh(fsm.mesh, matrix, info.fireShellMaterial, 0, null, 0, mpb);
                    }
                }
            }
        }

        private void DestroyReentryFxResources(ReentryFxInfo info)
        {
            if (info == null) return;
            if (info.allClonedMaterials != null)
                for (int i = 0; i < info.allClonedMaterials.Count; i++)
                    if (info.allClonedMaterials[i] != null)
                        Destroy(info.allClonedMaterials[i]);
            if (info.generatedTexture != null)
                Destroy(info.generatedTexture);
            if (info.combinedEmissionMesh != null)
                Destroy(info.combinedEmissionMesh);
        }

        /// <summary>
        /// Drive the camera pivot position every frame during watch mode.
        /// pivotTranslateSharpness is zeroed for the entire watch session to prevent
        /// KSP from pulling the camera back toward the active vessel.
        /// </summary>
        void UpdateWatchCamera()
        {
            if (watchedRecordingIndex < 0) return;

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
            if (watchedOverlapCycleIndex == -2 && overlapRetargetAfterUT > 0)
            {
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
                }
                else
                {
                    // During hold, keep camera where it is (don't update position)
                    if (FlightCamera.fetch != null)
                        FlightCamera.fetch.pivotTranslateSharpness = 0f;
                    watchNoTargetFrames = 0;
                    return;
                }
            }

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

        bool IsAnyWarpActive()
        {
            // CurrentRateIndex > 0 covers both rails warp and physics warp.
            return GhostPlaybackLogic.IsAnyWarpActive(TimeWarp.CurrentRateIndex, TimeWarp.CurrentRate);
        }

        void ExitAllWarpForPlaybackStart(string context)
        {
            if (!IsAnyWarpActive()) return;
            TimeWarp.SetRate(0, true);
            Log($"Warp reset for playback start ({context})");
        }

        #endregion

        #region Camera Follow (Watch Mode)

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
        /// Returns true if the ghost at index is on the same celestial body as the active vessel.
        /// </summary>
        internal bool IsGhostOnSameBody(int index)
        {
            GhostPlaybackState s;
            if (!ghostStates.TryGetValue(index, out s) || s == null) return false;
            string ghostBody = s.lastInterpolatedBodyName;
            string activeBody = FlightGlobals.ActiveVessel?.mainBody?.name;
            if (string.IsNullOrEmpty(ghostBody) || string.IsNullOrEmpty(activeBody)) return false;
            return ghostBody == activeBody;
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

            float boxW = 300f, boxH = 50f;
            float x = (Screen.width - boxW) / 2f;
            float y = 10f;
            Rect bgRect = new Rect(x, y, boxW, boxH);

            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(bgRect, Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUI.Label(new Rect(x, y + 5, boxW, 22f), "Watching: " + vesselName, watchOverlayStyle);
            GUI.Label(new Rect(x, y + 27, boxW, 18f), "[Backspace] Return to vessel", watchOverlayHintStyle);
        }

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
                if (cycleDuration <= MinLoopDurationSeconds)
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
            watchEndHoldUntilUT = -1;
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
            watchEndHoldUntilUT = -1;
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
            var committed = RecordingStore.CommittedRecordings;

            // Case 1: Chain continuation
            if (!string.IsNullOrEmpty(currentRec.ChainId) && currentRec.ChainIndex >= 0
                && currentRec.ChainBranch == 0)
            {
                int nextChainIndex = currentRec.ChainIndex + 1;
                for (int j = 0; j < committed.Count; j++)
                {
                    var candidate = committed[j];
                    if (candidate.ChainId == currentRec.ChainId
                        && candidate.ChainBranch == 0
                        && candidate.ChainIndex == nextChainIndex
                        && HasActiveGhost(j))
                    {
                        return j;
                    }
                }
            }

            // Case 2: Tree branching via ChildBranchPointId
            if (!string.IsNullOrEmpty(currentRec.ChildBranchPointId)
                && !string.IsNullOrEmpty(currentRec.TreeId))
            {
                // Find the BranchPoint
                BranchPoint bp = null;
                for (int t = 0; t < RecordingStore.CommittedTrees.Count; t++)
                {
                    var tree = RecordingStore.CommittedTrees[t];
                    if (tree.Id != currentRec.TreeId) continue;
                    for (int b = 0; b < tree.BranchPoints.Count; b++)
                    {
                        if (tree.BranchPoints[b].Id == currentRec.ChildBranchPointId)
                        {
                            bp = tree.BranchPoints[b];
                            break;
                        }
                    }
                    break;
                }

                if (bp != null)
                {
                    // Prefer child with same VesselPersistentId (same vessel continues)
                    int fallbackIdx = -1;
                    for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                    {
                        string childId = bp.ChildRecordingIds[c];
                        for (int j = 0; j < committed.Count; j++)
                        {
                            if (committed[j].RecordingId != childId) continue;
                            if (!HasActiveGhost(j)) continue;

                            if (committed[j].VesselPersistentId == currentRec.VesselPersistentId)
                                return j; // same vessel — best match

                            if (fallbackIdx < 0)
                                fallbackIdx = j; // first active child as fallback
                        }
                    }
                    if (fallbackIdx >= 0)
                        return fallbackIdx;
                }
            }

            return -1;
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

            watchEndHoldUntilUT = -1;

            ParsekLog.Info("CameraFollow",
                $"TransferWatch re-target: ghost #{nextIndex} \"{newName}\"" +
                $" target='{segTarget.name}' pivotLocal=({segTarget.localPosition.x:F2},{segTarget.localPosition.y:F2},{segTarget.localPosition.z:F2})" +
                $" ghostPos=({gs.ghost.transform.position.x:F1},{gs.ghost.transform.position.y:F1},{gs.ghost.transform.position.z:F1})" +
                $" camDist={FlightCamera.fetch.Distance:F1}");
        }

        #endregion

        IEnumerator DeferredActivateVessel(uint pid)
        {
            for (int frame = 0; frame < 10; frame++)
            {
                yield return null;
                Vessel v = FlightGlobals.Vessels?.Find(vessel => vessel.persistentId == pid);
                if (v != null && v.loaded)
                {
                    v.IgnoreGForces(240);
                    FlightGlobals.ForceSetActiveVessel(v);
                    Log($"Activated vessel pid={pid}");
                    yield break;
                }
            }
            Log($"WARNING: Could not activate vessel pid={pid} within 10 frames");
        }

        #region Zone Rendering (shared by normal, background, and looped ghosts)

        /// <summary>
        /// Result of zone rendering evaluation for a ghost.
        /// </summary>
        struct ZoneRenderingResult
        {
            public bool hiddenByZone;       // Ghost was hidden (Beyond zone) — caller should skip/continue
            public bool skipPartEvents;     // Part events should not be applied this frame
        }

        /// <summary>
        /// Evaluates zone-based rendering for a ghost: computes distance, classifies zone,
        /// detects transitions, hides/shows mesh, exits watch mode if needed.
        /// Shared by normal, background, and looped ghost update paths.
        /// </summary>
        ZoneRenderingResult ApplyZoneRendering(int recIdx, GhostPlaybackState state, Recording rec, double ghostDistance)
        {
            var zone = RenderingZoneManager.ClassifyDistance(ghostDistance);
            bool isWatchedGhost = watchedRecordingIndex == recIdx;

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

            // Beyond zone: hide mesh for non-watched ghosts.
            // Watched ghost gets a grace period: when Watch starts, the ghost may be near
            // the player. As it flies away and crosses ~120km from the active vessel,
            // terrain unloads and jitter occurs. Exit Watch after a 2s grace period so
            // the camera has time to lock on before zone checks kick in. This also avoids
            // the original bug where Watch on a chain segment already beyond 120km would
            // exit instantly before the player saw anything.
            if (shouldHideMesh)
            {
                if (isWatchedGhost)
                {
                    if (Time.time - watchStartTime > GhostPlaybackLogic.WatchModeZoneGraceSeconds)
                    {
                        ExitWatchMode();
                        ParsekLog.Info("Zone",
                            $"Watch exited: ghost #{recIdx} exceeded visual range " +
                            $"({ghostDistance.ToString("F0", CultureInfo.InvariantCulture)}m)");
                        // Fall through to hide the ghost normally
                    }
                    else
                    {
                        // Grace period: keep watching, don't hide
                        ParsekLog.VerboseRateLimited("Zone", $"watched-zone-{recIdx}",
                            $"Ghost #{recIdx} \"{rec.VesselName}\" beyond visual range " +
                            $"({ghostDistance.ToString("F0", CultureInfo.InvariantCulture)}m) but watched (grace period) — keeping visible",
                            5.0);
                        return new ZoneRenderingResult { hiddenByZone = false, skipPartEvents = shouldSkipPartEvents };
                    }
                }

                if (state != null && state.ghost != null && state.ghost.activeSelf)
                {
                    state.ghost.SetActive(false);
                    ParsekLog.Info("Zone",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" hidden: beyond visual range " +
                        $"({ghostDistance.ToString("F0", CultureInfo.InvariantCulture)}m)");
                }
                return new ZoneRenderingResult { hiddenByZone = true, skipPartEvents = true };
            }

            // Zone 1 or 2: ensure ghost is visible
            if (state != null && state.ghost != null && !state.ghost.activeSelf)
            {
                state.ghost.SetActive(true);
                ParsekLog.Info("Zone",
                    $"Ghost #{recIdx} \"{rec.VesselName}\" re-shown: entered visual range " +
                    $"({ghostDistance.ToString("F0", CultureInfo.InvariantCulture)}m)");
            }

            return new ZoneRenderingResult { hiddenByZone = false, skipPartEvents = shouldSkipPartEvents };
        }

        #endregion

        #region Ghost Positioning (shared by manual + timeline playback)

        GameObject CreateGhostSphere(string name, Color color)
        {
            GameObject ghost = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ghost.name = name;
            ghost.transform.localScale = Vector3.one * 12f; // 12m diameter

            Collider collider = ghost.GetComponent<Collider>();
            if (collider != null)
                collider.enabled = false;

            Renderer renderer = ghost.GetComponent<Renderer>();
            if (renderer != null)
            {
                Shader shader = Shader.Find("KSP/Emissive/Diffuse");
                if (shader != null)
                {
                    Material mat = new Material(shader);
                    mat.color = color;
                    mat.SetColor("_EmissiveColor", color);
                    renderer.material = mat;
                }
                else
                {
                    Log("Warning: Could not find KSP/Emissive/Diffuse shader, using default");
                }
            }

            return ghost;
        }

        void InterpolateAndPosition(GameObject ghost, List<TrajectoryPoint> points, ref int cachedIndex, double targetUT, out InterpolationResult interpResult, bool surfaceRelativeRotation = false)
        {
            if (points == null || points.Count == 0) { interpResult = InterpolationResult.Zero; ghost.SetActive(false); return; }

            int indexBefore = TrajectoryMath.FindWaypointIndex(points, ref cachedIndex, targetUT);

            if (indexBefore < 0)
            {
                PositionGhostAt(ghost, points[0], surfaceRelativeRotation);
                interpResult = new InterpolationResult(points[0].velocity, points[0].bodyName, points[0].altitude);
                return;
            }

            TrajectoryPoint before = points[indexBefore];
            TrajectoryPoint after = points[indexBefore + 1];

            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                PositionGhostAt(ghost, before, surfaceRelativeRotation);
                interpResult = new InterpolationResult(before.velocity, before.bodyName, before.altitude);
                return;
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

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

            interpolatedRot = SanitizeQuaternion(interpolatedRot);

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

            Quaternion sanitized = SanitizeQuaternion(point.rotation);
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
            Quaternion ghostRot = ghost.transform.rotation; // preserve current if no update
            Quaternion boundaryWorldRot = Quaternion.identity;
            bool hasOfr = TrajectoryMath.HasOrbitalFrameRotation(segment);
            bool spinning = TrajectoryMath.IsSpinning(segment);

            if (spinning)
            {
                // Spin-forward path: reconstruct boundary world rotation from orbit at startUT
                Vector3d velAtStart = orbit.getOrbitalVelocityAtUT(segment.startUT);
                Vector3d posAtStart = orbit.getPositionAtUT(segment.startUT);
                Vector3d radialAtStart = (posAtStart - (Vector3d)body.position).normalized;

                Quaternion orbFrameAtStart;
                if (Mathf.Abs(Vector3.Dot(((Vector3)velAtStart).normalized, ((Vector3)radialAtStart).normalized)) > 0.99f)
                {
                    orbFrameAtStart = Quaternion.LookRotation(velAtStart, Vector3.up);
                    ParsekLog.VerboseRateLimited("Playback", $"orbit-near-parallel-start-{cacheKey}",
                        $"Orbit segment {cacheKey}: velocity/radialOut near-parallel at startUT, LookRotation fallback");
                }
                else
                    orbFrameAtStart = Quaternion.LookRotation(velAtStart, radialAtStart);

                boundaryWorldRot = orbFrameAtStart * segment.orbitalFrameRotation;

                double dt = ut - segment.startUT;
                Vector3 worldAxis = boundaryWorldRot * segment.angularVelocity;
                float angle = (float)((double)segment.angularVelocity.magnitude * dt * Mathf.Rad2Deg);
                ghostRot = Quaternion.AngleAxis(angle, worldAxis) * boundaryWorldRot;
            }
            else if (hasOfr && velocity.sqrMagnitude > 0.001)
            {
                // Orbital-frame-relative path
                Vector3d radialOut = (worldPos - (Vector3d)body.position).normalized;

                Quaternion orbFrame;
                if (Mathf.Abs(Vector3.Dot(((Vector3)velocity).normalized, ((Vector3)radialOut).normalized)) > 0.99f)
                {
                    orbFrame = Quaternion.LookRotation(velocity, Vector3.up);
                    ParsekLog.VerboseRateLimited("Playback", $"orbit-near-parallel-{cacheKey}",
                        $"Orbit segment {cacheKey}: velocity/radialOut near-parallel, LookRotation fallback");
                }
                else
                    orbFrame = Quaternion.LookRotation(velocity, radialOut);

                ghostRot = orbFrame * segment.orbitalFrameRotation;
            }
            else if (velocity.sqrMagnitude > 0.001)
            {
                // Prograde fallback (old recordings)
                ghostRot = Quaternion.LookRotation(velocity);
            }

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
            Recording rec,
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
            interpolatedRot = SanitizeQuaternion(interpolatedRot);

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
                // Anchor not found — hide ghost entirely.
                // RELATIVE frames store dx/dy/dz meter offsets in lat/lon/alt fields,
                // so interpreting them as geographic coordinates would place the ghost
                // at completely wrong positions. Hiding is the safe fallback.
                long key = ((long)anchorVesselId << 32);
                if (loggedAnchorNotFound.Add(key))
                    ParsekLog.Warn("Anchor",
                        $"RELATIVE playback: anchor vessel pid={anchorVesselId} not found, " +
                        $"hiding ghost until anchor loads");

                interpResult = InterpolationResult.Zero;
                ghost.SetActive(false);
            }
        }

        /// <summary>
        /// Positions a ghost at a single RELATIVE frame point (no interpolation).
        /// Used for edge cases (before first frame, zero-duration segments).
        /// </summary>
        void PositionGhostRelativeAt(GameObject ghost, TrajectoryPoint point, uint anchorVesselId)
        {
            if (!ghost.activeSelf) ghost.SetActive(true);

            Quaternion sanitized = SanitizeQuaternion(point.rotation);
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
                // Anchor not found — hide ghost entirely.
                // RELATIVE frames store dx/dy/dz meter offsets in lat/lon/alt fields,
                // so interpreting them as geographic coordinates would place the ghost
                // at completely wrong positions. Hiding is the safe fallback.
                long key = ((long)anchorVesselId << 32);
                if (loggedAnchorNotFound.Add(key))
                    ParsekLog.Warn("Anchor",
                        $"PositionGhostRelativeAt: anchor vessel pid={anchorVesselId} not found, " +
                        $"hiding ghost until anchor loads");
                ghost.SetActive(false);
            }
        }

        void InterpolateAndPosition(GameObject ghost, List<TrajectoryPoint> points,
            List<OrbitSegment> segments, ref int cachedIndex, double targetUT, int orbitCacheBase,
            out InterpolationResult interpResult, bool surfaceRelativeRotation = false)
        {
            // Check orbit segments first
            if (segments != null && segments.Count > 0)
            {
                OrbitSegment? seg = FindOrbitSegment(segments, targetUT);
                if (seg.HasValue)
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
                    CelestialBody segBody = FlightGlobals.Bodies?.Find(b => b.name == seg.Value.bodyName);
                    double segAlt = segBody != null ? segBody.GetAltitude(ghost.transform.position) : 0;
                    interpResult = new InterpolationResult(vel, seg.Value.bodyName, segAlt);
                    return;
                }
            }

            // Fall through to point-based interpolation
            InterpolateAndPosition(ghost, points, ref cachedIndex, targetUT, out interpResult, surfaceRelativeRotation);
        }

        // Delegates to TrajectoryMath — kept for backward compatibility
        internal Quaternion SanitizeQuaternion(Quaternion q)
        {
            return TrajectoryMath.SanitizeQuaternion(q);
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
                RecordingStore.CommitPending();
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
                    vesselName, chain.SpawnUT, chain.IsTerminated, chain.SpawnBlocked);

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

        void Log(string message) => ParsekLog.Verbose("Flight", message);
        void ScreenMessage(string message, float duration) => ParsekLog.ScreenMessage(message, duration);

        #endregion
    }
}
