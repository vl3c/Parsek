using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression tests for Phase F of the ledger/lump-sum reconciliation fix
    /// (<c>docs/dev/plans/fix-ledger-lump-sum-reconciliation.md</c>).
    ///
    /// <para>The reproducer scenario is the +34400 stress-test save: a committed
    /// tree with <c>tree.DeltaFunds=+34400, ResourcesApplied=false</c> persisted
    /// in the .sfs. Pre-Phase-F, FLIGHT scene entry would invoke
    /// <c>ParsekFlight.ApplyTreeLumpSum</c> and add +34400 to the career pool,
    /// which the ledger walk did not represent — causing
    /// <c>KspStatePatcher.PatchFunds</c> to log
    /// <c>"PatchFunds: suspicious drawdown delta=-..."</c> on every revert/rewind.</para>
    ///
    /// <para>Phase F deletes <c>ApplyTreeLumpSum</c>, <c>ApplyTreeResourceDeltas</c>,
    /// the per-frame call site, and the standalone applier
    /// (<c>ApplyResourceDeltas</c>). Phase A's
    /// <c>LedgerOrchestrator.MigrateLegacyTreeResources</c> remains as the
    /// load-time bridge that converts pre-Phase-F residuals into ledger actions.
    /// These tests pin both halves: Phase A still injects the synthetic; the
    /// runtime applier code paths are gone.</para>
    ///
    /// <para>Lives in <see cref="Collection"/> "Sequential" because it mutates
    /// the static <c>committedTrees</c> on <see cref="RecordingStore"/> and the
    /// static <c>actions</c> list on <see cref="Ledger"/>.</para>
    /// </summary>
    [Collection("Sequential")]
    public class NoLumpSumRegressionTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public NoLumpSumRegressionTests()
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

        private static RecordingTree MakeStressTestTree(double deltaFunds)
        {
            // Mirrors the +34400 reproducer: a committed tree with one recording
            // whose UT window is [100, 200] and a non-zero persisted legacy
            // residual (the bit Phase A migrates on load).
            var rec = new Recording
            {
                RecordingId = "rec-stress",
                VesselName = "StressVessel",
                TreeId = "tree-stress"
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0 });

            var tree = new RecordingTree
            {
                Id = "tree-stress",
                TreeName = "StressTree",
                RootRecordingId = "rec-stress",
                ActiveRecordingId = "rec-stress"
            };
            tree.Recordings[rec.RecordingId] = rec;
            tree.SetLegacyResidualForTesting(
                deltaFunds: deltaFunds,
                deltaScience: 0.0,
                deltaReputation: 0f,
                resourcesApplied: false);
            return tree;
        }

        // ================================================================
        // Phase F: applier code paths are gone
        // ================================================================

        [Fact]
        public void StressTestScenario_NoApplyTreeLumpSumLogEverFires()
        {
            // Build the stress-test tree and run Phase A migration. After Phase F
            // there is no remaining caller of ApplyTreeLumpSum, so even a tree
            // that LOOKS like the original repro must not produce that log line.
            var tree = MakeStressTestTree(deltaFunds: 34400.0);
            RecordingStore.AddCommittedTreeForTesting(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.DoesNotContain(logLines, l => l.Contains("ApplyTreeLumpSum"));
            Assert.DoesNotContain(logLines, l => l.Contains("ApplyTreeResourceDeltas"));
            Assert.DoesNotContain(logLines, l => l.Contains("Tree resource lump sum applied"));
        }

        [Fact]
        public void StressTestScenario_LedgerCarriesMigratedFunds()
        {
            // Phase A injects a FundsEarning(LegacyMigration) action into the
            // ledger so the ledger walk represents the +34400. The Phase F
            // assertion is that this action is the ONLY funds adjustment for
            // this tree — no parallel lump-sum adjustment fires post-load.
            var tree = MakeStressTestTree(deltaFunds: 34400.0);
            RecordingStore.AddCommittedTreeForTesting(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            var migratedFunds = Ledger.Actions.Where(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration
                && a.RecordingId == "rec-stress").ToList();

            Assert.Single(migratedFunds);
            Assert.Equal(34400f, migratedFunds[0].FundsAwarded, precision: 0);
            Assert.Equal(200.0, migratedFunds[0].UT);
            Assert.Equal(1, tree.Recordings["rec-stress"].LastAppliedResourceIndex);
        }

        [Fact]
        public void StressTestScenario_LedgerWalkProducesExpectedFundsBalance()
        {
            // End-to-end: seed a +34400 legacy residual, run Phase A, then walk
            // the ledger via RecalculationEngine. The FundsModule's running
            // balance should equal initial funds + the migrated +34400 — no
            // mysterious drawdown gap, no double-counting from a parallel lump-sum.
            const double initialFunds = 56995.0; // matches reproducer log
            const double legacyDelta = 34400.0;

            var tree = MakeStressTestTree(deltaFunds: legacyDelta);
            RecordingStore.AddCommittedTreeForTesting(tree);

            // Phase A inserts the FundsEarning(LegacyMigration) action into Ledger.Actions.
            LedgerOrchestrator.MigrateLegacyTreeResources();

            // Prepend a FundsInitial seed so the walk has a starting balance.
            // (Phase A doesn't seed funds; that comes from the persisted FundsModule.)
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = (float)initialFunds,
                FundsAwarded = (float)initialFunds
            });

            var funds = new FundsModule();
            RecalculationEngine.ClearModules();
            try
            {
                RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);
                // Recalculate takes a concrete List<GameAction>; copy the read-only view.
                var ledgerActions = new List<GameAction>(Ledger.Actions);
                RecalculationEngine.Recalculate(ledgerActions);

                double expected = initialFunds + legacyDelta;
                Assert.Equal(expected, funds.GetRunningBalance(), 1);
            }
            finally
            {
                RecalculationEngine.ClearModules();
            }
        }

        [Fact]
        public void StressTestScenario_NoSuspiciousDrawdownLog()
        {
            // KspStatePatcher.PatchFunds emits "PatchFunds: suspicious drawdown
            // delta=-..." when a recalculation drops more than 10% of the live
            // funds pool. With Phase F's lump-sum gone and Phase A's migrated
            // residual present, the ledger walk reaches the right target on its
            // own — no spurious drawdown WARN should appear during migration.
            // (This test does not invoke PatchFunds itself; Funding.Instance is
            // null in the test harness, which short-circuits the patcher. The
            // assertion guards against the migration path emitting that log
            // shape via any future refactor that re-introduces the lump sum.)
            var tree = MakeStressTestTree(deltaFunds: 34400.0);
            RecordingStore.AddCommittedTreeForTesting(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.DoesNotContain(logLines,
                l => l.Contains("PatchFunds") && l.Contains("suspicious drawdown"));
        }

        [Fact]
        public void NoApplyResourceDeltasLogFromAnyRecording()
        {
            // Phase F also deleted the per-recording standalone applier
            // (ApplyResourceDeltas, gated by ManagesOwnResources). The test does
            // not exercise the Update loop directly — this is a guard that no
            // production code path emits the old "Timeline resource: …" log
            // shape during a normal Phase A migration cycle.
            var tree = MakeStressTestTree(deltaFunds: 1234.0);
            RecordingStore.AddCommittedTreeForTesting(tree);

            LedgerOrchestrator.MigrateLegacyTreeResources();

            Assert.DoesNotContain(logLines, l => l.Contains("Timeline resource:"));
            Assert.DoesNotContain(logLines, l => l.Contains("Resource deltas complete for"));
        }
    }
}
