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
            ParsekScenario.ResetInstanceForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            EffectiveState.ResetCachesForTesting();
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

        [Fact]
        public void CanRewind_TreeBranchRootHasNoSave_ReturnsNoSaveAvailable()
        {
            var tree = BuildTree("tree6", "root6", null,
                ("branch6", "EVAKerbal"));
            RecordingStore.AddCommittedTreeForTesting(tree);

            var branch = tree.Recordings["branch6"];
            string resolvedSave = RecordingStore.GetRewindSaveFileName(branch);

            string reason;
            bool result = RecordingStore.CanRewindWithResolvedSaveState(
                resolvedSave, saveExists: false, out reason, isRecording: false);

            Assert.False(result);
            Assert.Equal("No rewind save available", reason);
        }

        [Fact]
        public void CanRewind_TreeBranchSaveFileMissing_ReturnsSaveMissing()
        {
            var tree = BuildTree("tree7", "root7", "parsek_rw_r7",
                ("branch7", "EVAKerbal"));
            RecordingStore.AddCommittedTreeForTesting(tree);

            var branch = tree.Recordings["branch7"];
            string resolvedSave = RecordingStore.GetRewindSaveFileName(branch);

            string reason;
            bool result = RecordingStore.CanRewindWithResolvedSaveState(
                resolvedSave, saveExists: false, out reason, isRecording: false);

            Assert.False(result);
            Assert.Equal("Rewind save file missing", reason);
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

        #region ShouldShowLegacyRewindButton

        // The recordings table draws a legacy "R" (Rewind-to-launch) button on
        // every recording with a resolvable rewind save. Before the owner gate,
        // tree branches inherited the save through GetRewindRecording and each
        // branch row drew a duplicate R that just rewound to the tree root's
        // launch — four-plus identical buttons after a normal merge. The
        // helper enforces:
        //   1. Standalone owner → button.
        //   2. Tree root owner → button.
        //   3. Tree branch (non-owner) → suppressed.
        //   4. Unfinished-flight chain member → suppressed (Rewind-to-Staging).
        //   5. Future recording → suppressed (FF path renders instead).

        [Fact]
        public void ShouldShowLegacyRewindButton_StandaloneWithOwnSave_ReturnsTrue()
        {
            var rec = new Recording
            {
                RecordingId = "standalone_owner",
                VesselName = "StandaloneVessel",
                RewindSaveFileName = "parsek_rw_solo",
                // Standalone — no TreeId.
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });

            // now > StartUT — past/active branch.
            bool result = RecordingsTableUI.ShouldShowLegacyRewindButton(rec, now: 200.0);

            Assert.True(result);
        }

        [Fact]
        public void ShouldShowLegacyRewindButton_TreeRootWithSave_ReturnsTrue()
        {
            // Tree root owns the rewind save on behalf of the tree.
            // GetRewindRecording returns the root itself, so the owner gate
            // passes and the R button renders on the launch row.
            var tree = BuildTree("tree_owner", "root_owner", "parsek_rw_root_owner",
                ("branch_owner", "BranchVessel"));
            RecordingStore.AddCommittedTreeForTesting(tree);

            var rootRec = tree.Recordings["root_owner"];
            rootRec.Points.Add(new TrajectoryPoint { ut = 50.0 });

            bool result = RecordingsTableUI.ShouldShowLegacyRewindButton(rootRec, now: 100.0);

            Assert.True(result);
            // Sanity: GetRewindRecording resolves to the same instance.
            Assert.Same(rootRec, RecordingStore.GetRewindRecording(rootRec));
        }

        [Fact]
        public void ShouldShowLegacyRewindButton_TreeBranchNonOwner_ReturnsFalse()
        {
            // Branch has no own RewindSaveFileName but inherits one through
            // the tree root via GetRewindRecording. Without the owner gate this
            // row would draw a duplicate R that just rewinds the tree to the
            // root's launch — exactly the duplicate the user complained about.
            var tree = BuildTree("tree_branch", "root_branch", "parsek_rw_root_branch",
                ("branch_dup", "DebrisStage"));
            RecordingStore.AddCommittedTreeForTesting(tree);

            var branch = tree.Recordings["branch_dup"];
            branch.Points.Add(new TrajectoryPoint { ut = 60.0 });

            // Sanity: branch inherits the save through the tree root.
            Assert.Equal("parsek_rw_root_branch",
                RecordingStore.GetRewindSaveFileName(branch));
            // …but GetRewindRecording resolves to the root, NOT the branch.
            Assert.NotSame(branch, RecordingStore.GetRewindRecording(branch));

            bool result = RecordingsTableUI.ShouldShowLegacyRewindButton(branch, now: 100.0);

            Assert.False(result);
        }

        [Fact]
        public void ShouldShowLegacyRewindButton_ChainMemberOfUnfinishedFlight_ReturnsFalse()
        {
            // A chain member with a parentBranchPointId pointing at an active
            // RewindPoint becomes an Unfinished Flight; the row uses
            // Rewind-to-Staging (drawn by DrawUnfinishedFlightRewindButton)
            // and must NOT fall back to the legacy rewind-to-launch even when
            // it owns its own save.
            var bp = new BranchPoint { Id = "bp_uf", Type = BranchPointType.JointBreak };
            var tree = new RecordingTree
            {
                Id = "tree_uf",
                TreeName = "ChainTree",
                RootRecordingId = "rec_head_uf",
                BranchPoints = new List<BranchPoint> { bp }
            };
            var head = new Recording
            {
                RecordingId = "rec_head_uf",
                VesselName = "ChainHead",
                MergeState = MergeState.Immutable,
                TerminalStateValue = null,
                ParentBranchPointId = "bp_uf",
                TreeId = "tree_uf",
                ChainId = "chain_uf",
                ChainIndex = 0,
                // Owns the legacy launch save too — gate must still suppress.
                RewindSaveFileName = "parsek_rw_uf"
            };
            head.Points.Add(new TrajectoryPoint { ut = 10.0 });
            var tip = new Recording
            {
                RecordingId = "rec_tip_uf",
                VesselName = "ChainTip",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
                ParentBranchPointId = null,
                TreeId = "tree_uf",
                ChainId = "chain_uf",
                ChainIndex = 1
            };
            tip.Points.Add(new TrajectoryPoint { ut = 20.0 });
            tree.AddOrReplaceRecording(head);
            tree.AddOrReplaceRecording(tip);
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddRecordingWithTreeForTesting(head);
            RecordingStore.AddRecordingWithTreeForTesting(tip);

            var rp = new RewindPoint
            {
                RewindPointId = "rp_uf",
                BranchPointId = "bp_uf",
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = "rec_head_uf",
                        Controllable = true
                    }
                }
            };
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint> { rp },
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();

            // Confirm head is in fact a chain member of an unfinished flight.
            Assert.True(EffectiveState.IsChainMemberOfUnfinishedFlight(head));

            bool result = RecordingsTableUI.ShouldShowLegacyRewindButton(head, now: 100.0);

            Assert.False(result);
        }

        [Fact]
        public void ShouldShowLegacyRewindButton_FutureRecording_ReturnsFalse()
        {
            // Future recording: now < StartUT → the FF column renders instead
            // and the legacy R gate must stay closed regardless of save state.
            var rec = new Recording
            {
                RecordingId = "future_solo",
                VesselName = "FutureVessel",
                RewindSaveFileName = "parsek_rw_future"
            };
            rec.Points.Add(new TrajectoryPoint { ut = 500.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 600.0 });

            bool result = RecordingsTableUI.ShouldShowLegacyRewindButton(rec, now: 100.0);

            Assert.False(result);
        }

        [Fact]
        public void ShouldShowLegacyRewindButton_NullRecording_ReturnsFalse()
        {
            bool result = RecordingsTableUI.ShouldShowLegacyRewindButton(null, now: 100.0);

            Assert.False(result);
        }

        [Fact]
        public void ShouldShowLegacyRewindButton_NoRewindSaveAnywhere_ReturnsFalse()
        {
            // Standalone-shaped recording with no save and no tree → owner is
            // null, gate must close.
            var rec = new Recording
            {
                RecordingId = "no_save",
                VesselName = "NoSave"
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });

            bool result = RecordingsTableUI.ShouldShowLegacyRewindButton(rec, now: 200.0);

            Assert.False(result);
        }

        #endregion
    }
}
