using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Phase 3 (render union via append): <see cref="RouteGhostDriverSelector"/>
    /// filters committed routes down to the ghost-driving subset
    /// (<see cref="RouteStatusPolicy.GhostDriving"/>) and materializes each via
    /// <see cref="RouteBackingMission.BuildMission"/>. The three host push seams
    /// (<c>ParsekFlight</c> / <c>ParsekKSC</c> / <c>ParsekTrackingStation</c>
    /// <c>DriveMissionLoopUnits</c>) append the result to the single
    /// <c>MissionLoopUnitBuilder.Build</c> call.
    /// </summary>
    /// <remarks>
    /// Touches shared static state (<see cref="RouteStore"/>,
    /// <see cref="ParsekLog"/>), so the class is <c>[Collection("Sequential")]</c>
    /// and resets it in the ctor + Dispose.
    /// </remarks>
    [Collection("Sequential")]
    public class RouteGhostDriverSelectorTests : System.IDisposable
    {
        private const string GhostTag = "[RouteGhost]";
        private readonly List<string> logLines = new List<string>();

        public RouteGhostDriverSelectorTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // -----------------------------------------------------------------
        // Fixtures
        // -----------------------------------------------------------------

        // A loop-route bound to one tree, with a backing-mission tree id + a
        // dispatch interval (cadence) + an excluded interval key, so the produced
        // backing Mission carries the route's render parameters.
        private static Route RouteForGhost(
            string id, string treeId, RouteStatus status,
            double dispatchInterval = 600.0, double loopAnchorUT = 1000.0,
            string excludedKey = "post-undock-leg")
        {
            return new RouteFixtureBuilder()
                .WithId(id)
                .WithName("Route " + id)
                .WithStatus(status)
                .WithSchedule(transitDurationSeconds: 300.0, dispatchIntervalSeconds: dispatchInterval)
                .WithBackingMissionTreeId(treeId)
                .WithExcludedIntervalKey(excludedKey)
                .WithLoopAnchorUT(loopAnchorUT)
                .WithRecordingId("rec-" + treeId)
                .WithSourceRef(new RouteSourceRef { RecordingId = "rec-" + treeId, TreeId = treeId })
                .Build();
        }

        // -----------------------------------------------------------------
        // Status filter via RouteStatusPolicy.GhostDriving
        // -----------------------------------------------------------------

        // Every ghost-driving status produces exactly one backing mission.
        [Theory]
        [InlineData((int)RouteStatus.Active)]
        [InlineData((int)RouteStatus.InTransit)]
        [InlineData((int)RouteStatus.WaitingForResources)]
        [InlineData((int)RouteStatus.WaitingForFunds)]
        [InlineData((int)RouteStatus.DestinationFull)]
        public void GhostDrivingStatus_ProducesOneBackingMission(int statusOrdinal)
        {
            var status = (RouteStatus)statusOrdinal;
            RouteStore.AddRoute(RouteForGhost("route-A", "tree-X", status));

            IReadOnlyList<Mission> result =
                RouteGhostDriverSelector.SelectGhostDrivingBackingMissions(
                    RouteStore.CommittedRoutes, 1234.0);

            Assert.Single(result);
            Assert.Equal("route-A-backing", result[0].Id);
            Assert.Equal("tree-X", result[0].TreeId);
            Assert.True(result[0].LoopPlayback);
        }

        // Every non-ghost-driving status produces NOTHING (the ghost does not render).
        [Theory]
        [InlineData((int)RouteStatus.Paused)]
        [InlineData((int)RouteStatus.EndpointLost)]
        [InlineData((int)RouteStatus.MissingSourceRecording)]
        [InlineData((int)RouteStatus.SourceChanged)]
        public void NonGhostDrivingStatus_ProducesNothing(int statusOrdinal)
        {
            var status = (RouteStatus)statusOrdinal;
            RouteStore.AddRoute(RouteForGhost("route-A", "tree-X", status));

            IReadOnlyList<Mission> result =
                RouteGhostDriverSelector.SelectGhostDrivingBackingMissions(
                    RouteStore.CommittedRoutes, 1234.0);

            Assert.Empty(result);
        }

        // Mixed list: only the ghost-driving routes materialize, others skipped.
        [Fact]
        public void MixedStatuses_OnlyGhostDrivingMaterialize()
        {
            RouteStore.AddRoute(RouteForGhost("route-live", "tree-A", RouteStatus.Active));
            RouteStore.AddRoute(RouteForGhost("route-paused", "tree-B", RouteStatus.Paused));
            RouteStore.AddRoute(RouteForGhost("route-transit", "tree-C", RouteStatus.InTransit));
            RouteStore.AddRoute(RouteForGhost("route-broken", "tree-D", RouteStatus.EndpointLost));

            IReadOnlyList<Mission> result =
                RouteGhostDriverSelector.SelectGhostDrivingBackingMissions(
                    RouteStore.CommittedRoutes, 50.0);

            Assert.Equal(2, result.Count);
            var ids = result.Select(m => m.Id).ToList();
            Assert.Contains("route-live-backing", ids);
            Assert.Contains("route-transit-backing", ids);
            Assert.DoesNotContain("route-paused-backing", ids);
            Assert.DoesNotContain("route-broken-backing", ids);
        }

        // -----------------------------------------------------------------
        // Render parameters carried onto the backing Mission
        // -----------------------------------------------------------------

        // The materialized Mission carries the route's loop cadence (= DispatchInterval),
        // anchor, excluded interval key, and tree id (BuildMission contract).
        [Fact]
        public void MaterializedMission_CarriesRouteRenderParameters()
        {
            RouteStore.AddRoute(RouteForGhost(
                "route-A", "tree-X", RouteStatus.Active,
                dispatchInterval: 777.0, loopAnchorUT: 42.0, excludedKey: "leg-after-undock"));

            IReadOnlyList<Mission> result =
                RouteGhostDriverSelector.SelectGhostDrivingBackingMissions(
                    RouteStore.CommittedRoutes, 99.0);

            Mission m = Assert.Single(result);
            Assert.Equal(777.0, m.LoopIntervalSeconds);
            Assert.Equal(LoopTimeUnit.Sec, m.LoopTimeUnit);
            Assert.Equal(42.0, m.LoopAnchorUT);
            Assert.Contains("leg-after-undock", m.ExcludedIntervalKeys);
            // The coarse through-line field is NEVER populated by BuildMission.
            Assert.Empty(m.ExcludedThroughLineHeadIds);
        }

        // -----------------------------------------------------------------
        // Null / empty handling
        // -----------------------------------------------------------------

        [Fact]
        public void NullRoutes_ReturnsEmpty_NoThrow()
        {
            IReadOnlyList<Mission> result =
                RouteGhostDriverSelector.SelectGhostDrivingBackingMissions(null, 0.0);

            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public void EmptyRoutes_ReturnsEmpty()
        {
            IReadOnlyList<Mission> result =
                RouteGhostDriverSelector.SelectGhostDrivingBackingMissions(
                    RouteStore.CommittedRoutes, 0.0);

            Assert.NotNull(result);
            Assert.Empty(result);
        }

        // A null entry in the routes list is skipped, not crashed on.
        [Fact]
        public void NullRouteEntry_Skipped()
        {
            var routes = new List<Route>
            {
                RouteForGhost("route-A", "tree-X", RouteStatus.Active),
                null
            };

            IReadOnlyList<Mission> result =
                RouteGhostDriverSelector.SelectGhostDrivingBackingMissions(routes, 0.0);

            Assert.Single(result);
            Assert.Equal("route-A-backing", result[0].Id);
        }

        // -----------------------------------------------------------------
        // Log assertion
        // -----------------------------------------------------------------

        // The per-frame summary records the ghost-driving / skipped-by-status counts
        // so a missing route ghost is explainable from the log.
        [Fact]
        public void Summary_LogsGhostDrivingAndSkippedCounts()
        {
            RouteStore.AddRoute(RouteForGhost("route-live", "tree-A", RouteStatus.Active));
            RouteStore.AddRoute(RouteForGhost("route-paused", "tree-B", RouteStatus.Paused));

            RouteGhostDriverSelector.SelectGhostDrivingBackingMissions(
                RouteStore.CommittedRoutes, 0.0);

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]")
                && l.Contains(GhostTag)
                && l.Contains("SelectGhostDrivingBackingMissions")
                && l.Contains("ghostDriving=1")
                && l.Contains("skippedByStatus=1"));
        }
    }
}
