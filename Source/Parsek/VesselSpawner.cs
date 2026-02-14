using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Static methods for spawning, recovering, and snapshotting vessels.
    /// </summary>
    public static class VesselSpawner
    {
        public static ConfigNode TryBackupSnapshot(Vessel vessel)
        {
            if (vessel == null) return null;
            try
            {
                ProtoVessel pv = vessel.BackupVessel();
                if (pv == null) return null;
                ConfigNode node = new ConfigNode("VESSEL");
                pv.Save(node);
                return node;
            }
            catch (Exception ex)
            {
                ParsekLog.Log($"Failed to backup vessel snapshot: {ex.Message}");
                return null;
            }
        }

        public static uint RespawnVessel(ConfigNode vesselNode, HashSet<string> excludeCrew = null)
        {
            try
            {
                // Use a copy to avoid modifying the saved snapshot
                ConfigNode spawnNode = vesselNode.CreateCopy();

                // Remove dead/missing crew from snapshot to avoid resurrecting them
                RemoveDeadCrewFromSnapshot(spawnNode);

                // Ensure all referenced crew exist in the roster (synthetic recordings
                // may reference kerbals that were never added to this game's roster)
                EnsureCrewExistInRoster(spawnNode);

                // Remove specific crew (e.g. EVA'd kerbals) — they spawn via child recordings
                if (excludeCrew != null && excludeCrew.Count > 0)
                    RemoveSpecificCrewFromSnapshot(spawnNode, excludeCrew);

                // Set reserved crew back to Available so KSP can assign them to this vessel
                ParsekScenario.UnreserveCrewInSnapshot(spawnNode);

                EnsureOwnedDiscovery(spawnNode);

                ProtoVessel pv = new ProtoVessel(spawnNode, HighLogic.CurrentGame);
                HighLogic.CurrentGame.flightState.protoVessels.Add(pv);
                pv.Load(HighLogic.CurrentGame.flightState);

                if (pv.vesselRef != null)
                    GameEvents.onNewVesselCreated.Fire(pv.vesselRef);

                ParsekLog.Log($"Vessel respawned with crew (sit={spawnNode.GetValue("sit")}, pid={pv.persistentId})");
                return pv.persistentId;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Log($"Failed to respawn vessel: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Spawn a vessel from a snapshot at a specific position and velocity,
        /// overriding the snapshot's stored orbit and location.
        /// Used by Take Control to spawn at the ghost's current position.
        /// </summary>
        public static uint SpawnAtPosition(ConfigNode vesselNode, CelestialBody body,
            double lat, double lon, double alt,
            Vector3d velocity, double ut,
            HashSet<string> excludeCrew = null)
        {
            try
            {
                ConfigNode spawnNode = vesselNode.CreateCopy();

                // Update position
                spawnNode.SetValue("lat", lat.ToString("R"), true);
                spawnNode.SetValue("lon", lon.ToString("R"), true);
                spawnNode.SetValue("alt", alt.ToString("R"), true);

                // Determine situation from altitude and velocity
                double orbitalSpeed = Math.Sqrt(body.gravParameter / (body.Radius + alt));
                bool overWater = body.ocean && body.TerrainAltitude(lat, lon) < 0;
                string sit;
                if (alt <= 0 && overWater)
                {
                    sit = "SPLASHED";
                    spawnNode.SetValue("landed", "False", true);
                    spawnNode.SetValue("splashed", "True", true);
                }
                else if (alt <= 0)
                {
                    sit = "LANDED";
                    spawnNode.SetValue("landed", "True", true);
                    spawnNode.SetValue("splashed", "False", true);
                }
                else if (velocity.magnitude > orbitalSpeed * 0.9)
                {
                    sit = "ORBITING";
                    spawnNode.SetValue("landed", "False", true);
                    spawnNode.SetValue("splashed", "False", true);
                }
                else
                {
                    sit = "FLYING";
                    spawnNode.SetValue("landed", "False", true);
                    spawnNode.SetValue("splashed", "False", true);
                }
                spawnNode.SetValue("sit", sit, true);

                // Rebuild ORBIT subnode from position + velocity
                var orbit = new Orbit();
                Vector3d worldPos = body.GetWorldSurfacePosition(lat, lon, alt);
                orbit.UpdateFromStateVectors(worldPos, velocity, body, ut);
                spawnNode.RemoveNode("ORBIT");
                ConfigNode orbitNode = new ConfigNode("ORBIT");
                SaveOrbitToNode(orbit, orbitNode, body);
                spawnNode.AddNode(orbitNode);

                // Proximity check against nearby vessels
                Vector3d spawnPos = worldPos;
                double closestDist = double.MaxValue;
                Vector3d closestPos = Vector3d.zero;
                if (FlightGlobals.Vessels != null)
                {
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
                }

                if (closestDist < 200.0)
                {
                    Vector3d diff = spawnPos - closestPos;
                    Vector3d normal = body.GetSurfaceNVector(lat, lon).normalized;
                    Vector3d tangent = diff - Vector3d.Dot(diff, normal) * normal;
                    if (tangent.magnitude < 0.001)
                        tangent = Vector3d.Cross(normal, Vector3d.up);
                    if (tangent.magnitude < 0.001)
                        tangent = Vector3d.Cross(normal, Vector3d.right);
                    tangent = tangent.normalized;

                    double angularDistance = 250.0 / body.Radius;
                    Vector3d rotatedNormal =
                        (normal * Math.Cos(angularDistance) + tangent * Math.Sin(angularDistance)).normalized;
                    Vector3d surfacePos = body.position + rotatedNormal * (body.Radius + alt);

                    double newLat = body.GetLatitude(surfacePos);
                    double newLon = body.GetLongitude(surfacePos);
                    spawnNode.SetValue("lat", newLat.ToString("R"), true);
                    spawnNode.SetValue("lon", newLon.ToString("R"), true);

                    // Recompute orbit for new position
                    Vector3d newWorldPos = body.GetWorldSurfacePosition(newLat, newLon, alt);
                    orbit.UpdateFromStateVectors(newWorldPos, velocity, body, ut);
                    spawnNode.RemoveNode("ORBIT");
                    orbitNode = new ConfigNode("ORBIT");
                    SaveOrbitToNode(orbit, orbitNode, body);
                    spawnNode.AddNode(orbitNode);

                    ParsekLog.Log($"SpawnAtPosition: offset from {closestDist:F0}m to 250m from nearest vessel");
                }

                // Crew handling
                RemoveDeadCrewFromSnapshot(spawnNode);
                EnsureCrewExistInRoster(spawnNode);
                if (excludeCrew != null && excludeCrew.Count > 0)
                    RemoveSpecificCrewFromSnapshot(spawnNode, excludeCrew);
                ParsekScenario.UnreserveCrewInSnapshot(spawnNode);

                EnsureOwnedDiscovery(spawnNode);

                ProtoVessel pv = new ProtoVessel(spawnNode, HighLogic.CurrentGame);
                HighLogic.CurrentGame.flightState.protoVessels.Add(pv);
                pv.Load(HighLogic.CurrentGame.flightState);

                if (pv.vesselRef != null)
                    GameEvents.onNewVesselCreated.Fire(pv.vesselRef);

                ParsekLog.Log($"SpawnAtPosition: vessel spawned (sit={sit}, pid={pv.persistentId}, " +
                    $"body={body.name}, alt={alt:F0}m)");
                return pv.persistentId;
            }
            catch (Exception ex)
            {
                ParsekLog.Log($"SpawnAtPosition failed: {ex.Message}");
                return 0;
            }
        }

        public static void SpawnOrRecoverIfTooClose(RecordingStore.Recording rec, int index)
        {
            const int maxSpawnAttempts = 3;
            if (rec.SpawnAttempts >= maxSpawnAttempts)
                return;

            // Build exclude set once for all spawn paths (EVA'd crew spawn via child recordings)
            HashSet<string> excludeCrew = BuildExcludeCrewSet(rec);

            if (rec.Points.Count == 0 || FlightGlobals.Vessels == null)
            {
                LogSpawnContext(rec, double.MaxValue);
                rec.SpawnedVesselPersistentId = RespawnVessel(rec.VesselSnapshot, excludeCrew);
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
                LogSpawnContext(rec, double.MaxValue);
                rec.SpawnedVesselPersistentId = RespawnVessel(rec.VesselSnapshot, excludeCrew);
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

            // Skip proximity offset for EVA kerbals — let them spawn where recorded
            bool isEva = !string.IsNullOrEmpty(rec.EvaCrewName);

            if (closestDist < 200.0 && !isEva)
            {
                // Offset away from the closest vessel along the body surface to avoid
                // injecting vertical error that can produce hovering landed spawns.
                Vector3d diff = spawnPos - closestPos;
                Vector3d normal = body.GetSurfaceNVector(lastPt.latitude, lastPt.longitude).normalized;
                Vector3d tangent = diff - Vector3d.Dot(diff, normal) * normal;
                if (tangent.magnitude < 0.001)
                    tangent = Vector3d.Cross(normal, Vector3d.up);
                if (tangent.magnitude < 0.001)
                    tangent = Vector3d.Cross(normal, Vector3d.right);
                tangent = tangent.normalized;

                double angularDistance = 250.0 / body.Radius;
                Vector3d rotatedNormal =
                    (normal * Math.Cos(angularDistance) + tangent * Math.Sin(angularDistance)).normalized;
                Vector3d surfacePos = body.position + rotatedNormal * (body.Radius + 1.0);

                double newLat = body.GetLatitude(surfacePos);
                double newLon = body.GetLongitude(surfacePos);
                double newAlt = ResolveRelocatedAltitude(rec, body, newLat, newLon);

                rec.VesselSnapshot.SetValue("lat", newLat.ToString("R"));
                rec.VesselSnapshot.SetValue("lon", newLon.ToString("R"));
                rec.VesselSnapshot.SetValue("alt", newAlt.ToString("R"));

                ParsekLog.Log($"Offset vessel #{index} ({rec.VesselName}) from {closestDist:F0}m to 250m from nearest vessel/recording");
            }

            LogSpawnContext(rec, closestDist);

            rec.SpawnedVesselPersistentId = RespawnVessel(rec.VesselSnapshot, excludeCrew);
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

        internal static HashSet<string> BuildExcludeCrewSet(RecordingStore.Recording rec)
        {
            if (string.IsNullOrEmpty(rec.RecordingId)) return null;

            HashSet<string> excludeCrew = null;
            var committed = RecordingStore.CommittedRecordings;
            for (int c = 0; c < committed.Count; c++)
            {
                var child = committed[c];
                if (child.ParentRecordingId == rec.RecordingId && !string.IsNullOrEmpty(child.EvaCrewName))
                {
                    if (excludeCrew == null) excludeCrew = new HashSet<string>();
                    excludeCrew.Add(child.EvaCrewName);
                }
            }
            if (excludeCrew != null)
                ParsekLog.Log($"Excluding EVA'd crew from parent spawn: [{string.Join(", ", excludeCrew)}]");
            return excludeCrew;
        }

        internal static void LogSpawnContext(RecordingStore.Recording rec, double closestDist)
        {
            string sit = rec.VesselSnapshot?.GetValue("sit") ?? "?";
            var allCrew = new List<string>();
            if (rec.VesselSnapshot != null)
            {
                foreach (ConfigNode partNode in rec.VesselSnapshot.GetNodes("PART"))
                {
                    var crewNames = partNode.GetValues("crew");
                    for (int c = 0; c < crewNames.Length; c++)
                        allCrew.Add(crewNames[c]);
                }
            }
            string crewStr = allCrew.Count > 0 ? $", crew=[{string.Join(", ", allCrew)}]" : "";
            ParsekLog.Log($"Spawning vessel: \"{rec.VesselName}\" sit={sit}{crewStr}, " +
                $"nearest vessel={closestDist:F0}m");
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

        public static void RemoveSpecificCrewFromSnapshot(ConfigNode snapshot, HashSet<string> crewNames)
        {
            if (snapshot == null || crewNames == null || crewNames.Count == 0) return;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                var names = partNode.GetValues("crew");
                if (names.Length == 0) continue;

                var keep = new List<string>();
                bool removedAny = false;
                foreach (string name in names)
                {
                    if (crewNames.Contains(name))
                    {
                        removedAny = true;
                        ParsekLog.Log($"Removed EVA'd crew '{name}' from vessel snapshot");
                    }
                    else
                    {
                        keep.Add(name);
                    }
                }

                if (removedAny)
                {
                    partNode.RemoveValues("crew");
                    foreach (string name in keep)
                        partNode.AddValue("crew", name);
                }
            }
        }

        public static void RemoveDeadCrewFromSnapshot(ConfigNode snapshot)
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return;

            var reserved = ParsekScenario.CrewReplacements;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                var crewNames = partNode.GetValues("crew");
                if (crewNames.Length == 0) continue;

                // Check if any crew are dead/missing
                var keepNames = new List<string>();
                bool removedAny = false;
                foreach (string name in crewNames)
                {
                    // Reserved crew were alive at recording time — keep them
                    // regardless of current roster status. Stale roster state
                    // (e.g. Missing after --clean-start removed their vessel)
                    // must not prevent them from spawning.
                    if (reserved.ContainsKey(name))
                    {
                        keepNames.Add(name);
                        continue;
                    }

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

        /// <summary>
        /// Ensure all crew referenced in the snapshot exist in the game's CrewRoster.
        /// Synthetic recordings or cross-save imports may reference kerbals that were
        /// never added to this career's roster, causing NullRef in Part.RegisterCrew.
        /// </summary>
        public static void EnsureCrewExistInRoster(ConfigNode snapshot)
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                var crewNames = partNode.GetValues("crew");
                foreach (string name in crewNames)
                {
                    if (string.IsNullOrEmpty(name)) continue;

                    // Check if already in roster
                    bool found = false;
                    foreach (ProtoCrewMember pcm in roster.Crew)
                    {
                        if (pcm.name == name) { found = true; break; }
                    }
                    if (found) continue;

                    // Also check unowned (applicants, tourists, etc.)
                    ProtoCrewMember existing = roster[name];
                    if (existing != null) continue;

                    // Create the kerbal and add to roster
                    ProtoCrewMember newCrew = HighLogic.CurrentGame.CrewRoster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
                    if (newCrew != null)
                    {
                        newCrew.ChangeName(name);
                        newCrew.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
                        ParsekLog.Log($"Created missing crew '{name}' in roster for vessel spawn");
                    }
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

        public static void SnapshotVessel(
            RecordingStore.Recording pending,
            bool vesselDestroyed,
            Vessel vesselOverride = null,
            ConfigNode destroyedFallbackSnapshot = null)
        {
            if (pending == null || pending.Points.Count == 0) return;

            // Compute distance from launch
            var firstPoint = pending.Points[0];
            CelestialBody bodyFirst = FlightGlobals.Bodies?.Find(b => b.name == firstPoint.bodyName);

            if (vesselDestroyed)
            {
                pending.VesselDestroyed = true;
                pending.VesselSnapshot = destroyedFallbackSnapshot != null
                    ? destroyedFallbackSnapshot.CreateCopy()
                    : null;
                pending.GhostGeometryAvailable = false;
                pending.GhostGeometryCaptureError = pending.VesselSnapshot != null
                    ? "vessel_destroyed_using_last_snapshot"
                    : "vessel_destroyed";
                pending.GhostGeometryCaptureStrategy = "live_hierarchy_probe_v1";
                pending.GhostGeometryProbeStatus = pending.VesselSnapshot != null
                    ? "vessel_destroyed_with_snapshot"
                    : "vessel_destroyed";

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

                pending.VesselSituation = pending.VesselSnapshot != null ? "Destroyed (snapshot kept)" : "Destroyed";
                ParsekLog.Log($"Vessel was destroyed during recording. Distance from launch: {pending.DistanceFromLaunch:F0}m, " +
                    $"Max distance: {pending.MaxDistanceFromLaunch:F0}m, Snapshot kept: {pending.VesselSnapshot != null}");
                return;
            }

            Vessel vessel = vesselOverride ?? FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                pending.VesselDestroyed = true;
                pending.VesselSnapshot = null;
                pending.GhostGeometryAvailable = false;
                pending.GhostGeometryCaptureError = "no_active_vessel";
                pending.GhostGeometryCaptureStrategy = "live_hierarchy_probe_v1";
                pending.GhostGeometryProbeStatus = "no_active_vessel";
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
            ConfigNode node = TryBackupSnapshot(vessel);
            if (node == null)
            {
                pending.VesselDestroyed = true;
                pending.VesselSnapshot = null;
                pending.GhostGeometryAvailable = false;
                pending.GhostGeometryCaptureError = "snapshot_backup_failed";
                pending.GhostGeometryCaptureStrategy = "live_hierarchy_probe_v1";
                pending.GhostGeometryProbeStatus = "snapshot_backup_failed";
                pending.VesselSituation = "Unknown (snapshot failed)";
                ParsekLog.Log("Failed to backup active vessel at snapshot time");
                return;
            }
            pending.VesselSnapshot = node;
            pending.VesselDestroyed = false;

            // Build situation string
            pending.VesselSituation = vessel.isEVA
                ? $"EVA {vessel.mainBody.name}"
                : $"{vessel.situation} {vessel.mainBody.name}";

            // Atomic capture: snapshot and ghost geometry metadata are captured together.
            GhostGeometryCapture.CaptureStub(pending, vessel);

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

        private static double ResolveRelocatedAltitude(
            RecordingStore.Recording rec, CelestialBody body, double latitude, double longitude)
        {
            bool landedLike = IsLandedLikeSnapshot(rec?.VesselSnapshot);
            bool hasSnapshotAlt = TryGetSnapshotDouble(rec?.VesselSnapshot, "alt", out double snapshotAlt);
            if (landedLike)
            {
                double terrainAlt = body.TerrainAltitude(latitude, longitude);
                bool terrainValid = !double.IsNaN(terrainAlt) && !double.IsInfinity(terrainAlt);
                return SelectRelocatedAltitude(landedLike, terrainAlt, terrainValid, snapshotAlt, hasSnapshotAlt);
            }

            return SelectRelocatedAltitude(landedLike, 0.0, false, snapshotAlt, hasSnapshotAlt);
        }

        private static bool IsLandedLikeSnapshot(ConfigNode snapshot)
        {
            if (snapshot == null) return false;

            string sit = snapshot.GetValue("sit") ?? string.Empty;
            if (sit.Equals("LANDED", StringComparison.OrdinalIgnoreCase) ||
                sit.Equals("PRELAUNCH", StringComparison.OrdinalIgnoreCase) ||
                sit.Equals("SPLASHED", StringComparison.OrdinalIgnoreCase))
                return true;

            if (TryGetSnapshotBool(snapshot, "landed", out bool landed) && landed)
                return true;
            if (TryGetSnapshotBool(snapshot, "splashed", out bool splashed) && splashed)
                return true;

            return false;
        }

        private static bool TryGetSnapshotBool(ConfigNode node, string key, out bool value)
        {
            value = false;
            if (node == null) return false;
            string raw = node.GetValue(key);
            return bool.TryParse(raw, out value);
        }

        private static bool TryGetSnapshotDouble(ConfigNode node, string key, out double value)
        {
            value = 0.0;
            if (node == null) return false;
            string raw = node.GetValue(key);
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static void SaveOrbitToNode(Orbit orbit, ConfigNode node, CelestialBody body)
        {
            var ic = CultureInfo.InvariantCulture;
            node.AddValue("SMA", orbit.semiMajorAxis.ToString("R", ic));
            node.AddValue("ECC", orbit.eccentricity.ToString("R", ic));
            node.AddValue("INC", orbit.inclination.ToString("R", ic));
            node.AddValue("LPE", orbit.argumentOfPeriapsis.ToString("R", ic));
            node.AddValue("LAN", orbit.LAN.ToString("R", ic));
            node.AddValue("MNA", orbit.meanAnomalyAtEpoch.ToString("R", ic));
            node.AddValue("EPH", orbit.epoch.ToString("R", ic));
            node.AddValue("REF", FlightGlobals.Bodies.IndexOf(body).ToString(ic));
        }

        private static void EnsureOwnedDiscovery(ConfigNode spawnNode)
        {
            ConfigNode disc = spawnNode.GetNode("DISCOVERY");
            if (disc == null)
            {
                disc = spawnNode.AddNode("DISCOVERY");
                disc.AddValue("state", "31");
                disc.AddValue("lastObservedTime", "0");
                disc.AddValue("lifetime", "Infinity");
                disc.AddValue("refTime", "0");
                disc.AddValue("size", "2");
            }
            else
            {
                disc.SetValue("state", "31", true);
            }
        }

        internal static double SelectRelocatedAltitude(
            bool landedLike, double terrainAltitude, bool terrainValid, double snapshotAltitude, bool hasSnapshotAltitude)
        {
            if (landedLike && terrainValid)
                return terrainAltitude;
            if (hasSnapshotAltitude)
                return snapshotAltitude;
            return 0.0;
        }
    }
}
