using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class SelectiveSpawnUITests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SelectiveSpawnUITests()
        {
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            // Default to Earth time for existing tests
            SelectiveSpawnUI.KerbinTimeOverrideForTesting = false;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            SelectiveSpawnUI.KerbinTimeOverrideForTesting = null;
        }

        // ── IsSpawnCandidate ──

        [Fact]
        public void IsSpawnCandidate_AllConditionsMet_True()
        {
            Assert.True(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 300, proximityRadius: 500));
        }

        [Fact]
        public void IsSpawnCandidate_PastEndUT_False()
        {
            Assert.False(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 50, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 300, proximityRadius: 500));
        }

        [Fact]
        public void IsSpawnCandidate_NoSpawnNeeded_False()
        {
            Assert.False(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: false, chainSuppressed: false,
                distance: 300, proximityRadius: 500));
        }

        [Fact]
        public void IsSpawnCandidate_ChainSuppressed_False()
        {
            Assert.False(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: true, chainSuppressed: true,
                distance: 300, proximityRadius: 500));
        }

        [Fact]
        public void IsSpawnCandidate_OutsideRadius_False()
        {
            Assert.False(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 600, proximityRadius: 500));
        }

        [Fact]
        public void IsSpawnCandidate_AtExactRadius_True()
        {
            Assert.True(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 500, proximityRadius: 500));
        }

        [Fact]
        public void IsSpawnCandidate_EndUTEqualsCurrentUT_False()
        {
            Assert.False(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 100, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 300, proximityRadius: 500));
        }

        // ── FindNextSpawnCandidate ──

        [Fact]
        public void FindNextSpawnCandidate_ReturnsEarliestFuture()
        {
            var candidates = new List<NearbySpawnCandidate>
            {
                new NearbySpawnCandidate { recordingIndex = 0, vesselName = "A", endUT = 300 },
                new NearbySpawnCandidate { recordingIndex = 1, vesselName = "B", endUT = 150 },
                new NearbySpawnCandidate { recordingIndex = 2, vesselName = "C", endUT = 200 }
            };

            var result = SelectiveSpawnUI.FindNextSpawnCandidate(candidates, 100);

            Assert.NotNull(result);
            Assert.Equal("B", result.Value.vesselName);
        }

        [Fact]
        public void FindNextSpawnCandidate_AllPast_ReturnsNull()
        {
            var candidates = new List<NearbySpawnCandidate>
            {
                new NearbySpawnCandidate { endUT = 50 },
                new NearbySpawnCandidate { endUT = 80 }
            };

            Assert.Null(SelectiveSpawnUI.FindNextSpawnCandidate(candidates, 100));
        }

        [Fact]
        public void FindNextSpawnCandidate_Empty_ReturnsNull()
        {
            Assert.Null(SelectiveSpawnUI.FindNextSpawnCandidate(
                new List<NearbySpawnCandidate>(), 100));
        }

        [Fact]
        public void FindNextSpawnCandidate_Null_ReturnsNull()
        {
            Assert.Null(SelectiveSpawnUI.FindNextSpawnCandidate(null, 100));
        }

        [Fact]
        public void FindNextSpawnCandidate_EndUTEqualsCurrentUT_ReturnsNull()
        {
            var candidates = new List<NearbySpawnCandidate>
            {
                new NearbySpawnCandidate { endUT = 100 }
            };

            Assert.Null(SelectiveSpawnUI.FindNextSpawnCandidate(candidates, 100));
        }

        // ── FormatTimeDelta ──

        [Fact]
        public void FormatTimeDelta_UnderMinute()
        {
            Assert.Equal("45s", SelectiveSpawnUI.FormatTimeDelta(45));
        }

        [Fact]
        public void FormatTimeDelta_UnderHour()
        {
            Assert.Equal("5m 30s", SelectiveSpawnUI.FormatTimeDelta(330));
        }

        [Fact]
        public void FormatTimeDelta_OverHour()
        {
            Assert.Equal("2h 15m", SelectiveSpawnUI.FormatTimeDelta(8100));
        }

        [Fact]
        public void FormatTimeDelta_Negative_Clamps()
        {
            Assert.Equal("0s", SelectiveSpawnUI.FormatTimeDelta(-10));
        }

        [Fact]
        public void FormatTimeDelta_Zero()
        {
            Assert.Equal("0s", SelectiveSpawnUI.FormatTimeDelta(0));
        }

        // ── FormatCountdown ──

        [Fact]
        public void FormatCountdown_SecondsOnly()
        {
            Assert.Equal("T-45s", SelectiveSpawnUI.FormatCountdown(45));
        }

        [Fact]
        public void FormatCountdown_MinutesAndSeconds()
        {
            Assert.Equal("T-5m 30s", SelectiveSpawnUI.FormatCountdown(330));
        }

        [Fact]
        public void FormatCountdown_HoursMinutesSeconds()
        {
            Assert.Equal("T-2h 15m 0s", SelectiveSpawnUI.FormatCountdown(8100));
        }

        [Fact]
        public void FormatCountdown_DaysHoursMinutesSeconds()
        {
            Assert.Equal("T-1d 2h 3m 4s", SelectiveSpawnUI.FormatCountdown(93784));
        }

        [Fact]
        public void FormatCountdown_YearsDaysHours()
        {
            Assert.Equal("T-1y 10d 0h 0m 0s", SelectiveSpawnUI.FormatCountdown(365 * 86400 + 10 * 86400));
        }

        [Fact]
        public void FormatCountdown_HidesLeadingZeros()
        {
            // 3661 seconds = 1h 1m 1s — no years or days
            Assert.Equal("T-1h 1m 1s", SelectiveSpawnUI.FormatCountdown(3661));
        }

        [Fact]
        public void FormatCountdown_Negative_ShowsTPlus()
        {
            Assert.Equal("T+10s", SelectiveSpawnUI.FormatCountdown(-10));
        }

        [Fact]
        public void FormatCountdown_Zero()
        {
            Assert.Equal("T-0s", SelectiveSpawnUI.FormatCountdown(0));
        }

        // ── FormatCountdown (Kerbin time) ──

        [Fact]
        public void FormatCountdown_KerbinTime_DaysUse6HourDay()
        {
            SelectiveSpawnUI.KerbinTimeOverrideForTesting = true;
            // 21600s = 1 Kerbin day (6 hours)
            Assert.Equal("T-1d 0h 0m 0s", SelectiveSpawnUI.FormatCountdown(21600));
        }

        [Fact]
        public void FormatCountdown_KerbinTime_YearsUse426Days()
        {
            SelectiveSpawnUI.KerbinTimeOverrideForTesting = true;
            // 1 Kerbin year = 426 * 21600 = 9,201,600s
            double oneKerbinYear = 426.0 * 21600;
            Assert.Equal("T-1y 0d 0h 0m 0s", SelectiveSpawnUI.FormatCountdown(oneKerbinYear));
        }

        [Fact]
        public void FormatCountdown_KerbinTime_MixedComponents()
        {
            SelectiveSpawnUI.KerbinTimeOverrideForTesting = true;
            // 1 day (21600) + 2h (7200) + 3m (180) + 4s = 28984s
            Assert.Equal("T-1d 2h 3m 4s", SelectiveSpawnUI.FormatCountdown(28984));
        }

        [Fact]
        public void FormatCountdown_EarthTime_DaysUse24HourDay()
        {
            SelectiveSpawnUI.KerbinTimeOverrideForTesting = false;
            // 86400s = 1 Earth day
            Assert.Equal("T-1d 0h 0m 0s", SelectiveSpawnUI.FormatCountdown(86400));
        }

        [Fact]
        public void FormatCountdown_LargeValue_NoIntOverflow()
        {
            // Value larger than int.MaxValue seconds (~68 years Earth time)
            double hugeSeconds = 3_000_000_000.0;
            string result = SelectiveSpawnUI.FormatCountdown(hugeSeconds);
            Assert.StartsWith("T-", result);
            Assert.Contains("y", result);
        }

        // ── FormatNextSpawnTooltip ──

        [Fact]
        public void FormatNextSpawnTooltip_HasCandidate()
        {
            var cand = new NearbySpawnCandidate { vesselName = "Station", endUT = 200 };

            string result = SelectiveSpawnUI.FormatNextSpawnTooltip(cand, 100);

            Assert.Contains("Station", result);
            Assert.Contains("1m 40s", result);
        }

        [Fact]
        public void FormatNextSpawnTooltip_NullCandidate()
        {
            string result = SelectiveSpawnUI.FormatNextSpawnTooltip(null, 100);
            Assert.Equal("No nearby craft to spawn", result);
        }
    }
}
