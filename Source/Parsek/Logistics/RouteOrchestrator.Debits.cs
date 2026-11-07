using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    // One cycle's emit and debits: the loop cycle and its pickup half, the origin, pickup and inventory pickup debits, and dispatch with its debit.
    internal static partial class RouteOrchestrator
    {
        /// <summary>
        /// Emits the FULL per-cycle transaction for a loop-route crossing under
        /// ONE <paramref name="cycleId"/> (plan Phase 4 must-fix #1): the
        /// EXTRACTED dispatch-debit half (<see cref="EmitDispatchDebit"/> — origin
        /// debit + <c>RouteDispatched</c> + a vessel origin's <c>RouteCargoDebited</c>)
        /// FOLLOWED BY the delivery half
        /// (<see cref="ApplyDelivery"/> — <c>RouteCargoDelivered</c>), with NO
        /// InTransit state. The route event UTs are game-time
        /// <paramref name="currentUT"/>.
        ///
        /// <para><b>Why both halves.</b> Calling <see cref="ApplyDelivery"/> alone
        /// would never debit origin, and the event-log readers (the rewind counter
        /// reconstruction, Route History) would see a delivery with no dispatch.
        /// The dispatch-debit half emits
        /// <c>RouteDispatched</c> FIRST (Sequence 0) so the event-log reader sees the
        /// dispatch before the delivery at the same UT.</para>
        ///
        /// <para><b>cycleId alignment.</b> <see cref="EmitDispatchDebit"/> uses the
        /// passed <paramref name="cycleId"/> verbatim; <see cref="ApplyDelivery"/>
        /// recomputes <c>cycle-{Completed+Skipped}</c> internally, which equals
        /// <paramref name="cycleId"/> because neither half increments those
        /// counters before delivery runs. ApplyDelivery's idempotency guard
        /// (<see cref="IsDeliveryAlreadyLogged"/>) and its CompletedCycles bump
        /// are reused VERBATIM.</para>
        ///
        /// <para><b>PendingStopIndex.</b> v0 routes are single-stop; ApplyDelivery
        /// reads <c>PendingStopIndex</c> (falling back to 0 when &lt; 0). The loop
        /// path leaves it -1 so ApplyDelivery uses stop 0, then clears it back to
        /// -1 — no InTransit hand-off state is involved.</para>
        ///
        /// <para>Returns <c>true</c> when the full cycle was emitted, <c>false</c>
        /// on the event-log replay backstop (nothing emitted) so the caller's log line
        /// stays accurate.</para>
        /// </summary>
        internal static bool EmitLoopCycle(
            Route route, double currentUT, IRouteRuntimeEnvironment env, string cycleId)
        {
            // event-log idempotency backstop (must-fix #4 + M3 OQ4 re-key).
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
            if (IsDispatchAlreadyLogged(route.Id, cycleId, -1))
            {
                // Mirror ApplyDelivery's replay branch: bump CompletedCycles so the
                // NEXT cycle's id (cycle-{Completed+Skipped}) advances past this
                // already-dispatched one. PRESERVE this bump (OQ4): without it a
                // pickup-only route's next crossing would recompute the same
                // already-logged cycleId and replay-skip FOREVER (the route would
                // render but never pick up / deliver again).
                route.CompletedCycles += 1;
                ParsekLog.Verbose(Tag,
                    $"EmitLoopCycle: route {ShortIdForLog(route)} cycle={cycleId} " +
                    $"already logged (replay, dispatch-keyed) - emitting nothing, bumped " +
                    $"completedCycles={route.CompletedCycles.ToString(IC)}");
                return false;
            }

            // Dispatch + debit half (RouteDispatched Sequence 0, a vessel origin's
            // RouteCargoDebited Sequence 1 and its origin debit). The loop
            // crossing is the recorded dock phase and sits behind the event-log replay
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
            // emits RouteCargoDelivered, and bumps
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

                // SENDONCE-RESIDUAL-PATHS item 4: this branch IS the cycle's
                // completion for a pure-PICKUP route, so it owns the armed
                // one-shot exactly as the multi-stop cycle-completion seam does.
                // The delivered path resolves the arm inside ApplyDelivery's
                // BumpCompletedCycle-gated tail, which this branch never calls;
                // without the call here a Send Once on a pickup-only single-stop
                // route completed its cycle and kept looping with both flags
                // armed - the same end state as the blocked defect (#1582),
                // reached through a different door, and disagreeing with the
                // BLOCKED resolution of the very same route (which DOES pause,
                // via TryHonorArmedPauseOnBlockedCycle). Same helper, same
                // reason/marker/toast contract; a no-op when nothing is armed,
                // so an ordinary pickup cycle is byte-identical.
                TryHonorArmedPauseOnCompletedCycle(route, currentUT, env, cycleId);
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
        /// <see cref="RouteEventType.RouteCargoPickedUp"/> row carrying the ACTUAL
        /// debited manifest, the requested-on-shortfall manifest, and the endpoint
        /// pid. The row's <c>Sequence</c> is 2 - AFTER <c>RouteDispatched</c> (Seq0)
        /// and <c>RouteCargoDebited</c> (Seq1) emitted by
        /// <see cref="EmitDispatchDebit"/> - so an event-log reader sorting by
        /// (UT, Sequence) sees dispatch before the pickup at the shared UT. A pickup debits its physical source (D6). Reuses the same loaded-gate-per-vessel capture and
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
            // half PHYSICALLY debits the source, so a cursor/event-log desync of the
            // BLOCKER-1 class (a window re-presented under a frozen cycleId) could
            // otherwise double-debit a refinery/depot. The dispatch event-log guard +
            // per-stop LastFiredCycleIndex already prevent normal re-presentation;
            // this row-keyed guard makes a double-debit IMPOSSIBLE even if those are
            // out-flanked. Symmetric with the delivery guard (STEP 1 of
            // ApplyDelivery): if THIS (routeId, cycleId, stopIndex) pickup row is
            // already in the event log, emit NOTHING (no second physical debit, no second
            // row). Single-stop / legacy never double-fires (the dispatch backstop),
            // so this never trips for it - byte-identical there.
            if (IsPickupAlreadyLogged(route.Id, cycleId, pickupStopIndex))
            {
                ParsekLog.Verbose(Tag,
                    $"PickupHalf: route {ShortIdForLog(route)} cycle={cycleId} " +
                    $"stop={pickupStopIndex.ToString(IC)} replay detected — already logged, " +
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
            // (actual manifest, requested-on-shortfall, endpoint pid) at
            // Sequence 2 (after dispatch Seq0 + debit Seq1). Carries BOTH the
            // resource AND the inventory picked-up manifests (D7). Zero actuals in
            // either dimension serialize as no manifest. pickupStopIndex was resolved
            // at the top of this method (the same value the C-2 backstop keyed on).
            var pickedUpAction = new RouteEvent
            {
                Type = RouteEventType.RouteCargoPickedUp,
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
            RouteEventLog.Add(pickedUpAction);

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
            bool originIsLoaded = EndpointStoreIsLiveParts(originVessel);
            // The depot's OWN parts on that branch: a craft docked to it is not debited in
            // its place (ROUTE-DELIVERY-INTO-DOCKED-VISITOR). One scope for the probe and
            // every writer below, so the plan and the drain cover the same tanks.
            EndpointPartScope originScope = RouteEndpointPartScope.ForEndpoint(
                route, route.Origin, originVessel, originIsLoaded, "origin-debit");
            var probe = new LiveOriginCargoProbe(originVessel, originIsLoaded, originScope);
            OriginDebitPlan plan = RouteOriginDebitPlanner.PrepareDebit(route, probe);

            // One plan line per debit (bounded: one resource set per cycle).
            ParsekLog.Info(Tag,
                $"OriginDebit plan: route={ShortIdForLog(route)} ut={currentUT.ToString("R", IC)} " +
                $"resources={(plan.Resources?.Count ?? 0).ToString(IC)} " +
                $"short={(plan.IsShort ? "1" : "0")} " +
                $"path={(originIsLoaded ? "loaded" : "unloaded")} " +
                $"origin={originVessel.vesselName ?? "<none>"} " +
                $"pid={originVessel.persistentId.ToString(IC)} " +
                $"parts={EndpointPartScope.Describe(originScope)}");

            var writers = new LiveOriginDebitWriters(route, originVessel, plan, originIsLoaded, originScope);
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
                var inventoryWriter = new LiveInventoryPickupWriter(originVessel, originIsLoaded, originScope);
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
        /// the loop path yet (Phase 4) and emits NO route event (the
        /// <c>RouteCargoPickedUp</c> row + replay re-key are Phase 4).
        ///
        /// <para><b>Unresolved rule (mirrors <see cref="ApplyOriginDebit"/>):</b>
        /// a resolution that returns <c>false</c> OR returns <c>true</c> with a
        /// null vessel counts as UNRESOLVED: Warn, zero actuals, FULL requested
        /// manifest. Loop-path-only in spirit (M1 D11 parity); reversible via
        /// the rewind quicksave + event-log replay keys when Phase 4 wires it in.</para>
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
            bool endpointIsLoaded = EndpointStoreIsLiveParts(endpointVessel);
            EndpointPartScope endpointScope = RouteEndpointPartScope.ForEndpointOfRoute(
                routeIdForLog, endpoint, endpointVessel, endpointIsLoaded, "pickup-debit");
            var probe = new LiveOriginCargoProbe(endpointVessel, endpointIsLoaded, endpointScope);
            OriginDebitPlan plan = RouteOriginDebitPlanner.PrepareDebit(pickupManifest, probe);

            ParsekLog.Info(Tag,
                $"PickupDebit plan: route={routeIdForLog ?? "<none>"} " +
                $"resources={(plan.Resources?.Count ?? 0).ToString(IC)} " +
                $"short={(plan.IsShort ? "1" : "0")} " +
                $"path={(endpointIsLoaded ? "loaded" : "unloaded")} " +
                $"endpoint={endpointVessel.vesselName ?? "<none>"} " +
                $"pid={endpointVessel.persistentId.ToString(IC)} " +
                $"parts={EndpointPartScope.Describe(endpointScope)}");

            var writers = new LiveOriginDebitWriters(routeIdForLog, endpointVessel, plan, endpointIsLoaded, endpointScope);
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
        /// Production M3 per-window INVENTORY pickup debit (design D7, Phase 5):
        /// the inventory analogue of <see cref="ApplyPickupDebit"/>. Given a
        /// resolved stop endpoint and a witnessed inventory pickup manifest (the
        /// loaded term), resolves the endpoint vessel via the env, captures the
        /// loaded gate ONCE for THAT vessel, and removes each witnessed item from
        /// the source via the <see cref="LiveInventoryPickupWriter"/> (loaded
        /// <c>ClearPartAtSlot</c> / unloaded STOREDPART proto-node removal, matched
        /// by <see cref="InventoryPayloadItem.IdentityHash"/> (the KIND key), lowest-slot
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
            bool endpointIsLoaded = EndpointStoreIsLiveParts(endpointVessel);
            EndpointPartScope endpointScope = RouteEndpointPartScope.ForEndpointOfRoute(
                routeIdForLog, endpoint, endpointVessel, endpointIsLoaded, "inventory-pickup-debit");
            var writer = new LiveInventoryPickupWriter(endpointVessel, endpointIsLoaded, endpointScope);

            ParsekLog.Info(Tag,
                $"InventoryPickupDebit plan: route={routeIdForLog ?? "<none>"} " +
                $"items={pickupManifest.Count.ToString(IC)} " +
                $"path={(endpointIsLoaded ? "loaded" : "unloaded")} " +
                $"endpoint={endpointVessel.vesselName ?? "<none>"} " +
                $"pid={endpointVessel.persistentId.ToString(IC)} " +
                $"parts={EndpointPartScope.Describe(endpointScope)}");

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
        /// Dispatch happy-path: emit
        /// <see cref="RouteEventType.RouteDispatched"/> (+ a vessel origin's
        /// <see cref="RouteEventType.RouteCargoDebited"/>) route events,
        /// transition the route to <see cref="RouteStatus.InTransit"/>, and
        /// advance <c>NextDispatchUT</c>. <c>NextEligibilityCheckUT</c> is
        /// cleared because the route is no longer in a wait state.
        /// </summary>
        private static void ApplyDispatch(Route route, double currentUT, IRouteRuntimeEnvironment env)
        {
            // Cycle id pins the dispatch + debit pair together in the route event log.
            // Format is "cycle-<N>" where N = total cycles ever started for this
            // route (completed + skipped — both increment past cycles even
            // though SkippedCycles is currently informational only).
            //
            // Cycle id formula: cycle-{CompletedCycles + SkippedCycles}. Item-6
            // RouteCargoDelivered must use the SAME formula so dispatch/debit/
            // deliver triple stays aligned per cycle. First cycle is cycle-0
            // (both counters start at 0).
            string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles).ToString(IC);

            // Emit the dispatch + debit pair (shared with the
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
                $"kscOrigin={(route.IsKscOrigin ? "1" : "0")} " +
                $"nextDispatchUT={route.NextDispatchUT.ToString("R", IC)}");
        }

        /// <summary>
        /// EXTRACTED dispatch-debit body shared by the legacy self-timer path
        /// (<see cref="ApplyDispatch"/>) and the loop-clock path
        /// (<see cref="EmitLoopCycle"/>, plan Phase 4 must-fix #1). Emits the
        /// <see cref="RouteEventType.RouteDispatched"/> row and, for a vessel or
        /// harvest origin, the <see cref="RouteEventType.RouteCargoDebited"/> row
        /// under the SAME <paramref name="cycleId"/>. A Space Center origin is
        /// free and debits nothing, so it writes no debit row (todo
        /// LOGISTICS-WITHOUT-MONEY). Does NOT mutate route status,
        /// scheduling, or counters — the caller owns those (the legacy path
        /// transitions to InTransit + advances NextDispatchUT; the loop path runs
        /// delivery in the same tick and snaps the cycle index). This is the
        /// origin-debit half that <see cref="ApplyDelivery"/> alone would
        /// SKIP, so the loop path must call BOTH (must-fix #1): calling delivery
        /// alone never debits origin and leaves a delivery event with no
        /// dispatch event for the event-log readers.
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
        /// behind the event-log replay backstop. The legacy <see cref="ApplyDispatch"/>
        /// path passes <c>false</c> - it fires at cycle start (not the dock
        /// phase) and has no replay backstop - keeping its rows byte-identical
        /// to v0 (unconditional <c>CostManifest</c> clone, no physical write).</para>
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
                    // it a structural no-op: no physical write).
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
            // the debit row carries the resource payload. The sparse
            // RouteSendOnce stamp (route-timeline events) marks a cycle armed by
            // the player's Send Once one-shot so the individual run is
            // identifiable in the timeline.
            var dispatchedAction = new RouteEvent
            {
                Type = RouteEventType.RouteDispatched,
                UT = currentUT,
                RouteId = route.Id,
                RouteCycleId = cycleId,
                RouteStopIndex = dispatchStopIndex,
                Sequence = dispatchSeqBase + 0,
                RouteSendOnce = route.SendOnceArmed,
            };

            // A Space Center origin is free and moves nothing out of a vessel:
            // the cycle writes the dispatch marker alone (todo
            // LOGISTICS-WITHOUT-MONEY).
            if (route.IsKscOrigin)
            {
                RouteEventLog.Add(dispatchedAction);
                ParsekLog.Info(Tag,
                    $"DispatchDebit: route {ShortIdForLog(route)} cycle={cycleId} " +
                    $"ut={currentUT.ToString("R", IC)} space-center origin: dispatch row only " +
                    "(free, no debit row)");
                return;
            }

            // RouteCargoDebited — the physical origin debit. Carries the cost
            // manifest (resource amounts). Sequence=1
            // pins the debit AFTER the dispatched row at the same UT, which
            // matters for any future walker that orders by (UT, Sequence).
            // On the physical-debit path the manifest is the ACTUAL removed
            // amounts (zero actuals serialize as no manifest) plus the sparse
            // requested-on-shortfall manifest and origin pid; on the legacy
            // path the v0 shape is preserved byte-identical
            // (unconditional CostManifest clone, no requested manifest, pid 0).
            var debitedAction = new RouteEvent
            {
                Type = RouteEventType.RouteCargoDebited,
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
                // sparse (null on the legacy path so v0 rows stay
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
            };

            RouteEventLog.AddRange(new[] { dispatchedAction, debitedAction });

            string physicalSuffix = physicalDebitApplied
                ? $" originPid={originDebit.OriginVesselPid.ToString(IC)}" +
                  $" debitedResources={(originDebit.ActualDebited?.Count ?? 0).ToString(IC)}" +
                  $" short={(originDebit.Short ? "1" : "0")}" +
                  $" unresolved={(originDebit.Unresolved ? "1" : "0")}"
                : string.Empty;
            ParsekLog.Info(Tag,
                $"DispatchDebit: route {ShortIdForLog(route)} cycle={cycleId} " +
                $"ut={currentUT.ToString("R", IC)}" + physicalSuffix);
        }
    }
}
