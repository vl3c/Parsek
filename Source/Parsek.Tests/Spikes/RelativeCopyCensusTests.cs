using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xunit;

namespace Parsek.Tests.Spikes
{
    // Spike S10a (branch spike/mp-r0 only): how many Relative track sections carry a
    // complete body-fixed copy. The pure cells run in every suite; the manual census
    // runs only when PARSEK_SPIKE_S10_SAVE is set (shaped like
    // OfflineAnalyzerTests.Manual_AnalyzeEnvSave_WritesReports).
    [Collection("Sequential")]
    public class RelativeCopyCensusTests : IDisposable
    {
        public RelativeCopyCensusTests()
        {
            ParsekLog.TestSinkForTesting = line => { };
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static TrajectoryPoint P(double ut) => new TrajectoryPoint { ut = ut, bodyName = "Kerbin" };

        private static TrackSection Relative(double start, double end, double[] frameUts, double[] bfUts)
        {
            var s = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = start,
                endUT = end,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active,
            };
            if (frameUts != null) foreach (double u in frameUts) s.frames.Add(P(u));
            if (bfUts != null)
            {
                s.bodyFixedFrames = new List<TrajectoryPoint>();
                foreach (double u in bfUts) s.bodyFixedFrames.Add(P(u));
            }
            return s;
        }

        [Fact]
        public void Measure_CompleteCopy_CoversWholeSection()
        {
            var s = Relative(100, 110, new double[] { 100, 105, 110 }, new double[] { 100, 105, 110 });
            RelativeCopyMeasure m = RelativeCopyCensus.Measure(s);
            Assert.Equal(RelativeCopyClass.Complete, m.Class);
            Assert.Equal(3, m.Frames);
            Assert.Equal(3, m.BodyFixedFrames);
            Assert.Equal(10.0, m.DurationSeconds, 9);
            Assert.Equal(10.0, m.CoveredSeconds, 9);
            Assert.Equal(1.0, m.CoverageFraction, 9);
            Assert.Equal(0.0, m.GapStartSeconds, 9);
            Assert.Equal(0.0, m.GapEndSeconds, 9);
            Assert.True(m.BodyFixedMatchesFrames);
        }

        [Fact]
        public void Measure_CopyMatchesFramesButSectionEndsLater_IsPartialWithMatchFlag()
        {
            // The fixture shape: the section's endUT is the next section's start, one
            // sample period after the last frame; the copy shadows every frame.
            var s = Relative(100, 101, new double[] { 100, 100.5, 100.94 }, new double[] { 100, 100.5, 100.94 });
            RelativeCopyMeasure m = RelativeCopyCensus.Measure(s);
            Assert.Equal(RelativeCopyClass.Partial, m.Class);
            Assert.True(m.BodyFixedMatchesFrames);
            Assert.Equal(0.06, m.GapEndSeconds, 9);
            Assert.Equal(0.06, m.FramesGapEndSeconds, 9);
        }

        [Fact]
        public void Measure_ExactlyTwoSamplesAtTheEnds_IsComplete()
        {
            var s = Relative(100, 110, new double[] { 100, 103, 110 }, new double[] { 100, 110 });
            RelativeCopyMeasure m = RelativeCopyCensus.Measure(s);
            Assert.Equal(RelativeCopyClass.Complete, m.Class);
            Assert.Equal(2, m.BodyFixedFrames);
            Assert.Equal(10.0, m.CoveredSeconds, 9);
        }

        [Fact]
        public void Measure_TwoSamplesJustOutsideTolerance_IsPartial()
        {
            double late = 110 - (RelativeCopyCensus.CoverageToleranceSeconds * 2);
            var s = Relative(100, 110, new double[] { 100, 110 }, new double[] { 100, late });
            RelativeCopyMeasure m = RelativeCopyCensus.Measure(s);
            Assert.Equal(RelativeCopyClass.Partial, m.Class);
            Assert.Equal(RelativeCopyCensus.CoverageToleranceSeconds * 2, m.GapEndSeconds, 9);
        }

        [Fact]
        public void Measure_TwoSamplesWithinTolerance_IsComplete()
        {
            double almost = 110 - (RelativeCopyCensus.CoverageToleranceSeconds / 2);
            var s = Relative(100, 110, new double[] { 100, 110 }, new double[] { 100, almost });
            Assert.Equal(RelativeCopyClass.Complete, RelativeCopyCensus.Measure(s).Class);
        }

        [Fact]
        public void Measure_PartialCopy_ReportsCoveredSecondsAndGaps()
        {
            var s = Relative(100, 120, new double[] { 100, 110, 120 }, new double[] { 105, 110, 115 });
            RelativeCopyMeasure m = RelativeCopyCensus.Measure(s);
            Assert.Equal(RelativeCopyClass.Partial, m.Class);
            Assert.False(m.BodyFixedMatchesFrames);
            Assert.Equal(10.0, m.CoveredSeconds, 9);
            Assert.Equal(5.0, m.GapStartSeconds, 9);
            Assert.Equal(5.0, m.GapEndSeconds, 9);
            Assert.Equal(0.5, m.CoverageFraction, 9);
        }

