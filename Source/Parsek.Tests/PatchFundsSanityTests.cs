using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers §H/#402 of career-earnings-bundle plan: PatchFunds/PatchScience must
    /// log a WARN when a single recalculation drops more than 10% of a non-trivial
    /// resource pool. This is a tripwire for bugs in the earning-channel plumbing —
    /// the c1 save had a ledger with zero FundsEarning actions, so every recalc
    /// drove funds to the module's computed target (zero), and this diagnostic
    /// would have fired loudly on the first commit.
    ///
    /// The live PatchFunds/PatchScience entry points touch KSP singletons; this
    /// test covers the extracted IsSuspiciousDrawdown helper directly.
    /// </summary>
    [Collection("Sequential")]
    public class PatchFundsSanityTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PatchFundsSanityTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            KspStatePatcher.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void IsSuspiciousDrawdown_BigDropFromBigPool_IsSuspicious()
        {
            // 50% drawdown from 10000: true
            Assert.True(KspStatePatcher.IsSuspiciousDrawdown(-5000, 10000));
        }

        [Fact]
        public void IsSuspiciousDrawdown_SmallDrop_IsNotSuspicious()
        {
            // 1% drawdown: false
            Assert.False(KspStatePatcher.IsSuspiciousDrawdown(-100, 10000));
        }

        [Fact]
        public void IsSuspiciousDrawdown_Addition_IsNotSuspicious()
        {
            // Positive delta (earnings): false regardless of magnitude
            Assert.False(KspStatePatcher.IsSuspiciousDrawdown(+5000, 10000));
            Assert.False(KspStatePatcher.IsSuspiciousDrawdown(+9999999, 10000));
        }

        [Fact]
        public void IsSuspiciousDrawdown_SmallPool_IsNotSuspicious()
        {
            // Below the 1000 pool-floor threshold, no warning fires.
            // Someone with 500 funds and a recalc that drops them to 100 is
            // uninteresting noise.
            Assert.False(KspStatePatcher.IsSuspiciousDrawdown(-400, 500));
            Assert.False(KspStatePatcher.IsSuspiciousDrawdown(-999, 999));
        }

        [Fact]
        public void IsSuspiciousDrawdown_BoundaryExactly10Percent_NotSuspicious()
        {
            // Exactly 10% is NOT suspicious — the check is strict ">10%"
            Assert.False(KspStatePatcher.IsSuspiciousDrawdown(-1000, 10000));
        }

        [Fact]
        public void IsSuspiciousDrawdown_BoundaryJustAbove10Percent_Suspicious()
        {
            Assert.True(KspStatePatcher.IsSuspiciousDrawdown(-1001, 10000));
        }

        [Fact]
        public void IsSuspiciousDrawdown_ZeroPool_NotSuspicious()
        {
            Assert.False(KspStatePatcher.IsSuspiciousDrawdown(0, 0));
            Assert.False(KspStatePatcher.IsSuspiciousDrawdown(-1, 0));
        }
    }
}
