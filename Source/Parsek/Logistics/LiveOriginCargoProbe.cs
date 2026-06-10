using System;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Probe interface for origin stored-cargo queries (M1 origin debit).
    /// The origin cargo gate and the pure debit planner call this per
    /// resource; the live KSP read lives behind it in
    /// <see cref="LiveOriginCargoProbe"/>. Mirrors
    /// <see cref="IDeliveryCapacityProbe"/> on the delivery side.
    /// </summary>
    internal interface IOriginCargoProbe
    {
        /// <summary>Currently-stored amount of the named resource on the origin, summed across deliverable tanks (flowState=true, not NO_FLOW).</summary>
        double ProbeResourceStored(string resourceName);
    }

    /// <summary>
    /// Live stored-cargo probe over the ORIGIN vessel (M1 origin debit).
    /// Mirror of <see cref="LiveDeliveryCapacityProbe"/> but sums the
    /// currently STORED deliverable amount (not free capacity): the origin
    /// gate (<see cref="LiveRouteRuntimeEnvironment.OriginHasCargo"/>) asks
    /// "does the origin hold the manifest right now?", and the debit planner
    /// asks "how much can actually be removed?". Picks the loaded or unloaded
    /// read path from the caller-captured <c>isLoaded</c>.
    /// </summary>
    /// <remarks>
    /// The read gate (<see cref="RouteOrchestrator.ShouldDeliverToResource"/>)
    /// must stay symmetric with the origin debit writer - a flow-locked tank
    /// is player intent on the origin side too (design D4), so the probe
    /// counts only what the writer may touch. Counting a locked tank here
    /// while the writer skips it would let the gate pass on cargo the debit
    /// can never remove.
    /// </remarks>
    internal sealed class LiveOriginCargoProbe : IOriginCargoProbe
    {
        private const string Tag = RouteOrchestrator.Tag;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private readonly Vessel vessel;
        // Injected by the caller - captured once per gate-check / debit and
        // shared with the writer so the stored-amount read and the
        // resource-mutation path read from the SAME loaded/unloaded branch.
        // Re-evaluating <c>vessel.loaded && !vessel.packed</c> per-call would
        // diverge if the origin vessel transitions packed state mid-tick
        // (same rationale as <see cref="LiveDeliveryCapacityProbe.isLoaded"/>).
        internal readonly bool isLoaded;

        internal LiveOriginCargoProbe(Vessel vessel, bool isLoaded)
        {
            this.vessel = vessel;
            this.isLoaded = isLoaded;
        }

        public double ProbeResourceStored(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName) || vessel == null) return 0.0;
            try
            {
                return isLoaded
                    ? ProbeLoadedResourceStored(resourceName)
                    : ProbeUnloadedResourceStored(resourceName);
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"ProbeResourceStored({resourceName}) threw {ex.GetType().Name}: {ex.Message}; returning 0");
                return 0.0;
            }
        }

        private double ProbeLoadedResourceStored(string resourceName)
        {
            if (vessel.parts == null) return 0.0;
            double total = 0.0;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Resources == null) continue;
                PartResource pr = p.Resources.Get(resourceName);
                if (pr == null) continue;
                // Stored must match what the writer can actually remove -
                // closed tanks and NO_FLOW resources are non-debitable.
                ResourceFlowMode mode = pr.info != null ? pr.info.resourceFlowMode : ResourceFlowMode.ALL_VESSEL;
                if (!RouteOrchestrator.ShouldDeliverToResource(pr.flowState, mode)) continue;
                if (pr.amount > 0.0) total += pr.amount;
            }
            return total;
        }

        private double ProbeUnloadedResourceStored(string resourceName)
        {
            ProtoVessel pv = vessel.protoVessel;
            if (pv == null || pv.protoPartSnapshots == null) return 0.0;
            double total = 0.0;

            // NO_FLOW is a per-resource definition - look it up once and
            // reuse the mode in the loop (mirrors the capacity probe).
            ResourceFlowMode mode = RouteOrchestrator.LookupResourceFlowMode(resourceName);

            for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                if (pps == null || pps.resources == null) continue;
                for (int j = 0; j < pps.resources.Count; j++)
                {
                    ProtoPartResourceSnapshot prs = pps.resources[j];
                    if (prs == null) continue;
                    if (!string.Equals(prs.resourceName, resourceName, StringComparison.Ordinal)) continue;
                    // Mirror the writer-side gate so probe-reported stored and
                    // actual removable stay symmetric.
                    if (!RouteOrchestrator.ShouldDeliverToResource(prs.flowState, mode)) continue;
                    if (prs.amount > 0.0) total += prs.amount;
                }
            }
            return total;
        }
    }
}
