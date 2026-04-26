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
        internal delegate bool ResolveBodyNameByIndexDelegate(int index, out string name);
        internal delegate bool ResolveBodyByNameDelegate(string bodyName, out CelestialBody body);
        internal delegate bool ResolveBodyIndexDelegate(CelestialBody body, out int index);

        // Proximity offset removed — spawn collision detection now uses bounding box
        // overlap check via SpawnCollisionDetector (same system as chain-tip spawns).

        /// <summary>
        /// Maximum consecutive collision-blocked frames before abandoning spawn.
        /// ~2.5 seconds at 60fps. After this many frames the spawn is abandoned
        /// to prevent infinite retry loops (bug #110).
        /// </summary>
        internal const int MaxCollisionBlocks = 150;

        internal static ResolveBodyNameByIndexDelegate BodyNameResolverForTesting;
        internal static ResolveBodyByNameDelegate BodyResolverForTesting;
        internal static ResolveBodyIndexDelegate BodyIndexResolverForTesting;
        private static Func<uint, bool> materializedSourceVesselExistsOverrideForTesting;

        internal static void SetMaterializedSourceVesselExistsOverrideForTesting(Func<uint, bool> checker)
        {
            materializedSourceVesselExistsOverrideForTesting = checker;
        }

        internal static void ResetMaterializedSourceVesselExistsOverrideForTesting()
        {
            materializedSourceVesselExistsOverrideForTesting = null;
        }

        internal static bool TryAdoptExistingSourceVesselForSpawn(
            Recording rec,
            string logTag,
            string logContext,
            bool allowExistingSourceDuplicate = false)
        {
            uint sourcePid = rec != null ? rec.VesselPersistentId : 0u;
            bool sourceVesselExists = MaterializedSourceVesselExists(sourcePid);
            return TryAdoptExistingSourceVesselForSpawn(
                rec,
                sourceVesselExists,
                logTag,
                logContext,
                allowExistingSourceDuplicate);
        }

        internal static bool TryAdoptExistingSourceVesselForSpawn(
            Recording rec,
            bool sourceVesselExists,
            string logTag,
            string logContext,
            bool allowExistingSourceDuplicate = false)
        {
            if (allowExistingSourceDuplicate)
                return false;
            if (rec == null || !sourceVesselExists)
                return false;
            if (rec.VesselPersistentId == 0)
                return false;
            if (rec.VesselSpawned || rec.SpawnedVesselPersistentId != 0)
                return false;

            rec.VesselSpawned = true;
            rec.SpawnedVesselPersistentId = rec.VesselPersistentId;

            string tag = string.IsNullOrEmpty(logTag) ? "Spawner" : logTag;
            string context = string.IsNullOrEmpty(logContext)
                ? rec.VesselName ?? rec.RecordingId ?? "(unknown)"
                : logContext;
            ParsekLog.Info(tag,
                $"{context}: source vessel pid={rec.VesselPersistentId} already exists - " +
                "adopting instead of spawning duplicate");
            return true;
        }

        internal static bool ShouldAllowExistingSourceDuplicateForReplay(
            uint sourcePid,
            uint sceneEntryActiveVesselPid,
            uint activeVesselPid)
        {
            return ShouldAllowExistingSourceDuplicateForReplay(
                sourcePid,
                sceneEntryActiveVesselPid,
                activeVesselPid,
                replayTargetSourcePid: 0);
        }

        internal static bool ShouldAllowExistingSourceDuplicateForReplay(
            uint sourcePid,
            uint sceneEntryActiveVesselPid,
            uint activeVesselPid,
            uint replayTargetSourcePid)
        {
            if (sourcePid == 0)
                return false;

            if (replayTargetSourcePid != 0 && sourcePid != replayTargetSourcePid)
            {
                ParsekLog.Verbose("Spawner",
                    $"ShouldAllowExistingSourceDuplicate=false sourcePid={sourcePid.ToString(CultureInfo.InvariantCulture)} " +
                    $"outside rewind replay target sourcePid={replayTargetSourcePid.ToString(CultureInfo.InvariantCulture)} " +
                    $"(sceneEntryActiveVesselPid={sceneEntryActiveVesselPid.ToString(CultureInfo.InvariantCulture)}, " +
                    $"activeVesselPid={activeVesselPid.ToString(CultureInfo.InvariantCulture)})");
                return false;
            }

            // #226 replay/revert duplicate-spawn exception: when the recorded
            // source vessel matches the scene-entry active vessel or the
            // current active vessel, the source is the vehicle the player is
            // actively flying and the spawn is the replay ghost. Log which
            // PID match triggered the bypass so #226 replay diagnostics are
            // legible in KSP.log (follow-up to PR #505 review).
            if (sourcePid == sceneEntryActiveVesselPid)
            {
                ParsekLog.Verbose("Spawner",
                    $"ShouldAllowExistingSourceDuplicate=true sourcePid={sourcePid.ToString(CultureInfo.InvariantCulture)} " +
                    $"matched sceneEntryActiveVesselPid={sceneEntryActiveVesselPid.ToString(CultureInfo.InvariantCulture)} " +
                    $"(activeVesselPid={activeVesselPid.ToString(CultureInfo.InvariantCulture)}) " +
                    "- #226 replay/revert bypass");
                return true;
            }
            if (sourcePid == activeVesselPid)
            {
                ParsekLog.Verbose("Spawner",
                    $"ShouldAllowExistingSourceDuplicate=true sourcePid={sourcePid.ToString(CultureInfo.InvariantCulture)} " +
                    $"matched activeVesselPid={activeVesselPid.ToString(CultureInfo.InvariantCulture)} " +
                    $"(sceneEntryActiveVesselPid={sceneEntryActiveVesselPid.ToString(CultureInfo.InvariantCulture)}) " +
                    "- #226 replay/revert bypass");
                return true;
            }
            return false;
        }

        internal static bool ShouldAllowExistingSourceDuplicateForCurrentFlight(uint sourcePid)
        {
            uint activeVesselPid = 0;
            try
            {
                activeVesselPid = FlightGlobals.ActiveVessel != null
                    ? FlightGlobals.ActiveVessel.persistentId
                    : 0u;
            }
            catch (Exception ex)
            {
                if (!IsHeadlessKspAccessFailure(ex))
                    throw;
            }

            bool allow = ShouldAllowExistingSourceDuplicateForReplay(
                sourcePid,
                RecordingStore.SceneEntryActiveVesselPid,
                activeVesselPid,
                RecordingStore.RewindReplayTargetSourcePid);
            if (allow)
            {
                ParsekLog.Verbose("Spawner",
                    $"ShouldAllowExistingSourceDuplicateForCurrentFlight=true sourcePid={sourcePid.ToString(CultureInfo.InvariantCulture)} " +
                    $"sceneEntryActiveVesselPid={RecordingStore.SceneEntryActiveVesselPid.ToString(CultureInfo.InvariantCulture)} " +
                    $"activeVesselPid={activeVesselPid.ToString(CultureInfo.InvariantCulture)}");
            }
            return allow;
        }

        internal static bool MaterializedSourceVesselExists(uint sourcePid)
        {
            if (sourcePid == 0 || GhostMapPresence.IsGhostMapVessel(sourcePid))
                return false;

            if (materializedSourceVesselExistsOverrideForTesting != null)
                return materializedSourceVesselExistsOverrideForTesting(sourcePid);

            if (LoadedRealVesselExists(sourcePid))
                return true;

            return ProtoVesselExists(sourcePid);
        }

        private static bool LoadedRealVesselExists(uint sourcePid)
        {
            try
            {
                var vessels = FlightGlobals.Vessels;
                if (vessels == null)
                    return false;

                for (int i = 0; i < vessels.Count; i++)
                {
                    Vessel vessel = vessels[i];
                    if (vessel != null
                        && vessel.persistentId == sourcePid
                        && !GhostMapPresence.IsGhostMapVessel(vessel.persistentId))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!IsHeadlessKspAccessFailure(ex))
                    throw;

                ParsekLog.Verbose("Spawner",
                    $"LoadedRealVesselExists: FlightGlobals unavailable for pid={sourcePid}");
            }

            return false;
        }

        private static bool ProtoVesselExists(uint sourcePid)
        {
            try
            {
                var protoVessels = HighLogic.CurrentGame?.flightState?.protoVessels;
                if (protoVessels == null)
                    return false;

                for (int i = 0; i < protoVessels.Count; i++)
                {
                    ProtoVessel pv = protoVessels[i];
                    if (pv != null
                        && pv.persistentId == sourcePid
                        && !GhostMapPresence.IsGhostMapVessel(pv.persistentId))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!IsHeadlessKspAccessFailure(ex))
                    throw;

                ParsekLog.Verbose("Spawner",
                    $"ProtoVesselExists: flightState unavailable for pid={sourcePid}");
            }

            return false;
        }

        private static bool IsHeadlessKspAccessFailure(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                if (current is System.Security.SecurityException || current is MethodAccessException)
                    return true;
            }

            return false;
        }

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
                NormalizeBackedUpSnapshotFromLiveVessel(node, vessel);
                return node;
            }
            catch (Exception ex)
            {
                ParsekLog.Error("Spawner", $"Failed to backup vessel snapshot: {ex.Message}");
                return null;
            }
        }

        private static void NormalizeBackedUpSnapshotFromLiveVessel(ConfigNode snapshot, Vessel vessel)
        {
            if (snapshot == null || vessel == null)
                return;

            TerminalState liveState = ResolveLiveSnapshotTerminalState(vessel);
            CelestialBody surfaceBody = vessel.situation == Vessel.Situations.LANDED
                || vessel.situation == Vessel.Situations.SPLASHED
                ? vessel.mainBody
                : null;
            NormalizeStableSnapshotForPersistence(
                snapshot,
                liveState,
                surfaceBody,
                $"TryBackupSnapshot pid={vessel.persistentId} vessel='{vessel.vesselName ?? "(unknown)"}'");
        }

        private static TerminalState ResolveLiveSnapshotTerminalState(Vessel vessel)
        {
            if (vessel == null)
                return TerminalState.SubOrbital;

            return RecordingTree.DetermineTerminalState((int)vessel.situation, vessel);
        }

        /// <summary>
        /// Walk PART > RESOURCE nodes in a vessel snapshot ConfigNode and return
        /// a dictionary of resource names to summed amount/maxAmount.
        /// Excludes ElectricCharge and IntakeAir (noise, not meaningful cargo).
        /// Returns null if input is null, has no parts, or no resources found.
        /// </summary>
        internal static Dictionary<string, ResourceAmount> ExtractResourceManifest(ConfigNode vesselSnapshot)
        {
            if (vesselSnapshot == null) return null;

            var parts = vesselSnapshot.GetNodes("PART");
            if (parts.Length == 0) return null;

            var manifest = new Dictionary<string, ResourceAmount>();
            int partCount = parts.Length;

            for (int i = 0; i < parts.Length; i++)
            {
                var resources = parts[i].GetNodes("RESOURCE");
                for (int j = 0; j < resources.Length; j++)
                {
                    string name = resources[j].GetValue("name");
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name == "ElectricCharge" || name == "IntakeAir") continue;

                    double amount = 0;
                    double maxAmount = 0;
                    string amountStr = resources[j].GetValue("amount");
                    string maxStr = resources[j].GetValue("maxAmount");
                    if (amountStr != null)
                        double.TryParse(amountStr, System.Globalization.NumberStyles.Float,
                            CultureInfo.InvariantCulture, out amount);
                    if (maxStr != null)
                        double.TryParse(maxStr, System.Globalization.NumberStyles.Float,
                            CultureInfo.InvariantCulture, out maxAmount);

                    if (manifest.ContainsKey(name))
                    {
                        // Struct — indexer returns a copy. Read-modify-write.
                        var ra = manifest[name];
                        ra.amount += amount;
                        ra.maxAmount += maxAmount;
                        manifest[name] = ra;
                    }
                    else
                    {
                        manifest[name] = new ResourceAmount { amount = amount, maxAmount = maxAmount };
                    }
                }
            }

            if (manifest.Count == 0) return null;

            ParsekLog.Verbose("Spawner",
                $"ExtractResourceManifest: {manifest.Count} resource type(s) from {partCount} part(s)");

            return manifest;
        }

        /// <summary>
        /// Walk PART > MODULE nodes in a vessel snapshot ConfigNode and return
        /// a dictionary of stored inventory item names to summed count/slotsTaken.
        /// Also outputs total inventory slot capacity across all ModuleInventoryPart modules.
        /// Returns null if input is null, has no parts, or no inventory items found.
        /// </summary>
        internal static Dictionary<string, InventoryItem> ExtractInventoryManifest(
            ConfigNode vesselSnapshot, out int totalInventorySlots)
        {
            totalInventorySlots = 0;

            if (vesselSnapshot == null) return null;

            var parts = vesselSnapshot.GetNodes("PART");
            if (parts.Length == 0) return null;

            var manifest = new Dictionary<string, InventoryItem>();
            int moduleCount = 0;

            for (int i = 0; i < parts.Length; i++)
            {
                var modules = parts[i].GetNodes("MODULE");
                for (int j = 0; j < modules.Length; j++)
                {
                    if (modules[j].GetValue("name") != "ModuleInventoryPart") continue;
                    moduleCount++;

                    string slotsStr = modules[j].GetValue("InventorySlots");
                    if (slotsStr != null)
                    {
                        if (int.TryParse(slotsStr, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int slots))
                            totalInventorySlots += slots;
                    }

                    var storedPartsNode = modules[j].GetNode("STOREDPARTS");
                    if (storedPartsNode == null) continue;

                    var storedParts = storedPartsNode.GetNodes("STOREDPART");
                    for (int k = 0; k < storedParts.Length; k++)
                    {
                        string partName = storedParts[k].GetValue("partName");
                        if (string.IsNullOrEmpty(partName)) continue;

                        int quantity = 1;
                        string qtyStr = storedParts[k].GetValue("quantity");
                        if (qtyStr != null)
                        {
                            if (int.TryParse(qtyStr, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out int parsedQty))
                                quantity = parsedQty;
                        }

                        if (manifest.ContainsKey(partName))
                        {
                            // Struct — indexer returns a copy. Read-modify-write.
                            var item = manifest[partName];
                            item.count += quantity;
                            item.slotsTaken += 1;
                            manifest[partName] = item;
                        }
                        else
                        {
                            manifest[partName] = new InventoryItem { count = quantity, slotsTaken = 1 };
                        }
                    }
                }
            }

            if (manifest.Count == 0)
            {
                totalInventorySlots = 0;
                return null;
            }

            ParsekLog.Verbose("Spawner",
                $"ExtractInventoryManifest: {manifest.Count} item type(s), {totalInventorySlots} total slot(s) across {moduleCount} inventory module(s)");

            return manifest;
        }

        /// <summary>
        /// Walk PART nodes in a vessel snapshot ConfigNode and return
        /// a dictionary of crew traits (Pilot/Scientist/Engineer/Tourist) to counts.
        /// Uses traitResolver if provided; otherwise falls back to KerbalsModule.FindTraitForKerbal.
        /// Returns null if input is null, has no parts, or no crew found.
        /// </summary>
        internal static Dictionary<string, int> ExtractCrewManifest(
            ConfigNode vesselSnapshot, Func<string, string> traitResolver = null)
        {
            if (vesselSnapshot == null) return null;

            var parts = vesselSnapshot.GetNodes("PART");
            if (parts.Length == 0) return null;

            var manifest = new Dictionary<string, int>();
            int crewCount = 0;

            for (int i = 0; i < parts.Length; i++)
            {
                var crewValues = parts[i].GetValues("crew");
                for (int j = 0; j < crewValues.Length; j++)
                {
                    string name = crewValues[j];
                    if (string.IsNullOrEmpty(name)) continue;

                    string trait = traitResolver != null
                        ? traitResolver(name)
                        : KerbalsModule.FindTraitForKerbal(name);

                    if (manifest.ContainsKey(trait))
                        manifest[trait] = manifest[trait] + 1;
                    else
                        manifest[trait] = 1;

                    crewCount++;
                }
            }

            if (manifest.Count == 0) return null;

            ParsekLog.Verbose("Spawner",
                $"ExtractCrewManifest: {crewCount} crew across {manifest.Count} trait(s) from {parts.Length} part(s)");

            return manifest;
        }

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
                        proto.vesselRef.Die();
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

        public static uint RespawnVessel(ConfigNode vesselNode, HashSet<string> excludeCrew = null, bool preserveIdentity = false)
        {
            ProtoVessel pv = null;
            try
            {
                ParsekLog.Verbose("Spawner",
                    $"RespawnVessel: excludeCrew={(excludeCrew != null ? excludeCrew.Count.ToString() : "null")}");

                // Use a copy to avoid modifying the saved snapshot
                ConfigNode spawnNode = vesselNode.CreateCopy();

                // (#608/#609) Rescue reserved+Missing crew BEFORE the keep/remove
                // pass so RemoveDeadCrewFromSnapshot sees them as Available
                // and the snapshot is loadable by ProtoVessel.Load.
                // #615 P1 review (fourth pass): collect rescued names; the
                // pid-scoped MarkRescuePlaced call happens AFTER pv.Load
                // when the new vessel's persistentId is known.
                var rescuedNames = new List<string>();
                RescueReservedMissingCrewInSnapshot(spawnNode, rescuedNames);

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

                pv = new ProtoVessel(spawnNode, HighLogic.CurrentGame);
                HighLogic.CurrentGame.flightState.protoVessels.Add(pv);
                pv.Load(HighLogic.CurrentGame.flightState);

                if (pv.vesselRef == null)
                {
                    ParsekLog.Error("Spawner", "CRITICAL: ProtoVessel.Load() produced null vesselRef — vessel will not appear");
                    CleanupFailedSpawnedProtoVessel(pv, "Spawner", "RespawnVessel cleanup");
                    return 0;
                }
                if (pv.vesselRef.orbitDriver == null)
                    ParsekLog.Warn("Spawner", "Spawned vessel has no orbitDriver — may not appear in map view");

                // Suppress g-force destruction from spawn position correction (#235)
                pv.vesselRef.IgnoreGForces(240);

                // Zero velocity for surface spawns to prevent physics jitter (#239)
                ApplyPostSpawnStabilization(pv.vesselRef, spawnNode.GetValue("sit"));

                GameEvents.onNewVesselCreated.Fire(pv.vesselRef);

                // #615 P1 review (fourth pass): now that pv.vesselRef has a
                // persistentId, mark each rescued kerbal with the new vessel's
                // pid so the ApplyToRoster guard can scope the rescue marker
                // to the actual vessel the kerbal was placed onto.
                ulong respawnedVesselPid = pv.vesselRef.persistentId;
                for (int rn = 0; rn < rescuedNames.Count; rn++)
                {
                    CrewReservationManager.MarkRescuePlaced(
                        rescuedNames[rn], respawnedVesselPid);
                }
                if (rescuedNames.Count > 0)
                    ParsekLog.Info("Spawner",
                        $"Rescue marker pid-scoped for {rescuedNames.Count} kerbal(s) " +
                        $"(vesselPid={respawnedVesselPid}; #615 P1 fourth pass)");

                ParsekLog.Info("Spawner", $"Vessel respawned (sit={spawnNode.GetValue("sit")}, pid={pv.vesselRef.persistentId})");
                return pv.vesselRef.persistentId;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Error("Spawner", $"Failed to respawn vessel: {ex.Message}");
                CleanupFailedSpawnedProtoVessel(pv, "Spawner", "RespawnVessel cleanup");
                return 0;
            }
        }

        /// <summary>
        /// Real-vessel spawn consumes format-v0 surface-relative recording data. `VESSEL.rot`
        /// is ProtoVessel.rotation and KSP loads it into vesselRef.srfRelRotation, so keep
        /// it in the recorded surface-relative frame. Do not compose bodyTransform here;
        /// that conversion is only for assigning live Transform.rotation.
        /// </summary>
        internal static Quaternion ComputeProtoVesselSpawnRotationFromSurfaceRelative(
            Quaternion surfaceRelativeRotation)
        {
            return TrajectoryMath.SanitizeQuaternion(surfaceRelativeRotation);
        }

        internal static Quaternion ApplyProtoVesselSpawnRotationToNode(
            ConfigNode vesselNode,
            string bodyName,
            Quaternion? bodyRotation,
            Quaternion surfaceRelativeRotation,
            string context)
        {
            Quaternion protoVesselRotation =
                ComputeProtoVesselSpawnRotationFromSurfaceRelative(surfaceRelativeRotation);
            vesselNode?.SetValue("rot", KSPUtil.WriteQuaternion(protoVesselRotation), true);

            ParsekLog.Verbose("Spawner",
                $"Spawn rotation prep ({context}): body={bodyName ?? "?"} " +
                "frame=surface-relative(format-v0) -> ProtoVessel.rot " +
                "(KSP loads as vessel.srfRelRotation; no body multiply) " +
                $"srfRel={KSPUtil.WriteQuaternion(surfaceRelativeRotation)} " +
                $"bodyRot={(bodyRotation.HasValue ? KSPUtil.WriteQuaternion(bodyRotation.Value) : "(unavailable)")} " +
                $"rot={KSPUtil.WriteQuaternion(protoVesselRotation)}");

            return protoVesselRotation;
        }

        internal static bool TryApplySpawnRotationFromSurfaceRelative(
            ConfigNode vesselNode,
            string bodyName,
            Quaternion? bodyRotation,
            Quaternion? surfaceRelativeRotation,
            string context)
        {
            if (vesselNode == null || !surfaceRelativeRotation.HasValue)
                return false;

            ApplyProtoVesselSpawnRotationToNode(
                vesselNode,
                bodyName,
                bodyRotation,
                surfaceRelativeRotation.Value,
                context);
            return true;
        }

        internal static bool TryApplySpawnRotationFromSurfaceRelative(
            ConfigNode vesselNode,
            CelestialBody body,
            Quaternion? surfaceRelativeRotation,
            string context)
        {
            Quaternion? bodyRotation = body != null && body.bodyTransform != null
                ? (Quaternion?)body.bodyTransform.rotation
                : null;
            return TryApplySpawnRotationFromSurfaceRelative(
                vesselNode,
                body?.name,
                bodyRotation,
                surfaceRelativeRotation,
                context);
        }

        internal static bool IsSurfaceTerminal(TerminalState? terminalState)
        {
            return terminalState == TerminalState.Landed
                || terminalState == TerminalState.Splashed;
        }

        internal static bool TryGetPreferredSpawnRotationFrame(
            Recording rec,
            TrajectoryPoint? fallbackPoint,
            out string bodyName,
            out Quaternion surfaceRelativeRotation,
            out string source)
        {
            bodyName = null;
            surfaceRelativeRotation = Quaternion.identity;
            source = null;

            if (rec == null || !IsSurfaceTerminal(rec.TerminalStateValue))
                return false;

            if (rec.TerminalPosition.HasValue)
            {
                SurfacePosition terminalPose = rec.TerminalPosition.Value;
                if (terminalPose.HasRecordedRotation)
                {
                    bodyName = terminalPose.body;
                    if (string.IsNullOrEmpty(bodyName))
                        RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out bodyName);
                    if (!string.IsNullOrEmpty(bodyName))
                    {
                        surfaceRelativeRotation = TrajectoryMath.SanitizeQuaternion(terminalPose.rotation);
                        source = "terminal surface pose";
                        return true;
                    }
                }
            }

            if (fallbackPoint.HasValue)
            {
                bodyName = fallbackPoint.Value.bodyName;
                if (string.IsNullOrEmpty(bodyName))
                    RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out bodyName);
                if (!string.IsNullOrEmpty(bodyName))
                {
                    surfaceRelativeRotation = TrajectoryMath.SanitizeQuaternion(fallbackPoint.Value.rotation);
                    source = "last trajectory point";
                    return true;
                }
            }

            return false;
        }

        internal static bool TryResolvePreferredSpawnRotation(
            Recording rec,
            TrajectoryPoint? fallbackPoint,
            out string bodyName,
            out Quaternion? bodyRotation,
            out Quaternion surfaceRelativeRotation,
            out string source)
        {
            bodyName = null;
            bodyRotation = null;
            surfaceRelativeRotation = Quaternion.identity;
            source = null;

            if (!TryGetPreferredSpawnRotationFrame(
                rec, fallbackPoint, out bodyName, out surfaceRelativeRotation, out source))
            {
                return false;
            }

            if (TryResolveBodyByName(bodyName, out CelestialBody body)
                && body != null
                && body.bodyTransform != null)
            {
                bodyRotation = body.bodyTransform.rotation;
            }

            return true;
        }

        internal static bool TryApplyPreferredSpawnRotation(
            ConfigNode vesselNode,
            Recording rec,
            TrajectoryPoint? fallbackPoint,
            string context)
        {
            if (!TryResolvePreferredSpawnRotation(
                rec, fallbackPoint,
                out string bodyName,
                out Quaternion? bodyRotation,
                out Quaternion surfaceRelativeRotation,
                out string source))
            {
                return false;
            }

            return TryApplySpawnRotationFromSurfaceRelative(
                vesselNode,
                bodyName,
                bodyRotation,
                surfaceRelativeRotation,
                $"{context}, source={source}");
        }

        internal static void ApplyResolvedSpawnStateToSnapshot(
            ConfigNode spawnSnapshot,
            Recording rec,
            TrajectoryPoint? rotationPoint,
            double spawnLat,
            double spawnLon,
            double spawnAlt,
            int index,
            string logContext,
            bool allowPreferredRotation = true,
            bool stripEvaLadder = true)
        {
            if (spawnSnapshot == null)
                return;

            string snapshotLabel = !string.IsNullOrEmpty(logContext)
                ? logContext
                : rec?.VesselName ?? "(unknown)";
            string vesselName = rec?.VesselName ?? snapshotLabel;
            if (allowPreferredRotation
                && TryResolvePreferredSpawnRotation(
                    rec,
                    rotationPoint,
                    out string rotationBodyName,
                    out Quaternion? rotationBodyRotation,
                    out Quaternion surfaceRelativeRotation,
                    out string rotationSource))
            {
                OverrideSnapshotPosition(
                    spawnSnapshot,
                    spawnLat,
                    spawnLon,
                    spawnAlt,
                    index,
                    snapshotLabel,
                    rotationBodyName,
                    rotationBodyRotation,
                    surfaceRelativeRotation,
                    rotationSource);
            }
            else
            {
                OverrideSnapshotPosition(
                    spawnSnapshot,
                    spawnLat,
                    spawnLon,
                    spawnAlt,
                    index,
                    snapshotLabel);
            }

            if (stripEvaLadder && !string.IsNullOrEmpty(rec?.EvaCrewName))
                StripEvaLadderState(spawnSnapshot, index, vesselName);
        }

        internal static CelestialBody ResolveSpawnRotationBody(
            Recording rec,
            TrajectoryPoint? point = null)
        {
            string bodyName = null;
            if (point.HasValue && !string.IsNullOrEmpty(point.Value.bodyName))
                bodyName = point.Value.bodyName;
            if (string.IsNullOrEmpty(bodyName))
                RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out bodyName);

            return string.IsNullOrEmpty(bodyName)
                ? null
                : FlightGlobals.GetBodyByName(bodyName);
        }

        internal static string PrepareSpawnNodeAtPosition(
            ConfigNode spawnNode,
            string bodyName,
            Quaternion? bodyRotation,
            double lat, double lon, double alt,
            double speed,
            double orbitalSpeed,
            bool overWater,
            TerminalState? terminalState,
            Quaternion? surfaceRelativeRotation)
        {
            if (spawnNode == null)
                return null;

            spawnNode.SetValue("lat", lat.ToString("R"), true);
            spawnNode.SetValue("lon", lon.ToString("R"), true);
            spawnNode.SetValue("alt", alt.ToString("R"), true);

            string sit = DetermineSituation(alt, overWater, speed, orbitalSpeed);

            // Override FLYING situation from terminal state (#176 for Orbiting/Docked, #264
            // for Landed/Splashed). Without this, an EVA kerbal walking at alt > 0 with
            // low speed classifies as FLYING and hits the OrbitDriver updateMode=UPDATE
            // stale-orbit bug on the first physics frame after load.
            string overriddenSit = OverrideSituationFromTerminalState(sit, terminalState);
            if (overriddenSit != sit)
            {
                ParsekLog.Info("Spawner",
                    $"SpawnAtPosition: overriding sit {sit} → {overriddenSit} " +
                    $"(terminal={terminalState}, speed={speed.ToString("F1", CultureInfo.InvariantCulture)}, " +
                    $"orbitalSpeed={orbitalSpeed.ToString("F1", CultureInfo.InvariantCulture)}) — prevents stale-orbit / pressure destruction (#176, #264)");
                sit = overriddenSit;
            }

            ApplySituationToNode(spawnNode, sit);
            ParsekLog.Verbose("Spawner",
                $"SpawnAtPosition: determined sit={sit} (alt={alt.ToString("F0", CultureInfo.InvariantCulture)}, speed={speed.ToString("F1", CultureInfo.InvariantCulture)}, " +
                $"orbitalSpeed={orbitalSpeed.ToString("F1", CultureInfo.InvariantCulture)}, overWater={overWater})");

            // Optional rotation override: callers hand ProtoVessel.rot a surface-relative
            // rotation. Absolute/surface trajectory points already store that frame; v6
            // RELATIVE section rotations are anchor-local and must be converted before
            // reaching this spawn path.
            if (surfaceRelativeRotation.HasValue)
            {
                TryApplySpawnRotationFromSurfaceRelative(
                    spawnNode,
                    bodyName,
                    bodyRotation,
                    surfaceRelativeRotation,
                    "SpawnAtPosition");
            }

            return sit;
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
            Quaternion? surfaceRelativeRotation = null,
            Orbit orbitOverride = null)
        {
            ProtoVessel pv = null;
            try
            {
                ConfigNode spawnNode = vesselNode.CreateCopy();

                // Determine situation from altitude and velocity
                double orbitalSpeed = Math.Sqrt(body.gravParameter / (body.Radius + alt));
                bool overWater = body.ocean && body.TerrainAltitude(lat, lon) < 0;
                string sit = PrepareSpawnNodeAtPosition(
                    spawnNode,
                    body?.name,
                    body != null && body.bodyTransform != null
                        ? (Quaternion?)body.bodyTransform.rotation
                        : null,
                    lat,
                    lon,
                    alt,
                    velocity.magnitude,
                    orbitalSpeed,
                    overWater,
                    terminalState,
                    surfaceRelativeRotation);

                // Rebuild ORBIT subnode from position + velocity
                Orbit orbit = orbitOverride;
                if (orbit == null)
                {
                    orbit = new Orbit();
                    Vector3d worldPos = body.GetWorldSurfacePosition(lat, lon, alt);
                    orbit.UpdateFromStateVectors(worldPos, velocity, body, ut);
                }
                spawnNode.RemoveNode("ORBIT");
                ConfigNode orbitNode = new ConfigNode("ORBIT");
                SaveOrbitToNode(orbit, orbitNode, body);
                spawnNode.AddNode(orbitNode);
                if (sit == "ORBITING")
                    NormalizeOrbitalSpawnMetadata(spawnNode, ut);

                // Crew handling
                // (#608/#609) Rescue reserved+Missing crew BEFORE the keep/remove
                // pass so RemoveDeadCrewFromSnapshot sees them as Available
                // and the snapshot is loadable by ProtoVessel.Load.
                // #615 P1 review (fourth pass): collect rescued names; the
                // pid-scoped MarkRescuePlaced call happens AFTER pv.Load
                // when the new vessel's persistentId is known.
                var rescuedNames = new List<string>();
                RescueReservedMissingCrewInSnapshot(spawnNode, rescuedNames);
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

                pv = new ProtoVessel(spawnNode, HighLogic.CurrentGame);
                HighLogic.CurrentGame.flightState.protoVessels.Add(pv);
                pv.Load(HighLogic.CurrentGame.flightState);

                if (pv.vesselRef == null)
                {
                    ParsekLog.Error("Spawner", "CRITICAL: SpawnAtPosition — ProtoVessel.Load() produced null vesselRef");
                    CleanupFailedSpawnedProtoVessel(pv, "Spawner", "SpawnAtPosition cleanup");
                    return 0;
                }
                if (pv.vesselRef.orbitDriver == null)
                    ParsekLog.Warn("Spawner", "SpawnAtPosition vessel has no orbitDriver — may not appear in map view");

                // Suppress g-force destruction from spawn position correction (#235)
                pv.vesselRef.IgnoreGForces(240);

                // Zero velocity for surface spawns to prevent physics jitter (#239)
                ApplyPostSpawnStabilization(pv.vesselRef, sit);

                GameEvents.onNewVesselCreated.Fire(pv.vesselRef);

                // #615 P1 review (fourth pass): now that pv.vesselRef has a
                // persistentId, mark each rescued kerbal with the new vessel's
                // pid so the ApplyToRoster guard can scope the rescue marker
                // to the actual vessel the kerbal was placed onto.
                ulong spawnedAtPositionVesselPid = pv.vesselRef.persistentId;
                for (int rn = 0; rn < rescuedNames.Count; rn++)
                {
                    CrewReservationManager.MarkRescuePlaced(
                        rescuedNames[rn], spawnedAtPositionVesselPid);
                }
                if (rescuedNames.Count > 0)
                    ParsekLog.Info("Spawner",
                        $"Rescue marker pid-scoped for {rescuedNames.Count} kerbal(s) " +
                        $"(vesselPid={spawnedAtPositionVesselPid}; #615 P1 fourth pass)");

                ParsekLog.Info("Spawner", $"SpawnAtPosition: vessel spawned (sit={sit}, pid={pv.vesselRef.persistentId}, " +
                    $"body={body.name}, alt={alt:F0}m)");
                return pv.vesselRef.persistentId;
            }
            catch (Exception ex)
            {
                ParsekLog.Error("Spawner", $"SpawnAtPosition failed: {ex.Message}");
                CleanupFailedSpawnedProtoVessel(pv, "Spawner", "SpawnAtPosition cleanup");
                return 0;
            }
        }

        /// <summary>
        /// T57: Resolves the parent recording's VesselPersistentId for EVA collision
        /// exemption. Searches committed trees for the parent recording by ParentRecordingId.
        /// Returns 0 if no parent found. Pure lookup, no side effects.
        /// </summary>
        internal static uint ResolveParentVesselPid(Recording rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.ParentRecordingId)
                || string.IsNullOrEmpty(rec.TreeId))
                return 0;

            var trees = RecordingStore.CommittedTrees;
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i].Id != rec.TreeId) continue;
                Recording parentRec;
                if (trees[i].Recordings.TryGetValue(rec.ParentRecordingId, out parentRec))
                {
                    if (parentRec.VesselPersistentId != 0)
                    {
                        ParsekLog.Verbose("Spawner",
                            $"ResolveParentVesselPid: EVA '{rec.VesselName}' parent='{parentRec.VesselName}' " +
                            $"pid={parentRec.VesselPersistentId} (T57 exemption)");
                        return parentRec.VesselPersistentId;
                    }
                }
                break;
            }

            // Also check the pending tree (active recording in flight)
            var pendingTree = RecordingStore.PendingTree;
            if (pendingTree != null && pendingTree.Id == rec.TreeId)
            {
                Recording parentRec;
                if (pendingTree.Recordings.TryGetValue(rec.ParentRecordingId, out parentRec)
                    && parentRec.VesselPersistentId != 0)
                {
                    ParsekLog.Verbose("Spawner",
                        $"ResolveParentVesselPid: EVA '{rec.VesselName}' parent='{parentRec.VesselName}' " +
                        $"pid={parentRec.VesselPersistentId} (from pending tree, T57)");
                    return parentRec.VesselPersistentId;
                }
            }

            ParsekLog.Verbose("Spawner",
                $"ResolveParentVesselPid: no parent found for EVA '{rec.VesselName}' " +
                $"(parentId={rec.ParentRecordingId}, treeId={rec.TreeId})");
            return 0;
        }

        public static void SpawnOrRecoverIfTooClose(Recording rec, int index)
        {
            SpawnOrRecoverIfTooClose(rec, index, preserveIdentity: false);
        }

        public static void SpawnOrRecoverIfTooClose(
            Recording rec,
            int index,
            bool preserveIdentity,
            bool allowExistingSourceDuplicate = false)
        {
            const int maxSpawnAttempts = 3;
            string logContext = $"recording #{index} ({rec?.VesselName ?? "(unknown)"})";
            if (rec == null)
            {
                ParsekLog.Warn("Spawner",
                    $"Spawn skipped for {logContext}: recording is null");
                return;
            }

            if (TryAdoptExistingSourceVesselForSpawn(
                rec,
                "Spawner",
                logContext,
                allowExistingSourceDuplicate))
            {
                return;
            }

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
                rec.SpawnedVesselPersistentId = RespawnValidatedRecording(
                    rec,
                    logContext,
                    excludeCrew,
                    preserveIdentity: preserveIdentity,
                    allowExistingSourceDuplicate: allowExistingSourceDuplicate);
                rec.VesselSpawned = rec.SpawnedVesselPersistentId != 0;
                if (!rec.VesselSpawned)
                    LogSpawnFailure(rec, index, maxSpawnAttempts);
                return;
            }

            bool isEva = !string.IsNullOrEmpty(rec.EvaCrewName);
            bool isBreakupContinuous = rec.ChildBranchPointId != null && rec.TerminalStateValue.HasValue;
            bool useRecordedTerminalOrbit = ShouldUseRecordedTerminalOrbitSpawnState(rec, isEva);

            // Resolve spawn position from snapshot or trajectory endpoint.
            var lastPt = rec.Points[rec.Points.Count - 1];
            double spawnLat, spawnLon, spawnAlt;
            ResolveSpawnPosition(rec, index, lastPt, out spawnLat, out spawnLon, out spawnAlt);

            CelestialBody body = null;
            if (RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out string spawnBodyName))
                body = FlightGlobals.Bodies?.Find(b => b.name == spawnBodyName);

            if (body == null)
            {
                string reason = string.IsNullOrEmpty(spawnBodyName)
                    ? "no authoritative endpoint body"
                    : $"bodyName='{spawnBodyName}' not found";
                ParsekLog.Warn("Spawner", $"Spawn #{index} ({rec.VesselName}): {reason} — " +
                    $"falling back to validated snapshot respawn");
                LogSpawnContext(rec, double.MaxValue);
                rec.SpawnedVesselPersistentId = RespawnValidatedRecording(
                    rec,
                    logContext,
                    excludeCrew,
                    preserveIdentity: preserveIdentity,
                    allowExistingSourceDuplicate: allowExistingSourceDuplicate);
                rec.VesselSpawned = rec.SpawnedVesselPersistentId != 0;
                if (!rec.VesselSpawned)
                    LogSpawnFailure(rec, index, maxSpawnAttempts);
                return;
            }

            double spawnUT = Planetarium.GetUniversalTime();
            Vector3d orbitalSpawnVelocity = Vector3d.zero;
            Orbit orbitalSpawnOrbit = null;
            if (useRecordedTerminalOrbit)
            {
                if (TryResolveRecordedTerminalOrbitSpawnState(
                    rec, body, spawnUT, out double orbitLat, out double orbitLon,
                    out double orbitAlt, out orbitalSpawnVelocity, out orbitalSpawnOrbit))
                {
                    spawnLat = orbitLat;
                    spawnLon = orbitLon;
                    spawnAlt = orbitAlt;

                    ParsekLog.Verbose("Spawner", string.Format(CultureInfo.InvariantCulture,
                        "Spawn #{0} ({1}): using recorded terminal orbit propagated to current UT " +
                        "(ut={2:F2}, body={3}, lat={4:F4}, lon={5:F4}, alt={6:F1}, speed={7:F1})",
                        index, rec.VesselName, spawnUT, body.name, spawnLat, spawnLon,
                        spawnAlt, orbitalSpawnVelocity.magnitude));
                }
                else
                {
                    useRecordedTerminalOrbit = false;
                }
            }

            Vector3d spawnPos = body.GetWorldSurfacePosition(spawnLat, spawnLon, spawnAlt);

            // T57: resolve parent vessel PID for EVA collision exemption.
            // EVA trajectories overlap the parent vessel for their entire length;
            // without exemption, walkback exhausts all points and abandons the spawn.
            uint exemptPid = isEva ? ResolveParentVesselPid(rec) : 0;
            var collision = CheckSpawnCollisions(rec, index, isEva, body,
                spawnLat, spawnLon, spawnAlt, spawnPos, exemptPid);
            if (collision.blocked) return;
            // Walkback may have rewritten the spawn coordinates (#264)
            spawnLat = collision.lat;
            spawnLon = collision.lon;
            spawnAlt = collision.alt;
            spawnPos = collision.pos;

            ApplyResolvedSpawnStateOverrides(
                rec,
                index,
                lastPt,
                spawnLat,
                spawnLon,
                spawnAlt,
                isEva,
                isBreakupContinuous,
                useRecordedTerminalOrbit);

            // Dead crew guard: if ALL crew in the snapshot are dead, abandon spawn.
            // Spawning a crewless command pod is worse than not spawning at all. (#170)
            // Individual dead crew are already removed by RespawnVessel.RemoveDeadCrewFromSnapshot;
            // this catches the case where the entire crew complement is dead.
            if (!isEva && rec.VesselSnapshot != null)
            {
                if (ShouldBlockSpawnForDeadCrewInSnapshot(rec.VesselSnapshot, out List<string> snapshotCrew))
                {
                    rec.VesselSpawned = true;
                    rec.SpawnAbandoned = true;
                    var classified = ClassifySnapshotCrew(snapshotCrew);
                    ParsekLog.Warn("Spawner",
                        $"Spawn ABANDONED for #{index} ({rec.VesselName}): no spawnable crew — " +
                        FormatSpawnableClassificationSummary(classified));
                    return;
                }
            }

            // Find nearest vessel for spawn context logging
            double closestDist = FindNearestVesselDistance(spawnPos, body);

            LogSpawnContext(rec, closestDist);

            // SpawnAtPosition dispatch: EVA and breakup-continuous paths rebuild the ORBIT
            // subnode from the resolved spawn position + velocity. Orbiting paths now prefer
            // the recording's stored terminal orbit propagated to the current spawn UT, so
            // deferred high-warp spawns do not mix an old endpoint state vector with a later
            // planet rotation.
            //
            // - Orbital/Docked (#171): raw snapshot orbit was captured during ascent
            //   (suborbital), KSP's on-rails pressure check would destroy the vessel at
            //   periapsis. SpawnAtPosition constructs a new orbit + correct sit from a
            //   current-UT propagated orbital state when terminal orbit data is available.
            // - EVA (#264): the snapshot's stale ORBIT subnode (captured when the kerbal
            //   was on the parent ladder) causes OrbitDriver.updateFromParameters to
            //   overwrite the corrected transform with the parent position on the first
            //   physics frame after load. SpawnAtPosition rebuilds the ORBIT from the
            //   walked endpoint so the kerbal stays where recorded.
            // - Breakup-continuous (#224 follow-up via #264): same stale-ORBIT mechanism.
            //   Rotation is preserved via the same surface-relative ProtoVessel.rot helper
            //   used by snapshot-prep fallbacks.
            //
            // The earlier OverrideSnapshotPosition calls on the EVA and breakup-continuous
            // paths remain as defense-in-depth for the RespawnVessel fallback below: if
            // SpawnAtPosition returns 0, RespawnVessel still sees the corrected snapshot
            // endpoint pose. For breakup-continuous surface/state-vector spawns that now
            // includes the surface-relative ProtoVessel.rot rewrite, so the degraded
            // fallback keeps the same KSP load-frame contract.
            bool routeThroughSpawnAtPosition = ShouldRouteThroughSpawnAtPosition(rec);
            if (routeThroughSpawnAtPosition)
            {
                ConfigNode validatedSpawnSnapshot = BuildValidatedRespawnSnapshot(
                    rec,
                    spawnUT,
                    $"{logContext} SpawnAtPosition");
                if (validatedSpawnSnapshot == null)
                {
                    LogSpawnFailure(rec, index, maxSpawnAttempts);
                    return;
                }

                Vector3d velocity = useRecordedTerminalOrbit
                    ? orbitalSpawnVelocity
                    : new Vector3d(lastPt.velocity.x, lastPt.velocity.y, lastPt.velocity.z);
                Quaternion? surfaceRelativeRotationArg = null;
                if (!useRecordedTerminalOrbit
                    && (isEva || isBreakupContinuous)
                    && TryGetPreferredSpawnRotationFrame(
                        rec, lastPt,
                        out _,
                        out Quaternion preferredSurfaceRelativeRotation,
                        out _))
                {
                    surfaceRelativeRotationArg = preferredSurfaceRelativeRotation;
                }

                string pathLabel;
                if (useRecordedTerminalOrbit) pathLabel = "Orbital";
                else if (isEva) pathLabel = "EVA";
                else if (isBreakupContinuous) pathLabel = "Breakup";
                else pathLabel = "Orbital";

                rec.SpawnedVesselPersistentId = SpawnAtPosition(
                    validatedSpawnSnapshot, body, spawnLat, spawnLon, spawnAlt, velocity, spawnUT, excludeCrew,
                    preserveIdentity: preserveIdentity,
                    terminalState: rec.TerminalStateValue,
                    surfaceRelativeRotation: surfaceRelativeRotationArg,
                    orbitOverride: orbitalSpawnOrbit);
                rec.VesselSpawned = rec.SpawnedVesselPersistentId != 0;
                if (rec.VesselSpawned)
                {
                    ParsekLog.Info("Spawner", string.Format(CultureInfo.InvariantCulture,
                        "{0} vessel spawn for #{1} ({2}) pid={3} lat={4:F4} lon={5:F4} alt={6:F1} terminal={7}",
                        pathLabel, index, rec.VesselName, rec.SpawnedVesselPersistentId,
                        spawnLat, spawnLon, spawnAlt, rec.TerminalStateValue));
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
                    $"falling through to validated snapshot respawn (degraded fallback)");
            }

            rec.SpawnedVesselPersistentId = RespawnValidatedRecording(
                rec,
                logContext,
                excludeCrew,
                preserveIdentity: preserveIdentity,
                allowExistingSourceDuplicate: allowExistingSourceDuplicate);
            rec.VesselSpawned = rec.SpawnedVesselPersistentId != 0;
            if (rec.VesselSpawned)
            {
                string lat = rec.VesselSnapshot.GetValue("lat") ?? "?";
                string lon = rec.VesselSnapshot.GetValue("lon") ?? "?";
                string alt = rec.VesselSnapshot.GetValue("alt") ?? "?";
                string sit = rec.VesselSnapshot.GetValue("sit") ?? "?";
                ParsekLog.Info("Spawner",
                    $"Vessel spawn for #{index} ({rec.VesselName}) pid={rec.SpawnedVesselPersistentId} " +
                    $"sit={sit} lat={lat} lon={lon} alt={alt}");
                ParsekLog.ScreenMessage($"Vessel '{rec.VesselName}' has appeared!", 4f);
            }
            else
            {
                LogSpawnFailure(rec, index, maxSpawnAttempts);
            }
        }

        private static void ApplyResolvedSpawnStateOverrides(
            Recording rec,
            int index,
            TrajectoryPoint lastPt,
            double spawnLat,
            double spawnLon,
            double spawnAlt,
            bool isEva,
            bool isBreakupContinuous,
            bool useRecordedTerminalOrbit)
        {
            // EVA spawn position fix: update the snapshot's lat/lon/alt to the recording
            // endpoint. The snapshot was captured at EVA start (kerbal on the pod's ladder),
            // but the kerbal walked to a different location during the recording. Without
            // this override, the kerbal spawns on top of the parent vessel and grabs its
            // ladder, triggering KSP's "Kerbals on a ladder — cannot save" error.
            if (isEva && rec.VesselSnapshot != null)
            {
                ApplyResolvedSpawnStateToSnapshot(
                    rec.VesselSnapshot,
                    rec,
                    lastPt,
                    spawnLat,
                    spawnLon,
                    spawnAlt,
                    index,
                    rec.VesselName);
            }

            // Breakup-continuous recordings: snapshot position is from breakup time (mid-air).
            // RespawnVessel uses raw snapshot position, so override it with the trajectory
            // endpoint. Same pattern as EVA fix above. (#224)
            if (!isEva && isBreakupContinuous && rec.VesselSnapshot != null)
            {
                ApplyResolvedSpawnStateToSnapshot(
                    rec.VesselSnapshot,
                    rec,
                    lastPt,
                    spawnLat,
                    spawnLon,
                    spawnAlt,
                    index,
                    rec.VesselName,
                    allowPreferredRotation: !useRecordedTerminalOrbit,
                    stripEvaLadder: false);
            }

            // Surface terminal override: for any LANDED/SPLASHED recording, the snapshot
            // position may be from mid-flight (captured before the vessel reached its final
            // rest position). ResolveSpawnPosition clamped the altitude, but RespawnVessel
            // uses the raw snapshot. Override position and rotation so the vessel spawns
            // in its near-landing orientation, not the mid-flight descent pose. (#231)
            if (!isEva && !isBreakupContinuous && rec.VesselSnapshot != null
                && IsSurfaceTerminal(rec.TerminalStateValue))
            {
                ApplyResolvedSpawnStateToSnapshot(
                    rec.VesselSnapshot,
                    rec,
                    lastPt,
                    spawnLat,
                    spawnLon,
                    spawnAlt,
                    index,
                    rec.VesselName,
                    stripEvaLadder: false);
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
            double spawnLat, double spawnLon, double spawnAlt, Vector3d spawnPos,
            uint exemptVesselPid = 0)
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
                SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(
                    spawnPos, spawnBounds, 5f, skipActive, exemptVesselPid);
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
                    && ShouldRecoverBlockerVessel(rec, blockerName, resolvedRecName, blockerVessel.persistentId))
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
                        SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(
                            spawnPos, spawnBounds, 5f, skipActive, exemptVesselPid);
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
                        rec, index, spawnBounds, body, out walkLat, out walkLon, out walkAlt,
                        exemptVesselPid);
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
            out double walkLat, out double walkLon, out double walkAlt,
            uint exemptVesselPid = 0)
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
                        worldPos, spawnBounds, 5f, skipActive, exemptVesselPid);
                    return ov;
                });

            if (walkResult.found)
            {
                walkLat = walkResult.lat;
                walkLon = walkResult.lon;
                walkAlt = walkResult.alt;

                // For LANDED terminals: the walkback may return a mid-descent trajectory
                // point where the vessel was 10-50m in the air (approaching the landing
                // spot from a diagonal). Trusting that altitude would spawn the vessel in
                // mid-air and let it fall. Fix: top-down Physics.Raycast at the walkback
                // (lat, lon) to find the true surface (including mesh objects like the
                // Island Airfield runway), and clamp the candidate altitude to
                // surface + small clearance when it's notably higher.
                //
                // The raycast uses the same layer mask as KSP's Vessel.GetHeightFromSurface
                // (default + terrain + buildings), so it hits both PQS terrain AND placed
                // colliders correctly. Falls back to the PQS safety floor if the raycast
                // misses (e.g., target area not loaded).
                if (rec.TerminalStateValue == TerminalState.Landed)
                {
                    double raycastSurface = TryFindSurfaceAltitudeViaRaycast(body, walkLat, walkLon, walkAlt);
                    if (!double.IsNaN(raycastSurface))
                    {
                        double above = walkAlt - raycastSurface;
                        // Snap when the candidate is well above the surface (mid-descent
                        // trajectory point) OR below the surface (physics glitch / recorded
                        // alt ended up under the real mesh). Review finding: the original
                        // `above > threshold` check silently passed negative values through.
                        if (above > WalkbackSurfaceSnapThresholdMeters || above < 0)
                        {
                            double snapped = raycastSurface + WalkbackSurfaceClearanceMeters;
                            ParsekLog.Info("Spawner",
                                $"Walkback altitude snapping to surface for #{index} ({rec.VesselName}): " +
                                $"walkAlt={walkAlt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} " +
                                $"raycastSurface={raycastSurface.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} " +
                                $"above={above.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}m " +
                                $"-> snapping to {snapped.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} " +
                                $"(raycast=surface + {WalkbackSurfaceClearanceMeters}m)");
                            walkAlt = snapped;
                        }
                        // else: trusted — happy path, no log. Walkback substep spam.
                    }
                    else
                    {
                        double pqsTerrain = body.TerrainAltitude(walkLat, walkLon);
                        ParsekLog.VerboseRateLimited("Spawner", $"walkback-pqs-fallback-{index}",
                            $"Walkback raycast missed for #{index} ({rec.VesselName}) — " +
                            $"falling back to PQS safety floor (pqsTerrain={pqsTerrain.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)})");
                        walkAlt = ClampAltitudeForLanded(walkAlt, pqsTerrain, index, rec.VesselName);
                    }
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
            string endpointBodyName = lastPt.bodyName;
            double endpointLat = lastPt.latitude;
            double endpointLon = lastPt.longitude;
            double endpointAlt = lastPt.altitude;
            if (RecordingEndpointResolver.TryGetRecordingEndpointCoordinates(
                rec, out string resolvedBodyName, out double resolvedLat, out double resolvedLon, out double resolvedAlt))
            {
                endpointBodyName = resolvedBodyName;
                endpointLat = resolvedLat;
                endpointLon = resolvedLon;
                endpointAlt = resolvedAlt;
            }

            // EVA (#175): snapshot position is from EVA start (kerbal on the pod's ladder).
            // Breakup-continuous (#224): snapshot position is from breakup time (mid-air).
            // Both use trajectory endpoint instead. No early return — falls through to clamping.
            bool isEva = !string.IsNullOrEmpty(rec.EvaCrewName);
            bool isBreakupContinuous = rec.ChildBranchPointId != null && rec.TerminalStateValue.HasValue;
            bool useTrajectoryEndpoint = isEva || isBreakupContinuous;

            if (useTrajectoryEndpoint)
            {
                lat = endpointLat;
                lon = endpointLon;
                alt = endpointAlt;
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
                    lat = endpointLat;
                    lon = endpointLon;
                    alt = endpointAlt;
                    ParsekLog.Verbose("Spawner",
                        $"No snapshot lat/lon/alt for #{index} ({rec.VesselName}) — using trajectory endpoint for collision check");
                }
            }

            // Safety net: clamp altitude for surface terminal states.
            // SPLASHED: clamp to sea level.
            // LANDED: trust the recorded altitude (it's where the vessel came to rest — the
            // terminal state proves it had settled). Only push UP if the recorded altitude is
            // below the current PQS terrain (indicates the surface has shifted UP since
            // recording — rare, e.g. KSP version change or terrain-mod changes). Do NOT
            // clamp DOWN to PQS terrain — that would bury vessels recorded on mesh objects
            // (Island Airfield, launchpad, KSC buildings) since body.TerrainAltitude() is
            // PQS-only and ignores placed colliders. KSP's Vessel.CheckGroundCollision will
            // fire once the vessel loads and adjust using getLowestPoint() against real
            // colliders (including buildings), handling any residual clipping properly.
            if (rec.TerminalStateValue == TerminalState.Splashed)
            {
                if (alt != 0.0)
                {
                    ParsekLog.Verbose("Spawner",
                        $"Clamped altitude for SPLASHED spawn #{index} ({rec.VesselName}): {alt:F1} -> 0");
                    alt = 0;
                }
            }
            else if (rec.TerminalStateValue == TerminalState.Landed)
            {
                string bodyName = endpointBodyName;
                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                if (body != null)
                {
                    double pqsTerrain = body.TerrainAltitude(lat, lon);
                    alt = ClampAltitudeForLanded(alt, pqsTerrain, index, rec.VesselName);
                }
            }
        }

        /// <summary>
        /// Safety floor above PQS terrain. Only used when the recorded altitude is below
        /// current PQS terrain (terrain shift since recording) — we push up by this much
        /// above raw terrain to avoid an underground spawn. Not a clearance target.
        /// </summary>
        internal const double UndergroundSafetyFloorMeters = 2.0;

        /// <summary>
        /// Trigger threshold for walkback-to-surface snapping. If a walkback trajectory
        /// candidate is more than this many metres above the raycast-detected surface at
        /// its (lat, lon), we snap it down to surface + clearance — otherwise trust the
        /// recorded altitude. Guards against spawning a diagonally-descending vessel in
        /// mid-air.
        /// </summary>
        internal const double WalkbackSurfaceSnapThresholdMeters = 5.0;

        /// <summary>
        /// Clearance above the raycast-detected surface when snapping a walkback candidate
        /// down to the ground. KSP's Vessel.CheckGroundCollision will fire on load and
        /// further adjust via getLowestPoint(), so we only need a small breathing-room.
        /// </summary>
        internal const double WalkbackSurfaceClearanceMeters = 1.0;

        /// <summary>
        /// Part-collider layer mask used by the walkback raycast. Matches KSP's
        /// Vessel.GetHeightFromSurface: <c>LayerUtil.DefaultEquivalent | 0x8000 | 0x80000</c>
        /// — default layers + terrain (layer 15) + buildings (layer 19). Hits BOTH raw PQS
        /// terrain AND placed mesh objects (Island Airfield, launchpad, KSC facilities),
        /// unlike body.TerrainAltitude() which is PQS-only.
        /// </summary>
        private static int WalkbackSurfaceLayerMask
        {
            get { return LayerUtil.DefaultEquivalent | 0x8000 | 0x80000; }
        }

        /// <summary>
        /// Starting height above expected surface for the walkback raycast origin. We
        /// cast downward from (startAltAboveSurface + this) to ensure the ray origin is
        /// above any collider at the query point.
        /// </summary>
        private const double WalkbackRaycastOriginOffsetMeters = 1000.0;

        /// <summary>
        /// Maximum raycast distance. Covers the offset + a margin for deep valleys.
        /// Combined with <see cref="WalkbackRaycastOriginOffsetMeters"/> this gives a
        /// practical altitude ceiling of ~1500 m above surface for walkback candidates
        /// (above that, the ray won't reach the surface and <see cref="TryFindSurfaceAltitudeViaRaycast"/>
        /// returns NaN, falling back to the PQS safety floor via
        /// <see cref="ClampAltitudeForLanded"/>). This is fine for the walkback's target
        /// use case — LANDED terminal trajectories where the final descent happens in
        /// the last few hundred meters. Higher-altitude sub-orbital walkback candidates
        /// degrade gracefully to PQS-based clamping.
        /// </summary>
        private const float WalkbackRaycastMaxDistanceMeters = 2500f;

        /// <summary>
        /// Fires a top-down <see cref="Physics.Raycast"/> at <paramref name="lat"/>/<paramref name="lon"/>
        /// to find the true surface altitude — including mesh objects the PQS terrain
        /// query cannot see. Returns <see cref="double.NaN"/> if no collider is hit
        /// (target area not loaded, space, etc.). Used by the walkback clamp path to
        /// snap mid-air trajectory candidates to the ground without losing mesh-object
        /// offsets.
        /// </summary>
        internal static double TryFindSurfaceAltitudeViaRaycast(
            CelestialBody body, double lat, double lon, double startAltAboveSurface)
        {
            if (body == null)
            {
                ParsekLog.Verbose("Spawner",
                    "TryFindSurfaceAltitudeViaRaycast: null body — returning NaN");
                return double.NaN;
            }

            double originAlt = startAltAboveSurface + WalkbackRaycastOriginOffsetMeters;
            Vector3d originWorld = body.GetWorldSurfacePosition(lat, lon, originAlt);
            Vector3d upAxis = body.GetSurfaceNVector(lat, lon);

            RaycastHit hit;
            bool didHit = Physics.Raycast(
                (Vector3)originWorld,
                -(Vector3)upAxis,
                out hit,
                WalkbackRaycastMaxDistanceMeters,
                WalkbackSurfaceLayerMask,
                QueryTriggerInteraction.Ignore);

            if (!didHit)
            {
                // Rate-limited: walkback may call this once per sub-step on trajectories
                // where the area is not loaded (distant spawn from tracking station).
                ParsekLog.VerboseRateLimited("Spawner", "walkback-raycast-miss",
                    $"TryFindSurfaceAltitudeViaRaycast: no hit at " +
                    $"lat={lat.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"lon={lon.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"(origin alt={originAlt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}) — returning NaN");
                return double.NaN;
            }

            double hitAlt = FlightGlobals.getAltitudeAtPos((Vector3d)hit.point, body);
            ParsekLog.VerboseRateLimited("Spawner", "walkback-raycast-hit",
                $"TryFindSurfaceAltitudeViaRaycast: hit at alt={hitAlt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"(distance={hit.distance.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}m, " +
                $"collider={(hit.collider != null ? hit.collider.name : "<null>")})");
            return hitAlt;
        }

        /// <summary>
        /// NaN-fallback clearance for ghost playback of Landed/Splashed recordings.
        /// Only used when <c>Recording.TerrainHeightAtEnd</c> is NaN (legacy recordings
        /// without surface data). When surface data is available,
        /// <c>ApplyLandedGhostClearance</c> trusts the recorded altitude directly and
        /// only pushes up when it falls below the current PQS underground safety floor.
        /// </summary>
        internal const double LandedGhostClearanceMeters = 4.0;

        /// <summary>
        /// Pure: sanity check landed spawn altitude against PQS terrain.
        ///
        /// <para>Philosophy: the recorded altitude is the TRUTH — the vessel was literally
        /// at that altitude when the recording terminal state was determined to be LANDED.
        /// For vessels on terrain, the recorded clearance above PQS is small (1-5m). For
        /// vessels on mesh objects (Island Airfield, launchpad, KSC buildings), the recorded
        /// clearance above PQS can be 10-30m because <c>body.TerrainAltitude()</c> only
        /// returns the raw planetary surface UNDER the mesh object. We must preserve both.</para>
        ///
        /// <para>The only case where clamping is needed: the PQS terrain has shifted UP
        /// since recording (KSP update, terrain mod change). Then the recorded altitude may
        /// be below the current terrain, and we push up by <see cref="UndergroundSafetyFloorMeters"/>.
        /// Otherwise we return the recorded altitude unchanged and let KSP's
        /// <c>Vessel.CheckGroundCollision</c> handle any remaining part-geometry clipping
        /// via <c>getLowestPoint()</c> against real colliders (including buildings).</para>
        /// </summary>
        internal static double ClampAltitudeForLanded(double alt, double pqsTerrainAlt,
            int index, string vesselName)
        {
            var ic = CultureInfo.InvariantCulture;
            double safetyFloor = pqsTerrainAlt + UndergroundSafetyFloorMeters;

            if (alt < safetyFloor)
            {
                // Rate-limited by spawn index so each spawn logs once, but repeated
                // walkback substeps for the same spawn are deduped.
                double delta = safetyFloor - alt;
                ParsekLog.VerboseRateLimited("Spawner", $"clamp-pqs-floor-{index}",
                    $"Clamped altitude for LANDED spawn #{index} ({vesselName}): " +
                    $"{alt.ToString("F1", ic)} -> {safetyFloor.ToString("F1", ic)} " +
                    $"(below-pqs-floor, delta=+{delta.ToString("F1", ic)}m, pqsTerrain={pqsTerrainAlt.ToString("F1", ic)})");
                return safetyFloor;
            }

            // Happy path (recorded altitude preserved) — no log. Walkback calls this
            // once per substep, so logging the no-op case would spam hundreds of lines
            // per spawn attempt. The diagnostic value is low; callers already log when
            // they actually resolve the final spawn position.
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
        /// Builds a set of crew names from the given list that are spawn-blocking
        /// (strictly Dead, OR Missing-and-not-reserved). Reserved crew that are
        /// Missing are NOT included: they will be rescued to Available before the
        /// snapshot is loaded (see <see cref="RescueReservedMissingCrewInSnapshot"/>),
        /// so they are spawnable even though <see cref="IsCrewDeadInRoster"/>
        /// returns true for them today. This carve-out is symmetric with the
        /// reserved-keep branch in <see cref="RemoveDeadCrewFromSnapshot"/>
        /// (#170 / #608).
        /// Runtime-only: accesses HighLogic.CurrentGame.CrewRoster.
        /// Returns an empty set if the roster is unavailable.
        /// </summary>
        private static HashSet<string> BuildDeadCrewSet(List<string> crewNames)
        {
            var deadSet = new HashSet<string>();
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return deadSet;
            var reserved = CrewReservationManager.CrewReplacements;
            for (int i = 0; i < crewNames.Count; i++)
            {
                string name = crewNames[i];
                if (IsCrewStrictlyDeadInRoster(name, roster))
                {
                    deadSet.Add(name);
                    continue;
                }
                if (IsCrewDeadInRoster(name, roster) && !reserved.ContainsKey(name))
                    deadSet.Add(name);
            }
            return deadSet;
        }

        /// <summary>
        /// Per-crew classification used by the dead-crew abandon log so the WARN
        /// can break down the snapshot crew list by reason instead of just
        /// listing names. (#608/#609)
        /// </summary>
        internal enum SpawnableClassification
        {
            Alive = 0,                    // Available / Assigned / Crew
            ReservedMissingRescuable = 1, // Missing but reserved — will be rescued at spawn time
            MissingNotReserved = 2,       // Missing, no reservation — counts toward block
            StrictlyDead = 3,             // Dead — counts toward block, never rescuable
        }

        /// <summary>
        /// Classify each snapshot crew name into the categories the abandon log
        /// reports. Pure decision: does not mutate state. (#608/#609)
        /// </summary>
        internal static List<KeyValuePair<string, SpawnableClassification>> ClassifySnapshotCrew(
            List<string> snapshotCrew)
        {
            var result = new List<KeyValuePair<string, SpawnableClassification>>(
                snapshotCrew != null ? snapshotCrew.Count : 0);
            if (snapshotCrew == null || snapshotCrew.Count == 0) return result;
            var roster = HighLogic.CurrentGame?.CrewRoster;
            var reserved = CrewReservationManager.CrewReplacements;
            for (int i = 0; i < snapshotCrew.Count; i++)
            {
                string name = snapshotCrew[i];
                SpawnableClassification cls;
                if (roster != null && IsCrewStrictlyDeadInRoster(name, roster))
                    cls = SpawnableClassification.StrictlyDead;
                else if (roster != null && IsCrewDeadInRoster(name, roster))
                    cls = reserved.ContainsKey(name)
                        ? SpawnableClassification.ReservedMissingRescuable
                        : SpawnableClassification.MissingNotReserved;
                else
                    cls = SpawnableClassification.Alive;
                result.Add(new KeyValuePair<string, SpawnableClassification>(name, cls));
            }
            return result;
        }

        /// <summary>
        /// Format the per-category summary used by both the abandon WARN and
        /// the carve-out Verbose log (#608/#609). Counts each category and lists the
        /// names with their classification.
        /// </summary>
        internal static string FormatSpawnableClassificationSummary(
            List<KeyValuePair<string, SpawnableClassification>> classified)
        {
            int alive = 0, reservedMissing = 0, missingNotReserved = 0, strictlyDead = 0;
            for (int i = 0; i < classified.Count; i++)
            {
                switch (classified[i].Value)
                {
                    case SpawnableClassification.Alive: alive++; break;
                    case SpawnableClassification.ReservedMissingRescuable: reservedMissing++; break;
                    case SpawnableClassification.MissingNotReserved: missingNotReserved++; break;
                    case SpawnableClassification.StrictlyDead: strictlyDead++; break;
                }
            }
            var sb = new System.Text.StringBuilder();
            sb.Append("total=").Append(classified.Count);
            sb.Append(" strictlyDead=").Append(strictlyDead);
            sb.Append(" missingNotReserved=").Append(missingNotReserved);
            sb.Append(" reservedMissing=").Append(reservedMissing);
            sb.Append(" alive=").Append(alive);
            sb.Append(" [");
            for (int i = 0; i < classified.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(classified[i].Key).Append(": ").Append(classified[i].Value);
            }
            sb.Append("]");
            return sb.ToString();
        }

        internal static bool ShouldBlockSpawnForDeadCrewInSnapshot(
            ConfigNode snapshot,
            out List<string> snapshotCrew)
        {
            snapshotCrew = ExtractCrewNamesFromSnapshot(snapshot);
            var deadSet = BuildDeadCrewSet(snapshotCrew);
            bool block = ShouldBlockSpawnForDeadCrew(snapshotCrew, deadSet);

            // (#608/#609) When the carve-out lets a snapshot through that the
            // pre-fix code would have abandoned, log a Verbose breakdown so
            // playtest logs make the recovery visible. Pre-fix decision used
            // IsCrewDeadInRoster (Dead OR Missing) for every name, so the old
            // would-have-blocked predicate is "every snapshot name is Dead OR
            // Missing in the roster". A reservation-only carve-out means at
            // least one ReservedMissingRescuable entry made the difference.
            if (!block && snapshotCrew.Count > 0)
            {
                var classified = ClassifySnapshotCrew(snapshotCrew);
                bool wouldHaveBlocked = true;
                bool anyReservedMissing = false;
                for (int i = 0; i < classified.Count; i++)
                {
                    var c = classified[i].Value;
                    if (c == SpawnableClassification.Alive) { wouldHaveBlocked = false; }
                    else if (c == SpawnableClassification.ReservedMissingRescuable) anyReservedMissing = true;
                }
                if (wouldHaveBlocked && anyReservedMissing)
                {
                    ParsekLog.Verbose("Spawner",
                        "Spawn-block carve-out applied (#608/#609): allowing spawn that pre-fix would " +
                        "have abandoned because reserved+Missing crew is rescuable — " +
                        FormatSpawnableClassificationSummary(classified));
                }
            }

            return block;
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
        ///
        /// <para>#112 original scenario: the player quicksaves a Parsek-spawned vessel and
        /// then loads. KSP restores the vessel from the quicksave with its original PID,
        /// and Parsek tries to spawn the same recording again. We end up with two copies
        /// of the same recording's vessel overlapping. The correct response is to destroy
        /// the quicksave-restored copy (same PID as before) and keep the fresh spawn.</para>
        ///
        /// <para>#312 regression: the original check matched by NAME only. When multiple
        /// recordings of the same vessel (e.g. four "Crater Crawler" showcases on the
        /// runway) spawn near each other, each new spawn's overlap-check found the
        /// PREVIOUS recording's vessel -- same name -- and destroyed it. Only 2 of 4
        /// recordings survived in a playtest because each new spawn killed the last.
        /// Siblings should walkback, not recover.</para>
        ///
        /// <para>Fix: the blocker must match this recording's OWN <see cref="Recording.SpawnedVesselPersistentId"/>
        /// -- the PID we recorded the last time WE spawned. That's the only way to be
        /// certain the blocker is a duplicate of ourselves (the #112 scenario). If the
        /// blocker belongs to a sibling recording or to a non-Parsek vessel, PIDs
        /// differ and we fall through to walkback.</para>
        /// </summary>
        internal static bool ShouldRecoverBlockerVessel(
            Recording rec, string blockerName, string recordingVesselName, uint blockerPid)
        {
            if (rec == null || blockerPid == 0) return false;
            if (string.IsNullOrEmpty(blockerName) || string.IsNullOrEmpty(recordingVesselName))
                return false;
            if (!string.Equals(blockerName, recordingVesselName, StringComparison.Ordinal))
                return false;

            // Primary discriminator: the blocker must be the SAME vessel we spawned
            // previously for THIS recording. #112 applies only when KSP's quicksave
            // restored our own spawn with the same PID.
            return rec.VesselSpawned
                && rec.SpawnedVesselPersistentId != 0
                && rec.SpawnedVesselPersistentId == blockerPid;
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
            // [Phase 3] ERS-routed: chain-aware crew-exclusion walk must respect
            // effective recording visibility so that hidden / superseded chain
            // siblings do not spuriously exclude their crew.
            var committed = EffectiveState.ComputeERS();

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
            NormalizeCorrectedSituationLocationFields(snapshot);
            ParsekLog.Info("Spawner",
                $"Corrected unsafe snapshot situation: {currentSit} -> {corrected} " +
                $"(terminal={terminalState}) — prevents on-rails pressure destruction (#169)");
            return true;
        }

        internal static ConfigNode NormalizeStableSnapshotForPersistence(
            ConfigNode snapshot,
            TerminalState? terminalState,
            CelestialBody body = null,
            string logContext = null)
        {
            if (snapshot == null)
                return null;

            CorrectUnsafeSnapshotSituation(snapshot, terminalState);
            if ((terminalState == TerminalState.Landed
                    || terminalState == TerminalState.Splashed)
                && !object.ReferenceEquals(body, null))
            {
                ApplySurfaceOrbitToSnapshot(snapshot, body, logContext);
            }

            return snapshot;
        }

        private static void NormalizeCorrectedSituationLocationFields(ConfigNode snapshot)
        {
            if (snapshot == null)
                return;

            snapshot.SetValue("landedAt", string.Empty, true);
            snapshot.SetValue("displaylandedAt", string.Empty, true);
        }

        /// <summary>
        /// Overrides the snapshot's lat/lon/alt with the given endpoint coordinates.
        /// Optionally overrides rotation from the last trajectory point so the vessel
        /// spawns in its near-landing orientation rather than the mid-flight snapshot orientation.
        /// Modifies the snapshot in-place.
        /// </summary>
        internal static void OverrideSnapshotPosition(ConfigNode snapshot,
            double lat, double lon, double alt, int index, string vesselName,
            CelestialBody rotationBody = null,
            Quaternion? surfaceRelativeRotation = null,
            string rotationSource = null)
        {
            Quaternion? bodyRotation = rotationBody != null && rotationBody.bodyTransform != null
                ? (Quaternion?)rotationBody.bodyTransform.rotation
                : null;
            OverrideSnapshotPosition(
                snapshot,
                lat, lon, alt, index, vesselName,
                rotationBody?.name,
                bodyRotation,
                surfaceRelativeRotation,
                rotationSource);
        }

        internal static void OverrideSnapshotPosition(ConfigNode snapshot,
            double lat, double lon, double alt, int index, string vesselName,
            string rotationBodyName,
            Quaternion? rotationBodyRotation,
            Quaternion? surfaceRelativeRotation,
            string rotationSource = null)
        {
            if (snapshot == null) return;

            string oldAlt = snapshot.GetValue("alt") ?? "?";

            snapshot.SetValue("lat", lat.ToString("R", CultureInfo.InvariantCulture), true);
            snapshot.SetValue("lon", lon.ToString("R", CultureInfo.InvariantCulture), true);
            snapshot.SetValue("alt", alt.ToString("R", CultureInfo.InvariantCulture), true);

            bool rotationRequested = surfaceRelativeRotation.HasValue;
            string rotationContext = $"Snapshot override #{index} ({vesselName})";
            if (!string.IsNullOrEmpty(rotationSource))
                rotationContext += $", source={rotationSource}";
            bool rotationUpdated = TryApplySpawnRotationFromSurfaceRelative(
                snapshot,
                rotationBodyName,
                rotationBodyRotation,
                surfaceRelativeRotation,
                rotationContext);

            ParsekLog.Info("Spawner",
                $"Snapshot position override for #{index} ({vesselName}): " +
                $"alt {oldAlt} → {alt.ToString("R", CultureInfo.InvariantCulture)}" +
                (rotationUpdated
                    ? $" (rot updated from surface-relative frame{(!string.IsNullOrEmpty(rotationSource) ? $", source={rotationSource}" : "")})"
                    : rotationRequested ? " (rot unchanged)" : ""));
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

        /// <summary>
        /// (#608/#609) Pre-spawn pass: for each crew name in the snapshot that is
        /// reserved AND currently Missing in the roster, flip the roster status
        /// back to Available so KSP's <c>ProtoVessel.Load → Part.RegisterCrew</c>
        /// path can place them on the spawned vessel cleanly. Reserved+Missing
        /// is the post-Re-Fly-strip state where the original capsule was
        /// destroyed (KSP marked the crew Missing) but the recording is about
        /// to materialize and bring them back.
        ///
        /// <para>Mirrors the rescue branches in
        /// <see cref="CrewReservationManager.ReserveCrewIn"/> and
        /// <see cref="CrewReservationManager.PlaceOrphanedReplacements"/> so
        /// the three runtime paths agree on Missing-rescue semantics.</para>
        ///
        /// <para>Guards: skip strictly Dead crew (Dead is permanent —
        /// <see cref="RemoveDeadCrewFromSnapshot"/> will strip them). Skip
        /// non-reserved Missing crew (those should not be silently rescued —
        /// they were Missing for an external reason).</para>
        /// </summary>
        public static void RescueReservedMissingCrewInSnapshot(ConfigNode snapshot)
        {
            RescueReservedMissingCrewInSnapshot(snapshot, null);
        }

        /// <summary>
        /// Pid-scoping overload (#615 P1 review fourth pass). Collects the
        /// names actually flipped from Missing -> Available into
        /// <paramref name="rescuedNames"/> so the caller can call
        /// <see cref="CrewReservationManager.MarkRescuePlaced(string, ulong)"/>
        /// for each name once <c>ProtoVessel.Load</c> has assigned the new
        /// vessel's persistentId. The marker MUST be pid-scoped: a stale
        /// name-only marker from a long-past rescue would suppress a later
        /// unrelated fresh reservation for the same kerbal who happens to be
        /// on the active player vessel.
        /// </summary>
        public static void RescueReservedMissingCrewInSnapshot(
            ConfigNode snapshot, List<string> rescuedNames)
        {
            if (snapshot == null) return;
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                ParsekLog.Warn("Spawner",
                    "RescueReservedMissingCrewInSnapshot skipped: crew roster unavailable");
                return;
            }
            var reserved = CrewReservationManager.CrewReplacements;
            if (reserved == null || reserved.Count == 0) return;

            int rescued = 0;
            using (SuppressionGuard.Crew())
            {
                foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
                {
                    var crewNames = partNode.GetValues("crew");
                    for (int i = 0; i < crewNames.Length; i++)
                    {
                        string name = crewNames[i];
                        if (string.IsNullOrEmpty(name)) continue;
                        if (!reserved.ContainsKey(name)) continue;
                        ProtoCrewMember pcm = roster[name];
                        if (pcm == null) continue;
                        if (pcm.rosterStatus != ProtoCrewMember.RosterStatus.Missing) continue;
                        pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                        rescued++;
                        // #615 P1 review (fourth pass): the rescue-placed
                        // marker is now pid-scoped — the caller marks each
                        // rescued name with the spawned vessel's pid AFTER
                        // ProtoVessel.Load assigns one. Collect rescued names
                        // here so the caller can wire them to the new pid.
                        if (rescuedNames != null)
                            rescuedNames.Add(name);
                        ParsekLog.Info("Spawner",
                            $"Rescued reserved+Missing crew '{name}' -> Available before snapshot load (#608/#609; pending pid mark)");
                    }
                }
            }

            if (rescued > 0)
                ParsekLog.Info("Spawner",
                    $"Spawn prep: rescued {rescued} reserved+Missing crew member(s) -> Available (#608/#609; rescuedNames={(rescuedNames != null ? rescuedNames.Count.ToString() : "null")})");
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
            ComputeMaxDistanceCore(pending, bodyFirst, firstPoint);
        }

        /// <summary>
        /// Backfills <see cref="Recording.MaxDistanceFromLaunch"/> from trajectory points.
        /// Called from <see cref="ParsekFlight.FinalizeIndividualRecording"/> for tree recordings
        /// that reach finalization via ForceStop (which skips BuildCaptureRecording). Bug #290d.
        /// Requires FlightGlobals.Bodies to be available (KSP runtime only).
        /// </summary>
        internal static void BackfillMaxDistance(Recording rec)
        {
            if (rec == null || rec.Points == null || rec.Points.Count < 2) return;

            var firstPoint = rec.Points[0];
            CelestialBody bodyFirst;
            try
            {
                bodyFirst = FlightGlobals.Bodies?.Find(b => b.name == firstPoint.bodyName);
            }
            catch
            {
                // FlightGlobals not available (unit tests)
                return;
            }
            if (bodyFirst == null)
            {
                ParsekLog.Warn("Spawner",
                    $"BackfillMaxDistance: cannot resolve body '{firstPoint.bodyName}' for recording '{rec.RecordingId}'");
                return;
            }
            ComputeMaxDistanceCore(rec, bodyFirst, firstPoint);
            ParsekLog.Verbose("Spawner",
                $"BackfillMaxDistance: rec={rec.RecordingId} maxDist={rec.MaxDistanceFromLaunch:F0}m " +
                $"from {rec.Points.Count} points");
        }

        private static void ComputeMaxDistanceCore(Recording pending, CelestialBody bodyFirst, TrajectoryPoint firstPoint)
        {
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

        internal static bool TryGetSnapshotReferenceBodyName(ConfigNode snapshot, out string bodyName)
        {
            bodyName = null;
            if (snapshot == null)
                return false;

            ConfigNode orbitNode = snapshot.GetNode("ORBIT");
            if (orbitNode == null)
                return false;

            string rawRef = orbitNode.GetValue("REF");
            if (!int.TryParse(rawRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bodyIndex))
                return false;

            return TryResolveBodyNameByIndex(bodyIndex, out bodyName);
        }

        internal static bool TryResolveBodyNameByIndex(int bodyIndex, out string bodyName)
        {
            bodyName = null;

            ResolveBodyNameByIndexDelegate resolver = BodyNameResolverForTesting;
            if (resolver != null)
            {
                return resolver(bodyIndex, out bodyName);
            }

            try
            {
                if (FlightGlobals.Bodies == null
                    || bodyIndex < 0
                    || bodyIndex >= FlightGlobals.Bodies.Count)
                {
                    return false;
                }

                CelestialBody body = FlightGlobals.Bodies[bodyIndex];
                if (body == null || string.IsNullOrEmpty(body.name))
                    return false;

                bodyName = body.name;
                return true;
            }
            catch (Exception ex)
            {
                if (!IsHeadlessBodyRegistryFailure(ex))
                    throw;

                ParsekLog.Verbose("Spawner",
                    $"TryResolveBodyNameByIndex: FlightGlobals body registry unavailable for REF={bodyIndex}");
                return false;
            }
        }

        internal static bool TryResolveBodyByName(string bodyName, out CelestialBody body)
        {
            body = null;
            if (string.IsNullOrEmpty(bodyName))
                return false;

            ResolveBodyByNameDelegate resolver = BodyResolverForTesting;
            if (resolver != null)
            {
                return resolver(bodyName, out body);
            }

            try
            {
                body = FlightGlobals.Bodies?.Find(b => b != null && b.name == bodyName);
                return body != null;
            }
            catch (Exception ex)
            {
                if (!IsHeadlessBodyRegistryFailure(ex))
                    throw;

                ParsekLog.Verbose("Spawner",
                    $"TryResolveBodyByName: FlightGlobals body registry unavailable for '{bodyName}'");
                body = null;
                return false;
            }
        }

        private static bool IsHeadlessBodyRegistryFailure(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                if (current is System.Security.SecurityException)
                    return true;
            }

            return false;
        }

        internal static ConfigNode BuildValidatedRespawnSnapshot(Recording rec, double currentUT, string logContext)
        {
            string resolvedContext = DescribeSpawnValidationContext(rec, logContext);
            if (rec?.VesselSnapshot == null)
            {
                ParsekLog.Error("Spawner",
                    $"BuildValidatedRespawnSnapshot: missing VesselSnapshot for {resolvedContext}");
                return null;
            }

            return BuildValidatedRespawnSnapshot(rec.VesselSnapshot, rec, currentUT, logContext);
        }

        internal static ConfigNode BuildValidatedRespawnSnapshot(
            ConfigNode sourceSnapshot,
            Recording rec,
            double currentUT,
            string logContext)
        {
            string resolvedContext = DescribeSpawnValidationContext(rec, logContext);
            if (sourceSnapshot == null)
            {
                ParsekLog.Error("Spawner",
                    $"BuildValidatedRespawnSnapshot: missing prepared snapshot for {resolvedContext}");
                return null;
            }

            ConfigNode snapshot = sourceSnapshot.CreateCopy();
            CorrectUnsafeSnapshotSituation(snapshot, rec.TerminalStateValue);

            if (!TryRepairSnapshotBodyProvenance(snapshot, rec, currentUT, resolvedContext))
                return null;

            return snapshot;
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

            ConfigNode snapshot = BuildValidatedRespawnSnapshot(rec, currentUT, logContext);
            if (snapshot == null)
            {
                ParsekLog.Error("Spawner",
                    $"RespawnValidatedRecording: refusing spawn for {DescribeSpawnValidationContext(rec, logContext)}");
                return 0;
            }

            return RespawnVessel(snapshot, excludeCrew, preserveIdentity);
        }

        private static bool TryRepairSnapshotBodyProvenance(
            ConfigNode snapshot,
            Recording rec,
            double currentUT,
            string logContext)
        {
            if (snapshot == null || rec == null)
                return false;

            bool hasSnapshotPos = TryGetSnapshotDouble(snapshot, "lat", out _)
                && TryGetSnapshotDouble(snapshot, "lon", out _)
                && TryGetSnapshotDouble(snapshot, "alt", out _);
            bool hasSnapshotBody = TryGetSnapshotReferenceBodyName(snapshot, out string snapshotBodyName);
            bool hasEndpointCoords = RecordingEndpointResolver.TryGetRecordingEndpointCoordinates(
                rec,
                out string endpointBodyNameFromCoords,
                out double endpointLat,
                out double endpointLon,
                out double endpointAlt);
            string endpointBodyName = endpointBodyNameFromCoords;
            bool hasEndpointBody = hasEndpointCoords;
            if (!hasEndpointBody)
                hasEndpointBody = RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out endpointBodyName);
            bool landedLike = rec.TerminalStateValue == TerminalState.Landed
                || rec.TerminalStateValue == TerminalState.Splashed
                || IsLandedLikeSnapshot(snapshot);
            bool? bodyMismatch = hasEndpointBody && hasSnapshotBody
                ? (bool?)!string.Equals(snapshotBodyName, endpointBodyName, StringComparison.Ordinal)
                : null;
            bool needsSurfaceOrbitRepair = landedLike
                && !HasCanonicalSurfaceOrbitSignature(snapshot);
            bool needsRepair = !hasSnapshotPos
                || !hasSnapshotBody
                || bodyMismatch == true
                || needsSurfaceOrbitRepair;

            if (!needsRepair)
                return true;

            string repairBodyName = hasEndpointBody ? endpointBodyName : snapshotBodyName;
            if (string.IsNullOrEmpty(repairBodyName))
            {
                ParsekLog.Error("Spawner",
                    $"Spawn validation failed for {logContext}: " +
                    $"snapshot provenance is malformed (hasPos={hasSnapshotPos}, hasBody={hasSnapshotBody}) " +
                    $"and no repair body is available");
                return false;
            }

            if (!TryResolveBodyByName(repairBodyName, out CelestialBody body))
            {
                ParsekLog.Error("Spawner",
                    $"Spawn validation failed for {logContext}: " +
                    $"repair body '{repairBodyName}' was resolved but is not loaded");
                return false;
            }

            if (landedLike)
            {
                bool useEndpointCoords = hasEndpointCoords;
                bool useSnapshotCoords = !useEndpointCoords
                    && hasSnapshotPos
                    && hasSnapshotBody
                    && bodyMismatch != true;

                if (!useEndpointCoords && !useSnapshotCoords)
                {
                    ParsekLog.Error("Spawner",
                        $"Spawn validation failed for {logContext}: " +
                        "surface snapshot repair needs usable coordinates " +
                        $"(endpointCoords={hasEndpointCoords}, snapshotPos={hasSnapshotPos}, " +
                        $"snapshotBody={snapshotBodyName ?? "(none)"}, endpointBody={endpointBodyName ?? "(none)"})");
                    return false;
                }

                if (useEndpointCoords)
                {
                    OverrideSnapshotPosition(
                        snapshot,
                        endpointLat,
                        endpointLon,
                        endpointAlt,
                        -1,
                        logContext ?? rec.VesselName);
                }

                ApplySurfaceOrbitToSnapshot(snapshot, body, logContext);
                ParsekLog.Warn("Spawner",
                    $"Spawn validation repaired snapshot for {logContext} " +
                    $"using {(useEndpointCoords ? "endpoint" : "snapshot")} surface coordinates on body '{repairBodyName}'");
                return true;
            }

            if (TryBuildRecordedTerminalOrbitForSpawn(rec, body, currentUT, out Orbit endpointOrbit))
            {
                ReplaceSnapshotOrbitNode(snapshot, endpointOrbit, body);

                Vector3d orbitPos = endpointOrbit.getPositionAtUT(currentUT);
                if (!hasEndpointCoords && IsFinite(orbitPos))
                {
                    endpointLat = body.GetLatitude(orbitPos);
                    endpointLon = body.GetLongitude(orbitPos);
                    endpointAlt = body.GetAltitude(orbitPos);
                    hasEndpointCoords = true;
                }

                if (hasEndpointCoords)
                    OverrideSnapshotPosition(snapshot, endpointLat, endpointLon, endpointAlt, -1, logContext ?? rec.VesselName);

                if (rec.TerminalStateValue == TerminalState.Orbiting
                    || rec.TerminalStateValue == TerminalState.Docked)
                {
                    NormalizeOrbitalSpawnMetadata(snapshot, currentUT);
                }

                ParsekLog.Warn("Spawner",
                    $"Spawn validation repaired snapshot for {logContext} " +
                    $"using endpoint-aligned orbit data on body '{endpointBodyName}'");
                return true;
            }

            if (hasEndpointCoords)
            {
                OverrideSnapshotPosition(snapshot, endpointLat, endpointLon, endpointAlt, -1, logContext ?? rec.VesselName);

                if (TryResolveEndpointStateVector(rec, endpointBodyName, out Vector3d velocity))
                {
                    Orbit orbit = new Orbit();
                    Vector3d worldPos = body.GetWorldSurfacePosition(endpointLat, endpointLon, endpointAlt);
                    orbit.UpdateFromStateVectors(worldPos, velocity, body, currentUT);
                    ReplaceSnapshotOrbitNode(snapshot, orbit, body);
                    ParsekLog.Warn("Spawner",
                        $"Spawn validation repaired snapshot for {logContext} " +
                        $"using endpoint state vectors on body '{endpointBodyName}'");
                    return true;
                }
            }

            ParsekLog.Error("Spawner",
                $"Spawn validation failed for {logContext}: " +
                $"snapshot provenance is malformed and endpoint data cannot reconstruct a usable orbit/body " +
                $"(endpointBody={endpointBodyName}, endpointCoords={hasEndpointCoords}, landedLike={landedLike})");
            return false;
        }

        private static bool TryResolveEndpointStateVector(
            Recording rec,
            string endpointBodyName,
            out Vector3d velocity)
        {
            velocity = Vector3d.zero;
            if (rec?.Points == null || rec.Points.Count == 0 || string.IsNullOrEmpty(endpointBodyName))
                return false;

            TrajectoryPoint point = rec.Points[rec.Points.Count - 1];
            if (!string.Equals(point.bodyName, endpointBodyName, StringComparison.Ordinal))
                return false;

            velocity = new Vector3d(point.velocity.x, point.velocity.y, point.velocity.z);
            return IsFinite(velocity);
        }

        private static void SaveOrbitToNode(Orbit orbit, ConfigNode node, CelestialBody body)
        {
            var ic = CultureInfo.InvariantCulture;
            if (!TryResolveBodyIndex(body, out int bodyIndex))
                bodyIndex = -1;
            node.AddValue("SMA", orbit.semiMajorAxis.ToString("R", ic));
            node.AddValue("ECC", orbit.eccentricity.ToString("R", ic));
            node.AddValue("INC", orbit.inclination.ToString("R", ic));
            node.AddValue("LPE", orbit.argumentOfPeriapsis.ToString("R", ic));
            node.AddValue("LAN", orbit.LAN.ToString("R", ic));
            node.AddValue("MNA", orbit.meanAnomalyAtEpoch.ToString("R", ic));
            node.AddValue("EPH", orbit.epoch.ToString("R", ic));
            node.AddValue("REF", bodyIndex.ToString(ic));
        }

        private static void ReplaceSnapshotOrbitNode(ConfigNode snapshot, Orbit orbit, CelestialBody body)
        {
            if (snapshot == null || orbit == null || object.ReferenceEquals(body, null))
                return;

            snapshot.RemoveNode("ORBIT");
            ConfigNode orbitNode = new ConfigNode("ORBIT");
            SaveOrbitToNode(orbit, orbitNode, body);
            snapshot.AddNode(orbitNode);
        }

        private const double CanonicalSurfaceOrbitTolerance = 1e-9;

        private static bool HasCanonicalSurfaceOrbitSignature(ConfigNode snapshot)
        {
            ConfigNode orbitNode = snapshot?.GetNode("ORBIT");
            return OrbitNodeValueMatches(orbitNode, "SMA", 0.0)
                && OrbitNodeValueMatches(orbitNode, "ECC", 1.0)
                && OrbitNodeValueMatches(orbitNode, "INC", 0.0)
                && OrbitNodeValueMatches(orbitNode, "LPE", 0.0)
                && OrbitNodeValueMatches(orbitNode, "LAN", 0.0)
                && OrbitNodeValueMatches(orbitNode, "MNA", 0.0)
                && OrbitNodeValueMatches(orbitNode, "EPH", 0.0);
        }

        private static bool HasCanonicalSurfaceOrbitForBody(ConfigNode snapshot, int bodyIndex)
        {
            ConfigNode orbitNode = snapshot?.GetNode("ORBIT");
            return HasCanonicalSurfaceOrbitSignature(snapshot)
                && OrbitNodeIntValueMatches(orbitNode, "REF", bodyIndex);
        }

        private static bool OrbitNodeValueMatches(ConfigNode node, string key, double expected)
        {
            if (node == null)
                return false;

            string raw = node.GetValue(key);
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double actual)
                && Math.Abs(actual - expected) <= CanonicalSurfaceOrbitTolerance;
        }

        private static bool OrbitNodeIntValueMatches(ConfigNode node, string key, int expected)
        {
            if (node == null)
                return false;

            string raw = node.GetValue(key);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int actual)
                && actual == expected;
        }

        private static string DescribeBodyForLog(CelestialBody body)
        {
            if (object.ReferenceEquals(body, null))
                return "(unknown)";

            if (!string.IsNullOrEmpty(body.bodyName))
                return body.bodyName;

            if (!string.IsNullOrEmpty(body.name))
                return body.name;

            return "(unknown)";
        }

        internal static bool ApplySurfaceOrbitToSnapshot(
            ConfigNode snapshot,
            CelestialBody body,
            string logContext = null)
        {
            if (snapshot == null || object.ReferenceEquals(body, null))
                return false;

            if (!TryResolveBodyIndex(body, out int bodyIndex))
            {
                ParsekLog.Verbose("Spawner",
                    $"ApplySurfaceOrbitToSnapshot: unable to resolve body index for '{DescribeBodyForLog(body)}'");
                return false;
            }

            if (HasCanonicalSurfaceOrbitForBody(snapshot, bodyIndex))
                return false;

            snapshot.RemoveNode("ORBIT");
            ConfigNode orbitNode = new ConfigNode("ORBIT");
            orbitNode.AddValue("SMA", "0");
            orbitNode.AddValue("ECC", "1");
            orbitNode.AddValue("INC", "0");
            orbitNode.AddValue("LPE", "0");
            orbitNode.AddValue("LAN", "0");
            orbitNode.AddValue("MNA", "0");
            orbitNode.AddValue("EPH", "0");
            orbitNode.AddValue("REF", bodyIndex.ToString(CultureInfo.InvariantCulture));
            snapshot.AddNode(orbitNode);
            ParsekLog.Info("Spawner",
                $"ApplySurfaceOrbitToSnapshot: rewrote ORBIT to canonical surface tuple for " +
                $"{(string.IsNullOrEmpty(logContext) ? "snapshot" : logContext)} on body " +
                $"'{DescribeBodyForLog(body)}' (REF={bodyIndex})");
            return true;
        }

        private static bool TryResolveBodyIndex(CelestialBody body, out int index)
        {
            index = -1;
            if (object.ReferenceEquals(body, null))
                return false;

            ResolveBodyIndexDelegate resolver = BodyIndexResolverForTesting;
            if (resolver != null && resolver(body, out index))
                return true;

            try
            {
                if (FlightGlobals.Bodies == null)
                    return false;

                string bodyName = body.name;
                for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
                {
                    CelestialBody candidate = FlightGlobals.Bodies[i];
                    if (object.ReferenceEquals(candidate, body))
                    {
                        index = i;
                        return true;
                    }

                    if (!object.ReferenceEquals(candidate, null)
                        && !string.IsNullOrEmpty(bodyName)
                        && string.Equals(candidate.name, bodyName, StringComparison.Ordinal))
                    {
                        index = i;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!IsHeadlessBodyRegistryFailure(ex))
                    throw;
            }

            return false;
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

        /// <summary>
        /// Orbital spawns can stay on rails for a while during high warp. Normalize both the
        /// top-level packed vessel flags and the per-part atmospheric fields to stock orbital
        /// ProtoVessel conventions before Load(), so deferred spawns do not inherit ascent-era
        /// packed metadata. (#353)
        /// </summary>
        internal static void NormalizeOrbitalSpawnMetadata(ConfigNode spawnNode, double ut)
        {
            if (spawnNode == null)
                return;

            var ic = CultureInfo.InvariantCulture;
            int topLevelFixCount = 0;
            int partFixCount = 0;

            void SetNodeValue(ConfigNode node, string key, string value, ref int fixCount)
            {
                if (node == null)
                    return;

                string current = node.GetValue(key);
                if (current == value)
                    return;

                node.SetValue(key, value, true);
                fixCount++;
            }

            SetNodeValue(spawnNode, "hgt", "-1", ref topLevelFixCount);
            SetNodeValue(spawnNode, "distanceTraveled", "0", ref topLevelFixCount);
            SetNodeValue(spawnNode, "PQSMin", "0", ref topLevelFixCount);
            SetNodeValue(spawnNode, "PQSMax", "0", ref topLevelFixCount);
            SetNodeValue(spawnNode, "altDispState", "DEFAULT", ref topLevelFixCount);
            SetNodeValue(spawnNode, "skipGroundPositioning", "False", ref topLevelFixCount);
            SetNodeValue(spawnNode, "skipGroundPositioningForDroppedPart", "False", ref topLevelFixCount);
            SetNodeValue(spawnNode, "vesselSpawning", "False", ref topLevelFixCount);
            SetNodeValue(spawnNode, "lastUT", ut.ToString("R", ic), ref topLevelFixCount);

            ConfigNode[] partNodes = spawnNode.GetNodes("PART");
            for (int i = 0; i < partNodes.Length; i++)
            {
                SetNodeValue(partNodes[i], "tempExt", "0", ref partFixCount);
                SetNodeValue(partNodes[i], "tempExtUnexp", "0", ref partFixCount);
                SetNodeValue(partNodes[i], "staticPressureAtm", "0", ref partFixCount);
            }

            if (topLevelFixCount > 0 || partFixCount > 0)
            {
                ParsekLog.Verbose("Spawner",
                    $"NormalizeOrbitalSpawnMetadata: rewrote {topLevelFixCount} top-level field(s) " +
                    $"and {partFixCount} part field(s) for orbital on-rails spawn");
            }
        }

        internal static bool HasRecordedTerminalOrbit(Recording rec)
        {
            return rec != null
                && !string.IsNullOrEmpty(rec.TerminalOrbitBody)
                && rec.TerminalOrbitSemiMajorAxis > 0.0;
        }

        internal static double ComputeRecordedTerminalOrbitMeanAnomalyAtUT(
            Recording rec, double bodyGravParam, double ut)
        {
            if (rec == null
                || rec.TerminalOrbitSemiMajorAxis <= 0.0
                || bodyGravParam <= 0.0
                || double.IsNaN(ut)
                || double.IsInfinity(ut))
            {
                return rec != null ? rec.TerminalOrbitMeanAnomalyAtEpoch : 0.0;
            }

            if (Math.Abs(ut - rec.TerminalOrbitEpoch) < 1e-9)
                return rec.TerminalOrbitMeanAnomalyAtEpoch;

            return TimeJumpManager.ComputeEpochShiftedMeanAnomaly(
                rec.TerminalOrbitMeanAnomalyAtEpoch,
                rec.TerminalOrbitEpoch,
                rec.TerminalOrbitSemiMajorAxis,
                bodyGravParam,
                ut);
        }

        internal static bool TryGetPreferredRecordedOrbitSeedForSpawn(
            Recording rec,
            out double inclination,
            out double eccentricity,
            out double semiMajorAxis,
            out double lan,
            out double argumentOfPeriapsis,
            out double meanAnomalyAtEpoch,
            out double epoch,
            out string bodyName)
        {
            inclination = 0.0;
            eccentricity = 0.0;
            semiMajorAxis = 0.0;
            lan = 0.0;
            argumentOfPeriapsis = 0.0;
            meanAnomalyAtEpoch = 0.0;
            epoch = 0.0;
            bodyName = null;

            if (rec == null)
                return false;

            if (rec.OrbitSegments != null)
            {
                for (int i = rec.OrbitSegments.Count - 1; i >= 0; i--)
                {
                    OrbitSegment seg = rec.OrbitSegments[i];
                    if (string.IsNullOrEmpty(seg.bodyName) || seg.semiMajorAxis <= 0.0)
                        continue;

                    inclination = seg.inclination;
                    eccentricity = seg.eccentricity;
                    semiMajorAxis = seg.semiMajorAxis;
                    lan = seg.longitudeOfAscendingNode;
                    argumentOfPeriapsis = seg.argumentOfPeriapsis;
                    meanAnomalyAtEpoch = seg.meanAnomalyAtEpoch;
                    epoch = seg.epoch;
                    bodyName = seg.bodyName;
                    return true;
                }
            }

            if (!HasRecordedTerminalOrbit(rec))
                return false;

            inclination = rec.TerminalOrbitInclination;
            eccentricity = rec.TerminalOrbitEccentricity;
            semiMajorAxis = rec.TerminalOrbitSemiMajorAxis;
            lan = rec.TerminalOrbitLAN;
            argumentOfPeriapsis = rec.TerminalOrbitArgumentOfPeriapsis;
            meanAnomalyAtEpoch = rec.TerminalOrbitMeanAnomalyAtEpoch;
            epoch = rec.TerminalOrbitEpoch;
            bodyName = rec.TerminalOrbitBody;
            return true;
        }

        internal static bool TryGetEndpointAlignedRecordedOrbitSeedForSpawn(
            Recording rec,
            out double inclination,
            out double eccentricity,
            out double semiMajorAxis,
            out double lan,
            out double argumentOfPeriapsis,
            out double meanAnomalyAtEpoch,
            out double epoch,
            out string bodyName)
        {
            inclination = 0.0;
            eccentricity = 0.0;
            semiMajorAxis = 0.0;
            lan = 0.0;
            argumentOfPeriapsis = 0.0;
            meanAnomalyAtEpoch = 0.0;
            epoch = 0.0;
            bodyName = null;

            if (RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed(
                rec,
                out inclination,
                out eccentricity,
                out semiMajorAxis,
                out lan,
                out argumentOfPeriapsis,
                out meanAnomalyAtEpoch,
                out epoch,
                out bodyName))
            {
                return true;
            }

            return false;
        }

        internal static bool ShouldUseRecordedTerminalOrbitSpawnState(Recording rec, bool isEva)
        {
            return !isEva
                && rec != null
                && rec.TerminalStateValue == TerminalState.Orbiting
                && HasRecordedTerminalOrbit(rec);
        }

        internal static bool ShouldRouteThroughSpawnAtPosition(Recording rec)
        {
            if (rec == null)
                return false;

            bool isEva = !string.IsNullOrEmpty(rec.EvaCrewName);
            bool isBreakupContinuous = rec.ChildBranchPointId != null && rec.TerminalStateValue.HasValue;

            return rec.TerminalStateValue == TerminalState.Orbiting
                || rec.TerminalStateValue == TerminalState.Docked
                || isEva
                || isBreakupContinuous;
        }

        internal static bool TryBuildRecordedTerminalOrbitForSpawn(
            Recording rec, CelestialBody body, double ut, out Orbit orbit)
        {
            orbit = null;

            if (body == null)
                return false;

            if (!TryGetEndpointAlignedRecordedOrbitSeedForSpawn(
                rec,
                out double inclination,
                out double eccentricity,
                out double semiMajorAxis,
                out double lan,
                out double argumentOfPeriapsis,
                out double meanAnomalyAtEpoch,
                out double epoch,
                out string orbitBodyName))
            {
                string resolvedEndpointBody;
                string endpointBody = RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out resolvedEndpointBody)
                    ? resolvedEndpointBody
                    : "(none)";
                ParsekLog.Warn("Spawner",
                    $"TryBuildRecordedTerminalOrbitForSpawn: no endpoint-aligned orbit seed " +
                    $"(endpointBody={endpointBody}, terminalBody={rec?.TerminalOrbitBody ?? "(null)"}, " +
                    $"orbitSegments={rec?.OrbitSegments?.Count ?? 0})");
                return false;
            }

            if (!string.Equals(body.name, orbitBodyName, StringComparison.Ordinal))
            {
                ParsekLog.Warn("Spawner",
                    $"TryBuildRecordedTerminalOrbitForSpawn: body mismatch " +
                    $"(body={body.name}, terminalBody={orbitBodyName})");
                return false;
            }

            try
            {
                double meanAnomalyAtSpawnUT;
                if (semiMajorAxis <= 0.0
                    || body.gravParameter <= 0.0
                    || double.IsNaN(ut)
                    || double.IsInfinity(ut)
                    || Math.Abs(ut - epoch) < 1e-9)
                {
                    meanAnomalyAtSpawnUT = meanAnomalyAtEpoch;
                }
                else
                {
                    meanAnomalyAtSpawnUT = TimeJumpManager.ComputeEpochShiftedMeanAnomaly(
                        meanAnomalyAtEpoch,
                        epoch,
                        semiMajorAxis,
                        body.gravParameter,
                        ut);
                }

                orbit = new Orbit(
                    inclination,
                    eccentricity,
                    semiMajorAxis,
                    lan,
                    argumentOfPeriapsis,
                    meanAnomalyAtSpawnUT,
                    ut,
                    body);
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Spawner",
                    $"TryBuildRecordedTerminalOrbitForSpawn failed: {ex.Message}");
                return false;
            }
        }

        internal static bool TryResolveRecordedTerminalOrbitSpawnState(
            Recording rec, CelestialBody body, double ut,
            out double lat, out double lon, out double alt, out Vector3d velocity, out Orbit orbit)
        {
            lat = 0.0;
            lon = 0.0;
            alt = 0.0;
            velocity = Vector3d.zero;
            orbit = null;

            if (!TryBuildRecordedTerminalOrbitForSpawn(rec, body, ut, out orbit))
                return false;

            try
            {
                Vector3d worldPos = orbit.getPositionAtUT(ut);
                velocity = orbit.getOrbitalVelocityAtUT(ut);
                if (!IsFinite(worldPos) || !IsFinite(velocity))
                {
                    ParsekLog.Warn("Spawner",
                        "TryResolveRecordedTerminalOrbitSpawnState: propagated orbital state was non-finite");
                    return false;
                }

                lat = body.GetLatitude(worldPos);
                lon = body.GetLongitude(worldPos);
                alt = body.GetAltitude(worldPos);
                if (double.IsNaN(lat) || double.IsNaN(lon) || double.IsNaN(alt)
                    || double.IsInfinity(lat) || double.IsInfinity(lon) || double.IsInfinity(alt))
                {
                    ParsekLog.Warn("Spawner",
                        "TryResolveRecordedTerminalOrbitSpawnState: propagated orbital position was non-finite");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Spawner",
                    $"TryResolveRecordedTerminalOrbitSpawnState failed: {ex.Message}");
                return false;
            }
        }

        private static string DescribeSpawnValidationContext(Recording rec, string logContext)
        {
            string vesselName = rec != null ? rec.VesselName : null;
            if (string.IsNullOrEmpty(logContext))
                return string.IsNullOrEmpty(vesselName) ? "(unknown)" : vesselName;
            if (string.IsNullOrEmpty(vesselName) || string.Equals(logContext, vesselName, StringComparison.Ordinal))
                return logContext;
            return $"{logContext} ({vesselName})";
        }

        private static bool IsFinite(Vector3d value)
        {
            return !(double.IsNaN(value.x) || double.IsNaN(value.y) || double.IsNaN(value.z)
                || double.IsInfinity(value.x) || double.IsInfinity(value.y) || double.IsInfinity(value.z));
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
