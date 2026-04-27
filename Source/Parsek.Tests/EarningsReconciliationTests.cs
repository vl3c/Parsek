using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
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
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(true, true, true);
        }

        public void Dispose()
        {
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(null, null, null);
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
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
        public void Reconcile_AllTrackersUnavailable_SkipsWarns_AndLogsOnce()
        {
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(false, false, false);

            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(150, 0, 8000)
            };
            var newActions = new List<GameAction>();

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                startUT: 100, endUT: 200);
            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                startUT: 100, endUT: 200);

            int skipLogs = 0;
            foreach (var line in logLines)
            {
                if (line.Contains("Earnings reconcile skipped: sandbox / tracker unavailable"))
                    skipLogs++;
            }

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation ("));
            Assert.Equal(1, skipLogs);
        }

        [Fact]
        public void Reconcile_FundsTrackerUnavailable_OnlyTrackedLegsWarn()
        {
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(false, true, true);

            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(150, 0, 8000),
                new GameStateEvent
                {
                    ut = 150,
                    eventType = GameStateEventType.ReputationChanged,
                    valueBefore = 10,
                    valueAfter = 35
                },
                new GameStateEvent
                {
                    ut = 150,
                    eventType = GameStateEventType.ScienceChanged,
                    valueBefore = 0,
                    valueAfter = 5
                }
            };
            var newActions = new List<GameAction>();

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                startUT: 100, endUT: 200);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (funds)"));
            Assert.Contains(logLines, l => l.Contains("Earnings reconciliation (rep)"));
            Assert.Contains(logLines, l => l.Contains("Earnings reconciliation (sci)"));
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
                    ContractId = "c1",
                    // #440B: ReconcileEarningsWindow now reads post-walk derived fields.
                    // Under identity transforms these match the raw values; seed them
                    // explicitly since this test bypasses RecalculateAndPatch.
                    Effective = true,
                    TransformedFundsReward = 5000f,
                    EffectiveRep = 10f,
                    TransformedScienceReward = 0f
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
                    MilestoneId = "Mun/Landing",
                    // #440B: rep leg now reads EffectiveRep; seed to curve-identity value.
                    Effective = true,
                    EffectiveRep = 5f
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

        private static void EnsureVisibleRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId) ||
                RecordingStore.IsCurrentTimelineRecordingId(recordingId))
            {
                return;
            }

            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                RecordingId = recordingId,
                VesselName = recordingId
            });
        }

        private static GameStateEvent MakeKeyedFundsChanged(
            double ut, double before, double after, string reason, string recordingId = "",
            bool visible = true)
        {
            if (visible)
                EnsureVisibleRecording(recordingId);

            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.FundsChanged,
                key = reason,
                valueBefore = before,
                valueAfter = after,
                recordingId = recordingId
            };
        }

        private static GameStateEvent MakeKeyedScienceChanged(
            double ut, double before, double after, string reason, string recordingId = "",
            bool visible = true)
        {
            if (visible)
                EnsureVisibleRecording(recordingId);

            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.ScienceChanged,
                key = reason,
                valueBefore = before,
                valueAfter = after,
                recordingId = recordingId
            };
        }

        private static GameStateEvent MakeKeyedRepChanged(
            double ut, double before, double after, string reason, string recordingId = "",
            bool visible = true)
        {
            if (visible)
                EnsureVisibleRecording(recordingId);

            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.ReputationChanged,
                key = reason,
                valueBefore = before,
                valueAfter = after,
                recordingId = recordingId
            };
        }

        private static object CreateKscExpectationLeg(
            GameStateEventType eventType,
            string expectedReasonKey,
            double expectedDelta,
            string modeName)
        {
            Type legType = typeof(KscActionExpectationClassifier).GetNestedType(
                "KscExpectationLeg",
                BindingFlags.NonPublic);
            Type modeType = typeof(KscActionExpectationClassifier).GetNestedType(
                "KscExpectationLegMode",
                BindingFlags.NonPublic);

            object leg = Activator.CreateInstance(legType);
            legType.GetField("IsPresent").SetValue(leg, true);
            legType.GetField("EventType").SetValue(leg, eventType);
            legType.GetField("ExpectedReasonKey").SetValue(leg, expectedReasonKey);
            legType.GetField("ExpectedDelta").SetValue(leg, expectedDelta);
            legType.GetField("Mode").SetValue(leg, Enum.Parse(modeType, modeName));
            return leg;
        }

        private static object CreateKscExpectedLegMatch(GameAction action, object leg)
        {
            Type matchType = typeof(LedgerOrchestrator).GetNestedType(
                "KscExpectedLegMatch",
                BindingFlags.NonPublic);
            object match = Activator.CreateInstance(matchType);
            matchType.GetField("Action").SetValue(match, action);
            matchType.GetField("Leg").SetValue(match, leg);
            return match;
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

        [Fact]
        public void ReconcileKsc_StrategyActivate_AllThreeResourceEventsPresent_NoWarn()
        {
            float repBefore = 500f;
            var repResult = ReputationModule.ApplyReputationCurve(-10f, repBefore);
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(1500, 100000, 90000, "StrategySetup"),
                MakeKeyedScienceChanged(1500, 40, 35, "StrategySetup"),
                MakeKeyedRepChanged(1500, repBefore, repBefore + repResult.actualDelta, "StrategySetup")
            };
            var action = new GameAction
            {
                UT = 1500,
                Type = GameActionType.StrategyActivate,
                SetupCost = 10000f,
                SetupScienceCost = 5f,
                SetupReputationCost = 10f
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 1500);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        [Fact]
        public void ReconcileKsc_StrategyActivate_MissingRepEvent_OnlyRepWarn()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(1500, 100000, 90000, "StrategySetup"),
                MakeKeyedScienceChanged(1500, 40, 35, "StrategySetup")
            };
            var action = new GameAction
            {
                UT = 1500,
                Type = GameActionType.StrategyActivate,
                SetupCost = 10000f,
                SetupScienceCost = 5f,
                SetupReputationCost = 10f
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 1500);

            Assert.Contains(logLines, l =>
                l.Contains("KSC reconciliation (rep)") &&
                l.Contains("StrategyActivate") &&
                l.Contains("StrategySetup") &&
                l.Contains("no matching"));
            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation (funds)"));
            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation (sci)"));
        }

        [Fact]
        public void ReconcileKsc_StrategyActivate_MissingSciEvent_OnlySciWarn()
        {
            float repBefore = 500f;
            var repResult = ReputationModule.ApplyReputationCurve(-10f, repBefore);
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(1500, 100000, 90000, "StrategySetup"),
                MakeKeyedRepChanged(1500, repBefore, repBefore + repResult.actualDelta, "StrategySetup")
            };
            var action = new GameAction
            {
                UT = 1500,
                Type = GameActionType.StrategyActivate,
                SetupCost = 10000f,
                SetupScienceCost = 5f,
                SetupReputationCost = 10f
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 1500);

            Assert.Contains(logLines, l =>
                l.Contains("KSC reconciliation (sci)") &&
                l.Contains("StrategyActivate") &&
                l.Contains("StrategySetup") &&
                l.Contains("no matching"));
            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation (funds)"));
            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation (rep)"));
        }

        [Fact]
        public void ReconcileKsc_StrategyActivate_CoalescedRepEvent_ReplaysCurveAcrossAllMatchingLegs()
        {
            float repBefore = 500f;
            var firstAction = new GameAction
            {
                UT = 1500.0,
                Sequence = 1,
                Type = GameActionType.StrategyActivate,
                SetupReputationCost = 10f
            };
            var secondAction = new GameAction
            {
                UT = 1500.0,
                Sequence = 2,
                Type = GameActionType.StrategyActivate,
                SetupReputationCost = 20f
            };
            var firstResult = ReputationModule.ApplyReputationCurve(-firstAction.SetupReputationCost, repBefore);
            var secondResult = ReputationModule.ApplyReputationCurve(-secondAction.SetupReputationCost, firstResult.newRep);
            var events = new List<GameStateEvent>
            {
                MakeKeyedRepChanged(1500.0, repBefore, secondResult.newRep, "StrategySetup")
            };
            var ledger = new List<GameAction> { secondAction, firstAction };

            ReconcileKsc(events, ledger, firstAction, firstAction.UT);
            ReconcileKsc(events, ledger, secondAction, secondAction.UT);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        [Fact]
        public void ComputeExpectedDeltaForLeg_ReputationCurve_SameUtMatchesUseSequenceTiebreaker()
        {
            Type matchType = typeof(LedgerOrchestrator).GetNestedType(
                "KscExpectedLegMatch",
                BindingFlags.NonPublic);
            Type matchListType = typeof(List<>).MakeGenericType(matchType);
            MethodInfo computeMethod = typeof(LedgerOrchestrator).GetMethod(
                "ComputeExpectedDeltaForLeg",
                BindingFlags.Static | BindingFlags.NonPublic);

            var firstAction = new GameAction { UT = 1500.0, Sequence = 1 };
            var secondAction = new GameAction { UT = 1500.0, Sequence = 2 };
            object firstLeg = CreateKscExpectationLeg(
                GameStateEventType.ReputationChanged,
                "StrategySetup",
                15.5,
                "ReputationCurve");
            object secondLeg = CreateKscExpectationLeg(
                GameStateEventType.ReputationChanged,
                "StrategySetup",
                -5.25,
                "ReputationCurve");
            var matches = (IList)Activator.CreateInstance(matchListType);
            matches.Add(CreateKscExpectedLegMatch(secondAction, secondLeg));
            matches.Add(CreateKscExpectedLegMatch(firstAction, firstLeg));

            double startingRep = 100.0;
            double actual = (double)computeMethod.Invoke(
                null,
                new object[] { firstLeg, matches, startingRep, 0.001 });

            var forwardFirst = ReputationModule.ApplyReputationCurve(15.5f, (float)startingRep);
            var forwardSecond = ReputationModule.ApplyReputationCurve(-5.25f, forwardFirst.newRep);
            double forwardExpected = forwardFirst.actualDelta + forwardSecond.actualDelta;
            var reverseFirst = ReputationModule.ApplyReputationCurve(-5.25f, (float)startingRep);
            var reverseSecond = ReputationModule.ApplyReputationCurve(15.5f, reverseFirst.newRep);
            double reverseExpected = reverseFirst.actualDelta + reverseSecond.actualDelta;

            var sortedFirst = (GameAction)matchType.GetField("Action").GetValue(matches[0]);
            var sortedSecond = (GameAction)matchType.GetField("Action").GetValue(matches[1]);

            Assert.True(Math.Abs(forwardExpected - reverseExpected) > 0.1);
            Assert.Equal(1, sortedFirst.Sequence);
            Assert.Equal(2, sortedSecond.Sequence);
            Assert.Equal(forwardExpected, actual, 3);
            Assert.Empty(logLines);
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
            Assert.Equal(KscActionExpectationClassifier.KscReconcileClass.Untransformed, exp.Class);
            Assert.Equal(1, exp.LegCount);
            Assert.True(exp.FundsLeg.IsPresent);
            Assert.Equal(GameStateEventType.FundsChanged, exp.FundsLeg.EventType);
            Assert.Equal("RnDPartPurchase", exp.FundsLeg.ExpectedReasonKey);
            Assert.Equal(-600, exp.FundsLeg.ExpectedDelta);
            Assert.Empty(logLines);
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
            Assert.Equal(KscActionExpectationClassifier.KscReconcileClass.Transformed, exp.Class);
            Assert.False(string.IsNullOrEmpty(exp.SkipReason));
        }

        [Fact]
        public void ClassifyAction_KerbalAssignment_NoResourceImpact()
        {
            var a = new GameAction { Type = GameActionType.KerbalAssignment };
            var exp = LedgerOrchestrator.ClassifyAction(a);
            Assert.Equal(KscActionExpectationClassifier.KscReconcileClass.NoResourceImpact, exp.Class);
        }

        [Fact]
        public void ClassifyAction_StrategyActivate_FundsLegPresent()
        {
            var a = new GameAction
            {
                Type = GameActionType.StrategyActivate,
                SetupCost = 100000f
            };
            var exp = LedgerOrchestrator.ClassifyAction(a);
            Assert.Equal(KscActionExpectationClassifier.KscReconcileClass.Untransformed, exp.Class);
            Assert.True(exp.FundsLeg.IsPresent);
            Assert.Equal(GameStateEventType.FundsChanged, exp.FundsLeg.EventType);
            Assert.Equal("StrategySetup", exp.FundsLeg.ExpectedReasonKey);
            Assert.Equal(-100000.0, exp.FundsLeg.ExpectedDelta);
            Assert.Empty(logLines);
        }

        [Fact]
        public void ClassifyAction_StrategyActivate_ThreeResourceSetupCost_ThreeLegs()
        {
            var a = new GameAction
            {
                Type = GameActionType.StrategyActivate,
                SetupCost = 100000f,
                SetupScienceCost = 5f,
                SetupReputationCost = 10f
            };

            var exp = LedgerOrchestrator.ClassifyAction(a);

            Assert.Equal(KscActionExpectationClassifier.KscReconcileClass.Untransformed, exp.Class);
            Assert.Equal(3, exp.LegCount);
            Assert.True(exp.FundsLeg.IsPresent);
            Assert.True(exp.ScienceLeg.IsPresent);
            Assert.True(exp.ReputationLeg.IsPresent);
            Assert.Equal(GameStateEventType.FundsChanged, exp.FundsLeg.EventType);
            Assert.Equal(GameStateEventType.ScienceChanged, exp.ScienceLeg.EventType);
            Assert.Equal(GameStateEventType.ReputationChanged, exp.ReputationLeg.EventType);
            Assert.Equal("StrategySetup", exp.FundsLeg.ExpectedReasonKey);
            Assert.Equal("StrategySetup", exp.ScienceLeg.ExpectedReasonKey);
            Assert.Equal("StrategySetup", exp.ReputationLeg.ExpectedReasonKey);
            Assert.Equal(-100000.0, exp.FundsLeg.ExpectedDelta);
            Assert.Equal(-5.0, exp.ScienceLeg.ExpectedDelta);
            Assert.Equal(-10.0, exp.ReputationLeg.ExpectedDelta);
            Assert.Equal(KscActionExpectationClassifier.KscExpectationLegMode.ReputationCurve, exp.ReputationLeg.Mode);
            Assert.Empty(logLines);
        }

        [Fact]
        public void ClassifyAction_StrategyActivate_FundsOnly_OneLeg()
        {
            var a = new GameAction
            {
                Type = GameActionType.StrategyActivate,
                SetupCost = 25000f,
                SetupScienceCost = 0f,
                SetupReputationCost = 0f
            };

            var exp = LedgerOrchestrator.ClassifyAction(a);

            Assert.Equal(KscActionExpectationClassifier.KscReconcileClass.Untransformed, exp.Class);
            Assert.Equal(1, exp.LegCount);
            Assert.True(exp.FundsLeg.IsPresent);
            Assert.False(exp.ScienceLeg.IsPresent);
            Assert.False(exp.ReputationLeg.IsPresent);
            Assert.Equal(-25000.0, exp.FundsLeg.ExpectedDelta);
            Assert.Empty(logLines);
        }

        [Fact]
        public void ClassifyAction_StrategyActivate_ZeroSetupCost_ShortCircuits()
        {
            var a = new GameAction
            {
                Type = GameActionType.StrategyActivate,
                SetupCost = 0f,
                SetupScienceCost = 0f,
                SetupReputationCost = 0f
            };
            var exp = LedgerOrchestrator.ClassifyAction(a);
            Assert.Equal(KscActionExpectationClassifier.KscReconcileClass.Untransformed, exp.Class);
            Assert.Equal(0, exp.LegCount);

            var events = new List<GameStateEvent>();
            var ledger = new List<GameAction> { a };
            ReconcileKsc(events, ledger, a, 500.0);
            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        [Fact]
        public void ClassifyAction_StrategyDeactivate_NoResourceImpact()
        {
            var a = new GameAction { Type = GameActionType.StrategyDeactivate };
            var exp = LedgerOrchestrator.ClassifyAction(a);
            Assert.Equal(KscActionExpectationClassifier.KscReconcileClass.NoResourceImpact, exp.Class);
        }

        // ---------- #448 / #451: part-purchase zero-delta bypass path ----------

        [Fact]
        public void ReconcileKsc_PartPurchase_ZeroCost_NoMatchingEvent_Silent()
        {
            // Stock bypass=true careers do not pay part entry costs at all. The
            // PartPurchased event now records FundsSpent=0 in that mode, so the
            // reconciler should short-circuit on the zero expected delta without
            // looking for a paired FundsChanged(RnDPartPurchase) event.
            var events = new List<GameStateEvent>();
            var action = new GameAction
            {
                UT = 201.3,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 0f,
                DedupKey = "solidBooster.v2"
            };
            var ledger = new List<GameAction> { action };

            ReconcileKsc(events, ledger, action, 201.3);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        [Fact]
        public void ClassifyAction_PartPurchase_BypassOff_StaysUntransformed()
        {
            try
            {
                GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = () => false;

                var a = new GameAction
                {
                    Type = GameActionType.FundsSpending,
                    FundsSpendingSource = FundsSpendingSource.Other,
                    FundsSpent = 600f
                };
                var exp = LedgerOrchestrator.ClassifyAction(a);

                Assert.Equal(KscActionExpectationClassifier.KscReconcileClass.Untransformed, exp.Class);
                Assert.True(exp.FundsLeg.IsPresent);
                Assert.Equal(GameStateEventType.FundsChanged, exp.FundsLeg.EventType);
                Assert.Equal("RnDPartPurchase", exp.FundsLeg.ExpectedReasonKey);
                Assert.Equal(-600, exp.FundsLeg.ExpectedDelta);
            }
            finally
            {
                GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = null;
            }
        }

        [Fact]
        public void ReconcileKsc_PartPurchase_BypassOff_NoMatchingEvent_StillWarns()
        {
            // Inverse: under Bypass=false KSP would fire FundsChanged(RnDPartPurchase),
            // so a missing event is still a real diagnostic — the reconciler should
            // emit the existing "no matching" WARN.
            try
            {
                GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = () => false;

                var events = new List<GameStateEvent>();
                var action = new GameAction
                {
                    UT = 201.3,
                    Type = GameActionType.FundsSpending,
                    FundsSpendingSource = FundsSpendingSource.Other,
                    FundsSpent = 150f,
                    DedupKey = "solidBooster.v2"
                };
                var ledger = new List<GameAction> { action };

                ReconcileKsc(events, ledger, action, 201.3);

                Assert.Contains(logLines, l =>
                    l.Contains("[LedgerOrchestrator]") &&
                    l.Contains("KSC reconciliation (funds)") &&
                    l.Contains("FundsSpending") &&
                    l.Contains("RnDPartPurchase") &&
                    l.Contains("no matching"));
            }
            finally
            {
                GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = null;
            }
        }

        [Fact]
        public void ReconcileKsc_PartPurchase_BypassOff_EntryCostMatched_NoWarn()
        {
            // #451: under bypass=false, KSP fires FundsChanged(RnDPartPurchase) with
            // -entryCost (NOT -part.cost). Pre-#451 the recorder captured part.cost into
            // FundsSpent, producing a delta-mismatch WARN on every purchase whenever the
            // two values differed (the common case — entryCost is typically 1.5-3x cost).
            // Post-#451 the recorder captures entryCost, so the action's expected delta
            // (-entryCost) matches the event's observed delta (-entryCost) and the
            // reconciler stays silent.
            try
            {
                GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = () => false;

                // solidBooster.v2 numbers from the bug report: cost=450, entryCost=800.
                // Pre-#451 the action carried 450 and the event carried 800 -> WARN.
                // Post-#451 both sides carry 800.
                var events = new List<GameStateEvent>
                {
                    MakeKeyedFundsChanged(1500, 50000, 49200, "RnDPartPurchase")  // -800
                };
                var action = new GameAction
                {
                    UT = 1500,
                    Type = GameActionType.FundsSpending,
                    FundsSpendingSource = FundsSpendingSource.Other,
                    FundsSpent = 800f,                        // entryCost, post-#451
                    DedupKey = "solidBooster.v2"
                };
                var ledger = new List<GameAction> { action };

                ReconcileKsc(events, ledger, action, 1500);

                Assert.DoesNotContain(logLines, l =>
                    l.Contains("[LedgerOrchestrator]") &&
                    l.Contains("KSC reconciliation (funds)"));
            }
            finally
            {
                GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = null;
            }
        }

        #region #440 post-walk tests

        // ================================================================
        // #440 Phase E2 -- post-walk reconciliation for strategy-transformed
        // and curve-applied reward types. The new LedgerOrchestrator.
        // ReconcilePostWalk pairs the derived Transformed*/EffectiveRep/
        // EffectiveScience values (set by RecalculationEngine.Recalculate)
        // against live KSP deltas captured in GameStateStore.Events.
        //
        // Gate zero: `PostWalk_ReputationCurveMatchesKsp` must pass before
        // any rep-leg test is trustworthy -- it pins our curve output
        // against the Spike A decompile of KSP's addReputation_granular.
        // ================================================================

        // Pre-captured reference values from the Spike A KSP decompile of
        // addReputation_granular + the reputationAddition / reputationSubtraction
        // AnimationCurve keyframes (see docs/dev/done/game-actions/
        // game-actions-spike-findings.md). If these change, the curve has drifted.
        //
        // Values computed offline against the exact keyframes and algorithm in
        // ReputationModule.cs.
        private const float KspRefDelta_Nominal25_Rep0   = 24.980690f;
        private const float KspRefDelta_Nominal10_Rep200 =  9.471328f;
        private const float KspRefDelta_NegNom5_Rep150   = -5.737427f;
        private const float KspRefDelta_Nominal50_Rep500 = 23.845410f;

        [Fact]
        public void PostWalk_ReputationCurveMatchesKsp()
        {
            // Gate zero: feed known (nominal rep, runningRep) tuples into the curve
            // and assert output matches the KSP-decompile reference within 0.1.
            // If this fails, the curve has drifted and #440 rep-leg reconciliation
            // would false-WARN on every contract completion.

            var r1 = ReputationModule.ApplyReputationCurve(25f, 0f);
            var r2 = ReputationModule.ApplyReputationCurve(10f, 200f);
            var r3 = ReputationModule.ApplyReputationCurve(-5f, 150f);
            var r4 = ReputationModule.ApplyReputationCurve(50f, 500f);

            const float tol = 0.1f;
            Assert.InRange(r1.actualDelta, KspRefDelta_Nominal25_Rep0   - tol, KspRefDelta_Nominal25_Rep0   + tol);
            Assert.InRange(r2.actualDelta, KspRefDelta_Nominal10_Rep200 - tol, KspRefDelta_Nominal10_Rep200 + tol);
            Assert.InRange(r3.actualDelta, KspRefDelta_NegNom5_Rep150   - tol, KspRefDelta_NegNom5_Rep150   + tol);
            Assert.InRange(r4.actualDelta, KspRefDelta_Nominal50_Rep500 - tol, KspRefDelta_Nominal50_Rep500 + tol);

            // Log-capture assertion: invoke ProcessRepEarning so the module logs a
            // VERBOSE line, and assert the log shape carries the curve output. This
            // ties the curve-fidelity assertion to an observable log line consumed
            // by the reconcile hook's diagnostic path.
            var action = new GameAction
            {
                UT = 100,
                Type = GameActionType.ReputationEarning,
                NominalRep = 25f,
                RepSource = ReputationSource.Other
            };
            var module = new ReputationModule();
            module.ProcessAction(action);

            Assert.InRange(action.EffectiveRep,
                KspRefDelta_Nominal25_Rep0 - tol,
                KspRefDelta_Nominal25_Rep0 + tol);
            Assert.Contains(logLines, l =>
                l.Contains("[Reputation]") && l.Contains("RepEarning") && l.Contains("effective="));
        }

        // ---------- ContractComplete: all three legs match -> no WARN ----------

        [Fact]
        public void PostWalk_ContractComplete_AllLegsMatch_NoWarn()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(500, 20000, 24700, "ContractReward"),   // +4700
                MakeKeyedRepChanged(500, 0, 7, "ContractReward"),             // +7
                MakeKeyedScienceChanged(500, 0, 3, "ContractReward")          // +3
            };
            var action = new GameAction
            {
                UT = 500,
                Type = GameActionType.ContractComplete,
                ContractId = "c1",
                Effective = true,
                TransformedFundsReward = 4700f,
                EffectiveRep = 7f,
                TransformedScienceReward = 3f
            };
            var actions = new List<GameAction> { action };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
        }

        [Fact]
        public void PostWalk_ContractComplete_FundsMismatch_OnlyFundsWarn()
        {
            // Funds leg diverges by 500; rep and sci match. Only the funds WARN fires.
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(500, 20000, 24700, "ContractReward"),   // observed +4700
                MakeKeyedRepChanged(500, 0, 7, "ContractReward"),
                MakeKeyedScienceChanged(500, 0, 3, "ContractReward")
            };
            var action = new GameAction
            {
                UT = 500,
                Type = GameActionType.ContractComplete,
                ContractId = "c1",
                Effective = true,
                TransformedFundsReward = 5200f,   // diverges by +500
                EffectiveRep = 7f,
                TransformedScienceReward = 3f
            };
            var actions = new List<GameAction> { action };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("Earnings reconciliation (post-walk, funds)") &&
                l.Contains("ContractComplete"));
            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, rep)"));
            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, sci)"));
        }

        [Fact]
        public void PostWalk_ContractComplete_RepMismatch_OnlyRepWarn()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(500, 20000, 24700, "ContractReward"),
                MakeKeyedRepChanged(500, 0, 12, "ContractReward"),            // observed +12
                MakeKeyedScienceChanged(500, 0, 3, "ContractReward")
            };
            var action = new GameAction
            {
                UT = 500,
                Type = GameActionType.ContractComplete,
                ContractId = "c1",
                Effective = true,
                TransformedFundsReward = 4700f,
                EffectiveRep = 7f,               // diverges from +12
                TransformedScienceReward = 3f
            };
            var actions = new List<GameAction> { action };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.Contains(logLines, l =>
                l.Contains("Earnings reconciliation (post-walk, rep)") &&
                l.Contains("ContractComplete"));
            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, funds)"));
            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, sci)"));
        }

        [Fact]
        public void PostWalk_ContractComplete_SciMismatch_OnlySciWarn()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(500, 20000, 24700, "ContractReward"),
                MakeKeyedRepChanged(500, 0, 7, "ContractReward"),
                MakeKeyedScienceChanged(500, 0, 8.5, "ContractReward")       // observed +8.5
            };
            var action = new GameAction
            {
                UT = 500,
                Type = GameActionType.ContractComplete,
                ContractId = "c1",
                Effective = true,
                TransformedFundsReward = 4700f,
                EffectiveRep = 7f,
                TransformedScienceReward = 3f    // diverges from +8.5
            };
            var actions = new List<GameAction> { action };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.Contains(logLines, l =>
                l.Contains("Earnings reconciliation (post-walk, sci)") &&
                l.Contains("ContractComplete"));
            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, funds)"));
            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, rep)"));
        }

        // ---------- ContractFail / ContractCancel (ContractPenalty key) ----------

        [Fact]
        public void PostWalk_ContractFail_RepCurve_NoWarn()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(800, 20000, 19000, "ContractPenalty"),  // -1000
                MakeKeyedRepChanged(800, 10, 7, "ContractPenalty")            // -3 (curve-compressed)
            };
            var action = new GameAction
            {
                UT = 800,
                Type = GameActionType.ContractFail,
                ContractId = "c2",
                FundsPenalty = 1000f,
                RepPenalty = 5f,          // raw nominal
                EffectiveRep = -3f        // curve output
            };
            var actions = new List<GameAction> { action };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
        }

        [Fact]
        public void PostWalk_ContractCancel_FundsAndRep_Match_NoWarn()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(900, 20000, 19500, "ContractPenalty"),  // -500
                MakeKeyedRepChanged(900, 10, 8, "ContractPenalty")            // -2
            };
            var action = new GameAction
            {
                UT = 900,
                Type = GameActionType.ContractCancel,
                ContractId = "c3",
                FundsPenalty = 500f,
                RepPenalty = 3f,
                EffectiveRep = -2f
            };
            var actions = new List<GameAction> { action };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
        }

        // ---------- MilestoneAchievement (Progression key, Effective gate) ----------

        [Fact]
        public void PostWalk_MilestoneAchievement_EffectiveTrue_AllLegsMatch_NoWarn()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(600, 20000, 22000, "Progression"),     // +2000
                MakeKeyedRepChanged(600, 0, 4, "Progression"),               // +4
                MakeKeyedScienceChanged(600, 0, 6, "Progression")            // +6
            };
            var action = new GameAction
            {
                UT = 600,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "FirstLaunch",
                Effective = true,
                MilestoneFundsAwarded = 2000f,
                MilestoneRepAwarded = 5f,     // nominal
                EffectiveRep = 4f,            // curve output
                MilestoneScienceAwarded = 6f
            };
            var actions = new List<GameAction> { action };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
        }

        [Fact]
        public void PostWalk_MilestoneAchievement_OtherRecordingProgressionIgnored_NoWarn()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(600, 20000, 22880, "Progression", recordingId: "rec-parent"),
                MakeKeyedFundsChanged(600, 22880, 23360, "Progression", recordingId: "rec-child")
            };
            var action = new GameAction
            {
                UT = 600,
                Type = GameActionType.MilestoneAchievement,
                RecordingId = "rec-parent",
                MilestoneId = "RecordsSpeed",
                Effective = true,
                MilestoneFundsAwarded = 2880f
            };
            var actions = new List<GameAction> { action };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
        }

        [Fact]
        public void PostWalk_MilestoneAchievement_NullTaggedSiblingDefersToTaggedScope_NoWarn()
        {
            // Mixed-scope edge for the #462 partial fix: a null-tagged legacy action
            // should still be allowed to match tagged store events when it is alone,
            // but it must not become the primary owner of a coalesced window that also
            // contains a tagged sibling. Otherwise list order decides whether the
            // aggregate re-folds sibling recordings back into the expected sum.
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(600, 20000, 20960, "Progression", recordingId: "rec-parent"),
                MakeKeyedFundsChanged(600, 20960, 21440, "Progression", recordingId: "rec-child")
            };
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 600,
                    Type = GameActionType.MilestoneAchievement,
                    RecordingId = null,
                    MilestoneId = "Kerbin/SurfaceEVA (legacy)",
                    Effective = true,
                    MilestoneFundsAwarded = 960f
                },
                new GameAction
                {
                    UT = 600,
                    Type = GameActionType.MilestoneAchievement,
                    RecordingId = "rec-parent",
                    MilestoneId = "Kerbin/SurfaceEVA",
                    Effective = true,
                    MilestoneFundsAwarded = 960f
                }
            };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
            Assert.Contains(logLines, l =>
                l.Contains("Post-walk match: MilestoneAchievement funds") &&
                l.Contains("id=Kerbin/SurfaceEVA"));
        }

        [Fact]
        public void PostWalk_ContractComplete_NullTaggedAction_MatchesTaggedEvent_NoWarn()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(500, 20000, 24700, "ContractReward", recordingId: "rec-parent")
            };
            var action = new GameAction
            {
                UT = 500,
                Type = GameActionType.ContractComplete,
                RecordingId = null,
                ContractId = "c-recovered",
                Effective = true,
                TransformedFundsReward = 4700f
            };
            var actions = new List<GameAction> { action };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
        }

        [Fact]
        public void PostWalk_MilestoneAchievement_EffectiveFalseDuplicate_Skipped_NoWarn()
        {
            // Two milestone actions with the same id. The second is Effective=false
            // (duplicate; MilestonesModule already credited the first). The live
            // FundsChanged(Progression) event reflects only the first credit.
            // Post-walk must NOT reconcile the second -> no WARN.
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(600, 20000, 22000, "Progression")      // +2000 from first
            };
            var firstMilestone = new GameAction
            {
                UT = 600,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "FirstLaunch",
                Effective = true,
                MilestoneFundsAwarded = 2000f
            };
            var dupMilestone = new GameAction
            {
                UT = 700,   // different UT, no matching event in window
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "FirstLaunch",
                Effective = false,           // duplicate
                MilestoneFundsAwarded = 2000f
            };
            var actions = new List<GameAction> { firstMilestone, dupMilestone };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
        }

        [Fact]
        public void PostWalk_MilestoneAchievement_CoalescedWindow_MatchesOnce_NoWarn()
        {
            EnsureVisibleRecording("rec-mun");
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 19540.3,
                    eventType = GameStateEventType.MilestoneAchieved,
                    key = "Mun/Flyby",
                    recordingId = "rec-mun"
                },
                new GameStateEvent
                {
                    ut = 19540.3,
                    eventType = GameStateEventType.MilestoneAchieved,
                    key = "Kerbin/Escape",
                    recordingId = "rec-mun"
                },
                MakeKeyedFundsChanged(19540.3, 100000, 126200, "Progression", recordingId: "rec-mun"),
                MakeKeyedRepChanged(19540.3, 10, 13, "Progression", recordingId: "rec-mun"),
                MakeKeyedScienceChanged(19540.3, 2, 3, "Progression", recordingId: "rec-mun")
            };
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 19540.3,
                    Type = GameActionType.MilestoneAchievement,
                    RecordingId = "rec-mun",
                    MilestoneId = "Mun/Flyby",
                    Effective = true,
                    MilestoneFundsAwarded = 13000f,
                    MilestoneRepAwarded = 1f,
                    EffectiveRep = 1f,
                    MilestoneScienceAwarded = 0f
                },
                new GameAction
                {
                    UT = 19540.3,
                    Type = GameActionType.MilestoneAchievement,
                    RecordingId = "rec-mun",
                    MilestoneId = "Kerbin/Escape",
                    Effective = true,
                    MilestoneFundsAwarded = 13200f,
                    MilestoneRepAwarded = 2f,
                    EffectiveRep = 2f,
                    MilestoneScienceAwarded = 1f
                }
            };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
            var fundsMatches = logLines.FindAll(l =>
                l.Contains("Post-walk match: MilestoneAchievement funds"));
            var repMatches = logLines.FindAll(l =>
                l.Contains("Post-walk match: MilestoneAchievement rep"));
            var sciMatches = logLines.FindAll(l =>
                l.Contains("Post-walk match: MilestoneAchievement sci"));

            Assert.Single(fundsMatches);
            Assert.Single(repMatches);
            Assert.Single(sciMatches);
            Assert.Contains("ids=[Mun/Flyby, Kerbin/Escape] across 2 action(s)", fundsMatches[0]);
            Assert.Contains("ids=[Mun/Flyby, Kerbin/Escape] across 2 action(s)", repMatches[0]);
            Assert.Contains("id=Kerbin/Escape", sciMatches[0]);
        }

        [Fact]
        public void PostWalk_MilestoneAchievement_CoalescedWindow_MissingEvent_WarnsOncePerLeg()
        {
            EnsureVisibleRecording("rec-mun");
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 19540.3,
                    eventType = GameStateEventType.MilestoneAchieved,
                    key = "Mun/Flyby",
                    recordingId = "rec-mun"
                },
                new GameStateEvent
                {
                    ut = 19540.3,
                    eventType = GameStateEventType.MilestoneAchieved,
                    key = "Kerbin/Escape",
                    recordingId = "rec-mun"
                }
            };
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 19540.3,
                    Type = GameActionType.MilestoneAchievement,
                    RecordingId = "rec-mun",
                    MilestoneId = "Mun/Flyby",
                    Effective = true,
                    MilestoneFundsAwarded = 13000f,
                    MilestoneRepAwarded = 1f,
                    EffectiveRep = 1f,
                    MilestoneScienceAwarded = 0f
                },
                new GameAction
                {
                    UT = 19540.3,
                    Type = GameActionType.MilestoneAchievement,
                    RecordingId = "rec-mun",
                    MilestoneId = "Kerbin/Escape",
                    Effective = true,
                    MilestoneFundsAwarded = 13200f,
                    MilestoneRepAwarded = 2f,
                    EffectiveRep = 2f,
                    MilestoneScienceAwarded = 1f
                }
            };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            var fundsWarns = logLines.FindAll(l =>
                l.Contains("Earnings reconciliation (post-walk, funds)") &&
                l.Contains("MilestoneAchievement"));
            var repWarns = logLines.FindAll(l =>
                l.Contains("Earnings reconciliation (post-walk, rep)") &&
                l.Contains("MilestoneAchievement"));
            var sciWarns = logLines.FindAll(l =>
                l.Contains("Earnings reconciliation (post-walk, sci)") &&
                l.Contains("MilestoneAchievement"));

            Assert.Single(fundsWarns);
            Assert.Single(repWarns);
            Assert.Single(sciWarns);

            Assert.Contains("ids=[Mun/Flyby, Kerbin/Escape] across 2 action(s)", fundsWarns[0]);
            Assert.Contains("expected=26200.0", fundsWarns[0]);
            Assert.Contains("expected=3.0", repWarns[0]);
            Assert.Contains("expected=1.0", sciWarns[0]);
        }

        [Fact]
        public void PostWalk_MilestoneAchievement_CoalescedTinyScienceLegs_AggregateWarnsOnce()
        {
            EnsureVisibleRecording("rec-small");
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 600,
                    eventType = GameStateEventType.MilestoneAchieved,
                    key = "TinyA",
                    recordingId = "rec-small"
                },
                new GameStateEvent
                {
                    ut = 600,
                    eventType = GameStateEventType.MilestoneAchieved,
                    key = "TinyB",
                    recordingId = "rec-small"
                }
            };
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 600,
                    Type = GameActionType.MilestoneAchievement,
                    RecordingId = "rec-small",
                    MilestoneId = "TinyA",
                    Effective = true,
                    MilestoneScienceAwarded = 0.1f
                },
                new GameAction
                {
                    UT = 600,
                    Type = GameActionType.MilestoneAchievement,
                    RecordingId = "rec-small",
                    MilestoneId = "TinyB",
                    Effective = true,
                    MilestoneScienceAwarded = 0.1f
                }
            };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            var sciWarns = logLines.FindAll(l =>
                l.Contains("Earnings reconciliation (post-walk, sci)") &&
                l.Contains("MilestoneAchievement"));

            Assert.Single(sciWarns);
            Assert.Contains("ids=[TinyA, TinyB] across 2 action(s)", sciWarns[0]);
            Assert.Contains("expected=0.2", sciWarns[0]);
            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, funds)"));
            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, rep)"));
        }

        [Fact]
        public void PostWalk_MilestoneAchievement_NullTaggedLegacySibling_YieldsTaggedOwner_NoWarn()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(600, 20000, 22880, "Progression", recordingId: "rec-child")
            };
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 600,
                    Type = GameActionType.MilestoneAchievement,
                    RecordingId = null,
                    MilestoneId = "RecordsSpeed-Legacy",
                    Effective = true,
                    MilestoneFundsAwarded = 2880f
                },
                new GameAction
                {
                    UT = 600,
                    Type = GameActionType.MilestoneAchievement,
                    RecordingId = "rec-child",
                    MilestoneId = "RecordsSpeed",
                    Effective = true,
                    MilestoneFundsAwarded = 2880f
                }
            };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
            Assert.Contains(logLines, l =>
                l.Contains("Post-walk match: MilestoneAchievement funds") &&
                l.Contains("id=RecordsSpeed"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Post-walk match: MilestoneAchievement funds") &&
                l.Contains("RecordsSpeed-Legacy"));
        }

        [Fact]
        public void PostWalk_MilestoneAchievement_PrunedByCommittedThreshold_Skipped_NoWarn()
        {
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                StartUT = 0,
                EndUT = 650,
                Epoch = 0,
                Committed = true,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 650,
                        eventType = GameStateEventType.TechResearched,
                        key = "m1-seed"
                    }
                }
            });

            var action = new GameAction
            {
                UT = 600,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "FirstLaunch",
                Effective = true,
                MilestoneFundsAwarded = 2000f
            };

            LedgerOrchestrator.ReconcilePostWalk(
                new List<GameStateEvent>(),
                new List<GameAction> { action },
                utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
        }

        [Fact]
        public void PostWalk_MilestoneAchievement_WithoutLiveSourceAnchorInNewEpoch_Skipped_NoWarn()
        {
            var laterMilestone = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "LaterMilestone"
            };
            GameStateStore.AddEvent(ref laterMilestone);
            var laterFunds = MakeKeyedFundsChanged(100, 0, 500, "Progression");
            GameStateStore.AddEvent(ref laterFunds);
            var action = new GameAction
            {
                UT = 50,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "FirstLaunch",
                Effective = true,
                MilestoneFundsAwarded = 800f
            };

            LedgerOrchestrator.ReconcilePostWalk(
                GameStateStore.Events,
                new List<GameAction> { action },
                utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
        }

        [Fact]
        public void PostWalk_MilestoneAchievement_WithLiveSourceAnchorInNewEpoch_MissingFundsEvent_Warns()
        {
            var originalCulture = Thread.CurrentThread.CurrentCulture;
            var originalUICulture = Thread.CurrentThread.CurrentUICulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("ro-RO");
                Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("ro-RO");

                var milestoneEvt = new GameStateEvent
                {
                    ut = 50,
                    eventType = GameStateEventType.MilestoneAchieved,
                    key = "FirstLaunch"
                };
                GameStateStore.AddEvent(ref milestoneEvt);
                var action = new GameAction
                {
                    UT = 50,
                    Type = GameActionType.MilestoneAchievement,
                    MilestoneId = "FirstLaunch",
                    Effective = true,
                    MilestoneFundsAwarded = 800f
                };

                LedgerOrchestrator.ReconcilePostWalk(
                    GameStateStore.Events,
                    new List<GameAction> { action },
                    utCutoff: null);

                Assert.Contains(logLines, l =>
                    l.Contains("Earnings reconciliation (post-walk, funds)") &&
                    l.Contains("FirstLaunch") &&
                    l.Contains("expected=800.0") &&
                    l.Contains("within 0.1s of ut=50.0"));
                Assert.DoesNotContain(logLines, l => l.Contains("expected=800,0"));
                Assert.DoesNotContain(logLines, l => l.Contains("within 0,1s of ut=50,0"));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = originalCulture;
                Thread.CurrentThread.CurrentUICulture = originalUICulture;
            }
        }

        [Fact]
        public void PostWalk_MilestoneAchievement_WithLiveFundsButNoSourceAnchorInNewEpoch_DoesNotSkip()
        {
            var liveFunds = MakeKeyedFundsChanged(50, 0, 800, "Progression");
            GameStateStore.AddEvent(ref liveFunds);
            var action = new GameAction
            {
                UT = 50,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "FirstLaunch",
                Effective = true,
                MilestoneFundsAwarded = 800f
            };

            LedgerOrchestrator.ReconcilePostWalk(
                GameStateStore.Events,
                new List<GameAction> { action },
                utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
            Assert.Contains(logLines, l => l.Contains("Post-walk reconcile: actions=1"));
        }

        [Fact]
        public void PostWalk_MilestoneAchievement_StaleNeighborInsideCoalesceWindow_DoesNotInflateLiveExpected()
        {
            var liveMilestone = new GameStateEvent
            {
                ut = 50.05,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "LaterMilestone"
            };
            GameStateStore.AddEvent(ref liveMilestone);
            var liveFunds = MakeKeyedFundsChanged(50.05, 0, 800, "Progression");
            GameStateStore.AddEvent(ref liveFunds);

            var staleAction = new GameAction
            {
                UT = 50.00,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "FirstLaunch",
                Effective = true,
                MilestoneFundsAwarded = 800f
            };
            var liveAction = new GameAction
            {
                UT = 50.05,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "LaterMilestone",
                Effective = true,
                MilestoneFundsAwarded = 800f
            };

            LedgerOrchestrator.ReconcilePostWalk(
                GameStateStore.Events,
                new List<GameAction> { staleAction, liveAction },
                utCutoff: null);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("Earnings reconciliation (post-walk, funds)") &&
                l.Contains("LaterMilestone"));
        }

        [Fact]
        public void PostWalk_MilestoneAchievement_StaleObservedEventIgnored_InLiveWindow()
        {
            RecordingStore.AddCommittedInternal(new Recording
            {
                RecordingId = "live-rec",
                VesselName = "Visible"
            });

            var staleFunds = MakeKeyedFundsChanged(50.05, 0, 800, "Progression");
            staleFunds.recordingId = "old-rec";
            var liveMilestone = new GameStateEvent
            {
                ut = 50.05,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "LaterMilestone",
                recordingId = "live-rec"
            };
            var liveFunds = MakeKeyedFundsChanged(50.05, 0, 800, "Progression");
            liveFunds.recordingId = "live-rec";

            var action = new GameAction
            {
                UT = 50.05,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "LaterMilestone",
                Effective = true,
                MilestoneFundsAwarded = 800f
            };

            LedgerOrchestrator.ReconcilePostWalk(
                new List<GameStateEvent> { staleFunds, liveMilestone, liveFunds },
                new List<GameAction> { action },
                utCutoff: null);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("Earnings reconciliation (post-walk, funds)") &&
                l.Contains("LaterMilestone"));
        }

        [Fact]
        public void PostWalk_MilestoneAchievement_ThresholdStraddlingStaleNeighbor_DoesNotSuppressLiveFallback()
        {
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                StartUT = 0,
                EndUT = 50.0,
                Epoch = 0,
                Committed = true,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 50.0,
                        eventType = GameStateEventType.TechResearched,
                        key = "m1-seed"
                    }
                }
            });

            var liveFunds = MakeKeyedFundsChanged(50.05, 0, 800, "Progression");
            GameStateStore.AddEvent(ref liveFunds);

            var staleAction = new GameAction
            {
                UT = 49.98,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "FirstLaunch",
                Effective = true,
                MilestoneFundsAwarded = 800f
            };
            var liveAction = new GameAction
            {
                UT = 50.05,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "LaterMilestone",
                Effective = true,
                MilestoneFundsAwarded = 800f
            };

            LedgerOrchestrator.ReconcilePostWalk(
                GameStateStore.Events,
                new List<GameAction> { staleAction, liveAction },
                utCutoff: null);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("Earnings reconciliation (post-walk, funds)") &&
                l.Contains("LaterMilestone"));
            Assert.Contains(logLines, l => l.Contains("Post-walk reconcile: actions=1"));
        }

        [Fact]
        public void PostWalk_MilestoneAchievement_LiveNoSourceOverlap_SkipsAmbiguousFallback()
        {
            var liveFunds = MakeKeyedFundsChanged(50.05, 0, 800, "Progression");
            GameStateStore.AddEvent(ref liveFunds);

            var firstAction = new GameAction
            {
                UT = 50.00,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "FirstLaunch",
                Effective = true,
                MilestoneFundsAwarded = 800f
            };
            var secondAction = new GameAction
            {
                UT = 50.05,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "LaterMilestone",
                Effective = true,
                MilestoneFundsAwarded = 800f
            };

            LedgerOrchestrator.ReconcilePostWalk(
                GameStateStore.Events,
                new List<GameAction> { firstAction, secondAction },
                utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
            Assert.Contains(logLines, l => l.Contains("Post-walk reconcile: actions=0"));
        }

        // ---------- ReputationEarning / ReputationPenalty ----------

        [Fact]
        public void PostWalk_ReputationEarning_CurveMatch_NoWarn()
        {
            // ReputationSource enum today has ContractComplete / Milestone / Other.
            // RepSource.Other is synthetic -> ClassifyPostWalk returns Reconcile=false.
            // Use ContractComplete so the test exercises the ContractReward key path.
            var events = new List<GameStateEvent>
            {
                MakeKeyedRepChanged(700, 10, 17, "ContractReward")           // +7
            };
            var action = new GameAction
            {
                UT = 700,
                Type = GameActionType.ReputationEarning,
                NominalRep = 10f,
                RepSource = ReputationSource.ContractComplete,
                EffectiveRep = 7f
            };
            var actions = new List<GameAction> { action };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, rep)"));
        }

        [Fact]
        public void PostWalk_ReputationEarning_CurveDiverges_Warn()
        {
            var events = new List<GameStateEvent>
            {
                // Observed +12 from KSP, but walk's curve output was +7 -> 5 rep mismatch
                MakeKeyedRepChanged(700, 10, 22, "ContractReward")           // +12
            };
            var action = new GameAction
            {
                UT = 700,
                Type = GameActionType.ReputationEarning,
                NominalRep = 10f,
                RepSource = ReputationSource.ContractComplete,
                EffectiveRep = 7f     // diverges from observed +12
            };
            var actions = new List<GameAction> { action };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.Contains(logLines, l =>
                l.Contains("Earnings reconciliation (post-walk, rep)") &&
                l.Contains("ReputationEarning"));
        }

        [Fact]
        public void PostWalk_ReputationPenalty_CurveMatch_NoWarn()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedRepChanged(900, 20, 17, "ContractPenalty")          // -3
            };
            var action = new GameAction
            {
                UT = 900,
                Type = GameActionType.ReputationPenalty,
                NominalPenalty = 5f,
                RepPenaltySource = ReputationPenaltySource.ContractFail,
                EffectiveRep = -3f
            };
            var actions = new List<GameAction> { action };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
        }

        // ---------- KSC-path FundsEarning / ScienceEarning ----------

        [Fact]
        public void PostWalk_FundsEarning_KscPath_Match_NoWarn()
        {
            var events = new List<GameStateEvent>
            {
                // Keyed "Other" to represent a generic KSC-path funds earning whose
                // emitter did not attach a strategy/recovery/contract reason.
                MakeKeyedFundsChanged(1100, 20000, 21000, "Other")
            };
            var action = new GameAction
            {
                UT = 1100,
                Type = GameActionType.FundsEarning,
                FundsSource = FundsEarningSource.Other,
                Effective = true,
                FundsAwarded = 1000f
            };
            var actions = new List<GameAction> { action };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, funds)"));
        }

        [Fact]
        public void PostWalk_ScienceEarning_SubjectCapApplied_Match_NoWarn()
        {
            // ScienceEarning with EffectiveScience already post-cap. Observed matches.
            var events = new List<GameStateEvent>
            {
                MakeKeyedScienceChanged(1200, 0, 8.5, "ScienceTransmission")
            };
            var action = new GameAction
            {
                UT = 1200,
                Type = GameActionType.ScienceEarning,
                SubjectId = "crewReport@KerbinSrfLanded",
                Effective = true,
                ScienceAwarded = 10f,            // raw pre-cap
                EffectiveScience = 8.5f          // post-cap
            };
            var actions = new List<GameAction> { action };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, sci)"));
        }

        [Fact]
        public void PostWalk_ScienceEarning_EndAnchoredCommit_MatchesEarlierTransmissionWindow_NoWarn()
        {
            // #468: the committed ScienceEarning stays anchored at recording end/recovery
            // UT, but the paired ScienceTransmission events were emitted earlier in flight.
            var events = new List<GameStateEvent>
            {
                MakeKeyedScienceChanged(39.8, 0.0, 5.5, "ScienceTransmission", recordingId: "rec_sci_window"),
                MakeKeyedScienceChanged(66.3, 5.5, 11.0, "ScienceTransmission", recordingId: "rec_sci_window")
            };
            var action = new GameAction
            {
                UT = 204.4,
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec_sci_window",
                SubjectId = "mysteryGoo@KerbinSrfLandedLaunchPad",
                Effective = true,
                ScienceAwarded = 11.0f,
                EffectiveScience = 11.0f,
                StartUT = 10.0f,
                EndUT = 204.4f
            };
            var actions = new List<GameAction> { action };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, sci)"));
        }

        [Fact]
        public void PostWalk_ScienceEarning_EndAnchoredCommit_LargeUtFloatSpanStillMatchesEarlierTransmissionWindow_NoWarn()
        {
            // Reviewer follow-up: ScienceEarning spans round-trip through float fields.
            // At large UTs, the stored EndUT drifts from the double-backed action.UT by
            // more than 0.1s, so both the end-anchor gate and the widened window
            // boundaries must allow float quantization loss.
            const double actionUt = 10000000.4;

            var events = new List<GameStateEvent>
            {
                MakeKeyedScienceChanged(9999950.0, 0.0, 5.5, "ScienceTransmission", recordingId: "rec_sci_window_large_ut"),
                MakeKeyedScienceChanged(10000000.35, 5.5, 11.0, "ScienceTransmission", recordingId: "rec_sci_window_large_ut")
            };
            var action = new GameAction
            {
                UT = actionUt,
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec_sci_window_large_ut",
                SubjectId = "mysteryGoo@KerbinSrfLandedLaunchPad",
                Effective = true,
                ScienceAwarded = 11.0f,
                EffectiveScience = 11.0f,
                StartUT = 9999900.0f,
                EndUT = (float)actionUt
            };
            Assert.NotEqual(actionUt, (double)action.EndUT);

            LedgerOrchestrator.ReconcilePostWalk(events, new List<GameAction> { action }, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, sci)"));
        }

        [Fact]
        public void PostWalk_ScienceEarning_CollapsedLargeUtPersistedSpan_DoesNotFallbackToWholeRecording()
        {
            const double actionUt = 10000000.4;

            var rec = new Recording
            {
                RecordingId = "rec_sci_window_collapsed",
                VesselName = "Collapsed",
                ExplicitStartUT = 9999950.0,
                ExplicitEndUT = actionUt
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var events = new List<GameStateEvent>
            {
                MakeKeyedScienceChanged(9999950.0, 0.0, 3.0, "ScienceTransmission", recordingId: "rec_sci_window_collapsed"),
                MakeKeyedScienceChanged(10000000.35, 3.0, 8.5, "ScienceTransmission", recordingId: "rec_sci_window_collapsed")
            };
            var action = new GameAction
            {
                UT = actionUt,
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec_sci_window_collapsed",
                SubjectId = "mysteryGoo@KerbinSrfLandedLaunchPad",
                Effective = true,
                ScienceAwarded = 5.5f,
                EffectiveScience = 5.5f,
                StartUT = (float)10000000.35,
                EndUT = (float)actionUt
            };
            Assert.Equal(action.StartUT, action.EndUT);

            LedgerOrchestrator.ReconcilePostWalk(events, new List<GameAction> { action }, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, sci)"));
            Assert.Contains(logLines, l =>
                l.Contains("Post-walk match: ScienceEarning sci") &&
                l.Contains("expected=5.5, observed=5.5"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("falling back to recording rec_sci_window_collapsed"));
            Assert.Contains(logLines, l =>
                l.Contains("collapsed persisted span") &&
                l.Contains("rec_sci_window_collapsed"));
        }

        [Fact]
        public void PostWalk_ScienceEarning_CollapsedLargeUtPersistedSpan_UsesReconstructedScopeForUntaggedPreRecordingEvent()
        {
            const double actionUt = 10000000.4;

            var events = new List<GameStateEvent>
            {
                MakeKeyedScienceChanged(10000000.35, 0.0, 2.5, "ScienceTransmission"),
                MakeKeyedScienceChanged(10000000.38, 2.5, 5.5, "ScienceTransmission", recordingId: "rec_sci_window_collapsed_untagged")
            };
            var action = new GameAction
            {
                UT = actionUt,
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec_sci_window_collapsed_untagged",
                SubjectId = "mysteryGoo@KerbinSrfLandedLaunchPad",
                Effective = true,
                ScienceAwarded = 5.5f,
                EffectiveScience = 5.5f,
                Method = ScienceMethod.Transmitted,
                StartUT = (float)10000000.35,
                EndUT = (float)actionUt
            };
            Assert.Equal(action.StartUT, action.EndUT);

            LedgerOrchestrator.ReconcilePostWalk(events, new List<GameAction> { action }, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, sci)"));
            Assert.Contains(logLines, l =>
                l.Contains("Post-walk match: ScienceEarning sci") &&
                l.Contains("expected=5.5, observed=5.5"));
        }

        [Fact]
        public void PostWalk_ScienceEarning_AndMilestoneAchievement_SameUtNeighborhood_DoNotCrossCoalesce()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedScienceChanged(39.8, 0.0, 5.5, "ScienceTransmission", recordingId: "rec_mix"),
                MakeKeyedScienceChanged(66.3, 5.5, 11.0, "ScienceTransmission", recordingId: "rec_mix"),
                MakeKeyedScienceChanged(204.45, 11.0, 14.0, "Progression", recordingId: "rec_mix")
            };
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 204.4,
                    Type = GameActionType.ScienceEarning,
                    RecordingId = "rec_mix",
                    SubjectId = "mysteryGoo@KerbinSrfLandedLaunchPad",
                    Effective = true,
                    ScienceAwarded = 11.0f,
                    EffectiveScience = 11.0f,
                    StartUT = 10.0f,
                    EndUT = 204.4f
                },
                new GameAction
                {
                    UT = 204.45,
                    Type = GameActionType.MilestoneAchievement,
                    RecordingId = "rec_mix",
                    MilestoneId = "Kerbin/Escape",
                    Effective = true,
                    MilestoneScienceAwarded = 3.0f
                }
            };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, sci)"));

            var scienceMatches = logLines.FindAll(l =>
                l.Contains("Post-walk match: ScienceEarning sci"));
            var milestoneMatches = logLines.FindAll(l =>
                l.Contains("Post-walk match: MilestoneAchievement sci"));

            Assert.Single(scienceMatches);
            Assert.Single(milestoneMatches);
            Assert.Contains("within science window [10.0,204.4]", scienceMatches[0]);
            Assert.Contains("id=mysteryGoo@KerbinSrfLandedLaunchPad", scienceMatches[0]);
            Assert.Contains("within 0.1s of ut=204.5", milestoneMatches[0]);
            Assert.Contains("id=Kerbin/Escape", milestoneMatches[0]);
        }

        [Fact]
        public void PostWalk_ScienceEarning_EndAnchoredCommit_MultiSubjectRecording_CoalescesOnce()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedScienceChanged(39.8, 0.0, 5.5, "ScienceTransmission", recordingId: "rec_multi_subject"),
                MakeKeyedScienceChanged(66.3, 5.5, 11.0, "ScienceTransmission", recordingId: "rec_multi_subject"),
                MakeKeyedScienceChanged(80.0, 11.0, 15.0, "ScienceTransmission", recordingId: "rec_multi_subject")
            };
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 204.4,
                    Type = GameActionType.ScienceEarning,
                    RecordingId = "rec_multi_subject",
                    SubjectId = "mysteryGoo@KerbinSrfLandedLaunchPad",
                    Effective = true,
                    ScienceAwarded = 11.0f,
                    EffectiveScience = 11.0f,
                    StartUT = 10.0f,
                    EndUT = 204.4f
                },
                new GameAction
                {
                    UT = 204.4,
                    Type = GameActionType.ScienceEarning,
                    RecordingId = "rec_multi_subject",
                    SubjectId = "crewReport@KerbinSrfLandedLaunchPad",
                    Effective = true,
                    ScienceAwarded = 4.0f,
                    EffectiveScience = 4.0f,
                    StartUT = 10.0f,
                    EndUT = 204.4f
                }
            };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, sci)"));

            var scienceMatches = logLines.FindAll(l =>
                l.Contains("Post-walk match: ScienceEarning sci"));

            Assert.Single(scienceMatches);
            Assert.Contains(
                "ids=[mysteryGoo@KerbinSrfLandedLaunchPad, crewReport@KerbinSrfLandedLaunchPad] across 2 action(s)",
                scienceMatches[0]);
            Assert.Contains("expected=15.0, observed=15.0", scienceMatches[0]);
            Assert.Contains("within science window [10.0,204.4]", scienceMatches[0]);
        }

        [Fact]
        public void PostWalk_ScienceEarning_PreRecordingUntaggedAndRecoveredEvents_Match_NoWarn()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedScienceChanged(88.7, 0.0, 1.5, "ScienceTransmission"),
                MakeKeyedScienceChanged(94.6, 1.5, 2.7, "VesselRecovery"),
                MakeKeyedScienceChanged(116.2, 2.7, 6.2, "ScienceTransmission", recordingId: "rec_launchpad_prologue"),
                MakeKeyedScienceChanged(117.8, 6.2, 7.6, "VesselRecovery", recordingId: "rec_launchpad_prologue")
            };
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 248.8,
                    Type = GameActionType.ScienceEarning,
                    RecordingId = "rec_launchpad_prologue",
                    SubjectId = "mysteryGoo@KerbinSrfLandedLaunchPad",
                    Effective = true,
                    ScienceAwarded = 1.5f,
                    EffectiveScience = 1.5f,
                    Method = ScienceMethod.Transmitted,
                    StartUT = 88.7f,
                    EndUT = 248.8f
                },
                new GameAction
                {
                    UT = 248.8,
                    Type = GameActionType.ScienceEarning,
                    RecordingId = "rec_launchpad_prologue",
                    SubjectId = "temperatureScan@KerbinSrfLandedLaunchPad",
                    Effective = true,
                    ScienceAwarded = 1.2f,
                    EffectiveScience = 1.2f,
                    Method = ScienceMethod.Recovered,
                    StartUT = 94.6f,
                    EndUT = 248.8f
                },
                new GameAction
                {
                    UT = 248.8,
                    Type = GameActionType.ScienceEarning,
                    RecordingId = "rec_launchpad_prologue",
                    SubjectId = "mysteryGoo@KerbinFlyingLow",
                    Effective = true,
                    ScienceAwarded = 3.5f,
                    EffectiveScience = 3.5f,
                    Method = ScienceMethod.Transmitted,
                    StartUT = 116.2f,
                    EndUT = 248.8f
                },
                new GameAction
                {
                    UT = 248.8,
                    Type = GameActionType.ScienceEarning,
                    RecordingId = "rec_launchpad_prologue",
                    SubjectId = "telemetryReport@KerbinFlyingLow",
                    Effective = true,
                    ScienceAwarded = 1.4f,
                    EffectiveScience = 1.4f,
                    Method = ScienceMethod.Recovered,
                    StartUT = 117.8f,
                    EndUT = 248.8f
                }
            };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, sci)"));

            var scienceMatches = logLines.FindAll(l =>
                l.Contains("Post-walk match: ScienceEarning sci"));
            Assert.Equal(2, scienceMatches.Count);
            Assert.Contains(scienceMatches, l =>
                l.Contains("expected=5.0, observed=5.0") &&
                l.Contains("keyed 'ScienceTransmission'") &&
                l.Contains("within science window [88.7,248.8]"));
            Assert.Contains(scienceMatches, l =>
                l.Contains("expected=2.6, observed=2.6") &&
                l.Contains("keyed 'VesselRecovery'") &&
                l.Contains("within science window [94.6,248.8]"));
        }

        [Fact]
        public void PostWalk_ScienceEarning_RepeatedNonConvergingWindow_WarnsOnce()
        {
            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 248.8,
                    Type = GameActionType.ScienceEarning,
                    RecordingId = "rec_warn_once",
                    SubjectId = "mysteryGoo@KerbinFlyingLow",
                    Effective = true,
                    ScienceAwarded = 3.5f,
                    EffectiveScience = 3.5f,
                    Method = ScienceMethod.Transmitted,
                    StartUT = 116.2f,
                    EndUT = 248.8f
                }
            };

            LedgerOrchestrator.ReconcilePostWalk(new List<GameStateEvent>(), actions, utCutoff: null);
            LedgerOrchestrator.ReconcilePostWalk(new List<GameStateEvent>(), actions, utCutoff: null);

            Assert.Single(logLines.FindAll(l =>
                l.Contains("Earnings reconciliation (post-walk, sci)") &&
                l.Contains("mysteryGoo@KerbinFlyingLow")));
        }

        [Fact]
        public void Reconcile_ScienceWindow_IncludesPreRecordingUntaggedScienceEvents_NoWarn()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedScienceChanged(88.7, 0.0, 1.5, "ScienceTransmission"),
                MakeKeyedScienceChanged(89.0, 1.5, 2.1, "ScienceTransmission"),
                MakeKeyedScienceChanged(94.6, 2.1, 3.3, "VesselRecovery"),
                MakeKeyedScienceChanged(108.8, 3.3, 6.1, "VesselRecovery", recordingId: "rec_commit_science"),
                MakeKeyedScienceChanged(116.2, 6.1, 9.6, "ScienceTransmission", recordingId: "rec_commit_science"),
                MakeKeyedScienceChanged(117.8, 9.6, 11.0, "VesselRecovery", recordingId: "rec_commit_science")
            };
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 248.8,
                    Type = GameActionType.ScienceEarning,
                    RecordingId = "rec_commit_science",
                    SubjectId = "mysteryGoo@KerbinSrfLandedLaunchPad",
                    Effective = true,
                    ScienceAwarded = 1.5f,
                    EffectiveScience = 1.5f,
                    Method = ScienceMethod.Transmitted,
                    StartUT = 88.7f,
                    EndUT = 248.8f
                },
                new GameAction
                {
                    UT = 248.8,
                    Type = GameActionType.ScienceEarning,
                    RecordingId = "rec_commit_science",
                    SubjectId = "telemetryReport@KerbinSrfLandedLaunchPad",
                    Effective = true,
                    ScienceAwarded = 0.6f,
                    EffectiveScience = 0.6f,
                    Method = ScienceMethod.Transmitted,
                    StartUT = 89.0f,
                    EndUT = 248.8f
                },
                new GameAction
                {
                    UT = 248.8,
                    Type = GameActionType.ScienceEarning,
                    RecordingId = "rec_commit_science",
                    SubjectId = "temperatureScan@KerbinSrfLandedLaunchPad",
                    Effective = true,
                    ScienceAwarded = 1.2f,
                    EffectiveScience = 1.2f,
                    Method = ScienceMethod.Recovered,
                    StartUT = 94.6f,
                    EndUT = 248.8f
                },
                new GameAction
                {
                    UT = 248.8,
                    Type = GameActionType.ScienceEarning,
                    RecordingId = "rec_commit_science",
                    SubjectId = "temperatureScan@KerbinFlyingLowShores",
                    Effective = true,
                    ScienceAwarded = 2.8f,
                    EffectiveScience = 2.8f,
                    Method = ScienceMethod.Recovered,
                    StartUT = 108.8f,
                    EndUT = 248.8f
                },
                new GameAction
                {
                    UT = 248.8,
                    Type = GameActionType.ScienceEarning,
                    RecordingId = "rec_commit_science",
                    SubjectId = "mysteryGoo@KerbinFlyingLow",
                    Effective = true,
                    ScienceAwarded = 3.5f,
                    EffectiveScience = 3.5f,
                    Method = ScienceMethod.Transmitted,
                    StartUT = 116.2f,
                    EndUT = 248.8f
                },
                new GameAction
                {
                    UT = 248.8,
                    Type = GameActionType.ScienceEarning,
                    RecordingId = "rec_commit_science",
                    SubjectId = "telemetryReport@KerbinFlyingLow",
                    Effective = true,
                    ScienceAwarded = 1.4f,
                    EffectiveScience = 1.4f,
                    Method = ScienceMethod.Recovered,
                    StartUT = 117.8f,
                    EndUT = 248.8f
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(
                events,
                newActions,
                startUT: 100.3,
                endUT: 248.8,
                recordingId: "rec_commit_science");

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (sci)"));
        }

        [Fact]
        public void Reconcile_ScienceWindow_CollapsedLargeUtPersistedSpan_UsesReconstructedWindowForCommitMatch()
        {
            const double actionUt = 10000000.4;

            var events = new List<GameStateEvent>
            {
                MakeKeyedScienceChanged(10000000.35, 0.0, 2.5, "ScienceTransmission"),
                MakeKeyedScienceChanged(10000000.38, 2.5, 5.5, "ScienceTransmission", recordingId: "rec_commit_collapsed")
            };
            var action = new GameAction
            {
                UT = actionUt,
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec_commit_collapsed",
                SubjectId = "mysteryGoo@KerbinSrfLandedLaunchPad",
                Effective = true,
                ScienceAwarded = 5.5f,
                EffectiveScience = 5.5f,
                Method = ScienceMethod.Transmitted,
                StartUT = (float)10000000.35,
                EndUT = (float)actionUt
            };
            Assert.Equal(action.StartUT, action.EndUT);

            LedgerOrchestrator.ReconcileEarningsWindow(
                events,
                new List<GameAction> { action },
                startUT: 10000000.36,
                endUT: actionUt,
                recordingId: "rec_commit_collapsed");

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (sci)"));
            Assert.Contains(logLines, l =>
                l.Contains("extended science delta with 1") &&
                l.Contains("including 1 untagged pre-recording event(s)") &&
                l.Contains("rec_commit_collapsed"));
        }

        [Fact]
        public void Reconcile_ScienceWindow_StillWarnsForUnmatchedTaggedInWindowScience()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedScienceChanged(120.0, 0.0, 5.0, "ScienceTransmission", recordingId: "rec_commit_gap"),
                MakeKeyedScienceChanged(130.0, 5.0, 6.0, "VesselRecovery", recordingId: "rec_commit_gap")
            };
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 248.8,
                    Type = GameActionType.ScienceEarning,
                    RecordingId = "rec_commit_gap",
                    SubjectId = "mysteryGoo@KerbinFlyingLow",
                    Effective = true,
                    ScienceAwarded = 5.0f,
                    EffectiveScience = 5.0f,
                    Method = ScienceMethod.Transmitted,
                    StartUT = 120.0f,
                    EndUT = 248.8f
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(
                events,
                newActions,
                startUT: 100.3,
                endUT: 248.8,
                recordingId: "rec_commit_gap");

            Assert.Contains(logLines, l =>
                l.Contains("Earnings reconciliation (sci)") &&
                l.Contains("store delta=6.0") &&
                l.Contains("ledger emitted delta=5.0"));
        }

        // ---------- utCutoff ----------

        [Fact]
        public void PostWalk_UtCutoff_FiltersFutureActions_NoWarn()
        {
            // A ContractComplete at UT=500 with a funds-leg divergence. With utCutoff=200,
            // the action is filtered out and NO WARN fires (matches walk behavior).
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(500, 20000, 24700, "ContractReward")
            };
            var action = new GameAction
            {
                UT = 500,
                Type = GameActionType.ContractComplete,
                ContractId = "c_future",
                Effective = true,
                TransformedFundsReward = 9999f,   // gross divergence that WOULD warn
                EffectiveRep = 0f,
                TransformedScienceReward = 0f
            };
            var actions = new List<GameAction> { action };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: 200.0);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
        }

        // ---------- No matching event ----------

        [Fact]
        public void PostWalk_NoMatchingEvent_WarnsMissingChannel()
        {
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 500,
                    eventType = GameStateEventType.ContractCompleted,
                    key = "c_orphan"
                }
            };
            var action = new GameAction
            {
                UT = 500,
                Type = GameActionType.ContractComplete,
                ContractId = "c_orphan",
                Effective = true,
                TransformedFundsReward = 4700f,
                EffectiveRep = 0f,
                TransformedScienceReward = 0f
            };
            var actions = new List<GameAction> { action };

            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            Assert.Contains(logLines, l =>
                l.Contains("Earnings reconciliation (post-walk, funds)") &&
                l.Contains("ContractComplete") &&
                (l.Contains("no matching") || l.Contains("missing earning channel")));
        }

        [Fact]
        public void PostWalk_DoubleWarnGuard_TransformedActionOnlyFiresOnce()
        {
            // Regression guard for plan invariant I2 and success-criterion #6:
            // the existing per-action ReconcileKscAction path must NOT emit a
            // WARN for an action classified as Transformed, even when the
            // action genuinely diverges from the paired event. The post-walk
            // hook is the sole emitter of "Earnings reconciliation (post-walk,"
            // WARNs for the eight Transformed action types. If a future refactor
            // accidentally re-enables per-action WARN for a Transformed type,
            // this test catches the double-WARN regression.
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(500, 100000, 102000, "ContractReward"),
                MakeKeyedRepChanged(500, 10, 10, "ContractReward"),
                MakeKeyedScienceChanged(500, 0, 0, "ContractReward"),
            };
            var action = new GameAction
            {
                UT = 500,
                Type = GameActionType.ContractComplete,
                ContractId = "c_dbl",
                Effective = true,
                // Force a divergence: expected funds=+5000 but store saw +2000.
                TransformedFundsReward = 5000f,
                EffectiveRep = 0f,
                TransformedScienceReward = 0f,
            };
            var actions = new List<GameAction> { action };

            // Per-action KSC path (must stay silent on Transformed).
            Ledger.ResetForTesting();
            Ledger.AddAction(action);
            ReconcileKsc(events, new List<GameAction> { action }, action, action.UT);

            // Post-walk path (the only site allowed to WARN here).
            LedgerOrchestrator.ReconcilePostWalk(events, actions, utCutoff: null);

            // Post-walk must have emitted the mismatch WARN.
            Assert.Contains(logLines, l =>
                l.Contains("Earnings reconciliation (post-walk, funds)") &&
                l.Contains("ContractComplete"));

            // The KSC-path ReconcileKscAction must NOT have emitted a non-post-walk
            // funds WARN for the same action (it VERBOSE-skips Transformed types).
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Earnings reconciliation") &&
                l.Contains("(funds)") &&
                !l.Contains("(post-walk"));
        }

        #endregion

        [Fact]
        public void ReconcileKsc_PartPurchase_BypassOff_LegacyCostMismatch_WarnsDelta()
        {
            // #451 inverse: the pre-#451 shape — action FundsSpent=part.cost (450) but
            // KSP's event delta is -entryCost (-800). Reconciler MUST WARN with a delta-
            // mismatch line so a regression that re-introduces the part.cost capture
            // surfaces immediately. This is the "bug visible" assertion: it pins the
            // diagnostic that fires when ledger and KSP disagree on which field to use.
            try
            {
                GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = () => false;

                var events = new List<GameStateEvent>
                {
                    MakeKeyedFundsChanged(1600, 50000, 49200, "RnDPartPurchase")  // -800
                };
                var action = new GameAction
                {
                    UT = 1600,
                    Type = GameActionType.FundsSpending,
                    FundsSpendingSource = FundsSpendingSource.Other,
                    FundsSpent = 450f,                        // pre-#451 wrong value
                    DedupKey = "solidBooster.v2"
                };
                var ledger = new List<GameAction> { action };

                ReconcileKsc(events, ledger, action, 1600);

                Assert.Contains(logLines, l =>
                    l.Contains("[LedgerOrchestrator]") &&
                    l.Contains("KSC reconciliation (funds)") &&
                    l.Contains("delta mismatch"));
            }
            finally
            {
                GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = null;
            }
        }

        // ================================================================
        // #438 phase E1 — commit-window coverage for action types the switch
        // previously missed (ContractAccept, FacilityUpgrade, FacilityRepair)
        // plus positive-match coverage for types the switch already handled
        // (ContractFail/Cancel penalties, milestone science, ScienceEarning).
        // ================================================================

        [Fact]
        public void Reconcile_ContractAcceptAdvance_MatchesStore_Silent()
        {
            // #438 gap #1: ContractAccept inside a commit window emits a
            // FundsChanged(ContractAdvance) delta of +AdvanceFunds. Without the
            // production fix this test would warn (emitted=0 vs store=+2000).
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(150, 20000, 22000)  // +2000
            };
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 150,
                    Type = GameActionType.ContractAccept,
                    ContractId = "c-adv",
                    AdvanceFunds = 2000f
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                startUT: 100, endUT: 200);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
        }

        [Fact]
        public void Reconcile_ContractFailPenalty_MatchesStore_Silent()
        {
            // #438 gap #2: ContractFail penalties already handled by the switch;
            // pin the positive-match case so future refactors cannot silently
            // drop the FundsPenalty/RepPenalty subtraction.
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(150, 20000, 19000),  // -1000
                new GameStateEvent
                {
                    ut = 150,
                    eventType = GameStateEventType.ReputationChanged,
                    valueBefore = 10, valueAfter = 5
                }
            };
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 150,
                    Type = GameActionType.ContractFail,
                    ContractId = "cf1",
                    FundsPenalty = 1000f,
                    RepPenalty = 5f,
                    // #440B: rep penalty leg now reads EffectiveRep (signed negative).
                    EffectiveRep = -5f
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                startUT: 100, endUT: 200);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
        }

        [Fact]
        public void Reconcile_ContractCancelPenalty_MatchesStore_Silent()
        {
            // #438 gap #2: symmetric pin for ContractCancel — same switch case as
            // ContractFail but a distinct GameActionType, so the positive-match
            // case deserves its own test.
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(150, 20000, 19000),  // -1000
                new GameStateEvent
                {
                    ut = 150,
                    eventType = GameStateEventType.ReputationChanged,
                    valueBefore = 10, valueAfter = 5
                }
            };
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 150,
                    Type = GameActionType.ContractCancel,
                    ContractId = "cc1",
                    FundsPenalty = 1000f,
                    RepPenalty = 5f,
                    // #440B: rep penalty leg now reads EffectiveRep (signed negative).
                    EffectiveRep = -5f
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                startUT: 100, endUT: 200);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
        }

        [Fact]
        public void Reconcile_MilestoneAchievementWithScience_MatchesStore_Silent()
        {
            // #438 gap #3: extend the existing milestone test with a science-bearing
            // milestone. The switch already sums MilestoneScienceAwarded into
            // emittedSciDelta; this pins the three-channel positive match.
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(150, 0, 3000),
                new GameStateEvent
                {
                    ut = 150,
                    eventType = GameStateEventType.ReputationChanged,
                    valueBefore = 0, valueAfter = 5
                },
                new GameStateEvent
                {
                    ut = 150,
                    eventType = GameStateEventType.ScienceChanged,
                    valueBefore = 0, valueAfter = 20
                }
            };
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 150,
                    Type = GameActionType.MilestoneAchievement,
                    MilestoneId = "Mun/Landing",
                    MilestoneFundsAwarded = 3000f,
                    MilestoneRepAwarded = 5f,
                    MilestoneScienceAwarded = 20f,
                    // #440B: rep leg reads EffectiveRep; under identity curve matches raw.
                    Effective = true,
                    EffectiveRep = 5f
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                startUT: 100, endUT: 200);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
        }

        [Fact]
        public void Reconcile_ScienceEarningMatchesStore_Silent()
        {
            // #438 gap #4: only the mismatch/negative shape is pinned today
            // (Reconcile_ScienceMismatch_LogsWarn). Pin the symmetric positive
            // case for a standalone ScienceEarning inside the window.
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 150,
                    eventType = GameStateEventType.ScienceChanged,
                    valueBefore = 0, valueAfter = 16.44
                }
            };
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 150,
                    Type = GameActionType.ScienceEarning,
                    SubjectId = "surfaceSample@Mun",
                    ScienceAwarded = 16.44f,
                    // #440B: reads EffectiveScience (post-subject-cap). Under identity
                    // (subject not capped) EffectiveScience equals ScienceAwarded.
                    Effective = true,
                    EffectiveScience = 16.44f
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                startUT: 100, endUT: 200);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
        }

        [Fact]
        public void Reconcile_WorldFirstProgressReward_MatchesStore_Silent()
        {
            // #438 gap #5 (reconcile-side half): feed a MilestoneAchievement shaped
            // exactly like what GameStateEventConverter.ConvertMilestoneAchieved
            // produces from a world-first progress-reward detail string and assert
            // the three store deltas reconcile silently. The zero-science channel
            // is represented by omitting the ScienceChanged event (delta=0 side).
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(100, 0, 4800),
                new GameStateEvent
                {
                    ut = 100,
                    eventType = GameStateEventType.ReputationChanged,
                    valueBefore = 0, valueAfter = 2
                }
            };
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 100,
                    Type = GameActionType.MilestoneAchievement,
                    MilestoneId = "RecordsSpeed/Kerbin",
                    MilestoneFundsAwarded = 4800f,
                    MilestoneRepAwarded = 2f,
                    MilestoneScienceAwarded = 0f,
                    // #440B: rep leg reads EffectiveRep; identity curve -> matches raw.
                    Effective = true,
                    EffectiveRep = 2f
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                startUT: 50, endUT: 200);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
        }

        [Fact]
        public void Reconcile_FacilityUpgrade_MatchesStore_Silent()
        {
            // #438 gap #6 (upgrade half): without the production fix the store
            // sees -75000 and the emitted side sees 0, producing a false
            // "Earnings reconciliation (funds)" WARN. Post-fix: silent.
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(150, 200000, 125000)  // -75000
            };
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 150,
                    Type = GameActionType.FacilityUpgrade,
                    FacilityId = "VehicleAssemblyBuilding",
                    ToLevel = 2,
                    FacilityCost = 75000f
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                startUT: 100, endUT: 200);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
        }

        [Fact]
        public void Reconcile_FacilityRepair_MatchesStore_Silent()
        {
            // #438 gap #6 (repair half): same production fix covers both the
            // upgrade and repair branches so the symmetric pair is pinned.
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(150, 100000, 88000)  // -12000
            };
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 150,
                    Type = GameActionType.FacilityRepair,
                    FacilityId = "LaunchPad",
                    FacilityCost = 12000f
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                startUT: 100, endUT: 200);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
        }

        // ================================================================
        // #440B -- switch from raw to post-walk Transformed*/EffectiveRep/
        // EffectiveScience reads. These tests pin the swap at each swapped
        // switch leg, the Effective gates, the subject-cap behavior change,
        // the headline double-WARN closure, and the timing invariant.
        // ================================================================

        [Fact]
        public void Reconcile_ContractComplete_TransformedFundsReward_Silent()
        {
            // #440B positive: the switch reads TransformedFundsReward, not FundsReward.
            // Raw is 1000 (simulating a future non-identity transform), but the walk
            // produced 800 and KSP actually credited 800. No WARN.
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(150, 0, 800)
            };
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 150,
                    Type = GameActionType.ContractComplete,
                    ContractId = "c1",
                    Effective = true,
                    FundsReward = 1000f,              // raw (pre-transform)
                    TransformedFundsReward = 800f,    // walk output
                    RepReward = 0f,
                    EffectiveRep = 0f,
                    ScienceReward = 0f,
                    TransformedScienceReward = 0f
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                startUT: 100, endUT: 200);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
            // Log-assertion: a commit-time summary log should not warn; verify the
            // action type was processed (no false positive on the swapped leg).
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Earnings reconciliation (funds)") && l.Contains("1000"));
        }

        [Fact]
        public void Reconcile_ContractComplete_TransformedFundsReward_RawDelta_Warns()
        {
            // #440B inverse pin: store-side observed a raw-shaped delta (+1000) while
            // the walk produced TransformedFundsReward=800. The switch reads 800 and
            // WARNs because store (+1000) != emitted (+800).
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(150, 0, 1000)   // raw-aligned
            };
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 150,
                    Type = GameActionType.ContractComplete,
                    ContractId = "c1",
                    Effective = true,
                    FundsReward = 1000f,
                    TransformedFundsReward = 800f,
                    RepReward = 0f,
                    EffectiveRep = 0f,
                    ScienceReward = 0f,
                    TransformedScienceReward = 0f
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                startUT: 100, endUT: 200);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("Earnings reconciliation (funds)") &&
                l.Contains("1000") && l.Contains("800"));
        }

        [Fact]
        public void Reconcile_ContractComplete_NotEffective_SkipsEmitted_Silent()
        {
            // #440B gate: Effective=false (duplicate completion). The switch must skip
            // the entire ContractComplete case so raw FundsReward does not leak into
            // emittedFundsDelta. Store reflects zero (no real credit fired). No WARN.
            var events = new List<GameStateEvent>();   // no FundsChanged
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 150,
                    Type = GameActionType.ContractComplete,
                    ContractId = "c-dup",
                    Effective = false,                 // duplicate -- walk skips credit
                    FundsReward = 5000f,               // raw non-zero (would leak without gate)
                    TransformedFundsReward = 0f,
                    EffectiveRep = 0f,
                    TransformedScienceReward = 0f
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                startUT: 100, endUT: 200);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
        }

        [Fact]
        public void Reconcile_MilestoneAchievement_NotEffective_SkipsEmitted_Silent()
        {
            // #440B gate: duplicate milestone (Effective=false) must not leak raw
            // MilestoneFundsAwarded into emittedFundsDelta. Mirrors ReconcilePostWalk.
            var events = new List<GameStateEvent>();
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 150,
                    Type = GameActionType.MilestoneAchievement,
                    MilestoneId = "FirstLaunch",
                    Effective = false,
                    MilestoneFundsAwarded = 3000f,
                    MilestoneRepAwarded = 5f,
                    MilestoneScienceAwarded = 20f,
                    EffectiveRep = 0f
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                startUT: 100, endUT: 200);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
        }

        [Fact]
        public void Reconcile_MilestoneAchievement_OtherRecordingProgressionIgnored_Silent()
        {
            var events = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(150, 50000, 52880, "Progression", recordingId: "rec-parent"),
                MakeKeyedFundsChanged(150, 52880, 53360, "Progression", recordingId: "rec-child")
            };
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 150,
                    Type = GameActionType.MilestoneAchievement,
                    RecordingId = "rec-parent",
                    MilestoneId = "RecordsSpeed",
                    Effective = true,
                    MilestoneFundsAwarded = 2880f
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(
                events,
                newActions,
                startUT: 100,
                endUT: 200,
                recordingId: "rec-parent");

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("ReconcileEarningsWindow: skipped 1 event(s) tagged to other recordings") &&
                l.Contains("scope='rec-parent'"));
        }

        [Fact]
        public void Reconcile_ScienceEarning_AtSubjectCap_EffectiveScienceZero_Silent()
        {
            // #440B on-main behaviour change: a capped subject has ScienceAwarded > 0
            // but EffectiveScience = 0 (ScienceModule applied the per-subject cap).
            // Store does not fire ScienceChanged for a zero credit, so under the raw
            // read the pre-fix switch would WARN (emitted +16.44 vs store 0). Post-fix
            // reads EffectiveScience = 0 and stays silent. See plan risk R4.
            var events = new List<GameStateEvent>();   // no ScienceChanged fired
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 150,
                    Type = GameActionType.ScienceEarning,
                    SubjectId = "crewReport@KerbinSrfLanded",
                    Effective = true,
                    ScienceAwarded = 16.44f,           // raw pre-cap
                    EffectiveScience = 0f              // subject at cap
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                startUT: 100, endUT: 200);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation"));
        }

        [Fact]
        public void Reconcile_DoubleWarn_BothHooksReadSameDerivedValue_SingleWarn()
        {
            // #440B headline regression guard. Both ReconcileEarningsWindow (commit-path)
            // and ReconcilePostWalk (rewind-path) must read the SAME derived value for a
            // ContractComplete funds leg. Before #440B the commit-path read raw
            // FundsReward and the post-walk read TransformedFundsReward; on a non-identity
            // transform they WARNed with opposing shapes (one saying 1000 vs 800, the
            // other 800 vs 1000). Post-fix: both agree on 800 always.

            // Case 1: transformed matches KSP. Both hooks must stay silent.
            var events1 = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(500, 0, 800, "ContractReward")
            };
            var action1 = new GameAction
            {
                UT = 500,
                Type = GameActionType.ContractComplete,
                ContractId = "c-dbl-1",
                Effective = true,
                FundsReward = 1000f,
                TransformedFundsReward = 800f,
                EffectiveRep = 0f,
                TransformedScienceReward = 0f
            };
            var actions1 = new List<GameAction> { action1 };

            LedgerOrchestrator.ReconcileEarningsWindow(events1, actions1,
                startUT: 450, endUT: 550);
            LedgerOrchestrator.ReconcilePostWalk(events1, actions1, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (funds)"));
            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, funds)"));

            // Case 2: transformed diverges from KSP. Both hooks WARN with the SAME
            // expected=800 and observed=1000 (not opposing 1000-vs-800 / 800-vs-1000).
            logLines.Clear();
            var events2 = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(500, 0, 1000, "ContractReward")
            };
            var action2 = new GameAction
            {
                UT = 500,
                Type = GameActionType.ContractComplete,
                ContractId = "c-dbl-2",
                Effective = true,
                FundsReward = 1000f,
                TransformedFundsReward = 800f,
                EffectiveRep = 0f,
                TransformedScienceReward = 0f
            };
            var actions2 = new List<GameAction> { action2 };

            LedgerOrchestrator.ReconcileEarningsWindow(events2, actions2,
                startUT: 450, endUT: 550);
            LedgerOrchestrator.ReconcilePostWalk(events2, actions2, utCutoff: null);

            Assert.Contains(logLines, l =>
                l.Contains("Earnings reconciliation (funds)") && l.Contains("800"));
            Assert.Contains(logLines, l =>
                l.Contains("Earnings reconciliation (post-walk, funds)") && l.Contains("800"));

            // Case 3: the exact double-WARN scenario the #440B TODO flagged.
            // Transformed matches KSP (800 vs 800), raw is stale at 1000.
            // Pre-fix the commit-path would WARN (1000 vs 800) while post-walk was silent.
            // Post-fix: both silent. This closes the divergence.
            logLines.Clear();
            var events3 = new List<GameStateEvent>
            {
                MakeKeyedFundsChanged(500, 0, 800, "ContractReward")
            };
            var action3 = new GameAction
            {
                UT = 500,
                Type = GameActionType.ContractComplete,
                ContractId = "c-dbl-3",
                Effective = true,
                FundsReward = 1000f,                // stale raw
                TransformedFundsReward = 800f,      // walk output matches KSP
                EffectiveRep = 0f,
                TransformedScienceReward = 0f
            };
            var actions3 = new List<GameAction> { action3 };

            LedgerOrchestrator.ReconcileEarningsWindow(events3, actions3,
                startUT: 450, endUT: 550);
            LedgerOrchestrator.ReconcilePostWalk(events3, actions3, utCutoff: null);

            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (funds)"));
            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk, funds)"));
        }

        [Fact]
        public void Reconcile_CalledBeforeRecalculate_ReadsZeroDerived_WarnsVisibly()
        {
            // #440B timing invariant I2. Documents the failure mode when the production
            // caller reorders ReconcileEarningsWindow to run BEFORE RecalculateAndPatch:
            // TransformedFundsReward defaults to 0, so the switch reads 0 and WARNs on
            // every ContractComplete. If this test ever stops firing, someone silently
            // re-broke the ordering contract.
            var events = new List<GameStateEvent>
            {
                MakeFundsChanged(150, 0, 5000)
            };
            var newActions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 150,
                    Type = GameActionType.ContractComplete,
                    ContractId = "c-pre-walk",
                    Effective = true,
                    FundsReward = 5000f,
                    // TransformedFundsReward intentionally left as 0f default
                    // (simulates pre-walk state).
                    EffectiveRep = 0f,
                    TransformedScienceReward = 0f
                }
            };

            LedgerOrchestrator.ReconcileEarningsWindow(events, newActions,
                startUT: 100, endUT: 200);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("Earnings reconciliation (funds)") &&
                l.Contains("5000") && l.Contains("0"));
        }

    }
}
