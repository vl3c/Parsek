using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// DELETE-LEAVES-TREE-MEMBERSHIP (found by S0.11-ksc-table-delete's first flight,
    /// 2026-09-02): <c>RecordingStore.RemoveRecordingAt</c> removed the row from the flat
    /// committed list only, so the committed tree that owned it kept the member,
    /// <c>SaveTreeRecordings</c> re-wrote its sidecars on the next OnSave, and the next load
    /// put it back. The removal must also prune the row from every committed tree.
    /// </summary>
    [Collection("Sequential")]
    public class RemoveRecordingAtTreePruneTests : IDisposable
    {
        public RemoveRecordingAtTreePruneTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
        }

        private static Recording MakeRecording(string id)
        {
            var rec = new Recording { RecordingId = id, VesselName = "V-" + id };
            rec.Points.Add(new TrajectoryPoint { ut = 1000, altitude = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 1100, altitude = 100, bodyName = "Kerbin" });
            return rec;
        }

        private static bool AnyCommittedTreeHolds(string id)
        {
            var trees = RecordingStore.CommittedTrees;
            for (int t = 0; t < trees.Count; t++)
                if (trees[t]?.Recordings != null && trees[t].Recordings.ContainsKey(id))
                    return true;
            return false;
        }

        [Fact]
        public void RemoveRecordingAt_PrunesTheRowFromItsCommittedTree()
        {
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("a"));
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("b"));
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecording("c"));
            Assert.True(AnyCommittedTreeHolds("b"), "fixture: b must start as a tree member");

            RecordingStore.RemoveRecordingAt(1);

            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            Assert.False(AnyCommittedTreeHolds("b"),
                "the deleted row must leave its committed tree, or the tree flush re-materializes it");
            Assert.True(AnyCommittedTreeHolds("a"));
            Assert.True(AnyCommittedTreeHolds("c"));
        }

        [Fact]
        public void RemoveRecordingAt_OnAMultiMemberTree_KeepsTheSurvivorsAndScrubsBranchPointRefs()
        {
            var a = MakeRecording("a");
            var b = MakeRecording("b");
            var c = MakeRecording("c");
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "Tree 1",
                RootRecordingId = "a",
                BranchPoints = new List<BranchPoint>
                {
                    new BranchPoint
                    {
                        Id = "bp-1",
                        Type = BranchPointType.Undock,
                        UT = 1050,
                        ParentRecordingIds = new List<string> { "a" },
                        ChildRecordingIds = new List<string> { "b", "c" },
                    },
                },
                Recordings = new Dictionary<string, Recording> { ["a"] = a, ["b"] = b, ["c"] = c },
            };
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddCommittedInternal(a);
            RecordingStore.AddCommittedInternal(b);
            RecordingStore.AddCommittedInternal(c);

            RecordingStore.RemoveRecordingAt(1);

            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            Assert.False(tree.Recordings.ContainsKey("b"));
            Assert.True(tree.Recordings.ContainsKey("a"));
            Assert.True(tree.Recordings.ContainsKey("c"));
            Assert.Equal("a", tree.RootRecordingId);
            // The branch point survives with the deleted endpoint scrubbed.
            Assert.Single(tree.BranchPoints);
            Assert.DoesNotContain("b", tree.BranchPoints[0].ChildRecordingIds);
            Assert.Contains("c", tree.BranchPoints[0].ChildRecordingIds);
        }
    }
}
