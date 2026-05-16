using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Live <see cref="IDeliveryCapacityProbe"/> over the destination vessel.
    /// Picks the loaded or unloaded probe automatically based on
    /// <c>vessel.loaded</c> / <c>vessel.packed</c>. Tracks
    /// <c>consumedSlots</c> across calls so the planner's per-item
    /// inventory walk can ask for "next empty slot" without re-querying
    /// the same module repeatedly.
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
        private readonly HashSet<int> consumedSlots = new HashSet<int>();
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

        public int ProbeFirstEmptyInventorySlot()
        {
            if (vessel == null) return -1;
            try
            {
                return isLoaded ? ProbeLoadedFirstEmpty() : ProbeUnloadedFirstEmpty();
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"ProbeFirstEmptyInventorySlot threw {ex.GetType().Name}: {ex.Message}; returning -1");
                return -1;
            }
        }

        public void ConsumeInventorySlot(int slotIndex)
        {
            if (slotIndex < 0) return;
            consumedSlots.Add(slotIndex);
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

        private int ProbeLoadedFirstEmpty()
        {
            if (vessel.parts == null) return -1;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Modules == null) continue;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    if (!(p.Modules[m] is ModuleInventoryPart module)) continue;
                    // Walk slot indices [0, InventorySlots) in order so the
                    // result is deterministic and matches stock's
                    // FirstEmptySlot() contract; skip anything the planner
                    // has already claimed this pass.
                    for (int s = 0; s < module.InventorySlots; s++)
                    {
                        if (consumedSlots.Contains(s)) continue;
                        if (module.storedParts != null && module.storedParts.ContainsKey(s)) continue;
                        return s;
                    }
                    return -1;
                }
            }
            return -1;
        }

        private int ProbeUnloadedFirstEmpty()
        {
            ProtoVessel pv = vessel.protoVessel;
            if (pv == null || pv.protoPartSnapshots == null) return -1;
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

                    // InventorySlots default is 9 (ModuleInventoryPart.InventorySlots).
                    // The proto module's moduleValues may not carry the
                    // value when the part hasn't been individually
                    // configured (KSPField, not isPersistant by default),
                    // so fall back to the stock default if missing.
                    int slotCount = 9;
                    string slotsStr = mv.GetValue("InventorySlots");
                    if (!string.IsNullOrEmpty(slotsStr))
                        int.TryParse(slotsStr, System.Globalization.NumberStyles.Integer, IC, out slotCount);

                    // Build occupied set from existing STOREDPART children.
                    HashSet<int> occupied = new HashSet<int>();
                    ConfigNode storedParts = mv.GetNode("STOREDPARTS");
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
                        if (consumedSlots.Contains(s)) continue;
                        if (occupied.Contains(s)) continue;
                        return s;
                    }
                    return -1;
                }
            }
            return -1;
        }
    }
}
