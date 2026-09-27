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
        // RemoveCommittedById, RemoveCommittedTreeById,
        // ClearCommittedInternal - the last one removes top-down one item at a time, because
        // the failed-rewind-load bundle restore reaches it in FLIGHT with ghosts alive).

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

        /// <summary>
        /// Raised immediately AFTER a recording's per-recording playback tick box flipped
        /// (GUI-P1). Carries the recording and its new state. Subscribers that poll on a
        /// timer rather than per frame (the Tracking Station's 0.25 s lifecycle tick) use it
        /// to force their next tick NOW, so re-ticking the box brings the map icon, the orbit
        /// line and the Tracking Station row back on the same frame instead of up to a
        /// quarter second later. Every per-frame producer re-derives from
        /// <c>Recording.PlaybackEnabled</c> unconditionally and needs no subscription.
        /// </summary>
        internal static event Action<Recording, bool> RecordingPlaybackEnabledChanged;

        internal static void ResetCommittedListNotificationsForTesting()
        {
            CommittedRecordingRemoving = null;
            CommittedRecordingRemoved = null;
            CommittedRecordingInserted = null;
            RecordingPlaybackEnabledChanged = null;
        }

        /// <summary>
        /// THE single writer for <see cref="Recording.PlaybackEnabled"/> from the UI. Every
        /// affordance that flips the box - the per-row toggle, the select-all header toggle,
        /// the group-header toggles and the chain-block toggle - routes here, so all four
        /// behave identically by construction rather than by four copies of the same code
        /// (the mirror-direction requirement of the GUI-P1 ruling). Returns true when the
        /// value actually changed.
        ///
        /// <para>No-ops on an unchanged value so a per-frame IMGUI redraw cannot log or
        /// notify. Deliberately does NOT bump <c>StateVersion</c>: that version keys the
        /// committed-LIST index caches, and nothing about a visibility flip invalidates
        /// them - the render surfaces read the flag itself every pass.</para>
        /// </summary>
        internal static bool SetRecordingPlaybackEnabled(Recording rec, bool enabled)
        {
            if (rec == null || rec.PlaybackEnabled == enabled) return false;

            rec.PlaybackEnabled = enabled;
            ParsekLog.Info("RecordingStore",
                $"Recording playback {(enabled ? "enabled" : "disabled")}: " +
                $"rec={rec.RecordingId} vessel=\"{rec.VesselName}\"");
            NotifyRecordingPlaybackEnabledChanged(rec, enabled);
            return true;
        }

        internal static void NotifyRecordingPlaybackEnabledChanged(Recording rec, bool enabled)
        {
            var handler = RecordingPlaybackEnabledChanged;
            if (handler == null) return;
            try { handler(rec, enabled); }
            catch (Exception ex)
            {
                ParsekLog.Error("RecordingStore",
                    $"RecordingPlaybackEnabledChanged subscriber threw for " +
                    $"id={rec?.RecordingId} enabled={enabled}: {ex}");
            }
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
