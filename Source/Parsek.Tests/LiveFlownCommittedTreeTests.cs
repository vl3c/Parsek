using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner RULE 3 ("My live flight wins", finding H7): while a committed vessel is flown
    /// again (the live tree is a copy of a committed tree, also across a restart in the
    /// save's active or pending tree) the optimizer leaves the committed tree alone, and the
    /// commit keeps the new leg instead of dropping the copy as a duplicate.
    /// </summary>
    [Collection("Sequential")]
    public class LiveFlownCommittedTreeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public LiveFlownCommittedTreeTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            ParsekScenario.ResetInstanceForTesting();
            Parsek.Logistics.RouteStore.ResetForTesting();
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            tempDir = Path.Combine(Path.GetTempPath(), "parsek_lfct_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            RecordingPaths.SaveRootOverrideForTesting = tempDir;
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = false;
            Parsek.Logistics.RouteStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch { }
        }

        private static Recording Rec(string id, double startUT, double endUT, string chainId = null,
            int chainIndex = 0)
        {
            var rec = new Recording
            {
                RecordingId = id,
                TreeId = "tree_1",
                VesselName = "Station",
                ChainId = chainId,
                ChainIndex = chainIndex,
                SegmentPhase = "exo",
                SegmentBodyName = "Mun",
                MergeState = MergeState.Immutable,
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUT, altitude = 50000, bodyName = "Mun" });
            rec.Points.Add(new TrajectoryPoint { ut = endUT, altitude = 50000, bodyName = "Mun" });
            return rec;
        }

        private static RecordingTree Tree(string active, params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Station",
                RootRecordingId = recs[0].RecordingId,
                ActiveRecordingId = active,
                BranchPoints = new List<BranchPoint>(),
            };
            foreach (var r in recs)
                tree.AddOrReplaceRecording(r);
            return tree;
        }

        private static RecordingTree CommitExisting(RecordingTree tree)
        {
            foreach (var r in tree.Recordings.Values)
                RecordingStore.AddCommittedInternal(r);
            RecordingStore.AddCommittedTreeForTesting(tree);
            return tree;
        }

        private static BranchPoint Bp(string id) => new BranchPoint
        {
            Id = id,
            Type = BranchPointType.Undock,
            ParentRecordingIds = new List<string>(),
            ChildRecordingIds = new List<string>(),
        };

        // ---------- Commit ----------

        // catches: the optimizer re-cut the committed tree while the copy lived (the committed
        // side holds a tail id the copy lacks), so the strict gate refuses and the duplicate
        // skip dropped the player's new leg.
        [Fact]
        public void CommitPendingTree_FlownAgainAfterAReCut_KeepsTheNewLegAndTheCommittedOnlyPiece()
        {
            CommitExisting(Tree("rec_tail",
                Rec("rec_head", 0, 100, "chain_A", 0),
                Rec("rec_tail", 100, 200, "chain_A", 1)));

            // The live copy was taken before the re-cut: one whole recording, then flown on.
            var liveHead = Rec("rec_head", 0, 300);
            PendingTreeSlot.StashPendingTree(Tree("rec_head", liveHead));

            Assert.True(TreeCommitService.CommitPendingTree());

            Assert.False(PendingTreeSlot.HasPendingTree);
            var committed = RecordingStore.FindCommittedTreeById("tree_1");
            Assert.Same(liveHead, committed.Recordings["rec_head"]);
            Assert.True(committed.Recordings.ContainsKey("rec_tail"));
            Assert.Equal("rec_head", committed.ActiveRecordingId);
            Assert.Same(liveHead, RecordingStore.CommittedRecordings.Single(r => r.RecordingId == "rec_head"));
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("CommitTree: committed vessel flown again")
                && l.Contains("recordingRunsPastCommitted rec=rec_head")
                && l.Contains("keptCommittedOnlyRecordings=1"));
        }

        // catches: a copy that was never flown (the same flight, re-cut on the committed side)
        // must still be skipped as a duplicate, not unioned back over the cut.
        [Fact]
        public void CommitPendingTree_UnflownCopyOfAReCutTree_IsStillADuplicate()
        {
            CommitExisting(Tree("rec_tail",
                Rec("rec_head", 0, 100, "chain_A", 0),
                Rec("rec_tail", 100, 200, "chain_A", 1)));
            PendingTreeSlot.StashPendingTree(Tree("rec_head", Rec("rec_head", 0, 200)));

            Assert.False(TreeCommitService.CommitPendingTree());

            var committed = RecordingStore.FindCommittedTreeById("tree_1");
            Assert.Equal(100, committed.Recordings["rec_head"].EndUT);
            Assert.Contains(logLines, l => l.Contains("CommitTree: duplicate tree id='tree_1' skipped")
                && l.Contains("flownAgain=false evidence=none"));
        }

        [Fact]
        public void IsFlownAgain_NewBranchPoint_IsNewFlight()
        {
            var existing = Tree("a", Rec("a", 0, 100));
            var incoming = Tree("a", Rec("a", 0, 100));
            incoming.BranchPoints.Add(Bp("bp_new"));

            Assert.True(TreeCommitService.IsCommittedTreeFlownAgain(existing, incoming, out string evidence));
            Assert.Equal("newBranchPoints=1", evidence);
        }

        [Fact]
        public void IsFlownAgain_NewRecordingPastTheCommittedTree_IsNewFlight_OneInsideItIsNot()
        {
            var existing = Tree("a", Rec("a", 0, 100), Rec("b", 0, 150));

            var inside = Tree("a", Rec("a", 0, 100), Rec("merged_away", 20, 140));
            Assert.False(TreeCommitService.IsCommittedTreeFlownAgain(existing, inside, out string e1));
            Assert.Equal("none", e1);

            var past = Tree("a", Rec("a", 0, 100), Rec("new_leg", 120, 260));
            Assert.True(TreeCommitService.IsCommittedTreeFlownAgain(existing, past, out string e2));
            Assert.StartsWith("newRecordingPastCommitted rec=new_leg", e2);
        }

        [Fact]
        public void IsFlownAgain_PredictedTailIsNotFlight()
        {
            var existing = Tree("a", Rec("a", 0, 100));
            var copy = Rec("a", 0, 100);
            copy.OrbitSegments.Add(new OrbitSegment { startUT = 100, endUT = 5000, isPredicted = true });

            Assert.False(TreeCommitService.IsCommittedTreeFlownAgain(existing, Tree("a", copy), out _));

            copy.OrbitSegments.Add(new OrbitSegment { startUT = 100, endUT = 400, isPredicted = false });
            Assert.True(TreeCommitService.IsCommittedTreeFlownAgain(existing, Tree("a", copy), out _));
        }

        [Fact]
        public void IsFlownAgain_SameObjectOrOtherId_IsNotACopy()
        {
            var tree = Tree("a", Rec("a", 0, 100));
            Assert.False(TreeCommitService.IsCommittedTreeFlownAgain(tree, tree, out string e));
            Assert.Equal("not-a-copy", e);
            var other = Tree("a", Rec("a", 0, 500));
            other.Id = "tree_2";
            Assert.False(TreeCommitService.IsCommittedTreeFlownAgain(tree, other, out e));
            Assert.Equal("not-a-copy", e);
            Assert.False(TreeCommitService.IsCommittedTreeFlownAgain(null, tree, out _));
        }

        // ---------- Optimizer hold ----------

        // catches: the cold load's optimizer pass re-cut (here: merged) the committed tree a
        // saved live copy belongs to, so the copy's commit then read as a duplicate.
        [Fact]
        public void OptimizerPass_LeavesATreeWithAPendingLiveCopyWhole()
        {
            CommitExisting(Tree("seg_tail",
                Rec("seg_head", 17000, 17030, "chain_A", 0),
                Rec("seg_tail", 17030, 17060, "chain_A", 1)));
            PendingTreeSlot.StashPendingTree(Tree("seg_tail",
                Rec("seg_head", 17000, 17030, "chain_A", 0),
                Rec("seg_tail", 17030, 17090, "chain_A", 1)), PendingTreeState.Limbo);
            logLines.Clear();

            CommittedListOptimizer.RunOptimizationPass();

            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("Optimization pass: tree 'tree_1' held whole while its live copy is flown")
                && l.Contains("pendingCopy=True"));
        }

        [Fact]
        public void OptimizerPass_WithoutALiveCopy_StillMergesTheSameTree()
        {
            CommitExisting(Tree("seg_tail",
                Rec("seg_head", 17000, 17030, "chain_A", 0),
                Rec("seg_tail", 17030, 17060, "chain_A", 1)));

            CommittedListOptimizer.RunOptimizationPass();

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.DoesNotContain(logLines, l => l.Contains("held whole while its live copy is flown"));
        }

        [Fact]
        public void CollectLiveCopyTreeIds_DistinctCopiesAndTheRestoreAttempt()
        {
            var committedA = Tree("a", Rec("a", 0, 1));
            var committedB = Tree("b", Rec("b", 0, 1));
            committedB.Id = "tree_B";
            var committedC = Tree("c", Rec("c", 0, 1));
            committedC.Id = "tree_C";
            var trees = new List<RecordingTree> { committedA, committedB, committedC };
            var copyOfA = Tree("a", Rec("a", 0, 2));
            var fresh = Tree("x", Rec("x", 0, 1));
            fresh.Id = "tree_new";

            var ids = TreeCommitService.CollectLiveCopyTreeIds(
                trees, new[] { copyOfA, committedB, fresh, null }, "tree_C");

            Assert.Equal(new[] { "tree_1", "tree_C" }, ids.OrderBy(s => s, StringComparer.Ordinal).ToArray());

            var held = TreeCommitService.AddLiveCopyRecordingIds(
                new HashSet<string> { "route_src" }, trees, ids);
            Assert.Equal(new[] { "a", "c", "route_src" }, held.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        }
    }
}
