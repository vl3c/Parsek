using System;
using System.Collections.Generic;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Owns all recording state and sampling logic.
    /// Called each physics frame by the Harmony postfix on VesselPrecalculate.
    /// </summary>
    public class FlightRecorder
    {
        // Recording output
        public List<TrajectoryPoint> Recording { get; } = new List<TrajectoryPoint>();
        public List<OrbitSegment> OrbitSegments { get; } = new List<OrbitSegment>();
        public bool IsRecording { get; private set; }
        public uint RecordingVesselId { get; private set; }
        public bool VesselDestroyedDuringRecording { get; set; }
        public RecordingStore.Recording CaptureAtStop { get; private set; }

        // Adaptive sampling thresholds
        private const float maxSampleInterval = 3.0f;
        private const float velocityDirThreshold = 2.0f;
        private const float speedChangeThreshold = 0.05f;
        private const double snapshotRefreshIntervalUT = 10.0;
        private const float snapshotPerfLogThresholdMs = 25.0f;
        private double lastRecordedUT = -1;
        private Vector3 lastRecordedVelocity;
        private ConfigNode lastGoodVesselSnapshot;
        private double lastSnapshotRefreshUT = double.MinValue;

        // On-rails state
        private bool isOnRails;
        private OrbitSegment currentOrbitSegment;

        public void StartRecording()
        {
            if (Time.timeScale < 0.01f)
            {
                ParsekLog.Log("Cannot start recording while paused");
                ParsekLog.ScreenMessage("Cannot record while paused", 2f);
                return;
            }

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null)
            {
                ParsekLog.Log("No active vessel to record!");
                return;
            }

            Recording.Clear();
            OrbitSegments.Clear();
            IsRecording = true;
            isOnRails = false;
            VesselDestroyedDuringRecording = false;
            CaptureAtStop = null;
            RecordingVesselId = v.persistentId;
            lastRecordedUT = -1;
            lastRecordedVelocity = Vector3.zero;
            RefreshBackupSnapshot(v, "record_start", force: true);

            // Check if vessel is already on rails (e.g. started recording during time warp)
            if (v.packed)
            {
                // Take one boundary point first
                SamplePosition(v);

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
                ParsekLog.Log($"Recording started on rails — capturing orbit (body={v.mainBody.name})");
            }

            // Register the Harmony patch to call us each physics frame
            Patches.PhysicsFramePatch.ActiveRecorder = this;

            ParsekLog.Log("Recording started (physics-frame sampling)");
            ParsekLog.ScreenMessage("Recording STARTED", 2f);
        }

        public void StopRecording()
        {
            // Finalize in-progress orbit segment
            if (isOnRails)
            {
                currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
                OrbitSegments.Add(currentOrbitSegment);
                isOnRails = false;

                // Record a boundary point at stop time
                Vessel v = FlightGlobals.ActiveVessel;
                if (v != null) SamplePosition(v);
            }

            // Disconnect from Harmony patch
            Patches.PhysicsFramePatch.ActiveRecorder = null;
            IsRecording = false;

            // Capture persistence artifacts at stop-time so later scene changes
            // don't depend on whatever vessel is currently active.
            CaptureAtStop = new RecordingStore.Recording
            {
                RecordingId = System.Guid.NewGuid().ToString("N"),
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                GhostGeometryVersion = RecordingStore.CurrentGhostGeometryVersion,
                VesselName = FlightGlobals.ActiveVessel != null
                    ? FlightGlobals.ActiveVessel.vesselName
                    : "Unknown Vessel",
                Points = new List<TrajectoryPoint>(Recording),
                OrbitSegments = new List<OrbitSegment>(OrbitSegments)
            };
            VesselSpawner.SnapshotVessel(
                CaptureAtStop,
                VesselDestroyedDuringRecording,
                destroyedFallbackSnapshot: lastGoodVesselSnapshot);

            double duration = Recording.Count > 0
                ? Recording[Recording.Count - 1].ut - Recording[0].ut
                : 0;

            ParsekLog.Log($"Recording stopped. {Recording.Count} points, {OrbitSegments.Count} orbit segments over {duration:F1}s");
            ParsekLog.ScreenMessage($"Recording STOPPED: {Recording.Count} points", 3f);
        }

        /// <summary>
        /// Called by the Harmony postfix on each physics frame for the active vessel.
        /// </summary>
        public void OnPhysicsFrame(Vessel v)
        {
            if (!IsRecording) return;
            if (isOnRails) return;

            if (v.persistentId != RecordingVesselId)
            {
                // Keep recording across switch to EVA. This preserves player intent for
                // launch -> EVA sequences until multi-track replay lands.
                if (v.isEVA)
                {
                    RecordingVesselId = v.persistentId;
                    SamplePosition(v);
                    RefreshBackupSnapshot(v, "eva_switch", force: true);
                    ParsekLog.Log($"Recording switched to EVA vessel (pid={v.persistentId})");
                    return;
                }

                Vessel recordedVessel = FindVesselByPid(RecordingVesselId);

                CaptureAtStop = new RecordingStore.Recording
                {
                    RecordingId = System.Guid.NewGuid().ToString("N"),
                    RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                    GhostGeometryVersion = RecordingStore.CurrentGhostGeometryVersion,
                    VesselName = recordedVessel != null ? recordedVessel.vesselName : v.vesselName,
                    Points = new List<TrajectoryPoint>(Recording),
                    OrbitSegments = new List<OrbitSegment>(OrbitSegments)
                };
                VesselSpawner.SnapshotVessel(
                    CaptureAtStop,
                    VesselDestroyedDuringRecording || recordedVessel == null,
                    recordedVessel,
                    lastGoodVesselSnapshot);

                Patches.PhysicsFramePatch.ActiveRecorder = null;
                IsRecording = false;
                ParsekLog.Log($"Active vessel changed during recording (was pid={RecordingVesselId}, now pid={v.persistentId}) — auto-stopping");
                ParsekLog.ScreenMessage("Recording stopped — vessel changed", 3f);
                return;
            }

            // Krakensbane-corrected true velocity
            Vector3 currentVelocity = (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity());

            if (!TrajectoryMath.ShouldRecordPoint(currentVelocity, lastRecordedVelocity,
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
                velocity = currentVelocity,
                bodyName = v.mainBody.name,
                funds = Funding.Instance != null ? Funding.Instance.Funds : 0,
                science = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0,
                reputation = Reputation.Instance != null ? Reputation.CurrentRep : 0
            };

            Recording.Add(point);
            lastRecordedUT = point.ut;
            lastRecordedVelocity = point.velocity;

            RefreshBackupSnapshot(v, "periodic");

            if (Recording.Count % 10 == 0)
            {
                ParsekLog.Log($"Recorded point #{Recording.Count}: {point}");
            }
        }

        /// <summary>
        /// Force-record a boundary point (used at on-rails/off-rails transitions).
        /// </summary>
        public void SamplePosition(Vessel v)
        {
            if (v == null) return;

            Vector3 currentVelocity = v.packed
                ? (Vector3)v.obt_velocity
                : (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity());

            TrajectoryPoint point = new TrajectoryPoint
            {
                ut = Planetarium.GetUniversalTime(),
                latitude = v.latitude,
                longitude = v.longitude,
                altitude = v.altitude,
                rotation = v.transform.rotation,
                velocity = currentVelocity,
                bodyName = v.mainBody.name,
                funds = Funding.Instance != null ? Funding.Instance.Funds : 0,
                science = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0,
                reputation = Reputation.Instance != null ? Reputation.CurrentRep : 0
            };

            Recording.Add(point);
            lastRecordedUT = point.ut;
            lastRecordedVelocity = point.velocity;
        }

        public void OnVesselGoOnRails(Vessel v)
        {
            if (!IsRecording) return;
            if (v != FlightGlobals.ActiveVessel) return;
            if (v.persistentId != RecordingVesselId) return;

            // Record a boundary TrajectoryPoint at current UT (stitching point)
            SamplePosition(v);

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
            ParsekLog.Log($"Vessel went on rails — capturing orbit segment (body={v.mainBody.name})");
        }

        public void OnVesselGoOffRails(Vessel v)
        {
            if (!IsRecording || !isOnRails) return;
            if (v != FlightGlobals.ActiveVessel) return;
            if (v.persistentId != RecordingVesselId) return;

            // Finalize orbit segment
            currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
            OrbitSegments.Add(currentOrbitSegment);
            isOnRails = false;

            // Record a boundary TrajectoryPoint at current UT
            SamplePosition(v);

            ParsekLog.Log($"Vessel went off rails — orbit segment closed " +
                $"(UT {currentOrbitSegment.startUT:F0}-{currentOrbitSegment.endUT:F0})");
        }

        public void OnVesselSOIChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> data)
        {
            if (!IsRecording || !isOnRails) return;
            if (data.host != FlightGlobals.ActiveVessel) return;
            if (data.host.persistentId != RecordingVesselId) return;

            // Close current orbit segment in old SOI
            currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
            OrbitSegments.Add(currentOrbitSegment);

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

            ParsekLog.Log($"SOI changed during orbit recording: {data.from.name} → {data.to.name}");
        }

        public void OnVesselWillDestroy(Vessel v)
        {
            if (!IsRecording) return;
            if (FlightGlobals.ActiveVessel == null) return;
            if (v == FlightGlobals.ActiveVessel)
            {
                // Finalize in-progress orbit segment if on rails
                if (isOnRails)
                {
                    currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
                    OrbitSegments.Add(currentOrbitSegment);
                    isOnRails = false;
                }

                VesselDestroyedDuringRecording = true;
                RefreshBackupSnapshot(v, "destroy_event", force: true);
                ParsekLog.Log("Active vessel destroyed during recording!");
            }
        }

        /// <summary>
        /// Force-stop recording during scene change (no boundary point needed).
        /// </summary>
        public void ForceStop()
        {
            // Intentionally does not set CaptureAtStop. Forced stops happen during
            // scene transitions where vessel state may already be changing/unreliable;
            // ParsekFlight falls back to scene-change snapshot capture in that path.
            if (isOnRails)
            {
                currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
                OrbitSegments.Add(currentOrbitSegment);
                isOnRails = false;
            }

            Patches.PhysicsFramePatch.ActiveRecorder = null;
            IsRecording = false;
            ParsekLog.Log("Auto-stopped recording due to scene change");
        }

        private static Vessel FindVesselByPid(uint pid)
        {
            if (FlightGlobals.Vessels == null) return null;
            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                Vessel vessel = FlightGlobals.Vessels[i];
                if (vessel != null && vessel.persistentId == pid)
                    return vessel;
            }
            return null;
        }

        private void RefreshBackupSnapshot(Vessel vessel, string reason, bool force = false)
        {
            if (vessel == null) return;

            double ut = Planetarium.GetUniversalTime();
            if (!force && lastSnapshotRefreshUT != double.MinValue &&
                ut - lastSnapshotRefreshUT < snapshotRefreshIntervalUT)
                return;

            float startMs = Time.realtimeSinceStartup * 1000f;
            var snapshot = VesselSpawner.TryBackupSnapshot(vessel);
            float elapsedMs = Time.realtimeSinceStartup * 1000f - startMs;

            if (snapshot != null)
            {
                lastGoodVesselSnapshot = snapshot;
                lastSnapshotRefreshUT = ut;
            }

            if (elapsedMs >= snapshotPerfLogThresholdMs)
            {
                ParsekLog.Log(
                    $"Snapshot backup cost ({reason}): {elapsedMs:F1}ms " +
                    $"pid={vessel.persistentId}, points={Recording.Count}");
            }
        }
    }
}
