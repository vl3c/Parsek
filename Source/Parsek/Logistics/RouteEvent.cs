using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// The kind of a <see cref="RouteEvent"/>. The member names and numbers are the ones the
    /// game-actions ledger used for route rows before route events left it (todo
    /// LOGISTICS-WITHOUT-MONEY phase 2), so a log line that prints a type (for example
    /// <c>LifecycleMarker: ... type=RoutePaused</c>) reads as it always did. Those ids stay
    /// reserved in <see cref="GameActionType"/>; nothing writes them to the ledger.
    /// Serialized BY NAME in the route event log.
    /// </summary>
    public enum RouteEventType
    {
        /// <summary>A cycle went out (the scheduler-decision marker). Carries the cycle id,
        /// the dispatch window's stop index and the sparse Send Once stamp.</summary>
        RouteDispatched = 23,

        /// <summary>The physical origin debit of a vessel-origin cycle: the actual removed
        /// cargo, the requested cargo when the origin came up short, the origin pid.</summary>
        RouteCargoDebited = 24,

        /// <summary>Cargo delivered at a stop: the delivered amounts and, on a partial fill,
        /// the requested amounts.</summary>
        RouteCargoDelivered = 25,

        /// <summary>A route stopped dispatching (player pause, an armed pause after a cycle,
        /// or an auto pause on a source problem). The reason is in
        /// <see cref="RouteEvent.RouteEndpointReason"/>.</summary>
        RoutePaused = 26,

        /// <summary>A route's endpoint could not be resolved.</summary>
        RouteEndpointLost = 27,

        /// <summary>Cargo picked up at a pickup stop: actual and requested resource and
        /// inventory manifests, the endpoint pid.</summary>
        RouteCargoPickedUp = 29,

        /// <summary>A paused route was activated (player activate, an auto resume, or the
        /// catch-up resume after a silent flip).</summary>
        RouteResumed = 30,

        /// <summary>A run was held, recorded once per hold episode and reason (design
        /// section 6.7): kind, detail token and measured shortfall.</summary>
        RouteHeld = 36,
    }

    /// <summary>
    /// One supply-route event: something a route did on the real-time clock (a cycle sent,
    /// cargo debited, picked up or delivered, a pause, a resume, a held run, a lost
    /// endpoint). Route events live in the route-owned <see cref="RouteEventLog"/>, persisted
    /// with the route store in the PARSEK scenario node, never in the game-actions ledger:
    /// supply routes are a real-time system that only delivers resources (owner ruling
    /// 2026-10-21). The fields keep the names the ledger rows carried.
    /// </summary>
    internal sealed class RouteEvent
    {
        internal const string NodeName = "EVENT";

        public RouteEventType Type;

        /// <summary>The live clock UT the event was written at.</summary>
        public double UT;

        /// <summary>Order among events of one cycle at the same UT (a window's stride block:
        /// dispatch +0, debit +1, pickup +2, delivery +3).</summary>
        public int Sequence;

        /// <summary>The route the event belongs to. Never empty on a written event.</summary>
        public string RouteId;

        /// <summary>The cycle id (<c>cycle-N</c>); null on route-level events.</summary>
        public string RouteCycleId;

        /// <summary>The 0-based stop index; -1 when not applicable.</summary>
        public int RouteStopIndex = -1;

        /// <summary>Positive per-resource amounts the event moved (delivered, debited or
        /// picked up); null when none.</summary>
        public Dictionary<string, double> RouteResourceManifest;

        /// <summary>The requested amounts, only when the actual fell short.</summary>
        public Dictionary<string, double> RouteRequestedResourceManifest;

        /// <summary>Stored-part inventory the event moved (origin debit or pickup).</summary>
        internal List<InventoryPayloadItem> RouteInventoryManifest;

        /// <summary>The requested inventory, only when the actual fell short.</summary>
        internal List<InventoryPayloadItem> RouteRequestedInventoryManifest;

        /// <summary>The live vessel a physical debit or pickup took cargo from; 0 when unresolved.</summary>
        public uint RouteOriginVesselPid;

        /// <summary>True on a dispatched event whose cycle the player's Send Once armed.</summary>
        public bool RouteSendOnce;

        /// <summary>The reason of a pause, resume or lost endpoint; the detail token of a held run.</summary>
        public string RouteEndpointReason;

        /// <summary>The eligibility failure kind of a held run; None otherwise.</summary>
        internal RouteDispatchEvaluator.EligibilityFailureKind RouteHoldKind;

        /// <summary>The measured shortfall of a held run; 0 when none.</summary>
        public double RouteHoldShortfall;

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private static readonly NumberStyles NS = NumberStyles.Float;

        // Per-event inventory manifest nodes: one ITEM per stored-part identity, sparse.
        private const string InventoryManifestNode = "ROUTE_INVENTORY_MANIFEST";
        private const string RequestedInventoryManifestNode = "ROUTE_REQUESTED_INVENTORY_MANIFEST";
        private const string InventoryItemNode = "ITEM";
        private const string InventoryStoredResourcesNode = "STORED_RESOURCES";
        private const string InventoryStoredPartNode = "STOREDPART";

        /// <summary>
        /// Writes this event as one <c>EVENT</c> child of <paramref name="parent"/>. Every
        /// field is sparse (a default writes nothing), so an event round-trips exactly.
        /// </summary>
        internal void SerializeInto(ConfigNode parent)
        {
            if (parent == null)
                return;
            ConfigNode n = parent.AddNode(NodeName);
            n.AddValue("type", Type.ToString());
            n.AddValue("ut", UT.ToString("R", IC));
            if (Sequence != 0)
                n.AddValue("seq", Sequence.ToString(IC));
            if (!string.IsNullOrEmpty(RouteId))
                n.AddValue("routeId", RouteId);
            if (!string.IsNullOrEmpty(RouteCycleId))
                n.AddValue("routeCycleId", RouteCycleId);
            if (RouteStopIndex >= 0)
                n.AddValue("routeStopIndex", RouteStopIndex.ToString(IC));
            WriteResourceManifest(n, "resource", RouteResourceManifest);
            WriteResourceManifest(n, "requestedResource", RouteRequestedResourceManifest);
            WriteInventoryManifest(n, InventoryManifestNode, RouteInventoryManifest);
            WriteInventoryManifest(n, RequestedInventoryManifestNode, RouteRequestedInventoryManifest);
            if (RouteOriginVesselPid != 0u)
                n.AddValue("routeOriginVesselPid", RouteOriginVesselPid.ToString(IC));
            if (RouteSendOnce)
                n.AddValue("routeSendOnce", RouteSendOnce.ToString());
            if (!string.IsNullOrEmpty(RouteEndpointReason))
                n.AddValue("routeEndpointReason", RouteEndpointReason);
            if (RouteHoldKind != RouteDispatchEvaluator.EligibilityFailureKind.None)
                n.AddValue("routeHoldKind", RouteHoldKind.ToString());
            if (RouteHoldShortfall != 0.0)
                n.AddValue("routeHoldShortfall", RouteHoldShortfall.ToString("R", IC));
        }

        /// <summary>
        /// Reads one <c>EVENT</c> node. Returns null (and names the reason) when the type is
        /// missing or unknown, the UT does not parse, or the route id is empty.
        /// </summary>
        internal static RouteEvent DeserializeFrom(ConfigNode n, out string rejectReason)
        {
            rejectReason = null;
            if (n == null)
            {
                rejectReason = "null-node";
                return null;
            }
            string typeStr = n.GetValue("type");
            if (!TryParseType(typeStr, out RouteEventType type))
            {
                rejectReason = "unknown-type:" + (typeStr ?? "(missing)");
                return null;
            }
            if (!double.TryParse(n.GetValue("ut"), NS, IC, out double ut)
                || double.IsNaN(ut) || double.IsInfinity(ut))
            {
                rejectReason = "bad-ut";
                return null;
            }
            string routeId = n.GetValue("routeId");
            if (string.IsNullOrEmpty(routeId))
            {
                rejectReason = "no-route-id";
                return null;
            }

            var e = new RouteEvent
            {
                Type = type,
                UT = ut,
                RouteId = routeId,
                RouteCycleId = n.GetValue("routeCycleId"),
                RouteEndpointReason = n.GetValue("routeEndpointReason"),
                RouteResourceManifest = ReadResourceManifest(n, "resource"),
                RouteRequestedResourceManifest = ReadResourceManifest(n, "requestedResource"),
                RouteInventoryManifest = ReadInventoryManifest(n, InventoryManifestNode),
                RouteRequestedInventoryManifest = ReadInventoryManifest(n, RequestedInventoryManifestNode),
            };
            if (int.TryParse(n.GetValue("seq"), NumberStyles.Integer, IC, out int seq))
                e.Sequence = seq;
            if (int.TryParse(n.GetValue("routeStopIndex"), NumberStyles.Integer, IC, out int stop))
                e.RouteStopIndex = stop;
            if (uint.TryParse(n.GetValue("routeOriginVesselPid"), NumberStyles.Integer, IC, out uint pid))
                e.RouteOriginVesselPid = pid;
            string sendOnce = n.GetValue("routeSendOnce");
            if (sendOnce != null)
                bool.TryParse(sendOnce, out e.RouteSendOnce);
            string kindStr = n.GetValue("routeHoldKind");
            if (!string.IsNullOrEmpty(kindStr))
            {
                if (Enum.TryParse(kindStr, out RouteDispatchEvaluator.EligibilityFailureKind kind)
                    && Enum.IsDefined(typeof(RouteDispatchEvaluator.EligibilityFailureKind), kind))
                {
                    e.RouteHoldKind = kind;
                }
                else
                {
                    ParsekLog.Warn(RouteEventLog.Tag,
                        $"RouteHeld: unknown routeHoldKind '{kindStr}' on route '{routeId}' " +
                        "- reading None (the row renders the generic held text)");
                }
            }
            string shortfall = n.GetValue("routeHoldShortfall");
            if (shortfall != null)
                double.TryParse(shortfall, NS, IC, out e.RouteHoldShortfall);
            return e;
        }

        /// <summary>Parses a type name; only the defined members are accepted (no numbers).</summary>
        internal static bool TryParseType(string s, out RouteEventType type)
        {
            type = default(RouteEventType);
            if (string.IsNullOrEmpty(s))
                return false;
            for (int i = 0; i < s.Length; i++)
            {
                if (!char.IsLetter(s[i]))
                    return false;
            }
            return Enum.TryParse(s, false, out type)
                && Enum.IsDefined(typeof(RouteEventType), type);
        }

        // Manifest encoding: one "<key> = name|amount" line per non-empty entry.
        private static void WriteResourceManifest(ConfigNode n, string key, Dictionary<string, double> manifest)
        {
            if (manifest == null || manifest.Count == 0)
                return;
            foreach (var kv in manifest)
            {
                if (string.IsNullOrEmpty(kv.Key))
                    continue;
                n.AddValue(key, kv.Key + "|" + kv.Value.ToString("R", IC));
            }
        }

        // Null when the key is absent, so "absent" and "empty" stay apart.
        private static Dictionary<string, double> ReadResourceManifest(ConfigNode n, string key)
        {
            string[] raws = n.GetValues(key);
            if (raws == null || raws.Length == 0)
                return null;

            var dict = new Dictionary<string, double>(raws.Length, StringComparer.Ordinal);
            for (int i = 0; i < raws.Length; i++)
            {
                string raw = raws[i];
                if (string.IsNullOrEmpty(raw))
                    continue;
                int sep = raw.IndexOf('|');
                if (sep <= 0 || sep == raw.Length - 1)
                {
                    ParsekLog.Warn(RouteEventLog.Tag,
                        $"Route manifest entry malformed under key '{key}': '{raw}' (expected 'name|amount')");
                    continue;
                }
                string name = raw.Substring(0, sep);
                string amountStr = raw.Substring(sep + 1);
                if (!double.TryParse(amountStr, NS, IC, out double amount))
                {
                    ParsekLog.Warn(RouteEventLog.Tag,
                        $"Route manifest amount unparseable under key '{key}' for resource '{name}': '{amountStr}'");
                    continue;
                }
                dict[name] = amount;
            }
            return dict.Count == 0 ? null : dict;
        }

        // One ITEM per payload with its identity fields and the verbatim STOREDPART snapshot.
        private static void WriteInventoryManifest(
            ConfigNode n, string nodeName, List<InventoryPayloadItem> manifest)
        {
            if (manifest == null || manifest.Count == 0)
                return;

            ConfigNode parent = n.AddNode(nodeName);
            for (int i = 0; i < manifest.Count; i++)
            {
                InventoryPayloadItem item = manifest[i];
                if (item == null)
                    continue;

                ConfigNode itemNode = parent.AddNode(InventoryItemNode);
                if (!string.IsNullOrEmpty(item.IdentityHash))
                    itemNode.AddValue("identityHash", item.IdentityHash);
                if (!string.IsNullOrEmpty(item.PartName))
                    itemNode.AddValue("partName", item.PartName);
                if (!string.IsNullOrEmpty(item.VariantName))
                    itemNode.AddValue("variantName", item.VariantName);
                if (item.Quantity != 0)
                    itemNode.AddValue("quantity", item.Quantity.ToString(IC));
                if (item.SlotsTaken != 0)
                    itemNode.AddValue("slotsTaken", item.SlotsTaken.ToString(IC));

                if (item.StoredResources != null && item.StoredResources.Count > 0)
                {
                    ConfigNode res = itemNode.AddNode(InventoryStoredResourcesNode);
                    foreach (var kvp in item.StoredResources)
                    {
                        if (string.IsNullOrEmpty(kvp.Key))
                            continue;
                        ConfigNode r = res.AddNode("RESOURCE");
                        r.AddValue("name", kvp.Key);
                        r.AddValue("amount", kvp.Value.amount.ToString("R", IC));
                        r.AddValue("maxAmount", kvp.Value.maxAmount.ToString("R", IC));
                    }
                }

                if (item.StoredPartSnapshot != null)
                {
                    ConfigNode copy = item.StoredPartSnapshot.CreateCopy();
                    copy.name = InventoryStoredPartNode;
                    itemNode.AddNode(copy);
                }
            }
        }

        // Null when the node is absent or holds no items.
        private static List<InventoryPayloadItem> ReadInventoryManifest(ConfigNode n, string nodeName)
        {
            ConfigNode parent = n.GetNode(nodeName);
            if (parent == null)
                return null;

            ConfigNode[] itemNodes = parent.GetNodes(InventoryItemNode);
            if (itemNodes.Length == 0)
                return null;

            var items = new List<InventoryPayloadItem>(itemNodes.Length);
            for (int i = 0; i < itemNodes.Length; i++)
            {
                ConfigNode itemNode = itemNodes[i];
                var item = new InventoryPayloadItem
                {
                    IdentityHash = itemNode.GetValue("identityHash"),
                    PartName = itemNode.GetValue("partName"),
                    VariantName = itemNode.GetValue("variantName"),
                };
                if (int.TryParse(itemNode.GetValue("quantity"), NumberStyles.Integer, IC, out int qty))
                    item.Quantity = qty;
                if (int.TryParse(itemNode.GetValue("slotsTaken"), NumberStyles.Integer, IC, out int slots))
                    item.SlotsTaken = slots;

                ConfigNode res = itemNode.GetNode(InventoryStoredResourcesNode);
                if (res != null)
                {
                    ConfigNode[] resNodes = res.GetNodes("RESOURCE");
                    if (resNodes.Length > 0)
                    {
                        item.StoredResources = new Dictionary<string, ResourceAmount>(
                            resNodes.Length, StringComparer.Ordinal);
                        for (int r = 0; r < resNodes.Length; r++)
                        {
                            string name = resNodes[r].GetValue("name");
                            if (string.IsNullOrEmpty(name))
                                continue;
                            double.TryParse(resNodes[r].GetValue("amount"), NS, IC, out double amount);
                            double.TryParse(resNodes[r].GetValue("maxAmount"), NS, IC, out double maxAmount);
                            item.StoredResources[name] = new ResourceAmount { amount = amount, maxAmount = maxAmount };
                        }
                    }
                }

                ConfigNode snapshot = itemNode.GetNode(InventoryStoredPartNode);
                if (snapshot != null)
                {
                    ConfigNode copy = snapshot.CreateCopy();
                    copy.name = InventoryStoredPartNode;
                    item.StoredPartSnapshot = copy;
                }

                items.Add(item);
            }

            // The persisted identityHash is the KIND key; recompute it from each item's own
            // STOREDPART snapshot so an older fingerprint reads as the kind key.
            return VesselSnapshotOps.NormalizeLoadedInventoryPayloadItems(items, nodeName);
        }
    }
}
