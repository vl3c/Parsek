using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Orchestrator shell for the route dispatch scheduler. Driven from
    /// <c>ParsekScenario.Update</c> at the cadence defined by
    /// <see cref="TickIntervalSec"/>. Pure-logic decisions live in
    /// <see cref="RouteDispatchEvaluator"/>; this class wires the evaluator
    /// up to live KSP state via <see cref="LiveRouteRuntimeEnvironment"/> and
    /// applies the resulting <see cref="RouteDispatchDecision"/> to the
    /// committed routes — including action emission on Dispatch / EndpointLost
    /// (Phase 4 of the dispatch-scheduler plan).
    /// </summary>
    internal static partial class RouteOrchestrator
    {
        /// <summary>Wall-clock cadence at which the orchestrator advances routes.</summary>
        internal const double TickIntervalSec = 1.0;

        /// <summary>
        /// Retry backoff for transient wait states (resources, capacity,
        /// endpoint loss). Sets <c>Route.NextEligibilityCheckUT = currentUT + this</c>.
        /// </summary>
        internal const double WaitRetryIntervalSec = 30.0;

        /// <summary>
        /// Cap on the number of catch-up cycles processed in a single tick when
        /// the route is behind schedule (long unfocused warp). Prevents pathological
        /// per-tick work explosions on resume.
        /// </summary>
        internal const int MaxCatchUpCyclesPerTick = 32;

        /// <summary>
        /// Surface proximity radius (metres) for endpoint resolution fallback. When
        /// the saved <see cref="RouteEndpoint.VesselPersistentId"/> no longer
        /// resolves but the endpoint is surface-typed, the resolver searches for
        /// the closest matching surface vessel within this radius. 500 m is tight
        /// enough that a depot the player intentionally drove away from its dock
        /// spot loses the route (correct behaviour), while still tolerating the
        /// small drift from terrain settling, autostrut adjustments, and
        /// floating-origin shifts after warp / scene-load. Tuned down from 2000 m
        /// after the v0 playtests on the user's feedback.
        /// </summary>
        internal const double SurfaceProximityRadiusMeters = 500.0;

        /// <summary>
        /// (M4a A3) Per-window <see cref="RouteEvent.Sequence"/> stride. Under
        /// Horn A a multi-stop cycle fires N windows under ONE cycleId, so the
        /// fixed intra-window order (RouteDispatched 0 / RouteCargoDebited 1 /
        /// RouteCargoPickedUp 2 / RouteCargoDelivered 3) is NOT unique across
        /// windows. Each window's rows are offset by
        /// <c>stopIndex * SEQ_STRIDE + intraSeq</c> so <c>(UT, Sequence)</c> stays
        /// a TOTAL order across windows (replay-stability + the event-log dedup
        /// equality). Stride 8 (&gt;= 4 to cover the four intra-window types with
        /// headroom). A single-stop route fires only stop 0, so its sequences stay
        /// 0/1/2/3 -- byte-identical to the pre-A3 fixed values.
        /// </summary>
        internal const int SeqStride = 8;

        /// <summary>
        /// Canonical <see cref="ParsekLog"/> subsystem tag for every route-subsystem
        /// log line. Do not introduce new tag names — the integration follow-up
        /// unified all route logs under this single tag.
        /// </summary>
        internal const string Tag = "Route";

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // ==================================================================
        // Manual Send-Once (v0 UI testing)
        // ==================================================================

        /// <summary>
        /// Player-driven "Send Once" — arms a one-shot dispatch on a route.
        /// Disables the auto-cycle loop (via <see cref="Route.PauseAfterCurrentCycle"/>),
        /// brings the route's <see cref="Route.NextDispatchUT"/> down to
        /// <paramref name="currentUT"/> so the interval-wait component of the
        /// auto-cycle is skipped, and clears any wait-retry backoff. The
        /// per-cycle eligibility gates (ERS, resources, endpoint
        /// resolution, and any future orbital-alignment / transfer-window
        /// check) STILL apply: the route dispatches at the next moment those
        /// conditions are met. After that single cycle delivers, the orchestrator
        /// transitions the route to <see cref="RouteStatus.Paused"/> instead
        /// of looping back to Active.
        ///
        /// <para><b>Loop-clock dock phase is one of those conditions.</b> A v0
        /// route is a looped Mission segment; delivery fires when the loop clock
        /// reaches the recorded dock PHASE (design 0.4 / 0.9 DEL-2), which is the
        /// END of the <c>[launch..dock]</c> segment. The clock free-runs on
        /// absolute UT and Send Once intentionally does NOT reset
        /// <see cref="Route.LastObservedLoopCycleIndex"/> (resetting it would
        /// mis-fire a delivery mid-flight, with no ghost at the dock). So
        /// re-arming Send Once schedules ONE delivery at the next UN-delivered
        /// dock crossing: anywhere from ~immediate (if a dock passed during the
        /// pause) up to nearly one full span when the clock has just passed the
        /// prior dock. Re-arming right after a delivery lands near that worst
        /// case - the ghost must fly its outbound run to dock again. This is
        /// expected, not a stuck route; see the "send-once re-arm" closed entry
        /// in <c>docs/dev/todo-and-known-bugs.md</c>.</para>
        ///
        /// <para>If the route is currently Paused, "Send Once" un-pauses it
        /// to Active first so the one-shot cycle can dispatch.</para>
        ///
        /// <para>Returns true when the one-shot was armed. Returns false (with
        /// an Info log) when the route is null or in a status that blocks
        /// dispatch (`InTransit`, `MissingSourceRecording`, `SourceChanged`,
        /// `EndpointLost`).</para>
        /// </summary>
        internal static bool TrySendOneCycleNow(Route route, double currentUT)
        {
            if (route == null)
            {
                ParsekLog.Info(Tag, "TrySendOneCycleNow: route=null");
                return false;
            }

            // Status gates: refuse states that aren't recoverable into a
            // dispatchable one-shot. Paused IS accepted — Send Once un-pauses
            // for the next cycle.
            if (route.Status != RouteStatus.Active
                && route.Status != RouteStatus.WaitingForResources
                && route.Status != RouteStatus.DestinationFull
                && route.Status != RouteStatus.Paused)
            {
                ParsekLog.Info(Tag,
                    $"TrySendOneCycleNow: route={ShortIdForLog(route)} status={route.Status} — not dispatchable");
                return false;
            }

            RouteStatus previousStatus = route.Status;
            double previousNext = route.NextDispatchUT;

            // Disable the auto-cycle loop. After the upcoming cycle's delivery,
            // ApplyDelivery transitions the route to Paused instead of Active.
            route.PauseAfterCurrentCycle = true;
            // Route-timeline events: persist the Send Once provenance so (a) the UI
            // "Sending one cycle" state survives save/reload and (b) the dispatched
            // route event is stamped RouteSendOnce, bracketing the one-shot run in
            // the timeline together with the auto-pause row that follows delivery.
            route.SendOnceArmed = true;

            // Un-pause so the dispatch evaluator can fire on the next tick.
            if (route.Status == RouteStatus.Paused)
                route.TransitionTo(RouteStatus.Active, "send-once-arm");

            // Bring scheduling forward so the interval component of the
            // auto-cycle wait is skipped. The per-cycle conditions (resources,
            // endpoint, alignment) are evaluated by the dispatch
            // evaluator on the next tick and still gate the dispatch.
            if (route.NextDispatchUT > currentUT)
                route.NextDispatchUT = currentUT;
            route.NextEligibilityCheckUT = null;

            ParsekLog.Info(Tag,
                $"TrySendOneCycleNow: route={ShortIdForLog(route)} " +
                $"prevStatus={previousStatus} prevNextDispatchUT={previousNext.ToString("R", IC)} " +
                $"newNextDispatchUT={route.NextDispatchUT.ToString("R", IC)} " +
                $"PauseAfterCurrentCycle=true (route will dispatch one cycle when conditions allow, then transition to Paused)");
            return true;
        }

        /// <summary>
        /// Player-driven "Activate" — turns a Paused route into an auto-dispatching
        /// Active route. Routes are created Paused; Activate is how the player opts
        /// in to periodic dispatch after verifying the run (typically via Send Once).
        /// Clears any stale one-shot arm and any wait backoff. If the stored
        /// <see cref="Route.NextDispatchUT"/> is already in the past, it is pulled up
        /// to <paramref name="currentUT"/> so a freshly-activated route dispatches
        /// promptly instead of immediately firing a backlog of missed cycles.
        /// Returns false (Info-logged) for a null route or a route that is not
        /// currently <see cref="RouteStatus.Paused"/>.
        /// </summary>
        internal static bool TryActivate(Route route, double currentUT)
        {
            if (route == null)
            {
                ParsekLog.Info(Tag, "TryActivate: route=null");
                return false;
            }
            if (route.Status != RouteStatus.Paused)
            {
                ParsekLog.Info(Tag,
                    $"TryActivate: route={ShortIdForLog(route)} status={route.Status} — only a Paused route can be activated");
                return false;
            }

            route.PauseAfterCurrentCycle = false;
            // Activate supersedes any pending Send Once one-shot (the route now
            // auto-dispatches, so one-shot provenance must not linger).
            route.SendOnceArmed = false;
            route.NextEligibilityCheckUT = null;
            if (route.NextDispatchUT < currentUT)
                route.NextDispatchUT = currentUT;

            // Loop-clock reset discipline (plan Phase 4 task 5). Activating a
            // loop-route restarts cycle observation: -1 means "no cycle observed
            // yet", so the FIRST post-activate crossing fires. The field persists
            // through the codec so a save/reload mid-cycle does NOT double-fire
            // (the event log is the backstop). LoopAnchorUT capture-on-activate (plan Phase 5
            // task 1: "set route.LoopAnchorUT on activate") is set here for the
            // Paused->Activate path; RouteBuilder seeds it for create-Active. It is
            // the loop clock's phase anchor (RouteBackingMission.BuildMission copies it
            // into the backing mission, MissionLoopUnitBuilder takes max(anchor,
            // spanEnd)), so it defines the index space LastObservedLoopCycleIndex
            // counts in: moving it and resetting the cursor go together.
            long prevObserved = route.LastObservedLoopCycleIndex;
            double prevAnchor = route.LoopAnchorUT;
            RestartLoopObservation(route, currentUT);

            // M6 hold reasons: activation resets loop observation, so a
            // prior-session hold must not present as current. (Pause keeps the
            // hold - it answers "why wasn't this delivering".)
            route.ClearHold("player-activate");

            route.TransitionTo(RouteStatus.Active, "player-activate");
            // Route-timeline events: the durable resume marker. Recorded explicitly
            // because the next RouteDispatched row can be arbitrarily far away (or
            // never come, on a route that blocks on eligibility after resume).
            EmitRouteLifecycleMarker(route, currentUT, RouteEventType.RouteResumed, "player-activate");
            ParsekLog.Info(Tag,
                $"TryActivate: route={ShortIdForLog(route)} now Active " +
                $"nextDispatchUT={route.NextDispatchUT.ToString("R", IC)} " +
                $"loopRoute={(route.IsLoopRoute ? "1" : "0")} " +
                $"lastObservedLoopCycleIndex {prevObserved.ToString(IC)}->{route.LastObservedLoopCycleIndex.ToString(IC)} " +
                $"loopAnchorUT {prevAnchor.ToString("R", IC)}->{route.LoopAnchorUT.ToString("R", IC)}");
            return true;
        }

        /// <summary>
        /// Restarts a loop route's cycle observation at <paramref name="currentUT"/>, the way
        /// an activation does: -1 means "no cycle observed yet", so the FIRST crossing after
        /// the restart fires. The field persists through the codec so a save/reload mid-cycle
        /// does NOT double-fire (the event log is the backstop). <c>LoopAnchorUT</c> is the
        /// loop clock's phase anchor (RouteBackingMission.BuildMission copies it into the
        /// backing mission, MissionLoopUnitBuilder takes max(anchor, spanEnd)), so it defines
        /// the index space <c>LastObservedLoopCycleIndex</c> counts in: moving it and
        /// resetting the cursor go together, and the crossings the route did not watch while
        /// it was stopped are rebased away rather than fired as a backlog. No-op for a
        /// non-loop route.
        /// </summary>
        private static void RestartLoopObservation(Route route, double currentUT)
        {
            if (!route.IsLoopRoute)
                return;
            route.LastObservedLoopCycleIndex = -1;
            route.LoopAnchorUT = currentUT;
            // M4a A3 (OQ3): the per-stop fire sub-gates must reset alongside the
            // route-level cycle cursor, else a multi-stop route restarted after a
            // stop would see stale LastFiredCycleIndex values and stall (raised) or
            // jump (lowered) the windows after the restart. -1 means "no cycle fired
            // yet" so the first crossing of each window after the restart fires
            // exactly once.
            ResetStopFireState(route);
            // M5 (D3): the residual-modulo offset anchor resets wherever the cycle
            // cursor rebases - the first crossing after the restart on a windowed route
            // re-anchors and delivers (anchor adoption).
            route.WindowAnchorCycleIndex = -1;
            // Deliberately NOT reset: ReaimWindowBasisEngaged. It is the D6
            // flip-detector's MEMORY, not cycle state - keeping it across a stop
            // lets the first tick after the restart still detect a basis flip that
            // happened meanwhile and decline fail-closed (re-baseline, no fire)
            // instead of firing off a stale index space.
        }

        /// <summary>
        /// The cycle a single-stop loop route lost its endpoint in was dispatched but never
        /// completed, so its id (<c>cycle-{Completed+Skipped}</c>) is still the route's next
        /// id, and the first crossing after a restart would meet that cycle's dispatch row in
        /// the single-stop replay backstop and run nothing. Counts it as skipped instead (the
        /// rewind's cycle-counter rebuild counts a dispatched, undelivered cycle the same
        /// way), so the next crossing opens a fresh cycle. No-op when the next id has no
        /// dispatch row, and for a multi-stop route: its crossing path treats a logged
        /// dispatch as a RESUME of the paid-for cycle and fires only the windows that have
        /// not delivered, so skipping there would debit the origin again and re-deliver the
        /// stops that already received this cycle's cargo.
        /// </summary>
        private static void SkipUndeliveredLostCycle(Route route)
        {
            if (route.Stops != null && route.Stops.Count > 1)
                return;
            string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles).ToString(IC);
            var events = RouteEventLog.Events;
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (e != null && e.Type == RouteEventType.RouteDispatched
                    && string.Equals(e.RouteId, route.Id, StringComparison.Ordinal)
                    && string.Equals(e.RouteCycleId, cycleId, StringComparison.Ordinal))
                {
                    route.SkippedCycles += 1;
                    ParsekLog.Info(Tag,
                        $"EndpointRescan: route={ShortIdForLog(route)} lost cycle={cycleId} was dispatched, " +
                        $"not completed - counted skipped (skippedCycles={route.SkippedCycles.ToString(IC)})");
                    return;
                }
            }
        }

        /// <summary>Transition and marker reason of a stopped route a Re-scan found again.</summary>
        internal const string EndpointRescanReason = "endpoint-rescan";

        /// <summary>
        /// The player's "Re-scan for endpoint" found every stop of an
        /// <see cref="RouteStatus.EndpointLost"/> route again (the caller resolved them).
        /// A non-loop route only has its retry gate cleared: its dispatch evaluator
        /// re-resolves the endpoint on the next tick and flips the status itself. A loop
        /// route has no such retry (its tick skips every route that is not ghost-driving),
        /// so the re-scan restarts it here: back to the status the route had before it
        /// stopped (<see cref="Route.PreMissingStatus"/>, which a rewind sets to Paused when
        /// the kept timeline has the route paused), with the loop observation restarted at
        /// <paramref name="currentUT"/> like an activation, so the crossings missed while it
        /// was stopped are not fired as a backlog. A restart to Active writes a
        /// <see cref="RouteEventType.RouteResumed"/> marker, the Route History row that
        /// closes the stopped episode. An armed Send Once or pause-after-run stays armed.
        /// Returns false (Info-logged) for a null route or one that is not EndpointLost.
        /// </summary>
        internal static bool TryRecoverEndpointLostAfterRescan(Route route, double currentUT)
        {
            if (route == null)
            {
                ParsekLog.Info(Tag, "EndpointRescan: route=null");
                return false;
            }
            if (route.Status != RouteStatus.EndpointLost)
            {
                ParsekLog.Info(Tag,
                    $"EndpointRescan: route={ShortIdForLog(route)} status={route.Status} - only an EndpointLost route recovers");
                return false;
            }

            route.NextEligibilityCheckUT = null;
            if (!route.IsLoopRoute)
            {
                ParsekLog.Info(Tag,
                    $"EndpointRescan: route={ShortIdForLog(route)} loopRoute=0 cleared retry gate " +
                    "(the dispatch evaluator re-resolves and recovers the status)");
                return true;
            }

            RouteStatus baseline = route.PreMissingStatus == RouteStatus.Paused
                ? RouteStatus.Paused
                : RouteStatus.Active;
            RouteStatus prevPreMissing = route.PreMissingStatus;
            route.PreMissingStatus = RouteStatus.Active;
            long prevObserved = route.LastObservedLoopCycleIndex;
            double prevAnchor = route.LoopAnchorUT;
            if (baseline == RouteStatus.Paused)
            {
                // The kept timeline has the route paused: the player's later Activate
                // restarts the loop observation and writes the resume marker.
                route.TransitionTo(RouteStatus.Paused, EndpointRescanReason);
            }
            else
            {
                RestartLoopObservation(route, currentUT);
                SkipUndeliveredLostCycle(route);
                if (route.NextDispatchUT < currentUT)
                    route.NextDispatchUT = currentUT;
                route.ClearHold(EndpointRescanReason);
                route.TransitionTo(RouteStatus.Active, EndpointRescanReason);
                EmitRouteLifecycleMarker(route, currentUT, RouteEventType.RouteResumed, EndpointRescanReason);
            }
            ParsekLog.Info(Tag,
                $"EndpointRescan: route={ShortIdForLog(route)} loopRoute=1 EndpointLost->{baseline} " +
                $"preMissingStatus={prevPreMissing} " +
                $"lastObservedLoopCycleIndex {prevObserved.ToString(IC)}->{route.LastObservedLoopCycleIndex.ToString(IC)} " +
                $"loopAnchorUT {prevAnchor.ToString("R", IC)}->{route.LoopAnchorUT.ToString("R", IC)} " +
                $"sendOnceArmed={(route.SendOnceArmed ? "1" : "0")} pauseAfterRun={(route.PauseAfterCurrentCycle ? "1" : "0")}");
            return true;
        }

        /// <summary>
        /// Player-driven "Pause" — stops auto-dispatch on a route. A route that is
        /// not in transit transitions to <see cref="RouteStatus.Paused"/> immediately
        /// (including blocked-but-active waits and the hard-broken states, so the
        /// player can quiet a failing route). A route that is mid-cycle
        /// (<see cref="RouteStatus.InTransit"/>) instead arms
        /// <see cref="Route.PauseAfterCurrentCycle"/> so the current delivery still
        /// completes, then the route lands in Paused via the delivery applier rather
        /// than being abandoned in flight. Returns false (Info-logged) for a null
        /// route or one already Paused.
        /// </summary>
        internal static bool TryPause(Route route)
        {
            // Production entry: resolve the live UT for the RoutePaused marker.
            // Resolve defensively: a degenerate environment (early load, or an
            // off-Unity context) must NOT block the pause. If the live UT cannot
            // be obtained, pass -1 and the marker emit skips with a Warn.
            double ut = -1.0;
            try
            {
                ut = Planetarium.GetUniversalTime();
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"TryPause: live UT resolution threw {ex.GetType().Name}: {ex.Message}; " +
                    "pausing without a resolved UT");
            }
            return TryPause(route, ut);
        }

        /// <summary>
        /// UT-injected pause used by unit tests, and the implementation the
        /// parameterless <see cref="TryPause(Route)"/> delegates to with the live
        /// UT. <paramref name="currentUT"/> stamps the <see cref="RouteEventType.RoutePaused"/>
        /// marker row.
        /// </summary>
        internal static bool TryPause(Route route, double currentUT)
        {
            if (route == null)
            {
                ParsekLog.Info(Tag, "TryPause: route=null");
                return false;
            }
            if (route.Status == RouteStatus.Paused)
            {
                ParsekLog.Info(Tag, $"TryPause: route={ShortIdForLog(route)} already Paused");
                return false;
            }

            if (route.Status == RouteStatus.InTransit)
            {
                route.PauseAfterCurrentCycle = true;
                // A player Pause during a Send-Once cycle supersedes the one-shot
                // provenance: the arm's dispatch row was already stamped at emit,
                // and the UI label must flip from "Sending one cycle" to the
                // pausing-after-cycle state (review finding 1, PR #1327).
                if (route.SendOnceArmed)
                {
                    route.SendOnceArmed = false;
                    ParsekLog.Info(Tag,
                        $"TryPause: route={ShortIdForLog(route)} cleared Send Once arm (pause supersedes one-shot)");
                }
                ParsekLog.Info(Tag,
                    $"TryPause: route={ShortIdForLog(route)} InTransit — armed PauseAfterCurrentCycle " +
                    "(current cycle finishes, then route pauses)");
                return true;
            }

            // M4b escrow-strand fix (PR #1180 review): a multi-origin route that reserved
            // its per-source escrow at dispatch and is paused MID-cycle never reaches the
            // cycle-complete DropRouteEscrow sweep, so the stale reservation would keep
            // mis-gating a competing route sharing that source until the next scene switch.
            // Drop it on the immediate-pause transition. Idempotent no-op when nothing is
            // held (a delivery-only route, or a route paused between cycles).
            RouteStore.DropRouteEscrow(route.Id);

            route.PauseAfterCurrentCycle = false;
            // A pending Send Once one-shot is cleared by an explicit pause. This path
            // still writes its RoutePaused marker below; taking back an undispatched
            // Send without any route event is TryCancelSendOnce.
            if (route.SendOnceArmed)
            {
                route.SendOnceArmed = false;
                ParsekLog.Info(Tag,
                    $"TryPause: route={ShortIdForLog(route)} cleared pending Send Once arm");
            }
            route.TransitionTo(RouteStatus.Paused, "player-pause");
            // Route-timeline events: record the pause point so the route event timeline
            // carries the player's pause history (retired at rewind past it).
            EmitRouteLifecycleMarker(route, currentUT, RouteEventType.RoutePaused, "player-pause");
            return true;
        }

        /// <summary>Transition reason of a Send arm taken back before its run launched.</summary>
        internal const string SendOnceCancelledReason = "send-once-cancelled";

        /// <summary>
        /// Player "Cancel" on a Send-armed route whose run has not launched: undoes the
        /// arm and returns the route to Paused, writing NO route event. Arming wrote none
        /// either (<see cref="TrySendOneCycleNow"/> only flips fields; the dispatch row is
        /// stamped when the run fires), so a cancelled Send leaves the route event log, and the
        /// Route History built from it, exactly as before the arm. The Logistics window
        /// offers Send only on a Paused route, so Paused is the state the arm left.
        /// Refuses (false, Info-logged) a null route, a route with no pending Send arm,
        /// and a route whose run is in flight (InTransit: a launched run cannot be
        /// called back) or already Paused. Drops any escrow the arm could have reserved
        /// (idempotent no-op when none is held).
        /// </summary>
        internal static bool TryCancelSendOnce(Route route)
        {
            if (route == null)
            {
                ParsekLog.Info(Tag, "TryCancelSendOnce: route=null");
                return false;
            }
            if (!route.SendOnceArmed)
            {
                ParsekLog.Info(Tag,
                    $"TryCancelSendOnce: route={ShortIdForLog(route)} status={route.Status} has no pending Send arm");
                return false;
            }
            if (route.Status == RouteStatus.InTransit || route.Status == RouteStatus.Paused)
            {
                ParsekLog.Info(Tag,
                    $"TryCancelSendOnce: route={ShortIdForLog(route)} status={route.Status} - not cancellable " +
                    "(only an armed run that has not launched can be taken back)");
                return false;
            }

            RouteStore.DropRouteEscrow(route.Id);
            route.SendOnceArmed = false;
            route.PauseAfterCurrentCycle = false;
            route.TransitionTo(RouteStatus.Paused, SendOnceCancelledReason);
            ParsekLog.Info(Tag,
                $"TryCancelSendOnce: route={ShortIdForLog(route)} Send arm taken back before launch; " +
                "route Paused, no route event written");
            return true;
        }

        // ==================================================================
        // Tick entry points
        // ==================================================================

        /// <summary>
        /// Production tick entry point invoked from <c>ParsekScenario.Update</c>.
        /// Constructs a fresh <see cref="LiveRouteRuntimeEnvironment"/> for the
        /// tick (caches ERS internally for the duration of the call) and
        /// delegates to the env-injected overload.
        /// </summary>
        internal static void Tick(double currentUT)
        {
            Tick(currentUT, new LiveRouteRuntimeEnvironment());
        }

        /// <summary>
        /// Env-injected tick used by unit tests. Production calls the no-env
        /// overload which constructs a <see cref="LiveRouteRuntimeEnvironment"/>;
        /// tests supply a fake so xUnit can exercise the orchestrator without
        /// touching KSP statics.
        /// </summary>
        internal static void Tick(double currentUT, IRouteRuntimeEnvironment env)
        {
            if (env == null)
            {
                ParsekLog.Warn(Tag, "Tick: null env — skipped");
                return;
            }

            // Dormant-routes extension: materialize due dormant routes BEFORE
            // the committed-count early return, so a save whose only routes are
            // dormant still materializes at their creation point. No-cost when
            // the dormant list is empty.
            RouteStore.MaterializeDueDormantRoutes(currentUT);

            var routes = RouteStore.CommittedRoutes;
            if (routes == null || routes.Count == 0)
                return;

            // Snapshot the route list to avoid mid-iteration mutation if AddRoute
            // fires (Apply* methods do not call AddRoute today, but the Logistics window's
            // Create Route confirm can still mutate the store between Apply calls).
            int initialCount = routes.Count;
            Route[] snapshot = new Route[initialCount];
            for (int i = 0; i < initialCount; i++)
                snapshot[i] = routes[i];

            // Deterministic tick order (M1, design D8): lower DispatchPriority
            // dispatches first when several routes contend in the same tick;
            // ties fall to NextDispatchUT, then ordinal route id. Sorting the
            // snapshot (not the store) keeps the commit-list order untouched.
            Array.Sort(snapshot, CompareRoutesForTick);
            if (initialCount > 1)
            {
                ParsekLog.VerboseRateLimited(Tag, "route-tick-order",
                    () => $"Tick order: [{DescribeTickOrder(snapshot)}]");
            }

            // Tick pre-pass, before ANY route runs (so tick order cannot let a
            // competing route act on the stale state first): rebuild the cargo
            // escrow of in-flight multi-stop cycles that a scene switch or reload
            // dropped.
            ReEstablishInFlightCycleEscrow(snapshot, currentUT, env);

            int tickedRoutes = 0;
            int dispatched = 0;
            int transitioned = 0;
            int skipped = 0;
            int errored = 0;

            for (int i = 0; i < snapshot.Length; i++)
            {
                Route route = snapshot[i];
                if (route == null) continue;
                tickedRoutes++;

                try
                {
                    ProcessOneRoute(route, currentUT, env,
                        ref dispatched, ref transitioned, ref skipped);
                }
                catch (Exception ex)
                {
                    errored++;
                    ParsekLog.Error(Tag,
                        $"Tick: route {ShortIdForLog(route)} threw {ex.GetType().Name}: {ex.Message}");
                }
            }

            ParsekLog.VerboseRateLimited(Tag, "route-tick",
                $"Tick: ut={currentUT.ToString("R", IC)} " +
                $"routes={tickedRoutes.ToString(IC)} " +
                $"dispatched={dispatched.ToString(IC)} " +
                $"transitioned={transitioned.ToString(IC)} " +
                $"skipped={skipped.ToString(IC)} " +
                $"errored={errored.ToString(IC)}");
        }

        /// <summary>
        /// Deterministic per-tick processing order (M1, design D8): ascending
        /// <see cref="Route.DispatchPriority"/> (lower value dispatches first),
        /// then <see cref="Route.NextDispatchUT"/>, then ordinal
        /// <see cref="Route.Id"/>. Null routes sort last. TOTALITY: the double
        /// mid-key MUST compare via <see cref="double.CompareTo(double)"/>, never
        /// relational operators: a NaN <c>NextDispatchUT</c> under relational
        /// compares makes the comparator intransitive and
        /// <see cref="Array.Sort(Array)"/> throws on intransitive comparators;
        /// <c>CompareTo</c> totally orders NaN below every other value. The
        /// unique ordinal-id final key makes <c>Array.Sort</c>'s instability moot.
        /// </summary>
        internal static int CompareRoutesForTick(Route a, Route b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return 1;
            if (b == null) return -1;

            int byPriority = a.DispatchPriority.CompareTo(b.DispatchPriority);
            if (byPriority != 0) return byPriority;

            int byNextDispatchUT = a.NextDispatchUT.CompareTo(b.NextDispatchUT);
            if (byNextDispatchUT != 0) return byNextDispatchUT;

            return string.CompareOrdinal(a.Id, b.Id);
        }

        // Formats the sorted per-tick snapshot as "id:prio,id:prio,..." for the
        // route-tick-order breadcrumb (bounded by the committed-route count, built
        // only when the rate limiter actually emits via the factory overload).
        private static string DescribeTickOrder(Route[] ordered)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < ordered.Length; i++)
            {
                if (i > 0) sb.Append(',');
                Route route = ordered[i];
                if (route == null)
                {
                    sb.Append("<null>");
                    continue;
                }
                sb.Append(ShortIdForLog(route))
                  .Append(':')
                  .Append(route.DispatchPriority.ToString(IC));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Round-trip linking alternation advance (M4c Phase C1, plan D12 / OQ8).
        /// Called at the moment a chain-linked route's cycle DISPATCHES (the
        /// once-per-cycle dispatch emit), gated on a non-null
        /// <see cref="Route.LinkedRouteId"/>. Sets
        /// <see cref="Route.LastConsumedPartnerCycle"/> to the partner's CURRENT
        /// <see cref="Route.CompletedCycles"/> so the partner gate
        /// (<see cref="RouteDispatchEvaluator.PartnerConstraintSatisfied"/>) holds
        /// this route until the partner completes ANOTHER cycle (strict A-&gt;B-&gt;A
        /// alternation). The invariant: after this route dispatches consuming
        /// partner-completion <c>K</c>, the route holds until the partner reaches
        /// completion <c>K+1</c>.
        ///
        /// <para>An unlinked route, an unresolved partner, or a non-advancing
        /// (Paused / Broken) partner is a no-op - those are the same routes the
        /// partner gate bypasses, and a no-op here keeps the cursor at its
        /// already-correct value. Idempotent / monotone: it only ever assigns the
        /// partner's current completion count, never decrements.</para>
        /// </summary>
        internal static void AdvancePartnerAlternationOnDispatch(Route route, double currentUT)
        {
            if (route == null || string.IsNullOrEmpty(route.LinkedRouteId))
                return;
            if (!RouteStore.TryGetRoute(route.LinkedRouteId, out Route partner) || partner == null)
                return;

            int prev = route.LastConsumedPartnerCycle;
            if (route.LastConsumedPartnerCycle == partner.CompletedCycles)
                return; // already consumed this partner cycle (e.g. seed re-dispatch before partner advances)

            route.LastConsumedPartnerCycle = partner.CompletedCycles;
            ParsekLog.Verbose(Tag,
                $"PartnerGate: route {ShortIdForLog(route)} dispatched - consumed partner={ShortIdForLog(partner)} " +
                $"cycle lastConsumed {prev.ToString(IC)} -> {route.LastConsumedPartnerCycle.ToString(IC)} " +
                $"at ut={currentUT.ToString("R", IC)}");
        }

        /// <summary>
        /// Per-route catch-up loop. Re-evaluates the route until a terminal
        /// outcome lands (Skip / Dispatch / Wait* / EndpointLost /
        /// InTransitComplete). Dispatch transitions the route to InTransit and
        /// breaks immediately — an InTransit route cannot dispatch again in
        /// the same tick. The loop body is currently bounded by the evaluator
        /// always returning a terminal outcome on a single pass; the
        /// <see cref="MaxCatchUpCyclesPerTick"/> cap is defensive for future
        /// synodic-window dispatch that may legitimately fire multiple cycles
        /// per tick.
        /// </summary>
        private static void ProcessOneRoute(
            Route route, double currentUT, IRouteRuntimeEnvironment env,
            ref int dispatched, ref int transitioned, ref int skipped)
        {
            // ============================================================
            // Loop-route branch (plan Phase 4). Every v0 route is a loop-route
            // (IsLoopRoute true whenever a backing-mission tree is set). The
            // loop-clock crossing detector owns the dispatch phase, so a
            // loop-route NEVER reaches the legacy Dispatch / InTransit /
            // InTransitComplete state machine or the PendingDeliveryUT fire gate
            // below. A confirmed crossing emits the FULL per-cycle transaction
            // (origin debit + delivery) under one cycleId via EmitLoopCycle.
            // ============================================================
            if (route.IsLoopRoute)
            {
                ProcessLoopRoute(route, currentUT, env,
                    ref dispatched, ref transitioned, ref skipped);
                return;
            }

            // Pre-evaluator delivery hook (item 6 Phase B). When a pending
            // delivery boundary has come due, apply the delivery FIRST and then
            // fall through so the dispatch evaluator can fire the next cycle in
            // the same tick if <c>NextDispatchUT</c> is also due. Status gates:
            // Paused and EndpointLost routes skip delivery entirely — applying
            // a delivery to a paused or aborted route would silently move
            // cargo the player did not authorize.
            if (route.PendingDeliveryUT.HasValue
                && route.PendingDeliveryUT.Value <= currentUT
                && route.Status != RouteStatus.Paused
                && route.Status != RouteStatus.EndpointLost)
            {
                ApplyDelivery(route, currentUT, env);
                transitioned++;
                // Intentionally do NOT return — fall through to the evaluator
                // loop so a cycle whose <c>NextDispatchUT</c> is also due can
                // dispatch the next cycle in the same tick. The
                // <see cref="MaxCatchUpCyclesPerTick"/> cap below bounds the
                // catch-up work if delivery + dispatch repeatedly land in the
                // same tick (e.g. long unfocused warp).
            }

            // CS0162 suppression: every switch branch below `return`s on the
            // first iteration today, so the loop increment is currently
            // unreachable. The loop body is the forward-looking shape for
            // future synodic / catch-up dispatch that legitimately fires
            // multiple cycles per tick (see XML doc on MaxCatchUpCyclesPerTick).
#pragma warning disable CS0162
            for (int iter = 0; iter < MaxCatchUpCyclesPerTick; iter++)
            {
                RouteDispatchDecision decision = RouteDispatchEvaluator.EvaluateRoute(route, currentUT, env);

                switch (decision.Outcome)
                {
                    case RouteDispatchOutcome.Skip:
                        skipped++;
                        return;

                    case RouteDispatchOutcome.Dispatch:
                        ApplyDispatch(route, currentUT, env);
                        dispatched++;
                        transitioned++;
                        // Route is now InTransit — cannot dispatch again until
                        // arrival. Returning here is the v0 contract.
                        return;

                    case RouteDispatchOutcome.WaitResources:
                    case RouteDispatchOutcome.WaitDestinationFull:
                        ApplyWait(route, currentUT, decision);
                        transitioned++;
                        return;

                    case RouteDispatchOutcome.EndpointLost:
                        ApplyEndpointLost(route, currentUT, decision);
                        transitioned++;
                        return;

                    case RouteDispatchOutcome.InTransitComplete:
                        ApplyInTransitComplete(route, currentUT);
                        transitioned++;
                        return;

                    default:
                        ParsekLog.Warn(Tag,
                            $"ProcessOneRoute: unknown outcome {decision.Outcome} for route {ShortIdForLog(route)} — skipping");
                        return;
                }
            }

            // Forward-looking defensive guard. Today the evaluator always returns
            // a terminal outcome on the first call so this branch is unreachable
            // from production; once synodic / catch-up dispatch lands it becomes
            // the cap for legitimate multi-cycle ticks.
            ParsekLog.Warn(Tag,
                $"ProcessOneRoute: route {ShortIdForLog(route)} hit max catch-up iter " +
                $"({MaxCatchUpCyclesPerTick.ToString(IC)}), aborting per-route loop");
#pragma warning restore CS0162
        }

        // ==================================================================
        // Loop-route path (plan Phase 4)
        // ==================================================================

        /// <summary>
        /// Test seam for the route's backing-mission <see cref="GhostPlaybackLogic.LoopUnit"/>.
        /// Production leaves this null and <see cref="ProcessLoopRoute"/> builds
        /// the unit from live KSP state (<see cref="RecordingStore"/> + the LOCKED
        /// <c>MissionLoopUnitBuilder.TryBuildLoopUnitForSelection</c> entry point,
        /// behind the M-MIS-11 signature-gated per-route cache); xUnit assigns a
        /// fake that returns a directly-constructed
        /// <see cref="GhostPlaybackLogic.LoopUnit"/> so the crossing/fire logic is
        /// exercised without Planetarium / RecordingStore. When the seam is set,
        /// the cache is BYPASSED entirely (no route cache fields are read or
        /// written).
        /// A null return means the route has no resolvable loop unit this tick
        /// (no committed members, or the build collapsed), and the loop path
        /// skips it.
        /// </summary>
        internal static System.Func<Route, double, GhostPlaybackLogic.LoopUnit?> LoopUnitResolverForTesting;

        // ==================================================================
        // M5 - window-basis transitions (D6)
        // ==================================================================

        /// <summary>
        /// (M5 D6, diagnostic) Per-route count of basis transitions this session.
        /// Runtime-only: a builder verdict thrashing across ticks (engage /
        /// decline alternating) is bounded by the M-MIS-11 signature-gated build
        /// cache and the idempotent re-baselines, but more than 2 transitions in
        /// one session for one route is suspicious enough to Warn (risk register
        /// "builder verdict thrash").
        /// </summary>
        private static readonly Dictionary<string, int> windowBasisTransitionCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// Test seam for the delivery half of <see cref="EmitLoopCycle"/> (and the
        /// legacy <see cref="ApplyDelivery"/> path is unaffected — only the loop
        /// path consults this). Production leaves it null so
        /// <see cref="EmitLoopCycle"/> calls <see cref="ApplyDelivery"/> verbatim
        /// (which needs a live <c>Vessel</c>). xUnit assigns a fake that emits a
        /// <see cref="RouteEventType.RouteCargoDelivered"/> row + bumps
        /// <c>CompletedCycles</c> so the three-row full-cycle fire is verifiable
        /// without Planetarium / Vessel / Funding statics.
        /// </summary>
        internal static System.Action<Route, double, IRouteRuntimeEnvironment> DeliveryApplierForTesting;

        /// <summary>
        /// (M4a A3) Test seam consulted INSIDE the real <see cref="ApplyDelivery"/>,
        /// AFTER its per-window idempotency guard (STEP 1,
        /// <see cref="IsDeliveryAlreadyLogged"/>) but BEFORE the live-Vessel
        /// resolution. Production leaves it null (the live delivery path runs). A
        /// multi-window xUnit test assigns a fake that emits a
        /// <see cref="RouteEventType.RouteCargoDelivered"/> row for the passed
        /// (cycleId, stopIndex) so window 2's delivery is genuinely emitted through
        /// the REAL guard - a window-2-suppressed regression goes RED instead of
        /// false-green (the failure mode the plain <see cref="DeliveryApplierForTesting"/>
        /// fake masks by short-circuiting BEFORE the guard). The CompletedCycles
        /// bump + pending clear are handled by the caller after this returns; the
        /// fake must NOT bump.
        /// </summary>
        internal static System.Action<Route, double, IRouteRuntimeEnvironment, string, int, bool>
            DeliveryRowEmitterForTesting;

        /// <summary>
        /// Test seam for the physical origin debit half of
        /// <see cref="EmitDispatchDebit"/> (M1, design D12). Production leaves
        /// it null so the loop path calls <see cref="ApplyOriginDebit"/>
        /// verbatim (which needs a live <c>Vessel</c>); xUnit assigns a fake
        /// that returns a hand-built <see cref="OriginDebitOutcome"/> so the
        /// debited-row population (actuals, requested-on-shortfall, origin
        /// pid) is verifiable without Planetarium / Vessel statics. Only
        /// consulted on the loop path for non-KSC origins - the legacy path
        /// and KSC origins never reach it (design D11).
        /// </summary>
        internal static System.Func<Route, double, IRouteRuntimeEnvironment, OriginDebitOutcome> OriginDebitApplierForTesting;

        /// <summary>
        /// Production physical origin debit (M1; same signature as the
        /// <see cref="OriginDebitApplierForTesting"/> seam). Resolves the live
        /// origin vessel via the env, captures the loaded gate ONCE, plans the
        /// per-resource removal via <see cref="RouteOriginDebitPlanner"/> over
        /// a <see cref="LiveOriginCargoProbe"/>, applies it via
        /// <see cref="LiveOriginDebitWriters"/>, and returns the actuals /
        /// requested-on-shortfall / origin pid for the debited row.
        ///
        /// <para><b>Unresolved rule (D12):</b> a resolution that returns
        /// <c>false</c> OR returns <c>true</c> with a null vessel counts as
        /// UNRESOLVED: Warn, zero actuals, FULL requested manifest (honest
        /// bookkeeping - the eligibility gate passed this tick, so this is a
        /// one-tick race, mirroring the delivery side's
        /// endpoint-lost-at-delivery handling).</para>
        /// </summary>
        /// <summary>
        /// Shortfall tolerance for writer-accumulated actuals (mirrors
        /// RouteAnalysisEngine.ResourceEpsilon). Multi-tank drains sum
        /// independently rounded per-tank deltas, so exact comparison
        /// against the planned Required flags phantom shortfalls.
        /// </summary>
        private const double OriginDebitShortfallEpsilon = 1e-9;

        /// <summary>
        /// Test seam for the M3 per-window pickup debit
        /// (<see cref="ApplyPickupDebit"/>). Production leaves it null so the
        /// applier resolves a live endpoint vessel and drives the production
        /// probe + writer; xUnit assigns a fake that returns a hand-built
        /// <see cref="OriginDebitOutcome"/> so the Phase 4 two-direction applier
        /// can be exercised without Vessel statics. Mirror of
        /// <see cref="OriginDebitApplierForTesting"/>. The fake receives the
        /// resolved stop <see cref="RouteEndpoint"/> and the per-window pickup
        /// manifest so it can assert on the inputs the production path would
        /// resolve / plan from.
        /// </summary>
        internal static System.Func<RouteEndpoint, Dictionary<string, double>, IRouteRuntimeEnvironment, OriginDebitOutcome> PickupDebitApplierForTesting;

        /// <summary>
        /// Test seam for the M3 per-window INVENTORY pickup debit
        /// (<see cref="ApplyInventoryPickupDebit"/>). Production leaves it null so
        /// the applier resolves a live endpoint vessel and drives the production
        /// inventory probe + remove writer; xUnit assigns a fake that returns a
        /// hand-built <see cref="InventoryPickupOutcome"/> so the Phase 5
        /// two-direction applier + RouteCargoPickedUp inventory manifest can be
        /// exercised without Vessel statics. Mirror of
        /// <see cref="PickupDebitApplierForTesting"/>; receives the resolved stop
        /// endpoint and the per-window inventory pickup manifest.
        /// </summary>
        internal static System.Func<RouteEndpoint, List<InventoryPayloadItem>, IRouteRuntimeEnvironment, InventoryPickupOutcome> InventoryPickupApplierForTesting;

        /// <summary>A linked-route wait's held detail: this prefix plus the partner route's id.</summary>
        internal const string HeldPartnerDetailPrefix = "partner:";

        /// <summary>
        /// Status reason stamped on the Active -&gt; Paused transition (and on the
        /// <see cref="RouteEventType.RoutePaused"/> lifecycle marker) when an armed
        /// <see cref="Route.PauseAfterCurrentCycle"/> is honored on a BLOCKED cycle.
        /// Sibling of the delivered path's <c>delivered-then-paused</c> /
        /// <c>delivered-partial-then-paused</c> and the replay path's
        /// <c>delivered-replay-then-paused</c>.
        /// </summary>
        internal const string BlockedThenPausedReason = "blocked-then-paused";

        /// <summary>
        /// Status reason stamped when an armed <see cref="Route.PauseAfterCurrentCycle"/>
        /// is honored on a cycle that DELIVERED in full. Emitted from two sites that
        /// must never both fire for one cycle: the single-stop / legacy delivered tail
        /// in <see cref="ApplyDeliveryFromPlan"/>, and the multi-stop cycle-complete
        /// tail in <see cref="TryHonorArmedPauseOnCompletedCycle"/>.
        /// </summary>
        internal const string DeliveredThenPausedReason = "delivered-then-paused";

        /// <summary>
        /// <see cref="DeliveredThenPausedReason"/>'s PARTIAL sibling (the cycle
        /// delivered, but at least one window fell short and the remainder is lost).
        /// </summary>
        internal const string DeliveredPartialThenPausedReason = "delivered-partial-then-paused";

        /// <summary>
        /// Status reason stamped when the armed pause is honored on the EVENT-LOG REPLAY
        /// backstop (the delivered row was already in the route event log; nothing new was
        /// emitted, only the pause marker the crash lost).
        /// </summary>
        internal const string DeliveredReplayThenPausedReason = "delivered-replay-then-paused";

        // ==================================================================
        // Helpers
        // ==================================================================

        /// <summary>
        /// Defensive <see cref="EffectiveState.ComputeERS"/> wrapper. ComputeERS can throw
        /// during early KSP load if the scenario module is not yet published;
        /// the orchestrator survives such ticks by treating the failure as
        /// "no recordings available" and emitting a Verbose breadcrumb.
        /// </summary>
        internal static IReadOnlyList<Recording> SafeComputeErs()
        {
            try
            {
                return EffectiveState.ComputeERS();
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"SafeComputeErs: ComputeERS threw {ex.GetType().Name}: {ex.Message}; treating as empty");
                return null;
            }
        }

        // ==================================================================
        // M3 direction helpers (plan Phase 4 D6)
        // ==================================================================

        /// <summary>
        /// True when the route's resolved cycle stop carries a DELIVERY manifest
        /// (resource or inventory) - i.e. cargo flows FROM the transport ONTO the
        /// endpoint. A pure-PICKUP route has no delivery manifest, so the delivery
        /// half of <see cref="EmitLoopCycle"/> must be SKIPPED (no
        /// <c>RouteCargoDelivered</c> row, D6): firing <see cref="ApplyDelivery"/>
        /// on an empty plan would otherwise emit a delivered row even with nothing
        /// to deliver, masking the pickup-only replay hole the OQ4 re-key fixes.
        /// Reads the same stop <see cref="ApplyDelivery"/> resolves (PendingStopIndex
        /// fallback 0). Pure / internal for direct testing.
        /// </summary>
        internal static bool RouteHasDeliveryManifest(Route route)
        {
            RouteStop stop = ResolveCycleStop(route);
            if (stop == null) return false;
            bool hasResource = stop.DeliveryManifest != null && stop.DeliveryManifest.Count > 0;
            bool hasInventory = stop.InventoryDeliveryManifest != null && stop.InventoryDeliveryManifest.Count > 0;
            return hasResource || hasInventory;
        }

        /// <summary>
        /// True when the route's resolved cycle stop carries a PICKUP manifest -
        /// resource (M3 Phase 2/4) OR inventory (M3 Phase 5). When true, the pickup
        /// half of <see cref="EmitLoopCycle"/> resolves the endpoint and physically
        /// debits the witnessed load term in BOTH dimensions (D6/D7). Pure /
        /// internal for direct testing.
        /// </summary>
        internal static bool RouteHasPickupManifest(Route route)
        {
            RouteStop stop = ResolveCycleStop(route);
            if (stop == null) return false;
            bool hasResource = stop.PickupManifest != null && stop.PickupManifest.Count > 0;
            bool hasInventory = stop.InventoryPickupManifest != null && stop.InventoryPickupManifest.Count > 0;
            return hasResource || hasInventory;
        }

        /// <summary>
        /// Resolves the single cycle stop the loop crossing fires against - the
        /// SAME stop <see cref="ApplyDelivery"/> reads (PendingStopIndex, falling
        /// back to 0 when &lt; 0). v0 routes are single-stop. Returns null when
        /// the route has no stops or the index is out of range.
        /// </summary>
        private static RouteStop ResolveCycleStop(Route route)
        {
            if (route == null || route.Stops == null || route.Stops.Count == 0)
                return null;
            int stopIndex = route.PendingStopIndex >= 0 ? route.PendingStopIndex : 0;
            if (stopIndex < 0 || stopIndex >= route.Stops.Count)
                return null;
            return route.Stops[stopIndex];
        }

        /// <summary>
        /// (M4a A3 / OQ3) Resets EVERY stop's per-window fire sub-gate
        /// (<see cref="RouteStop.LastFiredCycleIndex"/>) back to -1 ("no cycle fired
        /// yet"). Called at the same reset sites as the route-level
        /// <see cref="Route.LastObservedLoopCycleIndex"/> cursor:
        /// <see cref="TryActivate"/> (Paused -> Active) and
        /// <see cref="RouteCadence.ApplyMultiplier"/> (cadence rebase). A single-stop
        /// route leaves its (single) stop's default -1 untouched in practice; the
        /// reset is idempotent there. Keeping it a shared helper means a multi-stop
        /// route's later windows are never stalled (cadence raised -> smaller cycle
        /// index never exceeds a stale per-stop value) or jumped (cadence lowered)
        /// after a rebase / re-activate.
        /// </summary>
        internal static void ResetStopFireState(Route route)
        {
            ResetStopFireStateReturningChanged(route);
        }

        /// <summary>
        /// <see cref="ResetStopFireState"/> variant reporting whether any stop's
        /// sub-gate actually moved, for batch counting at the rewind-reconcile
        /// site (<c>RouteRewindClassifier.ResetCycleStateForRewind</c>).
        /// </summary>
        internal static bool ResetStopFireStateReturningChanged(Route route)
        {
            if (route == null || route.Stops == null)
                return false;
            bool changed = false;
            for (int i = 0; i < route.Stops.Count; i++)
            {
                if (route.Stops[i] != null && route.Stops[i].LastFiredCycleIndex != -1)
                {
                    route.Stops[i].LastFiredCycleIndex = -1;
                    changed = true;
                }
            }
            return changed;
        }

        private static Dictionary<string, double> CloneManifest(Dictionary<string, double> source)
        {
            if (source == null) return null;
            return new Dictionary<string, double>(source);
        }

        /// <summary>
        /// Clone keeping positive entries only - the origin-debit gate and
        /// planner skip non-positive manifest entries, so row manifests
        /// built from CostManifest on the unresolved path must match.
        /// </summary>
        private static Dictionary<string, double> ClonePositiveManifest(Dictionary<string, double> source)
        {
            if (source == null) return null;
            var clone = new Dictionary<string, double>(source.Count, StringComparer.Ordinal);
            foreach (var kv in source)
            {
                if (kv.Value > 0.0)
                    clone[kv.Key] = kv.Value;
            }
            return clone;
        }

        /// <summary>
        /// Deep-clone of a witnessed inventory pickup manifest keeping
        /// positive-quantity items only (the unresolved path's
        /// requested-on-shortfall manifest, mirror of
        /// <see cref="ClonePositiveManifest"/>). Items carry mutable
        /// StoredPartSnapshot ConfigNodes, so each is deep-cloned. Null in -> null
        /// out.
        /// </summary>
        private static List<InventoryPayloadItem> ClonePositiveInventoryManifest(
            List<InventoryPayloadItem> source)
        {
            if (source == null) return null;
            List<InventoryPayloadItem> clone = null;
            for (int i = 0; i < source.Count; i++)
            {
                InventoryPayloadItem item = source[i];
                if (item == null || item.Quantity <= 0 || string.IsNullOrEmpty(item.IdentityHash))
                    continue;
                if (clone == null)
                    clone = new List<InventoryPayloadItem>(source.Count);
                clone.Add(item.DeepClone());
            }
            return clone;
        }

        private static string ShortIdForLog(Route route)
        {
            return RouteIds.Short(route);
        }
    }
}
