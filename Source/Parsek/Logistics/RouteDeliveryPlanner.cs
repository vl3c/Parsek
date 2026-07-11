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
        public readonly InventorySlotAddress AssignedSlot; // None (!IsValid) = skipped (no empty slot)

        public InventoryDeliveryLine(InventoryPayloadItem item, InventorySlotAddress assignedSlot)
        {
            Item = item;
            AssignedSlot = assignedSlot;
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
                    InventorySlotAddress slot = probe.ProbeFirstEmptyInventorySlot();
                    if (!slot.IsValid)
                    {
                        inventory.Add(new InventoryDeliveryLine(item, slot));
                        anyInventoryPartial = true;
                        continue;
                    }
                    probe.ConsumeInventorySlot(slot);
                    inventory.Add(new InventoryDeliveryLine(item, slot));
                    anyInventoryDelivered = true;
                }
            }

            bool isPartial = anyResourcePartial || anyInventoryPartial;
            bool isZero = !anyResourceDelivered && !anyInventoryDelivered;
            return new DeliveryPlan(resources, inventory, isPartial, isZero);
        }
    }
}
