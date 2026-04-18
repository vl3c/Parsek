using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// #440 Phase E2 -- integration tests that drive
    /// <see cref="LedgerOrchestrator.RecalculateAndPatch"/> end-to-end and assert
    /// the post-walk reconciliation hook fires (or does not fire) correctly.
    ///
    /// Each test seeds the Ledger + GameStateStore with an action/event pair
    /// that exercises one code path through <c>ReconcilePostWalk</c>:
    /// * commit-path baseline (no WARN when walk output matches observed KSP delta)
    /// * controlled-divergence (exactly one funds-leg WARN when Transformed*Reward
    ///   diverges from the store delta)
    /// * utCutoff filter (future actions past the cutoff do not reconcile)
    ///
    /// Shares the [Collection("Sequential")] scope with the other reconciliation
    /// test fixtures because <c>LedgerOrchestrator</c>, <c>Ledger</c>, and
    /// <c>GameStateStore</c> all hold static state.
    /// </summary>
    [Collection("Sequential")]
    public class PostWalkReconciliationIntegrationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PostWalkReconciliationIntegrationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void AddKeyedEvent(
            double ut, GameStateEventType type, string key, double before, double after,
            string recordingId = "")
        {
            var e = new GameStateEvent
            {
                ut = ut,
                eventType = type,
                key = key,
                valueBefore = before,
                valueAfter = after,
                recordingId = recordingId
            };
            GameStateStore.AddEvent(ref e);
        }

        [Fact]
        public void Integration_StrategyActive_ContractComplete_NoPostWalkWarn()
        {
            // Commit-path baseline. Seed initial funds, a StrategyActivate, and a
            // ContractComplete whose raw rewards match the observed KSP deltas.
            // After Recalculate populates Transformed*Reward + EffectiveRep, the
            // post-walk hook finds a clean match on all three legs -> no WARN.
            LedgerOrchestrator.Initialize();

            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 25000f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ReputationInitial,
                InitialReputation = 0f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.StrategyActivate,
                StrategyId = "UnpaidResearch",
                SetupCost = 0f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 500.0,
                Type = GameActionType.ContractComplete,
                RecordingId = "rec-contract",
                ContractId = "c-strat-1",
                FundsReward = 4700f,
                RepReward = 7f,
                ScienceReward = 3f
            });

            // KSP-side store events observed at the same UT (paired with the
            // ContractComplete by key "ContractReward").
            AddKeyedEvent(500.0, GameStateEventType.FundsChanged,      "ContractReward", 25000, 29700, "rec-contract");
            AddKeyedEvent(500.0, GameStateEventType.ReputationChanged, "ContractReward",     0,     7, "rec-contract");
            AddKeyedEvent(500.0, GameStateEventType.ScienceChanged,    "ContractReward",     0,     3, "rec-contract");

            LedgerOrchestrator.RecalculateAndPatch(utCutoff: null);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("Earnings reconciliation (post-walk,"));

            // Positive assertion: the post-walk summary ran (proves the hook was wired).
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("Post-walk reconcile: actions="));
        }

        [Fact]
        public void Integration_SameUtContractRewards_CoalescedEvent_DoesNotWarn()
        {
            // Two contract rewards inside the 0.1 s coalesce window share one
            // FundsChanged(ContractReward) event in the store. Post-walk must sum the
            // expected side across both actions before comparing, or it false-warns on
            // each individual action against the coalesced delta.
            LedgerOrchestrator.Initialize();

            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 25000f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ReputationInitial,
                InitialReputation = 0f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 500.0,
                Type = GameActionType.ContractComplete,
                RecordingId = "rec-coalesce",
                ContractId = "c-coalesce-a",
                FundsReward = 3000f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 500.05,
                Type = GameActionType.ContractComplete,
                RecordingId = "rec-coalesce",
                ContractId = "c-coalesce-b",
                FundsReward = 4000f
            });

            AddKeyedEvent(500.0, GameStateEventType.FundsChanged, "ContractReward", 25000, 32000, "rec-coalesce");

            LedgerOrchestrator.RecalculateAndPatch(utCutoff: null);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("Earnings reconciliation (post-walk, funds)") &&
                l.Contains("ContractComplete"));
        }

        [Fact]
        public void Integration_StrategyActive_ContractComplete_TransformDiverges_Warn()
        {
            // Controlled divergence: the raw FundsReward is 4700, which the walk will
            // copy to TransformedFundsReward (identity transform today). But we seed
            // the FundsChanged event with a LARGER observed delta (+9200) so the
            // funds leg diverges -> exactly one post-walk funds WARN fires. Rep + sci
            // match -> no WARN on those legs.
            LedgerOrchestrator.Initialize();

            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 25000f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ReputationInitial,
                InitialReputation = 0f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 500.0,
                Type = GameActionType.ContractComplete,
                RecordingId = "rec-contract",
                ContractId = "c-diverge-1",
                FundsReward = 4700f,
                RepReward = 7f,
                ScienceReward = 3f
            });

            // Store observed +9200 funds, +7 rep, +3 sci -> funds diverges by 4500.
            AddKeyedEvent(500.0, GameStateEventType.FundsChanged,      "ContractReward", 25000, 34200, "rec-contract");
            AddKeyedEvent(500.0, GameStateEventType.ReputationChanged, "ContractReward",     0,     7, "rec-contract");
            AddKeyedEvent(500.0, GameStateEventType.ScienceChanged,    "ContractReward",     0,     3, "rec-contract");

            LedgerOrchestrator.RecalculateAndPatch(utCutoff: null);

            int fundsWarns = 0;
            foreach (var l in logLines)
            {
                if (l.Contains("Earnings reconciliation (post-walk, funds)") &&
                    l.Contains("ContractComplete"))
                    fundsWarns++;
            }
            Assert.Equal(1, fundsWarns);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("Earnings reconciliation (post-walk, rep)"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Earnings reconciliation (post-walk, sci)"));
        }

        [Fact]
        public void Integration_RewindCutoffsFiltersPostWalk()
        {
            // A ContractComplete at UT=500 with a gross funds divergence that WOULD
            // warn. With utCutoff=200, the walk filters the action out and the
            // post-walk hook mirrors that filter -> no WARN.
            LedgerOrchestrator.Initialize();

            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 25000f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ReputationInitial,
                InitialReputation = 0f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 500.0,
                Type = GameActionType.ContractComplete,
                RecordingId = "rec-future",
                ContractId = "c-past-cutoff",
                FundsReward = 99999f,
                RepReward = 7f,
                ScienceReward = 3f
            });

            AddKeyedEvent(500.0, GameStateEventType.FundsChanged, "ContractReward", 25000, 29700, "rec-future");

            LedgerOrchestrator.RecalculateAndPatch(utCutoff: 200.0);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("Earnings reconciliation (post-walk,"));
        }
    }
}
