// [ERS-exempt] The EnterWatchMode seam verb reads RecordingStore.CommittedRecordings
// RAW: every ghost-engine API it drives (HasActiveGhost / IsGhostOnSameBody /
// IsGhostWithinVisualRange / WatchedRecordingIndex / EnterWatchMode) is keyed on the
// COMMITTED-LIST index, not the ERS index (which shifts after a Re-Fly supersede), so an
// ERS-filtered list would silently address a different vessel. Identical rationale to the
// production caller this verb reproduces (UI/MissionsWindowUI.DrawMissionWatchButton, which
// carries the same [ERS-exempt] note) and to RouteOrchestrator's list-alignment exemption.
// Test-command surface: unreachable without PARSEK_TEST_COMMANDS=1. File allowlisted in
// scripts/ers-els-audit-allowlist.txt.
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Player-workflow-lane partial: the thin Unity applier for the DEFERRED
    /// <c>EnterWatchMode</c> verb (the FOURTH strict reserved-name promotion since
    /// M-C1; wire token byte-identical before and after, only the response changes).
    ///
    /// <para>
    /// WHY THIS VERB EXISTS. Warping to a looped mission's next departure only pays
    /// off if something then WATCHES the replay - the Missions / Recordings window's
    /// "Watch" button, which points the flight camera at a ghost. Nothing unattended
    /// could reach it, so the flight-scene half of the player loop ended at the warp.
    /// </para>
    ///
    /// <para>
    /// ARGS. <c>index=&lt;committed recording index&gt;</c> (OPTIONAL) and
    /// <c>tree=&lt;treeId&gt;</c> (OPTIONAL). With no <c>index</c> the verb
    /// AUTO-SELECTS product-style, replicating
    /// <c>MissionsWindowUI.ResolveMissionWatchTarget</c>'s conjunction verbatim -
    /// the first candidate with <c>HasActiveGhost &amp;&amp; IsGhostOnSameBody
    /// &amp;&amp; IsGhostWithinVisualRange</c> - over ALL committed recordings,
    /// narrowed to one tree's members when <c>tree</c> is given. The narrowing uses
    /// the plain <c>RecordingTree.Recordings</c> membership walk rather than
    /// <c>MissionLoopUnitBuilder.ComputeTrimmedMemberWindows</c>: the trimmed-window
    /// path needs a MissionThroughLineView and the UI's composition roots, i.e. the
    /// whole Missions-window view pipeline, to answer a question this verb only asks
    /// as a SCOPE filter (which vessels belong to this flight). The trim exists to
    /// stop the UI offering a member the loop will not spawn; here the watchability
    /// conjunction already answers that from live engine truth - an untrimmed member
    /// has no active ghost.
    /// </para>
    ///
    /// <para>
    /// CRITICAL SEMANTICS. <c>ParsekFlight.EnterWatchMode(index)</c> is a TOGGLE
    /// (<c>WatchModeController.EnterWatchMode</c>: "if already watching this
    /// recording, exit") and is silent-failure-heavy - it returns void and simply
    /// returns on an out-of-range index, an unresolvable watch state, or a failed
    /// session start. So: (a) when the target is ALREADY the watched recording the
    /// verb completes OK immediately WITHOUT calling the toggle (calling it would
    /// EXIT watch mode - the exact inverse of what was asked); (b) otherwise it calls
    /// the toggle and DEFERS on the observed read-back
    /// <c>IsWatchingGhost &amp;&amp; WatchedRecordingIndex == index</c> - the same
    /// pair the in-game RuntimeTests assert on - so a silent refusal reports
    /// <c>ERROR watch-not-entered</c> instead of a false OK. The refusal CLASS is not
    /// re-derived here: the distance / body / resolve guards each log their own line,
    /// and re-deriving them would produce a second opinion that can disagree with the
    /// one that actually refused.
    /// </para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // EnterWatchMode two-phase state: the target committed index, the recording
        // id echoed in the terminal payload, and the settle-frame countdown. Re-armed
        // wholesale at the start of every EnterWatchModeImpl (like the TimeJump and
        // StartLoopPlayback fields), so they are deliberately not cleared in
        // ClearTwoPhase and no stale value can be read across commands.
        private int watchTargetIndex;
        private string watchTargetRecordingId;
        private int watchSettleFramesRemaining;

        private void EnterWatchModeImpl(ParsedCommand cmd)
        {
            string indexArg = ArgOrNull(cmd, "index");
            string treeArg = ArgOrNull(cmd, "tree");

            if (!TestCommandEnterWatchMode.TryParseIndexArg(indexArg, out int requestedIndex))
            {
                ParsekLog.Warn(Tag,
                    $"enterwatchmode rejected reason=index-arg-invalid index={indexArg ?? string.Empty}");
                SetExecResult("REJECTED", null, "index-arg-invalid");
                return;
            }

            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null)
            {
                ParsekLog.Warn(Tag, "enterwatchmode no-flight-instance");
                SetExecResult("ERROR", null, "no-flight-instance");
                return;
            }

            // [ERS-exempt] - see the file-header note (committed-LIST indices).
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            int count = committed != null ? committed.Count : 0;

            int index;
            if (requestedIndex >= 0)
            {
                if (requestedIndex >= count)
                {
                    ParsekLog.Warn(Tag,
                        $"enterwatchmode rejected reason=index-out-of-range index={requestedIndex} committed={count}");
                    SetExecResult("REJECTED", null, "index-out-of-range");
                    return;
                }
                index = requestedIndex;
            }
            else
            {
                RecordingTree scope = string.IsNullOrEmpty(treeArg)
                    ? null
                    : FindCommittedTreeById(treeArg);
                if (!string.IsNullOrEmpty(treeArg) && scope == null)
                {
                    ParsekLog.Warn(Tag,
                        $"enterwatchmode rejected reason=unknown-tree tree={treeArg}");
                    SetExecResult("REJECTED", null, "unknown-tree");
                    return;
                }

                var candidates = new List<TestCommandEnterWatchMode.WatchCandidate>(count);
                for (int i = 0; i < count; i++)
                {
                    Recording rec = committed[i];
                    bool inScope = scope == null
                        || (rec != null && !string.IsNullOrEmpty(rec.RecordingId)
                            && scope.Recordings != null
                            && scope.Recordings.ContainsKey(rec.RecordingId));
                    // The three engine probes are sampled ONLY for in-scope entries:
                    // each is a live ghost-state read, and an out-of-scope entry's
                    // answer can never change the selection.
                    candidates.Add(new TestCommandEnterWatchMode.WatchCandidate
                    {
                        Index = i,
                        InScope = inScope,
                        HasActiveGhost = inScope && flight.HasActiveGhost(i),
                        OnSameBody = inScope && flight.IsGhostOnSameBody(i),
                        WithinVisualRange = inScope && flight.IsGhostWithinVisualRange(i),
                    });
                }

                index = TestCommandEnterWatchMode.ResolveAutoWatchIndex(candidates);
                if (index < 0)
                {
                    ParsekLog.Warn(Tag,
                        $"enterwatchmode rejected reason=no-watchable-ghost committed={count} tree={treeArg ?? "(any)"}");
                    SetExecResult("REJECTED", null, "no-watchable-ghost");
                    return;
                }
            }

            string recId = index >= 0 && index < count && committed[index] != null
                ? (committed[index].RecordingId ?? string.Empty)
                : string.Empty;

            // (a) Idempotent short-circuit. EnterWatchMode is a TOGGLE, so calling it
            // on the already-watched index would EXIT watch mode. Single-phase OK.
            if (flight.IsWatchingGhost && flight.WatchedRecordingIndex == index)
            {
                ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                    "enterwatchmode already-watching: index={0} recId={1}", index, recId));
                SetExecResult("OK",
                    TestCommandEnterWatchMode.BuildCompletePayload(index, recId), null);
                return;
            }

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "enterwatchmode initiated: index={0} recId={1} tree={2} auto={3}",
                index, recId, treeArg ?? string.Empty, Bool(requestedIndex < 0)));

            flight.EnterWatchMode(index);

            watchTargetIndex = index;
            watchTargetRecordingId = recId;
            // Same short settle window the jump verbs use: watch entry runs a camera
            // session start whose read-back can trail the call by a frame.
            watchSettleFramesRemaining = TestCommandTimeJump.SettleFrames;
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteEnterWatchMode(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds("EnterWatchMode");
            if (watchSettleFramesRemaining > 0)
                watchSettleFramesRemaining--;

            ParsekFlight flight = ParsekFlight.Instance;
            // The observed read-back pair (the same one the in-game watch-mode
            // RuntimeTests assert): watching AND watching THIS recording.
            bool watchingTarget = flight != null
                && flight.IsWatchingGhost
                && flight.WatchedRecordingIndex == watchTargetIndex;

            WatchEntryCompletionDecision decision =
                TestCommandEnterWatchMode.DecideWatchCompletion(
                    elapsed, watchingTarget, watchSettleFramesRemaining, budget);
            if (decision == WatchEntryCompletionDecision.StillWaiting)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            int index = watchTargetIndex;
            string recId = watchTargetRecordingId;
            ClearTwoPhase();

            if (decision == WatchEntryCompletionDecision.CompleteOk)
            {
                ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                    "enterwatchmode complete: index={0} recId={1}", index, recId ?? string.Empty));
                EmitExecutedTerminal(id, seq, verb, "OK",
                    TestCommandEnterWatchMode.BuildCompletePayload(index, recId), null,
                    dequeueHead: true);
            }
            else // WatchTimeout
            {
                TestCommandDiagnostics.Timeout(id, verb, elapsed, "watch-not-entered");
                // The refusal class is NOT re-derived here: WatchModeController's
                // distance / body / resolve guards log their own Info lines at the
                // moment they refuse, and a second derivation could disagree with the
                // one that actually happened. The terminal names the timeout and the
                // target; the log names the cause.
                ParsekLog.Error(Tag, string.Format(CultureInfo.InvariantCulture,
                    "enterwatchmode timeout elapsed={0}s index={1} recId={2} " +
                    "(watch entry refused; see the CameraFollow guard lines for the reason)",
                    elapsed.ToString("F1", CultureInfo.InvariantCulture), index,
                    recId ?? string.Empty));
                EmitExecutedTerminal(id, seq, verb, "ERROR", null, "watch-not-entered",
                    dequeueHead: true);
            }
        }

        // Committed-tree lookup by id for the optional `tree` scope filter. Null when
        // no committed tree carries that id (the caller turns that into a typed
        // REJECTED rather than silently widening the search to every recording).
        private static RecordingTree FindCommittedTreeById(string treeId)
        {
            IReadOnlyList<RecordingTree> trees = RecordingStore.CommittedTrees;
            if (trees == null || string.IsNullOrEmpty(treeId))
                return null;
            for (int i = 0; i < trees.Count; i++)
                if (trees[i] != null && trees[i].Id == treeId)
                    return trees[i];
            return null;
        }
    }

    /// <summary>The two-phase EnterWatchMode completion outcome.</summary>
    internal enum WatchEntryCompletionDecision
    {
        /// <summary>Watch entry is not yet observable, or the settle window has not
        /// drained.</summary>
        StillWaiting,

        /// <summary>The read-back confirms the target is watched AND the settle
        /// window drained: terminal OK.</summary>
        CompleteOk,

        /// <summary>Budget expired without the read-back ever confirming: terminal
        /// ERROR (msg=watch-not-entered). This is the REAL failure mode, not a
        /// defensive one - EnterWatchMode is void and refuses silently.</summary>
        WatchTimeout,
    }

    /// <summary>
    /// Pure decision half of the EnterWatchMode verb (headlessly testable; the
    /// applier above owns the Unity / engine reads only).
    /// </summary>
    internal static class TestCommandEnterWatchMode
    {
        /// <summary>One committed recording as the auto-selector sees it: its
        /// committed-list index, whether the optional tree scope admits it, and the
        /// three live watchability probes.</summary>
        internal struct WatchCandidate
        {
            public int Index;
            public bool InScope;
            public bool HasActiveGhost;
            public bool OnSameBody;
            public bool WithinVisualRange;
        }

        /// <summary>
        /// Optional non-negative committed index. Absent (null / empty) parses as
        /// <c>-1</c>, the AUTO-SELECT sentinel, and returns true. A present value must
        /// be a non-negative InvariantCulture integer; a negative, fractional, or
        /// unparseable value is the REJECTED path (a locale comma or a "3.0" fails:
        /// the wire token is an integer index, not a number to coerce).
        /// </summary>
        internal static bool TryParseIndexArg(string raw, out int index)
        {
            index = -1;
            if (string.IsNullOrEmpty(raw))
                return true;
            if (!int.TryParse(raw, System.Globalization.NumberStyles.None,
                    CultureInfo.InvariantCulture, out int parsed))
                return false;
            if (parsed < 0)
                return false;
            index = parsed;
            return true;
        }

        /// <summary>
        /// The product auto-select: the FIRST in-scope candidate satisfying all three
        /// watchability probes, mirroring
        /// <c>MissionsWindowUI.ResolveMissionWatchTarget</c>'s conjunction and its
        /// first-match-wins order (ascending committed index). Returns -1 when nothing
        /// is watchable - a REJECTED, never a "watch something else" fallback: picking
        /// a different vessel than the one the scope names would certify the wrong
        /// thing.
        /// </summary>
        internal static int ResolveAutoWatchIndex(IReadOnlyList<WatchCandidate> candidates)
        {
            if (candidates == null)
                return -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                WatchCandidate c = candidates[i];
                if (c.InScope && c.HasActiveGhost && c.OnSameBody && c.WithinVisualRange)
                    return c.Index;
            }
            return -1;
        }

        /// <summary>
        /// Decide the two-phase watch-entry completion. Unlike the jump verbs there is
        /// no tolerance question - the read-back is a boolean identity
        /// (<c>IsWatchingGhost &amp;&amp; WatchedRecordingIndex == target</c>) - but
        /// the settle window is kept for the same reason: the camera session start can
        /// trail the call by a frame, and completing on the first true read-back would
        /// answer before the watch has actually taken. The budget expiry is the REAL
        /// failure mode (a silent refusal by a distance / body / resolve guard), not a
        /// defensive catch-all.
        /// </summary>
        internal static WatchEntryCompletionDecision DecideWatchCompletion(
            double elapsedSeconds, bool watchingTarget, int settleFramesRemaining,
            double budgetSeconds)
        {
            if (watchingTarget && settleFramesRemaining <= 0)
                return WatchEntryCompletionDecision.CompleteOk;
            if (elapsedSeconds >= budgetSeconds)
                return WatchEntryCompletionDecision.WatchTimeout;
            return WatchEntryCompletionDecision.StillWaiting;
        }

        /// <summary>Terminal completion payload: the watched committed index, its
        /// recording id (the stable handle a spec pins), and the flat
        /// <c>watching=true</c> claim the verdict stands behind.</summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(
            int index, string recId)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(
                    "index", index.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("recId", recId ?? string.Empty),
                new KeyValuePair<string, string>("watching", "true"),
            };
    }
}
