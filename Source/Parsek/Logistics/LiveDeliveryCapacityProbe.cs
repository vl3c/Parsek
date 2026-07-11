using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Live <see cref="IDeliveryCapacityProbe"/> over the destination vessel.
    /// Picks the loaded or unloaded probe automatically based on
    /// <c>vessel.loaded</c> / <c>vessel.packed</c>. Tracks
    /// <c>consumedSlots</c> (module-qualified <see cref="InventorySlotAddress"/>
    /// keys) across calls so the planner's per-item inventory walk can ask for
    /// "next empty slot" across ALL inventory modules without double-assigning
    /// a slot it already handed out.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="RouteOrchestrator"/> as a file-scope class so
    /// the 1500+ LOC orchestrator file can be split along its three natural
    /// seams (env, capacity probe, writers). The capacity gate
    /// (<see cref="RouteOrchestrator.ShouldDeliverToResource"/>) must stay
    /// symmetric with <see cref="LiveDeliveryWriters"/> — both call into the
    /// same policy point so probe-reported free capacity matches what the
    /// writer is willing to fill.
    /// </remarks>
    internal sealed class LiveDeliveryCapacityProbe : IDeliveryCapacityProbe
    {
        private const string Tag = RouteOrchestrator.Tag;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private readonly Vessel vessel;
        private readonly HashSet<InventorySlotAddress> consumedSlots = new HashSet<InventorySlotAddress>();
        // Set when a full walk found no empty slot. Within one probe's
        // lifetime (one delivery) occupancy never decreases — consumedSlots
        // only grows and nothing frees destination slots mid-apply — so once
        // exhausted every later manifest item can short-circuit instead of
        // re-scanning the whole vessel (and re-logging the same summary).
        private bool exhausted;
        // Injected by the orchestrator (ApplyDelivery) — captured once per
        // delivery and passed into BOTH the probe and the writer so the
        // free-capacity calculation and the resource-mutation path read
        // from the SAME loaded/unloaded branch. Re-evaluating
        // <c>vessel.loaded && !vessel.packed</c> per-call would diverge
        // if the destination vessel transitions packed state mid-tick (KSP
        // synchronously transitions on warp boundaries, focus changes, scene
        // events): the probe could report loaded-path free capacity while
        // the writer mutates the unloaded-path snapshot, causing under-fill
        // or writes into a snapshot about to be re-initialized.
        internal readonly bool isLoaded;

        internal LiveDeliveryCapacityProbe(Vessel vessel, bool isLoaded)
        {
            this.vessel = vessel;
            this.isLoaded = isLoaded;
        }

        public double ProbeResourceFreeCapacity(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName) || vessel == null) return 0.0;
            try
            {
                return isLoaded
                    ? ProbeLoadedResourceFree(resourceName)
                    : ProbeUnloadedResourceFree(resourceName);
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"ProbeResourceFreeCapacity({resourceName}) threw {ex.GetType().Name}: {ex.Message}; returning 0");
                return 0.0;
            }
        }

        public InventorySlotAddress ProbeFirstEmptyInventorySlot()
        {
            if (vessel == null || exhausted) return InventorySlotAddress.None;
            try
            {
                InventorySlotAddress result = isLoaded ? ProbeLoadedFirstEmpty() : ProbeUnloadedFirstEmpty();
                if (!result.IsValid) exhausted = true;
                return result;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"ProbeFirstEmptyInventorySlot threw {ex.GetType().Name}: {ex.Message}; returning None");
                return InventorySlotAddress.None;
            }
        }

        public void ConsumeInventorySlot(InventorySlotAddress address)
        {
            if (!address.IsValid) return;
            consumedSlots.Add(address);
        }

        private double ProbeLoadedResourceFree(string resourceName)
        {
            if (vessel.parts == null) return 0.0;
            double total = 0.0;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Resources == null) continue;
                PartResource pr = p.Resources.Get(resourceName);
                if (pr == null) continue;
                // Capacity must match what the writer will actually fill —
                // closed tanks and NO_FLOW resources are non-deliverable.
                ResourceFlowMode mode = pr.info != null ? pr.info.resourceFlowMode : ResourceFlowMode.ALL_VESSEL;
                if (!RouteOrchestrator.ShouldDeliverToResource(pr.flowState, mode)) continue;
                double free = pr.maxAmount - pr.amount;
                if (free > 0.0) total += free;
            }
            return total;
        }

        private double ProbeUnloadedResourceFree(string resourceName)
        {
            ProtoVessel pv = vessel.protoVessel;
            if (pv == null || pv.protoPartSnapshots == null) return 0.0;
            double total = 0.0;

            // NO_FLOW is a per-resource definition — look it up once and
            // either return 0 immediately or reuse the mode in the loop.
            ResourceFlowMode mode = RouteOrchestrator.LookupResourceFlowMode(resourceName);

            for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                if (pps == null || pps.resources == null) continue;
                for (int j = 0; j < pps.resources.Count; j++)
                {
                    ProtoPartResourceSnapshot prs = pps.resources[j];
                    if (prs == null) continue;
                    if (!string.Equals(prs.resourceName, resourceName, StringComparison.Ordinal)) continue;
                    // Mirror the writer-side gate so probe capacity and
                    // actual transferable stay symmetric.
                    if (!RouteOrchestrator.ShouldDeliverToResource(prs.flowState, mode)) continue;
                    double free = prs.maxAmount - prs.amount;
                    if (free > 0.0) total += free;
                }
            }
            return total;
        }

        private InventorySlotAddress ProbeLoadedFirstEmpty()
        {
            if (vessel.parts == null) return InventorySlotAddress.None;
            int modulesScanned = 0;
            int slotsOccupied = 0;
            int slotsConsumed = 0;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Modules == null) continue;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    if (!(p.Modules[m] is ModuleInventoryPart module)) continue;
                    modulesScanned++;
                    // Walk slot indices [0, InventorySlots) in order so the
                    // result is deterministic (vessel part order, then module
                    // order within the part, then ascending slot index); skip
                    // anything the planner has already claimed this pass and
                    // keep walking into LATER modules when this one is full.
                    for (int s = 0; s < module.InventorySlots; s++)
                    {
                        var address = new InventorySlotAddress(i, m, s);
                        if (consumedSlots.Contains(address)) { slotsConsumed++; continue; }
                        if (module.storedParts != null && module.storedParts.ContainsKey(s)) { slotsOccupied++; continue; }
                        return address;
                    }
                }
            }
            ParsekLog.Verbose(Tag,
                $"ProbeLoadedFirstEmpty: no empty slot on dest={vessel.vesselName ?? "<none>"} " +
                $"modulesScanned={modulesScanned.ToString(IC)} slotsOccupied={slotsOccupied.ToString(IC)} " +
                $"slotsConsumed={slotsConsumed.ToString(IC)}");
            return InventorySlotAddress.None;
        }

        private InventorySlotAddress ProbeUnloadedFirstEmpty()
        {
            ProtoVessel pv = vessel.protoVessel;
            if (pv == null || pv.protoPartSnapshots == null) return InventorySlotAddress.None;
            int modulesScanned = 0;
            int slotsOccupied = 0;
            int slotsConsumed = 0;
            for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                if (pps == null || pps.modules == null) continue;
                for (int m = 0; m < pps.modules.Count; m++)
                {
                    ProtoPartModuleSnapshot mod = pps.modules[m];
                    if (mod == null || mod.moduleName != "ModuleInventoryPart") continue;
                    ConfigNode mv = mod.moduleValues;
                    if (mv == null) continue;
                    modulesScanned++;
                    // Only pay the prefab lookup when the proto module does
                    // NOT carry a usable InventorySlots value (the helper
                    // prefers the persisted value, so the fallback would be
                    // dead weight otherwise).
                    int slotCountFallback = TryGetPersistedSlotCount(mv, out _)
                        ? 0
                        : ResolveUnloadedSlotCountFallback(pps, m);
                    int slot = FindFirstEmptySlotInUnloadedModule(
                        mv, slotCountFallback, i, m,
                        consumedSlots, out int occupied, out int consumed);
                    slotsOccupied += occupied;
                    slotsConsumed += consumed;
                    if (slot >= 0) return new InventorySlotAddress(i, m, slot);
                }
            }
            ParsekLog.Verbose(Tag,
                $"ProbeUnloadedFirstEmpty: no empty slot on dest={vessel.vesselName ?? "<none>"} " +
                $"modulesScanned={modulesScanned.ToString(IC)} slotsOccupied={slotsOccupied.ToString(IC)} " +
                $"slotsConsumed={slotsConsumed.ToString(IC)}");
            return InventorySlotAddress.None;
        }

        /// <summary>
        /// Slot count to assume for an unloaded proto inventory module whose
        /// <c>moduleValues</c> does not carry <c>InventorySlots</c> (KSPField,
        /// not isPersistant, so stock never persists it). The real count comes
        /// from the part PREFAB's module at the same index — assuming the
        /// stock default of 9 hands out phantom slot indices on smaller
        /// containers (e.g. stock 3-slot SEQ containers), which the writer
        /// would persist as UI-inaccessible stores. Falls back to 9 only when
        /// the prefab cannot be resolved.
        /// </summary>
        private static int ResolveUnloadedSlotCountFallback(ProtoPartSnapshot pps, int moduleIndex)
        {
            const int stockDefault = 9;
            try
            {
                AvailablePart ap = pps.partInfo ?? PartLoader.getPartInfoByName(pps.partName);
                Part prefab = ap != null ? ap.partPrefab : null;
                if (prefab == null || prefab.Modules == null) return stockDefault;
                // Proto module order mirrors the prefab module order, so the
                // same index is the right module; fall back to the first
                // inventory module on the prefab if the indices ever skew.
                if (moduleIndex < prefab.Modules.Count
                    && prefab.Modules[moduleIndex] is ModuleInventoryPart atIndex)
                {
                    return atIndex.InventorySlots;
                }
                for (int j = 0; j < prefab.Modules.Count; j++)
                {
                    if (prefab.Modules[j] is ModuleInventoryPart firstInventory)
                        return firstInventory.InventorySlots;
                }
                return stockDefault;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"ResolveUnloadedSlotCountFallback(part={pps?.partName ?? "<none>"}) threw " +
                    $"{ex.GetType().Name}: {ex.Message}; assuming {stockDefault.ToString(IC)}");
                return stockDefault;
            }
        }

        /// <summary>
        /// Reads the module's persisted <c>InventorySlots</c> value. True only
        /// when the value is present, parseable, and non-negative — a
        /// present-but-unparseable value (mod/MM garbage) must NOT zero the
        /// count via TryParse's out-param and report the module falsely full.
        /// Single parse point shared by the scan helper and its caller so the
        /// "is the fallback needed" decision cannot drift from the scan.
        /// </summary>
        internal static bool TryGetPersistedSlotCount(ConfigNode moduleValues, out int slotCount)
        {
            slotCount = 0;
            if (moduleValues == null) return false;
            string slotsStr = moduleValues.GetValue("InventorySlots");
            return !string.IsNullOrEmpty(slotsStr)
                && int.TryParse(slotsStr, System.Globalization.NumberStyles.Integer, IC, out slotCount)
                && slotCount >= 0;
        }

        /// <summary>
        /// First empty slot index in one proto ModuleInventoryPart module's
        /// <paramref name="moduleValues"/>, or -1 when the module is full.
        /// A slot is empty when it is neither listed in the module's persisted
        /// STOREDPARTS children nor already consumed by the planner at
        /// (<paramref name="partIndex"/>, <paramref name="moduleIndex"/>).
        /// The slot count is the module's persisted <c>InventorySlots</c> when
        /// present and parseable, else <paramref name="slotCountFallback"/>
        /// (the prefab-resolved count from
        /// <see cref="ResolveUnloadedSlotCountFallback"/>). The out counters
        /// are only complete when the module is full (-1 return); the found
        /// path returns early. Pure ConfigNode + set logic, internal for
        /// direct unit testing of the multi-module walk (a live ProtoVessel
        /// cannot be built headlessly).
        /// </summary>
        internal static int FindFirstEmptySlotInUnloadedModule(
            ConfigNode moduleValues,
            int slotCountFallback,
            int partIndex,
            int moduleIndex,
            HashSet<InventorySlotAddress> consumedSlots,
            out int occupiedCount,
            out int consumedCount)
        {
            occupiedCount = 0;
            consumedCount = 0;
            if (moduleValues == null) return -1;

            int slotCount = TryGetPersistedSlotCount(moduleValues, out int persistedSlots)
                ? persistedSlots
                : slotCountFallback;

            // Build occupied set from existing STOREDPART children.
            HashSet<int> occupied = new HashSet<int>();
            ConfigNode storedParts = moduleValues.GetNode("STOREDPARTS");
            if (storedParts != null)
            {
                ConfigNode[] sps = storedParts.GetNodes("STOREDPART");
                for (int s = 0; s < sps.Length; s++)
                {
                    string idxStr = sps[s].GetValue("slotIndex");
                    if (!string.IsNullOrEmpty(idxStr)
                        && int.TryParse(idxStr, System.Globalization.NumberStyles.Integer, IC, out int idx))
                    {
                        occupied.Add(idx);
                    }
                }
            }

            for (int s = 0; s < slotCount; s++)
            {
                if (consumedSlots != null
                    && consumedSlots.Contains(new InventorySlotAddress(partIndex, moduleIndex, s)))
                {
                    consumedCount++;
                    continue;
                }
                if (occupied.Contains(s)) { occupiedCount++; continue; }
                return s;
            }
            return -1;
        }
    }
}
