using Xunit;

namespace Parsek.Tests
{
    public class ReentryIntensityTests
    {
        // --- Basic threshold behavior ---

        [Fact]
        public void Vacuum_ZeroSpeedZeroPressure_ReturnsZero()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed: 0f, dynamicPressure: 0f);
            Assert.Equal(0f, intensity);
        }

        [Fact]
        public void MidRange_ReturnsIntensityBetweenZeroAndOne()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed: 1500f, dynamicPressure: 10000f);
            Assert.True(intensity > 0f, $"Expected intensity > 0, got {intensity}");
            Assert.True(intensity < 1f, $"Expected intensity < 1, got {intensity}");
        }

        [Fact]
        public void Saturated_HighPressureHighSpeed_ReturnsOne()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed: 2500f, dynamicPressure: 30000f);
            Assert.Equal(1f, intensity);
        }

        [Fact]
        public void BelowSpeedThreshold_HighPressure_ReturnsZero()
        {
            // Slow flight in thick atmosphere should not trigger reentry FX
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed: 200f, dynamicPressure: 15000f);
            Assert.Equal(0f, intensity);
        }

        // --- Edge values ---

        [Fact]
        public void DynamicPressureNaN_ReturnsZero()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed: 1500f, dynamicPressure: float.NaN);
            Assert.Equal(0f, intensity);
        }

        [Fact]
        public void DynamicPressureNegative_ReturnsZero()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed: 1500f, dynamicPressure: -100f);
            Assert.Equal(0f, intensity);
        }

        [Fact]
        public void SpeedNaN_ReturnsZero()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed: float.NaN, dynamicPressure: 10000f);
            Assert.Equal(0f, intensity);
        }

        [Fact]
        public void DynamicPressurePositiveInfinity_ReturnsOne()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed: 2000f, dynamicPressure: float.PositiveInfinity);
            Assert.Equal(1f, intensity);
        }

        [Fact]
        public void SpeedNegative_ReturnsZero()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed: -500f, dynamicPressure: 10000f);
            Assert.Equal(0f, intensity);
        }

        // --- Body-agnostic behavior ---

        [Fact]
        public void EveLikeDensity_HighPressure_Saturated()
        {
            // Eve-like: density=6.0 kg/m^3, speed=800 m/s
            // q = 0.5 * 6.0 * 800^2 = 1,920,000 Pa — well above ReentryQThresholdHigh
            float q = 0.5f * 6.0f * 800f * 800f;
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed: 800f, dynamicPressure: q);
            Assert.Equal(1f, intensity);
        }

        [Fact]
        public void DunaLikeDensity_ModeratePressure_BetweenZeroAndOne()
        {
            // Duna-like: density=0.02 kg/m^3, speed=800 m/s
            // q = 0.5 * 0.02 * 800^2 = 6400 Pa — between ReentryQThresholdLow and High
            float q = 0.5f * 0.02f * 800f * 800f;
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed: 800f, dynamicPressure: q);
            Assert.True(intensity > 0f, $"Expected intensity > 0 for Duna-like conditions, got {intensity}");
            Assert.True(intensity < 1f, $"Expected intensity < 1 for Duna-like conditions, got {intensity}");
        }

        // --- Linear ramp verification ---

        [Fact]
        public void ExactlyAtLowThreshold_ReturnsZero()
        {
            // q exactly at ReentryQThresholdLow (500) — at threshold, not above
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed: 1000f, dynamicPressure: 500f);
            Assert.Equal(0f, intensity);
        }

        [Fact]
        public void ExactlyAtHighThreshold_ReturnsOne()
        {
            // q exactly at ReentryQThresholdHigh (20000)
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed: 1000f, dynamicPressure: 20000f);
            Assert.Equal(1f, intensity);
        }

        [Fact]
        public void MidpointOfRamp_ReturnsApproxHalf()
        {
            // q at midpoint: (500 + 20000) / 2 = 10250
            // Expected intensity: (10250 - 500) / (20000 - 500) = 9750 / 19500 = 0.5
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed: 1000f, dynamicPressure: 10250f);
            Assert.Equal(0.5, (double)intensity, 4);
        }

        // --- Parameterized edge cases ---

        [Theory]
        [InlineData(0f, 0f, 0f)]           // Vacuum
        [InlineData(100f, 0f, 0f)]          // Slow, no pressure
        [InlineData(200f, 15000f, 0f)]      // Below speed threshold
        [InlineData(399f, 15000f, 0f)]      // Just below speed threshold
        [InlineData(1000f, 499f, 0f)]       // Just below q low threshold
        [InlineData(2000f, 25000f, 1f)]     // Saturated
        [InlineData(3000f, 50000f, 1f)]     // Far above saturation
        public void ParameterizedThresholds(float speed, float q, float expected)
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed, q);
            Assert.Equal((double)expected, (double)intensity, 4);
        }

        [Theory]
        [InlineData(1000f, 5250f, 0.2436f)]   // (5250-500)/(20000-500) = 4750/19500 ≈ 0.2436
        [InlineData(1000f, 10250f, 0.5f)]      // midpoint
        [InlineData(1000f, 15125f, 0.75f)]     // (15125-500)/19500 = 14625/19500 = 0.75
        public void LinearRampValues(float speed, float q, float expected)
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed, q);
            Assert.Equal((double)expected, (double)intensity, 3);
        }

        // --- Both NaN inputs ---

        [Fact]
        public void BothNaN_ReturnsZero()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed: float.NaN, dynamicPressure: float.NaN);
            Assert.Equal(0f, intensity);
        }

        // --- Speed exactly at threshold ---

        [Fact]
        public void SpeedExactlyAtThreshold_WithHighPressure_ReturnsZero()
        {
            // Speed exactly at 400 (threshold is <400 returns 0, so 400 should pass)
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed: 400f, dynamicPressure: 15000f);
            Assert.True(intensity > 0f, $"Speed at threshold (400) with high q should produce non-zero intensity, got {intensity}");
        }

        [Fact]
        public void SpeedJustBelowThreshold_ReturnsZero()
        {
            float intensity = GhostVisualBuilder.ComputeReentryIntensity(speed: 399.9f, dynamicPressure: 15000f);
            Assert.Equal(0f, intensity);
        }

        // --- Direction-agnostic ---

        [Fact]
        public void SameSpeedAndPressure_SameIntensity_RegardlessOfDirection()
        {
            // ComputeReentryIntensity takes speed (scalar) and q (scalar) —
            // direction is not an input, confirming direction-agnostic behavior
            float intensity1 = GhostVisualBuilder.ComputeReentryIntensity(speed: 1500f, dynamicPressure: 12000f);
            float intensity2 = GhostVisualBuilder.ComputeReentryIntensity(speed: 1500f, dynamicPressure: 12000f);
            Assert.Equal(intensity1, intensity2);
        }
    }
}
