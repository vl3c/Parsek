using Parsek.Reaim;
using Xunit;
using Phase = Parsek.Reaim.DescentTrigger.DescentHeadPhase;
using Action = Parsek.Reaim.DescentWarpAction;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for <see cref="DescentWarpControl.DecideWarpAction"/> — the pure, Unity-free deceleration-ramp
    /// decision behind the (provisional) auto-slow-warp-for-descent diagnostic. The Unity seam / addons / per-frame
    /// and per-cycle dedup are exercised in-game.
    /// </summary>
    public class DescentWarpControlTests
    {
        private const double Trigger = 1000.0;
        private const double DescentEnd = 21513.0; // ~5.7h clip
        private const float Dt = 0.02f;
        private const int HiIdx = 7; // a high warp-rate index (so StepDown is allowed)

        [Fact]
        public void Descent_HighWarp_DropsToRealtime()
        {
            Assert.Equal(Action.DropToRealtime, DescentWarpControl.DecideWarpAction(
                Phase.Descent, 1100.0, Trigger, DescentEnd, 1000f, HiIdx, Dt, false));
        }

        [Fact]
        public void Descent_AtRealtime_None()
        {
            Assert.Equal(Action.None, DescentWarpControl.DecideWarpAction(
                Phase.Descent, 1100.0, Trigger, DescentEnd, 1f, 0, Dt, false));
        }

        [Fact]
        public void AtOrPastTrigger_Warping_DropsToRealtime()
        {
            // Loop clock already at/after the trigger but before descentEnd, still warping -> drop.
            Assert.Equal(Action.DropToRealtime, DescentWarpControl.DecideWarpAction(
                Phase.Loiter, 1500.0, Trigger, DescentEnd, 1000f, HiIdx, Dt, false));
        }

        [Fact]
        public void AlreadyDropped_None()
        {
            Assert.Equal(Action.None, DescentWarpControl.DecideWarpAction(
                Phase.Descent, 1100.0, Trigger, DescentEnd, 1000f, HiIdx, Dt, true));
        }

        [Fact]
        public void PastDescentEnd_None()
        {
            Assert.Equal(Action.None, DescentWarpControl.DecideWarpAction(
                Phase.Descent, DescentEnd + 100.0, Trigger, DescentEnd, 1000f, HiIdx, Dt, false));
        }

        [Fact]
        public void Loiter_NextStepUnderHalfTimeToTrigger_None()
        {
            // currentUT 500, rate 1000x, dt 0.02 -> step 20s; ttt 500; 20 <= 250 -> keep warping.
            Assert.Equal(Action.None, DescentWarpControl.DecideWarpAction(
                Phase.Loiter, 500.0, Trigger, DescentEnd, 1000f, HiIdx, Dt, false));
        }

        [Fact]
        public void Loiter_NextStepOverHalfTimeToTrigger_StepsDown()
        {
            // currentUT 970, rate 1000x, dt 0.02 -> step 20s; ttt 30; 20 > 15 -> decelerate one level.
            Assert.Equal(Action.StepDown, DescentWarpControl.DecideWarpAction(
                Phase.Loiter, 970.0, Trigger, DescentEnd, 1000f, HiIdx, Dt, false));
        }

        [Fact]
        public void Loiter_AlreadyAtLowestIndex_None()
        {
            // Even if the step would overshoot, index 0 cannot step down further (the at-trigger drop handles it).
            Assert.Equal(Action.None, DescentWarpControl.DecideWarpAction(
                Phase.Loiter, 970.0, Trigger, DescentEnd, 50f, 0, Dt, false));
        }

        [Fact]
        public void Loiter_AtRealtime_None()
        {
            Assert.Equal(Action.None, DescentWarpControl.DecideWarpAction(
                Phase.Loiter, 970.0, Trigger, DescentEnd, 1f, 0, Dt, false));
        }

        [Fact]
        public void ExtremeWarp_StepsDownInsteadOfSteppingOver_TheActualFailedFrame()
        {
            // The real 2026-06-20 17:31 frame that the old single-shot check stepped over: at 1,000,000x the loiter
            // was ~2 frames wide and the next frame leapt past the whole window. With the ramp the SAME frame
            // (ttt ~31266s, step at 1Mx/0.02 = 20000s > 15633) decelerates instead of overshooting.
            const double trig = 3883978907.88, dEnd = 3883999421.21, cur = 3883947641.95;
            Assert.Equal(Action.StepDown, DescentWarpControl.DecideWarpAction(
                Phase.Loiter, cur, trig, dEnd, 1_000_000f, HiIdx, Dt, false));
            // And a frame-time hiccup (dt 0.05, the implied real step ~50000s) still decelerates, not overshoots.
            Assert.Equal(Action.StepDown, DescentWarpControl.DecideWarpAction(
                Phase.Loiter, cur, trig, dEnd, 1_000_000f, HiIdx, 0.05f, false));
        }

        [Fact]
        public void NaNTiming_None()
        {
            Assert.Equal(Action.None, DescentWarpControl.DecideWarpAction(
                Phase.Descent, 1100.0, double.NaN, DescentEnd, 1000f, HiIdx, Dt, false));
            Assert.Equal(Action.None, DescentWarpControl.DecideWarpAction(
                Phase.Loiter, 970.0, Trigger, double.NaN, 1000f, HiIdx, Dt, false));
        }

        // === Live/recorded frame conversion (the 2026-06-20 dead-warp-control bug) ===========================

        [Fact]
        public void DescentWindowEndLive_ConvertsRecordedClipIntoLiveFrame()
        {
            // Real values from the 2026-06-20 18:00 log: triggerUT is LIVE (~3.96e9), deorbit/end are RECORDED
            // (~2.57e9). The live window end must be triggerUT + the recorded clip, NOT the raw recorded end.
            const double trig = 3962534821.2722712, deorbit = 2570542380.9910212, recEnd = 2570562894.315805;
            double liveEnd = DescentWarpControl.DescentWindowEndLiveUT(trig, deorbit, recEnd);
            // Matches the DESCENT WINDOW log line's right bound (3962555334.597) within rounding.
            Assert.Equal(3962555334.597, liveEnd, 3);
            Assert.True(liveEnd > 3.9e9, "window end must be in the LIVE frame, not the recorded ~2.57e9 frame");
            // The clip duration is preserved exactly.
            Assert.Equal(recEnd - deorbit, liveEnd - trig, 6);
        }

        [Fact]
        public void DescentWindowEndLive_NaN_PropagatesNaN()
        {
            Assert.True(double.IsNaN(DescentWarpControl.DescentWindowEndLiveUT(double.NaN, 1.0, 2.0)));
            Assert.True(double.IsNaN(DescentWarpControl.DescentWindowEndLiveUT(1.0, double.NaN, 2.0)));
            Assert.True(double.IsNaN(DescentWarpControl.DescentWindowEndLiveUT(1.0, 2.0, double.NaN)));
        }

        [Fact]
        public void RealCrossFrameApproach_StillDecelerates_RegressionForDeadGuard()
        {
            // The exact failure the dead guard caused: a LIVE currentUT inside the loiter, approaching a LIVE
            // trigger at high warp, with the descent window end computed from the RECORDED clip. The OLD code
            // compared the live currentUT against the raw recorded descentEndUT (~2.57e9), so currentUT >= recEnd
            // was always true -> None on every frame (the warp control never acted; auto-drop=0 all session).
            // With the live-frame window end the SAME frame correctly decelerates.
            const double trig = 3962534821.2722712, deorbit = 2570542380.9910212, recEnd = 2570562894.315805;
            double liveEnd = DescentWarpControl.DescentWindowEndLiveUT(trig, deorbit, recEnd);
            const double cur = 3962518000.0; // live, ~16821s before the trigger; at 1Mx one frame would overshoot
            Assert.Equal(Action.StepDown, DescentWarpControl.DecideWarpAction(
                Phase.Loiter, cur, trig, liveEnd, 1_000_000f, HiIdx, Dt, false));

            // Sanity: feeding the RAW RECORDED end (the bug) makes the past-the-descent guard fire -> None.
            Assert.Equal(Action.None, DescentWarpControl.DecideWarpAction(
                Phase.Loiter, cur, trig, recEnd, 1_000_000f, HiIdx, Dt, false));
        }
    }
}
