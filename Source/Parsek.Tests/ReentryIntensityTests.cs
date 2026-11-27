using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The ghost reentry intensity is stock's <c>AerodynamicsFX</c> <c>FxScalar</c> (part parity
    /// P9 Y1, design-part-ghost-parity.md G.7 "Y1 design"), clamped to 0..1. The reference points
    /// are the X0 re-flight marks (PPS-20c): the ghost's own standard-atmosphere density, surface
    /// speed and Mach at each mark.
    /// </summary>
    public class ReentryIntensityTests
    {
        private const float Tolerance = 1e-3f;

        private static float Intensity(float speed, float density, float mach)
            => Intensity(speed, density, mach, out _);

        private static float Intensity(float speed, float density, float mach, out float state)
            => GhostVisualBuilder.ComputeReentryIntensity(speed, density, mach,
                AeroFxConstants.StockDefaults, out state);

        // --- The X0 marks ---

        [Fact]
        public void Mark_Mach6_IsFullReentryHeat()
        {
            float fx = Intensity(1819.9f, 0.01124f, 6f, out float state);
            Assert.Equal(1f, fx);
            Assert.Equal(1f, state);
        }

        [Fact]
        public void Mark_Mach375_IsStrongButNotFull()
        {
            float fx = Intensity(1110.2f, 0.03291f, 3.75f, out float state);
            Assert.InRange(fx, 0.796f - Tolerance, 0.796f + Tolerance);
            Assert.Equal(1f, state);
        }

        [Fact]
        public void Mark_Mach3_IsFaintAndPartlyThermal()
        {
            float fx = Intensity(885f, 0.04338f, 3f, out float state);
            Assert.InRange(fx, 0.1329f - Tolerance, 0.1329f + Tolerance);
            Assert.InRange(state, 0.4f - 1e-5f, 0.4f + 1e-5f);
        }

        [Fact]
        public void Mark_Mach25_InThinAir_IsGone()
        {
            // Below the Mach FX fade end (0.0875) the fade is 0, and at Mach 2.5 the thermal
            // blend is 0 too, so stock's effect is off.
            float fx = Intensity(800f, 0.05f, 2.5f, out float state);
            Assert.Equal(0f, fx);
            Assert.Equal(0f, state);
        }

        // --- Stock's condensation and fades ---

        [Fact]
        public void DenseAir_FastPass_IsCondensation()
        {
            // Sea level, Mach 1.47: stock draws condensation (state 0) at full strength.
            float fx = Intensity(500f, 1.225f, 1.47f, out float state);
            Assert.Equal(1f, fx);
            Assert.Equal(0f, state);
        }

        [Fact]
        public void BetweenTheMachFades_ScalesByTheDensityFade()
        {
            // rho 0.15 sits between MachFXFadeEnd 0.0875 and MachFXFadeStart 0.25.
            float fx = Intensity(700f, 0.15f, 2f, out float state);
            Assert.InRange(fx, 0.1652f - Tolerance, 0.1652f + Tolerance);
            Assert.Equal(0f, state);
        }

        [Fact]
        public void BelowTheDensityFadeStart_FadesButDoesNotCut()
        {
            // Under 0.0015 stock squares the density toward 0 (cutoff multiplier 667); it does
            // not cut the effect, so a fast pass high up still shows.
            float fx = Intensity(1500f, 0.001f, 5f, out float state);
            Assert.InRange(fx, 0.1841f - Tolerance, 0.1841f + Tolerance);
            Assert.Equal(1f, state);
        }

        [Fact]
        public void BelowTheHeatFluxThreshold_IsZero()
        {
            Assert.Equal(0f, Intensity(700f, 0.05f, 2f));
            Assert.Equal(0f, Intensity(300f, 0.01f, 1f));
        }

        [Fact]
        public void HigherSpeed_SameAir_NeverWeaker()
        {
            float a = Intensity(900f, 0.04f, 3f);
            float b = Intensity(1000f, 0.04f, 3.4f);
            float c = Intensity(1200f, 0.04f, 4f);
            Assert.True(b >= a, $"{b} < {a}");
            Assert.True(c >= b, $"{c} < {b}");
            Assert.True(c > a, $"{c} <= {a}");
        }

        // --- Guards ---

        [Theory]
        [InlineData(float.NaN, 0.05f, 3f)]
        [InlineData(1000f, float.NaN, 3f)]
        [InlineData(1000f, 0.05f, float.NaN)]
        [InlineData(-1f, 0.05f, 3f)]
        [InlineData(1000f, -0.05f, 3f)]
        [InlineData(1000f, 0.05f, -3f)]
        [InlineData(1000f, float.NegativeInfinity, 3f)]
        [InlineData(0f, 0f, 0f)]
        [InlineData(0f, 1.225f, 0f)]
        public void BadOrEmptyInputs_ReturnZero(float speed, float density, float mach)
        {
            Assert.Equal(0f, Intensity(speed, density, mach, out float state));
            Assert.Equal(0f, state);
        }

        [Fact]
        public void Infinities_Saturate()
        {
            Assert.Equal(1f, Intensity(float.PositiveInfinity, 0.05f, 10f));
            Assert.Equal(1f, Intensity(1000f, float.PositiveInfinity, 3f));
        }

        [Fact]
        public void StockDefaults_MatchPhysicsCfg()
        {
            AeroFxConstants c = AeroFxConstants.StockDefaults;
            Assert.Equal(2.5f, c.ThermalStartMach);
            Assert.Equal(3.75f, c.ThermalFullMach);
            Assert.Equal(3.5, c.VelocityExponent);
            Assert.Equal(0.0091, c.DensityScalar1);
            Assert.Equal(0.5, c.DensityExponent1);
            Assert.Equal(0.09, c.DensityScalar2);
            Assert.Equal(2.0, c.DensityExponent2);
            Assert.Equal(0.0015, c.DensityFadeStart);
            Assert.Equal(0.25f, c.MachFxFadeStart);
            Assert.Equal(0.0875f, c.MachFxFadeEnd);
        }
    }
}
