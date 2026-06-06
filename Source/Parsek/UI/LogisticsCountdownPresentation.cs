using System.Globalization;
using Parsek.Logistics;

namespace Parsek
{
    /// <summary>
    /// Pure presentation helper for the Logistics window "Next delivery" /
    /// "Rechecks in" countdown (H1). Decides WHICH countdown branch to show for a
    /// route given its status, the throttled next-dock-crossing seconds (from
    /// <see cref="RouteOrchestrator.TryComputeSecondsToNextDockCrossing"/>), and the
    /// wait-state retry UT (<see cref="Route.NextEligibilityCheckUT"/>). Unity-free
    /// and side-effect-free, so it is unit tested directly off the IMGUI path
    /// (mirrors <see cref="LogisticsButtonState"/>). The window owns the actual
    /// <c>FormatCountdown</c> formatting; this helper only picks the branch and the
    /// seconds to feed it.
    /// </summary>
    internal static class LogisticsCountdownPresentation
    {
        /// <summary>Which countdown line the detail panel should render.</summary>
        internal enum CountdownBranch
        {
            /// <summary>No countdown (not ghost-driving, out of span, or no data).</summary>
            None = 0,

            /// <summary>"Rechecks in {countdown}": a blocked wait-state route with a
            /// pending eligibility retry UT.</summary>
            RechecksIn = 1,

            /// <summary>"Next delivery {countdown}": a live next-dock-crossing
            /// countdown.</summary>
            NextDelivery = 2,
        }

        /// <summary>
        /// Outcome of <see cref="ResolveDetailCountdown"/>: the branch to render plus
        /// the seconds-from-now to feed the window's <c>FormatCountdown</c> call.
        /// </summary>
        internal readonly struct CountdownDecision
        {
            internal CountdownDecision(CountdownBranch branch, double seconds)
            {
                Branch = branch;
                Seconds = seconds;
            }

            /// <summary>The branch to render.</summary>
            internal CountdownBranch Branch { get; }

            /// <summary>Seconds from now to the displayed instant (next dock crossing
            /// for <see cref="CountdownBranch.NextDelivery"/>, next eligibility
            /// recheck for <see cref="CountdownBranch.RechecksIn"/>); 0 for
            /// <see cref="CountdownBranch.None"/>.</summary>
            internal double Seconds { get; }
        }

        /// <summary>
        /// True for the three blocked-but-flying wait states whose detail line shows
        /// the retry-recheck countdown instead of the next-delivery countdown. These
        /// are ghost-driving (the ghost still flies) but transfer nothing until the
        /// wait clears, and they carry a <see cref="Route.NextEligibilityCheckUT"/>
        /// set by the wait-state applier
        /// (<c>RouteStatus.WaitingForResources</c> / <c>WaitingForFunds</c> /
        /// <c>DestinationFull</c>).
        /// </summary>
        internal static bool IsWaitState(RouteStatus status)
        {
            return status == RouteStatus.WaitingForResources
                || status == RouteStatus.WaitingForFunds
                || status == RouteStatus.DestinationFull;
        }

        /// <summary>
        /// Picks the detail-panel countdown branch for a route. The wait-state
        /// retry countdown takes precedence: a blocked route
        /// (<see cref="IsWaitState"/> with a pending
        /// <paramref name="nextEligibilityCheckUT"/> still in the future) shows
        /// "Rechecks in {NextEligibilityCheckUT - now}". Otherwise, when the throttled
        /// next-dock-crossing accessor yielded a finite, positive
        /// <paramref name="secondsToNextDockCrossing"/>
        /// (<paramref name="hasNextCrossing"/> true), it shows
        /// "Next delivery {seconds}". When neither applies it returns
        /// <see cref="CountdownBranch.None"/> (the window draws nothing / a dash).
        /// Pure: no logging (the orchestrator accessor owns the rate-limited log).
        /// </summary>
        /// <param name="status">The route's current status.</param>
        /// <param name="nextEligibilityCheckUT">The route's pending wait-state retry
        /// UT, or null when not waiting.</param>
        /// <param name="hasNextCrossing">Whether the throttled accessor produced a
        /// finite next-dock-crossing.</param>
        /// <param name="secondsToNextDockCrossing">Seconds to the next dock crossing
        /// when <paramref name="hasNextCrossing"/> is true.</param>
        /// <param name="nowUT">Current game UT.</param>
        internal static CountdownDecision ResolveDetailCountdown(
            RouteStatus status,
            double? nextEligibilityCheckUT,
            bool hasNextCrossing,
            double secondsToNextDockCrossing,
            double nowUT)
        {
            // Wait-state retry countdown takes precedence: a blocked route shows when
            // it will next recheck eligibility, not a next-delivery time it cannot
            // currently meet.
            if (IsWaitState(status) && nextEligibilityCheckUT.HasValue)
            {
                double until = nextEligibilityCheckUT.Value - nowUT;
                if (until > 0.0)
                    return new CountdownDecision(CountdownBranch.RechecksIn, until);
            }

            // Live next-dock-crossing countdown.
            if (hasNextCrossing && secondsToNextDockCrossing > 0.0)
                return new CountdownDecision(CountdownBranch.NextDelivery, secondsToNextDockCrossing);

            return new CountdownDecision(CountdownBranch.None, 0.0);
        }

        /// <summary>
        /// The compact always-visible "Next" COLUMN cell text for a countdown branch.
        /// The window resolves <paramref name="formattedCountdown"/> with its real
        /// countdown helper (<c>SelectiveSpawnUI.FormatCountdown</c>) and passes it in,
        /// so this helper stays Unity-free and the cell wording is unit tested. Both the
        /// next-delivery and the wait-state "rechecks in" branch show the bare countdown
        /// in the narrow column (the branch wording lives in the detail line); the
        /// no-countdown branch shows a dash. Pure.
        /// </summary>
        internal static string FormatNextDeliveryCell(CountdownBranch branch, string formattedCountdown)
        {
            switch (branch)
            {
                case CountdownBranch.NextDelivery:
                case CountdownBranch.RechecksIn:
                    return string.IsNullOrEmpty(formattedCountdown) ? "-" : formattedCountdown;
                case CountdownBranch.None:
                default:
                    return "-";
            }
        }

        /// <summary>
        /// The detail-panel countdown line for a branch, e.g.
        /// "Next delivery T-12m 5s" or "Rechecks in T-0m 23s". The window passes the
        /// already-formatted countdown string (from its real countdown helper); this
        /// helper only prefixes the branch wording, so it is Unity-free and testable.
        /// Returns null for <see cref="CountdownBranch.None"/> (the window draws no
        /// countdown line). Pure.
        /// </summary>
        internal static string FormatDetailCountdownLine(CountdownBranch branch, string formattedCountdown)
        {
            switch (branch)
            {
                case CountdownBranch.NextDelivery:
                    return $"Next delivery {formattedCountdown}";
                case CountdownBranch.RechecksIn:
                    return $"Rechecks in {formattedCountdown}";
                case CountdownBranch.None:
                default:
                    return null;
            }
        }
    }
}
