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

        // A run's debit row (same RouteCycleId) as the dispatch writes it: funds on a
        // Career KSC launch, the actual cargo plus the origin pid on a vessel debit.
        private static GameAction Debit(double ut, string cycle, float funds = 0f,
            uint originPid = 0u, Dictionary<string, double> cargo = null)
        {
            return new GameAction
            {
                Type = GameActionType.RouteCargoDebited, UT = ut, RouteId = RouteId,
                RouteCycleId = cycle, RouteKscFundsCost = funds,
                RouteOriginVesselPid = originPid, RouteResourceManifest = cargo,
            };
        }

        private static Dictionary<string, double> LfOx()
            => new Dictionary<string, double> { { "LiquidFuel", 257.8 }, { "Oxidizer", 315.1 } };

        // Career KSC launch: the Sent row carries its funds cost, grouped whole funds; a
        // Send-armed run the same way. The debit row itself stays hidden.
        [Fact]
        public void CareerSentRowShowsTheFundsCost()
        {
            var rows = Build(new List<GameAction>
            {
                Act(GameActionType.RouteDispatched, 100, "c1"),
                Debit(100, "c1", funds: 7410f, cargo: LfOx()),
                Act(GameActionType.RouteDispatched, 500, "c2", sendOnce: true),
                Debit(500, "c2", funds: 7410f, cargo: LfOx()),
            });
            Assert.Equal(new[]
                {
                    "Run 1: Sent, cost 7,410 funds", "Run 2: Sent once, cost 7,410 funds",
                },
                rows.Select(r => r.Label).ToArray());
        }

        // A launch that took cargo from its origin vessel lists it after the funds; both
        // together read "cost <funds>, <cargo>".
        [Fact]
        public void OriginCargoDebitIsPartOfTheCost()
        {
            var rows = Build(new List<GameAction>
            {
                Act(GameActionType.RouteDispatched, 100, "c1"),
                Debit(100, "c1", originPid: 77u, cargo: LfOx()),
            });
            Assert.Equal("Run 1: Sent, cost 257.8 LiquidFuel, 315.1 Oxidizer", rows.Single().Label);
            Assert.Equal(", cost 7,410 funds, 257.8 LiquidFuel, 315.1 Oxidizer",
                RouteHistoryBuilder.SentCostSuffix(Debit(100, "c1", 7410f, 77u, LfOx())));
            Assert.Equal("", RouteHistoryBuilder.SentCostSuffix(
                Debit(100, "c1", originPid: 77u, cargo: new Dictionary<string, double> { { "Ore", 0.0 } })));
            Assert.Equal("", RouteHistoryBuilder.SentCostSuffix(null));
        }

        // Sandbox / Science: the dispatch writes no funds cost, and a KSC launch's manifest
        // is what the launch carried, not a cost, so the row reads plain "Sent".
        [Fact]
        public void SandboxSentRowHasNoFundsPart()
        {
            var rows = Build(new List<GameAction>
            {
                Act(GameActionType.RouteDispatched, 100, "c1"),
                Debit(100, "c1", funds: 0f, originPid: 0u, cargo: LfOx()),
            });
            Assert.Equal("Run 1: Sent", rows.Single().Label);
        }

        // Interleaved runs: each Sent row takes its own run's debit by RouteCycleId, never the
        // nearest row; a debit with no Sent row of its own adds no row.
        [Fact]
        public void DebitJoinsItsOwnRun_AndAnOrphanDebitAddsNoRow()
        {
            var rows = Build(new List<GameAction>
            {
                Act(GameActionType.RouteDispatched, 100, "c1"),
                Act(GameActionType.RouteDispatched, 200, "c2"),
                Debit(200, "c2", funds: 500f),
                Debit(100, "c1", funds: 7410f),
                Debit(300, "c3", funds: 999f),
            });
            Assert.Equal(new[] { "Run 1: Sent, cost 7,410 funds", "Run 2: Sent, cost 500 funds" },
                rows.Select(r => r.Label).ToArray());
        }

        // The cost text is InvariantCulture whatever the thread culture.
        [Fact]
        public void SentCostFormatsInvariantly()
        {
            CultureInfo prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal(", cost 1,234,567 funds, 257.8 LiquidFuel, 315.1 Oxidizer",
                    RouteHistoryBuilder.SentCostSuffix(Debit(1, "c1", 1234567f, 77u, LfOx())));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
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

        // A Send-armed run that delivered on a replayed crossing pauses with its own
        // reason; it reads the same as any other run that finished and paused.
        [Fact]
        public void DeliveredReplayThenPaused_ReadsPausedAfterTheRun()
        {
            Assert.Equal("Paused after the run",
                RouteHistoryBuilder.PausedLabel(RouteOrchestrator.DeliveredReplayThenPausedReason));
            var rows = Build(new List<GameAction>
            {
                Act(GameActionType.RoutePaused, 10, reason: RouteOrchestrator.DeliveredReplayThenPausedReason),
            });
            Assert.Equal("Paused after the run", rows.Single().Label);
        }

        // The window chrome: the Route History opens offset from the Mission Log, keeps its
        // table (headers and a "No runs yet." row) when empty, and closes when its route
        // is gone.
        [Fact]
        public void RouteHistoryOpensOffset_KeepsItsTableEmpty_AndClosesOnDeletedRoute()
        {
            var main = new UnityEngine.Rect(10f, 20f, 250f, 300f);
            UnityEngine.Rect log = StructureListWindowUI.DefaultWindowRect(main, false);
            UnityEngine.Rect history = StructureListWindowUI.DefaultWindowRect(main, true);
            Assert.Equal(270f, log.x);
            Assert.Equal(20f, log.y);
            Assert.Equal(log.x + StructureListWindowUI.RouteHistoryCascadeOffset, history.x);
            Assert.Equal(log.y + StructureListWindowUI.RouteHistoryCascadeOffset, history.y);
            Assert.Equal(log.width, history.width);
            Assert.Equal(log.height, history.height);

            Assert.True(StructureListWindowUI.DrawsTableWhenEmpty(StructureListWindowUI.TargetMode.Route));
            Assert.False(StructureListWindowUI.DrawsTableWhenEmpty(StructureListWindowUI.TargetMode.Mission));

            var route = StructureListWindowUI.TargetMode.Route;
            Assert.True(StructureListWindowUI.ShouldCloseForDeletedRoute(route, "r1", false));
            Assert.False(StructureListWindowUI.ShouldCloseForDeletedRoute(route, "r1", true));
            Assert.False(StructureListWindowUI.ShouldCloseForDeletedRoute(route, null, false));
            Assert.False(StructureListWindowUI.ShouldCloseForDeletedRoute(
                StructureListWindowUI.TargetMode.Mission, "r1", false));
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
