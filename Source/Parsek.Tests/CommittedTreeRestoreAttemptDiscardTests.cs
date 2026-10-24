using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// DiscardCommittedTreeRestoreAttempt: the shared purge-then-clear discard every
    /// caller-side drop site uses when it throws away the clone tree the armed
    /// committed-tree restore attempt guards. A bare clear at such a site lifts the
    /// attempt's event-suppression without purging, so the clone's suppressed
    /// same-id post-cutoff game-state events become durable on the next save. Cells:
    /// the helper's two behaviours (armed: purge then clear; unarmed: no-op), and a
    /// source gate pinning the three discard call sites (the no-op no-session
    /// resume revert, the switch-segment scoped discard, the pre-switch dialog
    /// discard) to this helper rather than the bare clear.
    /// </summary>
    [Collection("Sequential")]
    public class CommittedTreeRestoreAttemptDiscardTests : IDisposable
    {
        private const string CloneTreeId = "tree-discard-clone";
        private const string ArmReason = "test-arm";
        private const string DiscardReason = "test-discard";

        private readonly List<string> logLines = new List<string>();

        public CommittedTreeRestoreAttemptDiscardTests()
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
            return new RecordingTree { Id = id, TreeName = "DiscardClone " + id };
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
            // two events on the same id stay two events (the cells count events).
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.TechResearched,
                key = key,
                detail = "",
                recordingId = recordingId,
            };
        }

        // ===== the helper's behaviour =================================================

        // catches: the three caller-side discard sites using the bare clear (or the
        // helper clearing WITHOUT purging). While the attempt is armed its same-id
        // post-cutoff tails are kept off every save, so the moment the clear nulls the
        // cutoff map they become durable - the clone's abandoned post-restore events
        // would be written into the next save by OnSave. The absent tail-post below is
        // also the purge-BEFORE-clear proof: a clear first would null the cutoff map
        // and the purge would find nothing to do.
        [Fact]
        public void DiscardHelper_ArmedAttempt_PurgesSameIdPostCutoffEventTails_ThenClears()
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

            RecordingStore.DiscardCommittedTreeRestoreAttempt(DiscardReason, preserveIrreversibleTails: false);

            Assert.False(RecordingStore.IsCommittedTreeRestoreAttemptTree(CloneTreeId));
            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "tail-post");
            Assert.Contains(GameStateStore.Events, e => e.key == "tail-pre");
            // The purge must name THIS discard reason: a collected log then ties the tail
            // purge to this site instead of to some generic purge site.
            Assert.Contains(logLines, l =>
                l.Contains("[GameStateStore]")
                && l.Contains("PurgeEventsForRecordingAfterUT (" + DiscardReason + ")")
                && l.Contains("rec=rec-clone"));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("purged 1 same-id game-state event tail(s)")
                && l.Contains("tree=" + CloneTreeId));
            Assert.Contains(logLines, l =>
                l.Contains("Cleared committed-tree restore attempt")
                && l.Contains("tree=" + CloneTreeId)
                && l.Contains("reason=" + DiscardReason)
                && l.Contains("armedReason=" + ArmReason));
        }

        // catches: the helper purging (or logging a clear) when no attempt is armed -
        // the three discard sites call it unconditionally, so a non-no-op would purge
        // by cutoff map left over from nothing / emit a clear line naming an attempt
        // that does not exist.
        [Fact]
        public void DiscardHelper_NoAttemptArmed_IsANoOp()
        {
            GameStateEvent orphan = MakeGameStateEvent("orphan", ut: 250.0,
                recordingId: "rec-clone");
            GameStateStore.AddEvent(ref orphan);

            RecordingStore.DiscardCommittedTreeRestoreAttempt(DiscardReason, preserveIrreversibleTails: false);

            Assert.Contains(GameStateStore.Events, e => e.key == "orphan");
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Cleared committed-tree restore attempt")
                && l.Contains("reason=" + DiscardReason));
            Assert.DoesNotContain(logLines, l => l.Contains("PurgeEventsForRecordingAfterUT"));
        }

        // catches: a restore attempt armed by the flight that was live before a Re-Fly's
        // rewind staying armed through the session (CI-3): every save during the session
        // would treat the session's committed recordings as that stale attempt's members.
        [Fact]
        public void ReFlyStart_DiscardsTheRestoreAttemptOfThePreRewindFlight()
        {
            RecordingTree tree = TreeWithRecording(CloneTreeId, "rec-clone", endUT: 200.0);
            RecordingStore.ArmCommittedTreeRestoreAttempt(tree, ArmReason);
            GameStateEvent postCutoff = MakeGameStateEvent("tail-post", ut: 250.0,
                recordingId: "rec-clone");
            GameStateStore.AddEvent(ref postCutoff);

            Assert.True(RewindInvoker.DiscardRestoreAttemptOfAbandonedFlight("sess_t"));

            Assert.False(RecordingStore.IsCommittedTreeRestoreAttemptTree(CloneTreeId));
            Assert.False(RecordingStore.IsCommittedTreeRestoreAttemptRecordingId("rec-clone"));
            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "tail-post");
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("discarding the committed-tree restore attempt of the pre-rewind flight")
                && l.Contains("tree=" + CloneTreeId) && l.Contains("sess=sess_t"));

            Assert.False(RewindInvoker.DiscardRestoreAttemptOfAbandonedFlight("sess_t"));
        }

        // catches: the discard moving before the bundle restore (which would put the
        // purged tails back) or after the strip / activate / marker write.
        [Fact]
        public void ConsumePostLoad_DiscardsTheAttempt_AfterTheBundleRestore_BeforeTheStrip()
        {
            string body = ExtractMethodBody(
                ReadStrippedParsekSource("RewindInvoker.cs"),
                "internal static void ConsumePostLoad(");
            int restore = body.IndexOf("ReconciliationBundle.Restore(", StringComparison.Ordinal);
            int discard = body.IndexOf("DiscardRestoreAttemptOfAbandonedFlight(", StringComparison.Ordinal);
            int strip = body.IndexOf("RunStripActivateMarker(", StringComparison.Ordinal);
            Assert.True(restore >= 0 && discard >= 0 && strip >= 0,
                $"restore={restore} discard={discard} strip={strip}");
            Assert.True(restore < discard && discard < strip,
                $"restore={restore} discard={discard} strip={strip}");
        }

        // ===== wiring: the three caller-side discard sites use the helper ===========
        //
        // These sites drop the clone tree themselves (no drop-site id match runs for
        // them before the tree is gone), so a bare ClearCommittedTreeRestoreAttempt
        // would lift the suppression without purging. Comments stripped first: the
        // comment documenting each site names the same callee the code must call.
        // They are private/internal methods on live types and cannot be driven
        // headless, so this is the same file-scrape gate convention as
        // ResetFlightReadyStateRestoreAttemptTests.

        // catches: the Hook-1 no-op no-session resume revert dropping the clone after a
        // bare clear, leaving the clone's suppressed post-cutoff tails to save.
        [Fact]
        public void AutoDiscardNoOpNoSessionCommittedResume_DiscardsTheAttempt_NotBareClear()
        {
            string body = ExtractMethodBody(
                ReadStrippedParsekSource("ParsekFlight.cs"),
                "void AutoDiscardNoOpNoSessionCommittedResume(");
            AssertDiscardHelperNotBareClear(body,
                "AutoDiscardNoOpNoSessionCommittedResume",
                "noop-no-session-committed-resume-revert");
        }

        // catches: the switch-segment scoped discard's committed-restore-clone branch
        // reverting to the bare clear. Its own purge only covers the segment subtree's
        // owned ids, never the resumed committed parent's recordings, so the attempt
        // tails there survive a bare clear.
        [Fact]
        public void SwitchSegmentScopedDiscard_CommittedRestoreCloneBranch_DiscardsTheAttempt()
        {
            string body = ExtractMethodBody(
                ReadStrippedParsekSource("RecordingStore.cs"),
                "internal static SwitchSegmentDiscardDisposition " +
                "TryDiscardActiveSwitchSegmentAttempt(");
            AssertDiscardHelperNotBareClear(body,
                "TryDiscardActiveSwitchSegmentAttempt",
                "switch-segment-scoped-discard committed-restore-clone");

            // Pin the branch, not just the call: the discard belongs to the
            // isCommittedRestoreClone disposition and must precede its return, so the
            // pending-tree-prune path (which keeps its own attempt armed) never runs it.
            int branch = body.IndexOf(
                "if (isCommittedRestoreClone)", StringComparison.Ordinal);
            int discard = body.IndexOf(
                "DiscardCommittedTreeRestoreAttempt(", StringComparison.Ordinal);
            int done = body.IndexOf(
                "return SwitchSegmentDiscardDisposition.CommittedRestoreClone;",
                StringComparison.Ordinal);
            Assert.True(branch >= 0 && discard > branch && done > discard,
                "TryDiscardActiveSwitchSegmentAttempt must discard the restore attempt " +
                "inside the isCommittedRestoreClone branch, before that branch returns.");
        }

        // catches: the pre-switch dialog Case B/C discard dropping the live clone after
        // a bare clear (no session exists here, so no scoped discard follows).
        [Fact]
        public void PreSwitchDialogDiscard_DiscardsTheAttempt_NotBareClear()
        {
            string body = ExtractMethodBody(
                ReadStrippedParsekSource("Patches/MapFocusObjectOnSelectPatch.cs"),
                "void DiscardActiveRecordingAndSwitchTo(");
            // Id-matched purge-then-clear for the tree this discard tears down: an
            // unmatched discard would purge a parked pending clone's tails.
            Assert.Contains(
                "ClearCommittedTreeRestoreAttemptForDroppedTree(\n" +
                "                        flight.ActiveTreeForDisplay?.Id,\n" +
                "                        \"pre-switch-dialog discard (no-session)\",\n" +
                "                        preserveIrreversibleTails: true)",
                body.Replace("\r\n", "\n"));
            Assert.True(body.IndexOf("ClearCommittedTreeRestoreAttempt(", StringComparison.Ordinal) < 0,
                "DiscardActiveRecordingAndSwitchTo calls the bare ClearCommittedTreeRestoreAttempt");
            Assert.True(body.IndexOf("DiscardCommittedTreeRestoreAttempt(", StringComparison.Ordinal) < 0,
                "DiscardActiveRecordingAndSwitchTo calls the unmatched DiscardCommittedTreeRestoreAttempt");
        }

        /// <summary>
        /// One site's gate: the method body calls DiscardCommittedTreeRestoreAttempt
        /// with the exact reason literal, and never the bare
        /// ClearCommittedTreeRestoreAttempt (whose purge-less clear is the defect).
        /// The reason literal is the argument that matters - it ties the purge and the
        /// clear in a collected log to THIS site.
        /// </summary>
        private static void AssertDiscardHelperNotBareClear(
            string body, string siteName, string reasonLiteral)
        {
            Assert.True(body.IndexOf(
                    "DiscardCommittedTreeRestoreAttempt(", StringComparison.Ordinal) >= 0,
                siteName + " does not call DiscardCommittedTreeRestoreAttempt - a bare " +
                "clear there lifts the attempt's event suppression without purging, so " +
                "the clone's suppressed post-cutoff tails become durable on the next save.");
            Assert.True(body.IndexOf(
                    "ClearCommittedTreeRestoreAttempt(", StringComparison.Ordinal) < 0,
                siteName + " calls the bare ClearCommittedTreeRestoreAttempt - it must " +
                "discard (purge then clear) instead.");
            Assert.Contains("\"" + reasonLiteral + "\"", body);
        }

        // ===== source-locate / extract helpers =======================================

        private static string ReadStrippedParsekSource(string relativePath)
        {
            return SourceScanText.StripCSharpComments(
                File.ReadAllText(LocateParsekSourceFile(relativePath)));
        }

        /// <summary>
        /// Returns the signature-through-closing-brace span of the method declared by
        /// <paramref name="signature"/>, brace-counted so nested blocks are included.
        /// </summary>
        private static string ExtractMethodBody(string src, string signature)
        {
            int methodStart = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(methodStart >= 0,
                signature + " not found - did the method get renamed? Update this gate.");

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
                signature + " has unbalanced braces - the source file is malformed.");

            return src.Substring(methodStart, closeBrace - methodStart + 1);
        }

        /// <summary>
        /// Walks up from the test binary directory looking for Source/Parsek/
        /// <paramref name="relativePath"/> (a file may live in a subfolder, e.g.
        /// "Patches/MapFocusObjectOnSelectPatch.cs") so the file-scrape gate works
        /// regardless of where dotnet test is invoked from (same walk as
        /// ResetFlightReadyStateRestoreAttemptTests).
        /// </summary>
        private static string LocateParsekSourceFile(string relativePath)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = Path.Combine(dir, "Source", "Parsek", relativePath);
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "Parsek", relativePath));
        }
    }
}
