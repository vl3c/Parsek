using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The clock cursor the Space Center and the Tracking Station share to apply committed
    /// ledger actions their clock reaches with no scene change (TRACKING-STATION-LEDGER-CUTOFF-ALIGN).
    /// Pure over its two seams: the next-action lookup and the recalculation.
    /// </summary>
    public class CareerLedgerClockCursorTests
    {
        private readonly List<KeyValuePair<double, string>> recalcs =
            new List<KeyValuePair<double, string>>();

        private CareerLedgerClockCursor Make(string prefix, double nextActionUT)
        {
            return new CareerLedgerClockCursor(
                prefix,
                nextActionUTAfter: _ => nextActionUT,
                recalculate: (ut, reason) => recalcs.Add(new KeyValuePair<double, string>(ut, reason)));
        }

        [Fact]
        public void FirstValidFrame_SeedsWithoutRecalculating()
        {
            var cursor = Make("ts", nextActionUT: 50.0);
            Assert.False(cursor.Advance(100.0));
            Assert.Empty(recalcs);
            Assert.Equal(100.0, cursor.LastCutoffUT);
        }

        [Fact]
        public void InvalidClock_DoesNothing()
        {
            var cursor = Make("ts", nextActionUT: 50.0);
            Assert.False(cursor.Advance(double.NaN));
            Assert.False(cursor.Advance(double.PositiveInfinity));
            Assert.Empty(recalcs);
            Assert.True(double.IsNaN(cursor.LastCutoffUT));
        }

        [Fact]
        public void ClockBeforeNextAction_DoesNotRecalculate()
        {
            var cursor = Make("ts", nextActionUT: 500.0);
            cursor.Advance(100.0);
            Assert.False(cursor.Advance(499.0));
            Assert.Empty(recalcs);
        }

        [Fact]
        public void TrackingStationWarpPastNextAction_RecalculatesOnceAtNewClock()
        {
            var cursor = Make("ts", nextActionUT: 500.0);
            cursor.Advance(100.0);
            Assert.True(cursor.Advance(800.0));
            Assert.Single(recalcs);
            Assert.Equal(800.0, recalcs[0].Key);
            Assert.Equal("ts-clock", recalcs[0].Value);
            Assert.Equal(800.0, cursor.LastCutoffUT);
        }

        [Fact]
        public void BackwardClockMove_RecalculatesWithBackwardReason()
        {
            var cursor = Make("ksc", nextActionUT: double.PositiveInfinity);
            cursor.Advance(300.0);
            Assert.True(cursor.Advance(200.0));
            Assert.Equal("ksc-clock-backward", recalcs[0].Value);
        }

        [Fact]
        public void NoNextAction_DoesNotRecalculateGoingForward()
        {
            var cursor = Make("ts", nextActionUT: double.PositiveInfinity);
            cursor.Advance(100.0);
            Assert.False(cursor.Advance(1e9));
            Assert.Empty(recalcs);
        }

        [Fact]
        public void KscReasonHelper_MatchesCursorFormat()
        {
            Assert.Equal(
                CareerLedgerClockCursor.FormatReason("ksc", 129.0, 19.0, 0.05),
                ParsekKSC.GetCareerLedgerAdvanceReasonForKscUT(129.0, 19.0, 0.05));
        }
    }
}
