using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RESTORE-ATTEMPT-RECORD-FOLDS-INTO-HISTORY (harness run
    /// 2026-11-15_0704_SE-1-editor-round-trip): a committed-tree restore attempt resumes on a
    /// recording that already carries a stored world-record event of the same node (the
    /// fixture's splashdown RecordsDistance events, which the load-time optimizer split moves
    /// onto the chain tail the attempt now resumes on). A live award of that node must start
    /// the attempt's own seed (past the recording's cutoff) and never fold into the stored
    /// history event: the discard re-home reads only post-cutoff events, so a folded award is
    /// lost with the discard, the recalc patches stock back below the thresholds and stock
    /// pays them again as fresh untagged rows.
    /// </summary>
    [Collection("Sequential")]
    public class RestoreAttemptRecordCoalesceTests : IDisposable
    {
        private const string TreeId = "tree-restore-clone";
        private const string TailId = "rec-tail";
        private const double CutoffUT = 1276.7;
        private const double HistoryUT = 1276.4235614006236;

        private readonly List<string> logLines = new List<string>();

        public RestoreAttemptRecordCoalesceTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateRecorder.PendingMilestoneEventById.Clear();
            GameStateRecorder.IsReplayingActions = false;
        }

        public void Dispose()
        {
            GameStateRecorder.TagResolverForTesting = null;
            RecordingStore.ClearCommittedTreeRestoreAttempt("test-teardown");
            GameStateRecorder.PendingMilestoneEventById.Clear();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo f = target.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(f);
            f.SetValue(target, value);
        }

        private static void ArmAttemptOnTail()
        {
            var rec = new Recording
            {
                RecordingId = TailId,
                TreeId = TreeId,
                VesselName = "Kerbal X",
                ExplicitStartUT = 1000.0,
                ExplicitEndUT = CutoffUT,
            };
            var tree = new RecordingTree { Id = TreeId, TreeName = "Kerbal X" };
            tree.AddOrReplaceRecording(rec);
            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(tree, "test-arm");
        }

        // The fixture's two stored splashdown events (events.pgse of
        // kerbin-splashdown-recorded): a pre-band threshold event and stock's completion,
        // both at or before the recording's end, so committed history.
        private static void AddHistoryEvents()
        {
            var award = new GameStateEvent
            {
                ut = HistoryUT,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "RecordsDistance",
                detail = "funds=9600;rep=2;sci=1",
                recordingId = TailId,
            };
            GameStateStore.AddEvent(ref award);
            var completion = new GameStateEvent
            {
                ut = HistoryUT,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "RecordsDistance",
                detail = "funds=0;rep=0;sci=0",
                recordingId = TailId,
            };
            GameStateStore.AddEvent(ref completion);
        }

        // Stock's iterateVessels on the resumed vessel: thresholds 4 and 5, then Complete().
        private static void FireLiveRecordAwards()
        {
            var node = new KSPAchievements.RecordsDistance();
            SetField(node, "rewardInterval", 4);
            GameStateRecorder.EmitStandaloneProgressReward(node, 0.0, 0f, 0.0, ut: 1276.75);
            SetField(node, "rewardInterval", 5);
            GameStateRecorder.EmitStandaloneProgressReward(node, 0.0, 0f, 0.0, ut: 1276.75);
            GameStateRecorder.RegisterPendingMilestoneEvent("RecordsDistance", 1276.75);
        }

        // catches: the coalescer folding a live restore-attempt award into the stored history
        // event of the same node and scope (it would mutate committed history and leave the
        // attempt with no threshold event of its own).
        [Fact]
        public void LiveAward_InARestoreAttempt_StartsItsOwnSeed_AndLeavesHistoryUntouched()
        {
            LedgerOrchestrator.Initialize();
            ArmAttemptOnTail();
            AddHistoryEvents();
            GameStateRecorder.TagResolverForTesting = () => TailId;

            FireLiveRecordAwards();

            var records = GameStateStore.Events
                .Where(e => e.eventType == GameStateEventType.MilestoneAchieved && e.key == "RecordsDistance")
                .ToList();
            var history = records.Where(e => e.ut == HistoryUT).ToList();
            Assert.Equal(2, history.Count);
            Assert.Equal("funds=9600;rep=2;sci=1", history[0].detail);
            Assert.Equal("funds=0;rep=0;sci=0", history[1].detail);

            var live = records.Where(e => e.ut > CutoffUT).ToList();
            Assert.Equal(2, live.Count);
            Assert.Contains("thresholds=2", live[0].detail);
            Assert.Contains(";band=4:0:0:0,5:0:0:0", live[0].detail);
            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("TryCoalesceWorldRecordReward")
                && l.Contains("restore-attempt history")
                && l.Contains("scope='" + TailId + "'"));
        }

        // catches: the discard of the attempt dropping the awards it made. Both the threshold
        // seed and the completion must be re-homed as direct rows (the SE-1 PASS shape:
        // "re-homed 2 direct action(s) ... milestone=2"), so the post-discard recalc keeps
        // stock's band at the thresholds it paid and stock never pays them again.
        [Fact]
        public void DiscardOfTheAttempt_ReHomesTheThresholdSeedAndTheCompletion()
        {
            LedgerOrchestrator.Initialize();
            ArmAttemptOnTail();
            AddHistoryEvents();
            GameStateRecorder.TagResolverForTesting = () => TailId;
            FireLiveRecordAwards();
            GameStateRecorder.TagResolverForTesting = null;

            int before = Ledger.Actions.Count;
            RecordingStore.DiscardCommittedTreeRestoreAttempt("test-discard",
                LedgerOrchestrator.PreserveIrreversibleRestoreAttemptTailsOnDiscard);

            var rehomed = Ledger.Actions.Skip(before)
                .Where(a => a.Type == GameActionType.MilestoneAchievement && a.MilestoneId == "RecordsDistance")
                .ToList();
            Assert.Equal(2, rehomed.Count);
            Assert.Equal(2, rehomed.Sum(a => a.MilestoneRecordThresholds));
            Assert.Contains(logLines, l => l.Contains("PreserveIrreversibleRestoreAttemptTailsOnDiscard (test-discard)")
                && l.Contains("re-homed 2 direct action(s)") && l.Contains("milestone=2"));
            // History stays in the store; the attempt's own tail is purged.
            Assert.Equal(2, GameStateStore.Events.Count(e => e.key == "RecordsDistance"));
            Assert.All(GameStateStore.Events.Where(e => e.key == "RecordsDistance"),
                e => Assert.Equal(HistoryUT, e.ut));
        }

        private const double HistoryRowUT = 1276.68;

        // A committed history event AND its committed ledger row of the same scope, 0.02 s
        // before the cutoff: inside the 0.1 s row-match window of a live seed just after it.
        private static GameAction AddHistoryEventAndRow()
        {
            var evt = new GameStateEvent
            {
                ut = HistoryRowUT,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "RecordsDistance",
                detail = GameStateRecorder.BuildMilestoneDetail(100.0, 0f, 0.0, 1, "3:100:0:0"),
                recordingId = TailId,
            };
            GameStateStore.AddEvent(ref evt);
            var row = new GameAction
            {
                UT = HistoryRowUT,
                Type = GameActionType.MilestoneAchievement,
                RecordingId = TailId,
                MilestoneId = "RecordsDistance",
                MilestoneFundsAwarded = 100f,
                MilestoneRecordThresholds = 1,
                MilestoneRecordBand = new List<RecordThresholdCredit> { new RecordThresholdCredit(3, 100f, 0f, 0f) },
                // The row its event was filed as: same capture number, so a commit's dedup
                // recognises the event as already booked.
                CaptureSequence = evt.captureSequence,
            };
            Ledger.AddAction(row);
            return row;
        }

        // Stock pays threshold 4 (200) and 5 (300) on the resumed vessel just after the cutoff.
        private static void FirePayingAwards()
        {
            var node = new KSPAchievements.RecordsDistance();
            SetField(node, "rewardInterval", 4);
            GameStateRecorder.EmitStandaloneProgressReward(node, 200.0, 0f, 0.0, ut: 1276.72);
            SetField(node, "rewardInterval", 5);
            GameStateRecorder.EmitStandaloneProgressReward(node, 300.0, 0f, 0.0, ut: 1276.74);
        }

        private static float BookedRecordsDistanceFunds()
        {
            return Ledger.Actions
                .Where(a => a.Type == GameActionType.MilestoneAchievement && a.MilestoneId == "RecordsDistance")
                .Sum(a => a.MilestoneFundsAwarded);
        }

        // catches: the coalescer adding a live attempt award to a committed HISTORY ledger row
        // of the same scope inside the 0.1 s row window, while the attempt's own seed event
        // also holds it: the discard re-homes the seed and the inflated history row keeps the
        // same money (900 booked for 600 paid).
        [Fact]
        public void Discard_APayingAttemptNextToAHistoryRow_BooksWhatStockPaidOnce()
        {
            LedgerOrchestrator.Initialize();
            ArmAttemptOnTail();
            GameAction history = AddHistoryEventAndRow();
            GameStateRecorder.TagResolverForTesting = () => TailId;
            FirePayingAwards();
            GameStateRecorder.TagResolverForTesting = null;

            Assert.Equal(100f, history.MilestoneFundsAwarded);
            Assert.Equal(1, history.MilestoneRecordThresholds);

            RecordingStore.DiscardCommittedTreeRestoreAttempt("test-discard",
                LedgerOrchestrator.PreserveIrreversibleRestoreAttemptTailsOnDiscard);

            Assert.Equal(100f, history.MilestoneFundsAwarded);
            Assert.Equal(1, history.MilestoneRecordThresholds);
            var rehomed = Ledger.Actions.Single(a => a.Type == GameActionType.MilestoneAchievement
                && a.MilestoneId == "RecordsDistance" && !ReferenceEquals(a, history));
            Assert.Equal(500f, rehomed.MilestoneFundsAwarded);
            Assert.Equal(2, rehomed.MilestoneRecordThresholds);
            Assert.Equal(600f, BookedRecordsDistanceFunds());
        }

        // catches: the same double booking on the other end of the attempt: committing it
        // files the seed event as its own row beside the history row.
        [Fact]
        public void Commit_APayingAttemptNextToAHistoryRow_BooksWhatStockPaidOnce()
        {
            LedgerOrchestrator.Initialize();
            ArmAttemptOnTail();
            GameAction history = AddHistoryEventAndRow();
            GameStateRecorder.TagResolverForTesting = () => TailId;
            FirePayingAwards();
            GameStateRecorder.TagResolverForTesting = null;

            bool scienceAdded = false;
            LedgerOrchestrator.OnRecordingCommitted(
                TailId, 1000.0, 1276.8, new List<PendingScienceSubject>(), ref scienceAdded);

            Assert.Equal(100f, history.MilestoneFundsAwarded);
            Assert.Equal(1, history.MilestoneRecordThresholds);
            var attemptRow = Ledger.Actions.Single(a => a.Type == GameActionType.MilestoneAchievement
                && a.MilestoneId == "RecordsDistance" && !ReferenceEquals(a, history));
            Assert.Equal(500f, attemptRow.MilestoneFundsAwarded);
            Assert.Equal(600f, BookedRecordsDistanceFunds());
        }

        // catches: an off-by-one on the history boundary (a `>=` in either loop): an event or
        // row exactly AT the cutoff is history, one just after it is the attempt's own.
        [Fact]
        public void Boundary_AnEventAtTheCutoffIsHistory_OneJustAfterIsTheAttemptsOwn()
        {
            LedgerOrchestrator.Initialize();
            ArmAttemptOnTail();
            GameStateRecorder.TagResolverForTesting = () => TailId;
            var atCutoff = new GameStateEvent
            {
                ut = CutoffUT,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "RecordsDistance",
                detail = "funds=0;rep=0;sci=0",
                recordingId = TailId,
            };
            GameStateStore.AddEvent(ref atCutoff);
            Assert.False(GameStateRecorder.TryCoalesceWorldRecordReward("RecordsDistance", 100.0, 0f, 0.0, 4));
            Assert.Equal("funds=0;rep=0;sci=0",
                GameStateStore.Events.Single(e => e.key == "RecordsDistance").detail);

            var justAfter = new GameStateEvent
            {
                ut = CutoffUT + 0.001,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "RecordsDistance",
                detail = "funds=0;rep=0;sci=0",
                recordingId = TailId,
            };
            GameStateStore.AddEvent(ref justAfter);
            // A committed row AT the cutoff sits inside the 0.1 s window of that seed: history.
            var rowAtCutoff = new GameAction
            {
                UT = CutoffUT,
                Type = GameActionType.MilestoneAchievement,
                RecordingId = TailId,
                MilestoneId = "RecordsDistance",
                MilestoneFundsAwarded = 7f,
            };
            Ledger.AddAction(rowAtCutoff);
            Assert.True(GameStateRecorder.TryCoalesceWorldRecordReward("RecordsDistance", 100.0, 0f, 0.0, 4));
            Assert.Contains("funds=100", GameStateStore.Events.Single(e => e.ut > CutoffUT).detail);
            Assert.Equal(7f, rowAtCutoff.MilestoneFundsAwarded);

            // The attempt's OWN row (after the cutoff) is still found and folded.
            var ownRow = new GameAction
            {
                UT = CutoffUT + 0.001,
                Type = GameActionType.MilestoneAchievement,
                RecordingId = TailId,
                MilestoneId = "RecordsDistance",
                MilestoneFundsAwarded = 100f,
            };
            Ledger.AddAction(ownRow);
            Assert.True(GameStateRecorder.TryCoalesceWorldRecordReward("RecordsDistance", 50.0, 0f, 0.0, 5));
            Assert.Equal(150f, ownRow.MilestoneFundsAwarded);
            Assert.Equal(7f, rowAtCutoff.MilestoneFundsAwarded);
        }

        // catches: the history guard over-reaching: a second award inside the attempt still
        // folds into the attempt's OWN post-cutoff seed (bounded rows), and with no attempt
        // armed the scope rule is unchanged.
        [Fact]
        public void AttemptsOwnSeed_StillCoalesces_AndNoAttemptKeepsTheOldScopeRule()
        {
            LedgerOrchestrator.Initialize();
            ArmAttemptOnTail();
            GameStateRecorder.TagResolverForTesting = () => TailId;
            var own = new GameStateEvent
            {
                ut = 1276.8,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "RecordsDistance",
                detail = "funds=0;rep=0;sci=0",
                recordingId = TailId,
            };
            GameStateStore.AddEvent(ref own);
            Assert.True(GameStateRecorder.TryCoalesceWorldRecordReward("RecordsDistance", 100.0, 0f, 0.0, 4));

            RecordingStore.ClearCommittedTreeRestoreAttempt("test-clear");
            var old = new GameStateEvent
            {
                ut = 50.0,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "RecordsSpeed",
                detail = "funds=0;rep=0;sci=0",
                recordingId = TailId,
            };
            GameStateStore.AddEvent(ref old);
            Assert.True(GameStateRecorder.TryCoalesceWorldRecordReward("RecordsSpeed", 100.0, 0f, 0.0, 2));
        }
    }
}
