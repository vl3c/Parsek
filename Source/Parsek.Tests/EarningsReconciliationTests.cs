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
        //
        // Matcher scope: UNTRANSFORMED action types only — part purchase, tech unlock,
        // facility upgrade/repair, kerbal hire, contract advance. Events are paired by
        // type + KSP TransactionReasons key (written as GameStateEvent.key by the
        // OnFundsChanged/OnScienceChanged/OnReputationChanged handlers) within the
        // 0.5 s epsilon.
        //
        // Transformed types (contract rewards, milestones, reputation earnings) skip
        // with a VERBOSE line until a post-walk reconciliation hook lands (Phase D).
        // ================================================================

        /// <summary>Helper for the new signature.</summary>
        private static void ReconcileKsc(
            List<GameStateEvent> events, List<GameAction> ledger, GameAction action, double ut)
        {
            LedgerOrchestrator.ReconcileKscAction(events, ledger, action, ut);
        }

        private static GameStateEvent MakeKeyedFundsChanged(double ut, double before, double after, string reason)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.FundsChanged,
                key = reason,
                valueBefore = before,
                valueAfter = after
            };
        }

        private static GameStateEvent MakeKeyedScienceChanged(double ut, double before, double after, string reason)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.ScienceChanged,
                key = reason,
                valueBefore = before,
                valueAfter = after
            };
        }

        // ---------- Key-match positive (no WARN on correct pair) ----------

        [Fact]
        public void ReconcileKsc_PartPurchase_KeyedRnDPartPurchase_NoWarn()
        {
            // KSC part purchase of 600 funds; the paired FundsChanged event is keyed
            // 'RnDPartPurchase' (TransactionReasons.RnDPartPurchase.ToString()).
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(1000, 50000, 49400, "RnDPartPurchase")  // -600
            };
            var action = new GameAction
            {
                UT = 1000,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f,
                DedupKey = "solidBooster.v2"
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 1000);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        [Fact]
        public void ReconcileKsc_FacilityUpgrade_KeyedStructureConstruction_NoWarn()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(2000, 200000, 125000, "StructureConstruction")  // -75000
            };
            var action = new GameAction
            {
                UT = 2000,
                Type = GameActionType.FacilityUpgrade,
                FacilityId = "VehicleAssemblyBuilding",
                ToLevel = 2,
                FacilityCost = 75000f
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 2000);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        [Fact]
        public void ReconcileKsc_FacilityRepair_KeyedStructureRepair_NoWarn()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(2050, 100000, 75000, "StructureRepair")  // -25000
            };
            var action = new GameAction
            {
                UT = 2050,
                Type = GameActionType.FacilityRepair,
                FacilityId = "LaunchPad",
                FacilityCost = 25000f
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 2050);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        [Fact]
        public void ReconcileKsc_TechUnlock_KeyedRnDTechResearch_NoWarn()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedScienceChanged(3000, 120, 75, "RnDTechResearch")  // -45
            };
            var action = new GameAction
            {
                UT = 3000,
                Type = GameActionType.ScienceSpending,
                NodeId = "basicRocketry",
                Cost = 45f
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 3000);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        [Fact]
        public void ReconcileKsc_KerbalHire_KeyedCrewRecruited_NoWarn()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(400, 50000, -12113, "CrewRecruited")  // -62113
            };
            var action = new GameAction
            {
                UT = 400,
                Type = GameActionType.KerbalHire,
                KerbalName = "Jeb",
                HireCost = 62113f
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 400);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        [Fact]
        public void ReconcileKsc_ContractAdvance_KeyedContractAdvance_NoWarn()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(412.9, 22000, 24000, "ContractAdvance")  // +2000
            };
            var action = new GameAction
            {
                UT = 412.9,
                Type = GameActionType.ContractAccept,
                ContractId = "some-guid",
                AdvanceFunds = 2000f
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 412.9);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        // ---------- Key-match negative (type-correct, key-mismatch → WARN) ----------

        [Fact]
        public void ReconcileKsc_PartPurchase_EventKeyedDifferently_LogsWarn()
        {
            // Event exists but is keyed for a different reason (e.g. StructureConstruction
            // firing at almost the same UT as a PartPurchased callback). The matcher
            // must NOT latch onto the mismatched event. With no matching event, the
            // action's -600 produces a "missing event" WARN.
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(1000, 50000, 49400, "StructureConstruction")
            };
            var action = new GameAction
            {
                UT = 1000,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f,
                DedupKey = "solidBooster.v2"
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 1000);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("KSC reconciliation (funds)") &&
                l.Contains("FundsSpending") &&
                l.Contains("RnDPartPurchase") &&
                l.Contains("no matching"));
        }

        [Fact]
        public void ReconcileKsc_TechUnlock_DeltaMismatch_LogsWarn()
        {
            // Event is correctly keyed but delta disagrees by 10 sci.
            var events = new List<GameStateEvent>
            {
                MakeKeyedScienceChanged(3000, 120, 65, "RnDTechResearch")  // -55
            };
            var action = new GameAction
            {
                UT = 3000,
                Type = GameActionType.ScienceSpending,
                NodeId = "basicRocketry",
                Cost = 45f  // action expects -45
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 3000);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("KSC reconciliation (sci)") &&
                l.Contains("ScienceSpending") &&
                l.Contains("delta mismatch"));
        }

        // ---------- Transformed-type skip (VERBOSE, no WARN) ----------

        [Fact]
        public void ReconcileKsc_ContractComplete_SkipsWithVerbose()
        {
            // A matching FundsChanged event exists and delta disagrees — but
            // ContractComplete is in the transformed set (strategy transform + rep curve),
            // so the matcher must not WARN.
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(500, 20000, 24700, "ContractReward"),
                new GameStateEvent { ut = 500, eventType = GameStateEventType.ReputationChanged, key = "ContractReward", valueBefore = 5, valueAfter = 12 }
            };
            var action = new GameAction
            {
                UT = 500,
                Type = GameActionType.ContractComplete,
                ContractId = "c1",
                FundsReward = 5000f,   // doesn't match observed 4700 (strategy diverted)
                RepReward = 10f         // doesn't match observed +7 (curve)
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 500);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("KSC reconciliation") && l.Contains("WARN"));
            // Not strictly required that VERBOSE fires (rate-limited), but the skip path
            // must not produce a WARN regardless of how far the raw fields diverge.
            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation (funds)"));
            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation (rep)"));
        }

        [Fact]
        public void ReconcileKsc_MilestoneAchievement_SkipsWithVerbose()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(600, 20000, 22000, "Progression"),
                new GameStateEvent { ut = 600, eventType = GameStateEventType.ReputationChanged, key = "Progression", valueBefore = 0, valueAfter = 3 }
            };
            var action = new GameAction
            {
                UT = 600,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "FirstLaunch",
                MilestoneFundsAwarded = 800f,  // doesn't match observed +2000
                MilestoneRepAwarded = 5f        // doesn't match observed +3
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 600);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation (funds)"));
            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation (rep)"));
        }

        [Fact]
        public void ReconcileKsc_ReputationEarning_SkipsWithVerbose()
        {
            var events = new List<GameStateEvent>
            {
                new GameStateEvent { ut = 700, eventType = GameStateEventType.ReputationChanged, key = "Progression", valueBefore = 10, valueAfter = 17 }
            };
            var action = new GameAction
            {
                UT = 700,
                Type = GameActionType.ReputationEarning,
                NominalRep = 10f    // curve squeezed to +7
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 700);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation (rep)"));
        }

        [Fact]
        public void ReconcileKsc_ContractCancel_SkipsWithVerbose()
        {
            // FundsPenalty is raw but RepPenalty is curve-affected — skip until post-walk hook.
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(800, 20000, 19000, "ContractPenalty"),
                new GameStateEvent { ut = 800, eventType = GameStateEventType.ReputationChanged, key = "ContractPenalty", valueBefore = 10, valueAfter = 7 }
            };
            var action = new GameAction
            {
                UT = 800,
                Type = GameActionType.ContractCancel,
                ContractId = "c2",
                FundsPenalty = 1000f,
                RepPenalty = 5f       // curve-compressed to -3
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 800);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation (funds)"));
            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation (rep)"));
        }

        // ---------- Coalescing regression (reviewer's P2 finding) ----------

        [Fact]
        public void ReconcileKsc_TwoPartPurchases_CoalescedEvent_NoFalseWarn()
        {
            // Two KSC part purchases back-to-back at the same UT (600 + 400). The store
            // coalesced them into ONE FundsChanged with summed delta -1000. Both actions
            // are already in the ledger. When the second reconcile runs it sums expected
            // across both ledger actions (1000) against the coalesced event (1000) and
            // stays silent. This pins the P2 "coalescing cross-attributes deltas" fix.
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(1000, 50000, 49000, "RnDPartPurchase")  // coalesced -1000
            };
            var action1 = new GameAction
            {
                UT = 1000,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f,
                DedupKey = "solidBooster.v2"
            };
            var action2 = new GameAction
            {
                UT = 1000.02,  // within 0.1s coalesce window
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 400f,
                DedupKey = "fuelTankSmallFlat"
            };
            var ledger = new List<GameAction> { action1, action2 };

            // Reconcile the SECOND action (simulates OnKscSpending having added both).
            ReconcileKsc(events, ledger, action2, 1000.02);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        [Fact]
        public void ReconcileKsc_TwoPartPurchases_CoalescedFirstActionAlsoSilent()
        {
            // Mirror of the above but from the first action's perspective.
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(1000, 50000, 49000, "RnDPartPurchase")  // coalesced -1000
            };
            var action1 = new GameAction
            {
                UT = 1000,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f,
                DedupKey = "a"
            };
            var action2 = new GameAction
            {
                UT = 1000.05,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 400f,
                DedupKey = "b"
            };
            var ledger = new List<GameAction> { action1, action2 };

            ReconcileKsc(events, ledger, action1, 1000);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        // ---------- Windowing: aggregation scoped to coalesce window ----------
        //
        // Round-3 review: the aggregation window must match GameStateStore.AddEvent's
        // coalesce threshold (0.1 s). Two same-key events 0.1-0.5 s apart remain as
        // SEPARATE entries in the store; summing them across a wider window would
        // let opposing per-action errors cancel out silently.

        [Fact]
        public void ReconcileKsc_EventOutsideEpsilon_LogsMissingEventWarn()
        {
            // A correctly-keyed FundsChanged 1.0 s before the action is far outside the
            // 0.1 s matcher window. Action's expected -600 finds nothing → missing-event WARN.
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(1000 - 1.0, 50000, 49400, "RnDPartPurchase")
            };
            var action = new GameAction
            {
                UT = 1000,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f,
                DedupKey = "solidBooster.v2"
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 1000);

            Assert.Contains(logLines, l =>
                l.Contains("KSC reconciliation (funds)") &&
                l.Contains("no matching"));
        }

        [Fact]
        public void ReconcileKsc_TwoSameKeyEvents_0p3sApart_NotAggregated()
        {
            // Two RnDPartPurchase events 0.3 s apart stay as SEPARATE entries in the store
            // (GameStateStore coalesces only within 0.1 s). Each action must pair with its
            // own event, not see the sum. This pins the round-3 P2 fix: the aggregation
            // window must equal the coalesce window.
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(1000.0, 50000, 49400, "RnDPartPurchase"),   // -600
                MakeKeyedFundsChanged(1000.3, 49400, 49000, "RnDPartPurchase")    // -400
            };
            var action1 = new GameAction
            {
                UT = 1000.0,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f,
                DedupKey = "solidBooster.v2"
            };
            var action2 = new GameAction
            {
                UT = 1000.3,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 400f,
                DedupKey = "fuelTankSmallFlat"
            };
            var ledger = new List<GameAction> { action1, action2 };

            // Action1's 0.1 s window [999.9, 1000.1] includes only event1 and action1 —
            // event2 at 1000.3 and action2 at 1000.3 are outside.
            ReconcileKsc(events, ledger, action1, 1000.0);
            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));

            // Action2's 0.1 s window [1000.2, 1000.4] includes only event2 and action2.
            ReconcileKsc(events, ledger, action2, 1000.3);
            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        [Fact]
        public void ReconcileKsc_TwoSameKeyEvents_0p3sApart_OpposingErrors_BothWarn()
        {
            // The round-3 regression case. Action1 expected=-600 but observed=-500
            // (mismatch +100); Action2 expected=-400 but observed=-500 (mismatch -100).
            // Under the old 0.5 s aggregation the two errors cancelled and the matcher
            // stayed silent. With the tightened 0.1 s window each action reconciles
            // against its own event and both mismatches fire.
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(1000.0, 50000, 49500, "RnDPartPurchase"),   // -500
                MakeKeyedFundsChanged(1000.3, 49500, 49000, "RnDPartPurchase")    // -500
            };
            var action1 = new GameAction
            {
                UT = 1000.0,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f,      // expected -600 vs observed -500
                DedupKey = "a"
            };
            var action2 = new GameAction
            {
                UT = 1000.3,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 400f,      // expected -400 vs observed -500
                DedupKey = "b"
            };
            var ledger = new List<GameAction> { action1, action2 };

            ReconcileKsc(events, ledger, action1, 1000.0);
            Assert.Contains(logLines, l =>
                l.Contains("KSC reconciliation (funds)") &&
                l.Contains("delta mismatch"));

            logLines.Clear();
            ReconcileKsc(events, ledger, action2, 1000.3);
            Assert.Contains(logLines, l =>
                l.Contains("KSC reconciliation (funds)") &&
                l.Contains("delta mismatch"));
        }

        [Fact]
        public void ReconcileKsc_EventJustInsideWindow_NoWarn()
        {
            // Pins the inclusive-boundary behaviour: an event well under 0.1 s from the
            // action's UT pairs silently. The matcher rejects via `Math.Abs(diff) > 0.1`,
            // mirroring GameStateStore.AddEvent's `<= ResourceCoalesceEpsilon` gate. Use
            // 0.09 s rather than exactly 0.1 s to avoid double-precision round-off drama
            // (1000.1 - 1000.0 is actually 0.10000000000002274, not 0.1).
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(1000.09, 50000, 49400, "RnDPartPurchase")  // +0.09 s
            };
            var action = new GameAction
            {
                UT = 1000.0,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f,
                DedupKey = "solidBooster.v2"
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 1000.0);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        [Fact]
        public void ReconcileKsc_EventJustOutsideBoundary_WarnsMissing()
        {
            // Companion to the inside test: 0.11 s is comfortably past the 0.1 s window.
            // If the code ever widens the window back to 0.5 s (the round-3 regression),
            // this test stops flagging the expected miss.
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(1000.0 + 0.11, 50000, 49400, "RnDPartPurchase")
            };
            var action = new GameAction
            {
                UT = 1000.0,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f,
                DedupKey = "solidBooster.v2"
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 1000.0);

            Assert.Contains(logLines, l =>
                l.Contains("KSC reconciliation (funds)") &&
                l.Contains("no matching"));
        }

        // ---------- No-resource-impact + defensive ----------

        [Fact]
        public void ReconcileKsc_KerbalAssignment_NoReconciliation()
        {
            // Non-resource action types short-circuit — no WARN, no VERBOSE skip.
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(4000, 0, 12345, "None")  // entirely unrelated
            };
            var action = new GameAction
            {
                UT = 4000,
                Type = GameActionType.KerbalAssignment,
                KerbalName = "Jeb"
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 4000);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        [Fact]
        public void ReconcileKsc_NullEvents_WarnsMissingMatch()
        {
            // Defensive: null events list is handled without throwing. The untransformed
            // action still expects a paired event, so WARN fires.
            var action = new GameAction
            {
                UT = 5000,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 100f,
                DedupKey = "part"
            };
            var ledger = new List<GameAction> { action };

            LedgerOrchestrator.ReconcileKscAction(null, ledger, action, ut: 5000);

            Assert.Contains(logLines, l =>
                l.Contains("KSC reconciliation (funds)") &&
                l.Contains("no matching"));
        }

        [Fact]
        public void ReconcileKsc_NullLedgerActions_FallsBackToSingleAction()
        {
            // If the caller passes a null ledger list, the matcher must still reconcile
            // the given action against the events (using its own expected delta).
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(6000, 10000, 9400, "RnDPartPurchase")  // -600
            };
            var action = new GameAction
            {
                UT = 6000,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f,
                DedupKey = "part"
            };

            LedgerOrchestrator.ReconcileKscAction(events, null, action, ut: 6000);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        // ---------- ClassifyAction direct tests ----------

        [Fact]
        public void ClassifyAction_PartPurchase_UntransformedWithRnDPartPurchaseKey()
        {
            var a = new GameAction
            {
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f
            };
            var exp = LedgerOrchestrator.ClassifyAction(a);
            Assert.Equal(LedgerOrchestrator.KscReconcileClass.Untransformed, exp.Class);
            Assert.Equal(GameStateEventType.FundsChanged, exp.EventType);
            Assert.Equal("RnDPartPurchase", exp.ExpectedReasonKey);
            Assert.Equal(-600, exp.ExpectedDelta);
        }

        [Fact]
        public void ClassifyAction_ContractComplete_Transformed()
        {
            var a = new GameAction
            {
                Type = GameActionType.ContractComplete,
                FundsReward = 5000f,
                RepReward = 10f
            };
            var exp = LedgerOrchestrator.ClassifyAction(a);
            Assert.Equal(LedgerOrchestrator.KscReconcileClass.Transformed, exp.Class);
            Assert.False(string.IsNullOrEmpty(exp.SkipReason));
        }

        [Fact]
        public void ClassifyAction_KerbalAssignment_NoResourceImpact()
        {
            var a = new GameAction { Type = GameActionType.KerbalAssignment };
            var exp = LedgerOrchestrator.ClassifyAction(a);
            Assert.Equal(LedgerOrchestrator.KscReconcileClass.NoResourceImpact, exp.Class);
        }

        [Fact]
        public void ClassifyAction_StrategyActivate_TransformedNotUntransformed()
        {
            // StrategyActivate currently skips (Phase E1.5 lifecycle capture) — not
            // untransformed yet, per plan.
            var a = new GameAction { Type = GameActionType.StrategyActivate };
            var exp = LedgerOrchestrator.ClassifyAction(a);
            Assert.Equal(LedgerOrchestrator.KscReconcileClass.Transformed, exp.Class);
            Assert.Contains("Phase E1.5", exp.SkipReason);
        }
    }
}
