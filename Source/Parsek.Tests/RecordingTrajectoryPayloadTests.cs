using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// <see cref="TrajectorySidecarBinary.Decode"/> into a <see cref="RecordingTrajectoryPayload"/>
    /// is what <see cref="TrajectorySidecarBinary.Read"/> puts into a <see cref="Recording"/>:
    /// same lists, same header facts, the recording's own list instances kept unless the heal
    /// replaced one, the same partial state on a throw, and the same log lines. The P6-01
    /// manifest (<see cref="TrajectoryCodecFixtureIdentityTests"/>) pins the bytes and logs of
    /// every committed fixture; these cells cover what its dump cannot see (it omits null,
    /// zero and false values) and a heal-plus-drop case no committed fixture carries.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingTrajectoryPayloadTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public RecordingTrajectoryPayloadTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            RecordingSidecarStore.WriteReadableSidecarMirrorsOverrideForTesting = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(Path.GetTempPath(), "parsek-payload-" + Guid.NewGuid().ToString("N"));
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

        // ------------------------------------------------------------------ decode == read

        [Fact]
        public void Decode_EveryFormatGoldenPrec_EqualsWhatReadPutsIntoARecording()
        {
            string[] files = Directory.GetFiles(FormatGolden.VersionDir(1), "*.prec", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            Assert.Equal(7, files.Length);

            foreach (string path in files)
                AssertDecodeEqualsRead(path);
        }

        [Fact]
        public void Decode_EveryCommittedFixture_EqualsRead_CoveringHealRebuildAndDrop()
        {
            string root = SyntheticRecordingTests.ResolveProjectRoot();
            int healed = 0, sectionAuthoritative = 0, dropped = 0, decoded = 0;
            foreach (string rel in TrajectoryCodecFixtureIdentityTests.EnumerateFixtures(root))
            {
                string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                RecordingTrajectoryPayload payload = AssertDecodeEqualsRead(path);
                if (payload == null)
                    continue;
                decoded++;
                if (payload.Stats.HealedFlatFallback) healed++;
                if (payload.SectionAuthoritative) sectionAuthoritative++;
                if (payload.Stats.DroppedNonMonotonicPoints > 0) dropped++;
            }

            // Non-vacuity: the walk reaches each branch of the decode.
            Assert.True(decoded > 500, "decoded " + decoded);
            Assert.True(healed > 0, "no committed fixture runs the read-path heal");
            Assert.True(sectionAuthoritative > 0, "no committed fixture is section-authoritative");
            Assert.True(decoded - sectionAuthoritative > 0, "no committed fixture reads the flat path");
            // dropped may be 0: the drop is pinned by the synthetic cells below.
        }

        // Returns null for a fixture the cold load does not decode (probe failed / unsupported).
        private RecordingTrajectoryPayload AssertDecodeEqualsRead(string path)
        {
            string name = Path.GetFileName(path);
            TrajectorySidecarProbe probe;
            if (!RecordingSidecarStore.TryProbeTrajectorySidecar(path, out probe) || !probe.Supported)
                return null;

            var rec = new Recording
            {
                RecordingId = Path.GetFileNameWithoutExtension(path),
                SidecarEpoch = probe.SidecarEpoch,
            };
            object[] before = Lists(rec);
            TrajectorySidecarBinary.Read(path, rec, probe);

            var payload = new RecordingTrajectoryPayload();
            TrajectorySidecarBinary.Decode(path, probe, payload);

            Assert.True(DumpLists(payload) == DumpLists(rec), "decode differs from read for " + name);
            Assert.Equal(probe.FormatVersion, payload.FormatVersion);
            Assert.Equal(probe.SchemaGeneration, payload.SchemaGeneration);
            Assert.Equal(probe.SidecarEpoch, payload.SidecarEpoch);
            Assert.Equal(probe.RecordingId, payload.ProbeRecordingId);
            Assert.Equal(rec.RecordingFormatVersion, payload.FormatVersion);
            Assert.Equal(rec.RecordingSchemaGeneration, payload.SchemaGeneration);

            // List identity: the four lists the heal never touches are always the
            // recording's own; Points / OrbitSegments only change when the heal ran.
            object[] after = Lists(rec);
            for (int i = 2; i < before.Length; i++)
                Assert.True(ReferenceEquals(before[i], after[i]), "list " + i + " replaced for " + name);
            if (!payload.Stats.HealedFlatFallback)
            {
                Assert.True(ReferenceEquals(before[0], after[0]), "Points replaced without a heal for " + name);
                Assert.True(ReferenceEquals(before[1], after[1]), "OrbitSegments replaced without a heal for " + name);
            }
            Assert.False(rec.FilesDirty, "a read dirtied " + name);
            return payload;
        }

        [Fact]
        public void Read_WithNoHeal_KeepsEveryListInstanceAndStampsTheRecording()
        {
            var written = new Recording { RecordingId = "payload-plain" };
            written.Points.Add(MakePoint(1.0, 0));
            written.Points.Add(MakePoint(2.0, 1));
            string path = WritePrec(written, sidecarEpoch: 7);
            TrajectorySidecarProbe probe = Probe(path);

            var rec = new Recording { RecordingId = "", RecordingFormatVersion = 99, RecordingSchemaGeneration = 98 };
            rec.Points.Add(MakePoint(50.0, 9));
            object[] before = Lists(rec);
            TrajectorySidecarBinary.Read(path, rec, probe);

            object[] after = Lists(rec);
            for (int i = 0; i < before.Length; i++)
                Assert.Same(before[i], after[i]);
            Assert.Equal(2, rec.Points.Count);
            Assert.Equal("payload-plain", rec.RecordingId);
            Assert.Equal(probe.FormatVersion, rec.RecordingFormatVersion);
            Assert.Equal(probe.SchemaGeneration, rec.RecordingSchemaGeneration);
            Assert.False(rec.FilesDirty);

            var payload = new RecordingTrajectoryPayload();
            TrajectorySidecarBinary.Decode(path, probe, payload);
            Assert.Equal(7, payload.SidecarEpoch);
            Assert.Equal("payload-plain", payload.ProbeRecordingId);
            Assert.False(payload.SectionAuthoritative);
            Assert.False(payload.Stats.HealedFlatFallback);
        }

        [Fact]
        public void OverListsOf_SharesTheRecordingsListInstances()
        {
            var rec = new Recording();
            RecordingTrajectoryPayload payload = RecordingTrajectoryPayload.OverListsOf(rec);
            Assert.Same(rec.Points, payload.Points);
            Assert.Same(rec.OrbitSegments, payload.OrbitSegments);
            Assert.Same(rec.PartEvents, payload.PartEvents);
            Assert.Same(rec.FlagEvents, payload.FlagEvents);
            Assert.Same(rec.SegmentEvents, payload.SegmentEvents);
            Assert.Same(rec.TrackSections, payload.TrackSections);
        }

        // ------------------------------------------------------------------ throwing decode

        [Fact]
        public void TruncatedPayload_ReadLeavesTheSamePartialStateAsDecode_AndStampsNothing()
        {
            string golden = Directory.GetFiles(FormatGolden.VersionDir(1), "*.prec", SearchOption.AllDirectories)
                .OrderByDescending(f => new FileInfo(f).Length)
                .First();
            byte[] bytes = File.ReadAllBytes(golden);
            int headerEnd = HeaderLength(golden);
            int threw = 0;

            // Cuts across the flag byte, the string table and deep into the lists.
            int[] cuts =
            {
                headerEnd + 2,
                headerEnd + (bytes.Length - headerEnd) / 4,
                headerEnd + (bytes.Length - headerEnd) / 2,
                bytes.Length - 3,
            };
            foreach (int cut in cuts)
            {
                string path = Path.Combine(tempDir, "cut-" + cut.ToString(CultureInfo.InvariantCulture) + ".prec");
                File.WriteAllBytes(path, bytes.Take(cut).ToArray());
                TrajectorySidecarProbe probe = Probe(path);

                var rec = new Recording
                {
                    RecordingId = "keep-id",
                    RecordingFormatVersion = 99,
                    RecordingSchemaGeneration = 98,
                };
                rec.Points.Add(MakePoint(50.0, 9));
                rec.TrackSections.Add(new TrackSection { startUT = 1, endUT = 2 });
                object[] before = Lists(rec);
                Exception readEx = Record.Exception(() => TrajectorySidecarBinary.Read(path, rec, probe));

                // A payload holding the same pre-read contents, so a throw before the
                // clear (the shallow cut) compares like for like.
                var payload = new RecordingTrajectoryPayload();
                payload.Points.Add(MakePoint(50.0, 9));
                payload.TrackSections.Add(new TrackSection { startUT = 1, endUT = 2 });
                Exception decodeEx = Record.Exception(() => TrajectorySidecarBinary.Decode(path, probe, payload));

                Assert.NotNull(readEx);
                Assert.NotNull(decodeEx);
                Assert.Equal(readEx.GetType(), decodeEx.GetType());
                threw++;

                // Nothing stamped, no list replaced, no dirty flag, and the lists hold
                // exactly what the decode got through before it threw.
                Assert.Equal("keep-id", rec.RecordingId);
                Assert.Equal(99, rec.RecordingFormatVersion);
                Assert.Equal(98, rec.RecordingSchemaGeneration);
                Assert.False(rec.FilesDirty);
                object[] after = Lists(rec);
                for (int i = 0; i < before.Length; i++)
                    Assert.Same(before[i], after[i]);
                Assert.Equal(DumpLists(payload), DumpLists(rec));

                // The shallow cut throws in the string table, before the lists are
                // cleared; the deeper cuts throw after, so the pre-read contents are gone.
                if (cut == headerEnd + 2)
                    Assert.Contains(rec.Points, p => p.ut == 50.0);
                else
                    Assert.DoesNotContain(rec.Points, p => p.ut == 50.0);

                // TryValidatePayload reports the same failure family.
                Assert.False(TrajectorySidecarBinary.TryValidatePayload(path, probe, out string reason));
                Assert.Contains(readEx.GetType().Name, reason);
            }
            Assert.Equal(cuts.Length, threw);
        }

        // ------------------------------------------------------------------ log lines

        [Fact]
        public void FlatPathDrop_LogsPreDropPostPointsThenTheWarn()
        {
            string path = WriteFlatDropPrec();
            TrajectorySidecarProbe probe = Probe(path);

            var rec = new Recording { RecordingId = "payload-flat-drop" };
            logLines.Clear();
            TrajectorySidecarBinary.Read(path, rec, probe);

            Assert.Equal(2, rec.Points.Count);
            AssertLogSequence(
                "[RecordingStore] ReadBinaryTrajectoryFile: recording=payload-flat-drop version=1 used flat fallback path " +
                "healed=false prePoints=3 postPoints=3 preOrbitSegments=0 postOrbitSegments=0 trackSections=0",
                "[RecordingStore] ReadBinaryTrajectoryFile: recording=payload-flat-drop dropped 1 non-monotonic flat " +
                "trajectory point(s) on load (#419-class)");
        }

        [Fact]
        public void SectionAuthoritativeDrop_LogsPreDropRebuiltPointsThenTheWarn()
        {
            string path = WriteSectionDropPrec();
            TrajectorySidecarProbe probe = Probe(path);

            var rec = new Recording { RecordingId = "payload-section-drop" };
            logLines.Clear();
            TrajectorySidecarBinary.Read(path, rec, probe);

            Assert.Equal(2, rec.Points.Count);
            AssertLogSequence(
                "[RecordingStore] ReadBinaryTrajectoryFile: recording=payload-section-drop version=1 using " +
                "section-authoritative path sections=2 rebuiltPoints=3 dedupedPointCopies=0 rebuiltOrbitSegments=0",
                "[RecordingStore] ReadBinaryTrajectoryFile: recording=payload-section-drop dropped 1 non-monotonic flat " +
                "trajectory point(s) on load (#419-class)");
        }

        // An orbit-only heal over non-monotonic flat points: the heal line and the read line
        // report the point count after the heal and before the drop, then the Warn follows.
        // No committed fixture carries this shape, so the manifest cannot see it.
        [Fact]
        public void OrbitOnlyHealWithDrop_LogsHealThenReadThenWarn_WithPreDropCounts()
        {
            string path = WriteHealDropPrec();
            TrajectorySidecarProbe probe = Probe(path);

            var rec = new Recording { RecordingId = "payload-heal-drop", SidecarEpoch = 4 };
            object pointsBefore = rec.Points;
            object orbitsBefore = rec.OrbitSegments;
            logLines.Clear();
            TrajectorySidecarBinary.Read(path, rec, probe);

            Assert.Same(pointsBefore, rec.Points);
            Assert.NotSame(orbitsBefore, rec.OrbitSegments);
            Assert.Single(rec.OrbitSegments);
            Assert.Equal(2, rec.Points.Count);
            Assert.False(rec.FilesDirty);
            AssertLogSequence(
                "[RecordingStore] TryHealMalformedFlatFallbackTrajectory: recording=payload-heal-drop healed in memory " +
                "only, sidecar left byte-identical (loadTimeHealKeepsSidecarEpoch) points=3 orbitSegments=1 sidecarEpoch=4",
                "[RecordingStore] ReadBinaryTrajectoryFile: recording=payload-heal-drop version=1 used flat fallback path " +
                "healed=true prePoints=3 postPoints=3 preOrbitSegments=2 postOrbitSegments=1 trackSections=2",
                "[RecordingStore] ReadBinaryTrajectoryFile: recording=payload-heal-drop dropped 1 non-monotonic flat " +
                "trajectory point(s) on load (#419-class)");

            var payload = new RecordingTrajectoryPayload();
            TrajectorySidecarBinary.Decode(path, probe, payload);
            Assert.True(payload.Stats.HealedFlatFallback);
            Assert.Equal(3, payload.Stats.PreHealPointCount);
            Assert.Equal(2, payload.Stats.PreHealOrbitSegmentCount);
            Assert.Equal(1, payload.Stats.DroppedNonMonotonicPoints);
            Assert.Equal(DumpLists(payload), DumpLists(rec));
        }

        // ------------------------------------------------------------------ validate-only read

        // TryValidatePayload decodes into a scratch payload: no Recording, so neither the
        // heal line nor the read summary is written; the drop Warn alone, carrying the
        // probe's recording id (the save gate has already matched it to the caller's).
        [Theory]
        [InlineData("flat")]
        [InlineData("section")]
        [InlineData("heal")]
        public void ValidatePayload_WithADrop_LogsOnlyTheWarn_CarryingTheProbesId(string shape)
        {
            string path = WriteDropPrec(shape, out string id);
            TrajectorySidecarProbe probe = Probe(path);
            Assert.Equal(id, probe.RecordingId);

            logLines.Clear();
            Assert.True(TrajectorySidecarBinary.TryValidatePayload(path, probe, out string reason));
            Assert.Null(reason);

            AssertLogSequence(
                "[WARN][RecordingStore] ReadBinaryTrajectoryFile: recording=" + id + " dropped 1 non-monotonic " +
                "flat trajectory point(s) on load (#419-class)");
        }

        [Fact]
        public void ValidatePayload_WithNoDrop_LogsNothing()
        {
            var written = new Recording { RecordingId = "payload-validate-clean" };
            written.Points.Add(MakePoint(1.0, 0));
            written.Points.Add(MakePoint(2.0, 1));
            string path = WritePrec(written, sidecarEpoch: 2);
            TrajectorySidecarProbe probe = Probe(path);

            logLines.Clear();
            Assert.True(TrajectorySidecarBinary.TryValidatePayload(path, probe, out string reason));
            Assert.Null(reason);
            AssertLogSequence();
        }

        // The drop Warn ignores RecordingStore.SuppressLogging on both paths, as Read's always
        // did; the read summary stays gated.
        [Fact]
        public void DropWarn_IsWrittenUnderRecordingStoreSuppressLogging_ByReadAndValidate()
        {
            string path = WriteFlatDropPrec();
            TrajectorySidecarProbe probe = Probe(path);
            const string warn =
                "[WARN][RecordingStore] ReadBinaryTrajectoryFile: recording=payload-flat-drop dropped 1 " +
                "non-monotonic flat trajectory point(s) on load (#419-class)";

            RecordingStore.SuppressLogging = true;
            logLines.Clear();
            TrajectorySidecarBinary.Read(path, new Recording { RecordingId = "payload-flat-drop" }, probe);
            AssertLogSequence(warn);

            logLines.Clear();
            Assert.True(TrajectorySidecarBinary.TryValidatePayload(path, probe, out _));
            AssertLogSequence(warn);
        }

        // The save gate, the one production caller: a sidecar with a non-monotonic point is
        // current, and the one line its validation writes is the Warn with the recording's id.
        [Fact]
        public void SaveGate_OnADroppingSidecar_IsCurrent_AndWarnsOnceWithTheRecordingsId()
        {
            var written = new Recording
            {
                RecordingId = "payload-gate-drop",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                SidecarEpoch = 5,
            };
            written.Points.Add(MakePoint(1.0, 0));
            written.Points.Add(MakePoint(5.0, 1));
            written.Points.Add(MakePoint(2.0, 2));
            string path = WritePrec(written, sidecarEpoch: 5);

            logLines.Clear();
            bool current = RecordingSidecarStore.AreRecordingFilesCurrentAtPaths(
                written, path,
                Path.Combine(tempDir, "absent_vessel.craft"),
                Path.Combine(tempDir, "absent_ghost.craft"),
                out string reason);

            Assert.True(current, "reason=" + (reason ?? "<null>"));
            Assert.Null(reason);
            var readLines = logLines.Where(l =>
                l.Contains("ReadBinaryTrajectoryFile") || l.Contains("TryHealMalformedFlatFallbackTrajectory")).ToList();
            Assert.True(readLines.Count == 1, "got:\n" + string.Join("\n", readLines));
            Assert.Contains(
                "[WARN][RecordingStore] ReadBinaryTrajectoryFile: recording=payload-gate-drop dropped 1 non-monotonic " +
                "flat trajectory point(s) on load (#419-class)",
                readLines[0]);
        }

        [Fact]
        public void Decode_RefusesAnUnsupportedProbe_AndANullTarget()
        {
            var unsupported = new TrajectorySidecarProbe { Success = true, Supported = false };
            Assert.Throws<InvalidOperationException>(() =>
                TrajectorySidecarBinary.Decode("unused", unsupported, new RecordingTrajectoryPayload()));
            var supported = new TrajectorySidecarProbe { Success = true, Supported = true };
            Assert.Throws<ArgumentNullException>(() => TrajectorySidecarBinary.Decode("unused", supported, null));
            Assert.Throws<ArgumentNullException>(() => TrajectorySidecarBinary.Read("unused", null, supported));
        }

        // ------------------------------------------------------------------ helpers

        private void AssertLogSequence(params string[] expected)
        {
            var read = logLines.Where(l => l.Contains("[RecordingStore]")).ToList();
            Assert.True(read.Count == expected.Length,
                "expected " + expected.Length + " RecordingStore lines, got:\n" + string.Join("\n", read));
            for (int i = 0; i < expected.Length; i++)
                Assert.True(read[i].Contains(expected[i]), "line " + i + " was:\n" + read[i] + "\nexpected:\n" + expected[i]);
        }

        private string WriteDropPrec(string shape, out string id)
        {
            switch (shape)
            {
                case "flat": id = "payload-flat-drop"; return WriteFlatDropPrec();
                case "section": id = "payload-section-drop"; return WriteSectionDropPrec();
                case "heal": id = "payload-heal-drop"; return WriteHealDropPrec();
                default: throw new ArgumentException(shape);
            }
        }

        // Flat path, three points, the last one non-monotonic.
        private string WriteFlatDropPrec()
        {
            var written = new Recording { RecordingId = "payload-flat-drop" };
            written.Points.Add(MakePoint(1.0, 0));
            written.Points.Add(MakePoint(5.0, 1));
            written.Points.Add(MakePoint(2.0, 2));
            return WritePrec(written, sidecarEpoch: 3);
        }

        // Section-authoritative, whose rebuilt flat list holds one non-monotonic point.
        private string WriteSectionDropPrec()
        {
            var written = new Recording { RecordingId = "payload-section-drop" };
            TrajectoryPoint a = MakePoint(1.0, 0), b = MakePoint(3.0, 1), c = MakePoint(2.0, 2);
            written.TrackSections.Add(AbsoluteSection(a, b));
            written.TrackSections.Add(AbsoluteSection(c));
            written.Points.AddRange(new[] { a, b, c });
            Assert.True(TrajectoryTextSidecarCodec.ShouldWriteSectionAuthoritativeTrajectory(written));
            return WritePrec(written, sidecarEpoch: 1);
        }

        // Flat path whose orbit list the heal replaces, over non-monotonic flat points.
        private string WriteHealDropPrec()
        {
            var written = new Recording { RecordingId = "payload-heal-drop", SidecarEpoch = 4 };
            TrajectoryPoint p1 = MakePoint(1.0, 0), p2 = MakePoint(2.0, 1), p5 = MakePoint(5.0, 2);
            OrbitSegment segA = MakeSegment(100.0, 200.0, isPredicted: false);
            // Predicted first, so the write-path checkpoint Ensure promotes nothing and the
            // flat orbit list reaches the file as built.
            OrbitSegment segX = MakeSegment(50.0, 90.0, isPredicted: true);
            written.TrackSections.Add(AbsoluteSection(p1, p2));
            written.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = segA.startUT,
                endUT = segA.endUT,
                source = TrackSectionSource.Checkpoint,
                checkpoints = new List<OrbitSegment> { segA },
            });
            written.Points.AddRange(new[] { p1, p5, p2 });
            written.OrbitSegments.AddRange(new[] { segX, segA });
            return WritePrec(written, sidecarEpoch: 4);
        }

        private string WritePrec(Recording rec, int sidecarEpoch)
        {
            string path = Path.Combine(tempDir, rec.RecordingId + ".prec");
            TrajectorySidecarBinary.Write(path, rec, sidecarEpoch);
            return path;
        }

        private static TrajectorySidecarProbe Probe(string path)
        {
            TrajectorySidecarProbe probe;
            Assert.True(RecordingSidecarStore.TryProbeTrajectorySidecar(path, out probe));
            Assert.True(probe.Supported);
            return probe;
        }

        private static int HeaderLength(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(fs, Encoding.UTF8))
            {
                fs.Position += 4;
                reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadString();
                return (int)fs.Position;
            }
        }

        private static object[] Lists(Recording rec)
        {
            return new object[]
            {
                rec.Points, rec.OrbitSegments, rec.PartEvents, rec.FlagEvents, rec.SegmentEvents, rec.TrackSections,
            };
        }

        private static TrackSection AbsoluteSection(params TrajectoryPoint[] frames)
        {
            return new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = frames[0].ut,
                endUT = frames[frames.Length - 1].ut,
                source = TrackSectionSource.Active,
                frames = new List<TrajectoryPoint>(frames),
                checkpoints = new List<OrbitSegment>(),
            };
        }

        private static TrajectoryPoint MakePoint(double ut, int index)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = 0.1 + (index * 0.01),
                longitude = -74.0 + (index * 0.02),
                altitude = 1000.0 + (index * 25.0),
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(1f + index, 2f + index, 3f + index),
                bodyName = "Kerbin",
            };
        }

        private static OrbitSegment MakeSegment(double startUT, double endUT, bool isPredicted)
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

        // A full structural dump: every instance field of every element, nulls and default
        // values included (unlike the manifest's dump), doubles and floats as bit patterns.
        private static string DumpLists(RecordingTrajectoryPayload p)
        {
            return DumpLists(p.Points, p.OrbitSegments, p.PartEvents, p.FlagEvents, p.SegmentEvents, p.TrackSections);
        }

        private static string DumpLists(Recording r)
        {
            return DumpLists(r.Points, r.OrbitSegments, r.PartEvents, r.FlagEvents, r.SegmentEvents, r.TrackSections);
        }

        private static string DumpLists(params object[] lists)
        {
            var sb = new StringBuilder();
            string[] names = { "Points", "OrbitSegments", "PartEvents", "FlagEvents", "SegmentEvents", "TrackSections" };
            for (int i = 0; i < lists.Length; i++)
                Dump(sb, names[i], lists[i], 0);
            return sb.ToString();
        }

        private static void Dump(StringBuilder sb, string path, object value, int depth)
        {
            var ic = CultureInfo.InvariantCulture;
            if (depth > 16)
                throw new NotSupportedException("dump too deep at " + path);
            if (value == null)
            {
                sb.Append(path).Append("=null\n");
                return;
            }

            Type t = value.GetType();
            if (t == typeof(double))
            {
                sb.Append(path).Append("=d:").Append(BitConverter.DoubleToInt64Bits((double)value).ToString("X16", ic)).Append('\n');
                return;
            }
            if (t == typeof(float))
            {
                sb.Append(path).Append("=f:")
                  .Append(BitConverter.ToInt32(BitConverter.GetBytes((float)value), 0).ToString("X8", ic)).Append('\n');
                return;
            }
            if (t == typeof(string))
            {
                sb.Append(path).Append("=s:").Append(((string)value).Replace("\\", "\\\\").Replace("\n", "\\n")).Append('\n');
                return;
            }
            if (t.IsPrimitive || t.IsEnum)
            {
                sb.Append(path).Append('=').Append(Convert.ToString(t.IsEnum ? Convert.ToInt64(value, ic) : value, ic)).Append('\n');
                return;
            }
            if (value is IList list)
            {
                sb.Append(path).Append(".count=").Append(list.Count.ToString(ic)).Append('\n');
                for (int i = 0; i < list.Count; i++)
                    Dump(sb, path + "[" + i.ToString(ic) + "]", list[i], depth + 1);
                return;
            }

            bool modelled = t.Assembly == typeof(Recording).Assembly
                            || (t.IsValueType && t.Namespace == "UnityEngine");
            if (!modelled)
                throw new NotSupportedException("dump has no rule for " + t.FullName + " at " + path);

            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .OrderBy(f => f.Name, StringComparer.Ordinal))
                Dump(sb, path + "." + f.Name, f.GetValue(value), depth + 1);
        }
    }
}
