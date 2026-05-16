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
            Func<string> idFactory = null)
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

            // v0 single-recording transit duration: leaf EndUT - root StartUT.
            // The analysis surface only carries one source recording; future
            // multi-recording paths will walk committedTree's active path here.
            double transitDuration = source.EndUT - source.StartUT;

            if (inputs.DispatchIntervalSeconds <= 0.0)
            {
                ParsekLog.Info(Tag,
                    $"BuildRoute rejected: interval-invalid interval={inputs.DispatchIntervalSeconds.ToString("R", CultureInfo.InvariantCulture)}");
                return new RouteBuildOutcome { RejectReason = "interval-invalid" };
            }
            if (inputs.DispatchIntervalSeconds < transitDuration)
            {
                ParsekLog.Info(Tag,
                    $"BuildRoute rejected: interval-below-transit interval={inputs.DispatchIntervalSeconds.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"transit={transitDuration.ToString("R", CultureInfo.InvariantCulture)}");
                return new RouteBuildOutcome { RejectReason = "interval-below-transit" };
            }

            // Source ref capture — v0 has exactly one source recording.
            var sourceRefs = new List<RouteSourceRef>
            {
                new RouteSourceRef
                {
                    RecordingId = source.RecordingId,
                    TreeId = source.TreeId,
                    TreeOrder = source.TreeOrder,
                    RecordingFormatVersion = source.RecordingFormatVersion,
                    RecordingSchemaGeneration = source.RecordingSchemaGeneration,
                    SidecarEpoch = source.SidecarEpoch,
                    StartUT = source.StartUT,
                    EndUT = source.EndUT,
                    RouteProofHash = RouteStore.ComputeRouteProofHashFromRecording(source)
                }
            };

            // Origin discovery. KSC-origin if recording carries a launch site
            // name AND was launched from Kerbin. Otherwise non-KSC origin
            // requires RouteOriginProof.StartDockedOriginVesselPid != 0.
            // Endpoint coords default to zero for the launch-site path — the
            // scheduler resolves real coords from the launch-site name; for
            // the non-KSC path we cannot recover the origin vessel's
            // body-fixed position here in v0, so leave zero with a comment.
            bool isKscOrigin =
                !string.IsNullOrEmpty(source.LaunchSiteName)
                && string.Equals(source.StartBodyName, "Kerbin", StringComparison.Ordinal);

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
            else if (source.RouteOriginProof != null
                && source.RouteOriginProof.StartDockedOriginVesselPid != 0)
            {
                origin = new RouteEndpoint
                {
                    VesselPersistentId = source.RouteOriginProof.StartDockedOriginVesselPid,
                    BodyName = source.StartBodyName ?? string.Empty,
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
                    $"BuildRoute rejected: endpoint-missing source={source.RecordingId ?? "<none>"} " +
                    $"launchSite={(string.IsNullOrEmpty(source.LaunchSiteName) ? "<none>" : source.LaunchSiteName)} " +
                    $"startBody={source.StartBodyName ?? "<none>"} originProof={(source.RouteOriginProof != null ? "yes" : "no")}");
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

            var route = new Route
            {
                Id = routeId,
                Name = routeName,
                Status = RouteStatus.Active,
                RecordingIds = new List<string> { source.RecordingId },
                SourceRefs = sourceRefs,
                Origin = origin,
                IsKscOrigin = isKscOrigin,
                Stops = new List<RouteStop> { stop },
                TransitDuration = transitDuration,
                DispatchInterval = inputs.DispatchIntervalSeconds,
                DispatchWindowEpochUT = source.StartUT,
                DispatchWindowPeriod = 0.0,
                // Placeholder until scheduler (Phase 6+) computes from epoch + interval.
                NextDispatchUT = source.StartUT + inputs.DispatchIntervalSeconds,
                KscDispatchFundsCost = 0.0,
                CostManifest = costManifest,
                InventoryCostManifest = inventoryCostManifest,
                PauseAfterCurrentCycle = false,
                CompletedCycles = 0,
                SkippedCycles = 0,
                LinkedRouteId = null,
                CurrentSegmentIndex = -1,
                PendingStopIndex = -1
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
                $"stop-resources={stopResources.ToString(CultureInfo.InvariantCulture)} " +
                $"stop-inventory={stopInventory.ToString(CultureInfo.InvariantCulture)} " +
                $"transit={transitDuration.ToString("R", CultureInfo.InvariantCulture)} " +
                $"interval={inputs.DispatchIntervalSeconds.ToString("R", CultureInfo.InvariantCulture)} " +
                $"mode={mode}");

            return new RouteBuildOutcome { Route = route };
        }

        private static string DefaultIdFactory()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
