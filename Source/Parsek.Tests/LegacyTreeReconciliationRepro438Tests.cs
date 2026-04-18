using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// #438 gap #8: end-to-end repro for the +34400 legacy-tree reconciliation
    /// scenario. Phase A's unit tests in <see cref="LegacyTreeMigrationTests"/>
    /// pin <c>MigrateLegacyTreeResources</c> in isolation; Phase B's tests in
    /// <see cref="EarningsReconciliationTests"/> pin the commit-time diagnostic in
    /// isolation. This file bridges the two: synthesize a legacy tree, call
    /// <see cref="LedgerOrchestrator.OnKspLoad"/>, and assert the injected
    /// synthetic reconciles cleanly with
    /// <see cref="LedgerOrchestrator.ReconcileEarningsWindow"/> under both the
    /// empty-store and store-has-matching-event shapes.
    ///
    /// MakeTree is duplicated locally (verbatim shape from
    /// <see cref="LegacyTreeMigrationTests"/>) rather than extracted to a shared
    /// fixture — one reuse is not enough to warrant the indirection; plan doc
    /// §"File-touch list" explicitly defers extraction.
    /// </summary>
    [Collection("Sequential")]
    public class LegacyTreeReconciliationRepro438Tests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LegacyTreeReconciliationRepro438Tests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
        }

        // ================================================================
        // Helpers — verbatim shape from LegacyTreeMigrationTests.MakeTree.
        // ================================================================

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

            // Phase F removed the public tree-level resource delta fields; legacy
            // residuals now live on a transient seam exposed via
            // SetLegacyResidualForTesting. This mirrors LegacyTreeMigrationTests.MakeTree.
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

        // ================================================================
        // Gap #8 test 1: OnKspLoad injects the LegacyMigration synthetic,
        // tagged with the tree root, at the tree's end-UT, carrying the full
        // residual; the tree is marked applied so the lump-sum does not re-fire.
        // ================================================================

        [Fact]
        public void LegacyTree_34400_OnKspLoad_InjectsSyntheticAtEndUT_MarksResourcesApplied()
        {
            var tree = MakeTree("tree-438-1", "rec-root", 100.0, 200.0,
                deltaFunds: 34400.0);
            RecordingStore.AddCommittedTreeForTesting(tree);

            var valid = new HashSet<string> { "rec-root" };
            LedgerOrchestrator.OnKspLoad(valid, maxUT: 1000.0);

            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.NotNull(synth);
            Assert.Equal(34400f, synth.FundsAwarded, precision: 0);
            Assert.Equal("rec-root", synth.RecordingId);
            Assert.Equal(200.0, synth.UT);
            // Phase F: "resources applied" is now expressed as all recordings having
            // LastAppliedResourceIndex set to the tail (mirrors
            // LegacyTreeMigrationTests.AssertTreeRecordingsFullyApplied).
            foreach (var rec in tree.Recordings.Values)
            {
                int expected = rec.Points.Count > 0 ? rec.Points.Count - 1 : -1;
                Assert.Equal(expected, rec.LastAppliedResourceIndex);
            }

            // The migration logs a per-tree INFO summary with the residuals.
            // Pin that log-capture path for regression coverage.
            Assert.Contains(logLines, l =>
                l.Contains("MigrateLegacyTreeResources") && l.Contains("34400"));
        }

        // ================================================================
        // Gap #8 test 2: the real-world shape — pre-populate GameStateStore
        // with the matching FundsChanged +34400 inside [100, 200]. Both sides
        // now sum to +34400 and the diagnostic is silent.
        // ================================================================

        [Fact]
        public void LegacyTree_34400_WithStoreEvent_ReconcileWindowMatchesSynthetic()
        {
            var tree = MakeTree("tree-438-2", "rec-root-2", 100.0, 200.0,
                deltaFunds: 34400.0);
            RecordingStore.AddCommittedTreeForTesting(tree);

            // Pre-populate GameStateStore with the funds event the legacy save
            // originally captured at some UT inside the tree's window.
            var fundsEvt = new GameStateEvent
            {
                ut = 150.0,
                eventType = GameStateEventType.FundsChanged,
                valueBefore = 0.0,
                valueAfter = 34400.0
            };
            GameStateStore.AddEvent(ref fundsEvt);

            var valid = new HashSet<string> { "rec-root-2" };
            LedgerOrchestrator.OnKspLoad(valid, maxUT: 1000.0);

            var synth = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.LegacyMigration);
            Assert.NotNull(synth);
            Assert.Equal(34400f, synth.FundsAwarded, precision: 0);
            foreach (var rec in tree.Recordings.Values)
            {
                int expected = rec.Points.Count > 0 ? rec.Points.Count - 1 : -1;
                Assert.Equal(expected, rec.LastAppliedResourceIndex);
            }

            // Both sides sum to +34400 -- silent.
            var syntheticsForWindow = Ledger.Actions
                .Where(a => a.Type == GameActionType.FundsEarning
                            && a.FundsSource == FundsEarningSource.LegacyMigration
                            && a.UT >= 100.0 && a.UT <= 200.0)
                .ToList();
            LedgerOrchestrator.ReconcileEarningsWindow(
                GameStateStore.Events,
                syntheticsForWindow,
                startUT: 100.0, endUT: 200.0);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("Earnings reconciliation"));
        }
    }
}
