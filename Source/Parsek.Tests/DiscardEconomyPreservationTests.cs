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
    }
}
