using Xunit;

namespace Parsek.Tests
{
    public class ReentryIntensityTests
    {
        // Kerbin reference values for test readability
        // Sea-level density ≈ 1.225 kg/m³, speed of sound ≈ 340 m/s
        // Thermal FX starts at Mach 2.5 (850 m/s), fully orange at Mach 3.75 (1275 m/s)
        const float KerbinSeaLevelDensity = 1.225f;
        const float KerbinSpeedOfSound = 340f;

        // --- Basic threshold behavior ---

        [Fact]
        public void Vacuum_ZeroEverything_ReturnsZero()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: 0f, density: 0f, machNumber: 0f);
            Assert.Equal(0f, intensity);
        }

        [Fact]
        public void BelowMachThreshold_SeaLevel_ReturnsZero()
        {
            // Mach 2.0 — below thermal FX start (Mach 2.5)
            float speed = 2.0f * KerbinSpeedOfSound;
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: speed, density: KerbinSeaLevelDensity, machNumber: 2.0f);
            Assert.Equal(0f, intensity);
        }

        [Fact]
        public void AtMachThreshold_SeaLevel_ReturnsNonZero()
        {
            // Mach 2.5 — exactly at thermal FX start
            float speed = 2.5f * KerbinSpeedOfSound;
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: speed, density: KerbinSeaLevelDensity, machNumber: 2.5f);
            Assert.True(intensity > 0f, $"Mach 2.5 at sea level should produce non-zero intensity, got {intensity}");
        }

        [Fact]
        public void FullThermal_SeaLevel_ReturnsOne()
        {
            // Mach 3.75 at sea level — the calibration reference, should be ≈1.0
            float speed = 3.75f * KerbinSpeedOfSound;
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: speed, density: KerbinSeaLevelDensity, machNumber: 3.75f);
            Assert.Equal(1f, intensity);
        }

        [Fact]
        public void AboveFullThermal_SeaLevel_ClampedToOne()
        {
            // Mach 5 at sea level — above saturation, clamped
            float speed = 5f * KerbinSpeedOfSound;
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: speed, density: KerbinSeaLevelDensity, machNumber: 5f);
            Assert.Equal(1f, intensity);
        }

        [Fact]
        public void MidRange_Mach3_SeaLevel_BetweenZeroAndOne()
        {
            // Mach 3 at sea level — between start and full
            float speed = 3f * KerbinSpeedOfSound;
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: speed, density: KerbinSeaLevelDensity, machNumber: 3f);
            Assert.True(intensity > 0f, $"Expected intensity > 0 at Mach 3, got {intensity}");
            Assert.True(intensity < 1f, $"Expected intensity < 1 at Mach 3, got {intensity}");
        }

        // --- Density effects ---

        [Fact]
        public void BelowDensityFade_HighMach_ReturnsZero()
        {
            // Density below 0.0015 — near vacuum, FX fades out
            float speed = 4f * KerbinSpeedOfSound;
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: speed, density: 0.001f, machNumber: 4f);
            Assert.Equal(0f, intensity);
        }

        [Fact]
        public void LowDensity_HighMach_LowerIntensity()
        {
            // High altitude, thin air — lower intensity than sea level at same Mach
            float speed = 3.5f * KerbinSpeedOfSound;
            float seaLevel = GhostVisualBuilder.ComputeReentryIntensity(
                speed: speed, density: KerbinSeaLevelDensity, machNumber: 3.5f);
            float highAlt = GhostVisualBuilder.ComputeReentryIntensity(
                speed: speed, density: 0.01f, machNumber: 3.5f);
            Assert.True(highAlt < seaLevel,
                $"High altitude intensity ({highAlt}) should be less than sea level ({seaLevel})");
            Assert.True(highAlt > 0f,
                $"High altitude should still have some intensity at Mach 3.5, got {highAlt}");
        }

        // --- Body-agnostic behavior ---

        [Fact]
        public void EveLikeDensity_HighMach_Saturated()
        {
            // Eve-like: very dense atmosphere (density=6.0), Mach 3
            // Much higher density than Kerbin → saturated
            float speed = 3f * 270f; // Eve speed of sound ~270 m/s
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: speed, density: 6.0f, machNumber: 3f);
            Assert.Equal(1f, intensity);
        }

        [Fact]
        public void DunaLikeDensity_HighMach_LowIntensity()
        {
            // Duna-like: thin atmosphere (density=0.02), Mach 4
            float speed = 4f * 240f; // Duna speed of sound ~240 m/s
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: speed, density: 0.02f, machNumber: 4f);
            Assert.True(intensity > 0f, $"Duna-like at Mach 4 should produce some intensity, got {intensity}");
            Assert.True(intensity < 1f, $"Duna-like thin atmo should not saturate, got {intensity}");
        }

        // --- Edge values ---

        [Fact]
        public void SpeedNaN_ReturnsZero()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: float.NaN, density: 1f, machNumber: 3f);
            Assert.Equal(0f, intensity);
        }

        [Fact]
        public void DensityNaN_ReturnsZero()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: 1000f, density: float.NaN, machNumber: 3f);
            Assert.Equal(0f, intensity);
        }

        [Fact]
        public void MachNaN_ReturnsZero()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: 1000f, density: 1f, machNumber: float.NaN);
            Assert.Equal(0f, intensity);
        }

        [Fact]
        public void AllNaN_ReturnsZero()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: float.NaN, density: float.NaN, machNumber: float.NaN);
            Assert.Equal(0f, intensity);
        }

        [Fact]
        public void SpeedNegative_ReturnsZero()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: -500f, density: 1f, machNumber: 3f);
            Assert.Equal(0f, intensity);
        }

        [Fact]
        public void DensityNegative_ReturnsZero()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: 1000f, density: -1f, machNumber: 3f);
            Assert.Equal(0f, intensity);
        }

        [Fact]
        public void MachNegative_ReturnsZero()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: 1000f, density: 1f, machNumber: -1f);
            Assert.Equal(0f, intensity);
        }

        [Fact]
        public void SpeedPositiveInfinity_ReturnsOne()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: float.PositiveInfinity, density: 1f, machNumber: 3f);
            Assert.Equal(1f, intensity);
        }

        [Fact]
        public void DensityPositiveInfinity_ReturnsOne()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: 1000f, density: float.PositiveInfinity, machNumber: 3f);
            Assert.Equal(1f, intensity);
        }

        [Fact]
        public void DensityNegativeInfinity_ReturnsZero()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: 1000f, density: float.NegativeInfinity, machNumber: 3f);
            Assert.Equal(0f, intensity);
        }

        // --- Mach threshold boundary ---

        [Fact]
        public void JustBelowMachThreshold_ReturnsZero()
        {
            float speed = 2.49f * KerbinSpeedOfSound;
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(
                speed: speed, density: KerbinSeaLevelDensity, machNumber: 2.49f);
            Assert.Equal(0f, intensity);
        }

        // --- Parameterized tests ---

        [Theory]
        [InlineData(0f, 0f, 0f)]         // Vacuum
        [InlineData(100f, 1.0f, 0.3f)]   // Subsonic, below Mach 2.5
        [InlineData(500f, 1.0f, 1.5f)]   // Supersonic but below Mach 2.5
        [InlineData(800f, 1.0f, 2.35f)]  // Below Mach 2.5
        [InlineData(800f, 0.001f, 2.5f)] // At Mach threshold but below density fade
        public void BelowThreshold_ReturnsZero(float speed, float density, float mach)
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed, density, mach);
            Assert.Equal(0f, intensity);
        }

        [Theory]
        [InlineData(1000f, 1.0f, 3.0f)]   // Mid-range Mach, sea level
        [InlineData(1100f, 0.5f, 3.2f)]   // Mid-range Mach, mid altitude
        [InlineData(900f, 0.1f, 2.6f)]    // Just above Mach threshold, moderate density
        public void MidRange_ReturnsBetweenZeroAndOne(float speed, float density, float mach)
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed, density, mach);
            Assert.True(intensity > 0f, $"Expected > 0 for speed={speed}, density={density}, mach={mach}, got {intensity}");
            Assert.True(intensity < 1f, $"Expected < 1 for speed={speed}, density={density}, mach={mach}, got {intensity}");
        }

        // --- Monotonicity: higher Mach → higher intensity at same density ---

        [Fact]
        public void HigherMach_HigherIntensity_SameDensity()
        {
            float density = 0.5f;
            float i1 = GhostVisualBuilder.ComputeReentryIntensity(
                speed: 2.6f * KerbinSpeedOfSound, density: density, machNumber: 2.6f);
            float i2 = GhostVisualBuilder.ComputeReentryIntensity(
                speed: 3.0f * KerbinSpeedOfSound, density: density, machNumber: 3.0f);
            float i3 = GhostVisualBuilder.ComputeReentryIntensity(
                speed: 3.5f * KerbinSpeedOfSound, density: density, machNumber: 3.5f);

            Assert.True(i2 > i1, $"Mach 3.0 intensity ({i2}) should be > Mach 2.6 ({i1})");
            Assert.True(i3 > i2, $"Mach 3.5 intensity ({i3}) should be > Mach 3.0 ({i2})");
        }

        // --- Monotonicity: higher density → higher intensity at same Mach ---

        [Fact]
        public void HigherDensity_HigherIntensity_SameMach()
        {
            float mach = 3.0f;
            float speed = mach * KerbinSpeedOfSound;
            float i1 = GhostVisualBuilder.ComputeReentryIntensity(
                speed: speed, density: 0.01f, machNumber: mach);
            float i2 = GhostVisualBuilder.ComputeReentryIntensity(
                speed: speed, density: 0.1f, machNumber: mach);
            float i3 = GhostVisualBuilder.ComputeReentryIntensity(
                speed: speed, density: 1.0f, machNumber: mach);

            Assert.True(i2 > i1, $"Density 0.1 intensity ({i2}) should be > density 0.01 ({i1})");
            Assert.True(i3 > i2, $"Density 1.0 intensity ({i3}) should be > density 0.1 ({i2})");
        }

        // --- KSP Physics.cfg constants are exposed correctly ---

        [Fact]
        public void Constants_MatchKspPhysicsCfg()
        {
            Assert.Equal(2.5f, GhostVisualBuilder.AeroFxThermalStartMach);
            Assert.Equal(3.75f, GhostVisualBuilder.AeroFxThermalFullMach);
            Assert.Equal(3.5f, GhostVisualBuilder.AeroFxVelocityExponent);
            Assert.Equal(0.0091f, GhostVisualBuilder.AeroFxDensityScalar1);
            Assert.Equal(0.5f, GhostVisualBuilder.AeroFxDensityExponent1);
            Assert.Equal(0.09f, GhostVisualBuilder.AeroFxDensityScalar2);
            Assert.Equal(2f, GhostVisualBuilder.AeroFxDensityExponent2);
            Assert.Equal(0.0015f, GhostVisualBuilder.AeroFxDensityFadeStart);
        }
    }

    public class AltitudeInterpolationTests
    {
        [Fact]
        public void Midpoint_PreservesDoublePrecision()
        {
            double alt = TrajectoryMath.InterpolateAltitude(199000.0, 200000.0, 0.5f);
            Assert.Equal(199500.0, alt, 6);
        }

        [Fact]
        public void HighValues_NoFloatLoss()
        {
            double alt = TrajectoryMath.InterpolateAltitude(199999.123, 200000.456, 0.5f);
            double expected = 199999.123 + (200000.456 - 199999.123) * 0.5;
            Assert.Equal(expected, alt, 10);
        }

        [Fact]
        public void ZeroFraction_ReturnsBefore()
        {
            double alt = TrajectoryMath.InterpolateAltitude(100000.0, 200000.0, 0f);
            Assert.Equal(100000.0, alt);
        }

        [Fact]
        public void OneFraction_ReturnsAfter()
        {
            double alt = TrajectoryMath.InterpolateAltitude(100000.0, 200000.0, 1f);
            Assert.Equal(200000.0, alt);
        }

        [Fact]
        public void NegativeAltitudes_Work()
        {
            double alt = TrajectoryMath.InterpolateAltitude(-500.0, 500.0, 0.5f);
            Assert.Equal(0.0, alt, 6);
        }
    }
}
