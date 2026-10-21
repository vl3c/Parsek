using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A cold load of a save written while a committed-tree restore attempt was live
    /// (design-load-restores-the-save.md 4.3, open question 3, lane QL-13): the save names the
    /// attempt in <c>GAME_STATE/ATTEMPT_EVENTS</c> (tree id and arm reason, also with no event
    /// held back), and the cold active-tree restore re-arms it from the save's committed copy,
    /// so the held-back events come back into memory and reach the ledger only through the
    /// attempt's merge. Driven through <c>ParsekScenario.LoadGameStateFromNodeOrFiles</c>,
    /// <c>TryRestoreActiveTreeNode</c> and <c>ApplySaveAttemptEventsAfterActiveTreeRestore</c>
    /// in OnLoad's order, over real sidecars.
    /// </summary>
    [Collection("Sequential")]
    public class ColdLoadRestoreAttemptRearmTests : IDisposable
    {
        private const string TreeId = "tree_att";
        private const string RootId = "root_att";
        private const string SegmentId = "seg_att";
        private const string BranchPointId = "bp_att";
        private const double CommittedEndUT = 200.0;
        private const string OriginalArmReason = "TryTakeCommittedTreeForSpawnedVesselRestore copy-on-write";

        private readonly List<string> logLines = new List<string>();
        private readonly string saveRoot;

        public ColdLoadRestoreAttemptRearmTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ResetAll();
            RecordingStore.SuppressLogging = false;
            KspStatePatcher.SuppressUnityCallsForTesting = true;

            saveRoot = Path.Combine(Path.GetTempPath(), "parsek-attempt-rearm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(saveRoot);
            RecordingPaths.SaveRootOverrideForTesting = saveRoot;
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            try { Directory.Delete(saveRoot, true); } catch { }
            ResetAll();
            KspStatePatcher.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        private static void ResetAll()
        {
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

        // ---- pure decisions ----

        [Theory]
        [InlineData((int)EarlyLoadKind.Cold, null, "t1", true, (int)ParsekScenario.SaveAttemptRearmDecision.NoAttemptInSave)]
        [InlineData((int)EarlyLoadKind.Cold, "", "t1", true, (int)ParsekScenario.SaveAttemptRearmDecision.NoAttemptInSave)]
        [InlineData((int)EarlyLoadKind.InSession, "t1", "t1", true, (int)ParsekScenario.SaveAttemptRearmDecision.NotColdLoad)]
        [InlineData((int)EarlyLoadKind.PlainRewind, "t1", "t1", true, (int)ParsekScenario.SaveAttemptRearmDecision.NotColdLoad)]
        [InlineData((int)EarlyLoadKind.ReFlyStart, "t1", "t1", true, (int)ParsekScenario.SaveAttemptRearmDecision.NotColdLoad)]
        [InlineData((int)EarlyLoadKind.Cold, "t1", "t2", true, (int)ParsekScenario.SaveAttemptRearmDecision.OtherTree)]
        [InlineData((int)EarlyLoadKind.Cold, "t1", null, true, (int)ParsekScenario.SaveAttemptRearmDecision.OtherTree)]
        [InlineData((int)EarlyLoadKind.Cold, "t1", "t1", false, (int)ParsekScenario.SaveAttemptRearmDecision.NoCommittedCopy)]
        [InlineData((int)EarlyLoadKind.Cold, "t1", "t1", true, (int)ParsekScenario.SaveAttemptRearmDecision.Rearm)]
        public void DecideSaveAttemptRearm_Table(
            int kind, string saved, string restored, bool committedCopy, int expected)
        {
            Assert.Equal((ParsekScenario.SaveAttemptRearmDecision)expected,
                ParsekScenario.DecideSaveAttemptRearm((EarlyLoadKind)kind, saved, restored, committedCopy));
        }

        // catches: a re-arm reason that hides the original arm, or one that grows by a wrapper on
        // every F5 / F9 cycle.
        [Fact]
        public void BuildSaveAttemptRearmReason_NamesTheColdLoadAndKeepsTheOriginalOnce()
        {
            string once = ParsekScenario.BuildSaveAttemptRearmReason(OriginalArmReason);
            Assert.Equal(ParsekScenario.SaveAttemptRearmReasonPrefix + OriginalArmReason, once);
            Assert.Equal(once, ParsekScenario.BuildSaveAttemptRearmReason(once));
            Assert.Equal(ParsekScenario.SaveAttemptRearmReasonPrefix + "<unrecorded>",
                ParsekScenario.BuildSaveAttemptRearmReason(null));
            Assert.Equal(ParsekScenario.SaveAttemptRearmReasonPrefix + "<unrecorded>",
                ParsekScenario.BuildSaveAttemptRearmReason(ParsekScenario.SaveAttemptRearmReasonPrefix));
        }

        // catches: ATTEMPT_EVENTS written only when an event was held back, so an F5 inside an
        // attempt that earned nothing yet left a save that could not re-arm it.
        [Fact]
        public void AttemptNode_WrittenWithNoHeldEvent_CarriesTreeIdAndArmReason()
        {
            RecordingStore.ArmCommittedTreeRestoreAttempt(MakeCommittedTree(), OriginalArmReason);
            var build = GameStateStore.BuildEventsNode(
                RecordingStore.CommittedTreeRestoreAttemptTreeId,
                RecordingStore.CommittedTreeRestoreAttemptReason);

            Assert.NotNull(build.AttemptEvents);
            Assert.Equal(TreeId, build.AttemptEvents.GetValue(GameStateStore.AttemptTreeIdKey));
            Assert.Equal(OriginalArmReason, build.AttemptEvents.GetValue(GameStateStore.AttemptArmReasonKey));
            Assert.Empty(build.AttemptEvents.GetNodes("GAME_STATE_EVENT"));
        }

        // ---- the cold restore ----

        // catches: a cold load of a save made inside an attempt that leaves the attempt unarmed
        // (its held-back earning dropped, never reaching the ledger), arms it from the clone
        // instead of the committed copy (cutoffs at the clone's end), detaches the committed
        // original (a later Discard would lose committed history), or lets the splice revert
        // the attempt's segment link.
        [Fact]
        public void ColdLoad_SaveInsideAttempt_RearmsFromCommittedCopy_EventsBackInMemoryOnly()
        {
            ConfigNode save = WriteSaveInsideAttempt(withHeldEvent: true);
            SimulateColdLoad(save);

            logLines.Clear();
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(save, EarlyLoadKind.Cold, loadedSceneIsFlight: true));

            Assert.True(RecordingStore.IsCommittedTreeRestoreAttemptTree(TreeId));
            Assert.Equal(ParsekScenario.SaveAttemptRearmReasonPrefix + OriginalArmReason,
                RecordingStore.CommittedTreeRestoreAttemptReason);
            // The cutoffs are the committed copy's: an event past the committed end but before
            // the clone's end is held back, one before the committed end is not.
            Assert.True(RecordingStore.ShouldSuppressCommittedTreeRestoreAttemptEventPersistence(
                new GameStateEvent { ut = CommittedEndUT + 10.0, recordingId = RootId }));
            Assert.False(RecordingStore.ShouldSuppressCommittedTreeRestoreAttemptEventPersistence(
                new GameStateEvent { ut = CommittedEndUT - 10.0, recordingId = RootId }));

            // The committed original stays committed, as in a live attempt.
            Assert.Contains(RecordingStore.CommittedTrees, t => t.Id == TreeId);
            Assert.Contains(RecordingStore.CommittedRecordings, r => r.RecordingId == RootId);
            // The restored tree is the clone, its segment link intact and nothing dirtied.
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
            RecordingTree restored = RecordingStore.PendingTree;
            Assert.Equal(TreeId, restored.Id);
            Assert.Equal(BranchPointId, restored.Recordings[RootId].ChildBranchPointId);
            Assert.False(restored.Recordings[RootId].FilesDirty);
            Assert.Contains(logLines, l => l.Contains("[INFO][Scenario]")
                && l.Contains("re-armed the save's committed-tree restore attempt for tree")
                && l.Contains("id=" + TreeId) && l.Contains("heldEvents=1"));
            Assert.Contains(logLines, l => l.Contains("kept the committed copy of tree")
                && l.Contains("id=" + TreeId));

            logLines.Clear();
            Assert.Equal(1, ParsekScenario.ApplySaveAttemptEventsAfterActiveTreeRestore());
            Assert.Contains(GameStateStore.Events, e => e.key == "earned-in-attempt");
            Assert.Single(GameStateStore.Events.Where(e => e.key == "earned-in-attempt"));
            Assert.Contains(logLines, l => l.Contains("restored the save's ATTEMPT_EVENTS into memory only")
                && l.Contains("attemptTreeId=" + TreeId));

            // Memory only: the live attempt keeps them off the next save's file and its
            // milestone flush, and writes them again into the next save's ATTEMPT_EVENTS.
            Assert.True(ParsekScenario.ShouldDeferPendingEventMilestoneFlushForSave());
            var next = GameStateStore.BuildEventsNode(
                RecordingStore.CommittedTreeRestoreAttemptTreeId,
                RecordingStore.CommittedTreeRestoreAttemptReason);
            Assert.DoesNotContain(next.Root.GetNodes("GAME_STATE_EVENT"), n => n.GetValue("key") == "earned-in-attempt");
            Assert.Contains(next.AttemptEvents.GetNodes("GAME_STATE_EVENT"), n => n.GetValue("key") == "earned-in-attempt");
            Assert.Equal(RecordingStore.CommittedTreeRestoreAttemptReason,
                next.AttemptEvents.GetValue(GameStateStore.AttemptArmReasonKey));
        }

        // catches: the re-arm keyed on a held event instead of on the attempt itself.
        [Fact]
        public void ColdLoad_SaveInsideAttemptWithNoHeldEvent_StillRearms()
        {
            ConfigNode save = WriteSaveInsideAttempt(withHeldEvent: false);
            SimulateColdLoad(save);

            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(save, EarlyLoadKind.Cold, loadedSceneIsFlight: true));

            Assert.True(RecordingStore.IsCommittedTreeRestoreAttemptTree(TreeId));
            Assert.Contains(RecordingStore.CommittedTrees, t => t.Id == TreeId);
            Assert.Equal(0, ParsekScenario.ApplySaveAttemptEventsAfterActiveTreeRestore());
            Assert.True(RecordingStore.IsCommittedTreeRestoreAttemptTree(TreeId));
        }

        // catches: an attempt armed with no committed copy to take its cutoffs from (every
        // same-id event would then read as pending-only), or the held events restored without
        // an attempt to keep them memory-only.
        [Fact]
        public void ColdLoad_SaveNamesAttemptButHasNoCommittedCopy_NotArmed_EventsDropped()
        {
            ConfigNode save = WriteSaveInsideAttempt(withHeldEvent: true);
            save.RemoveNode(save.GetNodes("RECORDING_TREE").Single(n => !ParsekScenario.IsActiveTreeNode(n)));
            Assert.True(ParsekScenario.IsActiveTreeNode(save.GetNodes("RECORDING_TREE").Single()));
            SimulateColdLoad(save);

            logLines.Clear();
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(save, EarlyLoadKind.Cold, loadedSceneIsFlight: true));

            Assert.False(RecordingStore.HasCommittedTreeRestoreAttempt);
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][Scenario]")
                && l.Contains("did not re-arm the save's committed-tree restore attempt: attemptTreeId=" + TreeId)
                && l.Contains("decision=NoCommittedCopy")
                && l.Contains("cannot be reconstructed"));

            Assert.Equal(0, ParsekScenario.ApplySaveAttemptEventsAfterActiveTreeRestore());
            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "earned-in-attempt");
            Assert.Contains(logLines, l => l.Contains("dropped the save's ATTEMPT_EVENTS")
                && l.Contains("reason=DropAttemptNotArmed"));
        }

        // catches: the re-arm leaking into a load that keeps memory's state, and the detach of
        // the committed copy changing for a cold load whose save names no attempt.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void NoRearm_DetachRunsAsBefore(bool inSessionWithHeldNode)
        {
            ConfigNode save = WriteSaveInsideAttempt(withHeldEvent: true);
            if (!inSessionWithHeldNode)
                save.GetNode(ParsekScenario.GameStateNodeName).RemoveNodes(GameStateStore.AttemptEventsNodeName);
            SimulateColdLoad(save);

            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(save,
                inSessionWithHeldNode ? EarlyLoadKind.InSession : EarlyLoadKind.Cold,
                loadedSceneIsFlight: true));

            Assert.False(RecordingStore.HasCommittedTreeRestoreAttempt);
            Assert.DoesNotContain(RecordingStore.CommittedTrees, t => t.Id == TreeId);
            ParsekScenario.ApplySaveAttemptEventsAfterActiveTreeRestore();
        }

        // ---- wiring ----

        // catches: the re-arm moved after the splice or the detach (the cutoffs would then come
        // from nothing), or the splice / detach no longer skipped for a re-armed attempt.
        [Fact]
        public void Wiring_RearmBeforeSpliceAndDetach_BothSkippedWhenRearmed()
        {
            string body = Collapse(PreparedMethodBody("ParsekScenario.cs", "internal static bool TryRestoreActiveTreeNode("));
            int drop = IndexOrFail(body, "DropFailedSidecarHydrationRecordings(");
            int rearm = IndexOrFail(body, "TryRearmSaveAttemptForRestoredTree(tree, earlyLoadKind)");
            int splice = IndexOrFail(body, "SpliceMissingCommittedRecordingsIntoLoadedTree(");
            int detach = IndexOrFail(body, "RecordingStore.RemoveCommittedTreeById(");
            Assert.True(drop < rearm && rearm < splice && splice < detach);
            Assert.Contains("int splicedFromCommitted = saveAttemptRearmed ? 0 : SpliceMissingCommittedRecordingsIntoLoadedTree(", body);
            Assert.Contains("bool committedCopyDetached = !saveAttemptRearmed && !reFlySessionResumed && RecordingStore.RemoveCommittedTreeById(", body);

            string persist = Collapse(PreparedMethodBody("ParsekScenario.cs", "private static void PersistGameStateAndMilestones(ConfigNode node)"));
            Assert.Contains("GameStateStore.BuildEventsNode( RecordingStore.CommittedTreeRestoreAttemptTreeId, "
                + "RecordingStore.CommittedTreeRestoreAttemptReason)", persist);
        }

        // ---- helpers ----

        // The committed tree as the original arm saw it: the spawned vessel's recording, ended at 200.
        private static RecordingTree MakeCommittedTree()
        {
            var tree = new RecordingTree
            {
                Id = TreeId, TreeName = "Attempt", RootRecordingId = RootId, ActiveRecordingId = null,
            };
            var root = new Recording { RecordingId = RootId, TreeId = TreeId, VesselName = "Attempt", VesselPersistentId = 111u };
            SetPoints(root, 100.0, 150.0, CommittedEndUT);
            tree.AddOrReplaceRecording(root);
            Stamp(root, 1);
            tree.RebuildBackgroundMap();
            return tree;
        }

        // The attempt's clone at the F5: the resumed spawn continues in a new segment under the
        // committed recording, which only gained the segment's parent link.
        private static RecordingTree MakeAttemptClone(RecordingTree committed)
        {
            RecordingTree clone = RecordingTree.DeepClone(committed);
            var segment = new Recording { RecordingId = SegmentId, TreeId = TreeId, VesselName = "Attempt", VesselPersistentId = 333u };
            SetPoints(segment, CommittedEndUT, 230.0, 260.0);
            Stamp(segment, 1);
            clone.AddOrReplaceRecording(segment);
            clone.Recordings[RootId].ChildBranchPointId = BranchPointId;
            segment.ParentBranchPointId = BranchPointId;
            clone.BranchPoints.Add(new BranchPoint
            {
                Id = BranchPointId,
                UT = CommittedEndUT,
                Type = BranchPointType.VesselSwitchContinuation,
                ParentRecordingIds = new List<string> { RootId },
                ChildRecordingIds = new List<string> { SegmentId },
            });
            clone.ActiveRecordingId = SegmentId;
            clone.RebuildBackgroundMap();
            return clone;
        }

        // Live attempt (committed copy committed, clone active, attempt armed from the committed
        // copy), an event earned at 230 on the committed id, then an OnSave of the Parsek node:
        // committed trees first, then the active node, then GAME_STATE.
        private ConfigNode WriteSaveInsideAttempt(bool withHeldEvent)
        {
            RecordingTree committed = MakeCommittedTree();
            RecordingTree clone = MakeAttemptClone(committed);
            WriteSidecars(committed.Recordings[RootId]);
            WriteSidecars(clone.Recordings[SegmentId]);

            RecordingStore.ArmCommittedTreeRestoreAttempt(committed, OriginalArmReason);
            if (withHeldEvent)
            {
                var earned = new GameStateEvent
                {
                    ut = 230.0, eventType = GameStateEventType.ContractCompleted, key = "earned-in-attempt",
                    detail = "fundsReward=5000", recordingId = RootId,
                };
                GameStateStore.AddEvent(ref earned);
            }
            var career = new GameStateEvent
            {
                ut = 120.0, eventType = GameStateEventType.TechResearched, key = "career", detail = "",
            };
            GameStateStore.AddEvent(ref career);

            var node = new ConfigNode("PARSEK_SCENARIO");
            committed.Save(node.AddNode("RECORDING_TREE"));
            ConfigNode activeNode = node.AddNode("RECORDING_TREE");
            clone.Save(activeNode);
            activeNode.AddValue("isActive", "True");

            var eventsBuild = GameStateStore.BuildEventsNode(
                RecordingStore.CommittedTreeRestoreAttemptTreeId,
                RecordingStore.CommittedTreeRestoreAttemptReason);
            Assert.NotNull(eventsBuild.AttemptEvents);
            node.AddNode(ParsekScenario.BuildGameStateNode(
                Ledger.BuildSaveNode(), eventsBuild.Root, MilestoneStore.BuildMilestonesNode(),
                new ConfigNode(ParsekScenario.BaselinesNodeName),
                GameStateRecorder.BuildPendingScienceNode(GameStateRecorder.PendingScienceSubjects),
                eventsBuild.AttemptEvents));

            // Through text, as a .sfs.
            string path = Path.Combine(saveRoot, "persistent-parsek.cfg");
            node.Save(path);
            ConfigNode loaded = ConfigNode.Load(path);
            Assert.NotNull(loaded);
            return loaded;
        }

        // The cold branch up to the active-tree restore: memory forgotten, the save's game state
        // read (the held ATTEMPT_EVENTS among it), the save's committed trees loaded.
        private static void SimulateColdLoad(ConfigNode save)
        {
            ResetAll();
            ParsekScenario.LoadGameStateFromNodeOrFiles(save);
            foreach (ConfigNode treeNode in save.GetNodes("RECORDING_TREE"))
            {
                if (ParsekScenario.IsActiveTreeNode(treeNode))
                    continue;
                RecordingTree committed = RecordingTree.Load(treeNode);
                foreach (Recording rec in committed.Recordings.Values)
                {
                    Assert.True(RecordingStore.LoadRecordingFiles(rec));
                    RecordingStore.AddCommittedInternal(rec);
                }
                RecordingStore.AddCommittedTreeForTesting(committed);
            }
        }

        private static void Stamp(Recording rec, int epoch)
        {
            rec.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            rec.RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration;
            rec.SidecarEpoch = epoch;
        }

        private static void WriteSidecars(Recording rec)
        {
            var onDisk = Recording.DeepClone(rec);
            onDisk.SidecarEpoch = rec.SidecarEpoch - 1;
            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                onDisk,
                RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildTrajectoryRelativePath(onDisk.RecordingId)),
                RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildVesselSnapshotRelativePath(onDisk.RecordingId)),
                RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildGhostSnapshotRelativePath(onDisk.RecordingId)),
                incrementEpoch: true));
            Assert.Equal(rec.SidecarEpoch, onDisk.SidecarEpoch);
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

        private static string PreparedMethodBody(string relPath, string signatureFragment)
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(repo, "Source", "Parsek", relPath);
            Assert.True(File.Exists(path), "Source file not found at " + path);
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
            int sigIdx = prepared.IndexOf(signatureFragment, StringComparison.Ordinal);
            Assert.True(sigIdx >= 0, "signature not found in " + relPath + ": " + signatureFragment);
            int open = prepared.IndexOf('{', sigIdx);
            return SourceScanText.BraceMatchedBlock(prepared, open);
        }

        private static int IndexOrFail(string body, string needle)
        {
            int idx = body.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(idx >= 0, "'" + needle + "' not found");
            return idx;
        }

        private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ");
    }
}
