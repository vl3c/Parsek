using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Per-item count and slot usage, summed across all inventory modules on a vessel.
    /// Value type — dict indexer returns a copy; use read-modify-write for accumulation.
    /// </summary>
    internal struct InventoryItem
    {
        public int count;       // total quantity across all inventories on the vessel
        public int slotsTaken;  // total inventory slots occupied by this item type

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "count={0} slots={1}", count, slotsTaken);
        }
    }

    /// <summary>
    /// Pure static helpers for inventory manifest comparison.
    /// </summary>
    internal static class InventoryManifest
    {
        /// <summary>
        /// Compute per-item delta between start and end inventory manifests.
        /// Positive count = gained, negative = consumed/transferred.
        /// Returns null if both inputs are null.
        /// </summary>
        internal static Dictionary<string, InventoryItem> ComputeInventoryDelta(
            Dictionary<string, InventoryItem> start,
            Dictionary<string, InventoryItem> end)
        {
            if (start == null && end == null) return null;

            var delta = new Dictionary<string, InventoryItem>();

            // Build merged key set from start ∪ end
            var keys = new HashSet<string>();
            if (start != null)
                foreach (var k in start.Keys) keys.Add(k);
            if (end != null)
                foreach (var k in end.Keys) keys.Add(k);

            foreach (var key in keys)
            {
                int startCount = 0;
                int startSlots = 0;
                int endCount = 0;
                int endSlots = 0;

                if (start != null && start.TryGetValue(key, out var startItem))
                {
                    startCount = startItem.count;
                    startSlots = startItem.slotsTaken;
                }
                if (end != null && end.TryGetValue(key, out var endItem))
                {
                    endCount = endItem.count;
                    endSlots = endItem.slotsTaken;
                }

                delta[key] = new InventoryItem
                {
                    count = endCount - startCount,
                    slotsTaken = endSlots - startSlots
                };
            }

            return delta;
        }
    }
}
