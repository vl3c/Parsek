using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The Logistics route detail block's "Update parts" action (owner ruling 2026-10-07): it
    /// re-captures every endpoint's current parts through
    /// <see cref="RouteEndpointPartAdoption.AdoptCurrentParts(Route)"/>. Pinned here: when the
    /// button is greyed (a run under way: the gate planned against one part set, the writes
    /// would land in another), its hover (what it does, undock visitors first, and when the
    /// route last updated) and the slot it takes in the block in both modes. The source half
    /// (the slot draws it, the click calls the action) is in <c>TableRowInsetAlignmentTests</c>.
    /// </summary>
    public class LogisticsUpdatePartsPresentationTests
    {
        private static readonly Func<double, string> Date = ut => "Y1, D06, 14:05";

        private static RouteStop Stop(long lastFired = -1, double dockUT = -1.0)
        {
            return new RouteStop
            {
                Endpoint = new RouteEndpoint { RootPartUId = 100u, VesselPersistentId = 10u, BodyName = "Kerbin" },
                RecordedDockUT = dockUT,
                LastFiredCycleIndex = lastFired,
            };
        }

        private static Route LoopRoute(params RouteStop[] stops)
        {
            var b = new RouteFixtureBuilder()
                .WithId("route-update-parts")
                .WithBackingMissionTreeId("tree-1")
                .WithLastObservedLoopCycleIndex(2);
            foreach (RouteStop s in stops) b.WithStop(s);
            return b.Build();
        }

        // ------------------------------------------------------------------
        // A run under way
        // ------------------------------------------------------------------

        // catches: the button live while a legacy run is in transit or its arrival is
        // pending delivery (the capacity gate planned against the old part set).
        [Fact]
        public void RunInFlight_InTransitOrPendingDelivery()
        {
            Route inTransit = LoopRoute(Stop());
            inTransit.Status = RouteStatus.InTransit;
            Assert.True(LogisticsRoutePresentation.IsRunInFlight(inTransit));

            Route pending = LoopRoute(Stop());
            pending.PendingDeliveryUT = 500.0;
            Assert.True(LogisticsRoutePresentation.IsRunInFlight(pending));
        }

        // catches: greying the button forever after a legacy run (its cycle-start stamp is
        // never cleared by the delivery), or for any idle status.
        [Fact]
        public void RunNotInFlight_IdleRouteOfAnyOtherStatus()
        {
            foreach (RouteStatus status in (RouteStatus[])Enum.GetValues(typeof(RouteStatus)))
            {
                if (status == RouteStatus.InTransit) continue;
                Route route = LoopRoute(Stop());
                route.Status = status;
                route.CurrentCycleStartUT = 100.0;
                Assert.False(LogisticsRoutePresentation.IsRunInFlight(route), status.ToString());
            }
            Assert.False(LogisticsRoutePresentation.IsRunInFlight(null));
        }

        // catches: a multi-stop loop cycle that delivered to its first stop but not yet to
        // the later one reading as idle (the later stop's cargo was gated against the parts
        // the route knew at dispatch).
        [Fact]
        public void RunInFlight_MultiStopCyclePartWayThroughItsStops()
        {
            Route partWay = LoopRoute(Stop(lastFired: 3, dockUT: 100.0), Stop(lastFired: 2, dockUT: 200.0));
            Assert.True(LogisticsRoutePresentation.IsRunInFlight(partWay));

            Route firstCycle = LoopRoute(Stop(lastFired: 0, dockUT: 100.0), Stop(lastFired: -1, dockUT: 200.0));
            Assert.True(LogisticsRoutePresentation.IsRunInFlight(firstCycle));

            Route done = LoopRoute(Stop(lastFired: 3, dockUT: 100.0), Stop(lastFired: 3, dockUT: 200.0));
            Assert.False(LogisticsRoutePresentation.IsRunInFlight(done));

            Route fresh = LoopRoute(Stop(), Stop(), null);
            Assert.False(LogisticsRoutePresentation.IsRunInFlight(fresh));

            // A single-stop loop route fires its run in one tick and never keeps per-stop state.
            Route single = LoopRoute(Stop(lastFired: 7));
            Assert.False(LogisticsRoutePresentation.IsRunInFlight(single));
        }

        // catches (PR #2043 review): a multi-stop loop route paused between its stops
        // greyed until it ran again. A paused loop route fires nothing, and Activate resets
        // every stop's cursor, so the half-fired cycle is never finished.
        [Fact]
        public void RunNotInFlight_MultiStopLoopRoutePausedMidCycle()
        {
            Route paused = LoopRoute(Stop(lastFired: 3, dockUT: 100.0), Stop(lastFired: 2, dockUT: 200.0));
            paused.Status = RouteStatus.Paused;
            Assert.False(LogisticsRoutePresentation.IsRunInFlight(paused));

            // A self-timer run still pending delivery is in flight whatever the status says.
            Route pending = LoopRoute(Stop());
            pending.Status = RouteStatus.Paused;
            pending.PendingDeliveryUT = 500.0;
            Assert.True(LogisticsRoutePresentation.IsRunInFlight(pending));
        }

        [Fact]
        public void DisabledReason_EmptyWhenIdle_NamesTheRunWhenInFlight()
        {
            Route idle = LoopRoute(Stop());
            Assert.Equal(string.Empty, LogisticsRoutePresentation.UpdatePartsDisabledReason(idle));

            Route busy = LoopRoute(Stop());
            busy.Status = RouteStatus.InTransit;
            Assert.Equal(LogisticsRoutePresentation.UpdatePartsInFlightReason,
                LogisticsRoutePresentation.UpdatePartsDisabledReason(busy));
            Assert.False(string.IsNullOrEmpty(LogisticsRoutePresentation.UpdatePartsInFlightReason));

            Assert.Equal(string.Empty, LogisticsRoutePresentation.UpdatePartsDisabledReason(null));
        }

        // ------------------------------------------------------------------
        // Hover
        // ------------------------------------------------------------------

        // catches: the hover not saying what the press does to the origin and the stops,
        // or not warning to undock visitors first (the action captures the vessel whole).
        [Fact]
        public void Tooltip_SaysWhatItCountsAndToUndockVisitorsFirst()
        {
            string t = LogisticsRoutePresentation.UpdatePartsTooltip;
            Assert.Contains("origin", t);
            Assert.Contains("each stop", t);
            Assert.Contains("Undock visiting ships first", t);
            // PR #2043 review: a stop docked into a bigger station is refused, and says so.
            Assert.Contains("a stop docked into a bigger station keeps its own parts", t);
            Assert.DoesNotContain("\n", t);
        }

        // catches: no trace of a press the player made (the window has no outcome line,
        // so the hover is where the last update shows).
        [Fact]
        public void Tooltip_AddsTheLastUpdateDateWhenTheRouteHasOne()
        {
            Assert.Equal(LogisticsRoutePresentation.UpdatePartsTooltip,
                LogisticsRoutePresentation.FormatUpdatePartsTooltip(-1.0, Date));
            Assert.Equal(LogisticsRoutePresentation.UpdatePartsTooltip + " Last updated on Y1, D06, 14:05.",
                LogisticsRoutePresentation.FormatUpdatePartsTooltip(1234.0, Date));
            Assert.Equal(LogisticsRoutePresentation.UpdatePartsTooltip + " Last updated on UT 1234.",
                LogisticsRoutePresentation.FormatUpdatePartsTooltip(1234.0, null));
        }

        [Fact]
        public void LastAdoptedUT_IsTheLatestEntry()
        {
            Route route = LoopRoute(Stop());
            Assert.Equal(-1.0, RouteEndpointPartAdoption.LastAdoptedUT(route));
            Assert.Equal(-1.0, RouteEndpointPartAdoption.LastAdoptedUT(null));

            route.AdoptedEndpointParts = new List<RouteEndpointAdoptedParts>
            {
                new RouteEndpointAdoptedParts { AdoptedUT = 50.0, PartFlightIds = new HashSet<uint> { 1u } },
                new RouteEndpointAdoptedParts { AdoptedUT = 75.5, PartFlightIds = new HashSet<uint> { 2u } },
                new RouteEndpointAdoptedParts { AdoptedUT = -1.0, PartFlightIds = new HashSet<uint> { 3u } },
            };
            Assert.Equal(75.5, RouteEndpointPartAdoption.LastAdoptedUT(route));
        }

        // The label fits the block's 100 px single (the Interact column's width), so it
        // stays short; the hover carries the explanation.
        [Fact]
        public void Label_IsShortEnoughForTheDetailSingle()
        {
            Assert.Equal("Update parts", LogisticsRoutePresentation.UpdatePartsButtonLabel);
            Assert.True(LogisticsRoutePresentation.UpdatePartsButtonLabel.Length
                <= "Link round-trip...".Length);
        }
    }
}
