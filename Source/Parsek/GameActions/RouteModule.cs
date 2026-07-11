using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Second-tier route resource module skeleton (design doc §6, §10).
    ///
    /// Tracks per-route state across the recalculation walk: how many cycles have
    /// dispatched, how many stops have delivered, whether the route is currently
    /// paused, and whether endpoint resolution has failed. This is the data shape
    /// that future dispatch / delivery / pause / endpoint code will hang off of.
    ///
    /// <para><b>Skeleton scope:</b> the module ONLY observes the action stream the
    /// engine feeds it. It does not reach into the committed recordings store or
    /// the live ledger action list directly (which is why it does not need an entry
    /// in the ERS/ELS allowlist), it does not mutate stock funds / cargo /
    /// inventory, and it does not synthesize any new actions. Affordability gating,
    /// KSC funds dispatch charging, cargo debit, endpoint resolution, and
    /// pause/unpause UI are all future-integration work.</para>
    ///
    /// <para><b>Tier placement (see <see cref="LedgerOrchestrator.Initialize"/>):</b>
    /// registered at <see cref="RecalculationEngine.ModuleTier.SecondTier"/> AFTER
    /// <see cref="FundsModule"/>. The eventual route module will (a) read
    /// <see cref="GameAction.Affordable"/> set by FundsModule to gate KSC-origin
    /// Career dispatch and (b) potentially synthesize cascading FundsSpending-
    /// equivalent debits via <see cref="GameActionType.RouteCargoDebited"/>. Both
    /// require running after FundsModule in the same tier walk. The skeleton does
    /// not consume Affordable yet, but the tier slot is locked in now so the
    /// integration phase is a one-line wire-up.</para>
    ///
    /// <para><b>Future standalone-mod boundary:</b> the route system is the next
    /// candidate for the same standalone-mod treatment GhostPlaybackEngine
    /// already got. To stay portable, this module must never reach into Parsek-
    /// specific recording stores — its only inputs are <see cref="GameAction"/>
    /// objects fed by the engine.</para>
    /// </summary>
    internal class RouteModule : IResourceModule
    {
        private const string Tag = "Route";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Per-route accumulated walk state. One instance per <see cref="GameAction.RouteId"/>.
        /// Recomputed from scratch on every <see cref="Reset"/> + walk pair.
        /// </summary>
        internal sealed class RouteWalkState
        {
            /// <summary>Count of <see cref="GameActionType.RouteDispatched"/> rows seen for this route.</summary>
            internal int DispatchedCycles;

            /// <summary>Count of <see cref="GameActionType.RouteCargoDelivered"/> rows seen for this route.</summary>
            internal int DeliveredStops;

            /// <summary>Count of <see cref="GameActionType.RouteRecoveryCredited"/> rows seen for this route
            /// (logistics-recovery-credit, design doc section 6.2). DIAGNOSTIC ONLY. The deferred
            /// recovery credit's FUNDS effect is reversed by <see cref="FundsModule"/> (which processes
            /// the credit as an earning); <see cref="RouteModule"/> only OBSERVES the row so the per-route
            /// walk summary reflects how many credits a route has paid back. This module MUST NOT mutate
            /// funds for the credit (the observe-only design-doc contract, section 13.4): a future edit
            /// that adds a funds mutation here would double-count the credit and is pinned out by
            /// T-ROUTEMODULE-OBSERVE.</summary>
            internal int CreditedCycles;

            /// <summary>Count of <see cref="GameActionType.RouteCargoDebited"/> rows that look like
            /// PHYSICAL origin debits (non-empty resource manifest with zero KSC funds cost; M1).
            /// DIAGNOSTIC ONLY - the physical removal happened live at emit time
            /// (<c>RouteOrchestrator.EmitDispatchDebit</c> loop path) and is reverted by the rewind
            /// quicksave restore, NOT by this walk. RouteModule observes the row and mutates
            /// nothing (T-ROUTEMODULE-OBSERVE).</summary>
            internal int PhysicalDebits;

            /// <summary>Count of <see cref="GameActionType.RouteCargoPickedUp"/> rows with a
            /// resolved pickup ENDPOINT (non-zero <see cref="GameAction.RouteOriginVesselPid"/>;
            /// M3 per-window pickup debit). DIAGNOSTIC ONLY - same observe-only contract as
            /// <see cref="PhysicalDebits"/>: the cargo was physically removed from the endpoint
            /// LIVE at emit time (<c>RouteOrchestrator.EmitLoopCycle</c> pickup half) and is
            /// reverted by the rewind quicksave restore, NOT by this walk. RouteModule observes
            /// the row and mutates nothing (T-ROUTEMODULE-OBSERVE).</summary>
            internal int PhysicalPickups;

            /// <summary>True once a <see cref="GameActionType.RoutePaused"/> row has been processed.
            /// Skeleton scope: this flag survives subsequent <see cref="GameActionType.RouteDispatched"/>
            /// rows on the same route — the skeleton accepts the dispatch with a warn and still
            /// bumps the counter so the timeline records the intent, but it does NOT silently
            /// clear <see cref="Paused"/>. The integration phase replaces the warn with an
            /// affordability gate and the only clearing path will be an explicit player Resume.</summary>
            internal bool Paused;

            /// <summary>True once a <see cref="GameActionType.RouteEndpointLost"/> row has been processed.
            /// Skeleton scope: this flag survives subsequent <see cref="GameActionType.RouteDispatched"/>
            /// rows on the same route — the skeleton has no explicit re-target action yet, so the
            /// flag persists until the integration phase introduces one (design doc §6.6).</summary>
            internal bool EndpointLost;

            /// <summary>Last reason text observed on a <see cref="GameActionType.RoutePaused"/>
            /// or <see cref="GameActionType.RouteEndpointLost"/> row. Free-form by design.</summary>
            internal string LastReason;

            /// <summary>UT of the most recent route action observed for this route. Useful for
            /// future "is this route stale?" decisions and for log diagnostics.</summary>
            internal double LastActionUT;
        }

        private readonly Dictionary<string, RouteWalkState> byRoute =
            new Dictionary<string, RouteWalkState>();

        /// <summary>
        /// Clears all per-route state. Called by the engine at the start of every
        /// recalculation walk.
        /// </summary>
        public void Reset()
        {
            int prevCount = byRoute.Count;
            byRoute.Clear();
            ParsekLog.Verbose(Tag, $"Reset: cleared state for {prevCount.ToString(IC)} route(s)");
        }

        /// <summary>
        /// Pre-pass over the full sorted action list. Skeleton no-op: no aggregate
        /// numbers need to be computed up-front. Future versions may pre-compute
        /// per-route action counts to size lookup dicts or detect missing routes.
        /// </summary>
        public bool PrePass(List<GameAction> actions, double? walkNowUT = null)
        {
            // Intentionally empty. The skeleton's per-route counters are accumulated
            // during ProcessAction; there is no pre-pass aggregate that would change
            // dispatch ordering or affordability.
            return false;
        }

        /// <summary>
        /// Updates per-route walk state for a single action. Non-route action types
        /// are ignored.
        /// </summary>
        public void ProcessAction(GameAction action)
        {
            if (action == null)
                return;

            switch (action.Type)
            {
                case GameActionType.RouteDispatched:
                    ProcessDispatched(action);
                    break;
                case GameActionType.RouteCargoDebited:
                    ProcessCargoDebited(action);
                    break;
                case GameActionType.RouteCargoPickedUp:
                    ProcessCargoPickedUp(action);
                    break;
                case GameActionType.RouteCargoDelivered:
                    ProcessCargoDelivered(action);
                    break;
                case GameActionType.RouteRecoveryCredited:
                    ProcessRecoveryCredited(action);
                    break;
                case GameActionType.RoutePaused:
                    ProcessPaused(action);
                    break;
                case GameActionType.RouteEndpointLost:
                    ProcessEndpointLost(action);
                    break;
                // All other action types: ignore silently — they belong to other modules.
            }
        }

        /// <summary>
        /// Walk summary. Logged at <c>Verbose</c> so it appears in collect-logs dumps
        /// for re-fly / supersede investigations but does not spam the live console
        /// during normal play.
        /// </summary>
        public void PostWalk()
        {
            int total = byRoute.Count;
            int paused = 0;
            int endpointLost = 0;
            int totalDispatched = 0;
            int totalDelivered = 0;
            int totalCredited = 0;
            int totalPhysicalDebits = 0;
            int totalPhysicalPickups = 0;

            foreach (var kv in byRoute)
            {
                if (kv.Value.Paused) paused++;
                if (kv.Value.EndpointLost) endpointLost++;
                totalDispatched += kv.Value.DispatchedCycles;
                totalDelivered += kv.Value.DeliveredStops;
                totalCredited += kv.Value.CreditedCycles;
                totalPhysicalDebits += kv.Value.PhysicalDebits;
                totalPhysicalPickups += kv.Value.PhysicalPickups;
            }

            ParsekLog.Verbose(Tag,
                $"PostWalk: routes={total.ToString(IC)}, " +
                $"paused={paused.ToString(IC)}, endpointLost={endpointLost.ToString(IC)}, " +
                $"totalDispatched={totalDispatched.ToString(IC)}, " +
                $"totalDelivered={totalDelivered.ToString(IC)}, " +
                $"totalCredited={totalCredited.ToString(IC)}, " +
                $"physicalDebits={totalPhysicalDebits.ToString(IC)}, " +
                $"physicalPickups={totalPhysicalPickups.ToString(IC)}");
        }

        // ================================================================
        // Per-action processing
        // ================================================================

        private void ProcessDispatched(GameAction action)
        {
            if (!TryGetOrCreateState(action, "RouteDispatched", out RouteWalkState state, out string routeId))
                return;

            // Skeleton rule: the FundsModule affordability gate isn't wired through
            // yet, so a paused dispatch still bumps the counter. The future
            // integration will reject this path — for now we log a warn and
            // continue so the timeline reflects intent. The Paused flag is NOT
            // cleared here: the skeleton has no explicit Resume action, so every
            // subsequent dispatch on a paused route must keep warning until an
            // explicit clear path lands in the integration phase.
            if (state.Paused)
            {
                ParsekLog.Warn(Tag,
                    $"Dispatch on paused route {routeId}: skeleton accepts; " +
                    "future affordability gate will reject");
            }

            // EndpointLost survives subsequent dispatches for the same reason —
            // there is no explicit re-target action in the skeleton, so a flagged
            // route stays flagged until the integration phase introduces one
            // (design doc §6.6).
            if (state.EndpointLost)
            {
                ParsekLog.Verbose(Tag,
                    $"Dispatch on EndpointLost route {routeId}: skeleton accepts; " +
                    "flag persists until explicit re-target lands");
            }

            state.DispatchedCycles++;
            state.LastActionUT = action.UT;

            ParsekLog.Info(Tag,
                $"Processed RouteDispatched for route={routeId} " +
                $"cycle={state.DispatchedCycles.ToString(IC)}, " +
                $"cycleId={action.RouteCycleId ?? "(none)"}, " +
                $"ut={action.UT.ToString("R", IC)}");
        }

        /// <summary>
        /// Observes a <see cref="GameActionType.RouteCargoDebited"/> row.
        /// OBSERVE-ONLY (T-ROUTEMODULE-OBSERVE): the M1 physical origin debit
        /// is applied LIVE at emit time by the orchestrator's loop path and
        /// reverted by the rewind quicksave restore; this walk only counts the
        /// row (heuristic: non-empty manifest with zero funds = physical
        /// debit) and logs its attribution fields. The funds component alone
        /// replays through <see cref="FundsModule"/>. A future edit MUST NOT
        /// mutate vessel cargo from here - rewriting tanks from the recalc
        /// walk is the one thing the apply pattern forbids.
        /// </summary>
        private void ProcessCargoDebited(GameAction action)
        {
            if (!TryGetOrCreateState(action, "RouteCargoDebited", out RouteWalkState state, out string routeId))
                return;

            state.LastActionUT = action.UT;

            int manifestSize = action.RouteResourceManifest?.Count ?? 0;
            int requestedSize = action.RouteRequestedResourceManifest?.Count ?? 0;
            // Origin pid is set only by actual physical debits (M1 loop-path
            // emit); legacy non-loop non-KSC rows carry a CostManifest clone
            // with zero funds but no pid, and must not count as physical.
            if (action.RouteOriginVesselPid != 0u)
                state.PhysicalDebits++;

            ParsekLog.Verbose(Tag,
                $"Processed RouteCargoDebited for route={routeId}, " +
                $"cycleId={action.RouteCycleId ?? "(none)"}, " +
                $"resources={manifestSize.ToString(IC)}, " +
                $"requested={requestedSize.ToString(IC)}, " +
                $"originPid={action.RouteOriginVesselPid.ToString(IC)}, " +
                $"kscFundsCost={action.RouteKscFundsCost.ToString("R", IC)}, " +
                $"ut={action.UT.ToString("R", IC)} (observe-only, no cargo mutation)");
        }

        /// <summary>
        /// Observes a <see cref="GameActionType.RouteCargoPickedUp"/> row (M3
        /// per-window pickup debit). OBSERVE-ONLY (T-ROUTEMODULE-OBSERVE), the
        /// exact mirror of <see cref="ProcessCargoDebited"/>: the physical removal
        /// of cargo FROM the pickup endpoint happened LIVE at emit time
        /// (<c>RouteOrchestrator.EmitLoopCycle</c> pickup half) and is reverted by
        /// the rewind quicksave restore; this walk only counts the row (a resolved
        /// endpoint pid = physical pickup) and logs its attribution fields. A
        /// pickup row carries NO funds, so there is nothing for
        /// <see cref="FundsModule"/> to replay either. A future edit MUST NOT
        /// mutate vessel cargo from here - rewriting tanks from the recalc walk is
        /// the one thing the apply pattern forbids.
        /// </summary>
        private void ProcessCargoPickedUp(GameAction action)
        {
            if (!TryGetOrCreateState(action, "RouteCargoPickedUp", out RouteWalkState state, out string routeId))
                return;

            state.LastActionUT = action.UT;

            int manifestSize = action.RouteResourceManifest?.Count ?? 0;
            int requestedSize = action.RouteRequestedResourceManifest?.Count ?? 0;
            // M3 Phase 5: the picked-up INVENTORY rides the same row; observe-only
            // (the stored-part removal happened live at emit, reverted by rewind).
            int inventorySize = action.RouteInventoryManifest?.Count ?? 0;
            // Endpoint pid is set only by an actual physical pickup debit (M3
            // loop-path emit); an unresolved-at-emit pickup carries a full
            // requested manifest with pid 0 and must not count as physical.
            if (action.RouteOriginVesselPid != 0u)
                state.PhysicalPickups++;

            ParsekLog.Verbose(Tag,
                $"Processed RouteCargoPickedUp for route={routeId}, " +
                $"cycleId={action.RouteCycleId ?? "(none)"}, " +
                $"resources={manifestSize.ToString(IC)}, " +
                $"requested={requestedSize.ToString(IC)}, " +
                $"inventory={inventorySize.ToString(IC)}, " +
                $"endpointPid={action.RouteOriginVesselPid.ToString(IC)}, " +
                $"ut={action.UT.ToString("R", IC)} (observe-only, no cargo mutation)");
        }

        private void ProcessCargoDelivered(GameAction action)
        {
            if (!TryGetOrCreateState(action, "RouteCargoDelivered", out RouteWalkState state, out string routeId))
                return;

            // Design doc §6.3 says delivery follows dispatch. If we somehow see a
            // delivery for a route with no prior dispatch in this walk, that's a
            // data ordering bug (or a corrupted save) — warn and skip the bump so
            // the counter stays meaningful.
            if (state.DispatchedCycles == 0)
            {
                ParsekLog.Warn(Tag,
                    $"Out-of-order delivery for route {routeId}: no preceding dispatch in walk; " +
                    "skipping state update");
                return;
            }

            state.DeliveredStops++;
            state.LastActionUT = action.UT;

            int manifestSize = action.RouteResourceManifest?.Count ?? 0;
            int requestedSize = action.RouteRequestedResourceManifest?.Count ?? 0;
            ParsekLog.Verbose(Tag,
                $"Processed RouteCargoDelivered for route={routeId} " +
                $"stop={action.RouteStopIndex.ToString(IC)}, " +
                $"delivered={state.DeliveredStops.ToString(IC)}, " +
                $"resources={manifestSize.ToString(IC)}, " +
                $"requested={requestedSize.ToString(IC)}, " +
                $"ut={action.UT.ToString("R", IC)}");
        }

        /// <summary>
        /// Observes a <see cref="GameActionType.RouteRecoveryCredited"/> row
        /// (logistics-recovery-credit, design doc section 6.2). DIAGNOSTIC ONLY:
        /// increments the per-route <see cref="RouteWalkState.CreditedCycles"/>
        /// counter so the <see cref="PostWalk"/> summary reflects how many deferred
        /// recovery credits a route has paid back, and updates
        /// <see cref="RouteWalkState.LastActionUT"/>. It MUST NOT mutate funds: the
        /// credit's funds reversal is <see cref="FundsModule"/>'s job (the credit
        /// flows through the FundsModule earning path so a rewind / tombstone
        /// reconciles it via PatchFunds with no new rollback code). RouteModule is
        /// observe-only by the design-doc contract (section 13.4); adding a funds
        /// mutation here would double-count the credit. Pinned by
        /// T-ROUTEMODULE-OBSERVE.
        /// </summary>
        private void ProcessRecoveryCredited(GameAction action)
        {
            if (!TryGetOrCreateState(action, "RouteRecoveryCredited", out RouteWalkState state, out string routeId))
                return;

            state.CreditedCycles++;
            state.LastActionUT = action.UT;

            ParsekLog.Verbose(Tag,
                $"Processed RouteRecoveryCredited for route={routeId} " +
                $"credited={state.CreditedCycles.ToString(IC)}, " +
                $"cycleId={action.RouteCycleId ?? "(none)"}, " +
                $"amount={action.RouteKscFundsCost.ToString("R", IC)} funds, " +
                $"ut={action.UT.ToString("R", IC)} (observe-only, no funds mutation)");
        }

        private void ProcessPaused(GameAction action)
        {
            if (!TryGetOrCreateState(action, "RoutePaused", out RouteWalkState state, out string routeId))
                return;

            state.Paused = true;
            state.LastReason = action.RouteEndpointReason;
            state.LastActionUT = action.UT;

            ParsekLog.Info(Tag,
                $"Processed RoutePaused for route={routeId}, " +
                $"reason={action.RouteEndpointReason ?? "(none)"}, " +
                $"ut={action.UT.ToString("R", IC)}");
        }

        private void ProcessEndpointLost(GameAction action)
        {
            if (!TryGetOrCreateState(action, "RouteEndpointLost", out RouteWalkState state, out string routeId))
                return;

            state.EndpointLost = true;
            state.LastReason = action.RouteEndpointReason;
            state.LastActionUT = action.UT;

            ParsekLog.Info(Tag,
                $"Processed RouteEndpointLost for route={routeId}, " +
                $"reason={action.RouteEndpointReason ?? "(none)"}, " +
                $"ut={action.UT.ToString("R", IC)}");
        }

        /// <summary>
        /// Resolves the per-route state slot for an action, creating it on first use.
        /// Returns false (with a warn log) when the action has no <see cref="GameAction.RouteId"/> —
        /// every route action MUST be route-scoped and a missing id is a producer bug.
        /// </summary>
        private bool TryGetOrCreateState(GameAction action, string label,
            out RouteWalkState state, out string routeId)
        {
            routeId = action.RouteId;
            if (string.IsNullOrEmpty(routeId))
            {
                ParsekLog.Warn(Tag,
                    $"{label} action has empty RouteId at ut={action.UT.ToString("R", IC)}; " +
                    "skipping state update");
                state = null;
                return false;
            }

            if (!byRoute.TryGetValue(routeId, out state))
            {
                state = new RouteWalkState();
                byRoute[routeId] = state;
            }
            return true;
        }

        // ================================================================
        // Test seam
        // ================================================================

        /// <summary>
        /// Exposes per-route walk state for unit tests. Follows the FundsModule
        /// internal-accessor pattern (e.g. <see cref="FundsModule.GetInitialFunds"/>) —
        /// avoids the brittleness of reflection-based inspection.
        /// </summary>
        internal IReadOnlyDictionary<string, RouteWalkState> GetWalkStateForTesting() => byRoute;
    }
}
