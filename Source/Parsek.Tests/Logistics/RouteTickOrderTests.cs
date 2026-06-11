using System;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the deterministic per-tick processing comparator
    /// <see cref="RouteOrchestrator.CompareRoutesForTick"/> (M1, design D8):
    /// ascending <see cref="Route.DispatchPriority"/> (lower value dispatches
    /// first), then <see cref="Route.NextDispatchUT"/> via <c>CompareTo</c>,
    /// then ordinal <see cref="Route.Id"/>. Pure comparator, no statics touched,
    /// so no Sequential collection needed.
    /// </summary>
    public class RouteTickOrderTests
    {
        private static Route BuildRoute(string id, int priority, double nextDispatchUT)
        {
            return new Route
            {
                Id = id,
                DispatchPriority = priority,
                NextDispatchUT = nextDispatchUT
            };
        }

        // catches: a higher-priority-value route (later) sorting before a
        // lower-value one (earlier), or the priority key being shadowed by
        // NextDispatchUT / id.
        [Fact]
        public void CompareRoutesForTick_PriorityWins()
        {
            // Deliberately give the LOWER-priority-value route the LATER
            // NextDispatchUT and the LATER ordinal id so only priority can
            // explain the ordering.
            Route first = BuildRoute("route-z", priority: 0, nextDispatchUT: 9999.0);
            Route second = BuildRoute("route-a", priority: 1, nextDispatchUT: 100.0);

            Assert.True(RouteOrchestrator.CompareRoutesForTick(first, second) < 0);
            Assert.True(RouteOrchestrator.CompareRoutesForTick(second, first) > 0);
        }

        // catches: equal-priority routes not falling to the NextDispatchUT mid-key.
        [Fact]
        public void CompareRoutesForTick_TiesFallToNextDispatchUT()
        {
            Route earlier = BuildRoute("route-z", priority: 2, nextDispatchUT: 100.0);
            Route later = BuildRoute("route-a", priority: 2, nextDispatchUT: 200.0);

            Assert.True(RouteOrchestrator.CompareRoutesForTick(earlier, later) < 0);
            Assert.True(RouteOrchestrator.CompareRoutesForTick(later, earlier) > 0);
        }

        // catches: a full (priority, UT) tie not resolving deterministically on
        // the ordinal route id (the unique final key that makes Array.Sort's
        // instability moot).
        [Fact]
        public void CompareRoutesForTick_FinalTieIsOrdinalRouteId()
        {
            Route a = BuildRoute("route-a", priority: 1, nextDispatchUT: 500.0);
            Route b = BuildRoute("route-b", priority: 1, nextDispatchUT: 500.0);

            Assert.True(RouteOrchestrator.CompareRoutesForTick(a, b) < 0);
            Assert.True(RouteOrchestrator.CompareRoutesForTick(b, a) > 0);
            Assert.Equal(0, RouteOrchestrator.CompareRoutesForTick(a, a));
        }

        // catches: a null route slot (snapshot defensiveness) throwing instead of
        // sorting last, or null-vs-null not comparing equal.
        [Fact]
        public void CompareRoutesForTick_NullSafe()
        {
            Route route = BuildRoute("route-a", priority: 0, nextDispatchUT: 0.0);

            Assert.True(RouteOrchestrator.CompareRoutesForTick(route, null) < 0);
            Assert.True(RouteOrchestrator.CompareRoutesForTick(null, route) > 0);
            Assert.Equal(0, RouteOrchestrator.CompareRoutesForTick(null, null));
            // Null ids are tolerated by the ordinal final key.
            Route noId = BuildRoute(null, priority: 0, nextDispatchUT: 0.0);
            Assert.True(RouteOrchestrator.CompareRoutesForTick(noId, route) < 0);
        }

        // catches (comparator totality): a NaN NextDispatchUT breaking the strict
        // weak ordering. Relational double operators return false for every NaN
        // comparison, which makes a comparator intransitive and Array.Sort throws
        // IComparer-contract exceptions; double.CompareTo totally orders NaN below
        // everything. Sorting an array containing NaN UTs must not throw and must
        // stay deterministic.
        [Fact]
        public void CompareRoutesForTick_NaNNextDispatchUT_TotalOrder()
        {
            Route nan = BuildRoute("route-nan", priority: 1, nextDispatchUT: double.NaN);
            Route low = BuildRoute("route-low", priority: 1, nextDispatchUT: 100.0);
            Route high = BuildRoute("route-high", priority: 1, nextDispatchUT: 200.0);

            // NaN sorts below every real value under CompareTo.
            Assert.True(RouteOrchestrator.CompareRoutesForTick(nan, low) < 0);
            Assert.True(RouteOrchestrator.CompareRoutesForTick(low, nan) > 0);

            var routes = new[] { high, nan, low, null };
            Array.Sort(routes, RouteOrchestrator.CompareRoutesForTick);

            Assert.Same(nan, routes[0]);
            Assert.Same(low, routes[1]);
            Assert.Same(high, routes[2]);
            Assert.Null(routes[3]);
        }
    }
}
