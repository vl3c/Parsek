using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;

namespace Parsek.InGameTests.Helpers
{
    /// <summary>
    /// Shared fixture for the logistics in-game cells that drive a synthetic
    /// route whose DELIVERY lands on a live vessel (almost always
    /// <see cref="FlightGlobals.ActiveVessel"/>, the pad rocket).
    ///
    /// <para><b>The defect this exists to prevent.</b>
    /// <see cref="LiveRouteRuntimeEnvironment.DestinationHasCapacity"/> is an
    /// ALL-OR-NOTHING eligibility gate: a cycle only fires when the FULL delivery
    /// manifest fits the destination. A pad rocket whose LiquidFuel tank is full
    /// (720/720 on the committed logistics fixture) has ZERO free capacity, so the
    /// gate CORRECTLY holds every synthetic cycle in <c>DestinationFull</c> and the
    /// cell reds on a route-state or ledger assertion that is really about
    /// something else. Nine cells across three suites hit exactly that on the H40
    /// reading run; the cells that passed all happened to carry their own inline
    /// drain. This helper is that precondition, once, so the cells state their
    /// requirement instead of accidentally depending on a save's fuel level.</para>
    ///
    /// <para><b>It is NOT the inline pre-drain those other cells use.</b> Five
    /// cells (delivery / dock-boundary cadence / multi-stop / route-on-missions /
    /// rewind-redelivery) drain ONE specific <see cref="PartResource"/> that they
    /// then ASSERT ON by reference (<c>fuelResource.amount == postDrain +
    /// expectedDelta</c>). This helper drains the MINIMUM across whichever
    /// deliverable tanks it needs, which is the right contract for "make the gate
    /// pass" and the wrong one for "assert this exact tank received the fill" -
    /// swapping it in there would silently degrade those assertions to skipped
    /// whenever a different tank supplied the headroom. Those sites are
    /// deliberately left alone.</para>
    ///
    /// <para><b>Deliverability is read through the PRODUCTION predicate</b>
    /// (<see cref="RouteOrchestrator.ShouldDeliverToResource"/>, the same one
    /// <c>LiveDeliveryCapacityProbe.ProbeLoadedResourceFree</c> applies), so the
    /// headroom this creates is exactly the headroom the gate will measure. It
    /// cannot drift from the gate without the gate's own predicate changing.</para>
    ///
    /// <para><b>Never worse than today.</b> A destination that is not on the
    /// LOADED path is left untouched and reported as a no-op success: the capacity
    /// probe reads that vessel's ProtoPartResourceSnapshots, which draining live
    /// PartResources would not move, so the cell proceeds exactly as it did before
    /// the helper existed. Only a destination that physically cannot hold the
    /// manifest at all returns false, and the caller <c>Skip</c>s - never fails.</para>
    /// </summary>
    internal static class DestinationHeadroomFixture
    {
        private const string Tag = "TestHelper";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Ensures <paramref name="dest"/> has at least <paramref name="needed"/>
        /// free capacity for <paramref name="resourceName"/>, draining the MINIMUM
        /// required from deliverable (flow-enabled, non-NO_FLOW) tanks in
        /// <c>vessel.parts</c> order.
        ///
        /// <para><paramref name="restoreSnapshot"/> carries the pre-drain amount of
        /// every tank this helper touched (empty when it drained nothing). The
        /// caller MUST hand it to <see cref="RestoreDrainedResources"/> from its
        /// existing <c>finally</c> / restore lambda so no fuel litter survives the
        /// cell. A caller that already snapshots the whole vessel before calling
        /// this (the common case) has the drain covered by that snapshot too;
        /// restoring both is harmless because both hold the same pre-drain values.</para>
        ///
        /// <para>Returns false ONLY when the destination physically cannot hold
        /// <paramref name="needed"/> - summed deliverable <c>maxAmount</c> is below
        /// it, or there is no such tank at all - with a
        /// <paramref name="skipReason"/> naming the measurement. That is a fixture
        /// statement about the craft, so the caller <c>Skip</c>s; it is never an
        /// assertion failure.</para>
        /// </summary>
        internal static bool TryEnsureDestinationHeadroom(
            Vessel dest,
            string resourceName,
            double needed,
            out List<KeyValuePair<PartResource, double>> restoreSnapshot,
            out string skipReason)
        {
            restoreSnapshot = new List<KeyValuePair<PartResource, double>>();
            skipReason = null;

            if (dest == null)
            {
                skipReason = "Destination headroom: the delivery destination vessel is null";
                return false;
            }
            if (string.IsNullOrEmpty(resourceName))
            {
                skipReason = $"Destination headroom: no resource name given for '{dest.vesselName}'";
                return false;
            }
            if (needed <= 0.0)
            {
                ParsekLog.Verbose(Tag,
                    $"TryEnsureDestinationHeadroom: nothing needed for {resourceName} on " +
                    $"'{dest.vesselName}' (needed={needed.ToString("R", IC)}) - no drain");
                return true;
            }

            // A destination off the LOADED path is governed by its proto snapshots,
            // which a live PartResource drain does not move. Leave it exactly as it
            // was and let the cell run as it did before this helper existed.
            if (!(dest.loaded && !dest.packed))
            {
                ParsekLog.Verbose(Tag,
                    $"TryEnsureDestinationHeadroom: destination '{dest.vesselName}' is not " +
                    $"loaded+unpacked (loaded={dest.loaded}, packed={dest.packed}); the capacity probe " +
                    "reads its proto snapshots, so no live drain applies - leaving it untouched");
                return true;
            }

            // Gather the DELIVERABLE tanks through the production predicate, so the
            // headroom created here is the headroom the gate will measure.
            var tanks = new List<PartResource>();
            double free = 0.0;
            double capacity = 0.0;
            if (dest.parts != null)
            {
                for (int i = 0; i < dest.parts.Count; i++)
                {
                    Part p = dest.parts[i];
                    if (p == null || p.Resources == null) continue;
                    PartResource pr = p.Resources.Get(resourceName);
                    if (pr == null) continue;
                    ResourceFlowMode mode = pr.info != null
                        ? pr.info.resourceFlowMode
                        : ResourceFlowMode.ALL_VESSEL;
                    if (!RouteOrchestrator.ShouldDeliverToResource(pr.flowState, mode)) continue;
                    tanks.Add(pr);
                    capacity += pr.maxAmount;
                    double room = pr.maxAmount - pr.amount;
                    if (room > 0.0) free += room;
                }
            }

            if (tanks.Count == 0)
            {
                skipReason =
                    $"Destination '{dest.vesselName}' has no deliverable {resourceName} tank " +
                    "(none flow-enabled / all NO_FLOW); the delivery gate can never pass on this craft";
                return false;
            }
            if (capacity < needed)
            {
                skipReason =
                    $"Destination '{dest.vesselName}' cannot hold {needed.ToString("R", IC)} {resourceName} " +
                    $"at all (summed deliverable capacity {capacity.ToString("R", IC)}); " +
                    "use a craft with a larger tank to run this cell";
                return false;
            }

            if (free >= needed)
            {
                ParsekLog.Verbose(Tag,
                    $"TryEnsureDestinationHeadroom: '{dest.vesselName}' already has " +
                    $"{free.ToString("R", IC)} free {resourceName} (>= {needed.ToString("R", IC)}) - no drain");
                return true;
            }

            // Drain the MINIMUM: take the deficit out of the deliverable tanks in
            // parts order, snapshotting each before it is touched.
            double deficit = needed - free;
            int drainedTanks = 0;
            for (int i = 0; i < tanks.Count && deficit > 0.0; i++)
            {
                PartResource pr = tanks[i];
                if (pr.amount <= 0.0) continue;
                double take = Math.Min(deficit, pr.amount);
                restoreSnapshot.Add(new KeyValuePair<PartResource, double>(pr, pr.amount));
                pr.amount -= take;
                deficit -= take;
                drainedTanks++;
            }

            ParsekLog.Info(Tag,
                $"TryEnsureDestinationHeadroom: drained {(needed - free).ToString("R", IC)} {resourceName} " +
                $"from {drainedTanks.ToString(IC)} tank(s) on '{dest.vesselName}' " +
                $"pid={dest.persistentId.ToString(IC)} so the all-or-nothing delivery gate can pass " +
                $"(freeBefore={free.ToString("R", IC)} needed={needed.ToString("R", IC)} " +
                $"capacity={capacity.ToString("R", IC)} residualDeficit={deficit.ToString("R", IC)})");

            // capacity >= needed was proven above, so the walk always clears the
            // deficit; the guard is defensive against a mid-test tank change.
            if (deficit > 0.0)
            {
                skipReason =
                    $"Destination '{dest.vesselName}' still short {deficit.ToString("R", IC)} {resourceName} " +
                    "free capacity after draining every deliverable tank";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Restores the pre-drain amounts captured by
        /// <see cref="TryEnsureDestinationHeadroom"/>. Safe on a null / empty
        /// snapshot and on a tank whose part was destroyed mid-cell; call it from
        /// the cell's existing <c>finally</c> / restore lambda.
        /// </summary>
        internal static void RestoreDrainedResources(
            List<KeyValuePair<PartResource, double>> restoreSnapshot)
        {
            if (restoreSnapshot == null || restoreSnapshot.Count == 0) return;
            int restored = 0;
            for (int i = 0; i < restoreSnapshot.Count; i++)
            {
                PartResource pr = restoreSnapshot[i].Key;
                if (pr == null) continue;
                try
                {
                    pr.amount = restoreSnapshot[i].Value;
                    restored++;
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag,
                        $"RestoreDrainedResources: failed to restore a tank " +
                        $"({ex.GetType().Name}: {ex.Message})");
                }
            }
            ParsekLog.Verbose(Tag,
                $"RestoreDrainedResources: restored {restored.ToString(IC)}/" +
                $"{restoreSnapshot.Count.ToString(IC)} pre-drain tank amount(s)");
        }
    }
}
