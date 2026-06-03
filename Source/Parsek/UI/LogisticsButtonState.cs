using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;

namespace Parsek
{
    /// <summary>
    /// Pure presentation helpers for the main-window Logistics button: the
    /// live route-count label and the hard-broken-state detection that drives
    /// the red tint. Both are Unity-free and side-effect-free so they are unit
    /// tested directly (mirrors <see cref="SelectiveSpawnUI"/>, the pure static
    /// helper class backing the Real Spawn Control button). The button itself
    /// is drawn in <see cref="ParsekUI"/>.
    /// </summary>
    internal static class LogisticsButtonState
    {
        /// <summary>
        /// Formats the main-window Logistics button label with a live route
        /// count, e.g. "Logistics (3)" (mirrors the Real Spawn Control "(N)"
        /// idiom). Uses InvariantCulture so the count carries no locale-specific
        /// grouping separator. Not pluralized.
        /// </summary>
        internal static string FormatLogisticsButtonLabel(int count)
        {
            return string.Format(CultureInfo.InvariantCulture, "Logistics ({0})", count);
        }

        /// <summary>
        /// True when any status in the sequence is a hard-broken route state
        /// (<see cref="RouteStatus.EndpointLost"/>,
        /// <see cref="RouteStatus.MissingSourceRecording"/>, or
        /// <see cref="RouteStatus.SourceChanged"/>). The broken set is NOT
        /// duplicated here: it is delegated to <see cref="RouteStatusPolicy.Broken"/>,
        /// the single source of truth whose exhaustive switch fails loudly when a
        /// new RouteStatus is added without being classified, so the button tint
        /// and the policy can never diverge. Returns false on a null sequence and
        /// early-returns on the first broken status.
        /// </summary>
        internal static bool AnyRouteHardBroken(IEnumerable<RouteStatus> statuses)
        {
            if (statuses == null)
                return false;
            foreach (RouteStatus status in statuses)
            {
                if (RouteStatusPolicy.Broken(status))
                    return true;
            }
            return false;
        }
    }
}
