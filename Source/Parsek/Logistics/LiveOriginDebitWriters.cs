using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Production debit-writer bundle used by the physical origin debit
    /// (M1; <see cref="RouteOrchestrator.EmitDispatchDebit"/> loop path).
    /// Mirror of <see cref="LiveDeliveryWriters"/> in the REMOVE direction:
    /// drains the planned per-resource amounts from the ORIGIN vessel's
    /// tanks, clamping at zero per tank, and owns the per-resource
    /// actual-debited totals the <c>RouteCargoDebited</c> row is built from.
    /// Created fresh per debit. Resources only - inventory debit is the M3
    /// stock-slot-identity work (design D6).
    /// </summary>
    /// <remarks>
    /// All origin-side KSP-state mutation funnels through this class; the
    /// policy gates (<see cref="RouteOrchestrator.ShouldDeliverToResource"/>,
    /// <see cref="RouteOrchestrator.LookupResourceFlowMode"/>) are shared
    /// with <see cref="LiveOriginCargoProbe"/> so the probe counts only what
    /// the writer may touch (design D4 gate/writer symmetry).
    /// </remarks>
    internal sealed class LiveOriginDebitWriters
    {
        private const string Tag = RouteOrchestrator.Tag;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private readonly Route route;
        private readonly Vessel vessel;
        private readonly OriginDebitPlan plan;
        // Injected by the orchestrator (ApplyOriginDebit) - captured once per
        // debit and shared with <see cref="LiveOriginCargoProbe"/> so the
        // writer mutates the SAME branch (loaded vs unloaded) that the probe
        // reported stored amounts against. Re-evaluating
        // <c>vessel.loaded && !vessel.packed</c> per-call would diverge if
        // the origin transitions packed state mid-tick (same rationale as
        // <see cref="LiveDeliveryWriters.isLoaded"/>).
        internal readonly bool isLoaded;
        private readonly Dictionary<string, double> actualPerResource;

        internal LiveOriginDebitWriters(Route route, Vessel originVessel, OriginDebitPlan plan, bool isLoaded)
        {
            this.route = route;
            this.vessel = originVessel;
            this.plan = plan;
            this.isLoaded = isLoaded;
            this.actualPerResource = new Dictionary<string, double>(
                plan.Resources?.Count ?? 0, StringComparer.Ordinal);
        }

        internal void WriteResourceDebit(string resourceName, double amount)
        {
            if (string.IsNullOrEmpty(resourceName) || amount <= 0.0)
                return;

            // Snapshot the origin tank pool BEFORE the write so the debit log
            // can show what the tanks held before vs after. Read over the SAME
            // debitable-tank set the writer mutates (same loaded/unloaded
            // branch + flow-state / NO_FLOW gate), so tankBefore - tankAfter
            // equals the removed amount and the numbers are coherent.
            double tankBefore;
            ReadResourceStoredTotal(resourceName, out tankBefore);

            double actual = 0.0;
            try
            {
                // Use the orchestrator-captured isLoaded so probe/writer agree
                // on which branch (loaded vs unloaded) to mutate. See class
                // doc on <see cref="isLoaded"/> for why a per-call evaluation
                // here would race the probe's snapshot.
                if (isLoaded)
                {
                    actual = WriteResourceDebitLoaded(resourceName, amount);
                }
                else
                {
                    actual = WriteResourceDebitUnloaded(resourceName, amount);
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"WriteResourceDebit({resourceName}, {amount.ToString("R", IC)}) threw {ex.GetType().Name}: {ex.Message}");
                actual = 0.0;
            }

            if (actual > 0.0)
            {
                if (actualPerResource.TryGetValue(resourceName, out double existing))
                    actualPerResource[resourceName] = existing + actual;
                else
                    actualPerResource[resourceName] = actual;
            }

            // Re-read the same debitable-tank pool AFTER the write so the log
            // reports an independently-measured after-state (rather than just
            // before-removed), which also surfaces any divergence.
            double tankAfter;
            ReadResourceStoredTotal(resourceName, out tankAfter);

            // Debit verification (parity with the delivery side's "Delivery
            // write:" line): log requested-vs-debited plus the origin tank
            // pool before/after. Bounded (one resource set per route per
            // cycle), so Info is appropriate. debited==0 means the tanks were
            // already empty, NO_FLOW, or the resource is absent on the origin.
            ParsekLog.Info(Tag,
                $"Origin debit: route={route?.Id ?? "<none>"} origin={vessel?.vesselName ?? "<none>"} " +
                $"pid={(vessel != null ? vessel.persistentId : 0u).ToString(IC)} " +
                $"resource={resourceName} requested={amount.ToString("R", IC)} " +
                $"debited={actual.ToString("R", IC)} " +
                $"tankBefore={tankBefore.ToString("R", IC)} tankAfter={tankAfter.ToString("R", IC)} " +
                $"path={(isLoaded ? "loaded" : "unloaded")}");
        }

        internal double ReadActualDebited(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName)) return 0.0;
            return actualPerResource.TryGetValue(resourceName, out double v) ? v : 0.0;
        }

        /// <summary>
        /// Sums the currently-stored amount of <paramref name="resourceName"/>
        /// across the origin tanks the writer would debit from: the same
        /// loaded/unloaded branch and the same flow-state / NO_FLOW gate as
        /// <see cref="WriteResourceDebitLoaded"/> /
        /// <see cref="WriteResourceDebitUnloaded"/>. Reading over exactly the
        /// debitable pool keeps the "tank before / after" debit log coherent
        /// and read-only.
        /// </summary>
        private void ReadResourceStoredTotal(string resourceName, out double stored)
        {
            stored = 0.0;
            if (string.IsNullOrEmpty(resourceName)) return;
            try
            {
                if (isLoaded)
                    ReadResourceStoredTotalLoaded(resourceName, ref stored);
                else
                    ReadResourceStoredTotalUnloaded(resourceName, ref stored);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"ReadResourceStoredTotal({resourceName}) threw {ex.GetType().Name}: {ex.Message}");
                stored = 0.0;
            }
        }

        private void ReadResourceStoredTotalLoaded(string resourceName, ref double stored)
        {
            if (vessel.parts == null) return;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Resources == null) continue;
                PartResource pr = p.Resources.Get(resourceName);
                if (pr == null) continue;
                ResourceFlowMode mode = pr.info != null ? pr.info.resourceFlowMode : ResourceFlowMode.ALL_VESSEL;
                if (!RouteOrchestrator.ShouldDeliverToResource(pr.flowState, mode)) continue;
                stored += pr.amount;
            }
        }

        private void ReadResourceStoredTotalUnloaded(string resourceName, ref double stored)
        {
            ProtoVessel pv = vessel.protoVessel;
            if (pv == null || pv.protoPartSnapshots == null) return;
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
                    if (!RouteOrchestrator.ShouldDeliverToResource(prs.flowState, mode)) continue;
                    stored += prs.amount;
                }
            }
        }

        /// <summary>
        /// Drains <paramref name="amount"/> from the origin vessel's parts
        /// that hold the named resource. Walks parts in vessel order and
        /// takes from each tank, clamping at zero per tank, until the
        /// requested amount is satisfied or the deliverable pool runs dry.
        /// </summary>
        private double WriteResourceDebitLoaded(string resourceName, double amount)
        {
            double remaining = amount;
            double total = 0.0;
            if (vessel.parts == null) return 0.0;

            for (int i = 0; i < vessel.parts.Count && remaining > 0.0; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Resources == null) continue;
                PartResource pr = p.Resources.Get(resourceName);
                if (pr == null) continue;
                // Mirror the probe's flowState gate AND suppress NO_FLOW
                // resources at the seam. ShouldDeliverToResource is the
                // single policy point shared with the unloaded writer and
                // the origin-cargo probe so stored/actual stay symmetric.
                ResourceFlowMode mode = pr.info != null ? pr.info.resourceFlowMode : ResourceFlowMode.ALL_VESSEL;
                if (!RouteOrchestrator.ShouldDeliverToResource(pr.flowState, mode)) continue;
                double held = pr.amount;
                if (held <= 0.0) continue;
                double delta = held < remaining ? held : remaining;
                pr.amount -= delta;
                remaining -= delta;
                total += delta;
            }
            return total;
        }

        /// <summary>
        /// Unloaded-vessel resource drain. Same distribution as the loaded
        /// path but writes <c>ProtoPartResourceSnapshot.amount</c>; the next
        /// time the vessel loads, the live <c>PartResource</c> values
        /// initialize from the proto snapshots so the debited amounts take
        /// effect (same persistence mechanism the delivery side has
        /// field-proven in <see cref="LiveDeliveryWriters"/>).
        /// </summary>
        private double WriteResourceDebitUnloaded(string resourceName, double amount)
        {
            double remaining = amount;
            double total = 0.0;
            ProtoVessel pv = vessel.protoVessel;
            if (pv == null || pv.protoPartSnapshots == null) return 0.0;

            // NO_FLOW gate is per-resource definition, not per-tank - look
            // it up once outside the part loop so we don't hammer the
            // library for every proto-part. flowState stays per-snapshot.
            ResourceFlowMode mode = RouteOrchestrator.LookupResourceFlowMode(resourceName);

            for (int i = 0; i < pv.protoPartSnapshots.Count && remaining > 0.0; i++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                if (pps == null || pps.resources == null) continue;
                for (int j = 0; j < pps.resources.Count && remaining > 0.0; j++)
                {
                    ProtoPartResourceSnapshot prs = pps.resources[j];
                    if (prs == null) continue;
                    if (!string.Equals(prs.resourceName, resourceName, StringComparison.Ordinal)) continue;
                    // Mirror the probe + loaded-writer gate. Closed proto
                    // tanks and NO_FLOW resources are never drained.
                    if (!RouteOrchestrator.ShouldDeliverToResource(prs.flowState, mode)) continue;
                    double held = prs.amount;
                    if (held <= 0.0) continue;
                    double delta = held < remaining ? held : remaining;
                    prs.amount -= delta;
                    remaining -= delta;
                    total += delta;
                }
            }
            return total;
        }
    }
}
