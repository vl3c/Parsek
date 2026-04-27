using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Parsek.Tests.Generators;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RecordingStorageRoundTripTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public RecordingStorageRoundTripTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek-storage-fixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        public static IEnumerable<object[]> RepresentativeCases()
        {
            return RecordingStorageFixtures.RepresentativeCases();
        }

        [Theory]
        [MemberData(nameof(RepresentativeCases))]
        public void RepresentativeFixture_RoundTripsThroughRecordingStore(
            RecordingStorageFixtures.FixtureCase fixture)
        {
            Recording original = RecordingStorageFixtures.MaterializeTrajectory(fixture.Builder);
            Recording restored = RoundTrip(original);

            AssertSemanticTrajectoryEqual(original, restored);
            AssertRecordingStatsEqual(
                TrajectoryMath.ComputeStats(original, LookupBody),
                TrajectoryMath.ComputeStats(restored, LookupBody));
        }

        [Theory]
        [MemberData(nameof(RepresentativeCases))]
        public void ScenarioWriter_WritesExpectedSidecars_ForRepresentativeFixture(
            RecordingStorageFixtures.FixtureCase fixture)
        {
            var writer = new ScenarioWriter()
                .WithV3Format()
                .AddRecordingAsTree(fixture.Builder);

            writer.WriteSidecarFiles(tempDir);

            string recordingsDir = Path.Combine(tempDir, "Parsek", "Recordings");
            string recordingId = fixture.Builder.GetRecordingId();
            string precPath = Path.Combine(recordingsDir, recordingId + ".prec");
            string vesselPath = Path.Combine(recordingsDir, recordingId + "_vessel.craft");
            string ghostPath = Path.Combine(recordingsDir, recordingId + "_ghost.craft");
            string readablePrecPath = Path.Combine(recordingsDir, recordingId + ".prec.txt");
            string readableVesselPath = Path.Combine(recordingsDir, recordingId + "_vessel.craft.txt");
            string readableGhostPath = Path.Combine(recordingsDir, recordingId + "_ghost.craft.txt");

            Assert.True(File.Exists(precPath), $"Expected trajectory sidecar for {fixture.Name}");
            Assert.True(File.Exists(readablePrecPath), $"Expected readable trajectory mirror for {fixture.Name}");

            var original = RecordingStorageFixtures.MaterializeTrajectory(fixture.Builder);
            var loadedTrajectory = new Recording { RecordingId = recordingId };
            Assert.True(RecordingStore.LoadTrajectorySidecarForTesting(precPath, loadedTrajectory));
            AssertSemanticTrajectoryEqual(original, loadedTrajectory);

            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(precPath, out probe));
            Assert.Equal(fixture.Builder.GetFormatVersion(), probe.FormatVersion);
            Assert.Equal(
                fixture.Builder.GetFormatVersion() >= 3
                    ? TrajectorySidecarEncoding.BinaryV3
                    : fixture.Builder.GetFormatVersion() >= 2
                        ? TrajectorySidecarEncoding.BinaryV2
                    : TrajectorySidecarEncoding.TextConfigNode,
                probe.Encoding);

            if (probe.Encoding == TrajectorySidecarEncoding.TextConfigNode)
            {
                ConfigNode expectedPrecNode = new ConfigNode("PARSEK_RECORDING");
                expectedPrecNode.AddValue("version",
                    fixture.Builder.GetFormatVersion().ToString(System.Globalization.CultureInfo.InvariantCulture));
                expectedPrecNode.AddValue("recordingId", recordingId);
                RecordingStore.SerializeTrajectoryInto(expectedPrecNode, original);
                AssertConfigNodeEquivalent(expectedPrecNode, probe.LegacyNode, "prec");
            }

            ConfigNode expectedVessel = fixture.Builder.GetVesselSnapshot();
            Assert.Equal(expectedVessel != null, File.Exists(vesselPath));
            if (expectedVessel != null)
            {
                SnapshotSidecarProbe vesselProbe;
                Assert.True(RecordingStore.TryProbeSnapshotSidecar(vesselPath, out vesselProbe));
                Assert.Equal(SnapshotSidecarEncoding.DeflateV1, vesselProbe.Encoding);

                ConfigNode loadedVessel;
                Assert.True(RecordingStore.LoadSnapshotSidecarForTesting(vesselPath, out loadedVessel));
                Assert.NotNull(loadedVessel);
                AssertConfigNodeEquivalent(expectedVessel, loadedVessel, "vessel");
            }

            GhostSnapshotMode ghostSnapshotMode = RecordingStore.DetermineGhostSnapshotMode(original);
            Assert.Equal(ghostSnapshotMode == GhostSnapshotMode.Separate, File.Exists(ghostPath));
            if (ghostSnapshotMode == GhostSnapshotMode.Separate)
            {
                ConfigNode expectedGhost = original.GhostVisualSnapshot;
                SnapshotSidecarProbe ghostProbe;
                Assert.True(RecordingStore.TryProbeSnapshotSidecar(ghostPath, out ghostProbe));
                Assert.Equal(SnapshotSidecarEncoding.DeflateV1, ghostProbe.Encoding);

                ConfigNode loadedGhost;
                Assert.True(RecordingStore.LoadSnapshotSidecarForTesting(ghostPath, out loadedGhost));
                Assert.NotNull(loadedGhost);
                AssertConfigNodeEquivalent(expectedGhost, loadedGhost, "ghost");
            }

            var expectedReadablePrecNode = new ConfigNode("PARSEK_RECORDING");
            expectedReadablePrecNode.AddValue("version",
                fixture.Builder.GetFormatVersion().ToString(System.Globalization.CultureInfo.InvariantCulture));
            expectedReadablePrecNode.AddValue("recordingId", recordingId);
            expectedReadablePrecNode.AddValue("sidecarEpoch", "0");
            RecordingStore.SerializeTrajectoryInto(expectedReadablePrecNode, original);
            AssertConfigNodeEquivalent(expectedReadablePrecNode, ConfigNode.Load(readablePrecPath), "readablePrec");

            ConfigNode expectedReadableVessel = fixture.Builder.GetVesselSnapshot();
            Assert.Equal(expectedReadableVessel != null, File.Exists(readableVesselPath));
            if (expectedReadableVessel != null)
                AssertConfigNodeEquivalent(expectedReadableVessel, ConfigNode.Load(readableVesselPath), "readableVessel");

            Assert.Equal(ghostSnapshotMode == GhostSnapshotMode.Separate, File.Exists(readableGhostPath));
            if (ghostSnapshotMode == GhostSnapshotMode.Separate)
            {
                ConfigNode expectedReadableGhost = original.GhostVisualSnapshot;
                AssertConfigNodeEquivalent(expectedReadableGhost, ConfigNode.Load(readableGhostPath), "readableGhost");
            }
        }

        [Fact]
        public void SerializeTrajectoryInto_BackgroundFixture_LogsSingleSectionSummary()
        {
            logLines.Clear();
            ParsekLog.VerboseOverrideForTesting = true;

            Recording rec = RecordingStorageFixtures.MaterializeTrajectory(
                RecordingStorageFixtures.MixedActiveBackground().Builder);
            logLines.Clear();
            var node = new ConfigNode("TEST");

            RecordingStore.SerializeTrajectoryInto(node, rec);

            var sourceLogs = logLines.Where(l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("SerializeTrackSections") &&
                l.Contains("source=Background")).ToList();

            Assert.Single(sourceLogs);
        }

        [Theory]
        [InlineData("Atmospheric Active Multi-Section", 5, 6)]
        [InlineData("Optimizer Boundary Seed", 5, 6)]
        public void BaselineFixtures_PreserveExpectedOnDiskDuplicationShape(
            string fixtureName, int expectedFlatPointCount, int expectedSectionFrameCount)
        {
            RecordingStorageFixtures.FixtureCase fixture = RepresentativeCases()
                .Select(c => (RecordingStorageFixtures.FixtureCase)c[0])
                .Single(c => c.Name == fixtureName);

            Recording rec = RecordingStorageFixtures.MaterializeTrajectory(fixture.Builder);
            int sectionFrameCount = rec.TrackSections.Sum(ts => ts.frames.Count);

            Assert.Equal(expectedFlatPointCount, rec.Points.Count);
            Assert.Equal(expectedSectionFrameCount, sectionFrameCount);
            Assert.True(sectionFrameCount > rec.Points.Count,
                "Fixture must retain duplicated section frames so slice 2 can prove the on-disk win.");
        }

        [Fact]
        public void CurrentFormatTrajectorySidecar_WritesBinaryHeader_AndRoundTrips()
        {
            Recording original = RecordingStorageFixtures.MaterializeTrajectory(
                RecordingStorageFixtures.MixedActiveBackground().Builder);
            string path = Path.Combine(tempDir, "current-format.prec");

            logLines.Clear();
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.WriteTrajectorySidecar(path, original, sidecarEpoch: 7);

            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(path, out probe));
            Assert.Equal(TrajectorySidecarEncoding.BinaryV3, probe.Encoding);
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, probe.FormatVersion);
            Assert.Equal(7, probe.SidecarEpoch);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("WriteBinaryTrajectoryFile"));

            var restored = new Recording { RecordingId = original.RecordingId };
            RecordingStore.DeserializeTrajectorySidecar(path, probe, restored);
            AssertSemanticTrajectoryEqual(original, restored);
        }

        [Fact]
        public void TextTrajectorySidecar_PredictedOrbitSegment_RoundTrips()
        {
            var original = new Recording
            {
                RecordingId = "text-predicted-orbit",
                RecordingFormatVersion = 1
            };
            original.OrbitSegments.Add(MakeOrbitSegment(100.0, 220.0, isPredicted: true));

            string path = Path.Combine(tempDir, "text-predicted-orbit.prec");
            RecordingStore.WriteTrajectorySidecar(path, original, sidecarEpoch: 2);

            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(path, out probe));
            Assert.Equal(TrajectorySidecarEncoding.TextConfigNode, probe.Encoding);
            Assert.Equal(1, probe.FormatVersion);

            var restored = new Recording
            {
                RecordingId = original.RecordingId,
                RecordingFormatVersion = original.RecordingFormatVersion
            };
            RecordingStore.DeserializeTrajectorySidecar(path, probe, restored);

            AssertSemanticTrajectoryEqual(original, restored);
            Assert.True(restored.OrbitSegments[0].isPredicted);
        }

        [Fact]
        public void TextTrajectorySidecar_LegacyOrbitSegmentWithoutPredictedFlag_DefaultsFalse()
        {
            var node = new ConfigNode("PARSEK_RECORDING");
            node.AddValue("version", "1");
            node.AddValue("recordingId", "legacy-text-predicted");
            var segNode = node.AddNode("ORBIT_SEGMENT");
            segNode.AddValue("startUT", "100");
            segNode.AddValue("endUT", "220");
            segNode.AddValue("inc", "28.5");
            segNode.AddValue("ecc", "0.01");
            segNode.AddValue("sma", "700000");
            segNode.AddValue("lan", "90");
            segNode.AddValue("argPe", "45");
            segNode.AddValue("mna", "0.2");
            segNode.AddValue("epoch", "100");
            segNode.AddValue("body", "Kerbin");

            var restored = new Recording
            {
                RecordingId = "legacy-text-predicted",
                RecordingFormatVersion = 1
            };
            RecordingStore.DeserializeTrajectoryFrom(node, restored);

            Assert.Single(restored.OrbitSegments);
            Assert.False(restored.OrbitSegments[0].isPredicted);
        }

        [Fact]
        public void CurrentFormatTrajectorySidecar_PredictedCheckpoint_RoundTripsThroughSectionAuthoritativeBinary()
        {
            var original = BuildSectionAuthoritativeCodecFixture();
            original.RecordingId = "section-authoritative-predicted";
            original.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;

            var predictedFlat = original.OrbitSegments[1];
            predictedFlat.isPredicted = true;
            original.OrbitSegments[1] = predictedFlat;

            for (int t = 0; t < original.TrackSections.Count; t++)
            {
                if (original.TrackSections[t].referenceFrame != ReferenceFrame.OrbitalCheckpoint)
                    continue;

                var track = original.TrackSections[t];
                var predictedCheckpoint = track.checkpoints[1];
                predictedCheckpoint.isPredicted = true;
                track.checkpoints[1] = predictedCheckpoint;
                original.TrackSections[t] = track;
            }

            Assert.True(RecordingStore.ShouldWriteSectionAuthoritativeTrajectory(original));

            string path = Path.Combine(tempDir, "section-authoritative-predicted.prec");
            logLines.Clear();
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.WriteTrajectorySidecar(path, original, sidecarEpoch: 3);

            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(path, out probe));
            Assert.Equal(TrajectorySidecarEncoding.BinaryV3, probe.Encoding);
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, probe.FormatVersion);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("WriteBinaryTrajectoryFile") &&
                l.Contains("sectionAuthoritative=True") &&
                l.Contains("predictedOrbitSegments=1") &&
                l.Contains("predictedCheckpoints=1"));

            var restored = new Recording { RecordingId = original.RecordingId };
            logLines.Clear();
            RecordingStore.DeserializeTrajectorySidecar(path, probe, restored);

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("ReadBinaryTrajectoryFile") &&
                l.Contains("using section-authoritative path"));
            AssertSemanticTrajectoryEqual(original, restored);
            Assert.False(restored.OrbitSegments[0].isPredicted);
            Assert.True(restored.OrbitSegments[1].isPredicted);
        }

        [Fact]
        public void CurrentFormatTrajectorySidecar_PredictedTailBeyondTrackSections_FallsBackToFlatBinaryAndRoundTrips()
        {
            var original = BuildSectionAuthoritativeCodecFixture();
            original.RecordingId = "predicted-tail-fallback";
            original.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            double appendedStartUT = original.OrbitSegments[original.OrbitSegments.Count - 1].endUT;
            original.OrbitSegments.Add(MakeOrbitSegment(
                appendedStartUT,
                appendedStartUT + 300.0,
                isPredicted: true));

            Assert.True(RecordingStore.FlatTrajectoryExtendsTrackSectionPayload(
                original, original.TrackSections, allowRelativeSections: true));
            Assert.False(RecordingStore.ShouldWriteSectionAuthoritativeTrajectory(original));

            string path = Path.Combine(tempDir, "predicted-tail-fallback.prec");
            logLines.Clear();
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.WriteTrajectorySidecar(path, original, sidecarEpoch: 4);

            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(path, out probe));
            Assert.Equal(TrajectorySidecarEncoding.BinaryV3, probe.Encoding);
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, probe.FormatVersion);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("WriteBinaryTrajectoryFile") &&
                l.Contains("sectionAuthoritative=False"));

            var restored = new Recording { RecordingId = original.RecordingId };
            logLines.Clear();
            RecordingStore.DeserializeTrajectorySidecar(path, probe, restored);

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("ReadBinaryTrajectoryFile") &&
                l.Contains("used flat fallback path"));
            AssertSemanticTrajectoryEqual(original, restored);
            Assert.True(restored.OrbitSegments[restored.OrbitSegments.Count - 1].isPredicted);
        }

        [Fact]
        public void V4BinaryTrajectorySidecar_WithPredictedSegments_UpgradesToV5AndPreservesFlag()
        {
            var legacy = new Recording
            {
                RecordingId = "legacy-v4-predicted",
                RecordingFormatVersion = RecordingStore.LaunchToLaunchLoopIntervalFormatVersion
            };
            legacy.OrbitSegments.Add(MakeOrbitSegment(100.0, 220.0, isPredicted: true));

            string path = Path.Combine(tempDir, "legacy-v4-predicted.prec");
            logLines.Clear();
            RecordingStore.WriteTrajectorySidecar(path, legacy, sidecarEpoch: 1);

            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(path, out probe));
            Assert.Equal(TrajectorySidecarEncoding.BinaryV3, probe.Encoding);
            Assert.Equal(RecordingStore.PredictedOrbitSegmentFormatVersion, legacy.RecordingFormatVersion);
            Assert.Equal(RecordingStore.PredictedOrbitSegmentFormatVersion, probe.FormatVersion);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[RecordingStore]") &&
                l.Contains("NormalizeRecordingFormatVersionForPredictedSegments") &&
                l.Contains("recording=legacy-v4-predicted") &&
                l.Contains("version=4->5") &&
                l.Contains("predictedOrbitSegments=1") &&
                l.Contains("predictedCheckpoints=0"));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("WriteBinaryTrajectoryFile") &&
                l.Contains("predictedOrbitSegments=1") &&
                l.Contains("predictedCheckpoints=0"));

            var restored = new Recording { RecordingId = legacy.RecordingId };
            RecordingStore.DeserializeTrajectorySidecar(path, probe, restored);

            Assert.Single(restored.OrbitSegments);
            Assert.True(restored.OrbitSegments[0].isPredicted);
            Assert.Equal(legacy.OrbitSegments[0].startUT, restored.OrbitSegments[0].startUT);
            Assert.Equal(legacy.OrbitSegments[0].orbitalFrameRotation.x, restored.OrbitSegments[0].orbitalFrameRotation.x);
            Assert.Equal(legacy.OrbitSegments[0].angularVelocity.z, restored.OrbitSegments[0].angularVelocity.z);
        }

        [Fact]
        public void CurrentFormatTrajectorySidecar_MixedBackgroundCheckpointFallback_RoundTripsWithoutDroppingOrbitSegments()
        {
            var original = new Recording
            {
                RecordingId = "bg-mixed-fallback",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                VesselName = "Background Booster"
            };
            original.Points.Add(new TrajectoryPoint
            {
                ut = 100,
                latitude = 0,
                longitude = 0,
                altitude = 1000,
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = new Vector3(0, 120, 0),
                bodyName = "Kerbin"
            });
            original.Points.Add(new TrajectoryPoint
            {
                ut = 110,
                latitude = 0.01,
                longitude = 0.02,
                altitude = 1500,
                rotation = new Quaternion(0, 0.1f, 0, 0.99f),
                velocity = new Vector3(0, 200, 0),
                bodyName = "Kerbin"
            });
            original.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 110,
                endUT = 500,
                inclination = 28.5,
                eccentricity = 0.01,
                semiMajorAxis = 705000,
                longitudeOfAscendingNode = 90,
                argumentOfPeriapsis = 45,
                meanAnomalyAtEpoch = 0.2,
                epoch = 110,
                bodyName = "Kerbin"
            });
            original.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100,
                endUT = 110,
                source = TrackSectionSource.Background,
                frames = new List<TrajectoryPoint> { original.Points[0], original.Points[1] },
                checkpoints = new List<OrbitSegment>()
            });
            original.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 110,
                endUT = 500,
                source = TrackSectionSource.Checkpoint,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>()
            });

            string path = Path.Combine(tempDir, "bg-mixed-fallback.prec");
            logLines.Clear();
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.WriteTrajectorySidecar(path, original, sidecarEpoch: 5);

            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(path, out probe));
            Assert.Equal(TrajectorySidecarEncoding.BinaryV3, probe.Encoding);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("WriteBinaryTrajectoryFile") &&
                l.Contains("sectionAuthoritative=False"));

            var restored = new Recording { RecordingId = original.RecordingId };
            logLines.Clear();
            RecordingStore.DeserializeTrajectorySidecar(path, probe, restored);

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("ReadBinaryTrajectoryFile") &&
                l.Contains("used flat fallback path"));
            AssertSemanticTrajectoryEqual(original, restored);
        }

        [Fact]
        public void CurrentFormatTrajectorySidecar_RecorderLoadedToOnRailsFallback_RoundTripsWithoutDroppingOrbitSegments()
        {
            var original = BuildRecorderLoadedToOnRailsFallbackRecording();

            string path = Path.Combine(tempDir, "bg-recorder-loaded-onrails-fallback.prec");
            logLines.Clear();
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.WriteTrajectorySidecar(path, original, sidecarEpoch: 6);

            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(path, out probe));
            Assert.Equal(TrajectorySidecarEncoding.BinaryV3, probe.Encoding);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("WriteBinaryTrajectoryFile") &&
                l.Contains("sectionAuthoritative=False"));

            var restored = new Recording { RecordingId = original.RecordingId };
            logLines.Clear();
            RecordingStore.DeserializeTrajectorySidecar(path, probe, restored);

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("ReadBinaryTrajectoryFile") &&
                l.Contains("used flat fallback path"));
            AssertSemanticTrajectoryEqual(original, restored);
        }

        [Fact]
        public void MixedFormatTrajectorySidecars_LoadInSameProcess()
        {
            var legacy = RecordingStorageFixtures.MaterializeTrajectory(
                RecordingStorageFixtures.AtmosphericActiveMultiSection().Builder);
            legacy.RecordingId = "mixed-v0";
            legacy.RecordingFormatVersion = 0;

            var sectionedText = RecordingStorageFixtures.MaterializeTrajectory(
                RecordingStorageFixtures.OrbitalCheckpointTransition().Builder);
            sectionedText.RecordingId = "mixed-v1";
            sectionedText.RecordingFormatVersion = 1;

            var binary = RecordingStorageFixtures.MaterializeTrajectory(
                RecordingStorageFixtures.MixedActiveBackground().Builder);
            binary.RecordingId = "mixed-v2";
            binary.RecordingFormatVersion = 2;

            var sparseBinary = RecordingStorageFixtures.MaterializeTrajectory(
                RecordingStorageFixtures.AtmosphericActiveMultiSection().Builder);
            sparseBinary.RecordingId = "mixed-v3";
            sparseBinary.RecordingFormatVersion = 3;

            var cases = new[]
            {
                new { Original = legacy, Path = Path.Combine(tempDir, "mixed-v0.prec"), ExpectedEncoding = TrajectorySidecarEncoding.TextConfigNode },
                new { Original = sectionedText, Path = Path.Combine(tempDir, "mixed-v1.prec"), ExpectedEncoding = TrajectorySidecarEncoding.TextConfigNode },
                new { Original = binary, Path = Path.Combine(tempDir, "mixed-v2.prec"), ExpectedEncoding = TrajectorySidecarEncoding.BinaryV2 },
                new { Original = sparseBinary, Path = Path.Combine(tempDir, "mixed-v3.prec"), ExpectedEncoding = TrajectorySidecarEncoding.BinaryV3 }
            };

            for (int i = 0; i < cases.Length; i++)
            {
                RecordingStore.WriteTrajectorySidecar(cases[i].Path, cases[i].Original, sidecarEpoch: i + 1);

                TrajectorySidecarProbe probe;
                Assert.True(RecordingStore.TryProbeTrajectorySidecar(cases[i].Path, out probe));
                Assert.Equal(cases[i].ExpectedEncoding, probe.Encoding);
                Assert.Equal(cases[i].Original.RecordingFormatVersion, probe.FormatVersion);

                var restored = new Recording { RecordingId = cases[i].Original.RecordingId };
                RecordingStore.DeserializeTrajectorySidecar(cases[i].Path, probe, restored);
                AssertSemanticTrajectoryEqual(cases[i].Original, restored);
            }
        }

        [Fact]
        public void SnapshotSidecar_LegacyTextFormat_StillLoads()
        {
            ConfigNode expected = BuildSnapshot("Legacy Snapshot", pid: 1001);
            string path = Path.Combine(tempDir, "legacy_vessel.craft");
            expected.Save(path);

            SnapshotSidecarProbe probe;
            Assert.True(RecordingStore.TryProbeSnapshotSidecar(path, out probe));
            Assert.Equal(SnapshotSidecarEncoding.TextConfigNode, probe.Encoding);
            Assert.True(probe.Supported);

            ConfigNode loaded;
            Assert.True(RecordingStore.LoadSnapshotSidecarForTesting(path, out loaded));
            AssertConfigNodeEquivalent(expected, loaded, "legacySnapshot");
        }

        [Fact]
        public void SnapshotSidecar_CompressedFormat_UsesOptimalCompression_AndRoundTrips()
        {
            Assert.Equal(CompressionLevel.Optimal, SnapshotSidecarCodec.CurrentCompressionLevel);

            ConfigNode expected = VesselSnapshotBuilder.FleaRocket("Compressed Snapshot", "Jebediah Kerman", pid: 2002)
                .Build();
            string path = Path.Combine(tempDir, "compressed_vessel.craft");

            RecordingStore.WriteSnapshotSidecarForTesting(path, expected);

            SnapshotSidecarProbe probe;
            Assert.True(RecordingStore.TryProbeSnapshotSidecar(path, out probe));
            Assert.Equal(SnapshotSidecarEncoding.DeflateV1, probe.Encoding);
            Assert.Equal(SnapshotSidecarCodec.CurrentVersion, probe.FormatVersion);
            Assert.True(probe.CompressedLength > 0);
            Assert.True(probe.UncompressedLength > probe.CompressedLength,
                $"Expected compressed sidecar ({probe.CompressedLength} bytes) to be smaller than uncompressed payload ({probe.UncompressedLength} bytes)");

            ConfigNode loaded;
            Assert.True(RecordingStore.LoadSnapshotSidecarForTesting(path, out loaded));
            AssertConfigNodeEquivalent(expected, loaded, "compressedSnapshot");
        }

        [Fact]
        public void SnapshotSidecar_MixedLegacyAndCompressedFiles_LoadInSameProcess()
        {
            ConfigNode legacy = BuildSnapshot("Legacy Mixed", pid: 3001);
            ConfigNode compressed = VesselSnapshotBuilder.FleaRocket("Compressed Mixed", "Valentina Kerman", pid: 3002)
                .Build();

            string legacyPath = Path.Combine(tempDir, "mixed-legacy_vessel.craft");
            string compressedPath = Path.Combine(tempDir, "mixed-compressed_vessel.craft");

            legacy.Save(legacyPath);
            RecordingStore.WriteSnapshotSidecarForTesting(compressedPath, compressed);

            ConfigNode loadedLegacy;
            ConfigNode loadedCompressed;
            Assert.True(RecordingStore.LoadSnapshotSidecarForTesting(legacyPath, out loadedLegacy));
            Assert.True(RecordingStore.LoadSnapshotSidecarForTesting(compressedPath, out loadedCompressed));
            AssertConfigNodeEquivalent(legacy, loadedLegacy, "mixedLegacy");
            AssertConfigNodeEquivalent(compressed, loadedCompressed, "mixedCompressed");
        }

        [Fact]
        public void SnapshotSidecar_CompressedFormat_IsSmallerThanEquivalentLegacyText()
        {
            ConfigNode expected = VesselSnapshotBuilder.FleaRocket("Snapshot Size", "Bill Kerman", pid: 4004)
                .Build();
            string legacyPath = Path.Combine(tempDir, "snapshot-size-legacy_vessel.craft");
            string compressedPath = Path.Combine(tempDir, "snapshot-size-compressed_vessel.craft");

            expected.Save(legacyPath);
            RecordingStore.WriteSnapshotSidecarForTesting(compressedPath, expected);

            long legacyBytes = new FileInfo(legacyPath).Length;
            long compressedBytes = new FileInfo(compressedPath).Length;

            Assert.True(compressedBytes < legacyBytes,
                $"Expected compressed snapshot ({compressedBytes} bytes) to be smaller than legacy text ({legacyBytes} bytes)");
        }

        [Fact]
        public void UnsupportedBinaryTrajectoryVersion_IsRejected()
        {
            string path = Path.Combine(tempDir, "unsupported-binary.prec");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new byte[] { (byte)'P', (byte)'R', (byte)'K', (byte)'B' });
                writer.Write(99);
                writer.Write(0);
                writer.Write("unsupported");
            }

            logLines.Clear();
            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(path, out probe));
            Assert.False(probe.Supported);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[RecordingStore]") &&
                l.Contains("unsupported binary trajectory version"));
        }

        [Fact]
        public void UnsupportedSnapshotSidecarVersion_IsRejected()
        {
            string path = Path.Combine(tempDir, "unsupported_vessel.craft");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new byte[] { (byte)'P', (byte)'R', (byte)'K', (byte)'S' });
                writer.Write(99);
                writer.Write((byte)1);
                writer.Write(16);
                writer.Write(4);
                writer.Write(0u);
                writer.Write(new byte[] { 1, 2, 3, 4 });
            }

            logLines.Clear();
            SnapshotSidecarProbe probe;
            Assert.True(RecordingStore.TryProbeSnapshotSidecar(path, out probe));
            Assert.False(probe.Supported);
            Assert.Equal(SnapshotSidecarEncoding.UnknownBinary, probe.Encoding);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[RecordingStore]") &&
                l.Contains("unsupported snapshot sidecar"));
        }

        [Fact]
        public void SnapshotLoad_AliasMode_RecoversFromGhostSidecar_WhenVesselSidecarMissing()
        {
            ConfigNode ghost = BuildSnapshot("Alias Recovery", pid: 5001);
            string vesselPath = Path.Combine(tempDir, "alias-missing_vessel.craft");
            string ghostPath = Path.Combine(tempDir, "alias-recovery_ghost.craft");
            RecordingStore.WriteSnapshotSidecarForTesting(ghostPath, ghost);

            var rec = new Recording
            {
                RecordingId = "alias-recovery",
                GhostSnapshotMode = GhostSnapshotMode.AliasVessel
            };

            logLines.Clear();
            RecordingStore.LoadSnapshotSidecarsFromPaths(rec, vesselPath, ghostPath);

            Assert.NotNull(rec.VesselSnapshot);
            Assert.NotNull(rec.GhostVisualSnapshot);
            AssertConfigNodeEquivalent(ghost, rec.VesselSnapshot, "aliasRecoveredVessel");
            AssertConfigNodeEquivalent(ghost, rec.GhostVisualSnapshot, "aliasRecoveredGhost");
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[RecordingStore]") &&
                l.Contains("missing vessel snapshot, recovered from ghost sidecar"));
        }

        [Fact]
        public void SnapshotLoad_SeparateMode_FallsBackToVesselSnapshot_WhenGhostSidecarMissing()
        {
            ConfigNode vessel = BuildSnapshot("Separate Fallback", pid: 5002);
            string vesselPath = Path.Combine(tempDir, "separate-fallback_vessel.craft");
            string ghostPath = Path.Combine(tempDir, "separate-fallback_ghost.craft");
            RecordingStore.WriteSnapshotSidecarForTesting(vesselPath, vessel);

            var rec = new Recording
            {
                RecordingId = "separate-fallback",
                GhostSnapshotMode = GhostSnapshotMode.Separate
            };

            logLines.Clear();
            RecordingStore.LoadSnapshotSidecarsFromPaths(rec, vesselPath, ghostPath);

            Assert.NotNull(rec.VesselSnapshot);
            Assert.NotNull(rec.GhostVisualSnapshot);
            AssertConfigNodeEquivalent(vessel, rec.VesselSnapshot, "separateFallbackVessel");
            AssertConfigNodeEquivalent(vessel, rec.GhostVisualSnapshot, "separateFallbackGhost");
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[RecordingStore]") &&
                l.Contains("missing ghost snapshot, fell back to vessel snapshot"));
        }

        [Fact]
        public void SnapshotLoad_GhostOnlyRecording_RemainsSeparate()
        {
            ConfigNode ghost = BuildSnapshot("Ghost Only", pid: 5003);
            string vesselPath = Path.Combine(tempDir, "ghost-only_vessel.craft");
            string ghostPath = Path.Combine(tempDir, "ghost-only_ghost.craft");
            RecordingStore.WriteSnapshotSidecarForTesting(ghostPath, ghost);

            var rec = new Recording
            {
                RecordingId = "ghost-only",
                GhostSnapshotMode = GhostSnapshotMode.Unspecified
            };

            RecordingStore.LoadSnapshotSidecarsFromPaths(rec, vesselPath, ghostPath);

            Assert.Null(rec.VesselSnapshot);
            Assert.NotNull(rec.GhostVisualSnapshot);
            Assert.Equal(GhostSnapshotMode.Separate, rec.GhostSnapshotMode);
            AssertConfigNodeEquivalent(ghost, rec.GhostVisualSnapshot, "ghostOnly");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SnapshotLoad_InvalidCompressedEnvelope_IsRejectedAndLogged(bool truncatePayload)
        {
            ConfigNode vessel = BuildSnapshot("Corrupt Snapshot", pid: 5004);
            string vesselPath = Path.Combine(tempDir,
                truncatePayload ? "invalid-truncated_vessel.craft" : "invalid-corrupt_vessel.craft");
            RecordingStore.WriteSnapshotSidecarForTesting(vesselPath, vessel);

            byte[] bytes = File.ReadAllBytes(vesselPath);
            if (truncatePayload)
            {
                Array.Resize(ref bytes, bytes.Length - 3);
            }
            else
            {
                bytes[bytes.Length - 1] ^= 0x5A;
            }
            File.WriteAllBytes(vesselPath, bytes);

            var rec = new Recording
            {
                RecordingId = truncatePayload ? "invalid-truncated" : "invalid-corrupt",
                GhostSnapshotMode = GhostSnapshotMode.AliasVessel
            };

            logLines.Clear();
            RecordingStore.LoadSnapshotSidecarsFromPaths(rec, vesselPath, ghostPath: null);

            Assert.Null(rec.VesselSnapshot);
            Assert.Null(rec.GhostVisualSnapshot);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[RecordingStore]") &&
                l.Contains("invalid vessel snapshot sidecar"));
        }

        [Fact]
        public void SnapshotSidecar_OversizedLengthField_IsRejectedBeforeAllocation()
        {
            string path = Path.Combine(tempDir, "oversized_vessel.craft");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new byte[] { (byte)'P', (byte)'R', (byte)'K', (byte)'S' });
                writer.Write(SnapshotSidecarCodec.CurrentVersion);
                writer.Write((byte)1);
                writer.Write(SnapshotSidecarCodec.MaxPayloadBytes + 1);
                writer.Write(0);
                writer.Write(0u);
            }

            ConfigNode loaded;
            SnapshotSidecarProbe probe;
            Assert.False(RecordingStore.TryLoadSnapshotSidecar(path, out loaded, out probe));
            Assert.Null(loaded);
            Assert.Equal(
                $"uncompressed payload too large ({SnapshotSidecarCodec.MaxPayloadBytes + 1} bytes)",
                probe.FailureReason);
        }

        [Fact]
        public void LoadRecordingFiles_InvalidVesselSnapshot_MarksSidecarLoadFailure()
        {
            string dir = Path.Combine(tempDir, "snapshot-load-failure");
            Directory.CreateDirectory(dir);

            string precPath = Path.Combine(dir, "snapshot-load-failure.prec");
            string vesselPath = Path.Combine(dir, "snapshot-load-failure_vessel.craft");
            string ghostPath = Path.Combine(dir, "snapshot-load-failure_ghost.craft");

            Recording written = BuildRecordingWithSnapshots(
                "snapshot-load-failure",
                sidecarEpoch: 0,
                vesselName: "Broken Vessel",
                ghostName: "Broken Ghost",
                pidBase: 5100,
                pointUt: 200);
            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                written, precPath, vesselPath, ghostPath, incrementEpoch: true));

            byte[] vesselBytes = File.ReadAllBytes(vesselPath);
            vesselBytes[vesselBytes.Length - 1] ^= 0x5A;
            File.WriteAllBytes(vesselPath, vesselBytes);

            var loaded = new Recording
            {
                RecordingId = written.RecordingId,
                SidecarEpoch = written.SidecarEpoch,
                GhostSnapshotMode = written.GhostSnapshotMode
            };

            logLines.Clear();
            Assert.False(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                loaded, precPath, vesselPath, ghostPath));
            Assert.True(loaded.SidecarLoadFailed);
            Assert.Equal("snapshot-vessel-invalid", loaded.SidecarLoadFailureReason);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[RecordingStore]") &&
                l.Contains("invalid vessel snapshot sidecar"));
        }

        [Theory]
        [InlineData(false, "snapshot-ghost-invalid", "invalid ghost snapshot sidecar")]
        [InlineData(true, "snapshot-ghost-unsupported", "unsupported ghost snapshot sidecar")]
        public void LoadRecordingFiles_BadGhostSnapshot_DoesNotFallBackToVesselSnapshot(
            bool unsupportedVersion, string expectedFailureReason, string expectedLogFragment)
        {
            string dir = Path.Combine(tempDir,
                unsupportedVersion ? "ghost-sidecar-unsupported" : "ghost-sidecar-invalid");
            Directory.CreateDirectory(dir);

            string precPath = Path.Combine(dir, "ghost-sidecar.prec");
            string vesselPath = Path.Combine(dir, "ghost-sidecar_vessel.craft");
            string ghostPath = Path.Combine(dir, "ghost-sidecar_ghost.craft");

            Recording written = BuildRecordingWithSnapshots(
                "ghost-sidecar",
                sidecarEpoch: 0,
                vesselName: "Ghost Failure Vessel",
                ghostName: "Ghost Failure Visual",
                pidBase: 5200,
                pointUt: 300);
            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                written, precPath, vesselPath, ghostPath, incrementEpoch: true));
            Assert.Equal(GhostSnapshotMode.Separate, written.GhostSnapshotMode);

            if (unsupportedVersion)
            {
                WriteUnsupportedSnapshotSidecar(ghostPath);
            }
            else
            {
                byte[] ghostBytes = File.ReadAllBytes(ghostPath);
                ghostBytes[ghostBytes.Length - 1] ^= 0x5A;
                File.WriteAllBytes(ghostPath, ghostBytes);
            }

            var loaded = new Recording
            {
                RecordingId = written.RecordingId,
                SidecarEpoch = written.SidecarEpoch,
                GhostSnapshotMode = written.GhostSnapshotMode
            };

            logLines.Clear();
            Assert.False(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                loaded, precPath, vesselPath, ghostPath));
            Assert.True(loaded.SidecarLoadFailed);
            Assert.Equal(expectedFailureReason, loaded.SidecarLoadFailureReason);
            Assert.NotNull(loaded.VesselSnapshot);
            Assert.Null(loaded.GhostVisualSnapshot);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[RecordingStore]") &&
                l.Contains(expectedLogFragment));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("missing ghost snapshot, fell back to vessel snapshot"));
        }

        [Fact]
        public void SaveRecordingFiles_FailedGhostWrite_RollsBackOnDiskSidecars_AndLaterRewriteHeals()
        {
            string dir = Path.Combine(tempDir, "save-heal");
            Directory.CreateDirectory(dir);

            string precPath = Path.Combine(dir, "save-heal.prec");
            string vesselPath = Path.Combine(dir, "save-heal_vessel.craft");
            string ghostPath = Path.Combine(dir, "save-heal_ghost.craft");

            Recording original = BuildRecordingWithSnapshots(
                "save-heal",
                sidecarEpoch: 2,
                vesselName: "Heal Vessel Old",
                ghostName: "Heal Ghost Old",
                pidBase: 5005,
                pointUt: 123);
            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                original, precPath, vesselPath, ghostPath, incrementEpoch: true));
            Assert.Equal(3, original.SidecarEpoch);

            Recording rewritten = BuildRecordingWithSnapshots(
                "save-heal",
                sidecarEpoch: original.SidecarEpoch,
                vesselName: "Heal Vessel New",
                ghostName: "Heal Ghost New",
                pidBase: 5015,
                pointUt: 456);
            rewritten.GhostSnapshotMode = original.GhostSnapshotMode;

            using (var ghostLock = new FileStream(ghostPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                logLines.Clear();
                Assert.False(RecordingStore.SaveRecordingFilesToPathsForTesting(
                    rewritten, precPath, vesselPath, ghostPath, incrementEpoch: true));
            }
            Assert.Equal(3, rewritten.SidecarEpoch);
            Assert.Equal(original.GhostSnapshotMode, rewritten.GhostSnapshotMode);
            Assert.True(rewritten.FilesDirty);

            TrajectorySidecarProbe rolledBackProbe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(precPath, out rolledBackProbe));
            Assert.Equal(3, rolledBackProbe.SidecarEpoch);

            var loadedOld = new Recording
            {
                RecordingId = original.RecordingId,
                SidecarEpoch = original.SidecarEpoch,
                GhostSnapshotMode = original.GhostSnapshotMode
            };
            Assert.True(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                loadedOld, precPath, vesselPath, ghostPath));
            Assert.False(loadedOld.SidecarLoadFailed);
            Assert.Single(loadedOld.Points);
            Assert.Equal(123, loadedOld.Points[0].ut);
            AssertConfigNodeEquivalent(original.VesselSnapshot, loadedOld.VesselSnapshot, "rolledBackVessel");
            AssertConfigNodeEquivalent(original.GhostVisualSnapshot, loadedOld.GhostVisualSnapshot, "rolledBackGhost");

            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                rewritten, precPath, vesselPath, ghostPath, incrementEpoch: true));
            Assert.Equal(4, rewritten.SidecarEpoch);
            Assert.Equal(GhostSnapshotMode.Separate, rewritten.GhostSnapshotMode);
            Assert.False(rewritten.FilesDirty);

            TrajectorySidecarProbe healedProbe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(precPath, out healedProbe));
            Assert.Equal(4, healedProbe.SidecarEpoch);
            Assert.True(File.Exists(vesselPath));
            Assert.True(File.Exists(ghostPath));

            var loadedNew = new Recording
            {
                RecordingId = rewritten.RecordingId,
                SidecarEpoch = rewritten.SidecarEpoch,
                GhostSnapshotMode = rewritten.GhostSnapshotMode
            };
            Assert.True(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                loadedNew, precPath, vesselPath, ghostPath));
            Assert.Single(loadedNew.Points);
            Assert.Equal(456, loadedNew.Points[0].ut);
            AssertConfigNodeEquivalent(rewritten.VesselSnapshot, loadedNew.VesselSnapshot, "healedVessel");
            AssertConfigNodeEquivalent(rewritten.GhostVisualSnapshot, loadedNew.GhostVisualSnapshot, "healedGhost");
        }

        [Fact]
        public void SaveRecordingFiles_FirstWriteFailureAfterTrajectoryCommit_RollsBackNewFiles()
        {
            string dir = Path.Combine(tempDir, "first-write-rollback");
            Directory.CreateDirectory(dir);

            string precPath = Path.Combine(dir, "first-write-rollback.prec");
            string vesselPath = Path.Combine(dir, "first-write-rollback_vessel.craft");
            string ghostPath = Path.Combine(dir, "first-write-rollback_ghost.craft");
            Directory.CreateDirectory(vesselPath);

            Recording rec = BuildRecordingWithSnapshots(
                "first-write-rollback",
                sidecarEpoch: 0,
                vesselName: "First Write Vessel",
                ghostName: "First Write Ghost",
                pidBase: 5300,
                pointUt: 321);

            logLines.Clear();
            Assert.False(RecordingStore.SaveRecordingFilesToPathsForTesting(
                rec, precPath, vesselPath, ghostPath, incrementEpoch: true));
            Assert.Equal(0, rec.SidecarEpoch);
            Assert.True(rec.FilesDirty);
            Assert.False(File.Exists(precPath));
            Assert.False(File.Exists(ghostPath));
            Assert.True(Directory.Exists(vesselPath));
            AssertNoTransientSidecarArtifacts(dir);
        }

        [Fact]
        public void RecordingSidecarStore_SaveRecordingFilesToPaths_WritesAuthoritativeSidecarsAndClearsDirty()
        {
            string dir = Path.Combine(tempDir, "sidecar-store-direct-save");
            Directory.CreateDirectory(dir);

            string precPath = Path.Combine(dir, "sidecar-store-direct-save.prec");
            string vesselPath = Path.Combine(dir, "sidecar-store-direct-save_vessel.craft");
            string ghostPath = Path.Combine(dir, "sidecar-store-direct-save_ghost.craft");

            Recording rec = BuildRecordingWithSnapshots(
                "sidecar-store-direct-save",
                sidecarEpoch: 4,
                vesselName: "Direct Save Vessel",
                ghostName: "Direct Save Ghost",
                pidBase: 5350,
                pointUt: 654);

            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = false;
            Assert.True(RecordingSidecarStore.SaveRecordingFilesToPathsForTesting(
                rec, precPath, vesselPath, ghostPath, incrementEpoch: true));

            Assert.Equal(5, rec.SidecarEpoch);
            Assert.Equal(GhostSnapshotMode.Separate, rec.GhostSnapshotMode);
            Assert.False(rec.FilesDirty);
            Assert.True(File.Exists(precPath));
            Assert.True(File.Exists(vesselPath));
            Assert.True(File.Exists(ghostPath));

            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(precPath, out probe));
            Assert.Equal(5, probe.SidecarEpoch);
        }

        [Fact]
        public void SaveRecordingFiles_ReadableMirrorsDisabled_DeletesExistingMirrorFiles()
        {
            string dir = Path.Combine(tempDir, "readable-mirrors-disabled");
            Directory.CreateDirectory(dir);

            string precPath = Path.Combine(dir, "readable-mirrors-disabled.prec");
            string vesselPath = Path.Combine(dir, "readable-mirrors-disabled_vessel.craft");
            string ghostPath = Path.Combine(dir, "readable-mirrors-disabled_ghost.craft");
            string readablePrecPath = precPath + ".txt";
            string readableVesselPath = vesselPath + ".txt";
            string readableGhostPath = ghostPath + ".txt";

            Recording rec = BuildRecordingWithSnapshots(
                "readable-mirrors-disabled",
                sidecarEpoch: 0,
                vesselName: "Readable Vessel",
                ghostName: "Readable Ghost",
                pidBase: 5400,
                pointUt: 100);

            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = true;
            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                rec, precPath, vesselPath, ghostPath, incrementEpoch: false));
            Assert.True(File.Exists(readablePrecPath));
            Assert.True(File.Exists(readableVesselPath));
            Assert.True(File.Exists(readableGhostPath));

            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = false;
            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                rec, precPath, vesselPath, ghostPath, incrementEpoch: false));

            Assert.True(File.Exists(precPath));
            Assert.True(File.Exists(vesselPath));
            Assert.True(File.Exists(ghostPath));
            Assert.False(File.Exists(readablePrecPath));
            Assert.False(File.Exists(readableVesselPath));
            Assert.False(File.Exists(readableGhostPath));
        }

        [Fact]
        public void ReconcileReadableMirrorsToPaths_DisabledDeletesExistingMirrors_WithoutRewritingAuthoritativeSidecars()
        {
            string dir = Path.Combine(tempDir, "readable-mirrors-reconcile-only");
            Directory.CreateDirectory(dir);

            string precPath = Path.Combine(dir, "readable-mirrors-reconcile-only.prec");
            string vesselPath = Path.Combine(dir, "readable-mirrors-reconcile-only_vessel.craft");
            string ghostPath = Path.Combine(dir, "readable-mirrors-reconcile-only_ghost.craft");
            string readablePrecPath = precPath + ".txt";
            string readableVesselPath = vesselPath + ".txt";
            string readableGhostPath = ghostPath + ".txt";

            Recording rec = BuildRecordingWithSnapshots(
                "readable-mirrors-reconcile-only",
                sidecarEpoch: 0,
                vesselName: "Reconcile Vessel",
                ghostName: "Reconcile Ghost",
                pidBase: 5450,
                pointUt: 150);

            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = true;
            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                rec, precPath, vesselPath, ghostPath, incrementEpoch: false));

            DateTime authoritativePrecWriteUtc = File.GetLastWriteTimeUtc(precPath);
            DateTime authoritativeVesselWriteUtc = File.GetLastWriteTimeUtc(vesselPath);
            DateTime authoritativeGhostWriteUtc = File.GetLastWriteTimeUtc(ghostPath);

            Assert.True(File.Exists(readablePrecPath));
            Assert.True(File.Exists(readableVesselPath));
            Assert.True(File.Exists(readableGhostPath));

            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = false;
            Assert.True(RecordingStore.ReconcileReadableSidecarMirrorsToPathsForTesting(
                rec, precPath, vesselPath, ghostPath));

            Assert.True(File.Exists(precPath));
            Assert.True(File.Exists(vesselPath));
            Assert.True(File.Exists(ghostPath));
            Assert.False(File.Exists(readablePrecPath));
            Assert.False(File.Exists(readableVesselPath));
            Assert.False(File.Exists(readableGhostPath));
            Assert.Equal(authoritativePrecWriteUtc, File.GetLastWriteTimeUtc(precPath));
            Assert.Equal(authoritativeVesselWriteUtc, File.GetLastWriteTimeUtc(vesselPath));
            Assert.Equal(authoritativeGhostWriteUtc, File.GetLastWriteTimeUtc(ghostPath));
            Assert.Equal(0, rec.SidecarEpoch);
        }

        [Fact]
        public void ReconcileReadableMirrorsToPaths_UsesPreservedAuthoritativeVesselSnapshot_WhenInMemorySnapshotIsNull()
        {
            string dir = Path.Combine(tempDir, "readable-mirrors-preserved-vessel");
            Directory.CreateDirectory(dir);

            string precPath = Path.Combine(dir, "readable-mirrors-preserved-vessel.prec");
            string vesselPath = Path.Combine(dir, "readable-mirrors-preserved-vessel_vessel.craft");
            string ghostPath = Path.Combine(dir, "readable-mirrors-preserved-vessel_ghost.craft");
            string readableVesselPath = vesselPath + ".txt";

            Recording rec = BuildRecordingWithSnapshots(
                "readable-mirrors-preserved-vessel",
                sidecarEpoch: 0,
                vesselName: "Preserved Vessel",
                ghostName: "Preserved Ghost",
                pidBase: 5475,
                pointUt: 175);
            ConfigNode expectedVessel = rec.VesselSnapshot;

            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = false;
            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                rec, precPath, vesselPath, ghostPath, incrementEpoch: false));
            Assert.True(File.Exists(vesselPath));
            Assert.False(File.Exists(readableVesselPath));

            rec.VesselSnapshot = null;
            rec.GhostVisualSnapshot = null;

            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = true;
            Assert.True(RecordingStore.ReconcileReadableSidecarMirrorsToPathsForTesting(
                rec, precPath, vesselPath, ghostPath));

            Assert.True(File.Exists(readableVesselPath));
            AssertConfigNodeEquivalent(expectedVessel, ConfigNode.Load(readableVesselPath), "preservedReadableVessel");
        }

        [Fact]
        public void SaveRecordingFiles_LogsReadableVesselSource_WhenMirrorUsesPreservedAuthoritativeSnapshot()
        {
            string dir = Path.Combine(tempDir, "readable-mirror-log-source");
            Directory.CreateDirectory(dir);

            string precPath = Path.Combine(dir, "readable-mirror-log-source.prec");
            string vesselPath = Path.Combine(dir, "readable-mirror-log-source_vessel.craft");
            string ghostPath = Path.Combine(dir, "readable-mirror-log-source_ghost.craft");

            Recording rec = BuildRecordingWithSnapshots(
                "readable-mirror-log-source",
                sidecarEpoch: 0,
                vesselName: "Logged Vessel",
                ghostName: "Logged Ghost",
                pidBase: 5485,
                pointUt: 200);

            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = false;
            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                rec, precPath, vesselPath, ghostPath, incrementEpoch: false));

            rec.VesselSnapshot = null;
            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = true;
            logLines.Clear();
            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                rec, precPath, vesselPath, ghostPath, incrementEpoch: false));

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("SaveRecordingFiles: id=readable-mirror-log-source") &&
                l.Contains("wroteVessel=False") &&
                l.Contains("wroteReadableVessel=True") &&
                l.Contains("readableVesselSource=AuthoritativeSidecar"));
        }

        [Fact]
        public void SaveRecordingFiles_ReadableMirrorFailure_DoesNotRollBackAuthoritativeSidecars()
        {
            string dir = Path.Combine(tempDir, "readable-mirror-best-effort");
            Directory.CreateDirectory(dir);

            string precPath = Path.Combine(dir, "readable-mirror-best-effort.prec");
            string vesselPath = Path.Combine(dir, "readable-mirror-best-effort_vessel.craft");
            string ghostPath = Path.Combine(dir, "readable-mirror-best-effort_ghost.craft");
            string readablePrecPath = precPath + ".txt";

            var original = BuildRecordingWithSnapshots(
                "readable-mirror-best-effort",
                sidecarEpoch: 0,
                vesselName: "Readable Old Vessel",
                ghostName: "Readable Old Ghost",
                pidBase: 5500,
                pointUt: 123);
            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = true;
            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                original, precPath, vesselPath, ghostPath, incrementEpoch: false));

            File.Delete(readablePrecPath);
            Directory.CreateDirectory(readablePrecPath);

            var rewritten = BuildRecordingWithSnapshots(
                "readable-mirror-best-effort",
                sidecarEpoch: original.SidecarEpoch,
                vesselName: "Readable New Vessel",
                ghostName: "Readable New Ghost",
                pidBase: 5510,
                pointUt: 456);

            logLines.Clear();
            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                rewritten, precPath, vesselPath, ghostPath, incrementEpoch: false));
            Assert.False(rewritten.FilesDirty);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[RecordingStore]") &&
                l.Contains("readable sidecar mirror reconcile failed"));

            var restored = new Recording
            {
                RecordingId = rewritten.RecordingId,
                SidecarEpoch = rewritten.SidecarEpoch,
                GhostSnapshotMode = GhostSnapshotMode.Separate
            };
            Assert.True(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                restored, precPath, vesselPath, ghostPath));
            Assert.Single(restored.Points);
            Assert.Equal(456, restored.Points[0].ut);
            Assert.True(Directory.Exists(readablePrecPath));
            AssertNoTransientSidecarArtifacts(dir);
        }

        [Fact]
        public void ApplyStagedSidecarChanges_DeleteExistingRollback_RestoresOriginalGhostFile()
        {
            string dir = Path.Combine(tempDir, "delete-existing-rollback");
            Directory.CreateDirectory(dir);

            string ghostPath = Path.Combine(dir, "delete-existing-rollback_ghost.craft");
            string blockedPath = Path.Combine(dir, "delete-existing-rollback_blocked.prec");
            string stagedBlockedPath = Path.Combine(dir, "delete-existing-rollback_blocked.prec.stage.test");
            File.WriteAllText(ghostPath, "original ghost snapshot");
            File.WriteAllText(stagedBlockedPath, "staged trajectory");
            Directory.CreateDirectory(blockedPath);

            Assert.ThrowsAny<Exception>(
                () => SidecarFileCommitBatch.Apply(
                    new List<SidecarFileCommitBatch.StagedChange>
                    {
                        CreateStagedDeleteChangeForTesting(ghostPath),
                        CreateStagedWriteChangeForTesting(blockedPath, stagedBlockedPath)
                    },
                    () => RecordingStore.SuppressLogging));

            Assert.True(File.Exists(ghostPath));
            Assert.Equal("original ghost snapshot", File.ReadAllText(ghostPath));
            Assert.True(Directory.Exists(blockedPath));
            Assert.False(File.Exists(stagedBlockedPath));
            AssertNoTransientSidecarArtifacts(dir);
        }

        [Fact]
        public void BinaryTrajectorySidecar_IsSmallerThanEquivalentTextSidecar()
        {
            Recording text = RecordingStorageFixtures.MaterializeTrajectory(
                RecordingStorageFixtures.AtmosphericActiveMultiSection().Builder);
            text.RecordingId = "size-text";
            text.RecordingFormatVersion = 1;

            Recording binary = RecordingStorageFixtures.MaterializeTrajectory(
                RecordingStorageFixtures.AtmosphericActiveMultiSection().Builder);
            binary.RecordingId = "size-binary";
            binary.RecordingFormatVersion = 2;

            string textPath = Path.Combine(tempDir, "size-text.prec");
            string binaryPath = Path.Combine(tempDir, "size-binary.prec");

            RecordingStore.WriteTrajectorySidecar(textPath, text, sidecarEpoch: 1);
            RecordingStore.WriteTrajectorySidecar(binaryPath, binary, sidecarEpoch: 1);

            long textBytes = new FileInfo(textPath).Length;
            long binaryBytes = new FileInfo(binaryPath).Length;

            Assert.True(binaryBytes < textBytes,
                $"Expected binary sidecar ({binaryBytes} bytes) to be smaller than text ({textBytes} bytes)");
        }

        [Fact]
        public void SparseBinaryTrajectorySidecar_IsSmallerThanLegacyBinaryForStableFields()
        {
            Recording legacyBinary = RecordingStorageFixtures.MaterializeTrajectory(
                RecordingStorageFixtures.AtmosphericActiveMultiSection().Builder);
            legacyBinary.RecordingId = "size-v2";
            legacyBinary.RecordingFormatVersion = 2;

            Recording sparseBinary = RecordingStorageFixtures.MaterializeTrajectory(
                RecordingStorageFixtures.AtmosphericActiveMultiSection().Builder);
            sparseBinary.RecordingId = "size-v3";
            sparseBinary.RecordingFormatVersion = 3;

            string legacyPath = Path.Combine(tempDir, "size-v2.prec");
            string sparsePath = Path.Combine(tempDir, "size-v3.prec");

            RecordingStore.WriteTrajectorySidecar(legacyPath, legacyBinary, sidecarEpoch: 1);
            RecordingStore.WriteTrajectorySidecar(sparsePath, sparseBinary, sidecarEpoch: 1);

            long legacyBytes = new FileInfo(legacyPath).Length;
            long sparseBytes = new FileInfo(sparsePath).Length;

            Assert.True(sparseBytes < legacyBytes,
                $"Expected sparse binary sidecar ({sparseBytes} bytes) to be smaller than legacy binary ({legacyBytes} bytes)");
        }

        [Fact]
        public void SparseBinaryTrajectorySidecar_RoundTripsStableBodyAndCareerDefaultsExactly()
        {
            var rec = new Recording
            {
                RecordingId = "sparse-defaults",
                RecordingFormatVersion = 3,
                VesselName = "Sparse Defaults"
            };

            rec.Points.Add(new TrajectoryPoint
            {
                ut = 10,
                latitude = 1,
                longitude = 2,
                altitude = 100,
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = new Vector3(1, 2, 3),
                bodyName = "Kerbin",
                funds = 1000,
                science = 2.5f,
                reputation = 1.0f
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 20,
                latitude = 1.1,
                longitude = 2.1,
                altitude = 110,
                rotation = new Quaternion(0.1f, 0, 0, 0.995f),
                velocity = new Vector3(1, 3, 3),
                bodyName = "Kerbin",
                funds = 1000,
                science = 2.5f,
                reputation = 1.0f
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 30,
                latitude = 1.2,
                longitude = 2.2,
                altitude = 120,
                rotation = new Quaternion(0.2f, 0, 0, 0.98f),
                velocity = new Vector3(1, 4, 3),
                bodyName = "Kerbin",
                funds = 950,
                science = 2.5f,
                reputation = 1.5f
            });

            string path = Path.Combine(tempDir, "sparse-defaults.prec");
            logLines.Clear();
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.WriteTrajectorySidecar(path, rec, sidecarEpoch: 4);

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("WriteBinaryTrajectoryFile") &&
                l.Contains("sparsePointLists=1") &&
                l.Contains("omittedBody=3"));

            var restored = new Recording { RecordingId = rec.RecordingId };
            logLines.Clear();
            Assert.True(RecordingStore.LoadTrajectorySidecarForTesting(path, restored));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("ReadBinaryTrajectoryFile") &&
                l.Contains("used flat fallback path") &&
                l.Contains("sparsePointLists=1") &&
                l.Contains("defaultedBody=3") &&
                l.Contains("defaultedFunds=2") &&
                l.Contains("defaultedScience=3") &&
                l.Contains("defaultedRep=2"));
            AssertSemanticTrajectoryEqual(rec, restored);
        }

        private static Recording RoundTrip(Recording original)
        {
            var node = new ConfigNode("PARSEK_RECORDING");
            node.AddValue("version", original.RecordingFormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(original.RecordingId))
                node.AddValue("recordingId", original.RecordingId);
            RecordingStore.SerializeTrajectoryInto(node, original);

            var restored = new Recording
            {
                RecordingId = original.RecordingId,
                RecordingFormatVersion = original.RecordingFormatVersion,
                VesselName = original.VesselName
            };
            RecordingStore.DeserializeTrajectoryFrom(node, restored);
            return restored;
        }

        private static Recording BuildRecorderLoadedToOnRailsFallbackRecording()
        {
            const uint pid = 9201;
            const string recId = "bg-recorder-loaded-onrails";

            var tree = new RecordingTree { Id = "tree_storage_bg" };
            var rec = new Recording
            {
                RecordingId = recId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                VesselName = "Background Booster",
                VesselPersistentId = pid
            };
            tree.Recordings[recId] = rec;
            tree.BackgroundMap[pid] = recId;

            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.InjectLoadedStateWithEnvironmentForTesting(
                pid, recId, SegmentEnvironment.Atmospheric, 100.0);
            bgRecorder.InjectCurrentTrackSectionFrameForTesting(pid, new TrajectoryPoint
            {
                ut = 100.0,
                latitude = 0.0,
                longitude = 0.0,
                altitude = 1000.0,
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = new Vector3(0, 120, 0),
                bodyName = "Kerbin"
            });
            bgRecorder.InjectCurrentTrackSectionFrameForTesting(pid, new TrajectoryPoint
            {
                ut = 105.0,
                latitude = 0.01,
                longitude = 0.02,
                altitude = 1500.0,
                rotation = new Quaternion(0, 0.1f, 0, 0.99f),
                velocity = new Vector3(0, 200, 0),
                bodyName = "Kerbin"
            });
            bgRecorder.FlushLoadedStateForOnRailsTransitionForTesting(
                pid,
                SegmentEnvironment.ExoBallistic,
                willHavePlayableOnRailsPayload: true,
                boundaryPoint: new TrajectoryPoint
                {
                    ut = 110.0,
                    latitude = 0.02,
                    longitude = 0.04,
                    altitude = 2000.0,
                    rotation = new Quaternion(0, 0.2f, 0, 0.98f),
                    velocity = new Vector3(0, 260, 0),
                    bodyName = "Kerbin"
                },
                ut: 110.0);

            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 110.0,
                endUT = 500.0,
                inclination = 28.5,
                eccentricity = 0.01,
                semiMajorAxis = 705000.0,
                longitudeOfAscendingNode = 90.0,
                argumentOfPeriapsis = 45.0,
                meanAnomalyAtEpoch = 0.2,
                epoch = 110.0,
                bodyName = "Kerbin"
            });
            return rec;
        }

        private static double[] LookupBody(string bodyName)
        {
            switch (bodyName)
            {
                case "Kerbin":
                    return new[] { 600000d, 3.5316e12 };
                case "Mun":
                    return new[] { 200000d, 6.5138398e10 };
                default:
                    return null;
            }
        }

        private static void AssertSemanticTrajectoryEqual(Recording expected, Recording actual)
        {
            Assert.Equal(expected.Points.Count, actual.Points.Count);
            for (int i = 0; i < expected.Points.Count; i++)
                AssertPointEqual(expected.Points[i], actual.Points[i], $"Point[{i}]");

            Assert.Equal(expected.OrbitSegments.Count, actual.OrbitSegments.Count);
            for (int i = 0; i < expected.OrbitSegments.Count; i++)
                AssertOrbitSegmentEqual(expected.OrbitSegments[i], actual.OrbitSegments[i], $"Orbit[{i}]");

            Assert.Equal(expected.PartEvents.Count, actual.PartEvents.Count);
            for (int i = 0; i < expected.PartEvents.Count; i++)
                AssertPartEventEqual(expected.PartEvents[i], actual.PartEvents[i], $"PartEvent[{i}]");

            Assert.Equal(expected.FlagEvents.Count, actual.FlagEvents.Count);
            for (int i = 0; i < expected.FlagEvents.Count; i++)
                AssertFlagEventEqual(expected.FlagEvents[i], actual.FlagEvents[i], $"FlagEvent[{i}]");

            Assert.Equal(expected.SegmentEvents.Count, actual.SegmentEvents.Count);
            for (int i = 0; i < expected.SegmentEvents.Count; i++)
                AssertSegmentEventEqual(expected.SegmentEvents[i], actual.SegmentEvents[i], $"SegmentEvent[{i}]");

            Assert.Equal(expected.TrackSections.Count, actual.TrackSections.Count);
            for (int i = 0; i < expected.TrackSections.Count; i++)
                AssertTrackSectionEqual(expected.TrackSections[i], actual.TrackSections[i], $"TrackSection[{i}]");
        }

        private static void AssertRecordingStatsEqual(RecordingStats expected, RecordingStats actual)
        {
            Assert.Equal(expected.maxAltitude, actual.maxAltitude, 8);
            Assert.Equal(expected.maxSpeed, actual.maxSpeed, 8);
            Assert.Equal(expected.distanceTravelled, actual.distanceTravelled, 6);
            Assert.Equal(expected.pointCount, actual.pointCount);
            Assert.Equal(expected.orbitSegmentCount, actual.orbitSegmentCount);
            Assert.Equal(expected.partEventCount, actual.partEventCount);
            Assert.Equal(expected.primaryBody, actual.primaryBody);
            Assert.Equal(expected.maxRange, actual.maxRange, 6);
        }

        private static void AssertPointEqual(TrajectoryPoint expected, TrajectoryPoint actual, string label)
        {
            Assert.Equal(expected.ut, actual.ut);
            Assert.Equal(expected.latitude, actual.latitude);
            Assert.Equal(expected.longitude, actual.longitude);
            Assert.Equal(expected.altitude, actual.altitude);
            Assert.Equal(expected.bodyName, actual.bodyName);
            Assert.Equal(expected.velocity.x, actual.velocity.x);
            Assert.Equal(expected.velocity.y, actual.velocity.y);
            Assert.Equal(expected.velocity.z, actual.velocity.z);
            Assert.Equal(expected.rotation.x, actual.rotation.x);
            Assert.Equal(expected.rotation.y, actual.rotation.y);
            Assert.Equal(expected.rotation.z, actual.rotation.z);
            Assert.Equal(expected.rotation.w, actual.rotation.w);
            Assert.Equal(expected.funds, actual.funds);
            Assert.Equal(expected.science, actual.science);
            Assert.Equal(expected.reputation, actual.reputation);
        }

        private static void AssertOrbitSegmentEqual(OrbitSegment expected, OrbitSegment actual, string label)
        {
            Assert.Equal(expected.startUT, actual.startUT);
            Assert.Equal(expected.endUT, actual.endUT);
            Assert.Equal(expected.inclination, actual.inclination);
            Assert.Equal(expected.eccentricity, actual.eccentricity);
            Assert.Equal(expected.semiMajorAxis, actual.semiMajorAxis);
            Assert.Equal(expected.longitudeOfAscendingNode, actual.longitudeOfAscendingNode);
            Assert.Equal(expected.argumentOfPeriapsis, actual.argumentOfPeriapsis);
            Assert.Equal(expected.meanAnomalyAtEpoch, actual.meanAnomalyAtEpoch);
            Assert.Equal(expected.epoch, actual.epoch);
            Assert.Equal(expected.bodyName, actual.bodyName);
            Assert.Equal(expected.isPredicted, actual.isPredicted);
            Assert.Equal(expected.orbitalFrameRotation.x, actual.orbitalFrameRotation.x);
            Assert.Equal(expected.orbitalFrameRotation.y, actual.orbitalFrameRotation.y);
            Assert.Equal(expected.orbitalFrameRotation.z, actual.orbitalFrameRotation.z);
            Assert.Equal(expected.orbitalFrameRotation.w, actual.orbitalFrameRotation.w);
            Assert.Equal(expected.angularVelocity.x, actual.angularVelocity.x);
            Assert.Equal(expected.angularVelocity.y, actual.angularVelocity.y);
            Assert.Equal(expected.angularVelocity.z, actual.angularVelocity.z);
        }

        private static void AssertPartEventEqual(PartEvent expected, PartEvent actual, string label)
        {
            Assert.Equal(expected.ut, actual.ut);
            Assert.Equal(expected.partPersistentId, actual.partPersistentId);
            Assert.Equal(expected.eventType, actual.eventType);
            Assert.Equal(expected.partName, actual.partName);
            Assert.Equal(expected.value, actual.value);
            Assert.Equal(expected.moduleIndex, actual.moduleIndex);
        }

        private static void AssertFlagEventEqual(FlagEvent expected, FlagEvent actual, string label)
        {
            Assert.Equal(expected.ut, actual.ut);
            Assert.Equal(expected.flagSiteName, actual.flagSiteName);
            Assert.Equal(expected.placedBy, actual.placedBy);
            Assert.Equal(expected.plaqueText, actual.plaqueText);
            Assert.Equal(expected.flagURL, actual.flagURL);
            Assert.Equal(expected.latitude, actual.latitude);
            Assert.Equal(expected.longitude, actual.longitude);
            Assert.Equal(expected.altitude, actual.altitude);
            Assert.Equal(expected.rotX, actual.rotX);
            Assert.Equal(expected.rotY, actual.rotY);
            Assert.Equal(expected.rotZ, actual.rotZ);
            Assert.Equal(expected.rotW, actual.rotW);
            Assert.Equal(expected.bodyName, actual.bodyName);
        }

        private static void AssertSegmentEventEqual(SegmentEvent expected, SegmentEvent actual, string label)
        {
            Assert.Equal(expected.ut, actual.ut);
            Assert.Equal(expected.type, actual.type);
            Assert.Equal(expected.details, actual.details);
        }

        private static void AssertTrackSectionEqual(TrackSection expected, TrackSection actual, string label)
        {
            Assert.Equal(expected.environment, actual.environment);
            Assert.Equal(expected.referenceFrame, actual.referenceFrame);
            Assert.Equal(expected.startUT, actual.startUT);
            Assert.Equal(expected.endUT, actual.endUT);
            Assert.Equal(expected.anchorVesselId, actual.anchorVesselId);
            Assert.Equal(expected.sampleRateHz, actual.sampleRateHz);
            Assert.Equal(expected.source, actual.source);
            Assert.Equal(expected.boundaryDiscontinuityMeters, actual.boundaryDiscontinuityMeters);
            Assert.Equal(expected.minAltitude, actual.minAltitude);
            Assert.Equal(expected.maxAltitude, actual.maxAltitude);

            Assert.Equal(expected.frames.Count, actual.frames.Count);
            for (int i = 0; i < expected.frames.Count; i++)
                AssertPointEqual(expected.frames[i], actual.frames[i], label + ".frames[" + i + "]");

            Assert.Equal(expected.checkpoints.Count, actual.checkpoints.Count);
            for (int i = 0; i < expected.checkpoints.Count; i++)
                AssertOrbitSegmentEqual(expected.checkpoints[i], actual.checkpoints[i],
                    label + ".checkpoints[" + i + "]");
        }

        private static void AssertConfigNodeEquivalent(ConfigNode expected, ConfigNode actual, string label)
        {
            Assert.NotNull(expected);
            Assert.NotNull(actual);
            actual = NormalizeLoadedNode(expected.name, actual);
            if (actual.name != "root")
                Assert.Equal(expected.name, actual.name);
            Assert.Equal(expected.values.Count, actual.values.Count);

            for (int i = 0; i < expected.values.Count; i++)
            {
                Assert.Equal(expected.values[i].name, actual.values[i].name);
                Assert.Equal(expected.values[i].value, actual.values[i].value);
            }

            Assert.Equal(expected.nodes.Count, actual.nodes.Count);
            for (int i = 0; i < expected.nodes.Count; i++)
                AssertConfigNodeEquivalent(expected.nodes[i], actual.nodes[i], label + "." + expected.nodes[i].name + "[" + i + "]");
        }

        private static ConfigNode NormalizeLoadedNode(string expectedName, ConfigNode node)
        {
            if (node == null)
                return null;

            if (node.name == expectedName)
                return node;

            if (node.name == "root" &&
                node.nodes != null &&
                node.nodes.Count == 1 &&
                node.nodes[0].name == expectedName)
            {
                return node.nodes[0];
            }

            return node;
        }

        private static ConfigNode BuildSnapshot(string vesselName, uint pid)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("name", vesselName);
            snapshot.AddValue("persistentId", pid.ToString());

            var part = snapshot.AddNode("PART");
            part.AddValue("name", "probeCoreOcto");
            part.AddValue("persistentId", pid.ToString());

            var module = part.AddNode("MODULE");
            module.AddValue("name", "ModuleCommand");
            module.AddValue("isEnabled", "True");

            return snapshot;
        }

        private static Recording BuildRecordingWithSnapshots(
            string recordingId, int sidecarEpoch, string vesselName, string ghostName, uint pidBase, double pointUt)
        {
            var rec = new Recording
            {
                RecordingId = recordingId,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                SidecarEpoch = sidecarEpoch,
                VesselName = vesselName,
                GhostSnapshotMode = GhostSnapshotMode.Unspecified,
                FilesDirty = true,
                VesselSnapshot = BuildSnapshot(vesselName, pidBase),
                GhostVisualSnapshot = BuildSnapshot(ghostName, pidBase + 1)
            };

            rec.Points.Add(new TrajectoryPoint
            {
                ut = pointUt,
                latitude = 0.1,
                longitude = 0.2,
                altitude = 100,
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = new Vector3(1, 2, 3),
                bodyName = "Kerbin"
            });

            return rec;
        }

        private static void WriteUnsupportedSnapshotSidecar(string path)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new byte[] { (byte)'P', (byte)'R', (byte)'K', (byte)'S' });
                writer.Write(99);
                writer.Write((byte)1);
                writer.Write(16);
                writer.Write(4);
                writer.Write(0u);
                writer.Write(new byte[] { 1, 2, 3, 4 });
            }
        }

        private static void AssertNoTransientSidecarArtifacts(string dir)
        {
            string[] files = Directory.GetFiles(dir);
            Assert.DoesNotContain(files, f => RecordingStore.IsTransientSidecarArtifactFile(Path.GetFileName(f)));
        }

        private static SidecarFileCommitBatch.StagedChange CreateStagedDeleteChangeForTesting(string finalPath)
        {
            return new SidecarFileCommitBatch.StagedChange
            {
                FinalPath = finalPath,
                DeleteExisting = true
            };
        }

        private static SidecarFileCommitBatch.StagedChange CreateStagedWriteChangeForTesting(string finalPath, string stagedPath)
        {
            return new SidecarFileCommitBatch.StagedChange
            {
                FinalPath = finalPath,
                StagedPath = stagedPath,
                DeleteExisting = false
            };
        }

        // -----------------------------------------------------------------------
        // TrajectorySidecarBinary reader bounds (6 tests)
        // -----------------------------------------------------------------------

        [Fact]
        public void TrajectorySidecarBinary_TryProbe_FileShorterThanMagic_ReturnsFalse()
        {
            string path = Path.Combine(tempDir, "too-short.prec");
            File.WriteAllBytes(path, new byte[] { 0x50, 0x52 }); // 2 bytes, less than 4-byte magic "PRKB"

            // Call TrajectorySidecarBinary.TryProbe directly; the file length (2) is less than
            // Magic.Length + 2*sizeof(int) = 12, so TryProbe returns false with "binary header truncated".
            TrajectorySidecarProbe probe;
            bool result = TrajectorySidecarBinary.TryProbe(path, out probe);
            Assert.False(result);
            Assert.Equal("binary header truncated", probe.FailureReason);
        }

        [Fact]
        public void TrajectorySidecarBinary_TryProbe_PartialMagic_ReturnsFalse()
        {
            string path = Path.Combine(tempDir, "partial-magic.prec");
            // "PR" only -- first two bytes of "PRKB" but file length (2) < 12 minimum
            File.WriteAllBytes(path, new byte[] { (byte)'P', (byte)'R' });

            TrajectorySidecarProbe probe;
            bool result = TrajectorySidecarBinary.TryProbe(path, out probe);
            Assert.False(result);
            Assert.Equal("binary header truncated", probe.FailureReason);
        }

        [Fact]
        public void TrajectorySidecarBinary_TryProbe_MagicButHeaderBodyTruncated_ReturnsFalse()
        {
            // Write "PRKB" + 4 zero bytes = 8 bytes total.
            // TryProbe requires Magic.Length + 2*sizeof(int) = 4+4+4 = 12 bytes minimum.
            string path = Path.Combine(tempDir, "truncated-header.prec");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new byte[] { (byte)'P', (byte)'R', (byte)'K', (byte)'B' });
                writer.Write(new byte[] { 0, 0, 0, 0 }); // 4 zero bytes -- only 8 total
            }

            TrajectorySidecarProbe probe;
            bool result = TrajectorySidecarBinary.TryProbe(path, out probe);
            Assert.False(result);
            Assert.Equal("binary header truncated", probe.FailureReason);
        }

        [Fact]
        public void TrajectorySidecarBinary_Read_PointListCountExceedsRemainingBytes_ThrowsEndOfStream()
        {
            // Craft a file with valid header but point-count=999 with only 20 bytes of data.
            // Probe succeeds (header is valid); Read throws EndOfStreamException.
            string path = Path.Combine(tempDir, "truncated-points.prec");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8))
            {
                // Magic
                writer.Write(new byte[] { (byte)'P', (byte)'R', (byte)'K', (byte)'B' });
                // formatVersion = 3 (SparsePointBinaryVersion, supported)
                writer.Write(3);
                // sidecarEpoch
                writer.Write(1);
                // recordingId (BinaryWriter.Write(string) writes length-prefixed UTF8)
                writer.Write("truncated-points-test");
                // flags byte: sectionAuthoritative=0
                writer.Write((byte)0);
                // string table count = 1 entry ("Kerbin")
                writer.Write(1);
                writer.Write("Kerbin");
                // point list count = 999, but no actual point data follows
                writer.Write(999);
                // 20 zero bytes of payload (not enough for 999 points)
                writer.Write(new byte[20]);
            }

            TrajectorySidecarProbe probe;
            Assert.True(TrajectorySidecarBinary.TryProbe(path, out probe));
            Assert.True(probe.Success);

            var rec = new Recording { RecordingId = "truncated-points-test" };
            // Pin current behavior: the reader throws EndOfStreamException on truncation.
            // A graceful fallback would be a production-code fix tracked separately.
            Assert.Throws<EndOfStreamException>(() =>
                RecordingStore.DeserializeTrajectorySidecar(path, probe, rec));
        }

        [Fact]
        public void TrajectorySidecarBinary_Read_SparsePointListFlagPresent_TruncatedMidPoint_ThrowsEndOfStream()
        {
            // Craft a file with sparse-list header (listFlags = SparsePointListFlagEnabled = 0x01)
            // and point-count=50 but only minimal bytes of payload. Read throws EndOfStreamException.
            string path = Path.Combine(tempDir, "truncated-sparse.prec");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8))
            {
                writer.Write(new byte[] { (byte)'P', (byte)'R', (byte)'K', (byte)'B' });
                writer.Write(3); // formatVersion = 3
                writer.Write(1); // sidecarEpoch
                writer.Write("truncated-sparse-test");
                writer.Write((byte)0); // flags
                writer.Write(1);       // string table count
                writer.Write("Kerbin");
                // point list count = 50
                writer.Write(50);
                // listFlags = SparsePointListFlagEnabled (0x01) | SparsePointListFlagBodyDefault (0x02)
                writer.Write((byte)0x03);
                // defaultBodyName index into string table (index 0 = "Kerbin")
                writer.Write(0);
                // 10 bytes of point data then EOF -- far too short
                writer.Write(new byte[10]);
            }

            TrajectorySidecarProbe probe;
            Assert.True(TrajectorySidecarBinary.TryProbe(path, out probe));
            Assert.True(probe.Success);

            var rec = new Recording { RecordingId = "truncated-sparse-test" };
            Assert.Throws<EndOfStreamException>(() =>
                RecordingStore.DeserializeTrajectorySidecar(path, probe, rec));
        }

        [Fact]
        public void TrajectorySidecarBinary_Read_ValidHeaderWithZeroPoints_ReturnsEmptyRecording()
        {
            // Write a recording with RecordingFormatVersion=3 and empty trajectory lists.
            // Probe should succeed; deserialized recording should have all empty lists.
            var original = new Recording
            {
                RecordingId = "zero-points-v3",
                RecordingFormatVersion = 3
            };

            string path = Path.Combine(tempDir, "zero-points-v3.prec");
            RecordingStore.WriteTrajectorySidecar(path, original, sidecarEpoch: 1);

            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(path, out probe));
            Assert.True(probe.Success);
            Assert.Equal(TrajectorySidecarEncoding.BinaryV3, probe.Encoding);

            var restored = new Recording { RecordingId = "zero-points-v3" };
            RecordingStore.DeserializeTrajectorySidecar(path, probe, restored);

            Assert.Empty(restored.Points);
            Assert.Empty(restored.OrbitSegments);
            Assert.Empty(restored.TrackSections);
        }

        // -----------------------------------------------------------------------
        // Sparse v3 flag combinations (1 theory + 2 facts)
        // -----------------------------------------------------------------------

        public static IEnumerable<object[]> SparseFlagCombinationCases()
        {
            for (int mask = 0; mask < 16; mask++)
            {
                bool shareBody = (mask & 1) != 0;
                bool shareFunds = (mask & 2) != 0;
                bool shareScience = (mask & 4) != 0;
                bool shareRep = (mask & 8) != 0;
                yield return new object[] { shareBody, shareFunds, shareScience, shareRep };
            }
        }

        [Theory]
        [MemberData(nameof(SparseFlagCombinationCases))]
        public void SparseBinaryTrajectorySidecar_AllPresentAbsentCombinations_RoundTripLossless(
            bool shareBody, bool shareFunds, bool shareScience, bool shareRep)
        {
            var original = BuildSparseFixture(shareBody, shareFunds, shareScience, shareRep, pointCount: 8);
            string name = $"sparse-flags-{(shareBody ? "B" : "b")}{(shareFunds ? "F" : "f")}{(shareScience ? "S" : "s")}{(shareRep ? "R" : "r")}";
            string path = Path.Combine(tempDir, name + ".prec");

            RecordingStore.WriteTrajectorySidecar(path, original, sidecarEpoch: 1);

            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(path, out probe));

            var restored = new Recording { RecordingId = original.RecordingId };
            RecordingStore.DeserializeTrajectorySidecar(path, probe, restored);

            AssertSemanticTrajectoryEqual(original, restored);
        }

        [Fact]
        public void SparseBinaryTrajectorySidecar_BodyDefaultWithFundsOverride_MixedCase_RoundTripLossless()
        {
            // All 8 points share bodyName="Kerbin" (body default fires) but have per-point funds.
            var original = new Recording
            {
                RecordingId = "sparse-body-funds-mixed",
                RecordingFormatVersion = 3
            };
            for (int i = 0; i < 8; i++)
            {
                original.Points.Add(new TrajectoryPoint
                {
                    ut = 1000.0 + i * 10,
                    latitude = -0.1 + i * 0.01,
                    longitude = -74.5 + i * 0.01,
                    altitude = 100 + i * 50,
                    rotation = new Quaternion(0, 0, 0, 1),
                    velocity = new Vector3(0, 10 + i, 0),
                    bodyName = "Kerbin",
                    funds = 1000.0 + i * 50,
                    science = 0f,
                    reputation = 0f
                });
            }

            string path = Path.Combine(tempDir, "sparse-body-funds-mixed.prec");
            logLines.Clear();
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.WriteTrajectorySidecar(path, original, sidecarEpoch: 1);

            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(path, out probe));

            var restored = new Recording { RecordingId = original.RecordingId };
            RecordingStore.DeserializeTrajectorySidecar(path, probe, restored);

            AssertSemanticTrajectoryEqual(original, restored);

            // Body default should have fired: sparsePointLists=1 in the write log.
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("WriteBinaryTrajectoryFile") &&
                l.Contains("sparsePointLists=1"));
        }

        [Fact]
        public void SparseBinaryTrajectorySidecar_NoFieldsShareDefaults_UsesDenseListFlag_RoundTripLossless()
        {
            // Each point has a unique body name (frequency 1 each) so the body savings gate cannot fire.
            // All other fields also vary per-point. The sparse plan therefore stays disabled and the writer
            // uses the dense (non-sparse) list flag. Round-trip correctness is the primary assertion.
            string[] bodies =
            {
                "Body0", "Body1", "Body2", "Body3", "Body4", "Body5", "Body6", "Body7"
            };
            var original = new Recording
            {
                RecordingId = "sparse-all-vary",
                RecordingFormatVersion = 3
            };
            for (int i = 0; i < 8; i++)
            {
                original.Points.Add(new TrajectoryPoint
                {
                    ut = 2000.0 + i * 10,
                    latitude = -0.1 + i * 0.01,
                    longitude = -74.0 + i * 0.1,
                    altitude = 200 + i * 100,
                    rotation = new Quaternion(0, 0, 0, 1),
                    velocity = new Vector3(0, 20 + i, 0),
                    bodyName = bodies[i],
                    funds = 1000.0 + i * 137.3,
                    science = i * 0.5f,
                    reputation = i * 0.3f
                });
            }

            string path = Path.Combine(tempDir, "sparse-all-vary.prec");
            logLines.Clear();
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.WriteTrajectorySidecar(path, original, sidecarEpoch: 1);

            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(path, out probe));

            var restored = new Recording { RecordingId = original.RecordingId };
            RecordingStore.DeserializeTrajectorySidecar(path, probe, restored);

            AssertSemanticTrajectoryEqual(original, restored);

            // Dense branch: no sparsePointLists=1 in write log.
            // With 8 unique body names the body savings = (4*1)-4 = 0; all other fields also unique.
            // Total savings = 0, which does not exceed the gate (points.Count + 1 = 9), so sparse stays off.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("WriteBinaryTrajectoryFile") &&
                l.Contains("sparsePointLists=1"));
        }

        private static Recording BuildSparseFixture(
            bool shareBody, bool shareFunds, bool shareScience, bool shareRep, int pointCount = 8)
        {
            var rec = new Recording
            {
                RecordingId = $"sparse-fixture-{Guid.NewGuid():N}",
                RecordingFormatVersion = 3
            };

            // When shareBody=false, use pointCount unique body names so FindMostCommonString
            // returns a mode of 1, making body savings = (4*1)-4 = 0 and leaving the sparse
            // body-default flag disabled. The previous Kerbin/Mun alternation produced 4
            // matches out of 8, which silently enabled SparsePointListFlagBodyDefault even
            // for supposed-absent cases and collapsed the 16-combination matrix.
            for (int i = 0; i < pointCount; i++)
            {
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = 3000.0 + i * 10,
                    latitude = -0.1 + i * 0.01,
                    longitude = -74.0 + i * 0.02,
                    altitude = 500 + i * 50,
                    rotation = new Quaternion(0, 0, 0, 1),
                    velocity = new Vector3(0, 50 + i, 0),
                    bodyName = shareBody ? "Kerbin" : $"Body{i}",
                    funds = shareFunds ? 10000.0 : 1000.0 + i * 100,
                    science = shareScience ? 5.0f : i * 0.7f,
                    reputation = shareRep ? 3.0f : i * 0.4f
                });
            }

            return rec;
        }

        // -----------------------------------------------------------------------
        // Codec round-trip matrix (1 theory)
        // -----------------------------------------------------------------------

        [Theory]
        [InlineData("v0Flat")]
        [InlineData("v1FlatSectionsDuplicated")]
        [InlineData("v1SectionAuthoritative")]
        [InlineData("v2Binary")]
        [InlineData("v3Sparse")]
        [InlineData("aliasSnapshot")]
        public void CodecRoundTripMatrix_EveryFormatPreservesSemanticsAndBoundaryPairs(string caseName)
        {
            // The section-authoritative case needs a fixture whose flat payload exactly matches
            // both the rebuilt points AND the rebuilt orbit segments. BuildBoundaryCodecFixture
            // has 2 flat OrbitSegments but only Absolute track sections (which contribute nothing
            // to RebuildOrbitSegmentsFromTrackSections), so its OrbitSegment exact-match fails
            // and the writer falls through to flat-fallback — good for the duplicated case,
            // wrong for the section-authoritative case.
            Recording fixture = caseName == "v1SectionAuthoritative"
                ? BuildSectionAuthoritativeCodecFixture()
                : BuildBoundaryCodecFixture();

            TrajectorySidecarEncoding expectedEncoding;
            bool expectSectionAuthoritative;
            Recording writeRec;

            switch (caseName)
            {
                case "v0Flat":
                    writeRec = fixture;
                    writeRec.RecordingFormatVersion = 0;
                    writeRec.TrackSections.Clear();
                    expectedEncoding = TrajectorySidecarEncoding.TextConfigNode;
                    expectSectionAuthoritative = false;
                    break;

                case "v1FlatSectionsDuplicated":
                    // Flat OrbitSegments do not match sections (Absolute-only sections rebuild
                    // to empty), so ShouldWriteSectionAuthoritativeTrajectory returns false and
                    // the writer serializes POINT + ORBIT_SEGMENT + TRACK_SECTION nodes.
                    writeRec = fixture;
                    writeRec.RecordingFormatVersion = 1;
                    expectedEncoding = TrajectorySidecarEncoding.TextConfigNode;
                    expectSectionAuthoritative = false;
                    break;

                case "v1SectionAuthoritative":
                    // Fixture adds an OrbitalCheckpoint section whose checkpoints exactly match
                    // the flat OrbitSegments, so FlatTrajectoryExactlyMatchesTrackSectionPayload
                    // returns true and the writer emits only TRACK_SECTION nodes.
                    writeRec = fixture;
                    writeRec.RecordingFormatVersion = 1;
                    expectedEncoding = TrajectorySidecarEncoding.TextConfigNode;
                    expectSectionAuthoritative = true;
                    break;

                case "v2Binary":
                    writeRec = fixture;
                    writeRec.RecordingFormatVersion = 2;
                    expectedEncoding = TrajectorySidecarEncoding.BinaryV2;
                    expectSectionAuthoritative = false;
                    break;

                case "v3Sparse":
                    writeRec = fixture;
                    writeRec.RecordingFormatVersion = 3;
                    expectedEncoding = TrajectorySidecarEncoding.BinaryV3;
                    expectSectionAuthoritative = false;
                    break;

                case "aliasSnapshot":
                    writeRec = fixture;
                    writeRec.RecordingFormatVersion = 3;
                    // Set alias snapshot mode: GhostVisualSnapshot == VesselSnapshot
                    writeRec.VesselSnapshot = BuildSnapshot("CodecMatrix Vessel", pid: 9901u);
                    writeRec.GhostVisualSnapshot = writeRec.VesselSnapshot;
                    writeRec.GhostSnapshotMode = GhostSnapshotMode.AliasVessel;
                    expectedEncoding = TrajectorySidecarEncoding.BinaryV3;
                    expectSectionAuthoritative = false;
                    break;

                default:
                    throw new InvalidOperationException($"Unknown case: {caseName}");
            }

            // Pin the actual write-path choice so a future fixture change cannot silently
            // collapse v1FlatSectionsDuplicated and v1SectionAuthoritative into the same branch.
            Assert.Equal(expectSectionAuthoritative,
                RecordingStore.ShouldWriteSectionAuthoritativeTrajectory(writeRec));

            string path = Path.Combine(tempDir, $"codec-matrix-{caseName}.prec");
            RecordingStore.WriteTrajectorySidecar(path, writeRec, sidecarEpoch: 1);

            // For text-format cases (v0/v1) verify the serialized ConfigNode actually took
            // the expected path: section-authoritative writes omit POINT/ORBIT_SEGMENT,
            // flat-fallback writes include them.
            if (expectedEncoding == TrajectorySidecarEncoding.TextConfigNode)
            {
                var savedNode = ConfigNode.Load(path);
                Assert.NotNull(savedNode);
                int pointNodes = savedNode.GetNodes("POINT").Length;
                int orbitNodes = savedNode.GetNodes("ORBIT_SEGMENT").Length;
                int trackNodes = savedNode.GetNodes("TRACK_SECTION").Length;
                if (expectSectionAuthoritative)
                {
                    Assert.Equal(0, pointNodes);
                    Assert.Equal(0, orbitNodes);
                    Assert.True(trackNodes > 0, $"{caseName} expected TRACK_SECTION nodes");
                }
                else if (caseName != "v0Flat")
                {
                    Assert.True(pointNodes > 0, $"{caseName} expected POINT nodes (flat-fallback)");
                    Assert.True(trackNodes > 0, $"{caseName} expected TRACK_SECTION nodes (flat-fallback)");
                }
            }

            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(path, out probe),
                $"TryProbe failed for case {caseName}");
            Assert.Equal(expectedEncoding, probe.Encoding);

            var restored = new Recording { RecordingId = writeRec.RecordingId };
            RecordingStore.DeserializeTrajectorySidecar(path, probe, restored);

            // The fixture has matching flat and section data. For section-authoritative the restored
            // Points are rebuilt from sections and should equal the fixture's flat Points.
            AssertSemanticTrajectoryEqual(fixture, restored);

            // Boundary-pair assertions on points.
            if (fixture.Points.Count > 0)
            {
                AssertPointEqual(fixture.Points[0], restored.Points[0], $"{caseName} firstPoint");
                AssertPointEqual(fixture.Points[fixture.Points.Count - 1],
                    restored.Points[restored.Points.Count - 1], $"{caseName} lastPoint");
            }

            // Boundary-pair assertions on orbit segments.
            if (fixture.OrbitSegments.Count > 0)
            {
                AssertOrbitSegmentEqual(fixture.OrbitSegments[0], restored.OrbitSegments[0],
                    $"{caseName} firstOrbit");
                AssertOrbitSegmentEqual(fixture.OrbitSegments[fixture.OrbitSegments.Count - 1],
                    restored.OrbitSegments[restored.OrbitSegments.Count - 1], $"{caseName} lastOrbit");
            }

            // For aliasSnapshot case: verify the original's GhostSnapshotMode is AliasVessel.
            // The trajectory sidecar does not store snapshot mode; it is loaded separately from .craft files,
            // so we only assert the write-side state and the DetermineGhostSnapshotMode computation.
            if (caseName == "aliasSnapshot")
            {
                Assert.Equal(GhostSnapshotMode.AliasVessel, writeRec.GhostSnapshotMode);
                Assert.Equal(GhostSnapshotMode.AliasVessel,
                    RecordingStore.DetermineGhostSnapshotMode(writeRec));
            }
        }

        private static OrbitSegment MakeOrbitSegment(double startUT, double endUT,
            string bodyName = "Kerbin", bool isPredicted = false)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = 28.5,
                eccentricity = 0.01,
                semiMajorAxis = 700000.0,
                longitudeOfAscendingNode = 90.0,
                argumentOfPeriapsis = 45.0,
                meanAnomalyAtEpoch = 0.2,
                epoch = startUT,
                bodyName = bodyName,
                isPredicted = isPredicted,
                orbitalFrameRotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f),
                angularVelocity = new Vector3(0.4f, 0.5f, 0.6f)
            };
        }

        private static Recording BuildBoundaryCodecFixture()
        {
            const double t0 = 20000.0;

            // Shared boundary point appears in both sections and in the flat list.
            var boundary = new TrajectoryPoint
            {
                ut = t0 + 10,
                latitude = -0.09,
                longitude = -74.55,
                altitude = 500,
                rotation = new Quaternion(0, 0, 0.1f, 0.99f),
                velocity = new Vector3(0, 100, 0),
                bodyName = "Kerbin",
                funds = 20000.0,
                science = 1.0f,
                reputation = 0.5f
            };

            var rec = new Recording
            {
                RecordingId = "codec-matrix-boundary",
                RecordingFormatVersion = 3
            };

            // Flat point list: 3 points (first, boundary, last)
            rec.Points.Add(new TrajectoryPoint
            {
                ut = t0,
                latitude = -0.10,
                longitude = -74.56,
                altitude = 100,
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = new Vector3(0, 50, 0),
                bodyName = "Kerbin",
                funds = 19000.0,
                science = 0f,
                reputation = 0f
            });
            rec.Points.Add(boundary);
            rec.Points.Add(new TrajectoryPoint
            {
                ut = t0 + 20,
                latitude = -0.08,
                longitude = -74.54,
                altitude = 1000,
                rotation = new Quaternion(0, 0, 0.2f, 0.98f),
                velocity = new Vector3(0, 200, 0),
                bodyName = "Kerbin",
                funds = 21000.0,
                science = 2.0f,
                reputation = 1.0f
            });

            // 2 orbit segments
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = t0 + 30,
                endUT = t0 + 330,
                inclination = 28.5,
                eccentricity = 0.002,
                semiMajorAxis = 700000.0,
                longitudeOfAscendingNode = 90.0,
                argumentOfPeriapsis = 45.0,
                meanAnomalyAtEpoch = 0.1,
                epoch = t0 + 30,
                bodyName = "Kerbin"
            });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = t0 + 330,
                endUT = t0 + 630,
                inclination = 30.0,
                eccentricity = 0.001,
                semiMajorAxis = 710000.0,
                longitudeOfAscendingNode = 95.0,
                argumentOfPeriapsis = 50.0,
                meanAnomalyAtEpoch = 0.2,
                epoch = t0 + 330,
                bodyName = "Kerbin"
            });

            // 2 Absolute track sections; boundary point shared between them.
            // Rebuilt Points match flat Points, but rebuilt OrbitSegments is empty (no
            // OrbitalCheckpoint section), so FlatTrajectoryExactlyMatchesTrackSectionPayload
            // returns false and a writer with RecordingFormatVersion >= 1 falls through to
            // flat-fallback. This shape drives the v1FlatSectionsDuplicated case.
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = t0,
                endUT = t0 + 10,
                sampleRateHz = 10f,
                boundaryDiscontinuityMeters = 0f,
                minAltitude = 100f,
                maxAltitude = 500f,
                frames = new List<TrajectoryPoint>
                {
                    rec.Points[0],
                    boundary
                },
                checkpoints = new List<OrbitSegment>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = t0 + 10,
                endUT = t0 + 20,
                sampleRateHz = 10f,
                boundaryDiscontinuityMeters = 0f,
                minAltitude = 500f,
                maxAltitude = 1000f,
                frames = new List<TrajectoryPoint>
                {
                    boundary,
                    rec.Points[2]
                },
                checkpoints = new List<OrbitSegment>()
            });

            return rec;
        }

        // Adds an OrbitalCheckpoint section whose checkpoints exactly match the flat
        // OrbitSegments from BuildBoundaryCodecFixture, so rebuilt orbit segments equal
        // flat orbit segments and FlatTrajectoryExactlyMatchesTrackSectionPayload returns
        // true. Used by the v1SectionAuthoritative matrix case to exercise the real
        // section-authoritative write path (POINT and ORBIT_SEGMENT nodes omitted).
        private static Recording BuildSectionAuthoritativeCodecFixture()
        {
            var rec = BuildBoundaryCodecFixture();

            var checkpointCopies = new List<OrbitSegment>(rec.OrbitSegments.Count);
            foreach (var seg in rec.OrbitSegments)
                checkpointCopies.Add(seg);

            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Checkpoint,
                startUT = rec.OrbitSegments[0].startUT,
                endUT = rec.OrbitSegments[rec.OrbitSegments.Count - 1].endUT,
                sampleRateHz = 0.1f,
                boundaryDiscontinuityMeters = 0f,
                minAltitude = 700000f,
                maxAltitude = 710000f,
                frames = new List<TrajectoryPoint>(),
                checkpoints = checkpointCopies
            });

            return rec;
        }
    }
}
