using System;

namespace Parsek
{
    /// <summary>
    /// Which conclusion route a Re-Fly session takes when the merge dialog's
    /// Commit branch cannot find the session's provisional in
    /// the flat committed-recordings list.
    /// </summary>
    internal enum ReFlyConclusionRouteKind
    {
        /// <summary>
        /// The provisional resolved normally — the ordinary journaled supersede
        /// merge runs. Never returned by <see cref="ReFlyConclusionRoute.Classify"/>
        /// (that helper only classifies the not-found case); present so callers can
        /// name the happy path in logs.
        /// </summary>
        Supersede,

        /// <summary>
        /// The session's provisional was retired this session by a leaf-prune pass
        /// because it carried no playable payload. Nothing was flown into it, so
        /// there is nothing to supersede the origin with: take the named, in-session
        /// no-op conclusion (0 supersede rows, 0 tombstones, marker cleared).
        /// </summary>
        NoOpConclusion,

        /// <summary>
        /// The provisional is absent for a reason this session cannot explain.
        /// Leave the marker in place and let <see cref="LoadTimeSweep"/> reconcile
        /// on the next load — the conservative pre-2026-08 behaviour, kept for the
        /// genuinely anomalous case.
        /// </summary>
        LeaveForSweep,
    }

    /// <summary>Pure classification result, with a grep-stable reason token.</summary>
    internal struct ReFlyConclusionDecision
    {
        public ReFlyConclusionRouteKind Kind;

        /// <summary>Grep-stable reason token for the decision log line.</summary>
        public string Reason;
    }

    /// <summary>
    /// REFLY-CONCLUSION-SKIPS-APPENDRELATIONS (found 2026-08-11 by S4.2's first
    /// flight): a Re-Fly session that ends without flying could reach the merge
    /// with its provisional deleted out from under the durable marker, so
    /// <c>MergeDialog.TryCommitReFlySupersede</c> bailed one step ABOVE
    /// <see cref="SupersedeCommit.AppendRelations"/> and the whole supersede /
    /// tombstone machinery never ran. The end state was benign (0 rows, origin
    /// effective) but it was reached through a WARN cascade plus a cross-load
    /// zombie sweep instead of the designed, named refusal.
    ///
    /// <para>
    /// The route split established from the two collected flights:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     S4.1 (2026-07-31, green): the provisional is pruned from
    ///     <c>tree.Recordings</c> as a zero-point leaf, but it is STILL in the
    ///     flat committed-recordings list where
    ///     <c>RewindInvoker.AddProvisional</c> put it at invoke time, so the merge
    ///     finds it and reaches the named refusal.
    ///   </description></item>
    ///   <item><description>
    ///     S4.2 (2026-08-11, red): a quicksave/quickload happened between the
    ///     rewind and the conclusion (the in-game batch's isolation revert; for a
    ///     player, any F5/F9). Hydration puts the NotCommitted provisional back
    ///     into <c>tree.Recordings</c> ONLY — <c>ReconcileInPlaceForkIntoTreeIfNeeded</c>
    ///     logs exactly that. The prune then removes it from the tree too, so
    ///     <c>CommitPendingTree</c> never re-adds it and both surfaces are empty.
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// So the two routes were separated by an accident of load history, not by
    /// anything about the session. The fix has two halves: the prune hands the
    /// retired provisional over instead of dropping it on the floor (see
    /// <see cref="ReFlyProvisionalRetirement"/>), and the conclusion classifies the
    /// absence here instead of bailing blind.
    /// </para>
    ///
    /// <para>Pure so every branch is unit-testable without a live scene.</para>
    /// </summary>
    internal static class ReFlyConclusionRoute
    {
        /// <summary>
        /// Classifies a provisional that <c>FindCommittedRecording</c> could not
        /// resolve. <paramref name="retiredProvisional"/> is whatever the prune
        /// handed over for this session (null when nothing was handed over), and
        /// <paramref name="retiredValidatesAsSupersedeTarget"/> is
        /// <c>SupersedeCommit.ValidateSupersedeTarget</c> evaluated on it by the
        /// caller (kept as a parameter so this stays free of Recording payload
        /// walks and testable in isolation).
        ///
        /// <para>
        /// The no-op route is taken ONLY when the handed-over recording provably
        /// cannot be a supersede target. A retired recording that WOULD validate is
        /// real payload the session owes a supersede for; concluding it as a no-op
        /// would silently drop a legitimate merge, so that case falls back to the
        /// sweep (and the prune guard upstream is supposed to have kept it alive in
        /// the first place).
        /// </para>
        /// </summary>
        internal static ReFlyConclusionDecision Classify(
            ReFlySessionMarker marker,
            string missingRecordingId,
            Recording retiredProvisional,
            bool retiredValidatesAsSupersedeTarget)
        {
            if (marker == null)
                return Decide(ReFlyConclusionRouteKind.LeaveForSweep, "no-marker");
            if (retiredProvisional == null)
                return Decide(ReFlyConclusionRouteKind.LeaveForSweep, "no-retired-provisional");
            if (string.IsNullOrEmpty(retiredProvisional.RecordingId))
                return Decide(ReFlyConclusionRouteKind.LeaveForSweep, "retired-provisional-has-no-id");
            if (!string.IsNullOrEmpty(missingRecordingId)
                && !string.Equals(
                    retiredProvisional.RecordingId,
                    missingRecordingId,
                    StringComparison.Ordinal))
            {
                return Decide(ReFlyConclusionRouteKind.LeaveForSweep, "retired-provisional-id-mismatch");
            }
            if (retiredValidatesAsSupersedeTarget)
                return Decide(ReFlyConclusionRouteKind.LeaveForSweep, "retired-provisional-owes-supersede");

            return Decide(ReFlyConclusionRouteKind.NoOpConclusion, "retired-empty-provisional");
        }

