using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for <see cref="PartParityMotionMetrics"/> (design-part-ghost-parity.md
    /// Appendix H.1, H.2, piece M3): the tolerant mismatch against section 6's measure, each
    /// atlas row's picture progress as partparity.py recomputes it (a / (a + b) against the
    /// row's frame 0 and last tile, an empty slot a transparent tile), the pose progress
    /// helpers, the same-time ghost progress read off the scout by interpolation, and the
    /// sidecar block shapes M4 reads (every key, <c>imgProgress</c> keyed by row, <c>cols</c>
    /// the frames' <c>k</c>).
    /// </summary>
    public class PartParityMotionMetricsTests
    {
        private const int W = 8;

        private static byte[] Tile(params int[] coveredPixels)
        {
            var t = new byte[W * W * 4];
            foreach (int p in coveredPixels)
            {
                t[p * 4] = 200; t[p * 4 + 1] = 100; t[p * 4 + 2] = 50; t[p * 4 + 3] = 255;
            }
            return t;
        }

        private static int[] Block(int x0, int y0, int w, int h)
        {
            var px = new List<int>();
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++) px.Add(y * W + x);
            return px.ToArray();
        }

        [Fact]
        public void TheTolerantMismatch_IsTheMeasureOfSectionSix()
        {
            var rng = new Random(7);
            for (int trial = 0; trial < 20; trial++)
            {
                var a = Enumerable.Range(0, W * W).Where(_ => rng.NextDouble() < 0.3).ToArray();
                var b = Enumerable.Range(0, W * W).Where(_ => rng.NextDouble() < 0.3).ToArray();
                PartParityViewMeasures m = PartParityMetrics.Measure(Tile(a), Tile(b), W, W, double.NaN);
                Assert.Equal(m.RealOnlyTolPx + m.GhostOnlyTolPx, PartParityMotionMetrics.TolerantMismatchPx(Tile(a), Tile(b), W, W));
                Assert.Equal(PartParityMotionMetrics.TolerantMismatchPx(Tile(a), Tile(b), W, W),
                    PartParityMotionMetrics.TolerantMismatchPx(Tile(b), Tile(a), W, W));
            }
        }

        [Fact]
        public void TheTolerantMismatch_RefusesMismatchedArrays()
        {
            Assert.Throws<ArgumentNullException>(() => PartParityMotionMetrics.TolerantMismatchPx(null, Tile(), W, W));
            Assert.Throws<ArgumentException>(() => PartParityMotionMetrics.TolerantMismatchPx(new byte[4], Tile(), W, W));
        }

        [Fact]
        public void PictureProgress_RunsFromZeroAtFrameZeroToOneAtTheLastFrame()
        {
            var row = new List<byte[]> { Tile(Block(0, 0, 2, 2)), Tile(Block(3, 3, 2, 2)), Tile(Block(6, 6, 2, 2)) };
            double[] p = PartParityMotionMetrics.RowPictureProgress(row, W, W);
            Assert.Equal(0.0, p[0]);
            Assert.Equal(1.0, p[2]);
            int a = PartParityMotionMetrics.TolerantMismatchPx(row[0], row[1], W, W);
            int b = PartParityMotionMetrics.TolerantMismatchPx(row[1], row[2], W, W);
            Assert.Equal((double)a / (a + b), p[1], 12);
        }

        [Fact]
        public void PictureProgress_ReadsAnEmptySlotAsATransparentTile_AndIsNaNOnTheSlotItself()
        {
            // A matched row whose frame 0 had no match: the atlas holds a transparent tile there,
            // so the other slots are measured against nothing drawn (partparity.py reads the atlas).
            var row = new List<byte[]> { null, Tile(Block(2, 2, 2, 2)), Tile(Block(5, 5, 2, 2)) };
            double[] p = PartParityMotionMetrics.RowPictureProgress(row, W, W);
            Assert.True(double.IsNaN(p[0]));
            int a = PartParityMotionMetrics.TolerantMismatchPx(PartParityMotionMetrics.EmptyTile(W, W), row[1], W, W);
            int b = PartParityMotionMetrics.TolerantMismatchPx(row[1], row[2], W, W);
            Assert.Equal((double)a / (a + b), p[1], 12);
            Assert.Equal(1.0, p[2]);
            Assert.Empty(PartParityMotionMetrics.RowPictureProgress(new List<byte[]>(), W, W));
        }

        [Fact]
        public void PictureProgress_OfAStillRow_IsZero()
        {
            byte[] t = Tile(Block(1, 1, 3, 3));
            double[] p = PartParityMotionMetrics.RowPictureProgress(new List<byte[]> { t, t, t }, W, W);
            Assert.All(p, v => Assert.Equal(0.0, v));
        }

        private static PartParityMotionPose Pose(double x, double scale = 1.0)
            => new PartParityMotionPose { Position = new[] { x, 0.0, 0.0 }, Scale = new[] { scale, scale, scale } };

        private static PartParityMotionTrack Track(string path, params double[] xs)
        {
            var t = new PartParityMotionTrack { Path = path };
            foreach (double x in xs) t.Poses.Add(Pose(x));
            return t;
        }

        [Fact]
        public void FractionsIn_IsTheTravelSinceFrameZeroOverTheTotal()
        {
            PartParityMotionTrack t = Track("a", 0.0, 0.1, 0.1, 0.4);
            double[] f = PartParityMotionMetrics.FractionsIn(t, PartParityMotionRules.ChannelPosition);
            Assert.Equal(new[] { 0.0, 0.25, 0.25, 1.0 }, f.Select(v => Math.Round(v, 12)).ToArray());
            Assert.All(PartParityMotionMetrics.FractionsIn(t, PartParityMotionRules.ChannelScale), v => Assert.Equal(0.0, v));
            Assert.Equal(new[] { 0.0, 0.1, 0.1, 0.4 },
                PartParityMotionMetrics.CumulativeTravel(t, PartParityMotionRules.ChannelPosition).Select(v => Math.Round(v, 12)).ToArray());
        }

        [Fact]
        public void IndexOfOffset_TakesTheExactEntryElseTheNearest()
        {
            var s = new List<double> { 0.0, 0.05, 0.1, 0.15 };
            Assert.Equal(2, PartParityMotionMetrics.IndexOfOffset(s, 0.1));
            Assert.Equal(1, PartParityMotionMetrics.IndexOfOffset(s, 0.06));
            Assert.Equal(3, PartParityMotionMetrics.IndexOfOffset(s, 9.0));
            Assert.Equal(-1, PartParityMotionMetrics.IndexOfOffset(new List<double>(), 0.1));
            Assert.Equal(-1, PartParityMotionMetrics.IndexOfOffset(s, double.NaN));
        }

        [Fact]
        public void IntervalChange_IsTheStepOfTheBracketingInterval()
        {
            var s = new List<double> { 0.0, 1.0, 2.0 };
            var p = new List<double> { 0.0, 0.3, 1.0 };
            Assert.Equal(0.3, PartParityMotionMetrics.IntervalChange(s, p, 0.5), 12);
            Assert.Equal(0.7, PartParityMotionMetrics.IntervalChange(s, p, 1.5), 12);
            Assert.Equal(0.0, PartParityMotionMetrics.IntervalChange(s, p, 5.0));
            Assert.Equal(0.0, PartParityMotionMetrics.IntervalChange(s, p, -1.0));
            Assert.Equal(0.0, PartParityMotionMetrics.IntervalChange(s, new List<double> { 0.0 }, 0.5));
        }

        [Fact]
        public void ProgressOnScout_LandsOnTheScoutPointARenderMatches()
        {
            var scout = new List<PartParityMotionTrack> { Track("a", 0.0, 0.1, 0.2, 0.3, 0.4) };
            PartParityMotionSideProgress side = PartParityMotionRules.SideProgress(scout, 5);
            double on = PartParityMotionMetrics.ProgressOnScout(scout, side, new List<PartParityMotionPose> { Pose(0.2) });
            Assert.Equal(side.Progress[2], on, 9);
            double between = PartParityMotionMetrics.ProgressOnScout(scout, side, new List<PartParityMotionPose> { Pose(0.25) });
            Assert.InRange(between, side.Progress[2], side.Progress[3]);
            Assert.True(double.IsNaN(PartParityMotionMetrics.ProgressOnScout(scout, side, new List<PartParityMotionPose>())));
        }

        [Fact]
        public void BuildSeries_ReadsTheSameTimeGhostOffTheScoutByInterpolation()
        {
            // The real frames at 0.0, 0.5, 1.0 s; the scout on its own grid that skips 0.5 s:
            // the same-time value is the scout curve interpolated there (partparity.py's check).
            var realS = new List<double> { 0.0, 0.5, 1.0 };
            var real = new List<PartParityMotionTrack> { Track("a", 0.0, 0.5, 1.0) };
            var scoutS = new List<double> { 0.0, 0.4, 0.8, 1.0 };
            var scout = new List<PartParityMotionTrack> { Track("a", 0.0, 0.2, 0.9, 1.0) };
            var channels = new List<string>();
            PartParityMotionSeries s = PartParityMotionMetrics.BuildSeries(realS, real, scoutS, scout, null, null, channels,
                out PartParityMotionSideProgress realSide, out PartParityMotionSideProgress scoutSide);
            Assert.Equal(new[] { PartParityMotionRules.ChannelPosition }, channels.ToArray());
            for (int k = 0; k < realS.Count; k++)
                Assert.Equal(PartParityMotionRules.Interpolate(scoutS, scoutSide.Progress, realS[k]), s.GhostSameProgress[k], 12);
            Assert.Equal(realSide.Progress, s.RealProgress);
            Assert.Single(s.Transforms);
            Assert.Equal(3, s.Transforms[0].Real.Length);
            Assert.Equal(4, s.Transforms[0].Ghost.Length);
            Assert.Null(s.RealClock);
        }

        [Fact]
        public void BuildSeries_WritesAStillTransform_AsZeroArraysOfTheFrameAndScoutLengths()
        {
            // Track "a" moves, track "b" never does on either side: "b" has no channel and no
            // travel, but still one fraction per frame and per scout point (partparity.py's
            // motion_block_problems reads a missing array as an instrument mismatch).
            var realS = new List<double> { 0.0, 0.5, 1.0 };
            var real = new List<PartParityMotionTrack> { Track("a", 0.0, 0.5, 1.0), Track("b", 2.0, 2.0, 2.0) };
            var scoutS = new List<double> { 0.0, 0.4, 0.8, 1.0 };
            var scout = new List<PartParityMotionTrack> { Track("a", 0.0, 0.2, 0.9, 1.0), Track("b", 2.0, 2.0, 2.0, 2.0) };
            var channels = new List<string>();
            PartParityMotionSeries s = PartParityMotionMetrics.BuildSeries(realS, real, scoutS, scout, null, null, channels,
                out PartParityMotionSideProgress _, out PartParityMotionSideProgress _);
            Assert.Equal(2, s.Transforms.Count);
            Assert.Equal(new[] { PartParityMotionRules.ChannelPosition, null }, channels.ToArray());
            PartParityMotionTransformSeries still = s.Transforms[1];
            Assert.Equal("b", still.Path);
            Assert.Equal(new double[3], still.Real);
            Assert.Equal(new double[4], still.Ghost);
            Assert.Equal(3, s.Transforms[0].Real.Length);
            Assert.Equal(4, s.Transforms[0].Ghost.Length);
            Assert.Equal(1.0, s.Transforms[0].Real[2], 12);
        }

        private static PartParityMotionFrame Frame(int k, IList<string> rows, params double[] img)
        {
            var f = new PartParityMotionFrame { K = k, RealS = 0.1 * k, ImgProgress = img, ImgProgressRows = rows };
            var m = new PartParityViewMeasures { Width = W, Height = W };
            f.Views.Add(new PartParityMotionFrameView { View = "side-a", Same = new PartParityViewRecord { Measures = m } });
            return f;
        }

        [Fact]
        public void TheFrameBlock_CarriesEveryKey_AndImgProgressKeyedByAtlasRow()
        {
            string[] rows = PartParityJson.MotionAtlasRows(new[] { "side-a" }, true);
            string json = PartParityMotionMetrics.FrameBlock(Frame(3, rows, 0.25, 0.5, double.NaN));
            foreach (string key in PartParityJson.MotionFrameKeys) Assert.Contains("\"" + key + "\":", json);
            Assert.Contains("\"imgProgress\":{\"real:side-a\":0.25,\"ghost:side-a\":0.5,\"ghost-matched:side-a\":null}", json);
            Assert.Contains("\"matched\":null", json);
        }

        [Fact]
        public void TheMotionBlock_CarriesEveryKey()
        {
            var r = new PartParityMotionRecord
            {
                Option = new PartParityMotionOption { Family = PartParityMotionRules.FamilyDeployable, Kind = PartParityMotionRules.KindClip, Module = "ModuleDeployableAntenna" },
                Window = new PartParityMotionWindow { EndS = 1.0, SpacingS = 0.25 },
                Timing = new PartParityMotionTiming(),
            };
            r.Frames.Add(Frame(0, PartParityJson.MotionAtlasRows(new[] { "side-a" }, true), 0.0, 0.0, 0.0));
            Dictionary<string, string> d = PartParityMotionMetrics.MotionBlock(r);
            Assert.Equal(PartParityJson.MotionKeys.OrderBy(k => k), d.Keys.OrderBy(k => k));
            Assert.Empty(PartParityMotionMetrics.MotionBlock(null));
        }

        [Fact]
        public void TheMotionAtlasBlock_ListsTheFramesKAsItsColumns()
        {
            Dictionary<string, string> d = PartParityMotionMetrics.MotionAtlasBlock("x.motion.png", 256,
                new[] { "real:side-a", "ghost:side-a" }, new[] { 0, 1, 2 });
            Assert.Equal(PartParityJson.MotionAtlasKeys.OrderBy(k => k), d.Keys.OrderBy(k => k));
            Assert.Equal("[0,1,2]", d["cols"]);
            Assert.Equal("256", d["tile"]);
        }

        [Fact]
        public void TheImgProgressBlock_IsNullWithoutRows()
        {
            Assert.Equal("null", PartParityMotionMetrics.ImgProgressBlock(null, new[] { 1.0 }));
            Assert.Equal("null", PartParityMotionMetrics.ImgProgressBlock(new[] { "real:side-a" }, null));
        }
    }
}
