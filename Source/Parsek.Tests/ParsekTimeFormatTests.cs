using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ParsekTimeFormatTests : System.IDisposable
    {
        public ParsekTimeFormatTests()
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekTimeFormat.ResetForTesting();
        }

        // ── FormatDuration (Kerbin calendar: 6h days, 426d years) ──

        [Theory]
        [InlineData(0, "0s")]
        [InlineData(45, "45s")]
        [InlineData(130, "2m 10s")]
        [InlineData(3600, "1h 0m")]
        [InlineData(7200, "2h 0m")]
        [InlineData(21600, "1d")]           // 6h = 1 Kerbin day
        [InlineData(50000, "2d 1h")]        // 2 days + 1h remainder
        [InlineData(9201600, "1y")]          // 426 * 21600
        [InlineData(10000000, "1y 36d")]
        public void FormatDuration_KerbinTime(double seconds, string expected)
        {
            Assert.Equal(expected, ParsekTimeFormat.FormatDuration(seconds));
        }

        [Theory]
        [InlineData(double.NaN, "0s")]
        [InlineData(double.PositiveInfinity, "0s")]
        [InlineData(-10, "0s")]
        public void FormatDuration_EdgeCases(double seconds, string expected)
        {
            Assert.Equal(expected, ParsekTimeFormat.FormatDuration(seconds));
        }

        [Theory]
        [InlineData(0, "0s")]
        [InlineData(45, "45s")]
        [InlineData(130, "2m 10s")]
        [InlineData(3600, "1h 0m")]
        [InlineData(86400, "1d")]           // 24h = 1 Earth day
        [InlineData(90000, "1d 1h")]        // 86400 + 3600
        [InlineData(31536000, "1y")]         // 365 * 86400
        public void FormatDuration_EarthTime(double seconds, string expected)
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = false;
            Assert.Equal(expected, ParsekTimeFormat.FormatDuration(seconds));
        }

        // ── FormatDurationFull ──

        [Theory]
        [InlineData(0, "")]
        [InlineData(-5, "")]
        [InlineData(30, "30s")]
        [InlineData(90, "1m, 30s")]
        [InlineData(3600, "1h")]
        [InlineData(21600, "1d")]           // 1 Kerbin day
        [InlineData(9201600, "1y")]         // 1 Kerbin year
        [InlineData(9201600 + 21600 + 3661, "1y, 1d, 1h, 1m, 1s")]
        public void FormatDurationFull_KerbinTime(double seconds, string expected)
        {
            Assert.Equal(expected, ParsekTimeFormat.FormatDurationFull(seconds));
        }

        [Fact]
        public void FormatDurationFull_EarthTime_DifferentDayLength()
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = false;
            // 86400s = 1 Earth day (would be 4 Kerbin days)
            Assert.Equal("1d", ParsekTimeFormat.FormatDurationFull(86400));
        }

        // ── FormatCountdown ──

        [Theory]
        [InlineData(45, "T-45s")]
        [InlineData(330, "T-5m 30s")]
        [InlineData(8100, "T-2h 15m 0s")]
        [InlineData(21600, "T-1d 0h 0m 0s")]
        [InlineData(0, "T-0s")]
        [InlineData(-10, "T+10s")]
        public void FormatCountdown_KerbinTime(double delta, string expected)
        {
            Assert.Equal(expected, ParsekTimeFormat.FormatCountdown(delta));
        }

        [Fact]
        public void FormatCountdown_EarthTime_DaysUse24Hours()
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = false;
            Assert.Equal("T-1d 0h 0m 0s", ParsekTimeFormat.FormatCountdown(86400));
        }

        // ── Calendar constants ──

        [Fact]
        public void KerbinCalendar_6hDays_426dYears()
        {
            Assert.Equal(21600, ParsekTimeFormat.SecsPerDay);
            Assert.Equal(426, ParsekTimeFormat.DaysPerYear);
            Assert.Equal(9201600, ParsekTimeFormat.SecsPerYear);
        }

        [Fact]
        public void EarthCalendar_24hDays_365dYears()
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = false;
            Assert.Equal(86400, ParsekTimeFormat.SecsPerDay);
            Assert.Equal(365, ParsekTimeFormat.DaysPerYear);
            Assert.Equal(31536000, ParsekTimeFormat.SecsPerYear);
        }
    }
}
