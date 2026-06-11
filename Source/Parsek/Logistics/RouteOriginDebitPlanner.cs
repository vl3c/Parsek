using System;
using System.Collections.Generic;

namespace Parsek.Logistics
{
    internal readonly struct OriginDebitLine
    {
        public readonly string Name;
        public readonly double Required;
        public readonly double Available;

        public OriginDebitLine(string name, double required, double available)
        {
            Name = name;
            Required = required;
            Available = available;
        }
    }

    internal readonly struct OriginDebitPlan
    {
        public readonly IReadOnlyList<OriginDebitLine> Resources;
        /// <summary>True when at least one resource's available (stored) amount fell short of the required amount.</summary>
        public readonly bool IsShort;

        public OriginDebitPlan(IReadOnlyList<OriginDebitLine> resources, bool isShort)
        {
            Resources = resources;
            IsShort = isShort;
        }

        internal static OriginDebitPlan Empty()
        {
            return new OriginDebitPlan(Array.Empty<OriginDebitLine>(), false);
        }
    }

    /// <summary>
    /// Pure origin-debit planner (M1 origin debit; mirror of
    /// <see cref="RouteDeliveryPlanner"/>). Per <see cref="Route.CostManifest"/>
    /// resource, clamps the planned removal to what the origin currently
    /// stores: <c>available = min(required, stored)</c>. The eligibility gate
    /// (<see cref="RouteOriginCargoCheck"/>) already verified the full
    /// manifest this tick, so a short line here is a mid-tick drift - the
    /// caller clamps-and-warns and records actual-vs-requested on the
    /// <c>RouteCargoDebited</c> row (design D3) rather than aborting a
    /// half-emitted cycle.
    /// </summary>
    internal static class RouteOriginDebitPlanner
    {
        internal static OriginDebitPlan PrepareDebit(Route route, IOriginCargoProbe probe)
        {
            // Null guards
            if (route == null || probe == null) return OriginDebitPlan.Empty();
            if (route.CostManifest == null || route.CostManifest.Count == 0)
                return OriginDebitPlan.Empty();

            // Deterministic order: sort by resource name. Avoids hash-set
            // order leaking into ledger rows (same rule as the delivery
            // planner and the origin-cargo gate's first-failure pick).
            var names = new List<string>(route.CostManifest.Keys);
            names.Sort(StringComparer.Ordinal);

            var resources = new List<OriginDebitLine>();
            bool isShort = false;
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                if (string.IsNullOrEmpty(name)) continue;
                double required = route.CostManifest[name];
                if (required <= 0.0) continue;
                double stored = probe.ProbeResourceStored(name);
                if (stored < 0.0) stored = 0.0;
                double available = Math.Min(required, stored);
                resources.Add(new OriginDebitLine(name, required, available));
                if (available < required) isShort = true;
            }

            return new OriginDebitPlan(resources, isShort);
        }
    }
}
