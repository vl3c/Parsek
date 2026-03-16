using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RecordingFieldExtensionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecordingFieldExtensionTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
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
            MilestoneStore.ResetForTesting();
        }

        // --- ControllerInfo ToString ---

        [Fact]
        public void ControllerInfo_ToString_ReturnsReadableOutput()
        {
            var ctrl = new ControllerInfo
            {
                type = "CrewedPod",
                partName = "mk1pod.v2",
                partPersistentId = 100000
            };

            string result = ctrl.ToString();

            Assert.Contains("CrewedPod", result);
            Assert.Contains("mk1pod.v2", result);
            Assert.Contains("100000", result);
            Assert.Contains("type=", result);
            Assert.Contains("part=", result);
            Assert.Contains("pid=", result);
        }

        // --- Round-trip via RecordingTree: controllers ---

        [Fact]
        public void Controllers_TwoControllers_RoundTripViaSaveLoad()
        {
            var rec = new Recording();
            rec.RecordingId = "test_ctrl_roundtrip";
            rec.Controllers = new List<ControllerInfo>
            {
                new ControllerInfo { type = "CrewedPod", partName = "mk1pod.v2", partPersistentId = 100000 },
                new ControllerInfo { type = "ProbeCore", partName = "probeCoreCube", partPersistentId = 101111 }
            };

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);

            var restored = new Recording();
            RecordingTree.LoadRecordingFrom(node, restored);

            Assert.NotNull(restored.Controllers);
            Assert.Equal(2, restored.Controllers.Count);

            Assert.Equal("CrewedPod", restored.Controllers[0].type);
            Assert.Equal("mk1pod.v2", restored.Controllers[0].partName);
            Assert.Equal(100000u, restored.Controllers[0].partPersistentId);

            Assert.Equal("ProbeCore", restored.Controllers[1].type);
            Assert.Equal("probeCoreCube", restored.Controllers[1].partName);
            Assert.Equal(101111u, restored.Controllers[1].partPersistentId);
        }

        // --- Round-trip: IsDebris ---

        [Fact]
        public void IsDebris_True_SavesAndLoadsCorrectly()
        {
            var rec = new Recording();
            rec.RecordingId = "test_debris_true";
            rec.IsDebris = true;

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);

            var restored = new Recording();
            RecordingTree.LoadRecordingFrom(node, restored);

            Assert.True(restored.IsDebris);
        }

        [Fact]
        public void IsDebris_FalseDefault_NotWrittenToConfigNode()
        {
            var rec = new Recording();
            rec.RecordingId = "test_debris_false";
            rec.IsDebris = false; // default

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);

            // isDebris should NOT be written when false (sparse serialization)
            Assert.Null(node.GetValue("isDebris"));
        }

        // --- Backward compat: no CONTROLLER nodes ---

        [Fact]
        public void BackwardCompat_NoControllerNodes_ControllersIsNull()
        {
            // Simulate a legacy RECORDING node with no CONTROLLER sub-nodes
            var node = new ConfigNode("RECORDING");
            node.AddValue("recordingId", "legacy_rec");
            node.AddValue("vesselName", "LegacyVessel");

            var rec = new Recording();
            RecordingTree.LoadRecordingFrom(node, rec);

            Assert.Null(rec.Controllers);
        }

        // --- Backward compat: no isDebris ---

        [Fact]
        public void BackwardCompat_NoIsDebris_DefaultsFalse()
        {
            var node = new ConfigNode("RECORDING");
            node.AddValue("recordingId", "legacy_no_debris");
            node.AddValue("vesselName", "OldVessel");

            var rec = new Recording();
            RecordingTree.LoadRecordingFrom(node, rec);

            Assert.False(rec.IsDebris);
        }

        // --- ApplyPersistenceArtifactsFrom ---

        [Fact]
        public void ApplyPersistenceArtifactsFrom_CopiesControllersIsDebrisSegmentEventsTracks()
        {
            var source = new Recording();
            source.Controllers = new List<ControllerInfo>
            {
                new ControllerInfo { type = "KerbalEVA", partName = "kerbalEVA", partPersistentId = 55555 }
            };
            source.IsDebris = true;
            source.SegmentEvents = new List<SegmentEvent>
            {
                new SegmentEvent { ut = 100.0, eventType = SegmentEventType.NameChange, data = "NewName" }
            };
            source.Tracks = new List<TrackSection>
            {
                new TrackSection { startIndex = 0, endIndex = 10, environment = TrackEnvironment.Atmosphere, frame = TrackFrame.BodyFixed, bodyName = "Kerbin" }
            };

            var target = new Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            // Controllers copied
            Assert.NotNull(target.Controllers);
            Assert.Single(target.Controllers);
            Assert.Equal("KerbalEVA", target.Controllers[0].type);
            Assert.Equal(55555u, target.Controllers[0].partPersistentId);

            // IsDebris copied
            Assert.True(target.IsDebris);

            // SegmentEvents copied
            Assert.Single(target.SegmentEvents);
            Assert.Equal(SegmentEventType.NameChange, target.SegmentEvents[0].eventType);
            Assert.Equal("NewName", target.SegmentEvents[0].data);

            // Tracks copied
            Assert.Single(target.Tracks);
            Assert.Equal(TrackEnvironment.Atmosphere, target.Tracks[0].environment);
            Assert.Equal("Kerbin", target.Tracks[0].bodyName);
        }

        [Fact]
        public void ApplyPersistenceArtifactsFrom_NullControllers_TargetStaysNull()
        {
            var source = new Recording();
            source.Controllers = null;
            source.IsDebris = false;

            var target = new Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Null(target.Controllers);
            Assert.False(target.IsDebris);
        }

        // --- Empty controllers list ---

        [Fact]
        public void EmptyControllersList_NoControllerNodesWritten()
        {
            var rec = new Recording();
            rec.RecordingId = "test_empty_ctrl";
            rec.Controllers = new List<ControllerInfo>(); // empty, not null

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);

            // Empty list should write zero CONTROLLER nodes
            ConfigNode[] ctrlNodes = node.GetNodes("CONTROLLER");
            Assert.Empty(ctrlNodes);
        }

        // --- Log assertion: saving controllers logs the count ---

        [Fact]
        public void SaveControllers_LogsCount()
        {
            var rec = new Recording();
            rec.RecordingId = "test_ctrl_log";
            rec.Controllers = new List<ControllerInfo>
            {
                new ControllerInfo { type = "CrewedPod", partName = "mk1pod.v2", partPersistentId = 100000 },
                new ControllerInfo { type = "ProbeCore", partName = "probeCoreCube", partPersistentId = 101111 }
            };

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingTree]") &&
                l.Contains("2 controller(s)") &&
                l.Contains("test_ctrl_log"));
        }

        // --- Log assertion: loading controllers logs the count ---

        [Fact]
        public void LoadControllers_LogsCount()
        {
            // First save a recording with controllers
            var rec = new Recording();
            rec.RecordingId = "test_ctrl_load_log";
            rec.Controllers = new List<ControllerInfo>
            {
                new ControllerInfo { type = "ExternalSeat", partName = "externalSeat", partPersistentId = 200000 }
            };

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);

            // Clear log lines, then load
            logLines.Clear();

            var restored = new Recording();
            RecordingTree.LoadRecordingFrom(node, restored);

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingTree]") &&
                l.Contains("1 controller(s)") &&
                l.Contains("test_ctrl_load_log"));
        }
    }
}
