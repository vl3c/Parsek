using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers Phase A of the ledger/lump-sum reconciliation fix
    /// (<c>docs/dev/plans/fix-ledger-lump-sum-reconciliation.md</c>). On load,
    /// a pre-Phase-F committed tree with <c>ResourcesApplied=false</c> and a
    /// non-zero persisted <c>DeltaFunds</c>/<c>DeltaScience</c>/<c>DeltaReputation</c>
    /// must get its residual injected into the ledger as a synthetic action tagged
    /// with the tree's <see cref="RecordingTree.RootRecordingId"/>. This fixes the
    /// revert/rewind stress-test repro where KSP funds silently drew down by
    /// ~30k on every cycle (`KSP.log` "PatchFunds: suspicious drawdown delta=-30395.0").
    ///
    /// <para>Tests live in <see cref="Collection"/> "Sequential" because they
    /// mutate the static <c>committedTrees</c> list on <see cref="RecordingStore"/>
    /// and the static <c>actions</c> list on <see cref="Ledger"/>.</para>
    /// </summary>
    [Collection("Sequential")]
    public class LegacyTreeMigrationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LegacyTreeMigrationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
        }

        // ================================================================
        // Helpers
        // ================================================================

        /// <summary>
        /// Builds a single-recording tree with one trajectory point per UT. Caller
        /// sets the delta fields before handing the tree off to the migration.
        /// </summary>
        private static RecordingTree MakeTree(
            string treeId,
            string rootRecordingId,
            double startUT,
            double endUT,
            double deltaFunds = 0.0,
            double deltaScience = 0.0,
            float deltaRep = 0f,
            bool resourcesApplied = false)
        {
            var rec = new Recording
            {
                RecordingId = rootRecordingId,
                VesselName = "TestVessel",
                TreeId = treeId
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUT });
            rec.Points.Add(new TrajectoryPoint { ut = endUT });

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "TestTree",
                RootRecordingId = rootRecordingId,
                ActiveRecordingId = rootRecordingId,
                DeltaFunds = deltaFunds,
                DeltaScience = deltaScience,
                DeltaReputation = deltaRep,
                ResourcesApplied = resourcesApplied
            };
            tree.Recordings[rec.RecordingId] = rec;
            return tree;
        }

        private static void RegisterTree(RecordingTree tree)
        {
            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        // ================================================================
        // Funds: full residual / partial coverage / zero residual
        // ================================================================

        [Fact]
        public void Funds_FullResidual_NoLedgerActions_SynthesizesOneEarning()
        {
            var tree = MakeTree("tree-1", "rec-root", 100.0, 200.0, deltaFunds: 34400.0);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.NotNull(synth);
            Assert.Equal(34400f, synth.FundsAwarded, precision: 0);
            Assert.Equal("rec-root", synth.RecordingId);
            Assert.Equal(200.0, synth.UT);
            Assert.True(tree.ResourcesApplied);
        }

        [Fact]
        public void Funds_PartialCoverage_SynthesizesResidualOnly()
        {
            var tree = MakeTree("tree-1", "rec-root", 100.0, 200.0, deltaFunds: 34400.0);
            RegisterTree(tree);

            // A milestone already credited 20000 funds to this tree's root recording.
            Ledger.AddAction(new GameAction
            {
                UT = 150.0,
                Type = GameActionType.MilestoneAchievement,
                RecordingId = "rec-root",
                MilestoneId = "FirstOrbit",
                MilestoneFundsAwarded = 20000f
            });

            LedgerOrchestrator.MigrateLegacyTreeResources();

            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.NotNull(synth);
            Assert.Equal(14400f, synth.FundsAwarded, precision: 0);
            Assert.Equal("rec-root", synth.RecordingId);
        }

        [Fact]
        public void Funds_ZeroResidual_NoSynthetic()
        {
            var tree = MakeTree("tree-1", "rec-root", 100.0, 200.0, deltaFunds: 10000.0);
            RegisterTree(tree);

            Ledger.AddAction(new GameAction
            {
                UT = 175.0,
                Type = GameActionType.ContractComplete,
                RecordingId = "rec-root",
                ContractId = "c-1",
                FundsReward = 10000f
            });

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.True(tree.ResourcesApplied);
        }

        // ================================================================
        // Science: full residual / partial coverage / zero residual
        // ================================================================

        [Fact]
        public void Science_FullResidual_NoLedgerActions_SynthesizesOneEarning()
        {
            var tree = MakeTree("tree-sci", "rec-sci", 100.0, 200.0, deltaScience: 42.5);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.ScienceEarning
                && a.SubjectId == "LegacyMigration:tree-sci");
            Assert.NotNull(synth);
            Assert.Equal(42.5f, synth.ScienceAwarded, precision: 2);
            Assert.Equal("rec-sci", synth.RecordingId);
            Assert.Equal(200.0, synth.UT);
        }

        [Fact]
        public void Science_PartialCoverage_SynthesizesResidualOnly()
        {
            var tree = MakeTree("tree-sci", "rec-sci", 100.0, 200.0, deltaScience: 100.0);
            RegisterTree(tree);

            // Milestone with science reward covers some of the residual.
            Ledger.AddAction(new GameAction
            {
                UT = 120.0,
                Type = GameActionType.MilestoneAchievement,
                RecordingId = "rec-sci",
                MilestoneId = "KerbinLanding",
                MilestoneScienceAwarded = 40f
            });

            LedgerOrchestrator.MigrateLegacyTreeResources();

            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.ScienceEarning
                && a.SubjectId == "LegacyMigration:tree-sci");
            Assert.NotNull(synth);
            Assert.Equal(60f, synth.ScienceAwarded, precision: 2);
        }

        [Fact]
        public void Science_ZeroResidual_NoSynthetic()
        {
            var tree = MakeTree("tree-sci", "rec-sci", 100.0, 200.0, deltaScience: 30.0);
            RegisterTree(tree);

            Ledger.AddAction(new GameAction
            {
                UT = 150.0,
                Type = GameActionType.ContractComplete,
                RecordingId = "rec-sci",
                ContractId = "c-sci",
                ScienceReward = 30f
            });

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.ScienceEarning
                && a.SubjectId == "LegacyMigration:tree-sci");
            Assert.True(tree.ResourcesApplied);
        }

        // ================================================================
        // Reputation: full residual / partial coverage / zero residual
        // ================================================================

        [Fact]
        public void Reputation_FullResidual_NoLedgerActions_SynthesizesOneEarning()
        {
            var tree = MakeTree("tree-rep", "rec-rep", 100.0, 200.0, deltaRep: 12.5f);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.ReputationEarning
                && a.RepSource == ReputationSource.Other
                && a.RecordingId == "rec-rep");
            Assert.NotNull(synth);
            Assert.Equal(12.5f, synth.NominalRep, precision: 2);
            Assert.Equal(200.0, synth.UT);
        }

        [Fact]
        public void Reputation_PartialCoverage_SynthesizesResidualOnly()
        {
            var tree = MakeTree("tree-rep", "rec-rep", 100.0, 200.0, deltaRep: 40f);
            RegisterTree(tree);

            Ledger.AddAction(new GameAction
            {
                UT = 150.0,
                Type = GameActionType.MilestoneAchievement,
                RecordingId = "rec-rep",
                MilestoneId = "FirstFlight",
                MilestoneRepAwarded = 15f
            });

            LedgerOrchestrator.MigrateLegacyTreeResources();

            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.ReputationEarning
                && a.RepSource == ReputationSource.Other
                && a.RecordingId == "rec-rep");
            Assert.NotNull(synth);
            Assert.Equal(25f, synth.NominalRep, precision: 2);
        }

        [Fact]
        public void Reputation_ZeroResidual_NoSynthetic()
        {
            var tree = MakeTree("tree-rep", "rec-rep", 100.0, 200.0, deltaRep: 5f);
            RegisterTree(tree);

            Ledger.AddAction(new GameAction
            {
                UT = 160.0,
                Type = GameActionType.ContractComplete,
                RecordingId = "rec-rep",
                ContractId = "c-rep",
                RepReward = 5f
            });

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.ReputationEarning
                && a.RepSource == ReputationSource.Other
                && a.RecordingId == "rec-rep");
            Assert.True(tree.ResourcesApplied);
        }

        // ================================================================
        // Edge cases
        // ================================================================

        [Fact]
        public void ActiveRecordingIdNull_TaggedWithRootRecordingId()
        {
            var tree = MakeTree("tree-null-active", "rec-root", 100.0, 200.0, deltaFunds: 1000.0);
            tree.ActiveRecordingId = null;
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.NotNull(synth);
            Assert.Equal("rec-root", synth.RecordingId);
        }

        [Fact]
        public void EmptyRootRecordingId_LogsWarnAndSkipsSynthetic()
        {
            var tree = MakeTree("tree-empty-root", "will-clear", 100.0, 200.0, deltaFunds: 1000.0);
            tree.RootRecordingId = ""; // simulate pre-root-id persistence artifact
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.False(tree.ResourcesApplied,
                "tree with empty RootRecordingId must not be marked applied — residual was not reconciled");
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("empty RootRecordingId")
                && l.Contains("tree-empty-root"));
        }

        [Fact]
        public void ResourcesAppliedTrue_NoSyntheticInjected()
        {
            var tree = MakeTree("tree-applied", "rec-applied", 100.0, 200.0,
                deltaFunds: 9999.0, resourcesApplied: true);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.Empty(Ledger.Actions);
            Assert.True(tree.ResourcesApplied);
        }

        [Fact]
        public void AllResidualsWithinTolerance_NoSyntheticInjected()
        {
            var tree = MakeTree("tree-tiny", "rec-tiny", 100.0, 200.0,
                deltaFunds: 0.5, deltaScience: 0.05, deltaRep: 0.05f);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.Empty(Ledger.Actions);
            // Tree is still marked applied — we considered it and decided nothing needs injection.
            Assert.True(tree.ResourcesApplied);
        }

        [Fact]
        public void NegativeFundsResidual_SynthesizesSpendingInstead()
        {
            var tree = MakeTree("tree-neg", "rec-neg", 100.0, 200.0, deltaFunds: -5000.0);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            // Negative funds residual uses FundsSpending (cleaner than negative FundsEarning).
            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.FundsSpending
                && a.RecordingId == "rec-neg");
            Assert.NotNull(synth);
            Assert.Equal(5000f, synth.FundsSpent, precision: 0);
            Assert.Equal(FundsSpendingSource.Other, synth.FundsSpendingSource);
            // No positive FundsEarning was injected.
            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.True(tree.ResourcesApplied);
        }

        // ================================================================
        // Purge contract: Reconcile with empty validRecordingIds drops synthetics
        // ================================================================

        [Fact]
        public void Purge_SyntheticIsRemovedWhenTreeRecordingsAreInvalid()
        {
            var tree = MakeTree("tree-purge", "rec-purge", 100.0, 200.0, deltaFunds: 7500.0);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.Single(Ledger.Actions, a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);

            // Simulate the tree's recordings being removed (e.g., via discard or deletion)
            // and a subsequent Reconcile — the synthetic must be pruned along with them,
            // which proves the RootRecordingId tagging gives the correct cleanup contract.
            Ledger.Reconcile(new HashSet<string>(), maxUT: 10_000.0);

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
        }

        // ================================================================
        // Logging assertions
        // ================================================================

        [Fact]
        public void MigratedTree_LogsInfoWithAllThreeResiduals()
        {
            var tree = MakeTree("tree-log", "rec-log", 100.0, 200.0,
                deltaFunds: 3400.0, deltaScience: 25.0, deltaRep: 8f);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("MigrateLegacyTreeResources: migrated tree")
                && l.Contains("tree-log")
                && l.Contains("fundsResidual=")
                && l.Contains("scienceResidual=")
                && l.Contains("repResidual="));
        }
    }
}
