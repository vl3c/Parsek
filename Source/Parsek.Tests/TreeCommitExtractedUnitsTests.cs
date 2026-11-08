using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the four units <c>LedgerOrchestrator.NotifyLedgerTreeCommitted</c> was
    /// split into (wave 2 W7b). The method end to end, faults included, is pinned by
    /// <see cref="NotifyLedgerTreeCommittedCharacterizationTests"/>.
    /// </summary>
    [Collection("Sequential")]
    public class TreeCommitExtractedUnitsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TreeCommitExtractedUnitsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            GameStateRecorder.PendingScienceSubjects.Clear();
            ParsekScenario.ResetInstanceForTesting();
            logLines.Clear();
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
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private sealed class SentinelFaultException : Exception
        {
            public SentinelFaultException(string msg) : base(msg) { }
        }

        private static Recording Rec(string id, double startUT, double endUT,
            string chainId = null, int chainIndex = -1)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Test " + id,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
            };
            if (chainId != null)
            {
                rec.ChainId = chainId;
                rec.ChainIndex = chainIndex;
            }
            RecordingStore.AddCommittedInternal(rec);
            return rec;
        }

        private static PendingScienceSubject Subject(string subjectId, float science, double captureUT,
            string recordingId)
        {
            var s = new PendingScienceSubject
            {
                subjectId = subjectId,
                science = science,
                subjectMaxValue = science + 10f,
                captureUT = captureUT,
                recordingId = recordingId,
                reasonKey = "ScienceTransmission"
            };
            GameStateRecorder.PendingScienceSubjects.Add(s);
            return s;
        }

        private const string Prefix = "[Parsek][VERBOSE][LedgerOrchestrator] NotifyLedgerTreeCommitted: ";

        // ---- LogTreeCommitPendingScienceOutcome ----------------------------------

        [Theory]
        [InlineData(true, 2, 2, 2, 0, "cleared PendingScienceSubjects (snapshot=2, routed=2, removed=2)")]
        [InlineData(true, 1, 0, 0, 0, "cleared PendingScienceSubjects (snapshot=1, routed=0, removed=0)")]
        [InlineData(true, 0, 0, 1, 0, "cleared PendingScienceSubjects (snapshot=0, routed=0, removed=1)")]
        [InlineData(true, 3, 1, 1, 2,
            "preserved unrelated PendingScienceSubjects after success (snapshot=3, routed=1, removed=1, remaining=2)")]
        [InlineData(true, 0, 0, 0, 4,
            "preserved unrelated PendingScienceSubjects after success (snapshot=0, routed=0, removed=0, remaining=4)")]
        [InlineData(false, 1, 1, 0, 1,
            "retained PendingScienceSubjects after failure (snapshot=1, routed=1, removed=0, remaining=1)")]
        [InlineData(false, 0, 0, 0, 0,
            "retained PendingScienceSubjects after failure (snapshot=0, routed=0, removed=0, remaining=0)")]
        [InlineData(false, 1, 1, 1, 0,
            "already removed PendingScienceSubjects before failure (snapshot=1, routed=1, removed=1, remaining=0)")]
        [InlineData(false, 3, 2, 1, 2,
            "partially retained PendingScienceSubjects after failure (snapshot=3, routed=2, removed=1, remaining=2)")]
        public void PendingOutcome_LogsOneLinePerBranch(bool succeeded, int before, int routed, int removed,
            int remaining, string expected)
        {
            LedgerOrchestrator.LogTreeCommitPendingScienceOutcome(succeeded, before, routed, removed, remaining);
            Assert.Equal(Prefix + expected, Assert.Single(logLines));
        }

        [Fact]
        public void PendingOutcome_SuccessThatTouchedNothing_IsSilent()
        {
            LedgerOrchestrator.LogTreeCommitPendingScienceOutcome(true, 0, 0, 0, 0);
            Assert.Empty(logLines);
        }

        // ---- WalkAfterFailedTreeCommit -------------------------------------------

        [Fact]
        public void FailedWalk_NothingFiled_DoesNotWalk()
        {
            LedgerOrchestrator.WalkAfterFailedTreeCommit(
                new List<LedgerOrchestrator.FiledRecordingCommit>(), 0);
            Assert.Empty(logLines);
        }

        [Fact]
        public void FailedWalk_RetiredRowsOnly_WalksWithTheFailedReason()
        {
            LedgerOrchestrator.WalkAfterFailedTreeCommit(
                new List<LedgerOrchestrator.FiledRecordingCommit>(), 1);
            Assert.Contains(logLines, l => l.Contains("reason=tree-commit-failed"));
            Assert.DoesNotContain(logLines, l => l.Contains("walk after a failed commit threw"));
        }

        // ---- FileTreeRecordingCommit ---------------------------------------------

        [Fact]
        public void FileOne_ClosesTheChainGap_FilesRows_AndRemovesItsScience()
        {
            var first = Rec("u-0", 100.0, 200.0, "chain-u", 0);
            var second = Rec("u-1", 220.0, 300.0, "chain-u", 1);
            var tree = new RecordingTree { Id = "u", TreeName = "Tree u" };
            tree.Recordings[first.RecordingId] = first;
            tree.Recordings[second.RecordingId] = second;
            var chainEndUTs = LedgerOrchestrator.BuildChainEndUtMap(tree);
            var subject = Subject("crewReport@KerbinFlyingHigh", 3.0f, 250.0, "u-1");
            var routing = new Dictionary<string, IReadOnlyList<PendingScienceSubject>>(StringComparer.Ordinal)
            {
                ["u-1"] = new List<PendingScienceSubject> { subject }
            };
            var filed = new List<LedgerOrchestrator.FiledRecordingCommit>();
            int removed = 5, gaps = 1, reFly = 2;

            LedgerOrchestrator.FileTreeRecordingCommit(second, chainEndUTs, routing,
                ReputationSeedOrigin.NotYetCaptured, filed, ref removed, ref gaps, ref reFly);

            var one = Assert.Single(filed);
            Assert.Equal("u-1", one.RecordingId);
            Assert.Equal(200.0, one.StartUT);
            Assert.Equal(300.0, one.EndUT);
            Assert.Equal(6, removed);
            Assert.Equal(2, gaps);
            Assert.Equal(2, reFly);
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
            Assert.Contains(logLines, l => l.Contains("Chain gap closure: extended startUT from 220.0 to 200.0"));
        }

        [Fact]
        public void FileOne_NoRoutedScience_LeavesUnrelatedPending()
        {
            var rec = Rec("v-a", 100.0, 200.0);
            Subject("seismicScan@MunSrfLanded", 6.0f, 130.0, "v-elsewhere");
            var filed = new List<LedgerOrchestrator.FiledRecordingCommit>();
            int removed = 0, gaps = 0, reFly = 0;

            LedgerOrchestrator.FileTreeRecordingCommit(rec, new Dictionary<string, double>(),
                new Dictionary<string, IReadOnlyList<PendingScienceSubject>>(StringComparer.Ordinal),
                ReputationSeedOrigin.NotYetCaptured, filed, ref removed, ref gaps, ref reFly);

            Assert.Single(filed);
            Assert.Equal(0, removed);
            Assert.Equal(0, gaps);
            Assert.Single(GameStateRecorder.PendingScienceSubjects);
        }

        [Fact]
        public void FileOne_FaultBeforeTheScience_KeepsItPending_AndPropagates()
        {
            var rec = Rec("w-a", 100.0, 200.0);
            var subject = Subject("crewReport@KerbinSrfLanded", 2.5f, 120.0, "w-a");
            var routing = new Dictionary<string, IReadOnlyList<PendingScienceSubject>>(StringComparer.Ordinal)
            {
                ["w-a"] = new List<PendingScienceSubject> { subject }
            };
            var filed = new List<LedgerOrchestrator.FiledRecordingCommit>();
            int removed = 0, gaps = 0, reFly = 0;
            LedgerOrchestrator.OnRecordingCommittedFaultInjector = _ => throw new SentinelFaultException("pre");

            var ex = Assert.Throws<SentinelFaultException>(() =>
                LedgerOrchestrator.FileTreeRecordingCommit(rec, new Dictionary<string, double>(), routing,
                    ReputationSeedOrigin.NotYetCaptured, filed, ref removed, ref gaps, ref reFly));

            Assert.Equal("pre", ex.Message);
            Assert.Empty(filed);
            Assert.Equal(0, removed);
            Assert.Single(GameStateRecorder.PendingScienceSubjects);
        }

        [Fact]
        public void FileOne_FaultAfterTheSciencePersisted_RemovesIt_AndPropagates()
        {
            var rec = Rec("x-a", 100.0, 200.0);
            var subject = Subject("temperatureScan@MunFlyingHigh", 4.2f, 150.0, "x-a");
            var routing = new Dictionary<string, IReadOnlyList<PendingScienceSubject>>(StringComparer.Ordinal)
            {
                ["x-a"] = new List<PendingScienceSubject> { subject }
            };
            var filed = new List<LedgerOrchestrator.FiledRecordingCommit>();
            int removed = 0, gaps = 0, reFly = 0;
            LedgerOrchestrator.OnRecordingCommittedPostSciencePersistFaultInjector =
                _ => throw new SentinelFaultException("post");

            Assert.Throws<SentinelFaultException>(() =>
                LedgerOrchestrator.FileTreeRecordingCommit(rec, new Dictionary<string, double>(), routing,
                    ReputationSeedOrigin.NotYetCaptured, filed, ref removed, ref gaps, ref reFly));

            Assert.Empty(filed);
            Assert.Equal(1, removed);
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
        }

        // ---- WalkAndCompleteTreeCommit -------------------------------------------

        [Fact]
        public void WalkAndComplete_NothingFiled_IsSilent()
        {
            var tree = new RecordingTree { Id = "y", TreeName = "Tree y" };
            LedgerOrchestrator.WalkAndCompleteTreeCommit(tree, new Dictionary<string, double>(),
                new List<LedgerOrchestrator.FiledRecordingCommit>(), 0);
            Assert.Empty(logLines);
        }

        [Fact]
        public void WalkAndComplete_FiledRows_NamesTheTreeAndWalksOnce()
        {
            var rec = Rec("z-a", 100.0, 200.0);
            var tree = new RecordingTree { Id = "z", TreeName = "Tree z" };
            tree.Recordings[rec.RecordingId] = rec;
            var filed = new List<LedgerOrchestrator.FiledRecordingCommit>();
            int removed = 0, gaps = 0, reFly = 0;
            LedgerOrchestrator.FileTreeRecordingCommit(rec, new Dictionary<string, double>(),
                new Dictionary<string, IReadOnlyList<PendingScienceSubject>>(StringComparer.Ordinal),
                ReputationSeedOrigin.NotYetCaptured, filed, ref removed, ref gaps, ref reFly);
            var chainEndUTs = new Dictionary<string, double> { ["c:0"] = 1.0, ["c:1"] = 2.0 };
            logLines.Clear();

            LedgerOrchestrator.WalkAndCompleteTreeCommit(tree, chainEndUTs, filed, 0);

            Assert.Equal(
                "[Parsek][INFO][LedgerOrchestrator] NotifyLedgerTreeCommitted: filed rows for 1 recording(s) "
                + "of tree 'Tree z' before one ledger walk (chainSegments=2)",
                logLines[0]);
            Assert.Contains(logLines, l => l.Contains("reason=tree-commit"));
            Assert.DoesNotContain(logLines, l => l.Contains("reason=tree-commit-failed"));
        }
    }
}