        [Fact]
        public void Measure_SingleSample_IsSingleAndCoversNothing()
        {
            var s = Relative(100, 110, new double[] { 100, 110 }, new double[] { 105 });
            RelativeCopyMeasure m = RelativeCopyCensus.Measure(s);
            Assert.Equal(RelativeCopyClass.Single, m.Class);
            Assert.Equal(1, m.BodyFixedFrames);
            Assert.Equal(0.0, m.CoveredSeconds, 9);
            Assert.Equal(0.0, m.CoverageFraction, 9);
            Assert.Equal(105.0, m.BodyFixedFirstUT, 9);
        }

        [Fact]
        public void Measure_NoCopy_NullOrEmptyList_IsNone()
        {
            var nullList = Relative(100, 110, new double[] { 100, 110 }, null);
            var emptyList = Relative(100, 110, new double[] { 100, 110 }, new double[0]);
            foreach (var s in new[] { nullList, emptyList })
            {
                RelativeCopyMeasure m = RelativeCopyCensus.Measure(s);
                Assert.Equal(RelativeCopyClass.None, m.Class);
                Assert.Equal(0, m.BodyFixedFrames);
                Assert.True(double.IsNaN(m.BodyFixedFirstUT));
                Assert.Equal(10.0, m.GapStartSeconds, 9);
            }
        }

        [Fact]
        public void Measure_ZeroLengthSection_TwoSamplesIsComplete()
        {
            var s = Relative(100, 100, new double[] { 100 }, new double[] { 100, 100 });
            RelativeCopyMeasure m = RelativeCopyCensus.Measure(s);
            Assert.Equal(RelativeCopyClass.Complete, m.Class);
            Assert.Equal(1.0, m.CoverageFraction, 9);
        }

        [Theory]
        [InlineData(true, true, true, TrackSectionSource.Background, "eva")]
        [InlineData(false, true, true, TrackSectionSource.Active, "parent-anchored")]
        [InlineData(false, false, true, TrackSectionSource.Background, "docking")]
        [InlineData(false, false, false, TrackSectionSource.Active, "active")]
        [InlineData(false, false, false, TrackSectionSource.Background, "background")]
        [InlineData(false, false, false, TrackSectionSource.Checkpoint, "checkpoint")]
        public void ClassifySource_FirstMatchWins(
            bool eva, bool parentAnchored, bool dock, TrackSectionSource src, string expected)
        {
            Assert.Equal(expected, RelativeCopyCensus.ClassifySource(eva, parentAnchored, dock, src));
        }

        [Fact]
        public void DockLinks_DockOrUndockOnly_AndFormatSorted()
        {
            Assert.False(RelativeCopyCensus.HasDockLink(null));
            Assert.False(RelativeCopyCensus.HasDockLink(new HashSet<BranchPointType> { BranchPointType.EVA }));
            Assert.True(RelativeCopyCensus.HasDockLink(new HashSet<BranchPointType> { BranchPointType.Undock }));
            Assert.Equal("Dock|EVA|Launch", RelativeCopyCensus.FormatLinks(
                new HashSet<BranchPointType> { BranchPointType.Launch, BranchPointType.EVA, BranchPointType.Dock }));
            Assert.Equal("", RelativeCopyCensus.FormatLinks(null));
        }

        [Fact]
        public void Row_IsInvariantAndMatchesHeaderWidth()
        {
            CultureInfo prev = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                var s = Relative(100.5, 110.25, new double[] { 100.5, 110.25 }, new double[] { 100.5, 110.25 });
                RelativeCopyMeasure m = RelativeCopyCensus.Measure(s);
                object[] row = RelativeCopyCensus.Row("lbl", 0, false, "abc", 2, "active", s, m,
                    false, false, false, "Dock|Undock");
                Assert.Equal(RelativeCopyCensus.Header.Length, row.Length);
                string line = Parsek.InGameTests.Spikes.SpikeCsv.FormatRow(row);
                Assert.Contains(",100.5,110.25,9.75,", line);
                Assert.Contains(",complete,", line);
                Assert.DoesNotContain("100,5", line);
            }
            finally
            {
                CultureInfo.CurrentCulture = prev;
            }
        }

        [Fact]
        public void ParseSaveList_SplitsOnSemicolonAndTrims()
        {
            Assert.Empty(RelativeCopyCensus.ParseSaveList(null));
            Assert.Equal(new[] { "a", "b c" }, RelativeCopyCensus.ParseSaveList(" a ;; b c ;").ToArray());
        }

