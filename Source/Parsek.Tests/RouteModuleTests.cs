using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for <see cref="RouteModule"/> (route action skeleton). Uses the
    /// internal <c>GetWalkStateForTesting</c> accessor rather than reflection so the
    /// tests stay tied to a public-ish surface that the implementation can refactor
    /// alongside them.
    /// </summary>
    [Collection("Sequential")]
    public class RouteModuleTests : IDisposable
    {
        private readonly RouteModule module;
        private readonly List<string> logLines = new List<string>();

        public RouteModuleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            module = new RouteModule();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Helpers
        // ================================================================

        private static GameAction MakeDispatched(string routeId, string cycleId = "cyc-1", double ut = 100.0)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.RouteDispatched,
                RouteId = routeId,
                RouteCycleId = cycleId
            };
        }

        private static GameAction MakeDelivered(string routeId, string cycleId = "cyc-1",
            int stopIndex = 0, double ut = 200.0)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.RouteCargoDelivered,
                RouteId = routeId,
                RouteCycleId = cycleId,
                RouteStopIndex = stopIndex
            };
        }

        private static GameAction MakePaused(string routeId, string reason = "PlayerPause", double ut = 150.0)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.RoutePaused,
                RouteId = routeId,
                RouteEndpointReason = reason
            };
        }

        private static GameAction MakeEndpointLost(string routeId,
            string reason = "EndpointLost:OrbitalNoFallback", double ut = 175.0)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.RouteEndpointLost,
                RouteId = routeId,
                RouteEndpointReason = reason
            };
        }

        // ================================================================
        // State-tracking tests
        // ================================================================

        [Fact]
        public void ProcessAction_RouteDispatched_TracksCycle()
        {
            // Fails if RouteDispatched stops bumping DispatchedCycles or starts losing UT.
            module.ProcessAction(MakeDispatched("route-A", "cyc-1", 100.0));
            module.ProcessAction(MakeDispatched("route-A", "cyc-2", 200.0));

            var state = module.GetWalkStateForTesting()["route-A"];
            Assert.Equal(2, state.DispatchedCycles);
            Assert.Equal(200.0, state.LastActionUT);
        }

        [Fact]
        public void ProcessAction_RouteCargoDelivered_TracksStop()
        {
            // Fails if delivery counter regresses or if a valid post-dispatch delivery is rejected.
            module.ProcessAction(MakeDispatched("route-B"));
            module.ProcessAction(MakeDelivered("route-B", "cyc-1", 0, 250.0));

            var state = module.GetWalkStateForTesting()["route-B"];
            Assert.Equal(1, state.DispatchedCycles);
            Assert.Equal(1, state.DeliveredStops);
            Assert.Equal(250.0, state.LastActionUT);
        }

        [Fact]
        public void ProcessAction_RouteCargoDeliveredWithoutDispatch_LogsWarnAndSkips()
        {
            // Fails if the out-of-order guard regresses and the counter bumps without a dispatch.
            module.ProcessAction(MakeDelivered("route-C"));

            // Delivery still creates the route slot (TryGetOrCreateState ran) but does not bump
            // DeliveredStops, and a warn is emitted.
            var state = module.GetWalkStateForTesting()["route-C"];
            Assert.Equal(0, state.DispatchedCycles);
            Assert.Equal(0, state.DeliveredStops);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("Out-of-order delivery")
                && l.Contains("route-C"));
        }

        [Fact]
        public void ProcessAction_RoutePaused_SetsPausedFlag()
        {
            // Fails if RoutePaused stops setting the flag or loses the reason text.
            module.ProcessAction(MakePaused("route-D", "AutoPause:SourceChanged", 300.0));

            var state = module.GetWalkStateForTesting()["route-D"];
            Assert.True(state.Paused);
            Assert.Equal("AutoPause:SourceChanged", state.LastReason);
            Assert.Equal(300.0, state.LastActionUT);
        }

        [Fact]
        public void ProcessAction_RouteEndpointLost_SetsFlag()
        {
            // Fails if RouteEndpointLost regresses to a no-op or stops carrying the reason.
            module.ProcessAction(MakeEndpointLost("route-E", "EndpointLost:OrbitalNoFallback", 400.0));

            var state = module.GetWalkStateForTesting()["route-E"];
            Assert.True(state.EndpointLost);
            Assert.Equal("EndpointLost:OrbitalNoFallback", state.LastReason);
        }

        [Fact]
        public void Reset_ClearsAllState()
        {
            // Fails if Reset stops fully clearing the per-route dict — a stale entry
            // would silently leak between recalculation walks.
            module.ProcessAction(MakeDispatched("route-F"));
            module.ProcessAction(MakePaused("route-G"));
            Assert.Equal(2, module.GetWalkStateForTesting().Count);

            module.Reset();

            Assert.Empty(module.GetWalkStateForTesting());
        }

        [Fact]
        public void Reset_ThenReapply_ProducesIdenticalState()
        {
            // Fails if Reset leaks state OR if the action processor is non-idempotent.
            // Determinism here is critical — the recalculation engine resets and
            // re-walks the full timeline on every change.
            var actions = new[]
            {
                MakeDispatched("route-H", "cyc-1", 100.0),
                MakeDispatched("route-H", "cyc-2", 200.0),
                MakeDelivered("route-H", "cyc-1", 0, 150.0),
                MakePaused("route-H", "PlayerPause", 250.0),
            };

            foreach (var a in actions) module.ProcessAction(a);
            var first = Snapshot(module);

            module.Reset();
            foreach (var a in actions) module.ProcessAction(a);
            var second = Snapshot(module);

            Assert.Equal(first, second);
        }

        [Fact]
        public void ProcessAction_IgnoresNonRouteActionTypes()
        {
            // Fails if the route module starts accumulating funds / contract /
            // milestone state — those belong to other modules and a cross-contam
            // bug here would corrupt every walk.
            module.ProcessAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.FundsEarning,
                FundsAwarded = 5000f,
                RecordingId = "rec-A"
            });
            module.ProcessAction(new GameAction
            {
                UT = 200.0,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "FirstLaunch",
                RecordingId = "rec-B"
            });
            module.ProcessAction(new GameAction
            {
                UT = 300.0,
                Type = GameActionType.ContractAccept,
                ContractId = "contract-1"
            });

            Assert.Empty(module.GetWalkStateForTesting());
        }

        // ================================================================
        // Log-assertion tests
        // ================================================================

        [Fact]
        public void LogContract_ProcessRouteDispatched_EmitsInfoWithRouteAndCycle()
        {
            // Fails if the dispatch log line stops carrying the route id or cycle
            // count — debugging a missing dispatch in a player log depends on
            // those two tokens being present.
            module.ProcessAction(MakeDispatched("route-LOG-1", "cyc-X", 500.0));

            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("Processed RouteDispatched")
                && l.Contains("route=route-LOG-1")
                && l.Contains("cycle=1"));
        }

        [Fact]
        public void LogContract_Reset_EmitsVerboseWithPreviousCount()
        {
            // Fails if Reset stops reporting the prevCount — a non-zero
            // leak-between-walks signal would otherwise vanish from the log.
            module.ProcessAction(MakeDispatched("route-LOG-2"));
            module.ProcessAction(MakeDispatched("route-LOG-3"));

            module.Reset();

            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("Reset")
                && l.Contains("2 route"));
        }

        // ================================================================
        // Snapshot helper
        // ================================================================

        /// <summary>
        /// Deterministic serialization of the walk state so two walks can be
        /// equality-compared without depending on dictionary enumeration order.
        /// </summary>
        private static string Snapshot(RouteModule m)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in m.GetWalkStateForTesting().OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.Append(kv.Key).Append('|')
                    .Append(kv.Value.DispatchedCycles).Append('|')
                    .Append(kv.Value.DeliveredStops).Append('|')
                    .Append(kv.Value.Paused).Append('|')
                    .Append(kv.Value.EndpointLost).Append('|')
                    .Append(kv.Value.LastReason ?? "").Append('|')
                    .Append(kv.Value.LastActionUT.ToString("R",
                        System.Globalization.CultureInfo.InvariantCulture))
                    .Append(';');
            }
            return sb.ToString();
        }
    }
}
