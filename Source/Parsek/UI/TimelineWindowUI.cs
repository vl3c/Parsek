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

        private int lastRetiredKerbalCount = -1;

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

            // Position to the left of main window on first open
            if (timelineWindowRect.width < 1f)
            {
                float x = mainWindowRect.x - 538;
                if (x < 0) x = mainWindowRect.x + mainWindowRect.width + 10;
                float height = Math.Max(600f, mainWindowRect.height);
                timelineWindowRect = new Rect(x, mainWindowRect.y, 528, height);
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
                "Parsek \u2014 Timeline",
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
        /// Marks the cached timeline as stale so it rebuilds on next draw.
        /// Called by ParsekUI on cache invalidation triggers.
        /// </summary>
        public void InvalidateCache()
        {
            timelineDirty = true;
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
            // but tints it darker via the on-state background color trick
            toggleButtonStyle = new GUIStyle(GUI.skin.button);
            // Copy the normal (off) background to the on states so we keep rounded corners
            toggleButtonStyle.onNormal.background = GUI.skin.button.active.background;
            toggleButtonStyle.onHover.background = GUI.skin.button.active.background;
            toggleButtonStyle.onNormal.textColor = Color.white;
            toggleButtonStyle.onHover.textColor = Color.white;
        }

        private void DrawTimelineWindow(int windowID)
        {
            EnsureStyles();

            // Zone 1: Resource Budget
            DrawResourceBudget();

            // Zone 2: Filter Bar
            DrawFilterBar();

            // Rebuild cache if dirty
            if (timelineDirty || cachedTimeline == null)
            {
                cachedTimeline = TimelineBuilder.Build(
                    RecordingStore.CommittedRecordings,
                    Ledger.Actions,
                    MilestoneStore.Milestones,
                    MilestoneStore.CurrentEpoch);
                timelineDirty = false;
                ParsekLog.Verbose("Timeline", $"Cache rebuilt: {cachedTimeline.Count} entries");
            }

            // Zone 3: Entry List
            DrawEntryList();

            // Zone 4: Footer
            DrawRetiredKerbalsSection();

            GUILayout.FlexibleSpace();

            // Stats footer — count actions vs events from cached timeline
            int recCount = RecordingStore.CommittedRecordings.Count;
            uint epoch = MilestoneStore.CurrentEpoch;
            int playerActionCount = 0;
            int eventCount = 0;
            if (cachedTimeline != null)
            {
                for (int i = 0; i < cachedTimeline.Count; i++)
                {
                    if (cachedTimeline[i].Source == TimelineSource.Recording) continue;
                    if (cachedTimeline[i].IsPlayerAction) playerActionCount++;
                    else eventCount++;
                }
            }

            var stats = new System.Text.StringBuilder();
            stats.Append($"{recCount} Recording{(recCount == 1 ? "" : "s")}");
            if (epoch > 0)
                stats.Append($" ({(int)epoch} Revert{(epoch == 1 ? "" : "s")})");
            stats.Append($", {playerActionCount} Action{(playerActionCount == 1 ? "" : "s")}");
            stats.Append($", {eventCount} Event{(eventCount == 1 ? "" : "s")}");
            GUILayout.Label(stats.ToString(), timelineGrayStyle);

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
            GUILayout.BeginHorizontal();

            // Tier selector
            bool overviewActive = !showDetail;
            bool detailActive = showDetail;

            if (GUILayout.Toggle(overviewActive, "Overview", toggleButtonStyle, GUILayout.Width(80)) && !overviewActive)
            {
                showDetail = false;
                ParsekLog.Verbose("UI", "Timeline filter: Overview");
            }
            if (GUILayout.Toggle(detailActive, "Detail", toggleButtonStyle, GUILayout.Width(70)) && !detailActive)
            {
                showDetail = true;
                ParsekLog.Verbose("UI", "Timeline filter: Detail");
            }

            GUILayout.Space(10);

            // Source toggles
            bool newShowRec = GUILayout.Toggle(showRecordingEntries, "Recordings", toggleButtonStyle, GUILayout.Width(90));
            if (newShowRec != showRecordingEntries)
            {
                showRecordingEntries = newShowRec;
                ParsekLog.Verbose("UI", $"Timeline source toggle: Recordings={showRecordingEntries}");
            }

            bool newShowAct = GUILayout.Toggle(showActionEntries, "Actions", toggleButtonStyle, GUILayout.Width(70));
            if (newShowAct != showActionEntries)
            {
                showActionEntries = newShowAct;
                ParsekLog.Verbose("UI", $"Timeline source toggle: Actions={showActionEntries}");
            }

            bool newShowEvt = GUILayout.Toggle(showEventEntries, "Events", toggleButtonStyle, GUILayout.Width(65));
            if (newShowEvt != showEventEntries)
            {
                showEventEntries = newShowEvt;
                ParsekLog.Verbose("UI", $"Timeline source toggle: Events={showEventEntries}");
            }

            GUILayout.EndHorizontal();
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
                pendingScrollToRecordingId = null;
            }

            timelineScrollPos = GUILayout.BeginScrollView(timelineScrollPos, GUILayout.ExpandHeight(true));

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
                    else if (!string.IsNullOrEmpty(rec.RewindSaveFileName))
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

        private GUIStyle GetStyleForColor(Color color)
        {
            if (color.g > 0.9f && color.r < 0.6f) return timelineGreenStyle;
            if (color.r > 0.9f && color.g < 0.6f) return timelineRedStyle;
            return timelineWhiteStyle;
        }

        private static Recording FindRecordingById(string recordingId)
        {
            var recordings = RecordingStore.CommittedRecordings;
            for (int i = 0; i < recordings.Count; i++)
            {
                if (recordings[i].RecordingId == recordingId)
                    return recordings[i];
            }
            return null;
        }

        private void DrawRetiredKerbalsSection()
        {
            var retiredKerbals = LedgerOrchestrator.Kerbals?.GetRetiredKerbals() ?? new List<string>();
            if (retiredKerbals.Count > 0)
            {
                if (retiredKerbals.Count != lastRetiredKerbalCount)
                {
                    ParsekLog.Verbose("UI",
                        $"Retired kerbals count changed: {lastRetiredKerbalCount} -> {retiredKerbals.Count}");
                    lastRetiredKerbalCount = retiredKerbals.Count;
                }

                GUILayout.Space(5);
                GUILayout.Label($"Retired Stand-ins ({retiredKerbals.Count})", GUI.skin.box);
                GUILayout.BeginVertical(GUI.skin.box);
                for (int i = 0; i < retiredKerbals.Count; i++)
                {
                    GUILayout.Label(retiredKerbals[i], timelineGrayStyle);
                }
                GUILayout.EndVertical();
            }
            else if (lastRetiredKerbalCount > 0)
            {
                ParsekLog.Verbose("UI", "Retired kerbals list cleared");
                lastRetiredKerbalCount = 0;
            }
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
            GUILayout.Label("Resources", GUI.skin.box);

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