        /// <summary>
        /// Prune-side half of the same decision: what a leaf-prune pass must do
        /// with a recording it is about to remove, given the live Re-Fly marker.
        ///
        /// <para>
        /// A hygiene pass must never delete a recording the live session owes a
        /// supersede for. The prune's payload test
        /// (<c>ParsekFlight.IsZeroPointLeaf</c>: Points + OrbitSegments + playable
        /// TrackSections + SurfacePos) and the merge's payload test
        /// (<c>SupersedeCommit.ValidateSupersedeTarget</c>: Points + OrbitSegments
        /// + playable TrackSections) now share the section term, so the specific
        /// hole this guard was written for — a section-authoritative provisional
        /// reading as zero-point while being a valid supersede target — is closed at
        /// the predicate.
        /// </para>
        /// <para>
        /// This guard STAYS as belt-and-braces. The two predicates are still not the
        /// same test (SurfacePos counts only in the prune), they live in different
        /// files and can drift again, and the cost of being wrong here is deleting a
        /// recording the live session owes a supersede for. When they disagree, the
        /// merge's test wins and the recording stays.
        /// </para>
        /// </summary>
        internal static ReFlyProvisionalPruneAction ClassifyProvisionalPrune(
            ReFlySessionMarker marker,
            string recordingIdBeingPruned,
            bool validatesAsSupersedeTarget)
        {
            if (marker == null
                || string.IsNullOrEmpty(recordingIdBeingPruned)
                || string.IsNullOrEmpty(marker.ActiveReFlyRecordingId)
                || !string.Equals(
                    recordingIdBeingPruned,
                    marker.ActiveReFlyRecordingId,
                    StringComparison.Ordinal))
            {
                return ReFlyProvisionalPruneAction.NotTheProvisional;
            }

            return validatesAsSupersedeTarget
                ? ReFlyProvisionalPruneAction.KeepOwesSupersede
                : ReFlyProvisionalPruneAction.RetireEmpty;
        }

