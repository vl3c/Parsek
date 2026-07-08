using System.Globalization;
using Parsek.Logistics;

namespace Parsek
{
    /// <summary>
    /// (M5 D8 / OQ2) Pure presentation helpers for the Logistics window's
    /// windowed-basis surfaces: the basis label shown after the cadence
    /// ("(Duna transfer)" / "(launch window schedule)") and the windowed
    /// cadence-stepper wording ("2x (every 2nd window)") that replaces the
    /// interval arithmetic - actively misleading on synodic spacing. Unity-free
    /// and side-effect-free (mirrors <see cref="LogisticsCountdownPresentation"/>),
    /// so it is unit tested directly off the IMGUI path. A
    /// <see cref="RouteWindowBasis.FlatInterval"/> route gets null / the
    /// existing text everywhere - flat rows render byte-identically.
    /// </summary>
    internal static class RouteWindowBasisPresentation
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>True for the two windowed bases (the surfaces that swap to
        /// windowed wording); false for flat.</summary>
        internal static bool IsWindowedBasis(RouteWindowBasis basis)
        {
            return basis != RouteWindowBasis.FlatInterval;
        }

        /// <summary>
        /// The basis label appended after the cadence on the route row / detail:
        /// "(launch window schedule)" for a zero-drift scheduled route,
        /// "({TargetBody} transfer)" for a re-aim route (generic
        /// "(transfer windows)" when the plan carries no body name), and null
        /// for a flat route (draw nothing - the unchanged pre-M5 text).
        /// </summary>
        internal static string BasisLabel(RouteWindowBasis basis, string targetBody)
        {
            switch (basis)
            {
                case RouteWindowBasis.ZeroDriftSchedule:
                    return "(launch window schedule)";
                case RouteWindowBasis.ReaimWindows:
                    return string.IsNullOrEmpty(targetBody)
                        ? "(transfer windows)"
                        : string.Format(IC, "({0} transfer)", targetBody);
                default:
                    return null;
            }
        }

        /// <summary>
        /// The windowed cadence-stepper wording (OQ2): "1x (every window)" /
        /// "2x (every 2nd window)" / "3x (every 3rd window)" ... instead of the
        /// interval arithmetic. <paramref name="n"/> is clamped to the
        /// <c>&gt;= 1</c> floor first.
        /// </summary>
        internal static string FormatWindowedCadence(int n)
        {
            n = Route.ClampCadenceMultiplier(n);
            return n == 1
                ? "1x (every window)"
                : string.Format(IC, "{0}x (every {1} window)", n, Ordinal(n));
        }

        /// <summary>
        /// English ordinal for the windowed wording ("2nd", "3rd", "4th",
        /// "11th"-"13th" exceptions). Internal for direct testability.
        /// </summary>
        internal static string Ordinal(int n)
        {
            int hundredRem = n % 100;
            if (hundredRem >= 11 && hundredRem <= 13)
                return n.ToString(IC) + "th";
            switch (n % 10)
            {
                case 1: return n.ToString(IC) + "st";
                case 2: return n.ToString(IC) + "nd";
                case 3: return n.ToString(IC) + "rd";
                default: return n.ToString(IC) + "th";
            }
        }
    }
}
