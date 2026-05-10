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
        public void SaveTreeRecordings_DoesNotWriteLimboPendingAsPending()
        {
            string recordingsDir = CreateRecordingsDir("limbo-skip-with-sidecar");
            File.WriteAllText(Path.Combine(recordingsDir, "rec_limbo.prec"), "p");
            var limbo = MakeTree("tree_limbo", "Limbo Mission", "rec_limbo");
            RecordingStore.StashPendingTree(limbo, PendingTreeState.Limbo);

            var node = new ConfigNode("PARSEK_SCENARIO");
            ParsekScenario.SaveTreeRecordings(node);

            Assert.Empty(node.GetNodes("RECORDING_TREE"));
            Assert.False(RecordingStore.PendingTreeSerializedForSave);
            Assert.Contains(logLines, l =>
                l.Contains("SavePendingTreeIfAny: skipped pending tree")
                && l.Contains("state=Limbo"));
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("writing 0 RECORDING_TREE nodes")
                && l.Contains("1 stranded sidecar"));

            RecordingStore.ResetForTesting();
            logLines.Clear();
            recordingsDir = CreateRecordingsDir("limbo-switch-skip-with-sidecar");
            File.WriteAllText(Path.Combine(recordingsDir, "rec_limbo_switch.prec"), "p");

            var vesselSwitch = MakeTree("tree_limbo_switch", "Switch Mission", "rec_limbo_switch");
            RecordingStore.StashPendingTree(vesselSwitch, PendingTreeState.LimboVesselSwitch);
            node = new ConfigNode("PARSEK_SCENARIO");
            ParsekScenario.SaveTreeRecordings(node);

            Assert.Empty(node.GetNodes("RECORDING_TREE"));
            Assert.False(RecordingStore.PendingTreeSerializedForSave);
            Assert.Contains(logLines, l =>
                l.Contains("SavePendingTreeIfAny: skipped pending tree")
                && l.Contains("state=LimboVesselSwitch"));
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("writing 0 RECORDING_TREE nodes")
                && l.Contains("1 stranded sidecar"));
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

            HashSet<string> validIds = ParsekScenario.BuildValidRecordingIdsForKspLoad();
            Assert.Contains("rec_active_conflict", validIds);
            Assert.Contains("rec_pending_conflict", validIds);
            RecordingStore.CleanOrphanFiles();
            Assert.True(File.Exists(pendingPrec));
            Assert.False(File.Exists(orphanPrec));

            var saveNode = new ConfigNode("PARSEK_SCENARIO");
            ParsekScenario.SaveTreeRecordings(saveNode);
            ConfigNode[] savedTreeNodes = saveNode.GetNodes("RECORDING_TREE");
            Assert.Single(savedTreeNodes);
            Assert.True(ParsekScenario.IsPendingTreeNode(savedTreeNodes[0]));
            Assert.False(ParsekScenario.IsActiveTreeNode(savedTreeNodes[0]));
            Assert.Equal("tree_pending_conflict", savedTreeNodes[0].GetValue("id"));

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
