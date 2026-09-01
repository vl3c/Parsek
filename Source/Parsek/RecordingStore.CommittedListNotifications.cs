using System;

namespace Parsek
{
    public static partial class RecordingStore
    {
        // ─── Committed-list structural notifications ────────────────────────
        //
        // Index-keyed live state (ghost engine slots, held ghosts, map-presence dicts,
        // watch-mode index, chain continuation indices, KSC ghost slots) mirrors
        // committedRecordings by position. Every mid-list mutation of that list must raise
        // these so the scene controller (ParsekFlight / ParsekKSC / ParsekTrackingStation)
        // can shift that state in step. Raised by RunOptimizationPass (merge removal +
        // split insert), InsertCommittedAfter, and every removal helper through
        // RemoveCommittedAtWithNotifications (RemoveRecordingAt, RemoveCommittedInternal,
        // RemoveCommittedById, RemoveChainRecordings, RemoveCommittedTreeById).
        // ClearCommittedInternal wipes the whole list without per-item notifications; its
        // only caller runs inside ParsekScenario.OnLoad before any scene controller holds
        // ghost state.

        /// <summary>
        /// Raised immediately BEFORE a committed recording leaves the list. The index still
        /// addresses <c>removed</c> and the list is unshifted, so a subscriber can tear
        /// down index-keyed ghost state through the same lookups it uses every frame.
        /// </summary>
        internal static event Action<int, Recording> CommittedRecordingRemoving;

        /// <summary>
        /// Raised immediately AFTER a committed recording left the list: every index above
        /// <c>index</c> has shifted down by one. <c>absorbedInto</c> is the merge target that
        /// now carries the removed recording's trajectory (optimizer merge), or null for a
        /// plain delete.
        /// </summary>
        internal static event Action<int, Recording, Recording> CommittedRecordingRemoved;

        /// <summary>
        /// Raised immediately AFTER a recording was inserted at <c>index</c>: every index at
        /// or above it has shifted up by one.
        /// </summary>
        internal static event Action<int> CommittedRecordingInserted;

        internal static void ResetCommittedListNotificationsForTesting()
        {
            CommittedRecordingRemoving = null;
            CommittedRecordingRemoved = null;
            CommittedRecordingInserted = null;
        }

        // A throwing subscriber must not abort the mutation it is reacting to: the list is
        // already (or about to be) consistent, and the caller's own follow-up (file flush,
        // tree bookkeeping) still has to run. Contain, log loud, continue.
        internal static void NotifyCommittedRecordingRemoving(int index, Recording removed)
        {
            var handler = CommittedRecordingRemoving;
            if (handler == null) return;
            try { handler(index, removed); }
            catch (Exception ex)
            {
                ParsekLog.Error("RecordingStore",
                    $"CommittedRecordingRemoving subscriber threw for index={index} " +
                    $"id={removed?.RecordingId}: {ex}");
            }
        }

        internal static void NotifyCommittedRecordingRemoved(int index, Recording removed, Recording absorbedInto)
        {
            var handler = CommittedRecordingRemoved;
            if (handler == null) return;
            try { handler(index, removed, absorbedInto); }
            catch (Exception ex)
            {
                ParsekLog.Error("RecordingStore",
                    $"CommittedRecordingRemoved subscriber threw for index={index} " +
                    $"id={removed?.RecordingId} absorbedInto={absorbedInto?.RecordingId ?? "<none>"}: {ex}");
            }
        }

        internal static void NotifyCommittedRecordingInserted(int index)
        {
            var handler = CommittedRecordingInserted;
            if (handler == null) return;
            try { handler(index); }
            catch (Exception ex)
            {
                ParsekLog.Error("RecordingStore",
                    $"CommittedRecordingInserted subscriber threw for index={index}: {ex}");
            }
        }
    }
}
