using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Value formatters: candidate transit, origins and endpoints, cadence, durations, stop names and manifests.
    internal partial class LogisticsWindowUI
    {
        private static double CandidateTransit(RouteCandidate candidate)
        {
            // CRE-2: the player-shown transit must match the route's ACTUAL span,
            // which the builder computes as [root.StartUT .. undockUT] (the full
            // rendered path), NOT the leaf dock-child span (src.EndUT - src.StartUT).
            // Reuse the single span helper so display and creation never diverge.
            if (candidate?.Analysis == null) return 0.0;
            return RouteCreationDialog.ComputeRootToUndockSpan(candidate.Analysis, candidate.Tree);
        }

        private static string FormatOrigin(Route route)
        {
            if (route.IsKscOrigin) return "KSC";
            // M2 harvest origin (plan D7): no origin vessel, nothing debited.
            if (route.IsHarvestOrigin) return "harvested en route";
            if (route.Origin.VesselPersistentId != 0u || !string.IsNullOrEmpty(route.Origin.BodyName))
                return FormatEndpointShort(route.Origin);
            return "-";
        }

        internal static string FormatCandidateOrigin(RouteAnalysisResult analysis, RecordingTree tree)
        {
            // Resolve origin off the tree ROOT (the launch) via the shared helper,
            // not the dock-child source: the source started mid-flight at the dock
            // and has no LaunchSiteName, so reading it directly mis-reports a KSC
            // route as "-". Keeps this cell in step with the dialog summary, the
            // default name, and the built Route.IsKscOrigin.
            RouteCreationFormatters.RouteOriginIdentity id =
                RouteCreationFormatters.ResolveOriginIdentity(analysis, tree);
            switch (id.Kind)
            {
                case RouteCreationFormatters.RouteOriginKind.Ksc:
                    return "KSC";
                case RouteCreationFormatters.RouteOriginKind.Depot:
                    return "depot " + RouteCreationFormatters.FormatDepotIdentity(id);
                case RouteCreationFormatters.RouteOriginKind.Harvest:
                    return "harvested en route";
                default:
                    return "-";
            }
        }

        /// <summary>
        /// The candidate from/to line's origin, in the route rows' short form
        /// (<see cref="LogisticsRoutePresentation.FormatOriginShort"/>): "KSC", the depot's
        /// name, "Harvested", or "-" when the origin is unknown.
        /// </summary>
        internal static string FormatCandidateOriginShort(RouteAnalysisResult analysis, RecordingTree tree)
        {
            RouteCreationFormatters.RouteOriginIdentity id =
                RouteCreationFormatters.ResolveOriginIdentity(analysis, tree);
            switch (id.Kind)
            {
                case RouteCreationFormatters.RouteOriginKind.Ksc:
                    return LogisticsRoutePresentation.FormatOriginShort(true, false, null);
                case RouteCreationFormatters.RouteOriginKind.Harvest:
                    return LogisticsRoutePresentation.FormatOriginShort(false, true, null);
                case RouteCreationFormatters.RouteOriginKind.Depot:
                    return LogisticsRoutePresentation.FormatOriginShort(false, false,
                        RouteCreationFormatters.FormatDepotIdentity(id));
                default:
                    return "-";
            }
        }

        private static string FormatEndpointShort(RouteEndpoint? ep)
        {
            if (!ep.HasValue) return "-";
            return FormatEndpointShort(ep.Value);
        }

        /// <summary>
        /// A place name without coordinates for the candidate Destination cell:
        /// "Kerbin (surface)" / "Mun (orbit)"; the coordinates ride the hover
        /// (<see cref="FormatEndpointShort(RouteEndpoint)"/>). "-" when unknown.
        /// </summary>
        internal static string FormatEndpointPlace(RouteEndpoint? ep)
        {
            return RouteCreationFormatters.FormatEndpointPlace(ep);
        }

        private static string FormatEndpointShort(RouteEndpoint ep)
        {
            if (string.IsNullOrEmpty(ep.BodyName)) return "-";
            string sit = ep.IsSurface ? "surface" : "orbit";
            return string.Format(CultureInfo.InvariantCulture,
                "{0} ({1}) {2:F2},{3:F2}",
                ep.BodyName, sit, ep.Latitude, ep.Longitude);
        }

        // "Nx (~human)" — the cadence multiplier plus the resulting dispatch
        // interval as a human duration (Phase 6). When the span is unknown the
        // duration falls back to a dash from FormatDuration.
        internal static string FormatCadence(Route route)
        {
            int n = Route.ClampCadenceMultiplier(route.CadenceMultiplier);
            string human = FormatDuration(route.DispatchInterval);
            return string.Format(CultureInfo.InvariantCulture, LogisticsRoutePresentation.CadenceReadoutFormat, n, human);
        }

        internal static string FormatDuration(double seconds)
        {
            if (seconds <= 0.0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
                return "-";
            if (seconds < 60.0)
                return string.Format(CultureInfo.InvariantCulture, "{0:F0}s", seconds);
            if (seconds < 3600.0)
                return string.Format(CultureInfo.InvariantCulture, "{0:F1}m", seconds / 60.0);
            // The day length follows the player's calendar setting (Kerbin 6 h or Earth 24 h),
            // and RouteCadence.ParseAndSnapInterval reads a typed "d" with the same length, so
            // the displayed interval round-trips through the edit field on either calendar.
            double secsPerDay = ParsekTimeFormat.SecsPerDay;
            if (seconds < secsPerDay)
                return string.Format(CultureInfo.InvariantCulture, "{0:F1}h", seconds / 3600.0);
            return string.Format(CultureInfo.InvariantCulture, "{0:F1}d", seconds / secsPerDay);
        }

        // The place each stop names in the cargo cell and line: a single stop reads the
        // destination the from/to line uses; a multi-stop route each stop's resolved
        // vessel name, or its place ("Kerbin (surface)") when the stop did not resolve,
        // never raw coordinates.
        private static string[] ResolveStopNames(Route route, string singleStopName,
            Vessel[] stopVessels, string[] stopTexts)
        {
            int count = route?.Stops?.Count ?? 0;
            var names = new string[count];
            if (count == 1)
            {
                names[0] = singleStopName;
                return names;
            }
            for (int i = 0; i < count; i++)
            {
                RouteStop stop = route.Stops[i];
                if (stop == null) continue;
                bool resolved = stopVessels != null && i < stopVessels.Length && stopVessels[i] != null;
                names[i] = resolved && stopTexts != null && i < stopTexts.Length
                    ? stopTexts[i]
                    : FormatEndpointPlace(stop.Endpoint);
            }
            return names;
        }

        // L3: the manifest formatting moved to the pure
        // LogisticsDeliveryPresentation.FormatWouldDeliver so the Candidates section
        // "Would deliver" cell and the route / candidate detail lines share one
        // unit-tested formatter. This thin window-side forwarder keeps the existing
        // callers (route delivery, candidate detail) on the same text.
        private static string FormatManifest(
            Dictionary<string, double> resources,
            List<InventoryPayloadItem> inventory)
        {
            return LogisticsDeliveryPresentation.FormatWouldDeliver(resources, inventory);
        }
    }
}
