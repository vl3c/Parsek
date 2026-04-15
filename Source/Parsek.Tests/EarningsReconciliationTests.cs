using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers §I/#394 of career-earnings-bundle plan: a commit-time reconciliation
    /// diagnostic compares the sum of dropped FundsChanged/ReputationChanged/
    /// ScienceChanged events (which the converter intentionally drops to avoid
    /// double-counting against recovery/contract/milestone channels) against the
    /// sum of effective emitted earning/spending actions in the same UT window.
    ///
    /// The drop itself must remain — re-emitting FundsChanged as FundsEarning would
    /// double-count against recovery. The diagnostic is log-only and fires WARN
    /// when the two sides disagree beyond tolerance.
    /// </summary>
    [Collection("Sequential")]
    public class EarningsReconciliationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public EarningsReconciliationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameStateEvent MakeFundsChanged(double ut, double before, double after)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.FundsChanged,
                valueBefore = before,
                valueAfter = after
            };
        }

        [Fact]
        public void Reconcile_PerfectMatch_NoWarn()
        {
            // A recovery of +2000 captured as both a FundsChanged and a FundsEarning(Recovery).
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(150, 5000, 7000)  // +2000
            };
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 150,
                    Type = GameActionType.FundsEarning,
                    FundsSource = FundsEarningSource.Recovery,
                    FundsAwarded = 2000f
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                new List<GameAction>(), new List<GameAction>(),
                startUT: 100, endUT: 200);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
        }

        [Fact]
        public void Reconcile_MissingFundsEarning_LogsWarn()
        {
            // Store observed +8000 funds but no emitted action (the shape of c1's bug).
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(150, 0, 8000)
            };
            var newActions = new List<GameAction>();  // nothing emitted

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                new List<GameAction>(), new List<GameAction>(),
                startUT: 100, endUT: 200);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("Earnings reconciliation (funds)") &&
                l.Contains("8000") && l.Contains("0"));
        }

        [Fact]
        public void Reconcile_ReputationMismatch_LogsWarn()
        {
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 150,
                    eventType = GameStateEventType.ReputationChanged,
                    valueBefore = 10, valueAfter = 35
                }
            };
            var newActions = new List<GameAction>();

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                new List<GameAction>(), new List<GameAction>(),
                startUT: 100, endUT: 200);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("Earnings reconciliation (rep)") &&
                l.Contains("25"));  // 35 - 10 = 25
        }

        [Fact]
        public void Reconcile_ScienceMismatch_LogsWarn()
        {
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 150,
                    eventType = GameStateEventType.ScienceChanged,
                    valueBefore = 0, valueAfter = 16.44
                }
            };
            var newActions = new List<GameAction>();

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                new List<GameAction>(), new List<GameAction>(),
                startUT: 100, endUT: 200);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("Earnings reconciliation (sci)"));
        }

        [Fact]
        public void Reconcile_OutsideWindow_Ignored()
        {
            // Events outside the UT window should not contribute.
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(50, 0, 5000),   // before window
                MakeFundsChanged(250, 0, 5000)   // after window
            };
            var newActions = new List<GameAction>();

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                new List<GameAction>(), new List<GameAction>(),
                startUT: 100, endUT: 200);

            // Neither event counts — no warn.
            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
        }

        [Fact]
        public void Reconcile_ContractCompleteMatchesStore_Silent()
        {
            // Contract reward of 5000 funds + 10 rep should match a dropped FundsChanged
            // (+5000) and dropped ReputationChanged (+10).
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(150, 0, 5000),
                new GameStateEvent
                {
                    ut = 150,
                    eventType = GameStateEventType.ReputationChanged,
                    valueBefore = 0, valueAfter = 10
                }
            };
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 150,
                    Type = GameActionType.ContractComplete,
                    FundsReward = 5000f,
                    RepReward = 10f,
                    ContractId = "c1"
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                new List<GameAction>(), new List<GameAction>(),
                startUT: 100, endUT: 200);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
        }

        [Fact]
        public void Reconcile_MilestoneAchievementMatchesStore_Silent()
        {
            // Milestone awarding 3000 funds + 5 rep should match dropped channels.
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(150, 0, 3000),
                new GameStateEvent
                {
                    ut = 150,
                    eventType = GameStateEventType.ReputationChanged,
                    valueBefore = 0, valueAfter = 5
                }
            };
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 150,
                    Type = GameActionType.MilestoneAchievement,
                    MilestoneFundsAwarded = 3000f,
                    MilestoneRepAwarded = 5f,
                    MilestoneId = "Mun/Landing"
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                new List<GameAction>(), new List<GameAction>(),
                startUT: 100, endUT: 200);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
        }

        [Fact]
        public void Reconcile_FundsSpendingMatchesDroppedNegative_Silent()
        {
            // A part purchase of 600 should show as a dropped FundsChanged (-600)
            // AND a FundsSpending(600).
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(150, 10000, 9400)
            };
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 150,
                    Type = GameActionType.FundsSpending,
                    FundsSpendingSource = FundsSpendingSource.Other,
                    FundsSpent = 600f
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                new List<GameAction>(), new List<GameAction>(),
                startUT: 100, endUT: 200);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
        }
    }
}
