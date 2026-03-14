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
        /// <summary>
        /// When false, vessels always spawn at the exact recorded end position
        /// (no proximity offset). Set true to re-enable the 200m/250m offset logic.
        /// </summary>
        public static bool ProximityOffsetEnabled = false;

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
                ParsekLog.Error("Spawner", $"Failed to backup vessel snapshot: {ex.Message}");
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

                // Remove crew that already exist on a loaded vessel (prevents duplication)
                RemoveDuplicateCrewFromSnapshot(spawnNode);

                RegenerateVesselIdentity(spawnNode);
                EnsureSpawnReadiness(spawnNode);

                ProtoVessel pv = new ProtoVessel(spawnNode, HighLogic.CurrentGame);
                HighLogic.CurrentGame.flightState.protoVessels.Add(pv);
                pv.Load(HighLogic.CurrentGame.flightState);

                if (pv.vesselRef == null)
                {
                    ParsekLog.Error("Spawner", "CRITICAL: ProtoVessel.Load() produced null vesselRef — vessel will not appear");
                    return 0;
                }
                if (pv.vesselRef.orbitDriver == null)
                    ParsekLog.Warn("Spawner", "Spawned vessel has no orbitDriver — may not appear in map view");

                GameEvents.onNewVesselCreated.Fire(pv.vesselRef);

                ParsekLog.Info("Spawner", $"Vessel respawned (sit={spawnNode.GetValue("sit")}, pid={pv.vesselRef.persistentId})");
                return pv.vesselRef.persistentId;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Error("Spawner", $"Failed to respawn vessel: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Spawn a vessel from a snapshot at a specific position and velocity,
        /// overriding the snapshot's stored orbit and location.
        /// Spawns at a specific position overriding the snapshot's stored orbit.
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

                if (ProximityOffsetEnabled && closestDist < 100.0)
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

                    ParsekLog.Info("Spawner", $"SpawnAtPosition: offset from {closestDist:F0}m to 250m from nearest vessel");
                }

                // Crew handling
                RemoveDeadCrewFromSnapshot(spawnNode);
                EnsureCrewExistInRoster(spawnNode);
                if (excludeCrew != null && excludeCrew.Count > 0)
                    RemoveSpecificCrewFromSnapshot(spawnNode, excludeCrew);
                ParsekScenario.UnreserveCrewInSnapshot(spawnNode);

                // Remove crew that already exist on a loaded vessel (prevents duplication)
                RemoveDuplicateCrewFromSnapshot(spawnNode);

                RegenerateVesselIdentity(spawnNode);
                EnsureSpawnReadiness(spawnNode);

                ProtoVessel pv = new ProtoVessel(spawnNode, HighLogic.CurrentGame);
                HighLogic.CurrentGame.flightState.protoVessels.Add(pv);
                pv.Load(HighLogic.CurrentGame.flightState);

                if (pv.vesselRef == null)
                {
                    ParsekLog.Error("Spawner", "CRITICAL: SpawnAtPosition — ProtoVessel.Load() produced null vesselRef");
                    return 0;
                }
                if (pv.vesselRef.orbitDriver == null)
                    ParsekLog.Warn("Spawner", "SpawnAtPosition vessel has no orbitDriver — may not appear in map view");

                GameEvents.onNewVesselCreated.Fire(pv.vesselRef);

                ParsekLog.Info("Spawner", $"SpawnAtPosition: vessel spawned (sit={sit}, pid={pv.vesselRef.persistentId}, " +
                    $"body={body.name}, alt={alt:F0}m)");
                return pv.vesselRef.persistentId;
            }
            catch (Exception ex)
            {
                ParsekLog.Error("Spawner", $"SpawnAtPosition failed: {ex.Message}");
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
                        ParsekLog.Error("Spawner", $"Vessel spawn failed permanently for recording #{index} ({rec.VesselName}) after {maxSpawnAttempts} attempts");
                    else
                        ParsekLog.Warn("Spawner", $"Vessel spawn failed for recording #{index} ({rec.VesselName}) — will retry (attempt {rec.SpawnAttempts}/{maxSpawnAttempts})");
                }
                return;
            }

            var lastPt = rec.Points[rec.Points.Count - 1];
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == lastPt.bodyName);
            if (body == null)
            {
                ParsekLog.Info("Spawner", $"Body lookup failed for spawn: bodyName='{lastPt.bodyName}' not found — " +
                    $"falling back to RespawnVessel without position");
                LogSpawnContext(rec, double.MaxValue);
                rec.SpawnedVesselPersistentId = RespawnVessel(rec.VesselSnapshot, excludeCrew);
                rec.VesselSpawned = rec.SpawnedVesselPersistentId != 0;
                if (!rec.VesselSpawned)
                {
                    rec.SpawnAttempts++;
                    if (rec.SpawnAttempts >= maxSpawnAttempts)
                        ParsekLog.Error("Spawner", $"Vessel spawn failed permanently for recording #{index} ({rec.VesselName}) after {maxSpawnAttempts} attempts");
                    else
                        ParsekLog.Warn("Spawner", $"Vessel spawn failed for recording #{index} ({rec.VesselName}) — will retry (attempt {rec.SpawnAttempts}/{maxSpawnAttempts})");
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

            if (ProximityOffsetEnabled && closestDist < 100.0 && !isEva)
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

                // Rebuild ORBIT node for new position — map view uses ORBIT for icon placement.
                // Use last recorded velocity (inertial frame) to preserve flight dynamics
                // for any vessel situation, not just landed.
                Vector3d newWorldPos = body.GetWorldSurfacePosition(newLat, newLon, newAlt);
                Vector3d lastVel = new Vector3d(lastPt.velocity.x, lastPt.velocity.y, lastPt.velocity.z);
                var relocOrbit = new Orbit();
                relocOrbit.UpdateFromStateVectors(newWorldPos, lastVel, body, Planetarium.GetUniversalTime());
                rec.VesselSnapshot.RemoveNode("ORBIT");
                var relocOrbitNode = new ConfigNode("ORBIT");
                SaveOrbitToNode(relocOrbit, relocOrbitNode, body);
                rec.VesselSnapshot.AddNode(relocOrbitNode);

                ParsekLog.Info("Spawner", $"Offset vessel #{index} ({rec.VesselName}) from {closestDist:F0}m to 250m from nearest vessel/recording");
            }

            LogSpawnContext(rec, closestDist);

            rec.SpawnedVesselPersistentId = RespawnVessel(rec.VesselSnapshot, excludeCrew);
            rec.VesselSpawned = rec.SpawnedVesselPersistentId != 0;
            if (rec.VesselSpawned)
            {
                ParsekLog.Info("Spawner", $"Vessel spawn for recording #{index} ({rec.VesselName})");
                ParsekLog.ScreenMessage($"Vessel '{rec.VesselName}' has appeared!", 4f);
            }
            else
            {
                rec.SpawnAttempts++;
                if (rec.SpawnAttempts >= maxSpawnAttempts)
                    ParsekLog.Error("Spawner", $"Vessel spawn failed permanently for recording #{index} ({rec.VesselName}) after {maxSpawnAttempts} attempts");
                else
                    ParsekLog.Warn("Spawner", $"Vessel spawn failed for recording #{index} ({rec.VesselName}) — will retry (attempt {rec.SpawnAttempts}/{maxSpawnAttempts})");
            }
        }

        internal static HashSet<string> BuildExcludeCrewSet(RecordingStore.Recording rec)
        {
            if (string.IsNullOrEmpty(rec.RecordingId)) return null;

            HashSet<string> excludeCrew = null;
            var committed = RecordingStore.CommittedRecordings;

            // Chain-aware: exclude EVA crew who are still on EVA at the end of the chain
            // (no subsequent vessel segment). Crew who boarded back are NOT excluded.
            // EVA segments themselves should never exclude their own crew.
            if (!string.IsNullOrEmpty(rec.ChainId) && string.IsNullOrEmpty(rec.EvaCrewName))
            {
                // Find the highest ChainIndex of a non-EVA (vessel) segment on branch 0 only
                int highestVesselIndex = -1;
                List<(string crew, int index)> evaSegments = null;

                for (int c = 0; c < committed.Count; c++)
                {
                    var sibling = committed[c];
                    if (sibling.ChainId != rec.ChainId) continue;
                    // Skip branch > 0 segments for crew exclusion logic
                    if (sibling.ChainBranch != 0) continue;

                    if (!string.IsNullOrEmpty(sibling.EvaCrewName))
                    {
                        if (evaSegments == null) evaSegments = new List<(string, int)>();
                        evaSegments.Add((sibling.EvaCrewName, sibling.ChainIndex));
                    }
                    else if (sibling.ChainIndex > highestVesselIndex)
                    {
                        highestVesselIndex = sibling.ChainIndex;
                    }
                }

                // Exclude EVA crew whose EVA segment comes after all vessel segments
                // (they're still on EVA — didn't board back)
                if (evaSegments != null)
                {
                    for (int e = 0; e < evaSegments.Count; e++)
                    {
                        if (evaSegments[e].index > highestVesselIndex)
                        {
                            if (excludeCrew == null) excludeCrew = new HashSet<string>();
                            excludeCrew.Add(evaSegments[e].crew);
                        }
                    }
                }

                if (excludeCrew != null)
                    ParsekLog.Info("Spawner", $"Excluding EVA'd crew from chain vessel spawn: [{string.Join(", ", excludeCrew)}]");
                return excludeCrew;
            }

            // Legacy fallback: also check single-level parent→child linkage
            // (for old saves without chain fields)
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
                ParsekLog.Info("Spawner", $"Excluding EVA'd crew from parent spawn: [{string.Join(", ", excludeCrew)}]");
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
            ParsekLog.Info("Spawner", $"Spawning vessel: \"{rec.VesselName}\" sit={sit}{crewStr}, " +
                $"nearest vessel={closestDist:F0}m");
        }

        /// <summary>
        /// Removes crew members from a spawn snapshot if they already exist on a loaded
        /// vessel in the scene. Prevents kerbal duplication when a previously-spawned vessel
        /// still has the same crew member aboard.
        /// </summary>
        public static void RemoveDuplicateCrewFromSnapshot(ConfigNode snapshot)
        {
            if (snapshot == null) return;

            // Build set of crew names already on loaded vessels
            var existingCrew = BuildExistingCrewSet();
            if (existingCrew.Count == 0) return;

            // Extract crew from the snapshot and find duplicates
            var snapshotCrew = ParsekScenario.ExtractCrewFromSnapshot(snapshot);
            var duplicates = FindDuplicateCrew(snapshotCrew, existingCrew);

            if (duplicates.Count == 0) return;

            // Remove duplicates from snapshot parts (same pattern as RemoveSpecificCrewFromSnapshot)
            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                var names = partNode.GetValues("crew");
                if (names.Length == 0) continue;

                var keep = new List<string>();
                bool removedAny = false;
                foreach (string name in names)
                {
                    if (duplicates.Contains(name))
                    {
                        removedAny = true;
                        ParsekLog.Warn("Spawner",
                            $"Crew dedup: '{name}' already on a vessel in the scene — removed from spawn snapshot");
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

        /// <summary>
        /// Builds a set of all crew member names currently on loaded vessels.
        /// Pure decision helper: extracted for testability.
        /// </summary>
        internal static HashSet<string> BuildExistingCrewSet()
        {
            var existing = new HashSet<string>();
            if (FlightGlobals.Vessels == null) return existing;

            for (int v = 0; v < FlightGlobals.Vessels.Count; v++)
            {
                var crew = FlightGlobals.Vessels[v].GetVesselCrew();
                for (int c = 0; c < crew.Count; c++)
                {
                    if (!string.IsNullOrEmpty(crew[c].name))
                        existing.Add(crew[c].name);
                }
            }
            return existing;
        }

        /// <summary>
        /// Pure decision: given a set of crew in a snapshot and a set of crew already
        /// on loaded vessels, returns the names that would be duplicated.
        /// Extracted for testability.
        /// </summary>
        internal static HashSet<string> FindDuplicateCrew(
            List<string> snapshotCrew, HashSet<string> existingCrew)
        {
            var duplicates = new HashSet<string>();
            if (snapshotCrew == null || existingCrew == null) return duplicates;

            foreach (string name in snapshotCrew)
            {
                if (!string.IsNullOrEmpty(name) && existingCrew.Contains(name))
                    duplicates.Add(name);
            }
            return duplicates;
        }

        public static void RemoveSpecificCrewFromSnapshot(ConfigNode snapshot, HashSet<string> crewNames)
        {
            if (snapshot == null || crewNames == null || crewNames.Count == 0)
            {
                ParsekLog.VerboseRateLimited("Spawner", "remove-specific-crew-skipped",
                    "RemoveSpecificCrewFromSnapshot skipped due to missing snapshot or crew set", 5.0);
                return;
            }

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
                        ParsekLog.Info("Spawner", $"Removed EVA'd crew '{name}' from vessel snapshot");
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
            if (roster == null)
            {
                ParsekLog.Warn("Spawner", "RemoveDeadCrewFromSnapshot skipped: crew roster unavailable");
                return;
            }

            var reserved = ParsekScenario.CrewReplacements;
            int removedCount = 0;

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
                            ParsekLog.Info("Spawner", $"Removed dead/missing crew '{name}' from vessel snapshot");
                            removedCount++;
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

            if (removedCount > 0)
                ParsekLog.Info("Spawner", $"Spawn prep: removed {removedCount} dead/missing crew from snapshot");
        }

        /// <summary>
        /// Ensure all crew referenced in the snapshot exist in the game's CrewRoster.
        /// Synthetic recordings or cross-save imports may reference kerbals that were
        /// never added to this career's roster, causing NullRef in Part.RegisterCrew.
        /// </summary>
        public static void EnsureCrewExistInRoster(ConfigNode snapshot)
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                ParsekLog.Warn("Spawner", "EnsureCrewExistInRoster skipped: crew roster unavailable");
                return;
            }

            int createdCount = 0;
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
                        ParsekLog.Info("Spawner", $"Created missing crew '{name}' in roster for vessel spawn");
                        createdCount++;
                    }
                }
            }

            if (createdCount > 0)
                ParsekLog.Info("Spawner", $"Spawn prep: created {createdCount} missing crew in roster");
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
            if (pending == null || pending.Points.Count == 0)
            {
                ParsekLog.Warn("Spawner", "SnapshotVessel skipped: pending recording is null or has no points");
                return;
            }

            // Compute distance from launch
            var firstPoint = pending.Points[0];
            CelestialBody bodyFirst = FlightGlobals.Bodies?.Find(b => b.name == firstPoint.bodyName);
            if (bodyFirst == null)
                ParsekLog.Warn("Spawner", $"SnapshotVessel: body '{firstPoint.bodyName}' not found — distance computation will be skipped");

            if (vesselDestroyed)
            {
                pending.VesselDestroyed = true;
                pending.VesselSnapshot = destroyedFallbackSnapshot != null
                    ? destroyedFallbackSnapshot.CreateCopy()
                    : null;
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
                ParsekLog.Info("Spawner", $"Vessel was destroyed during recording. Distance from launch: {pending.DistanceFromLaunch:F0}m, " +
                    $"Max distance: {pending.MaxDistanceFromLaunch:F0}m, Snapshot kept: {pending.VesselSnapshot != null}");
                return;
            }

            Vessel vessel = vesselOverride ?? FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                pending.VesselDestroyed = true;
                pending.VesselSnapshot = null;
                pending.VesselSituation = "Unknown (no active vessel)";
                ParsekLog.Info("Spawner", "No active vessel at snapshot time");
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
                ParsekLog.Warn("Spawner", "Active vessel is unloaded at snapshot time — snapshot may be incomplete");
            ConfigNode node = TryBackupSnapshot(vessel);
            if (node == null)
            {
                pending.VesselDestroyed = true;
                pending.VesselSnapshot = null;
                pending.VesselSituation = "Unknown (snapshot failed)";
                ParsekLog.Error("Spawner", "Failed to backup active vessel at snapshot time");
                return;
            }
            pending.VesselSnapshot = node;
            pending.VesselDestroyed = false;

            // Build situation string
            pending.VesselSituation = vessel.isEVA
                ? $"EVA {vessel.mainBody.name}"
                : $"{vessel.situation} {vessel.mainBody.name}";

            ParsekLog.Info("Spawner", $"Vessel snapshot taken. Distance from launch: {pending.DistanceFromLaunch:F0}m, " +
                $"Max distance: {pending.MaxDistanceFromLaunch:F0}m, Situation: {pending.VesselSituation}");
        }

        private static void ComputeMaxDistance(RecordingStore.Recording pending, CelestialBody bodyFirst, TrajectoryPoint firstPoint)
        {
            if (bodyFirst == null) return;

            Vector3d launchPos = bodyFirst.GetWorldSurfacePosition(
                firstPoint.latitude, firstPoint.longitude, firstPoint.altitude);
            double maxDist = 0;
            int bodyLookupFailCount = 0;
            for (int i = 1; i < pending.Points.Count; i++)
            {
                var pt = pending.Points[i];
                CelestialBody bodyPt = FlightGlobals.Bodies?.Find(b => b.name == pt.bodyName);
                if (bodyPt == null)
                {
                    bodyLookupFailCount++;
                    continue;
                }
                Vector3d ptPos = bodyPt.GetWorldSurfacePosition(pt.latitude, pt.longitude, pt.altitude);
                double d = Vector3d.Distance(launchPos, ptPos);
                if (d > maxDist) maxDist = d;
            }
            if (bodyLookupFailCount > 0)
                ParsekLog.Warn("Spawner", $"ComputeMaxDistance: {bodyLookupFailCount} points had unresolvable body names");
            pending.MaxDistanceFromLaunch = maxDist;
        }

        private static double ResolveRelocatedAltitude(
            RecordingStore.Recording rec, CelestialBody body, double latitude, double longitude)
        {
            bool landedLike = IsLandedLikeSnapshot(rec?.VesselSnapshot);
            bool hasSnapshotAlt = TryGetSnapshotDouble(rec?.VesselSnapshot, "alt", out double snapshotAlt);
            double selectedAltitude;
            if (landedLike)
            {
                double terrainAlt = body.TerrainAltitude(latitude, longitude);
                bool terrainValid = !double.IsNaN(terrainAlt) && !double.IsInfinity(terrainAlt);
                selectedAltitude = SelectRelocatedAltitude(landedLike, terrainAlt, terrainValid, snapshotAlt, hasSnapshotAlt);
                ParsekLog.Verbose("Spawner",
                    $"Relocation altitude selected={selectedAltitude:F1} source={(terrainValid ? "terrain" : hasSnapshotAlt ? "snapshot" : "fallback")} " +
                    $"landedLike={landedLike} terrainValid={terrainValid} snapshotAltSet={hasSnapshotAlt}");
                return selectedAltitude;
            }

            selectedAltitude = SelectRelocatedAltitude(landedLike, 0.0, false, snapshotAlt, hasSnapshotAlt);
            ParsekLog.Verbose("Spawner",
                $"Relocation altitude selected={selectedAltitude:F1} source={(hasSnapshotAlt ? "snapshot" : "fallback")} " +
                $"landedLike={landedLike}");
            return selectedAltitude;
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

        /// <summary>
        /// Regenerate vessel identity fields to avoid PID collisions with the original vessel.
        /// After revert, the original vessel is back on the pad with the same PIDs.
        /// Spawning a copy with identical PIDs causes map view / tracking station issues.
        /// </summary>
        private static void RegenerateVesselIdentity(ConfigNode spawnNode)
        {
            spawnNode.SetValue("pid", Guid.NewGuid().ToString("N"), true);
            spawnNode.SetValue("persistentId", "0", true);
        }

        private static void EnsureSpawnReadiness(ConfigNode spawnNode)
        {
            int fixCount = 0;

            // Discovery info — use DiscoveryLevels.Owned enum to avoid hardcoded magic numbers
            string ownedState = ((int)DiscoveryLevels.Owned).ToString();
            ConfigNode disc = spawnNode.GetNode("DISCOVERY");
            if (disc == null)
            {
                disc = spawnNode.AddNode("DISCOVERY");
                disc.AddValue("state", ownedState);
                disc.AddValue("lastObservedTime", "0");
                disc.AddValue("lifetime", "Infinity");
                disc.AddValue("refTime", "0");
                disc.AddValue("size", "2");
                fixCount++;
            }
            else
            {
                disc.SetValue("state", ownedState, true);
            }

            // Defensive: ensure required sub-nodes exist (snapshots from BackupVessel
            // should have these, but synthetic recordings or edge cases might not)
            if (spawnNode.GetNode("ACTIONGROUPS") == null)
            { spawnNode.AddNode("ACTIONGROUPS"); fixCount++; }
            if (spawnNode.GetNode("FLIGHTPLAN") == null)
            { spawnNode.AddNode("FLIGHTPLAN"); fixCount++; }
            if (spawnNode.GetNode("CTRLSTATE") == null)
            { spawnNode.AddNode("CTRLSTATE"); fixCount++; }
            if (spawnNode.GetNode("VESSELMODULES") == null)
            { spawnNode.AddNode("VESSELMODULES"); fixCount++; }

            if (fixCount > 0)
                ParsekLog.Info("Spawner", $"Spawn prep: added {fixCount} missing node(s) to snapshot");
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
