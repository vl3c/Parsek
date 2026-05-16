using System;
using System.Collections.Generic;

namespace Parsek.Logistics
{
    /// <summary>
    /// Probe interface for destination capacity queries. The pure planner calls
    /// this for each resource and each inventory item; live KSP queries live
    /// behind it in Phase B's LiveDeliveryCapacityProbe.
    /// </summary>
    internal interface IDeliveryCapacityProbe
    {
        /// <summary>Free capacity on the destination for the named resource, summed across all parts that hold it (and that have flowState=true).</summary>
        double ProbeResourceFreeCapacity(string resourceName);

        /// <summary>Returns the slot index for the next empty inventory slot, or -1 if no slot is available.</summary>
        int ProbeFirstEmptyInventorySlot();

        /// <summary>Notify the probe that the given slot has been assigned, so subsequent ProbeFirstEmptyInventorySlot calls skip it.</summary>
        void ConsumeInventorySlot(int slotIndex);
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
        public readonly int AssignedSlot; // -1 = skipped (no empty slot)

        public InventoryDeliveryLine(InventoryPayloadItem item, int assignedSlot)
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
                    int slot = probe.ProbeFirstEmptyInventorySlot();
                    if (slot < 0)
                    {
                        inventory.Add(new InventoryDeliveryLine(item, -1));
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
