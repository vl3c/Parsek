using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 6 of Rewind-to-Staging (design §6.4 reconciliation table): guards
    /// <see cref="ReconciliationBundle.Capture"/> / <see cref="ReconciliationBundle.Restore"/>.
    /// </summary>
    [Collection("Sequential")]
    public class ReconciliationBundleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public ReconciliationBundleTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekScenario.ResetInstanceForTesting();

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            GroupHierarchyStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            MilestoneStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            ParsekScenario.ResetInstanceForTesting();

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            GroupHierarchyStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            MilestoneStore.ResetForTesting();
        }

        private static ParsekScenario MakeScenario()
        {
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint>(),
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            return scenario;
        }

        private static Recording MakeRec(string id)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Vessel-" + id,
            };
        }

        private static GameAction MakeAction(string actionId, double ut)
        {
            return new GameAction
            {
                ActionId = actionId,
                Type = GameActionType.FundsEarning,
                UT = ut,
            };
        }

        [Fact]
        public void Capture_RoundTrip_RestoresEveryDomain()
        {
            var scenario = MakeScenario();

            // --- Seed every domain -------------------------------------------
            var rec1 = MakeRec("rec1");
            var rec2 = MakeRec("rec2");
            RecordingStore.AddCommittedInternal(rec1);
            RecordingStore.AddCommittedInternal(rec2);

            Ledger.AddAction(MakeAction("a1", 10.0));
            Ledger.AddAction(MakeAction("a2", 20.0));

            var rp = new RewindPoint
            {
                RewindPointId = "rp_x",
                BranchPointId = "bp_x",
                QuicksaveFilename = "Parsek/RewindPoints/rp_x.sfs",
                UT = 5.0,
                ChildSlots = new List<ChildSlot>(),
            };
            scenario.RewindPoints.Add(rp);

            scenario.RecordingSupersedes.Add(new RecordingSupersedeRelation
            {
                OldRecordingId = "rec1",
                NewRecordingId = "rec2",
                UT = 11.0,
            });
            scenario.LedgerTombstones.Add(new LedgerTombstone
            {
                TombstoneId = "tomb1",
                ActionId = "a2",
            });

            scenario.ActiveReFlySessionMarker = new ReFlySessionMarker
            {
                SessionId = "sess_before",
                RewindPointId = "rp_x",
            };

            CrewReservationManager.SetReplacement("Alice", "Ada");
            CrewReservationManager.SetReplacement("Bob", "Bobbi");

            GroupHierarchyStore.SetGroupParent("ChildGroup", "ParentGroup");
            GroupHierarchyStore.AddHiddenGroup("HiddenA");

            var milestone = new Milestone
            {
                MilestoneId = "m1",
                StartUT = 0.0,
                EndUT = 1.0,
                Events = new List<GameStateEvent>(),
            };
            MilestoneStore.AddMilestoneForTesting(milestone);

            // --- Capture --------------------------------------------------
            var bundle = ReconciliationBundle.Capture();

            Assert.Equal(2, bundle.Recordings.Count);
            Assert.Equal(2, bundle.Actions.Count);
            Assert.Single(bundle.RewindPoints);
            Assert.Single(bundle.RecordingSupersedes);
            Assert.Single(bundle.LedgerTombstones);
            Assert.NotNull(bundle.ActiveReFlySessionMarker);
            Assert.Equal(2, bundle.CrewReplacements.Count);
            Assert.Single(bundle.GroupParents);
            Assert.Single(bundle.HiddenGroups);
            Assert.Single(bundle.Milestones);

            // --- Mutate global state after capture ---------------------------
            RecordingStore.ClearCommittedInternal();
            Ledger.Clear();
            scenario.RewindPoints.Clear();
            scenario.RecordingSupersedes.Clear();
            scenario.LedgerTombstones.Clear();
            scenario.ActiveReFlySessionMarker = null;
            CrewReservationManager.ResetReplacementsForTesting();
            GroupHierarchyStore.groupParents.Clear();
            GroupHierarchyStore.hiddenGroups.Clear();
            MilestoneStore.ResetForTesting();

            // --- Restore ----------------------------------------------------
            ReconciliationBundle.Restore(bundle);

            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            Assert.Equal(2, Ledger.Actions.Count);
            Assert.Single(scenario.RewindPoints);
            Assert.Single(scenario.RecordingSupersedes);
            Assert.Single(scenario.LedgerTombstones);
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Equal("sess_before", scenario.ActiveReFlySessionMarker.SessionId);
            Assert.Equal(2, CrewReservationManager.CrewReplacements.Count);
            Assert.Equal("Ada", CrewReservationManager.CrewReplacements["Alice"]);
            Assert.Equal("Bobbi", CrewReservationManager.CrewReplacements["Bob"]);
            Assert.Single(GroupHierarchyStore.GroupParents);
            Assert.Single(GroupHierarchyStore.HiddenGroups);
            Assert.Single(MilestoneStore.Milestones);

            Assert.Contains(logLines, l =>
                l.Contains("[ReconciliationBundle]") && l.Contains("Captured:"));
            Assert.Contains(logLines, l =>
                l.Contains("[ReconciliationBundle]") && l.Contains("Restored:"));
        }

        [Fact]
        public void Restore_Idempotent_NoDuplicates()
        {
            var scenario = MakeScenario();
            RecordingStore.AddCommittedInternal(MakeRec("rec1"));
            Ledger.AddAction(MakeAction("a1", 1.0));
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_y",
                QuicksaveFilename = "Parsek/RewindPoints/rp_y.sfs",
            });

            var bundle = ReconciliationBundle.Capture();

            // Two consecutive Restores must leave the state identical to one.
            ReconciliationBundle.Restore(bundle);
            ReconciliationBundle.Restore(bundle);

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Single(Ledger.Actions);
            Assert.Single(scenario.RewindPoints);
        }

        [Fact]
        public void Restore_AfterBundle_DoesNotDuplicatePreExistingEntries()
        {
            var scenario = MakeScenario();

            RecordingStore.AddCommittedInternal(MakeRec("rec1"));
            Ledger.AddAction(MakeAction("a1", 1.0));

            // Capture snapshot with 1 of each.
            var bundle = ReconciliationBundle.Capture();

            // Simulate a load swapping in two more of each; Restore must drop
            // them in favor of the bundle's snapshot (no merge, no duplicates).
            RecordingStore.AddCommittedInternal(MakeRec("rec2"));
            RecordingStore.AddCommittedInternal(MakeRec("rec3"));
            Ledger.AddAction(MakeAction("a2", 2.0));
            Ledger.AddAction(MakeAction("a3", 3.0));

            ReconciliationBundle.Restore(bundle);

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("rec1", RecordingStore.CommittedRecordings[0].RecordingId);
            Assert.Single(Ledger.Actions);
            Assert.Equal("a1", Ledger.Actions[0].ActionId);
        }
    }
}
