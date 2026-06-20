using System.Collections.Generic;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for the pure, Unity-free decision behind the (provisional) auto-slow-warp-for-descent diagnostic:
    /// <see cref="DescentWarpControl.DescentWindowEndLiveUT"/> (live/recorded frame conversion),
    /// <see cref="DescentWarpControl.ComputeMaxWarpRate"/> (the distance-proportional cap), and
    /// <see cref="DescentWarpControl.SelectRateIndexForCap"/> (rate-level pick). The Unity seam / addons / per-frame
    /// dedup are exercised in-game.
    /// </summary>
    public class DescentWarpControlTests
    {
        // Real cross-frame UTs from the 2026-06-20 logs: triggerUT is LIVE (~3.96e9); deorbit/end are RECORDED (~2.57e9).
        private const double Trigger = 3962534821.2722712;
        private const double Deorbit = 2570542380.9910212;
        private const double RecEnd = 2570562894.315805;     // recorded descent-end (the dead-guard trap value)
        private const double Clip = RecEnd - Deorbit;          // 20513.33s

        // The standard stock KSP high-warp rate table.
        private static readonly float[] Levels = { 1f, 5f, 10f, 50f, 100f, 1000f, 10000f, 100000f, 1000000f };

        // === Live/recorded frame conversion (the 2026-06-20 dead-warp-control bug) ===========================

        [Fact]
        public void DescentWindowEndLive_ConvertsRecordedClipIntoLiveFrame()
        {
            double liveEnd = DescentWarpControl.DescentWindowEndLiveUT(Trigger, Deorbit, RecEnd);
            // Matches the DESCENT WINDOW log line's right bound (3962555334.597) within rounding.
            Assert.Equal(3962555334.597, liveEnd, 3);
            Assert.True(liveEnd > 3.9e9, "window end must be in the LIVE frame, not the recorded ~2.57e9 frame");
            Assert.Equal(Clip, liveEnd - Trigger, 6); // clip duration preserved exactly
        }

        [Fact]
        public void DescentWindowEndLive_NaN_PropagatesNaN()
        {
            Assert.True(double.IsNaN(DescentWarpControl.DescentWindowEndLiveUT(double.NaN, 1.0, 2.0)));
            Assert.True(double.IsNaN(DescentWarpControl.DescentWindowEndLiveUT(1.0, double.NaN, 2.0)));
            Assert.True(double.IsNaN(DescentWarpControl.DescentWindowEndLiveUT(1.0, 2.0, double.NaN)));
        }

        // === ComputeMaxWarpRate (the distance-proportional cap) ================================================

        [Fact]
        public void MaxWarpRate_PastTheDescent_NoCap()
        {
            double liveEnd = DescentWarpControl.DescentWindowEndLiveUT(Trigger, Deorbit, RecEnd);
            Assert.True(double.IsPositiveInfinity(
                DescentWarpControl.ComputeMaxWarpRate(liveEnd + 1.0, Trigger, liveEnd)));
        }

        [Fact]
        public void MaxWarpRate_NaN_NoCap()
        {
            Assert.True(double.IsPositiveInfinity(
                DescentWarpControl.ComputeMaxWarpRate(Trigger, double.NaN, Trigger + Clip)));
        }

        [Fact]
        public void MaxWarpRate_InsideWindow_WatchableCap()
        {
            double liveEnd = DescentWarpControl.DescentWindowEndLiveUT(Trigger, Deorbit, RecEnd);
            // Just inside the window: cap = clip / DescentWatchSeconds (so the clip plays over ~that many seconds).
            double cap = DescentWarpControl.ComputeMaxWarpRate(Trigger + 1.0, Trigger, liveEnd);
            Assert.Equal(Clip / DescentWarpControl.DescentWatchSeconds, cap, 6);
            // A frame at this cap advances cap*dt; even a generous dt is far below the clip, so it cannot be skipped.
            Assert.True(cap * 0.5 < Clip, "in-window cap must leave many frames inside the clip");
        }

        [Fact]
        public void MaxWarpRate_Approaching_TightensWithDistance()
        {
            double liveEnd = DescentWarpControl.DescentWindowEndLiveUT(Trigger, Deorbit, RecEnd);
            // 2,000,000 s out: cap = distance / WorstFrameSeconds = 2e6 / 2 = 1,000,000.
            double cap = DescentWarpControl.ComputeMaxWarpRate(Trigger - 2_000_000.0, Trigger, liveEnd);
            Assert.Equal(2_000_000.0 / DescentWarpControl.WorstFrameSeconds, cap, 3);
        }

        [Fact]
        public void MaxWarpRate_Approaching_CannotOvershoot_TheCoreInvariant()
        {
            // The property that defeats the lag spike: at the cap, a worst-case frame (WorstFrameSeconds long) advances
            // cap * WorstFrameSeconds <= the remaining distance, so it lands AT or before the window — never past.
            double liveEnd = DescentWarpControl.DescentWindowEndLiveUT(Trigger, Deorbit, RecEnd);
            foreach (double distance in new[] { 50.0, 500.0, 20_000.0, 500_000.0, 5_000_000.0 })
            {
                double cap = DescentWarpControl.ComputeMaxWarpRate(Trigger - distance, Trigger, liveEnd);
                Assert.True(cap * DescentWarpControl.WorstFrameSeconds <= distance + 1e-6,
                    $"a worst-case frame at the cap must not leap past the descent (distance={distance})");
            }
        }

        [Fact]
        public void MaxWarpRate_NeverBelowRealtime()
        {
            double liveEnd = DescentWarpControl.DescentWindowEndLiveUT(Trigger, Deorbit, RecEnd);
            // 1 s from the trigger: distance/WorstFrameSeconds < 1, floored to 1.0 (never below realtime).
            Assert.Equal(1.0, DescentWarpControl.ComputeMaxWarpRate(Trigger - 1.0, Trigger, liveEnd));
        }

        // === SelectRateIndexForCap (rate-level pick) ==========================================================

        [Theory]
        [InlineData(1_000_000.0, 8)] // exactly the top level
        [InlineData(950_000.0, 7)]   // below 1M -> 100k
        [InlineData(1000.0, 5)]      // exactly 1000x
        [InlineData(999.0, 4)]       // below 1000 -> 100
        [InlineData(20.0, 2)]        // below 50 -> 10
        [InlineData(0.5, 0)]         // below realtime -> 1x floor
        public void SelectRateIndex_PicksHighestLevelAtOrBelowCap(double cap, int expected)
        {
            Assert.Equal(expected, DescentWarpControl.SelectRateIndexForCap(cap, Levels));
        }

        [Fact]
        public void SelectRateIndex_InfinityOrEmpty_TopOrZero()
        {
            Assert.Equal(Levels.Length - 1, DescentWarpControl.SelectRateIndexForCap(double.PositiveInfinity, Levels));
            Assert.Equal(0, DescentWarpControl.SelectRateIndexForCap(1000.0, new List<float>()));
            Assert.Equal(0, DescentWarpControl.SelectRateIndexForCap(1000.0, null));
        }

        // === The 2026-06-20 regression: 1,000,000x straddles the sub-frame window ============================

        [Fact]
        public void RealApproachAt1Mx_CapsBelow1Mx_SoTheWindowCannotBeStraddled()
        {
            // The exact failure: the player warps at 1,000,000x toward the descent; one frame (≈20,000 s, or a
            // lag-spike ≈400,000 s) jumps the entire 20,513 s window between two resolver calls -> SKIPPED every cycle.
            double liveEnd = DescentWarpControl.DescentWindowEndLiveUT(Trigger, Deorbit, RecEnd);
            // 500,000 s out (well before the window): the cap is distance/WorstFrameSeconds = 250,000 -> index 7
            // (100,000x), already BELOW 1,000,000x, so the player is forced to decelerate long before a frame could
            // straddle the window.
            double cap = DescentWarpControl.ComputeMaxWarpRate(Trigger - 500_000.0, Trigger, liveEnd);
            int targetIdx = DescentWarpControl.SelectRateIndexForCap(cap, Levels);
            Assert.True(targetIdx < 8, "approaching at 1,000,000x must be capped DOWN before the window");
            Assert.Equal(7, targetIdx); // 250,000 -> 100,000x

            // Sanity: passing the RAW RECORDED end (the dead-guard bug) makes the past-the-descent branch fire (a live
            // currentUT is always >= the recorded end), so the cap would be +Infinity = no cap = the bug.
            Assert.True(double.IsPositiveInfinity(
                DescentWarpControl.ComputeMaxWarpRate(Trigger - 500_000.0, Trigger, RecEnd)));
        }
    }
}
