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
        private const float ColW_LoopLabel = 30f;
        private const float ColW_LoopToggle = 15f;
        private const float ColW_Delete = 25f;
        private const float ScrollbarWidth = 16f;

        // Chain grouping state
        private HashSet<string> expandedChains = new HashSet<string>();

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
        }

        public void DrawWindow(int windowID)
        {
            GUILayout.BeginVertical();

            // Status
            GUILayout.Label($"Status: {GetStatusText()}");
            GUILayout.Space(5);

            // Recording info
            GUILayout.Label($"Recorded Points: {flight.recording.Count}");
            if (flight.recording.Count > 0)
            {
                double duration = flight.recording[flight.recording.Count - 1].ut - flight.recording[0].ut;
                GUILayout.Label($"Duration: {duration:F1}s");
            }

            // Timeline info
            int committedCount = RecordingStore.CommittedRecordings.Count;
            int activeGhosts = flight.TimelineGhostCount;
            GUILayout.Label($"Timeline: {committedCount} recording(s), {activeGhosts} active ghost(s)");

            int actionCount = MilestoneStore.GetPendingEventCount();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button($"Recordings ({committedCount})"))
            {
                showRecordingsWindow = !showRecordingsWindow;
                ParsekLog.Verbose("UI", $"Recordings window toggled: {(showRecordingsWindow ? "open" : "closed")}");
            }
            if (GUILayout.Button($"Actions ({actionCount})"))
            {
                showActionsWindow = !showActionsWindow;
                ParsekLog.Verbose("UI", $"Actions window toggled: {(showActionsWindow ? "open" : "closed")}");
            }
            if (GUILayout.Button("Settings"))
            {
                showSettingsWindow = !showSettingsWindow;
                ParsekLog.Verbose("UI", $"Settings window toggled: {(showSettingsWindow ? "open" : "closed")}");
            }
            GUILayout.EndHorizontal();

            // Compact budget summary (full display in Actions window)
            DrawCompactBudgetLine();

            // Active ghost controls — Take Control buttons
            var committed = RecordingStore.CommittedRecordings;
            var ghosts = flight.TimelineGhosts;
            for (int i = 0; i < committed.Count; i++)
            {
                if (!ghosts.ContainsKey(i) || ghosts[i] == null) continue;
                var rec = committed[i];
                if (rec.VesselSnapshot == null || rec.VesselDestroyed || rec.VesselSpawned || rec.TakenControl) continue;

                GUILayout.BeginHorizontal();
                GUILayout.Label(rec.VesselName, GUILayout.ExpandWidth(true));
                if (GUILayout.Button("Take Control", GUILayout.Width(90)))
                {
                    ParsekLog.Verbose("UI", $"Take Control clicked for ghost index={i} vessel='{rec.VesselName}'");
                    flight.TakeControlOfGhost(i);
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(10);

            // Controls
            GUILayout.Label("Controls:");
            GUILayout.Label("  F9  - Start/Stop Recording");
            GUILayout.Label("  F10 - Preview Playback");
            GUILayout.Label("  F11 - Stop Preview");

            GUILayout.Space(10);

            // Buttons
            GUILayout.BeginHorizontal();

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

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();

            GUI.enabled = !flight.IsRecording && flight.recording.Count > 0 && !flight.IsPlaying;
            if (GUILayout.Button("Preview Playback"))
            {
                ParsekLog.Verbose("UI", "Preview Playback button clicked");
                flight.StartPlayback();
            }

            GUI.enabled = flight.IsPlaying;
            if (GUILayout.Button("Stop Preview"))
            {
                ParsekLog.Verbose("UI", "Stop Preview button clicked");
                flight.StopPlayback();
            }

            GUI.enabled = true;

            GUILayout.EndHorizontal();

            // Clear buttons
            GUILayout.Space(5);
            GUI.enabled = !flight.IsRecording && !flight.IsPlaying && flight.recording.Count > 0;
            if (GUILayout.Button("Clear Current Recording"))
            {
                ParsekLog.Verbose("UI", "Clear Current Recording button clicked");
                flight.ClearRecording();
            }

            GUI.enabled = !flight.IsRecording && !flight.IsPlaying
                && flight.recording.Count >= 2 && !flight.HasActiveChain;
            if (GUILayout.Button("Commit Flight"))
            {
                ParsekLog.Verbose("UI", "Commit Flight button clicked");
                flight.CommitFlight();
            }

            GUI.enabled = activeGhosts > 0;
            if (GUILayout.Button($"Despawn Ghosts ({activeGhosts})"))
            {
                ParsekLog.Verbose("UI", $"Despawn Ghosts button clicked, count={activeGhosts}");
                flight.DestroyAllTimelineGhosts();
                ParsekLog.Info("UI", "Ghosts despawned");
            }

            GUI.enabled = committedCount > 0;
            if (GUILayout.Button($"Wipe Recordings ({committedCount})"))
            {
                ParsekLog.Verbose("UI", $"Wipe Recordings button clicked, count={committedCount}");
                // Unreserve crew from all recordings before wiping
                foreach (var rec in RecordingStore.CommittedRecordings)
                    ParsekScenario.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                ParsekScenario.ClearReplacements();
                flight.DestroyAllTimelineGhosts();
                RecordingStore.ClearCommitted();
                ParsekLog.Info("UI", "All recordings wiped");
                ParsekLog.ScreenMessage("All recordings wiped", 2f);
            }
            GUI.enabled = true;

            GUILayout.EndVertical();

            // Make window draggable
            GUI.DragWindow();
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
                MilestoneStore.Milestones);

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
                MilestoneStore.Milestones);

            if (budget.reservedFunds <= 0 && budget.reservedScience <= 0 && budget.reservedReputation <= 0)
                return;

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var parts = new List<string>();
            if (budget.reservedFunds > 0)
                parts.Add(budget.reservedFunds.ToString("N0", ic) + "F");
            if (budget.reservedScience > 0)
                parts.Add(budget.reservedScience.ToString("F1", ic) + "S");
            if (budget.reservedReputation > 0)
                parts.Add(budget.reservedReputation.ToString("F0", ic) + "Rep");

            if (parts.Count > 0)
            {
                string line = "Reserved: " + string.Join("  ", parts);
                if (GUILayout.Button(line, GUI.skin.label))
                {
                    showActionsWindow = !showActionsWindow;
                    ParsekLog.Verbose("UI", $"Budget line clicked — Actions window toggled: {(showActionsWindow ? "open" : "closed")}");
                }
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
                float x = mainWindowRect.x - 390;
                if (x < 0) x = mainWindowRect.x + mainWindowRect.width + 10;
                actionsWindowRect = new Rect(x, mainWindowRect.y, 380, 350);
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

            // B. Committed Actions List
            var milestones = MilestoneStore.Milestones;
            uint currentEpoch = MilestoneStore.CurrentEpoch;
            var allEvents = new List<System.Tuple<GameStateEvent, bool>>(); // event, isReplayed
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

            if (allEvents.Count > 0)
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
                GUILayout.EndHorizontal();

                actionsScrollPos = GUILayout.BeginScrollView(actionsScrollPos, GUILayout.ExpandHeight(true));

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

                    GUILayout.EndHorizontal();
                }

                GUILayout.EndScrollView();
            }
            else if (MilestoneStore.MilestoneCount == 0)
            {
                GUILayout.Space(5);
                GUILayout.Label("No committed actions.");
            }

            // C. Bottom Bar
            GUILayout.Space(5);

            uint epoch = MilestoneStore.CurrentEpoch;
            if (epoch > 0)
            {
                GUILayout.Label($"Epoch: {epoch} ({epoch} revert{(epoch == 1 ? "" : "s")})",
                    actionsGrayStyle);
            }

            GUILayout.BeginHorizontal();

            int committedCount = RecordingStore.CommittedRecordings.Count;
            GUI.enabled = committedCount > 0 || MilestoneStore.MilestoneCount > 0;
            if (GUILayout.Button("Wipe All"))
            {
                ParsekLog.Verbose("UI", $"Wipe All button clicked, recordings={committedCount} milestones={MilestoneStore.MilestoneCount}");
                foreach (var rec in RecordingStore.CommittedRecordings)
                    ParsekScenario.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                ParsekScenario.ClearReplacements();
                flight.DestroyAllTimelineGhosts();
                RecordingStore.ClearCommitted();
                MilestoneStore.ClearAll();
                GameStateStore.ClearEvents();
                GameStateStore.ClearBaselines();
                ParsekLog.Log("All recordings and game state wiped");
                ParsekLog.ScreenMessage("All data wiped", 2f);
            }
            GUI.enabled = true;

            if (GUILayout.Button("Close"))
            {
                showActionsWindow = false;
                ParsekLog.Verbose("UI", "Actions window closed via button");
            }

            GUILayout.EndHorizontal();

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
                recordingsWindowRect = new Rect(
                    mainWindowRect.x + mainWindowRect.width + 10,
                    mainWindowRect.y,
                    790, 520);
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
                GUILayout.Label("", GUILayout.Width(ColW_Enable)); // enable column header
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
                GUILayout.Label("Loop", GUILayout.Width(ColW_LoopLabel));
                bool newAllLoop = GUILayout.Toggle(allLoop, "", GUILayout.Width(ColW_LoopToggle));
                if (newAllLoop != allLoop)
                {
                    for (int i = 0; i < committed.Count; i++)
                        committed[i].LoopPlayback = newAllLoop;
                    ParsekLog.Info("UI", $"Set loop playback for all recordings: enabled={newAllLoop}");
                }

                GUILayout.Label("Period", GUILayout.Width(ColW_Period));

                GUILayout.Button("", GUI.skin.label, GUILayout.Width(ColW_Delete)); // placeholder
                GUILayout.Space(ScrollbarWidth);
                GUILayout.EndHorizontal();

                // Scrollable table body
                recordingsScrollPos = GUILayout.BeginScrollView(
                    recordingsScrollPos, GUILayout.ExpandHeight(true));

                // Build chain grouping: chainId → list of sorted row indices
                var chainRows = new Dictionary<string, List<int>>();
                var seenChains = new HashSet<string>();

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
                }

                // Draw in sorted order — standalone rows inline, chain groups
                // emitted at the position of their first sorted member
                bool deleted = false;
                for (int row = 0; row < sortedIndices.Length && !deleted; row++)
                {
                    int ri = sortedIndices[row];
                    var rec = committed[ri];

                    if (string.IsNullOrEmpty(rec.ChainId))
                    {
                        // Standalone recording
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

            string statsLabel = showExpandedStats ? "Stats \u25c0" : "Stats \u25b6";
            if (GUILayout.Button(statsLabel, GUILayout.Width(65)))
            {
                showExpandedStats = !showExpandedStats;
                ParsekLog.Verbose("UI", $"Recordings Stats toggled: {(showExpandedStats ? "expanded" : "collapsed")}");
                if (showExpandedStats && recordingsWindowRect.width < 1015f)
                    recordingsWindowRect.width = 1015f;
            }

            if (GUILayout.Button("Close"))
            {
                showRecordingsWindow = false;
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
            GUILayout.Label("", GUILayout.Width(ColW_LoopLabel));
            bool loop = GUILayout.Toggle(rec.LoopPlayback, "", GUILayout.Width(ColW_LoopToggle));
            if (loop != rec.LoopPlayback)
            {
                rec.LoopPlayback = loop;
                ParsekLog.Info("UI", $"Recording '{rec.VesselName}' loop playback set to {loop}");
                if (!loop && editingLoopPeriodIdx == ri)
                    editingLoopPeriodIdx = -1;
            }

            // Period
            DrawLoopPeriodCell(rec, ri, dur);

            // Delete button
            GUI.enabled = flight.CanDeleteRecording;
            if (deleteConfirmIndex == ri)
            {
                if (GUILayout.Button("?", GUILayout.Width(ColW_Delete)))
                {
                    deleteConfirmIndex = -1;
                    if (editingLoopPeriodIdx == ri)
                        editingLoopPeriodIdx = -1;
                    else if (editingLoopPeriodIdx > ri)
                        editingLoopPeriodIdx--;
                    ParsekLog.Info("UI", $"Delete confirmed for recording index={ri} name='{rec.VesselName}'");
                    flight.DeleteRecording(ri);
                    InvalidateSort();
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                    return true; // list changed
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

        private void InvalidateSort()
        {
            lastSortedCount = -1;
            sortedIndices = null;
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
                    ApplyLoopPeriodEdit(rec, dur);
                    editingLoopPeriodIdx = -1;
                }
            }
            else
            {
                double period = dur + rec.LoopPauseSeconds;
                if (GUILayout.Button(FormatDuration(period), GUILayout.Width(ColW_Period)))
                {
                    // Save any in-progress edit on another recording
                    if (editingLoopPeriodIdx >= 0)
                    {
                        var committed = RecordingStore.CommittedRecordings;
                        if (editingLoopPeriodIdx < committed.Count)
                        {
                            var editRec = committed[editingLoopPeriodIdx];
                            double editDur = editRec.EndUT - editRec.StartUT;
                            ApplyLoopPeriodEdit(editRec, editDur);
                        }
                    }

                    editingLoopPeriodIdx = ri;
                    editingLoopPeriodText = ((int)(dur + rec.LoopPauseSeconds)).ToString();
                }
            }
        }

        private void ApplyLoopPeriodEdit(RecordingStore.Recording rec, double dur)
        {
            double newPeriod;
            if (double.TryParse(editingLoopPeriodText, out newPeriod) && newPeriod > 0)
            {
                double pause = newPeriod - dur;
                if (pause < 0) pause = 0;
                rec.LoopPauseSeconds = pause;
                ParsekLog.Info("UI",
                    $"Recording '{rec.VesselName}' loop period updated to {(dur + rec.LoopPauseSeconds):F1}s (pause={pause:F1}s)");
            }
            else
            {
                ParsekLog.Warn("UI",
                    $"Rejected loop period edit '{editingLoopPeriodText}' for recording '{rec.VesselName}'");
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
                    mainWindowRect.y + 40,
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
            bool autoRecordOnLaunch = GUILayout.Toggle(s.autoRecordOnLaunch, "Auto-record on launch");
            if (autoRecordOnLaunch != s.autoRecordOnLaunch)
            {
                s.autoRecordOnLaunch = autoRecordOnLaunch;
                ParsekLog.Info("UI", $"Setting changed: autoRecordOnLaunch={s.autoRecordOnLaunch}");
            }

            bool autoRecordOnEva = GUILayout.Toggle(s.autoRecordOnEva, "Auto-record on EVA");
            if (autoRecordOnEva != s.autoRecordOnEva)
            {
                s.autoRecordOnEva = autoRecordOnEva;
                ParsekLog.Info("UI", $"Setting changed: autoRecordOnEva={s.autoRecordOnEva}");
            }

            bool autoWarpStop = GUILayout.Toggle(s.autoWarpStop, "Auto-stop time warp");
            if (autoWarpStop != s.autoWarpStop)
            {
                s.autoWarpStop = autoWarpStop;
                ParsekLog.Info("UI", $"Setting changed: autoWarpStop={s.autoWarpStop}");
            }

            bool autoSplitAtAtmosphere = GUILayout.Toggle(s.autoSplitAtAtmosphere, "Auto-split at atmosphere boundary");
            if (autoSplitAtAtmosphere != s.autoSplitAtAtmosphere)
            {
                s.autoSplitAtAtmosphere = autoSplitAtAtmosphere;
                ParsekLog.Info("UI", $"Setting changed: autoSplitAtAtmosphere={s.autoSplitAtAtmosphere}");
            }

            bool autoSplitAtSoi = GUILayout.Toggle(s.autoSplitAtSoi, "Auto-split at SOI change");
            if (autoSplitAtSoi != s.autoSplitAtSoi)
            {
                s.autoSplitAtSoi = autoSplitAtSoi;
                ParsekLog.Info("UI", $"Setting changed: autoSplitAtSoi={s.autoSplitAtSoi}");
            }

            GUILayout.Space(5);
            GUILayout.Label("Diagnostics", GUI.skin.box);
            bool verboseLogging = GUILayout.Toggle(s.verboseLogging, "Verbose logging (development default)");
            if (verboseLogging != s.verboseLogging)
            {
                s.verboseLogging = verboseLogging;
                ParsekLog.Info("UI", $"Setting changed: verboseLogging={s.verboseLogging}");
            }

            GUILayout.Space(5);
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

            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Defaults"))
            {
                ParsekLog.Verbose("UI", "Settings Defaults button clicked");
                s.autoRecordOnLaunch = true;
                s.autoRecordOnEva = true;
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
