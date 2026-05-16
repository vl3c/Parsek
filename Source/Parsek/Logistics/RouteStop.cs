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
        /// 0-based source recording whose completion UT triggers this stop;
        /// <c>-1</c> when not yet assigned (multi-stop default).
        /// </summary>
        public int SegmentIndexBefore = -1;

        /// <summary>Seconds from <c>CurrentCycleStartUT</c> to this stop boundary.</summary>
        public double DeliveryOffsetSeconds;
    }
}
