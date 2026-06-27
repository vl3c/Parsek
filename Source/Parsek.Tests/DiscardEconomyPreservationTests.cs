using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Discarding a recording must PRESERVE the irreversible live-gameplay economy the
    /// player earned during it (completed/failed/cancelled contracts, achieved milestones,
    /// collected science). A non-rewind discard has no quicksave to roll KSP back, so KSP
    /// keeps what it applied live; the ledger must keep it too or it diverges — re-listing
    /// a completed contract as active (dual-listing + re-completable duplicate reward + a
    /// silent fund drop). The fix re-homes those events to DIRECT ledger actions
    /// (recordingId cleared) before the discard purges/drops them.
    ///
    /// Covers <see cref="LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard"/>
    /// directly and the end-to-end DiscardPendingTree path.
    /// </summary>
    [Collection("Sequential")]
    public class DiscardEconomyPreservationTests : IDisposable
    {
        private readonly GameScenes originalScene;

        public DiscardEconomyPreservationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(true, true, true);
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            originalScene = HighLogic.LoadedScene;
        }

        public void Dispose()
        {
            HighLogic.LoadedScene = originalScene;
            KspStatePatcher.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameStateEvent TaggedEvent(
            GameStateEventType type, string key, double ut, string recordingId, string detail = "")
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = type,
                key = key,
                detail = detail,
                recordingId = recordingId ?? "",
            };
        }

        private static void AddTagged(GameStateEvent e) => GameStateStore.AddEvent(ref e);

        // --- Helper-level coverage -------------------------------------------------

        [Fact]
        public void Rehome_ContractCompletion_BecomesDirectLedgerAction()
        {
            AddTagged(TaggedEvent(GameStateEventType.ContractCompleted, "guid-c", 200.0, "rec-A"));

            LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(
                new HashSet<string> { "rec-A" }, "test");

            var a = Ledger.Actions.SingleOrDefault(x =>
                x.Type == GameActionType.ContractComplete && x.ContractId == "guid-c");
            Assert.NotNull(a);
            Assert.True(string.IsNullOrEmpty(a.RecordingId)); // direct (tag cleared)
        }

        [Fact]
        public void Rehome_Milestone_BecomesDirectLedgerAction()
        {
            AddTagged(TaggedEvent(GameStateEventType.MilestoneAchieved, "RecordsDistance", 150.0, "rec-A",
                GameStateRecorder.BuildMilestoneDetail(4800.0, 2.0f, 0.0)));

            LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(
                new HashSet<string> { "rec-A" }, "test");

            var a = Ledger.Actions.SingleOrDefault(x =>
                x.Type == GameActionType.MilestoneAchievement && x.MilestoneId == "RecordsDistance");
            Assert.NotNull(a);
            Assert.True(string.IsNullOrEmpty(a.RecordingId));
            Assert.Equal(4800f, a.MilestoneFundsAwarded);
        }

        [Fact]
        public void Rehome_ScienceSubject_BecomesDirectActionAndLeavesPendingClean()
        {
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = "sci-A",
                science = 12.5f,
                subjectMaxValue = 30f,
                captureUT = 120.0,
                reasonKey = "RecoveryAsh",
                recordingId = "rec-A",
            });
            // An unrelated subject for a different recording must be left untouched.
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = "sci-other",
                science = 5f,
                subjectMaxValue = 30f,
                captureUT = 130.0,
                reasonKey = "RecoveryAsh",
                recordingId = "rec-OTHER",
            });

            LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(
                new HashSet<string> { "rec-A" }, "test");

            var a = Ledger.Actions.SingleOrDefault(x =>
                x.Type == GameActionType.ScienceEarning && x.SubjectId == "sci-A");
            Assert.NotNull(a);
            Assert.True(string.IsNullOrEmpty(a.RecordingId));
            // Committed-subject cache mirrors the direct science.
            Assert.True(GameStateStore.TryGetCommittedSubjectScience("sci-A", out float committed));
            Assert.Equal(12.5f, committed);
            // The re-homed subject is removed from the pending list; the unrelated one stays.
            Assert.DoesNotContain(GameStateRecorder.PendingScienceSubjects, s => s.recordingId == "rec-A");
            Assert.Contains(GameStateRecorder.PendingScienceSubjects, s => s.subjectId == "sci-other");
        }

        [Fact]
        public void Rehome_IgnoresNonIrreversibleTaggedEventsAndUntaggedEvents()
        {
            // Tech is out of scope for this fix; funds/rep ride the terminal events.
            AddTagged(TaggedEvent(GameStateEventType.TechResearched, "node-x", 100.0, "rec-A"));
            AddTagged(TaggedEvent(GameStateEventType.FundsChanged, "Progression", 110.0, "rec-A"));
            // Untagged (KSC) completion must not be touched by an id-scoped re-home.
            AddTagged(TaggedEvent(GameStateEventType.ContractCompleted, "guid-untagged", 120.0, ""));

            LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(
                new HashSet<string> { "rec-A" }, "test");

            Assert.Empty(Ledger.Actions);
        }

        [Fact]
        public void Rehome_DedupsAgainstExistingLedgerAction()
        {
            // A ContractComplete already in the ledger for this contract+UT must not be
            // re-homed a second time.
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.ContractComplete,
                ContractId = "guid-dup",
                UT = 200.0,
            });
            AddTagged(TaggedEvent(GameStateEventType.ContractCompleted, "guid-dup", 200.0, "rec-A"));

            LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(
                new HashSet<string> { "rec-A" }, "test");

            Assert.Single(Ledger.Actions.Where(x =>
                x.Type == GameActionType.ContractComplete && x.ContractId == "guid-dup"));
        }

        [Fact]
        public void Rehome_ContractCompletion_PreservesRewardMagnitude_NoSilentFundDrop()
        {
            // The bug included a silent fund drop: the ledger never credited the purged
            // completion. The re-homed action must carry the contract's funds/rep/sci reward
            // so the next recalc credits it (the "no silent fund drop" guarantee, headless).
            AddTagged(TaggedEvent(GameStateEventType.ContractCompleted, "guid-reward", 200.0, "rec-A",
                "fundsReward=2080;repReward=1;sciReward=1"));

            LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(
                new HashSet<string> { "rec-A" }, "test");

            var a = Ledger.Actions.SingleOrDefault(x =>
                x.Type == GameActionType.ContractComplete && x.ContractId == "guid-reward");
            Assert.NotNull(a);
            Assert.Equal(2080f, a.FundsReward);
            Assert.Equal(1f, a.RepReward);
            Assert.Equal(1f, a.ScienceReward);
        }

        [Fact]
        public void Rehome_ScienceSubject_AlreadyCommitted_NotDoubleBanked()
        {
            // Pre-commit a science subject (committed cache populated), then re-home the SAME
            // subjectId on discard. CommitScienceSubject is subjectId-keyed and max-not-
            // additive, so re-homing an already-banked subject must not double the value.
            GameStateStore.CommitScienceActions(new List<GameAction>
            {
                new GameAction
                {
                    Type = GameActionType.ScienceEarning,
                    SubjectId = "sci-dup",
                    ScienceAwarded = 12.5f,
                    SubjectMaxValue = 30f,
                    UT = 100.0,
                },
            });

            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = "sci-dup",
                science = 12.5f,
                subjectMaxValue = 30f,
                captureUT = 120.0,
                reasonKey = "RecoveryAsh",
                recordingId = "rec-A",
            });

            LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(
                new HashSet<string> { "rec-A" }, "test");

            Assert.True(GameStateStore.TryGetCommittedSubjectScience("sci-dup", out float committed));
            Assert.Equal(12.5f, committed); // max-not-additive: still 12.5, never 25
        }

        [Fact]
        public void IsIrreversibleLiveGameplayEvent_OnlyTerminalContractsAndMilestones()
        {
            Assert.True(LedgerOrchestrator.IsIrreversibleLiveGameplayEvent(GameStateEventType.ContractCompleted));
            Assert.True(LedgerOrchestrator.IsIrreversibleLiveGameplayEvent(GameStateEventType.ContractFailed));
            Assert.True(LedgerOrchestrator.IsIrreversibleLiveGameplayEvent(GameStateEventType.ContractCancelled));
            Assert.True(LedgerOrchestrator.IsIrreversibleLiveGameplayEvent(GameStateEventType.MilestoneAchieved));
            Assert.False(LedgerOrchestrator.IsIrreversibleLiveGameplayEvent(GameStateEventType.ContractAccepted));
            Assert.False(LedgerOrchestrator.IsIrreversibleLiveGameplayEvent(GameStateEventType.TechResearched));
            Assert.False(LedgerOrchestrator.IsIrreversibleLiveGameplayEvent(GameStateEventType.FundsChanged));
        }

        // --- End-to-end: the actual desync is gone ---------------------------------

        [Fact]
        public void DiscardPendingTree_CompletedContract_NotReListedActive_AfterWalk()
        {
            // Accept is a direct (KSC) ledger action: contract starts ACTIVE in the ledger.
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.ContractAccept,
                ContractId = "guid-e2e",
                UT = 10.0,
                DeadlineUT = float.NaN,
            });

            // Completion captured DURING the recorded mission (tagged), then discarded.
            GameStateRecorder.TagResolverForTesting = () => "rec-A";
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            var complete = TaggedEvent(GameStateEventType.ContractCompleted, "guid-e2e", 200.0, "");
            GameStateRecorder.Emit(ref complete, "flight"); // Emit stamps recordingId = rec-A

            var tree = new RecordingTree
            {
                Id = "tree-e2e",
                TreeName = "e2e",
                RootRecordingId = "rec-A",
                ActiveRecordingId = "rec-A",
            };
            tree.Recordings["rec-A"] = new Recording { RecordingId = "rec-A", TreeId = "tree-e2e" };
            RecordingStore.StashPendingTree(tree);

            RecordingStore.DiscardPendingTree();

            // Walk the resulting ledger through ContractsModule: before the fix the contract
            // would be ACTIVE again (accept survives, completion gone); after the fix the
            // re-homed direct completion keeps it terminal/credited and NOT active.
            var contracts = new ContractsModule();
            contracts.Reset();
            var sorted = Ledger.Actions.OrderBy(x => x.UT).ToList();
            contracts.PrePass(sorted);
            foreach (var act in sorted)
                contracts.ProcessAction(act);

            Assert.DoesNotContain("guid-e2e", contracts.GetActiveContractIds());
            Assert.True(contracts.IsContractCredited("guid-e2e"));
        }

        [Fact]
        public void DiscardPendingTree_AbandonPath_DoesNotRehome()
        {
            // The abandon path (quickload-backwards / revert / stale-pending-from-another-
            // save) passes preserveIrreversibleLiveGameplay=false: KSP's economy is being
            // rolled back (or belongs to a different save), so the completion must NOT be
            // re-homed — doing so would credit economy KSP no longer reflects.
            GameStateRecorder.TagResolverForTesting = () => "rec-A";
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            var complete = TaggedEvent(GameStateEventType.ContractCompleted, "guid-abandon", 200.0, "");
            GameStateRecorder.Emit(ref complete, "flight"); // Emit stamps recordingId = rec-A

            var tree = new RecordingTree
            {
                Id = "tree-ab",
                TreeName = "ab",
                RootRecordingId = "rec-A",
                ActiveRecordingId = "rec-A",
            };
            tree.Recordings["rec-A"] = new Recording { RecordingId = "rec-A", TreeId = "tree-ab" };
            RecordingStore.StashPendingTree(tree);

            RecordingStore.DiscardPendingTree(preserveIrreversibleLiveGameplay: false);

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.ContractComplete && a.ContractId == "guid-abandon");
        }

        // --- Scoped pending-science clear: don't drop another recording's science ---

        private static PendingScienceSubject Subject(
            string subjectId, string recordingId, double captureUT, float science = 5f)
        {
            return new PendingScienceSubject
            {
                subjectId = subjectId,
                science = science,
                subjectMaxValue = science + 10f,
                captureUT = captureUT,
                reasonKey = "RecoveryAsh",
                recordingId = recordingId,
            };
        }

        [Fact]
        public void RemovePendingScienceSubjectsForRecordings_RemovesScoped_LeavesUnrelatedAndUntagged()
        {
            GameStateRecorder.PendingScienceSubjects.Add(Subject("sci-A", "rec-A", 100.0));
            GameStateRecorder.PendingScienceSubjects.Add(Subject("sci-other", "rec-OTHER", 110.0));
            GameStateRecorder.PendingScienceSubjects.Add(Subject("sci-untagged", "", 120.0));

            int removed = LedgerOrchestrator.RemovePendingScienceSubjectsForRecordings(
                new HashSet<string> { "rec-A" }, "test");

            Assert.Equal(1, removed);
            Assert.DoesNotContain(GameStateRecorder.PendingScienceSubjects, s => s.subjectId == "sci-A");
            // A DIFFERENT recording's uncommitted science and untagged KSC captures are kept.
            Assert.Contains(GameStateRecorder.PendingScienceSubjects, s => s.subjectId == "sci-other");
            Assert.Contains(GameStateRecorder.PendingScienceSubjects, s => s.subjectId == "sci-untagged");
        }

        [Fact]
        public void RemovePendingScienceSubjectsForRecordings_SkipsCommittedIds()
        {
            // A committed recording's science is already a ledger action; the scoped removal
            // must not strip it from the pending list even when its id is in the set.
            RecordingStore.AddCommittedInternal(new Recording { RecordingId = "rec-committed" });
            GameStateRecorder.PendingScienceSubjects.Add(Subject("sci-committed", "rec-committed", 100.0));

            int removed = LedgerOrchestrator.RemovePendingScienceSubjectsForRecordings(
                new HashSet<string> { "rec-committed" }, "test");

            Assert.Equal(0, removed);
            Assert.Contains(GameStateRecorder.PendingScienceSubjects, s => s.subjectId == "sci-committed");
        }

        [Fact]
        public void DiscardPendingTree_OtherRecordingUncommittedScience_Survives()
        {
            // The discarded tree owns rec-A; a DIFFERENT live recording rec-OTHER has
            // uncommitted (tagged) science still pending. The genuine discard must drop only
            // rec-A's pending science (re-homed direct), PRESERVING rec-OTHER's — a blanket
            // Clear() here silently lost the other recording's uncommitted science.
            GameStateRecorder.PendingScienceSubjects.Add(Subject("sci-A", "rec-A", 150.0, science: 8f));
            GameStateRecorder.PendingScienceSubjects.Add(Subject("sci-other", "rec-OTHER", 160.0, science: 3f));

            var tree = new RecordingTree
            {
                Id = "tree-x",
                TreeName = "x",
                RootRecordingId = "rec-A",
                ActiveRecordingId = "rec-A",
            };
            tree.Recordings["rec-A"] = new Recording { RecordingId = "rec-A", TreeId = "tree-x" };
            RecordingStore.StashPendingTree(tree);

            RecordingStore.DiscardPendingTree();

            // rec-A's pending science is gone, re-homed as a DIRECT science action.
            Assert.DoesNotContain(GameStateRecorder.PendingScienceSubjects, s => s.recordingId == "rec-A");
            var rehomed = Ledger.Actions.SingleOrDefault(a =>
                a.Type == GameActionType.ScienceEarning && a.SubjectId == "sci-A");
            Assert.NotNull(rehomed);
            Assert.True(string.IsNullOrEmpty(rehomed.RecordingId));
            // The OTHER live recording's uncommitted science SURVIVES.
            Assert.Contains(GameStateRecorder.PendingScienceSubjects, s => s.subjectId == "sci-other");
        }

        [Fact]
        public void DiscardPendingTree_AbandonPath_WipesAllPendingScience()
        {
            // The abandon path (quickload-backwards / revert / cross-save) rolls KSP's
            // economy back, so ALL pending science is the discarded future — wipe wholesale.
            GameStateRecorder.PendingScienceSubjects.Add(Subject("sci-A", "rec-A", 150.0));
            GameStateRecorder.PendingScienceSubjects.Add(Subject("sci-other", "rec-OTHER", 160.0));

            var tree = new RecordingTree
            {
                Id = "tree-ab",
                TreeName = "ab",
                RootRecordingId = "rec-A",
                ActiveRecordingId = "rec-A",
            };
            tree.Recordings["rec-A"] = new Recording { RecordingId = "rec-A", TreeId = "tree-ab" };
            RecordingStore.StashPendingTree(tree);

            RecordingStore.DiscardPendingTree(preserveIrreversibleLiveGameplay: false);

            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
        }

        // --- CommitTree duplicate-skip: scope the pending-science clear too ---------
        // The two duplicate-skip early-returns ("tree already committed") in
        // RecordingStore.CommitTree used a blanket PendingScienceSubjects.Clear(). Since
        // CommitTree does NOT consume science itself (the callers follow it with
        // NotifyLedgerTreeCommitted, which is scoped per-recording), a duplicate-skip's
        // blanket clear left NotifyLedgerTreeCommitted running on an empty list and
        // silently dropped a DIFFERENT live recording's tagged uncommitted science. These
        // mirror the discard-core survival tests above for that degenerate re-commit path.

        private static Recording CommitRec(string id, string treeId)
        {
            return new Recording { RecordingId = id, VesselName = id, TreeId = treeId };
        }

        [Fact]
        public void CommitTree_DuplicateSkipStrictReject_DropsOwnUncommitted_PreservesOtherAndCommitted()
        {
            ParsekScenario.ResetInstanceForTesting();

            // Existing committed tree: 1 committed recording + 1 BP.
            var head = CommitRec("rec-head", "tree-dup");
            var existing = new RecordingTree
            {
                Id = "tree-dup",
                TreeName = "dup",
                RootRecordingId = "rec-head",
                ActiveRecordingId = "rec-head",
                BranchPoints = new List<BranchPoint> { new BranchPoint { Id = "bp-keep", Type = BranchPointType.Undock } },
            };
            existing.AddOrReplaceRecording(head);
            RecordingStore.AddCommittedInternal(head);
            RecordingStore.AddCommittedTreeForTesting(existing);

            // Incoming duplicate (same id): carries an extra UNCOMMITTED fork but is
            // missing the existing BP, so the strict gate rejects it -> duplicate-skip.
            var incoming = new RecordingTree
            {
                Id = "tree-dup",
                TreeName = "dup",
                RootRecordingId = "rec-head",
                ActiveRecordingId = "rec-fork",
                BranchPoints = new List<BranchPoint>(),
            };
            incoming.AddOrReplaceRecording(CommitRec("rec-head", "tree-dup"));
            incoming.AddOrReplaceRecording(CommitRec("rec-fork", "tree-dup"));

            // Pending science: the duplicate tree's own uncommitted fork, its committed
            // head, and a DIFFERENT live recording.
            GameStateRecorder.PendingScienceSubjects.Add(Subject("sci-fork", "rec-fork", 100.0));
            GameStateRecorder.PendingScienceSubjects.Add(Subject("sci-head", "rec-head", 110.0));
            GameStateRecorder.PendingScienceSubjects.Add(Subject("sci-other", "rec-OTHER", 120.0));

            RecordingStore.CommitTree(incoming);

            // Only the duplicate tree's own UNCOMMITTED subject is dropped.
            Assert.DoesNotContain(GameStateRecorder.PendingScienceSubjects, s => s.subjectId == "sci-fork");
            // The committed-id subject is skipped (committed science is already a ledger
            // action), and a DIFFERENT live recording's uncommitted science SURVIVES.
            // A blanket Clear() here silently lost both.
            Assert.Contains(GameStateRecorder.PendingScienceSubjects, s => s.subjectId == "sci-head");
            Assert.Contains(GameStateRecorder.PendingScienceSubjects, s => s.subjectId == "sci-other");
        }

        [Fact]
        public void CommitTree_DuplicateSkipReferenceEqual_DropsOwnUncommitted_PreservesOther()
        {
            ParsekScenario.ResetInstanceForTesting();

            // Same tree object already in committedTrees -> the ReferenceEquals branch.
            var tree = new RecordingTree
            {
                Id = "tree-ref",
                TreeName = "ref",
                RootRecordingId = "rec-r1",
                ActiveRecordingId = "rec-r1",
            };
            tree.AddOrReplaceRecording(CommitRec("rec-r1", "tree-ref"));
            RecordingStore.AddCommittedTreeForTesting(tree);

            GameStateRecorder.PendingScienceSubjects.Add(Subject("sci-r1", "rec-r1", 100.0));
            GameStateRecorder.PendingScienceSubjects.Add(Subject("sci-other", "rec-OTHER", 110.0));

            RecordingStore.CommitTree(tree);

            // This tree's own pending subject is dropped scoped...
            Assert.DoesNotContain(GameStateRecorder.PendingScienceSubjects, s => s.subjectId == "sci-r1");
            // ...while a DIFFERENT live recording's uncommitted science SURVIVES (a blanket
            // Clear() here wiped the whole list).
            Assert.Contains(GameStateRecorder.PendingScienceSubjects, s => s.subjectId == "sci-other");
        }
    }
}
