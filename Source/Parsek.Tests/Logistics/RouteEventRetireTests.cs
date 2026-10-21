using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Unit tests for the pure Rec-1 retire predicate + list filter and the Rec-3 window
    /// selection (<see cref="RouteEventRetire"/>). No shared static state is touched, so this
    /// is a plain class (no <c>[Collection("Sequential")]</c>).
    ///
    /// Plan: <c>docs/dev/plans/fix-logistics-rewind-determinism.md</c> Phase 1.
    /// </summary>
    public class RouteEventRetireTests
    {
        private static readonly RouteEventType[] RouteTypes =
        {
            RouteEventType.RouteDispatched,
            RouteEventType.RouteCargoDebited,
            RouteEventType.RouteCargoDelivered,
            RouteEventType.RoutePaused,
            RouteEventType.RouteEndpointLost,
            RouteEventType.RouteCargoPickedUp,
            RouteEventType.RouteResumed,
            RouteEventType.RouteHeld,
        };

        private static RouteEvent Route(RouteEventType t, double ut, string routeId = "route-1")
            => new RouteEvent { Type = t, UT = ut, RouteId = routeId };

        // ---- ShouldRetireAtRewind: boundary + RouteId gating ----

        [Fact]
        public void ShouldRetire_FutureEvent_True_ForEveryType()
        {
            foreach (var t in RouteTypes)
                Assert.True(RouteEventRetire.ShouldRetireAtRewind(Route(t, 3000.0), 2500.0),
                    $"{t} at UT 3000 > cutoff 2500 should retire");
        }

        [Fact]
        public void ShouldRetire_AtCutoff_Kept_StrictGreaterThan()
        {
            // Strict > : an event stamped exactly at the cutoff is at/before the reverted
            // world and is KEPT (its physical effect is in the quicksave).
            foreach (var t in RouteTypes)
                Assert.False(RouteEventRetire.ShouldRetireAtRewind(Route(t, 2500.0), 2500.0),
                    $"{t} exactly at cutoff must be kept (strict >)");
        }

        [Fact]
        public void ShouldRetire_BeforeCutoff_Kept()
        {
            foreach (var t in RouteTypes)
                Assert.False(RouteEventRetire.ShouldRetireAtRewind(Route(t, 1000.0), 2500.0),
                    $"{t} before cutoff must be kept");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ShouldRetire_EmptyRouteId_NotRetired(string routeId)
        {
            var e = new RouteEvent { Type = RouteEventType.RouteDispatched, UT = 3000.0, RouteId = routeId };
            Assert.False(RouteEventRetire.ShouldRetireAtRewind(e, 2500.0));
        }

        [Fact]
        public void ShouldRetire_NullEvent_False()
        {
            Assert.False(RouteEventRetire.ShouldRetireAtRewind(null, 2500.0));
        }

        // ---- RetireFutureEvents: order, count, +inf, null ----

        [Fact]
        public void Retire_DropsFutureEvents_KeepsPast_PreservesOrder_HandsBackRetired()
        {
            var src = new List<RouteEvent>
            {
                Route(RouteEventType.RouteCargoDebited, 2000.0, "a"),    // keep (< cutoff)
                Route(RouteEventType.RouteDispatched, 3000.0, "b"),      // drop
                null,                                                    // skipped
                Route(RouteEventType.RoutePaused, 2500.0, "c"),          // keep (== cutoff)
                Route(RouteEventType.RouteCargoDelivered, 3000.0, "d"),  // drop
                Route(RouteEventType.RouteHeld, 3500.0, "e"),            // drop
            };

            var kept = RouteEventRetire.RetireFutureEvents(src, 2500.0, out int retired,
                out List<RouteEvent> retiredEvents);

            Assert.Equal(3, retired);
            Assert.Equal(new[] { "a", "c" }, kept.ConvertAll(e => e.RouteId).ToArray());
            Assert.Equal(new[] { "b", "d", "e" }, retiredEvents.ConvertAll(e => e.RouteId).ToArray());
            // The source list is never mutated.
            Assert.Equal(6, src.Count);
        }

        [Fact]
        public void Retire_PositiveInfinityCutoff_RetiresNothing()
        {
            var src = new List<RouteEvent>
            {
                Route(RouteEventType.RouteDispatched, 1e9),
                Route(RouteEventType.RouteCargoDelivered, 1e12),
            };

            var kept = RouteEventRetire.RetireFutureEvents(src, double.PositiveInfinity, out int retired);

            Assert.Equal(0, retired);
            Assert.Equal(src.Count, kept.Count);
        }

        [Fact]
        public void Retire_NullSource_EmptyList_NoThrow()
        {
            var kept = RouteEventRetire.RetireFutureEvents(null, 100.0, out int retired);
            Assert.Empty(kept);
            Assert.Equal(0, retired);
        }

        // ==================================================================
        // Rec-3 (non-rewind discard leak): pure selection helpers.
        // ==================================================================

        private static RouteEvent PhysResource(RouteEventType t, double ut, string routeId = "route-1")
            => new RouteEvent
            {
                Type = t,
                UT = ut,
                RouteId = routeId,
                RouteResourceManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
            };

        [Fact]
        public void IsPhysicalRouteMutation_TrueForEventWithResourceManifest()
        {
            Assert.True(RouteEventRetire.IsPhysicalRouteMutation(PhysResource(RouteEventType.RouteCargoDelivered, 10.0)));
            Assert.True(RouteEventRetire.IsPhysicalRouteMutation(PhysResource(RouteEventType.RouteCargoDebited, 10.0)));
            Assert.True(RouteEventRetire.IsPhysicalRouteMutation(PhysResource(RouteEventType.RouteCargoPickedUp, 10.0)));
        }

        [Fact]
        public void IsPhysicalRouteMutation_TrueForEventWithInventoryManifest()
        {
            var e = new RouteEvent
            {
                Type = RouteEventType.RouteCargoDelivered,
                UT = 10.0,
                RouteId = "route-1",
                RouteInventoryManifest = new List<InventoryPayloadItem> { new InventoryPayloadItem() },
            };
            Assert.True(RouteEventRetire.IsPhysicalRouteMutation(e));
        }

        [Fact]
        public void IsPhysicalRouteMutation_FalseForManifestlessMarkerEmptyAndNull()
        {
            Assert.False(RouteEventRetire.IsPhysicalRouteMutation(Route(RouteEventType.RouteCargoDebited, 10.0)));
            Assert.False(RouteEventRetire.IsPhysicalRouteMutation(Route(RouteEventType.RouteDispatched, 10.0)));
            Assert.False(RouteEventRetire.IsPhysicalRouteMutation(Route(RouteEventType.RoutePaused, 10.0)));
            Assert.False(RouteEventRetire.IsPhysicalRouteMutation(Route(RouteEventType.RouteEndpointLost, 10.0)));
            Assert.False(RouteEventRetire.IsPhysicalRouteMutation(new RouteEvent
            {
                Type = RouteEventType.RouteCargoDelivered,
                UT = 10.0,
                RouteId = "route-1",
                RouteResourceManifest = new Dictionary<string, double>(),
            }));
            // No route id: not a route event at all.
            Assert.False(RouteEventRetire.IsPhysicalRouteMutation(
                PhysResource(RouteEventType.RouteCargoDelivered, 10.0, routeId: null)));
            Assert.False(RouteEventRetire.IsPhysicalRouteMutation(null));
        }

        [Fact]
        public void SelectInWindow_InclusiveBoundsAndOrderPreserved()
        {
            var src = new List<RouteEvent>
            {
                Route(RouteEventType.RouteDispatched, 50.0),        // before window
                Route(RouteEventType.RouteCargoDelivered, 100.0),   // == min -> in
                Route(RouteEventType.RouteCargoDebited, 150.0),     // in
                Route(RouteEventType.RoutePaused, 200.0),           // == max -> in
                Route(RouteEventType.RouteCargoPickedUp, 250.0),    // after window
            };

            var sel = RouteEventRetire.SelectInWindow(src, 100.0, 200.0);

            Assert.Equal(3, sel.Count);
            Assert.Equal(100.0, sel[0].UT);
            Assert.Equal(150.0, sel[1].UT);
            Assert.Equal(200.0, sel[2].UT);
        }

        [Fact]
        public void SelectInWindow_ExcludesEmptyRouteIdAndNull()
        {
            var src = new List<RouteEvent>
            {
                Route(RouteEventType.RouteCargoDelivered, 110.0),
                new RouteEvent { Type = RouteEventType.RouteCargoDelivered, UT = 120.0, RouteId = null },
                null,
            };

            var sel = RouteEventRetire.SelectInWindow(src, 100.0, 200.0);

            Assert.Single(sel);
            Assert.Equal(110.0, sel[0].UT);
        }

        [Fact]
        public void SelectInWindow_DegenerateWindowAndNullSourceReturnEmpty()
        {
            Assert.Empty(RouteEventRetire.SelectInWindow(null, 0.0, 100.0));

            var src = new List<RouteEvent> { Route(RouteEventType.RouteCargoDelivered, 50.0) };
            Assert.Empty(RouteEventRetire.SelectInWindow(src, 200.0, 100.0)); // min > max
        }
    }
}
