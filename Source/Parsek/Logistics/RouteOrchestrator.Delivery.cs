using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    // Delivery: applying a delivery (whole or from a plan), the partial-delivery summary, the already-logged checks, live-parts endpoints and per-resource delivery rules.
    internal static partial class RouteOrchestrator
    {
        // ==================================================================
        // Delivery applier (item 6 Phase B)
        // ==================================================================

        /// <summary>
        /// Apply a pending delivery cycle (item 6 Phase B). Re-resolves the
        /// destination endpoint, builds a capacity probe against the live
        /// vessel (loaded or unloaded), asks the pure planner what to deliver,
        /// applies the planned resource + inventory writes, emits a <see cref="RouteEventType.RouteCargoDelivered"/>
        /// route event, and transitions the route back to
        /// <see cref="RouteStatus.Active"/>. Idempotent against a replayed cycle
        /// via an event-log lookup keyed on <c>(RouteId, RouteCycleId)</c>.
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

            // STEP 1: idempotency guard. If the event log already contains a
            // RouteCargoDelivered row for (routeId, cycleId, stopIndex), THIS
            // WINDOW was delivered on a previous tick (or recovered after crash);
            // applying again would double-write resources.
            // M4a A3: keyed per-window (stopIndex) so window 0's row does NOT
            // suppress windows 1..N (the RANK-1 hole). This is the orchestrator's
            // ONLY event-log read — every other consumer routes through ERS.
            if (IsDeliveryAlreadyLogged(route.Id, cycleId, stopIndex))
            {
                ParsekLog.Verbose(Tag,
                    $"Delivery: route {ShortIdForLog(route)} cycle={cycleId} " +
                    $"stop={stopIndex.ToString(IC)} replay detected — already logged");
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
                // Honor an armed PauseAfterCurrentCycle on the replay too
                // (route-timeline events, review finding 3 on PR #1327): the
                // crash-recovery replay means the delivered row landed but the
                // armed-pause tail in ApplyDeliveryFromPlan never ran, so
                // without this branch a Send-Once route ended up Active with
                // BOTH flags still armed — stamping a second one-shot row and
                // landing its pause marker one cycle late. Mirrors the armed
                // tail: consume the flags, pause, emit the RoutePaused marker at this
                // window's stride slot (+4, after the delivered row's +3), and
                // drop any held escrow (quiesce transition). The dedup
                // semantics are preserved: no delivery/debit row is
                // (re-)emitted here — only the pause marker the crash lost.
                //
                // SENDONCE-RESIDUAL-PATHS item 1: gated on bumpCompletedCycle,
                // the cycle-RESOLUTION signal (true exactly when this delivery IS
                // the whole cycle - the single-stop / legacy path). Every
                // multi-stop window passes false because ProcessMultiStopCrossings
                // owns cycle completion, and it owns the armed pause with it
                // (TryHonorArmedPauseOnCompletedCycle). Without the gate, window
                // A's replay consumed the arm and paused, then window B's ordinary
                // delivery in the SAME pass put the route back to Active - the
                // one-shot silently became an endless loop.
                if (bumpCompletedCycle && route.PauseAfterCurrentCycle)
                {
                    // Captured FIRST: the toast below is posted only for the Send
                    // Once provenance, and the flag is consumed on the next line.
                    bool wasSendOnce = route.SendOnceArmed;
                    route.PauseAfterCurrentCycle = false;
                    route.SendOnceArmed = false;
                    route.TransitionTo(RouteStatus.Paused, DeliveredReplayThenPausedReason);
                    EmitRouteLifecycleMarker(
                        route, currentUT, RouteEventType.RoutePaused,
                        DeliveredReplayThenPausedReason, stopIndex * SeqStride + 4);
                    RouteStore.DropRouteEscrow(route.Id);
                    // SENDONCE-RESIDUAL-PATHS item 2: the replay backstop used to
                    // consume the arm SILENTLY - and it is the resolution shape
                    // where the player is most likely to click again, because the
                    // run produced no NEW delivery to see. Counts-free by
                    // construction: the delivered row is already in the route event log and
                    // this branch re-plans nothing, so there are no per-resource
                    // actuals to quote.
                    if (wasSendOnce)
                    {
                        ParsekLog.ScreenMessage(
                            RouteSendOncePresentation.BuildAlreadyDeliveredMessage(route.Name, route.Id),
                            RouteSendOncePresentation.ToastSeconds);
                    }
                }
                else
                {
                    route.TransitionTo(RouteStatus.Active, "delivered-replay");
                }
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
                // An armed Send Once / pause-after-run deliberately survives: an endpoint loss
                // is retryable (like the blocked path's postponement holds), not the run, and
                // every way back to ghost-driving honours or clears the arm.
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
            bool destinationIsLoaded = EndpointStoreIsLiveParts(destVessel);
            // The destination's OWN parts on that branch, shared by the probe and the
            // writers: a visiting craft docked to the station neither counts toward its
            // capacity nor receives the cargo (ROUTE-DELIVERY-INTO-DOCKED-VISITOR).
            EndpointPartScope destinationScope = RouteEndpointPartScope.ForEndpoint(
                route, stop.Endpoint, destVessel, destinationIsLoaded, "delivery");
            LiveDeliveryCapacityProbe probe = new LiveDeliveryCapacityProbe(
                destVessel, destinationIsLoaded, destinationScope);

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
                $"parts={EndpointPartScope.Describe(destinationScope)} " +
                $"plannedResources={(plan.Resources?.Count ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                $"plannedInventory={(plan.Inventory?.Count ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}");

            // STEP 5/6: apply the planned writes. Build an ApplyDeliveryContext
            // that funnels the live mutations behind delegates so the testable
            // helper does not need to instantiate Vessel /
            // ProtoPartSnapshot. The "writer" delegates are the only place
            // KSP-state mutation happens; ApplyDeliveryFromPlan owns the
            // bookkeeping (actuals, partial detection, status transition,
            // route event construction).
            var liveWriters = new LiveDeliveryWriters(route, destVessel, plan, destinationIsLoaded, destinationScope);
            var ctx = new ApplyDeliveryContext
            {
                CycleId = cycleId,
                CurrentUT = currentUT,
                StopIndex = stopIndex,
                ResourceWriter = liveWriters.WriteResource,
                ResourceActualReader = liveWriters.ReadActualResource,
                InventoryWriter = liveWriters.WriteInventory,
                InventoryActualCountReader = liveWriters.ReadInventoryActualCount,
                EventEmitter = RouteEventLog.Add,
                BumpCompletedCycle = bumpCompletedCycle,
            };
            ApplyDeliveryFromPlan(route, plan, ctx);
        }

        /// <summary>
        /// Test-injectable apply core. Idempotency, endpoint resolution, and
        /// the live-probe build all happen in <see cref="ApplyDelivery"/> (the
        /// wrapper); this method takes a pre-built <see cref="DeliveryPlan"/>
        /// and a context bag of delegates so xUnit can verify the apply
        /// bookkeeping (resource writer invocations,
        /// route event shape, status transition) without instantiating any
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

            // Apply inventory writes. Lines with an invalid AssignedSlot were
            // skipped by the planner (no empty slot on ANY container, or no
            // volume-mass headroom at probe time); the writer is only called
            // for assigned slots, storing line.Units into the assigned
            // (part, module, slot) — a manifest item bigger than its stack
            // capacity spans several lines. The writer may itself fall short
            // (stock StoreCargoPartAtSlot edge cases); the actual-count reader
            // returns the total UNITS actually stored.
            int inventoryUnitsAttempted = 0;
            int inventoryUnitsSkipped = 0;
            if (plan.Inventory != null)
            {
                for (int i = 0; i < plan.Inventory.Count; i++)
                {
                    InventoryDeliveryLine line = plan.Inventory[i];
                    if (!line.AssignedSlot.IsValid)
                    {
                        // Planner skip (no empty slot / no volume-mass
                        // headroom); Units carries the unplaced count.
                        inventoryUnitsSkipped += line.Units;
                        continue;
                    }
                    if (line.Item == null) continue;
                    ctx.InventoryWriter(line.Item, line.AssignedSlot, line.Units);
                    inventoryUnitsAttempted += line.Units;
                }
            }

            // STEP 7: build and emit the RouteCargoDelivered row. The actuals
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
            // non-earning, no resource module reads RouteCargoDelivered, and the
            // event-log readers do not assume a contiguous 0..N
            // (RecalculationEngine.SortActions = OrderBy UT, ThenBy IsEarning, ThenBy
            // Sequence; the dedup equality keys on ActionId / type-specific fields,
            // NOT Sequence). With one RouteCargoDelivered per cycle at its own UT and
            // the dispatch-before-delivery guard still holding (dispatch seq 0/1 <
            // delivery seq 3), the 0 -> 3 shift does not change any walk outcome;
            // what matters is total ordering, which holds.
            int deliverySeqBase = ctx.StopIndex >= 0 ? ctx.StopIndex * SeqStride : 0;
            var action = new RouteEvent
            {
                Type = RouteEventType.RouteCargoDelivered,
                UT = ctx.CurrentUT,
                RouteId = route.Id,
                RouteCycleId = ctx.CycleId,
                RouteStopIndex = ctx.StopIndex,
                Sequence = deliverySeqBase + 3,
                RouteResourceManifest = actualManifest,
                RouteRequestedResourceManifest = requestedManifest,
            };
            ctx.EventEmitter(action);

            // STEP 8: mutate route state + transition. M4a A3 (Horn A): the
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
            // WaitingForResources / DestinationFull pass.
            // Mirrors ApplyDispatch (line ~301).
            route.NextEligibilityCheckUT = null;

            int inventoryActual = ctx.InventoryActualCountReader();

            // Last-partial-delivery report (destination-capacity gate follow-up):
            // the dispatch gate holds a cycle the destination cannot fully fit,
            // so a partial HERE means capacity shrank between the gate and this
            // write - the undelivered remainder is lost (origin already debited,
            // the transport is a ghost) and the player must be able to see what.
            // Cycle-scoped: a multi-stop cycle fires several windows under ONE
            // cycle id, so same-cycle partials APPEND into one report and a full
            // window only clears a report from an EARLIER cycle (else window B's
            // full delivery would erase window A's recorded loss mid-cycle).
            bool sameCycleReport = string.Equals(
                route.LastPartialDeliveryCycleId, ctx.CycleId, StringComparison.Ordinal);
            if (plan.IsPartial)
            {
                string summary = BuildPartialDeliverySummary(
                    plan, ctx.ResourceActualReader, inventoryActual, inventoryUnitsAttempted);
                route.LastPartialDeliverySummary =
                    sameCycleReport && !string.IsNullOrEmpty(route.LastPartialDeliverySummary)
                        ? AppendPartialDeliverySummary(route.LastPartialDeliverySummary, summary)
                        : summary;
                route.LastPartialDeliveryUT = ctx.CurrentUT;
                route.LastPartialDeliveryCycleId = ctx.CycleId;
                ParsekLog.Warn(Tag,
                    $"Delivery: route {ShortIdForLog(route)} cycle={ctx.CycleId} PARTIAL - " +
                    $"undelivered remainder is lost: {route.LastPartialDeliverySummary}");
            }
            else if (!sameCycleReport
                && (route.LastPartialDeliverySummary != null || route.LastPartialDeliveryUT >= 0.0))
            {
                route.LastPartialDeliverySummary = null;
                route.LastPartialDeliveryUT = -1.0;
                route.LastPartialDeliveryCycleId = null;
                ParsekLog.Verbose(Tag,
                    $"Delivery: route {ShortIdForLog(route)} full delivery cleared the last-partial report");
            }

            // Honor PauseAfterCurrentCycle: a route armed by "Send Once" (or a
            // future user pause-after-cycle action) transitions to Paused
            // after its in-flight cycle completes, instead of looping back to
            // Active. The flag is consumed here (cleared) so a subsequent
            // un-pause + dispatch doesn't auto-pause again.
            //
            // SENDONCE-RESIDUAL-PATHS item 1 - the arm is honored at CYCLE
            // completion, not per WINDOW. ctx.BumpCompletedCycle is the
            // authoritative "this delivery IS the cycle's completion" signal:
            // true only on the single-stop / legacy path, false on EVERY
            // multi-stop window (ProcessMultiStopCrossings owns the once-per-cycle
            // bump, and owns the armed pause with it - see
            // TryHonorArmedPauseOnCompletedCycle). Before the gate, a multi-stop
            // cycle with both windows due in one pass paused on window A (toast:
            // "route is now Paused"), then window B fell into the ordinary else
            // below and transitioned the route BACK to Active: the ghost looped
            // forever and the toast had lied.
            if (ctx.BumpCompletedCycle && route.PauseAfterCurrentCycle)
            {
                route.PauseAfterCurrentCycle = false;
                // Route-timeline events: the one-shot completed; drop the Send Once
                // provenance (the dispatched row was already stamped at emit time).
                // Captured FIRST: the send-once feedback toast below is posted only
                // for that provenance, and the flag is consumed on this line.
                bool wasSendOnce = route.SendOnceArmed;
                route.SendOnceArmed = false;
                string reason = plan.IsPartial
                    ? DeliveredPartialThenPausedReason
                    : DeliveredThenPausedReason;
                route.TransitionTo(RouteStatus.Paused, reason);
                // Record the armed-pause point in the timeline. Sequence +4 lands it
                // after this window's stride block (dispatch +0 / debit +1 / pickup +2 /
                // delivery +3) so the (UT, Sequence) order reads delivery-then-pause.
                EmitRouteLifecycleMarker(
                    route, ctx.CurrentUT, RouteEventType.RoutePaused, reason,
                    deliverySeqBase + 4, ctx.EventEmitter);
                // M4b escrow-strand fix (PR #1180 clean-review Finding 1): a Send-Once-armed
                // MULTI-STOP route can pause HERE mid-PARTIAL-cycle (an early window delivered
                // this tick, a later window still pending), so it never reaches the
                // cycle-complete escrow sweep (ProcessMultiStopCrossings) and the pending
                // window's source reservation would strand, mis-gating a competing route.
                // Drop it. Idempotent no-op on the complete-cycle / single-stop / no-escrow
                // paths (this is the sixth quiesce transition; the seventh is the
                // blocked-cycle armed pause in TryHonorArmedPauseOnBlockedCycle,
                // which goes quiet for the same reason without ever delivering).
                RouteStore.DropRouteEscrow(route.Id);

                // Send-once feedback: a warp-catch-up one-shot can dispatch AND
                // deliver inside the same frame the click is consumed, so without
                // this the whole run is invisible and the click reads as "did
                // nothing". Only the Send Once provenance toasts - an ordinary
                // pause-after-cycle arm is not a run the player is watching for,
                // and no ordinary (unarmed) loop cycle toasts at all.
                if (wasSendOnce)
                {
                    ParsekLog.ScreenMessage(
                        RouteSendOncePresentation.BuildDeliveredMessage(
                            route.Name, route.Id, resourceLinesApplied, inventoryActual,
                            plan.IsPartial),
                        RouteSendOncePresentation.ToastSeconds);
                }
            }
            else
            {
                route.TransitionTo(RouteStatus.Active, plan.IsPartial ? "delivered-partial" : "delivered");
            }

            ParsekLog.Info(Tag,
                $"Delivery: route {ShortIdForLog(route)} cycle={ctx.CycleId} " +
                $"resources={resourceLinesApplied.ToString(IC)} " +
                $"inventoryUnits={inventoryActual.ToString(IC)}/{inventoryUnitsAttempted.ToString(IC)} " +
                $"inventoryUnitsSkipped={inventoryUnitsSkipped.ToString(IC)} " +
                $"partial={(plan.IsPartial ? "1" : "0")} " +
                $"ut={ctx.CurrentUT.ToString("R", IC)}");
        }

        /// <summary>
        /// Joins a same-cycle earlier window's partial summary with this
        /// window's, capped at the same length bound as
        /// <see cref="BuildPartialDeliverySummary"/> so a many-window partial
        /// cycle cannot bloat the .sfs.
        /// </summary>
        internal static string AppendPartialDeliverySummary(string existing, string addition)
        {
            const int MaxSummaryChars = 240;
            if (string.IsNullOrEmpty(existing)) return addition;
            if (string.IsNullOrEmpty(addition)) return existing;
            string combined = existing + "; " + addition;
            if (combined.Length <= MaxSummaryChars) return combined;
            return combined.Substring(0, MaxSummaryChars - 3) + "...";
        }

        /// <summary>
        /// Plain-ASCII actual-vs-requested summary of a PARTIAL delivery plan,
        /// stored on <see cref="Route.LastPartialDeliverySummary"/> for the
        /// Logistics window's detail panel. One clause per SHORT item only
        /// (full-fill lines are noise here): resources as
        /// "Name actual/requested", inventory as "partName units/qty not placed"
        /// for planner-skipped stored-part units (no slot / no volume-mass
        /// headroom), plus one trailing clause when stock rejected attempted
        /// stored-part writes (per-line write success is not tracked, only the
        /// aggregate unit count). <paramref name="inventoryUnitsAttempted"/> is
        /// the total UNITS the writer was asked to store (matching the
        /// unit-accurate actual reader); <paramref name="inventoryActualCount"/>
        /// is the total units actually stored. Length-capped so a pathological
        /// manifest cannot bloat the .sfs / the detail panel. Pure; the actual
        /// amounts come from the caller's reader (write-time truth, not the
        /// plan's estimate).
        /// </summary>
        internal static string BuildPartialDeliverySummary(
            DeliveryPlan plan,
            Func<string, double> resourceActualReader,
            int inventoryActualCount,
            int inventoryUnitsAttempted)
        {
            const int MaxSummaryChars = 240;
            var sb = new System.Text.StringBuilder();
            bool truncated = false;

            if (plan.Resources != null)
            {
                for (int i = 0; i < plan.Resources.Count; i++)
                {
                    ResourceDeliveryLine line = plan.Resources[i];
                    if (!(line.Available < line.Requested))
                        continue; // full fill - not part of the loss report
                    if (sb.Length >= MaxSummaryChars) { truncated = true; break; }
                    double actual = resourceActualReader != null
                        ? resourceActualReader(line.Name)
                        : line.Available;
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(line.Name).Append(' ')
                        .Append(actual.ToString("0.##", IC))
                        .Append('/')
                        .Append(line.Requested.ToString("0.##", IC));
                }
            }

            if (plan.Inventory != null)
            {
                for (int i = 0; i < plan.Inventory.Count; i++)
                {
                    InventoryDeliveryLine line = plan.Inventory[i];
                    // Only planner-SKIP lines report here (invalid AssignedSlot):
                    // Units carries the unplaced unit count for that item
                    // (a manifest item bigger than its stack capacity splits
                    // into delivered lines plus at most one skip line).
                    if (line.AssignedSlot.IsValid || line.Item == null || line.Units <= 0)
                        continue;
                    if (sb.Length >= MaxSummaryChars) { truncated = true; break; }
                    int total = line.Item.Quantity > 0 ? line.Item.Quantity : line.Units;
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(string.IsNullOrEmpty(line.Item.PartName) ? "<unknown>" : line.Item.PartName)
                        .Append(' ')
                        .Append(line.Units.ToString(IC))
                        .Append('/')
                        .Append(total.ToString(IC))
                        .Append(" not placed (no room)");
                }
            }

            if (inventoryActualCount < inventoryUnitsAttempted)
            {
                if (sb.Length >= MaxSummaryChars)
                {
                    truncated = true;
                }
                else
                {
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append((inventoryUnitsAttempted - inventoryActualCount).ToString(IC))
                        .Append(" stored-part unit(s) rejected by the container");
                }
            }

            if (sb.Length == 0)
                return "delivery fell short of the recorded manifest";
            if (truncated || sb.Length > MaxSummaryChars)
            {
                int keep = Math.Min(sb.Length, MaxSummaryChars - 3);
                sb.Length = keep;
                sb.Append("...");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Event-log idempotency check (item 6 Phase B). Returns <c>true</c> if
        /// any <see cref="RouteEventType.RouteCargoDelivered"/>
        /// row already exists for the given <paramref name="routeId"/> +
        /// <paramref name="cycleId"/> pair. This is the orchestrator's only
        /// event-log read - every other event-log interaction in this file goes
        /// through <see cref="RouteEventLog.Add"/> on the write side.
        /// </summary>
        /// <summary>
        /// True when a multi-stop cycle reaching its last dock lost an endpoint in one of
        /// its windows (the route stopped as <see cref="RouteStatus.EndpointLost"/> this
        /// pass) and no window of the cycle logged a
        /// <see cref="RouteEventType.RouteCargoDelivered"/> row. A cycle that delivered at
        /// any stop stays completed, matching the rewind's counter rebuild; a cycle with no
        /// lost endpoint stays completed whatever it delivered (a pure-pickup route).
        /// </summary>
        internal static bool IsLostUndeliveredMultiStopCycle(Route route, string cycleId)
        {
            if (route == null || route.Status != RouteStatus.EndpointLost)
                return false;
            if (string.IsNullOrEmpty(cycleId))
                return false;
            IReadOnlyList<RouteEvent> els = RouteEventLog.Events;
            if (els == null) return true;
            for (int i = 0; i < els.Count; i++)
            {
                RouteEvent a = els[i];
                if (a == null || a.Type != RouteEventType.RouteCargoDelivered) continue;
                if (!string.Equals(a.RouteId, route.Id, StringComparison.Ordinal)) continue;
                if (string.Equals(a.RouteCycleId, cycleId, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        internal static bool IsDeliveryAlreadyLogged(string routeId, string cycleId, int stopIndex)
        {
            if (string.IsNullOrEmpty(routeId) || string.IsNullOrEmpty(cycleId))
                return false;

            IReadOnlyList<RouteEvent> els = RouteEventLog.Events;

            if (els == null) return false;
            for (int i = 0; i < els.Count; i++)
            {
                RouteEvent a = els[i];
                if (a == null) continue;
                if (a.Type != RouteEventType.RouteCargoDelivered) continue;
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
        /// DIRECTION-AGNOSTIC per-cycle event-log idempotency check (M3 OQ4, the replay
        /// re-key). Returns <c>true</c> if any non-tombstoned
        /// <see cref="RouteEventType.RouteDispatched"/> row already exists for the
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
        /// <see cref="IsDeliveryAlreadyLogged"/> (dead for loop routes, M1 D11).
        /// Structural mirror of <see cref="IsDeliveryAlreadyLogged"/>.</para>
        /// </summary>
        internal static bool IsDispatchAlreadyLogged(string routeId, string cycleId, int stopIndex)
        {
            if (string.IsNullOrEmpty(routeId) || string.IsNullOrEmpty(cycleId))
                return false;

            IReadOnlyList<RouteEvent> els = RouteEventLog.Events;

            if (els == null) return false;
            for (int i = 0; i < els.Count; i++)
            {
                RouteEvent a = els[i];
                if (a == null) continue;
                if (a.Type != RouteEventType.RouteDispatched) continue;
                if (!string.Equals(a.RouteId, routeId, StringComparison.Ordinal)) continue;
                if (!string.Equals(a.RouteCycleId, cycleId, StringComparison.Ordinal)) continue;
                // M4a A3 (Horn A re-key): the dispatch half fires exactly ONCE per
                // cycle, so its row carries
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
        /// (C-2, defense-in-depth) Per-window event-log idempotency check for the PICKUP
        /// half. Returns <c>true</c> if any non-tombstoned
        /// <see cref="RouteEventType.RouteCargoPickedUp"/> row already exists for the
        /// given <c>(routeId, cycleId, stopIndex)</c> triple.
        ///
        /// <para><b>Why a pickup-specific backstop.</b> The pickup half
        /// (<see cref="EmitPickupHalf"/> -&gt; <see cref="ApplyPickupDebit"/>)
        /// PHYSICALLY debits the source vessel, but unlike the dispatch and delivery
        /// halves it had NO event-log idempotency guard of its own - it relied
        /// entirely on the per-stop <see cref="RouteStop.LastFiredCycleIndex"/>
        /// cursor + the dispatch-keyed backstop to never re-present. The BLOCKER-1
        /// class of cursor/event-log desync (a frozen cycleId firing a window twice
        /// under two different live cursors) showed that a cursor-only guard can be
        /// out-flanked. Keying this backstop on the per-window pickup ROW (the same
        /// (RouteId, cycleId, stopIndex) key the delivery guard uses) means even a
        /// future cursor desync can never DOUBLE-DEBIT a physical source: a
        /// second presentation of the same window finds its own
        /// <c>RouteCargoPickedUp</c> row and skips the debit. Structural mirror of
        /// <see cref="IsDeliveryAlreadyLogged"/>.</para>
        /// </summary>
        internal static bool IsPickupAlreadyLogged(string routeId, string cycleId, int stopIndex)
        {
            if (string.IsNullOrEmpty(routeId) || string.IsNullOrEmpty(cycleId))
                return false;

            IReadOnlyList<RouteEvent> els = RouteEventLog.Events;

            if (els == null) return false;
            for (int i = 0; i < els.Count; i++)
            {
                RouteEvent a = els[i];
                if (a == null) continue;
                if (a.Type != RouteEventType.RouteCargoPickedUp) continue;
                if (!string.Equals(a.RouteId, routeId, StringComparison.Ordinal)) continue;
                if (!string.Equals(a.RouteCycleId, cycleId, StringComparison.Ordinal)) continue;
                if (a.RouteStopIndex != stopIndex) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Which store an endpoint's resources and inventory live in, decided
        /// once per probe/writer pair: the live <c>Part</c> objects whenever the
        /// vessel is LOADED, packed or not; the <c>ProtoVessel</c> snapshots only
        /// when it is unloaded. A loaded vessel that is packed (on rails under
        /// warp or a time jump, still inside physics range) keeps its live parts
        /// as the authority: stock <c>Vessel.BackupVessel</c> rebuilds
        /// <c>protoVessel</c> from those parts on every save and on unload, so a
        /// write into a loaded vessel's proto snapshot is silently discarded and
        /// a read from it can go stale in either direction.
        /// </summary>
        internal static bool EndpointStoreIsLiveParts(bool vesselLoaded, bool vesselPacked)
        {
            return vesselLoaded;
        }

        internal static bool EndpointStoreIsLiveParts(Vessel vessel)
        {
            return vessel != null && EndpointStoreIsLiveParts(vessel.loaded, vessel.packed);
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

        // ==================================================================
        // Delivery seam types (item 6 Phase B)
        // ==================================================================

        /// <summary>
        /// Context bag for <see cref="ApplyDeliveryFromPlan"/>. Bundles cycle
        /// identity and the apply-writer delegates so the
        /// test side can inject fakes that record calls without touching
        /// <c>Vessel</c> / <c>RouteEventLog.Add</c>.
        /// </summary>
        internal struct ApplyDeliveryContext
        {
            public string CycleId;
            public double CurrentUT;
            public int StopIndex;
            public Action<string, double> ResourceWriter;
            public Func<string, double> ResourceActualReader;
            public Action<InventoryPayloadItem, InventorySlotAddress, int> InventoryWriter; // (item, slot-address, units)
            public Func<int> InventoryActualCountReader; // total UNITS stored, not lines
            public Action<RouteEvent> EventEmitter;

            /// <summary>
            /// M4a A3 (Horn A): true when this delivery IS the cycle's completion -
            /// i.e. the single-stop / legacy path, where one delivery is the whole
            /// cycle. EVERY multi-stop window passes FALSE, including the last:
            /// <see cref="ProcessMultiStopCrossings"/> owns the once-per-cycle
            /// completion (a pickup-only last window still completes the cycle, so
            /// no delivery window can be trusted to be "the" one). The
            /// CompletedCycles bump is gated on this flag, so an earlier window of a
            /// multi-stop cycle does NOT advance the cycleId the later windows
            /// compute.
            ///
            /// <para>SENDONCE-RESIDUAL-PATHS item 1: this is ALSO the
            /// cycle-RESOLUTION signal the armed <see cref="Route.PauseAfterCurrentCycle"/>
            /// tail is gated on, for the same reason and with the same owner - the
            /// multi-stop arm is honored by
            /// <see cref="TryHonorArmedPauseOnCompletedCycle"/> at cycle completion,
            /// never per window.</para>
            /// </summary>
            public bool BumpCompletedCycle;
        }
    }
}
