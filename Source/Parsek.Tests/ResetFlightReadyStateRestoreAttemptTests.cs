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
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ClearCommittedTreeRestoreAttempt("test-teardown");
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RecordingTree Tree(string id)
        {
            return new RecordingTree { Id = id, TreeName = "ResetClone " + id };
        }

        /// <summary>
        /// A tree with ONE recording carrying <paramref name="endUT"/> as its explicit end,
        /// so the armed attempt builds a cutoff map entry for the recording id (an empty
        /// tree arms with zero cutoffs and the purge would find nothing to do).
        /// </summary>
        private static RecordingTree TreeWithRecording(
            string treeId, string recordingId, double endUT)
        {
            var rec = new Recording
            {
                RecordingId = recordingId,
                TreeId = treeId,
                VesselName = "Test Recording",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = endUT,
            };
            RecordingTree tree = Tree(treeId);
            tree.AddOrReplaceRecording(rec);
            return tree;
        }

        private static GameStateEvent MakeGameStateEvent(string key, double ut, string recordingId)
        {
            // TechResearched is not a resource event, so AddEvent never coalesces it and
            // two events on the same id stay two events (the two cells count events).
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.TechResearched,
                key = key,
                detail = "",
                recordingId = recordingId,
            };
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
                CloneTreeId, ResetReason, preserveIrreversibleTails: false);

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
                OtherTreeId, ResetReason, preserveIrreversibleTails: false);

            Assert.False(cleared);
            Assert.True(RecordingStore.IsCommittedTreeRestoreAttemptTree(CloneTreeId));
            Assert.DoesNotContain(logLines, l => l.Contains("reason=" + ResetReason));
        }

        [Fact]
        public void DroppedTree_WithNoAttemptArmed_IsANoOp()
        {
            bool cleared = RecordingStore.ClearCommittedTreeRestoreAttemptForDroppedTree(
                CloneTreeId, ResetReason, preserveIrreversibleTails: false);

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
                null, ResetReason, preserveIrreversibleTails: false);

            Assert.False(cleared);
            Assert.True(RecordingStore.IsCommittedTreeRestoreAttemptTree(CloneTreeId));
            Assert.DoesNotContain(logLines, l => l.Contains("reason=" + ResetReason));
        }

        // ===== the purge: the drop-site clear removes the attempt's event tails ======

        // catches: ClearCommittedTreeRestoreAttemptForDroppedTree clearing the attempt
        // WITHOUT purging its same-id post-cutoff game-state event tails. While the attempt
        // is armed those tails are kept off every save (the suppression predicate below
        // returns true for ut > cutoff), so the moment the clear nulls the cutoff map they
        // become durable - the clone's abandoned post-restore events would be written into
        // the next save by OnSave. Purge-then-clear, the same order DiscardPendingTree uses.
        [Fact]
        public void DroppedCloneTree_PurgesSameIdPostCutoffEventTails_ThenClears()
        {
            RecordingTree tree = TreeWithRecording(CloneTreeId, "rec-clone", endUT: 200.0);
            RecordingStore.ArmCommittedTreeRestoreAttempt(tree, ArmReason);

            GameStateEvent postCutoff = MakeGameStateEvent("tail-post", ut: 250.0,
                recordingId: "rec-clone");
            GameStateStore.AddEvent(ref postCutoff);
            GameStateEvent preCutoff = MakeGameStateEvent("tail-pre", ut: 150.0,
                recordingId: "rec-clone");
            GameStateStore.AddEvent(ref preCutoff);

            // The precondition that makes the purge load-bearing: while armed, only the
            // post-cutoff tail is held off disk, so only it can leak after the clear.
            Assert.True(RecordingStore.ShouldSuppressCommittedTreeRestoreAttemptEventPersistence(
                postCutoff));
            Assert.False(RecordingStore.ShouldSuppressCommittedTreeRestoreAttemptEventPersistence(
                preCutoff));

            bool cleared = RecordingStore.ClearCommittedTreeRestoreAttemptForDroppedTree(
                CloneTreeId, ResetReason, preserveIrreversibleTails: false);

            Assert.True(cleared);
            Assert.False(RecordingStore.IsCommittedTreeRestoreAttemptTree(CloneTreeId));
            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "tail-post");
            Assert.Contains(GameStateStore.Events, e => e.key == "tail-pre");
            // The purge must name THIS drop reason: a collected log then ties the tail
            // purge to this reset instead of to some generic purge site.
            Assert.Contains(logLines, l =>
                l.Contains("[GameStateStore]")
                && l.Contains("PurgeEventsForRecordingAfterUT (" + ResetReason + ")")
                && l.Contains("rec=rec-clone"));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("purged 1 same-id game-state event tail(s)"));
        }

        // catches: a drop-site clear for a DIFFERENT tree purging the armed attempt's
        // tails anyway - the tree-id match must gate the purge, not just the clear. A
        // purge for an attempt that stays armed would destroy state its suppression
        // contract is still hiding from every save.
        [Fact]
        public void DroppedOtherTree_PurgesNothing_AndKeepsTheAttemptArmed()
        {
            RecordingTree tree = TreeWithRecording(CloneTreeId, "rec-clone", endUT: 200.0);
            RecordingStore.ArmCommittedTreeRestoreAttempt(tree, ArmReason);

            GameStateEvent postCutoff = MakeGameStateEvent("tail-post", ut: 250.0,
                recordingId: "rec-clone");
            GameStateStore.AddEvent(ref postCutoff);

            bool cleared = RecordingStore.ClearCommittedTreeRestoreAttemptForDroppedTree(
                OtherTreeId, ResetReason, preserveIrreversibleTails: false);

            Assert.False(cleared);
            Assert.True(RecordingStore.IsCommittedTreeRestoreAttemptTree(CloneTreeId));
            Assert.Contains(GameStateStore.Events, e => e.key == "tail-post");
            Assert.DoesNotContain(logLines, l =>
                l.Contains("PurgeEventsForRecordingAfterUT") && l.Contains("rec=rec-clone"));
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
            // Comments stripped first: a needle that also appears in a comment near the
            // site (the comment explaining the guard says the same words) must not satisfy
            // this gate. StripCSharpComments is length- and line-preserving, and string
            // literals survive whole, so the exact-format assertion below still holds.
            string src = SourceScanText.StripCSharpComments(
                File.ReadAllText(LocateParsekFlightSource()));
            string body = ExtractMethodBody(src, "void ResetFlightReadyState()");

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

        // ===== wiring: the three other activeTree drop sites clear too =============
        //
        // These sites null activeTree WITHOUT stashing the tree as pending, so an armed
        // restore attempt would outlive its clone (every later OnSave then defers the
        // pending-event milestone flush). The clear reads activeTree?.Id, so it must run
        // inside the method BEFORE every activeTree = null teardown. They are private
        // instance methods on a live MonoBehaviour and cannot be driven headless, so this
        // is the same file-scrape gate convention as the cell above.

        // catches: the idle-on-pad scene-exit discard (SceneExitInterceptor) and the
        // automation DiscardTree verb reaching AutoDiscardActiveTreeCore with the clone
        // and dropping it while its attempt stays armed.
        [Fact]
        public void AutoDiscardActiveTreeCore_ClearsTheAttemptForTheDroppedTreeBeforeNulling()
        {
            AssertDropSiteClearsBeforeNulling(
                "void AutoDiscardActiveTreeCore(",
                "auto-discard dropped the restore clone");
        }

        // catches: a suppressed scene exit discarding the tree without a pending stash
        // and leaving its restore attempt armed.
        [Fact]
        public void DiscardActiveTreeForSuppressedSceneExit_ClearsTheAttemptForTheDroppedTreeBeforeNulling()
        {
            AssertDropSiteClearsBeforeNulling(
                "void DiscardActiveTreeForSuppressedSceneExit(",
                "suppressed scene exit dropped the restore clone");
        }

        // catches: StartRecording's bug #271 small-tree cleanup dropping the tree it
        // fails to start (possibly a restore clone with a single recording) with the
        // attempt still armed.
        [Fact]
        public void StartRecording_ClearsTheAttemptForTheDroppedTreeBeforeNulling()
        {
            AssertDropSiteClearsBeforeNulling(
                "void StartRecording(",
                "start-recording cleanup dropped the restore clone");
        }

        /// <summary>
        /// Source-order gate for one activeTree drop site: the method body holds the
        /// clear call with THIS reason, the call passes the tree being dropped
        /// (activeTree?.Id, read before the null), and the clear precedes EVERY
        /// activeTree = null; in the method, not just the first.
        /// </summary>
        private static void AssertDropSiteClearsBeforeNulling(string signature, string reason)
        {
            // Comments stripped, same as the cell above: the clear-callee and reason
            // needles must match code, not the comment that documents them.
            string src = SourceScanText.StripCSharpComments(
                File.ReadAllText(LocateParsekFlightSource()));
            string body = ExtractMethodBody(src, signature);

            const string clearCallee = "ClearCommittedTreeRestoreAttemptForDroppedTree(";
            int clearing = body.IndexOf(clearCallee, StringComparison.Ordinal);
            Assert.True(clearing >= 0,
                signature + " does not clear the restore attempt for the tree it drops - " +
                "a dropped clone would leave its attempt armed, so every later OnSave " +
                "defers the pending-event milestone flush.");
            Assert.Contains("\"" + reason + "\"", body);

            // Pin the argument that matters: the id of the tree being dropped, read from
            // activeTree before the null. Passing a constant, another tree's id or null
            // would keep the order cells green while the clear never fires.
            int callEnd = body.IndexOf(';', clearing);
            Assert.True(callEnd > clearing,
                signature + " has an unterminated clear call - update this gate.");
            Assert.Contains("activeTree?.Id",
                body.Substring(clearing, callEnd - clearing));

            const string nullMarker = "activeTree = null;";
            int nullCount = 0;
            for (int nulling = body.IndexOf(nullMarker, StringComparison.Ordinal);
                nulling >= 0;
                nulling = body.IndexOf(nullMarker, nulling + nullMarker.Length,
                    StringComparison.Ordinal))
            {
                nullCount++;
                Assert.True(clearing < nulling,
                    signature + " nulls activeTree (occurrence " + nullCount + ") " +
                    "before clearing the restore attempt for that tree - the clear reads " +
                    "activeTree?.Id, so it must run first.");
            }
            Assert.True(nullCount > 0,
                signature + " no longer nulls activeTree - update this gate.");
        }

        /// <summary>
        /// Returns the signature-through-closing-brace span of the method declared by
        /// <paramref name="signature"/>, brace-counted so nested blocks are included.
        /// </summary>
        private static string ExtractMethodBody(string src, string signature)
        {
            int methodStart = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(methodStart >= 0,
                signature + " not found in ParsekFlight.cs - did the method get renamed? " +
                "Update this regression test.");

            int openBrace = src.IndexOf('{', methodStart);
            Assert.True(openBrace >= 0,
                signature + " has no opening brace after its signature.");

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
                signature + " has unbalanced braces - ParsekFlight.cs is malformed.");

            return src.Substring(methodStart, closeBrace - methodStart + 1);
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
