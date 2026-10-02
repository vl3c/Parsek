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

            /// <summary>Game UT of the latest row (the last delivery); NaN when none. The
            /// ledger is the only record of when a route delivered: the route stores no
            /// delivered UT.</summary>
            internal double LastUt { get; set; } = double.NaN;
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
                    summary.LastUt = row.Ut;
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
        // L3: Candidates section "Would deliver" cell formatter.
        // ------------------------------------------------------------------

        /// <summary>
        /// Formats a candidate's would-deliver-per-cycle manifest for the L3 Candidates
        /// section "Would deliver" column (and reused by the candidate detail line). The
        /// inputs are the candidate analysis's already-built resource + inventory
        /// delivery manifests; a null resource map and a null / empty inventory list
        /// yield "(nothing)". Resources render as "&lt;key&gt; &lt;F1 amount&gt;"
        /// comma-joined (full stock key, no abbreviation) in dictionary order, followed
        /// by "&lt;N&gt; inventory item(s)" when any inventory payload is present. F1 +
        /// InvariantCulture so comma-locale systems render identically. Mirrors the
        /// window's prior private FormatManifest exactly so the rendered cell text does
        /// not change. Pure and Unity-free for unit testing.
        /// </summary>
        internal static string FormatWouldDeliver(
            IReadOnlyDictionary<string, double> resources,
            IReadOnlyList<InventoryPayloadItem> inventory)
        {
            var sb = new StringBuilder();
            if (resources != null)
            {
                foreach (KeyValuePair<string, double> kv in resources)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(kv.Key).Append(' ')
                      .Append(kv.Value.ToString("F1", CultureInfo.InvariantCulture));
                }
            }
            int invCount = inventory?.Count ?? 0;
            if (invCount > 0)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(invCount.ToString(CultureInfo.InvariantCulture)).Append(" inventory item(s)");
            }
            return sb.Length > 0 ? sb.ToString() : "(nothing)";
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

        // ------------------------------------------------------------------
        // GUI-P20: multi-stop routes. A route built from a multi-window Supply
        // Run carries one RouteStop per dock window (RouteBuilder), and the
        // engine fires EVERY stop each cycle: each stop's own DeliveryManifest
        // lands at that stop's endpoint (RouteDeliveryPlanner per stop index),
        // the capacity gate checks every stop (RouteDestinationCapacityCheck),
        // and the endpoint gate resolves every stop (RouteDispatchEvaluator).
        // These helpers make the window cells say the same thing. A single-stop
        // route takes the exact pre-P20 path in every helper, so its text is
        // byte-identical.
        // ------------------------------------------------------------------

        /// <summary>
        /// The route detail panel's "Delivers per cycle" value. A single-stop route
        /// renders <see cref="FormatWouldDeliver"/> over that stop's own manifests
        /// (null stop: "-"), exactly as before. A multi-stop route SUMS each resource
        /// across every stop's delivery manifest (each stop's amount really lands each
        /// cycle, so the per-cycle total is the sum) in first-seen key order, counts
        /// the inventory items of every stop, and appends " across N stops" when more
        /// than one stop receives cargo. Pickup manifests are not deliveries and are
        /// not counted, matching the single-stop cell. No non-null stop: "-".
        /// </summary>
        internal static string FormatRouteDeliveryPerCycle(IReadOnlyList<RouteStop> stops)
        {
            if (stops == null || stops.Count == 0) return "-";
            if (stops.Count == 1)
            {
                RouteStop only = stops[0];
                if (only == null) return "-";
                return FormatWouldDeliver(only.DeliveryManifest, only.InventoryDeliveryManifest);
            }

            // Insert-only dictionary: enumeration follows first-seen key order.
            var resources = new Dictionary<string, double>();
            var inventory = new List<InventoryPayloadItem>();
            int nonNullStops = 0;
            int deliveringStops = 0;
            for (int i = 0; i < stops.Count; i++)
            {
                RouteStop stop = stops[i];
                if (stop == null) continue;
                nonNullStops++;
                if (StopDelivers(stop)) deliveringStops++;
                if (stop.DeliveryManifest != null)
                {
                    foreach (KeyValuePair<string, double> kv in stop.DeliveryManifest)
                    {
                        if (kv.Key == null) continue;
                        resources.TryGetValue(kv.Key, out double sum);
                        resources[kv.Key] = sum + kv.Value;
                    }
                }
                if (stop.InventoryDeliveryManifest != null)
                    inventory.AddRange(stop.InventoryDeliveryManifest);
            }
            if (nonNullStops == 0) return "-";

            string text = FormatWouldDeliver(resources, inventory);
            if (deliveringStops > 1)
                text += " across " + deliveringStops.ToString(CultureInfo.InvariantCulture) + " stops";
            return text;
        }

        /// <summary>True when the stop carries any resource or inventory delivery.</summary>
        internal static bool StopDelivers(RouteStop stop)
        {
            return stop != null
                && ((stop.DeliveryManifest != null && stop.DeliveryManifest.Count > 0)
                    || (stop.InventoryDeliveryManifest != null && stop.InventoryDeliveryManifest.Count > 0));
        }

        /// <summary>True when the stop carries any resource or inventory pickup.</summary>
        internal static bool StopPicksUp(RouteStop stop)
        {
            return stop != null
                && ((stop.PickupManifest != null && stop.PickupManifest.Count > 0)
                    || (stop.InventoryPickupManifest != null && stop.InventoryPickupManifest.Count > 0));
        }

        /// <summary>Number of non-null stops on a route.</summary>
        internal static int CountStops(IReadOnlyList<RouteStop> stops)
        {
            if (stops == null) return 0;
            int n = 0;
            for (int i = 0; i < stops.Count; i++)
                if (stops[i] != null) n++;
            return n;
        }

        /// <summary>
        /// Which stop the Destination cell names. A single-stop route: index 0 (or -1
        /// when that stop is null), as before. A multi-stop route: the first stop that
        /// RECEIVES cargo, because the column is where the cargo goes and a relay run
        /// commonly visits its pickup source first (the rover-relay route is
        /// pickup-at-B then deliver-at-A); with no delivering stop, the first non-null
        /// stop. -1 when there is no non-null stop.
        /// </summary>
        internal static int ResolveDestinationStopIndex(IReadOnlyList<RouteStop> stops)
        {
            if (stops == null || stops.Count == 0) return -1;
            if (stops.Count == 1) return stops[0] != null ? 0 : -1;
            int firstNonNull = -1;
            for (int i = 0; i < stops.Count; i++)
            {
                if (stops[i] == null) continue;
                if (firstNonNull < 0) firstNonNull = i;
                if (StopDelivers(stops[i])) return i;
            }
            return firstNonNull;
        }

        /// <summary>
        /// The Destination cell text: the named stop's text, plus "(+N stops)" for
        /// the route's other stops when it has more than one ("Mun Base (+2 stops)",
        /// "Mun Base (+1 stop)"). A single-stop route returns the text unchanged.
        /// </summary>
        internal static string FormatMultiStopDestination(string primaryText, int stopCount)
        {
            if (stopCount <= 1) return primaryText;
            string text = string.IsNullOrEmpty(primaryText) ? "-" : primaryText;
            int others = stopCount - 1;
            return text + " (+" + others.ToString(CultureInfo.InvariantCulture)
                + (others == 1 ? " stop)" : " stops)");
        }

        /// <summary>The cargo direction of one stop for the stop-list tooltip.</summary>
        internal static string StopRoleLabel(RouteStop stop)
        {
            bool delivers = StopDelivers(stop);
            bool picksUp = StopPicksUp(stop);
            if (delivers && picksUp) return "pickup + delivery";
            if (delivers) return "delivery";
            if (picksUp) return "pickup";
            return "no cargo";
        }

        /// <summary>
        /// The multi-stop Destination cell tooltip: every stop in visit order on ONE
        /// line (the Logistics hover strip is single-line and rejects hard newlines),
        /// e.g. "Stops in order: 1. B (pickup), 2. A (delivery)". The display text per
        /// stop is the resolved vessel name or the coords fallback, supplied by the
        /// caller; a null entry renders "-". Returns empty for fewer than two entries
        /// (the single-stop tooltip keeps its own coords contract).
        /// <para>Capped to <paramref name="maxChars"/> (the window passes its strip
        /// budget, <see cref="TooltipEchoBox.BudgetChars"/>; 0 or less means no cap):
        /// when the whole list does not fit it keeps as many leading stops as fit and
        /// ends with ", ... +N more". If not even the first stop fits beside that
        /// suffix, it renders "Stops in order: ... +N more" alone, so the result never
        /// runs past the strip for any realistic budget.</para>
        /// </summary>
        internal static string FormatStopListTooltip(
            IReadOnlyList<string> stopTexts, IReadOnlyList<string> stopRoles, int maxChars)
        {
            if (stopTexts == null || stopTexts.Count < 2) return string.Empty;
            const string Prefix = "Stops in order: ";
            int count = stopTexts.Count;
            var entries = new string[count];
            for (int i = 0; i < count; i++)
            {
                var e = new StringBuilder();
                e.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(". ")
                 .Append(string.IsNullOrEmpty(stopTexts[i]) ? "-" : stopTexts[i]);
                string role = stopRoles != null && i < stopRoles.Count ? stopRoles[i] : null;
                if (!string.IsNullOrEmpty(role))
                    e.Append(" (").Append(role).Append(')');
                entries[i] = e.ToString();
            }

            string full = Prefix + string.Join(", ", entries);
            if (maxChars <= 0 || full.Length <= maxChars)
                return full;

            for (int shown = count - 1; shown >= 0; shown--)
            {
                var sb = new StringBuilder(Prefix);
                for (int i = 0; i < shown; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(entries[i]);
                }
                if (shown > 0) sb.Append(", ");
                sb.Append("... +").Append((count - shown).ToString(CultureInfo.InvariantCulture))
                  .Append(" more");
                if (sb.Length <= maxChars || shown == 0)
                    return sb.ToString();
            }
            return full; // unreachable: shown == 0 always returns
        }

        /// <summary>
        /// The requested amounts the DestinationFull capacity line compares against
        /// for the stop the capacity gate found full. Mirrors
        /// <see cref="RouteDestinationCapacityCheck.HasCapacityForAllStops"/>: stops
        /// that resolve to the SAME destination vessel share capacity, and the gate
        /// accumulates every such stop up to and including the full one, so the line
        /// sums each resource over stops 0..<paramref name="fullStopIndex"/> for which
        /// <paramref name="sharesDestination"/> is true (the full stop itself always
        /// counts). Null when the index is out of range or nothing is requested.
        /// </summary>
        internal static Dictionary<string, double> CombineRequestedForFullStop(
            IReadOnlyList<RouteStop> stops, int fullStopIndex, System.Func<int, bool> sharesDestination)
        {
            if (stops == null || fullStopIndex < 0 || fullStopIndex >= stops.Count)
                return null;
            var combined = new Dictionary<string, double>();
            for (int i = 0; i <= fullStopIndex; i++)
            {
                RouteStop stop = stops[i];
                if (stop?.DeliveryManifest == null) continue;
                bool counts = i == fullStopIndex || (sharesDestination != null && sharesDestination(i));
                if (!counts) continue;
                foreach (KeyValuePair<string, double> kv in stop.DeliveryManifest)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    combined.TryGetValue(kv.Key, out double sum);
                    combined[kv.Key] = sum + kv.Value;
                }
            }
            return combined.Count > 0 ? combined : null;
        }

        /// <summary>
        /// Whether the EndpointLost re-scan button is offered for the whole route: true
        /// when any stop's endpoint is a recoverable surface endpoint
        /// (<see cref="ShouldOfferEndpointRescan"/>). For a single-stop route this is
        /// exactly the per-endpoint decision. The click re-scans every stop.
        /// </summary>
        internal static bool ShouldOfferRouteEndpointRescan(RouteStatus status, IReadOnlyList<RouteStop> stops)
        {
            if (stops == null) return false;
            for (int i = 0; i < stops.Count; i++)
            {
                RouteStop stop = stops[i];
                if (stop != null && ShouldOfferEndpointRescan(status, stop.Endpoint))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The disabled-note explanation when no stop is re-scannable: the first
        /// non-null stop's <see cref="RescanIneligibleReason"/> (a single-stop route
        /// gets exactly its own reason). Null when there is no non-null stop.
        /// </summary>
        internal static string RouteRescanIneligibleReason(IReadOnlyList<RouteStop> stops)
        {
            if (stops == null) return null;
            for (int i = 0; i < stops.Count; i++)
                if (stops[i] != null) return RescanIneligibleReason(stops[i].Endpoint);
            return null;
        }

        /// <summary>One stop's outcome from a multi-stop endpoint re-scan.</summary>
        internal readonly struct RescanStopResult
        {
            internal RescanStopResult(int stopIndex, bool resolved, string vesselName, string reason)
            {
                StopIndex = stopIndex;
                Resolved = resolved;
                VesselName = vesselName;
                Reason = reason;
            }

            internal int StopIndex { get; }
            internal bool Resolved { get; }
            internal string VesselName { get; }
            internal string Reason { get; }
        }

        /// <summary>True when every re-scanned stop resolved (and at least one was scanned).</summary>
        internal static bool AllStopsResolved(IReadOnlyList<RescanStopResult> results)
        {
            if (results == null || results.Count == 0) return false;
            for (int i = 0; i < results.Count; i++)
                if (!results[i].Resolved) return false;
            return true;
        }

        /// <summary>
        /// The per-stop body of the multi-stop re-scan log line, 1-based stop numbers:
        /// "stops=2 resolved=1/2 [1:'Mun Base' 2:unresolved('no-vessel-near')]".
        /// </summary>
        internal static string FormatRescanOutcome(IReadOnlyList<RescanStopResult> results)
        {
            int total = results?.Count ?? 0;
            int resolved = 0;
            var sb = new StringBuilder();
            for (int i = 0; i < total; i++)
            {
                RescanStopResult r = results[i];
                if (i > 0) sb.Append(' ');
                sb.Append((r.StopIndex + 1).ToString(CultureInfo.InvariantCulture)).Append(':');
                if (r.Resolved)
                {
                    resolved++;
                    sb.Append('\'').Append(r.VesselName ?? string.Empty).Append('\'');
                }
                else
                {
                    sb.Append("unresolved('").Append(r.Reason ?? string.Empty).Append("')");
                }
            }
            return "stops=" + total.ToString(CultureInfo.InvariantCulture)
                + " resolved=" + resolved.ToString(CultureInfo.InvariantCulture)
                + "/" + total.ToString(CultureInfo.InvariantCulture)
                + " [" + sb + "]";
        }
    }
}
