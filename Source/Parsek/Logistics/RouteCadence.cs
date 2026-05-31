namespace Parsek.Logistics
{
    /// <summary>
    /// Pure cadence-stepper math for the Logistics window's dispatch-cadence
    /// control (plan Phase 6 task 3). Extracted so the stepper logic
    /// (clamp + recompute <c>DispatchInterval = N * span</c>) is unit-testable
    /// without spinning up IMGUI. The window's row/detail stepper calls
    /// <see cref="StepMultiplier"/> for the new <c>N</c> and
    /// <see cref="ApplyMultiplier"/> to recompute the route's interval, then
    /// persists through the existing deferred-mutation pattern.
    /// </summary>
    internal static class RouteCadence
    {
        /// <summary>
        /// Steps a cadence multiplier by <paramref name="delta"/> (+1 / -1 from
        /// the stepper buttons), clamped to the floor (<c>&gt;= 1</c>). The floor
        /// is the minimum loop time: the route cannot dispatch faster than the run
        /// allows, so a <c>-</c> click at <c>N == 1</c> is a no-op.
        /// </summary>
        internal static int StepMultiplier(int currentN, int delta)
        {
            return Route.ClampCadenceMultiplier(currentN + delta);
        }

        /// <summary>
        /// The derived dispatch interval for a v0 same-body route:
        /// <c>N * TransitDuration</c> (the span). <paramref name="multiplier"/> is
        /// clamped to the floor first. Returns <paramref name="transitDuration"/>
        /// (the <c>N == 1</c> floor) when the span is non-positive / non-finite so
        /// the caller never derives a zero / NaN interval.
        /// </summary>
        internal static double DeriveDispatchInterval(int multiplier, double transitDuration)
        {
            int n = Route.ClampCadenceMultiplier(multiplier);
            if (double.IsNaN(transitDuration) || double.IsInfinity(transitDuration) || transitDuration <= 0.0)
                return transitDuration;
            return n * transitDuration;
        }

        /// <summary>
        /// Applies a new cadence multiplier to <paramref name="route"/>: clamps
        /// <c>N &gt;= 1</c>, sets <see cref="Route.CadenceMultiplier"/>, and
        /// recomputes <see cref="Route.DispatchInterval"/> = <c>N * TransitDuration</c>
        /// so the two stay in lock-step (Phase 4's loop clock reads
        /// <see cref="Route.DispatchInterval"/> unchanged). No-op (logs + returns
        /// false) when <paramref name="route"/> is null or the new value equals the
        /// current one. Info-logs the change so cadence edits are greppable.
        /// </summary>
        internal static bool ApplyMultiplier(Route route, int newMultiplier)
        {
            if (route == null)
            {
                ParsekLog.Verbose("Route", "RouteCadence.ApplyMultiplier: null route, ignored");
                return false;
            }

            int clamped = Route.ClampCadenceMultiplier(newMultiplier);
            if (clamped == route.CadenceMultiplier)
            {
                ParsekLog.Verbose("Route",
                    $"RouteCadence.ApplyMultiplier: route {ShortId(route.Id)} N unchanged={clamped} — no-op");
                return false;
            }

            int oldN = route.CadenceMultiplier;
            double oldInterval = route.DispatchInterval;
            route.CadenceMultiplier = clamped;
            route.DispatchInterval = DeriveDispatchInterval(clamped, route.TransitDuration);

            ParsekLog.Info("Route",
                $"RouteCadence: route {ShortId(route.Id)} cadence {oldN}x->{clamped}x " +
                $"interval {FormatR(oldInterval)}->{FormatR(route.DispatchInterval)} " +
                $"span={FormatR(route.TransitDuration)}");
            return true;
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<no-id>";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }

        private static string FormatR(double v)
        {
            return v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
