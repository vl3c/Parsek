using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for <see cref="PartParityFxMetrics"/> (design-part-ghost-parity.md E.3 /
    /// E.4, piece F1b): the accumulation to 8-bit tiles, every side measure, every comparison,
    /// the fail lines the C# control judges read, the split halves, the particle reduction,
    /// the JSON blocks, and the committed synthetic fixture the harness recomputes. The
    /// broken-plume cells assert the WHOLE failing set, so each mutation moves exactly its
    /// own measure.
    /// </summary>
    public class PartParityFxMetricsTests
    {
        private const string Flame = PartParityFxRules.LayerFlame;
        private const string Smoke = PartParityFxRules.LayerSmoke;
        private const string SideA = PartParityFxRules.ViewFxSideA;
        private const string Bottom = PartParityFxRules.ViewFxBottom;
        private const int N = PartParityFxRules.Samples;

        private static PartParityFxViewResult Measure(FxPlume real, FxPlume ghost, string layer = Flame,
            string view = SideA, int n = N)
            => PartParityFxViewResult.Measure(view, PartParityStateRules.RouteEvent, layer, real.Axis(),
                real.Samples(layer, n), ghost.Samples(layer, n));

        private static FxPlume Smoky(FxPlume p)
        {
            FxPlume s = p.Clone();
            s.Alpha = 200;
            s.R = s.G = s.B = 120.0;
            s.HalfWidth = 7.0;
            return s;
        }

        // ----- the tile rule -----

        [Fact]
        public void Accumulator_WritesTheEightBitTileRule()
        {
            var acc = new PartParityFxAccumulator(Flame, 2, 1);
            acc.Add(new byte[] { 10, 20, 30, 0, 0, 0, 0, 0 });
            acc.Add(new byte[] { 20, 40, 61, 0, 15, 15, 15, 255 });
            acc.Add(new byte[] { 0, 0, 0, 0, 16, 0, 0, 0 });
            byte[] tile = acc.ToTile();
            Assert.Equal(3, acc.Samples);
            Assert.Equal(new byte[] { 15, 30, 46, 170, 16, 0, 0, 85 }, tile);
            Assert.Equal(2, PartParityFxRules.CoveredSamples(tile[3], 3));
            Assert.Equal(1, PartParityFxRules.CoveredSamples(tile[7], 3));
        }

        [Fact]
        public void Accumulator_FlameCoversByMaxChannel_SmokeByAlpha()
        {
            var flame = new PartParityFxAccumulator(Flame, 2, 1);
            flame.Add(new byte[] { 0, 16, 0, 0, 15, 15, 15, 255 });
            Assert.Equal(1, flame.CoveredAt(0));
            Assert.Equal(0, flame.CoveredAt(1));
            var smoke = new PartParityFxAccumulator(Smoke, 2, 1);
            smoke.Add(new byte[] { 0, 0, 0, 128, 255, 255, 255, 127 });
            Assert.Equal(1, smoke.CoveredAt(0));
            Assert.Equal(0, smoke.CoveredAt(1));
            Assert.Equal(new byte[] { 0, 0, 0, 255, 0, 0, 0, 0 }, smoke.ToTile());
        }

        [Fact]
        public void Accumulator_RefusesAWrongSizeAndMoreThan255Samples()
        {
            var acc = new PartParityFxAccumulator(Flame, 1, 1);
            Assert.Throws<ArgumentException>(() => acc.Add(new byte[8]));
            for (int i = 0; i < PartParityFxRules.MaxSamples; i++) acc.Add(new byte[4]);
            Assert.Throws<InvalidOperationException>(() => acc.Add(new byte[4]));
        }

        [Theory]
        [InlineData(16, 8, 8)]
        [InlineData(5, 3, 2)]
        [InlineData(1, 1, 0)]
        public void SampleSet_SplitsZeroBasedEvenAndOdd(int n, int even, int odd)
        {
            var set = new PartParityFxSampleSet(Flame, 1, 1);
            for (int i = 0; i < n; i++) set.Add(new byte[] { (byte)(i % 2 == 0 ? 200 : 0), 0, 0, 0 });
            Assert.Equal(n, set.All.Samples);
            Assert.Equal(even, set.Even.Samples);
            Assert.Equal(odd, set.Odd.Samples);
            Assert.Equal(even, set.Even.CoveredAt(0));
            Assert.Equal(0, set.Odd.CoveredAt(0));
        }

        [Fact]
        public void ViewMeasure_RefusesSampleSetsOfDifferentSizeOrCount()
        {
            Assert.Throws<ArgumentException>(() => PartParityFxViewResult.Measure(SideA, PartParityStateRules.RouteEvent, Flame,
                Intact.Axis(), Intact.Samples(Flame, N), Intact.Samples(Flame, N - 1)));
            Assert.Throws<ArgumentException>(() => PartParityFxViewResult.Measure(SideA, PartParityStateRules.RouteEvent, Flame,
                Intact.Axis(), Intact.Samples(Flame, N), FxPlume.Empty(Flame, 32, N)));
        }

        // ----- one side -----

        [Fact]
        public void MeasureTile_ARectangle_ReadsItsExactExtents()
        {
            var p = new FxPlume { Tile = 64, OriginX = 10, OriginY = 32, Length = 40, HalfWidth = 4, TipJitter = 0, EdgeDropout = 0, R = 200, G = 100, B = 50 };
            PartParityFxSampleSet set = p.Samples(Flame, N);
            PartParityFxSideMeasures m = PartParityFxMetrics.MeasureTile(set.All.ToTile(), 64, 64, N, SideA, p.Axis());
            // Pixel centres x 10.5 .. 49.5 (t 0.5 .. 39.5), y 28.5 .. 35.5 (s -3.5 .. 3.5).
            Assert.Equal(40 * 8, m.CorePx);
            Assert.Equal(40 * 8, m.EnvelopePx);
            Assert.Equal(0, m.SaturatedPx);
            Assert.Equal(0.5, m.OriginPx);
            Assert.Equal(39.0, m.LengthPx);
            Assert.Equal(7.0, m.WidthPx);
            Assert.Equal(new[] { 30.0, 32.0 }, m.CoreCentroid);
            Assert.Equal(0.0, m.CoreAxisDeg.Value, 12);
            Assert.NotNull(m.MeanColor);
            Assert.Equal(m.MeanColor[0] / (m.MeanColor[0] + m.MeanColor[1] + m.MeanColor[2]), m.Chromaticity[0], 12);
            Assert.Equal(PartParityFxRules.HueBins, m.HueHistogram.Length);
            Assert.Equal(m.HueHistogram.Sum(), m.HueHistogram[PartParityFxMetrics.HueBin(200, 100, 50)]);
        }

        [Fact]
        public void MeasureTile_TheEmptyPlume_IsZeroAndNull()
        {
            PartParityFxSampleSet set = FxPlume.Empty(Flame, 32, N);
            var p = new FxPlume { Tile = 32 };
            PartParityFxSideMeasures m = PartParityFxMetrics.MeasureTile(set.All.ToTile(), 32, 32, N, SideA, p.Axis());
            Assert.Equal(0, m.CorePx);
            Assert.Equal(0, m.EnvelopePx);
            Assert.Null(m.OriginPx);
            Assert.Null(m.LengthPx);
            Assert.Null(m.WidthPx);
            Assert.Null(m.CoreCentroid);
            Assert.Null(m.MeanColor);
            Assert.Null(m.Chromaticity);
            Assert.Null(m.Luminance);
            Assert.All(m.HueHistogram, v => Assert.Equal(0.0, v));
        }

        [Fact]
        public void MeasureTile_ACoreAtHalfTheSamples_EnvelopeAtOne()
        {
            var tile = new byte[3 * 4];
            tile[3] = (byte)PartParityFxRules.TileAlpha(8, N);
            tile[7] = (byte)PartParityFxRules.TileAlpha(7, N);
            tile[11] = (byte)PartParityFxRules.TileAlpha(1, N);
            tile[0] = tile[4] = tile[8] = 100;
            PartParityFxSideMeasures m = PartParityFxMetrics.MeasureTile(tile, 3, 1, N, SideA, null);
            Assert.Equal(1, m.CorePx);
            Assert.Equal(3, m.EnvelopePx);
            Assert.Null(m.OriginPx);
        }

        [Fact]
        public void MeasureTile_AClippedCore_ColourAndBrightnessReadOnlyTheUnclippedPixels()
        {
            // Left half of the core clipped white, right half (200, 100, 50).
            var tile = new byte[4 * 1 * 4];
            for (int x = 0; x < 4; x++)
            {
                byte v = (byte)(x < 2 ? 255 : 0);
                tile[x * 4] = x < 2 ? (byte)255 : (byte)200;
                tile[x * 4 + 1] = x < 2 ? (byte)255 : (byte)100;
                tile[x * 4 + 2] = x < 2 ? v : (byte)50;
                tile[x * 4 + 3] = 255;
            }
            PartParityFxSideMeasures m = PartParityFxMetrics.MeasureTile(tile, 4, 1, N, SideA, null);
            Assert.Equal(4, m.CorePx);
            Assert.Equal(2, m.SaturatedPx);
            Assert.Equal(new[] { 200.0, 100.0, 50.0 }, m.MeanColor);
            Assert.Equal(PartParityFxRules.Luminance(200, 100, 50), m.Luminance.Value, 12);
            Assert.Equal(new[] { 200.0 / 350.0, 100.0 / 350.0 }, m.Chromaticity);
            Assert.Equal(2 * PartParityFxRules.Luminance(200, 100, 50), m.HueHistogram.Sum(), 9);

            // A ghost with the same unclipped colour and no clipping passes colour.
            var ghostTile = (byte[])tile.Clone();
            for (int x = 0; x < 2; x++) { ghostTile[x * 4] = 200; ghostTile[x * 4 + 1] = 100; ghostTile[x * 4 + 2] = 50; }
            PartParityFxSideMeasures g = PartParityFxMetrics.MeasureTile(ghostTile, 4, 1, N, SideA, null);
            PartParityFxCompare c = PartParityFxMetrics.Compare(m, g, SideA, null);
            Assert.Equal(0, g.SaturatedPx);
            Assert.Equal(0.0, c.ChromaDistance.Value, 12);
            Assert.Equal(0.0, c.HueL1.Value, 12);
            Assert.Empty(PartParityFxMetrics.FailingMeasures(Flame, m, g, c, false));
        }

        [Theory]
        [InlineData(200, 0, 0, 0)]
        [InlineData(200, 100, 0, 1)]
        [InlineData(200, 200, 0, 2)]
        [InlineData(0, 200, 0, 4)]
        [InlineData(0, 200, 200, 6)]
        [InlineData(0, 0, 200, 8)]
        [InlineData(200, 0, 200, 10)]
        [InlineData(200, 0, 1, 11)]
        [InlineData(90, 90, 90, -1)]
        [InlineData(0, 0, 0, -1)]
        public void HueBin_IsTheIntegerHsvSector(int r, int g, int b, int bin)
            => Assert.Equal(bin, PartParityFxMetrics.HueBin(r, g, b));

        [Fact]
        public void HueBin_AgreesWithTheFloatHsvHueOverEveryColourStep()
        {
            for (int r = 0; r < 256; r += 17)
            for (int g = 0; g < 256; g += 17)
            for (int b = 0; b < 256; b += 17)
            {
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (max == min) { Assert.Equal(-1, PartParityFxMetrics.HueBin(r, g, b)); continue; }
                double d = max - min, h;
                if (max == r) h = 60.0 * (((g - b) / d) % 6.0);
                else if (max == g) h = 60.0 * ((b - r) / d + 2.0);
                else h = 60.0 * ((r - g) / d + 4.0);
                if (h < 0) h += 360.0;
                int want = Math.Min(PartParityFxRules.HueBins - 1, (int)Math.Floor(h / (360.0 / PartParityFxRules.HueBins) + 1e-9));
                Assert.Equal(want, PartParityFxMetrics.HueBin(r, g, b));
            }
        }

        // ----- each broken plume moves exactly its own measure -----

        private static readonly FxPlume Intact = new FxPlume { Seed = 7 };

        [Fact]
        public void IntactAgainstIntact_TwoSeeds_FailsNothing()
        {
            FxPlume other = Intact.Clone();
            other.Seed = 8;
            Assert.Empty(Measure(Intact, other).FailingMeasures(false));
            Assert.Empty(Measure(Smoky(Intact), Smoky(other), Smoke).FailingMeasures(false));
        }

        [Fact]
        public void ShiftedPlume_FailsOriginAndLateral_Only()
        {
            FxPlume shifted = Intact.Clone();
            shifted.Start = 6.0;
            shifted.Lateral = 6.0;
            PartParityFxViewResult r = Measure(Intact, shifted);
            Assert.Equal(new[] { PartParityFxRules.MeasureOrigin, PartParityFxRules.MeasureLateral }, r.FailingMeasures(false));
            Assert.Equal(6.0, r.Compare.OriginDeltaPx.Value, 9);
            Assert.Equal(6.0, r.Compare.LateralOffsetPx.Value, 9);
            Assert.Equal(1.0, r.Compare.LengthRatio.Value, 9);
            Assert.Equal(0.0, r.Compare.DirectionDeg.Value, 9);
        }

        [Fact]
        public void OffsetPlume_FailsLateral_Only()
        {
            FxPlume offset = Intact.Clone();
            offset.Lateral = -7.0;
            PartParityFxViewResult r = Measure(Intact, offset);
            Assert.Equal(new[] { PartParityFxRules.MeasureLateral }, r.FailingMeasures(false));
            Assert.Equal(-7.0, r.Compare.LateralOffsetPx.Value, 9);
        }

        [Fact]
        public void ShortPlume_FailsLength_Only()
        {
            FxPlume shortPlume = Intact.Clone();
            shortPlume.Length = Intact.Length * 0.5;
            PartParityFxViewResult r = Measure(Intact, shortPlume);
            Assert.Equal(new[] { PartParityFxRules.MeasureLength }, r.FailingMeasures(false));
            Assert.InRange(r.Compare.LengthRatio.Value, 0.4, 0.6);
            Assert.Equal(1.0, r.Compare.WidthRatio.Value, 9);
        }

        [Fact]
        public void ShortPlume_OnAnRcsCase_IsReportedNotFailed()
        {
            FxPlume shortPlume = Intact.Clone();
            shortPlume.Length = Intact.Length * 0.5;
            Assert.Empty(Measure(Intact, shortPlume).FailingMeasures(true));
        }

        [Fact]
        public void HueRotatedPlume_FailsColour_Only_WithCoverageExactlyUnchanged()
        {
            FxPlume rotated = Intact.Clone();
            rotated.RotateHue = true;
            PartParityFxViewResult r = Measure(Intact, rotated);
            Assert.Equal(new[] { PartParityFxRules.MeasureColour }, r.FailingMeasures(false));
            Assert.Equal(r.Real.CorePx, r.Ghost.CorePx);
            Assert.Equal(r.Real.EnvelopePx, r.Ghost.EnvelopePx);
            Assert.Equal(r.Real.LengthPx, r.Ghost.LengthPx);
            Assert.Equal(1.0, r.Compare.CoreIou);
            Assert.True(r.Compare.ChromaDistance.Value > PartParityFxRules.ChromaFail);
        }

        [Fact]
        public void SmokeRemoved_FailsSmoke_AndTheFlameStillPasses()
        {
            FxPlume smoke = Smoky(Intact);
            var r = PartParityFxViewResult.Measure(SideA, PartParityStateRules.RouteEvent, Smoke, smoke.Axis(),
                smoke.Samples(Smoke, N), FxPlume.Empty(Smoke, smoke.Tile, N));
            Assert.Equal(new[] { PartParityFxRules.MeasureSmoke }, r.FailingMeasures(false));
            Assert.Equal(PartParityFxRules.ReasonSmokeMissing, PartParityFxMetrics.PresenceReason(Smoke, r.Real.CorePx, r.Ghost.CorePx));
            FxPlume other = Intact.Clone();
            other.Seed = 9;
            Assert.Empty(Measure(Intact, other).FailingMeasures(false));
        }

        [Fact]
        public void GhostPlumeMissingOrExtra_FailsPresence_Only()
        {
            var missing = PartParityFxViewResult.Measure(SideA, PartParityStateRules.RouteEvent, Flame, Intact.Axis(),
                Intact.Samples(Flame, N), FxPlume.Empty(Flame, Intact.Tile, N));
            Assert.Equal(new[] { PartParityFxRules.MeasurePresence }, missing.FailingMeasures(false));
            Assert.Equal(PartParityFxRules.ReasonFxMissing, PartParityFxMetrics.PresenceReason(Flame, missing.Real.CorePx, missing.Ghost.CorePx));
            Assert.Equal(0.0, missing.Compare.CoreIou);
            var extra = PartParityFxViewResult.Measure(SideA, PartParityStateRules.RouteEvent, Flame, Intact.Axis(),
                FxPlume.Empty(Flame, Intact.Tile, N), Intact.Samples(Flame, N));
            Assert.Equal(new[] { PartParityFxRules.MeasurePresence }, extra.FailingMeasures(false));
            Assert.Equal(PartParityFxRules.ReasonFxExtra, PartParityFxMetrics.PresenceReason(Flame, extra.Real.CorePx, extra.Ghost.CorePx));
        }

        [Fact]
        public void PresenceReason_UnderTheMinimum_IsNeither()
        {
            Assert.Null(PartParityFxMetrics.PresenceReason(Flame, PartParityFxRules.PresenceMinPx - 1, 0));
            Assert.Null(PartParityFxMetrics.PresenceReason(Flame, 200, 1));
            Assert.Null(PartParityFxMetrics.PresenceReason(Smoke, PartParityFxRules.SmokeMinPx - 1, 0));
            Assert.Equal(PartParityFxRules.ReasonSmokeExtra, PartParityFxMetrics.PresenceReason(Smoke, 0, PartParityFxRules.SmokeMinPx));
        }

        /// <summary>A comparison over every fail line (the Python cell's WILD).</summary>
        private static PartParityFxCompare Wild() => new PartParityFxCompare
        {
            OriginDeltaPx = -29.0, LateralOffsetPx = 40.0, LengthRatio = 1.373, WidthRatio = 3.0, DirectionDeg = 50.0,
            CrossSectionAreaRatio = 9.0, CrossSectionOffset = 2.0, ChromaDistance = 0.5, HueL1 = 1.5,
            BrightnessRatio = 9.0, CoreIou = 0.0,
        };

        private static List<string> FailingOf(string layer, int realCore, int ghostCore)
            => PartParityFxMetrics.FailingMeasures(layer,
                new PartParityFxSideMeasures { CorePx = realCore, EnvelopePx = 1570, LengthPx = 100.0 },
                new PartParityFxSideMeasures { CorePx = ghostCore, EnvelopePx = 3680, LengthPx = 137.3 }, Wild(), false);

        [Theory]
        [InlineData(0, 0)]
        [InlineData(63, 63)]
        [InlineData(63, 0)]
        [InlineData(0, 63)]
        public void TwoWeakCores_JudgePresenceOnly(int real, int ghost)
        {
            foreach (string layer in new[] { Flame, Smoke })
                Assert.Empty(FailingOf(layer, real, ghost));
        }

        [Theory]
        [InlineData(64, 63)]
        [InlineData(63, 64)]
        [InlineData(64, 64)]
        public void OneCoreAtTheMinimum_JudgesEveryMeasure(int real, int ghost)
        {
            foreach (string layer in new[] { Flame, Smoke })
                Assert.Equal(new[]
                {
                    PartParityFxRules.MeasureOrigin, PartParityFxRules.MeasureLateral, PartParityFxRules.MeasureLength,
                    PartParityFxRules.MeasureWidth, PartParityFxRules.MeasureDirection, PartParityFxRules.MeasureCrossSection,
                    PartParityFxRules.MeasureColour,
                }, FailingOf(layer, real, ghost));
        }

        [Theory]
        [InlineData(63, 63, true)]
        [InlineData(0, 0, true)]
        [InlineData(64, 0, false)]
        [InlineData(0, 64, false)]
        public void TwoWeakCores_NeverReadAsUnsettled(int real, int ghost, bool settled)
        {
            var r = new PartParityFxViewResult
            {
                Layer = Smoke,
                Real = new PartParityFxSideMeasures { CorePx = real },
                Ghost = new PartParityFxSideMeasures { CorePx = ghost },
                RealEven = new PartParityFxSideMeasures { LengthPx = 21.0 },
                RealOdd = new PartParityFxSideMeasures { LengthPx = 12.0 },
            };
            Assert.Equal(settled, r.SplitHalfDisagreement() == null);
        }

        [Theory]
        [InlineData(64, 0)]
        [InlineData(0, 64)]
        public void AMissingOrExtraPlumeAtTheMinimum_FailsPresence(int real, int ghost)
        {
            Assert.Contains(PartParityFxRules.MeasurePresence, FailingOf(Flame, real, ghost));
            Assert.Contains(PartParityFxRules.MeasureSmoke, FailingOf(Smoke, real * 4, ghost * 4));
        }

        [Fact]
        public void BothPlumesEmpty_FailsNothing_IouIsOne()
        {
            var r = PartParityFxViewResult.Measure(SideA, PartParityStateRules.RouteEvent, Flame, Intact.Axis(),
                FxPlume.Empty(Flame, 32, N), FxPlume.Empty(Flame, 32, N));
            Assert.Empty(r.FailingMeasures(false));
            Assert.Equal(1.0, r.Compare.CoreIou);
            Assert.Null(r.Compare.LengthRatio);
            Assert.Null(r.Compare.BrightnessRatio);
            Assert.Null(r.Compare.HueL1);
            Assert.Null(r.Compare.DirectionDeg);
        }

        [Fact]
        public void TurnedPlume_MovesDirection_ATranslationDoesNot()
        {
            FxPlume turned = Intact.Clone();
            double a = 20.0 * Math.PI / 180.0;
            turned.DirX = Math.Cos(a);
            turned.DirY = Math.Sin(a);
            turned.OriginY = 24.0;
            PartParityFxViewResult r = Measure(Intact, turned);
            Assert.Contains(PartParityFxRules.MeasureDirection, r.FailingMeasures(false));
            Assert.InRange(r.Compare.DirectionDeg.Value, 18.0, 22.0);
        }

        [Fact]
        public void WrapDeg_KeepsTheShortWay()
        {
            Assert.Equal(-10.0, PartParityFxMetrics.WrapDeg(350.0), 12);
            Assert.Equal(180.0, PartParityFxMetrics.WrapDeg(-180.0), 12);
            Assert.Equal(170.0, PartParityFxMetrics.WrapDeg(-190.0), 12);
        }

        [Fact]
        public void CoreDirectionDeg_ReadsTheHalvesLine_NullWithAnEmptyHalf()
        {
            // A 1 x 3 column (x = 0, y = 0..2) against a downward axis, then a sideways one.
            var column = new[] { true, true, true };
            double[] c = { 0.5, 1.5 };
            Assert.Equal(0.0, PartParityFxMetrics.CoreDirectionDeg(column, 1, c, 0.5, 0.0, 0.0, 1.0).Value, 12);
            // Across the axis every pixel sits at one t: no upstream half.
            Assert.Null(PartParityFxMetrics.CoreDirectionDeg(column, 1, c, 0.0, 0.0, 1.0, 0.0));
            // One pixel: no upstream half.
            Assert.Null(PartParityFxMetrics.CoreDirectionDeg(new[] { true }, 1, new[] { 0.5, 0.5 }, 0.0, 0.0, 1.0, 0.0));
        }

        [Theory]
        [InlineData(12.0, 5.0)]
        [InlineData(10.0, 5.0)]
        [InlineData(9.0, 4.0)]
        public void ARoundCore_AgainstItself_NeverFailsDirection(double length, double halfWidth)
        {
            // The noise floor for a compact core (an RCS puff, a short flame): the even samples
            // against the odd ones, and two seeds against each other, over 40 seeds.
            double worst = 0.0;
            for (uint seed = 1; seed <= 40; seed++)
            {
                var p = new FxPlume { Seed = seed, Length = length, HalfWidth = halfWidth };
                var q = p.Clone();
                q.Seed = seed + 1000;
                PartParityFxViewResult r = Measure(p, q);
                Assert.DoesNotContain(PartParityFxRules.MeasureDirection, r.FailingMeasures(false));
                Assert.DoesNotContain(PartParityFxRules.MeasureDirection,
                    PartParityFxMetrics.FailingMeasures(Flame, r.RealEven, r.RealOdd,
                        PartParityFxMetrics.Compare(r.RealEven, r.RealOdd, SideA, p.Axis()), false));
                worst = Math.Max(worst, Math.Abs(r.Compare.DirectionDeg ?? 0.0));
            }
            Assert.True(worst < PartParityFxRules.DirectionFailDeg, "worst direction " + worst);
        }

        [Fact]
        public void BrightnessAndCoreIou_AreWarnOnly()
        {
            FxPlume dim = Intact.Clone();
            dim.R *= 0.3;
            dim.G *= 0.3;
            dim.B *= 0.3;
            PartParityFxViewResult r = Measure(Intact, dim);
            Assert.InRange(r.Compare.BrightnessRatio.Value, 0.2, 0.45);
            Assert.DoesNotContain(PartParityFxRules.MeasureBrightness, r.FailingMeasures(false));
            Assert.DoesNotContain(PartParityFxRules.MeasureCoreIou, r.FailingMeasures(false));
        }

        // ----- the axis view -----

        private static FxPlume Disc(double radius, double dx = 0.0, double dy = 0.0)
            => new FxPlume { OriginX = 32, OriginY = 32, DiscRadius = radius, DiscDx = dx, DiscDy = dy, Seed = 3 };

        [Fact]
        public void Bottom_AMatchingCrossSection_FailsNothing_AndHasNoAxisMeasures()
        {
            FxPlume other = Disc(10.0);
            other.Seed = 4;
            PartParityFxViewResult r = Measure(Disc(10.0), other, Flame, Bottom);
            Assert.Empty(r.FailingMeasures(false));
            Assert.Null(r.Real.OriginPx);
            Assert.Null(r.Real.LengthPx);
            Assert.Null(r.Compare.OriginDeltaPx);
            Assert.Null(r.Compare.LateralOffsetPx);
            Assert.Null(r.Compare.DirectionDeg);
            Assert.InRange(r.Compare.CrossSectionAreaRatio.Value, 0.9, 1.1);
            Assert.InRange(r.Compare.CrossSectionOffset.Value, 0.0, 0.05);
        }

        [Fact]
        public void Bottom_ASmallerOrOffsetCrossSection_FailsCrossSection_Only()
        {
            PartParityFxViewResult small = Measure(Disc(10.0), Disc(6.0), Flame, Bottom);
            Assert.Equal(new[] { PartParityFxRules.MeasureCrossSection }, small.FailingMeasures(false));
            Assert.True(small.Compare.CrossSectionAreaRatio.Value < PartParityFxRules.CrossSectionAreaFailMin);
            PartParityFxViewResult offset = Measure(Disc(10.0), Disc(10.0, 3.0, 0.0), Flame, Bottom);
            Assert.Equal(new[] { PartParityFxRules.MeasureCrossSection }, offset.FailingMeasures(false));
            Assert.Equal(0.3, offset.Compare.CrossSectionOffset.Value, 1);
        }

        [Fact]
        public void Bottom_WithNoNozzleDiameter_LeavesTheOffsetNull()
        {
            FxPlume d = Disc(10.0);
            var r = PartParityFxViewResult.Measure(Bottom, PartParityStateRules.RouteEvent, Flame, d.Axis(null),
                d.Samples(Flame, N), Disc(10.0, 3.0).Samples(Flame, N));
            Assert.Null(r.Compare.CrossSectionOffset);
            Assert.NotNull(r.Compare.CrossSectionAreaRatio);
        }

        // ----- split halves -----

        [Fact]
        public void SplitHalves_ASteadyPlume_Settles()
        {
            Assert.Null(Measure(Intact, Intact.Clone()).SplitHalfDisagreement());
        }

        [Fact]
        public void SplitHalves_AnAlternatingPlume_DisagreesOnPresence()
        {
            var set = new PartParityFxSampleSet(Flame, Intact.Tile, Intact.Tile);
            for (int i = 0; i < N; i++) set.Add(i % 2 == 0 ? Intact.Sample(i) : new byte[Intact.Tile * Intact.Tile * 4]);
            var r = PartParityFxViewResult.Measure(SideA, PartParityStateRules.RouteEvent, Flame, Intact.Axis(),
                set, Intact.Samples(Flame, N));
            Assert.StartsWith(PartParityFxRules.SideReal + " presence even=", r.SplitHalfDisagreement());
            Assert.Equal(N / 2, r.RealEven.Samples);
            Assert.Equal(0, r.RealOdd.CorePx);
        }

        [Fact]
        public void SplitHalves_AGrowingPlume_DisagreesOnLength()
        {
            FxPlume longer = Intact.Clone();
            longer.Length = Intact.Length * 1.4;
            var set = new PartParityFxSampleSet(Flame, Intact.Tile, Intact.Tile);
            for (int i = 0; i < N; i++) set.Add(i % 2 == 0 ? Intact.Sample(i) : longer.Sample(i));
            var r = PartParityFxViewResult.Measure(SideA, PartParityStateRules.RouteEvent, Flame, Intact.Axis(),
                Intact.Samples(Flame, N), set);
            Assert.StartsWith(PartParityFxRules.SideGhost + " length even=", r.SplitHalfDisagreement());
        }

        [Fact]
        public void SplitHalves_RealVsReal_IsTheNoiseFloor()
        {
            // ctl-fx-real-vs-real: the real side's even samples against its odd ones.
            PartParityFxSampleSet set = Intact.Samples(Flame, N);
            PartParityFxAxis axis = Intact.Axis();
            PartParityFxSideMeasures even = PartParityFxMetrics.MeasureTile(set.Even.ToTile(), Intact.Tile, Intact.Tile, set.Even.Samples, SideA, axis);
            PartParityFxSideMeasures odd = PartParityFxMetrics.MeasureTile(set.Odd.ToTile(), Intact.Tile, Intact.Tile, set.Odd.Samples, SideA, axis);
            PartParityFxCompare c = PartParityFxMetrics.Compare(even, odd, SideA, axis);
            Assert.Empty(PartParityFxMetrics.FailingMeasures(Flame, even, odd, c, false));
        }

        // ----- particles -----

        private static PartParityFxParticle P(double x, double y, double z, double vx = 0, double vy = 0, double vz = 0, double size = 1.0)
            => new PartParityFxParticle { Position = new[] { x, y, z }, Velocity = new[] { vx, vy, vz }, Size = size };

        [Fact]
        public void ReduceParticles_NearParticlesOnly_NearestRankP95()
        {
            var s1 = new List<PartParityFxParticle>();
            for (int i = 1; i <= 20; i++) s1.Add(P(0, -i, 0, 0, -10, 0, 2.0));
            s1.Add(P(0, -5000, 0));
            var s2 = new List<PartParityFxParticle> { P(3, -4, 0, 10, 0, 0, 4.0), P(0, -4, 3, 10, 0, 0, 4.0), P(-3, -4, 0, 10, 0, 0, 4.0) };
            var st = PartParityFxMetrics.ReduceParticles(new List<IList<PartParityFxParticle>> { s1, s2 },
                new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, -2.0, 0.0 }, PartParityFxRules.FramingNearParticleM);
            Assert.Equal(12.0, st.Count);
            Assert.Equal(23, st.Near);
            Assert.Equal(10.0, st.Speed.Value, 12);
            Assert.Equal(270.0 / 23.0, st.DirectionDeg.Value, 9);
            Assert.Equal((20 * 2.0 + 3 * 4.0) / 23.0, st.Size.Value, 12);
            Assert.Equal(19.0, st.AlongP95.Value, 12);
            Assert.Equal(3.0, st.AcrossP95.Value, 12);
        }

        [Fact]
        public void ReduceParticles_NullSamplesAndVelocities_LeaveTheDenominators()
        {
            var s1 = new List<PartParityFxParticle> { P(0, -1, 0, 0, -4, 0), new PartParityFxParticle { Position = new[] { 0.0, -2.0, 0.0 } } };
            var st = PartParityFxMetrics.ReduceParticles(new List<IList<PartParityFxParticle>> { s1, null },
                new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, -1.0, 0.0 }, PartParityFxRules.FramingNearParticleM);
            Assert.Equal(2.0, st.Count);
            Assert.Equal(2, st.Near);
            Assert.Equal(4.0, st.Speed.Value, 12);
            Assert.Equal(0.0, st.DirectionDeg.Value, 12);
        }

        [Fact]
        public void ReduceParticles_NothingNear_LeavesTheStatisticsNull()
        {
            var st = PartParityFxMetrics.ReduceParticles(
                new List<IList<PartParityFxParticle>> { new List<PartParityFxParticle> { P(0, -900, 0) } },
                new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, -1.0, 0.0 }, PartParityFxRules.FramingNearParticleM);
            Assert.Equal(1.0, st.Count);
            Assert.Null(st.Speed);
            Assert.Null(st.AlongP95);
            Assert.Null(PartParityFxMetrics.ReduceParticles(null, null, null, 1.0).Count);
        }

        // ----- report lines and the atlas rows -----

        [Fact]
        public void FormatRcsReportedOnly_IsInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var c = new PartParityFxCompare { LengthRatio = 1.5, WidthRatio = null, BrightnessRatio = 0.25 };
                Assert.Equal("rcs reported-only event:fx-side-a:flame length=1.500 width=null brightness=0.250 power=2.000",
                    PartParityFxMetrics.FormatRcsReportedOnly("event", SideA, Flame, c, 2.0));
                Assert.EndsWith("power=null", PartParityFxMetrics.FormatRcsReportedOnly("event", SideA, Flame, c, double.NaN));
            }
            finally { Thread.CurrentThread.CurrentCulture = saved; }
        }

        [Fact]
        public void AtlasRowFor_CoversEveryRcsRow_AndNothingElse()
        {
            var produced = new List<string>();
            foreach (string layer in PartParityFxRules.FxLayers)
                foreach (string side in PartParityFxRules.Sides)
                    produced.Add(PartParityFxMetrics.AtlasRowFor(side, layer));
            Assert.Equal(PartParityJson.RcsFxAtlasRows.OrderBy(s => s, StringComparer.Ordinal),
                produced.OrderBy(s => s, StringComparer.Ordinal));
            Assert.Null(PartParityFxMetrics.AtlasRowFor(PartParityFxRules.SideReal, PartParityFxRules.LayerBody));
            Assert.Null(PartParityFxMetrics.AtlasRowFor("nope", Flame));
        }

        // ----- JSON -----

        private static List<string> Keys(object obj) => ((Dictionary<string, object>)obj).Keys.ToList();

        [Fact]
        public void ViewJson_WritesEveryKeyOfEveryTable()
        {
            PartParityFxViewResult r = Measure(Intact, FxPlume_Short());
            object o = PartParityJsonReader.Parse(r.Json());
            Assert.Equal(PartParityJson.FxViewKeys, Keys(o));
            var d = (Dictionary<string, object>)o;
            Assert.Equal(PartParityJson.FxAxisKeys, Keys(d["axis"]));
            Assert.Equal(PartParityJson.FxSideMeasureKeys, Keys(d["real"]));
            Assert.Equal(PartParityJson.FxSideMeasureKeys, Keys(d["ghost"]));
            Assert.Equal(PartParityJson.FxCompareKeys, Keys(d["compare"]));
            Assert.Equal(PartParityJson.FxSplitHalfKeys, Keys(d["splitHalf"]));
            var even = ((Dictionary<string, object>)d["splitHalf"])["even"];
            Assert.Equal(PartParityJson.FxHalfKeys, Keys(even));
            Assert.Equal(8.0, PartParityJsonReader.Num(even, "samples"));
            Assert.Equal((double)r.Real.CorePx, PartParityJsonReader.Num(d["real"], "corePx"));
            Assert.Equal(r.Compare.LengthRatio, PartParityJsonReader.Num(d["compare"], "lengthRatio"));
            Assert.Equal(PartParityFxRules.HueBins, ((List<object>)((Dictionary<string, object>)d["real"])["hueHistogram"]).Count);
        }

        [Fact]
        public void SideJson_AnEmptyTile_WritesNullsNotZeros()
        {
            PartParityFxSideMeasures m = PartParityFxMetrics.MeasureTile(new byte[16], 2, 2, N, SideA, Intact.Axis());
            var d = (Dictionary<string, object>)PartParityJsonReader.Parse(PartParityFxMetrics.SideJson(m));
            Assert.Equal(0.0, PartParityJsonReader.Num(d, "corePx"));
            Assert.Null(d["originPx"]);
            Assert.Null(d["coreCentroid"]);
            Assert.Null(d["meanColor"]);
            Assert.Equal("null", PartParityFxMetrics.SideJson(null));
            Assert.Equal("null", PartParityFxMetrics.CompareJson(null));
            Assert.Equal("null", PartParityFxMetrics.AxisJson(null));
            Assert.Equal(PartParityJson.FxParticleKeys,
                Keys(PartParityJsonReader.Parse(PartParityFxMetrics.ParticlesJson(new PartParityFxParticleStats()))));
        }

        private static FxPlume FxPlume_Short()
        {
            FxPlume s = Intact.Clone();
            s.Length = 20;
            return s;
        }

        // ----- the measures read the written bytes only -----

        [Fact]
        public void Measures_FromTheAtlasCrop_EqualTheMeasuresFromTheTile()
        {
            PartParityFxViewResult r = Measure(Intact, FxPlume_Short());
            int tile = Intact.Tile;
            byte[] atlas = PartParityImage.BuildAtlas(new[] { new[] { r.RealTile }, new[] { r.GhostTile } }, 1, tile, out int w, out _);
            byte[] png = PartParityPng.EncodeRgba(w, 2 * tile, atlas);
            byte[] decoded = FxPngReader.DecodeRgba(png, out int dw, out int dh);
            Assert.Equal(atlas, decoded);
            PartParityFxSideMeasures real = PartParityFxMetrics.MeasureTile(FxPngReader.Crop(decoded, dw, 0, 0, tile), tile, tile, N, SideA, r.Axis);
            PartParityFxSideMeasures ghost = PartParityFxMetrics.MeasureTile(FxPngReader.Crop(decoded, dw, 1, 0, tile), tile, tile, N, SideA, r.Axis);
            Assert.Equal(PartParityFxMetrics.SideJson(r.Real), PartParityFxMetrics.SideJson(real));
            Assert.Equal(PartParityFxMetrics.SideJson(r.Ghost), PartParityFxMetrics.SideJson(ghost));
            Assert.Equal(PartParityFxMetrics.CompareJson(r.Compare),
                PartParityFxMetrics.CompareJson(PartParityFxMetrics.Compare(real, ghost, SideA, r.Axis)));
        }

        // ----- the committed fixture (F4's Python recompute reads it) -----

        private static string RepoRoot() => SyntheticRecordingTests.ResolveProjectRoot();

        internal sealed class WriteFxFixtureFactAttribute : FactAttribute
        {
            public WriteFxFixtureFactAttribute()
            {
                if (Environment.GetEnvironmentVariable("PARSEK_WRITE_PART_PARITY_FX_FIXTURE") != "1")
                    Skip = "set PARSEK_WRITE_PART_PARITY_FX_FIXTURE=1 to rewrite Source/Parsek.Tests/Fixtures/PartParity/fx/";
            }
        }

        [Trait("Category", "Manual")]
        [WriteFxFixtureFact]
        public void WriteFxFixture()
        {
            string target = PartParityFxFixture.TargetDir(RepoRoot());
            Directory.CreateDirectory(target);
            foreach (string old in Directory.GetFiles(target)) File.Delete(old);
            foreach (var kv in PartParityFxFixture.BuildFiles(out _))
                File.WriteAllBytes(Path.Combine(target, kv.Key), kv.Value);
        }

        [Fact]
        public void Fixture_ExercisesEveryMeasureItClaims()
        {
            PartParityFxFixture.Built b = PartParityFxFixture.Build();
            Assert.Equal(PartParityFxRules.Views.Length * PartParityFxRules.FxLayers.Length * 2, b.Entries.Count);
            var failing = new HashSet<string>();
            foreach (PartParityFxViewResult e in b.Entries) failing.UnionWith(e.FailingMeasures(false));
            foreach (string m in new[]
                     {
                         PartParityFxRules.MeasureLength, PartParityFxRules.MeasureColour, PartParityFxRules.MeasureOrigin,
                         PartParityFxRules.MeasureLateral, PartParityFxRules.MeasureSmoke, PartParityFxRules.MeasureCrossSection,
                     })
                Assert.Contains(m, failing);
            Assert.Contains(b.Entries, e => e.Real.SaturatedPx > 0 && e.Real.SaturatedPx < e.Real.CorePx);
            Assert.Contains(b.Entries, e => e.Ghost.CorePx == 0);
        }

        [Fact]
        public void Fixture_CommittedFilesMatchTheBuilder()
        {
            string target = PartParityFxFixture.TargetDir(RepoRoot());
            Assert.True(Directory.Exists(target), "fixture missing: " + target
                + " (write it with PARSEK_WRITE_PART_PARITY_FX_FIXTURE=1 dotnet test --filter WriteFxFixture)");
            SortedDictionary<string, byte[]> expected = PartParityFxFixture.BuildFiles(out _);
            var committed = Directory.GetFiles(target).Select(Path.GetFileName).OrderBy(s => s, StringComparer.Ordinal).ToList();
            Assert.Equal(expected.Keys.ToList(), committed);
            string id = PartParityFxFixture.CaseId();
            // The pixels, not the deflate bytes (a runtime's deflate output may differ).
            byte[] want = FxPngReader.DecodeRgba(expected[TestCommandPartParitySweep.FxAtlasFileName(id)], out int ww, out int wh);
            byte[] got = FxPngReader.DecodeRgba(File.ReadAllBytes(Path.Combine(target, TestCommandPartParitySweep.FxAtlasFileName(id))),
                out int gw, out int gh);
            Assert.Equal((ww, wh), (gw, gh));
            Assert.True(want.SequenceEqual(got), "the committed FX atlas differs from a fresh build - rebuild it, never hand-edit it");
            // The sidecar, as numbers (a runtime's "R" digits may differ in the last place).
            object a = PartParityJsonReader.Parse(System.Text.Encoding.UTF8.GetString(expected[TestCommandPartParitySweep.SidecarFileName(id)]));
            object c = PartParityJsonReader.Parse(File.ReadAllText(Path.Combine(target, TestCommandPartParitySweep.SidecarFileName(id))));
            AssertJsonEqual(a, c, "$");
        }

        [Fact]
        public void Fixture_EveryRecordedMeasure_RecomputesFromTheCommittedAtlas()
        {
            // What F4's Python judge does: read the PNG and the sidecar, crop each entry's two
            // tiles, measure, and compare with the sidecar.
            string target = PartParityFxFixture.TargetDir(RepoRoot());
            string id = PartParityFxFixture.CaseId();
            byte[] atlas = FxPngReader.DecodeRgba(File.ReadAllBytes(Path.Combine(target, TestCommandPartParitySweep.FxAtlasFileName(id))),
                out int w, out _);
            object sidecar = PartParityJsonReader.Parse(File.ReadAllText(Path.Combine(target, TestCommandPartParitySweep.SidecarFileName(id))));
            var fx = PartParityJsonReader.Obj(sidecar, "fx");
            var meta = PartParityJsonReader.Obj(sidecar, "fxAtlas");
            int tile = PartParityJsonReader.Int(meta, "tile");
            int n = PartParityJsonReader.Int(fx, "samples");
            List<string> rows = PartParityJsonReader.Arr(meta, "rows").Cast<string>().ToList();
            List<string> cols = PartParityJsonReader.Arr(meta, "cols").Cast<string>().ToList();
            List<object> views = PartParityJsonReader.Arr(fx, "views");
            Assert.Equal(16, views.Count);
            foreach (object v in views)
            {
                string view = PartParityJsonReader.Str(v, "view"), route = PartParityJsonReader.Str(v, "route"), layer = PartParityJsonReader.Str(v, "layer");
                var ax = PartParityJsonReader.Obj(v, "axis");
                var axis = new PartParityFxAxis
                {
                    OriginPx = NumArray(ax, "originPx"), DirPx = NumArray(ax, "dirPx"),
                    NozzleDiameterPx = PartParityJsonReader.Num(ax, "nozzleDiameterPx"),
                };
                string ghostSide = route == PartParityStateRules.RouteStart ? PartParityFxRules.SideGhostStart : PartParityFxRules.SideGhost;
                int c = cols.IndexOf(view);
                PartParityFxSideMeasures real = PartParityFxMetrics.MeasureTile(
                    FxPngReader.Crop(atlas, w, rows.IndexOf(PartParityFxMetrics.AtlasRowFor(PartParityFxRules.SideReal, layer)), c, tile),
                    tile, tile, n, view, axis);
                PartParityFxSideMeasures ghost = PartParityFxMetrics.MeasureTile(
                    FxPngReader.Crop(atlas, w, rows.IndexOf(PartParityFxMetrics.AtlasRowFor(ghostSide, layer)), c, tile),
                    tile, tile, n, view, axis);
                string at = view + "/" + route + "/" + layer;
                AssertJsonEqual(PartParityJsonReader.Obj(v, "real"), PartParityJsonReader.Parse(PartParityFxMetrics.SideJson(real)), at + ".real");
                AssertJsonEqual(PartParityJsonReader.Obj(v, "ghost"), PartParityJsonReader.Parse(PartParityFxMetrics.SideJson(ghost)), at + ".ghost");
                AssertJsonEqual(PartParityJsonReader.Obj(v, "compare"),
                    PartParityJsonReader.Parse(PartParityFxMetrics.CompareJson(PartParityFxMetrics.Compare(real, ghost, view, axis))), at + ".compare");
            }
        }

        private static double[] NumArray(object o, string key)
            => PartParityJsonReader.Arr(o, key)?.Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();

        private static void AssertJsonEqual(object a, object b, string path)
        {
            if (a == null || b == null) { Assert.True(a == null && b == null, path + ": " + a + " != " + b); return; }
            if (a is Dictionary<string, object> da)
            {
                var db = Assert.IsType<Dictionary<string, object>>(b);
                Assert.Equal(da.Keys.ToList(), db.Keys.ToList());
                foreach (var kv in da) AssertJsonEqual(kv.Value, db[kv.Key], path + "." + kv.Key);
                return;
            }
            if (a is List<object> la)
            {
                var lb = Assert.IsType<List<object>>(b);
                Assert.Equal(la.Count, lb.Count);
                for (int i = 0; i < la.Count; i++) AssertJsonEqual(la[i], lb[i], path + "[" + i + "]");
                return;
            }
            if (a is double x)
            {
                double y = Assert.IsType<double>(b);
                Assert.True(Math.Abs(x - y) <= 1e-12 * Math.Max(1.0, Math.Abs(x)), path + ": " + x.ToString("R", CultureInfo.InvariantCulture)
                    + " != " + y.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            Assert.Equal(a, b);
        }
    }
}
