using System.Globalization;
using Parsek.Logistics;

namespace Parsek
{
    /// <summary>
    /// Pure presentation helper for the Logistics window's "recently committed
    /// trees not yet eligible" near-miss subsection (M3). Maps the reason a
    /// committed tree is NOT a Supply Run candidate to player-facing text. Two
    /// reason families exist: the five
    /// <see cref="RouteAnalysisStatus"/> reject values (delegated verbatim to
    /// <see cref="RouteCreationFormatters.FormatRejectMessage"/> so the strings are
    /// never duplicated) and the separate not-fully-sealed gate
    /// (<see cref="RouteCandidateFinder.IsTreeFullySealed"/>), which has no
    /// <see cref="RouteAnalysisStatus"/> and gets the one new hand-written string
    /// here. Unity-free and side-effect-free so it is unit tested directly off the
    /// IMGUI path (mirrors <see cref="LogisticsDeliveryPresentation"/> and the other
    /// Logistics*Presentation siblings). InvariantCulture for the re-flyable count.
    /// </summary>
    internal static class LogisticsRejectPresentation
    {
        /// <summary>
        /// Describes why a committed recording tree is not yet a Supply Run
        /// candidate. When <paramref name="notSealed"/> is true the tree has at
        /// least one recording that can still be re-flown / re-written, so the
        /// route proof cannot be trusted: returns
        /// "not fully sealed (N recording(s) still re-flyable)" with singular /
        /// plural agreement on <paramref name="reflyableCount"/> (the
        /// <paramref name="status"/> argument is ignored in this branch). Otherwise
        /// the tree is sealed but ineligible, so the reason is the analysis status
        /// reject message from
        /// <see cref="RouteCreationFormatters.FormatRejectMessage"/>.
        /// </summary>
        /// <param name="status">The analysis reject status (used only when the tree
        /// is sealed; ignored when <paramref name="notSealed"/> is true).</param>
        /// <param name="notSealed">True when the tree is not fully sealed (at least
        /// one non-Immutable recording).</param>
        /// <param name="reflyableCount">Number of still-re-flyable (non-Immutable)
        /// recordings; drives the singular / plural noun.</param>
        internal static string DescribeNearMiss(
            RouteAnalysisStatus status, bool notSealed, int reflyableCount)
        {
            if (notSealed)
            {
                string noun = reflyableCount == 1 ? "recording" : "recordings";
                return string.Format(CultureInfo.InvariantCulture,
                    "not fully sealed ({0} {1} still re-flyable)", reflyableCount, noun);
            }

            return RouteCreationFormatters.FormatRejectMessage(status);
        }
    }
}
