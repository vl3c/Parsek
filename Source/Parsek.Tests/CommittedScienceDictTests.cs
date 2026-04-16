using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for #391: committedScienceSubjects dictionary staleness after recording
    /// deletion. Validates RebuildCommittedScienceSubjects and CommitScienceSubjects
    /// edge cases.
    /// </summary>
    [Collection("Sequential")]
    public class CommittedScienceDictTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CommittedScienceDictTests()
        {
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ================================================================
        // CommitScienceSubjects edge case: equal value re-commit
        // ================================================================

        [Fact]
        public void CommitScienceSubjects_EqualValue_DictionaryRetainsValue()
        {
            // Commit X=5.0, then commit X=5.0 again. The > guard means
            // the second commit doesn't update, but the value should still be 5.0.
            var batch1 = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "crewReport@KerbinSrfLanded", science = 5.0f }
            };
            GameStateStore.CommitScienceSubjects(batch1);

            var batch2 = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "crewReport@KerbinSrfLanded", science = 5.0f }
            };
            GameStateStore.CommitScienceSubjects(batch2);

            float sci;
            bool found = GameStateStore.TryGetCommittedSubjectScience("crewReport@KerbinSrfLanded", out sci);
            Assert.True(found);
            Assert.Equal(5.0f, sci);
            Assert.Equal(1, GameStateStore.CommittedScienceSubjectCount);
        }

        // ================================================================
        // RebuildCommittedScienceSubjects
        // ================================================================

        [Fact]
        public void RebuildCommittedScienceSubjects_ClearsAndRepopulates()
        {
            // Pre-populate with {X=5, Y=3}
            var initial = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "subjectX", science = 5.0f },
                new PendingScienceSubject { subjectId = "subjectY", science = 3.0f }
            };
            GameStateStore.CommitScienceSubjects(initial);
            Assert.Equal(2, GameStateStore.CommittedScienceSubjectCount);

            // Rebuild with only {X=5} — Y should be gone (simulates deleted recording)
            var rebuiltPairs = new List<KeyValuePair<string, float>>
            {
                new KeyValuePair<string, float>("subjectX", 5.0f)
            };
            GameStateStore.RebuildCommittedScienceSubjects(rebuiltPairs);

            Assert.Equal(1, GameStateStore.CommittedScienceSubjectCount);

            float sci;
            Assert.True(GameStateStore.TryGetCommittedSubjectScience("subjectX", out sci));
            Assert.Equal(5.0f, sci);

            Assert.False(GameStateStore.TryGetCommittedSubjectScience("subjectY", out sci));
        }

        [Fact]
        public void RebuildCommittedScienceSubjects_EmptyInput_ClearsAll()
        {
            // Pre-populate with two subjects
            var initial = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "subjectA", science = 10.0f },
                new PendingScienceSubject { subjectId = "subjectB", science = 7.5f }
            };
            GameStateStore.CommitScienceSubjects(initial);
            Assert.Equal(2, GameStateStore.CommittedScienceSubjectCount);

            // Rebuild with empty input — all subjects should be cleared
            var empty = new List<KeyValuePair<string, float>>();
            GameStateStore.RebuildCommittedScienceSubjects(empty);

            Assert.Equal(0, GameStateStore.CommittedScienceSubjectCount);

            float sci;
            Assert.False(GameStateStore.TryGetCommittedSubjectScience("subjectA", out sci));
            Assert.False(GameStateStore.TryGetCommittedSubjectScience("subjectB", out sci));
        }

        [Fact]
        public void RebuildCommittedScienceSubjects_LogsBeforeAndAfterCount()
        {
            ParsekLog.SuppressLogging = false;

            // Pre-populate with 2 subjects
            var initial = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "subjectP", science = 1.0f },
                new PendingScienceSubject { subjectId = "subjectQ", science = 2.0f }
            };
            GameStateStore.CommitScienceSubjects(initial);
            logLines.Clear();

            // Rebuild with 1 subject
            var pairs = new List<KeyValuePair<string, float>>
            {
                new KeyValuePair<string, float>("subjectP", 1.0f)
            };
            GameStateStore.RebuildCommittedScienceSubjects(pairs);

            Assert.Contains(logLines, l =>
                l.Contains("[GameStateStore]") &&
                l.Contains("RebuildCommittedScienceSubjects") &&
                l.Contains("before=2") &&
                l.Contains("rebuilt with 1"));
        }

        [Fact]
        public void RebuildCommittedScienceSubjects_OverwritesExistingValues()
        {
            // Pre-populate X=5
            var initial = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "subjectX", science = 5.0f }
            };
            GameStateStore.CommitScienceSubjects(initial);

            // Rebuild with X=8 (updated value from recalculation walk)
            var pairs = new List<KeyValuePair<string, float>>
            {
                new KeyValuePair<string, float>("subjectX", 8.0f)
            };
            GameStateStore.RebuildCommittedScienceSubjects(pairs);

            float sci;
            Assert.True(GameStateStore.TryGetCommittedSubjectScience("subjectX", out sci));
            Assert.Equal(8.0f, sci);
        }

        // ================================================================
        // GetCommittedScienceSubjectIds after rebuild
        // ================================================================

        [Fact]
        public void GetCommittedScienceSubjectIds_ReflectsRebuild()
        {
            // Populate with {A, B, C}
            var initial = new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "A", science = 1.0f },
                new PendingScienceSubject { subjectId = "B", science = 2.0f },
                new PendingScienceSubject { subjectId = "C", science = 3.0f }
            };
            GameStateStore.CommitScienceSubjects(initial);

            // Rebuild with only {A} — simulates deletion of recordings that had B and C
            var pairs = new List<KeyValuePair<string, float>>
            {
                new KeyValuePair<string, float>("A", 1.0f)
            };
            GameStateStore.RebuildCommittedScienceSubjects(pairs);

            var ids = GameStateStore.GetCommittedScienceSubjectIds();
            Assert.Single(ids);
            Assert.Contains("A", ids);
        }
    }
}
