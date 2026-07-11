using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Production inventory-pickup probe + remove-writer bundle (M3 Phase 5,
    /// design D7): the REVERSE-direction analogue of the inventory DELIVERY
    /// path (<see cref="LiveDeliveryCapacityProbe.ProbeFirstEmptyInventorySlot"/>
    /// + <see cref="LiveDeliveryWriters.WriteInventory"/>). Where delivery
    /// finds the first EMPTY destination slot and STORES a payload, pickup
    /// LOCATES a STORED item matching an <see cref="InventoryPayloadItem.IdentityHash"/>
    /// on the SOURCE endpoint and REMOVES it - the only genuinely new mutation
    /// in M3.
    ///
    /// <para><b>Loaded path:</b> walk <see cref="ModuleInventoryPart.storedParts"/>
    /// in ascending slot index; for each occupied slot, reconstruct the
    /// STOREDPART-shaped ConfigNode (<see cref="StoredPart.Save"/> produces the
    /// exact shape <see cref="VesselSpawner.BuildInventoryPayloadItem"/> reads),
    /// compute <see cref="VesselSpawner.ComputeInventoryPayloadIdentityHash"/>,
    /// and match. The match is REMOVED via stock
    /// <see cref="ModuleInventoryPart.ClearPartAtSlot"/> (the inverse of the
    /// delivery store).</para>
    ///
    /// <para><b>Unloaded path:</b> walk the proto
    /// <c>ModuleInventoryPart.STOREDPARTS</c> child nodes in ascending
    /// <c>slotIndex</c>, compute the identity hash of each, and REMOVE the
    /// matching STOREDPART node from the proto module (the inverse of
    /// <see cref="LiveDeliveryWriters.WriteInventoryUnloaded"/>'s add).</para>
    ///
    /// <para><b>Deterministic partial-match (design D7):</b> multiple stored
    /// parts of one IdentityHash with a partial load take the LOWEST slot index
    /// first, so replay across save/reload picks the same physical item. The
    /// transport CREDIT is BOOKKEEPING ONLY (the transport never materializes,
    /// 19.2.3) - this writer removes from the SOURCE only; no physical store on
    /// the transport. Revert-safety = the rewind quicksave (same mechanism as
    /// the M1 resource debit).</para>
    ///
    /// <para>Created fresh per pickup; the loaded gate is captured ONCE per
    /// vessel by the caller (design D5 per-vessel capture) and threaded in so
    /// the probe-match and the remove read/write the SAME loaded/unloaded
    /// branch.</para>
    /// </summary>
    internal sealed class LiveInventoryPickupWriter
    {
        private const string Tag = RouteOrchestrator.Tag;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private readonly Vessel vessel;
        // Injected by the caller - captured once per pickup and shared between
        // the probe (find slot by identity) and the remove (clear that slot) so
        // both read/write the SAME loaded/unloaded branch. Re-evaluating
        // vessel.loaded && !vessel.packed per-call would diverge if the source
        // transitions packed state mid-tick (same rationale as
        // LiveDeliveryWriters.isLoaded).
        internal readonly bool isLoaded;
        private int removedCount;

        internal LiveInventoryPickupWriter(Vessel vessel, bool isLoaded)
        {
            this.vessel = vessel;
            this.isLoaded = isLoaded;
            this.removedCount = 0;
        }

        internal int RemovedCount => removedCount;

        /// <summary>
        /// Count of stored parts on the source endpoint whose canonical
        /// identity hash equals <paramref name="identityHash"/>, summed across
        /// every inventory module (loaded slots OR unloaded proto STOREDPART
        /// nodes per the captured gate). The presence query the M1 origin
        /// inventory gate (<see cref="RouteOriginCargoCheck.HasRequiredInventory"/>)
        /// reads to decide whether the origin currently holds the witnessed
        /// payload, symmetric with what <see cref="RemoveOne"/> can remove.
        /// </summary>
        internal int CountStored(string identityHash)
        {
            if (string.IsNullOrEmpty(identityHash))
                return 0;
            try
            {
                return isLoaded
                    ? CountStoredLoaded(identityHash)
                    : CountStoredUnloaded(identityHash);
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"CountStored(hash={ShortHash(identityHash)}) threw {ex.GetType().Name}: {ex.Message}; returning 0");
                return 0;
            }
        }

        private int CountStoredLoaded(string identityHash)
        {
            if (vessel?.parts == null)
                return 0;
            int total = 0;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p?.Modules == null)
                    continue;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    if (!(p.Modules[m] is ModuleInventoryPart module) || module.storedParts == null)
                        continue;
                    for (int s = 0; s < module.InventorySlots; s++)
                    {
                        if (!module.storedParts.ContainsKey(s))
                            continue;
                        StoredPart sp = module.storedParts[s];
                        if (sp == null || sp.snapshot == null || sp.quantity <= 0)
                            continue;
                        if (string.Equals(ComputeLoadedStoredPartHash(sp), identityHash, StringComparison.Ordinal))
                            total += sp.quantity;
                    }
                }
            }
            return total;
        }

        private int CountStoredUnloaded(string identityHash)
        {
            ProtoVessel pv = vessel?.protoVessel;
            if (pv?.protoPartSnapshots == null)
                return 0;
            int total = 0;
            for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                if (pps?.modules == null)
                    continue;
                for (int m = 0; m < pps.modules.Count; m++)
                {
                    ProtoPartModuleSnapshot mod = pps.modules[m];
                    if (mod == null || mod.moduleName != "ModuleInventoryPart")
                        continue;
                    ConfigNode storedParts = mod.moduleValues?.GetNode("STOREDPARTS");
                    if (storedParts == null)
                        continue;
                    ConfigNode[] nodes = storedParts.GetNodes("STOREDPART");
                    for (int s = 0; s < nodes.Length; s++)
                    {
                        ConfigNode node = nodes[s];
                        if (node == null)
                            continue;
                        ConfigNode copy = node.CreateCopy();
                        copy.name = "STOREDPART";
                        if (!string.Equals(
                                VesselSpawner.ComputeInventoryPayloadIdentityHash(copy),
                                identityHash, StringComparison.Ordinal))
                            continue;
                        int qty = 1;
                        string qtyStr = node.GetValue("quantity");
                        if (!string.IsNullOrEmpty(qtyStr))
                            int.TryParse(qtyStr, NumberStyles.Integer, IC, out qty);
                        total += qty > 0 ? qty : 0;
                    }
                }
            }
            return total;
        }

        /// <summary>
        /// Count of stored parts on the source whose stock <c>partName</c>
        /// equals <paramref name="partName"/> REGARDLESS of identity hash,
        /// summed across every inventory module (loaded slots OR unloaded proto
        /// STOREDPART nodes per the captured gate). The NEAR-MISS probe behind
        /// the <c>inventory-state:</c> hold token: when the identity-hash gate
        /// (<see cref="CountStored"/>) reports zero but this reports a positive
        /// count, the origin physically holds the part and only its STATE
        /// (charge, fuel, module contents) differs from the recorded cargo -
        /// a legibility distinction only, never an admission relaxation.
        /// </summary>
        internal int CountStoredByPartName(string partName)
        {
            if (string.IsNullOrEmpty(partName))
                return 0;
            try
            {
                return isLoaded
                    ? CountStoredByPartNameLoaded(partName)
                    : CountStoredByPartNameUnloaded(partName);
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"CountStoredByPartName({partName}) threw {ex.GetType().Name}: {ex.Message}; returning 0");
                return 0;
            }
        }

        private int CountStoredByPartNameLoaded(string partName)
        {
            if (vessel?.parts == null)
                return 0;
            int total = 0;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p?.Modules == null)
                    continue;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    if (!(p.Modules[m] is ModuleInventoryPart module) || module.storedParts == null)
                        continue;
                    for (int s = 0; s < module.InventorySlots; s++)
                    {
                        if (!module.storedParts.ContainsKey(s))
                            continue;
                        StoredPart sp = module.storedParts[s];
                        if (sp == null || sp.quantity <= 0)
                            continue;
                        if (string.Equals(sp.partName, partName, StringComparison.Ordinal))
                            total += sp.quantity;
                    }
                }
            }
            return total;
        }

        private int CountStoredByPartNameUnloaded(string partName)
        {
            ProtoVessel pv = vessel?.protoVessel;
            if (pv?.protoPartSnapshots == null)
                return 0;
            int total = 0;
            for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                if (pps?.modules == null)
                    continue;
                for (int m = 0; m < pps.modules.Count; m++)
                {
                    ProtoPartModuleSnapshot mod = pps.modules[m];
                    if (mod == null || mod.moduleName != "ModuleInventoryPart")
                        continue;
                    ConfigNode storedParts = mod.moduleValues?.GetNode("STOREDPARTS");
                    if (storedParts == null)
                        continue;
                    total += CountStoredByPartNameInStoredPartsNode(storedParts, partName);
                }
            }
            return total;
        }

        /// <summary>
        /// Pure ConfigNode core of the unloaded by-part-name count: sums the
        /// <c>quantity</c> of every STOREDPART child whose <c>partName</c>
        /// value matches. Internal for direct unit testing (the live proto
        /// walk needs a Vessel and is pinned in-game).
        /// </summary>
        internal static int CountStoredByPartNameInStoredPartsNode(
            ConfigNode storedParts, string partName)
        {
            if (storedParts == null || string.IsNullOrEmpty(partName))
                return 0;
            int total = 0;
            ConfigNode[] nodes = storedParts.GetNodes("STOREDPART");
            for (int i = 0; i < nodes.Length; i++)
            {
                ConfigNode node = nodes[i];
                if (node == null)
                    continue;
                if (!string.Equals(node.GetValue("partName"), partName, StringComparison.Ordinal))
                    continue;
                int qty = 1;
                string qtyStr = node.GetValue("quantity");
                if (!string.IsNullOrEmpty(qtyStr))
                    int.TryParse(qtyStr, NumberStyles.Integer, IC, out qty);
                if (qty > 0)
                    total += qty;
            }
            return total;
        }

        /// <summary>
        /// Removes ONE stored part matching <paramref name="item"/>'s
        /// <see cref="InventoryPayloadItem.IdentityHash"/> from the source
        /// endpoint, taking the lowest-index occupied slot (deterministic
        /// partial-match, design D7). Returns true when a match was located AND
        /// removed; false when no slot matched (the source no longer holds the
        /// witnessed item - a mid-tick race, handled by the caller as a partial
        /// pickup). Each call removes a SINGLE unit; the caller loops per unit.
        /// </summary>
        internal bool RemoveOne(InventoryPayloadItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.IdentityHash))
                return false;

            bool removed = false;
            try
            {
                removed = isLoaded
                    ? RemoveOneLoaded(item.IdentityHash)
                    : RemoveOneUnloaded(item.IdentityHash);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"RemoveInventory(part={item.PartName}, hash={ShortHash(item.IdentityHash)}) " +
                    $"threw {ex.GetType().Name}: {ex.Message}");
                removed = false;
            }

            if (removed)
                removedCount++;
            return removed;
        }

        /// <summary>
        /// Loaded-path source slot match + clear. Walks
        /// <see cref="ModuleInventoryPart.storedParts"/> on EVERY inventory
        /// module in ascending slot order, hashes each occupied slot's stored
        /// part, and on the first identity match clears that slot via stock
        /// <see cref="ModuleInventoryPart.ClearPartAtSlot"/>. Lowest slot index
        /// wins (deterministic partial-match).
        /// </summary>
        private bool RemoveOneLoaded(string identityHash)
        {
            if (vessel?.parts == null)
                return false;

            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p?.Modules == null)
                    continue;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    if (!(p.Modules[m] is ModuleInventoryPart module))
                        continue;
                    if (module.storedParts == null)
                        continue;

                    int matchSlot = FindLoadedMatchSlot(module, identityHash);
                    if (matchSlot < 0)
                        continue;

                    // Remove ONE unit: a stacked slot (stackAmount > 1) decrements
                    // via UpdateStackAmountAtSlot (which sets the ABSOLUTE amount,
                    // clamped to >= 1, so it can never reach 0); a single-unit slot
                    // clears via ClearPartAtSlot. Removing the WHOLE stack with
                    // ClearPartAtSlot would over-debit a partial stacked pickup.
                    int stackAmount = module.GetStackAmountAtSlot(matchSlot);
                    bool removed;
                    if (stackAmount > 1)
                        removed = module.UpdateStackAmountAtSlot(matchSlot, stackAmount - 1);
                    else
                        removed = module.ClearPartAtSlot(matchSlot);
                    ParsekLog.Info(Tag,
                        $"Inventory remove (loaded): source={vessel?.vesselName ?? "<none>"} " +
                        $"pid={(vessel != null ? vessel.persistentId : 0u).ToString(IC)} " +
                        $"part={p.partInfo?.name ?? "<none>"} slot={matchSlot.ToString(IC)} " +
                        $"hash={ShortHash(identityHash)} stackBefore={stackAmount.ToString(IC)} " +
                        $"removed={(removed ? "1" : "0")}");
                    if (removed)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Lowest occupied slot index whose stored part hashes to
        /// <paramref name="identityHash"/> (deterministic partial-match), or -1.
        /// Reconstructs each slot's STOREDPART node via
        /// <see cref="StoredPart.Save"/> (the exact shape the recorded payload
        /// hash was computed over) and runs the canonical identity hash.
        /// </summary>
        private static int FindLoadedMatchSlot(ModuleInventoryPart module, string identityHash)
        {
            int best = -1;
            for (int s = 0; s < module.InventorySlots; s++)
            {
                if (!module.storedParts.ContainsKey(s))
                    continue;
                StoredPart sp = module.storedParts[s];
                if (sp == null || sp.snapshot == null || sp.quantity <= 0)
                    continue;

                string hash = ComputeLoadedStoredPartHash(sp);
                if (!string.Equals(hash, identityHash, StringComparison.Ordinal))
                    continue;

                if (best < 0 || s < best)
                    best = s;
            }
            return best;
        }

        /// <summary>
        /// Canonical identity hash for a LIVE <see cref="StoredPart"/>: serialize
        /// it via stock <see cref="StoredPart.Save"/> into a STOREDPART-named
        /// node (the shape <see cref="VesselSpawner.BuildInventoryPayloadItem"/>
        /// reads and the recorded payload hash was computed over) and run the
        /// canonical hash (slotIndex / quantity / transient PART fields stripped).
        /// </summary>
        internal static string ComputeLoadedStoredPartHash(StoredPart storedPart)
        {
            if (storedPart == null)
                return null;
            var node = new ConfigNode("STOREDPART");
            storedPart.Save(node);
            return VesselSpawner.ComputeInventoryPayloadIdentityHash(node);
        }

        /// <summary>
        /// Unloaded-path source slot match + remove. Walks the proto
        /// ModuleInventoryPart STOREDPARTS children in ascending slotIndex,
        /// hashes each, and removes the first STOREDPART node whose identity
        /// matches (the inverse of
        /// <see cref="LiveDeliveryWriters.WriteInventoryUnloaded"/>'s add). The
        /// node is removed via <see cref="ConfigNode.RemoveNode(ConfigNode)"/>
        /// so the slot frees on next OnLoad.
        /// </summary>
        private bool RemoveOneUnloaded(string identityHash)
        {
            ProtoVessel pv = vessel?.protoVessel;
            if (pv?.protoPartSnapshots == null)
                return false;

            for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                if (pps?.modules == null)
                    continue;
                for (int m = 0; m < pps.modules.Count; m++)
                {
                    ProtoPartModuleSnapshot mod = pps.modules[m];
                    if (mod == null || mod.moduleName != "ModuleInventoryPart")
                        continue;
                    ConfigNode mv = mod.moduleValues;
                    ConfigNode storedParts = mv?.GetNode("STOREDPARTS");
                    if (storedParts == null)
                        continue;

                    ConfigNode match = FindUnloadedMatchNode(storedParts, identityHash);
                    if (match == null)
                        continue;

                    string slotForLog = match.GetValue("slotIndex") ?? "<none>";
                    bool removed = RemoveOneUnitFromStoredPartsNode(
                        storedParts, match, out int stackBefore, out string action);
                    ParsekLog.Info(Tag,
                        $"Inventory remove (unloaded): source={vessel?.vesselName ?? "<none>"} " +
                        $"pid={(vessel != null ? vessel.persistentId : 0u).ToString(IC)} " +
                        $"part={pps.partName ?? "<none>"} " +
                        $"slot={slotForLog} " +
                        $"hash={ShortHash(identityHash)} stackBefore={stackBefore.ToString(IC)} " +
                        $"action={action} removed={(removed ? "1" : "0")}");
                    if (removed)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Removes ONE unit of <paramref name="match"/> from
        /// <paramref name="storedParts"/>: a stacked node (quantity &gt; 1)
        /// DECREMENTS the <c>quantity</c> value (removing the whole node would
        /// over-debit a partial stacked pickup); a single-unit node is REMOVED.
        /// Returns true when a unit was removed, with <paramref name="stackBefore"/>
        /// the pre-removal quantity and <paramref name="action"/> = "decremented"
        /// or "removed" for the log. Pure ConfigNode manipulation - the unloaded
        /// inverse of the delivery store, unit-testable without a Vessel.
        /// </summary>
        internal static bool RemoveOneUnitFromStoredPartsNode(
            ConfigNode storedParts, ConfigNode match, out int stackBefore, out string action)
        {
            stackBefore = 1;
            action = "removed";
            if (storedParts == null || match == null)
                return false;

            string qtyStr = match.GetValue("quantity");
            if (!string.IsNullOrEmpty(qtyStr))
                int.TryParse(qtyStr, NumberStyles.Integer, IC, out stackBefore);

            if (stackBefore > 1)
            {
                match.SetValue("quantity", (stackBefore - 1).ToString(IC), true);
                action = "decremented";
                return true;
            }

            int countBefore = storedParts.GetNodes("STOREDPART").Length;
            // ConfigNode.RemoveNode(ConfigNode) returns void; verify by re-counting
            // the STOREDPART children (object identity removal).
            storedParts.RemoveNode(match);
            int countAfter = storedParts.GetNodes("STOREDPART").Length;
            action = "removed";
            return countAfter < countBefore;
        }

        /// <summary>
        /// The lowest-slotIndex STOREDPART child node whose canonical identity
        /// hash equals <paramref name="identityHash"/> (deterministic
        /// partial-match), or null. A STOREDPART proto node is ALREADY in the
        /// shape the recorded payload hash was computed over, so it is hashed
        /// directly (a fresh copy named STOREDPART so the canonical walk's
        /// node-name check fires). Internal for direct unit testing of the
        /// deterministic lowest-slot rule (the live ClearPartAtSlot / RemoveNode
        /// removal needs a Vessel and is pinned in-game).
        /// </summary>
        internal static ConfigNode FindUnloadedMatchNode(ConfigNode storedParts, string identityHash)
        {
            ConfigNode[] nodes = storedParts.GetNodes("STOREDPART");
            ConfigNode best = null;
            int bestSlot = int.MaxValue;
            for (int i = 0; i < nodes.Length; i++)
            {
                ConfigNode node = nodes[i];
                if (node == null)
                    continue;

                ConfigNode copy = node.CreateCopy();
                copy.name = "STOREDPART";
                string hash = VesselSpawner.ComputeInventoryPayloadIdentityHash(copy);
                if (!string.Equals(hash, identityHash, StringComparison.Ordinal))
                    continue;

                int slot = int.MaxValue;
                string slotStr = node.GetValue("slotIndex");
                if (!string.IsNullOrEmpty(slotStr))
                    int.TryParse(slotStr, NumberStyles.Integer, IC, out slot);

                if (best == null || slot < bestSlot)
                {
                    best = node;
                    bestSlot = slot;
                }
            }
            return best;
        }

        private static string ShortHash(string hash)
        {
            if (string.IsNullOrEmpty(hash))
                return "<none>";
            return hash.Length > 8 ? hash.Substring(0, 8) : hash;
        }
    }
}
