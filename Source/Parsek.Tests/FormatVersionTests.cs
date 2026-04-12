using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for recording format version dispatch and the v1 section-authoritative path.
    /// </summary>
    [Collection("Sequential")]
    public class FormatVersionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public FormatVersionTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
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
        }

        #region Version constants

        [Fact]
        public void CurrentRecordingFormatVersion_Is1()
        {
            Assert.Equal(1, RecordingStore.CurrentRecordingFormatVersion);
        }

        [Fact]
        public void NewRecording_DefaultsToCurrentVersion()
        {
            var rec = new Recording();
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, rec.RecordingFormatVersion);
        }

        [Fact]
        public void RecordingBuilder_DefaultVersion_Is1()
        {
            var builder = new RecordingBuilder("TestVessel");
            builder.AddPoint(17000, 0, 0, 100);
            builder.AddPoint(17010, 0, 0, 200);

            var trajectoryNode = builder.BuildTrajectoryNode();
            Assert.Equal("1", trajectoryNode.GetValue("version"));
        }

        [Fact]
        public void RecordingBuilder_WithCustomFormatVersion_WritesCustomVersion()
        {
            var builder = new RecordingBuilder("TestVessel");
            builder.WithFormatVersion(42);
            builder.AddPoint(17000, 0, 0, 100);
            builder.AddPoint(17010, 0, 0, 200);

            var trajectoryNode = builder.BuildTrajectoryNode();
            Assert.Equal("42", trajectoryNode.GetValue("version"));

            var metadataNode = builder.BuildV3Metadata();
            Assert.Equal("42", metadataNode.GetValue("recordingFormatVersion"));
        }

        #endregion

        #region Backward compat

        [Fact]
        public void Recording_WithTrackSections_LoadsPopulatedTrackSections_FromV0()
        {
            var precNode = new ConfigNode("PARSEK_RECORDING");
            precNode.AddValue("version", "0");
            precNode.AddValue("recordingId", "track_test");

            AddMinimalPoint(precNode, 17000.0);

            var tsNode = precNode.AddNode("TRACK_SECTION");
            tsNode.AddValue("env", "0");
            tsNode.AddValue("ref", "0");
            tsNode.AddValue("startUT", "17000");
            tsNode.AddValue("endUT", "17050");
            tsNode.AddValue("sampleRate", "10");

            var frameNode = tsNode.AddNode("POINT");
            frameNode.AddValue("ut", "17000");
            frameNode.AddValue("lat", "-0.097");
            frameNode.AddValue("lon", "-74.558");
            frameNode.AddValue("alt", "70");
            frameNode.AddValue("rotX", "0");
            frameNode.AddValue("rotY", "0");
            frameNode.AddValue("rotZ", "0");
            frameNode.AddValue("rotW", "1");
            frameNode.AddValue("body", "Kerbin");
            frameNode.AddValue("velX", "0");
            frameNode.AddValue("velY", "5");
            frameNode.AddValue("velZ", "0");
            frameNode.AddValue("funds", "0");
            frameNode.AddValue("science", "0");
            frameNode.AddValue("rep", "0");

            var rec = new Recording { RecordingId = "track_test" };
            RecordingStore.DeserializeTrajectoryFrom(precNode, rec);

            Assert.Single(rec.Points);
            Assert.Single(rec.TrackSections);
            Assert.Equal(SegmentEnvironment.Atmospheric, rec.TrackSections[0].environment);
            Assert.Equal(ReferenceFrame.Absolute, rec.TrackSections[0].referenceFrame);
            Assert.Single(rec.TrackSections[0].frames);
        }

        #endregion

        #region V1 section-authoritative write path

        [Fact]
        public void SerializeTrajectoryInto_V1WithTrackSections_SkipsTopLevelTrajectoryCopies()
        {
            Recording rec = RecordingStorageFixtures.MaterializeTrajectory(
                RecordingStorageFixtures.OrbitalCheckpointTransition().Builder);

            var node = new ConfigNode("PARSEK_RECORDING");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            Assert.Empty(node.GetNodes("POINT"));
            Assert.Empty(node.GetNodes("ORBIT_SEGMENT"));
            Assert.Equal(2, node.GetNodes("TRACK_SECTION").Length);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("SerializeTrajectoryInto") &&
                l.Contains("section-authoritative path"));
        }

        [Fact]
        public void SerializeTrajectoryInto_V1WithoutTrackSections_FallsBackToFlatTrajectory()
        {
            var rec = new Recording
            {
                RecordingId = "v1-flat-fallback",
                RecordingFormatVersion = 1
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100, latitude = 0, longitude = 0, altitude = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 110, latitude = 0.1, longitude = 0.1, altitude = 200, bodyName = "Kerbin" });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 110,
                endUT = 500,
                semiMajorAxis = 700000,
                eccentricity = 0.01,
                bodyName = "Kerbin"
            });

            var node = new ConfigNode("PARSEK_RECORDING");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            Assert.Equal(2, node.GetNodes("POINT").Length);
            Assert.Single(node.GetNodes("ORBIT_SEGMENT"));
            Assert.Empty(node.GetNodes("TRACK_SECTION"));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("SerializeTrajectoryInto") &&
                l.Contains("used flat fallback path"));
        }

        [Fact]
        public void SerializeTrajectoryInto_MissingHeader_BackfillsVersionAndRecordingId()
        {
            var rec = new Recording
            {
                RecordingId = "missing-header",
                RecordingFormatVersion = 1
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100, latitude = 0, longitude = 0, altitude = 100, bodyName = "Kerbin" });

            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            Assert.Equal("1", node.GetValue("version"));
            Assert.Equal("missing-header", node.GetValue("recordingId"));
        }

        [Fact]
        public void SerializeTrajectoryInto_ExistingHeader_IsPreserved()
        {
            var rec = new Recording
            {
                RecordingId = "preserve-header",
                RecordingFormatVersion = 1
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100, latitude = 0, longitude = 0, altitude = 100, bodyName = "Kerbin" });

            var node = new ConfigNode("TEST");
            node.AddValue("version", "42");
            node.AddValue("recordingId", "custom-id");
            RecordingStore.SerializeTrajectoryInto(node, rec);

            Assert.Equal("42", node.GetValue("version"));
            Assert.Equal("custom-id", node.GetValue("recordingId"));
        }

        #endregion

        #region V1 section-authoritative read path

        [Fact]
        public void DeserializeTrajectoryFrom_V1WithTrackSections_RebuildsPointsWithoutBoundaryDupes()
        {
            var fixture = RecordingStorageFixtures.AtmosphericActiveMultiSection();
            var rec = new Recording { RecordingId = fixture.Builder.GetRecordingId() };

            RecordingStore.DeserializeTrajectoryFrom(fixture.Builder.BuildTrajectoryNode(), rec);

            Assert.Equal(5, rec.Points.Count);
            Assert.Equal(2, rec.TrackSections.Count);
            Assert.Equal(17020.0, rec.Points[2].ut);
            Assert.Equal(17030.0, rec.Points[3].ut);

            int sectionFrameCount = rec.TrackSections.Sum(ts => ts.frames.Count);
            Assert.Equal(6, sectionFrameCount);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("DeserializeTrajectoryFrom") &&
                l.Contains("section-authoritative path") &&
                l.Contains("dedupedPointCopies=1"));
        }

        [Fact]
        public void DeserializeTrajectoryFrom_V1WithTrackSections_RebuildsOrbitSegmentsFromCheckpoints()
        {
            var fixture = RecordingStorageFixtures.OrbitalCheckpointTransition();
            var rec = new Recording { RecordingId = fixture.Builder.GetRecordingId() };

            RecordingStore.DeserializeTrajectoryFrom(fixture.Builder.BuildTrajectoryNode(), rec);

            Assert.Equal(3, rec.Points.Count);
            Assert.Single(rec.OrbitSegments);
            Assert.Equal(2, rec.TrackSections.Count);
            Assert.Equal(18320.0, rec.OrbitSegments[0].startUT);
            Assert.Equal(19820.0, rec.OrbitSegments[0].endUT);
        }

        [Fact]
        public void DeserializeTrajectoryFrom_V1WithoutTrackSections_UsesFlatFallbackPath()
        {
            var node = new ConfigNode("PARSEK_RECORDING");
            node.AddValue("version", "1");
            node.AddValue("recordingId", "flat_v1");
            AddMinimalPoint(node, 17000.0);
            AddMinimalPoint(node, 17010.0);

            var rec = new Recording { RecordingId = "flat_v1" };
            RecordingStore.DeserializeTrajectoryFrom(node, rec);

            Assert.Equal(2, rec.Points.Count);
            Assert.Empty(rec.TrackSections);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") &&
                l.Contains("DeserializeTrajectoryFrom") &&
                l.Contains("used flat fallback path"));
        }

        [Fact]
        public void DeserializeTrajectoryFrom_InvalidVersion_LogsWarningAndTreatsAsV0()
        {
            var node = new ConfigNode("PARSEK_RECORDING");
            node.AddValue("version", "bogus");
            node.AddValue("recordingId", "bad_version");
            AddMinimalPoint(node, 17000.0);

            var rec = new Recording { RecordingId = "bad_version" };
            RecordingStore.DeserializeTrajectoryFrom(node, rec);

            Assert.Single(rec.Points);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[RecordingStore]") &&
                l.Contains("invalid trajectory version"));
        }

        #endregion

        #region Helpers

        private static void AddMinimalPoint(ConfigNode parent, double ut)
        {
            var pt = parent.AddNode("POINT");
            pt.AddValue("ut", ut.ToString("R", CultureInfo.InvariantCulture));
            pt.AddValue("lat", "0");
            pt.AddValue("lon", "0");
            pt.AddValue("alt", "100");
            pt.AddValue("rotX", "0");
            pt.AddValue("rotY", "0");
            pt.AddValue("rotZ", "0");
            pt.AddValue("rotW", "1");
            pt.AddValue("body", "Kerbin");
            pt.AddValue("velX", "0");
            pt.AddValue("velY", "0");
            pt.AddValue("velZ", "0");
            pt.AddValue("funds", "0");
            pt.AddValue("science", "0");
            pt.AddValue("rep", "0");
        }

        #endregion
    }
}
