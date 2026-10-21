using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A committed recording resumed IN PLACE (the restore attempt's clone keeps the committed
    /// recording's id), then saved, cold-loaded and discarded, over real hashed sidecars
    /// (todo ATTEMPT-EVENTS-DROPPED-WHEN-RESUMED-IN-PLACE, lane QL-13). The save writes the
    /// clone over the shared head and the committed bytes stay as their hashed version
    /// (design-load-restores-the-save.md 4.4), so: the committed copy is current through that
    /// version and never rewritten (its epoch does not move); one save names both versions; a
    /// cold load restores each from its own bytes and re-arms the attempt; a discard leaves
    /// the committed copy readable, and the next lone load promotes it back to head.
    /// Driven through <c>ParsekScenario.SaveTreeRecordings</c>, the active-tree plan plus
    /// <c>EnsureRecordingFilesCurrentForSave</c> (the active write pass) and
    /// <c>TryRestoreActiveTreeNode</c>.
    /// </summary>
    [Collection("Sequential")]
    public class InPlaceResumeSidecarVersionTests : IDisposable
    {
        private const string TreeId = "tree_inplace";
        private const string RootId = "root_inplace";
        private const string ArmReason = "TryTakeCommittedTreeForSpawnedVesselRestore copy-on-write";

        private readonly List<string> logLines = new List<string>();
        private readonly string saveRoot;

        public InPlaceResumeSidecarVersionTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ResetAll();
            RecordingStore.SuppressLogging = false;
            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = false;
            KspStatePatcher.SuppressUnityCallsForTesting = true;

            saveRoot = Path.Combine(Path.GetTempPath(), "parsek-inplace-resume-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(saveRoot);
            RecordingPaths.SaveRootOverrideForTesting = saveRoot;
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = null;
            try { Directory.Delete(saveRoot, true); } catch { }
            ResetAll();
            Parsek.Rendering.SmoothingPipeline.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        private static void ResetAll()
        {
            SidecarVersionResolution.ResetForTesting();
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            Ledger.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.ClearPendingQuickloadResumeContext();
            ParsekScenario.ClearRestoredQuicksaveTreeFactsForTesting();
            ParsekScenario.pendingActiveTreeResumeRewindSave = null;
            RewindContext.ResetForTesting();
            RevertDetector.ResetForTesting();
        }

        [Fact]
        public void ResumedInPlace_SaveColdLoadDiscard_EachCopyKeepsItsOwnBytes()
        {
            // ---- the committed recording, saved once (named hash H1, epoch 1) ----
            RecordingTree committed = MakeCommittedTree();
            Recording committedRec = committed.Recordings[RootId];
            RecordingStore.AddCommittedInternal(committedRec);
            RecordingStore.AddCommittedTreeForTesting(committed);
            ParsekScenario.SaveTreeRecordings(new ConfigNode("PARSEK_SCENARIO"));
            string h1 = committedRec.PrecHash;
            Assert.False(string.IsNullOrEmpty(h1));
            Assert.Equal(1, committedRec.SidecarEpoch);

            // ---- the resume: the attempt's clone keeps the id and flies on ----
            RecordingTree clone = RecordingTree.DeepClone(committed);
            Recording cloneRec = clone.Recordings[RootId];
            SetPoints(cloneRec, 100.0, 150.0, 200.0, 260.0, 320.0);
            cloneRec.VesselSnapshot = MakeVesselSnapshot("Resumed");
            cloneRec.FilesDirty = true;
            clone.ActiveRecordingId = RootId;
            RecordingStore.ArmCommittedTreeRestoreAttempt(committed, ArmReason);
            Assert.True(RecordingStore.IsCommittedTreeRestoreAttemptRecordingId(RootId));

            // ---- the F5: committed trees, then the active tree's write pass ----
            logLines.Clear();
            ConfigNode save = WriteSave(clone, expectedOverlap: 1);
            string h2 = cloneRec.PrecHash;
            Assert.False(string.IsNullOrEmpty(h2));
            Assert.NotEqual(h1, h2);
            Assert.Equal(h2, SidecarContentHash.ComputeFile(HeadPath(RootId)));
            Assert.True(File.Exists(VersionPath(RootId, h1)));
            Assert.Contains(logLines, l => l.Contains("writing restore-attempt recording '" + RootId + "'"));
            Assert.DoesNotContain(logLines, l => l.Contains("skipped dirty sidecar save for committed-restore overlap"));

            // The committed copy is current through its version: not rewritten, epoch unmoved,
            // still naming H1. A second save changes nothing either (no ping-pong).
            Assert.Equal(1, committedRec.SidecarEpoch);
            Assert.Equal(h1, committedRec.PrecHash);
            logLines.Clear();
            WriteSave(clone, expectedOverlap: 0);
            Assert.Contains(logLines, l => l.Contains("committed recording")
                && l.Contains("id=" + RootId) && l.Contains("is current through its named sidecar version"));
            Assert.DoesNotContain(logLines, l => l.Contains("rewriting sidecars") && l.Contains("treeKind=committed tree"));
            Assert.Equal(1, committedRec.SidecarEpoch);
            Assert.Equal(h2, SidecarContentHash.ComputeFile(HeadPath(RootId)));

            // In session, a dropped committed snapshot re-hydrates its own bytes, not the clone's.
            committedRec.VesselSnapshot = null;
            Assert.True(RecordingStore.TryHydrateVesselSnapshotFromSidecar(committedRec));
            Assert.Equal("Committed", committedRec.VesselSnapshot.GetValue("name"));

            // One save names both versions of the id.
            ConfigNode[] treeNodes = save.GetNodes("RECORDING_TREE");
            Assert.Equal(2, treeNodes.Length);
            var named = treeNodes.Select(n => n.GetNode("RECORDING").GetValue("precHash")).ToList();
            Assert.Contains(h1, named);
            Assert.Contains(h2, named);

            // ---- the F9: a cold load of that save ----
            ResetAll();
            logLines.Clear();
            SidecarVersionResolution.BeginColdLoad(save);
            ParsekScenario.LoadGameStateFromNodeOrFiles(save);
            foreach (ConfigNode treeNode in treeNodes.Where(n => !ParsekScenario.IsActiveTreeNode(n)))
            {
                RecordingTree loaded = RecordingTree.Load(treeNode);
                foreach (Recording rec in loaded.Recordings.Values)
                {
                    Assert.True(RecordingStore.LoadRecordingFiles(rec));
                    RecordingStore.AddCommittedInternal(rec);
                }
                RecordingStore.AddCommittedTreeForTesting(loaded);
            }
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(save, EarlyLoadKind.Cold, loadedSceneIsFlight: true));
            SidecarVersionResolution.End("test-cold-load");

            Recording loadedCommitted = RecordingStore.CommittedTrees.Single(t => t.Id == TreeId).Recordings[RootId];
            Assert.Equal(3, loadedCommitted.Points.Count);
            Assert.Equal(200.0, loadedCommitted.Points[2].ut);
            Assert.Equal(1, loadedCommitted.SidecarEpoch);
            RecordingTree restored = RecordingStore.PendingTree;
            Assert.Equal(TreeId, restored.Id);
            Assert.Equal(5, restored.Recordings[RootId].Points.Count);
            Assert.Equal(320.0, restored.Recordings[RootId].Points[4].ut);
            Assert.True(RecordingStore.IsCommittedTreeRestoreAttemptTree(TreeId));
            Assert.Contains(logLines, l => l.Contains("Sidecar version read without promote")
                && l.Contains("rec=" + RootId) && l.Contains("reason=multiple-owners"));
            // Both read their own bytes, so the head still holds the clone's.
            Assert.Equal(h2, SidecarContentHash.ComputeFile(HeadPath(RootId)));

            // ---- Discard: the clone goes, the committed copy stays readable ----
            RecordingStore.DiscardCommittedTreeRestoreAttempt("test-discard");
            RecordingStore.DiscardPendingTree();
            Assert.True(File.Exists(HeadPath(RootId)));
            Assert.True(File.Exists(VersionPath(RootId, h1)));

            logLines.Clear();
            var afterDiscard = new ConfigNode("PARSEK_SCENARIO");
            ParsekScenario.SaveTreeRecordings(afterDiscard);
            Assert.Equal(1, loadedCommitted.SidecarEpoch);
            Assert.Equal(h1, afterDiscard.GetNode("RECORDING_TREE").GetNode("RECORDING").GetValue("precHash"));

            // A later cold load of that save: one owner, so H1 is read and promoted to head.
            ConfigNode afterDiscardText = ThroughText(afterDiscard);
            ResetAll();
            SidecarVersionResolution.BeginColdLoad(afterDiscardText);
            RecordingTree again = RecordingTree.Load(afterDiscardText.GetNode("RECORDING_TREE"));
            Assert.True(RecordingStore.LoadRecordingFiles(again.Recordings[RootId]));
            SidecarVersionResolution.End("test-cold-load");
            Assert.Equal(3, again.Recordings[RootId].Points.Count);
            Assert.Equal(h1, SidecarContentHash.ComputeFile(HeadPath(RootId)));
        }

        // catches: the attempt member trusting the epoch alone. It starts at the committed copy's
        // epoch, so a committed rewrite (a dirty committed copy) can land on the clone's epoch;
        // the clone's save then read the committed bytes as its own and named a hash the head
        // no longer held.
        [Fact]
        public void CommittedCopyRewrittenAtTheClonesEpoch_CloneRewritesTheHead()
        {
            RecordingTree committed = MakeCommittedTree();
            Recording committedRec = committed.Recordings[RootId];
            RecordingStore.AddCommittedInternal(committedRec);
            RecordingStore.AddCommittedTreeForTesting(committed);
            ParsekScenario.SaveTreeRecordings(new ConfigNode("PARSEK_SCENARIO"));

            RecordingTree clone = RecordingTree.DeepClone(committed);
            Recording cloneRec = clone.Recordings[RootId];
            SetPoints(cloneRec, 100.0, 150.0, 200.0, 260.0, 320.0);
            cloneRec.FilesDirty = true;
            clone.ActiveRecordingId = RootId;
            RecordingStore.ArmCommittedTreeRestoreAttempt(committed, ArmReason);
            WriteSave(clone, expectedOverlap: 1);
            Assert.Equal(2, cloneRec.SidecarEpoch);

            committedRec.FilesDirty = true;
            logLines.Clear();
            WriteSave(clone, expectedOverlap: 0);
            Assert.Equal(2, committedRec.SidecarEpoch);
            Assert.Contains(logLines, l => l.Contains("rewriting sidecars") && l.Contains("id=" + RootId)
                && l.Contains("treeKind=active tree") && l.Contains("reason=head-holds-other-bytes"));
            Assert.Equal(cloneRec.PrecHash, SidecarContentHash.ComputeFile(HeadPath(RootId)));
            Assert.True(File.Exists(VersionPath(RootId, committedRec.PrecHash)));
        }

        // catches: leaving flight with a resumed-in-place attempt live. Stock's save before the
        // scene change now holds the attempt's active node, and that scene's in-session OnLoad
        // detached the committed original from memory, so the scene-exit merge of the Finalized
        // clone ran as a first commit (no replace, the canon guards lost) and a Discard deleted
        // the committed tree's sidecars.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void InSessionLoadOutsideFlight_LiveAttempt_KeepsTheCommittedOriginal(bool finalizedPending)
        {
            RecordingTree committed = MakeCommittedTree();
            Recording committedRec = committed.Recordings[RootId];
            RecordingStore.AddCommittedInternal(committedRec);
            RecordingStore.AddCommittedTreeForTesting(committed);
            ParsekScenario.SaveTreeRecordings(new ConfigNode("PARSEK_SCENARIO"));

            RecordingTree clone = RecordingTree.DeepClone(committed);
            Recording cloneRec = clone.Recordings[RootId];
            SetPoints(cloneRec, 100.0, 150.0, 200.0, 260.0, 320.0);
            cloneRec.FilesDirty = true;
            clone.ActiveRecordingId = RootId;
            RecordingStore.ArmCommittedTreeRestoreAttempt(committed, ArmReason);
            ConfigNode save = WriteSave(clone, expectedOverlap: 1);

            // The scene-exit finalization stashed the clone as the Finalized pending tree.
            if (finalizedPending)
                RecordingStore.StashPendingTree(RecordingTree.DeepClone(clone), PendingTreeState.Finalized);

            logLines.Clear();
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(save, EarlyLoadKind.InSession, loadedSceneIsFlight: false));

            Assert.Contains(RecordingStore.CommittedTrees, t => t.Id == TreeId);
            Assert.Contains(RecordingStore.CommittedRecordings, r => r.RecordingId == RootId);
            Assert.True(RecordingStore.IsCommittedTreeRestoreAttemptTree(TreeId));
            if (finalizedPending)
                Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            // The save's clone keeps the path flown since the resume (no splice refresh from the
            // committed copy).
            Assert.Equal(5, RecordingStore.PendingTree.Recordings[RootId].Points.Count);
            Assert.DoesNotContain(logLines, l => l.Contains("removed committed tree"));
            Assert.Contains(logLines, l => l.Contains("kept the committed copy of tree")
                && l.Contains("id=" + TreeId) && l.Contains("live restore attempt"));
        }

        // ---- helpers ----

        // OnSave's order: committed trees, then the active tree's classify and write passes
        // (the overlap is counted only while the clone is dirty),
        // then its node (isActive). Through text, as a .sfs.
        private ConfigNode WriteSave(RecordingTree activeTree, int expectedOverlap)
        {
            var node = new ConfigNode("PARSEK_SCENARIO");
            ParsekScenario.SaveTreeRecordings(node);

            ParsekScenario.ActiveTreeSidecarSavePlan plan = ParsekScenario.PlanActiveTreeSidecarSaves(activeTree);
            Assert.True(plan.AllRecordingsWritable);
            Assert.Equal(expectedOverlap, plan.CommittedRestoreOverlapCount);
            Assert.Single(plan.WriteCandidates);
            foreach (Recording candidate in plan.WriteCandidates)
                Assert.True(ParsekScenario.EnsureRecordingFilesCurrentForSave(candidate, ParsekScenario.ActiveTreeKind));

            ConfigNode activeNode = node.AddNode("RECORDING_TREE");
            activeTree.Save(activeNode);
            activeNode.AddValue("isActive", "True");

            var eventsBuild = GameStateStore.BuildEventsNode(
                RecordingStore.CommittedTreeRestoreAttemptTreeId,
                RecordingStore.CommittedTreeRestoreAttemptReason);
            node.AddNode(ParsekScenario.BuildGameStateNode(
                Ledger.BuildSaveNode(), eventsBuild.Root, MilestoneStore.BuildMilestonesNode(),
                new ConfigNode(ParsekScenario.BaselinesNodeName),
                GameStateRecorder.BuildPendingScienceNode(GameStateRecorder.PendingScienceSubjects),
                eventsBuild.AttemptEvents));
            return ThroughText(node);
        }

        private ConfigNode ThroughText(ConfigNode node)
        {
            string path = Path.Combine(saveRoot, "persistent-parsek-" + Guid.NewGuid().ToString("N") + ".cfg");
            node.Save(path);
            ConfigNode loaded = ConfigNode.Load(path);
            Assert.NotNull(loaded);
            return loaded;
        }

        private static RecordingTree MakeCommittedTree()
        {
            var tree = new RecordingTree
            {
                Id = TreeId, TreeName = "InPlace", RootRecordingId = RootId, ActiveRecordingId = null,
            };
            var root = new Recording
            {
                RecordingId = RootId, TreeId = TreeId, VesselName = "InPlace", VesselPersistentId = 111u,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                SidecarEpoch = 0,
                FilesDirty = true,
            };
            SetPoints(root, 100.0, 150.0, 200.0);
            root.VesselSnapshot = MakeVesselSnapshot("Committed");
            tree.AddOrReplaceRecording(root);
            tree.RebuildBackgroundMap();
            return tree;
        }

        private static ConfigNode MakeVesselSnapshot(string name)
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("name", name);
            node.AddValue("pid", "0f0e0d0c0b0a09080706050403020100");
            return node;
        }

        private static void SetPoints(Recording rec, params double[] uts)
        {
            rec.Points.Clear();
            rec.TrackSections.Clear();
            foreach (double ut in uts)
            {
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = ut, altitude = 1000.0, bodyName = "Kerbin",
                    rotation = Quaternion.identity, velocity = Vector3.zero,
                });
            }
            rec.ExplicitStartUT = uts[0];
            rec.ExplicitEndUT = uts[uts.Length - 1];
        }

        private static string HeadPath(string id) =>
            RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildTrajectoryRelativePath(id));

        private static string VersionPath(string id, string hash) =>
            SidecarVersions.VersionPathForHead(HeadPath(id), id, hash, SidecarKind.Prec);
    }
}
