using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// Pure snapshot operations over vessel ConfigNode data: resource and
    /// inventory manifest extraction, inventory payload identity (the kind key),
    /// per-part identity regeneration and remapping, the crew-in-snapshot
    /// helpers (crew names, strip / remove, duplicate and missing-crew planning)
    /// and the situation family (classify, correct an unsafe sit, write it).
    ///
    /// <para>Nothing here touches live KSP state (no FlightGlobals, no live
    /// Vessel, no roster) or the recording model (the TerminalState enum is the
    /// one piece of recording vocabulary it reads), which is what lets the
    /// ledger, the logistics stack, the route-proof capture and the UI call it
    /// without reaching into the spawner. Members that need either of those
    /// stay in <see cref="VesselSpawner"/> (so do ExtractCrewManifest, whose
    /// default trait lookup reads the roster, and
    /// RemoveDuplicateCrewFromSnapshotCore, which reads the crew list through
    /// CrewReservationManager).</para>
    /// </summary>
    internal static class VesselSnapshotOps
    {
        /// <summary>
        /// Walk PART > RESOURCE nodes in a vessel snapshot ConfigNode and return
        /// a dictionary of resource names to summed amount/maxAmount.
        /// Excludes ElectricCharge and IntakeAir (noise, not meaningful cargo).
        /// Returns null if input is null, has no parts, or no resources found.
        /// </summary>
        internal static Dictionary<string, ResourceAmount> ExtractResourceManifest(ConfigNode vesselSnapshot)
        {
            return ExtractResourceManifest(vesselSnapshot, null);
        }

        internal static Dictionary<string, ResourceAmount> ExtractResourceManifest(
            ConfigNode vesselSnapshot,
            ICollection<uint> partPersistentIds)
        {
            if (vesselSnapshot == null) return null;

            var parts = vesselSnapshot.GetNodes("PART");
            if (parts.Length == 0) return null;

            var manifest = new Dictionary<string, ResourceAmount>();
            int includedPartCount = 0;

            for (int i = 0; i < parts.Length; i++)
            {
                if (!ShouldIncludePartByPersistentId(parts[i], partPersistentIds))
                    continue;

                includedPartCount++;
                AddResourceNodesToManifest(parts[i].GetNodes("RESOURCE"), manifest);
            }

            if (manifest.Count == 0) return null;

            ParsekLog.Verbose("Spawner",
                $"ExtractResourceManifest: {manifest.Count} resource type(s) from {includedPartCount} part(s)");

            return manifest;
        }
        /// <summary>
        /// Walk PART > MODULE nodes in a vessel snapshot ConfigNode and return
        /// a dictionary of stored inventory item names to summed count/slotsTaken.
        /// Also outputs total inventory slot capacity across all ModuleInventoryPart modules.
        /// Returns null if input is null, has no parts, or no inventory items found.
        /// </summary>
        internal static Dictionary<string, InventoryItem> ExtractInventoryManifest(
            ConfigNode vesselSnapshot, out int totalInventorySlots)
        {
            totalInventorySlots = 0;

            if (vesselSnapshot == null) return null;

            var parts = vesselSnapshot.GetNodes("PART");
            if (parts.Length == 0) return null;

            var manifest = new Dictionary<string, InventoryItem>();
            int moduleCount = 0;

            for (int i = 0; i < parts.Length; i++)
            {
                var modules = parts[i].GetNodes("MODULE");
                for (int j = 0; j < modules.Length; j++)
                {
                    if (modules[j].GetValue("name") != "ModuleInventoryPart") continue;
                    moduleCount++;

                    string slotsStr = modules[j].GetValue("InventorySlots");
                    if (slotsStr != null)
                    {
                        if (int.TryParse(slotsStr, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int slots))
                            totalInventorySlots += slots;
                    }

                    var storedPartsNode = modules[j].GetNode("STOREDPARTS");
                    if (storedPartsNode == null) continue;

                    var storedParts = storedPartsNode.GetNodes("STOREDPART");
                    for (int k = 0; k < storedParts.Length; k++)
                    {
                        string partName = storedParts[k].GetValue("partName");
                        if (string.IsNullOrEmpty(partName)) continue;

                        int quantity = 1;
                        string qtyStr = storedParts[k].GetValue("quantity");
                        if (qtyStr != null)
                        {
                            if (int.TryParse(qtyStr, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out int parsedQty))
                                quantity = parsedQty;
                        }

                        if (manifest.ContainsKey(partName))
                        {
                            // Struct — indexer returns a copy. Read-modify-write.
                            var item = manifest[partName];
                            item.count += quantity;
                            item.slotsTaken += 1;
                            manifest[partName] = item;
                        }
                        else
                        {
                            manifest[partName] = new InventoryItem { count = quantity, slotsTaken = 1 };
                        }
                    }
                }
            }

            if (manifest.Count == 0)
            {
                totalInventorySlots = 0;
                return null;
            }

            ParsekLog.Verbose("Spawner",
                $"ExtractInventoryManifest: {manifest.Count} item type(s), {totalInventorySlots} total slot(s) across {moduleCount} inventory module(s)");

            return manifest;
        }

        /// <summary>
        /// One-token summary of the parts stored in a vessel snapshot's inventories, for a
        /// spawn log line: <c>storedParts=0</c>, or <c>storedParts=3 [evaChute x1, solarPanels5 x2]</c>
        /// (the total quantity, then each part name with its summed quantity, ordinal order).
        /// A spawn that lost or duplicated a stored part reads differently here. Logs nothing.
        /// </summary>
        internal static string FormatStoredPartsForLog(ConfigNode vesselSnapshot)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int total = 0;
            ConfigNode[] parts = vesselSnapshot != null ? vesselSnapshot.GetNodes("PART") : new ConfigNode[0];
            for (int i = 0; i < parts.Length; i++)
            {
                ConfigNode[] modules = parts[i].GetNodes("MODULE");
                for (int j = 0; j < modules.Length; j++)
                {
                    if (modules[j].GetValue("name") != "ModuleInventoryPart") continue;
                    ConfigNode stored = modules[j].GetNode("STOREDPARTS");
                    if (stored == null) continue;
                    ConfigNode[] items = stored.GetNodes("STOREDPART");
                    for (int k = 0; k < items.Length; k++)
                    {
                        string partName = items[k].GetValue("partName");
                        if (string.IsNullOrEmpty(partName)) continue;
                        int quantity = 1;
                        string qtyStr = items[k].GetValue("quantity");
                        if (qtyStr != null
                            && int.TryParse(qtyStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                            quantity = parsed;
                        counts.TryGetValue(partName, out int prior);
                        counts[partName] = prior + quantity;
                        total += quantity;
                    }
                }
            }
            if (counts.Count == 0)
                return "storedParts=0";
            var sb = new StringBuilder();
            sb.Append("storedParts=").Append(total.ToString(CultureInfo.InvariantCulture)).Append(" [");
            bool first = true;
            foreach (KeyValuePair<string, int> kv in counts)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(kv.Key).Append(" x").Append(kv.Value.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(']');
            return sb.ToString();
        }

        internal static List<InventoryPayloadItem> ExtractInventoryPayloadItems(
            ConfigNode vesselSnapshot,
            ICollection<uint> partPersistentIds = null)
        {
            if (vesselSnapshot == null) return null;

            ConfigNode[] parts = vesselSnapshot.GetNodes("PART");
            if (parts.Length == 0) return null;

            var byIdentity = new Dictionary<string, InventoryPayloadItem>();
            int moduleCount = 0;
            int storedPartCount = 0;

            for (int i = 0; i < parts.Length; i++)
            {
                if (!ShouldIncludePartByPersistentId(parts[i], partPersistentIds))
                    continue;

                ConfigNode[] modules = parts[i].GetNodes("MODULE");
                for (int j = 0; j < modules.Length; j++)
                {
                    if (modules[j].GetValue("name") != "ModuleInventoryPart") continue;
                    moduleCount++;

                    ConfigNode storedPartsNode = modules[j].GetNode("STOREDPARTS");
                    if (storedPartsNode == null) continue;

                    ConfigNode[] storedParts = storedPartsNode.GetNodes("STOREDPART");
                    for (int k = 0; k < storedParts.Length; k++)
                    {
                        InventoryPayloadItem item = BuildInventoryPayloadItem(storedParts[k]);
                        if (item == null)
                            continue;

                        storedPartCount++;
                        if (byIdentity.TryGetValue(item.IdentityHash, out InventoryPayloadItem existing))
                        {
                            existing.Quantity += item.Quantity;
                            existing.SlotsTaken += item.SlotsTaken;
                        }
                        else
                        {
                            byIdentity[item.IdentityHash] = item;
                        }
                    }
                }
            }

            if (byIdentity.Count == 0) return null;

            var items = new List<InventoryPayloadItem>(byIdentity.Values);
            items.Sort((a, b) => string.Compare(a.IdentityHash, b.IdentityHash, StringComparison.Ordinal));

            ParsekLog.Verbose("Spawner",
                $"ExtractInventoryPayloadItems: {items.Count} payload identity(s), " +
                $"{storedPartCount} stored part node(s) across {moduleCount} inventory module(s)");

            return items;
        }

        /// <summary>
        /// Canonical KIND string for a stored inventory part. Hashed by
        /// <see cref="ComputeInventoryPayloadKindKey"/>; exposed separately so
        /// tests and diagnostics can read the derivation instead of a hex digest.
        ///
        /// <para>Operator ruling (2026-09-02): parts inside an inventory are
        /// GENERIC. Identity matters for a mission-defining part only while it is
        /// PART OF A VESSEL; a vessel core pocketed into an inventory has ended
        /// its mission and from then on is cargo like any other. Supply routes
        /// therefore match stored parts BY KIND, never by an individual
        /// fingerprint.</para>
        ///
        /// <para>The kind is exactly three things: the part name as stored
        /// (dot-form), the selected part variant
        /// (<see cref="ResolveStoredPartVariantName"/>), and per stored resource
        /// its name plus a FILL BUCKET (empty / partial / full,
        /// <see cref="ClassifyResourceFillBucket"/>) so float drift cannot split
        /// one kind in two. Nothing else counts. Module state is ignored ENTIRELY
        /// apart from the variant selection, which is what the defect
        /// LOGISTICS-INVENTORY-IDENTITY-HASH-BREAKS-ON-A-LIVE-CARGO-MOVE turned
        /// on: stock's ModuleInventoryPart.StoreCargoPartAtSlot(Part, int)
        /// re-serializes a LIVE part on every in-flight move, so any module that
        /// writes a computed value in OnSave (ModuleGroundExpControl's canComm is
        /// the witnessed one) mutates the node in transit. Slot index, quantity,
        /// stack capacity and every ProtoPartSnapshot transient are ignored by
        /// construction - they never enter the string.</para>
        ///
        /// <para>ElectricCharge and IntakeAir are excluded, matching
        /// <see cref="ExtractStoredPartResourceManifest"/>: both drain on their
        /// own and would split a kind for no player-visible reason.</para>
        /// </summary>
        internal static string BuildInventoryPayloadKindCanonicalString(ConfigNode storedPart)
        {
            if (storedPart == null)
                return null;

            string partName = storedPart.GetValue("partName");
            if (string.IsNullOrEmpty(partName))
            {
                ConfigNode[] inner = storedPart.GetNodes("PART");
                for (int i = 0; i < inner.Length && string.IsNullOrEmpty(partName); i++)
                    partName = inner[i].GetValue("name");
            }

            StringBuilder canonical = new StringBuilder();
            canonical.Append("inventory-kind:v1\n");
            canonical.Append("part=").Append(partName ?? "").Append('\n');
            canonical.Append("variant=").Append(ResolveStoredPartVariantName(storedPart)).Append('\n');

            Dictionary<string, ResourceAmount> resources =
                ExtractStoredPartResourceManifest(storedPart);
            if (resources != null && resources.Count > 0)
            {
                var names = new List<string>(resources.Keys);
                names.Sort(StringComparer.Ordinal);
                for (int i = 0; i < names.Count; i++)
                {
                    ResourceAmount ra = resources[names[i]];
                    canonical.Append("res=").Append(names[i]).Append(':')
                             .Append(ClassifyResourceFillBucket(ra.amount, ra.maxAmount))
                             .Append('\n');
                }
            }

            return canonical.ToString();
        }

        /// <summary>
        /// Resolves the stored part's selected variant. Stock writes the variant
        /// in up to three places on a cargo part and a given serialization shape
        /// may carry only some of them, so this takes the FIRST populated source
        /// in a fixed order: the STOREDPART's own <c>variantName</c> (then the
        /// legacy <c>variant</c> spelling), the inner PART's
        /// <c>moduleVariantName</c>, then ModulePartVariants' <c>selectedVariant</c>.
        /// They agree when more than one is present, so the order only decides
        /// which source supplies the value, never what the value is. Returns the
        /// empty string for a variant-less part (most cargo).
        /// </summary>
        internal static string ResolveStoredPartVariantName(ConfigNode storedPart)
        {
            if (storedPart == null)
                return "";

            string direct = NormalizeVariantValue(storedPart.GetValue("variantName"));
            if (direct.Length > 0)
                return direct;
            direct = NormalizeVariantValue(storedPart.GetValue("variant"));
            if (direct.Length > 0)
                return direct;

            ConfigNode[] parts = storedPart.GetNodes("PART");
            for (int i = 0; i < parts.Length; i++)
            {
                string moduleVariant = NormalizeVariantValue(parts[i].GetValue("moduleVariantName"));
                if (moduleVariant.Length > 0)
                    return moduleVariant;
            }

            for (int i = 0; i < parts.Length; i++)
            {
                ConfigNode[] modules = parts[i].GetNodes("MODULE");
                for (int j = 0; j < modules.Length; j++)
                {
                    if (!string.Equals(modules[j].GetValue("name"), "ModulePartVariants",
                            StringComparison.Ordinal))
                    {
                        continue;
                    }
                    string selected = NormalizeVariantValue(modules[j].GetValue("selectedVariant"));
                    if (selected.Length > 0)
                        return selected;
                }
            }

            return "";
        }

        private static string NormalizeVariantValue(string value)
        {
            return string.IsNullOrEmpty(value) ? "" : value.Trim();
        }

        private static string NullIfEmpty(string value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>
        /// Buckets one stored resource's fill so float drift (a battery that lost
        /// a fraction of a unit in transit, a tank rounded on re-serialization)
        /// cannot split one kind into two. Under 1% of max reads <c>empty</c>,
        /// over 99% reads <c>full</c>, everything between reads <c>partial</c>. A
        /// resource with no capacity reads <c>empty</c>.
        /// </summary>
        internal static string ClassifyResourceFillBucket(double amount, double maxAmount)
        {
            if (maxAmount <= 0.0)
                return "empty";

            double ratio = amount / maxAmount;
            if (ratio < 0.01)
                return "empty";
            if (ratio > 0.99)
                return "full";
            return "partial";
        }

        /// <summary>
        /// SHA-256 hex digest of <see cref="BuildInventoryPayloadKindCanonicalString"/>.
        /// This is the ONLY notion of stored-part matching in the logistics stack;
        /// <see cref="ComputeInventoryPayloadIdentityHash"/> is an alias kept for
        /// the call sites and in-game cell names that predate the kind ruling.
        /// </summary>
        internal static string ComputeInventoryPayloadKindKey(ConfigNode storedPart)
        {
            string canonical = BuildInventoryPayloadKindCanonicalString(storedPart);
            if (canonical == null)
                return null;

            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(canonical);
                byte[] hash = sha.ComputeHash(bytes);
                StringBuilder hex = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        /// <summary>
        /// Alias for <see cref="ComputeInventoryPayloadKindKey"/>. The name (and
        /// the serialized <c>identityHash</c> key it feeds) predate the 2026-09-02
        /// kind ruling and are kept so no persisted format changes; the VALUE is
        /// the kind key, not a per-instance fingerprint.
        /// </summary>
        internal static string ComputeInventoryPayloadIdentityHash(ConfigNode storedPart)
        {
            return ComputeInventoryPayloadKindKey(storedPart);
        }

        /// <summary>
        /// Self-heals an inventory manifest read back from a save. Every item
        /// that carries its own STOREDPART snapshot gets its kind key RECOMPUTED
        /// from that snapshot, overwriting whatever string the save held; an item
        /// with no snapshot keeps the stored string (nothing to derive from).
        /// Items that then share a kind are merged - quantities and slots sum,
        /// the first item's snapshot survives - and the result is re-sorted
        /// ordinally, so a loaded manifest is shaped exactly like one
        /// <see cref="ExtractInventoryPayloadItems"/> just built.
        ///
        /// <para>This is what makes pre-2026-09-02 saves and committed fixtures
        /// match live inventories without a migration path or a schema-generation
        /// bump: those saves persisted the OLD per-instance fingerprint, which no
        /// longer matches anything the live code computes. Recomputing at load
        /// costs one SHA-256 per item and removes the whole migration question.</para>
        ///
        /// <para>Returns null for a null or empty input so callers keep the sparse
        /// "absent vs empty" distinction the codecs rely on.</para>
        /// </summary>
        internal static List<InventoryPayloadItem> NormalizeLoadedInventoryPayloadItems(
            List<InventoryPayloadItem> items, string context)
        {
            if (items == null || items.Count == 0)
                return null;

            int recomputed = 0;
            int kept = 0;
            int changed = 0;

            var byKind = new Dictionary<string, InventoryPayloadItem>(StringComparer.Ordinal);
            var ordered = new List<InventoryPayloadItem>(items.Count);
            int merged = 0;

            for (int i = 0; i < items.Count; i++)
            {
                InventoryPayloadItem item = items[i];
                if (item == null)
                    continue;

                if (item.StoredPartSnapshot != null)
                {
                    string kind = ComputeInventoryPayloadKindKey(item.StoredPartSnapshot);
                    if (!string.IsNullOrEmpty(kind))
                    {
                        recomputed++;
                        if (!string.Equals(kind, item.IdentityHash, StringComparison.Ordinal))
                            changed++;
                        item.IdentityHash = kind;
                    }
                    else
                    {
                        kept++;
                    }
                }
                else
                {
                    kept++;
                }

                if (string.IsNullOrEmpty(item.IdentityHash))
                {
                    ordered.Add(item);
                    continue;
                }

                if (byKind.TryGetValue(item.IdentityHash, out InventoryPayloadItem existing))
                {
                    existing.Quantity += item.Quantity;
                    existing.SlotsTaken += item.SlotsTaken;
                    merged++;
                    continue;
                }

                byKind[item.IdentityHash] = item;
                ordered.Add(item);
            }

            ordered.Sort((a, b) => string.Compare(
                a?.IdentityHash ?? "", b?.IdentityHash ?? "", StringComparison.Ordinal));

            ParsekLog.Verbose("Spawner",
                $"InventoryKindKeyRefresh: node={context ?? "<none>"} " +
                $"items={items.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"recomputed={recomputed.ToString(CultureInfo.InvariantCulture)} " +
                $"kept={kept.ToString(CultureInfo.InvariantCulture)} " +
                $"changed={changed.ToString(CultureInfo.InvariantCulture)} " +
                $"merged={merged.ToString(CultureInfo.InvariantCulture)}");

            return ordered.Count > 0 ? ordered : null;
        }

        private static bool ShouldIncludePartByPersistentId(
            ConfigNode partNode,
            ICollection<uint> partPersistentIds)
        {
            if (partPersistentIds == null || partPersistentIds.Count == 0)
                return true;

            return TryGetPartPersistentId(partNode, out uint pid)
                && partPersistentIds.Contains(pid);
        }

        internal static bool TryGetPartPersistentId(ConfigNode partNode, out uint pid)
        {
            pid = 0;
            if (partNode == null)
                return false;

            string pidStr = partNode.GetValue("persistentId");
            return pidStr != null
                && uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid)
                && pid != 0;
        }

        private static InventoryPayloadItem BuildInventoryPayloadItem(ConfigNode storedPart)
        {
            if (storedPart == null)
                return null;

            string partName = storedPart.GetValue("partName");
            if (string.IsNullOrEmpty(partName))
                return null;

            int quantity = 1;
            string qtyStr = storedPart.GetValue("quantity");
            if (qtyStr != null
                && int.TryParse(qtyStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedQty))
            {
                quantity = parsedQty;
            }

            ConfigNode snapshot = storedPart.CreateCopy();
            snapshot.name = "STOREDPART";

            return new InventoryPayloadItem
            {
                IdentityHash = ComputeInventoryPayloadIdentityHash(snapshot),
                PartName = partName,
                // Same resolver the kind key uses, so two items that share a kind
                // cannot disagree about the variant they display.
                VariantName = NullIfEmpty(ResolveStoredPartVariantName(storedPart)),
                Quantity = quantity,
                SlotsTaken = 1,
                StoredResources = ExtractStoredPartResourceManifest(storedPart),
                StoredPartSnapshot = snapshot
            };
        }

        private static Dictionary<string, ResourceAmount> ExtractStoredPartResourceManifest(
            ConfigNode storedPart)
        {
            if (storedPart == null)
                return null;

            var manifest = new Dictionary<string, ResourceAmount>();
            AddResourceNodesToManifest(storedPart.GetNodes("RESOURCE"), manifest);

            ConfigNode[] partNodes = storedPart.GetNodes("PART");
            for (int i = 0; i < partNodes.Length; i++)
                AddResourceNodesToManifest(partNodes[i].GetNodes("RESOURCE"), manifest);

            return manifest.Count > 0 ? manifest : null;
        }

        private static void AddResourceNodesToManifest(
            ConfigNode[] resourceNodes,
            Dictionary<string, ResourceAmount> manifest)
        {
            if (resourceNodes == null || manifest == null)
                return;

            for (int j = 0; j < resourceNodes.Length; j++)
            {
                string name = resourceNodes[j].GetValue("name");
                if (string.IsNullOrEmpty(name)) continue;
                if (name == "ElectricCharge" || name == "IntakeAir") continue;

                double amount = 0;
                double maxAmount = 0;
                string amountStr = resourceNodes[j].GetValue("amount");
                string maxStr = resourceNodes[j].GetValue("maxAmount");
                if (amountStr != null)
                    double.TryParse(amountStr, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out amount);
                if (maxStr != null)
                    double.TryParse(maxStr, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out maxAmount);

                if (manifest.ContainsKey(name))
                {
                    var ra = manifest[name];
                    ra.amount += amount;
                    ra.maxAmount += maxAmount;
                    manifest[name] = ra;
                }
                else
                {
                    manifest[name] = new ResourceAmount { amount = amount, maxAmount = maxAmount };
                }
            }
        }

        /// <summary>
        /// Collect all non-zero part persistentId values from PART sub-nodes. (#237)
        /// Used to identify stale entries for removal from the global PID registry.
        /// </summary>
        internal static List<uint> CollectPartPersistentIds(ConfigNode vesselNode)
        {
            var ids = new List<uint>();
            if (vesselNode == null) return ids;
            foreach (ConfigNode partNode in vesselNode.GetNodes("PART"))
            {
                string pidStr = partNode.GetValue("persistentId");
                if (pidStr != null
                    && uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pid)
                    && pid != 0)
                {
                    ids.Add(pid);
                }
            }
            return ids;
        }

        /// <summary>
        /// Reads a vessel snapshot's ROOT PART <c>flightID</c> (the <c>uid</c> value of the
        /// PART node the snapshot's <c>root</c> index names). Returns false when the node has
        /// no usable root index or the named part carries no non-zero <c>uid</c>.
        ///
        /// <para>A part <c>flightID</c> is assigned per LAUNCH and is not baked into the
        /// <c>.craft</c>, so unlike <c>persistentId</c> it identifies a physical vessel. This
        /// is how a route connection window records WHICH vessel its endpoint was
        /// (<c>RouteConnectionWindow.EndpointRootPartUId</c>) - the endpoint's own
        /// <c>Vessel</c> is destroyed by <c>Part.Couple</c> moments later.</para>
        /// </summary>
        internal static bool TryReadRootPartFlightId(ConfigNode vesselNode, out uint flightId)
        {
            flightId = 0u;
            if (vesselNode == null) return false;
            if (!int.TryParse(vesselNode.GetValue("root"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int rootIndex))
            {
                return false;
            }
            ConfigNode[] partNodes = vesselNode.GetNodes("PART");
            if (partNodes == null || rootIndex < 0 || rootIndex >= partNodes.Length)
                return false;
            string uidStr = partNodes[rootIndex].GetValue("uid");
            return uidStr != null
                && uint.TryParse(uidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out flightId)
                && flightId != 0u;
        }

        /// <summary>
        /// Regenerate per-part identity fields (persistentId, flightID, missionID, launchID)
        /// on all PART sub-nodes. Returns old→new persistentId mapping for robotics patching. (#234)
        /// When <paramref name="uidMap"/> is non-null it receives the old -> new part flightID
        /// (<c>uid</c>) mapping, for the dock records that name parts by flightID.
        /// Delegate injection allows pure unit testing without KSP runtime.
        /// </summary>
        internal static Dictionary<uint, uint> RegeneratePartIdentities(
            ConfigNode spawnNode,
            Func<uint> generatePersistentId,
            Func<uint> generateFlightId,
            uint missionId,
            uint launchId,
            Dictionary<uint, uint> uidMap = null)
        {
            var pidMap = new Dictionary<uint, uint>();
            if (spawnNode == null) return pidMap;

            string mid = missionId.ToString(CultureInfo.InvariantCulture);
            string lid = launchId.ToString(CultureInfo.InvariantCulture);

            foreach (ConfigNode partNode in spawnNode.GetNodes("PART"))
            {
                // Track old→new persistentId for robotics reference patching
                string oldPidStr = partNode.GetValue("persistentId");
                if (oldPidStr != null
                    && uint.TryParse(oldPidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint oldPid)
                    && oldPid != 0)
                {
                    uint newPid = generatePersistentId();
                    pidMap[oldPid] = newPid;
                    partNode.SetValue("persistentId", newPid.ToString(CultureInfo.InvariantCulture), true);
                }
                else
                {
                    // Part has no valid persistentId — assign a fresh one without mapping
                    uint newPid = generatePersistentId();
                    partNode.SetValue("persistentId", newPid.ToString(CultureInfo.InvariantCulture), true);
                }

                uint newUid = generateFlightId();
                if (uidMap != null
                    && uint.TryParse(partNode.GetValue("uid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint oldUid)
                    && oldUid != 0)
                    uidMap[oldUid] = newUid;
                partNode.SetValue("uid", newUid.ToString(CultureInfo.InvariantCulture), true);
                partNode.SetValue("mid", mid, true);
                partNode.SetValue("launchID", lid, true);
            }

            return pidMap;
        }

        /// <summary>
        /// Remap part persistentId references in ModuleRoboticController (KAL-1000) ConfigNodes
        /// after per-part identity regeneration. Returns count of PIDs remapped. (#238)
        /// </summary>
        internal static int PatchRoboticsReferences(ConfigNode spawnNode, Dictionary<uint, uint> pidMap)
        {
            if (spawnNode == null || pidMap == null || pidMap.Count == 0) return 0;

            int remapCount = 0;
            foreach (ConfigNode partNode in spawnNode.GetNodes("PART"))
            {
                foreach (ConfigNode moduleNode in partNode.GetNodes("MODULE"))
                {
                    if (moduleNode.GetValue("name") != "ModuleRoboticController")
                        continue;

                    foreach (ConfigNode axesNode in moduleNode.GetNodes("CONTROLLEDAXES"))
                    {
                        foreach (ConfigNode axisNode in axesNode.GetNodes("AXIS"))
                        {
                            RemapPidValue(axisNode, "persistentId", pidMap, ref remapCount);
                            foreach (ConfigNode symNode in axisNode.GetNodes("SYMPARTS"))
                                RemapPidValue(symNode, "symPersistentId", pidMap, ref remapCount);
                        }
                    }

                    foreach (ConfigNode actionsNode in moduleNode.GetNodes("CONTROLLEDACTIONS"))
                    {
                        foreach (ConfigNode actionNode in actionsNode.GetNodes("ACTION"))
                        {
                            RemapPidValue(actionNode, "persistentId", pidMap, ref remapCount);
                        }
                    }
                }
            }
            return remapCount;
        }

        /// <summary>
        /// Carries regenerated part flightIDs (<paramref name="uidMap"/>: old -> new, from
        /// <see cref="RegeneratePartIdentities"/>) into every stock external command seat's
        /// <c>occupantID</c>. Stock <c>KerbalSeat.OnSave</c> writes the seated kerbal's part
        /// <c>flightID</c> there and <c>OnStart</c> resolves it through the vessel's
        /// flightID indexer, so a stale value loads the seat empty (Board offered again, the
        /// seat no longer a control source). A zero value or one the map does not hold is
        /// left alone. Returns the values rewritten. Pure.
        /// </summary>
        internal static int RemapSeatOccupantUids(ConfigNode spawnNode, Dictionary<uint, uint> uidMap)
        {
            if (spawnNode == null || uidMap == null || uidMap.Count == 0) return 0;
            int count = 0;
            foreach (ConfigNode partNode in spawnNode.GetNodes("PART"))
            {
                foreach (ConfigNode moduleNode in partNode.GetNodes("MODULE"))
                {
                    if (moduleNode.GetValue("name") != "KerbalSeat")
                        continue;
                    string raw = moduleNode.GetValue("occupantID");
                    if (raw == null
                        || !uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint oldUid)
                        || oldUid == 0u)
                        continue;
                    RemapPidValue(moduleNode, "occupantID", uidMap, ref count);
                }
            }
            return count;
        }

        private static void RemapPidValue(ConfigNode node, string key,
            Dictionary<uint, uint> pidMap, ref int count)
        {
            string val = node.GetValue(key);
            if (val != null
                && uint.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint oldPid)
                && pidMap.TryGetValue(oldPid, out uint newPid))
            {
                node.SetValue(key, newPid.ToString(CultureInfo.InvariantCulture), true);
                count++;
            }
        }

        /// <summary>
        /// Pure summary line for <see cref="VesselSpawner.RemoveDuplicateCrewFromSnapshot"/>: states how
        /// many of the snapshot's seats were vacated and, when ALL of them were, says so
        /// explicitly. The all-empty case is the one worth a named line - the materialized
        /// craft arrives crewless, which looks like a Parsek bug from the outside and is
        /// actually the correct answer (every recorded kerbal is aboard something else that
        /// is still flying). Extracted so the wording is unit-testable without a live scene.
        /// </summary>
        internal static string DescribeCrewDedupOutcome(int snapshotCrewCount, int removedCount)
        {
            int remaining = snapshotCrewCount - removedCount;
            if (remaining < 0) remaining = 0;
            string tail = removedCount > 0 && remaining == 0
                ? " — vessel materializes with EVERY seat empty (all recorded crew are aboard other live vessels)"
                : "";
            return $"Crew dedup: removed {removedCount} of {snapshotCrewCount} snapshot crew, " +
                $"{remaining} seat(s) still filled{tail}";
        }

        /// <summary>
        /// Pure decision: given a set of crew in a snapshot and a set of crew already
        /// on loaded vessels, returns the names that would be duplicated.
        /// Extracted for testability.
        /// </summary>
        internal static HashSet<string> FindDuplicateCrew(
            List<string> snapshotCrew, HashSet<string> existingCrew)
        {
            var duplicates = new HashSet<string>();
            if (snapshotCrew == null || existingCrew == null) return duplicates;

            foreach (string name in snapshotCrew)
            {
                if (!string.IsNullOrEmpty(name) && existingCrew.Contains(name))
                    duplicates.Add(name);
            }
            return duplicates;
        }

        public static void RemoveSpecificCrewFromSnapshot(ConfigNode snapshot, HashSet<string> crewNames)
        {
            if (snapshot == null || crewNames == null || crewNames.Count == 0)
            {
                ParsekLog.VerboseRateLimited("Spawner", "remove-specific-crew-skipped",
                    "RemoveSpecificCrewFromSnapshot skipped due to missing snapshot or crew set", 5.0);
                return;
            }

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                var names = partNode.GetValues("crew");
                if (names.Length == 0) continue;

                var keep = new List<string>();
                bool removedAny = false;
                foreach (string name in names)
                {
                    if (crewNames.Contains(name))
                    {
                        removedAny = true;
                        ParsekLog.Info("Spawner", $"Removed EVA'd crew '{name}' from vessel snapshot");
                    }
                    else
                    {
                        keep.Add(name);
                    }
                }

                if (removedAny)
                {
                    partNode.RemoveValues("crew");
                    foreach (string name in keep)
                        partNode.AddValue("crew", name);
                }
            }
        }

        /// <summary>
        /// Pure decision: determines the corrected situation string when the snapshot's
        /// situation is unsafe (FLYING/SUB_ORBITAL) but the terminal state indicates the
        /// vessel safely reached a stable state. Returns the corrected situation string, or
        /// null if no correction is needed.
        ///
        /// Bug #169: EVA vessels captured with sit=FLYING but terminal state Landed are
        /// destroyed by KSP's on-rails atmospheric pressure check when spawned outside
        /// physics range. The snapshot's sit field must be corrected to match the terminal
        /// state before spawning. Also handles Orbiting: vessels captured during ascent
        /// (FLYING) that achieved orbit.
        /// </summary>
        internal static string ComputeCorrectedSituation(string snapshotSit, TerminalState? terminalState)
        {
            if (string.IsNullOrEmpty(snapshotSit))
                return null;

            bool isUnsafe = snapshotSit.Equals("FLYING", StringComparison.OrdinalIgnoreCase)
                         || snapshotSit.Equals("SUB_ORBITAL", StringComparison.OrdinalIgnoreCase);
            if (!isUnsafe)
                return null;

            if (!terminalState.HasValue)
                return null;

            switch (terminalState.Value)
            {
                case TerminalState.Landed:
                    return "LANDED";
                case TerminalState.Splashed:
                    return "SPLASHED";
                case TerminalState.Orbiting:
                    return "ORBITING";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Corrects the snapshot's situation field if it is FLYING/SUB_ORBITAL but the
        /// terminal state indicates the vessel safely reached a stable state (Landed/Splashed/Orbiting).
        /// Modifies the snapshot in-place. Returns true if a correction was applied.
        /// Bug #169: prevents KSP's on-rails pressure check from destroying spawned vessels.
        /// </summary>
        internal static bool CorrectUnsafeSnapshotSituation(ConfigNode snapshot, TerminalState? terminalState)
        {
            if (snapshot == null)
                return false;

            string currentSit = snapshot.GetValue("sit");
            string corrected = ComputeCorrectedSituation(currentSit, terminalState);
            if (corrected == null)
                return false;

            ApplySituationToNode(snapshot, corrected);
            NormalizeCorrectedSituationLocationFields(snapshot);
            ParsekLog.Info("Spawner",
                $"Corrected unsafe snapshot situation: {currentSit} -> {corrected} " +
                $"(terminal={terminalState}) — prevents on-rails pressure destruction (#169)");
            return true;
        }

        private static void NormalizeCorrectedSituationLocationFields(ConfigNode snapshot)
        {
            if (snapshot == null)
                return;

            snapshot.SetValue("landedAt", string.Empty, true);
            snapshot.SetValue("displaylandedAt", string.Empty, true);
        }

        /// <summary>
        /// Strips ALL crew from a vessel snapshot. Removes crew values from PART
        /// nodes and resets the crewAssignment field. Returns the number of crew removed.
        /// Modifies the snapshot in-place. (#114)
        /// </summary>
        internal static int StripAllCrewFromSnapshot(ConfigNode snapshot)
        {
            if (snapshot == null) return 0;

            int removedCount = 0;
            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                var crewNames = partNode.GetValues("crew");
                if (crewNames.Length > 0)
                {
                    for (int c = 0; c < crewNames.Length; c++)
                    {
                        ParsekLog.Verbose("Spawner",
                            $"StripAllCrewFromSnapshot: removing '{crewNames[c]}'");
                    }
                    removedCount += crewNames.Length;
                    partNode.RemoveValues("crew");
                }
            }
            return removedCount;
        }

        /// <summary>
        /// Pure decision: should a spawn be blocked because all crew in the snapshot are dead?
        /// Returns true when every crew member listed in the snapshot is in the dead names set.
        /// A vessel with no crew at all returns false (crewless vessels can spawn).
        /// Extracted for testability. (#170)
        /// </summary>
        /// <param name="crewNamesInSnapshot">Crew names from the vessel snapshot PART nodes.</param>
        /// <param name="deadCrewNames">Set of crew names known to be dead/missing.</param>
        internal static bool ShouldBlockSpawnForDeadCrew(
            List<string> crewNamesInSnapshot, HashSet<string> deadCrewNames)
        {
            if (crewNamesInSnapshot == null || crewNamesInSnapshot.Count == 0)
            {
                ParsekLog.Verbose("Spawner", "ShouldBlockSpawnForDeadCrew: no crew in snapshot — not blocking");
                return false;
            }

            if (deadCrewNames == null || deadCrewNames.Count == 0)
            {
                ParsekLog.Verbose("Spawner",
                    $"ShouldBlockSpawnForDeadCrew: no dead crew — not blocking ({crewNamesInSnapshot.Count} crew alive)");
                return false;
            }

            int deadCount = 0;
            for (int i = 0; i < crewNamesInSnapshot.Count; i++)
            {
                if (deadCrewNames.Contains(crewNamesInSnapshot[i]))
                    deadCount++;
            }

            bool allDead = deadCount == crewNamesInSnapshot.Count;

            ParsekLog.Verbose("Spawner",
                $"ShouldBlockSpawnForDeadCrew: {deadCount}/{crewNamesInSnapshot.Count} crew dead — " +
                (allDead ? "blocking spawn (all crew dead)" : "allowing spawn (some crew alive)"));

            return allDead;
        }

        /// <summary>
        /// Extracts all crew names from a vessel snapshot's PART nodes.
        /// Returns an empty list if the snapshot is null or has no crew.
        /// </summary>
        internal static List<string> ExtractCrewNamesFromSnapshot(ConfigNode snapshot)
        {
            var names = new List<string>();
            if (snapshot == null) return names;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                var crewNames = partNode.GetValues("crew");
                for (int i = 0; i < crewNames.Length; i++)
                {
                    if (!string.IsNullOrEmpty(crewNames[i]))
                        names.Add(crewNames[i]);
                }
            }
            return names;
        }

        /// <summary>One crew name a spawn snapshot needs that the roster lacks.</summary>
        internal struct MissingCrewEntry
        {
            public string Name;
            /// <summary>The timeline says he rode as a tourist: re-create him as one.</summary>
            public bool AsTourist;
        }

        /// <summary>
        /// The crew names of <paramref name="snapshot"/> that <paramref name="isInRoster"/>
        /// does not know, each once, in snapshot order, with whether
        /// <paramref name="hasTouristEvidence"/> says he rode as a tourist. Pure.
        /// </summary>
        internal static List<MissingCrewEntry> PlanMissingCrew(
            ConfigNode snapshot, Func<string, bool> isInRoster, Func<string, bool> hasTouristEvidence)
        {
            var result = new List<MissingCrewEntry>();
            if (snapshot == null || isInRoster == null) return result;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                foreach (string name in partNode.GetValues("crew"))
                {
                    if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                    if (isInRoster(name)) continue;
                    result.Add(new MissingCrewEntry
                    {
                        Name = name,
                        AsTourist = hasTouristEvidence != null && hasTouristEvidence(name)
                    });
                }
            }
            return result;
        }

        internal static bool IsLandedLikeSnapshot(ConfigNode snapshot)
        {
            if (snapshot == null) return false;

            string sit = snapshot.GetValue("sit") ?? string.Empty;
            if (sit.Equals("LANDED", StringComparison.OrdinalIgnoreCase) ||
                sit.Equals("PRELAUNCH", StringComparison.OrdinalIgnoreCase) ||
                sit.Equals("SPLASHED", StringComparison.OrdinalIgnoreCase))
                return true;

            if (TryGetSnapshotBool(snapshot, "landed", out bool landed) && landed)
                return true;
            if (TryGetSnapshotBool(snapshot, "splashed", out bool splashed) && splashed)
                return true;

            return false;
        }

        private static bool TryGetSnapshotBool(ConfigNode node, string key, out bool value)
        {
            value = false;
            if (node == null) return false;
            string raw = node.GetValue(key);
            return bool.TryParse(raw, out value);
        }

        internal static bool TryGetSnapshotDouble(ConfigNode node, string key, out double value)
        {
            value = 0.0;
            if (node == null) return false;
            string raw = node.GetValue(key);
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Pure decision: determine vessel situation string from altitude, water presence,
        /// speed, and orbital speed. Used by SpawnAtPosition.
        /// 4-way classifier: returns SPLASHED | LANDED | ORBITING | FLYING (never SUB_ORBITAL —
        /// that's handled upstream by `ComputeCorrectedSituation` reading the stored snapshot sit).
        ///
        /// NOTE: there are currently three layers of situation correction applied before a
        /// spawn: (1) <see cref="ComputeCorrectedSituation"/> in `PrepareSnapshotForSpawn`
        /// rewrites the snapshot's `sit` field based on the stored situation, (2) this
        /// method ignores the corrected `snapshot.sit` and classifies fresh from
        /// altitude+velocity, (3) <see cref="OverrideSituationFromTerminalState"/> then
        /// overrides FLYING based on the recording's terminal state. The triple layering
        /// is historical — a cleanup PR could replace this method with "read corrected
        /// snapshot.sit first, fall through to altitude/velocity classifier only if still
        /// FLYING/SUB_ORBITAL" for a cleaner invariant. Tracked under the #264 follow-ups
        /// in docs/dev/todo-and-known-bugs.md.
        /// </summary>
        internal static string DetermineSituation(double alt, bool overWater, double speed, double orbitalSpeed)
        {
            if (alt <= 0 && overWater)
                return "SPLASHED";
            if (alt <= 0)
                return "LANDED";
            if (speed > orbitalSpeed * 0.9)
                return "ORBITING";
            return "FLYING";
        }

        /// <summary>
        /// Pure decision: override a FLYING situation to match the recording's terminal state.
        /// Returns the overridden situation string, or the input unchanged if no override applies.
        ///
        /// When DetermineSituation returns FLYING (alt &gt; 0, speed &lt; 0.9*orbitalSpeed), the
        /// classifier can't tell whether the vessel was flying, walking (EVA), or stationary.
        /// The recording's TerminalStateValue holds the authoritative answer. This override
        /// was originally added for Orbiting/Docked terminals (#176) to prevent on-rails
        /// pressure destruction; #264 extended it to Landed/Splashed for EVA kerbals walking
        /// at alt &gt; 0, which would otherwise hit the OrbitDriver updateMode=UPDATE stale-orbit
        /// bug on the first physics frame after load.
        ///
        /// Only fires when input is FLYING — if the classifier returned ORBITING due to high
        /// speed, we trust that signal over a potentially stale terminal state (a fast-moving
        /// vessel should not be forced into LANDED just because the recording ended with
        /// Landed; that indicates data inconsistency and the safer path is orbital spawn).
        /// </summary>
        internal static string OverrideSituationFromTerminalState(string sit, TerminalState? terminalState)
        {
            if (sit != "FLYING" || !terminalState.HasValue)
                return sit;

            switch (terminalState.Value)
            {
                case TerminalState.Orbiting:
                case TerminalState.Docked:
                    return "ORBITING";
                case TerminalState.Landed:
                    return "LANDED";
                case TerminalState.Splashed:
                    return "SPLASHED";
                default:
                    return sit;
            }
        }

        /// <summary>
        /// Apply a situation string and its corresponding landed/splashed flags to a spawn node.
        /// </summary>
        internal static void ApplySituationToNode(ConfigNode spawnNode, string sit)
        {
            bool landed = sit == "LANDED";
            bool splashed = sit == "SPLASHED";
            spawnNode.SetValue("landed", landed ? "True" : "False", true);
            spawnNode.SetValue("splashed", splashed ? "True" : "False", true);
            spawnNode.SetValue("sit", sit, true);
        }

        /// <summary>
        /// Pure decision: is this spawn situation string a landed-like surface state
        /// (LANDED / SPLASHED)? Used by SpawnAtPosition to decide whether a non-finite
        /// reseeded orbit may be substituted with the canonical surface orbit tuple.
        /// </summary>
        internal static bool IsLandedLikeSituation(string situation)
        {
            if (string.IsNullOrEmpty(situation)) return false;
            return situation.Equals("LANDED", StringComparison.OrdinalIgnoreCase)
                || situation.Equals("SPLASHED", StringComparison.OrdinalIgnoreCase);
        }
    }
}
