using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers §F of career-earnings-bundle plan: KSC PartPurchased actions must not
    /// collide under DeduplicateAgainstLedger even though they share a null RecordingId.
    /// Before the fix, GetActionKey for FundsSpending returned "" for every KSC part —
    /// so the first one won and every subsequent purchase at the same (approx) UT was
    /// silently dropped as "duplicate".
    /// </summary>
    [Collection("Sequential")]
    public class FundsSpendingDedupKeyTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public FundsSpendingDedupKeyTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void GetActionKey_KscPartPurchase_UsesDedupKey()
        {
            var a = new GameAction
            {
                Type = GameActionType.FundsSpending,
                RecordingId = null,
                FundsSpendingSource = FundsSpendingSource.Other,
                DedupKey = "mk1pod"
            };

            string key = LedgerOrchestrator.GetActionKey(a);

            Assert.Equal(":mk1pod", key);
        }

        [Fact]
        public void GetActionKey_TwoDifferentParts_YieldDifferentKeys()
        {
            var a = new GameAction
            {
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                DedupKey = "mk1pod"
            };
            var b = new GameAction
            {
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                DedupKey = "solidBooster"
            };

            Assert.NotEqual(LedgerOrchestrator.GetActionKey(a), LedgerOrchestrator.GetActionKey(b));
        }

        [Fact]
        public void GetActionKey_VesselBuildVsKscPurchase_DifferentKeys()
        {
            // VesselBuild: RecordingId set, DedupKey null
            var build = new GameAction
            {
                Type = GameActionType.FundsSpending,
                RecordingId = "rec-A",
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                DedupKey = null
            };
            // KSC purchase: RecordingId null, DedupKey = part name
            var purchase = new GameAction
            {
                Type = GameActionType.FundsSpending,
                RecordingId = null,
                FundsSpendingSource = FundsSpendingSource.Other,
                DedupKey = "mk1pod"
            };

            Assert.NotEqual(LedgerOrchestrator.GetActionKey(build), LedgerOrchestrator.GetActionKey(purchase));
        }

        [Fact]
        public void DeduplicateAgainstLedger_TwoPartPurchasesSameUT_BothSurvive()
        {
            LedgerOrchestrator.Initialize();

            // Seed the ledger with the first KSC purchase (as if written via OnKscSpending earlier)
            var existing = new GameAction
            {
                UT = 100.0,
                Type = GameActionType.FundsSpending,
                RecordingId = null,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f,
                DedupKey = "mk1pod"
            };
            Ledger.AddAction(existing);

            // Candidate: a DIFFERENT part bought at (almost) the same UT.
            var candidate = new GameAction
            {
                UT = 100.05,
                Type = GameActionType.FundsSpending,
                RecordingId = null,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 200f,
                DedupKey = "solidBooster"
            };
            var candidates = new List<GameAction> { candidate };

            var result = LedgerOrchestrator.DeduplicateAgainstLedger(candidates);

            Assert.Single(result);
            Assert.Equal("solidBooster", result[0].DedupKey);
        }

        [Fact]
        public void DeduplicateAgainstLedger_SamePartSameUT_OneSurvives()
        {
            // Regression: duplicates of the exact same part at the same UT MUST still dedup.
            // This is the "save protection" half of the dedup key; we only care about
            // part-name disambiguation.
            LedgerOrchestrator.Initialize();

            var existing = new GameAction
            {
                UT = 100.0,
                Type = GameActionType.FundsSpending,
                RecordingId = null,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f,
                DedupKey = "mk1pod"
            };
            Ledger.AddAction(existing);

            var candidate = new GameAction
            {
                UT = 100.05,
                Type = GameActionType.FundsSpending,
                RecordingId = null,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 600f,
                DedupKey = "mk1pod"
            };
            var candidates = new List<GameAction> { candidate };

            var result = LedgerOrchestrator.DeduplicateAgainstLedger(candidates);

            Assert.Empty(result);
        }

        [Fact]
        public void ConvertPartPurchased_SetsDedupKeyToPartName()
        {
            var evt = new GameStateEvent
            {
                ut = 42.0,
                eventType = GameStateEventType.PartPurchased,
                key = "liquidFuelTank",
                detail = "cost=300"
            };

            var action = GameStateEventConverter.ConvertEvent(evt, null);

            Assert.NotNull(action);
            Assert.Equal(GameActionType.FundsSpending, action.Type);
            Assert.Equal("liquidFuelTank", action.DedupKey);
            Assert.Equal(FundsSpendingSource.Other, action.FundsSpendingSource);
            Assert.Equal(300f, action.FundsSpent);
        }

        [Fact]
        public void FundsSpending_DedupKey_RoundTripsThroughConfigNode()
        {
            // Regression for codex review [P1]: DedupKey was not persisted, so reloads
            // collapsed all KSC part-purchase actions to the same "" key and the recovery
            // migration re-synthesized them on every load.
            var original = new GameAction
            {
                UT = 42.0,
                Type = GameActionType.FundsSpending,
                RecordingId = null,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 300f,
                DedupKey = "liquidFuelTank"
            };

            var parent = new ConfigNode("LEDGER");
            original.SerializeInto(parent);
            var actionNode = parent.GetNode("GAME_ACTION");
            var reloaded = GameAction.DeserializeFrom(actionNode);

            Assert.NotNull(reloaded);
            Assert.Equal("liquidFuelTank", reloaded.DedupKey);
            Assert.Equal(300f, reloaded.FundsSpent);
            Assert.Equal(FundsSpendingSource.Other, reloaded.FundsSpendingSource);
        }

        [Fact]
        public void FundsSpending_DedupKeyNull_RoundTripsAsNull()
        {
            // VesselBuild path leaves DedupKey null — must not round-trip as "" either.
            var original = new GameAction
            {
                UT = 10.0,
                Type = GameActionType.FundsSpending,
                RecordingId = "rec-A",
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                FundsSpent = 5000f,
                DedupKey = null
            };

            var parent = new ConfigNode("LEDGER");
            original.SerializeInto(parent);
            var actionNode = parent.GetNode("GAME_ACTION");
            var reloaded = GameAction.DeserializeFrom(actionNode);

            Assert.Null(reloaded.DedupKey);
        }
    }
}
