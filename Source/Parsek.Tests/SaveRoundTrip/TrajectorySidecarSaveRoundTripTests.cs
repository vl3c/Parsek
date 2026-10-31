using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// Field-level save/load round-trip of the trajectory payload: every field of
    /// <see cref="TrajectoryPoint"/>, <see cref="OrbitSegment"/>, <see cref="PartEvent"/>,
    /// <see cref="FlagEvent"/>, <see cref="SegmentEvent"/> and <see cref="TrackSection"/>
    /// (with its frames, bodyFixedFrames and checkpoints) through BOTH codecs: the binary
    /// <c>.prec</c> sidecar (<see cref="TrajectorySidecarBinary"/>) and the text form
    /// (<see cref="TrajectoryTextSidecarCodec"/>, which carries the inline Re-Fly anchor
    /// snapshot in the .sfs and the readable <c>.prec.txt</c> mirror).
    ///
    /// <para>Every element is built by <see cref="FieldRoundTrip"/>, which fills each field
    /// with a distinct non-default value, so a field added to any element type without a
    /// codec arm reds here. The builders below only pin what the read path constrains:
    /// UTs (monotonic, sections in order and not overlapping, segments inside their
    /// section) and which nested lists a section's reference frame carries.</para>
    ///
    /// <para>Two writer shapes are covered. Section-authoritative: the flat Points /
    /// OrbitSegments are exactly the sections' payload, so the writer stores only the
    /// sections and the reader rebuilds the flat lists from them. Flat: the flat lists
    /// carry more than the sections (a tail), so they are written directly.</para>
    /// </summary>
    [Collection("Sequential")]
    [SaveRoundTripCovers(typeof(TrajectorySidecarBinary))]
    [SaveRoundTripCovers(typeof(TrajectoryTextSidecarCodec))]
    public class TrajectorySidecarSaveRoundTripTests : IDisposable
    {
        private readonly string tempDir;
        private readonly List<string> logLines = new List<string>();

        public TrajectorySidecarSaveRoundTripTests()
        {
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            tempDir = Path.Combine(Path.GetTempPath(), "parsek-rt-traj-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException) { }
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        // ------------------------------------------------------------------ builders

        private static FieldRoundTrip NewFiller(int seed) => new FieldRoundTrip(new FieldSpec[0], seed);

        private static TrajectoryPoint Point(FieldRoundTrip rt, double ut)
        {
            TrajectoryPoint p = rt.Build<TrajectoryPoint>();
            p.ut = ut;
            return p;
        }

        private static OrbitSegment Segment(FieldRoundTrip rt, double startUT, double endUT, bool predicted)
        {
            OrbitSegment s = rt.Build<OrbitSegment>();
            s.startUT = startUT;
            s.endUT = endUT;
            s.isPredicted = predicted;
            return s;
        }

        /// <summary>
        /// A section of the given frame over [startUT, endUT] carrying the lists that frame
        /// uses: frames for every frame, bodyFixedFrames (same UTs) for Relative, and two
        /// checkpoints splitting the span for OrbitalCheckpoint. Every scalar field keeps
        /// its distinct filled value.
        /// </summary>
        private static TrackSection Section(FieldRoundTrip rt, ReferenceFrame frame, double startUT, double endUT,
            int frameCount, bool firstCheckpointPredicted = false)
        {
            TrackSection s = rt.Build<TrackSection>();
            s.referenceFrame = frame;
            s.startUT = startUT;
            s.endUT = endUT;
            s.frames = new List<TrajectoryPoint>();
            for (int i = 0; i < frameCount; i++)
                s.frames.Add(Point(rt, startUT + (endUT - startUT) * (i + 0.5) / frameCount));
            s.bodyFixedFrames = null;
            s.checkpoints = null;
            if (frame == ReferenceFrame.Relative)
            {
                s.bodyFixedFrames = new List<TrajectoryPoint>();
                for (int i = 0; i < frameCount; i++)
                    s.bodyFixedFrames.Add(Point(rt, s.frames[i].ut));
            }
            if (frame == ReferenceFrame.OrbitalCheckpoint)
            {
                double mid = (startUT + endUT) / 2;
                s.checkpoints = new List<OrbitSegment>
                {
                    Segment(rt, startUT, mid, firstCheckpointPredicted),
                    Segment(rt, mid, endUT, true),
                };
            }
            return s;
        }

        private static void AddEvents(FieldRoundTrip rt, Recording rec)
        {
            for (int i = 0; i < 3; i++)
            {
                rec.PartEvents.Add(rt.Build<PartEvent>());
                rec.FlagEvents.Add(rt.Build<FlagEvent>());
                rec.SegmentEvents.Add(rt.Build<SegmentEvent>());
            }
        }

        /// <summary>
        /// Absolute, Relative and OrbitalCheckpoint sections whose payload IS the flat
        /// trajectory: the writer takes the section-authoritative path.
        /// </summary>
        private static Recording BuildSectionAuthoritative(int seed)
        {
            FieldRoundTrip rt = NewFiller(seed);
            var rec = new Recording { RecordingId = "rt-section-auth-" + seed };
            rec.TrackSections.Add(Section(rt, ReferenceFrame.Absolute, 100, 110, 3));
            rec.TrackSections.Add(Section(rt, ReferenceFrame.Relative, 120, 130, 3));
            rec.TrackSections.Add(Section(rt, ReferenceFrame.OrbitalCheckpoint, 140, 160, 2,
                firstCheckpointPredicted: false));
            TrajectoryTextSidecarCodec.RebuildPointsFromTrackSections(rec.TrackSections, rec.Points);
            TrajectoryTextSidecarCodec.RebuildOrbitSegmentsFromTrackSections(rec.TrackSections, rec.OrbitSegments);
            AddEvents(rt, rec);
            return rec;
        }

        /// <summary>
        /// An Absolute section plus a flat tail of points past it and flat orbit segments
        /// after that (one closed, one predicted): the flat lists extend the sections, so
        /// the writer stores them directly.
        /// </summary>
        private static Recording BuildFlat(int seed)
        {
            FieldRoundTrip rt = NewFiller(seed);
            var rec = new Recording { RecordingId = "rt-flat-" + seed };
            rec.TrackSections.Add(Section(rt, ReferenceFrame.Absolute, 100, 110, 3));
            rec.Points.AddRange(rec.TrackSections[0].frames);
            rec.Points.Add(Point(rt, 115));
            rec.Points.Add(Point(rt, 118));
            rec.OrbitSegments.Add(Segment(rt, 200, 300, predicted: false));
            rec.OrbitSegments.Add(Segment(rt, 300, 400, predicted: true));
            AddEvents(rt, rec);
            return rec;
        }

        // ------------------------------------------------------------------ codecs

        private Recording ThroughBinary(Recording written, int epoch, out TrajectorySidecarProbe probe,
            out bool sectionAuthoritative)
        {
            string path = Path.Combine(tempDir, written.RecordingId + ".prec");
            TrajectorySidecarBinary.Write(path, written, epoch);
            sectionAuthoritative = ReadSectionAuthoritativeFlag(path);
            Assert.True(TrajectorySidecarBinary.TryProbe(path, out probe), probe.FailureReason);
            Assert.True(probe.Supported, probe.FailureReason);
            var read = new Recording { RecordingId = written.RecordingId };
            TrajectorySidecarBinary.Read(path, read, probe);
            return read;
        }

        /// <summary>The flag byte that follows the .prec header (magic, 3 ints, recording id).</summary>
        private static bool ReadSectionAuthoritativeFlag(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                reader.ReadBytes(4);
                reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadString();
                return (reader.ReadByte() & 1) != 0;
            }
        }

        /// <summary>
        /// Text codec through the KSP text form: the node is printed and re-parsed, as the
        /// .sfs that holds the inline anchor snapshot is.
        /// </summary>
        private static Recording ThroughText(Recording written, out ConfigNode node)
        {
            var wrapper = new ConfigNode("WRAPPER");
            node = wrapper.AddNode("TRAJECTORY");
            TrajectoryTextSidecarCodec.SerializeTrajectoryInto(node, written);
            ConfigNode reparsed = ConfigNode.Parse(wrapper.ToString()).GetNode("WRAPPER").GetNode("TRAJECTORY");
            Assert.NotNull(reparsed);
            var read = new Recording { RecordingId = written.RecordingId };
            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(reparsed, read);
            node = reparsed;
            return read;
        }

        private static List<string> DiffTrajectory(Recording expected, Recording actual, string label)
        {
            var rt = NewFiller(0);
            var problems = new List<string>();
            problems.AddRange(rt.Diff(expected.Points, actual.Points, label + ".Points"));
            problems.AddRange(rt.Diff(expected.OrbitSegments, actual.OrbitSegments, label + ".OrbitSegments"));
            problems.AddRange(rt.Diff(expected.PartEvents, actual.PartEvents, label + ".PartEvents"));
            problems.AddRange(rt.Diff(expected.FlagEvents, actual.FlagEvents, label + ".FlagEvents"));
            problems.AddRange(rt.Diff(expected.SegmentEvents, actual.SegmentEvents, label + ".SegmentEvents"));
            problems.AddRange(rt.Diff(expected.TrackSections, actual.TrackSections, label + ".TrackSections"));
            return problems;
        }

        // ------------------------------------------------------------------ section-authoritative

        [Fact]
        public void Binary_SectionAuthoritative_EveryElementFieldRoundTrips()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                Recording written = BuildSectionAuthoritative(1000);
                Recording read = ThroughBinary(written, 7, out TrajectorySidecarProbe probe, out bool sectionAuth);

                Assert.True(sectionAuth, "the builder's sections carry the whole flat trajectory, "
                    + "so the writer must take the section-authoritative path this cell covers");
                // Write normalizes in place; the section-authoritative shape must be a no-op.
                List<string> normalized = DiffTrajectory(BuildSectionAuthoritative(1000), written, "write-normalize");
                Assert.True(normalized.Count == 0, FieldRoundTrip.Report("Write normalization", normalized));

                List<string> problems = DiffTrajectory(written, read, "binary");
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("Binary .prec (section-authoritative)", problems));

                Assert.Equal(written.RecordingId, probe.RecordingId);
                Assert.Equal(7, probe.SidecarEpoch);
                Assert.Equal(TrajectorySidecarBinary.CurrentBinaryVersion, probe.FormatVersion);
                Assert.Equal(RecordingStore.CurrentRecordingSchemaGeneration, probe.SchemaGeneration);
                Assert.Equal(probe.FormatVersion, read.RecordingFormatVersion);
                Assert.Equal(probe.SchemaGeneration, read.RecordingSchemaGeneration);
            });
        }

        [Fact]
        public void Text_SectionAuthoritative_EveryElementFieldRoundTrips()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                Recording written = BuildSectionAuthoritative(2000);
                Recording read = ThroughText(written, out ConfigNode node);

                Assert.Equal("True", node.GetValue("sectionAuthoritative"));
                Assert.Empty(node.GetNodes("POINT"));
                List<string> problems = DiffTrajectory(written, read, "text");
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("Text trajectory (section-authoritative)", problems));

                // The text header names the recording; the owning record carries its own id,
                // so the reader does not copy it back (asserted written, not read).
                Assert.Equal(written.RecordingId, node.GetValue("recordingId"));
                Assert.Equal(written.RecordingFormatVersion, TrajectoryTextSidecarCodec.GetTrajectoryFormatVersion(node));
                Assert.Equal(written.RecordingSchemaGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    node.GetValue("recordingSchemaGeneration"));
            });
        }

        // ------------------------------------------------------------------ flat

        [Fact]
        public void Binary_Flat_EveryElementFieldRoundTrips()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                Recording written = BuildFlat(3000);
                Recording read = ThroughBinary(written, 3, out TrajectorySidecarProbe probe, out bool sectionAuth);

                Assert.False(sectionAuth, "the flat tail extends the sections, so the writer must store the flat lists");
                AssertFlatWriteOnlyAddedTheCheckpointBridge(written, BuildFlat(3000));

                List<string> problems = DiffTrajectory(written, read, "binary");
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("Binary .prec (flat)", problems));
                Assert.Equal(3, probe.SidecarEpoch);
            });
        }

        [Fact]
        public void Text_Flat_EveryElementFieldRoundTrips()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                Recording written = BuildFlat(4000);
                Recording read = ThroughText(written, out ConfigNode node);

                Assert.Equal("False", node.GetValue("sectionAuthoritative"));
                Assert.Equal(written.Points.Count, node.GetNodes("POINT").Length);
                AssertFlatWriteOnlyAddedTheCheckpointBridge(written, BuildFlat(4000));

                List<string> problems = DiffTrajectory(written, read, "text");
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("Text trajectory (flat)", problems));
            });
        }

        /// <summary>
        /// The write path promotes the closed top-level orbit segment into a checkpoint
        /// section (OrbitSegmentCheckpointBridge) on the recording itself; everything else
        /// the builder made must be untouched, so the round-trip compares like with like.
        /// </summary>
        private static void AssertFlatWriteOnlyAddedTheCheckpointBridge(Recording afterWrite, Recording asBuilt)
        {
            Assert.Equal(asBuilt.TrackSections.Count + 1, afterWrite.TrackSections.Count);
            TrackSection bridge = afterWrite.TrackSections[afterWrite.TrackSections.Count - 1];
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, bridge.referenceFrame);
            afterWrite = ShallowWithSections(afterWrite, afterWrite.TrackSections.Take(asBuilt.TrackSections.Count));
            List<string> normalized = DiffTrajectory(asBuilt, afterWrite, "write-normalize");
            Assert.True(normalized.Count == 0, FieldRoundTrip.Report("Write normalization", normalized));
        }

        private static Recording ShallowWithSections(Recording rec, IEnumerable<TrackSection> sections)
        {
            return new Recording
            {
                RecordingId = rec.RecordingId,
                Points = rec.Points,
                OrbitSegments = rec.OrbitSegments,
                PartEvents = rec.PartEvents,
                FlagEvents = rec.FlagEvents,
                SegmentEvents = rec.SegmentEvents,
                TrackSections = sections.ToList(),
            };
        }

        // ------------------------------------------------------------------ RELATIVE flat fallback

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void FlatWithRelativeSection_FlatPointsAreTheBodyFixedSurfacePlusTail(bool binary)
        {
            // A Relative section's frames are anchor-local metres, so the flat fallback the
            // writer stores is the section's bodyFixedFrames (plus the flat tail outside the
            // section), never rec.Points' copy of the metre offsets. Every field of the
            // body-fixed samples must reach the loaded flat list.
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                FieldRoundTrip rt = NewFiller(5000);
                var written = new Recording { RecordingId = "rt-flat-relative-" + binary };
                written.TrackSections.Add(Section(rt, ReferenceFrame.Relative, 100, 110, 3));
                written.Points.AddRange(written.TrackSections[0].frames);
                written.Points.Add(Point(rt, 115));
                AddEvents(rt, written);

                Recording read;
                if (binary)
                {
                    read = ThroughBinary(written, 1, out _, out bool sectionAuth);
                    Assert.False(sectionAuth);
                }
                else
                {
                    read = ThroughText(written, out ConfigNode node);
                    Assert.Equal("False", node.GetValue("sectionAuthoritative"));
                }

                var expected = new Recording();
                expected.Points.AddRange(written.TrackSections[0].bodyFixedFrames);
                expected.Points.Add(written.Points[written.Points.Count - 1]);
                expected.PartEvents = written.PartEvents;
                expected.FlagEvents = written.FlagEvents;
                expected.SegmentEvents = written.SegmentEvents;
                expected.TrackSections = written.TrackSections;
                string codec = binary ? "binary" : "text";
                List<string> problems = DiffTrajectory(expected, read, codec);
                Assert.True(problems.Count == 0, FieldRoundTrip.Report(codec + " (flat, Relative)", problems));
            });
        }

        // ------------------------------------------------------------------ binary sparse point lists

        /// <summary>
        /// Twelve points sharing one body / funds / science / reputation, each of the four
        /// overridden on exactly one point; every other field keeps its distinct value.
        /// </summary>
        private static List<TrajectoryPoint> SparsePoints(FieldRoundTrip rt, double startUT, bool sharedFunds)
        {
            var list = new List<TrajectoryPoint>();
            for (int i = 0; i < 12; i++)
            {
                TrajectoryPoint p = Point(rt, startUT + i);
                p.bodyName = "Mun";
                if (sharedFunds)
                    p.funds = 12345.5;
                p.science = 6.25f;
                p.reputation = 7.75f;
                list.Add(p);
            }
            TrajectoryPoint o;
            o = list[2]; o.bodyName = "Minmus"; list[2] = o;
            o = list[4]; o.science = 99.5f; list[4] = o;
            o = list[6]; o.reputation = -3.5f; list[6] = o;
            if (sharedFunds)
            {
                o = list[8]; o.funds = 777.25; list[8] = o;
            }
            return list;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Binary_SparsePointList_KeepsEveryOverrideAndEveryDefault(bool sharedFunds)
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                FieldRoundTrip rt = NewFiller(sharedFunds ? 6000 : 7000);
                var written = new Recording { RecordingId = "rt-sparse-" + sharedFunds };
                written.Points.AddRange(SparsePoints(rt, 100, sharedFunds));
                // Section frames take the same sparse path: one Absolute section of its own.
                TrackSection s = Section(rt, ReferenceFrame.Absolute, 300, 320, 0);
                s.frames = SparsePoints(rt, 300, sharedFunds);
                written.TrackSections.Add(s);

                logLines.Clear();
                Recording read = ThroughBinary(written, 1, out _, out bool sectionAuth);
                Assert.False(sectionAuth);

                string writeLine = logLines.Single(l => l.Contains("WriteBinaryTrajectoryFile:"));
                Assert.Contains("sparsePointLists=2 ", writeLine);
                Assert.Contains("omittedBody=22 ", writeLine);
                Assert.Contains("omittedScience=22 ", writeLine);
                Assert.Contains("omittedRep=22", writeLine);
                Assert.Contains(sharedFunds ? "omittedFunds=22 " : "omittedFunds=0 ", writeLine);

                List<string> problems = DiffTrajectory(written, read, "binary");
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("Binary .prec (sparse points)", problems));
            });
        }

        // ------------------------------------------------------------------ negative

        [Fact]
        public void Harness_DetectsAFieldTheCodecDrops()
        {
            // The text codec writes checkpoints only for an OrbitalCheckpoint section (no
            // producer puts them on another frame). An Absolute section carrying them is a
            // real codec drop, and the diff must name it.
            FieldRoundTrip rt = NewFiller(8000);
            var written = new Recording { RecordingId = "rt-negative" };
            TrackSection s = Section(rt, ReferenceFrame.Absolute, 100, 110, 2);
            s.checkpoints = new List<OrbitSegment> { Segment(rt, 100, 110, predicted: true) };
            written.TrackSections.Add(s);
            written.Points.AddRange(s.frames);
            written.Points.Add(Point(rt, 120));

            Recording read = ThroughText(written, out _);
            List<string> problems = DiffTrajectory(written, read, "text");
            Assert.Contains(problems, p => p.Contains("TrackSections[0].checkpoints"));
        }
    }
}
