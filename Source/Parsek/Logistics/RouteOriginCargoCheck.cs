using System;
using System.Collections.Generic;

namespace Parsek.Logistics
{
    /// <summary>
    /// Pure all-or-nothing origin-cargo gate (M1 origin debit, design D3).
    /// Given the route's recorded <c>CostManifest</c> and a stored-amount
    /// reader (in production <see cref="LiveOriginCargoProbe.ProbeResourceStored"/>),
    /// decides whether the origin currently holds the FULL manifest. The
    /// first short resource is named deterministically (ordinal name order)
    /// so the hold reason is stable across ticks and dictionary orderings.
    /// No KSP statics, no logging, no mutation - callers own side effects.
    /// </summary>
    internal static class RouteOriginCargoCheck
    {
        /// <summary>
        /// True when <paramref name="storedReader"/> reports at least the
        /// required amount for EVERY positive entry in
        /// <paramref name="costManifest"/> (all-or-nothing, design 19.2.5
        /// rule 1). On failure, <paramref name="lackingResource"/> names the
        /// FIRST short resource in ordinal name order and
        /// <paramref name="shortfall"/> carries <c>required - stored</c> for
        /// it. Non-positive manifest entries are skipped; a null or empty
        /// manifest passes (nothing to verify).
        /// </summary>
        internal static bool HasRequired(
            Dictionary<string, double> costManifest,
            Func<string, double> storedReader,
            out string lackingResource,
            out double shortfall)
        {
            lackingResource = string.Empty;
            shortfall = 0.0;

            if (costManifest == null || costManifest.Count == 0)
                return true;

            if (storedReader == null)
            {
                // Defensive: without a reader the manifest cannot be verified;
                // failing closed keeps the all-or-nothing contract honest.
                lackingResource = "null-stored-reader";
                return false;
            }

            // Deterministic first-failure: sort the resource names ordinal so
            // the named short resource does not depend on dictionary order.
            var names = new List<string>(costManifest.Keys);
            names.Sort(StringComparer.Ordinal);

            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                if (string.IsNullOrEmpty(name)) continue;
                double required = costManifest[name];
                if (required <= 0.0) continue;
                double stored = storedReader(name);
                if (stored < required)
                {
                    lackingResource = name;
                    shortfall = required - stored;
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// True when the route would need an INVENTORY debit from a physical
        /// origin: non-KSC origin with a non-empty
        /// <see cref="Route.InventoryCostManifest"/>. M1 debits resources
        /// only (design D6 / OQ1) - removing exact STOREDPART payloads by
        /// identity is the M3 stock-slot-identity work, and delivering items
        /// without debiting them would duplicate matter. Routes matching this
        /// predicate hold in WaitingForResources with reason
        /// <c>inventory-origin-debit-unsupported</c>.
        /// </summary>
        internal static bool RequiresInventoryDebit(Route route)
        {
            if (route == null) return false;
            if (route.IsKscOrigin) return false;
            return route.InventoryCostManifest != null && route.InventoryCostManifest.Count > 0;
        }
    }
}
