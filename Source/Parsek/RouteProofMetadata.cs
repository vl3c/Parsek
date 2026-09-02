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
        // The endpoint vessel's ROOT PART flightID, when known. Launch-unique and not
        // craft-baked, so it identifies a physical vessel where a persistentId cannot.
        // Set for a start-docked origin (where no pid is knowable at capture); zero for
        // KSC origins, dock-window endpoints and pre-2026-09-02 routes, which keep the
        // pid + proximity resolution they always had.
        public uint RootPartUId;
        public string BodyName;
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public bool IsSurface;
    }

    internal sealed class InventoryPayloadItem
    {
        /// <summary>
        /// The stored part's KIND key, not a per-instance fingerprint (operator
        /// ruling 2026-09-02: parts inside an inventory are generic cargo; identity
        /// matters only while a part is PART OF A VESSEL). Computed by
        /// <see cref="VesselSpawner.ComputeInventoryPayloadKindKey"/> from part
        /// name + variant + per-resource fill bucket, and by nothing else - module
        /// state is ignored entirely. The FIELD NAME and its serialized key
        /// (<c>identityHash</c> in RouteProofCodec / RouteCodec / GameAction) are
        /// unchanged from the pre-ruling contract so no persisted format moved and
        /// no schema generation was bumped; the codecs recompute this value from
        /// each item's STOREDPART snapshot on load instead
        /// (<see cref="VesselSpawner.NormalizeLoadedInventoryPayloadItems"/>).
        /// </summary>
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

    /// <summary>
    /// One half of the start-docked seam PAIR, as that half's own
    /// <c>ModuleDockingNode.vesselInfo</c> recorded it, plus the part set that half owns on
    /// the merged vessel. Both halves are captured; NEITHER is labelled origin or transport
    /// at capture - the undock binds that (P12).
    ///
    /// <para><see cref="RootPartUId"/> / <see cref="VesselName"/> / <see cref="VesselType"/>
    /// are PERSISTED identity, the type informationally only: nothing decides on a vessel
    /// type any more. The part set and the start manifests are CAPTURE-TIME WORKING DATA and
    /// are NOT serialized - they exist to scope the transport half's manifests at the bind,
    /// which happens in the same session, before the recording is ever written.</para>
    /// </summary>
    internal sealed class StartDockedSeamHalf
    {
        public uint RootPartUId;
        public string VesselName;
        public int VesselType = -1; // (int)VesselType; -1 = unknown. INFORMATIONAL ONLY.
        // Transient (not serialized): this half's part persistentIds on the merged vessel,
        // derived by cutting the seam edge in the merged part tree.
        public List<uint> PartPersistentIds;
        // Transient (not serialized): this half's own start manifests, so whichever half
        // turns out to be the transport gets TRANSPORT-SCOPED start manifests at the bind.
        public Dictionary<string, ResourceAmount> StartResources;
        public List<InventoryPayloadItem> StartInventory;

        internal StartDockedSeamHalf DeepClone()
        {
            return new StartDockedSeamHalf
            {
                RootPartUId = RootPartUId,
                VesselName = VesselName,
                VesselType = VesselType,
                PartPersistentIds = PartPersistentIds != null ? new List<uint>(PartPersistentIds) : null,
                StartResources = RouteProofMetadata.CloneResourceManifest(StartResources),
                StartInventory = RouteProofMetadata.CloneInventoryPayloadItems(StartInventory)
            };
        }
    }

    /// <summary>
    /// The settled dock seam captured at recording start, BOTH halves, with the origin
    /// choice deferred to the undock. Half A is the near half (the node scanned on the
    /// merged vessel), half B the far half (the docked partner part's node); those labels
    /// are scan order and carry NO semantics.
    /// </summary>
    internal sealed class StartDockedSeamPair
    {
        public StartDockedSeamHalf HalfA;
        public StartDockedSeamHalf HalfB;

        internal StartDockedSeamPair DeepClone()
        {
            return new StartDockedSeamPair
            {
                HalfA = HalfA?.DeepClone(),
                HalfB = HalfB?.DeepClone()
            };
        }
    }

    /// <summary>
    /// Lifecycle of a start-docked origin proof. The ONLY state that names an origin is
    /// <see cref="BoundAtUndock"/>; every other state is a captured pair with no origin, and
    /// <c>RouteAnalysisEngine.HasDockedOriginProof</c> refuses it.
    ///
    /// <para><see cref="PairPendingBinding"/> is deliberately 0 so a proof recorded before
    /// P12 (which named an origin by vessel TYPE) deserializes as "not an origin" rather than
    /// as a bound one, and so the hasher's sparse append emits nothing for it.</para>
    /// </summary>
    internal enum StartDockedOriginBindState
    {
        /// <summary>Both halves captured, no origin chosen. Not an origin.</summary>
        PairPendingBinding = 0,
        /// <summary>An undock split bound the origin to the half the run was NOT flying.</summary>
        BoundAtUndock = 1,
        /// <summary>The recording ended while still docked; no undock ever separated the pair.</summary>
        UnboundAtStop = 2,
    }

    /// <summary>
    /// How the pickup was witnessed across the docked span (recording start -> undock), on
    /// the TRANSPORT half's own manifests. Design 19.2.2 item 2 ("Loaded": a recorded
    /// connection window in which cargo flowed FROM another vessel ONTO the transport) plus
    /// its workflow sentence "start the supply run docked to the origin (making it a Loaded
    /// provenance via the start-docked window)".
    /// </summary>
    internal enum OriginPickupKind
    {
        /// <summary>No transport-half manifest at the undock: unevaluable, so not validated.</summary>
        NoUndockManifest = 0,
        /// <summary>At least one admitted resource rose across the docked span. The strong witness.</summary>
        Gain = 1,
        /// <summary>No rise, but the transport leaves the seam carrying admitted cargo.</summary>
        Carried = 2,
        /// <summary>The transport leaves the seam with no admitted cargo at all.</summary>
        None = 3,
    }

    internal sealed class RouteOriginProof
    {
        // The origin depot's LIVE vessel pid. ZERO at capture: Part.Couple destroys the
        // absorbed half's Vessel so its pid is unrecoverable, and the merged pid names
        // whichever half stock made dominant. The UNDOCK binds it, behind the launch-guid
        // gate in RouteProofCapture.DecideOriginPidStamp (P12).
        public uint StartDockedOriginVesselPid;
        // Origin depot identity. ZERO until an undock binds it; at the bind it is the
        // rootPartUId of the seam half the RUN was not flying. rootPartUId is a KSP
        // part flightID: assigned per launch and NOT craft-baked, so unlike persistentId it
        // is a launch-unique key. Rule and derivation:
        // docs/dev/research/origin-proof-partner-identity-memo.md.
        public uint StartDockedOriginRootPartUId;
        public string StartDockedOriginVesselName;
        public int StartDockedOriginVesselType = -1; // (int)VesselType; -1 = unknown. INFORMATIONAL.
        // The transport half of the same pair, stamped at the bind so a reader can see which
        // half the run was flying without re-deriving the binding. Equal to
        // StartDockedOriginRootPartUId means a SELF-ORIGIN, which
        // RouteAnalysisEngine.IsSelfOriginProof refuses at the read.
        public uint StartDockedTransportRootPartUId;
        public int StartDockedTransportVesselType = -1; // (int)VesselType; -1 = unknown. INFORMATIONAL.
        // BOTH halves as captured at recording start, with no origin chosen. Present from
        // capture until (and after) the bind; null on a pre-P12 proof.
        public StartDockedSeamPair StartDockedPair;
        public StartDockedOriginBindState StartDockedOriginBindState =
            StartDockedOriginBindState.PairPendingBinding;
        // The transfer validation stamped at the bind. A proof with PickupValidated == false
        // is captured and persisted but is NOT an origin (RouteAnalysisEngine gate).
        public bool StartDockedOriginPickupValidated;
        public OriginPickupKind StartDockedOriginPickupKind = OriginPickupKind.NoUndockManifest;
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
                StartDockedOriginRootPartUId = StartDockedOriginRootPartUId,
                StartDockedOriginVesselName = StartDockedOriginVesselName,
                StartDockedOriginVesselType = StartDockedOriginVesselType,
                StartDockedTransportRootPartUId = StartDockedTransportRootPartUId,
                StartDockedTransportVesselType = StartDockedTransportVesselType,
                StartDockedPair = StartDockedPair?.DeepClone(),
                StartDockedOriginBindState = StartDockedOriginBindState,
                StartDockedOriginPickupValidated = StartDockedOriginPickupValidated,
                StartDockedOriginPickupKind = StartDockedOriginPickupKind,
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
