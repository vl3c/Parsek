using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal static class RouteProofCodec
    {
        private const string RouteOriginProofNode = "ROUTE_ORIGIN_PROOF";
        private const string RouteConnectionWindowsNode = "ROUTE_CONNECTION_WINDOWS";
        private const string RouteRunManifestNode = "ROUTE_RUN_MANIFEST";
        private const string StoredPartSnapshotNode = "STOREDPART_SNAPSHOT";
        private const string StockStoredPartNode = "STOREDPART";

        internal static void SerializeRouteProofMetadata(ConfigNode parent, Recording rec)
        {
            if (parent == null || rec == null)
                return;

            var ic = CultureInfo.InvariantCulture;
            bool wroteTargetPid = false;
            bool wroteTransferKind = false;
            bool wroteOriginProof = false;
            int writtenWindows = 0;

            if (rec.TransferTargetVesselPid != 0)
            {
                parent.AddValue("transferTargetPid", rec.TransferTargetVesselPid.ToString(ic));
                wroteTargetPid = true;
            }
            if (rec.TransferKind != RouteConnectionKind.None)
            {
                parent.AddValue("transferKind", rec.TransferKind.ToString());
                wroteTransferKind = true;
            }

            if (HasOriginProofData(rec.RouteOriginProof))
            {
                SerializeOriginProof(parent.AddNode(RouteOriginProofNode), rec.RouteOriginProof);
                wroteOriginProof = true;
            }

            // M2 run manifest (plan D3): additive sparse node - written only
            // when the manifest carries data, so pre-M2 saves are byte-stable
            // and absent nodes read back as null (never lazily allocated; the
            // RouteProofHasher null-vs-empty contract depends on this).
            bool wroteRunManifest = false;
            if (HasRunManifestData(rec.RouteRunManifest))
            {
                SerializeRunManifest(parent.AddNode(RouteRunManifestNode), rec.RouteRunManifest);
                wroteRunManifest = true;
            }

            if (rec.RouteConnectionWindows != null && rec.RouteConnectionWindows.Count > 0)
            {
                ConfigNode windowsNode = null;
                for (int i = 0; i < rec.RouteConnectionWindows.Count; i++)
                {
                    RouteConnectionWindow window = rec.RouteConnectionWindows[i];
                    if (window == null)
                        continue;

                    if (windowsNode == null)
                        windowsNode = parent.AddNode(RouteConnectionWindowsNode);
                    SerializeConnectionWindow(windowsNode.AddNode("WINDOW"), window);
                    writtenWindows++;
                }
            }

            if (wroteTargetPid || wroteTransferKind || wroteOriginProof || wroteRunManifest
                || writtenWindows > 0)
                ParsekLog.Verbose("RecordingStore",
                    $"SerializeRouteProofMetadata: recording={rec.RecordingId} " +
                    $"targetPid={(wroteTargetPid ? rec.TransferTargetVesselPid.ToString(ic) : "none")} " +
                    $"kind={(wroteTransferKind ? rec.TransferKind.ToString() : "none")} " +
                    $"originProof={wroteOriginProof} runManifest={wroteRunManifest} " +
                    $"windows={writtenWindows}");
        }

        internal static void DeserializeRouteProofMetadata(ConfigNode parent, Recording rec)
        {
            if (parent == null || rec == null)
                return;

            var ic = CultureInfo.InvariantCulture;

            string targetPidStr = parent.GetValue("transferTargetPid");
            if (targetPidStr != null
                && uint.TryParse(targetPidStr, NumberStyles.Integer, ic, out uint targetPid))
            {
                rec.TransferTargetVesselPid = targetPid;
            }

            rec.TransferKind = ParseConnectionKind(parent.GetValue("transferKind"));

            ConfigNode originNode = parent.GetNode(RouteOriginProofNode);
            if (originNode != null)
                rec.RouteOriginProof = DeserializeOriginProof(originNode);

            // Absent node -> RouteRunManifest stays null (old-shape recordings
            // and BG-voided legs); the codec must never lazily allocate or the
            // hasher's null-vs-empty contract flips every route to SourceChanged.
            ConfigNode runManifestNode = parent.GetNode(RouteRunManifestNode);
            if (runManifestNode != null)
                rec.RouteRunManifest = DeserializeRunManifest(runManifestNode);

            ConfigNode windowsNode = parent.GetNode(RouteConnectionWindowsNode);
            if (windowsNode == null)
                return;

            ConfigNode[] windowNodes = windowsNode.GetNodes("WINDOW");
            if (windowNodes.Length == 0)
                return;

            rec.RouteConnectionWindows = new List<RouteConnectionWindow>(windowNodes.Length);
            int loaded = 0;
            for (int i = 0; i < windowNodes.Length; i++)
            {
                RouteConnectionWindow window = DeserializeConnectionWindow(windowNodes[i]);
                if (window == null)
                    continue;

                rec.RouteConnectionWindows.Add(window);
                loaded++;
            }

            ParsekLog.Verbose("RecordingStore",
                $"DeserializeRouteProofMetadata: loaded {loaded} connection window(s) for recording={rec.RecordingId}");
        }

        private static bool HasRunManifestData(RouteRunCargoManifest manifest)
        {
            return manifest != null
                && (HasEntries(manifest.TransportPartPersistentIds)
                    || HasEntries(manifest.StartTransportResources)
                    || HasEntries(manifest.EndTransportResources)
                    || manifest.EndCaptured);
        }

        private static void SerializeRunManifest(ConfigNode node, RouteRunCargoManifest manifest)
        {
            SerializePartPidList(node, "TRANSPORT_PART_PIDS", manifest.TransportPartPersistentIds);
            SerializeResourceManifest(node, "START_TRANSPORT_RESOURCES", manifest.StartTransportResources);
            SerializeResourceManifest(node, "END_TRANSPORT_RESOURCES", manifest.EndTransportResources);
            // Sparse: written only when true (mirrors the lastHoldKind exemplar
            // style). EndCaptured=true with an absent END_TRANSPORT_RESOURCES
            // node is a COMPLETE manifest for a resource-less vessel.
            if (manifest.EndCaptured)
                node.AddValue("endCaptured", manifest.EndCaptured.ToString());
            // NO inventory fields in M2 - deferred to M3 (plan finding 13); the
            // sparse node shape makes adding them later free.
        }

        private static RouteRunCargoManifest DeserializeRunManifest(ConfigNode node)
        {
            var manifest = new RouteRunCargoManifest
            {
                TransportPartPersistentIds = DeserializePartPidList(node, "TRANSPORT_PART_PIDS"),
                StartTransportResources = DeserializeResourceManifest(node, "START_TRANSPORT_RESOURCES"),
                EndTransportResources = DeserializeResourceManifest(node, "END_TRANSPORT_RESOURCES"),
            };

            string endCapturedStr = node.GetValue("endCaptured");
            if (endCapturedStr != null && bool.TryParse(endCapturedStr, out bool endCaptured))
                manifest.EndCaptured = endCaptured;

            return manifest;
        }

        private static bool HasOriginProofData(RouteOriginProof proof)
        {
            return proof != null
                && (proof.StartDockedOriginVesselPid != 0
                    || HasEntries(proof.StartTransportResources)
                    || HasEntries(proof.EndTransportResources)
                    || HasEntries(proof.StartTransportInventory)
                    || HasEntries(proof.EndTransportInventory));
        }

        private static bool HasEntries<TKey, TValue>(Dictionary<TKey, TValue> dict)
        {
            return dict != null && dict.Count > 0;
        }

        private static bool HasEntries<T>(List<T> list)
        {
            return list != null && list.Count > 0;
        }

        private static void SerializeOriginProof(ConfigNode node, RouteOriginProof proof)
        {
            var ic = CultureInfo.InvariantCulture;
            if (proof.StartDockedOriginVesselPid != 0)
                node.AddValue("startDockedOriginVesselPid", proof.StartDockedOriginVesselPid.ToString(ic));

            // Origin endpoint descriptor (M1): additive + sparse, written only when
            // a partner pid exists AND the body name was captured. Old proofs without
            // the descriptor read back with defaults (empty body, zero coords,
            // situation -1).
            if (proof.StartDockedOriginVesselPid != 0
                && !string.IsNullOrEmpty(proof.StartDockedOriginBodyName))
            {
                node.AddValue("startDockedOriginBodyName", proof.StartDockedOriginBodyName);
                node.AddValue("startDockedOriginLatitude", proof.StartDockedOriginLatitude.ToString("R", ic));
                node.AddValue("startDockedOriginLongitude", proof.StartDockedOriginLongitude.ToString("R", ic));
                node.AddValue("startDockedOriginAltitude", proof.StartDockedOriginAltitude.ToString("R", ic));
                node.AddValue("startDockedOriginIsSurface", proof.StartDockedOriginIsSurface.ToString());
                if (proof.StartDockedOriginSituation >= 0)
                    node.AddValue("startDockedOriginSituation", proof.StartDockedOriginSituation.ToString(ic));
            }

            SerializeResourceManifest(node, "START_TRANSPORT_RESOURCES", proof.StartTransportResources);
            SerializeResourceManifest(node, "END_TRANSPORT_RESOURCES", proof.EndTransportResources);
            SerializeInventoryPayloadItems(node, "START_TRANSPORT_INVENTORY", proof.StartTransportInventory);
            SerializeInventoryPayloadItems(node, "END_TRANSPORT_INVENTORY", proof.EndTransportInventory);
        }

        private static RouteOriginProof DeserializeOriginProof(ConfigNode node)
        {
            var proof = new RouteOriginProof();
            var ic = CultureInfo.InvariantCulture;

            string originPidStr = node.GetValue("startDockedOriginVesselPid");
            if (originPidStr != null
                && uint.TryParse(originPidStr, NumberStyles.Integer, ic, out uint originPid))
            {
                proof.StartDockedOriginVesselPid = originPid;
            }

            // Origin endpoint descriptor (M1): absent values keep the field defaults
            // (empty body name, zero coords, IsSurface false, situation -1) so
            // old-shape proofs read back unchanged.
            var inv = NumberStyles.Float;
            proof.StartDockedOriginBodyName = node.GetValue("startDockedOriginBodyName");
            if (double.TryParse(node.GetValue("startDockedOriginLatitude"), inv, ic, out double originLat))
                proof.StartDockedOriginLatitude = originLat;
            if (double.TryParse(node.GetValue("startDockedOriginLongitude"), inv, ic, out double originLon))
                proof.StartDockedOriginLongitude = originLon;
            if (double.TryParse(node.GetValue("startDockedOriginAltitude"), inv, ic, out double originAlt))
                proof.StartDockedOriginAltitude = originAlt;
            string originIsSurfaceStr = node.GetValue("startDockedOriginIsSurface");
            if (originIsSurfaceStr != null
                && bool.TryParse(originIsSurfaceStr, out bool originIsSurface))
            {
                proof.StartDockedOriginIsSurface = originIsSurface;
            }
            string originSituationStr = node.GetValue("startDockedOriginSituation");
            if (originSituationStr != null
                && int.TryParse(originSituationStr, NumberStyles.Integer, ic, out int originSituation))
            {
                proof.StartDockedOriginSituation = originSituation;
            }

            proof.StartTransportResources = DeserializeResourceManifest(node, "START_TRANSPORT_RESOURCES");
            proof.EndTransportResources = DeserializeResourceManifest(node, "END_TRANSPORT_RESOURCES");
            proof.StartTransportInventory = DeserializeInventoryPayloadItems(node, "START_TRANSPORT_INVENTORY");
            proof.EndTransportInventory = DeserializeInventoryPayloadItems(node, "END_TRANSPORT_INVENTORY");
            return proof;
        }

        private static void SerializeConnectionWindow(ConfigNode node, RouteConnectionWindow window)
        {
            var ic = CultureInfo.InvariantCulture;
            if (!string.IsNullOrEmpty(window.WindowId))
                node.AddValue("windowId", window.WindowId);
            if (!double.IsNaN(window.DockUT))
                node.AddValue("dockUT", window.DockUT.ToString("R", ic));
            if (!double.IsNaN(window.UndockUT))
                node.AddValue("undockUT", window.UndockUT.ToString("R", ic));
            if (window.TransferTargetVesselPid != 0)
                node.AddValue("transferTargetPid", window.TransferTargetVesselPid.ToString(ic));
            if (window.TransferKind != RouteConnectionKind.None)
                node.AddValue("transferKind", window.TransferKind.ToString());
            if (window.TransferEndpointSituation >= 0)
                node.AddValue("transferEndpointSituation", window.TransferEndpointSituation.ToString(ic));

            SerializePartPidList(node, "TRANSPORT_PART_PIDS", window.TransportPartPersistentIds);
            SerializePartPidList(node, "ENDPOINT_PART_PIDS", window.EndpointPartPersistentIds);
            SerializeResourceManifest(node, "DOCK_TRANSPORT_RESOURCES", window.DockTransportResources);
            SerializeResourceManifest(node, "UNDOCK_TRANSPORT_RESOURCES", window.UndockTransportResources);
            SerializeResourceManifest(node, "DOCK_ENDPOINT_RESOURCES", window.DockEndpointResources);
            SerializeResourceManifest(node, "UNDOCK_ENDPOINT_RESOURCES", window.UndockEndpointResources);
            SerializeInventoryPayloadItems(node, "DOCK_TRANSPORT_INVENTORY", window.DockTransportInventory);
            SerializeInventoryPayloadItems(node, "UNDOCK_TRANSPORT_INVENTORY", window.UndockTransportInventory);
            SerializeInventoryPayloadItems(node, "DOCK_ENDPOINT_INVENTORY", window.DockEndpointInventory);
            SerializeInventoryPayloadItems(node, "UNDOCK_ENDPOINT_INVENTORY", window.UndockEndpointInventory);
            if (window.EndpointAtDock.HasValue)
                SerializeRouteEndpoint(node.AddNode("ENDPOINT_AT_DOCK"), window.EndpointAtDock.Value);
        }

        private static RouteConnectionWindow DeserializeConnectionWindow(ConfigNode node)
        {
            if (node == null)
                return null;

            var window = new RouteConnectionWindow();
            var inv = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            window.WindowId = node.GetValue("windowId");

            string dockUTStr = node.GetValue("dockUT");
            if (dockUTStr != null
                && double.TryParse(dockUTStr, inv, ic, out double dockUT))
            {
                window.DockUT = dockUT;
            }

            string undockUTStr = node.GetValue("undockUT");
            if (undockUTStr != null
                && double.TryParse(undockUTStr, inv, ic, out double undockUT))
            {
                window.UndockUT = undockUT;
            }

            string targetPidStr = node.GetValue("transferTargetPid");
            if (targetPidStr != null
                && uint.TryParse(targetPidStr, NumberStyles.Integer, ic, out uint targetPid))
            {
                window.TransferTargetVesselPid = targetPid;
            }
            window.TransferKind = ParseConnectionKind(node.GetValue("transferKind"));

            string situationStr = node.GetValue("transferEndpointSituation");
            if (situationStr != null
                && int.TryParse(situationStr, NumberStyles.Integer, ic, out int situation))
            {
                window.TransferEndpointSituation = situation;
            }

            window.TransportPartPersistentIds = DeserializePartPidList(node, "TRANSPORT_PART_PIDS");
            window.EndpointPartPersistentIds = DeserializePartPidList(node, "ENDPOINT_PART_PIDS");
            window.DockTransportResources = DeserializeResourceManifest(node, "DOCK_TRANSPORT_RESOURCES");
            window.UndockTransportResources = DeserializeResourceManifest(node, "UNDOCK_TRANSPORT_RESOURCES");
            window.DockEndpointResources = DeserializeResourceManifest(node, "DOCK_ENDPOINT_RESOURCES");
            window.UndockEndpointResources = DeserializeResourceManifest(node, "UNDOCK_ENDPOINT_RESOURCES");
            window.DockTransportInventory = DeserializeInventoryPayloadItems(node, "DOCK_TRANSPORT_INVENTORY");
            window.UndockTransportInventory = DeserializeInventoryPayloadItems(node, "UNDOCK_TRANSPORT_INVENTORY");
            window.DockEndpointInventory = DeserializeInventoryPayloadItems(node, "DOCK_ENDPOINT_INVENTORY");
            window.UndockEndpointInventory = DeserializeInventoryPayloadItems(node, "UNDOCK_ENDPOINT_INVENTORY");

            ConfigNode endpointNode = node.GetNode("ENDPOINT_AT_DOCK");
            if (endpointNode != null)
                window.EndpointAtDock = DeserializeRouteEndpoint(endpointNode);

            return window;
        }

        private static RouteConnectionKind ParseConnectionKind(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return RouteConnectionKind.None;

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue)
                && Enum.IsDefined(typeof(RouteConnectionKind), intValue))
            {
                return (RouteConnectionKind)intValue;
            }

            if (Enum.TryParse(raw, out RouteConnectionKind kind)
                && Enum.IsDefined(typeof(RouteConnectionKind), kind))
            {
                return kind;
            }

            return RouteConnectionKind.Unknown;
        }

        private static void SerializePartPidList(ConfigNode parent, string nodeName, List<uint> pids)
        {
            if (pids == null || pids.Count == 0)
                return;

            ConfigNode node = parent.AddNode(nodeName);
            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < pids.Count; i++)
                node.AddValue("pid", pids[i].ToString(ic));
        }

        private static List<uint> DeserializePartPidList(ConfigNode parent, string nodeName)
        {
            ConfigNode node = parent.GetNode(nodeName);
            if (node == null)
                return null;

            string[] values = node.GetValues("pid");
            if (values == null || values.Length == 0)
                return null;

            var pids = new List<uint>(values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                if (uint.TryParse(values[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pid))
                    pids.Add(pid);
            }

            return pids.Count > 0 ? pids : null;
        }

        private static void SerializeResourceManifest(
            ConfigNode parent,
            string nodeName,
            Dictionary<string, ResourceAmount> manifest)
        {
            if (manifest == null || manifest.Count == 0)
                return;

            ConfigNode node = parent.AddNode(nodeName);
            var ic = CultureInfo.InvariantCulture;
            foreach (var kvp in manifest)
            {
                if (string.IsNullOrEmpty(kvp.Key))
                    continue;

                ConfigNode resourceNode = node.AddNode("RESOURCE");
                resourceNode.AddValue("name", kvp.Key);
                resourceNode.AddValue("amount", kvp.Value.amount.ToString("R", ic));
                resourceNode.AddValue("maxAmount", kvp.Value.maxAmount.ToString("R", ic));
            }
        }

        private static Dictionary<string, ResourceAmount> DeserializeResourceManifest(
            ConfigNode parent,
            string nodeName)
        {
            ConfigNode node = parent.GetNode(nodeName);
            if (node == null)
                return null;

            ConfigNode[] resourceNodes = node.GetNodes("RESOURCE");
            if (resourceNodes.Length == 0)
                return null;

            var manifest = new Dictionary<string, ResourceAmount>();
            var inv = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < resourceNodes.Length; i++)
            {
                string name = resourceNodes[i].GetValue("name");
                if (string.IsNullOrEmpty(name))
                    continue;

                double amount = 0.0;
                double maxAmount = 0.0;
                double.TryParse(resourceNodes[i].GetValue("amount"), inv, ic, out amount);
                double.TryParse(resourceNodes[i].GetValue("maxAmount"), inv, ic, out maxAmount);
                manifest[name] = new ResourceAmount { amount = amount, maxAmount = maxAmount };
            }

            return manifest.Count > 0 ? manifest : null;
        }

        private static void SerializeInventoryPayloadItems(
            ConfigNode parent,
            string nodeName,
            List<InventoryPayloadItem> items)
        {
            if (items == null || items.Count == 0)
                return;

            ConfigNode node = parent.AddNode(nodeName);
            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < items.Count; i++)
            {
                InventoryPayloadItem item = items[i];
                if (item == null)
                    continue;

                ConfigNode itemNode = node.AddNode("ITEM");
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

                SerializeResourceManifest(itemNode, "STORED_RESOURCES", item.StoredResources);
                if (item.StoredPartSnapshot != null)
                {
                    ConfigNode snapshotWrapper = itemNode.AddNode(StoredPartSnapshotNode);
                    ConfigNode snapshotCopy = item.StoredPartSnapshot.CreateCopy();
                    snapshotCopy.name = StockStoredPartNode;
                    snapshotWrapper.AddNode(snapshotCopy);
                }
            }
        }

        private static List<InventoryPayloadItem> DeserializeInventoryPayloadItems(
            ConfigNode parent,
            string nodeName)
        {
            ConfigNode node = parent.GetNode(nodeName);
            if (node == null)
                return null;

            ConfigNode[] itemNodes = node.GetNodes("ITEM");
            if (itemNodes.Length == 0)
                return null;

            var items = new List<InventoryPayloadItem>(itemNodes.Length);
            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < itemNodes.Length; i++)
            {
                var item = new InventoryPayloadItem
                {
                    IdentityHash = itemNodes[i].GetValue("identityHash"),
                    PartName = itemNodes[i].GetValue("partName"),
                    VariantName = itemNodes[i].GetValue("variantName"),
                    StoredResources = DeserializeResourceManifest(itemNodes[i], "STORED_RESOURCES")
                };

                string quantityStr = itemNodes[i].GetValue("quantity");
                if (quantityStr != null
                    && int.TryParse(quantityStr, NumberStyles.Integer, ic, out int quantity))
                {
                    item.Quantity = quantity;
                }

                string slotsStr = itemNodes[i].GetValue("slotsTaken");
                if (slotsStr != null
                    && int.TryParse(slotsStr, NumberStyles.Integer, ic, out int slotsTaken))
                {
                    item.SlotsTaken = slotsTaken;
                }

                ConfigNode snapshotNode = itemNodes[i].GetNode(StoredPartSnapshotNode);
                item.StoredPartSnapshot = DeserializeStoredPartSnapshot(snapshotNode);
                items.Add(item);
            }

            return items.Count > 0 ? items : null;
        }

        private static ConfigNode DeserializeStoredPartSnapshot(ConfigNode snapshotNode)
        {
            if (snapshotNode == null)
                return null;

            ConfigNode storedPartNode = snapshotNode.GetNode(StockStoredPartNode);
            if (storedPartNode == null)
            {
                ParsekLog.Warn("RecordingStore",
                    $"DeserializeStoredPartSnapshot: missing inner {StockStoredPartNode} node " +
                    $"under {snapshotNode.name ?? "<unnamed>"}; dropping payload snapshot");
                return null;
            }
            ConfigNode result = storedPartNode.CreateCopy();
            result.name = StockStoredPartNode;
            return result;
        }

        private static void SerializeRouteEndpoint(ConfigNode node, RouteEndpoint endpoint)
        {
            var ic = CultureInfo.InvariantCulture;
            if (endpoint.VesselPersistentId != 0)
                node.AddValue("vesselPersistentId", endpoint.VesselPersistentId.ToString(ic));
            if (!string.IsNullOrEmpty(endpoint.BodyName))
                node.AddValue("bodyName", endpoint.BodyName);
            node.AddValue("latitude", endpoint.Latitude.ToString("R", ic));
            node.AddValue("longitude", endpoint.Longitude.ToString("R", ic));
            node.AddValue("altitude", endpoint.Altitude.ToString("R", ic));
            node.AddValue("isSurface", endpoint.IsSurface.ToString());
        }

        private static RouteEndpoint DeserializeRouteEndpoint(ConfigNode node)
        {
            var endpoint = new RouteEndpoint();
            var inv = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            string pidStr = node.GetValue("vesselPersistentId");
            if (pidStr != null
                && uint.TryParse(pidStr, NumberStyles.Integer, ic, out uint pid))
            {
                endpoint.VesselPersistentId = pid;
            }

            endpoint.BodyName = node.GetValue("bodyName");
            double.TryParse(node.GetValue("latitude"), inv, ic, out endpoint.Latitude);
            double.TryParse(node.GetValue("longitude"), inv, ic, out endpoint.Longitude);
            double.TryParse(node.GetValue("altitude"), inv, ic, out endpoint.Altitude);

            string isSurfaceStr = node.GetValue("isSurface");
            if (isSurfaceStr != null
                && bool.TryParse(isSurfaceStr, out bool isSurface))
            {
                endpoint.IsSurface = isSurface;
            }

            return endpoint;
        }
    }
}
