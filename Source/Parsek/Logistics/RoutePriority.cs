namespace Parsek.Logistics
{
    /// <summary>
    /// Pure priority-stepper math for the Logistics window's dispatch-priority
    /// control (M1, design D8). Lower priority value dispatches FIRST when
    /// several routes contend in the same orchestrator tick (the comparator is
    /// <see cref="RouteOrchestrator.CompareRoutesForTick"/>); 0 is the floor and
    /// the default. Extracted so the clamp + no-op + log shape is unit-testable
    /// without spinning up IMGUI, mirroring <see cref="RouteCadence"/>. The
    /// window's detail stepper calls <see cref="Step"/> for the new value and
    /// <see cref="Apply"/> to commit it, then persists through the existing
    /// deferred-mutation pattern.
    /// </summary>
    internal static class RoutePriority
    {
        /// <summary>
        /// Steps a dispatch priority by <paramref name="delta"/> (+1 / -1 from
        /// the stepper buttons), clamped to the floor (<c>&gt;= 0</c>). 0 is the
        /// highest priority (dispatches first), so a <c>-</c> click at 0 is a
        /// no-op.
        /// </summary>
        internal static int Step(int currentPriority, int delta)
        {
            return Route.ClampPriority(currentPriority + delta);
        }

        /// <summary>
        /// Applies a new dispatch priority to <paramref name="route"/>: clamps
        /// to the floor (<c>&gt;= 0</c>) and sets
        /// <see cref="Route.DispatchPriority"/>. Unlike a cadence change there is
        /// no derived field and no loop-clock rebase: priority only feeds the
        /// orchestrator's per-tick processing order. No-op (logs + returns
        /// false) when <paramref name="route"/> is null or the new value equals
        /// the current one. Info-logs the change so priority edits are greppable.
        /// </summary>
        internal static bool Apply(Route route, int newPriority)
        {
            if (route == null)
            {
                ParsekLog.Verbose("Route", "RoutePriority.Apply: null route, ignored");
                return false;
            }

            int clamped = Route.ClampPriority(newPriority);
            if (clamped == route.DispatchPriority)
            {
                ParsekLog.Verbose("Route",
                    $"RoutePriority.Apply: route {ShortId(route.Id)} priority unchanged={clamped} - no-op");
                return false;
            }

            int oldPriority = route.DispatchPriority;
            route.DispatchPriority = clamped;

            ParsekLog.Info("Route",
                $"RoutePriority: route {ShortId(route.Id)} priority " +
                $"{oldPriority.ToString(System.Globalization.CultureInfo.InvariantCulture)}->" +
                $"{clamped.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                "(lower dispatches first)");
            return true;
        }

        private static string ShortId(string id)
        {
            return RouteIds.Short(id);
        }
    }
}
