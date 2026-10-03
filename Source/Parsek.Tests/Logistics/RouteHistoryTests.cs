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
            Assert.Equal("Run 1: Picked up 50.0 LiquidFuel", rows[1].Label);
            Assert.Equal("Mun (surface)", rows[1].Location);
            Assert.Equal("Relay B", rows[1].VesselName);
            Assert.Equal("Run 1: Delivered 150.0 LiquidFuel", rows[2].Label);
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

        // Amount first throughout, like the Sent cost and a short delivery: several
        // resources in ordinal order, stored parts counted after them; the Logistics
        // table's Delivers column keeps its own "LiquidFuel 150.0" shape.
        [Fact]
        public void CargoRowsReadAmountFirst()
        {
            var two = new Dictionary<string, double> { { "Oxidizer", 40.0 }, { "LiquidFuel", 150.0 } };
            var rows = Build(new List<GameAction>
            {
                Act(GameActionType.RouteDispatched, 100, "c1"),
                Act(GameActionType.RouteCargoPickedUp, 200, "c1", 1, two, pid: 42u),
                new GameAction
                {
                    Type = GameActionType.RouteCargoDelivered, UT = 300, RouteId = RouteId,
                    RouteCycleId = "c1", RouteStopIndex = 0, RouteResourceManifest = two,
                    RouteInventoryManifest = new List<InventoryPayloadItem> { new InventoryPayloadItem() },
                },
                Act(GameActionType.RouteCargoDelivered, 400, "c1", 0, new Dictionary<string, double>()),
            });
            Assert.Equal("Run 1: Picked up 150.0 LiquidFuel, 40.0 Oxidizer", rows[1].Label);
            Assert.Equal("Run 1: Delivered 150.0 LiquidFuel, 40.0 Oxidizer, 1 stored part", rows[2].Label);
            Assert.Equal("Run 1: Delivered nothing", rows[3].Label);
            Assert.Equal("LiquidFuel 150.0", LogisticsDeliveryPresentation.FormatWouldDeliver(Fuel(150), null));
        }

        // Amount-first rows and held rows stay InvariantCulture under a comma locale.
        [Fact]
        public void CargoAndHeldRowsFormatInvariantly()
        {
            CultureInfo prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var rows = Build(new List<GameAction>
                {
                    Act(GameActionType.RouteCargoDelivered, 300, "c1", 0, Fuel(1234.5)),
                    Held(400, RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                        "LiquidFuel", 108.79999999999706),
                    Held(500, RouteDispatchEvaluator.EligibilityFailureKind.FundsShort, "funds-short", 7410.4),
                });
                Assert.Equal("Run 1: Delivered 1234.5 LiquidFuel", rows[0].Label);
                Assert.Equal("Held: origin is short 108.8 LiquidFuel", rows[1].Label);
                Assert.Equal("Held: not enough funds at KSC - short 7410 funds for this run", rows[2].Label);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
        }

        private static GameAction Held(double ut, RouteDispatchEvaluator.EligibilityFailureKind kind,
            string detail, double shortfall = 0.0)
        {
            return new GameAction
            {
                Type = GameActionType.RouteHeld, UT = ut, RouteId = RouteId, RouteCycleId = "c9",
                RouteStopIndex = -1, RouteHoldKind = kind, RouteEndpointReason = detail,
                RouteHoldShortfall = shortfall,
            };
        }

        private static List<StructureStep> BuildFromOrigin(List<GameAction> els,
            Func<string, string> partTitle = null)
        {
            return RouteHistoryBuilder.Build(els, RouteId, "Mun (surface)", "Mun Base",
                stop => "Minmus (orbit)", stop => "Depot",
                pid => pid == 42u ? "Relay B" : null, partTitle,
                id => id == "route-b" ? "Return Run" : null);
        }

        // A held run reads "Held: <reason>" in the Logistics window's words, without the
        // live "- delivers when ..." advice and with no run number; the Time column
        // carries the date. Origin-cargo and funds holds sit at the origin, a pickup
        // source hold names that source's vessel (live name first), everything else "-".
        [Fact]
        public void HeldRowsReadTheReason_WithoutAdviceOrRunNumber_AtTheirPlace()
        {
            var rows = BuildFromOrigin(new List<GameAction>
            {
                Act(GameActionType.RouteDispatched, 100, "c1"),
                Held(200, RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo, "LiquidFuel", 108.8),
                Held(300, RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull, "LiquidFuel"),
                Held(400, RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                    "source:42:Depot B:Ore", 20.0),
                Held(500, RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                    "source:77:Depot C:Ore", 20.0),
                Held(600, RouteDispatchEvaluator.EligibilityFailureKind.EndpointLost, "stop-0-no-live-vessels"),
                Held(700, RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner, "partner:route-b"),
                Act(GameActionType.RouteDispatched, 800, "c2"),
            });
            Assert.Equal(new[]
                {
                    "Run 1: Sent",
                    "Held: origin is short 108.8 LiquidFuel",
                    "Held: destination has no room for LiquidFuel",
                    "Held: Depot B is short 20.0 Ore",
                    "Held: Depot C is short 20.0 Ore",
                    "Held: destination vessel could not be found",
                    "Held: waiting for the linked route 'Return Run' to complete its run",
                    "Run 2: Sent",
                },
                rows.Select(r => r.Label).ToArray());
            Assert.Equal("Mun (surface)", rows[1].Location);
            Assert.Equal("Mun Base", rows[1].VesselName);
            Assert.Equal("-", rows[2].Location);
            Assert.Equal("-", rows[2].VesselName);
            Assert.Equal("-", rows[3].Location);
            Assert.Equal("Relay B", rows[3].VesselName);
            Assert.Equal("Depot C", rows[4].VesselName);
            Assert.Equal("-", rows[5].VesselName);
            Assert.Equal(200.0, rows[1].UT);
            Assert.All(rows.Skip(1).Take(6), r => Assert.DoesNotContain("delivers when", r.Label));
            Assert.All(rows.Skip(1).Take(6), r => Assert.DoesNotContain("Run ", r.Label));
        }

        // Stored parts in a held reason read by their title, not the internal name; a
        // name with no title falls back to itself. A kind that did not read back (an
        // unknown name from a newer build) renders plain "Held".
        [Fact]
        public void HeldRowsNamePartsByTitle_AndAnUnknownKindReadsHeld()
        {
            Func<string, string> title = name => name == "evaScienceKit" ? "EVA Science Kit" : null;
            var rows = BuildFromOrigin(new List<GameAction>
            {
                Held(100, RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull, "stored-part:evaScienceKit"),
                Held(200, RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo, "inventory:evaScienceKit"),
                Held(300, RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo, "inventory:mysteryPart"),
                Held(400, RouteDispatchEvaluator.EligibilityFailureKind.None, "whatever"),
            }, title);
            Assert.Equal(new[]
                {
                    "Held: destination has no free inventory slot for stored part 'EVA Science Kit'",
                    "Held: origin is missing stored part 'EVA Science Kit'",
                    "Held: origin is missing stored part 'mysteryPart'",
                    "Held",
                },
                rows.Select(r => r.Label).ToArray());
        }

        // A linked-route wait stores the partner's id; the row names its CURRENT name, and
        // reads "the linked route" once the partner no longer exists.
        [Fact]
        public void PartnerHeldRow_NamesThePartnerById()
        {
            var rows = BuildFromOrigin(new List<GameAction>
            {
                Held(100, RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner, "partner:route-b"),
                Held(200, RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner, "partner:route-gone"),
            });
            Assert.Equal("Held: waiting for the linked route 'Return Run' to complete its run", rows[0].Label);
            Assert.Equal("Held: waiting for the linked route to complete its run", rows[1].Label);
        }

        // The history sentence never carries live-route advice, whatever the clause.
        [Fact]
        public void HistorySentenceDropsEveryAdviceTail()
        {
            var cases = new[]
            {
                (RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo, "LiquidFuel", 0.0, "origin is out of LiquidFuel"),
                (RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo, "origin-lacks-LiquidFuel", 12.0, "origin is short 12.0 LiquidFuel"),
                (RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo, "origin-unresolved:pid=7", 0.0, "origin vessel could not be found"),
                (RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo, "pickup-source-unresolved:pid=7", 0.0, "a pickup source vessel could not be found"),
                (RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo, "source-reserved:42:Depot B:Ore:Other Run", 0.0, "Depot B has Ore reserved by route 'Other Run'"),
                (RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull, "destination-full-Ore", 0.0, "destination has no room for Ore"),
                (RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull, "stored-part:", 0.0, "destination has no free inventory slot for a stored part"),
                (RouteDispatchEvaluator.EligibilityFailureKind.EndpointLost, "origin-gone", 0.0, "origin vessel could not be found"),
                (RouteDispatchEvaluator.EligibilityFailureKind.SourcesStale, "sources-stale", 0.0, "a flight this route copies is unavailable right now"),
                (RouteDispatchEvaluator.EligibilityFailureKind.FundsShort, "funds-short", 0.0, "not enough funds at KSC for this run"),
            };
            foreach (var c in cases)
            {
                Assert.Equal(c.Item4,
                    LogisticsHoldPresentation.DescribeHoldForHistory(c.Item1, c.Item2, c.Item3, null));
            }
            Assert.Null(LogisticsHoldPresentation.DescribeHoldForHistory(
                RouteDispatchEvaluator.EligibilityFailureKind.None, "x", 0.0, null));
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
            Assert.Equal("Run 1: Delivered 10.0 LiquidFuel", rows[1].Label);
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
