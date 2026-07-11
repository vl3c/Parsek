using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Production apply-writer bundle used by <see cref="RouteOrchestrator.ApplyDelivery"/>.
    /// Owns the per-resource actual-transferred totals and the inventory
    /// success counter so the <see cref="ApplyDeliveryContext"/> delegates
    /// stay zero-allocation closures around this single object. Created fresh
    /// per delivery.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="RouteOrchestrator"/> as a file-scope class so
    /// the 1500+ LOC orchestrator file can be split along its three natural
    /// seams (env, capacity probe, writers). All KSP-state mutation funnels
    /// through this class; the policy gates (<see cref="RouteOrchestrator.ShouldDeliverToResource"/>,
    /// <see cref="RouteOrchestrator.LookupResourceFlowMode"/>) are reused
    /// across-file via internal static surface.
    /// </remarks>
    internal sealed class LiveDeliveryWriters
    {
        private const string Tag = RouteOrchestrator.Tag;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private readonly Route route;
        private readonly Vessel vessel;
        private readonly DeliveryPlan plan;
        // Injected by the orchestrator (ApplyDelivery) — captured once per
        // delivery and shared with <see cref="LiveDeliveryCapacityProbe"/> so
        // the writer mutates the SAME branch (loaded vs unloaded) that the
        // probe reported free capacity against. Re-evaluating
        // <c>vessel.loaded && !vessel.packed</c> per-call would diverge if
        // the destination transitions packed state mid-tick.
        internal readonly bool isLoaded;
        private readonly Dictionary<string, double> actualPerResource;
        private int inventorySuccessCount;

        internal LiveDeliveryWriters(Route route, Vessel vessel, DeliveryPlan plan, bool isLoaded)
        {
            this.route = route;
            this.vessel = vessel;
            this.plan = plan;
            this.isLoaded = isLoaded;
            this.actualPerResource = new Dictionary<string, double>(
                plan.Resources?.Count ?? 0, StringComparer.Ordinal);
            this.inventorySuccessCount = 0;
        }

        internal void WriteResource(string resourceName, double amount)
        {
            if (string.IsNullOrEmpty(resourceName) || amount <= 0.0)
                return;

            // Snapshot the destination tank pool BEFORE the write so the delivery
            // log can show what the tanks held before vs after, plus capacity.
            // Read over the SAME deliverable-tank set the writer mutates (same
            // loaded/unloaded branch + flow-state / NO_FLOW gate), so tankAfter -
            // tankBefore equals the written amount and the numbers are coherent.
            double tankBefore, capacity;
            ReadResourceTotals(resourceName, out tankBefore, out capacity);

            double actual = 0.0;
            try
            {
                // Use the orchestrator-captured isLoaded so probe/writer agree
                // on which branch (loaded vs unloaded) to mutate. See class
                // doc on <see cref="isLoaded"/> for why a per-call evaluation
                // here would race the probe's snapshot.
                if (isLoaded)
                {
                    actual = WriteResourceLoaded(resourceName, amount);
                }
                else
                {
                    actual = WriteResourceUnloaded(resourceName, amount);
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"WriteResource({resourceName}, {amount.ToString("R", IC)}) threw {ex.GetType().Name}: {ex.Message}");
                actual = 0.0;
            }

            if (actual > 0.0)
            {
                if (actualPerResource.TryGetValue(resourceName, out double existing))
                    actualPerResource[resourceName] = existing + actual;
                else
                    actualPerResource[resourceName] = actual;
            }

            // Re-read the same deliverable-tank pool AFTER the write so the log
            // reports an independently-measured after-state (rather than just
            // before+written), which also surfaces any divergence. Capacity does
            // not change across a write, so reuse the before-read's value.
            double tankAfter;
            ReadResourceTotals(resourceName, out tankAfter, out _);

            // Delivery verification (playtest follow-up): the actual resource write
            // into the destination tank was previously silent (only exceptions
            // logged), so a delivery cycle could not be confirmed to have moved fuel.
            // Log requested-vs-written plus the tank pool before/after and its
            // capacity. Bounded (one resource per route per cycle), so Info is
            // appropriate. written==0 means the tank was full (tankBefore==capacity),
            // NO_FLOW, or the resource is absent on the destination (capacity==0).
            ParsekLog.Info(Tag,
                $"Delivery write: route={route?.Id ?? "<none>"} dest={vessel?.vesselName ?? "<none>"} " +
                $"pid={(vessel != null ? vessel.persistentId : 0u).ToString(IC)} " +
                $"resource={resourceName} requested={amount.ToString("R", IC)} " +
                $"written={actual.ToString("R", IC)} " +
                $"tankBefore={tankBefore.ToString("R", IC)} tankAfter={tankAfter.ToString("R", IC)} " +
                $"capacity={capacity.ToString("R", IC)} path={(isLoaded ? "loaded" : "unloaded")}");
        }

        /// <summary>
        /// Sums the currently-stored amount and total capacity of
        /// <paramref name="resourceName"/> across the destination tanks the
        /// writer would deliver into: the same loaded/unloaded branch and the
        /// same flow-state / NO_FLOW gate as <see cref="WriteResourceLoaded"/> /
        /// <see cref="WriteResourceUnloaded"/>. Reading over exactly the
        /// deliverable pool keeps the "tank before / after" delivery log coherent
        /// (after - before equals the written amount) and read-only.
        /// </summary>
        private void ReadResourceTotals(string resourceName, out double stored, out double capacity)
        {
            stored = 0.0;
            capacity = 0.0;
            if (string.IsNullOrEmpty(resourceName)) return;
            try
            {
                if (isLoaded)
                    ReadResourceTotalsLoaded(resourceName, ref stored, ref capacity);
                else
                    ReadResourceTotalsUnloaded(resourceName, ref stored, ref capacity);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"ReadResourceTotals({resourceName}) threw {ex.GetType().Name}: {ex.Message}");
                stored = 0.0;
                capacity = 0.0;
            }
        }

        private void ReadResourceTotalsLoaded(string resourceName, ref double stored, ref double capacity)
        {
            if (vessel.parts == null) return;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Resources == null) continue;
                PartResource pr = p.Resources.Get(resourceName);
                if (pr == null) continue;
                ResourceFlowMode mode = pr.info != null ? pr.info.resourceFlowMode : ResourceFlowMode.ALL_VESSEL;
                if (!RouteOrchestrator.ShouldDeliverToResource(pr.flowState, mode)) continue;
                stored += pr.amount;
                capacity += pr.maxAmount;
            }
        }

        private void ReadResourceTotalsUnloaded(string resourceName, ref double stored, ref double capacity)
        {
            ProtoVessel pv = vessel.protoVessel;
            if (pv == null || pv.protoPartSnapshots == null) return;
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
                    if (!RouteOrchestrator.ShouldDeliverToResource(prs.flowState, mode)) continue;
                    stored += prs.amount;
                    capacity += prs.maxAmount;
                }
            }
        }

        internal double ReadActualResource(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName)) return 0.0;
            return actualPerResource.TryGetValue(resourceName, out double v) ? v : 0.0;
        }

        internal void WriteInventory(InventoryPayloadItem item, InventorySlotAddress slot)
        {
            if (item == null || !slot.IsValid) return;

            bool stored = false;
            try
            {
                // Use the orchestrator-captured isLoaded so probe/writer agree
                // on which branch (loaded vs unloaded) to mutate. See class
                // doc on <see cref="isLoaded"/> for why a per-call evaluation
                // here would race the probe's snapshot.
                if (isLoaded)
                {
                    stored = WriteInventoryLoaded(item, slot);
                }
                else
                {
                    stored = WriteInventoryUnloaded(item, slot);
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"WriteInventory(part={item.PartName}, slot={slot}) " +
                    $"threw {ex.GetType().Name}: {ex.Message}");
                stored = false;
            }

            if (stored) inventorySuccessCount++;

            // Bounded (one line per manifest item per delivery), mirrors the
            // pickup side's "Inventory remove" Info line so a cargo item's
            // pickup + delivery are both traceable in the log.
            ParsekLog.Info(Tag,
                $"Inventory store: route={route?.Id ?? "<none>"} dest={vessel?.vesselName ?? "<none>"} " +
                $"pid={(vessel != null ? vessel.persistentId : 0u).ToString(IC)} " +
                $"part={item.PartName ?? "<none>"} slot={slot} " +
                $"stored={(stored ? "1" : "0")} path={(isLoaded ? "loaded" : "unloaded")}");
        }

        internal int ReadInventoryActualCount() => inventorySuccessCount;

        /// <summary>
        /// Distributes <paramref name="amount"/> across the destination
        /// vessel's parts that hold the named resource. Walks parts in
        /// vessel order and fills each up to its <c>maxAmount</c> until the
        /// requested amount is satisfied or capacity runs out.
        /// </summary>
        private double WriteResourceLoaded(string resourceName, double amount)
        {
            double remaining = amount;
            double total = 0.0;
            if (vessel.parts == null) return 0.0;

            for (int i = 0; i < vessel.parts.Count && remaining > 0.0; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Resources == null) continue;
                PartResource pr = p.Resources.Get(resourceName);
                if (pr == null) continue;
                // Mirror the probe's flowState gate AND suppress NO_FLOW
                // resources at the seam. ShouldDeliverToResource is the
                // single policy point shared with the unloaded writer and
                // both probe paths so capacity/actual stay symmetric.
                ResourceFlowMode mode = pr.info != null ? pr.info.resourceFlowMode : ResourceFlowMode.ALL_VESSEL;
                if (!RouteOrchestrator.ShouldDeliverToResource(pr.flowState, mode)) continue;
                double free = pr.maxAmount - pr.amount;
                if (free <= 0.0) continue;
                double delta = free < remaining ? free : remaining;
                pr.amount += delta;
                remaining -= delta;
                total += delta;
            }
            return total;
        }

        /// <summary>
        /// Unloaded-vessel resource fill. Same distribution as the loaded
        /// path but writes <c>ProtoPartResourceSnapshot.amount</c>; the
        /// next time the vessel loads, the live <c>PartResource</c> values
        /// initialize from the proto snapshots so the delivered amounts
        /// become visible.
        /// </summary>
        private double WriteResourceUnloaded(string resourceName, double amount)
        {
            double remaining = amount;
            double total = 0.0;
            ProtoVessel pv = vessel.protoVessel;
            if (pv == null || pv.protoPartSnapshots == null) return 0.0;

            // NO_FLOW gate is per-resource definition, not per-tank — look
            // it up once outside the part loop so we don't hammer the
            // library for every proto-part. flowState stays per-snapshot.
            ResourceFlowMode mode = RouteOrchestrator.LookupResourceFlowMode(resourceName);

            for (int i = 0; i < pv.protoPartSnapshots.Count && remaining > 0.0; i++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                if (pps == null || pps.resources == null) continue;
                for (int j = 0; j < pps.resources.Count && remaining > 0.0; j++)
                {
                    ProtoPartResourceSnapshot prs = pps.resources[j];
                    if (prs == null) continue;
                    if (!string.Equals(prs.resourceName, resourceName, StringComparison.Ordinal)) continue;
                    // Mirror the probe + loaded-writer gate. Closed proto
                    // tanks and NO_FLOW resources never receive a write.
                    if (!RouteOrchestrator.ShouldDeliverToResource(prs.flowState, mode)) continue;
                    double free = prs.maxAmount - prs.amount;
                    if (free <= 0.0) continue;
                    double delta = free < remaining ? free : remaining;
                    prs.amount += delta;
                    remaining -= delta;
                    total += delta;
                }
            }
            return total;
        }

        /// <summary>
        /// Loaded-path inventory store. Resolves the exact
        /// <see cref="ModuleInventoryPart"/> the probe assigned (vessel part
        /// index + module index, same loaded branch), converts the
        /// STOREDPART ConfigNode payload to a <see cref="ProtoPartSnapshot"/>
        /// via the canonical KSP <c>(ConfigNode, ProtoVessel, Game)</c>
        /// constructor (see B0 finding below), and delegates to stock
        /// <c>StoreCargoPartAtSlot</c>.
        ///
        /// B0 finding: <see cref="ProtoPartSnapshot(ConfigNode, ProtoVessel, Game)"/>
        /// is the canonical stock constructor — verified against decompiled
        /// <c>Assembly-CSharp.dll</c> (StoredPart.Load line 103,
        /// ModuleInventoryPart.OnLoad line 3073). The constructor expects a
        /// PART-shaped node (i.e. the inner PART subnode of a STOREDPART),
        /// not the STOREDPART wrapper itself. Our payload is a STOREDPART
        /// ConfigNode (see <see cref="VesselSpawner.BuildInventoryPayloadItem"/>),
        /// so we extract the inner PART node before constructing.
        /// </summary>
        private bool WriteInventoryLoaded(InventoryPayloadItem item, InventorySlotAddress slot)
        {
            if (item.StoredPartSnapshot == null) return false;
            ModuleInventoryPart module = ResolveLoadedInventoryModule(slot);
            if (module == null) return false;

            ProtoPartSnapshot pps = BuildProtoPartSnapshotForDelivery(
                item.StoredPartSnapshot, vessel.protoVessel);
            if (pps == null) return false;

            return module.StoreCargoPartAtSlot(pps, slot.SlotIndex);
        }

        /// <summary>
        /// Resolves the probe-assigned (part index, module index) back to the
        /// live <see cref="ModuleInventoryPart"/> instance, on the SAME loaded
        /// branch and collections the probe walked. Returns null (with a Warn,
        /// the delivery is then reported as a failed store) when the address
        /// no longer resolves — a mid-tick part loss or module change between
        /// probe and write.
        /// </summary>
        private ModuleInventoryPart ResolveLoadedInventoryModule(InventorySlotAddress slot)
        {
            if (vessel == null || vessel.parts == null
                || slot.PartIndex >= vessel.parts.Count)
            {
                ParsekLog.Warn(Tag,
                    $"ResolveLoadedInventoryModule: part index out of range for slot={slot} " +
                    $"partCount={(vessel?.parts != null ? vessel.parts.Count : 0).ToString(IC)}");
                return null;
            }
            Part p = vessel.parts[slot.PartIndex];
            if (p == null || p.Modules == null || slot.ModuleIndex >= p.Modules.Count
                || !(p.Modules[slot.ModuleIndex] is ModuleInventoryPart module))
            {
                ParsekLog.Warn(Tag,
                    $"ResolveLoadedInventoryModule: module at slot={slot} is missing or not a " +
                    $"ModuleInventoryPart on part={p?.partInfo?.name ?? "<none>"}");
                return null;
            }
            return module;
        }

        /// <summary>
        /// Unloaded-path inventory store. Appends a deep-cloned STOREDPART
        /// ConfigNode under the probe-assigned proto
        /// <see cref="ModuleInventoryPart"/> module's persistent
        /// <c>STOREDPARTS</c> child (proto part index + module index, same
        /// unloaded branch the probe walked), matching the on-disk shape stock
        /// writes via <c>StoredPart.Save</c>. The slot index is persisted as
        /// the <c>slotIndex</c> value so stock's <c>OnLoad</c> (legacy and
        /// modern paths) restores the slot position when the vessel next loads.
        /// </summary>
        private bool WriteInventoryUnloaded(InventoryPayloadItem item, InventorySlotAddress slot)
        {
            if (item.StoredPartSnapshot == null) return false;
            ProtoPartModuleSnapshot mod = ResolveUnloadedInventoryModule(slot);
            if (mod == null) return false;

            return StoreIntoUnloadedInventoryModule(mod.moduleValues, item.StoredPartSnapshot, slot.SlotIndex);
        }

        /// <summary>
        /// Unloaded twin of <see cref="ResolveLoadedInventoryModule"/>: resolves
        /// the probe-assigned (part index, module index) back to the proto
        /// ModuleInventoryPart snapshot, on the SAME unloaded branch and
        /// collections the probe walked. Returns null (with a Warn, the
        /// delivery is then reported as a failed store) when the address no
        /// longer resolves.
        /// </summary>
        private ProtoPartModuleSnapshot ResolveUnloadedInventoryModule(InventorySlotAddress slot)
        {
            ProtoVessel pv = vessel != null ? vessel.protoVessel : null;
            if (pv == null || pv.protoPartSnapshots == null
                || slot.PartIndex >= pv.protoPartSnapshots.Count)
            {
                ParsekLog.Warn(Tag,
                    $"ResolveUnloadedInventoryModule: proto part index out of range for slot={slot} " +
                    $"protoPartCount={(pv?.protoPartSnapshots != null ? pv.protoPartSnapshots.Count : 0).ToString(IC)}");
                return null;
            }

            ProtoPartSnapshot pps = pv.protoPartSnapshots[slot.PartIndex];
            if (pps == null || pps.modules == null || slot.ModuleIndex >= pps.modules.Count)
            {
                ParsekLog.Warn(Tag,
                    $"ResolveUnloadedInventoryModule: proto module index out of range for slot={slot} " +
                    $"part={pps?.partName ?? "<none>"}");
                return null;
            }
            ProtoPartModuleSnapshot mod = pps.modules[slot.ModuleIndex];
            if (mod == null || mod.moduleName != "ModuleInventoryPart" || mod.moduleValues == null)
            {
                ParsekLog.Warn(Tag,
                    $"ResolveUnloadedInventoryModule: proto module at slot={slot} is missing or not a " +
                    $"ModuleInventoryPart on part={pps.partName ?? "<none>"}");
                return null;
            }
            return mod;
        }

        /// <summary>
        /// Appends the STOREDPART payload under <paramref name="moduleValues"/>'
        /// <c>STOREDPARTS</c> child with the planner-assigned
        /// <paramref name="slotIndex"/>. Stock's StoredPart.Save writes
        /// slotIndex as a value child of the STOREDPART node; our payload comes
        /// from VesselSpawner which preserves the original slotIndex, so it is
        /// overridden here so the STOREDPART lands in the assigned slot on next
        /// OnLoad. Pure ConfigNode manipulation, internal for direct unit
        /// testing of the module-targeted store.
        /// </summary>
        internal static bool StoreIntoUnloadedInventoryModule(
            ConfigNode moduleValues, ConfigNode storedPartSnapshot, int slotIndex)
        {
            if (moduleValues == null || storedPartSnapshot == null || slotIndex < 0) return false;
            ConfigNode storedParts = moduleValues.GetNode("STOREDPARTS");
            if (storedParts == null) storedParts = moduleValues.AddNode("STOREDPARTS");

            ConfigNode storedPartCopy = storedPartSnapshot.CreateCopy();
            storedPartCopy.name = "STOREDPART";
            storedPartCopy.RemoveValues("slotIndex");
            storedPartCopy.AddValue("slotIndex", slotIndex.ToString(IC));
            storedParts.AddNode(storedPartCopy);
            return true;
        }

        /// <summary>
        /// Convert a STOREDPART ConfigNode (the Parsek-canonical payload
        /// shape) to a <see cref="ProtoPartSnapshot"/> via the stock
        /// <c>(ConfigNode, ProtoVessel, Game)</c> constructor. Stock
        /// <c>StoredPart.Load</c> reads the inner PART child node and feeds
        /// it to the same constructor with <c>(node.GetNode("PART"), null, null)</c>;
        /// we replicate that exactly here. Returns <c>null</c> when the
        /// STOREDPART payload has no inner PART node (defensive — every
        /// VesselSpawner-built payload includes one).
        /// </summary>
        internal static ProtoPartSnapshot BuildProtoPartSnapshotForDelivery(
            ConfigNode storedPartNode, ProtoVessel hostProtoVessel)
        {
            if (storedPartNode == null) return null;
            ConfigNode partNode = storedPartNode.GetNode("PART");
            if (partNode == null) return null;
            // Stock passes Game=null at StoredPart.Load:103 and at
            // ModuleInventoryPart.OnLoad:3073. We mirror that.
            return new ProtoPartSnapshot(partNode, hostProtoVessel, null);
        }
    }
}
