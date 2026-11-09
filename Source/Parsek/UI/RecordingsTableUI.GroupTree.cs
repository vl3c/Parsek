using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The group tree: row visibility, the recursive group draw and its children, descendant collection and the tree data build.
    internal partial class RecordingsTableUI
    {
        /// <summary>
        /// Mirrors the two early-returns at the top of <see cref="DrawRecordingRow"/>:
        /// a row renders only when it is neither inactive-for-display nor hidden
        /// under an active hide filter. Used to land the corner connector on the
        /// last *visible* sibling rather than the last by raw index.
        /// </summary>
        private static bool IsRowVisible(Recording rec, IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (rec == null) return false;
            if (IsInactiveForDisplay(
                    rec,
                    supersedes ?? CurrentRecordingSupersedesForDisplay(),
                    CurrentRecordingRewindRetirementsForDisplay())) return false;
            if (rec.Hidden && GroupHierarchyStore.HideActive) return false;
            return true;
        }

        /// <summary>
        /// True when a display block would render at least its header row.
        /// Single-member blocks render iff the row is visible; multi-member
        /// (chain/grouped) blocks render the header unless the hide filter is
        /// active and every member is hidden, mirroring <see cref="DrawRecordingBlock"/>.
        /// </summary>
        private static bool DisplayBlockRendersAnything(
            GroupDisplayBlock block,
            IReadOnlyList<Recording> committed,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (block.Members == null || block.Members.Count == 0) return false;
            if (block.Members.Count == 1)
                return IsRowVisible(committed[block.Members[0]], supersedes);
            if (!GroupHierarchyStore.HideActive) return true;
            for (int m = 0; m < block.Members.Count; m++)
                if (!committed[block.Members[m]].Hidden) return true;
            return false;
        }

        /// <summary>
        /// True when a child sub-group would render anything. A visible group
        /// always renders its header row; a hidden group (under an active hide
        /// filter) renders only its nested Unfinished Flights escape hatch, if
        /// any. Mirrors the hide-escape early-return in <see cref="DrawGroupTree"/>.
        /// </summary>
        private static bool ChildGroupRendersAnything(string childGroupName)
        {
            if (GroupHierarchyStore.HideActive && GroupHierarchyStore.IsGroupHidden(childGroupName))
            {
                var nested = CollectUnfinishedFlightsForTreeGroup(childGroupName);
                return nested != null && nested.Count > 0;
            }
            return true;
        }

        /// <summary>
        /// Recursively draws a group and its children. Returns true if the recording list was modified.
        /// </summary>
        private bool DrawGroupTree(string groupName, int depth,
            IReadOnlyList<Recording> committed, double now,
            Dictionary<string, List<int>> grpToRecs,
            Dictionary<string, List<int>> chainToRecs,
            Dictionary<string, List<string>> grpChildren,
            IReadOnlyList<RecordingSupersedeRelation> supersedes = null,
            string treeConnector = null,
            string parentGroupName = null)
        {
            // Compute this tree's unfinished-flight members up front so the
            // nested virtual subgroup can be rendered even when the mission
            // group itself is hidden. Unfinished Flights is a system group
            // (design §7.30) and must remain visible regardless of parent
            // hide state — without this, hiding the auto-generated mission
            // group silently made unresolved re-fly opportunities disappear.
            var nestedUnfinished = CollectUnfinishedFlightsForTreeGroup(groupName);
            bool hasNestedUnfinished = nestedUnfinished != null && nestedUnfinished.Count > 0;

            // Skip hidden groups when hide is active — but still render the
            // nested Unfinished Flights subgroup if any, as an escape hatch.
            if (GroupHierarchyStore.HideActive && GroupHierarchyStore.IsGroupHidden(groupName))
            {
                if (hasNestedUnfinished
                    && DrawVirtualUnfinishedFlightsGroup(committed, now, supersedes, depth + 1, nestedUnfinished))
                    return true;
                return false;
            }

            // Collect unique descendant recordings for aggregate controls
            var descendants = new HashSet<int>();
            CollectDescendantRecordings(groupName, grpToRecs, grpChildren, descendants);
            int memberCount = descendants.Count;

            // The folder's own display blocks, built BEFORE the header row: on a mission
            // folder the tree-root vessel's block is absorbed (its segments draw directly
            // under the mission row), and the header's Loop and G cells take over what that
            // block's header offered, so they need its members now.
            List<int> absorbedRootMembers;
            List<GroupDisplayBlock> displayBlocks = ResolveFolderDisplayBlocks(
                groupName, committed, grpToRecs, chainToRecs, out absorbedRootMembers);

            float indent = SelfConnectorIndent(depth);

            // -- Group header --
            GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());

            // Enable checkbox (always at column 0)
            int enabledCount = 0;
            foreach (int idx in descendants)
                if (committed[idx].PlaybackEnabled) enabledCount++;
            bool allEnabled = memberCount > 0 && enabledCount == memberCount;
            bool newEnabled = GUILayout.Toggle(allEnabled,
                new GUIContent("",
                    "Turn ghost playback on or off for every recording in this folder and its subfolders."),
                GUILayout.Width(ColW_Enable));
            if (newEnabled != allEnabled)
            {
                foreach (int idx in descendants)
                    RecordingStore.SetRecordingPlaybackEnabled(committed[idx], newEnabled);
                ParsekLog.Info("UI", $"Set playback enabled for group '{groupName}': enabled={newEnabled}");
            }

            // Spacer for # column (fixed-width label for alignment with recording rows)
            GUILayout.Label("", GUILayout.Width(ColW_Index));

            // Lead-gap before Name column (see DrawRecordingRow for rationale).
            GUILayout.Space(NameColumnLeadGap);

            // Expand/collapse + name (indent inside Name column for sub-groups)
            if (indent > 0f) GUILayout.Space(indent);
            bool expanded = expandedGroups.Contains(groupName);
            string arrow = expanded ? "\u25bc" : "\u25b6";

            if (renamingGroup == groupName)
            {
                bool submitGrp = Event.current.type == EventType.KeyDown &&
                    (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
                bool cancelGrp = Event.current.type == EventType.KeyDown &&
                    Event.current.keyCode == KeyCode.Escape;

                GUILayout.Label(arrow, GUILayout.Width(15));
                GUI.SetNextControlName("GrpRename");
                renamingGroupText = GUILayout.TextField(renamingGroupText, GUILayout.ExpandWidth(true));
                activeRenameRect = GUILayoutUtility.GetLastRect();

                if (!renamingGroupFocused)
                {
                    GUI.FocusControl("GrpRename");
                    renamingGroupFocused = true;
                }

                if (submitGrp)
                {
                    CommitGroupRename(groupName);
                    Event.current.Use();
                }
                else if (cancelGrp)
                {
                    renamingGroup = null;
                    activeRenameRect = default;
                    Event.current.Use();
                }
            }
            else
            {
                // Display label only: "Mission / Debris" drawn under "Mission" reads "Debris".
                // The rename seed below, the stores and the census seam keep the full name.
                string groupLabel = GroupPickerPresentation.DisplayLabelUnderParent(
                    groupName, parentGroupName);
                if (GUILayout.Button(
                    new GUIContent($"{treeConnector ?? ""}{arrow} {groupLabel} ({memberCount})",
                        "Click to expand or collapse; double-click to rename (a name in use, or one with = { }, is refused)."),
                    GUI.skin.label, GUILayout.ExpandWidth(true)))
                {
                    float t = Time.realtimeSinceStartup;
                    if (lastClickedGroup == groupName && t - lastGroupClickTime < DoubleClickThreshold)
                    {
                        // Commit any active rename first
                        if (renamingRecordingId != null)
                            CommitRecordingRename(committed);
                        if (renamingGroup != null)
                            CommitGroupRename(renamingGroup);
                        renamingGroup = groupName;
                        renamingGroupText = groupName;
                        renamingGroupFocused = false;
                        lastClickedGroup = null;
                    }
                    else
                    {
                        if (expanded) expandedGroups.Remove(groupName);
                        else expandedGroups.Add(groupName);
                        expanded = !expanded;
                        lastClickedGroup = groupName;
                        lastGroupClickTime = t;
                        ParsekLog.Verbose("UI", $"Group '{groupName}' {(expanded ? "expanded" : "collapsed")} ({memberCount} recordings)");
                    }
                }
            }

            // Phase placeholder (groups have no phase)
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Phase));

            // Site (from main/root recording if available)
            int grpMainIdx = FindGroupMainRecordingIndex(descendants, committed);
            string grpSite = grpMainIdx >= 0 ? committed[grpMainIdx].LaunchSiteName : null;
            GUILayout.Label(grpSite ?? "", bodyCellLabel, GUILayout.Width(ColW_Site));

            // Launch time (earliest among descendants)
            double grpEarliest = GetGroupEarliestStartUT(descendants, committed);
            string grpLaunchText = (memberCount > 0 && grpEarliest < double.MaxValue)
                ? KSPUtil.PrintDateCompact(grpEarliest, true)
                : "-";
            GUILayout.Label(grpLaunchText, bodyCellLabel, GUILayout.Width(ColW_Launch));

            // Duration: the span the descendants cover (latest end - earliest start), the
            // figure a chain block shows, never a sum that double-counts parallel branches.
            double grpSpanDur = GetGroupSpanDuration(descendants, committed);
            GUILayout.Label(FormatDuration(grpSpanDur), bodyCellLabel, GUILayout.Width(ColW_Dur));

            // Status (closest active T- among descendants)
            string grpStatusText;
            int grpStatusOrder;
            GetGroupStatus(descendants, committed, now, out grpStatusText, out grpStatusOrder);
            GUIStyle grpStatusStyle = grpStatusOrder == 0 ? statusStyleFuture
                : grpStatusOrder == 1 ? statusStyleActive
                : statusStylePast;
            GUILayout.Label(grpStatusText, grpStatusStyle, GUILayout.Width(ColW_Status));

            bool canDisbandGroup = !RecordingGroupStore.IsPermanentGroup(groupName, RecordingStore.CommittedTrees);

            // Group management buttons: custom groups get G + X (wrapped to match the
            // single-G footprint of permanent groups); system-owned groups keep only
            // G via DrawBodyCenteredButton.
            bool grpGClicked;
            bool grpXClicked = false;
            bool grpSegmentsClicked = false;
            if (absorbedRootMembers != null)
            {
                // A mission folder that absorbed its launched vessel's block keeps that
                // block's "every segment to folders" picker as the second half of the cell.
                // No X here: a mission folder is a permanent (auto-generated tree) group.
                DrawBodyCenteredTwoButtons(
                    new GUIContent("G", "Choose which folder this folder sits inside."), true,
                    new GUIContent("S", "Choose which folders every segment of the launched vessel belongs to."), true,
                    ColW_Group, out grpGClicked, out grpSegmentsClicked);
            }
            else if (canDisbandGroup)
            {
                DrawBodyCenteredTwoButtons(
                    new GUIContent("G", "Choose which folder this folder sits inside."), true,
                    new GUIContent("X", "Disband this folder. Its recordings and subfolders move up to the parent - nothing is deleted."), true,
                    ColW_Group, out grpGClicked, out grpXClicked);
            }
            else
            {
                grpGClicked = DrawBodyCenteredButton(
                    new GUIContent("G", "Choose which folder this folder sits inside."),
                    ColW_Group);
            }
            if (grpGClicked)
            {
                var mousePos = ParsekUiScale.GuiPointToScaledRoot(Event.current.mousePosition);
                groupPicker.OpenForGroup(groupName, mousePos);
                ParsekLog.Verbose("UI", $"Group popup opened for group '{groupName}'");
            }
            if (grpXClicked)
            {
                ShowDisbandGroupConfirmation(groupName, descendants, grpChildren);
                ParsekLog.Verbose("UI", $"Disband clicked for group '{groupName}'");
            }
            if (grpSegmentsClicked)
            {
                var mousePos = ParsekUiScale.GuiPointToScaledRoot(Event.current.mousePosition);
                groupPicker.OpenForRecordings(absorbedRootMembers, mousePos);
                ParsekLog.Verbose("UI",
                    $"Group popup opened for the launched vessel's segments of '{groupName}' "
                    + $"({absorbedRootMembers.Count.ToString(CultureInfo.InvariantCulture)} recordings)");
            }

            // Aggregate Loop toggle. Group writes do not call ApplyAutoLoopRange
            // (so a user-customized LoopStartUT/LoopEndUT survives off/on toggle);
            // see BulkSetLoopPlayback.
            //
            // Mutual exclusion (design §0.6): grey OFF + block the group loop when ANY
            // descendant lives on a route-bound tree (safe default: a group spanning a
            // route-bound recording never partially applies).
            //
            // A mission folder that absorbed its launched vessel's block also takes over that
            // block's Loop semantics for those members: they are written WITH the auto loop
            // range (what the block's own toggle did), every other loopable descendant WITHOUT
            // (the folder's own behaviour). The aggregate and the route gate read the union.
            IEnumerable<int> grpLoopScope = descendants;
            if (absorbedRootMembers != null)
            {
                var union = new HashSet<int>(descendants);
                union.UnionWith(absorbedRootMembers);
                grpLoopScope = union;
            }
            bool grpRouteBound = AnyRecordingRouteBound(committed, grpLoopScope);
            var grpLoopAgg = ComputeLoopAggregate(committed, grpLoopScope);
            if (grpLoopAgg.SuppressToggle)
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Loop));
            }
            else
            {
                GUILayout.BeginHorizontal(GUILayout.Width(ColW_Loop));
                GUILayout.FlexibleSpace();
                bool prevGrpLoopEnabled = GUI.enabled;
                if (grpRouteBound) GUI.enabled = false;
                bool grpLoopDrawnEnabled = GUI.enabled;
                bool newLoop = GUILayout.Toggle(
                    grpLoopAgg.AllLoop,
                    grpRouteBound
                        ? new GUIContent("", "Looped by route")
                        : new GUIContent("",
                            "Replay every recording in this folder on a repeating schedule."));
                DisabledHoverEcho.CarryLastControl(
                    grpLoopDrawnEnabled, LoopToggleDisabledReason(null));
                GUI.enabled = prevGrpLoopEnabled;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                if (newLoop != grpLoopAgg.AllLoop)
                {
                    ApplyGroupLoopToggle(groupName, newLoop, grpRouteBound, committed,
                        descendants, grpLoopScope, absorbedRootMembers);
                }
            }

            // Period placeholder
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Period));

            var flight = parentUI.Flight;

            // Find the "main" (earliest non-debris) recording for group-level W and Rewind/Forward buttons
            int mainIdx = FindGroupMainRecordingIndex(descendants, committed);

            // Watch button (flight only) — Bug #382: cycles through eligible
            // descendants on repeated presses instead of toggling a single main.
            if (parentUI.InFlightMode)
            {
                // Bug #382: build the rotation and let the W button cycle through
                // eligible descendants. The helper is pure — we pass a closure
                // capturing the live eligibility probes; cursorRecId comes from
                // groupWatchCursorByGroupName (null on the first press or after prune).
                Func<int, bool> isEligibleForWatch = idx =>
                {
                    if (idx < 0 || idx >= committed.Count) return false;
                    var r = committed[idx];
                    if (r == null || r.IsDebris) return false;
                    if (!flight.HasActiveGhost(idx)) return false;
                    if (!flight.IsGhostOnSameBody(idx)) return false;
                    if (!flight.IsGhostWithinVisualRange(idx)) return false;
                    return true;
                };

                // W* indicator: any descendant currently under watch → W*.
                int watchedIdx = flight.WatchedRecordingIndex;
                bool anyDescendantWatched =
                    watchedIdx >= 0
                    && descendants.Contains(watchedIdx)
                    && watchedIdx < committed.Count
                    && committed[watchedIdx] != null;
                string watchedDescendantRecId = anyDescendantWatched ? committed[watchedIdx].RecordingId : null;

                string cursorRecId;
                groupWatchCursorByGroupName.TryGetValue(groupName, out cursorRecId);
                GhostPlaybackLogic.GroupWatchAdvanceResult rotation = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                    descendants, committed, isEligibleForWatch, cursorRecId, watchedDescendantRecId);

                int nextTargetIdx = rotation.NextRecordingId != null
                    ? GhostPlaybackLogic.FindRecordingIndexById(committed, rotation.NextRecordingId)
                    : -1;

                // nextTargetIdx can legitimately be -1 even if rotation.NextRecordingId != null
                // when the recording has been removed between draws — treat as empty rotation.
                if (nextTargetIdx < 0)
                    rotation = GhostPlaybackLogic.GroupWatchAdvanceResult.Empty;

                bool canWatch = rotation.NextRecordingId != null && nextTargetIdx >= 0 && !rotation.IsToggleOff;
                bool isToggleOffPress = rotation.IsToggleOff && anyDescendantWatched;
                bool isWatching = anyDescendantWatched;

                // Bug #279 transition logging. Key by groupName + mainRecId (unchanged).
                // To keep this log silent on cursor rotations (would otherwise spam
                // once per press), log the eligibility-set fingerprint as
                // resolvedTargetId instead of an individual target id — the set only
                // changes when a vessel enters/leaves eligibility, not on cursor
                // advance. Fingerprint is the sorted, comma-joined RecordingIds.
                if (mainIdx >= 0)
                {
                    string mainRecId = committed[mainIdx].RecordingId;
                    if (!string.IsNullOrEmpty(mainRecId))
                    {
                        string groupWatchKey = groupName + "/" + mainRecId;
                        string resolvedTargetId = BuildEligibleSetFingerprint(
                            descendants, committed, isEligibleForWatch);
                        bool prevGroupCanWatch;
                        string prevResolvedTargetId;
                        if (!lastCanWatchByGroup.TryGetValue(groupWatchKey, out prevGroupCanWatch)
                            || prevGroupCanWatch != canWatch
                            || !lastResolvedWatchTargetByGroup.TryGetValue(groupWatchKey, out prevResolvedTargetId)
                            || prevResolvedTargetId != resolvedTargetId)
                        {
                            lastCanWatchByGroup[groupWatchKey] = canWatch;
                            lastResolvedWatchTargetByGroup[groupWatchKey] = resolvedTargetId;
                            string reason = GetWatchButtonReason(canWatch,
                                hasGhost: nextTargetIdx >= 0,
                                sameBody: nextTargetIdx >= 0,
                                inRange: nextTargetIdx >= 0,
                                isDebris: false);
                            ParsekLog.Info("UI",
                                $"Group Watch button '{groupName}' source=#{mainIdx} \"{committed[mainIdx].VesselName}\" " +
                                $"next={(nextTargetIdx >= 0 ? "#" + nextTargetIdx + " \"" + committed[nextTargetIdx].VesselName + "\"" : "<none>")} " +
                                $"pos={rotation.Position}/{rotation.TotalEligible} eligibleSet=[{resolvedTargetId}] {reason} " +
                                $"{BuildWatchObservabilitySuffix(flight, mainIdx, nextTargetIdx)}");
                        }
                    }
                }

                GUI.enabled = canWatch || isToggleOffPress;
                string watchLabel = isWatching ? "W*" : "W";
                string watchTooltip;
                if (rotation.TotalEligible == 0)
                    watchTooltip = "no watchable vessels in this group";
                else if (isWatching && nextTargetIdx >= 0 && rotation.NextRecordingId != watchedDescendantRecId)
                    watchTooltip = $"switch to {committed[nextTargetIdx].VesselName}";
                else if (isWatching && rotation.IsToggleOff)
                    watchTooltip = "exit watch (no other watchable vessels)";
                else if (nextTargetIdx >= 0)
                    watchTooltip = $"enter watch on {committed[nextTargetIdx].VesselName}";
                else
                    watchTooltip = GetWatchButtonTooltip(isWatching, false, false, false, false);

                if (DrawBodyCenteredButton(new GUIContent(watchLabel, watchTooltip), ColW_Watch))
                {
                    string beforeFocus = flight.DescribeWatchFocusForLogs();
                    string beforeEligibility = BuildWatchObservabilitySuffix(flight, mainIdx, nextTargetIdx);
                    string pressKind;
                    if (isToggleOffPress)
                    {
                        flight.ExitWatchMode();
                        groupWatchCursorByGroupName.Remove(groupName);
                        pressKind = "toggle-off";
                    }
                    else if (nextTargetIdx >= 0)
                    {
                        flight.EnterWatchMode(nextTargetIdx);
                        groupWatchCursorByGroupName[groupName] = rotation.NextRecordingId;
                        pressKind = string.IsNullOrEmpty(cursorRecId)
                            ? "enter"
                            : (rotation.IsWrap ? "wrap" : "advance");
                    }
                    else
                    {
                        // Unreachable: GUI.enabled is canWatch || isToggleOffPress,
                        // and canWatch==true implies nextTargetIdx>=0. If this branch
                        // ever fires, the enable-gate has drifted — log a warning so
                        // the regression surfaces instead of silently no-opping.
                        ParsekLog.Warn("UI",
                            $"Group '{groupName}' W button press reached unreachable branch " +
                            $"(canWatch={canWatch} isToggleOffPress={isToggleOffPress} nextTargetIdx={nextTargetIdx}) — " +
                            "enable-gate and press-dispatch are out of sync");
                        pressKind = "no-op";
                    }
                    ParsekLog.Info("UI",
                        $"Group '{groupName}' W button: {pressKind} " +
                        $"next={(nextTargetIdx >= 0 ? "#" + nextTargetIdx + " \"" + committed[nextTargetIdx].VesselName + "\"" : "<none>")} " +
                        $"pos={rotation.Position}/{rotation.TotalEligible} " +
                        $"cursorBefore={cursorRecId ?? "<null>"} " +
                        $"before={beforeEligibility} beforeFocus={beforeFocus} afterFocus={flight.DescribeWatchFocusForLogs()}");
                }
                GUI.enabled = true;
            }

            // Rewind / Forward button. Forward still targets the group's main
            // recording because "next launch" is a single group-level time
            // affordance. Rewind scans descendants because a parent folder is
            // an escalation surface for any child launch rewind.
            bool isRecording = parentUI.InFlightMode && flight.IsRecording;
            int forwardIdx = FindAggregateForwardRecordingIndex(descendants, committed, now);
            int grpRewindIdx = forwardIdx >= 0
                ? -1
                : FindGroupLegacyRewindRecordingIndex(descendants, committed, now);
            // Shown once: a folder nested under a row that already offers this exact target
            // (a "/ Debris" folder under its mission, say) draws a blank cell.
            bool grpTimeSuppressed = forwardIdx >= 0
                ? IsTimeTargetShownByEnclosingRow(forwardIdx, enclosingForwardTargetIdx)
                : IsTimeTargetShownByEnclosingRow(grpRewindIdx, enclosingRewindTargetIdx);
            LogEnclosingTimeSuppressionTransition("group:" + groupName, grpTimeSuppressed,
                $"Group '{groupName}'");
            if (grpTimeSuppressed)
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Rewind));
            }
            else if (forwardIdx >= 0)
            {
                var mainRec = committed[forwardIdx];

                string ffReason;
                bool canFF = RewindLauncher.CanFastForward(mainRec, out ffReason, isRecording: isRecording);
                GUI.enabled = canFF;
                string tooltip = canFF ? "Fast-forward to this launch" : ffReason;
                if (DrawRewindColumnButton(new GUIContent(FastForwardActionLabel, tooltip)))
                {
                    ParsekLog.Info("UI", $"Group '{groupName}' Forward button: #{forwardIdx} \"{mainRec.VesselName}\"");
                    ShowFastForwardConfirmation(mainRec);
                }
                GUI.enabled = true;
            }
            else if (grpRewindIdx >= 0)
            {
                int rewindIdx = grpRewindIdx;
                var rewindRec = committed[rewindIdx];
                string rewindReason;
                bool canRewind = RewindLauncher.CanRewind(rewindRec, out rewindReason, isRecording: isRecording);
                GUI.enabled = canRewind;
                string targetName = string.IsNullOrEmpty(rewindRec.VesselName)
                    ? "this launch"
                    : rewindRec.VesselName;
                string tooltip = canRewind ? $"Rewind to launch: {targetName}" : rewindReason;
                if (DrawRewindColumnButton(new GUIContent(RewindActionLabel, tooltip)))
                {
                    ParsekLog.Info("UI", $"Group '{groupName}' Rewind button: #{rewindIdx} \"{rewindRec.VesselName}\"");
                    ShowRewindConfirmation(rewindRec);
                }
                GUI.enabled = true;
            }
            else
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Rewind));
            }

            // Group rows do not expose aggregate Re-Fly actions. Stash/Fly/Seal
            // remain per-recording because each action targets a specific RP slot.
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_ReFly));

            // Hide group checkbox — toggles Hidden on all member recordings
            int hiddenCount = 0;
            foreach (int idx in descendants)
                if (committed[idx].Hidden) hiddenCount++;
            bool allHidden = memberCount > 0 && hiddenCount == memberCount;
            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Hide));
            GUILayout.FlexibleSpace();
            bool newAllHidden = GUILayout.Toggle(allHidden,
                new GUIContent("",
                    "Archive every recording in this folder, so the whole folder drops out while the Archive filter is on."));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (newAllHidden != allHidden)
            {
                ApplyGroupArchiveToggle(groupName, descendants, committed, memberCount, newAllHidden);
            }

            GUILayout.EndHorizontal();

            if (!expanded) return false;

            // The rows drawn under this folder compare their own R / FF against what this
            // row actually drew (or, when it drew a blank, against what its own parent drew).
            int savedEnclosingRewind = enclosingRewindTargetIdx;
            int savedEnclosingForward = enclosingForwardTargetIdx;
            ResolveChildEnclosingTimeTargets(
                forwardIdx, grpRewindIdx, grpTimeSuppressed,
                savedEnclosingForward, savedEnclosingRewind,
                out enclosingForwardTargetIdx, out enclosingRewindTargetIdx);
            try
            {
                return DrawGroupChildren(groupName, depth, committed, now,
                    grpToRecs, chainToRecs, grpChildren, supersedes,
                    nestedUnfinished, hasNestedUnfinished, displayBlocks);
            }
            finally
            {
                enclosingRewindTargetIdx = savedEnclosingRewind;
                enclosingForwardTargetIdx = savedEnclosingForward;
            }
        }

        /// <summary>
        /// A folder's own display blocks, or null when it has no direct members. On a mission
        /// folder the tree-root vessel's block is flattened into single rows and its members
        /// come back in <paramref name="absorbedRootMembers"/> (null when nothing was
        /// absorbed); a change of absorption is logged once.
        /// </summary>
        internal List<GroupDisplayBlock> ResolveFolderDisplayBlocks(string groupName,
            IReadOnlyList<Recording> committed,
            Dictionary<string, List<int>> grpToRecs,
            Dictionary<string, List<int>> chainToRecs,
            out List<int> absorbedRootMembers)
        {
            List<int> directMembers;
            grpToRecs.TryGetValue(groupName, out directMembers);
            List<GroupDisplayBlock> displayBlocks = directMembers != null
                ? BuildGroupDisplayBlocks(groupName, directMembers, committed, chainToRecs)
                : null;
            absorbedRootMembers = null;
            if (displayBlocks != null)
            {
                RecordingTree missionTree = MissionGroupLink.FindTreeByRootGroup(groupName);
                string rootBlockKey = missionTree != null
                    ? ResolveRootVesselBlockKey(groupName, ResolveTreeRootRecording(missionTree))
                    : null;
                int absorbedIdx = FindRootVesselBlockIndex(displayBlocks, rootBlockKey);
                if (absorbedIdx >= 0)
                {
                    absorbedRootMembers = displayBlocks[absorbedIdx].Members;
                    displayBlocks = FlattenAbsorbedBlock(displayBlocks, absorbedIdx, groupName);
                }
                LogRootBlockAbsorptionTransition(groupName, rootBlockKey, absorbedRootMembers);
            }
            return displayBlocks;
        }

        /// <summary>
        /// Writes a folder Loop toggle change. A route-bound scope is refused and logged; on a
        /// mission folder that absorbed its launched vessel's block those members are written
        /// WITH the auto loop range and every other loopable descendant WITHOUT; otherwise
        /// every descendant is written without it.
        /// </summary>
        internal static void ApplyGroupLoopToggle(string groupName, bool newLoop, bool grpRouteBound,
            IReadOnlyList<Recording> committed, HashSet<int> descendants,
            IEnumerable<int> grpLoopScope, List<int> absorbedRootMembers)
        {
            if (grpRouteBound)
            {
                ParsekLog.Info("RouteGuard",
                    $"Recordings-tab group Loop blocked for '{groupName}' (a descendant is on a route-bound tree); request={newLoop} ignored");
            }
            else if (absorbedRootMembers != null)
            {
                SplitAbsorbedLoopWrite(grpLoopScope, absorbedRootMembers,
                    out List<int> withAutoRange, out List<int> withoutAutoRange);
                int rootWritten = BulkSetLoopPlayback(
                    committed, withAutoRange, newLoop, applyAutoRange: true);
                int otherWritten = BulkSetLoopPlayback(
                    committed, withoutAutoRange, newLoop, applyAutoRange: false);
                ParsekLog.Info("UI",
                    $"Group '{groupName}' loop set to {newLoop} "
                    + $"(launched vessel {rootWritten.ToString(CultureInfo.InvariantCulture)} "
                    + "recordings with auto loop range, other "
                    + $"{otherWritten.ToString(CultureInfo.InvariantCulture)} without)");
            }
            else
            {
                int written = BulkSetLoopPlayback(committed, descendants, newLoop, applyAutoRange: false);
                ParsekLog.Info("UI", $"Group '{groupName}' loop set to {newLoop} ({written} recordings)");
            }
        }

        /// <summary>
        /// Writes a folder Archive toggle change over every descendant, or refuses a hide that
        /// would bury an Unfinished Flight (warning plus a screen message naming the count).
        /// </summary>
        internal void ApplyGroupArchiveToggle(string groupName, HashSet<int> descendants,
            IReadOnlyList<Recording> committed, int memberCount, bool newAllHidden)
        {
            // The per-row Archive checkbox refuses to hide an Unfinished Flight
            // (see DrawRecordingRow: rewind access must remain visible). This
            // path wrote Hidden over every descendant with no such check, so
            // archiving a FOLDER buried exactly the re-flyable flight the per-row
            // control exists to protect (finding P17). Same guard, group
            // granularity: refuse the whole toggle so the folder's Hidden flag and
            // its members' can never disagree, and name the count so the player
            // knows what to resolve. Only the HIDE direction is gated - un-hiding
            // an Unfinished Flight is the recovery path, never a loss.
            int unfinishedCount = 0;
            if (newAllHidden)
            {
                foreach (int idx in descendants)
                    if (EffectiveState.IsUnfinishedFlight(committed[idx]))
                        unfinishedCount++;
            }

            if (IsArchiveRefusedForUnfinishedFlight(newAllHidden, unfinishedCount > 0))
            {
                ParsekLog.Warn("UnfinishedFlights",
                    $"Group hide-all refused for group '{groupName}': "
                    + $"{unfinishedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} of "
                    + $"{memberCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                    + "member(s) are Unfinished Flights; rewind access must remain "
                    + "visible (design §7.33)");
                ParsekLog.ScreenMessage(
                    BuildGroupArchiveRefusedMessage(groupName, unfinishedCount), 4f);
            }
            else
            {
                foreach (int idx in descendants)
                    committed[idx].Hidden = newAllHidden;
                // Also update group-level visibility
                if (newAllHidden)
                    GroupHierarchyStore.AddHiddenGroup(groupName);
                else
                    GroupHierarchyStore.RemoveHiddenGroup(groupName);
                NotifyTimelineOfArchiveChange();
                ParsekLog.Info("UI",
                    $"Group '{groupName}' hide-all={newAllHidden} ({memberCount} recordings)");
            }
        }

        /// <summary>
        /// Draws an expanded folder's children. Returns true if the recording list was
        /// modified (the caller stops iterating).
        /// </summary>
        private bool DrawGroupChildren(string groupName, int depth,
            IReadOnlyList<Recording> committed, double now,
            Dictionary<string, List<int>> grpToRecs,
            Dictionary<string, List<int>> chainToRecs,
            Dictionary<string, List<string>> grpChildren,
            IReadOnlyList<RecordingSupersedeRelation> supersedes,
            IReadOnlyList<Recording> nestedUnfinished,
            bool hasNestedUnfinished,
            List<GroupDisplayBlock> displayBlocks)
        {
            // -- Draw children --
            // Children render in three sections, in order: display blocks
            // (chain/grouped blocks + single recording rows), child sub-groups,
            // then the nested Unfinished Flights virtual group. Exactly one
            // section owns the corner (└─) connector; the last child in that
            // section that actually renders gets the corner, every earlier
            // sibling gets the tee (├─). Sections / rows that render nothing
            // (filtered out under the hide filter, inactive-for-display, or a
            // hidden child group with no escape hatch) are excluded from the
            // "last visible" computation so the corner never lands on a row
            // that draws nothing. `displayBlocks` arrives already built (and, on a mission
            // folder, with the launched vessel's block flattened into single rows).
            int lastVisibleBlockIdx = -1;
            if (displayBlocks != null)
            {
                for (int i = 0; i < displayBlocks.Count; i++)
                    if (DisplayBlockRendersAnything(displayBlocks[i], committed, supersedes))
                        lastVisibleBlockIdx = i;
            }

            List<string> children;
            grpChildren.TryGetValue(groupName, out children);
            int lastRenderableChildGroupIdx = -1;
            if (children != null)
            {
                for (int c = 0; c < children.Count; c++)
                    if (ChildGroupRendersAnything(children[c]))
                        lastRenderableChildGroupIdx = c;
            }

            TreeChildSection cornerSection = ResolveLastChildSection(
                hasVisibleBlock: lastVisibleBlockIdx >= 0,
                hasRenderableChildGroup: lastRenderableChildGroupIdx >= 0,
                hasRenderableVirtual: hasNestedUnfinished);

            // STASH is a mirror, not a destination: when this tree owns
            // Unfinished Flight members (rendered below as the nested
            // virtual subgroup), the same recordings remain in the regular
            // member list and render under their natural mission tree
            // group too. The nested STASH subgroup just surfaces the
            // Fly/Seal-eligible rows so they're easy to act on without
            // hunting through the tree.
            if (displayBlocks != null)
            {
                for (int i = 0; i < displayBlocks.Count; i++)
                {
                    var block = displayBlocks[i];
                    if (block.Members == null || block.Members.Count == 0)
                        continue;

                    string connector = TreeConnector(
                        cornerSection == TreeChildSection.Block && i == lastVisibleBlockIdx);
                    if (block.Members.Count > 1)
                    {
                        if (DrawGroupedRecordingBlock(block.Key, block.DisplayName,
                            block.Members, depth + 1, committed, now, supersedes, connector))
                            return true;
                    }
                    else if (DrawRecordingRow(block.Members[0], committed, now, ChildConnectorIndent(depth), supersedes, connector))
                        return true;
                }
            }

            // Draw child groups
            if (children != null)
            {
                for (int c = 0; c < children.Count; c++)
                {
                    string connector = TreeConnector(
                        cornerSection == TreeChildSection.ChildGroup && c == lastRenderableChildGroupIdx);
                    if (DrawGroupTree(children[c], depth + 1, committed, now,
                        grpToRecs, chainToRecs, grpChildren, supersedes, connector,
                        parentGroupName: groupName))
                        return true;
                }
            }

            // Nested Unfinished Flights: if this group is the auto-generated
            // root group of a tree that has any Unfinished Flight members,
            // render the virtual group as an indented sub-entry. This replaces
            // the pre-2026-04-24 design where the virtual group sat at root
            // level, detached from the mission it belongs to.
            // `nestedUnfinished` was already computed at the top of
            // DrawGroupTree so the hide-escape path can use it too.
            if (hasNestedUnfinished
                && DrawVirtualUnfinishedFlightsGroup(committed, now, supersedes, depth + 1, nestedUnfinished,
                    TreeConnector(cornerSection == TreeChildSection.Virtual)))
                return true;

            return false;
        }

        /// <summary>
        /// Recursively collects all unique recording indices under a group.
        /// </summary>
        private void CollectDescendantRecordings(string groupName,
            Dictionary<string, List<int>> grpToRecs,
            Dictionary<string, List<string>> grpChildren,
            HashSet<int> result)
        {
            List<int> direct;
            if (grpToRecs.TryGetValue(groupName, out direct))
                for (int i = 0; i < direct.Count; i++)
                    result.Add(direct[i]);

            List<string> children;
            if (grpChildren.TryGetValue(groupName, out children))
                for (int c = 0; c < children.Count; c++)
                    CollectDescendantRecordings(children[c], grpToRecs, grpChildren, result);
        }

        /// <summary>
        /// Builds the group tree data structures used to render the recordings tree.
        /// No IMGUI calls, but not pure: falls back to the active scenario
        /// supersede list when none is passed.
        /// </summary>
        internal static void BuildGroupTreeData(
            IReadOnlyList<Recording> committed, int[] sortedIndices,
            List<string> KnownEmptyGroups,
            out Dictionary<string, List<int>> grpToRecs,
            out Dictionary<string, List<int>> chainToRecs,
            out Dictionary<string, List<string>> grpChildren,
            out List<string> rootGrps,
            out HashSet<string> rootChainIds,
            IReadOnlyList<RecordingSupersedeRelation> supersedes = null,
            IReadOnlyList<RecordingRewindRetirement> retirements = null)
        {
            // group name -> list of recording indices directly in that group
            grpToRecs = new Dictionary<string, List<int>>();
            // chainId -> list of recording indices
            chainToRecs = new Dictionary<string, List<int>>();
            supersedes = supersedes ?? CurrentRecordingSupersedesForDisplay();
            retirements = retirements ?? CurrentRecordingRewindRetirementsForDisplay();

            for (int row = 0; row < sortedIndices.Length; row++)
            {
                int ri = sortedIndices[row];
                var rec = committed[ri];
                if (IsInactiveForDisplay(rec, supersedes, retirements))
                    continue;

                // Multi-group: recording appears in each group it belongs to
                if (rec.RecordingGroups != null)
                {
                    for (int g = 0; g < rec.RecordingGroups.Count; g++)
                    {
                        string grp = rec.RecordingGroups[g];
                        List<int> list;
                        if (!grpToRecs.TryGetValue(grp, out list))
                        {
                            list = new List<int>();
                            grpToRecs[grp] = list;
                        }
                        if (!list.Contains(ri)) list.Add(ri);
                    }
                }

                // Build chain lookup
                if (!string.IsNullOrEmpty(rec.ChainId))
                {
                    List<int> list;
                    if (!chainToRecs.TryGetValue(rec.ChainId, out list))
                    {
                        list = new List<int>();
                        chainToRecs[rec.ChainId] = list;
                    }
                    list.Add(ri);
                }
            }

            // Build parent -> children map from hierarchy
            grpChildren = new Dictionary<string, List<string>>();
            var allGrpNames = new HashSet<string>(grpToRecs.Keys);
            foreach (var kvp in GroupHierarchyStore.GroupParents)
            {
                allGrpNames.Add(kvp.Key);
                allGrpNames.Add(kvp.Value);
            }
            for (int i = 0; i < KnownEmptyGroups.Count; i++)
                allGrpNames.Add(KnownEmptyGroups[i]);

            foreach (var kvp in GroupHierarchyStore.GroupParents)
            {
                List<string> children;
                if (!grpChildren.TryGetValue(kvp.Value, out children))
                {
                    children = new List<string>();
                    grpChildren[kvp.Value] = children;
                }
                if (!children.Contains(kvp.Key)) children.Add(kvp.Key);
            }
            foreach (var ch in grpChildren.Values)
                ch.Sort(StringComparer.OrdinalIgnoreCase);

            // Root groups: in allGrpNames but not a child in groupParents
            rootGrps = new List<string>();
            foreach (var g in allGrpNames)
            {
                if (!GroupHierarchyStore.HasGroupParent(g))
                    rootGrps.Add(g);
            }
            rootGrps.Sort(StringComparer.OrdinalIgnoreCase);

            // Root chains: chains where NO member has any RecordingGroups
            rootChainIds = new HashSet<string>();
            foreach (var kvp in chainToRecs)
            {
                bool anyInGrp = false;
                for (int i = 0; i < kvp.Value.Count; i++)
                {
                    var rec = committed[kvp.Value[i]];
                    if (rec.RecordingGroups != null && rec.RecordingGroups.Count > 0)
                    { anyInGrp = true; break; }
                }
                if (!anyInGrp) rootChainIds.Add(kvp.Key);
            }
        }
    }
}
