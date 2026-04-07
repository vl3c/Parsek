using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for tree-aware rewind save lookup: GetRewindRecording, GetRewindSaveFileName,
    /// and CanRewind resolving through tree roots for branch recordings.
    /// </summary>
    [Collection("Sequential")]
    public class RewindTreeLookupTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RewindTreeLookupTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
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

        private static RecordingTree BuildTree(string treeId, string rootId,
            string rootRewindSave, params (string id, string name)[] branches)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "TestTree",
                RootRecordingId = rootId
            };

            var rootRec = new Recording
            {
                RecordingId = rootId,
                TreeId = treeId,
                VesselName = "RootVessel",
                RewindSaveFileName = rootRewindSave,
                RewindReservedFunds = 100,
                RewindReservedScience = 10,
                RewindReservedRep = 5
            };
            tree.Recordings[rootId] = rootRec;

            foreach (var (id, name) in branches)
            {
                tree.Recordings[id] = new Recording
                {
                    RecordingId = id,
                    TreeId = treeId,
                    VesselName = name
                    // No RewindSaveFileName — branches don't own one
                };
            }

            return tree;
        }

        #region GetRewindSaveFileName

        [Fact]
        public void GetRewindSaveFileName_DirectSave_ReturnsSave()
        {
            var rec = new Recording
            {
                RecordingId = "standalone",
                RewindSaveFileName = "parsek_rw_abc123"
            };
            var trees = new List<RecordingTree>();

            string result = RecordingStore.GetRewindSaveFileName(rec);

            Assert.Equal("parsek_rw_abc123", result);
        }

        [Fact]
        public void GetRewindSaveFileName_NoSaveNoTree_ReturnsNull()
        {
            var rec = new Recording
            {
                RecordingId = "orphan",
                VesselName = "NoSave"
            };

            string result = RecordingStore.GetRewindSaveFileName(rec);

            Assert.Null(result);
        }

        [Fact]
        public void GetRewindSaveFileName_TreeBranch_ReturnsRootSave()
        {
            var tree = BuildTree("tree1", "root1", "parsek_rw_root",
                ("branch1", "EVAKerbal"));
            RecordingStore.AddCommittedTreeForTesting(tree);

            var branch = tree.Recordings["branch1"];

            string result = RecordingStore.GetRewindSaveFileName(branch);

            Assert.Equal("parsek_rw_root", result);
        }

        [Fact]
        public void GetRewindSaveFileName_TreeBranchRootHasNoSave_ReturnsNull()
        {
            var tree = BuildTree("tree2", "root2", null,
                ("branch2", "EVAKerbal"));
            RecordingStore.AddCommittedTreeForTesting(tree);

            var branch = tree.Recordings["branch2"];

            string result = RecordingStore.GetRewindSaveFileName(branch);

            Assert.Null(result);
        }

        [Fact]
        public void GetRewindSaveFileName_TreeNotFound_ReturnsNull()
        {
            // Recording has TreeId but no matching tree in committed list
            var rec = new Recording
            {
                RecordingId = "orphanBranch",
                TreeId = "nonExistentTree"
            };

            string result = RecordingStore.GetRewindSaveFileName(rec);

            Assert.Null(result);
        }

        #endregion

        #region GetRewindRecording

        [Fact]
        public void GetRewindRecording_TreeBranch_ReturnsRootRecording()
        {
            var tree = BuildTree("tree3", "root3", "parsek_rw_r3",
                ("branch3", "DecoupledStage"));
            RecordingStore.AddCommittedTreeForTesting(tree);

            var branch = tree.Recordings["branch3"];
            var rootRec = tree.Recordings["root3"];

            Recording result = RecordingStore.GetRewindRecording(branch);

            Assert.Same(rootRec, result);
        }

        [Fact]
        public void GetRewindRecording_MultipleTrees_FindsCorrectTree()
        {
            var tree1 = BuildTree("treeA", "rootA", "parsek_rw_A",
                ("branchA", "VesselA"));
            var tree2 = BuildTree("treeB", "rootB", "parsek_rw_B",
                ("branchB", "VesselB"));
            RecordingStore.AddCommittedTreeForTesting(tree1);
            RecordingStore.AddCommittedTreeForTesting(tree2);

            var branchB = tree2.Recordings["branchB"];

            Recording result = RecordingStore.GetRewindRecording(branchB);

            Assert.Same(tree2.Recordings["rootB"], result);
            Assert.Equal("parsek_rw_B", result.RewindSaveFileName);
        }

        [Fact]
        public void GetRewindRecording_DirectSave_ReturnsSelf()
        {
            var rec = new Recording
            {
                RecordingId = "self",
                RewindSaveFileName = "parsek_rw_self"
            };

            Recording result = RecordingStore.GetRewindRecording(rec);

            Assert.Same(rec, result);
        }

        [Fact]
        public void GetRewindRecording_NullRecording_ReturnsNull()
        {
            Recording result = RecordingStore.GetRewindRecording(null);

            Assert.Null(result);
        }

        #endregion

        #region CanRewind tree integration

        [Fact]
        public void CanRewind_TreeBranch_ResolvesViaRoot()
        {
            // CanRewind checks file existence via KSP API (not available in unit tests).
            // Verify that the tree-aware lookup resolves the save filename, then confirm
            // CanRewind would get past the "No rewind save available" guard by checking
            // GetRewindSaveFileName directly — the file existence check is covered by
            // existing integration tests.
            var tree = BuildTree("tree4", "root4", "parsek_rw_r4",
                ("branch4", "BranchVessel"));
            RecordingStore.AddCommittedTreeForTesting(tree);

            var branch = tree.Recordings["branch4"];

            // Branch has no direct save, but GetRewindSaveFileName resolves through root
            string resolvedSave = RecordingStore.GetRewindSaveFileName(branch);
            Assert.Equal("parsek_rw_r4", resolvedSave);

            // Without tree lookup, branch would have no save
            Assert.Null(branch.RewindSaveFileName);
        }

        [Fact]
        public void CanRewind_NoBranchNoSave_ReturnsNoSaveAvailable()
        {
            var rec = new Recording
            {
                RecordingId = "nosave",
                VesselName = "NoSaveVessel"
            };

            string reason;
            bool result = RecordingStore.CanRewind(rec, out reason, isRecording: false);

            Assert.False(result);
            Assert.Equal("No rewind save available", reason);
        }

        #endregion

        #region Logging

        [Fact]
        public void GetRewindRecording_TreeBranch_NoExtraLogging()
        {
            // The lookup helpers are called per-frame by the UI, so they must not
            // produce log output on every call.
            var tree = BuildTree("tree5", "root5", "parsek_rw_r5",
                ("branch5", "LogTestBranch"));
            var trees = new List<RecordingTree> { tree };

            var branch = tree.Recordings["branch5"];
            logLines.Clear();

            RecordingStore.GetRewindRecording(branch, trees);

            // Lookup helpers should not log (per-frame hot path)
            Assert.Empty(logLines);
        }

        #endregion
    }
}
