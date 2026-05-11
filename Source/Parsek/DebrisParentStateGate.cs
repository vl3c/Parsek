using System;

namespace Parsek
{
    /// <summary>
    /// Pure predicate that decides whether a parent-anchored debris recording's parent
    /// recording is "closed/superseded" — i.e. no longer accepting new
    /// trajectory samples — so the debris recording should be ended by the
    /// TTL hook (`BackgroundRecorder.CheckDebrisTTL`).
    ///
    /// Rationale (PR 3b review follow-up — fourth iteration): the original
    /// v8-of-the-plan pseudocode used <c>parentRec.ExplicitEndUT &lt; currentUT</c>
    /// as the "finalized" signal. That's a real bug — active background
    /// recordings update <c>ExplicitEndUT</c> only at sample boundaries, so
    /// it lags the current frame by the sample interval. Treating that lag
    /// as "finalized" would end every debris child on the next TTL tick.
    ///
    /// The natural-looking replacement was "<c>ChildBranchPointId</c> set
    /// means closed." That's wrong too — focused-vessel breakups
    /// (<c>ParsekFlight.ProcessBreakupEvent</c>) set
    /// <c>activeRec.ChildBranchPointId = breakupBp.Id</c> on the active
    /// focused recording, but the recording <i>keeps growing past the
    /// breakup</i> (breakup-continuous design — see
    /// <c>ParsekFlight.cs:5427-5432</c>). So <c>ChildBranchPointId</c>-set
    /// is NOT sufficient to conclude "closed." It's a chain-topology
    /// marker, not a recording-state marker.
    ///
    /// The authoritative signal for "still accepting samples" is membership
    /// in one of the recorder's two active-recording pools:
    /// <list type="number">
    /// <item><description><b>Active focused recording</b> —
    /// <c>tree.ActiveRecordingId == parent.RecordingId</c>. The active /
    /// focused-vessel recording is intentionally absent from
    /// <c>BackgroundMap</c> (which tracks only background-recorded
    /// vessels), so checking <c>BackgroundMap</c> alone would
    /// incorrectly expire focused-vessel debris on the first TTL tick.
    /// This is the case for debris produced by
    /// <c>ParsekFlight.CreateBreakupChildRecording</c> — its parent is
    /// the focused vessel, and that vessel may have <c>ChildBranchPointId</c>
    /// set from the very breakup that spawned the debris.</description></item>
    /// <item><description><b>Active background recording</b> —
    /// <c>BackgroundMap[parent.VesselPersistentId] == parent.RecordingId</c>.
    /// The recorder is currently writing samples to this recording for
    /// its vessel. After a background split,
    /// <c>CloseParentRecording</c> sets <c>ChildBranchPointId</c> on the
    /// closed parent AND swaps <c>BackgroundMap[pid]</c> to the
    /// continuation's id — so the closed parent's <c>RecordingId</c> no
    /// longer matches <c>BackgroundMap[pid]</c>, and this branch
    /// correctly fails for it.</description></item>
    /// </list>
    ///
    /// Anything that fails BOTH active-pool checks is closed/superseded —
    /// either the parent vessel was destroyed (BG entry removed by
    /// <c>OnVesselRemovedFromBackground</c>), or the parent was superseded
    /// by a Re-Fly (the active recording swapped to the provisional fork),
    /// or the parent was a closed background recording whose continuation
    /// now owns the BackgroundMap entry. ChildBranchPointId is no longer a
    /// gate input — it is logged as a diagnostic hint at the call site so
    /// the operator can distinguish "closed by split" from "vessel
    /// destroyed" without changing predicate behavior.
    ///
    /// Defensive cases (return <c>true</c>): null inputs. Parent with
    /// <c>VesselPersistentId == 0</c> is not treated as automatically-closed
    /// because the focused-vessel case may legitimately have a zero pid
    /// before the recorder assigns one (the active-recording match handles
    /// that case correctly).
    ///
    /// Pure function of two <see cref="Recording"/> fields and two
    /// <see cref="RecordingTree"/> fields; testable from xUnit without a
    /// Unity runtime.
    /// </summary>
    internal static class DebrisParentStateGate
    {
        /// <summary>
        /// Returns <c>true</c> when <paramref name="parentRec"/> is no
        /// longer the active recording for its vessel pid in
        /// <paramref name="tree"/>. Callers that get <c>true</c> should
        /// end the debris recording anchored to <paramref name="parentRec"/>.
        /// </summary>
        internal static bool IsParentRecordingClosedOrSuperseded(
            Recording parentRec,
            RecordingTree tree)
        {
            if (parentRec == null) return true;
            if (tree == null) return true;

            // Active-focused recording: the parent IS the tree's active
            // recording. ChildBranchPointId may also be set (focused
            // breakup-continuous design — ProcessBreakupEvent links the
            // recording into the breakup BP topologically while the
            // recording keeps growing past the breakup), so the active
            // match must win over any ChildBranchPointId signal.
            if (!string.IsNullOrEmpty(tree.ActiveRecordingId)
                && string.Equals(
                        tree.ActiveRecordingId,
                        parentRec.RecordingId,
                        StringComparison.Ordinal))
            {
                return false;
            }

            // Active-background recording: BackgroundMap entry for the
            // parent's vessel pid points at the parent's recording id.
            // After a background split, CloseParentRecording sets
            // ChildBranchPointId AND swaps BackgroundMap[pid] to the
            // continuation's id — so this branch correctly fails for the
            // closed parent (its RecordingId no longer matches the
            // BackgroundMap entry).
            if (parentRec.VesselPersistentId != 0u
                && tree.BackgroundMap != null
                && tree.BackgroundMap.TryGetValue(
                        parentRec.VesselPersistentId,
                        out string activeBackgroundRecordingId)
                && string.Equals(
                        activeBackgroundRecordingId,
                        parentRec.RecordingId,
                        StringComparison.Ordinal))
            {
                return false;
            }

            // Neither active-focused nor active-background → closed/superseded.
            return true;
        }
    }
}
