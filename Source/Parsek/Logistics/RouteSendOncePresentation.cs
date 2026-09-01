using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Pure player-facing text for the "Send Once" one-shot outcome (the
    /// on-screen toast the orchestrator posts through
    /// <see cref="ParsekLog.ScreenMessage(string, float)"/> when an ARMED
    /// send-once cycle resolves).
    ///
    /// <para><b>Why this exists.</b> A send-once cycle can resolve instantly -
    /// the loop clock catches up in the same frame the click is consumed (the
    /// warp catch-up path) - so the route silently goes Active -> Paused with
    /// nothing on screen and the player reads the click as "did nothing". EVERY
    /// resolution shape gets a toast: DELIVERED (the single-stop cycle fired -
    /// <see cref="BuildDeliveredMessage"/>), CYCLE COMPLETE (a multi-stop cycle
    /// finished across its N windows - <see cref="BuildCycleDeliveredMessage"/>,
    /// posted by <c>RouteOrchestrator.TryHonorArmedPauseOnCompletedCycle</c>),
    /// ALREADY DELIVERED (the ELS replay backstop -
    /// <see cref="BuildAlreadyDeliveredMessage"/>), and BLOCKED (the cycle was
    /// consumed by an eligibility hold and the armed pause was honored - see
    /// <c>RouteOrchestrator.TryHonorArmedPauseOnBlockedCycle</c>).
    /// Postponement holds (see <c>RouteOrchestrator.IsPostponementHold</c>) do
    /// not resolve the one-shot, so they never toast. An ENDPOINT LOST at
    /// delivery is not a resolution either - the arm deliberately survives it
    /// (see SENDONCE-RESIDUAL-PATHS item 3), so it never toasts.</para>
    ///
    /// <para>Unity-free and side-effect-free so both strings are unit tested
    /// directly off the IMGUI / ScreenMessages path, mirroring the
    /// <c>Logistics*Presentation</c> siblings. InvariantCulture for every count
    /// (a comma-locale system must not render "1 234 resources").</para>
    /// </summary>
    internal static class RouteSendOncePresentation
    {
        /// <summary>
        /// On-screen duration for both send-once toasts. 5s is the house range
        /// (every other Parsek ScreenMessage uses 5f or less); one constant so
        /// retuning does not mean hunting orchestrator call sites.
        /// </summary>
        internal const float ToastSeconds = 5f;

        /// <summary>Shown when a send-once cycle DELIVERED and the route auto-paused.</summary>
        /// <param name="routeName">The route's display name (blank falls back to the short id).</param>
        /// <param name="routeId">The route id, for the unnamed-route fallback label.</param>
        /// <param name="resourceLines">Count of resource lines actually written at the destination.</param>
        /// <param name="inventoryUnits">Count of inventory parts actually placed at the destination.</param>
        /// <param name="isPartial">True when the delivery plan was short (remainder lost).</param>
        internal static string BuildDeliveredMessage(
            string routeName, string routeId, int resourceLines, int inventoryUnits, bool isPartial)
        {
            return "Send Once: route '" + DisplayName(routeName, routeId) + "' delivered "
                + Count(resourceLines, "resource line", "resource lines") + " and "
                + Count(inventoryUnits, "item", "items")
                + (isPartial ? " (PARTIAL - see the route's detail panel)" : string.Empty)
                + " - route is now Paused";
        }

        /// <summary>
        /// Shown when a send-once MULTI-STOP cycle completed and the route
        /// auto-paused (SENDONCE-RESIDUAL-PATHS item 1). Counts-free on purpose:
        /// the windows of one cycle can straddle several ticks, so no site can
        /// quote the whole cycle's per-resource actuals - quoting only the last
        /// window's would under-report the run. The partial/full discriminator IS
        /// cycle-scoped (<c>Route.LastPartialDeliveryCycleId</c>), so it survives.
        /// </summary>
        /// <param name="routeName">The route's display name (blank falls back to the short id).</param>
        /// <param name="routeId">The route id, for the unnamed-route fallback label.</param>
        /// <param name="isPartial">True when any window of the cycle fell short (remainder lost).</param>
        internal static string BuildCycleDeliveredMessage(
            string routeName, string routeId, bool isPartial)
        {
            return "Send Once: route '" + DisplayName(routeName, routeId)
                + "' completed its delivery run"
                + (isPartial ? " (PARTIAL - see the route's detail panel)" : string.Empty)
                + " - route is now Paused";
        }

        /// <summary>
        /// Shown when a send-once cycle resolved on the ELS REPLAY backstop
        /// (SENDONCE-RESIDUAL-PATHS item 2): the delivered row was already in the
        /// ledger - a save/reload or crash landed it without the pause marker - so
        /// the run produced no NEW delivery. That is precisely the shape where a
        /// silent resolution makes the player click Send Once again, so it toasts
        /// like the other two. Counts-free by construction: this branch re-plans
        /// nothing and has no actuals to quote.
        /// </summary>
        /// <param name="routeName">The route's display name (blank falls back to the short id).</param>
        /// <param name="routeId">The route id, for the unnamed-route fallback label.</param>
        internal static string BuildAlreadyDeliveredMessage(string routeName, string routeId)
        {
            return "Send Once: route '" + DisplayName(routeName, routeId)
                + "' - this cycle had already been delivered - route is now Paused";
        }

        /// <summary>
        /// Shown when a send-once cycle was CONSUMED BY A BLOCK (nothing dispatched,
        /// nothing delivered) and the armed pause was honored. Names the hold in the
        /// SAME player language the Logistics window's detail panel uses - the toast
        /// and the window must never disagree about why the run did not happen - by
        /// reusing <see cref="LogisticsHoldPresentation.DescribeHold"/> verbatim.
        /// </summary>
        /// <param name="routeName">The route's display name (blank falls back to the short id).</param>
        /// <param name="routeId">The route id, for the unnamed-route fallback label.</param>
        /// <param name="kind">The evaluator's failure kind, as recorded on the hold.</param>
        /// <param name="detail">The evaluator's raw reason token, as recorded on the hold.</param>
        /// <param name="shortfall">The recorded funds shortfall (0 when not a funds hold).</param>
        internal static string BuildBlockedMessage(
            string routeName,
            string routeId,
            RouteDispatchEvaluator.EligibilityFailureKind kind,
            string detail,
            double shortfall)
        {
            // DescribeHold returns null ONLY for the None kind, which the blocked
            // branch cannot produce (a blocked cycle always carries a failure kind);
            // the null-coalesce is the belt-and-braces path so the toast never reads
            // "did not run: ." if a future kind slips through as None.
            string why = LogisticsHoldPresentation.DescribeHold(kind, detail, shortfall)
                ?? "the route was not eligible to dispatch";
            return "Send Once: route '" + DisplayName(routeName, routeId) + "' did not run - "
                + why + " - route is now Paused";
        }

        /// <summary>
        /// Same fallback chain the Logistics window's dormant rows use
        /// (name -> short route id -> "&lt;unnamed&gt;"), so the toast and the
        /// window row identify an unnamed route the same way (reuse finding,
        /// PR #1582 clean review).
        /// </summary>
        private static string DisplayName(string routeName, string routeId)
        {
            string name = string.IsNullOrWhiteSpace(routeName) ? null : routeName;
            return LogisticsDormantPresentation.DormantRouteDisplayName(name, routeId);
        }

        /// <summary>"1 item" / "0 items" / "3 items" - InvariantCulture count + singular/plural noun.</summary>
        private static string Count(int n, string singular, string plural)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? singular : plural);
        }
    }
}
