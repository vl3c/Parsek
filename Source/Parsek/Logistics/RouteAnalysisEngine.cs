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
        UndockedStartOrigin = 6,
        /// <summary>
        /// M2 gain-side flow closure (plan D6): the transport GAINED a
        /// resource between the run start and the dock with no witnessed
        /// source (no harvest window covers it). Only emitted when the
        /// transport lineage carries complete run manifests (the presence
        /// gate); legacy recordings can never produce it. The reject detail
        /// names the resource and the gained/harvested quantities
        /// (<see cref="RouteAnalysisResult.RejectDetail"/>). Append-only value.
        /// </summary>
        UntrackedCargoGain = 7,
        /// <summary>
        /// M3 flow-closure rejection (plan D3): the full-run cargo balance does
        /// NOT close - the transport ended this run with MORE of a resource than
        /// ever arrived (launched + loaded + harvested - delivered &lt; residual),
        /// i.e. over-delivery / phantom cargo. A POSITIVE balancing slack
        /// (legitimate consumption / burn) never produces this. Only emitted on
        /// the harvest-data path (the closure is presence-gated on complete run
        /// manifests, OQ2); the window-direction classification still admits the
        /// pickup window-locally even when this validation is skipped. The
        /// reject detail names the resource and the unaccounted quantity
        /// (<see cref="RouteAnalysisResult.RejectDetail"/>, mirrors
        /// <see cref="UntrackedCargoGain"/>). Append-only value.
        /// </summary>
        FlowDoesNotClose = 8
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

        /// <summary>
        /// M3 pickup-direction load manifest (plan D2/D8): per routable resource
        /// name, <c>loaded = min(endpointLoss, transportGain)</c> over the
        /// connection window - the exact sign-flip mirror of
        /// <see cref="ResourceDeliveryManifest"/>. Cargo that flowed FROM the
        /// endpoint ONTO the transport across the window. Excludes
        /// undefined / non-routable names exactly as the delivery manifest does
        /// (admission direction). Null when the window carries no resource
        /// pickup; parallel to the delivery manifest, set on an Eligible window
        /// that is pure-pickup or mixed.
        /// </summary>
        public Dictionary<string, double> ResourceLoadManifest;

        /// <summary>
        /// M3 inventory pickup load manifest (plan D7/D8, Phase 5): per exact
        /// <see cref="InventoryPayloadItem.IdentityHash"/>, the sign-flip mirror
        /// of <see cref="InventoryDeliveryManifest"/> -
        /// <c>loaded = min(endpointLoss, transportGain)</c> stored-part copies
        /// that flowed FROM the endpoint ONTO the transport across the window,
        /// identity carried intact. Inventory closure is WINDOW-LOCAL (OQ3): an
        /// unwitnessed transport inventory gain (gained on the transport with no
        /// matching endpoint loss) fails closed inside
        /// <see cref="HasUnwitnessedInventoryGain"/> and the window rejects
        /// <see cref="RouteAnalysisStatus.MixedPickupDelivery"/> rather than
        /// admitting a phantom load term. Null when the window carries no
        /// inventory pickup; parallel to the resource load manifest, set on an
        /// Eligible window that loads inventory (pure-pickup or mixed).
        /// </summary>
        public List<InventoryPayloadItem> InventoryLoadManifest;

        /// <summary>
        /// Optional reject quantifier (M2, plan finding 12), e.g.
        /// <c>"Ore: 120.0 gained, 100.0 harvested"</c> for
        /// <see cref="RouteAnalysisStatus.UntrackedCargoGain"/>. Threaded
        /// through <see cref="RouteNearMiss.RejectDetail"/> and
        /// <c>RouteCreationFormatters.FormatRejectMessage(status, detail)</c>
        /// so the near-miss list shows the unaccounted amount. Null for every
        /// status that carries no quantity.
        /// </summary>
        public string RejectDetail;

        /// <summary>
        /// Witnessed harvested totals per resource over the checked span
        /// (windows + bridged boundary deltas), populated only when the M2
        /// gain check ENGAGED (complete run manifests on the whole transport
        /// lineage). Null on the legacy path. Feeds the D8 CostManifest
        /// reduction in <c>RouteBuilder</c>.
        /// </summary>
        public Dictionary<string, double> HarvestedManifest;

        /// <summary>
        /// True when the run started undocked (no KSC launch, no start-docked
        /// proof) but EVERY delivered resource is fully covered by witnessed
        /// harvest (plan D6 refined gate): the run is Eligible as a
        /// HARVEST-ORIGIN route (D7) - the environment, not a depot, supplied
        /// the cargo.
        /// </summary>
        public bool IsHarvestOrigin;

        /// <summary>
        /// Earliest in-span harvest window (by StartUT) on the transport
        /// lineage; its open-time location is the D7 harvest-origin display
        /// endpoint. Null when the gain check did not engage or no window
        /// fell inside the checked span.
        /// </summary>
        public RouteHarvestWindow FirstHarvestWindow;

        public bool IsEligible => Status == RouteAnalysisStatus.Eligible;
    }

    /// <summary>
    /// Result of <see cref="RouteAnalysisEngine.ComputeFlowClosure"/> (M3, plan
    /// D3). The full-run cargo balance per resource:
    /// <c>consumed := launched + Sum(loaded) + Sum(harvested) - Sum(delivered) - residual</c>.
    /// <see cref="Closes"/> is false ONLY when some resource's pre-clamp slack
    /// (<c>consumed</c>) is &lt; -<see cref="RouteHarvestAnalysis.GainEpsilon"/>
    /// (over-delivery / phantom cargo: the transport ended with more than ever
    /// arrived). A POSITIVE slack is legitimate consumption and closes. On a
    /// non-closing result the offending fields name the FIRST offending
    /// resource (ordinal order, deterministic) and the formatted detail string.
    /// </summary>
    internal sealed class FlowClosureResult
    {
        public bool Closes;
        public string OffendingResource;
        /// <summary>The unaccounted (over-delivered) quantity, a positive magnitude.</summary>
        public double UnaccountedQuantity;
        /// <summary>Formatted reject detail, e.g. "Ore: 30.0 over-delivered".</summary>
        public string RejectDetail;

        internal static FlowClosureResult Closed()
        {
            return new FlowClosureResult { Closes = true };
        }
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

            return AnalyzeWindow(source, window, originRec, tree, logMode);
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
            // No tree: the M2 gain check engages only when the recording is its
            // own complete lineage (no parent links), else it degrades to the
            // legacy path.
            return AnalyzeWindow(recording, window, recording, null, logMode);
        }

        private static RouteAnalysisResult AnalyzeWindow(
            Recording source,
            RouteConnectionWindow window,
            Recording originRec,
            RecordingTree tree,
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

            // M3 (plan D2/D4 item 1/6): build BOTH transfer directions up front.
            // The delivery manifest (assume-start-loaded, admission direction)
            // and the load manifest (sign-flip mirror) are computed here so the
            // load term can feed the harvest gain check (D3 composition: a pickup
            // window legitimately raises dock transport above anchor start) and
            // the gate ORDER below (D4) classifies off the built manifests.
            // Building early does not change the REJECT order - the gates that
            // consume them still fire in the D4 sequence.
            Dictionary<string, double> resources =
                BuildResourceDeliveryManifest(window, source?.RecordingId, logMode);
            List<InventoryPayloadItem> inventory = BuildInventoryDeliveryManifest(window);
            Dictionary<string, double> loadResources =
                BuildResourceLoadManifest(window, source?.RecordingId, logMode);
            List<InventoryPayloadItem> loadInventory = BuildInventoryLoadManifest(window);

            // M2 gain-side flow closure (plan D6): resolve the transport
            // lineage and run the gain check. ENGAGED only when every lineage
            // leg carries a COMPLETE run manifest; otherwise the result is
            // LegacyFallback (logged once per analysis inside) and the
            // pre-M2 path below runs byte-identically. The M3 load term is
            // threaded in as a witnessed source (D3 item 7) so a pickup window
            // does not false-reject as UntrackedCargoGain.
            HarvestGainCheckResult gainCheck =
                RouteHarvestAnalysis.CheckTransportGains(
                    tree, source, window, logMode, loadResources);
            bool harvestEngaged = gainCheck.Outcome != HarvestGainOutcome.LegacyFallback;

            // M1 workflow gate (design D7): an undocked-start run carries cargo
            // whose source was never witnessed, so it can never dispatch.
            // Ordering (legacy path): after the endpoint-proof check, before
            // the manifest-level complaints (the workflow error outranks them).
            // On the harvest-data path the verdict is DEFERRED (plan finding
            // 11, two-phase gate): the refined gate below needs harvested
            // totals AND the delivery manifest, so it runs after the gain
            // check. RouteBuilder's endpoint-missing reject stays as the
            // defensive backstop at create time.
            if (!harvestEngaged && IsUndockedStartOrigin(originRec))
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

            // M3 direction classification (plan D2/D7): cargo flow in EITHER
            // direction - resource AND inventory - is now admissible. A
            // pure-delivery, pure-pickup, or mixed window classifies and admits.
            // Inventory pickup (Phase 5) is the sign-flip mirror of the inventory
            // delivery builder: loadInventory (above) carries the witnessed
            // endpoint->transport stored-part moves per IdentityHash. Inventory
            // closure is WINDOW-LOCAL (OQ3): inventory is non-fungible and has no
            // harvested provenance, so a transport inventory GAIN with NO matching
            // endpoint LOSS is unwitnessed and MUST fail closed. The former
            // MixedPickupDelivery reject is now reachable ONLY for that
            // unwitnessed-gain case - a clean inventory pickup (gain matched by an
            // endpoint loss) classifies into loadInventory and admits.
            if (HasUnwitnessedInventoryGain(window, out string inventoryPickupReason))
            {
                Diag(logMode,
                    $"RouteAnalysis rejected: unwitnessed inventory gain " +
                    $"source={source?.RecordingId ?? "<none>"} " +
                    $"window={window.WindowId ?? "<none>"} {inventoryPickupReason}");
                return new RouteAnalysisResult
                {
                    Status = RouteAnalysisStatus.MixedPickupDelivery,
                    SourceRecording = source,
                    ConnectionWindow = window
                };
            }

            // Gate fix (a) (plan D4): a pure-pickup window has NO delivery
            // manifest. Pre-M3 this rejected here (NoDeliveryManifest) before
            // reaching Eligible. Widen the gate to "no delivery AND no load":
            // only reject when NO cargo flowed in EITHER direction. A window
            // carrying ONLY a resource load (pure pickup) now passes this gate.
            bool hasDelivery =
                (resources != null && resources.Count > 0) ||
                (inventory != null && inventory.Count > 0);
            bool hasInventoryLoad = loadInventory != null && loadInventory.Count > 0;
            bool hasLoad =
                (loadResources != null && loadResources.Count > 0) || hasInventoryLoad;
            if (!hasDelivery && !hasLoad)
            {
                Diag(logMode,
                    $"RouteAnalysis rejected: no delivery or load manifest source={source?.RecordingId ?? "<none>"} " +
                    $"window={window.WindowId ?? "<none>"}");
                return new RouteAnalysisResult
                {
                    Status = RouteAnalysisStatus.NoDeliveryManifest,
                    SourceRecording = source,
                    ConnectionWindow = window
                };
            }

            // M2 gain verdict (plan D6): a positive transport gain with no
            // witnessed harvest rejects with the exact unaccounted quantity
            // named. Runs after the manifest build (the gain check needs the
            // same window data) and only on the harvest-data path.
            if (harvestEngaged && gainCheck.Outcome == HarvestGainOutcome.UntrackedGain)
            {
                Diag(logMode,
                    $"RouteAnalysis rejected: untracked cargo gain resource={gainCheck.RejectResource} " +
                    $"gained={gainCheck.RejectGained.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"harvested={gainCheck.RejectHarvested.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"source={source?.RecordingId ?? "<none>"}");
                return new RouteAnalysisResult
                {
                    Status = RouteAnalysisStatus.UntrackedCargoGain,
                    SourceRecording = source,
                    ConnectionWindow = window,
                    RejectDetail = gainCheck.RejectDetail,
                    HarvestedManifest = gainCheck.HarvestedManifest,
                    FirstHarvestWindow = gainCheck.FirstHarvestWindow
                };
            }

            // M3 flow closure (plan D3, gate order D4: after the harvest
            // untracked-gain check, before the refined undocked-start gate).
            // Presence-gated per OQ2: the full-run balance needs the anchor's
            // launched (StartTransportResources) + residual (EndTransportResources)
            // terms, available only on the harvest-data path with a resolved
            // anchor. The window-direction classification above already admitted
            // the pickup window-locally (the self-contained min basis), so a
            // skipped closure NEVER drops the pickup. Closure REJECTS only on
            // over-delivery (pre-clamp slack < -GainEpsilon): the transport
            // ended with more of a resource than ever arrived. A positive slack
            // is legitimate consumption and never rejects.
            if (harvestEngaged && gainCheck.AnchorLeg?.RouteRunManifest != null)
            {
                FlowClosureResult closure = ComputeFlowClosure(
                    ToAmountDict(gainCheck.AnchorLeg.RouteRunManifest.StartTransportResources),
                    new List<Dictionary<string, double>> { loadResources },
                    gainCheck.HarvestedManifest,
                    new List<Dictionary<string, double>> { resources },
                    ToAmountDict(window.UndockTransportResources));
                if (!closure.Closes)
                {
                    Diag(logMode,
                        $"RouteAnalysis rejected: flow does not close resource={closure.OffendingResource} " +
                        $"unaccounted={closure.UnaccountedQuantity.ToString("R", CultureInfo.InvariantCulture)} " +
                        $"source={source?.RecordingId ?? "<none>"}");
                    return new RouteAnalysisResult
                    {
                        Status = RouteAnalysisStatus.FlowDoesNotClose,
                        SourceRecording = source,
                        ConnectionWindow = window,
                        RejectDetail = closure.RejectDetail,
                        ResourceLoadManifest = hasLoad ? loadResources : null,
                        HarvestedManifest = gainCheck.HarvestedManifest,
                        FirstHarvestWindow = gainCheck.FirstHarvestWindow
                    };
                }
            }

            // M2 refined undocked-start gate (plan D6, two-phase): on the
            // harvest-data path the deferred verdict lands here. Undocked
            // start stays rejected when the run delivers INVENTORY (no
            // harvest provenance exists for inventory in M2) or when any
            // delivered resource exceeds its witnessed harvested total; a
            // fully-harvest-covered delivery becomes Eligible as a
            // HARVEST-ORIGIN run (D7).
            bool isHarvestOrigin = false;
            if (harvestEngaged && IsUndockedStartOrigin(originRec))
            {
                string undockedRejectReason = null;
                if (inventory != null && inventory.Count > 0)
                {
                    undockedRejectReason = "inventory-delivery-not-harvestable";
                }
                else if (resources != null)
                {
                    foreach (KeyValuePair<string, double> kvp in resources)
                    {
                        double harvestedAmount = 0.0;
                        gainCheck.HarvestedManifest?.TryGetValue(kvp.Key, out harvestedAmount);
                        if (kvp.Value > harvestedAmount + RouteHarvestAnalysis.GainEpsilon)
                        {
                            undockedRejectReason =
                                $"delivered-exceeds-harvested resource={kvp.Key} " +
                                $"delivered={kvp.Value.ToString("R", CultureInfo.InvariantCulture)} " +
                                $"harvested={harvestedAmount.ToString("R", CultureInfo.InvariantCulture)}";
                            break;
                        }
                    }
                }

                if (undockedRejectReason != null)
                {
                    Diag(logMode,
                        $"RouteAnalysis rejected: undocked-start origin (harvest-refined) " +
                        $"originRec={originRec?.RecordingId ?? "<none>"} reason={undockedRejectReason}");
                    return new RouteAnalysisResult
                    {
                        Status = RouteAnalysisStatus.UndockedStartOrigin,
                        SourceRecording = source,
                        ConnectionWindow = window,
                        HarvestedManifest = gainCheck.HarvestedManifest,
                        FirstHarvestWindow = gainCheck.FirstHarvestWindow
                    };
                }

                isHarvestOrigin = true;
                Diag(logMode,
                    $"RouteAnalysis: undocked start fully harvest-covered -> harvest origin " +
                    $"originRec={originRec?.RecordingId ?? "<none>"} " +
                    $"resources={resources?.Count ?? 0}");
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
                $"load={loadResources?.Count ?? 0} " +
                $"inventory={inventory?.Count ?? 0} " +
                $"inventoryLoad={loadInventory?.Count ?? 0} " +
                $"harvestData={(harvestEngaged ? "1" : "0")} " +
                $"harvestOrigin={(isHarvestOrigin ? "1" : "0")}");

            return new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.Eligible,
                SourceRecording = source,
                ConnectionWindow = window,
                ResourceDeliveryManifest = resources,
                InventoryDeliveryManifest = inventory,
                ResourceLoadManifest = (loadResources != null && loadResources.Count > 0)
                    ? loadResources
                    : null,
                InventoryLoadManifest = hasInventoryLoad ? loadInventory : null,
                HarvestedManifest = harvestEngaged ? gainCheck.HarvestedManifest : null,
                IsHarvestOrigin = isHarvestOrigin,
                FirstHarvestWindow = harvestEngaged ? gainCheck.FirstHarvestWindow : null
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

        // EC/IntakeAir are the always-ignored environmental-noise resources.
        // The rule text lives on ResourceTransferability.IsAlwaysIgnored (M2
        // D1: the transferability rule has one authority).
        private static bool IsIgnoredResource(string name)
        {
            return ResourceTransferability.IsAlwaysIgnored(name);
        }

        // D2 direction-sensitivity (M2/M3): a PRESENCE detector - "does this
        // window carry ANY resource pickup?" - that deliberately keeps seeing
        // UNDEFINED resource names. NOTE: this is NOT the live eligibility
        // guard. As of M3 Phase 1 it is DEAD in the eligibility flow:
        // AnalyzeWindow no longer calls it, and the former
        // HasMixedPickupDelivery path that used it was deleted. The actual
        // fail-closed protection for an undefined-name pickup is the WIDENED
        // NoDeliveryManifest gate in AnalyzeWindow: an undefined name is
        // dropped from BOTH the load and delivery admission manifests
        // (BuildResourceLoadManifest / BuildResourceDeliveryManifest exclude
        // it), so a pure undefined-name pickup has hasLoad=false AND
        // hasDelivery=false and rejects at the no-delivery-AND-no-load gate.
        // A mod uninstall therefore cannot flip a recorded pickup from
        // rejected into a phantom admitted load term. This method is retained
        // ONLY as a directly-tested presence detector / future hook (the
        // undefined-name direction pin), not the live guard.
        internal static bool HasResourcePickup(RouteConnectionWindow window, out string reason)
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

        // Inventory pickup PRESENCE detector - "does this window carry ANY
        // inventory move FROM the endpoint OR onto the transport?" As of M3
        // Phase 5 it is DEAD in the eligibility flow: AnalyzeWindow no longer
        // calls it (the former inventory-reject path was REPLACED by
        // classification via BuildInventoryLoadManifest +
        // HasUnwitnessedInventoryGain). It is retained as a directly-tested
        // presence detector mirroring HasResourcePickup. The actual fail-closed
        // protection for an unwitnessed transport inventory gain is
        // HasUnwitnessedInventoryGain, the non-fungible window-local closure.
        internal static bool HasInventoryPickup(RouteConnectionWindow window, out string reason)
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

        internal static Dictionary<string, double> BuildResourceDeliveryManifest(
            RouteConnectionWindow window,
            string recordingId,
            RouteAnalysisLogMode logMode)
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

                // M2 transferability rule (ResourceTransferability, D1/D2):
                // this is the ADMISSION direction, so two exclusions apply.
                // EC/IntakeAir are environmental noise: never list them as
                // delivered cargo, so an EC-only "delivery" (transport charges
                // the depot's batteries) yields an empty manifest and the
                // candidate is rejected as no-delivery rather than treated as
                // an EC supply run (design section 6); silent, pre-M2
                // behavior. An UNDEFINED name (its defining mod was
                // uninstalled) is excluded AND logged - the recording degrades
                // to NoDeliveryManifest instead of routing a phantom resource,
                // and reinstalling the mod restores it. This admission-side
                // exclusion is itself the fail-closed mechanism: paired with
                // the same exclusion in BuildResourceLoadManifest, an
                // undefined-name pickup yields hasLoad=false AND
                // hasDelivery=false and rejects at AnalyzeWindow's widened
                // no-delivery-AND-no-load gate (a mod uninstall cannot route a
                // phantom resource in EITHER direction). HasResourcePickup is
                // a dead-in-eligibility presence detector, not the guard - see
                // the comment there.
                if (!ResourceTransferability.IsRoutableResource(name, out string excludeReason))
                {
                    if (excludeReason == ResourceTransferability.ReasonUndefined)
                        LogUndefinedResourceExclusion(logMode, name, recordingId);
                    continue;
                }

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

        /// <summary>
        /// M3 pickup-direction load manifest (plan D2, item 1): the EXACT
        /// sign-flip mirror of <see cref="BuildResourceDeliveryManifest"/>. Per
        /// routable resource name, <c>loaded = min(endpointLoss, transportGain)</c>
        /// where <c>endpointLoss = DockEndpoint - UndockEndpoint</c> (cargo that
        /// left the endpoint across the window) and
        /// <c>transportGain = UndockTransport - DockTransport</c> (cargo that
        /// arrived on the transport). Admit a name ONLY when BOTH terms exceed
        /// the epsilon (both witness the same flow). This is the ADMISSION
        /// direction: EC/IntakeAir and UNDEFINED names are EXCLUDED exactly as
        /// the delivery builder excludes them (an undefined name is logged once
        /// and dropped; reinstalling the mod restores it). This undefined-name
        /// exclusion, paired with the same exclusion in
        /// <see cref="BuildResourceDeliveryManifest"/>, is the D2 fail-closed
        /// mechanism: a pure undefined-name pickup produces hasLoad=false AND
        /// hasDelivery=false and rejects at <see cref="AnalyzeWindow"/>'s
        /// widened no-delivery-AND-no-load (<see cref="RouteAnalysisStatus.NoDeliveryManifest"/>)
        /// gate, so a mod uninstall can never flip a recorded pickup from
        /// rejected into a phantom admitted load term. (<see cref="HasResourcePickup"/>
        /// keeps undefined names visible but is dead in the eligibility flow;
        /// it is NOT the guard.) Returns null when no resource pickup is
        /// witnessed.
        /// </summary>
        internal static Dictionary<string, double> BuildResourceLoadManifest(
            RouteConnectionWindow window,
            string recordingId,
            RouteAnalysisLogMode logMode)
        {
            var load = new Dictionary<string, double>();
            AddResourceDeliveryKeys(load, window.DockEndpointResources);
            AddResourceDeliveryKeys(load, window.UndockEndpointResources);
            AddResourceDeliveryKeys(load, window.DockTransportResources);
            AddResourceDeliveryKeys(load, window.UndockTransportResources);

            var names = new List<string>(load.Keys);
            load.Clear();

            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];

                // Same admission-direction exclusions as the delivery builder:
                // EC/IntakeAir (environmental noise) and undefined names are
                // dropped, undefined names logged. Dropping the undefined name
                // from BOTH manifests is what makes the no-delivery-AND-no-load
                // gate the fail-closed guard (see the summary above).
                if (!ResourceTransferability.IsRoutableResource(name, out string excludeReason))
                {
                    if (excludeReason == ResourceTransferability.ReasonUndefined)
                        LogUndefinedResourceExclusion(logMode, name, recordingId);
                    continue;
                }

                double endpointLoss =
                    GetResourceAmount(window.DockEndpointResources, name) -
                    GetResourceAmount(window.UndockEndpointResources, name);
                double transportGain =
                    GetResourceAmount(window.UndockTransportResources, name) -
                    GetResourceAmount(window.DockTransportResources, name);

                if (endpointLoss <= ResourceEpsilon || transportGain <= ResourceEpsilon)
                    continue;

                load[name] = Math.Min(endpointLoss, transportGain);
            }

            return load.Count > 0 ? load : null;
        }

        /// <summary>
        /// M3 full-run flow closure (plan D3). Per resource:
        /// <c>consumed := launched + Sum(loaded) + Sum(harvested) - Sum(delivered) - residual</c>.
        /// Terms: <paramref name="launched"/> = the anchor run manifest's
        /// <c>StartTransportResources</c> (0 for a docked / harvest start);
        /// <paramref name="residual"/> = the transport's POST-window cargo
        /// (<c>window.UndockTransportResources</c>) - the actual run-end transport
        /// state for a single-window run, NOT the anchor leg's mid-run END
        /// manifest (which, when the anchor leg ends AT the dock, still contains
        /// the about-to-be-delivered cargo and would double-subtract delivered);
        /// <paramref name="harvested"/> = the M2 harvested manifest;
        /// <paramref name="loadedWindows"/> / <paramref name="deliveredWindows"/>
        /// sum over the window LISTS (length 1 in M3a; the list shape makes M4
        /// multi-window a fill-in, not a rewrite). Returns NOT-closed only when
        /// some resource's pre-clamp slack is &lt; -<see cref="RouteHarvestAnalysis.GainEpsilon"/>
        /// (over-delivery / phantom cargo: delivered + residual exceed arrived).
        /// A POSITIVE slack is legitimate burn / consumption and closes. The scan
        /// is ordinal so the FIRST offending resource is deterministic. Pure; the
        /// caller presence-gates it on a resolved anchor manifest (OQ2) - the
        /// window-direction classification already admitted any pickup
        /// window-locally, so a skipped closure never drops cargo.
        /// </summary>
        internal static FlowClosureResult ComputeFlowClosure(
            Dictionary<string, double> launched,
            List<Dictionary<string, double>> loadedWindows,
            Dictionary<string, double> harvested,
            List<Dictionary<string, double>> deliveredWindows,
            Dictionary<string, double> residual)
        {
            // Union of every resource name that appears in any term, skipping
            // the always-ignored environmental resources (EC/IntakeAir never
            // participate in the balance).
            var names = new HashSet<string>(StringComparer.Ordinal);
            AddDictNames(names, launched);
            AddDictNames(names, residual);
            AddDictNames(names, harvested);
            AddWindowListNames(names, loadedWindows);
            AddWindowListNames(names, deliveredWindows);

            var ordered = new List<string>(names);
            ordered.Sort(StringComparer.Ordinal);

            for (int i = 0; i < ordered.Count; i++)
            {
                string name = ordered[i];
                if (ResourceTransferability.IsAlwaysIgnored(name))
                    continue;

                double launchedAmount = 0.0;
                launched?.TryGetValue(name, out launchedAmount);
                double residualAmount = 0.0;
                residual?.TryGetValue(name, out residualAmount);
                double loaded = SumWindowList(loadedWindows, name);
                double delivered = SumWindowList(deliveredWindows, name);
                double harvestedAmount = 0.0;
                harvested?.TryGetValue(name, out harvestedAmount);

                double slack =
                    launchedAmount + loaded + harvestedAmount - delivered - residualAmount;

                // Over-delivery: more cargo left the transport (delivered +
                // residual) than ever arrived (launched + loaded + harvested).
                if (slack < -RouteHarvestAnalysis.GainEpsilon)
                {
                    double over = -slack;
                    return new FlowClosureResult
                    {
                        Closes = false,
                        OffendingResource = name,
                        UnaccountedQuantity = over,
                        RejectDetail = FormatClosureDetail(name, over)
                    };
                }
            }

            return FlowClosureResult.Closed();
        }

        /// <summary>
        /// Projects a <see cref="ResourceAmount"/> manifest down to a flat
        /// name-&gt;amount dictionary for <see cref="ComputeFlowClosure"/>.
        /// Returns null for a null input (the closure tolerates null terms).
        /// </summary>
        internal static Dictionary<string, double> ToAmountDict(
            Dictionary<string, ResourceAmount> manifest)
        {
            if (manifest == null)
                return null;
            var dict = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, ResourceAmount> kvp in manifest)
            {
                if (!string.IsNullOrEmpty(kvp.Key))
                    dict[kvp.Key] = kvp.Value.amount;
            }
            return dict;
        }

        /// <summary>
        /// The flow-closure reject detail shown to the player, e.g.
        /// <c>"Ore: 30.0 over-delivered"</c> (InvariantCulture); mirrors
        /// <see cref="RouteHarvestAnalysis.FormatGainDetail"/>'s shape.
        /// </summary>
        internal static string FormatClosureDetail(string name, double overDelivered)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0}: {1:F1} over-delivered",
                string.IsNullOrEmpty(name) ? "unknown" : name, overDelivered);
        }

        private static void AddDictNames(
            HashSet<string> names, Dictionary<string, double> manifest)
        {
            if (manifest == null)
                return;
            foreach (string name in manifest.Keys)
            {
                if (!string.IsNullOrEmpty(name))
                    names.Add(name);
            }
        }

        private static void AddWindowListNames(
            HashSet<string> names, List<Dictionary<string, double>> windows)
        {
            if (windows == null)
                return;
            for (int i = 0; i < windows.Count; i++)
                AddDictNames(names, windows[i]);
        }

        private static double SumWindowList(
            List<Dictionary<string, double>> windows, string name)
        {
            if (windows == null)
                return 0.0;
            double total = 0.0;
            for (int i = 0; i < windows.Count; i++)
            {
                if (windows[i] != null && windows[i].TryGetValue(name, out double v))
                    total += v;
            }
            return total;
        }

        // M2 logging plan row 1: the undefined-name admission skip. One-shot
        // callers (Diagnostic: commit-time / the Create Route dialog) log per
        // name at Info; the ~1/second candidate sweep (Quiet) folds into one
        // shared rate-limited key so the poll cannot spam. Per-name logging is
        // fine in Diagnostic mode: distinct resource names per window are
        // bounded well under the ~20-item batch-counter threshold.
        private static void LogUndefinedResourceExclusion(
            RouteAnalysisLogMode logMode,
            string name,
            string recordingId)
        {
            string message =
                $"Resource excluded: name={name} " +
                $"reason={ResourceTransferability.ReasonUndefined} " +
                $"recording={recordingId ?? "<none>"}";

            if (logMode == RouteAnalysisLogMode.Diagnostic)
                ParsekLog.Info(RouteOrchestrator.Tag, message);
            else
                ParsekLog.VerboseRateLimited(
                    RouteOrchestrator.Tag, "resource-excluded-undefined", message);
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

        /// <summary>
        /// M3 inventory pickup load manifest (plan D7, Phase 5): the EXACT
        /// sign-flip mirror of <see cref="BuildInventoryDeliveryManifest"/>. Per
        /// exact <see cref="InventoryPayloadItem.IdentityHash"/>,
        /// <c>loaded = min(endpointLoss, transportGain)</c> where
        /// <c>endpointLoss = DockEndpoint - UndockEndpoint</c> (stored parts that
        /// left the endpoint across the window) and
        /// <c>transportGain = UndockTransport - DockTransport</c> (stored parts
        /// that arrived on the transport). Admit an identity ONLY when BOTH terms
        /// are positive (both witness the same move). The identity is carried
        /// intact: the returned <see cref="InventoryPayloadItem"/> deep-clones the
        /// source payload (StoredPartSnapshot, StoredResources, PartName,
        /// VariantName, IdentityHash) with the loaded quantity / endpoint-side
        /// slots-lost set, mirroring the delivery builder's identity handling.
        /// The transport side is the gain witness; the SOURCE-side payload
        /// (DockEndpointInventory, the depot's own STOREDPART) is the canonical
        /// copy carried so the Phase-5 source probe + remove writer match against
        /// the depot's own stored geometry. Returns null when no inventory pickup
        /// is witnessed. The non-fungible window-local closure (an unwitnessed
        /// transport gain) is enforced separately by
        /// <see cref="HasUnwitnessedInventoryGain"/> BEFORE this admits.
        /// </summary>
        internal static List<InventoryPayloadItem> BuildInventoryLoadManifest(
            RouteConnectionWindow window)
        {
            // Source-side canonical copies: the DEPOT'S OWN stored part is the
            // payload the Phase-5 source probe + remove writer match against, so
            // seed the identity map from the endpoint inventories first (the
            // delivery builder seeds from UndockEndpoint = the gain side; the
            // pickup mirror seeds from the endpoint LOSS side = DockEndpoint).
            Dictionary<string, InventoryPayloadItem> loadedByIdentity =
                BuildInventoryMap(window.DockEndpointInventory);
            AddInventoryKeys(loadedByIdentity, window.UndockEndpointInventory);
            AddInventoryKeys(loadedByIdentity, window.DockTransportInventory);
            AddInventoryKeys(loadedByIdentity, window.UndockTransportInventory);

            if (loadedByIdentity.Count == 0)
                return null;

            var identities = new List<string>(loadedByIdentity.Keys);
            var load = new List<InventoryPayloadItem>();
            for (int i = 0; i < identities.Count; i++)
            {
                string identity = identities[i];
                int endpointLoss =
                    GetInventoryQuantity(window.DockEndpointInventory, identity) -
                    GetInventoryQuantity(window.UndockEndpointInventory, identity);
                int transportGain =
                    GetInventoryQuantity(window.UndockTransportInventory, identity) -
                    GetInventoryQuantity(window.DockTransportInventory, identity);

                int loaded = Math.Min(endpointLoss, transportGain);
                if (loaded <= 0)
                    continue;

                int endpointSlotsLost =
                    GetInventorySlots(window.DockEndpointInventory, identity) -
                    GetInventorySlots(window.UndockEndpointInventory, identity);

                InventoryPayloadItem source = loadedByIdentity[identity];
                InventoryPayloadItem item = source.DeepClone();
                item.Quantity = loaded;
                item.SlotsTaken = Math.Max(0, endpointSlotsLost);
                SetStoredPartQuantity(item.StoredPartSnapshot, loaded);
                load.Add(item);
            }

            load.Sort((a, b) => string.Compare(a.IdentityHash, b.IdentityHash, StringComparison.Ordinal));
            return load.Count > 0 ? load : null;
        }

        /// <summary>
        /// M3 window-local inventory closure (plan D7 / OQ3, Phase 5): inventory
        /// is non-fungible and has NO harvested provenance, so a transport
        /// inventory GAIN with NO matching endpoint LOSS is unwitnessed cargo and
        /// MUST fail closed. Per identity, the WITNESSED load term is
        /// <c>min(endpointLoss, transportGain)</c>; the UNWITNESSED gain is
        /// <c>transportGain - loaded == transportGain - min(endpointLoss, transportGain)</c>
        /// which is positive exactly when <c>transportGain > endpointLoss</c> (the
        /// transport gained more than the endpoint gave). Returns true on the
        /// FIRST identity (ordinal order, deterministic) with an unwitnessed gain,
        /// naming it + the unwitnessed quantity in <paramref name="reason"/>. A
        /// clean pickup (gain fully matched by an endpoint loss) returns false and
        /// admits via <see cref="BuildInventoryLoadManifest"/>. This is the
        /// inventory analogue of the resource <c>FlowDoesNotClose</c> over-supply
        /// reject, scoped window-locally because inventory carries no full-run
        /// closure (OQ3 defers Start/EndTransportInventory).
        /// </summary>
        internal static bool HasUnwitnessedInventoryGain(
            RouteConnectionWindow window, out string reason)
        {
            reason = null;

            Dictionary<string, InventoryPayloadItem> identities =
                BuildInventoryMap(window.DockEndpointInventory);
            AddInventoryKeys(identities, window.UndockEndpointInventory);
            AddInventoryKeys(identities, window.DockTransportInventory);
            AddInventoryKeys(identities, window.UndockTransportInventory);

            var ordered = new List<string>(identities.Keys);
            ordered.Sort(StringComparer.Ordinal);

            for (int i = 0; i < ordered.Count; i++)
            {
                string identity = ordered[i];
                int endpointLoss =
                    GetInventoryQuantity(window.DockEndpointInventory, identity) -
                    GetInventoryQuantity(window.UndockEndpointInventory, identity);
                int transportGain =
                    GetInventoryQuantity(window.UndockTransportInventory, identity) -
                    GetInventoryQuantity(window.DockTransportInventory, identity);

                if (transportGain <= 0)
                    continue;

                int witnessed = Math.Min(Math.Max(0, endpointLoss), transportGain);
                int unwitnessed = transportGain - witnessed;
                if (unwitnessed > 0)
                {
                    reason =
                        $"inventory={identity} " +
                        $"transportGain={transportGain.ToString(CultureInfo.InvariantCulture)} " +
                        $"endpointLoss={endpointLoss.ToString(CultureInfo.InvariantCulture)} " +
                        $"unwitnessed={unwitnessed.ToString(CultureInfo.InvariantCulture)}";
                    return true;
                }
            }

            return false;
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
