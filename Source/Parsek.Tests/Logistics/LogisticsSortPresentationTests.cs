using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the pure L2 route sort comparer
    /// <see cref="LogisticsSortPresentation.SortRoutes"/>. The comparer reads only
    /// <see cref="Route"/> scalar fields (Name / DispatchInterval / CompletedCycles) and
    /// the injected per-route <see cref="RouteSortKeys"/> (Origin / Destination /
    /// NextDelivery / Status / Delivery), never Unity / the orchestrator / the ledger,
    /// so it is exercised directly. Mirrors
    /// <c>SpawnControlPresentationTests.SortCandidates_*</c>: per-column ascending /
    /// descending order, the descending result is the reverse of ascending, the input
    /// list is not mutated, and null routes / null names are tolerated.
    /// </summary>
    public class LogisticsSortPresentationTests
    {
        // Three routes with distinct Name / Interval / Cycles so every column has a
        // strict ordering, plus a keys dictionary giving each a distinct Origin /
        // Destination / NextDelivery / Status / Delivery sort value.
        private static (List<Route> routes, Dictionary<string, RouteSortKeys> keys) BuildFixture()
        {
            Route a = new RouteFixtureBuilder()
                .WithId("ra").WithName("Alpha")
                .WithSchedule(transitDurationSeconds: 100.0, dispatchIntervalSeconds: 300.0)
                .WithCycleCounters(completed: 5, skipped: 0)
                .Build();
            Route b = new RouteFixtureBuilder()
                .WithId("rb").WithName("Bravo")
                .WithSchedule(transitDurationSeconds: 100.0, dispatchIntervalSeconds: 100.0)
                .WithCycleCounters(completed: 1, skipped: 0)
                .Build();
            Route c = new RouteFixtureBuilder()
                .WithId("rc").WithName("charlie")
                .WithSchedule(transitDurationSeconds: 100.0, dispatchIntervalSeconds: 200.0)
                .WithCycleCounters(completed: 9, skipped: 0)
                .Build();

            var keys = new Dictionary<string, RouteSortKeys>
            {
                ["ra"] = new RouteSortKeys
                {
                    OriginText = "KSC (funds)",
                    DestinationText = "Munar Station",
                    NextDeliverySeconds = 120.0,
                    HasNextDelivery = true,
                    StatusText = "Dispatching on schedule",
                    DeliveryText = "Delivering"
                },
                ["rb"] = new RouteSortKeys
                {
                    OriginText = "depot pid=7",
                    DestinationText = "Aurora Outpost",
                    NextDeliverySeconds = 30.0,
                    HasNextDelivery = true,
                    StatusText = "Paused - not auto-dispatching",
                    DeliveryText = "Paused"
                },
                ["rc"] = new RouteSortKeys
                {
                    OriginText = "Minmus depot",
                    DestinationText = "Zenith Lab",
                    NextDeliverySeconds = 0.0,
                    HasNextDelivery = false, // no countdown -> sorts last ascending
                    StatusText = "Ghost in transit",
                    DeliveryText = "Flying, not delivering"
                }
            };

            return (new List<Route> { a, b, c }, keys);
        }

        private static List<string> Ids(List<Route> routes)
        {
            var ids = new List<string>();
            for (int i = 0; i < routes.Count; i++)
                ids.Add(routes[i]?.Id ?? "<null>");
            return ids;
        }

        [Fact]
        public void SortRoutes_ByName_Ascending_IsCaseInsensitive()
        {
            (List<Route> routes, Dictionary<string, RouteSortKeys> keys) = BuildFixture();
            List<Route> sorted = LogisticsSortPresentation.SortRoutes(
                routes, LogisticsRouteSortColumn.Name, ascending: true, keys);
            // Alpha < Bravo < charlie (OrdinalIgnoreCase: "charlie" sorts after "Bravo").
            Assert.Equal(new List<string> { "ra", "rb", "rc" }, Ids(sorted));
        }

        [Fact]
        public void SortRoutes_ByName_Descending_ReversesAscending()
        {
            (List<Route> routes, Dictionary<string, RouteSortKeys> keys) = BuildFixture();
            List<Route> asc = LogisticsSortPresentation.SortRoutes(
                routes, LogisticsRouteSortColumn.Name, ascending: true, keys);
            List<Route> desc = LogisticsSortPresentation.SortRoutes(
                routes, LogisticsRouteSortColumn.Name, ascending: false, keys);
            asc.Reverse();
            Assert.Equal(Ids(asc), Ids(desc));
        }

        [Fact]
        public void SortRoutes_ByInterval_OrdersByDispatchInterval()
        {
            (List<Route> routes, Dictionary<string, RouteSortKeys> keys) = BuildFixture();
            List<Route> sorted = LogisticsSortPresentation.SortRoutes(
                routes, LogisticsRouteSortColumn.Interval, ascending: true, keys);
            // intervals: b=100 < c=200 < a=300
            Assert.Equal(new List<string> { "rb", "rc", "ra" }, Ids(sorted));
        }

        [Fact]
        public void SortRoutes_ByCycles_OrdersByCompletedCycles()
        {
            (List<Route> routes, Dictionary<string, RouteSortKeys> keys) = BuildFixture();
            List<Route> sorted = LogisticsSortPresentation.SortRoutes(
                routes, LogisticsRouteSortColumn.Cycles, ascending: true, keys);
            // completed: b=1 < a=5 < c=9
            Assert.Equal(new List<string> { "rb", "ra", "rc" }, Ids(sorted));
        }

        [Fact]
        public void SortRoutes_ByOrigin_UsesInjectedKey()
        {
            (List<Route> routes, Dictionary<string, RouteSortKeys> keys) = BuildFixture();
            List<Route> sorted = LogisticsSortPresentation.SortRoutes(
                routes, LogisticsRouteSortColumn.Origin, ascending: true, keys);
            // "depot pid=7"(rb) < "KSC (funds)"(ra) < "Minmus depot"(rc) (OrdinalIgnoreCase)
            Assert.Equal(new List<string> { "rb", "ra", "rc" }, Ids(sorted));
        }

        [Fact]
        public void SortRoutes_ByDestination_UsesInjectedKey()
        {
            (List<Route> routes, Dictionary<string, RouteSortKeys> keys) = BuildFixture();
            List<Route> sorted = LogisticsSortPresentation.SortRoutes(
                routes, LogisticsRouteSortColumn.Destination, ascending: true, keys);
            // "Aurora Outpost"(rb) < "Munar Station"(ra) < "Zenith Lab"(rc)
            Assert.Equal(new List<string> { "rb", "ra", "rc" }, Ids(sorted));
        }

        [Fact]
        public void SortRoutes_ByStatus_UsesInjectedKey()
        {
            (List<Route> routes, Dictionary<string, RouteSortKeys> keys) = BuildFixture();
            List<Route> sorted = LogisticsSortPresentation.SortRoutes(
                routes, LogisticsRouteSortColumn.Status, ascending: true, keys);
            // "Dispatching on schedule"(ra) < "Ghost in transit"(rc) < "Paused..."(rb)
            Assert.Equal(new List<string> { "ra", "rc", "rb" }, Ids(sorted));
        }

        [Fact]
        public void SortRoutes_ByDelivery_UsesInjectedKey()
        {
            (List<Route> routes, Dictionary<string, RouteSortKeys> keys) = BuildFixture();
            List<Route> sorted = LogisticsSortPresentation.SortRoutes(
                routes, LogisticsRouteSortColumn.Delivery, ascending: true, keys);
            // "Delivering"(ra) < "Flying, not delivering"(rc) < "Paused"(rb)
            Assert.Equal(new List<string> { "ra", "rc", "rb" }, Ids(sorted));
        }

        [Fact]
        public void SortRoutes_ByNextDelivery_NoCountdownSortsLastAscending()
        {
            (List<Route> routes, Dictionary<string, RouteSortKeys> keys) = BuildFixture();
            List<Route> sorted = LogisticsSortPresentation.SortRoutes(
                routes, LogisticsRouteSortColumn.NextDelivery, ascending: true, keys);
            // seconds: b=30 < a=120 < c(no countdown -> +inf, sorts last)
            Assert.Equal(new List<string> { "rb", "ra", "rc" }, Ids(sorted));
        }

        [Fact]
        public void SortRoutes_ByNextDelivery_DescendingPutsNoCountdownFirst()
        {
            (List<Route> routes, Dictionary<string, RouteSortKeys> keys) = BuildFixture();
            List<Route> sorted = LogisticsSortPresentation.SortRoutes(
                routes, LogisticsRouteSortColumn.NextDelivery, ascending: false, keys);
            // descending of [rb(30), ra(120), rc(+inf)] -> [rc, ra, rb]
            Assert.Equal(new List<string> { "rc", "ra", "rb" }, Ids(sorted));
        }

        [Fact]
        public void SortRoutes_DoesNotMutateInput()
        {
            (List<Route> routes, Dictionary<string, RouteSortKeys> keys) = BuildFixture();
            var original = Ids(routes);
            LogisticsSortPresentation.SortRoutes(
                routes, LogisticsRouteSortColumn.Cycles, ascending: false, keys);
            // The source list keeps its original insertion order.
            Assert.Equal(original, Ids(routes));
        }

        [Fact]
        public void SortRoutes_NullList_ReturnsEmpty()
        {
            List<Route> sorted = LogisticsSortPresentation.SortRoutes(
                null, LogisticsRouteSortColumn.Name, ascending: true,
                new Dictionary<string, RouteSortKeys>());
            Assert.Empty(sorted);
        }

        [Fact]
        public void SortRoutes_NullRoutesAndNullNames_AreTolerated()
        {
            Route named = new RouteFixtureBuilder().WithId("named").WithName("Zeta").Build();
            Route nullName = new RouteFixtureBuilder().WithId("nullname").WithName(null).Build();
            var routes = new List<Route> { named, null, nullName };
            var keys = new Dictionary<string, RouteSortKeys>();

            List<Route> sorted = LogisticsSortPresentation.SortRoutes(
                routes, LogisticsRouteSortColumn.Name, ascending: true, keys);

            // No throw; all three survive; the null route sorts to the end ascending.
            Assert.Equal(3, sorted.Count);
            Assert.Null(sorted[2]);
            // The null-name route sorts before the named "Zeta" (empty string < "Zeta").
            Assert.Equal("nullname", sorted[0].Id);
            Assert.Equal("named", sorted[1].Id);
        }

        [Fact]
        public void SortRoutes_MissingKeyEntry_FallsBackToEmptyKeysNoThrow()
        {
            // A route whose id is not present in the keys dictionary must sort with empty
            // (default) keys rather than throwing.
            Route a = new RouteFixtureBuilder().WithId("has").WithName("A").Build();
            Route b = new RouteFixtureBuilder().WithId("missing").WithName("B").Build();
            var keys = new Dictionary<string, RouteSortKeys>
            {
                ["has"] = new RouteSortKeys { DestinationText = "Somewhere" }
            };

            List<Route> sorted = LogisticsSortPresentation.SortRoutes(
                new List<Route> { a, b }, LogisticsRouteSortColumn.Destination,
                ascending: true, keys);

            // "missing" has empty destination ("") which sorts before "Somewhere".
            Assert.Equal(new List<string> { "missing", "has" }, Ids(sorted));
        }

        // The comparer is deterministic on equal primary keys: two routes with the same
        // Name break the tie by route id (direction-independent), so tied rows keep a
        // fixed relative order across re-sorts and read identically ascending vs descending.
        [Fact]
        public void SortRoutes_EqualPrimaryKey_TieBreaksByRouteIdDeterministically()
        {
            Route z = new RouteFixtureBuilder().WithId("rz").WithName("Same").Build();
            Route a = new RouteFixtureBuilder().WithId("ra").WithName("Same").Build();
            var keys = new Dictionary<string, RouteSortKeys>();

            List<Route> asc = LogisticsSortPresentation.SortRoutes(
                new List<Route> { z, a }, LogisticsRouteSortColumn.Name, ascending: true, keys);
            List<Route> desc = LogisticsSortPresentation.SortRoutes(
                new List<Route> { z, a }, LogisticsRouteSortColumn.Name, ascending: false, keys);

            // Equal Name -> tie-break by id ascending ("ra" before "rz"), the SAME in both
            // directions because the tie-break is not negated.
            Assert.Equal(new List<string> { "ra", "rz" }, Ids(asc));
            Assert.Equal(new List<string> { "ra", "rz" }, Ids(desc));
        }
    }
}
