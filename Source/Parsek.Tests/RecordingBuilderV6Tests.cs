using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Tests.Generators;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for v6 features added to RecordingBuilder:
    /// TrackSections, SegmentEvents, ControllerInfo, and IsDebris.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingBuilderV6Tests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecordingBuilderV6Tests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        #region TrackSection builder methods

        [Fact]
        public void AddTrackSection_SerializeDeserialize_RoundTrip()
        {
            var frames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = 17000.0, latitude = -0.097, longitude = -74.558, altitude = 70,
                    rotation = new Quaternion(0, 0, 0, 1), bodyName = "Kerbin",
                    velocity = new Vector3(0, 5f, 0)
                },
                new TrajectoryPoint
                {
                    ut = 17010.0, latitude = -0.097, longitude = -74.558, altitude = 200,
                    rotation = new Quaternion(0, 0, 0, 1), bodyName = "Kerbin",
                    velocity = new Vector3(0, 50f, 0)
                }
            };

            var builder = new RecordingBuilder("TrackSectionTest")
                .AddPoint(17000, -0.097, -74.558, 70)
                .AddTrackSection(
                    SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, TrackSectionSource.Active,
                    17000.0, 17010.0,
                    frames: frames, sampleRateHz: 10.0f);

            var trajNode = builder.BuildTrajectoryNode();

            // Deserialize into a Recording and verify
            var rec = new Recording();
            RecordingStore.DeserializeTrajectoryFrom(trajNode, rec);

            Assert.Single(rec.TrackSections);
            var section = rec.TrackSections[0];
            Assert.Equal(SegmentEnvironment.Atmospheric, section.environment);
            Assert.Equal(ReferenceFrame.Absolute, section.referenceFrame);
            Assert.Equal(TrackSectionSource.Active, section.source);
            Assert.Equal(17000.0, section.startUT);
            Assert.Equal(17010.0, section.endUT);
            Assert.Equal(10.0f, section.sampleRateHz);
            Assert.Equal(2, section.frames.Count);
            Assert.Equal(17000.0, section.frames[0].ut);
            Assert.Equal(17010.0, section.frames[1].ut);
        }

        [Fact]
        public void AddTrackSection_WithBackground_PreservesFlag()
        {
            var builder = new RecordingBuilder("BgTest")
                .AddTrackSection(
                    SegmentEnvironment.SurfaceStationary, ReferenceFrame.Absolute, TrackSectionSource.Background,
                    15000.0, 16000.0,
                    sampleRateHz: 0.5f);

            var trajNode = builder.BuildTrajectoryNode();
            var rec = new Recording();
            RecordingStore.DeserializeTrajectoryFrom(trajNode, rec);

            Assert.Single(rec.TrackSections);
            Assert.Equal(TrackSectionSource.Background, rec.TrackSections[0].source);
        }

        [Fact]
        public void AddTrackSection_WithAnchorVesselId_Preserved()
        {
            var builder = new RecordingBuilder("AnchorTest")
                .AddTrackSection(
                    SegmentEnvironment.ExoPropulsive, ReferenceFrame.Relative, TrackSectionSource.Active,
                    17500.0, 17600.0,
                    anchorVesselId: 42u, sampleRateHz: 5.0f);

            var trajNode = builder.BuildTrajectoryNode();
            var rec = new Recording();
            RecordingStore.DeserializeTrajectoryFrom(trajNode, rec);

            Assert.Single(rec.TrackSections);
            Assert.Equal(42u, rec.TrackSections[0].anchorVesselId);
        }

        [Fact]
        public void AddMultipleTrackSections_OrderPreserved()
        {
            var builder = new RecordingBuilder("MultiSection")
                .AddTrackSection(
                    SegmentEnvironment.SurfaceStationary, ReferenceFrame.Absolute, TrackSectionSource.Active,
                    16000.0, 17000.0)
                .AddTrackSection(
                    SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, TrackSectionSource.Active,
                    17000.0, 17300.0)
                .AddTrackSection(
                    SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, TrackSectionSource.Checkpoint,
                    17300.0, 20000.0);

            var trajNode = builder.BuildTrajectoryNode();
            var rec = new Recording();
            RecordingStore.DeserializeTrajectoryFrom(trajNode, rec);

            Assert.Equal(3, rec.TrackSections.Count);
            Assert.Equal(SegmentEnvironment.SurfaceStationary, rec.TrackSections[0].environment);
            Assert.Equal(SegmentEnvironment.Atmospheric, rec.TrackSections[1].environment);
            Assert.Equal(SegmentEnvironment.ExoBallistic, rec.TrackSections[2].environment);
            Assert.Equal(16000.0, rec.TrackSections[0].startUT);
            Assert.Equal(17000.0, rec.TrackSections[1].startUT);
            Assert.Equal(17300.0, rec.TrackSections[2].startUT);
        }

        #endregion

        #region Convenience: AddAtmosphericSection

        [Fact]
        public void AddAtmosphericSection_CreatesCorrectDefaults()
        {
            var builder = new RecordingBuilder("AtmoConvenience")
                .AddAtmosphericSection(17000.0, 17100.0);

            var sections = builder.GetTrackSections();
            Assert.Single(sections);
            var section = sections[0];
            Assert.Equal(SegmentEnvironment.Atmospheric, section.environment);
            Assert.Equal(ReferenceFrame.Absolute, section.referenceFrame);
            Assert.Equal(TrackSectionSource.Active, section.source);
            Assert.Equal(17000.0, section.startUT);
            Assert.Equal(17100.0, section.endUT);
            Assert.Equal(10.0f, section.sampleRateHz);
            Assert.Empty(section.frames);
            Assert.Empty(section.checkpoints);
        }

        [Fact]
        public void AddAtmosphericSection_SerializeDeserialize_RoundTrip()
        {
            var builder = new RecordingBuilder("AtmoRoundTrip")
                .AddAtmosphericSection(17000.0, 17100.0);

            var trajNode = builder.BuildTrajectoryNode();
            var rec = new Recording();
            RecordingStore.DeserializeTrajectoryFrom(trajNode, rec);

            Assert.Single(rec.TrackSections);
            Assert.Equal(SegmentEnvironment.Atmospheric, rec.TrackSections[0].environment);
            Assert.Equal(ReferenceFrame.Absolute, rec.TrackSections[0].referenceFrame);
        }

        #endregion

        #region Convenience: AddOrbitalCheckpointSection

        [Fact]
        public void AddOrbitalCheckpointSection_IncludesCheckpoint()
        {
            var checkpoint = new OrbitSegment
            {
                startUT = 18000.0, endUT = 20000.0,
                inclination = 28.5, eccentricity = 0.01, semiMajorAxis = 700000,
                longitudeOfAscendingNode = 90, argumentOfPeriapsis = 45,
                meanAnomalyAtEpoch = 0.5, epoch = 18000,
                bodyName = "Kerbin"
            };

            var builder = new RecordingBuilder("OrbitalCheckpoint")
                .AddOrbitalCheckpointSection(18000.0, 20000.0, checkpoint);

            var sections = builder.GetTrackSections();
            Assert.Single(sections);
            var section = sections[0];
            Assert.Equal(SegmentEnvironment.ExoBallistic, section.environment);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, section.referenceFrame);
            Assert.Equal(TrackSectionSource.Checkpoint, section.source);
            Assert.Single(section.checkpoints);
            Assert.Equal(28.5, section.checkpoints[0].inclination);
            Assert.Equal(700000, section.checkpoints[0].semiMajorAxis);
        }

        [Fact]
        public void AddOrbitalCheckpointSection_SerializeDeserialize_RoundTrip()
        {
            var checkpoint = new OrbitSegment
            {
                startUT = 18000.0, endUT = 20000.0,
                inclination = 28.5, eccentricity = 0.01, semiMajorAxis = 700000,
                longitudeOfAscendingNode = 90, argumentOfPeriapsis = 45,
                meanAnomalyAtEpoch = 0.5, epoch = 18000,
                bodyName = "Kerbin"
            };

            var builder = new RecordingBuilder("OrbitalRoundTrip")
                .AddOrbitalCheckpointSection(18000.0, 20000.0, checkpoint);

            var trajNode = builder.BuildTrajectoryNode();
            var rec = new Recording();
            RecordingStore.DeserializeTrajectoryFrom(trajNode, rec);

            Assert.Single(rec.TrackSections);
            var section = rec.TrackSections[0];
            Assert.Equal(SegmentEnvironment.ExoBallistic, section.environment);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, section.referenceFrame);
            Assert.Single(section.checkpoints);
            Assert.Equal(28.5, section.checkpoints[0].inclination);
            Assert.Equal(0.01, section.checkpoints[0].eccentricity);
            Assert.Equal(700000, section.checkpoints[0].semiMajorAxis);
        }

        #endregion

        #region SegmentEvent builder methods

        [Fact]
        public void AddSegmentEvent_SerializeDeserialize_RoundTrip()
        {
            var builder = new RecordingBuilder("SegEventTest")
                .AddSegmentEvent(SegmentEventType.ControllerChange, 17050.0, "switched to probe core")
                .AddSegmentEvent(SegmentEventType.PartDestroyed, 17080.0, "heatShield:100123")
                .AddSegmentEvent(SegmentEventType.CrewLost, 17090.0, null);

            var trajNode = builder.BuildTrajectoryNode();
            var rec = new Recording();
            RecordingStore.DeserializeTrajectoryFrom(trajNode, rec);

            Assert.Equal(3, rec.SegmentEvents.Count);

            Assert.Equal(SegmentEventType.ControllerChange, rec.SegmentEvents[0].type);
            Assert.Equal(17050.0, rec.SegmentEvents[0].ut);
            Assert.Equal("switched to probe core", rec.SegmentEvents[0].details);

            Assert.Equal(SegmentEventType.PartDestroyed, rec.SegmentEvents[1].type);
            Assert.Equal(17080.0, rec.SegmentEvents[1].ut);
            Assert.Equal("heatShield:100123", rec.SegmentEvents[1].details);

            Assert.Equal(SegmentEventType.CrewLost, rec.SegmentEvents[2].type);
            Assert.Equal(17090.0, rec.SegmentEvents[2].ut);
            Assert.Null(rec.SegmentEvents[2].details);
        }

        [Fact]
        public void AddControllerChangeEvent_CreatesCorrectEvent()
        {
            var builder = new RecordingBuilder("CtrlChangeTest")
                .AddControllerChangeEvent(17050.0, "switched from mk1pod to probeCoreOcto");

            var events = builder.GetSegmentEvents();
            Assert.Single(events);
            Assert.Equal(SegmentEventType.ControllerChange, events[0].type);
            Assert.Equal(17050.0, events[0].ut);
            Assert.Equal("switched from mk1pod to probeCoreOcto", events[0].details);
        }

        [Fact]
        public void AddPartDestroyedEvent_CreatesCorrectEvent()
        {
            var builder = new RecordingBuilder("PartDestroyedTest")
                .AddPartDestroyedEvent(17080.0, "heatShield3", 100123u);

            var events = builder.GetSegmentEvents();
            Assert.Single(events);
            Assert.Equal(SegmentEventType.PartDestroyed, events[0].type);
            Assert.Equal(17080.0, events[0].ut);
            Assert.Equal("heatShield3:100123", events[0].details);
        }

        [Fact]
        public void AddPartDestroyedEvent_SerializeDeserialize_RoundTrip()
        {
            var builder = new RecordingBuilder("PartDestroyedRoundTrip")
                .AddPartDestroyedEvent(17080.0, "noseCone", 200456u);

            var trajNode = builder.BuildTrajectoryNode();
            var rec = new Recording();
            RecordingStore.DeserializeTrajectoryFrom(trajNode, rec);

            Assert.Single(rec.SegmentEvents);
            Assert.Equal(SegmentEventType.PartDestroyed, rec.SegmentEvents[0].type);
            Assert.Equal(17080.0, rec.SegmentEvents[0].ut);
            Assert.Equal("noseCone:200456", rec.SegmentEvents[0].details);
        }

        #endregion

        #region ControllerInfo builder methods

        [Fact]
        public void WithControllers_BuildV3Metadata_ContainsControllerNodes()
        {
            var builder = new RecordingBuilder("ControllerTest")
                .WithControllers(
                    new ControllerInfo { type = "CrewedPod", partName = "mk1pod.v2", partPersistentId = 100000 },
                    new ControllerInfo { type = "ProbeCore", partName = "probeCoreOcto", partPersistentId = 100111 });

            var metadata = builder.BuildV3Metadata();

            var ctrlNodes = metadata.GetNodes("CONTROLLER");
            Assert.Equal(2, ctrlNodes.Length);

            Assert.Equal("CrewedPod", ctrlNodes[0].GetValue("type"));
            Assert.Equal("mk1pod.v2", ctrlNodes[0].GetValue("part"));
            Assert.Equal("100000", ctrlNodes[0].GetValue("pid"));

            Assert.Equal("ProbeCore", ctrlNodes[1].GetValue("type"));
            Assert.Equal("probeCoreOcto", ctrlNodes[1].GetValue("part"));
            Assert.Equal("100111", ctrlNodes[1].GetValue("pid"));
        }

        [Fact]
        public void AddController_BuildV3Metadata_ContainsControllerNode()
        {
            var builder = new RecordingBuilder("AddControllerTest")
                .AddController("CrewedPod", "mk1pod.v2", 100000u);

            var metadata = builder.BuildV3Metadata();

            var ctrlNodes = metadata.GetNodes("CONTROLLER");
            Assert.Single(ctrlNodes);
            Assert.Equal("CrewedPod", ctrlNodes[0].GetValue("type"));
            Assert.Equal("mk1pod.v2", ctrlNodes[0].GetValue("part"));
            Assert.Equal("100000", ctrlNodes[0].GetValue("pid"));
        }

        [Fact]
        public void AddController_MultipleAdds_AccumulatesControllers()
        {
            var builder = new RecordingBuilder("MultiControllerTest")
                .AddController("CrewedPod", "mk1pod.v2", 100000u)
                .AddController("ProbeCore", "probeCoreOcto", 100111u);

            var metadata = builder.BuildV3Metadata();

            var ctrlNodes = metadata.GetNodes("CONTROLLER");
            Assert.Equal(2, ctrlNodes.Length);
        }

        [Fact]
        public void WithControllers_Build_ContainsControllerNodes()
        {
            // Test v2-format Build() also includes controllers
            var builder = new RecordingBuilder("ControllerBuildTest")
                .AddController("KerbalEVA", "kerbalEVA", 100000u);

            var node = builder.Build();

            var ctrlNodes = node.GetNodes("CONTROLLER");
            Assert.Single(ctrlNodes);
            Assert.Equal("KerbalEVA", ctrlNodes[0].GetValue("type"));
        }

        [Fact]
        public void NoControllers_BuildV3Metadata_NoControllerNodes()
        {
            var builder = new RecordingBuilder("NoControllerTest");

            var metadata = builder.BuildV3Metadata();

            var ctrlNodes = metadata.GetNodes("CONTROLLER");
            Assert.Empty(ctrlNodes);
        }

        #endregion

        #region AsDebris

        [Fact]
        public void AsDebris_BuildV3Metadata_IsDebrisTrue()
        {
            var builder = new RecordingBuilder("DebrisTest")
                .AsDebris();

            var metadata = builder.BuildV3Metadata();

            Assert.Equal("True", metadata.GetValue("isDebris"));
        }

        [Fact]
        public void AsDebris_Build_IsDebrisTrue()
        {
            var builder = new RecordingBuilder("DebrisBuildTest")
                .AsDebris();

            var node = builder.Build();

            Assert.Equal("True", node.GetValue("isDebris"));
        }

        [Fact]
        public void NotDebris_BuildV3Metadata_NoIsDebrisKey()
        {
            var builder = new RecordingBuilder("NotDebrisTest");

            var metadata = builder.BuildV3Metadata();

            Assert.Null(metadata.GetValue("isDebris"));
        }

        #endregion

        #region Combined: full round-trip with Points + TrackSections + SegmentEvents + Controllers

        [Fact]
        public void FullV6Recording_BuildTrajectoryNode_CompleteRoundTrip()
        {
            var frames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = 17000.0, latitude = -0.097, longitude = -74.558, altitude = 70,
                    rotation = new Quaternion(0, 0, 0, 1), bodyName = "Kerbin",
                    velocity = new Vector3(0, 5f, 0)
                },
                new TrajectoryPoint
                {
                    ut = 17050.0, latitude = -0.095, longitude = -74.556, altitude = 2000,
                    rotation = new Quaternion(0, 0, 0, 1), bodyName = "Kerbin",
                    velocity = new Vector3(0, 200f, 0)
                }
            };

            var orbCheckpoint = new OrbitSegment
            {
                startUT = 17300.0, endUT = 20000.0,
                inclination = 28.5, eccentricity = 0.01, semiMajorAxis = 700000,
                longitudeOfAscendingNode = 90, argumentOfPeriapsis = 45,
                meanAnomalyAtEpoch = 0.5, epoch = 17300,
                bodyName = "Kerbin"
            };

            var builder = new RecordingBuilder("FullV6Test")
                // Legacy points
                .AddPoint(17000, -0.097, -74.558, 70)
                .AddPoint(17050, -0.095, -74.556, 2000)
                // Track sections
                .AddTrackSection(
                    SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, TrackSectionSource.Active,
                    17000.0, 17050.0, frames: frames, sampleRateHz: 10.0f)
                .AddOrbitalCheckpointSection(17300.0, 20000.0, orbCheckpoint)
                // Segment events
                .AddControllerChangeEvent(17050.0, "stage 1 sep")
                .AddPartDestroyedEvent(17080.0, "heatShield", 100123u)
                .AddSegmentEvent(SegmentEventType.CrewTransfer, 17090.0, "Jeb -> capsule");

            var trajNode = builder.BuildTrajectoryNode();

            // Deserialize
            var rec = new Recording();
            RecordingStore.DeserializeTrajectoryFrom(trajNode, rec);

            // Legacy points
            Assert.Equal(2, rec.Points.Count);

            // Track sections
            Assert.Equal(2, rec.TrackSections.Count);
            Assert.Equal(SegmentEnvironment.Atmospheric, rec.TrackSections[0].environment);
            Assert.Equal(ReferenceFrame.Absolute, rec.TrackSections[0].referenceFrame);
            Assert.Equal(2, rec.TrackSections[0].frames.Count);
            Assert.Equal(SegmentEnvironment.ExoBallistic, rec.TrackSections[1].environment);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, rec.TrackSections[1].referenceFrame);
            Assert.Single(rec.TrackSections[1].checkpoints);

            // Segment events
            Assert.Equal(3, rec.SegmentEvents.Count);
            Assert.Equal(SegmentEventType.ControllerChange, rec.SegmentEvents[0].type);
            Assert.Equal(SegmentEventType.PartDestroyed, rec.SegmentEvents[1].type);
            Assert.Equal(SegmentEventType.CrewTransfer, rec.SegmentEvents[2].type);
        }

        [Fact]
        public void FullV6Recording_BuildV3Metadata_IncludesAllV6Fields()
        {
            var builder = new RecordingBuilder("FullV6Metadata")
                .AddPoint(17000, -0.097, -74.558, 70)
                .AddController("CrewedPod", "mk1pod.v2", 100000u)
                .AddController("ProbeCore", "probeCoreOcto", 100111u)
                .AsDebris()
                .AddAtmosphericSection(17000.0, 17100.0)
                .AddSegmentEvent(SegmentEventType.ControllerChange, 17050.0, "ctrl change");

            var metadata = builder.BuildV3Metadata();

            // Controllers
            var ctrlNodes = metadata.GetNodes("CONTROLLER");
            Assert.Equal(2, ctrlNodes.Length);

            // IsDebris
            Assert.Equal("True", metadata.GetValue("isDebris"));

            // Format version
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion.ToString(CultureInfo.InvariantCulture),
                metadata.GetValue("recordingFormatVersion"));
        }

        [Fact]
        public void FullV6Recording_Build_IncludesSegmentEventsAndTrackSections()
        {
            var builder = new RecordingBuilder("FullV6Build")
                .AddPoint(17000, -0.097, -74.558, 70)
                .AddAtmosphericSection(17000.0, 17100.0)
                .AddSegmentEvent(SegmentEventType.PartAdded, 17050.0, "docking port")
                .AddController("CrewedPod", "mk1pod.v2", 100000u);

            var node = builder.Build();

            // Verify TRACK_SECTION nodes present
            Assert.Single(node.GetNodes("TRACK_SECTION"));

            // Verify SEGMENT_EVENT nodes present
            Assert.Single(node.GetNodes("SEGMENT_EVENT"));

            // Verify CONTROLLER nodes present
            Assert.Single(node.GetNodes("CONTROLLER"));
        }

        #endregion

        #region GetTrackSections / GetSegmentEvents accessors

        [Fact]
        public void GetTrackSections_ReturnsBuiltSections()
        {
            var builder = new RecordingBuilder("AccessorTest")
                .AddAtmosphericSection(17000.0, 17100.0)
                .AddAtmosphericSection(17100.0, 17200.0);

            var sections = builder.GetTrackSections();
            Assert.Equal(2, sections.Count);
            Assert.Equal(17000.0, sections[0].startUT);
            Assert.Equal(17100.0, sections[1].startUT);
        }

        [Fact]
        public void GetSegmentEvents_ReturnsBuiltEvents()
        {
            var builder = new RecordingBuilder("EventAccessorTest")
                .AddSegmentEvent(SegmentEventType.ControllerEnabled, 500.0, "power restored")
                .AddSegmentEvent(SegmentEventType.ControllerDisabled, 600.0, "power lost");

            var events = builder.GetSegmentEvents();
            Assert.Equal(2, events.Count);
            Assert.Equal(500.0, events[0].ut);
            Assert.Equal(600.0, events[1].ut);
        }

        #endregion

        #region Empty collections: no nodes emitted

        [Fact]
        public void NoTrackSections_BuildTrajectoryNode_NoTrackSectionNodes()
        {
            var builder = new RecordingBuilder("EmptyTrackTest")
                .AddPoint(17000, 0, 0, 100);

            var trajNode = builder.BuildTrajectoryNode();

            Assert.Empty(trajNode.GetNodes("TRACK_SECTION"));
        }

        [Fact]
        public void NoSegmentEvents_BuildTrajectoryNode_NoSegmentEventNodes()
        {
            var builder = new RecordingBuilder("EmptyEventTest")
                .AddPoint(17000, 0, 0, 100);

            var trajNode = builder.BuildTrajectoryNode();

            Assert.Empty(trajNode.GetNodes("SEGMENT_EVENT"));
        }

        [Fact]
        public void NoV6Data_Build_NoExtraNodes()
        {
            var builder = new RecordingBuilder("EmptyV6Test")
                .AddPoint(17000, 0, 0, 100);

            var node = builder.Build();

            Assert.Empty(node.GetNodes("TRACK_SECTION"));
            Assert.Empty(node.GetNodes("SEGMENT_EVENT"));
            Assert.Empty(node.GetNodes("CONTROLLER"));
            Assert.Null(node.GetValue("isDebris"));
        }

        #endregion
    }
}
