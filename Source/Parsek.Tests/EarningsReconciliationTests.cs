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
                startUT: 100, endUT: 200);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
        }

        // ================================================================
        // Phase B — KSC-side per-action reconciliation
        // (LedgerOrchestrator.ReconcileKscAction, wired from OnKscSpending)
        // ================================================================

        [Fact]
        public void ReconcileKsc_PartPurchaseMatchesFundsChanged_NoWarn()
        {
            // KSC part purchase of 600 funds with matching FundsChanged.
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(1000, 50000, 49400)  // -600
            };
            var action = new GameAction
            {
                UT = 1000,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f,
                DedupKey = "solidBooster.v2"
            };

            LedgerOrchestrator.ReconcileKscAction(events, action, ut: 1000);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        [Fact]
        public void ReconcileKsc_PartPurchaseMismatch_LogsWarn()
        {
            // KSC part purchase logs 600 spent but the store observed -610 (off by 10).
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(1000, 50000, 49390)  // -610
            };
            var action = new GameAction
            {
                UT = 1000,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f,
                DedupKey = "solidBooster.v2"
            };

            LedgerOrchestrator.ReconcileKscAction(events, action, ut: 1000);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("KSC reconciliation (funds)") &&
                l.Contains("FundsSpending") &&
                l.Contains("-600") &&
                l.Contains("-610"));
        }

        [Fact]
        public void ReconcileKsc_FacilityUpgradeMatchesFunds_NoWarn()
        {
            // Facility upgrade costing 75000 funds with matching FundsChanged.
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(2000, 200000, 125000)  // -75000
            };
            var action = new GameAction
            {
                UT = 2000,
                Type = GameActionType.FacilityUpgrade,
                FacilityId = "VehicleAssemblyBuilding",
                ToLevel = 2,
                FacilityCost = 75000f
            };

            LedgerOrchestrator.ReconcileKscAction(events, action, ut: 2000);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        [Fact]
        public void ReconcileKsc_TechUnlockMatchesScience_NoWarn()
        {
            // Tech node unlock costing 45 science with matching ScienceChanged.
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 3000,
                    eventType = GameStateEventType.ScienceChanged,
                    valueBefore = 120,
                    valueAfter = 75  // -45
                }
            };
            var action = new GameAction
            {
                UT = 3000,
                Type = GameActionType.ScienceSpending,
                NodeId = "basicRocketry",
                Cost = 45f
            };

            LedgerOrchestrator.ReconcileKscAction(events, action, ut: 3000);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        [Fact]
        public void ReconcileKsc_TechUnlockMismatch_LogsWarn()
        {
            // Tech node unlock recorded as 45 science but the store observed -55 (off by 10).
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 3000,
                    eventType = GameStateEventType.ScienceChanged,
                    valueBefore = 120,
                    valueAfter = 65  // -55
                }
            };
            var action = new GameAction
            {
                UT = 3000,
                Type = GameActionType.ScienceSpending,
                NodeId = "basicRocketry",
                Cost = 45f
            };

            LedgerOrchestrator.ReconcileKscAction(events, action, ut: 3000);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("KSC reconciliation (sci)") &&
                l.Contains("ScienceSpending") &&
                l.Contains("-45") &&
                l.Contains("-55"));
        }

        [Fact]
        public void ReconcileKsc_EventOutsideEpsilon_LogsWarn()
        {
            // A FundsChanged far outside the epsilon window (1 second away) must NOT
            // be paired with the action; with no paired event the action's expected
            // -600 shows as a mismatch vs observed 0 and WARNs.
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(1000 - 1.0, 50000, 49400)  // 1 s before, outside 0.5 s epsilon
            };
            var action = new GameAction
            {
                UT = 1000,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f,
                DedupKey = "solidBooster.v2"
            };

            LedgerOrchestrator.ReconcileKscAction(events, action, ut: 1000);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("KSC reconciliation (funds)"));
        }

        [Fact]
        public void ReconcileKsc_KerbalAssignment_NoReconciliation()
        {
            // Non-resource action types (e.g. KerbalAssignment) short-circuit — no WARN
            // even if unrelated FundsChanged events are nearby.
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(4000, 0, 12345)  // entirely unrelated
            };
            var action = new GameAction
            {
                UT = 4000,
                Type = GameActionType.KerbalAssignment,
                KerbalName = "Jeb"
            };

            LedgerOrchestrator.ReconcileKscAction(events, action, ut: 4000);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        [Fact]
        public void ReconcileKsc_NullEvents_NoThrow()
        {
            // Defensive: null events list should be handled. The action expected a
            // funds delta and the observed was 0, so a WARN still fires.
            var action = new GameAction
            {
                UT = 5000,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 100f,
                DedupKey = "part"
            };

            LedgerOrchestrator.ReconcileKscAction(null, action, ut: 5000);

            Assert.Contains(logLines, l => l.Contains("KSC reconciliation (funds)"));
        }
    }
}
