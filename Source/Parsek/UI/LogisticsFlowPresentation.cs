using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// Pure presentation helpers for the Logistics window's M6 per-cycle flow
    /// display: what each completed route cycle debited where, picked up where,
    /// and delivered where, so the crashed-disposition economics of a route
    /// (design 19.2.5 "a crashed run still debits") are a player-visible fact.
    /// All data comes from ledger rows the route system already writes
    /// (RouteCargoDebited / RouteCargoPickedUp / RouteCargoDelivered, keyed by
    /// RouteCycleId + RouteStopIndex) - no new ledger fields, no new rows, and
    /// the collection shares the ONE ELS walk the H2/H3 delivery summary
    /// already does. All methods are Unity-free and side-effect-free so they
    /// are unit tested directly off the IMGUI path (mirrors
    /// <see cref="LogisticsDeliveryPresentation"/> and
    /// <see cref="LogisticsHoldPresentation"/>). InvariantCulture for every
    /// numeric piece so comma-locale systems render identically.
    /// </summary>
    internal static class LogisticsFlowPresentation
    {
        /// <summary>
        /// How many most-recent cycles the detail panel shows. Bounded by design:
        /// the flow display is a "what has this route been doing lately" readout,
        /// not a scrollable history (the ledger keeps the full record).
        /// </summary>
        internal const int MaxCyclesShown = 5;

        /// <summary>Header line drawn above the per-cycle lines.</summary>
        internal const string RecentCyclesHeader = "Recent cycles:";

        /// <summary>Which route cargo row a <see cref="FlowRow"/> came from.</summary>
        internal enum FlowRowKind
        {
            /// <summary>RouteCargoDebited: the dispatch-time origin debit (funds or physical).</summary>
            Debited = 0,

            /// <summary>RouteCargoPickedUp: cargo loaded from a pickup stop onto the transport.</summary>
            PickedUp = 1,

            /// <summary>RouteCargoDelivered: cargo delivered at a stop.</summary>
            Delivered = 2,
        }

        /// <summary>
        /// One route cargo ledger row reduced to the fields the per-cycle flow
        /// display needs. Built by <see cref="CollectRows"/> during the shared
        /// ELS walk; consumed by <see cref="FormatPerCycleFlow"/>.
        /// </summary>
        internal readonly struct FlowRow
        {
            internal FlowRow(
                FlowRowKind kind,
                string cycleId,
                int stopIndex,
                double ut,
                int sequence,
                IReadOnlyDictionary<string, double> actual,
                IReadOnlyDictionary<string, double> requested,
                uint endpointPid,
                float kscFundsCost,
                int inventoryCount,
                int requestedInventoryCount)
            {
                Kind = kind;
                CycleId = cycleId;
                StopIndex = stopIndex;
                Ut = ut;
                Sequence = sequence;
                Actual = actual;
                Requested = requested;
                EndpointPid = endpointPid;
                KscFundsCost = kscFundsCost;
                InventoryCount = inventoryCount;
                RequestedInventoryCount = requestedInventoryCount;
            }

            internal FlowRowKind Kind { get; }

            /// <summary>Groups the row into its dispatch cycle; null on legacy rows (skipped).</summary>
            internal string CycleId { get; }

            /// <summary>0-based stop index; -1 means route-level / legacy (treated as stop 0).</summary>
            internal int StopIndex { get; }

            internal double Ut { get; }

            /// <summary>Ledger sequence; orders same-UT rows (dispatch/debit/pickup/delivery stride).</summary>
            internal int Sequence { get; }

            /// <summary>Actual manifest (positive magnitudes); null/empty when nothing moved.</summary>
            internal IReadOnlyDictionary<string, double> Actual { get; }

            /// <summary>Requested manifest, populated only on a shortfall; null on a full fill.</summary>
            internal IReadOnlyDictionary<string, double> Requested { get; }

            /// <summary>Origin/pickup endpoint pid (RouteOriginVesselPid); 0 on KSC / legacy rows.</summary>
            internal uint EndpointPid { get; }

            /// <summary>KSC funds charge on a debited row; 0 elsewhere.</summary>
            internal float KscFundsCost { get; }

            /// <summary>Count of stored-part inventory payloads moved on this row.</summary>
            internal int InventoryCount { get; }

            /// <summary>
            /// Count of requested stored-part inventory payloads, populated only
            /// on an inventory shortfall (the inventory analogue of
            /// <see cref="Requested"/>); 0 on a full fill.
            /// </summary>
            internal int RequestedInventoryCount { get; }
        }

        /// <summary>One rendered per-cycle line plus its shortfall tint flag.</summary>
        internal readonly struct CycleFlowLine
        {
            internal CycleFlowLine(string text, bool shortfall)
            {
                Text = text;
                Shortfall = shortfall;
            }

            /// <summary>The full line, e.g. "Cycle 3 (2.1h ago): paid 500 funds at KSC; delivered 150.0 LiquidFuel to Munar Station".</summary>
            internal string Text { get; }

            /// <summary>True when any row in the cycle recorded a shortfall (drives the yellow tint).</summary>
            internal bool Shortfall { get; }
        }

        /// <summary>
        /// ONE ledger walk for the route's cargo rows: fills BOTH the existing
        /// H2/H3 delivery-summary rows (under conditions byte-identical to the
        /// pre-M6 <c>CollectRouteDeliverySummary</c> loop: RouteCargoDelivered
        /// rows matched by RouteId, ordinal) AND the per-cycle flow rows for all
        /// three route cargo row types. Callers pass the memoized
        /// <c>EffectiveState.ComputeELS()</c> list (gate-safe, tombstone-filtered);
        /// this helper never touches the raw ledger. Null <paramref name="els"/>
        /// or null/empty <paramref name="routeId"/> is a no-op; either output
        /// list may be null when the caller only wants the other. Pure: callers
        /// own any logging.
        /// </summary>
        internal static void CollectRows(
            IReadOnlyList<GameAction> els,
            string routeId,
            List<LogisticsDeliveryPresentation.DeliveryRow> deliveryRows,
            List<FlowRow> flowRows)
        {
            if (els == null || string.IsNullOrEmpty(routeId))
                return;

            for (int i = 0; i < els.Count; i++)
            {
                GameAction a = els[i];
                if (a == null) continue;

                bool isDelivered = a.Type == GameActionType.RouteCargoDelivered;
                bool isDebited = a.Type == GameActionType.RouteCargoDebited;
                bool isPickedUp = a.Type == GameActionType.RouteCargoPickedUp;
                if (!isDelivered && !isDebited && !isPickedUp) continue;
                if (!string.Equals(a.RouteId, routeId, System.StringComparison.Ordinal)) continue;

                // Existing H2/H3 summary input: delivered rows only, exactly the
                // pre-M6 conditions, so SummarizeRouteDeliveries output is
                // byte-identical (pinned by LogisticsFlowPresentationTests).
                if (isDelivered && deliveryRows != null)
                {
                    deliveryRows.Add(new LogisticsDeliveryPresentation.DeliveryRow(
                        a.RouteResourceManifest, a.RouteRequestedResourceManifest, a.UT));
                }

                if (flowRows != null)
                {
                    FlowRowKind kind = isDelivered
                        ? FlowRowKind.Delivered
                        : isDebited ? FlowRowKind.Debited : FlowRowKind.PickedUp;
                    flowRows.Add(new FlowRow(
                        kind, a.RouteCycleId, a.RouteStopIndex, a.UT, a.Sequence,
                        a.RouteResourceManifest, a.RouteRequestedResourceManifest,
                        a.RouteOriginVesselPid, a.RouteKscFundsCost,
                        a.RouteInventoryManifest?.Count ?? 0,
                        a.RouteRequestedInventoryManifest?.Count ?? 0));
                }
            }
        }

        /// <summary>
        /// Reduces a route's flow rows to one compact player-language line per
        /// dispatch cycle, newest first, bounded to the last
        /// <paramref name="maxCycles"/> cycles (older cycles are simply not
        /// shown). Rows are grouped by <see cref="FlowRow.CycleId"/> (rows with
        /// no cycle id are skipped - every current emit site sets one); cycles
        /// are ordered by their earliest row UT and numbered 1-based across ALL
        /// cycles found, so the ordinal keeps growing as the route runs. Within
        /// a cycle, segments follow (UT, Sequence, StopIndex) order, which is
        /// the ledger's own dispatch/debit/pickup/delivery stride, so a
        /// multi-stop cycle reads its stops in firing order under ONE line.
        /// <para>Endpoint display names: <paramref name="endpointNames"/> maps a
        /// live-resolved pid to its vessel name; a vanished endpoint misses the
        /// map and renders as "vessel pid=N" (never blank).
        /// <paramref name="originFallback"/> names a pid-less debit source (the
        /// route's origin cell text, e.g. "KSC (funds)");
        /// <paramref name="stopDestinationNames"/> is indexed by stop index for
        /// delivery destinations (out-of-range falls back to stop 0, then
        /// "the destination").</para>
        /// <para>An empty result (no rows / no cycle ids) means the caller
        /// renders nothing - no header, per the zero-completed-cycles rule.</para>
        /// </summary>
        internal static List<CycleFlowLine> FormatPerCycleFlow(
            IReadOnlyList<FlowRow> rows,
            IReadOnlyDictionary<uint, string> endpointNames,
            string originFallback,
            IReadOnlyList<string> stopDestinationNames,
            double currentUT,
            int maxCycles)
        {
            var lines = new List<CycleFlowLine>();
            if (rows == null || rows.Count == 0 || maxCycles <= 0)
                return lines;

            // Group rows by cycle id, tracking each cycle's earliest row UT for
            // ordering. Insertion order of the dictionary is not relied on.
            var byCycle = new Dictionary<string, List<FlowRow>>(System.StringComparer.Ordinal);
            var minUtByCycle = new Dictionary<string, double>(System.StringComparer.Ordinal);
            var cycleIds = new List<string>();
            for (int i = 0; i < rows.Count; i++)
            {
                FlowRow row = rows[i];
                if (string.IsNullOrEmpty(row.CycleId)) continue;
                if (!byCycle.TryGetValue(row.CycleId, out List<FlowRow> bucket))
                {
                    bucket = new List<FlowRow>();
                    byCycle[row.CycleId] = bucket;
                    minUtByCycle[row.CycleId] = row.Ut;
                    cycleIds.Add(row.CycleId);
                }
                else if (row.Ut < minUtByCycle[row.CycleId])
                {
                    minUtByCycle[row.CycleId] = row.Ut;
                }
                bucket.Add(row);
            }
            if (cycleIds.Count == 0)
                return lines;

            // Order cycles by earliest row UT ascending (cycle id ordinal as the
            // deterministic tiebreak); ordinal = 1-based position in that order.
            cycleIds.Sort((idA, idB) =>
            {
                int cmp = minUtByCycle[idA].CompareTo(minUtByCycle[idB]);
                return cmp != 0 ? cmp : string.CompareOrdinal(idA, idB);
            });

            int first = cycleIds.Count > maxCycles ? cycleIds.Count - maxCycles : 0;

            // Newest first: walk the kept window from the end.
            for (int c = cycleIds.Count - 1; c >= first; c--)
            {
                List<FlowRow> bucket = byCycle[cycleIds[c]];
                lines.Add(FormatCycleLine(c + 1, bucket, endpointNames,
                    originFallback, stopDestinationNames, currentUT));
            }
            return lines;
        }

        /// <summary>
        /// True when the row recorded any shortfall: a per-resource requested
        /// amount exceeding the actual, OR a requested-inventory manifest
        /// (populated only when the stored-part pickup/debit came up short, so
        /// presence alone is the signal - entry counts can match on a
        /// quantity-level shortfall within one identity).
        /// </summary>
        private static bool RowHasShortfall(FlowRow row)
        {
            return LogisticsDeliveryPresentation.HasShortfall(row.Requested, row.Actual)
                || row.RequestedInventoryCount > 0;
        }

        /// <summary>
        /// Formats one cycle's line: "Cycle {ordinal} ({age} ago): {segments}".
        /// The age suffix uses the cycle's LATEST row UT (when the cycle's last
        /// recorded event happened) and is omitted when unknown/invalid,
        /// mirroring the hold line's age contract. Segments join with "; ";
        /// a cycle whose rows all reduce to nothing renders "(nothing recorded)"
        /// so the line is never blank.
        /// </summary>
        private static CycleFlowLine FormatCycleLine(
            int ordinal,
            List<FlowRow> bucket,
            IReadOnlyDictionary<uint, string> endpointNames,
            string originFallback,
            IReadOnlyList<string> stopDestinationNames,
            double currentUT)
        {
            // Ledger stride order: (UT, Sequence, StopIndex) - the same total
            // order the recalculation walkers use, so multi-stop segments read
            // in firing order.
            bucket.Sort((a, b) =>
            {
                int cmp = a.Ut.CompareTo(b.Ut);
                if (cmp != 0) return cmp;
                cmp = a.Sequence.CompareTo(b.Sequence);
                if (cmp != 0) return cmp;
                return a.StopIndex.CompareTo(b.StopIndex);
            });

            bool shortfall = false;
            double maxUt = double.MinValue;
            var segments = new List<string>();
            for (int i = 0; i < bucket.Count; i++)
            {
                FlowRow row = bucket[i];
                if (row.Ut > maxUt) maxUt = row.Ut;
                if (RowHasShortfall(row))
                    shortfall = true;

                switch (row.Kind)
                {
                    case FlowRowKind.Debited:
                        AppendDebitSegments(segments, row, endpointNames, originFallback);
                        break;
                    case FlowRowKind.PickedUp:
                        segments.Add(FormatPickupSegment(row, endpointNames));
                        break;
                    case FlowRowKind.Delivered:
                        segments.Add(FormatDeliverySegment(row, stopDestinationNames));
                        break;
                }
            }

            var sb = new StringBuilder();
            sb.Append("Cycle ").Append(ordinal.ToString(CultureInfo.InvariantCulture));
            double age = currentUT - maxUt;
            if (maxUt > double.MinValue && age >= 0.0)
            {
                string ageText = LogisticsWindowUI.FormatDuration(age);
                if (ageText != "-")
                    sb.Append(" (").Append(ageText).Append(" ago)");
            }
            sb.Append(": ");
            if (segments.Count == 0)
                sb.Append("(nothing recorded)");
            else
                sb.Append(string.Join("; ", segments));

            return new CycleFlowLine(sb.ToString(), shortfall);
        }

        /// <summary>
        /// Debit segments for one RouteCargoDebited row. A KSC funds charge
        /// (funds &gt; 0, pid 0) renders "paid {funds} funds at KSC" and
        /// deliberately SKIPS the row's resource manifest: on the KSC path that
        /// manifest is the informational cost manifest (nothing was physically
        /// removed anywhere - the delivery segment shows what arrived), so
        /// rendering it as a debit would double-count. A physical debit
        /// (pid != 0, or a pid-less legacy row with no funds) renders
        /// "took {amounts} from {source}" with the shortfall clause when the
        /// origin came up short. An empty debit row (no funds, no amounts)
        /// emits nothing.
        /// </summary>
        private static void AppendDebitSegments(
            List<string> segments,
            FlowRow row,
            IReadOnlyDictionary<uint, string> endpointNames,
            string originFallback)
        {
            bool kscFunds = row.KscFundsCost > 0f && row.EndpointPid == 0u;
            if (row.KscFundsCost > 0f)
            {
                segments.Add(string.Format(CultureInfo.InvariantCulture,
                    "paid {0:F0} funds at KSC", row.KscFundsCost));
            }

            if (kscFunds)
                return;

            string amounts = FormatAmounts(
                row.Actual, row.Requested, row.InventoryCount, row.RequestedInventoryCount);
            if (amounts == null)
                return;

            string source = row.EndpointPid != 0u
                ? ResolveEndpointName(row.EndpointPid, endpointNames)
                : (string.IsNullOrEmpty(originFallback) ? "the origin" : originFallback);

            string segment = "took " + amounts + " from " + source;
            if (RowHasShortfall(row))
                segment += " (origin was short)";
            segments.Add(segment);
        }

        private static string FormatPickupSegment(
            FlowRow row,
            IReadOnlyDictionary<uint, string> endpointNames)
        {
            string amounts = FormatAmounts(
                row.Actual, row.Requested, row.InventoryCount, row.RequestedInventoryCount)
                ?? "nothing";
            string source = row.EndpointPid != 0u
                ? ResolveEndpointName(row.EndpointPid, endpointNames)
                : "an unresolved source";
            string segment = "picked up " + amounts + " from " + source;
            if (RowHasShortfall(row))
                segment += " (source was short)";
            return segment;
        }

        private static string FormatDeliverySegment(
            FlowRow row,
            IReadOnlyList<string> stopDestinationNames)
        {
            string amounts = FormatAmounts(
                row.Actual, row.Requested, row.InventoryCount, row.RequestedInventoryCount)
                ?? "nothing";
            string dest = ResolveStopDestination(row.StopIndex, stopDestinationNames);
            string segment = "delivered " + amounts + " to " + dest;
            if (RowHasShortfall(row))
                segment += " (some cargo did not fit)";
            return segment;
        }

        /// <summary>
        /// Resolves a pid to its display name: the live vessel name when the
        /// caller resolved one, else "vessel pid=N" so a vanished endpoint is
        /// still identified, never blank.
        /// </summary>
        private static string ResolveEndpointName(
            uint pid, IReadOnlyDictionary<uint, string> endpointNames)
        {
            if (endpointNames != null
                && endpointNames.TryGetValue(pid, out string name)
                && !string.IsNullOrEmpty(name))
                return name;
            return "vessel pid=" + pid.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Picks the delivery destination display name by stop index. A legacy
        /// -1 / out-of-range index falls back to stop 0's name (v1 single-stop
        /// rows), then to "the destination" so the segment is never blank.
        /// </summary>
        private static string ResolveStopDestination(
            int stopIndex, IReadOnlyList<string> stopDestinationNames)
        {
            if (stopDestinationNames != null && stopDestinationNames.Count > 0)
            {
                int idx = stopIndex >= 0 && stopIndex < stopDestinationNames.Count
                    ? stopIndex
                    : 0;
                string name = stopDestinationNames[idx];
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            return "the destination";
        }

        /// <summary>
        /// Formats one row's moved amounts: the union of actual + requested
        /// resource keys sorted ordinal (stable across refreshes), each as
        /// "{actual} {key}" on a full fill or "{actual} of {requested} {key}"
        /// on a shortfall (F1 + InvariantCulture, full stock keys - the
        /// <see cref="LogisticsDeliveryPresentation.FormatRealizedDelivery"/>
        /// number idiom), plus "N inventory item(s)" when stored parts moved
        /// (the FormatWouldDeliver idiom) or "K of N inventory item(s)" when
        /// the requested-inventory entry count exceeds the actual (the resource
        /// shortfall idiom; a fully blocked pickup renders "0 of N"). Returns
        /// null when nothing moved and nothing was requested, so callers can
        /// skip or say "nothing".
        /// </summary>
        private static string FormatAmounts(
            IReadOnlyDictionary<string, double> actual,
            IReadOnlyDictionary<string, double> requested,
            int inventoryCount,
            int requestedInventoryCount)
        {
            var keys = new List<string>();
            if (actual != null)
            {
                foreach (string key in actual.Keys)
                    keys.Add(key);
            }
            if (requested != null)
            {
                foreach (string key in requested.Keys)
                {
                    if (actual == null || !actual.ContainsKey(key))
                        keys.Add(key);
                }
            }
            keys.Sort(System.StringComparer.Ordinal);

            var sb = new StringBuilder();
            foreach (string key in keys)
            {
                if (sb.Length > 0) sb.Append(", ");
                double act = 0.0;
                if (actual != null)
                    actual.TryGetValue(key, out act);
                double req = 0.0;
                bool hasReq = requested != null && requested.TryGetValue(key, out req);
                if (hasReq && req > act)
                {
                    sb.Append(act.ToString("F1", CultureInfo.InvariantCulture))
                      .Append(" of ")
                      .Append(req.ToString("F1", CultureInfo.InvariantCulture))
                      .Append(' ')
                      .Append(key);
                }
                else
                {
                    sb.Append(act.ToString("F1", CultureInfo.InvariantCulture))
                      .Append(' ')
                      .Append(key);
                }
            }

            if (inventoryCount > 0 || requestedInventoryCount > 0)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(inventoryCount.ToString(CultureInfo.InvariantCulture));
                if (requestedInventoryCount > inventoryCount)
                {
                    sb.Append(" of ")
                      .Append(requestedInventoryCount.ToString(CultureInfo.InvariantCulture));
                }
                sb.Append(" inventory item(s)");
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }
    }
}
