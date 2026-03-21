using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class LoopAnchorTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LoopAnchorTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(null);
        }

        // --- Default value ---

        [Fact]
        public void LoopAnchorVesselId_Default_IsZero()
        {
            var rec = new Recording();
            Assert.Equal(0u, rec.LoopAnchorVesselId);
        }

        // --- ParsekScenario serialization round-trip ---

        [Fact]
        public void LoopAnchorVesselId_Scenario_SaveLoad_RoundTrip()
        {
            var source = new Recording
            {
                RecordingId = "anchor-test",
                LoopPlayback = true,
                LoopAnchorVesselId = 12345,
            };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(12345u, loaded.LoopAnchorVesselId);
        }

        [Fact]
        public void LoopAnchorVesselId_Scenario_Zero_NotWritten()
        {
            var source = new Recording
            {
                RecordingId = "no-anchor",
                LoopAnchorVesselId = 0,
            };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            Assert.Null(node.GetValue("loopAnchorPid"));
        }

        [Fact]
        public void LoopAnchorVesselId_Scenario_BackwardCompat_MissingKey_DefaultsZero()
        {
            var node = new ConfigNode("RECORDING");
            // No loopAnchorPid key at all
            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(0u, loaded.LoopAnchorVesselId);
        }

        // --- RecordingTree serialization round-trip ---

        [Fact]
        public void LoopAnchorVesselId_Tree_SaveLoad_RoundTrip()
        {
            var source = new Recording
            {
                RecordingId = "tree-anchor-test",
                LoopPlayback = true,
                LoopAnchorVesselId = 67890,
            };
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, source);

            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);

            Assert.Equal(67890u, loaded.LoopAnchorVesselId);
        }

        [Fact]
        public void LoopAnchorVesselId_Tree_Zero_NotWritten()
        {
            var source = new Recording
            {
                RecordingId = "tree-no-anchor",
                LoopAnchorVesselId = 0,
            };
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, source);

            Assert.Null(node.GetValue("loopAnchorPid"));
        }

        [Fact]
        public void LoopAnchorVesselId_Tree_BackwardCompat_MissingKey_DefaultsZero()
        {
            var node = new ConfigNode("RECORDING");
            node.AddValue("recordingId", "compat-test");
            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);

            Assert.Equal(0u, loaded.LoopAnchorVesselId);
        }

        // --- ApplyPersistenceArtifactsFrom ---

        [Fact]
        public void ApplyPersistenceArtifacts_CopiesLoopAnchorVesselId()
        {
            var source = new Recording { LoopAnchorVesselId = 99999 };
            var target = new Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal(99999u, target.LoopAnchorVesselId);
        }

        // --- ValidateLoopAnchor ---

        [Fact]
        public void ValidateLoopAnchor_ZeroPid_ReturnsFalse()
        {
            bool result = GhostPlaybackLogic.ValidateLoopAnchor(0);
            Assert.False(result);
            Assert.Contains(logLines, l => l.Contains("[Loop]") && l.Contains("anchorPid=0"));
        }

        [Fact]
        public void ValidateLoopAnchor_VesselExists_ReturnsTrue()
        {
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == 42);

            bool result = GhostPlaybackLogic.ValidateLoopAnchor(42);
            Assert.True(result);
            Assert.Contains(logLines, l => l.Contains("[Loop]") && l.Contains("anchor pid=42 found"));
        }

        [Fact]
        public void ValidateLoopAnchor_VesselMissing_ReturnsFalse()
        {
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => false);

            bool result = GhostPlaybackLogic.ValidateLoopAnchor(777);
            Assert.False(result);
            Assert.Contains(logLines, l => l.Contains("[Loop]") && l.Contains("anchor pid=777 NOT found"));
        }

        [Fact]
        public void ValidateLoopAnchor_VesselMissing_LogsWarning()
        {
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => false);

            GhostPlaybackLogic.ValidateLoopAnchor(555);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("loop anchor broken"));
        }

        // --- ShouldUseLoopAnchor ---

        [Fact]
        public void ShouldUseLoopAnchor_NullRec_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldUseLoopAnchor(null));
        }

        [Fact]
        public void ShouldUseLoopAnchor_ZeroAnchor_ReturnsFalse()
        {
            var rec = new Recording { LoopAnchorVesselId = 0 };
            Assert.False(GhostPlaybackLogic.ShouldUseLoopAnchor(rec));
        }

        [Fact]
        public void ShouldUseLoopAnchor_NoTrackSections_ReturnsFalse()
        {
            var rec = new Recording { LoopAnchorVesselId = 100 };
            rec.TrackSections = new List<TrackSection>();
            Assert.False(GhostPlaybackLogic.ShouldUseLoopAnchor(rec));
        }

        [Fact]
        public void ShouldUseLoopAnchor_OnlyAbsoluteSections_ReturnsFalse()
        {
            var rec = new Recording { LoopAnchorVesselId = 100 };
            rec.TrackSections = new List<TrackSection>
            {
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Absolute,
                    startUT = 100, endUT = 200,
                    frames = new List<TrajectoryPoint>()
                }
            };
            Assert.False(GhostPlaybackLogic.ShouldUseLoopAnchor(rec));
        }

        [Fact]
        public void ShouldUseLoopAnchor_HasRelativeSection_ReturnsTrue()
        {
            var rec = new Recording { LoopAnchorVesselId = 100 };
            rec.TrackSections = new List<TrackSection>
            {
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Relative,
                    startUT = 100, endUT = 200,
                    anchorVesselId = 100,
                    frames = new List<TrajectoryPoint>()
                }
            };
            Assert.True(GhostPlaybackLogic.ShouldUseLoopAnchor(rec));
        }

        [Fact]
        public void ShouldUseLoopAnchor_MixedSections_WithRelative_ReturnsTrue()
        {
            var rec = new Recording { LoopAnchorVesselId = 100 };
            rec.TrackSections = new List<TrackSection>
            {
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Absolute,
                    startUT = 100, endUT = 150,
                    frames = new List<TrajectoryPoint>()
                },
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Relative,
                    startUT = 150, endUT = 200,
                    anchorVesselId = 100,
                    frames = new List<TrajectoryPoint>()
                }
            };
            Assert.True(GhostPlaybackLogic.ShouldUseLoopAnchor(rec));
        }

        [Fact]
        public void ShouldUseLoopAnchor_NullTrackSections_ReturnsFalse()
        {
            var rec = new Recording { LoopAnchorVesselId = 100 };
            rec.TrackSections = null;
            Assert.False(GhostPlaybackLogic.ShouldUseLoopAnchor(rec));
        }

        // --- Serialization with large PID values ---

        [Fact]
        public void LoopAnchorVesselId_Scenario_LargePid_RoundTrip()
        {
            var source = new Recording
            {
                RecordingId = "large-pid",
                LoopAnchorVesselId = 4294967295, // uint.MaxValue
            };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(4294967295u, loaded.LoopAnchorVesselId);
        }

        [Fact]
        public void LoopAnchorVesselId_Tree_LargePid_RoundTrip()
        {
            var source = new Recording
            {
                RecordingId = "tree-large-pid",
                LoopAnchorVesselId = 4294967295, // uint.MaxValue
            };
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, source);

            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);

            Assert.Equal(4294967295u, loaded.LoopAnchorVesselId);
        }

        // --- Cross-path: both serializers produce same key name ---

        [Fact]
        public void LoopAnchorVesselId_SavedKeyName_ConsistentAcrossSerializers()
        {
            var rec = new Recording
            {
                RecordingId = "key-consistency",
                LoopAnchorVesselId = 42,
            };

            var scenarioNode = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(scenarioNode, rec);

            var treeNode = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(treeNode, rec);

            // Both should use the same key name
            string scenarioValue = scenarioNode.GetValue("loopAnchorPid");
            string treeValue = treeNode.GetValue("loopAnchorPid");

            Assert.NotNull(scenarioValue);
            Assert.NotNull(treeValue);
            Assert.Equal("42", scenarioValue);
            Assert.Equal("42", treeValue);
        }
    }
}
