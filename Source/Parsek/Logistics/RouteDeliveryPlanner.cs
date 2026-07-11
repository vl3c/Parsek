using System;
using System.Collections.Generic;

namespace Parsek.Logistics
{
    /// <summary>
    /// Module-qualified inventory slot address on the destination vessel. A
    /// bare slot index is ambiguous once the destination carries more than one
    /// <c>ModuleInventoryPart</c> (multiple cargo containers, or a pod plus a
    /// container), so the probe/planner/writer contract addresses a slot by
    /// (part, module, slot). <see cref="PartIndex"/> / <see cref="ModuleIndex"/>
    /// are positional indices into the SAME collections the probe walked —
    /// <c>vessel.parts[i].Modules[m]</c> on the loaded branch,
    /// <c>protoVessel.protoPartSnapshots[i].modules[m]</c> on the unloaded
    /// branch — so the writer must resolve them on the same captured
    /// <c>isLoaded</c> branch the probe reported against.
    /// </summary>
    internal readonly struct InventorySlotAddress : IEquatable<InventorySlotAddress>
    {
        public readonly int PartIndex;
        public readonly int ModuleIndex;
        public readonly int SlotIndex;
        // default(InventorySlotAddress) would otherwise read as the VALID
        // address (0, 0, 0) — the root part's first slot — so a forgotten
        // initialization anywhere downstream would silently target a real
        // slot. The constructor is the only place that sets this, making the
        // type's default value invalid by construction.
        private readonly bool isSet;

        public InventorySlotAddress(int partIndex, int moduleIndex, int slotIndex)
        {
            PartIndex = partIndex;
            ModuleIndex = moduleIndex;
            SlotIndex = slotIndex;
            isSet = true;
        }

        /// <summary>Sentinel for "no empty slot available" (the old bare -1).</summary>
        public static readonly InventorySlotAddress None = new InventorySlotAddress(-1, -1, -1);

        public bool IsValid => isSet && PartIndex >= 0 && ModuleIndex >= 0 && SlotIndex >= 0;

        public bool Equals(InventorySlotAddress other)
        {
            // isSet participates so default(InventorySlotAddress) never
            // compares equal to the constructed (0, 0, 0) — two values with
            // different IsValid must not be interchangeable as keys.
            return PartIndex == other.PartIndex
                && ModuleIndex == other.ModuleIndex
                && SlotIndex == other.SlotIndex
                && isSet == other.isSet;
        }

        public override bool Equals(object obj) => obj is InventorySlotAddress other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = PartIndex;
                hash = (hash * 397) ^ ModuleIndex;
                hash = (hash * 397) ^ SlotIndex;
                hash = (hash * 397) ^ (isSet ? 1 : 0);
                return hash;
            }
        }

        public override string ToString()
        {
            return IsValid
                ? "part" + PartIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "/mod" + ModuleIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "/slot" + SlotIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "<none>";
        }
    }

    /// <summary>
    /// Probe interface for destination capacity queries. The pure planner calls
    /// this for each resource and each inventory item; live KSP queries live
    /// behind it in Phase B's LiveDeliveryCapacityProbe.
    /// </summary>
    internal interface IDeliveryCapacityProbe
    {
        /// <summary>Free capacity on the destination for the named resource, summed across all parts that hold it (and that have flowState=true).</summary>
        double ProbeResourceFreeCapacity(string resourceName);

        /// <summary>
        /// Returns the address of the next empty inventory slot across ALL
        /// inventory modules on the destination (deterministic order: vessel
        /// part order, then module order within the part, then ascending slot
        /// index), or <see cref="InventorySlotAddress.None"/> if every module
        /// is full.
        /// </summary>
        InventorySlotAddress ProbeFirstEmptyInventorySlot();

        /// <summary>Notify the probe that the given slot has been assigned, so subsequent ProbeFirstEmptyInventorySlot calls skip it.</summary>
        void ConsumeInventorySlot(InventorySlotAddress address);

        /// <summary>
        /// Fallback stack size for the item's part, read from the live
        /// <c>ModuleCargoPart.stackableQuantity</c> on the part prefab.
        /// Consulted only when the payload's STOREDPART wrapper carries no
        /// <c>stackCapacity</c> value and its inner PART node no
        /// <c>moduleCargoStackableQuantity</c>. Returns 1 when unknown.
        /// </summary>
        int ProbeInventoryStackableQuantity(InventoryPayloadItem item);

        /// <summary>
        /// Volume/mass admission for storing <paramref name="requestedUnits"/>
        /// units of the item into the destination's inventory containers,
        /// mirroring stock <c>ModuleInventoryPart.HasCapacity</c>: per-unit
        /// packed volume and prefab mass against the summed
        /// <c>packedVolumeLimit</c> / <c>massLimit</c> across ALL inventory
        /// modules (a limit &lt;= 0 is unlimited; any unlimited module makes
        /// that axis unlimited vessel-wide), accounting for what is already
        /// stored plus capacity consumed earlier in this planning pass.
        /// Returns how many of the requested units fit (0 when the item cannot
        /// be stored at all).
        /// </summary>
        int ProbeInventoryUnitsThatFit(InventoryPayloadItem item, int requestedUnits);

        /// <summary>Notify the probe that capacity for the given units has been claimed, so subsequent ProbeInventoryUnitsThatFit calls see less headroom.</summary>
        void ConsumeInventoryCapacity(InventoryPayloadItem item, int units);
    }

    internal readonly struct ResourceDeliveryLine
    {
        public readonly string Name;
        public readonly double Requested;
        public readonly double Available;

        public ResourceDeliveryLine(string name, double requested, double available)
        {
            Name = name;
            Requested = requested;
            Available = available;
        }
    }

    internal readonly struct InventoryDeliveryLine
    {
        public readonly InventoryPayloadItem Item;
        public readonly InventorySlotAddress AssignedSlot; // None (!IsValid) = skipped (no empty slot / does not fit)

        /// <summary>
        /// Units to store in the assigned slot (a manifest item whose Quantity
        /// exceeds its stack capacity splits into multiple lines). On a skipped
        /// line (AssignedSlot invalid) this is the count of units that could
        /// NOT be placed, for diagnostics; the writer never reads it there.
        /// </summary>
        public readonly int Units;

        public InventoryDeliveryLine(InventoryPayloadItem item, InventorySlotAddress assignedSlot, int units)
        {
            Item = item;
            AssignedSlot = assignedSlot;
            Units = units;
        }
    }

    internal readonly struct DeliveryPlan
    {
        public readonly IReadOnlyList<ResourceDeliveryLine> Resources;
        public readonly IReadOnlyList<InventoryDeliveryLine> Inventory;
        public readonly bool IsPartial;
        public readonly bool IsZero;

        public DeliveryPlan(
            IReadOnlyList<ResourceDeliveryLine> resources,
            IReadOnlyList<InventoryDeliveryLine> inventory,
            bool isPartial,
            bool isZero)
        {
            Resources = resources;
            Inventory = inventory;
            IsPartial = isPartial;
            IsZero = isZero;
        }

        internal static DeliveryPlan Empty()
        {
            return new DeliveryPlan(
                Array.Empty<ResourceDeliveryLine>(),
                Array.Empty<InventoryDeliveryLine>(),
                false,
                true);
        }
    }

    internal static class RouteDeliveryPlanner
    {
        internal static DeliveryPlan PrepareDelivery(
            Route route,
            int stopIndex,
            IDeliveryCapacityProbe probe)
        {
            // Null guards
            if (route == null || probe == null) return DeliveryPlan.Empty();
            if (route.Stops == null || stopIndex < 0 || stopIndex >= route.Stops.Count)
                return DeliveryPlan.Empty();
            RouteStop stop = route.Stops[stopIndex];
            if (stop == null) return DeliveryPlan.Empty();

            // Build resource plan
            var resources = new List<ResourceDeliveryLine>();
            bool anyResourcePartial = false;
            bool anyResourceDelivered = false;
            if (stop.DeliveryManifest != null && stop.DeliveryManifest.Count > 0)
            {
                // Deterministic order: sort by resource name. Avoids hash-set order leaking into ledger rows.
                var names = new List<string>(stop.DeliveryManifest.Keys);
                names.Sort(StringComparer.Ordinal);
                for (int i = 0; i < names.Count; i++)
                {
                    string name = names[i];
                    if (string.IsNullOrEmpty(name)) continue;
                    double requested = stop.DeliveryManifest[name];
                    if (requested <= 0.0) continue;
                    double freeCapacity = probe.ProbeResourceFreeCapacity(name);
                    if (freeCapacity < 0.0) freeCapacity = 0.0;
                    double available = Math.Min(requested, freeCapacity);
                    resources.Add(new ResourceDeliveryLine(name, requested, available));
                    if (available < requested) anyResourcePartial = true;
                    if (available > 0.0) anyResourceDelivered = true;
                }
            }

            // Build inventory plan
            var inventory = new List<InventoryDeliveryLine>();
            bool anyInventoryPartial = false;
            bool anyInventoryDelivered = false;
            if (stop.InventoryDeliveryManifest != null && stop.InventoryDeliveryManifest.Count > 0)
            {
                // Iterate in the manifest's existing order (already sorted by IdentityHash from item 1's RouteProofCapture).
                for (int i = 0; i < stop.InventoryDeliveryManifest.Count; i++)
                {
                    InventoryPayloadItem item = stop.InventoryDeliveryManifest[i];
                    if (item == null) continue;

                    // The manifest compresses identical stored parts per
                    // identity hash (Quantity is the delivered unit count),
                    // but a slot holds at most stackCapacity units — split
                    // into ceil(Quantity / stackCapacity) slot-sized stacks,
                    // each placed into its own (part, module, slot) address
                    // walked across ALL the destination's inventory modules.
                    int stackCapacity = ResolveStackCapacity(item, probe);
                    int remaining = item.Quantity > 0 ? item.Quantity : 1;
                    while (remaining > 0)
                    {
                        int want = remaining < stackCapacity ? remaining : stackCapacity;
                        // Volume/mass admission BEFORE claiming a slot, so an
                        // oversized item is skipped identically on the loaded
                        // and unloaded branches (stock StoreCargoPartAtSlot
                        // never checks limits itself — its enforcement lives
                        // in the storage UI, which automated delivery bypasses).
                        int admitted = probe.ProbeInventoryUnitsThatFit(item, want);
                        if (admitted <= 0) break;
                        InventorySlotAddress slot = probe.ProbeFirstEmptyInventorySlot();
                        if (!slot.IsValid) break;
                        probe.ConsumeInventorySlot(slot);
                        probe.ConsumeInventoryCapacity(item, admitted);
                        inventory.Add(new InventoryDeliveryLine(item, slot, admitted));
                        anyInventoryDelivered = true;
                        remaining -= admitted;
                    }
                    if (remaining > 0)
                    {
                        // No empty slot on any container or no volume/mass
                        // headroom for the rest — record ONE skip line carrying
                        // the unplaced unit count so the plan stays
                        // partial-aware.
                        inventory.Add(new InventoryDeliveryLine(item, InventorySlotAddress.None, remaining));
                        anyInventoryPartial = true;
                    }
                }
            }

            bool isPartial = anyResourcePartial || anyInventoryPartial;
            bool isZero = !anyResourceDelivered && !anyInventoryDelivered;
            return new DeliveryPlan(resources, inventory, isPartial, isZero);
        }

        /// <summary>
        /// Resolves the per-slot stack capacity for a manifest item. Priority:
        /// the STOREDPART wrapper's <c>stackCapacity</c> value (stock
        /// <c>StoredPart.Save</c> writes it, so recorded payloads carry it),
        /// then the inner PART node's <c>moduleCargoStackableQuantity</c>
        /// (stock <c>ProtoPartSnapshot.Save</c> writes it). The probe's live
        /// prefab lookup (<c>ModuleCargoPart.stackableQuantity</c>) is
        /// consulted ONLY when the item has no snapshot at all: when a
        /// snapshot exists but carries neither value, both stock load paths
        /// reconstruct it with stack capacity 1 (the loaded writer's
        /// UpdateStackAmountAtSlot clamps to the snapshot-derived capacity
        /// and the unloaded path's StoredPart.Load defaults to 1), so a
        /// prefab value above 1 would desync the plan from what the writers
        /// can actually persist. A resource-bearing payload is forced to 1
        /// regardless — stock <c>ModuleCargoPart</c> forces
        /// <c>stackableQuantity = 1</c> for parts that contain resources.
        /// Never below 1.
        /// </summary>
        internal static int ResolveStackCapacity(InventoryPayloadItem item, IDeliveryCapacityProbe probe)
        {
            if (item == null) return 1;

            if (item.StoredResources != null && item.StoredResources.Count > 0)
                return 1;

            ConfigNode wrapper = item.StoredPartSnapshot;
            if (wrapper == null)
            {
                int prefabCapacity = probe != null ? probe.ProbeInventoryStackableQuantity(item) : 1;
                return prefabCapacity > 0 ? prefabCapacity : 1;
            }

            int capacity = ReadPositiveIntValue(wrapper, "stackCapacity");
            if (capacity <= 0)
            {
                ConfigNode partNode = wrapper.GetNode("PART");
                if (partNode != null)
                    capacity = ReadPositiveIntValue(partNode, "moduleCargoStackableQuantity");
            }
            return capacity > 0 ? capacity : 1;
        }

        private static int ReadPositiveIntValue(ConfigNode node, string valueName)
        {
            string raw = node.GetValue(valueName);
            if (string.IsNullOrEmpty(raw)) return 0;
            return int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int parsed) && parsed > 0
                ? parsed
                : 0;
        }
    }
}
