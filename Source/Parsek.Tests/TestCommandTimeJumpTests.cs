using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// M-C1 coverage for the pure TimeJump core: forward-only validation, target
    /// resolution (exactly one of ut / deltaSeconds, InvariantCulture), the two-phase
    /// completion decision, and the completion payload. Fails if a backward jump is
    /// admitted, a locale comma is parsed, the settle window is skipped, or a payload key
    /// drifts.
    /// </summary>
    public class TestCommandTimeJumpTests
    {
        private static string Val(List<KeyValuePair<string, string>> p, string key)
            => p.First(kv => kv.Key == key).Value;

        // ----- IsForwardJump (mirrors TimeJumpManager.IsValidJump: strict target > now) -----

        [Fact]
        public void IsForwardJump_ForwardTrue_BackwardOrZeroFalse()
        {
            Assert.True(TestCommandTimeJump.IsForwardJump(100.0, 100.001));
            Assert.False(TestCommandTimeJump.IsForwardJump(100.0, 100.0));   // zero jump
            Assert.False(TestCommandTimeJump.IsForwardJump(100.0, 99.0));    // backward
        }

        // ----- ResolveTargetUt -----

        [Fact]
        public void ResolveTargetUt_AbsoluteUt_ResolvesExactly()
        {
            double t = TestCommandTimeJump.ResolveTargetUt(1000.0, "5000.5", null, out string err);
            Assert.Null(err);
            Assert.Equal(5000.5, t, 6);
        }

        [Fact]
        public void ResolveTargetUt_PositiveDelta_AddsToNow()
        {
            double t = TestCommandTimeJump.ResolveTargetUt(1000.0, null, "600.0", out string err);
            Assert.Null(err);
            Assert.Equal(1600.0, t, 6);
        }

        [Fact]
        public void ResolveTargetUt_BothPresent_MissingJumpTarget()
        {
            TestCommandTimeJump.ResolveTargetUt(1000.0, "5000", "600", out string err);
            Assert.Equal("missing-jump-target", err);
        }

        [Fact]
        public void ResolveTargetUt_NeitherPresent_MissingJumpTarget()
        {
            TestCommandTimeJump.ResolveTargetUt(1000.0, null, "", out string err);
            Assert.Equal("missing-jump-target", err);
        }

        [Fact]
        public void ResolveTargetUt_LocaleCommaDelta_Errors()
        {
            // "600,0" must NOT parse under InvariantCulture (a comma-locale leak).
            TestCommandTimeJump.ResolveTargetUt(1000.0, null, "600,0", out string err);
            Assert.Equal("missing-jump-target", err);
        }

        [Fact]
        public void ResolveTargetUt_UnparseableUt_Errors()
        {
            TestCommandTimeJump.ResolveTargetUt(1000.0, "not-a-number", null, out string err);
            Assert.Equal("missing-jump-target", err);
        }

        [Fact]
        public void ResolveTargetUt_NegativeDelta_ResolvesButNotForward()
        {
            // ResolveTargetUt is parse-only; the forward gate is IsForwardJump. A negative
            // delta resolves to a backward target that IsForwardJump then rejects.
            double t = TestCommandTimeJump.ResolveTargetUt(1000.0, null, "-50.0", out string err);
            Assert.Null(err);
            Assert.Equal(950.0, t, 6);
            Assert.False(TestCommandTimeJump.IsForwardJump(1000.0, t));
        }

        // ----- DecideJumpCompletion -----

        private const double Tol = TestCommandTimeJump.UtToleranceSeconds;
        private const double Budget = 120.0;

        [Fact]
        public void DecideJumpCompletion_ReachedAndSettled_CompleteOk()
        {
            Assert.Equal(JumpCompletionDecision.CompleteOk,
                TestCommandTimeJump.DecideJumpCompletion(1.0, 5000.0, 5000.0, Tol, 0, Budget));
        }

        [Fact]
        public void DecideJumpCompletion_ReachedNotSettled_StillWaiting()
        {
            Assert.Equal(JumpCompletionDecision.StillWaiting,
                TestCommandTimeJump.DecideJumpCompletion(1.0, 5000.0, 5000.0, Tol, 2, Budget));
        }

        [Fact]
        public void DecideJumpCompletion_NotReachedWithinBudget_StillWaiting()
        {
            Assert.Equal(JumpCompletionDecision.StillWaiting,
                TestCommandTimeJump.DecideJumpCompletion(1.0, 4000.0, 5000.0, Tol, 0, Budget));
        }

        [Fact]
        public void DecideJumpCompletion_ExactlyOnTolerance_Inclusive_CompleteOk()
        {
            // |current - target| == tolerance is inclusive (reached). Use exactly
            // representable values (current 2.0, target 0.0, tolerance 2.0) so the
            // diff is exactly the tolerance with no float rounding artifact.
            Assert.Equal(JumpCompletionDecision.CompleteOk,
                TestCommandTimeJump.DecideJumpCompletion(1.0, 2.0, 0.0, toleranceSeconds: 2.0, settleFramesRemaining: 0, budgetSeconds: Budget));
        }

        [Fact]
        public void DecideJumpCompletion_JustOverTolerance_NotReached_StillWaiting()
        {
            // Just outside the tolerance is not reached (the strict-greater boundary).
            Assert.Equal(JumpCompletionDecision.StillWaiting,
                TestCommandTimeJump.DecideJumpCompletion(1.0, 2.5, 0.0, toleranceSeconds: 2.0, settleFramesRemaining: 0, budgetSeconds: Budget));
        }

        [Fact]
        public void DecideJumpCompletion_BudgetExpiredNotReached_JumpTimeout()
        {
            Assert.Equal(JumpCompletionDecision.JumpTimeout,
                TestCommandTimeJump.DecideJumpCompletion(Budget, 4000.0, 5000.0, Tol, 0, Budget));
        }

        [Fact]
        public void DecideJumpCompletion_ReachedButSettlePendingPastBudget_JumpTimeout()
        {
            // Reached but the settle window never drained and the budget expired: timeout
            // wins so the head is not wedged.
            Assert.Equal(JumpCompletionDecision.JumpTimeout,
                TestCommandTimeJump.DecideJumpCompletion(Budget + 1.0, 5000.0, 5000.0, Tol, 3, Budget));
        }

        // ----- BuildCompletePayload -----

        [Fact]
        public void CompletePayload_CarriesUtTargetDelta_InvariantCulture()
        {
            var p = TestCommandTimeJump.BuildCompletePayload(reachedUT: 1600.0, targetUT: 1600.0, startUT: 1000.0);
            Assert.Equal(new[] { "ut", "target", "delta" }, p.Select(kv => kv.Key).ToArray());
            Assert.Equal("1600", Val(p, "ut"));
            Assert.Equal("1600", Val(p, "target"));
            Assert.Equal("600", Val(p, "delta"));
        }

        [Fact]
        public void CompletePayload_FractionalDelta_NoLocaleComma()
        {
            var p = TestCommandTimeJump.BuildCompletePayload(1000.5, 1000.5, 1000.0);
            Assert.DoesNotContain(",", Val(p, "delta"));
            Assert.Equal("0.5", Val(p, "delta"));
        }
    }
}
