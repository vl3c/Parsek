using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RESET-FLIGHT-READY-STATE-LEAVES-RESTORE-ATTEMPT-ARMED: ResetFlightReadyState drops
    /// the live activeTree on flight ready. When that tree is the clone the armed
    /// committed-tree restore attempt guards (same tree id), the attempt must be cleared
    /// with it through the same clear path the no-op revert uses; a stale armed attempt
    /// keeps deferring the OnSave pending-event milestone flush and the next arm logs a
    /// replacing-stale-context warn. An attempt for a different tree id is never cleared.
    /// </summary>
    [Collection("Sequential")]
    public class ResetFlightReadyStateRestoreAttemptTests : IDisposable
    {
        private const string CloneTreeId = "tree-reset-clone";
        private const string OtherTreeId = "tree-other";
        private const string ArmReason = "test-arm";
        private const string ResetReason = "flight-ready reset dropped the restore clone";

        private readonly List<string> logLines = new List<string>();

        public ResetFlightReadyStateRestoreAttemptTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
            RecordingStore.ClearCommittedTreeRestoreAttempt("test-setup");
        }

        public void Dispose()
        {
            RecordingStore.ClearCommittedTreeRestoreAttempt("test-teardown");
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RecordingTree Tree(string id)
        {
            return new RecordingTree { Id = id, TreeName = "ResetClone " + id };
        }

        // ===== the pure "is this dropped tree the attempt's clone" decision ==========

        [Fact]
        public void PureDecision_SameTreeId_MatchesTheArmedClone()
        {
            Assert.True(RecordingStore.DroppedTreeMatchesArmedRestoreAttempt(
                CloneTreeId, CloneTreeId));
        }

        [Fact]
        public void PureDecision_DifferentTreeIds_DoNotMatch()
        {
            Assert.False(RecordingStore.DroppedTreeMatchesArmedRestoreAttempt(
                CloneTreeId, OtherTreeId));
            Assert.False(RecordingStore.DroppedTreeMatchesArmedRestoreAttempt(
                OtherTreeId, CloneTreeId));
        }

        [Theory]
        [InlineData(null, "tree-a")]
        [InlineData("", "tree-a")]
        [InlineData("tree-a", null)]
        [InlineData("tree-a", "")]
        public void PureDecision_MissingIdOnEitherSide_NeverMatches(
            string armedAttemptTreeId, string droppedTreeId)
        {
            Assert.False(RecordingStore.DroppedTreeMatchesArmedRestoreAttempt(
                armedAttemptTreeId, droppedTreeId));
        }

        // ===== the clear path at the drop site =====================================

        // catches: the flight-ready reset dropping the clone tree while its restore
        // attempt stays armed (the EVA-6 boot log: every later OnSave deferred the
        // pending-event milestone flush, then the next arm warned replacing-stale-context).
        [Fact]
        public void DroppedCloneTree_ClearsTheAttempt_AndLogsTheDecisionAndTheClear()
        {
            RecordingStore.ArmCommittedTreeRestoreAttempt(Tree(CloneTreeId), ArmReason);

            bool cleared = RecordingStore.ClearCommittedTreeRestoreAttemptForDroppedTree(
                CloneTreeId, ResetReason);

            Assert.True(cleared);
            Assert.False(RecordingStore.IsCommittedTreeRestoreAttemptTree(CloneTreeId));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("ClearCommittedTreeRestoreAttemptForDroppedTree")
                && l.Contains("tree=" + CloneTreeId)
                && l.Contains("reason=" + ResetReason));
            Assert.Contains(logLines, l =>
                l.Contains("Cleared committed-tree restore attempt")
                && l.Contains("tree=" + CloneTreeId)
                && l.Contains("reason=" + ResetReason)
                && l.Contains("armedReason=" + ArmReason));
        }

        // catches: a reset dropping some OTHER tree clearing an attempt that belongs to
        // a different tree id (the fix must only clear its own clone).
        [Fact]
        public void DroppedOtherTree_LeavesTheAttemptArmedAndLogsNothing()
        {
            RecordingStore.ArmCommittedTreeRestoreAttempt(Tree(CloneTreeId), ArmReason);

            bool cleared = RecordingStore.ClearCommittedTreeRestoreAttemptForDroppedTree(
                OtherTreeId, ResetReason);

            Assert.False(cleared);
            Assert.True(RecordingStore.IsCommittedTreeRestoreAttemptTree(CloneTreeId));
            Assert.DoesNotContain(logLines, l => l.Contains("reason=" + ResetReason));
        }

        [Fact]
        public void DroppedTree_WithNoAttemptArmed_IsANoOp()
        {
            bool cleared = RecordingStore.ClearCommittedTreeRestoreAttemptForDroppedTree(
                CloneTreeId, ResetReason);

            Assert.False(cleared);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Cleared committed-tree restore attempt")
                && l.Contains("reason=" + ResetReason));
        }

        [Fact]
        public void DroppedTreeNull_WithAttemptArmed_IsANoOp()
        {
            RecordingStore.ArmCommittedTreeRestoreAttempt(Tree(CloneTreeId), ArmReason);

            bool cleared = RecordingStore.ClearCommittedTreeRestoreAttemptForDroppedTree(
                null, ResetReason);

            Assert.False(cleared);
            Assert.True(RecordingStore.IsCommittedTreeRestoreAttemptTree(CloneTreeId));
            Assert.DoesNotContain(logLines, l => l.Contains("reason=" + ResetReason));
        }

        // ===== wiring: ResetFlightReadyState clears before it nulls ================
        //
        // The clear reads activeTree?.Id, so it must run inside ResetFlightReadyState
        // BEFORE the activeTree = null teardown. The method is a private instance method
        // on a live MonoBehaviour and cannot be driven headless, so this is the same
        // file-scrape gate convention as OnFlightReadyOrderingTests.

        [Fact]
        public void ResetFlightReadyState_ClearsTheAttemptForTheDroppedTreeBeforeNulling()
        {
            string src = File.ReadAllText(LocateParsekFlightSource());

            int methodStart = src.IndexOf(
                "void ResetFlightReadyState()", StringComparison.Ordinal);
            Assert.True(methodStart >= 0,
                "ResetFlightReadyState() not found in ParsekFlight.cs - " +
                "did the method get renamed? Update this regression test.");

            int openBrace = src.IndexOf('{', methodStart);
            Assert.True(openBrace >= 0,
                "ResetFlightReadyState() has no opening brace after its signature.");

            int depth = 0;
            int closeBrace = -1;
            for (int i = openBrace; i < src.Length; i++)
            {
                char c = src[i];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) { closeBrace = i; break; }
                }
            }
            Assert.True(closeBrace >= 0,
                "ResetFlightReadyState() has unbalanced braces - ParsekFlight.cs is malformed.");

            string body = src.Substring(methodStart, closeBrace - methodStart + 1);

            int clearing = body.IndexOf(
                "ClearCommittedTreeRestoreAttemptForDroppedTree(", StringComparison.Ordinal);
            int nulling = body.IndexOf("activeTree = null;", StringComparison.Ordinal);

            Assert.True(clearing >= 0,
                "ResetFlightReadyState() does not clear the restore attempt for the tree " +
                "it drops - a dropped clone would leave its attempt armed (todo " +
                "RESET-FLIGHT-READY-STATE-LEAVES-RESTORE-ATTEMPT-ARMED).");
            Assert.True(nulling >= 0,
                "ResetFlightReadyState() no longer nulls activeTree - update this gate.");
            Assert.True(clearing < nulling,
                "the restore-attempt clear must run BEFORE activeTree = null; it reads " +
                "activeTree?.Id to decide whether the dropped tree is the attempt's clone.");
            Assert.Contains("\"" + ResetReason + "\"", body);
            // The argument must be the tree being dropped; passing null or any other id
            // would keep every other cell green while the clear silently never fires.
            Assert.Contains("ClearCommittedTreeRestoreAttemptForDroppedTree(\n                activeTree?.Id,",
                body.Replace("\r\n", "\n"));
        }

        /// <summary>
        /// Walks up from the current test binary directory looking for
        /// Source/Parsek/ParsekFlight.cs so the file-scrape gate works regardless of
        /// where dotnet test is invoked from (same walk as OnFlightReadyOrderingTests).
        /// </summary>
        private static string LocateParsekFlightSource()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = Path.Combine(dir, "Source", "Parsek", "ParsekFlight.cs");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "Parsek", "ParsekFlight.cs"));
        }
    }
}
