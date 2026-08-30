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
    /// nothing on screen and the player reads the click as "did nothing". Both
    /// resolution shapes get a toast: DELIVERED (the cycle fired) and BLOCKED
    /// (the cycle was consumed by an eligibility hold and the armed pause was
    /// honored - see <c>RouteOrchestrator.TryHonorArmedPauseOnBlockedCycle</c>).</para>
    ///
    /// <para>Unity-free and side-effect-free so both strings are unit tested
    /// directly off the IMGUI / ScreenMessages path, mirroring the
    /// <c>Logistics*Presentation</c> siblings. InvariantCulture for every count
    /// (a comma-locale system must not render "1 234 resources").</para>
    /// </summary>
    internal static class RouteSendOncePresentation
    {
        /// <summary>Shown when a send-once cycle DELIVERED and the route auto-paused.</summary>
        /// <param name="routeName">The route's display name (null / blank renders as "unnamed").</param>
        /// <param name="resourceLines">Count of resource lines actually written at the destination.</param>
        /// <param name="inventoryUnits">Count of inventory parts actually placed at the destination.</param>
        /// <param name="isPartial">True when the delivery plan was short (remainder lost).</param>
        internal static string BuildDeliveredMessage(
            string routeName, int resourceLines, int inventoryUnits, bool isPartial)
        {
            return "Send Once: route '" + DisplayName(routeName) + "' delivered "
                + Count(resourceLines, "resource line", "resource lines") + " and "
                + Count(inventoryUnits, "item", "items")
                + (isPartial ? " (PARTIAL - see the route's detail panel)" : string.Empty)
                + " - route is now Paused";
        }

        /// <summary>
        /// Shown when a send-once cycle was CONSUMED BY A BLOCK (nothing dispatched,
        /// nothing delivered) and the armed pause was honored. Names the hold in the
        /// SAME player language the Logistics window's detail panel uses - the toast
        /// and the window must never disagree about why the run did not happen - by
        /// reusing <see cref="LogisticsHoldPresentation.DescribeHold"/> verbatim.
        /// </summary>
        /// <param name="routeName">The route's display name (null / blank renders as "unnamed").</param>
        /// <param name="kind">The evaluator's failure kind, as recorded on the hold.</param>
        /// <param name="detail">The evaluator's raw reason token, as recorded on the hold.</param>
        /// <param name="shortfall">The recorded funds shortfall (0 when not a funds hold).</param>
        internal static string BuildBlockedMessage(
            string routeName,
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
            return "Send Once: route '" + DisplayName(routeName) + "' did not run - "
                + why + " - route is now Paused";
        }

        private static string DisplayName(string routeName)
        {
            return string.IsNullOrEmpty(routeName) || string.IsNullOrWhiteSpace(routeName)
                ? "unnamed"
                : routeName;
        }

        /// <summary>"1 item" / "0 items" / "3 items" - InvariantCulture count + singular/plural noun.</summary>
        private static string Count(int n, string singular, string plural)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? singular : plural);
        }
    }
}
