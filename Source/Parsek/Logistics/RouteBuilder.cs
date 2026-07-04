using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Pure constructor that turns an eligible <see cref="RouteAnalysisResult"/>
    /// plus the source <see cref="RecordingTree"/> and player-provided inputs
    /// (name, dispatch interval) into a fully populated <see cref="Route"/> ready
    /// for <see cref="RouteStore.AddRoute"/>. Used by the route creation dialog;
    /// kept pure so it is straightforward to unit test without spinning up a
    /// dialog or PopupDialog.
    /// </summary>
    /// <remarks>
    /// Logs every decision branch under the unified <c>Route</c> subsystem tag
    /// so the route subsystem's logs are co-located in <c>KSP.log</c> with the
    /// store, codec, analysis, dialog, and ledger module log sites.
    /// </remarks>
    internal static class RouteBuilder
    {
        private const string Tag = "Route";

        /// <summary>
        /// Player-controlled fields that the dialog collects before commit.
        /// Kept as a struct so call sites are explicit about what flows from
        /// the UI vs. what is derived from the analysis result.
        /// </summary>
        internal struct RouteCreationInputs
        {
            internal string Name;
            internal double DispatchIntervalSeconds;
        }

        /// <summary>
        /// Outcome of a build attempt. Exactly one of
        /// <see cref="Route"/> / <see cref="RejectReason"/> is populated.
        /// </summary>
        internal sealed class RouteBuildOutcome
        {
            internal Route Route;
            internal string RejectReason;
        }

        /// <summary>
        /// Build a <see cref="Route"/> from the analysis result, the source
        /// committed tree, and the player-provided inputs. <paramref name="idFactory"/>
        /// defaults to <see cref="Guid.NewGuid()"/> and exists so tests can seed
        /// a deterministic id (e.g. to exercise duplicate-id rejection paths in
        /// <see cref="RouteStore.AddRoute"/>).
        /// </summary>
        internal static RouteBuildOutcome BuildRoute(
            RouteAnalysisResult analysis,
            RecordingTree committedTree,
            RouteCreationInputs inputs,
            Game.Modes mode,
            Func<string> idFactory = null,
            RouteStatus initialStatus = RouteStatus.Active,
            bool allowIntervalBelowTransit = false)
        {
            if (analysis == null || !analysis.IsEligible)
            {
                string status = analysis != null
                    ? analysis.Status.ToString()
                    : "<null>";
                ParsekLog.Info(Tag,
                    $"BuildRoute rejected: source-no-longer-eligible status={status}");
                return new RouteBuildOutcome
                {
                    RejectReason = "source-no-longer-eligible"
                };
            }

            Recording source = analysis.SourceRecording;
            if (source == null)
            {
                ParsekLog.Info(Tag,
                    "BuildRoute rejected: source-no-longer-eligible status=Eligible source=<null>");
                return new RouteBuildOutcome
                {
                    RejectReason = "source-no-longer-eligible"
                };
            }

            // Origin info (launch-site for KSC, or the start-docked depot proof)
            // lives on the FIRST recording of the flight - the tree root - not on
            // the window-carrying merged child that analysis.SourceRecording
            // points at (that child started mid-flight at the dock, so it has no
            // launch site). Resolve the root recording for origin discovery; fall
            // back to the source recording when the tree has no resolvable root.
            Recording originRec = source;
            Recording rootRec = null;
            if (committedTree?.Recordings != null
                && !string.IsNullOrEmpty(committedTree.RootRecordingId)
                && committedTree.Recordings.TryGetValue(committedTree.RootRecordingId, out rootRec)
                && rootRec != null)
            {
                originRec = rootRec;
            }

            var ic = CultureInfo.InvariantCulture;

            // (CRE-4) Root-unresolvable reject. The route span is [root.StartUT ..
            // undockUT] and the loop clock's anchor / epoch is rootLaunchUT. When a
            // committed TREE is supplied but its RootRecordingId does NOT resolve to a
            // recording, we MUST NOT fall back to source.StartUT: source is the
            // window-carrying leaf, which started mid-flight at the DOCK, so
            // source.StartUT is the dock UT. Falling back would build a [dock..undock]
            // segment instead of [launch..undock] (a wrong-span route that mis-renders
            // and mis-clocks). Fail fast instead.
            //
            // The committedTree == null path is the legacy single-recording case: the
            // source recording IS the whole flight (its own root), so source.StartUT
            // genuinely IS the launch UT. That path stays valid (rootLaunchUT below
            // resolves to source.StartUT). Production always passes a non-null tree.
            if (committedTree != null && rootRec == null)
            {
                ParsekLog.Info(Tag,
                    $"BuildRoute rejected: root-recording-unresolvable source={source.RecordingId ?? "<none>"} " +
                    $"tree={committedTree.Id ?? "<none>"} " +
                    $"rootId={committedTree.RootRecordingId ?? "<none>"}");
                return new RouteBuildOutcome { RejectReason = "root-recording-unresolvable" };
            }

            // --- Backing-mission geometry (design §0; Phase 5). ---------------
            // The route renders as a looped Mission segment over the source tree's
            // [root.StartUT .. undockUT] path, NOT the leaf-only dock-child span.
            // Resolve the ROOT launch UT (NOT source.StartUT, which is the
            // mid-flight dock child) and the UNDOCK UT (from the connection
            // window). RouteBackingMission owns the interval-key + member-set
            // derivation (the geometry seam over the locked Missions composition).
            // When no tree was supplied (legacy single-recording path), the source IS
            // the root, so rootLaunchUT == source.StartUT.
            double rootLaunchUT = rootRec != null ? rootRec.StartUT : source.StartUT;

            // M4a (plan D4): resolve the ordered per-stop collection. A1 fills
            // analysis.Stops ascending by DockUT (single entry on a single-window
            // run, N on a multi-window run). The pure-logic RouteBuilder tests
            // construct analysis results with ONLY the scalar fields (no Stops
            // list), so fall back to a single synthesized stop mirroring the
            // scalar ConnectionWindow + manifests when Stops is null/empty. The
            // synthesized single stop is byte-identical to the pre-A2 single-stop
            // build (RANK-8). The LAST (max-DockUT) stop drives the route span.
            List<RouteAnalysisStop> analysisStops = ResolveAnalysisStops(analysis);
            int analysisStopCount = analysisStops.Count;
            RouteAnalysisStop lastAnalysisStop = analysisStops[analysisStopCount - 1];

            // The route segment renders [root launch .. DOCK] (playtest follow-up):
            // rendering STOPS at the docking moment, so the docked-together combined
            // vessel (the dock-merged child recording, which spans dock..undock) is
            // NOT rendered. The dock is a clean recording boundary (the transport's
            // solo recording ends at the couple; the merged child starts there), so
            // trimming the segment end to the dock excludes the combined vessel
            // exactly. The UNDOCK UT is kept only for the window-sanity check + logs.
            // M4a (D4): the route span keys on the LAST stop's dock/undock (the
            // run-end leg); for a single-stop route this is the same window as
            // pre-A2 (byte-identical).
            double undockUT = lastAnalysisStop.ConnectionWindow != null
                ? lastAnalysisStop.ConnectionWindow.UndockUT
                : double.NaN;
            double recordedDockUT = lastAnalysisStop.ConnectionWindow != null
                ? lastAnalysisStop.ConnectionWindow.DockUT
                : double.NaN;

            // (must-fix #3) Transit duration is the RENDERED span (DOCK - root launch:
            // the launch-to-dock outbound run), NOT the leaf-only
            // source.EndUT - source.StartUT and NOT the full launch-to-undock span.
            // Also the clamp reference for the DispatchInterval >= span pin below.
            double transitDuration = recordedDockUT - rootLaunchUT;

            // Backing-mission-unresolvable reject: the [launch..dock] window must
            // be finite and non-empty for the loop render + clock to work. The
            // undock must also be finite (used by the CRE-5 window-sanity check).
            if (double.IsNaN(recordedDockUT) || double.IsInfinity(recordedDockUT)
                || double.IsNaN(undockUT) || double.IsInfinity(undockUT)
                || double.IsNaN(rootLaunchUT) || double.IsInfinity(rootLaunchUT)
                || transitDuration <= 0.0)
            {
                ParsekLog.Info(Tag,
                    $"BuildRoute rejected: backing-mission-unresolvable source={source.RecordingId ?? "<none>"} " +
                    $"tree={(committedTree != null ? committedTree.Id ?? "<none>" : "<null>")} " +
                    $"rootLaunchUT={rootLaunchUT.ToString("R", ic)} dockUT={recordedDockUT.ToString("R", ic)} " +
                    $"undockUT={undockUT.ToString("R", ic)} span={transitDuration.ToString("R", ic)}");
                return new RouteBuildOutcome { RejectReason = "backing-mission-unresolvable" };
            }

            if (inputs.DispatchIntervalSeconds <= 0.0)
            {
                ParsekLog.Info(Tag,
                    $"BuildRoute rejected: interval-invalid interval={inputs.DispatchIntervalSeconds.ToString("R", ic)}");
                return new RouteBuildOutcome { RejectReason = "interval-invalid" };
            }

            // (must-fix #2) Clamp/require DispatchInterval >= backing-mission span so
            // the loop-clock CadenceSeconds == max(LoopIntervalSeconds, span) ==
            // DispatchInterval, and one crossing == one dispatch cycle (design 1.4
            // "minimum interval = chain duration"). When the player-entered interval
            // is below the span we CLAMP UP and Info-log (the legacy
            // allowIntervalBelowTransit flag, used by the debug/placeholder Create
            // Route path, now means "clamp instead of reject").
            double dispatchInterval = inputs.DispatchIntervalSeconds;
            if (dispatchInterval < transitDuration)
            {
                if (allowIntervalBelowTransit)
                {
                    ParsekLog.Info(Tag,
                        $"BuildRoute: interval below span -> clamped up entered={dispatchInterval.ToString("R", ic)} " +
                        $"span={transitDuration.ToString("R", ic)} (cadence pinned to span so one crossing == one cycle)");
                    dispatchInterval = transitDuration;
                }
                else
                {
                    ParsekLog.Info(Tag,
                        $"BuildRoute rejected: interval-below-transit interval={dispatchInterval.ToString("R", ic)} " +
                        $"transit={transitDuration.ToString("R", ic)}");
                    return new RouteBuildOutcome { RejectReason = "interval-below-transit" };
                }
            }

            // (Phase 6 / CRE-3) Cadence model: the player-facing dispatch cadence is
            // N x the run's natural period, which for a v0 SAME-BODY route IS the
            // span. Derive N by rounding the resolved interval to the nearest whole
            // multiple of the span, then RE-DERIVE interval = N x span so
            // DispatchInterval and CadenceMultiplier stay in LOCK-STEP (the UI later
            // recomputes the same way; one crossing == one cycle).
            //
            // (M5) On a RE-AIM inter-body route the derived interval is dead input
            // to the unit build (ApplyReaim discards it; the synodic cadence wins)
            // and N instead applies as the residual window modulo at the firing
            // path (RouteLoopClock.ResolveResidualCadence). The lock-step
            // derivation here still runs unchanged - it keeps N/interval coherent
            // for the flat fallback and the zero-drift minSpacing throttle.
            //
            // LOCK-STEP INTENT (CRE-3): this round-then-rederive deliberately SNAPS
            // any player-entered interval to the nearest whole-span multiple. A
            // sub-2x interval (e.g. 1.4 x span) rounds to N=1 and is rewritten back
            // DOWN to exactly span. That is correct, not a bug: the v0 contract is
            // whole-span cadence only, and N=1 == span is the minimum loop time. It
            // can NEVER shrink below span because the clamp above already guarantees
            // dispatchInterval >= span (so Round(>=1.0) >= 1) AND
            // Route.ClampCadenceMultiplier floors N at 1 explicitly. Both guards
            // preserve the one-crossing-per-cycle invariant.
            int cadenceMultiplier =
                Route.ClampCadenceMultiplier((int)System.Math.Round(dispatchInterval / transitDuration));
            // N >= 1 is guaranteed (ClampCadenceMultiplier floor), so the rederived
            // interval is always >= span: it never undercuts the rendered span.
            dispatchInterval = cadenceMultiplier * transitDuration;

            BuildRouteSourceRefs(committedTree, source, recordedDockUT, rootLaunchUT,
                out List<RouteSourceRef> sourceRefs, out List<string> recordingIds);

            // Excluded interval keys for the backing-mission render trim. End at the
            // DOCK so the docked-together combined vessel (the merged child, which
            // starts at the dock) and everything after it are excluded from render.
            HashSet<string> excludedIntervalKeys = committedTree != null
                ? RouteBackingMission.ComputeExcludedIntervalKeys(committedTree, recordedDockUT, rootLaunchUT)
                : new HashSet<string>();

            if (TryResolveRouteOrigin(analysis, source, originRec,
                    out RouteEndpoint origin, out string originLabel,
                    out bool isKscOrigin, out bool isHarvestOrigin,
                    out bool isPickupOrigin) is { } originReject)
            {
                return originReject;
            }

            // M4a (plan D4): build N ordered stops, one per analysis stop (ordered
            // ascending by DockUT in A1). Defensively copy manifests so later store
            // mutations don't reach back into the analysis result.
            //
            // Byte-identity gate (D4 / RANK-8, critical). A SINGLE-stop route MUST
            // serialize byte-identically to the pre-A2 build:
            //   - DeliveryOffsetSeconds is consumed ONLY by the codec (NOT at fire
            //     time - ResolveCycleStop / PendingDeliveryUT do not read it; the
            //     loop fires on the recorded dock PHASE via the loop clock). So its
            //     derivation is GATED to N>1; a single-stop route keeps the 0.0
            //     placeholder exactly as the pre-A2 build set it.
            //   - SegmentIndexBefore: a single-stop route keeps the placeholder 0
            //     exactly as before (matching index resolution on the anchor source
            //     would also land 0 in practice, but pinning it to the placeholder
            //     keeps the single-stop bytes provably unchanged).
            //   - The per-stop RecordedDockUT / LastFiredCycleIndex are GATED to
            //     N>1: a single-stop route leaves them at -1.0 / -1, so the codec
            //     omits both keys (sparse) and the bytes are identical.
            bool isMultiStop = analysisStopCount > 1;
            var stops = new List<RouteStop>(analysisStopCount);
            for (int i = 0; i < analysisStopCount; i++)
            {
                RouteAnalysisStop a = analysisStops[i];
                RouteConnectionWindow window = a.ConnectionWindow;

                // SegmentIndexBefore: best-effort index of the stop's source
                // recording in route.RecordingIds (the member-recording whose
                // completion triggers the stop). -1 when not resolvable. Single-stop
                // keeps the byte-identical placeholder 0 (gated below).
                int segmentIndexBefore = 0;
                double deliveryOffsetSeconds = 0.0;
                double stopRecordedDockUT = -1.0;
                if (isMultiStop)
                {
                    segmentIndexBefore = ResolveSegmentIndexBefore(recordingIds, a.SourceRecording);
                    // The scheduler/display projection per design 6.2 (the actual
                    // per-window firing keys on each window's DockUT through the
                    // loop clock, Phase A3 - NOT this offset).
                    deliveryOffsetSeconds = a.DockUT - rootLaunchUT;
                    // The per-stop firing phase (OQ3/D5; read at fire time in A3).
                    stopRecordedDockUT = a.DockUT;
                }

                stops.Add(new RouteStop
                {
                    Endpoint = window.EndpointAtDock.Value,
                    ConnectionKind = window.TransferKind,
                    DeliveryManifest = a.ResourceDeliveryManifest != null
                        ? new Dictionary<string, double>(a.ResourceDeliveryManifest)
                        : new Dictionary<string, double>(),
                    InventoryDeliveryManifest = a.InventoryDeliveryManifest != null
                        ? new List<InventoryPayloadItem>(a.InventoryDeliveryManifest)
                        : new List<InventoryPayloadItem>(),
                    // M3 pickup direction (plan D8): the analysis load manifest is
                    // the resource cargo that flowed FROM the endpoint ONTO the
                    // transport across the window (the sign-flip mirror of delivery).
                    // Defensively copy. Null when the window carried no resource
                    // pickup -> the codec omits the PICKUP_MANIFEST node.
                    PickupManifest = a.ResourceLoadManifest != null
                        ? new Dictionary<string, double>(a.ResourceLoadManifest)
                        : null,
                    // M3 inventory pickup (plan D7/D8): the analysis inventory load
                    // manifest is the stored-part cargo loaded FROM the endpoint
                    // ONTO the transport (identity carried intact). Deep-copy so
                    // store mutations cannot reach back into the analysis result
                    // (the items carry mutable StoredPartSnapshot ConfigNodes). Null
                    // when the window carried no inventory pickup -> the codec omits
                    // the INVENTORY_PICKUP_MANIFEST node.
                    InventoryPickupManifest = a.InventoryLoadManifest != null
                        ? RouteProofMetadata.CloneInventoryPayloadItems(a.InventoryLoadManifest)
                        : null,
                    SegmentIndexBefore = segmentIndexBefore,
                    DeliveryOffsetSeconds = deliveryOffsetSeconds,
                    RecordedDockUT = stopRecordedDockUT
                    // LastFiredCycleIndex left at its -1 default (A3 wires firing).
                });
            }

            string routeId = (idFactory ?? DefaultIdFactory)();
            string routeName = !string.IsNullOrEmpty(inputs.Name)
                ? inputs.Name
                : RouteCreationFormatters.GenerateDefaultRouteName(analysis, committedTree);

            BuildRouteCostManifests(analysis, isHarvestOrigin, isPickupOrigin, isKscOrigin, ic,
                out Dictionary<string, double> costManifest,
                out List<InventoryPayloadItem> inventoryCostManifest);

            // Loop-clock dock binding: recordedDockUT (computed above as the route
            // segment END) is the UT the loop clock crosses each cycle to fire
            // delivery (Phase 4); with the [launch..dock] segment it equals spanEnd.
            // LoopAnchorUT is set here only when the route is created ACTIVE (the
            // dialog path); the Paused->Activate path sets it in
            // RouteOrchestrator.TryActivate.

            // (CRE-5) Validate the recorded dock UT lies strictly inside the source
            // [rootLaunchUT .. undockUT] window: the dock must come AFTER launch (a
            // non-empty rendered [launch..dock] segment) and BEFORE undock (a
            // well-formed dock/undock pair). A malformed window (NaN dock,
            // dock <= launch, or dock >= undock) would build a route whose loop
            // clock can never fire a crossing (RouteLoopClock.IsDockUTInSpan false
            // forever -> a route that never delivers). Fail fast instead of
            // persisting a dead route.
            if (!IsDockUTWithinSpan(recordedDockUT, rootLaunchUT, undockUT))
            {
                ParsekLog.Info(Tag,
                    $"BuildRoute rejected: dock-ut-out-of-span source={source.RecordingId ?? "<none>"} " +
                    $"dockUT={recordedDockUT.ToString("R", ic)} rootLaunchUT={rootLaunchUT.ToString("R", ic)} " +
                    $"undockUT={undockUT.ToString("R", ic)} span={transitDuration.ToString("R", ic)}");
                return new RouteBuildOutcome { RejectReason = "dock-ut-out-of-span" };
            }

            double loopAnchorUT = initialStatus == RouteStatus.Active ? rootLaunchUT : -1.0;

            // (M-MIS-9-R1) Creation-time tree-membership snapshot scoping the
            // recovery-credit sum: every recording id in the source tree RIGHT
            // NOW, including the post-undock fly-home-and-recover leg (gotcha
            // G1). Post-creation branches mint new ids outside this set, so the
            // per-cycle credit cannot inflate. The defensive null-tree path
            // (production dialog/candidate sources always pass a committed
            // tree) leaves the snapshot EMPTY so the run-cost resolver fails
            // open to the whole current tree: a member-id fallback would
            // exclude the recover leg if the tree id resolved later (G1).
            var creationTreeRecordingIds = new HashSet<string>(StringComparer.Ordinal);
            if (committedTree?.Recordings != null)
            {
                foreach (string treeRecId in committedTree.Recordings.Keys)
                {
                    if (!string.IsNullOrEmpty(treeRecId))
                        creationTreeRecordingIds.Add(treeRecId);
                }
            }

            var route = new Route
            {
                Id = routeId,
                Name = routeName,
                Status = initialStatus,
                RecordingIds = recordingIds,
                SourceRefs = sourceRefs,
                Origin = origin,
                IsKscOrigin = isKscOrigin,
                IsHarvestOrigin = isHarvestOrigin,
                Stops = stops,
                TransitDuration = transitDuration,
                DispatchInterval = dispatchInterval,
                CadenceMultiplier = cadenceMultiplier,
                DispatchWindowEpochUT = rootLaunchUT,
                DispatchWindowPeriod = 0.0,
                // Placeholder until scheduler (Phase 6+) computes from epoch + interval.
                NextDispatchUT = rootLaunchUT + dispatchInterval,
                KscDispatchFundsCost = 0.0,
                CostManifest = costManifest,
                InventoryCostManifest = inventoryCostManifest,
                PauseAfterCurrentCycle = false,
                CompletedCycles = 0,
                SkippedCycles = 0,
                LinkedRouteId = null,
                CurrentSegmentIndex = -1,
                PendingStopIndex = -1,
                // Backing-mission definition (design §0; Phase 5 capture).
                BackingMissionTreeId = source.TreeId,
                ExcludedIntervalKeys = excludedIntervalKeys,
                CreationTreeRecordingIds = creationTreeRecordingIds,
                RecordedDockUT = recordedDockUT,
                // A2 review fold: the route-span pair (RecordedDockUT /
                // DockMemberRecordingId) must reference the SAME leaf - the
                // run-end (last, max-DockUT) dock. RecordedDockUT already uses
                // lastAnalysisStop; key the member id on the last stop's source
                // too (falling back to the anchor `source` when the last stop
                // carries none). For a single-stop route lastAnalysisStop's
                // source IS `source`, so this is byte-identical; for a
                // cross-recording multi-stop route it keeps the pair coherent
                // (A4 end-trim + MissionRouteStructureList resolve the dock
                // window through DockMemberRecordingId).
                DockMemberRecordingId = (lastAnalysisStop.SourceRecording ?? source).RecordingId,
                LoopAnchorUT = loopAnchorUT,
                LastObservedLoopCycleIndex = -1,
                // M5 (D3): a fresh route starts with the residual-modulo anchor
                // unset - the first owed crossing on a windowed route adopts it
                // and delivers. Explicit (matches the field default) so the
                // creation-default contract is visible at the build site.
                WindowAnchorCycleIndex = -1
            };

            LogBuiltRoute(routeId, originLabel, origin, source, rootLaunchUT, undockUT,
                recordedDockUT, transitDuration, dispatchInterval, cadenceMultiplier,
                recordingIds, excludedIntervalKeys, creationTreeRecordingIds, stops,
                isMultiStop, mode, ic);

            return new RouteBuildOutcome { Route = route };
        }

        /// <summary>
        /// Origin discovery (phase extract of <see cref="BuildRoute"/>). KSC-origin
        /// if recording carries a launch site name AND was launched from Kerbin.
        /// Otherwise non-KSC origin requires
        /// <c>RouteOriginProof.StartDockedOriginVesselPid != 0</c>. The M2 harvest
        /// origin admits an originless run whose delivered resources were all
        /// witnessed-harvested; the M3 pickup origin (out param
        /// <paramref name="isPickupOrigin"/>) admits an originless PURE-PICKUP run
        /// that loaded cargo FROM the dock endpoint with NOTHING delivered. Endpoint
        /// coords default to zero for the launch-site path — the scheduler resolves
        /// real coords from the launch-site name. The non-KSC path uses the proof's
        /// origin endpoint descriptor (M1) when present; pre-descriptor proofs keep
        /// the PID-only shape. Returns the <c>endpoint-missing</c>
        /// <see cref="RouteBuildOutcome"/> when no origin resolves, otherwise
        /// <c>null</c> with the origin written through the <c>out</c> params.
        /// </summary>
        private static RouteBuildOutcome TryResolveRouteOrigin(
            RouteAnalysisResult analysis,
            Recording source,
            Recording originRec,
            out RouteEndpoint origin,
            out string originLabel,
            out bool isKscOrigin,
            out bool isHarvestOrigin,
            out bool isPickupOrigin)
        {
            isKscOrigin =
                !string.IsNullOrEmpty(originRec.LaunchSiteName)
                && string.Equals(originRec.StartBodyName, "Kerbin", StringComparison.Ordinal);

            // M3 pickup direction (plan D8): an originless PURE-PICKUP run loads
            // its cargo FROM the dock endpoint (the reverse of delivery), so it
            // has no KSC launch, no start-docked origin proof, and no harvest
            // origin - the v0 chain below would reject it endpoint-missing. The
            // endpoint IS the source (debited later, Phase 3/4), so admit it: a
            // populated load manifest with NO delivery is the witnessed proof
            // the cargo came aboard. Resolution metadata only - a window that
            // ALSO delivers (mixed) keeps its real KSC / docked origin and never
            // reaches this branch.
            bool hasResourceLoad = analysis.ResourceLoadManifest != null
                && analysis.ResourceLoadManifest.Count > 0;
            bool hasInventoryLoad = analysis.InventoryLoadManifest != null
                && analysis.InventoryLoadManifest.Count > 0;
            bool hasLoad = hasResourceLoad || hasInventoryLoad;
            bool hasDelivery =
                (analysis.ResourceDeliveryManifest != null
                    && analysis.ResourceDeliveryManifest.Count > 0)
                || (analysis.InventoryDeliveryManifest != null
                    && analysis.InventoryDeliveryManifest.Count > 0);

            isHarvestOrigin = false;
            isPickupOrigin = false;
            if (isKscOrigin)
            {
                origin = new RouteEndpoint
                {
                    VesselPersistentId = 0,
                    BodyName = "Kerbin",
                    // v0: launch-site coordinates resolved at dispatch time by name.
                    // TODO: item 5 scheduler must resolve KSC coords via source ref → recording →
                    // LaunchSiteName, going through EffectiveState.ComputeERS(). The Route does NOT
                    // persist LaunchSiteName — only Origin.BodyName == "Kerbin" and IsKscOrigin == true.
                    Latitude = 0.0,
                    Longitude = 0.0,
                    Altitude = 0.0,
                    IsSurface = true
                };
                originLabel = "ksc";
            }
            else if (originRec.RouteOriginProof != null
                && originRec.RouteOriginProof.StartDockedOriginVesselPid != 0)
            {
                RouteOriginProof originProof = originRec.RouteOriginProof;
                if (!string.IsNullOrEmpty(originProof.StartDockedOriginBodyName))
                {
                    // M1: the proof carries the origin endpoint descriptor captured at
                    // recording start, so build a real-coordinate endpoint. Surface-base
                    // origins thereby reach RouteEndpointResolver's proximity fallback
                    // when the depot's pid no longer resolves.
                    origin = new RouteEndpoint
                    {
                        VesselPersistentId = originProof.StartDockedOriginVesselPid,
                        BodyName = originProof.StartDockedOriginBodyName,
                        Latitude = originProof.StartDockedOriginLatitude,
                        Longitude = originProof.StartDockedOriginLongitude,
                        Altitude = originProof.StartDockedOriginAltitude,
                        IsSurface = originProof.StartDockedOriginIsSurface
                    };
                }
                else
                {
                    // Pre-descriptor proof (recorded before M1): depot vessel coords
                    // were not captured, so keep the PID-only endpoint shape; the
                    // scheduler resolves the live vessel by pid at dispatch time.
                    origin = new RouteEndpoint
                    {
                        VesselPersistentId = originProof.StartDockedOriginVesselPid,
                        BodyName = originRec.StartBodyName ?? string.Empty,
                        Latitude = 0.0,
                        Longitude = 0.0,
                        Altitude = 0.0,
                        IsSurface = false
                    };
                }
                originLabel =
                    "non-ksc:pid=" + origin.VesselPersistentId.ToString(CultureInfo.InvariantCulture);
            }
            else if (analysis.IsHarvestOrigin && analysis.FirstHarvestWindow != null)
            {
                // M2 harvest origin (plan D7): the run started undocked but
                // every delivered resource was covered by witnessed harvest,
                // so the "origin" is the environment. Build a DISPLAY-ONLY
                // endpoint from the FIRST harvest window's open location
                // (pid 0 - there is no origin vessel to resolve; dispatch
                // eligibility skips origin resolution and the cargo gate).
                RouteHarvestWindow firstWindow = analysis.FirstHarvestWindow;
                origin = new RouteEndpoint
                {
                    VesselPersistentId = 0,
                    BodyName = firstWindow.BodyName ?? string.Empty,
                    Latitude = firstWindow.Latitude,
                    Longitude = firstWindow.Longitude,
                    Altitude = firstWindow.Altitude,
                    IsSurface = IsSurfaceSituation(firstWindow.SituationAtOpen)
                };
                isHarvestOrigin = true;
                originLabel = "harvest";
            }
            else if (hasLoad && !hasDelivery
                && analysis.ConnectionWindow.EndpointAtDock.HasValue)
            {
                // M3 pickup origin (plan D8): the run loaded cargo FROM the dock
                // endpoint with NOTHING delivered (pure pickup, resource AND/OR
                // inventory). The endpoint IS the source - debited at the
                // per-window pickup applier (Phase 3/4 resources, Phase 5
                // inventory), NOT at dispatch and NOT against funds - so the
                // origin is a DISPLAY-ONLY descriptor built from the connection
                // window's pickup endpoint (its pid resolves the live source
                // vessel at debit time; cost manifests stay EMPTY below,
                // mirroring the harvest-origin no-debit shape). A MIXED window
                // (loads AND delivers) keeps its real KSC / docked origin and
                // never lands here (the hasDelivery guard).
                RouteEndpoint pickupEndpoint = analysis.ConnectionWindow.EndpointAtDock.Value;
                origin = new RouteEndpoint
                {
                    VesselPersistentId = pickupEndpoint.VesselPersistentId,
                    BodyName = pickupEndpoint.BodyName ?? string.Empty,
                    Latitude = pickupEndpoint.Latitude,
                    Longitude = pickupEndpoint.Longitude,
                    Altitude = pickupEndpoint.Altitude,
                    IsSurface = pickupEndpoint.IsSurface
                };
                isPickupOrigin = true;
                originLabel =
                    "pickup:pid=" + origin.VesselPersistentId.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                origin = default;
                originLabel = null;
                ParsekLog.Info(Tag,
                    $"BuildRoute rejected: endpoint-missing (origin unresolvable) source={source.RecordingId ?? "<none>"} " +
                    $"originRec={originRec.RecordingId ?? "<none>"} " +
                    $"launchSite={(string.IsNullOrEmpty(originRec.LaunchSiteName) ? "<none>" : originRec.LaunchSiteName)} " +
                    $"startBody={originRec.StartBodyName ?? "<none>"} originProof={(originRec.RouteOriginProof != null ? "yes" : "no")} " +
                    $"harvestOrigin={(analysis.IsHarvestOrigin ? "yes-but-no-window" : "no")} " +
                    $"resourceLoad={(hasResourceLoad ? "yes" : "no")} " +
                    $"inventoryLoad={(hasInventoryLoad ? "yes" : "no")} delivery={(hasDelivery ? "yes" : "no")} " +
                    $"pickupEndpoint={(analysis.ConnectionWindow.EndpointAtDock.HasValue ? "yes" : "no")}");
                return new RouteBuildOutcome { RejectReason = "endpoint-missing" };
            }

            return null;
        }

        /// <summary>
        /// Source-ref derivation (phase extract of <see cref="BuildRoute"/>).
        /// (must-fix #3) Widen the source set to EVERY [root..dock] member recording
        /// so RevalidateSources tracks the whole rendered path, not just the leaf,
        /// then build one <see cref="RouteSourceRef"/> per member ordered
        /// deterministically by TreeOrder then recording id. Writes the parallel
        /// <paramref name="sourceRefs"/> / <paramref name="recordingIds"/> lists.
        /// </summary>
        private static void BuildRouteSourceRefs(
            RecordingTree committedTree,
            Recording source,
            double recordedDockUT,
            double rootLaunchUT,
            out List<RouteSourceRef> sourceRefs,
            out List<string> recordingIds)
        {
            // (must-fix #3) Widen the source set to EVERY [root..dock] member
            // recording so RevalidateSources tracks the whole rendered path, not
            // just the leaf. The member set is the KEPT-intervals' recording ids
            // from the same composition walk RouteBackingMission uses. The leaf
            // (dock child) always stays in the set (it carries the delivery
            // binding) even though it is NOT rendered; fall back to the leaf alone
            // when the walk yields nothing.
            HashSet<string> memberRecordingIds = committedTree != null
                ? RouteBackingMission.ComputeMemberRecordingIds(committedTree, recordedDockUT, rootLaunchUT)
                : new HashSet<string>();
            // The leaf (dock child) is ALWAYS a member: it carries the delivery
            // binding even when the composition walk surfaced the transport via its
            // root through-line head instead of the leaf id.
            if (!string.IsNullOrEmpty(source.RecordingId))
                memberRecordingIds.Add(source.RecordingId);

            // One RouteSourceRef per member recording. Resolve each id to its
            // recording (the leaf falls back to the analysis source), order
            // deterministically by TreeOrder then recording id so save round-trips
            // are stable, and compute each member's proof hash from its own
            // recording.
            sourceRefs = new List<RouteSourceRef>();
            recordingIds = new List<string>();
            var memberRecs = new List<Recording>();
            var seenMembers = new HashSet<string>(StringComparer.Ordinal);
            foreach (string mid in memberRecordingIds)
            {
                Recording memberRec = null;
                if (committedTree?.Recordings != null)
                    committedTree.Recordings.TryGetValue(mid, out memberRec);
                if (memberRec == null && string.Equals(mid, source.RecordingId, StringComparison.Ordinal))
                    memberRec = source;
                if (memberRec != null && !string.IsNullOrEmpty(memberRec.RecordingId)
                    && seenMembers.Add(memberRec.RecordingId))
                    memberRecs.Add(memberRec);
            }
            // Safety net: the leaf source must always carry a ref even if it was
            // absent from both the member set and the tree (e.g. null tree path).
            if (!string.IsNullOrEmpty(source.RecordingId) && seenMembers.Add(source.RecordingId))
                memberRecs.Add(source);
            memberRecs.Sort((a, b) =>
            {
                int byOrder = a.TreeOrder.CompareTo(b.TreeOrder);
                return byOrder != 0
                    ? byOrder
                    : string.Compare(a.RecordingId, b.RecordingId, StringComparison.Ordinal);
            });
            for (int i = 0; i < memberRecs.Count; i++)
            {
                Recording memberRec = memberRecs[i];
                sourceRefs.Add(new RouteSourceRef
                {
                    RecordingId = memberRec.RecordingId,
                    TreeId = memberRec.TreeId,
                    TreeOrder = memberRec.TreeOrder,
                    RecordingFormatVersion = memberRec.RecordingFormatVersion,
                    RecordingSchemaGeneration = memberRec.RecordingSchemaGeneration,
                    SidecarEpoch = memberRec.SidecarEpoch,
                    StartUT = memberRec.StartUT,
                    EndUT = memberRec.EndUT,
                    RouteProofHash = RouteProofHasher.ComputeRouteProofHashFromRecording(memberRec)
                });
                recordingIds.Add(memberRec.RecordingId);
            }
        }

        /// <summary>
        /// Cost-manifest derivation (phase extract of <see cref="BuildRoute"/>).
        /// CostManifest / InventoryCostManifest mirror what each cycle delivers —
        /// items debit what they deliver in v0. Future cost shaping can diverge from
        /// delivery. M2 adjustments (delivery manifests stay untouched in both):
        /// HARVEST origin (plan D7) -> empty cost manifests; PICKUP origin (plan
        /// D6/D8) -> empty cost manifests too (a pure-pickup run delivers nothing,
        /// so its dispatch-time cost is empty, mirroring the harvest no-debit shape;
        /// the loaded cargo debits its physical SOURCE at the per-window pickup
        /// applier, never funds and never the dispatch-time origin cost); DOCKED
        /// origin with harvest data (plan D8) -> reduce each delivered resource's
        /// debit basis by its witnessed harvested amount, removing entries that
        /// reduce to zero. Writes the <paramref name="costManifest"/> /
        /// <paramref name="inventoryCostManifest"/> out params.
        /// </summary>
        private static void BuildRouteCostManifests(
            RouteAnalysisResult analysis,
            bool isHarvestOrigin,
            bool isPickupOrigin,
            bool isKscOrigin,
            CultureInfo ic,
            out Dictionary<string, double> costManifest,
            out List<InventoryPayloadItem> inventoryCostManifest)
        {
            if (isHarvestOrigin || isPickupOrigin)
            {
                costManifest = new Dictionary<string, double>();
                inventoryCostManifest = new List<InventoryPayloadItem>();
            }
            else
            {
                costManifest = analysis.ResourceDeliveryManifest != null
                    ? new Dictionary<string, double>(analysis.ResourceDeliveryManifest)
                    : new Dictionary<string, double>();
                inventoryCostManifest = analysis.InventoryDeliveryManifest != null
                    ? new List<InventoryPayloadItem>(analysis.InventoryDeliveryManifest)
                    : new List<InventoryPayloadItem>();

                // D8: non-KSC docked origin only - KSC CostManifest semantics
                // stay unchanged (the funds basis is OQ1's job, Phase 6).
                if (!isKscOrigin && analysis.HarvestedManifest != null
                    && analysis.HarvestedManifest.Count > 0)
                {
                    ReduceCostManifestByHarvested(
                        costManifest, analysis.HarvestedManifest, ic);
                }
            }
        }

        /// <summary>
        /// Built-route summary log (phase extract of <see cref="BuildRoute"/>); the
        /// final summary <see cref="ParsekLog.Info"/> (anchor-stop headline)
        /// verbatim, plus the M4a multi-stop per-stop summary line (emitted only for
        /// a multi-stop route, so the single-stop common case logs unchanged).
        /// </summary>
        private static void LogBuiltRoute(
            string routeId,
            string originLabel,
            RouteEndpoint origin,
            Recording source,
            double rootLaunchUT,
            double undockUT,
            double recordedDockUT,
            double transitDuration,
            double dispatchInterval,
            int cadenceMultiplier,
            List<string> recordingIds,
            HashSet<string> excludedIntervalKeys,
            HashSet<string> creationTreeRecordingIds,
            List<RouteStop> stops,
            bool isMultiStop,
            Game.Modes mode,
            CultureInfo ic)
        {
            string shortId = !string.IsNullOrEmpty(routeId) && routeId.Length > 8
                ? routeId.Substring(0, 8)
                : routeId ?? "<no-id>";
            // The stop-* counts report the ANCHOR (first/min-DockUT) stop, the same
            // tokens the single-stop build logged (byte-identical anchor; the
            // multi-stop per-stop breakdown is the separate summary line below).
            RouteStop anchorStop = stops[0];
            int stopResources = anchorStop.DeliveryManifest != null ? anchorStop.DeliveryManifest.Count : 0;
            int stopInventory = anchorStop.InventoryDeliveryManifest != null
                ? anchorStop.InventoryDeliveryManifest.Count
                : 0;
            // M3: per-stop pickup-direction count (resource load manifest size).
            int stopPickup = anchorStop.PickupManifest != null ? anchorStop.PickupManifest.Count : 0;
            int stopInventoryPickup = anchorStop.InventoryPickupManifest != null
                ? anchorStop.InventoryPickupManifest.Count
                : 0;
            ParsekLog.Info(Tag,
                $"Built route id={shortId} origin={originLabel} " +
                $"originSurface={(origin.IsSurface ? "1" : "0")} " +
                $"originLat={origin.Latitude.ToString("R", ic)} " +
                $"originLon={origin.Longitude.ToString("R", ic)} " +
                $"originAlt={origin.Altitude.ToString("R", ic)} " +
                $"tree={source.TreeId ?? "<none>"} " +
                $"rootLaunchUT={rootLaunchUT.ToString("R", ic)} " +
                $"undockUT={undockUT.ToString("R", ic)} " +
                $"dockUT={recordedDockUT.ToString("R", ic)} " +
                $"span={transitDuration.ToString("R", ic)} " +
                $"interval={dispatchInterval.ToString("R", ic)} " +
                $"cadenceN={cadenceMultiplier.ToString(ic)} " +
                $"members={recordingIds.Count.ToString(ic)} " +
                $"excluded={excludedIntervalKeys.Count.ToString(ic)} " +
                $"creationTreeRecordings={creationTreeRecordingIds.Count.ToString(ic)} " +
                $"stops={stops.Count.ToString(ic)} " +
                $"stop-resources={stopResources.ToString(ic)} " +
                $"stop-inventory={stopInventory.ToString(ic)} " +
                $"stop-pickup={stopPickup.ToString(ic)} " +
                $"stop-inventory-pickup={stopInventoryPickup.ToString(ic)} " +
                $"mode={mode}");

            // M4a (plan D4): one-line per-stop summary for a multi-stop route
            // (stop count + each stop's dock UT + endpoint pid/body). Single-stop
            // routes skip this (the headline line already carries the anchor's
            // stop-* counts) so the common-case log stays unchanged.
            if (isMultiStop)
            {
                var stopDetails = new List<string>(stops.Count);
                for (int i = 0; i < stops.Count; i++)
                {
                    RouteStop s = stops[i];
                    stopDetails.Add(
                        $"#{i.ToString(ic)}:dockUT={s.RecordedDockUT.ToString("R", ic)}" +
                        $",seg={s.SegmentIndexBefore.ToString(ic)}" +
                        $",offset={s.DeliveryOffsetSeconds.ToString("R", ic)}" +
                        $",pid={s.Endpoint.VesselPersistentId.ToString(ic)}" +
                        $",body={(string.IsNullOrEmpty(s.Endpoint.BodyName) ? "<none>" : s.Endpoint.BodyName)}" +
                        $",deliver={(s.DeliveryManifest != null ? s.DeliveryManifest.Count : 0).ToString(ic)}" +
                        $",pickup={(s.PickupManifest != null ? s.PickupManifest.Count : 0).ToString(ic)}");
                }
                ParsekLog.Info(Tag,
                    $"Built multi-stop route id={shortId} stops={stops.Count.ToString(ic)} " +
                    "[" + string.Join(" ", stopDetails) + "]");
            }
        }

        /// <summary>
        /// D8 debit-basis reduction (M2): per delivered resource,
        /// <c>cost[r] = max(0, delivery[r] - harvested[r])</c>; entries that
        /// reduce to zero are REMOVED from the manifest, not kept as zero
        /// (review finding 16: matches the sparse-codec conventions and keeps
        /// the OriginHasCargo short-resource naming clean). "Zero" uses
        /// <see cref="RouteHarvestAnalysis.GainEpsilon"/> - a fully-harvested
        /// delivery whose subtraction leaves double-rounding residue (1e-13
        /// scale) must remove the entry, not keep a float-residue debit that
        /// would still gate OriginHasCargo on a resource the depot never owes.
        /// Mutates <paramref name="costManifest"/> in place and logs the
        /// reduction.
        /// </summary>
        internal static void ReduceCostManifestByHarvested(
            Dictionary<string, double> costManifest,
            Dictionary<string, double> harvestedManifest,
            CultureInfo ic)
        {
            if (costManifest == null || costManifest.Count == 0
                || harvestedManifest == null || harvestedManifest.Count == 0)
                return;

            var reductions = new List<string>();
            var names = new List<string>(costManifest.Keys);
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                if (!harvestedManifest.TryGetValue(name, out double harvested)
                    || harvested <= 0.0)
                    continue;

                double before = costManifest[name];
                double after = before - harvested;
                if (after > RouteHarvestAnalysis.GainEpsilon)
                {
                    costManifest[name] = after;
                    reductions.Add(
                        $"{name}:{before.ToString("R", ic)}->{after.ToString("R", ic)}");
                }
                else
                {
                    costManifest.Remove(name);
                    reductions.Add($"{name}:{before.ToString("R", ic)}->removed");
                }
            }

            if (reductions.Count > 0)
            {
                ParsekLog.Info(Tag,
                    "BuildRoute: costManifestReducedByHarvest=[" +
                    string.Join(",", reductions) + "]");
            }
        }

        /// <summary>
        /// True for the surface situations (<c>Vessel.Situations</c> LANDED=1,
        /// SPLASHED=2, PRELAUNCH=4). Used to classify the harvest-origin
        /// display endpoint from <c>RouteHarvestWindow.SituationAtOpen</c>;
        /// -1 (unknown) and the flight/orbit situations read as non-surface.
        /// </summary>
        internal static bool IsSurfaceSituation(int situation)
        {
            return situation > 0 && (situation & 0x7) != 0;
        }

        /// <summary>
        /// (CRE-5) True when the recorded dock UT lies STRICTLY inside the rendered
        /// <c>[rootLaunchUT .. undockUT]</c> span (dock after launch, before undock).
        /// Strict on both ends because the dock is a mid-flight event: a dock at the
        /// exact launch or undock instant is a degenerate window the loop clock
        /// cannot deliver from. Any NaN / infinity input is rejected. This is a
        /// fail-fast subset of <c>RouteLoopClock.IsDockUTInSpan</c>'s inclusive
        /// <c>[SpanStartUT, SpanEndUT]</c> check, so every dock this accepts the
        /// clock also accepts.
        /// </summary>
        internal static bool IsDockUTWithinSpan(double dockUT, double rootLaunchUT, double undockUT)
        {
            if (double.IsNaN(dockUT) || double.IsInfinity(dockUT)
                || double.IsNaN(rootLaunchUT) || double.IsInfinity(rootLaunchUT)
                || double.IsNaN(undockUT) || double.IsInfinity(undockUT))
            {
                return false;
            }
            return dockUT > rootLaunchUT && dockUT < undockUT;
        }

        /// <summary>
        /// M4a (plan D4): resolve the ordered per-stop analysis collection. A1
        /// fills <see cref="RouteAnalysisResult.Stops"/> ascending by DockUT for
        /// every Eligible production result. When the list is null/empty (the
        /// pure-logic RouteBuilder tests construct results with only the scalar
        /// fields), synthesize a SINGLE stop mirroring the scalar
        /// <c>ConnectionWindow</c> + manifests, so a single-window analysis builds
        /// the same byte-identical single-stop route as the pre-A2 code. Always
        /// returns at least one entry (the caller has already passed the
        /// <see cref="RouteAnalysisResult.IsEligible"/> + non-null-source gates,
        /// and the scalar <c>ConnectionWindow</c> is the eligibility proof).
        /// </summary>
        internal static List<RouteAnalysisStop> ResolveAnalysisStops(RouteAnalysisResult analysis)
        {
            if (analysis.Stops != null && analysis.Stops.Count > 0)
                return analysis.Stops;

            // A2 review fold: greppable trace for the scalar-fallback collapse.
            // Production AnalyzeWindows always sets a non-empty Stops on an
            // Eligible result, so this branch is normally test-only; if it ever
            // fires in production it means an Eligible result reached the builder
            // with no per-stop list, which would silently build a single anchor
            // stop. Logging it leaves a trace instead of a silent collapse.
            ParsekLog.Verbose(Tag,
                "RouteBuilder: analysis.Stops empty - synthesizing one stop from scalar fields "
                + "(expected only for scalar-only analysis results / tests).");

            return new List<RouteAnalysisStop>
            {
                new RouteAnalysisStop
                {
                    ConnectionWindow = analysis.ConnectionWindow,
                    ResourceDeliveryManifest = analysis.ResourceDeliveryManifest,
                    InventoryDeliveryManifest = analysis.InventoryDeliveryManifest,
                    ResourceLoadManifest = analysis.ResourceLoadManifest,
                    InventoryLoadManifest = analysis.InventoryLoadManifest,
                    EndpointAtDock = analysis.ConnectionWindow != null
                            && analysis.ConnectionWindow.EndpointAtDock.HasValue
                        ? analysis.ConnectionWindow.EndpointAtDock.Value
                        : default(RouteEndpoint),
                    DockUT = analysis.ConnectionWindow != null
                        ? analysis.ConnectionWindow.DockUT
                        : double.NaN,
                    SourceRecording = analysis.SourceRecording
                }
            };
        }

        /// <summary>
        /// M4a (plan D4): best-effort 0-based index of a stop's source recording
        /// in the route's ordered <paramref name="recordingIds"/> (the member set,
        /// sorted by TreeOrder then id). The index is the member-recording whose
        /// completion triggers the stop's firing window. Returns <c>-1</c> when the
        /// source recording id is missing or absent from the member set (the firing
        /// path still resolves the stop by DockUT, A3; this is the codec / display
        /// projection only).
        /// </summary>
        internal static int ResolveSegmentIndexBefore(
            List<string> recordingIds, Recording sourceRecording)
        {
            if (recordingIds == null || sourceRecording == null
                || string.IsNullOrEmpty(sourceRecording.RecordingId))
                return -1;
            for (int i = 0; i < recordingIds.Count; i++)
            {
                if (string.Equals(recordingIds[i], sourceRecording.RecordingId, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        private static string DefaultIdFactory()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
