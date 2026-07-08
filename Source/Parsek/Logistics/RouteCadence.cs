namespace Parsek.Logistics
{
    /// <summary>
    /// Pure cadence-stepper math for the Logistics window's dispatch-cadence
    /// control (plan Phase 6 task 3). Extracted so the stepper logic
    /// (clamp + recompute <c>DispatchInterval = N * span</c>) is unit-testable
    /// without spinning up IMGUI. The window's row/detail stepper calls
    /// <see cref="StepMultiplier"/> for the new <c>N</c> and
    /// <see cref="ApplyMultiplier"/> to recompute the route's interval, then
    /// persists through the existing deferred-mutation pattern. M1 adds
    /// <see cref="ParseAndSnapInterval"/> so the inline Interval text field can
    /// snap a typed target time to the nearest whole run-multiple.
    /// </summary>
    internal static class RouteCadence
    {
        /// <summary>
        /// Parses a player-typed target dispatch interval and snaps it UP to the
        /// nearest whole multiple of <paramref name="span"/> (the route's natural run
        /// duration, <see cref="Route.TransitDuration"/>). The text may carry an
        /// optional trailing unit (<c>s</c>, <c>m</c>/<c>min</c>, <c>h</c>, <c>d</c> =
        /// Kerbin day = 21600 s); a plain number is read as seconds. So "30m", "2 h",
        /// "1d", "90s", and "1800" are all accepted:
        /// <c>N = ceil(target / span)</c>, floored at 1 via
        /// <see cref="Route.ClampCadenceMultiplier"/>. CEIL (not round) is the
        /// contract: a typed time always snaps up to the next full run-multiple
        /// so the route is never asked to dispatch faster than the run allows
        /// (the <c>DispatchInterval &gt;= span</c> invariant). The caller feeds the
        /// resulting <paramref name="multiplier"/> into
        /// <see cref="ApplyMultiplier"/>, which recomputes the interval and
        /// rebases the loop clock.
        ///
        /// Returns <c>false</c> (and sets <paramref name="multiplier"/> to 0) on a
        /// rejected parse so the caller never calls <see cref="ApplyMultiplier"/>
        /// with a bogus value: <see cref="ApplyMultiplier"/> would silently clamp 0
        /// up to 1 and wrongly reset an already-raised cadence. Rejects when:
        /// the text is null / empty / whitespace, fails an InvariantCulture
        /// <see cref="double.TryParse"/>, parses to NaN / Infinity / a non-positive
        /// value, or when <paramref name="span"/> itself is non-positive /
        /// non-finite (cannot snap). InvariantCulture is mandatory so comma-locale
        /// systems do not misparse the typed seconds.
        /// </summary>
        /// <param name="text">The raw text typed into the Interval field.</param>
        /// <param name="span">The route's natural run duration in seconds
        /// (<see cref="Route.TransitDuration"/>).</param>
        /// <param name="multiplier">The snapped cadence multiplier on success; 0 on
        /// reject.</param>
        /// <returns><c>true</c> when the parse succeeded and a valid multiplier was
        /// produced; <c>false</c> on any reject.</returns>
        internal static bool ParseAndSnapInterval(string text, double span, out int multiplier)
        {
            multiplier = 0;

            if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(text))
            {
                ParsekLog.Verbose("Route",
                    "RouteCadence.ParseAndSnapInterval text=<empty> -> reject (null/blank)");
                return false;
            }

            if (double.IsNaN(span) || double.IsInfinity(span) || span <= 0.0)
            {
                ParsekLog.Verbose("Route",
                    $"RouteCadence.ParseAndSnapInterval text='{text.Trim()}' span={FormatR(span)} " +
                    "-> reject (non-positive/non-finite span)");
                return false;
            }

            // Accept an optional trailing unit so the friendly displayed form (e.g.
            // "14.0m", "1.6d") round-trips and the player can type "30m", "2 h", "1d",
            // "90s", or a plain number (= seconds, backward compatible). "d" is a Kerbin
            // day (21600 s = 6 h), matching the window's FormatDuration. Check "min"
            // before the single-letter units so "20 min" is not mis-stripped.
            string raw = text.Trim();
            string lower = raw.ToLowerInvariant();
            double unitFactor = 1.0; // seconds
            if (lower.EndsWith("min")) { unitFactor = 60.0; raw = raw.Substring(0, raw.Length - 3); }
            else if (lower.EndsWith("s")) { unitFactor = 1.0; raw = raw.Substring(0, raw.Length - 1); }
            else if (lower.EndsWith("m")) { unitFactor = 60.0; raw = raw.Substring(0, raw.Length - 1); }
            else if (lower.EndsWith("h")) { unitFactor = 3600.0; raw = raw.Substring(0, raw.Length - 1); }
            else if (lower.EndsWith("d")) { unitFactor = 21600.0; raw = raw.Substring(0, raw.Length - 1); }

            double number;
            bool parsed = double.TryParse(
                raw.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out number);
            if (!parsed || double.IsNaN(number) || double.IsInfinity(number) || number <= 0.0)
            {
                ParsekLog.Verbose("Route",
                    $"RouteCadence.ParseAndSnapInterval text='{text.Trim()}' span={FormatR(span)} " +
                    "-> reject (unparseable/non-positive/non-finite target)");
                return false;
            }
            double target = number * unitFactor;

            int n = Route.ClampCadenceMultiplier((int)System.Math.Ceiling(target / span));
            multiplier = n;
            ParsekLog.Verbose("Route",
                $"RouteCadence.ParseAndSnapInterval text='{text.Trim()}' target={FormatR(target)} " +
                $"span={FormatR(span)} -> N={n.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            return true;
        }

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
        /// Production entry: applies a new cadence multiplier, resolving the
        /// live UT defensively (mirrors <c>RouteOrchestrator.TryPause</c>) so
        /// the M5 windowed-basis rebase can compute the current dock cycle. A
        /// throw / pre-flight UT falls back to the flat -1 rebase inside the
        /// UT-injected overload (safe: production in-flight never throws here,
        /// and a windowed route cannot be cadence-edited outside a live scene).
        /// </summary>
        internal static bool ApplyMultiplier(Route route, int newMultiplier)
        {
            double ut = -1.0;
            try
            {
                ut = Planetarium.GetUniversalTime();
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("Route",
                    $"RouteCadence.ApplyMultiplier: live UT resolution threw {ex.GetType().Name}: " +
                    $"{ex.Message}; applying with the flat rebase");
            }
            return ApplyMultiplier(route, newMultiplier, ut);
        }

        /// <summary>
        /// Applies a new cadence multiplier to <paramref name="route"/>: clamps
        /// <c>N &gt;= 1</c>, sets <see cref="Route.CadenceMultiplier"/>, and
        /// recomputes <see cref="Route.DispatchInterval"/> = <c>N * TransitDuration</c>
        /// so the two stay in lock-step (Phase 4's loop clock reads
        /// <see cref="Route.DispatchInterval"/> unchanged). No-op (logs + returns
        /// false) when <paramref name="route"/> is null or the new value equals the
        /// current one. Info-logs the change so cadence edits are greppable.
        ///
        /// <para><b>M5 (D3 / review C4) windowed-basis rebase.</b> Under the
        /// <c>ReaimWindows</c> basis the historical reset-to--1 would make the
        /// already-delivered CURRENT window owed again under a FRESH
        /// <c>cycle-{C+S}</c> id the ELS <c>(RouteId, cycleId, stopIndex)</c>
        /// backstop cannot suppress - a duplicate delivery for a window whose
        /// ghost does not re-fly, with the next real window a synodic period
        /// away. So for a windowed route this re-baselines
        /// <see cref="Route.LastObservedLoopCycleIndex"/> to the CURRENT dock
        /// cycle index instead (per-stop cursors snapped against each stop's own
        /// dock phase), while the modulo anchor still resets to -1 at all rebase
        /// sites - the first crossing after the rebase re-anchors and delivers.
        /// Flat / zero-drift routes keep the accepted F5 one-immediate-fire -1
        /// reset byte-identically (their window index space genuinely changes
        /// with <c>DispatchInterval</c>; a re-aim unit's does not - the build
        /// discards the interval, so pre/post units index windows identically
        /// and the pre-mutation basis probe is safe).</para>
        /// </summary>
        internal static bool ApplyMultiplier(Route route, int newMultiplier, double currentUT)
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

            // M5 (review C4): probe the window basis + loop state BEFORE mutating
            // the interval (the resolved unit is signature-cached and cheap; for
            // a ReaimWindows unit DispatchInterval is dead input, so the
            // pre-mutation unit indexes windows identically to the post-mutation
            // one). Only a resolvable ReaimWindows clock takes the windowed
            // rebase; everything else (flat, zero-drift, unresolvable unit,
            // pre-anchor clock, no live UT) keeps the flat -1 reset.
            bool windowedRebase = false;
            double loopUT = 0.0;
            long cycleIndex = 0;
            if (currentUT > 0.0 && route.IsLoopRoute)
            {
                GhostPlaybackLogic.LoopUnit? unitOpt =
                    RouteOrchestrator.ResolveLoopUnit(route, currentUT);
                if (unitOpt.HasValue
                    && RouteLoopClock.DeriveWindowBasis(unitOpt.Value) == RouteWindowBasis.ReaimWindows)
                {
                    windowedRebase = RouteLoopClock.TryGetRouteLoopState(
                        unitOpt.Value, currentUT, out loopUT, out cycleIndex, out _);
                }
            }

            int oldN = route.CadenceMultiplier;
            double oldInterval = route.DispatchInterval;
            route.CadenceMultiplier = clamped;
            route.DispatchInterval = DeriveDispatchInterval(clamped, route.TransitDuration);

            long prevObserved = route.LastObservedLoopCycleIndex;
            if (windowedRebase)
            {
                // M5 (D3 / review C4): re-baseline to the CURRENT dock cycle
                // index - the already-delivered window stays consumed, the next
                // window still fires (and re-anchors via D3 adoption).
                long dockCycle = RouteLoopClock.ComputeDockCycleIndex(
                    loopUT, cycleIndex, route.RecordedDockUT);
                route.LastObservedLoopCycleIndex = dockCycle;
                int stopsSnapped = 0;
                if (route.Stops != null)
                {
                    for (int i = 0; i < route.Stops.Count; i++)
                    {
                        RouteStop s = route.Stops[i];
                        if (s == null) continue;
                        double stopDockUT = s.RecordedDockUT >= 0.0
                            ? s.RecordedDockUT : route.RecordedDockUT;
                        s.LastFiredCycleIndex = RouteLoopClock.ComputeDockCycleIndex(
                            loopUT, cycleIndex, stopDockUT);
                        stopsSnapped++;
                    }
                }
                long prevAnchor = route.WindowAnchorCycleIndex;
                route.WindowAnchorCycleIndex = -1;

                ParsekLog.Info("Route",
                    $"RouteCadence: route {ShortId(route.Id)} cadence {oldN}x->{clamped}x " +
                    $"interval {FormatR(oldInterval)}->{FormatR(route.DispatchInterval)} " +
                    $"span={FormatR(route.TransitDuration)} WINDOWED rebase (ReaimWindows): " +
                    $"lastObservedLoopCycleIndex {prevObserved.ToString(System.Globalization.CultureInfo.InvariantCulture)}->" +
                    $"{route.LastObservedLoopCycleIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"(current dock cycle; no duplicate delivery of the delivered window) " +
                    $"anchor {prevAnchor.ToString(System.Globalization.CultureInfo.InvariantCulture)}->-1 " +
                    $"stopsSnapped={stopsSnapped.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                return true;
            }

            // LST-3: rebase the loop clock when N actually changes. CadenceSeconds
            // derives from DispatchInterval, so the same UT now resolves to a
            // DIFFERENT span-clock cycleIndex (TryComputeSpanLoopUT). Leaving
            // LastObservedLoopCycleIndex stale would either stall the next crossing
            // (N raised -> smaller cycleIndex never exceeds the stale value) or snap
            // it forward (N lowered -> larger cycleIndex jumps past several). Reset to
            // -1 (mirrors RouteOrchestrator.TryActivate) so the clock re-anchors
            // cleanly: the next crossing inside the span fires exactly once, no
            // double-fire. We only reach here when N genuinely changed (the no-op
            // same-N path returned false above), so the reset is never spurious.
            route.LastObservedLoopCycleIndex = -1;

            // M4a A3 (OQ3 / C1): the per-stop fire sub-gates rebase with the
            // route-level cursor. A multi-stop route's later windows would
            // otherwise stall (N raised -> smaller cycleIndex never exceeds the
            // stale per-stop LastFiredCycleIndex) or jump (N lowered) on the next
            // crossing. Resetting all stops to -1 re-anchors the clock cleanly: each
            // window's next in-span crossing fires exactly once, no double-fire.
            RouteOrchestrator.ResetStopFireState(route);

            // M5 (D3): the modulo anchor resets alongside the cursor at every
            // rebase site (a no-op for the flat routes that dominate this path).
            route.WindowAnchorCycleIndex = -1;

            ParsekLog.Info("Route",
                $"RouteCadence: route {ShortId(route.Id)} cadence {oldN}x->{clamped}x " +
                $"interval {FormatR(oldInterval)}->{FormatR(route.DispatchInterval)} " +
                $"span={FormatR(route.TransitDuration)} " +
                $"lastObservedLoopCycleIndex {prevObserved.ToString(System.Globalization.CultureInfo.InvariantCulture)}->-1 (rebase)");
            return true;
        }

        private static string ShortId(string id)
        {
            return RouteIds.Short(id);
        }

        private static string FormatR(double v)
        {
            return v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
