using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The route event log (<see cref="RouteEventLog"/>): its persistence with the route store
    /// in the PARSEK scenario node, the walk summary the harness pins as <c>PostWalk: routes=</c>,
    /// and the in-place mutators every reader and load path goes through.
    /// </summary>
    [Collection("Sequential")]
    public class RouteEventLogTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteEventLogTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RouteEvent Ev(RouteEventType type, double ut, string routeId = "route-a",
            string cycle = "cycle-0", uint originPid = 0u)
        {
            return new RouteEvent
            {
                Type = type,
                UT = ut,
                RouteId = routeId,
                RouteCycleId = cycle,
                RouteStopIndex = type == RouteEventType.RouteCargoDelivered ? 0 : -1,
                RouteOriginVesselPid = originPid,
            };
        }

        // ---- persistence with the route store ----

        [Fact]
        public void SaveAndLoad_RoundTripsThroughTheRouteStoreNode()
        {
            RouteEventLog.Add(Ev(RouteEventType.RouteDispatched, 1000.0));
            RouteEventLog.Add(new RouteEvent
            {
                Type = RouteEventType.RouteCargoDelivered,
                UT = 1300.5,
                RouteId = "route-a",
                RouteCycleId = "cycle-0",
                RouteStopIndex = 0,
                RouteResourceManifest = new Dictionary<string, double> { { "LiquidFuel", 42.25 } },
            });
            RouteEventLog.Add(Ev(RouteEventType.RoutePaused, 1400.0, routeId: "route-b"));

            var parsek = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(parsek);
            ConfigNode logNode = parsek.GetNode(RouteEventLog.NodeName);
            Assert.NotNull(logNode);
            Assert.Equal(3, logNode.GetNodes(RouteEvent.NodeName).Length);

            RouteEventLog.ResetForTesting();
            logLines.Clear();
            RouteStore.LoadRoutesFrom(parsek);

            Assert.Equal(3, RouteEventLog.Events.Count);
            Assert.Equal(new[] { RouteEventType.RouteDispatched, RouteEventType.RouteCargoDelivered, RouteEventType.RoutePaused },
                RouteEventLog.Events.Select(e => e.Type).ToArray());
            Assert.Equal(42.25, RouteEventLog.Events[1].RouteResourceManifest["LiquidFuel"]);
            Assert.Equal("route-b", RouteEventLog.Events[2].RouteId);
            Assert.Contains(logLines, l => l.Contains("[RouteEventLog]")
                && l.Contains("Load: route event log loaded events=3 routes=2 dropped=0 node=present"));
            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("PostWalk: routes=2, paused=1, endpointLost=0, totalDispatched=1, totalDelivered=1"));
        }

        [Fact]
        public void Save_EmptyLog_WritesNoNode_AndLoadEmptiesMemory()
        {
            var parsek = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(parsek);
            Assert.Null(parsek.GetNode(RouteEventLog.NodeName));

            // Memory holds events the loaded save does not: a cold load replaces them.
            RouteEventLog.Add(Ev(RouteEventType.RouteDispatched, 1000.0));
            logLines.Clear();
            RouteStore.LoadRoutesFrom(parsek);

            Assert.Empty(RouteEventLog.Events);
            Assert.Contains(logLines, l => l.Contains("[RouteEventLog]")
                && l.Contains("loaded events=0 routes=0 dropped=0 node=absent"));
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("PostWalk: routes=0"));
        }

        [Fact]
        public void Save_StripsAStaleLogNode()
        {
            var parsek = new ConfigNode("SCENARIO");
            parsek.AddNode(RouteEventLog.NodeName).AddNode(RouteEvent.NodeName);
            RouteEventLog.SaveInto(parsek);
            Assert.Null(parsek.GetNode(RouteEventLog.NodeName));

            RouteEventLog.Add(Ev(RouteEventType.RouteDispatched, 1000.0));
            parsek.AddNode(RouteEventLog.NodeName);
            RouteEventLog.SaveInto(parsek);
            Assert.Single(parsek.GetNodes(RouteEventLog.NodeName));
        }

        [Fact]
        public void Load_DropsAnUnreadableEvent_WithOneWarn_KeepsTheRest()
        {
            var parsek = new ConfigNode("SCENARIO");
            ConfigNode logNode = parsek.AddNode(RouteEventLog.NodeName);
            Ev(RouteEventType.RouteDispatched, 1000.0).SerializeInto(logNode);
            ConfigNode bad = logNode.AddNode(RouteEvent.NodeName);
            bad.AddValue("type", "NotARouteEvent");
            bad.AddValue("ut", "1100");
            bad.AddValue("routeId", "route-a");
            ConfigNode noRoute = logNode.AddNode(RouteEvent.NodeName);
            noRoute.AddValue("type", "RouteDispatched");
            noRoute.AddValue("ut", "1200");

            int loaded = RouteEventLog.LoadFrom(parsek);

            Assert.Equal(1, loaded);
            Assert.Single(RouteEventLog.Events);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("[RouteEventLog]")
                && l.Contains("Load: dropped 2 unreadable route event(s)")
                && l.Contains("first reason=unknown-type:NotARouteEvent"));
        }

        [Fact]
        public void Load_NullNode_EmptiesTheLog()
        {
            RouteEventLog.Add(Ev(RouteEventType.RouteDispatched, 1000.0));
            Assert.Equal(0, RouteEventLog.LoadFrom(null));
            Assert.Empty(RouteEventLog.Events);
        }

        [Fact]
        public void TheLedgerFileNeverCarriesRouteEvents()
        {
            RouteEventLog.Add(Ev(RouteEventType.RouteDispatched, 1000.0));
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "parsek-route-event-log-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string path = System.IO.Path.Combine(dir, "ledger.pgld");
                Assert.True(Ledger.SaveToFile(path));
                string text = System.IO.File.ReadAllText(path);
                Assert.DoesNotContain("RouteDispatched", text);
                Assert.DoesNotContain(RouteEventLog.NodeName, text);
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, true); } catch { }
            }
        }

        // ---- mutators ----

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Add_RefusesAnEventWithNoRouteId(string routeId)
        {
            int version = RouteEventLog.StateVersion;
            RouteEventLog.Add(new RouteEvent { Type = RouteEventType.RouteDispatched, UT = 1.0, RouteId = routeId });
            RouteEventLog.Add(null);

            Assert.Empty(RouteEventLog.Events);
            Assert.Equal(version, RouteEventLog.StateVersion);
            Assert.Equal(2, logLines.Count(l => l.Contains("[WARN]") && l.Contains("Add: refused an event with no route id")));
        }

        [Fact]
        public void Add_AppendsInWriteOrder_AndBumpsTheVersion()
        {
            int version = RouteEventLog.StateVersion;
            RouteEventLog.AddRange(new[]
            {
                Ev(RouteEventType.RouteDispatched, 2000.0),
                Ev(RouteEventType.RouteCargoDebited, 1000.0),
            });

            Assert.Equal(new[] { 2000.0, 1000.0 }, RouteEventLog.Events.Select(e => e.UT).ToArray());
            Assert.NotEqual(version, RouteEventLog.StateVersion);
        }

        [Fact]
        public void ReplaceAll_TakesTheKeptList_SkippingRoutelessEntries()
        {
            RouteEventLog.Add(Ev(RouteEventType.RouteDispatched, 1000.0));
            var kept = new List<RouteEvent>
            {
                Ev(RouteEventType.RouteCargoDelivered, 1100.0, routeId: "route-b"),
                null,
                new RouteEvent { Type = RouteEventType.RouteDispatched, UT = 1200.0, RouteId = null },
            };

            RouteEventLog.ReplaceAll(kept, "test");

            Assert.Equal("route-b", Assert.Single(RouteEventLog.Events).RouteId);
            Assert.Contains(logLines, l => l.Contains("ReplaceAll (test): events 1 -> 1"));

            RouteEventLog.ReplaceAll(null, "empty");
            Assert.Empty(RouteEventLog.Events);
        }

        [Fact]
        public void RemoveWhere_RemovesTheSelectedEvents_AndBumpsOnlyWhenItRemoves()
        {
            RouteEventLog.Add(Ev(RouteEventType.RouteDispatched, 1000.0, routeId: "keep"));
            RouteEventLog.Add(Ev(RouteEventType.RouteDispatched, 1100.0, routeId: "drop"));
            RouteEventLog.Add(Ev(RouteEventType.RouteCargoDelivered, 1200.0, routeId: "drop"));
            int version = RouteEventLog.StateVersion;

            Assert.Equal(2, RouteEventLog.RemoveWhere(e => e.RouteId == "drop", "cleanup"));
            Assert.Equal("keep", Assert.Single(RouteEventLog.Events).RouteId);
            Assert.NotEqual(version, RouteEventLog.StateVersion);

            version = RouteEventLog.StateVersion;
            Assert.Equal(0, RouteEventLog.RemoveWhere(e => e.RouteId == "drop", "cleanup"));
            Assert.Equal(version, RouteEventLog.StateVersion);
            Assert.Equal(0, RouteEventLog.RemoveWhere(null, "cleanup"));
        }

        [Fact]
        public void RetireAtRewind_NothingAfterTheCutoff_KeepsTheVersion_AndLogsVerbose()
        {
            RouteEventLog.Add(Ev(RouteEventType.RouteDispatched, 1000.0));
            int version = RouteEventLog.StateVersion;

            int retired = RouteEventLog.RetireFutureRouteActionsAtRewind(1000.0, out List<RouteEvent> kept);

            Assert.Equal(0, retired);
            Assert.Single(kept);
            Assert.Equal(version, RouteEventLog.StateVersion);
            Assert.Contains(logLines, l => l.Contains("RetireFutureRouteActionsAtRewind: no free-standing route actions after UT 1000"));
        }

        [Fact]
        public void RetireAtRewind_HandsBackAnIndependentCopy()
        {
            RouteEventLog.Add(Ev(RouteEventType.RouteDispatched, 1000.0));
            RouteEventLog.Add(Ev(RouteEventType.RouteDispatched, 2000.0, cycle: "cycle-1"));

            int retired = RouteEventLog.RetireFutureRouteActionsAtRewind(1500.0, out List<RouteEvent> kept);

            Assert.Equal(1, retired);
            kept.Clear();
            Assert.Single(RouteEventLog.Events);
            Assert.False(RouteEventLog.HasEventsAfterUT(1500.0));
            Assert.Contains(logLines, l => l.Contains("[INFO]") && l.Contains("[RouteEventLog]")
                && l.Contains("RetireFutureRouteActionsAtRewind: removed 1 free-standing route action(s) after UT 1500"));
        }

        // ---- walk summary ----

        [Fact]
        public void WalkSummary_CountsRoutesPausesLostEndpointsAndPhysicalMoves()
        {
            var log = new List<RouteEvent>
            {
                Ev(RouteEventType.RouteDispatched, 100.0, routeId: "a"),
                Ev(RouteEventType.RouteCargoDebited, 100.0, routeId: "a", originPid: 7u),
                Ev(RouteEventType.RouteCargoDebited, 100.0, routeId: "a"),         // KSC debit: not physical
                Ev(RouteEventType.RouteCargoDelivered, 400.0, routeId: "a"),
                Ev(RouteEventType.RoutePaused, 500.0, routeId: "a"),
                Ev(RouteEventType.RouteResumed, 600.0, routeId: "a"),             // a ends resumed
                Ev(RouteEventType.RouteCargoPickedUp, 100.0, routeId: "b", originPid: 9u),
                Ev(RouteEventType.RoutePaused, 700.0, routeId: "b"),              // b ends paused
                Ev(RouteEventType.RouteEndpointLost, 800.0, routeId: "c"),
                Ev(RouteEventType.RouteEndpointLost, 900.0, routeId: "c"),        // counted once
                Ev(RouteEventType.RouteHeld, 950.0, routeId: "d"),
                null,
                new RouteEvent { Type = RouteEventType.RouteDispatched, UT = 1.0, RouteId = "" },
            };

            RouteEventLog.ComputeWalkSummary(log, out int routes, out int paused, out int endpointLost,
                out int dispatched, out int delivered, out int physicalDebits, out int physicalPickups);

            Assert.Equal(4, routes);
            Assert.Equal(1, paused);
            Assert.Equal(1, endpointLost);
            Assert.Equal(1, dispatched);
            Assert.Equal(1, delivered);
            Assert.Equal(1, physicalDebits);
            Assert.Equal(1, physicalPickups);
        }

        [Fact]
        public void WalkSummary_NullOrEmpty_IsAllZero()
        {
            RouteEventLog.ComputeWalkSummary(null, out int routes, out int paused, out int endpointLost,
                out int dispatched, out int delivered, out int physicalDebits, out int physicalPickups);
            Assert.Equal(0, routes + paused + endpointLost + dispatched + delivered + physicalDebits + physicalPickups);

            RouteEventLog.LogWalkSummary();
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("PostWalk: routes=0, paused=0"));
        }
    }
}