        private static ReFlyConclusionDecision Decide(ReFlyConclusionRouteKind kind, string reason)
        {
            return new ReFlyConclusionDecision { Kind = kind, Reason = reason };
        }
    }

    /// <summary>What a leaf-prune pass does with the recording it is visiting.</summary>
    internal enum ReFlyProvisionalPruneAction
    {
        /// <summary>Not the live session's provisional — prune normally.</summary>
        NotTheProvisional,

        /// <summary>
        /// It IS the provisional and it carries payload the merge would accept as a
        /// supersede target. Keep it: the session's own conclusion decides its fate.
        /// </summary>
        KeepOwesSupersede,

        /// <summary>
        /// It IS the provisional and the merge would refuse it. Prune it as before,
        /// but hand it to <see cref="ReFlyProvisionalRetirement"/> so the conclusion
        /// can take the named no-op route instead of bailing to the load-time sweep.
        /// </summary>
        RetireEmpty,
    }

    /// <summary>
    /// Transient (in-memory, never serialized) hand-over slot for a Re-Fly
    /// provisional that a leaf-prune pass retired during the live session.
    ///
    /// <para>
    /// Deliberately NOT a marker field: <see cref="ReFlySessionMarker"/>'s six
    /// durable fields are validated by <see cref="LoadTimeSweep"/> and the marker
    /// is the stale-marker guard's input; widening it for a same-session hint would
    /// buy schema churn for nothing. The retirement is only meaningful between the
    /// scene-exit finalize and the merge-dialog answer, both inside one process. A
    /// crash in that window loses the hint and the conclusion falls back to exactly
    /// the pre-fix behaviour (marker survives, next load's sweep reconciles), which
    /// is why no journal is needed on this route.
    /// </para>
    /// </summary>
    internal static class ReFlyProvisionalRetirement
    {
        private static string sessionId;
        private static string recordingId;
        private static string reason;
        private static Recording recording;

        internal static string SessionId => sessionId;
        internal static string RecordingId => recordingId;

        /// <summary>
        /// Records that <paramref name="rec"/>, the active Re-Fly session's
        /// provisional, was removed by a prune pass. Overwrites any prior note —
        /// one session has one provisional, and a later prune of the same id is the
        /// authoritative one.
        /// </summary>
        internal static void Note(ReFlySessionMarker marker, Recording rec, string retireReason)
        {
            if (marker == null || rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return;
            sessionId = marker.SessionId;
            recordingId = rec.RecordingId;
            reason = retireReason;
            recording = rec;
        }

        /// <summary>
        /// Returns the retired provisional for <paramref name="marker"/>'s session
        /// and CLEARS the slot (single-shot: a conclusion consumes it once). Returns
        /// false when nothing was noted, or when the note belongs to a different
        /// session — a stale note from an earlier session must never be adopted as
        /// this one's conclusion.
        /// </summary>
        internal static bool TryTake(
            ReFlySessionMarker marker, out Recording retired, out string retiredReason)
        {
            retired = null;
            retiredReason = null;
            if (marker == null || recording == null)
                return false;
            if (!string.Equals(sessionId, marker.SessionId, StringComparison.Ordinal))
                return false;

            retired = recording;
            retiredReason = reason;
            Clear("taken-by-conclusion");
            return true;
        }

        internal static void Clear(string clearReason)
        {
            if (recording != null)
            {
                ParsekLog.Verbose("ReFlySession",
                    $"ReFlyProvisionalRetirement cleared rec={recordingId ?? "<none>"} " +
                    $"sess={sessionId ?? "<none>"} reason={clearReason ?? "<none>"}");
            }
            sessionId = null;
            recordingId = null;
            reason = null;
            recording = null;
        }

        internal static void ResetForTesting() => Clear("reset-for-testing");
    }
}
