using System;
using System.Collections.Generic;
using System.Globalization;

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
    /// Eligibility-gate contract (intentional, not a TODO):
    /// <list type="bullet">
    ///   <item><c>OriginHasCargo</c> (M1): KSC origins return <c>true</c>
    ///     unconditionally (funds are gated separately via
    ///     <see cref="KscFundsAvailable"/>). Non-KSC origins resolve the live
    ///     origin vessel and gate ALL-OR-NOTHING against the route's recorded
    ///     <c>CostManifest</c> via <see cref="LiveOriginCargoProbe"/> +
    ///     <see cref="RouteOriginCargoCheck"/> (design D1/D3/D4); a short
    ///     origin holds the route in <c>WaitingForResources</c> naming the
    ///     first short resource. Inventory payloads are ALSO gated as of M3
    ///     Phase 5 (design D7 carve-out lift): a non-KSC route with a non-empty
    ///     <c>InventoryCostManifest</c> gates all-or-nothing by exact
    ///     <c>IdentityHash</c> via <see cref="RouteOriginCargoCheck.HasRequiredInventory"/>
    ///     + <see cref="LiveInventoryPickupWriter.CountStored"/>, holding with an
    ///     <c>inventory:&lt;hash&gt;</c> short token (the retired
    ///     <c>inventory-origin-debit-unsupported</c> deferral is gone - the origin
    ///     debit now physically removes the stored part).</item>
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
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

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

            if (route.IsHarvestOrigin)
            {
                // Harvest origin (M2, plan D7): the cargo was harvested en
                // route, so there is no physical source vessel to gate
                // against (the CostManifest is empty by construction).
                ParsekLog.VerboseRateLimited(Tag, "origin-harvest-" + route.Id,
                    $"OriginHasCargo: route {ShortIdForRoute(route)} harvest origin - " +
                    "no physical source to gate");
                return true;
            }

            // M1 un-stub: resolve the live origin vessel and gate
            // all-or-nothing against the recorded CostManifest (design
            // D1/D3). The evaluator's step-5 endpoint check normally catches
            // a resolution miss first; this is the defensive re-resolve at
            // the cargo gate. A true-with-null-vessel resolution counts as a
            // miss (the probe would have nothing to read).
            bool originResolved = RouteEndpointResolver.TryResolveEndpoint(
                route.Origin, out Vessel originVessel, out string resolveReason);
            if (!originResolved || originVessel == null)
            {
                // Same labeling as ApplyOriginDebit: resolved-true-with-null-
                // vessel is "resolved-null-vessel" regardless of any reason
                // text, so the gate and debit sites log identically.
                string reason = originResolved
                    ? "resolved-null-vessel"
                    : (string.IsNullOrEmpty(resolveReason) ? "unknown" : resolveReason);
                lackingResource = "origin-unresolved:" + reason;
                ParsekLog.VerboseRateLimited(Tag, "origin-unresolved-" + route.Id,
                    $"OriginHasCargo: route {ShortIdForRoute(route)} origin unresolved (reason={reason})");
                return false;
            }

            // Capture the loaded gate ONCE and thread it into the probe so a
            // mid-tick packed-state flip cannot split the read across branches
            // (same contract as the delivery side's destinationIsLoaded).
            bool originIsLoaded = originVessel.loaded && !originVessel.packed;
            var probe = new LiveOriginCargoProbe(originVessel, originIsLoaded);
            bool covered = RouteOriginCargoCheck.HasRequired(
                route.CostManifest, probe.ProbeResourceStored,
                out string shortResource, out double shortfall);
            if (!covered)
            {
                lackingResource = shortResource;
                double need = 0.0;
                if (route.CostManifest != null)
                    route.CostManifest.TryGetValue(shortResource, out need);
                double have = need - shortfall;
                ParsekLog.VerboseRateLimited(Tag, "origin-short-" + route.Id,
                    $"OriginHasCargo: route {ShortIdForRoute(route)} " +
                    $"origin={originVessel.vesselName ?? "<none>"} " +
                    $"pid={originVessel.persistentId.ToString(IC)} " +
                    $"short resource={shortResource} " +
                    $"have={have.ToString("R", IC)} " +
                    $"need={need.ToString("R", IC)} " +
                    $"path={(originIsLoaded ? "loaded" : "unloaded")}");
                return false;
            }

            // M3 Phase 5 (design D7 carve-out lift): the M1
            // inventory-origin-debit-unsupported HOLD is REMOVED - the
            // LiveInventoryPickupWriter now physically removes STOREDPART
            // payloads by identity from the origin-dispatch path (ApplyOriginDebit
            // inventory half), so an origin inventory cost is debitable, not
            // deferred. Gate it all-or-nothing against the recorded
            // InventoryCostManifest by exact IdentityHash, reusing the SAME
            // per-vessel loaded-gate capture as the resource gate so the count and
            // the eventual removal read the same loaded/unloaded branch. A
            // null/empty InventoryCostManifest (the common case, including every
            // pure-pickup run whose origin cost is empty by construction) passes
            // trivially.
            if (route.InventoryCostManifest != null && route.InventoryCostManifest.Count > 0)
            {
                var inventoryWriter = new LiveInventoryPickupWriter(originVessel, originIsLoaded);
                bool inventoryCovered = RouteOriginCargoCheck.HasRequiredInventory(
                    route.InventoryCostManifest, inventoryWriter.CountStored,
                    out string shortIdentity, out int shortInventory);
                if (!inventoryCovered)
                {
                    lackingResource = "inventory:" + shortIdentity;
                    ParsekLog.VerboseRateLimited(Tag, "origin-inventory-short-" + route.Id,
                        $"OriginHasCargo: route {ShortIdForRoute(route)} " +
                        $"origin={originVessel.vesselName ?? "<none>"} " +
                        $"pid={originVessel.persistentId.ToString(IC)} " +
                        $"short inventory identity={shortIdentity} " +
                        $"shortBy={shortInventory.ToString(IC)} " +
                        $"path={(originIsLoaded ? "loaded" : "unloaded")}");
                    return false;
                }
            }
            return true;
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
