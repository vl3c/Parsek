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
    /// either commits their science safely or, on failure, leaves the still-
    /// uncommitted subjects pending so the data is not lost."
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
            LedgerOrchestrator.OnRecordingCommittedPostSciencePersistFaultInjector = null;
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

        private static void SeedSubject(
            string subjectId,
            float science,
            double captureUT = double.NaN,
            string recordingId = "",
            string reasonKey = "")
        {
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = subjectId,
                science = science,
                subjectMaxValue = science + 10f,
                captureUT = captureUT,
                recordingId = recordingId,
                reasonKey = reasonKey
            });
        }

        [Fact]
        public void NotifyLedgerTreeCommitted_SingleRecording_SubjectsReadThenCleared()
        {
            SeedSubject("crewReport@KerbinSrfLanded", 2.5f, captureUT: 120.0, recordingId: "rec-solo");
            SeedSubject("temperatureScan@MunFlyingHigh", 4.2f, captureUT: 150.0, recordingId: "rec-solo");

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

            // Science actions from the pending subjects should have landed in the ledger —
            // exactly the seeded count, no duplication. Previously this test used `>= 2`,
            // which hid the tree-commit duplication until the codex review caught it.
            int scienceActions = Ledger.Actions.Count(a => a.Type == GameActionType.ScienceEarning);
            Assert.Equal(2, scienceActions);

            // And the pending list must now be empty.
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("cleared PendingScienceSubjects"));
        }

        [Fact]
        public void NotifyLedgerTreeCommitted_MultiRecording_DoesNotDuplicateSingleRecordingSubjects()
        {
            // Regression for codex review [P1] on PR #307: the previous implementation
            // had both recordings re-read PendingScienceSubjects and emit a full
            // ScienceEarning set each, causing ScienceModule to double-credit every
            // subject. The fix snapshots once and routes only the matching subset to
            // each recording, so a batch that all belongs to rec-B still lands once.
            SeedSubject("crewReport@KerbinSrfLanded", 2.5f, captureUT: 220.0, recordingId: "rec-B");
            SeedSubject("temperatureScan@MunFlyingHigh", 4.2f, captureUT: 250.0, recordingId: "rec-B");
            SeedSubject("barometerScan@KerbinInSpaceLow", 1.8f, captureUT: 280.0, recordingId: "rec-B");

            var recA = MakeRec("rec-A", 100.0, 200.0);
            var recB = MakeRec("rec-B", 200.0, 300.0);
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

            // Only rec-B should receive the 3 subjects, and rec-A must stay empty.
            Assert.Equal(0, recAScience);
            Assert.Equal(3, recBScience);

            // The total in the ledger must equal exactly the number of seeded subjects —
            // no duplication across recordings.
            int totalScienceActions = Ledger.Actions.Count(a => a.Type == GameActionType.ScienceEarning);
            Assert.Equal(3, totalScienceActions);

            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
        }

        [Fact]
        public void NotifyLedgerTreeCommitted_MultiRecording_RoutesMixedTaggedScienceWithoutDroppingEarlierSegments()
        {
            SeedSubject(
                "crewReport@KerbinSrfLanded",
                2.5f,
                captureUT: 150.0,
                recordingId: "rec-A",
                reasonKey: "ScienceTransmission");
            SeedSubject(
                "temperatureScan@MunFlyingHigh",
                4.2f,
                captureUT: 250.0,
                recordingId: "rec-B",
                reasonKey: "VesselRecovery");
            SeedSubject(
                "barometerScan@KerbinInSpaceLow",
                1.8f,
                captureUT: 150.0,
                recordingId: "rec-B",
                reasonKey: "ScienceTransmission");

            var recA = MakeRec("rec-A", 100.0, 200.0);
            var recB = MakeRec("rec-B", 200.0, 300.0);
            StageRecordings(recA, recB);

            var tree = new RecordingTree
            {
                Id = "tree-mixed",
                TreeName = "Mixed Tree",
                RootRecordingId = recA.RecordingId,
                ActiveRecordingId = recB.RecordingId
            };
            tree.Recordings[recA.RecordingId] = recA;
            tree.Recordings[recB.RecordingId] = recB;

            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);

            var scienceActions = Ledger.Actions
                .Where(a => a.Type == GameActionType.ScienceEarning)
                .OrderBy(a => a.RecordingId, StringComparer.Ordinal)
                .ThenBy(a => a.SubjectId, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(2, scienceActions.Count);
            Assert.Equal("rec-A", scienceActions[0].RecordingId);
            Assert.Equal("crewReport@KerbinSrfLanded", scienceActions[0].SubjectId);
            Assert.Equal(150.0f, scienceActions[0].StartUT);
            Assert.Equal("rec-B", scienceActions[1].RecordingId);
            Assert.Equal("temperatureScan@MunFlyingHigh", scienceActions[1].SubjectId);
            Assert.Equal(250.0f, scienceActions[1].StartUT);

            Assert.Equal(2, GameStateStore.CommittedScienceSubjectCount);
            float committedScience;
            Assert.True(GameStateStore.TryGetCommittedSubjectScience(
                "crewReport@KerbinSrfLanded",
                out committedScience));
            Assert.Equal(2.5f, committedScience, 0.001f);
            Assert.True(GameStateStore.TryGetCommittedSubjectScience(
                "temperatureScan@MunFlyingHigh",
                out committedScience));
            Assert.Equal(4.2f, committedScience, 0.001f);
            Assert.False(GameStateStore.TryGetCommittedSubjectScience(
                "barometerScan@KerbinInSpaceLow",
                out committedScience));
        }

        [Fact]
        public void NotifyLedgerTreeCommitted_SingleRecording_SkipsPreStartUntaggedScience()
        {
            SeedSubject(
                "crewReport@KerbinSrfLandedLaunchPad",
                2.5f,
                captureUT: 88.7,
                recordingId: "",
                reasonKey: "ScienceTransmission");
            SeedSubject(
                "temperatureScan@KerbinSrfLandedLaunchPad",
                4.2f,
                captureUT: 120.0,
                recordingId: "",
                reasonKey: "VesselRecovery");

            var rec = MakeRec("rec-528", 100.0, 200.0);
            StageRecordings(rec);

            var tree = new RecordingTree
            {
                Id = "tree-528",
                TreeName = "Issue 528",
                RootRecordingId = rec.RecordingId,
                ActiveRecordingId = rec.RecordingId
            };
            tree.Recordings[rec.RecordingId] = rec;

            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);

            var action = Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.ScienceEarning));
            Assert.Equal("temperatureScan@KerbinSrfLandedLaunchPad", action.SubjectId);
            Assert.Equal(120.0f, action.StartUT);

            Assert.Equal(1, GameStateStore.CommittedScienceSubjectCount);
            float committedScience;
            Assert.True(GameStateStore.TryGetCommittedSubjectScience(
                "temperatureScan@KerbinSrfLandedLaunchPad",
                out committedScience));
            Assert.Equal(4.2f, committedScience, 0.001f);
            Assert.False(GameStateStore.TryGetCommittedSubjectScience(
                "crewReport@KerbinSrfLandedLaunchPad",
                out committedScience));
        }

        [Fact]
        public void NotifyLedgerTreeCommitted_SingleRecording_SkipsPreStartTaggedScience()
        {
            SeedSubject(
                "crewReport@KerbinSrfLandedLaunchPad",
                2.5f,
                captureUT: 88.7,
                recordingId: "rec-528-tagged",
                reasonKey: "ScienceTransmission");
            SeedSubject(
                "temperatureScan@KerbinSrfLandedLaunchPad",
                4.2f,
                captureUT: 120.0,
                recordingId: "rec-528-tagged",
                reasonKey: "VesselRecovery");

            var rec = MakeRec("rec-528-tagged", 100.0, 200.0);
            StageRecordings(rec);

            var tree = new RecordingTree
            {
                Id = "tree-528-tagged",
                TreeName = "Issue 528 Tagged",
                RootRecordingId = rec.RecordingId,
                ActiveRecordingId = rec.RecordingId
            };
            tree.Recordings[rec.RecordingId] = rec;

            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);

            var action = Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.ScienceEarning));
            Assert.Equal("temperatureScan@KerbinSrfLandedLaunchPad", action.SubjectId);
            Assert.Equal(120.0f, action.StartUT);

            Assert.Equal(1, GameStateStore.CommittedScienceSubjectCount);
            float committedScience;
            Assert.True(GameStateStore.TryGetCommittedSubjectScience(
                "temperatureScan@KerbinSrfLandedLaunchPad",
                out committedScience));
            Assert.Equal(4.2f, committedScience, 0.001f);
            Assert.False(GameStateStore.TryGetCommittedSubjectScience(
                "crewReport@KerbinSrfLandedLaunchPad",
                out committedScience));
        }

        [Fact]
        public void NotifyLedgerTreeCommitted_SingleRecording_RoutesAllSubjects()
        {
            // A single-recording tree routes the whole batch to that lone recording.
            SeedSubject("temperatureScan@MunFlyingHigh", 4.2f, captureUT: 120.0, recordingId: "rec-lone");
            SeedSubject("barometerScan@KerbinInSpaceLow", 1.8f, captureUT: 150.0, recordingId: "rec-lone");

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
        public void NotifyLedgerTreeCommitted_OrchestratorThrows_RetainsPendingScience()
        {
            // Invariant: a pre-ledger throw must NOT permanently drop captured
            // PendingScienceSubjects. We use the fault injector at the very start of
            // OnRecordingCommitted, before conversion or mirroring runs, so the test
            // pins the exact failure path from the review finding.
            SeedSubject("crewReport@KerbinSrfLanded", 2.5f, captureUT: 120.0, recordingId: "rec-good");
            SeedSubject("temperatureScan@MunFlyingHigh", 4.2f, captureUT: 150.0, recordingId: "rec-good");

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

            // Because the throw happened before science reached either the ledger or
            // GameStateStore, the pending subjects must still remain visible afterward.
            Assert.Equal(2, GameStateRecorder.PendingScienceSubjects.Count);
            Assert.Equal(
                new[] { "crewReport@KerbinSrfLanded", "temperatureScan@MunFlyingHigh" },
                GameStateRecorder.PendingScienceSubjects.Select(s => s.subjectId).ToArray());
            Assert.Equal(0, GameStateStore.CommittedScienceSubjectCount);
            Assert.Empty(Ledger.Actions.Where(a => a.Type == GameActionType.ScienceEarning));

            // And the retention log line should have fired instead of a clear log.
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("retained PendingScienceSubjects after failure"));
            Assert.DoesNotContain(logLines,
                l => l.Contains("[LedgerOrchestrator]") && l.Contains("cleared PendingScienceSubjects"));
        }

        [Fact]
        public void NotifyLedgerTreeCommitted_PostSciencePersistThrow_DoesNotRetainPendingScience()
        {
            SeedSubject(
                "temperatureScan@MunFlyingHigh",
                4.2f,
                captureUT: 150.0,
                recordingId: "rec-safe",
                reasonKey: "ScienceTransmission");

            var rec = MakeRec("rec-safe", 100.0, 200.0);
            StageRecordings(rec);

            var tree = new RecordingTree
            {
                Id = "tree-safe",
                TreeName = "Safe Persist Tree",
                RootRecordingId = rec.RecordingId,
                ActiveRecordingId = rec.RecordingId
            };
            tree.Recordings[rec.RecordingId] = rec;

            var sentinel = new SentinelFaultException("forced after science persist");
            try
            {
                LedgerOrchestrator.OnRecordingCommittedPostSciencePersistFaultInjector =
                    _ => throw sentinel;

                var thrown = Assert.Throws<SentinelFaultException>(() =>
                    LedgerOrchestrator.NotifyLedgerTreeCommitted(tree));
                Assert.Same(sentinel, thrown);
            }
            finally
            {
                LedgerOrchestrator.OnRecordingCommittedPostSciencePersistFaultInjector = null;
            }

            Assert.Empty(GameStateRecorder.PendingScienceSubjects);

            var action = Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.ScienceEarning));
            Assert.Equal("temperatureScan@MunFlyingHigh", action.SubjectId);

            Assert.Equal(1, GameStateStore.CommittedScienceSubjectCount);
            float committedScience;
            Assert.True(GameStateStore.TryGetCommittedSubjectScience(
                "temperatureScan@MunFlyingHigh",
                out committedScience));
            Assert.Equal(4.2f, committedScience, 0.001f);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("already removed PendingScienceSubjects before failure"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("retained PendingScienceSubjects after failure"));
        }

        [Fact]
        public void NotifyLedgerTreeCommitted_UnrelatedSuccess_DoesNotWipeEarlierRetainedScience()
        {
            SeedSubject(
                "crewReport@KerbinSrfLanded",
                2.5f,
                captureUT: 150.0,
                recordingId: "rec-failed",
                reasonKey: "ScienceTransmission");

            var failedRec = MakeRec("rec-failed", 100.0, 200.0);
            StageRecordings(failedRec);

            var failedTree = new RecordingTree
            {
                Id = "tree-failed",
                TreeName = "Failed Tree",
                RootRecordingId = failedRec.RecordingId,
                ActiveRecordingId = failedRec.RecordingId
            };
            failedTree.Recordings[failedRec.RecordingId] = failedRec;

            var sentinel = new SentinelFaultException("forced for test");
            try
            {
                LedgerOrchestrator.OnRecordingCommittedFaultInjector = _ => throw sentinel;
                var thrown = Assert.Throws<SentinelFaultException>(() =>
                    LedgerOrchestrator.NotifyLedgerTreeCommitted(failedTree));
                Assert.Same(sentinel, thrown);
            }
            finally
            {
                LedgerOrchestrator.OnRecordingCommittedFaultInjector = null;
            }

            Assert.Single(GameStateRecorder.PendingScienceSubjects);
            Assert.Equal("crewReport@KerbinSrfLanded", GameStateRecorder.PendingScienceSubjects[0].subjectId);

            SeedSubject(
                "temperatureScan@MunFlyingHigh",
                4.2f,
                captureUT: 250.0,
                recordingId: "rec-success",
                reasonKey: "VesselRecovery");

            var successRec = MakeRec("rec-success", 200.0, 300.0);
            StageRecordings(successRec);

            var successTree = new RecordingTree
            {
                Id = "tree-success",
                TreeName = "Success Tree",
                RootRecordingId = successRec.RecordingId,
                ActiveRecordingId = successRec.RecordingId
            };
            successTree.Recordings[successRec.RecordingId] = successRec;

            LedgerOrchestrator.NotifyLedgerTreeCommitted(successTree);

            var committedAction = Assert.Single(Ledger.Actions.Where(a =>
                a.Type == GameActionType.ScienceEarning &&
                a.RecordingId == "rec-success"));
            Assert.Equal("temperatureScan@MunFlyingHigh", committedAction.SubjectId);

            Assert.Single(GameStateRecorder.PendingScienceSubjects);
            Assert.Equal("crewReport@KerbinSrfLanded", GameStateRecorder.PendingScienceSubjects[0].subjectId);

            Assert.Equal(1, GameStateStore.CommittedScienceSubjectCount);
            float committedScience;
            Assert.True(GameStateStore.TryGetCommittedSubjectScience(
                "temperatureScan@MunFlyingHigh",
                out committedScience));
            Assert.Equal(4.2f, committedScience, 0.001f);
            Assert.False(GameStateStore.TryGetCommittedSubjectScience(
                "crewReport@KerbinSrfLanded",
                out committedScience));
        }

        [Fact]
        public void FinalizeScopedPendingScienceCommit_UnrelatedSuccess_DoesNotWipeEarlierRetainedScience()
        {
            // CommitSegmentCore and FallbackCommitSplitRecorder both delegate to this helper
            // because the real standalone commit paths are not headless-testable without Unity.
            SeedSubject(
                "crewReport@KerbinSrfLanded",
                2.5f,
                captureUT: 150.0,
                recordingId: "rec-failed",
                reasonKey: "ScienceTransmission");

            IReadOnlyList<PendingScienceSubject> failedPending =
                LedgerOrchestrator.BuildPendingScienceSubsetForRecording(
                    GameStateRecorder.PendingScienceSubjects,
                    "rec-failed",
                    100.0,
                    200.0);
            LedgerOrchestrator.FinalizeScopedPendingScienceCommit(
                "Chain",
                "CommitSegmentCore",
                pendingBefore: GameStateRecorder.PendingScienceSubjects.Count,
                pendingForCommit: failedPending,
                commitSucceeded: false,
                scienceAddedToLedger: false);

            Assert.Single(GameStateRecorder.PendingScienceSubjects);
            Assert.Equal("crewReport@KerbinSrfLanded", GameStateRecorder.PendingScienceSubjects[0].subjectId);

            SeedSubject(
                "temperatureScan@MunFlyingHigh",
                4.2f,
                captureUT: 250.0,
                recordingId: "rec-success",
                reasonKey: "VesselRecovery");

            IReadOnlyList<PendingScienceSubject> successPending =
                LedgerOrchestrator.BuildPendingScienceSubsetForRecording(
                    GameStateRecorder.PendingScienceSubjects,
                    "rec-success",
                    200.0,
                    300.0);
            var scienceActions = GameStateEventConverter.ConvertScienceSubjects(
                successPending,
                "rec-success",
                200.0,
                300.0);
            var committedAction = Assert.Single(scienceActions);
            Assert.Equal("temperatureScan@MunFlyingHigh", committedAction.SubjectId);
            GameStateStore.CommitScienceActions(scienceActions);

            LedgerOrchestrator.FinalizeScopedPendingScienceCommit(
                "Chain",
                "CommitSegmentCore",
                pendingBefore: GameStateRecorder.PendingScienceSubjects.Count,
                pendingForCommit: successPending,
                commitSucceeded: true,
                scienceAddedToLedger: true);

            Assert.Single(GameStateRecorder.PendingScienceSubjects);
            Assert.Equal("crewReport@KerbinSrfLanded", GameStateRecorder.PendingScienceSubjects[0].subjectId);

            Assert.Equal(1, GameStateStore.CommittedScienceSubjectCount);
            float committedScience;
            Assert.True(GameStateStore.TryGetCommittedSubjectScience(
                "temperatureScan@MunFlyingHigh",
                out committedScience));
            Assert.Equal(4.2f, committedScience, 0.001f);
            Assert.False(GameStateStore.TryGetCommittedSubjectScience(
                "crewReport@KerbinSrfLanded",
                out committedScience));

            Assert.Contains(logLines, l =>
                l.Contains("[Chain]") &&
                l.Contains("CommitSegmentCore: preserved unrelated PendingScienceSubjects after success"));
        }

        [Fact]
        public void NotifyLedgerTreeCommitted_SecondRecordingThrows_LogsPartiallyRetainedPendingScience()
        {
            SeedSubject(
                "crewReport@KerbinSrfLanded",
                2.5f,
                captureUT: 150.0,
                recordingId: "rec-A",
                reasonKey: "ScienceTransmission");
            SeedSubject(
                "temperatureScan@MunFlyingHigh",
                4.2f,
                captureUT: 250.0,
                recordingId: "rec-B",
                reasonKey: "VesselRecovery");

            var recA = MakeRec("rec-A", 100.0, 200.0);
            var recB = MakeRec("rec-B", 200.0, 300.0);
            StageRecordings(recA, recB);

            var tree = new RecordingTree
            {
                Id = "tree-partial-failure",
                TreeName = "Partial Failure Tree",
                RootRecordingId = recA.RecordingId,
                ActiveRecordingId = recB.RecordingId
            };
            tree.Recordings[recA.RecordingId] = recA;
            tree.Recordings[recB.RecordingId] = recB;

            var subjectByRecording = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rec-A"] = "crewReport@KerbinSrfLanded",
                ["rec-B"] = "temperatureScan@MunFlyingHigh"
            };
            var scienceByRecording = new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["rec-A"] = 2.5f,
                ["rec-B"] = 4.2f
            };
            var invocationOrder = new List<string>();
            var sentinel = new SentinelFaultException("forced on second recording");

            try
            {
                LedgerOrchestrator.OnRecordingCommittedFaultInjector = recordingId =>
                {
                    invocationOrder.Add(recordingId);
                    if (invocationOrder.Count == 2)
                        throw sentinel;
                };

                var thrown = Assert.Throws<SentinelFaultException>(() =>
                    LedgerOrchestrator.NotifyLedgerTreeCommitted(tree));
                Assert.Same(sentinel, thrown);
            }
            finally
            {
                LedgerOrchestrator.OnRecordingCommittedFaultInjector = null;
            }

            Assert.Equal(2, invocationOrder.Count);
            string committedRecordingId = invocationOrder[0];
            string failedRecordingId = invocationOrder[1];
            string committedSubjectId = subjectByRecording[committedRecordingId];
            string failedSubjectId = subjectByRecording[failedRecordingId];

            var action = Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.ScienceEarning));
            Assert.Equal(committedRecordingId, action.RecordingId);
            Assert.Equal(committedSubjectId, action.SubjectId);

            Assert.Single(GameStateRecorder.PendingScienceSubjects);
            Assert.Equal(failedSubjectId, GameStateRecorder.PendingScienceSubjects[0].subjectId);

            Assert.Equal(1, GameStateStore.CommittedScienceSubjectCount);
            float committedScience;
            Assert.True(GameStateStore.TryGetCommittedSubjectScience(
                committedSubjectId,
                out committedScience));
            Assert.Equal(scienceByRecording[committedRecordingId], committedScience, 0.001f);
            Assert.False(GameStateStore.TryGetCommittedSubjectScience(
                failedSubjectId,
                out committedScience));

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") &&
                l.Contains("partially retained PendingScienceSubjects after failure"));
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

            // Clear log is NOT emitted when both pendingBefore and removedPending are zero
            // (noise reduction — see NotifyLedgerTreeCommitted impl).
            Assert.DoesNotContain(logLines,
                l => l.Contains("[LedgerOrchestrator]") && l.Contains("cleared PendingScienceSubjects"));
        }
    }
}
