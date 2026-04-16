using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the IsGhostOnly field: serialization round-trip, spawn suppression,
    /// persistence artifact copying, and Gloops group assignment.
    /// </summary>
    [Collection("Sequential")]
    public class GhostOnlyRecordingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostOnlyRecordingTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
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

        // --- Serialization round-trip ---

        [Fact]
        public void IsGhostOnly_True_RoundTripViaSaveLoad()
        {
            var rec = new Recording
            {
                RecordingId = "ghost_rt_1",
                IsGhostOnly = true
            };

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);

            var restored = new Recording();
            RecordingTree.LoadRecordingFrom(node, restored);

            Assert.True(restored.IsGhostOnly);
        }

        [Fact]
        public void IsGhostOnly_False_NotSerializedByDefault()
        {
            var rec = new Recording
            {
                RecordingId = "ghost_rt_2",
                IsGhostOnly = false
            };

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);

            // IsGhostOnly=false should not produce a node value (same pattern as IsDebris)
            Assert.Null(node.GetValue("isGhostOnly"));

            var restored = new Recording();
            RecordingTree.LoadRecordingFrom(node, restored);

            Assert.False(restored.IsGhostOnly);
        }

        // --- Spawn suppression ---

        [Fact]
        public void ShouldSpawnAtRecordingEnd_GhostOnly_ReturnsFalse()
        {
            var rec = new Recording
            {
                RecordingId = "ghost_spawn_1",
                IsGhostOnly = true,
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Landed
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec,
                isActiveChainMember: false,
                isChainLoopingOrDisabled: false,
                treeContext: null);

            Assert.False(needsSpawn);
            Assert.Contains("ghost-only", reason);
        }

        [Fact]
        public void ShouldSpawnAtRecordingEnd_NotGhostOnly_CanSpawn()
        {
            var rec = new Recording
            {
                RecordingId = "ghost_spawn_2",
                IsGhostOnly = false,
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Landed
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec,
                isActiveChainMember: false,
                isChainLoopingOrDisabled: false,
                treeContext: null);

            Assert.True(needsSpawn);
        }

        // --- ApplyPersistenceArtifactsFrom ---

        [Fact]
        public void ApplyPersistenceArtifactsFrom_CopiesIsGhostOnly()
        {
            var source = new Recording
            {
                RecordingId = "ghost_copy_src",
                IsGhostOnly = true
            };

            var target = new Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            Assert.True(target.IsGhostOnly);
        }

        // --- Gloops group assignment ---

        [Fact]
        public void CommitGloopsRecording_AssignsGloopsGroup()
        {
            var rec = new Recording
            {
                RecordingId = "ghost_group_1",
                VesselName = "Test Vessel"
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 110.0 });

            RecordingStore.CommitGloopsRecording(rec);

            Assert.True(rec.IsGhostOnly);
            Assert.NotNull(rec.RecordingGroups);
            Assert.Contains(RecordingStore.GloopsGroupName, rec.RecordingGroups);
            Assert.Contains(rec, RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void CommitGloopsRecording_SetsLoopPlaybackIfPreset()
        {
            var rec = new Recording
            {
                RecordingId = "ghost_loop_1",
                VesselName = "Loopy Ghost",
                LoopPlayback = true
            };
            rec.Points.Add(new TrajectoryPoint { ut = 200.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 220.0 });

            RecordingStore.CommitGloopsRecording(rec);

            Assert.True(rec.LoopPlayback);
            Assert.True(rec.IsGhostOnly);
        }
    }
}
