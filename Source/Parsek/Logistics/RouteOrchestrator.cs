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
    internal static class RouteOrchestrator
    {
        /// <summary>Wall-clock cadence at which the orchestrator advances routes.</summary>
        internal const double TickIntervalSec = 1.0;

        /// <summary>
        /// Retry backoff for transient wait states (resources, funds, capacity,
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
        /// per-cycle eligibility gates (ERS, funds, resources, endpoint
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
                && route.Status != RouteStatus.WaitingForFunds
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

            // Un-pause so the dispatch evaluator can fire on the next tick.
            if (route.Status == RouteStatus.Paused)
                route.TransitionTo(RouteStatus.Active, "send-once-arm");

            // Bring scheduling forward so the interval component of the
            // auto-cycle wait is skipped. The per-cycle conditions (funds,
            // resources, endpoint, alignment) are evaluated by the dispatch
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
            route.NextEligibilityCheckUT = null;
            if (route.NextDispatchUT < currentUT)
                route.NextDispatchUT = currentUT;

            // Loop-clock reset discipline (plan Phase 4 task 5). Activating a
            // loop-route restarts cycle observation: -1 means "no cycle observed
            // yet", so the FIRST post-activate crossing fires. The field persists
            // through the codec so a save/reload mid-cycle does NOT double-fire
            // (ELS is the backstop). LoopAnchorUT capture-on-activate (plan Phase 5
            // task 1: "set route.LoopAnchorUT on activate") is set here for the
            // Paused->Activate path; RouteBuilder seeds it for create-Active. The
            // value is diagnostic only: the loop builder floors the anchor to
            // spanEnd, so the route does NOT own render phase (the crossing detector
            // + LastObservedLoopCycleIndex do).
            long prevObserved = route.LastObservedLoopCycleIndex;
            double prevAnchor = route.LoopAnchorUT;
            if (route.IsLoopRoute)
            {
                route.LastObservedLoopCycleIndex = -1;
                route.LoopAnchorUT = currentUT;
            }

            // M6 hold reasons: activation resets loop observation, so a
            // prior-session hold must not present as current. (Pause keeps the
            // hold - it answers "why wasn't this delivering".)
            route.ClearHold("player-activate");

            route.TransitionTo(RouteStatus.Active, "player-activate");
            ParsekLog.Info(Tag,
                $"TryActivate: route={ShortIdForLog(route)} now Active " +
                $"nextDispatchUT={route.NextDispatchUT.ToString("R", IC)} " +
                $"loopRoute={(route.IsLoopRoute ? "1" : "0")} " +
                $"lastObservedLoopCycleIndex {prevObserved.ToString(IC)}->{route.LastObservedLoopCycleIndex.ToString(IC)} " +
                $"loopAnchorUT {prevAnchor.ToString("R", IC)}->{route.LoopAnchorUT.ToString("R", IC)}");
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
            // Production entry: resolve the live UT + env so the final-owed recovery
            // credit can be flushed on the immediate-pause transition (section 5.4).
            // Resolve defensively: a degenerate environment (early load, or an
            // off-Unity context) must NOT block the pause. If the live values cannot
            // be obtained, pass a null env / -1 UT and the flush no-ops safely
            // (EmitPendingRecoveryCredit fails the Career gate on a null env and
            // clears any stale pending marker without emitting).
            double ut = -1.0;
            IRouteRuntimeEnvironment env = null;
            try
            {
                ut = Planetarium.GetUniversalTime();
                env = new LiveRouteRuntimeEnvironment();
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"TryPause: live UT/env resolution threw {ex.GetType().Name}: {ex.Message}; " +
                    "pausing without a recovery-credit flush");
            }
            return TryPause(route, ut, env);
        }

        /// <summary>
        /// Env-injected pause used by unit tests, and the implementation the
        /// parameterless <see cref="TryPause(Route)"/> delegates to with live
        /// values. The <paramref name="currentUT"/> / <paramref name="env"/> are
        /// threaded so the IMMEDIATE-pause transition (section 5.4) can flush the
        /// route's last dispatched cycle's owed recovery credit before the route
        /// goes quiet (a loop-route that stops crossing never reaches its "next
        /// crossing", so the deferred credit would otherwise be stranded forever).
        /// The InTransit pause-after-cycle path does NOT flush here: that cycle's
        /// credit is flushed at the armed-pause transition in
        /// <see cref="ApplyDeliveryFromPlan"/> instead.
        /// </summary>
        internal static bool TryPause(Route route, double currentUT, IRouteRuntimeEnvironment env)
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
                ParsekLog.Info(Tag,
                    $"TryPause: route={ShortIdForLog(route)} InTransit — armed PauseAfterCurrentCycle " +
                    "(current cycle finishes, then route pauses)");
                return true;
            }

            // Flush the final owed recovery credit before the route stops crossing
            // (section 5.4). Idempotent via guard 3; no-ops on the Career-KSC gate /
            // zero-recovery branch if the route owes nothing.
            EmitPendingRecoveryCredit(route, currentUT, env);

            route.PauseAfterCurrentCycle = false;
            route.TransitionTo(RouteStatus.Paused, "player-pause");
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

            var routes = RouteStore.CommittedRoutes;
            if (routes == null || routes.Count == 0)
                return;

            // Snapshot the route list to avoid mid-iteration mutation if AddRoute
            // fires (Apply* methods do not call AddRoute today, but a user-driven
            // RouteCreationDialog can still mutate the store between Apply calls).
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
            // (origin/funds debit + delivery) under one cycleId via EmitLoopCycle.
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
            // a delivery to a paused or aborted route would silently charge
            // funds the player did not authorize.
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
                    case RouteDispatchOutcome.WaitFunds:
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
        /// the unit from live KSP state (<see cref="RecordingStore"/> +
        /// <see cref="RouteGhostDriverSelector"/> + the LOCKED
        /// <c>MissionLoopUnitBuilder.Build</c>); xUnit assigns a fake that returns
        /// a directly-constructed <see cref="GhostPlaybackLogic.LoopUnit"/> so the
        /// crossing/fire logic is exercised without Planetarium / RecordingStore.
        /// A null return means the route has no resolvable loop unit this tick
        /// (no committed members, or the build collapsed), and the loop path
        /// skips it.
        /// </summary>
        internal static System.Func<Route, double, GhostPlaybackLogic.LoopUnit?> LoopUnitResolverForTesting;

        /// <summary>
        /// Per-tick loop-route processing (plan Phase 4). Builds (or resolves)
        /// the route's backing-mission <see cref="GhostPlaybackLogic.LoopUnit"/>,
        /// asks <see cref="RouteLoopClock"/> for the span-clock state, detects a
        /// crossing (cycleIndex advanced past the last observed, NOT parked in the
        /// inter-cycle tail, and the recorded dock UT falls inside the span), and
        /// on a confirmed crossing runs eligibility then either emits the FULL
        /// cycle (<see cref="EmitLoopCycle"/>) or skips it. In all crossing cases
        /// it SNAPS <c>LastObservedLoopCycleIndex = cycleIndex</c> so a blocked or
        /// warp-jumped cycle never re-fires every tick.
        ///
        /// <para>The legacy Dispatch / InTransit / InTransitComplete state machine
        /// and the <c>PendingDeliveryUT</c> fire gate are NEVER reached for a
        /// loop-route — the crossing detector + <c>LastObservedLoopCycleIndex</c>
        /// own the dispatch phase (design §0.5 decision b).</para>
        /// </summary>
        private static void ProcessLoopRoute(
            Route route, double currentUT, IRouteRuntimeEnvironment env,
            ref int dispatched, ref int transitioned, ref int skipped)
        {
            // Status gate: only ghost-driving routes run the loop clock. Paused /
            // EndpointLost / MissingSourceRecording / SourceChanged render no
            // ghost and dispatch nothing (RouteStatusPolicy.GhostDriving false).
            if (!RouteStatusPolicy.GhostDriving(route.Status))
            {
                skipped++;
                ParsekLog.VerboseRateLimited(Tag, "loop-skip-status-" + route.Id,
                    $"LoopRoute: route {ShortIdForLog(route)} status={route.Status} " +
                    "not ghost-driving — skipped", 5.0);
                return;
            }

            // Resolve the backing-mission loop unit (test seam or live build).
            GhostPlaybackLogic.LoopUnit? unitOpt = ResolveLoopUnit(route, currentUT);
            if (!unitOpt.HasValue)
            {
                skipped++;
                ParsekLog.VerboseRateLimited(Tag, "loop-skip-nounit-" + route.Id,
                    $"LoopRoute: route {ShortIdForLog(route)} no resolvable loop unit — skipped", 5.0);
                return;
            }
            GhostPlaybackLogic.LoopUnit unit = unitOpt.Value;

            // Span-clock state.
            bool ok = RouteLoopClock.TryGetRouteLoopState(
                unit, currentUT, out double loopUT, out long cycleIndex, out bool isInInterCycleTail);
            if (!ok)
            {
                // Before the phase anchor, or a degenerate span — no cycle yet.
                skipped++;
                ParsekLog.VerboseRateLimited(Tag, "loop-clock-early-" + route.Id,
                    $"LoopRoute: route {ShortIdForLog(route)} clock pre-anchor/degenerate — skipped " +
                    RouteLoopClock.DescribeState(unit, currentUT, loopUT, cycleIndex,
                        isInInterCycleTail, route.RecordedDockUT, route.LastObservedLoopCycleIndex),
                    5.0);
                return;
            }

            // Dock-phase crossing detection (DEL-2). A new span-clock cycle alone
            // does NOT fire: the delivery is gated on the loop clock having reached
            // the recorded dock PHASE within the cycle (loopUT >= RecordedDockUT),
            // so one loop-clock crossing == one ghost relaunch == one delivery that
            // fires when the ghost reaches the recorded dock. dockCycleIndex is the
            // cycle whose dock instant has most recently passed (== cycleIndex once
            // the dock phase is reached, cycleIndex-1 while the ghost is still
            // pre-dock); the route snaps LastObservedLoopCycleIndex to THIS, not the
            // raw cycleIndex, so a tick landing early in a fresh cycle does not
            // consume that cycle before its dock.
            bool crossing = RouteLoopClock.IsDockCrossing(
                unit, loopUT, cycleIndex, route.RecordedDockUT,
                route.LastObservedLoopCycleIndex, out long dockCycleIndex);
            if (!crossing)
            {
                skipped++;
                ParsekLog.VerboseRateLimited(Tag, "loop-noncross-" + route.Id,
                    $"LoopRoute: route {ShortIdForLog(route)} no dock crossing " +
                    $"(dockCycleIdx={dockCycleIndex.ToString(IC)}) — " +
                    RouteLoopClock.DescribeState(unit, currentUT, loopUT, cycleIndex,
                        isInInterCycleTail, route.RecordedDockUT, route.LastObservedLoopCycleIndex),
                    5.0);
                return;
            }

            // Confirmed dock crossing. Verbose-log the fire DECISION (cycleIndex,
            // loopUT, recordedDockUT, dockCycleIndex) before the eligibility / emit
            // branches so the dock-phase gate is auditable in the log.
            ParsekLog.Verbose(Tag,
                $"LoopRoute: route {ShortIdForLog(route)} DOCK CROSSING confirmed " +
                $"dockCycleIdx={dockCycleIndex.ToString(IC)} cycleIdx={cycleIndex.ToString(IC)} " +
                $"loopUT={loopUT.ToString("R", IC)} recordedDockUT={route.RecordedDockUT.ToString("R", IC)} " +
                $"lastObserved={route.LastObservedLoopCycleIndex.ToString(IC)} at ut={currentUT.ToString("R", IC)}");

            // Warp: dockCycleIndex may jump N>1 since the last tick (~1 Hz
            // orchestrator vs fast warp). We fire ONCE per tick and snap
            // LastObservedLoopCycleIndex forward to dockCycleIndex (do NOT replay
            // each skipped cycle). A single warp frame that jumps past the recorded
            // dock still fires exactly once for the highest cycle whose dock instant
            // has passed. ELS (routeId, cycleId) idempotency is the backstop if a
            // save/reload or a double-tick re-fires the same cycleId.
            long jump = dockCycleIndex - route.LastObservedLoopCycleIndex;
            if (jump > 1)
            {
                ParsekLog.Verbose(Tag,
                    $"LoopRoute: route {ShortIdForLog(route)} warp jump={jump.ToString(IC)} " +
                    $"(lastObserved={route.LastObservedLoopCycleIndex.ToString(IC)} -> " +
                    $"dockCycleIndex={dockCycleIndex.ToString(IC)}); firing ONCE and snapping forward");
            }

            // cycleId pins the dispatch+debit+delivered triple under one id
            // (cycle-{Completed+Skipped}, the existing formula). On the PASS path
            // CompletedCycles increments (via ApplyDelivery); on the SKIP path
            // SkippedCycles increments — either way the next cycleId advances so
            // the sequence stays strictly-unique.
            string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles).ToString(IC);

            // Eligibility WITHOUT EvaluateRoute (must-fix #3): the loop path uses
            // the extracted CheckEligibility helper, never EvaluateRoute (which
            // would drive Dispatch->InTransit + the PendingDeliveryUT self-timer).
            RouteDispatchEvaluator.EligibilityResult elig =
                RouteDispatchEvaluator.CheckEligibility(route, currentUT, env);

            if (!elig.Eligible)
            {
                // Recovery-credit deferral across a blocked gap (logistics-recovery-credit
                // section 7): a blocked crossing emits NO credit of its OWN (it did not
                // dispatch), but it IS the "next crossing" for the PRIOR dispatched
                // cycle, so flush that prior cycle's owed credit here too. If the prior
                // cycle did not dispatch, the pending marker is null and this no-ops.
                EmitPendingRecoveryCredit(route, currentUT, env);

                // Blocked cycle: emit NOTHING (no debit, no delivery; the ghost
                // still renders — "world looks busy, transfers nothing"). Bump
                // SkippedCycles and STILL snap the cycle index forward (to the
                // dock-phase cycle, DEL-2) so the blocked cycle does not re-fire
                // every tick.
                route.SkippedCycles += 1;
                route.LastObservedLoopCycleIndex = dockCycleIndex;
                // M6 hold reasons: persist the block verdict so the Logistics
                // window can name it. Zero new computation - the evaluator
                // already produced kind/reason/shortfall for the log line below.
                route.RecordHold(elig.Kind, elig.Reason, elig.Shortfall, currentUT);
                skipped++;
                ParsekLog.Info(Tag,
                    $"LoopRoute: route {ShortIdForLog(route)} cycle={cycleId} " +
                    $"BLOCKED kind={elig.Kind} reason={elig.Reason ?? "<none>"} " +
                    $"shortfall={elig.Shortfall.ToString("R", IC)} — emitted nothing, " +
                    $"snapped lastObserved={dockCycleIndex.ToString(IC)} skippedCycles={route.SkippedCycles.ToString(IC)}");
                return;
            }

            // M6 hold reasons: the crossing is ELIGIBLE, so clear any prior hold
            // NOW, BEFORE EmitLoopCycle - ApplyDelivery can record an
            // EndpointLost hold INSIDE that call (endpoint lost at delivery),
            // and EmitLoopCycle returns true even on that branch, so a
            // post-call clear would erase it. One clear here covers both the
            // fired and the replay-backstop branches below.
            route.ClearHold("crossing-eligible");

            // Confirmed crossing + eligible: emit the FULL cycle (must-fix #1).
            // Returns false on the ELS-replay backstop (nothing emitted) so the
            // log line below distinguishes a real fire from a replay no-op.
            bool emitted = EmitLoopCycle(route, currentUT, env, cycleId);

            // Snap forward in BOTH cases (ELS idempotency is the backstop;
            // CompletedCycles was bumped inside EmitLoopCycle -> ApplyDelivery on
            // the fire path, or by the replay branch on the backstop path). Snap to
            // the dock-phase cycle (DEL-2), NOT the raw span-clock cycleIndex, so a
            // tick that already advanced into a fresh cycle before its dock does not
            // consume that next cycle's delivery.
            route.LastObservedLoopCycleIndex = dockCycleIndex;
            if (emitted)
            {
                dispatched++;
                transitioned++;
                ParsekLog.Info(Tag,
                    $"LoopRoute: route {ShortIdForLog(route)} cycle={cycleId} FIRED full cycle " +
                    $"(dispatch+debit+delivered) at ut={currentUT.ToString("R", IC)}; " +
                    $"snapped lastObserved={dockCycleIndex.ToString(IC)} completedCycles={route.CompletedCycles.ToString(IC)}");
            }
            else
            {
                skipped++;
                ParsekLog.Info(Tag,
                    $"LoopRoute: route {ShortIdForLog(route)} cycle={cycleId} replay backstop " +
                    "(already in ledger) — emitted nothing; " +
                    $"snapped lastObserved={dockCycleIndex.ToString(IC)} completedCycles={route.CompletedCycles.ToString(IC)}");
            }
        }

        /// <summary>
        /// READ-ONLY next-dock-crossing countdown accessor (H1, UI layer). Given a
        /// route and the current UT, returns in <paramref name="seconds"/> the
        /// wall-clock-UT seconds until the route's loop clock next reaches the
        /// recorded dock PHASE (one delivery == one dock crossing). Built for the
        /// Logistics window's "Next delivery" countdown; the window THROTTLES the
        /// call (the <see cref="ResolveLoopUnit"/> build is not free) and never sees
        /// a <see cref="GhostPlaybackLogic.LoopUnit"/> or the raw committed lists, so
        /// the ERS/ELS grep gate stays green with no allowlist change (the only raw
        /// read stays behind <see cref="ResolveLoopUnit"/> in this allowlisted file).
        ///
        /// <para><b>Strictly read-only.</b> Mirrors the
        /// <see cref="ProcessLoopRoute"/> resolve path
        /// (<see cref="ResolveLoopUnit"/>) but mutates NOTHING: it never touches
        /// <see cref="Route.LastObservedLoopCycleIndex"/> or any loop-clock state.
        /// Only <see cref="ProcessLoopRoute"/> advances the dispatch phase.</para>
        ///
        /// <para>Returns <c>false</c> (<paramref name="seconds"/> = 0) when the route
        /// is not ghost-driving (<see cref="RouteStatusPolicy.GhostDriving"/> false:
        /// Paused / EndpointLost / MissingSourceRecording / SourceChanged), has no
        /// resolvable loop unit this tick, carries a non-v0 schedule / loiter cut, or
        /// the recorded dock UT falls outside the unit's span. The window shows a
        /// dash / falls back to its wait-state countdown in those cases.</para>
        /// </summary>
        /// <param name="route">The route to count down for.</param>
        /// <param name="nowUT">Current game UT.</param>
        /// <param name="seconds">Seconds until the next dock crossing (always &gt; 0
        /// on success); 0 on a false return.</param>
        /// <returns>True when a finite next-dock-crossing countdown exists.</returns>
        internal static bool TryComputeSecondsToNextDockCrossing(
            Route route, double nowUT, out double seconds)
        {
            seconds = 0.0;

            if (route == null)
                return false;

            // Status gate: only ghost-driving routes run the loop clock, so only
            // they have a next-crossing. Mirrors ProcessLoopRoute's first gate.
            if (!RouteStatusPolicy.GhostDriving(route.Status))
            {
                ParsekLog.VerboseRateLimited(Tag, "next-cross-" + route.Id,
                    $"NextDockCrossing: route {ShortIdForLog(route)} status={route.Status} " +
                    "not ghost-driving, no countdown", 5.0);
                return false;
            }

            // Resolve the backing-mission loop unit (test seam or live build). The
            // raw committed-list read stays inside ResolveLoopUnit in this
            // allowlisted file.
            GhostPlaybackLogic.LoopUnit? unitOpt = ResolveLoopUnit(route, nowUT);
            if (!unitOpt.HasValue)
            {
                ParsekLog.VerboseRateLimited(Tag, "next-cross-" + route.Id,
                    $"NextDockCrossing: route {ShortIdForLog(route)} no resolvable loop unit, no countdown",
                    5.0);
                return false;
            }
            GhostPlaybackLogic.LoopUnit unit = unitOpt.Value;

            // Pure crossing-to-seconds math (gate-safe; reads only LoopUnit accessors).
            bool ok = RouteLoopClock.TryComputeSecondsToNextDockCrossing(
                unit, nowUT, route.RecordedDockUT, route.LastObservedLoopCycleIndex, out seconds);
            if (!ok)
            {
                ParsekLog.VerboseRateLimited(Tag, "next-cross-" + route.Id,
                    $"NextDockCrossing: route {ShortIdForLog(route)} dock-out-of-span/non-v0 " +
                    $"(dockUT={route.RecordedDockUT.ToString("R", IC)} " +
                    $"spanStart={unit.SpanStartUT.ToString("R", IC)} spanEnd={unit.SpanEndUT.ToString("R", IC)}) " +
                    "no countdown", 5.0);
                seconds = 0.0;
                return false;
            }

            ParsekLog.VerboseRateLimited(Tag, "next-cross-" + route.Id,
                $"NextDockCrossing: route {ShortIdForLog(route)} secondsToNextDock={seconds.ToString("R", IC)} " +
                $"at ut={nowUT.ToString("R", IC)} lastObserved={route.LastObservedLoopCycleIndex.ToString(IC)}",
                5.0);
            return true;
        }

        /// <summary>
        /// Resolves the route's backing-mission loop unit for this tick. Routes
        /// through the <see cref="LoopUnitResolverForTesting"/> seam when set;
        /// otherwise builds it from live KSP state via the same selector / Build
        /// path the host push seams use (<see cref="RouteGhostDriverSelector"/> +
        /// the LOCKED <c>MissionLoopUnitBuilder.Build</c>) over the SAME
        /// <c>RecordingStore.CommittedRecordings</c> / <c>CommittedTrees</c>
        /// snapshot, so member indices align. It also passes the IDENTICAL
        /// phase-lock seams the render path uses
        /// (<c>FlightGlobalsBodyInfo.Instance</c> +
        /// <see cref="ParsekSettings.TransitedBodyRotationMode"/>) so the delivery
        /// clock's <c>CadenceSeconds</c> / <c>PhaseAnchorUT</c> match the rendered
        /// ghost's exactly (DEL-1): a supported looping mission snaps its phase
        /// anchor to the next faithful launch window, and an unsupported config
        /// (cross-parent / rendezvous / no constraint — or, in xUnit, an empty
        /// <c>FlightGlobals</c>) degrades to no phase-lock, byte-identical to the
        /// pre-DEL-1 <c>bodyInfo:null</c> path. For a v0 same-body route this is the
        /// faithful (non-re-aimed) loop. Returns null when no unit owns the route's
        /// members this tick.
        /// </summary>
        private static GhostPlaybackLogic.LoopUnit? ResolveLoopUnit(Route route, double currentUT)
        {
            var resolver = LoopUnitResolverForTesting;
            if (resolver != null)
                return resolver(route, currentUT);

            // Live build. Construct the route's backing Mission, run it through the
            // unchanged builder over the committed snapshot, and extract the single
            // unit. The build gates on Mission.LoopPlayback (set by BuildMission),
            // so a one-element list yields at most one unit.
            Mission mission = RouteBackingMission.BuildMission(route, currentUT);
            if (mission == null)
                return null;

            // [ERS-exempt] member-index alignment with the engine. MissionLoopUnitBuilder
            // returns LoopUnit member indices keyed to the RAW
            // RecordingStore.CommittedRecordings list (the engine's alignment
            // contract). The host push seams (ParsekFlight / ParsekKSC /
            // ParsekTrackingStation DriveMissionLoopUnits — all already allowlisted)
            // build the rendered LoopUnitSet off this SAME raw list; the orchestrator
            // MUST source the identical committed snapshot so its clock-unit member
            // indices match the rendered ones (plan "Highest Residual Risks" #2). A
            // supersede-aware ERS filter would re-index the list and silently point
            // the loop clock at the wrong recording. No ledger read here.
            var committed = RecordingStore.CommittedRecordings;
            var trees = RecordingStore.CommittedTrees;
            double autoLoopIntervalSeconds = ParsekSettings.Current?.autoLoopIntervalSeconds
                                             ?? LoopTiming.DefaultLoopIntervalSeconds;

            // DEL-1: pass the SAME phase-lock seams every render seam uses
            // (ParsekFlight / ParsekKSC / ParsekTrackingStation DriveMissionLoopUnits)
            // so the delivery clock phase-locks to the IDENTICAL CadenceSeconds /
            // PhaseAnchorUT the ghost renders on. FlightGlobalsBodyInfo.Instance is a
            // non-null static singleton; in xUnit (empty FlightGlobals) it degrades to
            // no phase-lock, byte-identical to the pre-DEL-1 bodyInfo:null path, so
            // tests on the LoopUnitResolverForTesting seam are unaffected.
            IBodyInfo bodyInfo = FlightGlobalsBodyInfo.Instance;
            TransitedBodyRotationMode tbrMode = ParsekSettings.Current?.TransitedBodyRotationMode
                                                ?? TransitedBodyRotationMode.Loose;

            // Suppress the pure-derivation diagnostic logs the builder pipeline emits
            // (BuildMissionStructure / ExtractConstraints / Solve / ReaimDiag /
            // MissionLoopUnit / PhaseLock). This resolver runs on EVERY delivery-clock
            // tick (ProcessLoopRoute) and every Logistics-window OnGUI frame
            // (ComputeRouteLegibility -> TryComputeSecondsToNextDockCrossing); under time
            // warp the UT-throttled tick fires many times per real second, so an
            // un-suppressed build floods the log with a static verdict. The LoopUnit
            // computed is byte-identical - only the diagnostic output is gated (mirrors
            // MissionsWindowUI's display-mirror build). These flags gate Verbose/Info plus
            // two pre-existing MissionPeriodicity diagnostic Warns (degenerate-period
            // filter; over-constrained Tier-1 residual); the MissionLoopUnitBuilder.Build
            // owner/member collision Warns stay un-gated. Silencing the MissionPeriodicity
            // Warns here is intentional - at Warn they ignore verbose-off and would flood
            // every tick, and the signature-gated render build (DriveMissionLoopUnits) plus
            // the Missions window still surface them un-suppressed for the same config.
            bool prevStructSuppress = MissionStructureBuilder.SuppressLogging;
            bool prevPeriodicitySuppress = MissionPeriodicity.SuppressLogging;
            bool prevLoopSuppress = MissionLoopUnitBuilder.SuppressLogging;
            MissionStructureBuilder.SuppressLogging = true;
            MissionPeriodicity.SuppressLogging = true;
            MissionLoopUnitBuilder.SuppressLogging = true;
            GhostPlaybackLogic.LoopUnitSet set;
            try
            {
                set = MissionLoopUnitBuilder.Build(
                    new List<Mission> { mission }, trees, committed, autoLoopIntervalSeconds, bodyInfo, tbrMode);
            }
            finally
            {
                MissionStructureBuilder.SuppressLogging = prevStructSuppress;
                MissionPeriodicity.SuppressLogging = prevPeriodicitySuppress;
                MissionLoopUnitBuilder.SuppressLogging = prevLoopSuppress;
            }

            if (set == null || set.Count == 0)
                return null;

            // Single unit — return the first (and only) one.
            foreach (var kv in set.UnitsByOwner)
                return kv.Value;
            return null;
        }

        /// <summary>
        /// Emits the FULL per-cycle transaction for a loop-route crossing under
        /// ONE <paramref name="cycleId"/> (plan Phase 4 must-fix #1): the
        /// EXTRACTED dispatch-debit half (<see cref="EmitDispatchDebit"/> — origin
        /// debit + KSC funds + <c>RouteDispatched</c> + <c>RouteCargoDebited</c> +
        /// sets <c>KscDispatchFundsCost</c>) FOLLOWED BY the delivery half
        /// (<see cref="ApplyDelivery"/> — <c>RouteCargoDelivered</c>), with NO
        /// InTransit state. The ledger row UTs are game-time
        /// <paramref name="currentUT"/>.
        ///
        /// <para><b>Why both halves.</b> Calling <see cref="ApplyDelivery"/> alone
        /// would never debit origin / charge funds and would trip
        /// <c>RouteModule.ProcessCargoDelivered</c>'s out-of-order guard
        /// (DispatchedCycles == 0 -> Warn + return). The dispatch-debit half emits
        /// <c>RouteDispatched</c> FIRST (Sequence 0) so the ledger walker sees the
        /// dispatch before the delivery at the same UT.</para>
        ///
        /// <para><b>cycleId alignment.</b> <see cref="EmitDispatchDebit"/> uses the
        /// passed <paramref name="cycleId"/> verbatim; <see cref="ApplyDelivery"/>
        /// recomputes <c>cycle-{Completed+Skipped}</c> internally, which equals
        /// <paramref name="cycleId"/> because neither half increments those
        /// counters before delivery runs. ApplyDelivery's idempotency guard
        /// (<see cref="IsDeliveryAlreadyInLedger"/>) and its CompletedCycles bump
        /// are reused VERBATIM.</para>
        ///
        /// <para><b>PendingStopIndex.</b> v0 routes are single-stop; ApplyDelivery
        /// reads <c>PendingStopIndex</c> (falling back to 0 when &lt; 0). The loop
        /// path leaves it -1 so ApplyDelivery uses stop 0, then clears it back to
        /// -1 — no InTransit hand-off state is involved.</para>
        ///
        /// <para>Returns <c>true</c> when the full cycle was emitted, <c>false</c>
        /// on the ELS-replay backstop (nothing emitted) so the caller's log line
        /// stays accurate.</para>
        /// </summary>
        internal static bool EmitLoopCycle(
            Route route, double currentUT, IRouteRuntimeEnvironment env, string cycleId)
        {
            // Recovery-credit deferral (logistics-recovery-credit section 5.2/5.3):
            // flush the PRIOR dispatched cycle's owed credit FIRST, at THIS
            // crossing's UT, BEFORE the delivery-keyed replay short-circuit below.
            // The crash-window note (section 5.3) requires this ordering: a crossing
            // that replay-skips its OWN delivery must still flush the PRIOR cycle's
            // owed credit, so the flush cannot sit behind the IsDeliveryAlreadyInLedger
            // return. Idempotent via the credit's own keyed backstop (guard 3).
            EmitPendingRecoveryCredit(route, currentUT, env);

            // ELS idempotency backstop (must-fix #4). LastObservedLoopCycleIndex
            // (persisted via the Phase 1 codec) is the PRIMARY re-fire guard, but a
            // save/reload mid-cycle, a Rewind, or a double-tick can re-present the
            // SAME cycleId. EmitDispatchDebit has no idempotency guard of its own,
            // so without this check a replayed cycle would emit ORPHAN
            // RouteDispatched/RouteCargoDebited rows (ApplyDelivery would then skip
            // the delivery, leaving a partial double-charge). Check (routeId,
            // cycleId) in ELS up front and emit NOTHING on a replay — the caller
            // still snaps LastObservedLoopCycleIndex forward.
            if (IsDeliveryAlreadyInLedger(route.Id, cycleId))
            {
                // Mirror ApplyDelivery's replay branch: bump CompletedCycles so the
                // NEXT cycle's id (cycle-{Completed+Skipped}) advances past this
                // already-delivered one. Without it, the next crossing would
                // recompute the same already-in-ledger cycleId and replay-skip
                // forever (the route would render but never deliver again).
                route.CompletedCycles += 1;
                ParsekLog.Verbose(Tag,
                    $"EmitLoopCycle: route {ShortIdForLog(route)} cycle={cycleId} " +
                    $"already in ledger (replay) — emitting nothing, bumped " +
                    $"completedCycles={route.CompletedCycles.ToString(IC)}");
                return false;
            }

            // Dispatch + debit half (RouteDispatched Sequence 0, RouteCargoDebited
            // Sequence 1, origin/funds debit, sets KscDispatchFundsCost). The loop
            // crossing is the recorded dock phase and sits behind the ELS replay
            // backstop above, so this is the ONLY caller that applies the M1
            // physical origin debit (design D11).
            EmitDispatchDebit(route, currentUT, env, cycleId, applyPhysicalOriginDebit: true);

            // Recovery-credit deferral (logistics-recovery-credit section 5.2): this
            // cycle just dispatched a (potential) Career-KSC charge, so it now OWES a
            // recovery credit to be flushed on the NEXT crossing. Set the pending
            // marker only when this cycle actually dispatched a Career-KSC charge
            // (gotcha G5: no charge -> no credit owed). The prior cycle's credit was
            // already flushed above; this overwrites the cleared marker with this
            // cycle's id. Persisted by RouteCodec (section 5.6) so a save/reload
            // between crossings does not drop the owed credit.
            if (env != null && env.IsCareer && route.IsKscOrigin)
            {
                route.PendingRecoveryCreditCycleId = cycleId;
                route.PendingRecoveryCreditDispatchUT = currentUT;
                ParsekLog.Verbose(Tag,
                    $"EmitLoopCycle: route {ShortIdForLog(route)} armed pending recovery credit " +
                    $"cycle={cycleId} dispatchUT={currentUT.ToString("R", IC)}");
            }

            // Delivery half. ApplyDelivery resolves the live destination vessel,
            // plans + applies the fill, debits Career funds, emits
            // RouteCargoDelivered, and bumps CompletedCycles. It recomputes the
            // SAME cycleId internally (Completed+Skipped unchanged between the two
            // halves). For a ghost-driving loop-route the status is Active, so
            // ApplyDelivery's terminal TransitionTo(Active) is a self-transition
            // (no InTransit involved). Routed through the DeliveryApplierForTesting
            // seam so xUnit can verify the full three-row fire without a live
            // Vessel (production leaves the seam null -> calls ApplyDelivery
            // VERBATIM).
            var deliveryApplier = DeliveryApplierForTesting;
            if (deliveryApplier != null)
                deliveryApplier(route, currentUT, env);
            else
                ApplyDelivery(route, currentUT, env);
            return true;
        }

        /// <summary>
        /// Test seam for the delivery half of <see cref="EmitLoopCycle"/> (and the
        /// legacy <see cref="ApplyDelivery"/> path is unaffected — only the loop
        /// path consults this). Production leaves it null so
        /// <see cref="EmitLoopCycle"/> calls <see cref="ApplyDelivery"/> verbatim
        /// (which needs a live <c>Vessel</c>). xUnit assigns a fake that emits a
        /// <see cref="GameActionType.RouteCargoDelivered"/> row + bumps
        /// <c>CompletedCycles</c> so the three-row full-cycle fire is verifiable
        /// without Planetarium / Vessel / Funding statics.
        /// </summary>
        internal static System.Action<Route, double, IRouteRuntimeEnvironment> DeliveryApplierForTesting;

        /// <summary>
        /// Result of the physical origin debit half (M1, design D12): the
        /// row inputs <see cref="EmitDispatchDebit"/> populates the
        /// <c>RouteCargoDebited</c> action from. Unlike the delivery seam
        /// (which replaces the whole half INCLUDING its row emission), the
        /// debit row is constructed by <see cref="EmitDispatchDebit"/> itself,
        /// so the seam must RETURN the actuals rather than emit.
        /// </summary>
        internal struct OriginDebitOutcome
        {
            /// <summary>Per-resource amounts ACTUALLY removed from the origin; null when nothing was removed (serializes as no manifest).</summary>
            public Dictionary<string, double> ActualDebited;
            /// <summary>Requested amounts for resources whose actual fell short; null on a full debit (design D3).</summary>
            public Dictionary<string, double> RequestedOnShortfall;
            /// <summary>Persistent id of the debited origin vessel; 0 when unresolved.</summary>
            public uint OriginVesselPid;
            /// <summary>True when at least one resource's actual fell short of the required amount.</summary>
            public bool Short;
            /// <summary>True when the origin vessel could not be resolved at apply time (zero actuals, full requested manifest).</summary>
            public bool Unresolved;
        }

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

        private static OriginDebitOutcome ApplyOriginDebit(
            Route route, double currentUT, IRouteRuntimeEnvironment env)
        {
            Vessel originVessel = null;
            string resolveReason = null;
            bool resolved = false;
            try
            {
                resolved = env.TryResolveEndpointVessel(route.Origin, out originVessel, out resolveReason);
            }
            catch (Exception ex)
            {
                resolveReason = $"resolver-threw-{ex.GetType().Name}";
                resolved = false;
            }

            if (!resolved || originVessel == null)
            {
                string reason = resolved ? "resolved-null-vessel" : (resolveReason ?? "unknown");
                ParsekLog.Warn(Tag,
                    $"OriginDebit: route {ShortIdForLog(route)} origin unresolved at debit " +
                    $"(reason={reason}) - emitting requested manifest with zero actuals");
                return new OriginDebitOutcome
                {
                    ActualDebited = null,
                    // Positive entries only, matching the gate
                    // (RouteOriginCargoCheck) and planner skip of <= 0
                    // manifest entries - the short and unresolved paths
                    // must agree on row content.
                    RequestedOnShortfall = ClonePositiveManifest(route.CostManifest),
                    OriginVesselPid = 0u,
                    Short = true,
                    Unresolved = true,
                };
            }

            // Capture the loaded gate ONCE and thread it into both the probe
            // and the writers so the plan and the mutation read from the SAME
            // loaded/unloaded branch (same rationale as the delivery side's
            // destinationIsLoaded, ApplyDelivery STEP 3).
            bool originIsLoaded = originVessel.loaded && !originVessel.packed;
            var probe = new LiveOriginCargoProbe(originVessel, originIsLoaded);
            OriginDebitPlan plan = RouteOriginDebitPlanner.PrepareDebit(route, probe);

            // One plan line per debit (bounded: one resource set per cycle).
            ParsekLog.Info(Tag,
                $"OriginDebit plan: route={ShortIdForLog(route)} ut={currentUT.ToString("R", IC)} " +
                $"resources={(plan.Resources?.Count ?? 0).ToString(IC)} " +
                $"short={(plan.IsShort ? "1" : "0")} " +
                $"path={(originIsLoaded ? "loaded" : "unloaded")} " +
                $"origin={originVessel.vesselName ?? "<none>"} " +
                $"pid={originVessel.persistentId.ToString(IC)}");

            var writers = new LiveOriginDebitWriters(route, originVessel, plan, originIsLoaded);
            Dictionary<string, double> actualManifest = null;
            Dictionary<string, double> requestedManifest = null;
            bool anyShort = false;
            if (plan.Resources != null)
            {
                for (int i = 0; i < plan.Resources.Count; i++)
                {
                    OriginDebitLine line = plan.Resources[i];
                    if (line.Available > 0.0)
                        writers.WriteResourceDebit(line.Name, line.Available);
                    double actual = writers.ReadActualDebited(line.Name);
                    if (actual > 0.0)
                    {
                        if (actualManifest == null)
                            actualManifest = new Dictionary<string, double>(plan.Resources.Count, StringComparer.Ordinal);
                        actualManifest[line.Name] = actual;
                    }
                    // Requested-on-shortfall covers BOTH a short plan (stored <
                    // required at plan time) and a writer-level drift (actual <
                    // planned available) - the row records required for every
                    // resource whose actual fell short (design D3, mirroring
                    // the delivery side's RouteRequestedResourceManifest).
                    // Epsilon, not exact: the writer accumulates per-tank
                    // deltas, so a complete multi-tank debit can land 1 ULP
                    // below Required; an exact compare would persist a bogus
                    // requested manifest + Warn every such cycle.
                    if (actual < line.Required - OriginDebitShortfallEpsilon)
                    {
                        anyShort = true;
                        if (requestedManifest == null)
                            requestedManifest = new Dictionary<string, double>(plan.Resources.Count, StringComparer.Ordinal);
                        requestedManifest[line.Name] = line.Required;
                    }
                }
            }

            return new OriginDebitOutcome
            {
                ActualDebited = actualManifest,
                RequestedOnShortfall = requestedManifest,
                OriginVesselPid = originVessel.persistentId,
                Short = anyShort,
                Unresolved = false,
            };
        }

        // ==================================================================
        // Outcome appliers
        // ==================================================================

        /// <summary>
        /// Dispatch happy-path: compute KSC funds cost, emit
        /// <see cref="GameActionType.RouteDispatched"/> +
        /// <see cref="GameActionType.RouteCargoDebited"/> ledger rows,
        /// transition the route to <see cref="RouteStatus.InTransit"/>, and
        /// advance <c>NextDispatchUT</c>. <c>NextEligibilityCheckUT</c> is
        /// cleared because the route is no longer in a wait state.
        /// </summary>
        private static void ApplyDispatch(Route route, double currentUT, IRouteRuntimeEnvironment env)
        {
            // Cycle id pins the dispatch + debit pair together in the ledger.
            // Format is "cycle-<N>" where N = total cycles ever started for this
            // route (completed + skipped — both increment past cycles even
            // though SkippedCycles is currently informational only).
            //
            // Cycle id formula: cycle-{CompletedCycles + SkippedCycles}. Item-6
            // RouteCargoDelivered must use the SAME formula so dispatch/debit/
            // deliver triple stays aligned per cycle. First cycle is cycle-0
            // (both counters start at 0).
            string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles).ToString(IC);

            // Emit the dispatch + debit pair + the funds debit (shared with the
            // loop-clock path via EmitDispatchDebit). The M1 physical origin
            // debit is LOOP-PATH-ONLY (design D11): this legacy self-timer path
            // fires at cycle start, not the recorded dock phase (spec 19.2.5
            // rule 2), and has no replay backstop, so it keeps the v0 rows
            // byte-identical and never touches origin tanks.
            EmitDispatchDebit(route, currentUT, env, cycleId, applyPhysicalOriginDebit: false);

            // State transition: Active/Wait* → InTransit, advance the next-due
            // UT by one DispatchInterval, clear the retry timer. (Loop-routes do
            // NOT reach this method — their EmitLoopCycle path skips the InTransit
            // self-timer entirely; see ProcessLoopRoute.)
            route.TransitionTo(RouteStatus.InTransit, "dispatched");
            route.CurrentCycleStartUT = currentUT;
            route.NextDispatchUT = currentUT + route.DispatchInterval;
            route.NextEligibilityCheckUT = null;
            // M6 hold reasons: a successful dispatch ends any recorded hold.
            route.ClearHold("dispatched");

            ParsekLog.Info(Tag,
                $"Dispatch: route {ShortIdForLog(route)} cycle={cycleId} " +
                $"ut={currentUT.ToString("R", IC)} " +
                $"careerKsc={(env.IsCareer && route.IsKscOrigin ? "1" : "0")} " +
                $"nextDispatchUT={route.NextDispatchUT.ToString("R", IC)}");
        }

        /// <summary>
        /// EXTRACTED dispatch-debit body shared by the legacy self-timer path
        /// (<see cref="ApplyDispatch"/>) and the loop-clock path
        /// (<see cref="EmitLoopCycle"/>, plan Phase 4 must-fix #1). Computes the
        /// KSC funds cost (Career + KSC only), writes <c>route.KscDispatchFundsCost</c>,
        /// and emits the <see cref="GameActionType.RouteDispatched"/> +
        /// <see cref="GameActionType.RouteCargoDebited"/> ledger pair under the
        /// SAME <paramref name="cycleId"/>. Does NOT mutate route status,
        /// scheduling, or counters — the caller owns those (the legacy path
        /// transitions to InTransit + advances NextDispatchUT; the loop path runs
        /// delivery in the same tick and snaps the cycle index). This is the
        /// origin/funds-debit half that <see cref="ApplyDelivery"/> alone would
        /// SKIP, so the loop path must call BOTH (must-fix #1): calling delivery
        /// alone never debits origin / charges funds and trips
        /// <c>RouteModule.ProcessCargoDelivered</c>'s out-of-order guard
        /// (DispatchedCycles == 0).
        ///
        /// <para><b>Physical origin debit (M1, design D11: LOOP-PATH-ONLY).</b>
        /// When <paramref name="applyPhysicalOriginDebit"/> is <c>true</c> and
        /// the route's origin is non-KSC, the planned cargo is PHYSICALLY
        /// removed from the live origin vessel BEFORE the rows are built (via
        /// the <see cref="OriginDebitApplierForTesting"/> seam or the production
        /// <see cref="ApplyOriginDebit"/>), and the debited row carries the
        /// ACTUAL removed amounts plus requested-on-shortfall and the origin
        /// pid. Only <see cref="EmitLoopCycle"/> passes <c>true</c>: the loop
        /// crossing IS the recorded dock phase (spec 19.2.5 rule 2) and sits
        /// behind the ELS replay backstop. The legacy <see cref="ApplyDispatch"/>
        /// path passes <c>false</c> - it fires at cycle start (not the dock
        /// phase) and has no replay backstop - keeping its rows byte-identical
        /// to v0 (unconditional <c>CostManifest</c> clone, no physical write).
        /// KSC origins never debit physically; funds carry the cost.</para>
        /// </summary>
        internal static void EmitDispatchDebit(
            Route route, double currentUT, IRouteRuntimeEnvironment env, string cycleId,
            bool applyPhysicalOriginDebit)
        {
            // Funds cost: only meaningful for Career + KSC origin. We compute it
            // unconditionally so the persisted KscDispatchFundsCost stays in sync
            // with what the evaluator's funds gate saw, but the emitted action
            // only carries the cost when the actual charge would be non-zero.
            bool isCareerKsc = env.IsCareer && route.IsKscOrigin;
            double computedCost = 0.0;
            if (isCareerKsc)
            {
                computedCost = ComputeDispatchFundsCostForRoute(route);
                // Route.KscDispatchFundsCost is double-typed; preserve full
                // precision here. The ledger-row cast below is the only float
                // truncation, intentional because GameAction defines the field
                // as float.
                route.KscDispatchFundsCost = computedCost;
            }

            // Physical origin debit half (M1, design D11/D12). Runs BEFORE the
            // row construction so the debited row records what was ACTUALLY
            // removed. Routed through the OriginDebitApplierForTesting seam so
            // xUnit can verify the row population without a live Vessel
            // (production leaves the seam null -> ApplyOriginDebit).
            bool physicalDebitApplied = false;
            OriginDebitOutcome originDebit = default(OriginDebitOutcome);
            if (!route.IsKscOrigin)
            {
                if (route.IsHarvestOrigin)
                {
                    // M2 harvest origin (plan D7): no physical origin vessel
                    // exists and the CostManifest is empty by construction, so
                    // there is NOTHING to debit - but the row pair below still
                    // emits for row-shape stability (the empty manifest makes
                    // it a structural no-op: zero funds, no physical write).
                    ParsekLog.Verbose(Tag,
                        $"DispatchDebit: route {ShortIdForLog(route)} cycle={cycleId} " +
                        "harvest origin: physical origin debit skipped (harvested cargo debits nothing)");
                }
                else if (applyPhysicalOriginDebit)
                {
                    var originDebitApplier = OriginDebitApplierForTesting;
                    originDebit = originDebitApplier != null
                        ? originDebitApplier(route, currentUT, env)
                        : ApplyOriginDebit(route, currentUT, env);
                    physicalDebitApplied = true;

                    // D3 clamp-and-warn: the gate passed this tick, so a short
                    // apply is a mid-tick drift. The row records the
                    // actual-vs-requested split; warn so the clamp is visible.
                    // The unresolved case already warned inside the applier.
                    if (originDebit.Short && !originDebit.Unresolved)
                    {
                        ParsekLog.Warn(Tag,
                            $"OriginDebit: route {ShortIdForLog(route)} cycle={cycleId} SHORT at apply - " +
                            "clamped to stored; requested manifest recorded on the debited row " +
                            $"(debitedResources={(originDebit.ActualDebited?.Count ?? 0).ToString(IC)} " +
                            $"requestedResources={(originDebit.RequestedOnShortfall?.Count ?? 0).ToString(IC)})");
                    }
                }
                else
                {
                    // D11: the legacy self-timer path keeps v0 behavior
                    // byte-identical (no physical write, unconditional
                    // CostManifest clone below).
                    ParsekLog.Verbose(Tag,
                        $"DispatchDebit: route {ShortIdForLog(route)} cycle={cycleId} " +
                        "legacy dispatch path: physical origin debit skipped");
                }
            }

            // RouteDispatched — the scheduler-decision marker. No manifest;
            // the debit row carries the resources/funds payload.
            var dispatchedAction = new GameAction
            {
                Type = GameActionType.RouteDispatched,
                UT = currentUT,
                RouteId = route.Id,
                RouteCycleId = cycleId,
                RouteStopIndex = -1,
                Sequence = 0,
            };

            // RouteCargoDebited — the physical/funds debit. Carries the cost
            // manifest (resource amounts) and the KSC funds cost. Sequence=1
            // pins the debit AFTER the dispatched row at the same UT, which
            // matters for any future walker that orders by (UT, Sequence).
            // On the physical-debit path the manifest is the ACTUAL removed
            // amounts (zero actuals serialize as no manifest) plus the sparse
            // requested-on-shortfall manifest and origin pid; on the KSC and
            // legacy paths the v0 shape is preserved byte-identical
            // (unconditional CostManifest clone, no requested manifest, pid 0).
            var debitedAction = new GameAction
            {
                Type = GameActionType.RouteCargoDebited,
                UT = currentUT,
                RouteId = route.Id,
                RouteCycleId = cycleId,
                RouteStopIndex = -1,
                Sequence = 1,
                RouteResourceManifest = physicalDebitApplied
                    ? originDebit.ActualDebited
                    : CloneManifest(route.CostManifest),
                RouteRequestedResourceManifest = physicalDebitApplied
                    ? originDebit.RequestedOnShortfall
                    : null,
                RouteOriginVesselPid = physicalDebitApplied
                    ? originDebit.OriginVesselPid
                    : 0u,
                // RouteKscFundsCost is float-typed on GameAction by design;
                // double precision is preserved on Route.KscDispatchFundsCost
                // for diagnostics.
                RouteKscFundsCost = isCareerKsc ? (float)computedCost : 0f,
            };

            Ledger.AddActions(new[] { dispatchedAction, debitedAction });

            string physicalSuffix = physicalDebitApplied
                ? $" originPid={originDebit.OriginVesselPid.ToString(IC)}" +
                  $" debitedResources={(originDebit.ActualDebited?.Count ?? 0).ToString(IC)}" +
                  $" short={(originDebit.Short ? "1" : "0")}" +
                  $" unresolved={(originDebit.Unresolved ? "1" : "0")}"
                : string.Empty;
            ParsekLog.Info(Tag,
                $"DispatchDebit: route {ShortIdForLog(route)} cycle={cycleId} " +
                $"ut={currentUT.ToString("R", IC)} " +
                $"cost={computedCost.ToString("R", IC)} " +
                $"careerKsc={(isCareerKsc ? "1" : "0")}" + physicalSuffix);
        }

        /// <summary>
        /// Wait-state applier (resources / funds / destination-full). Updates
        /// <c>Status</c> and <c>NextEligibilityCheckUT</c>; per design §10.4
        /// <c>NextDispatchUT</c> is intentionally NOT touched, so a destination-
        /// full route holds its cycle slot until the destination has capacity.
        /// Also persists the hold reason (M6 hold reasons) so the Logistics
        /// window can name it; <paramref name="currentUT"/> stamps the hold age.
        /// </summary>
        private static void ApplyWait(Route route, double currentUT, RouteDispatchDecision decision)
        {
            if (decision.NextStatus.HasValue)
                route.TransitionTo(decision.NextStatus.Value, decision.Reason);

            route.NextEligibilityCheckUT = decision.NewNextEligibilityCheckUT;

            // M6 hold reasons: store the legacy-path verdict verbatim (the
            // prefixed decision token, e.g. "origin-lacks-X"; the formatter is
            // total over both token shapes). Shortfall stays 0 here - the
            // decision carries the number only inside the funds token, and the
            // legacy path is dead for v0 loop routes (accepted degradation).
            route.RecordHold(HoldKindForOutcome(decision.Outcome), decision.Reason, 0.0, currentUT);

            // §10.4: do NOT advance NextDispatchUT for any wait state. The route
            // re-evaluates at NextEligibilityCheckUT and either dispatches at the
            // original cycle slot or remains queued.

            ParsekLog.Info(Tag,
                $"Wait: route {ShortIdForLog(route)} " +
                $"status={(decision.NextStatus.HasValue ? decision.NextStatus.Value.ToString() : "<none>")} " +
                $"reason={decision.Reason ?? "<none>"} " +
                $"retryAt={(decision.NewNextEligibilityCheckUT.HasValue ? decision.NewNextEligibilityCheckUT.Value.ToString("R", IC) : "<none>")}");
        }

        /// <summary>
        /// Maps a legacy-path wait/loss <see cref="RouteDispatchOutcome"/> onto
        /// the <see cref="RouteDispatchEvaluator.EligibilityFailureKind"/> the
        /// hold-reason fields store (M6 hold reasons), so both capture paths
        /// (loop blocked branch and legacy appliers) persist the same kind for
        /// the same underlying failure. Total: non-hold outcomes (Skip /
        /// Dispatch / InTransitComplete) map to None. Pure and internal for
        /// direct testing.
        /// </summary>
        internal static RouteDispatchEvaluator.EligibilityFailureKind HoldKindForOutcome(
            RouteDispatchOutcome outcome)
        {
            switch (outcome)
            {
                case RouteDispatchOutcome.WaitResources:
                    return RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo;
                case RouteDispatchOutcome.WaitFunds:
                    return RouteDispatchEvaluator.EligibilityFailureKind.FundsShort;
                case RouteDispatchOutcome.WaitDestinationFull:
                    return RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull;
                case RouteDispatchOutcome.EndpointLost:
                    return RouteDispatchEvaluator.EligibilityFailureKind.EndpointLost;
                default:
                    return RouteDispatchEvaluator.EligibilityFailureKind.None;
            }
        }

        /// <summary>
        /// Endpoint-lost applier. Transitions status, sets the retry timer, and
        /// emits a <see cref="GameActionType.RouteEndpointLost"/> ledger row so
        /// the timeline records the failure. Design §10.1/§10.2 — endpoint loss
        /// is distinct from a player Pause because it may auto-recover via the
        /// surface-proximity fallback on a later tick.
        /// </summary>
        private static void ApplyEndpointLost(Route route, double currentUT, RouteDispatchDecision decision)
        {
            if (decision.NextStatus.HasValue)
                route.TransitionTo(decision.NextStatus.Value, decision.Reason);

            route.NextEligibilityCheckUT = decision.NewNextEligibilityCheckUT;

            // M6 hold reasons: persist the loss verdict (the resolver token,
            // e.g. "stop-0-no-live-vessels" or "origin-pid-miss...") so the
            // Logistics window can name which endpoint went missing.
            route.RecordHold(
                RouteDispatchEvaluator.EligibilityFailureKind.EndpointLost,
                decision.Reason, 0.0, currentUT);

            var action = new GameAction
            {
                Type = GameActionType.RouteEndpointLost,
                UT = currentUT,
                RouteId = route.Id,
                RouteStopIndex = -1,
                Sequence = 0,
                RouteEndpointReason = decision.Reason,
            };
            Ledger.AddAction(action);

            ParsekLog.Info(Tag,
                $"EndpointLost: route {ShortIdForLog(route)} " +
                $"reason={decision.Reason ?? "<none>"} " +
                $"retryAt={(decision.NewNextEligibilityCheckUT.HasValue ? decision.NewNextEligibilityCheckUT.Value.ToString("R", IC) : "<none>")}");
        }

        /// <summary>
        /// In-transit arrival applier. Sets <c>PendingDeliveryUT</c> + stop
        /// index so the upcoming item-6 delivery code can pick the boundary
        /// up. Status stays <see cref="RouteStatus.InTransit"/>: delivery
        /// completion (and the next-cycle reset) is owned by the delivery
        /// pipeline, not the dispatch orchestrator.
        /// </summary>
        internal static void ApplyInTransitComplete(Route route, double currentUT)
        {
            // The evaluator guards against re-emission via the
            // !route.PendingDeliveryUT.HasValue check, so this applier only
            // runs on the FIRST arrival. Guard defensively anyway in case a
            // future evaluator change relaxes the gate.
            if (route.PendingDeliveryUT.HasValue)
            {
                ParsekLog.Verbose(Tag,
                    $"InTransitComplete: route {ShortIdForLog(route)} already has " +
                    $"PendingDeliveryUT={route.PendingDeliveryUT.Value.ToString("R", IC)}; skipping re-set");
                return;
            }

            route.PendingDeliveryUT = currentUT;
            // v0: single-stop routes (design §11). Stop index 0 is the only
            // stop on every route; multi-stop will reuse this field with the
            // actual stop index resolved from CurrentSegmentIndex.
            route.PendingStopIndex = 0;

            ParsekLog.Info(Tag,
                $"InTransitComplete: route {ShortIdForLog(route)} reached transit boundary; " +
                $"PendingDeliveryUT={currentUT.ToString("R", IC)} stopIndex=0");
        }

        // ==================================================================
        // Delivery applier (item 6 Phase B)
        // ==================================================================

        /// <summary>
        /// Apply a pending delivery cycle (item 6 Phase B). Re-resolves the
        /// destination endpoint, builds a capacity probe against the live
        /// vessel (loaded or unloaded), asks the pure planner what to deliver,
        /// applies the planned resource + inventory writes, debits Career
        /// funds when applicable, emits a <see cref="GameActionType.RouteCargoDelivered"/>
        /// ledger row, and transitions the route back to
        /// <see cref="RouteStatus.Active"/>. Idempotent against ledger replay
        /// via an ELS lookup keyed on <c>(RouteId, RouteCycleId)</c>.
        /// </summary>
        private static void ApplyDelivery(Route route, double currentUT, IRouteRuntimeEnvironment env)
        {
            // Cycle id matches the dispatch/debit pair (cycle-{Completed + Skipped}).
            // The dispatch applier bumps neither counter; delivery is the FIRST
            // place CompletedCycles increments, so the cycle id at delivery time
            // still names the in-flight cycle.
            string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles).ToString(IC);

            // Stop index from the dispatch-arrival hand-off. v0 single-stop
            // routes always land at stopIndex=0; multi-stop will reuse this
            // field with the actual stop index resolved from CurrentSegmentIndex.
            int stopIndex = route.PendingStopIndex >= 0 ? route.PendingStopIndex : 0;

            // STEP 1: idempotency guard. If ELS already contains a
            // RouteCargoDelivered row for (routeId, cycleId), the cycle was
            // delivered on a previous tick (or recovered after crash); applying
            // again would double-charge funds and double-write resources. This
            // is the orchestrator's ONLY ELS read — every other consumer
            // routes through ERS or accepts dispatch evaluator's surface.
            if (IsDeliveryAlreadyInLedger(route.Id, cycleId))
            {
                ParsekLog.Verbose(Tag,
                    $"Delivery: route {ShortIdForLog(route)} cycle={cycleId} replay detected — already in ledger");
                // Advance CompletedCycles symmetric with the success path in
                // ApplyDeliveryFromPlan. The ledger says this cycle WAS
                // delivered (the row exists), so the route's counter must
                // reflect that completed cycle. Otherwise the next dispatch
                // evaluator fires and computes the same cycleId (cycle-{N+S})
                // as the replayed cycle, ApplyDispatch has no idempotency
                // guard and emits a fresh RouteDispatched + RouteCargoDebited
                // under that already-used cycleId, transit elapses, the
                // delivery idempotency check trips again on the same id, and
                // the route loops forever emitting redundant rows for cycle-N.
                // Bumping CompletedCycles here advances the next dispatch to
                // cycle-(N+1) so the cycle id sequence stays unique.
                route.CompletedCycles += 1;
                route.PendingDeliveryUT = null;
                route.PendingStopIndex = -1;
                // Clear any stale retry timer carried over from a pre-dispatch
                // wait state — mirrors the success branch in
                // ApplyDeliveryFromPlan and the ApplyDispatch transition so
                // a delivered cycle never leaves the route gated behind an
                // old WaitRetryIntervalSec deadline.
                route.NextEligibilityCheckUT = null;
                route.TransitionTo(RouteStatus.Active, "delivered-replay");
                return;
            }

            // STEP 2: re-resolve endpoint vessel. The dispatch evaluator already
            // resolved the endpoint at dispatch time, but during transit (which
            // can span many hours of warp) the destination may have been
            // destroyed, recovered, or simply moved out of the surface-proximity
            // radius. Re-resolving here is the only way to avoid forging a
            // delivery onto a vessel that no longer exists.
            RouteStop stop = (route.Stops != null && stopIndex < route.Stops.Count) ? route.Stops[stopIndex] : null;
            if (stop == null)
            {
                ParsekLog.Warn(Tag,
                    $"Delivery: route {ShortIdForLog(route)} cycle={cycleId} stopIndex={stopIndex.ToString(IC)} " +
                    $"missing — aborting delivery and clearing pending state");
                route.PendingDeliveryUT = null;
                route.PendingStopIndex = -1;
                return;
            }

            if (!env.TryResolveEndpointVessel(stop.Endpoint, out Vessel destVessel, out string endpointReason)
                || destVessel == null)
            {
                string lostReason = "endpoint-destroyed-at-delivery:" + (endpointReason ?? "unknown");
                ParsekLog.Info(Tag,
                    $"Delivery: route {ShortIdForLog(route)} cycle={cycleId} endpoint lost at delivery " +
                    $"reason={endpointReason ?? "<none>"}");
                EmitEndpointLostAction(route, currentUT, lostReason);
                // M6 hold reasons: this hold is recorded INSIDE EmitLoopCycle on
                // the loop path, AFTER the pre-emit "crossing-eligible" clear in
                // ProcessLoopRoute, so it survives the crossing (the clear must
                // never move after EmitLoopCycle).
                route.RecordHold(
                    RouteDispatchEvaluator.EligibilityFailureKind.EndpointLost,
                    lostReason, 0.0, currentUT);
                // Recovery-credit deferral tail (logistics-recovery-credit section 5.4):
                // this cycle dispatched + armed its pending credit during EmitLoopCycle,
                // and an EndpointLost route stops crossing, so flush the owed credit
                // before the route goes quiet. Idempotent / gated; no-ops if nothing owed.
                EmitPendingRecoveryCredit(route, currentUT, env);
                route.TransitionTo(RouteStatus.EndpointLost, "endpoint-lost-at-delivery");
                route.PendingDeliveryUT = null;
                route.PendingStopIndex = -1;
                route.NextEligibilityCheckUT = currentUT + WaitRetryIntervalSec;
                return;
            }

            // STEP 3: build a live capacity probe over the destination vessel.
            // Loaded (rendered, parts list populated) vs unloaded (background-
            // physics protoVessel) is decided here once; the probe handles both.
            //
            // CAPTURE the loaded-gate ONCE per delivery and pass it explicitly
            // into both the probe and the writers. KSP can synchronously flip
            // <c>Vessel.loaded</c> / <c>Vessel.packed</c> mid-tick (warp
            // boundaries, focus changes, scene events); if the probe captured
            // the gate at construction time and the writers re-evaluated it
            // per-call, the planner could see one source of free capacity
            // while the writer mutates the other branch — under-fill, or
            // writes into a snapshot that's about to be re-initialized. One
            // source of truth, threaded through every consumer.
            bool destinationIsLoaded = destVessel.loaded && !destVessel.packed;
            LiveDeliveryCapacityProbe probe = new LiveDeliveryCapacityProbe(destVessel, destinationIsLoaded);

            // STEP 4: planner. Pure decision over the resource + inventory
            // manifest, capacity-clamped per resource and slot-aware for
            // inventory. The planner is unit-tested in isolation (Phase A).
            DeliveryPlan plan = RouteDeliveryPlanner.PrepareDelivery(route, stopIndex, probe);

            // Delivery verification (playtest follow-up): log the resolved
            // destination vessel + which branch (loaded/unloaded) + the planned
            // counts, so a cycle's delivery is traceable end-to-end (this line +
            // the per-resource "Delivery write: ... written=X" from LiveDeliveryWriters).
            ParsekLog.Info(Tag,
                $"Delivery endpoint resolved: route={ShortIdForLog(route)} cycle={cycleId} " +
                $"dest={destVessel.vesselName ?? "<none>"} " +
                $"pid={destVessel.persistentId.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                $"path={(destinationIsLoaded ? "loaded" : "unloaded")} " +
                $"plannedResources={(plan.Resources?.Count ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                $"plannedInventory={(plan.Inventory?.Count ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}");

            // STEP 5/6: apply the planned writes. Build an ApplyDeliveryContext
            // that funnels the live mutations behind delegates so the testable
            // helper does not need to instantiate Vessel / Funding /
            // ProtoPartSnapshot. The "writer" delegates are the only place
            // KSP-state mutation happens; ApplyDeliveryFromPlan owns the
            // bookkeeping (actuals, partial detection, status transition,
            // ledger row construction).
            var liveWriters = new LiveDeliveryWriters(route, destVessel, plan, destinationIsLoaded);
            bool isCareerKsc = env.IsCareer && route.IsKscOrigin;
            var ctx = new ApplyDeliveryContext
            {
                CycleId = cycleId,
                CurrentUT = currentUT,
                StopIndex = stopIndex,
                IsCareer = env.IsCareer,
                IsKscOrigin = route.IsKscOrigin,
                KscFundsCost = isCareerKsc ? route.KscDispatchFundsCost : 0.0,
                ResourceWriter = liveWriters.WriteResource,
                ResourceActualReader = liveWriters.ReadActualResource,
                InventoryWriter = liveWriters.WriteInventory,
                InventoryActualCountReader = liveWriters.ReadInventoryActualCount,
                FundsDebiter = LiveDebitFunds,
                LedgerEmitter = Ledger.AddAction,
            };
            ApplyDeliveryFromPlan(route, plan, ctx);
        }

        /// <summary>
        /// Test-injectable apply core. Idempotency, endpoint resolution, and
        /// the live-probe build all happen in <see cref="ApplyDelivery"/> (the
        /// wrapper); this method takes a pre-built <see cref="DeliveryPlan"/>
        /// and a context bag of delegates so xUnit can verify the apply
        /// bookkeeping (resource writer invocations, funds-debit conditional,
        /// ledger row shape, status transition) without instantiating any
        /// live KSP objects.
        /// </summary>
        internal static void ApplyDeliveryFromPlan(Route route, DeliveryPlan plan, ApplyDeliveryContext ctx)
        {
            // Apply resource writes. The writer delegate returns nothing — the
            // actual transferred amount is fetched separately via
            // <see cref="ApplyDeliveryContext.ResourceActualReader"/> so the
            // delegate signature stays simple. Planner guarantees Available <=
            // capacity, so for the unloaded path the writer can write the full
            // amount; the loaded path may transfer less if mid-tick resource
            // state changed.
            int resourceLinesApplied = 0;
            if (plan.Resources != null)
            {
                for (int i = 0; i < plan.Resources.Count; i++)
                {
                    ResourceDeliveryLine line = plan.Resources[i];
                    if (line.Available <= 0.0) continue;
                    ctx.ResourceWriter(line.Name, line.Available);
                    resourceLinesApplied++;
                }
            }

            // Apply inventory writes. Items with AssignedSlot < 0 were skipped
            // by the planner (no empty slot at probe time); the writer is only
            // called for assigned slots. The writer may itself fail (stock
            // <c>StoreCargoPartAtSlot</c> can return false on edge cases like
            // mass-limit overruns); the actual-count reader returns how many
            // writes succeeded.
            int inventoryLinesAttempted = 0;
            if (plan.Inventory != null)
            {
                for (int i = 0; i < plan.Inventory.Count; i++)
                {
                    InventoryDeliveryLine line = plan.Inventory[i];
                    if (line.AssignedSlot < 0) continue;
                    if (line.Item == null) continue;
                    ctx.InventoryWriter(line.Item, line.AssignedSlot);
                    inventoryLinesAttempted++;
                }
            }

            // STEP 7: Career funds debit. Skip silently in Sandbox/Science or
            // when the route is not a KSC origin — the dispatch evaluator's
            // KscFundsAvailable check is only consulted in (Career AND KSC),
            // so the debit must mirror that exact predicate to keep the
            // dispatch/delivery pair internally consistent.
            if (ctx.IsCareer && ctx.IsKscOrigin && ctx.KscFundsCost > 0.0)
            {
                ctx.FundsDebiter(ctx.KscFundsCost);
                ParsekLog.Info(Tag,
                    $"Delivery: route {ShortIdForLog(route)} Career KSC funds debited: " +
                    $"-{ctx.KscFundsCost.ToString("R", IC)}");
            }

            // STEP 8: build and emit the RouteCargoDelivered row. The actuals
            // are populated from the readers; the requested manifest is only
            // populated when the actual fell short of requested for at least
            // one resource (saves bytes on the happy full-fill case).
            Dictionary<string, double> actualManifest = null;
            Dictionary<string, double> requestedManifest = null;
            if (plan.Resources != null)
            {
                for (int i = 0; i < plan.Resources.Count; i++)
                {
                    ResourceDeliveryLine line = plan.Resources[i];
                    double actual = ctx.ResourceActualReader(line.Name);
                    if (actual > 0.0)
                    {
                        if (actualManifest == null)
                            actualManifest = new Dictionary<string, double>(plan.Resources.Count, StringComparer.Ordinal);
                        actualManifest[line.Name] = actual;
                    }
                    if (line.Available < line.Requested)
                    {
                        if (requestedManifest == null)
                            requestedManifest = new Dictionary<string, double>(plan.Resources.Count, StringComparer.Ordinal);
                        requestedManifest[line.Name] = line.Requested;
                    }
                }
            }

            var action = new GameAction
            {
                Type = GameActionType.RouteCargoDelivered,
                UT = ctx.CurrentUT,
                RouteId = route.Id,
                RouteCycleId = ctx.CycleId,
                RouteStopIndex = ctx.StopIndex,
                Sequence = 0,
                RouteResourceManifest = actualManifest,
                RouteRequestedResourceManifest = requestedManifest,
                // GameAction.RouteKscFundsCost is float-typed by design (same
                // as RouteCargoDebited). The double precision on
                // ApplyDeliveryContext.KscFundsCost is preserved upstream for
                // diagnostics.
                RouteKscFundsCost = (ctx.IsCareer && ctx.IsKscOrigin) ? (float)ctx.KscFundsCost : 0f,
            };
            ctx.LedgerEmitter(action);

            // STEP 9: mutate route state + transition. Delivery completion is
            // the only place CompletedCycles increments. Status transitions
            // back to Active with a reason string carrying the partial/full
            // discriminator so the UI can surface "delivered partial" badges.
            route.CompletedCycles += 1;
            route.PendingDeliveryUT = null;
            route.PendingStopIndex = -1;
            // Successful delivery exits the route from any wait state — clear
            // the retry timer so a future tick is not blocked by a stale
            // NextEligibilityCheckUT inherited from a pre-dispatch
            // WaitingForResources / WaitingForFunds / DestinationFull pass.
            // Mirrors ApplyDispatch (line ~301).
            route.NextEligibilityCheckUT = null;

            int inventoryActual = ctx.InventoryActualCountReader();

            // Honor PauseAfterCurrentCycle: a route armed by "Send Once" (or a
            // future user pause-after-cycle action) transitions to Paused
            // after its in-flight cycle completes, instead of looping back to
            // Active. The flag is consumed here (cleared) so a subsequent
            // un-pause + dispatch doesn't auto-pause again.
            if (route.PauseAfterCurrentCycle)
            {
                // Armed pause-after-cycle tail (logistics-recovery-credit section 5.4):
                // this cycle SET its pending recovery credit during its own
                // EmitLoopCycle (right after EmitDispatchDebit), and there is no
                // further crossing because the route is about to go quiet, so flush
                // the pending credit here before TransitionTo(Paused). The ctx
                // career/KSC flags drive the gate (no live env in scope here).
                EmitPendingRecoveryCredit(
                    route, ctx.CurrentUT, new ApplyDeliveryEnvAdapter(ctx.IsCareer));

                route.PauseAfterCurrentCycle = false;
                string reason = plan.IsPartial ? "delivered-partial-then-paused" : "delivered-then-paused";
                route.TransitionTo(RouteStatus.Paused, reason);
            }
            else
            {
                route.TransitionTo(RouteStatus.Active, plan.IsPartial ? "delivered-partial" : "delivered");
            }

            ParsekLog.Info(Tag,
                $"Delivery: route {ShortIdForLog(route)} cycle={ctx.CycleId} " +
                $"resources={resourceLinesApplied.ToString(IC)} " +
                $"inventory={inventoryActual.ToString(IC)}/{inventoryLinesAttempted.ToString(IC)} " +
                $"partial={(plan.IsPartial ? "1" : "0")} " +
                $"ut={ctx.CurrentUT.ToString("R", IC)}");
        }

        /// <summary>
        /// ELS-based idempotency check (item 6 Phase B). Returns <c>true</c> if
        /// any non-tombstoned <see cref="GameActionType.RouteCargoDelivered"/>
        /// row already exists for the given <paramref name="routeId"/> +
        /// <paramref name="cycleId"/> pair. This is the orchestrator's only
        /// raw-ELS read — every other ledger interaction in this file goes
        /// through <see cref="Ledger.AddAction"/> on the write side.
        /// </summary>
        private static bool IsDeliveryAlreadyInLedger(string routeId, string cycleId)
        {
            if (string.IsNullOrEmpty(routeId) || string.IsNullOrEmpty(cycleId))
                return false;

            IReadOnlyList<GameAction> els;
            try
            {
                els = EffectiveState.ComputeELS();
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"IsDeliveryAlreadyInLedger: ComputeELS threw {ex.GetType().Name}: {ex.Message}; treating as not-in-ledger");
                return false;
            }

            if (els == null) return false;
            for (int i = 0; i < els.Count; i++)
            {
                GameAction a = els[i];
                if (a == null) continue;
                if (a.Type != GameActionType.RouteCargoDelivered) continue;
                if (!string.Equals(a.RouteId, routeId, StringComparison.Ordinal)) continue;
                if (!string.Equals(a.RouteCycleId, cycleId, StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// ELS-based idempotency check for the recovery credit
        /// (logistics-recovery-credit, design doc section 6.4). Exact structural
        /// mirror of <see cref="IsDeliveryAlreadyInLedger"/> but scanning for a
        /// <see cref="GameActionType.RouteRecoveryCredited"/> row with the same
        /// <c>(RouteId, RouteCycleId)</c> pair. Keyed on the CREDIT's own row (the
        /// PRIOR dispatched cycle it pays back), NOT on the delivery row, so a
        /// present delivery for the current crossing is never mistaken for "credit
        /// already emitted" (gotcha G6). Reads ELS (supersede / tombstone aware): a
        /// tombstoned credit row must NOT block re-emitting a fresh credit on a
        /// re-fly, and ELS hides tombstoned rows, so this is the right surface.
        /// </summary>
        private static bool IsRecoveryCreditAlreadyInLedger(string routeId, string cycleId)
        {
            if (string.IsNullOrEmpty(routeId) || string.IsNullOrEmpty(cycleId))
                return false;

            IReadOnlyList<GameAction> els;
            try
            {
                els = EffectiveState.ComputeELS();
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"IsRecoveryCreditAlreadyInLedger: ComputeELS threw {ex.GetType().Name}: {ex.Message}; treating as not-in-ledger");
                return false;
            }

            if (els == null) return false;
            for (int i = 0; i < els.Count; i++)
            {
                GameAction a = els[i];
                if (a == null) continue;
                if (a.Type != GameActionType.RouteRecoveryCredited) continue;
                if (!string.Equals(a.RouteId, routeId, StringComparison.Ordinal)) continue;
                if (!string.Equals(a.RouteCycleId, cycleId, StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// ELS reader for the recovery-credit AMOUNT computation. Exception-safe:
        /// a throw is treated as "no recoveries" (empty list), so a degenerate
        /// ledger state never crashes the crossing. Mirrors the try/catch in
        /// <see cref="IsRecoveryCreditAlreadyInLedger"/>.
        /// </summary>
        private static IReadOnlyList<GameAction> SafeComputeEls()
        {
            try
            {
                return EffectiveState.ComputeELS();
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"SafeComputeEls: ComputeELS threw {ex.GetType().Name}: {ex.Message}; treating as empty");
                return Array.Empty<GameAction>();
            }
        }

        /// <summary>
        /// Flush the PRIOR dispatched cycle's deferred recovery credit at
        /// <paramref name="currentUT"/> (logistics-recovery-credit, design doc
        /// section 5.2). Emits a single
        /// <see cref="GameActionType.RouteRecoveryCredited"/> row keyed on the
        /// PENDING (prior dispatched) cycle id and applies the matching live stock
        /// credit, then clears the pending marker. Returns true when a credit row
        /// was emitted (for the caller's log line). Called from the TOP of
        /// <see cref="EmitLoopCycle"/> (before the delivery-keyed replay
        /// short-circuit), from the blocked-cycle branch, and from the pause / stop
        /// transitions (section 5.4) so the deferral stays honest across blocked
        /// gaps and the route's final dispatched cycle. Idempotent (guard 3): a
        /// re-presented crossing whose credit already landed emits nothing.
        ///
        /// <para>Career + KSC origin only (gotcha G5). A pending id can only have
        /// been set on a Career-KSC dispatch, but the gate is re-checked
        /// defensively in case env flips (e.g. a save copied into Sandbox); a stale
        /// pending marker is then CLEARED without emitting.</para>
        /// </summary>
        internal static bool EmitPendingRecoveryCredit(
            Route route, double currentUT, IRouteRuntimeEnvironment env)
        {
            if (route == null)
            {
                ParsekLog.Verbose(Tag, "EmitPendingRecoveryCredit: null route");
                return false;
            }

            // Nothing owed (fresh route, or already flushed). Not an error.
            string pendingCycleId = route.PendingRecoveryCreditCycleId;
            if (string.IsNullOrEmpty(pendingCycleId))
            {
                ParsekLog.Verbose(Tag,
                    $"EmitPendingRecoveryCredit: route {ShortIdForLog(route)} no pending credit at " +
                    $"ut={currentUT.ToString("R", IC)}");
                return false;
            }

            // Gate: Career + KSC origin only. Mirror EmitDispatchDebit's isCareerKsc.
            bool isCareerKsc = env != null && env.IsCareer && route.IsKscOrigin;
            if (!isCareerKsc)
            {
                ClearPendingRecoveryCredit(route);
                ParsekLog.Info(Tag,
                    $"EmitPendingRecoveryCredit: route {ShortIdForLog(route)} cycle={pendingCycleId} " +
                    $"credit-skip non-career-ksc (career={(env != null && env.IsCareer ? "1" : "0")} " +
                    $"ksc={(route.IsKscOrigin ? "1" : "0")}): cleared pending");
                return false;
            }

            // Idempotency backstop (guard 3): do not emit a second credit for the
            // same (RouteId, pendingCycleId). A save/reload re-presented this
            // crossing whose pending credit was already flushed.
            if (IsRecoveryCreditAlreadyInLedger(route.Id, pendingCycleId))
            {
                ClearPendingRecoveryCredit(route);
                ParsekLog.Info(Tag,
                    $"EmitPendingRecoveryCredit: route {ShortIdForLog(route)} cycle={pendingCycleId} " +
                    "replay (credit already in ledger): emitting nothing, cleared stale pending");
                return false;
            }

            // Amount: reuse SumRecoveredCredits over the source tree, read from ELS.
            HashSet<string> treeIds = RouteRunCostCalculator.ResolveTreeRecordingIds(route);
            IReadOnlyList<GameAction> els = SafeComputeEls();
            double recovered = RouteRunCostCalculator.SumRecoveredCredits(route, els, treeIds, out int n);
            if (recovered <= 0.0)
            {
                ClearPendingRecoveryCredit(route);
                ParsekLog.Info(Tag,
                    $"EmitPendingRecoveryCredit: route {ShortIdForLog(route)} cycle={pendingCycleId} " +
                    $"credit-skip zero-recovery (recoveryRows={n.ToString(IC)}): cleared pending");
                return false;
            }

            double dispatchUTForLog = route.PendingRecoveryCreditDispatchUT;

            // Emit the credit row (section 6.1) and apply the live stock credit
            // (section 6.3). Sequence 0: emitted FIRST at this crossing's UT,
            // before this cycle's RouteDispatched (which is Sequence 0 too at a
            // LATER-or-equal UT, but the credit's RouteCycleId names the PRIOR
            // cycle, so the keys never collide).
            var credit = new GameAction
            {
                Type = GameActionType.RouteRecoveryCredited,
                UT = currentUT,
                RouteId = route.Id,
                RouteCycleId = pendingCycleId,
                RouteStopIndex = -1,
                Sequence = 0,
                RouteKscFundsCost = (float)recovered, // positive magnitude; type carries the credit direction
            };
            Ledger.AddAction(credit);

            var funder = RecoveryCreditFunderForTesting;
            if (funder != null)
                funder(recovered);
            else
                LiveCreditFunds(recovered);

            ClearPendingRecoveryCredit(route);

            ParsekLog.Info(Tag,
                $"RecoveryCredit: route {ShortIdForLog(route)} creditedCycle={pendingCycleId} " +
                $"recovered={recovered.ToString("R", IC)} recoveryRows={n.ToString(IC)} " +
                $"ut={currentUT.ToString("R", IC)} dispatchUT={dispatchUTForLog.ToString("R", IC)}");
            return true;
        }

        /// <summary>
        /// Clears the recovery-credit pending marker back to its "no credit owed"
        /// defaults (null cycle id, -1 dispatch UT). Single place the reset shape
        /// is defined so every clear path (flush, skip, gate-fail) stays
        /// consistent.
        /// </summary>
        private static void ClearPendingRecoveryCredit(Route route)
        {
            if (route == null) return;
            route.PendingRecoveryCreditCycleId = null;
            route.PendingRecoveryCreditDispatchUT = -1.0;
        }

        /// <summary>
        /// Emit a <see cref="GameActionType.RouteEndpointLost"/> ledger row from
        /// the delivery applier. Mirrors <see cref="ApplyEndpointLost"/> from
        /// the dispatch side but the reason string is delivery-specific so the
        /// ledger / UI can distinguish "endpoint lost before dispatch" from
        /// "endpoint destroyed mid-transit".
        /// </summary>
        private static void EmitEndpointLostAction(Route route, double currentUT, string reason)
        {
            var action = new GameAction
            {
                Type = GameActionType.RouteEndpointLost,
                UT = currentUT,
                RouteId = route.Id,
                RouteStopIndex = -1,
                Sequence = 0,
                RouteEndpointReason = reason,
            };
            Ledger.AddAction(action);
            ParsekLog.Info(Tag,
                $"EndpointLost: route {ShortIdForLog(route)} reason={reason ?? "<none>"} (at delivery)");
        }

        /// <summary>
        /// Resource-write filter shared by the live writers and the live
        /// capacity probe. Returns <c>true</c> only when the destination tank
        /// is BOTH:
        /// <list type="bullet">
        ///   <item><c>flowState == true</c> — the player has not closed the
        ///     tank's flow toggle. Writing to a closed tank silently violates
        ///     player intent (the tank is locked from the player's POV).</item>
        ///   <item><paramref name="flowMode"/> not <see cref="ResourceFlowMode.NO_FLOW"/>
        ///     — v0 simplicity: <c>SolidFuel</c>, <c>EVAPropellant</c>,
        ///     <c>Ablator</c>, etc. don't cross part boundaries in stock, so
        ///     depositing them via route delivery would be a stock contract
        ///     violation. v1+ may surface a player option to override.</item>
        /// </list>
        /// Extracting this as a pure helper lets xUnit pin the policy without
        /// touching live KSP <c>PartResource</c> / <c>ProtoPartResourceSnapshot</c>
        /// instances; the writers and probes call it at the tank-iteration
        /// boundary.
        /// </summary>
        internal static bool ShouldDeliverToResource(bool flowState, ResourceFlowMode flowMode)
        {
            if (!flowState) return false;
            if (flowMode == ResourceFlowMode.NO_FLOW) return false;
            return true;
        }

        /// <summary>
        /// Looks up the <see cref="ResourceFlowMode"/> for a named resource via
        /// <see cref="PartResourceLibrary"/>. Returns <see cref="ResourceFlowMode.ALL_VESSEL"/>
        /// (a "delivery allowed" value) when the library is not available or
        /// the resource is missing, so writes/probes don't accidentally suppress
        /// a legitimate delivery when the library is mid-load. <see cref="NO_FLOW"/>
        /// is a deliberate gate — only the explicit definition triggers the
        /// suppression in <see cref="ShouldDeliverToResource"/>.
        /// </summary>
        internal static ResourceFlowMode LookupResourceFlowMode(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName)) return ResourceFlowMode.ALL_VESSEL;
            try
            {
                PartResourceDefinition def =
                    PartResourceLibrary.Instance != null
                        ? PartResourceLibrary.Instance.GetDefinition(resourceName)
                        : null;
                return def != null ? def.resourceFlowMode : ResourceFlowMode.ALL_VESSEL;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"LookupResourceFlowMode({resourceName}) threw {ex.GetType().Name}: {ex.Message}; defaulting ALL_VESSEL");
                return ResourceFlowMode.ALL_VESSEL;
            }
        }

        /// <summary>
        /// Production funds-debit delegate. Skipped silently in Sandbox/Science
        /// because <see cref="ApplyDeliveryFromPlan"/> already gates the call
        /// on <c>(IsCareer AND IsKscOrigin)</c>. Defensive-null-check on
        /// <c>Funding.Instance</c> survives early-load ticks where the funding
        /// singleton is not yet published.
        /// </summary>
        private static void LiveDebitFunds(double cost)
        {
            try
            {
                if (Funding.Instance != null && cost > 0.0)
                {
                    Funding.Instance.AddFunds(-cost, TransactionReasons.None);
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"LiveDebitFunds({cost.ToString("R", IC)}) threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Production funds-CREDIT delegate (logistics-recovery-credit, design doc
        /// section 6.3). Mirror of <see cref="LiveDebitFunds"/> with a POSITIVE
        /// delta: the deferred per-cycle recovery credit. Defensively null-checks
        /// <c>Funding.Instance</c> (early-load ticks) and try/catches. This is the
        /// immediate live effect; the recalc walk's <c>PatchFunds</c> is a
        /// reconcile-to-target, NOT an additive replay, so the credit is not
        /// double-applied (when no rewind has happened the target already includes
        /// the credit as a FundsModule earning, so the delta is ~0). After a rewind
        /// cutoff the credit row is excluded from the target while live funds still
        /// carry this mutation, so PatchFunds subtracts it: reversible by
        /// construction.
        /// </summary>
        private static void LiveCreditFunds(double amount)
        {
            try
            {
                if (Funding.Instance != null && amount > 0.0)
                {
                    Funding.Instance.AddFunds(+amount, TransactionReasons.None);
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"LiveCreditFunds({amount.ToString("R", IC)}) threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Production funds-credit seam for the recovery credit. Default routes to
        /// <see cref="LiveCreditFunds"/>; xUnit assigns a fake that records the
        /// credit amount without touching the <c>Funding</c> static (which is not
        /// available off-Unity). Mirrors the
        /// <see cref="DeliveryApplierForTesting"/> seam pattern.
        /// </summary>
        internal static Action<double> RecoveryCreditFunderForTesting;

        // ==================================================================
        // Delivery seam types (item 6 Phase B)
        // ==================================================================

        /// <summary>
        /// Context bag for <see cref="ApplyDeliveryFromPlan"/>. Bundles cycle
        /// identity, Career/KSC gates, and the apply-writer delegates so the
        /// test side can inject fakes that record calls without touching
        /// <c>Vessel</c> / <c>Funding</c> / <c>Ledger.AddAction</c>.
        /// </summary>
        internal struct ApplyDeliveryContext
        {
            public string CycleId;
            public double CurrentUT;
            public int StopIndex;
            public bool IsCareer;
            public bool IsKscOrigin;
            public double KscFundsCost;
            public Action<string, double> ResourceWriter;
            public Func<string, double> ResourceActualReader;
            public Action<InventoryPayloadItem, int> InventoryWriter;
            public Func<int> InventoryActualCountReader;
            public Action<double> FundsDebiter;
            public Action<GameAction> LedgerEmitter;
        }

        /// <summary>
        /// Minimal <see cref="IRouteRuntimeEnvironment"/> adapter carrying only the
        /// Career flag, used to drive <see cref="EmitPendingRecoveryCredit"/>'s
        /// Career + KSC gate from inside <see cref="ApplyDeliveryFromPlan"/> (the
        /// armed pause-after-cycle credit flush, section 5.4), where the live env is
        /// not in scope. <see cref="EmitPendingRecoveryCredit"/> reads only
        /// <see cref="IsCareer"/> off the env; every other member is a never-called
        /// stub. The KSC-origin half of the gate is read off the route, not the env.
        /// </summary>
        private sealed class ApplyDeliveryEnvAdapter : IRouteRuntimeEnvironment
        {
            public ApplyDeliveryEnvAdapter(bool isCareer) { IsCareer = isCareer; }
            public bool IsCareer { get; }
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = null; return false; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason) { vessel = null; reason = null; return false; }
            public bool OriginHasCargo(Route route, out string lackingResource) { lackingResource = null; return false; }
            public bool KscFundsAvailable(Route route, out double shortfall) { shortfall = 0.0; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = null; return true; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        /// <summary>
        /// Locates the route's source <see cref="Recording"/> in ERS, then
        /// computes the dispatch funds cost from its <c>VesselSnapshot</c> via
        /// <see cref="RouteFundsCalculator.ComputeDispatchFundsCost"/>. Returns
        /// 0 when ERS cannot be queried (e.g. during early load) or the
        /// recording / snapshot is missing.
        ///
        /// <para>M2 funds basis (plan D9 / OQ1): when the source recording -
        /// <c>SourceRefs[0]</c>, the tree ROOT, whose snapshot is taken at the
        /// FIRST chain/branch boundary - carries a COMPLETE
        /// <see cref="RouteRunCargoManifest"/> (start half present AND
        /// <see cref="RouteRunCargoManifest.EndCaptured"/>, the SAME completeness
        /// gate the harvest analysis uses, plan risk 7: gate and charge must not
        /// diverge), the resource term is priced from the run's START transport
        /// manifest (the launch load). Recordings without a complete manifest -
        /// every pre-M2 recording and every degraded leg - keep the legacy
        /// stop-snapshot walk byte-identical, so existing routes keep their
        /// exact cost. Both the eligibility gate and the emit recompute call
        /// this one method, so they always pick the same basis.</para>
        /// </summary>
        internal static double ComputeDispatchFundsCostForRoute(Route route)
        {
            if (route == null || route.SourceRefs == null || route.SourceRefs.Count == 0)
                return 0.0;

            // v0 routes have a single source recording — chain-source routes
            // will pick the first/transport recording once multi-source funds
            // computation is specified.
            string sourceId = route.SourceRefs[0]?.RecordingId;
            if (string.IsNullOrEmpty(sourceId))
                return 0.0;

            var ers = SafeComputeErs();
            if (ers == null) return 0.0;

            Recording source = null;
            for (int i = 0; i < ers.Count; i++)
            {
                var rec = ers[i];
                if (rec != null && string.Equals(rec.RecordingId, sourceId, StringComparison.Ordinal))
                {
                    source = rec;
                    break;
                }
            }
            if (source == null || source.VesselSnapshot == null)
            {
                ParsekLog.Verbose(Tag,
                    $"ComputeDispatchFundsCostForRoute: route {ShortIdForLog(route)} source " +
                    $"recording {sourceId} not in ERS or has no VesselSnapshot; cost=0");
                return 0.0;
            }

            // D9 basis selection: launch manifest only from a COMPLETE run
            // manifest (same gate as the analysis presence gate); anything
            // else stays on the legacy stop-snapshot walk.
            RouteRunCargoManifest runManifest = source.RouteRunManifest;
            Dictionary<string, ResourceAmount> startResources =
                runManifest != null && runManifest.IsComplete
                    ? runManifest.StartTransportResources
                    : null;

            double cost = RouteFundsCalculator.ComputeDispatchFundsCost(
                source.VesselSnapshot,
                startResources,
                LiveRouteRuntimeEnvironment.LookupPartCost,
                LiveRouteRuntimeEnvironment.LookupResourceUnitCost);

            ParsekLog.Verbose(Tag,
                $"FundsCost basis={(startResources != null ? "launch-manifest" : "stop-snapshot")} " +
                $"route={ShortIdForLog(route)} source={sourceId} " +
                $"cost={cost.ToString("R", CultureInfo.InvariantCulture)}");
            return cost;
        }

        /// <summary>
        /// Defensive <see cref="EffectiveState.ComputeERS"/> wrapper used by
        /// <see cref="ComputeDispatchFundsCostForRoute"/>. ComputeERS can throw
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

        private static string ShortIdForLog(Route route)
        {
            if (route == null || string.IsNullOrEmpty(route.Id)) return "<no-id>";
            return route.Id.Length > 8 ? route.Id.Substring(0, 8) : route.Id;
        }
    }
}
