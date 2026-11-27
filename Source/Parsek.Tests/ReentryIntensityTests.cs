using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
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

        [Fact]
        public void StockReferencePoints_PPS20c_OffSmallStrongFull()
        {
            // The X0 re-flight reference points (AERO_FX_QUALITY 3, real side): no flame at rho 0
            // (72 km), full at Mach 6 (pixel diff 23290), strong at Mach 3.75 (13955), small at
            // Mach 3 (FxScalar 0.14, state 0.40; 429 px), none at Mach 2.5 and Mach 2 (0 px).
            Assert.Equal(0f, Intensity(2280f, 0f, 7.5f));
            Assert.Equal(1f, Intensity(1819.9f, 0.01124f, 6f, out float s6));
            Assert.Equal(1f, s6);
            float m375 = Intensity(1110.2f, 0.03291f, 3.75f);
            Assert.InRange(m375, 0.7f, 0.9f);
            float m3 = Intensity(885f, 0.04338f, 3f, out float s3);
            Assert.InRange(m3, 0.1f, 0.2f);
            Assert.InRange(s3, 0.39f, 0.41f);
            Assert.True(m375 > 4f * m3, $"Mach 3.75 ({m375}) should be far stronger than Mach 3 ({m3})");
            Assert.Equal(0f, Intensity(800f, 0.05f, 2.5f));
            Assert.Equal(0f, Intensity(640f, 0.0593f, 2f));
        }

        // --- Against stock's own trace (owner ruling 2026-11-28: same on / off, similar strength) ---

        private static readonly Regex TraceLine = new Regex(
            @"\] (?:trace|real unpacked=\S+ .*?) ut=\S+ alt=(?<alt>\S+) mach=(?<mach>\S+) rho=(?<rho>\S+) " +
            @"srfSpeed=(?<speed>\S+) .*? FxScalar=(?<fx>\S+) state=(?<state>\S+) ",
            RegexOptions.Compiled);

        private static List<double[]> ReadStockTrace()
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(root, "docs", "dev", "research", "p9-x0-spike-2026-11-27",
                "reflight", "PPS-20c", "log-FxReentryRig.txt");
            var rows = new List<double[]>();
            foreach (string line in File.ReadAllLines(path))
            {
                Match m = TraceLine.Match(line);
                if (!m.Success) continue;
                double rho = double.Parse(m.Groups["rho"].Value, CultureInfo.InvariantCulture);
                if (rho <= 0.0) continue;   // above the atmosphere stock forces 0 and so does the ghost
                rows.Add(new[]
                {
                    double.Parse(m.Groups["alt"].Value, CultureInfo.InvariantCulture),
                    double.Parse(m.Groups["mach"].Value, CultureInfo.InvariantCulture),
                    rho,
                    double.Parse(m.Groups["speed"].Value, CultureInfo.InvariantCulture),
                    double.Parse(m.Groups["fx"].Value, CultureInfo.InvariantCulture),
                    double.Parse(m.Groups["state"].Value, CultureInfo.InvariantCulture),
                });
            }
            return rows;
        }

        [Fact]
        public void StockTrace_PPS20c_TheGhostRuleSwitchesAndScalesAsStockDid()
        {
            // Each line is stock's AerodynamicsFX on the real probe (KSP 1.12.5, the X0
            // re-flight): the vessel's atmDensity, Mach and surface speed, and the FxScalar and
            // state stock computed from them. The ghost rule fed the same inputs must switch on
            // and off where stock did and read within 0.05 of its strength (the log rounds rho to
            // five decimals, which moves the scalar by up to 0.04 near the top of the air).
            List<double[]> rows = ReadStockTrace();
            Assert.True(rows.Count >= 40, $"expected the in-atmosphere trace, read {rows.Count} lines");
            int on = 0, off = 0;
            double worst = 0.0;
            foreach (double[] r in rows)
            {
                float raw = GhostAeroFx.ComputeFxScalar(r[2], r[3], (float)r[1],
                    AeroFxConstants.StockDefaults, out float state);
                double stock = Math.Max(0.0, r[4]);
                double ghost = Math.Max(0.0, raw);
                string where = string.Format(CultureInfo.InvariantCulture,
                    "alt={0:F0} mach={1:F2} rho={2:F5} speed={3:F1} stock={4:F3}/{5:F3} ghost={6:F3}/{7:F3}",
                    r[0], r[1], r[2], r[3], r[4], r[5], raw, state);
                if (Math.Abs(r[4]) > 0.01)   // away from the threshold itself, the sign must agree
                    Assert.True((r[4] > 0.0) == (raw > 0f), "on / off differs: " + where);
                Assert.True(Math.Abs(stock - ghost) <= 0.05, "strength differs: " + where);
                Assert.True(Math.Abs(r[5] - state) <= 0.01, "thermal blend differs: " + where);
                worst = Math.Max(worst, Math.Abs(stock - ghost));
                if (stock > 0.0) on++; else off++;
            }
            // The trace crosses every regime: fading in high up, full, the Mach 3.75 to 2.5 fall
            // and off below Mach 2.5.
            Assert.True(on >= 20 && off >= 10, $"on={on} off={off}");
            Assert.True(worst < 0.05, $"worst={worst}");
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
