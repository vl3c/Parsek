using System.Collections.Generic;

namespace Parsek
{
    internal sealed class GroupPickerTreeModel
    {
        internal GroupPickerTreeModel(
            HashSet<string> allNames,
            Dictionary<string, List<string>> parentToChildren,
            List<string> rootNames,
            HashSet<string> cycleInvalid)
        {
            AllNames = allNames;
            ParentToChildren = parentToChildren;
            RootNames = rootNames;
            CycleInvalid = cycleInvalid;
        }

        internal HashSet<string> AllNames { get; private set; }
        internal Dictionary<string, List<string>> ParentToChildren { get; private set; }
        internal List<string> RootNames { get; private set; }
        internal HashSet<string> CycleInvalid { get; private set; }
    }

    internal struct GroupMembershipDelta
    {
        internal HashSet<string> Added;
        internal HashSet<string> Removed;
    }

    /// <summary>
    /// Pure selection/tree helpers for the Group Picker popup.
    /// Keeps hierarchy shaping and selection rules headless-testable.
    /// </summary>
    internal static class GroupPickerPresentation
    {
        internal static List<int> NormalizeRecordingSelection(
            IReadOnlyList<int> recordingIndices,
            int committedCount)
        {
            var normalized = new List<int>();
            if (recordingIndices == null || committedCount <= 0)
                return normalized;

            var seen = new HashSet<int>();
            for (int i = 0; i < recordingIndices.Count; i++)
            {
                int recordingIndex = recordingIndices[i];
                if (recordingIndex < 0 || recordingIndex >= committedCount || !seen.Add(recordingIndex))
                    continue;
                normalized.Add(recordingIndex);
            }

            return normalized;
        }

        internal static HashSet<string> GetCommonGroups(
            IReadOnlyList<int> memberIndices,
            IReadOnlyList<Recording> committed,
            IReadOnlyList<string> allGroups)
        {
            var commonGroups = new HashSet<string>();
            if (memberIndices == null || memberIndices.Count == 0
                || committed == null || allGroups == null)
            {
                return commonGroups;
            }

            for (int g = 0; g < allGroups.Count; g++)
            {
                string groupName = allGroups[g];
                bool allInGroup = true;

                for (int m = 0; m < memberIndices.Count; m++)
                {
                    int recordingIndex = memberIndices[m];
                    if (recordingIndex < 0 || recordingIndex >= committed.Count)
                    {
                        allInGroup = false;
                        break;
                    }

                    Recording recording = committed[recordingIndex];
                    if (recording == null
                        || recording.RecordingGroups == null
                        || !recording.RecordingGroups.Contains(groupName))
                    {
                        allInGroup = false;
                        break;
                    }
                }

                if (allInGroup)
                    commonGroups.Add(groupName);
            }

            return commonGroups;
        }

        internal static HashSet<string> BuildExpandedGroups(
            IReadOnlyDictionary<string, string> groupParents,
            IReadOnlyList<string> recordingGroupNames,
            IReadOnlyCollection<string> knownEmptyGroups)
        {
            var expanded = new HashSet<string>();

            if (groupParents != null)
            {
                foreach (KeyValuePair<string, string> kvp in groupParents)
                {
                    if (!string.IsNullOrEmpty(kvp.Value))
                        expanded.Add(kvp.Value);
                }
            }

            AddNames(expanded, recordingGroupNames);
            AddNames(expanded, knownEmptyGroups);
            return expanded;
        }

