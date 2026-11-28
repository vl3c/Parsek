using Parsek;
using Xunit;

namespace Parsek.Tests
{
    public class WatchOverlayTitleWrapTests
    {
        // 8 px per character: a stand-in for the label style's measured width.
        private static float Measure(string s) => s.Length * 8f;

        [Fact]
        public void ShortTitle_StaysOnOneLine()
        {
            string[] lines = WatchModeController.BuildWatchOverlayTitleLines(
                "Pod", "4.0 km", " [Free]", Measure, 288f);
            Assert.Equal(new[] { "Watching: Pod  (4.0 km) [Free]" }, lines);
        }

        [Fact]
        public void LongTitle_PutsDistanceAndModeOnSecondLine()
        {
            string[] lines = WatchModeController.BuildWatchOverlayTitleLines(
                "Kerbal X With Probe", "1069.7 km", " [Horizon]", Measure, 288f);
            Assert.Equal(new[] { "Watching: Kerbal X With Probe", "(1069.7 km) [Horizon]" }, lines);
        }

        [Fact]
        public void NoDistance_LongName_KeepsModeOnSecondLine()
        {
            string[] lines = WatchModeController.BuildWatchOverlayTitleLines(
                "A Very Long Vessel Name Indeed", "", " [Free]", Measure, 288f);
            Assert.Equal(new[] { "Watching: A Very Long Vessel Name Indeed", "[Free]" }, lines);
        }

        [Theory]
        [InlineData(0.0, "0 m")]
        [InlineData(999.4, "999 m")]
        [InlineData(999.6, "1.0 km")]
        [InlineData(4049.0, "4.0 km")]
        [InlineData(9960.0, "10 km")]
        [InlineData(290208.25, "290 km")]
        [InlineData(999600.0, "1.0 Mm")]
        [InlineData(12345678.0, "12 Mm")]
        [InlineData(13599840256.0, "14 Gm")]
        [InlineData(5.0e15, "5000 Tm")]
        public void FormatWatchDistance_UsesShortStockUnits(double meters, string expected)
        {
            Assert.Equal(expected, WatchModeController.FormatWatchDistance(meters));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(-1.0)]
        public void FormatWatchDistance_BadInput_IsAQuestionMark(double meters)
        {
            Assert.Equal("?", WatchModeController.FormatWatchDistance(meters));
        }

        [Fact]
        public void NoMeasure_FallsBackToOneLine()
        {
            string[] lines = WatchModeController.BuildWatchOverlayTitleLines(
                "Kerbal X With Probe", "4.0 km", " [Free]", null, 10f);
            Assert.Single(lines);
        }
    }
}
