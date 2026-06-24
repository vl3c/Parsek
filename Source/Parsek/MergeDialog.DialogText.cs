using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    public static partial class MergeDialog
    {
        internal static string FormatDuration(double seconds)
            => ParsekTimeFormat.FormatDuration(seconds);

        internal static string BuildTimelineActionButtonLabel(
            bool isPermanent, bool isReFlyAttempt = false)
        {
            // Non-permanent is only reachable on a Re-Fly attempt whose
            // outcome is not yet a sealing terminal; that dialog offers a
            // separate "Merge & Seal" button, so this one commits without
            // closing the slot.
            if (!isPermanent)
                return "Commit (don't seal)";
            // Only a permanent Re-Fly merge auto-seals the rewind slot
            // (MergeState.Immutable); name that consequence on the button.
            // Ordinary whole-tree merges are also permanent but have no
            // slot to seal, so they keep the plain "Merge to Timeline".
            return isReFlyAttempt ? "Merge & Seal" : "Merge to Timeline";
        }

        /// <summary>
        /// Button label for the explicit "commit and close the re-fly slot
        /// now" action shown beneath "Commit (don't seal)" on a not-yet-
        /// sealable Re-Fly attempt. Shares the wording with the permanent
        /// auto-seal button so there is one canonical seal string.
        /// </summary>
        internal static string BuildReFlyMergeAndSealButtonLabel()
            => BuildTimelineActionButtonLabel(isPermanent: true, isReFlyAttempt: true);

        internal static string BuildTimelineActionDialogTitle(bool isPermanent)
        {
            return isPermanent ? "Confirm: Merge to Timeline" : "Confirm: Commit to Timeline";
        }

        internal static bool DetermineReFlyTimelineActionIsPermanent(
            Recording reFlyRec,
            ReFlySessionMarker marker,
            ReFlyAutoSealPreviewResult preview,
            out string source)
        {
            bool classifierPermanent;
            string classifierReason;
            if (SupersedeCommit.TryPredictReFlyMergeIsPermanent(
                    marker,
                    reFlyRec,
                    ParsekScenario.Instance,
                    out classifierPermanent,
                    out classifierReason))
            {
                source = "classifier:" + (classifierReason ?? "<none>");
                return classifierPermanent;
            }

            source = "preview:" + (classifierReason ?? "<unavailable>");
            return preview.WillAutoSeal;
        }

        /// <summary>
        /// Composes the Re-Fly merge dialog body. Shared by the pre-
        /// transition <see cref="ShowTreeDialog"/> 4-arg overload and the
        /// deferred post-transition 1-arg overload - both paths can land
        /// in <c>TryCommitReFlySupersede</c> and auto-seal, so both must
        /// render the auto-seal warning when the preview classifies the
        /// attempt as sealable. Pure helper for unit-testability: the
        /// dialog spawn requires Unity runtime, but the body string is
        /// data-driven. Callers pass a precomputed
        /// <see cref="ReFlyAutoSealPreviewResult"/>; this method only
        /// formats it.
        /// </summary>
        internal static string BuildReFlyDialogBody(
            string vesselLabel,
            double reFlyDuration,
            ReFlyAutoSealPreviewResult preview,
            bool willAutoSeal)
        {
            string headline = $"<align=\"center\">{vesselLabel} - " +
                              $"{FormatDuration(reFlyDuration)}</align>\n\n";
            if (!willAutoSeal)
            {
                // Not a sealing terminal: the dialog shows both
                // "Commit (don't seal)" and "Merge & Seal", so the body
                // names what each one does to the re-fly slot.
                return headline +
                    "<align=\"left\">Do you want to commit this Re-Fly attempt " +
                    "to the timeline?\n\n" +
                    "Commit (don't seal) keeps this Re-Fly slot open. " +
                    "Merge & Seal permanently closes it — you cannot Re-Fly " +
                    "this line again.</align>";
            }

            string reasons = BuildReFlyAutoSealReasonLines(preview);
            // Auto-seal flips MergeState to Immutable and closes the rewind
            // slot, so this branch *is* the irreversible one. Keep the
            // short translation of what "auto-seal" means in player terms
            // (the slot becomes permanent, the line of flight can no longer
            // be Re-Flown) - dropping that sentence in the original trim
            // left "auto-sealed" undefined for players unfamiliar with the
            // term. Voice stays declarative ("If not discarded, ... will be
            // ...") instead of matching the no-seal branch's question form;
            // the "If not discarded" anchor signals that the Discard button
            // is still the escape hatch and that asymmetry is intentional.
            return headline +
                "<align=\"left\"><b>If not discarded, this Re-Fly attempt " +
                $"will be merged AND auto-sealed</b> for the following " +
                $"reason(s):\n{reasons}\n\n" +
                "Auto-seal makes the slot permanent and you cannot Re-Fly this line again.</align>";
        }

        internal static string BuildReFlyAutoSealReasonLines(
            ReFlyAutoSealPreviewResult preview)
        {
            if (preview.Reasons == null || preview.Reasons.Count == 0)
            {
                ParsekLog.Warn("MergeDialog",
                    "BuildReFlyAutoSealReasonLines: auto-seal preview returned no reasons; using fallback line");
                return "- auto-seal condition met";
            }

            var lines = new List<string>(preview.Reasons.Count);
            for (int i = 0; i < preview.Reasons.Count; i++)
                lines.Add("- " + ReFlyAutoSealPreviewResult.PhraseFor(preview.Reasons[i]));
            return string.Join("\n", lines);
        }

        private static string FormatClearReason(string reason)
            => string.IsNullOrEmpty(reason) ? "<unspecified>" : reason;

        /// <summary>
        /// Bug 3 (post-#876 playtest 2026-05-17): unified body for the
        /// whole-tree pre/post-transition merge dialog. Both switch-segment
        /// (short standalone segment) and long-lived launch trees render the
        /// same wording: "{TreeName} - {Duration}". The player tells short
        /// segments apart from whole recordings by reading the duration.
        /// Previously a separate <c>BuildSwitchSegmentDialogBody</c> emitted
        /// entry-reason-aware copy ("Keep your switch into ..." / "Keep your
        /// new flight on ..."), but the playtest report identified that
        /// bespoke copy as confusing — the duration line is the load-bearing
        /// distinguisher.
        ///
        /// <para>Bug 6 follow-up (post-#876 playtest 2026-05-17, route=
        /// committed-spawned-clone case): when an
        /// <see cref="SwitchSegmentSession"/> is armed AND the dialog's tree
        /// owns the session's <c>ActiveSegmentRecordingId</c>, the duration
        /// shown is the SEGMENT recording's elapsed time
        /// (<c>EndUT - StartUT</c>), NOT the whole tree's span. Without this
        /// the dialog after a committed-clone switch/Fly auto-record shows
        /// the launch-to-present mission time (e.g. "Kerbal X - 28m") even
        /// though the pending work is the ~10-second post-switch segment,
        /// because the segment recording lives inside the committed-clone
        /// tree alongside ~15 pre-existing recordings. Player mental model:
        /// "how long did I fly after the switch" — child-debris-recording
        /// windows after explosions are intentionally NOT included.</para>
        ///
        /// <para>Defensive fallbacks:</para>
        /// <list type="bullet">
        /// <item>session armed but <c>ActiveSegmentRecordingId</c> is null
        ///     or not in this tree → tree-wide duration (a different tree
        ///     is being merged than the one carrying the segment; no warn).</item>
        /// <item>segment has <c>EndUT &lt; StartUT</c> (malformed bounds) →
        ///     tree-wide duration + <c>[SwitchSegment]</c> Warn log.</item>
        /// </list>
        /// </summary>
        internal static string BuildWholeTreeMergeDialogBody(RecordingTree tree)
        {
            string treeName = tree?.TreeName ?? "<unnamed>";
            // L2 (PR #876 final review): ParsekScenario.Instance is a Unity-static
            // touch point — its static initializer can race during scene load
            // and throw a TypeInitializationException (or a downstream null
            // chase). Wrap the dispatch so a broken scenario state cannot
            // crash the dialog. Falling back to the tree-wide duration is the
            // safe whole-launch reading; the [SwitchSegment] Warn surfaces the
            // race in KSP.log so a real bug can still be diagnosed.
            double duration;
            try
            {
                duration = ResolveDialogBodyDuration(tree);
            }
            // LOW 2 (PR #876 round-6 review): defensive for Unity-runtime
            // hazards (Planetarium.GetUniversalTime throwing during scene
            // tear-down, ParsekScenario.Instance static initializer
            // racing scene load, etc.). For Case A (session-active), the
            // catch falls back from segment-scoped to tree-wide duration.
            // For Case B (no-session, priorTreeOverride), the normal
            // path already returns ComputeTreeDurationRange(tree) — the
            // catch is purely a safety net there.
            catch (System.Exception ex)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"BuildWholeTreeMergeDialogBody: dialog-body-static-exception " +
                    $"exType={ex.GetType().Name} " +
                    $"message={ex.Message ?? "<null>"} — falling back to tree-wide duration");
                duration = ComputeTreeDurationRange(tree);
            }
            return $"{treeName} - {FormatDuration(duration)}";
        }

        /// <summary>
        /// Test seam: override the current Planetarium UT used by the
        /// segment-aware duration path's live-recording fallback. Production
        /// code leaves this null and the helper falls back to
        /// <c>Planetarium.GetUniversalTime()</c>; unit tests inject a fixed
        /// UT so the live-recording branch is exercisable without a Unity
        /// runtime.
        /// </summary>
        internal static System.Func<double> NowUtProviderForTesting;

        /// <summary>
        /// BUG #2 test seam: override the (live recording id, resume-start UT) pair the
        /// no-session live-resumed-segment branch reads. Production code leaves this null
        /// and the resolver reads
        /// <see cref="ParsekFlight.GetLiveResumedRecordingIdForDialog"/> /
        /// <see cref="ParsekFlight.GetLiveResumeSessionStartUT"/>; unit tests inject a
        /// fixed pair so the branch is exercisable without a Unity
        /// <see cref="ParsekFlight.Instance"/>.
        /// </summary>
        internal static System.Func<(string liveRecordingId, double resumeStartUT)>
            LiveResumedSegmentProviderForTesting;

        /// <summary>Clears all test seams in MergeDialog.</summary>
        internal static void ResetTestOverrides()
        {
            NowUtProviderForTesting = null;
            LiveResumedSegmentProviderForTesting = null;
        }

        /// <summary>
        /// BUG #2: resolves the currently-live resumed segment (id + resume-start UT) for
        /// the no-session dialog branch. Prefers the
        /// <see cref="LiveResumedSegmentProviderForTesting"/> seam, else reads the
        /// <see cref="ParsekFlight"/> statics inside try/catch (Unity-static safety),
        /// falling back to <c>(null, NaN)</c> when neither yields a value.
        /// </summary>
        private static void TryGetLiveResumedSegment(
            out string liveRecordingId, out double resumeStartUT)
        {
            var hook = LiveResumedSegmentProviderForTesting;
            if (hook != null)
            {
                try
                {
                    var pair = hook();
                    liveRecordingId = pair.liveRecordingId;
                    resumeStartUT = pair.resumeStartUT;
                    return;
                }
                catch { /* fall through to ParsekFlight statics */ }
            }

            try
            {
                liveRecordingId = ParsekFlight.GetLiveResumedRecordingIdForDialog();
                resumeStartUT = ParsekFlight.GetLiveResumeSessionStartUT();
            }
            catch
            {
                // No Unity ParsekFlight.Instance (unit test without injected seam).
                liveRecordingId = null;
                resumeStartUT = double.NaN;
            }
        }

        private static double CurrentUtForSegmentDuration()
        {
            var hook = NowUtProviderForTesting;
            if (hook != null)
            {
                try { return hook(); }
                catch { /* fall through to Planetarium */ }
            }
            try { return Planetarium.GetUniversalTime(); }
            catch
            {
                // No Planetarium singleton (unit test without injected hook).
                // Returning NaN forces the segment-aware branch to fall back
                // to the persisted EndUT - StartUT computation instead of
                // synthesizing a nonsense live duration.
                return double.NaN;
            }
        }

        /// <summary>
        /// Picks the duration to render in the merge dialog body. When an
        /// active switch-segment session targets a recording inside
        /// <paramref name="tree"/>, prefers the segment recording's elapsed
        /// time so the dialog distinguishes a ~10s post-switch segment from
        /// the surrounding committed-clone tree's full mission duration.
        /// Falls back to <see cref="ComputeTreeDurationRange"/> on:
        /// no session armed, session missing the active segment id, segment
        /// not present in this tree, or malformed segment bounds.
        ///
        /// <para>Live-recording fallback (Bug A, post-#876 playtest
        /// 2026-05-17): a still-live segment that has only sampled its
        /// initial point has <c>EndUT == StartUT</c> (or a stale
        /// <c>ExplicitEndUT</c> behind <c>StartUT</c>), so
        /// <c>EndUT - StartUT</c> rounds to zero even after ~40 seconds of
        /// real flight. The dialog renders "0s" in that window. When the
        /// segment looks live (<c>EndUT &lt;= StartUT</c>) AND the current
        /// Planetarium UT is past <c>StartUT</c>, fall back to
        /// <c>currentUT - StartUT</c>. Clamps non-finite or negative results
        /// to <c>0</c> so a UT regression (rewind-to-staging crossing the
        /// segment, etc.) cannot leak a nonsense duration into the UI.</para>
        /// </summary>
        internal static double ResolveDialogBodyDuration(RecordingTree tree)
        {
            var scenario = ParsekScenario.Instance;
            var session = !object.ReferenceEquals(null, scenario)
                ? scenario.ActiveSwitchSegmentSession
                : null;
            if (session == null
                || string.IsNullOrEmpty(session.ActiveSegmentRecordingId)
                || tree?.Recordings == null)
            {
                // BUG #2: no SwitchSegmentSession is armed (the committed-tree resume
                // flow re-attaches a live recorder to the EXISTING committed recording
                // WITHOUT arming a session). Before falling back to the whole-tree span,
                // try the live-resumed-segment branch: when this dialog's tree carries a
                // currently-live recording, show the elapsed time since the recorder
                // session resumed (currentUT - resumeStartUT), not the resumed
                // recording's multi-year StartUT-anchored span.
                return ResolveNoSessionDialogBodyDuration(tree);
            }

            Recording segment;
            if (!tree.Recordings.TryGetValue(
                    session.ActiveSegmentRecordingId, out segment)
                || segment == null)
            {
                // Different tree than the one carrying the segment: legitimate
                // case (dialog is for tree B but session targets tree A), no
                // warning needed. Fall back to tree-wide duration.
                ParsekLog.Verbose("SwitchSegment",
                    $"BuildWholeTreeMergeDialogBody: duration source=tree-wide " +
                    $"recId=<none> " +
                    $"durationSec={ComputeTreeDurationRange(tree).ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"reason=session-segment-not-in-tree treeId={tree.Id ?? "<null>"}");
                return ComputeTreeDurationRange(tree);
            }

            double startUT = segment.StartUT;
            double endUT = segment.EndUT;

            // Bug A live-recording fallback (post-#876 playtest 2026-05-17):
            // a recording that has only sampled its initial point — or that
            // is bound to the live recorder but has not yet been finalized —
            // has EndUT <= StartUT. Prefer currentUT - startUT in that case
            // so the dialog reflects the wall-clock elapsed segment time
            // instead of rounding to 0s. This intentionally takes precedence
            // over the malformed-segment-bounds warn below: a live-but-still
            // sampling segment is the expected steady-state shape, not a
            // bug, and emitting a Warn every dialog open would be noise.
            if (endUT <= startUT)
            {
                double currentUT = CurrentUtForSegmentDuration();
                if (!double.IsNaN(currentUT)
                    && !double.IsInfinity(currentUT)
                    && currentUT > startUT)
                {
                    double liveDuration = currentUT - startUT;
                    if (liveDuration < 0.0
                        || double.IsNaN(liveDuration)
                        || double.IsInfinity(liveDuration))
                    {
                        liveDuration = 0.0;
                    }
                    ParsekLog.Verbose("SwitchSegment",
                        $"BuildWholeTreeMergeDialogBody: using live segment duration " +
                        $"recId={segment.RecordingId ?? "<null>"} " +
                        $"durationSec={liveDuration.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                        $"startUT={startUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                        $"currentUT={currentUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                        $"sessionId={session.SessionId:D} treeId={tree.Id ?? "<null>"}");
                    return liveDuration;
                }
                if (endUT < startUT)
                {
                    // currentUT unavailable (no Planetarium / regression
                    // edge) and the bounds are malformed: fall back to
                    // tree-wide duration and Warn so a real bug surfaces.
                    ParsekLog.Warn("SwitchSegment",
                        $"BuildWholeTreeMergeDialogBody: malformed-segment-bounds " +
                        $"recId={segment.RecordingId ?? "<null>"} " +
                        $"startUT={startUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                        $"endUT={endUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                        $"sessionId={session.SessionId:D} — falling back to tree-wide duration");
                    return ComputeTreeDurationRange(tree);
                }
                // LOW 1 (PR #876 round-6 review): endUT == startUT and
                // no live clock available. The function falls through to
                // the normal-path Verbose below reporting durationSec=0,
                // which a reader grepping for "using live segment
                // duration" would never see. Emit a greppable
                // `live-clock-unavailable` Verbose so the fallthrough
                // is visible in the diagnostic trail.
                ParsekLog.Verbose("SwitchSegment",
                    $"BuildWholeTreeMergeDialogBody: live-clock-unavailable " +
                    $"durationSec=0 " +
                    $"endUT={endUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"startUT={startUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"currentUT={currentUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"recId={segment.RecordingId ?? "<null>"} " +
                    $"sessionId={session.SessionId:D}");
            }

            double segmentDuration = endUT - startUT;
            if (segmentDuration < 0.0
                || double.IsNaN(segmentDuration)
                || double.IsInfinity(segmentDuration))
            {
                segmentDuration = 0.0;
            }
            ParsekLog.Verbose("SwitchSegment",
                $"BuildWholeTreeMergeDialogBody: using segment duration " +
                $"recId={segment.RecordingId ?? "<null>"} " +
                $"durationSec={segmentDuration.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"sessionId={session.SessionId:D} treeId={tree.Id ?? "<null>"}");
            return segmentDuration;
        }

        /// <summary>
        /// BUG #2: duration for the no-SwitchSegmentSession dialog flow (committed-tree
        /// resume → Map-Switch-To away). When this dialog's <paramref name="tree"/>
        /// carries a currently-live recording (the resumed committed recording the player
        /// just flew ~17s on), returns <c>currentUT - resumeStartUT</c> so the dialog
        /// shows the short live segment instead of the resumed recording's multi-year
        /// span. The resumed recording's Points are appended in place, so its
        /// <c>StartUT</c> is the original launch UT and is the WRONG anchor — only the
        /// per-session resume UT (captured in
        /// <see cref="ParsekFlight.ResumeCommittedActiveRecording"/>) yields the short
        /// elapsed time. Falls back to <see cref="ComputeTreeDurationRange"/> when: no
        /// live recording is armed, the live recording is not in this tree (cross-tree
        /// safety), the resume UT is non-finite, or the current clock is unavailable /
        /// regressed behind the resume UT. Reuses the session-armed branch's clamp so a
        /// UT regression cannot leak a negative duration.
        /// </summary>
        internal static double ResolveNoSessionDialogBodyDuration(RecordingTree tree)
        {
            string liveId;
            double resumeStartUT;
            TryGetLiveResumedSegment(out liveId, out resumeStartUT);

            // Gate: the live recording must belong to THIS dialog's tree (cross-tree
            // safety, mirroring the session-segment-not-in-tree fallback) AND the resume
            // anchor must be finite.
            bool liveInThisTree =
                !string.IsNullOrEmpty(liveId)
                && tree?.Recordings != null
                && tree.Recordings.ContainsKey(liveId);
            bool resumeFinite =
                !double.IsNaN(resumeStartUT) && !double.IsInfinity(resumeStartUT);

            if (liveInThisTree && resumeFinite)
            {
                double currentUT = CurrentUtForSegmentDuration();
                if (!double.IsNaN(currentUT)
                    && !double.IsInfinity(currentUT)
                    && currentUT > resumeStartUT)
                {
                    double liveDuration = currentUT - resumeStartUT;
                    // Reuse the session-armed branch's clamp: a UT regression
                    // (rewind crossing the resume UT, etc.) cannot leak a nonsense
                    // duration into the UI.
                    if (liveDuration < 0.0
                        || double.IsNaN(liveDuration)
                        || double.IsInfinity(liveDuration))
                    {
                        liveDuration = 0.0;
                    }
                    ParsekLog.Verbose("SwitchSegment",
                        $"BuildWholeTreeMergeDialogBody: duration source=live-resumed-segment " +
                        $"recId={liveId} " +
                        $"durationSec={liveDuration.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                        $"resumeStartUT={resumeStartUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                        $"currentUT={currentUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                        $"treeId={tree.Id ?? "<null>"}");
                    return liveDuration;
                }
            }

            // No live recording, not in this tree, non-finite resume UT, or current
            // clock unavailable / regressed behind the resume UT: fall back to the
            // whole-tree span exactly as before.
            double treeWide = ComputeTreeDurationRange(tree);
            ParsekLog.Verbose("SwitchSegment",
                $"BuildWholeTreeMergeDialogBody: duration source=tree-wide " +
                $"recId={(string.IsNullOrEmpty(liveId) ? "<none>" : liveId)} " +
                $"durationSec={treeWide.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"reason=no-session liveInThisTree={liveInThisTree} resumeFinite={resumeFinite} " +
                $"treeId={tree?.Id ?? "<null>"}");
            return treeWide;
        }

        /// <summary>
        /// Locate the recording the active re-fly session targets. Tries the
        /// pending tree first (so the lookup works whether the dialog fires
        /// before or after `RecordingStore.CommitPendingTree`), then falls
        /// back to the committed recordings list. Returns null if neither
        /// source has the recording — the caller falls back to whole-tree
        /// metadata.
        /// </summary>
        internal static Recording FindReFlyRecording(
            ReFlySessionMarker marker, RecordingTree pendingTree)
        {
            if (marker == null) return null;
            string targetId = marker.ActiveReFlyRecordingId;
            if (string.IsNullOrEmpty(targetId)) return null;

            if (pendingTree != null && pendingTree.Recordings != null
                && pendingTree.Recordings.TryGetValue(targetId, out Recording fromTree)
                && fromTree != null)
                return fromTree;

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return null;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (string.Equals(rec.RecordingId, targetId, System.StringComparison.Ordinal))
                    return rec;
            }
            return null;
        }

        /// <summary>
        /// Pure function: compute the total time span across all recordings in a tree.
        /// Returns 0 if the tree has no recordings.
        /// </summary>
        internal static double ComputeTreeDurationRange(RecordingTree tree)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return 0;

            double minStartUT = double.MaxValue;
            double maxEndUT = double.MinValue;
            foreach (var rec in tree.Recordings.Values)
            {
                double start = rec.StartUT;
                double end = rec.EndUT;
                if (start < minStartUT) minStartUT = start;
                if (end > maxEndUT) maxEndUT = end;
            }

            return (minStartUT < double.MaxValue && maxEndUT > double.MinValue)
                ? maxEndUT - minStartUT
                : 0;
        }
    }
}
