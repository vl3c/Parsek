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
            var logLines = CaptureVerboseLogs();

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
            AssertRebuildLog(logLines, subjects: 2, scienceActions: 2);
        }

        [Fact]
        public void RecalculateAndPatch_CutoffThenFullWalk_DoesNotDriftCommittedCache()
        {
            Ledger.AddActions(new[]
            {
                ScienceAction(100.0, "rec-past", "shared-subject", 6f, 10f),
                ScienceAction(200.0, "rec-future", "shared-subject", 6f, 10f),
                ScienceAction(250.0, "rec-future", "future-subject", 3f, 5f)
            });

            LedgerOrchestrator.RecalculateAndPatch(utCutoff: 150.0);

            var cutoffSnapshot = SnapshotCommittedScience(
                "shared-subject",
                "future-subject");
            Assert.Equal(2, GameStateStore.CommittedScienceSubjectCount);
            Assert.Equal(10f, cutoffSnapshot["shared-subject"], 0.001f);
            Assert.Equal(3f, cutoffSnapshot["future-subject"], 0.001f);
            Assert.Equal(6.0, LedgerOrchestrator.Science.GetSubjectCredited("shared-subject"), 3);
            Assert.Equal(0.0, LedgerOrchestrator.Science.GetSubjectCredited("future-subject"), 3);

            LedgerOrchestrator.RecalculateAndPatch();

            AssertCommittedScienceSnapshot(cutoffSnapshot);
            Assert.Equal(10.0, LedgerOrchestrator.Science.GetSubjectCredited("shared-subject"), 3);
            Assert.Equal(3.0, LedgerOrchestrator.Science.GetSubjectCredited("future-subject"), 3);

            LedgerOrchestrator.RecalculateAndPatch();

            AssertCommittedScienceSnapshot(cutoffSnapshot);
            Assert.Equal(10.0, LedgerOrchestrator.Science.GetSubjectCredited("shared-subject"), 3);
            Assert.Equal(3.0, LedgerOrchestrator.Science.GetSubjectCredited("future-subject"), 3);
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

        private static Dictionary<string, float> SnapshotCommittedScience(params string[] subjectIds)
        {
            var snapshot = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int i = 0; i < subjectIds.Length; i++)
            {
                float science;
                Assert.True(GameStateStore.TryGetCommittedSubjectScience(subjectIds[i], out science));
                snapshot[subjectIds[i]] = science;
            }

            return snapshot;
        }

        private static void AssertCommittedScienceSnapshot(Dictionary<string, float> expected)
        {
            Assert.Equal(expected.Count, GameStateStore.CommittedScienceSubjectCount);
            foreach (var kvp in expected)
                AssertCommittedScience(kvp.Key, kvp.Value);
        }

        private static List<string> CaptureVerboseLogs()
        {
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            return logLines;
        }

        private static void AssertRebuildLog(
            List<string> logLines,
            int subjects,
            int scienceActions)
        {
            Assert.Contains(logLines, line =>
                line.Contains("[VERBOSE][LedgerOrchestrator] " +
                    "RebuildCommittedScienceFromSurvivingLedger") &&
                line.Contains($"subjects={subjects}") &&
                line.Contains($"scienceActions={scienceActions}") &&
                line.Contains("actions="));
        }
    }
}
