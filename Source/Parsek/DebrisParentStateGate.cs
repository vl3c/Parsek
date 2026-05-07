using System;

namespace Parsek
{
    /// <summary>
    /// Pure predicate that decides whether a v12+ debris recording's parent
    /// recording is "closed/superseded" — i.e. no longer accepting new
    /// trajectory samples — so the debris recording should be ended by the
    /// TTL hook (`BackgroundRecorder.CheckDebrisTTL`).
    ///
    /// Rationale (PR 3b review follow-up): the original v8-of-the-plan
    /// pseudocode used <c>parentRec.ExplicitEndUT &lt; currentUT</c> as the
    /// "finalized" signal. That's a real bug — active background recordings
    /// update <c>ExplicitEndUT</c> only at sample boundaries, so it lags the
    /// current frame by the sample interval. Treating that lag as "finalized"
    /// would end every v12 debris on the next TTL tick (a regression worse
    /// than the original bug).
    ///
    /// Three categories of "still accepting samples" the predicate must
    /// recognize as NOT-closed:
    /// <list type="number">
    /// <item><description><b>Active focused recording</b> —
    /// <c>tree.ActiveRecordingId == parent.RecordingId</c>. The active /
    /// focused-vessel recording is intentionally absent from
    /// <c>BackgroundMap</c> (which tracks only background-recorded
    /// vessels), so checking <c>BackgroundMap</c> alone would
    /// incorrectly expire focused-vessel debris on the first TTL tick.
    /// This is the case for debris produced by
    /// <c>ParsekFlight.CreateBreakupChildRecording</c> — its parent is
    /// the focused vessel.</description></item>
    /// <item><description><b>Active background recording</b> —
    /// <c>BackgroundMap[parent.VesselPersistentId] == parent.RecordingId</c>.
    /// The recorder is currently writing samples to this recording for
    /// its vessel.</description></item>
    /// </list>
    ///
    /// Robust "closed/superseded" signals (return <c>true</c>):
    /// <list type="bullet">
    /// <item><description><c>ChildBranchPointId</c> is set — the parent
    /// recording was closed by a later split, even if a continuation took
    /// over the live vessel. <c>CloseParentRecording</c> sets this when a
    /// background split closes a parent.</description></item>
    /// <item><description>Neither active-focused nor active-background:
    /// the recorder has moved on (parent vessel destroyed →
    /// <c>OnVesselRemovedFromBackground</c> removed the BG entry; or
    /// parent was superseded by a Re-Fly that swapped the active
    /// recording).</description></item>
    /// </list>
    ///
    /// Defensive cases (return <c>true</c>): null inputs. Parent with
    /// <c>VesselPersistentId == 0</c> is no longer treated as
    /// automatically-closed because the focused-vessel case may legitimately
    /// have a zero pid before the recorder assigns one (the active-recording
    /// match handles that case correctly).
    ///
    /// Pure function of three <see cref="Recording"/> fields and two
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
            // Closed at a split — even if a continuation took over the live
            // vessel, the closed recording itself receives no more samples.
            if (!string.IsNullOrEmpty(parentRec.ChildBranchPointId)) return true;
            if (tree == null) return true;

            // Active-focused recording: the parent IS the tree's active
            // recording. PR 3b review follow-up #2 — focused-vessel debris
            // (ParsekFlight.CreateBreakupChildRecording) anchors here, and
            // the active recording is intentionally absent from BackgroundMap.
            // Without this branch, every focused-vessel debris would be
            // expired on the first TTL tick.
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
