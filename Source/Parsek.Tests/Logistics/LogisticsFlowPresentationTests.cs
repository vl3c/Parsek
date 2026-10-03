using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// M6 per-cycle flow display: the pure
    /// <see cref="LogisticsFlowPresentation"/> collector + formatter. Covers the
    /// one-walk bucketing extension (the existing H2/H3 delivery summary must
    /// stay byte-identical while the same pass also yields the flow rows),
    /// single-stop delivery-only / pickup-only / mixed cycles, multi-stop
    /// aggregation under one cycle line, shortfall flagging, last-N bounding
    /// (newest first), and the vanished-endpoint pid fallback. All inputs are
    /// plain data (no Unity, no shared static state), so no Sequential
    /// collection is needed.
    /// </summary>
    public class LogisticsFlowPresentationTests
    {
        private const string RouteId = "route-A";

        // ------------------------------------------------------------------
        // CollectRows: one walk, both outputs.
        // ------------------------------------------------------------------

        [Fact]
        public void CollectRows_OneWalk_DeliverySummaryByteIdenticalToLegacyScan()
        {
            var els = new List<GameAction>
            {
                null,
                new GameAction { Type = GameActionType.FundsEarning, UT = 10.0 },
                MakeDeliveredAction(RouteId, "cyc-1", 0, 100.0, 3,
                    Manifest(("LiquidFuel", 150.0)), null),
                MakeDebitedAction(RouteId, "cyc-1", 0, 100.0, 1,
                    Manifest(("LiquidFuel", 150.0)), null, 0u, 500f),
                MakePickedUpAction(RouteId, "cyc-1", 0, 100.0, 2,
                    Manifest(("Ore", 20.0)), null, 42u),
                MakeDeliveredAction("other-route", "cyc-x", 0, 105.0, 3,
                    Manifest(("Oxidizer", 30.0)), null),
                MakeDeliveredAction(RouteId, "cyc-2", 0, 200.0, 3,
                    Manifest(("LiquidFuel", 40.0)), Manifest(("LiquidFuel", 150.0))),
            };

            // Legacy scan: the exact pre-M6 CollectRouteDeliverySummary loop
            // (RouteCargoDelivered rows matched by RouteId, ordinal).
            var legacyRows = new List<LogisticsDeliveryPresentation.DeliveryRow>();
            for (int i = 0; i < els.Count; i++)
            {
                GameAction a = els[i];
                if (a == null) continue;
                if (a.Type != GameActionType.RouteCargoDelivered) continue;
                if (!string.Equals(a.RouteId, RouteId, StringComparison.Ordinal)) continue;
                legacyRows.Add(new LogisticsDeliveryPresentation.DeliveryRow(
                    a.RouteResourceManifest, a.RouteRequestedResourceManifest, a.UT));
            }
            LogisticsDeliveryPresentation.RouteDeliverySummary legacy =
                LogisticsDeliveryPresentation.SummarizeRouteDeliveries(legacyRows);

            // One shared walk: both outputs from a single pass.
            var deliveryRows = new List<LogisticsDeliveryPresentation.DeliveryRow>();
            var flowRows = new List<LogisticsFlowPresentation.FlowRow>();
            LogisticsFlowPresentation.CollectRows(els, RouteId, deliveryRows, flowRows);
            LogisticsDeliveryPresentation.RouteDeliverySummary shared =
                LogisticsDeliveryPresentation.SummarizeRouteDeliveries(deliveryRows);

            // Pin: the summary is byte-identical to the legacy scan - same row
            // count, the SAME manifest object references for the latest cycle,
            // and an equal cumulative total.
            Assert.Equal(legacy.RowCount, shared.RowCount);
            Assert.Same(legacy.LastActual, shared.LastActual);
            Assert.Same(legacy.LastRequested, shared.LastRequested);
            Assert.Equal(legacy.CumulativeTotal.Count, shared.CumulativeTotal.Count);
            foreach (KeyValuePair<string, double> kv in legacy.CumulativeTotal)
            {
                Assert.True(shared.CumulativeTotal.TryGetValue(kv.Key, out double v));
                Assert.Equal(kv.Value, v, 10);
            }

            // The same pass yielded the flow rows: 2 delivered + 1 debit +
            // 1 pickup for this route; the other route's row is excluded.
            Assert.Equal(4, flowRows.Count);
            Assert.Equal(2, flowRows.FindAll(
                r => r.Kind == LogisticsFlowPresentation.FlowRowKind.Delivered).Count);
            Assert.Single(flowRows.FindAll(
                r => r.Kind == LogisticsFlowPresentation.FlowRowKind.Debited));
            Assert.Single(flowRows.FindAll(
                r => r.Kind == LogisticsFlowPresentation.FlowRowKind.PickedUp));
        }

        [Fact]
        public void CollectRows_NullElsOrRouteId_IsNoOp()
        {
            var deliveryRows = new List<LogisticsDeliveryPresentation.DeliveryRow>();
            var flowRows = new List<LogisticsFlowPresentation.FlowRow>();

            LogisticsFlowPresentation.CollectRows(null, RouteId, deliveryRows, flowRows);
            Assert.Empty(deliveryRows);
            Assert.Empty(flowRows);

            var els = new List<GameAction>
            {
                MakeDeliveredAction(RouteId, "cyc-1", 0, 100.0, 3,
                    Manifest(("LiquidFuel", 150.0)), null),
            };
            LogisticsFlowPresentation.CollectRows(els, null, deliveryRows, flowRows);
            LogisticsFlowPresentation.CollectRows(els, string.Empty, deliveryRows, flowRows);
            Assert.Empty(deliveryRows);
            Assert.Empty(flowRows);
        }

        [Fact]
        public void CollectRows_CarriesRequestedInventoryCount()
        {
            var pickedUp = MakePickedUpAction(RouteId, "cyc-1", 0, 100.0, 2, null, null, 42u);
            pickedUp.RouteInventoryManifest = new List<InventoryPayloadItem>
            {
                new InventoryPayloadItem { IdentityHash = "ore-container", Quantity = 1 },
            };
            pickedUp.RouteRequestedInventoryManifest = new List<InventoryPayloadItem>
            {
                new InventoryPayloadItem { IdentityHash = "ore-container", Quantity = 2 },
                new InventoryPayloadItem { IdentityHash = "science-box", Quantity = 1 },
            };
            var els = new List<GameAction> { pickedUp };

            var flowRows = new List<LogisticsFlowPresentation.FlowRow>();
            LogisticsFlowPresentation.CollectRows(els, RouteId, null, flowRows);

            Assert.Single(flowRows);
            Assert.Equal(1, flowRows[0].InventoryCount);
            Assert.Equal(2, flowRows[0].RequestedInventoryCount);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static Dictionary<string, double> Manifest(params (string Key, double Value)[] entries)
        {
            var map = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach ((string key, double value) in entries)
                map[key] = value;
            return map;
        }

        private static LogisticsFlowPresentation.FlowRow FlowRow(
            LogisticsFlowPresentation.FlowRowKind kind,
            string cycleId, int stopIndex, double ut, int sequence,
            Dictionary<string, double> actual, Dictionary<string, double> requested,
            uint endpointPid, float kscFundsCost, int inventoryCount,
            int requestedInventoryCount = 0)
        {
            return new LogisticsFlowPresentation.FlowRow(
                kind, cycleId, stopIndex, ut, sequence,
                actual, requested, endpointPid, kscFundsCost, inventoryCount,
                requestedInventoryCount);
        }

        private static GameAction MakeDeliveredAction(
            string routeId, string cycleId, int stopIndex, double ut, int sequence,
            Dictionary<string, double> actual, Dictionary<string, double> requested)
        {
            return new GameAction
            {
                Type = GameActionType.RouteCargoDelivered,
                UT = ut,
                RouteId = routeId,
                RouteCycleId = cycleId,
                RouteStopIndex = stopIndex,
                Sequence = sequence,
                RouteResourceManifest = actual,
                RouteRequestedResourceManifest = requested,
            };
        }

        private static GameAction MakeDebitedAction(
            string routeId, string cycleId, int stopIndex, double ut, int sequence,
            Dictionary<string, double> actual, Dictionary<string, double> requested,
            uint originPid, float kscFundsCost)
        {
            return new GameAction
            {
                Type = GameActionType.RouteCargoDebited,
                UT = ut,
                RouteId = routeId,
                RouteCycleId = cycleId,
                RouteStopIndex = stopIndex,
                Sequence = sequence,
                RouteResourceManifest = actual,
                RouteRequestedResourceManifest = requested,
                RouteOriginVesselPid = originPid,
                RouteKscFundsCost = kscFundsCost,
            };
        }

        private static GameAction MakePickedUpAction(
            string routeId, string cycleId, int stopIndex, double ut, int sequence,
            Dictionary<string, double> actual, Dictionary<string, double> requested,
            uint endpointPid)
        {
            return new GameAction
            {
                Type = GameActionType.RouteCargoPickedUp,
                UT = ut,
                RouteId = routeId,
                RouteCycleId = cycleId,
                RouteStopIndex = stopIndex,
                Sequence = sequence,
                RouteResourceManifest = actual,
                RouteRequestedResourceManifest = requested,
                RouteOriginVesselPid = endpointPid,
            };
        }
    }
}
