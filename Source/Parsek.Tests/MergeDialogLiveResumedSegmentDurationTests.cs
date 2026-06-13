using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// BUG #2 coverage for the no-SwitchSegmentSession dialog flow
    /// (<c>MergeDialog.ResolveNoSessionDialogBodyDuration</c>). When the player
    /// Map-Switch-To's onto an existing committed vessel, the OnLoad
    /// committed-tree restore re-attaches a live recorder to the EXISTING
    /// committed recording WITHOUT arming a <c>SwitchSegmentSession</c>. The
    /// player flies a short segment (~17s), then Switch-To's away and the
    /// pre-switch Merge/Discard dialog fires. The resumed recording's Points are
    /// appended in place, so its <c>StartUT</c> is the original (multi-year-ago)
    /// launch UT — <c>currentUT - StartUT</c> would still render the whole span.
    ///
    /// <para>The fix anchors on a per-session resume UT captured when the
    /// recorder resumed this craft (exposed through the
    /// <c>LiveResumedSegmentProviderForTesting</c> seam here), so
    /// <c>currentUT - resumeStartUT</c> yields the short segment. These tests pin
    /// the new branch and its fallbacks; the session-armed Case A path is covered
    /// by <see cref="MergeDialogSwitchSegmentDurationTests"/> and re-verified by
    /// the precedence test below.</para>
    /// </summary>
    [Collection("Sequential")]
    public class MergeDialogLiveResumedSegmentDurationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MergeDialogLiveResumedSegmentDurationTests()
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
            // Pin the Earth calendar so duration formatting is deterministic
            // regardless of the host machine's GameSettings.KERBIN_TIME.
            ParsekTimeFormat.KerbinTimeOverrideForTesting = false;
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
            ParsekTimeFormat.ResetForTesting();
        }

        // -----------------------------------------------------------------
        // Helpers (mirror MergeDialogSwitchSegmentDurationTests)
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

        // Earth-time seconds: 8 years ago launch, "now".
        private const double EarthYearSeconds = 365.0 * 86400.0;
        private const double LaunchUT = 1000.0;
        private const double NowUT = LaunchUT + 8.0 * EarthYearSeconds;

        /// <summary>
        /// Builds the canonical resume-flow tree: 5 recordings, the active leaf
        /// being the resumed committed recording whose StartUT is the original
        /// launch UT and whose EndUT is "now" (8 years of points appended in
        /// place). No SwitchSegmentSession is armed.
        /// </summary>
        private static RecordingTree MakeResumedTree(out string liveRecId)
        {
            // Four prior committed recordings + the resumed-active recording.
            var r1 = MakeRecording("rec_1", "tree_res", LaunchUT, LaunchUT + 600.0, "Kerbal X");
            var r2 = MakeRecording("rec_2", "tree_res", LaunchUT + 600.0, LaunchUT + 1000.0, "Kerbal X");
            var r3 = MakeRecording("rec_3", "tree_res", LaunchUT + 1000.0, LaunchUT + 1500.0, "Kerbal X");
            var r4 = MakeRecording("rec_4", "tree_res", LaunchUT + 1500.0, LaunchUT + 2000.0, "Kerbal X");
            // The resumed committed recording: StartUT = launch, EndUT = now.
            var live = MakeRecording("rec_live", "tree_res", LaunchUT, NowUT, "Kerbal X");
            var tree = MakeTree("tree_res", r1, r2, r3, r4, live);
            tree.TreeName = "Kerbal X";
            tree.ActiveRecordingId = "rec_live";
            liveRecId = "rec_live";
            return tree;
        }

        private static void ArmSession(string treeId, string segmentRecordingId, double switchUT)
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
                SwitchUT = switchUT,
                PreSessionBranchPointIds = new List<string>(),
            });
        }

        // -----------------------------------------------------------------
        // Tests
        // -----------------------------------------------------------------

        // Fails if: the no-session dialog regresses to showing the resumed
        // recording's whole multi-year span instead of the short live segment
        // (the BUG #2 symptom).
        [Fact]
        public void DialogBody_NoSession_LiveResumedSegmentInTree_ShowsCurrentUTMinusResumeStartUT()
        {
            string liveRecId;
            var tree = MakeResumedTree(out liveRecId);

            // No session armed (the resume flow does not arm one).
            ParsekScenario.SetInstanceForTesting(new ParsekScenario());

            // Resume captured 17s before "now"; the dialog should show 17s.
            double resumeStartUT = NowUT - 17.0;
            MergeDialog.LiveResumedSegmentProviderForTesting =
                () => (liveRecId, resumeStartUT);
            MergeDialog.NowUtProviderForTesting = () => NowUT;

            string body = MergeDialog.BuildWholeTreeMergeDialogBody(tree);
            Assert.Contains("Kerbal X", body);
            // 17s segment, NOT the 8-year span.
            Assert.Contains("17s", body);
            Assert.DoesNotContain("8y", body);
            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]") &&
                l.Contains("duration source=live-resumed-segment") &&
                l.Contains("recId=rec_live") &&
                l.Contains("durationSec="));
        }

        // Fails if: no live recording armed yet the dialog tries the
        // live-resumed branch (must fall back to the whole-tree span).
        [Fact]
        public void DialogBody_NoSession_NoLiveResumedSegment_FallsBackToTreeWide()
        {
            string liveRecId;
            var tree = MakeResumedTree(out liveRecId);

            ParsekScenario.SetInstanceForTesting(new ParsekScenario());

            // Provider returns the production "no live recording" shape.
            MergeDialog.LiveResumedSegmentProviderForTesting =
                () => (null, double.NaN);
            MergeDialog.NowUtProviderForTesting = () => NowUT;

            string body = MergeDialog.BuildWholeTreeMergeDialogBody(tree);
            Assert.Contains("Kerbal X", body);
            // Tree-wide span: 8 years.
            Assert.Contains("8y", body);
            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]") &&
                l.Contains("duration source=tree-wide") &&
                l.Contains("reason=no-session"));
        }

        // Fails if: a stale live-resume id from a prior craft bleeds into a
        // different tree's dialog (cross-tree safety via ContainsKey).
        [Fact]
        public void DialogBody_NoSession_LiveResumedRecordingNotInThisTree_FallsBackToTreeWide()
        {
            string liveRecId;
            var tree = MakeResumedTree(out liveRecId);

            ParsekScenario.SetInstanceForTesting(new ParsekScenario());

            // Live id belongs to a DIFFERENT tree (not in this dialog's tree).
            MergeDialog.LiveResumedSegmentProviderForTesting =
                () => ("rec_from_other_tree", NowUT - 17.0);
            MergeDialog.NowUtProviderForTesting = () => NowUT;

            string body = MergeDialog.BuildWholeTreeMergeDialogBody(tree);
            Assert.Contains("Kerbal X", body);
            // Tree-wide span, NOT the 17s segment.
            Assert.Contains("8y", body);
            Assert.DoesNotContain("17s", body);
            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]") &&
                l.Contains("duration source=tree-wide") &&
                l.Contains("liveInThisTree=False"));
        }

        // Fails if: a UT regression (currentUT < resumeStartUT) leaks a
        // negative duration instead of falling through to the tree-wide span.
        [Fact]
        public void DialogBody_NoSession_LiveResume_CurrentUTRegressedBehindResumeStart_FallsBackToTreeWide()
        {
            string liveRecId;
            var tree = MakeResumedTree(out liveRecId);

            ParsekScenario.SetInstanceForTesting(new ParsekScenario());

            // resumeStartUT is AHEAD of the current clock (a rewind crossing
            // the resume UT could synthesize this).
            MergeDialog.LiveResumedSegmentProviderForTesting =
                () => (liveRecId, 1000.0);
            MergeDialog.NowUtProviderForTesting = () => 500.0;

            string body = MergeDialog.BuildWholeTreeMergeDialogBody(tree);
            Assert.Contains("Kerbal X", body);
            // currentUT > resumeStartUT guard fails → tree-wide span.
            Assert.Contains("8y", body);
            // No negative duration leaks into the duration substring.
            int sepIdx = body.IndexOf(" - ", StringComparison.Ordinal);
            Assert.True(sepIdx > 0, "body must contain ' - ' separator");
            string durationPart = body.Substring(sepIdx + 3);
            Assert.DoesNotContain("-", durationPart);
            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]") &&
                l.Contains("duration source=tree-wide"));
        }

        // Fails if: a non-finite resume UT is treated as a valid anchor (must
        // fall back to the whole-tree span).
        [Fact]
        public void DialogBody_NoSession_LiveResume_NaNResumeStartUT_FallsBackToTreeWide()
        {
            string liveRecId;
            var tree = MakeResumedTree(out liveRecId);

            ParsekScenario.SetInstanceForTesting(new ParsekScenario());

            MergeDialog.LiveResumedSegmentProviderForTesting =
                () => (liveRecId, double.NaN);
            MergeDialog.NowUtProviderForTesting = () => NowUT;

            string body = MergeDialog.BuildWholeTreeMergeDialogBody(tree);
            Assert.Contains("Kerbal X", body);
            Assert.Contains("8y", body);
            Assert.DoesNotContain("17s", body);
            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]") &&
                l.Contains("duration source=tree-wide") &&
                l.Contains("resumeFinite=False"));
        }

        // Fails if: the new live-resumed branch is checked BEFORE the
        // session-armed Case A path (the session-segment duration must win
        // even when a live-resume provider is also injected).
        [Fact]
        public void DialogBody_SessionArmed_TakesPrecedenceOverLiveResumeProvider()
        {
            // Committed-clone tree: the active leaf is the freshly-attached
            // switch segment (UT NowUT-10..NowUT), plus an older recording so
            // the tree-wide span is large.
            var older = MakeRecording("rec_old", "tree_p", LaunchUT, NowUT - 10.0, "Kerbal P");
            var segment = MakeRecording("rec_seg", "tree_p", NowUT - 10.0, NowUT, "Kerbal P");
            var tree = MakeTree("tree_p", older, segment);
            tree.TreeName = "Kerbal P";
            tree.ActiveRecordingId = "rec_seg";

            // Arm a session targeting the 10s segment.
            ArmSession("tree_p", "rec_seg", NowUT - 10.0);

            // ALSO inject a live-resume provider that would claim a different
            // (17s) duration. Case A must win.
            MergeDialog.LiveResumedSegmentProviderForTesting =
                () => ("rec_seg", NowUT - 17.0);
            MergeDialog.NowUtProviderForTesting = () => NowUT;

            string body = MergeDialog.BuildWholeTreeMergeDialogBody(tree);
            Assert.Contains("Kerbal P", body);
            // Session-segment finalized duration (EndUT-StartUT = 10s) wins,
            // NOT the 17s live-resume provider value.
            Assert.Contains("10s", body);
            Assert.DoesNotContain("17s", body);
            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]") &&
                l.Contains("using segment duration") &&
                !l.Contains("live-resumed-segment"));
        }
    }
}
