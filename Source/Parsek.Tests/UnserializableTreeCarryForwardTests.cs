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
            ParsekScenario.CarriedRecordingSidecarExistsOverrideForTesting = null;
            ParsekScenario.SaveTargetFileNameHintForCurrentSave = null;
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
            // The reconciliation keeps a carried RECORDING only when its sidecar is on
            // disk; outside KSP the real probe cannot resolve a path, so declare it.
            ParsekScenario.CarriedRecordingSidecarExistsOverrideForTesting = _ => true;
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
            ParsekScenario.CarriedRecordingSidecarExistsOverrideForTesting = _ => true;
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
        public void TryCarryForward_NullInputs_ReturnFalseAndNeverThrow()
        {
            var node = new ConfigNode("ParsekScenario");
            var live = new RecordingTree { Id = "tree-x" };
            Assert.False(ParsekScenario.TryCarryForwardCommittedTreeNodeFromLoadedSave(
                node, null, live, out _, out _));
            Assert.False(ParsekScenario.TryCarryForwardCommittedTreeNodeFromLoadedSave(
                null, new ConfigNode("ParsekScenario"), live, out _, out _));
            Assert.False(ParsekScenario.TryCarryForwardCommittedTreeNodeFromLoadedSave(
                node, new ConfigNode("ParsekScenario"), null, out _, out _));
            Assert.False(ParsekScenario.TryCarryForwardCommittedTreeNodeFromLoadedSave(
                node, new ConfigNode("ParsekScenario"), new RecordingTree { Id = null }, out _, out _));
            Assert.Empty(node.GetNodes("RECORDING_TREE"));
        }

        // catches: THE RESURRECTION. The on-disk node is older than memory, so carried
        // verbatim it re-lists recordings the player has since deleted - ids whose
        // sidecars are gone, leaving the save disagreeing with the corpus the next load's
        // orphan reap works against. Only recordings the LIVE tree still has AND whose
        // sidecars are on disk may be carried.
        [Fact]
        public void CarryForward_DropsRecordingsTheLiveTreeNoLongerHas()
        {
            var live = new RecordingTree { Id = "tree-r", TreeName = "Reconciled", RootRecordingId = "rec-keep" };
            live.AddOrReplaceRecording(new Recording { RecordingId = "rec-keep", TreeId = "tree-r" });
            ParsekScenario.CarriedRecordingSidecarExistsOverrideForTesting = _ => true;

            ConfigNode source = BuildParsekNode("tree-r", "Reconciled", "rec-keep", "rec-deleted");
            var target = new ConfigNode("ParsekScenario");

            Assert.True(ParsekScenario.TryCarryForwardCommittedTreeNodeFromLoadedSave(
                target, source, live, out int carried, out int dropped));

            Assert.Equal(1, carried);
            Assert.Equal(1, dropped);
            ConfigNode[] recs = target.GetNodes("RECORDING_TREE")[0].GetNodes("RECORDING");
            Assert.Single(recs);
            Assert.Equal("rec-keep", recs[0].GetValue("recordingId"));
        }

        // The other half: the id is still in the live tree but its sidecar is gone, so
        // carrying it would describe data this save no longer has.
        [Fact]
        public void CarryForward_DropsRecordingsWhoseSidecarsAreGone()
        {
            var live = new RecordingTree { Id = "tree-s", TreeName = "Sidecars", RootRecordingId = "rec-a" };
            live.AddOrReplaceRecording(new Recording { RecordingId = "rec-a", TreeId = "tree-s" });
            live.AddOrReplaceRecording(new Recording { RecordingId = "rec-b", TreeId = "tree-s" });
            ParsekScenario.CarriedRecordingSidecarExistsOverrideForTesting =
                id => string.Equals(id, "rec-a", StringComparison.Ordinal);

            ConfigNode source = BuildParsekNode("tree-s", "Sidecars", "rec-a", "rec-b");
            var target = new ConfigNode("ParsekScenario");

            Assert.True(ParsekScenario.TryCarryForwardCommittedTreeNodeFromLoadedSave(
                target, source, live, out int carried, out int dropped));

            Assert.Equal(1, carried);
            Assert.Equal(1, dropped);
            Assert.Equal("rec-a",
                target.GetNodes("RECORDING_TREE")[0].GetNodes("RECORDING")[0].GetValue("recordingId"));
        }

        // Reconciled down to nothing: a husk is not worth writing and would itself read
        // as a corrupt tree, so the carry is refused rather than writing an empty shell.
        [Fact]
        public void CarryForward_RefusesWhenReconciliationLeavesNoRecordings()
        {
            var live = new RecordingTree { Id = "tree-e", TreeName = "Empty", RootRecordingId = "rec-live" };
            live.AddOrReplaceRecording(new Recording { RecordingId = "rec-live", TreeId = "tree-e" });
            ParsekScenario.CarriedRecordingSidecarExistsOverrideForTesting = _ => true;

            ConfigNode source = BuildParsekNode("tree-e", "Empty", "rec-gone-1", "rec-gone-2");
            var target = new ConfigNode("ParsekScenario");
            logLines.Clear();

            Assert.False(ParsekScenario.TryCarryForwardCommittedTreeNodeFromLoadedSave(
                target, source, live, out int carried, out int dropped));

            Assert.Equal(0, carried);
            Assert.Equal(2, dropped);
            Assert.Empty(target.GetNodes("RECORDING_TREE"));
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") && l.Contains("would resurrect deleted data"));
        }

        [Fact]
        public void IsCarriedRecordingStillReal_RequiresBothHalves()
        {
            var live = new RecordingTree { Id = "t", RootRecordingId = "rec-1" };
            live.AddOrReplaceRecording(new Recording { RecordingId = "rec-1", TreeId = "t" });

            ParsekScenario.CarriedRecordingSidecarExistsOverrideForTesting = _ => true;
            Assert.True(ParsekScenario.IsCarriedRecordingStillReal(live, "rec-1"));
            Assert.False(ParsekScenario.IsCarriedRecordingStillReal(live, "rec-absent"));

            ParsekScenario.CarriedRecordingSidecarExistsOverrideForTesting = _ => false;
            Assert.False(ParsekScenario.IsCarriedRecordingStillReal(live, "rec-1"));

            Assert.False(ParsekScenario.IsCarriedRecordingStillReal(null, "rec-1"));
            Assert.False(ParsekScenario.IsCarriedRecordingStillReal(live, null));
        }

        // ==================================================================
        // Fix C - the committed-overlap discard guard's durable input
        // ==================================================================

        // Finding 5: the parked (isActive / isPending) nodes' recordings MUST be in the
        // hint. TryRestoreActiveTreeNode / TryRestorePendingTreeNode restore exactly those
        // nodes later in the SAME OnLoad, so their sidecars are about to be hydrated -
        // deleting them in the discard window loses data the load was seconds from
        // bringing back. The tree actually being discarded is the in-memory stale pending
        // tree from a PREVIOUS save, which is not in this node at all.
        [Fact]
        public void CollectCommittedRecordingIds_IncludesParkedNodesAboutToBeRestored()
        {
            var parsek = new ConfigNode("ParsekScenario");
            ConfigNode committed = parsek.AddNode("RECORDING_TREE");
            committed.AddValue("id", "tree-a");
            committed.AddNode("RECORDING").AddValue("recordingId", "rec-1");
            committed.AddNode("RECORDING").AddValue("recordingId", "rec-2");

            ConfigNode active = parsek.AddNode("RECORDING_TREE");
            active.AddValue("id", "tree-b");
            active.AddValue("isActive", "True");
            active.AddNode("RECORDING").AddValue("recordingId", "rec-active");
            ConfigNode pending = parsek.AddNode("RECORDING_TREE");
            pending.AddValue("id", "tree-c");
            pending.AddValue("isPending", "True");
            pending.AddNode("RECORDING").AddValue("recordingId", "rec-pending");

            HashSet<string> ids = ParsekScenario.CollectCommittedRecordingIdsFromScenarioNode(parsek);

            Assert.Equal(4, ids.Count);
            Assert.Contains("rec-1", ids);
            Assert.Contains("rec-2", ids);
            Assert.Contains("rec-active", ids);
            Assert.Contains("rec-pending", ids);
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

        /// <summary>An in-memory ParsekScenario node holding one committed tree with the
        /// given recording ids - the "on-disk save" input to the carry-forward.</summary>
        private static ConfigNode BuildParsekNode(
            string treeId, string treeName, params string[] recordingIds)
        {
            var parsek = new ConfigNode("ParsekScenario");
            ConfigNode treeNode = parsek.AddNode("RECORDING_TREE");
            treeNode.AddValue("id", treeId);
            treeNode.AddValue("treeName", treeName);
            for (int i = 0; i < recordingIds.Length; i++)
                treeNode.AddNode("RECORDING").AddValue("recordingId", recordingIds[i]);
            return parsek;
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
