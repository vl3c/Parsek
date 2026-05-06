using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class CommittedScienceCacheRebuildTests : IDisposable
    {
        public CommittedScienceCacheRebuildTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;

            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;

            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void RecalculateAndPatch_CutoffWalk_KeepsFutureScienceInCommittedCacheOnly()
        {
            Ledger.AddActions(new[]
            {
                ScienceAction(100.0, "rec-past", "past-subject", 5f, 10f),
                ScienceAction(200.0, "rec-future", "future-subject", 7f, 10f)
            });

            LedgerOrchestrator.RecalculateAndPatch(utCutoff: 150.0);

            Assert.Equal(2, GameStateStore.CommittedScienceSubjectCount);
            AssertCommittedScience("past-subject", 5f);
            AssertCommittedScience("future-subject", 7f);

            Assert.Equal(5.0, LedgerOrchestrator.Science.GetSubjectCredited("past-subject"), 3);
            Assert.Equal(0.0, LedgerOrchestrator.Science.GetSubjectCredited("future-subject"), 3);
        }

        [Fact]
        public void RecalculateAndPatch_FullWalk_PrunesDeletedScienceSubjectsFromCommittedCache()
        {
            GameStateStore.RebuildCommittedScienceSubjects(new[]
            {
                new KeyValuePair<string, float>("surviving-subject", 4f),
                new KeyValuePair<string, float>("deleted-subject", 9f)
            });

            Ledger.AddAction(ScienceAction(
                100.0,
                "rec-surviving",
                "surviving-subject",
                4f,
                10f));

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(1, GameStateStore.CommittedScienceSubjectCount);
            AssertCommittedScience("surviving-subject", 4f);

            float science;
            Assert.False(GameStateStore.TryGetCommittedSubjectScience("deleted-subject", out science));
        }

        private static GameAction ScienceAction(
            double ut,
            string recordingId,
            string subjectId,
            float scienceAwarded,
            float subjectMaxValue)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ScienceEarning,
                RecordingId = recordingId,
                SubjectId = subjectId,
                ScienceAwarded = scienceAwarded,
                SubjectMaxValue = subjectMaxValue
            };
        }

        private static void AssertCommittedScience(string subjectId, float expected)
        {
            float science;
            Assert.True(GameStateStore.TryGetCommittedSubjectScience(subjectId, out science));
            Assert.Equal(expected, science, 0.001f);
        }
    }
}
