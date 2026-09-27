using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// KSP-SETTINGS-FOLLOWUPS-2026-09-27 item 3: with stock
    /// <c>AdvancedParams.AllowNegativeCurrency</c> on (Moderate / Hard), an involuntary debit
    /// (a contract failure penalty) leaves the live pool negative. The patch target used to be
    /// floored at 0, so the uplift guard read the gap as a missing spending channel (WARN
    /// every recalc plus a toast) and an authoritative recalc lifted the deficit to 0. With the
    /// flag on the target may go below zero only by a deficit that already exists now (the
    /// lower of the running balance and the live pool), never by a reserved FUTURE deficit.
    /// With the flag off nothing changes.
    /// </summary>
    [Collection("Sequential")]
    public class AllowNegativeCurrencyPatchTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public AllowNegativeCurrencyPatchTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            KspStatePatcher.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Theory]
        // floored, projectedMin, runningNow, live, flag -> target
        [InlineData(0.0, -3000.0, -3000.0, -3000.0, true, -3000.0)]  // a past penalty: running and live negative
        [InlineData(0.0, -3000.0, 5000.0, -3000.0, true, -3000.0)]   // reservation + penalty: only live negative
        [InlineData(0.0, -50000.0, 20000.0, 10000.0, true, 0.0)]     // FUTURE deficit only: stays floored
        [InlineData(0.0, -11000.0, -3000.0, -3000.0, true, -3000.0)] // existing deficit plus a future one: existing only
        [InlineData(0.0, -3000.0, -3000.0, -3000.0, false, 0.0)]     // flag off: floored
        [InlineData(2000.0, 2000.0, 5000.0, 2000.0, true, 2000.0)]   // positive pool: unchanged
        [InlineData(0.0, double.NaN, -3000.0, -3000.0, true, 0.0)]   // no readable minimum: keep the floor
        public void ResolveNegativeCurrencyPatchTarget_Cases(
            double floored, double projectedMin, double runningNow, double live,
            bool allowNegative, double expected)
        {
            Assert.Equal(expected, KspStatePatcher.ResolveNegativeCurrencyPatchTarget(
                floored, projectedMin, runningNow, live, allowNegative));
        }

        // catches: the review repro - a reserved FUTURE deficit (a committed 70k upgrade at
        // UT 600 that a superseded 60k reward no longer covers) written into the live pool
        // at UT 150, before any debit happened. Real walk over the real FundsModule.
        [Fact]
        public void Funds_FutureReservedDeficit_IsNotWrittenIntoTheLivePool()
        {
            Ledger.AddAction(new GameAction { UT = 0.0, Type = GameActionType.FundsInitial, InitialFunds = 20000f });
            Ledger.AddAction(new GameAction
            {
                UT = 600.0, Type = GameActionType.FacilityUpgrade,
                FacilityId = "SpaceCenter/LaunchPad", ToLevel = 2, FacilityCost = 70000f, Effective = true
            });

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(150.0, "negative-currency-test");
            FundsModule funds = LedgerOrchestrator.Funds;

            Assert.Equal(20000.0, funds.GetRunningBalance());
            Assert.Equal(-50000.0, funds.GetProjectionMinBalance());
            Assert.Equal(0.0, funds.GetAvailableFunds());

            double target = KspStatePatcher.ResolveFundsPatchTarget(funds, currentLive: 10000.0,
                allowNegativeCurrency: true);
            Assert.Equal(0.0, target);
            Assert.Equal(target, KspStatePatcher.ResolveFundsPatchTarget(funds, 10000.0, false));
        }

        // Same repro for science: a committed tech unlock the pool cannot cover yet.
        [Fact]
        public void Science_FutureReservedDeficit_IsNotWrittenIntoTheLivePool()
        {
            Ledger.AddAction(new GameAction { UT = 0.0, Type = GameActionType.ScienceInitial, InitialScience = 100f });
            Ledger.AddAction(new GameAction
            {
                UT = 600.0, Type = GameActionType.ScienceSpending, NodeId = "basicRocketry", Cost = 300f
            });

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(150.0, "negative-currency-test");
            ScienceModule science = LedgerOrchestrator.Science;

            Assert.Equal(100.0, science.GetRunningScience(), 3);
            Assert.Equal(-200.0, science.GetProjectionMinBalance(), 3);

            Assert.Equal(0.0, KspStatePatcher.ResolveSciencePatchBaseTarget(science, 50.0, true));
        }

        // A real past penalty made the running balance negative: the pool stays negative (no
        // lift to 0) and the patch neither writes nor clamps (no false GUARDED UPLIFT warn).
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Funds_PastPenaltyDeficit_StaysNegative_NoUpliftClamp(bool authoritative)
        {
            Ledger.AddAction(new GameAction { UT = 0.0, Type = GameActionType.FundsInitial, InitialFunds = 2000f });
            Ledger.AddAction(new GameAction
            {
                UT = 100.0, Type = GameActionType.FundsSpending,
                FundsSpent = 5000f, FundsSpendingSource = FundsSpendingSource.Other
            });

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(150.0, "negative-currency-test");
            FundsModule funds = LedgerOrchestrator.Funds;
            Assert.Equal(-3000.0, funds.GetRunningBalance());

            double target = KspStatePatcher.ResolveFundsPatchTarget(funds, currentLive: -3000.0,
                allowNegativeCurrency: true);
            Assert.Equal(-3000.0, target);

            logLines.Clear();
            var decision = KspStatePatcher.ResolveFundsPatch(
                -3000.0, target, funds.GetRunningBalance(), authoritative);
            Assert.False(decision.Clamped);
            Assert.False(decision.ShouldWrite);
            Assert.DoesNotContain(logLines, l => l.Contains("GUARDED UPLIFT"));
        }

        // The audit sequence: a reservation lowered live to 2000, a 5000 penalty took it to
        // -3000 while the ledger's running balance is still 5000. Live is the existing deficit.
        [Fact]
        public void Funds_ReservationPlusPenalty_LiveDeficitKept_NoUpliftClamp()
        {
            double target = KspStatePatcher.ResolveNegativeCurrencyPatchTarget(
                0.0, -3000.0, runningBalanceNow: 5000.0, currentLive: -3000.0, allowNegativeCurrency: true);

            var decision = KspStatePatcher.ResolveFundsPatch(-3000.0, target, 5000.0, authoritativeReduction: false);

            Assert.False(decision.Clamped);
            Assert.False(decision.ShouldWrite);
        }

        // Documents the defect the fix removes: the floored target against a negative live
        // pool trips the uplift clamp.
        [Fact]
        public void FlooredTarget_NegativePool_WouldTripTheUpliftClamp()
        {
            var decision = KspStatePatcher.ResolveFundsPatch(-3000.0, 0.0, 5000.0, authoritativeReduction: false);

            Assert.True(decision.Clamped);
            Assert.Equal(KspStatePatcher.ClampDirection.Down, decision.Direction);
        }

        [Fact]
        public void Science_PastDeficit_NoUpliftClamp()
        {
            double target = KspStatePatcher.ResolveNegativeCurrencyPatchTarget(
                0.0, -40.0, runningBalanceNow: -40.0, currentLive: -40.0, allowNegativeCurrency: true);

            var decision = KspStatePatcher.ResolveSciencePoolPatch(-40f, target, -40.0, authoritativeReduction: false);

            Assert.Equal(-40.0, target);
            Assert.False(decision.Clamped);
            Assert.False(decision.ShouldWrite);
        }

        [Fact]
        public void ReadAllowNegativeCurrency_SeamAndNoGame()
        {
            Assert.False(KspStatePatcher.ReadAllowNegativeCurrency()); // no live game
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = () => true;
            Assert.True(KspStatePatcher.ReadAllowNegativeCurrency());
            KspStatePatcher.ResetForTesting();
            Assert.Null(KspStatePatcher.AllowNegativeCurrencyProviderForTesting);
        }
    }
}
