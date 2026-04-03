using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for recording format version and the TrackSections playback gate.
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
        public void CurrentRecordingFormatVersion_Is0()
        {
            Assert.Equal(0, RecordingStore.CurrentRecordingFormatVersion);
        }

        [Fact]
        public void NewRecording_DefaultsToCurrentVersion()
        {
            var rec = new Recording();
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, rec.RecordingFormatVersion);
        }

        #endregion

        #region Recording with TrackSections loads correctly

        [Fact]
        public void Recording_WithTrackSections_LoadsPopulatedTrackSections()
        {
            var precNode = new ConfigNode("PARSEK_RECORDING");
            precNode.AddValue("version", "0");
            precNode.AddValue("recordingId", "track_test");

            AddMinimalPoint(precNode, 17000.0);

            var tsNode = precNode.AddNode("TRACK_SECTION");
            tsNode.AddValue("env", "0");  // Atmospheric
            tsNode.AddValue("ref", "0");  // Absolute
            tsNode.AddValue("startUT", "17000");
            tsNode.AddValue("endUT", "17050");
            tsNode.AddValue("sampleRate", "10");

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

            var seNode = precNode.AddNode("SEGMENT_EVENT");
            seNode.AddValue("ut", "17030");
            seNode.AddValue("type", "0");  // ControllerChange
            seNode.AddValue("details", "stage 1 sep");

            var rec = new Recording();
            rec.RecordingId = "track_test";
            RecordingStore.DeserializeTrajectoryFrom(precNode, rec);

            Assert.Single(rec.TrackSections);
            Assert.Equal(SegmentEnvironment.Atmospheric, rec.TrackSections[0].environment);
            Assert.Equal(ReferenceFrame.Absolute, rec.TrackSections[0].referenceFrame);
            Assert.Equal(17000.0, rec.TrackSections[0].startUT);
            Assert.Equal(17050.0, rec.TrackSections[0].endUT);
            Assert.Single(rec.TrackSections[0].frames);

            Assert.Single(rec.SegmentEvents);
            Assert.Equal(17030.0, rec.SegmentEvents[0].ut);
            Assert.Equal(SegmentEventType.ControllerChange, rec.SegmentEvents[0].type);
            Assert.Equal("stage 1 sep", rec.SegmentEvents[0].details);
        }

        #endregion

        #region Playback gate: TrackSections.Count > 0

        [Fact]
        public void PlaybackGate_NoTrackSections_False()
        {
            var precNode = new ConfigNode("PARSEK_RECORDING");
            precNode.AddValue("version", "0");
            precNode.AddValue("recordingId", "gate_test");

            AddMinimalPoint(precNode, 17000.0);
            AddMinimalPoint(precNode, 17010.0);

            var rec = new Recording();
            rec.RecordingId = "gate_test";
            RecordingStore.DeserializeTrajectoryFrom(precNode, rec);

            bool useTrackSections = rec.TrackSections.Count > 0;
            Assert.False(useTrackSections);
        }

        [Fact]
        public void PlaybackGate_WithTrackSections_True()
        {
            var precNode = new ConfigNode("PARSEK_RECORDING");
            precNode.AddValue("version", "0");
            precNode.AddValue("recordingId", "gate_test_ts");

            var tsNode = precNode.AddNode("TRACK_SECTION");
            tsNode.AddValue("env", "0");
            tsNode.AddValue("ref", "0");
            tsNode.AddValue("startUT", "17000");
            tsNode.AddValue("endUT", "17100");
            tsNode.AddValue("sampleRate", "10");

            var rec = new Recording();
            rec.RecordingId = "gate_test_ts";
            RecordingStore.DeserializeTrajectoryFrom(precNode, rec);

            bool useTrackSections = rec.TrackSections.Count > 0;
            Assert.True(useTrackSections);
        }

        #endregion

        #region RecordingBuilder version default

        [Fact]
        public void RecordingBuilder_DefaultVersion_Is0()
        {
            var builder = new Parsek.Tests.Generators.RecordingBuilder("TestVessel");
            builder.AddPoint(17000, 0, 0, 100);
            builder.AddPoint(17010, 0, 0, 200);

            var trajectoryNode = builder.BuildTrajectoryNode();
            string version = trajectoryNode.GetValue("version");
            Assert.Equal("0", version);
        }

        [Fact]
        public void RecordingBuilder_WithCustomFormatVersion_WritesCustomVersion()
        {
            var builder = new Parsek.Tests.Generators.RecordingBuilder("TestVessel");
            builder.WithFormatVersion(42);
            builder.AddPoint(17000, 0, 0, 100);
            builder.AddPoint(17010, 0, 0, 200);

            var trajectoryNode = builder.BuildTrajectoryNode();
            string version = trajectoryNode.GetValue("version");
            Assert.Equal("42", version);

            var metadataNode = builder.BuildV3Metadata();
            string metaVersion = metadataNode.GetValue("recordingFormatVersion");
            Assert.Equal("42", metaVersion);
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
