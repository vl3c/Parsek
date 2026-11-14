using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Pure static helpers for crew manifest comparison.
    /// Crew manifests are Dictionary&lt;string, int&gt; (trait name → count).
    /// </summary>
    internal static class CrewManifest
    {
        /// <summary>
        /// Compute per-trait delta between start and end crew manifests.
        /// Positive = gained, negative = lost.
        /// Returns null if both inputs are null.
        /// </summary>
        internal static Dictionary<string, int> ComputeCrewDelta(
            Dictionary<string, int> start,
            Dictionary<string, int> end)
        {
            if (start == null && end == null) return null;

            var delta = new Dictionary<string, int>();

            // Build merged key set from start ∪ end
            var keys = new HashSet<string>();
            if (start != null)
                foreach (var k in start.Keys) keys.Add(k);
            if (end != null)
                foreach (var k in end.Keys) keys.Add(k);

            foreach (var key in keys)
            {
                int startCount = 0;
                int endCount = 0;

                if (start != null && start.TryGetValue(key, out var sc))
                    startCount = sc;
                if (end != null && end.TryGetValue(key, out var ec))
                    endCount = ec;

                delta[key] = endCount - startCount;
            }

            return delta;
        }
    }
}
