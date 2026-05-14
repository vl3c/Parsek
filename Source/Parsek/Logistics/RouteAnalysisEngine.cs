using System;
using System.Collections.Generic;

namespace Parsek.Logistics
{
    internal enum RouteAnalysisStatus
    {
        Eligible = 0,
        MissingRouteProof = 1,
        MultipleConnectionWindows = 2,
        NoDeliveryManifest = 3
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

            Recording source = null;
            RouteConnectionWindow window = null;

            foreach (Recording rec in tree.Recordings.Values)
            {
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
                int transportSlotsLoss =
                    GetInventorySlots(window.DockTransportInventory, identity) -
                    GetInventorySlots(window.UndockTransportInventory, identity);

                InventoryPayloadItem source = deliveredByIdentity[identity];
                InventoryPayloadItem item = source.DeepClone();
                item.Quantity = delivered;
                item.SlotsTaken = Math.Max(0, Math.Min(endpointSlotsGain, transportSlotsLoss));
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
    }
}
