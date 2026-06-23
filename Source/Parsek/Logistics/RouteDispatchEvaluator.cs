namespace Parsek.Logistics
{
    /// <summary>
    /// Pure decision engine for the dispatch scheduler. Given a route, the
    /// current UT, and an <see cref="IRouteRuntimeEnvironment"/>, returns a
    /// <see cref="RouteDispatchDecision"/> describing what the orchestrator
    /// should do. No KSP statics, no mutation — the orchestrator owns the
    /// physical side effects (ledger rows, debits, transitions).
    ///
    /// <para>ONE deliberate exception to "no logging": the round-trip linking
    /// partner gate (<see cref="PartnerConstraintSatisfied"/>, M4c Phase C1)
    /// emits a Verbose audit line for its hold / seed / bypass decision. The gate
    /// runs once per dock crossing (not per frame), the project's logging policy
    /// requires every guard decision to be auditable, and the seed / bypass
    /// branches are otherwise invisible to the orchestrator (they return an
    /// ELIGIBLE result, so no hold is recorded downstream). It also resolves the
    /// partner via the <see cref="RouteStore"/> static (committedRoutes only - NOT
    /// the ERS/ELS surfaces), the same seam M4b used to read escrow, to avoid
    /// growing <see cref="IRouteRuntimeEnvironment"/> (plan RANK-10).</para>
    /// </summary>
    internal static class RouteDispatchEvaluator
    {
        /// <summary>
        /// Per-cycle eligibility-failure kinds shared by the legacy self-timer
        /// path (<see cref="EvaluateRoute"/>) and the loop-clock path
        /// (<see cref="RouteOrchestrator"/>'s <c>ProcessLoopRoute</c>, plan
        /// Phase 4). The loop path only cares whether the cycle is eligible at
        /// all; the legacy path maps each kind onto its specific
        /// <see cref="RouteDispatchDecision"/>.
        /// </summary>
        internal enum EligibilityFailureKind
        {
            /// <summary>All per-cycle conditions met.</summary>
            None = 0,
            /// <summary>Defensive ERS cross-check failed (sources stale).</summary>
            SourcesStale,
            /// <summary>A stop's destination (or the non-KSC origin) endpoint did not resolve.</summary>
            EndpointLost,
            /// <summary>Origin lacks the manifest's required resources.</summary>
            OriginLacksCargo,
            /// <summary>Career + KSC origin without funds for the dispatch cost.</summary>
            FundsShort,
            /// <summary>Destination has no capacity for the cost manifest.</summary>
            DestinationFull,
            /// <summary>
            /// Round-trip linking (M4c Phase C1, plan D12 / OQ8): this route is
            /// linked to a partner route (<see cref="Route.LinkedRouteId"/>) and
            /// the partner has NOT completed a new cycle since this route last
            /// consumed one (<c>partner.CompletedCycles &lt;= route.LastConsumedPartnerCycle</c>),
            /// so the chain constraint holds the route this cycle. NOT a
            /// <see cref="RouteStatus"/> - the route stays Active / GhostDriving and
            /// renders its loop while waiting; only the hold reason names the wait.
            /// Appended LAST so the three <c>RouteStatusPolicy</c> throw-on-default
            /// switches are untouched (they switch on <see cref="RouteStatus"/>, not
            /// this evaluator enum) and a genuinely-blocked route surfaces its real
            /// blocker first (the partner gate is ordered last in
            /// <see cref="CheckEligibility"/>).
            /// </summary>
            WaitingForPartner,
        }

        /// <summary>
        /// Pure per-cycle eligibility result. <see cref="Eligible"/> is the
        /// loop-path's only interest; the legacy path additionally reads
        /// <see cref="Kind"/> / <see cref="Reason"/> / <see cref="Shortfall"/>
        /// to build its specific <see cref="RouteDispatchDecision"/>.
        /// </summary>
        internal readonly struct EligibilityResult
        {
            internal EligibilityResult(EligibilityFailureKind kind, string reason, double shortfall)
            {
                Kind = kind;
                Reason = reason;
                Shortfall = shortfall;
            }

            internal bool Eligible => Kind == EligibilityFailureKind.None;
            internal EligibilityFailureKind Kind { get; }
            internal string Reason { get; }
            internal double Shortfall { get; }

            internal static EligibilityResult Ok() =>
                new EligibilityResult(EligibilityFailureKind.None, null, 0.0);
        }

        /// <summary>
        /// Evaluate a single route at <paramref name="currentUT"/>. See
        /// <see cref="RouteDispatchOutcome"/> for the discrete result set.
        /// </summary>
        internal static RouteDispatchDecision EvaluateRoute(
            Route route,
            double currentUT,
            IRouteRuntimeEnvironment env)
        {
            if (route == null)
                return RouteDispatchDecision.Skip("null-route");
            if (env == null)
                return RouteDispatchDecision.Skip("null-env");

            // 1. Status filter — silent skip for permanent blockers, or in-transit
            //    arrival check. Active and the wait states fall through to the
            //    full dispatch decision chain.
            switch (route.Status)
            {
                case RouteStatus.Paused:
                case RouteStatus.MissingSourceRecording:
                case RouteStatus.SourceChanged:
                    return RouteDispatchDecision.Skip("status-permanent-block");

                case RouteStatus.InTransit:
                    // Arrival: cycle has been running, transit duration has elapsed,
                    // and we have not yet emitted the pending-delivery transition.
                    if (route.CurrentCycleStartUT.HasValue
                        && currentUT >= route.CurrentCycleStartUT.Value + route.TransitDuration
                        && !route.PendingDeliveryUT.HasValue)
                    {
                        return RouteDispatchDecision.InTransitComplete();
                    }
                    return RouteDispatchDecision.Skip("in-transit-pending");

                // Active, WaitingForResources, WaitingForFunds, DestinationFull,
                // EndpointLost: fall through to the dispatch decision chain.
            }

            // 2. Rate-limit on retry.
            if (route.NextEligibilityCheckUT.HasValue && currentUT < route.NextEligibilityCheckUT.Value)
                return RouteDispatchDecision.Skip("rate-limited");

            // 3. NextDispatchUT due.
            if (currentUT < route.NextDispatchUT)
                return RouteDispatchDecision.Skip("not-due-yet");

            // 4-8. Per-cycle eligibility (ERS, endpoint, cargo, funds, capacity).
            //      Extracted into CheckEligibility (plan Phase 4 task 3) so the
            //      loop-clock path can reuse the identical gate WITHOUT this
            //      method's Dispatch->InTransit + PendingDeliveryUT self-timer
            //      machinery. Map each failure kind onto its specific decision so
            //      the legacy path's behavior is byte-identical to the inline
            //      checks it replaces.
            EligibilityResult elig = CheckEligibility(route, currentUT, env);
            switch (elig.Kind)
            {
                case EligibilityFailureKind.None:
                    return RouteDispatchDecision.Dispatch();

                case EligibilityFailureKind.SourcesStale:
                    return RouteDispatchDecision.Skip(elig.Reason);

                case EligibilityFailureKind.EndpointLost:
                    return RouteDispatchDecision.EndpointLost(
                        currentUT + RouteOrchestrator.WaitRetryIntervalSec,
                        elig.Reason);

                case EligibilityFailureKind.OriginLacksCargo:
                    return RouteDispatchDecision.WaitResources(
                        currentUT + RouteOrchestrator.WaitRetryIntervalSec,
                        elig.Reason);

                case EligibilityFailureKind.FundsShort:
                    return RouteDispatchDecision.WaitFunds(
                        currentUT + RouteOrchestrator.WaitRetryIntervalSec,
                        elig.Shortfall);

                case EligibilityFailureKind.DestinationFull:
                    return RouteDispatchDecision.WaitDestinationFull(
                        currentUT + RouteOrchestrator.WaitRetryIntervalSec,
                        elig.Reason);

                case EligibilityFailureKind.WaitingForPartner:
                    // Round-trip linking (M4c, D12). v0 has no non-loop dispatch
                    // model, so the legacy self-timer path is dead for every loop
                    // route; a chain-linked route is always a loop route. Map the
                    // wait to a plain Skip (the chain constraint is not a route
                    // STATUS, so there is no Wait* decision for it) - the route
                    // simply does not dispatch this evaluation. The loop-clock path
                    // (ProcessLoopRoute) is the live consumer; it records the hold.
                    return RouteDispatchDecision.Skip(elig.Reason);

                default:
                    return RouteDispatchDecision.Skip("eligibility-unknown");
            }
        }

        /// <summary>
        /// Pure per-cycle eligibility gate (plan Phase 4 task 3, must-fix #3).
        /// Runs ONLY the per-cycle condition checks (ERS sources, endpoint
        /// resolution for every stop + non-KSC origin, origin cargo, Career/KSC
        /// funds, destination capacity) and returns the first failure (or
        /// <see cref="EligibilityResult.Ok"/>). It does NOT touch route status,
        /// scheduling, or the <c>PendingDeliveryUT</c> self-timer — that
        /// machinery is owned by <see cref="EvaluateRoute"/> for the legacy path
        /// and is intentionally bypassed by the loop-clock path, which calls this
        /// helper directly before emitting a full cycle. The check ORDER mirrors
        /// the inline <see cref="EvaluateRoute"/> steps 4-8 so both paths surface
        /// the same first-failure reason. Pure: no logging, no mutation.
        /// </summary>
        internal static EligibilityResult CheckEligibility(
            Route route,
            double currentUT,
            IRouteRuntimeEnvironment env)
        {
            if (route == null)
                return new EligibilityResult(EligibilityFailureKind.SourcesStale, "null-route", 0.0);
            if (env == null)
                return new EligibilityResult(EligibilityFailureKind.SourcesStale, "null-env", 0.0);

            // 4. ERS validation (defensive — RouteStore.RevalidateSources owns the canonical check).
            if (!env.RouteHasValidSourcesInErs(route))
                return new EligibilityResult(EligibilityFailureKind.SourcesStale, "sources-stale", 0.0);

            // 5. Endpoint resolution — every stop, plus the origin when it is a vessel (non-KSC).
            if (route.Stops != null)
            {
                for (int i = 0; i < route.Stops.Count; i++)
                {
                    RouteStop stop = route.Stops[i];
                    if (stop == null) continue;

                    if (!env.TryResolveEndpoint(stop.Endpoint, out string epReason))
                    {
                        return new EligibilityResult(
                            EligibilityFailureKind.EndpointLost, $"stop-{i}-{epReason}", 0.0);
                    }
                }
            }
            // Harvest origins (M2, plan D7) have NO origin vessel: the Origin
            // endpoint is a display-only harvest-site descriptor (pid 0), so
            // there is nothing to resolve - skipping keeps the route from
            // holding EndpointLost forever. The origin-cargo gate below still
            // runs; the env answers true for harvest origins (nothing to gate).
            if (!route.IsKscOrigin && !route.IsHarvestOrigin
                && !env.TryResolveEndpoint(route.Origin, out string originReason))
            {
                return new EligibilityResult(
                    EligibilityFailureKind.EndpointLost, $"origin-{originReason}", 0.0);
            }

            // 6. Origin has cargo.
            if (!env.OriginHasCargo(route, out string lackingResource))
            {
                return new EligibilityResult(
                    EligibilityFailureKind.OriginLacksCargo, lackingResource, 0.0);
            }

            // 7. Career funds (only when Career mode AND KSC origin).
            if (env.IsCareer && route.IsKscOrigin)
            {
                if (!env.KscFundsAvailable(route, out double shortfall))
                {
                    return new EligibilityResult(
                        EligibilityFailureKind.FundsShort, "funds-short", shortfall);
                }
            }

            // 8. Destination capacity.
            if (!env.DestinationHasCapacity(route, out string fullResource))
            {
                return new EligibilityResult(
                    EligibilityFailureKind.DestinationFull, fullResource, 0.0);
            }

            // 9. Round-trip linking chain constraint (M4c Phase C1, plan D12 / OQ8).
            //    Ordered LAST so a genuinely-blocked route (sources / endpoint /
            //    cargo / funds / capacity) surfaces its real blocker first - the
            //    partner wait is the lowest-priority hold. Resolved via
            //    RouteStore.TryGetRoute (a static, the same seam B1 used to read
            //    escrow) rather than a new IRouteRuntimeEnvironment member, so the
            //    ~14 env fakes are untouched (plan RANK-10). RouteStore reads its
            //    own committedRoutes list only (not the recording / ledger action
            //    surfaces the ERS/ELS audit gate guards), so there is no audit-gate
            //    concern.
            if (!PartnerConstraintSatisfied(route, out string partnerReason))
            {
                return new EligibilityResult(
                    EligibilityFailureKind.WaitingForPartner, partnerReason, 0.0);
            }

            // All conditions met.
            return EligibilityResult.Ok();
        }

        /// <summary>
        /// Round-trip linking chain constraint (M4c Phase C1, plan D12 / OQ8).
        /// Returns TRUE when this route is free to dispatch with respect to its
        /// linked partner, FALSE (with <paramref name="reason"/> set to a
        /// <c>partner:</c>-prefixed hold token) when the chain constraint holds it.
        ///
        /// <para><b>Unlinked routes are byte-behaviour-identical.</b> A null /
        /// empty <see cref="Route.LinkedRouteId"/> returns true immediately
        /// (no RouteStore read, no log), so pre-M4c routes behave exactly as
        /// before.</para>
        ///
        /// <para><b>Bypass when the partner is missing / Paused / not
        /// dispatching</b> (design 10.14): if the partner cannot be resolved
        /// (<see cref="RouteStore.TryGetRoute"/> false), or it is Paused, or it is
        /// in a non-ghost-driving status (the three Broken states), the constraint
        /// does NOT apply - this route dispatches on its own schedule. The bypass
        /// set is precisely <c>!RouteStatusPolicy.GhostDriving(partner.Status)</c>
        /// (Paused + EndpointLost + MissingSourceRecording + SourceChanged), which
        /// is exactly the set of partners that will never complete a cycle to
        /// satisfy the constraint, so holding on them would stall this route
        /// forever.</para>
        ///
        /// <para><b>The hold</b>: a linked, live partner that has NOT completed a
        /// new cycle since this route last consumed one
        /// (<c>partner.CompletedCycles &lt;= route.LastConsumedPartnerCycle</c>)
        /// holds the route. The default <see cref="Route.LastConsumedPartnerCycle"/>
        /// is 0, so a never-dispatched pair both compute <c>0 &lt;= 0</c> = held -
        /// the mutual-link deadlock the seed rule below breaks.</para>
        ///
        /// <para><b>Deadlock seed</b>: when BOTH routes are linked and neither has
        /// completed a cycle (<c>partner.CompletedCycles == 0</c>), without a seed
        /// both wait forever. The SEED (the route that sorts first by
        /// <see cref="IsChainSeed"/> - lower <see cref="Route.DispatchPriority"/>,
        /// then ordinal <see cref="Route.Id"/>) treats the constraint as satisfied
        /// and dispatches first, breaking the cycle. The non-seed waits for the
        /// seed's first completion. The guard is scoped to
        /// <c>partner.CompletedCycles == 0</c> so it only ever fires on the very
        /// first cycle of a fresh chain; after the seed completes once, ordinary
        /// alternation takes over.</para>
        ///
        /// <para>Logs the gate decision (partner name, partner.CompletedCycles vs
        /// LastConsumedPartnerCycle, seed/bypass) at Verbose. Called once per
        /// crossing (not per frame), so this is not per-frame spam.</para>
        /// </summary>
        internal static bool PartnerConstraintSatisfied(Route route, out string reason)
        {
            reason = null;

            if (route == null || string.IsNullOrEmpty(route.LinkedRouteId))
                return true; // unlinked: byte-behaviour-identical, no RouteStore read

            if (!RouteStore.TryGetRoute(route.LinkedRouteId, out Route partner) || partner == null)
            {
                // Partner gone (deleted / tombstoned / never created): bypass the
                // constraint - the route dispatches on its own schedule (10.14
                // analogue: a partner that cannot complete a cycle must not stall us).
                ParsekLog.Verbose("Route",
                    $"PartnerGate: route {ShortId(route.Id)} linkedRouteId={ShortId(route.LinkedRouteId)} " +
                    "unresolved - bypassing chain constraint (dispatch allowed)");
                return true;
            }

            // Bypass when the partner is Paused / Broken (design 10.14): a partner
            // that is not ghost-driving will never advance CompletedCycles, so the
            // constraint would stall this route forever - the partner dispatches /
            // resumes on its own schedule.
            if (!RouteStatusPolicy.GhostDriving(partner.Status))
            {
                ParsekLog.Verbose("Route",
                    $"PartnerGate: route {ShortId(route.Id)} partner={ShortId(partner.Id)} " +
                    $"status={partner.Status} not ghost-driving - bypassing chain constraint (dispatch allowed)");
                return true;
            }

            // Deadlock seed: a fresh mutual chain (NEITHER route has completed a
            // cycle yet) would have BOTH routes hold at the default
            // LastConsumedPartnerCycle (both compute 0 <= 0). The SEED dispatches
            // its FIRST cycle to break the deadlock. Scoped to BOTH counts at 0
            // (route.CompletedCycles == 0 && partner.CompletedCycles == 0) so the
            // bypass fires ONCE - the seed's very first cycle. After the seed
            // completes that cycle (CompletedCycles -> 1), this guard no longer
            // fires and the seed falls to the normal alternation gate below, holding
            // until the partner completes a cycle. Without the route.CompletedCycles
            // == 0 clause the seed would re-dispatch every cycle while the partner
            // stayed at 0, starving the partner and breaking alternation.
            if (route.CompletedCycles == 0 && partner.CompletedCycles == 0
                && IsChainSeed(route, partner))
            {
                ParsekLog.Verbose("Route",
                    $"PartnerGate: route {ShortId(route.Id)} is the chain SEED " +
                    $"(prio={route.DispatchPriority} id<={ShortId(route.Id)}) and partner={ShortId(partner.Id)} " +
                    "has completed no cycle - dispatch allowed (deadlock break)");
                return true;
            }

            // Core alternation gate: hold until the partner completes a NEW cycle
            // since this route last consumed one.
            if (partner.CompletedCycles <= route.LastConsumedPartnerCycle)
            {
                reason = "partner:" + (partner.Name ?? partner.Id ?? "<unknown>");
                ParsekLog.Verbose("Route",
                    $"PartnerGate: route {ShortId(route.Id)} HOLD WaitingForPartner partner={ShortId(partner.Id)} " +
                    $"partnerCompleted={partner.CompletedCycles} lastConsumed={route.LastConsumedPartnerCycle} " +
                    $"(needs partnerCompleted > lastConsumed)");
                return false;
            }

            ParsekLog.Verbose("Route",
                $"PartnerGate: route {ShortId(route.Id)} CLEAR partner={ShortId(partner.Id)} " +
                $"partnerCompleted={partner.CompletedCycles} lastConsumed={route.LastConsumedPartnerCycle} - dispatch allowed");
            return true;
        }

        /// <summary>
        /// Deterministic chain-seed predicate (M4c, plan D12 deadlock guard): TRUE
        /// when <paramref name="route"/> sorts strictly before
        /// <paramref name="partner"/> by lower <see cref="Route.DispatchPriority"/>,
        /// then ordinal <see cref="Route.Id"/>. Mirrors the first + last keys of
        /// <see cref="RouteOrchestrator.CompareRoutesForTick"/> but deliberately
        /// OMITS the schedule-state <c>NextDispatchUT</c> middle key, so the seed is
        /// a STABLE structural property of the pair (it does not flip as the routes'
        /// schedules drift across cycles). Total / deterministic for distinct ids
        /// (the ordinal-id final key is unique). Pure.
        /// </summary>
        internal static bool IsChainSeed(Route route, Route partner)
        {
            if (route == null) return false;
            if (partner == null) return true;

            int byPriority = route.DispatchPriority.CompareTo(partner.DispatchPriority);
            if (byPriority != 0) return byPriority < 0;

            return string.CompareOrdinal(route.Id, partner.Id) < 0;
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<no-id>";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }
    }
}
