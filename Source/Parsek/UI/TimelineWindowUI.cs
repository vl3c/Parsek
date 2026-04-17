using System;
using System.Collections.Generic;
using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Timeline window — replaces ActionsWindowUI.
    /// Shows a chronological, read-only view of all committed career events
    /// from recordings and game actions, with current-UT divider, tier filtering,
    /// resource budget, and rewind access.
    /// </summary>
    internal class TimelineWindowUI
    {
        private readonly ParsekUI parentUI;

        // Window state
        private bool showTimelineWindow;
        private Rect timelineWindowRect;
        private Vector2 timelineScrollPos;
        private bool isResizingTimelineWindow;
        private bool timelineWindowHasInputLock;
        private const string TimelineInputLockId = "Parsek_TimelineWindow";
        private Rect lastTimelineWindowRect;
        private const float MinWindowWidth = 350f;
        private const float MinWindowHeight = 150f;
        private const float ApproxRowHeight = 20f;

        // Shared width for all top filter/preset buttons (Overview, Details, Recordings,
        // Actions, Events + Last Day / Last 7d / Last 30d / This Year / All / Custom).
        // Every button in the Timeline's top zone uses the same width so the rows align.
        private const float FilterButtonWidth = 93f;

        // Cached timeline data (invalidated on triggers)
        private List<TimelineEntry> cachedTimeline;
        private bool timelineDirty = true;

        // Filter state
        private bool showDetail = false;
        private bool showRecordingEntries = true;
        private bool showActionEntries = true;
        private bool showEventEntries = true;

        // Cross-link: tracks which recordingId was last set externally
        // so we can scroll to it once
        private string pendingScrollToRecordingId;

        // Styles
        private GUIStyle timelineGrayStyle;
        private GUIStyle timelineWhiteStyle;
        private GUIStyle timelineGreenStyle;
        private GUIStyle timelineRedStyle;
        private GUIStyle timelineDimStyle;
        private GUIStyle timelineStrikethroughStyle;
        private GUIStyle timelineBlueStyle;
        private GUIStyle toggleButtonStyle;

        // Cached stats text — refreshed on cache rebuild or filter change
        private string cachedStatsText;
        private bool filterDirty = true;

        // Time-range filter UI state
        private bool showCustomRange;
        private float sliderMin;
        private float sliderMax;
        private float sliderBoundMin;
        private float sliderBoundMax;
        private bool sliderBoundsInitialized;

        // Cached recording lookup by ID — refreshed on cache rebuild
        private Dictionary<string, Recording> recordingById;

        public bool IsOpen
        {
            get { return showTimelineWindow; }
            set { showTimelineWindow = value; }
        }

        internal TimelineWindowUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
        }

        public void DrawIfOpen(Rect mainWindowRect)
        {
            if (!showTimelineWindow)
            {
                ReleaseInputLock();
                return;
            }

            // Position to the left of main window on first open. Default width must
            // accommodate 6 minimum-width buttons + inter-button margin budget
            // (6*93 + 28 + chrome ≈ 608), with some breathing room.
            if (timelineWindowRect.width < 1f)
            {
                float x = mainWindowRect.x - 650;
                if (x < 0) x = mainWindowRect.x + mainWindowRect.width + 10;
                float height = Math.Max(600f, mainWindowRect.height);
                timelineWindowRect = new Rect(x, mainWindowRect.y, 640, height);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI",
                    $"Timeline window initial position: x={x.ToString("F0", ic)} y={mainWindowRect.y.ToString("F0", ic)} (mainWindow.x={mainWindowRect.x.ToString("F0", ic)})");
            }

            ParsekUI.HandleResizeDrag(ref timelineWindowRect, ref isResizingTimelineWindow,
                MinWindowWidth, MinWindowHeight, "Timeline window");

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            timelineWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekTimeline".GetHashCode(),
                timelineWindowRect,
                DrawTimelineWindow,
                "Parsek - Timeline",
                opaqueWindowStyle,
                GUILayout.Width(timelineWindowRect.width),
                GUILayout.Height(timelineWindowRect.height)
            );
            parentUI.LogWindowPosition("Timeline", ref lastTimelineWindowRect, timelineWindowRect);

            if (timelineWindowRect.Contains(Event.current.mousePosition))
            {
                if (!timelineWindowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, TimelineInputLockId);
                    timelineWindowHasInputLock = true;
                }
            }
            else
            {
                ReleaseInputLock();
            }
        }

        public void ReleaseInputLock()
        {
            if (!timelineWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(TimelineInputLockId);
            timelineWindowHasInputLock = false;
        }

        /// <summary>
        /// Called by the Recordings Manager to scroll the timeline to a specific recording.
        /// The next draw pass will scroll to the RecordingStart entry with this ID.
        /// </summary>
        internal void ScrollToRecording(string recordingId)
        {
            pendingScrollToRecordingId = recordingId;
            ParsekLog.Verbose("Timeline", $"Cross-link: scroll requested for recordingId={recordingId}");
        }

        /// <summary>
        /// Resets the time-range filter slider positions to full bounds.
        /// Called when the filter is cleared from another window (e.g. Recordings table).
        /// </summary>
        internal void ResetTimeRangeSliders()
        {
            sliderMin = sliderBoundMin;
            sliderMax = sliderBoundMax;
            filterDirty = true;
        }

        /// <summary>
        /// Marks the cached timeline as stale so it rebuilds on next draw.
        /// Called by ParsekUI on cache invalidation triggers.
        /// </summary>
        public void InvalidateCache()
        {
            timelineDirty = true;
            filterDirty = true;
            cachedStatsText = null;
            recordingById = null;
            ParsekLog.Verbose("Timeline", "Cache invalidated");
        }

        private void EnsureStyles()
        {
            if (timelineGrayStyle != null) return;

            timelineGrayStyle = new GUIStyle(GUI.skin.label);
            timelineGrayStyle.normal.textColor = Color.gray;

            timelineWhiteStyle = new GUIStyle(GUI.skin.label);
            timelineWhiteStyle.normal.textColor = Color.white;

            timelineGreenStyle = new GUIStyle(GUI.skin.label);
            timelineGreenStyle.normal.textColor = new Color(0.5f, 1f, 0.5f);

            timelineRedStyle = new GUIStyle(GUI.skin.label);
            timelineRedStyle.normal.textColor = new Color(1f, 0.6f, 0.6f);

            timelineDimStyle = new GUIStyle(GUI.skin.label);
            timelineDimStyle.normal.textColor = new Color(1f, 1f, 1f, 0.5f);

            timelineStrikethroughStyle = new GUIStyle(GUI.skin.label);
            timelineStrikethroughStyle.normal.textColor = Color.gray;

            timelineBlueStyle = new GUIStyle(GUI.skin.label);
            timelineBlueStyle.normal.textColor = new Color(0.5f, 0.7f, 1f);

            // Toggle button: "on" state reuses the button's own rounded-corner texture
            // but tints it darker via the on-state background color trick. Explicit
            // 4px horizontal margin gives adjacent buttons a visible gap (inter-button
            // gap = 4px after IMGUI's max-collapse); the GetResponsiveButtonWidth math
            // below accounts for this exact margin budget. Vertical margin inherits
            // from GUI.skin.button so the L toggle vertically aligns with the R /
            // FF / GoTo buttons (which use plain GUI.skin.button) in entry rows.
            toggleButtonStyle = new GUIStyle(GUI.skin.button);
            toggleButtonStyle.margin = new RectOffset(4, 4,
                GUI.skin.button.margin.top, GUI.skin.button.margin.bottom);
            toggleButtonStyle.onNormal.background = GUI.skin.button.active.background;
            toggleButtonStyle.onHover.background = GUI.skin.button.active.background;
            toggleButtonStyle.onNormal.textColor = Color.white;
            toggleButtonStyle.onHover.textColor = Color.white;
        }

        /// <summary>
        /// Shared button width used by every button in the top filter row (5 buttons)
        /// and the time-range preset row (6 buttons). Computed each frame from the
        /// current window width so both rows scale uniformly. The margin budget
        /// (outer+inter-cell gaps at 2px per margin.left/right) is subtracted before
        /// dividing by 6 so the preset row fills the available span exactly AND the
        /// filter row's FlexibleSpace expands to exactly one button-plus-gap —
        /// guaranteeing the Recordings/Actions/Events column centers sit directly
        /// above This Year/All/Custom in the row below. Floored at FilterButtonWidth
        /// so buttons never shrink below the minimum legibility width.
        /// </summary>
        private float GetResponsiveButtonWidth()
        {
            const float horizontalChromePx = 22f;      // approx left+right window padding
            // margin=(4,4,0,0): outer left (4) + 5 inter-button gaps (4 each) + outer right (4) = 28
            const float marginBudget = 28f;
            float avail = timelineWindowRect.width - horizontalChromePx - marginBudget;
            return Mathf.Max(FilterButtonWidth, avail / 6f);
        }

        private void DrawTimelineWindow(int windowID)
        {
            EnsureStyles();

            // Zone 1: Resource Budget
            DrawResourceBudget();

            // Zone 2: Filter Bar
            DrawFilterBar();

            // Zone 2b: Time-Range Filter
            DrawTimeRangeFilterBar();

            // Rebuild cache if dirty
            if (timelineDirty || cachedTimeline == null)
            {
                cachedTimeline = TimelineBuilder.Build(
                    RecordingStore.CommittedRecordings,
                    Ledger.Actions,
                    MilestoneStore.Milestones,
                    MilestoneStore.CurrentEpoch);
                timelineDirty = false;
                filterDirty = true;

                // Rebuild recording lookup cache
                var recordings = RecordingStore.CommittedRecordings;
                recordingById = new Dictionary<string, Recording>(recordings.Count);
                for (int i = 0; i < recordings.Count; i++)
                {
                    var r = recordings[i];
                    if (!string.IsNullOrEmpty(r.RecordingId))
                        recordingById[r.RecordingId] = r;
                }

                ParsekLog.Verbose("Timeline",
                    $"Cache rebuilt: {cachedTimeline.Count} entries, " +
                    $"{recordingById.Count} recordings indexed");
            }

            // Zone 3: Entry List
            DrawEntryList();

            GUILayout.FlexibleSpace();

            // Stats footer — count only visible (filtered) entries
            if (filterDirty || cachedStatsText == null)
            {
                int recCount = 0;
                int playerActionCount = 0;
                int eventCount = 0;
                if (cachedTimeline != null)
                {
                    for (int i = 0; i < cachedTimeline.Count; i++)
                    {
                        var e = cachedTimeline[i];
                        if (!IsEntryVisible(e)) continue;
                        if (e.Source == TimelineSource.Recording) recCount++;
                        else if (e.IsPlayerAction) playerActionCount++;
                        else eventCount++;
                    }
                }

                var stats = new System.Text.StringBuilder();
                stats.Append($"{recCount} Recording{(recCount == 1 ? "" : "s")}");
                stats.Append($", {playerActionCount} Action{(playerActionCount == 1 ? "" : "s")}");
                stats.Append($", {eventCount} Event{(eventCount == 1 ? "" : "s")}");
                cachedStatsText = stats.ToString();
                filterDirty = false;
            }
            GUILayout.Label(cachedStatsText, timelineGrayStyle);

            if (GUILayout.Button("Close"))
            {
                showTimelineWindow = false;
                ParsekLog.Verbose("UI", "Timeline window closed via button");
            }

            ParsekUI.DrawResizeHandle(timelineWindowRect, ref isResizingTimelineWindow,
                "Timeline window");

            GUI.DragWindow();
        }

        private void DrawFilterBar()
        {
            GUILayout.Space(5);

            // Top-filter buttons sit at fixed column positions matching the preset
            // row below: Overview=col1, Details=col2, (empty col3), Recordings=col4,
            // Actions=col5, Events=col6. The empty column 3 is an explicit
            // GUILayout.Space(btnW) so the source-group buttons are placed at
            // sequential positions, not right-anchored — this keeps their columns
            // locked to the preset row regardless of any future layout quirks.
            float btnW = GetResponsiveButtonWidth();

            GUILayout.BeginHorizontal();

            // Tier selector (columns 1-2).
            bool overviewActive = !showDetail;
            bool detailActive = showDetail;

            if (GUILayout.Toggle(overviewActive, "Overview", toggleButtonStyle, GUILayout.Width(btnW)) && !overviewActive)
            {
                showDetail = false;
                filterDirty = true;
                ParsekLog.Verbose("UI", "Timeline filter: Overview");
            }
            if (GUILayout.Toggle(detailActive, "Details", toggleButtonStyle, GUILayout.Width(btnW)) && !detailActive)
            {
                showDetail = true;
                filterDirty = true;
                ParsekLog.Verbose("UI", "Timeline filter: Details");
            }

            // Empty column 3 — keeps source-group buttons at col 4/5/6. Must be a
            // Label (not Space) so its margins participate in IMGUI's max-collapse
            // rule the same way the preset row's Last 30d button does at col 3;
            // GUILayout.Space doesn't carry a margin, so Space(btnW) would leave
            // Recordings 8px left of This Year due to the missing margin gap.
            GUILayout.Label("", GUILayout.Width(btnW));

            // Source toggles (columns 4-6).
            bool newShowRec = GUILayout.Toggle(showRecordingEntries, "Recordings", toggleButtonStyle, GUILayout.Width(btnW));
            if (newShowRec != showRecordingEntries)
            {
                showRecordingEntries = newShowRec;
                filterDirty = true;
                ParsekLog.Verbose("UI", $"Timeline source toggle: Recordings={showRecordingEntries}");
            }

            bool newShowAct = GUILayout.Toggle(showActionEntries, "Actions", toggleButtonStyle, GUILayout.Width(btnW));
            if (newShowAct != showActionEntries)
            {
                showActionEntries = newShowAct;
                filterDirty = true;
                ParsekLog.Verbose("UI", $"Timeline source toggle: Actions={showActionEntries}");
            }

            bool newShowEvt = GUILayout.Toggle(showEventEntries, "Events", toggleButtonStyle, GUILayout.Width(btnW));
            if (newShowEvt != showEventEntries)
            {
                showEventEntries = newShowEvt;
                filterDirty = true;
                ParsekLog.Verbose("UI", $"Timeline source toggle: Events={showEventEntries}");
            }

            GUILayout.EndHorizontal();
        }

        private void DrawTimeRangeFilterBar()
        {
            var filter = parentUI.TimeRangeFilter;
            var committed = RecordingStore.CommittedRecordings;
            double currentUT = 0;
            try { currentUT = Planetarium.GetUniversalTime(); } catch { }

            // Recompute slider bounds when timeline data changes
            if (!sliderBoundsInitialized || timelineDirty)
            {
                TimeRangeFilterLogic.ComputeSliderBounds(committed, currentUT,
                    out double bMin, out double bMax);
                sliderBoundMin = (float)bMin;
                sliderBoundMax = (float)bMax;
                if (!sliderBoundsInitialized)
                {
                    sliderMin = sliderBoundMin;
                    sliderMax = sliderBoundMax;
                    sliderBoundsInitialized = true;
                }
                else
                {
                    // Clamp slider positions to new bounds so thumbs don't
                    // end up outside the track after data changes
                    if (sliderMin < sliderBoundMin) sliderMin = sliderBoundMin;
                    if (sliderMin > sliderBoundMax) sliderMin = sliderBoundMax;
                    if (sliderMax < sliderBoundMin) sliderMax = sliderBoundMin;
                    if (sliderMax > sliderBoundMax) sliderMax = sliderBoundMax;
                }
            }

            bool hasRange = sliderBoundMax - sliderBoundMin > 1f;

            // Every preset button (including All and Custom) uses the same responsive
            // width as the top filter row so both rows column-align and scale together.
            float btnW = GetResponsiveButtonWidth();
            GUILayout.Space(2);
            GUILayout.BeginHorizontal();

            int secsPerDay = ParsekTimeFormat.SecsPerDay;
            int secsPerYear = ParsekTimeFormat.SecsPerYear;

            DrawPresetButton(filter, "Last Day", currentUT - secsPerDay, currentUT, currentUT, btnW);
            DrawPresetButton(filter, "Last 7d", currentUT - 7.0 * secsPerDay, currentUT, currentUT, btnW);
            DrawPresetButton(filter, "Last 30d", currentUT - 30.0 * secsPerDay, currentUT, currentUT, btnW);

            // "This Year" = current Kerbin/Earth calendar year boundaries
            double yearStart = System.Math.Floor(currentUT / secsPerYear) * secsPerYear;
            double yearEnd = yearStart + secsPerYear;
            DrawPresetButton(filter, "This Year", yearStart, yearEnd, currentUT, btnW);

            // "All" = clear filter
            bool allActive = !filter.IsActive;
            if (GUILayout.Toggle(allActive, "All", toggleButtonStyle, GUILayout.Width(btnW)) && !allActive)
            {
                filter.Clear();
                sliderMin = sliderBoundMin;
                sliderMax = sliderBoundMax;
                filterDirty = true;
                ParsekLog.Verbose("UI", "Time-range filter: cleared (All)");
            }

            // "Custom" toggle at the end of the preset row — reveals the sliders underneath.
            if (hasRange)
            {
                bool newShowCustom = GUILayout.Toggle(showCustomRange, "Custom", toggleButtonStyle, GUILayout.Width(btnW));
                if (newShowCustom != showCustomRange)
                {
                    showCustomRange = newShowCustom;
                    ParsekLog.Verbose("UI",
                        $"Time-range filter: custom sliders {(showCustomRange ? "shown" : "hidden")}");
                }
            }

            GUILayout.EndHorizontal();

            if (!hasRange) return;

            // Custom range sliders (visible only when the "Custom" toggle is on).
            if (showCustomRange)
            {
                // Active-range readout (only meaningful when the filter is set to a custom range, not a preset).
                if (filter.IsActive && filter.ActivePresetName == null)
                {
                    string rangeLabel = TimeRangeFilterLogic.FormatSliderLabel(filter.MinUT ?? sliderBoundMin)
                        + " \u2014 " + TimeRangeFilterLogic.FormatSliderLabel(filter.MaxUT ?? sliderBoundMax);
                    GUILayout.Label(rangeLabel, timelineDimStyle);
                }

                // From slider — slider gets a vertical nudge so the track aligns
                // with the label baselines (IMGUI's default slider renders a few px
                // higher than the adjacent labels in a horizontal row).
                GUILayout.BeginHorizontal();
                string fromLabel = TimeRangeFilterLogic.FormatSliderLabel(sliderMin);
                GUILayout.Label("From:", GUILayout.Width(38));
                GUILayout.BeginVertical();
                GUILayout.Space(9f);
                float newMin = GUILayout.HorizontalSlider(sliderMin, sliderBoundMin, sliderBoundMax);
                GUILayout.EndVertical();
                GUILayout.Label(fromLabel, GUILayout.Width(120));
                GUILayout.EndHorizontal();

                // To slider
                GUILayout.BeginHorizontal();
                string toLabel = TimeRangeFilterLogic.FormatSliderLabel(sliderMax);
                GUILayout.Label("To:", GUILayout.Width(38));
                GUILayout.BeginVertical();
                GUILayout.Space(9f);
                float newMax = GUILayout.HorizontalSlider(sliderMax, sliderBoundMin, sliderBoundMax);
                GUILayout.EndVertical();
                GUILayout.Label(toLabel, GUILayout.Width(120));
                GUILayout.EndHorizontal();

                // Clamp so From <= To
                if (newMin > newMax) newMin = newMax;

                // Apply immediately on slider change
                if (System.Math.Abs(newMin - sliderMin) > 0.5f || System.Math.Abs(newMax - sliderMax) > 0.5f)
                {
                    sliderMin = newMin;
                    sliderMax = newMax;
                    filter.SetRange(sliderMin, sliderMax);
                    filterDirty = true;
                }
            }
        }

        private void DrawPresetButton(TimeRangeFilterState filter, string name,
            double minUT, double maxUT, double currentUT, float width)
        {
            bool isActive = filter.IsActive && filter.ActivePresetName == name;
            if (GUILayout.Toggle(isActive, name, toggleButtonStyle, GUILayout.Width(width)) && !isActive)
            {
                // Clamp to slider bounds so we don't filter outside the data range
                double clampedMin = System.Math.Max(minUT, sliderBoundMin);
                double clampedMax = System.Math.Min(maxUT, sliderBoundMax);
                if (clampedMax < clampedMin) clampedMax = clampedMin;
                filter.SetRange(clampedMin, clampedMax, name);
                sliderMin = (float)clampedMin;
                sliderMax = (float)clampedMax;
                filterDirty = true;
                ParsekLog.Verbose("UI", $"Time-range filter: preset '{name}' " +
                    $"[{TimeRangeFilterLogic.FormatSliderLabel(clampedMin)} - " +
                    $"{TimeRangeFilterLogic.FormatSliderLabel(clampedMax)}]");
            }
        }

        private void DrawEntryList()
        {
            if (cachedTimeline == null || cachedTimeline.Count == 0)
            {
                GUILayout.Space(5);
                GUILayout.Label("No timeline entries.");
                return;
            }

            double currentUT = 0;
            try { currentUT = Planetarium.GetUniversalTime(); } catch { }

            GUILayout.Space(5);

            // Handle pending cross-link scroll: find the target row index
            // before entering the scroll view so we can set the scroll position
            int scrollTargetRow = -1;
            if (!string.IsNullOrEmpty(pendingScrollToRecordingId) && cachedTimeline != null)
            {
                int visibleRow = 0;
                for (int i = 0; i < cachedTimeline.Count; i++)
                {
                    var e = cachedTimeline[i];
                    if (!IsEntryVisible(e)) continue;
                    if (e.Type == TimelineEntryType.RecordingStart &&
                        e.RecordingId == pendingScrollToRecordingId)
                    {
                        scrollTargetRow = visibleRow;
                        break;
                    }
                    visibleRow++;
                }
                if (scrollTargetRow >= 0)
                {
                    timelineScrollPos.y = scrollTargetRow * ApproxRowHeight;
                    ParsekLog.Verbose("Timeline",
                        $"Cross-link: scrolled to row {scrollTargetRow} for recordingId={pendingScrollToRecordingId}");
                }
                else
                {
                    // E14: stale id (recording purged, outside visibility filter, etc.).
                    // Behavior unchanged — we still clear the pending id below — but
                    // the click trail is no longer silent.
                    ParsekLog.Verbose("Timeline",
                        $"Timeline scroll target not found: id={pendingScrollToRecordingId}");
                }
                pendingScrollToRecordingId = null;
            }

            timelineScrollPos = GUILayout.BeginScrollView(timelineScrollPos, GUILayout.ExpandHeight(true));

            // Dark list-area background (matches Career State / Recordings body look).
            GUILayout.BeginVertical(GUI.skin.box);

            bool dividerDrawn = false;

            for (int i = 0; i < cachedTimeline.Count; i++)
            {
                var entry = cachedTimeline[i];

                // Visibility check
                if (!IsEntryVisible(entry)) continue;

                // Draw divider before the first future entry
                if (!dividerDrawn && entry.UT > currentUT)
                {
                    DrawNowDivider(currentUT);
                    dividerDrawn = true;
                }

                bool isFuture = entry.UT > currentUT;
                DrawEntryRow(entry, isFuture);
            }

            // Draw divider at the end if all entries are in the past
            if (!dividerDrawn)
            {
                DrawNowDivider(currentUT);
            }

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
        }

        private bool IsEntryVisible(TimelineEntry entry)
        {
            if (entry.Tier == SignificanceTier.T2 && !showDetail) return false;
            if (entry.Source == TimelineSource.Recording && !showRecordingEntries) return false;
            if (entry.Source == TimelineSource.GameAction || entry.Source == TimelineSource.Legacy)
            {
                if (entry.IsPlayerAction && !showActionEntries) return false;
                if (!entry.IsPlayerAction && !showEventEntries) return false;
            }

            // Time-range filter
            var filter = parentUI.TimeRangeFilter;
            if (filter.IsActive && !TimeRangeFilterLogic.IsUTInRange(entry.UT, filter.MinUT, filter.MaxUT))
                return false;

            return true;
        }

        private void DrawNowDivider(double currentUT)
        {
            string utText;
            try { utText = KSPUtil.PrintDateCompact(currentUT, true); }
            catch { utText = currentUT.ToString("F0", System.Globalization.CultureInfo.InvariantCulture); }

            GUILayout.Space(3);
            GUILayout.Label($"\u2500\u2500 {utText} (now) \u2500\u2500", timelineGrayStyle);
            GUILayout.Space(3);
        }

        private void DrawEntryRow(TimelineEntry entry, bool isFuture)
        {
            GUILayout.BeginHorizontal();

            // Pick style based on entry state
            GUIStyle style;
            if (!entry.IsEffective)
                style = timelineStrikethroughStyle;
            else if (isFuture)
                style = timelineDimStyle;
            else if (entry.IsPlayerAction)
                style = timelineBlueStyle;
            else
                style = GetStyleForColor(entry.DisplayColor);

            // UT column
            string time;
            try { time = KSPUtil.PrintDateCompact(entry.UT, true); }
            catch { time = entry.UT.ToString("F0", System.Globalization.CultureInfo.InvariantCulture); }
            GUILayout.Label(time, style, GUILayout.Width(90));

            // Visual spacer between UT and description — matches the breathing room
            // that appears before the R / FF / L / GoTo buttons on the far right.
            GUILayout.Space(14f);

            // Description text
            GUILayout.Label(entry.DisplayText, style, GUILayout.ExpandWidth(true));

            // R/FF + GoTo for RecordingStart entries (R/FF first, GoTo last for alignment)
            if (entry.Type == TimelineEntryType.RecordingStart && !string.IsNullOrEmpty(entry.RecordingId))
            {
                var rec = FindRecordingById(entry.RecordingId);
                if (rec != null)
                {
                    var tableUI = parentUI.GetRecordingsTableUI();
                    bool isRecording = parentUI.InFlightMode && parentUI.Flight != null && parentUI.Flight.IsRecording;

                    if (isFuture)
                    {
                        // Future recording: FF button
                        string ffReason;
                        bool canFF = RecordingStore.CanFastForward(rec, out ffReason, isRecording: isRecording);
                        GUI.enabled = canFF;
                        if (GUILayout.Button(new GUIContent("FF", canFF ? "Fast-forward to this launch" : ffReason),
                            GUILayout.Width(35)))
                        {
                            ParsekLog.Info("UI",
                                $"Timeline FF button clicked: \"{rec.VesselName}\" id={rec.RecordingId}");
                            tableUI.ShowFastForwardConfirmation(rec);
                        }
                        GUI.enabled = true;
                    }
                    else if (!string.IsNullOrEmpty(RecordingStore.GetRewindSaveFileName(rec)))
                    {
                        // Past/active recording: Rewind button
                        string rewindReason;
                        bool canRewind = RecordingStore.CanRewind(rec, out rewindReason, isRecording: isRecording);
                        GUI.enabled = canRewind;
                        if (GUILayout.Button(new GUIContent("R", canRewind ? "Rewind to this launch" : rewindReason),
                            GUILayout.Width(25)))
                        {
                            ParsekLog.Info("UI",
                                $"Timeline rewind button clicked: \"{rec.VesselName}\" id={rec.RecordingId}");
                            tableUI.ShowRewindConfirmation(rec);
                        }
                        GUI.enabled = true;
                    }

                    // L (loop toggle) — show for past/active recordings that are loopable,
                    // or any recording already looping so the timeline can still disable it.
                    // Uses the shared toggleButtonStyle so the "on" state looks pressed in
                    // (same idiom as the filter/tab toggles); text color stays default.
                    if (ShouldShowLoopToggle(rec, isFuture))
                    {
                        string lTooltip = rec.LoopPlayback ? "Disable looping" : "Enable looping (uses saved interval)";
                        bool newLoop = GUILayout.Toggle(rec.LoopPlayback, new GUIContent("L", lTooltip),
                            toggleButtonStyle, GUILayout.Width(25));
                        if (newLoop != rec.LoopPlayback)
                        {
                            rec.LoopPlayback = newLoop;
                            RecordingsTableUI.ApplyAutoLoopRange(rec, rec.LoopPlayback);
                            if (!rec.LoopPlayback)
                                tableUI?.ClearLoopPeriodFocus();
                            ParsekLog.Info("UI",
                                $"Timeline loop toggled {(rec.LoopPlayback ? "ON" : "OFF")} for \"{rec.VesselName}\" id={rec.RecordingId}");
                        }
                    }

                    // GoTo button — always last, right-aligned
                    if (GUILayout.Button(new GUIContent("GoTo", "Show in Recordings Manager"), GUILayout.Width(48)))
                    {
                        parentUI.SelectedRecordingId = entry.RecordingId;
                        if (tableUI != null)
                            tableUI.ScrollToRecording(entry.RecordingId);
                        ParsekLog.Verbose("Timeline",
                            $"GoTo: \"{rec.VesselName}\" id={entry.RecordingId}");
                    }
                }
            }

            GUILayout.EndHorizontal();
        }

        internal static bool ShouldShowLoopToggle(Recording rec, bool isFuture)
        {
            return !isFuture
                && rec != null
                && (rec.LoopPlayback || Recording.IsLoopableRecording(rec));
        }

        private GUIStyle GetStyleForColor(Color color)
        {
            if (color.g > 0.9f && color.r < 0.6f) return timelineGreenStyle;
            if (color.r > 0.9f && color.g < 0.6f) return timelineRedStyle;
            return timelineWhiteStyle;
        }

        private Recording FindRecordingById(string recordingId)
        {
            if (recordingById == null || string.IsNullOrEmpty(recordingId))
                return null;
            Recording rec;
            recordingById.TryGetValue(recordingId, out rec);
            return rec;
        }

        private void DrawResourceBudget()
        {
            // Hide budget entirely in sandbox mode
            try
            {
                if (HighLogic.CurrentGame?.Mode == Game.Modes.SANDBOX)
                    return;
            }
            catch { }

            var budget = parentUI.GetCachedBudget();

            if (budget.reservedFunds <= 0 && budget.reservedScience <= 0 && budget.reservedReputation <= 0)
                return;

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            GUILayout.Space(5);
            GUILayout.Label("Resources", parentUI.GetSectionHeaderStyle());

            bool anyOverCommitted = false;
            bool isScienceMode = false;
            try { isScienceMode = HighLogic.CurrentGame?.Mode == Game.Modes.SCIENCE_SANDBOX; }
            catch { }

            if (!isScienceMode && budget.reservedFunds > 0)
            {
                double currentFunds = 0;
                try { if (Funding.Instance != null) currentFunds = Funding.Instance.Funds; } catch { }
                anyOverCommitted |= DrawResourceLine("Funds", currentFunds, budget.reservedFunds, "N0", ic);
            }

            if (budget.reservedScience > 0)
            {
                double currentScience = 0;
                try { if (ResearchAndDevelopment.Instance != null) currentScience = ResearchAndDevelopment.Instance.Science; } catch { }
                anyOverCommitted |= DrawResourceLine("Science", currentScience, budget.reservedScience, "F1", ic);
            }

            if (!isScienceMode && budget.reservedReputation > 0)
            {
                float currentRep = 0;
                try { if (Reputation.Instance != null) currentRep = Reputation.Instance.reputation; } catch { }
                anyOverCommitted |= DrawResourceLine("Reputation", (double)currentRep, budget.reservedReputation, "F0", ic);
            }

            if (anyOverCommitted)
            {
                Color prev = GUI.contentColor;
                GUI.contentColor = Color.yellow;
                GUILayout.Label("Over-committed! Some timeline actions may fail.");
                GUI.contentColor = prev;
            }
        }

        /// <summary>
        /// Draws a single resource budget line. Returns true if over-committed.
        /// </summary>
        private static bool DrawResourceLine(string label, double currentAmount, double reserved,
            string format, System.Globalization.CultureInfo ic)
        {
            double available = currentAmount - reserved;
            double total = currentAmount;
            bool over = available < 0;
            Color prev = GUI.contentColor;
            if (over) GUI.contentColor = Color.red;
            GUILayout.Label($"{label}: {available.ToString(format, ic)} available to use ({reserved.ToString(format, ic)} committed out of {total.ToString(format, ic)} total)");
            GUI.contentColor = prev;
            return over;
        }
    }
}
