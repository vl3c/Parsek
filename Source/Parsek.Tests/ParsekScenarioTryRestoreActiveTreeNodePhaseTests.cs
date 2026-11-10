using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the phases extracted from <c>ParsekScenario.TryRestoreActiveTreeNode</c>
    /// (<c>TryRestoreActiveTreeNodePhase*</c>). Their place inside the method is pinned by
    /// <see cref="ParsekScenarioTryRestoreActiveTreeNodeCharacterizationTests"/>; these cells pin
    /// what each one does. Touches shared static state (ParsekLog, RecordingStore,
    /// PendingTreeSlot, the scenario's restore statics), hence Sequential.
    /// </summary>
    [Collection("Sequential")]
    public class ParsekScenarioTryRestoreActiveTreeNodePhaseTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ParsekScenarioTryRestoreActiveTreeNodePhaseTests()
        {
            ResetAll();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ResetAll();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void ResetAll()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            PendingTreeSlot.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.ClearPendingQuickloadResumeContext();
            ParsekScenario.ClearRestoredQuicksaveTreeFactsForTesting();
            ParsekScenario.pendingActiveTreeResumeRewindSave = null;
        }

        private static RecordingTree Tree(string id, string activeRecId, params string[] recIds)
        {
            var tree = new RecordingTree { Id = id, TreeName = "Tree " + id, ActiveRecordingId = activeRecId };
            foreach (string recId in recIds)
                tree.AddOrReplaceRecording(new Recording { RecordingId = recId, TreeId = id, VesselName = recId });
            return tree;
        }

        private static void ArmResumeContext(string treeId)
        {
            ParsekScenario.ConfigurePendingQuickloadResumeContext(
                Tree(treeId, "rec_a", "rec_a"), LoadKind.QuickloadFlight);
            Assert.True(ParsekScenario.MatchesPendingQuickloadResumeContext(treeId));
        }

        // ---- the rewind-in-progress skip ----

        [Fact]
        public void SkipForRewind_DropsTheResumeHintAndConsumesAnArmedSuppression()
        {
            ParsekScenario.pendingActiveTreeResumeRewindSave = "rewind_save";
            PendingTreeSlot.ArmNextActiveTreeRestoreSuppression("discard-refly");

            ParsekScenario.TryRestoreActiveTreeNodePhaseSkipForRewind();

            Assert.Null(ParsekScenario.pendingActiveTreeResumeRewindSave);
            Assert.False(PendingTreeSlot.NextActiveTreeRestoreSuppressionArmedForTesting);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("[Scenario]")
                && l.Contains("TryRestoreActiveTreeNode: consumed active-tree restore suppression while rewind")
                && l.Contains("reason='discard-refly'"));
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("TryRestoreActiveTreeNode: skipped (rewind in progress"));
        }

        [Fact]
        public void SkipForRewind_NoSuppressionArmed_LogsOnlyTheSkip()
        {
            ParsekScenario.TryRestoreActiveTreeNodePhaseSkipForRewind();

            Assert.DoesNotContain(logLines, l => l.Contains("consumed active-tree restore suppression"));
            Assert.Contains(logLines, l => l.Contains("TryRestoreActiveTreeNode: skipped (rewind in progress"));
        }

        // ---- the two empty-tree abandons ----

        [Fact]
        public void AbandonSchemaRejectedTree_ClearsTheResumeStateAndWarns()
        {
            ParsekScenario.pendingActiveTreeResumeRewindSave = "rewind_save";
            ArmResumeContext("t1");

            ParsekScenario.TryRestoreActiveTreeNodePhaseAbandonSchemaRejectedTree(Tree("t1", null));

            Assert.Null(ParsekScenario.pendingActiveTreeResumeRewindSave);
            Assert.False(ParsekScenario.MatchesPendingQuickloadResumeContext("t1"));
            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("TryRestoreActiveTreeNode: skipped active tree 'Tree t1' id=t1 because all recordings were rejected by the schema gate"));
        }

        [Fact]
        public void AbandonEmptiedTree_ClearsTheResumeStateAndWarnsWithTheDropCount()
        {
            ParsekScenario.pendingActiveTreeResumeRewindSave = "rewind_save";
            ArmResumeContext("t2");

            ParsekScenario.TryRestoreActiveTreeNodePhaseAbandonEmptiedTree(Tree("t2", null), 3);

            Assert.Null(ParsekScenario.pendingActiveTreeResumeRewindSave);
            Assert.False(ParsekScenario.MatchesPendingQuickloadResumeContext("t2"));
            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("TryRestoreActiveTreeNode: skipped active tree 'Tree t2' id=t2 after dropping 3 recording(s) with incompatible sidecars"));
        }

        [Fact]
        public void AbandonSuppressedTree_DropsTheResumeHintKeepsTheContextAndLogsTheReason()
        {
            ParsekScenario.pendingActiveTreeResumeRewindSave = "rewind_save";
            ArmResumeContext("t16");

            ParsekScenario.TryRestoreActiveTreeNodePhaseAbandonSuppressedTree(Tree("t16", "a", "a", "b"), "discard-refly");

            Assert.Null(ParsekScenario.pendingActiveTreeResumeRewindSave);
            Assert.True(ParsekScenario.MatchesPendingQuickloadResumeContext("t16"));
            Assert.Contains(logLines, l => l.Contains("[INFO]") && l.Contains(
                "TryRestoreActiveTreeNode: suppressed saved active tree restore tree='Tree t16' id=t16 recordings=2 "
                + "activeRecId=a reason='discard-refly' - leaving committed mission tree intact and not stashing pending-Limbo"));
        }

        // ---- the captured-facts line ----

        [Fact]
        public void LogCapturedQuicksaveFacts_NoFacts_ReportsZeroMembers()
        {
            ParsekScenario.TryRestoreActiveTreeNodePhaseLogCapturedQuicksaveFacts(Tree("t3", "a", "a"));

            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("TryRestoreActiveTreeNode: captured quicksave facts tree='Tree t3' id=t3 members=0 treeCommittedInQuicksave=false"));
        }

        [Fact]
        public void LogCapturedQuicksaveFacts_ReportsTheCapturedMembersAndTreeCommit()
        {
            RecordingTree tree = Tree("t4", "a", "a", "b");
            var facts = ParsekScenario.CaptureQuicksaveTreeFacts(
                tree, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) { "t4" });
            SetLastRestoredFacts(facts);

            ParsekScenario.TryRestoreActiveTreeNodePhaseLogCapturedQuicksaveFacts(tree);

            Assert.Contains(logLines, l => l.Contains(
                "TryRestoreActiveTreeNode: captured quicksave facts tree='Tree t4' id=t4 members="
                + facts.Members.Count + " treeCommittedInQuicksave=true"));
        }

        private static void SetLastRestoredFacts(object facts)
        {
            FieldInfo field = typeof(ParsekScenario).GetField(
                "lastRestoredQuicksaveTreeFacts", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            field.SetValue(null, facts);
        }

        // ---- the chain-link repair ----

        [Fact]
        public void RepairChainLinks_RepairsAMissingPredecessorAndRebuildsTheBackgroundMap()
        {
            var tree = new RecordingTree { Id = "t5", TreeName = "Tree t5", ActiveRecordingId = "succ" };
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "pred", TreeId = "t5", VesselName = "V", VesselPersistentId = 7u,
                TreeOrder = 0, ExplicitStartUT = 100.0, ExplicitEndUT = 150.0,
            });
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "succ", TreeId = "t5", VesselName = "V", VesselPersistentId = 7u,
                TreeOrder = 1, ChainId = "chain", ChainIndex = 1, ChainBranch = 0,
                ExplicitStartUT = 150.0, ExplicitEndUT = 200.0,
            });
            tree.BackgroundMap[999u] = "stale";

            ParsekScenario.TryRestoreActiveTreeNodePhaseRepairChainLinks(tree);

            Recording pred = tree.Recordings["pred"];
            Assert.Equal("chain", pred.ChainId);
            Assert.Equal(0, pred.ChainIndex);
            Assert.False(tree.BackgroundMap.ContainsKey(999u));
            Assert.Contains(logLines, l => l.Contains("RepairMissingChainPredecessor:")
                && l.Contains("context=TryRestoreActiveTreeNode"));
        }

        [Fact]
        public void RepairChainLinks_NothingToRepair_LeavesTheBackgroundMapAlone()
        {
            RecordingTree tree = Tree("t6", "a", "a");
            tree.BackgroundMap[999u] = "stale";

            ParsekScenario.TryRestoreActiveTreeNodePhaseRepairChainLinks(tree);

            Assert.True(tree.BackgroundMap.ContainsKey(999u));
            Assert.DoesNotContain(logLines, l => l.Contains("RepairMissingChainPredecessor:"));
        }

        // ---- the committed-copy disposition line ----

        [Fact]
        public void LogCommittedCopyDisposition_ResumedReFlySession_LogsTheKeptSessionCopy()
        {
            ParsekScenario.TryRestoreActiveTreeNodePhaseLogCommittedCopyDisposition(
                Tree("t7", "a", "a"), true, false, false, false);

            Assert.Contains(logLines, l => l.Contains("[INFO]")
                && l.Contains("kept the committed copy of tree 'Tree t7' (id=t7) committed: the restored tree is the live Re-Fly session sess=<no-id>"));
            Assert.DoesNotContain(logLines, l => l.Contains("needed detaching"));
        }

        [Fact]
        public void LogCommittedCopyDisposition_RearmedAttempt_LogsTheSkippedSplice()
        {
            ParsekScenario.TryRestoreActiveTreeNodePhaseLogCommittedCopyDisposition(
                Tree("t8", "a", "a"), true, true, false, false);

            Assert.Contains(logLines, l => l.Contains(
                "kept the committed copy of tree 'Tree t8' (id=t8) committed and skipped the splice: the restored tree is the re-armed restore attempt's clone"));
            Assert.DoesNotContain(logLines, l => l.Contains("live Re-Fly session"));
        }

        [Fact]
        public void LogCommittedCopyDisposition_LiveAttempt_LogsTheLiveClone()
        {
            ParsekScenario.TryRestoreActiveTreeNodePhaseLogCommittedCopyDisposition(
                Tree("t9", "a", "a"), false, false, true, false);

            Assert.Contains(logLines, l => l.Contains(
                "kept the committed copy of tree 'Tree t9' (id=t9) committed: the restored tree is the live restore attempt's clone"));
        }

        [Fact]
        public void LogCommittedCopyDisposition_NothingDetached_LogsTheNoOp()
        {
            ParsekScenario.TryRestoreActiveTreeNodePhaseLogCommittedCopyDisposition(
                Tree("t10", "a", "a"), false, false, false, false);

            Assert.Contains(logLines, l => l.Contains("[VERBOSE]") && l.Contains(
                "TryRestoreActiveTreeNode: no committed copy of tree 'Tree t10' (id=t10) needed detaching"));
        }

        [Fact]
        public void LogCommittedCopyDisposition_Detached_LogsNothing()
        {
            ParsekScenario.TryRestoreActiveTreeNodePhaseLogCommittedCopyDisposition(
                Tree("t11", "a", "a"), false, false, false, true);

            Assert.DoesNotContain(logLines, l => l.Contains("t11"));
        }

        // ---- the stash ----

        [Fact]
        public void StashAsPendingLimbo_ReplacesThePendingTreeAndReadsTheResumeHint()
        {
            PendingTreeSlot.StashPendingTree(Tree("older", "x", "x"), PendingTreeState.Limbo);
            RecordingTree tree = Tree("t12", "a", "a", "b");
            var node = new ConfigNode("RECORDING_TREE");
            node.AddValue("resumeRewindSave", "parsek_rw_abc");

            ParsekScenario.TryRestoreActiveTreeNodePhaseStashAsPendingLimbo(
                tree, new[] { new ConfigNode("RECORDING_TREE"), node }, 1, 0, 0, 0);

            Assert.Same(tree, PendingTreeSlot.PendingTree);
            Assert.Equal(PendingTreeState.Limbo, PendingTreeSlot.PendingTreeStateValue);
            Assert.Equal("parsek_rw_abc", ParsekScenario.pendingActiveTreeResumeRewindSave);
            Assert.Contains(logLines, l => l.Contains("[INFO]") && l.Contains(
                "TryRestoreActiveTreeNode: stashed active tree 'Tree t12' (2 recording(s), activeRecId=a) "
                + "into pending-Limbo slot for revert-detection dispatch")
                && !l.Contains("sidecar hydration failure") && !l.Contains("spliced"));
            Assert.DoesNotContain(logLines, l => l.Contains("overwriting existing pending tree"));
        }

        [Fact]
        public void StashAsPendingLimbo_NoActiveRecording_StashesAsVesselSwitchLimbo()
        {
            RecordingTree tree = Tree("t13", null, "a");

            ParsekScenario.TryRestoreActiveTreeNodePhaseStashAsPendingLimbo(
                tree, new[] { new ConfigNode("RECORDING_TREE") }, 0, 0, 0, 0);

            Assert.Same(tree, PendingTreeSlot.PendingTree);
            Assert.Equal(PendingTreeState.LimboVesselSwitch, PendingTreeSlot.PendingTreeStateValue);
            Assert.Null(ParsekScenario.pendingActiveTreeResumeRewindSave);
            Assert.Contains(logLines, l => l.Contains(
                "(1 recording(s), activeRecId=<null>) into pending-LimboVesselSwitch slot"));
        }

        [Fact]
        public void StashAsPendingLimbo_ReportsHydrationSalvageAndSpliceCounts()
        {
            ParsekScenario.TryRestoreActiveTreeNodePhaseStashAsPendingLimbo(
                Tree("t14", "a", "a"), new[] { new ConfigNode("RECORDING_TREE") }, 0, 3, 2, 4);

            Assert.Contains(logLines, l => l.Contains(
                "slot for revert-detection dispatch with 3 sidecar hydration failure(s), salvaged 2 from pending, "
                + "spliced 4 post-RP recording(s) from committed tree"));
        }

        [Fact]
        public void StashAsPendingLimbo_FailuresWithoutSalvage_OmitTheSalvageClause()
        {
            ParsekScenario.TryRestoreActiveTreeNodePhaseStashAsPendingLimbo(
                Tree("t15", "a", "a"), new[] { new ConfigNode("RECORDING_TREE") }, 0, 1, 0, 0);

            Assert.Contains(logLines, l => l.Contains("dispatch with 1 sidecar hydration failure(s)")
                && !l.Contains("salvaged") && !l.Contains("spliced"));
        }

        // ---- the unused suppression after the loop ----

        [Fact]
        public void ConsumeUnusedSuppression_ConsumesAnArmedSuppressionAndWarns()
        {
            PendingTreeSlot.ArmNextActiveTreeRestoreSuppression("discard-refly");

            ParsekScenario.TryRestoreActiveTreeNodePhaseConsumeUnusedSuppression();

            Assert.False(PendingTreeSlot.NextActiveTreeRestoreSuppressionArmedForTesting);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains(
                "TryRestoreActiveTreeNode: consumed active-tree restore suppression but no active tree node was present reason='discard-refly'"));
        }

        [Fact]
        public void ConsumeUnusedSuppression_NothingArmed_LogsNothing()
        {
            ParsekScenario.TryRestoreActiveTreeNodePhaseConsumeUnusedSuppression();

            Assert.DoesNotContain(logLines, l => l.Contains("TryRestoreActiveTreeNode"));
        }
    }
}
