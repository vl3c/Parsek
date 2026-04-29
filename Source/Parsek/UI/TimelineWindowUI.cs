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
        internal enum TimelineWatchButtonAction
        {
            Enter,
            Exit
        }

        /// <summary>
        /// Identifies the timeline row actions that share the button-width contract.
        /// </summary>
        internal enum TimelineRowActionButtonKind
        {
            Watch,
            FastForward,
            Rewind,
            Loop,
            GoTo
        }

        private enum TimelineTierFilterMode
        {
            Overview,
            Details,
            RewindOrFastForward
        }

        private readonly ParsekUI parentUI;

        // Window state
        private bool showTimelineWindow;
        private Rect timelineWindowRect;
        private Vector2 timelineScrollPos;
        private bool isResizingTimelineWindow;
        private bool timelineWindowHasInputLock;
        private const string TimelineInputLockId = "Parsek_TimelineWindow";
        private Rect lastTimelineWindowRect;
        private const float DefaultWindowWidth = CareerStateWindowUI.DefaultWindowWidth;
        private const float MinWindowWidth = CareerStateWindowUI.MinWindowWidth;
        private const float MinWindowHeight = 150f;
        private const float ApproxRowHeight = 20f;
        // Keep the short row actions aligned; GoTo stays wider for its text label.
        private const float RowActionButtonWidth = 35f;
        private const float GoToButtonWidth = 48f;

        // Shared width for all top filter/preset buttons (Overview, Details, Rewind/FF,
        // Recordings, Actions, Events + Last Day / Last 7d / Last 30d / This Year / All / Custom).
        // Every button in the Timeline's top zone uses the same width so the rows align.
        private const float FilterButtonWidth = 93f;

        // Cached timeline data (invalidated on triggers)
        private List<TimelineEntry> cachedTimeline;
        private bool timelineDirty = true;

        // Filter state
        private TimelineTierFilterMode tierFilterMode = TimelineTierFilterMode.Overview;
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
        private Dictionary<string, int> recordingIndexById;
        private Dictionary<string, bool> rewindSaveExistsByRecordingId;
        private readonly Dictionary<string, bool> lastCanWatchByRecId = new Dictionary<string, bool>();

        internal readonly struct TimelineWatchButtonDescriptor
        {
            internal TimelineWatchButtonDescriptor(
                string label,
                string tooltip,
                bool enabled,
                bool canWatch,
                TimelineWatchButtonAction action)
            {
                Label = label;
                Tooltip = tooltip;
                Enabled = enabled;
                CanWatch = canWatch;
                Action = action;
            }

            internal string Label { get; }
            internal string Tooltip { get; }
            internal bool Enabled { get; }
            internal bool CanWatch { get; }
            internal TimelineWatchButtonAction Action { get; }
        }

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

            // Position to the left of main window on first open. Keep Timeline's
            // default width aligned with Career State so both wide data windows
            // have the same footprint once the row action buttons share one width.
            if (timelineWindowRect.width < 1f)
            {
                float x = mainWindowRect.x - (DefaultWindowWidth + 10f);
                if (x < 0) x = mainWindowRect.x + mainWindowRect.width + 10;
                float height = Math.Max(600f, mainWindowRect.height);
                timelineWindowRect = new Rect(x, mainWindowRect.y, DefaultWindowWidth, height);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI",
                    $"Timeline window initial position: x={x.ToString("F0", ic)} y={mainWindowRect.y.ToString("F0", ic)} (mainWindow.x={mainWindowRect.x.ToString("F0", ic)})");
            }

            ParsekUI.HandleResizeDrag(ref timelineWindowRect, ref isResizingTimelineWindow,
                MinWindowWidth, MinWindowHeight, "Timeline window");

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;
            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                timelineWindowRect = ClickThruBlocker.GUILayoutWindow(
                    "ParsekTimeline".GetHashCode(),
                    timelineWindowRect,
                    DrawTimelineWindow,
                    "Parsek - Timeline",
                    opaqueWindowStyle,
                    GUILayout.Width(timelineWindowRect.width),
                    GUILayout.Height(timelineWindowRect.height)
                );
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
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
            recordingIndexById = null;
            rewindSaveExistsByRecordingId = null;
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
                // [Phase 3] ERS+ELS-routed: timeline view feeds from visible
                // recordings and non-tombstoned ledger actions only (design §3.4).
                cachedTimeline = TimelineBuilder.Build(
                    EffectiveState.ComputeERS(),
                    EffectiveState.ComputeELS(),
                    MilestoneStore.Milestones,
                    GameStateStore.IsEventVisibleToCurrentTimeline,
                    GetCurrentGameMode());
                timelineDirty = false;
                filterDirty = true;

                // Rebuild recording lookup cache (ERS-scoped so cross-link
                // navigation only resolves to visible recordings).
                var recordings = EffectiveState.ComputeERS();
                recordingById = new Dictionary<string, Recording>(recordings.Count);
                recordingIndexById = BuildRecordingIndexLookup(recordings);
                rewindSaveExistsByRecordingId = new Dictionary<string, bool>(recordings.Count);
                RecordingsTableUI.PruneStaleWatchEntries(lastCanWatchByRecId, null, recordings);
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

            // Stats footer — count only visible (filtered) entries. Rewind/FF mode
            // depends on live currentUT, so its counts can change without any
            // explicit filter toggle.
            bool statsDependOnCurrentUT = tierFilterMode == TimelineTierFilterMode.RewindOrFastForward;
            if (filterDirty || cachedStatsText == null || statsDependOnCurrentUT)
            {
                double currentUT = 0;
                try { currentUT = Planetarium.GetUniversalTime(); } catch { }

                int recCount = 0;
                int playerActionCount = 0;
                int eventCount = 0;
                if (cachedTimeline != null)
                {
                    for (int i = 0; i < cachedTimeline.Count; i++)
                    {
                        var e = cachedTimeline[i];
                        if (!IsEntryVisible(e, currentUT)) continue;
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

        private static Game.Modes? GetCurrentGameMode()
        {
            try
            {
                var currentGame = HighLogic.CurrentGame;
                if (currentGame != null)
                    return currentGame.Mode;
            }
            catch (NullReferenceException) { }

            return null;
        }

        private void DrawFilterBar()
        {
            GUILayout.Space(5);

            // First-row buttons sit at fixed column positions matching the preset row
            // below: Overview=col1, Details=col2, (empty col3), Recordings=col4,
            // Actions=col5, Events=col6. The second row adds a lone Rewind/FF toggle
            // directly under Overview using the same width, so it reads as a third
            // mutually-exclusive tier preset instead of another source toggle.
            float btnW = GetResponsiveButtonWidth();

            GUILayout.BeginHorizontal();

            // Tier selector (columns 1-2).
            bool overviewActive = tierFilterMode == TimelineTierFilterMode.Overview;
            bool detailActive = tierFilterMode == TimelineTierFilterMode.Details;

            if (GUILayout.Toggle(overviewActive, "Overview", toggleButtonStyle, GUILayout.Width(btnW)) && !overviewActive)
            {
                tierFilterMode = TimelineTierFilterMode.Overview;
                filterDirty = true;
                ParsekLog.Verbose("UI", "Timeline filter: Overview");
            }
            if (GUILayout.Toggle(detailActive, "Details", toggleButtonStyle, GUILayout.Width(btnW)) && !detailActive)
            {
                tierFilterMode = TimelineTierFilterMode.Details;
                filterDirty = true;
                ParsekLog.Verbose("UI", "Timeline filter: Details");
            }

            // Empty column 3 — keeps source-group buttons at col 4/5/6. Must be a
            // Label (not Space) so its margins participate in IMGUI's max-collapse
            // rule the same way the preset row's Last 30d button does at col 3;
            // GUILayout.Space doesn't carry a margin, so Space(btnW) would leave
            // Recordings 8px left of This Year due to the missing margin gap.
            GUILayout.Label("", GUILayout.Width(btnW));

            bool rewindOrFastForwardMode = tierFilterMode == TimelineTierFilterMode.RewindOrFastForward;
            if (rewindOrFastForwardMode && !showRecordingEntries)
            {
                showRecordingEntries = true;
                filterDirty = true;
            }

            // Source toggles (columns 4-6).
            bool previousGuiEnabled = GUI.enabled;
            GUI.enabled = !rewindOrFastForwardMode;
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
            GUI.enabled = previousGuiEnabled;

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            bool rewindOrFastForwardActive = tierFilterMode == TimelineTierFilterMode.RewindOrFastForward;
            if (GUILayout.Toggle(rewindOrFastForwardActive, "Rewind/FF", toggleButtonStyle, GUILayout.Width(btnW)) &&
                !rewindOrFastForwardActive)
            {
                tierFilterMode = TimelineTierFilterMode.RewindOrFastForward;
                showRecordingEntries = true;
                filterDirty = true;
                ParsekLog.Verbose("UI", "Timeline filter: Rewind/FF");
            }
            GUILayout.EndHorizontal();
        }

        private void DrawTimeRangeFilterBar()
        {
            var filter = parentUI.TimeRangeFilter;
            // [Phase 3] ERS-routed: time-range slider bounds are computed from
            // visible recordings only.
            var committed = EffectiveState.ComputeERS();
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
                    if (!IsEntryVisible(e, currentUT)) continue;
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
                if (!IsEntryVisible(entry, currentUT)) continue;

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

        private bool IsEntryVisible(TimelineEntry entry, double currentUT)
        {
            if (tierFilterMode == TimelineTierFilterMode.RewindOrFastForward)
            {
                var rec = FindRecordingById(entry.RecordingId);
                bool canFastForward = false;
                if (ShouldShowFastForwardButton(rec, entry != null && entry.UT > currentUT))
                    canFastForward = CanFastForwardAtCurrentUT(rec, currentUT);

                bool canRewind = false;
                if (ShouldShowRewindButton(rec, entry != null && entry.UT > currentUT))
                    canRewind = CanRewindWithResolvedSaveState(rec);

                if (!HasActionableRewindOrFastForwardButton(entry, rec, currentUT, canFastForward, canRewind))
                    return false;
            }
            else if (entry.Tier == SignificanceTier.T2 && tierFilterMode == TimelineTierFilterMode.Overview)
            {
                return false;
            }

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
                    var flight = parentUI.Flight;
                    int recIndex = FindRecordingIndexById(entry.RecordingId);

                    // Watch button - flight only. Shown disabled for entries that
                    // are not currently watchable so the action layout stays stable.
                    if (ShouldShowWatchButton(parentUI.InFlightMode && flight != null, rec))
                    {
                        bool hasGhost = recIndex >= 0 && flight.HasActiveGhost(recIndex);
                        bool sameBody = recIndex >= 0 && flight.IsGhostOnSameBody(recIndex);
                        bool inRange = recIndex >= 0 && flight.IsGhostWithinVisualRange(recIndex);
                        bool isWatching = recIndex >= 0 && flight.WatchedRecordingIndex == recIndex;
                        TimelineWatchButtonDescriptor watchButton = BuildWatchButtonDescriptor(
                            isWatching, hasGhost, sameBody, inRange, rec.IsDebris);

                        if (RecordingsTableUI.UpdateWatchButtonTransitionCache(
                            lastCanWatchByRecId, rec.RecordingId, watchButton.CanWatch))
                        {
                            string reason = RecordingsTableUI.GetWatchButtonReason(
                                watchButton.CanWatch, hasGhost, sameBody, inRange, rec.IsDebris);
                            string eligibility = recIndex >= 0
                                ? flight.DescribeWatchEligibilityForLogs(recIndex)
                                : "watchEval(rec=<missing-index>)";
                            ParsekLog.Info("UI",
                                $"Timeline watch button \"{rec.VesselName}\" id={rec.RecordingId} {reason} " +
                                $"(hasGhost={hasGhost} sameBody={sameBody} inRange={inRange} debris={rec.IsDebris}) " +
                                $"{eligibility} {flight.DescribeWatchFocusForLogs()}");
                        }

                        GUI.enabled = watchButton.Enabled;
                        if (GUILayout.Button(
                            new GUIContent(watchButton.Label, watchButton.Tooltip),
                            GUILayout.Width(GetRowActionButtonWidth(TimelineRowActionButtonKind.Watch))))
                        {
                            string beforeFocus = flight.DescribeWatchFocusForLogs();
                            string beforeEligibility = flight.DescribeWatchEligibilityForLogs(recIndex);
                            ApplyWatchButtonAction(
                                watchButton.Action,
                                recIndex,
                                () => flight.ExitWatchMode(),
                                index => flight.EnterWatchMode(index));
                            ParsekLog.Info("UI",
                                $"Timeline W button clicked: {(isWatching ? "exit" : "enter")} watch on " +
                                $"\"{rec.VesselName}\" id={rec.RecordingId} before={beforeEligibility} " +
                                $"beforeFocus={beforeFocus} afterFocus={flight.DescribeWatchFocusForLogs()}");
                        }
                        GUI.enabled = true;
                    }

                    bool showFastForward = ShouldShowFastForwardButton(rec, isFuture);
                    bool showRewind = ShouldShowRewindButton(rec, isFuture);

                    if (showFastForward)
                    {
                        // Future recording: FF button
                        string ffReason;
                        bool canFF = CanFastForwardNow(rec, out ffReason);
                        GUI.enabled = canFF;
                        if (GUILayout.Button(new GUIContent("FF", canFF ? "Fast-forward to this launch" : ffReason),
                            GUILayout.Width(GetRowActionButtonWidth(TimelineRowActionButtonKind.FastForward))))
                        {
                            ParsekLog.Info("UI",
                                $"Timeline FF button clicked: \"{rec.VesselName}\" id={rec.RecordingId}");
                            tableUI.ShowFastForwardConfirmation(rec);
                        }
                        GUI.enabled = true;
                    }
                    else if (showRewind)
                    {
                        // Past/active recording: Rewind button
                        string rewindReason;
                        bool canRewind = CanRewindWithResolvedSaveState(rec, out rewindReason);
                        GUI.enabled = canRewind;
                        if (GUILayout.Button(new GUIContent("R", canRewind ? "Rewind to this launch" : rewindReason),
                            GUILayout.Width(GetRowActionButtonWidth(TimelineRowActionButtonKind.Rewind))))
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
                    // Suppressed for Unfinished Flight recordings: loop on a crashed
                    // sibling that the user is meant to re-fly is misleading. Mirrors
                    // the rec window's hide-loop-checkbox treatment for the virtual
                    // "Unfinished Flights" group.
                    bool isUnfinishedFlight = EffectiveState.IsUnfinishedFlight(rec);
                    if (ShouldShowLoopToggle(rec, isFuture, isUnfinishedFlight))
                    {
                        string lTooltip = rec.LoopPlayback ? "Disable looping" : "Enable looping (uses saved interval)";
                        bool newLoop = GUILayout.Toggle(rec.LoopPlayback, new GUIContent("L", lTooltip),
                            toggleButtonStyle, GUILayout.Width(GetRowActionButtonWidth(TimelineRowActionButtonKind.Loop)));
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
                    if (GUILayout.Button(
                            new GUIContent("GoTo", "Show in Recordings Manager"),
                            GUILayout.Width(GetRowActionButtonWidth(TimelineRowActionButtonKind.GoTo))))
                    {
                        parentUI.SelectedRecordingId = entry.RecordingId;
                        if (tableUI != null)
                            tableUI.ScrollToRecording(entry.RecordingId);
                        ParsekLog.Verbose("Timeline",
                            $"GoTo: \"{rec.VesselName}\" id={entry.RecordingId}");
                    }
                }
            }
            else if ((entry.Type == TimelineEntryType.UnfinishedFlightSeparation
                      || entry.Type == TimelineEntryType.Separation)
                     && !string.IsNullOrEmpty(entry.RecordingId))
            {
                // Separation row: tree-child split point. UF flavour gets a
                // Fly button; the plain post-merge / non-UF flavour just
                // gets GoTo. No Watch / Loop / R / FF — those are launch-
                // playback affordances, not split affordances.
                var rec = FindRecordingById(entry.RecordingId);
                if (rec != null)
                {
                    var tableUI = parentUI.GetRecordingsTableUI();

                    if (entry.Type == TimelineEntryType.UnfinishedFlightSeparation)
                    {
                        DrawTimelineFlyButton(rec);
                    }

                    if (GUILayout.Button(
                            new GUIContent("GoTo", "Show in Recordings Manager"),
                            GUILayout.Width(GetRowActionButtonWidth(TimelineRowActionButtonKind.GoTo))))
                    {
                        parentUI.SelectedRecordingId = entry.RecordingId;
                        if (tableUI != null)
                            tableUI.ScrollToRecording(entry.RecordingId);
                        ParsekLog.Verbose("Timeline",
                            $"GoTo: \"{rec.VesselName}\" id={entry.RecordingId} " +
                            $"(separation entry, type={entry.Type})");
                    }
                }
            }

            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Draw the timeline-row "Fly" button for an Unfinished Flight
        /// separation entry. Resolves the RP slot via the shared
        /// <see cref="RecordingsTableUI.ResolveUnfinishedFlightRewindRoute"/>
        /// so the timeline and the Recordings table dispatch through the
        /// same code path; clicking ends in
        /// <see cref="RewindInvoker.ShowDialog"/>. Width matches the
        /// other row action buttons.
        /// </summary>
        private void DrawTimelineFlyButton(Recording rec)
        {
            float width = GetRowActionButtonWidth(TimelineRowActionButtonKind.Rewind);

            RewindPoint rp;
            int slotListIndex;
            string routeReason;
            var route = RecordingsTableUI.ResolveUnfinishedFlightRewindRoute(
                rec, out rp, out slotListIndex, out routeReason);

            bool resolvable =
                route == RecordingsTableUI.UnfinishedFlightRewindRoute.Resolved
                && rp != null && slotListIndex >= 0 && rp.ChildSlots != null
                && slotListIndex < rp.ChildSlots.Count;

            if (!resolvable)
            {
                GUI.enabled = false;
                GUILayout.Button(
                    new GUIContent("Fly", routeReason ?? "Re-Fly unavailable"),
                    GUILayout.Width(width));
                GUI.enabled = true;
                return;
            }

            string reason;
            bool canInvoke = RecordingsTableUI.CanInvokeRewindPointSlot(
                rp, slotListIndex, out reason);
            GUI.enabled = canInvoke;
            string tooltip = canInvoke
                ? "Re-fly this unfinished flight from the separation moment"
                : (reason ?? "Re-Fly unavailable");
            if (GUILayout.Button(new GUIContent("Fly", tooltip), GUILayout.Width(width)))
            {
                ParsekLog.Info("UI",
                    $"Timeline Fly button clicked: \"{rec.VesselName}\" id={rec.RecordingId} " +
                    $"rp={rp.RewindPointId ?? "<no-id>"} slot={slotListIndex}");
                RewindInvoker.ShowDialog(rp, slotListIndex);
            }
            GUI.enabled = true;
        }

        internal static bool ShouldShowFastForwardButton(Recording rec, bool isFuture)
        {
            return isFuture && rec != null;
        }

        internal static bool ShouldShowRewindButton(Recording rec, bool isFuture)
        {
            return !isFuture
                && rec != null
                && !string.IsNullOrEmpty(RecordingStore.GetRewindSaveFileName(rec));
        }

        internal static bool HasActionableRewindOrFastForwardButton(
            TimelineEntry entry, Recording rec, double currentUT, bool canFastForward, bool canRewind)
        {
            if (entry == null || entry.Type != TimelineEntryType.RecordingStart)
                return false;

            bool isFuture = entry.UT > currentUT;
            return (ShouldShowFastForwardButton(rec, isFuture) && canFastForward)
                || (ShouldShowRewindButton(rec, isFuture) && canRewind);
        }

        internal static float GetRowActionButtonWidth(TimelineRowActionButtonKind actionKind)
        {
            return actionKind == TimelineRowActionButtonKind.GoTo
                ? GoToButtonWidth
                : RowActionButtonWidth;
        }

        internal static bool ShouldShowLoopToggle(Recording rec, bool isFuture, bool isUnfinishedFlight = false)
        {
            return !isFuture
                && !isUnfinishedFlight
                && rec != null
                && (rec.LoopPlayback || Recording.IsLoopableRecording(rec));
        }

        internal static bool ShouldShowWatchButton(bool inFlightMode, Recording rec)
        {
            return inFlightMode && rec != null;
        }

        internal static TimelineWatchButtonAction GetWatchButtonAction(bool isWatching)
        {
            return isWatching ? TimelineWatchButtonAction.Exit : TimelineWatchButtonAction.Enter;
        }

        internal static TimelineWatchButtonDescriptor BuildWatchButtonDescriptor(
            bool isWatching, bool hasGhost, bool sameBody, bool inRange, bool isDebris)
        {
            bool canWatch = RecordingsTableUI.IsWatchButtonEnabled(
                hasGhost, sameBody, inRange, isDebris);
            return new TimelineWatchButtonDescriptor(
                isWatching ? "W*" : "W",
                RecordingsTableUI.GetWatchButtonTooltip(
                    isWatching, hasGhost, sameBody, inRange, isDebris),
                RecordingsTableUI.ShouldEnableWatchButton(canWatch, isWatching),
                canWatch,
                GetWatchButtonAction(isWatching));
        }

        internal static void ApplyWatchButtonAction(
            TimelineWatchButtonAction watchAction,
            int recIndex,
            Action exitWatchMode,
            Action<int> enterWatchMode)
        {
            if (watchAction == TimelineWatchButtonAction.Exit)
                exitWatchMode?.Invoke();
            else
                enterWatchMode?.Invoke(recIndex);
        }

        internal static Dictionary<string, int> BuildRecordingIndexLookup(IReadOnlyList<Recording> recordings)
        {
            var result = new Dictionary<string, int>(recordings != null ? recordings.Count : 0);
            if (recordings == null)
                return result;

            for (int i = 0; i < recordings.Count; i++)
            {
                var recording = recordings[i];
                if (recording != null && !string.IsNullOrEmpty(recording.RecordingId))
                    result[recording.RecordingId] = i;
            }

            return result;
        }

        private bool CanFastForwardAtCurrentUT(Recording rec, double currentUT)
        {
            string unusedReason;
            return CanFastForwardAtCurrentUT(rec, currentUT, out unusedReason);
        }

        private bool CanFastForwardNow(Recording rec, out string reason)
        {
            double currentUT = 0;
            try { currentUT = Planetarium.GetUniversalTime(); } catch { }
            return CanFastForwardAtCurrentUT(rec, currentUT, out reason);
        }

        private bool CanFastForwardAtCurrentUT(Recording rec, double currentUT, out string reason)
        {
            bool isRecording = parentUI.InFlightMode && parentUI.Flight != null && parentUI.Flight.IsRecording;
            return RecordingStore.CanFastForwardAtUT(rec, currentUT, out reason, isRecording: isRecording);
        }

        private bool CanRewindWithResolvedSaveState(Recording rec)
        {
            string unusedReason;
            return CanRewindWithResolvedSaveState(rec, out unusedReason);
        }

        private bool CanRewindWithResolvedSaveState(Recording rec, out string reason)
        {
            bool isRecording = parentUI.InFlightMode && parentUI.Flight != null && parentUI.Flight.IsRecording;
            string resolvedSave = RecordingStore.GetRewindSaveFileName(rec);
            bool saveExists = DoesRewindSaveExist(rec, resolvedSave);
            return RecordingStore.CanRewindWithResolvedSaveState(
                resolvedSave, saveExists, out reason, isRecording: isRecording);
        }

        private bool DoesRewindSaveExist(Recording rec, string resolvedSave)
        {
            if (string.IsNullOrEmpty(resolvedSave))
                return false;

            string recordingId = rec?.RecordingId;
            if (string.IsNullOrEmpty(recordingId))
                return DoesRewindSaveExistUncached(resolvedSave);

            if (rewindSaveExistsByRecordingId == null)
                rewindSaveExistsByRecordingId = new Dictionary<string, bool>(StringComparer.Ordinal);

            bool saveExists;
            if (rewindSaveExistsByRecordingId.TryGetValue(recordingId, out saveExists))
                return saveExists;

            saveExists = DoesRewindSaveExistUncached(resolvedSave);
            rewindSaveExistsByRecordingId[recordingId] = saveExists;
            return saveExists;
        }

        private static bool DoesRewindSaveExistUncached(string resolvedSave)
        {
            string savePath = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildRewindSaveRelativePath(resolvedSave));
            return !string.IsNullOrEmpty(savePath) && System.IO.File.Exists(savePath);
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

        private int FindRecordingIndexById(string recordingId)
        {
            if (recordingIndexById == null || string.IsNullOrEmpty(recordingId))
                return -1;
            int index;
            return recordingIndexById.TryGetValue(recordingId, out index) ? index : -1;
        }

        private void DrawResourceBudget()
        {
            Game.Modes? currentMode = GetCurrentGameMode();
            if (currentMode == Game.Modes.SANDBOX)
                return;

            var budget = parentUI.GetCachedBudget();

            if (budget.reservedFunds <= 0 && budget.reservedScience <= 0 && budget.reservedReputation <= 0)
                return;

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            GUILayout.Space(5);
            GUILayout.Label("Resources", parentUI.GetSectionHeaderStyle());

            bool anyOverCommitted = false;
            bool isScienceMode = currentMode == Game.Modes.SCIENCE_SANDBOX;

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
