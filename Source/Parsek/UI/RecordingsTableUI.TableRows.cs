using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The column header and the row drawers: leaf rows, the name cell and tree connectors, chain / grouped recording blocks, the row hover, the centred cell buttons and the alignment debug capture.
    internal partial class RecordingsTableUI
    {
        internal void ArmAlignmentDebug()
        {
            alignmentDebugArmed = true;
            alignmentDebugHeaderCaptured = false;
            alignmentDebugRowCaptured = false;
            alignmentDebugHeaderLog.Length = 0;
            alignmentDebugRowLog.Length = 0;
            ParsekLog.Info("UI", "Rec table alignment debug armed (will capture next header + first row)");
        }

        private void AlignDebugLogLastRect(System.Text.StringBuilder sb, string tag)
        {
            if (Event.current.type != EventType.Repaint) return;
            var r = GUILayoutUtility.GetLastRect();
            sb.Append($"{tag}[x={r.x:F1},w={r.width:F1},r={r.x + r.width:F1}] ");
        }

        // Wraps a single body button in BeginHorizontal(Width=cellWidth) with a fixed
        // leading Space so the button is shifted right (not centered). The button uses
        // bodyCellButtonFlush (margin=0) and Width=cellWidth-BodyCellButtonLeftInset so
        // the inner layout exactly fills the cell. Net effect: button visible rect runs
        // from [cell + BodyCellButtonLeftInset, cell + cellWidth]; text stays centered
        // inside the button.
        private bool DrawBodyCenteredButton(GUIContent content, float cellWidth)
        {
            return DrawBodyCenteredButton(content, cellWidth, bodyCellButtonFlush);
        }

        private bool DrawBodyCenteredButton(GUIContent content, float cellWidth, GUIStyle buttonStyle)
        {
            GUILayout.BeginHorizontal(bodyCellWrapStyle, GUILayout.Width(cellWidth));
            GUILayout.Space(BodyCellButtonLeftInset);
            // Read GUI.enabled BEFORE the draw: the Watch / Forward / Rewind call sites all
            // restore it to true immediately after this returns, so reading it afterwards
            // would report every button as enabled.
            bool enabledWhenDrawn = GUI.enabled;
            bool clicked = GUILayout.Button(content, buttonStyle ?? bodyCellButtonFlush,
                GUILayout.Width(cellWidth - BodyCellButtonLeftInset));
            // Every greyed-out button routed through this helper already carries its own
            // reason in content.tooltip (ffReason / rewindReason / GetWatchButtonTooltip).
            // This is what actually gets that text to the window's help strip.
            DisabledHoverEcho.CarryLastControl(
                enabledWhenDrawn, content != null ? content.tooltip : null);
            GUILayout.EndHorizontal();
            return clicked;
        }

        private bool DrawRewindColumnButton(GUIContent content)
        {
            return DrawBodyCenteredButton(content, ColW_Rewind, bodyCellButtonCompact);
        }

        // Twin-button variant used by ghost-only recording rows (G + X) and
        // canDisband group rows (G + X). Uses the same wrap + left inset + flush
        // button margin as DrawBodyCenteredButton so the cell footprint matches
        // the single-button version exactly — otherwise twin-button rows would
        // have a different fixed-width sum from single-button rows and
        // Name.ExpandWidth would absorb the difference unevenly.
        private void DrawBodyCenteredTwoButtons(
            GUIContent firstContent, bool firstEnabled,
            GUIContent secondContent, bool secondEnabled,
            float cellWidth,
            out bool firstClicked, out bool secondClicked,
            float leftInset = BodyCellButtonLeftInset)
        {
            bool priorEnabled = GUI.enabled;
            float innerW = cellWidth - leftInset;
            float halfInner = (innerW - 4f) * 0.5f;
            GUILayout.BeginHorizontal(bodyCellWrapStyle, GUILayout.Width(cellWidth));
            if (leftInset > 0f)
                GUILayout.Space(leftInset);
            bool firstDrawnEnabled = priorEnabled && firstEnabled;
            GUI.enabled = firstDrawnEnabled;
            firstClicked = GUILayout.Button(firstContent, bodyCellButtonCompact, GUILayout.Width(halfInner));
            // Per-half carriers: the Re-Fly cell greys Fly and Seal independently and gives
            // each its own reason, so one carrier for the whole cell would attribute the
            // wrong half's reason to whichever the pointer is actually over.
            DisabledHoverEcho.CarryLastControl(
                firstDrawnEnabled, firstContent != null ? firstContent.tooltip : null);
            GUILayout.Space(4f);
            bool secondDrawnEnabled = priorEnabled && secondEnabled;
            GUI.enabled = secondDrawnEnabled;
            secondClicked = GUILayout.Button(secondContent, bodyCellButtonCompact, GUILayout.Width(halfInner));
            DisabledHoverEcho.CarryLastControl(
                secondDrawnEnabled, secondContent != null ? secondContent.tooltip : null);
            GUI.enabled = priorEnabled;
            GUILayout.EndHorizontal();
        }

        private void DrawRecordingsTableHeader(IReadOnlyList<Recording> committed)
        {
            // Header row: the house pinned-header row container (shared left inset, and the
            // scroll view's scrollbar gutter as its own right padding - see
            // ParsekUI.VerticalScrollbarGutterWidth), so the header ends where the body rows
            // below the forced vertical scrollbar do.
            GUILayout.BeginHorizontal(parentUI.GetTableHeaderRowStyle());

            // Select-all enable toggle + "#" sortable header live in ONE boxed cell
            // so the column reads as a single unit (toggle above column 0's toggles,
            // "#" above column 1's row indices) — each inner widget uses the same
            // individual cell width as its row counterparts so things line up.
            int enableCount = 0;
            for (int i = 0; i < committed.Count; i++)
                if (committed[i].PlaybackEnabled) enableCount++;
            bool allEnabled = enableCount == committed.Count;

            // Width = ColW_Enable + ColW_Index + 8 to absorb the body pair's 8 px of margin
            // budget (Toggle.L=4 + gap(4,4)=4 + Label.R=4 collapsed with Name.L=4 = 4, total
            // 4+20+4+30+4 = 62; container L=0/R=0 + W=58 + next.L=4 collapse = 62 matches).
            // Without the +8, the merged container is 8 px narrower than the body pair, so
            // every column right of # drifts 8 px relative to its header cell.
            GUILayout.BeginHorizontal(colHdrCellContainerStyle,
                GUILayout.Width(HeaderMergedEnableIndexWidth),
                GUILayout.Height(ColHeaderHeight));
            bool newAllEnabled = GUILayout.Toggle(allEnabled,
                new GUIContent("",
                    "Turn ghost playback on or off for every recording at once - including ones "
                    + "hidden by the current filters."),
                GUILayout.Width(ColW_Enable));
            if (newAllEnabled != allEnabled)
            {
                for (int i = 0; i < committed.Count; i++)
                    RecordingStore.SetRecordingPlaybackEnabled(committed[i], newAllEnabled);
                ParsekLog.Info("UI", $"Set playback enabled for all recordings: enabled={newAllEnabled}");
            }
            // "#" sortable — inline (no outer box because the parent BeginHorizontal
            // already supplies it; otherwise we'd double-box).
            string hashArrow = (sortColumn == SortColumn.Index)
                ? (sortAscending ? " \u25b2" : " \u25bc") : "";
            if (GUILayout.Button(
                    new GUIContent("#" + hashArrow,
                        "Sort by recording number - the order the recordings were captured in. Click again to reverse."),
                    boldHeaderInnerLabel, GUILayout.Width(ColW_Index)))
            {
                if (sortColumn == SortColumn.Index) sortAscending = !sortAscending;
                else { sortColumn = SortColumn.Index; sortAscending = true; }
                InvalidateSort();
                ParsekLog.Verbose("UI", $"Sort column changed: {sortColumn} {(sortAscending ? "asc" : "desc")}");
            }
            GUILayout.EndHorizontal();
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrMerged");

            DrawSortableHeader("Name", SortColumn.Name, 0, true,
                "Sort the list by vessel name. Click again to reverse the order.");
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrName");
            DrawSortableHeader("Phase", SortColumn.Phase, ColW_Phase, false,
                "Sort by flight phase - whether the flight ends in atmosphere, in space, on approach, or on a surface.");
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrPhase");
            DrawSortableHeader("Site", SortColumn.LaunchSite, ColW_Site, false,
                "Sort by launch site - where each flight started from.");
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrSite");
            DrawSortableHeader("Launch", SortColumn.LaunchTime, ColW_Launch, false,
                "Sort by launch date - when each flight started.");
            DrawSortableHeader("Duration", SortColumn.Duration, ColW_Dur, false,
                "Sort by how long each flight lasted.");
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrDur");

            var colHdr = parentUI.GetColumnHeaderStyle();

            DrawSortableHeader("Status", SortColumn.Status, ColW_Status, false,
                "Sort by status - counting down to launch, flying now, or finished.");
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrStatus");

            // Group column header
            GUILayout.Label(new GUIContent("Group",
                "Folders each recording belongs to. Use the G button on a row to change them."),
                colHdr, GUILayout.Width(ColW_Group), GUILayout.Height(ColHeaderHeight));
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrGroup");

            // Select-all loop header + checkbox. Non-loopable recordings (debris
            // and pure orbital coasts that are not player-viewable) are excluded
            // from both the aggregate and the bulk write; see ComputeLoopAggregate,
            // BulkSetLoopPlayback, and Recording.IsLoopableRecording.
            //
            // Mutual exclusion (design §0.6): the select-all loop greys OFF when ANY
            // committed recording lives on a route-bound tree (safe default: a bulk
            // control spanning a route-bound recording never partially applies). The
            // commit guard below blocks the bulk write even if the wrap is dropped.
            bool headerRouteBound = AnyRecordingRouteBound(committed, indices: null);
            var headerLoopAgg = ComputeLoopAggregate(committed);
            // Label and toggle carry the SAME text: the header cell reads as one control,
            // and the pointer lands on either half depending on where it enters.
            // Scope wording matches BulkSetLoopPlayback(indices: null): every LOOPABLE committed
            // recording, filtered-out rows included - not just what the table is showing.
            const string loopHeaderTooltip =
                "Replay every loopable recording on a repeating schedule, including ones hidden by the current filters. Greyed out when any recording is driven by a supply route.";
            GUILayout.BeginHorizontal(colHdrCellContainerStyle, GUILayout.Width(ColW_Loop), GUILayout.Height(ColHeaderHeight));
            GUILayout.FlexibleSpace();
            GUILayout.Label(new GUIContent("Loop", loopHeaderTooltip), boldHeaderInnerLabel);
            bool prevAllLoopEnabled = GUI.enabled;
            if (headerRouteBound) GUI.enabled = false;
            bool headerLoopDrawnEnabled = GUI.enabled;
            bool newAllLoop = GUILayout.Toggle(headerLoopAgg.AllLoop,
                new GUIContent("", loopHeaderTooltip));
            // The header's own tooltip is the generic what-it-does sentence, so the
            // carrier gets the short aggregate reason instead of echoing 180 characters.
            DisabledHoverEcho.CarryLastControl(
                headerLoopDrawnEnabled, LoopToggleDisabledReason(null));
            GUI.enabled = prevAllLoopEnabled;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrLoop");
            if (newAllLoop != headerLoopAgg.AllLoop)
            {
                if (headerRouteBound)
                {
                    ParsekLog.Info("RouteGuard",
                        $"Recordings-tab select-all Loop blocked (a committed recording is on a route-bound tree); request={newAllLoop} ignored");
                }
                else
                {
                    int written = BulkSetLoopPlayback(committed, indices: null, value: newAllLoop, applyAutoRange: false);
                    ParsekLog.Info("UI", $"Set loop playback for all recordings: enabled={newAllLoop} ({written} recordings)");
                }
            }

            GUILayout.Label(new GUIContent("Period",
                "Launch-to-launch period: how often the ghost relaunches. Shorter than the flight means launches overlap. Click the unit to cycle sec / min / hr / auto; auto follows Settings > Looping."),
                colHdr, GUILayout.Width(ColW_Period), GUILayout.Height(ColHeaderHeight));
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrPeriod");

            if (parentUI.InFlightMode)
            {
                GUILayout.Label(new GUIContent("Watch",
                    "Follow a ghost with the camera. Only available in flight, for ghosts on the same body and within 300 km."),
                    colHdr, GUILayout.Width(ColW_Watch), GUILayout.Height(ColHeaderHeight));
                if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrWatch");
            }
            GUILayout.Label(new GUIContent("Rewind",
                "Jump the game clock back to a flight's launch, or forward to a launch that has not happened yet."),
                colHdr, GUILayout.Width(ColW_Rewind), GUILayout.Height(ColHeaderHeight));
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrRewind");
            GUILayout.Label(new GUIContent("Re-Fly",
                "Take control of a flight that ended badly and fly it again from the moment it separated."),
                colHdr, GUILayout.Width(ColW_ReFly), GUILayout.Height(ColHeaderHeight));
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrReFly");

            // Hide column header + toggle. Both halves carry the same text, which says
            // FILTER: this toggle sits where every other header toggle is a select-all,
            // and it is the one that writes nothing to any recording.
            const string archiveHeaderTooltip =
                "Filter: hide archived recordings from this list. It archives nothing - use each row's Archive box for that.";
            GUILayout.BeginHorizontal(colHdrCellContainerStyle, GUILayout.Width(ColW_Hide), GUILayout.Height(ColHeaderHeight));
            GUILayout.FlexibleSpace();
            GUILayout.Label(new GUIContent("Archive", archiveHeaderTooltip), boldHeaderInnerLabel);
            bool newHideActive = GUILayout.Toggle(GroupHierarchyStore.HideActive,
                new GUIContent("", archiveHeaderTooltip));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured) AlignDebugLogLastRect(alignmentDebugHeaderLog, "hdrArchive");
            if (newHideActive != GroupHierarchyStore.HideActive)
            {
                GroupHierarchyStore.HideActive = newHideActive;
                ParsekLog.Info("UI", $"Hide active toggled: {GroupHierarchyStore.HideActive}");
            }

            // No trailing gutter Space: the header row style above already reserves the
            // scrollbar gutter as its right padding, and its last cell (the Archive container,
            // margin R0) ends exactly there. A Space here as well would reserve it twice.
            GUILayout.EndHorizontal();

            if (alignmentDebugArmed && !alignmentDebugHeaderCaptured && Event.current.type == EventType.Repaint)
            {
                alignmentDebugHeaderCaptured = true;
                ParsekLog.Info("UI", "AlignDbg HEADER: " + alignmentDebugHeaderLog.ToString());
            }
        }

        /// <summary>
        /// Draws a single recording row. Returns true if the list was modified (break iteration).
        /// </summary>
        private bool DrawRecordingRow(int ri, IReadOnlyList<Recording> committed, double now, float indentPx,
            IReadOnlyList<RecordingSupersedeRelation> supersedes = null, string treeConnector = null)
        {
            var rec = committed[ri];
            if (IsInactiveForDisplay(
                    rec,
                    supersedes ?? CurrentRecordingSupersedesForDisplay(),
                    CurrentRecordingRewindRetirementsForDisplay())) return false;
            if (rec.Hidden && GroupHierarchyStore.HideActive) return false;

            RecordingVisualKind visualKind = RecordingVisualClassifier.Classify(rec, committed);

            // Cross-link: detect target row during draw pass
            if (!string.IsNullOrEmpty(pendingScrollToRecordingId) &&
                rec.RecordingId == pendingScrollToRecordingId)
            {
                pendingScrollRowIndex = renderedRowCounter;
                ParsekLog.Verbose("UI",
                    $"Cross-link: found \"{rec.VesselName}\" at rendered row {renderedRowCounter}");
                pendingScrollToRecordingId = null;
            }
            renderedRowCounter++;

            GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());
            bool captureThisRow = alignmentDebugArmed && !alignmentDebugRowCaptured;

            // Enable checkbox (always at column 0)
            bool enabled = GUILayout.Toggle(rec.PlaybackEnabled,
                new GUIContent("",
                    "Play this recording back as a ghost. Unticked, the flight stays recorded but no ghost appears anywhere - world, map or Tracking Station."),
                GUILayout.Width(ColW_Enable));
            if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowToggle");
            if (enabled != rec.PlaybackEnabled)
            {
                // GUI-P1: the sole writer, shared with the select-all / group / chain-block
                // toggles below so all four hide and restore identically.
                RecordingStore.SetRecordingPlaybackEnabled(rec, enabled);
                ParsekLog.Info("UI", $"Recording '{rec.VesselName}' playback {(enabled ? "enabled" : "disabled")}" +
                    (!string.IsNullOrEmpty(rec.SegmentPhase) ? $" (segment: {RecordingStore.GetSegmentPhaseLabel(rec)})" : ""));
            }

            // #
            GUILayout.Label((ri + 1).ToString(), indexRowStyle, GUILayout.Width(ColW_Index));
            if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowIdx");

            // Lead-gap before Name column: shifts body text right to sit under the header
            // "Name" label (which is inset by box padding inside its colHdr cell). Absorbed
            // into Name.ExpandWidth so cells right of Name stay aligned with the header.
            GUILayout.Space(NameColumnLeadGap);

            // Name (double-click to rename, deferred to next frame)
            DrawRecordingNameCell(ri, rec, committed, indentPx, treeConnector);
            if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowName");

            // Phase label
            string phaseLabel = RecordingStore.GetSegmentPhaseLabel(rec);
            if (!string.IsNullOrEmpty(phaseLabel))
            {
                GUIStyle phaseStyle;
                string phaseStyleKey = GetPhaseStyleKey(rec);
                if (phaseStyleKey == "atmo") phaseStyle = phaseStyleAtmo;
                else if (phaseStyleKey == "surface") phaseStyle = phaseStyleSurface;
                else if (phaseStyleKey == "approach") phaseStyle = phaseStyleApproach;
                else if (phaseStyleKey == "space") phaseStyle = phaseStyleSpace;
                else phaseStyle = phaseStyleExo;
                GUILayout.Label(phaseLabel, phaseStyle, GUILayout.Width(ColW_Phase));
            }
            else
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Phase));
            }
            if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowPhase");

            GUILayout.Label(rec.LaunchSiteName ?? "", bodyCellLabel, GUILayout.Width(ColW_Site));
            if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowSite");

            // Launch Time
            string launchTime = rec.Points.Count > 0
                ? KSPUtil.PrintDateCompact(rec.StartUT, true)
                : "-";
            GUILayout.Label(launchTime, bodyCellLabel, GUILayout.Width(ColW_Launch));

            // Duration
            double dur = rec.EndUT - rec.StartUT;
            GUILayout.Label(FormatDuration(dur), bodyCellLabel, GUILayout.Width(ColW_Dur));
            if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowDur");

            // Status (#98: merged countdown into status column). Debris shows its ending word
            // too (Destroyed, Landed, ...), like every other row.
            int statusOrder;
            bool statusTextIsTerminalState;
            string statusText = ResolveRecordingStatusText(
                rec, now, out statusOrder, out statusTextIsTerminalState);
            GUIStyle statusStyle = statusOrder == 0 ? statusStyleFuture
                : statusOrder == 1 ? statusStyleActive
                : statusStylePast;

            string visualStatusText = FormatRecordingVisualStatusText(
                visualKind, statusText, statusTextIsTerminalState);
            if (visualStatusText != statusText)
            {
                statusText = visualStatusText;
                if (!statusTextIsTerminalState)
                    statusStyle = GetRecordingVisualStatusStyle(visualKind);
            }

            // Phase 6d-3: Chain status tooltip — show ghost chain info on hover
            string chainStatusTooltip = "";
            var flight = parentUI.Flight;
            if (parentUI.InFlightMode && flight != null)
            {
                string chainStatus = ParsekFlight.GetChainStatusForRecording(
                    flight.ActiveGhostChains, rec);
                if (chainStatus != null)
                    chainStatusTooltip = chainStatus;
            }
            // Where the flight came from (an EVA's vessel), where it ended up, and how high
            // and fast it went lead the hover: they were the old Start / End / MaxAlt /
            // MaxSpd columns, which are gone. The stats are cached per recording.
            string parentVesselName = ResolveParentVesselName(rec, committed);
            string statusPlaceTooltip = BuildStatusPlaceTooltip(
                FormatStartPosition(rec, parentVesselName),
                FormatEndPosition(rec, parentVesselName),
                statusText);
            var stats = GetOrComputeStats(rec);
            string statusStatsTooltip = BuildStatusStatsTooltip(
                stats.maxAltitude, stats.maxSpeed, stats.pointCount);
            string statusTooltip = CombineTooltipText(
                CombineTooltipText(
                    CombineTooltipText(statusPlaceTooltip, statusStatsTooltip),
                    GetOrComputeManifestSummary(rec)),
                CombineTooltipText(
                    CombineTooltipText(
                        GetRecordingVisualStatusTooltip(visualKind),
                        chainStatusTooltip),
                    ResolveDockPartnerTooltip(rec)));
            // Fall back to the column's own explanation when this row composed nothing
            // specific, so the cell is never a silent hover.
            if (string.IsNullOrEmpty(statusTooltip))
                statusTooltip = "How this flight is doing: counting down to launch, flying now, or how it ended.";
            var statusContent = new GUIContent(statusText, statusTooltip);
            GUILayout.Label(statusContent, statusStyle, GUILayout.Width(ColW_Status));
            if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowStatus");

            // Group assignment button. No row carries a delete: recordings are never
            // player-deletable (a deletion breaks the timeline and the ledger); the per-row
            // Archive checkbox is the only way to stop seeing one.
            if (DrawBodyCenteredButton(
                    new GUIContent("G", "Choose which folders this recording belongs to."),
                    ColW_Group))
            {
                var mousePos = ParsekUiScale.GuiPointToScaledRoot(Event.current.mousePosition);
                groupPicker.OpenForRecording(ri, mousePos);
                ParsekLog.Verbose("UI", $"Group popup opened for recording index={ri} name='{rec.VesselName}'");
            }
            if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowGroup");

            // Loop checkbox + Period cell are both gated by ShouldSuppressRowLoopUi
            // (Unfinished Flights virtual group, or a non-loopable recording: debris
            // or a pure orbital coast that is not player-viewable). Render width-stable
            // placeholders when suppressed so the grid stays aligned.
            bool suppressRowLoop = ShouldSuppressRowLoopUi(rec, unfinishedFlightRowDepth > 0);
            if (suppressRowLoop)
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Loop));
            }
            else
            {
                // Mutual exclusion (design §0.6): grey the per-recording Loop toggle OFF when
                // this recording's tree is bound to a supply route, and block the commit (this
                // toggle writes Recording.LoopPlayback directly, bypassing MissionStore, so the
                // commit guard is the ONLY thing preventing a per-recording loop on a route tree).
                bool rowRouteBound = IsRecordingRouteBound(rec);
                GUILayout.BeginHorizontal(GUILayout.Width(ColW_Loop));
                GUILayout.FlexibleSpace();
                bool prevRowLoopEnabled = GUI.enabled;
                if (rowRouteBound) GUI.enabled = false;
                bool rowLoopDrawnEnabled = GUI.enabled;
                bool loop = GUILayout.Toggle(
                    rec.LoopPlayback,
                    rowRouteBound
                        ? new GUIContent("", $"Looped by route: {RouteBindingTooltipName(rec)}")
                        : new GUIContent("",
                            "Replay this flight on a repeating schedule. Set how often in the Period column."));
                // Named reason: this row knows exactly which route took its loop over.
                DisabledHoverEcho.CarryLastControl(
                    rowLoopDrawnEnabled, LoopToggleDisabledReason(RouteBindingTooltipName(rec)));
                GUI.enabled = prevRowLoopEnabled;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                if (loop != rec.LoopPlayback)
                {
                    if (rowRouteBound)
                    {
                        ParsekLog.Info("RouteGuard",
                            $"Recordings-tab per-recording Loop blocked for '{rec.VesselName}' " +
                            $"(tree={rec.TreeId} bound by route); request={loop} ignored");
                    }
                    else
                    {
                        rec.LoopPlayback = loop;
                        ApplyAutoLoopRange(rec, loop);
                        if (!loop && loopPeriodFocusedRi == ri)
                            loopPeriodFocusedRi = -1;
                        ParsekLog.Info("UI", $"Recording '{rec.VesselName}' loop playback set to {loop}");
                    }
                }
            }
            if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowLoop");

            // Period: wrapped with Space(BodyCellButtonLeftInset) on the left so
            // val+unit start 10 px into the cell, matching the shifted-right
            // treatment applied to single-button body cells (DrawBodyCenteredButton).
            if (suppressRowLoop)
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Period));
            }
            else if (!rec.LoopPlayback)
            {
                // Loop off: the period means nothing yet, so the cell draws blank rather
                // than a greyed value + unit pair. It still explains itself on hover (the
                // cell is never a silent hover), with the same reason the greyed pair
                // carried.
                GUILayout.Label(
                    new GUIContent("", LoopPeriodBlankCellTooltip(rec)),
                    bodyCellLabel, GUILayout.Width(ColW_Period));
            }
            else
            {
                GUILayout.BeginHorizontal(bodyCellWrapStyle, GUILayout.Width(ColW_Period));
                GUILayout.Space(BodyCellButtonLeftInset);
                DrawLoopPeriodCell(rec, ri);
                GUILayout.EndHorizontal();
            }
            if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowPeriod");

            // Watch button (flight only)
            if (parentUI.InFlightMode)
            {
                bool hasGhost = flight.HasActiveGhost(ri);
                bool sameBody = flight.IsGhostOnSameBody(ri);
                bool inRange = flight.IsGhostWithinVisualRange(ri);
                bool bodyReadingCurrent = flight.IsGhostBodyReadingCurrent(ri);
                bool isWatching = flight.WatchedRecordingIndex == ri;
                bool canWatch = IsWatchButtonEnabled(hasGhost, sameBody, inRange, rec.IsDebris);

                // Bug #279: log enabled/disabled transitions at INFO level so future
                // playtests can distinguish "user didn't try" from "UI was broken".
                // Mirrors the existing FF/R transition pattern, but at Info because
                // the Watch button is the headline user-facing affordance and the
                // 2026-04-09 playtest report had no usable signal in the verbose-only
                // logs covering the impacted window. Keyed by RecordingId (not row
                // index) so a rewound/truncated recording reusing an old row index
                // doesn't inherit the previous occupant's cached canWatch and emit
                // a spurious transition log line.
                string watchKey = rec.RecordingId;
                if (UpdateWatchButtonTransitionCache(lastCanWatchByRecId, watchKey, canWatch))
                {
                    string reason = GetWatchButtonReason(canWatch, hasGhost, sameBody, inRange, rec.IsDebris, bodyReadingCurrent);
                    ParsekLog.Info("UI",
                        $"Watch button #{ri} \"{rec.VesselName}\" {reason} " +
                        $"(hasGhost={hasGhost} sameBody={sameBody} bodyReadingCurrent={bodyReadingCurrent} inRange={inRange} debris={rec.IsDebris}) " +
                        $"{BuildWatchObservabilitySuffix(flight, ri)}");
                }

                GUI.enabled = ShouldEnableWatchButton(canWatch, isWatching);
                string watchLabel = isWatching ? "W*" : "W";
                string watchTooltip = GetWatchButtonTooltip(isWatching, hasGhost, sameBody, inRange, rec.IsDebris, bodyReadingCurrent);
                var watchContent = new GUIContent(watchLabel, watchTooltip);
                if (DrawBodyCenteredButton(watchContent, ColW_Watch))
                {
                    string beforeFocus = flight.DescribeWatchFocusForLogs();
                    string beforeEligibility = flight.DescribeWatchEligibilityForLogs(ri);
                    if (isWatching)
                        flight.ExitWatchMode();
                    else
                        flight.EnterWatchMode(ri);
                    ParsekLog.Info("UI",
                        $"Recording #{ri} W button clicked: {(isWatching ? "exit" : "enter")} watch on \"{rec.VesselName}\" " +
                        $"before={beforeEligibility} beforeFocus={beforeFocus} afterFocus={flight.DescribeWatchFocusForLogs()}");
                }
                GUI.enabled = true;
                if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowWatch");
            }

            DrawLegacyRewindForwardCell(rec, ri, now, flight);
            if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowRewind");

            DrawReFlyColumnCell(rec, ri, now);
            if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowReFly");

            // Hide checkbox
            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Hide));
            GUILayout.FlexibleSpace();
            bool hidden = GUILayout.Toggle(rec.Hidden,
                new GUIContent("",
                    "Archive this recording so it drops out of the list while the Archive filter is on. Nothing is deleted."));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (captureThisRow) AlignDebugLogLastRect(alignmentDebugRowLog, "rowArchive");
            if (hidden != rec.Hidden)
            {
                // Phase 14 of Rewind-to-Staging (design §7.33 / §7.30): an
                // Unfinished Flight is a diagnostic of an unresolved split
                // sibling. Hiding would sweep the re-fly opportunity out of
                // the player's view, so we refuse the toggle + toast a clear
                // warning. The recording's Hidden flag stays unchanged.
                // The gate is the classifier alone (no depth check) so normal-
                // list RP-backed rows refuse hide just like the virtual group
                // rows. IsUnfinishedFlight is cheap (single RewindPoints scan)
                // so the accept-side branch tolerates this per-row call.
                //
                // The direction is now part of the gate (finding P17, mirror
                // half): only HIDING is refused. Refusing the un-hide too - which
                // the undirected check did - would keep a flight that is ALREADY
                // buried buried, the exact opposite of "rewind access must remain
                // visible", and it is the only way back for a row an older build's
                // ungated group hide-all wrote Hidden over.
                if (IsArchiveRefusedForUnfinishedFlight(
                        hidden, EffectiveState.IsUnfinishedFlight(rec)))
                {
                    ParsekLog.Warn("UnfinishedFlights",
                        $"Hide refused for Unfinished Flight rec={rec.RecordingId ?? "<no-id>"} " +
                        $"vessel='{rec.VesselName}': rewind access must remain visible (design §7.33)");
                    ParsekLog.ScreenMessage(
                        $"Cannot hide '{rec.VesselName}' — it is an Unfinished Flight. " +
                        "Re-fly the rewind point or merge as Immutable to clear it from the list.",
                        4f);
                }
                else
                {
                    rec.Hidden = hidden;
                    NotifyTimelineOfArchiveChange();
                    ParsekLog.Info("UI", $"Recording '{rec.VesselName}' hidden={hidden}");
                }
            }

            GUILayout.EndHorizontal();

            if (captureThisRow && Event.current.type == EventType.Repaint)
            {
                alignmentDebugRowCaptured = true;
                ParsekLog.Info("UI", "AlignDbg ROW: " + alignmentDebugRowLog.ToString());
                if (alignmentDebugHeaderCaptured) alignmentDebugArmed = false;
            }

            return false;
        }

        /// <summary>
        /// Draws the Name column cell for a recording row, handling indent,
        /// inline rename text field, double-click-to-rename, and auto-focus.
        /// </summary>
        private void DrawRecordingNameCell(int ri, Recording rec,
            IReadOnlyList<Recording> committed, float indentPx, string treeConnector = null)
        {
            // Uniform 2px nudge so rows (which have a "#" number in col 1) line up
            // their Name text with the group/chain header Name text above. Group/
            // chain headers render the arrow at the cell's raw origin; rows'
            // Name button gets shifted right by this 2px to match visually.
            GUILayout.Space(2f);
            // Indent inside Name column for grouped/chained subitems.
            if (indentPx > 0f) GUILayout.Space(indentPx);
            string name = string.IsNullOrEmpty(rec.VesselName) ? "Untitled" : rec.VesselName;
            if (renamingRecordingId != null && renamingRecordingId == rec.RecordingId)
            {
                bool submitRec = Event.current.type == EventType.KeyDown &&
                    (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
                bool cancelRec = Event.current.type == EventType.KeyDown &&
                    Event.current.keyCode == KeyCode.Escape;

                GUI.SetNextControlName("RecRename");
                renamingRecordingText = GUILayout.TextField(renamingRecordingText, GUILayout.ExpandWidth(true));
                activeRenameRect = GUILayoutUtility.GetLastRect();

                // Auto-focus once on first frame
                if (!renamingRecordingFocused)
                {
                    GUI.FocusControl("RecRename");
                    renamingRecordingFocused = true;
                }

                if (submitRec)
                {
                    CommitRecordingRename(committed);
                    Event.current.Use();
                }
                else if (cancelRec)
                {
                    renamingRecordingId = null;
                    activeRenameRect = default;
                    Event.current.Use();
                }
            }
            else
            {
                // Tree connector prefix (├─ / └─) for rows rendered as a subentry
                // under an expanded group/chain caret. Mirrors the Kerbals window
                // roster-tree style. Top-level rows pass a null connector.
                string nameLabel = (treeConnector ?? "") + name;
                if (GUILayout.Button(
                        new GUIContent(nameLabel,
                            "Click to select; double-click to rename this recording."),
                        GUI.skin.label, GUILayout.ExpandWidth(true)))
                {
                    float now2 = Time.realtimeSinceStartup;
                    if (lastClickedRecordingId != null && lastClickedRecordingId == rec.RecordingId
                        && now2 - lastClickTime < DoubleClickThreshold)
                    {
                        // Commit any active rename first
                        if (renamingRecordingId != null)
                            CommitRecordingRename(committed);
                        if (renamingGroup != null)
                            CommitGroupRename(renamingGroup);
                        renamingRecordingId = rec.RecordingId;
                        renamingRecordingText = rec.VesselName ?? "";
                        renamingRecordingFocused = false;
                        lastClickedRecordingId = null;
                    }
                    else
                    {
                        lastClickedRecordingId = rec.RecordingId;
                        lastClickTime = now2;
                    }
                }
            }
        }

        /// <summary>
        /// Tree-branch glyph prefix for a subentry rendered under an expanded
        /// group/chain caret. <paramref name="isLast"/> picks the corner glyph;
        /// every other sibling gets the tee glyph. Mirrors the Kerbals roster
        /// window style (see KerbalsWindowUI.FormatRosterChainMemberText).
        /// </summary>
        internal static string TreeConnector(bool isLast)
        {
            return isLast ? TreeConnectorLast : TreeConnectorMid;
        }

        internal static float ConnectorWidth()
        {
            if (cachedConnectorWidth > 0f) return cachedConnectorWidth;
            if (GUI.skin == null) return 15f; // skin only valid inside OnGUI
            GUIStyle style = GUI.skin.label;
            float w = style.CalcSize(new GUIContent(TreeConnectorMid)).x
                    - style.CalcSize(GUIContent.none).x;
            if (w <= 0f) return 15f;
            cachedConnectorWidth = w;
            return w;
        }

        // Blank indent before a connector item's OWN label (group / chain /
        // virtual header). A depth-D item leaves D-1 connector-width steps blank;
        // the connector itself fills the Dth step. Depth 0 (top level) has no
        // connector and no indent.
        internal static float SelfConnectorIndent(int depth)
        {
            return Math.Max(0, depth - 1) * ConnectorWidth();
        }

        // Blank indent to pass to a child drawn at parentDepth + 1 so the child's
        // connector lands under the parent's caret.
        private static float ChildConnectorIndent(int parentDepth)
        {
            return parentDepth * ConnectorWidth();
        }

        /// <summary>
        /// Which of a group's three child sections owns the corner connector.
        /// Render order is blocks then child sub-groups then the nested
        /// Unfinished Flights group, so the last section that renders anything
        /// owns the corner and everything before it draws tees. Pure, so the
        /// precedence table is unit-testable independent of IMGUI.
        /// </summary>
        internal enum TreeChildSection { None, Block, ChildGroup, Virtual }

        internal static TreeChildSection ResolveLastChildSection(
            bool hasVisibleBlock, bool hasRenderableChildGroup, bool hasRenderableVirtual)
        {
            if (hasRenderableVirtual) return TreeChildSection.Virtual;
            if (hasRenderableChildGroup) return TreeChildSection.ChildGroup;
            if (hasVisibleBlock) return TreeChildSection.Block;
            return TreeChildSection.None;
        }

        /// <summary>
        /// Draws a chain block (header + members). Returns true if the recording list was modified.
        /// </summary>
        private bool DrawChainBlock(string chainId, List<int> members, int depth,
            IReadOnlyList<Recording> committed, double now,
            IReadOnlyList<RecordingSupersedeRelation> supersedes = null,
            string treeConnector = null)
        {
            string chainName = members.Count > 0 ? committed[members[0]].VesselName : null;
            return DrawRecordingBlock(chainId, chainName, members, depth,
                committed, now, chainId, "Chain", supersedes, treeConnector);
        }

        private bool DrawGroupedRecordingBlock(string blockKey, string blockName, List<int> members, int depth,
            IReadOnlyList<Recording> committed, double now,
            IReadOnlyList<RecordingSupersedeRelation> supersedes = null,
            string treeConnector = null)
        {
            return DrawRecordingBlock(blockKey, blockName, members, depth,
                committed, now, null, "Block", supersedes, treeConnector);
        }

        private bool DrawRecordingBlock(string blockId, string blockName, List<int> members, int depth,
            IReadOnlyList<Recording> committed, double now, string chainIdForPopup, string logKind,
            IReadOnlyList<RecordingSupersedeRelation> supersedes = null,
            string treeConnector = null)
        {
            if (members == null || members.Count == 0)
                return false;

            if (GroupHierarchyStore.HideActive)
            {
                bool anyVisible = false;
                for (int m = 0; m < members.Count; m++)
                    if (!committed[members[m]].Hidden) { anyVisible = true; break; }
                if (!anyVisible) return false;
            }

            float indent = SelfConnectorIndent(depth);

            GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());

            int blockEnabledCount = 0;
            for (int m = 0; m < members.Count; m++)
                if (committed[members[m]].PlaybackEnabled) blockEnabledCount++;
            bool blockAllEnabled = members.Count > 0 && blockEnabledCount == members.Count;
            bool blockNewEnabled = GUILayout.Toggle(blockAllEnabled,
                new GUIContent("",
                    "Turn ghost playback on or off for every segment of this flight."),
                GUILayout.Width(ColW_Enable));
            if (string.IsNullOrEmpty(blockName)) blockName = logKind;
            string logId = !string.IsNullOrEmpty(chainIdForPopup) ? chainIdForPopup : blockName;
            if (blockNewEnabled != blockAllEnabled)
            {
                for (int m = 0; m < members.Count; m++)
                    RecordingStore.SetRecordingPlaybackEnabled(committed[members[m]], blockNewEnabled);
                ParsekLog.Info("UI", $"{logKind} '{logId}' playback set to {blockNewEnabled} ({members.Count} recordings)");
            }

            // Use the same empty-label spacer as DrawGroupTree (which is also what
            // subitem rows use) so the triangle's X position in this chain/block
            // header matches sibling group headers and subitems in the column below.
            GUILayout.Label("", GUILayout.Width(ColW_Index));

            // Lead-gap before Name column (see DrawRecordingRow for rationale).
            GUILayout.Space(NameColumnLeadGap);

            if (indent > 0f) GUILayout.Space(indent);

            bool expanded = expandedChains.Contains(blockId);
            string arrow = expanded ? "\u25bc" : "\u25b6";

            double blockStart = double.MaxValue, blockEnd = double.MinValue;
            for (int m = 0; m < members.Count; m++)
            {
                var mr = committed[members[m]];
                if (mr.StartUT < blockStart) blockStart = mr.StartUT;
                if (mr.EndUT > blockEnd) blockEnd = mr.EndUT;
            }

            if (GUILayout.Button(
                new GUIContent($"{treeConnector ?? ""}{arrow} {blockName} ({members.Count})",
                    "Click to expand or collapse the individual segments of this flight."),
                GUI.skin.label, GUILayout.ExpandWidth(true)))
            {
                if (expanded) expandedChains.Remove(blockId);
                else expandedChains.Add(blockId);
                ParsekLog.Verbose("UI",
                    $"{logKind} '{blockName}' {(expanded ? "collapsed" : "expanded")} ({members.Count} recordings)");
            }

            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Phase));

            string blockSite = committed[members[0]].LaunchSiteName;
            GUILayout.Label(blockSite ?? "", bodyCellLabel, GUILayout.Width(ColW_Site));

            GUILayout.Label(blockStart < double.MaxValue
                ? KSPUtil.PrintDateCompact(blockStart, true) : "-",
                bodyCellLabel, GUILayout.Width(ColW_Launch));

            GUILayout.Label(FormatDuration(blockEnd - blockStart), bodyCellLabel, GUILayout.Width(ColW_Dur));

            string blockStatusText;
            int blockStatusOrder;
            GetChainStatus(members, committed, now, out blockStatusText, out blockStatusOrder);
            GUIStyle blockStatusStyle = blockStatusOrder == 0 ? statusStyleFuture
                : blockStatusOrder == 1 ? statusStyleActive
                : statusStylePast;
            GUILayout.Label(blockStatusText, blockStatusStyle, GUILayout.Width(ColW_Status));

            // Aggregated blocks expose group assignment but no disband button.
            if (DrawBodyCenteredButton(
                    new GUIContent("G", "Choose which folders every segment of this flight belongs to."),
                    ColW_Group))
            {
                var mousePos = ParsekUiScale.GuiPointToScaledRoot(Event.current.mousePosition);
                if (!string.IsNullOrEmpty(chainIdForPopup))
                    groupPicker.OpenForChain(chainIdForPopup, mousePos);
                else
                    groupPicker.OpenForRecordings(members, mousePos);
                ParsekLog.Verbose("UI", $"Group popup opened for {logKind.ToLowerInvariant()} '{blockName}'");
            }

            // Aggregate Loop toggle. Chain/block writes call ApplyAutoLoopRange
            // (auto-narrow on enable, clear on disable), matching the per-row
            // toggle; see BulkSetLoopPlayback.
            //
            // Mutual exclusion (design §0.6): grey OFF + block the chain/block loop when
            // ANY member lives on a route-bound tree (safe default).
            bool blockRouteBound = AnyRecordingRouteBound(committed, members);
            var blockLoopAgg = ComputeLoopAggregate(committed, members);
            if (blockLoopAgg.SuppressToggle)
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Loop));
            }
            else
            {
                GUILayout.BeginHorizontal(GUILayout.Width(ColW_Loop));
                GUILayout.FlexibleSpace();
                bool prevBlockLoopEnabled = GUI.enabled;
                if (blockRouteBound) GUI.enabled = false;
                bool blockLoopDrawnEnabled = GUI.enabled;
                bool blockNewLoop = GUILayout.Toggle(
                    blockLoopAgg.AllLoop,
                    blockRouteBound
                        ? new GUIContent("", "Looped by route")
                        : new GUIContent("",
                            "Replay every segment of this flight together on a repeating schedule."));
                DisabledHoverEcho.CarryLastControl(
                    blockLoopDrawnEnabled, LoopToggleDisabledReason(null));
                GUI.enabled = prevBlockLoopEnabled;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                if (blockNewLoop != blockLoopAgg.AllLoop)
                {
                    if (blockRouteBound)
                    {
                        ParsekLog.Info("RouteGuard",
                            $"Recordings-tab {logKind} Loop blocked for '{logId}' (a member is on a route-bound tree); request={blockNewLoop} ignored");
                    }
                    else
                    {
                        int blockWritten = BulkSetLoopPlayback(committed, members, blockNewLoop, applyAutoRange: true);
                        ParsekLog.Info("UI", $"{logKind} '{logId}' loop set to {blockNewLoop} ({blockWritten} recordings)");
                    }
                }
            }
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Period));
            if (parentUI.InFlightMode) GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Watch));

            // Rewind / Forward button for chain and grouped recording blocks.
            // These rows aggregate recordings, so surface the same temporal
            // affordance as the recordings they contain.
            bool isRecording = parentUI.InFlightMode && parentUI.Flight.IsRecording;
            int forwardIdx = FindAggregateForwardRecordingIndex(members, committed, now);
            int blockRewindIdx = forwardIdx >= 0
                ? -1
                : FindAggregateLegacyRewindRecordingIndex(members, committed, now);
            // Shown once: the block's target is usually the very launch its mission folder
            // already offers, so under that folder the block draws a blank cell.
            bool blockTimeSuppressed = forwardIdx >= 0
                ? IsTimeTargetShownByEnclosingRow(forwardIdx, enclosingForwardTargetIdx)
                : IsTimeTargetShownByEnclosingRow(blockRewindIdx, enclosingRewindTargetIdx);
            LogEnclosingTimeSuppressionTransition("block:" + blockId, blockTimeSuppressed,
                $"{logKind} '{logId}'");
            if (blockTimeSuppressed)
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Rewind));
            }
            else if (forwardIdx >= 0)
            {
                var forwardRec = committed[forwardIdx];
                string ffReason;
                bool canFF = RecordingStore.CanFastForward(forwardRec, out ffReason, isRecording: isRecording);
                GUI.enabled = canFF;
                string tooltip = canFF ? "Fast-forward to this launch" : ffReason;
                if (DrawRewindColumnButton(new GUIContent(FastForwardActionLabel, tooltip)))
                {
                    ParsekLog.Info("UI", $"{logKind} '{logId}' Forward button: #{forwardIdx} \"{forwardRec.VesselName}\"");
                    ShowFastForwardConfirmation(forwardRec);
                }
                GUI.enabled = true;
            }
            else if (blockRewindIdx >= 0)
            {
                int rewindIdx = blockRewindIdx;
                var rewindRec = committed[rewindIdx];
                string rewindReason;
                bool canRewind = RecordingStore.CanRewind(rewindRec, out rewindReason, isRecording: isRecording);
                GUI.enabled = canRewind;
                string targetName = string.IsNullOrEmpty(rewindRec.VesselName)
                    ? "this launch"
                    : rewindRec.VesselName;
                string tooltip = canRewind ? $"Rewind to launch: {targetName}" : rewindReason;
                if (DrawRewindColumnButton(new GUIContent(RewindActionLabel, tooltip)))
                {
                    ParsekLog.Info("UI", $"{logKind} '{logId}' Rewind button: #{rewindIdx} \"{rewindRec.VesselName}\"");
                    ShowRewindConfirmation(rewindRec);
                }
                GUI.enabled = true;
            }
            else
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Rewind));
            }
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_ReFly));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Hide));

            GUILayout.EndHorizontal();

            if (expanded)
            {
                int savedEnclosingRewind = enclosingRewindTargetIdx;
                int savedEnclosingForward = enclosingForwardTargetIdx;
                ResolveChildEnclosingTimeTargets(
                    forwardIdx, blockRewindIdx, blockTimeSuppressed,
                    savedEnclosingForward, savedEnclosingRewind,
                    out enclosingForwardTargetIdx, out enclosingRewindTargetIdx);
                try
                {
                    int lastVisibleMember = -1;
                    for (int m = 0; m < members.Count; m++)
                        if (IsRowVisible(committed[members[m]], supersedes))
                            lastVisibleMember = m;
                    for (int m = 0; m < members.Count; m++)
                    {
                        string connector = TreeConnector(m == lastVisibleMember);
                        if (DrawRecordingRow(members[m], committed, now, ChildConnectorIndent(depth), supersedes, connector))
                            return true;
                    }
                }
                finally
                {
                    enclosingRewindTargetIdx = savedEnclosingRewind;
                    enclosingForwardTargetIdx = savedEnclosingForward;
                }
            }
            return false;
        }

        // Per-recording cache of the Status hover's used / carried clause. The six manifests
        // are replaced (never mutated in place) when a recording finalizes or is edited, so a
        // reference compare is enough to notice a change without rebuilding the string for
        // every row on every frame.
        private sealed class ManifestSummaryCacheEntry
        {
            internal object StartResources, EndResources, StartInventory, EndInventory, StartCrew, EndCrew;
            internal string Text;
        }

        private readonly Dictionary<Recording, ManifestSummaryCacheEntry> manifestSummaryCache =
            new Dictionary<Recording, ManifestSummaryCacheEntry>();

        private string GetOrComputeManifestSummary(Recording rec)
        {
            if (rec == null) return null;
            if (manifestSummaryCache.TryGetValue(rec, out var cached)
                && ReferenceEquals(cached.StartResources, rec.StartResources)
                && ReferenceEquals(cached.EndResources, rec.EndResources)
                && ReferenceEquals(cached.StartInventory, rec.StartInventory)
                && ReferenceEquals(cached.EndInventory, rec.EndInventory)
                && ReferenceEquals(cached.StartCrew, rec.StartCrew)
                && ReferenceEquals(cached.EndCrew, rec.EndCrew))
                return cached.Text;

            if (manifestSummaryCache.Count > 4096)
                manifestSummaryCache.Clear();
            string text = RecordingsTableFormatters.FormatManifestChangeSummary(
                rec.StartResources, rec.EndResources,
                rec.StartInventory, rec.EndInventory,
                rec.StartCrew, rec.EndCrew,
                ResolveResourceDisplayName, ResolvePartDisplayName);
            manifestSummaryCache[rec] = new ManifestSummaryCacheEntry
            {
                StartResources = rec.StartResources,
                EndResources = rec.EndResources,
                StartInventory = rec.StartInventory,
                EndInventory = rec.EndInventory,
                StartCrew = rec.StartCrew,
                EndCrew = rec.EndCrew,
                Text = text,
            };
            ParsekLog.VerboseRateLimited("UI", "recordings-manifest-summary",
                $"Recordings: Status hover used/carried clause built rec={rec.RecordingId ?? "<no-id>"} " +
                $"empty={(text == null ? "1" : "0")}");
            return text;
        }

        // Stock display name of a resource ("Liquid Fuel"), or null to keep the internal name.
        private static string ResolveResourceDisplayName(string resourceName)
        {
            try
            {
                var def = PartResourceLibrary.Instance?.GetDefinition(resourceName);
                string name = def?.displayName;
                // An unresolved localization tag reads worse than the internal name.
                return string.IsNullOrEmpty(name) || name.StartsWith("#", StringComparison.Ordinal)
                    ? null : name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Stock title of an inventory part ("OX-STAT Photovoltaic Panels"), or null to keep the
        // internal name. Part names are dot-form at runtime.
        private static string ResolvePartDisplayName(string partName)
        {
            try
            {
                if (string.IsNullOrEmpty(partName)) return null;
                return PartLoader.getPartInfoByName(partName.Replace('_', '.'))?.title;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private RecordingStats GetOrComputeStats(Recording rec)
        {
            if (rec.CachedStats.HasValue && rec.CachedStatsPointCount == rec.Points.Count)
                return rec.CachedStats.Value;

            Func<string, double[]> bodyLookup = name =>
            {
                var body = FlightGlobals.GetBodyByName(name);
                if (body == null) return null;
                return new double[] { body.Radius, body.gravParameter };
            };

            var stats = TrajectoryMath.ComputeStats(rec, bodyLookup);
            rec.CachedStats = stats;
            rec.CachedStatsPointCount = rec.Points.Count;
            return stats;
        }
    }
}
