using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct cells for the two tree-commit tails: the active Re-Fly tree union
    /// (<see cref="TreeCommitService.TryUnionActiveReFlyTreeIntoCommitted"/>: recordings,
    /// branch points, root id, active id, background map) and the committed-store finalize
    /// (<see cref="TreeCommitService.FinalizeTreeCommit"/>: append vs replace per recording,
    /// the runtime-field carry, the tree append vs in-place replace, the version bump, the
    /// summary logs and the milestone end UT).
    /// </summary>
    [Collection("Sequential")]
    public class TreeCommitUnionFinalizeUnitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public TreeCommitUnionFinalizeUnitTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekScenario.ResetInstanceForTesting();
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            tempDir = Path.Combine(Path.GetTempPath(), "parsek_tcuf_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            RecordingPaths.SaveRootOverrideForTesting = tempDir;
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = false;
            ParsekScenario.ResetInstanceForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch { }
        }

        private static Recording Rec(string id, uint pid = 0, double endUT = double.NaN)
        {
            var r = new Recording { RecordingId = id, TreeId = "tree", VesselName = "V-" + id, VesselPersistentId = pid };
            if (!double.IsNaN(endUT))
            {
                r.ExplicitStartUT = 0;
                r.ExplicitEndUT = endUT;
            }
            return r;
        }

        private static BranchPoint Bp(string id)
        {
            return new BranchPoint
            {
                Id = id,
                ParentRecordingIds = new List<string>(),
                ChildRecordingIds = new List<string>(),
            };
        }

        private static RecordingTree Tree(string root, string active, params Recording[] recs)
        {
            var tree = new RecordingTree { Id = "tree", TreeName = "Tree", RootRecordingId = root, ActiveRecordingId = active };
            foreach (var r in recs)
                tree.Recordings[r.RecordingId] = r;
            return tree;
        }

        private static bool Union(RecordingTree existing, RecordingTree incoming,
            out int addedRecs, out int addedBps, out bool swapped)
        {
            return TreeCommitService.TryUnionActiveReFlyTreeIntoCommitted(
                existing, incoming, new ReFlySessionMarker(), out addedRecs, out addedBps, out swapped);
        }

        // ---------- Union: guards ----------

        [Fact]
        public void Union_NullTreeOrMarker_FailsWithZeroedOuts()
        {
            var tree = Tree("a", "a", Rec("a"));
            Assert.False(TreeCommitService.TryUnionActiveReFlyTreeIntoCommitted(
                null, tree, new ReFlySessionMarker(), out int r1, out int b1, out bool s1));
            Assert.False(TreeCommitService.TryUnionActiveReFlyTreeIntoCommitted(
                tree, null, new ReFlySessionMarker(), out r1, out b1, out s1));
            Assert.False(TreeCommitService.TryUnionActiveReFlyTreeIntoCommitted(
                tree, Tree("b", "b", Rec("b")), null, out r1, out b1, out s1));
            Assert.Equal(0, r1);
            Assert.Equal(0, b1);
            Assert.False(s1);
        }

        [Fact]
        public void Union_NullRecordingsDictionary_Fails()
        {
            var existing = Tree("a", "a", Rec("a"));
            var incoming = Tree("b", "b", Rec("b"));
            incoming.Recordings = null;
            Assert.False(Union(existing, incoming, out _, out _, out _));

            existing.Recordings = null;
            Assert.False(Union(existing, Tree("b", "b", Rec("b")), out _, out _, out _));
        }

        // ---------- Union: recordings ----------

        [Fact]
        public void Union_AddsIncomingOnly_OverwritesShared_KeepsExistingOnly()
        {
            var keptOnly = Rec("old");
            var sharedOld = Rec("shared");
            var existing = Tree("old", "old", keptOnly, sharedOld);
            var sharedNew = Rec("shared");
            var added = Rec("new");
            var incoming = Tree("old", "old", sharedNew, added);

            Assert.True(Union(existing, incoming, out int addedRecs, out _, out _));

            Assert.Equal(1, addedRecs);
            Assert.Same(keptOnly, existing.Recordings["old"]);
            Assert.Same(sharedNew, existing.Recordings["shared"]);
            Assert.Same(added, existing.Recordings["new"]);
            Assert.Equal(3, existing.Recordings.Count);
        }

        [Fact]
        public void Union_SameSharedInstance_IsNotCountedOrReplaced()
        {
            var shared = Rec("s");
            var existing = Tree("s", "s", shared);
            var incoming = Tree("s", "s", shared);

            Assert.True(Union(existing, incoming, out int addedRecs, out int addedBps, out bool swapped));

            Assert.Equal(0, addedRecs);
            Assert.Equal(0, addedBps);
            Assert.False(swapped);
            Assert.Same(shared, existing.Recordings["s"]);
        }

        [Fact]
        public void Union_SkipsEmptyKeysAndNullRecordings()
        {
            var existing = Tree("a", "a", Rec("a"));
            var incoming = Tree("a", "a");
            incoming.Recordings[""] = Rec("");
            incoming.Recordings["ghost"] = null;

            Assert.True(Union(existing, incoming, out int addedRecs, out _, out _));

            Assert.Equal(0, addedRecs);
            Assert.Equal(new[] { "a" }, existing.Recordings.Keys.ToArray());
        }

        [Fact]
        public void Union_RebuildsTheBackgroundMapFromTheUnion()
        {
            var existing = Tree("a", "a", Rec("a", pid: 11u));
            var incoming = Tree("a", "a", Rec("b", pid: 22u));

            Assert.True(Union(existing, incoming, out _, out _, out _));

            Assert.Contains(11u, existing.RecordedVesselPids);
            Assert.Contains(22u, existing.RecordedVesselPids);
        }

        // ---------- Union: branch points ----------

        [Fact]
        public void Union_BranchPoints_AddsNewIds_SkipsKnownNullAndEmpty()
        {
            var existing = Tree("a", "a", Rec("a"));
            existing.BranchPoints.Add(null);
            existing.BranchPoints.Add(Bp("bp1"));
            var incoming = Tree("a", "a");
            incoming.BranchPoints.Add(Bp("bp1"));
            incoming.BranchPoints.Add(null);
            incoming.BranchPoints.Add(Bp(""));
            var fresh = Bp("bp2");
            incoming.BranchPoints.Add(fresh);

            Assert.True(Union(existing, incoming, out _, out int addedBps, out _));

            Assert.Equal(1, addedBps);
            Assert.Equal(3, existing.BranchPoints.Count);
            Assert.Same(fresh, existing.BranchPoints[2]);
        }

        [Fact]
        public void Union_BranchPoints_RepeatedNewIdInIncoming_IsAddedEachTime()
        {
            // The known-id set is built from the existing list only, so an id that appears
            // twice in the incoming list (and not in the existing one) is appended twice.
            var existing = Tree("a", "a", Rec("a"));
            var incoming = Tree("a", "a");
            incoming.BranchPoints.Add(Bp("bp9"));
            incoming.BranchPoints.Add(Bp("bp9"));

            Assert.True(Union(existing, incoming, out _, out int addedBps, out _));

            Assert.Equal(2, addedBps);
            Assert.Equal(2, existing.BranchPoints.Count(b => b.Id == "bp9"));
        }

        [Fact]
        public void Union_BranchPoints_NullExistingListIsCreated_NullIncomingLeavesIt()
        {
            var existing = Tree("a", "a", Rec("a"));
            existing.BranchPoints = null;
            var incoming = Tree("a", "a");
            incoming.BranchPoints.Add(Bp("bp1"));

            Assert.True(Union(existing, incoming, out _, out int addedBps, out _));
            Assert.Equal(1, addedBps);
            Assert.Single(existing.BranchPoints);

            var untouched = Tree("a", "a", Rec("a"));
            untouched.BranchPoints = null;
            var noBps = Tree("a", "a");
            noBps.BranchPoints = null;
            Assert.True(Union(untouched, noBps, out _, out addedBps, out _));
            Assert.Equal(0, addedBps);
            Assert.Null(untouched.BranchPoints);
        }

        // ---------- Union: root and active ids ----------

        [Fact]
        public void Union_Root_KeptWhenSet_AdoptedWhenMissing()
        {
            var existing = Tree("keep", "a", Rec("a"));
            Assert.True(Union(existing, Tree("other", "a"), out _, out _, out _));
            Assert.Equal("keep", existing.RootRecordingId);

            var rootless = Tree(null, "a", Rec("a"));
            Assert.True(Union(rootless, Tree("adopted", "a"), out _, out _, out _));
            Assert.Equal("adopted", rootless.RootRecordingId);

            var stillRootless = Tree("", "a", Rec("a"));
            Assert.True(Union(stillRootless, Tree(null, "a"), out _, out _, out _));
            Assert.Equal("", stillRootless.RootRecordingId);
        }

        [Fact]
        public void Union_Active_IncomingWinsWhenSetAndDifferent()
        {
            var existing = Tree("a", "head", Rec("a"));
            Assert.True(Union(existing, Tree("a", "tip"), out _, out _, out bool swapped));
            Assert.True(swapped);
            Assert.Equal("tip", existing.ActiveRecordingId);

            Assert.True(Union(existing, Tree("a", "tip"), out _, out _, out swapped));
            Assert.False(swapped);

            Assert.True(Union(existing, Tree("a", null), out _, out _, out swapped));
            Assert.False(swapped);
            Assert.Equal("tip", existing.ActiveRecordingId);
        }

        [Fact]
        public void UnionForMerge_MatchesTheUnionAndItsCounts()
        {
            var existing = Tree("a", "a", Rec("a"));
            var incoming = Tree("a", "b", Rec("b"));
            incoming.BranchPoints.Add(Bp("bp1"));

            Assert.True(TreeCommitService.UnionActiveReFlyTreeIntoCommittedForMerge(
                existing, incoming, new ReFlySessionMarker(), out int addedRecs, out int addedBps));

            Assert.Equal(1, addedRecs);
            Assert.Equal(1, addedBps);
            Assert.Equal("b", existing.ActiveRecordingId);
        }

        // ---------- Finalize ----------

        [Fact]
        public void Finalize_NewTree_AppendsEveryRecordingAndTheTree()
        {
            var a = Rec("a", endUT: 120);
            var b = Rec("b", endUT: 340);
            var tree = Tree("a", "a", a, b);
            int version = RecordingStore.StateVersion;

            TreeCommitService.FinalizeTreeCommit(tree);

            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            Assert.Single(RecordingStore.CommittedTrees);
            Assert.Same(tree, RecordingStore.CommittedTrees[0]);
            Assert.True(RecordingStore.StateVersion > version);
            Assert.Contains(logLines, l => l.Contains("Committed tree 'Tree' (2 recordings). Total committed: 2 recordings, 1 trees"));
            Assert.DoesNotContain(logLines, l => l.Contains("CommitTree: replaced committed tree"));
            Assert.Equal(2, logLines.Count(l => l.Contains("CommitTree: child ")));
            Assert.Contains(logLines, l => l.Contains("CreateMilestone: scanning visible events in window UT 0-340, recordingId=tree"));
        }

        [Fact]
        public void Finalize_RecordingAlreadyCommittedAsTheSameInstance_IsLeftAlone()
        {
            var a = Rec("a", endUT: 50);
            RecordingStore.AddRecordingWithTreeForTesting(a);
            int treesBefore = RecordingStore.CommittedTrees.Count;
            int version = RecordingStore.StateVersion;
            var tree = Tree("a", "a", a);

            TreeCommitService.FinalizeTreeCommit(tree);

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Same(a, RecordingStore.CommittedRecordings[0]);
            Assert.Equal(treesBefore + 1, RecordingStore.CommittedTrees.Count);
            // Nothing added or replaced and the tree is appended unbumped.
            Assert.Equal(version, RecordingStore.StateVersion);
        }

        [Fact]
        public void Finalize_ReplaceInPlace_SwapsTheRecordingAndTheTreeAndCarriesTheRewindSave()
        {
            var oldRoot = Rec("root", endUT: 100);
            oldRoot.RewindSaveFileName = "parsek_rw_root";
            var first = Tree("root", "root", oldRoot);
            TreeCommitService.FinalizeTreeCommit(first);
            logLines.Clear();

            var newRoot = Rec("root", endUT: 200);
            var added = Rec("child", endUT: 250);
            var second = Tree("root", "child", newRoot, added);
            int version = RecordingStore.StateVersion;

            TreeCommitService.FinalizeTreeCommit(second, replaceCommittedTreeIndex: 0);

            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            Assert.Same(newRoot, RecordingStore.CommittedRecordings[0]);
            Assert.Equal("parsek_rw_root", newRoot.RewindSaveFileName);
            Assert.Single(RecordingStore.CommittedTrees);
            Assert.Same(second, RecordingStore.CommittedTrees[0]);
            Assert.True(RecordingStore.StateVersion > version);
            Assert.Contains(logLines, l => l.Contains("Updated committed tree 'Tree' (2 recordings)."));
            Assert.Contains(logLines, l => l.Contains("CommitTree: replaced committed tree index=0 addedRecordings=1 " +
                "replacedRecordings=1 preservedRewindSaves=1"));
        }

        [Fact]
        public void Finalize_ReplaceWithNoRecordingChange_StillBumpsOnceForTheTree()
        {
            // Every recording of the replacement tree is already committed as the same
            // instance, so nothing is added or replaced and the recording-level bump stays
            // off: the one bump left is the tree replace's own.
            var root = Rec("root", endUT: 100);
            var first = Tree("root", "root", root);
            TreeCommitService.FinalizeTreeCommit(first);
            var second = Tree("root", "root", root);
            int version = RecordingStore.StateVersion;

            TreeCommitService.FinalizeTreeCommit(second, replaceCommittedTreeIndex: 0);

            Assert.Same(second, RecordingStore.CommittedTrees[0]);
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal(version + 1, RecordingStore.StateVersion);
            Assert.Contains(logLines, l => l.Contains("CommitTree: replaced committed tree index=0 addedRecordings=0 " +
                "replacedRecordings=0 preservedRewindSaves=0 preservedRuntimeFields=0"));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        public void Finalize_ReplaceIndexOutOfRange_AppendsAsANewTree(int index)
        {
            TreeCommitService.FinalizeTreeCommit(Tree("x", "x", Rec("x", endUT: 10)));
            logLines.Clear();
            var tree = Tree("y", "y", Rec("y", endUT: 20));
            tree.Id = "tree2";

            TreeCommitService.FinalizeTreeCommit(tree, replaceCommittedTreeIndex: index);

            Assert.Equal(2, RecordingStore.CommittedTrees.Count);
            Assert.Same(tree, RecordingStore.CommittedTrees[1]);
            Assert.Contains(logLines, l => l.Contains("Committed tree 'Tree' (1 recordings)"));
            Assert.DoesNotContain(logLines, l => l.Contains("CommitTree: replaced committed tree"));
        }

        [Fact]
        public void Finalize_MarksEveryTreeRecordingDirtyBeforeTheFlush()
        {
            var a = Rec("a", endUT: 10);
            var b = Rec("b", endUT: 20);
            a.FilesDirty = false;
            b.FilesDirty = false;

            TreeCommitService.FinalizeTreeCommit(Tree("a", "a", a, b));

            Assert.Contains(logLines, l => l.Contains("FlushDirtyFiles: saved 2, failed 0"));
        }

        [Fact]
        public void Finalize_MilestoneEndIsTheLatestRecordingEnd()
        {
            TreeCommitService.FinalizeTreeCommit(Tree("a", "a", Rec("a", endUT: 900), Rec("b", endUT: 75)));
            Assert.Contains(logLines, l => l.Contains("window UT 0-900, recordingId=tree"));
        }

        [Fact]
        public void Finalize_MilestoneEndIsZeroWhenNoRecordingHasAnEnd()
        {
            TreeCommitService.FinalizeTreeCommit(Tree("a", "a", Rec("a")));
            Assert.Contains(logLines, l => l.Contains("window UT 0-0, recordingId=tree"));
        }

        // ---------- Extracted units ----------

        [Fact]
        public void UnionRecordingsInto_CountsOnlyNewIds()
        {
            var existing = Tree("a", "a", Rec("a"), Rec("s"));
            var incoming = Tree("a", "a", Rec("s"), Rec("n1"), Rec("n2"));

            Assert.Equal(2, TreeCommitService.UnionRecordingsInto(existing, incoming));
            Assert.Equal(4, existing.Recordings.Count);
            Assert.Same(incoming.Recordings["s"], existing.Recordings["s"]);
        }

        [Fact]
        public void UnionBranchPointsInto_NullIncomingListAddsNothing()
        {
            var existing = Tree("a", "a");
            existing.BranchPoints.Add(Bp("bp1"));
            var incoming = Tree("a", "a");
            incoming.BranchPoints = null;

            Assert.Equal(0, TreeCommitService.UnionBranchPointsInto(existing, incoming));
            Assert.Single(existing.BranchPoints);
        }

        [Fact]
        public void UnionBranchPointsInto_KnownIdIsOrdinal()
        {
            var existing = Tree("a", "a");
            existing.BranchPoints.Add(Bp("BP1"));
            var incoming = Tree("a", "a");
            incoming.BranchPoints.Add(Bp("bp1"));

            Assert.Equal(1, TreeCommitService.UnionBranchPointsInto(existing, incoming));
        }

        [Theory]
        [InlineData(null, "in", "in")]
        [InlineData("", "in", "in")]
        [InlineData("keep", "in", "keep")]
        [InlineData(null, null, null)]
        [InlineData(null, "", null)]
        public void AdoptMissingRootRecordingId_Table(string existingRoot, string incomingRoot, string expected)
        {
            var existing = Tree(existingRoot, null);
            TreeCommitService.AdoptMissingRootRecordingId(existing, Tree(incomingRoot, null));
            Assert.Equal(expected, existing.RootRecordingId);
        }

        [Theory]
        [InlineData("head", "tip", true, "tip")]
        [InlineData(null, "tip", true, "tip")]
        [InlineData("tip", "tip", false, "tip")]
        [InlineData("head", null, false, "head")]
        [InlineData("head", "", false, "head")]
        [InlineData("tip", "TIP", true, "TIP")]
        public void AdoptIncomingActiveRecordingId_Table(string existingActive, string incomingActive,
            bool swapped, string expected)
        {
            var existing = Tree(null, existingActive);
            Assert.Equal(swapped, TreeCommitService.AdoptIncomingActiveRecordingId(existing, Tree(null, incomingActive)));
            Assert.Equal(expected, existing.ActiveRecordingId);
        }

        [Fact]
        public void MergeTreeRecordings_AppendsNewReplacesOtherInstanceSkipsSameInstance()
        {
            var same = Rec("same", endUT: 10);
            var oldCopy = Rec("swap", endUT: 10);
            oldCopy.RewindSaveFileName = "parsek_rw_swap";
            oldCopy.LastAppliedResourceIndex = 5;
            RecordingStore.AddRecordingWithTreeForTesting(same);
            RecordingStore.AddRecordingWithTreeForTesting(oldCopy);
            var newCopy = Rec("swap", endUT: 20);
            var fresh = Rec("fresh", endUT: 30);
            same.FilesDirty = false;
            var tree = Tree("same", "same", same, newCopy, fresh);
            int version = RecordingStore.StateVersion;

            TreeCommitService.MergeTreeRecordingsIntoCommitted(tree, null, null,
                out int added, out int replaced, out int saves, out int runtime);

            Assert.Equal(1, added);
            Assert.Equal(1, replaced);
            Assert.Equal(1, saves);
            // The resource cursor is the one runtime field carried (the save is counted apart).
            Assert.Equal(1, runtime);
            Assert.Equal(5, newCopy.LastAppliedResourceIndex);
            Assert.Equal(new[] { "same", "swap", "fresh" },
                RecordingStore.CommittedRecordings.Select(r => r.RecordingId).ToArray());
            Assert.Same(newCopy, RecordingStore.CommittedRecordings[1]);
            Assert.Equal("parsek_rw_swap", newCopy.RewindSaveFileName);
            Assert.True(same.FilesDirty && newCopy.FilesDirty && fresh.FilesDirty);
            // The caller owns the bump: appends and replaces here are unbumped.
            Assert.Equal(version, RecordingStore.StateVersion);
        }

        [Fact]
        public void MergeTreeRecordings_EmptyTree_CountsNothing()
        {
            TreeCommitService.MergeTreeRecordingsIntoCommitted(Tree(null, null), null, null,
                out int added, out int replaced, out int saves, out int runtime);

            Assert.Equal(0, added + replaced + saves + runtime);
            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        [Theory]
        [InlineData(-1, 3, false)]
        [InlineData(0, 3, true)]
        [InlineData(2, 3, true)]
        [InlineData(3, 3, false)]
        [InlineData(0, 0, false)]
        public void IsCommittedTreeIndex_Table(int index, int count, bool expected)
        {
            Assert.Equal(expected, TreeCommitService.IsCommittedTreeIndex(index, count));
        }

        [Fact]
        public void ComputeTreeEndUT_LatestEnd_ZeroForNoneOrNegative()
        {
            Assert.Equal(500.0, TreeCommitService.ComputeTreeEndUT(
                Tree(null, null, Rec("a", endUT: 120), Rec("b", endUT: 500), Rec("c", endUT: 300))));
            Assert.Equal(0.0, TreeCommitService.ComputeTreeEndUT(Tree(null, null)));
            Assert.Equal(0.0, TreeCommitService.ComputeTreeEndUT(Tree(null, null, Rec("n", endUT: -40))));
        }
    }
}
