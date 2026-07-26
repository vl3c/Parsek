using System;

namespace Parsek
{
    /// <summary>
    /// Which tree <c>ParsekFlight.RestoreActiveTreeFromPending</c> must install
    /// as the live active tree.
    /// </summary>
    internal enum ReFlyRestoreTreeSource
    {
        /// <summary>
        /// The historical behaviour: adopt whatever occupies the single
        /// pending-tree slot. Correct for every non-Re-Fly restore and for the
        /// Re-Fly shape where the pending slot already holds the marker's tree.
        /// </summary>
        PendingTree = 0,

        /// <summary>
        /// An in-place-continuation Re-Fly is live and its tree is NOT the tree
        /// occupying the pending slot. The restore must bind the recorder to
        /// the marker's tree so the session's provisional actually records.
        /// </summary>
        MarkerTree = 1,
    }

    /// <summary>Pure result of <see cref="ReFlyRestoreAdoption.ResolveRestoreTree"/>.</summary>
    internal struct ReFlyRestoreTreeDecision
    {
        public ReFlyRestoreTreeSource Source;

        /// <summary>Marker tree id when <see cref="Source"/> is MarkerTree; null otherwise.</summary>
        public string TreeId;

        /// <summary>Grep-stable reason token for the restore log line.</summary>
        public string Reason;
    }

    /// <summary>
    /// R1-EMPTY-PROVISIONAL: decides which tree the post-Re-Fly restore adopts.
    ///
    /// <para>
    /// The bug #585 in-place-continuation carve-out in
    /// <c>ParsekFlight.RestoreActiveTreeFromPending</c> swaps the wait target to
    /// <see cref="ReFlySessionMarker.ActiveReFlyRecordingId"/> via
    /// <see cref="ReFlySessionMarker.ResolveInPlaceContinuationTarget"/>, which is
    /// gated on the restored tree's id matching <see cref="ReFlySessionMarker.TreeId"/>.
    /// That gate holds only because the pending slot USUALLY happens to carry the
    /// marker's tree: the Re-Fly's FLIGHT-&gt;FLIGHT quicksave load stashes whatever
    /// tree was live at invocation time as pending-Limbo, and the RP quicksave the
    /// load restores was authored during the origin's own flight.
    /// </para>
    ///
    /// <para>
    /// The pending slot is single-occupancy, so that coincidence breaks whenever the
    /// re-flown origin lives in a DIFFERENT tree from the one being flown when Re-Fly
    /// was clicked (re-fly a slot from an earlier mission; re-fly from
    /// SPACECENTER / TRACKSTATION with no live tree at all). The swap then refuses
    /// with <c>marker-tree-id-mismatch</c>, the restore waits for the pre-rewind
    /// vessel that the rewind just replaced, times out, and NOTHING is ever bound to
    /// the session's provisional. The relaunch takes the plain auto-record path,
    /// which creates a fresh tree by construction, and the merge later refuses to
    /// write supersede rows against a trajectory-less provisional - so the origin
    /// branch and the re-flown branch both stay live in ERS as unrelated histories.
    /// </para>
    ///
    /// <para>
    /// The fix is to stop sourcing the adoption from the pending slot when an
    /// in-place-continuation marker names a resolvable tree. Pure so every branch is
    /// unit-testable without a live scene or a running coroutine.
    /// </para>
    /// </summary>
    internal static class ReFlyRestoreAdoption
    {
        /// <summary>
        /// Picks the tree the restore coroutine must install as <c>activeTree</c>.
        /// </summary>
        /// <param name="marker">
        /// Live marker from <c>ParsekScenario.Instance.ActiveReFlySessionMarker</c>,
        /// or null. Read AFTER <c>RewindInvokeContext.Pending</c> clears so the
        /// marker write has certainly completed.
        /// </param>
        /// <param name="hasPendingTree">Whether the single pending slot is occupied.</param>
        /// <param name="pendingTreeId">Pending slot occupant's tree id (null when empty).</param>
        /// <param name="markerTreeResolvable">
        /// Whether the live side could resolve <c>marker.TreeId</c> to an in-memory
        /// tree (pending slot / committed trees / live active tree - see
        /// <c>RewindInvoker.FindTreeForReFlyFork</c>).
        /// </param>
        internal static ReFlyRestoreTreeDecision ResolveRestoreTree(
            ReFlySessionMarker marker,
            bool hasPendingTree,
            string pendingTreeId,
            bool markerTreeResolvable)
        {
            // Placeholder-mode markers (and no marker at all) keep the historical
            // path verbatim. The marker swap cannot fire for a placeholder shape
            // (ResolveInPlaceContinuationTarget returns "placeholder-pattern"), so
            // adopting the marker's tree would bind a recorder that the rest of the
            // placeholder flow does not expect; the merge-dialog fallback stays the
            // documented recovery there.
            if (!ReFlySessionMarker.IsInPlaceContinuation(marker))
                return Pending("no-inplace-refly-session");

            // Legacy markers without a tree id: ResolveInPlaceContinuationTarget
            // deliberately allows the swap against whatever tree it is handed
            // (Bug585InPlaceContinuationRestoreTests.EmptyMarkerTreeId_AllowsSwap),
            // so there is nothing better to adopt than the pending slot.
            if (string.IsNullOrEmpty(marker.TreeId))
                return Pending("refly-marker-has-no-treeid");

            // The bug #585 path: the pending slot already holds the marker's tree.
            // Byte-identical to pre-fix behaviour.
            if (hasPendingTree
                && !string.IsNullOrEmpty(pendingTreeId)
                && string.Equals(pendingTreeId, marker.TreeId, StringComparison.Ordinal))
            {
                return Pending("pending-slot-is-marker-tree");
            }

            // Marker names a tree we cannot find in memory. Nothing to adopt;
            // fall back to the pending slot (whose own gate bails when empty).
            if (!markerTreeResolvable)
                return Pending("refly-marker-tree-unresolvable");

            return new ReFlyRestoreTreeDecision
            {
                Source = ReFlyRestoreTreeSource.MarkerTree,
                TreeId = marker.TreeId,
                Reason = hasPendingTree
                    ? "refly-pending-slot-holds-foreign-tree"
                    : "refly-no-pending-tree",
            };
        }

        /// <summary>
        /// Early-detection predicate (R1-EMPTY-PROVISIONAL layer 2). True when
        /// auto-record is about to create a BRAND NEW single-node tree while an
        /// in-place-continuation Re-Fly session is live and names a different tree
        /// - i.e. the session's provisional has no recorder bound to it and the
        /// flight that is starting will not be the one the merge supersedes with.
        ///
        /// <para>
        /// This is the cheapest, earliest and most actionable observation point for
        /// the whole defect class: it fires seconds after the rewind load, while the
        /// player can still act, instead of at merge time inside the journal
        /// orchestrator.
        /// </para>
        /// </summary>
        internal static bool IsOrphanedFreshTreeStart(
            ReFlySessionMarker marker, string newTreeId)
        {
            if (!ReFlySessionMarker.IsInPlaceContinuation(marker))
                return false;
            if (string.IsNullOrEmpty(marker.TreeId))
                return false;
            if (string.IsNullOrEmpty(newTreeId))
                return false;
            return !string.Equals(newTreeId, marker.TreeId, StringComparison.Ordinal);
        }

        private static ReFlyRestoreTreeDecision Pending(string reason)
        {
            return new ReFlyRestoreTreeDecision
            {
                Source = ReFlyRestoreTreeSource.PendingTree,
                TreeId = null,
                Reason = reason,
            };
        }
    }
}
