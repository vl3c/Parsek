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
        /// <see cref="Route.InventoryCostManifest"/>. Before M3 Phase 5 this
        /// gated the <c>inventory-origin-debit-unsupported</c> HOLD (the M1 D6
        /// carve-out). M3 Phase 5 lifts that hold by wiring the
        /// <see cref="LiveInventoryPickupWriter"/> into the origin-dispatch path,
        /// so this predicate is now only the SHAPE detector (does the route carry
        /// an origin inventory cost?) - the gate proper is
        /// <see cref="HasRequiredInventory"/>.
        /// </summary>
        internal static bool RequiresInventoryDebit(Route route)
        {
            if (route == null) return false;
            if (route.IsKscOrigin) return false;
            return route.InventoryCostManifest != null && route.InventoryCostManifest.Count > 0;
        }

        /// <summary>
        /// M3 Phase 5 (design D7 carve-out lift): all-or-nothing inventory
        /// presence gate, the inventory analogue of <see cref="HasRequired"/>.
        /// True when <paramref name="storedCounter"/> reports at least the
        /// witnessed quantity for EVERY positive-quantity item in
        /// <paramref name="inventoryManifest"/> (matched by exact
        /// <see cref="InventoryPayloadItem.IdentityHash"/>). On failure,
        /// <paramref name="lackingIdentity"/> names the FIRST short identity in
        /// ordinal hash order (deterministic) and <paramref name="shortQuantity"/>
        /// carries <c>required - stored</c>. A null/empty manifest passes
        /// (nothing to verify). No KSP statics, no mutation - the caller owns
        /// side effects (the live count comes from
        /// <see cref="LiveInventoryPickupWriter.CountStored"/>).
        /// </summary>
        internal static bool HasRequiredInventory(
            List<InventoryPayloadItem> inventoryManifest,
            Func<string, int> storedCounter,
            out string lackingIdentity,
            out int shortQuantity)
        {
            lackingIdentity = string.Empty;
            shortQuantity = 0;

            if (inventoryManifest == null || inventoryManifest.Count == 0)
                return true;

            if (storedCounter == null)
            {
                lackingIdentity = "null-stored-counter";
                return false;
            }

            // Deterministic first-failure: order by identity hash so the named
            // short identity does not depend on list order.
            var ordered = new List<InventoryPayloadItem>(inventoryManifest);
            ordered.Sort((a, b) => string.Compare(
                a?.IdentityHash, b?.IdentityHash, StringComparison.Ordinal));

            for (int i = 0; i < ordered.Count; i++)
            {
                InventoryPayloadItem item = ordered[i];
                if (item == null || string.IsNullOrEmpty(item.IdentityHash))
                    continue;
                int required = item.Quantity;
                if (required <= 0) continue;
                int stored = storedCounter(item.IdentityHash);
                if (stored < required)
                {
                    lackingIdentity = item.IdentityHash;
                    shortQuantity = required - stored;
                    return false;
                }
            }
            return true;
        }
    }
}
