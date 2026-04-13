using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
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

            Assert.True(File.Exists(precPath), $"Expected trajectory sidecar for {fixture.Name}");

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

            TargetInvocationException ex = Assert.Throws<TargetInvocationException>(
                () => InvokeApplyStagedSidecarChangesForTesting(
                    CreateStagedDeleteChangeForTesting(ghostPath),
                    CreateStagedWriteChangeForTesting(blockedPath, stagedBlockedPath)));

            Assert.NotNull(ex.InnerException);
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

        private static object CreateStagedDeleteChangeForTesting(string finalPath)
        {
            Type stagedType = GetStagedSidecarChangeType();
            object change = Activator.CreateInstance(stagedType, nonPublic: true);
            stagedType.GetField("FinalPath").SetValue(change, finalPath);
            stagedType.GetField("DeleteExisting").SetValue(change, true);
            return change;
        }

        private static object CreateStagedWriteChangeForTesting(string finalPath, string stagedPath)
        {
            Type stagedType = GetStagedSidecarChangeType();
            object change = Activator.CreateInstance(stagedType, nonPublic: true);
            stagedType.GetField("FinalPath").SetValue(change, finalPath);
            stagedType.GetField("StagedPath").SetValue(change, stagedPath);
            stagedType.GetField("DeleteExisting").SetValue(change, false);
            return change;
        }

        private static void InvokeApplyStagedSidecarChangesForTesting(params object[] changes)
        {
            Type stagedType = GetStagedSidecarChangeType();
            Type listType = typeof(List<>).MakeGenericType(stagedType);
            object list = Activator.CreateInstance(listType);
            MethodInfo addMethod = listType.GetMethod("Add");
            for (int i = 0; i < changes.Length; i++)
                addMethod.Invoke(list, new[] { changes[i] });

            MethodInfo applyMethod = typeof(RecordingStore).GetMethod(
                "ApplyStagedSidecarChanges",
                BindingFlags.Static | BindingFlags.NonPublic);
            applyMethod.Invoke(null, new[] { list });
        }

        private static Type GetStagedSidecarChangeType()
        {
            return typeof(RecordingStore).GetNestedType(
                "StagedSidecarChange",
                BindingFlags.NonPublic);
        }
    }
}
