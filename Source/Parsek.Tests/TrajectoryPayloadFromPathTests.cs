using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The save path's trajectory shrink diagnostic reads the existing sidecar it is about to
    /// overwrite. These cells pin every Parsek line that read writes, line for line, for each
    /// sidecar shape it can meet (clean, non-monotonic, healed, section-authoritative,
    /// unsupported version, foreign magic, truncated payload) and the warning it builds.
    /// The summary reads through <see cref="RecordingSidecarStore.TryLoadTrajectoryPayloadFromPath"/>,
    /// whose own refusals, decode and lines are pinned by the "Entry_" cells.
    /// </summary>
    [Collection("Sequential")]
    public class TrajectoryPayloadFromPathTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public TrajectoryPayloadFromPathTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            RecordingSidecarStore.WriteReadableSidecarMirrorsOverrideForTesting = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(Path.GetTempPath(), "parsek-payload-path-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingSidecarStore.WriteReadableSidecarMirrorsOverrideForTesting = null;
            RecordingStore.ResetForTesting();

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        // ------------------------------------------------------------------ shrink diagnostic lines

        [Fact]
        public void ShrinkSummary_CleanFlatSidecar_WritesNoLine_AndBuildsTheWarning()
        {
            string path = WriteFlat("char-rich", epoch: 6, goodPoints: 12, badPoints: 0);
            Recording caller = OnePointSection("char-rich", epoch: 6);

            logLines.Clear();
            Assert.True(RecordingSidecarStore.TryBuildTrajectoryShrinkageWarningForSave(caller, path, true, out string warning));

            AssertLines();
            Assert.Equal(ExpectedWarning("char-rich", 6, 7, "char-rich", 6, 12, 0, path), warning);
        }

        [Fact]
        public void ShrinkSummary_DroppingSidecar_WarnsWithTheCallersId_AndCountsTheKeptPoints()
        {
            string path = WriteFlat("char-file-id", epoch: 3, goodPoints: 12, badPoints: 1);
            Recording caller = OnePointSection("char-caller-id", epoch: 3);

            logLines.Clear();
            Assert.True(RecordingSidecarStore.TryBuildTrajectoryShrinkageWarningForSave(caller, path, false, out string warning));

            AssertLines(DropWarn("char-caller-id", 1));
            Assert.Equal(ExpectedWarning("char-caller-id", 3, 3, "char-file-id", 3, 12, 0, path), warning);
        }

        // Ten points on disk, one non-monotonic: nine survive the load, below the threshold.
        [Fact]
        public void ShrinkSummary_DroppingSidecar_BelowTheThresholdAfterTheDrop_DoesNotWarn()
        {
            string path = WriteFlat("char-nine", epoch: 2, goodPoints: 9, badPoints: 1);
            Recording caller = OnePointSection("char-nine", epoch: 2);

            logLines.Clear();
            Assert.False(RecordingSidecarStore.TryBuildTrajectoryShrinkageWarningForSave(caller, path, true, out string warning));

            AssertLines(DropWarn("char-nine", 1));
            Assert.Null(warning);
        }

        // A caller with no id: the drop Warn names the sidecar's own id.
        [Fact]
        public void ShrinkSummary_CallerWithNoId_DropWarnCarriesTheSidecarsId()
        {
            string path = WriteFlat("char-probe-only", epoch: 1, goodPoints: 11, badPoints: 2);
            Recording caller = OnePointSection("", epoch: 1);

            logLines.Clear();
            Assert.True(RecordingSidecarStore.TryBuildTrajectoryShrinkageWarningForSave(caller, path, true, out string warning));

            AssertLines(DropWarn("char-probe-only", 2));
            Assert.Equal(ExpectedWarning("<none>", 1, 2, "char-probe-only", 1, 11, 0, path), warning);
        }

        // RecordingStore.SuppressLogging silences the probe's Warns but not the drop Warn.
        [Fact]
        public void ShrinkSummary_UnderRecordingStoreSuppressLogging_WritesOnlyTheDropWarn()
        {
            string dropping = WriteFlat("char-suppressed", epoch: 4, goodPoints: 12, badPoints: 1);
            string unsupported = WriteUnsupportedVersion("char-suppressed-unsupported");
            Recording caller = OnePointSection("char-suppressed-caller", epoch: 4);

            RecordingStore.SuppressLogging = true;
            logLines.Clear();
            Assert.True(RecordingSidecarStore.TryBuildTrajectoryShrinkageWarningForSave(caller, dropping, true, out string warning));
            AssertLines(DropWarn("char-suppressed-caller", 1));
            Assert.Equal(ExpectedWarning("char-suppressed-caller", 4, 5, "char-suppressed", 4, 12, 0, dropping), warning);

            logLines.Clear();
            Assert.False(RecordingSidecarStore.TryBuildTrajectoryShrinkageWarningForSave(caller, unsupported, true, out warning));
            AssertLines();
        }

        [Fact]
        public void ShrinkSummary_SectionAuthoritativeSidecar_CountsTheRebuiltPoints_AndWritesNoLine()
        {
            string path = WriteSectionAuthoritative("char-section", epoch: 5, frames: 12);
            Recording caller = OnePointSection("char-section", epoch: 5);

            logLines.Clear();
            Assert.True(RecordingSidecarStore.TryBuildTrajectoryShrinkageWarningForSave(caller, path, true, out string warning));

            AssertLines();
            Assert.Equal(ExpectedWarning("char-section", 5, 6, "char-section", 5, 12, 1, path), warning);
        }

        // The read-path heal replaces the orbit list; the summary writes no heal line.
        [Fact]
        public void ShrinkSummary_HealedSidecar_WritesOnlyTheDropWarn()
        {
            string path = WriteHealShape("char-heal");
            Recording caller = OnePointSection("char-heal-caller", epoch: 4);

            logLines.Clear();
            Assert.False(RecordingSidecarStore.TryBuildTrajectoryShrinkageWarningForSave(caller, path, true, out string warning));

            AssertLines(DropWarn("char-heal-caller", 1));
            Assert.Null(warning);
        }

        [Fact]
        public void ShrinkSummary_UnsupportedVersion_WritesTheProbeWarnOnly_AndDeclines()
        {
            string path = WriteUnsupportedVersion("char-unsupported");
            Recording caller = OnePointSection("char-unsupported", epoch: 1);

            logLines.Clear();
            Assert.False(RecordingSidecarStore.TryBuildTrajectoryShrinkageWarningForSave(caller, path, true, out string warning));

            AssertLines(
                "[Parsek][WARN][RecordingStore] TryProbeTrajectorySidecar: unsupported binary trajectory " +
                "reason=format-version-mismatch version=99 generation=" +
                RecordingStore.CurrentRecordingSchemaGeneration.ToString(CultureInfo.InvariantCulture) +
                " magic=PSK0 for recording=char-unsupported");
            Assert.Null(warning);
        }

        [Fact]
        public void ShrinkSummary_ForeignMagic_WritesTheProbeWarnOnly_AndDeclines()
        {
            string path = Path.Combine(tempDir, "char-text.prec");
            File.WriteAllText(path, "RECORDING\n{\n}\n");
            Recording caller = OnePointSection("char-text", epoch: 1);

            logLines.Clear();
            Assert.False(RecordingSidecarStore.TryBuildTrajectoryShrinkageWarningForSave(caller, path, true, out string warning));

            AssertLines(
                "[Parsek][WARN][RecordingStore] TryProbeTrajectorySidecar: unsupported trajectory sidecar " +
                "reason=magic-mismatch path='" + path + "'");
            Assert.Null(warning);
        }

        [Fact]
        public void ShrinkSummary_TruncatedPayload_WritesTheUnableToEvaluateWarn_AndDeclines()
        {
            string path = WriteTruncated("char-truncated");
            Recording caller = OnePointSection("char-truncated-caller", epoch: 1);

            logLines.Clear();
            Assert.False(RecordingSidecarStore.TryBuildTrajectoryShrinkageWarningForSave(caller, path, true, out string warning));

            var lines = ParsekLines();
            Assert.True(lines.Count == 1, "got:\n" + string.Join("\n", lines));
            Assert.StartsWith(
                "[Parsek][WARN][RecordingStore] SaveRecordingFiles: unable to evaluate trajectory shrink diagnostic " +
                "for char-truncated-caller path='" + path + "' error=EndOfStreamException:",
                lines[0]);
            Assert.Null(warning);
        }

        // End to end: every line a save writes up to and including its shrink Warn.
        [Fact]
        public void SaveRecordingFiles_ShrinkOverADroppingSidecar_WritesTheDropWarnThenTheShrinkWarn()
        {
            string precPath = WriteFlat("char-save", epoch: 7, goodPoints: 12, badPoints: 1);
            Recording shrink = OnePointSection("char-save", epoch: 7);
            shrink.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            shrink.FilesDirty = true;

            logLines.Clear();
            Assert.True(RecordingSidecarStore.SaveRecordingFilesToPathsForTesting(
                shrink, precPath,
                Path.Combine(tempDir, "char-save_vessel.craft"),
                Path.Combine(tempDir, "char-save_ghost.craft"),
                incrementEpoch: true));

            var lines = ParsekLines();
            int shrinkIndex = lines.FindIndex(l => l.Contains("near-empty section-authoritative trajectory"));
            Assert.True(shrinkIndex >= 0, "got:\n" + string.Join("\n", lines));
            Assert.Equal(
                new[]
                {
                    DropWarn("char-save", 1),
                    "[Parsek][WARN][RecordingStore] " + ExpectedWarning("char-save", 7, 8, "char-save", 7, 12, 0, precPath),
                },
                lines.Take(shrinkIndex + 1).ToArray());
            Assert.Equal(1, lines.Count(l => l.Contains("near-empty section-authoritative trajectory")));
        }

        // ------------------------------------------------------------------ the entry

        [Fact]
        public void Entry_MissingFile_RefusesWithNoPayload_AndWritesNoLine()
        {
            logLines.Clear();
            foreach (string path in new[] { Path.Combine(tempDir, "absent.prec"), "", null })
            {
                Assert.False(RecordingSidecarStore.TryLoadTrajectoryPayloadFromPath(
                    path, quietProbe: false, out RecordingTrajectoryPayload payload, out TrajectorySidecarProbe probe));
                Assert.Null(payload);
                Assert.False(probe.Success);
            }
            AssertLines();
        }

        // A file with the magic but too short for the header: the probe fails, silently.
        [Fact]
        public void Entry_TruncatedHeader_RefusesWithNoPayload()
        {
            string path = Path.Combine(tempDir, "short.prec");
            File.WriteAllBytes(path, new byte[] { (byte)'P', (byte)'S', (byte)'K', (byte)'0', 1, 0, 0, 0 });

            logLines.Clear();
            Assert.False(RecordingSidecarStore.TryLoadTrajectoryPayloadFromPath(
                path, quietProbe: false, out RecordingTrajectoryPayload payload, out TrajectorySidecarProbe probe));
            Assert.Null(payload);
            Assert.False(probe.Success);
            Assert.Equal("binary header truncated", probe.FailureReason);
            AssertLines();
        }

        [Fact]
        public void Entry_ForeignMagic_RefusesWithNoPayload_AndTheProbeWarns()
        {
            string path = Path.Combine(tempDir, "text.prec");
            File.WriteAllText(path, "RECORDING\n{\n}\n");

            logLines.Clear();
            Assert.False(RecordingSidecarStore.TryLoadTrajectoryPayloadFromPath(
                path, quietProbe: true, out RecordingTrajectoryPayload payload, out TrajectorySidecarProbe probe));
            Assert.Null(payload);
            Assert.True(probe.Success);
            Assert.False(probe.Supported);
            Assert.Equal("magic-mismatch", probe.FailureReason);
            AssertLines(
                "[Parsek][WARN][RecordingStore] TryProbeTrajectorySidecar: unsupported trajectory sidecar " +
                "reason=magic-mismatch path='" + path + "'");
        }

        // An unsupported version is refused before any decode; quietProbe drops only the
        // routine Verbose probe line, never the Warn.
        [Fact]
        public void Entry_UnsupportedVersion_RefusesWithNoPayload_BeforeDecoding()
        {
            string path = WriteUnsupportedVersion("entry-unsupported");
            string gen = RecordingStore.CurrentRecordingSchemaGeneration.ToString(CultureInfo.InvariantCulture);
            string warn =
                "[Parsek][WARN][RecordingStore] TryProbeTrajectorySidecar: unsupported binary trajectory " +
                "reason=format-version-mismatch version=99 generation=" + gen +
                " magic=PSK0 for recording=entry-unsupported";

            logLines.Clear();
            Assert.False(RecordingSidecarStore.TryLoadTrajectoryPayloadFromPath(
                path, quietProbe: true, out RecordingTrajectoryPayload payload, out TrajectorySidecarProbe probe));
            Assert.Null(payload);
            Assert.True(probe.Success);
            Assert.False(probe.Supported);
            Assert.Equal(TrajectorySidecarEncoding.UnknownBinary, probe.Encoding);
            Assert.Equal(99, probe.FormatVersion);
            AssertLines(warn);

            logLines.Clear();
            Assert.False(RecordingSidecarStore.TryLoadTrajectoryPayloadFromPath(
                path, quietProbe: false, out payload, out probe));
            Assert.Null(payload);
            AssertLines(
                "[Parsek][VERBOSE][RecordingStore] TryProbeTrajectorySidecar: encoding=UnknownBinary magic=PSK0 " +
                "version=99 generation=" + gen + " recording=entry-unsupported sidecarEpoch=1",
                warn);
        }

        // Every FormatGolden sidecar loads to exactly what the cold load puts into a
        // Recording, with the header facts of the probe; quietProbe silences the one
        // routine line, and the entry writes nothing else.
        [Fact]
        public void Entry_EveryFormatGoldenPrec_EqualsWhatTheColdLoadReads()
        {
            string[] files = Directory.GetFiles(FormatGolden.VersionDir(1), "*.prec", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            Assert.Equal(7, files.Length);

            foreach (string path in files)
            {
                logLines.Clear();
                Assert.True(RecordingSidecarStore.TryLoadTrajectoryPayloadFromPath(
                    path, quietProbe: true, out RecordingTrajectoryPayload payload, out TrajectorySidecarProbe probe));
                AssertLines();

                Assert.NotNull(payload);
                Assert.True(probe.Supported);
                Assert.Equal(TrajectorySidecarEncoding.BinaryV0, probe.Encoding);
                Assert.Equal(probe.FormatVersion, payload.FormatVersion);
                Assert.Equal(probe.SchemaGeneration, payload.SchemaGeneration);
                Assert.Equal(probe.SidecarEpoch, payload.SidecarEpoch);
                Assert.Equal(probe.RecordingId, payload.ProbeRecordingId);

                var rec = new Recording { RecordingId = Path.GetFileNameWithoutExtension(path) };
                RecordingStore.SuppressLogging = true;
                try { Assert.True(RecordingSidecarStore.LoadTrajectorySidecarForTesting(path, rec)); }
                finally { RecordingStore.SuppressLogging = false; }
                Assert.Equal(RecordingTrajectoryPayloadTests.DumpLists(rec), RecordingTrajectoryPayloadTests.DumpLists(payload));
                Assert.Equal(rec.RecordingFormatVersion, payload.FormatVersion);
                Assert.Equal(rec.RecordingSchemaGeneration, payload.SchemaGeneration);

                logLines.Clear();
                Assert.True(RecordingSidecarStore.TryLoadTrajectoryPayloadFromPath(
                    path, quietProbe: false, out payload, out probe));
                var lines = ParsekLines();
                Assert.True(lines.Count == 1, "got:\n" + string.Join("\n", lines));
                Assert.StartsWith("[Parsek][VERBOSE][RecordingStore] TryProbeTrajectorySidecar: encoding=BinaryV0 ", lines[0]);
            }
        }

        // The decode drops the point; the Warn is the caller's to write, with its own id.
        [Fact]
        public void Entry_DroppingSidecar_ReportsTheDropInStats_AndWritesNoWarn()
        {
            string path = WriteFlat("entry-drop", epoch: 2, goodPoints: 4, badPoints: 1);

            logLines.Clear();
            Assert.True(RecordingSidecarStore.TryLoadTrajectoryPayloadFromPath(
                path, quietProbe: true, out RecordingTrajectoryPayload payload, out _));
            AssertLines();
            Assert.Equal(4, payload.Points.Count);
            Assert.Equal(1, payload.Stats.DroppedNonMonotonicPoints);
            Assert.False(payload.SectionAuthoritative);
        }

        [Fact]
        public void Entry_TruncatedPayload_ThrowsTheReadersException()
        {
            string path = WriteTruncated("entry-truncated");

            logLines.Clear();
            RecordingTrajectoryPayload payload = null;
            TrajectorySidecarProbe probe = default(TrajectorySidecarProbe);
            Exception ex = Record.Exception(() => RecordingSidecarStore.TryLoadTrajectoryPayloadFromPath(
                path, quietProbe: true, out payload, out probe));
            Assert.IsType<EndOfStreamException>(ex);
            Assert.Null(payload);
            AssertLines();
        }

        // ------------------------------------------------------------------ helpers

        private List<string> ParsekLines()
        {
            return logLines.Where(l => l.StartsWith("[Parsek]", StringComparison.Ordinal)).ToList();
        }

        private void AssertLines(params string[] expected)
        {
            var lines = ParsekLines();
            Assert.True(lines.Count == expected.Length,
                "expected " + expected.Length + " lines, got:\n" + string.Join("\n", lines));
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], lines[i]);
        }

        private static string DropWarn(string id, int dropped)
        {
            return "[Parsek][WARN][RecordingStore] ReadBinaryTrajectoryFile: recording=" + id + " dropped " +
                dropped.ToString(CultureInfo.InvariantCulture) +
                " non-monotonic flat trajectory point(s) on load (#419-class)";
        }

        private static string ExpectedWarning(
            string recId, int currentEpoch, int nextEpoch, string probeId, int probeEpoch,
            int existingPoints, int existingSections, string path)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "SaveRecordingFiles: near-empty section-authoritative trajectory will overwrite richer existing sidecar " +
                "recId={0} currentEpoch={1} nextEpoch={2} existingProbeRecordingId={3} " +
                "existingProbeEpoch={4} existingProbeVersion={5} existingProbeEncoding=BinaryV0 " +
                "existingPoints={6} incomingPoints=1 existingSections={7} incomingSections=1 " +
                "sectionAuthoritative=True path='{8}' - continuing normal save (diagnostic only)",
                recId, currentEpoch, nextEpoch, probeId, probeEpoch,
                RecordingStore.CurrentRecordingFormatVersion, existingPoints, existingSections, path);
        }

        private static TrajectoryPoint Pt(double ut) => new TrajectoryPoint
        {
            ut = ut, latitude = -0.05, longitude = -74.5, altitude = 1000.0 + ut, bodyName = "Kerbin",
        };

        private static TrackSection Section(params TrajectoryPoint[] frames)
        {
            return new TrackSection
            {
                environment = SegmentEnvironment.SurfaceMobile,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = frames[0].ut,
                endUT = frames[frames.Length - 1].ut,
                frames = new List<TrajectoryPoint>(frames),
                checkpoints = new List<OrbitSegment>(),
            };
        }

        // The near-empty incoming trajectory: one point, section-authoritative.
        private static Recording OnePointSection(string id, int epoch)
        {
            var rec = new Recording { RecordingId = id, SidecarEpoch = epoch };
            TrajectoryPoint only = Pt(100.0);
            rec.Points.Add(only);
            rec.TrackSections.Add(Section(only));
            Assert.True(TrajectoryTextSidecarCodec.ShouldWriteSectionAuthoritativeTrajectory(rec));
            return rec;
        }

        // A flat sidecar: goodPoints rising points, then badPoints that each go back in time.
        private string WriteFlat(string id, int epoch, int goodPoints, int badPoints)
        {
            var rec = new Recording { RecordingId = id, SidecarEpoch = epoch };
            for (int i = 0; i < goodPoints; i++)
                rec.Points.Add(Pt(100.0 + i));
            for (int i = 0; i < badPoints; i++)
                rec.Points.Add(Pt(50.0 + i));
            return WritePrec(rec, epoch);
        }

        private string WriteSectionAuthoritative(string id, int epoch, int frames)
        {
            var rec = new Recording { RecordingId = id, SidecarEpoch = epoch };
            var points = new TrajectoryPoint[frames];
            for (int i = 0; i < frames; i++)
                points[i] = Pt(100.0 + i);
            rec.TrackSections.Add(Section(points));
            rec.Points.AddRange(points);
            Assert.True(TrajectoryTextSidecarCodec.ShouldWriteSectionAuthoritativeTrajectory(rec));
            return WritePrec(rec, epoch);
        }

        // Flat path whose orbit list the read-path heal replaces, over one non-monotonic point.
        private string WriteHealShape(string id)
        {
            var rec = new Recording { RecordingId = id, SidecarEpoch = 4 };
            TrajectoryPoint p1 = Pt(1.0), p2 = Pt(2.0), p5 = Pt(5.0);
            OrbitSegment segA = Segment(100.0, 200.0, isPredicted: false);
            OrbitSegment segX = Segment(50.0, 90.0, isPredicted: true);
            rec.TrackSections.Add(Section(p1, p2));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = segA.startUT,
                endUT = segA.endUT,
                source = TrackSectionSource.Checkpoint,
                checkpoints = new List<OrbitSegment> { segA },
            });
            rec.Points.AddRange(new[] { p1, p5, p2 });
            rec.OrbitSegments.AddRange(new[] { segX, segA });
            string path = WritePrec(rec, 4);

            // The shape must really take the heal on load.
            var loaded = new Recording { RecordingId = id };
            RecordingStore.SuppressLogging = true;
            try { Assert.True(RecordingSidecarStore.LoadTrajectorySidecarForTesting(path, loaded)); }
            finally { RecordingStore.SuppressLogging = false; }
            Assert.Single(loaded.OrbitSegments);
            return path;
        }

        private static OrbitSegment Segment(double startUT, double endUT, bool isPredicted)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = 1.5,
                eccentricity = 0.01,
                semiMajorAxis = 700000.0,
                longitudeOfAscendingNode = 10.0,
                argumentOfPeriapsis = 20.0,
                meanAnomalyAtEpoch = 0.5,
                epoch = startUT,
                bodyName = "Kerbin",
                isPredicted = isPredicted,
            };
        }

        // A clean sidecar whose header names format version 99.
        private string WriteUnsupportedVersion(string id)
        {
            string path = WriteFlat(id, epoch: 1, goodPoints: 12, badPoints: 0);
            byte[] bytes = File.ReadAllBytes(path);
            byte[] version = BitConverter.GetBytes(99);
            Array.Copy(version, 0, bytes, 4, 4);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        // A clean sidecar cut three bytes short: the header probes, the payload does not read.
        private string WriteTruncated(string id)
        {
            string path = WriteFlat(id, epoch: 1, goodPoints: 12, badPoints: 0);
            byte[] bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes.Take(bytes.Length - 3).ToArray());
            return path;
        }

        private string WritePrec(Recording rec, int epoch)
        {
            string path = Path.Combine(tempDir, rec.RecordingId + ".prec");
            RecordingStore.SuppressLogging = true;
            try { TrajectorySidecarBinary.Write(path, rec, epoch); }
            finally { RecordingStore.SuppressLogging = false; }
            return path;
        }
    }
}
