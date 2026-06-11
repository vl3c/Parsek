using System.Globalization;
using Parsek.Logistics;

namespace Parsek
{
    /// <summary>
    /// Pure presentation helper for live route hold reasons (M6 hold reasons):
    /// maps the persisted <c>Route.LastHold*</c> fields (the
    /// <see cref="RouteDispatchEvaluator.EligibilityFailureKind"/> plus the raw
    /// evaluator reason token) to plain-ASCII player language for the Logistics
    /// window's detail panel and status-cell tooltip. TOTAL over both token
    /// shapes - the loop path stores bare tokens ("LiquidFuel", "funds-short",
    /// "stop-0-no-live-vessels") while the legacy self-timer path stores
    /// prefixed decision tokens ("origin-lacks-X", "funds-shortfall-N",
    /// "destination-full-X") - and over unknown future kinds/tokens via the
    /// "route is blocked (kind: token)" fallback: never throws, never returns
    /// blank for a real hold. Unity-free and side-effect-free so it is unit
    /// tested directly off the IMGUI path (mirrors
    /// <see cref="LogisticsRejectPresentation"/> and the other
    /// Logistics*Presentation siblings). InvariantCulture for the shortfall.
    /// </summary>
    internal static class LogisticsHoldPresentation
    {
        /// <summary>
        /// One-clause player-language description of a hold, used verbatim as
        /// the status-cell tooltip augmentation and as the body of the detail
        /// line (<see cref="FormatHoldDetailLine"/>). Returns null ONLY for
        /// <see cref="RouteDispatchEvaluator.EligibilityFailureKind.None"/>
        /// (no hold recorded); every real hold maps to non-empty text.
        /// </summary>
        internal static string DescribeHold(
            RouteDispatchEvaluator.EligibilityFailureKind kind,
            string detail,
            double shortfall)
        {
            switch (kind)
            {
                case RouteDispatchEvaluator.EligibilityFailureKind.None:
                    return null;

                case RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo:
                    return DescribeOriginLacksCargo(detail);

                case RouteDispatchEvaluator.EligibilityFailureKind.FundsShort:
                    // Both token shapes ("funds-short" / "funds-shortfall-N") land
                    // here; the number comes ONLY from the shortfall argument.
                    // The legacy capture stores shortfall 0 (the value lives only
                    // inside its token), so legacy holds render the generic text -
                    // accepted degradation, do NOT parse the token suffix.
                    return shortfall > 0.0
                        ? string.Format(CultureInfo.InvariantCulture,
                            "not enough funds at KSC - short {0:F0} funds for this dispatch",
                            shortfall)
                        : "not enough funds at KSC for this dispatch";

                case RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull:
                {
                    string resource = StripPrefix(detail, "destination-full-");
                    return string.IsNullOrEmpty(resource)
                        ? "destination has no room for the delivery"
                        : "destination has no room for " + resource;
                }

                case RouteDispatchEvaluator.EligibilityFailureKind.EndpointLost:
                    // "origin-*" names the origin resolver; everything else
                    // ("stop-N-*", "endpoint-destroyed-at-delivery:*", unknown)
                    // is a destination loss.
                    return detail != null
                        && detail.StartsWith("origin-", System.StringComparison.Ordinal)
                        ? "origin vessel could not be found"
                        : "destination vessel could not be found - re-target or recreate the route";

                case RouteDispatchEvaluator.EligibilityFailureKind.SourcesStale:
                    return "route source recordings are unavailable right now";

                default:
                    return Fallback(kind, detail);
            }
        }

        /// <summary>
        /// The detail-panel line: "Last cycle blocked: {describe} (checked
        /// {age} ago)". The age suffix is mandatory display context (a reason
        /// held across a long warp reads as historical fact, not a live claim)
        /// and is omitted only when the age is unknown/invalid (negative, or a
        /// degenerate duration the formatter renders as "-"). Returns null when
        /// <paramref name="describe"/> is null/empty (no hold to render).
        /// </summary>
        internal static string FormatHoldDetailLine(string describe, double ageSeconds)
        {
            if (string.IsNullOrEmpty(describe))
                return null;
            if (ageSeconds < 0.0)
                return "Last cycle blocked: " + describe;
            string age = LogisticsWindowUI.FormatDuration(ageSeconds);
            if (age == "-")
                return "Last cycle blocked: " + describe;
            return "Last cycle blocked: " + describe + " (checked " + age + " ago)";
        }

        /// <summary>
        /// Display gate for the hold text (plan-review MAJOR 2): no hold
        /// renders when none is recorded, and
        /// <see cref="RouteStatus.MissingSourceRecording"/> /
        /// <see cref="RouteStatus.SourceChanged"/> rows suppress the hold
        /// because those statuses already explain themselves and a persisted
        /// OLDER hold (e.g. an OriginLacksCargo from before the source changed)
        /// would actively mislead. Persistence stays unconditional - this gates
        /// DISPLAY only (mirrors the M4 CapacityContext status gate). Holds DO
        /// display for Active, the three wait states, EndpointLost, InTransit,
        /// and Paused (keep-on-Pause answers "why wasn't this delivering").
        /// </summary>
        internal static bool ShouldDisplayHold(
            RouteStatus status,
            RouteDispatchEvaluator.EligibilityFailureKind kind)
        {
            if (kind == RouteDispatchEvaluator.EligibilityFailureKind.None)
                return false;
            if (status == RouteStatus.MissingSourceRecording
                || status == RouteStatus.SourceChanged)
                return false;
            return true;
        }

        /// <summary>
        /// Status-cell tooltip: the raw enum name alone (the pre-M6 contract),
        /// or the enum name plus the one-clause hold description on a second
        /// line. Visible cell text and styles are unchanged by M6 - only the
        /// tooltip is augmented.
        /// </summary>
        internal static string StatusCellTooltip(RouteStatus status, string holdShort)
        {
            if (string.IsNullOrEmpty(holdShort))
                return status.ToString();
            return status + "\n" + holdShort;
        }

        // The OriginLacksCargo token family: the special markers first
        // (inventory-unsupported, origin-unresolved), then the resource name -
        // bare on the loop path, "origin-lacks-" prefixed on the legacy path.
        private static string DescribeOriginLacksCargo(string detail)
        {
            if (string.Equals(detail, "inventory-origin-debit-unsupported",
                    System.StringComparison.Ordinal))
            {
                return "this route carries stored inventory parts, which docked-origin routes cannot debit yet";
            }
            if (detail != null
                && detail.StartsWith("origin-unresolved:", System.StringComparison.Ordinal))
            {
                // Keep the raw token in the tail so the log-grep handle survives
                // into the UI text.
                return "origin vessel could not be found - it may have moved, been recovered, or been destroyed ("
                    + detail + ")";
            }
            string resource = StripPrefix(detail, "origin-lacks-");
            if (string.IsNullOrEmpty(resource))
                return Fallback(RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo, detail);
            return "origin is out of " + resource + " - delivers when the origin has the full amount";
        }

        // Total fallback row: readable, never blank, never throws - new tokens
        // or future kinds degrade here instead of rendering nothing.
        private static string Fallback(
            RouteDispatchEvaluator.EligibilityFailureKind kind, string detail)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "route is blocked ({0}: {1})",
                kind, string.IsNullOrEmpty(detail) ? "<none>" : detail);
        }

        private static string StripPrefix(string token, string prefix)
        {
            if (string.IsNullOrEmpty(token))
                return token;
            return token.StartsWith(prefix, System.StringComparison.Ordinal)
                ? token.Substring(prefix.Length)
                : token;
        }
    }
}
