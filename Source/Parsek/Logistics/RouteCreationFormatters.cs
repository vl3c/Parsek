using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.Logistics
{
    /// <summary>
    /// Pure string formatters used by the route-creation dialog. Every method
    /// is locale-stable (<see cref="CultureInfo.InvariantCulture"/>), so the
    /// dialog renders consistently on comma-locale systems.
    /// </summary>
    internal static class RouteCreationFormatters
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Format a resource line for the dialog body. Returns
        /// <c>"&lt;name&gt;: &lt;amount&gt;"</c> with one fractional digit, e.g.
        /// <c>"LiquidFuel: 150.0"</c>.
        /// </summary>
        internal static string FormatResourceLine(string name, double amount)
        {
            string displayName = string.IsNullOrEmpty(name) ? "<unknown>" : name;
            return displayName + ": " + amount.ToString("F1", IC);
        }

        /// <summary>
        /// Format an inventory line. Quantity > 1 emits a "<c>xN</c>" suffix;
        /// quantity 1 omits the multiplier. Non-empty variant names render in
        /// parens, e.g. <c>"evaJetpack (white) x2"</c>.
        /// </summary>
        internal static string FormatInventoryLine(InventoryPayloadItem item)
        {
            if (item == null) return "<null>";

            string partLabel = string.IsNullOrEmpty(item.PartName) ? "<unknown>" : item.PartName;
            string variant = item.VariantName;
            if (!string.IsNullOrEmpty(variant))
                partLabel = partLabel + " (" + variant + ")";

            if (item.Quantity > 1)
                return partLabel + " x" + item.Quantity.ToString(IC);
            return partLabel;
        }

        /// <summary>
        /// Format a <see cref="RouteEndpoint"/> as
        /// <c>"&lt;body&gt; (lat°, lon°, altm)"</c>. Empty body falls back to
        /// <c>"&lt;unknown&gt;"</c>.
        /// </summary>
        internal static string FormatEndpoint(RouteEndpoint ep)
        {
            string body = string.IsNullOrEmpty(ep.BodyName) ? "<unknown>" : ep.BodyName;
            string lat = ep.Latitude.ToString("F3", IC);
            string lon = ep.Longitude.ToString("F3", IC);
            string alt = ep.Altitude.ToString("F0", IC);
            return body + " (" + lat + "°, " + lon + "°, " + alt + "m)";
        }

        /// <summary>
        /// Human-readable transit time. Delegates to
        /// <see cref="ParsekTimeFormat.FormatDuration"/> so calendar settings
        /// are respected (Kerbin vs. Earth).
        /// </summary>
        internal static string FormatTransitTime(double seconds)
        {
            return ParsekTimeFormat.FormatDuration(seconds);
        }

        /// <summary>
        /// Map a non-eligible <see cref="RouteAnalysisStatus"/> to player-facing
        /// reject text. <see cref="RouteAnalysisStatus.Eligible"/> returns an
        /// empty string - callers should not invoke this on the happy path.
        /// </summary>
        internal static string FormatRejectMessage(RouteAnalysisStatus status)
        {
            switch (status)
            {
                case RouteAnalysisStatus.Eligible:
                    return string.Empty;
                case RouteAnalysisStatus.MissingRouteProof:
                    return "Recording has no route proof - log the dock event to enable a Supply Route.";
                case RouteAnalysisStatus.MultipleConnectionWindows:
                    return "Multiple dock windows detected - multi-stop routes are not yet supported in v1.";
                case RouteAnalysisStatus.NoDeliveryManifest:
                    return "No delivery payload detected - check that cargo actually moved from transport to destination.";
                case RouteAnalysisStatus.MixedPickupDelivery:
                    return "Mixed pickup and delivery detected - Supply Routes must be one-way in v1.";
                case RouteAnalysisStatus.MissingEndpointProof:
                    return "Endpoint vessel could not be identified at dock time.";
                default:
                    return "Route source is not eligible (" + status + ").";
            }
        }

        /// <summary>
        /// Build the multi-line dialog summary block describing the route the
        /// player is about to create. Includes Origin / Endpoint / Resources /
        /// Inventory / Transit lines, plus a "Dispatch cost: TBD" line in
        /// Career mode only.
        /// </summary>
        /// <remarks>
        /// CRE-2: the Transit line is the FULL rendered <c>[root..undock]</c> span
        /// the builder actually uses (see <c>RouteCreationDialog.ComputeRootToUndockSpan</c>),
        /// NOT the leaf dock-child span (<c>source.EndUT - source.StartUT</c>). On a
        /// multi-recording flight the leaf span is smaller, so showing it here would
        /// understate the player-facing transit relative to the created route's
        /// <c>TransitDuration</c>. Pass the source <paramref name="tree"/> so the
        /// span helper can resolve the tree ROOT launch UT; when it is null the
        /// helper falls back to the leaf span (no worse than the old behaviour).
        /// </remarks>
        internal static string BuildSummaryBlock(
            RouteAnalysisResult analysis, Game.Modes mode, RecordingTree tree = null)
        {
            var sb = new StringBuilder();
            if (analysis == null || !analysis.IsEligible)
            {
                // Defensive - callers should gate on IsEligible. Render the
                // reject message so the dialog body is never blank.
                sb.Append(analysis != null
                    ? FormatRejectMessage(analysis.Status)
                    : "No route analysis available.");
                return sb.ToString();
            }

            Recording source = analysis.SourceRecording;
            string originLabel;
            if (source != null
                && !string.IsNullOrEmpty(source.LaunchSiteName)
                && source.StartBodyName == "Kerbin")
            {
                originLabel = "Kerbin (" + source.LaunchSiteName + ")";
            }
            else if (source != null
                && source.RouteOriginProof != null
                && source.RouteOriginProof.StartDockedOriginVesselPid != 0)
            {
                originLabel =
                    (source.StartBodyName ?? "<unknown>")
                    + " (vessel #"
                    + source.RouteOriginProof.StartDockedOriginVesselPid.ToString(IC)
                    + ")";
            }
            else
            {
                originLabel = "<unknown>";
            }
            sb.Append("Origin: ").Append(originLabel).Append('\n');

            sb.Append("Endpoint: ");
            if (analysis.ConnectionWindow != null && analysis.ConnectionWindow.EndpointAtDock.HasValue)
                sb.Append(FormatEndpoint(analysis.ConnectionWindow.EndpointAtDock.Value));
            else
                sb.Append("<unknown>");
            sb.Append('\n');

            sb.Append("Resources:\n");
            if (analysis.ResourceDeliveryManifest != null && analysis.ResourceDeliveryManifest.Count > 0)
            {
                var keys = new List<string>(analysis.ResourceDeliveryManifest.Keys);
                keys.Sort(System.StringComparer.Ordinal);
                for (int i = 0; i < keys.Count; i++)
                {
                    string k = keys[i];
                    sb.Append("  - ").Append(FormatResourceLine(k, analysis.ResourceDeliveryManifest[k])).Append('\n');
                }
            }
            else
            {
                sb.Append("  (none)\n");
            }

            sb.Append("Inventory:\n");
            if (analysis.InventoryDeliveryManifest != null && analysis.InventoryDeliveryManifest.Count > 0)
            {
                for (int i = 0; i < analysis.InventoryDeliveryManifest.Count; i++)
                    sb.Append("  - ").Append(FormatInventoryLine(analysis.InventoryDeliveryManifest[i])).Append('\n');
            }
            else
            {
                sb.Append("  (none)\n");
            }

            // CRE-2: full [root..undock] span (matches the created route's
            // TransitDuration), reusing the single span helper so display and
            // creation never diverge. Falls back to the leaf span when the tree /
            // root cannot be resolved.
            double transit = source != null
                ? RouteCreationDialog.ComputeRootToUndockSpan(analysis, tree)
                : 0.0;
            sb.Append("Transit: ").Append(FormatTransitTime(transit)).Append('\n');

            if (mode == Game.Modes.CAREER)
            {
                // v0 dispatch-cost computation is not wired yet; the line
                // exists so Phase 3 has the layout pinned by tests.
                sb.Append("Dispatch cost: TBD\n");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Generate a default route name when the player leaves the name
        /// field empty. Format: <c>"Route: &lt;origin&gt; → &lt;endpoint-body&gt;"</c>,
        /// trimmed to ~40 characters.
        /// </summary>
        internal static string GenerateDefaultRouteName(RouteAnalysisResult analysis)
        {
            string origin = "?";
            string endpoint = "?";
            if (analysis != null)
            {
                Recording source = analysis.SourceRecording;
                if (source != null)
                {
                    if (!string.IsNullOrEmpty(source.LaunchSiteName)
                        && source.StartBodyName == "Kerbin")
                    {
                        origin = "KSC";
                    }
                    else if (!string.IsNullOrEmpty(source.StartBodyName))
                    {
                        origin = source.StartBodyName;
                    }
                }
                if (analysis.ConnectionWindow != null
                    && analysis.ConnectionWindow.EndpointAtDock.HasValue
                    && !string.IsNullOrEmpty(analysis.ConnectionWindow.EndpointAtDock.Value.BodyName))
                {
                    endpoint = analysis.ConnectionWindow.EndpointAtDock.Value.BodyName;
                }
            }

            string name = "Route: " + origin + " → " + endpoint;
            const int maxLen = 40;
            if (name.Length > maxLen)
                name = name.Substring(0, maxLen);
            return name;
        }
    }
}
