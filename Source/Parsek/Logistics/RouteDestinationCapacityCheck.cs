using System;
using System.Collections.Generic;

namespace Parsek.Logistics
{
    /// <summary>
    /// Pure all-or-nothing DESTINATION capacity gate (the dispatch-time
    /// counterpart of the origin-side <see cref="RouteOriginCargoCheck"/>).
    /// Given the route's delivery stops and a per-stop capacity probe factory,
    /// decides whether EVERY stop's full delivery manifest (resources AND
    /// stored-part inventory) fits the destination right now.
    ///
    /// <para><b>Why all-or-nothing.</b> Dispatch physically debits the origin
    /// (or charges KSC funds) for the FULL manifest; a partial delivery loses
    /// the remainder (the transport is a ghost, nothing comes back). Holding
    /// the cycle until the destination has room for everything keeps cargo
    /// conservation honest and mirrors the origin gate's all-or-nothing
    /// contract. The apply-time partial fill in
    /// <see cref="RouteDeliveryPlanner.PrepareDelivery"/> +
    /// <see cref="RouteOrchestrator.ApplyDeliveryFromPlan"/> remains as the
    /// backstop for capacity that changes between the gate and the write.</para>
    ///
    /// <para><b>Fit is evaluated with the SAME planner the delivery uses</b>
    /// (<see cref="RouteDeliveryPlanner.PrepareDelivery"/> over the caller's
    /// probe), so the gate cannot drift from what the applier would actually
    /// fit: a plan reporting <see cref="DeliveryPlan.IsPartial"/> is exactly a
    /// delivery that would lose cargo.</para>
    ///
    /// <para><b>Fail-open on an unresolvable destination:</b> a null probe for
    /// a stop means the caller could not resolve the stop's live vessel; the
    /// eligibility chain's earlier endpoint check owns that failure mode, so
    /// this gate skips the stop rather than double-reporting it (and the
    /// apply-time clamp still protects the write). No KSP statics, no logging,
    /// no mutation - callers own side effects.</para>
    /// </summary>
    internal static class RouteDestinationCapacityCheck
    {
        /// <summary>
        /// Token prefix for an inventory (stored-part) capacity shortfall, the
        /// sibling of the bare resource-name token. Consumed by
        /// <c>LogisticsHoldPresentation</c>.
        /// </summary>
        internal const string StoredPartTokenPrefix = "stored-part:";

        /// <summary>
        /// True when every stop's full delivery manifest fits its destination.
        /// On failure, <paramref name="fullToken"/> names the FIRST item that
        /// does not fit - the bare resource name (matching the loop-path hold
        /// token convention) or <c>stored-part:&lt;partName&gt;</c> for an
        /// inventory slot shortfall - and <paramref name="fullStopIndex"/> is
        /// the stop it was found at. Stops with no delivery manifest (pure
        /// pickup windows) and stops whose probe is null (unresolved vessel,
        /// fail-open) are skipped.
        /// </summary>
        internal static bool HasCapacityForAllStops(
            Route route,
            Func<int, IDeliveryCapacityProbe> probeForStop,
            out string fullToken,
            out int fullStopIndex)
        {
            fullToken = string.Empty;
            fullStopIndex = -1;

            if (route == null || route.Stops == null || probeForStop == null)
                return true; // nothing to gate

            for (int i = 0; i < route.Stops.Count; i++)
            {
                RouteStop stop = route.Stops[i];
                if (stop == null)
                    continue;

                bool hasResourceDelivery = stop.DeliveryManifest != null
                    && stop.DeliveryManifest.Count > 0;
                bool hasInventoryDelivery = stop.InventoryDeliveryManifest != null
                    && stop.InventoryDeliveryManifest.Count > 0;
                if (!hasResourceDelivery && !hasInventoryDelivery)
                    continue; // pure-pickup window - nothing arrives here

                IDeliveryCapacityProbe probe = probeForStop(i);
                if (probe == null)
                    continue; // unresolved destination - endpoint gate owns it (fail-open)

                DeliveryPlan plan = RouteDeliveryPlanner.PrepareDelivery(route, i, probe);
                if (!plan.IsPartial)
                    continue; // full manifest fits this stop

                fullToken = FirstShortToken(plan);
                fullStopIndex = i;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Names the first item of a partial plan that fell short: the first
        /// resource line whose available is below requested (bare resource
        /// name), else the first inventory line with no assigned slot
        /// (<c>stored-part:&lt;partName&gt;</c>). The plan's resource lines are
        /// already in deterministic ordinal-name order and its inventory lines
        /// in identity-hash order (both from
        /// <see cref="RouteDeliveryPlanner.PrepareDelivery"/>), so the named
        /// item is stable across ticks. Falls back to <c>delivery</c> when a
        /// partial plan carries no identifiable short line (defensive - cannot
        /// happen with the current planner).
        /// </summary>
        internal static string FirstShortToken(DeliveryPlan plan)
        {
            IReadOnlyList<ResourceDeliveryLine> resources = plan.Resources;
            if (resources != null)
            {
                for (int i = 0; i < resources.Count; i++)
                {
                    ResourceDeliveryLine line = resources[i];
                    if (line.Available < line.Requested && !string.IsNullOrEmpty(line.Name))
                        return line.Name;
                }
            }

            IReadOnlyList<InventoryDeliveryLine> inventory = plan.Inventory;
            if (inventory != null)
            {
                for (int i = 0; i < inventory.Count; i++)
                {
                    InventoryDeliveryLine line = inventory[i];
                    if (line.AssignedSlot < 0 && line.Item != null)
                    {
                        return StoredPartTokenPrefix
                            + (string.IsNullOrEmpty(line.Item.PartName)
                                ? "<unknown>"
                                : line.Item.PartName);
                    }
                }
            }

            return "delivery";
        }
    }
}
