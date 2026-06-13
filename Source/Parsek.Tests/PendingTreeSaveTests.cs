using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class PendingTreeSaveTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string originalSaveFolder;
        private readonly GameScenes originalScene;
        private readonly List<string> cleanupRoots = new List<string>();

        public PendingTreeSaveTests()
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
            RecordingStore.SkipSidecarCurrencyCheckForTesting = true;
            HighLogic.LoadedScene = GameScenes.SPACECENTER;
        }

        public void Dispose()
        {
            HighLogic.SaveFolder = originalSaveFolder;
            HighLogic.LoadedScene = originalScene;
            RecordingStore.CleanOrphanFilesDirectoryOverrideForTesting = null;
            for (int i = 0; i < cleanupRoots.Count; i++)
            {
                try
                {
                    if (Directory.Exists(cleanupRoots[i]))
                        Directory.Delete(cleanupRoots[i], true);
                }
                catch { }
            }
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
        }

        [Fact]
        public void SaveTreeRecordings_WritesPendingTreeNode_WithIsPendingTrue()
        {
            var tree = MakeTree("tree_pending_save", "Pending Mission", "rec_pending_save");
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);

            var node = new ConfigNode("PARSEK_SCENARIO");
            ParsekScenario.SaveTreeRecordings(node);

            ConfigNode[] treeNodes = node.GetNodes("RECORDING_TREE");
            Assert.Single(treeNodes);
            Assert.True(ParsekScenario.IsPendingTreeNode(treeNodes[0]));
            Assert.False(ParsekScenario.IsActiveTreeNode(treeNodes[0]));
            Assert.Equal("True", treeNodes[0].GetValue("isPending"));
            Assert.Equal(tree.Id, treeNodes[0].GetValue("id"));
            Assert.True(RecordingStore.PendingTreeSerializedForSave);
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") && l.Contains("wrote PENDING tree")
                && l.Contains("Pending Mission"));
        }

        [Fact]
        public void SaveTreeRecordings_PendingTreeSuppressesStrandedSidecarWarn()
        {
            string recordingsDir = CreateRecordingsDir("pending-suppresses-warn");
            File.WriteAllText(Path.Combine(recordingsDir, "rec_pending_sidecar.prec"), "p");

            var tree = MakeTree("tree_pending_sidecar", "Pending Sidecar", "rec_pending_sidecar");
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            logLines.Clear();

            var node = new ConfigNode("PARSEK_SCENARIO");
            ParsekScenario.SaveTreeRecordings(node);

            Assert.Single(node.GetNodes("RECORDING_TREE"));
            Assert.DoesNotContain(logLines, l => l.Contains("stranded sidecar"));
        }

        [Fact]
        public void SaveTreeRecordings_WritesLimboPendingAsActiveResumeNode_ForCrashSafety()
        {
            // Data-loss fix: a Limbo (quickload-resume) pending tree used to be SKIPPED by
            // OnSave, so a save in the resume window dropped it from persistent.sfs and its
            // mission + sidecars were later purged as orphans. It is now serialized with the
            // isActive marker (so it round-trips through TryRestoreActiveTreeNode back into the
            // Limbo resume flow) and the stranded-sidecar warn no longer fires.
            string recordingsDir = CreateRecordingsDir("limbo-active-resume");
            File.WriteAllText(Path.Combine(recordingsDir, "rec_limbo.prec"), "p");
            var limbo = MakeTree("tree_limbo", "Limbo Mission", "rec_limbo");
            RecordingStore.StashPendingTree(limbo, PendingTreeState.Limbo);

            var node = new ConfigNode("PARSEK_SCENARIO");
            ParsekScenario.SaveTreeRecordings(node);

            ConfigNode[] treeNodes = node.GetNodes("RECORDING_TREE");
            Assert.Single(treeNodes);
            Assert.True(ParsekScenario.IsActiveTreeNode(treeNodes[0]));
            Assert.False(ParsekScenario.IsPendingTreeNode(treeNodes[0]));
            Assert.Equal("tree_limbo", treeNodes[0].GetValue("id"));
            Assert.True(RecordingStore.PendingTreeSerializedForSave);
            Assert.DoesNotContain(logLines, l => l.Contains("stranded sidecar"));
            Assert.Contains(logLines, l =>
                l.Contains("serializing Limbo pending tree") && l.Contains("isActive resume node"));

            // Round-trip: loading the serialized node re-stashes it as a Limbo pending tree
            // (state re-derived from the non-null ActiveRecordingId), exactly as a fresh
            // quickload would — so the resume continues instead of the tree being lost.
            RecordingStore.ResetForTesting();
            bool restored = ParsekScenario.TryRestoreActiveTreeNode(node);
            Assert.True(restored);
            Assert.True(RecordingStore.HasPendingTree);
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
            Assert.Equal("tree_limbo", RecordingStore.PendingTree.Id);
        }

        [Fact]
        public void SaveTreeRecordings_WritesLimboVesselSwitchPending_AndRoundTrips()
        {
            // The vessel-switch flavor (ActiveRecordingId == null) round-trips to
            // LimboVesselSwitch — the stash state is re-derived from the null active rec id.
            var vesselSwitch = MakeTree("tree_limbo_switch", "Switch Mission", "rec_limbo_switch");
            vesselSwitch.ActiveRecordingId = null; // outsider state at stash time (#266)
            RecordingStore.StashPendingTree(vesselSwitch, PendingTreeState.LimboVesselSwitch);

            var node = new ConfigNode("PARSEK_SCENARIO");
            ParsekScenario.SaveTreeRecordings(node);

            ConfigNode[] treeNodes = node.GetNodes("RECORDING_TREE");
            Assert.Single(treeNodes);
            Assert.True(ParsekScenario.IsActiveTreeNode(treeNodes[0]));
            Assert.True(RecordingStore.PendingTreeSerializedForSave);

            RecordingStore.ResetForTesting();
            bool restored = ParsekScenario.TryRestoreActiveTreeNode(node);
            Assert.True(restored);
            Assert.True(RecordingStore.HasPendingTree);
            Assert.Equal(PendingTreeState.LimboVesselSwitch, RecordingStore.PendingTreeStateValue);
            Assert.Equal("tree_limbo_switch", RecordingStore.PendingTree.Id);
        }

        [Fact]
        public void CollectParkedTreeIdsForMissionPrune_ReturnsActiveAndPendingNodeIds_NotCommitted()
        {
            // The parked-id collector feeds MissionStore.PruneOrphans so a mission whose tree
            // is serialized as an isActive / isPending node (restored later this OnLoad) is not
            // pruned as an orphan. Committed nodes are excluded (already in the live list).
            var node = new ConfigNode("PARSEK_SCENARIO");
            AddTreeNode(node, MakeTree("t_committed", "Committed", "r_c"), isPending: false, isActive: false);
            AddTreeNode(node, MakeTree("t_active", "Active", "r_a"), isPending: false, isActive: true);
            AddTreeNode(node, MakeTree("t_pending", "Pending", "r_p"), isPending: true, isActive: false);

            var ids = ParsekScenario.CollectParkedTreeIdsForMissionPrune(node);

            Assert.Contains("t_active", ids);
            Assert.Contains("t_pending", ids);
            Assert.DoesNotContain("t_committed", ids);
        }

        [Fact]
        public void HasActiveTreeNode_DetectsActiveMarker_GuardForLimboCoexistence()
        {
            // The coexistence guard: SavePendingTreeIfAny uses HasActiveTreeNode to avoid
            // writing a SECOND isActive node when serializing a Limbo resume tree (only the
            // first round-trips through TryRestoreActiveTreeNode; a second would be lost). The
            // full fallback fires only with a live flight active tree (not constructible under
            // xUnit), so we cover the predicate directly here.
            var node = new ConfigNode("PARSEK_SCENARIO");
            Assert.False(ParsekScenario.HasActiveTreeNode(node));

            AddTreeNode(node, MakeTree("t_committed", "Committed", "r_c"), isPending: false, isActive: false);
            AddTreeNode(node, MakeTree("t_pending", "Pending", "r_p"), isPending: true, isActive: false);
            Assert.False(ParsekScenario.HasActiveTreeNode(node)); // committed + pending only

            AddTreeNode(node, MakeTree("t_active", "Active", "r_a"), isPending: false, isActive: true);
            Assert.True(ParsekScenario.HasActiveTreeNode(node));  // now an active node is present
        }

        [Fact]
        public void LoadRecordingTrees_SkipsPendingTreeNode_NotCommitted()
        {
            var committed = MakeTree("tree_committed", "Committed Mission", "rec_committed");
            var pending = MakeTree("tree_pending_load", "Pending Mission", "rec_pending_load");
            var node = new ConfigNode("PARSEK_SCENARIO");
            AddTreeNode(node, committed, isPending: false, isActive: false);
            AddTreeNode(node, pending, isPending: true, isActive: false);

            ParsekScenario.LoadRecordingTrees(node, RecordingStore.CommittedRecordings);

            Assert.Single(RecordingStore.CommittedTrees);
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("rec_committed", RecordingStore.CommittedRecordings[0].RecordingId);
            Assert.DoesNotContain(RecordingStore.CommittedRecordings, r => r.RecordingId == "rec_pending_load");
            Assert.False(RecordingStore.HasPendingTree);
        }

        [Fact]
        public void TryRestorePendingTreeNode_RestoresFinalizedPendingAndClearsStashedThisTransition()
        {
            var pending = MakeTree("tree_pending_restore", "Restored Pending", "rec_pending_restore");
            var node = new ConfigNode("PARSEK_SCENARIO");
            AddTreeNode(node, pending, isPending: true, isActive: false);
            RecordingStore.PendingStashedThisTransition = true;

            bool restored = ParsekScenario.TryRestorePendingTreeNode(node);

            Assert.True(restored);
            Assert.True(RecordingStore.HasPendingTree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            Assert.Equal("tree_pending_restore", RecordingStore.PendingTree.Id);
            Assert.False(RecordingStore.PendingStashedThisTransition);
            Assert.True(RecordingStore.PendingTreeSerializedForSave);
            Assert.Contains(logLines, l =>
                l.Contains("TryRestorePendingTreeNode: restored pending tree")
                && l.Contains("Restored Pending"));
        }

        [Fact]
        public void TryRestorePendingTreeNode_ActiveAndPendingMarkers_PreservesPendingAlongsideActiveRestore()
        {
            string recordingsDir = CreateRecordingsDir("active-pending-conflict");
            string pendingPrec = Path.Combine(recordingsDir, "rec_pending_conflict.prec");
            string orphanPrec = Path.Combine(recordingsDir, "rec_orphan_conflict.prec");
            File.WriteAllText(pendingPrec, "keep");
            File.WriteAllText(orphanPrec, "orphan");
            var pending = MakeTree("tree_pending_conflict", "Pending Conflict", "rec_pending_conflict");
            var active = MakeTree("tree_active_conflict", "Active Conflict", "rec_active_conflict");
            var node = new ConfigNode("PARSEK_SCENARIO");
            AddTreeNode(node, pending, isPending: true, isActive: false);
            AddTreeNode(node, active, isPending: false, isActive: true);

            bool activeRestored = ParsekScenario.TryRestoreActiveTreeNode(node);
            bool restored = ParsekScenario.TryRestorePendingTreeNode(
                node, activeTreeRestoredFromSave: true);

            Assert.True(activeRestored);
            Assert.True(restored);
            Assert.True(RecordingStore.HasPendingTree);
            Assert.Equal("tree_active_conflict", RecordingStore.PendingTree.Id);
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
            Assert.True(RecordingStore.HasSavedPendingTreeDuringActiveRestore);
            Assert.Equal("tree_pending_conflict",
                RecordingStore.SavedPendingTreeDuringActiveRestore.Id);
            Assert.True(RecordingStore.IsPendingRecordingId("rec_pending_conflict"));

            HashSet<string> validIds = ParsekScenario.BuildValidRecordingIdsForKspLoad();
            Assert.Contains("rec_active_conflict", validIds);
            Assert.Contains("rec_pending_conflict", validIds);
            RecordingStore.CleanOrphanFiles();
            Assert.True(File.Exists(pendingPrec));
            Assert.False(File.Exists(orphanPrec));

            var saveNode = new ConfigNode("PARSEK_SCENARIO");
            ParsekScenario.SaveTreeRecordings(saveNode);
            ConfigNode[] savedTreeNodes = saveNode.GetNodes("RECORDING_TREE");
            // Data-loss fix: BOTH trees now survive the save. The Limbo active-restore tree is
            // serialized as an isActive resume node (previously it was dropped, losing the
            // mission), and the preserved pending tree is serialized as an isPending node.
            Assert.Equal(2, savedTreeNodes.Length);
            ConfigNode activeNode = Array.Find(savedTreeNodes, ParsekScenario.IsActiveTreeNode);
            ConfigNode pendingNode = Array.Find(savedTreeNodes, ParsekScenario.IsPendingTreeNode);
            Assert.NotNull(activeNode);
            Assert.NotNull(pendingNode);
            Assert.Equal("tree_active_conflict", activeNode.GetValue("id"));
            Assert.Equal("tree_pending_conflict", pendingNode.GetValue("id"));

            var poppedActive = RecordingStore.PopPendingTree();
            Assert.Equal("tree_active_conflict", poppedActive.Id);
            Assert.True(RecordingStore.PromoteSavedPendingTreeAfterActiveRestore("test"));
            Assert.True(RecordingStore.HasPendingTree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            Assert.Equal("tree_pending_conflict", RecordingStore.PendingTree.Id);
            Assert.True(RecordingStore.PendingTreeSerializedForSave);

            Assert.Contains(logLines, l =>
                l.Contains("[WARN][Scenario]")
                && l.Contains("alongside 1 active marker")
                && l.Contains("preserving saved pending tree separately"));
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("wrote PENDING tree")
                && l.Contains("preserved during active-tree restore"));
        }

        [Fact]
        public void TryRestorePendingTreeNode_ActiveMarkerWithoutActiveRestore_RestoresPendingMainSlot()
        {
            var pending = MakeTree("tree_pending_fallback", "Pending Fallback", "rec_pending_fallback");
            var active = MakeTree("tree_active_fallback", "Active Fallback", "rec_active_fallback");
            var node = new ConfigNode("PARSEK_SCENARIO");
            AddTreeNode(node, pending, isPending: true, isActive: false);
            AddTreeNode(node, active, isPending: false, isActive: true);

            bool restored = ParsekScenario.TryRestorePendingTreeNode(
                node, activeTreeRestoredFromSave: false);

            Assert.True(restored);
            Assert.True(RecordingStore.HasPendingTree);
            Assert.False(RecordingStore.HasSavedPendingTreeDuringActiveRestore);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            Assert.Equal("tree_pending_fallback", RecordingStore.PendingTree.Id);
            Assert.True(RecordingStore.PendingTreeSerializedForSave);
            Assert.True(RecordingStore.IsPendingRecordingId("rec_pending_fallback"));
            Assert.Contains(logLines, l =>
                l.Contains("[WARN][Scenario]")
                && l.Contains("alongside 1 active marker")
                && l.Contains("active restore did not run, restoring pending tree normally"));
        }

        [Fact]
        public void QuickloadDiscard_DoesNotDiscardPendingRestoredFromSave()
        {
            var pending = MakeTree("tree_pending_quickload", "Quickload Pending", "rec_pending_quickload");
            RecordingStore.RestorePendingTreeFromSave(pending);
            Assert.False(RecordingStore.PendingStashedThisTransition);
            logLines.Clear();

            ParsekScenario.DiscardStashedOnQuickload(preChangeUT: 400.0, currentUT: 370.0);

            Assert.True(RecordingStore.HasPendingTree);
            Assert.Equal("tree_pending_quickload", RecordingStore.PendingTree.Id);
            Assert.Contains(logLines, l =>
                l.Contains("Quickload discard complete") && l.Contains("tree=0"));
            Assert.DoesNotContain(logLines, l => l.Contains("discarded pending tree"));
        }

        [Fact]
        public void CleanOrphanFiles_KeepsSidecarsForRestoredPendingTree()
        {
            string recordingsDir = CreateRecordingsDir("pending-clean-orphan");
            string keepPrec = Path.Combine(recordingsDir, "rec_pending_keep.prec");
            string orphanPrec = Path.Combine(recordingsDir, "rec_pending_orphan.prec");
            File.WriteAllText(keepPrec, "keep");
            File.WriteAllText(orphanPrec, "orphan");

            var pending = MakeTree("tree_pending_keep", "Pending Keep", "rec_pending_keep");
            RecordingStore.RestorePendingTreeFromSave(pending);

            RecordingStore.CleanOrphanFiles();

            Assert.True(File.Exists(keepPrec));
            Assert.False(File.Exists(orphanPrec));
        }

        [Fact]
        public void OnKspLoadValidIds_IncludesPendingTreeIds()
        {
            var pending = MakeTree("tree_pending_valid_ids", "Pending Valid", "rec_pending_valid");
            RecordingStore.RestorePendingTreeFromSave(pending);

            HashSet<string> validIds = ParsekScenario.BuildValidRecordingIdsForKspLoad();

            Assert.Contains("rec_pending_valid", validIds);
        }

        [Fact]
        public void DiscardPendingTree_SkipsCommittedRecordingIdsWhenDeletingPendingFiles()
        {
            var committed = new Recording
            {
                RecordingId = "rec_shared_discard",
                VesselName = "Committed Shared",
            };
            RecordingStore.AddRecordingWithTreeForTesting(committed, "Committed Shared Tree");
            var pending = MakeTree(
                "tree_pending_shared_discard", "Pending Shared", "rec_shared_discard");
            RecordingStore.StashPendingTree(pending, PendingTreeState.Finalized);
            logLines.Clear();

            RecordingStore.DiscardPendingTree();

            Assert.False(RecordingStore.HasPendingTree);
            Assert.Contains(RecordingStore.CommittedRecordings, r =>
                r.RecordingId == "rec_shared_discard");
            Assert.Contains(logLines, l =>
                l.Contains("[WARN][RecordingStore]")
                && l.Contains("skipped deleting 1 recording sidecar set")
                && l.Contains("committed history"));
        }

        private string CreateRecordingsDir(string label)
        {
            string saveFolder = "parsek-test-pending-tree-" + label + "-" + Guid.NewGuid().ToString("N");
            HighLogic.SaveFolder = saveFolder;
            string root = Path.Combine(Path.GetTempPath(), saveFolder);
            string recordingsDir = Path.Combine(root, "Parsek", "Recordings");
            Directory.CreateDirectory(recordingsDir);
            cleanupRoots.Add(root);
            RecordingStore.CleanOrphanFilesDirectoryOverrideForTesting = recordingsDir;
            return recordingsDir;
        }

        private static ConfigNode AddTreeNode(
            ConfigNode scenarioNode,
            RecordingTree tree,
            bool isPending,
            bool isActive)
        {
            ConfigNode treeNode = scenarioNode.AddNode("RECORDING_TREE");
            tree.Save(treeNode);
            if (isPending)
                treeNode.AddValue("isPending", "True");
            if (isActive)
                treeNode.AddValue("isActive", "True");
            return treeNode;
        }

        private static RecordingTree MakeTree(string treeId, string treeName, string recordingId)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeName,
                RootRecordingId = recordingId,
                ActiveRecordingId = recordingId,
            };
            var rec = new Recording
            {
                RecordingId = recordingId,
                VesselName = treeName + " Vessel",
                TreeId = treeId,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 110.0,
                VesselPersistentId = 12345,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 110.0 });
            tree.AddOrReplaceRecording(rec);
            return tree;
        }
    }
}
