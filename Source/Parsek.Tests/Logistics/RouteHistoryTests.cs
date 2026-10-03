using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Threading;
using Parsek;
using Parsek.Logistics;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The Route History window: its rows from synthetic route ledger actions
    /// (<see cref="RouteHistoryBuilder"/>), its per-row dates, its title and its seam
    /// token, and that it is a SECOND instance of the log-table window (its own id), so it
    /// stands beside an open Mission Log.
    /// </summary>
    [Collection("Sequential")]
    public class RouteHistoryTests
    {
        private const string RouteId = "route-a";

        private static GameAction Act(GameActionType type, double ut, string cycle = null,
            int stop = -1, Dictionary<string, double> actual = null,
            Dictionary<string, double> requested = null, uint pid = 0u,
            bool sendOnce = false, string reason = null, string routeId = RouteId)
        {
            return new GameAction
            {
                Type = type, UT = ut, RouteId = routeId, RouteCycleId = cycle,
                RouteStopIndex = stop, RouteResourceManifest = actual,
                RouteRequestedResourceManifest = requested, RouteOriginVesselPid = pid,
                RouteSendOnce = sendOnce, RouteEndpointReason = reason,
            };
        }

        private static Dictionary<string, double> Fuel(double amount)
            => new Dictionary<string, double> { { "LiquidFuel", amount } };

        private static List<StructureStep> Build(List<GameAction> els)
        {
            return RouteHistoryBuilder.Build(els, RouteId, "KSC", null,
                stop => stop == 1 ? "Mun (surface)" : "Mun (orbit)",
                stop => stop == 1 ? "Mun Base" : "Depot",
                pid => pid == 42u ? "Relay B" : null);
        }

        // A run: Sent at the origin, Picked up at an intermediate stop, Delivered at the
        // destination (the row that finishes the run), each in its own place and vessel.
        [Fact]
        public void ARunReadsSentPickedUpDelivered_WithPlaceAndVessel()
        {
            var rows = Build(new List<GameAction>
            {
                Act(GameActionType.RouteDispatched, 100, "c1"),
                Act(GameActionType.RouteCargoDebited, 100, "c1"),
                Act(GameActionType.RouteCargoPickedUp, 200, "c1", 1, Fuel(50), pid: 42u),
                Act(GameActionType.RouteCargoDelivered, 300, "c1", 0, Fuel(150)),
            });
            Assert.Equal(3, rows.Count); // the debit row is not shown
            Assert.Equal("Run 1: Sent", rows[0].Label);
            Assert.Equal("KSC", rows[0].Location);
            Assert.Equal("-", rows[0].VesselName);
            Assert.Equal("Run 1: Picked up LiquidFuel 50.0", rows[1].Label);
            Assert.Equal("Mun (surface)", rows[1].Location);
            Assert.Equal("Relay B", rows[1].VesselName);
            Assert.Equal("Run 1: Delivered LiquidFuel 150.0", rows[2].Label);
            Assert.Equal("Mun (orbit)", rows[2].Location);
            Assert.Equal("Depot", rows[2].VesselName);
        }

        // Send-stamped runs read "Sent once"; runs number in dispatch order; a short
        // delivery shows its "of" amounts.
        [Fact]
        public void SendOnceRunsAndShortDeliveriesReadAsSuch()
        {
            var rows = Build(new List<GameAction>
            {
                Act(GameActionType.RouteDispatched, 100, "c1"),
                Act(GameActionType.RouteDispatched, 500, "c2", sendOnce: true),
                Act(GameActionType.RouteCargoDelivered, 600, "c2", 0, Fuel(40), Fuel(150)),
            });
            Assert.Equal("Run 1: Sent", rows[0].Label);
            Assert.Equal("Run 2: Sent once", rows[1].Label);
            Assert.StartsWith("Run 2: Delivered 40.0 of 150.0 LiquidFuel", rows[2].Label);
        }

        // State rows: pause, resume, endpoint loss, with their reasons in player words.
        [Fact]
        public void PauseResumeAndLossAreStateRows()
        {
            var rows = Build(new List<GameAction>
            {
                Act(GameActionType.RoutePaused, 10, reason: "player-pause"),
                Act(GameActionType.RouteResumed, 20, reason: "player-activate"),
                Act(GameActionType.RoutePaused, 30, reason: "delivered-then-paused"),
                Act(GameActionType.RouteResumed, 40, reason: "AutoResume:SourcesRestored"),
                Act(GameActionType.RoutePaused, 50, reason: "AutoPause:SourceChanged"),
                Act(GameActionType.RouteEndpointLost, 60, reason: "stop-0-no-live-vessels"),
            });
            Assert.Equal(new[]
                {
                    "Paused", "Activated", "Paused after the run", "Resumed",
                    "Stopped: flight changed", "Stopped: destination lost",
                },
                rows.Select(r => r.Label).ToArray());
        }

        // ELS filtering: the builder sees only what the caller passes - another route's
        // rows, a tombstoned run (absent from the ELS) and non-route rows never show.
        [Fact]
        public void OnlyThisRoutesEffectiveRowsShow_InTimeOrder()
        {
            var rows = Build(new List<GameAction>
            {
                Act(GameActionType.RouteCargoDelivered, 300, "c1", 0, Fuel(10)),
                Act(GameActionType.RouteDispatched, 100, "c1"),
                Act(GameActionType.RouteDispatched, 50, "x1", routeId: "other-route"),
                new GameAction { Type = GameActionType.FundsEarning, UT = 75 },
            });
            Assert.Equal(2, rows.Count);
            Assert.Equal(100.0, rows[0].UT);
            Assert.Equal("Run 1: Delivered LiquidFuel 10.0", rows[1].Label);
            Assert.Empty(Build(new List<GameAction>()));
            Assert.Empty(RouteHistoryBuilder.Build(null, RouteId, null, null, null, null, null));
        }

        // Every row carries its own date (a route's runs are days to years apart); the
        // fallback is invariant whatever the culture.
        [Fact]
        public void EveryRowIsDated_Invariantly()
        {
            var steps = new List<StructureStep>
            {
                new StructureStep { UT = 1234.6 }, new StructureStep { UT = double.NaN },
            };
            CultureInfo prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal(new[] { "UT 1235", "-" }, RouteHistoryBuilder.FormatRowTimes(steps, null));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
            Assert.Equal(new[] { "D", "-" }, RouteHistoryBuilder.FormatRowTimes(steps, ut => "D"));
        }

        [Fact]
        public void TitleEmptyStateAndSeamToken()
        {
            Assert.Equal("Parsek - Route History: Route A",
                StructureListWindowUI.BuildWindowTitle(StructureListWindowUI.RouteHistoryTitlePrefix, "Route A"));
            Assert.Equal("No runs yet.",
                StructureListWindowUI.EmptyText(StructureListWindowUI.TargetMode.Route));
            Assert.Equal("routehistory", TestCommandUiAction.RouteHistoryWindow);
            Assert.NotEqual(StructureListWindowUI.WindowIdKey, StructureListWindowUI.RouteHistoryWindowIdKey);
        }

        // The seam: window=routehistory takes route=, the Mission Log refuses route=.
        [Fact]
        public void TargetParse_RouteHistoryTakesRoute()
        {
            Assert.True(TestCommandUiState.TryParseTarget(TestCommandUiAction.RouteHistoryWindow,
                null, "r1", out UiTargetKind kind, out string value, out _));
            Assert.Equal(UiTargetKind.Route, kind);
            Assert.Equal("r1", value);
            Assert.Equal("route", TestCommandUiState.TargetKindToken(kind));
            Assert.False(TestCommandUiState.TryParseTarget(TestCommandUiAction.RouteHistoryWindow,
                "m", null, out _, out _, out string missing));
            Assert.Equal(TestCommandUiState.TargetArgMissingReason, missing);
            Assert.False(TestCommandUiState.TryParseTarget(TestCommandUiAction.StructureWindow,
                null, "r1", out _, out _, out string retired));
            Assert.Equal(TestCommandUiState.TargetRouteRetiredReason, retired);
        }

        // Two instances: the Route History has its own open state beside the Mission Log.
        [Fact]
        public void RouteHistoryIsASecondWindowBesideTheMissionLog()
        {
            var ui = new ParsekUI(UIMode.KSC);
            try
            {
                StructureListWindowUI log = ui.GetStructureListUI();
                StructureListWindowUI history = ui.GetRouteHistoryUI();
                Assert.NotSame(log, history);
                Assert.True(history.IsRouteHistory);
                Assert.False(log.IsRouteHistory);
                Assert.Equal(StructureListWindowUI.RouteHistoryWindowIdKey, history.InstanceWindowIdKey);
                log.IsOpen = true;
                history.IsOpen = true;
                Assert.True(log.IsOpen && history.IsOpen);
                history.IsOpen = false;
                Assert.True(log.IsOpen);
            }
            finally
            {
                try { ui.Cleanup(); }
                catch (SecurityException) { }
                catch (MissingMethodException) { }
            }
        }

        [Fact]
        public void ChangeSignatureMovesWithEachInput()
        {
            int a = LogisticsRoutePresentation.RouteHistoryChangeSignature(1, 0, true, "A");
            Assert.NotEqual(a, LogisticsRoutePresentation.RouteHistoryChangeSignature(2, 0, true, "A"));
            Assert.NotEqual(a, LogisticsRoutePresentation.RouteHistoryChangeSignature(1, 1, true, "A"));
            Assert.NotEqual(a, LogisticsRoutePresentation.RouteHistoryChangeSignature(1, 0, false, "A"));
            Assert.NotEqual(a, LogisticsRoutePresentation.RouteHistoryChangeSignature(1, 0, true, "B"));
            Assert.Equal(a, LogisticsRoutePresentation.RouteHistoryChangeSignature(1, 0, true, "A"));
        }
    }
}
