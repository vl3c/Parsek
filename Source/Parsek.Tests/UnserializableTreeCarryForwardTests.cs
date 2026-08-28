using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Guards for QUICKLOAD-OVER-COMMITTED-RESTORE-OVERLAP-DELETES-TREE-ON-SAVE
    /// (found 2026-08-28 by the H39/H40 isolated censuses), which had two halves.
    ///
    /// <para><b>The invariant (fix B): a save must never DELETE a tree it cannot
    /// serialize.</b> <c>SaveTreeRecordings</c> skipped the <c>AddNode</c> for any tree
    /// with an unwritable recording. The skip condition is STICKY (a recording whose
    /// .prec is gone stays that way), so one unserializable recording permanently
    /// dropped a whole mission's metadata from persistent.sfs on that save and every
    /// save after it. The tree's last-known-good node is now carried forward from the
    /// on-disk save instead.</para>
    ///
    /// <para><b>The trigger (fix C): the committed-overlap discard guard was answered
    /// against an empty store.</b> <c>DiscardPendingTree</c> refuses to delete sidecars
    /// whose recording id is still committed (#431) - the guard that makes the
    /// copy-on-write committed-tree restore (which clones a committed tree into the
    /// ACTIVE slot with ids PRESERVED) safe to throw away. On a COLD load
    /// <c>DiscardStalePendingState</c> runs BEFORE <c>LoadRecordingTrees</c>, so every
    /// id read as not-committed and the discard deleted the COMMITTED tree's sidecars.
    /// The save node's own committed ids now answer the guard in that window.</para>
    /// </summary>
    [Collection("Sequential")]
    public class UnserializableTreeCarryForwardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> cleanupFiles = new List<string>();
        private readonly List<string> cleanupRoots = new List<string>();
        private readonly string originalSaveFolder;
        private readonly GameScenes originalScene;

        public UnserializableTreeCarryForwardTests()
        {
            originalSaveFolder = HighLogic.SaveFolder;
            originalScene = HighLogic.LoadedScene;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            HighLogic.LoadedScene = GameScenes.SPACECENTER;
        }

        public void Dispose()
        {
            HighLogic.SaveFolder = originalSaveFolder;
            HighLogic.LoadedScene = originalScene;
            ParsekScenario.PersistentSavePathOverrideForTesting = null;
            RecordingStore.SkipSidecarCurrencyCheckForTesting = false;
            RecordingStore.ClearDurableCommittedRecordingIdHint();
            for (int i = 0; i < cleanupFiles.Count; i++)
            {
                try { if (File.Exists(cleanupFiles[i])) File.Delete(cleanupFiles[i]); }
                catch { }
            }
            for (int i = 0; i < cleanupRoots.Count; i++)
            {
                try { if (Directory.Exists(cleanupRoots[i])) Directory.Delete(cleanupRoots[i], true); }
                catch { }
            }
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
        }

        // ==================================================================
        // Fix B - the save-must-not-delete invariant
        // ==================================================================

        // THE INVARIANT, end to end through the real SaveTreeRecordings: a committed
        // tree whose recording cannot be written (SidecarLoadFailed + empty in-memory
        // state, the shape the bug-#585 guard refuses to clobber) keeps its node in the
        // written save, carried forward from the on-disk one.
        [Fact]
        public void SaveTreeRecordings_UnserializableTree_KeepsItsNodeFromTheOnDiskSave()
        {
            RecordingStore.SkipSidecarCurrencyCheckForTesting = false;
            PrepareSaveFolder("carry-forward");

            RecordingTree tree = MakeUnserializableTree("tree-x", "Mun Program", "rec-x");
            RecordingStore.AddCommittedTreeForTesting(tree);
            ParsekScenario.PersistentSavePathOverrideForTesting =
                WriteTempPersistentSfs(("tree-x", "Mun Program", "rec-x"));

            var node = new ConfigNode("ParsekScenario");
            ParsekScenario.SaveTreeRecordings(node);

            ConfigNode[] treeNodes = node.GetNodes("RECORDING_TREE");
            Assert.Single(treeNodes);
            Assert.Equal("tree-x", treeNodes[0].GetValue("id"));
            // Carried forward WITH its payload, not as a hollow stub.
            Assert.Equal("Mun Program", treeNodes[0].GetValue("treeName"));
            Assert.Single(treeNodes[0].GetNodes("RECORDING"));
            Assert.Equal("rec-x", treeNodes[0].GetNodes("RECORDING")[0].GetValue("recordingId"));
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") && l.Contains("carried forward last-known-good"));
        }

        // The honest failure mode: nothing on disk to carry forward. The tree is still
        // dropped (there is nothing to preserve), but LOUDLY, at Error - never silently
        // as before.
        [Fact]
        public void SaveTreeRecordings_UnserializableTreeWithNoDiskNode_DropsLoudly()
        {
            RecordingStore.SkipSidecarCurrencyCheckForTesting = false;
            PrepareSaveFolder("carry-forward-none");

            RecordingStore.AddCommittedTreeForTesting(
                MakeUnserializableTree("tree-y", "Minmus Program", "rec-y"));
            // On-disk save carries a DIFFERENT tree, so there is no node for tree-y.
            ParsekScenario.PersistentSavePathOverrideForTesting =
                WriteTempPersistentSfs(("tree-other", "Other", "rec-other"));

            var node = new ConfigNode("ParsekScenario");
            ParsekScenario.SaveTreeRecordings(node);

            Assert.Empty(node.GetNodes("RECORDING_TREE"));
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") && l.Contains("no last-known-good node in the on-disk save"));
        }

        // A tree that CAN be serialized is untouched by the new branch (no carry-forward,
        // no duplicate node).
        [Fact]
        public void SaveTreeRecordings_SerializableTree_WritesFromMemoryOnce()
        {
            RecordingStore.SkipSidecarCurrencyCheckForTesting = true;
            PrepareSaveFolder("carry-forward-healthy");

            RecordingStore.AddCommittedTreeForTesting(
                MakeUnserializableTree("tree-z", "Healthy", "rec-z"));
            ParsekScenario.PersistentSavePathOverrideForTesting =
                WriteTempPersistentSfs(("tree-z", "Healthy", "rec-z"));

            var node = new ConfigNode("ParsekScenario");
            ParsekScenario.SaveTreeRecordings(node);

            Assert.Single(node.GetNodes("RECORDING_TREE"));
            Assert.DoesNotContain(logLines, l => l.Contains("carried forward last-known-good"));
        }

        [Fact]
        public void FindCommittedTreeNodeById_MatchesCommitted_AndSkipsActiveOrPending()
        {
            var parsek = new ConfigNode("ParsekScenario");
            parsek.AddNode("RECORDING_TREE").AddValue("id", "committed-1");
            ConfigNode activeNode = parsek.AddNode("RECORDING_TREE");
            activeNode.AddValue("id", "parked-1");
            activeNode.AddValue("isActive", "True");
            ConfigNode pendingNode = parsek.AddNode("RECORDING_TREE");
            pendingNode.AddValue("id", "parked-2");
            pendingNode.AddValue("isPending", "True");

            Assert.NotNull(ParsekScenario.FindCommittedTreeNodeById(parsek, "committed-1"));
            // A parked node must never be carried forward as committed history.
            Assert.Null(ParsekScenario.FindCommittedTreeNodeById(parsek, "parked-1"));
            Assert.Null(ParsekScenario.FindCommittedTreeNodeById(parsek, "parked-2"));
            Assert.Null(ParsekScenario.FindCommittedTreeNodeById(parsek, "absent"));
            Assert.Null(ParsekScenario.FindCommittedTreeNodeById(null, "committed-1"));
            Assert.Null(ParsekScenario.FindCommittedTreeNodeById(parsek, null));
        }

        [Fact]
        public void TryCarryForward_MissingOrUnparseableFile_ReturnsFalseAndNeverThrows()
        {
            var node = new ConfigNode("ParsekScenario");
            Assert.False(ParsekScenario.TryCarryForwardCommittedTreeNodeFromDiskSave(
                node, Path.Combine(Path.GetTempPath(), "parsek-absent-" + Guid.NewGuid().ToString("N") + ".sfs"), "tree-x"));
            Assert.False(ParsekScenario.TryCarryForwardCommittedTreeNodeFromDiskSave(node, null, "tree-x"));
            Assert.False(ParsekScenario.TryCarryForwardCommittedTreeNodeFromDiskSave(node, "some.sfs", null));
            Assert.False(ParsekScenario.TryCarryForwardCommittedTreeNodeFromDiskSave(null, "some.sfs", "tree-x"));
            Assert.Empty(node.GetNodes("RECORDING_TREE"));
        }

        // ==================================================================
        // Fix C - the committed-overlap discard guard's durable input
        // ==================================================================

        [Fact]
        public void CollectCommittedRecordingIds_ReadsCommittedTreesOnly()
        {
            var parsek = new ConfigNode("ParsekScenario");
            ConfigNode committed = parsek.AddNode("RECORDING_TREE");
            committed.AddValue("id", "tree-a");
            committed.AddNode("RECORDING").AddValue("recordingId", "rec-1");
            committed.AddNode("RECORDING").AddValue("recordingId", "rec-2");

            // The parked clone whose discard would otherwise delete rec-1/rec-2's files:
            // its own node must NOT contribute ids (it is the thing being discarded).
            ConfigNode active = parsek.AddNode("RECORDING_TREE");
            active.AddValue("id", "tree-a");
            active.AddValue("isActive", "True");
            active.AddNode("RECORDING").AddValue("recordingId", "rec-1");
            ConfigNode pending = parsek.AddNode("RECORDING_TREE");
            pending.AddValue("id", "tree-b");
            pending.AddValue("isPending", "True");
            pending.AddNode("RECORDING").AddValue("recordingId", "rec-3");

            HashSet<string> ids = ParsekScenario.CollectCommittedRecordingIdsFromScenarioNode(parsek);

            Assert.Equal(2, ids.Count);
            Assert.Contains("rec-1", ids);
            Assert.Contains("rec-2", ids);
            Assert.DoesNotContain("rec-3", ids);
            Assert.Empty(ParsekScenario.CollectCommittedRecordingIdsFromScenarioNode(null));
        }

        // The pure decision: an id committed in memory OR declared committed by the save
        // file keeps its events and sidecars. Only a pending-owned id may be destroyed.
        [Theory]
        [InlineData(false, false, false)] // pending-only -> purge/delete
        [InlineData(true, false, true)]   // committed in memory (the pre-existing #431 guard)
        [InlineData(false, true, true)]   // THE FIX: store not loaded yet, save says committed
        [InlineData(true, true, true)]
        public void ShouldPreserveCommittedOverlapOnPendingDiscard_Matrix(
            bool committedInMemory, bool durableHint, bool expected)
        {
            Assert.Equal(expected, RecordingStore.ShouldPreserveCommittedOverlapOnPendingDiscard(
                committedInMemory, durableHint));
        }

        [Fact]
        public void DurableCommittedRecordingIdHint_RoundTripsAndClears()
        {
            Assert.False(RecordingStore.IsDurableCommittedRecordingIdHint("rec-1"));

            RecordingStore.SetDurableCommittedRecordingIdHint(
                new HashSet<string>(StringComparer.Ordinal) { "rec-1", "rec-2" });
            Assert.True(RecordingStore.IsDurableCommittedRecordingIdHint("rec-1"));
            Assert.True(RecordingStore.IsDurableCommittedRecordingIdHint("rec-2"));
            Assert.False(RecordingStore.IsDurableCommittedRecordingIdHint("rec-3"));
            Assert.False(RecordingStore.IsDurableCommittedRecordingIdHint(null));
            Assert.False(RecordingStore.IsDurableCommittedRecordingIdHint(""));

            RecordingStore.ClearDurableCommittedRecordingIdHint();
            Assert.False(RecordingStore.IsDurableCommittedRecordingIdHint("rec-1"));

            // An empty set is stored as "no hint" so the guard cannot be armed vacuously.
            RecordingStore.SetDurableCommittedRecordingIdHint(new HashSet<string>(StringComparer.Ordinal));
            Assert.False(RecordingStore.IsDurableCommittedRecordingIdHint("rec-1"));
            RecordingStore.SetDurableCommittedRecordingIdHint(null);
            Assert.False(RecordingStore.IsDurableCommittedRecordingIdHint("rec-1"));
        }

        // THE TRIGGER, at the decision level: a pending tree that is a copy-on-write
        // clone of a committed tree (ids PRESERVED) is discarded while the committed
        // store is still empty - exactly the cold-load ordering. With the save's ids
        // published, the clone's discard must route those ids to the PRESERVE branch
        // instead of the delete branch.
        //
        // The assertion is on the decision (the counters the discard logs), not on the
        // file system: DeleteRecordingFiles resolves its paths through
        // KSPUtil.ApplicationRootPath, which throws outside KSP, so a file-existence
        // assertion here would pass without the delete branch ever being reachable -
        // i.e. it would be vacuous rather than a guard.
        [Fact]
        public void DiscardPendingTree_ColdLoadOverlap_RoutesOverlapIdsToPreserveViaDurableHint()
        {
            RecordingStore.StashPendingTree(
                MakeUnserializableTree("tree-overlap", "Overlap", "rec-overlap"),
                PendingTreeState.Finalized);
            Assert.Empty(RecordingStore.CommittedTrees); // cold load: trees not loaded yet

            logLines.Clear();
            RecordingStore.SetDurableCommittedRecordingIdHint(
                new HashSet<string>(StringComparer.Ordinal) { "rec-overlap" });
            try
            {
                RecordingStore.DiscardPendingTree(preserveIrreversibleLiveGameplay: false);
            }
            finally
            {
                RecordingStore.ClearDurableCommittedRecordingIdHint();
            }

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") && l.Contains("skipped deleting 1 recording sidecar set(s)")
                && l.Contains("viaDurableSaveHint=1"));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("committed-overlap sidecars preserved by durable save hint"));
            // The destructive event/milestone purge is scoped the same way.
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("skipped destructive event/milestone purge for 1 committed-overlap")
                && l.Contains("viaDurableSaveHint=1"));
        }

        // The negative control: with no hint (and no committed store) the same discard
        // treats the id as pending-owned and takes the delete branch - the pre-fix
        // behaviour, and the data loss itself.
        [Fact]
        public void DiscardPendingTree_ColdLoadOverlap_WithoutHint_TakesTheDeleteBranch()
        {
            RecordingStore.StashPendingTree(
                MakeUnserializableTree("tree-control", "Control", "rec-control"),
                PendingTreeState.Finalized);
            RecordingStore.ClearDurableCommittedRecordingIdHint();

            logLines.Clear();
            RecordingStore.DiscardPendingTree(preserveIrreversibleLiveGameplay: false);

            Assert.DoesNotContain(logLines, l => l.Contains("skipped deleting"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("committed-overlap sidecars preserved by durable save hint"));
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        /// <summary>
        /// A tree whose single recording is in the unserializable shape the bug-#585
        /// guard refuses to clobber: marked SidecarLoadFailed with completely empty
        /// in-memory state, so SaveRecordingFiles skips and the recording stays unsafe.
        /// </summary>
        private static RecordingTree MakeUnserializableTree(
            string treeId, string treeName, string recordingId)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeName,
                RootRecordingId = recordingId,
            };
            var rec = new Recording
            {
                RecordingId = recordingId,
                VesselName = treeName + " Vessel",
                TreeId = treeId,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 110.0,
            };
            RecordingStore.MarkSidecarLoadFailure(rec, "trajectory-missing");
            tree.AddOrReplaceRecording(rec);
            return tree;
        }

        private string PrepareSaveFolder(string label)
        {
            string saveFolder = "parsek-test-carry-forward-" + label + "-" + Guid.NewGuid().ToString("N");
            HighLogic.SaveFolder = saveFolder;
            string root = Path.Combine(Path.GetTempPath(), saveFolder);
            string recordingsDir = Path.Combine(root, "Parsek", "Recordings");
            Directory.CreateDirectory(recordingsDir);
            RecordingStore.CleanOrphanFilesDirectoryOverrideForTesting = recordingsDir;
            cleanupRoots.Add(root);
            return recordingsDir;
        }

        /// <summary>
        /// A minimal KSP-shaped .sfs holding one committed RECORDING_TREE with one
        /// RECORDING, behind a decoy SCENARIO so the finder must match by name.
        /// </summary>
        private string WriteTempPersistentSfs((string treeId, string treeName, string recordingId) tree)
        {
            var root = new ConfigNode();
            ConfigNode game = root.AddNode("GAME");
            game.AddNode("SCENARIO").AddValue("name", "ContractSystem"); // decoy
            ConfigNode scenario = game.AddNode("SCENARIO");
            scenario.AddValue("name", "ParsekScenario");
            ConfigNode treeNode = scenario.AddNode("RECORDING_TREE");
            treeNode.AddValue("id", tree.treeId);
            treeNode.AddValue("treeName", tree.treeName);
            treeNode.AddNode("RECORDING").AddValue("recordingId", tree.recordingId);

            string path = Path.Combine(Path.GetTempPath(),
                "parsek-test-carry-sfs-" + Guid.NewGuid().ToString("N") + ".sfs");
            root.Save(path);
            cleanupFiles.Add(path);
            return path;
        }
    }
}
