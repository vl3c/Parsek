using Xunit;

namespace Parsek.Tests
{
    public class CareerStartSnapshotTests
    {
        // Kerbin day = 21600s; used as the "first day" threshold in these cases.
        private const double Day = 21600.0;

        [Theory]
        // Fresh career: no snapshot, no recordings, clock at/near start -> capture.
        [InlineData(false, 0, 0.0, true)]
        [InlineData(false, 0, 5.0, true)]
        [InlineData(false, 0, 21599.0, true)]
        // Snapshot already exists -> never re-capture.
        [InlineData(true, 0, 0.0, false)]
        [InlineData(true, 5, 999999.0, false)]
        // Existing save loaded post-update: has recordings -> skip.
        [InlineData(false, 1, 0.0, false)]
        [InlineData(false, 3, 10.0, false)]
        // Past the first day -> skip (treated as not a fresh career).
        [InlineData(false, 0, 21600.0, false)]
        [InlineData(false, 0, 999999.0, false)]
        // Negative UT (shouldn't happen) -> skip.
        [InlineData(false, 0, -5.0, false)]
        public void ShouldCapture_Cases(bool exists, int recordingCount, double now, bool expected)
        {
            Assert.Equal(expected, CareerStartSnapshot.ShouldCapture(exists, recordingCount, now, Day));
        }
    }
}
