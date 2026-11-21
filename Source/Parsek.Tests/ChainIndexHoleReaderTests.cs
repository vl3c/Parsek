using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// BATCH-B5 (CHAIN-NEXT-PIECE-EXACT-INDEX): a hole in a chain's indices (a piece removed
    /// from the chain, indices 0 and 2 left) must not end the chain for the readers that walk to
    /// the next piece. Every reader takes the next index from the one shared
    /// <see cref="RecordingChainQueries.NextChainIndexAfter(Recording, IReadOnlyList{Recording})"/>;
    /// before it each looked for exactly <c>ChainIndex + 1</c>. Each cell reds with its reader on
    /// the old "+1".
    /// </summary>
    [Collection("Sequential")]
    public class ChainIndexHoleReaderTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ChainIndexHoleReaderTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }

        private static Recording Piece(string id, int index, double start, double end,
            string chainId = "chain_hole", int branch = 0, uint pid = 7u)
        {
            var rec = new Recording
            {
                RecordingId = id,
                TreeId = "tree_hole",
                VesselName = "Hole",
                VesselPersistentId = pid,
                ChainId = chainId,
                ChainIndex = index,
                ChainBranch = branch,
            };
            rec.Points.Add(new TrajectoryPoint { ut = start, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = end, bodyName = "Kerbin" });
            return rec;
        }

        private static RecordingTree TreeOf(params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = "tree_hole",
                TreeName = "Hole",
                RootRecordingId = recs[0].RecordingId,
                BranchPoints = new List<BranchPoint>(),
            };
            foreach (var r in recs) tree.AddOrReplaceRecording(r);
            return tree;
        }

        [Fact]
        public void NextChainIndexAfter_StepsOverAHole()
        {
            var head = Piece("h", 0, 0, 100);
            var mid = Piece("m", 2, 100, 200);
            var last = Piece("l", 5, 200, 300);
            var otherChain = Piece("o", 1, 0, 100, chainId: "chain_other");
            var otherBranch = Piece("b", 1, 0, 100, branch: 1);
            var list = new List<Recording> { last, otherChain, mid, otherBranch, head, null };

            Assert.Equal(2, RecordingChainQueries.NextChainIndexAfter(head, list));
            Assert.Equal(5, RecordingChainQueries.NextChainIndexAfter(mid, list));
            Assert.Equal(RecordingChainQueries.NoNextChainIndex, RecordingChainQueries.NextChainIndexAfter(last, list));
            var unchained = Piece("u", 0, 0, 1, chainId: null);
            Assert.Equal(RecordingChainQueries.NoNextChainIndex, RecordingChainQueries.NextChainIndexAfter(unchained, list));
            Assert.Equal(RecordingChainQueries.NoNextChainIndex, RecordingChainQueries.NextChainIndexAfter(head, (List<Recording>)null));

            var dict = TreeOf(head, mid, last).Recordings;
            Assert.Equal(2, RecordingChainQueries.NextChainIndexAfter(head, dict));
            // A branch-1 piece follows its own branch only.
            Assert.Equal(RecordingChainQueries.NoNextChainIndex, RecordingChainQueries.NextChainIndexAfter(otherBranch, list));
        }

        [Fact]
        public void RecordingTree_HasNextChainSegment_AcrossAHole()
        {
            var head = Piece("h", 0, 0, 100);
            var tail = Piece("t", 2, 100, 200);
            var tree = TreeOf(head, tail);
            Assert.True(RecordingTree.HasNextChainSegment(head, tree.Recordings));
            Assert.False(RecordingTree.HasNextChainSegment(tail, tree.Recordings));
        }

        [Fact]
        public void PlaybackHost_FindNextChainRecording_AcrossAHole()
        {
            var head = Piece("h", 0, 0, 100);
            var tail = Piece("t", 2, 100, 200);
            var tree = TreeOf(head, tail);
            Assert.Same(tail, ParsekFlightPlaybackHost.FindNextChainRecording(tree, head));
            Assert.Null(ParsekFlightPlaybackHost.FindNextChainRecording(tree, tail));
        }

        [Fact]
        public void WatchMode_ChainNextSlotAndWatchTarget_AcrossAHole()
        {
            var head = Piece("h", 0, 0, 100);
            var tail = Piece("t", 2, 100, 200);
            var committed = new List<Recording> { head, tail };

            Assert.Equal(1, GhostPlaybackLogic.ResolveChainNextSlotIndex(0, committed, null));
            Assert.Equal(-1, GhostPlaybackLogic.ResolveChainNextSlotIndex(1, committed, null));
            Assert.Equal(1, GhostPlaybackLogic.FindNextWatchTarget(head, committed, null, i => true));

            Assert.True(GhostPlaybackLogic.TryGetPendingWatchActivationUT(
                head, committed, null, i => false, out double activationUT));
            Assert.Equal(100.0, activationUT);
        }

        [Fact]
        public void MissionCrossTreeDock_ChainSuccessor_AcrossAHole()
        {
            var head = Piece("h", 0, 0, 100);
            var tail = Piece("t", 2, 100, 200);
            var tree = TreeOf(head, tail);
            MethodInfo find = typeof(MissionCrossTreeDock).GetMethod(
                "FindChainSuccessor", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(find);
            Assert.Same(tail, find.Invoke(null, new object[] { tree, head }));
        }

        // The lineage trace follows the chain to the piece that carries the branch point; past a
        // hole it must still reach the child's pid.
        [Fact]
        public void GhostChainWalker_RootLineage_FollowsTheChainAcrossAHole()
        {
            var head = Piece("h", 0, 0, 100);
            var tail = Piece("t", 2, 100, 200);
            var child = Piece("c", 0, 150, 300, chainId: null, pid: 99u);
            tail.ChildBranchPointId = "bp_c";
            var tree = TreeOf(head, tail, child);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_c", Type = BranchPointType.Undock, UT = 150,
                ParentRecordingIds = new List<string> { "t" },
                ChildRecordingIds = new List<string> { "c" },
            });
            Assert.Contains(99u, GhostChainWalker.GetRootLineageVesselPids(tree));
        }

        // The load-time parent-link repair walks a stale parent forward through its chain to the
        // piece that owns the branch point's UT.
        [Fact]
        public void SplitParentLinks_RepairStaleParents_WalksAcrossAHole()
        {
            var head = Piece("h", 0, 0, 100);
            var tail = Piece("t", 2, 100, 200);
            var tree = TreeOf(head, tail);
            var bp = new BranchPoint
            {
                Id = "bp_late", Type = BranchPointType.Undock, UT = 150,
                ParentRecordingIds = new List<string> { "h" },
                ChildRecordingIds = new List<string>(),
            };
            tree.BranchPoints.Add(bp);

            Assert.Equal(1, SplitParentLinks.RepairStaleParents(tree, "test"));
            Assert.Equal("t", bp.ParentRecordingIds[0]);
        }
    }
}
