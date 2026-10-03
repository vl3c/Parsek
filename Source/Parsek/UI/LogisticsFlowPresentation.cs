using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// Pure collection of a route's cargo ledger rows (RouteCargoDebited /
    /// RouteCargoPickedUp / RouteCargoDelivered, keyed by RouteCycleId +
    /// RouteStopIndex) in ONE ELS walk: the delivery-summary rows behind the
    /// Logistics detail block's "Last delivered" line, and optionally the full
    /// per-row flow records. All methods are Unity-free and side-effect-free so they
    /// are unit tested directly off the IMGUI path (mirrors
    /// <see cref="LogisticsDeliveryPresentation"/> and
    /// <see cref="LogisticsHoldPresentation"/>). InvariantCulture for every
    /// numeric piece so comma-locale systems render identically.
    /// </summary>
    internal static class LogisticsFlowPresentation
    {
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
        /// ELS walk.
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
    }
}
