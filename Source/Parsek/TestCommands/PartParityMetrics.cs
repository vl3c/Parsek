using System;
using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>Counts from composing one side's matte (<see cref="PartParityMetrics.ComposeMatte"/>).</summary>
    internal struct PartParityMatteStats
    {
        /// <summary>Pixels with alpha &gt;= 0.98.</summary>
        internal int Opaque;

        /// <summary>Pixels with 0.02 &lt; alpha &lt; 0.98 (antialiased edges, translucency).</summary>
        internal int Edge;

        /// <summary>Pixels where the over-white render is darker than the over-black render on
        /// some channel by more than <see cref="PartParityMetrics.InvertedNoiseFloor"/>: a
        /// render that was not deterministic between the two clears.</summary>
        internal int Inverted;
    }

    /// <summary>The raw measures of one view: the per-view block of the case sidecar
    /// (design-part-ghost-parity.md section 5.2 / 6). Coordinates are array coordinates:
    /// x is the column, y the row index of the RGBA array (the capture flips to top-down
    /// before measuring, so they match the atlas).</summary>
    internal sealed class PartParityViewMeasures
    {
        internal int Width;
        internal int Height;

        internal int RealPx;
        internal int GhostPx;
        internal int AndPx;
        internal int OrPx;

        /// <summary>Raw mismatch, before the 1 px tolerance.</summary>
        internal int RealOnlyPx;
        internal int GhostOnlyPx;

        /// <summary>Mismatch with no other-side pixel in the 8-neighbourhood.</summary>
        internal int RealOnlyTolPx;
        internal int GhostOnlyTolPx;

        /// <summary>Largest 4-connected component of the tolerance-filtered mismatch.</summary>
        internal int RealOnlyBlobPx;
        internal int GhostOnlyBlobPx;

        /// <summary>The blobs as a percentage of the projected shared box area; null when no
        /// positive area was given.</summary>
        internal double? RealOnlyBlobPct;
        internal double? GhostOnlyBlobPct;

        /// <summary>[x, y, w, h] of the coverage, null when the side covers nothing.</summary>
        internal int[] BboxReal;
        internal int[] BboxGhost;

        /// <summary>[x, y] coverage centroid at pixel centres (x + 0.5, y + 0.5), null when
        /// the side covers nothing.</summary>
        internal double[] CentroidReal;
        internal double[] CentroidGhost;

        internal bool TouchesBorderReal;
        internal bool TouchesBorderGhost;

        /// <summary>Overlap INTERIOR pixels the colour measures were taken over: covered on both
        /// sides across the whole 8-neighbourhood (see <see cref="PartParityMetrics.IsInteriorOfBoth"/>).</summary>
        internal int ColorOverlapPx;

        /// <summary>The colour threshold T the over-T measures used.</summary>
        internal int ColorThreshold;

        internal int ColorOverPx;
        internal int ColorBlobPx;
        internal double ColorP99;
        internal double ColorBlockMax;

        /// <summary>Whole-image intersection over union (1 when both sides are empty). For
        /// sorting only: it dilutes a local defect.</summary>
        internal double Iou;
    }

    /// <summary>
    /// The measuring instrument of the part ghost parity sweep (design-part-ghost-parity.md
    /// sections 4.4, 6). Pure: plain RGBA8 arrays in (row-major, 4 bytes per pixel), plain
    /// numbers out; verdicts are decided in Python, never here.
    ///
    /// <para>Definitions. Coverage is alpha &gt;= 0.5 (byte &gt;= 128). A real-only pixel
    /// counts toward the TOLERANT mismatch only when no ghost-covered pixel lies in its
    /// 8-neighbourhood (and the mirror for ghost-only), so a 1 px antialiasing edge is not a
    /// defect. Blobs are 4-connected components. Colour is compared over the overlap (both
    /// sides covered) after alpha premultiply (<c>c * a / 255</c>, rounded), as the
    /// Euclidean distance in 0..255 RGB space (range 0..441.7): it reads a hue shift and a
    /// brightness shift on one scale, and a single channel off by d reads as d, so the
    /// default threshold T = 24 means "about a tenth of full scale on one channel". The p99
    /// is the nearest-rank 99th percentile of the overlap distances. The block measure
    /// splits the image into a 16 x 16 GRID of cells (cell bounds <c>i * size / 16</c>, so
    /// it is the same grid for every tile size) and reports the largest mean distance over
    /// a cell's overlap pixels, counting only cells whose overlap fills at least a quarter of
    /// the cell (a cell with one overlap pixel is an edge, not a patch).</para>
    /// </summary>
    internal static class PartParityMetrics
    {
        /// <summary>The coverage threshold on straight alpha.</summary>
        internal const double CoverageAlpha = 0.5;

        /// <summary><see cref="CoverageAlpha"/> as an alpha byte (128 / 255 = 0.502, the first
        /// byte at or above 0.5).</summary>
        internal const byte CoverageAlphaByte = 128;

        /// <summary>The mismatch tolerance: the neighbourhood radius, in pixels.</summary>
        internal const int TolerancePx = 1;

        /// <summary>Blob connectivity (4: edge neighbours only).</summary>
        internal const int BlobConnectivity = 4;

        /// <summary>Default colour threshold T (Euclidean RGB distance, premultiplied).</summary>
        internal const int DefaultColorThreshold = 24;

        /// <summary>Cells per side of the colour block grid.</summary>
        internal const int ColorBlockGrid = 16;

        /// <summary>A grid cell enters the block measure only when its overlap pixels fill at
        /// least this fraction of the cell.</summary>
        internal const double MinBlockOverlapFraction = 0.25;

        /// <summary>A white-minus-black channel difference below minus this counts as inverted.
        /// P0 measured 0 inverted pixels in all views but one (9 pixels), so a small floor
        /// keeps 8-bit rounding from counting.</summary>
        internal const int InvertedNoiseFloor = 2;

        /// <summary>Alpha below which the colour of a composed pixel is written as 0 (dividing
        /// by a near-zero alpha amplifies 8-bit noise into a full-bright colour).</summary>
        internal const double MinAlphaForColor = 1.0 / 255.0;

        internal const double OpaqueAlpha = 0.98;
        internal const double TransparentAlpha = 0.02;

        /// <summary>
        /// Composes straight-alpha RGBA from the same render over a black clear and over a
        /// white clear (D6). Per pixel <c>alpha = 1 - mean(max(0, white - black)) / 255</c> and
        /// <c>colour = black / alpha</c>, clamped and rounded. Row order in = row order out.
        /// </summary>
        internal static byte[] ComposeMatte(byte[] overBlack, byte[] overWhite, out PartParityMatteStats stats)
        {
            if (overBlack == null || overWhite == null)
                throw new ArgumentNullException(overBlack == null ? nameof(overBlack) : nameof(overWhite));
            if (overBlack.Length != overWhite.Length || overBlack.Length % 4 != 0)
                throw new ArgumentException("matte inputs must be RGBA8 arrays of equal length");

            stats = new PartParityMatteStats();
            var outp = new byte[overBlack.Length];
            for (int i = 0; i < overBlack.Length; i += 4)
            {
                int dr = overWhite[i] - overBlack[i];
                int dg = overWhite[i + 1] - overBlack[i + 1];
                int db = overWhite[i + 2] - overBlack[i + 2];
                if (dr < -InvertedNoiseFloor || dg < -InvertedNoiseFloor || db < -InvertedNoiseFloor)
                    stats.Inverted++;
                double a = 1.0 - (Math.Max(0, dr) + Math.Max(0, dg) + Math.Max(0, db)) / (3.0 * 255.0);
                if (a < 0.0) a = 0.0;
                if (a > 1.0) a = 1.0;
                if (a >= OpaqueAlpha) stats.Opaque++;
                else if (a > TransparentAlpha) stats.Edge++;
                for (int c = 0; c < 3; c++)
                {
                    double v = a >= MinAlphaForColor ? overBlack[i + c] / a : 0.0;
                    outp[i + c] = (byte)Math.Min(255.0, Math.Round(v, MidpointRounding.AwayFromZero));
                }
                outp[i + 3] = (byte)Math.Round(a * 255.0, MidpointRounding.AwayFromZero);
            }
            return outp;
        }

        /// <summary>The number of covered pixels (alpha &gt;= 0.5) of an RGBA8 image; 0 for null.</summary>
        internal static int CoveragePx(byte[] rgba)
        {
            if (rgba == null) return 0;
            int n = 0;
            for (int i = 3; i < rgba.Length; i += 4)
                if (rgba[i] >= CoverageAlphaByte) n++;
            return n;
        }

        /// <summary>The coverage mask (alpha &gt;= 0.5) of an RGBA8 image.</summary>
        internal static bool[] CoverageMask(byte[] rgba)
        {
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            if (rgba.Length % 4 != 0) throw new ArgumentException("not an RGBA8 array");
            var mask = new bool[rgba.Length / 4];
            for (int p = 0; p < mask.Length; p++)
                mask[p] = rgba[p * 4 + 3] >= CoverageAlphaByte;
            return mask;
        }

        /// <summary>
        /// Every measure of one view. <paramref name="projectedBoxAreaPx"/> is the area, in
        /// pixels, of the shared framing box projected into this view (the denominator of the
        /// blob percentages); zero or less leaves the percentages null.
        /// </summary>
        internal static PartParityViewMeasures Measure(
            byte[] realRgba, byte[] ghostRgba, int width, int height,
            double projectedBoxAreaPx, int colorThreshold = DefaultColorThreshold)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException("image size must be positive");
            long expected = (long)width * height * 4;
            if (realRgba == null || ghostRgba == null)
                throw new ArgumentNullException(realRgba == null ? nameof(realRgba) : nameof(ghostRgba));
            if (realRgba.Length != expected || ghostRgba.Length != expected)
                throw new ArgumentException("RGBA arrays do not match width x height x 4");

            bool[] real = CoverageMask(realRgba);
            bool[] ghost = CoverageMask(ghostRgba);
            var m = new PartParityViewMeasures { Width = width, Height = height, ColorThreshold = colorThreshold };

            int n = width * height;
            var realOnlyTol = new bool[n];
            var ghostOnlyTol = new bool[n];
            for (int p = 0; p < n; p++)
            {
                bool r = real[p], g = ghost[p];
                if (r) m.RealPx++;
                if (g) m.GhostPx++;
                if (r && g) m.AndPx++;
                if (r || g) m.OrPx++;
                if (r && !g)
                {
                    m.RealOnlyPx++;
                    if (!AnyInNeighbourhood(ghost, width, height, p))
                    {
                        realOnlyTol[p] = true;
                        m.RealOnlyTolPx++;
                    }
                }
                else if (g && !r)
                {
                    m.GhostOnlyPx++;
                    if (!AnyInNeighbourhood(real, width, height, p))
                    {
                        ghostOnlyTol[p] = true;
                        m.GhostOnlyTolPx++;
                    }
                }
            }

            m.RealOnlyBlobPx = LargestBlob(realOnlyTol, width, height);
            m.GhostOnlyBlobPx = LargestBlob(ghostOnlyTol, width, height);
            if (projectedBoxAreaPx > 0.0 && !double.IsInfinity(projectedBoxAreaPx))
            {
                m.RealOnlyBlobPct = 100.0 * m.RealOnlyBlobPx / projectedBoxAreaPx;
                m.GhostOnlyBlobPct = 100.0 * m.GhostOnlyBlobPx / projectedBoxAreaPx;
            }

            m.BboxReal = BoundingBox(real, width, height);
            m.BboxGhost = BoundingBox(ghost, width, height);
            m.CentroidReal = Centroid(real, width, height);
            m.CentroidGhost = Centroid(ghost, width, height);
            m.TouchesBorderReal = TouchesBorder(real, width, height);
            m.TouchesBorderGhost = TouchesBorder(ghost, width, height);
            m.Iou = m.OrPx > 0 ? (double)m.AndPx / m.OrPx : 1.0;

            MeasureColor(realRgba, ghostRgba, real, ghost, width, height, colorThreshold, m);
            return m;
        }

        /// <summary>True when any pixel of <paramref name="mask"/> lies in the 8-neighbourhood
        /// of pixel <paramref name="p"/> (the pixel itself excluded).</summary>
        internal static bool AnyInNeighbourhood(bool[] mask, int width, int height, int p)
        {
            int x = p % width, y = p / width;
            for (int dy = -1; dy <= 1; dy++)
            {
                int yy = y + dy;
                if (yy < 0 || yy >= height) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int xx = x + dx;
                    if (xx < 0 || xx >= width) continue;
                    if (mask[yy * width + xx]) return true;
                }
            }
            return false;
        }

        /// <summary>Size of the largest 4-connected component of <paramref name="mask"/>
        /// (0 for an empty mask). Iterative flood fill, no recursion.</summary>
        internal static int LargestBlob(bool[] mask, int width, int height)
        {
            if (mask == null) throw new ArgumentNullException(nameof(mask));
            if (mask.Length != width * height) throw new ArgumentException("mask size mismatch");
            var seen = new bool[mask.Length];
            var stack = new int[mask.Length];
            int best = 0;
            for (int start = 0; start < mask.Length; start++)
            {
                if (!mask[start] || seen[start]) continue;
                int size = 0, top = 0;
                stack[top++] = start;
                seen[start] = true;
                while (top > 0)
                {
                    int p = stack[--top];
                    size++;
                    int x = p % width;
                    if (x > 0) Push(mask, seen, stack, ref top, p - 1);
                    if (x < width - 1) Push(mask, seen, stack, ref top, p + 1);
                    if (p >= width) Push(mask, seen, stack, ref top, p - width);
                    if (p + width < mask.Length) Push(mask, seen, stack, ref top, p + width);
                }
                if (size > best) best = size;
            }
            return best;
        }

        private static void Push(bool[] mask, bool[] seen, int[] stack, ref int top, int q)
        {
            if (!mask[q] || seen[q]) return;
            seen[q] = true;
            stack[top++] = q;
        }

        /// <summary>[x, y, w, h] of the set pixels, null when none is set.</summary>
        internal static int[] BoundingBox(bool[] mask, int width, int height)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int p = 0; p < mask.Length; p++)
            {
                if (!mask[p]) continue;
                int x = p % width, y = p / width;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            if (maxX < 0) return null;
            return new[] { minX, minY, maxX - minX + 1, maxY - minY + 1 };
        }

        /// <summary>[x, y] mean of the set pixels' centres, null when none is set.</summary>
        internal static double[] Centroid(bool[] mask, int width, int height)
        {
            double sx = 0.0, sy = 0.0;
            long count = 0;
            for (int p = 0; p < mask.Length; p++)
            {
                if (!mask[p]) continue;
                sx += p % width + 0.5;
                sy += p / width + 0.5;
                count++;
            }
            if (count == 0) return null;
            return new[] { sx / count, sy / count };
        }

        /// <summary>True when a set pixel lies on the outermost row or column.</summary>
        internal static bool TouchesBorder(bool[] mask, int width, int height)
        {
            for (int x = 0; x < width; x++)
                if (mask[x] || mask[(height - 1) * width + x]) return true;
            for (int y = 0; y < height; y++)
                if (mask[y * width] || mask[y * width + width - 1]) return true;
            return false;
        }

        /// <summary>Premultiplied channel value, rounded: <c>c * a / 255</c>.</summary>
        internal static int Premultiply(byte c, byte a) => (c * a + 127) / 255;

        /// <summary>Euclidean distance between two RGBA8 pixels after premultiply.</summary>
        internal static double PremultipliedDistance(byte[] a, int ia, byte[] b, int ib)
        {
            int dr = Premultiply(a[ia], a[ia + 3]) - Premultiply(b[ib], b[ib + 3]);
            int dg = Premultiply(a[ia + 1], a[ia + 3]) - Premultiply(b[ib + 1], b[ib + 3]);
            int db = Premultiply(a[ia + 2], a[ia + 3]) - Premultiply(b[ib + 2], b[ib + 3]);
            return Math.Sqrt(dr * dr + dg * dg + db * db);
        }

        /// <summary>Nearest-rank percentile (<paramref name="q"/> in (0, 1]) of a list,
        /// 0 for an empty list. Sorts the list in place.</summary>
        internal static double NearestRankPercentile(List<double> values, double q)
        {
            if (values == null || values.Count == 0) return 0.0;
            values.Sort();
            int rank = (int)Math.Ceiling(q * values.Count);
            if (rank < 1) rank = 1;
            if (rank > values.Count) rank = values.Count;
            return values[rank - 1];
        }

        private static void MeasureColor(
            byte[] realRgba, byte[] ghostRgba, bool[] real, bool[] ghost,
            int width, int height, int threshold, PartParityViewMeasures m)
        {
            int n = width * height;
            var over = new bool[n];
            var dist = new double[n];
            // Colour is read over the overlap INTERIOR only: a pixel whose 8-neighbourhood is
            // covered on both sides. Antialiased edge pixels blend with the clear colour, so
            // a sub-pixel silhouette offset would otherwise read as a full-edge colour blob;
            // the shape measures own the edge. A part thinner than 3 px has no interior and
            // no colour measure.
            var interior = new bool[n];
            for (int p = 0; p < n; p++)
                interior[p] = IsInteriorOfBoth(real, ghost, width, height, p);
            var values = new List<double>(m.AndPx);
            for (int p = 0; p < n; p++)
            {
                if (!interior[p]) continue;
                double d = PremultipliedDistance(realRgba, p * 4, ghostRgba, p * 4);
                dist[p] = d;
                values.Add(d);
                m.ColorOverlapPx++;
                if (d > threshold)
                {
                    over[p] = true;
                    m.ColorOverPx++;
                }
            }
            m.ColorBlobPx = LargestBlob(over, width, height);
            m.ColorP99 = NearestRankPercentile(values, 0.99);

            double blockMax = 0.0;
            for (int cy = 0; cy < ColorBlockGrid; cy++)
            {
                int y0 = cy * height / ColorBlockGrid, y1 = (cy + 1) * height / ColorBlockGrid;
                for (int cx = 0; cx < ColorBlockGrid; cx++)
                {
                    int x0 = cx * width / ColorBlockGrid, x1 = (cx + 1) * width / ColorBlockGrid;
                    int cellArea = (x1 - x0) * (y1 - y0);
                    if (cellArea <= 0) continue;
                    int count = 0;
                    double sum = 0.0;
                    for (int y = y0; y < y1; y++)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            int p = y * width + x;
                            if (!interior[p]) continue;
                            count++;
                            sum += dist[p];
                        }
                    }
                    if (count == 0 || count < MinBlockOverlapFraction * cellArea) continue;
                    double mean = sum / count;
                    if (mean > blockMax) blockMax = mean;
                }
            }
            m.ColorBlockMax = blockMax;
        }

        /// <summary>True when pixel <paramref name="p"/> and all 8 neighbours are covered
        /// on both sides; a neighbour outside the image counts as uncovered.</summary>
        internal static bool IsInteriorOfBoth(bool[] real, bool[] ghost, int width, int height, int p)
        {
            int x = p % width, y = p / width;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) return false;
                    int q = ny * width + nx;
                    if (!real[q] || !ghost[q]) return false;
                }
            }
            return true;
        }
    }
}
