using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Canonical ConfigNode codec for <see cref="Route"/> (design §4.8).
    ///
    /// Save shape: a single <c>ROUTE</c> node carrying scalar fields,
    /// <c>RECORDING_IDS</c>, <c>SOURCE_REFS</c>, <c>ORIGIN</c>, one or more
    /// <c>STOP</c> children, plus optional <c>COST_MANIFEST</c> and
    /// <c>INVENTORY_COST_MANIFEST</c>. Optional UT fields are omitted when
    /// null so saves stay lean.
    ///
    /// Backing-mission definition (Phase 1): <c>backingMissionTreeId</c>,
    /// <c>dockMemberRecordingId</c>, <c>recordedDockUT</c>, <c>loopAnchorUT</c>,
    /// a sparse <c>lastObservedLoopCycleIndex</c> (omitted when -1), and an
    /// <c>EXCLUDED_INTERVALS</c> child node carrying repeated
    /// <c>excludedInterval</c> values (no node written for an empty set). A
    /// missing backing-mission definition does NOT reject the route — only the
    /// existing zero-STOP / malformed-SOURCE rejects stand. Pre-1.0: graceful
    /// default, no migration.
    ///
    /// Load rejects the whole route on (a) zero <c>STOP</c> children or
    /// (b) a malformed <c>SOURCE</c> entry — returning <c>null</c> with a
    /// warn log so partially-loaded routes never look valid downstream.
    /// Unknown status strings map to <see cref="RouteStatus.Active"/> with
    /// a warn log so future enum additions stay forward-compatible.
    /// </summary>
    internal static class RouteCodec
    {
        private const string Tag = "Route";

        // Node names — kept here so codec rename refactors touch one place.
        internal const string RecordingIdsNode = "RECORDING_IDS";
        internal const string SourceRefsNode = "SOURCE_REFS";
        internal const string SourceChildNode = "SOURCE";
        internal const string OriginNode = "ORIGIN";
        internal const string StopNode = "STOP";
        internal const string EndpointNode = "ENDPOINT";
        internal const string DeliveryManifestNode = "DELIVERY_MANIFEST";
        internal const string InventoryDeliveryManifestNode = "INVENTORY_DELIVERY_MANIFEST";
        internal const string CostManifestNode = "COST_MANIFEST";
        internal const string InventoryCostManifestNode = "INVENTORY_COST_MANIFEST";
        internal const string InventoryItemNode = "ITEM";
        internal const string StoredPartNode = "STOREDPART";
        internal const string StoredResourcesNode = "STORED_RESOURCES";
        internal const string ResourceChildNode = "RESOURCE";
        internal const string ExcludedIntervalsNode = "EXCLUDED_INTERVALS";
        internal const string ExcludedIntervalValue = "excludedInterval";
        internal const string CreationTreeRecordingsNode = "CREATION_TREE_RECORDINGS";
        internal const string CreationTreeRecordingValue = "id";

        // -----------------------------------------------------------------
        // Serialize
        // -----------------------------------------------------------------

        internal static void SerializeInto(Route route, ConfigNode node)
        {
            if (route == null || node == null)
                return;

            var ic = CultureInfo.InvariantCulture;

            // --- Identity ---
            if (!string.IsNullOrEmpty(route.Id))
                node.AddValue("id", route.Id);
            if (!string.IsNullOrEmpty(route.Name))
                node.AddValue("name", route.Name);

            // --- Endpoints + flags ---
            node.AddValue("isKscOrigin", route.IsKscOrigin.ToString());
            // Sparse harvest-origin flag (M2, plan D7): false (the default,
            // every pre-M2 route) writes nothing - mirrors the lastHoldKind /
            // dispatchPriority sparse convention.
            if (route.IsHarvestOrigin)
                node.AddValue("isHarvestOrigin", route.IsHarvestOrigin.ToString());
            node.AddValue("kscDispatchFundsCost", route.KscDispatchFundsCost.ToString("R", ic));

            // --- Scheduling scalars ---
            node.AddValue("transitDuration", route.TransitDuration.ToString("R", ic));
            node.AddValue("dispatchInterval", route.DispatchInterval.ToString("R", ic));
            // Sparse cadence multiplier: 1 (the floor / default) writes nothing.
            if (route.CadenceMultiplier != 1)
                node.AddValue("cadenceMultiplier",
                    Route.ClampCadenceMultiplier(route.CadenceMultiplier).ToString(ic));
            node.AddValue("dispatchWindowEpochUT", route.DispatchWindowEpochUT.ToString("R", ic));
            node.AddValue("dispatchWindowPeriod", route.DispatchWindowPeriod.ToString("R", ic));
            node.AddValue("nextDispatchUT", route.NextDispatchUT.ToString("R", ic));

            // Optional UT scalars: write ONLY when non-null.
            if (route.CurrentCycleStartUT.HasValue)
                node.AddValue("currentCycleStartUT",
                    route.CurrentCycleStartUT.Value.ToString("R", ic));
            if (route.NextEligibilityCheckUT.HasValue)
                node.AddValue("nextEligibilityCheckUT",
                    route.NextEligibilityCheckUT.Value.ToString("R", ic));
            if (route.PendingDeliveryUT.HasValue)
                node.AddValue("pendingDeliveryUT",
                    route.PendingDeliveryUT.Value.ToString("R", ic));

            node.AddValue("currentSegmentIndex", route.CurrentSegmentIndex.ToString(ic));
            node.AddValue("pendingStopIndex", route.PendingStopIndex.ToString(ic));

            if (!string.IsNullOrEmpty(route.LinkedRouteId))
                node.AddValue("linkedRouteId", route.LinkedRouteId);

            // --- Status ---
            node.AddValue("status", route.Status.ToString());
            // Sparse pre-missing baseline (LST-2): the status to restore when a
            // MissingSourceRecording route's sources flicker back into ERS. Default
            // Active (the sentinel) writes nothing; only a deliberately Paused (or
            // other non-Active) baseline lands on the wire.
            if (route.PreMissingStatus != RouteStatus.Active)
                node.AddValue("preMissingStatus", route.PreMissingStatus.ToString());
            node.AddValue("pauseAfterCurrentCycle", route.PauseAfterCurrentCycle.ToString());
            node.AddValue("completedCycles", route.CompletedCycles.ToString(ic));
            node.AddValue("skippedCycles", route.SkippedCycles.ToString(ic));
            // Sparse dispatch priority (M1): 0 (the floor / default) writes nothing.
            if (route.DispatchPriority != 0)
                node.AddValue("dispatchPriority",
                    Route.ClampPriority(route.DispatchPriority).ToString(ic));

            // Sparse last-hold reason (M6 hold reasons): None / null / 0 / -1
            // are the "no hold recorded" defaults, so each field is omitted at
            // its default and a never-held route writes nothing. The kind is
            // enum-by-name (unknown strings load back as None via
            // ParseHoldKindOrNone, mirroring ParseStatusOrWarn).
            if (route.LastHoldKind != RouteDispatchEvaluator.EligibilityFailureKind.None)
                node.AddValue("lastHoldKind", route.LastHoldKind.ToString());
            if (!string.IsNullOrEmpty(route.LastHoldDetail))
                node.AddValue("lastHoldDetail", route.LastHoldDetail);
            if (route.LastHoldShortfall > 0.0)
                node.AddValue("lastHoldShortfall", route.LastHoldShortfall.ToString("R", ic));
            if (route.LastHoldUT >= 0.0)
                node.AddValue("lastHoldUT", route.LastHoldUT.ToString("R", ic));

            // --- Backing-mission definition (Phase 1) ---
            if (!string.IsNullOrEmpty(route.BackingMissionTreeId))
                node.AddValue("backingMissionTreeId", route.BackingMissionTreeId);
            if (!string.IsNullOrEmpty(route.DockMemberRecordingId))
                node.AddValue("dockMemberRecordingId", route.DockMemberRecordingId);
            node.AddValue("recordedDockUT", route.RecordedDockUT.ToString("R", ic));
            node.AddValue("loopAnchorUT", route.LoopAnchorUT.ToString("R", ic));
            // Sparse: -1 (no cycle observed) is the default, so omit it.
            if (route.LastObservedLoopCycleIndex != -1)
                node.AddValue("lastObservedLoopCycleIndex",
                    route.LastObservedLoopCycleIndex.ToString(ic));

            // Recovery-credit deferral marker (logistics-recovery-credit section 5.6).
            // Sparse: null cycle id / -1 dispatch UT are the "no credit owed"
            // defaults, so omit both. This is the credit's save/reload re-fire guard,
            // mirroring lastObservedLoopCycleIndex above.
            if (!string.IsNullOrEmpty(route.PendingRecoveryCreditCycleId))
                node.AddValue("pendingRecoveryCreditCycleId", route.PendingRecoveryCreditCycleId);
            if (route.PendingRecoveryCreditDispatchUT >= 0.0)
                node.AddValue("pendingRecoveryCreditDispatchUT",
                    route.PendingRecoveryCreditDispatchUT.ToString("R", ic));

            // EXCLUDED_INTERVALS: one child node carrying repeated excludedInterval
            // values. Empty set writes NO node (keeps the empty-definition save lean).
            if (route.ExcludedIntervalKeys != null && route.ExcludedIntervalKeys.Count > 0)
            {
                ConfigNode excludedNode = node.AddNode(ExcludedIntervalsNode);
                foreach (string key in route.ExcludedIntervalKeys)
                {
                    if (!string.IsNullOrEmpty(key))
                        excludedNode.AddValue(ExcludedIntervalValue, key);
                }
            }

            // CREATION_TREE_RECORDINGS (M-MIS-9-R1): the creation-time
            // tree-membership snapshot scoping the recovery-credit sum. Sparse:
            // an empty snapshot writes NO node (degenerate / pre-field route;
            // the run-cost resolver then fails open to the whole current tree).
            if (route.CreationTreeRecordingIds != null && route.CreationTreeRecordingIds.Count > 0)
            {
                ConfigNode creationNode = node.AddNode(CreationTreeRecordingsNode);
                foreach (string rid in route.CreationTreeRecordingIds)
                {
                    if (!string.IsNullOrEmpty(rid))
                        creationNode.AddValue(CreationTreeRecordingValue, rid);
                }
            }

            // --- RECORDING_IDS ---
            if (route.RecordingIds != null && route.RecordingIds.Count > 0)
            {
                ConfigNode ridsNode = node.AddNode(RecordingIdsNode);
                for (int i = 0; i < route.RecordingIds.Count; i++)
                {
                    string rid = route.RecordingIds[i];
                    if (!string.IsNullOrEmpty(rid))
                        ridsNode.AddValue("id", rid);
                }
            }

            // --- SOURCE_REFS ---
            if (route.SourceRefs != null && route.SourceRefs.Count > 0)
            {
                ConfigNode refsNode = node.AddNode(SourceRefsNode);
                for (int i = 0; i < route.SourceRefs.Count; i++)
                {
                    RouteSourceRef srcRef = route.SourceRefs[i];
                    if (srcRef == null)
                        continue;
                    SerializeSourceRef(refsNode.AddNode(SourceChildNode), srcRef, ic);
                }
            }

            // --- ORIGIN ---
            SerializeEndpoint(node.AddNode(OriginNode), route.Origin, ic);

            // --- STOPs ---
            if (route.Stops != null)
            {
                for (int i = 0; i < route.Stops.Count; i++)
                {
                    RouteStop stop = route.Stops[i];
                    if (stop == null)
                        continue;
                    SerializeStop(node.AddNode(StopNode), stop, ic);
                }
            }

            // --- COST_MANIFEST (whole-route) ---
            SerializeFlatResourceManifest(node, CostManifestNode, route.CostManifest, ic);

            // --- INVENTORY_COST_MANIFEST ---
            SerializeInventoryItems(node, InventoryCostManifestNode, route.InventoryCostManifest, ic);
        }

        // -----------------------------------------------------------------
        // Deserialize
        // -----------------------------------------------------------------

        internal static Route DeserializeFrom(ConfigNode node)
        {
            if (node == null)
                return null;

            var ic = CultureInfo.InvariantCulture;
            var inv = NumberStyles.Float;

            var route = new Route();

            route.Id = node.GetValue("id");
            route.Name = node.GetValue("name");

            TryParseBool(node.GetValue("isKscOrigin"), out route.IsKscOrigin);
            // Sparse harvest-origin flag (M2): absent reads back false.
            TryParseBool(node.GetValue("isHarvestOrigin"), out route.IsHarvestOrigin);
            TryParseDouble(node.GetValue("kscDispatchFundsCost"), inv, ic, out route.KscDispatchFundsCost);

            TryParseDouble(node.GetValue("transitDuration"), inv, ic, out route.TransitDuration);
            TryParseDouble(node.GetValue("dispatchInterval"), inv, ic, out route.DispatchInterval);
            // Sparse cadence multiplier: absent -> the floor (1). Clamp on read so a
            // hand-edited 0 / negative save never lands a sub-floor cadence.
            TryParseInt(node.GetValue("cadenceMultiplier"), ic, 1, out int cadenceN);
            route.CadenceMultiplier = Route.ClampCadenceMultiplier(cadenceN);
            TryParseDouble(node.GetValue("dispatchWindowEpochUT"), inv, ic, out route.DispatchWindowEpochUT);
            TryParseDouble(node.GetValue("dispatchWindowPeriod"), inv, ic, out route.DispatchWindowPeriod);
            TryParseDouble(node.GetValue("nextDispatchUT"), inv, ic, out route.NextDispatchUT);

            route.CurrentCycleStartUT = TryParseOptionalDouble(node.GetValue("currentCycleStartUT"), inv, ic);
            route.NextEligibilityCheckUT = TryParseOptionalDouble(node.GetValue("nextEligibilityCheckUT"), inv, ic);
            route.PendingDeliveryUT = TryParseOptionalDouble(node.GetValue("pendingDeliveryUT"), inv, ic);

            TryParseInt(node.GetValue("currentSegmentIndex"), ic, -1, out route.CurrentSegmentIndex);
            TryParseInt(node.GetValue("pendingStopIndex"), ic, -1, out route.PendingStopIndex);

            route.LinkedRouteId = NullIfEmpty(node.GetValue("linkedRouteId"));

            string statusStr = node.GetValue("status");
            route.Status = ParseStatusOrWarn(statusStr, route.Id);

            // Sparse pre-missing baseline (LST-2): absent -> Active (the default
            // sentinel). An unknown value also maps to Active via ParseStatusOrWarn.
            string preMissingStr = node.GetValue("preMissingStatus");
            route.PreMissingStatus = string.IsNullOrEmpty(preMissingStr)
                ? RouteStatus.Active
                : ParseStatusOrWarn(preMissingStr, route.Id);

            TryParseBool(node.GetValue("pauseAfterCurrentCycle"), out route.PauseAfterCurrentCycle);
            TryParseInt(node.GetValue("completedCycles"), ic, 0, out route.CompletedCycles);
            TryParseInt(node.GetValue("skippedCycles"), ic, 0, out route.SkippedCycles);
            // Sparse dispatch priority: absent -> the floor (0). Clamp on read so a
            // hand-edited negative save never lands a sub-floor priority.
            TryParseInt(node.GetValue("dispatchPriority"), ic, 0, out int priority);
            route.DispatchPriority = Route.ClampPriority(priority);

            // Sparse last-hold reason (M6 hold reasons): absent keys read the
            // "no hold recorded" defaults (None / null / 0 / -1); an unknown
            // kind string maps to None with a warn (mirrors ParseStatusOrWarn).
            route.LastHoldKind = ParseHoldKindOrNone(node.GetValue("lastHoldKind"), route.Id);
            route.LastHoldDetail = NullIfEmpty(node.GetValue("lastHoldDetail"));
            TryParseDouble(node.GetValue("lastHoldShortfall"), inv, ic, out route.LastHoldShortfall);
            TryParseDoubleWithDefault(node.GetValue("lastHoldUT"), inv, ic, -1.0, out route.LastHoldUT);

            // --- Backing-mission definition (Phase 1) ---
            // A missing backing-mission definition does NOT reject the route —
            // graceful default (pre-1.0, no migration). Missing scalar -> default,
            // missing node -> empty set, absent cycle index -> -1.
            route.BackingMissionTreeId = NullIfEmpty(node.GetValue("backingMissionTreeId"));
            route.DockMemberRecordingId = NullIfEmpty(node.GetValue("dockMemberRecordingId"));
            // Missing -> field default (-1), so seed the out-default to -1.
            TryParseDoubleWithDefault(node.GetValue("recordedDockUT"), inv, ic, -1.0, out route.RecordedDockUT);
            TryParseDoubleWithDefault(node.GetValue("loopAnchorUT"), inv, ic, -1.0, out route.LoopAnchorUT);
            TryParseLong(node.GetValue("lastObservedLoopCycleIndex"), ic, -1L, out route.LastObservedLoopCycleIndex);

            // Recovery-credit deferral marker (logistics-recovery-credit section 5.6).
            // Missing pendingRecoveryCreditCycleId -> null (no credit owed); missing
            // pendingRecoveryCreditDispatchUT -> -1. A pre-feature save simply has no
            // pending marker, which is the correct "no credit owed yet" state.
            route.PendingRecoveryCreditCycleId = NullIfEmpty(node.GetValue("pendingRecoveryCreditCycleId"));
            TryParseDoubleWithDefault(node.GetValue("pendingRecoveryCreditDispatchUT"),
                inv, ic, -1.0, out route.PendingRecoveryCreditDispatchUT);

            LoadStringList(node, ExcludedIntervalsNode, ExcludedIntervalValue, route.ExcludedIntervalKeys);

            // CREATION_TREE_RECORDINGS (M-MIS-9-R1). A missing node loads as an
            // empty snapshot: the run-cost resolver fails open to the whole
            // current tree, which is the pre-field behavior.
            LoadStringList(node, CreationTreeRecordingsNode, CreationTreeRecordingValue, route.CreationTreeRecordingIds);

            // --- RECORDING_IDS ---
            ConfigNode ridsNode = node.GetNode(RecordingIdsNode);
            if (ridsNode != null)
            {
                string[] ids = ridsNode.GetValues("id");
                if (ids != null)
                {
                    for (int i = 0; i < ids.Length; i++)
                    {
                        if (!string.IsNullOrEmpty(ids[i]))
                            route.RecordingIds.Add(ids[i]);
                    }
                }
            }

            // --- SOURCE_REFS (route-wide reject on any malformed entry) ---
            ConfigNode refsNode = node.GetNode(SourceRefsNode);
            if (refsNode != null)
            {
                ConfigNode[] srcNodes = refsNode.GetNodes(SourceChildNode);
                for (int i = 0; i < srcNodes.Length; i++)
                {
                    RouteSourceRef srcRef = DeserializeSourceRef(srcNodes[i], inv, ic);
                    if (srcRef == null)
                    {
                        ParsekLog.Warn(Tag,
                            $"DeserializeFrom: rejecting route id={route.Id ?? "<no-id>"} " +
                            $"because SOURCE child #{i} is missing recordingId or treeId");
                        return null;
                    }
                    route.SourceRefs.Add(srcRef);
                }
            }

            // --- ORIGIN ---
            ConfigNode originNode = node.GetNode(OriginNode);
            if (originNode != null)
                route.Origin = DeserializeEndpoint(originNode, inv, ic);

            // --- STOPs (reject route on empty list) ---
            ConfigNode[] stopNodes = node.GetNodes(StopNode);
            if (stopNodes == null || stopNodes.Length == 0)
            {
                ParsekLog.Warn(Tag,
                    $"DeserializeFrom: rejecting route id={route.Id ?? "<no-id>"} " +
                    "because it has zero STOP children");
                return null;
            }
            for (int i = 0; i < stopNodes.Length; i++)
            {
                route.Stops.Add(DeserializeStop(stopNodes[i], inv, ic));
            }

            // --- COST_MANIFEST ---
            route.CostManifest = DeserializeFlatResourceManifest(node, CostManifestNode, inv, ic);

            // --- INVENTORY_COST_MANIFEST ---
            route.InventoryCostManifest = DeserializeInventoryItems(node, InventoryCostManifestNode, ic);

            return route;
        }

        // -----------------------------------------------------------------
        // SOURCE
        // -----------------------------------------------------------------

        private static void SerializeSourceRef(ConfigNode node, RouteSourceRef src, CultureInfo ic)
        {
            if (!string.IsNullOrEmpty(src.RecordingId))
                node.AddValue("recordingId", src.RecordingId);
            if (!string.IsNullOrEmpty(src.TreeId))
                node.AddValue("treeId", src.TreeId);
            node.AddValue("treeOrder", src.TreeOrder.ToString(ic));
            node.AddValue("recordingFormatVersion", src.RecordingFormatVersion.ToString(ic));
            node.AddValue("recordingSchemaGeneration", src.RecordingSchemaGeneration.ToString(ic));
            node.AddValue("sidecarEpoch", src.SidecarEpoch.ToString(ic));
            node.AddValue("startUT", src.StartUT.ToString("R", ic));
            node.AddValue("endUT", src.EndUT.ToString("R", ic));
            if (!string.IsNullOrEmpty(src.RouteProofHash))
                node.AddValue("routeProofHash", src.RouteProofHash);
        }

        /// <summary>
        /// Returns null if <c>recordingId</c> or <c>treeId</c> is missing —
        /// caller must reject the whole route on null.
        /// </summary>
        private static RouteSourceRef DeserializeSourceRef(
            ConfigNode node, NumberStyles inv, CultureInfo ic)
        {
            string recId = node.GetValue("recordingId");
            string treeId = node.GetValue("treeId");
            if (string.IsNullOrEmpty(recId) || string.IsNullOrEmpty(treeId))
                return null;

            var src = new RouteSourceRef
            {
                RecordingId = recId,
                TreeId = treeId,
                RouteProofHash = node.GetValue("routeProofHash")
            };
            TryParseInt(node.GetValue("treeOrder"), ic, 0, out src.TreeOrder);
            TryParseInt(node.GetValue("recordingFormatVersion"), ic, 0, out src.RecordingFormatVersion);
            TryParseInt(node.GetValue("recordingSchemaGeneration"), ic, 0, out src.RecordingSchemaGeneration);
            TryParseInt(node.GetValue("sidecarEpoch"), ic, 0, out src.SidecarEpoch);
            TryParseDouble(node.GetValue("startUT"), inv, ic, out src.StartUT);
            TryParseDouble(node.GetValue("endUT"), inv, ic, out src.EndUT);
            return src;
        }

        // -----------------------------------------------------------------
        // ENDPOINT
        // -----------------------------------------------------------------

        // Endpoint shape matches RouteProofCodec.SerializeRouteEndpoint exactly so the
        // same struct serializes the same way regardless of whether it lands inside a
        // ROUTE node here or inside a RouteConnectionWindow there. Sparse writes on
        // pid == 0 and empty body name match that contract -- KSC origins (pid == 0)
        // omit the key on both sides.
        private static void SerializeEndpoint(ConfigNode node, RouteEndpoint ep, CultureInfo ic)
            => RouteNodeCodec.SerializeEndpoint(node, ep, ic);

        private static RouteEndpoint DeserializeEndpoint(
            ConfigNode node, NumberStyles inv, CultureInfo ic)
        {
            var ep = new RouteEndpoint
            {
                BodyName = node.GetValue("bodyName")
            };
            TryParseDouble(node.GetValue("latitude"), inv, ic, out ep.Latitude);
            TryParseDouble(node.GetValue("longitude"), inv, ic, out ep.Longitude);
            TryParseDouble(node.GetValue("altitude"), inv, ic, out ep.Altitude);

            string pidStr = node.GetValue("vesselPersistentId");
            if (pidStr != null
                && uint.TryParse(pidStr, NumberStyles.Integer, ic, out uint pid))
            {
                ep.VesselPersistentId = pid;
            }

            string isSurfaceStr = node.GetValue("isSurface");
            if (isSurfaceStr != null && bool.TryParse(isSurfaceStr, out bool isSurface))
                ep.IsSurface = isSurface;

            return ep;
        }

        // -----------------------------------------------------------------
        // STOP
        // -----------------------------------------------------------------

        private static void SerializeStop(ConfigNode node, RouteStop stop, CultureInfo ic)
        {
            SerializeEndpoint(node.AddNode(EndpointNode), stop.Endpoint, ic);
            node.AddValue("connectionKind", stop.ConnectionKind.ToString());
            node.AddValue("segmentIndexBefore", stop.SegmentIndexBefore.ToString(ic));
            node.AddValue("deliveryOffsetSeconds", stop.DeliveryOffsetSeconds.ToString("R", ic));

            SerializeFlatResourceManifest(node, DeliveryManifestNode, stop.DeliveryManifest, ic);
            SerializeInventoryItems(node, InventoryDeliveryManifestNode, stop.InventoryDeliveryManifest, ic);
        }

        private static RouteStop DeserializeStop(
            ConfigNode node, NumberStyles inv, CultureInfo ic)
        {
            var stop = new RouteStop();
            ConfigNode endpointNode = node.GetNode(EndpointNode);
            if (endpointNode != null)
                stop.Endpoint = DeserializeEndpoint(endpointNode, inv, ic);

            stop.ConnectionKind = ParseConnectionKind(node.GetValue("connectionKind"));
            TryParseInt(node.GetValue("segmentIndexBefore"), ic, -1, out stop.SegmentIndexBefore);
            TryParseDouble(node.GetValue("deliveryOffsetSeconds"), inv, ic, out stop.DeliveryOffsetSeconds);

            stop.DeliveryManifest = DeserializeFlatResourceManifest(node, DeliveryManifestNode, inv, ic);
            stop.InventoryDeliveryManifest = DeserializeInventoryItems(node, InventoryDeliveryManifestNode, ic);

            return stop;
        }

        // -----------------------------------------------------------------
        // Resource + inventory manifests
        // -----------------------------------------------------------------

        // Flat manifest: <name> = <amount> child values directly under the
        // wrapper node (matches design §4.8 COST_MANIFEST/DELIVERY_MANIFEST
        // shape — no nested RESOURCE { name=... amount=... } children).
        private static void SerializeFlatResourceManifest(
            ConfigNode parent,
            string nodeName,
            Dictionary<string, double> manifest,
            CultureInfo ic)
        {
            if (manifest == null || manifest.Count == 0)
                return;

            ConfigNode node = parent.AddNode(nodeName);
            foreach (var kvp in manifest)
            {
                if (string.IsNullOrEmpty(kvp.Key))
                    continue;
                node.AddValue(kvp.Key, kvp.Value.ToString("R", ic));
            }
        }

        private static Dictionary<string, double> DeserializeFlatResourceManifest(
            ConfigNode parent,
            string nodeName,
            NumberStyles inv,
            CultureInfo ic)
        {
            ConfigNode node = parent.GetNode(nodeName);
            if (node == null)
                return null;

            var manifest = new Dictionary<string, double>();
            for (int i = 0; i < node.values.Count; i++)
            {
                ConfigNode.Value v = node.values[i];
                if (string.IsNullOrEmpty(v.name))
                    continue;
                if (double.TryParse(v.value, inv, ic, out double amount))
                    manifest[v.name] = amount;
            }
            return manifest.Count > 0 ? manifest : null;
        }

        private static void SerializeInventoryItems(
            ConfigNode parent,
            string nodeName,
            List<InventoryPayloadItem> items,
            CultureInfo ic)
        {
            if (items == null || items.Count == 0)
                return;

            ConfigNode parentNode = parent.AddNode(nodeName);
            for (int i = 0; i < items.Count; i++)
            {
                InventoryPayloadItem item = items[i];
                if (item == null)
                    continue;

                ConfigNode itemNode = parentNode.AddNode(InventoryItemNode);
                if (!string.IsNullOrEmpty(item.IdentityHash))
                    itemNode.AddValue("identityHash", item.IdentityHash);
                if (!string.IsNullOrEmpty(item.PartName))
                    itemNode.AddValue("partName", item.PartName);
                if (!string.IsNullOrEmpty(item.VariantName))
                    itemNode.AddValue("variantName", item.VariantName);
                if (item.Quantity != 0)
                    itemNode.AddValue("quantity", item.Quantity.ToString(ic));
                if (item.SlotsTaken != 0)
                    itemNode.AddValue("slotsTaken", item.SlotsTaken.ToString(ic));

                SerializeResourceAmountManifest(itemNode, StoredResourcesNode, item.StoredResources, ic);

                if (item.StoredPartSnapshot != null)
                {
                    // Verbatim STOREDPART child of ITEM — preserves payload
                    // identity exactly (no canonicalization).
                    ConfigNode copy = item.StoredPartSnapshot.CreateCopy();
                    copy.name = StoredPartNode;
                    itemNode.AddNode(copy);
                }
            }
        }

        private static List<InventoryPayloadItem> DeserializeInventoryItems(
            ConfigNode parent,
            string nodeName,
            CultureInfo ic)
        {
            ConfigNode parentNode = parent.GetNode(nodeName);
            if (parentNode == null)
                return null;

            ConfigNode[] itemNodes = parentNode.GetNodes(InventoryItemNode);
            if (itemNodes.Length == 0)
                return null;

            var items = new List<InventoryPayloadItem>(itemNodes.Length);
            for (int i = 0; i < itemNodes.Length; i++)
            {
                var item = new InventoryPayloadItem
                {
                    IdentityHash = itemNodes[i].GetValue("identityHash"),
                    PartName = itemNodes[i].GetValue("partName"),
                    VariantName = itemNodes[i].GetValue("variantName"),
                    StoredResources = DeserializeResourceAmountManifest(
                        itemNodes[i], StoredResourcesNode, ic)
                };

                TryParseInt(itemNodes[i].GetValue("quantity"), ic, 0, out item.Quantity);
                TryParseInt(itemNodes[i].GetValue("slotsTaken"), ic, 0, out item.SlotsTaken);

                ConfigNode snapshot = itemNodes[i].GetNode(StoredPartNode);
                if (snapshot != null)
                {
                    ConfigNode copy = snapshot.CreateCopy();
                    copy.name = StoredPartNode;
                    item.StoredPartSnapshot = copy;
                }

                items.Add(item);
            }

            return items;
        }

        private static void SerializeResourceAmountManifest(
            ConfigNode parent,
            string nodeName,
            Dictionary<string, ResourceAmount> manifest,
            CultureInfo ic)
        {
            if (manifest == null || manifest.Count == 0)
                return;

            ConfigNode node = parent.AddNode(nodeName);
            foreach (var kvp in manifest)
            {
                if (string.IsNullOrEmpty(kvp.Key))
                    continue;
                ConfigNode r = node.AddNode(ResourceChildNode);
                r.AddValue("name", kvp.Key);
                r.AddValue("amount", kvp.Value.amount.ToString("R", ic));
                r.AddValue("maxAmount", kvp.Value.maxAmount.ToString("R", ic));
            }
        }

        private static Dictionary<string, ResourceAmount> DeserializeResourceAmountManifest(
            ConfigNode parent,
            string nodeName,
            CultureInfo ic)
        {
            ConfigNode node = parent.GetNode(nodeName);
            if (node == null)
                return null;

            ConfigNode[] rNodes = node.GetNodes(ResourceChildNode);
            if (rNodes.Length == 0)
                return null;

            var manifest = new Dictionary<string, ResourceAmount>();
            var inv = NumberStyles.Float;
            for (int i = 0; i < rNodes.Length; i++)
            {
                string name = rNodes[i].GetValue("name");
                if (string.IsNullOrEmpty(name))
                    continue;
                double amount = 0.0;
                double maxAmount = 0.0;
                double.TryParse(rNodes[i].GetValue("amount"), inv, ic, out amount);
                double.TryParse(rNodes[i].GetValue("maxAmount"), inv, ic, out maxAmount);
                manifest[name] = new ResourceAmount { amount = amount, maxAmount = maxAmount };
            }
            return manifest.Count > 0 ? manifest : null;
        }

        // -----------------------------------------------------------------
        // Enum + primitive parsing helpers
        // -----------------------------------------------------------------

        private static RouteStatus ParseStatusOrWarn(string raw, string routeIdForLog)
        {
            if (string.IsNullOrEmpty(raw))
                return RouteStatus.Active;

            if (Enum.TryParse(raw, out RouteStatus status)
                && Enum.IsDefined(typeof(RouteStatus), status))
            {
                return status;
            }

            ParsekLog.Warn(Tag,
                $"DeserializeFrom: unknown status='{raw}' on route id={routeIdForLog ?? "<no-id>"}; " +
                "mapping to Active. Next dispatch revalidation will re-derive a safer status if needed.");
            return RouteStatus.Active;
        }

        /// <summary>
        /// Parses the sparse <c>lastHoldKind</c> value (M6 hold reasons).
        /// Absent / empty reads the None default; an unknown string maps to
        /// None with a warn (the hold simply reads as cleared), mirroring
        /// <see cref="ParseStatusOrWarn"/> so future enum additions stay
        /// forward-compatible.
        /// </summary>
        private static RouteDispatchEvaluator.EligibilityFailureKind ParseHoldKindOrNone(
            string raw, string routeIdForLog)
        {
            if (string.IsNullOrEmpty(raw))
                return RouteDispatchEvaluator.EligibilityFailureKind.None;

            if (Enum.TryParse(raw, out RouteDispatchEvaluator.EligibilityFailureKind kind)
                && Enum.IsDefined(typeof(RouteDispatchEvaluator.EligibilityFailureKind), kind))
            {
                return kind;
            }

            ParsekLog.Warn(Tag,
                $"DeserializeFrom: unknown lastHoldKind='{raw}' on route id={routeIdForLog ?? "<no-id>"}; " +
                "mapping to None (the hold reads as cleared).");
            return RouteDispatchEvaluator.EligibilityFailureKind.None;
        }

        private static RouteConnectionKind ParseConnectionKind(string raw)
            => RouteNodeCodec.ParseConnectionKind(raw);

        private static void TryParseBool(string raw, out bool value)
        {
            if (raw != null && bool.TryParse(raw, out bool parsed))
                value = parsed;
            else
                value = false;
        }

        private static void TryParseInt(string raw, CultureInfo ic, int defaultValue, out int value)
        {
            if (raw != null && int.TryParse(raw, NumberStyles.Integer, ic, out int parsed))
                value = parsed;
            else
                value = defaultValue;
        }

        private static void TryParseDouble(string raw, NumberStyles inv, CultureInfo ic, out double value)
        {
            if (raw != null && double.TryParse(raw, inv, ic, out double parsed))
                value = parsed;
            else
                value = 0.0;
        }

        // Like TryParseDouble but falls back to a caller-supplied default (not 0.0)
        // when the value is missing or malformed — used for fields whose unset
        // sentinel is -1, so an absent value reads back as the field default.
        private static void TryParseDoubleWithDefault(
            string raw, NumberStyles inv, CultureInfo ic, double defaultValue, out double value)
        {
            if (raw != null && double.TryParse(raw, inv, ic, out double parsed))
                value = parsed;
            else
                value = defaultValue;
        }

        private static void TryParseLong(string raw, CultureInfo ic, long defaultValue, out long value)
        {
            if (raw != null && long.TryParse(raw, NumberStyles.Integer, ic, out long parsed))
                value = parsed;
            else
                value = defaultValue;
        }

        private static double? TryParseOptionalDouble(string raw, NumberStyles inv, CultureInfo ic)
        {
            if (raw != null && double.TryParse(raw, inv, ic, out double parsed))
                return parsed;
            return null;
        }

        /// <summary>
        /// Returns null for a null/empty input, otherwise the input unchanged.
        /// Folds the "GetValue then if-empty-set-null" idiom used for the optional
        /// string scalars in <see cref="DeserializeFrom"/>.
        /// </summary>
        private static string NullIfEmpty(string raw)
            => string.IsNullOrEmpty(raw) ? null : raw;

        /// <summary>
        /// Loads a child node's repeated <paramref name="valueKey"/> values into
        /// <paramref name="target"/>, skipping null/empty entries. A missing node
        /// or null value array adds nothing. Folds the two byte-identical
        /// repeated-value loaders (EXCLUDED_INTERVALS, CREATION_TREE_RECORDINGS).
        /// </summary>
        private static void LoadStringList(
            ConfigNode node, string nodeName, string valueKey, ICollection<string> target)
        {
            ConfigNode child = node.GetNode(nodeName);
            if (child != null)
            {
                string[] values = child.GetValues(valueKey);
                if (values != null)
                {
                    for (int i = 0; i < values.Length; i++)
                    {
                        if (!string.IsNullOrEmpty(values[i]))
                            target.Add(values[i]);
                    }
                }
            }
        }
    }
}
