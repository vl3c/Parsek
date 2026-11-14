using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests for the units <c>LedgerOrchestrator.FileRetiredReFlyProvisionalRows</c>
    /// calls: the event collect, the pending-science collect, the conversion, the seed stamp
    /// and the surviving-science commit. The whole-method behavior is pinned by
    /// <see cref="FileRetiredReFlyProvisionalRowsCharacterizationTests"/>.
    /// </summary>
    [Collection("Sequential")]
    public class RetiredProvisionalRowUnitsTests : IDisposable
    {
        private const string Prov = "rec-prov";

        private readonly List<string> logLines = new List<string>();
        private readonly bool priorStoreSuppress;

        public RetiredProvisionalRowUnitsTests()
        {
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            ResetAll();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            ResetAll();
            KspStatePatcher.ResetForTesting();
        }

        private static void ResetAll()
        {
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        private static void AddMilestone(string id, double ut, string tag)
        {
            var e = new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.MilestoneAchieved,
                key = id,
                detail = GameStateRecorder.BuildMilestoneDetail(100.0, 2f, 0.0),
                recordingId = tag,
            };
            GameStateStore.AddEvent(ref e);
        }

        private static PendingScienceSubject Subject(string id, float science, double captureUT, string tag) =>
            new PendingScienceSubject
            {
                subjectId = id,
                science = science,
                subjectMaxValue = 10f,
                captureUT = captureUT,
                reasonKey = "ScienceTransmission",
                recordingId = tag,
                scienceGainMultiplier = 1f,
            };

        // ---- CollectRetiredProvisionalTaggedEvents ----------------------------------

        [Fact]
        public void CollectTaggedEvents_TakesOnlyExactTagMatches_AndClearsTheCopyTag()
        {
            AddMilestone("A", 10.0, Prov);
            AddMilestone("B", 20.0, "REC-PROV");
            AddMilestone("C", 30.0, null);
            AddMilestone("D", 40.0, "rec-other");
            AddMilestone("E", 50.0, Prov);

            var tagged = LedgerOrchestrator.CollectRetiredProvisionalTaggedEvents(Prov);

            Assert.Equal(new[] { "A", "E" }, tagged.Select(e => e.key).ToArray());
            Assert.All(tagged, e => Assert.Equal("", e.recordingId));
            // The store keeps its own tags; only the copies are cleared.
            Assert.Equal(2, GameStateStore.Events.Count(e => e.recordingId == Prov));
        }

        [Fact]
        public void CollectTaggedEvents_EmptyStore_ReturnsEmptyList()
        {
            var tagged = LedgerOrchestrator.CollectRetiredProvisionalTaggedEvents(Prov);
            Assert.NotNull(tagged);
            Assert.Empty(tagged);
        }

        // ---- CollectRetiredProvisionalScienceSubjects -------------------------------

        [Fact]
        public void CollectScienceSubjects_SpansOnlyTheMatchingCaptures()
        {
            var pending = GameStateRecorder.PendingScienceSubjects;
            pending.Add(Subject("s1", 1f, 300.0, Prov));
            pending.Add(Subject("s2", 1f, 100.0, "rec-other"));
            pending.Add(Subject("s3", 1f, 250.0, Prov));
            pending.Add(Subject("s4", 1f, 900.0, null));
            pending.Add(Subject("s5", 1f, 275.0, Prov));

            var subjects = LedgerOrchestrator.CollectRetiredProvisionalScienceSubjects(
                Prov, out double minUT, out double maxUT);

            Assert.Equal(new[] { "s1", "s3", "s5" }, subjects.Select(s => s.subjectId).ToArray());
            Assert.All(subjects, s => Assert.Equal("", s.recordingId));
            Assert.Equal(250.0, minUT);
            Assert.Equal(300.0, maxUT);
            Assert.Equal(3, pending.Count(s => s.recordingId == Prov));
        }

        [Fact]
        public void CollectScienceSubjects_NoMatch_ReturnsTheEmptySpanSentinels()
        {
            GameStateRecorder.PendingScienceSubjects.Add(Subject("s1", 1f, 300.0, "rec-other"));

            var subjects = LedgerOrchestrator.CollectRetiredProvisionalScienceSubjects(
                Prov, out double minUT, out double maxUT);

            Assert.Empty(subjects);
            Assert.Equal(double.MaxValue, minUT);
            Assert.Equal(double.MinValue, maxUT);
        }

        [Fact]
        public void CollectScienceSubjects_SingleMatch_MinAndMaxAreItsCapture()
        {
            GameStateRecorder.PendingScienceSubjects.Add(Subject("s1", 1f, 412.5, Prov));

            LedgerOrchestrator.CollectRetiredProvisionalScienceSubjects(
                Prov, out double minUT, out double maxUT);

            Assert.Equal(412.5, minUT);
            Assert.Equal(412.5, maxUT);
        }

        // ---- ConvertRetiredProvisionalRows ------------------------------------------

        [Fact]
        public void Convert_NoSubjects_LeavesScienceActionsNull()
        {
            var tagged = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 5.0,
                    eventType = GameStateEventType.MilestoneAchieved,
                    key = "FirstLaunch",
                    detail = GameStateRecorder.BuildMilestoneDetail(100.0, 1f, 0.0),
                    recordingId = "",
                },
            };

            var actions = LedgerOrchestrator.ConvertRetiredProvisionalRows(
                tagged, new List<PendingScienceSubject>(), double.MaxValue, double.MinValue,
                out List<GameAction> scienceActions);

            Assert.Null(scienceActions);
            var row = Assert.Single(actions);
            Assert.Equal(GameActionType.MilestoneAchievement, row.Type);
            Assert.True(string.IsNullOrEmpty(row.RecordingId));
        }

        [Fact]
        public void Convert_SubjectsWithNoScience_GiveAnEmptyNonNullScienceList()
        {
            var actions = LedgerOrchestrator.ConvertRetiredProvisionalRows(
                new List<GameStateEvent>(),
                new List<PendingScienceSubject> { Subject("s0", 0f, 50.0, "") },
                50.0, 50.0, out List<GameAction> scienceActions);

            Assert.NotNull(scienceActions);
            Assert.Empty(scienceActions);
            Assert.Empty(actions);
        }

        [Fact]
        public void Convert_AppendsScienceAfterEvents_OverTheCaptureSpanWidenedOneSecond()
        {
            var tagged = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 900.0,
                    eventType = GameStateEventType.TechResearched,
                    key = "basicRocketry",
                    detail = "cost=5",
                    recordingId = "",
                },
            };
            var subjects = new List<PendingScienceSubject>
            {
                Subject("s1", 1.5f, 400.0, ""),
                Subject("s2", 2.5f, 410.0, ""),
            };

            var actions = LedgerOrchestrator.ConvertRetiredProvisionalRows(
                tagged, subjects, 400.0, 410.0, out List<GameAction> scienceActions);

            Assert.Equal(2, scienceActions.Count);
            Assert.Equal(3, actions.Count);
            Assert.Equal(GameActionType.ScienceSpending, actions[0].Type);
            Assert.Same(scienceActions[0], actions[1]);
            Assert.Same(scienceActions[1], actions[2]);
            // The window's end is the latest capture plus one second.
            Assert.All(scienceActions, a => Assert.Equal(411.0, a.EndUT));
        }

        // ---- StampRetiredProvisionalRowsAgainstSeed ---------------------------------

        private static GameAction RepRow(double ut) =>
            new GameAction
            {
                UT = ut,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "RecordsAltitude",
                MilestoneRepAwarded = 3f,
            };

        [Fact]
        public void Stamp_KnownOrigin_DoesNotEnsureTheSeed()
        {
            bool probed = false;
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () =>
            {
                probed = true;
                return new LedgerOrchestrator.CurrencyPoolProbe
                {
                    ReputationSingletonPresent = true,
                    ReputationValue = 40f,
                };
            };
            var actions = new List<GameAction> { RepRow(383.0) };

            LedgerOrchestrator.StampRetiredProvisionalRowsAgainstSeed(
                actions, Prov, ReputationSeedOrigin.PreExisting);

            Assert.False(probed);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.ReputationInitial);
            Assert.False(actions[0].InsideReputationSeed);
        }

        [Fact]
        public void Stamp_KnownLivePoolOrigin_StampsWithoutCreatingASeed()
        {
            var actions = new List<GameAction> { RepRow(383.0) };

            LedgerOrchestrator.StampRetiredProvisionalRowsAgainstSeed(
                actions, Prov, ReputationSeedOrigin.CreatedThisCommitFromLivePool);

            Assert.True(actions[0].InsideReputationSeed);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.ReputationInitial);
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("for retired-refly-provisional=rec-prov")
                && l.Contains("repSeedOrigin=CreatedThisCommitFromLivePool"));
        }

        [Fact]
        public void Stamp_NotYetCaptured_EnsuresTheSeedOffTheLivePool()
        {
            LedgerOrchestrator.CurrencyPoolProbeForTesting = () => new LedgerOrchestrator.CurrencyPoolProbe
            {
                ReputationSingletonPresent = true,
                ReputationValue = 40f,
            };
            LedgerOrchestrator.UncommittedRecordingIdsForTesting = () => new List<string>();
            var actions = new List<GameAction> { RepRow(383.0) };

            LedgerOrchestrator.StampRetiredProvisionalRowsAgainstSeed(
                actions, Prov, ReputationSeedOrigin.NotYetCaptured);

            var seed = Assert.Single(Ledger.Actions, a => a.Type == GameActionType.ReputationInitial);
            Assert.Equal(40f, seed.InitialReputation);
            Assert.True(actions[0].InsideReputationSeed);
            Assert.Contains(logLines, l => l.Contains("repSeedOrigin=CreatedThisCommitFromLivePool"));
        }

        [Fact]
        public void Stamp_RowRecordedBeforeALivePoolSeedCapture_IsStampedInside()
        {
            Ledger.SeedInitialReputation(40f, new List<string> { Prov }, 500.0);
            var actions = new List<GameAction> { RepRow(383.0), RepRow(600.0) };

            LedgerOrchestrator.StampRetiredProvisionalRowsAgainstSeed(
                actions, Prov, ReputationSeedOrigin.PreExisting);

            Assert.True(actions[0].InsideReputationSeed);
            Assert.False(actions[1].InsideReputationSeed);
            Assert.Contains(logLines, l => l.Contains("Reputation seed stamp: 1 row(s)")
                && l.Contains("for retired-refly-provisional=rec-prov"));
        }

        // ---- CommitSurvivingRetiredProvisionalScience -------------------------------

        [Fact]
        public void CommitSurvivingScience_CommitsOnlyScienceEarningRows()
        {
            var actions = new List<GameAction>
            {
                RepRow(10.0),
                new GameAction
                {
                    UT = 20.0, Type = GameActionType.ScienceEarning,
                    SubjectId = "crewReport@KerbinSrfLanded", ScienceAwarded = 1.5f,
                },
                new GameAction
                {
                    UT = 30.0, Type = GameActionType.ScienceEarning,
                    SubjectId = "evaReport@KerbinSrfLanded", ScienceAwarded = 2f,
                },
            };

            LedgerOrchestrator.CommitSurvivingRetiredProvisionalScience(actions);

            var ids = GameStateStore.GetCommittedScienceSubjectIds();
            ids.Sort(StringComparer.Ordinal);
            Assert.Equal(new[] { "crewReport@KerbinSrfLanded", "evaReport@KerbinSrfLanded" }, ids.ToArray());
            Assert.Contains(logLines, l => l.Contains("CommitScienceActions: 2 added, 0 updated, skipped=0"));
        }

        [Fact]
        public void CommitSurvivingScience_NoScienceRows_DoesNotCallTheStore()
        {
            LedgerOrchestrator.CommitSurvivingRetiredProvisionalScience(
                new List<GameAction> { RepRow(10.0) });

            Assert.Empty(GameStateStore.GetCommittedScienceSubjectIds());
            Assert.DoesNotContain(logLines, l => l.Contains("CommitScienceActions"));
        }
    }
}
