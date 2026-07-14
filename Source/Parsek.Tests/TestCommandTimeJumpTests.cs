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
            // ResolveTargetUt is parse-only for the sign; the forward gate is IsForwardJump.
            // A negative delta resolves to a backward (but in-range) target that
            // IsForwardJump then rejects.
            double t = TestCommandTimeJump.ResolveTargetUt(1000.0, null, "-50.0", out string err);
            Assert.Null(err);
            Assert.Equal(950.0, t, 6);
            Assert.False(TestCommandTimeJump.IsForwardJump(1000.0, t));
        }

        [Fact]
        public void ResolveTargetUt_AbsurdlyLargeUt_OutOfRange()
        {
            // A 1e308 absolute target must NEVER reach ExecuteJump: rejected as out-of-range,
            // not admitted as a finite (huge) UT.
            TestCommandTimeJump.ResolveTargetUt(1000.0, "1e308", null, out string err);
            Assert.Equal("target-out-of-range", err);
        }

        [Fact]
        public void ResolveTargetUt_DeltaOverflowsToInfinity_OutOfRange()
        {
            // now + delta overflows to +Infinity (both parse finite, their sum does not):
            // the non-finite resolved target is rejected before it can reach ExecuteJump.
            TestCommandTimeJump.ResolveTargetUt(1e308, null, "1e308", out string err);
            Assert.Equal("target-out-of-range", err);
        }

        [Fact]
        public void ResolveTargetUt_NaNTarget_OutOfRange()
        {
            TestCommandTimeJump.ResolveTargetUt(1000.0, "NaN", null, out string err);
            Assert.Equal("target-out-of-range", err);
        }

        [Fact]
        public void ResolveTargetUt_JustInsideBound_Resolves()
        {
            // A generous-but-finite target inside the bound still resolves cleanly.
            double t = TestCommandTimeJump.ResolveTargetUt(0.0, "1000000000.0", null, out string err);
            Assert.Null(err);
            Assert.Equal(1_000_000_000.0, t, 3);
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
        public void DecideJumpCompletion_ExactlyOnTargetMinusTolerance_Inclusive_CompleteOk()
        {
            // current == target - tolerance is inclusive (reached). Use exactly representable
            // values (current 0.0, target 2.0, tolerance 2.0) so current is exactly
            // target - tolerance with no float rounding artifact.
            Assert.Equal(JumpCompletionDecision.CompleteOk,
                TestCommandTimeJump.DecideJumpCompletion(1.0, 0.0, 2.0, toleranceSeconds: 2.0, settleFramesRemaining: 0, budgetSeconds: Budget));
        }

        [Fact]
        public void DecideJumpCompletion_ClockOvershotTarget_StaysReached_CompleteOk()
        {
            // The one-sided latch: once the clock reaches or PASSES the target it stays
            // reached (the live clock keeps advancing after the epoch-shift lands). A current
            // UT well ABOVE the target is reached, not "past tolerance and lost".
            Assert.Equal(JumpCompletionDecision.CompleteOk,
                TestCommandTimeJump.DecideJumpCompletion(1.0, 5000.5, 5000.0, Tol, 0, Budget));
        }

        [Fact]
        public void DecideJumpCompletion_JustBelowTargetMinusTolerance_NotReached_StillWaiting()
        {
            // Just below target - tolerance is not reached (the clock has not arrived yet).
            Assert.Equal(JumpCompletionDecision.StillWaiting,
                TestCommandTimeJump.DecideJumpCompletion(1.0, -0.5, 2.0, toleranceSeconds: 2.0, settleFramesRemaining: 0, budgetSeconds: Budget));
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

        // ----- Moving-clock deciders (the regression the frozen-clock tests missed) -----
        // The shipped tests all fed a FROZEN currentUT, so the two-sided
        // Abs(current-target)<=tol bug (the clock advances past target during the settle
        // drain, so reached went false and every live jump timed out) never surfaced. These
        // drive the decider across polls with an ADVANCING clock, exactly as the addon does.

        // Model one poll of TryCompleteTimeJump: it decrements the settle counter FIRST, then
        // reads the (now advanced) clock and decides. Returns the decision; mutates the
        // running settle counter through the ref.
        private static JumpCompletionDecision PollOnce(
            double elapsed, double currentUt, double target, ref int settleRemaining, double budget)
        {
            if (settleRemaining > 0) settleRemaining--;
            return TestCommandTimeJump.DecideJumpCompletion(
                elapsed, currentUt, target, Tol, settleRemaining, Budget);
        }

        [Fact]
        public void DecideJumpCompletion_ClockAdvancesPastTargetAcrossPolls_CompleteOk()
        {
            // The clock crosses the target and keeps receding while the settle window drains.
            // The one-sided latch must still land CompleteOk (not fall through to
            // JumpTimeout). target=5000; clock starts just below and advances 0.05s/frame so
            // by the time the 3-frame settle drains it is ~50ms PAST the target.
            const double target = 5000.0;
            int settle = TestCommandTimeJump.SettleFrames; // 3
            JumpCompletionDecision decision = JumpCompletionDecision.StillWaiting;
            double clock = 4999.98;
            for (int frame = 0; frame < 200 && decision == JumpCompletionDecision.StillWaiting; frame++)
            {
                double elapsed = frame * 0.02;
                decision = PollOnce(elapsed, clock, target, ref settle, Budget);
                clock += 0.05; // live clock keeps ticking, never freezes at target
            }
            Assert.Equal(JumpCompletionDecision.CompleteOk, decision);
        }

        [Fact]
        public void DecideJumpCompletion_ClockNeverReachesTarget_JumpTimeout()
        {
            // A pathological non-landing jump: the clock never reaches the target across the
            // whole budget window. The decider must eventually time out (never a false OK).
            const double target = 5000.0;
            int settle = TestCommandTimeJump.SettleFrames;
            JumpCompletionDecision decision = JumpCompletionDecision.StillWaiting;
            const double clock = 4000.0; // stuck well below target for the whole run
            int frame = 0;
            // Step elapsed past the budget in ~coarse frames; assert it is JumpTimeout.
            for (; frame < 100000 && decision == JumpCompletionDecision.StillWaiting; frame++)
            {
                double elapsed = frame * 0.02;
                decision = PollOnce(elapsed, clock, target, ref settle, Budget);
            }
            Assert.Equal(JumpCompletionDecision.JumpTimeout, decision);
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
