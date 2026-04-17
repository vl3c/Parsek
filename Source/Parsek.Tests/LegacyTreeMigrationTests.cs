using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers Phase A of the ledger/lump-sum reconciliation fix
    /// (<c>docs/dev/plans/fix-ledger-lump-sum-reconciliation.md</c>).
    ///
    /// <para>Scope per reviewer: <b>zero-coverage trees only</b>. On load, a pre-Phase-F
    /// committed tree with <c>ResourcesApplied=false</c> and any persisted
    /// <c>DeltaFunds</c>/<c>DeltaScience</c>/<c>DeltaReputation</c> outside tolerance is
    /// migrated only when the ledger has NO action tagged to any of the tree's
    /// recordings within the tree's UT window. Partial-coverage trees log WARN and are
    /// marked applied without injection — comparing pre-walk raw field sums to
    /// post-walk persisted deltas (after ScienceModule cap, ReputationModule curve,
    /// StrategiesModule transform, Effective=false suppression) is structurally wrong.</para>
    ///
    /// <para>Negative science and reputation residuals are skipped with WARN in the
    /// zero-coverage branch too: ScienceModule clamps negative earnings to zero, and
    /// Ledger.Reconcile doesn't yet prune spendings by RecordingId. Negative funds
    /// work because FundsModule.ProcessFundsEarning handles negatives correctly and
    /// earnings ARE pruned by RecordingId.</para>
    ///
    /// <para>All synthetics carry <c>RecordingId=tree.RootRecordingId</c> so
    /// <see cref="Ledger.Reconcile"/> prunes them with the tree on deletion. Trees
    /// with empty <c>RootRecordingId</c> or <c>ComputeEndUT()==0</c> are marked
    /// applied without injection to disarm <c>ApplyTreeLumpSum</c>.</para>
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
        /// sets the delta fields before handing the tree off to the migration. Use
        /// <paramref name="emptyPoints"/> to simulate the degraded-tree case
        /// (<c>ComputeEndUT()==0</c>).
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
            Assert.True(tree.ResourcesApplied);
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
            Assert.True(tree.ResourcesApplied);
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
            Assert.True(tree.ResourcesApplied);
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
            Assert.True(tree.ResourcesApplied);
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
            Assert.True(tree.ResourcesApplied,
                "partial-coverage tree is still marked applied to disarm the lump-sum path");
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
            Assert.True(tree.ResourcesApplied);
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
            Assert.True(tree.ResourcesApplied);
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
            Assert.True(tree.ResourcesApplied,
                "empty-root tree MUST be marked applied to disarm ApplyTreeLumpSum, even though residual is lost");
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("empty RootRecordingId")
                && l.Contains("tree-empty-root"));
        }

        // ================================================================
        // Edge: ResourcesApplied=true -> no-op
        // ================================================================

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
            Assert.True(tree.ResourcesApplied);
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
            Assert.True(tree.ResourcesApplied);
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
        // Negative science: skipped with WARN, mark applied
        // ================================================================

        [Fact]
        public void NegativeScience_SkipsWithWarn_MarksApplied()
        {
            var tree = MakeTree("tree-neg-sci", "rec-neg-sci", 100.0, 200.0,
                deltaScience: -25.0);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.ScienceEarning
                || a.Type == GameActionType.ScienceSpending);
            Assert.True(tree.ResourcesApplied);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("negative persisted deltaScience")
                && l.Contains("tree-neg-sci"));
        }

        // ================================================================
        // Negative reputation: skipped with WARN, mark applied
        // ================================================================

        [Fact]
        public void NegativeReputation_SkipsWithWarn_MarksApplied()
        {
            var tree = MakeTree("tree-neg-rep", "rec-neg-rep", 100.0, 200.0,
                deltaRep: -8f);
            RegisterTree(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.ReputationEarning
                || a.Type == GameActionType.ReputationPenalty);
            Assert.True(tree.ResourcesApplied);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("negative persisted deltaRep")
                && l.Contains("tree-neg-rep"));
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
            MilestoneStore.CurrentEpoch = 0;
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 150.0,
                eventType = GameStateEventType.ContractAccepted,
                key = "contract-guid-di",
                detail = "title=Test",
                epoch = 0
            });

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

        // ================================================================
        // RecordingStore.MarkTreeAsApplied primitive (Phase C dependency)
        // ================================================================

        [Fact]
        public void MarkTreeAsApplied_SetsResourcesAppliedAndAdvancesRecordings()
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
            Assert.True(tree.ResourcesApplied);
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
            Assert.True(tree.ResourcesApplied);
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
            Assert.True(tree.ResourcesApplied);
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
            Assert.True(tree.ResourcesApplied);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("partial ledger coverage")
                && l.Contains("tree-mixed"));
        }

        /// <summary>
        /// <see cref="LedgerOrchestrator.MigrateOldSaveEvents"/> tags its synthesized
        /// actions with <c>RecordingId = null</c> ("can't reliably map old events to
        /// specific recordings"). When the null-tagged action's UT falls inside a
        /// tree's window, that row represents a reward the ledger already carries —
        /// the coverage probe must register it as coverage to prevent double-crediting.
        /// </summary>
        [Fact]
        public void CoverageProbe_NullTaggedInWindow_CountsAsCoverage()
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

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.True(tree.ResourcesApplied);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("partial ledger coverage")
                && l.Contains("tree-null-cov"));
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
            Assert.True(tree.ResourcesApplied);
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
            Assert.True(tree.ResourcesApplied);
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
