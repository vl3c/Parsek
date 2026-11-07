using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Parsek
{
    // Respawn of a recorded vessel from its snapshot: the respawn entry points, failed proto-vessel cleanup and vessel identity regeneration.
    public static partial class VesselSpawner
    {
        internal static void CleanupFailedSpawnedProtoVessel(
            ProtoVessel pv,
            string logTag,
            string logContext)
        {
            CleanupFailedSpawnedProtoVessel(
                pv,
                logTag,
                logContext,
                HighLogic.CurrentGame?.flightState?.protoVessels,
                proto =>
                {
                    if (proto?.vesselRef != null)
                    {
                        using (RemovedReplayFlags.BeginParsekRemoval())
                            proto.vesselRef.Die();
                    }
                });
        }

        internal static void CleanupFailedSpawnedProtoVessel(
            ProtoVessel pv,
            string logTag,
            string logContext,
            IList<ProtoVessel> protoVessels,
            Action<ProtoVessel> destroyPartialVessel)
        {
            if (pv == null)
                return;

            try
            {
                destroyPartialVessel?.Invoke(pv);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(
                    string.IsNullOrEmpty(logTag) ? "Spawner" : logTag,
                    $"{logContext}: failed to destroy partially spawned vessel during cleanup: {ex.Message}");
            }

            try
            {
                protoVessels?.Remove(pv);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(
                    string.IsNullOrEmpty(logTag) ? "Spawner" : logTag,
                    $"{logContext}: failed to remove ProtoVessel during cleanup: {ex.Message}");
            }
        }

        public static uint RespawnVessel(ConfigNode vesselNode, HashSet<string> excludeCrew = null, bool preserveIdentity = false,
            Recording nameSource = null)
        {
            ProtoVessel pv = null;
            CrewInventoryRestoreResult crewInventoryRestore = default(CrewInventoryRestoreResult);
            try
            {
                if (!VesselSnapshotOps.TryValidateSnapshotHasParts(vesselNode, out string partRejectionReason)
                    || !VesselSnapshotOps.TryValidateFiniteMaterializationMetadata(vesselNode, out partRejectionReason))
                {
                    LogMaterializationSnapshotRejected("RespawnVessel", partRejectionReason);
                    return 0;
                }

                ParsekLog.Verbose("Spawner",
                    $"RespawnVessel: excludeCrew={(excludeCrew != null ? excludeCrew.Count.ToString() : "null")}");

                // Use a copy to avoid modifying the saved snapshot
                ConfigNode spawnNode = vesselNode.CreateCopy();
                ConverterJumpPolicy.ApplyToSpawnSnapshot(spawnNode, "RespawnVessel");

                // (#608/#609/#687) Rescue Missing crew BEFORE the keep/remove
                // pass so RemoveDeadCrewFromSnapshot sees them as Available
                // and the snapshot is loadable by ProtoVessel.Load. Both
                // reserved+Missing (post-Re-Fly strip) and unreserved+Missing
                // (post-plain-Rewind strip) shapes are rescued.
                // #615 P1 review (fourth pass): collect rescued names; the
                // pid-scoped MarkRescuePlaced call happens AFTER pv.Load when
                // the new vessel's persistentId is known. Only RESERVED
                // rescues are collected — unreserved rescues have no stand-in
                // lifecycle for the marker to suppress.
                var rescuedNames = new List<string>();
                RescueReservedMissingCrewInSnapshot(spawnNode, rescuedNames);

                // Remove dead/missing crew from snapshot to avoid resurrecting them
                RemoveDeadCrewFromSnapshot(spawnNode);

                // Ensure all referenced crew exist in the roster (synthetic recordings
                // may reference kerbals that were never added to this game's roster)
                EnsureCrewExistInRoster(spawnNode);

                // Remove specific crew (e.g. EVA'd kerbals) — they spawn via child recordings
                if (excludeCrew != null && excludeCrew.Count > 0)
                    VesselSnapshotOps.RemoveSpecificCrewFromSnapshot(spawnNode, excludeCrew);

                // Set reserved crew back to Available so KSP can assign them to this vessel
                CrewReservationManager.UnreserveCrewInSnapshot(spawnNode);

                // Remove crew that already exist on a loaded vessel (prevents duplication)
                RemoveDuplicateCrewFromSnapshot(spawnNode);

                if (!preserveIdentity)
                {
                    RegenerateVesselIdentity(spawnNode, nameSource);
                }
                else
                {
                    ParsekLog.Info("Spawner", "RespawnVessel: preserving vessel identity (chain-tip spawn)");
                }
                VesselNameSpawn.ApplySpawnStamp(spawnNode, nameSource, "RespawnVessel");
                VesselSnapshotOps.EnsureSpawnReadiness(spawnNode);

                if (!VesselSnapshotOps.TryValidateSnapshotHasParts(spawnNode, out string materializationRejectionReason)
                    || !VesselSnapshotOps.TryValidateFiniteMaterializationMetadata(spawnNode, out materializationRejectionReason))
                {
                    LogMaterializationSnapshotRejected("SpawnAtPosition", materializationRejectionReason);
                    return 0;
                }

                VesselSnapshotOps.StripDeliberatePositionOverrideStamp(spawnNode, "RespawnVessel");

                // Give each kerbal still seated in the final spawn node the inventory he
                // carried when the snapshot was taken, replacing his current roster
                // inventory, before the ProtoVessel reads the roster (no duplicated or
                // lost cargo). No capture on the snapshot keeps the roster inventory.
                crewInventoryRestore = CrewInventorySnapshot.RestoreForSpawnLive(spawnNode, "RespawnVessel");

                var runningHarvesters = SpawnedHarvesterRestore.CollectActiveHarvesters(spawnNode);
                pv = new ProtoVessel(spawnNode, HighLogic.CurrentGame);
                HighLogic.CurrentGame.flightState.protoVessels.Add(pv);
                pv.Load(HighLogic.CurrentGame.flightState);

                if (pv.vesselRef == null)
                {
                    ParsekLog.Error("Spawner", "CRITICAL: ProtoVessel.Load() produced null vesselRef — vessel will not appear");
                    CleanupFailedSpawnedProtoVessel(pv, "Spawner", "RespawnVessel cleanup");
                    CrewInventorySnapshot.RollbackRestoreLive(crewInventoryRestore, "RespawnVessel null vesselRef");
                    return 0;
                }
                if (pv.vesselRef.orbitDriver == null)
                    ParsekLog.Warn("Spawner", "Spawned vessel has no orbitDriver — may not appear in map view");

                // rb.mass seeding is handled centrally by the
                // PartMassivePartCheckSeederPatch Harmony prefix on
                // Part.MassivePartCheck. The prefix lazily seeds each
                // part's rb.mass at the exact read site during the
                // ForceHeaviest autostrut selection, so the seed is
                // correct for every load path (stock save-load, background
                // activation, this RespawnVessel path once it reaches
                // physics) without depending on coroutine-timing
                // assumptions.

                // Suppress g-force destruction from spawn position correction (#235)
                pv.vesselRef.IgnoreGForces(240);

                // Zero velocity for surface spawns to prevent physics jitter (#239)
                ApplyPostSpawnStabilization(pv.vesselRef, spawnNode.GetValue("sit"));
                if (HighLogic.LoadedSceneIsFlight)
                    SpawnedHarvesterRestore.Arm(pv.vesselRef, runningHarvesters, "RespawnVessel");

                GameEvents.onNewVesselCreated.Fire(pv.vesselRef);

                // #615 P1 review (fourth pass): now that pv.vesselRef has a
                // persistentId, mark each rescued kerbal with the new vessel's
                // pid so the ApplyToRoster guard can scope the rescue marker
                // to the actual vessel the kerbal was placed onto.
                // The rescue UT lets a later flight's reservation retire the marker
                // (RESCUE-PLACED-MARKER-NEVER-RETIRES).
                ulong respawnedVesselPid = pv.vesselRef.persistentId;
                double respawnRescueUT = rescuedNames.Count > 0
                    ? Planetarium.GetUniversalTime() : double.NaN;
                for (int rn = 0; rn < rescuedNames.Count; rn++)
                {
                    CrewReservationManager.MarkRescuePlaced(
                        rescuedNames[rn], respawnedVesselPid, respawnRescueUT);
                }
                if (rescuedNames.Count > 0)
                    ParsekLog.Info("Spawner",
                        $"Rescue marker pid-scoped for {rescuedNames.Count} kerbal(s) " +
                        $"(vesselPid={respawnedVesselPid} " +
                        $"markUT={respawnRescueUT.ToString("F1", CultureInfo.InvariantCulture)}; #615 P1 fourth pass)");

                ParsekLog.Info("Spawner", $"Vessel respawned (sit={spawnNode.GetValue("sit")}, pid={pv.vesselRef.persistentId}) " +
                    VesselSnapshotOps.FormatStoredPartsForLog(spawnNode));
                return pv.vesselRef.persistentId;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Error("Spawner", $"Failed to respawn vessel: {ex.Message}");
                CleanupFailedSpawnedProtoVessel(pv, "Spawner", "RespawnVessel cleanup");
                CrewInventorySnapshot.RollbackRestoreLive(crewInventoryRestore, "RespawnVessel exception");
                return 0;
            }
        }

        internal static uint RespawnValidatedRecording(
            Recording rec,
            string logContext,
            HashSet<string> excludeCrew = null,
            bool preserveIdentity = false,
            double currentUT = double.NaN,
            bool allowExistingSourceDuplicate = false)
        {
            if (TryAdoptExistingSourceVesselForSpawn(
                rec,
                "Spawner",
                logContext,
                allowExistingSourceDuplicate))
            {
                return rec.SpawnedVesselPersistentId;
            }

            if (double.IsNaN(currentUT) || double.IsInfinity(currentUT))
            {
                try
                {
                    currentUT = Planetarium.GetUniversalTime();
                }
                catch
                {
                    currentUT = 0.0;
                }
            }

            ConfigNode snapshot = BuildValidatedRespawnSnapshot(
                rec,
                currentUT,
                logContext,
                out string materializationRejectionReason);
            if (snapshot == null)
            {
                if (!string.IsNullOrEmpty(materializationRejectionReason))
                {
                    AbandonSpawnForInvalidMaterialization(
                        rec,
                        logContext,
                        materializationRejectionReason);
                }
                else
                {
                    ParsekLog.Error("Spawner",
                        $"RespawnValidatedRecording: refusing spawn for {DescribeSpawnValidationContext(rec, logContext)}");
                }
                return 0;
            }

            return RespawnVessel(snapshot, excludeCrew, preserveIdentity, nameSource: rec);
        }

        /// <summary>
        /// Regenerate vessel identity fields to avoid PID collisions with the original vessel.
        /// After revert, the original vessel is back on the pad with the same PIDs.
        /// Spawning a copy with identical PIDs causes map view / tracking station issues.
        /// Regenerates vessel-level GUID + per-part persistentId/flightID/missionID/launchID (#234),
        /// cleans old PIDs from global registry (#237), patches robotics references (#238).
        /// The fresh guid is linked to the source recording's guid (a Spawn link, design
        /// vessel names 7.5; the snapshot's own guid when no recording is known), and the
        /// regenerated part flightIDs are carried into the node's dock records and the
        /// dock-absorb rows. Returns the old -> new part flightID map (empty when the per-part
        /// pass was skipped).
        /// </summary>
        private static Dictionary<uint, uint> RegenerateVesselIdentity(ConfigNode spawnNode, Recording source)
        {
            var uidMap = new Dictionary<uint, uint>();
            // Vessel-level identity
            string oldGuid = VesselLaunchIdentity.TryReadVesselGuid(spawnNode);
            string newPid = Guid.NewGuid().ToString("N");
            spawnNode.SetValue("pid", newPid, true);
            spawnNode.SetValue("persistentId", "0", true);
            string linkTarget = VesselNameStore.NormalizeKnownGuid(source?.RecordedVesselGuid)
                ?? VesselNameStore.NormalizeKnownGuid(oldGuid);
            VesselNameStore.AddLink(newPid, linkTarget, VesselIdentityLinkKind.Spawn,
                "spawn rec=" + (source?.RecordingId ?? "<none>"));

            // Clean up old part PIDs from global registry before reassigning (#237)
            List<uint> oldPartPids = VesselSnapshotOps.CollectPartPersistentIds(spawnNode);
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
                return uidMap;
            }
            uint missionId = (uint)Guid.NewGuid().GetHashCode();
            uint launchId = game.launchID++;
            var pidMap = VesselSnapshotOps.RegeneratePartIdentities(spawnNode,
                () => FlightGlobals.GetUniquepersistentId(),
                () => ShipConstruction.GetUniqueFlightID(game.flightState),
                missionId, launchId, uidMap);

            // Patch robotics controller references with new PIDs (#238)
            int roboticsPatched = 0;
            if (pidMap.Count > 0)
                roboticsPatched = VesselSnapshotOps.PatchRoboticsReferences(spawnNode, pidMap);

            // Dock records name parts by flightID: carry the regenerated uids into them, and
            // into the dock-absorb rows, so an undock of the spawned stack is recognized.
            int dockRefsRemapped = VesselNameSpawn.RemapDockRecordUids(spawnNode, uidMap);
            // A seated EVA kerbal is a part of the seat's vessel, named by flightID in the
            // seat's occupantID: without the remap the spawned seat loads empty.
            int seatOccupantsRemapped = VesselSnapshotOps.RemapSeatOccupantUids(spawnNode, uidMap);
            int dockRowsCopied = VesselNameStore.CopyDockAbsorbsForUidMap(uidMap,
                "spawn rec=" + (source?.RecordingId ?? "<none>"));

            ParsekLog.Verbose("Spawner",
                $"Regenerated vessel identity: pid={newPid}, {pidMap.Count} part(s) regenerated, " +
                $"{oldPartPids.Count} old PID(s) cleaned from registry" +
                (roboticsPatched > 0 ? $", {roboticsPatched} robotics ref(s) patched" : "") +
                $", dockRefsRemapped={dockRefsRemapped} seatOccupantsRemapped={seatOccupantsRemapped}" +
                $" dockAbsorbRowsCopied={dockRowsCopied}");
            return uidMap;
        }
    }
}
