using System.Collections.Generic;

namespace Parsek.Logistics
{
    /// <summary>
    /// One delivery stop along a route. v1 routes have exactly one stop;
    /// the list shape stays now so multi-stop save data does not need to be
    /// replaced later (design §4.6).
    /// </summary>
    internal sealed class RouteStop
    {
        /// <summary>Where this stop is (destination vessel + body-fixed coordinates).</summary>
        public RouteEndpoint Endpoint;

        /// <summary>How the Supply Run connected to this stop (DockingPort in v1).</summary>
        public RouteConnectionKind ConnectionKind;

        /// <summary>Per-resource delivery amounts (positive only in v1).</summary>
        public Dictionary<string, double> DeliveryManifest;

        /// <summary>Exact stored-part payloads delivered (v1 inventory delivery).</summary>
        public List<InventoryPayloadItem> InventoryDeliveryManifest;

        /// <summary>
        /// M3 pickup-direction resource amounts (plan D8): the exact mirror of
        /// <see cref="DeliveryManifest"/> for cargo that flowed FROM this stop's
        /// endpoint ONTO the transport across the connection window (the
        /// reverse of delivery). Populated from
        /// <c>RouteAnalysisResult.ResourceLoadManifest</c> at build time;
        /// null/empty for a pure-delivery stop. Sparse in the codec (a new
        /// <c>PICKUP_MANIFEST</c> node, omitted when empty), so a pre-M3 /
        /// delivery-only route writes nothing new and round-trips byte-identically.
        /// </summary>
        public Dictionary<string, double> PickupManifest;

        /// <summary>
        /// M3 pickup-direction stored-part payloads (plan D8): the exact mirror
        /// of <see cref="InventoryDeliveryManifest"/> for inventory loaded FROM
        /// this stop's endpoint ONTO the transport. The route-shape field lands
        /// in M3 Phase 2 alongside <see cref="PickupManifest"/>; the inventory
        /// pickup APPLIER (and the analysis term that fills this) is Phase 5, so
        /// in Phase 2 it stays null/empty for every built route. Sparse in the
        /// codec (a new <c>INVENTORY_PICKUP_MANIFEST</c> node, omitted when
        /// empty).
        /// </summary>
        public List<InventoryPayloadItem> InventoryPickupManifest;

        /// <summary>
        /// 0-based source recording whose completion UT triggers this stop;
        /// <c>-1</c> when not yet assigned (multi-stop default).
        /// </summary>
        public int SegmentIndexBefore = -1;

        /// <summary>Seconds from <c>CurrentCycleStartUT</c> to this stop boundary.</summary>
        public double DeliveryOffsetSeconds;

        /// <summary>
        /// M4a per-stop dock-phase UT (plan OQ3/D5): the recorded loop-clock UT
        /// this stop fires its delivery / debit against, the per-window analogue
        /// of the route-level <c>Route.RecordedDockUT</c>. Populated by
        /// <c>RouteBuilder</c> from the per-window <c>RouteAnalysisStop.DockUT</c>.
        /// <c>-1.0</c> when not set (a single-stop / pre-M4 route fires on the
        /// route-level scalar). Sparse in the codec (omitted when -1.0,
        /// empty -> -1.0 on load), so a single-stop / pre-M4 route writes no
        /// <c>recordedDockUT</c> STOP key and round-trips byte-identically.
        /// The per-window FIRING that reads this is Phase A3.
        /// </summary>
        public double RecordedDockUT = -1.0;

        /// <summary>
        /// M4a per-stop firing sub-gate (plan OQ3/D5): the last loop-cycle index
        /// at which this stop fired, so a partially-fired multi-stop cycle does
        /// not double-fire a window after save/reload. Left at the default this
        /// phase; the firing logic that drives it is Phase A3. Sparse in the
        /// codec (omitted when -1, empty -> -1 on load), so a single-stop / pre-M4
        /// route writes no <c>lastFiredCycleIndex</c> STOP key and round-trips
        /// byte-identically.
        /// </summary>
        public long LastFiredCycleIndex = -1;
    }
}
