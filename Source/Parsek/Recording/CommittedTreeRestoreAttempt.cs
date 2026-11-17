using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The committed-tree restore attempt (RecordingStore services plan, service H): the
    /// one-shot context armed when a committed spawned vessel is resumed through a
    /// copy-on-write clone of its committed tree. While it is armed, the clone's same-id
    /// game-state events past each recording's cutoff (its committed end, or the resume
    /// instant for the resumed recording whose end lies ahead of the clock) stay off disk
    /// (<see cref="ShouldSuppressCommittedTreeRestoreAttemptEventPersistence"/>), and a
    /// discard purges them (<see cref="DiscardCommittedTreeRestoreAttempt"/>). The state is
    /// save-scoped: a cold load forgets it (<see cref="ResetForLoad"/>).
    /// </summary>
    internal static class CommittedTreeRestoreAttempt
    {
        // One-shot context for the committed-spawned-vessel restore path. That
        // path keeps the original committed tree durable and gives the live
        // flight a copy-on-write clone. While the clone is unmerged, dirty
        // overlap sidecars must not be written over committed history, and
        // Discard must remove same-id game-state event tails authored after the
        // committed recording's original end UT.
        // The save-scoped state: the armed attempt. The Cold reset replaces the whole object
        // (ResetForLoad), so a field added here is forgotten with the rest.
        private sealed class ColdResetState
        {
            internal string TreeId;
            internal string Reason;
            internal HashSet<string> RecordingIds;
            internal Dictionary<string, double> EventCutoffs;
            // Each recording's committed end at arm time, before the resumed recording's clamp.
            internal Dictionary<string, double> EndUTs;
            internal string ResumedRecordingId;
            internal double ResumeUT = double.NaN;
        }

        private static ColdResetState state = new ColdResetState();

        private static string committedTreeRestoreAttemptTreeId
        {
            get => state.TreeId;
            set => state.TreeId = value;
        }

        private static string committedTreeRestoreAttemptReason
        {
            get => state.Reason;
            set => state.Reason = value;
        }

        private static HashSet<string> committedTreeRestoreAttemptRecordingIds
        {
            get => state.RecordingIds;
            set => state.RecordingIds = value;
        }

        private static Dictionary<string, double> committedTreeRestoreAttemptEventCutoffs
        {
            get => state.EventCutoffs;
            set => state.EventCutoffs = value;
        }

        private static Dictionary<string, double> committedTreeRestoreAttemptEndUTs
        {
            get => state.EndUTs;
            set => state.EndUTs = value;
        }

        private static string committedTreeRestoreAttemptResumedRecordingId
        {
            get => state.ResumedRecordingId;
            set => state.ResumedRecordingId = value;
        }

        private static double committedTreeRestoreAttemptResumeUT
        {
            get => state.ResumeUT;
            set => state.ResumeUT = value;
        }

        internal static bool HasCommittedTreeRestoreAttempt =>
            committedTreeRestoreAttemptTreeId != null;
        internal static bool HasCommittedTreeRestoreAttemptForTesting =>
            HasCommittedTreeRestoreAttempt;

        /// <summary>The tree id of the live committed-tree restore attempt, or null.</summary>
        internal static string CommittedTreeRestoreAttemptTreeId => committedTreeRestoreAttemptTreeId;

        /// <summary>The arm reason of the live committed-tree restore attempt, or null.</summary>
        internal static string CommittedTreeRestoreAttemptReason => committedTreeRestoreAttemptReason;

        /// <summary>Cold-load reset, called by <c>RecordingStore.ResetSaveScopedStateForLoad</c>
        /// at the point where it used to clear these fields itself. The attempt's events are
        /// reloaded from the save with the rest, so nothing is purged.</summary>
        internal static void ResetForLoad()
        {
            state = new ColdResetState();
        }

        /// <summary>Test reset, called by <c>RecordingStore.ResetForTestingInternal</c> at the
        /// point where it used to clear these fields itself.</summary>
        internal static void ResetForTesting()
        {
            state = new ColdResetState();
        }

        /// <summary>
        /// Arms the attempt for <paramref name="tree"/> (the committed copy). Every recording's
        /// event cutoff is its committed end UT, except the recording the attempt resumes
        /// (<paramref name="resumedRecordingId"/>): when its end lies ahead of the resume
        /// instant <paramref name="resumeUT"/> (a predicted terminal past the live clock), the
        /// flight from that instant on is the attempt's, so its cutoff is the resume instant
        /// (<see cref="ComputeResumedRecordingEventCutoff"/>). A null id or a non-finite UT keeps
        /// every cutoff at its end.
        /// </summary>
        internal static void ArmCommittedTreeRestoreAttempt(RecordingTree tree, string reason,
            string resumedRecordingId = null, double resumeUT = double.NaN)
        {
            if (tree == null || string.IsNullOrEmpty(tree.Id))
            {
                ParsekLog.Warn("RecordingStore",
                    $"ArmCommittedTreeRestoreAttempt skipped: tree={(tree == null ? "<null>" : "<no-id>")} " +
                    $"reason={reason ?? "<none>"}");
                return;
            }

            if (!string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId))
            {
                ParsekLog.Warn("RecordingStore",
                    $"ArmCommittedTreeRestoreAttempt: replacing stale context " +
                    $"tree={committedTreeRestoreAttemptTreeId} " +
                    $"reason={committedTreeRestoreAttemptReason ?? "<none>"}");
            }

            committedTreeRestoreAttemptTreeId = tree.Id;
            committedTreeRestoreAttemptReason = reason ?? "<unspecified>";
            committedTreeRestoreAttemptRecordingIds =
                BuildCommittedTreeRestoreRecordingIds(tree);
            committedTreeRestoreAttemptEndUTs =
                BuildCommittedTreeRestoreEventCutoffs(tree);
            committedTreeRestoreAttemptEventCutoffs = new Dictionary<string, double>(
                committedTreeRestoreAttemptEndUTs, StringComparer.Ordinal);
            committedTreeRestoreAttemptResumedRecordingId = resumedRecordingId;
            committedTreeRestoreAttemptResumeUT = resumeUT;
            string resumeSummary = ApplyResumedRecordingCutoff();
            ParsekLog.Info("RecordingStore",
                $"Armed committed-tree restore attempt for '{tree.TreeName ?? "<unnamed>"}' " +
                $"(id={tree.Id}, recordings={tree.Recordings?.Count ?? 0}, " +
                $"cutoffs={committedTreeRestoreAttemptEventCutoffs.Count}, " +
                $"{resumeSummary}, " +
                $"reason={committedTreeRestoreAttemptReason})");
        }

        /// <summary>The recording the live attempt resumed, or null (none named at arm).</summary>
        internal static string CommittedTreeRestoreAttemptResumedRecordingId =>
            HasCommittedTreeRestoreAttempt ? committedTreeRestoreAttemptResumedRecordingId : null;

        /// <summary>The live UT the attempt resumed at, or NaN (none named at arm).</summary>
        internal static double CommittedTreeRestoreAttemptResumeUT =>
            HasCommittedTreeRestoreAttempt ? committedTreeRestoreAttemptResumeUT : double.NaN;

        /// <summary>
        /// Moves the armed attempt's resume instant back to <paramref name="savedResumeUT"/>,
        /// the instant a save made inside the same attempt recorded, when the flight re-arms
        /// the same tree on the same recording later than that (an attempt resumed in place:
        /// the save holds no active tree, so the flight arms afresh at the load's clock). The
        /// attempt's held-back events lie between the two instants and are its own. Any other
        /// case leaves the attempt as armed. Returns true when the cutoff moved.
        /// </summary>
        internal static bool AdoptSavedResumeInstant(
            string savedResumedRecordingId, double savedResumeUT, string reason)
        {
            if (!HasCommittedTreeRestoreAttempt
                || string.IsNullOrEmpty(savedResumedRecordingId)
                || double.IsNaN(savedResumeUT) || double.IsInfinity(savedResumeUT))
            {
                return false;
            }

            string armedId = committedTreeRestoreAttemptResumedRecordingId;
            double armedUT = committedTreeRestoreAttemptResumeUT;
            bool sameRecording = string.Equals(armedId, savedResumedRecordingId, StringComparison.Ordinal);
            bool earlier = double.IsNaN(armedUT) || savedResumeUT < armedUT;
            if (!sameRecording || !earlier)
            {
                ParsekLog.Verbose("RecordingStore",
                    $"AdoptSavedResumeInstant ({reason ?? "<none>"}): kept the armed resume instant " +
                    $"rec={armedId ?? "<none>"} resumeUT={FormatUT(armedUT)} " +
                    $"(saved rec={savedResumedRecordingId} resumeUT={FormatUT(savedResumeUT)} " +
                    $"sameRecording={sameRecording} earlier={earlier})");
                return false;
            }

            committedTreeRestoreAttemptResumeUT = savedResumeUT;
            string summary = ApplyResumedRecordingCutoff();
            ParsekLog.Info("RecordingStore",
                $"AdoptSavedResumeInstant ({reason ?? "<none>"}): tree={committedTreeRestoreAttemptTreeId} " +
                $"resume instant moved back {FormatUT(armedUT)} -> {FormatUT(savedResumeUT)} " +
                $"from the save made inside the attempt ({summary})");
            return true;
        }

        /// <summary>
        /// Pure: the event cutoff of the recording a restore attempt resumes. Its committed end
        /// <paramref name="committedEndUT"/> stays the cutoff when the resume instant
        /// <paramref name="resumeUT"/> is not finite or the end is not after it (the end is in
        /// the past: everything up to it is history). When the end lies AFTER the resume instant
        /// (a predicted terminal ahead of the live clock), the vessel's flight from the resume
        /// instant on is the attempt's: the cutoff is the largest UT below
        /// <paramref name="resumeUT"/>, so an award stamped in the resume frame itself is the
        /// attempt's own. <paramref name="latestStoredEventUTAfterResume"/> is the latest stored
        /// event of the recording strictly after the resume instant and not after its end (NaN
        /// when none): such an event is history the store already holds, so the cutoff never
        /// drops below it and no stored event becomes the attempt's.
        /// </summary>
        internal static double ComputeResumedRecordingEventCutoff(
            double committedEndUT, double resumeUT, double latestStoredEventUTAfterResume)
        {
            if (double.IsNaN(resumeUT) || double.IsInfinity(resumeUT))
                return committedEndUT;
            if (double.IsNaN(committedEndUT) || !(committedEndUT > resumeUT))
                return committedEndUT;

            double cutoff = LargestDoubleBelow(resumeUT);
            if (!double.IsNaN(latestStoredEventUTAfterResume)
                && latestStoredEventUTAfterResume > cutoff)
            {
                cutoff = latestStoredEventUTAfterResume;
            }
            return Math.Min(committedEndUT, cutoff);
        }

        /// <summary>Pure: the largest double strictly below a finite <paramref name="value"/>.</summary>
        internal static double LargestDoubleBelow(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return value;
            if (value == 0.0)
                return -double.Epsilon;
            long bits = BitConverter.DoubleToInt64Bits(value);
            bits += value > 0.0 ? -1 : 1;
            return BitConverter.Int64BitsToDouble(bits);
        }

        /// <summary>
        /// Pure: the latest UT among <paramref name="events"/> of <paramref name="recordingId"/>
        /// strictly after <paramref name="afterUT"/> and not after <paramref name="upToUT"/>, and
        /// how many lie exactly at <paramref name="afterUT"/>. NaN when none.
        /// </summary>
        internal static double LatestRecordingEventUTInWindow(
            IEnumerable<GameStateEvent> events, string recordingId, double afterUT, double upToUT,
            ref int atAfterUT)
        {
            double latest = double.NaN;
            if (events == null || string.IsNullOrEmpty(recordingId))
                return latest;
            foreach (GameStateEvent e in events)
            {
                if (!string.Equals(e.recordingId, recordingId, StringComparison.Ordinal))
                    continue;
                if (e.ut == afterUT)
                {
                    atAfterUT++;
                    continue;
                }
                if (!(e.ut > afterUT) || e.ut > upToUT)
                    continue;
                if (double.IsNaN(latest) || e.ut > latest)
                    latest = e.ut;
            }
            return latest;
        }

        // Recomputes the resumed recording's cutoff from the end the arm built and the resume
        // instant, over the events the stores hold now (the attempt's history). Returns the
        // summary the arm and the adopt lines print.
        private static string ApplyResumedRecordingCutoff()
        {
            string recId = committedTreeRestoreAttemptResumedRecordingId;
            double resumeUT = committedTreeRestoreAttemptResumeUT;
            if (string.IsNullOrEmpty(recId))
                return "resumed=<none>";

            if (committedTreeRestoreAttemptEndUTs == null
                || !committedTreeRestoreAttemptEndUTs.TryGetValue(recId, out double endUT))
            {
                return $"resumed={recId} resumeUT={FormatUT(resumeUT)} resumedCutoff=<none> (no committed end)";
            }

            int atResume = 0;
            double latestAfter = double.NaN;
            if (!double.IsNaN(resumeUT) && !double.IsInfinity(resumeUT))
            {
                latestAfter = LatestRecordingEventUTInWindow(
                    GameStateStore.Events, recId, resumeUT, endUT, ref atResume);
                IReadOnlyList<Milestone> milestones = MilestoneStore.Milestones;
                for (int i = 0; i < milestones.Count; i++)
                {
                    double m = LatestRecordingEventUTInWindow(
                        milestones[i].Events, recId, resumeUT, endUT, ref atResume);
                    if (!double.IsNaN(m) && (double.IsNaN(latestAfter) || m > latestAfter))
                        latestAfter = m;
                }
            }

            double cutoff = ComputeResumedRecordingEventCutoff(endUT, resumeUT, latestAfter);
            committedTreeRestoreAttemptEventCutoffs[recId] = cutoff;
            string rule;
            if (double.IsNaN(resumeUT) || double.IsInfinity(resumeUT))
                rule = "end (no live clock)";
            else if (!(endUT > resumeUT))
                rule = "end (not ahead of the clock)";
            else if (!double.IsNaN(latestAfter) && cutoff == latestAfter)
                rule = "latest stored event after the resume instant";
            else
                rule = "resume instant (end ahead of the clock)";
            return $"resumed={recId} resumeUT={FormatUT(resumeUT)} endUT={FormatUT(endUT)} " +
                $"resumedCutoff={FormatUT(cutoff)} rule={rule} " +
                $"storedEventsAtResume={atResume.ToString(CultureInfo.InvariantCulture)}";
        }

        private static string FormatUT(double ut) => ut.ToString("R", CultureInfo.InvariantCulture);

        internal static void ClearCommittedTreeRestoreAttempt(string reason)
        {
            if (string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId))
                return;

            ParsekLog.Verbose("RecordingStore",
                $"Cleared committed-tree restore attempt " +
                $"tree={committedTreeRestoreAttemptTreeId} " +
                $"reason={reason ?? "<none>"} " +
                $"armedReason={committedTreeRestoreAttemptReason ?? "<none>"}");
            committedTreeRestoreAttemptTreeId = null;
            committedTreeRestoreAttemptReason = null;
            committedTreeRestoreAttemptRecordingIds = null;
            committedTreeRestoreAttemptEventCutoffs = null;
            committedTreeRestoreAttemptEndUTs = null;
            committedTreeRestoreAttemptResumedRecordingId = null;
            committedTreeRestoreAttemptResumeUT = double.NaN;
        }

        /// <summary>
        /// Discards the armed committed-tree restore attempt as a whole: purges the
        /// attempt's same-id post-cutoff game-state event tails FIRST (see
        /// <see cref="PurgeCommittedTreeRestoreAttemptEventTails"/> - the clear nulls
        /// the cutoff map), then clears it. No-op when no attempt is armed.
        /// <paramref name="preserveIrreversibleTails"/> is the ledger's re-home step
        /// (<c>LedgerOrchestrator.PreserveIrreversibleRestoreAttemptTailsOnDiscard</c>) on a LIVE
        /// discard (KSP keeps what the player did in the attempt, so its irreversible tail events
        /// and science become direct ledger rows before the purge) and null on a rollback (a
        /// rewind or a load reverts KSP with the save). The caller passes it, so this type does
        /// not reference the orchestrator.
        /// Discard paths whose caller drops the attempt's clone tree before any
        /// drop-site id match can run (the no-op no-session resume revert, the
        /// switch-segment scoped discard, the pre-switch dialog discard) must call
        /// THIS, not the bare clear: the caller-side clear leaves the clone's
        /// suppressed post-cutoff events to become durable on the next save.
        /// </summary>
        internal static void DiscardCommittedTreeRestoreAttempt(
            string reason, Action<IReadOnlyDictionary<string, double>, string> preserveIrreversibleTails)
        {
            if (string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId))
                return;

            PurgeCommittedTreeRestoreAttemptEventTails(
                reason ?? "<none>", preserveIrreversibleTails);
            ClearCommittedTreeRestoreAttempt(reason);
        }

        internal static void ClearCommittedTreeRestoreAttemptForTree(string treeId, string reason)
        {
            if (!DroppedTreeMatchesArmedRestoreAttempt(committedTreeRestoreAttemptTreeId, treeId))
                return;

            ClearCommittedTreeRestoreAttempt(reason);
        }

        /// <summary>
        /// Pure: true when a tree a scene reset is about to drop is the clone the armed
        /// committed-tree restore attempt guards (same tree id), so the attempt must be
        /// cleared with it. An attempt armed for a different tree id stays armed; a null
        /// or empty id on either side never clears.
        /// </summary>
        internal static bool DroppedTreeMatchesArmedRestoreAttempt(
            string armedAttemptTreeId, string droppedTreeId)
        {
            if (string.IsNullOrEmpty(armedAttemptTreeId) || string.IsNullOrEmpty(droppedTreeId))
                return false;

            return string.Equals(armedAttemptTreeId, droppedTreeId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Clears the armed committed-tree restore attempt when <paramref name="droppedTreeId"/>
        /// is its clone, so a reset that drops the clone tree (for example
        /// <c>ParsekFlight.ResetFlightReadyState</c>) leaves no stale attempt armed: while it
        /// stays armed every OnSave defers the pending-event milestone flush and the next arm
        /// logs a replacing-stale-context warn. The attempt's same-id post-cutoff event tails
        /// are purged FIRST through <see cref="DiscardCommittedTreeRestoreAttempt"/> (the same
        /// purge-then-clear order DiscardPendingTree uses), because the clear nulls the cutoff
        /// map and the clone's abandoned post-restore events must not become durable on the
        /// next save. An attempt for another tree id, or no attempt at all, is left untouched.
        /// <paramref name="preserveIrreversibleTails"/> as in
        /// <see cref="DiscardCommittedTreeRestoreAttempt"/>: the re-home step when the drop is a
        /// live discard, null when a load or rewind already reverted KSP. Returns true when an
        /// attempt was cleared.
        /// </summary>
        internal static bool ClearCommittedTreeRestoreAttemptForDroppedTree(
            string droppedTreeId, string reason,
            Action<IReadOnlyDictionary<string, double>, string> preserveIrreversibleTails)
        {
            if (!DroppedTreeMatchesArmedRestoreAttempt(
                    committedTreeRestoreAttemptTreeId, droppedTreeId))
                return false;

            ParsekLog.Info("RecordingStore",
                $"ClearCommittedTreeRestoreAttemptForDroppedTree: dropped tree={droppedTreeId} " +
                $"matches the armed restore attempt; clearing it reason={reason ?? "<none>"} " +
                $"preserveIrreversibleTails={preserveIrreversibleTails != null}");
            DiscardCommittedTreeRestoreAttempt(reason, preserveIrreversibleTails);
            return true;
        }

        /// <summary>
        /// True when <paramref name="treeId"/> is the tree currently armed as a
        /// committed-tree restore attempt (a copy-on-write clone of a committed
        /// tree is the live active tree). Used by the in-flight discard helper to
        /// detect a live committed clone BEFORE the discard clears the attempt,
        /// so it can tear the clone down (the committed original survives in
        /// committedTrees) instead of leaving it to strand.
        /// </summary>
        internal static bool IsCommittedTreeRestoreAttemptTree(string treeId)
        {
            return !string.IsNullOrEmpty(treeId)
                && string.Equals(committedTreeRestoreAttemptTreeId, treeId,
                    StringComparison.Ordinal);
        }

        internal static bool IsCommittedTreeRestoreAttemptRecordingId(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)
                || committedTreeRestoreAttemptRecordingIds == null)
                return false;

            return committedTreeRestoreAttemptRecordingIds.Contains(recordingId);
        }

        /// <summary>
        /// The armed attempt's event cutoff for <paramref name="recordingId"/> (the recording's
        /// committed end UT at arm time, or just below the resume instant for the resumed
        /// recording whose end lies ahead of it, see
        /// <see cref="ComputeResumedRecordingEventCutoff"/>): an event of that recording at or before it is
        /// committed history, one after it is the clone's own. False when no attempt is armed
        /// or the recording has no cutoff.
        /// </summary>
        internal static bool TryGetEventCutoff(string recordingId, out double cutoffUT)
        {
            cutoffUT = double.NaN;
            if (string.IsNullOrEmpty(recordingId)
                || string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId)
                || committedTreeRestoreAttemptEventCutoffs == null)
            {
                return false;
            }

            return committedTreeRestoreAttemptEventCutoffs.TryGetValue(recordingId, out cutoffUT);
        }

        internal static bool ShouldSuppressCommittedTreeRestoreAttemptEventPersistence(
            GameStateEvent evt)
        {
            string recordingId = evt.recordingId;
            if (string.IsNullOrEmpty(recordingId)
                || string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId))
            {
                return false;
            }

            // Switch-segment narrowing (segment-scoped-switch-fly-autorecord Phase D):
            // a marker-owned new recording id created by SwitchSegmentSession is real
            // pending state and must survive F5/save/reload, even when a #866
            // committed-tree restore attempt is concurrently armed. Bypass before the
            // existing same-id cutoff / pending-only checks so the suppression contract
            // for original committed recording ids remains unchanged.
            if (SwitchSegmentAttempts.IsMarkerOwnedSwitchSegmentRecordingId(recordingId))
            {
                var session = ParsekScenario.Instance?.ActiveSwitchSegmentSession;
                ParsekLog.Verbose("RecordingStore",
                    $"event persistence not-suppressed reason=marker-owned-switch-segment " +
                    $"recId={recordingId} " +
                    $"sessionId={(session != null ? session.SessionId.ToString("D", CultureInfo.InvariantCulture) : "<null>")}");
                return false;
            }

            if (committedTreeRestoreAttemptEventCutoffs != null
                && committedTreeRestoreAttemptEventCutoffs.TryGetValue(
                    recordingId,
                    out double cutoffUT))
            {
                return evt.ut > cutoffUT;
            }

            return IsPendingOnlyCommittedTreeRestoreAttemptRecordingId(recordingId);
        }

        private static bool IsPendingOnlyCommittedTreeRestoreAttemptRecordingId(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)
                || string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId))
            {
                return false;
            }

            if (committedTreeRestoreAttemptRecordingIds != null
                && committedTreeRestoreAttemptRecordingIds.Contains(recordingId))
            {
                return false;
            }

            if (RecordingStore.IsCommittedRecordingId(recordingId))
                return false;

            return TreeContainsRecordingId(
                    PendingTreeSlot.PendingTree,
                    committedTreeRestoreAttemptTreeId,
                    recordingId)
                || TreeContainsRecordingId(
                    PendingTreeSlot.SavedPendingTreeDuringActiveRestore,
                    committedTreeRestoreAttemptTreeId,
                    recordingId)
                || ParsekFlight.IsActiveTreeRecordingIdForTree(
                    recordingId,
                    committedTreeRestoreAttemptTreeId);
        }

        internal static bool TreeContainsRecordingId(
            RecordingTree tree,
            string treeId,
            string recordingId)
        {
            return tree != null
                && !string.IsNullOrEmpty(treeId)
                && string.Equals(tree.Id, treeId, StringComparison.Ordinal)
                && tree.Recordings != null
                && tree.Recordings.ContainsKey(recordingId);
        }

        internal static HashSet<string> BuildCommittedTreeRestoreRecordingIds(RecordingTree tree)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (tree?.Recordings == null)
                return result;

            foreach (Recording rec in tree.Recordings.Values)
            {
                if (!string.IsNullOrEmpty(rec?.RecordingId))
                    result.Add(rec.RecordingId);
            }

            return result;
        }

        internal static Dictionary<string, double> BuildCommittedTreeRestoreEventCutoffs(
            RecordingTree tree)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            if (tree?.Recordings == null)
                return result;

            foreach (Recording rec in tree.Recordings.Values)
            {
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (!rec.HasActualTrajectoryBounds && double.IsNaN(rec.ExplicitEndUT))
                    continue;

                double cutoff = rec.EndUT;
                if (double.IsNaN(cutoff) || double.IsInfinity(cutoff))
                    continue;

                result[rec.RecordingId] = cutoff;
            }

            return result;
        }

        internal static bool PendingTreeMatchesCommittedTreeRestoreAttempt()
        {
            return PendingTreeSlot.PendingTree != null
                && !string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId)
                && string.Equals(
                    PendingTreeSlot.PendingTree.Id,
                    committedTreeRestoreAttemptTreeId,
                    StringComparison.Ordinal);
        }

        /// <summary>
        /// Purges the armed attempt's same-id post-cutoff game-state event tails - the events
        /// <see cref="ShouldSuppressCommittedTreeRestoreAttemptEventPersistence"/> keeps off
        /// disk while the attempt is armed - keyed by the cutoff map built at arm time. Call
        /// BEFORE <see cref="ClearCommittedTreeRestoreAttempt"/>: the clear nulls that map.
        /// <paramref name="contextLabel"/> is the per-recording reason GameStateStore logs,
        /// <paramref name="warnLabel"/> prefixes the summary line (emitted only when
        /// something was purged). When <paramref name="preserveIrreversibleTails"/> is set (a
        /// live discard), the tails' irreversible events and science subjects are re-homed to the
        /// direct ledger first through it (the caller passes
        /// <c>LedgerOrchestrator.PreserveIrreversibleRestoreAttemptTailsOnDiscard</c>).
        /// Returns the number of events purged.
        /// </summary>
        private static int PurgeCommittedTreeRestoreAttemptEventTails(
            string contextLabel,
            Action<IReadOnlyDictionary<string, double>, string> preserveIrreversibleTails,
            string warnLabel = null)
        {
            if (committedTreeRestoreAttemptEventCutoffs == null
                || committedTreeRestoreAttemptEventCutoffs.Count == 0)
            {
                return 0;
            }

            if (preserveIrreversibleTails != null)
                preserveIrreversibleTails(
                    committedTreeRestoreAttemptEventCutoffs, contextLabel);

            int purged = 0;
            foreach (var kvp in committedTreeRestoreAttemptEventCutoffs)
            {
                purged += GameStateStore.PurgeEventsForRecordingAfterUT(
                    kvp.Key,
                    kvp.Value,
                    contextLabel);
            }

            if (purged > 0)
            {
                ParsekLog.Warn("RecordingStore",
                    $"{warnLabel ?? contextLabel}: purged {purged} same-id game-state event tail(s) " +
                    $"for committed-tree restore attempt tree={committedTreeRestoreAttemptTreeId} " +
                    $"armedReason={committedTreeRestoreAttemptReason ?? "<none>"}");
            }

            return purged;
        }

        internal static int PurgeCommittedTreeRestoreAttemptEventTailsForPendingDiscard(
            Action<IReadOnlyDictionary<string, double>, string> preserveIrreversibleTails)
        {
            if (!PendingTreeMatchesCommittedTreeRestoreAttempt()
                || committedTreeRestoreAttemptEventCutoffs == null
                || committedTreeRestoreAttemptEventCutoffs.Count == 0)
            {
                return 0;
            }

            return PurgeCommittedTreeRestoreAttemptEventTails(
                $"DiscardPendingTree committed-spawned-vessel attempt '{PendingTreeSlot.PendingTree.TreeName}'",
                preserveIrreversibleTails,
                "DiscardPendingTree");
        }
    }
}
