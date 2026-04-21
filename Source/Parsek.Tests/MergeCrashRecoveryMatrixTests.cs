using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 10 of Rewind-to-Staging: crash-recovery matrix called out
    /// explicitly in the plan (§6.6 "5-point matrix"). Each test targets one
    /// of the five designated crash windows, triggers the crash via
    /// <see cref="MergeJournalOrchestrator.FaultInjectionPoint"/>, invokes
    /// <see cref="MergeJournalOrchestrator.RunFinisher"/>, and asserts
    /// specific scenario-state invariants the reviewer sign-off demands:
    /// relation-list size, tombstone count, marker state, journal state,
    /// MergeState value on the provisional.
    ///
    /// <para>
    /// The five windows:
    /// </para>
    /// <list type="number">
    ///   <item><description>Step 1 supersede append: journal "Begin" written, relations half-appended.</description></item>
    ///   <item><description>Step 3 tombstone scan: relations done, tombstones half-appended.</description></item>
    ///   <item><description>Step 6 reservation recompute: tombstones done, MergeState not flipped.</description></item>
    ///   <item><description>Step 8 durable save: memory committed, disk not yet written.</description></item>
    ///   <item><description>Step 9 reap check: Durable1 done, RPs not yet reaped.</description></item>
    /// </list>
    /// </summary>
    [Collection("Sequential")]
    public class MergeCrashRecoveryMatrixTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly List<string> durableSaveCheckpoints = new List<string>();

        public MergeCrashRecoveryMatrixTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            MergeJournalOrchestrator.ResetTestOverrides();

            MergeJournalOrchestrator.DurableSaveForTesting =
                label => durableSaveCheckpoints.Add(label);
        }

        public void Dispose()
        {
            MergeJournalOrchestrator.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
        }

        // ------------------------------------------------------------------
        // Shared fixture: origin -> inside -> outside + a kerbal-death action
        // in the supersede subtree so the tombstone scan has real work to do.
        // ------------------------------------------------------------------

        private static Recording Rec(string id, string treeId,
            string parentBranchPointId = null, string childBranchPointId = null,
            MergeState state = MergeState.Immutable,
            TerminalState? terminal = null, string supersedeTargetId = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = state,
                TerminalStateValue = terminal,
                ParentBranchPointId = parentBranchPointId,
                ChildBranchPointId = childBranchPointId,
                SupersedeTargetId = supersedeTargetId,
            };
        }

        private ParsekScenario BuildFixture(out Recording provisional, out GameAction kerbalDeath)
        {
            var origin = Rec("rec_origin", "tree_1", childBranchPointId: "bp_c");
            var inside = Rec("rec_inside", "tree_1", parentBranchPointId: "bp_c");
            var outside = Rec("rec_outside", "tree_1");
            var bp_c = new BranchPoint
            {
                Id = "bp_c",
                Type = BranchPointType.Undock,
                UT = 0.0,
                ParentRecordingIds = new List<string> { "rec_origin" },
                ChildRecordingIds = new List<string> { "rec_inside" },
            };
            var tree = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Test_tree_1",
                BranchPoints = new List<BranchPoint> { bp_c },
            };
            foreach (var rec in new[] { origin, inside, outside })
            {
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            }
            RecordingStore.CommittedTrees.Add(tree);

            provisional = Rec("rec_provisional", "tree_1",
                state: MergeState.NotCommitted,
                terminal: TerminalState.Landed,
                supersedeTargetId: "rec_origin");
            RecordingStore.AddRecordingWithTreeForTesting(provisional, "tree_1");

            kerbalDeath = new GameAction
            {
                ActionId = "act_death_1",
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec_origin",
                KerbalName = "Jeb",
                KerbalEndStateField = KerbalEndState.Dead,
                UT = 50.0,
            };
            Ledger.AddAction(kerbalDeath);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_crash_matrix",
                TreeId = "tree_1",
                ActiveReFlyRecordingId = "rec_provisional",
                OriginChildRecordingId = "rec_origin",
                RewindPointId = "rp_1",
                InvokedUT = 0.0,
            };

            var rps = new List<RewindPoint>
            {
                new RewindPoint
                {
                    RewindPointId = "rp_session_matrix",
                    BranchPointId = "bp_c",
                    UT = 0.0,
                    SessionProvisional = true,
                    CreatingSessionId = "sess_crash_matrix",
                    ChildSlots = new List<ChildSlot>(),
                },
            };

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = rps,
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();
            SessionSuppressionState.ResetForTesting();
            return scenario;
        }

        private MergeJournalOrchestrator.FaultInjectionException RunUntil(
            MergeJournalOrchestrator.Phase at, ParsekScenario scenario, Recording provisional)
        {
            MergeJournalOrchestrator.FaultInjectionPoint = at;
            try
            {
                MergeJournalOrchestrator.RunMerge(
                    scenario.ActiveReFlySessionMarker, provisional);
                return null;
            }
            catch (MergeJournalOrchestrator.FaultInjectionException ex)
            {
                return ex;
            }
            finally
            {
                MergeJournalOrchestrator.FaultInjectionPoint = null;
            }
        }

        // ================================================================
        // Window 1: supersede append (Phase.Supersede just written).
        // Expected post-crash state: supersedes in memory (not yet durable),
        // no tombstones, marker present, MergeState still NotCommitted.
        // Finisher rollback: marker + journal cleared, session-provisional
        // removed.
        // ================================================================

        [Fact]
        public void Window1_SupersedeAppend_RollsBack_ProvisionalRemoved()
        {
            var scenario = BuildFixture(out var provisional, out _);
            RunUntil(MergeJournalOrchestrator.Phase.Supersede, scenario, provisional);

            Assert.Equal(MergeJournal.Phases.Supersede, scenario.ActiveMergeJournal.Phase);
            Assert.Equal(2, scenario.RecordingSupersedes.Count);
            Assert.Empty(scenario.LedgerTombstones);
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Equal(MergeState.NotCommitted, provisional.MergeState);
            Assert.Contains("begin", durableSaveCheckpoints);
            // No post-merge durable save fired before the crash.
            Assert.DoesNotContain("durable1", durableSaveCheckpoints);

            MergeJournalOrchestrator.RunFinisher();

            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.DoesNotContain(RecordingStore.CommittedRecordings,
                r => r != null && r.RecordingId == "rec_provisional");
            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]") && l.Contains("Rolled back"));
        }

        // ================================================================
        // Window 2: tombstone scan (Phase.Tombstone just written).
        // Expected post-crash: supersedes + tombstones in memory; MergeState
        // still NotCommitted; marker present. Rollback.
        // ================================================================

        [Fact]
        public void Window2_TombstoneScan_RollsBack_MarkerAndJournalCleared()
        {
            var scenario = BuildFixture(out var provisional, out var kerbalDeath);
            RunUntil(MergeJournalOrchestrator.Phase.Tombstone, scenario, provisional);

            Assert.Equal(MergeJournal.Phases.Tombstone, scenario.ActiveMergeJournal.Phase);
            Assert.Equal(2, scenario.RecordingSupersedes.Count);
            Assert.Single(scenario.LedgerTombstones);
            Assert.Equal(kerbalDeath.ActionId,
                scenario.LedgerTombstones[0].ActionId);
            Assert.Equal(MergeState.NotCommitted, provisional.MergeState);
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Contains("begin", durableSaveCheckpoints);
            Assert.DoesNotContain("durable1", durableSaveCheckpoints);

            MergeJournalOrchestrator.RunFinisher();

            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Null(scenario.ActiveReFlySessionMarker);
        }

        // ================================================================
        // Window 3: reservation recompute / MergeState flip (Phase.Finalize).
        // Expected post-crash: MergeState flipped in memory BUT Durable1
        // never happened, so disk still shows NotCommitted + no supersedes.
        // Rollback is still the right recovery.
        // ================================================================

        [Fact]
        public void Window3_FinalizeFlip_RollsBack_EvenThoughMergeStateFlipped()
        {
            var scenario = BuildFixture(out var provisional, out _);
            RunUntil(MergeJournalOrchestrator.Phase.Finalize, scenario, provisional);

            Assert.Equal(MergeJournal.Phases.Finalize, scenario.ActiveMergeJournal.Phase);
            // In-memory the flip has happened; on disk (simulated by the
            // absence of durable1 in the checkpoint list) it has not.
            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.Null(provisional.SupersedeTargetId);
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Contains("begin", durableSaveCheckpoints);
            Assert.DoesNotContain("durable1", durableSaveCheckpoints);

            MergeJournalOrchestrator.RunFinisher();

            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Null(scenario.ActiveReFlySessionMarker);
        }

        // ================================================================
        // Window 4: durable save (Phase.Durable1Done just written).
        // Expected post-crash: durable1 fired, marker still present, RPs not
        // yet tagged. Completion path.
        // ================================================================

        [Fact]
        public void Window4_Durable1Done_CompletesRemaining_RpsTagged()
        {
            var scenario = BuildFixture(out var provisional, out _);
            RunUntil(MergeJournalOrchestrator.Phase.Durable1Done, scenario, provisional);

            Assert.Equal(MergeJournal.Phases.Durable1Done, scenario.ActiveMergeJournal.Phase);
            Assert.Contains("begin", durableSaveCheckpoints);
            Assert.Contains("durable1", durableSaveCheckpoints);
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            // Pre-finisher: the RP is still session-provisional (tag step not run yet).
            Assert.True(scenario.RewindPoints[0].SessionProvisional);

            MergeJournalOrchestrator.RunFinisher();

            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            // Phase 11: the session-provisional RP had empty ChildSlots so
            // the reaper pass at the RpReap checkpoint drops it entirely
            // after the tag step flips SessionProvisional=false.
            Assert.Empty(scenario.RewindPoints);
            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.Equal(2, scenario.RecordingSupersedes.Count);
            Assert.Single(scenario.LedgerTombstones);
            Assert.Contains("finisher-durable2", durableSaveCheckpoints);
            Assert.Contains("durable3", durableSaveCheckpoints);
        }

        // ================================================================
        // Window 5: reap check (Phase.RpReap just written).
        // Expected post-crash: RPs already promoted; marker still present.
        // Completion drives marker clear + Durable2 + Durable3.
        // ================================================================

        [Fact]
        public void Window5_RpReap_CompletesRemaining_MarkerClear()
        {
            var scenario = BuildFixture(out var provisional, out _);
            RunUntil(MergeJournalOrchestrator.Phase.RpReap, scenario, provisional);

            Assert.Equal(MergeJournal.Phases.RpReap, scenario.ActiveMergeJournal.Phase);
            // Phase 11: RP tag + reap step both ran pre-crash. The fixture
            // RP has empty slots so it was already reaped from the scenario.
            Assert.Empty(scenario.RewindPoints);
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Contains("begin", durableSaveCheckpoints);
            Assert.Contains("durable1", durableSaveCheckpoints);
            Assert.DoesNotContain("finisher-durable2", durableSaveCheckpoints);

            MergeJournalOrchestrator.RunFinisher();

            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Contains("finisher-durable2", durableSaveCheckpoints);
            Assert.Contains("durable3", durableSaveCheckpoints);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") && l.Contains("End reason=merged"));
        }
    }
}
