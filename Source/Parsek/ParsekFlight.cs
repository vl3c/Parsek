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

        // Manual playback state (F10/F11 preview of current recording)
        private bool isPlaying = false;
        private double playbackStartUT;
        private double recordingStartUT;
        private GameObject ghostObject;
        private List<Material> previewGhostMaterials = new List<Material>();
        internal int lastPlaybackIndex = 0;

        // Per-ghost playback state (bundled to prevent dict-sync bugs)
        private class LightPlaybackState
        {
            public bool isOn;
            public bool blinkEnabled;
            public float blinkRateHz = 1f;
        }

        private class GhostPlaybackState
        {
            public GameObject ghost;
            public List<Material> materials;
            public int playbackIndex;
            public int partEventIndex;
            public int loopCycleIndex = -1;
            public Dictionary<uint, List<uint>> partTree;
            public Dictionary<uint, ParachuteGhostInfo> parachuteInfos;
            public Dictionary<uint, JettisonGhostInfo> jettisonInfos;
            public Dictionary<ulong, EngineGhostInfo> engineInfos; // key = EncodeEngineKey(pid, moduleIndex)
            public Dictionary<ulong, RcsGhostInfo> rcsInfos;   // separate from engineInfos — keys can overlap for same part
            public Dictionary<ulong, RoboticGhostInfo> roboticInfos; // key = EncodeEngineKey(pid, moduleIndex)
            public Dictionary<uint, DeployableGhostInfo> deployableInfos;
            public Dictionary<uint, HeatGhostInfo> heatInfos;
            public Dictionary<uint, LightGhostInfo> lightInfos;
            public Dictionary<uint, LightPlaybackState> lightPlaybackStates;
            public Dictionary<uint, FairingGhostInfo> fairingInfos;
            public Dictionary<uint, GameObject> fakeCanopies;
        }

        private Dictionary<int, GhostPlaybackState> ghostStates = new Dictionary<int, GhostPlaybackState>();

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

        // Timeline warp protection — tracks previous frame's UT
        private double lastTimelineUT = -1;

        // Diagnostic logging guards (log once per state transition, not per frame)
        private HashSet<int> loggedGhostEnter = new HashSet<int>();
        private HashSet<int> loggedOrbitSegments = new HashSet<int>();

        // Warp-stop guard: only stop time warp once per recording
        private HashSet<int> warpStoppedForRecording = new HashSet<int>();
        private bool timelineResourceReplayPausedLogged = false;
        private const double DefaultLoopPauseSeconds = 10.0;
        private const double MinLoopDurationSeconds = 0.001;

        // UI
        private Rect windowRect = new Rect(20, 100, 250, 250);
        private bool showUI = false;
        private ToolbarControl toolbarControl;
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

        #endregion

        #region Unity Lifecycle

        void Start()
        {
            Log("Parsek Flight loaded.");
            Log("Press F9 to record, F10 to preview playback.");

            GameEvents.onGameSceneLoadRequested.Add(OnSceneChangeRequested);
            GameEvents.onFlightReady.Add(OnFlightReady);
            GameEvents.onVesselWillDestroy.Add(OnVesselWillDestroy);
            GameEvents.onVesselSituationChange.Add(OnVesselSituationChange);
            GameEvents.onCrewOnEva.Add(OnCrewOnEva);
            GameEvents.onCrewBoardVessel.Add(OnCrewBoardVessel);
            GameEvents.onVesselGoOnRails.Add(OnVesselGoOnRails);
            GameEvents.onVesselGoOffRails.Add(OnVesselGoOffRails);
            GameEvents.onVesselSOIChanged.Add(OnVesselSOIChanged);
            GameEvents.onPartCouple.Add(OnPartCouple);
            GameEvents.onPartUndock.Add(OnPartUndock);
            GameEvents.onGroundSciencePartDeployed.Add(OnGroundSciencePartDeployed);
            GameEvents.onGroundSciencePartRemoved.Add(OnGroundSciencePartRemoved);

            ui = new ParsekUI(this);

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
            // Auto-clear stale boarding confirmation after 3 frames
            if (pendingBoardingTargetPid != 0)
            {
                boardingConfirmFrames++;
                if (boardingConfirmFrames > 3)
                {
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
                    pendingDockMergedPid = 0;
                    pendingDockAsTarget = false;
                    dockConfirmFrames = 0;
                }
            }
            if (pendingUndockOtherPid != 0)
            {
                undockConfirmFrames++;
                if (undockConfirmFrames > 5)
                {
                    pendingUndockOtherPid = 0;
                    undockConfirmFrames = 0;
                }
            }

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

            // Chain: auto-commit previous segment when recording stopped (EVA exit)
            // VesselSnapshot nulled in CommitChainSegment — mid-chain segments are ghost-only.
            if (pendingChainContinuation && !pendingChainIsBoarding &&
                recorder != null && !recorder.IsRecording && recorder.CaptureAtStop != null)
            {
                CommitChainSegment(recorder, pendingChainEvaName);
                recorder = null;
                // pendingAutoRecord is still true — will start child EVA recording next
            }

            // Chain: auto-commit EVA segment when boarding detected (EVA→vessel)
            if (recorder != null && recorder.ChainToVesselPending && !recorder.IsRecording && recorder.CaptureAtStop != null)
            {
                // Only continue the chain if we're already in one AND the boarding event confirmed
                if (activeChainId != null &&
                    pendingBoardingTargetPid != 0 &&
                    FlightGlobals.ActiveVessel != null &&
                    FlightGlobals.ActiveVessel.persistentId == pendingBoardingTargetPid)
                {
                    Log($"Chain boarding confirmed: EVA→vessel pid={pendingBoardingTargetPid}");
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
                    Log("ChainToVessel without active chain or boarding confirmation — treating as normal stop");
                    ParsekLog.ScreenMessage("Recording stopped — vessel changed", 3f);
                    // Leave CaptureAtStop intact for normal revert/merge handling
                }
            }

            // Complete deferred auto-record for EVA (vessel switch may take a frame)
            if (pendingAutoRecord && !IsRecording &&
                FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.isEVA)
            {
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
                        ScreenMessage("Recording STARTED (auto — EVA from pad)", 2f);
                    }
                }
                // else: StartRecording failed (paused game or no active vessel).
                // Keep pending flags so we retry next frame; fall through to
                // HandleInput/playback so they aren't starved.
            }

            UpdateContinuationSampling();
            UpdateUndockContinuationSampling();

            HandleInput();

            if (isPlaying)
            {
                UpdatePlayback();
            }

            UpdateTimelinePlayback();
        }

        void OnGUI()
        {
            if (MapView.MapIsEnabled)
                ui.DrawMapMarkers();

            if (showUI)
            {
                windowRect = ClickThruBlocker.GUILayoutWindow(
                    GetInstanceID(),
                    windowRect,
                    ui.DrawWindow,
                    "Parsek",
                    GUILayout.Width(250)
                );
                ui.DrawRecordingsWindowIfOpen(windowRect);
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
            GameEvents.onVesselSOIChanged.Remove(OnVesselSOIChanged);
            GameEvents.onPartCouple.Remove(OnPartCouple);
            GameEvents.onPartUndock.Remove(OnPartUndock);
            GameEvents.onGroundSciencePartDeployed.Remove(OnGroundSciencePartDeployed);
            GameEvents.onGroundSciencePartRemoved.Remove(OnGroundSciencePartRemoved);

            // Clean up recording if active
            if (IsRecording)
            {
                recorder.ForceStop();
            }
            StopPlayback();
            DestroyAllTimelineGhosts();

            ui?.Cleanup();
        }

        #endregion

        #region Scene Change Handling

        void OnSceneChangeRequested(GameScenes scene)
        {
            Log($"Scene change requested: {scene}");

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

            // Clear dock/undock pending state
            pendingDockMergedPid = 0;
            pendingDockAsTarget = false;
            dockConfirmFrames = 0;
            pendingUndockOtherPid = 0;
            undockConfirmFrames = 0;

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
                    ghostGeometryVersion: captured != null ? (int?)captured.GhostGeometryVersion : null,
                    partEvents: recorder.PartEvents);

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
                }

                // Clear chain fields after both paths have consumed them
                activeChainId = null;
                activeChainNextIndex = 0;
                activeChainPrevId = null;
                activeChainCrewName = null;

                recorder = null;
                lastPlaybackIndex = 0;
            }

            // Stop manual playback
            StopPlayback();
            DestroyAllTimelineGhosts();
            GhostVisualBuilder.ClearDeployedCanopyCache();
            GhostVisualBuilder.ClearDeployableCache();
            GhostVisualBuilder.ClearGearCache();
            GhostVisualBuilder.ClearLadderCache();
            GhostVisualBuilder.ClearCargoBayCache();
            GhostVisualBuilder.ClearAnimateHeatCache();
        }

        void OnVesselWillDestroy(Vessel v)
        {
            recorder?.OnVesselWillDestroy(v);

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

        void OnVesselSituationChange(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> data)
        {
            if (IsRecording) return;
            if (data.host != FlightGlobals.ActiveVessel) return;
            if (data.from != Vessel.Situations.PRELAUNCH) return;
            if (ParsekSettings.Current?.autoRecordOnLaunch == false) return;

            StartRecording();
            Log("Auto-record started (vessel left pad/runway)");
            ScreenMessage("Recording STARTED (auto)", 2f);
        }

        void OnCrewBoardVessel(GameEvents.FromToAction<Part, Part> data)
        {
            if (activeChainId == null) return;
            if (data.to?.vessel == null) return;

            pendingBoardingTargetPid = data.to.vessel.persistentId;
            boardingConfirmFrames = 0;
            Log($"onCrewBoardVessel: target vessel pid={pendingBoardingTargetPid}");
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
                ghostGeometryVersion: segmentRecorder.CaptureAtStop.GhostGeometryVersion,
                partEvents: segmentRecorder.PartEvents);

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
                Log($"Chain: started new chain (id={activeChainId})");
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
                rotation = v.transform.rotation,
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
            // Mid-recording EVA: auto-stop parent, start chain child for EVA kerbal
            if (IsRecording)
            {
                // Only trigger chain flow if EVA is from the vessel we're recording
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

                pendingChainContinuation = true;
                pendingChainIsBoarding = false;
                pendingChainEvaName = kerbalName;
                activeChainCrewName = kerbalName;
                pendingAutoRecord = true;
                Log($"Mid-recording EVA detected: '{kerbalName}' — pending chain continuation");
                return;
            }

            if (data.from?.vessel == null) return;
            if (data.from.vessel.situation != Vessel.Situations.PRELAUNCH) return;
            if (ParsekSettings.Current?.autoRecordOnEva == false) return;

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
        }

        void OnVesselGoOffRails(Vessel v)
        {
            recorder?.OnVesselGoOffRails(v);
        }

        void OnVesselSOIChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> data)
        {
            recorder?.OnVesselSOIChanged(data);
        }

        void OnPartCouple(GameEvents.FromToAction<Part, Part> data)
        {
            if (data.to?.vessel == null) return;
            uint mergedPid = data.to.vessel.persistentId;

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
            if (undockedPart?.vessel == null) return;

            uint newPid = undockedPart.vessel.persistentId;
            if (newPid != recorder.RecordingVesselId)
            {
                // Something undocked FROM us — we stay, other vessel splits off
                pendingUndockOtherPid = newPid;
                undockConfirmFrames = 0;

                // Stop recording silently — Update() will commit and restart
                recorder.StopRecordingForChainBoundary();
                Log($"onPartUndock: vessel split off (otherPid={newPid})");
            }
            // else: undocked part still on our vessel (transient state) — ignore.
            // If the player follows the undocked vessel, OnPhysicsFrame detects
            // the pid mismatch and UndockSiblingPid handles it.
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
                ghostGeometryVersion: segmentRecorder.CaptureAtStop.GhostGeometryVersion,
                partEvents: segmentRecorder.PartEvents);

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
                Log($"Dock/Undock chain: started new chain (id={activeChainId})");
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
                rotation = otherVessel.transform.rotation,
                velocity = velocity,
                bodyName = otherVessel.mainBody.name
            };

            var contRec = new RecordingStore.Recording
            {
                VesselName = otherVessel.vesselName + " (undock continuation)",
                ChainId = activeChainId,
                ChainIndex = activeChainNextIndex - 1, // same index as the player's new segment
                ChainBranch = 1, // parallel branch — ghost-only, never spawns
                GhostVisualSnapshot = ghostSnapshot,
                RecordingId = System.Guid.NewGuid().ToString("N"),
                RecordingFormatVersion = 4
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
                rotation = v.transform.rotation,
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
                    MergeDialog.Show(pending);
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
                        Log($"EVA child recording has no matching parent — showing merge dialog");
                        MergeDialog.Show(pending);
                    }
                }
                else
                {
                    Log($"Found pending recording from {pending.VesselName} ({pending.Points.Count} points)");
                    MergeDialog.Show(pending);
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

        #region Input Handling

        void HandleInput()
        {
            // F9 - Toggle recording
            if (Input.GetKeyDown(KeyCode.F9))
            {
                if (!IsRecording)
                    StartRecording();
                else
                    StopRecording();
            }

            // F10 - Start manual playback (preview current recording)
            if (Input.GetKeyDown(KeyCode.F10))
            {
                if (!IsRecording && recording.Count > 0 && !isPlaying)
                {
                    StartPlayback();
                }
            }

            // F11 - Stop manual playback
            if (Input.GetKeyDown(KeyCode.F11))
            {
                if (isPlaying)
                {
                    StopPlayback();
                }
            }

        }

        #endregion

        #region Recording

        public void StartRecording()
        {
            recorder = new FlightRecorder();
            if (pendingBoundaryAnchor.HasValue)
            {
                recorder.BoundaryAnchor = pendingBoundaryAnchor;
                pendingBoundaryAnchor = null;
            }
            recorder.StartRecording();
        }

        public void StopRecording()
        {
            recorder?.StopRecording();

            // Tag the final segment with chain metadata if in a chain
            if (recorder?.CaptureAtStop != null && activeChainId != null)
            {
                recorder.CaptureAtStop.ChainId = activeChainId;
                recorder.CaptureAtStop.ChainIndex = activeChainNextIndex;
                recorder.CaptureAtStop.ParentRecordingId = activeChainPrevId;
                recorder.CaptureAtStop.EvaCrewName = activeChainCrewName;
            }
        }

        public void ClearRecording()
        {
            recorder = null;
            lastPlaybackIndex = 0;
            Log("Recording cleared");
        }

        #endregion

        #region Manual Playback (F10/F11 preview)

        public void StartPlayback()
        {
            if (recording.Count < 2)
            {
                Log("Not enough recording points for playback!");
                return;
            }

            ExitAllWarpForPlaybackStart("Preview");

            Color previewColor = Color.green;
            ghostObject = BuildPreviewGhostFromActiveVessel("Parsek_Ghost_Preview");
            if (ghostObject != null)
            {
                previewGhostMaterials = new List<Material>();
                Log("Manual preview ghost: built from active vessel snapshot");
            }
            else
            {
                ghostObject = CreateGhostSphere("Parsek_Ghost_Preview", previewColor);
                var m = ghostObject.GetComponent<Renderer>()?.material;
                previewGhostMaterials = m != null ? new List<Material> { m } : new List<Material>();
                Log("Manual preview ghost: using sphere fallback");
            }

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

            if (previewGhostMaterials != null)
            {
                for (int i = 0; i < previewGhostMaterials.Count; i++)
                {
                    if (previewGhostMaterials[i] != null)
                        Destroy(previewGhostMaterials[i]);
                }
                previewGhostMaterials.Clear();
            }

            if (ghostObject != null)
            {
                Destroy(ghostObject);
                ghostObject = null;
            }

            orbitCache.Clear();
            loggedOrbitSegments.Clear();
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
                StopPlayback();
                ScreenMessage("Preview playback complete", 2f);
                return;
            }

            InterpolateAndPosition(ghostObject, recording, orbitSegments,
                ref lastPlaybackIndex, recordingTime, 10000);
        }

        #endregion

        #region Timeline Auto-Playback

        void UpdateTimelinePlayback()
        {
            var committed = RecordingStore.CommittedRecordings;
            double currentUT = Planetarium.GetUniversalTime();

            // Prevent time warp from skipping ghost playback for vessel spawns
            if (committed.Count > 0 && IsAnyWarpActive() && lastTimelineUT >= 0
                && ParsekSettings.Current?.autoWarpStop != false)
            {
                double timelineStep = System.Math.Max(0, currentUT - lastTimelineUT);
                for (int i = 0; i < committed.Count; i++)
                {
                    var rec = committed[i];
                    if (rec.Points.Count < 2) continue;
                    if (ShouldLoopPlayback(rec)) continue;
                    if (rec.VesselSnapshot == null || rec.VesselSpawned || rec.VesselDestroyed ||
                        RecordingStore.IsChainMidSegment(rec)) continue;

                    bool crossedInto = lastTimelineUT < rec.StartUT && currentUT >= rec.StartUT;
                    bool approaching = currentUT < rec.StartUT &&
                                       rec.StartUT - currentUT <= System.Math.Max(1.0, timelineStep);
                    if (crossedInto || approaching)
                    {
                        if (warpStoppedForRecording.Add(i))
                        {
                            ExitAllWarpForPlaybackStart(rec.VesselName);
                            Log($"Stopped warp for recording #{i} ({rec.VesselName}) ghost playback");
                            ScreenMessage($"Time warp stopped — '{rec.VesselName}' playback", 3f);
                        }
                        break;
                    }
                }
            }
            lastTimelineUT = currentUT;

            if (committed.Count == 0) return;

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec.Points.Count < 2) continue;

                bool inRange = currentUT >= rec.StartUT && currentUT <= rec.EndUT;
                bool pastEnd = currentUT > rec.EndUT;
                GhostPlaybackState state;
                ghostStates.TryGetValue(i, out state);
                bool ghostActive = state != null && state.ghost != null;

                if (ShouldLoopPlayback(rec))
                {
                    UpdateLoopingTimelinePlayback(i, rec, currentUT, state, ghostActive);
                    continue;
                }

                bool isMidChain = RecordingStore.IsChainMidSegment(rec);
                double chainEndUT = isMidChain ? RecordingStore.GetChainEndUT(rec) : rec.EndUT;
                bool pastChainEnd = currentUT > chainEndUT;
                bool needsSpawn = rec.VesselSnapshot != null && !rec.VesselSpawned && !rec.VesselDestroyed && !rec.TakenControl;

                // Branch > 0 recordings are ghost-only (undock continuations) — never spawn
                if (needsSpawn && rec.ChainBranch > 0)
                    needsSpawn = false;

                // Suppress spawning for recordings belonging to a chain currently being built.
                // Without this guard, CommitChainSegment commits the vessel segment mid-flight,
                // and UpdateTimelinePlayback would spawn it immediately (on top of the real vessel).
                if (needsSpawn && activeChainId != null && rec.ChainId == activeChainId)
                {
                    needsSpawn = false;
                }

                // One-time chain spawn diagnostics when entering the spawn window
                if (pastChainEnd && !string.IsNullOrEmpty(rec.ChainId) && loggedGhostEnter.Add(i + 100000))
                    Log($"Chain spawn check #{i} \"{rec.VesselName}\": chainId={rec.ChainId}, idx={rec.ChainIndex}, " +
                        $"isMidChain={isMidChain}, hasSnapshot={rec.VesselSnapshot != null}, needsSpawn={needsSpawn}, " +
                        $"chainEndUT={chainEndUT:F1}, currentUT={currentUT:F1}");

                // Guard: if vessel was spawned before but VesselSpawned got reset (scene change),
                // check if the vessel still exists by its persistentId to avoid duplicates.
                if (needsSpawn && rec.SpawnedVesselPersistentId != 0 &&
                    VesselSpawner.VesselExistsByPid(rec.SpawnedVesselPersistentId))
                {
                    rec.VesselSpawned = true;
                    needsSpawn = false;
                    Log($"Vessel pid={rec.SpawnedVesselPersistentId} still exists — skipping duplicate spawn for recording #{i}");
                }

                // Skip ghost playback entirely for recordings where player took control
                if (rec.TakenControl)
                {
                    if (ghostActive)
                        DestroyTimelineGhost(i);
                    continue;
                }

                if (inRange)
                {
                    // Normal ghost playback
                    if (!ghostActive)
                    {
                        SpawnTimelineGhost(i, rec);
                        state = ghostStates[i];
                        if (loggedGhostEnter.Add(i))
                            Log($"Ghost ENTERED range: #{i} \"{rec.VesselName}\" at UT {currentUT:F1}");
                    }

                    int playbackIdx = state.playbackIndex;

                    InterpolateAndPosition(state.ghost, rec.Points, rec.OrbitSegments,
                        ref playbackIdx, currentUT, i * 10000);
                    state.playbackIndex = playbackIdx;

                    ApplyPartEvents(i, rec, currentUT, state);
                    ApplyResourceDeltas(rec, currentUT);
                }
                else if (pastChainEnd && needsSpawn && ghostActive)
                {
                    // Ghost was playing, UT crossed chain EndUT — spawn vessel, despawn ghost
                    Log($"Ghost EXITED range: #{i} \"{rec.VesselName}\" at UT {currentUT:F1} — spawning vessel");
                    PositionGhostAt(state.ghost, rec.Points[rec.Points.Count - 1]);
                    VesselSpawner.SpawnOrRecoverIfTooClose(rec, i);
                    DestroyTimelineGhost(i);
                    ApplyResourceDeltas(rec, currentUT);
                }
                else if (pastChainEnd && needsSpawn && !ghostActive)
                {
                    // UT already past chain EndUT on scene load — spawn immediately, no ghost
                    Log($"Ghost SKIPPED (UT already past EndUT): #{i} \"{rec.VesselName}\" at UT {currentUT:F1} " +
                        $"(EndUT={rec.EndUT:F1}) — spawning vessel immediately");
                    VesselSpawner.SpawnOrRecoverIfTooClose(rec, i);
                    ApplyResourceDeltas(rec, currentUT);
                }
                else if (pastEnd && ghostActive && isMidChain && !pastChainEnd)
                {
                    // Mid-chain segment past its own EndUT but chain still playing — hold at final pos
                    PositionGhostAt(state.ghost, rec.Points[rec.Points.Count - 1]);
                    if (pastEnd)
                        ApplyResourceDeltas(rec, currentUT);
                }
                else
                {
                    // Outside time range, no spawn needed — despawn ghost if active
                    if (ghostActive)
                    {
                        Log($"Ghost EXITED range: #{i} \"{rec.VesselName}\" at UT {currentUT:F1} — no vessel to spawn");
                        DestroyTimelineGhost(i);
                    }
                    // Catch up resource deltas for recordings we jumped past
                    if (pastEnd)
                        ApplyResourceDeltas(rec, currentUT);
                }
            }
        }

        private bool ShouldLoopPlayback(RecordingStore.Recording rec)
        {
            if (rec == null || !rec.LoopPlayback || rec.Points == null || rec.Points.Count < 2)
                return false;
            return rec.EndUT - rec.StartUT > MinLoopDurationSeconds;
        }

        private double GetLoopPauseSeconds(RecordingStore.Recording rec)
        {
            if (rec == null) return DefaultLoopPauseSeconds;
            if (double.IsNaN(rec.LoopPauseSeconds) || double.IsInfinity(rec.LoopPauseSeconds))
                return DefaultLoopPauseSeconds;
            return Math.Max(0.0, rec.LoopPauseSeconds);
        }

        private bool TryComputeLoopPlaybackUT(
            RecordingStore.Recording rec,
            double currentUT,
            out double loopUT,
            out int cycleIndex,
            out bool inPauseWindow)
        {
            loopUT = rec != null ? rec.StartUT : 0;
            cycleIndex = 0;
            inPauseWindow = false;
            if (rec == null || rec.Points == null || rec.Points.Count < 2) return false;
            if (currentUT < rec.StartUT) return false;

            double duration = rec.EndUT - rec.StartUT;
            if (duration <= MinLoopDurationSeconds) return false;

            double pauseSeconds = GetLoopPauseSeconds(rec);
            double cycleDuration = duration + pauseSeconds;
            if (cycleDuration <= MinLoopDurationSeconds)
                cycleDuration = duration;

            double elapsed = currentUT - rec.StartUT;
            cycleIndex = (int)Math.Floor(elapsed / cycleDuration);
            if (cycleIndex < 0) cycleIndex = 0;

            double cycleTime = elapsed - (cycleIndex * cycleDuration);
            if (pauseSeconds > 0 && cycleTime > duration)
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
            RecordingStore.Recording rec,
            double currentUT,
            GhostPlaybackState state,
            bool ghostActive)
        {
            double loopUT;
            int cycleIndex;
            bool inPauseWindow;
            if (!TryComputeLoopPlaybackUT(rec, currentUT, out loopUT, out cycleIndex, out inPauseWindow))
            {
                if (ghostActive)
                    DestroyTimelineGhost(recIdx);
                return;
            }

            // Rebuild once per loop cycle to guarantee clean visual state and event indices.
            bool cycleChanged = !ghostActive || state == null || state.loopCycleIndex != cycleIndex;
            if (cycleChanged && ghostActive)
            {
                DestroyTimelineGhost(recIdx);
                ghostActive = false;
                state = null;
            }

            if (!ghostActive)
            {
                SpawnTimelineGhost(recIdx, rec);
                state = ghostStates[recIdx];
                state.loopCycleIndex = cycleIndex;
                if (loggedGhostEnter.Add(recIdx))
                    Log($"Ghost ENTERED range: #{recIdx} \"{rec.VesselName}\" at UT {currentUT:F1} (loop)");
            }

            if (state == null || state.ghost == null)
                return;

            if (inPauseWindow)
            {
                PositionGhostAt(state.ghost, rec.Points[rec.Points.Count - 1]);
                return;
            }

            int playbackIdx = state.playbackIndex;
            InterpolateAndPosition(state.ghost, rec.Points, rec.OrbitSegments,
                ref playbackIdx, loopUT, recIdx * 10000);
            state.playbackIndex = playbackIdx;

            ApplyPartEvents(recIdx, rec, loopUT, state);
        }

        /// <summary>
        /// Apply resource deltas (funds, science, reputation) for recorded points
        /// that we've passed since the last frame. Computes delta between consecutive
        /// points and adds it to the current career pools.
        /// </summary>
        void ApplyResourceDeltas(RecordingStore.Recording rec, double currentUT)
        {
            if (ShouldPauseTimelineResourceReplay(IsRecording))
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
            int targetIndex = ComputeTargetResourceIndex(points, rec.LastAppliedResourceIndex, currentUT);

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

        void SpawnTimelineGhost(int index, RecordingStore.Recording rec)
        {
            Log($"Spawning timeline ghost #{index} for {rec.VesselName}");

            // Use a slightly different color to distinguish from manual preview
            Color ghostColor = new Color(0.2f, 1f, 0.4f, 0.8f); // bright green-cyan
            List<ParachuteGhostInfo> parachuteInfoList;
            List<JettisonGhostInfo> jettisonInfoList;
            List<EngineGhostInfo> engineInfoList;
            List<DeployableGhostInfo> deployableInfoList;
            List<HeatGhostInfo> heatInfoList;
            List<LightGhostInfo> lightInfoList;
            List<FairingGhostInfo> fairingInfoList;
            List<RcsGhostInfo> rcsInfoList;
            List<RoboticGhostInfo> roboticInfoList;
            GameObject ghost = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                rec, $"Parsek_Timeline_{index}", out parachuteInfoList, out jettisonInfoList,
                out engineInfoList, out deployableInfoList, out heatInfoList, out lightInfoList, out fairingInfoList,
                out rcsInfoList, out roboticInfoList);
            bool builtFromSnapshot = ghost != null;
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

            var state = new GhostPlaybackState
            {
                ghost = ghost,
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

            if (parachuteInfoList != null)
            {
                state.parachuteInfos = new Dictionary<uint, ParachuteGhostInfo>();
                for (int i = 0; i < parachuteInfoList.Count; i++)
                    state.parachuteInfos[parachuteInfoList[i].partPersistentId] = parachuteInfoList[i];
            }

            if (jettisonInfoList != null)
            {
                state.jettisonInfos = new Dictionary<uint, JettisonGhostInfo>();
                for (int i = 0; i < jettisonInfoList.Count; i++)
                    state.jettisonInfos[jettisonInfoList[i].partPersistentId] = jettisonInfoList[i];
            }

            if (engineInfoList != null)
            {
                state.engineInfos = new Dictionary<ulong, EngineGhostInfo>();
                for (int i = 0; i < engineInfoList.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        engineInfoList[i].partPersistentId, engineInfoList[i].moduleIndex);
                    state.engineInfos[key] = engineInfoList[i];
                }
            }

            if (deployableInfoList != null)
            {
                state.deployableInfos = new Dictionary<uint, DeployableGhostInfo>();
                for (int i = 0; i < deployableInfoList.Count; i++)
                    state.deployableInfos[deployableInfoList[i].partPersistentId] = deployableInfoList[i];
            }

            if (heatInfoList != null)
            {
                state.heatInfos = new Dictionary<uint, HeatGhostInfo>();
                for (int i = 0; i < heatInfoList.Count; i++)
                    state.heatInfos[heatInfoList[i].partPersistentId] = heatInfoList[i];
            }

            if (lightInfoList != null)
            {
                state.lightInfos = new Dictionary<uint, LightGhostInfo>();
                state.lightPlaybackStates = new Dictionary<uint, LightPlaybackState>();
                for (int i = 0; i < lightInfoList.Count; i++)
                {
                    state.lightInfos[lightInfoList[i].partPersistentId] = lightInfoList[i];
                    state.lightPlaybackStates[lightInfoList[i].partPersistentId] = new LightPlaybackState();
                }
            }

            if (fairingInfoList != null)
            {
                state.fairingInfos = new Dictionary<uint, FairingGhostInfo>();
                for (int i = 0; i < fairingInfoList.Count; i++)
                    state.fairingInfos[fairingInfoList[i].partPersistentId] = fairingInfoList[i];
            }

            if (rcsInfoList != null)
            {
                state.rcsInfos = new Dictionary<ulong, RcsGhostInfo>();
                for (int i = 0; i < rcsInfoList.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        rcsInfoList[i].partPersistentId, rcsInfoList[i].moduleIndex);
                    state.rcsInfos[key] = rcsInfoList[i];
                }
            }

            if (roboticInfoList != null)
            {
                state.roboticInfos = new Dictionary<ulong, RoboticGhostInfo>();
                for (int i = 0; i < roboticInfoList.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        roboticInfoList[i].partPersistentId, roboticInfoList[i].moduleIndex);
                    state.roboticInfos[key] = roboticInfoList[i];
                }
            }

            InitializeInventoryPlacementVisibility(rec, state);
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

            if (state.ghost != null)
                Destroy(state.ghost);

            DestroyAllFakeCanopies(state);
            ghostStates.Remove(index);
        }

        public void DestroyAllTimelineGhosts()
        {
            // Copy keys to avoid modifying during iteration
            var keys = new List<int>(ghostStates.Keys);
            foreach (int key in keys)
            {
                DestroyTimelineGhost(key);
            }
            orbitCache.Clear();
            loggedGhostEnter.Clear();
            loggedOrbitSegments.Clear();
            warpStoppedForRecording.Clear();
        }

        public bool CanDeleteRecording =>
            !IsRecording && continuationRecordingIdx < 0 && undockContinuationRecIdx < 0;

        public void DeleteRecording(int index)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (index < 0 || index >= committed.Count) return;
            if (!CanDeleteRecording) return;

            var rec = committed[index];
            Log($"Deleting recording '{rec.VesselName}' at index {index}");

            // Unreserve crew
            ParsekScenario.UnreserveCrewInSnapshot(rec.VesselSnapshot);

            // Destroy ghost if active
            DestroyTimelineGhost(index);

            // Remove from store (handles chain degradation + file deletion)
            RecordingStore.RemoveRecordingAt(index);

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

            // Clear orbit cache — keys are index-derived (i * 10000 + segIdx),
            // so after reindexing they would map to wrong recordings
            orbitCache.Clear();

            // Clear diagnostic guards since indices shifted
            loggedGhostEnter.Clear();
            loggedOrbitSegments.Clear();
            warpStoppedForRecording.Clear();

            ParsekLog.ScreenMessage($"Recording '{rec.VesselName}' deleted", 2f);
        }

        void ApplyPartEvents(int recIdx, RecordingStore.Recording rec, double currentUT, GhostPlaybackState state)
        {
            if (rec.PartEvents == null || rec.PartEvents.Count == 0) return;
            if (state.ghost == null) return;

            int evtIdx = state.partEventIndex;
            var tree = state.partTree;
            var ghost = state.ghost;

            while (evtIdx < rec.PartEvents.Count && rec.PartEvents[evtIdx].ut <= currentUT)
            {
                var evt = rec.PartEvents[evtIdx];
                switch (evt.eventType)
                {
                    case PartEventType.Decoupled:
                        if (tree != null)
                            HidePartSubtree(ghost, evt.partPersistentId, tree);
                        else
                            HideGhostPart(ghost, evt.partPersistentId);
                        Log($"Part event applied: Decoupled '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.Destroyed:
                        HideGhostPart(ghost, evt.partPersistentId);
                        Log($"Part event applied: Destroyed '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.ParachuteCut:
                        if (state.parachuteInfos != null)
                        {
                            ParachuteGhostInfo cutInfo;
                            if (state.parachuteInfos.TryGetValue(evt.partPersistentId, out cutInfo))
                            {
                                if (cutInfo.canopyTransform != null)
                                    cutInfo.canopyTransform.localScale = Vector3.zero;
                                if (cutInfo.capTransform != null)
                                    cutInfo.capTransform.gameObject.SetActive(false);
                            }
                        }
                        DestroyFakeCanopy(state, evt.partPersistentId);
                        Log($"Part event: ParachuteCut '{evt.partName}' — canopy hidden, housing remains");
                        break;
                    case PartEventType.ShroudJettisoned:
                        ApplyJettisonPanelState(state, evt, jettisoned: true);
                        Log($"Part event applied: ShroudJettisoned '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.ParachuteDestroyed:
                        // Clean up canopy visuals before hiding the part
                        if (state.parachuteInfos != null)
                        {
                            ParachuteGhostInfo destroyedInfo;
                            if (state.parachuteInfos.TryGetValue(evt.partPersistentId, out destroyedInfo))
                            {
                                if (destroyedInfo.canopyTransform != null)
                                    destroyedInfo.canopyTransform.localScale = Vector3.zero;
                            }
                        }
                        DestroyFakeCanopy(state, evt.partPersistentId);
                        HideGhostPart(ghost, evt.partPersistentId);
                        Log($"Part event applied: ParachuteDestroyed '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.ParachuteDeployed:
                        bool usedRealCanopy = false;

                        if (state.parachuteInfos != null)
                        {
                            ParachuteGhostInfo info;
                            if (state.parachuteInfos.TryGetValue(evt.partPersistentId, out info) && info.canopyTransform != null)
                            {
                                info.canopyTransform.localScale = info.deployedCanopyScale;
                                info.canopyTransform.localPosition = info.deployedCanopyPos;
                                info.canopyTransform.localRotation = info.deployedCanopyRot;
                                if (info.capTransform != null)
                                    info.capTransform.gameObject.SetActive(false);
                                usedRealCanopy = true;
                                Log($"Part event: ParachuteDeployed '{evt.partName}' — real canopy deployed");
                            }
                        }

                        if (!usedRealCanopy)
                        {
                            var canopy = GhostVisualBuilder.CreateFakeCanopy(ghost, evt.partPersistentId);
                            if (canopy != null)
                            {
                                TrackFakeCanopy(state, evt.partPersistentId, canopy);
                                Log($"Part event: ParachuteDeployed '{evt.partName}' — fake canopy (fallback)");
                            }
                            else
                            {
                                Log($"Part event: ParachuteDeployed '{evt.partName}' — could not create canopy");
                            }
                        }
                        break;
                    case PartEventType.EngineIgnited:
                        SetEngineEmission(state, evt, evt.value);
                        Log($"Part event applied: EngineIgnited '{evt.partName}' pid={evt.partPersistentId} midx={evt.moduleIndex} throttle={evt.value:F2}");
                        break;
                    case PartEventType.EngineShutdown:
                        SetEngineEmission(state, evt, 0f);
                        Log($"Part event applied: EngineShutdown '{evt.partName}' pid={evt.partPersistentId} midx={evt.moduleIndex}");
                        break;
                    case PartEventType.EngineThrottle:
                        SetEngineEmission(state, evt, evt.value);
                        break;
                    case PartEventType.DeployableExtended:
                        ApplyDeployableState(state, evt, deployed: true);
                        Log($"Part event applied: DeployableExtended '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.DeployableRetracted:
                        ApplyDeployableState(state, evt, deployed: false);
                        Log($"Part event applied: DeployableRetracted '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.ThermalAnimationHot:
                        ApplyHeatState(state, evt, heated: true);
                        Log($"Part event applied: ThermalAnimationHot '{evt.partName}' pid={evt.partPersistentId} midx={evt.moduleIndex} heat={evt.value:F2}");
                        break;
                    case PartEventType.ThermalAnimationCold:
                        ApplyHeatState(state, evt, heated: false);
                        Log($"Part event applied: ThermalAnimationCold '{evt.partName}' pid={evt.partPersistentId} midx={evt.moduleIndex} heat={evt.value:F2}");
                        break;
                    case PartEventType.LightOn:
                        ApplyLightPowerEvent(state, evt.partPersistentId, true);
                        Log($"Part event applied: LightOn '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.LightOff:
                        ApplyLightPowerEvent(state, evt.partPersistentId, false);
                        Log($"Part event applied: LightOff '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.LightBlinkEnabled:
                        ApplyLightBlinkModeEvent(state, evt.partPersistentId, enabled: true, evt.value);
                        Log($"Part event applied: LightBlinkEnabled '{evt.partName}' pid={evt.partPersistentId} rate={evt.value:F2}");
                        break;
                    case PartEventType.LightBlinkDisabled:
                        ApplyLightBlinkModeEvent(state, evt.partPersistentId, enabled: false, evt.value);
                        Log($"Part event applied: LightBlinkDisabled '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.LightBlinkRate:
                        ApplyLightBlinkRateEvent(state, evt.partPersistentId, evt.value);
                        Log($"Part event applied: LightBlinkRate '{evt.partName}' pid={evt.partPersistentId} rate={evt.value:F2}");
                        break;
                    case PartEventType.GearDeployed:
                        ApplyDeployableState(state, evt, deployed: true);
                        Log($"Part event applied: GearDeployed '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.GearRetracted:
                        ApplyDeployableState(state, evt, deployed: false);
                        Log($"Part event applied: GearRetracted '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.CargoBayOpened:
                        if (!ApplyDeployableState(state, evt, deployed: true))
                            ApplyJettisonPanelState(state, evt, jettisoned: true);
                        Log($"Part event applied: CargoBayOpened '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.CargoBayClosed:
                        if (!ApplyDeployableState(state, evt, deployed: false))
                            ApplyJettisonPanelState(state, evt, jettisoned: false);
                        Log($"Part event applied: CargoBayClosed '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.FairingJettisoned:
                        if (state.fairingInfos != null)
                        {
                            FairingGhostInfo fInfo;
                            if (state.fairingInfos.TryGetValue(evt.partPersistentId, out fInfo)
                                && fInfo.fairingMeshObject != null)
                            {
                                fInfo.fairingMeshObject.SetActive(false);
                                Log($"Part event applied: FairingJettisoned '{evt.partName}' pid={evt.partPersistentId}");
                            }
                        }
                        break;
                    case PartEventType.RCSActivated:
                        SetRcsEmission(state, evt, evt.value);
                        Log($"Part event applied: RCSActivated '{evt.partName}' pid={evt.partPersistentId} midx={evt.moduleIndex} power={evt.value:F2}");
                        break;
                    case PartEventType.RCSStopped:
                        SetRcsEmission(state, evt, 0f);
                        Log($"Part event applied: RCSStopped '{evt.partName}' pid={evt.partPersistentId} midx={evt.moduleIndex}");
                        break;
                    case PartEventType.RCSThrottle:
                        SetRcsEmission(state, evt, evt.value);
                        break;
                    case PartEventType.RoboticMotionStarted:
                    case PartEventType.RoboticPositionSample:
                    case PartEventType.RoboticMotionStopped:
                        ApplyRoboticEvent(state, evt, currentUT);
                        break;
                    case PartEventType.InventoryPartPlaced:
                        SetGhostPartActive(ghost, evt.partPersistentId, true);
                        Log($"Part event applied: InventoryPartPlaced '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.InventoryPartRemoved:
                        SetGhostPartActive(ghost, evt.partPersistentId, false);
                        Log($"Part event applied: InventoryPartRemoved '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                }
                evtIdx++;
            }

            state.partEventIndex = evtIdx;
            UpdateBlinkingLights(state, currentUT);
            UpdateActiveRobotics(state, currentUT);
        }

        static void HideGhostPart(GameObject ghost, uint persistentId)
        {
            var t = ghost.transform.Find($"ghost_part_{persistentId}");
            if (t != null) t.gameObject.SetActive(false);
        }

        static void SetGhostPartActive(GameObject ghost, uint persistentId, bool active)
        {
            if (ghost == null) return;
            var t = ghost.transform.Find($"ghost_part_{persistentId}");
            if (t != null) t.gameObject.SetActive(active);
        }

        static void InitializeInventoryPlacementVisibility(
            RecordingStore.Recording rec, GhostPlaybackState state)
        {
            if (rec == null || rec.PartEvents == null || rec.PartEvents.Count == 0) return;
            if (state == null || state.ghost == null) return;

            // If a part's first placement-related event is "placed", start hidden so it
            // visibly appears only when the event fires.
            var initialized = new HashSet<uint>();
            for (int i = 0; i < rec.PartEvents.Count; i++)
            {
                var evt = rec.PartEvents[i];
                if (initialized.Contains(evt.partPersistentId)) continue;

                if (evt.eventType == PartEventType.InventoryPartPlaced)
                {
                    SetGhostPartActive(state.ghost, evt.partPersistentId, false);
                    initialized.Add(evt.partPersistentId);
                }
                else if (evt.eventType == PartEventType.InventoryPartRemoved)
                {
                    SetGhostPartActive(state.ghost, evt.partPersistentId, true);
                    initialized.Add(evt.partPersistentId);
                }
            }
        }

        void TrackFakeCanopy(GhostPlaybackState state, uint partPid, GameObject canopy)
        {
            if (state.fakeCanopies == null)
                state.fakeCanopies = new Dictionary<uint, GameObject>();
            // Destroy previous canopy for this part if one exists (prevents leak)
            GameObject existing;
            if (state.fakeCanopies.TryGetValue(partPid, out existing) && existing != null)
                DestroyCanopyAndMaterial(existing);
            state.fakeCanopies[partPid] = canopy;
        }

        void DestroyFakeCanopy(GhostPlaybackState state, uint partPid)
        {
            if (state.fakeCanopies == null) return;
            GameObject canopy;
            if (state.fakeCanopies.TryGetValue(partPid, out canopy) && canopy != null)
                DestroyCanopyAndMaterial(canopy);
            state.fakeCanopies.Remove(partPid);
        }

        void DestroyAllFakeCanopies(GhostPlaybackState state)
        {
            if (state.fakeCanopies == null) return;
            foreach (var kv in state.fakeCanopies)
                if (kv.Value != null) DestroyCanopyAndMaterial(kv.Value);
            state.fakeCanopies = null;
        }

        static void DestroyCanopyAndMaterial(GameObject canopy)
        {
            var renderer = canopy.GetComponent<Renderer>();
            if (renderer != null && renderer.material != null)
                Destroy(renderer.material);
            Destroy(canopy);
        }

        static void SetEngineEmission(GhostPlaybackState state, PartEvent evt, float power)
        {
            if (state.engineInfos == null) return;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            EngineGhostInfo info;
            if (!state.engineInfos.TryGetValue(key, out info)) return;

            for (int i = 0; i < info.particleSystems.Count; i++)
            {
                var ps = info.particleSystems[i];
                if (ps == null) continue;

                var emission = ps.emission;
                if (power > 0f)
                {
                    float emRate = info.emissionCurve != null ? info.emissionCurve.Evaluate(power) : power * 100f;
                    emission.rateOverTimeMultiplier = emRate;

                    var main = ps.main;
                    float spd = info.speedCurve != null ? info.speedCurve.Evaluate(power) : power * 10f;
                    main.startSpeedMultiplier = spd;

                    if (!ps.isPlaying) ps.Play();
                }
                else
                {
                    emission.rateOverTimeMultiplier = 0;
                    ps.Stop();
                }
            }
        }

        static void SetRcsEmission(GhostPlaybackState state, PartEvent evt, float power)
        {
            if (state.rcsInfos == null) return;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            RcsGhostInfo info;
            if (!state.rcsInfos.TryGetValue(key, out info)) return;

            int configuredSystems = 0;
            int enabledRenderers = 0;
            int playingSystems = 0;
            float sampleRate = 0f;
            float sampleSpeed = 0f;
            float sampleSize = 0f;
            float sampleLifetime = 0f;

            for (int i = 0; i < info.particleSystems.Count; i++)
            {
                var ps = info.particleSystems[i];
                if (ps == null) continue;

                configuredSystems++;
                var emission = ps.emission;
                if (power > 0f)
                {
                    emission.enabled = true;
                    float emRate = ComputeScaledRcsEmissionRate(info.emissionCurve, power, info.emissionScale);
                    emission.rateOverTimeMultiplier = emRate;

                    var main = ps.main;
                    float spd = ComputeScaledRcsSpeed(info.speedCurve, power, info.speedScale);
                    main.startSpeedMultiplier = spd;

                    if (!ps.isPlaying) ps.Play();

                    if (sampleRate <= 0f)
                    {
                        sampleRate = emRate;
                        sampleSpeed = spd;
                        sampleSize = main.startSizeMultiplier;
                        sampleLifetime = main.startLifetimeMultiplier;
                    }
                }
                else
                {
                    emission.rateOverTimeMultiplier = 0;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                }

                if (ps.isPlaying) playingSystems++;
                var renderer = ps.GetComponent<ParticleSystemRenderer>();
                if (renderer != null && renderer.enabled) enabledRenderers++;
            }

            if (info.emissionScale > 1f)
            {
                ParsekLog.Log($"RCS showcase diagnostics: part='{evt.partName}' pid={evt.partPersistentId} midx={evt.moduleIndex} " +
                    $"power={power:F2} systems={configuredSystems} playing={playingSystems} renderers={enabledRenderers} " +
                    $"rate={sampleRate:F1} speed={sampleSpeed:F1} size={sampleSize:F2} life={sampleLifetime:F2}");
            }
        }

        internal static float ComputeScaledRcsEmissionRate(
            FloatCurve emissionCurve, float power, float emissionScale)
        {
            if (power <= 0f) return 0f;

            float emRate = emissionCurve != null ? emissionCurve.Evaluate(power) : power * 100f;
            emRate *= emissionScale > 0f ? emissionScale : 1f;
            if (emissionScale > 1f)
                emRate = Math.Max(emRate, 60f);

            return emRate;
        }

        internal static float ComputeScaledRcsSpeed(
            FloatCurve speedCurve, float power, float speedScale)
        {
            if (power <= 0f) return 0f;

            float spd = speedCurve != null ? speedCurve.Evaluate(power) : power * 10f;
            spd *= speedScale > 0f ? speedScale : 1f;
            if (speedScale > 1f)
                spd = Math.Max(spd, 4f);

            return spd;
        }

        internal static float ComputeRotorDeltaDegrees(float rpm, double deltaSeconds)
        {
            if (double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || deltaSeconds <= 0)
                return 0f;
            if (float.IsNaN(rpm) || float.IsInfinity(rpm) || Mathf.Abs(rpm) <= 0.0001f)
                return 0f;

            // RPM * 360deg / 60s
            return rpm * 6f * (float)deltaSeconds;
        }

        private static void ApplyRoboticPose(RoboticGhostInfo info, float value)
        {
            if (info == null || info.servoTransform == null)
                return;

            Vector3 axis = info.axisLocal.sqrMagnitude > 0.0001f
                ? info.axisLocal.normalized
                : Vector3.up;

            if (info.visualMode == RoboticVisualMode.Linear)
            {
                info.servoTransform.localPosition = info.stowedPos + (axis * value);
            }
            else if (info.visualMode == RoboticVisualMode.Rotational)
            {
                info.servoTransform.localRotation =
                    info.stowedRot * Quaternion.AngleAxis(value, axis);
            }
        }

        private static void ApplyRoboticEvent(
            GhostPlaybackState state, PartEvent evt, double currentUT)
        {
            if (state == null || state.roboticInfos == null)
                return;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            if (!state.roboticInfos.TryGetValue(key, out RoboticGhostInfo info) || info == null)
                return;

            info.currentValue = evt.value;

            if (info.visualMode == RoboticVisualMode.RotorRpm)
            {
                info.active = evt.eventType != PartEventType.RoboticMotionStopped &&
                    Mathf.Abs(evt.value) > 0.0001f;
                info.lastUpdateUT = currentUT;
            }
            else
            {
                ApplyRoboticPose(info, evt.value);
                info.active = evt.eventType != PartEventType.RoboticMotionStopped;
                info.lastUpdateUT = currentUT;
            }
        }

        private void UpdateActiveRobotics(GhostPlaybackState state, double currentUT)
        {
            if (state == null || state.roboticInfos == null || state.roboticInfos.Count == 0)
                return;

            foreach (var kv in state.roboticInfos)
            {
                RoboticGhostInfo info = kv.Value;
                if (info == null || info.servoTransform == null)
                    continue;

                if (double.IsNaN(info.lastUpdateUT) || double.IsInfinity(info.lastUpdateUT))
                {
                    info.lastUpdateUT = currentUT;
                    continue;
                }

                double deltaSeconds = currentUT - info.lastUpdateUT;
                if (deltaSeconds <= 0)
                {
                    info.lastUpdateUT = currentUT;
                    continue;
                }

                // Timeline jumps/loop boundaries rebuild ghosts, but guard large UT gaps anyway.
                deltaSeconds = Math.Min(deltaSeconds, 1.0);

                if (info.visualMode == RoboticVisualMode.RotorRpm && info.active)
                {
                    float deltaDegrees = ComputeRotorDeltaDegrees(info.currentValue, deltaSeconds);
                    if (Mathf.Abs(deltaDegrees) > 0.0001f)
                    {
                        Vector3 axis = info.axisLocal.sqrMagnitude > 0.0001f
                            ? info.axisLocal.normalized
                            : Vector3.up;
                        info.servoTransform.localRotation =
                            info.servoTransform.localRotation * Quaternion.AngleAxis(deltaDegrees, axis);
                    }
                }

                info.lastUpdateUT = currentUT;
            }
        }

        static bool ApplyHeatState(GhostPlaybackState state, PartEvent evt, bool heated)
        {
            if (state == null || state.heatInfos == null) return false;

            if (!state.heatInfos.TryGetValue(evt.partPersistentId, out HeatGhostInfo info) || info == null)
                return false;

            bool applied = false;

            if (info.transforms != null)
            {
                for (int i = 0; i < info.transforms.Count; i++)
                {
                    var ts = info.transforms[i];
                    if (ts.t == null) continue;

                    if (heated)
                    {
                        ts.t.localPosition = ts.deployedPos;
                        ts.t.localRotation = ts.deployedRot;
                        ts.t.localScale = ts.deployedScale;
                    }
                    else
                    {
                        ts.t.localPosition = ts.stowedPos;
                        ts.t.localRotation = ts.stowedRot;
                        ts.t.localScale = ts.stowedScale;
                    }
                    applied = true;
                }
            }

            if (info.materialStates != null)
            {
                for (int i = 0; i < info.materialStates.Count; i++)
                {
                    HeatMaterialState materialState = info.materialStates[i];
                    if (materialState.material == null) continue;

                    if (!string.IsNullOrEmpty(materialState.colorProperty))
                    {
                        materialState.material.SetColor(
                            materialState.colorProperty,
                            heated ? materialState.hotColor : materialState.coldColor);
                    }

                    if (!string.IsNullOrEmpty(materialState.emissiveProperty))
                    {
                        materialState.material.SetColor(
                            materialState.emissiveProperty,
                            heated ? materialState.hotEmission : materialState.coldEmission);
                    }

                    applied = true;
                }
            }

            return applied;
        }

        static bool ApplyDeployableState(GhostPlaybackState state, PartEvent evt, bool deployed)
        {
            if (state.deployableInfos == null) return false;

            DeployableGhostInfo info;
            if (!state.deployableInfos.TryGetValue(evt.partPersistentId, out info)) return false;

            bool applied = false;

            for (int i = 0; i < info.transforms.Count; i++)
            {
                var ts = info.transforms[i];
                if (ts.t == null) continue;
                applied = true;
                if (deployed)
                {
                    ts.t.localPosition = ts.deployedPos;
                    ts.t.localRotation = ts.deployedRot;
                    ts.t.localScale = ts.deployedScale;
                }
                else
                {
                    ts.t.localPosition = ts.stowedPos;
                    ts.t.localRotation = ts.stowedRot;
                    ts.t.localScale = ts.stowedScale;
                }
            }

            return applied;
        }

        static bool ApplyJettisonPanelState(GhostPlaybackState state, PartEvent evt, bool jettisoned)
        {
            if (state.jettisonInfos == null) return false;

            JettisonGhostInfo jetInfo;
            if (!state.jettisonInfos.TryGetValue(evt.partPersistentId, out jetInfo) ||
                jetInfo.jettisonTransforms == null ||
                jetInfo.jettisonTransforms.Count == 0)
                return false;

            bool applied = false;
            for (int i = 0; i < jetInfo.jettisonTransforms.Count; i++)
            {
                Transform jettisonTransform = jetInfo.jettisonTransforms[i];
                if (jettisonTransform == null) continue;
                jettisonTransform.gameObject.SetActive(!jettisoned);
                applied = true;
            }

            return applied;
        }

        private static LightPlaybackState GetOrCreateLightPlaybackState(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state.lightPlaybackStates == null)
                state.lightPlaybackStates = new Dictionary<uint, LightPlaybackState>();

            LightPlaybackState playbackState;
            if (!state.lightPlaybackStates.TryGetValue(partPersistentId, out playbackState))
            {
                playbackState = new LightPlaybackState();
                state.lightPlaybackStates[partPersistentId] = playbackState;
            }

            return playbackState;
        }

        private void ApplyLightPowerEvent(GhostPlaybackState state, uint partPersistentId, bool on)
        {
            if (state == null) return;
            LightPlaybackState playbackState = GetOrCreateLightPlaybackState(state, partPersistentId);
            playbackState.isOn = on;
            if (!on)
                SetLightState(state, partPersistentId, false);
            else if (!playbackState.blinkEnabled)
                SetLightState(state, partPersistentId, true);
        }

        private void ApplyLightBlinkModeEvent(
            GhostPlaybackState state, uint partPersistentId, bool enabled, float blinkRateHz)
        {
            if (state == null) return;
            LightPlaybackState playbackState = GetOrCreateLightPlaybackState(state, partPersistentId);
            playbackState.blinkEnabled = enabled;
            if (blinkRateHz > 0f)
                playbackState.blinkRateHz = blinkRateHz;
        }

        private void ApplyLightBlinkRateEvent(GhostPlaybackState state, uint partPersistentId, float blinkRateHz)
        {
            if (state == null) return;
            LightPlaybackState playbackState = GetOrCreateLightPlaybackState(state, partPersistentId);
            if (blinkRateHz > 0f)
                playbackState.blinkRateHz = blinkRateHz;
        }

        private void UpdateBlinkingLights(GhostPlaybackState state, double currentUT)
        {
            if (state == null || state.lightPlaybackStates == null || state.lightPlaybackStates.Count == 0)
                return;

            foreach (var kv in state.lightPlaybackStates)
            {
                uint partPersistentId = kv.Key;
                LightPlaybackState playbackState = kv.Value;
                if (playbackState == null)
                    continue;

                bool shouldEnable = playbackState.isOn;
                if (shouldEnable && playbackState.blinkEnabled)
                {
                    float rateHz = playbackState.blinkRateHz > 0f ? playbackState.blinkRateHz : 1f;
                    double cycle = currentUT * rateHz;
                    double frac = cycle - Math.Floor(cycle);
                    shouldEnable = frac < 0.5;
                }

                SetLightState(state, partPersistentId, shouldEnable);
            }
        }

        static void SetLightState(GhostPlaybackState state, uint partPersistentId, bool on)
        {
            if (state.lightInfos == null) return;

            LightGhostInfo info;
            if (!state.lightInfos.TryGetValue(partPersistentId, out info)) return;

            for (int i = 0; i < info.lights.Count; i++)
            {
                if (info.lights[i] != null)
                    info.lights[i].enabled = on;
            }
        }

        static void HidePartSubtree(GameObject ghost, uint rootPid, Dictionary<uint, List<uint>> tree)
        {
            var stack = new Stack<uint>();
            stack.Push(rootPid);
            while (stack.Count > 0)
            {
                uint pid = stack.Pop();
                var t = ghost.transform.Find($"ghost_part_{pid}");
                if (t != null) t.gameObject.SetActive(false);
                List<uint> children;
                if (tree.TryGetValue(pid, out children))
                    for (int c = 0; c < children.Count; c++)
                        stack.Push(children[c]);
            }
        }

        GameObject BuildPreviewGhostFromActiveVessel(string name)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return null;

            ProtoVessel pv = vessel.BackupVessel();
            if (pv == null) return null;

            ConfigNode node = new ConfigNode("VESSEL");
            pv.Save(node);

            var recordingView = new RecordingStore.Recording
            {
                VesselSnapshot = node
            };
            return GhostVisualBuilder.BuildTimelineGhostFromSnapshot(recordingView, name);
        }

        bool IsAnyWarpActive()
        {
            // CurrentRateIndex > 0 covers both rails warp and physics warp.
            return IsAnyWarpActive(TimeWarp.CurrentRateIndex, TimeWarp.CurrentRate);
        }

        void ExitAllWarpForPlaybackStart(string context)
        {
            if (!IsAnyWarpActive()) return;
            TimeWarp.SetRate(0, true);
            Log($"Warp reset for playback start ({context})");
        }

        internal static bool ShouldPauseTimelineResourceReplay(bool isRecording)
        {
            return isRecording;
        }

        internal static bool ShouldLoopPlayback(bool recordingLoopPlayback)
        {
            return recordingLoopPlayback;
        }

        internal static int ComputeTargetResourceIndex(
            List<TrajectoryPoint> points, int lastAppliedResourceIndex, double currentUT)
        {
            int targetIndex = lastAppliedResourceIndex;
            for (int j = lastAppliedResourceIndex + 1; j < points.Count; j++)
            {
                if (points[j].ut <= currentUT)
                    targetIndex = j;
                else
                    break;
            }
            return targetIndex;
        }

        internal static bool TryComputeLoopPlaybackUT(
            double currentUT, double startUT, double endUT, double pauseSeconds,
            out double loopUT, out int cycleIndex)
        {
            loopUT = startUT;
            cycleIndex = 0;

            double duration = endUT - startUT;
            if (duration <= 0 || pauseSeconds < 0 || currentUT < startUT)
                return false;

            double cycleDuration = duration + pauseSeconds;
            if (cycleDuration <= 0)
                return false;

            double elapsed = currentUT - startUT;
            cycleIndex = (int)Math.Floor(elapsed / cycleDuration);
            double phase = elapsed - (cycleIndex * cycleDuration);

            const double epsilon = 1e-6;
            if (phase > duration + epsilon)
                return false;

            if (phase < 0) phase = 0;
            if (phase > duration) phase = duration;

            loopUT = startUT + phase;
            return true;
        }

        internal static bool IsAnyWarpActive(int currentRateIndex, float currentRate)
        {
            return currentRateIndex > 0 || currentRate > 1f;
        }

        #endregion

        #region Take Control

        /// <summary>
        /// Take control of an active timeline ghost, converting it into a real vessel
        /// at its current position and switching the player to it.
        /// </summary>
        public void TakeControlOfGhost(int index)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (index < 0 || index >= committed.Count) return;

            var rec = committed[index];
            if (rec.VesselSnapshot == null || rec.VesselDestroyed || rec.VesselSpawned || rec.TakenControl)
            {
                Log($"Take control: cannot take control of recording #{index} — invalid state");
                return;
            }

            GhostPlaybackState controlState;
            if (!ghostStates.TryGetValue(index, out controlState) || controlState.ghost == null)
            {
                Log($"Take control: no active ghost for recording #{index}");
                return;
            }

            double ut = Planetarium.GetUniversalTime();
            GameObject ghost = controlState.ghost;

            // Determine body and velocity from current interpolation context
            CelestialBody body = null;
            Vector3d velocity = Vector3d.zero;

            // Check orbit segments first
            OrbitSegment? seg = null;
            if (rec.OrbitSegments != null && rec.OrbitSegments.Count > 0)
                seg = TrajectoryMath.FindOrbitSegment(rec.OrbitSegments, ut);

            if (seg.HasValue)
            {
                body = FlightGlobals.Bodies?.Find(b => b.name == seg.Value.bodyName);
                if (body != null)
                {
                    var orbit = new Orbit(
                        seg.Value.inclination, seg.Value.eccentricity,
                        seg.Value.semiMajorAxis, seg.Value.longitudeOfAscendingNode,
                        seg.Value.argumentOfPeriapsis, seg.Value.meanAnomalyAtEpoch,
                        seg.Value.epoch, body);
                    velocity = orbit.getOrbitalVelocityAtUT(ut);
                }
            }
            else
            {
                // Point-based interpolation
                int cachedIdx = controlState.playbackIndex;

                int idxBefore = TrajectoryMath.FindWaypointIndex(rec.Points, ref cachedIdx, ut);
                if (idxBefore >= 0 && idxBefore < rec.Points.Count - 1)
                {
                    var before = rec.Points[idxBefore];
                    var after = rec.Points[idxBefore + 1];
                    double segDur = after.ut - before.ut;
                    float t = segDur > 0.0001 ? (float)((ut - before.ut) / segDur) : 0f;
                    t = Mathf.Clamp01(t);

                    body = FlightGlobals.Bodies?.Find(b => b.name == before.bodyName);
                    velocity = Vector3.Lerp(before.velocity, after.velocity, t);
                }
                else if (rec.Points.Count > 0)
                {
                    var pt = rec.Points[rec.Points.Count - 1];
                    body = FlightGlobals.Bodies?.Find(b => b.name == pt.bodyName);
                    velocity = pt.velocity;
                }
            }

            if (body == null)
            {
                string searchedBody = seg.HasValue ? seg.Value.bodyName
                    : (rec.Points.Count > 0 ? rec.Points[rec.Points.Count - 1].bodyName : "none");
                Log($"Take control: could not determine body for recording #{index} — " +
                    $"searched for '{searchedBody}', Bodies={FlightGlobals.Bodies?.Count ?? 0}");
                return;
            }

            // Compute lat/lon/alt from ghost world position
            Vector3d ghostWorldPos = ghost.transform.position;
            double lat = body.GetLatitude(ghostWorldPos);
            double lon = body.GetLongitude(ghostWorldPos);
            double alt = body.GetAltitude(ghostWorldPos);

            // Apply remaining resource deltas up to current UT
            ApplyResourceDeltas(rec, ut);

            // Build exclude crew set (same as EndUT spawn)
            HashSet<string> excludeCrew = VesselSpawner.BuildExcludeCrewSet(rec);

            // Spawn vessel at ghost position
            uint pid = VesselSpawner.SpawnAtPosition(
                rec.VesselSnapshot, body, lat, lon, alt, velocity, ut, excludeCrew);

            if (pid == 0)
            {
                Log($"Take control: spawn failed for recording #{index}");
                ScreenMessage("Take Control failed — vessel spawn error", 3f);
                return;
            }

            // Mark recording as taken
            rec.TakenControl = true;
            rec.VesselSpawned = true;
            rec.SpawnedVesselPersistentId = pid;

            // Clean up crew reservations (same as EndUT path)
            ParsekScenario.UnreserveCrewInSnapshot(rec.VesselSnapshot);
            rec.VesselSnapshot = null;

            // Destroy ghost
            DestroyTimelineGhost(index);

            // Deferred vessel activation
            StartCoroutine(DeferredActivateVessel(pid));

            Log($"Take control: spawned vessel pid={pid} for recording #{index} ({rec.VesselName})");
            ScreenMessage($"Taking control of '{rec.VesselName}'", 3f);
        }

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

        void InterpolateAndPosition(GameObject ghost, List<TrajectoryPoint> points, ref int cachedIndex, double targetUT)
        {
            int indexBefore = TrajectoryMath.FindWaypointIndex(points, ref cachedIndex, targetUT);

            if (indexBefore < 0)
            {
                PositionGhostAt(ghost, points[0]);
                return;
            }

            TrajectoryPoint before = points[indexBefore];
            TrajectoryPoint after = points[indexBefore + 1];

            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                PositionGhostAt(ghost, before);
                return;
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

            CelestialBody bodyBefore = FlightGlobals.Bodies.Find(b => b.name == before.bodyName);
            CelestialBody bodyAfter = FlightGlobals.Bodies.Find(b => b.name == after.bodyName);
            if (bodyBefore == null || bodyAfter == null)
            {
                Log($"Could not find body: {(bodyBefore == null ? before.bodyName : after.bodyName)}");
                ghost.SetActive(false);
                return;
            }
            if (!ghost.activeSelf) ghost.SetActive(true);

            Vector3 posBefore = bodyBefore.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3 posAfter = bodyAfter.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);

            Vector3 interpolatedPos = Vector3.Lerp(posBefore, posAfter, t);
            Quaternion interpolatedRot = Quaternion.Slerp(before.rotation, after.rotation, t);

            interpolatedRot = SanitizeQuaternion(interpolatedRot);

            if (float.IsNaN(interpolatedPos.x) || float.IsNaN(interpolatedPos.y) || float.IsNaN(interpolatedPos.z))
            {
                Log("Warning: NaN in interpolated position, using 'before' position");
                interpolatedPos = posBefore;
            }

            ghost.transform.position = interpolatedPos;
            ghost.transform.rotation = interpolatedRot;
        }

        // Keep the old signature for backward compat with tests
        internal int FindWaypointIndex(double targetUT)
        {
            return TrajectoryMath.FindWaypointIndex(recording, ref lastPlaybackIndex, targetUT);
        }

        void PositionGhostAt(GameObject ghost, TrajectoryPoint point)
        {
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.name == point.bodyName);
            if (body == null) return;

            Vector3 worldPos = body.GetWorldSurfacePosition(
                point.latitude, point.longitude, point.altitude);

            ghost.transform.position = worldPos;
            ghost.transform.rotation = SanitizeQuaternion(point.rotation);
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

            // Orient prograde
            Vector3d velocity = orbit.getOrbitalVelocityAtUT(ut);
            if (velocity.sqrMagnitude > 0.001)
                ghost.transform.rotation = Quaternion.LookRotation(velocity);
        }

        void InterpolateAndPosition(GameObject ghost, List<TrajectoryPoint> points,
            List<OrbitSegment> segments, ref int cachedIndex, double targetUT, int orbitCacheBase)
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
                    return;
                }
            }

            // Fall through to point-based interpolation
            InterpolateAndPosition(ghost, points, ref cachedIndex, targetUT);
        }

        // Delegates to TrajectoryMath — kept for backward compatibility
        internal Quaternion SanitizeQuaternion(Quaternion q)
        {
            return TrajectoryMath.SanitizeQuaternion(q);
        }

        #endregion

        #region Utilities

        void Log(string message) => ParsekLog.Log(message);
        void ScreenMessage(string message, float duration) => ParsekLog.ScreenMessage(message, duration);

        #endregion
    }
}
