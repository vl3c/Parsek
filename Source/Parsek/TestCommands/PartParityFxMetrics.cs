using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The exhaust axis of one FX view, in the view's tile pixels (x the column, y the row,
    /// rows top-down, a pixel's centre at (x + 0.5, y + 0.5)): the sidecar's <c>axis</c> block
    /// (design-part-ghost-parity.md E.4).
    /// </summary>
    internal sealed class PartParityFxAxis
    {
        /// <summary>[x, y] of the projected nozzle point; null when the view has no axis.</summary>
        internal double[] OriginPx;

        /// <summary>[dx, dy] of the projected exhaust axis, pointing downstream, any non-zero
        /// length (the measures normalize it); null or zero on the axis view
        /// (<c>fx-bottom</c>), where the axis projects to a point.</summary>
        internal double[] DirPx;

        /// <summary>The nozzle box's diameter projected in this view, px (the cross-section
        /// offset's unit); null when not known.</summary>
        internal double? NozzleDiameterPx;
    }

    /// <summary>
    /// One side's measures of one FX tile (the sidecar's <c>real</c> / <c>ghost</c> blocks,
    /// <see cref="PartParityJson.FxSideMeasureKeys"/>), every one read from the 8-bit tile
    /// alone (<see cref="PartParityFxMetrics.MeasureTile"/>).
    /// </summary>
    internal sealed class PartParityFxSideMeasures
    {
        internal int Width;
        internal int Height;
        internal int Samples;

        internal int CorePx;
        internal int EnvelopePx;

        /// <summary>Core pixels with a tile channel at 255.</summary>
        internal int SaturatedPx;

        /// <summary>The envelope's smallest along-axis position from the nozzle point; null
        /// on the axis view or with no envelope.</summary>
        internal double? OriginPx;
        internal double? LengthPx;
        internal double? WidthPx;

        /// <summary>[x, y] mean of the core pixel centres; null with no core.</summary>
        internal double[] CoreCentroid;

        /// <summary>The core's direction against the exhaust axis, degrees in (-180, 180]: the
        /// line from the centroid of its upstream half to that of its downstream half
        /// (<see cref="PartParityFxMetrics.CoreDirectionDeg"/>); null on the axis view, with no
        /// core, or when a half is empty. Not written: the direction compare reads it.</summary>
        internal double? CoreAxisDeg;

        /// <summary>[r, g, b] mean tile colour over the core's unsaturated pixels; null when
        /// there is none.</summary>
        internal double[] MeanColor;

        /// <summary>[r, g] over r + g + b of the same sums; null when there is none or the sum is 0.</summary>
        internal double[] Chromaticity;

        /// <summary>Mean Rec. 709 luminance over the core's unsaturated pixels.</summary>
        internal double? Luminance;

        /// <summary><see cref="PartParityFxRules.HueBins"/> luminance sums over the envelope's
        /// unsaturated, non-grey pixels, by HSV hue bin.</summary>
        internal double[] HueHistogram;

        /// <summary>The core mask (not written: the IoU reads it).</summary>
        internal bool[] Core;
    }

    /// <summary>The ghost-minus-real comparison of one tile (<see cref="PartParityJson.FxCompareKeys"/>).
    /// A measure is null where it is not defined.</summary>
    internal sealed class PartParityFxCompare
    {
        internal double? OriginDeltaPx;
        internal double? LateralOffsetPx;
        internal double? LengthRatio;
        internal double? WidthRatio;
        internal double? DirectionDeg;
        internal double? CrossSectionAreaRatio;
        internal double? CrossSectionOffset;
        internal double? ChromaDistance;
        internal double? HueL1;
        internal double? BrightnessRatio;
        internal double CoreIou;
    }

    /// <summary>
    /// One side's accumulation of one FX view and layer over its samples (design E.3): per
    /// pixel, the samples that covered it (<see cref="PartParityFxRules.SampleCovered"/>) and
    /// their colour sums; <see cref="ToTile"/> writes the 8-bit tile of the E.4 rule.
    /// </summary>
    internal sealed class PartParityFxAccumulator
    {
        internal readonly string Layer;
        internal readonly int Width;
        internal readonly int Height;
        // k <= MaxSamples (255) fits a byte, and 255 covered samples of a 255 channel sum to 65025,
        // so one 512 px side / view / layer holds 7 bytes per pixel: a case's 32 sample sets of
        // two halves stay near 120 MB instead of 700.
        private readonly byte[] covered;
        private readonly ushort[] sumR, sumG, sumB;

        internal int Samples { get; private set; }

        internal PartParityFxAccumulator(string layer, int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("tile size must be positive");
            Layer = layer;
            Width = width;
            Height = height;
            int n = width * height;
            covered = new byte[n];
            sumR = new ushort[n];
            sumG = new ushort[n];
            sumB = new ushort[n];
        }

        /// <summary>Adds one sample: RGBA8, rows top-down, the tile's size. A flame sample is
        /// the render over black, a smoke sample the straight-alpha matte of its two renders.</summary>
        internal void Add(byte[] rgba)
        {
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            if (rgba.Length != (long)Width * Height * 4) throw new ArgumentException("sample does not match the tile size");
            if (Samples >= PartParityFxRules.MaxSamples)
                throw new InvalidOperationException("more than " + PartParityFxRules.MaxSamples + " samples");
            for (int p = 0, i = 0; p < covered.Length; p++, i += 4)
            {
                byte r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
                if (!PartParityFxRules.SampleCovered(Layer, r, g, b, rgba[i + 3])) continue;
                covered[p]++;
                sumR[p] += r;
                sumG[p] += g;
                sumB[p] += b;
            }
            Samples++;
        }

        /// <summary>Adds another accumulator of the same size and layer: the sum of its samples.</summary>
        internal void Merge(PartParityFxAccumulator other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            if (other.Width != Width || other.Height != Height || other.Layer != Layer)
                throw new ArgumentException("accumulators differ in size or layer");
            if (Samples + other.Samples > PartParityFxRules.MaxSamples)
                throw new InvalidOperationException("more than " + PartParityFxRules.MaxSamples + " samples");
            for (int p = 0; p < covered.Length; p++)
            {
                covered[p] += other.covered[p];
                sumR[p] += other.sumR[p];
                sumG[p] += other.sumG[p];
                sumB[p] += other.sumB[p];
            }
            Samples += other.Samples;
        }

        /// <summary>The samples that covered pixel <paramref name="p"/>.</summary>
        internal int CoveredAt(int p) => covered[p];

        /// <summary>The tile (E.4): alpha <see cref="PartParityFxRules.TileAlpha"/>, RGB the
        /// covered samples' mean (<see cref="PartParityFxRules.TileChannel"/>); an uncovered
        /// pixel is (0, 0, 0, 0).</summary>
        internal byte[] ToTile()
        {
            var tile = new byte[covered.Length * 4];
            for (int p = 0, i = 0; p < covered.Length; p++, i += 4)
            {
                int k = covered[p];
                if (k == 0) continue;
                tile[i] = (byte)PartParityFxRules.TileChannel(sumR[p], k);
                tile[i + 1] = (byte)PartParityFxRules.TileChannel(sumG[p], k);
                tile[i + 2] = (byte)PartParityFxRules.TileChannel(sumB[p], k);
                tile[i + 3] = (byte)PartParityFxRules.TileAlpha(k, Samples);
            }
            return tile;
        }
    }

    /// <summary>
    /// One side's samples of one FX view and layer, kept whole and split into halves (design
    /// E.4, convergence): the 0-based even samples (0, 2, 4, ...) and the odd ones.
    /// </summary>
    internal sealed class PartParityFxSampleSet
    {
        internal readonly PartParityFxAccumulator Even;
        internal readonly PartParityFxAccumulator Odd;
        private readonly string layer;
        private readonly int width;
        private readonly int height;
        private PartParityFxAccumulator all;

        internal PartParityFxSampleSet(string layer, int width, int height)
        {
            this.layer = layer;
            this.width = width;
            this.height = height;
            Even = new PartParityFxAccumulator(layer, width, height);
            Odd = new PartParityFxAccumulator(layer, width, height);
        }

        /// <summary>Every sample: the even and odd halves' sums, built on first read and dropped
        /// by the next <see cref="Add"/> (a live case holds the two halves, never a third copy).</summary>
        internal PartParityFxAccumulator All
        {
            get
            {
                if (all == null)
                {
                    all = new PartParityFxAccumulator(layer, width, height);
                    all.Merge(Even);
                    all.Merge(Odd);
                }
                return all;
            }
        }

        internal void Add(byte[] rgba)
        {
            all = null;
            ((Even.Samples + Odd.Samples) % 2 == 0 ? Even : Odd).Add(rgba);
        }
    }

    /// <summary>One particle a sample read (<c>ParticleSystem.GetParticles</c>), in part space.</summary>
    internal sealed class PartParityFxParticle
    {
        internal double[] Position;
        internal double[] Velocity;
        internal double Size;
    }

    /// <summary>One side's particle statistics (<see cref="PartParityJson.FxParticleKeys"/>),
    /// report-only in P8 (design E.4).</summary>
    internal sealed class PartParityFxParticleStats
    {
        /// <summary>Mean particles per sample, every particle counted; null with no sample.</summary>
        internal double? Count;

        /// <summary>The near particles (within the near distance of the nozzle), all samples.</summary>
        internal int Near;

        internal double? Speed;
        internal double? DirectionDeg;
        internal double? Size;
        internal double? AlongP95;
        internal double? AcrossP95;
    }

    /// <summary>
    /// The pure FX measures of the part ghost parity sweep (design-part-ghost-parity.md E.3,
    /// E.4, piece F1b). Every measure is read from the 8-bit FX tile the capture writes and the
    /// view's <see cref="PartParityFxAxis"/>, never from the float accumulators, so the harness
    /// recomputes each from the FX atlas and the sidecar alone: the counts, extents, origins and
    /// offsets exactly, centroids, ratios, distances, angles and the histogram within 1e-9.
    /// Every constant is <see cref="PartParityFxRules"/>'; this file holds none. Verdicts are
    /// decided in Python; <see cref="FailingMeasures"/> exists for the C# FX control judges
    /// (<see cref="PartParityFxRules.JudgeFxControl"/>).
    ///
    /// <para>Definitions over a tile of N samples. Pixel p covered in k(p) =
    /// <see cref="PartParityFxRules.CoveredSamples"/>(alpha, N) samples; CORE is
    /// <see cref="PartParityFxRules.IsCore"/>, ENVELOPE is k at least 1; a pixel is saturated
    /// when its tile colour has a channel at 255. With u = dir / |dir| and the pixel centre c,
    /// <c>t = (cx - ox) * ux + (cy - oy) * uy</c> is the along-axis position from the nozzle
    /// point o and <c>s = (cy - oy) * ux - (cx - ox) * uy</c> the across-axis one.</para>
    /// </summary>
    internal static class PartParityFxMetrics
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        // =====================================================================================
        // one side
        // =====================================================================================

        /// <summary>
        /// Every side measure of one FX tile (RGBA8, rows top-down). <paramref name="view"/>
        /// <see cref="PartParityFxRules.ViewFxBottom"/> (or an axis with no direction) leaves
        /// the axis measures null. Sums are integers, so the means and histogram bins are one
        /// division or one weighted sum from the integer totals.
        /// </summary>
        internal static PartParityFxSideMeasures MeasureTile(byte[] tile, int width, int height, int samples,
            string view, PartParityFxAxis axis)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("tile size must be positive");
            if (tile == null) throw new ArgumentNullException(nameof(tile));
            if (tile.Length != (long)width * height * 4) throw new ArgumentException("tile does not match width x height x 4");

            var m = new PartParityFxSideMeasures { Width = width, Height = height, Samples = samples };
            int n = width * height;
            m.Core = new bool[n];
            bool hasAxis = TryAxis(view, axis, out double ox, out double oy, out double ux, out double uy);
            var bin = new long[PartParityFxRules.HueBins * 3];
            long coreSumX = 0, coreSumY = 0, uR = 0, uG = 0, uB = 0;
            int unsaturated = 0;
            double tMin = double.PositiveInfinity, tMax = double.NegativeInfinity;
            double sMin = double.PositiveInfinity, sMax = double.NegativeInfinity;
            for (int p = 0; p < n; p++)
            {
                int i = p * 4;
                int k = PartParityFxRules.CoveredSamples(tile[i + 3], samples);
                if (k <= 0) continue;
                int x = p % width, y = p / width;
                int r = tile[i], g = tile[i + 1], b = tile[i + 2];
                bool saturated = PartParityFxRules.IsSaturated(r, g, b);
                m.EnvelopePx++;
                if (!saturated) AddHue(bin, r, g, b);
                double t = 0.0;
                if (hasAxis)
                {
                    double cx = x + 0.5, cy = y + 0.5;
                    t = AlongPx(cx, cy, ox, oy, ux, uy);
                    double s = AcrossPx(cx, cy, ox, oy, ux, uy);
                    if (t < tMin) tMin = t;
                    if (t > tMax) tMax = t;
                    if (s < sMin) sMin = s;
                    if (s > sMax) sMax = s;
                }
                if (!PartParityFxRules.IsCore(k, samples)) continue;
                m.Core[p] = true;
                m.CorePx++;
                coreSumX += x;
                coreSumY += y;
                if (saturated) { m.SaturatedPx++; continue; }
                unsaturated++;
                uR += r;
                uG += g;
                uB += b;
            }

            if (m.CorePx > 0) m.CoreCentroid = PixelMean(coreSumX, coreSumY, m.CorePx);
            if (hasAxis && m.EnvelopePx > 0)
            {
                m.OriginPx = tMin;
                m.LengthPx = tMax - tMin;
                m.WidthPx = sMax - sMin;
            }
            if (hasAxis && m.CorePx > 0)
                m.CoreAxisDeg = CoreDirectionDeg(m.Core, width, m.CoreCentroid, ox, oy, ux, uy);
            if (unsaturated > 0)
            {
                m.MeanColor = new[] { (double)uR / unsaturated, (double)uG / unsaturated, (double)uB / unsaturated };
                m.Luminance = WeightedLuminance(uR, uG, uB) / unsaturated;
                long sum = uR + uG + uB;
                if (sum > 0) m.Chromaticity = new[] { (double)uR / sum, (double)uG / sum };
            }
            m.HueHistogram = new double[PartParityFxRules.HueBins];
            for (int h = 0; h < PartParityFxRules.HueBins; h++)
                m.HueHistogram[h] = WeightedLuminance(bin[3 * h], bin[3 * h + 1], bin[3 * h + 2]);
            return m;
        }

        /// <summary>
        /// The HSV hue bin of an 8-bit colour, in integers: with d = max - min, the hue in
        /// sixths of a turn times d is <c>(g - b)</c> (plus 6d when negative) when r is the
        /// max, <c>(b - r) + 2d</c> when g is, else <c>(r - g) + 4d</c>; the bin is that times
        /// <see cref="PartParityFxRules.HueBins"/> over 6d. A grey colour (d = 0) has no hue: -1.
        /// </summary>
        internal static int HueBin(int r, int g, int b)
        {
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            int d = max - min;
            if (d <= 0) return -1;
            int hn;
            if (max == r)
            {
                hn = g - b;
                if (hn < 0) hn += 6 * d;
            }
            else if (max == g) hn = b - r + 2 * d;
            else hn = r - g + 4 * d;
            int bin = PartParityFxRules.HueBins * hn / (6 * d);
            return bin >= PartParityFxRules.HueBins ? PartParityFxRules.HueBins - 1 : bin;
        }

        private static void AddHue(long[] bin, int r, int g, int b)
        {
            int h = HueBin(r, g, b);
            if (h < 0) return;
            bin[3 * h] += r;
            bin[3 * h + 1] += g;
            bin[3 * h + 2] += b;
        }

        /// <summary>The Rec. 709 luminance of integer channel sums, in the operation order of
        /// <see cref="PartParityFxRules.Luminance"/>.</summary>
        internal static double WeightedLuminance(long r, long g, long b)
            => PartParityFxRules.LuminanceR * r + PartParityFxRules.LuminanceG * g + PartParityFxRules.LuminanceB * b;

        private static double[] PixelMean(long sumX, long sumY, int count)
            => new[] { (sumX + 0.5 * count) / count, (sumY + 0.5 * count) / count };

        /// <summary>The along-axis position of a point from the nozzle point.</summary>
        internal static double AlongPx(double cx, double cy, double ox, double oy, double ux, double uy)
            => (cx - ox) * ux + (cy - oy) * uy;

        /// <summary>The across-axis position of a point from the axis line (positive to the
        /// axis's left in image coordinates, rows down).</summary>
        internal static double AcrossPx(double cx, double cy, double ox, double oy, double ux, double uy)
            => (cy - oy) * ux - (cx - ox) * uy;

        /// <summary>The axis of a view, normalized; false on the axis view, or with no origin
        /// or a zero direction.</summary>
        internal static bool TryAxis(string view, PartParityFxAxis axis, out double ox, out double oy,
            out double ux, out double uy)
        {
            ox = oy = ux = uy = 0.0;
            if (PartParityFxRules.IsAxisView(view)) return false;
            if (axis?.OriginPx == null || axis.OriginPx.Length < 2 || axis.DirPx == null || axis.DirPx.Length < 2)
                return false;
            double dx = axis.DirPx[0], dy = axis.DirPx[1];
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (!(len > 0.0) || double.IsInfinity(len)) return false;
            ox = axis.OriginPx[0];
            oy = axis.OriginPx[1];
            ux = dx / len;
            uy = dy / len;
            return true;
        }

        // =====================================================================================
        // ghost against real
        // =====================================================================================

        /// <summary>
        /// The comparison of one tile, ghost minus real (E.4). On a side view: the origin
        /// delta, the lateral offset (the core centroids' across-axis positions, ghost minus
        /// real), the length and width ratios, and the direction (<see cref="CoreDirectionDeg"/>,
        /// ghost minus real, wrapped into (-180, 180]: a translation or a length change leaves it
        /// unchanged, only a turn moves it). On the axis
        /// view: the core area ratio and the distance between the core centroids over the
        /// nozzle diameter (the difference of their offsets from the nozzle point). On every
        /// view: the chromaticity distance, the hue histograms' L1 distance (each normalized
        /// to sum 1), the luminance ratio and the core IoU (1 when both cores are empty).
        /// </summary>
        internal static PartParityFxCompare Compare(PartParityFxSideMeasures real, PartParityFxSideMeasures ghost,
            string view, PartParityFxAxis axis)
        {
            if (real == null || ghost == null) throw new ArgumentNullException(real == null ? nameof(real) : nameof(ghost));
            if (real.Width != ghost.Width || real.Height != ghost.Height) throw new ArgumentException("tile sizes differ");
            var c = new PartParityFxCompare();
            bool hasAxis = TryAxis(view, axis, out double ox, out double oy, out double ux, out double uy);
            if (hasAxis)
            {
                c.OriginDeltaPx = Diff(ghost.OriginPx, real.OriginPx);
                c.LengthRatio = Ratio(ghost.LengthPx, real.LengthPx);
                c.WidthRatio = Ratio(ghost.WidthPx, real.WidthPx);
                if (real.CoreCentroid != null && ghost.CoreCentroid != null)
                {
                    c.LateralOffsetPx = AcrossPx(ghost.CoreCentroid[0], ghost.CoreCentroid[1], ox, oy, ux, uy)
                                        - AcrossPx(real.CoreCentroid[0], real.CoreCentroid[1], ox, oy, ux, uy);
                }
                if (real.CoreAxisDeg.HasValue && ghost.CoreAxisDeg.HasValue)
                    c.DirectionDeg = WrapDeg(ghost.CoreAxisDeg.Value - real.CoreAxisDeg.Value);
            }
            else if (PartParityFxRules.IsAxisView(view))
            {
                if (real.CorePx > 0) c.CrossSectionAreaRatio = (double)ghost.CorePx / real.CorePx;
                double? nozzle = axis?.NozzleDiameterPx;
                if (real.CoreCentroid != null && ghost.CoreCentroid != null && nozzle.HasValue && nozzle.Value > 0.0)
                {
                    double dx = ghost.CoreCentroid[0] - real.CoreCentroid[0];
                    double dy = ghost.CoreCentroid[1] - real.CoreCentroid[1];
                    c.CrossSectionOffset = Math.Sqrt(dx * dx + dy * dy) / nozzle.Value;
                }
            }
            if (real.Chromaticity != null && ghost.Chromaticity != null)
            {
                double dr = ghost.Chromaticity[0] - real.Chromaticity[0];
                double dg = ghost.Chromaticity[1] - real.Chromaticity[1];
                c.ChromaDistance = Math.Sqrt(dr * dr + dg * dg);
            }
            c.HueL1 = HueL1(real.HueHistogram, ghost.HueHistogram);
            c.BrightnessRatio = Ratio(ghost.Luminance, real.Luminance);
            c.CoreIou = CoreIou(real.Core, ghost.Core);
            return c;
        }

        /// <summary>
        /// The direction of a core against the exhaust axis, degrees in (-180, 180]: the core
        /// is split at its centroid's along-axis position <c>tc</c> (upstream: t &lt; tc;
        /// downstream: the rest), each half's centroid is taken from its integer index sums,
        /// and the direction is <c>atan2(s_down - s_up, t_down - t_up)</c>. A translation or a
        /// length change leaves it unchanged and a turn moves it by the turn, and it stays
        /// well conditioned for a round core (the halves' centroids sit about a radius apart).
        /// Null when a half is empty.
        /// </summary>
        internal static double? CoreDirectionDeg(bool[] core, int width, double[] centroid,
            double ox, double oy, double ux, double uy)
        {
            if (core == null || centroid == null || width <= 0) return null;
            double tc = AlongPx(centroid[0], centroid[1], ox, oy, ux, uy);
            long ux0 = 0, uy0 = 0, dx0 = 0, dy0 = 0;
            int nu = 0, nd = 0;
            for (int p = 0; p < core.Length; p++)
            {
                if (!core[p]) continue;
                int x = p % width, y = p / width;
                if (AlongPx(x + 0.5, y + 0.5, ox, oy, ux, uy) < tc) { ux0 += x; uy0 += y; nu++; }
                else { dx0 += x; dy0 += y; nd++; }
            }
            if (nu == 0 || nd == 0) return null;
            double[] up = PixelMean(ux0, uy0, nu), down = PixelMean(dx0, dy0, nd);
            double dt = AlongPx(down[0], down[1], ox, oy, ux, uy) - AlongPx(up[0], up[1], ox, oy, ux, uy);
            double ds = AcrossPx(down[0], down[1], ox, oy, ux, uy) - AcrossPx(up[0], up[1], ox, oy, ux, uy);
            return Math.Atan2(ds, dt) * 180.0 / Math.PI;
        }

        /// <summary>An angle difference wrapped into (-180, 180].</summary>
        internal static double WrapDeg(double d)
        {
            while (d > 180.0) d -= 360.0;
            while (d <= -180.0) d += 360.0;
            return d;
        }

        private static double? Diff(double? a, double? b) => a.HasValue && b.HasValue ? a.Value - b.Value : (double?)null;

        private static double? Ratio(double? num, double? den)
            => num.HasValue && den.HasValue && den.Value > 0.0 ? num.Value / den.Value : (double?)null;

        /// <summary>The L1 distance of two hue histograms each normalized to sum 1 (0 to 2);
        /// null when either sums to 0.</summary>
        internal static double? HueL1(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return null;
            double sa = 0.0, sb = 0.0;
            for (int i = 0; i < a.Length; i++) { sa += a[i]; sb += b[i]; }
            if (!(sa > 0.0) || !(sb > 0.0)) return null;
            double d = 0.0;
            for (int i = 0; i < a.Length; i++) d += Math.Abs(a[i] / sa - b[i] / sb);
            return d;
        }

        /// <summary>Intersection over union of two core masks; 1 when both are empty.</summary>
        internal static double CoreIou(bool[] a, bool[] b)
        {
            if (a == null || b == null || a.Length != b.Length) throw new ArgumentException("core masks differ");
            int and = 0, or = 0;
            for (int p = 0; p < a.Length; p++)
            {
                if (a[p] && b[p]) and++;
                if (a[p] || b[p]) or++;
            }
            return or > 0 ? (double)and / or : 1.0;
        }

        // =====================================================================================
        // the fail lines, for the C# FX control judges
        // =====================================================================================

        /// <summary>
        /// The measures of one tile comparison over their fail line (E.4 defaults, in
        /// <see cref="PartParityFxRules.Measures"/> order). The flame layer judges
        /// <c>presence</c> (one side's core at <see cref="PartParityFxRules.PresenceMinPx"/>
        /// or more, the other's 0), the smoke layer <c>smoke</c> (the same at
        /// <see cref="PartParityFxRules.SmokeMinPx"/>); both judge origin
        /// (|delta| over max(4 px, 10 % of the real length)), lateral offset (max(4 px, 5 %)),
        /// length and width (|ratio - 1| over 25 % / 30 %), direction (over 10 deg),
        /// cross-section (area ratio outside [0.6, 1.6], offset over 0.1) and colour
        /// (chromaticity distance over 0.06 or hue L1 over 0.5). A measure that is null is not
        /// measured and never fails. Brightness and core IoU are warn-only and never listed;
        /// an rcs case drops <see cref="PartParityFxRules.RcsReportOnlyMeasures"/>. Where both
        /// cores are weak (<see cref="BothCoresWeak"/>) only presence is judged: the envelope
        /// the other measures read is then stray particles.
        /// </summary>
        internal static List<string> FailingMeasures(string layer, PartParityFxSideMeasures real,
            PartParityFxSideMeasures ghost, PartParityFxCompare c, bool rcsCase)
        {
            var failing = new HashSet<string>(StringComparer.Ordinal);
            if (real == null || ghost == null || c == null) return new List<string>();
            bool smoke = string.Equals(layer, PartParityFxRules.LayerSmoke, StringComparison.Ordinal);
            if (PresenceReason(layer, real.CorePx, ghost.CorePx) != null)
                failing.Add(smoke ? PartParityFxRules.MeasureSmoke : PartParityFxRules.MeasurePresence);
            if (BothCoresWeak(real.CorePx, ghost.CorePx)) return new List<string>(failing);
            double realLength = real.LengthPx ?? 0.0;
            if (Over(Abs(c.OriginDeltaPx), Math.Max(PartParityFxRules.OriginFailMinPx,
                    PartParityFxRules.OriginFailLengthFraction * realLength)))
                failing.Add(PartParityFxRules.MeasureOrigin);
            if (Over(Abs(c.LateralOffsetPx), Math.Max(PartParityFxRules.LateralFailMinPx,
                    PartParityFxRules.LateralFailLengthFraction * realLength)))
                failing.Add(PartParityFxRules.MeasureLateral);
            if (Over(Abs(Minus1(c.LengthRatio)), PartParityFxRules.LengthFailFraction)) failing.Add(PartParityFxRules.MeasureLength);
            if (Over(Abs(Minus1(c.WidthRatio)), PartParityFxRules.WidthFailFraction)) failing.Add(PartParityFxRules.MeasureWidth);
            if (Over(Abs(c.DirectionDeg), PartParityFxRules.DirectionFailDeg)) failing.Add(PartParityFxRules.MeasureDirection);
            if (Outside(c.CrossSectionAreaRatio, PartParityFxRules.CrossSectionAreaFailMin, PartParityFxRules.CrossSectionAreaFailMax)
                || Over(c.CrossSectionOffset, PartParityFxRules.CrossSectionOffsetFail))
                failing.Add(PartParityFxRules.MeasureCrossSection);
            if (Over(c.ChromaDistance, PartParityFxRules.ChromaFail) || Over(c.HueL1, PartParityFxRules.HueL1Fail))
                failing.Add(PartParityFxRules.MeasureColour);
            if (rcsCase)
                foreach (string m in PartParityFxRules.RcsReportOnlyMeasures) failing.Remove(m);
            var ordered = new List<string>();
            foreach (string m in PartParityFxRules.Measures)
                if (failing.Contains(m)) ordered.Add(m);
            return ordered;
        }

        /// <summary>The presence reason of one layer (E.4): <c>fx-missing</c> / <c>fx-extra</c>
        /// on the flame layer, <c>smoke-missing</c> / <c>smoke-extra</c> on the smoke layer,
        /// when one side's core is at the layer's minimum
        /// (<see cref="PartParityFxRules.PresenceMinPx"/>, <see cref="PartParityFxRules.SmokeMinPx"/>)
        /// or more and the other's is 0; null otherwise.</summary>
        internal static string PresenceReason(string layer, int realCorePx, int ghostCorePx)
        {
            bool smoke = string.Equals(layer, PartParityFxRules.LayerSmoke, StringComparison.Ordinal);
            int minPx = smoke ? PartParityFxRules.SmokeMinPx : PartParityFxRules.PresenceMinPx;
            if (realCorePx >= minPx && ghostCorePx == 0)
                return smoke ? PartParityFxRules.ReasonSmokeMissing : PartParityFxRules.ReasonFxMissing;
            if (ghostCorePx >= minPx && realCorePx == 0)
                return smoke ? PartParityFxRules.ReasonSmokeExtra : PartParityFxRules.ReasonFxExtra;
            return null;
        }

        /// <summary>True when neither core reaches <see cref="PartParityFxRules.PresenceMinPx"/>,
        /// on either layer (E.4): the tile holds no plume to compare. One weak core is not enough,
        /// so a plume one side lacks stays judged.</summary>
        internal static bool BothCoresWeak(int realCorePx, int ghostCorePx)
            => realCorePx < PartParityFxRules.PresenceMinPx && ghostCorePx < PartParityFxRules.PresenceMinPx;

        private static double? Abs(double? v) => v.HasValue ? Math.Abs(v.Value) : (double?)null;

        private static double? Minus1(double? v) => v.HasValue ? v.Value - 1.0 : (double?)null;

        private static bool Over(double? v, double line) => v.HasValue && v.Value > line;

        private static bool Outside(double? v, double min, double max) => v.HasValue && (v.Value < min || v.Value > max);

        // =====================================================================================
        // convergence (split halves)
        // =====================================================================================

        /// <summary>
        /// The split-half check of one side and tile (E.4): the halves disagree on presence
        /// (one half's core at the layer's presence minimum or more, the other's 0), or their
        /// lengths or widths differ by more than <see cref="PartParityFxRules.SplitHalfPct"/>
        /// of the larger. Returns null when they agree, else the disagreement
        /// (<c>presence</c>, <c>length</c> or <c>width</c>, the first that holds, with both values).
        /// </summary>
        internal static string SplitHalfDisagreement(string layer, PartParityFxSideMeasures even, PartParityFxSideMeasures odd)
        {
            if (even == null || odd == null) return null;
            if (PresenceReason(layer, even.CorePx, odd.CorePx) != null)
                return "presence even=" + even.CorePx.ToString(Ic) + " odd=" + odd.CorePx.ToString(Ic);
            if (HalvesDiffer(even.LengthPx, odd.LengthPx))
                return "length even=" + F(even.LengthPx) + " odd=" + F(odd.LengthPx);
            if (HalvesDiffer(even.WidthPx, odd.WidthPx))
                return "width even=" + F(even.WidthPx) + " odd=" + F(odd.WidthPx);
            return null;
        }

        private static bool HalvesDiffer(double? a, double? b)
        {
            if (!a.HasValue || !b.HasValue) return false;
            return Math.Abs(a.Value - b.Value) > PartParityFxRules.SplitHalfPct * Math.Max(a.Value, b.Value);
        }

        private static string F(double? v) => v.HasValue ? v.Value.ToString("0.###", Ic) : "null";

        // =====================================================================================
        // particles (report-only) and the RCS reported-only line
        // =====================================================================================

        /// <summary>
        /// One side's particle statistics over its samples (E.4, report-only): the mean
        /// particles per sample over every particle; over the particles within
        /// <paramref name="nearM"/> of the nozzle (all samples, the E0 U8 rule): the mean speed,
        /// the mean angle of the velocity from the exhaust axis (moving particles only), the
        /// mean size, and the nearest-rank <see cref="PartParityFxRules.FramingPercentile"/>
        /// of the along-axis position and of the across-axis distance. Null where nothing counts.
        /// </summary>
        internal static PartParityFxParticleStats ReduceParticles(IList<IList<PartParityFxParticle>> samples,
            double[] nozzle, double[] axis, double nearM)
        {
            var st = new PartParityFxParticleStats();
            if (samples == null || samples.Count == 0 || nozzle == null || axis == null) return st;
            double al = Math.Sqrt(axis[0] * axis[0] + axis[1] * axis[1] + axis[2] * axis[2]);
            if (!(al > 0.0)) return st;
            double ax = axis[0] / al, ay = axis[1] / al, az = axis[2] / al;
            long total = 0;
            double speedSum = 0.0, dirSum = 0.0, sizeSum = 0.0;
            int moving = 0, withVelocity = 0, sampleCount = 0;
            var along = new List<double>();
            var across = new List<double>();
            foreach (IList<PartParityFxParticle> sample in samples)
            {
                if (sample == null) continue;
                sampleCount++;
                total += sample.Count;
                foreach (PartParityFxParticle q in sample)
                {
                    if (q?.Position == null) continue;
                    double dx = q.Position[0] - nozzle[0], dy = q.Position[1] - nozzle[1], dz = q.Position[2] - nozzle[2];
                    double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (dist > nearM) continue;
                    double t = dx * ax + dy * ay + dz * az;
                    double cross2 = dist * dist - t * t;
                    along.Add(t);
                    across.Add(cross2 > 0.0 ? Math.Sqrt(cross2) : 0.0);
                    sizeSum += q.Size;
                    if (q.Velocity != null)
                    {
                        double vx = q.Velocity[0], vy = q.Velocity[1], vz = q.Velocity[2];
                        double sp = Math.Sqrt(vx * vx + vy * vy + vz * vz);
                        speedSum += sp;
                        withVelocity++;
                        if (sp > 0.0)
                        {
                            double cos = (vx * ax + vy * ay + vz * az) / sp;
                            dirSum += Math.Acos(Math.Max(-1.0, Math.Min(1.0, cos))) * 180.0 / Math.PI;
                            moving++;
                        }
                    }
                }
            }
            if (sampleCount == 0) return st;
            st.Count = (double)total / sampleCount;
            st.Near = along.Count;
            if (st.Near > 0)
            {
                if (withVelocity > 0) st.Speed = speedSum / withVelocity;
                st.Size = sizeSum / st.Near;
                st.AlongP95 = PartParityMetrics.NearestRankPercentile(along, PartParityFxRules.FramingPercentile);
                st.AcrossP95 = PartParityMetrics.NearestRankPercentile(across, PartParityFxRules.FramingPercentile);
            }
            if (moving > 0) st.DirectionDeg = dirSum / moving;
            return st;
        }

        /// <summary>The R4 report of an rcs case's judged row (E.2, E.7): the length, width
        /// and brightness ratios it reports and never judges, beside <c>rcsPowerRatio</c>.</summary>
        internal static string FormatRcsReportedOnly(string route, string view, string layer, PartParityFxCompare c,
            double powerRatio)
            => "rcs reported-only " + (route ?? string.Empty) + ":" + (view ?? string.Empty) + ":" + (layer ?? string.Empty)
               + " length=" + R3(c?.LengthRatio) + " width=" + R3(c?.WidthRatio)
               + " brightness=" + R3(c?.BrightnessRatio)
               + " power=" + R3(double.IsNaN(powerRatio) || double.IsInfinity(powerRatio) ? (double?)null : powerRatio);

        private static string R3(double? v) => v.HasValue ? v.Value.ToString("0.000", Ic) : "null";

        // =====================================================================================
        // the atlas and the sidecar blocks
        // =====================================================================================

        /// <summary>The FX atlas row (<see cref="PartParityJson.RcsFxAtlasRows"/>) of a side
        /// (<see cref="PartParityFxRules.Sides"/>) and FX layer; null for an unknown pair.</summary>
        internal static string AtlasRowFor(string side, string layer)
        {
            if (layer != PartParityFxRules.LayerFlame && layer != PartParityFxRules.LayerSmoke) return null;
            switch (side)
            {
                case PartParityFxRules.SideReal: return "real-" + layer;
                case PartParityFxRules.SideGhost: return "ghost-" + layer;
                case PartParityFxRules.SideGhostStart: return "ghost-start-" + layer;
                case PartParityFxRules.SideGhostAll: return "ghost-all-" + layer;
                default: return null;
            }
        }

        internal static string AxisJson(PartParityFxAxis a)
        {
            if (a == null) return "null";
            return PartParityJson.KeyedBlock(PartParityJson.FxAxisKeys, new Dictionary<string, string>
            {
                { "originPx", PartParityJson.NumArray(a.OriginPx) },
                { "dirPx", PartParityJson.NumArray(a.DirPx) },
                { "nozzleDiameterPx", PartParityJson.Num(a.NozzleDiameterPx) },
            });
        }

        internal static string SideJson(PartParityFxSideMeasures m)
        {
            if (m == null) return "null";
            return PartParityJson.KeyedBlock(PartParityJson.FxSideMeasureKeys, new Dictionary<string, string>
            {
                { "corePx", PartParityJson.Int(m.CorePx) },
                { "envelopePx", PartParityJson.Int(m.EnvelopePx) },
                { "saturatedPx", PartParityJson.Int(m.SaturatedPx) },
                { "originPx", PartParityJson.Num(m.OriginPx) },
                { "lengthPx", PartParityJson.Num(m.LengthPx) },
                { "widthPx", PartParityJson.Num(m.WidthPx) },
                { "coreCentroid", PartParityJson.NumArray(m.CoreCentroid) },
                { "meanColor", PartParityJson.NumArray(m.MeanColor) },
                { "chromaticity", PartParityJson.NumArray(m.Chromaticity) },
                { "luminance", PartParityJson.Num(m.Luminance) },
                { "hueHistogram", PartParityJson.NumArray(m.HueHistogram) },
            });
        }

        internal static string CompareJson(PartParityFxCompare c)
        {
            if (c == null) return "null";
            return PartParityJson.KeyedBlock(PartParityJson.FxCompareKeys, new Dictionary<string, string>
            {
                { "originDeltaPx", PartParityJson.Num(c.OriginDeltaPx) },
                { "lateralOffsetPx", PartParityJson.Num(c.LateralOffsetPx) },
                { "lengthRatio", PartParityJson.Num(c.LengthRatio) },
                { "widthRatio", PartParityJson.Num(c.WidthRatio) },
                { "directionDeg", PartParityJson.Num(c.DirectionDeg) },
                { "crossSectionAreaRatio", PartParityJson.Num(c.CrossSectionAreaRatio) },
                { "crossSectionOffset", PartParityJson.Num(c.CrossSectionOffset) },
                { "chromaDistance", PartParityJson.Num(c.ChromaDistance) },
                { "hueL1", PartParityJson.Num(c.HueL1) },
                { "brightnessRatio", PartParityJson.Num(c.BrightnessRatio) },
                { "coreIou", PartParityJson.Num(c.CoreIou) },
            });
        }

        internal static string ParticlesJson(PartParityFxParticleStats s)
        {
            if (s == null) return "null";
            return PartParityJson.KeyedBlock(PartParityJson.FxParticleKeys, new Dictionary<string, string>
            {
                { "count", PartParityJson.Num(s.Count) },
                { "speed", PartParityJson.Num(s.Speed) },
                { "direction", PartParityJson.Num(s.DirectionDeg) },
                { "size", PartParityJson.Num(s.Size) },
                { "alongP95", PartParityJson.Num(s.AlongP95) },
                { "acrossP95", PartParityJson.Num(s.AcrossP95) },
            });
        }
    }

    /// <summary>
    /// One <c>fx.views[]</c> entry measured (design E.4, E.5): a route, FX view and layer,
    /// the real and route-ghost tiles of the whole sample set and of each half, their side
    /// measures and comparisons. The whole-set tiles are the ones the FX atlas holds; the
    /// halves are written as numbers only.
    /// </summary>
    internal sealed class PartParityFxViewResult
    {
        internal string View;
        internal string Route;
        internal string Layer;
        internal PartParityFxAxis Axis;
        internal int Width;
        internal int Height;
        internal byte[] RealTile;
        internal byte[] GhostTile;
        internal PartParityFxSideMeasures Real;
        internal PartParityFxSideMeasures Ghost;
        internal PartParityFxCompare Compare;
        internal int EvenSamples;
        internal int OddSamples;
        internal PartParityFxSideMeasures RealEven, GhostEven, RealOdd, GhostOdd;
        internal PartParityFxCompare CompareEven, CompareOdd;

        /// <summary>Measures one entry from the two sides' sample sets (same size and count).</summary>
        internal static PartParityFxViewResult Measure(string view, string route, string layer, PartParityFxAxis axis,
            PartParityFxSampleSet real, PartParityFxSampleSet ghost)
        {
            if (real == null || ghost == null) throw new ArgumentNullException(real == null ? nameof(real) : nameof(ghost));
            if (real.All.Width != ghost.All.Width || real.All.Height != ghost.All.Height)
                throw new ArgumentException("sample sets differ in size");
            if (real.All.Samples != ghost.All.Samples)
                throw new ArgumentException("sample sets differ in sample count");
            int w = real.All.Width, h = real.All.Height;
            var r = new PartParityFxViewResult
            {
                View = view, Route = route, Layer = layer, Axis = axis, Width = w, Height = h,
                RealTile = real.All.ToTile(), GhostTile = ghost.All.ToTile(),
                EvenSamples = real.Even.Samples, OddSamples = real.Odd.Samples,
            };
            r.Real = PartParityFxMetrics.MeasureTile(r.RealTile, w, h, real.All.Samples, view, axis);
            r.Ghost = PartParityFxMetrics.MeasureTile(r.GhostTile, w, h, ghost.All.Samples, view, axis);
            r.Compare = PartParityFxMetrics.Compare(r.Real, r.Ghost, view, axis);
            r.RealEven = PartParityFxMetrics.MeasureTile(real.Even.ToTile(), w, h, real.Even.Samples, view, axis);
            r.GhostEven = PartParityFxMetrics.MeasureTile(ghost.Even.ToTile(), w, h, ghost.Even.Samples, view, axis);
            r.CompareEven = PartParityFxMetrics.Compare(r.RealEven, r.GhostEven, view, axis);
            r.RealOdd = PartParityFxMetrics.MeasureTile(real.Odd.ToTile(), w, h, real.Odd.Samples, view, axis);
            r.GhostOdd = PartParityFxMetrics.MeasureTile(ghost.Odd.ToTile(), w, h, ghost.Odd.Samples, view, axis);
            r.CompareOdd = PartParityFxMetrics.Compare(r.RealOdd, r.GhostOdd, view, axis);
            return r;
        }

        /// <summary>The split-half disagreement of this entry: the real side's, else the
        /// ghost's, prefixed with the side; null when both settled, and where both whole-set
        /// cores are weak (<see cref="PartParityFxMetrics.BothCoresWeak"/>): stray particles that
        /// differ between the halves must not cap a case whose plume fails elsewhere.</summary>
        internal string SplitHalfDisagreement()
        {
            if (Real != null && Ghost != null && PartParityFxMetrics.BothCoresWeak(Real.CorePx, Ghost.CorePx)) return null;
            string real = PartParityFxMetrics.SplitHalfDisagreement(Layer, RealEven, RealOdd);
            if (real != null) return PartParityFxRules.SideReal + " " + real;
            string ghost = PartParityFxMetrics.SplitHalfDisagreement(Layer, GhostEven, GhostOdd);
            return ghost != null ? PartParityFxRules.SideGhost + " " + ghost : null;
        }

        /// <summary>The failing measures of the whole sample set (<see cref="PartParityFxMetrics.FailingMeasures"/>).</summary>
        internal List<string> FailingMeasures(bool rcsCase)
            => PartParityFxMetrics.FailingMeasures(Layer, Real, Ghost, Compare, rcsCase);

        /// <summary>The entry as JSON, every <see cref="PartParityJson.FxViewKeys"/> key.</summary>
        internal string Json()
        {
            string Half(int samples, PartParityFxSideMeasures re, PartParityFxSideMeasures gh, PartParityFxCompare c)
                => PartParityJson.KeyedBlock(PartParityJson.FxHalfKeys, new Dictionary<string, string>
                {
                    { "samples", PartParityJson.Int(samples) },
                    { "real", PartParityFxMetrics.SideJson(re) },
                    { "ghost", PartParityFxMetrics.SideJson(gh) },
                    { "compare", PartParityFxMetrics.CompareJson(c) },
                });
            string split = PartParityJson.KeyedBlock(PartParityJson.FxSplitHalfKeys, new Dictionary<string, string>
            {
                { "even", Half(EvenSamples, RealEven, GhostEven, CompareEven) },
                { "odd", Half(OddSamples, RealOdd, GhostOdd, CompareOdd) },
            });
            return PartParityJson.KeyedBlock(PartParityJson.FxViewKeys, new Dictionary<string, string>
            {
                { "view", PartParityJson.Str(View) },
                { "route", PartParityJson.Str(Route) },
                { "layer", PartParityJson.Str(Layer) },
                { "width", PartParityJson.Int(Width) },
                { "height", PartParityJson.Int(Height) },
                { "axis", PartParityFxMetrics.AxisJson(Axis) },
                { "real", PartParityFxMetrics.SideJson(Real) },
                { "ghost", PartParityFxMetrics.SideJson(Ghost) },
                { "compare", PartParityFxMetrics.CompareJson(Compare) },
                { "splitHalf", split },
            });
        }
    }
}
