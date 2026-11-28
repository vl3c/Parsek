using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Fix Y2 (design-part-ghost-parity.md G.8 item 2, "Y2 as built" under G.7): a destroyed ghost
    /// part's explosion power carries stock <c>Part.explode</c>'s speed term, read from the ghost's
    /// own surface speed.
    /// </summary>
    public class PartExplosionSpeedTermTests
    {
        private const int StockPrefabs = 7; // X0 U1: Thud 0, Explosion 0, 1, 2, 4, 5, 6

        [Theory]
        // Decompiled IL: 0.25 from 2000 m/s on; the 0.12 branch (above 1000 AND below 200) never runs.
        [InlineData(0.0, 0f)]
        [InlineData(150.0, 0f)]
        [InlineData(199.9, 0f)]
        [InlineData(200.0, 0f)]
        [InlineData(999.9, 0f)]
        [InlineData(1000.0, 0f)]
        [InlineData(1000.1, 0f)]
        [InlineData(1500.0, 0f)]
        [InlineData(1999.999, 0f)]
        [InlineData(2000.0, 0.25f)]
        [InlineData(2000.001, 0.25f)]
        [InlineData(3200.0, 0.25f)]
        [InlineData(-50.0, 0f)]
        public void SpeedOffset_IsStocksRule(double speed, float expected)
        {
            Assert.Equal(expected, GhostPlaybackLogic.StockPartExplosionSpeedOffset(speed));
        }

        [Fact]
        public void SpeedOffset_NonFiniteSpeeds()
        {
            Assert.Equal(0f, GhostPlaybackLogic.StockPartExplosionSpeedOffset(double.NaN));
            Assert.Equal(0f, GhostPlaybackLogic.StockPartExplosionSpeedOffset(double.NegativeInfinity));
            // +inf >= 2000 in stock's comparison too.
            Assert.Equal(0.25f, GhostPlaybackLogic.StockPartExplosionSpeedOffset(double.PositiveInfinity));
        }

        [Theory]
        // potential, speed, expected index among stock's 7 explosion prefabs
        [InlineData(0.5f, 0.0, 3)]     // the default part, slow: Explosion 2
        [InlineData(0.5f, 1999.0, 3)]
        [InlineData(0.5f, 2000.0, 4)]  // the default part at 2 km/s: Explosion 4
        [InlineData(0.0f, 0.0, 0)]     // potential 0: Thud 0
        [InlineData(0.0f, 2500.0, 1)]  // 0 + 0.25: Explosion 0
        [InlineData(0.1f, 0.0, 0)]
        [InlineData(0.1f, 2500.0, 2)]  // 0.1 + 0.25 = 0.35: Explosion 1
        [InlineData(1.0f, 2500.0, 6)]  // clamped at 1
        [InlineData(-1.0f, 2500.0, 0)] // stock: -0.75 is raised to minPower 0.001, index 0
        [InlineData(2.0f, 0.0, 6)]
        public void Power_PicksStocksPrefabIndex(float potential, double speed, int expectedIndex)
        {
            double power = GhostPlaybackLogic.StockPartExplosionPower(potential, speed);
            Assert.InRange(power, 0.0, 1.0);
            Assert.Equal(expectedIndex, StockExplosionPrefabCache.ResolvePrefabIndex(power, StockPrefabs));
            // The same index stock's own arithmetic gives: float sum, minPower floor, Clamp01.
            float sum = potential + (speed >= 2000.0 ? 0.25f : 0f);
            double howHard = sum < 0.001 ? 0.001 : sum;
            double clamped = howHard > 1.0 ? 1.0 : howHard;
            Assert.Equal((int)(clamped * (StockPrefabs - 1)), expectedIndex);
        }

        [Fact]
        public void Power_SumsInFloatAsStockDoes()
        {
            Assert.Equal((double)(0.1f + 0.25f), GhostPlaybackLogic.StockPartExplosionPower(0.1f, 2100.0));
            Assert.Equal(0.75, GhostPlaybackLogic.StockPartExplosionPower(0.5f, 2100.0));
            Assert.Equal(0.5, GhostPlaybackLogic.StockPartExplosionPower(0.5f, 900.0));
        }

        [Fact]
        public void Power_NaNPotentialFallsBackToTheDefault()
        {
            Assert.Equal(GhostAudioPresets.DefaultPartExplosionPotential,
                GhostPlaybackLogic.StockPartExplosionPower(float.NaN, 2100.0));
        }

        [Fact]
        public void SurfaceSpeed_UnknownIsZero()
        {
            Assert.Equal(0.0, GhostPlaybackLogic.PartExplosionSurfaceSpeed(null, out string src));
            Assert.Equal("none", src);

            var state = new GhostPlaybackState();
            state.ClearInterpolatedVelocities();
            Assert.Equal(0.0, GhostPlaybackLogic.PartExplosionSurfaceSpeed(state, out src));
            Assert.Equal("none", src);

            // A stale vector with no source is never read as a speed.
            state.lastInterpolatedSurfaceVelocity = new Vector3(3000f, 0f, 0f);
            Assert.Equal(0.0, GhostPlaybackLogic.PartExplosionSurfaceSpeed(state, out src));
            Assert.Equal("none", src);
        }

        [Fact]
        public void SurfaceSpeed_ReadsTheRecordedSpeedForTheStoredChord()
        {
            var chord = new Vector3(0f, 2010f, 0f);
            var state = new GhostPlaybackState();
            state.SetInterpolated(new InterpolationResult(Vector3.zero, "Kerbin", 30000.0)
                .WithSurfaceVelocity(chord, GhostSurfaceVelocitySource.Positions)
                .WithStockSurfaceSpeed(1995f));
            double speed = GhostPlaybackLogic.PartExplosionSurfaceSpeed(state, out string src);
            Assert.Equal(1995.0, speed, 3);
            Assert.Equal("recorded", src);
            // Just below stock's threshold: no speed term although the chord is above it.
            Assert.Equal(0f, GhostPlaybackLogic.StockPartExplosionSpeedOffset(speed));
        }

        [Fact]
        public void SurfaceSpeed_FallsBackToTheChordMagnitude()
        {
            var state = new GhostPlaybackState();
            state.SetInterpolated(new InterpolationResult(Vector3.zero, "Kerbin", 30000.0)
                .WithSurfaceVelocity(new Vector3(1200f, 1600f, 0f), GhostSurfaceVelocitySource.Positions));
            double speed = GhostPlaybackLogic.PartExplosionSurfaceSpeed(state, out string src);
            Assert.Equal(2000.0, speed, 2);
            Assert.Equal("positions", src);

            state.SetInterpolated(new InterpolationResult(Vector3.zero, "Kerbin", 90000.0)
                .WithSurfaceVelocity(new Vector3(0f, 0f, 2300f), GhostSurfaceVelocitySource.Orbit));
            speed = GhostPlaybackLogic.PartExplosionSurfaceSpeed(state, out src);
            Assert.Equal(2300.0, speed, 2);
            Assert.Equal("orbit", src);
        }

        [Fact]
        public void Format_IsGrepStableAndInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string s = GhostPlaybackLogic.FormatPartExplosionPower(0.5f, 2104.31, "recorded", 0.75);
                Assert.Equal(
                    "potential=0.5 srfSpeed=2104.3 speedSource=recorded speedOffset=0.25 power=0.75 prefabIndex=4",
                    s);
                Assert.Equal(
                    "potential=0.1 srfSpeed=0.0 speedSource=none speedOffset=0 power=0.1 prefabIndex=0",
                    GhostPlaybackLogic.FormatPartExplosionPower(0.1f, 0.0, null, 0.1));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void TheDestroyedPartPath_ReadsTheGhostSpeedAndStocksRule()
        {
            string text = PartParityStateRulesTests.StripComments(File.ReadAllText(Path.Combine(
                SyntheticRecordingTests.ResolveProjectRoot(), "Source", "Parsek", "Ghost",
                "GhostPlaybackLogic.PartEvents.cs")));
            string apply = PartParityStateRulesTests.MethodBody(text,
                "private static GhostPartEventOutcome ApplyDestroyedPartEvent(");
            Assert.True(apply != null, "ApplyDestroyedPartEvent moved; this cell is vacuous");
            Assert.Contains("PartExplosionSurfaceSpeed(state, out string speedSource)", apply);
            string play = PartParityStateRulesTests.MethodBody(text, "internal static void PlayPartDestroyedFxAtPart(");
            Assert.True(play != null, "PlayPartDestroyedFxAtPart moved; this cell is vacuous");
            Assert.Contains("double power = StockPartExplosionPower(potential, surfaceSpeedMps);", play);
            Assert.Contains("float potential = ResolvePartExplosionPotential(partName);", play);
            Assert.DoesNotContain("ActiveVessel", play);
            Assert.DoesNotContain("ship_srfSpeed", play);
        }

        [Fact]
        public void TheDestroyedPartFallback_DrawsStocksExplosionNotThePuff()
        {
            string text = PartParityStateRulesTests.StripComments(File.ReadAllText(Path.Combine(
                SyntheticRecordingTests.ResolveProjectRoot(), "Source", "Parsek", "Ghost",
                "GhostPlaybackLogic.PartEvents.cs")));
            string play = PartParityStateRulesTests.MethodBody(text, "internal static void PlayPartDestroyedFxAtPart(");
            Assert.True(play != null, "PlayPartDestroyedFxAtPart moved; this cell is vacuous");
            Assert.Contains("GhostVisualBuilder.SpawnFallbackExplosionFx(t.position, power, ResolveGhostPartScale(t));", play);
            Assert.DoesNotContain("SpawnPartPuffAtPart(", play);
            Assert.DoesNotContain("SpawnPartPuffFx(", play);
        }
    }
}
