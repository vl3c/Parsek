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

        // Volume/mass claimed by earlier ConsumeInventoryCapacity calls in
        // this planning pass, so a multi-item plan cannot collectively
        // overshoot the vessel's summed packedVolumeLimit / massLimit even
        // though each individual admission fit at probe time.
        private double consumedPackedVolume;
        private double consumedCargoMass;

        // The container limits + pre-existing occupancy are invariant for
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
        /// Reads the destination's volume/mass limits and current occupancy
        /// SUMMED across ALL inventory modules on the captured branch
        /// (loaded/unloaded), matching the multi-module slot probe and writers
        /// (a full first container no longer blocks delivery, so the volume
        /// budget must span every container too), memoized for the probe's
        /// lifetime (one planning pass). Limits come from each container
        /// part's PREFAB ModuleInventoryPart on BOTH branches (packedVolumeLimit
        /// / massLimit are non-persistent KSPFields, so the proto module node
        /// never carries them; reading the live module on the loaded branch
        /// only would let admission diverge between branches for module-tweaked
        /// containers). A module with a limit &lt;= 0 (stock "unlimited") makes
        /// that axis unlimited vessel-wide. Occupancy is recomputed stock-style
        /// from the stored parts (prefab per-unit footprint * quantity) across
        /// every module. Returns false — admit nothing — when no inventory
        /// module exists OR any container part's prefab cannot be resolved
        /// (limits unknown; failing open would admit unbounded cargo into a
        /// limited container).
        ///
        /// Residual imprecision (bounded, documented): the budget is at VESSEL
        /// granularity (summed limits / summed occupancy) while stock enforces
        /// packedVolumeLimit per-module. Because slot assignment and the volume
        /// gate are decoupled, a specific container could individually over- or
        /// under-fill; only the vessel TOTAL is bounded. Stock automated
        /// delivery bypasses per-slot volume enforcement anyway, so the
        /// meaningful guarantee (no gross total overshoot) holds. Per-container
        /// budgets coordinated with slot placement are a deferred refinement.
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
            if (vessel.parts == null) return false;
            bool anyModule = false;
            bool volumeUnlimited = false;
            bool massUnlimited = false;
            double volumeLimitSum = 0.0;
            double massLimitSum = 0.0;

            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Modules == null) continue;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    if (!(p.Modules[m] is ModuleInventoryPart module)) continue;
                    anyModule = true;

                    string containerName = module.part != null && module.part.partInfo != null
                        ? module.part.partInfo.name : null;
                    if (!TryResolveContainerLimits(containerName, out double vLimit, out double mLimit))
                        return false; // fail closed: a container whose limits we can't read
                    if (vLimit > 0.0) volumeLimitSum += vLimit; else volumeUnlimited = true;
                    if (mLimit > 0.0) massLimitSum += mLimit; else massUnlimited = true;

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
                }
            }
            if (!anyModule) return false;
            cachedVolumeLimit = volumeUnlimited ? 0.0 : volumeLimitSum;
            cachedMassLimit = massUnlimited ? 0.0 : massLimitSum;
            return true;
        }

        private bool ReadContainerBudgetUnloaded()
        {
            ProtoVessel pv = vessel.protoVessel;
            if (pv == null || pv.protoPartSnapshots == null) return false;
            bool anyModule = false;
            bool volumeUnlimited = false;
            bool massUnlimited = false;
            double volumeLimitSum = 0.0;
            double massLimitSum = 0.0;

            for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                if (pps == null || pps.modules == null) continue;
                for (int m = 0; m < pps.modules.Count; m++)
                {
                    ProtoPartModuleSnapshot mod = pps.modules[m];
                    if (mod == null || mod.moduleName != "ModuleInventoryPart") continue;
                    anyModule = true;

                    if (!TryResolveContainerLimits(pps.partName, out double vLimit, out double mLimit))
                        return false;
                    if (vLimit > 0.0) volumeLimitSum += vLimit; else volumeUnlimited = true;
                    if (mLimit > 0.0) massLimitSum += mLimit; else massUnlimited = true;

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
                }
            }
            if (!anyModule) return false;
            cachedVolumeLimit = volumeUnlimited ? 0.0 : volumeLimitSum;
            cachedMassLimit = massUnlimited ? 0.0 : massLimitSum;
            return true;
        }

        /// <summary>
        /// Resolves one container part's prefab ModuleInventoryPart
        /// packedVolumeLimit / massLimit. Fails (false) when the prefab or its
        /// inventory module cannot be resolved, so the caller can fail the
        /// whole budget closed (admit 0) rather than under-count the limits.
        /// </summary>
        private bool TryResolveContainerLimits(string containerPartName, out double volumeLimit, out double massLimit)
        {
            volumeLimit = 0.0;
            massLimit = 0.0;
            Part containerPrefab = FindPrefabPart(containerPartName);
            ModuleInventoryPart prefabModule = containerPrefab != null
                ? containerPrefab.FindModuleImplementing<ModuleInventoryPart>()
                : null;
            if (prefabModule == null)
            {
                ParsekLog.Verbose(Tag,
                    $"TryResolveContainerLimits: container part={containerPartName ?? "<none>"} has no resolvable " +
                    $"prefab inventory module; failing closed (admit 0) path={(isLoaded ? "loaded" : "unloaded")}");
                return false;
            }
            volumeLimit = prefabModule.packedVolumeLimit;
            massLimit = prefabModule.massLimit;
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
