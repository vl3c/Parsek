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

        // Recording state
        internal List<TrajectoryPoint> recording = new List<TrajectoryPoint>();
        private bool isRecording = false;
        private uint recordingVesselId;

        // Adaptive sampling: fast tick that decides whether to record
        private const float sampleTickInterval = 0.1f;   // how often SamplePosition fires
        private const float maxSampleInterval = 3.0f;     // max time between recorded points
        private const float velocityDirThreshold = 2.0f;  // degrees of velocity direction change
        private const float speedChangeThreshold = 0.05f; // 5% relative speed change
        private double lastRecordedUT = -1;
        private Vector3 lastRecordedVelocity;

        // Manual playback state (F10/F11 preview of current recording)
        private bool isPlaying = false;
        private double playbackStartUT;
        private double recordingStartUT;
        private GameObject ghostObject;
        private Material ghostMaterial;
        internal int lastPlaybackIndex = 0;

        // Timeline ghost state (auto-playback of committed recordings)
        private Dictionary<int, GameObject> timelineGhosts = new Dictionary<int, GameObject>();
        private Dictionary<int, Material> timelineGhostMaterials = new Dictionary<int, Material>();
        private Dictionary<int, int> timelinePlaybackIndices = new Dictionary<int, int>();

        // Orbital recording state (on-rails detection)
        private List<OrbitSegment> orbitSegments = new List<OrbitSegment>();
        private bool isOnRails = false;
        private OrbitSegment currentOrbitSegment;

        // Vessel destruction tracking
        private bool vesselDestroyedDuringRecording = false;

        // Auto-record: EVA from pad triggers recording after vessel switch completes
        private bool pendingAutoRecord = false;

        // Timeline warp protection — tracks previous frame's UT
        private double lastTimelineUT = -1;

        // UI
        private Rect windowRect = new Rect(20, 100, 250, 250);
        private bool showUI = false;
        private ToolbarControl toolbarControl;
        private ParsekUI ui;

        #endregion

        #region Public Accessors (for ParsekUI)

        public bool IsRecording => isRecording;
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
            // Complete deferred auto-record for EVA (vessel switch may take a frame)
            if (pendingAutoRecord && !isRecording &&
                FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.isEVA)
            {
                pendingAutoRecord = false;
                StartRecording();
                Log("Auto-record started (EVA from pad)");
                ScreenMessage("Recording STARTED (auto — EVA from pad)", 2f);
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
            if (isRecording)
            {
                CancelInvoke(nameof(SamplePosition));
                isRecording = false;
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
            if (recording.Count > 0)
            {
                // Stop recording if active
                if (isRecording)
                {
                    // Finalize in-progress orbit segment
                    if (isOnRails)
                    {
                        currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
                        orbitSegments.Add(currentOrbitSegment);
                        isOnRails = false;
                    }

                    CancelInvoke(nameof(SamplePosition));
                    isRecording = false;
                    Log("Auto-stopped recording due to scene change");
                }

                string vesselName = FlightGlobals.ActiveVessel != null
                    ? FlightGlobals.ActiveVessel.vesselName
                    : "Unknown Vessel";

                RecordingStore.StashPending(recording, vesselName, orbitSegments);

                // Snapshot vessel for persistence across revert
                VesselSpawner.SnapshotVessel(RecordingStore.Pending, vesselDestroyedDuringRecording);

                recording.Clear();
                orbitSegments.Clear();
                lastPlaybackIndex = 0;
                vesselDestroyedDuringRecording = false;
            }

            // Stop manual playback
            StopPlayback();
            DestroyAllTimelineGhosts();
        }

        void OnVesselWillDestroy(Vessel v)
        {
            if (!isRecording) return;
            if (FlightGlobals.ActiveVessel == null) return;
            if (v == FlightGlobals.ActiveVessel)
            {
                // Finalize in-progress orbit segment if on rails
                if (isOnRails)
                {
                    currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
                    orbitSegments.Add(currentOrbitSegment);
                    isOnRails = false;
                }

                vesselDestroyedDuringRecording = true;
                Log("Active vessel destroyed during recording!");
            }
        }

        void OnVesselSituationChange(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> data)
        {
            if (isRecording) return;
            if (data.host != FlightGlobals.ActiveVessel) return;
            if (data.from != Vessel.Situations.PRELAUNCH) return;

            StartRecording();
            Log("Auto-record started (vessel left pad/runway)");
            ScreenMessage("Recording STARTED (auto)", 2f);
        }

        void OnCrewOnEva(GameEvents.FromToAction<Part, Part> data)
        {
            if (isRecording) return;
            if (data.from.vessel == null) return;
            if (data.from.vessel.situation != Vessel.Situations.PRELAUNCH) return;

            // The EVA kerbal may not yet be the active vessel, defer to Update()
            pendingAutoRecord = true;
            Log("EVA from pad detected — pending auto-record");
        }

        void OnVesselGoOnRails(Vessel v)
        {
            if (!isRecording) return;
            if (v != FlightGlobals.ActiveVessel) return;
            if (v.persistentId != recordingVesselId) return;

            // Record a boundary TrajectoryPoint at current UT (stitching point)
            SamplePosition();

            // Capture orbit params
            currentOrbitSegment = new OrbitSegment
            {
                startUT = Planetarium.GetUniversalTime(),
                inclination = v.orbit.inclination,
                eccentricity = v.orbit.eccentricity,
                semiMajorAxis = v.orbit.semiMajorAxis,
                longitudeOfAscendingNode = v.orbit.LAN,
                argumentOfPeriapsis = v.orbit.argumentOfPeriapsis,
                meanAnomalyAtEpoch = v.orbit.meanAnomalyAtEpoch,
                epoch = v.orbit.epoch,
                bodyName = v.mainBody.name
            };

            CancelInvoke(nameof(SamplePosition));
            isOnRails = true;
            Log($"Vessel went on rails — capturing orbit segment (body={v.mainBody.name})");
        }

        void OnVesselGoOffRails(Vessel v)
        {
            if (!isRecording || !isOnRails) return;
            if (v != FlightGlobals.ActiveVessel) return;
            if (v.persistentId != recordingVesselId) return;

            // Finalize orbit segment
            currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
            orbitSegments.Add(currentOrbitSegment);
            isOnRails = false;

            // Record a boundary TrajectoryPoint at current UT
            SamplePosition();

            // Resume sampling (fast tick — adaptive logic decides whether to record)
            InvokeRepeating(nameof(SamplePosition), sampleTickInterval, sampleTickInterval);
            Log($"Vessel went off rails — orbit segment closed " +
                $"(UT {currentOrbitSegment.startUT:F0}-{currentOrbitSegment.endUT:F0})");
        }

        void OnVesselSOIChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> data)
        {
            if (!isRecording || !isOnRails) return;
            if (data.host != FlightGlobals.ActiveVessel) return;
            if (data.host.persistentId != recordingVesselId) return;

            // Close current orbit segment in old SOI
            currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
            orbitSegments.Add(currentOrbitSegment);

            // Start new orbit segment in new SOI
            var v = data.host;
            currentOrbitSegment = new OrbitSegment
            {
                startUT = Planetarium.GetUniversalTime(),
                inclination = v.orbit.inclination,
                eccentricity = v.orbit.eccentricity,
                semiMajorAxis = v.orbit.semiMajorAxis,
                longitudeOfAscendingNode = v.orbit.LAN,
                argumentOfPeriapsis = v.orbit.argumentOfPeriapsis,
                meanAnomalyAtEpoch = v.orbit.meanAnomalyAtEpoch,
                epoch = v.orbit.epoch,
                bodyName = v.mainBody.name
            };

            Log($"SOI changed during orbit recording: {data.from.name} → {data.to.name}");
        }

        void OnFlightReady()
        {
            Log("Flight ready. Checking for pending recordings...");

            if (RecordingStore.HasPending)
            {
                var pending = RecordingStore.Pending;
                Log($"Found pending recording from {pending.VesselName} ({pending.Points.Count} points)");
                MergeDialog.Show(pending);
            }

            // Swap reserved crew out of the active vessel so the player
            // can't record with them again (they belong to deferred-spawn vessels)
            ParsekScenario.SwapReservedCrewInFlight();

            Log($"Timeline has {RecordingStore.CommittedRecordings.Count} committed recording(s)");
        }

        #endregion

        #region Input Handling

        void HandleInput()
        {
            // F9 - Toggle recording
            if (Input.GetKeyDown(KeyCode.F9))
            {
                if (!isRecording)
                    StartRecording();
                else
                    StopRecording();
            }

            // F10 - Start manual playback (preview current recording)
            if (Input.GetKeyDown(KeyCode.F10))
            {
                if (!isRecording && recording.Count > 0 && !isPlaying)
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
            if (Time.timeScale < 0.01f)
            {
                Log("Cannot start recording while paused");
                ScreenMessage("Cannot record while paused", 2f);
                return;
            }

            if (FlightGlobals.ActiveVessel == null)
            {
                Log("No active vessel to record!");
                return;
            }

            recording.Clear();
            orbitSegments.Clear();
            isRecording = true;
            isOnRails = false;
            vesselDestroyedDuringRecording = false;
            recordingVesselId = FlightGlobals.ActiveVessel.persistentId;
            recordingStartUT = Planetarium.GetUniversalTime();
            lastRecordedUT = -1;
            lastRecordedVelocity = Vector3.zero;

            // Check if vessel is already on rails (e.g. started recording during time warp)
            if (FlightGlobals.ActiveVessel.packed)
            {
                var v = FlightGlobals.ActiveVessel;
                // Take one boundary point first
                SamplePosition();

                currentOrbitSegment = new OrbitSegment
                {
                    startUT = Planetarium.GetUniversalTime(),
                    inclination = v.orbit.inclination,
                    eccentricity = v.orbit.eccentricity,
                    semiMajorAxis = v.orbit.semiMajorAxis,
                    longitudeOfAscendingNode = v.orbit.LAN,
                    argumentOfPeriapsis = v.orbit.argumentOfPeriapsis,
                    meanAnomalyAtEpoch = v.orbit.meanAnomalyAtEpoch,
                    epoch = v.orbit.epoch,
                    bodyName = v.mainBody.name
                };
                isOnRails = true;
                Log($"Recording started on rails — capturing orbit (body={v.mainBody.name})");
            }
            else
            {
                // Start sampling (fast tick — adaptive logic decides whether to record)
                InvokeRepeating(nameof(SamplePosition), 0f, sampleTickInterval);
            }

            Log("Recording started (adaptive sampling)");
            ScreenMessage("Recording STARTED", 2f);
        }

        public void StopRecording()
        {
            // Finalize in-progress orbit segment
            if (isOnRails)
            {
                currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
                orbitSegments.Add(currentOrbitSegment);
                isOnRails = false;

                // Record a boundary point at stop time
                SamplePosition();
            }

            CancelInvoke(nameof(SamplePosition));
            isRecording = false;

            double duration = recording.Count > 0
                ? recording[recording.Count - 1].ut - recording[0].ut
                : 0;

            Log($"Recording stopped. {recording.Count} points, {orbitSegments.Count} orbit segments over {duration:F1}s");
            ScreenMessage($"Recording STOPPED: {recording.Count} points", 3f);
        }

        public void ClearRecording()
        {
            recording.Clear();
            orbitSegments.Clear();
            lastPlaybackIndex = 0;
            Log("Recording cleared");
        }

        void SamplePosition()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;

            // Safety net: don't sample while on rails (orbit segment handles this range)
            if (isOnRails) return;

            if (v.persistentId != recordingVesselId)
            {
                CancelInvoke(nameof(SamplePosition));
                isRecording = false;
                Log($"Active vessel changed during recording (was pid={recordingVesselId}, now pid={v.persistentId}) — auto-stopping");
                ScreenMessage("Recording stopped — vessel changed", 3f);
                return;
            }

            // Adaptive threshold: skip if nothing meaningful changed
            Vector3 currentVelocity = v.GetSrfVelocity();
            if (!ShouldRecordPoint(currentVelocity, lastRecordedVelocity,
                Planetarium.GetUniversalTime(), lastRecordedUT,
                maxSampleInterval, velocityDirThreshold, speedChangeThreshold))
                return;

            TrajectoryPoint point = new TrajectoryPoint
            {
                ut = Planetarium.GetUniversalTime(),
                latitude = v.latitude,
                longitude = v.longitude,
                altitude = v.altitude,
                rotation = v.transform.rotation,
                velocity = v.GetSrfVelocity(),
                bodyName = v.mainBody.name,
                funds = Funding.Instance != null ? Funding.Instance.Funds : 0,
                science = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0,
                reputation = Reputation.Instance != null ? Reputation.CurrentRep : 0
            };

            recording.Add(point);
            lastRecordedUT = point.ut;
            lastRecordedVelocity = point.velocity;

            // Debug: Log every 10th point
            if (recording.Count % 10 == 0)
            {
                Log($"Recorded point #{recording.Count}: {point}");
            }
        }

        // Delegates to TrajectoryMath — kept for backward compatibility
        internal static bool ShouldRecordPoint(
            Vector3 currentVelocity, Vector3 lastVelocity,
            double currentUT, double lastRecordedUT,
            float maxInterval, float velDirThreshold, float speedThreshold)
        {
            return TrajectoryMath.ShouldRecordPoint(currentVelocity, lastVelocity,
                currentUT, lastRecordedUT, maxInterval, velDirThreshold, speedThreshold);
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

            ghostObject = CreateGhostSphere("Parsek_Ghost_Preview", Color.green);
            ghostMaterial = ghostObject.GetComponent<Renderer>()?.material;

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

            if (ghostMaterial != null)
            {
                Destroy(ghostMaterial);
                ghostMaterial = null;
            }

            if (ghostObject != null)
            {
                Destroy(ghostObject);
                ghostObject = null;
            }

            orbitCache.Clear();
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
            if (committed.Count > 0 && TimeWarp.CurrentRate > 1f && lastTimelineUT >= 0)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    var rec = committed[i];
                    if (rec.Points.Count < 2) continue;
                    if (rec.VesselSnapshot == null || rec.VesselSpawned) continue;

                    bool crossedInto = lastTimelineUT < rec.StartUT && currentUT >= rec.StartUT;
                    bool approaching = currentUT < rec.StartUT &&
                                       currentUT + TimeWarp.CurrentRate >= rec.StartUT;
                    if (crossedInto || approaching)
                    {
                        TimeWarp.SetRate(0, true);
                        Log($"Stopped time warp for recording #{i} ({rec.VesselName}) ghost playback");
                        ScreenMessage($"Time warp stopped — '{rec.VesselName}' playback", 3f);
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
                bool needsSpawn = rec.VesselSnapshot != null && !rec.VesselSpawned;

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
                        SpawnTimelineGhost(i, rec);

                    int playbackIdx = 0;
                    if (timelinePlaybackIndices.ContainsKey(i))
                        playbackIdx = timelinePlaybackIndices[i];

                    InterpolateAndPosition(timelineGhosts[i], rec.Points, rec.OrbitSegments,
                        ref playbackIdx, currentUT, i * 100);
                    timelinePlaybackIndices[i] = playbackIdx;

                    ApplyResourceDeltas(rec, currentUT);
                }
                else if (pastEnd && needsSpawn && ghostActive)
                {
                    // Ghost was playing, UT just crossed EndUT — hold at final pos, spawn, despawn ghost
                    PositionGhostAt(timelineGhosts[i], rec.Points[rec.Points.Count - 1]);
                    VesselSpawner.SpawnOrRecoverIfTooClose(rec, i);
                    DestroyTimelineGhost(i);
                }
                else if (pastEnd && needsSpawn && !ghostActive)
                {
                    // UT already past EndUT on scene load — spawn immediately, no ghost
                    VesselSpawner.SpawnOrRecoverIfTooClose(rec, i);
                }
                else
                {
                    // Outside time range, no spawn needed — despawn ghost if active
                    if (ghostActive)
                        DestroyTimelineGhost(i);
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
            var points = rec.Points;

            // Find the highest point index whose UT we've passed
            int targetIndex = rec.LastAppliedResourceIndex;
            for (int j = rec.LastAppliedResourceIndex + 1; j < points.Count; j++)
            {
                if (points[j].ut <= currentUT)
                    targetIndex = j;
                else
                    break;
            }

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
            }
        }

        void SpawnTimelineGhost(int index, RecordingStore.Recording rec)
        {
            Log($"Spawning timeline ghost #{index} for {rec.VesselName}");

            // Use a slightly different color to distinguish from manual preview
            Color ghostColor = new Color(0.2f, 1f, 0.4f, 0.8f); // bright green-cyan
            GameObject ghost = CreateGhostSphere($"Parsek_Timeline_{index}", ghostColor);

            timelineGhosts[index] = ghost;
            timelineGhostMaterials[index] = ghost.GetComponent<Renderer>()?.material;
            timelinePlaybackIndices[index] = 0;
        }

        void DestroyTimelineGhost(int index)
        {
            Log($"Despawning timeline ghost #{index}");

            if (timelineGhostMaterials.ContainsKey(index) && timelineGhostMaterials[index] != null)
            {
                Destroy(timelineGhostMaterials[index]);
                timelineGhostMaterials.Remove(index);
            }

            if (timelineGhosts.ContainsKey(index) && timelineGhosts[index] != null)
            {
                Destroy(timelineGhosts[index]);
                timelineGhosts.Remove(index);
            }

            timelinePlaybackIndices.Remove(index);
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
                return;
            }

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
                    PositionGhostFromOrbit(ghost, seg.Value, targetUT, orbitCacheBase + segIdx);
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

        void Log(string message)
        {
            Debug.Log($"[Parsek] {message}");
        }

        void ScreenMessage(string message, float duration)
        {
            ScreenMessages.PostScreenMessage(
                $"[Parsek] {message}",
                duration,
                ScreenMessageStyle.UPPER_CENTER
            );
        }

        #endregion
    }
}
