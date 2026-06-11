using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    internal enum RouteAnalysisStatus
    {
        Eligible = 0,
        MissingRouteProof = 1,
        MultipleConnectionWindows = 2,
        NoDeliveryManifest = 3,
        MixedPickupDelivery = 4,
        MissingEndpointProof = 5,
        /// <summary>
        /// M1 workflow rejection (design D7): the run's ORIGIN recording proves
        /// neither a KSC launch (no Kerbin launch site) nor a start-docked
        /// origin partner (no <see cref="RouteOriginProof"/> pid), so it started
        /// undocked with cargo already aboard and the cargo's source was never
        /// witnessed. Append-only value.
        /// </summary>
        UndockedStartOrigin = 6
    }

    /// <summary>
    /// Controls whether <see cref="RouteAnalysisEngine"/> emits its per-call
    /// INFO diagnostics. One-shot callers (commit-time / the Create Route dialog)
    /// use <see cref="Diagnostic"/>; the ~1/second candidate sweep
    /// (<see cref="RouteCandidateFinder.DeriveCandidates"/>) uses <see cref="Quiet"/>
    /// and logs a single rate-appropriate batch summary instead, so the per-tree
    /// rejection lines do not spam the log on every poll.
    /// </summary>
    internal enum RouteAnalysisLogMode
    {
        Diagnostic = 0,
        Quiet = 1
    }

    internal sealed class RouteAnalysisResult
    {
        public RouteAnalysisStatus Status;
        public Recording SourceRecording;
        public RouteConnectionWindow ConnectionWindow;
        public Dictionary<string, double> ResourceDeliveryManifest;
        public List<InventoryPayloadItem> InventoryDeliveryManifest;

        public bool IsEligible => Status == RouteAnalysisStatus.Eligible;
    }

    /// <summary>
    /// Proof verification for Supply Runs: which committed recording carries a
    /// complete dock-deliver-undock <see cref="RouteConnectionWindow"/>, and what
    /// its delivery manifest is. This pass verifies PROOF only — it does NOT scan
    /// trajectory geometry. The backing-mission render geometry (the
    /// <c>[launch .. undock]</c> interval selection + member-recording set) is
    /// owned by <see cref="RouteBackingMission"/>, derived from the window's
    /// <c>UndockUT</c> + the tree root launch. (design §0: "geometry no longer
    /// scanned bespoke; proof verification unchanged".)
    /// </summary>
    internal static class RouteAnalysisEngine
    {
        private const double ResourceEpsilon = 1e-9;

        internal static RouteAnalysisResult AnalyzeTree(
            RecordingTree tree,
            RouteAnalysisLogMode logMode = RouteAnalysisLogMode.Diagnostic)
        {
            if (tree?.Recordings == null || tree.Recordings.Count == 0)
                return MissingProof(logMode);

            HashSet<string> sourcePathIds = CollectSourcePathRecordingIds(tree);
            if (sourcePathIds == null || sourcePathIds.Count == 0)
                return MissingProof(logMode);

            Recording source = null;
            RouteConnectionWindow window = null;

            foreach (string recordingId in sourcePathIds)
            {
                if (!tree.Recordings.TryGetValue(recordingId, out Recording rec))
                    continue;
                if (rec?.RouteConnectionWindows == null)
                    continue;

                for (int i = 0; i < rec.RouteConnectionWindows.Count; i++)
                {
                    RouteConnectionWindow candidate = rec.RouteConnectionWindows[i];
                    if (candidate == null || !candidate.IsComplete)
                        continue;

                    if (window != null)
                    {
                        Diag(logMode,
                            $"RouteAnalysis rejected: multiple completed windows tree={tree.Id ?? "<none>"}");
                        return new RouteAnalysisResult
                        {
                            Status = RouteAnalysisStatus.MultipleConnectionWindows
                        };
                    }

                    source = rec;
                    window = candidate;
                }
            }

            if (window == null)
                return MissingProof(logMode);

            // Resolve the ORIGIN recording for the workflow gate: the tree ROOT
            // (the launch carries LaunchSiteName / StartBodyName / the
            // RouteOriginProof) when it resolves, else the analysis source (the
            // legacy single-recording case where the source IS the root). Same
            // walk as RouteCreationFormatters.ResolveOriginIdentity so the gate
            // and the display classification cannot diverge.
            Recording originRec = source;
            if (!string.IsNullOrEmpty(tree.RootRecordingId)
                && tree.Recordings.TryGetValue(tree.RootRecordingId, out Recording rootRec)
                && rootRec != null)
            {
                originRec = rootRec;
            }

            return AnalyzeWindow(source, window, originRec, logMode);
        }

        internal static RouteAnalysisResult AnalyzeRecording(
            Recording recording,
            RouteAnalysisLogMode logMode = RouteAnalysisLogMode.Diagnostic)
        {
            if (recording?.RouteConnectionWindows == null ||
                recording.RouteConnectionWindows.Count == 0)
                return MissingProof(logMode);

            RouteConnectionWindow window = null;
            for (int i = 0; i < recording.RouteConnectionWindows.Count; i++)
            {
                RouteConnectionWindow candidate = recording.RouteConnectionWindows[i];
                if (candidate == null || !candidate.IsComplete)
                    continue;

                if (window != null)
                    return new RouteAnalysisResult
                    {
                        Status = RouteAnalysisStatus.MultipleConnectionWindows
                    };

                window = candidate;
            }

            if (window == null)
                return MissingProof(logMode);

            // Single-recording analysis: the recording IS the origin recording.
            return AnalyzeWindow(recording, window, recording, logMode);
        }

        private static RouteAnalysisResult AnalyzeWindow(
            Recording source,
            RouteConnectionWindow window,
            Recording originRec,
            RouteAnalysisLogMode logMode)
        {
            if (!HasEndpointProof(window))
            {
                Diag(logMode,
                    $"RouteAnalysis rejected: missing endpoint proof source={source?.RecordingId ?? "<none>"} " +
                    $"window={window.WindowId ?? "<none>"} targetPid={window.TransferTargetVesselPid} " +
                    $"kind={window.TransferKind} situation={window.TransferEndpointSituation} " +
                    $"endpointAtDock={(window.EndpointAtDock.HasValue ? "yes" : "no")}");
                return new RouteAnalysisResult
                {
                    Status = RouteAnalysisStatus.MissingEndpointProof,
                    SourceRecording = source,
                    ConnectionWindow = window
                };
            }

            // M1 workflow gate (design D7): an undocked-start run carries cargo
            // whose source was never witnessed, so it can never dispatch.
            // Ordering: after the endpoint-proof check, before the
            // manifest-level complaints (the workflow error outranks them).
            // RouteBuilder's endpoint-missing reject stays as the defensive
            // backstop at create time.
            if (IsUndockedStartOrigin(originRec))
            {
                Diag(logMode,
                    $"RouteAnalysis rejected: undocked-start origin originRec={originRec?.RecordingId ?? "<none>"} " +
                    $"launchSite={(string.IsNullOrEmpty(originRec?.LaunchSiteName) ? "<none>" : originRec.LaunchSiteName)} " +
                    $"startBody={(string.IsNullOrEmpty(originRec?.StartBodyName) ? "<none>" : originRec.StartBodyName)} " +
                    $"originProof={(HasDockedOriginProof(originRec) ? "yes" : "no")}");
                return new RouteAnalysisResult
                {
                    Status = RouteAnalysisStatus.UndockedStartOrigin,
                    SourceRecording = source,
                    ConnectionWindow = window
                };
            }

            if (HasMixedPickupDelivery(window, out string pickupReason))
            {
                Diag(logMode,
                    $"RouteAnalysis rejected: mixed pickup/delivery source={source?.RecordingId ?? "<none>"} " +
                    $"window={window.WindowId ?? "<none>"} {pickupReason}");
                return new RouteAnalysisResult
                {
                    Status = RouteAnalysisStatus.MixedPickupDelivery,
                    SourceRecording = source,
                    ConnectionWindow = window
                };
            }

            Dictionary<string, double> resources = BuildResourceDeliveryManifest(window);
            List<InventoryPayloadItem> inventory = BuildInventoryDeliveryManifest(window);

            if ((resources == null || resources.Count == 0) &&
                (inventory == null || inventory.Count == 0))
            {
                Diag(logMode,
                    $"RouteAnalysis rejected: no delivery manifest source={source?.RecordingId ?? "<none>"} " +
                    $"window={window.WindowId ?? "<none>"}");
                return new RouteAnalysisResult
                {
                    Status = RouteAnalysisStatus.NoDeliveryManifest,
                    SourceRecording = source,
                    ConnectionWindow = window
                };
            }

            // Backing-mission render geometry (RouteBackingMission) keys its
            // [launch..undock] trim on window.UndockUT. A non-finite UndockUT would
            // make the window unrenderable downstream (RouteBuilder rejects it with
            // backing-mission-unresolvable), so surface it at analysis time as a
            // diagnostic — eligibility itself is unchanged. Gated with the other
            // per-call diagnostics so the poll sweep does not re-warn every second;
            // the one-shot Create Route path (Diagnostic) still surfaces it, and
            // RouteBuilder independently rejects the build.
            if (logMode == RouteAnalysisLogMode.Diagnostic &&
                (double.IsNaN(window.UndockUT) || double.IsInfinity(window.UndockUT)))
                ParsekLog.Warn("Route",
                    $"RouteAnalysis: eligible window carries non-finite UndockUT source={source?.RecordingId ?? "<none>"} " +
                    $"window={window.WindowId ?? "<none>"} undockUT={window.UndockUT.ToString("R", CultureInfo.InvariantCulture)} " +
                    "(RouteBackingMission cannot derive the [launch..undock] trim; RouteBuilder will reject)");

            Diag(logMode,
                $"RouteAnalysis eligible: source={source?.RecordingId ?? "<none>"} " +
                $"window={window.WindowId ?? "<none>"} resources={resources?.Count ?? 0} " +
                $"inventory={inventory?.Count ?? 0}");

            return new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.Eligible,
                SourceRecording = source,
                ConnectionWindow = window,
                ResourceDeliveryManifest = resources,
                InventoryDeliveryManifest = inventory
            };
        }

        private static RouteAnalysisResult MissingProof(RouteAnalysisLogMode logMode)
        {
            Diag(logMode, "RouteAnalysis rejected: missing route proof");
            return new RouteAnalysisResult { Status = RouteAnalysisStatus.MissingRouteProof };
        }

        // Per-call INFO diagnostic, emitted only in Diagnostic mode. In Quiet mode
        // (the ~1/second candidate sweep) the per-tree lines are suppressed; the
        // sweep's single batch summary (RouteCandidateFinder) carries the aggregate.
        private static void Diag(RouteAnalysisLogMode logMode, string message)
        {
            if (logMode == RouteAnalysisLogMode.Diagnostic)
                ParsekLog.Info("Route", message);
        }

        /// <summary>
        /// True when the origin recording proves a KSC origin: launched from a
        /// named Kerbin launch site. Mirrors <c>RouteBuilder.BuildRoute</c>'s
        /// KSC branch and is shared with
        /// <see cref="RouteCreationFormatters.ResolveOriginIdentity"/> so the
        /// analysis gate and the display classification cannot diverge.
        /// </summary>
        internal static bool IsKscOriginRecording(Recording originRec)
        {
            return originRec != null
                && !string.IsNullOrEmpty(originRec.LaunchSiteName)
                && string.Equals(originRec.StartBodyName, "Kerbin", StringComparison.Ordinal);
        }

        /// <summary>
        /// True when the origin recording carries a captured start-docked origin
        /// partner proof (<see cref="Recording.RouteOriginProof"/> with a
        /// non-zero partner pid). Shared with
        /// <see cref="RouteCreationFormatters.ResolveOriginIdentity"/>.
        /// </summary>
        internal static bool HasDockedOriginProof(Recording originRec)
        {
            return originRec?.RouteOriginProof != null
                && originRec.RouteOriginProof.StartDockedOriginVesselPid != 0;
        }

        /// <summary>
        /// M1 workflow gate (design D7): true when the origin recording proves
        /// NEITHER a KSC launch NOR a start-docked origin partner, i.e. the run
        /// started undocked with cargo already aboard so the cargo's source was
        /// never witnessed. A null recording counts as undocked (no proof can be
        /// verified).
        /// </summary>
        internal static bool IsUndockedStartOrigin(Recording originRec)
        {
            return !IsKscOriginRecording(originRec) && !HasDockedOriginProof(originRec);
        }

        private static bool HasEndpointProof(RouteConnectionWindow window)
        {
            return window != null
                && window.TransferTargetVesselPid != 0
                && window.TransferKind != RouteConnectionKind.None
                && window.EndpointAtDock.HasValue
                && window.TransferEndpointSituation >= 0;
        }

        private static bool HasMixedPickupDelivery(RouteConnectionWindow window, out string reason)
        {
            // || short-circuits, but both helpers assign reason unconditionally,
            // so reason is definitely assigned on every path: the resource
            // culprit when the left side trips, the inventory culprit when the
            // right side trips, and null when neither does.
            return HasResourcePickup(window, out reason)
                || HasInventoryPickup(window, out reason);
        }

        // ElectricCharge and IntakeAir are environmental noise, never meaningful
        // supply cargo: a docked transport recharges its batteries from the depot
        // and its IntakeAir reading drifts between the dock and undock snapshots,
        // so either can show a spurious per-resource delta on an otherwise clean
        // delivery-only run. Design section 5.3 rule 7 ("after EC/IntakeAir
        // filtering") and section 6 ("EC-only delivery ... remains excluded")
        // filter them out of BOTH the pickup gate and the delivery manifest,
        // matching VesselSpawner.ExtractResourceManifest. These two are the only
        // always-ignored resources the design names.
        private static bool IsIgnoredResource(string name)
        {
            return name == "ElectricCharge" || name == "IntakeAir";
        }

        private static bool HasResourcePickup(RouteConnectionWindow window, out string reason)
        {
            reason = null;

            var keys = new Dictionary<string, double>();
            AddResourceDeliveryKeys(keys, window.DockEndpointResources);
            AddResourceDeliveryKeys(keys, window.UndockEndpointResources);
            AddResourceDeliveryKeys(keys, window.DockTransportResources);
            AddResourceDeliveryKeys(keys, window.UndockTransportResources);

            foreach (string name in keys.Keys)
            {
                if (IsIgnoredResource(name))
                    continue;

                double endpointLoss =
                    GetResourceAmount(window.DockEndpointResources, name) -
                    GetResourceAmount(window.UndockEndpointResources, name);
                double transportGain =
                    GetResourceAmount(window.UndockTransportResources, name) -
                    GetResourceAmount(window.DockTransportResources, name);

                if (endpointLoss > ResourceEpsilon || transportGain > ResourceEpsilon)
                {
                    reason =
                        $"pickup resource={name} " +
                        $"endpointLoss={endpointLoss.ToString("R", CultureInfo.InvariantCulture)} " +
                        $"transportGain={transportGain.ToString("R", CultureInfo.InvariantCulture)}";
                    return true;
                }
            }

            return false;
        }

        private static bool HasInventoryPickup(RouteConnectionWindow window, out string reason)
        {
            reason = null;

            Dictionary<string, InventoryPayloadItem> identities =
                BuildInventoryMap(window.DockEndpointInventory);
            AddInventoryKeys(identities, window.UndockEndpointInventory);
            AddInventoryKeys(identities, window.DockTransportInventory);
            AddInventoryKeys(identities, window.UndockTransportInventory);

            foreach (string identity in identities.Keys)
            {
                int endpointLoss =
                    GetInventoryQuantity(window.DockEndpointInventory, identity) -
                    GetInventoryQuantity(window.UndockEndpointInventory, identity);
                int transportGain =
                    GetInventoryQuantity(window.UndockTransportInventory, identity) -
                    GetInventoryQuantity(window.DockTransportInventory, identity);

                if (endpointLoss > 0 || transportGain > 0)
                {
                    reason =
                        $"pickup inventory={identity} " +
                        $"endpointLoss={endpointLoss.ToString(CultureInfo.InvariantCulture)} " +
                        $"transportGain={transportGain.ToString(CultureInfo.InvariantCulture)}";
                    return true;
                }
            }

            return false;
        }

        private static Dictionary<string, double> BuildResourceDeliveryManifest(
            RouteConnectionWindow window)
        {
            var delivery = new Dictionary<string, double>();
            AddResourceDeliveryKeys(delivery, window.DockEndpointResources);
            AddResourceDeliveryKeys(delivery, window.UndockEndpointResources);
            AddResourceDeliveryKeys(delivery, window.DockTransportResources);
            AddResourceDeliveryKeys(delivery, window.UndockTransportResources);

            var names = new List<string>(delivery.Keys);
            delivery.Clear();

            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];

                // EC/IntakeAir are environmental noise (see IsIgnoredResource):
                // never list them as delivered cargo, so an EC-only "delivery"
                // (transport charges the depot's batteries) yields an empty
                // manifest and the candidate is rejected as no-delivery rather
                // than treated as an EC supply run (design section 6).
                if (IsIgnoredResource(name))
                    continue;

                double endpointGain =
                    GetResourceAmount(window.UndockEndpointResources, name) -
                    GetResourceAmount(window.DockEndpointResources, name);
                double transportLoss =
                    GetResourceAmount(window.DockTransportResources, name) -
                    GetResourceAmount(window.UndockTransportResources, name);

                if (endpointGain <= ResourceEpsilon || transportLoss <= ResourceEpsilon)
                    continue;

                delivery[name] = Math.Min(endpointGain, transportLoss);
            }

            return delivery.Count > 0 ? delivery : null;
        }

        private static void AddResourceDeliveryKeys(
            Dictionary<string, double> keys,
            Dictionary<string, ResourceAmount> manifest)
        {
            if (manifest == null)
                return;

            foreach (string name in manifest.Keys)
            {
                if (!string.IsNullOrEmpty(name) && !keys.ContainsKey(name))
                    keys[name] = 0.0;
            }
        }

        private static double GetResourceAmount(
            Dictionary<string, ResourceAmount> manifest,
            string name)
        {
            return manifest != null && manifest.TryGetValue(name, out ResourceAmount amount)
                ? amount.amount
                : 0.0;
        }

        private static List<InventoryPayloadItem> BuildInventoryDeliveryManifest(
            RouteConnectionWindow window)
        {
            Dictionary<string, InventoryPayloadItem> deliveredByIdentity =
                BuildInventoryMap(window.UndockEndpointInventory);
            AddInventoryKeys(deliveredByIdentity, window.DockEndpointInventory);
            AddInventoryKeys(deliveredByIdentity, window.DockTransportInventory);
            AddInventoryKeys(deliveredByIdentity, window.UndockTransportInventory);

            if (deliveredByIdentity.Count == 0)
                return null;

            var identities = new List<string>(deliveredByIdentity.Keys);
            var delivery = new List<InventoryPayloadItem>();
            for (int i = 0; i < identities.Count; i++)
            {
                string identity = identities[i];
                int endpointGain =
                    GetInventoryQuantity(window.UndockEndpointInventory, identity) -
                    GetInventoryQuantity(window.DockEndpointInventory, identity);
                int transportLoss =
                    GetInventoryQuantity(window.DockTransportInventory, identity) -
                    GetInventoryQuantity(window.UndockTransportInventory, identity);

                int delivered = Math.Min(endpointGain, transportLoss);
                if (delivered <= 0)
                    continue;

                int endpointSlotsGain =
                    GetInventorySlots(window.UndockEndpointInventory, identity) -
                    GetInventorySlots(window.DockEndpointInventory, identity);

                InventoryPayloadItem source = deliveredByIdentity[identity];
                InventoryPayloadItem item = source.DeepClone();
                item.Quantity = delivered;
                item.SlotsTaken = Math.Max(0, endpointSlotsGain);
                SetStoredPartQuantity(item.StoredPartSnapshot, delivered);
                delivery.Add(item);
            }

            delivery.Sort((a, b) => string.Compare(a.IdentityHash, b.IdentityHash, StringComparison.Ordinal));
            return delivery.Count > 0 ? delivery : null;
        }

        private static Dictionary<string, InventoryPayloadItem> BuildInventoryMap(
            List<InventoryPayloadItem> items)
        {
            var map = new Dictionary<string, InventoryPayloadItem>();
            AddInventoryKeys(map, items);
            return map;
        }

        private static void AddInventoryKeys(
            Dictionary<string, InventoryPayloadItem> map,
            List<InventoryPayloadItem> items)
        {
            if (map == null || items == null)
                return;

            for (int i = 0; i < items.Count; i++)
            {
                InventoryPayloadItem item = items[i];
                if (item == null || string.IsNullOrEmpty(item.IdentityHash))
                    continue;

                if (!map.ContainsKey(item.IdentityHash))
                    map[item.IdentityHash] = item;
            }
        }

        private static int GetInventoryQuantity(
            List<InventoryPayloadItem> items,
            string identity)
        {
            if (items == null || string.IsNullOrEmpty(identity))
                return 0;

            int total = 0;
            for (int i = 0; i < items.Count; i++)
            {
                InventoryPayloadItem item = items[i];
                if (item != null && item.IdentityHash == identity)
                    total += item.Quantity;
            }
            return total;
        }

        private static int GetInventorySlots(
            List<InventoryPayloadItem> items,
            string identity)
        {
            if (items == null || string.IsNullOrEmpty(identity))
                return 0;

            int total = 0;
            for (int i = 0; i < items.Count; i++)
            {
                InventoryPayloadItem item = items[i];
                if (item != null && item.IdentityHash == identity)
                    total += item.SlotsTaken;
            }
            return total;
        }

        private static void SetStoredPartQuantity(ConfigNode storedPart, int quantity)
        {
            if (storedPart == null)
                return;

            storedPart.SetValue(
                "quantity",
                quantity.ToString(CultureInfo.InvariantCulture),
                true);
        }

        internal static HashSet<string> CollectSourcePathRecordingIds(RecordingTree tree)
        {
            if (tree?.Recordings == null || tree.Recordings.Count == 0)
                return null;

            // When ActiveRecordingId is empty — typical after the player
            // switches vessels before committing, which nulls
            // activeTree.ActiveRecordingId in ParsekFlight.OnVesselSwitchComplete
            // (line 3029, transitioning the old recorder to background) — the
            // leaf-to-root walk from RootRecordingId finds only the root
            // itself, so a route window on a non-root branch (e.g. a
            // dock-merged child) is invisible. Fall back to every recording in
            // the tree: for v0 single-route eligibility we just need to know
            // whether ANY recording carries a complete RouteConnectionWindow,
            // and committed trees never carry orphaned debris that would
            // misclassify.
            if (string.IsNullOrEmpty(tree.ActiveRecordingId))
            {
                var all = new HashSet<string>();
                foreach (string id in tree.Recordings.Keys)
                {
                    if (!string.IsNullOrEmpty(id))
                        all.Add(id);
                }
                return all.Count > 0 ? all : null;
            }

            string leafId = tree.ActiveRecordingId;

            var branchPointsById = new Dictionary<string, BranchPoint>();
            if (tree.BranchPoints != null)
            {
                for (int i = 0; i < tree.BranchPoints.Count; i++)
                {
                    BranchPoint bp = tree.BranchPoints[i];
                    if (bp != null && !string.IsNullOrEmpty(bp.Id))
                        branchPointsById[bp.Id] = bp;
                }
            }

            var path = new HashSet<string>();
            var pending = new Stack<string>();
            pending.Push(leafId);

            while (pending.Count > 0)
            {
                string recId = pending.Pop();
                if (string.IsNullOrEmpty(recId) || !path.Add(recId))
                    continue;

                if (!tree.Recordings.TryGetValue(recId, out Recording rec))
                    continue;

                if (string.IsNullOrEmpty(rec.ParentBranchPointId))
                    continue;

                if (!branchPointsById.TryGetValue(rec.ParentBranchPointId, out BranchPoint bp) ||
                    bp.ParentRecordingIds == null)
                {
                    continue;
                }

                for (int i = 0; i < bp.ParentRecordingIds.Count; i++)
                    pending.Push(bp.ParentRecordingIds[i]);
            }

            return path;
        }
    }
}
