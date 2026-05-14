using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    internal enum RouteAnalysisStatus
    {
        Eligible = 0,
        MissingRouteProof = 1,
        MultipleConnectionWindows = 2,
        NoDeliveryManifest = 3,
        MixedPickupDelivery = 4,
        MissingEndpointProof = 5
    }

    internal sealed class RouteAnalysisResult
    {
        public RouteAnalysisStatus Status;
        public Recording SourceRecording;
        public RouteConnectionWindow ConnectionWindow;
        public Dictionary<string, double> ResourceDeliveryManifest;
        public List<InventoryPayloadItem> InventoryDeliveryManifest;

        public bool IsEligible => Status == RouteAnalysisStatus.Eligible;
    }

    internal static class RouteAnalysisEngine
    {
        private const double ResourceEpsilon = 1e-9;

        internal static RouteAnalysisResult AnalyzeTree(RecordingTree tree)
        {
            if (tree?.Recordings == null || tree.Recordings.Count == 0)
                return MissingProof();

            HashSet<string> sourcePathIds = CollectSourcePathRecordingIds(tree);
            if (sourcePathIds == null || sourcePathIds.Count == 0)
                return MissingProof();

            Recording source = null;
            RouteConnectionWindow window = null;

            foreach (string recordingId in sourcePathIds)
            {
                if (!tree.Recordings.TryGetValue(recordingId, out Recording rec))
                    continue;
                if (rec?.RouteConnectionWindows == null)
                    continue;

                for (int i = 0; i < rec.RouteConnectionWindows.Count; i++)
                {
                    RouteConnectionWindow candidate = rec.RouteConnectionWindows[i];
                    if (candidate == null || !candidate.IsComplete)
                        continue;

                    if (window != null)
                    {
                        ParsekLog.Verbose("Logistics",
                            $"RouteAnalysis: multiple completed windows tree={tree.Id ?? "<none>"}");
                        return new RouteAnalysisResult
                        {
                            Status = RouteAnalysisStatus.MultipleConnectionWindows
                        };
                    }

                    source = rec;
                    window = candidate;
                }
            }

            if (window == null)
                return MissingProof();

            return AnalyzeWindow(source, window);
        }

        internal static RouteAnalysisResult AnalyzeRecording(Recording recording)
        {
            if (recording?.RouteConnectionWindows == null ||
                recording.RouteConnectionWindows.Count == 0)
                return MissingProof();

            RouteConnectionWindow window = null;
            for (int i = 0; i < recording.RouteConnectionWindows.Count; i++)
            {
                RouteConnectionWindow candidate = recording.RouteConnectionWindows[i];
                if (candidate == null || !candidate.IsComplete)
                    continue;

                if (window != null)
                    return new RouteAnalysisResult
                    {
                        Status = RouteAnalysisStatus.MultipleConnectionWindows
                    };

                window = candidate;
            }

            if (window == null)
                return MissingProof();

            return AnalyzeWindow(recording, window);
        }

        private static RouteAnalysisResult AnalyzeWindow(
            Recording source,
            RouteConnectionWindow window)
        {
            if (!HasEndpointProof(window))
            {
                ParsekLog.Verbose("Logistics",
                    $"RouteAnalysis: missing endpoint proof source={source?.RecordingId ?? "<none>"} " +
                    $"window={window.WindowId ?? "<none>"}");
                return new RouteAnalysisResult
                {
                    Status = RouteAnalysisStatus.MissingEndpointProof,
                    SourceRecording = source,
                    ConnectionWindow = window
                };
            }

            if (HasMixedPickupDelivery(window))
            {
                ParsekLog.Verbose("Logistics",
                    $"RouteAnalysis: mixed pickup/delivery rejected source={source?.RecordingId ?? "<none>"} " +
                    $"window={window.WindowId ?? "<none>"}");
                return new RouteAnalysisResult
                {
                    Status = RouteAnalysisStatus.MixedPickupDelivery,
                    SourceRecording = source,
                    ConnectionWindow = window
                };
            }

            Dictionary<string, double> resources = BuildResourceDeliveryManifest(window);
            List<InventoryPayloadItem> inventory = BuildInventoryDeliveryManifest(window);

            if ((resources == null || resources.Count == 0) &&
                (inventory == null || inventory.Count == 0))
            {
                ParsekLog.Verbose("Logistics",
                    $"RouteAnalysis: no delivery manifest source={source?.RecordingId ?? "<none>"} " +
                    $"window={window.WindowId ?? "<none>"}");
                return new RouteAnalysisResult
                {
                    Status = RouteAnalysisStatus.NoDeliveryManifest,
                    SourceRecording = source,
                    ConnectionWindow = window
                };
            }

            ParsekLog.Verbose("Logistics",
                $"RouteAnalysis: eligible source={source?.RecordingId ?? "<none>"} " +
                $"window={window.WindowId ?? "<none>"} resources={resources?.Count ?? 0} " +
                $"inventory={inventory?.Count ?? 0}");

            return new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.Eligible,
                SourceRecording = source,
                ConnectionWindow = window,
                ResourceDeliveryManifest = resources,
                InventoryDeliveryManifest = inventory
            };
        }

        private static RouteAnalysisResult MissingProof()
        {
            ParsekLog.Verbose("Logistics", "RouteAnalysis: missing route proof");
            return new RouteAnalysisResult { Status = RouteAnalysisStatus.MissingRouteProof };
        }

        private static bool HasEndpointProof(RouteConnectionWindow window)
        {
            return window != null
                && window.TransferTargetVesselPid != 0
                && window.TransferKind != RouteConnectionKind.None
                && window.EndpointAtDock.HasValue
                && window.TransferEndpointSituation >= 0;
        }

        private static bool HasMixedPickupDelivery(RouteConnectionWindow window)
        {
            return HasResourcePickup(window) || HasInventoryPickup(window);
        }

        private static bool HasResourcePickup(RouteConnectionWindow window)
        {
            var keys = new Dictionary<string, double>();
            AddResourceDeliveryKeys(keys, window.DockEndpointResources);
            AddResourceDeliveryKeys(keys, window.UndockEndpointResources);
            AddResourceDeliveryKeys(keys, window.DockTransportResources);
            AddResourceDeliveryKeys(keys, window.UndockTransportResources);

            foreach (string name in keys.Keys)
            {
                double endpointLoss =
                    GetResourceAmount(window.DockEndpointResources, name) -
                    GetResourceAmount(window.UndockEndpointResources, name);
                double transportGain =
                    GetResourceAmount(window.UndockTransportResources, name) -
                    GetResourceAmount(window.DockTransportResources, name);

                if (endpointLoss > ResourceEpsilon || transportGain > ResourceEpsilon)
                    return true;
            }

            return false;
        }

        private static bool HasInventoryPickup(RouteConnectionWindow window)
        {
            Dictionary<string, InventoryPayloadItem> identities =
                BuildInventoryMap(window.DockEndpointInventory);
            AddInventoryKeys(identities, window.UndockEndpointInventory);
            AddInventoryKeys(identities, window.DockTransportInventory);
            AddInventoryKeys(identities, window.UndockTransportInventory);

            foreach (string identity in identities.Keys)
            {
                int endpointLoss =
                    GetInventoryQuantity(window.DockEndpointInventory, identity) -
                    GetInventoryQuantity(window.UndockEndpointInventory, identity);
                int transportGain =
                    GetInventoryQuantity(window.UndockTransportInventory, identity) -
                    GetInventoryQuantity(window.DockTransportInventory, identity);

                if (endpointLoss > 0 || transportGain > 0)
                    return true;
            }

            return false;
        }

        private static Dictionary<string, double> BuildResourceDeliveryManifest(
            RouteConnectionWindow window)
        {
            var delivery = new Dictionary<string, double>();
            AddResourceDeliveryKeys(delivery, window.DockEndpointResources);
            AddResourceDeliveryKeys(delivery, window.UndockEndpointResources);
            AddResourceDeliveryKeys(delivery, window.DockTransportResources);
            AddResourceDeliveryKeys(delivery, window.UndockTransportResources);

            var names = new List<string>(delivery.Keys);
            delivery.Clear();

            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                double endpointGain =
                    GetResourceAmount(window.UndockEndpointResources, name) -
                    GetResourceAmount(window.DockEndpointResources, name);
                double transportLoss =
                    GetResourceAmount(window.DockTransportResources, name) -
                    GetResourceAmount(window.UndockTransportResources, name);

                if (endpointGain <= ResourceEpsilon || transportLoss <= ResourceEpsilon)
                    continue;

                delivery[name] = Math.Min(endpointGain, transportLoss);
            }

            return delivery.Count > 0 ? delivery : null;
        }

        private static void AddResourceDeliveryKeys(
            Dictionary<string, double> keys,
            Dictionary<string, ResourceAmount> manifest)
        {
            if (manifest == null)
                return;

            foreach (string name in manifest.Keys)
            {
                if (!string.IsNullOrEmpty(name) && !keys.ContainsKey(name))
                    keys[name] = 0.0;
            }
        }

        private static double GetResourceAmount(
            Dictionary<string, ResourceAmount> manifest,
            string name)
        {
            return manifest != null && manifest.TryGetValue(name, out ResourceAmount amount)
                ? amount.amount
                : 0.0;
        }

        private static List<InventoryPayloadItem> BuildInventoryDeliveryManifest(
            RouteConnectionWindow window)
        {
            Dictionary<string, InventoryPayloadItem> deliveredByIdentity =
                BuildInventoryMap(window.UndockEndpointInventory);
            AddInventoryKeys(deliveredByIdentity, window.DockEndpointInventory);
            AddInventoryKeys(deliveredByIdentity, window.DockTransportInventory);
            AddInventoryKeys(deliveredByIdentity, window.UndockTransportInventory);

            if (deliveredByIdentity.Count == 0)
                return null;

            var identities = new List<string>(deliveredByIdentity.Keys);
            var delivery = new List<InventoryPayloadItem>();
            for (int i = 0; i < identities.Count; i++)
            {
                string identity = identities[i];
                int endpointGain =
                    GetInventoryQuantity(window.UndockEndpointInventory, identity) -
                    GetInventoryQuantity(window.DockEndpointInventory, identity);
                int transportLoss =
                    GetInventoryQuantity(window.DockTransportInventory, identity) -
                    GetInventoryQuantity(window.UndockTransportInventory, identity);

                int delivered = Math.Min(endpointGain, transportLoss);
                if (delivered <= 0)
                    continue;

                int endpointSlotsGain =
                    GetInventorySlots(window.UndockEndpointInventory, identity) -
                    GetInventorySlots(window.DockEndpointInventory, identity);

                InventoryPayloadItem source = deliveredByIdentity[identity];
                InventoryPayloadItem item = source.DeepClone();
                item.Quantity = delivered;
                item.SlotsTaken = Math.Max(0, endpointSlotsGain);
                SetStoredPartQuantity(item.StoredPartSnapshot, delivered);
                delivery.Add(item);
            }

            delivery.Sort((a, b) => string.Compare(a.IdentityHash, b.IdentityHash, StringComparison.Ordinal));
            return delivery.Count > 0 ? delivery : null;
        }

        private static Dictionary<string, InventoryPayloadItem> BuildInventoryMap(
            List<InventoryPayloadItem> items)
        {
            var map = new Dictionary<string, InventoryPayloadItem>();
            AddInventoryKeys(map, items);
            return map;
        }

        private static void AddInventoryKeys(
            Dictionary<string, InventoryPayloadItem> map,
            List<InventoryPayloadItem> items)
        {
            if (map == null || items == null)
                return;

            for (int i = 0; i < items.Count; i++)
            {
                InventoryPayloadItem item = items[i];
                if (item == null || string.IsNullOrEmpty(item.IdentityHash))
                    continue;

                if (!map.ContainsKey(item.IdentityHash))
                    map[item.IdentityHash] = item;
            }
        }

        private static int GetInventoryQuantity(
            List<InventoryPayloadItem> items,
            string identity)
        {
            if (items == null || string.IsNullOrEmpty(identity))
                return 0;

            int total = 0;
            for (int i = 0; i < items.Count; i++)
            {
                InventoryPayloadItem item = items[i];
                if (item != null && item.IdentityHash == identity)
                    total += item.Quantity;
            }
            return total;
        }

        private static int GetInventorySlots(
            List<InventoryPayloadItem> items,
            string identity)
        {
            if (items == null || string.IsNullOrEmpty(identity))
                return 0;

            int total = 0;
            for (int i = 0; i < items.Count; i++)
            {
                InventoryPayloadItem item = items[i];
                if (item != null && item.IdentityHash == identity)
                    total += item.SlotsTaken;
            }
            return total;
        }

        private static void SetStoredPartQuantity(ConfigNode storedPart, int quantity)
        {
            if (storedPart == null)
                return;

            storedPart.SetValue(
                "quantity",
                quantity.ToString(CultureInfo.InvariantCulture),
                true);
        }

        private static HashSet<string> CollectSourcePathRecordingIds(RecordingTree tree)
        {
            if (tree?.Recordings == null || tree.Recordings.Count == 0)
                return null;

            string leafId = !string.IsNullOrEmpty(tree.ActiveRecordingId)
                ? tree.ActiveRecordingId
                : tree.RootRecordingId;
            if (string.IsNullOrEmpty(leafId))
                return null;

            var branchPointsById = new Dictionary<string, BranchPoint>();
            if (tree.BranchPoints != null)
            {
                for (int i = 0; i < tree.BranchPoints.Count; i++)
                {
                    BranchPoint bp = tree.BranchPoints[i];
                    if (bp != null && !string.IsNullOrEmpty(bp.Id))
                        branchPointsById[bp.Id] = bp;
                }
            }

            var path = new HashSet<string>();
            var pending = new Stack<string>();
            pending.Push(leafId);

            while (pending.Count > 0)
            {
                string recId = pending.Pop();
                if (string.IsNullOrEmpty(recId) || !path.Add(recId))
                    continue;

                if (!tree.Recordings.TryGetValue(recId, out Recording rec))
                    continue;

                if (string.IsNullOrEmpty(rec.ParentBranchPointId))
                    continue;

                if (!branchPointsById.TryGetValue(rec.ParentBranchPointId, out BranchPoint bp) ||
                    bp.ParentRecordingIds == null)
                {
                    continue;
                }

                for (int i = 0; i < bp.ParentRecordingIds.Count; i++)
                    pending.Push(bp.ParentRecordingIds[i]);
            }

            return path;
        }
    }
}
