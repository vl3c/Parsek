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

        // catches (PR #2043 review, second round): a multi-stop loop route paused between its
        // stops reading as idle. Send un-pauses it WITHOUT resetting the stop cursors, so the
        // next tick finishes that half-fired cycle into whatever the press adopted; the
        // button stays greyed until Send finishes the cycle or Activate resets it.
        [Fact]
        public void RunInFlight_MultiStopLoopRoutePausedMidCycle()
        {
            Route paused = LoopRoute(Stop(lastFired: 3, dockUT: 100.0), Stop(lastFired: 2, dockUT: 200.0));
            paused.Status = RouteStatus.Paused;
            Assert.True(LogisticsRoutePresentation.IsRunInFlight(paused));
            Assert.Equal(LogisticsRoutePresentation.UpdatePartsInFlightReason,
                LogisticsRoutePresentation.UpdatePartsDisabledReason(paused));

            // A paused route between cycles is idle.
            Route pausedIdle = LoopRoute(Stop(lastFired: 3, dockUT: 100.0), Stop(lastFired: 3, dockUT: 200.0));
            pausedIdle.Status = RouteStatus.Paused;
            Assert.False(LogisticsRoutePresentation.IsRunInFlight(pausedIdle));

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
            // PR #2043 review: the size rule, in the player's words (a lander at a station
            // leaves the larger station out; a module as big as the station is left out too).
            Assert.Contains("each takes only docked craft with fewer parts than itself", t);
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

    /// <summary>
    /// PR #2043 review, second round: the Update parts gate driven through the real
    /// orchestrator on a two-stop loop route paused between its stops. Send returns a Paused
    /// route to Active without resetting the stop cursors (the next tick finishes the
    /// half-fired cycle on its pre-pause dispatch), so the button must stay greyed until
    /// that cycle has delivered to every stop, or until Activate resets the cursors.
    /// </summary>
    [Collection("Sequential")]
    public class LogisticsUpdatePartsPausedCycleTests : IDisposable
    {
        public LogisticsUpdatePartsPausedCycleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            ResetHooks();
        }

        public void Dispose()
        {
            ResetHooks();
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void ResetHooks()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.DeliveryRowEmitterForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
        }

        private sealed class EligibleEnv : IRouteRuntimeEnvironment
        {
            public bool IsCareer { get; set; }
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason) { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource, out double shortfall) { shortfall = 0.0; lackingResource = string.Empty; return true; }
            public bool KscFundsAvailable(Route route, out double shortfall) { shortfall = 0.0; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = string.Empty; return true; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        private static Route TwoStopLoopRoute()
        {
            var route = new Route
            {
                Id = "route-paused-cycle",
                Status = RouteStatus.Active,
                IsKscOrigin = true,
                BackingMissionTreeId = "tree-1",
                RecordedDockUT = 1300.0,
                DockMemberRecordingId = "rec-dock-b",
                LoopAnchorUT = 1000.0,
                LastObservedLoopCycleIndex = -1,
                DispatchInterval = 400.0,
                TransitDuration = 400.0,
                CostManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 }, { "Oxidizer", 120.0 } },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 42u },
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                        SegmentIndexBefore = 0, RecordedDockUT = 1150.0, LastFiredCycleIndex = -1,
                    },
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 43u },
                        DeliveryManifest = new Dictionary<string, double> { { "Oxidizer", 120.0 } },
                        SegmentIndexBefore = 1, RecordedDockUT = 1300.0, LastFiredCycleIndex = -1,
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-dock-b", TreeId = "tree-1", RouteProofHash = "deadbeef" },
                },
            };
            RouteStore.AddRoute(route);
            var unit = new GhostPlaybackLogic.LoopUnit(ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1400.0, cadenceSeconds: 400.0, phaseAnchorUT: 1000.0);
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => unit;
            RouteOrchestrator.DeliveryRowEmitterForTesting =
                (r, currentUT, env, cycleId, stopIndex, bump) => Ledger.AddAction(new GameAction
                {
                    Type = GameActionType.RouteCargoDelivered, UT = currentUT, RouteId = r.Id,
                    RouteCycleId = cycleId, RouteStopIndex = stopIndex,
                    Sequence = stopIndex * RouteOrchestrator.SeqStride + 3,
                });
            return route;
        }

        // catches: the button going live on a paused half-fired cycle, after which Send
        // finishes that cycle's later stop under the pre-pause dispatch (the reviewer's probe).
        [Fact]
        public void PausedMidCycle_GreyedUntilSendFinishesTheCycle()
        {
            Route route = TwoStopLoopRoute();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(1150.0, env);
            Assert.Equal(0, route.Stops[0].LastFiredCycleIndex);
            Assert.Equal(-1, route.Stops[1].LastFiredCycleIndex);
            Assert.True(LogisticsRoutePresentation.IsRunInFlight(route));

            Assert.True(RouteOrchestrator.TryPause(route, 1160.0, env));
            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.True(LogisticsRoutePresentation.IsRunInFlight(route));
            Assert.Equal(LogisticsRoutePresentation.UpdatePartsInFlightReason,
                LogisticsRoutePresentation.UpdatePartsDisabledReason(route));

            // Send un-pauses without resetting the cursors: still under way.
            Assert.True(RouteOrchestrator.TrySendOneCycleNow(route, 1170.0));
            Assert.Equal(0, route.Stops[0].LastFiredCycleIndex);
            Assert.True(LogisticsRoutePresentation.IsRunInFlight(route));

            // The next tick finishes the half-fired cycle 0 at its second stop; then idle.
            RouteOrchestrator.Tick(1350.0, env);
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.RouteCargoDelivered
                && a.RouteStopIndex == 1 && a.RouteCycleId == "cycle-0");
            Assert.False(LogisticsRoutePresentation.IsRunInFlight(route));
            Assert.Equal(string.Empty, LogisticsRoutePresentation.UpdatePartsDisabledReason(route));
        }

        // catches: a paused half-fired cycle as a dead end: Activate resets every stop's
        // cursor, so the button is live again at once.
        [Fact]
        public void PausedMidCycle_ActivateResetsTheCursorsAndFreesTheButton()
        {
            Route route = TwoStopLoopRoute();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(1150.0, env);
            Assert.True(RouteOrchestrator.TryPause(route, 1160.0, env));
            Assert.True(LogisticsRoutePresentation.IsRunInFlight(route));

            Assert.True(RouteOrchestrator.TryActivate(route, 1170.0));
            Assert.Equal(-1, route.Stops[0].LastFiredCycleIndex);
            Assert.Equal(-1, route.Stops[1].LastFiredCycleIndex);
            Assert.False(LogisticsRoutePresentation.IsRunInFlight(route));
        }
    }
}
