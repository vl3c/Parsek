using System;
using System.Collections.Generic;

namespace Parsek.Logistics
{
    /// <summary>
    /// Production <see cref="IRouteRuntimeEnvironment"/> backed by live KSP
    /// statics (<see cref="HighLogic"/>, <see cref="Funding"/>,
    /// <see cref="PartLoader"/>, <see cref="EffectiveState.ComputeERS"/>).
    /// One instance per <see cref="RouteOrchestrator.Tick(double)"/> call so the
    /// per-tick ERS snapshot stays scoped and cannot leak between ticks.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="RouteOrchestrator"/> as a file-scope class so
    /// the 1500+ LOC orchestrator file can be split along its three natural
    /// seams (env, capacity probe, writers). The funds-cost lookup that
    /// previously lived as a private static on RouteOrchestrator
    /// (<see cref="RouteOrchestrator.ComputeDispatchFundsCostForRoute"/>) is
    /// shared between this env and <see cref="RouteOrchestrator.ProcessOneRoute"/>;
    /// it stays on RouteOrchestrator as <c>internal static</c> after the split.
    ///
    /// v0 eligibility-gate contract (intentional, not a TODO):
    /// <list type="bullet">
    ///   <item><c>OriginHasCargo</c>: non-KSC origins return <c>false</c>
    ///     with reason <c>"non-ksc-origin-unsupported-in-v0"</c> so those
    ///     routes hold in <c>WaitingForResources</c> until live origin-cargo
    ///     gating ships (post-v0). KSC origins return <c>true</c>
    ///     unconditionally; funds are gated separately via
    ///     <see cref="KscFundsAvailable"/>.</item>
    ///   <item><c>DestinationHasCapacity</c>: returns <c>true</c> by design.
    ///     v0 enforces capacity at apply time via
    ///     <see cref="RouteDeliveryPlanner.PrepareDelivery"/> +
    ///     <see cref="LiveDeliveryCapacityProbe"/>, which partial-fills
    ///     each resource and records the actual-vs-requested split in the
    ///     <c>RouteCargoDelivered</c> ledger row. The eligibility-gate stub
    ///     stays so dispatch can always attempt; apply-time clamping is
    ///     the real capacity contract.</item>
    /// </list>
    /// </remarks>
    internal sealed class LiveRouteRuntimeEnvironment : IRouteRuntimeEnvironment
    {
        private const string Tag = RouteOrchestrator.Tag;

        // Per-tick ERS cache. Built lazily on first call and shared across
        // every method on this instance — multiple env queries during the
        // same tick (one per route) all see the same ERS snapshot.
        private Dictionary<string, Recording> ersById;
        private bool ersBuilt;

        public bool IsCareer
        {
            get
            {
                try
                {
                    return HighLogic.CurrentGame != null
                        && HighLogic.CurrentGame.Mode == Game.Modes.CAREER;
                }
                catch (Exception ex)
                {
                    ParsekLog.Verbose(Tag,
                        $"IsCareer probe threw {ex.GetType().Name}: {ex.Message}; defaulting false");
                    return false;
                }
            }
        }

        public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason)
        {
            // The dispatch-time check does not need the live Vessel reference
            // (cargo / capacity checks are stubbed in v0 and the delivery
            // side resolves the vessel separately via
            // <see cref="TryResolveEndpointVessel"/>). Discard the out-vessel
            // here so the env interface stays narrowly scoped for dispatch.
            return RouteEndpointResolver.TryResolveEndpoint(endpoint, out _, out reason);
        }

