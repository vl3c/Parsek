using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal static class RecordingManifestCodec
    {
        #region Crew End States Serialization

        /// <summary>
        /// Serializes CrewEndStates dictionary into CREW_END_STATES ConfigNode children
        /// on the given parent node. Each entry becomes an ENTRY subnode with "name" and "state" keys.
        /// No-op if CrewEndStates is null or empty.
        /// </summary>
        internal static void SerializeCrewEndStates(ConfigNode parent, Recording rec)
        {
            if (rec.CrewEndStates == null || rec.CrewEndStates.Count == 0)
                return;

            ConfigNode cesNode = parent.AddNode("CREW_END_STATES");
            int count = 0;
            foreach (var kvp in rec.CrewEndStates)
            {
                ConfigNode entry = cesNode.AddNode("ENTRY");
                entry.AddValue("name", kvp.Key ?? "");
                entry.AddValue("state", ((int)kvp.Value).ToString(CultureInfo.InvariantCulture));
                count++;
            }
            ParsekLog.Verbose("RecordingStore",
                $"SerializeCrewEndStates: wrote {count} entries for recording={rec.RecordingId}");
        }

        /// <summary>
        /// Deserializes CrewEndStates from a CREW_END_STATES ConfigNode on the given parent.
        /// Sets rec.CrewEndStates to a new dictionary if entries are found, or leaves it null
        /// if the node is absent (backward compatible with legacy recordings).
        /// </summary>
        internal static void DeserializeCrewEndStates(ConfigNode parent, Recording rec)
        {
            ConfigNode cesNode = parent.GetNode("CREW_END_STATES");
            if (cesNode == null)
                return;

            ConfigNode[] entries = cesNode.GetNodes("ENTRY");
            if (entries.Length == 0)
                return;

            rec.CrewEndStates = new Dictionary<string, KerbalEndState>();
            int loaded = 0;
            int skipped = 0;

            for (int i = 0; i < entries.Length; i++)
            {
                string name = entries[i].GetValue("name");
                string stateStr = entries[i].GetValue("state");

                if (string.IsNullOrEmpty(name))
                {
                    skipped++;
                    continue;
                }

                int stateInt;
                if (!int.TryParse(stateStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out stateInt)
                    || !Enum.IsDefined(typeof(KerbalEndState), stateInt))
                {
                    skipped++;
                    continue;
                }

                rec.CrewEndStates[name] = (KerbalEndState)stateInt;
                loaded++;
            }

            ParsekLog.Verbose("RecordingStore",
                $"DeserializeCrewEndStates: loaded={loaded} skipped={skipped} for recording={rec.RecordingId}");
        }

        #endregion

        #region Resource Manifest Serialization

        /// <summary>
        /// Serializes StartResources and EndResources dictionaries into a RESOURCE_MANIFEST
        /// ConfigNode on the given parent. Each resource becomes a RESOURCE child node with
        /// name, startAmount, startMax, endAmount, endMax fields.
        /// No-op if both StartResources and EndResources are null or empty.
        /// </summary>
        internal static void SerializeResourceManifest(ConfigNode parent, Recording rec)
        {
            bool hasStart = rec.StartResources != null && rec.StartResources.Count > 0;
            bool hasEnd = rec.EndResources != null && rec.EndResources.Count > 0;
            if (!hasStart && !hasEnd)
                return;

            ConfigNode manifestNode = parent.AddNode("RESOURCE_MANIFEST");

            // Build merged key set from StartResources ∪ EndResources
            var keys = new HashSet<string>();
            if (hasStart)
                foreach (var k in rec.StartResources.Keys) keys.Add(k);
            if (hasEnd)
                foreach (var k in rec.EndResources.Keys) keys.Add(k);

            int count = 0;
            foreach (var name in keys)
            {
                ConfigNode resNode = manifestNode.AddNode("RESOURCE");
                resNode.AddValue("name", name);

                if (hasStart && rec.StartResources.TryGetValue(name, out var startRa))
                {
                    resNode.AddValue("startAmount", startRa.amount.ToString("R", CultureInfo.InvariantCulture));
                    resNode.AddValue("startMax", startRa.maxAmount.ToString("R", CultureInfo.InvariantCulture));
                }

                if (hasEnd && rec.EndResources.TryGetValue(name, out var endRa))
                {
                    resNode.AddValue("endAmount", endRa.amount.ToString("R", CultureInfo.InvariantCulture));
                    resNode.AddValue("endMax", endRa.maxAmount.ToString("R", CultureInfo.InvariantCulture));
                }

                count++;
            }

            ParsekLog.Verbose("RecordingStore",
                $"SerializeResourceManifest: wrote {count} resource(s) for recording={rec.RecordingId}");
        }

        /// <summary>
        /// Deserializes StartResources and EndResources from a RESOURCE_MANIFEST ConfigNode
        /// on the given parent. Sets the dictionaries if entries are found, or leaves them null
        /// if the node is absent (backward compatible with legacy recordings).
        /// </summary>
        internal static void DeserializeResourceManifest(ConfigNode parent, Recording rec)
        {
            ConfigNode manifestNode = parent.GetNode("RESOURCE_MANIFEST");
            if (manifestNode == null)
                return;

            ConfigNode[] resources = manifestNode.GetNodes("RESOURCE");
            if (resources.Length == 0)
                return;

            int loaded = 0;
            int skipped = 0;

            for (int i = 0; i < resources.Length; i++)
            {
                string name = resources[i].GetValue("name");
                if (string.IsNullOrEmpty(name))
                {
                    skipped++;
                    continue;
                }

                // Parse start fields (if present)
                string startAmountStr = resources[i].GetValue("startAmount");
                string startMaxStr = resources[i].GetValue("startMax");
                if (startAmountStr != null || startMaxStr != null)
                {
                    if (rec.StartResources == null)
                        rec.StartResources = new Dictionary<string, ResourceAmount>();

                    double startAmount = 0;
                    double startMax = 0;
                    if (startAmountStr != null)
                        double.TryParse(startAmountStr, NumberStyles.Float, CultureInfo.InvariantCulture, out startAmount);
                    if (startMaxStr != null)
                        double.TryParse(startMaxStr, NumberStyles.Float, CultureInfo.InvariantCulture, out startMax);

                    rec.StartResources[name] = new ResourceAmount { amount = startAmount, maxAmount = startMax };
                }

                // Parse end fields (if present)
                string endAmountStr = resources[i].GetValue("endAmount");
                string endMaxStr = resources[i].GetValue("endMax");
                if (endAmountStr != null || endMaxStr != null)
                {
                    if (rec.EndResources == null)
                        rec.EndResources = new Dictionary<string, ResourceAmount>();

                    double endAmount = 0;
                    double endMax = 0;
                    if (endAmountStr != null)
                        double.TryParse(endAmountStr, NumberStyles.Float, CultureInfo.InvariantCulture, out endAmount);
                    if (endMaxStr != null)
                        double.TryParse(endMaxStr, NumberStyles.Float, CultureInfo.InvariantCulture, out endMax);

                    rec.EndResources[name] = new ResourceAmount { amount = endAmount, maxAmount = endMax };
                }

                loaded++;
            }

            ParsekLog.Verbose("RecordingStore",
                $"DeserializeResourceManifest: loaded={loaded} skipped={skipped} for recording={rec.RecordingId}");
        }

        /// <summary>
        /// Serializes StartInventory and EndInventory dictionaries into an INVENTORY_MANIFEST
        /// ConfigNode on the given parent. Each item becomes an ITEM child node with
        /// name, startCount, startSlots, endCount, endSlots fields.
        /// No-op if both StartInventory and EndInventory are null or empty.
        /// </summary>
        internal static void SerializeInventoryManifest(ConfigNode parent, Recording rec)
        {
            bool hasStart = rec.StartInventory != null && rec.StartInventory.Count > 0;
            bool hasEnd = rec.EndInventory != null && rec.EndInventory.Count > 0;
            if (!hasStart && !hasEnd)
                return;

            ConfigNode manifestNode = parent.AddNode("INVENTORY_MANIFEST");

            // Build merged key set from StartInventory ∪ EndInventory
            var keys = new HashSet<string>();
            if (hasStart)
                foreach (var k in rec.StartInventory.Keys) keys.Add(k);
            if (hasEnd)
                foreach (var k in rec.EndInventory.Keys) keys.Add(k);

            int count = 0;
            foreach (var name in keys)
            {
                ConfigNode itemNode = manifestNode.AddNode("ITEM");
                itemNode.AddValue("name", name);

                if (hasStart && rec.StartInventory.TryGetValue(name, out var startItem))
                {
                    itemNode.AddValue("startCount", startItem.count.ToString(CultureInfo.InvariantCulture));
                    itemNode.AddValue("startSlots", startItem.slotsTaken.ToString(CultureInfo.InvariantCulture));
                }

                if (hasEnd && rec.EndInventory.TryGetValue(name, out var endItem))
                {
                    itemNode.AddValue("endCount", endItem.count.ToString(CultureInfo.InvariantCulture));
                    itemNode.AddValue("endSlots", endItem.slotsTaken.ToString(CultureInfo.InvariantCulture));
                }

                count++;
            }

            ParsekLog.Verbose("RecordingStore",
                $"SerializeInventoryManifest: wrote {count} item(s) for recording={rec.RecordingId}");
        }

        /// <summary>
        /// Deserializes StartInventory and EndInventory from an INVENTORY_MANIFEST ConfigNode
        /// on the given parent. Sets the dictionaries if entries are found, or leaves them null
        /// if the node is absent (backward compatible with legacy recordings).
        /// </summary>
        internal static void DeserializeInventoryManifest(ConfigNode parent, Recording rec)
        {
            ConfigNode manifestNode = parent.GetNode("INVENTORY_MANIFEST");
            if (manifestNode == null)
                return;

            ConfigNode[] items = manifestNode.GetNodes("ITEM");
            if (items.Length == 0)
                return;

            int loaded = 0;
            int skipped = 0;

            for (int i = 0; i < items.Length; i++)
            {
                string name = items[i].GetValue("name");
                if (string.IsNullOrEmpty(name))
                {
                    skipped++;
                    continue;
                }

                // Parse start fields (if present)
                string startCountStr = items[i].GetValue("startCount");
                string startSlotsStr = items[i].GetValue("startSlots");
                if (startCountStr != null || startSlotsStr != null)
                {
                    if (rec.StartInventory == null)
                        rec.StartInventory = new Dictionary<string, InventoryItem>();

                    int startCount = 0;
                    int startSlots = 0;
                    if (startCountStr != null)
                        int.TryParse(startCountStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out startCount);
                    if (startSlotsStr != null)
                        int.TryParse(startSlotsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out startSlots);

                    rec.StartInventory[name] = new InventoryItem { count = startCount, slotsTaken = startSlots };
                }

                // Parse end fields (if present)
                string endCountStr = items[i].GetValue("endCount");
                string endSlotsStr = items[i].GetValue("endSlots");
                if (endCountStr != null || endSlotsStr != null)
                {
                    if (rec.EndInventory == null)
                        rec.EndInventory = new Dictionary<string, InventoryItem>();

                    int endCount = 0;
                    int endSlots = 0;
                    if (endCountStr != null)
                        int.TryParse(endCountStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out endCount);
                    if (endSlotsStr != null)
                        int.TryParse(endSlotsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out endSlots);

                    rec.EndInventory[name] = new InventoryItem { count = endCount, slotsTaken = endSlots };
                }

                loaded++;
            }

            ParsekLog.Verbose("RecordingStore",
                $"DeserializeInventoryManifest: loaded={loaded} skipped={skipped} for recording={rec.RecordingId}");
        }

        /// <summary>
        /// Serializes StartCrew and EndCrew dictionaries into a CREW_MANIFEST
        /// ConfigNode on the given parent. Each trait becomes a TRAIT child node with
        /// name, startCount, endCount fields.
        /// No-op if both StartCrew and EndCrew are null or empty.
        /// </summary>
        internal static void SerializeCrewManifest(ConfigNode parent, Recording rec)
        {
            bool hasStart = rec.StartCrew != null && rec.StartCrew.Count > 0;
            bool hasEnd = rec.EndCrew != null && rec.EndCrew.Count > 0;
            if (!hasStart && !hasEnd)
                return;

            ConfigNode manifestNode = parent.AddNode("CREW_MANIFEST");

            // Build merged key set from StartCrew ∪ EndCrew
            var keys = new HashSet<string>();
            if (hasStart)
                foreach (var k in rec.StartCrew.Keys) keys.Add(k);
            if (hasEnd)
                foreach (var k in rec.EndCrew.Keys) keys.Add(k);

            int count = 0;
            foreach (var name in keys)
            {
                ConfigNode traitNode = manifestNode.AddNode("TRAIT");
                traitNode.AddValue("name", name);

                if (hasStart && rec.StartCrew.TryGetValue(name, out var startCount))
                {
                    traitNode.AddValue("startCount", startCount.ToString(CultureInfo.InvariantCulture));
                }

                if (hasEnd && rec.EndCrew.TryGetValue(name, out var endCount))
                {
                    traitNode.AddValue("endCount", endCount.ToString(CultureInfo.InvariantCulture));
                }

                count++;
            }

            ParsekLog.Verbose("RecordingStore",
                $"SerializeCrewManifest: wrote {count} trait(s) for recording={rec.RecordingId}");
        }

        /// <summary>
        /// Deserializes StartCrew and EndCrew from a CREW_MANIFEST ConfigNode
        /// on the given parent. Sets the dictionaries if entries are found, or leaves them null
        /// if the node is absent (backward compatible with legacy recordings).
        /// </summary>
        internal static void DeserializeCrewManifest(ConfigNode parent, Recording rec)
        {
            ConfigNode manifestNode = parent.GetNode("CREW_MANIFEST");
            if (manifestNode == null)
                return;

            ConfigNode[] traits = manifestNode.GetNodes("TRAIT");
            if (traits.Length == 0)
                return;

            int loaded = 0;
            int skipped = 0;

            for (int i = 0; i < traits.Length; i++)
            {
                string name = traits[i].GetValue("name");
                if (string.IsNullOrEmpty(name))
                {
                    skipped++;
                    continue;
                }

                // Parse start fields (if present)
                string startCountStr = traits[i].GetValue("startCount");
                if (startCountStr != null)
                {
                    if (rec.StartCrew == null)
                        rec.StartCrew = new Dictionary<string, int>();

                    int startCount = 0;
                    int.TryParse(startCountStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out startCount);
                    rec.StartCrew[name] = startCount;
                }

                // Parse end fields (if present)
                string endCountStr = traits[i].GetValue("endCount");
                if (endCountStr != null)
                {
                    if (rec.EndCrew == null)
                        rec.EndCrew = new Dictionary<string, int>();

                    int endCount = 0;
                    int.TryParse(endCountStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out endCount);
                    rec.EndCrew[name] = endCount;
                }

                loaded++;
            }

            ParsekLog.Verbose("RecordingStore",
                $"DeserializeCrewManifest: loaded={loaded} skipped={skipped} for recording={rec.RecordingId}");
        }

        #endregion
    }
}
