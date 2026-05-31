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
            // (ELS is the backstop). NOTE: LoopAnchorUT capture-on-activate (which
            // re-phases the span clock) is owned by Phase 5 RouteBuilder; here we
            // only reset the cycle-observation cursor.
            long prevObserved = route.LastObservedLoopCycleIndex;
            if (route.IsLoopRoute)
                route.LastObservedLoopCycleIndex = -1;

            route.TransitionTo(RouteStatus.Active, "player-activate");
            ParsekLog.Info(Tag,
                $"TryActivate: route={ShortIdForLog(route)} now Active " +
                $"nextDispatchUT={route.NextDispatchUT.ToString("R", IC)} " +
                $"loopRoute={(route.IsLoopRoute ? "1" : "0")} " +
                $"lastObservedLoopCycleIndex {prevObserved.ToString(IC)}->{route.LastObservedLoopCycleIndex.ToString(IC)}");
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
                        ApplyWait(route, decision);
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

            // Crossing detection.
            bool crossing = RouteLoopClock.IsCrossing(
                unit, cycleIndex, isInInterCycleTail, route.RecordedDockUT, route.LastObservedLoopCycleIndex);
            if (!crossing)
            {
                skipped++;
                ParsekLog.VerboseRateLimited(Tag, "loop-noncross-" + route.Id,
                    $"LoopRoute: route {ShortIdForLog(route)} no crossing — " +
                    RouteLoopClock.DescribeState(unit, currentUT, loopUT, cycleIndex,
                        isInInterCycleTail, route.RecordedDockUT, route.LastObservedLoopCycleIndex),
                    5.0);
                return;
            }

            // Warp: cycleIndex may jump N>1 since the last tick (~1 Hz orchestrator
            // vs fast warp). We fire ONCE per tick and snap LastObservedLoopCycleIndex
            // forward to cycleIndex (do NOT replay each skipped cycle). ELS
            // (routeId, cycleId) idempotency is the backstop if a save/reload or a
            // double-tick re-fires the same cycleId.
            long jump = cycleIndex - route.LastObservedLoopCycleIndex;
            if (jump > 1)
            {
                ParsekLog.Verbose(Tag,
                    $"LoopRoute: route {ShortIdForLog(route)} warp jump={jump.ToString(IC)} " +
                    $"(lastObserved={route.LastObservedLoopCycleIndex.ToString(IC)} -> " +
                    $"cycleIndex={cycleIndex.ToString(IC)}); firing ONCE and snapping forward");
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
                // Blocked cycle: emit NOTHING (no debit, no delivery; the ghost
                // still renders — "world looks busy, transfers nothing"). Bump
                // SkippedCycles and STILL snap the cycle index forward so the
                // blocked cycle does not re-fire every tick.
                route.SkippedCycles += 1;
                route.LastObservedLoopCycleIndex = cycleIndex;
                skipped++;
                ParsekLog.Info(Tag,
                    $"LoopRoute: route {ShortIdForLog(route)} cycle={cycleId} " +
                    $"BLOCKED kind={elig.Kind} reason={elig.Reason ?? "<none>"} " +
                    $"shortfall={elig.Shortfall.ToString("R", IC)} — emitted nothing, " +
                    $"snapped lastObserved={cycleIndex.ToString(IC)} skippedCycles={route.SkippedCycles.ToString(IC)}");
                return;
            }

            // Confirmed crossing + eligible: emit the FULL cycle (must-fix #1).
            // Returns false on the ELS-replay backstop (nothing emitted) so the
            // log line below distinguishes a real fire from a replay no-op.
            bool emitted = EmitLoopCycle(route, currentUT, env, cycleId);

            // Snap forward in BOTH cases (ELS idempotency is the backstop;
            // CompletedCycles was bumped inside EmitLoopCycle -> ApplyDelivery on
            // the fire path, or by the replay branch on the backstop path).
            route.LastObservedLoopCycleIndex = cycleIndex;
            if (emitted)
            {
                dispatched++;
                transitioned++;
                ParsekLog.Info(Tag,
                    $"LoopRoute: route {ShortIdForLog(route)} cycle={cycleId} FIRED full cycle " +
                    $"(dispatch+debit+delivered) at ut={currentUT.ToString("R", IC)}; " +
                    $"snapped lastObserved={cycleIndex.ToString(IC)} completedCycles={route.CompletedCycles.ToString(IC)}");
            }
            else
            {
                skipped++;
                ParsekLog.Info(Tag,
                    $"LoopRoute: route {ShortIdForLog(route)} cycle={cycleId} replay backstop " +
                    "(already in ledger) — emitted nothing; " +
                    $"snapped lastObserved={cycleIndex.ToString(IC)} completedCycles={route.CompletedCycles.ToString(IC)}");
            }
        }

        /// <summary>
        /// Resolves the route's backing-mission loop unit for this tick. Routes
        /// through the <see cref="LoopUnitResolverForTesting"/> seam when set;
        /// otherwise builds it from live KSP state via the same selector / Build
        /// path the host push seams use (<see cref="RouteGhostDriverSelector"/> +
        /// the LOCKED <c>MissionLoopUnitBuilder.Build</c>) over the SAME
        /// <c>RecordingStore.CommittedRecordings</c> / <c>CommittedTrees</c>
        /// snapshot, so member indices align. v0 passes <c>bodyInfo:null</c> (no
        /// re-aim). Returns null when no unit owns the route's members this tick.
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

            GhostPlaybackLogic.LoopUnitSet set = MissionLoopUnitBuilder.Build(
                new List<Mission> { mission }, trees, committed, autoLoopIntervalSeconds, bodyInfo: null);

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
            // Sequence 1, origin/funds debit, sets KscDispatchFundsCost).
            EmitDispatchDebit(route, currentUT, env, cycleId);

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

            // Emit the dispatch + debit pair + the origin/funds debit (shared
            // with the loop-clock path via EmitDispatchDebit).
            EmitDispatchDebit(route, currentUT, env, cycleId);

            // State transition: Active/Wait* → InTransit, advance the next-due
            // UT by one DispatchInterval, clear the retry timer. (Loop-routes do
            // NOT reach this method — their EmitLoopCycle path skips the InTransit
            // self-timer entirely; see ProcessLoopRoute.)
            route.TransitionTo(RouteStatus.InTransit, "dispatched");
            route.CurrentCycleStartUT = currentUT;
            route.NextDispatchUT = currentUT + route.DispatchInterval;
            route.NextEligibilityCheckUT = null;

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
        /// </summary>
        internal static void EmitDispatchDebit(
            Route route, double currentUT, IRouteRuntimeEnvironment env, string cycleId)
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
            var debitedAction = new GameAction
            {
                Type = GameActionType.RouteCargoDebited,
                UT = currentUT,
                RouteId = route.Id,
                RouteCycleId = cycleId,
                RouteStopIndex = -1,
                Sequence = 1,
                RouteResourceManifest = CloneManifest(route.CostManifest),
                // RouteKscFundsCost is float-typed on GameAction by design;
                // double precision is preserved on Route.KscDispatchFundsCost
                // for diagnostics.
                RouteKscFundsCost = isCareerKsc ? (float)computedCost : 0f,
            };

            Ledger.AddActions(new[] { dispatchedAction, debitedAction });

            ParsekLog.Info(Tag,
                $"DispatchDebit: route {ShortIdForLog(route)} cycle={cycleId} " +
                $"ut={currentUT.ToString("R", IC)} " +
                $"cost={computedCost.ToString("R", IC)} " +
                $"careerKsc={(isCareerKsc ? "1" : "0")}");
        }

        /// <summary>
        /// Wait-state applier (resources / funds / destination-full). Updates
        /// <c>Status</c> and <c>NextEligibilityCheckUT</c>; per design §10.4
        /// <c>NextDispatchUT</c> is intentionally NOT touched, so a destination-
        /// full route holds its cycle slot until the destination has capacity.
        /// </summary>
        private static void ApplyWait(Route route, RouteDispatchDecision decision)
        {
            if (decision.NextStatus.HasValue)
                route.TransitionTo(decision.NextStatus.Value, decision.Reason);

            route.NextEligibilityCheckUT = decision.NewNextEligibilityCheckUT;

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
                ParsekLog.Info(Tag,
                    $"Delivery: route {ShortIdForLog(route)} cycle={cycleId} endpoint lost at delivery " +
                    $"reason={endpointReason ?? "<none>"}");
                EmitEndpointLostAction(route, currentUT, "endpoint-destroyed-at-delivery:" + (endpointReason ?? "unknown"));
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

        // ==================================================================
        // Helpers
        // ==================================================================

        /// <summary>
        /// Locates the route's source <see cref="Recording"/> in ERS, then
        /// computes the dispatch funds cost from its <c>VesselSnapshot</c> via
        /// <see cref="RouteFundsCalculator.ComputeDispatchFundsCost"/>. Returns
        /// 0 when ERS cannot be queried (e.g. during early load) or the
        /// recording / snapshot is missing.
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

            return RouteFundsCalculator.ComputeDispatchFundsCost(
                source.VesselSnapshot,
                LiveRouteRuntimeEnvironment.LookupPartCost,
                LiveRouteRuntimeEnvironment.LookupResourceUnitCost);
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

        private static string ShortIdForLog(Route route)
        {
            if (route == null || string.IsNullOrEmpty(route.Id)) return "<no-id>";
            return route.Id.Length > 8 ? route.Id.Substring(0, 8) : route.Id;
        }
    }
}
