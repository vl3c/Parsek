using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug 6 follow-up (post-#876 playtest 2026-05-17): regression coverage
    /// for <c>MergeDialog.BuildWholeTreeMergeDialogBody</c>. When the
    /// switch/Fly auto-record route is <c>committed-spawned-clone</c>, the
    /// segment recording is attached INSIDE the committed-clone tree
    /// alongside the prior committed Kerbal X recording (which may span
    /// the entire launch-to-present mission). The original
    /// implementation rendered the WHOLE tree's UT span as the dialog
    /// duration, so the player saw "Kerbal X - 28m" for a 10-second
    /// post-switch segment. These tests pin the segment-aware duration
    /// path: when an <c>ActiveSwitchSegmentSession</c> is armed AND the
    /// dialog's tree carries the segment, the duration is the segment
    /// recording's elapsed time alone.
    ///
    /// <para>The standalone-route case (a fresh standalone tree
    /// containing only the segment) renders the same value because
    /// tree-duration ≈ segment-duration there — but the test still
    /// exercises the segment-aware codepath to guard against a refactor
    /// that conditionally drops the segment-aware branch for the
    /// equal-duration case.</para>
    /// </summary>
    [Collection("Sequential")]
    public class MergeDialogSwitchSegmentDurationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MergeDialogSwitchSegmentDurationTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            MergeDialog.ResetTestOverrides();
        }

        public void Dispose()
        {
            ParsekScenario.SetInstanceForTesting(null);
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MergeDialog.ResetTestOverrides();
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static Recording MakeRecording(
            string recordingId,
            string treeId,
            double startUT,
            double endUT,
            string vesselName = "Test")
        {
            return new Recording
            {
                RecordingId = recordingId,
                TreeId = treeId,
                VesselName = vesselName,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
            };
        }

        private static RecordingTree MakeTree(
            string treeId,
            params Recording[] recordings)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeId,
                BranchPoints = new List<BranchPoint>(),
            };
            foreach (var rec in recordings)
                tree.AddOrReplaceRecording(rec);
            if (recordings.Length > 0)
            {
                tree.RootRecordingId = recordings[0].RecordingId;
                tree.ActiveRecordingId = recordings[0].RecordingId;
            }
            return tree;
        }

        private static void ArmSession(string treeId, string segmentRecordingId)
        {
            var scenario = new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.ArmSwitchSegmentSession(new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                IntentId = Guid.NewGuid(),
                EntryReason = SwitchSegmentEntryReason.MapSwitchTo,
                TreeId = treeId,
                ActiveSegmentRecordingId = segmentRecordingId,
                SwitchUT = 0.0,
                PreSessionBranchPointIds = new List<string>(),
            });
        }

        // -----------------------------------------------------------------
        // Tests
        // -----------------------------------------------------------------

        // Fails if: dialog regresses to showing the whole tree's duration
        // when a segment session is active (the committed-spawned-clone
        // route bug from the 2026-05-17 playtest).
        [Fact]
        public void DialogBody_SwitchSegmentActive_InCommittedCloneTree_ShowsSegmentDuration()
        {
            // Committed-clone tree: 5 recordings spanning a 30-minute
            // mission (UT 0..1800), the 5th of which is the freshly
            // attached switch segment that ran for only 10 seconds
            // (UT 1790..1800). The pre-fix code reads 1800-0 = 30m;
            // the post-fix code reads 1800-1790 = 10s.
            var r1 = MakeRecording("rec_1", "tree_a", 0.0, 600.0, "Kerbal X");
            var r2 = MakeRecording("rec_2", "tree_a", 600.0, 1000.0, "Kerbal X");
            var r3 = MakeRecording("rec_3", "tree_a", 1000.0, 1500.0, "Kerbal X");
            var r4 = MakeRecording("rec_4", "tree_a", 1500.0, 1790.0, "Kerbal X");
            var segment = MakeRecording("rec_seg", "tree_a", 1790.0, 1800.0, "Kerbal X");
            var tree = MakeTree("tree_a", r1, r2, r3, r4, segment);
            tree.TreeName = "Kerbal X";

            ArmSession("tree_a", "rec_seg");

            string body = MergeDialog.BuildWholeTreeMergeDialogBody(tree);
            Assert.Contains("Kerbal X", body);
            // 10s segment, NOT 30m tree.
            Assert.Contains("10s", body);
            Assert.DoesNotContain("30m", body);
        }

        // Fails if: standalone-route segment-scoped dialog drops the
        // segment-aware duration computation when tree-duration equals
        // segment-duration (a future refactor could short-circuit).
        [Fact]
        public void DialogBody_SwitchSegmentActive_InStandaloneTree_ShowsSegmentDuration()
        {
            var segment = MakeRecording("rec_seg", "tree_s", 100.0, 145.0, "Probe");
            var tree = MakeTree("tree_s", segment);
            tree.TreeName = "Probe";

            ArmSession("tree_s", "rec_seg");

            string body = MergeDialog.BuildWholeTreeMergeDialogBody(tree);
            Assert.Contains("Probe", body);
            // 45s segment = 45s tree.
            Assert.Contains("45s", body);
            // Verify the segment-aware code path actually ran (not just
            // the tree-wide fallback that happens to produce the same
            // number).
            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]") &&
                l.Contains("using segment duration"));
        }

        // Fails if: non-segment dialogs lose the tree-wide duration
        // formula (regression guard for the unrelated regular-merge path).
        [Fact]
        public void DialogBody_NoActiveSwitchSegmentSession_ShowsFullTreeDuration()
        {
            var r1 = MakeRecording("rec_1", "tree_b", 0.0, 60.0, "Kerbal Y");
            var r2 = MakeRecording("rec_2", "tree_b", 60.0, 120.0, "Kerbal Y");
            var tree = MakeTree("tree_b", r1, r2);
            tree.TreeName = "Kerbal Y";

            // No session armed.
            ParsekScenario.SetInstanceForTesting(new ParsekScenario());

            string body = MergeDialog.BuildWholeTreeMergeDialogBody(tree);
            Assert.Contains("Kerbal Y", body);
            // Tree-wide 120s = 2m.
            Assert.Contains("2m", body);
        }

        // Fails if: cross-tree session armed bleeds into a different
        // tree's dialog (segment in tree A, dialog is for tree B).
        [Fact]
        public void DialogBody_SegmentSessionForDifferentTree_ShowsFullTreeDuration()
        {
            var rTreeB1 = MakeRecording("rec_b1", "tree_b", 0.0, 200.0, "Kerbal B");
            var rTreeB2 = MakeRecording("rec_b2", "tree_b", 200.0, 300.0, "Kerbal B");
            var treeB = MakeTree("tree_b", rTreeB1, rTreeB2);
            treeB.TreeName = "Kerbal B";

            // Session targets a recording in tree A, not in tree B.
            ArmSession("tree_a", "rec_seg_in_tree_a");

            string body = MergeDialog.BuildWholeTreeMergeDialogBody(treeB);
            Assert.Contains("Kerbal B", body);
            // Full tree-B span: 300s = 5m.
            Assert.Contains("5m", body);
        }

        // Fails if: malformed segment data silently produces a
        // nonsensical duration string instead of falling back to the
        // tree-wide span and logging a Warn.
        [Fact]
        public void DialogBody_MalformedSegmentBounds_FallsBackToTreeDuration_AndLogsWarn()
        {
            var r1 = MakeRecording("rec_1", "tree_c", 0.0, 240.0, "Kerbal C");
            // Malformed segment: EndUT < StartUT.
            var segment = MakeRecording("rec_seg", "tree_c", 1000.0, 500.0, "Kerbal C");
            var tree = MakeTree("tree_c", r1, segment);
            tree.TreeName = "Kerbal C";

            ArmSession("tree_c", "rec_seg");

            // No live clock available — the malformed-bounds Warn branch
            // is the documented fallback. Suppress the live-recording
            // branch by leaving NowUtProviderForTesting unset (which
            // returns NaN under xUnit, taking the malformed-Warn path).
            string body = MergeDialog.BuildWholeTreeMergeDialogBody(tree);
            Assert.Contains("Kerbal C", body);
            // Tree-wide span: min(r1.StartUT=0, segment.StartUT=1000)=0
            // and max(r1.EndUT=240, segment.EndUT=500)=500, so 500s
            // = "8m 20s". The malformed segment alone would produce
            // a negative duration -- the assertion guards against that.
            Assert.Contains("8m 20s", body);
            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]") &&
                l.Contains("malformed-segment-bounds"));
        }

        // -----------------------------------------------------------------
        // Bug A (post-#876 playtest 2026-05-17): segment duration shows
        // 0s during a live recording. The segment has been recording for
        // ~40 seconds but only its initial point is sampled, so
        // segment.EndUT == segment.StartUT. The dialog rendered
        // "Kerbal X Probe - 0s". Fix: when EndUT <= StartUT, prefer
        // currentUT - StartUT.
        // -----------------------------------------------------------------

        // Fails if: a future refactor regresses live-recording duration
        // to 0 by reading EndUT directly without the live-UT fallback.
        [Fact]
        public void DialogBody_LiveSegmentNotYetFinalized_ShowsCurrentUTMinusStartUT()
        {
            // Live segment recording: ExplicitStartUT set on segment
            // creation, but ExplicitEndUT is still NaN (no per-frame
            // update) and Points is empty so EndUT falls back to 0 in
            // Recording.cs. Without the live-UT fallback,
            // EndUT - StartUT = 0 - 799.557 (or with one point both
            // equal startUT, so the diff is exactly 0).
            const double startUT = 799.557;
            const double currentUT = 839.557; // +40 s flight time
            var segment = MakeRecording(
                "rec_seg", "tree_live", startUT, 0.0, "Kerbal X Probe");
            // Force the "single sample, EndUT == StartUT" shape: clear
            // ExplicitEndUT so EndUT == StartUT from the getter.
            segment.ExplicitEndUT = double.NaN;
            // Recording.EndUT computes from points / orbit / sections.
            // With none of those populated and ExplicitEndUT == NaN,
            // EndUT returns 0.0, the exact production shape that
            // surfaced in the playtest log
            // (durationSec=0, recId=40ac6b22).
            var tree = MakeTree("tree_live", segment);
            tree.TreeName = "Kerbal X Probe";

            ArmSession("tree_live", "rec_seg");
            MergeDialog.NowUtProviderForTesting = () => currentUT;

            string body = MergeDialog.BuildWholeTreeMergeDialogBody(tree);
            Assert.Contains("Kerbal X Probe", body);
            // 40s elapsed, NOT 0s.
            Assert.Contains("40s", body);
            Assert.DoesNotContain("- 0s", body);
            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]") &&
                l.Contains("using live segment duration"));
        }

        // Fails if: a future refactor breaks the finalized-recording
        // path (regression guard for the EndUT > StartUT branch).
        [Fact]
        public void DialogBody_FinalizedSegment_ShowsEndUTMinusStartUT()
        {
            // Finalized segment: EndUT > StartUT. The currentUT hook is
            // set far in the future to prove the helper does NOT prefer
            // wall-clock over the finalized bounds.
            var segment = MakeRecording(
                "rec_seg", "tree_fin", 100.0, 145.0, "Probe");
            var tree = MakeTree("tree_fin", segment);
            tree.TreeName = "Probe";

            ArmSession("tree_fin", "rec_seg");
            MergeDialog.NowUtProviderForTesting = () => 9999.0;

            string body = MergeDialog.BuildWholeTreeMergeDialogBody(tree);
            Assert.Contains("Probe", body);
            // EndUT - StartUT = 45s, NOT 9899s (live fallback would say
            // 9899s; the finalized branch must win).
            Assert.Contains("45s", body);
            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]") &&
                l.Contains("using segment duration") &&
                !l.Contains("using live segment duration"));
        }

        // Fails if: a future refactor lets bogus duration values reach
        // the UI on a UT regression (currentUT < startUT) — the live-
        // fallback must clamp to 0 / fall through to the finalized
        // bounds, never emit a negative duration.
        [Fact]
        public void DialogBody_LiveSegmentNegativeOrNonFiniteUT_ClampsToZero()
        {
            // Segment looks live (EndUT == StartUT), but currentUT is
            // BEHIND startUT (a rewind-to-staging crossing the segment
            // could synthesize this).
            const double startUT = 1000.0;
            const double currentUT = 500.0; // regression
            var segment = MakeRecording(
                "rec_seg", "tree_neg", startUT, 0.0, "Probe");
            segment.ExplicitEndUT = double.NaN;
            var tree = MakeTree("tree_neg", segment);
            tree.TreeName = "Probe";

            ArmSession("tree_neg", "rec_seg");
            MergeDialog.NowUtProviderForTesting = () => currentUT;

            string body = MergeDialog.BuildWholeTreeMergeDialogBody(tree);
            Assert.Contains("Probe", body);
            // currentUT < startUT → live branch is skipped, the
            // malformed-bounds warn fires, ComputeTreeDurationRange
            // returns negative, FormatDuration clamps to 0. The
            // user-facing format is "0s"; assert no negative number
            // leaks into the duration substring after "Probe - ".
            int sepIdx = body.IndexOf(" - ", StringComparison.Ordinal);
            Assert.True(sepIdx > 0, "body must contain ' - ' separator");
            string durationPart = body.Substring(sepIdx + 3);
            Assert.Equal("0s", durationPart);
            Assert.DoesNotContain("-", durationPart);
        }
    }
}
