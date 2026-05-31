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

            // --- Backing-mission geometry (design §0; Phase 5). ---------------
            // The route renders as a looped Mission segment over the source tree's
            // [root.StartUT .. undockUT] path, NOT the leaf-only dock-child span.
            // Resolve the ROOT launch UT (NOT source.StartUT, which is the
            // mid-flight dock child) and the UNDOCK UT (from the connection
            // window). RouteBackingMission owns the interval-key + member-set
            // derivation (the geometry seam over the locked Missions composition).
            double rootLaunchUT = rootRec != null ? rootRec.StartUT : source.StartUT;
            double undockUT = analysis.ConnectionWindow != null
                ? analysis.ConnectionWindow.UndockUT
                : double.NaN;

            // (must-fix #3) Transit duration is the RENDERED span (undock - root
            // launch), not the leaf-only source.EndUT - source.StartUT. Also the
            // clamp reference for the DispatchInterval >= span pin below.
            double transitDuration = undockUT - rootLaunchUT;

            // Backing-mission-unresolvable reject: the [launch..undock] window must
            // be finite and non-empty for the loop render + clock to work.
            if (double.IsNaN(undockUT) || double.IsInfinity(undockUT)
                || double.IsNaN(rootLaunchUT) || double.IsInfinity(rootLaunchUT)
                || transitDuration <= 0.0)
            {
                ParsekLog.Info(Tag,
                    $"BuildRoute rejected: backing-mission-unresolvable source={source.RecordingId ?? "<none>"} " +
                    $"tree={(committedTree != null ? committedTree.Id ?? "<none>" : "<null>")} " +
                    $"rootLaunchUT={rootLaunchUT.ToString("R", ic)} undockUT={undockUT.ToString("R", ic)} " +
                    $"span={transitDuration.ToString("R", ic)}");
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

            // (must-fix #3) Widen the source set to EVERY [root..undock] member
            // recording so RevalidateSources tracks the whole rendered path, not
            // just the leaf. The member set is the KEPT-intervals' recording ids
            // from the same composition walk RouteBackingMission uses. The leaf
            // (dock child) always stays in the set (it carries the delivery
            // binding); fall back to the leaf alone when the walk yields nothing.
            HashSet<string> memberRecordingIds = committedTree != null
                ? RouteBackingMission.ComputeMemberRecordingIds(committedTree, undockUT, rootLaunchUT)
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
            var sourceRefs = new List<RouteSourceRef>();
            var recordingIds = new List<string>();
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

            // Excluded interval keys for the backing-mission render trim.
            HashSet<string> excludedIntervalKeys = committedTree != null
                ? RouteBackingMission.ComputeExcludedIntervalKeys(committedTree, undockUT, rootLaunchUT)
                : new HashSet<string>();

            // Origin discovery. KSC-origin if recording carries a launch site
            // name AND was launched from Kerbin. Otherwise non-KSC origin
            // requires RouteOriginProof.StartDockedOriginVesselPid != 0.
            // Endpoint coords default to zero for the launch-site path — the
            // scheduler resolves real coords from the launch-site name; for
            // the non-KSC path we cannot recover the origin vessel's
            // body-fixed position here in v0, so leave zero with a comment.
            bool isKscOrigin =
                !string.IsNullOrEmpty(originRec.LaunchSiteName)
                && string.Equals(originRec.StartBodyName, "Kerbin", StringComparison.Ordinal);

            RouteEndpoint origin;
            string originLabel;
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
                origin = new RouteEndpoint
                {
                    VesselPersistentId = originRec.RouteOriginProof.StartDockedOriginVesselPid,
                    BodyName = originRec.StartBodyName ?? string.Empty,
                    // v0: depot vessel coords are not captured in the origin
                    // proof; scheduler resolves them from the live vessel at
                    // dispatch time. Leave zero so the saved data is honest.
                    Latitude = 0.0,
                    Longitude = 0.0,
                    Altitude = 0.0,
                    IsSurface = false
                };
                originLabel =
                    "non-ksc:pid=" + origin.VesselPersistentId.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                ParsekLog.Info(Tag,
                    $"BuildRoute rejected: endpoint-missing (origin unresolvable) source={source.RecordingId ?? "<none>"} " +
                    $"originRec={originRec.RecordingId ?? "<none>"} " +
                    $"launchSite={(string.IsNullOrEmpty(originRec.LaunchSiteName) ? "<none>" : originRec.LaunchSiteName)} " +
                    $"startBody={originRec.StartBodyName ?? "<none>"} originProof={(originRec.RouteOriginProof != null ? "yes" : "no")}");
                return new RouteBuildOutcome { RejectReason = "endpoint-missing" };
            }

            // Single stop (v0). Defensively copy manifests so later store
            // mutations don't reach back into the analysis result.
            var stop = new RouteStop
            {
                Endpoint = analysis.ConnectionWindow.EndpointAtDock.Value,
                ConnectionKind = analysis.ConnectionWindow.TransferKind,
                DeliveryManifest = analysis.ResourceDeliveryManifest != null
                    ? new Dictionary<string, double>(analysis.ResourceDeliveryManifest)
                    : new Dictionary<string, double>(),
                InventoryDeliveryManifest = analysis.InventoryDeliveryManifest != null
                    ? new List<InventoryPayloadItem>(analysis.InventoryDeliveryManifest)
                    : new List<InventoryPayloadItem>(),
                SegmentIndexBefore = 0,
                DeliveryOffsetSeconds = 0.0
            };

            string routeId = (idFactory ?? DefaultIdFactory)();
            string routeName = !string.IsNullOrEmpty(inputs.Name)
                ? inputs.Name
                : RouteCreationFormatters.GenerateDefaultRouteName(analysis);

            // CostManifest / InventoryCostManifest mirror what each cycle
            // delivers — items debit what they deliver in v0. Future cost
            // shaping can diverge from delivery.
            var costManifest = analysis.ResourceDeliveryManifest != null
                ? new Dictionary<string, double>(analysis.ResourceDeliveryManifest)
                : new Dictionary<string, double>();
            var inventoryCostManifest = analysis.InventoryDeliveryManifest != null
                ? new List<InventoryPayloadItem>(analysis.InventoryDeliveryManifest)
                : new List<InventoryPayloadItem>();

            // Loop-clock dock binding: the leaf (dock child) source carries the
            // recorded dock UT the loop clock crosses each cycle to fire delivery
            // (Phase 4). LoopAnchorUT is set here only when the route is created
            // ACTIVE (the dialog path); the Paused->Activate path sets it in
            // RouteOrchestrator.TryActivate. The builder floors nothing — the loop
            // builder floors the anchor to spanEnd, so this value is diagnostic.
            double recordedDockUT = analysis.ConnectionWindow != null
                ? analysis.ConnectionWindow.DockUT
                : double.NaN;
            double loopAnchorUT = initialStatus == RouteStatus.Active ? rootLaunchUT : -1.0;

            var route = new Route
            {
                Id = routeId,
                Name = routeName,
                Status = initialStatus,
                RecordingIds = recordingIds,
                SourceRefs = sourceRefs,
                Origin = origin,
                IsKscOrigin = isKscOrigin,
                Stops = new List<RouteStop> { stop },
                TransitDuration = transitDuration,
                DispatchInterval = dispatchInterval,
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
                RecordedDockUT = recordedDockUT,
                DockMemberRecordingId = source.RecordingId,
                LoopAnchorUT = loopAnchorUT,
                LastObservedLoopCycleIndex = -1
            };

            string shortId = !string.IsNullOrEmpty(routeId) && routeId.Length > 8
                ? routeId.Substring(0, 8)
                : routeId ?? "<no-id>";
            int stopResources = stop.DeliveryManifest != null ? stop.DeliveryManifest.Count : 0;
            int stopInventory = stop.InventoryDeliveryManifest != null
                ? stop.InventoryDeliveryManifest.Count
                : 0;
            ParsekLog.Info(Tag,
                $"Built route id={shortId} origin={originLabel} " +
                $"tree={source.TreeId ?? "<none>"} " +
                $"rootLaunchUT={rootLaunchUT.ToString("R", ic)} " +
                $"undockUT={undockUT.ToString("R", ic)} " +
                $"dockUT={recordedDockUT.ToString("R", ic)} " +
                $"span={transitDuration.ToString("R", ic)} " +
                $"interval={dispatchInterval.ToString("R", ic)} " +
                $"members={recordingIds.Count.ToString(ic)} " +
                $"excluded={excludedIntervalKeys.Count.ToString(ic)} " +
                $"stop-resources={stopResources.ToString(ic)} " +
                $"stop-inventory={stopInventory.ToString(ic)} " +
                $"mode={mode}");

            return new RouteBuildOutcome { Route = route };
        }

        private static string DefaultIdFactory()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
