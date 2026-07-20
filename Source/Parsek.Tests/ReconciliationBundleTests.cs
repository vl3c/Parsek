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
            GameStateRecorder.PendingScienceSubjects.Clear();
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
            GameStateRecorder.PendingScienceSubjects.Clear();
        }

        private static ParsekScenario MakeScenario()
        {
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint>(),
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>(),
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
            scenario.RecordingRewindRetirements.Add(new RecordingRewindRetirement
            {
                RetirementId = "rrt_rec2",
                RecordingId = "rec2",
                RestoredRecordingId = "rec1",
                Reason = RecordingRewindRetirement.DefaultReason
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
            Assert.Single(bundle.RecordingRewindRetirements);
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
            scenario.RecordingRewindRetirements.Clear();
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
            Assert.Single(scenario.RecordingRewindRetirements);
            Assert.Equal("rec2", scenario.RecordingRewindRetirements[0].RecordingId);
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

        // ---- Rec-1: route-row retire on the SUCCESS restore path ----
        // (logistics<->time-rewind determinism; plan
        //  docs/dev/plans/fix-logistics-rewind-determinism.md)

        private static GameAction MakeRouteAction(string actionId, double ut, GameActionType type)
        {
            return new GameAction
            {
                ActionId = actionId,
                Type = type,
                UT = ut,
                RouteId = "route-1",
            };
        }

        [Fact]
        public void Restore_WithRouteCutoff_DropsFutureRouteRows_KeepsPastAndNonRoute()
        {
            MakeScenario();
            // non-route future row (kept regardless of UT), past route row (<= cutoff, kept),
            // three future route rows (> cutoff, dropped).
            Ledger.AddAction(MakeAction("nonroute-future", 3000.0)); // FundsEarning
            Ledger.AddAction(MakeRouteAction("route-past", 2000.0, GameActionType.RouteCargoDebited));
            Ledger.AddAction(MakeRouteAction("route-disp", 3000.0, GameActionType.RouteDispatched));
            Ledger.AddAction(MakeRouteAction("route-deliv", 3000.0, GameActionType.RouteCargoDelivered));
            Ledger.AddAction(MakeRouteAction("route-credit", 3500.0, GameActionType.RouteRecoveryCredited));

            var bundle = ReconciliationBundle.Capture();
            // Capture is UNCHANGED: the bundle holds all five rows (so the failed-load
            // rollback can restore them intact).
            Assert.Equal(5, bundle.Actions.Count);

            Ledger.Clear();
            ReconciliationBundle.Restore(bundle, 2500.0);

            // Three future route rows dropped; the non-route future row + the past route row kept.
            Assert.Equal(2, Ledger.Actions.Count);
            Assert.Contains(Ledger.Actions, a => a.ActionId == "nonroute-future");
            Assert.Contains(Ledger.Actions, a => a.ActionId == "route-past");
            Assert.DoesNotContain(Ledger.Actions, a => a.ActionId == "route-disp");
            Assert.DoesNotContain(Ledger.Actions, a => a.ActionId == "route-deliv");
            Assert.DoesNotContain(Ledger.Actions, a => a.ActionId == "route-credit");
        }

        [Fact]
        public void Restore_Parameterless_KeepsAllRouteRows_RollbackContract()
        {
            MakeScenario();
            Ledger.AddAction(MakeRouteAction("route-disp", 3000.0, GameActionType.RouteDispatched));
            Ledger.AddAction(MakeAction("nonroute", 10.0));

            var bundle = ReconciliationBundle.Capture();
            Ledger.Clear();
            // Parameterless overload = +inf cutoff = retire NOTHING (the failed-load
            // rollback contract: the player stays in the pre-rewind world, so the
            // pre-rewind route rows must be restored intact).
            ReconciliationBundle.Restore(bundle);

            Assert.Equal(2, Ledger.Actions.Count);
            Assert.Contains(Ledger.Actions, a => a.ActionId == "route-disp");
        }

        // ---- Re-fly pending-science reconciliation (preservation-branch audit,
        // 2026-07-19): PendingScienceSubjects is in-memory only and the re-fly
        // load skips the quickload discard, so the bundle must capture the list
        // and Restore(cutoff) must drop entries captured strictly after the
        // rewind boundary. Assertions are scoped to this fixture's own seeded
        // entries (the ctor/Dispose clear the static list).

        private static void SeedPendingScience(string subjectId, double captureUT, string recordingId = "rec1")
        {
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = subjectId,
                science = 3.0f,
                subjectMaxValue = 10.0f,
                captureUT = captureUT,
                reasonKey = "ScienceTransmission",
                recordingId = recordingId
            });
        }

        [Fact]
        public void Restore_WithCutoff_DropsPendingScienceStrictlyAfterCutoff_KeepsAtAndBefore()
        {
            MakeScenario();
            SeedPendingScience("bundle-sci-before@Kerbin", 900.0);
            SeedPendingScience("bundle-sci-at@Kerbin", 1000.0);
            SeedPendingScience("bundle-sci-after@Kerbin", 1200.0);

            var bundle = ReconciliationBundle.Capture();
            // Capture is UNCHANGED: the bundle holds all three entries so the
            // failed-load rollback can restore them intact.
            Assert.Equal(3, bundle.PendingScienceSubjects.Count);

            // Simulate the post-load world: entries accumulated in-memory are
            // whatever survived the in-process load (same list). Add a stray
            // entry to prove Restore REPLACES contents (no merge).
            SeedPendingScience("bundle-sci-stray@Kerbin", 950.0);

            ReconciliationBundle.Restore(bundle, 1000.0);

            var live = GameStateRecorder.PendingScienceSubjects;
            Assert.Equal(2, live.Count);
            Assert.Contains(live, s => s.subjectId == "bundle-sci-before@Kerbin");
            // Boundary contract: an entry stamped exactly at the cutoff fired
            // at-or-before the state the quicksave embeds — kept (strict >).
            Assert.Contains(live, s => s.subjectId == "bundle-sci-at@Kerbin");
            Assert.DoesNotContain(live, s => s.subjectId == "bundle-sci-after@Kerbin");
            Assert.DoesNotContain(live, s => s.subjectId == "bundle-sci-stray@Kerbin");

            Assert.Contains(logLines, l =>
                l.Contains("[ReconciliationBundle]") &&
                l.Contains("dropped 1 pending science subject(s) with captureUT > cutoff 1000"));
        }

        [Fact]
        public void Restore_Parameterless_RestoresPendingScienceWholesale_RollbackContract()
        {
            MakeScenario();
            SeedPendingScience("bundle-sci-past@Kerbin", 900.0);
            SeedPendingScience("bundle-sci-future@Kerbin", 999999.0);
            SeedPendingScience("bundle-sci-nan@Kerbin", double.NaN);

            var bundle = ReconciliationBundle.Capture();
            GameStateRecorder.PendingScienceSubjects.Clear();

            // Parameterless overload = +inf cutoff = blind wholesale restore
            // (the failed-load rollback stays in the pre-rewind world, so every
            // captured entry must come back, including future-dated and
            // unclassifiable NaN entries).
            ReconciliationBundle.Restore(bundle);

            var live = GameStateRecorder.PendingScienceSubjects;
            Assert.Equal(3, live.Count);
            Assert.Contains(live, s => s.subjectId == "bundle-sci-past@Kerbin");
            Assert.Contains(live, s => s.subjectId == "bundle-sci-future@Kerbin");
            Assert.Contains(live, s => s.subjectId == "bundle-sci-nan@Kerbin");
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[ReconciliationBundle]") && l.Contains("pending science subject(s) with captureUT"));
        }

        [Fact]
        public void Restore_WithCutoff_KeepsNaNCaptureUtPendingScience()
        {
            MakeScenario();
            SeedPendingScience("bundle-sci-nan2@Kerbin", double.NaN);

            var bundle = ReconciliationBundle.Capture();
            GameStateRecorder.PendingScienceSubjects.Clear();

            ReconciliationBundle.Restore(bundle, 1000.0);

            // NaN > cutoff is false: unclassifiable entries keep today's behavior.
            Assert.Single(GameStateRecorder.PendingScienceSubjects);
            Assert.Equal("bundle-sci-nan2@Kerbin", GameStateRecorder.PendingScienceSubjects[0].subjectId);
        }
    }
}
