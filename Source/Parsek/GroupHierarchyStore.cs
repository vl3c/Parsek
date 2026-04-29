using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Static store for recording group hierarchy and visibility state.
    /// Extracted from ParsekScenario — pure UI grouping/persistence with
    /// zero coupling to crew, resources, or game state systems.
    /// </summary>
    internal static class GroupHierarchyStore
    {
        // Group hierarchy: child group name → parent group name
        // Internal for test access; production code should use GroupParents/TryGetGroupParent/HasGroupParent
        internal static Dictionary<string, string> groupParents = new Dictionary<string, string>();

        // Hidden groups: group names hidden from the recordings list when Hide is active
        // Internal for test access; production code should use HiddenGroups/IsGroupHidden/AddHiddenGroup/RemoveHiddenGroup
        internal static HashSet<string> hiddenGroups = new HashSet<string>();

        // Hide toggle state (persisted across scene changes and save/load)
        // Internal for test access; production code should use HideActive property
        internal static bool hideActive = true;

        internal static void ResetForTesting()
        {
            groupParents.Clear();
            hiddenGroups.Clear();
            hideActive = true;
        }

        /// <summary>Read-only access to group parent mappings.</summary>
        internal static IReadOnlyDictionary<string, string> GroupParents => groupParents;

        /// <summary>Read-only access to hidden group set.</summary>
        internal static IReadOnlyCollection<string> HiddenGroups => hiddenGroups;

        /// <summary>Whether hidden groups are actively filtered from the UI.</summary>
        internal static bool HideActive
        {
            get => hideActive;
            set => hideActive = value;
        }

        /// <summary>Add a group to the hidden set.</summary>
        internal static void AddHiddenGroup(string groupName)
        {
            hiddenGroups.Add(groupName);
        }

        /// <summary>Remove a group from the hidden set.</summary>
        internal static void RemoveHiddenGroup(string groupName)
        {
            hiddenGroups.Remove(groupName);
        }

        /// <summary>Check if a group is hidden.</summary>
        internal static bool IsGroupHidden(string groupName)
        {
            return hiddenGroups.Contains(groupName);
        }

        /// <summary>
        /// Phase 5 of Rewind-to-Staging (design §7.25). Returns <c>false</c>
        /// for system groups (currently only
        /// <see cref="UnfinishedFlightsGroup.GroupName"/>) so manual drag-into
        /// / group-assign attempts silently reject. Returns <c>true</c> for
        /// every user-defined group and every auto-generated tree group.
        /// </summary>
        internal static bool IsDropTargetAllowed(string groupName)
        {
            if (string.IsNullOrEmpty(groupName)) return false;
            return !UnfinishedFlightsGroup.IsSystemGroup(groupName);
        }

        /// <summary>
        /// Phase 5 of Rewind-to-Staging (design §7.30). Returns <c>false</c>
        /// for system groups — the Unfinished Flights row renders without a
        /// hide checkbox because the list is a diagnostic of the player's
        /// unresolved split siblings. Returns <c>true</c> for every
        /// user-defined group and every auto-generated tree group.
        /// </summary>
        internal static bool CanHide(string groupName)
        {
            if (string.IsNullOrEmpty(groupName)) return false;
            return !UnfinishedFlightsGroup.IsSystemGroup(groupName);
        }

        /// <summary>Try to get the parent of a group.</summary>
        internal static bool TryGetGroupParent(string groupName, out string parentName)
        {
            return groupParents.TryGetValue(groupName, out parentName);
        }

        /// <summary>Check if a group has a parent assigned.</summary>
        internal static bool HasGroupParent(string groupName)
        {
            return groupParents.ContainsKey(groupName);
        }

        #region Hierarchy Operations

        /// <summary>
        /// Walks the parent chain from 'startGroup' upward, returns true if 'targetAncestor'
        /// is found (or equals 'startGroup' itself). Used for cycle detection.
        /// Max depth guard protects against corrupted cycles.
        /// </summary>
        internal static bool IsInAncestorChain(string startGroup, string targetAncestor)
        {
            if (string.IsNullOrEmpty(startGroup) || string.IsNullOrEmpty(targetAncestor))
                return false;
            string current = startGroup;
            for (int depth = 0; depth < 100; depth++)
            {
                if (current == targetAncestor) return true;
                if (!groupParents.TryGetValue(current, out string parent))
                    return false;
                current = parent;
            }
            ParsekLog.Warn("GroupHierarchy", $"IsInAncestorChain: max depth reached for group '{startGroup}' — possible cycle");
            return true;
        }

        public static bool SetGroupParent(string childGroup, string parentGroup)
        {
            if (string.IsNullOrEmpty(childGroup)) return false;

            if (parentGroup == null)
            {
                if (groupParents.Remove(childGroup))
                    ParsekLog.Info("GroupHierarchy", $"Group '{childGroup}' moved to root level");
                return true;
            }

            if (RecordingStore.IsPermanentRootGroup(childGroup))
            {
                ParsekLog.Warn("GroupHierarchy",
                    $"SetGroupParent: cannot assign permanent root group '{childGroup}' to parent '{parentGroup}'");
                return false;
            }

            if (IsInAncestorChain(parentGroup, childGroup))
            {
                ParsekLog.Warn("GroupHierarchy", $"SetGroupParent: cannot assign '{childGroup}' to parent '{parentGroup}' — would create cycle");
                return false;
            }

            groupParents[childGroup] = parentGroup;
            ParsekLog.Info("GroupHierarchy", $"Group '{childGroup}' assigned to parent group '{parentGroup}'");
            return true;
        }

        internal static void EnsurePermanentRootGroupsAreRoot()
        {
            if (groupParents.Count == 0)
                return;

            List<string> demotedPermanentRoots = null;
            foreach (var kvp in groupParents)
            {
                if (!RecordingStore.IsPermanentRootGroup(kvp.Key))
                    continue;

                if (demotedPermanentRoots == null)
                    demotedPermanentRoots = new List<string>();
                demotedPermanentRoots.Add(kvp.Key);
            }

            if (demotedPermanentRoots == null)
                return;

            for (int i = 0; i < demotedPermanentRoots.Count; i++)
            {
                string groupName = demotedPermanentRoots[i];
                string parentName = groupParents[groupName];
                groupParents.Remove(groupName);
                ParsekLog.Info("GroupHierarchy",
                    $"Removed parent '{parentName}' from permanent root group '{groupName}'");
            }
        }

        internal static int PruneUnusedHierarchyEntriesFromCommittedRecordings(string reason)
        {
            // [ERS-exempt] reason: this is a cleanup pass for the raw
            // persisted group hierarchy. It must inspect all committed rows,
            // then explicitly subtract relation-superseded ids so the
            // hierarchy matches the recordings table's display-effective group
            // membership without losing NotCommitted management rows.
            var committed = RecordingStore.CommittedRecordings;
            var scenario = ParsekScenario.Instance;
            var relationSupersededIds = EffectiveState.ComputeSupersededRecordingIdsByRelation(
                committed,
                object.ReferenceEquals(null, scenario) ? null : scenario.RecordingSupersedes);

            var liveGroupNames = new HashSet<string>(StringComparer.Ordinal);
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    var rec = committed[i];
                    if (rec == null)
                        continue;
                    if (!string.IsNullOrEmpty(rec.RecordingId)
                        && relationSupersededIds.Contains(rec.RecordingId))
                        continue;
                    if (rec.RecordingGroups == null)
                        continue;
                    for (int g = 0; g < rec.RecordingGroups.Count; g++)
                    {
                        string groupName = rec.RecordingGroups[g];
                        if (!string.IsNullOrEmpty(groupName))
                            liveGroupNames.Add(groupName);
                    }
                }
            }

            return PruneUnusedHierarchyEntries(liveGroupNames, reason);
        }

        internal static int PruneUnusedHierarchyEntries(
            IReadOnlyCollection<string> liveGroupNames,
            string reason)
        {
            if ((groupParents == null || groupParents.Count == 0)
                && (hiddenGroups == null || hiddenGroups.Count == 0))
                return 0;

            var keep = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            if (liveGroupNames != null)
            {
                foreach (var groupName in liveGroupNames)
                {
                    if (string.IsNullOrEmpty(groupName))
                        continue;
                    if (keep.Add(groupName))
                        queue.Enqueue(groupName);
                }
            }

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                string parent;
                if (!groupParents.TryGetValue(current, out parent))
                    continue;
                if (string.IsNullOrEmpty(parent))
                    continue;
                if (keep.Add(parent))
                    queue.Enqueue(parent);
            }

            var staleChildren = new List<string>();
            foreach (var kvp in groupParents)
            {
                if (!keep.Contains(kvp.Key) || !keep.Contains(kvp.Value))
                    staleChildren.Add(kvp.Key);
            }

            for (int i = 0; i < staleChildren.Count; i++)
                groupParents.Remove(staleChildren[i]);

            int hiddenRemoved = 0;
            if (hiddenGroups != null && hiddenGroups.Count > 0)
            {
                var staleHidden = new List<string>();
                foreach (var groupName in hiddenGroups)
                {
                    if (!keep.Contains(groupName))
                        staleHidden.Add(groupName);
                }

                for (int i = 0; i < staleHidden.Count; i++)
                {
                    if (hiddenGroups.Remove(staleHidden[i]))
                        hiddenRemoved++;
                }
            }

            int removed = staleChildren.Count + hiddenRemoved;
            if (removed > 0)
            {
                ParsekLog.Info("GroupHierarchy",
                    $"Pruned stale group hierarchy reason={reason ?? "<none>"} " +
                    $"hierarchyEntries={staleChildren.Count} hiddenGroups={hiddenRemoved} " +
                    $"liveGroups={keep.Count}");
            }

            return removed;
        }

        /// <summary>
        /// Removes a group from the hierarchy. Promotes its children to root-level.
        /// </summary>
        public static void RemoveGroupFromHierarchy(string groupName)
        {
            if (string.IsNullOrEmpty(groupName)) return;

            hiddenGroups.Remove(groupName);

            // Find grandparent (null if this group was root-level)
            string grandparent;
            groupParents.TryGetValue(groupName, out grandparent);

            // Remove as child
            groupParents.Remove(groupName);

            // Reparent children to grandparent (or root if no grandparent)
            var toReparent = new List<string>();
            foreach (var kvp in groupParents)
            {
                if (kvp.Value == groupName)
                    toReparent.Add(kvp.Key);
            }
            for (int i = 0; i < toReparent.Count; i++)
            {
                if (grandparent != null)
                    groupParents[toReparent[i]] = grandparent;
                else
                    groupParents.Remove(toReparent[i]);
            }

            string destLabel = grandparent != null ? $"reparented under '{grandparent}'" : "promoted to root";
            ParsekLog.Info("GroupHierarchy", $"Group '{groupName}' removed from hierarchy ({toReparent.Count} sub-groups {destLabel})");
        }

        /// <summary>
        /// Returns all descendant group names (recursive).
        /// </summary>
        public static List<string> GetDescendantGroups(string groupName)
        {
            var descendants = new List<string>();
            if (string.IsNullOrEmpty(groupName)) return descendants;
            CollectDescendants(groupName, descendants, 0);
            return descendants;
        }

        private static void CollectDescendants(string groupName, List<string> result, int depth)
        {
            if (depth > 100)
            {
                ParsekLog.Warn("GroupHierarchy", $"CollectDescendants: max depth reached for group '{groupName}' — possible cycle, result truncated");
                return;
            }
            foreach (var kvp in groupParents)
            {
                if (kvp.Value == groupName)
                {
                    result.Add(kvp.Key);
                    CollectDescendants(kvp.Key, result, depth + 1);
                }
            }
        }

        /// <summary>
        /// Renames a group in the hierarchy (updates both keys and values).
        /// </summary>
        public static void RenameGroupInHierarchy(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName == newName)
                return;

            // Update hidden groups
            if (hiddenGroups.Remove(oldName))
                hiddenGroups.Add(newName);

            int updated = 0;

            // Update as child (key)
            if (groupParents.TryGetValue(oldName, out string parent))
            {
                groupParents.Remove(oldName);
                groupParents[newName] = parent;
                updated++;
            }

            // Update as parent (values)
            var toUpdate = new List<string>();
            foreach (var kvp in groupParents)
            {
                if (kvp.Value == oldName)
                    toUpdate.Add(kvp.Key);
            }
            for (int i = 0; i < toUpdate.Count; i++)
            {
                groupParents[toUpdate[i]] = newName;
                updated++;
            }

            if (updated > 0)
                ParsekLog.Info("GroupHierarchy", $"RenameGroupInHierarchy: '{oldName}' → '{newName}' ({updated} hierarchy entries updated)");
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Saves group hierarchy and hidden groups into the scenario ConfigNode.
        /// Called from ParsekScenario.OnSave.
        /// </summary>
        internal static void SaveInto(ConfigNode node)
        {
            PruneUnusedHierarchyEntriesFromCommittedRecordings("save");

            if (groupParents.Count > 0)
            {
                ConfigNode hierarchyNode = node.AddNode("GROUP_HIERARCHY");
                foreach (var kvp in groupParents)
                {
                    ConfigNode entry = hierarchyNode.AddNode("ENTRY");
                    entry.AddValue("child", kvp.Key);
                    entry.AddValue("parent", kvp.Value);
                }
                ParsekLog.Info("GroupHierarchy", $"Saved group hierarchy: {groupParents.Count} entries");
            }

            if (hiddenGroups.Count > 0 || !hideActive)
            {
                ConfigNode hiddenNode = node.AddNode("HIDDEN_GROUPS");
                foreach (var g in hiddenGroups)
                    hiddenNode.AddValue("group", g);
                if (!hideActive)
                    hiddenNode.AddValue("hideActive", "False");
                ParsekLog.Info("GroupHierarchy", $"Saved hidden groups: {hiddenGroups.Count} entries, hideActive={hideActive}");
            }
        }

        /// <summary>
        /// Load group hierarchy from a ConfigNode.
        /// </summary>
        internal static void LoadGroupHierarchy(ConfigNode node)
        {
            groupParents.Clear();

            ConfigNode hierarchyNode = node.GetNode("GROUP_HIERARCHY");
            if (hierarchyNode == null)
            {
                ParsekLog.Info("GroupHierarchy", "Loaded 0 group hierarchy entries (no GROUP_HIERARCHY node)");
                return;
            }

            ConfigNode[] entries = hierarchyNode.GetNodes("ENTRY");
            for (int i = 0; i < entries.Length; i++)
            {
                string child = entries[i].GetValue("child");
                string parent = entries[i].GetValue("parent");
                if (!string.IsNullOrEmpty(child) && !string.IsNullOrEmpty(parent))
                {
                    groupParents[child] = parent;
                }
            }

            // Post-load validation: detect and break corrupted cycles
            var visited = new HashSet<string>();
            var toRemove = new List<string>();
            foreach (var kvp in groupParents)
            {
                visited.Clear();
                string current = kvp.Key;
                bool hasCycle = false;
                while (groupParents.TryGetValue(current, out string p))
                {
                    if (!visited.Add(current)) { hasCycle = true; break; }
                    current = p;
                }
                if (hasCycle && !toRemove.Contains(kvp.Key))
                    toRemove.Add(kvp.Key);
            }
            for (int i = 0; i < toRemove.Count; i++)
            {
                groupParents.Remove(toRemove[i]);
                ParsekLog.Warn("GroupHierarchy", $"LoadGroupHierarchy: broke cycle involving group '{toRemove[i]}'");
            }

            ParsekLog.Info("GroupHierarchy", $"Loaded {groupParents.Count} group hierarchy entries");
        }

        internal static void LoadHiddenGroups(ConfigNode node)
        {
            hiddenGroups.Clear();
            hideActive = true;

            ConfigNode hiddenNode = node.GetNode("HIDDEN_GROUPS");
            if (hiddenNode == null)
            {
                ParsekLog.Info("GroupHierarchy", "Loaded 0 hidden groups (no HIDDEN_GROUPS node)");
                return;
            }

            string[] groups = hiddenNode.GetValues("group");
            if (groups != null)
            {
                for (int i = 0; i < groups.Length; i++)
                {
                    if (!string.IsNullOrEmpty(groups[i]))
                        hiddenGroups.Add(groups[i]);
                }
            }

            // Restore hide toggle state (defaults to true if not saved)
            string hideActiveStr = hiddenNode.GetValue("hideActive");
            if (hideActiveStr != null)
            {
                bool parsed;
                if (bool.TryParse(hideActiveStr, out parsed))
                    hideActive = parsed;
            }
            else
            {
                hideActive = true;
            }

            ParsekLog.Info("GroupHierarchy", $"Loaded {hiddenGroups.Count} hidden groups, hideActive={hideActive}");
        }

        #endregion

        #region Testing Support

        /// <summary>
        /// Resets group hierarchy for testing.
        /// </summary>
        public static void ResetGroupsForTesting()
        {
            groupParents.Clear();
            hiddenGroups.Clear();
            hideActive = true;
        }

        #endregion
    }
}
