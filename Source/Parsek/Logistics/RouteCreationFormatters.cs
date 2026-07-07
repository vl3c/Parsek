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

        /// <summary>How a route's origin was classified by <see cref="ResolveOriginIdentity"/>.</summary>
        internal enum RouteOriginKind
        {
            /// <summary>Neither a KSC launch nor a captured start-docked depot proof.</summary>
            Unknown = 0,
            /// <summary>Launched from a Kerbin launch site (funds origin).</summary>
            Ksc = 1,
            /// <summary>Started docked to an origin depot (start-route proof present).</summary>
            Depot = 2,
            /// <summary>
            /// Cargo harvested en route (M2, plan D7): undocked start whose
            /// delivery is fully covered by witnessed harvest windows
            /// (<see cref="RouteAnalysisResult.IsHarvestOrigin"/>).
            /// </summary>
            Harvest = 3
        }

        /// <summary>
        /// Origin identity resolved once for all three display surfaces (the dialog
        /// summary origin line, the default route name, and the candidate table
        /// origin cell) so they cannot diverge on origin classification.
        /// </summary>
        internal struct RouteOriginIdentity
        {
            public RouteOriginKind Kind;
            /// <summary>Origin body (tree-root <c>StartBodyName</c>, else the source recording's).</summary>
            public string BodyName;
            /// <summary>Launch site of the tree root (set only when <see cref="Kind"/> is <c>Ksc</c>).</summary>
            public string LaunchSiteName;
            /// <summary>Start-docked origin partner pid (set only when <see cref="Kind"/> is <c>Depot</c>).</summary>
            public uint DepotVesselPid;
        }

        /// <summary>
        /// Resolve the route's origin identity from the tree ROOT recording (the
        /// launch carries <c>LaunchSiteName</c> / <c>StartBodyName</c>), falling
        /// back to the dock-child <see cref="RouteAnalysisResult.SourceRecording"/>
        /// only when the tree has no resolvable root (the legacy single-recording
        /// case where the source IS the root).
        /// </summary>
        /// <remarks>
        /// The bug this fixes: on the common multi-recording docking flight the
        /// analysis source is the DOCK CHILD, which started mid-flight at the dock
        /// and therefore has <c>LaunchSiteName == null</c>. Reading the origin off
        /// the source made every origin branch fall through to the "unknown"
        /// placeholder even for a correctly KSC-classified route. Both the launch
        /// site (KSC) AND the start-docked depot proof are read from the resolved
        /// origin recording (the tree root, source fallback), matching the
        /// authoritative <see cref="RouteBuilder"/> (which reads
        /// <c>originRec.RouteOriginProof</c>) and
        /// <see cref="RouteRunCostCalculator.IsCandidateKscOrigin"/> /
        /// <see cref="RouteCreationDialog.ComputeRootToUndockSpan"/>, so the three
        /// origin labels stay consistent with the built
        /// <see cref="Route.IsKscOrigin"/> / <see cref="Route.Origin"/>.
        /// </remarks>
        internal static RouteOriginIdentity ResolveOriginIdentity(
            RouteAnalysisResult analysis, RecordingTree tree)
        {
            var id = new RouteOriginIdentity { Kind = RouteOriginKind.Unknown };
            if (analysis == null) return id;

            // M2 harvest origin (plan D7): classified directly off the
            // analysis verdict - the origin recording proves neither a KSC
            // launch nor a depot proof by definition, so it cannot answer.
            // The body comes from the first harvest window's open location.
            if (analysis.IsHarvestOrigin)
            {
                id.Kind = RouteOriginKind.Harvest;
                id.BodyName = analysis.FirstHarvestWindow?.BodyName;
                return id;
            }

            // Resolve the ORIGIN recording: the tree ROOT (the launch / the
            // recording that started the flight, which carries the launch site and
            // the start-docked depot proof) when it resolves, else the analysis
            // source (the legacy single-recording case where the source IS the root).
            Recording originRec = analysis.SourceRecording;
            if (tree?.Recordings != null
                && !string.IsNullOrEmpty(tree.RootRecordingId)
                && tree.Recordings.TryGetValue(tree.RootRecordingId, out Recording rootRec)
                && rootRec != null)
            {
                originRec = rootRec;
            }

            if (originRec == null) return id;

            id.BodyName = originRec.StartBodyName;

            // Classification delegates to the RouteAnalysisEngine helpers (M1):
            // the analysis engine's undocked-start workflow gate
            // (RouteAnalysisStatus.UndockedStartOrigin) is the negation of these
            // two branches, so sharing the predicates keeps the display labels
            // and the gate from ever diverging.
            if (RouteAnalysisEngine.IsKscOriginRecording(originRec))
            {
                id.Kind = RouteOriginKind.Ksc;
                id.LaunchSiteName = originRec.LaunchSiteName;
                return id;
            }

            if (RouteAnalysisEngine.HasDockedOriginProof(originRec))
            {
                id.Kind = RouteOriginKind.Depot;
                id.DepotVesselPid = originRec.RouteOriginProof.StartDockedOriginVesselPid;
                return id;
            }

            return id;
        }

        /// <summary>
        /// Format a resource line for the dialog body. Returns
        /// <c>"name: amount"</c> with one fractional digit, e.g.
        /// <c>"LiquidFuel: 150.0"</c>. The empty-name fallback is bracket-free
        /// (<c>"unknown"</c>) so it renders as readable text in the TMP-backed
        /// PopupDialog body instead of being parsed as a bogus rich-text tag.
        /// </summary>
        internal static string FormatResourceLine(string name, double amount)
        {
            string displayName = string.IsNullOrEmpty(name) ? "unknown" : name;
            return displayName + ": " + amount.ToString("F1", IC);
        }

        /// <summary>
        /// Format an inventory line. Quantity > 1 emits a "<c>xN</c>" suffix;
        /// quantity 1 omits the multiplier. Non-empty variant names render in
        /// parens, e.g. <c>"evaJetpack (white) x2"</c>.
        /// </summary>
        internal static string FormatInventoryLine(InventoryPayloadItem item)
        {
            // Bracket-free fallbacks ("unknown"): this text feeds the TMP-backed
            // PopupDialog body, which parses "<...>" as rich-text markup.
            if (item == null) return "unknown";

            string partLabel = string.IsNullOrEmpty(item.PartName) ? "unknown" : item.PartName;
            string variant = item.VariantName;
            if (!string.IsNullOrEmpty(variant))
                partLabel = partLabel + " (" + variant + ")";

            if (item.Quantity > 1)
                return partLabel + " x" + item.Quantity.ToString(IC);
            return partLabel;
        }

        /// <summary>
        /// Format a <see cref="RouteEndpoint"/> as
        /// <c>"body (lat, lon, alt)"</c>. Empty body falls back to the
        /// bracket-free <c>"unknown"</c> (this string renders into the TMP-backed
        /// PopupDialog body, where <c>"&lt;...&gt;"</c> is parsed as markup).
        /// </summary>
        internal static string FormatEndpoint(RouteEndpoint ep)
        {
            string body = string.IsNullOrEmpty(ep.BodyName) ? "unknown" : ep.BodyName;
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
            return FormatRejectMessage(status, null);
        }

        /// <summary>
        /// Detail-carrying overload (M2, plan finding 12): when
        /// <paramref name="detail"/> is non-empty it quantifies the rejection
        /// (today only <see cref="RouteAnalysisStatus.UntrackedCargoGain"/>
        /// carries one, e.g. <c>"Ore: 120.0 gained, 100.0 harvested"</c>).
        /// Statuses without a detail render identically to the single-arg
        /// overload.
        /// </summary>
        /// <summary>
        /// Claw producer (design-logistics-claw-producer.md 5): the one
        /// player-facing label for a non-dock connection, shared by the
        /// route-creation summary and the detail panel's per-stop destination.
        /// Dock (and every other kind) returns an empty suffix so existing
        /// routes render byte-identically.
        /// </summary>
        internal static string ConnectionKindSuffix(RouteConnectionKind kind)
        {
            return kind == RouteConnectionKind.Grapple ? " (grappled)" : string.Empty;
        }

        internal static string FormatRejectMessage(RouteAnalysisStatus status, string detail)
        {
            switch (status)
            {
                case RouteAnalysisStatus.Eligible:
                    return string.Empty;
                case RouteAnalysisStatus.MissingRouteProof:
                    return "Recording has no route proof - log the dock event to enable a Supply Route.";
                case RouteAnalysisStatus.MultipleConnectionWindows:
                    return "Two transfers happened at the same recorded time and cannot be ordered. Re-record so each dock happens at a distinct moment.";
                case RouteAnalysisStatus.NoDeliveryManifest:
                    return "No delivery payload detected - check that cargo actually moved from transport to destination.";
                case RouteAnalysisStatus.MixedPickupDelivery:
                    return "Unwitnessed inventory gain detected - the transport gained a stored part that the destination did not give it. Inventory is non-fungible, so only a stored part that visibly moved from the destination onto the transport can be picked up. Re-record so the picked-up part is the same one the destination held.";
                case RouteAnalysisStatus.MissingEndpointProof:
                    return "Endpoint vessel could not be identified at dock time.";
                case RouteAnalysisStatus.UndockedStartOrigin:
                    return "This run starts undocked with cargo already aboard, so the cargo's source was never witnessed. Start the supply run docked to the origin depot, record the mining that produced the cargo, or launch it from KSC.";
                case RouteAnalysisStatus.UntrackedCargoGain:
                    return "The transport gained cargo during this run with no recorded source"
                        + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")")
                        + ". Only witnessed gains can route: record the mining with the drill or converter running, or re-record without the unexplained gain.";
                case RouteAnalysisStatus.FlowDoesNotClose:
                    return "This run's cargo does not add up: the transport ended with more of a resource than ever arrived"
                        + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")")
                        + ". The recorded loads, harvest, and deliveries cannot account for what was left aboard. Re-record so every resource that leaves the transport is matched by a recorded load, harvest, or delivery.";
                case RouteAnalysisStatus.MidRecordingStartTrimUnsupported:
                    return "This run starts between two docks. Routes must begin at launch or while docked to the origin; mid-flight start points are not supported yet.";
                case RouteAnalysisStatus.UnsupportedConnectionKind:
                    return "This run's transfer used a connection type Parsek does not support for routes"
                        + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")")
                        + ". Docked and claw-grappled transfers are supported.";
                default:
                    return "Route source is not eligible (" + status + ").";
            }
        }

        /// <summary>
        /// Build the multi-line dialog summary block describing the route the
        /// player is about to create. Includes Origin / Endpoint / Resources /
        /// Inventory / Transit lines, plus a per-run cost block (Career + KSC
        /// origin only).
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
        ///
        /// Run-cost (Phase 3.2): the old "Dispatch cost: TBD" line was gated on
        /// <c>mode == CAREER</c> ONLY (no KSC-origin check), so a Career non-KSC
        /// route printed TBD. The replacement cost block is computed by the caller
        /// (it needs the live ledger + part-cost lookups) and passed in as
        /// <paramref name="runCost"/>; its <c>Applicable</c> flag already encodes
        /// the ADDED Career + KSC-origin gate, so the block shows ONLY for Career +
        /// KSC origin with a known launch cost and NOTHING otherwise (no TBD, no "0
        /// funds", no "n/a"). A null <paramref name="runCost"/> (test paths that do
        /// not exercise the cost) simply omits the block.
        /// </remarks>
        internal static string BuildSummaryBlock(
            RouteAnalysisResult analysis,
            Game.Modes mode,
            RecordingTree tree = null,
            RouteRunCostCalculator.RouteRunCost? runCost = null)
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
            // Origin identity is resolved from the tree ROOT (the launch), not the
            // dock-child source. Bracket-free fallbacks ("unknown") so a genuine
            // miss renders as text in the TMP-backed PopupDialog body rather than
            // being parsed as a bogus "<...>" rich-text tag.
            RouteOriginIdentity origin = ResolveOriginIdentity(analysis, tree);
            string originLabel;
            switch (origin.Kind)
            {
                case RouteOriginKind.Ksc:
                    originLabel =
                        (string.IsNullOrEmpty(origin.BodyName) ? "Kerbin" : origin.BodyName)
                        + " (" + origin.LaunchSiteName + ")";
                    break;
                case RouteOriginKind.Depot:
                    originLabel =
                        (string.IsNullOrEmpty(origin.BodyName) ? "unknown" : origin.BodyName)
                        + " (vessel #"
                        + origin.DepotVesselPid.ToString(IC)
                        + ")";
                    break;
                case RouteOriginKind.Harvest:
                    // M2 (plan D7): no origin vessel - the cargo was mined /
                    // converted during the run itself.
                    originLabel = "harvested en route";
                    break;
                default:
                    originLabel = "unknown";
                    break;
            }
            sb.Append("Origin: ").Append(originLabel).Append('\n');

            sb.Append("Endpoint: ");
            if (analysis.ConnectionWindow != null && analysis.ConnectionWindow.EndpointAtDock.HasValue)
            {
                sb.Append(FormatEndpoint(analysis.ConnectionWindow.EndpointAtDock.Value));
                sb.Append(ConnectionKindSuffix(analysis.ConnectionWindow.TransferKind));
            }
            else
                sb.Append("unknown");
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

            // Run-cost block (Phase 3.2): show ONLY for Career + KSC origin with a
            // known launch cost. Applicable already encodes the Career + KSC gate
            // (computed by the caller from the source recording + tree), and
            // CostKnown gates out an unhydrated snapshot (gotcha G7). Outside that,
            // append nothing (no TBD, no "0 funds"). The `mode` parameter is no
            // longer the gate (Applicable carries it), but is kept in the signature
            // for callers and back-compat.
            if (runCost.HasValue && runCost.Value.Applicable && runCost.Value.CostKnown)
            {
                sb.Append(LogisticsCostPresentation.FormatCreationSummaryBlock(runCost.Value));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Generate a default route name when the player leaves the name
        /// field empty. Format: <c>"Route: origin -> endpoint-body"</c>,
        /// trimmed to ~40 characters. The <paramref name="tree"/> is plumbed
        /// through to <see cref="ResolveOriginIdentity"/> so a KSC origin resolves
        /// to <c>"KSC"</c> off the tree ROOT rather than the dock-child body; a
        /// null tree falls back to the source recording (legacy single-recording).
        /// </summary>
        internal static string GenerateDefaultRouteName(
            RouteAnalysisResult analysis, RecordingTree tree = null)
        {
            string origin = "?";
            string endpoint = "?";
            if (analysis != null)
            {
                RouteOriginIdentity id = ResolveOriginIdentity(analysis, tree);
                if (id.Kind == RouteOriginKind.Ksc)
                    origin = "KSC";
                else if (!string.IsNullOrEmpty(id.BodyName))
                    origin = id.BodyName;
                else if (id.Kind == RouteOriginKind.Harvest)
                    origin = "harvested"; // bodyless harvest window (defensive)

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
