using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Stock-runtime smoke check for the per-stop delivery applier (item 6
    /// of the logistics plan, Phase C). Injects a synthetic InTransit
    /// <see cref="Route"/> with <c>PendingDeliveryUT</c> already due and a
    /// <c>LiquidFuel</c> delivery manifest into <see cref="RouteStore"/>,
    /// pre-drains the active vessel's LiquidFuel so there's headroom, drives
    /// one <see cref="RouteOrchestrator.Tick(double)"/> through the production
    /// no-env overload (live <c>LiveRouteRuntimeEnvironment</c> + live
    /// <see cref="RouteEndpointResolver"/> + live <c>PartResource</c> writers),
    /// and asserts:
    ///
    ///   * Route transitioned out of <see cref="RouteStatus.InTransit"/> back
    ///     to <see cref="RouteStatus.Active"/> with <c>CompletedCycles == 1</c>.
    ///   * <c>PendingDeliveryUT</c> cleared and <c>PendingStopIndex == -1</c>.
    ///   * The active vessel's LiquidFuel amount increased by the manifest
    ///     amount (within a small tolerance — the loaded-path writer mutates
    ///     <see cref="PartResource.amount"/> directly).
    ///   * A new <see cref="GameActionType.RouteCargoDelivered"/> action exists
    ///     in <see cref="Ledger.Actions"/> tagged with the synthetic route id.
    ///
    /// xUnit covers <see cref="RouteOrchestrator.ApplyDeliveryFromPlan"/> with
    /// injected delegate writers; this test exercises the production-only path
    /// where the wrapper builds a <c>LiveDeliveryWriters</c> bundle that walks
    /// live <c>Vessel.parts</c> and mutates real <see cref="PartResource"/>
    /// amounts. A regression that NREs inside the live writer, mis-keys the
    /// flowState/NO_FLOW gate, or fails to clear pending state on success
    /// would surface here.
    ///
    /// The synthetic route is removed in <c>finally</c> and the pre-drain
    /// LiquidFuel amount is restored on the part so the test leaves no
    /// residue in <see cref="RouteStore.CommittedRoutes"/> or the live
    /// vessel state. The ledger row stays — <see cref="Ledger"/> is
    /// append-only and the synthetic route id is distinct from any real
    /// route's id so the row is harmless for downstream replay.
    /// </summary>
    public sealed class LogisticsDeliveryRuntimeTests
    {
        private const string LiquidFuelName = "LiquidFuel";
        private const double DeliveryAmount = 5.0;
        private const double ResourceTolerance = 0.01;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only - mutates RouteStore, Ledger, and live PartResource.amount under live KSP statics; excluded from ordinary Run All / Run category. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
            Description = "RouteOrchestrator.Tick applies a pending delivery to the active vessel: the route returns to Active with CompletedCycles=1, LiquidFuel is topped up by the manifest amount, and a RouteCargoDelivered ledger row is emitted")]
        public IEnumerator Delivery_LoadedVessel_AppliesResourceTransfer()
        {
            // PRECONDITION CHECKS -------------------------------------------------
            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live vessel to deliver onto");
            if (Planetarium.fetch == null)
                InGameAssert.Skip("Planetarium.fetch is null; cannot resolve current UT");

            Vessel activeVessel = FlightGlobals.ActiveVessel;

            // Find any part on the active vessel that holds LiquidFuel. The
            // delivery applier walks vessel.parts in order and fills the first
            // tank with free capacity, so we just need at least one tank with
            // headroom after the pre-drain.
            Part fuelPart;
            PartResource fuelResource;
            if (!TryFindLiquidFuelPart(activeVessel, out fuelPart, out fuelResource))
                InGameAssert.Skip(
                    $"Active vessel '{activeVessel.vesselName}' has no part with a LiquidFuel resource; " +
                    "skipping — pick a vessel with at least one LF tank to run this test");

            // PRE-DRAIN: ensure there's headroom for the synthetic delivery.
            // Cap the drain at max(0, current - DeliveryAmount) so a tiny tank
            // can still receive at least some of the manifest (the test
            // tolerates a partial fill — the applier sets CompletedCycles even
            // on partial deliveries — but the resource-amount assertion is
            // skipped when the tank's capacity is below DeliveryAmount).
            double originalAmount = fuelResource.amount;
            double maxAmount = fuelResource.maxAmount;
            double preDrainTarget = originalAmount - DeliveryAmount;
            if (preDrainTarget < 0.0) preDrainTarget = 0.0;
            fuelResource.amount = preDrainTarget;
            double postDrainAmount = fuelResource.amount;

            // EXPECTED post-delivery amount = post-drain + min(DeliveryAmount, headroom).
            // Headroom is capped at maxAmount-postDrain so we don't assert a
            // bigger fill than the tank physically holds.
            double headroom = maxAmount - postDrainAmount;
            double expectedDelta = DeliveryAmount < headroom ? DeliveryAmount : headroom;
            double expectedAmount = postDrainAmount + expectedDelta;

            string syntheticRouteId = "ingame-delivery-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            int beforeLedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;

            double currentUT = Planetarium.GetUniversalTime();

            // Build the synthetic InTransit route inline. RouteFixtureBuilder
            // lives in Source/Parsek.Tests/Generators/ and is not visible
            // from the production assembly, so we author the route directly
            // here — same field shape, just hand-written.
            //
            // PendingDeliveryUT is already past so the pre-evaluator delivery
            // hook in ProcessOneRoute fires on this tick. NextDispatchUT is
            // far in the future so the dispatch evaluator does not also fire
            // a second cycle in the same tick (which would re-set
            // PendingDeliveryUT and confuse the assertions).
            Route synthetic = new Route
            {
                Id = syntheticRouteId,
                Name = "Parsek Delivery Smoke Test",
                Status = RouteStatus.InTransit,
                IsKscOrigin = true,
                TransitDuration = 60.0,
                DispatchInterval = 600.0,
                NextDispatchUT = currentUT + 600.0,
                CurrentCycleStartUT = currentUT - 100.0,
                CurrentSegmentIndex = 0,
                PendingDeliveryUT = currentUT - 1.0,
                PendingStopIndex = 0,
                CompletedCycles = 0,
                KscDispatchFundsCost = 0.0,
                CostManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    { LiquidFuelName, DeliveryAmount },
                },
                RecordingIds = new List<string>(),
                SourceRefs = new List<RouteSourceRef>(),
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint
                        {
                            VesselPersistentId = activeVessel.persistentId,
                            BodyName = activeVessel.mainBody != null ? activeVessel.mainBody.bodyName : "Kerbin",
                            IsSurface = false,
                        },
                        ConnectionKind = RouteConnectionKind.DockingPort,
                        DeliveryManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            { LiquidFuelName, DeliveryAmount },
                        },
                        InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                        SegmentIndexBefore = 0,
                        DeliveryOffsetSeconds = 60.0,
                    },
                },
            };

            RouteStore.AddRoute(synthetic);
            bool addedToStore = true;

            string fuelPartNameForLog = fuelPart.partInfo != null ? fuelPart.partInfo.name : "<unknown>";

            try
            {
                ParsekLog.Verbose("TestRunner",
                    $"Delivery_LoadedVessel: pre-tick routeId={syntheticRouteId} " +
                    $"vessel={activeVessel.vesselName} pid={activeVessel.persistentId.ToString(IC)} " +
                    $"partName='{fuelPartNameForLog}' " +
                    $"originalAmount={originalAmount.ToString("R", IC)} " +
                    $"postDrainAmount={postDrainAmount.ToString("R", IC)} " +
                    $"maxAmount={maxAmount.ToString("R", IC)} " +
                    $"expectedAmount={expectedAmount.ToString("R", IC)} " +
                    $"beforeLedgerCount={beforeLedgerCount.ToString(IC)}");

                // ACT — production no-env overload. ProcessOneRoute's pre-evaluator
                // delivery hook fires because PendingDeliveryUT is already due.
                RouteOrchestrator.Tick(currentUT);

                // Yield one frame in case any deferred behavior settles on the
                // next FixedUpdate.
                yield return null;

                // ASSERTIONS ---------------------------------------------------

                // 1. Route state: delivered → Active, cycle completed, pending cleared.
                Route postTick;
                InGameAssert.IsTrue(
                    RouteStore.TryGetRoute(syntheticRouteId, out postTick),
                    "Synthetic route disappeared from store during Tick");
                InGameAssert.IsNotNull(postTick,
                    "TryGetRoute returned true but post-tick route was null");

                InGameAssert.AreEqual(RouteStatus.Active, postTick.Status,
                    $"Expected route status to transition to Active after delivery, but was {postTick.Status}");
                InGameAssert.AreEqual(1, postTick.CompletedCycles,
                    $"Expected CompletedCycles=1 after delivery, but was {postTick.CompletedCycles.ToString(IC)}");
                string pendingForLog = postTick.PendingDeliveryUT.HasValue
                    ? postTick.PendingDeliveryUT.Value.ToString("R", IC)
                    : "<null>";
                InGameAssert.IsFalse(postTick.PendingDeliveryUT.HasValue,
                    $"PendingDeliveryUT should be cleared after delivery applied (was {pendingForLog})");
                InGameAssert.AreEqual(-1, postTick.PendingStopIndex,
                    $"Expected PendingStopIndex=-1 after delivery, but was {postTick.PendingStopIndex.ToString(IC)}");

                // 2. Live resource: vessel's LiquidFuel topped up by manifest amount
                //    (within tolerance). The applier writes pr.amount directly on
                //    the loaded path, so re-reading the SAME PartResource here is
                //    the correct probe — no need to walk the vessel again.
                double actualAmount = fuelResource.amount;
                double diff = Math.Abs(actualAmount - expectedAmount);
                InGameAssert.IsTrue(
                    diff < ResourceTolerance,
                    $"Expected LiquidFuel ~= {expectedAmount.ToString("R", IC)} on part " +
                    $"'{fuelPartNameForLog}' " +
                    $"after delivery, but was {actualAmount.ToString("R", IC)} (diff={diff.ToString("R", IC)} tol={ResourceTolerance.ToString("R", IC)})");

                // 3. Ledger row: a RouteCargoDelivered action with our route id
                //    must have been appended during the tick.
                int afterLedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;
                bool deliveredRowFound = false;
                if (afterLedgerCount > beforeLedgerCount && Ledger.Actions != null)
                {
                    for (int i = beforeLedgerCount; i < afterLedgerCount; i++)
                    {
                        GameAction action = Ledger.Actions[i];
                        if (action == null) continue;
                        if (action.Type != GameActionType.RouteCargoDelivered) continue;
                        if (!string.Equals(action.RouteId, syntheticRouteId, StringComparison.Ordinal)) continue;
                        deliveredRowFound = true;
                        break;
                    }
                }
                InGameAssert.IsTrue(
                    deliveredRowFound,
                    $"No RouteCargoDelivered ledger row found for synthetic routeId={syntheticRouteId} " +
                    $"(beforeCount={beforeLedgerCount.ToString(IC)} afterCount={afterLedgerCount.ToString(IC)}). " +
                    "ApplyDeliveryFromPlan should have emitted one via ctx.LedgerEmitter.");

                ParsekLog.Info("TestRunner",
                    $"Delivery_LoadedVessel: PASS routeId={syntheticRouteId} " +
                    $"status={postTick.Status} completedCycles={postTick.CompletedCycles.ToString(IC)} " +
                    $"fuelBefore={postDrainAmount.ToString("R", IC)} fuelAfter={actualAmount.ToString("R", IC)} " +
                    $"newLedgerRows={(afterLedgerCount - beforeLedgerCount).ToString(IC)}");
            }
            finally
            {
                // TEARDOWN: remove the synthetic route and restore the pre-drain
                // LiquidFuel amount so the player's save / next batch test do
                // not see the pre-drain or the synthetic delivery. The ledger
                // row stays (Ledger is append-only); the synthetic RouteId is
                // distinct from any real route's id so the row is harmless.
                if (addedToStore)
                {
                    bool removed = RouteStore.RemoveRoute(syntheticRouteId);
                    ParsekLog.Verbose("TestRunner",
                        $"Delivery_LoadedVessel cleanup: RemoveRoute(synthetic)={removed}");
                }

                // Restore LiquidFuel to the value we read before the pre-drain
                // step. fuelResource is a reference into the live vessel, so
                // this writes directly back. Guard against a null reference if
                // the part was destroyed mid-test.
                try
                {
                    if (fuelResource != null)
                    {
                        fuelResource.amount = originalAmount;
                        ParsekLog.Verbose("TestRunner",
                            $"Delivery_LoadedVessel cleanup: restored LiquidFuel to {originalAmount.ToString("R", IC)}");
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("TestRunner",
                        $"Delivery_LoadedVessel cleanup: failed to restore LiquidFuel ({ex.GetType().Name}: {ex.Message})");
                }
            }
        }

        /// <summary>
        /// Finds the first <see cref="Part"/> on <paramref name="vessel"/> that
        /// carries a <c>LiquidFuel</c> resource entry. Mirrors the order the
        /// delivery applier walks (vessel.parts ascending), so the test's
        /// pre-drain / restore + the applier's fill all land on the same tank.
        /// </summary>
        private static bool TryFindLiquidFuelPart(Vessel vessel, out Part part, out PartResource resource)
        {
            part = null;
            resource = null;
            if (vessel == null || vessel.parts == null) return false;

            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Resources == null) continue;
                PartResource pr = p.Resources.Get(LiquidFuelName);
                if (pr == null) continue;
                part = p;
                resource = pr;
                return true;
            }
            return false;
        }
    }
}
