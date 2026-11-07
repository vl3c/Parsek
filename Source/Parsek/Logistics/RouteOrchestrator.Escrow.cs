using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    // Cycle escrow: reserving a cycle's pickup, re-establishing escrow for unfired windows and in-flight cycles after a load, releasing a window's escrow and the escrow source.
    internal static partial class RouteOrchestrator
    {
        // =====================================================================
        // M4b Phase B3 (plan D10/D11 / OQ7): the reserve / release ESCROW
        // lifecycle for source-debiting (pickup) windows. RESERVE at dispatch
        // (once per cycle, post-eligibility), RELEASE at each window's physical
        // debit (or on a window endpoint loss), so a partial cycle keeps the
        // un-fired window's reservation LIVE across the gap (a competing route's
        // B1 gate sees it). The leak-free invariant: every Reserve is matched by
        // a Release (debit or skip) within the cycle, or a Drop on tombstone /
        // scene-change. Reserve keys on the SAME resolved pid the B1 gate nets on.
        // =====================================================================

        /// <summary>
        /// (M4b B3) RESERVE this cycle's per-source cargo escrow at DISPATCH. Builds
        /// the per-pid SUMMED reservation list from the route's pickup
        /// <see cref="RouteStop"/>s (the SAME grouping <see cref="RoutePickupSourceGate"/>
        /// uses for the B1 gate), resolving each pickup source endpoint to the SAME
        /// live pid the gate nets on (<see cref="ResolveEscrowSourcePid"/>), and calls
        /// <see cref="RouteStore.ReserveCargo"/> once per (pid, resource). A competing
        /// higher-priority route gating the same source in this or a later tick (before
        /// this route's physical debit) then sees the source's available amount reduced
        /// by what this route reserved. Reserve is the SUMMED amount per pid so it nets
        /// to zero once every pickup window of the cycle releases its own portion.
        ///
        /// <para>Pure RAM (no route event); the per-cycle reserve is idempotent in
        /// practice because dispatch fires exactly once per cycle (the carrier-stop event-log
        /// guard), so this runs once per cycle. A delivery-only route reserves nothing.
        /// On an unresolved pickup source the build returns false and nothing is
        /// reserved (the eligibility gate already held the cycle; reserving a partial
        /// set would leak).</para>
        /// </summary>
        internal static void ReserveCycleEscrow(
            Route route, double currentUT, IRouteRuntimeEnvironment env, string cycleId)
        {
            ReserveCycleEscrow(route, currentUT, env, cycleId, includeStop: null, contextTag: "dispatch");
        }

        /// <summary>
        /// (M4b B3) Internal reserve worker shared by the dispatch-time full-cycle
        /// reserve (<paramref name="includeStop"/> = null) and the C1
        /// re-establish-on-resume path (<paramref name="includeStop"/> keeps only the
        /// un-fired windows of the resumed cycle). Builds the per-pid SUMMED
        /// reservation over the included pickup windows and calls
        /// <see cref="RouteStore.ReserveCargo"/> once per (pid, resource).
        /// <paramref name="contextTag"/> distinguishes the two call sites in the log.
        /// </summary>
        internal static void ReserveCycleEscrow(
            Route route, double currentUT, IRouteRuntimeEnvironment env, string cycleId,
            Func<RouteStop, bool> includeStop, string contextTag)
        {
            if (route == null || string.IsNullOrEmpty(route.Id)
                || route.Stops == null || route.Stops.Count == 0)
                return;

            RoutePickupSourceGate.PickupSourceResolution Resolver(RouteEndpoint endpoint)
            {
                uint pid = ResolveEscrowSourcePid(endpoint, env);
                if (pid == 0u)
                    return RoutePickupSourceGate.PickupSourceResolution.Miss("escrow-source-unresolved");
                // The escrow builder only reads ResolvedPid + the summed manifest; the
                // readers are unused on the reserve path, so pass harmless no-ops.
                return RoutePickupSourceGate.PickupSourceResolution.Ok(
                    pid, EscrowSourceNameForLog(endpoint, env), _ => 0.0, _ => 0);
            }

            bool built = RoutePickupSourceGate.TryBuildReservations(
                route, Resolver, includeStop,
                out List<RoutePickupSourceGate.PickupSourceReservation> reservations,
                out string unresolvedReason);

            if (!built)
            {
                // A pickup source did not resolve at dispatch. The eligibility gate
                // (which re-resolves the same sources) would normally have held this
                // cycle; if we still got here, reserve NOTHING (a partial reservation
                // would leak - some windows would never release a hold they never had
                // a matching reserve for). The window-debit release is a no-op against
                // the empty escrow, so no leak.
                ParsekLog.VerboseRateLimited(Tag, "escrow-reserve-unresolved-" + route.Id,
                    $"ReserveCycleEscrow: route {ShortIdForLog(route)} cycle={cycleId} " +
                    $"context={contextTag} pickup source unresolved (reason={unresolvedReason}) - reserved nothing");
                return;
            }

            if (reservations.Count == 0)
                return; // delivery-only / inventory-only / no un-fired window - nothing to reserve

            int reservedPids = 0;
            int reservedEntries = 0;
            for (int i = 0; i < reservations.Count; i++)
            {
                var r = reservations[i];
                bool any = false;
                foreach (var kv in r.SummedResourceManifest)
                {
                    RouteStore.ReserveCargo(route.Id, r.ResolvedPid, kv.Key, kv.Value);
                    reservedEntries++;
                    any = true;
                }
                if (any) reservedPids++;
            }

            ParsekLog.Verbose(Tag,
                $"ReserveCycleEscrow: route {ShortIdForLog(route)} cycle={cycleId} " +
                $"context={contextTag} reservedSources={reservedPids.ToString(IC)} " +
                $"reservedResourceEntries={reservedEntries.ToString(IC)} at ut={currentUT.ToString("R", IC)}");
        }

        /// <summary>
        /// (M4b B3 C1) RE-ESTABLISH this cycle's cargo escrow from the still-UN-FIRED
        /// pickup windows on a <c>dispatchAlready</c> resume - honoring OQ7/D11's
        /// "the reserve is recomputed from pending route state on the next Tick".
        /// After a within-game scene switch (<c>ClearAllEscrow</c>) or a reload
        /// (escrow is RAM-only) lands MID-cycle, the cycle resumes with its dispatch
        /// already in the route event log, so <see cref="ReserveCycleEscrow(Route, double, IRouteRuntimeEnvironment, string)"/>
        /// at dispatch does NOT re-run and the un-fired window's hold would be silently
        /// lost - a competing route could then drain the source in the gap (violating
        /// 19.2.5 strand-protection). This re-reserves ONLY the windows of cycle
        /// <paramref name="cycleIndex"/> whose <see cref="RouteStop.LastFiredCycleIndex"/>
        /// is still <c>&lt; cycleIndex</c> (a window already debited+released this cycle
        /// has its hold consumed and must NOT be re-reserved, else double).
        ///
        /// <para>Called from the tick pre-pass
        /// (<see cref="ReEstablishInFlightCycleEscrow"/>), which covers the gap ticks
        /// with no due window, and again from the dispatchAlready branch as a
        /// backstop.</para>
        ///
        /// <para><b>IDEMPOTENT:</b> runs ONLY when the route currently holds NO escrow
        /// (<see cref="RouteStore.HasEscrow"/> is false - i.e. a clear/reload dropped
        /// it). A normal in-session resume (no clear happened) still holds its
        /// reservation, so this short-circuits and does NOT re-reserve (no
        /// double-reserve). The C2 cycle-complete <see cref="RouteStore.DropRouteEscrow"/>
        /// then drops whatever this re-established when the cycle finishes.</para>
        /// </summary>
        internal static void ReEstablishEscrowForUnfiredWindows(
            Route route, double currentUT, IRouteRuntimeEnvironment env, string cycleId, long cycleIndex)
        {
            if (route == null || string.IsNullOrEmpty(route.Id))
                return;

            // Idempotency guard: only re-establish when the escrow was CLEARED (a
            // scene-switch ClearAllEscrow / a reload). A normal in-session resume
            // still holds its reservation -> skip (no double-reserve).
            if (RouteStore.HasEscrow(route.Id))
                return;

            ReserveCycleEscrow(route, currentUT, env, cycleId,
                includeStop: stop => stop != null && stop.LastFiredCycleIndex < cycleIndex,
                contextTag: "resume-reestablish");
        }

        /// <summary>
        /// Pure: the loop-cycle index of the route's IN-FLIGHT multi-stop cycle, i.e.
        /// one whose dispatch fired and at least one window fired, but whose last dock
        /// has not been reached. A fired window snaps its stop's
        /// <see cref="RouteStop.LastFiredCycleIndex"/> to the cycle, while
        /// <see cref="Route.LastObservedLoopCycleIndex"/> snaps only at cycle complete
        /// (or a blocked / modulo skip, which snaps every stop too), so the cycle is in
        /// flight exactly when the highest stop cursor is ahead of the route marker.
        /// Also requires an un-fired stop of that cycle with a pickup manifest (the only
        /// windows that hold escrow). Single-stop routes return false (no gap).
        /// </summary>
        internal static bool TryGetInFlightMultiStopCycle(Route route, out long cycleIndex)
        {
            cycleIndex = -1;
            if (route == null || route.Stops == null || route.Stops.Count < 2)
                return false;

            long maxFired = long.MinValue;
            for (int i = 0; i < route.Stops.Count; i++)
            {
                RouteStop s = route.Stops[i];
                if (s != null && s.LastFiredCycleIndex > maxFired)
                    maxFired = s.LastFiredCycleIndex;
            }
            if (maxFired < 0 || maxFired <= route.LastObservedLoopCycleIndex)
                return false;

            for (int i = 0; i < route.Stops.Count; i++)
            {
                RouteStop s = route.Stops[i];
                if (s != null && s.LastFiredCycleIndex < maxFired
                    && s.PickupManifest != null && s.PickupManifest.Count > 0)
                {
                    cycleIndex = maxFired;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Tick pre-pass (ROUTE-ESCROW-LOST-ON-SCENE-SWITCH): re-establish the cargo
        /// escrow of every ghost-driving route with an in-flight multi-stop cycle whose
        /// reservation was dropped (a scene-switch <c>ClearAllEscrow</c>, or a reload:
        /// escrow is RAM-only). Runs before any route is processed and does not need
        /// the route's loop unit or a due window, so the hold is back before a
        /// competing route can gate on the source in the dispatch-to-window gap.
        /// Cheap checks first; the event-log read (the cycle's dispatch row, which proves
        /// the cycle is open under <c>cycle-{C+S}</c>) runs only for a route that is in
        /// flight AND holds no escrow. Idempotent through
        /// <see cref="ReEstablishEscrowForUnfiredWindows"/>'s HasEscrow guard.
        /// Returns the number of routes whose escrow was re-established.
        /// </summary>
        internal static int ReEstablishInFlightCycleEscrow(
            IList<Route> routes, double currentUT, IRouteRuntimeEnvironment env)
        {
            if (routes == null || routes.Count == 0)
                return 0;

            int reestablished = 0;
            for (int i = 0; i < routes.Count; i++)
            {
                Route route = routes[i];
                if (route == null || string.IsNullOrEmpty(route.Id))
                    continue;
                if (!RouteStatusPolicy.GhostDriving(route.Status))
                    continue;
                if (!TryGetInFlightMultiStopCycle(route, out long cycleIndex))
                    continue;
                if (RouteStore.HasEscrow(route.Id))
                    continue;

                try
                {
                    string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles).ToString(IC);
                    // 0 = the cycle-stable dispatch carrier stop index ProcessMultiStopCrossings writes.
                    if (!IsDispatchAlreadyLogged(route.Id, cycleId, 0))
                    {
                        ParsekLog.VerboseRateLimited(Tag, "escrow-prepass-nodispatch-" + route.Id,
                            $"ReEstablishInFlightCycleEscrow: route {ShortIdForLog(route)} cycle={cycleId} " +
                            $"cycleIdx={cycleIndex.ToString(IC)} looks in flight but has no dispatch row - skipped",
                            5.0);
                        continue;
                    }

                    ReEstablishEscrowForUnfiredWindows(route, currentUT, env, cycleId, cycleIndex);
                    if (RouteStore.HasEscrow(route.Id))
                    {
                        reestablished++;
                        ParsekLog.Info(Tag,
                            $"ReEstablishInFlightCycleEscrow: route {ShortIdForLog(route)} cycle={cycleId} " +
                            $"cycleIdx={cycleIndex.ToString(IC)} escrow re-established for un-fired windows " +
                            $"at ut={currentUT.ToString("R", IC)}");
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag,
                        $"ReEstablishInFlightCycleEscrow: route {ShortIdForLog(route)} threw " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
            return reestablished;
        }

        /// <summary>
        /// (M4b B3) RELEASE one pickup window's escrow portion AFTER its physical
        /// debit fires (or when the window endpoint is lost mid-cycle, OQ4: the cargo
        /// was not taken, so the hold is freed without debiting). Releases the
        /// window's OWN <see cref="RouteStop.PickupManifest"/> amounts against the
        /// SAME resolved pid the reserve keyed on (<paramref name="resolvedPid"/> -
        /// the pid <see cref="ApplyPickupDebit"/> resolved, or the escrow-resolve pid
        /// on the unresolved-debit path). After the cycle's LAST pickup window
        /// releases, the source's reservation nets to zero (summed reserve ==
        /// sum-of-per-window releases per pid). A no-op against an empty escrow (e.g.
        /// after a reload, where the escrow was not persisted - the reserve is
        /// recomputed from pending state on the live session only).
        /// </summary>
        internal static void ReleaseWindowEscrow(
            Route route, RouteStop stop, uint resolvedPid, string cycleId, string contextTag)
        {
            if (route == null || string.IsNullOrEmpty(route.Id) || stop == null)
                return;
            if (resolvedPid == 0u)
                return; // unresolved pid - nothing was reserved against pid 0 (reserve skips it too)
            Dictionary<string, double> manifest = stop.PickupManifest;
            if (manifest == null || manifest.Count == 0)
                return; // inventory-only / pure-delivery window - no resource reservation to release

            int releasedEntries = 0;
            foreach (var kv in manifest)
            {
                if (string.IsNullOrEmpty(kv.Key) || !(kv.Value > 0.0))
                    continue;
                RouteStore.ReleaseCargo(route.Id, resolvedPid, kv.Key, kv.Value);
                releasedEntries++;
            }

            if (releasedEntries > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"ReleaseWindowEscrow: route {ShortIdForLog(route)} cycle={cycleId} " +
                    $"pid={resolvedPid.ToString(IC)} releasedResourceEntries={releasedEntries.ToString(IC)} " +
                    $"context={contextTag ?? "<none>"}");
            }
        }

        /// <summary>
        /// (M4b B3) Resolve a pickup-source endpoint to the SAME live pid the B1 gate
        /// and <see cref="ApplyPickupDebit"/> resolve to. Production resolves the live
        /// vessel via the env and returns its <c>persistentId</c> (exactly what
        /// <c>OriginVesselPid</c> on the debit outcome carries). When the env cannot
        /// return a live vessel (headless test env, or an off-Unity context), fall
        /// back to the endpoint's craft-baked <see cref="RouteEndpoint.VesselPersistentId"/>
        /// so the reserve and the release key on the same pid the test seam's
        /// <c>OriginVesselPid</c> reports. Returns 0 when neither resolves.
        /// </summary>
        private static uint ResolveEscrowSourcePid(RouteEndpoint endpoint, IRouteRuntimeEnvironment env)
        {
            if (env != null)
            {
                try
                {
                    if (env.TryResolveEndpointVessel(endpoint, out Vessel vessel, out _)
                        && vessel != null)
                        return vessel.persistentId;
                }
                catch (Exception ex)
                {
                    ParsekLog.Verbose(Tag,
                        $"ResolveEscrowSourcePid: env resolve threw {ex.GetType().Name}: {ex.Message}; " +
                        "falling back to endpoint baked pid");
                }
            }
            // Fallback: the craft-baked endpoint pid. This matches the pid the headless
            // test path's PickupDebitApplierForTesting returns as OriginVesselPid, so
            // reserve and release key identically.
            return endpoint.VesselPersistentId;
        }

        private static string EscrowSourceNameForLog(RouteEndpoint endpoint, IRouteRuntimeEnvironment env)
        {
            if (env != null)
            {
                try
                {
                    if (env.TryResolveEndpointVessel(endpoint, out Vessel vessel, out _)
                        && vessel != null)
                        return vessel.vesselName;
                }
                catch
                {
                    // best-effort name only
                }
            }
            return null;
        }
    }
}
