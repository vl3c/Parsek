using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Per-resource amount/capacity pair, summed across all parts of a vessel.
    /// Value type — dict indexer returns a copy; use read-modify-write for accumulation.
    /// </summary>
    internal struct ResourceAmount
    {
        public double amount;
        public double maxAmount;

        public override string ToString()
        {
            return $"{amount:F1}/{maxAmount:F1}";
        }
    }

    /// <summary>
    /// Pure static helpers for resource manifest extraction and comparison.
    /// </summary>
    internal static class ResourceManifest
    {
        /// <summary>
        /// Compute per-resource delta between start and end manifests.
        /// Positive = gained, negative = consumed.
        /// Returns null if both inputs are null.
        /// </summary>
        internal static Dictionary<string, double> ComputeResourceDelta(
            Dictionary<string, ResourceAmount> start,
            Dictionary<string, ResourceAmount> end)
        {
            if (start == null && end == null) return null;

            var delta = new Dictionary<string, double>();

            // Build merged key set from start ∪ end
            var keys = new HashSet<string>();
            if (start != null)
                foreach (var k in start.Keys) keys.Add(k);
            if (end != null)
                foreach (var k in end.Keys) keys.Add(k);

            foreach (var key in keys)
            {
                double startAmt = 0;
                double endAmt = 0;

                if (start != null && start.TryGetValue(key, out var startRa))
                    startAmt = startRa.amount;
                if (end != null && end.TryGetValue(key, out var endRa))
                    endAmt = endRa.amount;

                delta[key] = endAmt - startAmt;
            }

            return delta;
        }
    }
}