        internal static GroupPickerTreeModel BuildTreeModel(
            IReadOnlyCollection<string> recordingGroupNames,
            IReadOnlyDictionary<string, string> groupParents,
            IReadOnlyCollection<string> knownEmptyGroups,
            string selectedGroup)
        {
            var allNames = new HashSet<string>();
            AddNames(allNames, recordingGroupNames);
            AddNames(allNames, knownEmptyGroups);

            var parentToChildren = new Dictionary<string, List<string>>();
            if (groupParents != null)
            {
                foreach (KeyValuePair<string, string> kvp in groupParents)
                {
                    if (!string.IsNullOrEmpty(kvp.Key))
                        allNames.Add(kvp.Key);
                    if (!string.IsNullOrEmpty(kvp.Value))
                        allNames.Add(kvp.Value);

                    if (string.IsNullOrEmpty(kvp.Key) || string.IsNullOrEmpty(kvp.Value))
                        continue;

                    if (!parentToChildren.TryGetValue(kvp.Value, out List<string> children))
                    {
                        children = new List<string>();
                        parentToChildren[kvp.Value] = children;
                    }

                    children.Add(kvp.Key);
                }
            }

            foreach (List<string> children in parentToChildren.Values)
                children.Sort(System.StringComparer.OrdinalIgnoreCase);

            var rootNames = new List<string>();
            foreach (string name in allNames)
            {
                if (groupParents == null || !groupParents.ContainsKey(name))
                    rootNames.Add(name);
            }
            rootNames.Sort(System.StringComparer.OrdinalIgnoreCase);

            HashSet<string> cycleInvalid = null;
            if (!string.IsNullOrEmpty(selectedGroup))
            {
                cycleInvalid = new HashSet<string>();
                AddGroupAndDescendants(selectedGroup, parentToChildren, cycleInvalid);
            }

            return new GroupPickerTreeModel(allNames, parentToChildren, rootNames, cycleInvalid);
        }

        internal static HashSet<string> ApplySelectionToggle(
            IReadOnlyCollection<string> currentSelection,
            string groupName,
            bool isChecked,
            bool singleSelect)
        {
            if (singleSelect)
            {
                var updated = new HashSet<string>();
                if (isChecked && !string.IsNullOrEmpty(groupName))
                    updated.Add(groupName);
                return updated;
            }

            var nextSelection = currentSelection != null
                ? new HashSet<string>(currentSelection)
                : new HashSet<string>();

            if (string.IsNullOrEmpty(groupName))
                return nextSelection;

            if (isChecked)
                nextSelection.Add(groupName);
            else
                nextSelection.Remove(groupName);

            return nextSelection;
        }

        internal static bool TryCreateGroupName(
            string rawName,
            IReadOnlyCollection<string> allNames,
            IReadOnlyCollection<string> knownEmptyGroups,
            out string newName)
        {
            newName = rawName != null ? rawName.Trim() : null;
            if (RecordingStore.IsInvalidGroupName(newName))
                return false;
            if (ContainsName(allNames, newName) || ContainsName(knownEmptyGroups, newName))
                return false;

            return true;
        }

        internal static GroupMembershipDelta ComputeMembershipDelta(
            IReadOnlyCollection<string> originalSelection,
            IReadOnlyCollection<string> currentSelection)
        {
            var added = currentSelection != null
                ? new HashSet<string>(currentSelection)
                : new HashSet<string>();
            if (originalSelection != null)
                added.ExceptWith(originalSelection);

            var removed = originalSelection != null
                ? new HashSet<string>(originalSelection)
                : new HashSet<string>();
            if (currentSelection != null)
                removed.ExceptWith(currentSelection);

            return new GroupMembershipDelta
            {
                Added = added,
                Removed = removed
            };
        }

        private static void AddNames(HashSet<string> target, IEnumerable<string> names)
        {
            if (target == null || names == null)
                return;

            foreach (string name in names)
            {
                if (!string.IsNullOrEmpty(name))
                    target.Add(name);
            }
        }

        private static bool ContainsName(IEnumerable<string> names, string value)
        {
            if (names == null)
                return false;

            foreach (string name in names)
            {
                if (name == value)
                    return true;
            }

            return false;
        }

        private static void AddGroupAndDescendants(
            string groupName,
            Dictionary<string, List<string>> parentToChildren,
            HashSet<string> result)
        {
            var pending = new Stack<string>();
            pending.Push(groupName);

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                if (!result.Add(current))
                    continue;

                if (!parentToChildren.TryGetValue(current, out List<string> children))
                    continue;

                for (int i = children.Count - 1; i >= 0; i--)
                    pending.Push(children[i]);
            }
        }
    }
}
