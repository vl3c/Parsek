using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The window body draw: tabs, the Recordings and Missions bottom bars, the time-range filter indicator, the hover help strip and the table GUI styles.
    internal partial class RecordingsTableUI
    {
        /// <summary>A body label style on the house table cell (header-matched horizontal
        /// text inset), with this table's zero vertical padding. Must run inside a draw.</summary>
        private GUIStyle NewTableCellLabelStyle()
        {
            GUIStyle tableCell = parentUI.GetTableCellStyle();
            return new GUIStyle(tableCell)
            {
                padding = new RectOffset(tableCell.padding.left, tableCell.padding.right, 0, 0)
            };
        }

        private void EnsurePhaseStyles()
        {
            if (phaseStyleAtmo != null) return;

            // Every left-aligned body label (and the tinted phase / status labels) is built on
            // the house table cell style, whose HORIZONTAL padding is the column header's, so
            // cell text starts at its header's text x (the old hand-set L5 indent read 1px
            // right of the header's L4). Vertical padding stays 0, the row pitch this table
            // always had (the cross-link scroll steps 22 px per row).
            phaseStyleAtmo = NewTableCellLabelStyle();
            phaseStyleAtmo.normal.textColor = new Color(0.4f, 0.7f, 1f); // blue

            phaseStyleExo = NewTableCellLabelStyle();
            phaseStyleExo.normal.textColor = new Color(0.75f, 0.55f, 1f); // light purple

            phaseStyleSpace = NewTableCellLabelStyle();
            phaseStyleSpace.normal.textColor = new Color(0.2f, 1f, 0.6f); // lime green

            phaseStyleApproach = NewTableCellLabelStyle();
            phaseStyleApproach.normal.textColor = new Color(0.3f, 0.8f, 1f); // cyan

            phaseStyleSurface = NewTableCellLabelStyle();
            phaseStyleSurface.normal.textColor = new Color(1f, 0.6f, 0.2f); // orange

            // Generic body-cell label style (Site / Launch / Duration / every blank
            // placeholder cell).
            bodyCellLabel = NewTableCellLabelStyle();
            // Zero horizontal margin variant for use inside DrawBodyCenteredButton wrap.
            bodyCellButtonFlush = new GUIStyle(GUI.skin.button)
            {
                margin = new RectOffset(
                    0, 0,
                    GUI.skin.button.margin.top, GUI.skin.button.margin.bottom)
            };
            bodyCellButtonCompact = new GUIStyle(bodyCellButtonFlush)
            {
                padding = new RectOffset(
                    2, 2,
                    GUI.skin.button.padding.top, GUI.skin.button.padding.bottom)
            };
            bodyCellTextFieldFlush = new GUIStyle(GUI.skin.textField)
            {
                margin = new RectOffset(
                    0, 0,
                    GUI.skin.textField.margin.top, GUI.skin.textField.margin.bottom)
            };
            bodyCellWrapStyle = new GUIStyle
            {
                margin = new RectOffset(4, 4, 0, 0)
            };

            // Bold label for header cells that share their box with a toggle (Loop /
            // Hide) — the outer BeginHorizontal(colHdr) provides the box background,
            // but the inner label needs its own bold style (colHdr would double-box).
            boldHeaderInnerLabel = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };

            // Body-row # (index) number: MiddleCenter so 1-3 digit values line up visually
            // under the "#" header character (which is also MiddleCenter in boldHeaderInnerLabel).
            indexRowStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter
            };

            // Container style for boxed header cells wrapping a toggle. Clones the
            // shared column-header style but zeroes only LEFT/RIGHT margin so the
            // BeginHorizontal's footprint is exactly Width(X). Top/bottom margin
            // (4/4) preserved so the vertical gap between the header row and the
            // body list is unchanged.
            colHdrCellContainerStyle = new GUIStyle(parentUI.GetColumnHeaderStyle())
            {
                margin = new RectOffset(0, 0, 4, 4)
            };

            // Tab-bar button: selected tab looks pressed via onNormal/onHover background copied
            // from GUI.skin.button.active.background (matches KerbalsWindowUI).
            toggleButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            toggleButtonStyle.onNormal.background = GUI.skin.button.active.background;
            toggleButtonStyle.onHover.background = GUI.skin.button.active.background;
            toggleButtonStyle.onNormal.textColor = Color.white;
            toggleButtonStyle.onHover.textColor = Color.white;

            // One-shot diagnostic log of the runtime GUI skin margins — dictates
            // exactly how much space each cell leaks or collapses in the layout.
            var colHdrStyle = parentUI.GetColumnHeaderStyle();
            // The scrollbar terms are what the pinned table headers reserve out of
            // (GUI-TABLE-HEADERS-OFFSET-FROM-CELLS): a scroll view spends
            // fixedWidth + margin.left on the bar, not fixedWidth alone. Described
            // defensively so a skin without the style cannot throw out of a draw.
            var vScroll = GUI.skin.verticalScrollbar;
            string vScrollDesc = vScroll == null
                ? "vScroll=absent"
                : $"vScroll.fixedWidth={vScroll.fixedWidth.ToString("0.##", CultureInfo.InvariantCulture)}"
                  + $" vScroll.margin=L{vScroll.margin.left}/R{vScroll.margin.right}";
            ParsekLog.Verbose("UI",
                $"Rec table skin margins: box=L{GUI.skin.box.margin.left}/R{GUI.skin.box.margin.right}/T{GUI.skin.box.margin.top}/B{GUI.skin.box.margin.bottom} " +
                $"pad=L{GUI.skin.box.padding.left}/R{GUI.skin.box.padding.right} " +
                $"button.margin=L{GUI.skin.button.margin.left}/R{GUI.skin.button.margin.right} " +
                $"label.margin=L{GUI.skin.label.margin.left}/R{GUI.skin.label.margin.right} " +
                $"toggle.margin=L{GUI.skin.toggle.margin.left}/R{GUI.skin.toggle.margin.right} " +
                $"textField.margin=L{GUI.skin.textField.margin.left}/R{GUI.skin.textField.margin.right} " +
                $"colHdr.margin=L{colHdrStyle.margin.left}/R{colHdrStyle.margin.right}/T{colHdrStyle.margin.top}/B{colHdrStyle.margin.bottom} " +
                vScrollDesc + " " +
                $"vScroll.footprint={ParsekUI.VerticalScrollbarFootprintWidth().ToString("0.##", CultureInfo.InvariantCulture)} " +
                $"tableHeaderGutter={ParsekUI.VerticalScrollbarGutterWidth().ToString("0.##", CultureInfo.InvariantCulture)}");

            // Arm one-shot alignment capture so the next header+row draw dumps actual rects.
            ArmAlignmentDebug();
        }

        private void DrawRecordingsBottomBar(IReadOnlyList<Recording> committed)
        {
            // Bottom button bar — pinned to window bottom
            GUILayout.FlexibleSpace();

            // Help strip first, Close row last - the house ordering (see
            // DrawRecordingsWindowTooltip).
            DrawRecordingsWindowTooltip();

            GUILayout.BeginHorizontal();

            if (GUILayout.Button(
                    new GUIContent("New Group",
                        "Create an empty folder and name it. Put recordings in it with the G button on each row."),
                    GUILayout.Width(80)))
            {
                string newName = GenerateUniqueGroupName();
                KnownEmptyGroups.Add(newName);
                expandedGroups.Add(newName);
                renamingGroup = newName;
                renamingGroupText = newName;
                ParsekLog.Info("UI", $"Group '{newName}' created");
            }

            if (GUILayout.Button("Close"))
            {
                showRecordingsWindow = false;
                groupPicker.Close();
                ParsekLog.Verbose("UI", "Recordings window closed via button");
            }

            GUILayout.EndHorizontal();

            ParsekUI.DrawResizeHandle(recordingsWindowRect, ref isResizingRecordingsWindow,
                "Missions window");

            GUI.DragWindow();
        }

        // Bottom bar for the Missions tab: just a shared Close (the recordings-specific
        // Info / New Group buttons live only on the recordings tab) plus the same window
        // chrome (resize handle + drag) so the merged window behaves identically on both tabs.
        private void DrawMissionsTabBottomBar()
        {
            GUILayout.FlexibleSpace();

            // Help strip first, Close row last - the house ordering (see
            // DrawRecordingsWindowTooltip).
            DrawRecordingsWindowTooltip();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Close"))
            {
                showRecordingsWindow = false;
                groupPicker.Close();
                ParsekLog.Verbose("UI", "Recordings window closed via button (Missions tab)");
            }
            GUILayout.EndHorizontal();

            ParsekUI.DrawResizeHandle(recordingsWindowRect, ref isResizingRecordingsWindow,
                "Missions window");

            GUI.DragWindow();
        }

        // Pure tab-switch log helper (extracted so the log contract is unit-testable outside
        // IMGUI; mirrors KerbalsWindowUI.SwitchTab).
        internal static void LogTabSwitch(int oldTab, int newTab)
        {
            ParsekLog.Verbose("UI",
                $"Recordings window: tab switched {oldTab}->{newTab}");
        }

        private void DrawTimeRangeFilterIndicator()
        {
            var filter = parentUI.TimeRangeFilter;
            if (!filter.IsActive) return;

            GUILayout.BeginHorizontal();
            string label;
            if (filter.ActivePresetName != null)
            {
                label = "Filtered: " + filter.ActivePresetName;
            }
            else
            {
                label = "Filtered: "
                    + TimeRangeFilterLogic.FormatSliderLabel(filter.MinUT ?? 0)
                    + " \u2014 "
                    + TimeRangeFilterLogic.FormatSliderLabel(filter.MaxUT ?? 0);
            }
            GUILayout.Label(label, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(
                    new GUIContent("Clear",
                        "Clear the time-range filter so every recording is listed again. Also resets the Timeline window's range sliders."),
                    GUILayout.Width(50)))
            {
                filter.Clear();
                ParsekWindowSet.Of(parentUI).GetTimelineUI()?.ResetTimeRangeSliders();
                ParsekLog.Verbose("UI", "Time-range filter: cleared from Recordings table");
            }
            GUILayout.EndHorizontal();
        }

        private void DrawRecordingsWindow(int windowID)
        {
            // Breathing room below the title bar — matches Timeline's visual spacing.
            GUILayout.Space(5);

            // Horizontal-scroll decision for this frame (Layout-latched): true only when a
            // narrow screen has capped the window below its natural width.
            wideScroll.Latch(recordingsWindowRect.width);

            // Ensure body + tab-bar styles exist before the tab bar draws.
            EnsurePhaseStyles();

            // Basic / Advanced gating (design 7.1, 7.4). Read the FRAME-LATCHED mode ONCE
            // per draw pass, never the settings field: the Layout and Repaint passes of one
            // frame must agree on the control count, and the toolbar IS a control.
            UiComplexityMode complexity = ParsekUI.AppliedUiComplexityMode;

            // Defensive on-draw clamp (design 7.4). The deferred mode-apply already clamps, so
            // this is a backstop for any caller that moves `selectedTab` without going through
            // it. The tab bar itself only moves `selectedTab` while it is drawn (Advanced),
            // and `ShowMissionForRecording` writes `TabMissions`, which is Basic-valid already;
            // `ScrollToRecording` (no production caller) is the one write that can still produce
            // the Basic-invalid index. It
            // reads the latched mode, so it is deterministic within a frame - identical in
            // Layout and Repaint - and therefore layout-safe.
            ApplyTabClamp(complexity, "draw");

            // Two-tab bar (Missions | Recordings), each half the window width. The Missions
            // tab is the higher mission abstraction over the same recordings; switching tabs
            // swaps the content inside this one window (delegated to MissionsWindowUI).
            // In Basic the toolbar is not drawn AT ALL (zero tabs, design 7.4) and its
            // trailing separator goes with it, or Basic shows a stray gap where the tab bar
            // used to be.
            if (VisibleTabCount(complexity) > 0)
            {
                int newTab = GUILayout.Toolbar(selectedTab, TabLabels, toggleButtonStyle);
                if (newTab != selectedTab)
                {
                    LogTabSwitch(selectedTab, newTab);
                    selectedTab = newTab;
                }
                GUILayout.Space(3);
            }

            // With no toolbar drawn there is no way to leave Missions, so the content
            // dispatch is PINNED to it rather than trusting the clamp alone.
            int activeTab = VisibleTabCount(complexity) > 0 ? selectedTab : TabMissions;

            if (activeTab == TabMissions)
            {
                // The Missions tab sets GUIContent tooltips (include checkbox, Loop, period cell,
                // Next launch, Clone / Archive / Warp to, partner journey) but had no renderer for
                // them, so they were set and never shown. Mirror the recordings tab: clear the
                // sticky text this window uses for its own hover strings, draw the tab, then let
                // the bottom bar draw the same help strip above its Close button.
                // DrawRecordingsWindowTooltip always emits exactly one label at one fixed
                // height, so neither the control count nor the window height depends on whether
                // the cursor is over a tooltipped control.
                recordingsWindowTooltipText = string.Empty;
                ParsekWindowSet.Of(parentUI).GetMissionsUI().DrawMissionsTabContent(wideScroll);
                DrawMissionsTabBottomBar();
                return;
            }

            // ===== Recordings tab =====
            // [ERS-exempt] reason: the recordings-table window is the authoritative
            // management surface — it lists, sorts, renames, groups, and deletes
            // recordings by index into the raw committed list (including
            // NotCommitted rows when present). Sort + delete index-paths would
            // shift under ERS, breaking user actions. Visibility filters
            // (Hidden, HideActive, superseded chain blocks) continue to be
            // handled inline per-row as before.
            // TODO(phase 6+): migrate recording table to recording-id-keyed rows.
            var committed = RecordingStore.CommittedRecordings;
            var supersedes = CurrentRecordingSupersedesForDisplay();
            var retirements = CurrentRecordingRewindRetirementsForDisplay();
            double now = Planetarium.GetUniversalTime();
            recordingsWindowTooltipText = string.Empty;

            // Bug #279 follow-up: drop watch-transition cache entries whose
            // RecordingId is no longer in the committed list (rewind, truncate,
            // recording deletion). Without this, the dicts grow unbounded over
            // a long session and a removed recording's stale state could
            // theoretically be revived if a future code path resurrected the
            // ID. The pre-existing lastCanFF/lastCanRewind dicts have the same
            // unbounded-growth shape but are deferred to a separate cleanup
            // pass per the bug #279 review.
            PruneStaleWatchTransitionEntries(committed);

            HandleRecordingsDefocus(committed);

            EnsureStatusStyles();
            EnsurePhaseStyles();
            RebuildSortedIndices(committed, now);

            // Cross-link scroll: apply deferred scroll from previous frame's row detection
            if (pendingScrollRowIndex >= 0)
            {
                recordingsScrollPos.y = pendingScrollRowIndex * 22f;
                ParsekLog.Verbose("UI",
                    $"Cross-link: applied deferred scroll to rendered row {pendingScrollRowIndex}");
                pendingScrollRowIndex = -1;
            }

            // Time-range filter indicator
            DrawTimeRangeFilterIndicator();

            if (committed.Count == 0)
            {
                GUILayout.Label("No recordings yet.", parentUI.GetEmptyStateStyle());
            }
            else
            {
                // Fixed header row (outside the scroll view) — stays pinned to the top
                // while the body scrolls. A trailing spacer inside the header matches the
                // vertical scrollbar's reserved width so column right-edges line up with
                // the row cells exactly. On a screen narrower than the window's natural
                // width, the header and the body scroll horizontally together inside one
                // wrapper (a no-op on a screen the window fits).
                wideScroll.BeginPinnedHeaderArea(parentUI.GetOpaqueWindowStyle());
                DrawRecordingsTableHeader(committed);

                renderedRowCounter = 0;
                // Top-level rows have no enclosing row (the fields are restored in a
                // finally by every nested draw; this is the belt to that).
                enclosingRewindTargetIdx = -1;
                enclosingForwardTargetIdx = -1;
                recordingsScrollPos = GUILayout.BeginScrollView(
                    recordingsScrollPos, false, true, GUILayout.ExpandHeight(true));

                // Dark list-area background: the house table body box, which adds no
                // horizontal inset of its own, and every row below opens with the house row
                // container, so row columns align with the header above.
                GUILayout.BeginVertical(parentUI.GetTableBodyBoxStyle());

                // Rebuild if a header click invalidated during this frame
                RebuildSortedIndices(committed, now);

                // -- Build group tree data --
                Dictionary<string, List<int>> grpToRecs;
                Dictionary<string, List<int>> chainToRecs;
                Dictionary<string, List<string>> grpChildren;
                List<string> rootGrps;
                HashSet<string> rootChainIds;
                BuildGroupTreeData(committed, sortedIndices, KnownEmptyGroups,
                    out grpToRecs, out chainToRecs, out grpChildren,
                    out rootGrps, out rootChainIds, supersedes, retirements);

                // -- Build unified sorted root items --
                var rootItems = new List<RootDrawItem>();

                // Time-range filter state for skipping non-overlapping items
                var timeFilter = parentUI.TimeRangeFilter;
                double? tfMin = timeFilter.MinUT;
                double? tfMax = timeFilter.MaxUT;

                // Add root groups
                for (int g = 0; g < rootGrps.Count; g++)
                {
                    var desc = new HashSet<int>();
                    CollectDescendantRecordings(rootGrps[g], grpToRecs, grpChildren, desc);

                    // Group filter: skip if no descendant overlaps the time range
                    if (timeFilter.IsActive &&
                        !TimeRangeFilterLogic.DoesAnyRecordingOverlapRange(
                            committed, desc, timeFilter.MinUT, timeFilter.MaxUT))
                        continue;

                    rootItems.Add(new RootDrawItem
                    {
                        SortKey = GetGroupSortKey(desc, committed, sortColumn, now),
                        SortName = rootGrps[g],
                        ItemType = RootItemType.Group,
                        GroupName = rootGrps[g],
                        RecIdx = -1
                    });
                }

                // Add root chains and standalone recordings from sortedIndices
                var seenChains = new HashSet<string>();
                for (int row = 0; row < sortedIndices.Length; row++)
                {
                    int ri = sortedIndices[row];
                    var rec = committed[ri];
                    if (IsInactiveForDisplay(rec, supersedes, retirements)) continue;
                    // Skip recordings that belong to groups (drawn inside group trees)
                    if (rec.RecordingGroups != null && rec.RecordingGroups.Count > 0) continue;

                    if (!string.IsNullOrEmpty(rec.ChainId) && rootChainIds.Contains(rec.ChainId))
                    {
                        if (!seenChains.Add(rec.ChainId)) continue;
                        var members = chainToRecs[rec.ChainId];

                        // Chain filter: show entire chain if any segment overlaps
                        if (timeFilter.IsActive &&
                            !TimeRangeFilterLogic.DoesAnyRecordingOverlapRange(
                                committed, members, tfMin, tfMax))
                            continue;

                        var firstRec = members.Count > 0 ? committed[members[0]] : null;
                        string cSortName = sortColumn == SortColumn.LaunchSite
                            ? (firstRec?.LaunchSiteName ?? "")
                            : (firstRec != null ? firstRec.VesselName : "");
                        rootItems.Add(new RootDrawItem
                        {
                            SortKey = GetChainSortKey(members, committed, sortColumn, now),
                            SortName = cSortName,
                            ItemType = RootItemType.Chain,
                            ChainId = rec.ChainId,
                            RecIdx = -1
                        });
                    }
                    else if (string.IsNullOrEmpty(rec.ChainId))
                    {
                        // Standalone recording filter
                        if (timeFilter.IsActive &&
                            !TimeRangeFilterLogic.DoesRecordingOverlapRange(
                                rec.StartUT, rec.EndUT, tfMin, tfMax))
                            continue;

                        string rSortName = sortColumn == SortColumn.LaunchSite
                            ? (rec.LaunchSiteName ?? "")
                            : (string.IsNullOrEmpty(rec.VesselName) ? "Untitled" : rec.VesselName);
                        rootItems.Add(new RootDrawItem
                        {
                            SortKey = GetRecordingSortKey(rec, sortColumn, now, row),
                            SortName = rSortName,
                            ItemType = RootItemType.Recording,
                            RecIdx = ri
                        });
                    }
                }

                // Sort root items
                var col = sortColumn;
                var asc = sortAscending;
                rootItems.Sort((a, b) =>
                    CompareRootItemsForSort(
                        a.ItemType == RootItemType.Group, a.GroupName, a.SortName, a.SortKey,
                        b.ItemType == RootItemType.Group, b.GroupName, b.SortName, b.SortKey,
                        col, asc));

                // Unfinished Flights is a virtual group of recordings whose
                // parent split left a re-flyable crashed, stranded, or stable
                // unconcluded sibling. It used to render at root level, but that made the
                // row float detached from the mission it belongs to. Now it is
                // rendered NESTED under each owning tree's auto-generated root
                // group (see DrawGroupTree → DrawVirtualUnfinishedFlightsGroup
                // call path below). The rootItems loop no longer inserts a
                // top-level entry for the virtual group. We still log the
                // render so per-frame membership remains auditable.
                var unfinishedMembers = UnfinishedFlightsGroup.ComputeMembers();
                if (unfinishedMembers != null && unfinishedMembers.Count > 0)
                {
                    ParsekLog.VerboseRateLimited("UnfinishedFlights",
                        "unfinishedflights-render",
                        $"render: nested virtual group enabled members={unfinishedMembers.Count}");
                }

                // -- Draw tree --
                bool deleted = false;

                for (int i = 0; i < rootItems.Count && !deleted; i++)
                {
                    var item = rootItems[i];
                    switch (item.ItemType)
                    {
                        case RootItemType.Group:
                            deleted = DrawGroupTree(item.GroupName, 0, committed, now,
                                grpToRecs, chainToRecs, grpChildren, supersedes);
                            break;
                        case RootItemType.Chain:
                            deleted = DrawChainBlock(item.ChainId,
                                chainToRecs[item.ChainId], 0, committed, now, supersedes);
                            break;
                        case RootItemType.Recording:
                            deleted = DrawRecordingRow(item.RecIdx, committed, now, 0f, supersedes);
                            break;
                        case RootItemType.VirtualGroup:
                            deleted = DrawVirtualUnfinishedFlightsGroup(committed, now, supersedes);
                            break;
                    }
                }

                GUILayout.EndVertical();
                GUILayout.EndScrollView();

                // Capture scroll view rect for tooltip visibility guard
                if (Event.current.type == EventType.Repaint)
                    scrollViewRect = GUILayoutUtility.GetLastRect();
                wideScroll.EndPinnedHeaderArea();
            }

            DrawRecordingsBottomBar(committed);
        }

        /// <summary>
        /// Bottom "hovered control help text" strip (shared house helper). Called from
        /// the bottom bar of each tab, AFTER every row has drawn and directly above the
        /// Close button - the house ordering, and the ordering the manual side-channel
        /// needs: <see cref="recordingsWindowTooltipText"/> is cleared at the top of
        /// every pass and re-set during Repaint by whichever row resolved its own hover
        /// string (the clamped loop-period cell, via GetLastRect), so the override is
        /// already final by the time the strip reads it. Nothing below the strip carries
        /// a tooltip, so the live GUI.tooltip read misses nothing.
        /// </summary>
        private void DrawRecordingsWindowTooltip()
        {
            tooltipEcho.Draw(recordingsWindowTooltipText);
        }

        private void EnsureStatusStyles()
        {
            if (statusStyleFuture != null) return;

            // Built on the house table cell style like every other body label.
            // A flight still ahead shows the house countdown, so it takes the house
            // countdown amber; a flight in progress is the palette's active green.
            statusStyleFuture = NewTableCellLabelStyle();
            statusStyleFuture.normal.textColor = ParsekUI.CountdownTextColor;

            statusStyleActive = NewTableCellLabelStyle();
            statusStyleActive.normal.textColor = parentUI.GetStatusColor(ParsekUI.StatusColorKind.Green);

            statusStylePast = NewTableCellLabelStyle();
            statusStylePast.normal.textColor = new Color(0.5f, 0.5f, 0.5f);

            statusStyleStatic = NewTableCellLabelStyle();
            statusStyleStatic.normal.textColor = new Color(1f, 0.72f, 0.25f);

            // Stationary is the palette's informational cyan. The ended grey (0.5) and
            // the static orange above mean ended / never-moved here and have no palette
            // slot, so they stay local.
            statusStyleStationary = NewTableCellLabelStyle();
            statusStyleStationary.normal.textColor = parentUI.GetStatusColor(ParsekUI.StatusColorKind.Cyan);
        }
    }
}
