using System;
using System.Collections.Generic;
using ClickThroughFix;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Parsek Spike Prototype — Phase 2
    ///
    /// Purpose: Validate recording persistence across reverts and
    /// automatic timeline playback at original UT timestamps.
    ///
    /// Controls:
    ///   F9  - Start/Stop recording
    ///   F10 - Start manual playback of current recording (preview)
    ///   F11 - Stop manual playback
    ///
    /// Timeline: Committed recordings auto-play when UT enters their time range.
    /// On revert/scene change, pending recordings trigger a merge dialog.
    ///
    /// This is a throwaway prototype. Delete after validating concept.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ParsekSpike : MonoBehaviour
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

        // Map view markers
        private GUIStyle mapMarkerStyle;
        private Texture2D mapMarkerTexture;

        #endregion

        #region Unity Lifecycle

        void Start()
        {
            Log("Parsek Spike loaded (Phase 2 — timeline persistence).");
            Log("Press F9 to record, F10 to preview playback.");

            GameEvents.onGameSceneLoadRequested.Add(OnSceneChangeRequested);
            GameEvents.onFlightReady.Add(OnFlightReady);
            GameEvents.onVesselWillDestroy.Add(OnVesselWillDestroy);
            GameEvents.onVesselSituationChange.Add(OnVesselSituationChange);
            GameEvents.onCrewOnEva.Add(OnCrewOnEva);
            GameEvents.onVesselGoOnRails.Add(OnVesselGoOnRails);
            GameEvents.onVesselGoOffRails.Add(OnVesselGoOffRails);
            GameEvents.onVesselSOIChanged.Add(OnVesselSOIChanged);

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
                DrawMapMarkers();

            if (showUI)
            {
                windowRect = ClickThruBlocker.GUILayoutWindow(
                    GetInstanceID(),
                    windowRect,
                    DrawWindow,
                    "Parsek Spike",
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

            if (mapMarkerTexture != null)
                Destroy(mapMarkerTexture);
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
                SnapshotVessel(RecordingStore.Pending);

                recording.Clear();
                orbitSegments.Clear();
                lastPlaybackIndex = 0;
                vesselDestroyedDuringRecording = false;
            }

            // Stop manual playback
            StopPlayback();
            DestroyAllTimelineGhosts();
        }

        void SnapshotVessel(RecordingStore.Recording pending)
        {
            if (pending == null || pending.Points.Count == 0) return;

            // Compute distance from launch
            var firstPoint = pending.Points[0];
            CelestialBody bodyFirst = FlightGlobals.Bodies?.Find(b => b.name == firstPoint.bodyName);

            if (vesselDestroyedDuringRecording)
            {
                pending.VesselDestroyed = true;
                pending.VesselSnapshot = null;

                // Use last recorded point for distance (may be a different SOI)
                var lastPoint = pending.Points[pending.Points.Count - 1];
                CelestialBody bodyLast = FlightGlobals.Bodies?.Find(b => b.name == lastPoint.bodyName);
                if (bodyFirst != null && bodyLast != null)
                {
                    Vector3d launchPos = bodyFirst.GetWorldSurfacePosition(
                        firstPoint.latitude, firstPoint.longitude, firstPoint.altitude);
                    Vector3d lastPos = bodyLast.GetWorldSurfacePosition(
                        lastPoint.latitude, lastPoint.longitude, lastPoint.altitude);
                    pending.DistanceFromLaunch = Vector3d.Distance(launchPos, lastPos);
                }

                // Compute max distance from launch across all recorded points
                if (bodyFirst != null)
                {
                    Vector3d launchPos = bodyFirst.GetWorldSurfacePosition(
                        firstPoint.latitude, firstPoint.longitude, firstPoint.altitude);
                    double maxDist = 0;
                    for (int i = 1; i < pending.Points.Count; i++)
                    {
                        var pt = pending.Points[i];
                        CelestialBody bodyPt = FlightGlobals.Bodies?.Find(b => b.name == pt.bodyName);
                        if (bodyPt == null) continue;
                        Vector3d ptPos = bodyPt.GetWorldSurfacePosition(pt.latitude, pt.longitude, pt.altitude);
                        double d = Vector3d.Distance(launchPos, ptPos);
                        if (d > maxDist) maxDist = d;
                    }
                    pending.MaxDistanceFromLaunch = maxDist;
                }

                pending.VesselSituation = "Destroyed";
                Log($"Vessel was destroyed during recording. Distance from launch: {pending.DistanceFromLaunch:F0}m, " +
                    $"Max distance: {pending.MaxDistanceFromLaunch:F0}m");
                return;
            }

            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                pending.VesselDestroyed = true;
                pending.VesselSnapshot = null;
                pending.VesselSituation = "Unknown (no active vessel)";
                Log("No active vessel at snapshot time");
                return;
            }

            // Compute distance from launch position
            // Use vessel.GetWorldPos3D() for current position — always correct regardless of SOI
            if (bodyFirst != null)
            {
                Vector3d launchPos = bodyFirst.GetWorldSurfacePosition(
                    firstPoint.latitude, firstPoint.longitude, firstPoint.altitude);
                Vector3d currentPos = vessel.GetWorldPos3D();
                pending.DistanceFromLaunch = Vector3d.Distance(launchPos, currentPos);

                // Compute max distance from launch across all recorded points
                double maxDist = 0;
                for (int i = 1; i < pending.Points.Count; i++)
                {
                    var pt = pending.Points[i];
                    CelestialBody bodyPt = FlightGlobals.Bodies?.Find(b => b.name == pt.bodyName);
                    if (bodyPt == null) continue;
                    Vector3d ptPos = bodyPt.GetWorldSurfacePosition(pt.latitude, pt.longitude, pt.altitude);
                    double d = Vector3d.Distance(launchPos, ptPos);
                    if (d > maxDist) maxDist = d;
                }
                pending.MaxDistanceFromLaunch = maxDist;
            }

            // Snapshot the vessel (works for regular vessels and EVA kerbals)
            if (!vessel.loaded)
                Log("Warning: Active vessel is unloaded at snapshot time — snapshot may be incomplete");
            ProtoVessel pv = vessel.BackupVessel();
            ConfigNode node = new ConfigNode("VESSEL");
            pv.Save(node);
            pending.VesselSnapshot = node;
            pending.VesselDestroyed = false;

            // Build situation string
            pending.VesselSituation = vessel.isEVA
                ? $"EVA {vessel.mainBody.name}"
                : $"{vessel.situation} {vessel.mainBody.name}";

            Log($"Vessel snapshot taken. Distance from launch: {pending.DistanceFromLaunch:F0}m, " +
                $"Max distance: {pending.MaxDistanceFromLaunch:F0}m, Situation: {pending.VesselSituation}");
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
                ShowMergeDialog(pending);
            }

            // Swap reserved crew out of the active vessel so the player
            // can't record with them again (they belong to deferred-spawn vessels)
            ParsekScenario.SwapReservedCrewInFlight();

            Log($"Timeline has {RecordingStore.CommittedRecordings.Count} committed recording(s)");
        }

        void ShowMergeDialog(RecordingStore.Recording pending)
        {
            double duration = pending.EndUT - pending.StartUT;
            var recommended = RecordingStore.GetRecommendedAction(
                pending.DistanceFromLaunch, pending.VesselDestroyed,
                pending.VesselSnapshot != null,
                duration, pending.MaxDistanceFromLaunch);

            Log($"Merge dialog: distance={pending.DistanceFromLaunch:F0}m, " +
                $"maxDistance={pending.MaxDistanceFromLaunch:F0}m, duration={duration:F1}s, " +
                $"destroyed={pending.VesselDestroyed}, hasSnapshot={pending.VesselSnapshot != null}, " +
                $"recommended={recommended}");

            DialogGUIButton[] buttons;

            switch (recommended)
            {
                case RecordingStore.MergeDefault.MergeOnly:
                    // Vessel destroyed or no snapshot — no vessel to persist
                    buttons = new[]
                    {
                        new DialogGUIButton("Merge to Timeline", () =>
                        {
                            RecordingStore.CommitPending();
                            ScreenMessage("Recording merged to timeline!", 3f);
                            Log("User chose: Merge to Timeline (vessel destroyed)");
                        }),
                        new DialogGUIButton("Discard", () =>
                        {
                            ParsekScenario.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                            RecordingStore.DiscardPending();
                            ScreenMessage("Recording discarded", 2f);
                            Log("User chose: Discard");
                        })
                    };
                    break;

                default: // Recover or Persist — vessel intact with snapshot
                    buttons = new[]
                    {
                        new DialogGUIButton("Merge + Keep Vessel", () =>
                        {
                            // Defer spawn — vessel appears when ghost finishes at EndUT
                            RecordingStore.CommitPending();
                            ParsekScenario.ReserveSnapshotCrew();
                            ParsekScenario.SwapReservedCrewInFlight();
                            ScreenMessage("Recording merged — vessel will appear after ghost playback", 3f);
                            Log("User chose: Merge + Keep Vessel (deferred spawn)");
                        }),
                        new DialogGUIButton("Merge + Recover", () =>
                        {
                            RecordingStore.CommitPending();
                            if (pending.VesselSnapshot != null)
                            {
                                ParsekScenario.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                                RecoverVessel(pending.VesselSnapshot);
                            }
                            // Clear snapshot so ghost despawns normally at EndUT
                            pending.VesselSnapshot = null;
                            ScreenMessage("Recording merged, vessel recovered!", 3f);
                            Log("User chose: Merge + Recover");
                        }),
                        new DialogGUIButton("Discard", () =>
                        {
                            ParsekScenario.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                            RecordingStore.DiscardPending();
                            ScreenMessage("Recording discarded", 2f);
                            Log("User chose: Discard");
                        })
                    };
                    break;
            }

            string message = BuildMergeMessage(pending, duration, recommended);

            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekMerge",
                    message,
                    "Parsek — Merge Recording",
                    HighLogic.UISkin,
                    buttons
                ),
                false,
                HighLogic.UISkin
            );
        }

        string BuildMergeMessage(RecordingStore.Recording pending, double duration,
            RecordingStore.MergeDefault recommended)
        {
            string header = $"Vessel: {pending.VesselName}\n" +
                $"Points: {pending.Points.Count}\n" +
                $"Duration: {duration:F1}s\n" +
                $"Distance from launch: {pending.DistanceFromLaunch:F0}m\n\n";

            switch (recommended)
            {
                case RecordingStore.MergeDefault.Recover:
                    return header + "Your vessel hasn't moved far from the launch site.";

                case RecordingStore.MergeDefault.MergeOnly:
                    return header + "Your vessel was destroyed. Recording captured.";

                default: // Persist
                    string situation = pending.DistanceFromLaunch < 100.0
                        ? "Your vessel returned near the launch site after traveling " +
                          $"{pending.MaxDistanceFromLaunch:F0}m."
                        : $"Your vessel is {pending.VesselSituation}.";
                    return header + situation + "\nIt will persist in the timeline.";
            }
        }

        uint RespawnVessel(ConfigNode vesselNode)
        {
            try
            {
                // Use a copy to avoid modifying the saved snapshot
                ConfigNode spawnNode = vesselNode.CreateCopy();

                // Remove dead/missing crew from snapshot to avoid resurrecting them
                RemoveDeadCrewFromSnapshot(spawnNode);

                // Set reserved crew back to Available so KSP can assign them to this vessel
                ParsekScenario.UnreserveCrewInSnapshot(spawnNode);

                ProtoVessel pv = new ProtoVessel(spawnNode, HighLogic.CurrentGame);
                HighLogic.CurrentGame.flightState.protoVessels.Add(pv);
                pv.Load(HighLogic.CurrentGame.flightState);
                Log($"Vessel respawned with crew (sit={spawnNode.GetValue("sit")}, pid={pv.persistentId})");
                return pv.persistentId;
            }
            catch (System.Exception ex)
            {
                Log($"Failed to respawn vessel: {ex.Message}");
                return 0;
            }
        }

        void SpawnOrRecoverIfTooClose(RecordingStore.Recording rec, int index)
        {
            if (rec.Points.Count == 0 || FlightGlobals.Vessels == null)
            {
                rec.SpawnedVesselPersistentId = RespawnVessel(rec.VesselSnapshot);
                rec.VesselSpawned = true;
                return;
            }

            var lastPt = rec.Points[rec.Points.Count - 1];
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == lastPt.bodyName);
            if (body == null)
            {
                rec.SpawnedVesselPersistentId = RespawnVessel(rec.VesselSnapshot);
                rec.VesselSpawned = true;
                return;
            }

            Vector3d spawnPos = body.GetWorldSurfacePosition(
                lastPt.latitude, lastPt.longitude, lastPt.altitude);

            // Check proximity against all loaded vessels on the same body
            Vector3d closestPos = Vector3d.zero;
            double closestDist = double.MaxValue;
            for (int v = 0; v < FlightGlobals.Vessels.Count; v++)
            {
                Vessel other = FlightGlobals.Vessels[v];
                if (other.mainBody != body) continue;
                double dist = Vector3d.Distance(spawnPos, other.GetWorldPos3D());
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestPos = other.GetWorldPos3D();
                }
            }

            // Also check final positions of other committed recordings that have
            // already spawned (may not be in FlightGlobals yet) or will spawn later,
            // to prevent overlapping spawn positions.
            var committed = RecordingStore.CommittedRecordings;
            for (int r = 0; r < committed.Count; r++)
            {
                if (r == index) continue;
                var other = committed[r];
                if (other.VesselSnapshot == null) continue;
                if (other.Points.Count == 0) continue;
                var otherLastPt = other.Points[other.Points.Count - 1];
                CelestialBody otherBody = FlightGlobals.Bodies?.Find(b => b.name == otherLastPt.bodyName);
                if (otherBody != body) continue;
                Vector3d otherPos = otherBody.GetWorldSurfacePosition(
                    otherLastPt.latitude, otherLastPt.longitude, otherLastPt.altitude);
                double dist = Vector3d.Distance(spawnPos, otherPos);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestPos = otherPos;
                }
            }

            if (closestDist < 200.0)
            {
                // Offset away from the closest vessel
                Vector3d direction = (spawnPos - closestPos).normalized;
                if (direction.magnitude < 0.001)
                    direction = body.GetSurfaceNVector(lastPt.latitude, lastPt.longitude);
                Vector3d offsetPos = closestPos + direction * 250.0;

                double newLat = body.GetLatitude(offsetPos);
                double newLon = body.GetLongitude(offsetPos);
                double newAlt = body.GetAltitude(offsetPos);

                rec.VesselSnapshot.SetValue("lat", newLat.ToString("R"));
                rec.VesselSnapshot.SetValue("lon", newLon.ToString("R"));
                rec.VesselSnapshot.SetValue("alt", newAlt.ToString("R"));

                Log($"Offset vessel #{index} ({rec.VesselName}) from {closestDist:F0}m to 250m from nearest vessel/recording");
            }

            rec.SpawnedVesselPersistentId = RespawnVessel(rec.VesselSnapshot);
            rec.VesselSpawned = true;
            Log($"Vessel spawn for recording #{index} ({rec.VesselName})");
            ScreenMessage($"Vessel '{rec.VesselName}' has appeared!", 4f);
        }

        void RecoverVessel(ConfigNode vesselNode)
        {
            try
            {
                ProtoVessel pv = new ProtoVessel(vesselNode, HighLogic.CurrentGame);
                ShipConstruction.RecoverVesselFromFlight(pv, HighLogic.CurrentGame.flightState, true);
                Log($"Vessel recovered for funds");
            }
            catch (System.Exception ex)
            {
                Log($"Failed to recover vessel: {ex.Message}");
            }
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

        void StartRecording()
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

        void StopRecording()
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

        /// <summary>
        /// Decides whether to record a trajectory point based on velocity changes
        /// and a max-interval backstop. Pure function for testability.
        /// </summary>
        internal static bool ShouldRecordPoint(
            Vector3 currentVelocity, Vector3 lastVelocity,
            double currentUT, double lastRecordedUT,
            float maxInterval, float velDirThreshold, float speedThreshold)
        {
            // Always record the first point
            if (lastRecordedUT < 0)
                return true;

            // Max interval backstop — always record after this long
            if (currentUT - lastRecordedUT >= maxInterval)
                return true;

            float currentSpeed = currentVelocity.magnitude;
            float lastSpeed = lastVelocity.magnitude;

            // Velocity direction change (guard against zero vectors to avoid NaN)
            if (currentSpeed > 0.1f && lastSpeed > 0.1f)
            {
                float angle = Vector3.Angle(currentVelocity, lastVelocity);
                if (angle > velDirThreshold)
                    return true;
            }

            // Speed change (relative to last speed, with floor to avoid div-by-near-zero)
            float speedDelta = Mathf.Abs(currentSpeed - lastSpeed);
            float reference = Mathf.Max(lastSpeed, 0.1f);
            if (speedDelta / reference > speedThreshold)
                return true;

            return false;
        }

        #endregion

        #region Manual Playback (F10/F11 preview)

        void StartPlayback()
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

        void StopPlayback()
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
                    VesselExistsByPid(rec.SpawnedVesselPersistentId))
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
                    SpawnOrRecoverIfTooClose(rec, i);
                    DestroyTimelineGhost(i);
                }
                else if (pastEnd && needsSpawn && !ghostActive)
                {
                    // UT already past EndUT on scene load — spawn immediately, no ghost
                    SpawnOrRecoverIfTooClose(rec, i);
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

        void DestroyAllTimelineGhosts()
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
            int indexBefore = FindWaypointIndex(points, ref cachedIndex, targetUT);

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

        /// <summary>
        /// Find the waypoint index for interpolation using cached lookup.
        /// Parameterized to work with any point list + cached index.
        /// </summary>
        internal int FindWaypointIndex(List<TrajectoryPoint> points, ref int cachedIndex, double targetUT)
        {
            if (points.Count < 2)
                return -1;

            if (targetUT < points[0].ut)
                return -1;

            if (targetUT >= points[points.Count - 1].ut)
                return points.Count - 2;

            // Try cached index first (common case: sequential playback)
            if (cachedIndex >= 0 && cachedIndex < points.Count - 1)
            {
                if (points[cachedIndex].ut <= targetUT &&
                    points[cachedIndex + 1].ut > targetUT)
                {
                    return cachedIndex;
                }

                int nextIndex = cachedIndex + 1;
                if (nextIndex < points.Count - 1 &&
                    points[nextIndex].ut <= targetUT &&
                    points[nextIndex + 1].ut > targetUT)
                {
                    cachedIndex = nextIndex;
                    return nextIndex;
                }
            }

            // Binary search fallback
            int low = 0;
            int high = points.Count - 2;

            while (low <= high)
            {
                int mid = (low + high) / 2;

                if (points[mid].ut <= targetUT && points[mid + 1].ut > targetUT)
                {
                    cachedIndex = mid;
                    return mid;
                }
                else if (points[mid].ut > targetUT)
                {
                    high = mid - 1;
                }
                else
                {
                    low = mid + 1;
                }
            }

            // Linear fallback (shouldn't reach here)
            for (int i = 0; i < points.Count - 1; i++)
            {
                if (points[i].ut <= targetUT && points[i + 1].ut > targetUT)
                {
                    cachedIndex = i;
                    return i;
                }
            }

            return -1;
        }

        // Keep the old signature for backward compat with tests
        internal int FindWaypointIndex(double targetUT)
        {
            return FindWaypointIndex(recording, ref lastPlaybackIndex, targetUT);
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

        /// <summary>
        /// Find an orbit segment that covers the given UT. Returns null if none match.
        /// Linear scan — the list is tiny (typically 0-3 segments per recording).
        /// </summary>
        internal static OrbitSegment? FindOrbitSegment(List<OrbitSegment> segments, double ut)
        {
            if (segments == null) return null;
            for (int i = 0; i < segments.Count; i++)
            {
                if (ut >= segments[i].startUT && ut <= segments[i].endUT)
                    return segments[i];
            }
            return null;
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

        internal Quaternion SanitizeQuaternion(Quaternion q)
        {
            if (float.IsNaN(q.x) || float.IsInfinity(q.x)) q.x = 0;
            if (float.IsNaN(q.y) || float.IsInfinity(q.y)) q.y = 0;
            if (float.IsNaN(q.z) || float.IsInfinity(q.z)) q.z = 0;
            if (float.IsNaN(q.w) || float.IsInfinity(q.w)) q.w = 1;

            float magnitude = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (float.IsNaN(magnitude) || float.IsInfinity(magnitude) || magnitude < 0.001f)
            {
                return Quaternion.identity;
            }

            return new Quaternion(q.x / magnitude, q.y / magnitude, q.z / magnitude, q.w / magnitude);
        }

        #endregion

        #region Map View Markers

        void DrawMapMarkers()
        {
            Camera cam = PlanetariumCamera.Camera;
            if (cam == null) return;

            EnsureMapMarkerResources();

            // Manual preview ghost
            if (isPlaying && ghostObject != null)
            {
                DrawMapMarkerAt(cam, ghostObject.transform.position, "Preview", Color.green);
            }

            // Timeline ghosts
            var committed = RecordingStore.CommittedRecordings;
            Color ghostColor = new Color(0.2f, 1f, 0.4f, 0.9f);
            foreach (var kvp in timelineGhosts)
            {
                if (kvp.Value == null) continue;
                string name = kvp.Key < committed.Count ? committed[kvp.Key].VesselName : "Ghost";
                DrawMapMarkerAt(cam, kvp.Value.transform.position, name, ghostColor);
            }
        }

        void DrawMapMarkerAt(Camera cam, Vector3 worldPos, string label, Color color)
        {
            Vector3d scaledPos = ScaledSpace.LocalToScaledSpace(worldPos);
            Vector3 screenPos = cam.WorldToScreenPoint(scaledPos);

            // Behind camera
            if (screenPos.z < 0) return;

            // GUI coordinates (Y inverted)
            float x = screenPos.x;
            float y = Screen.height - screenPos.y;

            // Draw marker dot
            Color prevColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(x - 5, y - 5, 10, 10), mapMarkerTexture);
            GUI.color = prevColor;

            // Draw vessel name label
            mapMarkerStyle.normal.textColor = color;
            GUI.Label(new Rect(x - 75, y + 7, 150, 20), label, mapMarkerStyle);
        }

        void EnsureMapMarkerResources()
        {
            if (mapMarkerTexture == null)
            {
                int size = 10;
                mapMarkerTexture = new Texture2D(size, size, TextureFormat.ARGB32, false);
                float center = size / 2f;
                float radius = size / 2f - 1f;
                for (int py = 0; py < size; py++)
                {
                    for (int px = 0; px < size; px++)
                    {
                        float dist = Mathf.Sqrt((px - center) * (px - center) + (py - center) * (py - center));
                        mapMarkerTexture.SetPixel(px, py, dist <= radius ? Color.white : Color.clear);
                    }
                }
                mapMarkerTexture.Apply();
            }

            if (mapMarkerStyle == null)
            {
                mapMarkerStyle = new GUIStyle(GUI.skin.label);
                mapMarkerStyle.fontSize = 11;
                mapMarkerStyle.fontStyle = FontStyle.Bold;
                mapMarkerStyle.alignment = TextAnchor.UpperCenter;
            }
        }

        #endregion

        #region UI

        void DrawWindow(int windowID)
        {
            GUILayout.BeginVertical();

            // Status
            GUILayout.Label($"Status: {GetStatusText()}");
            GUILayout.Space(5);

            // Recording info
            GUILayout.Label($"Recorded Points: {recording.Count}");
            if (recording.Count > 0)
            {
                double duration = recording[recording.Count - 1].ut - recording[0].ut;
                GUILayout.Label($"Duration: {duration:F1}s");
            }

            // Timeline info
            int committedCount = RecordingStore.CommittedRecordings.Count;
            int activeGhosts = timelineGhosts.Count;
            GUILayout.Label($"Timeline: {committedCount} recording(s), {activeGhosts} active ghost(s)");

            GUILayout.Space(10);

            // Controls
            GUILayout.Label("Controls:");
            GUILayout.Label("  F9  - Start/Stop Recording");
            GUILayout.Label("  F10 - Preview Playback");
            GUILayout.Label("  F11 - Stop Preview");

            GUILayout.Space(10);

            // Buttons
            GUILayout.BeginHorizontal();

            if (!isRecording)
            {
                if (GUILayout.Button("Start Recording"))
                    StartRecording();
            }
            else
            {
                if (GUILayout.Button("Stop Recording"))
                    StopRecording();
            }

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();

            GUI.enabled = !isRecording && recording.Count > 0 && !isPlaying;
            if (GUILayout.Button("Preview Playback"))
                StartPlayback();

            GUI.enabled = isPlaying;
            if (GUILayout.Button("Stop Preview"))
                StopPlayback();

            GUI.enabled = true;

            GUILayout.EndHorizontal();

            // Clear buttons
            GUILayout.Space(5);
            GUI.enabled = !isRecording && !isPlaying && recording.Count > 0;
            if (GUILayout.Button("Clear Current Recording"))
            {
                recording.Clear();
                orbitSegments.Clear();
                lastPlaybackIndex = 0;
                Log("Recording cleared");
            }

            GUI.enabled = activeGhosts > 0;
            if (GUILayout.Button($"Despawn Ghosts ({activeGhosts})"))
            {
                DestroyAllTimelineGhosts();
                Log("Ghosts despawned");
            }

            GUI.enabled = committedCount > 0;
            if (GUILayout.Button($"Wipe Recordings ({committedCount})"))
            {
                // Unreserve crew from all recordings before wiping
                foreach (var rec in RecordingStore.CommittedRecordings)
                    ParsekScenario.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                ParsekScenario.ClearReplacements();
                DestroyAllTimelineGhosts();
                RecordingStore.CommittedRecordings.Clear();
                Log("All recordings wiped");
                ScreenMessage("All recordings wiped", 2f);
            }
            GUI.enabled = true;

            GUILayout.EndVertical();

            // Make window draggable
            GUI.DragWindow();
        }

        string GetStatusText()
        {
            if (isRecording) return "RECORDING";
            if (isPlaying) return "PREVIEWING";
            if (recording.Count > 0) return "Ready (has recording)";
            return "Idle";
        }

        #endregion

        #region Utilities

        void RemoveDeadCrewFromSnapshot(ConfigNode snapshot)
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                var crewNames = partNode.GetValues("crew");
                if (crewNames.Length == 0) continue;

                // Check if any crew are dead/missing
                var keepNames = new List<string>();
                bool removedAny = false;
                foreach (string name in crewNames)
                {
                    bool isDead = false;
                    foreach (ProtoCrewMember pcm in roster.Crew)
                    {
                        if (pcm.name == name &&
                            (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Dead ||
                             pcm.rosterStatus == ProtoCrewMember.RosterStatus.Missing))
                        {
                            isDead = true;
                            Log($"Removed dead/missing crew '{name}' from vessel snapshot");
                            break;
                        }
                    }
                    if (isDead)
                        removedAny = true;
                    else
                        keepNames.Add(name);
                }

                if (removedAny)
                {
                    partNode.RemoveValues("crew");
                    foreach (string name in keepNames)
                        partNode.AddValue("crew", name);
                }
            }
        }

        bool VesselExistsByPid(uint pid)
        {
            if (FlightGlobals.Vessels == null) return false;
            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                if (FlightGlobals.Vessels[i].persistentId == pid)
                    return true;
            }
            return false;
        }

        void Log(string message)
        {
            Debug.Log($"[Parsek Spike] {message}");
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
