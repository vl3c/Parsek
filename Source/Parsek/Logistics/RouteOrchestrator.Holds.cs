using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    // Holds and lifecycle transitions: wait, held rows and hold episodes, the armed pause, endpoint lost and in-transit complete, and the lifecycle marker rows.
    internal static partial class RouteOrchestrator
    {
        /// <summary>
        /// Wait-state applier (resources / destination-full). Updates
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
            // total over both token shapes). The legacy path measures no
            // shortfall, so the hold carries 0.
            route.RecordHold(HoldKindForOutcome(decision.Outcome), decision.Reason, 0.0, currentUT);
            TryEmitRouteHeldRow(route, HoldKindForOutcome(decision.Outcome),
                HeldRowDetail(route, HoldKindForOutcome(decision.Outcome), decision.Reason),
                0.0, currentUT, cycleId: null);

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
                case RouteDispatchOutcome.WaitDestinationFull:
                    return RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull;
                case RouteDispatchOutcome.EndpointLost:
                    return RouteDispatchEvaluator.EligibilityFailureKind.EndpointLost;
                default:
                    return RouteDispatchEvaluator.EligibilityFailureKind.None;
            }
        }

        /// <summary>
        /// Emits a route lifecycle marker row (<see cref="RouteEventType.RoutePaused"/> /
        /// <see cref="RouteEventType.RouteResumed"/>) so player pause/resume points are
        /// recorded in the timeline (route-timeline events). Free-standing like every
        /// route event; a rewind retires it via <see cref="RouteEventRetire"/> when it
        /// is stamped after the cutoff. Skips with a Warn when the UT could not be
        /// resolved (degenerate off-Unity context): a marker carrying a bogus UT would
        /// survive every rewind cutoff and misplace the event on the timeline. The
        /// guard treats 0 as unresolved too — some UI fallbacks surface 0 instead of
        /// -1 when Planetarium is unavailable, and UT=0 is never a trustworthy
        /// timeline position (the BUG-F cold-load lesson); no real route can exist
        /// at the very first instant of a save.
        /// <paramref name="emitter"/> is the test seam (mirrors
        /// <c>ApplyDeliveryContext.EventEmitter</c>); null routes to
        /// <see cref="RouteEventLog.Add"/>.
        /// </summary>
        internal static void EmitRouteLifecycleMarker(
            Route route, double currentUT, RouteEventType type, string reason,
            int sequence = 0, Action<RouteEvent> emitter = null)
        {
            if (currentUT <= 0.0)
            {
                ParsekLog.Warn(Tag,
                    $"LifecycleMarker: route {ShortIdForLog(route)} type={type} " +
                    $"reason={reason ?? "<none>"} SKIPPED - live UT unresolved " +
                    "(marker would carry a bogus timeline position)");
                return;
            }

            var action = new RouteEvent
            {
                Type = type,
                UT = currentUT,
                RouteId = route.Id,
                RouteStopIndex = -1,
                Sequence = sequence,
                RouteEndpointReason = reason,
            };
            if (emitter != null)
                emitter(action);
            else
                RouteEventLog.Add(action);

            ParsekLog.Info(Tag,
                $"LifecycleMarker: route {ShortIdForLog(route)} type={type} " +
                $"reason={reason ?? "<none>"} ut={currentUT.ToString("R", IC)} " +
                $"seq={sequence.ToString(IC)}");
        }

        /// <summary>
        /// Writes a <see cref="RouteEventType.RouteHeld"/> route event for a held run when
        /// its reason is new to the route's open hold episode (design doc section 6.7).
        /// Called right after <see cref="Route.RecordHold"/> at the three hold sites (the
        /// single-stop and multi-stop loop blocked branches and the legacy wait applier).
        ///
        /// <para>The "already recorded?" check reads the ROUTE EVENT LOG
        /// (<see cref="IsHoldReasonRecordedInOpenEpisode"/> over
        /// <see cref="RouteEventLog.Events"/>), never <c>Route.LastHold*</c>: the
        /// rewind reconcile clears those fields while the episode's pre-cutoff Held row
        /// survives, and a stock revert never touches them, so a field compare would write
        /// a duplicate row after a rewind and miss a row after a revert. A row is written
        /// once per (kind, detail) per episode: the episode start, and each reason not yet
        /// recorded in it. A shortfall-only change writes nothing.</para>
        ///
        /// <para>Skips with a rate-limited Warn when the live UT is unresolved (the same
        /// bogus-timeline-position guard as <see cref="EmitRouteLifecycleMarker"/>).
        /// <paramref name="els"/> and <paramref name="emitter"/> are test seams: null reads
        /// the route event log and appends through <see cref="RouteEventLog.Add"/>. Returns true
        /// when a row was written.</para>
        /// </summary>
        internal static bool TryEmitRouteHeldRow(
            Route route,
            RouteDispatchEvaluator.EligibilityFailureKind kind,
            string detail,
            double shortfall,
            double currentUT,
            string cycleId,
            IReadOnlyList<RouteEvent> els = null,
            Action<RouteEvent> emitter = null)
        {
            if (route == null || string.IsNullOrEmpty(route.Id)
                || kind == RouteDispatchEvaluator.EligibilityFailureKind.None)
                return false;

            if (currentUT <= 0.0)
            {
                ParsekLog.WarnRateLimited(Tag, "route-held-ut-" + route.Id,
                    $"RouteHeld: route {ShortIdForLog(route)} kind={kind} detail={detail ?? "<none>"} " +
                    "SKIPPED - live UT unresolved (the row would carry a bogus timeline position)");
                return false;
            }

            detail = HeldRowDetail(route, kind, detail);

            if (els == null)
                els = RouteEventLog.Events;

            if (IsHoldReasonRecordedInOpenEpisode(els, route.Id, kind, detail))
            {
                ParsekLog.VerboseRateLimited(Tag, "route-held-skip-" + route.Id,
                    $"RouteHeld: route {ShortIdForLog(route)} hold unchanged, skipped - kind={kind} " +
                    $"detail={detail ?? "<none>"} already recorded in the open episode " +
                    $"(shortfall={shortfall.ToString("R", IC)} ut={currentUT.ToString("R", IC)})");
                return false;
            }

            var action = new RouteEvent
            {
                Type = RouteEventType.RouteHeld,
                UT = currentUT,
                RouteId = route.Id,
                RouteCycleId = cycleId,
                RouteStopIndex = -1,
                Sequence = 0,
                RouteHoldKind = kind,
                RouteEndpointReason = detail,
                RouteHoldShortfall = shortfall,
            };
            if (emitter != null)
                emitter(action);
            else
                RouteEventLog.Add(action);

            ParsekLog.Info(Tag,
                $"RouteHeld: route {ShortIdForLog(route)} kind={kind} detail={detail ?? "<none>"} " +
                $"shortfall={shortfall.ToString("R", IC)} cycle={cycleId ?? "<none>"} " +
                $"ut={currentUT.ToString("R", IC)} - hold row written");
            return true;
        }

        /// <summary>
        /// The detail a <see cref="RouteEventType.RouteHeld"/> row carries: the evaluator
        /// token, made STABLE where the token itself is not: a linked-route wait is
        /// <c>partner:&lt;partner route id&gt;</c> (the evaluator token names the partner,
        /// and a rename mid-hold must not read as a new reason). Every other token passes
        /// through. Pure.
        /// </summary>
        internal static string HeldRowDetail(
            Route route, RouteDispatchEvaluator.EligibilityFailureKind kind, string detail)
        {
            switch (kind)
            {
                case RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner:
                    return route != null && !string.IsNullOrEmpty(route.LinkedRouteId)
                        ? HeldPartnerDetailPrefix + route.LinkedRouteId
                        : detail;
                default:
                    return detail;
            }
        }

        /// <summary>
        /// True when <paramref name="els"/> already holds a
        /// <see cref="RouteEventType.RouteHeld"/> row for <paramref name="routeId"/> with
        /// this (<paramref name="kind"/>, <paramref name="detail"/>) inside the route's
        /// OPEN hold episode: the route's rows after its latest
        /// <see cref="RouteEventType.RouteDispatched"/> / <see cref="RouteEventType.RoutePaused"/> /
        /// <see cref="RouteEventType.RouteResumed"/> / <see cref="RouteEventType.RouteEndpointLost"/>
        /// row, walked in time order with log order breaking ties (a same-UT dispatch
        /// emitted after a hold closes that hold's episode). Shortfall is not part of the
        /// identity. Pure; the list is passed in.
        /// </summary>
        internal static bool IsHoldReasonRecordedInOpenEpisode(
            IReadOnlyList<RouteEvent> els, string routeId,
            RouteDispatchEvaluator.EligibilityFailureKind kind, string detail)
        {
            if (els == null || string.IsNullOrEmpty(routeId))
                return false;

            var rows = new List<KeyValuePair<int, RouteEvent>>();
            for (int i = 0; i < els.Count; i++)
            {
                RouteEvent a = els[i];
                if (a == null || !string.Equals(a.RouteId, routeId, StringComparison.Ordinal))
                    continue;
                if (a.Type == RouteEventType.RouteHeld || IsHoldEpisodeBoundary(a.Type))
                    rows.Add(new KeyValuePair<int, RouteEvent>(i, a));
            }
            rows.Sort((x, y) =>
            {
                int byUt = x.Value.UT.CompareTo(y.Value.UT);
                return byUt != 0 ? byUt : x.Key.CompareTo(y.Key);
            });

            bool recorded = false;
            for (int i = 0; i < rows.Count; i++)
            {
                RouteEvent a = rows[i].Value;
                if (IsHoldEpisodeBoundary(a.Type))
                {
                    recorded = false;
                    continue;
                }
                if (a.RouteHoldKind == kind
                    && string.Equals(a.RouteEndpointReason, detail, StringComparison.Ordinal))
                    recorded = true;
            }
            return recorded;
        }

        // The route rows that close a hold episode: a run went out, the route paused or
        // was activated, or it stopped on a lost endpoint.
        private static bool IsHoldEpisodeBoundary(RouteEventType type)
        {
            return type == RouteEventType.RouteDispatched
                || type == RouteEventType.RoutePaused
                || type == RouteEventType.RouteResumed
                || type == RouteEventType.RouteEndpointLost;
        }

        /// <summary>
        /// Honors an armed <see cref="Route.PauseAfterCurrentCycle"/> on a cycle the
        /// loop path CONSUMED AS BLOCKED (eligibility failed: nothing dispatched,
        /// nothing delivered, <see cref="Route.SkippedCycles"/> bumped, the crossing
        /// index snapped forward). Mirrors the delivered-path armed tail in
        /// <see cref="ApplyDeliveryFromPlan"/> row-for-row: consume both flags,
        /// transition to <see cref="RouteStatus.Paused"/>, emit the
        /// <see cref="RouteEventType.RoutePaused"/> lifecycle marker, and drop any
        /// held escrow (a route going quiet must not strand a reservation).
        ///
        /// <para><b>Why a blocked cycle must honor the arm.</b> Without this the
        /// route stayed Active with the flag STILL armed after the one-shot's cycle
        /// was consumed: the ghost looped forever ("send once turned into an endless
        /// cycle") and the still-armed flag would silently deliver at an arbitrary
        /// future crossing whenever the block cleared. Both arming provenances want
        /// the pause here - Send Once (the one-shot's cycle IS over; a blocked cycle
        /// is a resolution, not a postponement) and Pause-while-InTransit (the player
        /// asked to pause; the cycle finished, blocked).</para>
        ///
        /// <para><b>The hold is deliberately KEPT.</b> The caller has already run
        /// <see cref="Route.RecordHold"/> for this cycle; nothing here clears it, so
        /// the Logistics window still names WHY the run did not happen next to the
        /// now-Paused row.</para>
        ///
        /// <para>Returns true when the pause was honored (the caller must then stop
        /// processing further owed cycles this tick), false when nothing was armed.</para>
        /// </summary>
        internal static bool TryHonorArmedPauseOnBlockedCycle(
            Route route, double currentUT, string cycleId)
        {
            if (route == null || !route.PauseAfterCurrentCycle)
                return false;

            // A POSTPONEMENT is not a resolution. SourcesStale (the defensive
            // ERS cross-check, which clears itself once ERS warms after a scene
            // load / mid-recalc tick) and WaitingForPartner (round-trip
            // linking's by-design wait turn - the route "keeps flying its loop
            // while it waits") block individual crossings ROUTINELY on a
            // healthy route. Consuming the one-shot on them would cancel a
            // Send Once for a reason the player cannot see coming - a linked
            // route could never be Send-Once'd during the partner's turn at
            // all. The arm survives (matching pre-fix behavior for these kinds:
            // the crossing was consumed as skipped either way) and fires on the
            // next crossing that RESOLVES - delivers, or blocks for a real
            // per-cycle reason (destination full, origin cargo,
            // endpoint lost). Every other kind consumes by default, so a future
            // kind lands on the consuming side unless deliberately classified
            // as a postponement here.
            if (IsPostponementHold(route.LastHoldKind))
            {
                ParsekLog.Verbose(Tag,
                    $"ArmedPause: route {ShortIdForLog(route)} cycle={cycleId ?? "<none>"} " +
                    $"BLOCKED by postponement kind={route.LastHoldKind} — arm kept, " +
                    $"route stays {route.Status} at ut={currentUT.ToString("R", IC)}");
                return false;
            }

            bool wasSendOnce = route.SendOnceArmed;
            RouteStatus previousStatus = route.Status;
            route.PauseAfterCurrentCycle = false;
            // Route-timeline events: the one-shot is over (consumed as blocked), so
            // drop the Send Once provenance exactly as the delivered tail does.
            route.SendOnceArmed = false;
            route.TransitionTo(RouteStatus.Paused, BlockedThenPausedReason);
            // Ordering note: the marker lands at Sequence 0, like TryPause's
            // player-pause marker.
            EmitRouteLifecycleMarker(route, currentUT, RouteEventType.RoutePaused, BlockedThenPausedReason);
            // Quiesce transition: the route stops crossing here, so any escrow held
            // from an earlier partially-fired cycle would strand and mis-gate a
            // competing route sharing the source. Idempotent no-op when nothing is held.
            RouteStore.DropRouteEscrow(route.Id);

            ParsekLog.Info(Tag,
                $"ArmedPause: route {ShortIdForLog(route)} cycle={cycleId ?? "<none>"} " +
                $"BLOCKED cycle consumed the armed pause — {previousStatus}->Paused " +
                $"reason={BlockedThenPausedReason} armedBy={(wasSendOnce ? "send-once" : "pause-after-cycle")} " +
                $"hold kind={route.LastHoldKind} detail={route.LastHoldDetail ?? "<none>"} (kept) " +
                $"at ut={currentUT.ToString("R", IC)}");

            // Send-once feedback: the click's one-shot resolved without delivering,
            // and (unlike an ordinary loop cycle) the player is waiting on THIS run,
            // so say so on screen. Only for the Send Once provenance - an ordinary
            // pause-after-cycle arm is not a run the player is watching for.
            if (wasSendOnce)
            {
                ParsekLog.ScreenMessage(
                    RouteSendOncePresentation.BuildBlockedMessage(
                        route.Name, route.Id, route.LastHoldKind, route.LastHoldDetail,
                        route.LastHoldShortfall),
                    RouteSendOncePresentation.ToastSeconds);
            }
            return true;
        }

        /// <summary>
        /// (SENDONCE-RESIDUAL-PATHS items 1 and 4) Honors an armed
        /// <see cref="Route.PauseAfterCurrentCycle"/> at CYCLE COMPLETION.
        /// Sibling of <see cref="TryHonorArmedPauseOnBlockedCycle"/> (the blocked
        /// resolution) and of <see cref="ApplyDeliveryFromPlan"/>'s delivered tail
        /// (the single-stop DELIVERED resolution).
        ///
        /// <para>Two callers, both of which own a cycle completion that no
        /// delivery tail can: <see cref="ProcessMultiStopCrossings"/>'s
        /// <c>cycleLastDockReached</c> branch (item 1), and
        /// <see cref="EmitLoopCycle"/>'s pure-PICKUP branch (item 4), which
        /// completes a single-stop cycle without ever calling
        /// <see cref="ApplyDelivery"/>.</para>
        ///
        /// <para><b>Why the caller owns this.</b> A multi-stop cycle fires N delivery
        /// windows under ONE cycleId, so no single window is "the cycle". The
        /// per-window tail used to run on whichever window happened to deliver first:
        /// window A consumed the arm and paused, then window B - same pass, no status
        /// re-check - fell into the ordinary delivered branch and put the route back
        /// to Active. The route kept looping with the toast already claiming
        /// "Paused". Cycle completion is exactly where
        /// <see cref="Route.CompletedCycles"/> is bumped and the cycle-complete escrow
        /// sweep runs (<see cref="ProcessMultiStopCrossings"/>), so the one-shot's
        /// resolution belongs on the same seam - it fires ONCE per cycle regardless of
        /// which windows emitted, which halves they emitted, or how many ticks the
        /// cycle spanned.</para>
        ///
        /// <para><b>Counts-free toast by construction.</b> The windows of one cycle can
        /// straddle several TICKS (window A at dock A, window B at dock B), so no
        /// caller-side accumulator could quote the whole cycle's per-resource actuals
        /// anyway; the partial/full discriminator IS available, off the cycle-scoped
        /// <see cref="Route.LastPartialDeliveryCycleId"/> report that
        /// <see cref="ApplyDeliveryFromPlan"/> maintains across the cycle's windows.</para>
        ///
        /// <para>Idempotent: a no-op when nothing is armed, so the single-stop
        /// DELIVERED path (which resolves inside <see cref="ApplyDeliveryFromPlan"/>) is
        /// untouched, and a multi-stop cycle whose completing window already consumed
        /// the flags cannot double-pause. Returns true when the pause was honored (the
        /// caller must then stop processing further owed cycles this tick).</para>
        /// </summary>
        internal static bool TryHonorArmedPauseOnCompletedCycle(
            Route route, double currentUT, IRouteRuntimeEnvironment env, string cycleId)
        {
            if (route == null || !route.PauseAfterCurrentCycle)
                return false;

            // Captured FIRST: the toast below is posted only for the Send Once
            // provenance, and the flag is consumed on the line after.
            bool wasSendOnce = route.SendOnceArmed;
            RouteStatus previousStatus = route.Status;
            // Cycle-scoped partial report: ApplyDeliveryFromPlan APPENDS same-cycle
            // partials under one cycleId and only clears a report from an EARLIER
            // cycle, so a match here means at least one window of THIS cycle fell
            // short - the same discriminator the single-stop tail reads off plan.IsPartial.
            bool cyclePartial = !string.IsNullOrEmpty(cycleId)
                && string.Equals(route.LastPartialDeliveryCycleId, cycleId, StringComparison.Ordinal);
            string reason = cyclePartial
                ? DeliveredPartialThenPausedReason
                : DeliveredThenPausedReason;

            route.PauseAfterCurrentCycle = false;
            route.SendOnceArmed = false;
            route.TransitionTo(RouteStatus.Paused, reason);
            // Sequence: the LAST stop's stride block + 4, so the marker sorts after
            // every window's rows at this UT (the single-stop tail uses this window's
            // own block + 4; here the "window" is the whole cycle).
            int lastStopIndex = (route.Stops != null && route.Stops.Count > 0)
                ? route.Stops.Count - 1 : 0;
            EmitRouteLifecycleMarker(
                route, currentUT, RouteEventType.RoutePaused, reason,
                lastStopIndex * SeqStride + 4);
            // Quiesce transition (the eighth): a route going quiet must not strand a
            // reservation. Idempotent no-op after the cycle-complete sweep.
            RouteStore.DropRouteEscrow(route.Id);

            ParsekLog.Info(Tag,
                $"ArmedPause: route {ShortIdForLog(route)} cycle={cycleId ?? "<none>"} " +
                $"COMPLETED cycle consumed the armed pause — {previousStatus}->Paused " +
                $"reason={reason} armedBy={(wasSendOnce ? "send-once" : "pause-after-cycle")} " +
                $"at ut={currentUT.ToString("R", IC)}");

            if (wasSendOnce)
            {
                ParsekLog.ScreenMessage(
                    RouteSendOncePresentation.BuildCycleDeliveredMessage(
                        route.Name, route.Id, cyclePartial),
                    RouteSendOncePresentation.ToastSeconds);
            }
            return true;
        }

        /// <summary>
        /// The two <see cref="RouteDispatchEvaluator.EligibilityFailureKind"/>s
        /// that are POSTPONEMENTS (the crossing self-resolves on a later tick /
        /// the partner's grant) rather than resolutions of the cycle. An armed
        /// pause survives them; see the classification rationale at the call
        /// site in <see cref="TryHonorArmedPauseOnBlockedCycle"/>. Deliberately
        /// an allowlist: a future kind consumes the arm unless classified here.
        /// </summary>
        internal static bool IsPostponementHold(
            RouteDispatchEvaluator.EligibilityFailureKind kind)
        {
            return kind == RouteDispatchEvaluator.EligibilityFailureKind.SourcesStale
                || kind == RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner;
        }

        /// <summary>
        /// Endpoint-lost applier. Transitions status, sets the retry timer, and
        /// emits a <see cref="RouteEventType.RouteEndpointLost"/> route event so
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

            var action = new RouteEvent
            {
                Type = RouteEventType.RouteEndpointLost,
                UT = currentUT,
                RouteId = route.Id,
                RouteStopIndex = -1,
                Sequence = 0,
                RouteEndpointReason = decision.Reason,
            };
            RouteEventLog.Add(action);

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

        /// <summary>
        /// Emit a <see cref="RouteEventType.RouteEndpointLost"/> route event from
        /// the delivery applier. Mirrors <see cref="ApplyEndpointLost"/> from
        /// the dispatch side but the reason string is delivery-specific so the
        /// event log / UI can distinguish "endpoint lost before dispatch" from
        /// "endpoint destroyed mid-transit".
        /// </summary>
        private static void EmitEndpointLostAction(Route route, double currentUT, string reason)
        {
            var action = new RouteEvent
            {
                Type = RouteEventType.RouteEndpointLost,
                UT = currentUT,
                RouteId = route.Id,
                RouteStopIndex = -1,
                Sequence = 0,
                RouteEndpointReason = reason,
            };
            RouteEventLog.Add(action);
            ParsekLog.Info(Tag,
                $"EndpointLost: route {ShortIdForLog(route)} reason={reason ?? "<none>"} (at delivery)");
        }
    }
}
