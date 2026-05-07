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
    /// Robust signals:
    /// <list type="bullet">
    /// <item><description><c>ChildBranchPointId</c> is set — the parent
    /// recording was closed by a later split, even if a continuation took
    /// over the live vessel. <c>CloseParentRecording</c> sets this when a
    /// background split closes a parent.</description></item>
    /// <item><description><c>BackgroundMap[parent.VesselPersistentId]</c>
    /// no longer points at <c>parent.RecordingId</c> — the recorder has
    /// moved on (parent vessel destroyed →
    /// <c>OnVesselRemovedFromBackground</c> removed the entry, or parent
    /// was superseded by a Re-Fly).</description></item>
    /// </list>
    ///
    /// Defensive cases (return <c>true</c>): null inputs, parent with
    /// <c>VesselPersistentId == 0</c>, missing <c>BackgroundMap</c>. In all
    /// of these, the debris is unrenderable and should be ended.
    ///
    /// Pure function of two <see cref="Recording"/> fields and one
    /// <see cref="RecordingTree"/> field; testable from xUnit without a
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
            if (!string.IsNullOrEmpty(parentRec.ChildBranchPointId)) return true;
            if (parentRec.VesselPersistentId == 0u) return true;
            if (tree == null || tree.BackgroundMap == null) return true;
            if (!tree.BackgroundMap.TryGetValue(
                    parentRec.VesselPersistentId,
                    out string activeRecordingId))
            {
                return true;
            }
            return !string.Equals(
                activeRecordingId,
                parentRec.RecordingId,
                StringComparison.Ordinal);
        }
    }
}
