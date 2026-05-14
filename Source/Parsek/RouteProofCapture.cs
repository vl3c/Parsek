using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal static class RouteProofCapture
    {
        internal static RouteConnectionWindow BuildDockRouteConnectionWindow(
            double dockUT,
            uint transferTargetVesselPid,
            RouteConnectionKind transferKind,
            ConfigNode dockedSnapshot,
            ICollection<uint> transportPartPersistentIds,
            ICollection<uint> endpointPartPersistentIds,
            RouteEndpoint? endpointAtDock,
            int transferEndpointSituation)
        {
            if (transferTargetVesselPid == 0 || dockedSnapshot == null)
                return null;

            List<uint> transportPids = NormalizePartPids(transportPartPersistentIds);
            if (transportPids == null || transportPids.Count == 0)
                return null;

            List<uint> endpointPids = NormalizePartPids(endpointPartPersistentIds);
            if (endpointPids == null || endpointPids.Count == 0)
                endpointPids = DeriveEndpointPartPids(dockedSnapshot, transportPids);

            if (endpointPids == null || endpointPids.Count == 0)
                return null;

            var window = new RouteConnectionWindow
            {
                WindowId = BuildWindowId(dockUT, transferTargetVesselPid),
                DockUT = dockUT,
                TransferTargetVesselPid = transferTargetVesselPid,
                TransferKind = transferKind != RouteConnectionKind.None
                    ? transferKind
                    : RouteConnectionKind.DockingPort,
                TransportPartPersistentIds = transportPids,
                EndpointPartPersistentIds = endpointPids,
                DockTransportResources =
                    VesselSpawner.ExtractResourceManifest(dockedSnapshot, transportPids),
                DockEndpointResources =
                    VesselSpawner.ExtractResourceManifest(dockedSnapshot, endpointPids),
                DockTransportInventory =
                    VesselSpawner.ExtractInventoryPayloadItems(dockedSnapshot, transportPids),
                DockEndpointInventory =
                    VesselSpawner.ExtractInventoryPayloadItems(dockedSnapshot, endpointPids),
                EndpointAtDock = endpointAtDock,
                TransferEndpointSituation = transferEndpointSituation
            };

            ParsekLog.Verbose("Flight",
                $"Route window dock capture: window={window.WindowId} " +
                $"targetPid={transferTargetVesselPid} transportParts={transportPids.Count} " +
                $"endpointParts={endpointPids.Count} transportRes={window.DockTransportResources?.Count ?? 0} " +
                $"endpointRes={window.DockEndpointResources?.Count ?? 0} " +
                $"transportInv={window.DockTransportInventory?.Count ?? 0} " +
                $"endpointInv={window.DockEndpointInventory?.Count ?? 0}");

            return window;
        }

        internal static bool TryCompleteLatestRouteConnectionWindow(
            Recording recording,
            double undockUT,
            params ConfigNode[] undockSnapshots)
        {
            if (recording?.RouteConnectionWindows == null ||
                recording.RouteConnectionWindows.Count == 0)
            {
                ParsekLog.Verbose("Flight",
                    "Route window undock completion skipped: recording has no connection windows");
                return false;
            }

            for (int i = recording.RouteConnectionWindows.Count - 1; i >= 0; i--)
            {
                RouteConnectionWindow window = recording.RouteConnectionWindows[i];
                if (window == null || window.IsComplete)
                    continue;

                bool completed = CompleteRouteConnectionWindowAtUndock(
                    window,
                    undockUT,
                    undockSnapshots);
                if (completed)
                    recording.MarkFilesDirty();
                return completed;
            }

            ParsekLog.Verbose("Flight",
                $"Route window undock completion skipped: recording={recording.RecordingId ?? "<none>"} " +
                "has no incomplete window");
            return false;
        }

        internal static bool CompleteRouteConnectionWindowAtUndock(
            RouteConnectionWindow window,
            double undockUT,
            params ConfigNode[] undockSnapshots)
        {
            if (window == null || window.TransportPartPersistentIds == null ||
                window.EndpointPartPersistentIds == null)
            {
                ParsekLog.Warn("Flight",
                    "Route window undock completion failed: missing window or part PID sets");
                return false;
            }

            window.UndockUT = undockUT;
            window.UndockTransportResources = ExtractResourceManifestFromSnapshots(
                undockSnapshots,
                window.TransportPartPersistentIds);
            window.UndockEndpointResources = ExtractResourceManifestFromSnapshots(
                undockSnapshots,
                window.EndpointPartPersistentIds);
            window.UndockTransportInventory = ExtractInventoryPayloadItemsFromSnapshots(
                undockSnapshots,
                window.TransportPartPersistentIds);
            window.UndockEndpointInventory = ExtractInventoryPayloadItemsFromSnapshots(
                undockSnapshots,
                window.EndpointPartPersistentIds);

            ParsekLog.Verbose("Flight",
                $"Route window undock capture: window={window.WindowId ?? "<none>"} " +
                $"targetPid={window.TransferTargetVesselPid} " +
                $"transportRes={window.UndockTransportResources?.Count ?? 0} " +
                $"endpointRes={window.UndockEndpointResources?.Count ?? 0} " +
                $"transportInv={window.UndockTransportInventory?.Count ?? 0} " +
                $"endpointInv={window.UndockEndpointInventory?.Count ?? 0}");

            return true;
        }

        private static string BuildWindowId(double dockUT, uint transferTargetVesselPid)
        {
            return "dock-" + dockUT.ToString("R", CultureInfo.InvariantCulture)
                + "-target-" + transferTargetVesselPid.ToString(CultureInfo.InvariantCulture);
        }

        private static Dictionary<string, ResourceAmount> ExtractResourceManifestFromSnapshots(
            ConfigNode[] snapshots,
            ICollection<uint> partPersistentIds)
        {
            if (snapshots == null || partPersistentIds == null || partPersistentIds.Count == 0)
                return null;

            Dictionary<string, ResourceAmount> merged = null;
            for (int i = 0; i < snapshots.Length; i++)
            {
                Dictionary<string, ResourceAmount> manifest =
                    VesselSpawner.ExtractResourceManifest(snapshots[i], partPersistentIds);
                if (manifest == null || manifest.Count == 0)
                    continue;

                if (merged == null)
                    merged = new Dictionary<string, ResourceAmount>();
                MergeResourceManifest(merged, manifest);
            }

            return merged != null && merged.Count > 0 ? merged : null;
        }

        private static List<InventoryPayloadItem> ExtractInventoryPayloadItemsFromSnapshots(
            ConfigNode[] snapshots,
            ICollection<uint> partPersistentIds)
        {
            if (snapshots == null || partPersistentIds == null || partPersistentIds.Count == 0)
                return null;

            Dictionary<string, InventoryPayloadItem> merged = null;
            for (int i = 0; i < snapshots.Length; i++)
            {
                List<InventoryPayloadItem> items =
                    VesselSpawner.ExtractInventoryPayloadItems(snapshots[i], partPersistentIds);
                if (items == null || items.Count == 0)
                    continue;

                if (merged == null)
                    merged = new Dictionary<string, InventoryPayloadItem>();
                MergeInventoryPayloadItems(merged, items);
            }

            if (merged == null || merged.Count == 0)
                return null;

            var list = new List<InventoryPayloadItem>(merged.Values);
            list.Sort((a, b) => string.Compare(a.IdentityHash, b.IdentityHash, StringComparison.Ordinal));
            return list;
        }

        private static void MergeResourceManifest(
            Dictionary<string, ResourceAmount> target,
            Dictionary<string, ResourceAmount> source)
        {
            foreach (KeyValuePair<string, ResourceAmount> kvp in source)
            {
                if (target.TryGetValue(kvp.Key, out ResourceAmount existing))
                {
                    existing.amount += kvp.Value.amount;
                    existing.maxAmount += kvp.Value.maxAmount;
                    target[kvp.Key] = existing;
                }
                else
                {
                    target[kvp.Key] = kvp.Value;
                }
            }
        }

        private static void MergeInventoryPayloadItems(
            Dictionary<string, InventoryPayloadItem> target,
            List<InventoryPayloadItem> source)
        {
            for (int i = 0; i < source.Count; i++)
            {
                InventoryPayloadItem item = source[i];
                if (item == null || string.IsNullOrEmpty(item.IdentityHash))
                    continue;

                if (target.TryGetValue(item.IdentityHash, out InventoryPayloadItem existing))
                {
                    existing.Quantity += item.Quantity;
                    existing.SlotsTaken += item.SlotsTaken;
                }
                else
                {
                    target[item.IdentityHash] = item.DeepClone();
                }
            }
        }

        private static List<uint> DeriveEndpointPartPids(
            ConfigNode dockedSnapshot,
            List<uint> transportPartPersistentIds)
        {
            List<uint> allPids = VesselSpawner.CollectPartPersistentIds(dockedSnapshot);
            if (allPids == null || allPids.Count == 0)
                return null;

            var endpoint = new List<uint>();
            for (int i = 0; i < allPids.Count; i++)
            {
                if (!transportPartPersistentIds.Contains(allPids[i]))
                    endpoint.Add(allPids[i]);
            }

            return NormalizePartPids(endpoint);
        }

        private static List<uint> NormalizePartPids(ICollection<uint> source)
        {
            if (source == null || source.Count == 0)
                return null;

            var pids = new List<uint>();
            foreach (uint pid in source)
            {
                if (pid == 0 || pids.Contains(pid))
                    continue;
                pids.Add(pid);
            }

            if (pids.Count == 0)
                return null;

            pids.Sort();
            return pids;
        }
    }
}
