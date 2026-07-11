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
        // Volume/mass claimed by earlier ConsumeInventoryCapacity calls in
        // this planning pass, so a multi-item plan cannot collectively
        // overshoot the container's packedVolumeLimit / massLimit even though
        // each individual admission fit at probe time.
        private double consumedPackedVolume;
        private double consumedCargoMass;

        // The container's limits + pre-existing occupancy are invariant for
        // the whole planning pass (the probe is created fresh per delivery
        // and the writers only run after planning), so read them once and
        // memoize — the budget walk costs a full parts/proto walk plus a
        // prefab lookup per stored part.
        private bool budgetRead;
        private bool budgetValid;
        private double cachedVolumeLimit;
        private double cachedMassLimit;
        private double cachedVolumeOccupied;
        private double cachedMassOccupied;

        // Per-part-name footprint memo: probe + consume + the budget walk all
        // resolve the same prefabs repeatedly within one planning pass.
        private readonly Dictionary<string, PartFootprint> footprintByPartName =
            new Dictionary<string, PartFootprint>(StringComparer.Ordinal);

        private struct PartFootprint
        {
            public bool Storable;
            public double Volume;
            public double Mass;
        }
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

        public int ProbeInventoryStackableQuantity(InventoryPayloadItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.PartName)) return 1;
            try
            {
                ModuleCargoPart cargo = FindPrefabCargoModule(item.PartName);
                return cargo != null && cargo.stackableQuantity > 0 ? cargo.stackableQuantity : 1;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"ProbeInventoryStackableQuantity(part={item.PartName}) threw {ex.GetType().Name}: {ex.Message}; returning 1");
                return 1;
            }
        }

        public int ProbeInventoryUnitsThatFit(InventoryPayloadItem item, int requestedUnits)
        {
            if (item == null || requestedUnits <= 0 || vessel == null) return 0;
            try
            {
                // Per-unit footprint from the part prefab, mirroring stock's
                // capacity accounting (ModuleInventoryPart.UpdateCapacityValues
                // uses prefab packedVolume * quantity and GetPartMass(prefab)
                // = prefab.mass + prefab.GetResourceMass()). An unresolvable
                // prefab fails CLOSED: the loaded store would no-op on a null
                // partInfo anyway, so admitting it would desync the branches.
                if (!TryGetPerUnitFootprint(item.PartName, out double perUnitVolume, out double perUnitMass))
                {
                    ParsekLog.Verbose(Tag,
                        $"ProbeInventoryUnitsThatFit: part={item.PartName ?? "<none>"} has no resolvable " +
                        "cargo prefab (packedVolume<0 or prefab missing); admitting 0 units");
                    return 0;
                }

                if (!TryReadContainerBudget(out double volumeLimit, out double massLimit,
                        out double volumeOccupied, out double massOccupied))
                {
                    return 0;
                }

                int fit = ComputeUnitsThatFit(
                    perUnitVolume, perUnitMass,
                    volumeLimit - volumeOccupied - consumedPackedVolume,
                    massLimit - massOccupied - consumedCargoMass,
                    volumeLimit > 0.0, massLimit > 0.0,
                    requestedUnits);
                if (fit < requestedUnits)
                {
                    ParsekLog.Verbose(Tag,
                        $"ProbeInventoryUnitsThatFit: part={item.PartName} requested={requestedUnits.ToString(IC)} " +
                        $"fit={fit.ToString(IC)} perUnitVol={perUnitVolume.ToString("R", IC)} " +
                        $"perUnitMass={perUnitMass.ToString("R", IC)} volLimit={volumeLimit.ToString("R", IC)} " +
                        $"volOccupied={(volumeOccupied + consumedPackedVolume).ToString("R", IC)} " +
                        $"massLimit={massLimit.ToString("R", IC)} " +
                        $"massOccupied={(massOccupied + consumedCargoMass).ToString("R", IC)} " +
                        $"path={(isLoaded ? "loaded" : "unloaded")}");
                }
                return fit;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"ProbeInventoryUnitsThatFit(part={item.PartName ?? "<none>"}) threw {ex.GetType().Name}: {ex.Message}; returning 0");
                return 0;
            }
        }

        public void ConsumeInventoryCapacity(InventoryPayloadItem item, int units)
        {
            if (item == null || units <= 0) return;
            try
            {
                if (TryGetPerUnitFootprint(item.PartName, out double perUnitVolume, out double perUnitMass))
                {
                    consumedPackedVolume += perUnitVolume * units;
                    consumedCargoMass += perUnitMass * units;
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"ConsumeInventoryCapacity(part={item.PartName ?? "<none>"}) threw {ex.GetType().Name}: {ex.Message}; ignoring");
            }
        }

        // Tolerance on the unit-count ratio: the free budget is computed by
        // double subtraction and division, so an EXACT fit (e.g. 100 units of
        // 0.6 L into a 60 L limit -> 60/0.6 = 99.99999999999999) would floor
        // to one unit short without it. 1e-9 of a unit can never admit a
        // genuinely non-fitting unit for real part footprints.
        private const double UnitFitEpsilon = 1e-9;

        /// <summary>
        /// Pure admission core, unit-tested directly: how many of
        /// <paramref name="requestedUnits"/> fit into the remaining free
        /// volume/mass. A disabled limit (stock <c>HasPackedVolumeLimit</c> /
        /// <c>HasMassLimit</c> false, i.e. limit &lt;= 0) does not constrain.
        /// A zero per-unit footprint never constrains on that axis.
        /// </summary>
        internal static int ComputeUnitsThatFit(
            double perUnitVolume, double perUnitMass,
            double freeVolume, double freeMass,
            bool hasVolumeLimit, bool hasMassLimit,
            int requestedUnits)
        {
            if (requestedUnits <= 0) return 0;
            int fit = requestedUnits;
            if (hasVolumeLimit && perUnitVolume > 0.0)
            {
                double byVolume = Math.Floor(freeVolume / perUnitVolume + UnitFitEpsilon);
                if (byVolume < fit) fit = byVolume < 0.0 ? 0 : (int)byVolume;
            }
            if (hasMassLimit && perUnitMass > 0.0)
            {
                double byMass = Math.Floor(freeMass / perUnitMass + UnitFitEpsilon);
                if (byMass < fit) fit = byMass < 0.0 ? 0 : (int)byMass;
            }
            return fit;
        }

        /// <summary>
        /// Per-unit packed volume + mass of the item's part prefab, memoized
        /// per part name for the probe's lifetime (one planning pass). False
        /// when the prefab cannot be resolved or the part is not storable
        /// cargo (no ModuleCargoPart, or packedVolume &lt; 0 — stock's
        /// <c>HasCapacity</c> refuses those outright).
        /// </summary>
        private bool TryGetPerUnitFootprint(string partName, out double perUnitVolume, out double perUnitMass)
        {
            perUnitVolume = 0.0;
            perUnitMass = 0.0;
            if (string.IsNullOrEmpty(partName)) return false;

            if (!footprintByPartName.TryGetValue(partName, out PartFootprint footprint))
            {
                footprint = ResolvePartFootprint(partName);
                footprintByPartName[partName] = footprint;
            }
            if (!footprint.Storable) return false;
            perUnitVolume = footprint.Volume;
            perUnitMass = footprint.Mass;
            return true;
        }

        private static PartFootprint ResolvePartFootprint(string partName)
        {
            Part prefab = FindPrefabPart(partName);
            if (prefab == null) return new PartFootprint { Storable = false };
            ModuleCargoPart cargo = prefab.FindModuleImplementing<ModuleCargoPart>();
            if (cargo == null || cargo.packedVolume < 0f) return new PartFootprint { Storable = false };
            return new PartFootprint
            {
                Storable = true,
                Volume = cargo.packedVolume,
                Mass = prefab.mass + prefab.GetResourceMass(),
            };
        }

        private static Part FindPrefabPart(string partName)
        {
            if (string.IsNullOrEmpty(partName)) return null;
            AvailablePart info = PartLoader.getPartInfoByName(partName);
            return info != null ? info.partPrefab : null;
        }

        private static ModuleCargoPart FindPrefabCargoModule(string partName)
        {
            Part prefab = FindPrefabPart(partName);
            return prefab != null ? prefab.FindModuleImplementing<ModuleCargoPart>() : null;
        }

        /// <summary>
        /// Reads the destination container's volume/mass limits and current
        /// occupancy over the SAME branch (loaded/unloaded) and the SAME
        /// first-inventory-module scope as the slot probe and the writers,
        /// memoized for the probe's lifetime (one planning pass). Limits come
        /// from the container part's PREFAB ModuleInventoryPart on BOTH
        /// branches (packedVolumeLimit / massLimit are non-persistent
        /// KSPFields, so the proto module node never carries them; reading
        /// the live module on the loaded branch only would let admission
        /// diverge between branches for module-tweaked containers).
        /// Occupancy is recomputed stock-style from the stored parts (prefab
        /// per-unit footprint * quantity). Returns false — admit nothing —
        /// when no inventory module exists OR the container part's prefab
        /// cannot be resolved (limits unknown; failing open would admit
        /// unbounded cargo into a limited container).
        /// </summary>
        private bool TryReadContainerBudget(
            out double volumeLimit, out double massLimit,
            out double volumeOccupied, out double massOccupied)
        {
            if (!budgetRead)
            {
                budgetRead = true;
                budgetValid = isLoaded ? ReadContainerBudgetLoaded() : ReadContainerBudgetUnloaded();
            }
            volumeLimit = cachedVolumeLimit;
            massLimit = cachedMassLimit;
            volumeOccupied = cachedVolumeOccupied;
            massOccupied = cachedMassOccupied;
            return budgetValid;
        }

        private bool ReadContainerBudgetLoaded()
        {
            // Same module the loaded writer stores into.
            ModuleInventoryPart module = LiveDeliveryWriters.FindFirstInventoryModule(vessel);
            if (module == null || module.part == null) return false;

            string containerName = module.part.partInfo != null ? module.part.partInfo.name : null;
            if (!TryReadContainerLimits(containerName)) return false;

            if (module.storedParts != null)
            {
                for (int s = 0; s < module.InventorySlots; s++)
                {
                    if (!module.storedParts.ContainsKey(s)) continue;
                    StoredPart sp = module.storedParts[s];
                    if (sp == null || sp.quantity <= 0) continue;
                    AccumulateStoredFootprint(sp.partName, sp.quantity);
                }
            }
            return true;
        }

        private bool ReadContainerBudgetUnloaded()
        {
            ProtoVessel pv = vessel.protoVessel;
            if (pv == null || pv.protoPartSnapshots == null) return false;
            for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                if (pps == null || pps.modules == null) continue;
                for (int m = 0; m < pps.modules.Count; m++)
                {
                    ProtoPartModuleSnapshot mod = pps.modules[m];
                    if (mod == null || mod.moduleName != "ModuleInventoryPart") continue;

                    if (!TryReadContainerLimits(pps.partName)) return false;

                    ConfigNode mv = mod.moduleValues;
                    ConfigNode storedParts = mv != null ? mv.GetNode("STOREDPARTS") : null;
                    if (storedParts != null)
                    {
                        ConfigNode[] sps = storedParts.GetNodes("STOREDPART");
                        for (int s = 0; s < sps.Length; s++)
                        {
                            string storedName = sps[s].GetValue("partName");
                            // Default-preserving parse (mirrors VesselSpawner's
                            // manifest extraction): a malformed value counts as
                            // 1 stored unit, never as 0, so its footprint is
                            // not silently excluded from occupancy.
                            int quantity = 1;
                            string qtyStr = sps[s].GetValue("quantity");
                            if (!string.IsNullOrEmpty(qtyStr)
                                && int.TryParse(qtyStr, System.Globalization.NumberStyles.Integer, IC, out int parsedQty))
                            {
                                quantity = parsedQty;
                            }
                            if (quantity <= 0) continue;
                            AccumulateStoredFootprint(storedName, quantity);
                        }
                    }
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Resolves the container part's prefab ModuleInventoryPart limits
        /// into the cached budget fields. Fails CLOSED (false) when the
        /// prefab or its inventory module cannot be resolved.
        /// </summary>
        private bool TryReadContainerLimits(string containerPartName)
        {
            Part containerPrefab = FindPrefabPart(containerPartName);
            ModuleInventoryPart prefabModule = containerPrefab != null
                ? containerPrefab.FindModuleImplementing<ModuleInventoryPart>()
                : null;
            if (prefabModule == null)
            {
                ParsekLog.Verbose(Tag,
                    $"TryReadContainerLimits: container part={containerPartName ?? "<none>"} has no resolvable " +
                    $"prefab inventory module; failing closed (admit 0) path={(isLoaded ? "loaded" : "unloaded")}");
                return false;
            }
            cachedVolumeLimit = prefabModule.packedVolumeLimit;
            cachedMassLimit = prefabModule.massLimit;
            return true;
        }

        private void AccumulateStoredFootprint(string partName, int quantity)
        {
            if (TryGetPerUnitFootprint(partName, out double perUnitVolume, out double perUnitMass))
            {
                cachedVolumeOccupied += perUnitVolume * quantity;
                cachedMassOccupied += perUnitMass * quantity;
            }
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
