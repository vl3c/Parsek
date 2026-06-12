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

    /// <summary>
    /// Full-run transport-scoped cargo manifest (M2 / plan D3). One per
    /// Recording, presence-gated: old recordings simply lack it and analyze
    /// exactly as today.
    ///
    /// Lifecycle contract:
    /// - The START half (scope pid set + start resources) is captured ONCE at
    ///   the recording's BIRTH (root/user start, undock-split child, dock-merge
    ///   child, chain-segment birth) and written onto the tree recording
    ///   immediately. It is NEVER re-captured on BG-promotion or quickload
    ///   resume (a re-captured mid-run baseline would fold prior gains into
    ///   "start cargo" and bypass the gain check).
    /// - The END half completes only on ACTIVE stops (BuildCaptureRecording
    ///   paths) and is overwrite-per-active-stop: a chain-boundary stop
    ///   abandoned by ResumeAfterFalseAlarm leaves a stale END that the
    ///   eventual real stop replaces. ForceStop leaves the END absent.
    /// - A recording that transits BACKGROUND has its manifest VOIDED.
    /// - <see cref="EndCaptured"/> is the explicit completion marker so a
    ///   complete manifest is distinguishable from a start-only one even when
    ///   the extracted end manifest is null (resource-less vessel). The
    ///   analysis presence gate (M2 Phase 4) requires BOTH halves.
    ///
    /// No inventory fields in M2 - deferred to M3 (plan review finding 13).
    /// </summary>
    internal sealed class RouteRunCargoManifest
    {
        // Scope set captured at recording start (identical scope rule to the
        // start-docked origin proof). END extraction is scoped to this set, so
        // parts decoupled mid-run drop out of the END manifest (losses, which
        // M2 does not check).
        public List<uint> TransportPartPersistentIds;
        public Dictionary<string, ResourceAmount> StartTransportResources;
        public Dictionary<string, ResourceAmount> EndTransportResources;
        // True once an active stop completed the END half. Null
        // EndTransportResources with EndCaptured=true means "captured, vessel
        // had no resource-bearing parts" - still a complete manifest.
        public bool EndCaptured;

        internal bool HasStartHalf =>
            TransportPartPersistentIds != null && TransportPartPersistentIds.Count > 0;

        internal bool IsComplete => HasStartHalf && EndCaptured;

        internal RouteRunCargoManifest DeepClone()
        {
            return new RouteRunCargoManifest
            {
                TransportPartPersistentIds = TransportPartPersistentIds != null
                    ? new List<uint>(TransportPartPersistentIds)
                    : null,
                StartTransportResources = RouteProofMetadata.CloneResourceManifest(StartTransportResources),
                EndTransportResources = RouteProofMetadata.CloneResourceManifest(EndTransportResources),
                EndCaptured = EndCaptured
            };
        }
    }

    /// <summary>
    /// One witnessed harvest window (M2 / plan D4): the span during which at
    /// least one <c>BaseConverter</c>-derived module (stock and modded
    /// harvesters, converters, asteroid/comet drills) was activated on the
    /// recorded transport. Opened/closed on activity threshold crossings, at
    /// recording start (converter already running), at recording stop, and at
    /// rails transitions (warp re-baseline). The harvested manifest of a
    /// window is the per-resource POSITIVE delta end-minus-start; an
    /// activated-but-stalled drill nets 0 harmlessly.
    ///
    /// The open-time location fields and <see cref="ActiveConverters"/> are
    /// diagnostic / endpoint-resolution metadata, deliberately EXCLUDED from
    /// <c>RouteProofHasher</c> (plan D10): the hash pins the witnessed
    /// quantities only.
    /// </summary>
    internal sealed class RouteHarvestWindow
    {
        public string WindowId;
        public double StartUT = double.NaN;
        public double EndUT = double.NaN; // NaN while open
        public bool OpenedAtRecordingStart;
        public bool ClosedAtRecordingStop;
        public Dictionary<string, ResourceAmount> StartTransportResources;
        public Dictionary<string, ResourceAmount> EndTransportResources;
        // Diagnostic: "partPid:moduleClass:ConverterName" per active converter
        // at open time. Hash-excluded.
        public List<string> ActiveConverters;
        // Open-time location for the M2 Phase 5 harvest-origin endpoint.
        // Hash-excluded.
        public string BodyName;
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public int SituationAtOpen = -1; // (int)Vessel.Situations; -1 = unknown

        internal bool IsOpen => double.IsNaN(EndUT);

        internal RouteHarvestWindow DeepClone()
        {
            return new RouteHarvestWindow
            {
                WindowId = WindowId,
                StartUT = StartUT,
                EndUT = EndUT,
                OpenedAtRecordingStart = OpenedAtRecordingStart,
                ClosedAtRecordingStop = ClosedAtRecordingStop,
                StartTransportResources = RouteProofMetadata.CloneResourceManifest(StartTransportResources),
                EndTransportResources = RouteProofMetadata.CloneResourceManifest(EndTransportResources),
                ActiveConverters = ActiveConverters != null ? new List<string>(ActiveConverters) : null,
                BodyName = BodyName,
                Latitude = Latitude,
                Longitude = Longitude,
                Altitude = Altitude,
                SituationAtOpen = SituationAtOpen
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

        internal static List<RouteHarvestWindow> CloneHarvestWindows(
            List<RouteHarvestWindow> source)
        {
            if (source == null)
                return null;

            var clone = new List<RouteHarvestWindow>(source.Count);
            for (int i = 0; i < source.Count; i++)
                clone.Add(source[i]?.DeepClone());
            return clone;
        }
    }
}
