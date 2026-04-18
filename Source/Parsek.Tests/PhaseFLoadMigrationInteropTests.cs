using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class PhaseFLoadMigrationInteropTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PhaseFLoadMigrationInteropTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static ConfigNode CreateLegacyTreeNode(
            string treeId,
            string rootRecordingId,
            string recordingId,
            double deltaFunds,
            bool resourcesApplied,
            double explicitStartUT = 100.0,
            double explicitEndUT = 200.0)
        {
            var ic = CultureInfo.InvariantCulture;

            var treeNode = new ConfigNode("RECORDING_TREE");
            treeNode.AddValue("id", treeId);
            treeNode.AddValue("treeName", "Legacy Tree");
            treeNode.AddValue("rootRecordingId", rootRecordingId ?? "");
            if (!string.IsNullOrEmpty(recordingId))
                treeNode.AddValue("activeRecordingId", recordingId);
            treeNode.AddValue("deltaFunds", deltaFunds.ToString("R", ic));
            treeNode.AddValue("deltaScience", "0");
            treeNode.AddValue("deltaRep", "0");
            treeNode.AddValue("resourcesApplied", resourcesApplied.ToString());

            var recNode = treeNode.AddNode("RECORDING");
            recNode.AddValue("recordingId", recordingId);
            recNode.AddValue("vesselName", "Legacy Vessel");
            recNode.AddValue("treeId", treeId);
            recNode.AddValue("recordingFormatVersion", "0");
            recNode.AddValue("loopPlayback", "False");
            recNode.AddValue("loopIntervalSeconds", "10");
            recNode.AddValue("lastResIdx", "-1");
            recNode.AddValue("pointCount", "0");
            recNode.AddValue("explicitStartUT", explicitStartUT.ToString("R", ic));
            recNode.AddValue("explicitEndUT", explicitEndUT.ToString("R", ic));
            return treeNode;
        }

        private static HashSet<string> CollectValidRecordingIds(RecordingTree tree)
        {
            return new HashSet<string>(tree.Recordings.Keys);
        }

        private static RecordingTree RoundTripViaSave(RecordingTree tree, out ConfigNode savedNode)
        {
            savedNode = new ConfigNode("RECORDING_TREE");
            tree.Save(savedNode);
            return RecordingTree.Load(savedNode);
        }

        [Fact]
        public void Legacy_0_7_FormatSave_LoadsUnderPhaseF_PhaseAMigratesSyntheticIntoLedger()
        {
            var legacyTreeNode = CreateLegacyTreeNode(
                treeId: "tree-phasef",
                rootRecordingId: "rec-phasef",
                recordingId: "rec-phasef",
                deltaFunds: 34400.0,
                resourcesApplied: false);

            var tree = RecordingTree.Load(legacyTreeNode);
            RecordingStore.AddCommittedTreeForTesting(tree);

            LedgerOrchestrator.OnKspLoad(CollectValidRecordingIds(tree), maxUT: 1000.0);

            var migrated = Assert.Single(Ledger.Actions.Where(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration
                && a.RecordingId == "rec-phasef"));
            Assert.Equal(34400f, migrated.FundsAwarded, precision: 0);
            Assert.Equal(200.0, migrated.UT);

            var reloaded = RoundTripViaSave(tree, out var savedNode);

            Assert.Equal(RecordingTree.CurrentTreeFormatVersion, reloaded.TreeFormatVersion);
            Assert.Null(reloaded.ConsumeLegacyResidual());
            Assert.Equal(
                RecordingTree.CurrentTreeFormatVersion.ToString(CultureInfo.InvariantCulture),
                savedNode.GetValue("treeFormatVersion"));
            Assert.Null(savedNode.GetValue("deltaFunds"));
            Assert.Null(savedNode.GetValue("resourcesApplied"));
        }

        [Fact]
        public void Legacy_0_7_FormatSave_EmptyRootId_LoadsAndWarnsViaGate()
        {
            var legacyTreeNode = CreateLegacyTreeNode(
                treeId: "tree-empty-root",
                rootRecordingId: "",
                recordingId: "rec-empty-root",
                deltaFunds: 1000.0,
                resourcesApplied: false);

            var tree = RecordingTree.Load(legacyTreeNode);
            RecordingStore.AddCommittedTreeForTesting(tree);

            LedgerOrchestrator.OnKspLoad(CollectValidRecordingIds(tree), maxUT: 1000.0);

            Assert.Empty(Ledger.Actions.Where(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration));

            var gateWarn = Assert.Single(logLines.Where(line =>
                line.Contains("LegacyFormatGate")
                && line.Contains("tree-empty-root")
                && line.Contains("funds=1000")));
            Assert.Contains("Phase A migration could not recover", gateWarn);
            Assert.Contains("will not be applied", gateWarn);

            var reloaded = RoundTripViaSave(tree, out var savedNode);

            Assert.Equal(RecordingTree.CurrentTreeFormatVersion, reloaded.TreeFormatVersion);
            Assert.Null(reloaded.ConsumeLegacyResidual());
            Assert.Null(savedNode.GetValue("deltaFunds"));
        }

        [Fact]
        public void Legacy_0_7_FormatSave_AlreadyMigrated_NoDoubleCreditOnSecondLoad()
        {
            var legacyTreeNode = CreateLegacyTreeNode(
                treeId: "tree-double-load",
                rootRecordingId: "rec-double-load",
                recordingId: "rec-double-load",
                deltaFunds: 34400.0,
                resourcesApplied: false);

            var firstTree = RecordingTree.Load(legacyTreeNode);
            RecordingStore.AddCommittedTreeForTesting(firstTree);

            LedgerOrchestrator.OnKspLoad(CollectValidRecordingIds(firstTree), maxUT: 1000.0);

            RecordingStore.ResetForTesting();

            var secondTree = RecordingTree.Load(legacyTreeNode);
            RecordingStore.AddCommittedTreeForTesting(secondTree);

            LedgerOrchestrator.OnKspLoad(CollectValidRecordingIds(secondTree), maxUT: 1000.0);

            var migrated = Ledger.Actions.Where(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration
                && a.RecordingId == "rec-double-load").ToList();
            Assert.Single(migrated);
            Assert.Equal(34400f, migrated[0].FundsAwarded, precision: 0);
        }
    }
}
