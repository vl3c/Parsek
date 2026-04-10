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
        // Proximity offset removed — spawn collision detection now uses bounding box
        // overlap check via SpawnCollisionDetector (same system as chain-tip spawns).

        /// <summary>
        /// Maximum consecutive collision-blocked frames before abandoning spawn.
        /// ~2.5 seconds at 60fps. After this many frames the spawn is abandoned
        /// to prevent infinite retry loops (bug #110).
        /// </summary>
        internal const int MaxCollisionBlocks = 150;

        public static ConfigNode TryBackupSnapshot(Vessel vessel)
        {
            if (vessel == null) return null;
            try
            {
                // BackupVessel() sets vessel.isBackingUp = true internally (confirmed
                // via decompilation) — PartModules see the flag during serialization. (#236)
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

        public static uint RespawnVessel(ConfigNode vesselNode, HashSet<string> excludeCrew = null, bool preserveIdentity = false)
        {
            try
            {
                ParsekLog.Verbose("Spawner",
                    $"RespawnVessel: excludeCrew={(excludeCrew != null ? excludeCrew.Count.ToString() : "null")}");

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
                CrewReservationManager.UnreserveCrewInSnapshot(spawnNode);

                // Remove crew that already exist on a loaded vessel (prevents duplication)
                RemoveDuplicateCrewFromSnapshot(spawnNode);

                if (!preserveIdentity)
                {
                    RegenerateVesselIdentity(spawnNode);
                }
                else
                {
                    ParsekLog.Info("Spawner", "RespawnVessel: preserving vessel identity (chain-tip spawn)");
                }
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

                // Suppress g-force destruction from spawn position correction (#235)
                pv.vesselRef.IgnoreGForces(240);

                // Zero velocity for surface spawns to prevent physics jitter (#239)
                ApplyPostSpawnStabilization(pv.vesselRef, spawnNode.GetValue("sit"));

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
            HashSet<string> excludeCrew = null, bool preserveIdentity = false,
            TerminalState? terminalState = null,
            Quaternion? rotation = null)
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
                string sit = DetermineSituation(alt, overWater, velocity.magnitude, orbitalSpeed);

                // Override FLYING situation from terminal state (#176 for Orbiting/Docked, #264
                // for Landed/Splashed). Without this, an EVA kerbal walking at alt > 0 with
                // low speed classifies as FLYING and hits the OrbitDriver updateMode=UPDATE
                // stale-orbit bug on the first physics frame after load.
                string overriddenSit = OverrideSituationFromTerminalState(sit, terminalState);
                if (overriddenSit != sit)
                {
                    ParsekLog.Info("Spawner",
                        $"SpawnAtPosition: overriding sit {sit} → {overriddenSit} " +
                        $"(terminal={terminalState}, speed={velocity.magnitude.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}, " +
                        $"orbitalSpeed={orbitalSpeed.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}) — prevents stale-orbit / pressure destruction (#176, #264)");
                    sit = overriddenSit;
                }

                ApplySituationToNode(spawnNode, sit);
                ParsekLog.Verbose("Spawner",
                    $"SpawnAtPosition: determined sit={sit} (alt={alt.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}, speed={velocity.magnitude.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}, " +
                    $"orbitalSpeed={orbitalSpeed.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}, overWater={overWater})");

                // Optional rotation override: for breakup-continuous spawns the last trajectory
                // point's rotation represents the near-impact orientation; without this, the
                // spawned vessel appears in its mid-flight tumbling pose. (#264 follow-up)
                if (rotation.HasValue)
                {
                    spawnNode.SetValue("rot", KSPUtil.WriteQuaternion(rotation.Value), true);
                    ParsekLog.Verbose("Spawner",
                        $"SpawnAtPosition: rotation override applied (rot={rotation.Value})");
                }

                // Rebuild ORBIT subnode from position + velocity
                var orbit = new Orbit();
                Vector3d worldPos = body.GetWorldSurfacePosition(lat, lon, alt);
                orbit.UpdateFromStateVectors(worldPos, velocity, body, ut);
                spawnNode.RemoveNode("ORBIT");
                ConfigNode orbitNode = new ConfigNode("ORBIT");
                SaveOrbitToNode(orbit, orbitNode, body);
                spawnNode.AddNode(orbitNode);

                // Crew handling
                RemoveDeadCrewFromSnapshot(spawnNode);
                EnsureCrewExistInRoster(spawnNode);
                if (excludeCrew != null && excludeCrew.Count > 0)
                    RemoveSpecificCrewFromSnapshot(spawnNode, excludeCrew);
                CrewReservationManager.UnreserveCrewInSnapshot(spawnNode);

                // Remove crew that already exist on a loaded vessel (prevents duplication)
                RemoveDuplicateCrewFromSnapshot(spawnNode);

                if (!preserveIdentity)
                {
                    RegenerateVesselIdentity(spawnNode);
                }
                else
                {
                    ParsekLog.Info("Spawner", "SpawnAtPosition: preserving vessel identity (chain-tip spawn)");
                }
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

                // Suppress g-force destruction from spawn position correction (#235)
                pv.vesselRef.IgnoreGForces(240);

                // Zero velocity for surface spawns to prevent physics jitter (#239)
                ApplyPostSpawnStabilization(pv.vesselRef, sit);

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

        public static void SpawnOrRecoverIfTooClose(Recording rec, int index)
        {
            const int maxSpawnAttempts = 3;
            if (rec.SpawnAttempts >= maxSpawnAttempts)
            {
                ParsekLog.Verbose("Spawner",
                    $"Spawn skipped for #{index} ({rec.VesselName}): max attempts ({maxSpawnAttempts}) already reached");
                return;
            }

            HashSet<string> excludeCrew = PrepareSnapshotForSpawn(rec, index);

            if (rec.Points.Count == 0 || FlightGlobals.Vessels == null)
            {
                LogSpawnContext(rec, double.MaxValue);
                rec.SpawnedVesselPersistentId = RespawnVessel(rec.VesselSnapshot, excludeCrew);
                rec.VesselSpawned = rec.SpawnedVesselPersistentId != 0;
                if (!rec.VesselSpawned)
                    LogSpawnFailure(rec, index, maxSpawnAttempts);
                return;
            }

            // Resolve spawn position from snapshot or trajectory endpoint
            var lastPt = rec.Points[rec.Points.Count - 1];
            double spawnLat, spawnLon, spawnAlt;
            ResolveSpawnPosition(rec, index, lastPt, out spawnLat, out spawnLon, out spawnAlt);

            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == lastPt.bodyName);
            if (body == null)
            {
                ParsekLog.Info("Spawner", $"Body lookup failed for spawn: bodyName='{lastPt.bodyName}' not found — " +
                    $"falling back to RespawnVessel without position");
                LogSpawnContext(rec, double.MaxValue);
                rec.SpawnedVesselPersistentId = RespawnVessel(rec.VesselSnapshot, excludeCrew);
                rec.VesselSpawned = rec.SpawnedVesselPersistentId != 0;
                if (!rec.VesselSpawned)
                    LogSpawnFailure(rec, index, maxSpawnAttempts);
                return;
            }

            Vector3d spawnPos = body.GetWorldSurfacePosition(spawnLat, spawnLon, spawnAlt);

            bool isEva = !string.IsNullOrEmpty(rec.EvaCrewName);
            var collision = CheckSpawnCollisions(rec, index, isEva, body,
                spawnLat, spawnLon, spawnAlt, spawnPos);
            if (collision.blocked) return;
            // Walkback may have rewritten the spawn coordinates (#264)
            spawnLat = collision.lat;
            spawnLon = collision.lon;
            spawnAlt = collision.alt;
            spawnPos = collision.pos;

            // EVA spawn position fix: update the snapshot's lat/lon/alt to the recording
            // endpoint. The snapshot was captured at EVA start (kerbal on the pod's ladder),
            // but the kerbal walked to a different location during the recording. Without
            // this override, the kerbal spawns on top of the parent vessel and grabs its
            // ladder, triggering KSP's "Kerbals on a ladder — cannot save" error.
            if (isEva && rec.VesselSnapshot != null)
            {
                OverrideSnapshotPosition(rec.VesselSnapshot, spawnLat, spawnLon, spawnAlt,
                    index, rec.VesselName);
                StripEvaLadderState(rec.VesselSnapshot, index, rec.VesselName);
            }

            // Breakup-continuous recordings: snapshot position is from breakup time (mid-air).
            // RespawnVessel uses raw snapshot position, so override it with the trajectory
            // endpoint. Same pattern as EVA fix above. (#224)
            bool isBreakupContinuous = rec.ChildBranchPointId != null && rec.TerminalStateValue.HasValue;
            if (!isEva && isBreakupContinuous && rec.VesselSnapshot != null)
            {
                OverrideSnapshotPosition(rec.VesselSnapshot, spawnLat, spawnLon, spawnAlt,
                    index, rec.VesselName);
            }

            // Surface terminal override: for any LANDED/SPLASHED recording, the snapshot
            // position may be from mid-flight (captured before the vessel reached its final
            // rest position). ResolveSpawnPosition clamped the altitude, but RespawnVessel
            // uses the raw snapshot. Override position and rotation so the vessel spawns
            // in its near-landing orientation, not the mid-flight descent pose. (#231)
            if (!isEva && !isBreakupContinuous && rec.VesselSnapshot != null
                && (rec.TerminalStateValue == TerminalState.Landed || rec.TerminalStateValue == TerminalState.Splashed))
            {
                OverrideSnapshotPosition(rec.VesselSnapshot, spawnLat, spawnLon, spawnAlt,
                    index, rec.VesselName, lastPt.rotation);
            }

            // Dead crew guard: if ALL crew in the snapshot are dead, abandon spawn.
            // Spawning a crewless command pod is worse than not spawning at all. (#170)
            // Individual dead crew are already removed by RespawnVessel.RemoveDeadCrewFromSnapshot;
            // this catches the case where the entire crew complement is dead.
            if (!isEva && rec.VesselSnapshot != null)
            {
                var snapshotCrew = ExtractCrewNamesFromSnapshot(rec.VesselSnapshot);
                var deadSet = BuildDeadCrewSet(snapshotCrew);
                if (ShouldBlockSpawnForDeadCrew(snapshotCrew, deadSet))
                {
                    rec.VesselSpawned = true;
                    rec.SpawnAbandoned = true;
                    ParsekLog.Warn("Spawner",
                        $"Spawn ABANDONED for #{index} ({rec.VesselName}): all {snapshotCrew.Count} crew " +
                        $"are dead/missing — [{string.Join(", ", snapshotCrew)}]");
                    return;
                }
            }

            // Find nearest vessel for spawn context logging
            double closestDist = FindNearestVesselDistance(spawnPos, body);

            LogSpawnContext(rec, closestDist);

            // SpawnAtPosition dispatch: orbital, EVA, and breakup-continuous all need the
            // ORBIT subnode rebuilt from the endpoint's lat/lon/alt + velocity.
            //
            // - Orbital/Docked (#171): raw snapshot orbit was captured during ascent
            //   (suborbital), KSP's on-rails pressure check would destroy the vessel at
            //   periapsis. SpawnAtPosition constructs a new orbit + correct sit from
            //   altitude+speed.
            // - EVA (#264): the snapshot's stale ORBIT subnode (captured when the kerbal
            //   was on the parent ladder) causes OrbitDriver.updateFromParameters to
            //   overwrite the corrected transform with the parent position on the first
            //   physics frame after load. SpawnAtPosition rebuilds the ORBIT from the
            //   walked endpoint so the kerbal stays where recorded.
            // - Breakup-continuous (#224 follow-up via #264): same stale-ORBIT mechanism.
            //   Rotation is preserved via the new Quaternion? rotation parameter.
            //
            // The earlier OverrideSnapshotPosition calls on the EVA and breakup-continuous
            // paths remain as defense-in-depth for the RespawnVessel fallback below: if
            // SpawnAtPosition returns 0, RespawnVessel still sees the corrected snapshot
            // lat/lon/alt (which at least places the kerbal correctly on frame 0 — the
            // stale-orbit bug only re-fires on frame 1).
            bool routeThroughSpawnAtPosition =
                rec.TerminalStateValue == TerminalState.Orbiting
                || rec.TerminalStateValue == TerminalState.Docked
                || isEva
                || isBreakupContinuous;
            if (routeThroughSpawnAtPosition)
            {
                Vector3d velocity = new Vector3d(lastPt.velocity.x, lastPt.velocity.y, lastPt.velocity.z);
                double spawnUT = Planetarium.GetUniversalTime();
                Quaternion? rotArg = isBreakupContinuous ? (Quaternion?)lastPt.rotation : null;

                string pathLabel;
                if (isEva) pathLabel = "EVA";
                else if (isBreakupContinuous) pathLabel = "Breakup";
                else pathLabel = "Orbital";

                rec.SpawnedVesselPersistentId = SpawnAtPosition(
                    rec.VesselSnapshot, body, spawnLat, spawnLon, spawnAlt, velocity, spawnUT, excludeCrew,
                    terminalState: rec.TerminalStateValue,
                    rotation: rotArg);
                rec.VesselSpawned = rec.SpawnedVesselPersistentId != 0;
                if (rec.VesselSpawned)
                {
                    ParsekLog.Info("Spawner",
                        $"{pathLabel} vessel spawn for recording #{index} ({rec.VesselName}) via SpawnAtPosition");
                    ParsekLog.ScreenMessage($"Vessel '{rec.VesselName}' has appeared!", 4f);
                    return;
                }

                // SpawnAtPosition failed — fall through to RespawnVessel as last-resort.
                // The earlier OverrideSnapshotPosition call on this path keeps the snapshot's
                // lat/lon/alt pointing at the recorded endpoint so the fallback still produces
                // a vessel at the right position on frame 0 (though the stale-orbit bug may
                // re-appear on frame 1 — acceptable for a degraded fallback).
                ParsekLog.Warn("Spawner",
                    $"SpawnAtPosition returned 0 for #{index} ({rec.VesselName}) — " +
                    $"falling through to RespawnVessel (degraded fallback)");
            }

            rec.SpawnedVesselPersistentId = RespawnVessel(rec.VesselSnapshot, excludeCrew);
            rec.VesselSpawned = rec.SpawnedVesselPersistentId != 0;
            if (rec.VesselSpawned)
            {
                ParsekLog.Info("Spawner", $"Vessel spawn for recording #{index} ({rec.VesselName})");
                ParsekLog.ScreenMessage($"Vessel '{rec.VesselName}' has appeared!", 4f);
            }
            else
            {
                LogSpawnFailure(rec, index, maxSpawnAttempts);
            }
        }

        /// <summary>
        /// Prepares the vessel snapshot for spawning: corrects unsafe situation,
        /// strips crew from destroyed recordings, and builds the exclude crew set.
        /// </summary>
        private static HashSet<string> PrepareSnapshotForSpawn(Recording rec, int index)
        {
            // Correct unsafe snapshot situation before spawning (#169).
            // Vessels captured mid-flight have sit=FLYING but terminal state may be Landed/Orbiting.
            // Without correction, KSP's on-rails pressure check destroys the vessel immediately.
            CorrectUnsafeSnapshotSituation(rec.VesselSnapshot, rec.TerminalStateValue);

            // Crew protection: strip crew from spawn snapshot when recording ended in
            // destruction to prevent killing crew during spawn-death cycles (#114).
            // Modifies the snapshot in-place — acceptable for Destroyed recordings
            // since they won't be re-spawned (blocked by FLYING/non-leaf checks).
            if (ShouldStripCrewForSpawn(rec))
            {
                if (rec.VesselSnapshot != null)
                {
                    int stripped = StripAllCrewFromSnapshot(rec.VesselSnapshot);
                    ParsekLog.Info("Spawner",
                        $"Stripped {stripped} crew from spawn snapshot for destroyed recording " +
                        $"#{index} ({rec.VesselName}) — prevents crew death on spawn");
                }
            }

            // Build exclude set once for all spawn paths (EVA'd crew spawn via child recordings)
            return BuildExcludeCrewSet(rec);
        }

        /// <summary>
        /// Checks KSC exclusion zone and bounding box overlap collisions. On bounding-box
        /// overlap, runs the duplicate-blocker recovery path (#112) first; if that doesn't
        /// clear the overlap, runs the subdivided trajectory walkback (#264) to find an
        /// earlier collision-free sub-step. The walkback is now applied to EVA kerbals too
        /// (the previous `!isEva` guard was removed because the endpoint-spawn path now
        /// places EVAs accurately and can overlap with a parent vessel).
        ///
        /// Returns <c>blocked=true</c> to tell the caller to skip spawning this frame.
        /// On successful walkback, returns <c>blocked=false</c> with the rewritten
        /// <c>(lat, lon, alt, pos)</c>; the caller must honour these values when dispatching
        /// to <c>SpawnAtPosition</c>/<c>RespawnVessel</c>. Resets CollisionBlockCount on a
        /// clear or walkback-rewritten path.
        /// </summary>
        private static (bool blocked, double lat, double lon, double alt, Vector3d pos) CheckSpawnCollisions(
            Recording rec, int index, bool isEva, CelestialBody body,
            double spawnLat, double spawnLon, double spawnAlt, Vector3d spawnPos)
        {
            // KSC exclusion zone — block spawn near the launch pad to prevent collisions
            // with KSC infrastructure that isn't in FlightGlobals.Vessels. (#170)
            if (!isEva && body.isHomeWorld &&
                SpawnCollisionDetector.IsWithinKscExclusionZone(
                    spawnLat, spawnLon, body.Radius,
                    SpawnCollisionDetector.DefaultKscExclusionRadiusMeters))
            {
                rec.CollisionBlockCount++;
                if (ShouldAbandonCollisionBlockedSpawn(rec.CollisionBlockCount, MaxCollisionBlocks))
                {
                    rec.VesselSpawned = true;
                    rec.SpawnAbandoned = true;
                    ParsekLog.Warn("Spawner",
                        $"Spawn ABANDONED for #{index} ({rec.VesselName}): within KSC exclusion zone " +
                        $"(lat={spawnLat:F4}, lon={spawnLon:F4}) for {rec.CollisionBlockCount} consecutive frames — " +
                        $"giving up (max={MaxCollisionBlocks})");
                }
                else
                {
                    ParsekLog.VerboseRateLimited("Spawner",
                        "ksc-exclusion-" + index,
                        $"Spawn blocked for #{index} ({rec.VesselName}): within KSC exclusion zone " +
                        $"(lat={spawnLat:F4}, lon={spawnLon:F4}) — will retry next frame " +
                        $"(block={rec.CollisionBlockCount}/{MaxCollisionBlocks})");
                }
                return (true, spawnLat, spawnLon, spawnAlt, spawnPos);
            }

            // Bounding box overlap check — block spawn if overlapping a loaded vessel.
            // Applies to EVA kerbals too (#264) — the previous skip was based on the
            // assumption that EVAs spawn exactly where recorded, which was broken before
            // the #264 fix landed.
            Bounds spawnBounds = SpawnCollisionDetector.ComputeVesselBounds(rec.VesselSnapshot);
            if (isEva)
            {
                ParsekLog.VerboseRateLimited("Spawner", "eva-bounds-" + index,
                    $"CheckSpawnCollisions: EVA bounds={spawnBounds.size} for #{index} ({rec.VesselName})");
            }
            // EVA spawns must detect the active vessel (parent rocket) as a blocker
            // so walkback can find a clear position (#291). Non-EVA spawns skip it
            // to avoid the player's vessel blocking its own recording's spawn.
            bool skipActive = !isEva;
            var (overlap, overlapDist, blockerName, blockerVessel) =
                SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(spawnPos, spawnBounds, 5f, skipActive);
            if (overlap)
            {
                // Precedence: duplicate-blocker-recovery FIRST (#112), walkback SECOND (#264).
                // A quicksave-loaded duplicate takes one frame to recover via
                // ShipConstruction.RecoverVesselFromFlight; if that clears the overlap, the
                // original position is still valid and we must NOT walk back unnecessarily.
                string resolvedRecName = Recording.ResolveLocalizedName(rec.VesselName);
                if (!rec.DuplicateBlockerRecovered
                    && blockerVessel != null
                    && blockerVessel.loaded
                    && ShouldRecoverBlockerVessel(blockerName, resolvedRecName))
                {
                    ParsekLog.Warn("Spawner",
                        $"Duplicate blocker detected for #{index} ({rec.VesselName}): " +
                        $"recovering '{blockerName}' (pid={blockerVessel.persistentId}) at {overlapDist:F0}m — " +
                        $"likely quicksave-loaded duplicate (#112)");
                    ShipConstruction.RecoverVesselFromFlight(
                        blockerVessel.protoVessel, HighLogic.CurrentGame.flightState, true);
                    rec.DuplicateBlockerRecovered = true;
                    rec.CollisionBlockCount = 0;

                    // Re-check overlap after recovery — another vessel may still block
                    var (stillOverlap, recheckDist, recheckName, _) =
                        SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(spawnPos, spawnBounds, 5f, skipActive);
                    if (!stillOverlap)
                    {
                        // Blocker removed, no other overlap — fall through to spawn at original position
                        return (false, spawnLat, spawnLon, spawnAlt, spawnPos);
                    }

                    ParsekLog.Verbose("Spawner",
                        $"Post-recovery overlap persists for #{index}: '{recheckName}' at {recheckDist:F0}m — trying walkback");
                    // Fall through to the walkback branch
                }

                // Walkback: if the recording has enough trajectory points, step backward with
                // 1.5 m linear sub-steps until we find a clear position. (#264)
                if (rec.Points != null && rec.Points.Count > 1)
                {
                    double walkLat, walkLon, walkAlt;
                    bool walked = TryWalkbackForEndOfRecordingSpawn(
                        rec, index, spawnBounds, body, out walkLat, out walkLon, out walkAlt);
                    if (walked)
                    {
                        Vector3d walkPos = body.GetWorldSurfacePosition(walkLat, walkLon, walkAlt);
                        rec.CollisionBlockCount = 0;
                        ParsekLog.Info("Spawner",
                            $"CheckSpawnCollisions: walkback rewrote spawn position for #{index} ({rec.VesselName}) — " +
                            $"original overlap with '{blockerName}' at {overlapDist:F0}m cleared");
                        return (false, walkLat, walkLon, walkAlt, walkPos);
                    }
                    // Walkback exhausted — TryWalkbackForEndOfRecordingSpawn already set
                    // SpawnAbandoned / WalkbackExhausted / VesselSpawned. Report blocked.
                    return (true, spawnLat, spawnLon, spawnAlt, spawnPos);
                }

                // Fallback for 1-point / empty trajectories (synthetic recordings):
                // use the existing CollisionBlockCount / MaxCollisionBlocks retry path.
                rec.CollisionBlockCount++;
                if (ShouldAbandonCollisionBlockedSpawn(rec.CollisionBlockCount, MaxCollisionBlocks))
                {
                    rec.VesselSpawned = true;   // prevent ShouldSpawnAtRecordingEnd from returning true
                    rec.SpawnAbandoned = true;  // prevent vessel-gone check from resetting VesselSpawned
                    ParsekLog.Warn("Spawner",
                        $"Spawn ABANDONED for #{index} ({rec.VesselName}): collision-blocked for " +
                        $"{rec.CollisionBlockCount} consecutive frames by '{blockerName}' at {overlapDist:F0}m " +
                        $"(single-point trajectory, no walkback possible) — giving up (max={MaxCollisionBlocks})");
                }
                else
                {
                    ParsekLog.VerboseRateLimited("Spawner",
                        "collision-block-" + index,
                        $"Spawn blocked for #{index} ({rec.VesselName}): overlaps '{blockerName}' at {overlapDist:F0}m " +
                        $"(single-point trajectory) — will retry next frame (block={rec.CollisionBlockCount}/{MaxCollisionBlocks})");
                }
                return (true, spawnLat, spawnLon, spawnAlt, spawnPos);
            }

            // Reset collision block counter on successful spawn path
            rec.CollisionBlockCount = 0;
            return (false, spawnLat, spawnLon, spawnAlt, spawnPos);
        }

        /// <summary>
        /// Walk backward along the recording's trajectory with 1.5 m linear sub-steps,
        /// looking for the first position whose bounding box does NOT overlap any loaded
        /// vessel. On success, writes the walkback position into the out parameters and
        /// returns true. On exhaustion (entire trajectory overlaps), marks the recording
        /// with SpawnAbandoned + WalkbackExhausted + VesselSpawned and returns false.
        ///
        /// Used by CheckSpawnCollisions for end-of-recording spawns when the endpoint
        /// overlaps a loaded vessel (typically the parent vessel for an EVA recording).
        /// Unlike VesselGhoster's point-granularity walkback, this runs with metric-step
        /// subdivision so fast-moving trajectories don't skip 10-50 m per iteration. (#264)
        /// </summary>
        internal static bool TryWalkbackForEndOfRecordingSpawn(
            Recording rec, int index, Bounds spawnBounds, CelestialBody body,
            out double walkLat, out double walkLon, out double walkAlt)
        {
            walkLat = 0;
            walkLon = 0;
            walkAlt = 0;

            if (rec == null || rec.Points == null || rec.Points.Count == 0 || body == null)
            {
                ParsekLog.Verbose("Spawner",
                    $"TryWalkbackForEndOfRecordingSpawn: rec/body null or empty trajectory for #{index} — cannot walkback");
                return false;
            }

            // EVA spawns must detect the active vessel as a blocker (#291)
            bool skipActive = string.IsNullOrEmpty(rec.EvaCrewName);
            var walkResult = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                rec.Points,
                body.Radius,
                SpawnCollisionDetector.DefaultWalkbackStepMeters,
                (lat, lon, alt) => body.GetWorldSurfacePosition(lat, lon, alt),
                worldPos =>
                {
                    var (ov, _, _, _) = SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(
                        worldPos, spawnBounds, 5f, skipActive);
                    return ov;
                });

            if (walkResult.found)
            {
                walkLat = walkResult.lat;
                walkLon = walkResult.lon;
                walkAlt = walkResult.alt;

                // Clamp altitude for surface terminals (#231) — the walkback candidate may be
                // mid-flight altitude; ClampAltitudeForLanded puts the root part above terrain.
                if (rec.TerminalStateValue == TerminalState.Landed)
                {
                    double terrainAlt = body.TerrainAltitude(walkLat, walkLon);
                    walkAlt = ClampAltitudeForLanded(walkAlt, terrainAlt, index, rec.VesselName);
                }
                else if (rec.TerminalStateValue == TerminalState.Splashed && walkAlt > 0)
                {
                    walkAlt = 0;
                }

                ParsekLog.Info("Spawner",
                    $"TryWalkbackForEndOfRecordingSpawn: found clear position for #{index} ({rec.VesselName}) " +
                    $"lat={walkLat.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"lon={walkLon.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"alt={walkAlt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
                return true;
            }

            // Exhausted — mark the recording so the UI/diagnostics can distinguish this
            // from the KSC-exclusion / MaxCollisionBlocks abandon paths.
            rec.SpawnAbandoned = true;
            rec.WalkbackExhausted = true;
            rec.VesselSpawned = true; // prevent vessel-gone check from resetting VesselSpawned
            ParsekLog.Warn("Spawner",
                $"Spawn ABANDONED for #{index} ({rec.VesselName}): entire trajectory overlaps " +
                $"with loaded vessels — walkback exhausted");
            return false;
        }

        /// <summary>
        /// Resolve spawn position. EVA and breakup-continuous recordings use the trajectory
        /// endpoint (snapshot position is stale — from EVA start or breakup time).
        /// Other recordings use the snapshot lat/lon/alt, falling back to the trajectory
        /// endpoint if snapshot lacks position data (#127).
        /// All paths fall through to altitude clamping for surface terminal states (#231).
        /// </summary>
        internal static void ResolveSpawnPosition(Recording rec, int index,
            TrajectoryPoint lastPt, out double lat, out double lon, out double alt)
        {
            lat = 0; lon = 0; alt = 0;

            // EVA (#175): snapshot position is from EVA start (kerbal on the pod's ladder).
            // Breakup-continuous (#224): snapshot position is from breakup time (mid-air).
            // Both use trajectory endpoint instead. No early return — falls through to clamping.
            bool isEva = !string.IsNullOrEmpty(rec.EvaCrewName);
            bool isBreakupContinuous = rec.ChildBranchPointId != null && rec.TerminalStateValue.HasValue;
            bool useTrajectoryEndpoint = isEva || isBreakupContinuous;

            if (useTrajectoryEndpoint)
            {
                lat = lastPt.latitude;
                lon = lastPt.longitude;
                alt = lastPt.altitude;
                string reason = isEva
                    ? "EVA endpoint (snapshot is from EVA start)"
                    : "breakup-continuous endpoint (snapshot is from breakup time)";
                ParsekLog.Verbose("Spawner",
                    $"Spawn #{index} ({rec.VesselName}): using trajectory endpoint — {reason}");
            }
            else
            {
                bool hasSnapshotPos = TryGetSnapshotDouble(rec.VesselSnapshot, "lat", out lat)
                                   && TryGetSnapshotDouble(rec.VesselSnapshot, "lon", out lon)
                                   && TryGetSnapshotDouble(rec.VesselSnapshot, "alt", out alt);
                if (!hasSnapshotPos)
                {
                    lat = lastPt.latitude;
                    lon = lastPt.longitude;
                    alt = lastPt.altitude;
                    ParsekLog.Verbose("Spawner",
                        $"No snapshot lat/lon/alt for #{index} ({rec.VesselName}) — using trajectory endpoint for collision check");
                }
            }

            // Safety net: clamp altitude for surface terminal states.
            // The last trajectory point or snapshot may be from while still descending —
            // altitude is above the actual rest position. Without clamping, KSP spawns
            // a LANDED vessel in mid-air, reclassifies to FLYING, and it falls and crashes.
            // SPLASHED: clamp to sea level. LANDED: clamp to terrain height. (#224, #231)
            if (rec.TerminalStateValue == TerminalState.Splashed)
            {
                if (alt > 0)
                {
                    ParsekLog.Verbose("Spawner",
                        $"Clamped altitude for SPLASHED spawn #{index} ({rec.VesselName}): {alt:F1} -> 0");
                    alt = 0;
                }
            }
            else if (rec.TerminalStateValue == TerminalState.Landed)
            {
                string bodyName = lastPt.bodyName ?? "Kerbin";
                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                if (body != null)
                {
                    double terrainAlt = body.TerrainAltitude(lat, lon);
                    alt = ClampAltitudeForLanded(alt, terrainAlt, index, rec.VesselName);
                }
            }
        }

        /// <summary>
        /// Pure: clamp altitude to terrain height + clearance for LANDED spawns.
        /// KSP's alt field positions the vessel's root part (typically the command pod at the top).
        /// Setting alt = terrainAlt puts the root part at ground level, burying lower parts
        /// (heat shields, engines, landing legs) underground. The clearance offset ensures the
        /// entire vessel is above the surface so KSP's physics can settle it naturally. (#231)
        ///
        /// <para>Bug #282: the original implementation left <c>alt ∈ [terrainAlt, target)</c>
        /// unchanged on the assumption that "alt ≥ terrain ⇒ not underground". That only
        /// holds if the root part origin is the lowest point of the vessel, which is
        /// false for any vessel with the command pod on top (Mk1-3: ~1.77 m below root).
        /// The 2026-04-09 s33 playtest had three landed leaves with clearances 0.8/0.9/1.3 m,
        /// all in the no-op range, all spawned clipped into terrain. The gap is now closed:
        /// any alt below target is clamped up regardless of whether it's below terrain.</para>
        /// </summary>
        internal const double LandedClearanceMeters = 2.0;

        /// <summary>
        /// Clearance for kinematic ghost playback of Landed/Splashed recordings (#282).
        /// Larger than <see cref="LandedClearanceMeters"/> because ghost playback is not
        /// physics-driven — there is no part-damage concern, so we can use a margin that
        /// clears the Mk1-3 pod's 1.77 m root-to-bottom offset with headroom to spare.
        /// For taller stacks the root origin sits even higher, so the pod may still
        /// float slightly above the recorded landing position, but the alternative
        /// (burying the mesh in terrain) is worse. Per-vessel clearance derived from
        /// bounds is tracked as a follow-up in docs/dev/todo-and-known-bugs.md.
        /// </summary>
        internal const double LandedGhostClearanceMeters = 4.0;

        internal static double ClampAltitudeForLanded(double alt, double terrainAlt,
            int index, string vesselName)
        {
            var ic = CultureInfo.InvariantCulture;
            double target = terrainAlt + LandedClearanceMeters;

            if (alt > target)
            {
                // Above target: clamp down (vessel was still descending when recorded)
                double delta = alt - target;
                ParsekLog.Verbose("Spawner",
                    $"Clamped altitude for LANDED spawn #{index} ({vesselName}): " +
                    $"{alt.ToString("F1", ic)} -> {target.ToString("F1", ic)} " +
                    $"(down-clamp, delta={delta.ToString("F1", ic)})");
                return target;
            }
            if (alt < target)
            {
                // Below target: clamp up. Reason depends on whether alt is also below terrain
                // (truly underground) or just in the low-clearance gap above terrain.
                double delta = target - alt;
                string reason = alt < terrainAlt ? "underground" : "low-clearance";
                ParsekLog.Verbose("Spawner",
                    $"Clamped altitude for LANDED spawn #{index} ({vesselName}): " +
                    $"{alt.ToString("F1", ic)} -> {target.ToString("F1", ic)} " +
                    $"({reason}, delta={delta.ToString("F1", ic)})");
                return target;
            }
            // alt == target exactly — no-op (rare, but possible after a prior clamp)
            return alt;
        }

        /// <summary>
        /// Find the distance to the nearest loaded vessel on the same body.
        /// Used for spawn context logging.
        /// </summary>
        private static double FindNearestVesselDistance(Vector3d spawnPos, CelestialBody body)
        {
            double closestDist = double.MaxValue;
            for (int v = 0; v < FlightGlobals.Vessels.Count; v++)
            {
                Vessel other = FlightGlobals.Vessels[v];
                if (GhostMapPresence.IsGhostMapVessel(other.persistentId)) continue;
                if (other.mainBody != body) continue;
                double dist = Vector3d.Distance(spawnPos, other.GetWorldPos3D());
                if (dist < closestDist)
                    closestDist = dist;
            }
            return closestDist;
        }

        /// <summary>
        /// Builds a set of crew names from the given list that are Dead or Missing
        /// in the KSP crew roster. Runtime-only: accesses HighLogic.CurrentGame.CrewRoster.
        /// Returns an empty set if the roster is unavailable. (#170)
        /// </summary>
        private static HashSet<string> BuildDeadCrewSet(List<string> crewNames)
        {
            var deadSet = new HashSet<string>();
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return deadSet;

            for (int i = 0; i < crewNames.Count; i++)
            {
                if (IsCrewDeadInRoster(crewNames[i], roster))
                    deadSet.Add(crewNames[i]);
            }
            return deadSet;
        }

        /// <summary>
        /// Log spawn failure and increment attempt counter. Logs error on final
        /// attempt, warning on retryable failures.
        /// </summary>
        private static void LogSpawnFailure(Recording rec, int index, int maxAttempts)
        {
            rec.SpawnAttempts++;
            if (rec.SpawnAttempts >= maxAttempts)
                ParsekLog.Error("Spawner", $"Vessel spawn failed permanently for recording #{index} ({rec.VesselName}) after {maxAttempts} attempts");
            else
                ParsekLog.Warn("Spawner", $"Vessel spawn failed for recording #{index} ({rec.VesselName}) — will retry (attempt {rec.SpawnAttempts}/{maxAttempts})");
        }

        /// <summary>
        /// Pure decision: should we abandon a collision-blocked spawn?
        /// Returns true when the collision block count has reached or exceeded the maximum.
        /// </summary>
        internal static bool ShouldAbandonCollisionBlockedSpawn(int collisionBlockCount, int maxBlocks)
        {
            return collisionBlockCount >= maxBlocks;
        }

        /// <summary>
        /// Pure decision: should the blocking vessel be recovered as a likely duplicate?
        /// Returns true when the blocker's resolved name matches the recording's vessel name,
        /// indicating a leftover from a previous spawn (e.g., quicksave-loaded duplicate after rewind).
        /// Both names must already be resolved via ResolveLocalizedName. (#112)
        /// </summary>
        internal static bool ShouldRecoverBlockerVessel(string blockerName, string recordingVesselName)
        {
            if (string.IsNullOrEmpty(blockerName) || string.IsNullOrEmpty(recordingVesselName))
                return false;
            return string.Equals(blockerName, recordingVesselName, StringComparison.Ordinal);
        }

        /// <summary>
        /// Maximum spawn-then-die cycles before abandoning spawn.
        /// A vessel that spawns and immediately dies (e.g., FLYING at sea level,
        /// destroyed by KSP on-rails aero check) should not retry forever.
        /// </summary>
        internal const int MaxSpawnDeathCycles = 3;

        /// <summary>
        /// Pure decision: should we abandon a spawn that keeps dying immediately?
        /// Returns true when the spawn-death count has reached or exceeded the maximum.
        /// </summary>
        internal static bool ShouldAbandonSpawnDeathLoop(int spawnDeathCount, int maxCycles)
        {
            return spawnDeathCount >= maxCycles;
        }

        internal static HashSet<string> BuildExcludeCrewSet(Recording rec)
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

        internal static void LogSpawnContext(Recording rec, double closestDist)
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
            if (existingCrew.Count == 0)
            {
                ParsekLog.Verbose("Spawner", "Crew dedup: no existing crew in scene — skipped");
                return;
            }

            // Extract crew from the snapshot and find duplicates
            var snapshotCrew = CrewReservationManager.ExtractCrewFromSnapshot(snapshot);
            var duplicates = FindDuplicateCrew(snapshotCrew, existingCrew);

            if (duplicates.Count == 0)
            {
                ParsekLog.Verbose("Spawner",
                    $"Crew dedup: checked {snapshotCrew.Count} crew against {existingCrew.Count} existing — no duplicates");
                return;
            }

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
                if (GhostMapPresence.IsGhostMapVessel(FlightGlobals.Vessels[v].persistentId)) continue;
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

        /// <summary>
        /// Pure decision: should crew be stripped from the spawn snapshot?
        /// Returns true when the recording's terminal state is Destroyed,
        /// preventing crew deaths during spawn-death cycles. (#114)
        /// </summary>
        internal static bool ShouldStripCrewForSpawn(Recording rec)
        {
            return rec.TerminalStateValue.HasValue
                && rec.TerminalStateValue.Value == TerminalState.Destroyed;
        }

        /// <summary>
        /// Pure decision: determines the corrected situation string when the snapshot's
        /// situation is unsafe (FLYING/SUB_ORBITAL) but the terminal state indicates the
        /// vessel safely reached a stable state. Returns the corrected situation string, or
        /// null if no correction is needed.
        ///
        /// Bug #169: EVA vessels captured with sit=FLYING but terminal state Landed are
        /// destroyed by KSP's on-rails atmospheric pressure check when spawned outside
        /// physics range. The snapshot's sit field must be corrected to match the terminal
        /// state before spawning. Also handles Orbiting: vessels captured during ascent
        /// (FLYING) that achieved orbit.
        /// </summary>
        internal static string ComputeCorrectedSituation(string snapshotSit, TerminalState? terminalState)
        {
            if (string.IsNullOrEmpty(snapshotSit))
                return null;

            bool isUnsafe = snapshotSit.Equals("FLYING", StringComparison.OrdinalIgnoreCase)
                         || snapshotSit.Equals("SUB_ORBITAL", StringComparison.OrdinalIgnoreCase);
            if (!isUnsafe)
                return null;

            if (!terminalState.HasValue)
                return null;

            switch (terminalState.Value)
            {
                case TerminalState.Landed:
                    return "LANDED";
                case TerminalState.Splashed:
                    return "SPLASHED";
                case TerminalState.Orbiting:
                    return "ORBITING";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Corrects the snapshot's situation field if it is FLYING/SUB_ORBITAL but the
        /// terminal state indicates the vessel safely reached a stable state (Landed/Splashed/Orbiting).
        /// Modifies the snapshot in-place. Returns true if a correction was applied.
        /// Bug #169: prevents KSP's on-rails pressure check from destroying spawned vessels.
        /// </summary>
        internal static bool CorrectUnsafeSnapshotSituation(ConfigNode snapshot, TerminalState? terminalState)
        {
            if (snapshot == null)
                return false;

            string currentSit = snapshot.GetValue("sit");
            string corrected = ComputeCorrectedSituation(currentSit, terminalState);
            if (corrected == null)
                return false;

            ApplySituationToNode(snapshot, corrected);
            ParsekLog.Info("Spawner",
                $"Corrected unsafe snapshot situation: {currentSit} -> {corrected} " +
                $"(terminal={terminalState}) — prevents on-rails pressure destruction (#169)");
            return true;
        }

        /// <summary>
        /// Overrides the snapshot's lat/lon/alt with the given endpoint coordinates.
        /// Optionally overrides rotation from the last trajectory point so the vessel
        /// spawns in its near-landing orientation rather than the mid-flight snapshot orientation.
        /// Modifies the snapshot in-place.
        /// </summary>
        internal static void OverrideSnapshotPosition(ConfigNode snapshot,
            double lat, double lon, double alt, int index, string vesselName,
            Quaternion? rotation = null)
        {
            if (snapshot == null) return;

            string oldAlt = snapshot.GetValue("alt") ?? "?";

            snapshot.SetValue("lat", lat.ToString("R", CultureInfo.InvariantCulture), true);
            snapshot.SetValue("lon", lon.ToString("R", CultureInfo.InvariantCulture), true);
            snapshot.SetValue("alt", alt.ToString("R", CultureInfo.InvariantCulture), true);

            if (rotation.HasValue)
            {
                snapshot.SetValue("rot", KSPUtil.WriteQuaternion(rotation.Value), true);
            }

            ParsekLog.Info("Spawner",
                $"Snapshot position override for #{index} ({vesselName}): " +
                $"alt {oldAlt} → {alt.ToString("R", CultureInfo.InvariantCulture)}" +
                (rotation.HasValue ? " (rot updated)" : ""));
        }

        /// <summary>
        /// Strips ladder-grab animation state from EVA kerbal snapshots.
        /// KSP's KerbalEVA FSM stores the current state in MODULE data. If the snapshot
        /// was captured while the kerbal was on a ladder (e.g., at EVA start), the spawned
        /// kerbal initializes in ladder mode even when far from any ladder. KSP then
        /// blocks all saves with "There are Kerbals on a ladder. Cannot save."
        /// This clears the ladder state so the kerbal spawns idle on the ground.
        /// Modifies the snapshot in-place. (#175 follow-up)
        /// </summary>
        internal static void StripEvaLadderState(ConfigNode snapshot, int index, string vesselName)
        {
            if (snapshot == null) return;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                foreach (ConfigNode moduleNode in partNode.GetNodes("MODULE"))
                {
                    string moduleName = moduleNode.GetValue("name");
                    if (moduleName != "KerbalEVA" && moduleName != "KerbalEVAFlight")
                        continue;

                    // Check and clear ladder-related FSM state. Real KerbalEVA states
                    // are st_idle_gr / st_idle_fl / st_swim_idle — picked at runtime
                    // by KerbalEVA.StartEVA based on situation. Writing a literal
                    // "idle" produces an unknown-state exception (caught by KSP, falls
                    // back to SurfaceContact-driven default). Removing the value lets
                    // the FSM initialize fresh with the correct state name (#264 follow-up).
                    string currentState = moduleNode.GetValue("state");
                    if (currentState != null && currentState.IndexOf("ladder", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        moduleNode.RemoveValue("state");
                        ParsekLog.Info("Spawner",
                            $"EVA ladder state stripped for #{index} ({vesselName}): '{currentState}' → (removed; KSP will reinitialize)");
                    }

                    // Also clear any ladder-related boolean flags
                    string onLadder = moduleNode.GetValue("OnALadder");
                    if (onLadder != null && onLadder.ToLowerInvariant() == "true")
                    {
                        moduleNode.SetValue("OnALadder", "False", true);
                        ParsekLog.Verbose("Spawner",
                            $"EVA OnALadder flag cleared for #{index} ({vesselName})");
                    }
                }
            }
        }

        /// <summary>
        /// Strips ALL crew from a vessel snapshot. Removes crew values from PART
        /// nodes and resets the crewAssignment field. Returns the number of crew removed.
        /// Modifies the snapshot in-place. (#114)
        /// </summary>
        internal static int StripAllCrewFromSnapshot(ConfigNode snapshot)
        {
            if (snapshot == null) return 0;

            int removedCount = 0;
            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                var crewNames = partNode.GetValues("crew");
                if (crewNames.Length > 0)
                {
                    for (int c = 0; c < crewNames.Length; c++)
                    {
                        ParsekLog.Verbose("Spawner",
                            $"StripAllCrewFromSnapshot: removing '{crewNames[c]}'");
                    }
                    removedCount += crewNames.Length;
                    partNode.RemoveValues("crew");
                }
            }
            return removedCount;
        }

        public static void RemoveDeadCrewFromSnapshot(ConfigNode snapshot)
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                ParsekLog.Warn("Spawner", "RemoveDeadCrewFromSnapshot skipped: crew roster unavailable");
                return;
            }

            var reserved = CrewReservationManager.CrewReplacements;
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
                    bool isDeadOrMissing = IsCrewDeadInRoster(name, roster);

                    // Reserved crew with Missing status are kept — Missing can be
                    // stale state from save manipulation (e.g. --clean-start).
                    // But Dead is permanent — a dead reserved crew member must be
                    // removed to avoid resurrecting them. (#170)
                    // Use Dead-only check: IsCrewDeadInRoster includes Missing,
                    // which would incorrectly remove reserved+Missing crew.
                    bool isStrictlyDead = IsCrewStrictlyDeadInRoster(name, roster);
                    if (reserved.ContainsKey(name) && !isStrictlyDead)
                    {
                        keepNames.Add(name);
                        continue;
                    }

                    if (isDeadOrMissing)
                    {
                        ParsekLog.Info("Spawner", $"Removed dead/missing crew '{name}' from vessel snapshot" +
                            (reserved.ContainsKey(name) ? " (was reserved but Dead overrides)" : ""));
                        removedCount++;
                        removedAny = true;
                    }
                    else
                    {
                        keepNames.Add(name);
                    }
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
        /// Checks whether a crew member is Dead or Missing in the KSP crew roster.
        /// Returns false if the crew member is not found in the roster (unknown crew
        /// are not considered dead — they may be from synthetic recordings).
        /// Runtime-only: requires HighLogic.CurrentGame.CrewRoster via the roster parameter.
        /// </summary>
        private static bool IsCrewDeadInRoster(string crewName, KerbalRoster roster)
        {
            foreach (ProtoCrewMember pcm in roster.Crew)
            {
                if (pcm.name == crewName &&
                    (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Dead ||
                     pcm.rosterStatus == ProtoCrewMember.RosterStatus.Missing))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Checks whether a crew member is strictly Dead (not Missing) in the KSP crew roster.
        /// Used by RemoveDeadCrewFromSnapshot for reserved crew: Missing is potentially stale
        /// state from save manipulation, but Dead is permanent and must override reservation. (#170)
        /// </summary>
        private static bool IsCrewStrictlyDeadInRoster(string crewName, KerbalRoster roster)
        {
            foreach (ProtoCrewMember pcm in roster.Crew)
            {
                if (pcm.name == crewName &&
                    pcm.rosterStatus == ProtoCrewMember.RosterStatus.Dead)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Pure decision: should a spawn be blocked because all crew in the snapshot are dead?
        /// Returns true when every crew member listed in the snapshot is in the dead names set.
        /// A vessel with no crew at all returns false (crewless vessels can spawn).
        /// Extracted for testability. (#170)
        /// </summary>
        /// <param name="crewNamesInSnapshot">Crew names from the vessel snapshot PART nodes.</param>
        /// <param name="deadCrewNames">Set of crew names known to be dead/missing.</param>
        internal static bool ShouldBlockSpawnForDeadCrew(
            List<string> crewNamesInSnapshot, HashSet<string> deadCrewNames)
        {
            if (crewNamesInSnapshot == null || crewNamesInSnapshot.Count == 0)
            {
                ParsekLog.Verbose("Spawner", "ShouldBlockSpawnForDeadCrew: no crew in snapshot — not blocking");
                return false;
            }

            if (deadCrewNames == null || deadCrewNames.Count == 0)
            {
                ParsekLog.Verbose("Spawner",
                    $"ShouldBlockSpawnForDeadCrew: no dead crew — not blocking ({crewNamesInSnapshot.Count} crew alive)");
                return false;
            }

            int deadCount = 0;
            for (int i = 0; i < crewNamesInSnapshot.Count; i++)
            {
                if (deadCrewNames.Contains(crewNamesInSnapshot[i]))
                    deadCount++;
            }

            bool allDead = deadCount == crewNamesInSnapshot.Count;

            ParsekLog.Verbose("Spawner",
                $"ShouldBlockSpawnForDeadCrew: {deadCount}/{crewNamesInSnapshot.Count} crew dead — " +
                (allDead ? "blocking spawn (all crew dead)" : "allowing spawn (some crew alive)"));

            return allDead;
        }

        /// <summary>
        /// Extracts all crew names from a vessel snapshot's PART nodes.
        /// Returns an empty list if the snapshot is null or has no crew.
        /// </summary>
        internal static List<string> ExtractCrewNamesFromSnapshot(ConfigNode snapshot)
        {
            var names = new List<string>();
            if (snapshot == null) return names;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                var crewNames = partNode.GetValues("crew");
                for (int i = 0; i < crewNames.Length; i++)
                {
                    if (!string.IsNullOrEmpty(crewNames[i]))
                        names.Add(crewNames[i]);
                }
            }
            return names;
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
                {
                    if (GhostMapPresence.IsGhostMapVessel(pid)) return false;
                    return true;
                }
            }
            return false;
        }

        public static void SnapshotVessel(
            Recording pending,
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

                // End biome from last trajectory point (vessel already destroyed)
                if (pending.Points.Count > 0)
                {
                    var lastPt = pending.Points[pending.Points.Count - 1];
                    pending.EndBiome = TryResolveBiome(lastPt.bodyName, lastPt.latitude, lastPt.longitude);
                }

                ParsekLog.Info("Spawner", $"Vessel was destroyed during recording. Distance from launch: {pending.DistanceFromLaunch:F0}m, " +
                    $"Max distance: {pending.MaxDistanceFromLaunch:F0}m, Snapshot kept: {pending.VesselSnapshot != null}");
                return;
            }

            Vessel vessel = vesselOverride ?? FlightGlobals.ActiveVessel;
            ParsekLog.Verbose("Spawner",
                $"SnapshotVessel: using {(vesselOverride != null ? "override" : "active")} vessel, " +
                $"vesselDestroyed={vesselDestroyed}, points={pending.Points.Count}");
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

            // Build situation string (humanized, not raw enum)
            pending.VesselSituation = vessel.isEVA
                ? $"EVA {vessel.mainBody.name}"
                : $"{HumanizeSituation(vessel.situation)} {vessel.mainBody.name}";

            // Capture end biome (Phase 10)
            pending.EndBiome = TryResolveBiome(vessel.mainBody?.name, vessel.latitude, vessel.longitude);

            ParsekLog.Info("Spawner", $"Vessel snapshot taken. Distance from launch: {pending.DistanceFromLaunch:F0}m, " +
                $"Max distance: {pending.MaxDistanceFromLaunch:F0}m, Situation: {pending.VesselSituation}, " +
                $"EndBiome: {pending.EndBiome ?? "(null)"}");
        }

        private static void ComputeMaxDistance(Recording pending, CelestialBody bodyFirst, TrajectoryPoint firstPoint)
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

        /// <summary>
        /// Resolves the biome name at the given coordinates on the given body.
        /// Returns null if the body is not found or ScienceUtil is unavailable (unit tests).
        /// </summary>
        internal static string TryResolveBiome(string bodyName, double lat, double lon)
        {
            if (string.IsNullOrEmpty(bodyName)) return null;
            try
            {
                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                if (body == null) return null;
                string biome = ScienceUtil.GetExperimentBiome(body, lat, lon);
                return string.IsNullOrEmpty(biome) ? null : biome;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose("Spawner", $"TryResolveBiome failed for {bodyName} ({lat:F4},{lon:F4}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Converts KSP's Vessel.Situations enum to a human-readable string.
        /// KSP enum names are ALL_CAPS (PRELAUNCH, SUB_ORBITAL, etc.).
        /// </summary>
        internal static string HumanizeSituation(Vessel.Situations situation)
        {
            switch (situation)
            {
                case Vessel.Situations.PRELAUNCH:  return "Prelaunch";
                case Vessel.Situations.LANDED:     return "Landed";
                case Vessel.Situations.SPLASHED:   return "Splashed";
                case Vessel.Situations.FLYING:     return "Flying";
                case Vessel.Situations.SUB_ORBITAL: return "Sub-orbital";
                case Vessel.Situations.ORBITING:   return "Orbiting";
                case Vessel.Situations.ESCAPING:   return "Escaping";
                case Vessel.Situations.DOCKED:     return "Docked";
                default:                           return situation.ToString();
            }
        }

        private static double ResolveRelocatedAltitude(
            Recording rec, CelestialBody body, double latitude, double longitude)
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

        internal static bool TryGetSnapshotDouble(ConfigNode node, string key, out double value)
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
        /// Regenerates vessel-level GUID + per-part persistentId/flightID/missionID/launchID (#234),
        /// cleans old PIDs from global registry (#237), patches robotics references (#238).
        /// </summary>
        private static void RegenerateVesselIdentity(ConfigNode spawnNode)
        {
            // Vessel-level identity
            string newPid = Guid.NewGuid().ToString("N");
            spawnNode.SetValue("pid", newPid, true);
            spawnNode.SetValue("persistentId", "0", true);

            // Clean up old part PIDs from global registry before reassigning (#237)
            List<uint> oldPartPids = CollectPartPersistentIds(spawnNode);
            for (int i = 0; i < oldPartPids.Count; i++)
                FlightGlobals.PersistentUnloadedPartIds.Remove(oldPartPids[i]);

            // Regenerate per-part identities (#234)
            var game = HighLogic.CurrentGame;
            if (game == null)
            {
                ParsekLog.Warn("Spawner",
                    $"RegenerateVesselIdentity: vessel GUID regenerated (pid={newPid}), " +
                    $"{oldPartPids.Count} old PID(s) cleaned from registry, " +
                    "but no CurrentGame — per-part regeneration skipped");
                return;
            }
            uint missionId = (uint)Guid.NewGuid().GetHashCode();
            uint launchId = game.launchID++;
            var pidMap = RegeneratePartIdentities(spawnNode,
                () => FlightGlobals.GetUniquepersistentId(),
                () => ShipConstruction.GetUniqueFlightID(game.flightState),
                missionId, launchId);

            // Patch robotics controller references with new PIDs (#238)
            int roboticsPatched = 0;
            if (pidMap.Count > 0)
                roboticsPatched = PatchRoboticsReferences(spawnNode, pidMap);

            ParsekLog.Verbose("Spawner",
                $"Regenerated vessel identity: pid={newPid}, {pidMap.Count} part(s) regenerated, " +
                $"{oldPartPids.Count} old PID(s) cleaned from registry" +
                (roboticsPatched > 0 ? $", {roboticsPatched} robotics ref(s) patched" : ""));
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

        #region Extracted helpers

        /// <summary>
        /// Pure decision: should vessel identity be regenerated?
        /// Returns true for normal spawns (new GUID), false for chain-tip spawns
        /// that must preserve the original vessel's PID for continuity.
        /// </summary>
        internal static bool ShouldRegenerateIdentity(bool preserveIdentity)
        {
            return !preserveIdentity;
        }

        /// <summary>
        /// Pure decision: determine vessel situation string from altitude, water presence,
        /// speed, and orbital speed. Used by SpawnAtPosition.
        /// 4-way classifier: returns SPLASHED | LANDED | ORBITING | FLYING (never SUB_ORBITAL —
        /// that's handled upstream by `ComputeCorrectedSituation` reading the stored snapshot sit).
        ///
        /// NOTE: there are currently three layers of situation correction applied before a
        /// spawn: (1) <see cref="ComputeCorrectedSituation"/> in `PrepareSnapshotForSpawn`
        /// rewrites the snapshot's `sit` field based on the stored situation, (2) this
        /// method ignores the corrected `snapshot.sit` and classifies fresh from
        /// altitude+velocity, (3) <see cref="OverrideSituationFromTerminalState"/> then
        /// overrides FLYING based on the recording's terminal state. The triple layering
        /// is historical — a cleanup PR could replace this method with "read corrected
        /// snapshot.sit first, fall through to altitude/velocity classifier only if still
        /// FLYING/SUB_ORBITAL" for a cleaner invariant. Tracked under the #264 follow-ups
        /// in docs/dev/todo-and-known-bugs.md.
        /// </summary>
        internal static string DetermineSituation(double alt, bool overWater, double speed, double orbitalSpeed)
        {
            if (alt <= 0 && overWater)
                return "SPLASHED";
            if (alt <= 0)
                return "LANDED";
            if (speed > orbitalSpeed * 0.9)
                return "ORBITING";
            return "FLYING";
        }

        /// <summary>
        /// Pure decision: override a FLYING situation to match the recording's terminal state.
        /// Returns the overridden situation string, or the input unchanged if no override applies.
        ///
        /// When DetermineSituation returns FLYING (alt &gt; 0, speed &lt; 0.9*orbitalSpeed), the
        /// classifier can't tell whether the vessel was flying, walking (EVA), or stationary.
        /// The recording's TerminalStateValue holds the authoritative answer. This override
        /// was originally added for Orbiting/Docked terminals (#176) to prevent on-rails
        /// pressure destruction; #264 extended it to Landed/Splashed for EVA kerbals walking
        /// at alt &gt; 0, which would otherwise hit the OrbitDriver updateMode=UPDATE stale-orbit
        /// bug on the first physics frame after load.
        ///
        /// Only fires when input is FLYING — if the classifier returned ORBITING due to high
        /// speed, we trust that signal over a potentially stale terminal state (a fast-moving
        /// vessel should not be forced into LANDED just because the recording ended with
        /// Landed; that indicates data inconsistency and the safer path is orbital spawn).
        /// </summary>
        internal static string OverrideSituationFromTerminalState(string sit, TerminalState? terminalState)
        {
            if (sit != "FLYING" || !terminalState.HasValue)
                return sit;

            switch (terminalState.Value)
            {
                case TerminalState.Orbiting:
                case TerminalState.Docked:
                    return "ORBITING";
                case TerminalState.Landed:
                    return "LANDED";
                case TerminalState.Splashed:
                    return "SPLASHED";
                default:
                    return sit;
            }
        }

        /// <summary>
        /// Apply a situation string and its corresponding landed/splashed flags to a spawn node.
        /// </summary>
        private static void ApplySituationToNode(ConfigNode spawnNode, string sit)
        {
            bool landed = sit == "LANDED";
            bool splashed = sit == "SPLASHED";
            spawnNode.SetValue("landed", landed ? "True" : "False", true);
            spawnNode.SetValue("splashed", splashed ? "True" : "False", true);
            spawnNode.SetValue("sit", sit, true);
        }

        /// <summary>
        /// Collect all non-zero part persistentId values from PART sub-nodes. (#237)
        /// Used to identify stale entries for removal from the global PID registry.
        /// </summary>
        internal static List<uint> CollectPartPersistentIds(ConfigNode vesselNode)
        {
            var ids = new List<uint>();
            if (vesselNode == null) return ids;
            foreach (ConfigNode partNode in vesselNode.GetNodes("PART"))
            {
                string pidStr = partNode.GetValue("persistentId");
                if (pidStr != null
                    && uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pid)
                    && pid != 0)
                {
                    ids.Add(pid);
                }
            }
            return ids;
        }

        /// <summary>
        /// Regenerate per-part identity fields (persistentId, flightID, missionID, launchID)
        /// on all PART sub-nodes. Returns old→new persistentId mapping for robotics patching. (#234)
        /// Delegate injection allows pure unit testing without KSP runtime.
        /// </summary>
        internal static Dictionary<uint, uint> RegeneratePartIdentities(
            ConfigNode spawnNode,
            Func<uint> generatePersistentId,
            Func<uint> generateFlightId,
            uint missionId,
            uint launchId)
        {
            var pidMap = new Dictionary<uint, uint>();
            if (spawnNode == null) return pidMap;

            string mid = missionId.ToString(CultureInfo.InvariantCulture);
            string lid = launchId.ToString(CultureInfo.InvariantCulture);

            foreach (ConfigNode partNode in spawnNode.GetNodes("PART"))
            {
                // Track old→new persistentId for robotics reference patching
                string oldPidStr = partNode.GetValue("persistentId");
                if (oldPidStr != null
                    && uint.TryParse(oldPidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint oldPid)
                    && oldPid != 0)
                {
                    uint newPid = generatePersistentId();
                    pidMap[oldPid] = newPid;
                    partNode.SetValue("persistentId", newPid.ToString(CultureInfo.InvariantCulture), true);
                }
                else
                {
                    // Part has no valid persistentId — assign a fresh one without mapping
                    uint newPid = generatePersistentId();
                    partNode.SetValue("persistentId", newPid.ToString(CultureInfo.InvariantCulture), true);
                }

                uint newUid = generateFlightId();
                partNode.SetValue("uid", newUid.ToString(CultureInfo.InvariantCulture), true);
                partNode.SetValue("mid", mid, true);
                partNode.SetValue("launchID", lid, true);
            }

            return pidMap;
        }

        /// <summary>
        /// Remap part persistentId references in ModuleRoboticController (KAL-1000) ConfigNodes
        /// after per-part identity regeneration. Returns count of PIDs remapped. (#238)
        /// </summary>
        internal static int PatchRoboticsReferences(ConfigNode spawnNode, Dictionary<uint, uint> pidMap)
        {
            if (spawnNode == null || pidMap == null || pidMap.Count == 0) return 0;

            int remapCount = 0;
            foreach (ConfigNode partNode in spawnNode.GetNodes("PART"))
            {
                foreach (ConfigNode moduleNode in partNode.GetNodes("MODULE"))
                {
                    if (moduleNode.GetValue("name") != "ModuleRoboticController")
                        continue;

                    foreach (ConfigNode axesNode in moduleNode.GetNodes("CONTROLLEDAXES"))
                    {
                        foreach (ConfigNode axisNode in axesNode.GetNodes("AXIS"))
                        {
                            RemapPidValue(axisNode, "persistentId", pidMap, ref remapCount);
                            foreach (ConfigNode symNode in axisNode.GetNodes("SYMPARTS"))
                                RemapPidValue(symNode, "symPersistentId", pidMap, ref remapCount);
                        }
                    }

                    foreach (ConfigNode actionsNode in moduleNode.GetNodes("CONTROLLEDACTIONS"))
                    {
                        foreach (ConfigNode actionNode in actionsNode.GetNodes("ACTION"))
                        {
                            RemapPidValue(actionNode, "persistentId", pidMap, ref remapCount);
                        }
                    }
                }
            }
            return remapCount;
        }

        private static void RemapPidValue(ConfigNode node, string key,
            Dictionary<uint, uint> pidMap, ref int count)
        {
            string val = node.GetValue(key);
            if (val != null
                && uint.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint oldPid)
                && pidMap.TryGetValue(oldPid, out uint newPid))
            {
                node.SetValue(key, newPid.ToString(CultureInfo.InvariantCulture), true);
                count++;
            }
        }

        /// <summary>
        /// Pure decision: should post-spawn velocity zeroing be applied? (#239)
        /// Only surface situations need stabilization — orbital/flying vessels must keep velocity.
        /// </summary>
        internal static bool ShouldZeroVelocityAfterSpawn(string situation)
        {
            if (string.IsNullOrEmpty(situation)) return false;
            return situation.Equals("LANDED", StringComparison.OrdinalIgnoreCase)
                || situation.Equals("SPLASHED", StringComparison.OrdinalIgnoreCase)
                || situation.Equals("PRELAUNCH", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Zero linear and angular velocity on a freshly spawned vessel to prevent
        /// physics jitter on surface spawns. No-op for orbital/flying situations. (#239)
        /// </summary>
        internal static void ApplyPostSpawnStabilization(Vessel vessel, string situation)
        {
            if (vessel == null || !ShouldZeroVelocityAfterSpawn(situation)) return;

            vessel.SetWorldVelocity(Vector3d.zero);
            vessel.angularVelocity = Vector3.zero;
            vessel.angularMomentum = Vector3.zero;

            ParsekLog.Verbose("Spawner",
                $"Post-spawn stabilization: pid={vessel.persistentId} sit={situation} — velocity zeroed");
        }

        #endregion
    }
}
