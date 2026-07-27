using System;

namespace Parsek
{
    /// <summary>Pure result of the unbound-provisional raise predicates.</summary>
    internal struct UnboundProvisionalRaise
    {
        public bool ShouldRaise;

        /// <summary>Grep-stable reason token for the raise log line.</summary>
        public string Reason;
    }

    /// <summary>
    /// R1-EMPTY-PROVISIONAL, the fixture-independent half.
    ///
    /// <para>
    /// A Re-Fly session can reach the merge orchestrator with **no recorder ever
    /// bound to its provisional**, and nothing in between refuses. When that
    /// happens the re-flight is recorded somewhere else (or not at all), the
    /// provisional stays trajectory-less, and
    /// <see cref="SupersedeCommit.AppendRelations"/> correctly declines to let it
    /// replace the origin - so the re-fly silently supersedes nothing.
    /// </para>
    ///
    /// <para>
    /// The condition is cheaply observable at two points that are ignored today,
    /// both far earlier and more actionable than the merge orchestrator:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <c>ParsekFlight.RestoreActiveTreeFromPending</c> giving up ("leaving
    ///     tree in Limbo") while a session is live. This is the exact moment the
    ///     binding fails, and it fires seconds after the rewind load.
    ///   </description></item>
    ///   <item><description>
    ///     The <c>OnSave</c> sidecar rewrite reporting
    ///     <c>reason=trajectory-missing</c> for the session's own provisional.
    ///     This one also catches the shape where the restore coroutine was never
    ///     scheduled at all, so point 1 never printed.
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// Deliberately observation only. Neither raise changes control flow: the
    /// route by which a session reaches this state is NOT yet established (see the
    /// todo entry - the only demonstrated route is fixture-shaped), so acting on
    /// the signal would be building on an unproven premise. Raising loudly is what
    /// turns the next occurrence into evidence instead of silence.
    /// </para>
    ///
    /// <para>Pure so every branch is unit-testable without a live scene.</para>
    /// </summary>
    internal static class ReFlyProvisionalBinding
    {
        /// <summary>
        /// Sidecar-currency reason meaning the <c>.prec</c> file does not exist at
        /// all, i.e. the recording has never had a trajectory written for it. Other
        /// reasons (epoch / schema / payload mismatches) are ordinary sidecar churn
        /// on a recording that DOES have data, and must not raise.
        /// </summary>
        internal const string TrajectoryMissingReason = "trajectory-missing";

        /// <summary>
        /// Detection point 1: the quickload-resume restore gave up without binding
        /// a recorder, while an in-place-continuation Re-Fly session is live.
        /// </summary>
        /// <param name="marker">Live marker, or null.</param>
        /// <param name="attemptedTreeId">
        /// Id of the tree the restore was actually working on, for the log line.
        /// Distinguishing "gave up on the marker's own tree" from "gave up on some
        /// other tree" is the single most useful bit for whoever reads the next
        /// occurrence, because those have different causes.
        /// </param>
        internal static UnboundProvisionalRaise EvaluateRestoreGiveUp(
            ReFlySessionMarker marker, string attemptedTreeId)
        {
            if (!ReFlySessionMarker.IsInPlaceContinuation(marker))
                return NoRaise("no-inplace-refly-session");

            bool onMarkerTree = !string.IsNullOrEmpty(marker.TreeId)
                && !string.IsNullOrEmpty(attemptedTreeId)
                && string.Equals(marker.TreeId, attemptedTreeId, StringComparison.Ordinal);

            return new UnboundProvisionalRaise
            {
                ShouldRaise = true,
                Reason = onMarkerTree
                    ? "refly-restore-gave-up-on-marker-tree"
                    : "refly-restore-gave-up-on-other-tree",
            };
        }

        /// <summary>
        /// Detection point 2: OnSave is rewriting sidecars for a recording that has
        /// no trajectory file at all, and that recording IS the live session's
        /// provisional. Catches the shape where the restore coroutine never ran, so
        /// <see cref="EvaluateRestoreGiveUp"/> never had a chance to fire.
        /// </summary>
        internal static UnboundProvisionalRaise EvaluateSidecarRewrite(
            ReFlySessionMarker marker, string recordingId, string sidecarReason)
        {
            if (!ReFlySessionMarker.IsInPlaceContinuation(marker))
                return NoRaise("no-inplace-refly-session");
            if (string.IsNullOrEmpty(recordingId)
                || !string.Equals(
                    recordingId, marker.ActiveReFlyRecordingId, StringComparison.Ordinal))
            {
                return NoRaise("not-the-session-provisional");
            }
            if (!string.Equals(sidecarReason, TrajectoryMissingReason, StringComparison.Ordinal))
                return NoRaise("sidecar-reason-is-not-trajectory-missing");

            return new UnboundProvisionalRaise
            {
                ShouldRaise = true,
                Reason = "refly-provisional-has-no-trajectory-at-save",
            };
        }

        private static UnboundProvisionalRaise NoRaise(string reason)
        {
            return new UnboundProvisionalRaise { ShouldRaise = false, Reason = reason };
        }
    }
}
