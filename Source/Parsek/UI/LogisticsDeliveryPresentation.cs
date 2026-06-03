using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Parsek.Logistics;

namespace Parsek
{
    /// <summary>
    /// Pure presentation helpers for the Logistics window's delivery legibility
    /// work (Phase 2 H2/H3/H4/H5). All methods are Unity-free and side-effect-free
    /// so they are unit tested directly off the IMGUI path (mirrors
    /// <see cref="LogisticsButtonState"/> and <see cref="LogisticsCountdownPresentation"/>).
    /// IMGUI drawing and the live ledger / store reads stay in the window file; this
    /// class only formats already-resolved inputs. InvariantCulture is used for every
    /// numeric piece so comma-locale systems render identically.
    /// </summary>
    internal static class LogisticsDeliveryPresentation
    {
        // ------------------------------------------------------------------
        // H2: realized delivery (actual vs requested) plus cumulative total.
        // ------------------------------------------------------------------

        /// <summary>
        /// Formats one cycle's realized delivery for the detail panel. The pair
        /// (requested, actual) comes from a single <c>RouteCargoDelivered</c> ledger
        /// row: <paramref name="actual"/> is the post-clamp delivered manifest (always
        /// populated on a delivered row) and <paramref name="requested"/> is the
        /// requested manifest, which the recorder leaves null on a full fill (the
        /// "no shortfall" signal) and populates only when at least one resource fell
        /// short.
        /// <list type="bullet">
        ///   <item>Null/empty actual: "delivered nothing".</item>
        ///   <item>Null requested (full fill): "delivered 150 LiquidFuel".</item>
        ///   <item>Requested set with a per-resource shortfall: "delivered 40 of 150
        ///   LiquidFuel (110 did not fit)".</item>
        /// </list>
        /// The full stock resource key is rendered (no abbreviation), matching the
        /// window's existing FormatManifest. F1 + InvariantCulture throughout.
        /// </summary>
        internal static string FormatRealizedDelivery(
            IReadOnlyDictionary<string, double> requested,
            IReadOnlyDictionary<string, double> actual)
        {
            if (actual == null || actual.Count == 0)
                return "delivered nothing";

            // Render every resource that was delivered OR requested. A resource that was
            // requested but fully blocked (0 delivered) has no actual entry, so fold the
            // requested-only keys in too, otherwise a mixed-resource partial cycle would
            // silently omit the fully-blocked one. Sorted (ordinal) so the per-resource
            // order is stable across cache refreshes (a plain Dictionary order can flip).
            var keys = new List<string>(actual.Keys);
            if (requested != null)
            {
                foreach (string rk in requested.Keys)
                {
                    if (!actual.ContainsKey(rk))
                        keys.Add(rk);
                }
            }
            keys.Sort(System.StringComparer.Ordinal);

            var sb = new StringBuilder();
            sb.Append("delivered ");
            bool first = true;
            foreach (string key in keys)
            {
                if (!first) sb.Append(", ");
                first = false;

                double delivered;
                actual.TryGetValue(key, out delivered); // 0.0 for a requested-only key
                double req = 0.0;
                bool hasReq = requested != null && requested.TryGetValue(key, out req);
                double shortfall = hasReq ? req - delivered : 0.0;

                if (hasReq && shortfall > 0.0)
                {
                    // Partial (or fully blocked) on this resource: show
                    // delivered-of-requested and the amount that did not fit.
                    sb.Append(delivered.ToString("F1", CultureInfo.InvariantCulture))
                      .Append(" of ")
                      .Append(req.ToString("F1", CultureInfo.InvariantCulture))
                      .Append(' ')
                      .Append(key)
                      .Append(" (")
                      .Append(shortfall.ToString("F1", CultureInfo.InvariantCulture))
                      .Append(" did not fit)");
                }
                else
                {
                    // Full fill on this resource (or no request recorded for it).
                    sb.Append(delivered.ToString("F1", CultureInfo.InvariantCulture))
                      .Append(' ')
                      .Append(key);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// True when the (requested, actual) pair represents a shortfall on at least
        /// one resource: a non-null requested manifest with a recorded requested value
        /// strictly greater than the delivered value. Drives the yellow tint on the
        /// realized-delivery line. A null requested manifest is a full fill (never a
        /// shortfall).
        /// </summary>
        internal static bool HasShortfall(
            IReadOnlyDictionary<string, double> requested,
            IReadOnlyDictionary<string, double> actual)
        {
            if (requested == null)
                return false;
            // Walk the requested manifest (the shortfall signal) and compare each entry
            // against the delivered amount (0 when the resource is absent from actual),
            // so a resource that was requested but fully blocked also counts as a
            // shortfall, not just a partial fill of a delivered resource.
            foreach (KeyValuePair<string, double> kv in requested)
            {
                double act = 0.0;
                if (actual != null)
                    actual.TryGetValue(kv.Key, out act);
                if (kv.Value - act > 0.0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// One realized-delivery ledger row reduced to the fields the summary needs:
        /// the actual (delivered) manifest, the requested manifest (null on a full
        /// fill), and the row's game-time UT (used to pick the latest cycle).
        /// </summary>
        internal readonly struct DeliveryRow
        {
            internal DeliveryRow(
                IReadOnlyDictionary<string, double> actual,
                IReadOnlyDictionary<string, double> requested,
                double ut)
            {
                Actual = actual;
                Requested = requested;
                Ut = ut;
            }

            /// <summary>Post-clamp delivered manifest for this cycle.</summary>
            internal IReadOnlyDictionary<string, double> Actual { get; }

            /// <summary>Requested manifest for this cycle; null on a full fill.</summary>
            internal IReadOnlyDictionary<string, double> Requested { get; }

            /// <summary>Row game-time UT; the latest row by max Ut is the last cycle.</summary>
            internal double Ut { get; }
        }

        /// <summary>
        /// Summary of all this route's realized-delivery rows: the latest cycle (by
        /// max UT) and the cumulative per-resource total across every row. Returned by
        /// <see cref="SummarizeRouteDeliveries"/>.
        /// </summary>
        internal sealed class RouteDeliverySummary
        {
            /// <summary>Number of realized-delivery rows found for the route.</summary>
            internal int RowCount { get; set; }

            /// <summary>True when at least one row was found.</summary>
            internal bool HasAny { get { return RowCount > 0; } }

            /// <summary>Actual (delivered) manifest of the latest row; null when none.</summary>
            internal IReadOnlyDictionary<string, double> LastActual { get; set; }

            /// <summary>Requested manifest of the latest row; null when a full fill or none.</summary>
            internal IReadOnlyDictionary<string, double> LastRequested { get; set; }

            /// <summary>Per-resource sum of every row's actual manifest.</summary>
            internal Dictionary<string, double> CumulativeTotal { get; set; }
        }

        /// <summary>
        /// Reduces a route's realized-delivery rows to its latest cycle plus a
        /// cumulative per-resource total. The latest row is the one with the maximum
        /// <see cref="DeliveryRow.Ut"/>; the cumulative total sums each row's actual
        /// manifest per resource key. An empty / null sequence yields a zero summary
        /// (RowCount 0, empty cumulative, null last manifests). Pure: callers own any
        /// logging.
        /// </summary>
        internal static RouteDeliverySummary SummarizeRouteDeliveries(IEnumerable<DeliveryRow> rows)
        {
            var summary = new RouteDeliverySummary
            {
                CumulativeTotal = new Dictionary<string, double>()
            };
            if (rows == null)
                return summary;

            bool haveLatest = false;
            double latestUt = 0.0;
            foreach (DeliveryRow row in rows)
            {
                summary.RowCount++;

                if (!haveLatest || row.Ut > latestUt)
                {
                    haveLatest = true;
                    latestUt = row.Ut;
                    summary.LastActual = row.Actual;
                    summary.LastRequested = row.Requested;
                }

                if (row.Actual != null)
                {
                    foreach (KeyValuePair<string, double> kv in row.Actual)
                    {
                        double prev;
                        summary.CumulativeTotal.TryGetValue(kv.Key, out prev);
                        summary.CumulativeTotal[kv.Key] = prev + kv.Value;
                    }
                }
            }

            return summary;
        }

        /// <summary>
        /// Formats a cumulative per-resource total for the "Total delivered" detail
        /// line, e.g. "1240.0 LiquidFuel, 30.0 Oxidizer". Empty / null map yields
        /// "(none)". Full stock resource keys (no abbreviation), F1 + InvariantCulture.
        /// </summary>
        internal static string FormatCumulativeTotal(IReadOnlyDictionary<string, double> cumulative)
        {
            if (cumulative == null || cumulative.Count == 0)
                return "(none)";
            // Sorted (ordinal) so the per-resource order is stable across cache
            // refreshes; a plain Dictionary enumeration order can reorder between frames.
            var keys = new List<string>(cumulative.Keys);
            keys.Sort(System.StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (string key in keys)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(cumulative[key].ToString("F1", CultureInfo.InvariantCulture))
                  .Append(' ')
                  .Append(key);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // H3: delivering vs "flying, not delivering" badge.
        // ------------------------------------------------------------------

        /// <summary>
        /// The realized outcome of a route's latest delivery cycle, derived from its
        /// latest <c>RouteCargoDelivered</c> ledger row.
        /// </summary>
        internal enum DeliveryOutcome
        {
            /// <summary>No fresh delivered row (never ran or blocked this cycle).</summary>
            None = 0,

            /// <summary>Delivered, but a requested manifest was recorded (shortfall).</summary>
            Partial = 1,

            /// <summary>Delivered in full (a non-empty actual with no requested manifest).</summary>
            Full = 2,
        }

        /// <summary>
        /// The always-visible delivery badge shown per route row and in the detail panel.
        /// </summary>
        internal enum DeliveryBadge
        {
            /// <summary>Ghost flying and the last cycle delivered (green).</summary>
            Delivering = 0,

            /// <summary>Ghost flying but the last cycle transferred nothing (yellow).</summary>
            FlyingNotDelivering = 1,

            /// <summary>Not ghost-driving: a paused / hard-broken route (grey).</summary>
            Paused = 2,

            /// <summary>Ghost flying but no cycle has completed yet (cyan).</summary>
            New = 3,
        }

        /// <summary>
        /// Derives the always-visible delivery badge for a route. A non-driving route
        /// (paused or hard-broken) is <see cref="DeliveryBadge.Paused"/>. A driving
        /// route that has delivered at least once and whose last outcome was Full or
        /// Partial is <see cref="DeliveryBadge.Delivering"/>. A driving route that has
        /// never completed a cycle and has no delivered row yet is
        /// <see cref="DeliveryBadge.New"/>. A driving route that has run before but
        /// whose latest cycle delivered nothing (blocked / skipped) is
        /// <see cref="DeliveryBadge.FlyingNotDelivering"/>.
        /// </summary>
        /// <param name="ghostDriving">Whether the ghost is flying
        /// (<see cref="RouteStatusPolicy.GhostDriving"/>).</param>
        /// <param name="lastOutcome">Outcome derived from the latest delivered row.</param>
        /// <param name="completedCycles">The route's completed cycle count; separates
        /// "New (never run)" from "Flying, not delivering".</param>
        /// <param name="skippedCycles">The route's skipped cycle count (blocked cycles).</param>
        internal static DeliveryBadge ClassifyDeliveryBadge(
            bool ghostDriving,
            DeliveryOutcome lastOutcome,
            int completedCycles,
            int skippedCycles)
        {
            if (!ghostDriving)
                return DeliveryBadge.Paused;

            if (lastOutcome == DeliveryOutcome.Full || lastOutcome == DeliveryOutcome.Partial)
                return DeliveryBadge.Delivering;

            // Ghost-driving with no fresh delivered row.
            if (completedCycles == 0 && skippedCycles == 0)
                return DeliveryBadge.New;

            return DeliveryBadge.FlyingNotDelivering;
        }

        /// <summary>
        /// The short cell label for a delivery badge. Plain ASCII, no glyphs.
        /// </summary>
        internal static string DeliveryBadgeLabel(DeliveryBadge badge)
        {
            switch (badge)
            {
                case DeliveryBadge.Delivering:
                    return "Delivering";
                case DeliveryBadge.FlyingNotDelivering:
                    return "Flying, not delivering";
                case DeliveryBadge.New:
                    return "New (not yet run)";
                case DeliveryBadge.Paused:
                default:
                    return "Paused";
            }
        }

        // ------------------------------------------------------------------
        // H4: name the destination vessel instead of bare coordinates.
        // ------------------------------------------------------------------

        /// <summary>
        /// Formats the route Destination cell. When the endpoint's target vessel was
        /// resolved at the draw site (<paramref name="resolvedVesselName"/> non-empty)
        /// the vessel name is shown; otherwise it falls back to the body + situation +
        /// coordinates string (identical to the window's FormatEndpointShort). The live
        /// resolution (RouteEndpointResolver.TryResolveEndpoint) happens at the draw
        /// site, never here, so this helper stays Unity-free and testable.
        /// </summary>
        internal static string FormatDestinationDisplay(string resolvedVesselName, RouteEndpoint endpoint)
        {
            if (!string.IsNullOrEmpty(resolvedVesselName))
                return resolvedVesselName;
            return FormatEndpointCoords(endpoint);
        }

        /// <summary>
        /// The coordinate fallback string for an endpoint: "Body (orbit|surface)
        /// lat,lon" with F2 + InvariantCulture, or "-" when the body is unknown.
        /// Replicates the window's private FormatEndpointShort so both the cell text
        /// (fallback) and the hover tooltip can render the same coords from the pure
        /// layer.
        /// </summary>
        internal static string FormatEndpointCoords(RouteEndpoint endpoint)
        {
            if (string.IsNullOrEmpty(endpoint.BodyName)) return "-";
            string sit = endpoint.IsSurface ? "surface" : "orbit";
            return string.Format(CultureInfo.InvariantCulture,
                "{0} ({1}) {2:F2},{3:F2}",
                endpoint.BodyName, sit, endpoint.Latitude, endpoint.Longitude);
        }

        // ------------------------------------------------------------------
        // H5: recording / mission names instead of 8-char GUID fragments.
        // ------------------------------------------------------------------

        /// <summary>
        /// Formats a source recording for the detail panel. When the id resolved to a
        /// display name (<paramref name="recordingName"/> non-empty) it shows the name,
        /// the human-1-based position in its owning tree, and the tree/mission name
        /// when known: "Mun Fuel Run (rec 3 of tree 'Munar Logistics')". A standalone
        /// recording (no tree name) drops the tree clause: "Mun Fuel Run (rec 3)". An
        /// unknown position (<paramref name="humanTreePosition"/> &lt;= 0) drops the
        /// "rec N" clause entirely. When the id did not resolve
        /// (<paramref name="recordingName"/> null/empty) it falls back to the short id
        /// verbatim. The short id is kept by the caller as a hover tooltip only.
        /// InvariantCulture on the position number.
        /// </summary>
        /// <param name="shortId">The 8-char id fragment used as the resolved-miss
        /// fallback text and the hover tooltip.</param>
        /// <param name="recordingName">The resolved recording display name, or null/empty
        /// when the id is not in the committed store.</param>
        /// <param name="treeName">The owning tree/mission name, or null for a standalone
        /// recording.</param>
        /// <param name="humanTreePosition">The 1-based position within the tree, or a
        /// non-positive value when unknown.</param>
        internal static string FormatSourceRecordingDisplay(
            string shortId,
            string recordingName,
            string treeName,
            int humanTreePosition)
        {
            if (string.IsNullOrEmpty(recordingName))
                return string.IsNullOrEmpty(shortId) ? "<none>" : shortId;

            bool hasPosition = humanTreePosition > 0;
            bool hasTree = !string.IsNullOrEmpty(treeName);

            if (hasTree && hasPosition)
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} (rec {1} of tree '{2}')", recordingName, humanTreePosition, treeName);
            if (hasTree)
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} (tree '{1}')", recordingName, treeName);
            if (hasPosition)
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} (rec {1})", recordingName, humanTreePosition);
            return recordingName;
        }

        // ------------------------------------------------------------------
        // M4: disambiguate DestinationFull (free-capacity context) and offer a
        // re-scan for a recoverable surface EndpointLost.
        // ------------------------------------------------------------------

        /// <summary>
        /// One destination resource's capacity context: how much the route asked
        /// to deliver this cycle (<see cref="Requested"/>) and how much free
        /// capacity the destination vessel currently has (<see cref="Free"/>). The
        /// LIVE free-capacity read (LiveDeliveryCapacityProbe over a real Vessel)
        /// happens in the window's ~1 Hz legibility pass, never in this pure layer;
        /// these are already-resolved numbers.
        /// </summary>
        internal readonly struct CapacityEntry
        {
            internal CapacityEntry(string resource, double requested, double free)
            {
                Resource = resource;
                Requested = requested;
                Free = free;
            }

            /// <summary>Full stock resource key (no abbreviation).</summary>
            internal string Resource { get; }

            /// <summary>Amount the route requested to deliver this cycle.</summary>
            internal double Requested { get; }

            /// <summary>Free capacity on the destination vessel for this resource.</summary>
            internal double Free { get; }
        }

        /// <summary>
        /// Formats the DestinationFull free-capacity context line, e.g.
        /// "Munar Station tanks full: 0.0 of 150.0 LiquidFuel free" (multi-resource
        /// comma-joined, ordinal-sorted by resource key for stable ordering like
        /// <see cref="FormatRealizedDelivery"/>). Each entry renders as
        /// "&lt;free&gt; of &lt;requested&gt; &lt;resource&gt; free" with F1 +
        /// InvariantCulture and the full stock resource key. A null / empty entry
        /// list yields "(capacity unknown)" so the line is never blank when the
        /// route is DestinationFull but the destination could not be probed.
        /// </summary>
        /// <param name="destinationName">The resolved destination vessel name, or
        /// null / empty when it could not be resolved (falls back to "Destination").</param>
        /// <param name="entries">Already-resolved per-resource (requested, free)
        /// pairs.</param>
        internal static string FormatCapacityContext(
            string destinationName, IReadOnlyList<CapacityEntry> entries)
        {
            string name = string.IsNullOrEmpty(destinationName) ? "Destination" : destinationName;
            if (entries == null || entries.Count == 0)
                return name + " tanks full: (capacity unknown)";

            // Sort by resource key (ordinal) so the per-resource order is stable
            // across cache refreshes, matching the other delivery formatters.
            var sorted = new List<CapacityEntry>(entries);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Resource, b.Resource));

            var sb = new StringBuilder();
            sb.Append(name).Append(" tanks full: ");
            bool first = true;
            foreach (CapacityEntry entry in sorted)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(entry.Free.ToString("F1", CultureInfo.InvariantCulture))
                  .Append(" of ")
                  .Append(entry.Requested.ToString("F1", CultureInfo.InvariantCulture))
                  .Append(' ')
                  .Append(string.IsNullOrEmpty(entry.Resource) ? "<unknown>" : entry.Resource)
                  .Append(" free");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Pure decision: whether the route detail panel should offer a "Re-scan for
        /// endpoint" button. True only for a recoverable SURFACE endpoint loss, i.e.
        /// <paramref name="status"/> == <see cref="RouteStatus.EndpointLost"/> AND the
        /// endpoint is a surface endpoint with a known body. The surface-proximity
        /// fallback in RouteEndpointResolver can only re-find a SURFACE target; an
        /// orbital endpoint loss re-runs the identical baked-PID lookup, so re-scan
        /// cannot recover it (the caller shows a disabled note instead, see
        /// <see cref="RescanIneligibleReason"/>).
        /// </summary>
        internal static bool ShouldOfferEndpointRescan(RouteStatus status, RouteEndpoint endpoint)
        {
            return status == RouteStatus.EndpointLost
                && endpoint.IsSurface
                && !string.IsNullOrEmpty(endpoint.BodyName);
        }

        /// <summary>
        /// The disabled-note explanation when a re-scan is not offered for an
        /// EndpointLost route. An orbital endpoint (not surface) can only be matched
        /// by its baked target PID, so re-scan cannot help: the player must re-create
        /// the route. A surface endpoint with no body name carries no anchor for the
        /// surface-proximity fallback. Both cases steer the player to re-create.
        /// </summary>
        internal static string RescanIneligibleReason(RouteEndpoint endpoint)
        {
            if (!endpoint.IsSurface)
                return "Orbital endpoint: only the baked target PID can be matched. "
                    + "Re-create the route to point at a new vessel.";
            return "Endpoint has no body reference to re-scan. "
                + "Re-create the route to point at a new vessel.";
        }
    }
}
