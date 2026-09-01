using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Tests.Generators
{
    /// <summary>
    /// Shared factory for production-shaped <see cref="RouteConnectionWindow"/>
    /// fixtures, seeded from a REAL flight so a synthetic route test measures a
    /// shape the game actually produces.
    ///
    /// <para><b>Provenance of every default below</b> — save
    /// <c>logistics-rover-a</c>, flown 2026-08-30: a basic ground supply route,
    /// two rovers, KSC-Runway origin, surface dock on the Kerbin flats, fuel +
    /// inventory transfer. The recording tree root carries
    /// <c>launchSiteName="Runway"</c> / <c>StartBodyName="Kerbin"</c> and spans
    /// from UT <c>468.49999999986386</c> (<see cref="RoverRootSpanStartUT"/>);
    /// the dock-merged child carries the one connection window with
    /// <c>dockUT=513.539999999823</c>, <c>undockUT=594.27999999974952</c>,
    /// <c>TransferKind=DockingPort</c>, <c>transferTargetPid=2123618197</c>
    /// (the endpoint rover; the transport's own pid is <c>313889796</c>),
    /// <c>TransferEndpointSituation=1</c> (<c>Vessel.Situations.LANDED</c>) and
    /// <c>EndpointAtDock = {Kerbin, lat 0.0055, lon -74.726, alt 65.98,
    /// IsSurface=true}</c>. The delivery it witnessed was 97.6 LiquidFuel
    /// transport-&gt;endpoint plus one <c>evaChute</c> and one
    /// <c>evaScienceKit</c>.</para>
    ///
    /// <para><b>Scope.</b> This factory is for NEW tests. The ~16 hand-rolled
    /// window helpers already scattered across the logistics test files are
    /// deliberately left alone — rewriting them would be churn with no
    /// coverage gain.</para>
    /// </summary>
    internal static class RouteWindowFixtures
    {
        // ---- Ground-truth constants (save logistics-rover-a, 2026-08-30) ----

        /// <summary>Tree-root recording span start; the route's launch UT.</summary>
        internal const double RoverRootSpanStartUT = 468.49999999986386;

        /// <summary>Recorded dock UT of the surface transfer.</summary>
        internal const double RoverDockUT = 513.539999999823;

        /// <summary>Recorded undock UT of the surface transfer.</summary>
        internal const double RoverUndockUT = 594.27999999974952;

        /// <summary>Endpoint (receiving) rover's vessel persistentId.</summary>
        internal const uint RoverEndpointPid = 2123618197u;

        /// <summary>Transport (delivering) rover's vessel persistentId.</summary>
        internal const uint RoverTransportPid = 313889796u;

        /// <summary>Body the transfer happened on.</summary>
        internal const string RoverBodyName = "Kerbin";

        /// <summary>Endpoint latitude at dock.</summary>
        internal const double RoverEndpointLatitude = 0.0055;

        /// <summary>Endpoint longitude at dock.</summary>
        internal const double RoverEndpointLongitude = -74.726;

        /// <summary>Endpoint altitude at dock (metres).</summary>
        internal const double RoverEndpointAltitude = 65.98;

        /// <summary>Launch site recorded on the tree root.</summary>
        internal const string RoverLaunchSiteName = "Runway";

        /// <summary>
        /// <c>(int)Vessel.Situations.LANDED</c>. The endpoint-proof gate
        /// (<c>RouteAnalysisEngine.HasEndpointProof</c>) only requires
        /// <c>&gt;= 0</c>; the value is what makes this a SURFACE transfer
        /// rather than the ORBITING (4) endpoint every other KSC-origin
        /// fixture in the suite uses.
        /// </summary>
        internal const int LandedSituation = 1;

        /// <summary>The single resource the flight delivered.</summary>
        internal const string RoverDeliveredResourceName = "LiquidFuel";

        /// <summary>LiquidFuel delivered transport -&gt; endpoint.</summary>
        internal const double RoverLiquidFuelDelivered = 97.6;

        /// <summary>First delivered cargo item.</summary>
        internal const string RoverInventoryPartA = "evaChute";

        /// <summary>Second delivered cargo item.</summary>
        internal const string RoverInventoryPartB = "evaScienceKit";

        /// <summary>
        /// The LANDED endpoint captured at dock: real body-fixed coordinates
        /// plus <c>IsSurface = true</c>, which is what carries surface-ness
        /// onto the built <c>RouteStop.Endpoint</c>
        /// (<c>TransferEndpointSituation</c> itself is an analysis-gate input
        /// and is NOT persisted on the stop).
        /// </summary>
        internal static RouteEndpoint SurfaceEndpoint(
            uint endpointPid = RoverEndpointPid,
            string bodyName = RoverBodyName,
            double latitude = RoverEndpointLatitude,
            double longitude = RoverEndpointLongitude,
            double altitude = RoverEndpointAltitude)
        {
            return new RouteEndpoint
            {
                VesselPersistentId = endpointPid,
                BodyName = bodyName,
                Latitude = latitude,
                Longitude = longitude,
                Altitude = altitude,
                IsSurface = true
            };
        }

        /// <summary>
        /// One complete (dock + undock both recorded) surface-delivery window
        /// with a LANDED endpoint, ready for
        /// <c>RouteAnalysisEngine.AnalyzeTree</c> / <c>AnalyzeRecording</c>.
        /// Every default is the rover flight's measured value; every one is
        /// overridable so a test can move the window in time, re-point it at a
        /// different endpoint, or change what it carries.
        ///
        /// <para><b>Cargo corners.</b> The four resource manifests are authored
        /// so the delivery direction reads EXACTLY the requested amounts and
        /// the pickup direction reads nothing: per resource the transport holds
        /// the delivered amount at dock and none at undock, the endpoint holds
        /// none at dock and the delivered amount at undock. That makes
        /// <c>BuildResourceDeliveryManifest</c>'s <c>min(endpointGain,
        /// transportLoss)</c> come out at the requested amount with no
        /// rounding slack, and <c>BuildResourceLoadManifest</c> come out
        /// empty. Inventory mirrors it: each item sits in
        /// <c>DockTransportInventory</c> and reappears in
        /// <c>UndockEndpointInventory</c>, so the endpoint gains one slot per
        /// item (the built <c>SlotsTaken</c>).</para>
        ///
        /// <para><b>No run manifest.</b> The window carries no
        /// <c>RouteRunCargoManifest</c>, so the M2 harvest gain check stays on
        /// its legacy fallback and the M3 flow closure never engages — the
        /// window is judged on its own four corners, which is what a plain
        /// ground supply run is.</para>
        /// </summary>
        /// <param name="windowId">Window id.</param>
        /// <param name="dockUT">Recorded dock UT (the stop's firing phase and sort key).</param>
        /// <param name="undockUT">Recorded undock UT; must be finite for <c>IsComplete</c>.</param>
        /// <param name="endpointPid">Endpoint vessel persistentId (also the transfer target pid).</param>
        /// <param name="transportPid">Transport vessel persistentId, recorded as the window's transport part scope.</param>
        /// <param name="bodyName">Endpoint body.</param>
        /// <param name="latitude">Endpoint latitude at dock.</param>
        /// <param name="longitude">Endpoint longitude at dock.</param>
        /// <param name="altitude">Endpoint altitude at dock.</param>
        /// <param name="transferEndpointSituation">Endpoint <c>Vessel.Situations</c> at dock; LANDED by default.</param>
        /// <param name="transferKind">Connection producer; DockingPort by default.</param>
        /// <param name="resourceDeliveries">
        /// Resource name -&gt; delivered amount. Null uses the flight's single
        /// <c>LiquidFuel = 97.6</c>; an empty dictionary authors a window with
        /// no resource delivery at all.
        /// </param>
        /// <param name="inventoryDeliveries">
        /// Cargo part names delivered, one unit each. Null uses the flight's
        /// <c>evaChute</c> + <c>evaScienceKit</c>; an empty list authors a
        /// window with no inventory delivery.
        /// </param>
        internal static RouteConnectionWindow SurfaceDeliveryWindow(
            string windowId = "w-rover-surface-delivery",
            double dockUT = RoverDockUT,
            double undockUT = RoverUndockUT,
            uint endpointPid = RoverEndpointPid,
            uint transportPid = RoverTransportPid,
            string bodyName = RoverBodyName,
            double latitude = RoverEndpointLatitude,
            double longitude = RoverEndpointLongitude,
            double altitude = RoverEndpointAltitude,
            int transferEndpointSituation = LandedSituation,
            RouteConnectionKind transferKind = RouteConnectionKind.DockingPort,
            IDictionary<string, double> resourceDeliveries = null,
            IEnumerable<string> inventoryDeliveries = null)
        {
            IDictionary<string, double> resources = resourceDeliveries
                ?? new Dictionary<string, double>
                {
                    [RoverDeliveredResourceName] = RoverLiquidFuelDelivered
                };

            IEnumerable<string> inventory = inventoryDeliveries
                ?? new List<string> { RoverInventoryPartA, RoverInventoryPartB };

            var dockTransport = new Dictionary<string, ResourceAmount>();
            var undockTransport = new Dictionary<string, ResourceAmount>();
            var dockEndpoint = new Dictionary<string, ResourceAmount>();
            var undockEndpoint = new Dictionary<string, ResourceAmount>();
            foreach (KeyValuePair<string, double> kvp in resources)
            {
                double delivered = kvp.Value;
                dockTransport[kvp.Key] =
                    new ResourceAmount { amount = delivered, maxAmount = delivered };
                undockTransport[kvp.Key] =
                    new ResourceAmount { amount = 0.0, maxAmount = delivered };
                dockEndpoint[kvp.Key] =
                    new ResourceAmount { amount = 0.0, maxAmount = delivered };
                undockEndpoint[kvp.Key] =
                    new ResourceAmount { amount = delivered, maxAmount = delivered };
            }

            var dockTransportInventory = new List<InventoryPayloadItem>();
            var undockEndpointInventory = new List<InventoryPayloadItem>();
            foreach (string partName in inventory)
            {
                InventoryPayloadItem item = StoredPartPayload(partName);
                dockTransportInventory.Add(item.DeepClone());
                undockEndpointInventory.Add(item.DeepClone());
            }

            return new RouteConnectionWindow
            {
                WindowId = windowId,
                DockUT = dockUT,
                UndockUT = undockUT,
                TransferTargetVesselPid = endpointPid,
                TransferKind = transferKind,
                TransportPartPersistentIds = new List<uint> { transportPid },
                EndpointPartPersistentIds = new List<uint> { endpointPid },
                DockTransportResources = dockTransport.Count > 0 ? dockTransport : null,
                UndockTransportResources = undockTransport.Count > 0 ? undockTransport : null,
                DockEndpointResources = dockEndpoint.Count > 0 ? dockEndpoint : null,
                UndockEndpointResources = undockEndpoint.Count > 0 ? undockEndpoint : null,
                // Delivery direction only: the transport holds the payload at
                // dock, the endpoint holds it at undock. The two null corners
                // are what production writes for an empty inventory side.
                DockTransportInventory =
                    dockTransportInventory.Count > 0 ? dockTransportInventory : null,
                UndockTransportInventory = null,
                DockEndpointInventory = null,
                UndockEndpointInventory =
                    undockEndpointInventory.Count > 0 ? undockEndpointInventory : null,
                EndpointAtDock = SurfaceEndpoint(
                    endpointPid, bodyName, latitude, longitude, altitude),
                TransferEndpointSituation = transferEndpointSituation
            };
        }

        /// <summary>
        /// One stored-part payload whose <see cref="InventoryPayloadItem.IdentityHash"/>
        /// is computed by the PRODUCTION hasher over a real STOREDPART node, so
        /// a fixture item and an item extracted from a vessel snapshot with the
        /// same stored geometry compare equal.
        /// </summary>
        internal static InventoryPayloadItem StoredPartPayload(
            string partName,
            int quantity = 1,
            int slotsTaken = 1,
            string variantName = null)
        {
            var ic = CultureInfo.InvariantCulture;
            var storedPart = new ConfigNode("STOREDPART");
            storedPart.AddValue("slotIndex", "0");
            storedPart.AddValue("partName", partName);
            storedPart.AddValue("quantity", quantity.ToString(ic));
            storedPart.AddValue("stackCapacity", "1");
            if (!string.IsNullOrEmpty(variantName))
                storedPart.AddValue("variantName", variantName);

            return new InventoryPayloadItem
            {
                IdentityHash = VesselSpawner.ComputeInventoryPayloadIdentityHash(storedPart),
                PartName = partName,
                VariantName = variantName,
                Quantity = quantity,
                SlotsTaken = slotsTaken,
                StoredPartSnapshot = storedPart
            };
        }
    }
}
