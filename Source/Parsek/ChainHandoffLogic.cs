using System;

namespace Parsek
{
    /// <summary>
    /// Decisions returned by <see cref="ChainHandoffLogic.DecideBridgeHold"/>.
    /// </summary>
    internal enum ChainBridgeAction
    {
        /// <summary>No bridge needed: no chain continuation, or the continuation is already rendering.</summary>
        None = 0,
        /// <summary>Hold the head ghost in place; continuation has not yet activated.</summary>
        Hold = 1,
        /// <summary>Bridge window has expired without continuation activation; destroy normally.</summary>
        Expired = 2,
    }

    /// <summary>
    /// Pure decisions for the chain-seam handoff between a chain HEAD slot
    /// and its continuation. Two call sites in
    /// <see cref="GhostPlaybackEngine"/>: the in-range render branch
    /// consults <see cref="DecideShadow"/> to suppress double-rendering when
    /// both segments are live at the same UT (the section-overlap case);
    /// the stale-past-end cleanup consults <see cref="DecideBridgeHold"/>
    /// to keep the head ghost alive briefly when the continuation's
    /// activation lags behind the head's section end (the section-gap
    /// case). Together they collapse the visible chain seam onto a single
    /// contiguous ghost per chain.
    ///
    /// Keep these decisions pure: every input is a primitive, the output
    /// is a primitive enum. Bridge-window state is tracked by the caller
    /// (per slot, in the engine) so callers can write log lines and reset
    /// state when the slot churns. Unit tests live in
    /// <c>ChainHandoffLogicTests</c>.
    /// </summary>
    internal static class ChainHandoffLogic
    {
        /// <summary>
        /// Default bridge window in playback UT-seconds (the same clock the
        /// engine's <c>ctx.currentUT</c> advances on). Under time warp the
        /// budget elapses faster than wall-clock; under physics pause it
        /// does not elapse. This is intentional: the continuation's
        /// activation is keyed to UT, not real time, so the bridge needs to
        /// hold for "long enough in UT for the continuation to spawn" —
        /// roughly a few engine ticks at 1x warp. Bounded conservatively
        /// (1.0 s) so a continuation that genuinely never spawns (recorder
        /// bug, spawn throttle stall, etc.) still tears down the head
        /// within a single second of UT progression. Tunable for tests via
        /// the explicit <see cref="DecideBridgeHold"/> overload parameter.
        /// </summary>
        internal const double DefaultBridgeMaxSeconds = 1.0;

        /// <summary>
        /// In-range render decision: should the head's render be shadowed
        /// by an actively-rendering chain continuation?
        /// </summary>
        /// <param name="chainNextIndex">Resolved chain-next slot index, or -1 if none.</param>
        /// <param name="continuationHasActiveGhost">True if the continuation slot has loaded visuals.</param>
        /// <returns>True iff the head's render should be suppressed this frame.</returns>
        internal static bool DecideShadow(int chainNextIndex, bool continuationHasActiveGhost)
        {
            if (chainNextIndex < 0) return false;
            return continuationHasActiveGhost;
        }

        /// <summary>
        /// Stale-past-end cleanup decision: should the head ghost be held
        /// (instead of destroyed) while waiting for the continuation to
        /// spawn?
        /// </summary>
        /// <param name="chainNextIndex">Resolved chain-next slot index, or -1 if none.</param>
        /// <param name="continuationHasActiveGhost">
        /// True if the continuation slot has loaded visuals. When true the
        /// shadow path already hides the head's render, so the bridge is
        /// unnecessary and the head should destroy normally.
        /// </param>
        /// <param name="currentUT">Current playback UT.</param>
        /// <param name="bridgeOpenedUT">
        /// UT at which the bridge first opened for this slot, or
        /// <see cref="double.NaN"/> if the bridge has not opened yet (i.e.
        /// this is the first frame past-end with no continuation active).
        /// </param>
        /// <param name="bridgeMaxSeconds">
        /// Bridge window in seconds. Use <see cref="DefaultBridgeMaxSeconds"/>
        /// for production. Zero or negative collapses immediately to
        /// <see cref="ChainBridgeAction.Expired"/> after the first open frame.
        /// </param>
        /// <returns>
        /// <see cref="ChainBridgeAction.None"/> when no continuation exists
        /// or the continuation is already rendering;
        /// <see cref="ChainBridgeAction.Hold"/> when the bridge should
        /// open or stay open; <see cref="ChainBridgeAction.Expired"/>
        /// when the bridge has run past its window.
        /// </returns>
        internal static ChainBridgeAction DecideBridgeHold(
            int chainNextIndex,
            bool continuationHasActiveGhost,
            double currentUT,
            double bridgeOpenedUT,
            double bridgeMaxSeconds)
        {
            if (chainNextIndex < 0) return ChainBridgeAction.None;
            if (continuationHasActiveGhost) return ChainBridgeAction.None;
            if (double.IsNaN(bridgeOpenedUT)) return ChainBridgeAction.Hold;
            if (bridgeMaxSeconds <= 0.0) return ChainBridgeAction.Expired;
            return (currentUT - bridgeOpenedUT) < bridgeMaxSeconds
                ? ChainBridgeAction.Hold
                : ChainBridgeAction.Expired;
        }
    }
}