        public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason)
        {
            // Same resolver as the bool-only overload above, but exposes the
            // <see cref="Vessel"/> so the delivery applier can decide between
            // the loaded (live <c>parts</c>) and unloaded
            // (<c>protoVessel.protoPartSnapshots</c>) write paths.
            return RouteEndpointResolver.TryResolveEndpoint(endpoint, out vessel, out reason);
        }

        public bool OriginHasCargo(Route route, out string lackingResource)
        {
            lackingResource = string.Empty;
            if (route == null)
            {
                lackingResource = "null-route";
                return false;
            }

            if (route.IsKscOrigin)
            {
                // KSC origin: per design §6.1, KSC has unlimited cargo.
                // The dispatch cost is charged via funds (KscFundsAvailable),
                // not by checking a resource bin.
                return true;
            }

            // v0 contract: non-KSC origins do not dispatch. Returning true
            // here would let a route dispatch without verifying the live
            // origin vessel has the manifest — a silent correctness bug.
            // Returning false with a stable reason holds non-KSC routes
            // in WaitingForResources; post-v0 work wires live origin-cargo
            // gating that replaces this stub.
            lackingResource = "non-ksc-origin-unsupported-in-v0";
            ParsekLog.VerboseRateLimited(Tag, "non-ksc-origin-stub",
                $"OriginHasCargo: non-KSC origin path is stubbed in v0; " +
                $"route {ShortIdForRoute(route)} held in WaitingForResources " +
                "until live origin-cargo gating ships");
            return false;
        }

        public bool KscFundsAvailable(Route route, out double shortfall)
        {
            shortfall = 0.0;
            if (route == null) return false;

            double cost = RouteOrchestrator.ComputeDispatchFundsCostForRoute(route);
            double funds = 0.0;
            try
            {
                funds = Funding.Instance != null ? Funding.Instance.Funds : 0.0;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"KscFundsAvailable: Funding.Instance probe threw {ex.GetType().Name}: {ex.Message}; treating funds=0");
                funds = 0.0;
            }

            if (funds >= cost)
                return true;

            shortfall = cost - funds;
            return false;
        }

        public bool DestinationHasCapacity(Route route, out string fullResource)
        {
            // v0 contract: eligibility gate returns true unconditionally —
            // capacity is enforced at apply time by RouteDeliveryPlanner +
            // LiveDeliveryCapacityProbe, which partial-fills each resource
            // and records the actual-vs-requested split in the
            // RouteCargoDelivered ledger row. The stub stays so dispatch
            // can always attempt; apply-time clamping is the real contract.
            //
            // Emit a per-route rate-limited breadcrumb so operators can
            // see the v0 split in KSP.log alongside the matching apply-time
            // partial-fill log — same shape as OriginHasCargo's stub above.
            fullResource = string.Empty;
            string routeId = route?.Id ?? "<none>";
            ParsekLog.VerboseRateLimited(Tag,
                "route-destcap-" + routeId,
                $"DestinationHasCapacity: v0 eligibility-gate stub returns " +
                $"true; capacity enforced at apply time; routeId={routeId}");
            return true;
        }

        public bool RouteHasValidSourcesInErs(Route route)
        {
            if (route == null || route.SourceRefs == null || route.SourceRefs.Count == 0)
                return false;

            EnsureErsBuilt();
            if (ersById == null) return false;

            for (int i = 0; i < route.SourceRefs.Count; i++)
            {
                var sref = route.SourceRefs[i];
                if (sref == null || string.IsNullOrEmpty(sref.RecordingId))
                    return false;
                if (!ersById.ContainsKey(sref.RecordingId))
                    return false;
            }
            return true;
        }

        private void EnsureErsBuilt()
        {
            if (ersBuilt) return;
            ersBuilt = true;
            var ers = RouteOrchestrator.SafeComputeErs();
            if (ers == null) return;
            ersById = new Dictionary<string, Recording>(StringComparer.Ordinal);
            for (int i = 0; i < ers.Count; i++)
            {
                var rec = ers[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                ersById[rec.RecordingId] = rec;
            }
        }

        // ---- Stock cost lookups used by RouteOrchestrator.ComputeDispatchFundsCostForRoute ----

        internal static float LookupPartCost(string partName)
        {
            if (string.IsNullOrEmpty(partName)) return 0f;
            try
            {
                AvailablePart ap = PartLoader.getPartInfoByName(partName);
                return ap != null ? ap.cost : 0f;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"LookupPartCost({partName}) threw {ex.GetType().Name}: {ex.Message}; returning 0");
                return 0f;
            }
        }

        internal static float LookupResourceUnitCost(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName)) return 0f;
            try
            {
                PartResourceDefinition def =
                    PartResourceLibrary.Instance != null
                        ? PartResourceLibrary.Instance.GetDefinition(resourceName)
                        : null;
                return def != null ? def.unitCost : 0f;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"LookupResourceUnitCost({resourceName}) threw {ex.GetType().Name}: {ex.Message}; returning 0");
                return 0f;
            }
        }

        private static string ShortIdForRoute(Route route)
        {
            if (route == null || string.IsNullOrEmpty(route.Id)) return "<no-id>";
            return route.Id.Length > 8 ? route.Id.Substring(0, 8) : route.Id;
        }
    }
}
