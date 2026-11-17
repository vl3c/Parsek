using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Parsek.InGameTests.Spikes.S11;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Spikes
{
    // R0 spike branch only (spike/mp-r0, never merged). Plan: r0-spikes.md, task S11.
    // The pure half of GhostCostSweep: plan, layout, statistics, fit, formatting, the
    // first-open helper over the committed fixture sidecar, and the injection fixture.

    [Collection("Sequential")]
    public class SpikeS11Tests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> tempDirs = new List<string>();

        public SpikeS11Tests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            foreach (string d in tempDirs)
            {
                try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { }
            }
        }

        private string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "parsek-s11-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            tempDirs.Add(d);
            return d;
        }

        private static string RepoRoot() => SyntheticRecordingTests.ProjectRoot;

        private static string ColdPrec() => Path.Combine(RepoRoot(),
            SpikeS11Fixture.ColdSourceRecordingsDir.Replace('/', Path.DirectorySeparatorChar),
            SpikeS11Fixture.ColdSourceRecordingId + ".prec");

        private static SpikeS11StepResult Step(SpikeS11View view, int n, int built, double meanMs, int frames = 600)
        {
            var values = new double[frames];
            for (int i = 0; i < frames; i++) values[i] = meanMs;
            return new SpikeS11StepResult
            {
                Run = "asis-20261117T000000Z", Pass = SpikeS11Pass.AsIs, View = view, Requested = n, Built = built,
                Active = built, OnScreen = view == SpikeS11View.Flight ? built : -1,
                FrameMs = SpikeS11Stats.Summarize(values, frames),
                EngineUs = SpikeS11Stats.Summarize(values, frames),
            };
        }

        // ------------------------------------------------------------ plan

        [Fact]
        public void Plan_GhostCounts_AscendFromZeroToMax()
        {
            Assert.Equal(new[] { 0, 8, 16, 32, 64 }, SpikeS11Plan.GhostCounts);
            Assert.Equal(SpikeS11Plan.MaxGhosts, SpikeS11Plan.GhostCounts[SpikeS11Plan.GhostCounts.Length - 1]);
            Assert.Equal(600, SpikeS11Plan.DefaultFramesPerStep);
            Assert.Equal("Spike.S11", SpikeS11Plan.Tag);
        }

        [Fact]
        public void Plan_RecordingIds_RoundTripAndRejectOthers()
        {
            for (int i = 0; i < SpikeS11Plan.MaxGhosts; i++)
                Assert.Equal(i, SpikeS11Plan.IndexOfRecordingId(SpikeS11Plan.RecordingIdFor(i)));
            Assert.Equal("spike-s11-ghost-07", SpikeS11Plan.RecordingIdFor(7));
            Assert.Equal("S11 Ghost 63", SpikeS11Plan.VesselNameFor(63));
            Assert.Equal(-1, SpikeS11Plan.IndexOfRecordingId(null));
            Assert.Equal(-1, SpikeS11Plan.IndexOfRecordingId(""));
            Assert.Equal(-1, SpikeS11Plan.IndexOfRecordingId("spike-s11-ghost-64"));
            Assert.Equal(-1, SpikeS11Plan.IndexOfRecordingId("spike-s11-ghost-5"));
            Assert.Equal(-1, SpikeS11Plan.IndexOfRecordingId("spike-s11-ghost-+5"));
            Assert.Equal(-1, SpikeS11Plan.IndexOfRecordingId("overlapcap000000000000000000d6oc"));
        }

        [Fact]
        public void Plan_EnabledInStep_IsThePrefix()
        {
            Assert.False(SpikeS11Plan.EnabledInStep(0, 0));
            Assert.True(SpikeS11Plan.EnabledInStep(7, 8));
            Assert.False(SpikeS11Plan.EnabledInStep(8, 8));
            Assert.False(SpikeS11Plan.EnabledInStep(-1, 64));
        }

        [Theory]
        [InlineData(null, "asis", false)]
        [InlineData("", "asis", false)]
        [InlineData("asis", "asis", false)]
        [InlineData(" UNCAPPED ", "uncapped", false)]
        [InlineData("fast", "asis", true)]
        public void Plan_ParsePass(string raw, string expectedToken, bool hasNote)
        {
            Assert.Equal(expectedToken, SpikeS11Plan.PassToken(SpikeS11Plan.ParsePass(raw, out string note)));
            Assert.Equal(hasNote, note != null);
        }

        [Theory]
        [InlineData(null, 600, false)]
        [InlineData(" ", 600, false)]
        [InlineData("1200", 1200, false)]
        [InlineData("10", 60, true)]
        [InlineData("99999", 6000, true)]
        [InlineData("abc", 600, true)]
        public void Plan_ParseFrames(string raw, int expected, bool hasNote)
        {
            Assert.Equal(expected, SpikeS11Plan.ParseFrames(raw, out string note));
            Assert.Equal(hasNote, note != null);
        }

        [Fact]
        public void Plan_ParseShots_OnUnlessSaidOff()
        {
            Assert.True(SpikeS11Plan.ParseShots(null));
            Assert.True(SpikeS11Plan.ParseShots("1"));
            Assert.False(SpikeS11Plan.ParseShots("0"));
            Assert.False(SpikeS11Plan.ParseShots(" Off "));
        }

        [Fact]
        public void Plan_RunLabelAndShotName_AreInvariant()
        {
            var prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("ar-SA");
                var utc = new DateTime(2026, 11, 17, 9, 5, 7, DateTimeKind.Utc);
                Assert.Equal("uncapped-20261117T090507Z", SpikeS11Plan.RunLabel(SpikeS11Pass.Uncapped, utc));
            }
            finally { Thread.CurrentThread.CurrentCulture = prev; }
            Assert.Equal("asis-x-map-n08.png", SpikeS11Plan.ShotFileName("asis-x", SpikeS11View.Map, 8));
        }

        [Fact]
        public void Plan_JudgeStep_CountsOnlyComplete_InViewWindows()
        {
            Assert.Null(SpikeS11Plan.JudgeStep(Step(SpikeS11View.Flight, 8, 8, 16), 600));
            Assert.Null(SpikeS11Plan.JudgeStep(Step(SpikeS11View.Map, 8, 8, 16), 600));
            Assert.Null(SpikeS11Plan.JudgeStep(Step(SpikeS11View.Flight, 0, 0, 16), 600));

            var timedOut = Step(SpikeS11View.Flight, 8, 8, 16);
            timedOut.SpawnWaitTimedOut = true;
            Assert.Contains("timed out", SpikeS11Plan.JudgeStep(timedOut, 600));

            Assert.Contains("built 7 of 8", SpikeS11Plan.JudgeStep(Step(SpikeS11View.Map, 8, 7, 16), 600));

            var leftover = Step(SpikeS11View.Flight, 8, 8, 16);
            leftover.Leftover = 2;
            Assert.Contains("later step", SpikeS11Plan.JudgeStep(leftover, 600));

            Assert.Contains("measured 300 of 600", SpikeS11Plan.JudgeStep(Step(SpikeS11View.Flight, 8, 8, 16, 300), 600));

            var offScreen = Step(SpikeS11View.Flight, 8, 8, 16);
            offScreen.OnScreen = 6;
            Assert.Contains("only 6 of 8", SpikeS11Plan.JudgeStep(offScreen, 600));
            Assert.NotNull(SpikeS11Plan.JudgeStep(null, 600));
        }

        // ------------------------------------------------------------ layout

        [Fact]
        public void Layout_EveryGhost_StaysNearThePadForTheWholeRecording()
        {
            const double degToM = Math.PI / 180.0 * SpikeS11Layout.KerbinRadiusMeters;
            double cosLat = Math.Cos(SpikeS11Layout.CenterLatitude * Math.PI / 180.0);
            for (int i = 0; i < SpikeS11Plan.MaxGhosts; i++)
            {
                double r = SpikeS11Layout.RadiusFor(i);
                Assert.InRange(r, 40.0, 110.0);
                Assert.InRange(SpikeS11Layout.AltitudeFor(i) - SpikeS11Layout.PadAltitudeMeters, 30.0, 100.0);
                // well inside the 10 km full-fidelity zone and the 300 m camera framing
                Assert.InRange(SpikeS11Layout.SlantRangeMeters(i), 45.0, 150.0);
                for (int k = 0; k < SpikeS11Layout.PointCount; k += 7)
                {
                    double t = k * SpikeS11Layout.SampleSpacingSeconds;
                    SpikeS11Layout.PositionAt(i, t, out double lat, out double lon, out double alt);
                    double north = (lat - SpikeS11Layout.CenterLatitude) * degToM;
                    double east = (lon - SpikeS11Layout.CenterLongitude) * degToM * cosLat;
                    Assert.InRange(Math.Sqrt(north * north + east * east), r - 0.5, r + 0.5);
                    Assert.Equal(SpikeS11Layout.AltitudeFor(i), alt);
                }
            }
        }

        [Fact]
        public void Layout_RecordingOutlastsTheSweep()
        {
            Assert.Equal(901, SpikeS11Layout.PointCount);
            // The RunTests step is capped at 540 s and starts well under a minute of game
            // time after the save's clock, so a 1800 s recording outlasts any sweep.
            Assert.True(SpikeS11Layout.DurationSeconds >= 1800.0);
            // at most 8 degrees of arc between samples, so the interpolated chord stays
            // within 0.3 m of the circle on the widest ring
            double step = 2 * Math.PI * SpikeS11Layout.SampleSpacingSeconds / SpikeS11Layout.LapSeconds;
            Assert.True(SpikeS11Layout.RadiusFor(7) * (1 - Math.Cos(step / 2)) < 0.3);
        }

        [Fact]
        public void Layout_AnyPrefix_IsSpreadAroundThePad_AndGhostsNeverOverlap()
        {
            foreach (int n in new[] { 8, 16, 32, 64 })
            {
                var a = new List<double>();
                for (int i = 0; i < n; i++) a.Add(SpikeS11Layout.StartAngleFor(i));
                a.Sort();
                double maxGap = a[0] + 2 * Math.PI - a[a.Count - 1];
                for (int k = 1; k < a.Count; k++) maxGap = Math.Max(maxGap, a[k] - a[k - 1]);
                Assert.True(maxGap < Math.PI / 3, "n=" + n + " leaves a gap of " + (maxGap * 180 / Math.PI) + " deg");
            }

            double minSep = double.MaxValue;
            for (double t = 0; t <= SpikeS11Layout.LapSeconds; t += 1)
            {
                var p = new double[SpikeS11Plan.MaxGhosts, 3];
                for (int i = 0; i < SpikeS11Plan.MaxGhosts; i++)
                {
                    double ang = SpikeS11Layout.AngleAt(i, t), r = SpikeS11Layout.RadiusFor(i);
                    p[i, 0] = r * Math.Cos(ang); p[i, 1] = r * Math.Sin(ang); p[i, 2] = SpikeS11Layout.AltitudeFor(i);
                }
                for (int i = 0; i < SpikeS11Plan.MaxGhosts; i++)
                    for (int j = i + 1; j < SpikeS11Plan.MaxGhosts; j++)
                    {
                        double dx = p[i, 0] - p[j, 0], dy = p[i, 1] - p[j, 1], dz = p[i, 2] - p[j, 2];
                        minSep = Math.Min(minSep, Math.Sqrt(dx * dx + dy * dy + dz * dz));
                    }
            }
            Assert.True(minSep >= 10.0, "two ghosts come within " + minSep + " m");
        }

        // ------------------------------------------------------------ statistics

        [Fact]
        public void Stats_Percentile_IsNearestRank()
        {
            var v = new double[100];
            for (int i = 0; i < 100; i++) v[i] = i + 1;
            Assert.Equal(50, SpikeS11Stats.Percentile(v, 100, 0.50));
            Assert.Equal(95, SpikeS11Stats.Percentile(v, 100, 0.95));
            Assert.Equal(99, SpikeS11Stats.Percentile(v, 100, 0.99));
            Assert.Equal(100, SpikeS11Stats.Percentile(v, 100, 1.0));
            Assert.Equal(7, SpikeS11Stats.Percentile(new double[] { 7 }, 1, 0.99));
            Assert.True(double.IsNaN(SpikeS11Stats.Percentile(new double[0], 0, 0.5)));
            // 600 frames: p99 is rank 594, so the six worst frames sit above it
            var w = new double[600];
            for (int i = 0; i < 600; i++) w[i] = i;
            Assert.Equal(593, SpikeS11Stats.Percentile(w, 600, 0.99));
        }

        [Fact]
        public void Stats_Summarize_KnownValues_AndLeavesInputOrder()
        {
            var v = new double[] { 4, 2, 8, 6, 999 };
            var s = SpikeS11Stats.Summarize(v, 4);
            Assert.Equal(4, s.Count);
            Assert.Equal(5.0, s.Mean, 9);
            Assert.Equal(2.0, s.Min);
            Assert.Equal(8.0, s.Max);
            Assert.Equal(4.0, s.P50);
            Assert.Equal(8.0, s.P99);
            Assert.Equal(Math.Sqrt(20.0 / 3.0), s.StdDev, 9);
            Assert.Equal(new double[] { 4, 2, 8, 6, 999 }, v);

            var e = SpikeS11Stats.Summarize(new double[3], 0);
            Assert.Equal(0, e.Count);
            Assert.True(double.IsNaN(e.Mean) && double.IsNaN(e.P99));
            Assert.Equal(0.0, SpikeS11Stats.Summarize(new double[] { 3 }, 1).StdDev);
        }

        [Fact]
        public void Stats_Fit_Line_DegenerateAndNaN()
        {
            var f = SpikeS11Stats.Fit(new double[] { 0, 8, 16, 32, 64 }, new double[] { 1, 17, 33, 65, 129 });
            Assert.True(f.Valid);
            Assert.Equal(5, f.Points);
            Assert.Equal(2.0, f.Slope, 9);
            Assert.Equal(1.0, f.Intercept, 9);
            Assert.Equal(1.0, f.R2, 9);

            Assert.False(SpikeS11Stats.Fit(new double[] { 8 }, new double[] { 3 }).Valid);
            Assert.False(SpikeS11Stats.Fit(new double[] { 8, 8, 8 }, new double[] { 1, 2, 3 }).Valid);
            Assert.False(SpikeS11Stats.Fit(null, null).Valid);

            var n = SpikeS11Stats.Fit(new double[] { 0, 8, 16 }, new double[] { 1, double.NaN, 33 });
            Assert.True(n.Valid);
            Assert.Equal(2, n.Points);
            Assert.Equal(2.0, n.Slope, 9);

            var flat = SpikeS11Stats.Fit(new double[] { 0, 8 }, new double[] { 5, 5 });
            Assert.Equal(0.0, flat.Slope, 9);
            Assert.Equal(1.0, flat.R2);
        }

        // ------------------------------------------------------------ formatting

        [Fact]
        public void Format_StepLineAndRows_AreInvariantAndComplete()
        {
            var prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var r = Step(SpikeS11View.Flight, 16, 16, 16.25);
                r.SpawnMicroseconds = 12345;
                string line = SpikeS11Format.StepLine(r);
                Assert.StartsWith("step run=asis-20261117T000000Z pass=asis view=flight n=16 built=16 active=16 leftover=0 onScreen=16 ", line);
                Assert.Contains(" meanMs=16.250 ", line);
                Assert.Contains(" p99Ms=16.250 ", line);
                Assert.Contains(" spawnUs=12345 ", line);
                Assert.EndsWith(" valid=true", line);
                Assert.DoesNotContain("16,25", line);

                var map = Step(SpikeS11View.Map, 16, 15, 16.25);
                map.InvalidReason = "built 15 of 16";
                string mapLine = SpikeS11Format.StepLine(map);
                Assert.Contains(" onScreen=n/a ", mapLine);
                Assert.EndsWith(" valid=false invalidReason=\"built 15 of 16\"", mapLine);

                Assert.Equal(SpikeS11Format.StepHeader.Length, SpikeS11Format.StepRow(r).Length);
                string row = Parsek.InGameTests.Spikes.SpikeCsv.FormatRow(SpikeS11Format.StepRow(r));
                Assert.DoesNotContain("16,25", row);
                Assert.Contains(",16.25,", row);
            }
            finally { Thread.CurrentThread.CurrentCulture = prev; }
        }

        [Fact]
        public void Format_FitAll_UsesBuiltAndFiltersView()
        {
            var steps = new List<SpikeS11StepResult>
            {
                Step(SpikeS11View.Flight, 0, 0, 10),
                Step(SpikeS11View.Flight, 8, 8, 12),
                Step(SpikeS11View.Map, 8, 8, 99),
                Step(SpikeS11View.Flight, 16, 12, 13), // spawn wait timed out at 12: x is 12
                null,
            };
            var fits = SpikeS11Format.FitAll(steps, SpikeS11View.Flight);
            Assert.Equal(SpikeS11Format.FitMetrics.Length, fits.Count);
            Assert.Equal("meanMs", fits[0].Key);
            Assert.Equal(3, fits[0].Value.Points);
            Assert.Equal(0.25, fits[0].Value.Slope, 9);
            Assert.Equal(10.0, fits[0].Value.Intercept, 9);

            var mapFits = SpikeS11Format.FitAll(steps, SpikeS11View.Map);
            Assert.False(mapFits[0].Value.Valid);
            string line = SpikeS11Format.FitLine("r", SpikeS11Pass.AsIs, SpikeS11View.Map, "meanMs", mapFits[0].Value);
            Assert.Equal("fit run=r pass=asis view=map metric=meanMs points=1 slopePerGhost=NaN intercept=NaN r2=NaN (invalid: fewer than two distinct ghost counts)", line);
            Assert.Equal(SpikeS11Format.FitHeader.Length,
                SpikeS11Format.FitRow("r", SpikeS11Pass.AsIs, SpikeS11View.Flight, "meanMs", fits[0].Value).Length);
            Assert.Equal("fit run=r pass=asis view=flight metric=meanMs points=3 slopePerGhost=0.2500 intercept=10.000 r2=1.000",
                SpikeS11Format.FitLine("r", SpikeS11Pass.AsIs, SpikeS11View.Flight, "meanMs", fits[0].Value));
        }

        // ------------------------------------------------------------ first open

        [Fact]
        public void FirstOpen_CommittedSidecarPair_OpensWithTheProductionReaders()
        {
            // Runs Parsek's own readers under the test host: a smoke check of the helper,
            // NOT the KSP number (that is attempt=1 in the game's firstopen line).
            string prec = ColdPrec();
            Assert.True(File.Exists(prec), "fixture sidecar missing: " + prec);
            var t = SpikeS11FirstOpen.OpenTrajectory(prec);
            Assert.True(t.Ok, t.Error);
            Assert.Equal(new FileInfo(prec).Length, t.Bytes);
            Assert.True(t.Items > 0);
            Assert.True(t.TotalMs >= t.DecodeMs);

            string craft = SpikeS11FirstOpen.GhostSnapshotFor(prec);
            Assert.NotNull(craft);
            var s = SpikeS11FirstOpen.OpenSnapshot(craft);
            Assert.True(s.Ok, s.Error);
            Assert.True(s.Items > 0);

            var b = SpikeS11FirstOpen.ReadBytes(prec);
            Assert.True(b.Ok);
            Assert.Equal(t.Bytes, b.Bytes);
        }

        [Fact]
        public void FirstOpen_FindsLargest_AndReportsABadFileWithoutThrowing()
        {
            string dir = TempDir();
            File.WriteAllBytes(Path.Combine(dir, "a.prec"), new byte[10]);
            File.WriteAllBytes(Path.Combine(dir, "b.prec"), new byte[30]);
            File.WriteAllBytes(Path.Combine(dir, "c.craft"), new byte[90]);
            Assert.Equal(Path.Combine(dir, "b.prec"), SpikeS11FirstOpen.FindLargestTrajectory(dir));
            Assert.Null(SpikeS11FirstOpen.FindLargestTrajectory(Path.Combine(dir, "missing")));
            Assert.Null(SpikeS11FirstOpen.GhostSnapshotFor(Path.Combine(dir, "b.prec")));

            var bad = SpikeS11FirstOpen.OpenTrajectory(Path.Combine(dir, "b.prec"));
            Assert.False(bad.Ok);
            Assert.NotNull(bad.Error);
            var missing = SpikeS11FirstOpen.OpenSnapshot(Path.Combine(dir, "nope_ghost.craft"));
            Assert.False(missing.Ok);
            Assert.NotNull(missing.Error);
        }

        [Fact]
        public void FirstOpen_LineAndRow_AreInvariant()
        {
            var prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var r = new SpikeS11OpenResult
                {
                    Kind = SpikeS11OpenKind.Trajectory, File = "x.prec", Bytes = 250106,
                    ProbeMs = 0.5, DecodeMs = 12.25, TotalMs = 12.75, Items = 4000, Ok = true,
                };
                Assert.Equal("firstopen attempt=1 kind=trajectory file=x.prec bytes=250106 probeMs=0.500 decodeMs=12.250 totalMs=12.750 items=4000 ok=true",
                    SpikeS11FirstOpen.Line(1, r));
                Assert.Equal(SpikeS11FirstOpen.CsvHeader.Length, SpikeS11FirstOpen.Row("run", 1, r).Length);
                r.Ok = false; r.Error = "probe failed: x";
                Assert.EndsWith(" ok=false error=\"probe failed: x\"", SpikeS11FirstOpen.Line(2, r));
            }
            finally { Thread.CurrentThread.CurrentCulture = prev; }
        }

        // ------------------------------------------------------------ injection fixture

        [Fact]
        public void Fixture_InjectsSixtyFourGhostTrees_IntoACopyOfTheHostSave()
        {
            string host = Path.Combine(RepoRoot(), "harness", "fixtures", "saves", "pad-runway-pair", "persistent.sfs");
            Assert.True(File.Exists(host), "host fixture missing: " + host);
            string saveDir = TempDir();
            string save = Path.Combine(saveDir, "persistent.sfs");
            File.Copy(host, save);

            var writer = new ScenarioWriter().WithV3Format();
            SpikeS11Fixture.PopulateWriter(writer, SpikeS11Fixture.HostSaveUT);
            string tmp = save + ".tmp";
            writer.InjectIntoSaveFile(save, tmp);
            string content = File.ReadAllText(tmp);

            Assert.Contains("name = ParsekScenario", content);
            for (int i = 0; i < SpikeS11Plan.MaxGhosts; i++)
                Assert.Contains(SpikeS11Plan.RecordingIdFor(i), content);
            Assert.Contains("vesselName = S11 Ghost 00", content);

            string recDir = Path.Combine(saveDir, "Parsek", "Recordings");
            Assert.Equal(SpikeS11Plan.MaxGhosts, Directory.GetFiles(recDir, "*.prec").Length);
            Assert.Equal(SpikeS11Plan.MaxGhosts, Directory.GetFiles(recDir, "*_ghost.craft").Length);
            Assert.Empty(Directory.GetFiles(recDir, "*_vessel.craft"));

            // each ghost sidecar decodes to the layout's points and one lit engine
            var t = SpikeS11FirstOpen.OpenTrajectory(Path.Combine(recDir, SpikeS11Plan.RecordingIdFor(5) + ".prec"));
            Assert.True(t.Ok, t.Error);
            Assert.Equal(SpikeS11Layout.PointCount + 1, t.Items);
            var g = SpikeS11FirstOpen.OpenSnapshot(Path.Combine(recDir, SpikeS11Plan.RecordingIdFor(5) + "_ghost.craft"));
            Assert.True(g.Ok, g.Error);
            Assert.Equal(3, g.Items);

            string cold = SpikeS11Fixture.CopyColdSidecar(RepoRoot(), saveDir);
            Assert.Equal(Path.Combine(saveDir, "SpikeS11", SpikeS11Fixture.ColdSourceRecordingId + ".prec"), cold);
            Assert.Equal(cold, SpikeS11FirstOpen.FindLargestTrajectory(Path.Combine(saveDir, "SpikeS11")));
            Assert.NotNull(SpikeS11FirstOpen.GhostSnapshotFor(cold));
            // a second copy replaces the folder rather than failing on the existing files
            Assert.Equal(cold, SpikeS11Fixture.CopyColdSidecar(RepoRoot(), saveDir));
        }

        [Fact]
        public void Fixture_FirstSample_IsTheLayoutAndStartsAfterTheHostClock()
        {
            var b = SpikeS11Fixture.BuildGhost(9, SpikeS11Fixture.HostSaveUT);
            var rec = b.Build();
            var pts = rec.GetNodes("POINT");
            Assert.Equal(SpikeS11Layout.PointCount, pts.Length);
            double ut0 = double.Parse(pts[0].GetValue("ut"), CultureInfo.InvariantCulture);
            Assert.Equal(SpikeS11Fixture.HostSaveUT + SpikeS11Layout.StartAfterSaveSeconds, ut0, 9);
            SpikeS11Layout.PositionAt(9, 0, out double lat, out double lon, out double alt);
            Assert.Equal(lat, double.Parse(pts[0].GetValue("lat"), CultureInfo.InvariantCulture), 9);
            Assert.Equal(lon, double.Parse(pts[0].GetValue("lon"), CultureInfo.InvariantCulture), 9);
            Assert.Equal(alt, double.Parse(pts[0].GetValue("alt"), CultureInfo.InvariantCulture), 9);
            Assert.Throws<ArgumentOutOfRangeException>(() => SpikeS11Fixture.BuildGhost(64, 0));
        }
    }
}
