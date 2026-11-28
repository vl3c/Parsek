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

        [Fact]
        public void NoMeasure_FallsBackToOneLine()
        {
            string[] lines = WatchModeController.BuildWatchOverlayTitleLines(
                "Kerbal X With Probe", "4.0 km", " [Free]", null, 10f);
            Assert.Single(lines);
        }
    }
}
