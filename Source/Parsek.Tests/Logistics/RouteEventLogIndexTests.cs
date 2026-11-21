using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The route event log's per-route index (todo ROUTE-TICK-EVENT-LOG-SCANS): after every way
    /// the log changes (append, append range, refused append, replace, rewind retire, window
    /// removal, removal, cold load, reset), <see cref="RouteEventLog.RowsForRoute"/> and
    /// <see cref="RouteEventLog.RowsForCycle"/> return exactly the rows a full scan of
    /// <see cref="RouteEventLog.Events"/> filtered by ordinal route id (and cycle id) returns,
    /// in the same order, and <see cref="RouteEventLog.IndexMatchesFullScanForTesting"/> agrees.
    /// Every replay guard and deficit read filters its rows by route and cycle itself, so equal
    /// rows in equal order is what makes it decide exactly as the full scan did.
    /// </summary>
    [Collection("Sequential")]
    public class RouteEventLogIndexTests : IDisposable
    {
        private static readonly string[] RouteIds = { "route-a", "route-b", "route-c" };
        private static readonly string[] CycleIds = { null, "", "cycle-0", "cycle-1", "cycle-2" };

        public RouteEventLogIndexTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RouteEventLog.ResetForTesting();
        }

        public void Dispose()
        {
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RouteEventLog.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RouteEvent Ev(RouteEventType type, double ut, string routeId, string cycle, int stop = -1)
        {
            return new RouteEvent
            {
                Type = type,
                UT = ut,
                RouteId = routeId,
                RouteCycleId = cycle,
                RouteStopIndex = stop,
            };
        }

        private static List<RouteEvent> RandomEvents(Random rng, int count, double fromUT)
        {
            var types = (RouteEventType[])Enum.GetValues(typeof(RouteEventType));
            var list = new List<RouteEvent>();
            double ut = fromUT;
            for (int i = 0; i < count; i++)
            {
                ut += rng.Next(0, 3) * 5.0;
                list.Add(Ev(types[rng.Next(types.Length)], ut,
                    RouteIds[rng.Next(RouteIds.Length)], CycleIds[rng.Next(CycleIds.Length)],
                    rng.Next(-1, 3)));
            }
            return list;
        }

        // The full scan every reader ran before the index, and the index's answer, side by side.
        private static void AssertIndexEqualsFullScan(string step)
        {
            Assert.True(RouteEventLog.IndexMatchesFullScanForTesting(out string mismatch), step + ": " + mismatch);
            IReadOnlyList<RouteEvent> all = RouteEventLog.Events;
            foreach (string routeId in RouteIds.Concat(new[] { "route-unknown", "", null }))
            {
                var expected = all.Where(e => string.Equals(e.RouteId, routeId, StringComparison.Ordinal)).ToList();
                AssertSameRows(expected, RouteEventLog.RowsForRoute(routeId), step + " route=" + (routeId ?? "(null)"));
                foreach (string cycleId in CycleIds.Concat(new[] { "cycle-unknown" }))
                {
                    // Every reader refuses an empty route or cycle id before it scans.
                    var expectedCycle = string.IsNullOrEmpty(routeId) || string.IsNullOrEmpty(cycleId)
                        ? new List<RouteEvent>()
                        : expected.Where(e => string.Equals(e.RouteCycleId, cycleId, StringComparison.Ordinal)).ToList();
                    AssertSameRows(expectedCycle, RouteEventLog.RowsForCycle(routeId, cycleId),
                        step + " route=" + (routeId ?? "(null)") + " cycle=" + (cycleId ?? "(null)"));
                }
            }
        }

        private static void AssertSameRows(IList<RouteEvent> expected, IReadOnlyList<RouteEvent> actual, string what)
        {
            Assert.True(expected.Count == actual.Count,
                $"{what}: index has {actual.Count} rows, the scan {expected.Count}");
            for (int i = 0; i < expected.Count; i++)
                Assert.True(ReferenceEquals(expected[i], actual[i]), $"{what}: row {i} differs");
        }

        // catches: a mutator of the route event log that forgets the index (the index then
        // answers from rows the log no longer holds, or misses rows it gained), or a rebuild
        // that reorders rows (the open-hold-episode walk breaks UT ties by log order).
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void EveryMutator_KeepsTheIndexEqualToAFullScan(int seed)
        {
            var rng = new Random(seed);
            AssertIndexEqualsFullScan("empty");

            foreach (RouteEvent e in RandomEvents(rng, 60, 1000.0))
                RouteEventLog.Add(e);
            AssertIndexEqualsFullScan("Add");

            RouteEventLog.AddRange(RandomEvents(rng, 20, 1400.0));
            AssertIndexEqualsFullScan("AddRange");

            RouteEventLog.Add(null);
            RouteEventLog.Add(Ev(RouteEventType.RouteDispatched, 1500.0, null, "cycle-0"));
            RouteEventLog.Add(Ev(RouteEventType.RouteDispatched, 1500.0, "", "cycle-0"));
            AssertIndexEqualsFullScan("refused Add");

            double cutoff = RouteEventLog.Events[RouteEventLog.Events.Count / 2].UT;
            RouteEventLog.RetireFutureRouteActionsAtRewind(cutoff, out List<RouteEvent> kept);
            AssertIndexEqualsFullScan("RetireFutureRouteActionsAtRewind");
            Assert.Equal(kept.Count, RouteEventLog.Events.Count);

            RouteEventLog.RetireFutureRouteActionsAtRewind(1e9, out _);
            AssertIndexEqualsFullScan("RetireFutureRouteActionsAtRewind (nothing retired)");

            foreach (RouteEvent e in RandomEvents(rng, 30, cutoff))
                RouteEventLog.Add(e);
            double saveUT = cutoff;
            double floorUT = cutoff + 40.0;
            int windowRemoved = RouteEventLog.RetireUnheldWindowRows(saveUT, floorUT, new List<RouteEvent>(), out _);
            AssertIndexEqualsFullScan("RetireUnheldWindowRows (" + windowRemoved + " removed)");

            int removed = RouteEventLog.RemoveWhere(e => e.RouteId == "route-b", "test");
            Assert.True(removed > 0, "the random log held no route-b row");
            AssertIndexEqualsFullScan("RemoveWhere");
            Assert.Empty(RouteEventLog.RowsForRoute("route-b"));

            RouteEventLog.RemoveWhere(e => false, "test (nothing)");
            AssertIndexEqualsFullScan("RemoveWhere (nothing removed)");

            var replacement = RandomEvents(rng, 40, 3000.0);
            replacement.Insert(5, null);
            RouteEventLog.ReplaceAll(replacement, "test");
            AssertIndexEqualsFullScan("ReplaceAll");

            var parsek = new ConfigNode("SCENARIO");
            RouteEventLog.SaveInto(parsek);
            RouteEventLog.ReplaceAll(RandomEvents(rng, 10, 9000.0), "test (before load)");
            RouteEventLog.LoadFrom(parsek);
            AssertIndexEqualsFullScan("LoadFrom");
            Assert.NotEmpty(RouteEventLog.Events);

            RouteEventLog.LoadFrom(new ConfigNode("SCENARIO"));
            AssertIndexEqualsFullScan("LoadFrom (no log)");
            Assert.Empty(RouteEventLog.Events);

            foreach (RouteEvent e in RandomEvents(rng, 10, 100.0))
                RouteEventLog.Add(e);
            RouteEventLog.ResetForTesting();
            AssertIndexEqualsFullScan("ResetForTesting");
        }

        // catches: the consistency check reading green on a desynced index (it must name the
        // first difference), so the fuzzer's per-step check cannot pass vacuously.
        [Fact]
        public void ConsistencyCheck_ReportsAnIndexThatMissesARow()
        {
            RouteEventLog.Add(Ev(RouteEventType.RouteDispatched, 1000.0, "route-a", "cycle-0"));
            RouteEventLog.Add(Ev(RouteEventType.RouteCargoDelivered, 1010.0, "route-a", "cycle-0", 0));
            Assert.True(RouteEventLog.IndexMatchesFullScanForTesting(out _));

            // Append behind the index's back, as a mutator that forgot it would.
            var events = (List<RouteEvent>)typeof(RouteEventLog)
                .GetField("events", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            events.Add(Ev(RouteEventType.RouteCargoDebited, 1020.0, "route-a", "cycle-0"));

            Assert.False(RouteEventLog.IndexMatchesFullScanForTesting(out string mismatch));
            Assert.Contains("route-a", mismatch);
        }

        // catches: the replay guards reading the index wrong: a dispatch, delivery or pickup row
        // of another route, another cycle or another stop suppressing a window it does not own
        // (or the guard missing its own row).
        [Fact]
        public void ReplayGuards_MatchOnlyTheirOwnRouteCycleAndStop()
        {
            RouteEventLog.Add(Ev(RouteEventType.RouteDispatched, 1000.0, "route-a", "cycle-0", -1));
            RouteEventLog.Add(Ev(RouteEventType.RouteCargoDelivered, 1010.0, "route-a", "cycle-0", 1));
            RouteEventLog.Add(Ev(RouteEventType.RouteCargoPickedUp, 1020.0, "route-b", "cycle-3", 2));

            Assert.True(RouteOrchestrator.IsDispatchAlreadyLogged("route-a", "cycle-0", -1));
            Assert.False(RouteOrchestrator.IsDispatchAlreadyLogged("route-a", "cycle-0", 0));
            Assert.False(RouteOrchestrator.IsDispatchAlreadyLogged("route-a", "cycle-1", -1));
            Assert.False(RouteOrchestrator.IsDispatchAlreadyLogged("route-b", "cycle-0", -1));

            Assert.True(RouteOrchestrator.IsDeliveryAlreadyLogged("route-a", "cycle-0", 1));
            Assert.False(RouteOrchestrator.IsDeliveryAlreadyLogged("route-a", "cycle-0", 0));
            Assert.False(RouteOrchestrator.IsDeliveryAlreadyLogged("route-b", "cycle-0", 1));

            Assert.True(RouteOrchestrator.IsPickupAlreadyLogged("route-b", "cycle-3", 2));
            Assert.False(RouteOrchestrator.IsPickupAlreadyLogged("route-b", "cycle-3", 1));
            Assert.False(RouteOrchestrator.IsPickupAlreadyLogged("route-a", "cycle-3", 2));

            Assert.False(RouteOrchestrator.IsDispatchAlreadyLogged(null, "cycle-0", -1));
            Assert.False(RouteOrchestrator.IsDispatchAlreadyLogged("route-a", null, -1));
            Assert.False(RouteOrchestrator.IsDeliveryAlreadyLogged("route-a", "", 1));

            // After a rewind retire drops the delivery, the guard no longer sees it.
            RouteEventLog.RetireFutureRouteActionsAtRewind(1005.0, out _);
            Assert.True(RouteOrchestrator.IsDispatchAlreadyLogged("route-a", "cycle-0", -1));
            Assert.False(RouteOrchestrator.IsDeliveryAlreadyLogged("route-a", "cycle-0", 1));
            Assert.False(RouteOrchestrator.IsPickupAlreadyLogged("route-b", "cycle-3", 2));
        }

        // catches: the per-cycle supply deficit or the open-hold-episode walk deciding
        // differently on the index's rows than on the whole log (a row of the cycle missing
        // from the index, or rows handed over out of log order, which both walks use to break
        // Sequence and UT ties).
        [Theory]
        [InlineData(11)]
        [InlineData(12)]
        [InlineData(13)]
        [InlineData(14)]
        public void DeficitAndHoldReads_DecideTheSameOnTheIndexRows(int seed)
        {
            var rng = new Random(seed);
            const string LF = "LiquidFuel";
            var kinds = new[]
            {
                RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                RouteDispatchEvaluator.EligibilityFailureKind.EndpointLost,
                RouteDispatchEvaluator.EligibilityFailureKind.SourcesStale,
            };
            var details = new[] { null, "LiquidFuel", "Oxidizer" };
            var types = new[]
            {
                RouteEventType.RouteCargoDebited, RouteEventType.RouteCargoPickedUp,
                RouteEventType.RouteCargoDelivered, RouteEventType.RouteHeld,
            };
            // The rows that close a hold episode, rare so most episodes hold several reasons.
            var boundaries = new[]
            {
                RouteEventType.RouteDispatched, RouteEventType.RoutePaused,
                RouteEventType.RouteResumed, RouteEventType.RouteEndpointLost,
            };
            for (int i = 0; i < 400; i++)
            {
                RouteEventType type = rng.Next(25) == 0
                    ? boundaries[rng.Next(boundaries.Length)]
                    : types[rng.Next(types.Length)];
                var e = Ev(type, 1000.0 + rng.Next(0, 40) * 10.0, RouteIds[rng.Next(2)],
                    CycleIds[2 + rng.Next(3)], rng.Next(0, 2));
                e.Sequence = rng.Next(0, 12);
                e.RouteResourceManifest = new Dictionary<string, double> { { LF, rng.Next(0, 11) * 10.0 } };
                if (type != RouteEventType.RouteCargoDelivered && rng.Next(2) == 0)
                    e.RouteRequestedResourceManifest = new Dictionary<string, double> { { LF, 100.0 } };
                e.RouteHoldKind = kinds[rng.Next(kinds.Length)];
                e.RouteEndpointReason = details[rng.Next(details.Length)];
                RouteEventLog.Add(e);
            }

            int deficits = 0, held = 0;
            foreach (string routeId in RouteIds.Take(2))
            {
                var route = new Route
                {
                    Id = routeId,
                    Stops = new List<RouteStop>
                    {
                        new RouteStop { DeliveryManifest = new Dictionary<string, double> { { LF, 50.0 } } },
                        new RouteStop { DeliveryManifest = new Dictionary<string, double> { { LF, 50.0 } } },
                    },
                };
                foreach (string cycleId in CycleIds.Skip(2))
                {
                    for (int seq = 0; seq <= 12; seq++)
                    {
                        var full = RouteOrchestrator.ComputeCycleSupplyDeficit(route, cycleId, seq, RouteEventLog.Events);
                        var indexed = RouteOrchestrator.ComputeCycleSupplyDeficit(
                            route, cycleId, seq, RouteEventLog.RowsForCycle(routeId, cycleId));
                        Assert.Equal(full.OrderBy(kv => kv.Key), indexed.OrderBy(kv => kv.Key));
                        if (full.Count > 0) deficits++;
                    }
                }
                foreach (var kind in kinds)
                {
                    foreach (string detail in details)
                    {
                        bool full = RouteOrchestrator.IsHoldReasonRecordedInOpenEpisode(
                            RouteEventLog.Events, routeId, kind, detail);
                        bool indexed = RouteOrchestrator.IsHoldReasonRecordedInOpenEpisode(
                            RouteEventLog.RowsForRoute(routeId), routeId, kind, detail);
                        Assert.Equal(full, indexed);
                        if (full) held++;
                    }
                }
            }
            Assert.True(deficits > 0, "the random log produced no supply deficit to compare");
            Assert.True(held > 0, "the random log produced no recorded hold reason to compare");
        }

        // catches: the live delivery, hold and replay-guard reads going back to scanning the
        // whole career's log (or reading another cycle's rows) instead of the index.
        [Fact]
        public void LiveReaders_ReadTheIndexRows()
        {
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string Read(string rel) => System.IO.File.ReadAllText(System.IO.Path.Combine(root, rel));
            string delivery = Read("Source/Parsek/Logistics/RouteOrchestrator.Delivery.cs");
            Assert.Contains("stopIndex * SeqStride + 3, RouteEventLog.RowsForCycle(route.Id, cycleId));", delivery);
            Assert.Equal(3, CountOf(delivery, "RouteEventLog.RowsForCycle(routeId, cycleId);"));
            Assert.Contains("RouteEventLog.RowsForCycle(route.Id, cycleId);", delivery);
            Assert.DoesNotContain("= RouteEventLog.Events;", delivery);
            string holds = Read("Source/Parsek/Logistics/RouteOrchestrator.Holds.cs");
            Assert.Contains("els = RouteEventLog.RowsForRoute(route.Id);", holds);
        }

        private static int CountOf(string text, string needle)
        {
            int n = 0;
            for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;
            return n;
        }

        // catches: the full-scan seam not handing back the whole log (the fuzzer's with/without
        // comparison would then compare the index with itself).
        [Fact]
        public void IndexDisabledSeam_HandsBackTheWholeLog()
        {
            RouteEventLog.Add(Ev(RouteEventType.RouteDispatched, 1000.0, "route-a", "cycle-0"));
            RouteEventLog.Add(Ev(RouteEventType.RouteDispatched, 1010.0, "route-b", "cycle-1"));
            RouteEventLog.IndexDisabledForTesting = true;
            Assert.Same(RouteEventLog.Events, RouteEventLog.RowsForRoute("route-a"));
            Assert.Same(RouteEventLog.Events, RouteEventLog.RowsForCycle("route-a", "cycle-0"));
            RouteEventLog.IndexDisabledForTesting = false;
            Assert.Single(RouteEventLog.RowsForRoute("route-a"));
        }
    }
}
