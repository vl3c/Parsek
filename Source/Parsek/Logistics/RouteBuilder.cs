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
            // The route segment renders [root launch .. DOCK] (playtest follow-up):
            // rendering STOPS at the docking moment, so the docked-together combined
            // vessel (the dock-merged child recording, which spans dock..undock) is
            // NOT rendered. The dock is a clean recording boundary (the transport's
            // solo recording ends at the couple; the merged child starts there), so
            // trimming the segment end to the dock excludes the combined vessel
            // exactly. The UNDOCK UT is kept only for the window-sanity check + logs.
            double undockUT = analysis.ConnectionWindow != null
                ? analysis.ConnectionWindow.UndockUT
                : double.NaN;
            double recordedDockUT = analysis.ConnectionWindow != null
                ? analysis.ConnectionWindow.DockUT
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

            // Excluded interval keys for the backing-mission render trim. End at the
            // DOCK so the docked-together combined vessel (the merged child, which
            // starts at the dock) and everything after it are excluded from render.
            HashSet<string> excludedIntervalKeys = committedTree != null
                ? RouteBackingMission.ComputeExcludedIntervalKeys(committedTree, recordedDockUT, rootLaunchUT)
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
                : RouteCreationFormatters.GenerateDefaultRouteName(analysis, committedTree);

            // CostManifest / InventoryCostManifest mirror what each cycle
            // delivers — items debit what they deliver in v0. Future cost
            // shaping can diverge from delivery.
            var costManifest = analysis.ResourceDeliveryManifest != null
                ? new Dictionary<string, double>(analysis.ResourceDeliveryManifest)
                : new Dictionary<string, double>();
            var inventoryCostManifest = analysis.InventoryDeliveryManifest != null
                ? new List<InventoryPayloadItem>(analysis.InventoryDeliveryManifest)
                : new List<InventoryPayloadItem>();

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
                $"cadenceN={cadenceMultiplier.ToString(ic)} " +
                $"members={recordingIds.Count.ToString(ic)} " +
                $"excluded={excludedIntervalKeys.Count.ToString(ic)} " +
                $"stop-resources={stopResources.ToString(ic)} " +
                $"stop-inventory={stopInventory.ToString(ic)} " +
                $"mode={mode}");

            return new RouteBuildOutcome { Route = route };
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

        private static string DefaultIdFactory()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
