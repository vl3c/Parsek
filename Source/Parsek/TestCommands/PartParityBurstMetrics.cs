using System;
using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>Where a burst view's source point projects on each side (each side is
    /// photographed from its own pose relative to its own source, design G.2.1), and the source
    /// part's box diameter in the view's pixels (the centroid offset's scale).</summary>
    internal sealed class PartParityBurstSource
    {
        internal double[] RealPx;
        internal double[] GhostPx;
        internal double? DiameterPx;
    }

    /// <summary>One side's measures of one bin's tile (every key of
    /// <see cref="PartParityJson.BurstBinMeasureKeys"/>), all from the 8-bit tile (E.4's rule).</summary>
    internal sealed class PartParityBurstBinMeasures
    {
        internal int Samples;
        internal int CorePx;
        internal int EnvelopePx;
        internal int SaturatedPx;

        /// <summary>The core's centroid (pixel centres), null with no core.</summary>
        internal double[] CoreCentroid;

        /// <summary>The nearest-rank <see cref="PartParityFxRules.BurstRadiusPercentile"/> of the
        /// envelope pixels' distances from the side's source point; null with no envelope or no source.</summary>
        internal double? RadiusPx;

        internal double[] MeanColor;
        internal double[] Chromaticity;
        internal double? Luminance;
        internal double[] HueHistogram;
    }

    /// <summary>One side's measures of a view and layer over the bins (every key of
    /// <see cref="PartParityJson.BurstSideMeasureKeys"/>).</summary>
    internal sealed class PartParityBurstSideMeasures
    {
        internal readonly List<PartParityBurstBinMeasures> Bins = new List<PartParityBurstBinMeasures>();

        /// <summary>The first bin with envelope pixels.</summary>
        internal int? OnsetBin;

        /// <summary>The bin of the largest envelope (the earliest on a tie).</summary>
        internal int? PeakBin;

        /// <summary>The last bin with core pixels.</summary>
        internal int? LastCoreBin;

        /// <summary>The largest core over the bins.</summary>
        internal int PeakCorePx;
    }

    /// <summary>The ghost-minus-real comparison of a view and layer (every key of
    /// <see cref="PartParityJson.BurstCompareKeys"/>; null where undefined).</summary>
    internal sealed class PartParityBurstCompare
    {
        internal string Presence;
        internal double? OnsetDeltaBins;
        internal double? PeakTimeDeltaBins;
        internal double? DurationDelta;
        internal double? PeakAreaRatio;
        internal double? RadiusRatio;
        internal double? CentroidOffset;
        internal double? ChromaDistance;
        internal double? HueL1;
        internal double? BrightnessRatio;
        internal double? ChromaDistanceMean;
        internal double? HueL1Mean;
    }

    /// <summary>One blast of a side's schedule (design G.2.1): its frame, its time after the
    /// trigger, its position relative to the side's own source point (metres), its power and prefab.</summary>
    internal sealed class PartParityBurstBlast
    {
        internal int Frame;
        internal double T;
        internal double[] OffsetM;
        internal double Power;
        internal string Prefab;
        internal int Index;
    }

    /// <summary>The schedule diff (every key of <see cref="PartParityJson.BurstScheduleCompareKeys"/>)
    /// and its G.3 level.</summary>
    internal sealed class PartParityBurstScheduleCompare
    {
        internal int RealCount;
        internal int GhostCount;
        internal bool CountDiffers;
        internal bool IndexDiffers;
        internal bool PowerDiffers;

        /// <summary>The largest distance between paired blasts' offsets; null with nothing paired.</summary>
        internal double? MaxOffsetDeltaM;

        /// <summary><see cref="PartParityFxRules.LevelFail"/> on a count, index or position
        /// difference, <see cref="PartParityFxRules.LevelWarn"/> on a power difference inside one
        /// index, else <see cref="PartParityFxRules.LevelOk"/>.</summary>
        internal string Level;
    }

    /// <summary>
    /// One side's accumulation of one burst view and layer (design G.2.1): per time bin, the
    /// E.4 accumulator of every frame of every trial in it, kept as two halves by trial
    /// (<see cref="PartParityFxRules.IsEvenTrial"/>: trials 1 and 3 against 2 and 4).
    /// </summary>
    internal sealed class PartParityBurstAccumulator
    {
        internal readonly string Layer;
        internal readonly int Width;
        internal readonly int Height;
        internal readonly int BinCount;
        private readonly PartParityFxAccumulator[] even;
        private readonly PartParityFxAccumulator[] odd;

        internal PartParityBurstAccumulator(string layer, int width, int height, int bins)
        {
            if (bins <= 0) throw new ArgumentException("a burst needs a bin");
            Layer = layer;
            Width = width;
            Height = height;
            BinCount = bins;
            even = new PartParityFxAccumulator[bins];
            odd = new PartParityFxAccumulator[bins];
            for (int b = 0; b < bins; b++)
            {
                even[b] = new PartParityFxAccumulator(layer, width, height);
                odd[b] = new PartParityFxAccumulator(layer, width, height);
            }
        }

        /// <summary>Adds one capture frame of one trial to its bin (a bin out of range is refused:
        /// the caller drops frames past the window, <see cref="PartParityFxRules.BurstBinIndex"/>).</summary>
        internal void Add(int bin, int trial, byte[] rgba)
        {
            if (bin < 0 || bin >= BinCount) throw new ArgumentOutOfRangeException(nameof(bin));
            if (trial < 0) throw new ArgumentOutOfRangeException(nameof(trial));
            (PartParityFxRules.IsEvenTrial(trial) ? even : odd)[bin].Add(rgba);
        }

        /// <summary>One bin's accumulator: the whole set, the even trials or the odd ones.</summary>
        internal PartParityFxAccumulator Bin(int bin, PartParityBurstHalf half)
        {
            if (half == PartParityBurstHalf.Even) return even[bin];
            if (half == PartParityBurstHalf.Odd) return odd[bin];
            var all = new PartParityFxAccumulator(Layer, Width, Height);
            all.Merge(even[bin]);
            all.Merge(odd[bin]);
            return all;
        }
    }

    /// <summary>Which trials a tile holds.</summary>
    internal enum PartParityBurstHalf
    {
        All,
        Even,
        Odd,
    }

    /// <summary>
    /// The pure burst measures of the part parity sweep (design-part-ghost-parity.md Appendix
    /// G.3, piece X3). Every constant is <see cref="PartParityFxRules"/>' (this file holds
    /// none), every measure is computed from the 8-bit tiles the atlas holds, in one order of
    /// operations, so <c>partparity.py</c>'s transcription recomputes each value. A burst has no
    /// exhaust axis: the measures are radial about each side's own projected source point.
    /// </summary>
    internal static class PartParityBurstMetrics
    {
        // =====================================================================================
        // one bin, one side
        // =====================================================================================

        /// <summary>
        /// One side's measures of one bin's tile (RGBA8, rows top-down, E.4's tile rule): core,
        /// envelope and saturated counts; the core centroid; the radius about
        /// <paramref name="sourcePx"/>; colour, luminance and chromaticity over the core's
        /// unsaturated pixels; the hue histogram over the envelope's unsaturated pixels (E.4).
        /// </summary>
        internal static PartParityBurstBinMeasures MeasureBin(byte[] tile, int width, int height, int samples,
            double[] sourcePx)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("tile size must be positive");
            if (tile == null) throw new ArgumentNullException(nameof(tile));
            if (tile.Length != (long)width * height * 4) throw new ArgumentException("tile does not match width x height x 4");
            var m = new PartParityBurstBinMeasures { Samples = samples };
            bool hasSource = sourcePx != null && sourcePx.Length >= 2;
            var bin = new long[PartParityFxRules.HueBins * 3];
            var distances = new List<double>();
            long coreSumX = 0, coreSumY = 0, uR = 0, uG = 0, uB = 0;
            int unsaturated = 0;
            for (int p = 0, n = width * height; p < n; p++)
            {
                int i = p * 4;
                int k = PartParityFxRules.CoveredSamples(tile[i + 3], samples);
                if (k <= 0) continue;
                int x = p % width, y = p / width;
                int r = tile[i], g = tile[i + 1], b = tile[i + 2];
                bool saturated = PartParityFxRules.IsSaturated(r, g, b);
                m.EnvelopePx++;
                if (!saturated) AddHue(bin, r, g, b);
                if (hasSource)
                {
                    double dx = x + 0.5 - sourcePx[0], dy = y + 0.5 - sourcePx[1];
                    distances.Add(Math.Sqrt(dx * dx + dy * dy));
                }
                if (!PartParityFxRules.IsCore(k, samples)) continue;
                m.CorePx++;
                coreSumX += x;
                coreSumY += y;
                if (saturated) { m.SaturatedPx++; continue; }
                unsaturated++;
                uR += r;
                uG += g;
                uB += b;
            }
            if (m.CorePx > 0)
                m.CoreCentroid = new[] { (coreSumX + 0.5 * m.CorePx) / m.CorePx, (coreSumY + 0.5 * m.CorePx) / m.CorePx };
            if (hasSource && distances.Count > 0)
                m.RadiusPx = PartParityMetrics.NearestRankPercentile(distances, PartParityFxRules.BurstRadiusPercentile);
            if (unsaturated > 0)
            {
                m.MeanColor = new[] { (double)uR / unsaturated, (double)uG / unsaturated, (double)uB / unsaturated };
                m.Luminance = PartParityFxMetrics.WeightedLuminance(uR, uG, uB) / unsaturated;
                long sum = uR + uG + uB;
                if (sum > 0) m.Chromaticity = new[] { (double)uR / sum, (double)uG / sum };
            }
            m.HueHistogram = new double[PartParityFxRules.HueBins];
            for (int h = 0; h < PartParityFxRules.HueBins; h++)
                m.HueHistogram[h] = PartParityFxMetrics.WeightedLuminance(bin[3 * h], bin[3 * h + 1], bin[3 * h + 2]);
            return m;
        }

        private static void AddHue(long[] bin, int r, int g, int b)
        {
            int h = PartParityFxMetrics.HueBin(r, g, b);
            if (h < 0) return;
            bin[3 * h] += r;
            bin[3 * h + 1] += g;
            bin[3 * h + 2] += b;
        }

        /// <summary>One side's measures over its bins: the bin measures, then the onset (first
        /// envelope), the peak (largest envelope, earliest on a tie), the last core bin and the
        /// largest core.</summary>
        internal static PartParityBurstSideMeasures MeasureSide(IList<PartParityBurstBinMeasures> bins)
        {
            var s = new PartParityBurstSideMeasures();
            if (bins == null) return s;
            int peakEnvelope = 0;
            for (int b = 0; b < bins.Count; b++)
            {
                PartParityBurstBinMeasures m = bins[b];
                s.Bins.Add(m);
                if (m == null) continue;
                if (m.EnvelopePx > 0 && !s.OnsetBin.HasValue) s.OnsetBin = b;
                if (m.EnvelopePx > peakEnvelope)
                {
                    peakEnvelope = m.EnvelopePx;
                    s.PeakBin = b;
                }
                if (m.CorePx > 0) s.LastCoreBin = b;
                if (m.CorePx > s.PeakCorePx) s.PeakCorePx = m.CorePx;
            }
            return s;
        }

        // =====================================================================================
        // ghost against real
        // =====================================================================================

        /// <summary>
        /// The comparison of one view and layer, ghost minus real (G.3):
        /// <list type="bullet">
        /// <item>presence: over the window, one side's largest core reaches the layer's minimum
        /// (<see cref="PartParityFxRules.PresenceMinPx"/>, <see cref="PartParityFxRules.SmokeMinPx"/> on the
        /// smoke layer) and the other side's is 0 (<see cref="PresenceReason"/>).</item>
        /// <item>onset, peak time: bin deltas; duration: the last core bins' delta over the real
        /// duration, which runs from the trigger to the end of its last core bin (<c>last + 1</c> bins).</item>
        /// <item>peak area: the largest cores' ratio.</item>
        /// <item>At the REAL peak bin: the radius ratio; the centroid offset (each centroid from its
        /// own side's source point, ghost minus real, its length over the source part's box
        /// diameter); the chromaticity distance, the hue L1 and the luminance ratio.</item>
        /// <item>The chromaticity distance and hue L1 averaged over every bin where both are defined.</item>
        /// </list>
        /// </summary>
        internal static PartParityBurstCompare Compare(PartParityBurstSideMeasures real, PartParityBurstSideMeasures ghost,
            PartParityBurstSource source, string layer)
        {
            if (real == null || ghost == null) throw new ArgumentNullException(real == null ? nameof(real) : nameof(ghost));
            if (real.Bins.Count != ghost.Bins.Count) throw new ArgumentException("sides differ in bins");
            var c = new PartParityBurstCompare { Presence = PresenceReason(layer, real, ghost) };
            if (real.OnsetBin.HasValue && ghost.OnsetBin.HasValue) c.OnsetDeltaBins = ghost.OnsetBin.Value - real.OnsetBin.Value;
            if (real.PeakBin.HasValue && ghost.PeakBin.HasValue) c.PeakTimeDeltaBins = ghost.PeakBin.Value - real.PeakBin.Value;
            if (real.LastCoreBin.HasValue && ghost.LastCoreBin.HasValue)
                c.DurationDelta = (double)(ghost.LastCoreBin.Value - real.LastCoreBin.Value) / (real.LastCoreBin.Value + 1);
            if (real.PeakCorePx > 0) c.PeakAreaRatio = (double)ghost.PeakCorePx / real.PeakCorePx;
            if (real.PeakBin.HasValue)
            {
                PartParityBurstBinMeasures r = real.Bins[real.PeakBin.Value], g = ghost.Bins[real.PeakBin.Value];
                if (r != null && g != null)
                {
                    if (r.RadiusPx.HasValue && g.RadiusPx.HasValue && r.RadiusPx.Value > 0.0)
                        c.RadiusRatio = g.RadiusPx.Value / r.RadiusPx.Value;
                    c.CentroidOffset = CentroidOffset(r.CoreCentroid, g.CoreCentroid, source);
                    c.ChromaDistance = ChromaDistance(r.Chromaticity, g.Chromaticity);
                    c.HueL1 = PartParityFxMetrics.HueL1(r.HueHistogram, g.HueHistogram);
                    if (r.Luminance.HasValue && g.Luminance.HasValue && r.Luminance.Value > 0.0)
                        c.BrightnessRatio = g.Luminance.Value / r.Luminance.Value;
                }
            }
            double chroma = 0.0, hue = 0.0;
            int nc = 0, nh = 0;
            for (int b = 0; b < real.Bins.Count; b++)
            {
                PartParityBurstBinMeasures r = real.Bins[b], g = ghost.Bins[b];
                if (r == null || g == null) continue;
                double? d = ChromaDistance(r.Chromaticity, g.Chromaticity);
                if (d.HasValue) { chroma += d.Value; nc++; }
                double? h = PartParityFxMetrics.HueL1(r.HueHistogram, g.HueHistogram);
                if (h.HasValue) { hue += h.Value; nh++; }
            }
            if (nc > 0) c.ChromaDistanceMean = chroma / nc;
            if (nh > 0) c.HueL1Mean = hue / nh;
            return c;
        }

        /// <summary>
        /// The presence reason of a view and layer: <c>burst-missing</c> / <c>burst-extra</c>
        /// (<c>smoke-missing</c> / <c>smoke-extra</c> on the smoke layer) when one side's largest
        /// core over the window reaches the layer's minimum and the other side draws no core in
        /// any bin; missing first. Read over the window, not bin by bin: a burst one bin late leaves
        /// bins with one side's core only, and that is onset's and duration's measure, not presence's.
        /// </summary>
        internal static string PresenceReason(string layer, PartParityBurstSideMeasures real, PartParityBurstSideMeasures ghost)
        {
            bool smoke = string.Equals(layer, PartParityFxRules.LayerSmoke, StringComparison.Ordinal);
            int min = smoke ? PartParityFxRules.SmokeMinPx : PartParityFxRules.PresenceMinPx;
            if (real.PeakCorePx >= min && ghost.PeakCorePx == 0)
                return smoke ? PartParityFxRules.ReasonSmokeMissing : PartParityFxRules.ReasonBurstMissing;
            if (ghost.PeakCorePx >= min && real.PeakCorePx == 0)
                return smoke ? PartParityFxRules.ReasonSmokeExtra : PartParityFxRules.ReasonBurstExtra;
            return null;
        }

        /// <summary>The centroid offset: <c>|(ghost centroid - ghost source) - (real centroid - real source)|</c>
        /// over the source part's box diameter; null without both centroids, both source points
        /// and a positive diameter.</summary>
        internal static double? CentroidOffset(double[] realCentroid, double[] ghostCentroid, PartParityBurstSource source)
        {
            if (realCentroid == null || ghostCentroid == null || source?.RealPx == null || source.GhostPx == null
                || !source.DiameterPx.HasValue || !(source.DiameterPx.Value > 0.0))
                return null;
            double dx = (ghostCentroid[0] - source.GhostPx[0]) - (realCentroid[0] - source.RealPx[0]);
            double dy = (ghostCentroid[1] - source.GhostPx[1]) - (realCentroid[1] - source.RealPx[1]);
            return Math.Sqrt(dx * dx + dy * dy) / source.DiameterPx.Value;
        }

        private static double? ChromaDistance(double[] a, double[] b)
        {
            if (a == null || b == null) return null;
            double dr = b[0] - a[0], dg = b[1] - a[1];
            return Math.Sqrt(dr * dr + dg * dg);
        }

        // =====================================================================================
        // the schedule (G.3): deterministic, judged first
        // =====================================================================================

        /// <summary>
        /// The schedule diff: the blasts are paired in order. A count, prefab index or position
        /// difference (paired offsets more than <see cref="PartParityFxRules.BurstSchedulePositionToleranceM"/>
        /// apart) fails; a power difference over <see cref="PartParityFxRules.BurstSchedulePowerEpsilon"/>
        /// inside one index warns.
        /// </summary>
        internal static PartParityBurstScheduleCompare CompareSchedule(IList<PartParityBurstBlast> real,
            IList<PartParityBurstBlast> ghost)
        {
            var s = new PartParityBurstScheduleCompare { RealCount = real?.Count ?? 0, GhostCount = ghost?.Count ?? 0 };
            s.CountDiffers = s.RealCount != s.GhostCount;
            int paired = Math.Min(s.RealCount, s.GhostCount);
            bool positionDiffers = false;
            for (int i = 0; i < paired; i++)
            {
                PartParityBurstBlast r = real[i], g = ghost[i];
                if (r.Index != g.Index) s.IndexDiffers = true;
                else if (Math.Abs(g.Power - r.Power) > PartParityFxRules.BurstSchedulePowerEpsilon) s.PowerDiffers = true;
                double d = OffsetDistance(r.OffsetM, g.OffsetM);
                if (!s.MaxOffsetDeltaM.HasValue || d > s.MaxOffsetDeltaM.Value) s.MaxOffsetDeltaM = d;
                if (d > PartParityFxRules.BurstSchedulePositionToleranceM) positionDiffers = true;
            }
            s.Level = s.CountDiffers || s.IndexDiffers || positionDiffers ? PartParityFxRules.LevelFail
                : s.PowerDiffers ? PartParityFxRules.LevelWarn : PartParityFxRules.LevelOk;
            return s;
        }

        private static double OffsetDistance(double[] a, double[] b)
        {
            double sum = 0.0;
            for (int i = 0; i < 3; i++)
            {
                double d = (b != null && b.Length > i ? b[i] : 0.0) - (a != null && a.Length > i ? a[i] : 0.0);
                sum += d * d;
            }
            return Math.Sqrt(sum);
        }

        // =====================================================================================
        // the fail lines, for the C# burst control judges (case verdicts stay Python's, X5)
        // =====================================================================================

        /// <summary>
        /// The measures of one view and layer over their fail line (names of
        /// <see cref="PartParityFxRules.BurstMeasures"/>, in that order). The flame layer judges
        /// presence, onset, peak time, duration, peak area, radius, centroid offset and colour
        /// (at the real peak bin or averaged over the bins, <see cref="PartParityFxRules.BurstColourLevel"/>);
        /// the smoke layer judges <c>smoke</c> (its presence, or its peak area). Brightness is
        /// warn-only, never listed; a null measure never fails; a layer one side lacks fails
        /// presence alone; where both sides' largest cores are under
        /// <see cref="PartParityFxRules.PresenceMinPx"/> only presence is judged (E.4's weak-core rule).
        /// </summary>
        internal static List<string> FailingMeasures(string layer, PartParityBurstSideMeasures real,
            PartParityBurstSideMeasures ghost, PartParityBurstCompare c)
        {
            var failing = new List<string>();
            if (real == null || ghost == null || c == null) return failing;
            bool smoke = string.Equals(layer, PartParityFxRules.LayerSmoke, StringComparison.Ordinal);
            if (smoke)
            {
                if (c.Presence != null
                    || (!PartParityFxMetrics.BothCoresWeak(real.PeakCorePx, ghost.PeakCorePx)
                        && Fails(PartParityFxRules.MeasurePeakArea, c.PeakAreaRatio)))
                    failing.Add(PartParityFxRules.MeasureSmoke);
                return failing;
            }
            if (c.Presence != null)
            {
                // A layer one side lacks is named by its presence reason alone (F4's rule).
                failing.Add(PartParityFxRules.MeasurePresence);
                return failing;
            }
            if (PartParityFxMetrics.BothCoresWeak(real.PeakCorePx, ghost.PeakCorePx)) return failing;
            if (Fails(PartParityFxRules.MeasureOnset, c.OnsetDeltaBins)) failing.Add(PartParityFxRules.MeasureOnset);
            if (Fails(PartParityFxRules.MeasurePeakTime, c.PeakTimeDeltaBins)) failing.Add(PartParityFxRules.MeasurePeakTime);
            if (Fails(PartParityFxRules.MeasureDuration, c.DurationDelta)) failing.Add(PartParityFxRules.MeasureDuration);
            if (Fails(PartParityFxRules.MeasurePeakArea, c.PeakAreaRatio)) failing.Add(PartParityFxRules.MeasurePeakArea);
            if (Fails(PartParityFxRules.MeasureRadius, c.RadiusRatio)) failing.Add(PartParityFxRules.MeasureRadius);
            if (Fails(PartParityFxRules.MeasureCentroidOffset, c.CentroidOffset)) failing.Add(PartParityFxRules.MeasureCentroidOffset);
            if (ColourLevel(c.ChromaDistance, c.HueL1) == PartParityFxRules.LevelFail
                || ColourLevel(c.ChromaDistanceMean, c.HueL1Mean) == PartParityFxRules.LevelFail)
                failing.Add(PartParityFxRules.MeasureColour);
            return failing;
        }

        /// <summary>The schedule's failing measure: <c>schedule</c> at its fail level.</summary>
        internal static List<string> FailingScheduleMeasures(PartParityBurstScheduleCompare s)
        {
            var failing = new List<string>();
            if (s != null && s.Level == PartParityFxRules.LevelFail) failing.Add(PartParityFxRules.MeasureSchedule);
            return failing;
        }

        private static bool Fails(string measure, double? value)
            => value.HasValue && PartParityFxRules.BurstMeasureLevel(measure, value.Value) == PartParityFxRules.LevelFail;

        private static string ColourLevel(double? chroma, double? hue)
            => PartParityFxRules.BurstColourLevel(chroma ?? double.NaN, hue ?? double.NaN);

        /// <summary>
        /// The split-half disagreement of one side (trials 1 and 3 against 2 and 4): the halves
        /// disagree on presence (<see cref="PresenceReason"/> between them), or their largest
        /// cores differ by more than <see cref="PartParityFxRules.SplitHalfPct"/> of the larger.
        /// Null when they agree.
        /// </summary>
        internal static string SplitHalfDisagreement(string layer, PartParityBurstSideMeasures even, PartParityBurstSideMeasures odd)
        {
            if (even == null || odd == null) return null;
            string presence = PresenceReason(layer, even, odd);
            if (presence != null) return "presence " + presence;
            int larger = Math.Max(even.PeakCorePx, odd.PeakCorePx);
            if (larger > 0 && Math.Abs(even.PeakCorePx - odd.PeakCorePx) > PartParityFxRules.SplitHalfPct * larger)
                return "peakCorePx " + even.PeakCorePx + "/" + odd.PeakCorePx;
            return null;
        }

        // =====================================================================================
        // JSON (every key of its PartParityJson table, null where undefined)
        // =====================================================================================

        internal static string BinJson(PartParityBurstBinMeasures m)
        {
            if (m == null) return "null";
            return PartParityJson.KeyedBlock(PartParityJson.BurstBinMeasureKeys, new Dictionary<string, string>
            {
                { "corePx", PartParityJson.Int(m.CorePx) },
                { "envelopePx", PartParityJson.Int(m.EnvelopePx) },
                { "saturatedPx", PartParityJson.Int(m.SaturatedPx) },
                { "coreCentroid", PartParityJson.NumArray(m.CoreCentroid) },
                { "radiusPx", PartParityJson.Num(m.RadiusPx) },
                { "meanColor", PartParityJson.NumArray(m.MeanColor) },
                { "chromaticity", PartParityJson.NumArray(m.Chromaticity) },
                { "luminance", PartParityJson.Num(m.Luminance) },
                { "hueHistogram", PartParityJson.NumArray(m.HueHistogram) },
            });
        }

        internal static string SideJson(PartParityBurstSideMeasures s)
        {
            if (s == null) return "null";
            var bins = new List<string>();
            foreach (PartParityBurstBinMeasures m in s.Bins) bins.Add(BinJson(m));
            return PartParityJson.KeyedBlock(PartParityJson.BurstSideMeasureKeys, new Dictionary<string, string>
            {
                { "bins", "[" + string.Join(",", bins.ToArray()) + "]" },
                { "onsetBin", IntOrNull(s.OnsetBin) },
                { "peakBin", IntOrNull(s.PeakBin) },
                { "lastCoreBin", IntOrNull(s.LastCoreBin) },
                { "peakCorePx", PartParityJson.Int(s.PeakCorePx) },
            });
        }

        internal static string CompareJson(PartParityBurstCompare c)
        {
            if (c == null) return "null";
            return PartParityJson.KeyedBlock(PartParityJson.BurstCompareKeys, new Dictionary<string, string>
            {
                { "presence", PartParityJson.Str(c.Presence) },
                { "onsetDeltaBins", PartParityJson.Num(c.OnsetDeltaBins) },
                { "peakTimeDeltaBins", PartParityJson.Num(c.PeakTimeDeltaBins) },
                { "durationDelta", PartParityJson.Num(c.DurationDelta) },
                { "peakAreaRatio", PartParityJson.Num(c.PeakAreaRatio) },
                { "radiusRatio", PartParityJson.Num(c.RadiusRatio) },
                { "centroidOffset", PartParityJson.Num(c.CentroidOffset) },
                { "chromaDistance", PartParityJson.Num(c.ChromaDistance) },
                { "hueL1", PartParityJson.Num(c.HueL1) },
                { "brightnessRatio", PartParityJson.Num(c.BrightnessRatio) },
                { "chromaDistanceMean", PartParityJson.Num(c.ChromaDistanceMean) },
                { "hueL1Mean", PartParityJson.Num(c.HueL1Mean) },
            });
        }

        internal static string SourceJson(PartParityBurstSource s)
        {
            if (s == null) return "null";
            return PartParityJson.KeyedBlock(PartParityJson.BurstSourceKeys, new Dictionary<string, string>
            {
                { "realPx", PartParityJson.NumArray(s.RealPx) },
                { "ghostPx", PartParityJson.NumArray(s.GhostPx) },
                { "diameterPx", PartParityJson.Num(s.DiameterPx) },
            });
        }

        internal static string BlastJson(PartParityBurstBlast b)
        {
            if (b == null) return "null";
            return PartParityJson.KeyedBlock(PartParityJson.BurstScheduleEntryKeys, new Dictionary<string, string>
            {
                { "frame", PartParityJson.Int(b.Frame) },
                { "t", PartParityJson.Num(b.T) },
                { "offsetM", PartParityJson.NumArray(b.OffsetM) },
                { "power", PartParityJson.Num(b.Power) },
                { "prefab", PartParityJson.Str(b.Prefab) },
                { "index", PartParityJson.Int(b.Index) },
            });
        }

        internal static string ScheduleJson(IList<PartParityBurstBlast> real, IList<PartParityBurstBlast> ghost)
        {
            string List(IList<PartParityBurstBlast> l)
            {
                var items = new List<string>();
                if (l != null) foreach (PartParityBurstBlast b in l) items.Add(BlastJson(b));
                return "[" + string.Join(",", items.ToArray()) + "]";
            }
            return PartParityJson.KeyedBlock(PartParityJson.BurstSideKeys, new Dictionary<string, string>
            {
                { PartParityFxRules.SideReal, List(real) },
                { PartParityFxRules.SideGhost, List(ghost) },
            });
        }

        internal static string ScheduleCompareJson(PartParityBurstScheduleCompare s)
        {
            if (s == null) return "null";
            return PartParityJson.KeyedBlock(PartParityJson.BurstScheduleCompareKeys, new Dictionary<string, string>
            {
                { "realCount", PartParityJson.Int(s.RealCount) },
                { "ghostCount", PartParityJson.Int(s.GhostCount) },
                { "countDiffers", PartParityJson.Bool(s.CountDiffers) },
                { "indexDiffers", PartParityJson.Bool(s.IndexDiffers) },
                { "powerDiffers", PartParityJson.Bool(s.PowerDiffers) },
                { "maxOffsetDeltaM", PartParityJson.Num(s.MaxOffsetDeltaM) },
            });
        }

        private static string IntOrNull(int? v) => v.HasValue ? PartParityJson.Int(v.Value) : "null";
    }

    /// <summary>
    /// One <c>burst.views[]</c> entry measured (design G.3): a view and layer over every bin,
    /// the real and ghost tiles of the whole trial set (the ones the atlas holds) and of each
    /// half (numbers only), their measures and comparisons.
    /// </summary>
    internal sealed class PartParityBurstViewResult
    {
        internal string View;
        internal string Layer;
        internal int Width;
        internal int Height;
        internal PartParityBurstSource Source;
        internal byte[][] RealTiles;
        internal byte[][] GhostTiles;
        internal int[] RealSamples;
        internal int[] GhostSamples;
        internal PartParityBurstSideMeasures Real, Ghost, RealEven, GhostEven, RealOdd, GhostOdd;
        internal PartParityBurstCompare Compare, CompareEven, CompareOdd;
        internal int[] EvenSamples, OddSamples;

        /// <summary>Measures one entry from the two sides' accumulators (same size and bins).</summary>
        internal static PartParityBurstViewResult Measure(string view, string layer, PartParityBurstSource source,
            PartParityBurstAccumulator real, PartParityBurstAccumulator ghost)
        {
            if (real == null || ghost == null) throw new ArgumentNullException(real == null ? nameof(real) : nameof(ghost));
            if (real.Width != ghost.Width || real.Height != ghost.Height || real.BinCount != ghost.BinCount)
                throw new ArgumentException("accumulators differ in size or bins");
            int w = real.Width, h = real.Height, n = real.BinCount;
            var r = new PartParityBurstViewResult
            {
                View = view, Layer = layer, Width = w, Height = h, Source = source,
                RealTiles = new byte[n][], GhostTiles = new byte[n][],
                RealSamples = new int[n], GhostSamples = new int[n], EvenSamples = new int[n], OddSamples = new int[n],
            };
            r.Real = Side(real, PartParityBurstHalf.All, source?.RealPx, r.RealTiles, r.RealSamples);
            r.Ghost = Side(ghost, PartParityBurstHalf.All, source?.GhostPx, r.GhostTiles, r.GhostSamples);
            r.Compare = PartParityBurstMetrics.Compare(r.Real, r.Ghost, source, layer);
            r.RealEven = Side(real, PartParityBurstHalf.Even, source?.RealPx, null, r.EvenSamples);
            r.GhostEven = Side(ghost, PartParityBurstHalf.Even, source?.GhostPx, null, null);
            r.CompareEven = PartParityBurstMetrics.Compare(r.RealEven, r.GhostEven, source, layer);
            r.RealOdd = Side(real, PartParityBurstHalf.Odd, source?.RealPx, null, r.OddSamples);
            r.GhostOdd = Side(ghost, PartParityBurstHalf.Odd, source?.GhostPx, null, null);
            r.CompareOdd = PartParityBurstMetrics.Compare(r.RealOdd, r.GhostOdd, source, layer);
            return r;
        }

        private static PartParityBurstSideMeasures Side(PartParityBurstAccumulator acc, PartParityBurstHalf half,
            double[] sourcePx, byte[][] tiles, int[] samples)
        {
            var bins = new List<PartParityBurstBinMeasures>();
            for (int b = 0; b < acc.BinCount; b++)
            {
                PartParityFxAccumulator a = acc.Bin(b, half);
                byte[] tile = a.ToTile();
                if (tiles != null) tiles[b] = tile;
                if (samples != null) samples[b] = a.Samples;
                bins.Add(PartParityBurstMetrics.MeasureBin(tile, acc.Width, acc.Height, a.Samples, sourcePx));
            }
            return PartParityBurstMetrics.MeasureSide(bins);
        }

        /// <summary>The failing measures of the whole trial set (<see cref="PartParityBurstMetrics.FailingMeasures"/>).</summary>
        internal List<string> FailingMeasures() => PartParityBurstMetrics.FailingMeasures(Layer, Real, Ghost, Compare);

        /// <summary>The split-half disagreement of this entry, the real side's first; null when
        /// both sides' halves agree.</summary>
        internal string SplitHalfDisagreement()
        {
            string real = PartParityBurstMetrics.SplitHalfDisagreement(Layer, RealEven, RealOdd);
            if (real != null) return PartParityFxRules.SideReal + " " + real;
            string ghost = PartParityBurstMetrics.SplitHalfDisagreement(Layer, GhostEven, GhostOdd);
            return ghost != null ? PartParityFxRules.SideGhost + " " + ghost : null;
        }

        /// <summary>The entry as JSON, every <see cref="PartParityJson.BurstViewKeys"/> key.</summary>
        internal string Json()
        {
            string Half(string trials, int[] samples, PartParityBurstSideMeasures re, PartParityBurstSideMeasures gh,
                PartParityBurstCompare c)
                => PartParityJson.KeyedBlock(PartParityJson.BurstHalfKeys, new Dictionary<string, string>
                {
                    { "trials", trials },
                    { "samples", IntArray(samples) },
                    { "real", PartParityBurstMetrics.SideJson(re) },
                    { "ghost", PartParityBurstMetrics.SideJson(gh) },
                    { "compare", PartParityBurstMetrics.CompareJson(c) },
                });
            string split = PartParityJson.KeyedBlock(PartParityJson.FxSplitHalfKeys, new Dictionary<string, string>
            {
                { "even", Half("[1,3]", EvenSamples, RealEven, GhostEven, CompareEven) },
                { "odd", Half("[2,4]", OddSamples, RealOdd, GhostOdd, CompareOdd) },
            });
            return PartParityJson.KeyedBlock(PartParityJson.BurstViewKeys, new Dictionary<string, string>
            {
                { "view", PartParityJson.Str(View) },
                { "layer", PartParityJson.Str(Layer) },
                { "width", PartParityJson.Int(Width) },
                { "height", PartParityJson.Int(Height) },
                { "source", PartParityBurstMetrics.SourceJson(Source) },
                { "real", PartParityBurstMetrics.SideJson(Real) },
                { "ghost", PartParityBurstMetrics.SideJson(Ghost) },
                { "compare", PartParityBurstMetrics.CompareJson(Compare) },
                { "splitHalf", split },
            });
        }

        private static string IntArray(int[] v)
        {
            if (v == null) return "null";
            var parts = new string[v.Length];
            for (int i = 0; i < v.Length; i++) parts[i] = PartParityJson.Int(v[i]);
            return "[" + string.Join(",", parts) + "]";
        }
    }
}
