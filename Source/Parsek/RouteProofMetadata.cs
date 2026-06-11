using System.Collections.Generic;

namespace Parsek
{
    internal enum RouteConnectionKind
    {
        None = 0,
        DockingPort = 1,
        Grapple = 2,
        StockCrossfeed = 3,
        Unknown = 4
    }

    internal struct RouteEndpoint
    {
        public uint VesselPersistentId;
        public string BodyName;
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public bool IsSurface;
    }

    internal sealed class InventoryPayloadItem
    {
        public string IdentityHash;
        public string PartName;
        public string VariantName;
        public int Quantity;
        public int SlotsTaken;
        public Dictionary<string, ResourceAmount> StoredResources;
        public ConfigNode StoredPartSnapshot;

        internal InventoryPayloadItem DeepClone()
        {
            return new InventoryPayloadItem
            {
                IdentityHash = IdentityHash,
                PartName = PartName,
                VariantName = VariantName,
                Quantity = Quantity,
                SlotsTaken = SlotsTaken,
                StoredResources = RouteProofMetadata.CloneResourceManifest(StoredResources),
                StoredPartSnapshot = StoredPartSnapshot != null ? StoredPartSnapshot.CreateCopy() : null
            };
        }
    }

    internal sealed class RouteConnectionWindow
    {
        public string WindowId;
        public double DockUT = double.NaN;
        public double UndockUT = double.NaN;
        public uint TransferTargetVesselPid;
        public RouteConnectionKind TransferKind;
        public List<uint> TransportPartPersistentIds;
        public List<uint> EndpointPartPersistentIds;
        public Dictionary<string, ResourceAmount> DockTransportResources;
        public Dictionary<string, ResourceAmount> UndockTransportResources;
        public Dictionary<string, ResourceAmount> DockEndpointResources;
        public Dictionary<string, ResourceAmount> UndockEndpointResources;
        public List<InventoryPayloadItem> DockTransportInventory;
        public List<InventoryPayloadItem> UndockTransportInventory;
        public List<InventoryPayloadItem> DockEndpointInventory;
        public List<InventoryPayloadItem> UndockEndpointInventory;
        public RouteEndpoint? EndpointAtDock;
        public int TransferEndpointSituation = -1;

        internal bool IsComplete => !double.IsNaN(UndockUT);

        internal RouteConnectionWindow DeepClone()
        {
            return new RouteConnectionWindow
            {
                WindowId = WindowId,
                DockUT = DockUT,
                UndockUT = UndockUT,
                TransferTargetVesselPid = TransferTargetVesselPid,
                TransferKind = TransferKind,
                TransportPartPersistentIds = TransportPartPersistentIds != null
                    ? new List<uint>(TransportPartPersistentIds)
                    : null,
                EndpointPartPersistentIds = EndpointPartPersistentIds != null
                    ? new List<uint>(EndpointPartPersistentIds)
                    : null,
                DockTransportResources = RouteProofMetadata.CloneResourceManifest(DockTransportResources),
                UndockTransportResources = RouteProofMetadata.CloneResourceManifest(UndockTransportResources),
                DockEndpointResources = RouteProofMetadata.CloneResourceManifest(DockEndpointResources),
                UndockEndpointResources = RouteProofMetadata.CloneResourceManifest(UndockEndpointResources),
                DockTransportInventory = RouteProofMetadata.CloneInventoryPayloadItems(DockTransportInventory),
                UndockTransportInventory = RouteProofMetadata.CloneInventoryPayloadItems(UndockTransportInventory),
                DockEndpointInventory = RouteProofMetadata.CloneInventoryPayloadItems(DockEndpointInventory),
                UndockEndpointInventory = RouteProofMetadata.CloneInventoryPayloadItems(UndockEndpointInventory),
                EndpointAtDock = EndpointAtDock,
                TransferEndpointSituation = TransferEndpointSituation
            };
        }
    }

    internal sealed class RouteOriginProof
    {
        public uint StartDockedOriginVesselPid;
        // Origin endpoint descriptor (M1): the docked origin partner's body +
        // body-fixed coordinates + situation at recording start. Captured
        // additively; old proofs simply lack the fields (empty body name,
        // zero coords, situation -1). Deliberately EXCLUDED from
        // RouteProofHasher; see the intent comment there (D5).
        public string StartDockedOriginBodyName;
        public double StartDockedOriginLatitude;
        public double StartDockedOriginLongitude;
        public double StartDockedOriginAltitude;
        public bool StartDockedOriginIsSurface;
        public int StartDockedOriginSituation = -1; // (int)Vessel.Situations; -1 = unknown (diagnostic)
        public Dictionary<string, ResourceAmount> StartTransportResources;
        public Dictionary<string, ResourceAmount> EndTransportResources;
        public List<InventoryPayloadItem> StartTransportInventory;
        public List<InventoryPayloadItem> EndTransportInventory;

        internal RouteOriginProof DeepClone()
        {
            return new RouteOriginProof
            {
                StartDockedOriginVesselPid = StartDockedOriginVesselPid,
                StartDockedOriginBodyName = StartDockedOriginBodyName,
                StartDockedOriginLatitude = StartDockedOriginLatitude,
                StartDockedOriginLongitude = StartDockedOriginLongitude,
                StartDockedOriginAltitude = StartDockedOriginAltitude,
                StartDockedOriginIsSurface = StartDockedOriginIsSurface,
                StartDockedOriginSituation = StartDockedOriginSituation,
                StartTransportResources = RouteProofMetadata.CloneResourceManifest(StartTransportResources),
                EndTransportResources = RouteProofMetadata.CloneResourceManifest(EndTransportResources),
                StartTransportInventory = RouteProofMetadata.CloneInventoryPayloadItems(StartTransportInventory),
                EndTransportInventory = RouteProofMetadata.CloneInventoryPayloadItems(EndTransportInventory)
            };
        }
    }

    internal static class RouteProofMetadata
    {
        internal static Dictionary<string, ResourceAmount> CloneResourceManifest(
            Dictionary<string, ResourceAmount> source)
        {
            return source != null ? new Dictionary<string, ResourceAmount>(source) : null;
        }

        internal static Dictionary<string, InventoryItem> CloneInventoryManifest(
            Dictionary<string, InventoryItem> source)
        {
            return source != null ? new Dictionary<string, InventoryItem>(source) : null;
        }

        internal static Dictionary<string, int> CloneCrewManifest(Dictionary<string, int> source)
        {
            return source != null ? new Dictionary<string, int>(source) : null;
        }

        internal static List<InventoryPayloadItem> CloneInventoryPayloadItems(
            List<InventoryPayloadItem> source)
        {
            if (source == null)
                return null;

            var clone = new List<InventoryPayloadItem>(source.Count);
            for (int i = 0; i < source.Count; i++)
                clone.Add(source[i]?.DeepClone());
            return clone;
        }

        internal static List<RouteConnectionWindow> CloneConnectionWindows(
            List<RouteConnectionWindow> source)
        {
            if (source == null)
                return null;

            var clone = new List<RouteConnectionWindow>(source.Count);
            for (int i = 0; i < source.Count; i++)
                clone.Add(source[i]?.DeepClone());
            return clone;
        }
    }
}
