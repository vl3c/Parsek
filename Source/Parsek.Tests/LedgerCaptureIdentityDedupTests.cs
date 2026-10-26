using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// LEDGER-EVENT-IDENTITY step 3 (design 3.15, "Row duplicates collapse on capture
    /// identity"): the ledger dedups key on the capture, never on economic keys alone. A row
    /// duplicate (one capture filed twice) still collapses; an event duplicate (two captures
    /// with equal type, key and moment) keeps both rows for the replay to decide; a pair
    /// where either side names no capture (an older save) keeps the economic match.
    /// Covers <see cref="LedgerOrchestrator.DeduplicateAgainst"/>, the discard re-home that
    /// runs through it, the save recovery migration's
    /// <see cref="LedgerOrchestrator.LedgerHasMatchingAction(GameStateEvent)"/>, and
    /// <see cref="GameStateStore.EventIdentityMatches"/>.
    /// </summary>
    [Collection("Sequential")]
    public class LedgerCaptureIdentityDedupTests : IDisposable
    {
        private const string Node = "RecordsSpeed";
        private readonly List<string> logLines = new List<string>();

        public LedgerCaptureIdentityDedupTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(true, true, true);
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            KspStatePatcher.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Helpers
        // ================================================================

        private static GameStateEvent Captured(
            GameStateEventType type, string key, double ut, string recordingId, string detail = "")
        {
            var e = new GameStateEvent
            {
                ut = ut,
                eventType = type,
                key = key,
                detail = detail,
                recordingId = recordingId ?? "",
            };
            GameStateStore.AddEvent(ref e);
            return e;
        }

        private static GameStateEvent RecordCrossing(double ut, string recordingId, double funds)
        {
            return Captured(GameStateEventType.MilestoneAchieved, Node, ut, recordingId,
                GameStateRecorder.BuildMilestoneDetail(funds, 1f, 0.0));
        }

        private int CountLines(string fragment)
        {
            return logLines.Count(l => l.Contains("[LedgerOrchestrator]") && l.Contains(fragment));
        }

        // ================================================================
        // The converter names the capture
        // ================================================================

        [Fact]
        public void ConvertEvent_ACapturedEvent_NamesItsCapture_AnUnsequencedOneDoesNot()
        {
            var captured = RecordCrossing(100.0, "rec-A", 1000.0);
            var row = GameStateEventConverter.ConvertEvent(captured, "rec-A");
            Assert.True(captured.captureSequence > 0);
            Assert.Equal(captured.captureSequence, row.CaptureSequence);
            Assert.True(row.HasEventCaptureIdentity);
            Assert.True(LedgerOrchestrator.IsEventCaptureIdentified(row));

            // An event an older build captured has no number: its row is Unsequenced and
            // names no capture, so it keeps the economic dedup.
            var old = new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.MilestoneAchieved,
                key = Node,
                detail = GameStateRecorder.BuildMilestoneDetail(1000.0, 1f, 0.0),
            };
            var oldRow = GameStateEventConverter.ConvertEvent(old, "rec-A");
            Assert.Equal(CaptureSequence.Unsequenced, oldRow.CaptureSequence);
            Assert.False(oldRow.HasEventCaptureIdentity);
            Assert.False(LedgerOrchestrator.IsEventCaptureIdentified(oldRow));
        }

        [Fact]
        public void EventCaptureIdentity_IsNotSerialized()
        {
            var row = GameStateEventConverter.ConvertEvent(RecordCrossing(100.0, "rec-A", 1000.0), "rec-A");
            var parent = new ConfigNode("LEDGER");
            row.SerializeInto(parent);
            var restored = GameAction.DeserializeFrom(parent.GetNode("GAME_ACTION"));
            Assert.Equal(row.CaptureSequence, restored.CaptureSequence);
            Assert.False(restored.HasEventCaptureIdentity);
        }

        // ================================================================
        // DeduplicateAgainst
        // ================================================================

        [Fact]
        public void TwoCaptures_SameKeyAndMoment_BothSurvive()
        {
            // Two recordings of one tree cross the same record node 0.03 s apart: stock pays
            // two thresholds. The economic key (type + MilestoneId + 0.1 s) is equal, the
            // captures are not, so the second row is an event duplicate and stays.
            var first = GameStateEventConverter.ConvertEvent(RecordCrossing(100.0, "rec-parent", 1000.0), "rec-parent");
            var second = GameStateEventConverter.ConvertEvent(RecordCrossing(100.03, "rec-child", 2000.0), "rec-child");
            Ledger.AddAction(first);

            Assert.True(LedgerOrchestrator.LegacyEconomicKeyMatches(first, second));

            var kept = LedgerOrchestrator.DeduplicateAgainstLedger(new List<GameAction> { second });

            Assert.Single(kept);
            Assert.Same(second, kept[0]);
            Assert.Equal(1, CountLines("as a second event"));
            Assert.Equal(1, CountLines("eventDuplicatesKept=1"));
        }

        [Fact]
        public void AReFileOfOneCapture_StillCollapses()
        {
            // A re-commit converts the same stored event again: same capture, one row.
            var evt = RecordCrossing(100.0, "rec-A", 1000.0);
            Ledger.AddAction(GameStateEventConverter.ConvertEvent(evt, "rec-A"));
            var again = GameStateEventConverter.ConvertEvent(evt, "rec-A");

            var kept = LedgerOrchestrator.DeduplicateAgainstLedger(new List<GameAction> { again });

            Assert.Empty(kept);
            Assert.Equal(1, CountLines("eventCaptureMatches=1"));
            Assert.Equal(0, CountLines("as a second event"));
        }

        [Fact]
        public void AReFileOfOneCapture_CollapsesWhateverItsMoment()
        {
            // The capture decides, not the 0.1 s window: a re-file of the same capture whose
            // row moved (a retag keeps the number) is still the same row.
            var evt = RecordCrossing(100.0, "rec-A", 1000.0);
            var filed = GameStateEventConverter.ConvertEvent(evt, "rec-A");
            filed.UT = 250.0;
            Ledger.AddAction(filed);

            var kept = LedgerOrchestrator.DeduplicateAgainstLedger(
                new List<GameAction> { GameStateEventConverter.ConvertEvent(evt, "rec-A") });

            Assert.Empty(kept);
        }

        [Fact]
        public void ARowWithNoCapture_KeepsTheEconomicMatch()
        {
            // An older save's row carries no capture number: the candidate (which names one)
            // falls back to type + UT + key and collapses, as before.
            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = Node,
                CaptureSequence = CaptureSequence.Unsequenced,
            });
            var candidate = GameStateEventConverter.ConvertEvent(RecordCrossing(100.05, "rec-A", 1000.0), "rec-A");
            Assert.True(candidate.HasEventCaptureIdentity);

            var kept = LedgerOrchestrator.DeduplicateAgainstLedger(new List<GameAction> { candidate });

            Assert.Empty(kept);
            Assert.Equal(1, CountLines("legacyKeyMatches=1"));
        }

        [Fact]
        public void ACandidateWithNoCapture_KeepsTheEconomicMatch()
        {
            // A candidate built without an event (or from an event an older build captured)
            // names no capture: the economic match decides even against a numbered row.
            Ledger.AddAction(GameStateEventConverter.ConvertEvent(RecordCrossing(100.0, "rec-A", 1000.0), "rec-A"));
            var candidate = new GameAction
            {
                UT = 100.05,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = Node,
            };

            var kept = LedgerOrchestrator.DeduplicateAgainstLedger(new List<GameAction> { candidate });

            Assert.Empty(kept);
            Assert.Equal(1, CountLines("legacyKeyMatches=1"));
        }

        [Fact]
        public void AnotherCapture_WithAnotherKey_IsNotReportedAsAnEventDuplicate()
        {
            Ledger.AddAction(GameStateEventConverter.ConvertEvent(RecordCrossing(100.0, "rec-A", 1000.0), "rec-A"));
            var other = GameStateEventConverter.ConvertEvent(
                Captured(GameStateEventType.MilestoneAchieved, "RecordsAltitude", 100.0, "rec-A",
                    GameStateRecorder.BuildMilestoneDetail(500.0, 1f, 0.0)), "rec-A");

            var kept = LedgerOrchestrator.DeduplicateAgainstLedger(new List<GameAction> { other });

            Assert.Single(kept);
            Assert.Equal(0, CountLines("as a second event"));
            Assert.Equal(0, CountLines("DeduplicateAgainstLedger: removed"));
        }

        [Fact]
        public void CommitPreview_SameTreeTwoCaptures_BothSurvive()
        {
            // The commit preview dedups each recording against the rows the same tree commit
            // filed before it: the explicit-list form keys on the capture the same way.
            var parentRow = GameStateEventConverter.ConvertEvent(RecordCrossing(100.0, "rec-parent", 1000.0), "rec-parent");
            var childRow = GameStateEventConverter.ConvertEvent(RecordCrossing(100.0, "rec-child", 2000.0), "rec-child");

            var kept = LedgerOrchestrator.DeduplicateAgainst(
                new List<GameAction> { parentRow }, new List<GameAction> { childRow });

            Assert.Single(kept);
            Assert.Empty(LedgerOrchestrator.DeduplicateAgainst(
                new List<GameAction> { parentRow },
                new List<GameAction> { GameStateEventConverter.ConvertEvent(
                    GameStateStore.Events.First(e => e.recordingId == "rec-parent"), "rec-parent") }));
        }

        // ================================================================
        // The discard re-home runs through the same dedup
        // ================================================================

        [Fact]
        public void DiscardReHome_ASecondCaptureOfAMilestone_StaysNextToTheFirst()
        {
            // The ledger already holds a direct row for one crossing; a discarded flight's own
            // crossing of the same node at the same instant is another payment and is kept.
            var direct = RecordCrossing(150.0, "", 1000.0);
            Ledger.AddAction(GameStateEventConverter.ConvertEvent(direct, null));
            RecordCrossing(150.0, "rec-discarded", 2000.0);

            LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(
                new HashSet<string> { "rec-discarded" }, "test");

            var rows = Ledger.Actions.Where(a =>
                a.Type == GameActionType.MilestoneAchievement && a.MilestoneId == Node).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.MilestoneFundsAwarded == 2000f);
            Assert.Contains(logLines, l => l.Contains("PreserveIrreversibleLiveGameplayOnDiscard") && l.Contains("deduped=0"));
        }

        [Fact]
        public void DiscardReHome_AnEventAlreadyFiled_IsDedupedOnItsCapture()
        {
            // The same tagged event already reached the ledger as a row (same capture): the
            // re-home presents it again and the dedup drops it.
            var evt = RecordCrossing(150.0, "rec-discarded", 2000.0);
            Ledger.AddAction(GameStateEventConverter.ConvertEvent(evt, null));

            LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(
                new HashSet<string> { "rec-discarded" }, "test");

            Assert.Single(Ledger.Actions.Where(a =>
                a.Type == GameActionType.MilestoneAchievement && a.MilestoneId == Node));
            Assert.Contains(logLines, l => l.Contains("PreserveIrreversibleLiveGameplayOnDiscard") && l.Contains("deduped=1"));
        }

        // ================================================================
        // Save recovery migration
        // ================================================================

        [Fact]
        public void RecoveryMigration_ARowOfAnotherCapture_IsNotThisEventsRow()
        {
            // Two accepts of one guid can never be the same capture: the stored event whose
            // own row is missing is rebuilt even though a row with its key and moment exists.
            var first = Captured(GameStateEventType.ContractAccepted, "guid-x", 100.0, "");
            Ledger.AddAction(GameStateEventConverter.ConvertEvent(first, null));
            var second = Captured(GameStateEventType.ContractAccepted, "guid-x", 100.0, "");

            bool otherCapture;
            Assert.False(LedgerOrchestrator.LedgerHasMatchingAction(second, Ledger.Actions, out otherCapture));
            Assert.True(otherCapture);
            Assert.True(LedgerOrchestrator.LedgerHasMatchingAction(first, Ledger.Actions, out otherCapture));

            int recovered = LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad();

            Assert.Equal(1, recovered);
            Assert.Equal(2, Ledger.Actions.Count(a => a.Type == GameActionType.ContractAccept));
            Assert.Equal(1, CountLines("has no row of its own capture"));
            Assert.Equal(1, CountLines("keptDistinctCapture=1"));
            // Idempotent: both captures now have their row.
            Assert.Equal(0, LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad());
        }

        [Fact]
        public void RecoveryMigration_ARowWithNoCapture_KeepsTheEconomicMatch()
        {
            var evt = Captured(GameStateEventType.ContractAccepted, "guid-y", 100.0, "");
            Ledger.AddAction(new GameAction
            {
                UT = 100.05,
                Type = GameActionType.ContractAccept,
                ContractId = "guid-y",
                CaptureSequence = CaptureSequence.Unsequenced,
            });

            bool otherCapture;
            Assert.True(LedgerOrchestrator.LedgerHasMatchingAction(evt, Ledger.Actions, out otherCapture));
            Assert.False(otherCapture);
            Assert.Equal(0, LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad());
        }

        // ================================================================
        // Event identity in the store
        // ================================================================

        [Fact]
        public void EventIdentityMatches_TwoCapturesAtOneMoment_AreTwoEvents()
        {
            var first = RecordCrossing(100.0, "rec-A", 1000.0);
            var second = RecordCrossing(100.0, "rec-A", 2000.0);
            Assert.NotEqual(first.captureSequence, second.captureSequence);

            Assert.False(GameStateStore.EventIdentityMatches(first, second));
            Assert.True(GameStateStore.EventIdentityMatches(second, second));

            // The update lands on the capture it names, not the first look-alike.
            Assert.True(GameStateStore.UpdateEventDetail(second, "funds=3000;rep=1;sci=0"));
            var stored = GameStateStore.Events.Where(e => e.key == Node).ToList();
            Assert.Equal(first.detail, stored.Single(e => e.captureSequence == first.captureSequence).detail);
            Assert.Equal("funds=3000;rep=1;sci=0",
                stored.Single(e => e.captureSequence == second.captureSequence).detail);
        }

        [Fact]
        public void EventIdentityMatches_ACopyWithNoCapture_KeepsTheFieldMatch()
        {
            var stored = RecordCrossing(100.0, "rec-A", 1000.0);
            var copy = new GameStateEvent
            {
                ut = stored.ut,
                eventType = stored.eventType,
                key = stored.key,
                recordingId = stored.recordingId,
            };
            Assert.Equal(0, copy.captureSequence);
            Assert.True(GameStateStore.EventIdentityMatches(stored, copy));
        }
    }
}
