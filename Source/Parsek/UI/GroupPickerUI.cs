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
        /// <summary>
        /// Phase 5 of Rewind-to-Staging (design §7.25). Pure-static predicate:
        /// returns <c>false</c> iff <paramref name="rec"/> is currently an
        /// Unfinished Flight per <see cref="EffectiveState.IsUnfinishedFlight"/>
        /// AND <paramref name="targetGroup"/> is a manual (user-defined or
        /// auto-generated tree) group — i.e. any non-system group. The
        /// virtual Unfinished Flights group has no stored membership to
        /// uncheck, so removals and system-group targets always return
        /// <c>true</c>. Factored out of <see cref="ApplyGroupPopupChanges"/>
        /// so unit tests can exercise the gate without a live Unity popup.
        /// </summary>
        internal static bool CanAddToUserGroup(Recording rec, string targetGroup)
        {
            if (rec == null) return true;
            if (string.IsNullOrEmpty(targetGroup)) return true;

            // System groups are never valid drop targets; callers should
            // consult GroupHierarchyStore.IsDropTargetAllowed for that gate.
            // If somehow we get asked about a system-group target here,
            // treat as reject.
            if (!GroupHierarchyStore.IsDropTargetAllowed(targetGroup))
                return false;

            // The only additional rule in Phase 5: Unfinished Flights
            // recordings cannot be moved into manual groups (§7.25).
            if (EffectiveState.IsUnfinishedFlight(rec))
                return false;

            return true;
        }

        /// <summary>
        /// Phase 5 of Rewind-to-Staging. Emits the toast + log when a
        /// manual-group add is rejected. Shared by the popup apply path and
        /// unit tests (tests pass null for <c>targetGroup</c> to skip the
        /// ScreenMessages call which requires a live KSP runtime).
        /// </summary>
        internal static void LogAndToastRejectAdd(string recordingId, string targetGroup)
        {
            string recId = string.IsNullOrEmpty(recordingId) ? "<no-id>" : recordingId;
            string grp = string.IsNullOrEmpty(targetGroup) ? "<no-group>" : targetGroup;
            ParsekLog.Verbose("UnfinishedFlights",
                $"Drag reject: rec={recId} target={grp}");

            // ScreenMessages is a Unity singleton that may be null in tests;
            // guard so the log path is always taken even without a UI host.
            try
            {
                ScreenMessages.PostScreenMessage(
                    "Cannot move STASH entries to manual groups",
                    3f, ScreenMessageStyle.UPPER_CENTER);
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("UnfinishedFlights",
                    $"Drag reject toast suppressed (no ScreenMessages host): {ex.GetType().Name}");
            }
        }
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
            // [ERS-exempt] reason: `ri` is an index into RecordingStore.CommittedRecordings
            // passed in from the caller's recordings table (which itself is index-aligned
            // with the raw store). Converting to ERS here would de-align the index.
            // TODO(phase 6+): migrate GroupPickerUI to recording-id-keyed inputs.
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
            groupPopupChecked = GroupPickerPresentation.GetCommonGroups(
                RecordingStore.GetChainMemberIndices(chainId),
                RecordingStore.CommittedRecordings,
                RecordingStore.GetGroupNames());
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
            groupPopupRecIndices = GroupPickerPresentation.NormalizeRecordingSelection(
                recordingIndices,
                RecordingStore.CommittedRecordings.Count);

            groupPopupChecked = GroupPickerPresentation.GetCommonGroups(
                groupPopupRecIndices,
                RecordingStore.CommittedRecordings,
                RecordingStore.GetGroupNames());
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
            groupPopupExpanded = GroupPickerPresentation.BuildExpandedGroups(
                GroupHierarchyStore.GroupParents,
                RecordingStore.GetGroupNames(),
                parentUI.KnownEmptyGroups);
            groupPopupScrollPos = Vector2.zero;
        }

        /// <summary>
        /// Draws the group picker popup. Called from OnGUI or after the recordings window.
        /// </summary>
        public void Draw()
        {
            if (!groupPopupOpen) return;

            GroupPickerTreeModel treeModel = GroupPickerPresentation.BuildTreeModel(
                RecordingStore.GetGroupNames(),
                GroupHierarchyStore.GroupParents,
                parentUI.KnownEmptyGroups,
                groupPopupGroup);

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
            if (opaqueWindowStyle == null)
                return;
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                groupPopupRect = ClickThruBlocker.GUILayoutWindow(
                    "ParsekGroupPopup".GetHashCode(),
                    groupPopupRect,
                    (id) => DrawGroupPopupContents(treeModel, isGroupPopup),
                    popupTitle,
                    opaqueWindowStyle,
                    GUILayout.Width(groupPopupRect.width),
                    GUILayout.Height(groupPopupRect.height));
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
        }

        private void DrawGroupPopupContents(GroupPickerTreeModel treeModel, bool isGroupPopup)
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
            for (int r = 0; r < treeModel.RootNames.Count; r++)
            {
                DrawGroupPopupNode(
                    treeModel.RootNames[r],
                    0,
                    treeModel.ParentToChildren,
                    treeModel.CycleInvalid,
                    isGroupPopup);
            }

            GUILayout.EndScrollView();

            GUILayout.Space(3);

            // New group creation
            GUILayout.BeginHorizontal();
            groupPopupNewName = GUILayout.TextField(groupPopupNewName, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("+", GUILayout.Width(25)))
            {
                var knownEmptyGroups = parentUI.KnownEmptyGroups;
                if (GroupPickerPresentation.TryCreateGroupName(
                    groupPopupNewName,
                    treeModel.AllNames,
                    knownEmptyGroups,
                    out string newName))
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
                groupPopupChecked = GroupPickerPresentation.ApplySelectionToggle(
                    groupPopupChecked,
                    groupName,
                    newChecked,
                    singleSelect);

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
            // [ERS-exempt] reason: group mutations are applied to recordings by
            // index from the live table. See TODO(phase 6+) on OpenForRecording.
            var committed = RecordingStore.CommittedRecordings;
            GroupMembershipDelta delta = GroupPickerPresentation.ComputeMembershipDelta(
                groupPopupOriginal,
                groupPopupChecked);

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
                // Phase 5 (design §7.25): consult CanAddToUserGroup for each
                // chain member and each add target. If ANY member of the chain
                // is an Unfinished Flight, the whole add is rejected for that
                // target group (chains are all-or-nothing for group membership
                // in this popup). Removes are always allowed.
                var chainMemberIndices = RecordingStore.GetChainMemberIndices(groupPopupChainId);
                var rejectedAdds = new HashSet<string>();
                foreach (var g in delta.Added)
                {
                    bool reject = false;
                    string rejectRecId = null;
                    for (int m = 0; m < chainMemberIndices.Count; m++)
                    {
                        int ri = chainMemberIndices[m];
                        if (ri < 0 || ri >= committed.Count) continue;
                        var memberRec = committed[ri];
                        if (!CanAddToUserGroup(memberRec, g))
                        {
                            reject = true;
                            rejectRecId = memberRec != null ? memberRec.RecordingId : null;
                            break;
                        }
                    }
                    if (reject)
                    {
                        rejectedAdds.Add(g);
                        LogAndToastRejectAdd(rejectRecId, g);
                    }
                }

                foreach (var g in delta.Added)
                {
                    if (rejectedAdds.Contains(g)) continue;
                    RecordingStore.AddChainToGroup(groupPopupChainId, g);
                }
                foreach (var g in delta.Removed)
                    RecordingStore.RemoveChainFromGroup(groupPopupChainId, g);
                ParsekLog.Info("UI", $"Chain '{groupPopupChainId}' groups changed: +[{string.Join(", ", delta.Added)}] -[{string.Join(", ", delta.Removed)}] rejected=[{string.Join(", ", rejectedAdds)}]");
            }
            else if (groupPopupRecIndices != null && groupPopupRecIndices.Count > 0)
            {
                int addsAttempted = 0, addsRejected = 0;
                for (int i = 0; i < groupPopupRecIndices.Count; i++)
                {
                    int ri = groupPopupRecIndices[i];
                    var rec = (ri >= 0 && ri < committed.Count) ? committed[ri] : null;
                    foreach (var g in delta.Added)
                    {
                        addsAttempted++;
                        // Phase 5 (design §7.25): gate per-recording so that a
                        // bulk selection mixing Unfinished Flights with regular
                        // recordings only rejects the Unfinished ones; the
                        // regular members are still added.
                        if (!CanAddToUserGroup(rec, g))
                        {
                            addsRejected++;
                            LogAndToastRejectAdd(rec != null ? rec.RecordingId : null, g);
                            continue;
                        }
                        RecordingStore.AddRecordingToGroup(ri, g);
                    }
                    foreach (var g in delta.Removed)
                        RecordingStore.RemoveRecordingFromGroup(ri, g);
                }

                ParsekLog.Info("UI",
                    $"Recording selection [{string.Join(", ", groupPopupRecIndices)}] groups changed: +[{string.Join(", ", delta.Added)}] -[{string.Join(", ", delta.Removed)}] addsAttempted={addsAttempted} addsRejected={addsRejected}");
            }
            else if (groupPopupRecIdx >= 0 && groupPopupRecIdx < committed.Count)
            {
                // Recording: add/remove groups
                var rec = committed[groupPopupRecIdx];
                var rejectedAdds = new HashSet<string>();
                foreach (var g in delta.Added)
                {
                    // Phase 5 (design §7.25): Unfinished Flights cannot be
                    // moved into manual groups; silently skip + toast + log.
                    if (!CanAddToUserGroup(rec, g))
                    {
                        rejectedAdds.Add(g);
                        LogAndToastRejectAdd(rec != null ? rec.RecordingId : null, g);
                        continue;
                    }
                    RecordingStore.AddRecordingToGroup(groupPopupRecIdx, g);
                }
                foreach (var g in delta.Removed)
                    RecordingStore.RemoveRecordingFromGroup(groupPopupRecIdx, g);
                ParsekLog.Info("UI", $"Recording [{groupPopupRecIdx}] groups changed: +[{string.Join(", ", delta.Added)}] -[{string.Join(", ", delta.Removed)}] rejected=[{string.Join(", ", rejectedAdds)}]");
            }
        }
    }
}
