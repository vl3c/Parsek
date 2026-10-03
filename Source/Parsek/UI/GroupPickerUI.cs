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
        private string groupPopupHeading = "";
        private Rect groupPopupRect;
        private Vector2 groupPopupScrollPos;
        private bool isResizingGroupPopup;
        internal const float GroupPopupMinW = 260f;
        internal const float GroupPopupMinH = 220f;
        internal const float GroupPopupDefaultW = 320f;
        internal const float GroupPopupDefaultH = 360f;

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
            groupPopupHeading = GroupPickerPresentation.FormatHeading(null, rec.VesselName, 1);
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
            List<int> chainMembers = RecordingStore.GetChainMemberIndices(chainId);
            groupPopupChecked = GroupPickerPresentation.GetCommonGroups(
                chainMembers,
                RecordingStore.CommittedRecordings,
                RecordingStore.GetGroupNames());
            groupPopupOriginal = new HashSet<string>(groupPopupChecked);
            groupPopupNewName = "";
            groupPopupHeading = GroupPickerPresentation.FormatHeading(
                null, SingleRecordingName(chainMembers), chainMembers.Count);
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
            groupPopupHeading = GroupPickerPresentation.FormatHeading(
                null, SingleRecordingName(groupPopupRecIndices), groupPopupRecIndices.Count);
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
            groupPopupHeading = GroupPickerPresentation.FormatHeading(groupName, null, 0);
            groupPopupPosition = mousePos;
            InitGroupPopupExpansion();
        }

        /// <summary>The vessel name of the one recording in <paramref name="indices"/>, or
        /// null when there is not exactly one (the heading then counts them).</summary>
        private static string SingleRecordingName(List<int> indices)
        {
            if (indices == null || indices.Count != 1) return null;
            // [ERS-exempt] reason: indices are into the raw committed list, as above.
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            int ri = indices[0];
            return ri >= 0 && ri < committed.Count && committed[ri] != null
                ? committed[ri].VesselName : null;
        }

        private void InitGroupPopupExpansion()
        {
            // An empty rect is re-placed on the next draw: next to the clicked G button,
            // or centred over the Missions window when the opener had no click point.
            groupPopupRect = new Rect(0, 0, 0, 0);
            isResizingGroupPopup = false;
            groupPopupExpanded = GroupPickerPresentation.BuildExpandedGroups(
                GroupHierarchyStore.GroupParents,
                RecordingStore.GetGroupNames(),
                parentUI.KnownEmptyGroups);
            groupPopupScrollPos = Vector2.zero;
        }

        /// <summary>
        /// Draws the group picker popup after the Missions window, whose rect
        /// (<paramref name="parentWindowRect"/>) a picker opened with no click point centres
        /// over.
        /// </summary>
        public void Draw(Rect parentWindowRect)
        {
            if (!groupPopupOpen) return;

            GroupPickerTreeModel treeModel = GroupPickerPresentation.BuildTreeModel(
                RecordingStore.GetGroupNames(),
                GroupHierarchyStore.GroupParents,
                parentUI.KnownEmptyGroups,
                groupPopupGroup);

            // Place the first-open rect BEFORE the resize/screen fit: the fit would widen an
            // unplaced zero rect to the minimum width at the screen origin, and the placement
            // would then never run (PickerWindowLayout).
            if (groupPopupRect.width < 1f)
            {
                groupPopupRect = PickerWindowLayout.PlaceOnOpen(
                    groupPopupPosition, parentWindowRect,
                    GroupPopupDefaultW, GroupPopupDefaultH, Screen.width, Screen.height);
                ParsekLog.Verbose("UI", PickerWindowLayout.FormatPlacementLog(
                    "Group picker", groupPopupPosition, groupPopupRect,
                    Screen.width, Screen.height));
            }

            ParsekUI.HandleResizeDrag(ref groupPopupRect, ref isResizingGroupPopup,
                GroupPopupMinW, GroupPopupMinH, null);

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
            // The house picker look (PickerWindowLayout): a heading in the shared table
            // section style below the main windows' title gap, then the entries inside the
            // shared dark table body box.
            PickerWindowLayout.DrawTitleGap();
            GUILayout.Label(groupPopupHeading, parentUI.GetTableSectionHeaderStyle());
            groupPopupScrollPos = PickerWindowLayout.BeginEntryList(parentUI, groupPopupScrollPos);

            // For group-in-group: add "(None / Root)" option
            if (isGroupPopup)
            {
                PickerWindowLayout.BeginEntryRow();
                bool noneChecked = groupPopupChecked.Count == 0;
                bool newNone = GUILayout.Toggle(noneChecked, "(None / Root level)");
                if (newNone && !noneChecked)
                    groupPopupChecked.Clear();
                GUILayout.EndHorizontal();
            }

            // Draw group hierarchy with checkboxes
            for (int r = 0; r < treeModel.RootNames.Count; r++)
            {
                DrawGroupPopupNode(
                    treeModel.RootNames[r],
                    0,
                    treeModel.ParentToChildren,
                    treeModel.CycleInvalid,
                    isGroupPopup,
                    parentName: null);
            }

            PickerWindowLayout.EndEntryList();

            GUILayout.Space(3);

            // New group creation
            GUILayout.BeginHorizontal();
            groupPopupNewName = GUILayout.TextField(groupPopupNewName, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("+", GUILayout.Width(PickerWindowLayout.SmallButtonWidth)))
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
            if (GUILayout.Button("OK", GUILayout.Width(PickerWindowLayout.ButtonWidth)))
            {
                ApplyGroupPopupChanges();
                groupPopupOpen = false;
            }
            GUILayout.Space(PickerWindowLayout.ButtonGap);
            if (GUILayout.Button("Cancel", GUILayout.Width(PickerWindowLayout.ButtonWidth)))
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
            HashSet<string> cycleInvalid, bool singleSelect, string parentName)
        {
            // Skip self + all descendants (can't assign a group to itself or its children)
            if (cycleInvalid != null && cycleInvalid.Contains(groupName))
                return;

            List<string> children;
            bool hasChildren = parentToChildren.TryGetValue(groupName, out children) && children.Count > 0;

            PickerWindowLayout.BeginEntryRow();
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

            // Display label only: a nested "Parent / Sub" name drawn under its parent reads
            // "Sub" (the checkbox and every write below keep the full stored name).
            GUILayout.Label(
                GroupPickerPresentation.DisplayLabelUnderParent(groupName, parentName),
                parentUI.GetTableCellStyle(),
                GUILayout.ExpandWidth(true));

            GUILayout.EndHorizontal();

            // Draw children if expanded
            if (hasChildren && groupPopupExpanded.Contains(groupName))
            {
                for (int c = 0; c < children.Count; c++)
                    DrawGroupPopupNode(children[c], depth + 1, parentToChildren, cycleInvalid, singleSelect,
                        groupName);
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
