using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RESTORE-ATTEMPT-CUTOFF-AHEAD-OF-CLOCK (lane RF-11-both-slots-in-sequence, run
    /// 2026-11-16_2341): a committed-tree restore attempt resumes a recording whose end is a
    /// predicted terminal AHEAD of the live clock (a merged Re-Fly provisional, crashed at an
    /// extrapolated UT 429.43, resumed by a load at UT 131.9 while its vessel still flies).
    /// The resumed recording's event cutoff must be the resume instant, not that predicted
    /// end: every award the resumed flight earns is the attempt's own (coalesced into one
    /// seed, kept off the file, purged by a rollback discard), never committed history.
    /// </summary>
    [Collection("Sequential")]
    public class RestoreAttemptCutoffAheadOfClockTests : IDisposable
    {
        private const string TreeId = "tree-refly-provisional";
        private const string ResumedId = "rec-provisional";
        private const double PredictedEndUT = 429.43;
        private const double HistoryUT = 132.0;
        private const double ResumeUT = 132.5;

        private readonly List<string> logLines = new List<string>();

        public RestoreAttemptCutoffAheadOfClockTests()
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

        // The merged provisional: no recorded data, its end the extrapolated crash.
        private static RecordingTree MakeTree()
        {
            var rec = new Recording
            {
                RecordingId = ResumedId,
                TreeId = TreeId,
                VesselName = "Kerbal X",
                ExplicitStartUT = 131.54,
                ExplicitEndUT = PredictedEndUT,
            };
            var tree = new RecordingTree { Id = TreeId, TreeName = "Kerbal X" };
            tree.AddOrReplaceRecording(rec);
            return tree;
        }

        private static void AddHistoryAltitudeEvent()
        {
            var history = new GameStateEvent
            {
                ut = HistoryUT,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "RecordsAltitude",
                detail = GameStateRecorder.BuildMilestoneDetail(100.0, 0f, 0.0, 2, "1:50:0:0,2:50:0:0"),
                recordingId = ResumedId,
            };
            GameStateStore.AddEvent(ref history);
        }

        private static void FireAltitudeAward(int interval, double funds, double ut)
        {
            var node = new KSPAchievements.RecordsAltitude();
            SetField(node, "rewardInterval", interval);
            GameStateRecorder.EmitStandaloneProgressReward(node, funds, 0f, 0.0, ut: ut);
        }

        private static List<GameStateEvent> AltitudeEvents() => GameStateStore.Events
            .Where(e => e.eventType == GameStateEventType.MilestoneAchieved && e.key == "RecordsAltitude")
            .ToList();

        // catches: the resumed recording's cutoff left at its predicted end ahead of the
        // clock: each live award reads as history, is never coalesced (one standalone event
        // per threshold), is written to the file as committed history, and survives the
        // rollback discard of the attempt, so the next tree commit files it as a ledger row
        // of the merged provisional and the load sweep seals the slot.
        [Fact]
        public void ResumedRecordingEndAheadOfClock_LiveAwardsAreTheAttemptsOwn_AndADiscardPurgesThem()
        {
            LedgerOrchestrator.Initialize();
            AddHistoryAltitudeEvent();
            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(
                MakeTree(), "test-arm", ResumedId, ResumeUT);
            GameStateRecorder.TagResolverForTesting = () => ResumedId;

            FireAltitudeAward(3, 200.0, 132.6);
            FireAltitudeAward(4, 300.0, 132.7);
            GameStateRecorder.TagResolverForTesting = null;

            var attemptOwn = AltitudeEvents().Where(e => e.ut > ResumeUT).ToList();
            Assert.Single(attemptOwn);
            Assert.Contains("thresholds=2", attemptOwn[0].detail);
            Assert.True(CommittedTreeRestoreAttempt
                .ShouldSuppressCommittedTreeRestoreAttemptEventPersistence(attemptOwn[0]));

            RecordingStore.DiscardCommittedTreeRestoreAttempt("test-rollback", null);

            var left = AltitudeEvents();
            Assert.DoesNotContain(left, e => e.ut > ResumeUT);
            var history = Assert.Single(left);
            Assert.Equal(HistoryUT, history.ut);
            Assert.Equal(
                GameStateRecorder.BuildMilestoneDetail(100.0, 0f, 0.0, 2, "1:50:0:0,2:50:0:0"),
                history.detail);
        }

        // catches: the clamp landing AT the resume instant: stock awards the records in the
        // load frame itself, stamped with the same clock the arm read (RF-11: ut=131.9 at the
        // 131.9 load), and `!(ut > cutoff)` would put them back on the history side.
        [Fact]
        public void AwardStampedAtTheResumeInstant_IsTheAttemptsOwn_AndTheNextOneCoalescesIntoIt()
        {
            LedgerOrchestrator.Initialize();
            AddHistoryAltitudeEvent();
            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(
                MakeTree(), "test-arm", ResumedId, ResumeUT);
            GameStateRecorder.TagResolverForTesting = () => ResumedId;

            FireAltitudeAward(3, 200.0, ResumeUT);
            FireAltitudeAward(4, 300.0, ResumeUT);
            GameStateRecorder.TagResolverForTesting = null;

            var own = Assert.Single(AltitudeEvents().Where(e => e.ut == ResumeUT));
            Assert.Contains("thresholds=2", own.detail);
            Assert.True(CommittedTreeRestoreAttempt.ShouldSuppressCommittedTreeRestoreAttemptEventPersistence(own));
            var history = Assert.Single(AltitudeEvents().Where(e => e.ut == HistoryUT));
            Assert.False(CommittedTreeRestoreAttempt.ShouldSuppressCommittedTreeRestoreAttemptEventPersistence(history));
            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("TryCoalesceWorldRecordReward")
                && l.Contains("skipped 1 restore-attempt history event(s)"));
        }

        // catches: a missing or unreadable arm line: the RF-11 log could not show the cutoff
        // the arm chose for the resumed recording.
        [Fact]
        public void ArmLine_NamesTheResumedRecordingTheResumeInstantAndItsCutoff()
        {
            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(
                MakeTree(), "test-arm", ResumedId, ResumeUT);

            Assert.True(CommittedTreeRestoreAttempt.TryGetEventCutoff(ResumedId, out double cutoff));
            Assert.True(cutoff < ResumeUT);
            Assert.Equal(CommittedTreeRestoreAttempt.LargestDoubleBelow(ResumeUT), cutoff);
            Assert.Contains(logLines, l => l.Contains("[INFO][RecordingStore]")
                && l.Contains("Armed committed-tree restore attempt")
                && l.Contains("resumed=" + ResumedId)
                && l.Contains("resumeUT=132.5 ")
                && l.Contains("endUT=429.43 ")
                && l.Contains("resumedCutoff=" + cutoff.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                && l.Contains("rule=resume instant (end ahead of the clock)"));
        }

        // catches: the clamp applied to every recording of the tree. A recording the attempt
        // did not resume can hold recorded data past the clock (a sibling's future); a
        // discard would then purge its real history.
        [Fact]
        public void NonResumedRecording_WithRecordedDataPastTheClock_KeepsItsEndCutoff()
        {
            RecordingTree tree = MakeTree();
            var sibling = new Recording
            {
                RecordingId = "rec-sibling", TreeId = TreeId, VesselName = "Booster",
                ExplicitStartUT = 131.54, ExplicitEndUT = 500.0,
            };
            sibling.Points.Add(new TrajectoryPoint { ut = 131.54, bodyName = "Kerbin" });
            sibling.Points.Add(new TrajectoryPoint { ut = 500.0, bodyName = "Kerbin" });
            tree.AddOrReplaceRecording(sibling);
            var siblingHistory = new GameStateEvent
            {
                ut = 300.0, eventType = GameStateEventType.MilestoneAchieved, key = "Kerbin/Landing",
                detail = "funds=0;rep=0;sci=0", recordingId = "rec-sibling",
            };
            GameStateStore.AddEvent(ref siblingHistory);

            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(tree, "test-arm", ResumedId, ResumeUT);

            Assert.True(CommittedTreeRestoreAttempt.TryGetEventCutoff("rec-sibling", out double siblingCutoff));
            Assert.Equal(500.0, siblingCutoff);
            Assert.True(CommittedTreeRestoreAttempt.TryGetEventCutoff(ResumedId, out double resumedCutoff));
            Assert.True(resumedCutoff < ResumeUT);

            RecordingStore.DiscardCommittedTreeRestoreAttempt("test-rollback", null);
            Assert.Contains(GameStateStore.Events, e => e.recordingId == "rec-sibling" && e.ut == 300.0);
        }

        // catches: the clamp leaking into the SE-1 shape (the resumed recording ended in the
        // past, or exactly at the resume instant): its cutoff stays its end, as before.
        [Theory]
        [InlineData(120.0)]
        [InlineData(ResumeUT)]
        public void ResumedRecordingEndedAtOrBeforeTheClock_KeepsItsEndCutoff(double endUT)
        {
            RecordingTree tree = MakeTree();
            tree.Recordings[ResumedId].ExplicitEndUT = endUT;

            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(tree, "test-arm", ResumedId, ResumeUT);

            Assert.True(CommittedTreeRestoreAttempt.TryGetEventCutoff(ResumedId, out double cutoff));
            Assert.Equal(endUT, cutoff);
            Assert.Contains(logLines, l => l.Contains("Armed committed-tree restore attempt")
                && l.Contains("rule=end (not ahead of the clock)"));
        }

        // catches: a NaN or infinite clock (headless callers, an unreadable Planetarium), or no
        // resumed recording named (the 2-argument arm every other caller uses), changing today's
        // cutoffs.
        [Theory]
        [InlineData(double.NaN, ResumedId)]
        [InlineData(double.PositiveInfinity, ResumedId)]
        [InlineData(double.NegativeInfinity, ResumedId)]
        [InlineData(ResumeUT, null)]
        [InlineData(ResumeUT, "rec-not-in-tree")]
        public void NoUsableResumeFacts_KeepEveryCutoffAtItsEnd(double resumeUT, string resumedId)
        {
            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(MakeTree(), "test-arm", resumedId, resumeUT);

            Assert.True(CommittedTreeRestoreAttempt.TryGetEventCutoff(ResumedId, out double cutoff));
            Assert.Equal(PredictedEndUT, cutoff);
            Assert.False(CommittedTreeRestoreAttempt.TryGetEventCutoff("rec-not-in-tree", out _));
        }

        // catches: the clamp turning history the store already holds into the attempt's: an
        // event of the resumed recording stamped after the resume instant (the save put the
        // clock back behind it) must stay history, so the cutoff never drops below it.
        [Fact]
        public void StoredEventAfterTheResumeInstant_RaisesTheCutoffToIt()
        {
            var later = new GameStateEvent
            {
                ut = 140.0, eventType = GameStateEventType.ContractCompleted, key = "history-later",
                detail = "", recordingId = ResumedId,
            };
            GameStateStore.AddEvent(ref later);
            var past = new GameStateEvent
            {
                ut = 500.0, eventType = GameStateEventType.ContractCompleted, key = "past-the-end",
                detail = "", recordingId = ResumedId,
            };
            GameStateStore.AddEvent(ref past);

            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(MakeTree(), "test-arm", ResumedId, ResumeUT);

            Assert.True(CommittedTreeRestoreAttempt.TryGetEventCutoff(ResumedId, out double cutoff));
            Assert.Equal(140.0, cutoff);
            Assert.Contains(logLines, l => l.Contains("rule=latest stored event after the resume instant"));
        }

        // catches: the same raise reading only the live event list: an event flushed into a
        // milestone is history the purge also reaches (PurgeTaggedEventsAfterUT).
        [Fact]
        public void StoredMilestoneEventAfterTheResumeInstant_RaisesTheCutoffToIt()
        {
            var milestone = new Milestone
            {
                MilestoneId = "m1",
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 150.0, eventType = GameStateEventType.TechResearched, key = "flushed",
                        detail = "", recordingId = ResumedId,
                    },
                },
            };
            MilestoneStore.AddMilestoneForTesting(milestone);

            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(MakeTree(), "test-arm", ResumedId, ResumeUT);

            Assert.True(CommittedTreeRestoreAttempt.TryGetEventCutoff(ResumedId, out double cutoff));
            Assert.Equal(150.0, cutoff);
        }

        // Pins the designed tie: a stored event stamped exactly at the resume instant cannot be
        // told from an award of the resume frame by its UT; it reads as the attempt's (counted
        // on the arm line), because the load frame's awards are the case that happens.
        [Fact]
        public void StoredEventExactlyAtTheResumeInstant_ReadsAsTheAttemptsAndIsCounted()
        {
            var atResume = new GameStateEvent
            {
                ut = ResumeUT, eventType = GameStateEventType.ContractCompleted, key = "at-resume",
                detail = "", recordingId = ResumedId,
            };
            GameStateStore.AddEvent(ref atResume);

            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(MakeTree(), "test-arm", ResumedId, ResumeUT);

            Assert.True(CommittedTreeRestoreAttempt.TryGetEventCutoff(ResumedId, out double cutoff));
            Assert.True(cutoff < ResumeUT);
            Assert.Contains(logLines, l => l.Contains("Armed committed-tree restore attempt")
                && l.Contains("storedEventsAtResume=1"));
        }

        [Theory]
        [InlineData(429.43, 132.5, double.NaN, "below")]
        [InlineData(429.43, 132.5, 140.0, "140")]
        [InlineData(429.43, 132.5, 132.4, "below")]
        [InlineData(120.0, 132.5, double.NaN, "120")]
        [InlineData(132.5, 132.5, double.NaN, "132.5")]
        [InlineData(429.43, double.NaN, double.NaN, "429.43")]
        [InlineData(429.43, double.PositiveInfinity, double.NaN, "429.43")]
        [InlineData(double.NaN, 132.5, double.NaN, "NaN")]
        public void ComputeResumedRecordingEventCutoff_Table(
            double endUT, double resumeUT, double latestAfter, string expected)
        {
            double cutoff = CommittedTreeRestoreAttempt.ComputeResumedRecordingEventCutoff(endUT, resumeUT, latestAfter);
            if (expected == "below")
                Assert.Equal(CommittedTreeRestoreAttempt.LargestDoubleBelow(resumeUT), cutoff);
            else
                Assert.Equal(double.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), cutoff);
        }

        [Theory]
        [InlineData(132.5)]
        [InlineData(1.0)]
        [InlineData(-3.25)]
        [InlineData(0.0)]
        [InlineData(1e15)]
        public void LargestDoubleBelow_IsTheNextRepresentableValueDown(double value)
        {
            double below = CommittedTreeRestoreAttempt.LargestDoubleBelow(value);
            Assert.True(below < value);
            // Nothing representable lies between them.
            double mid = below + (value - below) / 2.0;
            Assert.True(mid == below || mid == value);
        }

        // ---- the save round trip ----

        // catches: the clamp lost on a save inside the attempt: the cold re-arm rebuilt the
        // resumed recording's cutoff at its predicted end again.
        [Fact]
        public void AttemptNode_CarriesTheResumeFacts_RoundTrip_AndAbsentKeysReadAsNone()
        {
            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(MakeTree(), "test-arm", ResumedId, 131.9000000001);
            var build = GameStateStore.BuildEventsNode(
                RecordingStore.CommittedTreeRestoreAttemptTreeId,
                RecordingStore.CommittedTreeRestoreAttemptReason);

            Assert.Equal(ResumedId, build.AttemptEvents.GetValue(GameStateStore.AttemptResumedRecordingIdKey));
            Assert.True(GameStateStore.TryReadAttemptResumeValues(build.AttemptEvents, out string id, out double ut));
            Assert.Equal(ResumedId, id);
            Assert.Equal(131.9000000001, ut);

            // Another tree id (no live attempt of that id) writes no resume facts.
            var other = GameStateStore.BuildEventsNode("tree-other", "x");
            Assert.Null(other.AttemptEvents.GetValue(GameStateStore.AttemptResumedRecordingIdKey));
            Assert.False(GameStateStore.TryReadAttemptResumeValues(other.AttemptEvents, out id, out ut));
            Assert.Null(id);
            Assert.True(double.IsNaN(ut));

            // An attempt armed without them (the 2-argument arm) writes none either.
            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(MakeTree(), "test-arm");
            var bare = GameStateStore.BuildEventsNode(
                RecordingStore.CommittedTreeRestoreAttemptTreeId,
                RecordingStore.CommittedTreeRestoreAttemptReason);
            Assert.Null(bare.AttemptEvents.GetValue(GameStateStore.AttemptResumeUTKey));

            var garbled = new ConfigNode(GameStateStore.AttemptEventsNodeName);
            garbled.AddValue(GameStateStore.AttemptResumedRecordingIdKey, ResumedId);
            garbled.AddValue(GameStateStore.AttemptResumeUTKey, "not-a-number");
            Assert.False(GameStateStore.TryReadAttemptResumeValues(garbled, out _, out _));
        }

        [Fact]
        public void AttemptResumeValuesMissingReason_NamesWhatIsMissing()
        {
            Assert.Equal("no-attempt-node", GameStateStore.AttemptResumeValuesMissingReason(null));
            var node = new ConfigNode(GameStateStore.AttemptEventsNodeName);
            Assert.Equal("no-resumed-recording-id", GameStateStore.AttemptResumeValuesMissingReason(node));
            node.AddValue(GameStateStore.AttemptResumedRecordingIdKey, ResumedId);
            Assert.Equal("no-resume-ut", GameStateStore.AttemptResumeValuesMissingReason(node));
            node.AddValue(GameStateStore.AttemptResumeUTKey, "Infinity");
            Assert.Equal("resume-ut-unreadable", GameStateStore.AttemptResumeValuesMissingReason(node));
            node.SetValue(GameStateStore.AttemptResumeUTKey, "132.5");
            Assert.Null(GameStateStore.AttemptResumeValuesMissingReason(node));
        }

        // catches: the held-path re-arm (an attempt resumed in place; the flight arms afresh at
        // the load's clock) moving the cutoff forward past the attempt's own held events, or
        // adopting an instant of another recording or a later one.
        [Fact]
        public void AdoptSavedResumeInstant_MovesBackOnlyForTheSameRecordingAndAnEarlierInstant()
        {
            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(MakeTree(), "test-arm", ResumedId, 140.0);

            Assert.False(CommittedTreeRestoreAttempt.AdoptSavedResumeInstant("rec-other", ResumeUT, "test"));
            Assert.False(CommittedTreeRestoreAttempt.AdoptSavedResumeInstant(ResumedId, 150.0, "test"));
            Assert.False(CommittedTreeRestoreAttempt.AdoptSavedResumeInstant(ResumedId, double.NaN, "test"));
            Assert.True(CommittedTreeRestoreAttempt.TryGetEventCutoff(ResumedId, out double cutoff));
            Assert.Equal(CommittedTreeRestoreAttempt.LargestDoubleBelow(140.0), cutoff);

            Assert.True(CommittedTreeRestoreAttempt.AdoptSavedResumeInstant(ResumedId, ResumeUT, "test"));
            Assert.True(CommittedTreeRestoreAttempt.TryGetEventCutoff(ResumedId, out cutoff));
            Assert.Equal(CommittedTreeRestoreAttempt.LargestDoubleBelow(ResumeUT), cutoff);
            Assert.Equal(ResumeUT, RecordingStore.CommittedTreeRestoreAttemptResumeUT);
            Assert.Contains(logLines, l => l.Contains("[INFO][RecordingStore]")
                && l.Contains("AdoptSavedResumeInstant (test)") && l.Contains("moved back 140 -> 132.5"));

            RecordingStore.ClearCommittedTreeRestoreAttempt("test-clear");
            Assert.False(CommittedTreeRestoreAttempt.AdoptSavedResumeInstant(ResumedId, 100.0, "test"));
            Assert.Null(RecordingStore.CommittedTreeRestoreAttemptResumedRecordingId);
            Assert.True(double.IsNaN(RecordingStore.CommittedTreeRestoreAttemptResumeUT));
        }

        // ---- ledger rows (the #2622 guards, clamped) ----

        private const double HistoryRowUT = 132.45;

        // A committed history event and its row 0.05 s before the resume instant: inside the
        // 0.1 s row window of an award in the resume frame.
        private static GameAction AddHistoryEventAndRow()
        {
            var evt = new GameStateEvent
            {
                ut = HistoryRowUT,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "RecordsAltitude",
                detail = GameStateRecorder.BuildMilestoneDetail(100.0, 0f, 0.0, 1, "2:100:0:0"),
                recordingId = ResumedId,
            };
            GameStateStore.AddEvent(ref evt);
            var row = new GameAction
            {
                UT = HistoryRowUT,
                Type = GameActionType.MilestoneAchievement,
                RecordingId = ResumedId,
                MilestoneId = "RecordsAltitude",
                MilestoneFundsAwarded = 100f,
                MilestoneRecordThresholds = 1,
                MilestoneRecordBand = new List<RecordThresholdCredit> { new RecordThresholdCredit(2, 100f, 0f, 0f) },
                CaptureSequence = evt.captureSequence,
            };
            Ledger.AddAction(row);
            return row;
        }

        private static void FirePayingAwardsFromTheResumeFrame()
        {
            FireAltitudeAward(3, 200.0, ResumeUT);
            FireAltitudeAward(4, 300.0, ResumeUT + 0.02);
        }

        private static float BookedAltitudeFunds() => Ledger.Actions
            .Where(a => a.Type == GameActionType.MilestoneAchievement && a.MilestoneId == "RecordsAltitude")
            .Sum(a => a.MilestoneFundsAwarded);

        // catches: under the clamp, a live discard booking an award twice (folded into the
        // history row AND re-homed with the attempt's seed) or dropping it.
        [Fact]
        public void Discard_ClampedAttemptNextToAHistoryRow_BooksWhatStockPaidOnce()
        {
            LedgerOrchestrator.Initialize();
            GameAction history = AddHistoryEventAndRow();
            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(MakeTree(), "test-arm", ResumedId, ResumeUT);
            GameStateRecorder.TagResolverForTesting = () => ResumedId;
            FirePayingAwardsFromTheResumeFrame();
            GameStateRecorder.TagResolverForTesting = null;

            Assert.Equal(100f, history.MilestoneFundsAwarded);

            RecordingStore.DiscardCommittedTreeRestoreAttempt("test-discard",
                LedgerOrchestrator.PreserveIrreversibleRestoreAttemptTailsOnDiscard);

            Assert.Equal(100f, history.MilestoneFundsAwarded);
            Assert.Equal(1, history.MilestoneRecordThresholds);
            var rehomed = Ledger.Actions.Single(a => a.Type == GameActionType.MilestoneAchievement
                && a.MilestoneId == "RecordsAltitude" && !ReferenceEquals(a, history));
            Assert.Equal(500f, rehomed.MilestoneFundsAwarded);
            Assert.Equal(2, rehomed.MilestoneRecordThresholds);
            Assert.Equal(600f, BookedAltitudeFunds());
            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "RecordsAltitude" && e.ut >= ResumeUT);
        }

        // catches: the same on commit: the attempt's seed filed as its own row beside the
        // history row, once.
        [Fact]
        public void Commit_ClampedAttemptNextToAHistoryRow_BooksWhatStockPaidOnce()
        {
            LedgerOrchestrator.Initialize();
            GameAction history = AddHistoryEventAndRow();
            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(MakeTree(), "test-arm", ResumedId, ResumeUT);
            GameStateRecorder.TagResolverForTesting = () => ResumedId;
            FirePayingAwardsFromTheResumeFrame();
            GameStateRecorder.TagResolverForTesting = null;

            bool scienceAdded = false;
            LedgerOrchestrator.OnRecordingCommitted(
                ResumedId, 131.54, PredictedEndUT, new List<PendingScienceSubject>(), ref scienceAdded);

            Assert.Equal(100f, history.MilestoneFundsAwarded);
            Assert.Equal(1, history.MilestoneRecordThresholds);
            var attemptRow = Ledger.Actions.Single(a => a.Type == GameActionType.MilestoneAchievement
                && a.MilestoneId == "RecordsAltitude" && !ReferenceEquals(a, history));
            Assert.Equal(500f, attemptRow.MilestoneFundsAwarded);
            Assert.Equal(600f, BookedAltitudeFunds());
        }
    }
}