        [Fact]
        public void RefuseReason_RefusesALiveSaveInsideAnInstall()
        {
            string root = Path.Combine(Path.GetTempPath(), "s10a-refuse-" + Guid.NewGuid().ToString("N"));
            try
            {
                string live = Path.Combine(root, "KSP", "saves", "career");
                Directory.CreateDirectory(live);
                Directory.CreateDirectory(Path.Combine(root, "KSP", "GameData"));
                File.WriteAllText(Path.Combine(live, "persistent.sfs"), "GAME\n{\n}\n");
                string copy = Path.Combine(root, "copies", "career-A");
                Directory.CreateDirectory(copy);
                File.WriteAllText(Path.Combine(copy, "persistent.sfs"), "GAME\n{\n}\n");

                Assert.NotNull(RelativeCopyCensus.RefuseReason(live));
                Assert.Null(RelativeCopyCensus.RefuseReason(copy));
                Assert.Equal("no persistent.sfs", RelativeCopyCensus.RefuseReason(root));
                Assert.Equal("career-A", RelativeCopyCensus.LabelFor(copy + Path.DirectorySeparatorChar));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        // The census. PARSEK_SPIKE_S10_SAVE: one save folder, or several separated by ';'
        // (each a COPY, never a save inside a KSP install). PARSEK_SPIKE_OUT: the output
        // root; each save writes <out>/<label>/relative_census.csv and load_summary.csv,
        // where <label> is the save folder's name. Unset: no-op.
        [Fact]
        [Trait("Category", "Manual")]
        public void Manual_RelativeCopyCensus_WritesCsv()
        {
            List<string> saves = RelativeCopyCensus.ParseSaveList(
                Environment.GetEnvironmentVariable("PARSEK_SPIKE_S10_SAVE"));
            if (saves.Count == 0)
                return; // env unset -> skip cleanly (CI-safe)

            string outRoot = Environment.GetEnvironmentVariable("PARSEK_SPIKE_OUT");
            Assert.False(string.IsNullOrEmpty(outRoot), "PARSEK_SPIKE_OUT must name the output folder");

            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool prevStoreSuppress = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = true;
            try
            {
                foreach (string save in saves)
                {
                    string refuse = RelativeCopyCensus.RefuseReason(save);
                    Assert.True(refuse == null, "refused '" + save + "': " + refuse);
                    string label = RelativeCopyCensus.LabelFor(save);
                    Assert.True(labels.Add(label), "two saves share the label '" + label + "'");

                    RelativeCopyCensus.SaveCensus census = RelativeCopyCensus.Run(save, label);
                    string outDir = Path.Combine(outRoot, label);
                    RelativeCopyCensus.Write(outDir, census);
                    Assert.True(File.Exists(Path.Combine(outDir, "relative_census.csv")));
                }
            }
            finally
            {
                RecordingStore.SuppressLogging = prevStoreSuppress;
            }
        }

        // Hand check: PARSEK_SPIKE_S10_DUMP = "<save dir>|<recording id>" (several separated
        // by ';') dumps every section of that recording, read through the production
        // codec, to <PARSEK_SPIKE_OUT>/dump_<recording id>.txt. Unset: no-op.
        [Fact]
        [Trait("Category", "Manual")]
        public void Manual_DumpRecordingSections()
        {
            string spec = Environment.GetEnvironmentVariable("PARSEK_SPIKE_S10_DUMP");
            if (string.IsNullOrEmpty(spec))
                return;
            string outRoot = Environment.GetEnvironmentVariable("PARSEK_SPIKE_OUT");
            Assert.False(string.IsNullOrEmpty(outRoot), "PARSEK_SPIKE_OUT must name the output folder");
            foreach (string one in RelativeCopyCensus.ParseSaveList(spec))
                DumpOne(one, outRoot);
        }

        private static void DumpOne(string spec, string outRoot)
        {
            string[] parts = spec.Split('|');
            Assert.Equal(2, parts.Length);
            string saveDir = parts[0];
            string recId = parts[1];
            Assert.True(RecordingPaths.ValidateRecordingId(recId, RecordingIdValidationLogContext.Test));

            var rec = new Recording { RecordingId = recId };
            string prec = Path.Combine(saveDir, RecordingPaths.BuildTrajectoryRelativePath(recId));
            Assert.True(RecordingSidecarStore.LoadTrajectorySidecarForTesting(prec, rec), "codec read failed: " + prec);

            var ic = CultureInfo.InvariantCulture;
            var lines = new List<string> { "recording " + recId + " sections=" + rec.TrackSections.Count };
            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                TrackSection s = rec.TrackSections[i];
                lines.Add("[" + i.ToString(ic) + "] " + s);
                if (s.frames != null && s.frames.Count > 0)
                    lines.Add("    frames first=" + s.frames[0].ut.ToString("R", ic)
                        + " last=" + s.frames[s.frames.Count - 1].ut.ToString("R", ic));
                if (s.bodyFixedFrames != null && s.bodyFixedFrames.Count > 0)
                    lines.Add("    bodyFixed first=" + s.bodyFixedFrames[0].ut.ToString("R", ic)
                        + " last=" + s.bodyFixedFrames[s.bodyFixedFrames.Count - 1].ut.ToString("R", ic)
                        + " body=" + s.bodyFixedFrames[0].bodyName);
                lines.Add("    startUT=" + s.startUT.ToString("R", ic) + " endUT=" + s.endUT.ToString("R", ic));
            }
            Directory.CreateDirectory(outRoot);
            File.WriteAllLines(Path.Combine(outRoot, "dump_" + recId + ".txt"), lines.ToArray());
        }
    }
}
