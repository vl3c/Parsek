using System;
using System.Collections;
using System.Collections.Generic;
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
        private class GhostPlaybackState
        {
            public GameObject ghost;
            public List<Material> materials;
            public int playbackIndex;
            public int partEventIndex;
            public Dictionary<uint, List<uint>> partTree;
            public Dictionary<uint, ParachuteGhostInfo> parachuteInfos;
            public Dictionary<uint, JettisonGhostInfo> jettisonInfos;
            public Dictionary<ulong, EngineGhostInfo> engineInfos; // key = EncodeEngineKey(pid, moduleIndex)
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

        // Timeline warp protection — tracks previous frame's UT
        private double lastTimelineUT = -1;

        // Diagnostic logging guards (log once per state transition, not per frame)
        private HashSet<int> loggedGhostEnter = new HashSet<int>();
        private HashSet<int> loggedOrbitSegments = new HashSet<int>();

        // Warp-stop guard: only stop time warp once per recording
        private HashSet<int> warpStoppedForRecording = new HashSet<int>();
        private bool timelineResourceReplayPausedLogged = false;

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

            // Chain: auto-commit previous segment when recording stopped (EVA exit)
            // VesselSnapshot is kept — for V→EVA chains, the vessel should spawn at its
            // final position alongside the EVA kerbal.
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
        }

        void OnVesselWillDestroy(Vessel v)
        {
            recorder?.OnVesselWillDestroy(v);
        }

        void OnVesselSituationChange(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> data)
        {
            if (IsRecording) return;
            if (data.host != FlightGlobals.ActiveVessel) return;
            if (data.from != Vessel.Situations.PRELAUNCH) return;

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
        /// Mid-chain segments keep their VesselSnapshot so they can spawn independently
        /// (e.g. V→EVA: vessel spawns at landing, kerbal spawns at walk endpoint).
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

            // Keep VesselSnapshot on mid-chain segments — for V→EVA chains, the vessel
            // should still spawn at its final position. BuildExcludeCrewSet handles crew
            // deduplication. When boarding is implemented, only EVA segments where the
            // kerbal subsequently boards a vessel should have their snapshot nulled.

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
            if (committed.Count > 0 && IsAnyWarpActive() && lastTimelineUT >= 0)
            {
                double timelineStep = System.Math.Max(0, currentUT - lastTimelineUT);
                for (int i = 0; i < committed.Count; i++)
                {
                    var rec = committed[i];
                    if (rec.Points.Count < 2) continue;
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
                bool isMidChain = RecordingStore.IsChainMidSegment(rec);
                double chainEndUT = isMidChain ? RecordingStore.GetChainEndUT(rec) : rec.EndUT;
                bool pastChainEnd = currentUT > chainEndUT;
                bool needsSpawn = rec.VesselSnapshot != null && !rec.VesselSpawned && !rec.VesselDestroyed && !rec.TakenControl;

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
            GameObject ghost = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                rec, $"Parsek_Timeline_{index}", out parachuteInfoList, out jettisonInfoList,
                out engineInfoList);
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

            ghostStates[index] = state;
        }

        void DestroyTimelineGhost(int index)
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
                        if (state.jettisonInfos != null)
                        {
                            JettisonGhostInfo jetInfo;
                            if (state.jettisonInfos.TryGetValue(evt.partPersistentId, out jetInfo) && jetInfo.jettisonTransform != null)
                            {
                                jetInfo.jettisonTransform.gameObject.SetActive(false);
                                Log($"Part event applied: ShroudJettisoned '{evt.partName}' pid={evt.partPersistentId}");
                            }
                        }
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
                }
                evtIdx++;
            }

            state.partEventIndex = evtIdx;
        }

        static void HideGhostPart(GameObject ghost, uint persistentId)
        {
            var t = ghost.transform.Find($"ghost_part_{persistentId}");
            if (t != null) t.gameObject.SetActive(false);
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
