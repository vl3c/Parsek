using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers §C/#397 of career-earnings-bundle plan: PendingScienceSubjects must
    /// survive until the orchestrator has read them for every recording in the
    /// commit batch. The old code cleared the list inside RecordingStore BEFORE
    /// LedgerOrchestrator.NotifyLedgerTreeCommitted ran, so ConvertScienceSubjects
    /// always saw zero subjects and no ScienceEarning actions landed.
    ///
    /// The new invariant:
    /// "If PendingScienceSubjects were populated during recording, they remain
    /// readable until NotifyLedgerTreeCommitted (or CommitSegmentCore for chains)
    /// clears them in a try/finally AFTER the orchestrator runs. The clear happens
    /// even if OnRecordingCommitted throws."
    /// </summary>
    [Collection("Sequential")]
    public class PendingScienceSubjectsClearTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PendingScienceSubjectsClearTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            GameStateRecorder.PendingScienceSubjects.Clear();
        }

        public void Dispose()
        {
            LedgerOrchestrator.OnRecordingCommittedFaultInjector = null;
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            GameStateRecorder.PendingScienceSubjects.Clear();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private sealed class SentinelFaultException : Exception
        {
            public SentinelFaultException(string msg) : base(msg) { }
        }

        private static Recording MakeRec(string id, double startUT, double endUT)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Test " + id,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT
            };
        }

        private static void StageRecordings(params Recording[] recs)
        {
            foreach (var r in recs)
                RecordingStore.AddCommittedInternal(r);
        }

        private static void SeedSubject(string subjectId, float science)
        {
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = subjectId,
                science = science,
                subjectMaxValue = science + 10f
            });
        }

        [Fact]
        public void NotifyLedgerTreeCommitted_SingleRecording_SubjectsReadThenCleared()
        {
            SeedSubject("crewReport@KerbinSrfLanded", 2.5f);
            SeedSubject("temperatureScan@MunFlyingHigh", 4.2f);

            var rec = MakeRec("rec-solo", 100.0, 200.0);
            StageRecordings(rec);

            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "Solo Tree",
                RootRecordingId = rec.RecordingId,
                ActiveRecordingId = rec.RecordingId
            };
            tree.Recordings[rec.RecordingId] = rec;

            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);

            // Science actions from the pending subjects should have landed in the ledger.
            int scienceActions = Ledger.Actions.Count(a => a.Type == GameActionType.ScienceEarning);
            Assert.True(scienceActions >= 2,
                $"expected >=2 ScienceEarning actions, got {scienceActions}");

            // And the pending list must now be empty.
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("cleared PendingScienceSubjects"));
        }

        [Fact]
        public void NotifyLedgerTreeCommitted_MultiRecording_OnlyOneRecordingAbsorbsSubjects()
        {
            // Regression for codex review [P1] on PR #307: the previous implementation
            // had both recordings re-read PendingScienceSubjects and emit a full
            // ScienceEarning set each, causing ScienceModule to double-credit every
            // subject. The fix: NotifyLedgerTreeCommitted snapshots once and picks
            // exactly one recording (highest EndUT) to own the batch; siblings receive
            // the empty sentinel.
            SeedSubject("crewReport@KerbinSrfLanded", 2.5f);
            SeedSubject("temperatureScan@MunFlyingHigh", 4.2f);
            SeedSubject("barometerScan@KerbinInSpaceLow", 1.8f);

            var recA = MakeRec("rec-A", 100.0, 200.0);
            var recB = MakeRec("rec-B", 200.0, 300.0);  // higher EndUT -> owner
            StageRecordings(recA, recB);

            var tree = new RecordingTree
            {
                Id = "tree-multi",
                TreeName = "Multi Tree",
                RootRecordingId = recA.RecordingId,
                ActiveRecordingId = recB.RecordingId
            };
            tree.Recordings[recA.RecordingId] = recA;
            tree.Recordings[recB.RecordingId] = recB;

            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);

            int recAScience = Ledger.Actions.Count(a =>
                a.Type == GameActionType.ScienceEarning && a.RecordingId == "rec-A");
            int recBScience = Ledger.Actions.Count(a =>
                a.Type == GameActionType.ScienceEarning && a.RecordingId == "rec-B");

            // Only the owner (rec-B, higher EndUT) absorbs the 3 subjects.
            Assert.Equal(0, recAScience);
            Assert.Equal(3, recBScience);

            // The total in the ledger must equal exactly the number of seeded subjects —
            // no duplication across recordings.
            int totalScienceActions = Ledger.Actions.Count(a => a.Type == GameActionType.ScienceEarning);
            Assert.Equal(3, totalScienceActions);

            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
        }

        [Fact]
        public void PickScienceOwnerRecordingId_PicksHighestEndUT()
        {
            var recA = MakeRec("rec-A", 100.0, 200.0);
            var recB = MakeRec("rec-B", 200.0, 500.0);  // highest EndUT
            var recC = MakeRec("rec-C", 500.0, 450.0);

            var tree = new RecordingTree
            {
                Id = "t",
                ActiveRecordingId = "rec-A"
            };
            tree.Recordings[recA.RecordingId] = recA;
            tree.Recordings[recB.RecordingId] = recB;
            tree.Recordings[recC.RecordingId] = recC;

            Assert.Equal("rec-B", LedgerOrchestrator.PickScienceOwnerRecordingId(tree));
        }

        [Fact]
        public void PickScienceOwnerRecordingId_EmptyTree_ReturnsNull()
        {
            var tree = new RecordingTree { Id = "empty" };
            Assert.Null(LedgerOrchestrator.PickScienceOwnerRecordingId(tree));
        }

        [Fact]
        public void PickScienceOwnerRecordingId_NullTree_ReturnsNull()
        {
            Assert.Null(LedgerOrchestrator.PickScienceOwnerRecordingId(null));
        }

        [Fact]
        public void PickScienceOwnerRecordingId_TieBreaksOnActiveRecording()
        {
            // Two recordings share the highest EndUT — the active one wins.
            var recA = MakeRec("rec-A", 100.0, 300.0);
            var recB = MakeRec("rec-B", 200.0, 300.0);  // tied on EndUT and is active

            var tree = new RecordingTree
            {
                Id = "t",
                ActiveRecordingId = "rec-B"
            };
            tree.Recordings[recA.RecordingId] = recA;
            tree.Recordings[recB.RecordingId] = recB;

            Assert.Equal("rec-B", LedgerOrchestrator.PickScienceOwnerRecordingId(tree));
        }

        [Fact]
        public void NotifyLedgerTreeCommitted_SingleRecording_OwnerAbsorbsSubjects()
        {
            // A single-recording tree: the sole recording IS the owner, and absorbs all
            // subjects. No sibling comparison is needed — this is the degenerate case of
            // the multi-recording attribution logic.
            SeedSubject("temperatureScan@MunFlyingHigh", 4.2f);
            SeedSubject("barometerScan@KerbinInSpaceLow", 1.8f);

            var rec = MakeRec("rec-lone", 100.0, 200.0);
            StageRecordings(rec);

            var tree = new RecordingTree
            {
                Id = "tree-lone",
                TreeName = "Lone Tree",
                RootRecordingId = rec.RecordingId,
                ActiveRecordingId = rec.RecordingId
            };
            tree.Recordings[rec.RecordingId] = rec;

            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);

            int scienceActions = Ledger.Actions.Count(a =>
                a.Type == GameActionType.ScienceEarning && a.RecordingId == "rec-lone");
            Assert.Equal(2, scienceActions);
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
        }

        [Fact]
        public void NotifyLedgerTreeCommitted_OrchestratorThrows_StillClears()
        {
            // Invariant: the try/finally inside NotifyLedgerTreeCommitted clears
            // PendingScienceSubjects even when OnRecordingCommitted throws. We use
            // the LedgerOrchestrator.OnRecordingCommittedFaultInjector test hook to
            // force a throw at the very start of OnRecordingCommitted, so the
            // invariant is exercised against real code (not asserted by inspection).
            SeedSubject("crewReport@KerbinSrfLanded", 2.5f);
            SeedSubject("temperatureScan@MunFlyingHigh", 4.2f);

            var recGood = MakeRec("rec-good", 100.0, 200.0);
            StageRecordings(recGood);

            var tree = new RecordingTree
            {
                Id = "tree-throw",
                TreeName = "Throw Tree",
                RootRecordingId = recGood.RecordingId,
                ActiveRecordingId = recGood.RecordingId
            };
            tree.Recordings[recGood.RecordingId] = recGood;

            var sentinel = new SentinelFaultException("forced for test");
            try
            {
                LedgerOrchestrator.OnRecordingCommittedFaultInjector = _ => throw sentinel;

                // The sentinel exception must bubble out of NotifyLedgerTreeCommitted —
                // the method does not swallow OnRecordingCommitted throws.
                var thrown = Assert.Throws<SentinelFaultException>(() =>
                    LedgerOrchestrator.NotifyLedgerTreeCommitted(tree));
                Assert.Same(sentinel, thrown);
            }
            finally
            {
                LedgerOrchestrator.OnRecordingCommittedFaultInjector = null;
            }

            // Even though OnRecordingCommitted threw at its entry point, the
            // finally block must have cleared PendingScienceSubjects.
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);

            // And the clear log line should have fired.
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("cleared PendingScienceSubjects"));
        }

        [Fact]
        public void NotifyLedgerTreeCommitted_NullTree_NoOp()
        {
            SeedSubject("crewReport", 1f);

            LedgerOrchestrator.NotifyLedgerTreeCommitted(null);

            // Null tree short-circuits; the subjects are still pending because
            // no orchestrator ran.
            Assert.Single(GameStateRecorder.PendingScienceSubjects);
        }

        [Fact]
        public void NotifyLedgerTreeCommitted_EmptyList_Silent()
        {
            var recA = MakeRec("rec-empty", 100.0, 200.0);
            StageRecordings(recA);

            var tree = new RecordingTree
            {
                Id = "tree-empty",
                TreeName = "Empty Tree",
                RootRecordingId = recA.RecordingId,
                ActiveRecordingId = recA.RecordingId
            };
            tree.Recordings[recA.RecordingId] = recA;

            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);

            // No subjects to begin with, no subjects after.
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);

            // Clear log is NOT emitted when both pendingBefore and cleared are zero
            // (noise reduction — see NotifyLedgerTreeCommitted impl).
            Assert.DoesNotContain(logLines,
                l => l.Contains("cleared PendingScienceSubjects") && l.Contains("atClear="));
        }
    }
}
