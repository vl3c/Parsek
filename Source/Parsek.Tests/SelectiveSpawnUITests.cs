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
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
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
