using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for <see cref="TestCommandWarpToUT"/>, the decision core of the
    /// REAL rails-warp seam verb (RF-12 / re-fly phase 4).
    ///
    /// <para>The cells are shaped around the four things the verb can get wrong in a
    /// way no log line would reveal: it can accept a target it should refuse; it can
    /// pick a rate that overshoots; it can call a warp complete while the game is still
    /// warped; and it can hang instead of timing out. Each has its own cell, and the
    /// warp-still-raised case has TWO (the success predicate refuses it, and the
    /// timeout predicate still fires) because "never leave the game warped" is the
    /// mirror-direction obligation the design took on.</para>
    /// </summary>
    public class TestCommandWarpToUTTests
    {
        // The stock Kerbin-era rails ladder, verbatim in shape (8 entries, 1x .. 100000x).
        private static readonly float[] StockRates =
            { 1f, 5f, 10f, 50f, 100f, 1000f, 10000f, 100000f };

        private const int TopIndex = 7;

        // ----- ResolveTargetUt: parse -----

        [Fact]
        public void ResolveTargetUt_ParsesAnInvariantCultureAbsoluteTarget()
        {
            double target = TestCommandWarpToUT.ResolveTargetUt("1234.5", out string error);
            Assert.Null(error);
            Assert.Equal(1234.5, target, 9);
        }

        [Fact]
        public void ResolveTargetUt_AbsentIsMissingTarget()
        {
            double target = TestCommandWarpToUT.ResolveTargetUt(null, out string error);
            Assert.Equal(TestCommandWarpToUT.MissingTargetReason, error);
            Assert.True(double.IsNaN(target));

            TestCommandWarpToUT.ResolveTargetUt("", out string emptyError);
            Assert.Equal(TestCommandWarpToUT.MissingTargetReason, emptyError);
        }

        [Fact]
        public void ResolveTargetUt_LocaleCommaIsRefusedNotSilentlyReinterpreted()
        {
            // The InvariantCulture rule the whole seam is built on: `600,0` is what a
            // ro-RO / de-DE author's copy-paste produces, and parsing it as 6000 would be
            // a silently WRONG warp target rather than a refusal.
            TestCommandWarpToUT.ResolveTargetUt("600,0", out string error);
            Assert.Equal(TestCommandWarpToUT.MissingTargetReason, error);
        }

        [Fact]
        public void ResolveTargetUt_IsInvariantUnderAHostileThreadCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                // de-DE reads "." as a group separator. Pinning the culture PROVES the
                // parse is invariant rather than making a culture-dependent site pass.
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                double target = TestCommandWarpToUT.ResolveTargetUt("1234.5", out string error);
                Assert.Null(error);
                Assert.Equal(1234.5, target, 9);

                List<KeyValuePair<string, string>> payload =
                    TestCommandWarpToUT.BuildCompletePayload(200.5, 200.0, 100.0, 50.0);
                Assert.Equal("200.5", payload.Single(kv => kv.Key == "ut").Value);
                Assert.Equal("100", payload.Single(kv => kv.Key == "delta").Value);
                Assert.Equal("50", payload.Single(kv => kv.Key == "maxRate").Value);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Theory]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("1e300")]
        public void ResolveTargetUt_NonFiniteOrAbsurdIsOutOfRange(string raw)
        {
            TestCommandWarpToUT.ResolveTargetUt(raw, out string error);
            Assert.Equal(TestCommandWarpToUT.TargetOutOfRangeReason, error);
        }

        // ----- The forward gate: a target in the past is a refusal -----

        [Fact]
        public void IsForwardWarp_RefusesAPastOrEqualTarget()
        {
            Assert.False(TestCommandWarpToUT.IsForwardWarp(500.0, 499.9));
            Assert.False(TestCommandWarpToUT.IsForwardWarp(500.0, 500.0));
            Assert.True(TestCommandWarpToUT.IsForwardWarp(500.0, 500.1));
        }

        // ----- ResolveMaxRate -----

        [Fact]
        public void ResolveMaxRate_AbsentIsUncapped()
        {
            double cap = TestCommandWarpToUT.ResolveMaxRate(null, out string error);
            Assert.Null(error);
            Assert.Equal(TestCommandWarpToUT.UncappedMaxRate, cap);
        }

        [Fact]
        public void ResolveMaxRate_ParsesAValidCap()
        {
            double cap = TestCommandWarpToUT.ResolveMaxRate("100", out string error);
            Assert.Null(error);
            Assert.Equal(100.0, cap, 9);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("0.5")]
        [InlineData("-10")]
        [InlineData("fast")]
        [InlineData("NaN")]
        public void ResolveMaxRate_FailsClosedOnAnythingBelowOneOrUnparseable(string raw)
        {
            // Fail-closed, not ignored: a mis-typed cap must never read as an uncapped
            // warp, which is the direction that would silently blow past a lane's intent.
            TestCommandWarpToUT.ResolveMaxRate(raw, out string error);
            Assert.Equal(TestCommandWarpToUT.MaxRateInvalidReason, error);
        }

        // ----- Feasibility -----

        [Fact]
        public void EvaluateFeasibility_OrdersControllerBeforeLock_AndPassesWhenBothAreFine()
        {
            Assert.Equal(TestCommandWarpToUT.WarpUnavailableReason,
                TestCommandWarpToUT.EvaluateFeasibility(warpControllerPresent: false, warpLocked: true));
            Assert.Equal(TestCommandWarpToUT.WarpLockedReason,
                TestCommandWarpToUT.EvaluateFeasibility(warpControllerPresent: true, warpLocked: true));
            Assert.Null(
                TestCommandWarpToUT.EvaluateFeasibility(warpControllerPresent: true, warpLocked: false));
        }

        // ----- The rate ladder -----

        [Fact]
        public void SelectRateIndex_PicksTheHighestRateThatStillRunsForTheMinimumWindow()
        {
            // 100000x needs 250000 s of remaining span; 10000x needs 25000 s.
            Assert.Equal(TopIndex,
                TestCommandWarpToUT.SelectRateIndex(300000.0, StockRates, TopIndex,
                    TestCommandWarpToUT.UncappedMaxRate));
            Assert.Equal(6,
                TestCommandWarpToUT.SelectRateIndex(30000.0, StockRates, TopIndex,
                    TestCommandWarpToUT.UncappedMaxRate));
            // 1000x needs 2500 s.
            Assert.Equal(5,
                TestCommandWarpToUT.SelectRateIndex(2500.0, StockRates, TopIndex,
                    TestCommandWarpToUT.UncappedMaxRate));

            // THE SPAN THAT ACTUALLY DISCRIMINATES THE FACTOR, added after the review
            // panel mutation-tested this cell and found the three spans above answer the
            // SAME index with or without the `* MinRealSecondsAtRate` - so the cell's own
            // name promised coverage only the ladder-walk cell supplied. 2499 s is one
            // second short of 1000x's window: with the factor the answer steps down to
            // 100x, without it 1000x still qualifies.
            Assert.Equal(4,
                TestCommandWarpToUT.SelectRateIndex(2499.0, StockRates, TopIndex,
                    TestCommandWarpToUT.UncappedMaxRate));
        }

        [Fact]
        public void SelectRateIndex_WalksDownTheLadderAsTheTargetNears_AndReachesOneX()
        {
            // This is the mirror-direction property in its purest form: the SAME selector
            // that raised the warp lowers it, purely as a function of the shrinking span.
            // Each span is exactly `rate * MinRealSecondsAtRate` for one rung, so the walk
            // steps down the whole ladder one rung at a time and lands on 1x.
            int[] observed = new[] { 300000.0, 30000.0, 2500.0, 250.0, 125.0, 25.0, 12.5, 5.0, 0.0 }
                .Select(rem => TestCommandWarpToUT.SelectRateIndex(
                    rem, StockRates, TopIndex, TestCommandWarpToUT.UncappedMaxRate))
                .ToArray();

            Assert.Equal(new[] { 7, 6, 5, 4, 3, 2, 1, 0, 0 }, observed);
            // Monotone non-increasing: no step of the approach ever speeds back up.
            for (int i = 1; i < observed.Length; i++)
                Assert.True(observed[i] <= observed[i - 1],
                    $"ladder rose at step {i}: {observed[i - 1]} -> {observed[i]}");
        }

        [Fact]
        public void SelectRateIndex_HonoursTheAltitudeCeilingAndTheMaxRateCap()
        {
            // A ceiling of 2 (stock's answer below a body's rails altitude limits) pins the
            // selection at 10x no matter how much span remains.
            Assert.Equal(2,
                TestCommandWarpToUT.SelectRateIndex(1e9, StockRates, 2,
                    TestCommandWarpToUT.UncappedMaxRate));
            // A ceiling of 0 is a hard 1x - the atmospheric-descent case the RF-12W lane
            // is built around, where the warp degrades into a real-time wait.
            Assert.Equal(0,
                TestCommandWarpToUT.SelectRateIndex(1e9, StockRates, 0,
                    TestCommandWarpToUT.UncappedMaxRate));
            // The caller's cap bites independently of the ceiling.
            Assert.Equal(3, TestCommandWarpToUT.SelectRateIndex(1e9, StockRates, TopIndex, 50.0));
            Assert.Equal(0, TestCommandWarpToUT.SelectRateIndex(1e9, StockRates, TopIndex, 1.0));
        }

        [Fact]
        public void SelectRateIndex_WorksOnThePhysicsLadderToo_WhoseRungsAreNotTheRailsOnes()
        {
            // MEASURED on RF-12W's reading run 2: inside the atmosphere the log read
            // `rateIndex=3 rate=4`, because stock was in PHYSICS warp
            // (TimeWarp.Modes.LOW) whose separate physicsWarpRates array shares the index
            // space with the rails one and has completely different values - rails index 3
            // is 50x, physics index 3 is 4x. The selector must be a pure function of the
            // rungs it is HANDED, never of a rails array it looks up itself, or a lane
            // inside an atmosphere is sized against rates it will never get.
            float[] physics = { 1f, 2f, 3f, 4f };
            int physTop = physics.Length - 1;

            // 4x needs 10 s of span; 3x needs 7.5; 2x needs 5.
            Assert.Equal(3, TestCommandWarpToUT.SelectRateIndex(
                1000.0, physics, physTop, TestCommandWarpToUT.UncappedMaxRate));
            Assert.Equal(3, TestCommandWarpToUT.SelectRateIndex(
                10.0, physics, physTop, TestCommandWarpToUT.UncappedMaxRate));
            Assert.Equal(2, TestCommandWarpToUT.SelectRateIndex(
                9.9, physics, physTop, TestCommandWarpToUT.UncappedMaxRate));
            Assert.Equal(1, TestCommandWarpToUT.SelectRateIndex(
                5.0, physics, physTop, TestCommandWarpToUT.UncappedMaxRate));
            Assert.Equal(0, TestCommandWarpToUT.SelectRateIndex(
                4.9, physics, physTop, TestCommandWarpToUT.UncappedMaxRate));

            // The SAME span picks a DIFFERENT rung on the two ladders, which is exactly
            // why the ladder is a parameter rather than a lookup: 30 s of remaining span
            // takes rails rung 2 (10x, needing 25 s) and physics rung 3 (4x, needing 10 s).
            // Reading the rails array while stock is in physics warp would therefore both
            // over-rate the rung and step down at the wrong distance.
            float[] rails = { 1f, 5f, 10f, 50f, 100f, 1000f, 10000f, 100000f };
            Assert.Equal(2, TestCommandWarpToUT.SelectRateIndex(
                30.0, rails, rails.Length - 1, TestCommandWarpToUT.UncappedMaxRate));
            Assert.Equal(3, TestCommandWarpToUT.SelectRateIndex(
                30.0, physics, physTop, TestCommandWarpToUT.UncappedMaxRate));
        }

        [Fact]
        public void SelectRateIndex_ClampsAnOutOfRangeCeilingRatherThanIndexingPastTheLadder()
        {
            // The UPPER clamp is the one that matters: without it a stale ceiling indexes
            // past the array and throws. Removing the clause reds exactly here.
            Assert.Equal(TopIndex,
                TestCommandWarpToUT.SelectRateIndex(1e9, StockRates, 99,
                    TestCommandWarpToUT.UncappedMaxRate));
            // A NEGATIVE ceiling answers 1x, and this cell claims nothing about HOW. The
            // review panel mutation-tested the lower clamp that used to sit beside the
            // upper one and found it dead - the descending loop runs zero times either
            // way - so the clause is gone and this assertion pins the OUTPUT, which is the
            // only thing that was ever observable.
            Assert.Equal(0,
                TestCommandWarpToUT.SelectRateIndex(1e9, StockRates, -5,
                    TestCommandWarpToUT.UncappedMaxRate));
        }

        [Fact]
        public void SelectRateIndex_MissingOrEmptyLadderAndNonPositiveSpanAreOneX()
        {
            Assert.Equal(0, TestCommandWarpToUT.SelectRateIndex(1e9, null, TopIndex, 0.0));
            Assert.Equal(0, TestCommandWarpToUT.SelectRateIndex(1e9, new float[0], TopIndex, 0.0));
            Assert.Equal(0, TestCommandWarpToUT.SelectRateIndex(0.0, StockRates, TopIndex, 0.0));
            Assert.Equal(0, TestCommandWarpToUT.SelectRateIndex(-50.0, StockRates, TopIndex, 0.0));
            Assert.Equal(0, TestCommandWarpToUT.SelectRateIndex(double.NaN, StockRates, TopIndex, 0.0));
        }

        // ----- HasReached -----

        [Fact]
        public void HasReached_IsOneSidedSoAnOvershootStaysLatched()
        {
            double target = 1000.0;
            double tol = TestCommandWarpToUT.UtToleranceSeconds;
            Assert.False(TestCommandWarpToUT.HasReached(999.0, target, tol));
            Assert.True(TestCommandWarpToUT.HasReached(target - tol, target, tol));
            Assert.True(TestCommandWarpToUT.HasReached(target, target, tol));
            // The whole reason the latch is one-sided: warp keeps the clock running well
            // past the target while the rate winds down.
            Assert.True(TestCommandWarpToUT.HasReached(target + 500.0, target, tol));
        }

        // ----- The completion predicate -----

        [Fact]
        public void DecideWarpCompletion_CompletesOnlyWhenReached_Unwarped_AndSettled()
        {
            Assert.Equal(WarpCompletionDecision.CompleteOk,
                TestCommandWarpToUT.DecideWarpCompletion(
                    elapsedSeconds: 10.0, currentUT: 1000.0, targetUT: 1000.0,
                    toleranceSeconds: TestCommandWarpToUT.UtToleranceSeconds,
                    currentRateIndex: 0, settleFramesRemaining: 0, budgetSeconds: 300.0));
        }

        [Fact]
        public void DecideWarpCompletion_StillWaitingWhileTheGameIsStillWarped()
        {
            // THE CELL THAT MATTERS MOST. The clock has landed and the settle has drained,
            // but warp is still at index 3. Completing here would hand a WARPED game to the
            // next step (an in-game batch, an ExitToSpaceCenter), which is exactly what the
            // "never leave the game warped" obligation forbids. Mutating `currentRateIndex
            // <= 0` out of the success condition reds here and nowhere else.
            Assert.Equal(WarpCompletionDecision.StillWaiting,
                TestCommandWarpToUT.DecideWarpCompletion(
                    elapsedSeconds: 10.0, currentUT: 1200.0, targetUT: 1000.0,
                    toleranceSeconds: TestCommandWarpToUT.UtToleranceSeconds,
                    currentRateIndex: 3, settleFramesRemaining: 0, budgetSeconds: 300.0));
        }

        [Fact]
        public void DecideWarpCompletion_StillWaitingWhileTheSettleWindowHasNotDrained()
        {
            Assert.Equal(WarpCompletionDecision.StillWaiting,
                TestCommandWarpToUT.DecideWarpCompletion(
                    elapsedSeconds: 10.0, currentUT: 1000.0, targetUT: 1000.0,
                    toleranceSeconds: TestCommandWarpToUT.UtToleranceSeconds,
                    currentRateIndex: 0, settleFramesRemaining: 2, budgetSeconds: 300.0));
        }

        [Fact]
        public void DecideWarpCompletion_StillWaitingWhileTheClockHasNotArrived()
        {
            Assert.Equal(WarpCompletionDecision.StillWaiting,
                TestCommandWarpToUT.DecideWarpCompletion(
                    elapsedSeconds: 10.0, currentUT: 400.0, targetUT: 1000.0,
                    toleranceSeconds: TestCommandWarpToUT.UtToleranceSeconds,
                    currentRateIndex: 0, settleFramesRemaining: 0, budgetSeconds: 300.0));
        }

        // ----- The timeout -----

        [Fact]
        public void DecideWarpCompletion_TimesOutWhenTheBudgetExpiresShortOfTheTarget()
        {
            // The clamped-to-1x case: a span stock will not let the ladder cover inside the
            // budget must produce a bounded ERROR, never an unbounded hold on the FIFO head.
            Assert.Equal(WarpCompletionDecision.WarpTimeout,
                TestCommandWarpToUT.DecideWarpCompletion(
                    elapsedSeconds: 300.0, currentUT: 400.0, targetUT: 1000.0,
                    toleranceSeconds: TestCommandWarpToUT.UtToleranceSeconds,
                    currentRateIndex: 0, settleFramesRemaining: 0, budgetSeconds: 300.0));
        }

        [Fact]
        public void DecideWarpCompletion_TimesOutEvenWhenTheClockArrivedButWarpNeverCameDown()
        {
            // The stuck-warp shape: the target is reached but the rate never returns to 0.
            // The verb must NOT hold forever - it must produce the ERROR whose applier path
            // forces rate 0 before the terminal.
            Assert.Equal(WarpCompletionDecision.WarpTimeout,
                TestCommandWarpToUT.DecideWarpCompletion(
                    elapsedSeconds: 301.0, currentUT: 5000.0, targetUT: 1000.0,
                    toleranceSeconds: TestCommandWarpToUT.UtToleranceSeconds,
                    currentRateIndex: 5, settleFramesRemaining: 0, budgetSeconds: 300.0));
        }

        [Fact]
        public void DecideWarpCompletion_SuccessWinsOverAnExactlyExpiredBudget()
        {
            // Ordering matters: a warp that landed and un-warped on the very frame the
            // budget expired is a SUCCESS, not a timeout.
            Assert.Equal(WarpCompletionDecision.CompleteOk,
                TestCommandWarpToUT.DecideWarpCompletion(
                    elapsedSeconds: 300.0, currentUT: 1000.0, targetUT: 1000.0,
                    toleranceSeconds: TestCommandWarpToUT.UtToleranceSeconds,
                    currentRateIndex: 0, settleFramesRemaining: 0, budgetSeconds: 300.0));
        }

        // ----- Payload -----

        [Fact]
        public void BuildCompletePayload_CarriesReachedTargetDeltaAndMaxRate()
        {
            List<KeyValuePair<string, string>> payload =
                TestCommandWarpToUT.BuildCompletePayload(
                    reachedUT: 1000.25, targetUT: 1000.0, startUT: 400.0, maxObservedRate: 1000.0);

            Assert.Equal(new[] { "ut", "target", "delta", "maxRate" },
                payload.Select(kv => kv.Key).ToArray());
            Assert.Equal("1000.25", payload[0].Value);
            Assert.Equal("1000", payload[1].Value);
            Assert.Equal("600", payload[2].Value);
            Assert.Equal("1000", payload[3].Value);
        }

        [Fact]
        public void BuildCompletePayload_MaxRateOfOneIsHowAReaderTellsARealTimeWaitApart()
        {
            // A fully-clamped warp still completes; `maxRate=1` is the only signal in the
            // terminal that the span was waited out rather than warped through.
            List<KeyValuePair<string, string>> payload =
                TestCommandWarpToUT.BuildCompletePayload(300.0, 300.0, 100.0, 1.0);
            Assert.Equal("1", payload.Single(kv => kv.Key == "maxRate").Value);
        }

        // ----- The refusal vocabulary is distinct -----

        [Fact]
        public void RefusalReasons_AreDistinctTokens()
        {
            string[] reasons =
            {
                TestCommandWarpToUT.MissingTargetReason,
                TestCommandWarpToUT.TargetOutOfRangeReason,
                TestCommandWarpToUT.BackwardWarpReason,
                TestCommandWarpToUT.MaxRateInvalidReason,
                TestCommandWarpToUT.WarpUnavailableReason,
                TestCommandWarpToUT.WarpLockedReason,
                TestCommandWarpToUT.WarpTimeoutReason,
            };
            Assert.Equal(reasons.Length, reasons.Distinct().Count());
            // A reason token reaches the wire as the response `msg`, which the harness
            // splits on whitespace: a token with a space in it would be truncated.
            Assert.All(reasons, r => Assert.DoesNotContain(" ", r));
        }
    }
}
