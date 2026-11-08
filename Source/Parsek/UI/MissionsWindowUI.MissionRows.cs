using System.Collections.Generic;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The mission header and value rows: name and value cells, the action line, the summary name and summary cells, the next-launch warning and the mission rename.
    internal partial class MissionsWindowUI
    {
        // Mission bar (Missions Model 1; owner mock-up 2026-09-30): a dark section-header bubble
        // holding TWO table rows, each laying out the same columns as every vessel row under it
        // (MissionsTabColumnSequenceTests holds both to the column header's sequence).
        //   Line 1: index | bold title + Log (right-aligned in the name cell) | the mission's
        //           start time, start event, outcome, end time | Interact: Watch.
        //   Line 2: the collapse caret under the index | the summary, then (Advanced) the loop
        //           group right-aligned in the name cell | four blank data cells | Interact:
        //           Rewind / Forward (a reserved slot when neither applies).
        // Clone/Delete mutate MissionStore; the draw loop iterates a snapshot so that is safe. The
        // loop toggle goes through MissionStore.SetLoopEnabled, which allows concurrent looping
        // across trees but at most one looping mission per tree (it only flips bools on same-tree
        // siblings, never adds/removes, so it is safe to call from inside the draw loop).
        private void DrawMissionHeader(Mission mission, int index, MissionThroughLineView view,
            MissionPeriodicityDisplay periodicity, MissionPresentation.MissionSummaryFacts summary,
            string startEvent, bool collapsed)
        {
            // The style's left/right margin + padding are zeroed, so both lines' cells land at the
            // same x as the vessel rows' cells; the caller's CaptureRevealAnchor measures this
            // group's rect, whose y is the block top.
            GUILayout.BeginVertical(missionHeaderRowStyle);
            DrawMissionValueRow(mission, index, view, periodicity, summary, startEvent);
            DrawMissionActionLine(mission, view, periodicity, summary, collapsed);
            GUILayout.EndVertical();
        }

        // Line 1 of the mission bar: the mission as a table row.
        private void DrawMissionValueRow(Mission mission, int index, MissionThroughLineView view,
            MissionPeriodicityDisplay periodicity, MissionPresentation.MissionSummaryFacts summary,
            string startEvent)
        {
            GUILayout.BeginHorizontal(GUILayout.MinHeight(CompositionRowMinHeight));

            // First column = blank enable slot + the index number, totalling the recordings tab's
            // [enable+index] width so the title lines up with the recordings "Name" column.
            GUILayout.Label("", missionHeaderTextStyle, GUILayout.Width(ColW_Enable));
            // Index cell: the per-tree number, non-modifiable. Shared by clones. Bold transparent
            // text on the row bubble (no box of its own). Shown in BOTH modes: T1.7 originally
            // hid it in Basic as a support ordinal, but a numberless first column read as a
            // rendering gap (owner playtest 2026-08-20), and with the # header sortable in both
            // modes the number is what the sort is BY.
            GUILayout.Label(
                index > 0
                    ? index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "",
                missionHeaderTextStyle, GUILayout.Width(ColW_Index));

            // Small left inset on the title so its text starts at the same x as the "Missions and
            // vessels" column header text (the header box insets its label; the bare title label
            // does not).
            GUILayout.Space(BodyCellTextIndent);
            DrawMissionNameCell(mission, periodicity);

            // The mission-level values, under their headings: the span's first and last dates
            // (the same pre-stamped strings the summary tooltip carries), the first vessel's
            // start event ("Launch" on an ordinary mission) and the primary vessel's outcome.
            // Non-wrapping cells, so a long event word clips rather than growing the bar.
            DrawMissionValueCell(summary.StartDateText, ColW_StartTime);
            DrawMissionValueCell(startEvent, ColW_StartEvent);
            DrawMissionValueCell(summary.TerminalWord, ColW_EndEvent);
            DrawMissionValueCell(summary.EndDateText, ColW_EndTime);

            DrawInteractWatch(mission, view);

            GUILayout.EndHorizontal();
        }

        // The name cell of line 1: the bold title (double-click renames), which expands, then (in
        // Advanced) the loop grid's first row - Clone in column A, "Warp to..." in column B - and
        // Log right-aligned at the Interact single-button width. See LoopGridColumnAWidth for
        // how the grid lines up with its second row on line 2.
        private void DrawMissionNameCell(Mission mission, MissionPeriodicityDisplay periodicity)
        {
            bool loopAuthoring = ShowsLoopAuthoringControls(ParsekUI.AppliedUiComplexityMode);
            GUILayout.BeginHorizontal(GUILayout.ExpandWidth(true));
            DrawMissionTitleOrRename(mission);

            if (loopAuthoring)
            {
                // Column A, line 1: Clone. Loop AUTHORING (a clone exists to carry a second
                // include set / loop period over the same recordings), so Advanced-only.
                if (GUILayout.Button(new GUIContent("Clone", MissionPresentation.CloneButtonTooltip),
                        loopGridButtonStyle, GUILayout.Width(LoopGridColumnAWidth)))
                    MissionStore.Clone(mission);
                GUILayout.Space(LoopGridGap);

                // Column B, line 1: "Warp to...". Jumps the game clock to this mission's next
                // relaunch (the countdown target = periodicity.NextRelaunchUT) through the
                // in-place forward jump the Forward button uses. ALWAYS drawn in Advanced
                // (owner request 2026-10-01): greyed, with its reason on hover, while the mission
                // does not loop or cannot warp yet, so the grid reads the same in every state.
                DrawMissionWarpToWindowButton(mission, periodicity);
            }

            // "Log": opens the chronological step-list window for this mission (launch,
            // staging, dock / undock, terminal).
            if (GUILayout.Button("Log", interactButtonStyle, GUILayout.Width(InteractButtonWidth)))
            {
                ParsekLog.Info("UI",
                    $"Mission Log button: tree={mission.TreeId ?? "<null>"} mission={mission.Id ?? "<null>"} " +
                    $"name='{mission.Name ?? ""}'");
                parentUI.OpenStructureWindowForMission(mission.TreeId, mission.Id, mission.Name);
            }
            GUILayout.EndHorizontal();
        }

        private void DrawMissionValueCell(string value, float width)
        {
            // The full value rides the tooltip only when the cell clips it; a tooltip that
            // repeats a fully visible word would just echo it in the help strip.
            DrawEventCell(value, null, compositionCellLabelNoWrap, width);
        }

        // Line 2 of the mission bar: the collapse caret in the # column, the summary (+ the
        // Advanced loop grid's second row) in the name cell, blank data cells, and Rewind /
        // Forward in the Interact column.
        private void DrawMissionActionLine(Mission mission, MissionThroughLineView view,
            MissionPeriodicityDisplay periodicity, MissionPresentation.MissionSummaryFacts summary,
            bool collapsed)
        {
            GUILayout.BeginHorizontal(GUILayout.MinHeight(CompositionRowMinHeight));
            // Same leading cells as line 1, so the caret sits under the index and the summary
            // starts under the title.
            GUILayout.Label("", missionHeaderInlineLabel, GUILayout.Width(ColW_Enable));
            DrawMissionCollapseCaret(mission, collapsed);
            GUILayout.Space(BodyCellTextIndent);
            DrawMissionSummaryNameCell(mission, view, periodicity, summary);
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartTime));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartEvent));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndEvent));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndTime));
            DrawInteractRewindForward(view);
            GUILayout.EndHorizontal();
        }

        // The name cell of line 2: the summary on the left (it expands and wraps), then:
        //   Basic:    [Run by route], right-aligned, when route-bound.
        //   Advanced: the loop grid's second row - Delete in column A, and the loop row
        //             spanning column B plus the Log slot above it: "Loop [x] every" under
        //             Warp and the period under Log, or "Looped by route" across the whole
        //             row when route-bound.
        private void DrawMissionSummaryNameCell(Mission mission, MissionThroughLineView view,
            MissionPeriodicityDisplay periodicity, MissionPresentation.MissionSummaryFacts summary)
        {
            bool loopAuthoring = ShowsLoopAuthoringControls(ParsekUI.AppliedUiComplexityMode);
            bool missionRouteBound = RouteTreeGuard.RouteBindingForMission(
                mission, RecordingStore.CommittedTrees, out Route bindingRoute);

            GUILayout.BeginHorizontal(GUILayout.ExpandWidth(true));
            DrawMissionSummaryCell(mission, periodicity, summary, loopAuthoring);

            if (loopAuthoring)
            {
                // Column A, line 2: Delete. It removes a clone, so it has nothing to act on
                // without one (CanDelete keeps the tree's first mission safe either way);
                // Advanced-only with Clone above it (owner ruling 2026-09-29).
                bool canDeleteMission = MissionStore.CanDelete(mission);
                GUI.enabled = canDeleteMission;
                bool deleteClicked = GUILayout.Button("Delete", loopGridButtonStyle,
                    GUILayout.Width(LoopGridColumnAWidth));
                DisabledHoverEcho.CarryLastControl(canDeleteMission, MissionDeleteDisabledReason(mission));
                if (deleteClicked)
                    MissionStore.Delete(mission);
                GUI.enabled = true;
                GUILayout.Space(LoopGridGap);

                DrawMissionLoopRow(mission, view, periodicity, missionRouteBound, bindingRoute);
            }
            else if (missionRouteBound)
            {
                // Basic keeps the route label (owner decision 2026-09-29) - it says why this
                // mission's flights repeat - right-aligned after the summary, in Basic's
                // words ("Run by route"; Basic reads no loop word).
                DrawLoopedByRouteLabel(bindingRoute, ParsekUI.AppliedUiComplexityMode);
            }

            GUILayout.EndHorizontal();
        }

        // The mission summary, narrative form (T2.1): "Kerbin -> Mun -> Kerbin . 2d 3h . Jeb,
        // Bob, Val . Landed . Next launch T- 2h 14m", the expanding left cell of the
        // mission bar's second line. It answers "what is this mission?" without expanding
        // anything: where it went, how long, who flew, how it ended, and (Advanced) the schedule.
        // The facts the narrative dropped from T1.1 (span dates, vessel count, full roster) ride
        // the tooltip, with the countdown's state explanation and warning when there is one.
        //
        // WRAPS rather than clips: it shares its line with the action buttons, and the countdown
        // it ends with is the piece a clip would cut. The wrapped height is fed from the previous
        // Repaint's width (missionSummaryCellWidth), the same fix DrawWideRowCell applies to the
        // name cells, because IMGUI measures a wrapped label's height before the horizontal group
        // resolves its width. MinWidth(0) lets the cell shrink to whatever the buttons leave.
        // Always ONE label (an empty one when there is nothing to say), so neither the loop state
        // nor the mode changes this line's control count.
        private void DrawMissionSummaryCell(Mission mission, MissionPeriodicityDisplay periodicity,
            MissionPresentation.MissionSummaryFacts summary, bool loopAuthoring)
        {
            bool hasSummary = summary.HasSpan || summary.VesselCount > 0 || summary.CrewCount > 0
                || !string.IsNullOrEmpty(summary.TerminalWord)
                || !string.IsNullOrEmpty(summary.BodyPath);

            string text = "";
            string tooltip = null;
            if (hasSummary)
            {
                // The countdown ("Next launch T- ...") is Advanced-only, with the loop controls it
                // reports on (ShowsLoopAuthoringControls): Basic reads no loop word.
                string cellText = !loopAuthoring ? "" : BuildTMinusCellText(
                    mission != null && mission.LoopPlayback,
                    periodicity.Solved,
                    periodicity.Solution.ShouldPhaseLock,
                    periodicity.UnitBuilt,
                    periodicity.Solution.P,
                    periodicity.NextRelaunchUT,
                    periodicity.NowUT,
                    periodicity.IsReaim);
                // The countdown, only when one exists (T1.2).
                string nextLaunch = MissionPresentation.SummaryNextLaunchText(cellText);

                // The span dates + detail tooltip are frame-invariant and pre-stamped on the cached
                // facts (GetSummaryFacts) - only the countdown / loop pieces are built per pass.
                string startText = summary.StartDateText ?? "";
                string endText = summary.EndDateText ?? "";
                string durationText = summary.HasSpan
                    ? ParsekTimeFormat.FormatDuration(summary.EndUT - summary.StartUT)
                    : "";

                // The warning the old Next launch cell carried as an amber tint (D3 drift, M4c
                // arrival, a launch outside its alignment tolerance): its words ride this
                // tooltip, as do the explanations of the two state words the line itself does
                // not print, and a warned countdown carries a "(!)" marker on the line so it
                // reads differently from a plain one before it is hovered.
                string warning = !loopAuthoring ? null : NextLaunchWarningText(periodicity);
                bool countdownWarned = nextLaunch != null && warning != null;
                if (MissionPresentation.RecordCountdownWarnedState(
                        lastCountdownWarnedByMission, mission != null ? mission.Id : null,
                        countdownWarned))
                {
                    ParsekLog.Verbose("Mission",
                        $"Missions summary: mission '{mission?.Name}' next-launch countdown " +
                        $"warned={countdownWarned}" +
                        (countdownWarned ? $" reason='{warning}'" : ""));
                }

                // No "Loops ~P" piece: the period cell beside the summary already shows the loop
                // period (owner decision 2026-09-30).
                text = MissionPresentation.BuildNarrativeSummaryLine(
                    summary.BodyPath, startText, endText, durationText,
                    MissionPresentation.BuildCrewNamesText(summary.CrewNames), summary.CrewCount,
                    summary.TerminalWord, nextLaunch, countdownWarned);

                string nextLaunchNote = !loopAuthoring ? null
                    : MissionPresentation.BuildNextLaunchCellTooltip(cellText, warning);
                tooltip = MissionPresentation.BuildSummaryTooltip(
                    summary.DetailTooltip ?? MissionPresentation.MissionSummaryTooltip,
                    nextLaunchNote);
            }

            var content = tooltip != null ? new GUIContent(text, tooltip) : new GUIContent(text);
            string key = mission != null ? mission.Id ?? "" : "";
            if (missionSummaryCellWidth.TryGetValue(key, out float w) && w > 1f)
            {
                float h = Mathf.Max(CompositionRowMinHeight, missionSummaryLabel.CalcHeight(content, w));
                GUILayout.Label(content, missionSummaryLabel,
                    GUILayout.ExpandWidth(true), GUILayout.MinWidth(0f), GUILayout.Height(h));
            }
            else
            {
                GUILayout.Label(content, missionSummaryLabel,
                    GUILayout.ExpandWidth(true), GUILayout.MinWidth(0f));
            }
            if (Event.current.type == EventType.Repaint)
            {
                Rect r = GUILayoutUtility.GetLastRect();
                if (r.width > 1f)
                    missionSummaryCellWidth[key] = r.width;
            }
        }

        // The warning text for the mission's next launch, or null when there is none: the amber
        // reasons the old Next launch cell tinted for, with a sentence of its own for the
        // tolerance-only case that used to be tint alone.
        private static string NextLaunchWarningText(MissionPeriodicityDisplay periodicity)
        {
            bool amber = ShouldTintTMinusAmber(
                periodicity.IsPhaseLockedConstrained,
                periodicity.IsScheduled,
                periodicity.ScheduleAllLaunchesWithinTolerance,
                periodicity.Solution.WithinTolerance,
                periodicity.DriftAmberReason,
                periodicity.ArrivalAmberReason,
                periodicity.IsReaim);
            if (!amber)
                return null;
            return JoinAmberReasons(periodicity.DriftAmberReason, periodicity.ArrivalAmberReason)
                ?? MissionPresentation.NextLaunchToleranceWarning;
        }

        // The mission title cell: a label that enters inline-edit on double-click (mirrors the
        // recordings window group rename). While editing, a text field replaces the label;
        // Enter commits, Escape cancels. Click-away commit is handled by the window-level
        // mouse-down check (see DrawWindow's outer event handling parity with RecordingsTableUI).
        private void DrawMissionTitleOrRename(Mission mission)
        {
            string display = string.IsNullOrEmpty(mission.Name) ? "(mission)" : mission.Name;

            if (renamingMissionId == mission.Id)
            {
                bool submit = Event.current.type == EventType.KeyDown &&
                    (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
                bool cancel = Event.current.type == EventType.KeyDown &&
                    Event.current.keyCode == KeyCode.Escape;

                GUI.SetNextControlName("MissionRename");
                renamingMissionText = GUILayout.TextField(renamingMissionText, GUILayout.ExpandWidth(true));
                activeMissionRenameRect = GUILayoutUtility.GetLastRect();
                if (!renamingMissionFocused)
                {
                    GUI.FocusControl("MissionRename");
                    renamingMissionFocused = true;
                }
                if (submit)
                {
                    CommitMissionRename(mission);
                    Event.current.Use();
                }
                else if (cancel)
                {
                    renamingMissionId = null;
                    Event.current.Use();
                }
                return;
            }

            // Bold transparent text on the row bubble (clickable for rename); no box of its own,
            // since the row's missionHeaderRowStyle already supplies the dark background.
            // A main mission's name is its Recordings folder's name, so it takes the folder rules.
            string renameHover = MissionStore.IsOriginalMission(mission)
                ? MissionHeaderRenameHoverOriginal
                : MissionHeaderRenameHoverClone;
            if (GUILayout.Button(new GUIContent(display, renameHover), missionHeaderTextStyle, GUILayout.ExpandWidth(true)))
            {
                float t = Time.realtimeSinceStartup;
                if (lastClickedMissionId == mission.Id && t - lastMissionClickTime < DoubleClickThreshold)
                {
                    // Commit any other in-progress rename before starting this one.
                    if (renamingMissionId != null)
                        CommitMissionRenameById(renamingMissionId);
                    renamingMissionId = mission.Id;
                    renamingMissionText = mission.Name ?? "";
                    renamingMissionFocused = false;
                    lastClickedMissionId = null;
                    ParsekLog.Verbose("Mission", $"Rename started for mission '{mission.Name}'");
                }
                else
                {
                    lastClickedMissionId = mission.Id;
                    lastMissionClickTime = t;
                }
            }
        }

        // Commits the in-progress mission-title rename. Empty / unchanged text is discarded.
        private void CommitMissionRename(Mission mission)
        {
            if (mission == null)
            {
                renamingMissionId = null;
                renamingMissionFocused = false;
                return;
            }
            string newName = (renamingMissionText ?? "").Trim();
            if (!string.IsNullOrEmpty(newName) && newName != mission.Name)
            {
                if (MissionStore.IsOriginalMission(mission))
                {
                    // The main mission IS the tree's Recordings-window folder. Rename both (and the
                    // auto subgroups) atomically; on a group-name collision the rename is refused and
                    // the mission keeps its old name, so the two views never drift.
                    RecordingTree tree = FindTree(RecordingStore.CommittedTrees, mission.TreeId);
                    if (tree != null)
                    {
                        if (!MissionGroupLink.RenameMissionGroup(tree, newName, out string reason))
                            ParsekLog.Warn("Mission", $"Mission rename rejected: '{newName}' ({reason})");
                    }
                    else
                    {
                        // No committed tree (unexpected for a main mission) - rename the mission only.
                        MissionStore.RenameMission(mission, newName);
                    }
                }
                else
                {
                    // A clone is a variant selection that does not own the folder; rename it alone.
                    MissionStore.RenameMission(mission, newName);
                }
            }
            renamingMissionId = null;
            renamingMissionFocused = false;
            activeMissionRenameRect = default;
        }

        private void CommitMissionRenameById(string id)
        {
            var missions = MissionStore.Missions;
            for (int i = 0; i < missions.Count; i++)
                if (missions[i] != null && missions[i].Id == id)
                {
                    CommitMissionRename(missions[i]);
                    return;
                }
            renamingMissionId = null;
            renamingMissionFocused = false;
            activeMissionRenameRect = default;
        }
    }
}
