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
    /// flag on the target is the unfloored projected minimum; with it off nothing changes.
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
        }

        public void Dispose()
        {
            KspStatePatcher.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // The audit's sequence: total 10000, a committed future spend of 8000 (live patched
        // to 2000), then a contract fails with a 5000 penalty. Running 5000, projected
        // minimum -3000; stock (flag on) leaves live at -3000.
        private const double Running = 5000.0;
        private const double ProjectedMin = -3000.0;
        private const double LiveAfterPenalty = -3000.0;

        [Theory]
        [InlineData(0.0, -3000.0, true, -3000.0)]   // floor hides a real deficit: unfloored
        [InlineData(0.0, -3000.0, false, 0.0)]      // flag off: stock clamps at 0, keep the floor
        [InlineData(2000.0, 2000.0, true, 2000.0)]  // positive pool: unchanged either way
        [InlineData(0.0, 0.0, true, 0.0)]           // exactly zero: unchanged
        [InlineData(0.0, double.NaN, true, 0.0)]    // no readable minimum: keep the floor
        public void ResolveNegativeCurrencyPatchTarget_Cases(
            double floored, double unflooredMin, bool allowNegative, double expected)
        {
            Assert.Equal(expected,
                KspStatePatcher.ResolveNegativeCurrencyPatchTarget(floored, unflooredMin, allowNegative));
        }

        // catches: the false "GUARDED UPLIFT ... missing spending channel" clamp every recalc
        // while a flag-on pool is legitimately negative.
        [Fact]
        public void FlagOn_NegativePool_NonAuthoritativeRecalc_NoUpliftClamp_NoWrite()
        {
            double target = KspStatePatcher.ResolveNegativeCurrencyPatchTarget(
                0.0, ProjectedMin, allowNegativeCurrency: true);

            var decision = KspStatePatcher.ResolveFundsPatch(
                LiveAfterPenalty, target, Running, authoritativeReduction: false);

            Assert.False(decision.Clamped);
            Assert.False(decision.ShouldWrite);
            Assert.Equal(LiveAfterPenalty, decision.EffectiveTarget);
        }

        // Documents the defect the fix removes: the floored target against the same
        // negative live pool trips the uplift clamp.
        [Fact]
        public void FlooredTarget_NegativePool_WouldTripTheUpliftClamp()
        {
            var decision = KspStatePatcher.ResolveFundsPatch(
                LiveAfterPenalty, 0.0, Running, authoritativeReduction: false);

            Assert.True(decision.Clamped);
            Assert.Equal(KspStatePatcher.ClampDirection.Down, decision.Direction);
        }

        // catches: an authoritative recalc (rewind, re-fly) lifting the deficit to 0.
        [Fact]
        public void FlagOn_NegativePool_AuthoritativeRecalc_KeepsTheDeficit()
        {
            double target = KspStatePatcher.ResolveNegativeCurrencyPatchTarget(
                0.0, ProjectedMin, allowNegativeCurrency: true);

            var decision = KspStatePatcher.ResolveFundsPatch(
                LiveAfterPenalty, target, Running, authoritativeReduction: true);

            Assert.False(decision.ShouldWrite);
            Assert.Equal(ProjectedMin, decision.EffectiveTarget);
        }

        // Flag off: stock clamped the penalty at 0, the target is 0, nothing to do.
        [Fact]
        public void FlagOff_PoolClampedAtZero_Unchanged()
        {
            double target = KspStatePatcher.ResolveNegativeCurrencyPatchTarget(
                0.0, ProjectedMin, allowNegativeCurrency: false);

            var decision = KspStatePatcher.ResolveFundsPatch(
                0.0, target, Running, authoritativeReduction: false);

            Assert.Equal(0.0, target);
            Assert.False(decision.Clamped);
            Assert.False(decision.ShouldWrite);
        }

        // Same floor on the science pool (ScienceModule.GetAvailableScience floors at 0).
        [Fact]
        public void FlagOn_NegativeSciencePool_NoUpliftClamp()
        {
            double target = KspStatePatcher.ResolveNegativeCurrencyPatchTarget(
                0.0, -40.0, allowNegativeCurrency: true);

            var decision = KspStatePatcher.ResolveSciencePoolPatch(
                -40f, target, 60.0, authoritativeReduction: false);

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
