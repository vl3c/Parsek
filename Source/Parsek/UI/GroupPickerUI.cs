using System.Collections.Generic;
using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Group picker popup extracted from ParsekUI.
    /// Manages group assignment for recordings, chains, and group-in-group nesting.
    /// </summary>
    internal class GroupPickerUI
    {
        private readonly ParsekUI parentUI;

        // Group picker popup state
        private bool groupPopupOpen;
        private int groupPopupRecIdx = -1;
        private List<int> groupPopupRecIndices;
        private string groupPopupChainId;
        private string groupPopupGroup;
        private Vector2 groupPopupPosition;
        private HashSet<string> groupPopupChecked;
        private HashSet<string> groupPopupOriginal;
        private HashSet<string> groupPopupExpanded;
        private string groupPopupNewName = "";
        private Rect groupPopupRect;
        private Vector2 groupPopupScrollPos;
        private bool isResizingGroupPopup;
        private const float ColW_Group = 50f;
        private const float GroupPopupMinW = 220f;
        private const float GroupPopupMinH = 200f;

        public bool IsOpen => groupPopupOpen;

        internal GroupPickerUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
        }

        public void Close()
        {
            groupPopupOpen = false;
        }

        public void OpenForRecording(int ri, Vector2 mousePos)
        {
            var rec = RecordingStore.CommittedRecordings[ri];
            groupPopupOpen = true;
            groupPopupRecIdx = ri;
            groupPopupRecIndices = null;
            groupPopupChainId = null;
            groupPopupGroup = null;
            groupPopupChecked = rec.RecordingGroups != null
                ? new HashSet<string>(rec.RecordingGroups) : new HashSet<string>();
            groupPopupOriginal = new HashSet<string>(groupPopupChecked);
            groupPopupNewName = "";
            groupPopupPosition = mousePos;
            InitGroupPopupExpansion();
        }

        public void OpenForChain(string chainId, Vector2 mousePos)
        {
            groupPopupOpen = true;
            groupPopupRecIdx = -1;
            groupPopupRecIndices = null;
            groupPopupChainId = chainId;
            groupPopupGroup = null;
            groupPopupChecked = GetCommonGroups(RecordingStore.GetChainMemberIndices(chainId));
            groupPopupOriginal = new HashSet<string>(groupPopupChecked);
            groupPopupNewName = "";
            groupPopupPosition = mousePos;
            InitGroupPopupExpansion();
        }

        public void OpenForRecordings(List<int> recordingIndices, Vector2 mousePos)
        {
            groupPopupOpen = true;
            groupPopupRecIdx = -1;
            groupPopupChainId = null;
            groupPopupGroup = null;
            groupPopupRecIndices = new List<int>();

            var seen = new HashSet<int>();
            var committed = RecordingStore.CommittedRecordings;
            if (recordingIndices != null)
            {
                for (int i = 0; i < recordingIndices.Count; i++)
                {
                    int ri = recordingIndices[i];
                    if (ri < 0 || ri >= committed.Count || !seen.Add(ri))
                        continue;
                    groupPopupRecIndices.Add(ri);
                }
            }

            groupPopupChecked = GetCommonGroups(groupPopupRecIndices);
            groupPopupOriginal = new HashSet<string>(groupPopupChecked);
            groupPopupNewName = "";
            groupPopupPosition = mousePos;
            InitGroupPopupExpansion();
        }

        public void OpenForGroup(string groupName, Vector2 mousePos)
        {
            groupPopupOpen = true;
            groupPopupRecIdx = -1;
            groupPopupRecIndices = null;
            groupPopupChainId = null;
            groupPopupGroup = groupName;
            // For group-in-group: checked = current parent (if any)
            groupPopupChecked = new HashSet<string>();
            string parent;
            if (GroupHierarchyStore.TryGetGroupParent(groupName, out parent))
                groupPopupChecked.Add(parent);
            groupPopupOriginal = new HashSet<string>(groupPopupChecked);
            groupPopupNewName = "";
            groupPopupPosition = mousePos;
            InitGroupPopupExpansion();
        }

        private void InitGroupPopupExpansion()
        {
            // Reset popup rect so it repositions near the clicked G button
            groupPopupRect = new Rect(0, 0, 0, 0);
            isResizingGroupPopup = false;
            groupPopupExpanded = new HashSet<string>();
            // Default: all groups expanded
            foreach (var kvp in GroupHierarchyStore.GroupParents)
            {
                groupPopupExpanded.Add(kvp.Value);
            }
            var allNames = RecordingStore.GetGroupNames();
            for (int i = 0; i < allNames.Count; i++)
                groupPopupExpanded.Add(allNames[i]);
            var knownEmptyGroups = parentUI.KnownEmptyGroups;
            for (int i = 0; i < knownEmptyGroups.Count; i++)
                groupPopupExpanded.Add(knownEmptyGroups[i]);
            groupPopupScrollPos = Vector2.zero;
        }

        private static HashSet<string> GetCommonGroups(List<int> memberIndices)
        {
            var commonGroups = new HashSet<string>();
            if (memberIndices == null || memberIndices.Count == 0)
                return commonGroups;

            var committed = RecordingStore.CommittedRecordings;
            var allGroups = RecordingStore.GetGroupNames();
            for (int g = 0; g < allGroups.Count; g++)
            {
                bool allIn = true;
                for (int m = 0; m < memberIndices.Count; m++)
                {
                    int ri = memberIndices[m];
                    if (ri < 0 || ri >= committed.Count)
                    {
                        allIn = false;
                        break;
                    }

                    var rec = committed[ri];
                    if (rec.RecordingGroups == null || !rec.RecordingGroups.Contains(allGroups[g]))
                    {
                        allIn = false;
                        break;
                    }
                }

                if (allIn)
                    commonGroups.Add(allGroups[g]);
            }

            return commonGroups;
        }

        /// <summary>
        /// Draws the group picker popup. Called from OnGUI or after the recordings window.
        /// </summary>
        public void Draw()
        {
            if (!groupPopupOpen) return;

            // Collect all group names
            var allNames = new HashSet<string>(RecordingStore.GetGroupNames());
            foreach (var kvp in GroupHierarchyStore.GroupParents)
            {
                allNames.Add(kvp.Key);
                allNames.Add(kvp.Value);
            }
            var knownEmptyGroups = parentUI.KnownEmptyGroups;
            for (int i = 0; i < knownEmptyGroups.Count; i++)
                allNames.Add(knownEmptyGroups[i]);

            // For group-in-group popup: determine which groups are cycle-invalid
            HashSet<string> cycleInvalid = null;
            if (groupPopupGroup != null)
            {
                cycleInvalid = new HashSet<string>();
                cycleInvalid.Add(groupPopupGroup);
                var desc = GroupHierarchyStore.GetDescendantGroups(groupPopupGroup);
                for (int i = 0; i < desc.Count; i++)
                    cycleInvalid.Add(desc[i]);
            }

            // Build hierarchy for display
            var parentToChildren = new Dictionary<string, List<string>>();
            foreach (var kvp in GroupHierarchyStore.GroupParents)
            {
                List<string> ch;
                if (!parentToChildren.TryGetValue(kvp.Value, out ch))
                {
                    ch = new List<string>();
                    parentToChildren[kvp.Value] = ch;
                }
                ch.Add(kvp.Key);
            }
            foreach (var ch in parentToChildren.Values)
                ch.Sort(System.StringComparer.OrdinalIgnoreCase);

            var rootNames = new List<string>();
            foreach (var n in allNames)
            {
                if (!GroupHierarchyStore.HasGroupParent(n))
                    rootNames.Add(n);
            }
            rootNames.Sort(System.StringComparer.OrdinalIgnoreCase);

            ParsekUI.HandleResizeDrag(ref groupPopupRect, ref isResizingGroupPopup,
                GroupPopupMinW, GroupPopupMinH, null);

            // Initialize popup rect on first open
            if (groupPopupRect.width < 1f)
            {
                groupPopupRect = new Rect(
                    Mathf.Clamp(groupPopupPosition.x, 0, Screen.width - 280f),
                    Mathf.Clamp(groupPopupPosition.y, 0, Screen.height - 300f),
                    280f, 300f);
            }

            bool isGroupPopup = groupPopupGroup != null;
            string popupTitle = isGroupPopup ? "Set Parent Group" : "Manage Groups";

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            groupPopupRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekGroupPopup".GetHashCode(),
                groupPopupRect,
                (id) => DrawGroupPopupContents(rootNames, parentToChildren, cycleInvalid, allNames, isGroupPopup),
                popupTitle,
                opaqueWindowStyle,
                GUILayout.Width(groupPopupRect.width),
                GUILayout.Height(groupPopupRect.height));
        }

        private void DrawGroupPopupContents(List<string> rootNames,
            Dictionary<string, List<string>> parentToChildren,
            HashSet<string> cycleInvalid, HashSet<string> allNames, bool isGroupPopup)
        {
            groupPopupScrollPos = GUILayout.BeginScrollView(groupPopupScrollPos, GUILayout.ExpandHeight(true));

            // For group-in-group: add "(None / Root)" option
            if (isGroupPopup)
            {
                bool noneChecked = groupPopupChecked.Count == 0;
                bool newNone = GUILayout.Toggle(noneChecked, "(None / Root level)");
                if (newNone && !noneChecked)
                    groupPopupChecked.Clear();
            }

            // Draw group hierarchy with checkboxes
            for (int r = 0; r < rootNames.Count; r++)
                DrawGroupPopupNode(rootNames[r], 0, parentToChildren, cycleInvalid, isGroupPopup);

            GUILayout.EndScrollView();

            GUILayout.Space(3);

            // New group creation
            GUILayout.BeginHorizontal();
            groupPopupNewName = GUILayout.TextField(groupPopupNewName, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("+", GUILayout.Width(25)))
            {
                string newName = groupPopupNewName.Trim();
                var knownEmptyGroups = parentUI.KnownEmptyGroups;
                if (!RecordingStore.IsInvalidGroupName(newName) &&
                    !allNames.Contains(newName) && !knownEmptyGroups.Contains(newName))
                {
                    knownEmptyGroups.Add(newName);
                    if (!isGroupPopup)
                        groupPopupChecked.Add(newName);
                    groupPopupNewName = "";
                    ParsekLog.Info("UI", $"Group '{newName}' created via popup");
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(3);

            // Done / Cancel
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("OK", GUILayout.Width(60)))
            {
                ApplyGroupPopupChanges();
                groupPopupOpen = false;
            }
            if (GUILayout.Button("Cancel", GUILayout.Width(60)))
            {
                groupPopupOpen = false;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            ParsekUI.DrawResizeHandle(groupPopupRect, ref isResizingGroupPopup, null);

            GUI.DragWindow();
        }

        private void DrawGroupPopupNode(string groupName, int depth,
            Dictionary<string, List<string>> parentToChildren,
            HashSet<string> cycleInvalid, bool singleSelect)
        {
            // Skip self + all descendants (can't assign a group to itself or its children)
            if (cycleInvalid != null && cycleInvalid.Contains(groupName))
                return;

            List<string> children;
            bool hasChildren = parentToChildren.TryGetValue(groupName, out children) && children.Count > 0;

            GUILayout.BeginHorizontal();
            if (depth > 0) GUILayout.Space(depth * 12f);

            bool isChecked = groupPopupChecked.Contains(groupName);
            bool newChecked = GUILayout.Toggle(isChecked, "", GUILayout.Width(20));
            if (newChecked != isChecked)
            {
                if (singleSelect)
                {
                    groupPopupChecked.Clear();
                    if (newChecked) groupPopupChecked.Add(groupName);
                }
                else
                {
                    if (newChecked) groupPopupChecked.Add(groupName);
                    else groupPopupChecked.Remove(groupName);
                }
            }

            if (hasChildren)
            {
                bool expanded = groupPopupExpanded.Contains(groupName);
                string arrow = expanded ? "\u25bc" : "\u25b6";
                if (GUILayout.Button(arrow, GUI.skin.label, GUILayout.Width(14)))
                {
                    if (expanded) groupPopupExpanded.Remove(groupName);
                    else groupPopupExpanded.Add(groupName);
                }
            }

            GUILayout.Label(groupName, GUILayout.ExpandWidth(true));

            GUILayout.EndHorizontal();

            // Draw children if expanded
            if (hasChildren && groupPopupExpanded.Contains(groupName))
            {
                for (int c = 0; c < children.Count; c++)
                    DrawGroupPopupNode(children[c], depth + 1, parentToChildren, cycleInvalid, singleSelect);
            }
        }

        private void ApplyGroupPopupChanges()
        {
            var committed = RecordingStore.CommittedRecordings;

            if (groupPopupGroup != null)
            {
                // Group-in-group: set parent
                if (groupPopupChecked.Count == 0)
                {
                    // Remove parent (root level)
                    GroupHierarchyStore.SetGroupParent(groupPopupGroup, null);
                    ParsekLog.Info("UI", $"Group '{groupPopupGroup}' moved to root level");
                }
                else
                {
                    // Set parent to the single checked group
                    foreach (var parent in groupPopupChecked)
                    {
                        GroupHierarchyStore.SetGroupParent(groupPopupGroup, parent);
                        ParsekLog.Info("UI", $"Group '{groupPopupGroup}' parent set to '{parent}'");
                        break;
                    }
                }
            }
            else if (groupPopupChainId != null)
            {
                // Chain: add/remove groups for all chain members
                var added = new HashSet<string>(groupPopupChecked);
                added.ExceptWith(groupPopupOriginal);
                var removed = new HashSet<string>(groupPopupOriginal);
                removed.ExceptWith(groupPopupChecked);

                foreach (var g in added)
                    RecordingStore.AddChainToGroup(groupPopupChainId, g);
                foreach (var g in removed)
                    RecordingStore.RemoveChainFromGroup(groupPopupChainId, g);
                ParsekLog.Info("UI", $"Chain '{groupPopupChainId}' groups changed: +[{string.Join(", ", added)}] -[{string.Join(", ", removed)}]");
            }
            else if (groupPopupRecIndices != null && groupPopupRecIndices.Count > 0)
            {
                var added = new HashSet<string>(groupPopupChecked);
                added.ExceptWith(groupPopupOriginal);
                var removed = new HashSet<string>(groupPopupOriginal);
                removed.ExceptWith(groupPopupChecked);

                for (int i = 0; i < groupPopupRecIndices.Count; i++)
                {
                    int ri = groupPopupRecIndices[i];
                    foreach (var g in added)
                        RecordingStore.AddRecordingToGroup(ri, g);
                    foreach (var g in removed)
                        RecordingStore.RemoveRecordingFromGroup(ri, g);
                }

                ParsekLog.Info("UI",
                    $"Recording selection [{string.Join(", ", groupPopupRecIndices)}] groups changed: +[{string.Join(", ", added)}] -[{string.Join(", ", removed)}]");
            }
            else if (groupPopupRecIdx >= 0 && groupPopupRecIdx < committed.Count)
            {
                // Recording: add/remove groups
                var added = new HashSet<string>(groupPopupChecked);
                added.ExceptWith(groupPopupOriginal);
                var removed = new HashSet<string>(groupPopupOriginal);
                removed.ExceptWith(groupPopupChecked);

                foreach (var g in added)
                    RecordingStore.AddRecordingToGroup(groupPopupRecIdx, g);
                foreach (var g in removed)
                    RecordingStore.RemoveRecordingFromGroup(groupPopupRecIdx, g);
                ParsekLog.Info("UI", $"Recording [{groupPopupRecIdx}] groups changed: +[{string.Join(", ", added)}] -[{string.Join(", ", removed)}]");
            }
        }
    }
}
