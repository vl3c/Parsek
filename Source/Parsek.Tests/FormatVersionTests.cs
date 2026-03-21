using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for recording format version 6 bump and the backward compatibility
    /// playback gate (TrackSections present in v6, absent in v5).
    /// </summary>
    [Collection("Sequential")]
    public class FormatVersionTests
    {
        public FormatVersionTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;
        }

        #region Version constants

        [Fact]
        public void CurrentRecordingFormatVersion_Is7()
        {
            Assert.Equal(7, RecordingStore.CurrentRecordingFormatVersion);
        }

        [Fact]
        public void NewRecording_DefaultsToCurrentVersion()
        {
            var rec = new Recording();
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, rec.RecordingFormatVersion);
        }

        #endregion

        #region v5 backward compat: loads with empty TrackSections and SegmentEvents

        [Fact]
        public void V5Recording_PointsOnly_LoadsWithEmptyTrackSectionsAndSegmentEvents()
        {
            // Simulate a v5 .prec file: POINT nodes only, no TRACK_SECTION or SEGMENT_EVENT
            var precNode = new ConfigNode("PARSEK_RECORDING");
            precNode.AddValue("version", "5");
            precNode.AddValue("recordingId", "v5_legacy_test");

            AddMinimalPoint(precNode, 17000.0);
            AddMinimalPoint(precNode, 17010.0);
            AddMinimalPoint(precNode, 17020.0);

            var rec = new Recording();
            rec.RecordingId = "v5_legacy_test";
            rec.RecordingFormatVersion = 5;
            RecordingStore.DeserializeTrajectoryFrom(precNode, rec);

            // Points loaded normally
            Assert.Equal(3, rec.Points.Count);
            Assert.Equal(17000.0, rec.Points[0].ut);
            Assert.Equal(17020.0, rec.Points[2].ut);

            // No TrackSections or SegmentEvents in v5
            Assert.Empty(rec.TrackSections);
            Assert.Empty(rec.SegmentEvents);
        }

        #endregion

        #region v6 recording: loads with populated TrackSections

        [Fact]
        public void V6Recording_WithTrackSections_LoadsPopulatedTrackSections()
        {
            var precNode = new ConfigNode("PARSEK_RECORDING");
            precNode.AddValue("version", "6");
            precNode.AddValue("recordingId", "v6_track_test");

            // Legacy points (may coexist with TrackSections during transition)
            AddMinimalPoint(precNode, 17000.0);

            // Add a TRACK_SECTION node
            var tsNode = precNode.AddNode("TRACK_SECTION");
            tsNode.AddValue("env", "0");  // Atmospheric
            tsNode.AddValue("ref", "0");  // Absolute
            tsNode.AddValue("startUT", "17000");
            tsNode.AddValue("endUT", "17050");
            tsNode.AddValue("sampleRate", "10");

            // Add a frame inside the track section
            var frameNode = tsNode.AddNode("POINT");
            frameNode.AddValue("ut", "17000");
            frameNode.AddValue("lat", "-0.097");
            frameNode.AddValue("lon", "-74.558");
            frameNode.AddValue("alt", "70");
            frameNode.AddValue("rotX", "0"); frameNode.AddValue("rotY", "0");
            frameNode.AddValue("rotZ", "0"); frameNode.AddValue("rotW", "1");
            frameNode.AddValue("body", "Kerbin");
            frameNode.AddValue("velX", "0"); frameNode.AddValue("velY", "5");
            frameNode.AddValue("velZ", "0");
            frameNode.AddValue("funds", "0"); frameNode.AddValue("science", "0");
            frameNode.AddValue("rep", "0");

            // Add a SEGMENT_EVENT
            var seNode = precNode.AddNode("SEGMENT_EVENT");
            seNode.AddValue("ut", "17030");
            seNode.AddValue("type", "0");  // ControllerChange
            seNode.AddValue("details", "stage 1 sep");

            var rec = new Recording();
            rec.RecordingId = "v6_track_test";
            rec.RecordingFormatVersion = 6;
            RecordingStore.DeserializeTrajectoryFrom(precNode, rec);

            // TrackSections populated
            Assert.Single(rec.TrackSections);
            Assert.Equal(SegmentEnvironment.Atmospheric, rec.TrackSections[0].environment);
            Assert.Equal(ReferenceFrame.Absolute, rec.TrackSections[0].referenceFrame);
            Assert.Equal(17000.0, rec.TrackSections[0].startUT);
            Assert.Equal(17050.0, rec.TrackSections[0].endUT);
            Assert.Single(rec.TrackSections[0].frames);

            // SegmentEvents populated
            Assert.Single(rec.SegmentEvents);
            Assert.Equal(17030.0, rec.SegmentEvents[0].ut);
            Assert.Equal(SegmentEventType.ControllerChange, rec.SegmentEvents[0].type);
            Assert.Equal("stage 1 sep", rec.SegmentEvents[0].details);
        }

        #endregion

        #region Playback gate: TrackSections.Count distinguishes v5 from v6

        [Fact]
        public void PlaybackGate_V5Recording_TrackSectionsEmpty()
        {
            // Build a v5 recording with only Points
            var precNode = new ConfigNode("PARSEK_RECORDING");
            precNode.AddValue("version", "5");
            precNode.AddValue("recordingId", "v5_gate_test");

            AddMinimalPoint(precNode, 17000.0);
            AddMinimalPoint(precNode, 17010.0);

            var rec = new Recording();
            rec.RecordingId = "v5_gate_test";
            rec.RecordingFormatVersion = 5;
            RecordingStore.DeserializeTrajectoryFrom(precNode, rec);

            // Playback gate: v5 recordings have no TrackSections
            bool useTrackSections = rec.TrackSections.Count > 0;
            Assert.False(useTrackSections);
        }

        [Fact]
        public void PlaybackGate_V6Recording_WithTrackSections_True()
        {
            // Build a v6 recording with a TrackSection
            var precNode = new ConfigNode("PARSEK_RECORDING");
            precNode.AddValue("version", "6");
            precNode.AddValue("recordingId", "v6_gate_test");

            var tsNode = precNode.AddNode("TRACK_SECTION");
            tsNode.AddValue("env", "0");
            tsNode.AddValue("ref", "0");
            tsNode.AddValue("startUT", "17000");
            tsNode.AddValue("endUT", "17100");
            tsNode.AddValue("sampleRate", "10");

            var rec = new Recording();
            rec.RecordingId = "v6_gate_test";
            rec.RecordingFormatVersion = 6;
            RecordingStore.DeserializeTrajectoryFrom(precNode, rec);

            // Playback gate: v6 recordings have TrackSections
            bool useTrackSections = rec.TrackSections.Count > 0;
            Assert.True(useTrackSections);
        }

        [Fact]
        public void PlaybackGate_V5HasNoTrackSections_V6Has()
        {
            // Verify that v5 recordings have no TrackSections, v6 recordings do
            var v5Rec = new Recording();
            v5Rec.RecordingFormatVersion = 5;
            Assert.Empty(v5Rec.TrackSections);

            var v6Rec = new Recording();
            v6Rec.RecordingFormatVersion = 6;
            v6Rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 17000,
                endUT = 17100,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>()
            });
            Assert.True(v6Rec.TrackSections.Count > 0);
        }

        #endregion

        #region RecordingBuilder version default

        [Fact]
        public void RecordingBuilder_DefaultVersion_Is7()
        {
            var builder = new Parsek.Tests.Generators.RecordingBuilder("TestVessel");
            builder.AddPoint(17000, 0, 0, 100);
            builder.AddPoint(17010, 0, 0, 200);

            var trajectoryNode = builder.BuildTrajectoryNode();
            string version = trajectoryNode.GetValue("version");
            Assert.Equal("7", version);
        }

        [Fact]
        public void RecordingBuilder_WithFormatVersion5_WritesVersion5()
        {
            var builder = new Parsek.Tests.Generators.RecordingBuilder("TestVessel");
            builder.WithFormatVersion(5);
            builder.AddPoint(17000, 0, 0, 100);
            builder.AddPoint(17010, 0, 0, 200);

            var trajectoryNode = builder.BuildTrajectoryNode();
            string version = trajectoryNode.GetValue("version");
            Assert.Equal("5", version);

            var metadataNode = builder.BuildV3Metadata();
            string metaVersion = metadataNode.GetValue("recordingFormatVersion");
            Assert.Equal("5", metaVersion);
        }

        #endregion

        #region Helpers

        private static void AddMinimalPoint(ConfigNode parent, double ut)
        {
            var pt = parent.AddNode("POINT");
            pt.AddValue("ut", ut.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
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
