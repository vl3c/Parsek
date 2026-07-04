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
        /// (M4a A3) Per-window <see cref="GameAction.Sequence"/> stride. Under
        /// Horn A a multi-stop cycle fires N windows under ONE cycleId, so the
        /// fixed intra-window order (RouteDispatched 0 / RouteCargoDebited 1 /
        /// RouteCargoPickedUp 2 / RouteCargoDelivered 3) is NOT unique across
        /// windows. Each window's rows are offset by
        /// <c>stopIndex * SEQ_STRIDE + intraSeq</c> so <c>(UT, Sequence)</c> stays
        /// a TOTAL order across windows (replay-stability + the ledger dedup
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
                // M4a A3 (OQ3): the per-stop fire sub-gates must reset alongside the
                // route-level cycle cursor, else a multi-stop route re-activated
                // after a Pause would see stale LastFiredCycleIndex values and stall
                // (raised) or jump (lowered) the post-activate windows. -1 means
                // "no cycle fired yet" so the first post-activate crossing of each
                // window fires exactly once.
                ResetStopFireState(route);
                // M5 (D3): the residual-modulo offset anchor resets wherever the
                // cycle cursor rebases - the first post-activate crossing on a
                // windowed route re-anchors and delivers (anchor adoption).
                route.WindowAnchorCycleIndex = -1;
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

            // M4b escrow-strand fix (PR #1180 review): a multi-origin route that reserved
            // its per-source escrow at dispatch and is paused MID-cycle never reaches the
            // cycle-complete DropRouteEscrow sweep, so the stale reservation would keep
            // mis-gating a competing route sharing that source until the next scene switch.
            // Drop it on the immediate-pause transition. Idempotent no-op when nothing is
            // held (a delivery-only route, or a route paused between cycles).
            RouteStore.DropRouteEscrow(route.Id);

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

            // M5 (D1/D6): derive the route-side window basis once per tick from
            // the resolved unit and run the basis-flip transition evaluator
            // BEFORE any crossing consumption - a build-level flip re-baselines
            // the cycle cursors between the flat and window index spaces
            // (Engage: the review-C6 permanent-silent-skip guard; Decline: the
            // mis-fire guard, no fire this tick by construction). Behaviorally
            // inert for flat routes (basis FlatInterval + marker false =
            // steady-state None; no field writes, no logs).
            RouteWindowBasis basis = RouteLoopClock.DeriveWindowBasis(unit);
            ApplyWindowBasisTransition(route, basis, loopUT, cycleIndex, currentUT);

            // M5 (D2): the residual cadence modulo - live only when > 1, which
            // is only possible under the ReaimWindows basis (flat: N is fully
            // consumed by the flat cadence DispatchInterval = N x span;
            // zero-drift: N is fully consumed Missions-side by the schedule's
            // minSpacing throttle, sIdx indexes the THROTTLED launch list).
            int nResidual = RouteLoopClock.ResolveResidualCadence(basis, route.CadenceMultiplier);

            // M4a A3 (Horn A, BLOCKER-1 fix): a multi-stop DELIVERY route fires N
            // windows under ONE cycleId, each at its OWN recorded dock phase via the
            // loop clock. The dispatch half fires once (cycle open), each window's
            // delivery (+pickup) fires at its own RecordedDockUT, and CompletedCycles
            // bumps once per completed cycle. A single-stop route keeps the
            // byte-identical scalar path below.
            //
            // Catch-up loop (BLOCKER-1): ProcessMultiStopCrossings processes EXACTLY
            // ONE dock-cycle per pass - the LOWEST owed cycle (cMin). Processing one
            // cycle at a time, in ascending order, with Completed/Skipped bumped
            // exactly once when cMin's last dock is reached, is what keeps the
            // invariant Completed+Skipped == cMin (so cycleId = cycle-{C+S} is
            // genuinely UNIQUE per cycle and the per-window replay guards never
            // collide across cycles). When a tick straddles >1 owed cycle (a gap tick
            // with a prior owed window, or a long warp), the pass returns
            // stillDue=true and this loop re-invokes the helper - the next pass sees
            // the bumped C+S baseline and a fresh cycleId for cMin+1. Bounded by the
            // same MaxCatchUpCyclesPerTick cap as the legacy catch-up loop.
            if (route.Stops != null && route.Stops.Count > 1)
            {
                int passes = 0;
                bool stillDue = true;
                while (stillDue && passes < MaxCatchUpCyclesPerTick)
                {
                    ProcessMultiStopCrossings(
                        route, currentUT, env, unit, loopUT, cycleIndex, isInInterCycleTail,
                        basis, nResidual,
                        ref dispatched, ref transitioned, ref skipped, out stillDue);
                    passes++;
                }
                if (stillDue)
                {
                    ParsekLog.Warn(Tag,
                        $"LoopRoute(multi): route {ShortIdForLog(route)} hit max catch-up " +
                        $"passes ({MaxCatchUpCyclesPerTick.ToString(IC)}) at ut={currentUT.ToString("R", IC)} " +
                        "- deferring remaining owed cycles to next tick");
                }
                return;
            }

            // Dock-phase crossing detection (DEL-2), via the shared owed-crossing
            // emitter (M-MIS-11 item 2). A new span-clock cycle alone does NOT
            // fire: the delivery is gated on the loop clock having reached the
            // recorded dock PHASE within the cycle (loopUT >= RecordedDockUT), so
            // one loop-clock crossing == one ghost relaunch == one delivery that
            // fires when the ghost reaches the recorded dock. The emitter's
            // DockCycleIndex is the cycle whose dock instant has most recently
            // passed (== cycleIndex once the dock phase is reached, cycleIndex-1
            // while the ghost is still pre-dock); the route snaps
            // LastObservedLoopCycleIndex to THIS, not the raw cycleIndex, so a
            // tick landing early in a fresh cycle does not consume that cycle
            // before its dock.
            bool crossing = RouteLoopClock.TryGetOwedDockCrossing(
                unit, loopUT, cycleIndex, route.RecordedDockUT,
                route.LastObservedLoopCycleIndex, out RouteLoopClock.OwedDockCrossing owedCrossing);
            long dockCycleIndex = owedCrossing.DockCycleIndex;
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

            // M5 (D2/D3/D4/D5): the residual cadence modulo, live only under the
            // ReaimWindows basis (nResidual > 1 is impossible elsewhere, D2).
            // Decided AFTER the crossing is confirmed and BEFORE the cycleId /
            // eligibility machinery, so a skipped window touches neither.
            if (nResidual > 1)
            {
                if (route.WindowAnchorCycleIndex < 0)
                {
                    // D3 anchor adoption: the first owed crossing after creation /
                    // activation / rebase / engage anchors the modulo and DELIVERS
                    // (fall through to the fire path below).
                    route.WindowAnchorCycleIndex = dockCycleIndex;
                    ParsekLog.Info(Tag,
                        $"LoopRoute: route {ShortIdForLog(route)} window-anchor ADOPTED " +
                        $"anchor={dockCycleIndex.ToString(IC)} (N={nResidual.ToString(IC)} basis={basis}) " +
                        $"- this window delivers, every {nResidual.ToString(IC)}th after it");
                }
                else
                {
                    long deliverable = RouteLoopClock.ComputeHighestDeliverableWindow(
                        route.LastObservedLoopCycleIndex, dockCycleIndex,
                        route.WindowAnchorCycleIndex, nResidual);
                    if (deliverable == RouteLoopClock.NoDeliverableWindow)
                    {
                        // D4: a modulo-SKIPPED window advances the marker and
                        // NOTHING else. No SkippedCycles bump (scheduled behavior,
                        // not a failure; cycleId uniqueness is per FIRED cycle
                        // only), no hold, no ledger rows, no escrow, no
                        // partner-alternation advance. The skip IS the "next
                        // crossing" for the previously dispatched cycle, so its
                        // pending recovery credit flushes here (OQ3), mirroring
                        // the blocked path above.
                        EmitPendingRecoveryCredit(route, currentUT, env);
                        route.LastObservedLoopCycleIndex = dockCycleIndex;
                        skipped++;
                        ParsekLog.VerboseRateLimited(Tag, "loop-modulo-skip-" + route.Id,
                            $"LoopRoute: route {ShortIdForLog(route)} window {dockCycleIndex.ToString(IC)} " +
                            $"SKIPPED by cadence modulo (N={nResidual.ToString(IC)} " +
                            $"anchor={route.WindowAnchorCycleIndex.ToString(IC)} basis={basis}) " +
                            "- marker advanced, nothing emitted", 5.0);
                        return;
                    }
                    if (deliverable != dockCycleIndex)
                    {
                        // D5 warp rule: the jump spans a deliverable window plus
                        // trailing skipped one(s). Fire ONCE for the highest
                        // deliverable window; the snap below still goes to
                        // dockCycleIndex (never replay, never re-fire).
                        ParsekLog.Verbose(Tag,
                            $"LoopRoute: route {ShortIdForLog(route)} warp spans modulo windows - " +
                            $"firing for highest deliverable window {deliverable.ToString(IC)} " +
                            $"within (lastObserved, {dockCycleIndex.ToString(IC)}]; snapping to " +
                            $"{dockCycleIndex.ToString(IC)}");
                    }
                }
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
                // M4c Phase C1 (plan D12 / OQ8): a fresh dispatch consumes the
                // partner's current cycle so the route holds WaitingForPartner until
                // the partner completes ANOTHER cycle (strict A->B->A alternation).
                // Advance ONLY on a genuine fire (emitted true), NOT on the
                // ELS-replay backstop below (a replay re-presents an already-consumed
                // cycle - re-advancing would skip a partner cycle).
                AdvancePartnerAlternationOnDispatch(route, currentUT);
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
        /// (M4a A3 / Horn A) Per-window firing for a MULTI-STOP delivery route.
        /// Within ONE span cycle, N windows fire each at its OWN recorded dock
        /// phase under ONE cycleId (<c>cycle-{Completed+Skipped}</c>): the dispatch
        /// half fires once (cycle open, the once-per-cycle KSC funds charge), each
        /// stop's delivery (+ pickup if witnessed) fires at <c>stop.RecordedDockUT</c>
        /// via the loop clock, and <see cref="Route.CompletedCycles"/> bumps ONCE
        /// after the LAST window's phase has been reached.
        ///
        /// <para><b>Per-stop fire state (OQ3).</b> Each <see cref="RouteStop"/>
        /// carries <see cref="RouteStop.LastFiredCycleIndex"/>; a window fires when
        /// its dock phase has been reached for a cycle index strictly greater than
        /// that, then snaps it forward (fire-once across save/reload). The route-
        /// level <see cref="Route.LastObservedLoopCycleIndex"/> snap is DEFERRED
        /// until <c>loopUT &gt;= max(stop.RecordedDockUT)</c> (the cycle's last
        /// dock), so a tick landing between dock A and dock B still fires window B
        /// on the next tick (it would otherwise see the cycle consumed). Warp:
        /// each due window fires ONCE for the highest passed dock cycle and snaps
        /// forward; the per-stop guard + the dispatch ELS backstop keep it
        /// idempotent.</para>
        ///
        /// <para><b>Dispatch-once / eligibility.</b> Eligibility is checked once
        /// per cycle, the first tick a window of the cycle is due. A blocked cycle
        /// emits nothing, bumps <see cref="Route.SkippedCycles"/>, snaps every
        /// stop + the route marker forward to the cycle, and records the hold. The
        /// dispatch half is gated by <see cref="IsDispatchAlreadyInLedger"/> on the
        /// cycle's first-window stop index, so the second tick (window B's) does NOT
        /// re-dispatch.</para>
        ///
        /// <para><b>BLOCKER-1: one dock-cycle per pass.</b> This helper processes
        /// EXACTLY ONE dock-cycle per invocation - the LOWEST owed cycle
        /// <c>cMin</c> across the stops. When a tick straddles more than one owed
        /// cycle (a gap tick with a prior owed window, or a long warp jump), only
        /// <c>cMin</c>'s windows fire this pass; the dispatch/funds charge fires
        /// once for <c>cMin</c>, and Completed/Skipped is bumped exactly once when
        /// <c>cMin</c>'s last dock is reached. That bump advances the
        /// <c>cycle-{C+S}</c> baseline so the NEXT pass computes a FRESH cycleId for
        /// <c>cMin+1</c>. The caller (<see cref="ProcessLoopRoute"/>) re-invokes
        /// while <paramref name="stillDue"/> is true so multiple owed cycles in one
        /// tick are processed in ascending order. The invariant <c>C+S == cMin</c>
        /// holds at the start of every pass, which is what makes the per-window
        /// replay guard keys (<c>RouteId, cycleId, stopIndex</c>) genuinely unique
        /// per cycle - closing the gap-tick guard collision that dropped a whole
        /// cycle's dispatch + a delivery under a frozen single cycleId.</para>
        /// </summary>
        /// <param name="stillDue">On return, true when the route has at least one
        /// owed window of a LATER cycle than the one just processed (so the caller's
        /// catch-up loop should re-invoke). False when no window is owed (terminal:
        /// either nothing was due this pass, or the highest passed dock cycle was
        /// just processed).</param>
        private static void ProcessMultiStopCrossings(
            Route route, double currentUT, IRouteRuntimeEnvironment env,
            GhostPlaybackLogic.LoopUnit unit, double loopUT, long cycleIndex, bool isInInterCycleTail,
            RouteWindowBasis basis, int nResidual,
            ref int dispatched, ref int transitioned, ref int skipped, out bool stillDue)
        {
            stillDue = false;
            int stopCount = route.Stops.Count;

            // The dispatch/debit rows of a multi-stop cycle carry a CYCLE-STABLE
            // carrier stop index (stop 0, the first stop in DockUT order). It MUST
            // be stable across the whole cycle - NOT the "first window due this
            // tick" - so a partial-cycle resume (window 0 fired + persisted; the
            // resume tick has only window 1 due) does NOT re-dispatch under a
            // different stop index. The dispatch fires exactly once per cycleId,
            // guarded by IsDispatchAlreadyInLedger on (cycleId, this carrier index).
            const int dispatchCarrierStopIndex = 0;

            // max dock UT across the route's stops (the cycle's LAST dock).
            double maxDockUT = double.NegativeInfinity;
            for (int i = 0; i < stopCount; i++)
            {
                RouteStop s = route.Stops[i];
                if (s.RecordedDockUT > maxDockUT) maxDockUT = s.RecordedDockUT;
            }

            // BLOCKER-1: scan every stop's owed dock-cycle and find the LOWEST owed
            // cycle cMin. A window is owed when its dock phase has been reached for a
            // cycle index strictly greater than its own LastFiredCycleIndex (the
            // shared owed-crossing emitter, RouteLoopClock.TryGetOwedDockCrossing,
            // gated per stop on the stop's OWN LastFiredCycleIndex - M-MIS-11 item 2
            // keeps this per-stop sub-gate here). We process ONLY cMin this pass:
            // each stop is "due this pass" iff its owed dock-cycle equals cMin AND
            // its dock phase is reached for cMin (sDockCycle == cMin). A later-cycle
            // owed window (sDockCycle > cMin, e.g. an earlier stop already in cMin+1)
            // sets stillDue so the caller re-invokes after the C+S bump advances the
            // cycleId. Stops are walked in DockUT-ascending order (RouteBuilder A1/A2).
            long cMin = long.MaxValue;
            bool anyOwed = false;
            for (int i = 0; i < stopCount; i++)
            {
                RouteStop s = route.Stops[i];
                bool windowCrossing = RouteLoopClock.TryGetOwedDockCrossing(
                    unit, loopUT, cycleIndex, s.RecordedDockUT,
                    s.LastFiredCycleIndex, out RouteLoopClock.OwedDockCrossing sOwed);
                if (!windowCrossing)
                    continue;
                long sDockCycle = sOwed.DockCycleIndex;
                anyOwed = true;
                if (sDockCycle < cMin)
                    cMin = sDockCycle;
            }

            if (!anyOwed)
            {
                skipped++;
                ParsekLog.VerboseRateLimited(Tag, "loop-noncross-multi-" + route.Id,
                    $"LoopRoute(multi): route {ShortIdForLog(route)} stops={stopCount.ToString(IC)} " +
                    $"no due window — loopUT={loopUT.ToString("R", IC)} " +
                    $"cycleIdx={cycleIndex.ToString(IC)} " +
                    $"lastObserved={route.LastObservedLoopCycleIndex.ToString(IC)} " +
                    $"maxDockUT={maxDockUT.ToString("R", IC)} at ut={currentUT.ToString("R", IC)}",
                    5.0);
                return;
            }

            // Collect the windows belonging to cMin (the only cycle fired this pass).
            // A window belongs to cMin when its dock phase has been reached for
            // EXACTLY cMin (its own owed dock-cycle == cMin). dueCount > 0 always
            // here (cMin is the min of a non-empty owed set), and every cMin window
            // has sDockCycle == cMin so all share ONE cycleId.
            var dueStopIndex = new List<int>(stopCount);
            int laterOwed = 0;
            for (int i = 0; i < stopCount; i++)
            {
                RouteStop s = route.Stops[i];
                bool windowCrossing = RouteLoopClock.TryGetOwedDockCrossing(
                    unit, loopUT, cycleIndex, s.RecordedDockUT,
                    s.LastFiredCycleIndex, out RouteLoopClock.OwedDockCrossing sOwed);
                if (!windowCrossing)
                    continue;
                if (sOwed.DockCycleIndex == cMin)
                    dueStopIndex.Add(i);
                else
                    laterOwed++; // a window of cMin+1.. (processed on a later pass)
            }
            int dueCount = dueStopIndex.Count;

            // M5 (D10): the modulo decision is CYCLE-level, taken once per pass
            // on cMin, BEFORE the dispatch guard / escrow reserve below. Live
            // only under the ReaimWindows basis (nResidual > 1, D2). A
            // non-deliverable cMin is skipped ATOMICALLY - the blocked-cycle
            // snap shape MINUS the SkippedCycles bump, the hold, and the rows:
            // every stop's LastFiredCycleIndex and the route marker snap through
            // cMin, the prior cycle's pending recovery credit flushes (OQ3, the
            // skip IS its next crossing), no escrow is reserved, and stillDue
            // reports as usual so a deliverable LATER owed cycle still fires in
            // this tick's catch-up loop. A partially-fired deliverable cycle
            // always IS cMin (its stops' cursors lag there until it completes),
            // so this gate can never strand or leapfrog a half-fired cycle
            // (review Missed #4).
            if (nResidual > 1)
            {
                if (route.WindowAnchorCycleIndex < 0)
                {
                    // D3 anchor adoption: the first owed cycle after creation /
                    // activation / rebase / engage anchors the modulo and
                    // delivers (fall through to the dispatch/fire path below).
                    route.WindowAnchorCycleIndex = cMin;
                    ParsekLog.Info(Tag,
                        $"LoopRoute(multi): route {ShortIdForLog(route)} window-anchor ADOPTED " +
                        $"anchor={cMin.ToString(IC)} (N={nResidual.ToString(IC)} basis={basis}) " +
                        $"- this cycle delivers, every {nResidual.ToString(IC)}th after it");
                }
                else if (!RouteLoopClock.IsDeliverableWindow(
                    cMin, route.WindowAnchorCycleIndex, nResidual))
                {
                    EmitPendingRecoveryCredit(route, currentUT, env);
                    for (int j = 0; j < stopCount; j++)
                    {
                        if (route.Stops[j].LastFiredCycleIndex < cMin)
                            route.Stops[j].LastFiredCycleIndex = cMin;
                    }
                    if (route.LastObservedLoopCycleIndex < cMin)
                        route.LastObservedLoopCycleIndex = cMin;
                    skipped++;
                    stillDue = laterOwed > 0;
                    ParsekLog.VerboseRateLimited(Tag, "loop-modulo-skip-multi-" + route.Id,
                        $"LoopRoute(multi): route {ShortIdForLog(route)} window {cMin.ToString(IC)} " +
                        $"SKIPPED by cadence modulo (N={nResidual.ToString(IC)} " +
                        $"anchor={route.WindowAnchorCycleIndex.ToString(IC)} basis={basis}) " +
                        $"- marker+stops snapped, nothing emitted, stillDue={(stillDue ? "1" : "0")}",
                        5.0);
                    return;
                }
            }

            // The cycle's id. The SAFETY PROPERTY (BLOCKER-1 fix) is that the
            // cycleId is unique per FIRED cycle: CompletedCycles + SkippedCycles is
            // strictly monotonic, bumped exactly once per cycle as each completes /
            // is skipped, and a partially-fired cycle resumes under its EXISTING
            // count-id (the per-window guard keys (RouteId, cycleId, stopIndex)
            // therefore never collide across cycles). NOTE: the stronger equality
            // C+S == cMin holds only for same-tick, NON-warp ascending catch-up
            // passes; across a warp that SKIPS span cycles, cMin (a loop-cycle
            // index) runs ahead of C+S (a fired-cycle COUNT). The fix does NOT rely
            // on that equality - only on per-fired-cycle id uniqueness + correct
            // partial-bucket resume, both of which hold under warp.
            string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles).ToString(IC);

            // True when this tick's loop phase has reached/passed the cycle's LAST
            // dock for CMIN: the cycle is complete this pass. Because we fire only
            // cMin per pass, a tick deep in a later cycle still completes cMin here
            // (its last dock is long past), then the caller re-invokes for cMin+1.
            // A tick that lands in cMin's own (dockA, dockB) gap leaves this false
            // and strands cMin's window B for the next TICK (stillDue stays false -
            // there is no LATER owed cycle, only cMin's own un-reached window).
            bool cycleLastDockReached = loopUT >= maxDockUT
                ? true
                // The last dock is reached for cMin when the highest cMin-window dock
                // phase has been crossed. For the in-gap case all cMin windows whose
                // dock is reached are due; cMin's last dock is reached iff the
                // highest-DockUT stop's owed dock-cycle is also cMin (it is due).
                : IsMaxDockStopDue(route, dueStopIndex, maxDockUT);

            ParsekLog.Verbose(Tag,
                $"LoopRoute(multi): route {ShortIdForLog(route)} cMin={cMin.ToString(IC)} " +
                $"DUE windows={dueCount.ToString(IC)} laterOwed={laterOwed.ToString(IC)} " +
                $"cycle={cycleId} dispatchCarrierStop={dispatchCarrierStopIndex.ToString(IC)} " +
                $"loopUT={loopUT.ToString("R", IC)} maxDockUT={maxDockUT.ToString("R", IC)} " +
                $"lastDockReached={(cycleLastDockReached ? "1" : "0")} " +
                $"lastObserved={route.LastObservedLoopCycleIndex.ToString(IC)} at ut={currentUT.ToString("R", IC)}");

            // Has CMIN's dispatch already fired (a prior tick of the same cycle, or a
            // save/reload)? The dispatch/debit rows carry the cycle-STABLE carrier
            // stop index (stop 0), so this is true on the SECOND tick of a multi-tick
            // cycle (window B) AND on a partial-cycle resume - the carrier index never
            // drifts to a later due window.
            bool dispatchAlready = IsDispatchAlreadyInLedger(route.Id, cycleId, dispatchCarrierStopIndex);

            // Eligibility gates the cycle at DISPATCH only (OQ4: all-or-nothing is
            // at the source gate, NOT mid-cycle delivery). Once the cycle is
            // committed (dispatch fired on an earlier tick), the later windows fire
            // unconditionally - a witnessed delivery to A is not discarded because a
            // later eligibility re-check would fail. So skip the eligibility gate
            // entirely on a dispatch-already cycle.
            if (!dispatchAlready)
            {
                RouteDispatchEvaluator.EligibilityResult elig =
                    RouteDispatchEvaluator.CheckEligibility(route, currentUT, env);

                if (!elig.Eligible)
                {
                    // Blocked cycle cMin (NOT yet committed): emit NOTHING for any
                    // window. Flush any prior dispatched cycle's owed recovery credit
                    // (the blocked crossing IS the next crossing for it). Bump
                    // SkippedCycles ONCE (C+S advances to cMin+1 for the next pass),
                    // and ATOMICALLY skip the WHOLE blocked cycle by snapping EVERY
                    // stop not yet fired through cMin forward to cMin (OQ4 all-or-
                    // nothing-at-dispatch / OQ3 snap-every-stop). The unconditional
                    // `< cMin` snap is required for correctness, NOT just the windows
                    // dock-crossed THIS tick: when the block tick lands in cMin's own
                    // (dockA, dockB) gap, dock B's stop has not dock-crossed yet, but
                    // it MUST still be marked skipped for cMin - otherwise, if the
                    // block clears before the ghost reaches dock B, dock B would fire
                    // next tick as a SEPARATELY-dispatched delivery under a fresh
                    // cycleId, splitting one logically-skipped cycle into
                    // "dock A skipped" + "dock B dispatched + delivered" (an
                    // unaffordable cycle the player's resources never supported).
                    // Safe with laterOwed: a stop owed at cMin+1 has LastFired == cMin
                    // (it fired/skipped cMin already), so `cMin < cMin` is false and
                    // its cMin+1 obligation survives for the next catch-up pass.
                    EmitPendingRecoveryCredit(route, currentUT, env);
                    route.SkippedCycles += 1;
                    for (int j = 0; j < stopCount; j++)
                    {
                        if (route.Stops[j].LastFiredCycleIndex < cMin)
                            route.Stops[j].LastFiredCycleIndex = cMin;
                    }
                    if (route.LastObservedLoopCycleIndex < cMin)
                        route.LastObservedLoopCycleIndex = cMin;
                    route.RecordHold(elig.Kind, elig.Reason, elig.Shortfall, currentUT);
                    skipped++;
                    stillDue = laterOwed > 0;
                    ParsekLog.Info(Tag,
                        $"LoopRoute(multi): route {ShortIdForLog(route)} cycle={cycleId} cMin={cMin.ToString(IC)} " +
                        $"BLOCKED kind={elig.Kind} reason={elig.Reason ?? "<none>"} " +
                        $"shortfall={elig.Shortfall.ToString("R", IC)} — emitted nothing for {dueCount.ToString(IC)} " +
                        $"cMin window(s), snapped due stops+lastObserved={cMin.ToString(IC)} " +
                        $"skippedCycles={route.SkippedCycles.ToString(IC)} stillDue={(stillDue ? "1" : "0")}");
                    return;
                }

                // Eligible + first commit of the cycle: clear any prior hold BEFORE
                // emitting (a per-window endpoint loss can record an EndpointLost
                // hold inside the delivery half; one clear here covers it).
                route.ClearHold("crossing-eligible");

                // Dispatch half (once per cycle): origin/funds debit + the
                // RouteDispatched/RouteCargoDebited pair under the cycle's carrier
                // stop index. The dispatch ELS guard suppresses a re-dispatch on the
                // SECOND tick (window B) / on a save-reload resume.
                EmitDispatchDebit(route, currentUT, env, cycleId,
                    applyPhysicalOriginDebit: true, dispatchStopIndex: dispatchCarrierStopIndex);

                // M4b B3 (plan D11 / OQ7): RESERVE this cycle's per-source cargo escrow
                // at dispatch (once per cycle, post-eligibility, pre-emit). The summed
                // per-pid reservation holds across the dispatch-to-window-phase gap so a
                // competing route's B1 gate sees it; each pickup window RELEASEs its own
                // portion at its physical debit (or on a window endpoint loss), netting
                // the reservation to zero by cycle end (the leak-free invariant).
                ReserveCycleEscrow(route, currentUT, env, cycleId);

                if (env != null && env.IsCareer && route.IsKscOrigin)
                {
                    route.PendingRecoveryCreditCycleId = cycleId;
                    route.PendingRecoveryCreditDispatchUT = currentUT;
                    ParsekLog.Verbose(Tag,
                        $"LoopRoute(multi): route {ShortIdForLog(route)} armed pending recovery credit " +
                        $"cycle={cycleId} dispatchUT={currentUT.ToString("R", IC)}");
                }
                // M4c Phase C1 (plan D12 / OQ8): the cycle's once-per-cycle dispatch
                // just fired (this is the !dispatchAlready first-commit branch), so
                // consume the partner's current cycle. A dispatchAlready resume (the
                // else branch below) does NOT re-advance - the cycle's dispatch was
                // already counted on its first tick.
                AdvancePartnerAlternationOnDispatch(route, currentUT);
                dispatched++;
                ParsekLog.Info(Tag,
                    $"LoopRoute(multi): route {ShortIdForLog(route)} cycle={cycleId} cMin={cMin.ToString(IC)} " +
                    $"dispatch fired (carrierStopIndex={dispatchCarrierStopIndex.ToString(IC)}) " +
                    $"at ut={currentUT.ToString("R", IC)}");
            }
            else
            {
                // Cycle already committed (dispatch in the ledger): fire only the due
                // window(s), no eligibility re-gate. The crossing is eligible by
                // construction (the cycle dispatched), so clear any stale hold; the
                // prior cycle's owed recovery credit still flushes on this crossing.
                route.ClearHold("crossing-eligible");
                EmitPendingRecoveryCredit(route, currentUT, env);

                // M4b B3 C1 (plan OQ7/D11 "recomputed from pending state on the next
                // Tick"): a dispatchAlready resume of an IN-FLIGHT multi-stop cycle
                // re-establishes the escrow for this cycle's still-UN-FIRED pickup
                // windows when the escrow was CLEARED mid-cycle (a scene-switch
                // ClearAllEscrow or a reload dropped the RAM-only map). Idempotent:
                // a no-op when the route already holds escrow (a normal in-session
                // resume - no clear happened), so it never double-reserves. The C2
                // cycle-complete drop sweeps it when the cycle finishes.
                ReEstablishEscrowForUnfiredWindows(route, currentUT, env, cycleId, cMin);

                ParsekLog.Verbose(Tag,
                    $"LoopRoute(multi): route {ShortIdForLog(route)} cycle={cycleId} cMin={cMin.ToString(IC)} " +
                    $"dispatch already in ledger (committed) - firing only the due window(s), no re-gate");
            }

            // Fire each DUE window of cMin's pickup (+ delivery), in DockUT order.
            // Each window carries its OWN stop index (drives ResolveCycleStop +
            // RouteHas*Manifest via PendingStopIndex, the Sequence stride, and the
            // per-window delivery ELS guard). No window bumps CompletedCycles — the
            // once-per-cycle bump fires below when the last dock is reached.
            int firedWindows = 0;
            for (int d = 0; d < dueStopIndex.Count; d++)
            {
                int stopIdx = dueStopIndex[d];

                // Drive PendingStopIndex so ResolveCycleStop / RouteHasDeliveryManifest /
                // RouteHasPickupManifest resolve THIS window's stop (C5 - the helpers
                // need no rewrite).
                route.PendingStopIndex = stopIdx;

                EmitMultiStopWindow(route, currentUT, env, cycleId, stopIdx,
                    out bool windowEmitted);

                // Snap THIS stop's fire index forward to cMin (fire-once across
                // reload). Every due window of this pass belongs to cMin.
                if (route.Stops[stopIdx].LastFiredCycleIndex < cMin)
                    route.Stops[stopIdx].LastFiredCycleIndex = cMin;

                if (windowEmitted)
                    firedWindows++;
                transitioned++;
            }

            // PendingStopIndex is a within-tick scratch for the helpers; clear it so
            // a later non-loop read never sees a stale value.
            route.PendingStopIndex = -1;

            // Deferred route-level cycle-completion snap (OQ3) + the once-per-cycle
            // Completed bump (BLOCKER-1): advance the route marker + COUNT cMin ONLY
            // when cMin's last dock has been reached this pass. The CompletedCycles
            // bump lives HERE (not in any per-window applier) so it fires exactly once
            // per multi-stop cycle regardless of which half(s) the windows emitted,
            // and it advances C+S to cMin+1 so the next catch-up pass computes a fresh
            // cycleId.
            if (cycleLastDockReached)
            {
                route.CompletedCycles += 1;
                if (route.LastObservedLoopCycleIndex < cMin)
                    route.LastObservedLoopCycleIndex = cMin;

                // M4b B3 C2 (close the reserve-pid != release-pid leak robustly): at
                // cycle-complete EVERY window has fired (debited+released) or been
                // skipped, so ANY residual reservation for this route is stale - e.g.
                // a window whose resolved release pid diverged from the dispatch-time
                // reserve pid (a mid-cycle source-identity change / surface-fallback
                // / craft-baked-pid regeneration), where the release missed the
                // reserved key. Dropping the whole route's escrow here guarantees no
                // positive-residual leak regardless of pid divergence. Safe because a
                // loop route is single-instance (cadence >= span, only one cycle in
                // flight). This also drops the C1 re-established reservation. A no-op
                // when nothing is held (the normal leak-free path already netted to
                // zero).
                RouteStore.DropRouteEscrow(route.Id);

                // A LATER owed cycle remains -> ask the caller to re-invoke. (cMin's
                // own un-fired windows are all reached this pass, so the only
                // remaining work is cMin+1's windows.)
                stillDue = laterOwed > 0;
                ParsekLog.Info(Tag,
                    $"LoopRoute(multi): route {ShortIdForLog(route)} cycle={cycleId} cMin={cMin.ToString(IC)} " +
                    $"last dock reached — firedWindows={firedWindows.ToString(IC)} " +
                    $"snapped lastObserved={cMin.ToString(IC)} completedCycles={route.CompletedCycles.ToString(IC)} " +
                    $"stillDue={(stillDue ? "1" : "0")}");
            }
            else
            {
                // cMin's last dock NOT yet reached this tick (the ghost is between
                // cMin's dock A and dock B): the cycle is NOT counted, the route
                // marker is held, and there is no LATER owed cycle (a window of cMin+1
                // cannot be due before cMin's last dock). stillDue stays false: cMin's
                // remaining windows fire on a future TICK, not a same-tick re-pass.
                stillDue = false;
                ParsekLog.Info(Tag,
                    $"LoopRoute(multi): route {ShortIdForLog(route)} cycle={cycleId} cMin={cMin.ToString(IC)} " +
                    $"partial — firedWindows={firedWindows.ToString(IC)} (more windows pending this cycle); " +
                    $"lastObserved held at {route.LastObservedLoopCycleIndex.ToString(IC)} " +
                    $"completedCycles={route.CompletedCycles.ToString(IC)}");
            }
        }

        /// <summary>
        /// (M4a A3 / BLOCKER-1) True when the stop carrying the route's LAST dock
        /// (max <c>RecordedDockUT</c>) is among the windows due THIS pass
        /// (<paramref name="dueStopIndex"/>). Used only on the in-gap branch
        /// (<c>loopUT &lt; maxDockUT</c> is false on the fast path): a cycle's last
        /// dock is reached this pass iff the max-DockUT stop's owed dock-cycle is the
        /// cycle being processed (so it is due). When the last-dock stop is NOT due
        /// this pass (a true mid-cycle gap tick), the cycle is left open and its
        /// remaining windows fire on a future tick. Pure.
        /// </summary>
        private static bool IsMaxDockStopDue(
            Route route, List<int> dueStopIndex, double maxDockUT)
        {
            // Find the stop index that carries the max dock UT.
            int maxStopIdx = -1;
            for (int i = 0; i < route.Stops.Count; i++)
            {
                if (route.Stops[i].RecordedDockUT == maxDockUT)
                {
                    maxStopIdx = i;
                    break;
                }
            }
            if (maxStopIdx < 0)
                return false;
            return dueStopIndex.Contains(maxStopIdx);
        }

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

        /// <summary>Clears the per-session transition-thrash counters (test isolation).</summary>
        internal static void ResetWindowBasisTransitionCountsForTesting()
        {
            windowBasisTransitionCounts.Clear();
        }

        /// <summary>
        /// (M5 D6) Applies the tick's window-basis transition to
        /// <paramref name="route"/>: evaluates the pure
        /// <see cref="RouteLoopClock.EvaluateWindowBasisTransition"/> decision
        /// from the persisted flip-detector marker + the derived
        /// <paramref name="basis"/>, and on a flip RE-BASELINES the cycle
        /// cursors between the flat and window index spaces:
        /// <list type="bullet">
        ///   <item><b>Engage</b> (marker false, basis ReaimWindows): set the
        ///     marker, reset the anchor, and re-baseline
        ///     <see cref="Route.LastObservedLoopCycleIndex"/> to the current
        ///     dock cycle index MINUS ONE (per-stop
        ///     <c>LastFiredCycleIndex</c> re-baselined the same way against each
        ///     stop's OWN dock UT) - exactly the CURRENT window is left owed, so
        ///     the first crossing after engage still delivers via D3 anchor
        ///     adoption. Without this, a stale flat-space cursor (large: flat
        ///     cadence = days) exceeds every future synodic index (small:
        ///     ~synodic cadence) and <c>TryGetOwedDockCrossing</c> never emits
        ///     again - a PERMANENT silent skip (review C6, the blocker).</item>
        ///   <item><b>Decline</b> (marker true, basis NOT ReaimWindows): clear
        ///     the marker, reset the anchor, and re-baseline the cursors to the
        ///     CURRENT dock cycle index in the new (flat) space with NO fire
        ///     this tick - fail-closed: a stale window-space cursor compared
        ///     against flat indices reads as a huge owed jump and would fire a
        ///     delivery the player never scheduled (the mis-fire the milestone
        ///     forbids). The whole warp span is consumed without a fire by
        ///     design (a decline is a property of the BUILD, not a window).</item>
        /// </list>
        /// Per-stop snap values are computed against each stop's OWN
        /// <c>RecordedDockUT</c> (falling back to the route-level dock UT for a
        /// stop without one) so no stop is left owed (Decline) / more than the
        /// current window owed (Engage) regardless of where the tick lands
        /// between docks. Cold-load belt (BUG-F class): never evaluates at
        /// <paramref name="currentUT"/> &lt;= 0. Returns the applied kind
        /// (<see cref="WindowBasisTransitionKind.None"/> when nothing changed).
        /// Wired into <see cref="ProcessLoopRoute"/> (P2); internal static for
        /// direct xUnit coverage.
        /// </summary>
        internal static WindowBasisTransitionKind ApplyWindowBasisTransition(
            Route route, RouteWindowBasis basis, double loopUT, long cycleIndex, double currentUT)
        {
            if (route == null)
                return WindowBasisTransitionKind.None;

            // Cold-load belt: Planetarium UT 0 means "not a real tick yet"
            // (BUG-F class). The production Update accumulator never delivers a
            // UT<=0 tick, but the transition re-baseline is destructive enough
            // to warrant the explicit gate here too.
            if (currentUT <= 0.0)
            {
                ParsekLog.VerboseRateLimited(Tag, "basis-transition-ut0-" + route.Id,
                    $"WindowBasis: route {ShortIdForLog(route)} transition evaluation skipped " +
                    $"at ut={currentUT.ToString("R", IC)} (cold-load gate)", 5.0);
                return WindowBasisTransitionKind.None;
            }

            WindowBasisTransitionKind kind = RouteLoopClock.EvaluateWindowBasisTransition(
                route.ReaimWindowBasisEngaged, basis);
            if (kind == WindowBasisTransitionKind.None)
                return kind;

            long prevObserved = route.LastObservedLoopCycleIndex;
            long prevAnchor = route.WindowAnchorCycleIndex;
            long routeDockCycle = RouteLoopClock.ComputeDockCycleIndex(
                loopUT, cycleIndex, route.RecordedDockUT);

            // Engage owes exactly the current window (dock - 1); Decline consumes
            // the current index (dock) so nothing is owed this tick. FLOOR at -1
            // ("nothing consumed yet"): before the FIRST dock of the phase
            // anchor's window 0 has passed, ComputeDockCycleIndex reports -1 and
            // an unfloored Engage would set -2, making a nonexistent "window -1"
            // owed and firing PRE-dock.
            long offset = kind == WindowBasisTransitionKind.Engage ? -1L : 0L;
            long rebased = routeDockCycle + offset;
            if (rebased < -1L)
                rebased = -1L;
            route.LastObservedLoopCycleIndex = rebased;
            route.WindowAnchorCycleIndex = -1;
            route.ReaimWindowBasisEngaged = kind == WindowBasisTransitionKind.Engage;

            // Per-stop cursors re-baseline against each stop's OWN dock phase so
            // the multi-stop sub-gates land in the same index space (a stop
            // without a per-stop dock UT - the single-stop shape - snaps against
            // the route-level dock).
            int stopsSnapped = 0;
            if (route.Stops != null)
            {
                for (int i = 0; i < route.Stops.Count; i++)
                {
                    RouteStop s = route.Stops[i];
                    if (s == null) continue;
                    double stopDockUT = s.RecordedDockUT >= 0.0 ? s.RecordedDockUT : route.RecordedDockUT;
                    long stopRebased = RouteLoopClock.ComputeDockCycleIndex(
                        loopUT, cycleIndex, stopDockUT) + offset;
                    if (stopRebased < -1L)
                        stopRebased = -1L;
                    s.LastFiredCycleIndex = stopRebased;
                    stopsSnapped++;
                }
            }

            ParsekLog.Info(Tag,
                $"WindowBasis: route {ShortIdForLog(route)} {(kind == WindowBasisTransitionKind.Engage ? "ENGAGED" : "DECLINED")} " +
                $"ReaimWindows basis (basis={basis}) - re-baselined into {(kind == WindowBasisTransitionKind.Engage ? "window" : "flat")} space: " +
                $"lastObserved {prevObserved.ToString(IC)}->{route.LastObservedLoopCycleIndex.ToString(IC)} " +
                $"(dockCycleIdx={routeDockCycle.ToString(IC)}) anchor {prevAnchor.ToString(IC)}->-1 " +
                $"stopsSnapped={stopsSnapped.ToString(IC)} at ut={currentUT.ToString("R", IC)}");

            // Thrash diagnostic: >2 transitions for one route in a session means
            // the builder verdict is alternating (degraded bodyInfo flicker, a
            // mutating tree, or a bug) - Warn once per route per threshold cross.
            windowBasisTransitionCounts.TryGetValue(route.Id ?? string.Empty, out int count);
            count++;
            windowBasisTransitionCounts[route.Id ?? string.Empty] = count;
            if (count > 2)
            {
                ParsekLog.WarnRateLimited(Tag, "basis-thrash-" + route.Id,
                    $"WindowBasis: route {ShortIdForLog(route)} has flipped basis {count.ToString(IC)} times " +
                    "this session - builder verdict thrash (degraded bodyInfo / mutating tree?)", 30.0);
            }
            return kind;
        }

        /// <summary>
        /// (M4a A3) Emits ONE window of a multi-stop cycle: the pickup half (when
        /// the stop carries a witnessed pickup manifest) then the delivery half
        /// (when the stop carries a delivery manifest), under the cycle's shared
        /// <paramref name="cycleId"/> and this window's <paramref name="stopIndex"/>.
        /// The pickup-before-delivery order mirrors the single-stop
        /// <see cref="EmitLoopCycle"/> (endpoint DEBIT before endpoint CREDIT so a
        /// same-resource deliver+pickup nets without a phantom clamp).
        ///
        /// <para><b>CompletedCycles is NOT bumped here.</b> Every multi-stop window
        /// passes <c>bumpCompletedCycle=false</c> to <see cref="ApplyDelivery"/>;
        /// the once-per-cycle bump is owned by
        /// <see cref="ProcessMultiStopCrossings"/> (fired after the last window,
        /// independent of which half a window emits), so a pickup-only last window
        /// still completes the cycle.</para>
        /// </summary>
        private static void EmitMultiStopWindow(
            Route route, double currentUT, IRouteRuntimeEnvironment env,
            string cycleId, int stopIndex, out bool windowEmitted)
        {
            windowEmitted = false;

            // Pickup half (endpoint DEBIT first). A pure-delivery stop has no pickup
            // manifest and skips this. M4b B3 LIFTED the M4a defer: each pickup
            // window's source debit now fires here via EmitPickupHalf (PendingStopIndex
            // is driven to THIS window's stop index by the caller, so ResolveCycleStop
            // resolves THIS stop's PickupManifest), under the windowed C-2 replay key
            // IsPickupAlreadyInLedger((RouteId, cycleId, stopIndex)) - so a >1
            // source-debiting (pickup) multi-window run fires each window's source
            // debit at its own dock phase, idempotent per window across reload. The
            // single bidirectional M3a window is byte-behaviour-identical (its single
            // pickup stop still fires exactly this path). The reserve/release escrow
            // (ReserveCycleEscrow at dispatch / ReleaseWindowEscrow inside EmitPickupHalf)
            // holds each window's source contribution between dispatch and its debit.
            if (RouteHasPickupManifest(route))
            {
                EmitPickupHalf(route, currentUT, env, cycleId);
                windowEmitted = true;
            }

            // Delivery half (endpoint CREDIT). Skip for a stop with no delivery
            // manifest (the per-window ELS guard inside ApplyDelivery keys on
            // (routeId, cycleId, stopIndex), so window 0's row never suppresses
            // window 1). bumpCompletedCycle=false for EVERY multi-stop window — the
            // cycle-complete bump is fired once by the caller.
            if (RouteHasDeliveryManifest(route))
            {
                var deliveryApplier = DeliveryApplierForTesting;
                if (deliveryApplier != null)
                    deliveryApplier(route, currentUT, env);
                else
                    ApplyDelivery(route, currentUT, env, bumpCompletedCycle: false);
                windowEmitted = true;
            }
        }

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
        /// <para>Pure RAM (no ledger row); the per-cycle reserve is idempotent in
        /// practice because dispatch fires exactly once per cycle (the carrier-stop ELS
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
        /// already in the ledger, so <see cref="ReserveCycleEscrow(Route, double, IRouteRuntimeEnvironment, string)"/>
        /// at dispatch does NOT re-run and the un-fired window's hold would be silently
        /// lost - a competing route could then drain the source in the gap (violating
        /// 19.2.5 strand-protection). This re-reserves ONLY the windows of cycle
        /// <paramref name="cycleIndex"/> whose <see cref="RouteStop.LastFiredCycleIndex"/>
        /// is still <c>&lt; cycleIndex</c> (a window already debited+released this cycle
        /// has its hold consumed and must NOT be re-reserved, else double).
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
        /// (M5 D8) READ-ONLY next-dispatch-WINDOW countdown accessor for the
        /// Logistics window. Mirrors <see cref="TryComputeSecondsToNextDockCrossing"/>'s
        /// discipline (throttled by the ~1 Hz legibility cache, mutates NOTHING,
        /// never leaks a <see cref="GhostPlaybackLogic.LoopUnit"/> or a raw
        /// committed list) but serves the WINDOWED bases the flat H1 helper
        /// deliberately refuses: for <see cref="RouteWindowBasis.ZeroDriftSchedule"/>
        /// the next scheduled launch (<c>RelaunchSchedule.NextLaunchAfter</c>);
        /// for <see cref="RouteWindowBasis.ReaimWindows"/> the launch of the next
        /// DELIVERABLE window - the smallest k with
        /// <c>unit.PhaseAnchorUT + k * unit.CadenceSeconds &gt; nowUT</c> that the
        /// residual modulo (anchor + N, D2/D3) admits. The countdown targets the
        /// window LAUNCH, not the dock instant (per-launch knob timings / loiter
        /// cuts make the exact dock offset builder-internal; the launch is the
        /// honest, stable number - label it so, D8).
        ///
        /// <para><b>Field-source pin (review Missed #2).</b> ALL window
        /// arithmetic here reads <c>unit.PhaseAnchorUT</c> /
        /// <c>unit.CadenceSeconds</c>, NEVER raw <c>ReaimSchedule</c> fields:
        /// pad-align updates <c>sched.FirstDepartureUT</c> / <c>CadenceSeconds</c>
        /// but NOT <c>sched.PhaseAnchorUT</c>, and the unit anchor additionally
        /// carries the loiter-compression cutBeforeDeparture shift - raw sched
        /// fields desynchronize k from the firing index space.</para>
        ///
        /// <para><paramref name="basis"/> + <paramref name="targetBody"/> are
        /// populated whenever the unit resolves (even on a false return) so the
        /// caller can label the row; a <see cref="RouteWindowBasis.FlatInterval"/>
        /// route returns false and the caller keeps the existing flat dock
        /// countdown - flat rows render byte-identically.</para>
        /// </summary>
        /// <param name="route">The route to count down for.</param>
        /// <param name="nowUT">Current game UT.</param>
        /// <param name="seconds">Seconds until the next deliverable dispatch
        /// window's launch (&gt; 0 on success); 0 on a false return.</param>
        /// <param name="basis">The derived window basis (FlatInterval when the
        /// route is not ghost-driving / has no resolvable unit).</param>
        /// <param name="targetBody">The re-aim plan's target body for the basis
        /// label ("(Duna transfer)"), or null.</param>
        /// <returns>True when a finite windowed launch countdown exists.</returns>
        internal static bool TryComputeSecondsToNextDispatchWindow(
            Route route, double nowUT, out double seconds,
            out RouteWindowBasis basis, out string targetBody)
        {
            seconds = 0.0;
            basis = RouteWindowBasis.FlatInterval;
            targetBody = null;

            if (route == null)
                return false;

            // Status gate: only ghost-driving routes run the loop clock (mirrors
            // the H1 accessor, whose rate-limited log already names the status).
            if (!RouteStatusPolicy.GhostDriving(route.Status))
                return false;

            GhostPlaybackLogic.LoopUnit? unitOpt = ResolveLoopUnit(route, nowUT);
            if (!unitOpt.HasValue)
                return false;
            GhostPlaybackLogic.LoopUnit unit = unitOpt.Value;

            basis = RouteLoopClock.DeriveWindowBasis(unit);
            if (basis == RouteWindowBasis.ReaimWindows && unit.ReaimPlan.HasValue)
                targetBody = unit.ReaimPlan.Value.TargetBody;

            switch (basis)
            {
                case RouteWindowBasis.ZeroDriftSchedule:
                {
                    // Non-uniform launch UTs: resolve through the schedule only
                    // (the flat arithmetic is INVALID for a Scheduled index).
                    double next = unit.RelaunchSchedule.NextLaunchAfter(nowUT);
                    if (double.IsNaN(next) || next <= nowUT)
                        return false;
                    seconds = next - nowUT;
                    ParsekLog.VerboseRateLimited(Tag, "next-window-" + route.Id,
                        $"NextDispatchWindow: route {ShortIdForLog(route)} basis=ZeroDriftSchedule " +
                        $"nextLaunchUT={next.ToString("R", IC)} seconds={seconds.ToString("R", IC)} " +
                        $"at ut={nowUT.ToString("R", IC)}", 5.0);
                    return true;
                }

                case RouteWindowBasis.ReaimWindows:
                {
                    // Field-source pin: unit anchor + unit cadence ONLY.
                    double anchorUT = unit.PhaseAnchorUT;
                    double cadence = unit.CadenceSeconds;
                    if (double.IsNaN(cadence) || cadence <= 0.0)
                        return false;

                    // Smallest window k whose launch is strictly after now.
                    long k = (long)Math.Floor((nowUT - anchorUT) / cadence) + 1;
                    if (k < 0)
                        k = 0;

                    // Advance to the next DELIVERABLE window under the residual
                    // modulo. An unset anchor (-1) means the next crossing
                    // adopts and delivers (D3), so k stands as-is.
                    int nResidual = RouteLoopClock.ResolveResidualCadence(
                        basis, route.CadenceMultiplier);
                    if (nResidual > 1 && route.WindowAnchorCycleIndex >= 0)
                    {
                        long m = (k - route.WindowAnchorCycleIndex) % nResidual;
                        if (m < 0)
                            m += nResidual;
                        if (m != 0)
                            k += nResidual - m;
                    }

                    double launchUT = anchorUT + k * cadence;
                    if (launchUT <= nowUT)
                        return false;
                    seconds = launchUT - nowUT;
                    ParsekLog.VerboseRateLimited(Tag, "next-window-" + route.Id,
                        $"NextDispatchWindow: route {ShortIdForLog(route)} basis=ReaimWindows " +
                        $"window={k.ToString(IC)} launchUT={launchUT.ToString("R", IC)} " +
                        $"seconds={seconds.ToString("R", IC)} " +
                        $"(N={nResidual.ToString(IC)} anchor={route.WindowAnchorCycleIndex.ToString(IC)} " +
                        $"target={targetBody ?? "<none>"}) at ut={nowUT.ToString("R", IC)}", 5.0);
                    return true;
                }

                default:
                    // FlatInterval: the existing flat dock countdown owns it.
                    return false;
            }
        }

        /// <summary>
        /// Resolves the route's backing-mission loop unit for this tick. Routes
        /// through the <see cref="LoopUnitResolverForTesting"/> seam when set;
        /// otherwise builds it from live KSP state via the first-class Missions
        /// entry point (<c>MissionLoopUnitBuilder.TryBuildLoopUnitForSelection</c>,
        /// M-MIS-11 item 1 - the same pipeline the host push seams'
        /// <c>Build</c> runs for a one-element list) over the SAME
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
        ///
        /// <para><b>(M-MIS-11 item 1) Signature-gated cache.</b> The full builder
        /// pipeline used to re-run on EVERY orchestrator tick and again per
        /// Logistics-window countdown call. The built unit is now cached on the
        /// route (runtime-only fields, <c>RouteCodec</c> untouched) and reused
        /// while BOTH cache keys hold: the one-element
        /// <c>MissionLoopUnitBuilder.BuildSignature</c> (the SAME change-detection
        /// signature every scene render driver uses, covering the mission fields,
        /// tree counts, committed-list identity, auto-loop setting, body-geometry
        /// + station-anchor digests, and rotation mode) and the M-MIS-9
        /// <c>RouteBackingMission.ComputeTopologySignature</c> of the backing tree
        /// (id-level rolling hashes that catch count-neutral tree mutations the
        /// builder signature's counts miss). Any input drift the old
        /// rebuild-every-tick code would have picked up moves one of the two keys
        /// and forces a rebuild; a cached NULL outcome (builder yielded no unit)
        /// is reused the same way. Rebuilds log Verbose with the change reason;
        /// steady-state hits log nothing.</para>
        /// </summary>
        internal static GhostPlaybackLogic.LoopUnit? ResolveLoopUnit(Route route, double currentUT)
        {
            var resolver = LoopUnitResolverForTesting;
            if (resolver != null)
                return resolver(route, currentUT);

            // Live build. Construct the route's backing Mission (cheap: the
            // M-MIS-9 auto-exclude derivation inside is already signature-gated),
            // then resolve the unit through the signature-gated cache below. The
            // build gates on Mission.LoopPlayback (set by BuildMission).
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

            // (M-MIS-11 item 1) Cache keys. The builder signature is the SAME
            // sanctioned change-detection fold the render drivers gate their
            // per-frame Build on; the topology signature is the SAME M-MIS-9
            // scheme BuildMission's auto-exclude cache uses (id-level hashes
            // covering count-neutral tree mutations the builder signature's
            // count folds miss). Computing both per tick is the render seams'
            // established per-frame cost; the FULL builder pipeline only runs on
            // a key change.
            var missionList = new List<Mission> { mission };
            string builderSignature = MissionLoopUnitBuilder.BuildSignature(
                missionList, trees, committed, autoLoopIntervalSeconds, bodyInfo, tbrMode);
            string topologySignature = RouteBackingMission.ComputeTopologySignature(
                RouteTreeGuard.FindCommittedTree(route.BackingMissionTreeId));

            bool cachePrimed = route.LoopUnitBuilderSignature != null;
            bool builderKeyHolds = cachePrimed && string.Equals(
                builderSignature, route.LoopUnitBuilderSignature, StringComparison.Ordinal);
            bool topologyKeyHolds = cachePrimed && string.Equals(
                topologySignature, route.LoopUnitTopologySignature, StringComparison.Ordinal);
            if (builderKeyHolds && topologyKeyHolds)
                return route.CachedLoopUnit; // steady-state hit: no build, no log

            string rebuildReason = !cachePrimed
                ? "first-build"
                : !builderKeyHolds ? "builder-inputs-changed" : "tree-topology-changed";

            // Suppress the pure-derivation diagnostic logs the builder pipeline emits
            // (BuildMissionStructure / ExtractConstraints / Solve / ReaimDiag /
            // MissionLoopUnit / PhaseLock). This resolver runs on EVERY delivery-clock
            // tick (ProcessLoopRoute) and every Logistics-window OnGUI frame
            // (ComputeRouteLegibility -> TryComputeSecondsToNextDockCrossing); the
            // signature cache above collapses steady-state calls, but a genuinely
            // changing input set (e.g. an in-progress tree gaining recordings) can
            // still rebuild often, so an un-suppressed build would flood the log
            // with a near-static verdict. The LoopUnit computed is byte-identical -
            // only the diagnostic output is gated (mirrors MissionsWindowUI's
            // display-mirror build). These flags gate Verbose/Info plus two
            // pre-existing MissionPeriodicity diagnostic Warns (degenerate-period
            // filter; over-constrained Tier-1 residual); the MissionLoopUnitBuilder
            // owner/member collision Warns stay un-gated. Silencing the
            // MissionPeriodicity Warns here is intentional - at Warn they ignore
            // verbose-off and would flood, and the signature-gated render build
            // (DriveMissionLoopUnits) plus the Missions window still surface them
            // un-suppressed for the same config.
            bool prevStructSuppress = MissionStructureBuilder.SuppressLogging;
            bool prevPeriodicitySuppress = MissionPeriodicity.SuppressLogging;
            bool prevLoopSuppress = MissionLoopUnitBuilder.SuppressLogging;
            MissionStructureBuilder.SuppressLogging = true;
            MissionPeriodicity.SuppressLogging = true;
            MissionLoopUnitBuilder.SuppressLogging = true;
            bool built;
            GhostPlaybackLogic.LoopUnit unit;
            try
            {
                built = MissionLoopUnitBuilder.TryBuildLoopUnitForSelection(
                    mission, trees, committed, autoLoopIntervalSeconds, bodyInfo, tbrMode, out unit);
            }
            finally
            {
                MissionStructureBuilder.SuppressLogging = prevStructSuppress;
                MissionPeriodicity.SuppressLogging = prevPeriodicitySuppress;
                MissionLoopUnitBuilder.SuppressLogging = prevLoopSuppress;
            }

            route.CachedLoopUnit = built ? unit : (GhostPlaybackLogic.LoopUnit?)null;
            route.LoopUnitBuilderSignature = builderSignature;
            route.LoopUnitTopologySignature = topologySignature;
            // Rate-limited per route+reason: a quantization-boundary thrash (loaded
            // anchor under thrust straddling the 1s period floor) rebuilds with a
            // constant reason on every Tick and Logistics-window OnGUI frame; the
            // reason in the key keeps distinct transitions individually visible.
            ParsekLog.VerboseRateLimited(Tag,
                "loop-unit-cache-rebuild-" + route.Id + "-" + rebuildReason,
                $"ResolveLoopUnit: route {ShortIdForLog(route)} loop-unit cache rebuilt " +
                $"reason={rebuildReason} built={(built ? "1" : "0")} " +
                $"tree={route.BackingMissionTreeId ?? "<null>"} at ut={currentUT.ToString("R", IC)}");
            return route.CachedLoopUnit;
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

            // ELS idempotency backstop (must-fix #4 + M3 OQ4 re-key).
            // LastObservedLoopCycleIndex (persisted via the Phase 1 codec) is the
            // PRIMARY re-fire guard, but a save/reload mid-cycle, a Rewind, or a
            // double-tick can re-present the SAME cycleId. EmitDispatchDebit has no
            // idempotency guard of its own, so without this check a replayed cycle
            // would emit ORPHAN RouteDispatched/RouteCargoDebited rows AND re-apply
            // the per-window pickup endpoint debit.
            //
            // M3 OQ4 re-key (the correctness fix): the guard is keyed on the
            // DIRECTION-AGNOSTIC RouteDispatched row (every cycle emits exactly one),
            // NOT the RouteCargoDelivered row (which a pickup-ONLY route never emits,
            // D6). Keying on delivery would let a pickup-only route's endpoint debit
            // re-apply every reload (risk register #1). Check (routeId, cycleId) on
            // the dispatch row up front and emit NOTHING on a replay - the caller
            // still snaps LastObservedLoopCycleIndex forward.
            // M4a A3: EmitLoopCycle is the SINGLE-STOP full-cycle emitter (the
            // multi-stop cycle runs through ProcessMultiStopCrossings /
            // EmitMultiStopWindow). Its dispatch row carries the v0 sentinel stop
            // index -1, so the re-keyed dispatch guard is checked against -1
            // (byte-identical to pre-A3: one dispatch per cycle, stopIndex -1).
            if (IsDispatchAlreadyInLedger(route.Id, cycleId, -1))
            {
                // Mirror ApplyDelivery's replay branch: bump CompletedCycles so the
                // NEXT cycle's id (cycle-{Completed+Skipped}) advances past this
                // already-dispatched one. PRESERVE this bump (OQ4): without it a
                // pickup-only route's next crossing would recompute the same
                // already-in-ledger cycleId and replay-skip FOREVER (the route would
                // render but never pick up / deliver again).
                route.CompletedCycles += 1;
                ParsekLog.Verbose(Tag,
                    $"EmitLoopCycle: route {ShortIdForLog(route)} cycle={cycleId} " +
                    $"already in ledger (replay, dispatch-keyed) - emitting nothing, bumped " +
                    $"completedCycles={route.CompletedCycles.ToString(IC)}");
                return false;
            }

            // Dispatch + debit half (RouteDispatched Sequence 0, RouteCargoDebited
            // Sequence 1, origin/funds debit, sets KscDispatchFundsCost). The loop
            // crossing is the recorded dock phase and sits behind the ELS replay
            // backstop above, so this is the ONLY caller that applies the M1
            // physical origin debit (design D11).
            EmitDispatchDebit(route, currentUT, env, cycleId, applyPhysicalOriginDebit: true);

            // M4b B3 (plan D11 / OQ7): RESERVE this cycle's per-source cargo escrow at
            // dispatch (single-stop path - the M3a single bidirectional window reserves
            // its source here too). Released at the pickup window's physical debit
            // below. For a single-window route reserve == release, so the escrow nets to
            // zero within this one EmitLoopCycle (a competing route only sees it during
            // the instant between this reserve and EmitPickupHalf's release - within one
            // tick, the priority-ordered CompareRoutesForTick snapshot means a route
            // reserves-and-debits inside its own EmitLoopCycle before a competing route
            // runs). A delivery-only route reserves nothing.
            ReserveCycleEscrow(route, currentUT, env, cycleId);

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

            // Pickup half (M3, D6). When the cycle stop carries a witnessed pickup
            // manifest, physically debit the per-window pickup ENDPOINT and emit one
            // RouteCargoPickedUp row under the SAME cycleId, with a deterministic
            // Sequence AFTER RouteDispatched (Seq0) + RouteCargoDebited (Seq1). A
            // pure-delivery route has no pickup manifest and skips this (no
            // RouteCargoPickedUp row).
            //
            // MIXED-WINDOW ORDERING RULE (M3 Phase 6, design D6/D3 - the
            // phantom-clamp invariant). The pickup half (endpoint DEBIT, removes a
            // resource from the endpoint's source tank => FREES space) MUST run
            // BEFORE the delivery half (endpoint CREDIT, adds a resource to the
            // endpoint's destination tank => FILLS it) because BOTH halves resolve
            // the SAME stop.Endpoint => the SAME physical vessel, so for a resource
            // that is both delivered AND picked up at one dock (a net-zero or
            // partially-overlapping flow) the two physical writes hit the SAME tank
            // and the ORDER decides the result:
            //   * pickup-then-delivery (THIS order): the debit frees capacity first,
            //     so the endpoint holds MAXIMUM free space when ApplyDelivery's live
            //     LiveDeliveryCapacityProbe + LiveDeliveryWriters.WriteResourceLoaded
            //     run (the probe/planner read LIVE tank state AFTER the debit). A
            //     same-resource deliver+pickup nets correctly with NO phantom clamp
            //     and no clamp-and-waste of the delivered amount.
            //   * delivery-then-pickup (the WRONG order): a delivery into an
            //     already-near-full endpoint clamps at maxAmount (WriteResourceLoaded
            //     line `free = pr.maxAmount - pr.amount`), silently WASTES the
            //     clamped surplus, then the pickup debits from the post-clamp state -
            //     a different (lossy) result than the witnessed recording.
            // The rule is REPLAY-DETERMINISTIC: it is a fixed statement order inside
            // EmitLoopCycle under one cycleId, and the whole cycle re-presents
            // idempotently via the dispatch-keyed backstop above, so re-presentation
            // never re-runs either half. Pinned by RouteLoopPickupFireTests'
            // MixedSameResourceWindow_PickupFiresBeforeDelivery regression test.
            // Inventory pickup (EmitPickupHalf's inventory dimension) and inventory
            // delivery (ApplyDelivery's inventory writer) act on different identity /
            // resource spaces (stored-part STOREDPART slots keyed by IdentityHash vs
            // resource tanks), so they rarely collide on one tank; the same
            // debit-before-credit order still holds and is harmless when they do not.
            if (RouteHasPickupManifest(route))
            {
                EmitPickupHalf(route, currentUT, env, cycleId);
            }

            // Delivery half (M3 gate, D6). Runs AFTER the pickup half above (the
            // mixed-window ordering rule: endpoint DEBIT before endpoint CREDIT, see
            // the pickup-half comment) so ApplyDelivery's live capacity probe sees
            // the post-debit (max-free) tank state on a same-resource mixed window.
            // Skip ENTIRELY for a pure-PICKUP route
            // (no delivery manifest): firing ApplyDelivery on an empty plan would
            // emit a RouteCargoDelivered row even with nothing to deliver, which is
            // exactly what created the OQ4 replay hole (the re-key above keys on
            // RouteDispatched precisely so a delivery-less cycle is still
            // idempotent). When a delivery manifest IS present, ApplyDelivery
            // resolves the live destination vessel, plans + applies the fill,
            // debits Career funds, emits RouteCargoDelivered, and bumps
            // CompletedCycles. It recomputes the SAME cycleId internally
            // (Completed+Skipped unchanged between the halves). For a ghost-driving
            // loop-route the status is Active, so ApplyDelivery's terminal
            // TransitionTo(Active) is a self-transition (no InTransit involved).
            // Routed through the DeliveryApplierForTesting seam so xUnit can verify
            // the full fire without a live Vessel (production leaves the seam null
            // -> calls ApplyDelivery VERBATIM).
            if (RouteHasDeliveryManifest(route))
            {
                var deliveryApplier = DeliveryApplierForTesting;
                if (deliveryApplier != null)
                    deliveryApplier(route, currentUT, env);
                else
                    ApplyDelivery(route, currentUT, env);
            }
            else
            {
                // Pure-pickup route: no delivery half. CompletedCycles is normally
                // bumped by ApplyDelivery (the only counter advance on the fire
                // path); without a delivery the loop would recompute the SAME
                // cycleId next crossing and replay-skip forever (the dispatch-keyed
                // backstop would now match its own RouteDispatched row). Bump it
                // here so the next cycle's id advances - the pure-pickup analogue of
                // ApplyDelivery's CompletedCycles increment.
                route.CompletedCycles += 1;
                ParsekLog.Info(Tag,
                    $"EmitLoopCycle: route {ShortIdForLog(route)} cycle={cycleId} " +
                    $"pure-pickup (no delivery manifest) - delivery half skipped, " +
                    $"bumped completedCycles={route.CompletedCycles.ToString(IC)}");
            }

            // M4b escrow-strand fix (PR #1180 review): the single-stop analogue of the
            // multi-stop cycle-complete sweep (ProcessMultiStopCrossings). The cycle is
            // fully emitted here (dispatch + pickup-release + delivery), so ANY residual
            // reservation is stale - e.g. a reserve-pid != release-pid divergence where
            // EmitPickupHalf's release missed the dispatch-time reserved key. Drop it so
            // no positive residual can leak. No-op on the normal leak-free path (reserve
            // == release already netted to zero), and on a delivery-only route (no reserve).
            RouteStore.DropRouteEscrow(route.Id);
            return true;
        }

        /// <summary>
        /// Pickup half of the M3 two-direction loop applier (D6). Resolves the
        /// cycle stop's witnessed pickup endpoint + manifest, physically debits the
        /// endpoint via <see cref="ApplyPickupDebit"/> (the Phase 3 reusable
        /// per-window endpoint debit, routed through the
        /// <see cref="PickupDebitApplierForTesting"/> seam), and emits ONE
        /// <see cref="GameActionType.RouteCargoPickedUp"/> row carrying the ACTUAL
        /// debited manifest, the requested-on-shortfall manifest, and the endpoint
        /// pid. The row's <c>Sequence</c> is 2 - AFTER <c>RouteDispatched</c> (Seq0)
        /// and <c>RouteCargoDebited</c> (Seq1) emitted by
        /// <see cref="EmitDispatchDebit"/> - so the ledger walker (and the
        /// <c>RouteModule</c> out-of-order guard) sees dispatch before the pickup at
        /// the shared UT. Emits ZERO funds (a pickup debits its physical source,
        /// never funds, D6). Reuses the same loaded-gate-per-vessel capture and
        /// unresolved-at-emit honest-bookkeeping rules as the M1 origin debit (via
        /// <see cref="ApplyPickupDebit"/>).
        /// </summary>
        internal static void EmitPickupHalf(
            Route route, double currentUT, IRouteRuntimeEnvironment env, string cycleId)
        {
            RouteStop stop = ResolveCycleStop(route);
            if (stop == null)
            {
                ParsekLog.Warn(Tag,
                    $"PickupHalf: route {ShortIdForLog(route)} cycle={cycleId} no resolvable " +
                    "cycle stop - emitting nothing");
                return;
            }

            // The stop index this pickup window keys on (same value the row carries
            // and ResolveCycleStop resolved): PendingStopIndex, falling back to 0.
            int pickupStopIndex = route.PendingStopIndex >= 0 ? route.PendingStopIndex : 0;

            // C-2 (defense-in-depth): per-window pickup replay backstop. The pickup
            // half PHYSICALLY debits the source, so a cursor/ledger desync of the
            // BLOCKER-1 class (a window re-presented under a frozen cycleId) could
            // otherwise double-debit a refinery/depot. The dispatch ELS guard +
            // per-stop LastFiredCycleIndex already prevent normal re-presentation;
            // this row-keyed guard makes a double-debit IMPOSSIBLE even if those are
            // out-flanked. Symmetric with the delivery guard (STEP 1 of
            // ApplyDelivery): if THIS (routeId, cycleId, stopIndex) pickup row is
            // already in the ELS, emit NOTHING (no second physical debit, no second
            // row). Single-stop / legacy never double-fires (the dispatch backstop),
            // so this never trips for it - byte-identical there.
            if (IsPickupAlreadyInLedger(route.Id, cycleId, pickupStopIndex))
            {
                ParsekLog.Verbose(Tag,
                    $"PickupHalf: route {ShortIdForLog(route)} cycle={cycleId} " +
                    $"stop={pickupStopIndex.ToString(IC)} replay detected — already in ledger, " +
                    "skipping physical debit + row (C-2 backstop)");
                return;
            }

            Dictionary<string, double> pickupManifest = stop.PickupManifest;
            OriginDebitOutcome pickup = ApplyPickupDebit(stop.Endpoint, pickupManifest, env, route.Id);

            // M4b B3 (plan D11 / OQ7): RELEASE this window's escrow portion now that the
            // physical debit has fired. The debit is NOT gated on transport disposition
            // (19.2.5: the outflow was witnessed; a crashed transport still debits the
            // source), so the release follows the debit unconditionally here. Key on the
            // pid ApplyPickupDebit resolved (pickup.OriginVesselPid) - the SAME pid
            // ReserveCycleEscrow keyed on; when the endpoint was UNRESOLVED at debit
            // (OQ4: endpoint lost mid-cycle, zero actuals, cargo NOT taken) the outcome
            // carries pid 0, so fall back to the escrow-resolve pid to free the hold
            // anyway (no leak). After the cycle's last pickup window releases, the
            // source's summed reservation nets to zero (leak-free invariant). A no-op
            // against an empty escrow (e.g. after a reload, where escrow is RAM-only and
            // not persisted - the reserve is recomputed on the live session only).
            uint releasePid = pickup.OriginVesselPid != 0u
                ? pickup.OriginVesselPid
                : ResolveEscrowSourcePid(stop.Endpoint, env);
            ReleaseWindowEscrow(route, stop, releasePid, cycleId,
                pickup.Unresolved ? "window-endpoint-lost" : "window-debit");

            if (pickup.Short && !pickup.Unresolved)
            {
                ParsekLog.Warn(Tag,
                    $"PickupDebit: route {ShortIdForLog(route)} cycle={cycleId} SHORT at apply - " +
                    "clamped to stored; requested manifest recorded on the pickup row " +
                    $"(debitedResources={(pickup.ActualDebited?.Count ?? 0).ToString(IC)} " +
                    $"requestedResources={(pickup.RequestedOnShortfall?.Count ?? 0).ToString(IC)})");
            }

            // M3 Phase 5 (D7): inventory pickup half. Physically remove the
            // witnessed stored-part payloads from the SAME endpoint (the writer
            // captures its OWN loaded-gate per vessel internally) and carry the
            // ACTUAL removed inventory on the pickup row. A resource-only pickup
            // stop has no inventory manifest -> a structural no-op (empty
            // outcome). The endpoint pid resolves identically on both halves; the
            // resource half's pid wins on the row (they are the same vessel), with
            // the inventory pid as the fallback when the resource manifest was
            // empty.
            List<InventoryPayloadItem> inventoryPickupManifest = stop.InventoryPickupManifest;
            InventoryPickupOutcome inventoryPickup =
                ApplyInventoryPickupDebit(stop.Endpoint, inventoryPickupManifest, env, route.Id);

            if (inventoryPickup.Short && !inventoryPickup.Unresolved)
            {
                ParsekLog.Warn(Tag,
                    $"InventoryPickupDebit: route {ShortIdForLog(route)} cycle={cycleId} SHORT at apply - " +
                    "source no longer held a witnessed item; requested inventory recorded on the pickup row " +
                    $"(pickedUpInventory={(inventoryPickup.ActualPickedUp?.Count ?? 0).ToString(IC)} " +
                    $"requestedInventory={(inventoryPickup.RequestedOnShortfall?.Count ?? 0).ToString(IC)})");
            }

            // RouteCargoPickedUp row. Mirror of the debited row's payload shape
            // (actual manifest, requested-on-shortfall, endpoint pid) but ZERO funds
            // and Sequence 2 (after dispatch Seq0 + debit Seq1). Carries BOTH the
            // resource AND the inventory picked-up manifests (D7). Zero actuals in
            // either dimension serialize as no manifest. pickupStopIndex was resolved
            // at the top of this method (the same value the C-2 backstop keyed on).
            var pickedUpAction = new GameAction
            {
                Type = GameActionType.RouteCargoPickedUp,
                UT = currentUT,
                RouteId = route.Id,
                RouteCycleId = cycleId,
                RouteStopIndex = pickupStopIndex,
                // M4a A3 (Horn A): offset into THIS window's stride block so the
                // RouteCargoPickedUp row stays a TOTAL-order sibling of the
                // dispatch (stride+0/+1) and delivery (stride+3) rows across all
                // windows. Single-stop keeps stopIndex 0 -> Sequence 2 (v0-identical).
                Sequence = pickupStopIndex * SeqStride + 2,
                RouteResourceManifest = pickup.ActualDebited,
                RouteRequestedResourceManifest = pickup.RequestedOnShortfall,
                RouteInventoryManifest = inventoryPickup.ActualPickedUp,
                RouteRequestedInventoryManifest = inventoryPickup.RequestedOnShortfall,
                RouteOriginVesselPid = pickup.OriginVesselPid != 0u
                    ? pickup.OriginVesselPid
                    : inventoryPickup.EndpointVesselPid,
            };
            Ledger.AddAction(pickedUpAction);

            ParsekLog.Info(Tag,
                $"PickupHalf: route {ShortIdForLog(route)} cycle={cycleId} " +
                $"ut={currentUT.ToString("R", IC)} " +
                $"endpointPid={pickedUpAction.RouteOriginVesselPid.ToString(IC)} " +
                $"pickedUpResources={(pickup.ActualDebited?.Count ?? 0).ToString(IC)} " +
                $"requestedResources={(pickup.RequestedOnShortfall?.Count ?? 0).ToString(IC)} " +
                $"pickedUpInventory={(inventoryPickup.ActualPickedUp?.Count ?? 0).ToString(IC)} " +
                $"requestedInventory={(inventoryPickup.RequestedOnShortfall?.Count ?? 0).ToString(IC)} " +
                $"short={((pickup.Short || inventoryPickup.Short) ? "1" : "0")} " +
                $"unresolved={((pickup.Unresolved || inventoryPickup.Unresolved) ? "1" : "0")}");
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
        /// (M4a A3) Test seam consulted INSIDE the real <see cref="ApplyDelivery"/>,
        /// AFTER its per-window idempotency guard (STEP 1,
        /// <see cref="IsDeliveryAlreadyInLedger"/>) but BEFORE the live-Vessel
        /// resolution. Production leaves it null (the live delivery path runs). A
        /// multi-window xUnit test assigns a fake that emits a
        /// <see cref="GameActionType.RouteCargoDelivered"/> row for the passed
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
            /// <summary>
            /// M3 Phase 5 (D7 carve-out lift): stored-part payloads ACTUALLY
            /// removed from the origin's InventoryCostManifest (identity intact,
            /// removed quantity); null when no inventory was removed. Carried on
            /// the M1 origin-debit path now that the inventory remove writer is
            /// wired in; null on the legacy / KSC / pickup paths.
            /// </summary>
            public List<InventoryPayloadItem> ActualInventoryDebited;
            /// <summary>M3 Phase 5: witnessed inventory whose actual fell short; null on a full inventory debit.</summary>
            public List<InventoryPayloadItem> RequestedInventoryOnShortfall;
            /// <summary>Persistent id of the debited origin vessel; 0 when unresolved.</summary>
            public uint OriginVesselPid;
            /// <summary>True when at least one resource OR inventory item's actual fell short of the required amount.</summary>
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
                    // M3 Phase 5 (D7): the unresolved path records the full
                    // requested inventory too (honest bookkeeping).
                    ActualInventoryDebited = null,
                    RequestedInventoryOnShortfall = ClonePositiveInventoryManifest(route.InventoryCostManifest),
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

            // M3 Phase 5 (D7 carve-out lift): the inventory origin-debit half.
            // The route gate (OriginHasCargo) already verified the
            // InventoryCostManifest is fully held this tick; remove each witnessed
            // payload by identity from the SAME loaded/unloaded branch (the
            // per-vessel gate captured above), clamping at what is stored. A null
            // / empty InventoryCostManifest (the common case) is a no-op.
            List<InventoryPayloadItem> actualInventory = null;
            List<InventoryPayloadItem> requestedInventory = null;
            if (route.InventoryCostManifest != null && route.InventoryCostManifest.Count > 0)
            {
                var inventoryWriter = new LiveInventoryPickupWriter(originVessel, originIsLoaded);
                for (int i = 0; i < route.InventoryCostManifest.Count; i++)
                {
                    InventoryPayloadItem item = route.InventoryCostManifest[i];
                    if (item == null || string.IsNullOrEmpty(item.IdentityHash))
                        continue;
                    int want = item.Quantity > 0 ? item.Quantity : 0;
                    if (want <= 0)
                        continue;

                    int removed = 0;
                    for (int u = 0; u < want; u++)
                    {
                        if (inventoryWriter.RemoveOne(item))
                            removed++;
                        else
                            break;
                    }

                    if (removed > 0)
                    {
                        if (actualInventory == null)
                            actualInventory = new List<InventoryPayloadItem>(route.InventoryCostManifest.Count);
                        InventoryPayloadItem actualItem = item.DeepClone();
                        actualItem.Quantity = removed;
                        actualInventory.Add(actualItem);
                    }
                    if (removed < want)
                    {
                        anyShort = true;
                        if (requestedInventory == null)
                            requestedInventory = new List<InventoryPayloadItem>(route.InventoryCostManifest.Count);
                        InventoryPayloadItem requestedItem = item.DeepClone();
                        requestedItem.Quantity = want;
                        requestedInventory.Add(requestedItem);
                    }
                }

                ParsekLog.Info(Tag,
                    $"OriginDebit inventory: route={ShortIdForLog(route)} " +
                    $"requested={route.InventoryCostManifest.Count.ToString(IC)} " +
                    $"removed={(actualInventory?.Count ?? 0).ToString(IC)} " +
                    $"short={(requestedInventory != null ? "1" : "0")} " +
                    $"path={(originIsLoaded ? "loaded" : "unloaded")}");
            }

            return new OriginDebitOutcome
            {
                ActualDebited = actualManifest,
                RequestedOnShortfall = requestedManifest,
                ActualInventoryDebited = actualInventory,
                RequestedInventoryOnShortfall = requestedInventory,
                OriginVesselPid = originVessel.persistentId,
                Short = anyShort,
                Unresolved = false,
            };
        }

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
        /// Production M3 per-window pickup debit (design D5, plan Phase 3): the
        /// REVERSE-direction reuse of the M1 origin-debit machinery, re-aimed at
        /// the per-window pickup ENDPOINT vessel. Given a resolved stop endpoint
        /// and a pickup resource manifest (the witnessed loaded term), resolves
        /// the endpoint vessel via the env, captures the loaded gate ONCE for
        /// THAT vessel, builds a <see cref="LiveOriginCargoProbe"/>, plans via
        /// the manifest-agnostic <see cref="RouteOriginDebitPlanner.PrepareDebit(Dictionary{string,double},IOriginCargoProbe)"/>,
        /// applies via <see cref="LiveOriginDebitWriters"/>, and returns the
        /// actuals / requested-on-shortfall / endpoint pid / short / unresolved
        /// outcome - the SAME <see cref="OriginDebitOutcome"/> shape the M1
        /// origin path returns (here <c>OriginVesselPid</c> carries the ENDPOINT
        /// pid). This is the helper the Phase 4 <c>EmitLoopCycle</c>
        /// two-direction applier calls per pickup window; it is NOT wired into
        /// the loop path yet (Phase 4) and emits NO ledger row (the
        /// <c>RouteCargoPickedUp</c> row + replay re-key are Phase 4).
        ///
        /// <para><b>Unresolved rule (mirrors <see cref="ApplyOriginDebit"/>):</b>
        /// a resolution that returns <c>false</c> OR returns <c>true</c> with a
        /// null vessel counts as UNRESOLVED: Warn, zero actuals, FULL requested
        /// manifest. Loop-path-only in spirit (M1 D11 parity); reversible via
        /// the rewind quicksave + ELS replay keys when Phase 4 wires it in.</para>
        /// </summary>
        internal static OriginDebitOutcome ApplyPickupDebit(
            RouteEndpoint endpoint,
            Dictionary<string, double> pickupManifest,
            IRouteRuntimeEnvironment env,
            string routeIdForLog)
        {
            // Test seam: short-circuit to a hand-built outcome so Phase 4 can
            // verify the two-direction applier without a live endpoint Vessel.
            var seam = PickupDebitApplierForTesting;
            if (seam != null)
                return seam(endpoint, pickupManifest, env);

            // Nothing to pick up - empty/null manifest is a structural no-op
            // (zero actuals, resolved, not short). Distinct from the unresolved
            // path: the endpoint may be perfectly resolvable, there is simply
            // no witnessed load term for this window.
            if (pickupManifest == null || pickupManifest.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"PickupDebit: route {routeIdForLog ?? "<none>"} empty pickup manifest - nothing to debit");
                return new OriginDebitOutcome
                {
                    ActualDebited = null,
                    RequestedOnShortfall = null,
                    OriginVesselPid = 0u,
                    Short = false,
                    Unresolved = false,
                };
            }

            Vessel endpointVessel = null;
            string resolveReason = null;
            bool resolved = false;
            try
            {
                resolved = env.TryResolveEndpointVessel(endpoint, out endpointVessel, out resolveReason);
            }
            catch (Exception ex)
            {
                resolveReason = $"resolver-threw-{ex.GetType().Name}";
                resolved = false;
            }

            if (!resolved || endpointVessel == null)
            {
                string reason = resolved ? "resolved-null-vessel" : (resolveReason ?? "unknown");
                ParsekLog.Warn(Tag,
                    $"PickupDebit: route {routeIdForLog ?? "<none>"} pickup endpoint unresolved at debit " +
                    $"(reason={reason}) - emitting requested manifest with zero actuals");
                return new OriginDebitOutcome
                {
                    ActualDebited = null,
                    // Positive entries only, matching the planner's <=0 skip -
                    // the short and unresolved paths must agree on row content.
                    RequestedOnShortfall = ClonePositiveManifest(pickupManifest),
                    OriginVesselPid = 0u,
                    Short = true,
                    Unresolved = true,
                };
            }

            // Capture the loaded gate ONCE for THIS endpoint vessel and thread
            // it into both the probe and the writer so the plan and the
            // mutation read from the SAME loaded/unloaded branch. A
            // two-direction applier touching multiple endpoint vessels captures
            // this PER VESSEL - never hoist one flag across vessels (design D5).
            bool endpointIsLoaded = endpointVessel.loaded && !endpointVessel.packed;
            var probe = new LiveOriginCargoProbe(endpointVessel, endpointIsLoaded);
            OriginDebitPlan plan = RouteOriginDebitPlanner.PrepareDebit(pickupManifest, probe);

            ParsekLog.Info(Tag,
                $"PickupDebit plan: route={routeIdForLog ?? "<none>"} " +
                $"resources={(plan.Resources?.Count ?? 0).ToString(IC)} " +
                $"short={(plan.IsShort ? "1" : "0")} " +
                $"path={(endpointIsLoaded ? "loaded" : "unloaded")} " +
                $"endpoint={endpointVessel.vesselName ?? "<none>"} " +
                $"pid={endpointVessel.persistentId.ToString(IC)}");

            var writers = new LiveOriginDebitWriters(routeIdForLog, endpointVessel, plan, endpointIsLoaded);
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
                    // Same shortfall epsilon as the origin path (multi-tank
                    // drains sum per-tank rounding; an exact compare flags a
                    // phantom shortfall).
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
                OriginVesselPid = endpointVessel.persistentId,
                Short = anyShort,
                Unresolved = false,
            };
        }

        /// <summary>
        /// Result of the M3 per-window INVENTORY pickup debit (Phase 5, design
        /// D7): the inventory analogue of <see cref="OriginDebitOutcome"/>. Carries
        /// the ACTUAL removed stored-part payloads (the items the source actually
        /// held, identity intact), the requested-on-shortfall payloads (witnessed
        /// items the source no longer held at debit time), the endpoint pid, and
        /// the short / unresolved flags. The actual / requested item lists carry
        /// the per-identity quantity that was removed / fell short. Empty actual
        /// list serializes as no inventory manifest on the row.
        /// </summary>
        internal struct InventoryPickupOutcome
        {
            /// <summary>Stored-part payloads ACTUALLY removed from the source (identity + removed quantity); null when nothing was removed.</summary>
            public List<InventoryPayloadItem> ActualPickedUp;
            /// <summary>Witnessed payloads whose actual fell short (the source no longer held them); null on a full pickup.</summary>
            public List<InventoryPayloadItem> RequestedOnShortfall;
            /// <summary>Persistent id of the debited endpoint vessel; 0 when unresolved.</summary>
            public uint EndpointVesselPid;
            /// <summary>True when at least one witnessed item's removed quantity fell short of the witnessed quantity.</summary>
            public bool Short;
            /// <summary>True when the endpoint vessel could not be resolved at apply time (zero actuals, full requested manifest).</summary>
            public bool Unresolved;
        }

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

        /// <summary>
        /// Production M3 per-window INVENTORY pickup debit (design D7, Phase 5):
        /// the inventory analogue of <see cref="ApplyPickupDebit"/>. Given a
        /// resolved stop endpoint and a witnessed inventory pickup manifest (the
        /// loaded term), resolves the endpoint vessel via the env, captures the
        /// loaded gate ONCE for THAT vessel, and removes each witnessed item from
        /// the source via the <see cref="LiveInventoryPickupWriter"/> (loaded
        /// <c>ClearPartAtSlot</c> / unloaded STOREDPART proto-node removal, matched
        /// strictly by <see cref="InventoryPayloadItem.IdentityHash"/>, lowest-slot
        /// partial-match). The transport CREDIT is bookkeeping only - the writer
        /// removes from the SOURCE only; no physical store on the transport (the
        /// transport never materializes, 19.2.3). Returns the actual-removed /
        /// requested-on-shortfall payloads / endpoint pid / short / unresolved
        /// outcome.
        ///
        /// <para><b>Unresolved rule (mirrors <see cref="ApplyPickupDebit"/>):</b> a
        /// resolution that returns <c>false</c> OR returns <c>true</c> with a null
        /// vessel counts as UNRESOLVED: Warn, zero actuals, FULL requested manifest
        /// (honest bookkeeping). Loop-path-only in spirit (M1 D11 parity);
        /// reversible via the rewind quicksave.</para>
        /// </summary>
        internal static InventoryPickupOutcome ApplyInventoryPickupDebit(
            RouteEndpoint endpoint,
            List<InventoryPayloadItem> pickupManifest,
            IRouteRuntimeEnvironment env,
            string routeIdForLog)
        {
            var seam = InventoryPickupApplierForTesting;
            if (seam != null)
                return seam(endpoint, pickupManifest, env);

            // Nothing to pick up - empty/null manifest is a structural no-op.
            if (pickupManifest == null || pickupManifest.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"InventoryPickupDebit: route {routeIdForLog ?? "<none>"} empty inventory pickup manifest - nothing to debit");
                return new InventoryPickupOutcome
                {
                    ActualPickedUp = null,
                    RequestedOnShortfall = null,
                    EndpointVesselPid = 0u,
                    Short = false,
                    Unresolved = false,
                };
            }

            Vessel endpointVessel = null;
            string resolveReason = null;
            bool resolved = false;
            try
            {
                resolved = env.TryResolveEndpointVessel(endpoint, out endpointVessel, out resolveReason);
            }
            catch (Exception ex)
            {
                resolveReason = $"resolver-threw-{ex.GetType().Name}";
                resolved = false;
            }

            if (!resolved || endpointVessel == null)
            {
                string reason = resolved ? "resolved-null-vessel" : (resolveReason ?? "unknown");
                ParsekLog.Warn(Tag,
                    $"InventoryPickupDebit: route {routeIdForLog ?? "<none>"} pickup endpoint unresolved at debit " +
                    $"(reason={reason}) - emitting requested manifest with zero actuals");
                return new InventoryPickupOutcome
                {
                    ActualPickedUp = null,
                    RequestedOnShortfall = ClonePositiveInventoryManifest(pickupManifest),
                    EndpointVesselPid = 0u,
                    Short = true,
                    Unresolved = true,
                };
            }

            // Capture the loaded gate ONCE for THIS endpoint vessel (design D5
            // per-vessel capture) and thread it into the writer.
            bool endpointIsLoaded = endpointVessel.loaded && !endpointVessel.packed;
            var writer = new LiveInventoryPickupWriter(endpointVessel, endpointIsLoaded);

            ParsekLog.Info(Tag,
                $"InventoryPickupDebit plan: route={routeIdForLog ?? "<none>"} " +
                $"items={pickupManifest.Count.ToString(IC)} " +
                $"path={(endpointIsLoaded ? "loaded" : "unloaded")} " +
                $"endpoint={endpointVessel.vesselName ?? "<none>"} " +
                $"pid={endpointVessel.persistentId.ToString(IC)}");

            List<InventoryPayloadItem> actual = null;
            List<InventoryPayloadItem> requested = null;
            bool anyShort = false;
            for (int i = 0; i < pickupManifest.Count; i++)
            {
                InventoryPayloadItem item = pickupManifest[i];
                if (item == null || string.IsNullOrEmpty(item.IdentityHash))
                    continue;
                int want = item.Quantity > 0 ? item.Quantity : 0;
                if (want <= 0)
                    continue;

                int removed = 0;
                for (int u = 0; u < want; u++)
                {
                    if (writer.RemoveOne(item))
                        removed++;
                    else
                        break; // source no longer holds this identity
                }

                if (removed > 0)
                {
                    if (actual == null)
                        actual = new List<InventoryPayloadItem>(pickupManifest.Count);
                    InventoryPayloadItem actualItem = item.DeepClone();
                    actualItem.Quantity = removed;
                    actual.Add(actualItem);
                }
                if (removed < want)
                {
                    anyShort = true;
                    if (requested == null)
                        requested = new List<InventoryPayloadItem>(pickupManifest.Count);
                    InventoryPayloadItem requestedItem = item.DeepClone();
                    requestedItem.Quantity = want;
                    requested.Add(requestedItem);
                }
            }

            return new InventoryPickupOutcome
            {
                ActualPickedUp = actual,
                RequestedOnShortfall = requested,
                EndpointVesselPid = endpointVessel.persistentId,
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
            bool applyPhysicalOriginDebit, int dispatchStopIndex = -1)
        {
            // M4a A3 (Horn A): the dispatch/debit rows are stamped with
            // dispatchStopIndex (the cycle's first delivery/pickup window in DockUT
            // order on the loop path) so BOTH replay guards key per-window, and
            // their Sequence is offset into that window's stride block. The legacy
            // self-timer path passes -1 (the v0 sentinel) so its rows stay
            // byte-identical: stopIndex -1 (codec-omitted) + Sequence 0/1.
            int dispatchSeqBase = dispatchStopIndex >= 0 ? dispatchStopIndex * SeqStride : 0;

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
                RouteStopIndex = dispatchStopIndex,
                Sequence = dispatchSeqBase + 0,
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
                RouteStopIndex = dispatchStopIndex,
                Sequence = dispatchSeqBase + 1,
                RouteResourceManifest = physicalDebitApplied
                    ? originDebit.ActualDebited
                    : CloneManifest(route.CostManifest),
                RouteRequestedResourceManifest = physicalDebitApplied
                    ? originDebit.RequestedOnShortfall
                    : null,
                // M3 Phase 5 (D7 carve-out lift): the physical-debit path carries
                // the ACTUAL removed origin inventory + requested-on-shortfall,
                // sparse (null on the legacy / KSC paths so v0 rows stay
                // byte-identical).
                RouteInventoryManifest = physicalDebitApplied
                    ? originDebit.ActualInventoryDebited
                    : null,
                RouteRequestedInventoryManifest = physicalDebitApplied
                    ? originDebit.RequestedInventoryOnShortfall
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

            // M4b escrow-strand fix (PR #1180 review): an EndpointLost route stops crossing
            // and never reaches its cycle-complete escrow drop, so a reservation held from a
            // dispatch earlier in the cycle would mis-gate a competing route. Drop it.
            // Idempotent no-op when nothing is held.
            RouteStore.DropRouteEscrow(route.Id);

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
            // Single-stop / legacy entry: the (only) delivery window completes the
            // cycle, so bump CompletedCycles once (byte-behaviour-identical to
            // pre-A3). The multi-stop loop helper calls the 4-arg overload below,
            // passing false on every window except the last.
            ApplyDelivery(route, currentUT, env, bumpCompletedCycle: true);
        }

        /// <summary>
        /// M4a A3 (Horn A) delivery applier with explicit cycle-completion control.
        /// <paramref name="bumpCompletedCycle"/> is true on the single / last window
        /// of a cycle (advances <see cref="Route.CompletedCycles"/> once) and false
        /// on the earlier windows of a multi-stop cycle (so the cycleId the later
        /// windows compute stays stable). The 3-arg <see cref="ApplyDelivery"/>
        /// forwards with <c>true</c> for the single-stop / legacy self-timer path.
        /// </summary>
        private static void ApplyDelivery(
            Route route, double currentUT, IRouteRuntimeEnvironment env, bool bumpCompletedCycle)
        {
            // Cycle id matches the dispatch/debit pair (cycle-{Completed + Skipped}).
            // M4a A3 (Horn A): NEITHER half bumps CompletedCycles any more (the
            // cycle-complete bump moved OUT of the per-window path to the loop
            // caller, ProcessLoopRoute / EmitLoopCycle, so a multi-stop cycle's
            // N windows all recompute the SAME cycleId here). The recompute stays
            // in sync with the tick-start computation precisely because Completed
            // and Skipped are stable across the windows of one cycle.
            string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles).ToString(IC);

            // Stop index from the per-crossing hand-off (M4a A3: ProcessLoopRoute
            // drives PendingStopIndex to the firing window's index per crossing;
            // single-stop / legacy paths leave it -1 -> stop 0). Both cycleId and
            // stopIndex feed the per-window idempotency guard below.
            int stopIndex = route.PendingStopIndex >= 0 ? route.PendingStopIndex : 0;

            // STEP 1: idempotency guard. If ELS already contains a
            // RouteCargoDelivered row for (routeId, cycleId, stopIndex), THIS
            // WINDOW was delivered on a previous tick (or recovered after crash);
            // applying again would double-charge funds and double-write resources.
            // M4a A3: keyed per-window (stopIndex) so window 0's row does NOT
            // suppress windows 1..N (the RANK-1 hole). This is the orchestrator's
            // ONLY ELS read — every other consumer routes through ERS.
            if (IsDeliveryAlreadyInLedger(route.Id, cycleId, stopIndex))
            {
                ParsekLog.Verbose(Tag,
                    $"Delivery: route {ShortIdForLog(route)} cycle={cycleId} " +
                    $"stop={stopIndex.ToString(IC)} replay detected — already in ledger");
                // M4a A3: the CompletedCycles bump on the replay branch is gated on
                // bumpCompletedCycle (symmetric with the success path). The legacy
                // self-timer + single-stop loop pass true (the replayed cycle WAS
                // completed, so the counter must advance or the next dispatch reuses
                // the same cycleId and loops forever - the original P2-1 fix). A
                // multi-stop EARLIER window passes false: the loop caller owns the
                // once-per-cycle bump, so the cycleId the LATER windows compute stays
                // stable.
                if (bumpCompletedCycle)
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

            // M4a A3 test seam: emit the RouteCargoDelivered row headlessly AFTER
            // the REAL per-window idempotency guard above (STEP 1) but BEFORE the
            // live-Vessel resolution below (which xUnit cannot satisfy). This lets a
            // multi-window test exercise the REAL guard (so a window-2-suppressed
            // regression goes RED, not false-green) without a live KSP Vessel.
            // Production leaves this null and falls through to the live path.
            var deliveryRowEmitter = DeliveryRowEmitterForTesting;
            if (deliveryRowEmitter != null)
            {
                deliveryRowEmitter(route, currentUT, env, cycleId, stopIndex, bumpCompletedCycle);
                if (bumpCompletedCycle)
                    route.CompletedCycles += 1;
                route.PendingDeliveryUT = null;
                route.PendingStopIndex = -1;
                route.NextEligibilityCheckUT = null;
                route.TransitionTo(RouteStatus.Active, "delivered-loop-seam");
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
                // M4b escrow-strand fix (PR #1180 review): see ApplyEndpointLost - drop any
                // held escrow on the delivery-time endpoint-lost transition too (the cycle
                // never completes, so the cycle-complete sweep won't run). Idempotent no-op.
                RouteStore.DropRouteEscrow(route.Id);
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
                BumpCompletedCycle = bumpCompletedCycle,
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

            // M4a A3 (Horn A): the RouteCargoDelivered row is offset into THIS
            // window's stride block (+3, after dispatch +0 / debit +1 / pickup +2)
            // so a multi-stop cycle's N delivery rows have a TOTAL (UT, Sequence)
            // order. A single-stop route has ctx.StopIndex 0 -> Sequence 3.
            //
            // NOTE (C-1): single-stop is BEHAVIOUR-identical, not byte-identical.
            // The single-stop RouteCargoDelivered.Sequence changes 0 -> 3 (the +3
            // delivery offset of the stride-0 block). This is verified
            // behaviour-preserving (recalc-walk-order-equivalent): route rows are
            // non-earning, FundsModule ignores RouteCargoDelivered entirely, and the
            // ledger walkers do not assume a contiguous 0..N
            // (RecalculationEngine.SortActions = OrderBy UT, ThenBy IsEarning, ThenBy
            // Sequence; the dedup equality keys on ActionId / type-specific fields,
            // NOT Sequence). With one RouteCargoDelivered per cycle at its own UT and
            // the dispatch-before-delivery guard still holding (dispatch seq 0/1 <
            // delivery seq 3), the 0 -> 3 shift does not change any walk outcome;
            // what matters is total ordering, which holds.
            int deliverySeqBase = ctx.StopIndex >= 0 ? ctx.StopIndex * SeqStride : 0;
            var action = new GameAction
            {
                Type = GameActionType.RouteCargoDelivered,
                UT = ctx.CurrentUT,
                RouteId = route.Id,
                RouteCycleId = ctx.CycleId,
                RouteStopIndex = ctx.StopIndex,
                Sequence = deliverySeqBase + 3,
                RouteResourceManifest = actualManifest,
                RouteRequestedResourceManifest = requestedManifest,
                // GameAction.RouteKscFundsCost is float-typed by design (same
                // as RouteCargoDebited). The double precision on
                // ApplyDeliveryContext.KscFundsCost is preserved upstream for
                // diagnostics.
                RouteKscFundsCost = (ctx.IsCareer && ctx.IsKscOrigin) ? (float)ctx.KscFundsCost : 0f,
            };
            ctx.LedgerEmitter(action);

            // STEP 9: mutate route state + transition. M4a A3 (Horn A): the
            // CompletedCycles bump is gated on ctx.BumpCompletedCycle so it fires
            // exactly ONCE per cycle (the single/last window). An earlier window of
            // a multi-stop cycle leaves the counter alone so the LATER windows
            // recompute the SAME cycleId. A single-stop / legacy delivery always
            // passes BumpCompletedCycle=true -> one bump per cycle (byte-identical
            // to the pre-A3 unconditional increment here). Status transitions back
            // to Active with a reason string carrying the partial/full discriminator
            // so the UI can surface "delivered partial" badges.
            if (ctx.BumpCompletedCycle)
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
                // M4b escrow-strand fix (PR #1180 clean-review Finding 1): a Send-Once-armed
                // MULTI-STOP route can pause HERE mid-PARTIAL-cycle (an early window delivered
                // this tick, a later window still pending), so it never reaches the
                // cycle-complete escrow sweep (ProcessMultiStopCrossings) and the pending
                // window's source reservation would strand, mis-gating a competing route.
                // Drop it. Idempotent no-op on the complete-cycle / single-stop / no-escrow
                // paths (this is the sixth and final quiesce transition).
                RouteStore.DropRouteEscrow(route.Id);
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
        internal static bool IsDeliveryAlreadyInLedger(string routeId, string cycleId, int stopIndex)
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
                // M4a A3 (Horn A re-key): a multi-stop cycle fires N delivery
                // windows under ONE cycleId, so the guard MUST also match the
                // per-window stopIndex - else window 0's RouteCargoDelivered row
                // would suppress windows 1..N forever (the RANK-1 hole). The
                // delivery row carries its real ctx.StopIndex (ApplyDeliveryFromPlan),
                // so this reads an already-serialized field.
                if (a.RouteStopIndex != stopIndex) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// DIRECTION-AGNOSTIC per-cycle ELS idempotency check (M3 OQ4, the replay
        /// re-key). Returns <c>true</c> if any non-tombstoned
        /// <see cref="GameActionType.RouteDispatched"/> row already exists for the
        /// given <paramref name="routeId"/> + <paramref name="cycleId"/> pair.
        ///
        /// <para><b>Why RouteDispatched, not RouteCargoDelivered.</b> Every loop
        /// crossing emits exactly one <c>RouteDispatched</c> row regardless of
        /// direction, but a pickup-ONLY route emits NO <c>RouteCargoDelivered</c>
        /// row (the delivery half is skipped, D6). Keying the backstop on the
        /// delivery row (the pre-M3 behavior of <see cref="EmitLoopCycle"/>) would
        /// never fire for a pickup-only route, so its endpoint debit would
        /// RE-APPLY every save/reload (the M3 replay hole, risk register #1).
        /// Keying on <c>RouteDispatched</c> makes the guard fire for every cycle
        /// shape: deliver-only, pickup-only, and mixed. This is the LOOP-PATH-ONLY
        /// guard; the legacy <see cref="ApplyDelivery"/> path keeps using
        /// <see cref="IsDeliveryAlreadyInLedger"/> (dead for loop routes, M1 D11).
        /// Structural mirror of <see cref="IsDeliveryAlreadyInLedger"/>.</para>
        /// </summary>
        internal static bool IsDispatchAlreadyInLedger(string routeId, string cycleId, int stopIndex)
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
                    $"IsDispatchAlreadyInLedger: ComputeELS threw {ex.GetType().Name}: {ex.Message}; treating as not-in-ledger");
                return false;
            }

            if (els == null) return false;
            for (int i = 0; i < els.Count; i++)
            {
                GameAction a = els[i];
                if (a == null) continue;
                if (a.Type != GameActionType.RouteDispatched) continue;
                if (!string.Equals(a.RouteId, routeId, StringComparison.Ordinal)) continue;
                if (!string.Equals(a.RouteCycleId, cycleId, StringComparison.Ordinal)) continue;
                // M4a A3 (Horn A re-key): the dispatch half fires exactly ONCE per
                // cycle (the once-per-cycle KSC funds charge), so its row carries
                // the cycle's dispatch stopIndex (the first window's index, stamped
                // by EmitDispatchDebit). Matching the stopIndex keeps the guard
                // symmetric with the delivery guard and reads the same already-
                // serialized field; since exactly one dispatch row exists per
                // cycle, the stopIndex match is effectively per-cycle.
                if (a.RouteStopIndex != stopIndex) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// (C-2, defense-in-depth) Per-window ELS idempotency check for the PICKUP
        /// half. Returns <c>true</c> if any non-tombstoned
        /// <see cref="GameActionType.RouteCargoPickedUp"/> row already exists for the
        /// given <c>(routeId, cycleId, stopIndex)</c> triple.
        ///
        /// <para><b>Why a pickup-specific backstop.</b> The pickup half
        /// (<see cref="EmitPickupHalf"/> -&gt; <see cref="ApplyPickupDebit"/>)
        /// PHYSICALLY debits the source vessel, but unlike the dispatch and delivery
        /// halves it had NO ledger-level idempotency guard of its own - it relied
        /// entirely on the per-stop <see cref="RouteStop.LastFiredCycleIndex"/>
        /// cursor + the dispatch-keyed backstop to never re-present. The BLOCKER-1
        /// class of cursor/ledger desync (a frozen cycleId firing a window twice
        /// under two different live cursors) showed that a cursor-only guard can be
        /// out-flanked. Keying this backstop on the per-window pickup ROW (the same
        /// (RouteId, cycleId, stopIndex) key the delivery guard uses) means even a
        /// future cursor desync can never DOUBLE-DEBIT a physical source: a
        /// second presentation of the same window finds its own
        /// <c>RouteCargoPickedUp</c> row and skips the debit. Structural mirror of
        /// <see cref="IsDeliveryAlreadyInLedger"/>.</para>
        /// </summary>
        internal static bool IsPickupAlreadyInLedger(string routeId, string cycleId, int stopIndex)
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
                    $"IsPickupAlreadyInLedger: ComputeELS threw {ex.GetType().Name}: {ex.Message}; treating as not-in-ledger");
                return false;
            }

            if (els == null) return false;
            for (int i = 0; i < els.Count; i++)
            {
                GameAction a = els[i];
                if (a == null) continue;
                if (a.Type != GameActionType.RouteCargoPickedUp) continue;
                if (!string.Equals(a.RouteId, routeId, StringComparison.Ordinal)) continue;
                if (!string.Equals(a.RouteCycleId, cycleId, StringComparison.Ordinal)) continue;
                if (a.RouteStopIndex != stopIndex) continue;
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
            return ElsContainsRouteCycleRow(
                GameActionType.RouteRecoveryCredited, routeId, cycleId,
                "IsRecoveryCreditAlreadyInLedger");
        }

        /// <summary>
        /// Shared route-cycle ELS idempotency scan. Currently forwarded to only by
        /// <see cref="IsRecoveryCreditAlreadyInLedger"/>;
        /// <see cref="IsDeliveryAlreadyInLedger"/> evolved a per-stop inline body during
        /// the M4 multi-stop work and no longer routes through here. Returns
        /// <c>true</c> if any ELS row of <paramref name="type"/> matches the given
        /// <paramref name="routeId"/> + <paramref name="cycleId"/> pair. ELS is
        /// supersede / tombstone aware. Exception-safe: a throw is treated as
        /// not-in-ledger. The <paramref name="logCtx"/> reproduces the caller's
        /// original log prefix verbatim so the emitted line is unchanged.
        /// </summary>
        private static bool ElsContainsRouteCycleRow(
            GameActionType type, string routeId, string cycleId, string logCtx)
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
                    $"{logCtx}: ComputeELS threw {ex.GetType().Name}: {ex.Message}; treating as not-in-ledger");
                return false;
            }

            if (els == null) return false;
            for (int i = 0; i < els.Count; i++)
            {
                GameAction a = els[i];
                if (a == null) continue;
                if (a.Type != type) continue;
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

            // Amount: reuse SumRecoveredCredits over the source tree, read from
            // ELS. ResolveTreeRecordingIds scopes the tree to the route's
            // creation-time membership snapshot (M-MIS-9-R1), so a branch added
            // to the tree after creation cannot inflate the recurring credit.
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

            /// <summary>
            /// M4a A3 (Horn A): true when this delivery window COMPLETES the cycle
            /// (the only window, or the last window of a multi-stop cycle). The
            /// CompletedCycles bump fires ONCE per cycle and is gated on this flag,
            /// so an EARLIER window of a multi-stop cycle does NOT advance the
            /// cycleId the LATER windows compute. A single-stop / legacy delivery
            /// always sets this true -> CompletedCycles bumps exactly once
            /// (byte-behaviour-identical to pre-A3).
            /// </summary>
            public bool BumpCompletedCycle;
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
            if (route == null || route.Stops == null)
                return;
            for (int i = 0; i < route.Stops.Count; i++)
            {
                if (route.Stops[i] != null)
                    route.Stops[i].LastFiredCycleIndex = -1;
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
