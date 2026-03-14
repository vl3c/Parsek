using System.Collections.Generic;
using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// UI rendering for the Parsek window and map view markers.
    /// Receives a ParsekFlight reference for accessing flight state.
    /// </summary>
    public class ParsekUI
    {
        public enum UIMode { Flight, KSC }
        private readonly UIMode mode;
        private bool InFlight => mode == UIMode.Flight;

        private readonly ParsekFlight flight;

        // Map view markers
        private GUIStyle mapMarkerStyle;
        private Texture2D mapMarkerTexture;

        // Recordings window
        private bool showRecordingsWindow;
        private Rect recordingsWindowRect;
        private Vector2 recordingsScrollPos;
        private int deleteConfirmIndex = -1;
        private bool isResizingRecordingsWindow;
        private bool recordingsWindowHasInputLock;
        private const string RecordingsInputLockId = "Parsek_RecordingsWindow";
        private const float ResizeHandleSize = 16f;
        private const float MinWindowWidth = 350f;
        private const float MinWindowHeight = 150f;

        // Settings window
        private bool showSettingsWindow;
        private Rect settingsWindowRect;
        private bool settingsWindowHasInputLock;
        private const string SettingsInputLockId = "Parsek_SettingsWindow";

        // Actions window
        private bool showActionsWindow;
        private Rect actionsWindowRect;
        private Vector2 actionsScrollPos;
        private bool isResizingActionsWindow;
        private bool actionsWindowHasInputLock;
        private const string ActionsInputLockId = "Parsek_ActionsWindow";

        // Cached styles for actions window
        private GUIStyle actionsGrayStyle;
        private GUIStyle actionsWhiteStyle;

        // Actions table sort state
        private enum ActionsSortColumn { Time, Type, Description, Status }
        private ActionsSortColumn actionsSortColumn = ActionsSortColumn.Time;
        private bool actionsSortAscending = true;

        // Column widths — shared between header and body for alignment
        private const float ColW_Enable = 15f;
        private const float ColW_Phase = 70f;
        private const float ColW_Index = 25f;
        private const float ColW_Launch = 110f;
        private const float ColW_Dur = 55f;
        private const float ColW_Status = 45f;
        private const float ColW_Loop = 55f;
        private const float ColW_Watch = 50f;
        private const float ColW_Rewind = 55f;
        private const float ColW_Delete = 50f;
        private const float ScrollbarWidth = 16f;

        // Chain grouping state
        private HashSet<string> expandedChains = new HashSet<string>();
        private HashSet<string> expandedGroups = new HashSet<string>();

        // Cached phase label styles
        private GUIStyle phaseStyleAtmo;
        private GUIStyle phaseStyleExo;
        private GUIStyle phaseStyleSpace;

        // Sort state
        internal enum SortColumn { Index, Name, LaunchTime, Duration, Status }
        private SortColumn sortColumn = SortColumn.Index;
        private bool sortAscending = true;
        private int[] sortedIndices; // maps display row → CommittedRecordings index
        private int lastSortedCount = -1;

        // Tooltip state
        private int hoveredRecIdx = -1;
        private GUIStyle tooltipLabelStyle;
        private Rect scrollViewRect;

        // Expanded stats columns
        private bool showExpandedStats;
        private const float ColW_MaxAlt = 65f;
        private const float ColW_MaxSpd = 65f;
        private const float ColW_Dist = 65f;
        private const float ColW_Pts = 35f;

        // Loop period editing
        private int editingLoopPeriodIdx = -1;
        private string editingLoopPeriodText = "";
        private const float ColW_Period = 55f;

        // Cached styles for status labels
        private GUIStyle statusStyleFuture;
        private GUIStyle statusStyleActive;
        private GUIStyle statusStylePast;

        // Window drag tracking for position logging
        private Rect lastMainWindowRect;
        private Rect lastRecordingsWindowRect;
        private Rect lastActionsWindowRect;
        private Rect lastSettingsWindowRect;

        public ParsekUI(ParsekFlight flight)
        {
            this.flight = flight;
            this.mode = UIMode.Flight;
        }

        public ParsekUI(UIMode mode)
        {
            this.flight = null;
            this.mode = mode;
        }

        private const float SpacingSmall = 3f;
        private const float SpacingLarge = 10f;

        public void DrawWindow(int windowID)
        {
            GUILayout.BeginVertical();

            if (InFlight)
                DrawFlightStatus();

            DrawCompactBudgetLine();

            // --- Timeline buttons ---
            GUILayout.Space(SpacingLarge);

            int committedCount = RecordingStore.CommittedRecordings.Count;
            if (GUILayout.Button($"Recordings ({committedCount})"))
            {
                showRecordingsWindow = !showRecordingsWindow;
                if (!showRecordingsWindow)
                    deleteConfirmIndex = -1;
                ParsekLog.Verbose("UI", $"Recordings window toggled: {(showRecordingsWindow ? "open" : "closed")}");
            }

            int actionCount = MilestoneStore.GetPendingEventCount() + GameStateStore.GetUncommittedEventCount();
            if (GUILayout.Button($"Game Actions ({actionCount})"))
            {
                showActionsWindow = !showActionsWindow;
                ParsekLog.Verbose("UI", $"Actions window toggled: {(showActionsWindow ? "open" : "closed")}");
            }

            if (InFlight)
                DrawFlightRecordingControls();

            // --- Settings button ---
            GUILayout.Space(SpacingLarge);
            if (GUILayout.Button("Settings"))
            {
                showSettingsWindow = !showSettingsWindow;
                ParsekLog.Verbose("UI", $"Settings window toggled: {(showSettingsWindow ? "open" : "closed")}");
            }

            GUILayout.EndVertical();

            // Make window draggable
            GUI.DragWindow();
        }

        private void DrawFlightStatus()
        {
            GUILayout.Label("Status", GUI.skin.box);
            GUILayout.Label($"State: {GetStatusText()}");
            GUILayout.Label($"Recorded Points: {flight.recording.Count}");
            if (flight.recording.Count > 0)
            {
                double duration = flight.recording[flight.recording.Count - 1].ut - flight.recording[0].ut;
                GUILayout.Label($"Duration: {duration:F1}s");
            }
            GUILayout.Label($"Active Ghosts: {flight.TimelineGhostCount}");
        }

        private void DrawFlightRecordingControls()
        {
            GUILayout.Space(SpacingLarge);

            if (!flight.IsRecording)
            {
                if (GUILayout.Button("Start Recording"))
                {
                    ParsekLog.Verbose("UI", "Start Recording button clicked");
                    flight.StartRecording();
                }
            }
            else
            {
                if (GUILayout.Button("Stop Recording"))
                {
                    ParsekLog.Verbose("UI", "Stop Recording button clicked");
                    flight.StopRecording();
                }
            }

            if (!flight.IsPlaying)
            {
                GUI.enabled = !flight.IsRecording && flight.recording.Count > 0;
                if (GUILayout.Button("Preview Playback"))
                {
                    ParsekLog.Verbose("UI", "Preview Playback button clicked");
                    flight.StartPlayback();
                }
                GUI.enabled = true;
            }
            else
            {
                if (GUILayout.Button("Stop Preview"))
                {
                    ParsekLog.Verbose("UI", "Stop Preview button clicked");
                    flight.StopPlayback();
                }
            }

            GUI.enabled = !flight.IsRecording && !flight.IsPlaying && flight.recording.Count > 0;
            if (GUILayout.Button("Clear Current Recording"))
            {
                ParsekLog.Verbose("UI", "Clear Current Recording button clicked");
                ShowClearRecordingConfirmation();
            }

            bool canCommitStandalone = !flight.IsRecording && !flight.IsPlaying
                && flight.recording.Count >= 2 && !flight.HasActiveChain && !flight.HasActiveTree;
            bool canCommitTree = flight.HasActiveTree;
            bool stableSituation = FlightGlobals.ActiveVessel != null
                && RecordingStore.IsStableState((int)FlightGlobals.ActiveVessel.situation);
            GUI.enabled = (canCommitStandalone || canCommitTree) && stableSituation;
            if (GUILayout.Button(stableSituation
                ? new GUIContent("Commit Recording to Timeline")
                : new GUIContent("Commit Recording to Timeline", "Land or stop before committing.")))
            {
                ParsekLog.Verbose("UI", "Commit Flight button clicked");
                if (flight.HasActiveTree)
                    flight.CommitTreeFlight();
                else
                    flight.CommitFlight();
            }
            GUI.enabled = true;
        }

        /// <summary>
        /// Call after each window's GUILayoutWindow to log position/size changes (rate-limited).
        /// </summary>
        private void LogWindowPosition(string windowName, ref Rect lastRect, Rect currentRect)
        {
            if (lastRect.x != currentRect.x || lastRect.y != currentRect.y ||
                lastRect.width != currentRect.width || lastRect.height != currentRect.height)
            {
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.VerboseRateLimited("UI", $"window.{windowName}",
                    $"{windowName} window position: x={currentRect.x.ToString("F0", ic)} y={currentRect.y.ToString("F0", ic)} " +
                    $"w={currentRect.width.ToString("F0", ic)} h={currentRect.height.ToString("F0", ic)}", 2.0);
                lastRect = currentRect;
            }
        }

        private void DrawResourceBudget()
        {
            var budget = ResourceBudget.ComputeTotal(
                RecordingStore.CommittedRecordings,
                MilestoneStore.Milestones,
                RecordingStore.CommittedTrees);

            if (budget.reservedFunds <= 0 && budget.reservedScience <= 0 && budget.reservedReputation <= 0)
                return;

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            GUILayout.Space(5);
            GUILayout.Label("Resources", GUI.skin.box);

            bool anyOverCommitted = false;

            if (budget.reservedFunds > 0)
            {
                double currentFunds = 0;
                try { if (Funding.Instance != null) currentFunds = Funding.Instance.Funds; } catch { }
                double available = currentFunds - budget.reservedFunds;
                double total = currentFunds;
                bool over = available < 0;
                if (over) anyOverCommitted = true;
                Color prev = GUI.contentColor;
                if (over) GUI.contentColor = Color.red;
                GUILayout.Label($"Funds: {available.ToString("N0", ic)} available to use ({budget.reservedFunds.ToString("N0", ic)} committed out of {total.ToString("N0", ic)} total)");
                GUI.contentColor = prev;
            }

            if (budget.reservedScience > 0)
            {
                double currentScience = 0;
                try { if (ResearchAndDevelopment.Instance != null) currentScience = ResearchAndDevelopment.Instance.Science; } catch { }
                double available = currentScience - budget.reservedScience;
                double total = currentScience;
                bool over = available < 0;
                if (over) anyOverCommitted = true;
                Color prev = GUI.contentColor;
                if (over) GUI.contentColor = Color.red;
                GUILayout.Label($"Science: {available.ToString("F1", ic)} available to use ({budget.reservedScience.ToString("F1", ic)} committed out of {total.ToString("F1", ic)} total)");
                GUI.contentColor = prev;
            }

            if (budget.reservedReputation > 0)
            {
                float currentRep = 0;
                try { if (Reputation.Instance != null) currentRep = Reputation.Instance.reputation; } catch { }
                double available = currentRep - budget.reservedReputation;
                double total = (double)currentRep;
                bool over = available < 0;
                if (over) anyOverCommitted = true;
                Color prev = GUI.contentColor;
                if (over) GUI.contentColor = Color.red;
                GUILayout.Label($"Reputation: {available.ToString("F0", ic)} available to use ({budget.reservedReputation.ToString("F0", ic)} committed out of {total.ToString("F0", ic)} total)");
                GUI.contentColor = prev;
            }

            if (anyOverCommitted)
            {
                Color prev = GUI.contentColor;
                GUI.contentColor = Color.yellow;
                GUILayout.Label("Over-committed! Some timeline actions may fail.");
                GUI.contentColor = prev;
            }
        }

        public void LogMainWindowPosition(Rect currentRect)
        {
            LogWindowPosition("Main", ref lastMainWindowRect, currentRect);
        }

        private void DrawCompactBudgetLine()
        {
            var budget = ResourceBudget.ComputeTotal(
                RecordingStore.CommittedRecordings,
                MilestoneStore.Milestones,
                RecordingStore.CommittedTrees);

            if (budget.reservedFunds <= 0 && budget.reservedScience <= 0 && budget.reservedReputation <= 0)
                return;

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var parts = new List<string>();
            if (budget.reservedFunds > 0)
                parts.Add(budget.reservedFunds.ToString("N0", ic) + " funds");
            if (budget.reservedScience > 0)
                parts.Add(budget.reservedScience.ToString("F1", ic) + " science");
            if (budget.reservedReputation > 0)
                parts.Add(budget.reservedReputation.ToString("F0", ic) + " reputation");

            if (parts.Count > 0)
            {
                GUILayout.Label("Reserved:");
                for (int i = 0; i < parts.Count; i++)
                    GUILayout.Label("  \u2022 " + parts[i]);
            }
        }

        public void DrawActionsWindowIfOpen(Rect mainWindowRect)
        {
            if (!showActionsWindow)
            {
                ReleaseActionsInputLock();
                return;
            }

            // Position to the left of main window on first open
            if (actionsWindowRect.width < 1f)
            {
                float x = mainWindowRect.x - 538;
                if (x < 0) x = mainWindowRect.x + mainWindowRect.width + 10;
                actionsWindowRect = new Rect(x, mainWindowRect.y, 528, mainWindowRect.height);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI", $"Actions window initial position: x={x.ToString("F0", ic)} y={mainWindowRect.y.ToString("F0", ic)} (mainWindow.x={mainWindowRect.x.ToString("F0", ic)})");
            }

            // Handle resize drag
            if (isResizingActionsWindow)
            {
                if (Event.current.type == EventType.MouseDrag || Event.current.type == EventType.MouseUp)
                {
                    float newW = Mathf.Max(MinWindowWidth, Event.current.mousePosition.x - actionsWindowRect.x);
                    float newH = Mathf.Max(MinWindowHeight, Event.current.mousePosition.y - actionsWindowRect.y);
                    actionsWindowRect.width = newW;
                    actionsWindowRect.height = newH;
                }
                if (Event.current.type == EventType.MouseUp)
                {
                    isResizingActionsWindow = false;
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    ParsekLog.Verbose("UI", $"Actions window resize ended: w={actionsWindowRect.width.ToString("F0", ic)} h={actionsWindowRect.height.ToString("F0", ic)}");
                }
                if (Event.current.type == EventType.MouseDrag)
                    Event.current.Use();
            }

            actionsWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekActions".GetHashCode(),
                actionsWindowRect,
                DrawActionsWindow,
                "Parsek \u2014 Game Actions",
                GUILayout.Width(actionsWindowRect.width),
                GUILayout.Height(actionsWindowRect.height)
            );
            LogWindowPosition("Actions", ref lastActionsWindowRect, actionsWindowRect);

            if (actionsWindowRect.Contains(Event.current.mousePosition))
            {
                if (!actionsWindowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, ActionsInputLockId);
                    actionsWindowHasInputLock = true;
                }
            }
            else
            {
                ReleaseActionsInputLock();
            }
        }

        private void ReleaseActionsInputLock()
        {
            if (!actionsWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(ActionsInputLockId);
            actionsWindowHasInputLock = false;
        }

        private void EnsureActionsStyles()
        {
            if (actionsGrayStyle != null) return;

            actionsGrayStyle = new GUIStyle(GUI.skin.label);
            actionsGrayStyle.normal.textColor = Color.gray;

            actionsWhiteStyle = new GUIStyle(GUI.skin.label);
            actionsWhiteStyle.normal.textColor = Color.white;
        }

        private void DrawActionsWindow(int windowID)
        {
            EnsureActionsStyles();

            // A. Resource Budget Summary
            DrawResourceBudget();

            // B. Recorded Actions List
            var milestones = MilestoneStore.Milestones;
            uint currentEpoch = MilestoneStore.CurrentEpoch;
            // event, isReplayed, isCommitted (true=milestone, false=uncommitted)
            var allEvents = new List<System.Tuple<GameStateEvent, bool>>();
            for (int i = 0; i < milestones.Count; i++)
            {
                var m = milestones[i];
                if (!m.Committed || m.Epoch != currentEpoch) continue;
                for (int j = 0; j < m.Events.Count; j++)
                {
                    if (GameStateStore.IsMilestoneFilteredEvent(m.Events[j].eventType))
                        continue;
                    bool replayed = j <= m.LastReplayedEventIndex;
                    allEvents.Add(System.Tuple.Create(m.Events[j], replayed));
                }
            }

            // Sort events
            switch (actionsSortColumn)
            {
                case ActionsSortColumn.Time:
                    allEvents.Sort((a, b) => actionsSortAscending
                        ? a.Item1.ut.CompareTo(b.Item1.ut)
                        : b.Item1.ut.CompareTo(a.Item1.ut));
                    break;
                case ActionsSortColumn.Type:
                    allEvents.Sort((a, b) =>
                    {
                        int cmp = string.Compare(
                            GameStateEventDisplay.GetDisplayCategory(a.Item1.eventType),
                            GameStateEventDisplay.GetDisplayCategory(b.Item1.eventType),
                            System.StringComparison.Ordinal);
                        return actionsSortAscending ? cmp : -cmp;
                    });
                    break;
                case ActionsSortColumn.Description:
                    allEvents.Sort((a, b) =>
                    {
                        int cmp = string.Compare(
                            GameStateEventDisplay.GetDisplayDescription(a.Item1),
                            GameStateEventDisplay.GetDisplayDescription(b.Item1),
                            System.StringComparison.Ordinal);
                        return actionsSortAscending ? cmp : -cmp;
                    });
                    break;
                case ActionsSortColumn.Status:
                    allEvents.Sort((a, b) =>
                    {
                        int cmp = a.Item2.CompareTo(b.Item2); // false (Pending) < true (Replayed)
                        return actionsSortAscending ? cmp : -cmp;
                    });
                    break;
            }

            // C. Uncommitted events (not yet in any milestone)
            double lastMilestoneEndUT = 0;
            for (int i = 0; i < milestones.Count; i++)
            {
                if (milestones[i].Epoch == currentEpoch && milestones[i].EndUT > lastMilestoneEndUT)
                    lastMilestoneEndUT = milestones[i].EndUT;
            }

            var storeEvents = GameStateStore.Events;
            var uncommittedEvents = new List<GameStateEvent>();
            for (int i = 0; i < storeEvents.Count; i++)
            {
                var e = storeEvents[i];
                if (e.epoch != currentEpoch) continue;
                if (e.ut <= lastMilestoneEndUT) continue;
                if (GameStateStore.IsMilestoneFilteredEvent(e.eventType)) continue;
                uncommittedEvents.Add(e);
            }
            uncommittedEvents.Sort((a, b) => a.ut.CompareTo(b.ut));

            // Single scroll view for both sections
            bool hasCommitted = allEvents.Count > 0;
            bool hasUncommitted = uncommittedEvents.Count > 0;

            if (hasCommitted || hasUncommitted)
            {
                GUILayout.Space(5);

                // Column headers (clickable to sort)
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(SortHeader("Time", ActionsSortColumn.Time), GUI.skin.label, GUILayout.Width(90)))
                    ToggleActionsSort(ActionsSortColumn.Time);
                if (GUILayout.Button(SortHeader("Type", ActionsSortColumn.Type), GUI.skin.label, GUILayout.Width(65)))
                    ToggleActionsSort(ActionsSortColumn.Type);
                if (GUILayout.Button(SortHeader("Description", ActionsSortColumn.Description), GUI.skin.label, GUILayout.ExpandWidth(true)))
                    ToggleActionsSort(ActionsSortColumn.Description);
                if (GUILayout.Button(SortHeader("Status", ActionsSortColumn.Status), GUI.skin.label, GUILayout.Width(55)))
                    ToggleActionsSort(ActionsSortColumn.Status);
                GUILayout.Space(25); // space for delete column
                GUILayout.EndHorizontal();

                GameStateEvent? eventToDeleteCommitted = null;
                GameStateEvent? eventToDeleteUncommitted = null;

                actionsScrollPos = GUILayout.BeginScrollView(actionsScrollPos, GUILayout.ExpandHeight(true));

                if (hasCommitted)
                {
                    GUILayout.Label("Recorded Actions", GUI.skin.box);

                    for (int i = 0; i < allEvents.Count; i++)
                    {
                        var e = allEvents[i].Item1;
                        bool replayed = allEvents[i].Item2;
                        GUIStyle style = replayed ? actionsGrayStyle : actionsWhiteStyle;

                        GUILayout.BeginHorizontal();

                        string time = KSPUtil.PrintDateCompact(e.ut, true);
                        GUILayout.Label(time, style, GUILayout.Width(90));

                        string category = GameStateEventDisplay.GetDisplayCategory(e.eventType);
                        GUILayout.Label(category, style, GUILayout.Width(65));

                        string desc = GameStateEventDisplay.GetDisplayDescription(e);
                        GUILayout.Label(desc, style, GUILayout.ExpandWidth(true));

                        string status = replayed ? "Replayed" : "Pending";
                        GUILayout.Label(status, style, GUILayout.Width(55));

                        if (GUILayout.Button("x", GUILayout.Width(20)))
                            eventToDeleteCommitted = e;

                        GUILayout.EndHorizontal();
                    }
                }

                if (hasUncommitted)
                {
                    if (hasCommitted)
                        GUILayout.Space(5);
                    GUILayout.Label("Uncommitted", GUI.skin.box);

                    for (int i = 0; i < uncommittedEvents.Count; i++)
                    {
                        var e = uncommittedEvents[i];

                        GUILayout.BeginHorizontal();

                        string time = KSPUtil.PrintDateCompact(e.ut, true);
                        GUILayout.Label(time, actionsWhiteStyle, GUILayout.Width(90));

                        string category = GameStateEventDisplay.GetDisplayCategory(e.eventType);
                        GUILayout.Label(category, actionsWhiteStyle, GUILayout.Width(65));

                        string desc = GameStateEventDisplay.GetDisplayDescription(e);
                        GUILayout.Label(desc, actionsWhiteStyle, GUILayout.ExpandWidth(true));

                        GUILayout.Label("\u2014", actionsGrayStyle, GUILayout.Width(55)); // em dash

                        if (GUILayout.Button("x", GUILayout.Width(20)))
                            eventToDeleteUncommitted = e;

                        GUILayout.EndHorizontal();
                    }
                }

                GUILayout.EndScrollView();

                // Process deletions outside the iteration loop
                if (eventToDeleteCommitted.HasValue)
                {
                    var del = eventToDeleteCommitted.Value;
                    ParsekLog.Verbose("UI", $"Delete committed action: {del.eventType} key='{del.key}' ut={del.ut:F1}");
                    MilestoneStore.RemoveCommittedEvent(del);
                }
                if (eventToDeleteUncommitted.HasValue)
                {
                    var del = eventToDeleteUncommitted.Value;
                    ParsekLog.Verbose("UI", $"Delete uncommitted action: {del.eventType} key='{del.key}' ut={del.ut:F1}");
                    GameStateStore.RemoveEvent(del);
                }
            }
            else
            {
                GUILayout.Space(5);
                GUILayout.Label("No actions recorded.");
            }

            // C. Bottom Bar
            GUILayout.Space(5);

            uint epoch = MilestoneStore.CurrentEpoch;
            if (epoch > 0)
            {
                GUILayout.Label($"Epoch: {epoch} ({epoch} revert{(epoch == 1 ? "" : "s")})",
                    actionsGrayStyle);
            }

            if (GUILayout.Button("Close"))
            {
                showActionsWindow = false;
                ParsekLog.Verbose("UI", "Actions window closed via button");
            }

            // Resize handle (bottom-right corner)
            Rect handleRect = new Rect(
                actionsWindowRect.width - ResizeHandleSize,
                actionsWindowRect.height - ResizeHandleSize,
                ResizeHandleSize, ResizeHandleSize);
            GUI.Label(handleRect, "\u25e2"); // triangle
            if (Event.current.type == EventType.MouseDown && handleRect.Contains(Event.current.mousePosition))
            {
                isResizingActionsWindow = true;
                ParsekLog.Verbose("UI", "Actions window resize started");
                Event.current.Use();
            }

            GUI.DragWindow();
        }

        private string SortHeader(string label, ActionsSortColumn column)
        {
            if (actionsSortColumn == column)
                return label + (actionsSortAscending ? " \u25b2" : " \u25bc"); // ▲ / ▼
            return label;
        }

        private void ToggleActionsSort(ActionsSortColumn column)
        {
            if (actionsSortColumn == column)
                actionsSortAscending = !actionsSortAscending;
            else
            {
                actionsSortColumn = column;
                actionsSortAscending = true;
            }
            ParsekLog.Verbose("UI", $"Actions sort changed: column={column} ascending={actionsSortAscending}");
        }

        public void DrawRecordingsWindowIfOpen(Rect mainWindowRect)
        {
            if (!showRecordingsWindow)
            {
                ReleaseRecordingsInputLock();
                return;
            }

            // Position to the right of main window on first open
            if (recordingsWindowRect.width < 1f)
            {
                float recHeight = InFlight ? mainWindowRect.height : mainWindowRect.height * 2;
                recordingsWindowRect = new Rect(
                    mainWindowRect.x + mainWindowRect.width + 10,
                    mainWindowRect.y,
                    790, recHeight);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI", $"Recordings window initial position: x={recordingsWindowRect.x.ToString("F0", ic)} y={recordingsWindowRect.y.ToString("F0", ic)}");
            }

            // Handle resize drag (must be outside the window function to track across frames)
            if (isResizingRecordingsWindow)
            {
                if (Event.current.type == EventType.MouseDrag || Event.current.type == EventType.MouseUp)
                {
                    float newW = Mathf.Max(MinWindowWidth, Event.current.mousePosition.x - recordingsWindowRect.x);
                    float newH = Mathf.Max(MinWindowHeight, Event.current.mousePosition.y - recordingsWindowRect.y);
                    recordingsWindowRect.width = newW;
                    recordingsWindowRect.height = newH;
                }
                if (Event.current.type == EventType.MouseUp)
                {
                    isResizingRecordingsWindow = false;
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    ParsekLog.Verbose("UI", $"Recordings window resize ended: w={recordingsWindowRect.width.ToString("F0", ic)} h={recordingsWindowRect.height.ToString("F0", ic)}");
                }
                if (Event.current.type == EventType.MouseDrag)
                    Event.current.Use();
            }

            recordingsWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekRecordings".GetHashCode(),
                recordingsWindowRect,
                DrawRecordingsWindow,
                "Parsek \u2014 Recordings",
                GUILayout.Width(recordingsWindowRect.width),
                GUILayout.Height(recordingsWindowRect.height)
            );
            LogWindowPosition("Recordings", ref lastRecordingsWindowRect, recordingsWindowRect);

            // Lock camera controls (including scroll zoom) when mouse is over window.
            // ClickThroughBlocker uses ALLBUTCAMERAS which intentionally leaves camera
            // controls unlocked. We add our own lock for CAMERACONTROLS to block scroll zoom.
            if (recordingsWindowRect.Contains(Event.current.mousePosition))
            {
                if (!recordingsWindowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, RecordingsInputLockId);
                    recordingsWindowHasInputLock = true;
                }
            }
            else
            {
                ReleaseRecordingsInputLock();
            }
        }

        private void ReleaseRecordingsInputLock()
        {
            if (!recordingsWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(RecordingsInputLockId);
            recordingsWindowHasInputLock = false;
        }

        private void EnsurePhaseStyles()
        {
            if (phaseStyleAtmo != null) return;

            phaseStyleAtmo = new GUIStyle(GUI.skin.label);
            phaseStyleAtmo.normal.textColor = new Color(1f, 0.6f, 0.2f); // orange

            phaseStyleExo = new GUIStyle(GUI.skin.label);
            phaseStyleExo.normal.textColor = new Color(0.3f, 0.8f, 1f); // cyan

            phaseStyleSpace = new GUIStyle(GUI.skin.label);
            phaseStyleSpace.normal.textColor = new Color(0.2f, 1f, 0.6f); // lime green
        }

        private void DrawRecordingsWindow(int windowID)
        {
            var committed = RecordingStore.CommittedRecordings;
            double now = Planetarium.GetUniversalTime();

            EnsureStatusStyles();
            EnsurePhaseStyles();
            RebuildSortedIndices(committed, now);

            hoveredRecIdx = -1;

            if (committed.Count == 0)
            {
                GUILayout.Label("No recordings.");
            }
            else
            {
                // Header row
                GUILayout.BeginHorizontal();
                // Select-all enable header checkbox
                int enableCount = 0;
                for (int i = 0; i < committed.Count; i++)
                    if (committed[i].PlaybackEnabled) enableCount++;
                bool allEnabled = enableCount == committed.Count;
                bool newAllEnabled = GUILayout.Toggle(allEnabled, "", GUILayout.Width(ColW_Enable));
                if (newAllEnabled != allEnabled)
                {
                    for (int i = 0; i < committed.Count; i++)
                        committed[i].PlaybackEnabled = newAllEnabled;
                    ParsekLog.Info("UI", $"Set playback enabled for all recordings: enabled={newAllEnabled}");
                }
                DrawSortableHeader("#", SortColumn.Index, ColW_Index);
                GUILayout.Label("Phase", GUILayout.Width(ColW_Phase));
                DrawSortableHeader("Name", SortColumn.Name, 0, true);
                DrawSortableHeader("Launch", SortColumn.LaunchTime, ColW_Launch);
                DrawSortableHeader("Dur", SortColumn.Duration, ColW_Dur);

                if (showExpandedStats)
                {
                    GUILayout.Label("MaxAlt", GUILayout.Width(ColW_MaxAlt));
                    GUILayout.Label("MaxSpd", GUILayout.Width(ColW_MaxSpd));
                    GUILayout.Label("Dist", GUILayout.Width(ColW_Dist));
                    GUILayout.Label("Pts", GUILayout.Width(ColW_Pts));
                }

                DrawSortableHeader("Status", SortColumn.Status, ColW_Status);

                // Select-all loop header + checkbox
                int loopCount = 0;
                for (int i = 0; i < committed.Count; i++)
                    if (committed[i].LoopPlayback) loopCount++;

                bool allLoop = loopCount == committed.Count;
                GUILayout.BeginHorizontal(GUILayout.Width(ColW_Loop));
                GUILayout.FlexibleSpace();
                GUILayout.Label("Loop");
                bool newAllLoop = GUILayout.Toggle(allLoop, "");
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                if (newAllLoop != allLoop)
                {
                    for (int i = 0; i < committed.Count; i++)
                        committed[i].LoopPlayback = newAllLoop;
                    ParsekLog.Info("UI", $"Set loop playback for all recordings: enabled={newAllLoop}");
                }

                GUILayout.BeginHorizontal(GUILayout.Width(ColW_Period));
                GUILayout.FlexibleSpace();
                GUILayout.Label(new GUIContent("Every",
                    "Loop interval (seconds):\n  Positive: wait N seconds after end\n  Zero: restart immediately\n  Negative: overlap by N seconds"));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                if (InFlight)
                    GUILayout.Label("Watch", GUILayout.Width(ColW_Watch));
                GUILayout.Label("Rewind", GUILayout.Width(ColW_Rewind));
                GUILayout.Label("Delete", GUILayout.Width(ColW_Delete));
                GUILayout.Space(ScrollbarWidth);
                GUILayout.EndHorizontal();

                // Scrollable table body (alwaysShowVertical keeps columns aligned with header)
                recordingsScrollPos = GUILayout.BeginScrollView(
                    recordingsScrollPos, false, true, GUILayout.ExpandHeight(true));

                // Rebuild if a header click invalidated during this frame
                RebuildSortedIndices(committed, now);

                // Build chain grouping: chainId → list of sorted row indices
                // Build tag grouping: recordingGroup → list of sorted row indices
                var chainRows = new Dictionary<string, List<int>>();
                var seenChains = new HashSet<string>();
                var groupRows = new Dictionary<string, List<int>>();

                for (int row = 0; row < sortedIndices.Length; row++)
                {
                    int ri = sortedIndices[row];
                    var rec = committed[ri];
                    if (!string.IsNullOrEmpty(rec.ChainId))
                    {
                        List<int> list;
                        if (!chainRows.TryGetValue(rec.ChainId, out list))
                        {
                            list = new List<int>();
                            chainRows[rec.ChainId] = list;
                        }
                        list.Add(ri);
                    }
                    else if (!string.IsNullOrEmpty(rec.RecordingGroup))
                    {
                        List<int> list;
                        if (!groupRows.TryGetValue(rec.RecordingGroup, out list))
                        {
                            list = new List<int>();
                            groupRows[rec.RecordingGroup] = list;
                        }
                        list.Add(ri);
                    }
                }

                // Draw in sorted order — standalone rows inline, chain groups
                // emitted at the position of their first sorted member
                bool deleted = false;
                var groupSet = new HashSet<int>();
                foreach (var glist in groupRows.Values)
                    foreach (var gi in glist) groupSet.Add(gi);
                var drawnGroups = new HashSet<string>();

                for (int row = 0; row < sortedIndices.Length && !deleted; row++)
                {
                    int ri = sortedIndices[row];
                    var rec = committed[ri];

                    if (groupSet.Contains(ri))
                    {
                        string grp = rec.RecordingGroup;
                        if (drawnGroups.Add(grp))
                        {
                            var members = groupRows[grp];
                            // Group header
                            GUILayout.BeginHorizontal();

                            // Group enable checkbox
                            int enabledCount = 0;
                            for (int s = 0; s < members.Count; s++)
                                if (committed[members[s]].PlaybackEnabled) enabledCount++;
                            bool grpAllEnabled = enabledCount == members.Count;
                            bool grpNewEnabled = GUILayout.Toggle(grpAllEnabled, "", GUILayout.Width(ColW_Enable));
                            if (grpNewEnabled != grpAllEnabled)
                            {
                                for (int s = 0; s < members.Count; s++)
                                    committed[members[s]].PlaybackEnabled = grpNewEnabled;
                                ParsekLog.Info("UI", $"Set playback enabled for group '{grp}': enabled={grpNewEnabled}");
                            }

                            bool expanded = expandedGroups.Contains(grp);
                            string arrow = expanded ? "\u25bc" : "\u25b6";
                            if (GUILayout.Button($"{arrow} {grp} ({members.Count})",
                                GUI.skin.label, GUILayout.ExpandWidth(true)))
                            {
                                if (expanded) expandedGroups.Remove(grp);
                                else expandedGroups.Add(grp);
                                expanded = !expanded;
                                ParsekLog.Verbose("UI", $"Group '{grp}' {(expanded ? "expanded" : "collapsed")} ({members.Count} recordings)");
                            }
                            GUILayout.EndHorizontal();

                            if (expanded)
                            {
                                for (int s = 0; s < members.Count; s++)
                                {
                                    if (DrawRecordingRow(members[s], committed, now, true))
                                    { deleted = true; break; }
                                }
                            }
                        }
                        // else: already drawn with group — skip
                    }
                    else if (string.IsNullOrEmpty(rec.ChainId))
                    {
                        // Standalone recording (non-showcase)
                        if (DrawRecordingRow(ri, committed, now, false))
                        { deleted = true; break; }
                    }
                    else if (seenChains.Add(rec.ChainId))
                    {
                        // First time seeing this chain — draw the whole group here
                        var members = chainRows[rec.ChainId];

                        // Chain header
                        GUILayout.BeginHorizontal();
                        bool expanded = expandedChains.Contains(rec.ChainId);
                        string arrow = expanded ? "\u25bc" : "\u25b6";
                        string chainName = committed[members[0]].VesselName;
                        if (string.IsNullOrEmpty(chainName)) chainName = "Chain";

                        // Aggregate duration
                        double chainStart = double.MaxValue, chainEnd = double.MinValue;
                        for (int m = 0; m < members.Count; m++)
                        {
                            var mr = committed[members[m]];
                            if (mr.StartUT < chainStart) chainStart = mr.StartUT;
                            if (mr.EndUT > chainEnd) chainEnd = mr.EndUT;
                        }

                        if (GUILayout.Button($"{arrow} {chainName} ({members.Count} segments, {FormatDuration(chainEnd - chainStart)})",
                            GUI.skin.label, GUILayout.ExpandWidth(true)))
                        {
                            if (expanded) expandedChains.Remove(rec.ChainId);
                            else expandedChains.Add(rec.ChainId);
                            ParsekLog.Verbose("UI", $"Chain '{chainName}' {(expanded ? "collapsed" : "expanded")} ({members.Count} segments)");
                        }
                        GUILayout.EndHorizontal();

                        if (expanded)
                        {
                            for (int m = 0; m < members.Count; m++)
                            {
                                if (DrawRecordingRow(members[m], committed, now, true))
                                { deleted = true; break; }
                            }
                        }
                    }
                    // else: chain member already drawn with its group — skip
                }

                GUILayout.EndScrollView();

                // Capture scroll view rect for tooltip visibility guard
                if (Event.current.type == EventType.Repaint)
                    scrollViewRect = GUILayoutUtility.GetLastRect();
            }

            // Reset out-of-range state
            if (deleteConfirmIndex >= committed.Count)
                deleteConfirmIndex = -1;
            if (editingLoopPeriodIdx >= committed.Count)
                editingLoopPeriodIdx = -1;

            // Bottom button bar
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();

            if (committed.Count > 0)
            {
                string statsLabel = showExpandedStats ? "Stats \u25c0" : "Stats \u25b6";
                if (GUILayout.Button(statsLabel, GUILayout.Width(65)))
                {
                    showExpandedStats = !showExpandedStats;
                    ParsekLog.Verbose("UI", $"Recordings Stats toggled: {(showExpandedStats ? "expanded" : "collapsed")}");
                    if (showExpandedStats && recordingsWindowRect.width < 1015f)
                        recordingsWindowRect.width = 1015f;
                }
            }

            if (GUILayout.Button("Close"))
            {
                showRecordingsWindow = false;
                deleteConfirmIndex = -1;
                ParsekLog.Verbose("UI", "Recordings window closed via button");
            }

            GUILayout.EndHorizontal();

            // Tooltip rendering (on top of all window content, before DragWindow)
            if (Event.current.type == EventType.Repaint && hoveredRecIdx >= 0 &&
                hoveredRecIdx < committed.Count &&
                scrollViewRect.width > 0 &&
                scrollViewRect.Contains(Event.current.mousePosition))
            {
                DrawRecordingTooltip(committed[hoveredRecIdx]);
            }

            // Resize handle (bottom-right corner)
            Rect handleRect = new Rect(
                recordingsWindowRect.width - ResizeHandleSize,
                recordingsWindowRect.height - ResizeHandleSize,
                ResizeHandleSize, ResizeHandleSize);
            GUI.Label(handleRect, "\u25e2"); // triangle
            if (Event.current.type == EventType.MouseDown && handleRect.Contains(Event.current.mousePosition))
            {
                isResizingRecordingsWindow = true;
                ParsekLog.Verbose("UI", "Recordings window resize started");
                Event.current.Use();
            }

            GUI.DragWindow();
        }

        /// <summary>
        /// Draws a single recording row. Returns true if the list was modified (break iteration).
        /// </summary>
        private bool DrawRecordingRow(int ri, List<RecordingStore.Recording> committed, double now, bool indented)
        {
            var rec = committed[ri];
            GUILayout.BeginHorizontal();

            if (indented)
                GUILayout.Space(15f); // indent chain children

            // Enable checkbox
            bool enabled = GUILayout.Toggle(rec.PlaybackEnabled, "", GUILayout.Width(ColW_Enable));
            if (enabled != rec.PlaybackEnabled)
            {
                rec.PlaybackEnabled = enabled;
                ParsekLog.Info("UI", $"Recording '{rec.VesselName}' playback {(enabled ? "enabled" : "disabled")}" +
                    (!string.IsNullOrEmpty(rec.SegmentPhase) ? $" (segment: {RecordingStore.GetSegmentPhaseLabel(rec)})" : ""));
            }

            // #
            GUILayout.Label((ri + 1).ToString(), GUILayout.Width(ColW_Index));

            // Phase label
            string phaseLabel = RecordingStore.GetSegmentPhaseLabel(rec);
            if (!string.IsNullOrEmpty(phaseLabel))
            {
                GUIStyle phaseStyle;
                if (rec.SegmentPhase == "atmo") phaseStyle = phaseStyleAtmo;
                else if (rec.SegmentPhase == "space") phaseStyle = phaseStyleSpace;
                else phaseStyle = phaseStyleExo;
                GUILayout.Label(phaseLabel, phaseStyle, GUILayout.Width(ColW_Phase));
            }
            else
            {
                GUILayout.Label("", GUILayout.Width(ColW_Phase));
            }

            // Name
            string name = string.IsNullOrEmpty(rec.VesselName) ? "Untitled" : rec.VesselName;
            GUILayout.Label(name, GUILayout.ExpandWidth(true));

            // Launch Time
            string launchTime = rec.Points.Count > 0
                ? KSPUtil.PrintDateCompact(rec.StartUT, true)
                : "-";
            GUILayout.Label(launchTime, GUILayout.Width(ColW_Launch));

            // Duration
            double dur = rec.EndUT - rec.StartUT;
            GUILayout.Label(FormatDuration(dur), GUILayout.Width(ColW_Dur));

            // Expanded stats
            if (showExpandedStats)
            {
                var stats = GetOrComputeStats(rec);
                GUILayout.Label(FormatAltitude(stats.maxAltitude), GUILayout.Width(ColW_MaxAlt));
                GUILayout.Label(FormatSpeed(stats.maxSpeed), GUILayout.Width(ColW_MaxSpd));
                GUILayout.Label(FormatDistance(stats.distanceTravelled), GUILayout.Width(ColW_Dist));
                GUILayout.Label(stats.pointCount.ToString(), GUILayout.Width(ColW_Pts));
            }

            // Status
            GUIStyle statusStyle;
            string statusText;
            if (now < rec.StartUT)
            {
                statusStyle = statusStyleFuture;
                statusText = "future";
            }
            else if (now <= rec.EndUT)
            {
                statusStyle = statusStyleActive;
                statusText = "active";
            }
            else
            {
                statusStyle = statusStylePast;
                statusText = "past";
            }
            GUILayout.Label(statusText, statusStyle, GUILayout.Width(ColW_Status));

            // Loop checkbox
            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Loop));
            GUILayout.FlexibleSpace();
            bool loop = GUILayout.Toggle(rec.LoopPlayback, "");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (loop != rec.LoopPlayback)
            {
                rec.LoopPlayback = loop;
                ParsekLog.Info("UI", $"Recording '{rec.VesselName}' loop playback set to {loop}");
                if (!loop && editingLoopPeriodIdx == ri)
                    editingLoopPeriodIdx = -1;
            }

            // Period
            DrawLoopPeriodCell(rec, ri, dur);

            // Watch button (flight only)
            if (InFlight)
            {
                bool hasGhost = flight.HasActiveGhost(ri);
                bool sameBody = flight.IsGhostOnSameBody(ri);
                bool isWatching = flight.WatchedRecordingIndex == ri;
                bool canWatch = hasGhost && sameBody;

                GUI.enabled = canWatch;
                string watchLabel = isWatching ? "W*" : "W";
                string watchTooltip = (hasGhost && !sameBody) ? "Ghost is on a different body" : "";
                var watchContent = new GUIContent(watchLabel, watchTooltip);
                if (GUILayout.Button(watchContent, GUILayout.Width(ColW_Watch)))
                {
                    if (isWatching)
                        flight.ExitWatchMode();
                    else
                        flight.EnterWatchMode(ri);
                }
                GUI.enabled = true;
            }

            // Rewind button
            {
                bool hasRewindSave = !string.IsNullOrEmpty(rec.RewindSaveFileName);
                if (hasRewindSave)
                {
                    string rewindReason;
                    bool isRecording = InFlight && flight.IsRecording;
                    bool canRewind = RecordingStore.CanRewind(rec, out rewindReason, isRecording: isRecording);
                    GUI.enabled = canRewind;
                    string tooltip = canRewind ? "Rewind to this launch" : rewindReason;
                    if (GUILayout.Button(new GUIContent("R", tooltip), GUILayout.Width(ColW_Rewind)))
                        ShowRewindConfirmation(rec);
                    GUI.enabled = true;
                }
                else
                {
                    GUILayout.Label("", GUILayout.Width(ColW_Rewind));
                }
            }

            // Delete button (X → ? → PopupDialog confirm → delete)
            GUI.enabled = InFlight ? flight.CanDeleteRecording : true;
            if (deleteConfirmIndex == ri)
            {
                if (GUILayout.Button("?", GUILayout.Width(ColW_Delete)))
                {
                    deleteConfirmIndex = -1;
                    ParsekLog.Verbose("UI", $"Delete confirm clicked for recording index={ri} name='{rec.VesselName}'");
                    ShowDeleteRecordingConfirmation(ri, rec.VesselName);
                }
                // Right-click cancels
                if (Event.current.type == EventType.MouseDown && Event.current.button == 1 &&
                    GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                {
                    deleteConfirmIndex = -1;
                    Event.current.Use();
                }
            }
            else
            {
                if (GUILayout.Button("X", GUILayout.Width(ColW_Delete)))
                {
                    deleteConfirmIndex = ri;
                    ParsekLog.Verbose("UI", $"Delete armed for recording index={ri} name='{rec.VesselName}'");
                }
            }
            GUI.enabled = true;

            GUILayout.EndHorizontal();

            // Hover detection
            if (Event.current.type == EventType.Repaint)
            {
                Rect rowRect = GUILayoutUtility.GetLastRect();
                if (rowRect.Contains(Event.current.mousePosition))
                    hoveredRecIdx = ri;
            }

            return false;
        }

        private void ShowDeleteRecordingConfirmation(int index, string vesselName)
        {
            int capturedIndex = index;
            string capturedName = vesselName;
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekDeleteRecordingConfirm",
                    $"Delete recording \"{capturedName}\" and its files?\n\nThis cannot be undone.",
                    "Confirm Delete Recording",
                    HighLogic.UISkin,
                    new DialogGUIButton("Delete", () =>
                    {
                        ParsekLog.Info("UI", $"Delete confirmed for recording index={capturedIndex} name='{capturedName}'");
                        if (editingLoopPeriodIdx == capturedIndex)
                            editingLoopPeriodIdx = -1;
                        else if (editingLoopPeriodIdx > capturedIndex)
                            editingLoopPeriodIdx--;
                        if (InFlight)
                            flight.DeleteRecording(capturedIndex);
                        else
                            RecordingStore.DeleteRecordingFull(capturedIndex);
                        InvalidateSort();
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Verbose("UI", $"Delete cancelled for recording '{capturedName}'");
                    })
                ),
                false, HighLogic.UISkin);
        }

        private void ShowRewindConfirmation(RecordingStore.Recording rec)
        {
            int futureCount = RecordingStore.CountFutureRecordings(rec.StartUT);
            string futureText = futureCount > 0
                ? $"\n\n{futureCount} recording(s) after this launch will replay as ghosts."
                : "";

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            string launchDate = KSPUtil.PrintDateCompact(rec.StartUT, true);
            string message = $"Rewind to \"{rec.VesselName}\" launch at {launchDate}?" +
                futureText +
                "\n\nAny uncommitted progress will be lost.";

            var capturedRec = rec;
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekRewindConfirm",
                    message,
                    "Confirm Rewind",
                    HighLogic.UISkin,
                    new DialogGUIButton("Rewind", () =>
                    {
                        ParsekLog.Info("Rewind",
                            $"User confirmed rewind to \"{capturedRec.VesselName}\" at UT {capturedRec.StartUT}");
                        RecordingStore.InitiateRewind(capturedRec);
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Info("Rewind", "User cancelled rewind confirmation");
                    })
                ),
                false, HighLogic.UISkin);
        }

        private void ShowClearRecordingConfirmation()
        {
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekClearRecordingConfirm",
                    "Discard the current recording?\n\nThis cannot be undone.",
                    "Confirm Clear Recording",
                    HighLogic.UISkin,
                    new DialogGUIButton("Clear", () =>
                    {
                        ParsekLog.Info("UI", "User confirmed clear recording");
                        flight.ClearRecording();
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Info("UI", "User cancelled clear recording");
                    })
                ),
                false, HighLogic.UISkin);
        }

        private void ShowWipeRecordingsConfirmation(int count)
        {
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekWipeRecordingsConfirm",
                    $"Delete all {count} recording(s) and their files?\n\nThis cannot be undone.",
                    "Confirm Wipe Recordings",
                    HighLogic.UISkin,
                    new DialogGUIButton("Wipe All", () =>
                    {
                        foreach (var rec in RecordingStore.CommittedRecordings)
                            ParsekScenario.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                        ParsekScenario.ClearReplacements();
                        if (InFlight) flight.DestroyAllTimelineGhosts();
                        RecordingStore.ClearCommitted();
                        GameStateStore.ClearScienceSubjects();
                        ParsekLog.Info("UI", "All recordings wiped");
                        ParsekLog.ScreenMessage("All recordings wiped", 2f);
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Verbose("UI", "Wipe recordings cancelled");
                    })
                ),
                false, HighLogic.UISkin);
        }

        private void ShowWipeActionsConfirmation(int count)
        {
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekWipeActionsConfirm",
                    $"Delete all {count} game action milestone(s)?\n\nThis cannot be undone.",
                    "Confirm Wipe Game Actions",
                    HighLogic.UISkin,
                    new DialogGUIButton("Wipe All", () =>
                    {
                        MilestoneStore.ClearAll();
                        ResourceBudget.Invalidate();
                        ParsekLog.Info("UI", "All game actions wiped");
                        ParsekLog.ScreenMessage("All game actions wiped", 2f);
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Verbose("UI", "Wipe game actions cancelled");
                    })
                ),
                false, HighLogic.UISkin);
        }

        private void DrawSortableHeader(string label, SortColumn col, float width, bool expand = false)
        {
            string arrow = sortColumn == col ? (sortAscending ? " \u25b2" : " \u25bc") : "";
            bool clicked;
            if (expand)
                clicked = GUILayout.Button(label + arrow, GUI.skin.label, GUILayout.ExpandWidth(true));
            else
                clicked = GUILayout.Button(label + arrow, GUI.skin.label, GUILayout.Width(width));

            if (clicked)
            {
                if (sortColumn == col)
                    sortAscending = !sortAscending;
                else
                {
                    sortColumn = col;
                    sortAscending = true;
                }
                InvalidateSort();
                ParsekLog.Verbose("UI", $"Sort column changed: {sortColumn} {(sortAscending ? "asc" : "desc")}");
            }
        }

        internal void CancelDeleteConfirm()
        {
            deleteConfirmIndex = -1;
        }

        private void InvalidateSort()
        {
            lastSortedCount = -1;
        }

        private void RebuildSortedIndices(List<RecordingStore.Recording> committed, double now)
        {
            if (sortedIndices != null && lastSortedCount == committed.Count)
                return;

            lastSortedCount = committed.Count;
            sortedIndices = new int[committed.Count];
            for (int i = 0; i < committed.Count; i++)
                sortedIndices[i] = i;

            if (sortColumn == SortColumn.Index)
            {
                if (!sortAscending)
                    System.Array.Reverse(sortedIndices);
                return;
            }

            var col = sortColumn;
            var asc = sortAscending;
            System.Array.Sort(sortedIndices, (a, b) =>
                CompareRecordings(committed[a], committed[b], col, asc, now));
        }

        internal static int GetStatusOrder(RecordingStore.Recording rec, double now)
        {
            if (now < rec.StartUT) return 0;  // future
            if (now <= rec.EndUT) return 1;    // active
            return 2;                          // past
        }

        internal static int CompareRecordings(
            RecordingStore.Recording ra, RecordingStore.Recording rb,
            SortColumn column, bool ascending, double now)
        {
            int cmp = 0;
            switch (column)
            {
                case SortColumn.Index:
                    cmp = 0; // stable — Array.Sort preserves original order for equal elements
                    break;
                case SortColumn.Name:
                    string na = string.IsNullOrEmpty(ra.VesselName) ? "Untitled" : ra.VesselName;
                    string nb = string.IsNullOrEmpty(rb.VesselName) ? "Untitled" : rb.VesselName;
                    cmp = string.Compare(na, nb, System.StringComparison.OrdinalIgnoreCase);
                    break;
                case SortColumn.LaunchTime:
                    cmp = ra.StartUT.CompareTo(rb.StartUT);
                    break;
                case SortColumn.Duration:
                    cmp = (ra.EndUT - ra.StartUT).CompareTo(rb.EndUT - rb.StartUT);
                    break;
                case SortColumn.Status:
                    cmp = GetStatusOrder(ra, now).CompareTo(GetStatusOrder(rb, now));
                    break;
            }
            return ascending ? cmp : -cmp;
        }

        internal static int[] BuildSortedIndices(
            List<RecordingStore.Recording> committed, SortColumn column, bool ascending, double now)
        {
            var indices = new int[committed.Count];
            for (int i = 0; i < committed.Count; i++)
                indices[i] = i;

            if (column == SortColumn.Index)
            {
                if (!ascending)
                    System.Array.Reverse(indices);
                return indices;
            }

            System.Array.Sort(indices, (a, b) =>
                CompareRecordings(committed[a], committed[b], column, ascending, now));
            return indices;
        }

        internal static string FormatDuration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
            int total = (int)seconds;
            if (total < 60) return $"{total}s";
            if (total < 3600) return $"{total / 60}m {total % 60}s";
            return $"{total / 3600}h {(total % 3600) / 60}m";
        }

        internal static string FormatAltitude(double meters)
        {
            if (meters < 1000) return $"{(int)meters}m";
            if (meters < 1000000) return (meters / 1000).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "km";
            return (meters / 1000000).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "Mm";
        }

        internal static string FormatSpeed(double mps)
        {
            if (mps < 1000) return $"{(int)mps}m/s";
            return (mps / 1000).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "km/s";
        }

        internal static string FormatDistance(double meters)
        {
            if (meters < 1000) return $"{(int)meters}m";
            if (meters < 1000000) return (meters / 1000).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "km";
            return (meters / 1000000).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "Mm";
        }

        private RecordingStats GetOrComputeStats(RecordingStore.Recording rec)
        {
            if (rec.CachedStats.HasValue && rec.CachedStatsPointCount == rec.Points.Count)
                return rec.CachedStats.Value;

            System.Func<string, double[]> bodyLookup = name =>
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

        private void DrawRecordingTooltip(RecordingStore.Recording rec)
        {
            var stats = GetOrComputeStats(rec);

            string text = $"Max Altitude: {FormatAltitude(stats.maxAltitude)}\n" +
                          $"Max Speed: {FormatSpeed(stats.maxSpeed)}\n" +
                          $"Distance: {FormatDistance(stats.distanceTravelled)}\n" +
                          $"Points: {stats.pointCount}";

            if (stats.orbitSegmentCount > 0)
                text += $"\nOrbit Segments: {stats.orbitSegmentCount}";
            if (stats.partEventCount > 0)
                text += $"\nPart Events: {stats.partEventCount}";
            if (!string.IsNullOrEmpty(stats.primaryBody))
                text += $"\nBody: {stats.primaryBody}";
            if (stats.maxRange > 0)
                text += $"\nMax Range: {FormatDistance(stats.maxRange)}";

            EnsureTooltipStyle();

            GUIContent content = new GUIContent(text);
            Vector2 size = tooltipLabelStyle.CalcSize(content);
            size.x += 12;
            size.y += 8;

            Vector2 mousePos = Event.current.mousePosition;
            float tooltipX = mousePos.x + 15;
            float tooltipY = mousePos.y - size.y - 5;

            if (tooltipX + size.x > recordingsWindowRect.width)
                tooltipX = mousePos.x - size.x - 5;
            if (tooltipY < 0)
                tooltipY = mousePos.y + 20;

            Rect tooltipRect = new Rect(tooltipX, tooltipY, size.x, size.y);
            GUI.Box(tooltipRect, "");
            GUI.Label(new Rect(tooltipX + 6, tooltipY + 4, size.x - 12, size.y - 8),
                content, tooltipLabelStyle);
        }

        private static readonly GUIContent intervalTooltip = new GUIContent("",
            "Loop interval (seconds):\n  Positive: wait N seconds after end\n  Zero: restart immediately\n  Negative: overlap by N seconds");

        private string FormatInterval(double interval)
        {
            string sign = interval < 0 ? "-" : "";
            return sign + FormatDuration(System.Math.Abs(interval));
        }

        private void DrawLoopPeriodCell(RecordingStore.Recording rec, int ri, double dur)
        {
            if (!rec.LoopPlayback)
            {
                GUI.enabled = false;
                GUILayout.Label("-", GUILayout.Width(ColW_Period));
                GUI.enabled = true;
                return;
            }

            if (editingLoopPeriodIdx == ri)
            {
                GUI.SetNextControlName("PeriodEdit");
                editingLoopPeriodText = GUILayout.TextField(
                    editingLoopPeriodText, GUILayout.Width(ColW_Period));

                if (Event.current.type == EventType.KeyUp &&
                    Event.current.keyCode == KeyCode.Return &&
                    GUI.GetNameOfFocusedControl() == "PeriodEdit")
                {
                    ApplyLoopIntervalEdit(rec, dur);
                    editingLoopPeriodIdx = -1;
                }
            }
            else
            {
                var content = new GUIContent(FormatInterval(rec.LoopIntervalSeconds), intervalTooltip.tooltip);
                if (GUILayout.Button(content, GUILayout.Width(ColW_Period)))
                {
                    // Save any in-progress edit on another recording
                    if (editingLoopPeriodIdx >= 0)
                    {
                        var committed = RecordingStore.CommittedRecordings;
                        if (editingLoopPeriodIdx < committed.Count)
                        {
                            var editRec = committed[editingLoopPeriodIdx];
                            double editDur = editRec.EndUT - editRec.StartUT;
                            ApplyLoopIntervalEdit(editRec, editDur);
                        }
                    }

                    editingLoopPeriodIdx = ri;
                    editingLoopPeriodText = ((int)rec.LoopIntervalSeconds).ToString();
                }
            }
        }

        private void ApplyLoopIntervalEdit(RecordingStore.Recording rec, double dur)
        {
            double newInterval;
            if (double.TryParse(editingLoopPeriodText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out newInterval)
                && newInterval > -(dur - 0.001))
            {
                rec.LoopIntervalSeconds = newInterval;
                ParsekLog.Info("UI",
                    $"Recording '{rec.VesselName}' loop interval updated to " +
                    rec.LoopIntervalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "s");
            }
            else
            {
                ParsekLog.Warn("UI",
                    $"Rejected loop interval edit '{editingLoopPeriodText}' for recording '{rec.VesselName}'");
            }
        }

        private void EnsureTooltipStyle()
        {
            if (tooltipLabelStyle != null) return;
            tooltipLabelStyle = new GUIStyle(GUI.skin.label);
            tooltipLabelStyle.wordWrap = false;
            tooltipLabelStyle.fontSize = 11;
        }

        private void EnsureStatusStyles()
        {
            if (statusStyleFuture != null) return;

            statusStyleFuture = new GUIStyle(GUI.skin.label);
            statusStyleFuture.normal.textColor = Color.gray;

            statusStyleActive = new GUIStyle(GUI.skin.label);
            statusStyleActive.normal.textColor = Color.green;

            statusStylePast = new GUIStyle(GUI.skin.label);
            statusStylePast.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
        }

        public void DrawSettingsWindowIfOpen(Rect mainWindowRect)
        {
            if (!showSettingsWindow)
            {
                ReleaseSettingsInputLock();
                return;
            }

            if (settingsWindowRect.width < 1f)
            {
                settingsWindowRect = new Rect(
                    mainWindowRect.x + mainWindowRect.width + 10,
                    mainWindowRect.y,
                    280, 10);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI", $"Settings window initial position: x={settingsWindowRect.x.ToString("F0", ic)} y={settingsWindowRect.y.ToString("F0", ic)}");
            }

            settingsWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekSettings".GetHashCode(),
                settingsWindowRect,
                DrawSettingsWindow,
                "Parsek \u2014 Settings",
                GUILayout.Width(280)
            );
            LogWindowPosition("Settings", ref lastSettingsWindowRect, settingsWindowRect);

            if (settingsWindowRect.Contains(Event.current.mousePosition))
            {
                if (!settingsWindowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, SettingsInputLockId);
                    settingsWindowHasInputLock = true;
                }
            }
            else
            {
                ReleaseSettingsInputLock();
            }
        }

        private void ReleaseSettingsInputLock()
        {
            if (!settingsWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(SettingsInputLockId);
            settingsWindowHasInputLock = false;
        }

        private void DrawSettingsWindow(int windowID)
        {
            var s = ParsekSettings.Current;
            if (s == null)
            {
                GUILayout.Label("Settings unavailable (no active game).");
                if (GUILayout.Button("Close"))
                    showSettingsWindow = false;
                GUI.DragWindow();
                return;
            }

            GUILayout.Label("Recording", GUI.skin.box);
            bool autoRecordOnLaunch = GUILayout.Toggle(s.autoRecordOnLaunch,
                new GUIContent("Auto-record on launch", "Start recording when a vessel leaves the pad or runway"));
            if (autoRecordOnLaunch != s.autoRecordOnLaunch)
            {
                s.autoRecordOnLaunch = autoRecordOnLaunch;
                ParsekLog.Info("UI", $"Setting changed: autoRecordOnLaunch={s.autoRecordOnLaunch}");
            }

            bool autoRecordOnEva = GUILayout.Toggle(s.autoRecordOnEva,
                new GUIContent("Auto-record on EVA", "Start recording when a kerbal goes EVA from the pad"));
            if (autoRecordOnEva != s.autoRecordOnEva)
            {
                s.autoRecordOnEva = autoRecordOnEva;
                ParsekLog.Info("UI", $"Setting changed: autoRecordOnEva={s.autoRecordOnEva}");
            }

            bool autoMerge = GUILayout.Toggle(s.autoMerge,
                new GUIContent("Auto-merge recordings", "When off, a confirmation dialog appears after each recording"));
            if (autoMerge != s.autoMerge)
            {
                s.autoMerge = autoMerge;
                ParsekLog.Info("UI", $"Setting changed: autoMerge={s.autoMerge}");
            }

            bool autoWarpStop = GUILayout.Toggle(s.autoWarpStop,
                new GUIContent("Stop time warp for ghost playback", "Exit time warp when a ghost recording is about to start playing"));
            if (autoWarpStop != s.autoWarpStop)
            {
                s.autoWarpStop = autoWarpStop;
                ParsekLog.Info("UI", $"Setting changed: autoWarpStop={s.autoWarpStop}");
            }

            bool autoSplitAtAtmosphere = GUILayout.Toggle(s.autoSplitAtAtmosphere,
                new GUIContent("Auto-split at atmosphere boundary", "Split recordings when crossing the atmosphere boundary"));
            if (autoSplitAtAtmosphere != s.autoSplitAtAtmosphere)
            {
                s.autoSplitAtAtmosphere = autoSplitAtAtmosphere;
                ParsekLog.Info("UI", $"Setting changed: autoSplitAtAtmosphere={s.autoSplitAtAtmosphere}");
            }

            bool autoSplitAtSoi = GUILayout.Toggle(s.autoSplitAtSoi,
                new GUIContent("Auto-split at SOI change", "Split recordings when entering a new sphere of influence"));
            if (autoSplitAtSoi != s.autoSplitAtSoi)
            {
                s.autoSplitAtSoi = autoSplitAtSoi;
                ParsekLog.Info("UI", $"Setting changed: autoSplitAtSoi={s.autoSplitAtSoi}");
            }

            GUILayout.Space(SpacingSmall);
            GUILayout.Label("Diagnostics", GUI.skin.box);
            bool verboseLogging = GUILayout.Toggle(s.verboseLogging, "Verbose logging (development default)");
            if (verboseLogging != s.verboseLogging)
            {
                s.verboseLogging = verboseLogging;
                ParsekLog.Info("UI", $"Setting changed: verboseLogging={s.verboseLogging}");
            }

            GUILayout.Space(SpacingSmall);
            GUILayout.Label("Sampling", GUI.skin.box);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Max interval: {s.maxSampleInterval:F1}s", GUILayout.Width(140));
            float maxSampleInterval = GUILayout.HorizontalSlider(s.maxSampleInterval, 1f, 10f);
            if (Mathf.Abs(maxSampleInterval - s.maxSampleInterval) > 0.0001f)
            {
                s.maxSampleInterval = maxSampleInterval;
                ParsekLog.VerboseRateLimited("UI", "sampling.maxSampleInterval",
                    $"Setting changed: maxSampleInterval={s.maxSampleInterval:F1}s", 1.0);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Direction: {s.velocityDirThreshold:F1}\u00b0", GUILayout.Width(140));
            float velocityDirThreshold = GUILayout.HorizontalSlider(s.velocityDirThreshold, 0.5f, 10f);
            if (Mathf.Abs(velocityDirThreshold - s.velocityDirThreshold) > 0.0001f)
            {
                s.velocityDirThreshold = velocityDirThreshold;
                ParsekLog.VerboseRateLimited("UI", "sampling.velocityDirThreshold",
                    $"Setting changed: velocityDirThreshold={s.velocityDirThreshold:F1}", 1.0);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Speed: {s.speedChangeThreshold:F0}%", GUILayout.Width(140));
            float speedChangeThreshold = GUILayout.HorizontalSlider(s.speedChangeThreshold, 1f, 20f);
            if (Mathf.Abs(speedChangeThreshold - s.speedChangeThreshold) > 0.0001f)
            {
                s.speedChangeThreshold = speedChangeThreshold;
                ParsekLog.VerboseRateLimited("UI", "sampling.speedChangeThreshold",
                    $"Setting changed: speedChangeThreshold={s.speedChangeThreshold:F0}%", 1.0);
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(SpacingSmall);
            GUILayout.Label("Data Management", GUI.skin.box);

            int committedCount = RecordingStore.CommittedRecordings.Count;
            int milestoneCount = MilestoneStore.Milestones.Count;

            GUI.enabled = committedCount > 0;
            if (GUILayout.Button($"Wipe All Recordings ({committedCount})"))
                ShowWipeRecordingsConfirmation(committedCount);
            GUI.enabled = true;

            GUI.enabled = milestoneCount > 0;
            if (GUILayout.Button($"Wipe All Game Actions ({milestoneCount})"))
                ShowWipeActionsConfirmation(milestoneCount);
            GUI.enabled = true;

            if (InFlight)
            {
                int activeGhosts = flight.TimelineGhostCount;
                GUI.enabled = activeGhosts > 0;
                if (GUILayout.Button($"Despawn Ghosts ({activeGhosts})"))
                {
                    flight.DestroyAllTimelineGhosts();
                    ParsekLog.Info("UI", "Ghosts despawned from settings");
                }
                GUI.enabled = true;
            }

            GUILayout.Space(SpacingLarge);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Defaults"))
            {
                ParsekLog.Verbose("UI", "Settings Defaults button clicked");
                s.autoRecordOnLaunch = true;
                s.autoRecordOnEva = true;
                s.autoMerge = false;
                s.autoWarpStop = true;
                s.autoSplitAtAtmosphere = true;
                s.autoSplitAtSoi = true;
                s.verboseLogging = true;
                s.maxSampleInterval = 3.0f;
                s.velocityDirThreshold = 2.0f;
                s.speedChangeThreshold = 5.0f;
                ParsekLog.Info("UI", "Settings reset to defaults");
            }
            if (GUILayout.Button("Close"))
            {
                showSettingsWindow = false;
                ParsekLog.Verbose("UI", "Settings window closed via button");
            }
            GUILayout.EndHorizontal();

            // Render tooltip at bottom of window when hovering a control with GUIContent tooltip
            if (!string.IsNullOrEmpty(GUI.tooltip))
            {
                GUILayout.Space(SpacingSmall);
                GUILayout.Label(GUI.tooltip, GUI.skin.box);
            }

            GUI.DragWindow();
        }

        public void DrawMapMarkers()
        {
            Camera cam = PlanetariumCamera.Camera;
            if (cam == null) return;

            EnsureMapMarkerResources();

            // Manual preview ghost
            if (flight.IsPlaying && flight.PreviewGhost != null)
            {
                DrawMapMarkerAt(cam, flight.PreviewGhost.transform.position, "Preview", Color.green);
            }

            // Timeline ghosts
            var committed = RecordingStore.CommittedRecordings;
            Color ghostColor = new Color(0.2f, 1f, 0.4f, 0.9f);
            foreach (var kvp in flight.TimelineGhosts)
            {
                if (kvp.Value == null) continue;
                string ghostName = kvp.Key < committed.Count ? committed[kvp.Key].VesselName : "Ghost";
                DrawMapMarkerAt(cam, kvp.Value.transform.position, ghostName, ghostColor);
            }
        }

        public string GetStatusText()
        {
            if (flight.IsRecording) return "RECORDING";
            if (flight.IsPlaying) return "PREVIEWING";
            if (flight.recording.Count > 0) return "Ready (has recording)";
            return "Idle";
        }

        public void Cleanup()
        {
            ReleaseRecordingsInputLock();
            ReleaseActionsInputLock();
            ReleaseSettingsInputLock();
            if (mapMarkerTexture != null)
                Object.Destroy(mapMarkerTexture);
        }

        private void DrawMapMarkerAt(Camera cam, Vector3 worldPos, string label, Color color)
        {
            Vector3d scaledPos = ScaledSpace.LocalToScaledSpace(worldPos);
            Vector3 screenPos = cam.WorldToScreenPoint(scaledPos);

            // Behind camera
            if (screenPos.z < 0) return;

            // GUI coordinates (Y inverted)
            float x = screenPos.x;
            float y = Screen.height - screenPos.y;

            // Draw marker dot
            Color prevColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(x - 5, y - 5, 10, 10), mapMarkerTexture);
            GUI.color = prevColor;

            // Draw vessel name label
            mapMarkerStyle.normal.textColor = color;
            GUI.Label(new Rect(x - 75, y + 7, 150, 20), label, mapMarkerStyle);
        }

        private void EnsureMapMarkerResources()
        {
            if (mapMarkerTexture == null)
            {
                int size = 10;
                mapMarkerTexture = new Texture2D(size, size, TextureFormat.ARGB32, false);
                float center = size / 2f;
                float radius = size / 2f - 1f;
                for (int py = 0; py < size; py++)
                {
                    for (int px = 0; px < size; px++)
                    {
                        float dist = Mathf.Sqrt((px - center) * (px - center) + (py - center) * (py - center));
                        mapMarkerTexture.SetPixel(px, py, dist <= radius ? Color.white : Color.clear);
                    }
                }
                mapMarkerTexture.Apply();
            }

            if (mapMarkerStyle == null)
            {
                mapMarkerStyle = new GUIStyle(GUI.skin.label);
                mapMarkerStyle.fontSize = 11;
                mapMarkerStyle.fontStyle = FontStyle.Bold;
                mapMarkerStyle.alignment = TextAnchor.UpperCenter;
            }
        }
    }
}
