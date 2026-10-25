using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The shared click body of the Logistics window's "Re-scan for endpoint" button, which
    /// the seam's <c>RouteCommand action=rescan</c> also runs (lane RS-1). The button and the
    /// seam must press the same thing, so these cells pin the gate, the recovery hand-off and
    /// the exact log line both print.
    /// </summary>
    [Collection("Sequential")]
    public class LogisticsEndpointRescanTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LogisticsEndpointRescanTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
        }

        public void Dispose()
        {
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RouteEndpoint Surface(uint pid) => new RouteEndpoint
        {
            VesselPersistentId = pid, BodyName = "Kerbin", IsSurface = true,
            Latitude = -0.0875, Longitude = -74.78, Altitude = 66.0,
        };

        private static RouteEndpoint Orbital(uint pid) => new RouteEndpoint
        {
            VesselPersistentId = pid, BodyName = "Mun", IsSurface = false,
        };

        private static Route LoopRoute(RouteStatus status, params RouteEndpoint[] endpoints)
        {
            var route = new Route
            {
                Id = "rescan-route-id",
                Name = "RS1",
                Status = status,
                IsKscOrigin = true,
                BackingMissionTreeId = "tree-1",
                RecordedDockUT = 1150.0,
                DockMemberRecordingId = "rec-dock",
                LoopAnchorUT = 1000.0,
                LastObservedLoopCycleIndex = 3,
                DispatchInterval = 300.0,
                TransitDuration = 300.0,
                NextEligibilityCheckUT = 1530.0,
                Stops = new List<RouteStop>(),
            };
            foreach (RouteEndpoint e in endpoints)
            {
                route.Stops.Add(new RouteStop
                {
                    Endpoint = e,
                    DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 10.0 } },
                });
            }
            return route;
        }

        private static LogisticsEndpointRescan.StopResolver Resolver(Dictionary<uint, string> found)
        {
            return (RouteEndpoint endpoint, out string name, out string reason) =>
            {
                if (found.TryGetValue(endpoint.VesselPersistentId, out name))
                {
                    reason = string.Empty;
                    return true;
                }
                name = null;
                reason = "no-candidate-after-transport-exclusion";
                return false;
            };
        }

        // catches: the seam pressing a button the window does not draw (wrong status, or an
        // orbital-only route whose button is the disabled-with-note form).
        [Fact]
        public void IsOffered_MirrorsTheEnabledButton()
        {
            Assert.True(LogisticsEndpointRescan.IsOffered(LoopRoute(RouteStatus.EndpointLost, Surface(1))));
            Assert.False(LogisticsEndpointRescan.IsOffered(LoopRoute(RouteStatus.Active, Surface(1))));
            Assert.False(LogisticsEndpointRescan.IsOffered(LoopRoute(RouteStatus.Paused, Surface(1))));
            Assert.False(LogisticsEndpointRescan.IsOffered(LoopRoute(RouteStatus.EndpointLost, Orbital(1))));
            Assert.True(LogisticsEndpointRescan.IsOffered(
                LoopRoute(RouteStatus.EndpointLost, Orbital(1), Surface(2))));
            Assert.False(LogisticsEndpointRescan.IsOffered(
                LoopRoute(RouteStatus.EndpointLost, Orbital(1), Orbital(2))));
            Assert.False(LogisticsEndpointRescan.IsOffered(LoopRoute(RouteStatus.EndpointLost)));
            Assert.False(LogisticsEndpointRescan.IsOffered(null));
        }

        // catches: the press not handing a found endpoint to the recovery (the
        // LOOP-ROUTE-RESCAN-DOES-NOTHING shape: a loop route left stopped after a hit).
        [Fact]
        public void SingleStop_Hit_RecoversTheLoopRoute_AndLogsTheButtonsLine()
        {
            var route = LoopRoute(RouteStatus.EndpointLost, Surface(42));

            var outcome = LogisticsEndpointRescan.Run(route, 1500.0,
                Resolver(new Dictionary<uint, string> { { 42u, "Base" } }));

            Assert.Equal(1, outcome.Stops);
            Assert.Equal(1, outcome.Resolved);
            Assert.True(outcome.AllResolved);
            Assert.True(outcome.Recovered);
            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.Equal(1500.0, route.LoopAnchorUT);
            Assert.Contains(RouteEventLog.Events, e => e.Type == RouteEventType.RouteResumed
                && e.RouteEndpointReason == RouteOrchestrator.EndpointRescanReason);
            Assert.Contains(logLines, l => l.Contains("[UI]")
                && l.Contains("Logistics: endpoint re-scan route=rescan-r resolved=true name='Base' (recovering)"));
        }

        // catches: a miss that still recovers, or that clears the retry gate as the old
        // button did (the miss must leave the route exactly as it was).
        [Fact]
        public void SingleStop_Miss_LeavesTheRouteEndpointLost()
        {
            var route = LoopRoute(RouteStatus.EndpointLost, Surface(42));

            var outcome = LogisticsEndpointRescan.Run(route, 1500.0,
                Resolver(new Dictionary<uint, string>()));

            Assert.Equal(1, outcome.Stops);
            Assert.Equal(0, outcome.Resolved);
            Assert.False(outcome.AllResolved);
            Assert.False(outcome.Recovered);
            Assert.Equal(RouteStatus.EndpointLost, route.Status);
            Assert.Equal(1530.0, route.NextEligibilityCheckUT);
            Assert.Equal(3, route.LastObservedLoopCycleIndex);
            Assert.Empty(RouteEventLog.Events);
            Assert.Contains(logLines, l => l.Contains("[UI]")
                && l.Contains("Logistics: endpoint re-scan route=rescan-r resolved=false "
                    + "reason='no-candidate-after-transport-exclusion' (still EndpointLost)"));
            Assert.DoesNotContain(logLines, l => l.Contains("EndpointRescan:"));
        }

        // catches: a multi-stop press recovering while one stop is still lost (the dispatch
        // gate refuses the cycle until every stop resolves).
        [Fact]
        public void MultiStop_OneStopStillLost_DoesNotRecover()
        {
            var route = LoopRoute(RouteStatus.EndpointLost, Surface(1), Surface(2));

            var outcome = LogisticsEndpointRescan.Run(route, 1500.0,
                Resolver(new Dictionary<uint, string> { { 1u, "B" } }));

            Assert.Equal(2, outcome.Stops);
            Assert.Equal(1, outcome.Resolved);
            Assert.False(outcome.AllResolved);
            Assert.False(outcome.Recovered);
            Assert.Equal(RouteStatus.EndpointLost, route.Status);
            Assert.Contains(logLines, l => l.Contains("Logistics: endpoint re-scan route=rescan-r "
                + "stops=2 resolved=1/2 [1:'B' 2:unresolved('no-candidate-after-transport-exclusion')] (still EndpointLost)"));
        }

        [Fact]
        public void MultiStop_EveryStopFound_Recovers()
        {
            var route = LoopRoute(RouteStatus.EndpointLost, Surface(1), Surface(2));

            var outcome = LogisticsEndpointRescan.Run(route, 1500.0,
                Resolver(new Dictionary<uint, string> { { 1u, "B" }, { 2u, "A" } }));

            Assert.Equal(2, outcome.Resolved);
            Assert.True(outcome.AllResolved);
            Assert.True(outcome.Recovered);
            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.Contains(logLines, l => l.Contains("Logistics: endpoint re-scan route=rescan-r "
                + "stops=2 resolved=2/2 [1:'B' 2:'A'] (recovering)"));
            Assert.Contains(logLines, l => l.Contains("EndpointRescan:") && l.Contains("EndpointLost->Active"));
        }
    }
}
