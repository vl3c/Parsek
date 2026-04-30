using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the Re-Fly tree anchor lock helpers in
    /// <see cref="ParsekFlight"/>. The lock translates ghost positions in the
    /// active Re-Fly tree by a constant world-space delta so the recorded
    /// trajectory aligns with the live player at session start and the
    /// ghost continues along its own original recorded path independent of
    /// the player's later movement.
    ///
    /// <para>
    /// These tests exercise the parts that don't require a live KSP
    /// runtime: the recording-id → tree-id resolution path that decides
    /// which ghost positions get translated. The lazy delta-compute path
    /// and per-frame translation are covered by the in-game test in
    /// <c>RuntimeTests</c>.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyTreeAnchorLockTests : IDisposable
    {
        public ReFlyTreeAnchorLockTests()
        {
            ParsekLog.SuppressLogging = true;
            RecordingStore.Clear();
        }

        public void Dispose()
        {
            RecordingStore.Clear();
            ParsekLog.SuppressLogging = false;
        }

        [Fact]
        public void ResolveTreeIdForRecording_NullId_ReturnsNull()
        {
            Assert.Null(ParsekFlight.ResolveTreeIdForRecording(null));
            Assert.Null(ParsekFlight.ResolveTreeIdForRecording(""));
        }

        [Fact]
        public void ResolveTreeIdForRecording_NoTrees_ReturnsNull()
        {
            Assert.Null(ParsekFlight.ResolveTreeIdForRecording("any-rec-id"));
        }

        [Fact]
        public void ResolveTreeIdForRecording_RecordingInCommittedTree_ReturnsTreeId()
        {
            var tree = MakeTreeWithRecording(treeId: "tree-1", recordingId: "rec-A");
            RecordingStore.CommittedTrees.Add(tree);

            string treeId = ParsekFlight.ResolveTreeIdForRecording("rec-A");

            Assert.Equal("tree-1", treeId);
        }

        [Fact]
        public void ResolveTreeIdForRecording_UnknownRecording_ReturnsNull()
        {
            var tree = MakeTreeWithRecording(treeId: "tree-1", recordingId: "rec-A");
            RecordingStore.CommittedTrees.Add(tree);

            Assert.Null(ParsekFlight.ResolveTreeIdForRecording("rec-B"));
        }

        [Fact]
        public void ResolveTreeIdForRecording_RecordingInPendingTree_ReturnsPendingTreeId()
        {
            var pending = MakeTreeWithRecording(treeId: "pending-tree", recordingId: "rec-pending");
            RecordingStore.StashPendingTree(pending);

            string treeId = ParsekFlight.ResolveTreeIdForRecording("rec-pending");

            Assert.Equal("pending-tree", treeId);
        }

        [Fact]
        public void ResolveTreeIdForRecording_MultipleTrees_FindsCorrectTree()
        {
            var tree1 = MakeTreeWithRecording(treeId: "tree-1", recordingId: "rec-A");
            var tree2 = MakeTreeWithRecording(treeId: "tree-2", recordingId: "rec-B");
            RecordingStore.CommittedTrees.Add(tree1);
            RecordingStore.CommittedTrees.Add(tree2);

            Assert.Equal("tree-1", ParsekFlight.ResolveTreeIdForRecording("rec-A"));
            Assert.Equal("tree-2", ParsekFlight.ResolveTreeIdForRecording("rec-B"));
        }

        [Fact]
        public void TrySampleRecordedAbsoluteWorld_NullRec_ReturnsFalse()
        {
            Vector3d worldPos;
            Assert.False(ParsekFlight.TrySampleRecordedAbsoluteWorld(null, 0.0, out worldPos));
        }

        [Fact]
        public void TrySampleRecordedAbsoluteWorld_EmptySections_ReturnsFalse()
        {
            var rec = new Recording { RecordingId = "rec-empty" };
            Vector3d worldPos;
            Assert.False(ParsekFlight.TrySampleRecordedAbsoluteWorld(rec, 0.0, out worldPos));
        }

        [Fact]
        public void TrySampleRecordedAbsoluteWorld_RelativeSectionWithoutShadow_ReturnsFalse()
        {
            var rec = new Recording { RecordingId = "rec-rel-no-shadow" };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 0.0,
                endUT = 10.0,
                anchorVesselId = 12345u,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 5.0, latitude = 1, longitude = 2, altitude = 3 },
                },
                // absoluteFrames = null → no shadow data; helper must return false
                absoluteFrames = null,
            });

            Vector3d worldPos;
            Assert.False(ParsekFlight.TrySampleRecordedAbsoluteWorld(rec, 5.0, out worldPos));
        }

        [Fact]
        public void TrySampleRecordedAbsoluteWorld_OrbitalCheckpointSection_ReturnsFalse()
        {
            var rec = new Recording { RecordingId = "rec-checkpoint" };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 0.0,
                endUT = 10.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 5.0 },
                },
            });

            Vector3d worldPos;
            Assert.False(ParsekFlight.TrySampleRecordedAbsoluteWorld(rec, 5.0, out worldPos));
        }

        // ============================================================
        // Helpers
        // ============================================================

        private static RecordingTree MakeTreeWithRecording(string treeId, string recordingId)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeId,
                RootRecordingId = recordingId,
            };
            var rec = new Recording
            {
                RecordingId = recordingId,
                VesselName = "Test Vessel",
                TreeId = treeId,
            };
            tree.Recordings[recordingId] = rec;
            return tree;
        }
    }
}
