using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for <see cref="PartParityBurstMetrics"/> (design-part-ghost-parity.md
    /// Appendix G.3, piece X3): the time-binned accumulator over frames and trials, the bin
    /// measures radial about the source point, onset / peak / duration / peak area / radius /
    /// centroid offset / colour / brightness, the schedule diff, the split halves by trial, the
    /// fail lines; on synthetic bursts a late, small, shifted, hue-rotated and a missing burst
    /// each fail exactly their own measures; and the committed fixture the Python recompute reads.
    /// </summary>
    public class PartParityBurstMetricsTests
    {
        private static string RepoRoot() => SyntheticRecordingTests.ResolveProjectRoot();

        private static PartParityBurstViewResult Measure(FxBurst real, FxBurst ghost, string layer = PartParityFxRules.LayerFlame,
            double diameterPx = 8.0)
            => PartParityBurstViewResult.Measure(PartParityFxRules.ViewBurstSide, layer, real.Source(diameterPx),
                real.Accumulate(layer), ghost.Accumulate(layer));

        private static FxBurst Ghost(FxBurst real, Action<FxBurst> turn = null)
        {
            FxBurst g = real.Clone();
            g.Seed += 100;
            turn?.Invoke(g);
            return g;
        }

        private static string Level(string measure, double? v)
            => v.HasValue ? PartParityFxRules.BurstMeasureLevel(measure, v.Value) : null;

        // ----- the accumulator and one bin -----

        [Fact]
        public void TheAccumulator_BinsEveryFrameOfEveryTrial_AndSplitsByTrial()
        {
            var burst = new FxBurst();
            PartParityBurstAccumulator acc = burst.Accumulate(PartParityFxRules.LayerFlame);
            Assert.Equal(16, acc.BinCount);
            Assert.Equal(8, acc.Bin(5, PartParityBurstHalf.All).Samples);
            Assert.Equal(4, acc.Bin(5, PartParityBurstHalf.Even).Samples);
            Assert.Equal(4, acc.Bin(5, PartParityBurstHalf.Odd).Samples);
            // Trials 1 and 3 (0-based 0, 2) are the even half.
            var a = new PartParityBurstAccumulator(PartParityFxRules.LayerFlame, 4, 4, 2);
            var lit = Enumerable.Repeat((byte)200, 64).ToArray();
            a.Add(1, 0, lit);
            a.Add(1, 2, lit);
            a.Add(1, 1, new byte[64]);
            Assert.Equal(2, a.Bin(1, PartParityBurstHalf.Even).Samples);
            Assert.Equal(1, a.Bin(1, PartParityBurstHalf.Odd).Samples);
            Assert.Equal(2, a.Bin(1, PartParityBurstHalf.Even).CoveredAt(0));
            Assert.Equal(0, a.Bin(1, PartParityBurstHalf.Odd).CoveredAt(0));
            Assert.Equal(0, a.Bin(0, PartParityBurstHalf.All).Samples);
            Assert.Throws<ArgumentOutOfRangeException>(() => a.Add(2, 0, lit));
            Assert.Throws<ArgumentOutOfRangeException>(() => a.Add(-1, 0, lit));
        }

        [Fact]
        public void OneBin_IsMeasuredFromTheTile_RadialAboutTheSource()
        {
            // A 3 x 3 block of core at (4..6, 4..6), one envelope pixel at (9, 5); source at (5.5, 5.5).
            const int w = 12, n = 4;
            var tile = new byte[w * w * 4];
            void Put(int x, int y, int k, int r, int g, int b)
            {
                int o = (y * w + x) * 4;
                tile[o] = (byte)r; tile[o + 1] = (byte)g; tile[o + 2] = (byte)b;
                tile[o + 3] = (byte)PartParityFxRules.TileAlpha(k, n);
            }
            for (int y = 4; y <= 6; y++)
                for (int x = 4; x <= 6; x++)
                    Put(x, y, 4, 200, 100, 50);
            Put(6, 6, 4, 255, 100, 50); // a clipped core pixel
            Put(9, 5, 1, 50, 100, 200);
            PartParityBurstBinMeasures m = PartParityBurstMetrics.MeasureBin(tile, w, w, n, new[] { 5.5, 5.5 });
            Assert.Equal(9, m.CorePx);
            Assert.Equal(10, m.EnvelopePx);
            Assert.Equal(1, m.SaturatedPx);
            Assert.Equal(new[] { 5.5, 5.5 }, m.CoreCentroid);
            Assert.Equal(new[] { 200.0, 100.0, 50.0 }, m.MeanColor);
            Assert.Equal(200.0 / 350.0, m.Chromaticity[0], 12);
            // Nearest-rank 95th percentile of the ten envelope distances: the 10th, the far pixel (4 px off).
            Assert.Equal(Math.Sqrt(16.0), m.RadiusPx.Value, 12);
            // The hue histogram over the envelope's unsaturated pixels: eight orange (bin 0) and one blue.
            Assert.True(m.HueHistogram[0] > 0.0);
            Assert.True(m.HueHistogram[PartParityFxMetrics.HueBin(50, 100, 200)] > 0.0);
            // No source: no radius.
            Assert.Null(PartParityBurstMetrics.MeasureBin(tile, w, w, n, null).RadiusPx);
            // An empty tile: nothing, no centroid, no colour.
            PartParityBurstBinMeasures e = PartParityBurstMetrics.MeasureBin(new byte[w * w * 4], w, w, n, new[] { 5.5, 5.5 });
            Assert.Equal(0, e.EnvelopePx);
            Assert.Null(e.CoreCentroid);
            Assert.Null(e.RadiusPx);
            Assert.Null(e.MeanColor);
        }

        [Fact]
        public void TheSide_ReadsOnsetPeakAndDuration()
        {
            var side = Measure(new FxBurst(), new FxBurst()).Real;
            Assert.Equal(2, side.OnsetBin);
            Assert.Equal(4, side.PeakBin);
            Assert.Equal(14, side.LastCoreBin);
            Assert.True(side.PeakCorePx > 500);
            var empty = PartParityBurstMetrics.MeasureSide(new List<PartParityBurstBinMeasures>
                { new PartParityBurstBinMeasures(), new PartParityBurstBinMeasures() });
            Assert.Null(empty.OnsetBin);
            Assert.Null(empty.PeakBin);
            Assert.Null(empty.LastCoreBin);
            Assert.Equal(0, empty.PeakCorePx);
        }

        // ----- each synthetic ghost moves exactly its own measures -----

        [Fact]
        public void ARealAgainstAnotherDrawOfItself_PassesEveryMeasure()
        {
            var real = new FxBurst();
            PartParityBurstViewResult r = Measure(real, Ghost(real));
            Assert.Empty(r.FailingMeasures());
            Assert.Equal(0.0, r.Compare.OnsetDeltaBins);
            Assert.Equal(0.0, r.Compare.PeakTimeDeltaBins);
            Assert.Equal(0.0, r.Compare.DurationDelta);
            foreach (var (m, v) in new[]
                     {
                         ("peak-area", r.Compare.PeakAreaRatio), ("radius", r.Compare.RadiusRatio),
                         ("centroid-offset", r.Compare.CentroidOffset), ("brightness", r.Compare.BrightnessRatio),
                     })
                Assert.Equal("ok", Level(m, v));
            Assert.Equal("ok", PartParityFxRules.BurstColourLevel(r.Compare.ChromaDistance.Value, r.Compare.HueL1.Value));
            Assert.Null(r.SplitHalfDisagreement());
            Assert.Null(r.Compare.Presence);
        }

        [Fact]
        public void ALateBurst_FailsOnsetOnly()
        {
            var real = new FxBurst();
            PartParityBurstViewResult r = Measure(real, Ghost(real, g => g.DelayBins = 2));
            Assert.Equal(new[] { "onset" }, r.FailingMeasures());
            Assert.Equal(2.0, r.Compare.OnsetDeltaBins);
            Assert.Equal(2.0, r.Compare.PeakTimeDeltaBins);
            Assert.Equal("warn", Level("peak-time", r.Compare.PeakTimeDeltaBins));
            Assert.Equal("ok", Level("duration", r.Compare.DurationDelta));
            Assert.Equal("ok", Level("peak-area", r.Compare.PeakAreaRatio));
            Assert.Equal("ok", Level("radius", r.Compare.RadiusRatio));
            Assert.Equal("ok", Level("centroid-offset", r.Compare.CentroidOffset));
            Assert.Null(r.Compare.Presence);
        }

        [Fact]
        public void ASmallBurst_FailsPeakAreaAndRadiusOnly()
        {
            var real = new FxBurst();
            PartParityBurstViewResult r = Measure(real, Ghost(real, g => g.Scale = 0.6));
            Assert.Equal(new[] { "peak-area", "radius" }, r.FailingMeasures());
            Assert.InRange(r.Compare.PeakAreaRatio.Value, 0.3, 0.45);
            Assert.InRange(r.Compare.RadiusRatio.Value, 0.55, 0.65);
            Assert.Equal(0.0, r.Compare.OnsetDeltaBins);
            Assert.Equal(0.0, r.Compare.PeakTimeDeltaBins);
            Assert.Equal("ok", Level("centroid-offset", r.Compare.CentroidOffset));
            Assert.Equal("ok", PartParityFxRules.BurstColourLevel(r.Compare.ChromaDistance.Value, r.Compare.HueL1.Value));
        }

        [Fact]
        public void AShiftedBurst_FailsCentroidOffsetOnly()
        {
            var real = new FxBurst();
            PartParityBurstViewResult r = Measure(real, Ghost(real, g => g.Dx = 3.0));
            Assert.Equal(new[] { "centroid-offset" }, r.FailingMeasures());
            Assert.InRange(r.Compare.CentroidOffset.Value, 0.33, 0.42);
            Assert.Equal("ok", Level("peak-area", r.Compare.PeakAreaRatio));
            Assert.NotEqual("fail", Level("radius", r.Compare.RadiusRatio));
            Assert.Equal(0.0, r.Compare.OnsetDeltaBins);
            // The offset is each centroid from its OWN side's source: the same burst photographed
            // from a ghost pose 3 px over reads no offset.
            var moved = Ghost(real, g => { g.SourceX += 3.0; g.Dx = 0.0; });
            moved.SourceX = real.SourceX;
            moved.Dx = 3.0;
            var src = real.Source();
            src.GhostPx = new[] { real.SourceX + 3.0, real.SourceY };
            var own = PartParityBurstViewResult.Measure(PartParityFxRules.ViewBurstSide, PartParityFxRules.LayerFlame, src,
                real.Accumulate(PartParityFxRules.LayerFlame), moved.Accumulate(PartParityFxRules.LayerFlame));
            Assert.Equal("ok", Level("centroid-offset", own.Compare.CentroidOffset));
        }

        [Fact]
        public void AHueRotatedBurst_FailsColourOnly_AndMovesNothingElse()
        {
            var real = new FxBurst();
            FxBurst same = Ghost(real);
            PartParityBurstViewResult plain = Measure(real, same);
            PartParityBurstViewResult r = Measure(real, Ghost(real, g => g.RotateHue = true));
            Assert.Equal(new[] { "colour" }, r.FailingMeasures());
            // Max-channel coverage is unchanged exactly, so every shape and time measure is the intact ghost's.
            Assert.Equal(plain.Compare.PeakAreaRatio, r.Compare.PeakAreaRatio);
            Assert.Equal(plain.Compare.RadiusRatio, r.Compare.RadiusRatio);
            Assert.Equal(plain.Compare.CentroidOffset, r.Compare.CentroidOffset);
            Assert.Equal(plain.Compare.OnsetDeltaBins, r.Compare.OnsetDeltaBins);
            Assert.Equal(plain.Compare.DurationDelta, r.Compare.DurationDelta);
            Assert.Equal("fail", PartParityFxRules.BurstColourLevel(r.Compare.ChromaDistance.Value, r.Compare.HueL1.Value));
            Assert.Equal("fail", PartParityFxRules.BurstColourLevel(r.Compare.ChromaDistanceMean.Value, r.Compare.HueL1Mean.Value));
        }

        [Fact]
        public void AMissingBurst_FailsPresenceOnly()
        {
            var real = new FxBurst();
            PartParityBurstViewResult r = Measure(real, Ghost(real, g => g.Off = true));
            Assert.Equal(new[] { "presence" }, r.FailingMeasures());
            Assert.Equal("burst-missing", r.Compare.Presence);
            Assert.Null(r.Compare.OnsetDeltaBins);
            Assert.Null(r.Compare.RadiusRatio);
            Assert.Null(r.Compare.ChromaDistance);
            // The other way round is an extra burst.
            Assert.Equal("burst-extra", Measure(Ghost(real, g => g.Off = true), real).Compare.Presence);
        }

        [Fact]
        public void TheSmokeLayer_JudgesSmokeOnly()
        {
            var real = new FxBurst { R = 120, G = 120, B = 120, Alpha = 200 };
            Assert.Empty(Measure(real, Ghost(real), PartParityFxRules.LayerSmoke).FailingMeasures());
            PartParityBurstViewResult gone = Measure(real, Ghost(real, g => g.Off = true), PartParityFxRules.LayerSmoke);
            Assert.Equal("smoke-missing", gone.Compare.Presence);
            Assert.Equal(new[] { "smoke" }, gone.FailingMeasures());
            Assert.Equal(new[] { "smoke" }, Measure(real, Ghost(real, g => g.Scale = 0.6), PartParityFxRules.LayerSmoke).FailingMeasures());
            // A late smoke is not the smoke measure's business.
            Assert.Empty(Measure(real, Ghost(real, g => g.DelayBins = 2), PartParityFxRules.LayerSmoke).FailingMeasures());
        }

        [Fact]
        public void WeakCores_JudgePresenceOnly()
        {
            var tiny = new FxBurst { Radius = new double[] { 0, 2, 3, 3, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } };
            PartParityBurstViewResult r = Measure(tiny, Ghost(tiny, g => { g.DelayBins = 5; g.Dx = 4.0; }));
            Assert.True(r.Real.PeakCorePx < PartParityFxRules.PresenceMinPx);
            Assert.DoesNotContain("onset", r.FailingMeasures());
            Assert.DoesNotContain("centroid-offset", r.FailingMeasures());
        }

        [Fact]
        public void TheSplitHalves_AreTrialsOneAndThreeAgainstTwoAndFour()
        {
            var real = new FxBurst();
            PartParityBurstViewResult r = Measure(real, Ghost(real));
            Assert.All(r.EvenSamples, s => Assert.Equal(4, s));
            Assert.All(r.OddSamples, s => Assert.Equal(4, s));
            Assert.Null(PartParityBurstMetrics.SplitHalfDisagreement(PartParityFxRules.LayerFlame, r.RealEven, r.RealOdd));
            // Halves that disagree on presence, or on the largest core by more than 10 %.
            var off = Measure(real, Ghost(real, g => g.Off = true));
            Assert.StartsWith("presence ", PartParityBurstMetrics.SplitHalfDisagreement(PartParityFxRules.LayerFlame, r.RealEven, off.GhostOdd));
            var small = Measure(real, Ghost(real, g => g.Scale = 0.8));
            Assert.StartsWith("peakCorePx ", PartParityBurstMetrics.SplitHalfDisagreement(PartParityFxRules.LayerFlame, r.RealEven, small.GhostOdd));
        }

        // ----- the schedule -----

        private static PartParityBurstBlast Blast(double power, int index, double x = 0.0)
            => new PartParityBurstBlast { Frame = 3, OffsetM = new[] { x, 0.0, 0.0 }, Power = power, Prefab = "p" + index, Index = index };

        [Fact]
        public void TheSchedule_FailsOnCountIndexOrPosition_AndWarnsOnPower()
        {
            var one = new List<PartParityBurstBlast> { Blast(0.5, 1) };
            Assert.Equal("ok", PartParityBurstMetrics.CompareSchedule(one, new List<PartParityBurstBlast> { Blast(0.5, 1, 0.9) }).Level);
            var s = PartParityBurstMetrics.CompareSchedule(one, new List<PartParityBurstBlast>());
            Assert.Equal("fail", s.Level);
            Assert.True(s.CountDiffers);
            Assert.Equal(1, s.RealCount);
            Assert.Null(s.MaxOffsetDeltaM);
            s = PartParityBurstMetrics.CompareSchedule(one, new List<PartParityBurstBlast> { Blast(0.2, 0) });
            Assert.Equal("fail", s.Level);
            Assert.True(s.IndexDiffers);
            Assert.False(s.PowerDiffers);
            s = PartParityBurstMetrics.CompareSchedule(one, new List<PartParityBurstBlast> { Blast(0.5, 1, 3.0) });
            Assert.Equal("fail", s.Level);
            Assert.Equal(3.0, s.MaxOffsetDeltaM);
            s = PartParityBurstMetrics.CompareSchedule(one, new List<PartParityBurstBlast> { Blast(0.6, 1) });
            Assert.Equal("warn", s.Level);
            Assert.True(s.PowerDiffers);
            Assert.Equal(new[] { "schedule" }, PartParityBurstMetrics.FailingScheduleMeasures(
                PartParityBurstMetrics.CompareSchedule(one, null)));
            Assert.Empty(PartParityBurstMetrics.FailingScheduleMeasures(PartParityBurstMetrics.CompareSchedule(null, null)));
        }

        // ----- the control judges read these measures -----

        [Fact]
        public void TheBurstControls_AreMetByTheirOwnSyntheticGhosts()
        {
            var real = new FxBurst();
            PartParityBurstControlEvidence Evidence(FxBurst ghost, List<PartParityBurstBlast> g)
            {
                var e = new PartParityBurstControlEvidence();
                foreach (string m in Measure(real, ghost).FailingMeasures()) e.FailingMeasures.Add(m);
                var r = new List<PartParityBurstBlast> { Blast(0.5, 1) };
                var s = PartParityBurstMetrics.CompareSchedule(r, g);
                foreach (string m in PartParityBurstMetrics.FailingScheduleMeasures(s)) e.FailingMeasures.Add(m);
                e.RealBlasts = s.RealCount;
                e.GhostBlasts = s.GhostCount;
                e.ScheduleIndexDiffers = s.IndexDiffers;
                return e;
            }
            var same = new List<PartParityBurstBlast> { Blast(0.5, 1) };
            string J(string id, PartParityBurstControlEvidence e) => PartParityFxRules.JudgeBurstControl(id, e, out _);
            Assert.Equal("ok", J("ctl-burst-real-vs-real", Evidence(Ghost(real), same)));
            Assert.Equal("ok", J("ctl-burst-off", Evidence(Ghost(real, g => g.Off = true), new List<PartParityBurstBlast>())));
            Assert.Equal("ok", J("ctl-burst-late", Evidence(Ghost(real, g => g.DelayBins = 2), same)));
            Assert.Equal("ok", J("ctl-burst-power", Evidence(Ghost(real, g => g.Scale = 0.6), new List<PartParityBurstBlast> { Blast(0.2, 0) })));
            Assert.Equal("ok", J("ctl-burst-shift", Evidence(Ghost(real, g => g.Dx = 3.0), new List<PartParityBurstBlast> { Blast(0.5, 1, 3.0) })));
            Assert.Equal("ok", J("ctl-burst-hue", Evidence(Ghost(real, g => g.RotateHue = true), same)));
            // And each control refuses another control's ghost.
            Assert.Equal("bad", J("ctl-burst-hue", Evidence(Ghost(real, g => g.Dx = 3.0), same)));
            Assert.Equal("bad", J("ctl-burst-late", Evidence(Ghost(real, g => g.Scale = 0.6), same)));
        }

        // ----- JSON -----

        [Fact]
        public void TheEntry_WritesEveryKeyOfItsTables()
        {
            var real = new FxBurst();
            PartParityBurstViewResult r = Measure(real, Ghost(real, g => g.Off = true));
            var o = (Dictionary<string, object>)PartParityJsonReader.Parse(r.Json());
            Assert.Equal(PartParityJson.BurstViewKeys, o.Keys);
            var real0 = (Dictionary<string, object>)o["real"];
            Assert.Equal(PartParityJson.BurstSideMeasureKeys, real0.Keys);
            var bins = (List<object>)real0["bins"];
            Assert.Equal(16, bins.Count);
            Assert.Equal(PartParityJson.BurstBinMeasureKeys, ((Dictionary<string, object>)bins[0]).Keys);
            Assert.Equal(PartParityJson.BurstCompareKeys, ((Dictionary<string, object>)o["compare"]).Keys);
            Assert.Equal(PartParityJson.BurstSourceKeys, ((Dictionary<string, object>)o["source"]).Keys);
            var split = (Dictionary<string, object>)o["splitHalf"];
            Assert.Equal(PartParityJson.FxSplitHalfKeys, split.Keys);
            Assert.Equal(PartParityJson.BurstHalfKeys, ((Dictionary<string, object>)split["even"]).Keys);
            Assert.Equal("burst-missing", ((Dictionary<string, object>)o["compare"])["presence"]);
            Assert.Null(((Dictionary<string, object>)o["ghost"])["onsetBin"]);
            var s = (Dictionary<string, object>)PartParityJsonReader.Parse(PartParityBurstMetrics.ScheduleCompareJson(
                PartParityBurstMetrics.CompareSchedule(new List<PartParityBurstBlast> { Blast(0.5, 1) }, null)));
            Assert.Equal(PartParityJson.BurstScheduleCompareKeys, s.Keys);
            var blast = (Dictionary<string, object>)PartParityJsonReader.Parse(PartParityBurstMetrics.BlastJson(Blast(0.5, 1)));
            Assert.Equal(PartParityJson.BurstScheduleEntryKeys, blast.Keys);
        }

        // ----- the committed fixture -----

        internal sealed class WriteBurstFixtureFactAttribute : FactAttribute
        {
            public WriteBurstFixtureFactAttribute()
            {
                if (Environment.GetEnvironmentVariable("PARSEK_WRITE_PART_PARITY_BURST_FIXTURE") != "1")
                    Skip = "set PARSEK_WRITE_PART_PARITY_BURST_FIXTURE=1 to rewrite Source/Parsek.Tests/Fixtures/PartParity/burst/";
            }
        }

        [Trait("Category", "Manual")]
        [WriteBurstFixtureFact]
        public void WriteBurstFixture()
        {
            string target = PartParityBurstFixture.TargetDir(RepoRoot());
            Directory.CreateDirectory(target);
            foreach (string old in Directory.GetFiles(target)) File.Delete(old);
            foreach (var kv in PartParityBurstFixture.BuildFiles(out _))
                File.WriteAllBytes(Path.Combine(target, kv.Key), kv.Value);
        }

        [Fact]
        public void Fixture_ExercisesEveryMeasureItClaims()
        {
            PartParityBurstFixture.Built b = PartParityBurstFixture.Build();
            Assert.Equal(PartParityFxRules.BurstViews.Length * PartParityFxRules.BurstLayers.Length, b.Entries.Count);
            var failing = new Dictionary<string, List<string>>();
            foreach (PartParityBurstViewResult e in b.Entries) failing[e.View + ":" + e.Layer] = e.FailingMeasures();
            Assert.Equal(new[] { "onset" }, failing["burst-side:flame"]);
            Assert.Empty(failing["burst-side:smoke"]);
            Assert.Equal(new[] { "centroid-offset" }, failing["burst-top:flame"]);
            Assert.Equal(new[] { "smoke" }, failing["burst-top:smoke"]);
            Assert.Equal(new[] { "colour" }, failing["burst-far:flame"]);
            Assert.Equal(new[] { "smoke" }, failing["burst-far:smoke"]);
            Assert.Equal("warn", b.Schedule.Level);
            Assert.True(b.Schedule.PowerDiffers);
            Assert.Equal(3 * 16 * 48, b.Width);
            Assert.Equal(4 * 48, b.Height);
        }

        [Fact]
        public void Fixture_CommittedFilesMatchTheBuilder()
        {
            string target = PartParityBurstFixture.TargetDir(RepoRoot());
            Assert.True(Directory.Exists(target), "fixture missing: " + target
                + " (write it with PARSEK_WRITE_PART_PARITY_BURST_FIXTURE=1 dotnet test --filter WriteBurstFixture)");
            SortedDictionary<string, byte[]> expected = PartParityBurstFixture.BuildFiles(out _);
            var committed = Directory.GetFiles(target).Select(Path.GetFileName).OrderBy(s => s, StringComparer.Ordinal).ToList();
            Assert.Equal(expected.Keys.ToList(), committed);
            string id = PartParityBurstFixture.CaseId();
            byte[] want = FxPngReader.DecodeRgba(expected[TestCommandPartParitySweep.BurstAtlasFileName(id)], out int ww, out int wh);
            byte[] got = FxPngReader.DecodeRgba(File.ReadAllBytes(Path.Combine(target, TestCommandPartParitySweep.BurstAtlasFileName(id))),
                out int gw, out int gh);
            Assert.Equal((ww, wh), (gw, gh));
            Assert.True(want.SequenceEqual(got), "the committed burst atlas differs from a fresh build - rebuild it, never hand-edit it");
            object a = PartParityJsonReader.Parse(System.Text.Encoding.UTF8.GetString(expected[TestCommandPartParitySweep.SidecarFileName(id)]));
            object c = PartParityJsonReader.Parse(File.ReadAllText(Path.Combine(target, TestCommandPartParitySweep.SidecarFileName(id))));
            AssertJsonEqual(a, c, "$");
        }

        [Fact]
        public void Fixture_EveryRecordedBinMeasure_RecomputesFromTheCommittedAtlas()
        {
            // What the Python recompute does: crop each bin's two tiles, measure, compare with the sidecar.
            string target = PartParityBurstFixture.TargetDir(RepoRoot());
            string id = PartParityBurstFixture.CaseId();
            byte[] atlas = FxPngReader.DecodeRgba(File.ReadAllBytes(Path.Combine(target, TestCommandPartParitySweep.BurstAtlasFileName(id))),
                out int w, out _);
            object sidecar = PartParityJsonReader.Parse(File.ReadAllText(Path.Combine(target, TestCommandPartParitySweep.SidecarFileName(id))));
            var burst = PartParityJsonReader.Obj(sidecar, "burst");
            var meta = PartParityJsonReader.Obj(burst, "atlas");
            int tile = PartParityJsonReader.Int(meta, "tile"), bins = PartParityJsonReader.Int(meta, "bins");
            List<string> rows = PartParityJsonReader.Arr(meta, "rows").Cast<string>().ToList();
            List<string> views = PartParityJsonReader.Arr(meta, "views").Cast<string>().ToList();
            List<object> realN = PartParityJsonReader.Arr(PartParityJsonReader.Obj(burst, "samples"), "real");
            int checkedBins = 0;
            foreach (object v in PartParityJsonReader.Arr(burst, "views"))
            {
                string view = PartParityJsonReader.Str(v, "view"), layer = PartParityJsonReader.Str(v, "layer");
                var source = PartParityJsonReader.Obj(v, "source");
                foreach (string side in new[] { "real", "ghost" })
                {
                    double[] src = PartParityJsonReader.Arr(source, side + "Px").Select(Convert.ToDouble).ToArray();
                    var recorded = PartParityJsonReader.Arr(PartParityJsonReader.Obj(v, side), "bins");
                    for (int b = 0; b < bins; b++)
                    {
                        byte[] t = FxPngReader.Crop(atlas, w, rows.IndexOf(side + "-" + layer), views.IndexOf(view) * bins + b, tile);
                        var m = PartParityBurstMetrics.MeasureBin(t, tile, tile, Convert.ToInt32(realN[b]), src);
                        var rec = (Dictionary<string, object>)recorded[b];
                        Assert.Equal(Convert.ToInt64(rec["corePx"]), m.CorePx);
                        Assert.Equal(Convert.ToInt64(rec["envelopePx"]), m.EnvelopePx);
                        Assert.Equal(rec["radiusPx"] == null ? (double?)null : Convert.ToDouble(rec["radiusPx"]), m.RadiusPx);
                        checkedBins++;
                    }
                }
            }
            Assert.Equal(3 * 2 * 2 * 16, checkedBins);
        }

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
                Assert.True(Math.Abs(x - y) <= 1e-12 * Math.Max(1.0, Math.Abs(x)), path + ": " + x + " != " + y);
                return;
            }
            Assert.Equal(a, b);
        }
    }
}
