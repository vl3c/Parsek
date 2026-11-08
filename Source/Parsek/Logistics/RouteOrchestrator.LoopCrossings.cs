using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    // The loop-route tick: single-stop and multi-stop crossing detection, the dock-stop cap, dispatch-window basis transitions and the multi-stop window emit.
    internal static partial class RouteOrchestrator
    {
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

            // WHICH CLOCK THE DELIVERY SIDE ACTUALLY RAN. Change-based, so a v0
            // same-body route prints one `arrivalHoldSeconds=0 ...` line per route per
            // session and a hold-carrying one re-prints only when the args themselves
            // move. The change key is the cheap half (a constant on the common path);
            // the message is built only when the line emits. It exists because the two
            // clocks USED to differ silently (ROUTE-DELIVERY-CLOCK-OMITS-THE-HOLD-ARGS)
            // and nothing in the log said so.
            ParsekLog.VerboseOnChange(Tag,
                "route-loop-clock-args-" + route.Id,
                RouteLoopClock.HoldArgsChangeKey(unit),
                () => "LoopRoute clock args: route=" + ShortIdForLog(route)
                    + " unit=" + unit.OwnerIndex.ToString(IC)
                    + " schedule=" + (unit.RelaunchSchedule != null ? "1" : "0")
                    + " loiterCuts=" + (unit.LoiterCuts != null ? unit.LoiterCuts.Count.ToString(IC) : "0")
                    + " " + RouteLoopClock.DescribeHoldArgs(unit));

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

                    // SENDONCE-RESIDUAL-PATHS item 1 (second half): a pass can take the
                    // route OUT of ghost-driving status MID-TICK - an armed pause honored
                    // on the cycle it just completed or blocked, or an endpoint lost at a
                    // window's delivery. Re-check the SAME gate this method opens with
                    // (RouteStatusPolicy.GhostDriving) rather than trusting the pass's own
                    // stillDue: a later owed cycle must never be dispatched + debited on a
                    // route that stopped driving its ghost part-way through the tick.
                    if (!RouteStatusPolicy.GhostDriving(route.Status))
                    {
                        ParsekLog.Info(Tag,
                            $"LoopRoute(multi): route {ShortIdForLog(route)} left ghost-driving " +
                            $"status={route.Status} after catch-up pass {passes.ToString(IC)} " +
                            $"at ut={currentUT.ToString("R", IC)} - stopping catch-up " +
                            $"(stillDue={(stillDue ? "1" : "0")} deferred/rebased away)");
                        stillDue = false;
                        break;
                    }
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
            // M-A7 render-composition CLOCK-EVENT capture (capture point 4, route half). The DELIVERY
            // clock is now the SAME clock as the render one - TryGetRouteLoopState forwards the unit's
            // whole optional surface, hold arguments included - so this event is the instrument that
            // keeps it so rather than the instrument that measures a known divergence. Instant no-op
            // when unarmed.
            if (crossing)
            {
                Parsek.MapRender.RenderCompositionRecorder.NoteRouteDockCrossing(
                    unit.OwnerIndex, dockCycleIndex, currentUT, route.Id);
            }
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
            // has passed. Event-log (routeId, cycleId) idempotency is the backstop if a
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
                        // only), no hold, no route events, no escrow, no
                        // partner-alternation advance.
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
                // Blocked cycle: no run (no dispatch, debit or delivery; the ghost
                // still renders - "world looks busy, transfers nothing"); the only
                // row it may write is the Route History's hold row below. Bump
                // SkippedCycles and STILL snap the cycle index forward (to the
                // dock-phase cycle, DEL-2) so the blocked cycle does not re-fire
                // every tick.
                route.SkippedCycles += 1;
                route.LastObservedLoopCycleIndex = dockCycleIndex;
                // M6 hold reasons: persist the block verdict so the Logistics
                // window can name it. Zero new computation - the evaluator
                // already produced kind/reason/shortfall for the log line below.
                route.RecordHold(elig.Kind, elig.Reason, elig.Shortfall, currentUT);
                // Route History: one Held row per episode and reason, BEFORE any
                // same-UT blocked-then-paused marker the armed-pause tail emits.
                bool heldRowWritten = TryEmitRouteHeldRow(
                    route, elig.Kind, elig.Reason, elig.Shortfall, currentUT, cycleId);
                skipped++;
                ParsekLog.Info(Tag,
                    $"LoopRoute: route {ShortIdForLog(route)} cycle={cycleId} " +
                    $"BLOCKED kind={elig.Kind} reason={elig.Reason ?? "<none>"} " +
                    $"shortfall={elig.Shortfall.ToString("R", IC)} - no run, " +
                    $"holdRow={(heldRowWritten ? "1" : "0")}, " +
                    $"snapped lastObserved={dockCycleIndex.ToString(IC)} skippedCycles={route.SkippedCycles.ToString(IC)}");
                // An armed one-shot / pause-after-cycle is CONSUMED by this blocked
                // cycle: pause instead of looping on forever with the flag still
                // armed (which would silently deliver at some arbitrary future
                // crossing once the block cleared). The hold recorded above is kept.
                TryHonorArmedPauseOnBlockedCycle(route, currentUT, cycleId);
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
            // Returns false on the event-log replay backstop (nothing emitted) so the
            // log line below distinguishes a real fire from a replay no-op.
            bool emitted = EmitLoopCycle(route, currentUT, env, cycleId);

            // Snap forward in BOTH cases (event-log idempotency is the backstop;
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
                // event-log replay backstop below (a replay re-presents an already-consumed
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
                    "(already logged) — emitted nothing; " +
                    $"snapped lastObserved={dockCycleIndex.ToString(IC)} completedCycles={route.CompletedCycles.ToString(IC)}");
            }
        }

        /// <summary>
        /// (M4a A3 / Horn A) Per-window firing for a MULTI-STOP delivery route.
        /// Within ONE span cycle, N windows fire each at its OWN recorded dock
        /// phase under ONE cycleId (<c>cycle-{Completed+Skipped}</c>): the dispatch
        /// half fires once (cycle open, the once-per-cycle origin debit), each
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
        /// forward; the per-stop guard + the dispatch event-log backstop keep it
        /// idempotent.</para>
        ///
        /// <para><b>Dispatch-once / eligibility.</b> Eligibility is checked once
        /// per cycle, the first tick a window of the cycle is due. A blocked cycle
        /// emits nothing, bumps <see cref="Route.SkippedCycles"/>, snaps every
        /// stop + the route marker forward to the cycle, and records the hold. The
        /// dispatch half is gated by <see cref="IsDispatchAlreadyLogged"/> on the
        /// cycle's first-window stop index, so the second tick (window B's) does NOT
        /// re-dispatch.</para>
        ///
        /// <para><b>BLOCKER-1: one dock-cycle per pass.</b> This helper processes
        /// EXACTLY ONE dock-cycle per invocation - the LOWEST owed cycle
        /// <c>cMin</c> across the stops. When a tick straddles more than one owed
        /// cycle (a gap tick with a prior owed window, or a long warp jump), only
        /// <c>cMin</c>'s windows fire this pass; the dispatch half fires
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
            // guarded by IsDispatchAlreadyLogged on (cycleId, this carrier index).
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
            // cMin, no escrow is reserved, and stillDue
            // reports as usual so a deliverable LATER owed cycle still fires in
            // this tick's catch-up loop. A partially-fired deliverable cycle
            // always IS cMin (its stops' cursors lag there until it completes),
            // so this gate can never strand or leapfrog a half-fired cycle
            // (review Missed #4).
            //
            // M5-1 (review CONFIRMED-MINOR): cMin is the COLLAPSED owed cycle -
            // after a long warp each stop's owed-crossing emitter reports only
            // the HIGHEST passed dock cycle, so a non-deliverable cMin can hide
            // a deliverable window that was passed DURING the warp (N=2: land
            // on wrong parity and a naive skip drops a whole deliverable
            // window, ~2 synodic periods with zero deliveries). Before
            // skipping, mirror the single-stop D5 downward search on the
            // collapsed cMin (ComputeHighestDeliverableWindow, reused): when a
            // deliverable window exists in (lastObserved, cMin), RETARGET this
            // pass to fire THAT cycle in the normal one-dock-cycle-per-pass
            // shape; the still-owed collapsed cycle then reports stillDue and a
            // later pass D4-skips it, snapping the cursors to the landed
            // window. When none exists, skip exactly as before.
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
                    // M5-1 downward mirror of D5 on the collapsed cMin.
                    long deliverable = RouteLoopClock.ComputeHighestDeliverableWindow(
                        route.LastObservedLoopCycleIndex, cMin,
                        route.WindowAnchorCycleIndex, nResidual);
                    bool retargeted = false;
                    if (deliverable != RouteLoopClock.NoDeliverableWindow)
                    {
                        // Rebuild the due set + later-owed count for the
                        // retargeted cycle. Every crossing stop's collapsed
                        // DockCycleIndex is >= the collapsed cMin > deliverable,
                        // so its dock phase for the retargeted cycle was passed
                        // during the warp; a stop is due iff its own cursor has
                        // not consumed the retargeted cycle yet (the per-stop
                        // sub-gate - which also makes a pre-warp half-fired
                        // deliverable cycle resume correctly: its already-fired
                        // stops filter out here and its dispatch resumes under
                        // the existing cycle-{C+S} id via the dispatchAlready
                        // branch below). All crossing stops remain owed ABOVE
                        // the retargeted cycle, so they count as laterOwed and
                        // stillDue re-invokes the caller until the D4 skip
                        // snaps them through the landed cycle.
                        var retargetDue = new List<int>(stopCount);
                        int retargetLaterOwed = 0;
                        for (int i = 0; i < stopCount; i++)
                        {
                            RouteStop s = route.Stops[i];
                            bool windowCrossing = RouteLoopClock.TryGetOwedDockCrossing(
                                unit, loopUT, cycleIndex, s.RecordedDockUT,
                                s.LastFiredCycleIndex, out RouteLoopClock.OwedDockCrossing sOwed);
                            if (!windowCrossing)
                                continue;
                            if (s.LastFiredCycleIndex < deliverable
                                && sOwed.DockCycleIndex >= deliverable)
                                retargetDue.Add(i);
                            if (sOwed.DockCycleIndex > deliverable)
                                retargetLaterOwed++;
                        }
                        if (retargetDue.Count > 0)
                        {
                            ParsekLog.Info(Tag,
                                $"LoopRoute(multi): route {ShortIdForLog(route)} warp-collapsed window {cMin.ToString(IC)} " +
                                $"NOT deliverable (N={nResidual.ToString(IC)} " +
                                $"anchor={route.WindowAnchorCycleIndex.ToString(IC)} basis={basis}) " +
                                $"- retargeting pass to highest deliverable window {deliverable.ToString(IC)} " +
                                $"within ({route.LastObservedLoopCycleIndex.ToString(IC)}, {cMin.ToString(IC)}); " +
                                $"dueWindows={retargetDue.Count.ToString(IC)} " +
                                $"laterOwed={retargetLaterOwed.ToString(IC)}");
                            cMin = deliverable;
                            dueStopIndex = retargetDue;
                            laterOwed = retargetLaterOwed;
                            dueCount = retargetDue.Count;
                            retargeted = true;
                        }
                        else
                        {
                            // Defensive: a deliverable window below the collapsed
                            // cMin with NO due stop should be impossible (a
                            // completed cycle advances the route marker past it;
                            // a half-fired one leaves its unfired stops due).
                            // Warn + fall through to the D4 skip rather than
                            // dispatching an empty cycle.
                            ParsekLog.Warn(Tag,
                                $"LoopRoute(multi): route {ShortIdForLog(route)} downward search found " +
                                $"deliverable window {deliverable.ToString(IC)} below collapsed " +
                                $"cMin {cMin.ToString(IC)} but NO stop is due there " +
                                "- falling back to the modulo skip");
                        }
                    }
                    if (!retargeted)
                    {
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
                    // Retargeted: fall through - the pass now fires the
                    // retargeted deliverable cycle exactly like a normal cMin
                    // pass (dispatch guard, eligibility, per-window emit,
                    // deferred completion snap).
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
            bool dispatchAlready = IsDispatchAlreadyLogged(route.Id, cycleId, dispatchCarrierStopIndex);

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
                    // Blocked cycle cMin (NOT yet committed): no run for any window
                    // (the only row it may write is the Route History's hold row
                    // below). Bump SkippedCycles ONCE (C+S advances to cMin+1 for the next pass),
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
                    route.SkippedCycles += 1;
                    for (int j = 0; j < stopCount; j++)
                    {
                        if (route.Stops[j].LastFiredCycleIndex < cMin)
                            route.Stops[j].LastFiredCycleIndex = cMin;
                    }
                    if (route.LastObservedLoopCycleIndex < cMin)
                        route.LastObservedLoopCycleIndex = cMin;
                    route.RecordHold(elig.Kind, elig.Reason, elig.Shortfall, currentUT);
                    bool heldRowWritten = TryEmitRouteHeldRow(
                        route, elig.Kind, elig.Reason, elig.Shortfall, currentUT, cycleId);
                    skipped++;
                    stillDue = laterOwed > 0;
                    ParsekLog.Info(Tag,
                        $"LoopRoute(multi): route {ShortIdForLog(route)} cycle={cycleId} cMin={cMin.ToString(IC)} " +
                        $"BLOCKED kind={elig.Kind} reason={elig.Reason ?? "<none>"} " +
                        $"shortfall={elig.Shortfall.ToString("R", IC)} - no run, " +
                        $"holdRow={(heldRowWritten ? "1" : "0")}, for {dueCount.ToString(IC)} " +
                        $"cMin window(s), snapped due stops+lastObserved={cMin.ToString(IC)} " +
                        $"skippedCycles={route.SkippedCycles.ToString(IC)} stillDue={(stillDue ? "1" : "0")}");
                    // An armed one-shot / pause-after-cycle is CONSUMED by this
                    // blocked cycle (same contract as the single-stop path). The
                    // route goes quiet HERE, so the catch-up loop must stop too:
                    // clear stillDue or the caller would keep processing later owed
                    // cycles on a route the player just paused. Those owed cycles
                    // are then REBASED AWAY, deliberately: TryActivate resets every
                    // per-stop cursor and LastObservedLoopCycleIndex, so a later
                    // re-activation starts fresh instead of replaying cycles the
                    // player never authorized.
                    if (TryHonorArmedPauseOnBlockedCycle(route, currentUT, cycleId))
                        stillDue = false;
                    return;
                }

                // Eligible + first commit of the cycle: clear any prior hold BEFORE
                // emitting (a per-window endpoint loss can record an EndpointLost
                // hold inside the delivery half; one clear here covers it).
                route.ClearHold("crossing-eligible");

                // Dispatch half (once per cycle): origin debit + the
                // RouteDispatched/RouteCargoDebited pair under the cycle's carrier
                // stop index. The dispatch event-log guard suppresses a re-dispatch on the
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
                // Cycle already committed (dispatch in the route event log): fire only the due
                // window(s), no eligibility re-gate. The crossing is eligible by
                // construction (the cycle dispatched), so clear any stale hold.
                route.ClearHold("crossing-eligible");

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
                    $"dispatch already logged - firing only the due window(s), no re-gate");
            }

            // Fire each DUE window of cMin's pickup (+ delivery), in DockUT order.
            // Each window carries its OWN stop index (drives ResolveCycleStop +
            // RouteHas*Manifest via PendingStopIndex, the Sequence stride, and the
            // per-window delivery event-log guard). No window bumps CompletedCycles — the
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
                // A cycle that lost an endpoint in one of its windows and delivered
                // nothing in any window counts as skipped, as the single-stop path and
                // the rewind's counter rebuild count it. Either bump advances C+S by
                // one, so the next cycle id is the same.
                bool lostUndelivered = IsLostUndeliveredMultiStopCycle(route, cycleId);
                if (lostUndelivered)
                    route.SkippedCycles += 1;
                else
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

                // SENDONCE-RESIDUAL-PATHS item 1: an armed one-shot / pause-after-cycle
                // is resolved by the COMPLETED cycle, here - not by whichever window
                // happened to deliver first (see TryHonorArmedPauseOnCompletedCycle).
                // The route goes quiet, so the catch-up loop must stop with it: clear
                // stillDue exactly as the blocked branch does, or the caller would
                // dispatch + debit a whole further cycle on a route the player just
                // paused. Those owed cycles are then REBASED AWAY deliberately
                // (TryActivate resets every cursor on a later re-activation).
                bool armedPauseHonored =
                    TryHonorArmedPauseOnCompletedCycle(route, currentUT, env, cycleId);
                if (armedPauseHonored)
                    stillDue = false;

                ParsekLog.Info(Tag,
                    $"LoopRoute(multi): route {ShortIdForLog(route)} cycle={cycleId} cMin={cMin.ToString(IC)} " +
                    $"last dock reached — firedWindows={firedWindows.ToString(IC)} " +
                    $"snapped lastObserved={cMin.ToString(IC)} completedCycles={route.CompletedCycles.ToString(IC)} " +
                    $"skippedCycles={route.SkippedCycles.ToString(IC)} lostUndelivered={(lostUndelivered ? "1" : "0")} " +
                    $"armedPause={(armedPauseHonored ? "honored" : "none")} " +
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
            // IsPickupAlreadyLogged((RouteId, cycleId, stopIndex)) - so a >1
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
            // manifest (the per-window event-log guard inside ApplyDelivery keys on
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
    }
}
