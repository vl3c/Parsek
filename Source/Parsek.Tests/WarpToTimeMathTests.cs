using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class WarpToTimeMathTests : System.IDisposable
    {
        public WarpToTimeMathTests()
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekTimeFormat.ResetForTesting();
        }

        // ── ComputeTargetUT (Kerbin: 6h days = 21600s, 426d years) ──

        [Theory]
        [InlineData(1, 1, 0, 0, 0.0)]                 // game start
        [InlineData(0, 0, 0, 0, 0.0)]                 // 0/0 floors to Year 1 / Day 1 = start
        [InlineData(1, 2, 0, 0, 21600.0)]             // +1 Kerbin day
        [InlineData(2, 1, 0, 0, 9201600.0)]           // +1 Kerbin year (426*21600)
        [InlineData(1, 1, 1, 0, 3600.0)]              // +1 hour
        [InlineData(1, 1, 0, 1, 60.0)]                // +1 minute
        [InlineData(1, 1, 2, 30, 9000.0)]             // 2h30m
        [InlineData(-5, -5, -5, -5, 0.0)]             // negatives floor (Y/D->1, H/M->0)
        public void ComputeTargetUT_Kerbin(int y, int d, int h, int m, double expected)
        {
            Assert.Equal(expected, WarpToTimeMath.ComputeTargetUT(y, d, h, m), 3);
        }

        [Theory]
        [InlineData(1, 1, 0, 0, 0.0)]
        [InlineData(1, 2, 0, 0, 86400.0)]             // +1 Earth day (24h)
        [InlineData(2, 1, 0, 0, 31536000.0)]          // +1 Earth year (365*86400)
        public void ComputeTargetUT_Earth(int y, int d, int h, int m, double expected)
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = false;
            Assert.Equal(expected, WarpToTimeMath.ComputeTargetUT(y, d, h, m), 3);
        }

        [Fact]
        public void ComputeTargetUT_HourMinuteOverflow_RollsOver()
        {
            // Hour 6 on Kerbin = +1 day (no upper clamp at compute time).
            Assert.Equal(21600.0, WarpToTimeMath.ComputeTargetUT(1, 1, 6, 0), 3);
            Assert.Equal(3600.0, WarpToTimeMath.ComputeTargetUT(1, 1, 0, 60), 3);
        }

        // ── TryParseField ──

        // Field kind passed as a string (the enum is internal, so it cannot be a public
        // [Theory] parameter type) and mapped to the enum inside the test.
        [Theory]
        [InlineData("Year", "5", true, 5)]
        [InlineData("Year", "0", true, 1)]      // Year floors at 1
        [InlineData("Year", "-3", true, 1)]
        [InlineData("Day", "0", true, 1)]       // Day floors at 1
        [InlineData("Hour", "0", true, 0)]      // Hour floors at 0
        [InlineData("Hour", "-2", true, 0)]
        [InlineData("Minute", "  7 ", true, 7)] // trims
        [InlineData("Year", "", false, 1)]      // empty -> reject (value=floor)
        [InlineData("Year", "abc", false, 1)]   // garbage -> reject
        [InlineData("Hour", "1.5", false, 0)]   // non-integer -> reject
        public void TryParseField_Cases(string fieldName, string draft, bool ok, int expected)
        {
            var kind = MapKind(fieldName);
            bool result = WarpToTimeMath.TryParseField(kind, draft, out int value);
            Assert.Equal(ok, result);
            Assert.Equal(expected, value);
        }

        private static WarpToTimeMath.WarpFieldKind MapKind(string name)
        {
            switch (name)
            {
                case "Day": return WarpToTimeMath.WarpFieldKind.Day;
                case "Hour": return WarpToTimeMath.WarpFieldKind.Hour;
                case "Minute": return WarpToTimeMath.WarpFieldKind.Minute;
                default: return WarpToTimeMath.WarpFieldKind.Year;
            }
        }

        // ── DecideWarpPlan ──

        [Fact]
        public void DecideWarpPlan_FutureTarget_ForwardOnly()
        {
            var plan = WarpToTimeMath.DecideWarpPlan(1000, 100, inFlight: false,
                hasRewindTarget: false, landsAtTimelineStart: false);
            Assert.Equal(WarpToTimeMath.WarpPlanKind.ForwardOnly, plan.Kind);
            Assert.False(plan.RequiresFlightExit);
        }

        [Fact]
        public void DecideWarpPlan_FutureTarget_InFlight_RequiresExit()
        {
            var plan = WarpToTimeMath.DecideWarpPlan(1000, 100, inFlight: true,
                hasRewindTarget: false, landsAtTimelineStart: false);
            Assert.Equal(WarpToTimeMath.WarpPlanKind.ForwardOnly, plan.Kind);
            Assert.True(plan.RequiresFlightExit);
        }

        [Theory]
        [InlineData(100.0, 100.0)]   // exactly now
        [InlineData(100.5, 100.0)]   // within epsilon
        public void DecideWarpPlan_AtTarget(double target, double now)
        {
            var plan = WarpToTimeMath.DecideWarpPlan(target, now, inFlight: false,
                hasRewindTarget: true, landsAtTimelineStart: false);
            Assert.Equal(WarpToTimeMath.WarpPlanKind.AtTarget, plan.Kind);
        }

        [Fact]
        public void DecideWarpPlan_PastTarget_WithRewindTarget_RewindThenForward()
        {
            var plan = WarpToTimeMath.DecideWarpPlan(50, 1000, inFlight: true,
                hasRewindTarget: true, landsAtTimelineStart: true);
            Assert.Equal(WarpToTimeMath.WarpPlanKind.RewindThenForward, plan.Kind);
            Assert.True(plan.RequiresFlightExit);
            Assert.True(plan.LandsAtTimelineStart);
        }

        [Fact]
        public void DecideWarpPlan_PastTarget_NoRewindTarget_Unreachable()
        {
            var plan = WarpToTimeMath.DecideWarpPlan(50, 1000, inFlight: false,
                hasRewindTarget: false, landsAtTimelineStart: false);
            Assert.Equal(WarpToTimeMath.WarpPlanKind.Unreachable, plan.Kind);
            Assert.False(string.IsNullOrEmpty(plan.Reason));
        }

        // ── SelectRewindTargetIndex ──

        [Fact]
        public void SelectRewindTarget_NearestPrior()
        {
            var starts = new double[] { 100, 300, 200 };
            int idx = WarpToTimeMath.SelectRewindTargetIndex(starts, 250, out bool atStart);
            Assert.Equal(2, idx);          // 200 is the greatest <= 250
            Assert.False(atStart);
        }

        [Fact]
        public void SelectRewindTarget_ExactMatch()
        {
            var starts = new double[] { 100, 300, 200 };
            int idx = WarpToTimeMath.SelectRewindTargetIndex(starts, 300, out bool atStart);
            Assert.Equal(1, idx);
            Assert.False(atStart);
        }

        [Fact]
        public void SelectRewindTarget_BeforeAll_FallsBackToEarliest()
        {
            // Target precedes every launch (e.g. 1/1/0/0 game start with first launch at 100).
            var starts = new double[] { 300, 100, 200 };
            int idx = WarpToTimeMath.SelectRewindTargetIndex(starts, 0, out bool atStart);
            Assert.Equal(1, idx);          // earliest is 100 at index 1
            Assert.True(atStart);
        }

        [Fact]
        public void SelectRewindTarget_Empty_ReturnsMinusOne()
        {
            int idx = WarpToTimeMath.SelectRewindTargetIndex(new double[0], 100, out bool atStart);
            Assert.Equal(-1, idx);
            Assert.False(atStart);
        }

        [Fact]
        public void SelectRewindTarget_SingleAfterTarget_FallsBackToEarliest()
        {
            var starts = new double[] { 100 };
            int idx = WarpToTimeMath.SelectRewindTargetIndex(starts, 50, out bool atStart);
            Assert.Equal(0, idx);
            Assert.True(atStart);
        }
    }
}
