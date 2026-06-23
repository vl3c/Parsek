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
    /// Pure resource-removal planner (M1 origin debit + M3 per-window pickup;
    /// mirror of <see cref="RouteDeliveryPlanner"/>). Per required-amounts
    /// manifest entry, clamps the planned removal to what the source vessel
    /// currently stores: <c>available = min(required, stored)</c>. The M1
    /// eligibility gate (<see cref="RouteOriginCargoCheck"/>) already verified
    /// the route's full <c>CostManifest</c> this tick, so a short line on the
    /// M1 path is a mid-tick drift - the caller clamps-and-warns and records
    /// actual-vs-requested on the <c>RouteCargoDebited</c> row (design D3)
    /// rather than aborting a half-emitted cycle. The M3 pickup path
    /// (design D5) plans from a per-window PICKUP manifest (the witnessed
    /// loaded term) instead of <c>CostManifest</c>; the clamp is identical and
    /// the floor (<c>min(required, stored)</c>) makes the window-local debit
    /// self-contained per source.
    /// </summary>
    internal static class RouteOriginDebitPlanner
    {
        /// <summary>
        /// M1 origin-debit entry point: plan a debit from the route's
        /// <see cref="Route.CostManifest"/> over <paramref name="probe"/>.
        /// Byte-behaviour-identical to the pre-M3 implementation; delegates to
        /// the general manifest overload (design D5: the M1 call site stays
        /// unchanged when the planner becomes manifest-agnostic).
        /// </summary>
        internal static OriginDebitPlan PrepareDebit(Route route, IOriginCargoProbe probe)
        {
            if (route == null) return OriginDebitPlan.Empty();
            return PrepareDebit(route.CostManifest, probe);
        }

        /// <summary>
        /// General resource-removal planner (M3 design D5): plan a debit from
        /// an arbitrary <paramref name="requiredAmounts"/> manifest over
        /// <paramref name="probe"/>. The M1 origin path passes
        /// <see cref="Route.CostManifest"/> (via the <see cref="Route"/>
        /// overload) and the M3 per-window pickup path passes a stop's pickup
        /// manifest; the clamp (<c>available = min(required, stored)</c>),
        /// the non-positive skip, the negative-stored floor, and the
        /// deterministic ordinal ordering are identical for both.
        /// </summary>
        internal static OriginDebitPlan PrepareDebit(
            Dictionary<string, double> requiredAmounts, IOriginCargoProbe probe)
        {
            // Null guards
            if (requiredAmounts == null || probe == null) return OriginDebitPlan.Empty();
            if (requiredAmounts.Count == 0) return OriginDebitPlan.Empty();

            // Deterministic order: sort by resource name. Avoids hash-set
            // order leaking into ledger rows (same rule as the delivery
            // planner and the origin-cargo gate's first-failure pick).
            var names = new List<string>(requiredAmounts.Keys);
            names.Sort(StringComparer.Ordinal);

            var resources = new List<OriginDebitLine>();
            bool isShort = false;
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                if (string.IsNullOrEmpty(name)) continue;
                double required = requiredAmounts[name];
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
