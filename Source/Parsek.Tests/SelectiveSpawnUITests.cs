using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class SelectiveSpawnUITests : System.IDisposable
    {
        private const double Radius = 250.0;
        private const double MaxRelSpeed = 2.0;
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
                distance: 200, proximityRadius: 250,
                relativeSpeed: 1.0, maxRelativeSpeed: 2.0));
        }

        [Fact]
        public void IsSpawnCandidate_PastEndUT_False()
        {
            Assert.False(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 50, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 200, proximityRadius: 250,
                relativeSpeed: 1.0, maxRelativeSpeed: 2.0));
        }

        [Fact]
        public void IsSpawnCandidate_NoSpawnNeeded_False()
        {
            Assert.False(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: false, chainSuppressed: false,
                distance: 200, proximityRadius: 250,
                relativeSpeed: 1.0, maxRelativeSpeed: 2.0));
        }

        [Fact]
        public void IsSpawnCandidate_ChainSuppressed_False()
        {
            Assert.False(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: true, chainSuppressed: true,
                distance: 200, proximityRadius: 250,
                relativeSpeed: 1.0, maxRelativeSpeed: 2.0));
        }

        [Fact]
        public void IsSpawnCandidate_OutsideRadius_False()
        {
            Assert.False(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 300, proximityRadius: 250,
                relativeSpeed: 1.0, maxRelativeSpeed: 2.0));
        }

        [Fact]
        public void IsSpawnCandidate_AtExactRadius_True()
        {
            Assert.True(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 250, proximityRadius: 250,
                relativeSpeed: 1.0, maxRelativeSpeed: 2.0));
        }

        [Fact]
        public void IsSpawnCandidate_EndUTEqualsCurrentUT_False()
        {
            Assert.False(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 100, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 200, proximityRadius: 250,
                relativeSpeed: 1.0, maxRelativeSpeed: 2.0));
        }

        [Fact]
        public void IsSpawnCandidate_AboveMaxRelativeSpeed_False()
        {
            Assert.False(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 200, proximityRadius: 250,
                relativeSpeed: 2.5, maxRelativeSpeed: 2.0));
        }

        [Fact]
        public void IsSpawnCandidate_AtExactMaxRelativeSpeed_True()
        {
            Assert.True(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 200, proximityRadius: 250,
                relativeSpeed: 2.0, maxRelativeSpeed: 2.0));
        }

        [Fact]
        public void IsSpawnCandidate_ZeroRelativeSpeed_True()
        {
            Assert.True(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 200, proximityRadius: 250,
                relativeSpeed: 0.0, maxRelativeSpeed: 2.0));
        }

        // ── ComputeRelativeSpeed ──

        [Fact]
        public void ComputeRelativeSpeed_StationKeeping_ReturnsZero()
        {
            // Same relative offset at both samples: ghost and active vessel moved together.
            var prevActive = new Vector3d(1000, 0, 0);
            var prevGhost = new Vector3d(1100, 0, 0);
            var nowActive = new Vector3d(1500, 200, 0);  // both translated by (500, 200, 0)
            var nowGhost = new Vector3d(1600, 200, 0);
            double rel = SelectiveSpawnUI.ComputeRelativeSpeed(
                nowActive, nowGhost, prevActive, prevGhost,
                dt: 1.5f, minDt: 0.5f, maxDt: 5.0f);
            Assert.Equal(0.0, rel, precision: 6);
        }

        [Fact]
        public void ComputeRelativeSpeed_FrameShift_Cancels()
        {
            // Floating-origin shift: same uniform offset added to all four positions.
            // Relative geometry preserved -> relative speed should still be zero.
            var prevActive = new Vector3d(0, 0, 0);
            var prevGhost = new Vector3d(50, 0, 0);
            var shift = new Vector3d(1e6, -2e6, 5e5);
            var nowActive = prevActive + shift;
            var nowGhost = prevGhost + shift;
            double rel = SelectiveSpawnUI.ComputeRelativeSpeed(
                nowActive, nowGhost, prevActive, prevGhost,
                dt: 1.5f, minDt: 0.5f, maxDt: 5.0f);
            Assert.Equal(0.0, rel, precision: 6);
        }

        [Fact]
        public void ComputeRelativeSpeed_ApproachingAt3MperS_Returns3()
        {
            // Active stationary; ghost moves 4.5 m closer along x over 1.5s -> 3 m/s.
            var prevActive = new Vector3d(0, 0, 0);
            var prevGhost = new Vector3d(100, 0, 0);
            var nowActive = new Vector3d(0, 0, 0);
            var nowGhost = new Vector3d(95.5, 0, 0);
            double rel = SelectiveSpawnUI.ComputeRelativeSpeed(
                nowActive, nowGhost, prevActive, prevGhost,
                dt: 1.5f, minDt: 0.5f, maxDt: 5.0f);
            Assert.Equal(3.0, rel, precision: 6);
        }

        [Fact]
        public void ComputeRelativeSpeed_DtTooShort_ReturnsInfinity()
        {
            double rel = SelectiveSpawnUI.ComputeRelativeSpeed(
                Vector3d.zero, Vector3d.zero, Vector3d.zero, Vector3d.zero,
                dt: 0.1f, minDt: 0.5f, maxDt: 5.0f);
            Assert.Equal(double.PositiveInfinity, rel);
        }

        [Fact]
        public void ComputeRelativeSpeed_DtTooLong_ReturnsInfinity()
        {
            // Stale sample (e.g. after time warp / scene change) — must reject.
            double rel = SelectiveSpawnUI.ComputeRelativeSpeed(
                Vector3d.zero, Vector3d.zero, Vector3d.zero, Vector3d.zero,
                dt: 10f, minDt: 0.5f, maxDt: 5.0f);
            Assert.Equal(double.PositiveInfinity, rel);
        }

        [Fact]
        public void ComputeRelativeSpeed_NegativeDt_ReturnsInfinity()
        {
            double rel = SelectiveSpawnUI.ComputeRelativeSpeed(
                Vector3d.zero, Vector3d.zero, Vector3d.zero, Vector3d.zero,
                dt: -1f, minDt: 0.5f, maxDt: 5.0f);
            Assert.Equal(double.PositiveInfinity, rel);
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

            var result = SelectiveSpawnUI.FindNextSpawnCandidate(candidates, 100, Radius, MaxRelSpeed);

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

            Assert.Null(SelectiveSpawnUI.FindNextSpawnCandidate(candidates, 100, Radius, MaxRelSpeed));
        }

        [Fact]
        public void FindNextSpawnCandidate_Empty_ReturnsNull()
        {
            Assert.Null(SelectiveSpawnUI.FindNextSpawnCandidate(
                new List<NearbySpawnCandidate>(), 100, Radius, MaxRelSpeed));
        }

        [Fact]
        public void FindNextSpawnCandidate_Null_ReturnsNull()
        {
            Assert.Null(SelectiveSpawnUI.FindNextSpawnCandidate(null, 100, Radius, MaxRelSpeed));
        }

        [Fact]
        public void FindNextSpawnCandidate_EndUTEqualsCurrentUT_ReturnsNull()
        {
            var candidates = new List<NearbySpawnCandidate>
            {
                new NearbySpawnCandidate { endUT = 100 }
            };

            Assert.Null(SelectiveSpawnUI.FindNextSpawnCandidate(candidates, 100, Radius, MaxRelSpeed));
        }

        [Fact]
        public void FindNextSpawnCandidate_FarButSlow_SkippedInFavorOfInnerCandidate()
        {
            // Slow ghost at 500m sits inside the wider "show in list" envelope but outside the
            // 250m FF radius — the bottom-bar "Warp to Next Real Spawn" must not pick it.
            // The closer slow ghost (within both gates) wins even though its endUT is later.
            var candidates = new List<NearbySpawnCandidate>
            {
                new NearbySpawnCandidate
                {
                    vesselName = "FarSlow",
                    distance = 500,        // > Radius (250)
                    relativeSpeed = 0.5,   // <= MaxRelSpeed
                    endUT = 200            // earlier
                },
                new NearbySpawnCandidate
                {
                    vesselName = "NearSlow",
                    distance = 100,        // <= Radius
                    relativeSpeed = 0.5,
                    endUT = 400            // later
                }
            };

            var result = SelectiveSpawnUI.FindNextSpawnCandidate(candidates, 100, Radius, MaxRelSpeed);

            Assert.NotNull(result);
            Assert.Equal("NearSlow", result.Value.vesselName);
        }

        [Fact]
        public void FindNextSpawnCandidate_OnlyFarSlowCandidate_ReturnsNull()
        {
            // Lone slow-but-distant ghost: window shows it (red distance text, FF disabled),
            // bottom-bar "Warp to Next Real Spawn" stays disabled.
            var candidates = new List<NearbySpawnCandidate>
            {
                new NearbySpawnCandidate
                {
                    vesselName = "FarSlow",
                    distance = 500,
                    relativeSpeed = 0.5,
                    endUT = 200
                }
            };

            Assert.Null(SelectiveSpawnUI.FindNextSpawnCandidate(candidates, 100, Radius, MaxRelSpeed));
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
