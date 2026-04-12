using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek.Tests.Generators;
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
            ConfigNode expectedPrecNode = new ConfigNode("PARSEK_RECORDING");
            expectedPrecNode.AddValue("version",
                fixture.Builder.GetFormatVersion().ToString(System.Globalization.CultureInfo.InvariantCulture));
            expectedPrecNode.AddValue("recordingId", recordingId);
            RecordingStore.SerializeTrajectoryInto(expectedPrecNode, original);
            var loadedTrajectory = new Recording { RecordingId = recordingId };
            ConfigNode precNode = ConfigNode.Load(precPath);
            Assert.NotNull(precNode);
            Assert.Equal(
                fixture.Builder.GetFormatVersion().ToString(System.Globalization.CultureInfo.InvariantCulture),
                precNode.GetValue("version"));
            AssertConfigNodeEquivalent(expectedPrecNode, precNode, "prec");
            RecordingStore.DeserializeTrajectoryFrom(precNode, loadedTrajectory);
            AssertSemanticTrajectoryEqual(original, loadedTrajectory);

            ConfigNode expectedVessel = fixture.Builder.GetVesselSnapshot();
            Assert.Equal(expectedVessel != null, File.Exists(vesselPath));
            if (expectedVessel != null)
            {
                ConfigNode loadedVessel = ConfigNode.Load(vesselPath);
                Assert.NotNull(loadedVessel);
                AssertConfigNodeEquivalent(expectedVessel, loadedVessel, "vessel");
            }

            GhostSnapshotMode ghostSnapshotMode = RecordingStore.DetermineGhostSnapshotMode(original);
            Assert.Equal(ghostSnapshotMode == GhostSnapshotMode.Separate, File.Exists(ghostPath));
            if (ghostSnapshotMode == GhostSnapshotMode.Separate)
            {
                ConfigNode expectedGhost = original.GhostVisualSnapshot;
                ConfigNode loadedGhost = ConfigNode.Load(ghostPath);
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
    }
}
