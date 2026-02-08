using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Static methods for spawning, recovering, and snapshotting vessels.
    /// </summary>
    public static class VesselSpawner
    {
        public static uint RespawnVessel(ConfigNode vesselNode)
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
                ParsekLog.Log($"Vessel respawned with crew (sit={spawnNode.GetValue("sit")}, pid={pv.persistentId})");
                return pv.persistentId;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Log($"Failed to respawn vessel: {ex.Message}");
                return 0;
            }
        }

        public static void SpawnOrRecoverIfTooClose(RecordingStore.Recording rec, int index)
        {
            const int maxSpawnAttempts = 3;
            if (rec.SpawnAttempts >= maxSpawnAttempts)
                return;

            if (rec.Points.Count == 0 || FlightGlobals.Vessels == null)
            {
                rec.SpawnedVesselPersistentId = RespawnVessel(rec.VesselSnapshot);
                rec.VesselSpawned = rec.SpawnedVesselPersistentId != 0;
                if (!rec.VesselSpawned)
                {
                    rec.SpawnAttempts++;
                    if (rec.SpawnAttempts >= maxSpawnAttempts)
                        ParsekLog.Log($"Vessel spawn failed permanently for recording #{index} ({rec.VesselName}) after {maxSpawnAttempts} attempts");
                    else
                        ParsekLog.Log($"Vessel spawn failed for recording #{index} ({rec.VesselName}) — will retry (attempt {rec.SpawnAttempts}/{maxSpawnAttempts})");
                }
                return;
            }

            var lastPt = rec.Points[rec.Points.Count - 1];
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == lastPt.bodyName);
            if (body == null)
            {
                rec.SpawnedVesselPersistentId = RespawnVessel(rec.VesselSnapshot);
                rec.VesselSpawned = rec.SpawnedVesselPersistentId != 0;
                if (!rec.VesselSpawned)
                {
                    rec.SpawnAttempts++;
                    if (rec.SpawnAttempts >= maxSpawnAttempts)
                        ParsekLog.Log($"Vessel spawn failed permanently for recording #{index} ({rec.VesselName}) after {maxSpawnAttempts} attempts");
                    else
                        ParsekLog.Log($"Vessel spawn failed for recording #{index} ({rec.VesselName}) — will retry (attempt {rec.SpawnAttempts}/{maxSpawnAttempts})");
                }
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
                Vector3d diff = spawnPos - closestPos;
                Vector3d direction = diff.magnitude > 0.001
                    ? diff.normalized
                    : body.GetSurfaceNVector(lastPt.latitude, lastPt.longitude);
                Vector3d offsetPos = closestPos + direction * 250.0;

                double newLat = body.GetLatitude(offsetPos);
                double newLon = body.GetLongitude(offsetPos);
                double newAlt = body.GetAltitude(offsetPos);

                rec.VesselSnapshot.SetValue("lat", newLat.ToString("R"));
                rec.VesselSnapshot.SetValue("lon", newLon.ToString("R"));
                rec.VesselSnapshot.SetValue("alt", newAlt.ToString("R"));

                ParsekLog.Log($"Offset vessel #{index} ({rec.VesselName}) from {closestDist:F0}m to 250m from nearest vessel/recording");
            }

            rec.SpawnedVesselPersistentId = RespawnVessel(rec.VesselSnapshot);
            rec.VesselSpawned = rec.SpawnedVesselPersistentId != 0;
            if (rec.VesselSpawned)
            {
                ParsekLog.Log($"Vessel spawn for recording #{index} ({rec.VesselName})");
                ParsekLog.ScreenMessage($"Vessel '{rec.VesselName}' has appeared!", 4f);
            }
            else
            {
                rec.SpawnAttempts++;
                if (rec.SpawnAttempts >= maxSpawnAttempts)
                    ParsekLog.Log($"Vessel spawn failed permanently for recording #{index} ({rec.VesselName}) after {maxSpawnAttempts} attempts");
                else
                    ParsekLog.Log($"Vessel spawn failed for recording #{index} ({rec.VesselName}) — will retry (attempt {rec.SpawnAttempts}/{maxSpawnAttempts})");
            }
        }

        public static void RecoverVessel(ConfigNode vesselNode)
        {
            try
            {
                ProtoVessel pv = new ProtoVessel(vesselNode, HighLogic.CurrentGame);
                ShipConstruction.RecoverVesselFromFlight(pv, HighLogic.CurrentGame.flightState, true);
                ParsekLog.Log("Vessel recovered for funds");
            }
            catch (System.Exception ex)
            {
                ParsekLog.Log($"Failed to recover vessel: {ex.Message}");
            }
        }

        public static void RemoveDeadCrewFromSnapshot(ConfigNode snapshot)
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
                            ParsekLog.Log($"Removed dead/missing crew '{name}' from vessel snapshot");
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

        public static bool VesselExistsByPid(uint pid)
        {
            if (FlightGlobals.Vessels == null) return false;
            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                if (FlightGlobals.Vessels[i].persistentId == pid)
                    return true;
            }
            return false;
        }

        public static void SnapshotVessel(RecordingStore.Recording pending, bool vesselDestroyed)
        {
            if (pending == null || pending.Points.Count == 0) return;

            // Compute distance from launch
            var firstPoint = pending.Points[0];
            CelestialBody bodyFirst = FlightGlobals.Bodies?.Find(b => b.name == firstPoint.bodyName);

            if (vesselDestroyed)
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
                ComputeMaxDistance(pending, bodyFirst, firstPoint);

                pending.VesselSituation = "Destroyed";
                ParsekLog.Log($"Vessel was destroyed during recording. Distance from launch: {pending.DistanceFromLaunch:F0}m, " +
                    $"Max distance: {pending.MaxDistanceFromLaunch:F0}m");
                return;
            }

            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                pending.VesselDestroyed = true;
                pending.VesselSnapshot = null;
                pending.VesselSituation = "Unknown (no active vessel)";
                ParsekLog.Log("No active vessel at snapshot time");
                return;
            }

            // Compute distance from launch position
            if (bodyFirst != null)
            {
                Vector3d launchPos = bodyFirst.GetWorldSurfacePosition(
                    firstPoint.latitude, firstPoint.longitude, firstPoint.altitude);
                Vector3d currentPos = vessel.GetWorldPos3D();
                pending.DistanceFromLaunch = Vector3d.Distance(launchPos, currentPos);

                ComputeMaxDistance(pending, bodyFirst, firstPoint);
            }

            // Snapshot the vessel (works for regular vessels and EVA kerbals)
            if (!vessel.loaded)
                ParsekLog.Log("Warning: Active vessel is unloaded at snapshot time — snapshot may be incomplete");
            ProtoVessel pv = vessel.BackupVessel();
            ConfigNode node = new ConfigNode("VESSEL");
            pv.Save(node);
            pending.VesselSnapshot = node;
            pending.VesselDestroyed = false;

            // Build situation string
            pending.VesselSituation = vessel.isEVA
                ? $"EVA {vessel.mainBody.name}"
                : $"{vessel.situation} {vessel.mainBody.name}";

            ParsekLog.Log($"Vessel snapshot taken. Distance from launch: {pending.DistanceFromLaunch:F0}m, " +
                $"Max distance: {pending.MaxDistanceFromLaunch:F0}m, Situation: {pending.VesselSituation}");
        }

        private static void ComputeMaxDistance(RecordingStore.Recording pending, CelestialBody bodyFirst, TrajectoryPoint firstPoint)
        {
            if (bodyFirst == null) return;

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
    }
}
