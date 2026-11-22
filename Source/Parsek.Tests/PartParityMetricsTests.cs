using System;
using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the part parity instrument (<see cref="PartParityMetrics"/>) on
    /// synthetic masks: the double-clear matte, the 1 px tolerance (an edge shift is not a
    /// defect, a real shift is), the 4-connected blobs that catch a small missing piece a
    /// whole-image IoU hides, and the colour measures that fire on a colour-only change.
    /// </summary>
    public class PartParityMetricsTests
    {
        private const int W = 512;
        private const int H = 512;

        private static byte[] Img(int w, int h, Func<int, int, bool> cover,
            Func<int, int, byte[]> colour = null)
        {
            var img = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!cover(x, y)) continue;
                    int i = (y * w + x) * 4;
                    byte[] c = colour != null ? colour(x, y) : new byte[] { 200, 120, 60 };
                    img[i] = c[0];
                    img[i + 1] = c[1];
                    img[i + 2] = c[2];
                    img[i + 3] = 255;
                }
            }
            return img;
        }

        private static Func<int, int, bool> Rect(int x0, int y0, int w, int h)
            => (x, y) => x >= x0 && x < x0 + w && y >= y0 && y < y0 + h;

        private static PartParityViewMeasures Measure(byte[] real, byte[] ghost, double area = 0.0)
            => PartParityMetrics.Measure(real, ghost, W, H, area);

        // ----- matte -----

        [Fact]
        public void ComposeMatte_OpaqueTransparentAndHalfPixels()
        {
            // pixel 0 opaque (same on both clears), pixel 1 empty (clear colour shows),
            // pixel 2 half covered, pixel 3 inverted (white render darker than black).
            byte[] black = { 100, 50, 25, 255, 0, 0, 0, 255, 50, 50, 50, 255, 90, 90, 90, 255 };
            byte[] white = { 100, 50, 25, 255, 255, 255, 255, 255, 178, 178, 178, 255, 80, 80, 80, 255 };
            byte[] rgba = PartParityMetrics.ComposeMatte(black, white, out PartParityMatteStats stats);

            Assert.Equal(new byte[] { 100, 50, 25, 255 }, Slice(rgba, 0));
            Assert.Equal(new byte[] { 0, 0, 0, 0 }, Slice(rgba, 1));
            // alpha = 1 - 128/255 = 0.498 -> 127; colour = 50 / 0.498 = 100.4 -> 100
            Assert.Equal(new byte[] { 100, 100, 100, 127 }, Slice(rgba, 2));
            Assert.Equal(255, rgba[15]);
            Assert.Equal(2, stats.Opaque);
            Assert.Equal(1, stats.Edge);
            Assert.Equal(1, stats.Inverted);
        }

        [Fact]
        public void ComposeMatte_InversionWithinTheNoiseFloor_IsNotCounted()
        {
            byte[] black = { 100, 100, 100, 255 };
            byte[] white = { 98, 100, 100, 255 };
            PartParityMetrics.ComposeMatte(black, white, out PartParityMatteStats stats);
            Assert.Equal(0, stats.Inverted);
        }

        [Fact]
        public void ComposeMatte_RejectsMismatchedInputs()
        {
            Assert.Throws<ArgumentException>(() =>
                PartParityMetrics.ComposeMatte(new byte[8], new byte[4], out _));
            Assert.Throws<ArgumentNullException>(() =>
                PartParityMetrics.ComposeMatte(null, new byte[4], out _));
        }

        private static byte[] Slice(byte[] rgba, int pixel)
            => new[] { rgba[pixel * 4], rgba[pixel * 4 + 1], rgba[pixel * 4 + 2], rgba[pixel * 4 + 3] };

        // ----- shape -----

        [Fact]
        public void Identical_MeasuresNoMismatchAndNoColour()
        {
            byte[] a = Img(W, H, Rect(100, 120, 200, 150));
            var m = Measure(a, (byte[])a.Clone(), 40000);
            Assert.Equal(30000, m.RealPx);
            Assert.Equal(30000, m.GhostPx);
            Assert.Equal(30000, m.AndPx);
            Assert.Equal(30000, m.OrPx);
            Assert.Equal(0, m.RealOnlyPx);
            Assert.Equal(0, m.GhostOnlyPx);
            Assert.Equal(0, m.RealOnlyTolPx);
            Assert.Equal(0, m.RealOnlyBlobPx);
            Assert.Equal(0, m.GhostOnlyBlobPx);
            Assert.Equal(0.0, m.RealOnlyBlobPct);
            Assert.Equal(1.0, m.Iou);
            Assert.Equal(0, m.ColorOverPx);
            Assert.Equal(0.0, m.ColorP99);
            Assert.Equal(0.0, m.ColorBlockMax);
            Assert.Equal(m.BboxReal, m.BboxGhost);
            Assert.Equal(new[] { 100, 120, 200, 150 }, m.BboxReal);
        }

        [Theory]
        [InlineData(1, 0)]
        [InlineData(-1, 0)]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        public void OnePixelEdgeShift_IsNotADefect(int dx, int dy)
        {
            byte[] real = Img(W, H, Rect(100, 100, 200, 200));
            byte[] ghost = Img(W, H, Rect(100 + dx, 100 + dy, 200, 200));
            var m = Measure(real, ghost, 40000);
            Assert.True(m.RealOnlyPx > 0);
            Assert.True(m.GhostOnlyPx > 0);
            Assert.Equal(0, m.RealOnlyTolPx);
            Assert.Equal(0, m.GhostOnlyTolPx);
            Assert.Equal(0, m.RealOnlyBlobPx);
            Assert.Equal(0, m.GhostOnlyBlobPx);
            Assert.True(m.Iou < 1.0);
        }

        [Fact]
        public void OnePixelThinAntenna_ShiftedByOnePixel_IsNotADefect()
        {
            // The design's case: a 1 px wide antenna whose edge lands one column over.
            byte[] real = Img(W, H, Rect(200, 50, 1, 300));
            byte[] ghost = Img(W, H, Rect(201, 50, 1, 300));
            var m = Measure(real, ghost);
            Assert.Equal(0.0, m.Iou);
            Assert.Equal(0, m.RealOnlyTolPx);
            Assert.Equal(0, m.GhostOnlyTolPx);
        }

        [Fact]
        public void ShapeShiftedSeveralPixels_LeavesAColumnBlobOnEachSide()
        {
            byte[] real = Img(W, H, Rect(50, 50, 100, 100));
            byte[] ghost = Img(W, H, Rect(56, 50, 100, 100));
            var m = Measure(real, ghost, 10000);
            // Real-only columns 50..55; column 55 touches ghost column 56 and is forgiven.
            Assert.Equal(600, m.RealOnlyPx);
            Assert.Equal(500, m.RealOnlyTolPx);
            Assert.Equal(500, m.RealOnlyBlobPx);
            Assert.Equal(500, m.GhostOnlyTolPx);
            Assert.Equal(500, m.GhostOnlyBlobPx);
            Assert.Equal(5.0, m.RealOnlyBlobPct.Value, 9);
            Assert.Equal(6.0, m.CentroidGhost[0] - m.CentroidReal[0], 9);
        }

        [Fact]
        public void FlippedAsymmetricShape_FailsOnShape()
        {
            // An L: a tall bar plus a foot to the right; the ghost is mirrored left-right
            // about the same centre column.
            Func<int, int, bool> l = (x, y) =>
                (x >= 200 && x < 240 && y >= 100 && y < 400) || (x >= 200 && x < 340 && y >= 360 && y < 400);
            Func<int, int, bool> mirrored = (x, y) => l(539 - x, y);
            var m = Measure(Img(W, H, l), Img(W, H, mirrored), 300 * 140);
            Assert.True(m.RealOnlyBlobPx > 1000, "real-only blob " + m.RealOnlyBlobPx);
            Assert.True(m.GhostOnlyBlobPx > 1000, "ghost-only blob " + m.GhostOnlyBlobPx);
            Assert.True(m.Iou < 0.8);
        }

        [Fact]
        public void RotatedShape_FailsOnShape()
        {
            // A 160 x 40 bar and the same bar turned 90 degrees about the image centre.
            var m = Measure(Img(W, H, Rect(176, 236, 160, 40)), Img(W, H, Rect(236, 176, 40, 160)), 160 * 160);
            Assert.Equal(1600, m.AndPx);
            Assert.True(m.RealOnlyBlobPx > 64);
            Assert.True(m.GhostOnlyBlobPx > 64);
        }

        [Fact]
        public void SmallMissingBlobOnALargeShape_HidesFromIouButNotFromTheBlob()
        {
            // 300 x 300 square; the ghost lacks a 16 x 16 notch at the top edge.
            Func<int, int, bool> square = Rect(100, 100, 300, 300);
            Func<int, int, bool> notched = (x, y) => square(x, y) && !(x >= 200 && x < 216 && y >= 100 && y < 116);
            var m = Measure(Img(W, H, square), Img(W, H, notched), 90000);
            Assert.True(m.Iou > 0.99, "iou " + m.Iou);
            Assert.Equal(256, m.RealOnlyPx);
            // The notch's two side columns and its bottom row touch ghost pixels: 14 x 15 remain.
            Assert.Equal(210, m.RealOnlyTolPx);
            Assert.Equal(210, m.RealOnlyBlobPx);
            Assert.Equal(0, m.GhostOnlyTolPx);
            Assert.Equal(210.0 / 900.0, m.RealOnlyBlobPct.Value, 9);
        }

        [Fact]
        public void BlobsAreFourConnected_DiagonalPixelsAreSeparate()
        {
            var mask = new bool[9];
            mask[0] = true;
            mask[4] = true;
            mask[8] = true;
            Assert.Equal(1, PartParityMetrics.LargestBlob(mask, 3, 3));
            mask[1] = true;
            Assert.Equal(3, PartParityMetrics.LargestBlob(mask, 3, 3)); // 0,1,4
        }

        [Fact]
        public void LargestBlob_HandlesAFullImageWithoutRecursion()
        {
            var mask = new bool[W * H];
            for (int i = 0; i < mask.Length; i++) mask[i] = true;
            Assert.Equal(W * H, PartParityMetrics.LargestBlob(mask, W, H));
        }

        [Fact]
        public void EmptyImages_MeasureEmpty()
        {
            var empty = new byte[W * H * 4];
            var m = Measure(empty, (byte[])empty.Clone(), 1000);
            Assert.Equal(0, m.RealPx);
            Assert.Equal(0, m.GhostPx);
            Assert.Equal(0, m.OrPx);
            Assert.Equal(1.0, m.Iou);
            Assert.Null(m.BboxReal);
            Assert.Null(m.BboxGhost);
            Assert.Null(m.CentroidReal);
            Assert.Null(m.CentroidGhost);
            Assert.False(m.TouchesBorderReal);
            Assert.False(m.TouchesBorderGhost);
            Assert.Equal(0, m.ColorOverlapPx);
            Assert.Equal(0.0, m.ColorP99);
            Assert.Equal(0.0, m.RealOnlyBlobPct);
        }

        [Fact]
        public void GhostAbsent_IsAllRealOnly()
        {
            var m = Measure(Img(W, H, Rect(10, 10, 50, 50)), new byte[W * H * 4], 2500);
            Assert.Equal(0, m.GhostPx);
            Assert.Equal(2500, m.RealOnlyTolPx);
            Assert.Equal(2500, m.RealOnlyBlobPx);
            Assert.Equal(100.0, m.RealOnlyBlobPct.Value, 9);
            Assert.Equal(0.0, m.Iou);
        }

        [Fact]
        public void BboxCentroidAndBorder()
        {
            var m = Measure(Img(W, H, Rect(10, 5, 10, 5)), Img(W, H, Rect(0, 500, 20, 12)));
            Assert.Equal(new[] { 10, 5, 10, 5 }, m.BboxReal);
            Assert.Equal(15.0, m.CentroidReal[0], 9);
            Assert.Equal(7.5, m.CentroidReal[1], 9);
            Assert.False(m.TouchesBorderReal);
            Assert.True(m.TouchesBorderGhost);
        }

        [Fact]
        public void NoBoxArea_LeavesThePercentagesNull()
        {
            var m = Measure(Img(W, H, Rect(10, 10, 5, 5)), new byte[W * H * 4], 0.0);
            Assert.Null(m.RealOnlyBlobPct);
            Assert.Null(m.GhostOnlyBlobPct);
        }

        [Fact]
        public void Measure_RejectsArraysOfTheWrongSize()
        {
            Assert.Throws<ArgumentException>(() =>
                PartParityMetrics.Measure(new byte[16], new byte[16], 3, 1, 0));
            Assert.Throws<ArgumentException>(() =>
                PartParityMetrics.Measure(new byte[0], new byte[0], 0, 0, 0));
        }

        // ----- colour -----

        [Fact]
        public void ColourOnlyDifference_IouStaysOneAndTheColourMeasuresFire()
        {
            Func<int, int, bool> square = Rect(100, 100, 300, 300);
            Func<int, int, bool> patch = Rect(192, 192, 40, 40);
            byte[] real = Img(W, H, square);
            byte[] ghost = Img(W, H, square, (x, y) => patch(x, y)
                ? new byte[] { 40, 120, 60 } : new byte[] { 200, 120, 60 });
            var m = Measure(real, ghost, 90000);
            Assert.Equal(1.0, m.Iou);
            Assert.Equal(0, m.RealOnlyTolPx);
            // Colour reads the interior only: the 300 x 300 square minus its 1 px rim.
            Assert.Equal(298 * 298, m.ColorOverlapPx);
            Assert.Equal(1600, m.ColorOverPx);
            Assert.Equal(1600, m.ColorBlobPx);
            // 1600 of 90000 is 1.8 %: the 99th percentile lands inside the patch.
            Assert.Equal(160.0, m.ColorP99, 9);
            // The patch fully covers the 32 x 32 grid cell at (192, 192).
            Assert.Equal(160.0, m.ColorBlockMax, 9);
        }

        [Fact]
        public void ColourDifferenceBelowTheThreshold_IsNotOverT()
        {
            byte[] real = Img(W, H, Rect(100, 100, 100, 100));
            byte[] ghost = Img(W, H, Rect(100, 100, 100, 100), (x, y) => new byte[] { 210, 125, 60 });
            var m = Measure(real, ghost);
            Assert.Equal(0, m.ColorOverPx);
            Assert.Equal(0, m.ColorBlobPx);
            Assert.True(m.ColorP99 > 0.0 && m.ColorP99 < PartParityMetrics.DefaultColorThreshold);
        }

        [Fact]
        public void BlockMax_IgnoresACellWithTooLittleOverlap()
        {
            // A 3 x 3 overlap has one interior pixel, with a large colour error: it is over
            // T, but its cell is 1/1024 full, so the block measure does not read it as a patch.
            byte[] real = Img(W, H, Rect(5, 5, 3, 3));
            byte[] ghost = Img(W, H, Rect(5, 5, 3, 3), (x, y) => new byte[] { 0, 0, 0 });
            var m = Measure(real, ghost);
            Assert.Equal(1, m.ColorOverPx);
            Assert.Equal(0.0, m.ColorBlockMax);
        }

        [Fact]
        public void SubPixelEdgeShift_WithAntialiasedRim_IsNoColourDefect()
        {
            // An 80 x 80 square with a 1 px antialiased rim (alpha 160), the ghost shifted
            // 1 px: the rim pixels blend differently on the two sides, but colour reads the
            // interior only, so neither shape nor colour reports a defect.
            byte[] Square(int x0)
            {
                var img = Img(W, H, Rect(x0, 100, 80, 80));
                for (int y = 100; y < 180; y++)
                {
                    for (int x = x0; x < x0 + 80; x++)
                    {
                        bool rim = x == x0 || x == x0 + 79 || y == 100 || y == 179;
                        if (rim) img[(y * W + x) * 4 + 3] = 160;
                    }
                }
                return img;
            }
            var m = Measure(Square(100), Square(101));
            Assert.Equal(0, m.RealOnlyBlobPx);
            Assert.Equal(0, m.GhostOnlyBlobPx);
            Assert.Equal(0, m.ColorOverPx);
            Assert.Equal(0, m.ColorBlobPx);
            Assert.True(m.ColorOverlapPx > 0);
        }

        [Fact]
        public void InteriorOfBoth_NeedsTheWholeNeighbourhoodOnBothSides()
        {
            bool[] full = { true, true, true, true, true, true, true, true, true };
            bool[] hole = { true, true, true, true, true, true, true, true, false };
            Assert.True(PartParityMetrics.IsInteriorOfBoth(full, full, 3, 3, 4));
            Assert.False(PartParityMetrics.IsInteriorOfBoth(full, hole, 3, 3, 4));
            Assert.False(PartParityMetrics.IsInteriorOfBoth(full, full, 3, 3, 0));
        }

        [Fact]
        public void PremultipliedDistance_ScalesWithAlpha()
        {
            byte[] a = { 255, 0, 0, 255, 255, 0, 0, 128 };
            byte[] b = { 0, 0, 0, 255, 0, 0, 0, 128 };
            Assert.Equal(255.0, PartParityMetrics.PremultipliedDistance(a, 0, b, 0), 9);
            Assert.Equal(128.0, PartParityMetrics.PremultipliedDistance(a, 4, b, 4), 9);
            byte[] c = { 0, 0, 255, 255 };
            Assert.Equal(Math.Sqrt(2.0) * 255.0, PartParityMetrics.PremultipliedDistance(a, 0, c, 0), 9);
        }

        [Fact]
        public void NearestRankPercentile()
        {
            var values = new List<double>();
            for (int i = 100; i >= 1; i--) values.Add(i);
            Assert.Equal(99.0, PartParityMetrics.NearestRankPercentile(values, 0.99));
            Assert.Equal(0.0, PartParityMetrics.NearestRankPercentile(new List<double>(), 0.99));
            Assert.Equal(7.0, PartParityMetrics.NearestRankPercentile(new List<double> { 7.0 }, 0.99));
        }
    }
}
