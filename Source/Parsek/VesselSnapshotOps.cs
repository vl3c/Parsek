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
    /// and per-part identity regeneration and remapping.
    ///
    /// <para>Nothing here touches live KSP state (no FlightGlobals, no live
    /// Vessel, no roster) or the recording model, which is what lets the ledger,
    /// the logistics stack, the route-proof capture and the UI call it without
    /// reaching into the spawner. Members that need either of those stay in
    /// <see cref="VesselSpawner"/>.</para>
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
        /// Delegate injection allows pure unit testing without KSP runtime.
        /// </summary>
        internal static Dictionary<uint, uint> RegeneratePartIdentities(
            ConfigNode spawnNode,
            Func<uint> generatePersistentId,
            Func<uint> generateFlightId,
            uint missionId,
            uint launchId)
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
    }
}
