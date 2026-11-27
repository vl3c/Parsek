using System;
using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>One view of one motion frame: the same-time pair's and the matched pair's
    /// measures (null when the tile was left empty).</summary>
    internal sealed class PartParityMotionFrameView
    {
        internal string View;
        internal PartParityViewRecord Same;
        internal PartParityViewRecord Matched;
    }

    /// <summary>One <c>frames[]</c> entry of a motion case (Appendix H.2).</summary>
    internal sealed class PartParityMotionFrame
    {
        internal int K;
        internal double TargetS = double.NaN;
        internal double RealS = double.NaN;
        internal double Ut = double.NaN;
        internal double RealClock = double.NaN;
        internal double GhostClock = double.NaN;
        internal double RealProgress = double.NaN;
        internal double GhostProgress = double.NaN;
        internal double MatchedS = double.NaN;
        internal double MatchedProgress = double.NaN;

        /// <summary>The picture progress of this frame in every atlas row, in row order
        /// (<see cref="ImgProgressRows"/>); NaN, written null, on an empty matched slot.</summary>
        internal double[] ImgProgress;
        internal IList<string> ImgProgressRows;
        internal readonly List<PartParityMotionFrameView> Views = new List<PartParityMotionFrameView>();
        internal readonly List<string> Flags = new List<string>();
    }

    /// <summary>Everything a motion case's <c>motion</c> block holds (Appendix H.2), filled by
    /// the live driver and written by <see cref="PartParityMotionMetrics.MotionBlock"/>.</summary>
    internal sealed class PartParityMotionRecord
    {
        internal PartParityMotionOption Option;
        internal double PressUT = double.NaN;
        internal double PredictedS = double.NaN;
        internal double GhostClipS = double.NaN;
        internal double EventDelayS = double.NaN;
        internal double GhostStartDelayS = double.NaN;
        internal int FramesPerMotion;
        internal PartParityMotionWindow Window;
        internal double ScoutStepS = PartParityMotionRules.ScoutStepSeconds;
        internal readonly List<PartParityMotionFrame> Frames = new List<PartParityMotionFrame>();
        internal PartParityMotionSeries Series;

        /// <summary>The channel each <see cref="PartParityMotionSeries.Transforms"/> entry is read in.</summary>
        internal readonly List<string> TransformChannels = new List<string>();
        internal PartParityMotionTiming Timing;
    }

    /// <summary>
    /// The per-frame measures of a motion case (design-part-ghost-parity.md Appendix H.1, H.2),
    /// pure over the 8-bit tiles the case writes and the poses it read, so
    /// <c>harness/lib/partparity.py</c> recomputes them: the tolerant mismatch of section 6, the
    /// picture progress of every atlas row, the pose progress series of both sides, the
    /// progress a render shows on its scout's track, and the <c>motion</c> block. Every
    /// threshold lives in <see cref="PartParityMotionRules"/>; this class holds none.
    /// </summary>
    internal static class PartParityMotionMetrics
    {
        // =====================================================================================
        // picture progress (H.1)
        // =====================================================================================

        /// <summary>
        /// The tolerant mismatch of section 6 between two tiles: the pixels covered in one and
        /// not in the other with no covered pixel of the other in their neighbourhood, both
        /// ways (<see cref="PartParityMetrics.Measure"/>'s <c>realOnlyTolPx + ghostOnlyTolPx</c>,
        /// without the blob and colour work).
        /// </summary>
        internal static int TolerantMismatchPx(byte[] a, byte[] b, int width, int height)
        {
            if (a == null || b == null) throw new ArgumentNullException(a == null ? nameof(a) : nameof(b));
            long expected = (long)width * height * 4;
            if (width <= 0 || height <= 0 || a.Length != expected || b.Length != expected)
                throw new ArgumentException("RGBA arrays do not match width x height x 4");
            bool[] ma = PartParityMetrics.CoverageMask(a);
            bool[] mb = PartParityMetrics.CoverageMask(b);
            int n = width * height, count = 0;
            for (int p = 0; p < n; p++)
            {
                if (ma[p] && !mb[p] && !PartParityMetrics.AnyInNeighbourhood(mb, width, height, p)) count++;
                else if (mb[p] && !ma[p] && !PartParityMetrics.AnyInNeighbourhood(ma, width, height, p)) count++;
            }
            return count;
        }

        /// <summary>A transparent RGBA tile: what the atlas holds in an empty slot.</summary>
        internal static byte[] EmptyTile(int width, int height) => new byte[Math.Max(0, width) * Math.Max(0, height) * 4];

        /// <summary>
        /// The picture progress of every tile of one atlas row (H.1): a = the tolerant mismatch
        /// against the row's frame 0, b = against its last frame, p = a / (a + b), over the
        /// tiles the atlas holds (an empty slot is a transparent tile, so a row whose frame 0
        /// is empty is measured against nothing drawn). NaN for an empty slot itself: the
        /// matched pair with no matched tile, whose block is null.
        /// </summary>
        internal static double[] RowPictureProgress(IList<byte[]> row, int width, int height)
        {
            int n = row?.Count ?? 0;
            var p = new double[n];
            if (n == 0) return p;
            byte[] first = row[0] ?? EmptyTile(width, height);
            byte[] last = row[n - 1] ?? EmptyTile(width, height);
            for (int k = 0; k < n; k++)
            {
                if (row[k] == null) { p[k] = double.NaN; continue; }
                int a = TolerantMismatchPx(first, row[k], width, height);
                int b = TolerantMismatchPx(row[k], last, width, height);
                p[k] = PartParityMotionRules.PictureProgress(a, b);
            }
            return p;
        }

        /// <summary>The <c>imgProgress</c> object of one frame: keyed by atlas row name, a NaN
        /// written null.</summary>
        internal static string ImgProgressBlock(IList<string> rows, double[] progress)
        {
            if (rows == null || progress == null) return "null";
            var members = new KeyValuePair<string, string>[Math.Min(rows.Count, progress.Length)];
            for (int i = 0; i < members.Length; i++) members[i] = M(rows[i], PartParityJson.Num(progress[i]));
            return PartParityJson.Obj(members);
        }

        // =====================================================================================
        // pose progress (H.1)
        // =====================================================================================

        private static double Step(PartParityMotionPose a, PartParityMotionPose b, string channel)
        {
            switch (channel)
            {
                case PartParityMotionRules.ChannelPosition: return Distance(a.Position, b.Position);
                case PartParityMotionRules.ChannelRotation: return PartParityMotionRules.RotationStepDeg(a.Rotation, b.Rotation);
                case PartParityMotionRules.ChannelScale: return Distance(a.Scale, b.Scale);
                default: return 0.0;
            }
        }

        private static double Distance(double[] a, double[] b)
        {
            double d = 0.0;
            for (int i = 0; i < a.Length; i++) d += (a[i] - b[i]) * (a[i] - b[i]);
            return Math.Sqrt(d);
        }

        /// <summary>A track's accumulated travel in one channel, frame to frame.</summary>
        internal static double[] CumulativeTravel(PartParityMotionTrack track, string channel)
        {
            int n = track?.Poses?.Count ?? 0;
            var cum = new double[n];
            for (int k = 1; k < n; k++) cum[k] = cum[k - 1] + Step(track.Poses[k - 1], track.Poses[k], channel);
            return cum;
        }

        /// <summary>A track's fraction of its own total travel in <paramref name="channel"/> at
        /// every pose (all zero when it did not move in that channel).</summary>
        internal static double[] FractionsIn(PartParityMotionTrack track, string channel)
        {
            double[] cum = CumulativeTravel(track, channel);
            double total = cum.Length > 0 ? cum[cum.Length - 1] : 0.0;
            var f = new double[cum.Length];
            if (total > 0.0)
                for (int k = 0; k < cum.Length; k++) f[k] = cum[k] / total;
            return f;
        }

        /// <summary>The index of <paramref name="at"/> in an increasing series: the exact
        /// entry within <see cref="PartParityMotionRules.ScoutMergeEpsilonSeconds"/>, else the
        /// nearest; -1 for an empty series.</summary>
        internal static int IndexOfOffset(IList<double> s, double at)
        {
            if (s == null || s.Count == 0 || double.IsNaN(at)) return -1;
            int best = 0;
            for (int i = 0; i < s.Count; i++)
            {
                if (Math.Abs(s[i] - at) <= PartParityMotionRules.ScoutMergeEpsilonSeconds) return i;
                if (Math.Abs(s[i] - at) < Math.Abs(s[best] - at)) best = i;
            }
            return best;
        }

        /// <summary>The change of a series across the interval its interpolation at
        /// <paramref name="at"/> used (0 at or outside its ends): a matched render's allowance
        /// (<see cref="PartParityMotionRules.RenderAgrees"/>).</summary>
        internal static double IntervalChange(IList<double> s, IList<double> p, double at)
        {
            if (s == null || p == null || s.Count < 2 || s.Count != p.Count || double.IsNaN(at)) return 0.0;
            for (int k = 1; k < s.Count; k++)
                if (at <= s[k] + PartParityMotionRules.ScoutMergeEpsilonSeconds)
                    return at < s[k - 1] ? 0.0 : p[k] - p[k - 1];
            return 0.0;
        }

        /// <summary>
        /// The pose progress a render shows, read on its scout's track: per transform that moved
        /// on the scout, the scout pose nearest the render's in that transform's channel, refined
        /// linearly toward the nearer neighbour, its fraction there; the side's value the same
        /// travel-weighted mean as <see cref="PartParityMotionRules.SideProgress"/>. A render at
        /// a scout UT lands exactly on that point (the pose is a pure function of the event UTs),
        /// so the same-time check of H.2 step 3 compares like with like. NaN when no scout
        /// transform moved or the poses do not line up with the tracks.
        /// </summary>
        internal static double ProgressOnScout(IList<PartParityMotionTrack> scoutTracks,
            PartParityMotionSideProgress scoutSide, IList<PartParityMotionPose> renderPoses)
        {
            if (scoutTracks == null || scoutSide == null || renderPoses == null
                || renderPoses.Count != scoutTracks.Count || scoutSide.Transforms.Count != scoutTracks.Count)
                return double.NaN;
            double weights = 0.0, sum = 0.0;
            for (int i = 0; i < scoutTracks.Count; i++)
            {
                PartParityMotionTransformProgress t = scoutSide.Transforms[i];
                PartParityMotionTrack track = scoutTracks[i];
                PartParityMotionPose pose = renderPoses[i];
                if (t?.Channel == null || track?.Poses == null || pose == null || track.Poses.Count == 0) continue;
                int n = track.Poses.Count;
                var d = new double[n];
                int best = 0;
                for (int k = 0; k < n; k++)
                {
                    d[k] = Step(track.Poses[k], pose, t.Channel);
                    if (d[k] < d[best]) best = k;
                }
                double f = t.Fraction[best];
                if (d[best] > 0.0)
                {
                    int other = best == 0 ? 1 : best == n - 1 ? n - 2
                        : (d[best - 1] <= d[best + 1] ? best - 1 : best + 1);
                    if (other >= 0 && other < n && d[best] + d[other] > 0.0)
                        f += (t.Fraction[other] - t.Fraction[best]) * d[best] / (d[best] + d[other]);
                }
                weights += t.Weight;
                sum += t.Weight * f;
            }
            return weights > 0.0 ? sum / weights : double.NaN;
        }

        /// <summary>
        /// The series the timing reads (H.1): both sides' pose progress from their tracks (each
        /// normalised by its own first and last pose), the ghost's at the real frames' own
        /// offsets taken off the scout (whose grid carries every one of them), and per transform
        /// paired by index the real fractions at the real frames and the ghost fractions on the
        /// scout in the REAL side's channel (the ghost's own when the real did not move).
        /// <paramref name="channels"/> receives each entry's channel. The clocks are a servo's.
        /// </summary>
        internal static PartParityMotionSeries BuildSeries(IList<double> realS, IList<PartParityMotionTrack> realTracks,
            IList<double> scoutS, IList<PartParityMotionTrack> scoutTracks, IList<double> realClock, IList<double> ghostClock,
            List<string> channels, out PartParityMotionSideProgress realSide, out PartParityMotionSideProgress scoutSide)
        {
            int nReal = realS?.Count ?? 0, nScout = scoutS?.Count ?? 0;
            realSide = PartParityMotionRules.SideProgress(realTracks, nReal);
            scoutSide = PartParityMotionRules.SideProgress(scoutTracks, nScout);
            var s = new PartParityMotionSeries
            {
                RealS = ToArray(realS), RealProgress = realSide.Progress,
                ScoutS = ToArray(scoutS), ScoutProgress = scoutSide.Progress,
                GhostSameProgress = new double[nReal],
                RealClock = realClock != null ? ToArray(realClock) : null,
                GhostClock = ghostClock != null ? ToArray(ghostClock) : null,
            };
            for (int k = 0; k < nReal; k++)
                s.GhostSameProgress[k] = nScout > 0 ? PartParityMotionRules.Interpolate(s.ScoutS, s.ScoutProgress, realS[k]) : double.NaN;
            int pairs = Math.Min(realTracks?.Count ?? 0, scoutTracks?.Count ?? 0);
            for (int i = 0; i < pairs; i++)
            {
                PartParityMotionTransformProgress r = realSide.Transforms[i], g = scoutSide.Transforms[i];
                string channel = r.Channel ?? g.Channel;
                channels?.Add(channel);
                var tr = new PartParityMotionTransformSeries { Path = r.Path ?? g.Path };
                if (channel != null)
                {
                    tr.TravelReal = r.TravelIn(channel);
                    tr.TravelGhost = g.TravelIn(channel);
                    tr.Real = FractionsIn(realTracks[i], channel);
                    tr.Ghost = FractionsIn(scoutTracks[i], channel);
                }
                else
                {
                    // A still transform (neither side past the still thresholds): no channel and
                    // no travel, but one zero fraction per frame and per scout point, so the
                    // arrays keep the lengths partparity's block check requires.
                    tr.Real = new double[realTracks[i]?.Poses?.Count ?? 0];
                    tr.Ghost = new double[scoutTracks[i]?.Poses?.Count ?? 0];
                }
                s.Transforms.Add(tr);
            }
            return s;
        }

        private static double[] ToArray(IList<double> v)
        {
            if (v == null) return new double[0];
            var a = new double[v.Count];
            v.CopyTo(a, 0);
            return a;
        }

        // =====================================================================================
        // the sidecar blocks (H.2)
        // =====================================================================================

        private static KeyValuePair<string, string> M(string key, string value) => new KeyValuePair<string, string>(key, value);

        /// <summary>One <c>frames[]</c> entry, every <see cref="PartParityJson.MotionFrameKeys"/> key.</summary>
        internal static string FrameBlock(PartParityMotionFrame f)
        {
            var views = new List<string>();
            foreach (PartParityMotionFrameView v in f.Views)
                views.Add(PartParityJson.Obj(M("view", PartParityJson.Str(v.View)),
                    M("same", PartParityJson.MeasureBlock(v.Same)), M("matched", PartParityJson.MeasureBlock(v.Matched))));
            return PartParityJson.Obj(
                M("k", PartParityJson.Int(f.K)), M("targetS", PartParityJson.Num(f.TargetS)),
                M("realS", PartParityJson.Num(f.RealS)), M("ut", PartParityJson.Num(f.Ut)),
                M("realClock", PartParityJson.Num(f.RealClock)), M("ghostClock", PartParityJson.Num(f.GhostClock)),
                M("realProgress", PartParityJson.Num(f.RealProgress)), M("ghostProgress", PartParityJson.Num(f.GhostProgress)),
                M("matchedS", PartParityJson.Num(f.MatchedS)), M("matchedProgress", PartParityJson.Num(f.MatchedProgress)),
                M("imgProgress", ImgProgressBlock(f.ImgProgressRows, f.ImgProgress)),
                M("views", PartParityJson.RawArray(views)), M("flags", PartParityJson.StrArray(f.Flags)));
        }

        /// <summary>The <c>motion</c> block's fragments, one per <see cref="PartParityJson.MotionKeys"/> key.</summary>
        internal static Dictionary<string, string> MotionBlock(PartParityMotionRecord r)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (r == null) return d;
            PartParityMotionOption o = r.Option;
            d["family"] = PartParityJson.Str(o?.Family);
            d["kind"] = PartParityJson.Str(o?.Kind);
            d["module"] = PartParityJson.Str(o?.Module);
            d["moduleIndex"] = o != null ? PartParityJson.Int(o.ModuleIndex) : "null";
            d["pressUT"] = PartParityJson.Num(r.PressUT);
            d["predictedS"] = PartParityJson.Num(r.PredictedS);
            d["ghostClipS"] = PartParityJson.Num(r.GhostClipS);
            d["eventDelayS"] = PartParityJson.Num(r.EventDelayS);
            d["ghostStartDelayS"] = PartParityJson.Num(r.GhostStartDelayS);
            d["framesPerMotion"] = PartParityJson.Int(r.FramesPerMotion);
            d["window"] = PartParityJson.MotionWindowBlock(r.Window);
            d["scout"] = PartParityJson.Obj(M("stepS", PartParityJson.Num(r.ScoutStepS)),
                M("s", PartParityJson.NumArray(r.Series?.ScoutS)), M("progress", PartParityJson.NumArray(r.Series?.ScoutProgress)));
            var frames = new List<string>();
            foreach (PartParityMotionFrame f in r.Frames) frames.Add(FrameBlock(f));
            d["frames"] = PartParityJson.RawArray(frames);
            var transforms = new List<string>();
            if (r.Series != null)
                for (int i = 0; i < r.Series.Transforms.Count; i++)
                {
                    PartParityMotionTransformSeries t = r.Series.Transforms[i];
                    transforms.Add(PartParityJson.Obj(M("path", PartParityJson.Str(t.Path)),
                        M("channel", PartParityJson.Str(i < r.TransformChannels.Count ? r.TransformChannels[i] : null)),
                        M("travelReal", PartParityJson.Num(t.TravelReal)), M("travelGhost", PartParityJson.Num(t.TravelGhost)),
                        M("real", PartParityJson.NumArray(t.Real)), M("ghost", PartParityJson.NumArray(t.Ghost))));
                }
            d["transforms"] = PartParityJson.RawArray(transforms);
            d["timing"] = PartParityJson.MotionTimingBlock(r.Timing);
            return d;
        }

        /// <summary>The <c>motionAtlas</c> block's fragments: <c>cols</c> lists the frames' <c>k</c>
        /// in column order.</summary>
        internal static Dictionary<string, string> MotionAtlasBlock(string file, int tile, IList<string> rows, IList<int> cols)
        {
            var ks = new List<string>();
            if (cols != null) foreach (int k in cols) ks.Add(PartParityJson.Int(k));
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "file", PartParityJson.Str(file) }, { "tile", PartParityJson.Int(tile) },
                { "rows", PartParityJson.StrArray(rows) }, { "cols", PartParityJson.RawArray(ks) },
            };
        }
    }
}
