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
