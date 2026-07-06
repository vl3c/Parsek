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

        // ------------------------------------------------------------------
        // FormatPerCycleFlow: per-cycle lines.
        // ------------------------------------------------------------------

        [Fact]
        public void FormatPerCycleFlow_SingleStopDeliveryOnly_FullFill()
        {
            var rows = new List<LogisticsFlowPresentation.FlowRow>
            {
                FlowRow(LogisticsFlowPresentation.FlowRowKind.Delivered, "cyc-1", 0, 1000.0, 3,
                    Manifest(("LiquidFuel", 150.0)), null, 0u, 0f, 0),
            };

            List<LogisticsFlowPresentation.CycleFlowLine> lines =
                LogisticsFlowPresentation.FormatPerCycleFlow(
                    rows, null, "KSC (funds)", new[] { "Munar Station" }, 4600.0, 5);

            Assert.Single(lines);
            Assert.False(lines[0].Shortfall);
            Assert.StartsWith("Cycle 1 (1.0h ago): ", lines[0].Text, StringComparison.Ordinal);
            Assert.Contains("delivered 150.0 LiquidFuel to Munar Station", lines[0].Text);
        }

        [Fact]
        public void FormatPerCycleFlow_PickupOnly_ResolvedSourceName()
        {
            var rows = new List<LogisticsFlowPresentation.FlowRow>
            {
                FlowRow(LogisticsFlowPresentation.FlowRowKind.PickedUp, "cyc-1", 0, 1000.0, 2,
                    Manifest(("Ore", 20.0)), null, 42u, 0f, 0),
            };
            var names = new Dictionary<uint, string> { [42u] = "Mining Rig" };

            List<LogisticsFlowPresentation.CycleFlowLine> lines =
                LogisticsFlowPresentation.FormatPerCycleFlow(
                    rows, names, null, null, 1000.0, 5);

            Assert.Single(lines);
            Assert.False(lines[0].Shortfall);
            Assert.Contains("picked up 20.0 Ore from Mining Rig", lines[0].Text);
        }

        [Fact]
        public void FormatPerCycleFlow_InventoryOnlyPickup_CountsItems()
        {
            var rows = new List<LogisticsFlowPresentation.FlowRow>
            {
                FlowRow(LogisticsFlowPresentation.FlowRowKind.PickedUp, "cyc-1", 0, 1000.0, 2,
                    null, null, 42u, 0f, 2),
            };
            var names = new Dictionary<uint, string> { [42u] = "Parts Depot" };

            List<LogisticsFlowPresentation.CycleFlowLine> lines =
                LogisticsFlowPresentation.FormatPerCycleFlow(
                    rows, names, null, null, 1000.0, 5);

            Assert.Single(lines);
            Assert.Contains("picked up 2 inventory item(s) from Parts Depot", lines[0].Text);
        }

        [Fact]
        public void FormatPerCycleFlow_MixedCycle_KscFundsDebit_SegmentsInLedgerOrder()
        {
            // One cycle: KSC funds debit (seq 1), pickup (seq 2), delivery
            // (seq 3), all at the dispatch UT. The KSC debit renders the funds
            // charge and SKIPS its informational cost manifest (no phantom
            // "took ... from KSC" double-count).
            var rows = new List<LogisticsFlowPresentation.FlowRow>
            {
                FlowRow(LogisticsFlowPresentation.FlowRowKind.Delivered, "cyc-1", 0, 1000.0, 3,
                    Manifest(("LiquidFuel", 150.0)), null, 0u, 0f, 0),
                FlowRow(LogisticsFlowPresentation.FlowRowKind.Debited, "cyc-1", 0, 1000.0, 1,
                    Manifest(("LiquidFuel", 150.0)), null, 0u, 500f, 0),
                FlowRow(LogisticsFlowPresentation.FlowRowKind.PickedUp, "cyc-1", 0, 1000.0, 2,
                    Manifest(("Ore", 20.0)), null, 42u, 0f, 0),
            };
            var names = new Dictionary<uint, string> { [42u] = "Mining Rig" };

            List<LogisticsFlowPresentation.CycleFlowLine> lines =
                LogisticsFlowPresentation.FormatPerCycleFlow(
                    rows, names, "KSC (funds)", new[] { "Munar Station" }, 1000.0, 5);

            Assert.Single(lines);
            string text = lines[0].Text;
            int paidIdx = text.IndexOf("paid 500 funds at KSC", StringComparison.Ordinal);
            int pickIdx = text.IndexOf("picked up 20.0 Ore from Mining Rig", StringComparison.Ordinal);
            int delivIdx = text.IndexOf("delivered 150.0 LiquidFuel to Munar Station", StringComparison.Ordinal);
            Assert.True(paidIdx >= 0, "funds segment missing: " + text);
            Assert.True(pickIdx > paidIdx, "pickup should follow the funds debit: " + text);
            Assert.True(delivIdx > pickIdx, "delivery should follow the pickup: " + text);
            // The KSC debit's cost manifest must NOT render as a physical debit.
            Assert.DoesNotContain("took", text);
        }

        [Fact]
        public void FormatPerCycleFlow_PhysicalDebit_NamesOriginVessel()
        {
            var rows = new List<LogisticsFlowPresentation.FlowRow>
            {
                FlowRow(LogisticsFlowPresentation.FlowRowKind.Debited, "cyc-1", 0, 1000.0, 1,
                    Manifest(("LiquidFuel", 150.0)), null, 7u, 0f, 0),
            };
            var names = new Dictionary<uint, string> { [7u] = "Minmus Depot" };

            List<LogisticsFlowPresentation.CycleFlowLine> lines =
                LogisticsFlowPresentation.FormatPerCycleFlow(
                    rows, names, "KSC (funds)", null, 1000.0, 5);

            Assert.Single(lines);
            Assert.Contains("took 150.0 LiquidFuel from Minmus Depot", lines[0].Text);
        }

        [Fact]
        public void FormatPerCycleFlow_MultiStopCycle_AggregatesUnderOneLine()
        {
            // One cycle, two delivery windows (stop 0 then stop 1, later UT).
            var rows = new List<LogisticsFlowPresentation.FlowRow>
            {
                FlowRow(LogisticsFlowPresentation.FlowRowKind.Delivered, "cyc-1", 1, 1200.0, 7,
                    Manifest(("Oxidizer", 30.0)), null, 0u, 0f, 0),
                FlowRow(LogisticsFlowPresentation.FlowRowKind.Delivered, "cyc-1", 0, 1100.0, 3,
                    Manifest(("LiquidFuel", 150.0)), null, 0u, 0f, 0),
            };

            List<LogisticsFlowPresentation.CycleFlowLine> lines =
                LogisticsFlowPresentation.FormatPerCycleFlow(
                    rows, null, null, new[] { "Base A", "Base B" }, 1200.0, 5);

            Assert.Single(lines);
            string text = lines[0].Text;
            int aIdx = text.IndexOf("delivered 150.0 LiquidFuel to Base A", StringComparison.Ordinal);
            int bIdx = text.IndexOf("delivered 30.0 Oxidizer to Base B", StringComparison.Ordinal);
            Assert.True(aIdx >= 0, "stop 0 delivery missing: " + text);
            Assert.True(bIdx > aIdx, "stop 1 should follow stop 0 (firing order): " + text);
        }

        [Fact]
        public void FormatPerCycleFlow_DeliveryShortfall_FlagsAndAnnotates()
        {
            var rows = new List<LogisticsFlowPresentation.FlowRow>
            {
                FlowRow(LogisticsFlowPresentation.FlowRowKind.Delivered, "cyc-1", 0, 1000.0, 3,
                    Manifest(("LiquidFuel", 40.0)), Manifest(("LiquidFuel", 150.0)), 0u, 0f, 0),
            };

            List<LogisticsFlowPresentation.CycleFlowLine> lines =
                LogisticsFlowPresentation.FormatPerCycleFlow(
                    rows, null, null, new[] { "Munar Station" }, 1000.0, 5);

            Assert.Single(lines);
            Assert.True(lines[0].Shortfall);
            Assert.Contains("delivered 40.0 of 150.0 LiquidFuel to Munar Station", lines[0].Text);
            Assert.Contains("(some cargo did not fit)", lines[0].Text);
        }

        [Fact]
        public void FormatPerCycleFlow_DebitShortfall_MarksOriginShort()
        {
            // Requested-only resource (fully blocked) also renders, via the
            // actual+requested key union.
            var rows = new List<LogisticsFlowPresentation.FlowRow>
            {
                FlowRow(LogisticsFlowPresentation.FlowRowKind.Debited, "cyc-1", 0, 1000.0, 1,
                    Manifest(("Ore", 40.0)), Manifest(("Ore", 150.0)), 7u, 0f, 0),
            };

            List<LogisticsFlowPresentation.CycleFlowLine> lines =
                LogisticsFlowPresentation.FormatPerCycleFlow(
                    rows, null, null, null, 1000.0, 5);

            Assert.Single(lines);
            Assert.True(lines[0].Shortfall);
            Assert.Contains("took 40.0 of 150.0 Ore from vessel pid=7", lines[0].Text);
            Assert.Contains("(origin was short)", lines[0].Text);
        }

        [Fact]
        public void FormatPerCycleFlow_InventoryOnlyPickupShortfall_FlagsAndAnnotates()
        {
            // Inventory-only pickup that came up short: 1 of 3 requested
            // stored-part payloads moved, no resources involved. The cycle line
            // must carry the shortfall tint flag and the "(source was short)"
            // annotation, mirroring the resource shortfall path.
            var rows = new List<LogisticsFlowPresentation.FlowRow>
            {
                FlowRow(LogisticsFlowPresentation.FlowRowKind.PickedUp, "cyc-1", 0, 1000.0, 2,
                    null, null, 42u, 0f, 1, 3),
            };
            var names = new Dictionary<uint, string> { [42u] = "Parts Depot" };

            List<LogisticsFlowPresentation.CycleFlowLine> lines =
                LogisticsFlowPresentation.FormatPerCycleFlow(
                    rows, names, null, null, 1000.0, 5);

            Assert.Single(lines);
            Assert.True(lines[0].Shortfall);
            Assert.Contains("picked up 1 of 3 inventory item(s) from Parts Depot", lines[0].Text);
            Assert.Contains("(source was short)", lines[0].Text);
        }

        [Fact]
        public void FormatPerCycleFlow_FullyBlockedInventoryPickup_RendersZeroOfN()
        {
            // Fully blocked inventory pickup: nothing moved but 2 payloads were
            // requested. Must NOT read "picked up nothing" with no explanation.
            var rows = new List<LogisticsFlowPresentation.FlowRow>
            {
                FlowRow(LogisticsFlowPresentation.FlowRowKind.PickedUp, "cyc-1", 0, 1000.0, 2,
                    null, null, 42u, 0f, 0, 2),
            };
            var names = new Dictionary<uint, string> { [42u] = "Parts Depot" };

            List<LogisticsFlowPresentation.CycleFlowLine> lines =
                LogisticsFlowPresentation.FormatPerCycleFlow(
                    rows, names, null, null, 1000.0, 5);

            Assert.Single(lines);
            Assert.True(lines[0].Shortfall);
            Assert.Contains("picked up 0 of 2 inventory item(s) from Parts Depot", lines[0].Text);
            Assert.Contains("(source was short)", lines[0].Text);
            Assert.DoesNotContain("nothing", lines[0].Text);
        }

        [Fact]
        public void FormatPerCycleFlow_InventoryDebitShortfall_MarksOriginShort()
        {
            var rows = new List<LogisticsFlowPresentation.FlowRow>
            {
                FlowRow(LogisticsFlowPresentation.FlowRowKind.Debited, "cyc-1", 0, 1000.0, 1,
                    null, null, 7u, 0f, 1, 2),
            };
            var names = new Dictionary<uint, string> { [7u] = "Minmus Depot" };

            List<LogisticsFlowPresentation.CycleFlowLine> lines =
                LogisticsFlowPresentation.FormatPerCycleFlow(
                    rows, names, null, null, 1000.0, 5);

            Assert.Single(lines);
            Assert.True(lines[0].Shortfall);
            Assert.Contains("took 1 of 2 inventory item(s) from Minmus Depot", lines[0].Text);
            Assert.Contains("(origin was short)", lines[0].Text);
        }

        [Fact]
        public void FormatPerCycleFlow_QuantityLevelInventoryShortfall_EqualCounts_StillFlags()
        {
            // A quantity-level shortfall within ONE identity: the requested
            // manifest (populated only on shortfall) has the same ENTRY count
            // as the actual, so no "K of N" rendering, but the presence of the
            // requested manifest alone must still flag + annotate the line.
            var rows = new List<LogisticsFlowPresentation.FlowRow>
            {
                FlowRow(LogisticsFlowPresentation.FlowRowKind.PickedUp, "cyc-1", 0, 1000.0, 2,
                    null, null, 42u, 0f, 1, 1),
            };
            var names = new Dictionary<uint, string> { [42u] = "Parts Depot" };

            List<LogisticsFlowPresentation.CycleFlowLine> lines =
                LogisticsFlowPresentation.FormatPerCycleFlow(
                    rows, names, null, null, 1000.0, 5);

            Assert.Single(lines);
            Assert.True(lines[0].Shortfall);
            Assert.Contains("picked up 1 inventory item(s) from Parts Depot", lines[0].Text);
            Assert.DoesNotContain(" of ", lines[0].Text);
            Assert.Contains("(source was short)", lines[0].Text);
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

        [Fact]
        public void FormatPerCycleFlow_LastNBounding_NewestFirst()
        {
            var rows = new List<LogisticsFlowPresentation.FlowRow>();
            for (int c = 1; c <= 7; c++)
            {
                rows.Add(FlowRow(LogisticsFlowPresentation.FlowRowKind.Delivered,
                    "cyc-" + c, 0, c * 1000.0, 3,
                    Manifest(("LiquidFuel", 10.0)), null, 0u, 0f, 0));
            }

            List<LogisticsFlowPresentation.CycleFlowLine> lines =
                LogisticsFlowPresentation.FormatPerCycleFlow(
                    rows, null, null, new[] { "Base" }, 8000.0, 5);

            // Bounded to the last 5, newest first, ordinals preserved (7..3);
            // cycles 1 and 2 are simply not shown.
            Assert.Equal(5, lines.Count);
            Assert.StartsWith("Cycle 7", lines[0].Text, StringComparison.Ordinal);
            Assert.StartsWith("Cycle 3", lines[4].Text, StringComparison.Ordinal);
            Assert.DoesNotContain(lines, l => l.Text.StartsWith("Cycle 2", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.Text.StartsWith("Cycle 1 ", StringComparison.Ordinal));
        }

        [Fact]
        public void FormatPerCycleFlow_VanishedEndpoint_PidFallbackNeverBlank()
        {
            var rows = new List<LogisticsFlowPresentation.FlowRow>
            {
                FlowRow(LogisticsFlowPresentation.FlowRowKind.PickedUp, "cyc-1", 0, 1000.0, 2,
                    Manifest(("Ore", 20.0)), null, 99u, 0f, 0),
            };

            // No name map at all: the pid renders as the identity fallback.
            List<LogisticsFlowPresentation.CycleFlowLine> lines =
                LogisticsFlowPresentation.FormatPerCycleFlow(
                    rows, null, null, null, 1000.0, 5);

            Assert.Single(lines);
            Assert.Contains("picked up 20.0 Ore from vessel pid=99", lines[0].Text);
        }

        [Fact]
        public void FormatPerCycleFlow_LegacyStopIndex_FallsBackToStopZeroName()
        {
            var rows = new List<LogisticsFlowPresentation.FlowRow>
            {
                FlowRow(LogisticsFlowPresentation.FlowRowKind.Delivered, "cyc-1", -1, 1000.0, 0,
                    Manifest(("LiquidFuel", 150.0)), null, 0u, 0f, 0),
            };

            List<LogisticsFlowPresentation.CycleFlowLine> lines =
                LogisticsFlowPresentation.FormatPerCycleFlow(
                    rows, null, null, new[] { "Munar Station" }, 1000.0, 5);

            Assert.Single(lines);
            Assert.Contains("to Munar Station", lines[0].Text);
        }

        [Fact]
        public void FormatPerCycleFlow_NoCycleScopedRows_ReturnsEmpty()
        {
            // Rows without a cycle id are skipped; an empty result means the
            // caller renders nothing (no header).
            var rows = new List<LogisticsFlowPresentation.FlowRow>
            {
                FlowRow(LogisticsFlowPresentation.FlowRowKind.Delivered, null, 0, 1000.0, 3,
                    Manifest(("LiquidFuel", 150.0)), null, 0u, 0f, 0),
            };

            Assert.Empty(LogisticsFlowPresentation.FormatPerCycleFlow(
                rows, null, null, null, 1000.0, 5));
            Assert.Empty(LogisticsFlowPresentation.FormatPerCycleFlow(
                new List<LogisticsFlowPresentation.FlowRow>(), null, null, null, 1000.0, 5));
            Assert.Empty(LogisticsFlowPresentation.FormatPerCycleFlow(
                null, null, null, null, 1000.0, 5));
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
