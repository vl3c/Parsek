using System;
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

        // Timeline ghost state (auto-playback of committed recordings)
        private Dictionary<int, GameObject> timelineGhosts = new Dictionary<int, GameObject>();
        private Dictionary<int, List<Material>> timelineGhostMaterials = new Dictionary<int, List<Material>>();
        private Dictionary<int, int> timelinePlaybackIndices = new Dictionary<int, int>();

        // Part event playback state
        private Dictionary<int, int> timelinePartEventIndices = new Dictionary<int, int>();
        private Dictionary<int, Dictionary<uint, List<uint>>> ghostPartTrees = new Dictionary<int, Dictionary<uint, List<uint>>>();

        // Auto-record: EVA from pad triggers recording after vessel switch completes
        private bool pendingAutoRecord = false;

        // EVA child recording state
        private bool pendingEvaChildRecord;
        private string pendingEvaCrewName;
        private string activeChildParentId;
        private string activeChildCrewName;

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
        public int TimelineGhostCount => timelineGhosts.Count;
        public GameObject PreviewGhost => ghostObject;
        public Dictionary<int, GameObject> TimelineGhosts => timelineGhosts;

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
            // EVA child: auto-commit parent recording once vessel-switch auto-stop fires
            if (pendingEvaChildRecord && recorder != null && !recorder.IsRecording && recorder.CaptureAtStop != null)
            {
                string parentRecordingId = recorder.CaptureAtStop.RecordingId;
                Log($"EVA child: auto-committing parent recording (id={parentRecordingId})");

                RecordingStore.StashPending(
                    recorder.Recording,
                    recorder.CaptureAtStop.VesselName,
                    recorder.OrbitSegments,
                    recordingId: parentRecordingId,
                    recordingFormatVersion: recorder.CaptureAtStop.RecordingFormatVersion,
                    ghostGeometryVersion: recorder.CaptureAtStop.GhostGeometryVersion,
                    partEvents: recorder.PartEvents);

                if (RecordingStore.HasPending)
                {
                    RecordingStore.Pending.ApplyPersistenceArtifactsFrom(recorder.CaptureAtStop);
                    RecordingStore.CommitPending();
                    ParsekScenario.ReserveSnapshotCrew();
                    ParsekScenario.SwapReservedCrewInFlight();

                    activeChildParentId = parentRecordingId;
                    activeChildCrewName = pendingEvaCrewName;
                }
                else
                {
                    // Parent recording too short — abort child flow
                    Log("EVA child: parent recording too short to stash — aborting child");
                    pendingEvaChildRecord = false;
                    pendingEvaCrewName = null;
                    pendingAutoRecord = false;
                }

                recorder = null;
                // pendingAutoRecord is still true — will start child recording next
            }

            // Complete deferred auto-record for EVA (vessel switch may take a frame)
            if (pendingAutoRecord && !IsRecording &&
                FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.isEVA)
            {
                pendingAutoRecord = false;
                StartRecording();

                // Tag child recording with parent linkage
                if (pendingEvaChildRecord)
                {
                    pendingEvaChildRecord = false;
                    pendingEvaCrewName = null;
                    Log($"EVA child recording started (parent={activeChildParentId}, crew={activeChildCrewName})");
                    ScreenMessage("Recording STARTED (EVA child)", 2f);
                }
                else
                {
                    Log("Auto-record started (EVA from pad)");
                    ScreenMessage("Recording STARTED (auto — EVA from pad)", 2f);
                }
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
                // ApplyPersistenceArtifactsFrom copies ParentRecordingId/EvaCrewName from
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
                    // ApplyPersistenceArtifactsFrom didn't run. Apply child
                    // metadata directly from the active fields.
                    if (RecordingStore.HasPending && !string.IsNullOrEmpty(activeChildParentId))
                    {
                        RecordingStore.Pending.ParentRecordingId = activeChildParentId;
                        RecordingStore.Pending.EvaCrewName = activeChildCrewName;
                    }
                }

                // Clear child fields after both paths have consumed them
                activeChildParentId = null;
                activeChildCrewName = null;

                recorder = null;
                lastPlaybackIndex = 0;
            }

            // Stop manual playback
            StopPlayback();
            DestroyAllTimelineGhosts();
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

        void OnCrewOnEva(GameEvents.FromToAction<Part, Part> data)
        {
            // Mid-recording EVA: auto-stop parent, start child for EVA kerbal
            if (IsRecording)
            {
                // Only trigger child flow if EVA is from the vessel we're recording
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

                pendingEvaChildRecord = true;
                pendingEvaCrewName = kerbalName;
                pendingAutoRecord = true;
                Log($"Mid-recording EVA detected: '{kerbalName}' — pending child record");
                return;
            }

            if (data.from.vessel == null) return;
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

            if (RecordingStore.HasPending)
            {
                var pending = RecordingStore.Pending;

                // Auto-commit EVA child recordings (skip merge dialog)
                if (!string.IsNullOrEmpty(pending.ParentRecordingId))
                {
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
            ParsekScenario.SwapReservedCrewInFlight();

            var committed = RecordingStore.CommittedRecordings;
            Log($"Timeline has {committed.Count} committed recording(s)");
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                string hasVessel = (rec.GhostVisualSnapshot != null || rec.VesselSnapshot != null) ? "vessel" : "ghost-only";
                string orbitInfo = rec.OrbitSegments.Count > 0
                    ? $", {rec.OrbitSegments.Count} orbit seg(s)"
                    : "";
                Log($"  Recording #{i}: \"{rec.VesselName}\" UT {rec.StartUT:F0}-{rec.EndUT:F0}, " +
                    $"{rec.Points.Count} pts{orbitInfo}, {hasVessel}");
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
            recorder.StartRecording();
        }

        public void StopRecording()
        {
            recorder?.StopRecording();

            // Tag child recording if this was an EVA child.
            // Do NOT clear activeChild* here — OnSceneChangeRequested needs them
            // as a fallback when CaptureAtStop is unavailable (ForceStop path).
            // ApplyPersistenceArtifactsFrom copies them from CaptureAtStop on the
            // normal path; the activeChild* fields cover the ForceStop fallback.
            if (recorder?.CaptureAtStop != null && !string.IsNullOrEmpty(activeChildParentId))
            {
                recorder.CaptureAtStop.ParentRecordingId = activeChildParentId;
                recorder.CaptureAtStop.EvaCrewName = activeChildCrewName;
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
                previewGhostMaterials = ApplyGhostStyleAndCollectMaterials(ghostObject, previewColor, 0.55f);
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
            Log("Manual playback stopped");
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
                    if (rec.VesselSnapshot == null || rec.VesselSpawned) continue;

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
                bool ghostActive = timelineGhosts.ContainsKey(i) && timelineGhosts[i] != null;
                bool needsSpawn = rec.VesselSnapshot != null && !rec.VesselSpawned && !rec.VesselDestroyed;

                // Guard: if vessel was spawned before but VesselSpawned got reset (scene change),
                // check if the vessel still exists by its persistentId to avoid duplicates.
                if (needsSpawn && rec.SpawnedVesselPersistentId != 0 &&
                    VesselSpawner.VesselExistsByPid(rec.SpawnedVesselPersistentId))
                {
                    rec.VesselSpawned = true;
                    needsSpawn = false;
                    Log($"Vessel pid={rec.SpawnedVesselPersistentId} still exists — skipping duplicate spawn for recording #{i}");
                }

                if (inRange)
                {
                    // Normal ghost playback
                    if (!ghostActive)
                    {
                        SpawnTimelineGhost(i, rec);
                        if (loggedGhostEnter.Add(i))
                            Log($"Ghost ENTERED range: #{i} \"{rec.VesselName}\" at UT {currentUT:F1}");
                    }

                    int playbackIdx = 0;
                    if (timelinePlaybackIndices.ContainsKey(i))
                        playbackIdx = timelinePlaybackIndices[i];

                    InterpolateAndPosition(timelineGhosts[i], rec.Points, rec.OrbitSegments,
                        ref playbackIdx, currentUT, i * 10000);
                    timelinePlaybackIndices[i] = playbackIdx;

                    ApplyPartEvents(i, rec, currentUT, timelineGhosts[i]);
                    ApplyResourceDeltas(rec, currentUT);
                }
                else if (pastEnd && needsSpawn && ghostActive)
                {
                    // Ghost was playing, UT just crossed EndUT — hold at final pos, spawn, despawn ghost
                    Log($"Ghost EXITED range: #{i} \"{rec.VesselName}\" at UT {currentUT:F1} — spawning vessel");
                    PositionGhostAt(timelineGhosts[i], rec.Points[rec.Points.Count - 1]);
                    VesselSpawner.SpawnOrRecoverIfTooClose(rec, i);
                    DestroyTimelineGhost(i);
                    ApplyResourceDeltas(rec, currentUT);
                }
                else if (pastEnd && needsSpawn && !ghostActive)
                {
                    // UT already past EndUT on scene load — spawn immediately, no ghost
                    VesselSpawner.SpawnOrRecoverIfTooClose(rec, i);
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
            GameObject ghost = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                rec, $"Parsek_Timeline_{index}");
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
                // Snapshot-based ghosts use prefab materials by default; apply a
                // consistent translucent tint so they remain visually distinct.
                timelineGhostMaterials[index] = ApplyGhostStyleAndCollectMaterials(ghost, ghostColor, 0.55f);
            }

            timelineGhosts[index] = ghost;
            if (!builtFromSnapshot)
            {
                var m = ghost.GetComponent<Renderer>()?.material;
                timelineGhostMaterials[index] = m != null ? new List<Material> { m } : new List<Material>();
            }
            timelinePlaybackIndices[index] = 0;

            // Build part tree for event playback
            timelinePartEventIndices[index] = 0;
            ConfigNode snapshot = GhostVisualBuilder.GetGhostSnapshot(rec);
            ghostPartTrees[index] = GhostVisualBuilder.BuildPartSubtreeMap(snapshot);
        }

        void DestroyTimelineGhost(int index)
        {
            Log($"Despawning timeline ghost #{index}");

            if (timelineGhostMaterials.ContainsKey(index) && timelineGhostMaterials[index] != null)
            {
                var mats = timelineGhostMaterials[index];
                for (int i = 0; i < mats.Count; i++)
                {
                    if (mats[i] != null)
                        Destroy(mats[i]);
                }
                timelineGhostMaterials.Remove(index);
            }

            if (timelineGhosts.ContainsKey(index) && timelineGhosts[index] != null)
            {
                Destroy(timelineGhosts[index]);
                timelineGhosts.Remove(index);
            }

            timelinePlaybackIndices.Remove(index);
            timelinePartEventIndices.Remove(index);
            ghostPartTrees.Remove(index);
        }

        public void DestroyAllTimelineGhosts()
        {
            // Copy keys to avoid modifying during iteration
            var keys = new List<int>(timelineGhosts.Keys);
            foreach (int key in keys)
            {
                DestroyTimelineGhost(key);
            }
            orbitCache.Clear();
            loggedGhostEnter.Clear();
            loggedOrbitSegments.Clear();
            warpStoppedForRecording.Clear();
        }

        void ApplyPartEvents(int recIdx, RecordingStore.Recording rec, double currentUT, GameObject ghost)
        {
            if (rec.PartEvents == null || rec.PartEvents.Count == 0) return;
            if (ghost == null) return;

            int evtIdx;
            if (!timelinePartEventIndices.TryGetValue(recIdx, out evtIdx))
                evtIdx = 0;

            Dictionary<uint, List<uint>> tree;
            ghostPartTrees.TryGetValue(recIdx, out tree);

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
                        HideGhostPart(ghost, evt.partPersistentId);
                        break;
                    case PartEventType.ParachuteDeployed:
                        // Canopy mesh is procedural — cannot recreate; log only
                        Log($"Part event: ParachuteDeployed '{evt.partName}' (visual not applied)");
                        break;
                }
                evtIdx++;
            }

            timelinePartEventIndices[recIdx] = evtIdx;
        }

        static void HideGhostPart(GameObject ghost, uint persistentId)
        {
            var t = ghost.transform.Find($"ghost_part_{persistentId}");
            if (t != null) t.gameObject.SetActive(false);
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

        List<Material> ApplyGhostStyleAndCollectMaterials(GameObject ghost, Color tint, float alpha)
        {
            var unique = new HashSet<Material>();
            if (ghost == null) return new List<Material>();

            // Try to find a transparent shader so the ghost preserves original
            // part textures instead of being a solid green blob.
            Shader transparentShader = FindTransparentGhostShader();

            var renderers = ghost.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;

                // Accessing .materials creates per-instance copies so styling this
                // ghost doesn't mutate shared prefab materials.
                var mats = renderer.materials;
                for (int m = 0; m < mats.Length; m++)
                {
                    var mat = mats[m];
                    if (mat == null) continue;

                    if (transparentShader != null)
                    {
                        // Switch to transparent shader, preserving original textures.
                        // Ghost looks like a semi-transparent replica of the vessel.
                        mat.shader = transparentShader;
                        Color original = mat.HasProperty("_Color")
                            ? mat.GetColor("_Color") : Color.white;
                        Color blended = Color.Lerp(original, tint, 0.2f);
                        blended.a = alpha;
                        mat.SetColor("_Color", blended);
                    }
                    else
                    {
                        // Fallback: solid color tint (no transparency support)
                        Color c = tint;
                        c.a = alpha;
                        if (mat.HasProperty("_Color"))
                            mat.SetColor("_Color", c);
                        if (mat.HasProperty("_EmissiveColor"))
                            mat.SetColor("_EmissiveColor", c);
                        if (mat.HasProperty("_TintColor"))
                            mat.SetColor("_TintColor", c);
                    }

                    unique.Add(mat);
                }
            }
            return new List<Material>(unique);
        }

        static Shader FindTransparentGhostShader()
        {
            // KSP-specific transparent shaders first, then Unity built-in
            Shader s = Shader.Find("KSP/Alpha/Translucent Specular");
            if (s != null) return s;
            s = Shader.Find("KSP/Alpha/Translucent");
            if (s != null) return s;
            return Shader.Find("Legacy Shaders/Transparent/Diffuse");
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
