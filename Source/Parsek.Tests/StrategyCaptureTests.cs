using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// #439 Phase A: strategy lifecycle capture coverage. These tests exercise the
    /// pure-static detail builders, the converter branches, the recorder emitters
    /// (exercised directly with a reflectively-constructed <c>Strategies.Strategy</c>
    /// is not possible without a live KSP runtime -- see the skipped test below), and
    /// the classifier wiring. Harmony patch coverage itself is deferred to an in-game
    /// test: <c>Strategies.Strategy</c> requires a live <c>StrategyConfig</c> and
    /// PartLoader to construct, which xUnit cannot set up from scratch.
    ///
    /// Pattern copied from <c>MilestoneRewardCaptureTests</c>: log capture, shared
    /// static state resets in ctor/Dispose, <c>[Collection("Sequential")]</c>.
    /// </summary>
    [Collection("Sequential")]
    public class StrategyCaptureTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        public StrategyCaptureTests()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateRecorder.ResetForTesting();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Detail builders (pure static)
        // ================================================================

        [Fact]
        public void BuildStrategyActivateDetail_FormatsAsExpected()
        {
            string s = GameStateRecorder.BuildStrategyActivateDetail(
                title: "Unpaid Research Program",
                dept: "Science",
                factor: 0.25f,
                setupFunds: 7500f,
                setupSci: 0f,
                setupRep: 0f,
                sourceResource: StrategyResource.Reputation,
                targetResource: StrategyResource.Science);

            Assert.Contains("title=Unpaid Research Program", s);
            Assert.Contains("dept=Science", s);
            Assert.Contains("factor=0.25", s);
            Assert.Contains("source=Reputation", s);
            Assert.Contains("target=Science", s);
            Assert.Contains("setupFunds=7500", s);
            Assert.Contains("setupSci=0", s);
            Assert.Contains("setupRep=0", s);

            // Eight semicolon-separated fields, order-stable.
            Assert.Equal(7, s.Split(';').Length - 1);
        }

        [Fact]
        public void BuildStrategyActivateDetail_IncludesSciRepSetup()
        {
            logLines.Clear();

            string s = GameStateRecorder.BuildStrategyActivateDetail(
                title: "Aggressive Negotiations",
                dept: "Administration",
                factor: 0.3f,
                setupFunds: 25000f,
                setupSci: 12.5f,
                setupRep: 8f);

            Assert.Contains("setupFunds=25000", s);
            Assert.Contains("setupSci=12.5", s);
            Assert.Contains("setupRep=8", s);
            Assert.Empty(logLines);
        }

        [Fact]
        public void BuildStrategyActivateDetail_UsesInvariantCulture()
        {
            // 0.25 must stringify as "0.25", never "0,25" even on comma-locale hosts.
            string s = GameStateRecorder.BuildStrategyActivateDetail(
                title: "t", dept: "d",
                factor: 0.25f, setupFunds: 1000.5f, setupSci: 0.5f, setupRep: -1.75f);

            Assert.DoesNotContain(",", s);
            Assert.Contains("factor=0.25", s);
            Assert.Contains("setupFunds=1000.5", s);
            Assert.Contains("setupSci=0.5", s);
            Assert.Contains("setupRep=-1.75", s);
        }

        [Fact]
        public void BuildStrategyDeactivateDetail_FormatsAsExpected()
        {
            string s = GameStateRecorder.BuildStrategyDeactivateDetail(
                title: "Unpaid Research Program",
                dept: "Science",
                factor: 0.1f,
                activeDurationSec: 12345.5);

            Assert.Contains("title=Unpaid Research Program", s);
            Assert.Contains("dept=Science", s);
            Assert.Contains("factor=0.1", s);
            Assert.Contains("activeDurationSec=12345.5", s);
            Assert.DoesNotContain(",", s);
        }

        [Fact]
        public void BuildStrategyActivateDetail_NullInputs_ProduceEmptyFields()
        {
            string s = GameStateRecorder.BuildStrategyActivateDetail(
                title: null, dept: null,
                factor: 0f, setupFunds: 0f, setupSci: 0f, setupRep: 0f);

            // Null fields serialize as empty strings; the semicolon skeleton stays intact
            // so the converter's ExtractDetail can still index by key name.
            Assert.Contains("title=;", s);
            Assert.Contains("dept=;", s);
            Assert.Contains("source=Funds", s);
            Assert.Contains("target=Funds", s);
        }

        // ================================================================
        // Converter round-trip
        // ================================================================

        [Fact]
        public void ConvertStrategyActivated_ReturnsStrategyActivateAction()
        {
            var evt = new GameStateEvent
            {
                ut = 1234.0,
                eventType = GameStateEventType.StrategyActivated,
                key = "UnpaidResearchProgram",
                detail = GameStateRecorder.BuildStrategyActivateDetail(
                    "Unpaid Research Program", "Science",
                    factor: 0.25f, setupFunds: 7500f, setupSci: 0f, setupRep: 0f,
                    sourceResource: StrategyResource.Reputation,
                    targetResource: StrategyResource.Science)
            };

            var action = GameStateEventConverter.ConvertStrategyActivated(evt, "rec-1");

            Assert.Equal(GameActionType.StrategyActivate, action.Type);
            Assert.Equal("UnpaidResearchProgram", action.StrategyId);
            Assert.Equal(StrategyResource.Reputation, action.SourceResource);
            Assert.Equal(StrategyResource.Science, action.TargetResource);
            Assert.Equal(0.25f, action.Commitment);
            Assert.Equal(7500f, action.SetupCost);
            Assert.Equal(1234.0, action.UT);
            Assert.Equal("rec-1", action.RecordingId);
        }

        [Fact]
        public void ConvertStrategyActivated_ParsesSciRepSetupFromDetail()
        {
            logLines.Clear();

            var evt = new GameStateEvent
            {
                ut = 1234.0,
                eventType = GameStateEventType.StrategyActivated,
                key = "AggressiveNegotiations",
                detail = GameStateRecorder.BuildStrategyActivateDetail(
                    "Aggressive Negotiations", "Administration",
                    factor: 0.3f, setupFunds: 25000f, setupSci: 12.5f, setupRep: 8f)
            };

            var action = GameStateEventConverter.ConvertStrategyActivated(evt, "rec-1");

            Assert.Equal(25000f, action.SetupCost);
            Assert.Equal(12.5f, action.SetupScienceCost);
            Assert.Equal(8f, action.SetupReputationCost);
            Assert.Empty(logLines);
        }

        [Fact]
        public void ConvertStrategyDeactivated_ReturnsStrategyDeactivateAction()
        {
            var evt = new GameStateEvent
            {
                ut = 5000.0,
                eventType = GameStateEventType.StrategyDeactivated,
                key = "UnpaidResearchProgram",
                detail = GameStateRecorder.BuildStrategyDeactivateDetail(
                    "Unpaid Research Program", "Science",
                    factor: 0.25f, activeDurationSec: 3766.0)
            };

            var action = GameStateEventConverter.ConvertStrategyDeactivated(evt, "rec-1");

            Assert.Equal(GameActionType.StrategyDeactivate, action.Type);
            Assert.Equal("UnpaidResearchProgram", action.StrategyId);
            Assert.Equal(5000.0, action.UT);
            Assert.Equal("rec-1", action.RecordingId);
        }

        [Fact]
        public void ConvertStrategyActivated_MissingDetailFields_DefaultToZero()
        {
            // Resilience: a legacy or malformed StrategyActivated event whose detail
            // lacks factor/setupFunds must not throw; missing numeric fields default
            // to zero and the action still carries the strategy id.
            var evt = new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.StrategyActivated,
                key = "BarebonesStrategy",
                detail = "title=Barebones"
            };

            var action = GameStateEventConverter.ConvertStrategyActivated(evt, null);

            Assert.Equal(GameActionType.StrategyActivate, action.Type);
            Assert.Equal("BarebonesStrategy", action.StrategyId);
            Assert.Equal(StrategyResource.Funds, action.SourceResource);
            Assert.Equal(StrategyResource.Funds, action.TargetResource);
            Assert.Equal(0f, action.Commitment);
            Assert.Equal(0f, action.SetupCost);
        }

        [Fact]
        public void ConvertEvents_RoutesStrategyEvents()
        {
            // End-to-end: ConvertEvents dispatches both event types through the right
            // branches in ConvertEvent.
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 100.0,
                    eventType = GameStateEventType.StrategyActivated,
                    key = "StratA",
                    recordingId = "rec-e2e",
                    detail = GameStateRecorder.BuildStrategyActivateDetail(
                        "A", "Science", 0.1f, 5000f, 0f, 0f,
                        sourceResource: StrategyResource.Reputation,
                        targetResource: StrategyResource.Science)
                },
                new GameStateEvent
                {
                    ut = 200.0,
                    eventType = GameStateEventType.StrategyDeactivated,
                    key = "StratA",
                    recordingId = "rec-e2e",
                    detail = GameStateRecorder.BuildStrategyDeactivateDetail(
                        "A", "Science", 0.1f, 100.0)
                }
            };

            var actions = GameStateEventConverter.ConvertEvents(events, "rec-e2e", 0, 1000);

            Assert.Equal(2, actions.Count);
            Assert.Equal(GameActionType.StrategyActivate, actions[0].Type);
            Assert.Equal(GameActionType.StrategyDeactivate, actions[1].Type);
            Assert.Equal(StrategyResource.Reputation, actions[0].SourceResource);
            Assert.Equal(StrategyResource.Science, actions[0].TargetResource);
            Assert.Equal(5000f, actions[0].SetupCost);
        }

        // ================================================================
        // Save/load round-trip via GameStateEvent (SerializeInto / DeserializeFrom)
        // ================================================================

        [Fact]
        public void SerializeDeserialize_StrategyActivated_OmitsLegacyEpochOnWrite()
        {
            var original = new GameStateEvent
            {
                ut = 4242.25,
                eventType = GameStateEventType.StrategyActivated,
                key = "UnpaidResearchProgram",
                detail = GameStateRecorder.BuildStrategyActivateDetail(
                    "Unpaid Research Program", "Science",
                    factor: 0.25f, setupFunds: 7500f, setupSci: 0f, setupRep: 0f),
                epoch = 3,
                recordingId = "rec-round-trip"
            };

            var node = new ConfigNode("EVENT");
            original.SerializeInto(node);
            var reloaded = GameStateEvent.DeserializeFrom(node);

            Assert.Null(node.GetValue("epoch"));
            Assert.Equal(original.eventType, reloaded.eventType);
            Assert.Equal(original.ut, reloaded.ut);
            Assert.Equal(original.key, reloaded.key);
            Assert.Equal(original.detail, reloaded.detail);
            Assert.Equal(0u, reloaded.epoch);
            Assert.Equal(original.recordingId, reloaded.recordingId);
        }

        [Fact]
        public void SerializeDeserialize_StrategyDeactivated_RoundTripsByteExact()
        {
            var original = new GameStateEvent
            {
                ut = 9999.0,
                eventType = GameStateEventType.StrategyDeactivated,
                key = "UnpaidResearchProgram",
                detail = GameStateRecorder.BuildStrategyDeactivateDetail(
                    "Unpaid Research Program", "Science",
                    factor: 0.25f, activeDurationSec: 5757.0)
            };

            var node = new ConfigNode("EVENT");
            original.SerializeInto(node);
            var reloaded = GameStateEvent.DeserializeFrom(node);

            Assert.Equal(original.eventType, reloaded.eventType);
            Assert.Equal(original.ut, reloaded.ut);
            Assert.Equal(original.key, reloaded.key);
            Assert.Equal(original.detail, reloaded.detail);
        }

        [Fact]
        public void RoundTrip_StrategyActivatedWithSciRepSetup_SurvivesSaveLoad()
        {
            logLines.Clear();

            var original = new GameAction
            {
                UT = 4242.25,
                Type = GameActionType.StrategyActivate,
                RecordingId = "rec-round-trip",
                StrategyId = "AggressiveNegotiations",
                Commitment = 0.3f,
                SetupCost = 25000f,
                SetupScienceCost = 12.5f,
                SetupReputationCost = 8f
            };

            var parent = new ConfigNode("ROOT");
            original.SerializeInto(parent);
            var reloaded = GameAction.DeserializeFrom(parent.GetNode("GAME_ACTION"));

            Assert.Equal(original.Type, reloaded.Type);
            Assert.Equal(original.UT, reloaded.UT);
            Assert.Equal(original.RecordingId, reloaded.RecordingId);
            Assert.Equal(original.StrategyId, reloaded.StrategyId);
            Assert.Equal(original.Commitment, reloaded.Commitment);
            Assert.Equal(original.SetupCost, reloaded.SetupCost);
            Assert.Equal(original.SetupScienceCost, reloaded.SetupScienceCost);
            Assert.Equal(original.SetupReputationCost, reloaded.SetupReputationCost);
            Assert.Empty(logLines);
        }

        [Fact]
        public void EnumValues_StrategyActivatedAndDeactivated_AppendedAt20And21()
        {
            // Append-only enum contract: existing saves with event ids 0-19 continue
            // to load, and any future additions must keep these two IDs fixed.
            Assert.Equal(20, (int)GameStateEventType.StrategyActivated);
            Assert.Equal(21, (int)GameStateEventType.StrategyDeactivated);
        }

        // ================================================================
        // Classifier (ClassifyAction) and reconciliation behavior
        // ================================================================

        [Fact]
        public void ClassifyAction_StrategyActivate_FundsLegPresent()
        {
            var a = new GameAction
            {
                Type = GameActionType.StrategyActivate,
                StrategyId = "S1",
                SetupCost = 7500f,
                UT = 100.0
            };

            var exp = LedgerOrchestrator.ClassifyAction(a);

            Assert.Equal(KscActionExpectationClassifier.KscReconcileClass.Untransformed, exp.Class);
            Assert.True(exp.FundsLeg.IsPresent);
            Assert.Equal(GameStateEventType.FundsChanged, exp.FundsLeg.EventType);
            Assert.Equal("StrategySetup", exp.FundsLeg.ExpectedReasonKey);
            Assert.Equal(-7500.0, exp.FundsLeg.ExpectedDelta);
        }

        [Fact]
        public void ClassifyAction_StrategyActivate_ZeroSetupCost_ShortCircuits()
        {
            var a = new GameAction
            {
                Type = GameActionType.StrategyActivate,
                StrategyId = "FreeStrat",
                SetupCost = 0f,
                SetupScienceCost = 0f,
                SetupReputationCost = 0f,
                UT = 100.0
            };
            var exp = LedgerOrchestrator.ClassifyAction(a);
            Assert.Equal(KscActionExpectationClassifier.KscReconcileClass.Untransformed, exp.Class);
            Assert.Equal(0, exp.LegCount);

            // Reconcile with an empty event stream -- must stay silent.
            LedgerOrchestrator.ReconcileKscAction(
                new List<GameStateEvent>(),
                new List<GameAction> { a },
                a,
                100.0);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        [Fact]
        public void ClassifyAction_StrategyDeactivate_NoResourceImpact()
        {
            var a = new GameAction
            {
                Type = GameActionType.StrategyDeactivate,
                StrategyId = "S1",
                UT = 500.0
            };
            var exp = LedgerOrchestrator.ClassifyAction(a);
            Assert.Equal(KscActionExpectationClassifier.KscReconcileClass.NoResourceImpact, exp.Class);
        }

        [Fact]
        public void ReconcileKscAction_StrategyActivate_MatchingStrategySetupDebit_NoWarn()
        {
            // Happy path: the StrategyActivated patch fires after Strategy.Activate
            // deducted InitialCostFunds, so the FundsChanged(StrategySetup) event is
            // already in the store. Reconcile must match it cleanly.
            var events = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 1000.0,
                    eventType = GameStateEventType.FundsChanged,
                    key = "StrategySetup",
                    valueBefore = 50000.0,
                    valueAfter = 42500.0  // -7500
                }
            };
            var action = new GameAction
            {
                UT = 1000.0,
                Type = GameActionType.StrategyActivate,
                StrategyId = "UnpaidResearch",
                SetupCost = 7500f
            };
            var ledger = new List<GameAction> { action };

            LedgerOrchestrator.ReconcileKscAction(events, ledger, action, 1000.0);

            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation"));
        }

        // ================================================================
        // Emitter behavior (static helpers; Harmony patch tested in-game)
        // ================================================================

        [Fact]
        public void BuildStrategyActivateDetail_ConverterRoundTrip_PreservesNumericFields()
        {
            // Belt-and-braces: go from builder -> event -> converter and assert the
            // numeric fields survive the IC round-trip with full precision.
            string detail = GameStateRecorder.BuildStrategyActivateDetail(
                "T", "D", factor: 0.125f, setupFunds: 12345.5f,
                setupSci: 0f, setupRep: 0f,
                sourceResource: StrategyResource.Science,
                targetResource: StrategyResource.Reputation);
            var evt = new GameStateEvent
            {
                ut = 1.0,
                eventType = GameStateEventType.StrategyActivated,
                key = "S",
                detail = detail
            };
            var action = GameStateEventConverter.ConvertStrategyActivated(evt, null);

            Assert.Equal(StrategyResource.Science, action.SourceResource);
            Assert.Equal(StrategyResource.Reputation, action.TargetResource);
            Assert.Equal(0.125f, action.Commitment);
            Assert.Equal(12345.5f, action.SetupCost);
            Assert.Equal(0f, action.SetupScienceCost);
            Assert.Equal(0f, action.SetupReputationCost);
        }

        // ================================================================
        // End-to-end: walk a StrategyActivate action through the ledger
        // ================================================================

        [Fact]
        public void EndToEnd_ActivateDeactivateWalk_PopulatesActiveStrategies()
        {
            // Register Strategies at the module position the RecalculationEngine
            // expects and walk an activate + deactivate pair. The module must see
            // both actions and land in the expected activeStrategies state.
            var strategies = new StrategiesModule();
            RecalculationEngine.RegisterModule(
                strategies, RecalculationEngine.ModuleTier.Strategy);

            var activateEvt = new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.StrategyActivated,
                key = "UnpaidResearch",
                detail = GameStateRecorder.BuildStrategyActivateDetail(
                    "Unpaid Research Program", "Science",
                    factor: 0.1f, setupFunds: 5000f, setupSci: 0f, setupRep: 0f)
            };

            var activateAction = GameStateEventConverter.ConvertStrategyActivated(
                activateEvt, null);
            var actions = new List<GameAction> { activateAction };

            RecalculationEngine.Recalculate(actions);
            Assert.True(strategies.IsStrategyActive("UnpaidResearch"));
            Assert.Equal(1, strategies.GetActiveStrategyCount());

            // Deactivate at UT=200.
            var deactivateEvt = new GameStateEvent
            {
                ut = 200.0,
                eventType = GameStateEventType.StrategyDeactivated,
                key = "UnpaidResearch",
                detail = GameStateRecorder.BuildStrategyDeactivateDetail(
                    "Unpaid Research Program", "Science",
                    factor: 0.1f, activeDurationSec: 100.0)
            };
            actions.Add(GameStateEventConverter.ConvertStrategyDeactivated(
                deactivateEvt, null));

            RecalculationEngine.Recalculate(actions);
            Assert.False(strategies.IsStrategyActive("UnpaidResearch"));
            Assert.Equal(0, strategies.GetActiveStrategyCount());
        }

        [Fact]
        public void EndToEnd_OnKscSpending_StrategyActivated_WritesStrategyActivateAction()
        {
            // OnKscSpending routes the StrategyActivated event through ConvertEvent
            // into the ledger, assigning a sequence number and running reconciliation.
            LedgerOrchestrator.Initialize();

            var evt = new GameStateEvent
            {
                ut = 1234.0,
                eventType = GameStateEventType.StrategyActivated,
                key = "UnpaidResearch",
                detail = GameStateRecorder.BuildStrategyActivateDetail(
                    "Unpaid Research Program", "Science",
                    factor: 0.1f, setupFunds: 5000f, setupSci: 4f, setupRep: 3f)
            };
            GameStateStore.AddEvent(ref evt);

            float repBefore = 100f;
            var repResult = ReputationModule.ApplyReputationCurve(-3f, repBefore);

            var fundsEvt = new GameStateEvent
            {
                ut = 1234.0,
                eventType = GameStateEventType.FundsChanged,
                key = "StrategySetup",
                valueBefore = 50000.0,
                valueAfter = 45000.0
            };
            GameStateStore.AddEvent(ref fundsEvt);
            var scienceEvt = new GameStateEvent
            {
                ut = 1234.0,
                eventType = GameStateEventType.ScienceChanged,
                key = "StrategySetup",
                valueBefore = 20.0,
                valueAfter = 16.0
            };
            GameStateStore.AddEvent(ref scienceEvt);
            var repEvt = new GameStateEvent
            {
                ut = 1234.0,
                eventType = GameStateEventType.ReputationChanged,
                key = "StrategySetup",
                valueBefore = repBefore,
                valueAfter = repBefore + repResult.actualDelta
            };
            GameStateStore.AddEvent(ref repEvt);

            LedgerOrchestrator.OnKscSpending(evt);

            var written = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.StrategyActivate &&
                a.StrategyId == "UnpaidResearch");
            Assert.NotNull(written);
            Assert.Equal(5000f, written.SetupCost);
            Assert.Equal(4f, written.SetupScienceCost);
            Assert.Equal(3f, written.SetupReputationCost);
            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation (funds)"));
            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation (sci)"));
            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation (rep)"));

            // The INFO line from GameStateRecorder.OnContractCompleted's cousin is
            // the KSC spending line; pin it so the log-assertion requirement is met.
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("KSC spending recorded") &&
                l.Contains("StrategyActivate"));
        }

        [Fact]
        public void RecalculateAndPatch_StrategyActivateSetupCostsAffectScienceAndRepBalances()
        {
            LedgerOrchestrator.Initialize();
            logLines.Clear();

            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ScienceInitial,
                InitialScience = 20f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ReputationInitial,
                InitialReputation = 100f
            });

            var action = new GameAction
            {
                UT = 100.0,
                Type = GameActionType.StrategyActivate,
                StrategyId = "AggressiveNegotiations",
                SetupScienceCost = 4f,
                SetupReputationCost = 3f
            };
            Ledger.AddAction(action);

            var repResult = ReputationModule.ApplyReputationCurve(-3f, 100f);

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(16.0, LedgerOrchestrator.Science.GetRunningScience(), 3);
            Assert.Equal(16.0, LedgerOrchestrator.Science.GetAvailableScience(), 3);
            Assert.Equal(4.0, LedgerOrchestrator.Science.GetTotalCommittedSpendings(), 3);
            Assert.Equal(repResult.actualDelta, action.EffectiveRep, 0.001f);
            Assert.Equal(repResult.newRep, LedgerOrchestrator.Reputation.GetRunningRep(), 0.001f);
            Assert.Contains(logLines, l =>
                l.Contains("[ScienceModule]") &&
                l.Contains("StrategyActivate science") &&
                l.Contains("AggressiveNegotiations"));
            Assert.Contains(logLines, l =>
                l.Contains("[Reputation]") &&
                l.Contains("StrategyActivate rep") &&
                l.Contains("AggressiveNegotiations"));
        }

        [Fact]
        public void EndToEnd_OnKscSpending_StrategyDeactivated_WritesNoResourceAction()
        {
            LedgerOrchestrator.Initialize();

            var evt = new GameStateEvent
            {
                ut = 2000.0,
                eventType = GameStateEventType.StrategyDeactivated,
                key = "UnpaidResearch",
                detail = GameStateRecorder.BuildStrategyDeactivateDetail(
                    "Unpaid Research Program", "Science",
                    factor: 0.1f, activeDurationSec: 1000.0)
            };

            LedgerOrchestrator.OnKscSpending(evt);

            var written = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.StrategyDeactivate &&
                a.StrategyId == "UnpaidResearch");
            Assert.NotNull(written);
            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation: Funds mismatch"));
        }
    }
}
