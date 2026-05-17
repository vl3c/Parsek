using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 10 of Rewind-to-Staging (design §5.8, §6.6, §6.9 step 2, §10.8):
    /// guards the journaled staged-commit orchestrator.
    ///
    /// <para>
    /// Covers the happy path (all 5 checkpoints advance, journal cleared),
    /// the 4 pre-durable rollback cases (crash at Begin / Supersede /
    /// Tombstone / Finalize -> session restored), the 4 post-durable
    /// completion cases (crash at Durable1Done / RpReap / MarkerCleared /
    /// Durable2Done -> finisher completes remaining steps), and the
    /// idempotence cases (no-journal no-op, Complete-phase clears,
    /// double-finisher no side-effects).
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class MergeJournalOrchestratorTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly List<string> durableSaveCheckpoints = new List<string>();
        private readonly List<(string saveName, string saveFolder, SaveMode mode)> saveCalls
            = new List<(string, string, SaveMode)>();
        private readonly List<string> journalPhasesAtSave = new List<string>();

        public MergeJournalOrchestratorTests()
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

        // ---------- Helpers -------------------------------------------------

        private static Recording Rec(string id, string treeId,
            string parentBranchPointId = null, string childBranchPointId = null,
            MergeState state = MergeState.Immutable,
            TerminalState? terminal = null,
            string supersedeTargetId = null)
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

        private static BranchPoint Bp(string id, BranchPointType type,
            List<string> parents = null, List<string> children = null)
        {
            return new BranchPoint
            {
                Id = id,
                Type = type,
                UT = 0.0,
                ParentRecordingIds = parents ?? new List<string>(),
                ChildRecordingIds = children ?? new List<string>(),
            };
        }

        private static void InstallTree(string treeId, List<Recording> recordings,
            List<BranchPoint> branchPoints)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Test_" + treeId,
                BranchPoints = branchPoints ?? new List<BranchPoint>(),
            };
            foreach (var rec in recordings)
            {
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec, treeId);
            }
            var trees = RecordingStore.CommittedTrees;
            for (int i = trees.Count - 1; i >= 0; i--)
                if (trees[i].Id == treeId) trees.RemoveAt(i);
            trees.Add(tree);
        }

        private static ParsekScenario InstallScenario(
            ReFlySessionMarker marker, List<RewindPoint> rps = null)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = rps ?? new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker,
                ActiveMergeJournal = null,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();
            SessionSuppressionState.ResetForTesting();
            return scenario;
        }

        private static ReFlySessionMarker Marker(
            string originId, string provisionalId,
            string sessionId = "sess_merge_1", string treeId = "tree_1")
        {
            return new ReFlySessionMarker
            {
                SessionId = sessionId,
                TreeId = treeId,
                ActiveReFlyRecordingId = provisionalId,
                OriginChildRecordingId = originId,
                RewindPointId = "rp_1",
                InvokedUT = 0.0,
            };
        }

        // origin -> inside closure + 1 outside recording. Matches Phase 8/9
        // fixtures so the shared walk semantics apply identically.
        private void InstallOriginClosureFixture(
            string originId, string insideId, string outsideId,
            TerminalState? originTerminal = null, TerminalState? insideTerminal = null)
        {
            var origin = Rec(originId, "tree_1",
                childBranchPointId: "bp_c", terminal: originTerminal);
            var inside = Rec(insideId, "tree_1",
                parentBranchPointId: "bp_c", terminal: insideTerminal);
            var outside = Rec(outsideId, "tree_1");
            var bp_c = Bp("bp_c", BranchPointType.Undock,
                parents: new List<string> { originId },
                children: new List<string> { insideId });
            InstallTree("tree_1",
                new List<Recording> { origin, inside, outside },
                new List<BranchPoint> { bp_c });
        }

        private static Recording AddProvisional(string recordingId, string treeId,
            TerminalState? terminal, string supersedeTargetId)
        {
            var provisional = Rec(recordingId, treeId,
                state: MergeState.NotCommitted,
                terminal: terminal,
                supersedeTargetId: supersedeTargetId);
            // Satisfy SupersedeCommit.AppendRelations supersede-target
            // invariant (>=1 trajectory point + non-null terminal). Tests
            // that exercise the empty / null-terminal cases construct
            // provisionals directly.
            provisional.Points.Add(new TrajectoryPoint { ut = 0.0 });
            RecordingStore.AddRecordingWithTreeForTesting(provisional, treeId);
            return provisional;
        }

        private static RewindPoint Rp(string id, string creatingSessionId,
            bool sessionProvisional = true)
        {
            return new RewindPoint
            {
                RewindPointId = id,
                BranchPointId = "bp_c",
                UT = 0.0,
                SessionProvisional = sessionProvisional,
                CreatingSessionId = creatingSessionId,
                ChildSlots = new List<ChildSlot>(),
            };
        }

        /// <summary>
        /// Sets up a standard happy-path merge fixture:
        /// origin + inside + outside recordings; provisional with
        /// Landed terminal; 1 session-provisional RP matching the marker's
        /// session id so TagRpsForReap has work to do.
        /// </summary>
        private (ParsekScenario scenario, Recording provisional) MakeStandardFixture(
            TerminalState terminal = TerminalState.Landed)
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                terminal, supersedeTargetId: "rec_origin");
            var rps = new List<RewindPoint>
            {
                Rp("rp_session_1", "sess_merge_1"),
                Rp("rp_persistent", "sess_merge_1", sessionProvisional: false),
            };
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"), rps);
            return (scenario, provisional);
        }

        // ================================================================
        // Happy path
        // ================================================================

        [Fact]
        public void RunMerge_HappyPath_AllPhasesCompleted_JournalCleared()
        {
            var (scenario, provisional) = MakeStandardFixture();

            bool ok = MergeJournalOrchestrator.RunMerge(
                scenario.ActiveReFlySessionMarker, provisional);

            Assert.True(ok);

            // Final state: journal + marker cleared, MergeState flipped,
            // supersedes + tombstones + RP tag + durable saves fired.
            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.Null(provisional.SupersedeTargetId);
            Assert.Equal(2, scenario.RecordingSupersedes.Count);

            // Phase 11: the session-provisional RP gets tagged (flag flipped)
            // and then reaped in the same RpReap checkpoint window because
            // its ChildSlots list is empty (no open re-fly target). The
            // pre-existing persistent RP (also empty slots) is reaped too.
            Assert.DoesNotContain(scenario.RewindPoints,
                r => r.RewindPointId == "rp_session_1");
            Assert.DoesNotContain(scenario.RewindPoints,
                r => r.RewindPointId == "rp_persistent");

            // Begin + TreeMerge (Bug fix-refly-abandon-and-fork-persist §Bug2b)
            // + Split (post-Begin durable barrier from
            // fix-supersede-identity-scope plan §5) + all 3 stable durable
            // barriers fired in order. The fixture's origin doesn't span the
            // rewind UT so the splitter returns Skipped=true, but the Split
            // DurableSave barrier still fires (the journal advances to
            // Phase.Split regardless of split work). TreeMerge is gated on
            // marker.InPlaceContinuation but the DurableSave still fires
            // (the journal advances to TreeMerge regardless of migrate work).
            Assert.Equal(new[] { "begin", "treemerge", "split", "durable1", "durable2", "durable3" },
                durableSaveCheckpoints.ToArray());

            // §10.8 journal log contract.
            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]") && l.Contains("sess=sess_merge_1")
                && l.Contains("phase=Begin"));
            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]") && l.Contains("Completed sess=sess_merge_1"));
            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]") && l.Contains("sess=sess_merge_1 cleared"));
        }

        [Fact]
        public void RunMerge_SlotLookupFailure_AbortsBeforeJournalRelationsOrTombstones()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Orbiting, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = "bp_missing";
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));
            scenario.RecordingSupersedes.Add(new RecordingSupersedeRelation
            {
                RelationId = "rsr_existing",
                OldRecordingId = "rec_prior_old",
                NewRecordingId = "rec_prior_new",
                UT = 1.0,
            });
            scenario.LedgerTombstones.Add(new LedgerTombstone
            {
                TombstoneId = "tomb_existing",
                ActionId = "act_existing",
                RetiringRecordingId = "rec_prior_new",
            });
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_death",
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec_origin",
                KerbalEndStateField = KerbalEndState.Dead,
                UT = 12.0,
            });
            int relationCountBefore = scenario.RecordingSupersedes.Count;
            int tombstoneCountBefore = scenario.LedgerTombstones.Count;

            var ex = Assert.Throws<InvalidOperationException>(() =>
                MergeJournalOrchestrator.RunMerge(
                    scenario.ActiveReFlySessionMarker, provisional));

            Assert.Contains("Site B-1 slot lookup failed", ex.Message);
            Assert.Equal(MergeState.NotCommitted, provisional.MergeState);
            Assert.Equal("rec_origin", provisional.SupersedeTargetId);
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Empty(durableSaveCheckpoints);
            Assert.Equal(relationCountBefore, scenario.RecordingSupersedes.Count);
            Assert.Equal(tombstoneCountBefore, scenario.LedgerTombstones.Count);
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.NewRecordingId == "rec_provisional");
            Assert.DoesNotContain(scenario.LedgerTombstones,
                t => t.ActionId == "act_death");
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Site B-1 slot lookup failed")
                && l.Contains("aborting because stable-leaf classification cannot safely fall back"));
        }

        [Fact]
        public void TagRpsForReap_NormalOriginRpWithNullCreatingSession_Promotes()
        {
            var marker = Marker("rec_origin", "rec_provisional");
            var originRp = Rp("rp_1", null, sessionProvisional: true);
            var unrelatedNormalRp = Rp("rp_other", null, sessionProvisional: true);
            var scenario = InstallScenario(marker,
                new List<RewindPoint> { originRp, unrelatedNormalRp });

            MergeJournalOrchestrator.TagRpsForReap(marker, scenario);

            Assert.False(originRp.SessionProvisional);
            Assert.Null(originRp.CreatingSessionId);
            Assert.True(unrelatedNormalRp.SessionProvisional);
        }

        [Fact]
        public void TagRpsForReap_LogSplitsSessionAndNormalOriginCounts()
        {
            // Follow-up to PR #504 review: the "promoted N session-provisional
            // RP(s)" summary conflated the two code paths. The log now breaks
            // the total out into fromSession + fromNormalOrigin so a grep can
            // tell the new normal-origin promotion path from the old same-
            // session path without re-reading code.
            var marker = Marker("rec_origin", "rec_provisional");
            // marker.RewindPointId = "rp_1" (Marker default) matches normal-origin.
            var normalOriginRp = Rp("rp_1", null, sessionProvisional: true);
            var sessionRp = Rp("rp_session", "sess_merge_1", sessionProvisional: true);
            var scenario = InstallScenario(marker,
                new List<RewindPoint> { normalOriginRp, sessionRp });

            MergeJournalOrchestrator.TagRpsForReap(marker, scenario);

            Assert.False(normalOriginRp.SessionProvisional);
            Assert.False(sessionRp.SessionProvisional);

            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]")
                && l.Contains("TagRpsForReap: promoted 2 session-provisional RP(s)")
                && l.Contains("fromSession=1")
                && l.Contains("fromNormalOrigin=1"));
        }

        [Fact]
        public void RunMerge_Crashed_PromotesProvisionalToCommittedProvisional()
        {
            var (scenario, provisional) = MakeStandardFixture(TerminalState.Destroyed);

            MergeJournalOrchestrator.RunMerge(
                scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.CommittedProvisional, provisional.MergeState);
        }

        // ================================================================
        // Fault-injection setup helpers
        // ================================================================

        private MergeJournalOrchestrator.FaultInjectionException TryRunWithFault(
            MergeJournalOrchestrator.Phase at,
            ReFlySessionMarker marker, Recording provisional)
        {
            MergeJournalOrchestrator.FaultInjectionPoint = at;
            try
            {
                MergeJournalOrchestrator.RunMerge(marker, provisional);
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
        // Roll-back path (crash at or before Finalize)
        // ================================================================

        [Fact]
        public void CrashAtBegin_Finisher_RollsBack_SessionRestored()
        {
            var (scenario, provisional) = MakeStandardFixture();
            var faultMarker = scenario.ActiveReFlySessionMarker;

            var ex = TryRunWithFault(MergeJournalOrchestrator.Phase.Begin,
                faultMarker, provisional);
            Assert.NotNull(ex);

            // Post-crash: journal on disk at Phase=Begin, no supersedes yet,
            // marker + provisional still present.
            Assert.NotNull(scenario.ActiveMergeJournal);
            Assert.Equal(MergeJournal.Phases.Begin, scenario.ActiveMergeJournal.Phase);
            Assert.Empty(scenario.RecordingSupersedes);
            Assert.NotNull(scenario.ActiveReFlySessionMarker);

            // Finisher drives rollback.
            bool ran = MergeJournalOrchestrator.RunFinisher();
            Assert.True(ran);

            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            // Session-provisional removed from the committed list.
            Assert.DoesNotContain(RecordingStore.CommittedRecordings,
                r => r != null && r.RecordingId == "rec_provisional");

            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]") && l.Contains("Rolled back from phase=Begin"));
        }

        [Fact]
        public void CrashAtBegin_Finisher_RemovesAllSessionTaggedFragments()
        {
            // Reproduces the post-failure orphan: optimizer split during
            // FinalizeTreeRecordings creates a new chain-tail Recording that
            // inherits CreatingSessionId via RunOptimizationSplitPass; when
            // AppendRelations then trips and the journal rolls back, both the
            // head and the optimizer-created tail must go away — otherwise the
            // tail survives degraded-to-standalone in the recordings table.
            var (scenario, provisional) = MakeStandardFixture();
            var marker = scenario.ActiveReFlySessionMarker;
            provisional.CreatingSessionId = marker.SessionId;
            provisional.ChainId = "chain_session";
            provisional.ChainIndex = 0;

            // Faux optimizer-created split tail tagged with the same session id.
            var splitTail = new Recording
            {
                RecordingId = "rec_split_tail",
                VesselName = "rec_split_tail",
                TreeId = provisional.TreeId,
                MergeState = MergeState.NotCommitted,
                CreatingSessionId = marker.SessionId,
                ChainId = "chain_session",
                ChainIndex = 1,
            };
            splitTail.Points.Add(new TrajectoryPoint { ut = 1.0 });
            RecordingStore.AddRecordingWithTreeForTesting(splitTail, provisional.TreeId);

            var ex = TryRunWithFault(MergeJournalOrchestrator.Phase.Begin,
                marker, provisional);
            Assert.NotNull(ex);

            MergeJournalOrchestrator.RunFinisher();

            Assert.DoesNotContain(RecordingStore.CommittedRecordings,
                r => r != null && r.RecordingId == "rec_provisional");
            Assert.DoesNotContain(RecordingStore.CommittedRecordings,
                r => r != null && r.RecordingId == "rec_split_tail");
            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]")
                && l.Contains("removed 2 session-provisional recording(s)")
                && l.Contains("sess_merge_1"));
        }

        [Fact]
        public void CrashAtSupersede_Finisher_DrivesForward_JournalCleared()
        {
            // fix-supersede-identity-scope plan W2/W5 reclassification: under
            // the new post-Split-durable barrier classification, a crash at
            // Supersede drives forward via idempotent re-run instead of
            // rolling back. AppendRelations skips existing rows; the
            // remaining steps (Tombstone, Finalize, Durable1Done tail)
            // complete the merge.
            var (scenario, provisional) = MakeStandardFixture();
            var ex = TryRunWithFault(MergeJournalOrchestrator.Phase.Supersede,
                scenario.ActiveReFlySessionMarker, provisional);
            Assert.NotNull(ex);

            Assert.Equal(MergeJournal.Phases.Supersede,
                scenario.ActiveMergeJournal.Phase);
            // Supersede relations are in-memory but not yet durable.
            Assert.Equal(2, scenario.RecordingSupersedes.Count);

            MergeJournalOrchestrator.RunFinisher();

            // Drive-forward: journal cleared + marker cleared (merge
            // completed via CompleteFromPostDurable).
            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]") && l.Contains("Completed from phase=Supersede"));
        }

        [Fact]
        public void CrashAtTombstone_Finisher_DrivesForward_JournalCleared()
        {
            // fix-supersede-identity-scope plan W2/W5 reclassification:
            // Tombstone is now post-Begin-durable, drives forward via
            // CommitTombstones idempotency (alreadyTombstoned dedup) +
            // Finalize re-run + tail.
            var (scenario, provisional) = MakeStandardFixture();
            TryRunWithFault(MergeJournalOrchestrator.Phase.Tombstone,
                scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeJournal.Phases.Tombstone,
                scenario.ActiveMergeJournal.Phase);

            MergeJournalOrchestrator.RunFinisher();

            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]") && l.Contains("Completed from phase=Tombstone"));
        }

        [Fact]
        public void CrashAtFinalize_Finisher_DrivesForward_JournalCleared()
        {
            // fix-supersede-identity-scope plan W2/W5 reclassification:
            // Finalize is now post-Begin-durable, drives forward via
            // FlipMergeStateAndClearTransient idempotency (rewrites same
            // fields with same inputs) + the existing Durable1Done tail.
            var (scenario, provisional) = MakeStandardFixture();
            TryRunWithFault(MergeJournalOrchestrator.Phase.Finalize,
                scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeJournal.Phases.Finalize,
                scenario.ActiveMergeJournal.Phase);
            // MergeState already flipped in memory before the fault inject.
            Assert.Equal(MergeState.Immutable, provisional.MergeState);

            MergeJournalOrchestrator.RunFinisher();

            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            // Idempotent re-run keeps the flip in place.
            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]") && l.Contains("Completed from phase=Finalize"));
        }

        // ================================================================
        // Completion path (crash after Durable1Done)
        // ================================================================

        [Fact]
        public void CrashAtDurable1Done_Finisher_CompletesRemaining_JournalCleared()
        {
            var (scenario, provisional) = MakeStandardFixture();
            TryRunWithFault(MergeJournalOrchestrator.Phase.Durable1Done,
                scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeJournal.Phases.Durable1Done,
                scenario.ActiveMergeJournal.Phase);
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            // "treemerge" (Bug fix-refly-abandon-and-fork-persist §Bug2b) +
            // "split" (fix-supersede-identity-scope plan §5) inserted
            // between "begin" and "durable1" as post-Begin-durable barriers.
            Assert.Equal(new[] { "begin", "treemerge", "split", "durable1" },
                durableSaveCheckpoints);

            MergeJournalOrchestrator.RunFinisher();

            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            // Phase 11: RPs tagged + reaped during finisher completion.
            // Both rp_session_1 and rp_persistent have empty ChildSlots, so
            // they become reap-eligible the moment SessionProvisional flips
            // and the reaper drops them.
            Assert.DoesNotContain(scenario.RewindPoints,
                r => r.RewindPointId == "rp_session_1");

            // The begin checkpoint plus the pre-crash durable1 and the
            // finisher's deferred durable2 + durable3 all fire.
            Assert.Contains("begin", durableSaveCheckpoints);
            Assert.Contains("durable1", durableSaveCheckpoints);
            Assert.Contains("finisher-durable2", durableSaveCheckpoints);
            Assert.Contains("durable3", durableSaveCheckpoints);

            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]") && l.Contains("Completed from phase=Durable1Done"));
        }

        [Fact]
        public void CrashAtTreeMerge_Finisher_DrivesForwardThroughSplit()
        {
            // Bug fix-refly-abandon-and-fork-persist §Bug2b: TreeMerge is a
            // post-Begin-durable phase, so a crash with disk-phase=TreeMerge
            // must drive forward through Split → Supersede → … → Complete
            // via CompleteFromPostDurable. IsKnownPostBeginPhase(TreeMerge)
            // returning true is what routes the dispatch.
            var (scenario, provisional) = MakeStandardFixture();
            TryRunWithFault(MergeJournalOrchestrator.Phase.TreeMerge,
                scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeJournal.Phases.TreeMerge,
                scenario.ActiveMergeJournal.Phase);
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            // Pre-finisher: only begin + treemerge fired.
            Assert.Equal(new[] { "begin", "treemerge" }, durableSaveCheckpoints);

            MergeJournalOrchestrator.RunFinisher();

            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]") && l.Contains("Completed from phase=TreeMerge"));
        }

        [Fact]
        public void CrashAtRpReap_Finisher_CompletesRemaining_JournalCleared()
        {
            var (scenario, provisional) = MakeStandardFixture();
            TryRunWithFault(MergeJournalOrchestrator.Phase.RpReap,
                scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeJournal.Phases.RpReap,
                scenario.ActiveMergeJournal.Phase);

            MergeJournalOrchestrator.RunFinisher();

            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]") && l.Contains("Completed from phase=RpReap"));
        }

        [Fact]
        public void CrashAtMarkerCleared_Finisher_CompletesRemaining_JournalCleared()
        {
            var (scenario, provisional) = MakeStandardFixture();
            TryRunWithFault(MergeJournalOrchestrator.Phase.MarkerCleared,
                scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeJournal.Phases.MarkerCleared,
                scenario.ActiveMergeJournal.Phase);
            Assert.Null(scenario.ActiveReFlySessionMarker); // already cleared

            MergeJournalOrchestrator.RunFinisher();

            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]") && l.Contains("Completed from phase=MarkerCleared"));
        }

        [Fact]
        public void CrashAtDurable2Done_Finisher_CompletesRemaining_JournalCleared()
        {
            var (scenario, provisional) = MakeStandardFixture();
            TryRunWithFault(MergeJournalOrchestrator.Phase.Durable2Done,
                scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeJournal.Phases.Durable2Done,
                scenario.ActiveMergeJournal.Phase);

            MergeJournalOrchestrator.RunFinisher();

            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]") && l.Contains("Completed from phase=Durable2Done"));
        }

        // ================================================================
        // Idempotency
        // ================================================================

        [Fact]
        public void Finisher_NoJournal_NoOp()
        {
            var (scenario, _) = MakeStandardFixture();
            Assert.Null(scenario.ActiveMergeJournal);

            bool ran = MergeJournalOrchestrator.RunFinisher();

            Assert.False(ran);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]") && l.Contains("no active journal"));
        }

        [Fact]
        public void Finisher_CompletePhase_ClearsJournal_ReturnsTrue()
        {
            var (scenario, _) = MakeStandardFixture();
            scenario.ActiveMergeJournal = new MergeJournal
            {
                JournalId = "mj_test",
                SessionId = "sess_merge_1",
                Phase = MergeJournal.Phases.Complete,
                StartedUT = 0.0,
            };

            bool ran = MergeJournalOrchestrator.RunFinisher();

            Assert.True(ran);
            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Contains("durable3", durableSaveCheckpoints);
        }

        [Fact]
        public void Finisher_CalledTwice_Idempotent()
        {
            var (scenario, provisional) = MakeStandardFixture();
            TryRunWithFault(MergeJournalOrchestrator.Phase.Durable1Done,
                scenario.ActiveReFlySessionMarker, provisional);

            bool firstRun = MergeJournalOrchestrator.RunFinisher();
            Assert.True(firstRun);
            Assert.Null(scenario.ActiveMergeJournal);

            // Second call: no journal -> no-op.
            bool secondRun = MergeJournalOrchestrator.RunFinisher();
            Assert.False(secondRun);
        }

        [Fact]
        public void RunMerge_ProductionPath_PersistsStableCheckpointsToPersistent()
        {
            MergeJournalOrchestrator.DurableSaveForTesting = null;
            MergeJournalOrchestrator.SaveGameForTesting = (saveName, saveFolder, mode) =>
            {
                saveCalls.Add((saveName, saveFolder, mode));
                journalPhasesAtSave.Add(
                    ParsekScenario.Instance?.ActiveMergeJournal?.Phase ?? "<cleared>");
                return "ok";
            };

            var (scenario, provisional) = MakeStandardFixture();

            bool ok = MergeJournalOrchestrator.RunMerge(
                scenario.ActiveReFlySessionMarker, provisional);

            Assert.True(ok);
            // 6 save calls: begin + treemerge (Bug fix-refly-abandon-and-fork-
            // persist §Bug2b) + split + durable1 + durable2 + durable3.
            Assert.Equal(6, saveCalls.Count);
            Assert.All(saveCalls, call =>
            {
                Assert.Equal("persistent", call.saveName);
                Assert.Equal(SaveMode.OVERWRITE, call.mode);
            });
            Assert.Equal(
                new[]
                {
                    MergeJournal.Phases.Begin,
                    MergeJournal.Phases.TreeMerge,
                    MergeJournal.Phases.Split,
                    MergeJournal.Phases.Durable1Done,
                    MergeJournal.Phases.Durable2Done,
                    "<cleared>",
                },
                journalPhasesAtSave);
        }

        [Fact]
        public void RunFinisher_ProductionPath_DoesNotInvokeSaveGame()
        {
            var (scenario, provisional) = MakeStandardFixture();
            TryRunWithFault(MergeJournalOrchestrator.Phase.Durable1Done,
                scenario.ActiveReFlySessionMarker, provisional);

            MergeJournalOrchestrator.DurableSaveForTesting = null;
            MergeJournalOrchestrator.SaveGameForTesting = (saveName, saveFolder, mode) =>
            {
                saveCalls.Add((saveName, saveFolder, mode));
                return "ok";
            };

            bool ran = MergeJournalOrchestrator.RunFinisher();

            Assert.True(ran);
            Assert.Empty(saveCalls);
        }

        [Fact]
        public void Finisher_UnknownPhase_TreatsAsRollback()
        {
            var (scenario, _) = MakeStandardFixture();
            scenario.ActiveMergeJournal = new MergeJournal
            {
                JournalId = "mj_test",
                SessionId = "sess_merge_1",
                Phase = "NotARealPhase",
                StartedUT = 0.0,
            };

            bool ran = MergeJournalOrchestrator.RunFinisher();
            Assert.True(ran);
            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]") && l.Contains("unknown phase"));
        }

        // ================================================================
        // Split-phase orchestration (fix-supersede-identity-scope plan §5)
        // ================================================================

        /// <summary>
        /// Builds an origin recording whose Points + TrackSection.frames
        /// include a sample at <paramref name="midUT"/> so
        /// <c>SplitAtSection</c>'s Unity-runtime-only Slerp interpolation
        /// branch is bypassed when the split UT lands on midUT. Use the
        /// same value for midUT and the planned rewindUT so the splitter
        /// finds an existing point at the cut.
        /// </summary>
        private static Recording BuildSpanningOrigin(
            string id, string treeId, double startUT, double midUT, double endUT,
            TerminalState? terminal = null)
        {
            var origin = new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = MergeState.Immutable,
                TerminalStateValue = terminal,
            };
            origin.Points.Add(new TrajectoryPoint { ut = startUT });
            if (midUT > startUT && midUT < endUT)
                origin.Points.Add(new TrajectoryPoint { ut = midUT });
            origin.Points.Add(new TrajectoryPoint { ut = endUT });
            origin.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = (midUT > startUT && midUT < endUT)
                    ? new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = startUT },
                        new TrajectoryPoint { ut = midUT },
                        new TrajectoryPoint { ut = endUT },
                    }
                    : new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = startUT },
                        new TrajectoryPoint { ut = endUT },
                    },
            });
            return origin;
        }

        [Fact]
        public void RunMerge_OriginSpansRewindUT_CallsSplitterBeforeSupersede()
        {
            // Install an origin recording that spans rewindUT [8..53] with
            // rewindUT=34, plus a provisional fork. RunMerge should:
            //  1. Advance through the Split phase + fire DurableSave("split").
            //  2. Mutate marker.SupersedeTargetId to TIP's id (the post-split
            //     half), so the AppendRelations closure starts at TIP.
            //  3. The resulting supersede row points TIP → fork (not origin → fork).
            var origin = BuildSpanningOrigin("rec_origin", "tree_1",
                startUT: 8.0, midUT: 34.0, endUT: 53.0,
                terminal: TerminalState.Destroyed);
            var tree = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Test_tree_1",
                RootRecordingId = "rec_origin",
                ActiveRecordingId = "rec_origin",
                BranchPoints = new List<BranchPoint>(),
            };
            tree.AddOrReplaceRecording(origin);
            RecordingStore.AddRecordingWithTreeForTesting(origin, "tree_1");
            var trees = RecordingStore.CommittedTrees;
            for (int i = trees.Count - 1; i >= 0; i--)
                if (trees[i].Id == "tree_1") trees.RemoveAt(i);
            trees.Add(tree);

            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var marker = Marker("rec_origin", "rec_provisional");
            marker.RewindPointUT = 34.0;
            var scenario = InstallScenario(marker);

            bool ok = MergeJournalOrchestrator.RunMerge(marker, provisional);
            Assert.True(ok);

            // Split barrier fired between begin and durable1.
            Assert.Contains("split", durableSaveCheckpoints);
            int beginIdx = durableSaveCheckpoints.IndexOf("begin");
            int splitIdx = durableSaveCheckpoints.IndexOf("split");
            int durable1Idx = durableSaveCheckpoints.IndexOf("durable1");
            Assert.True(beginIdx < splitIdx && splitIdx < durable1Idx,
                $"Expected begin({beginIdx}) < split({splitIdx}) < durable1({durable1Idx}) " +
                $"in {string.Join(",", durableSaveCheckpoints)}");

            // Splitter mutated marker.SupersedeTargetId to TIP's id (a new id).
            // marker is cleared at merge end, so the "after" check needs the
            // resulting supersede rows to show TIP → fork.
            // The TIP recording is a new Recording in the committed list
            // sharing rec_origin's ChainId with ChainIndex=1.
            Recording head = null, tip = null;
            foreach (var rec in RecordingStore.CommittedRecordings)
            {
                if (rec == null) continue;
                if (rec.RecordingId == "rec_origin") head = rec;
                else if (rec.TreeId == "tree_1" && rec.RecordingId != "rec_provisional"
                    && rec.ChainIndex == 1) tip = rec;
            }
            Assert.NotNull(head);
            Assert.NotNull(tip);
            Assert.Equal(head.ChainId, tip.ChainId);
            Assert.Equal(0, head.ChainIndex);
            Assert.Equal(1, tip.ChainIndex);
            Assert.Equal(34.0, head.EndUT);
            Assert.Equal(34.0, tip.StartUT);

            // The supersede row added by AppendRelations points TIP → fork,
            // not origin → fork (the data-correctness goal of the fix).
            Assert.Contains(scenario.RecordingSupersedes, r =>
                r.OldRecordingId == tip.RecordingId
                && r.NewRecordingId == "rec_provisional");
            // HEAD is NOT superseded by fork — it's a pre-rewind chain head
            // carved out by SupersedeCommit.IsPreRewindCarveOut.
            Assert.DoesNotContain(scenario.RecordingSupersedes, r =>
                r.OldRecordingId == "rec_origin"
                && r.NewRecordingId == "rec_provisional");
        }

        [Fact]
        public void RunMerge_OriginDoesNotSpanRewindUT_SkipsSplit()
        {
            // Origin lies entirely post-rewind: [40..53] with rewindUT=34.
            // SplitOriginAtRewindUT returns Skipped=true with no mutation.
            // The Split phase still advances (and DurableSave("split") fires)
            // because the journal barrier is unconditional, but the marker's
            // SupersedeTargetId is untouched so AppendRelations writes
            // origin → fork (today's whole-recording supersede shape).
            var origin = BuildSpanningOrigin("rec_origin", "tree_1",
                startUT: 40.0, midUT: double.NaN, endUT: 53.0,
                terminal: TerminalState.Destroyed);
            var tree = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Test_tree_1",
                RootRecordingId = "rec_origin",
                ActiveRecordingId = "rec_origin",
                BranchPoints = new List<BranchPoint>(),
            };
            tree.AddOrReplaceRecording(origin);
            RecordingStore.AddRecordingWithTreeForTesting(origin, "tree_1");
            var trees = RecordingStore.CommittedTrees;
            for (int i = trees.Count - 1; i >= 0; i--)
                if (trees[i].Id == "tree_1") trees.RemoveAt(i);
            trees.Add(tree);

            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            int committedBefore = RecordingStore.CommittedRecordings.Count;
            var marker = Marker("rec_origin", "rec_provisional");
            marker.RewindPointUT = 34.0;
            var scenario = InstallScenario(marker);

            bool ok = MergeJournalOrchestrator.RunMerge(marker, provisional);
            Assert.True(ok);

            // Split barrier still fires (unconditional advance + DurableSave).
            Assert.Contains("split", durableSaveCheckpoints);
            // No new recording inserted.
            Assert.Equal(committedBefore, RecordingStore.CommittedRecordings.Count);
            // origin recordings is unchanged.
            Recording head = null;
            foreach (var rec in RecordingStore.CommittedRecordings)
            {
                if (rec == null) continue;
                if (rec.RecordingId == "rec_origin") head = rec;
            }
            Assert.NotNull(head);
            Assert.Equal(40.0, head.StartUT);
            Assert.Equal(53.0, head.EndUT);

            // The supersede row uses origin's id (unchanged) as oldRecordingId.
            Assert.Contains(scenario.RecordingSupersedes, r =>
                r.OldRecordingId == "rec_origin"
                && r.NewRecordingId == "rec_provisional");
            // Splitter logged the skip.
            Assert.Contains(logLines, l =>
                l.Contains("[Splitter]") && l.Contains("skip")
                && l.Contains("do not strictly span"));
        }

        [Fact]
        public void RunFinisher_PhaseSplit_DrivesForwardThroughComplete()
        {
            // Install a journal at phase=Split (post-Split DurableSave fired,
            // crash before Supersede). RunFinisher should drive forward
            // through Supersede → Tombstone → Finalize → Durable1Done →
            // RpReap → MarkerCleared → Durable2Done → Complete, finishing
            // the merge.
            var (scenario, provisional) = MakeStandardFixture();

            // Crash at Split (just after the new post-Begin durable barrier).
            TryRunWithFault(MergeJournalOrchestrator.Phase.Split,
                scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeJournal.Phases.Split,
                scenario.ActiveMergeJournal.Phase);
            Assert.Contains("begin", durableSaveCheckpoints);
            Assert.Contains("split", durableSaveCheckpoints);
            Assert.DoesNotContain("durable1", durableSaveCheckpoints);
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Equal(MergeState.NotCommitted, provisional.MergeState);

            MergeJournalOrchestrator.RunFinisher();

            // Drive-forward: marker + journal cleared, MergeState flipped.
            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.Equal(2, scenario.RecordingSupersedes.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]") && l.Contains("Completed from phase=Split"));
        }

        // ================================================================
        // Early no-op guards
        // ================================================================

        [Fact]
        public void RunMerge_NullMarker_ReturnsFalse()
        {
            MakeStandardFixture();
            bool ok = MergeJournalOrchestrator.RunMerge(null, null);
            Assert.False(ok);
        }

        [Fact]
        public void RunMerge_NullProvisional_ReturnsFalse()
        {
            var (scenario, _) = MakeStandardFixture();
            bool ok = MergeJournalOrchestrator.RunMerge(
                scenario.ActiveReFlySessionMarker, null);
            Assert.False(ok);
        }
    }
}
