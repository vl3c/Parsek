using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers Phase A of the ledger/lump-sum reconciliation fix
    /// (<c>docs/dev/done/plans/fix-ledger-lump-sum-reconciliation.md</c>).
    ///
    /// <para>Scope per reviewer: <b>zero-coverage trees only</b>. On load, a pre-Phase-F
    /// committed tree with a non-zero persisted legacy residual outside tolerance is
    /// migrated only when the ledger has NO action tagged to any of the tree's
    /// recordings within the tree's UT window. Partial-coverage trees log WARN and are
    /// marked fully applied without injection — comparing pre-walk raw field sums to
    /// post-walk persisted deltas (after ScienceModule cap, ReputationModule curve,
    /// StrategiesModule transform, Effective=false suppression) is structurally wrong.</para>
    ///
    /// <para>Negative residuals inject a spending-side synthetic: negative science
    /// emits a ScienceSpending with Cost=-residual, negative rep emits a
    /// ReputationPenalty with NominalPenalty=-residual. Negative funds keep the
    /// existing negative-FundsEarning shape (FundsModule.ProcessFundsEarning handles
    /// negatives correctly). All three are purged by Ledger.Reconcile on tree
    /// deletion — spendings are now pruned by RecordingId symmetrically with
    /// earnings since #441.</para>
    ///
    /// <para>All synthetics carry <c>RecordingId=tree.RootRecordingId</c> so
    /// <see cref="Ledger.Reconcile"/> prunes them with the tree on deletion. Trees
    /// with empty <c>RootRecordingId</c> or <c>ComputeEndUT()==0</c> are marked
    /// fully applied without injection.</para>
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
        /// Builds a single-recording tree with one trajectory point per UT and
        /// populates its transient legacy residual. Use <paramref name="emptyPoints"/>
        /// to simulate the degraded-tree case (<c>ComputeEndUT()==0</c>).
        /// </summary>
        private static RecordingTree MakeTree(
            string treeId,
            string rootRecordingId,
            double startUT,
            double endUT,
            double deltaFunds = 0.0,
            double deltaScience = 0.0,
            float deltaRep = 0f,
            bool resourcesApplied = false,
            bool emptyPoints = false)
        {
            var rec = new Recording
            {
                RecordingId = rootRecordingId,
                VesselName = "TestVessel",
                TreeId = treeId
            };
            if (!emptyPoints)
            {
                rec.Points.Add(new TrajectoryPoint { ut = startUT });
                rec.Points.Add(new TrajectoryPoint { ut = endUT });
            }

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "TestTree",
                RootRecordingId = rootRecordingId,
                ActiveRecordingId = rootRecordingId
            };
            tree.Recordings[rec.RecordingId] = rec;
            tree.SetLegacyResidualForTesting(
                deltaFunds: deltaFunds,
                deltaScience: deltaScience,
                deltaReputation: deltaRep,
                resourcesApplied: resourcesApplied);
            return tree;
        }

        private static void RegisterTree(RecordingTree tree)
        {
            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        private static void AssertTreeRecordingsFullyApplied(RecordingTree tree)
        {
            foreach (var rec in tree.Recordings.Values)
            {
                int expected = rec.Points.Count > 0 ? rec.Points.Count - 1 : -1;
                Assert.Equal(expected, rec.LastAppliedResourceIndex);
            }
        }

        // ================================================================
        // Zero-coverage happy path (3 resources)
        // ================================================================

        [Fact]
        public void ZeroCoverage_Funds_InjectsFullDelta()
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
            AssertTreeRecordingsFullyApplied(tree);
        }

        [Fact]
        public void ZeroCoverage_Science_InjectsFullDelta()
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
            AssertTreeRecordingsFullyApplied(tree);
        }

        [Fact]
        public void ZeroCoverage_Reputation_InjectsFullDelta()
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
            AssertTreeRecordingsFullyApplied(tree);
        }

        [Fact]
        public void ZeroCoverage_AllThreeResources_InjectsAllThree()
        {
            var tree = MakeTree("tree-all", "rec-all", 100.0, 200.0,
                deltaFunds: 5000.0, deltaScience: 20.0, deltaRep: 3f);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.Single(Ledger.Actions, a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.Single(Ledger.Actions, a =>
                a.Type == GameActionType.ScienceEarning
                && a.SubjectId == "LegacyMigration:tree-all");
            Assert.Single(Ledger.Actions, a =>
                a.Type == GameActionType.ReputationEarning
                && a.RepSource == ReputationSource.Other);
            AssertTreeRecordingsFullyApplied(tree);
        }

        // ================================================================
        // Partial coverage: ANY tagged action in window -> skip, WARN, mark applied
        // ================================================================

        [Fact]
        public void PartialCoverage_AnyTaggedAction_SkipsWarnsAndMarksApplied()
        {
            var tree = MakeTree("tree-partial", "rec-root", 100.0, 200.0, deltaFunds: 34400.0);
            RegisterTree(tree);

            // A single ledger action tagged to the tree's recording falls inside the window.
            // This counts as partial coverage regardless of its amount or type.
            Ledger.AddAction(new GameAction
            {
                UT = 150.0,
                Type = GameActionType.MilestoneAchievement,
                RecordingId = "rec-root",
                MilestoneId = "FirstOrbit",
                MilestoneFundsAwarded = 20000f
            });

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            AssertTreeRecordingsFullyApplied(tree);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("partial ledger coverage")
                && l.Contains("tree-partial"));
        }

        [Fact]
        public void PartialCoverage_ActionOutsideWindow_StillCountsAsZeroCoverage()
        {
            // Regression guard: action tagged to tree's recording but at a UT OUTSIDE
            // the tree's [startUT, endUT] window should NOT count as coverage.
            var tree = MakeTree("tree-outside", "rec-root", 100.0, 200.0, deltaFunds: 5000.0);
            RegisterTree(tree);

            // KSC-era action at UT before tree start — not coverage for the tree window.
            Ledger.AddAction(new GameAction
            {
                UT = 50.0,
                Type = GameActionType.FundsSpending,
                RecordingId = "rec-root",
                FundsSpent = 123f,
                FundsSpendingSource = FundsSpendingSource.Other
            });

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.Contains(Ledger.Actions, a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            AssertTreeRecordingsFullyApplied(tree);
        }

        // ================================================================
        // Edge: degraded tree (ComputeEndUT()==0)
        // ================================================================

        [Fact]
        public void DegradedTree_ComputeEndUTZero_NoSyntheticMarksAppliedLogsInfo()
        {
            var tree = MakeTree("tree-degraded", "rec-degraded", 0.0, 0.0,
                deltaFunds: 9999.0, emptyPoints: true);
            RegisterTree(tree);

            Assert.Equal(0.0, tree.ComputeEndUT()); // precondition

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.Empty(Ledger.Actions);
            AssertTreeRecordingsFullyApplied(tree);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("skipping degraded tree")
                && l.Contains("tree-degraded"));
        }

        // ================================================================
        // Edge: empty RootRecordingId -> mark applied, WARN, no synthetic
        // ================================================================

        [Fact]
        public void EmptyRootRecordingId_MarksAppliedAndWarns_NoSynthetic()
        {
            var tree = MakeTree("tree-empty-root", "will-clear", 100.0, 200.0, deltaFunds: 1000.0);
            tree.RootRecordingId = ""; // simulate pre-root-id persistence artifact
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.Empty(Ledger.Actions);
            AssertTreeRecordingsFullyApplied(tree);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("empty RootRecordingId")
                && l.Contains("tree-empty-root"));
        }

        // ================================================================
        // Edge: resourcesApplied=true in the legacy residual -> no-op
        // ================================================================

        [Fact]
        public void LegacyResidualResourcesAppliedTrue_NoSyntheticInjected()
        {
            var tree = MakeTree("tree-applied", "rec-applied", 100.0, 200.0,
                deltaFunds: 9999.0, resourcesApplied: true);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.Empty(Ledger.Actions);
            AssertTreeRecordingsFullyApplied(tree);
        }

        // ================================================================
        // Edge: all residuals sub-tolerance -> no synthetic, mark applied
        // ================================================================

        [Fact]
        public void AllResidualsWithinTolerance_NoSyntheticInjected()
        {
            var tree = MakeTree("tree-tiny", "rec-tiny", 100.0, 200.0,
                deltaFunds: 0.5, deltaScience: 0.05, deltaRep: 0.05f);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.Empty(Ledger.Actions);
            AssertTreeRecordingsFullyApplied(tree);
        }

        // ================================================================
        // Negative funds: FundsEarning with negative FundsAwarded
        // ================================================================

        [Fact]
        public void NegativeFunds_InjectsNegativeFundsEarning()
        {
            var tree = MakeTree("tree-neg-f", "rec-neg-f", 100.0, 200.0, deltaFunds: -5000.0);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.NotNull(synth);
            Assert.Equal(-5000f, synth.FundsAwarded, precision: 0);
            Assert.Equal("rec-neg-f", synth.RecordingId);
            // No FundsSpending synthesized (the v1 approach used FundsSpending for negatives;
            // v2 uses negative FundsEarning so the action is purged by RecordingId).
            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.FundsSpending);
            AssertTreeRecordingsFullyApplied(tree);
        }

        [Fact]
        public void NegativeFunds_RunningBalanceCorrectlyReducedAfterWalk()
        {
            // Verify the negative FundsEarning drives runningBalance down through the
            // real FundsModule walk — closes the reviewer's concern about "poisoning
            // totalEarnings" (both runningBalance and totalEarnings should decrease by
            // the same amount).
            var tree = MakeTree("tree-neg-walk", "rec-neg-walk", 100.0, 200.0,
                deltaFunds: -3000.0);
            RegisterTree(tree);

            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 10000f
            });

            LedgerOrchestrator.MigrateLegacyTreeResources();

            var actions = new List<GameAction>(Ledger.Actions);
            actions.Sort((a, b) => a.UT.CompareTo(b.UT));

            var module = new FundsModule();
            module.Reset();
            module.PrePass(actions);
            for (int i = 0; i < actions.Count; i++) module.ProcessAction(actions[i]);

            // Initial 10000 + (-3000 LegacyMigration) = 7000
            Assert.Equal(7000.0, module.GetRunningBalance(), precision: 2);
            Assert.Equal(-3000.0, module.GetTotalEarnings(), precision: 2);
        }

        // ================================================================
        // Negative science: inject ScienceSpending synthetic tagged with root recording
        // ================================================================

        [Fact]
        public void NegativeScience_InjectsScienceSpendingSynthetic()
        {
            var tree = MakeTree("tree-neg-sci", "rec-neg-sci", 100.0, 200.0,
                deltaScience: -7.7);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.ScienceSpending
                && a.NodeId == "LegacyMigration:tree-neg-sci");
            Assert.NotNull(synth);
            Assert.Equal(7.7f, synth.Cost, precision: 3);
            Assert.Equal("rec-neg-sci", synth.RecordingId);
            Assert.Equal(200.0, synth.UT);
            // No ScienceEarning synthesized (positive-residual path is not taken).
            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.ScienceEarning);
            AssertTreeRecordingsFullyApplied(tree);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("MigrateLegacyTreeResources: migrated tree")
                && l.Contains("tree-neg-sci")
                && l.Contains("deltaScience=-7.7"));
        }

        [Fact]
        public void NegativeScience_SyntheticPurgedByReconcile()
        {
            // #441 contract: Ledger.Reconcile now prunes spendings by RecordingId, so the
            // negative-science synthetic is cleaned up when the tree's recordings are
            // discarded. Without #441's change this test would fail (spending survives).
            var tree = MakeTree("tree-neg-sci-purge", "rec-neg-sci-purge", 100.0, 200.0,
                deltaScience: -12.5);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();
            Assert.Single(Ledger.Actions, a =>
                a.Type == GameActionType.ScienceSpending
                && a.NodeId == "LegacyMigration:tree-neg-sci-purge");

            Ledger.Reconcile(new HashSet<string>(), maxUT: 10_000.0);

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.ScienceSpending);
        }

        // ================================================================
        // Negative reputation: inject ReputationPenalty synthetic tagged with root recording
        // ================================================================

        [Fact]
        public void NegativeReputation_InjectsReputationPenaltySynthetic()
        {
            var tree = MakeTree("tree-neg-rep", "rec-neg-rep", 100.0, 200.0,
                deltaRep: -5f);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.ReputationPenalty
                && a.RecordingId == "rec-neg-rep");
            Assert.NotNull(synth);
            Assert.Equal(5f, synth.NominalPenalty, precision: 2);
            Assert.Equal(ReputationPenaltySource.Other, synth.RepPenaltySource);
            Assert.Equal(200.0, synth.UT);
            // No ReputationEarning synthesized.
            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.ReputationEarning);
            AssertTreeRecordingsFullyApplied(tree);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("MigrateLegacyTreeResources: migrated tree")
                && l.Contains("tree-neg-rep")
                && l.Contains("deltaRep=-5"));
        }

        [Fact]
        public void NegativeReputation_SyntheticPurgedByReconcile()
        {
            // #441 contract: Ledger.Reconcile now prunes spendings by RecordingId, so the
            // negative-rep ReputationPenalty synthetic is cleaned up when the tree's
            // recordings are discarded.
            var tree = MakeTree("tree-neg-rep-purge", "rec-neg-rep-purge", 100.0, 200.0,
                deltaRep: -3f);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();
            Assert.Single(Ledger.Actions, a =>
                a.Type == GameActionType.ReputationPenalty
                && a.RecordingId == "rec-neg-rep-purge");

            Ledger.Reconcile(new HashSet<string>(), maxUT: 10_000.0);

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.ReputationPenalty);
        }

        // ================================================================
        // Root-recording rewrite (#441 round 2 P2): optimizer merge that absorbs the
        // tree's root must retag LegacyMigration synthetics to the successor so the
        // next Ledger.Reconcile doesn't orphan them.
        // ================================================================

        /// <summary>
        /// Round-2 P2 (PR #347 external review): when the optimizer merges the root
        /// recording into a successor, <see cref="RecordingStore"/>'s
        /// <c>UpdateTreeStateAfterOptimizationMerge</c> rewrites
        /// <see cref="RecordingTree.RootRecordingId"/> from the absorbed id to the new
        /// root. Without the retag hook, LegacyMigration synthetics still carry the
        /// old id and get pruned as orphans on the next
        /// <see cref="Ledger.Reconcile"/>. This test exercises the full path:
        /// inject a synthetic, run <see cref="RecordingStore.RunOptimizationPass"/>
        /// on a configured tree whose root is the absorbed segment, then Reconcile
        /// and assert the synthetic survives.
        /// </summary>
        [Fact]
        public void RootRewrite_OptimizationMerge_RetagsLegacyMigrationSyntheticAndSurvivesReconcile()
        {
            // Build two chain-adjacent atmospheric segments that CanAutoMerge accepts.
            // ChainIndex 0 becomes the target; ChainIndex 1 becomes absorbed.
            // We set tree.RootRecordingId to the absorbed id so the retag hook fires.
            var target = new Recording
            {
                RecordingId = "rec_target",
                VesselName = "V",
                TreeId = "tree_rootrewrite",
                ChainId = "chain_rr",
                ChainIndex = 0,
                ChainBranch = 0,
                SegmentPhase = "exo",
                SegmentBodyName = "Mun",
                LoopPlayback = false,
                PlaybackEnabled = true,
                Hidden = false,
                LoopIntervalSeconds = LoopTiming.UntouchedLoopIntervalSentinel
            };
            target.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 50000 });
            target.Points.Add(new TrajectoryPoint { ut = 150.0, altitude = 50000 });

            var absorbed = new Recording
            {
                RecordingId = "rec_absorbed_root",
                VesselName = "V",
                TreeId = "tree_rootrewrite",
                ChainId = "chain_rr",
                ChainIndex = 1,
                ChainBranch = 0,
                SegmentPhase = "exo",
                SegmentBodyName = "Mun",
                LoopPlayback = false,
                PlaybackEnabled = true,
                Hidden = false,
                LoopIntervalSeconds = LoopTiming.UntouchedLoopIntervalSentinel
            };
            absorbed.Points.Add(new TrajectoryPoint { ut = 150.0, altitude = 50000 });
            absorbed.Points.Add(new TrajectoryPoint { ut = 200.0, altitude = 50000 });

            var tree = new RecordingTree
            {
                Id = "tree_rootrewrite",
                TreeName = "RootRewriteTree",
                // Configure root to point at the segment that the optimizer will absorb
                // — this is the P2 scenario the retag hook protects.
                RootRecordingId = absorbed.RecordingId,
                ActiveRecordingId = absorbed.RecordingId
            };
            tree.Recordings[target.RecordingId] = target;
            tree.Recordings[absorbed.RecordingId] = absorbed;

            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddRecordingWithTreeForTesting(target);
            RecordingStore.AddRecordingWithTreeForTesting(absorbed);

            // Inject the LegacyMigration synthetic that P2 is protecting.
            Ledger.AddAction(new GameAction
            {
                UT = 200.0,
                Type = GameActionType.FundsEarning,
                RecordingId = absorbed.RecordingId,
                FundsAwarded = 34400f,
                FundsSource = FundsEarningSource.LegacyMigration
            });

            RecordingStore.RunOptimizationPass();

            // Post-merge: absorbed is gone, target is the new root, synthetic retagged.
            Assert.Equal(target.RecordingId, tree.RootRecordingId);
            Assert.DoesNotContain(absorbed.RecordingId, tree.Recordings.Keys);

            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.NotNull(synth);
            Assert.Equal(target.RecordingId, synth.RecordingId);

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("Optimization merge: retagged")
                && l.Contains("absorbed root '" + absorbed.RecordingId + "'")
                && l.Contains("new root '" + target.RecordingId + "'"));

            // Reconcile with the NEW root id only — synthetic must survive.
            var valid = new HashSet<string> { target.RecordingId };
            Ledger.Reconcile(valid, maxUT: 10_000.0);

            Assert.Single(Ledger.Actions, a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration
                && a.RecordingId == target.RecordingId);
        }

        // ================================================================
        // ActiveRecordingId=null -> still tags with RootRecordingId
        // ================================================================

        [Fact]
        public void ActiveRecordingIdNull_TaggedWithRootRecordingId()
        {
            var tree = MakeTree("tree-null-active", "rec-root", 100.0, 200.0,
                deltaFunds: 1000.0);
            tree.ActiveRecordingId = null;
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.NotNull(synth);
            Assert.Equal("rec-root", synth.RecordingId);
        }

        // ================================================================
        // Purge contract: Reconcile drops synthetics when recordings are gone
        // ================================================================

        [Fact]
        public void Purge_PositiveSynthetic_RemovedWhenTreeRecordingsAreInvalid()
        {
            var tree = MakeTree("tree-purge", "rec-purge", 100.0, 200.0, deltaFunds: 7500.0);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.Single(Ledger.Actions, a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);

            // Simulate the tree's recordings being removed (discard/deletion) and a
            // subsequent Reconcile — the synthetic must be pruned with them.
            Ledger.Reconcile(new HashSet<string>(), maxUT: 10_000.0);

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
        }

        [Fact]
        public void Purge_NegativeFundsSynthetic_RemovedWhenTreeRecordingsAreInvalid()
        {
            // Extra regression: negative FundsEarning is still an earning and MUST be
            // pruned by RecordingId on tree deletion. This is exactly why the v1
            // FundsSpending approach was wrong (Ledger.Reconcile only UT-prunes spendings).
            var tree = MakeTree("tree-purge-neg", "rec-purge-neg", 100.0, 200.0,
                deltaFunds: -2500.0);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();
            Assert.Single(Ledger.Actions, a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);

            Ledger.Reconcile(new HashSet<string>(), maxUT: 10_000.0);

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.FundsEarning);
        }

        // ================================================================
        // Double-inject guard: TryRecoverBrokenLedgerOnLoad synthesizes null-RecordingId
        // KSC actions — those never collide with the migration's tree-tagged synthetics.
        // ================================================================

        [Fact]
        public void DoubleInjectGuard_RecoveryDoesNotDuplicateLegacyMigrationSynthetic()
        {
            // Scenario: zero ledger coverage for a tree, migration injects a
            // LegacyMigration FundsEarning tagged with tree.RootRecordingId. The save
            // also has a GameStateStore event that TryRecoverBrokenLedgerOnLoad would
            // synthesize into a null-RecordingId KSC action. The two MUST NOT be
            // treated as duplicates — they live in disjoint RecordingId spaces
            // (tree-tagged vs. null) and the migration's FundsEarning has no key
            // fields (ContractId, DedupKey) that could collide with the recovery's
            // LedgerHasMatchingAction probe.
            var tree = MakeTree("tree-di", "rec-di", 100.0, 200.0, deltaFunds: 34400.0);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            // Confirm migration synthesized its action.
            var legacyAction = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.NotNull(legacyAction);

            // Now simulate a KSC contract-accept event at a UT that falls inside the
            // tree's UT window but is semantically unrelated (KSC activity during the
            // flight's real-time duration).
            var contractEvt = new GameStateEvent
            {
                ut = 150.0,
                eventType = GameStateEventType.ContractAccepted,
                key = "contract-guid-di",
                detail = "title=Test",
                epoch = 0
            };
            GameStateStore.AddEvent(ref contractEvt);

            int recovered = LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad();

            Assert.Equal(1, recovered);
            // Both the migration's LegacyMigration earning AND the recovery's
            // ContractAccept exist — they do not shadow each other.
            Assert.Single(Ledger.Actions, a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.Single(Ledger.Actions, a =>
                a.Type == GameActionType.ContractAccept
                && a.ContractId == "contract-guid-di");
            // Recovery's action is null-tagged (KSC), migration's is tree-tagged.
            var contractAction = Ledger.Actions.First(a => a.Type == GameActionType.ContractAccept);
            Assert.Null(contractAction.RecordingId);
            Assert.Equal("rec-di", legacyAction.RecordingId);
        }

        [Fact]
        public void DoubleInjectGuard_PartialExistingSynthetics_OnlyInjectsMissingResources()
        {
            var tree = MakeTree(
                "tree-di-partial",
                "rec-di-partial",
                100.0,
                200.0,
                deltaFunds: 34400.0,
                deltaScience: 42.5);
            RegisterTree(tree);

            Ledger.AddAction(new GameAction
            {
                UT = 200.0,
                Type = GameActionType.FundsEarning,
                RecordingId = "rec-di-partial",
                FundsAwarded = 34400f,
                FundsSource = FundsEarningSource.LegacyMigration
            });

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.Single(Ledger.Actions.Where(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration
                && a.RecordingId == "rec-di-partial"));
            Assert.Single(Ledger.Actions.Where(a =>
                a.Type == GameActionType.ScienceEarning
                && a.SubjectId == "LegacyMigration:tree-di-partial"
                && a.RecordingId == "rec-di-partial"));
            AssertTreeRecordingsFullyApplied(tree);
        }

        // ================================================================
        // RecordingStore.MarkTreeAsApplied primitive (Phase C dependency)
        // ================================================================

        [Fact]
        public void MarkTreeAsApplied_AdvancesRecordings()
        {
            var tree = MakeTree("tree-mark", "rec-mark", 100.0, 200.0);
            RegisterTree(tree);

            // Precondition: both recordings have non-empty Points, indexes start at -1.
            var rec1 = new Recording { RecordingId = "rec-mark-2", VesselName = "Second", TreeId = "tree-mark" };
            rec1.Points.Add(new TrajectoryPoint { ut = 110.0 });
            rec1.Points.Add(new TrajectoryPoint { ut = 120.0 });
            rec1.Points.Add(new TrajectoryPoint { ut = 190.0 });
            tree.Recordings[rec1.RecordingId] = rec1;

            foreach (var r in tree.Recordings.Values)
                Assert.Equal(-1, r.LastAppliedResourceIndex);

            int advanced = RecordingStore.MarkTreeAsApplied(tree);

            Assert.Equal(2, advanced);
            Assert.Equal(1, tree.Recordings["rec-mark"].LastAppliedResourceIndex); // 2 points - 1
            Assert.Equal(2, tree.Recordings["rec-mark-2"].LastAppliedResourceIndex); // 3 points - 1
        }

        [Fact]
        public void MarkTreeAsApplied_SkipsEmptyPointRecordings()
        {
            var tree = MakeTree("tree-mark-empty", "rec-mark-empty", 0.0, 0.0, emptyPoints: true);
            RegisterTree(tree);

            int advanced = RecordingStore.MarkTreeAsApplied(tree);

            Assert.Equal(0, advanced);
            Assert.Equal(-1, tree.Recordings["rec-mark-empty"].LastAppliedResourceIndex);
        }

        [Fact]
        public void MarkTreeAsApplied_DoesNotTouchMilestoneReplayIndexes()
        {
            // Snapshot MilestoneStore.Milestones LastReplayedEventIndex values before and
            // after — a tree-scoped mark MUST NOT advance unrelated milestone indexes.
            // This is the exact reason a new primitive was added rather than reusing
            // MarkAllFullyApplied (which does bump milestones globally).
            var tree = MakeTree("tree-mark-no-milestones", "rec-mark-no-milestones", 100.0, 200.0);
            RegisterTree(tree);

            var milestonesBefore = MilestoneStore.Milestones;
            var indexesBefore = new List<int>(milestonesBefore.Count);
            for (int i = 0; i < milestonesBefore.Count; i++)
                indexesBefore.Add(milestonesBefore[i].LastReplayedEventIndex);

            RecordingStore.MarkTreeAsApplied(tree);

            var milestonesAfter = MilestoneStore.Milestones;
            Assert.Equal(indexesBefore.Count, milestonesAfter.Count);
            for (int i = 0; i < milestonesAfter.Count; i++)
                Assert.Equal(indexesBefore[i], milestonesAfter[i].LastReplayedEventIndex);
        }

        [Fact]
        public void MarkTreeAsApplied_NullTree_Safe()
        {
            int advanced = RecordingStore.MarkTreeAsApplied(null);
            Assert.Equal(0, advanced);
        }

        // ================================================================
        // Logging: migrated tree logs INFO with zero-coverage marker
        // ================================================================

        [Fact]
        public void MigratedTree_LogsInfoWithCoverageMarker()
        {
            var tree = MakeTree("tree-log", "rec-log", 100.0, 200.0,
                deltaFunds: 3400.0, deltaScience: 25.0, deltaRep: 8f);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("MigrateLegacyTreeResources: migrated tree")
                && l.Contains("tree-log")
                && l.Contains("coverage=zero")
                && l.Contains("deltaFunds=")
                && l.Contains("deltaScience=")
                && l.Contains("deltaRep="));
        }

        // ================================================================
        // Round-3 P1: coverage probe must ignore KerbalAssignment backfills
        // and must count null-tagged in-window actions as coverage.
        // ================================================================

        /// <summary>
        /// <see cref="LedgerOrchestrator.MigrateKerbalAssignments"/> runs inside
        /// <c>OnKspLoad</c> right before this migration and tags every crewed committed
        /// recording with a <see cref="GameActionType.KerbalAssignment"/> row. The
        /// coverage probe must ignore those rows — otherwise every crewed legacy tree
        /// would be flagged as partially covered and its persisted residual silently
        /// dropped. Only the stress-test repro's uncrewed probe dodged this.
        /// </summary>
        [Fact]
        public void CoverageProbe_KerbalAssignmentAlone_DoesNotCountAsCoverage()
        {
            var tree = MakeTree("tree-crew", "rec-crew", 100.0, 200.0, deltaFunds: 34400.0);
            RegisterTree(tree);

            // Shape that MigrateKerbalAssignments produces: a KerbalAssignment action
            // tagged with the tree's recording id, inside the tree's UT window.
            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-crew",
                KerbalName = "Jebediah Kerman",
                KerbalRole = "Pilot",
                StartUT = 100f,
                EndUT = 200f
            });

            LedgerOrchestrator.MigrateLegacyTreeResources();

            // Probe ignored KerbalAssignment -> zero-coverage path -> full delta injected.
            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.NotNull(synth);
            Assert.Equal(34400f, synth.FundsAwarded, precision: 0);
            Assert.Equal("rec-crew", synth.RecordingId);
            AssertTreeRecordingsFullyApplied(tree);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("partial ledger coverage")
                && l.Contains("tree-crew"));
        }

        /// <summary>
        /// Boring-but-important negative control: the type filter must not accidentally
        /// swallow a real resource-impacting row that happens to share a tree with a
        /// <see cref="GameActionType.KerbalAssignment"/>. Real action should still trip
        /// the partial-coverage path.
        /// </summary>
        [Fact]
        public void CoverageProbe_KerbalAssignmentPlusRealEarning_CountsAsCoverage()
        {
            var tree = MakeTree("tree-mixed", "rec-mixed", 100.0, 200.0, deltaFunds: 34400.0);
            RegisterTree(tree);

            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-mixed",
                KerbalName = "Valentina Kerman",
                KerbalRole = "Pilot"
            });
            // A real funds-impacting row tagged to the same recording. Partial coverage.
            Ledger.AddAction(new GameAction
            {
                UT = 150.0,
                Type = GameActionType.MilestoneAchievement,
                RecordingId = "rec-mixed",
                MilestoneId = "FirstOrbit",
                MilestoneFundsAwarded = 5000f
            });

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            AssertTreeRecordingsFullyApplied(tree);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("partial ledger coverage")
                && l.Contains("tree-mixed"));
        }

        /// <summary>
        /// <see cref="LedgerOrchestrator.MigrateOldSaveEvents"/> tags its synthesized
        /// actions with <c>RecordingId = null</c> ("can't reliably map old events to
        /// specific recordings"). When the null-tagged action's UT falls inside a
        /// tree's window AND <c>migrateOldSaveEventsRanThisLoad=true</c> (i.e., those
        /// synthetics were produced on this very load), the coverage probe must
        /// register them as coverage to prevent double-crediting.
        /// </summary>
        [Fact]
        public void CoverageProbe_NullTaggedInWindow_CountsAsCoverage_WhenMigrateOldSaveEventsRanThisLoad()
        {
            var tree = MakeTree("tree-null-cov", "rec-null-cov", 100.0, 200.0, deltaFunds: 34400.0);
            RegisterTree(tree);

            // Shape that MigrateOldSaveEvents emits: a resource-impacting action with
            // null RecordingId at a UT inside the tree's window.
            Ledger.AddAction(new GameAction
            {
                UT = 150.0,
                Type = GameActionType.ContractComplete,
                RecordingId = null,
                ContractId = "c-old",
                FundsReward = 10000f
            });

            // Precondition for the null-tag coverage branch — signals that this load
            // just synthesized the null-tagged actions via MigrateOldSaveEvents.
            LedgerOrchestrator.SetMigrateOldSaveEventsRanThisLoadForTesting(true);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            AssertTreeRecordingsFullyApplied(tree);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("partial ledger coverage")
                && l.Contains("tree-null-cov"));
        }

        /// <summary>
        /// Round-2 P1 (PR #347 external review): a null-tagged in-window action that
        /// came from <see cref="LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad"/> on a
        /// PRIOR load (persisted in the ledger file) must NOT count as coverage on the
        /// next load. Otherwise a multi-day mission whose window overlaps normal KSC
        /// activity (contract accept, part purchase, etc.) would have its legacy
        /// residual silently dropped.
        /// </summary>
        [Fact]
        public void CoverageProbe_NullTaggedInWindow_DoesNotCountAsCoverage_WhenMigrateOldSaveEventsDidNotRun()
        {
            var tree = MakeTree("tree-null-noflag", "rec-null-noflag", 100.0, 200.0, deltaFunds: 34400.0);
            RegisterTree(tree);

            // Shape that TryRecoverBrokenLedgerOnLoad emits on a prior load and that
            // now sits in the ledger file: a null-tagged ContractAccept inside the
            // tree's window. This is UNRELATED KSC activity — the player accepted a
            // contract during the mission's real-time span — and must not prevent
            // the legacy residual from being migrated.
            Ledger.AddAction(new GameAction
            {
                UT = 150.0,
                Type = GameActionType.ContractAccept,
                RecordingId = null,
                ContractId = "c-ksc-midmission",
                AdvanceFunds = 500f
            });

            // Flag stays false — this load did NOT run MigrateOldSaveEvents.
            Assert.False(LedgerOrchestrator.GetMigrateOldSaveEventsRanThisLoadForTesting());

            LedgerOrchestrator.MigrateLegacyTreeResources();

            // Legacy funds synthetic injected (null-tag rule correctly skipped).
            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.NotNull(synth);
            Assert.Equal(34400f, synth.FundsAwarded, precision: 0);
            Assert.Equal("rec-null-noflag", synth.RecordingId);
            AssertTreeRecordingsFullyApplied(tree);
            // No "partial ledger coverage" WARN — this is the zero-coverage path.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("partial ledger coverage")
                && l.Contains("tree-null-noflag"));
        }

        /// <summary>
        /// Round-2 P1 (PR #347 external review): the flag is reset when a new load
        /// begins. <see cref="LedgerOrchestrator.ResetForTesting"/> models the same
        /// "fresh load" transition tests use between runs — a prior load that set the
        /// flag must not leak state into the next load's coverage probe.
        /// </summary>
        [Fact]
        public void CoverageProbe_MigrateOldSaveEventsFlag_ResetsBetweenLoads()
        {
            // Simulate the state that would exist after a prior load that ran
            // MigrateOldSaveEvents.
            LedgerOrchestrator.SetMigrateOldSaveEventsRanThisLoadForTesting(true);
            Assert.True(LedgerOrchestrator.GetMigrateOldSaveEventsRanThisLoadForTesting());

            // The fresh-load transition (ResetForTesting is the xUnit-safe equivalent
            // of a new KSP session; in production the same reset happens at the top of
            // OnKspLoad before any migration code runs).
            LedgerOrchestrator.ResetForTesting();

            Assert.False(LedgerOrchestrator.GetMigrateOldSaveEventsRanThisLoadForTesting(),
                "migrateOldSaveEventsRanThisLoad must reset between loads");

            // Also directly exercise HasAnyLedgerCoverage via a null-tagged in-window
            // action: with the flag cleared, the probe must return false (zero coverage).
            var tree = MakeTree("tree-reset", "rec-reset", 100.0, 200.0, deltaFunds: 1000.0);
            RegisterTree(tree);

            Ledger.AddAction(new GameAction
            {
                UT = 150.0,
                Type = GameActionType.ContractAccept,
                RecordingId = null,
                ContractId = "c-ksc-after-reset",
                AdvanceFunds = 100f
            });

            LedgerOrchestrator.MigrateLegacyTreeResources();

            // Zero coverage path: synthetic injected despite the null-tagged in-window
            // action, because the flag was cleared.
            Assert.Single(Ledger.Actions, a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration
                && a.RecordingId == "rec-reset");
        }

        /// <summary>
        /// Pipeline-level guard for the load-order handoff between old-save event
        /// migration and legacy-tree residual migration. The first load must let
        /// null-tagged actions synthesized by MigrateOldSaveEvents count as coverage;
        /// the next load must reset that flag so previously-persisted null-tagged KSC
        /// rows do not mask an unrelated legacy tree's zero-coverage residual.
        /// </summary>
        [Fact]
        public void OnKspLoad_OldSaveEventMigrationFlag_CountsOnlyForSameLoad()
        {
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            try
            {
                var firstTree = MakeTree(
                    "tree-load-flag-first",
                    "rec-load-flag-first",
                    100.0,
                    200.0,
                    deltaFunds: 34400.0);
                RegisterTree(firstTree);

                var oldSaveEvent = new GameStateEvent
                {
                    ut = 150.0,
                    eventType = GameStateEventType.ContractCompleted,
                    key = "contract-old-save",
                    detail = "fundsReward=1000;repReward=0;sciReward=0"
                };
                GameStateStore.AddEvent(ref oldSaveEvent);

                LedgerOrchestrator.OnKspLoad(
                    new HashSet<string> { "rec-load-flag-first" },
                    maxUT: 1000.0);

                Assert.Contains(Ledger.Actions, a =>
                    a.Type == GameActionType.ContractComplete
                    && a.RecordingId == null
                    && a.ContractId == "contract-old-save");
                Assert.DoesNotContain(Ledger.Actions, a =>
                    a.Type == GameActionType.FundsEarning
                    && a.FundsSource == FundsEarningSource.LegacyMigration
                    && a.RecordingId == "rec-load-flag-first");
                AssertTreeRecordingsFullyApplied(firstTree);
                Assert.True(LedgerOrchestrator.GetMigrateOldSaveEventsRanThisLoadForTesting());

                var secondTree = MakeTree(
                    "tree-load-flag-second",
                    "rec-load-flag-second",
                    100.0,
                    200.0,
                    deltaFunds: 1200.0);
                RegisterTree(secondTree);

                LedgerOrchestrator.OnKspLoad(
                    new HashSet<string> { "rec-load-flag-first", "rec-load-flag-second" },
                    maxUT: 1000.0);

                var synth = Ledger.Actions.SingleOrDefault(a =>
                    a.Type == GameActionType.FundsEarning
                    && a.FundsSource == FundsEarningSource.LegacyMigration
                    && a.RecordingId == "rec-load-flag-second");
                Assert.NotNull(synth);
                Assert.Equal(1200f, synth.FundsAwarded, precision: 0);
                AssertTreeRecordingsFullyApplied(secondTree);
                Assert.False(LedgerOrchestrator.GetMigrateOldSaveEventsRanThisLoadForTesting());
            }
            finally
            {
                KspStatePatcher.ResetForTesting();
            }
        }

        /// <summary>
        /// Null-tag coverage is bounded by the tree's UT window. A null-tagged action
        /// at a UT OUTSIDE the window is KSC activity unrelated to the tree and must
        /// not short-circuit the zero-coverage path.
        /// </summary>
        [Fact]
        public void CoverageProbe_NullTaggedOutsideWindow_DoesNotCountAsCoverage()
        {
            var tree = MakeTree("tree-null-out", "rec-null-out", 100.0, 200.0, deltaFunds: 1200.0);
            RegisterTree(tree);

            // Null-tagged KSC action BEFORE tree.StartUT — not the tree's concern.
            Ledger.AddAction(new GameAction
            {
                UT = 50.0,
                Type = GameActionType.ContractAccept,
                RecordingId = null,
                ContractId = "c-ksc",
                AdvanceFunds = 500f
            });
            // Null-tagged KSC action AFTER tree.EndUT — also out of window.
            Ledger.AddAction(new GameAction
            {
                UT = 300.0,
                Type = GameActionType.FundsSpending,
                RecordingId = null,
                FundsSpent = 250f,
                FundsSpendingSource = FundsSpendingSource.Other
            });

            LedgerOrchestrator.MigrateLegacyTreeResources();

            // Probe ignored both out-of-window actions -> zero coverage -> full injection.
            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.NotNull(synth);
            Assert.Equal(1200f, synth.FundsAwarded, precision: 0);
            AssertTreeRecordingsFullyApplied(tree);
        }

        /// <summary>
        /// Ordering regression: simulate the real <c>OnKspLoad</c> composition
        /// (<see cref="LedgerOrchestrator.MigrateKerbalAssignments"/> runs first and
        /// backfills KerbalAssignment rows, then this migration runs). Before the
        /// round-3 fix the probe would see the backfilled row and falsely skip. Now
        /// the crewed tree migrates correctly.
        /// </summary>
        [Fact]
        public void OrderingRegression_KerbalBackfillBeforeMigration_StillInjectsDelta()
        {
            var tree = MakeTree("tree-order", "rec-order", 100.0, 200.0, deltaFunds: 7500.0);
            RegisterTree(tree);

            // Step 1: simulate MigrateKerbalAssignments output (its exact shape:
            // KerbalAssignment rows with RecordingId set to the tree's recording id).
            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-order",
                KerbalName = "Bill Kerman",
                KerbalRole = "Engineer"
            });
            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-order",
                KerbalName = "Bob Kerman",
                KerbalRole = "Scientist"
            });

            // Step 2: run our migration as OnKspLoad would.
            LedgerOrchestrator.MigrateLegacyTreeResources();

            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.NotNull(synth);
            Assert.Equal(7500f, synth.FundsAwarded, precision: 0);
            AssertTreeRecordingsFullyApplied(tree);
        }

        // ================================================================
        // IsResourceImpactingAction classifier: exhaustive Theory over every
        // GameActionType value. Forces a code review when a new enum value is
        // added — the default-false branch in the classifier would otherwise
        // silently exclude new action types from the coverage probe.
        // ================================================================

        [Theory]
        [InlineData(GameActionType.ScienceEarning,       true)]
        [InlineData(GameActionType.ScienceSpending,      true)]
        [InlineData(GameActionType.FundsEarning,         true)]
        [InlineData(GameActionType.FundsSpending,        true)]
        [InlineData(GameActionType.MilestoneAchievement, true)]
        [InlineData(GameActionType.ContractAccept,       true)]
        [InlineData(GameActionType.ContractComplete,     true)]
        [InlineData(GameActionType.ContractFail,         true)]
        [InlineData(GameActionType.ContractCancel,       true)]
        [InlineData(GameActionType.ReputationEarning,    true)]
        [InlineData(GameActionType.ReputationPenalty,    true)]
        [InlineData(GameActionType.KerbalHire,           true)]
        [InlineData(GameActionType.FacilityUpgrade,      true)]
        [InlineData(GameActionType.FacilityRepair,       true)]
        [InlineData(GameActionType.StrategyActivate,     true)]
        [InlineData(GameActionType.KerbalAssignment,     false)]
        [InlineData(GameActionType.KerbalRescue,         false)]
        [InlineData(GameActionType.KerbalStandIn,        false)]
        [InlineData(GameActionType.FacilityDestruction,  false)]
        [InlineData(GameActionType.StrategyDeactivate,   false)]
        [InlineData(GameActionType.FundsInitial,         false)]
        [InlineData(GameActionType.ScienceInitial,       false)]
        [InlineData(GameActionType.ReputationInitial,    false)]
        public void IsResourceImpactingAction_Theory(GameActionType type, bool expected)
        {
            Assert.Equal(expected, LedgerOrchestrator.IsResourceImpactingAction(type));
        }

        /// <summary>
        /// Pins the enum surface: if a new <see cref="GameActionType"/> value is added,
        /// the InlineData in <c>IsResourceImpactingAction_Theory</c> must be extended
        /// to match. This check fails loudly when it isn't — preventing a silent
        /// default-false exclusion from the coverage probe.
        /// </summary>
        [Fact]
        public void IsResourceImpactingAction_Theory_CoversEveryEnumValue()
        {
            var attrs = typeof(LegacyTreeMigrationTests)
                .GetMethod(nameof(IsResourceImpactingAction_Theory))
                .GetCustomAttributes(typeof(InlineDataAttribute), inherit: false)
                .Cast<InlineDataAttribute>();

            var covered = new HashSet<GameActionType>();
            foreach (var a in attrs)
            {
                covered.Add((GameActionType)a.GetData(null).First()[0]);
            }

            var all = Enum.GetValues(typeof(GameActionType)).Cast<GameActionType>().ToList();
            var missing = all.Where(t => !covered.Contains(t)).ToList();

            Assert.True(missing.Count == 0,
                $"IsResourceImpactingAction_Theory InlineData is missing entries for: " +
                $"[{string.Join(", ", missing)}]. Add them with the correct expected value " +
                "and update LedgerOrchestrator.IsResourceImpactingAction's switch.");
        }
    }
}
