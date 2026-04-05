using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClickThroughFix;
using KSP.UI.Screens.Mapview;
using Parsek.InGameTests;
using UnityEngine;

namespace Parsek
{
    public enum UIMode { Flight, KSC }

    /// <summary>
    /// UI rendering for the Parsek window and map view markers.
    /// Receives a ParsekFlight reference for accessing flight state.
    /// </summary>
    public class ParsekUI
    {
        private readonly UIMode mode;
        private bool InFlight => mode == UIMode.Flight;

        private readonly ParsekFlight flight;

        // Opaque window style (replaces KSP's semi-transparent default)
        private GUIStyle opaqueWindowStyle;

        // Map view markers
        private GUIStyle mapMarkerStyle;
        private Texture2D mapMarkerDiamond; // fallback when atlas unavailable
        private static Texture2D vesselIconAtlas;
        private static Dictionary<VesselType, Rect> vesselIconUVs;

        // Recordings window
        private bool showRecordingsWindow;
        private Rect recordingsWindowRect;
        private Vector2 recordingsScrollPos;
        // GroupHierarchyStore.HideActive is stored on GroupHierarchyStore for persistence across scene changes and save/load
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

        // Test runner window
        private bool showTestRunnerWindow;
        private Rect testRunnerWindowRect;
        private Vector2 testRunnerScrollPos;
        private bool testRunnerWindowHasInputLock;
        private const string TestRunnerInputLockId = "Parsek_TestRunnerWindow";
        private Rect lastTestRunnerWindowRect;
        private InGameTestRunner testRunner;
        private readonly HashSet<string> expandedTestCategories = new HashSet<string>();
        // Cached grouped test list — rebuilt on init and after each run to avoid per-frame LINQ
        private List<KeyValuePair<string, List<InGameTestInfo>>> cachedTestGroups;
        private bool testRunnerWasRunning;

        // Actions window
        private bool showActionsWindow;
        private Rect actionsWindowRect;
        private Vector2 actionsScrollPos;
        private bool isResizingActionsWindow;
        private bool actionsWindowHasInputLock;
        private const string ActionsInputLockId = "Parsek_ActionsWindow";
        private int lastRetiredKerbalCount = -1; // for change-based logging

        // Cached styles for actions window
        private GUIStyle actionsGrayStyle;
        private GUIStyle actionsWhiteStyle;
        private GUIStyle actionsGreenStyle;
        private GUIStyle actionsRedStyle;

        // Actions table sort state
        private enum ActionsSortColumn { Time, Type, Description, Status }
        private ActionsSortColumn actionsSortColumn = ActionsSortColumn.Time;
        private bool actionsSortAscending = true;

        // Column widths — shared between header and body for alignment
        private const float ColW_Enable = 20f;
        private const float ColW_Phase = 70f;
        private const float ColW_Index = 30f;
        private const float ColW_Launch = 110f;
        // ColW_Countdown removed (#98) — merged into Status column
        private const float ColW_Dur = 80f;
        private const float ColW_Status = 120f;
        private const float ColW_Loop = 55f;
        private const float ColW_Watch = 50f;
        private const float ColW_Rewind = 65f;
        private const float ColW_Hide = 50f;

        // Reusable per-frame buffers (avoid allocation each frame)
        private static readonly Dictionary<string, int> chainTipIndexBuffer = new Dictionary<string, int>();
        private static readonly HashSet<int> chainStatusBuffer = new HashSet<int>();

        // Chain and group expansion state
        private HashSet<string> expandedChains = new HashSet<string>();
        private HashSet<string> expandedGroups = new HashSet<string>();

        // Group rename (deferred to next frame to avoid IMGUI layout mismatch)
        private string renamingGroup;
        private string renamingGroupText = "";
        private bool renamingGroupFocused;

        // Recording rename (deferred to next frame)
        private int renamingRecordingIdx = -1;
        private string renamingRecordingText = "";
        private bool renamingRecordingFocused;
        private Rect activeRenameRect;

        // Double-click detection
        private int lastClickedRecIdx = -1;
        private float lastClickTime;
        private string lastClickedGroup;
        private float lastGroupClickTime;
        private const float DoubleClickThreshold = 0.3f;

        // Group picker popup state
        private bool groupPopupOpen;
        private int groupPopupRecIdx = -1;
        private string groupPopupChainId;
        private string groupPopupGroup;
        private Vector2 groupPopupPosition;
        private HashSet<string> groupPopupChecked;
        private HashSet<string> groupPopupOriginal;
        private HashSet<string> groupPopupExpanded;
        private string groupPopupNewName = "";
        private Rect groupPopupRect;
        private Vector2 groupPopupScrollPos;
        private bool isResizingGroupPopup;
        private const float ColW_Group = 50f;
        private const float GroupPopupMinW = 220f;
        private const float GroupPopupMinH = 200f;

        // Runtime-only empty groups (not persisted)
        private List<string> knownEmptyGroups = new List<string>();

        // Cached phase label styles
        private GUIStyle phaseStyleAtmo;
        private GUIStyle phaseStyleExo;
        private GUIStyle phaseStyleSpace;
        private GUIStyle phaseStyleApproach;

        // Sort state
        internal enum SortColumn { Index, Phase, Name, LaunchTime, Duration, Status }
        private SortColumn sortColumn = SortColumn.LaunchTime;
        private bool sortAscending = true;

        // Root-level draw item for unified sorting of groups, chains, and standalone recordings
        private enum RootItemType { Group, Chain, Recording }

        private struct RootDrawItem
        {
            public double SortKey;
            public string SortName;
            public RootItemType ItemType;
            public string GroupName;   // for groups
            public string ChainId;     // for chains
            public int RecIdx;         // for standalone recordings
        }
        private int[] sortedIndices; // maps display row → CommittedRecordings index
        private int lastSortedCount = -1;

        // Tooltip state
        private GUIStyle tooltipLabelStyle;
        private Rect scrollViewRect;

        // Expanded stats columns
        private bool showExpandedStats;
        private const float ColW_MaxAlt = 65f;
        private const float ColW_MaxSpd = 65f;
        private const float ColW_Dist = 65f;
        private const float ColW_Pts = 35f;

        // Loop period editing — buffer used while text field is focused
        private int loopPeriodFocusedRi = -1;
        private string loopPeriodEditText = "";
        private Rect loopPeriodEditRect;

        // Settings auto-loop editing
        private string settingsAutoLoopText = "";
        private bool settingsAutoLoopEditing;
        private Rect settingsAutoLoopEditRect;

        private bool settingsCameraCutoffEditing;
        private string settingsCameraCutoffText = "";
        private Rect settingsCameraCutoffEditRect;
        private const float ColW_Period = 80f;

        // R/FF button state tracking for transition logging
        private Dictionary<int, bool> lastCanRewind = new Dictionary<int, bool>();
        private Dictionary<int, bool> lastCanFF = new Dictionary<int, bool>();

        // Cached styles for status labels
        private GUIStyle statusStyleFuture;
        private GUIStyle statusStyleActive;
        private GUIStyle statusStylePast;

        // Window drag tracking for position logging
        private Rect lastMainWindowRect;
        private Rect lastRecordingsWindowRect;
        private Rect lastActionsWindowRect;
        private Rect lastSettingsWindowRect;
        private Rect lastSpawnControlWindowRect;

        // Spawn Control window
        private bool showSpawnControlWindow;
        private Rect spawnControlWindowRect;
        private bool spawnControlWindowHasInputLock;
        private const string SpawnControlInputLockId = "Parsek_SpawnControlWindow";

        // Spawn Control sort state
        private enum SpawnSortColumn { Name, Distance, SpawnTime }
        private SpawnSortColumn spawnSortColumn = SpawnSortColumn.Distance;
        private bool spawnSortAscending = true;
        private bool isResizingSpawnControlWindow;
        private Vector2 spawnControlScrollPos;

        // Cached sorted candidate list -- re-sorted only when source data or sort state changes
        private List<NearbySpawnCandidate> cachedSortedCandidates = new List<NearbySpawnCandidate>();
        private int cachedCandidateCount = -1;
        private int cachedProximityGeneration = -1;
        private SpawnSortColumn cachedSortColumn = SpawnSortColumn.Distance;
        private bool cachedSortAscending = true;

        private BudgetSummary cachedBudget = default(BudgetSummary);

        private static readonly string VersionLabel = "v" +
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        private GUIStyle versionStyle;

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

        /// <summary>
        /// Returns the resource budget.
        /// </summary>
        private BudgetSummary GetCachedBudget()
        {
            return cachedBudget;
        }

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
                ParsekLog.Verbose("UI", $"Recordings window toggled: {(showRecordingsWindow ? "open" : "closed")}");
            }

            int actionCount = MilestoneStore.GetPendingEventCount() + GameStateStore.GetUncommittedEventCount();
            if (GUILayout.Button($"Game Actions ({actionCount})"))
            {
                showActionsWindow = !showActionsWindow;
                ParsekLog.Verbose("UI", $"Actions window toggled: {(showActionsWindow ? "open" : "closed")}");
            }

            // --- Real Spawn Control toggle (in the window group, after Game Actions) ---
            if (InFlight && flight != null)
            {
                int spawnCount = flight.NearbySpawnCandidates.Count;
                GUI.enabled = spawnCount > 0;
                if (GUILayout.Button(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Real Spawn Control ({0})", spawnCount)))
                {
                    showSpawnControlWindow = !showSpawnControlWindow;
                    ParsekLog.Verbose("UI",
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "Real Spawn Control window toggled: {0}",
                            showSpawnControlWindow ? "open" : "closed"));
                }
                GUI.enabled = true;
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

            // --- Version footer ---
            GUILayout.Space(SpacingSmall);
            if (versionStyle == null)
            {
                versionStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 10,
                    alignment = TextAnchor.MiddleRight,
                    normal = { textColor = new Color(1f, 1f, 1f, 0.4f) }
                };
            }
            GUILayout.Label(VersionLabel, versionStyle);

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
            var budget = GetCachedBudget();

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
                anyOverCommitted |= DrawResourceLine("Funds", currentFunds, budget.reservedFunds, "N0", ic);
            }

            if (budget.reservedScience > 0)
            {
                double currentScience = 0;
                try { if (ResearchAndDevelopment.Instance != null) currentScience = ResearchAndDevelopment.Instance.Science; } catch { }
                anyOverCommitted |= DrawResourceLine("Science", currentScience, budget.reservedScience, "F1", ic);
            }

            if (budget.reservedReputation > 0)
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
        /// Draws a single resource budget line (funds, science, or reputation).
        /// Returns true if the resource is over-committed.
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

        public void LogMainWindowPosition(Rect currentRect)
        {
            LogWindowPosition("Main", ref lastMainWindowRect, currentRect);
        }

        private void DrawCompactBudgetLine()
        {
            var budget = GetCachedBudget();

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

            HandleResizeDrag(ref actionsWindowRect, ref isResizingActionsWindow,
                MinWindowWidth, MinWindowHeight, "Actions window");

            EnsureOpaqueWindowStyle();
            actionsWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekActions".GetHashCode(),
                actionsWindowRect,
                DrawActionsWindow,
                "Parsek \u2014 Game Actions",
                opaqueWindowStyle,
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

            actionsGreenStyle = new GUIStyle(GUI.skin.label);
            actionsGreenStyle.normal.textColor = new Color(0.5f, 1f, 0.5f);

            actionsRedStyle = new GUIStyle(GUI.skin.label);
            actionsRedStyle.normal.textColor = new Color(1f, 0.5f, 0.5f);
        }

        private void DrawLedgerActionsSection(IReadOnlyList<GameAction> ledgerActions)
        {
            GUILayout.Label($"Ledger Actions ({ledgerActions.Count})", GUI.skin.box);

            // Sorted by UT descending (newest first)
            for (int i = ledgerActions.Count - 1; i >= 0; i--)
            {
                var action = ledgerActions[i];
                Color color = GameActionDisplay.GetColor(action.Type);
                GUIStyle style;
                if (color.g > 0.9f && color.r < 0.6f)
                    style = actionsGreenStyle;
                else if (color.r > 0.9f && color.g < 0.6f)
                    style = actionsRedStyle;
                else
                    style = actionsWhiteStyle;

                GUILayout.BeginHorizontal();

                string time = KSPUtil.PrintDateCompact(action.UT, true);
                GUILayout.Label(time, style, GUILayout.Width(90));

                string category = GameActionDisplay.GetCategory(action.Type);
                GUILayout.Label(category, style, GUILayout.Width(65));

                string desc = GameActionDisplay.GetDescription(action);
                GUILayout.Label(desc, style, GUILayout.ExpandWidth(true));

                GUILayout.EndHorizontal();
            }
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
                    GUILayout.Label(retiredKerbals[i], actionsGrayStyle);
                }
                GUILayout.EndVertical();
            }
            else if (lastRetiredKerbalCount > 0)
            {
                ParsekLog.Verbose("UI", "Retired kerbals list cleared");
                lastRetiredKerbalCount = 0;
            }
        }

        private void DrawActionsWindow(int windowID)
        {
            EnsureActionsStyles();

            // A. Resource Budget Summary
            DrawResourceBudget();

            // B. Recorded Actions List + C. Uncommitted events
            List<System.Tuple<GameStateEvent, bool>> allEvents;
            List<GameStateEvent> uncommittedEvents;
            BuildSortedActionEvents(actionsSortColumn, actionsSortAscending,
                out allEvents, out uncommittedEvents);

            // Single scroll view for all sections
            bool hasCommitted = allEvents.Count > 0;
            bool hasUncommitted = uncommittedEvents.Count > 0;
            var ledgerActions = Ledger.Actions;
            bool hasLedger = ledgerActions.Count > 0;

            if (hasLedger || hasCommitted || hasUncommitted)
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

                if (hasLedger)
                    DrawLedgerActionsSection(ledgerActions);

                // --- Recorded Actions (legacy game state events) ---
                if (hasCommitted)
                {
                    if (hasLedger)
                        GUILayout.Space(5);
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
                    if (hasCommitted || hasLedger)
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

            DrawRetiredKerbalsSection();

            // C. Bottom Bar — pinned to window bottom
            GUILayout.FlexibleSpace();

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

            DrawResizeHandle(actionsWindowRect, ref isResizingActionsWindow,
                "Actions window");

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

        /// <summary>
        /// Builds the sorted list of committed action events and the list of uncommitted events
        /// for the actions window.
        /// </summary>
        private static void BuildSortedActionEvents(
            ActionsSortColumn sortColumn, bool sortAscending,
            out List<System.Tuple<GameStateEvent, bool>> allEvents,
            out List<GameStateEvent> uncommittedEvents)
        {
            var milestones = MilestoneStore.Milestones;
            uint currentEpoch = MilestoneStore.CurrentEpoch;

            allEvents = new List<System.Tuple<GameStateEvent, bool>>();
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
            switch (sortColumn)
            {
                case ActionsSortColumn.Time:
                    allEvents.Sort((a, b) => sortAscending
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
                        return sortAscending ? cmp : -cmp;
                    });
                    break;
                case ActionsSortColumn.Description:
                    allEvents.Sort((a, b) =>
                    {
                        int cmp = string.Compare(
                            GameStateEventDisplay.GetDisplayDescription(a.Item1),
                            GameStateEventDisplay.GetDisplayDescription(b.Item1),
                            System.StringComparison.Ordinal);
                        return sortAscending ? cmp : -cmp;
                    });
                    break;
                case ActionsSortColumn.Status:
                    allEvents.Sort((a, b) =>
                    {
                        int cmp = a.Item2.CompareTo(b.Item2); // false (Pending) < true (Replayed)
                        return sortAscending ? cmp : -cmp;
                    });
                    break;
            }

            // Uncommitted events (not yet in any milestone)
            double lastMilestoneEndUT = 0;
            for (int i = 0; i < milestones.Count; i++)
            {
                if (milestones[i].Epoch == currentEpoch && milestones[i].EndUT > lastMilestoneEndUT)
                    lastMilestoneEndUT = milestones[i].EndUT;
            }

            var storeEvents = GameStateStore.Events;
            uncommittedEvents = new List<GameStateEvent>();
            for (int i = 0; i < storeEvents.Count; i++)
            {
                var e = storeEvents[i];
                if (e.epoch != currentEpoch) continue;
                if (e.ut <= lastMilestoneEndUT) continue;
                if (GameStateStore.IsMilestoneFilteredEvent(e.eventType)) continue;
                uncommittedEvents.Add(e);
            }
            uncommittedEvents.Sort((a, b) => a.ut.CompareTo(b.ut));
        }

        /// <summary>
        /// Builds the group tree data structures used to render the recordings tree.
        /// Pure data-computation — no IMGUI calls.
        /// </summary>
        internal static void BuildGroupTreeData(
            List<Recording> committed, int[] sortedIndices,
            List<string> knownEmptyGroups,
            out Dictionary<string, List<int>> grpToRecs,
            out Dictionary<string, List<int>> chainToRecs,
            out Dictionary<string, List<string>> grpChildren,
            out List<string> rootGrps,
            out HashSet<string> rootChainIds)
        {
            // group name → list of recording indices directly in that group
            grpToRecs = new Dictionary<string, List<int>>();
            // chainId → list of recording indices
            chainToRecs = new Dictionary<string, List<int>>();

            for (int row = 0; row < sortedIndices.Length; row++)
            {
                int ri = sortedIndices[row];
                var rec = committed[ri];

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

            // Build parent → children map from hierarchy
            grpChildren = new Dictionary<string, List<string>>();
            var allGrpNames = new HashSet<string>(grpToRecs.Keys);
            foreach (var kvp in GroupHierarchyStore.GroupParents)
            {
                allGrpNames.Add(kvp.Key);
                allGrpNames.Add(kvp.Value);
            }
            for (int i = 0; i < knownEmptyGroups.Count; i++)
                allGrpNames.Add(knownEmptyGroups[i]);

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
                ch.Sort(System.StringComparer.OrdinalIgnoreCase);

            // Root groups: in allGrpNames but not a child in groupParents
            rootGrps = new List<string>();
            foreach (var g in allGrpNames)
            {
                if (!GroupHierarchyStore.HasGroupParent(g))
                    rootGrps.Add(g);
            }
            rootGrps.Sort(System.StringComparer.OrdinalIgnoreCase);

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
                    1106, recHeight);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI", $"Recordings window initial position: x={recordingsWindowRect.x.ToString("F0", ic)} y={recordingsWindowRect.y.ToString("F0", ic)}");
            }

            HandleResizeDrag(ref recordingsWindowRect, ref isResizingRecordingsWindow,
                MinWindowWidth, MinWindowHeight, "Recordings window");

            EnsureOpaqueWindowStyle();
            recordingsWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekRecordings".GetHashCode(),
                recordingsWindowRect,
                DrawRecordingsWindow,
                "Parsek \u2014 Recordings",
                opaqueWindowStyle,
                GUILayout.Width(recordingsWindowRect.width),
                GUILayout.Height(recordingsWindowRect.height)
            );
            LogWindowPosition("Recordings", ref lastRecordingsWindowRect, recordingsWindowRect);

            // Group picker popup (rendered outside recordings window to avoid scroll clipping)
            DrawGroupPickerPopup();

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

        private void EnsureOpaqueWindowStyle()
        {
            if (opaqueWindowStyle != null) return;

            // Copy KSP's default window style, then make background textures opaque
            // while preserving all border/highlight details
            opaqueWindowStyle = new GUIStyle(GUI.skin.window);
            opaqueWindowStyle.normal.background = MakeOpaqueCopy(opaqueWindowStyle.normal.background);
            opaqueWindowStyle.onNormal.background = MakeOpaqueCopy(opaqueWindowStyle.onNormal.background);
            opaqueWindowStyle.focused.background = MakeOpaqueCopy(opaqueWindowStyle.focused.background);
            opaqueWindowStyle.onFocused.background = MakeOpaqueCopy(opaqueWindowStyle.onFocused.background);
            opaqueWindowStyle.active.background = MakeOpaqueCopy(opaqueWindowStyle.active.background);
            opaqueWindowStyle.onActive.background = MakeOpaqueCopy(opaqueWindowStyle.onActive.background);
            opaqueWindowStyle.hover.background = MakeOpaqueCopy(opaqueWindowStyle.hover.background);
            opaqueWindowStyle.onHover.background = MakeOpaqueCopy(opaqueWindowStyle.onHover.background);
        }

        /// <summary>
        /// Creates a readable copy of a texture with all pixel alpha set to 1.
        /// Uses RenderTexture blit to handle non-readable source textures (KSP skin).
        /// </summary>
        private static Texture2D MakeOpaqueCopy(Texture2D source)
        {
            if (source == null) return null;

            // Blit to RenderTexture (works even for non-readable textures)
            RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;

            Texture2D copy = new Texture2D(source.width, source.height, TextureFormat.ARGB32, false);
            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            // Max out alpha on all pixels
            Color[] pixels = copy.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
                pixels[i].a = 1f;
            copy.SetPixels(pixels);
            copy.Apply();
            copy.filterMode = source.filterMode;
            return copy;
        }

        /// <summary>
        /// Returns the opaque window style for use by external callers (ParsekFlight, ParsekKSC).
        /// </summary>
        public GUIStyle GetOpaqueWindowStyle()
        {
            EnsureOpaqueWindowStyle();
            return opaqueWindowStyle;
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

            phaseStyleApproach = new GUIStyle(GUI.skin.label);
            phaseStyleApproach.normal.textColor = new Color(0.4f, 0.7f, 1f); // sky blue
        }

        private void HandleRecordingsDefocus(List<Recording> committed)
        {
            // Click outside active rename field → commit and close
            if (Event.current.type == EventType.MouseDown &&
                (renamingRecordingIdx >= 0 || renamingGroup != null))
            {
                if (activeRenameRect.width > 0 && !activeRenameRect.Contains(Event.current.mousePosition))
                {
                    if (renamingRecordingIdx >= 0)
                        CommitRecordingRename(committed);
                    if (renamingGroup != null)
                        CommitGroupRename(renamingGroup);
                }
            }

            // Click outside active loop period field → commit and defocus
            if (Event.current.type == EventType.MouseDown && loopPeriodFocusedRi >= 0)
            {
                if (loopPeriodEditRect.width > 0 && !loopPeriodEditRect.Contains(Event.current.mousePosition))
                {
                    CommitLoopPeriodEdit(committed);
                }
            }
        }

        private void DrawRecordingsTableHeader(List<Recording> committed)
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
            DrawSortableHeader("Name", SortColumn.Name, 0, true);
            DrawSortableHeader("Phase", SortColumn.Phase, ColW_Phase);
            DrawSortableHeader("Launch", SortColumn.LaunchTime, ColW_Launch);
            DrawSortableHeader("Duration", SortColumn.Duration, ColW_Dur);

            if (showExpandedStats)
            {
                GUILayout.Label("MaxAlt", GUILayout.Width(ColW_MaxAlt));
                GUILayout.Label("MaxSpd", GUILayout.Width(ColW_MaxSpd));
                GUILayout.Label("Dist", GUILayout.Width(ColW_Dist));
                GUILayout.Label("Pts", GUILayout.Width(ColW_Pts));
            }

            DrawSortableHeader("Status", SortColumn.Status, ColW_Status);

            // Group column header
            GUILayout.Label("Group", GUILayout.Width(ColW_Group));

            // Select-all loop header + checkbox
            int loopCount = 0;
            for (int i = 0; i < committed.Count; i++)
                if (committed[i].LoopPlayback) loopCount++;

            bool allLoop = loopCount == committed.Count;
            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Loop));
            GUILayout.FlexibleSpace();
            GUILayout.Label("Loop\nGhost");
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
                "Loop interval between cycles.\nClick unit to cycle: sec \u2192 min \u2192 hr \u2192 auto.\n\"auto\" inherits from Settings > Looping."));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (InFlight)
                GUILayout.Label("Watch", GUILayout.Width(ColW_Watch));
            GUILayout.Label("Rewind\nF.Forward", GUILayout.Width(ColW_Rewind));

            // Hide column header + toggle
            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Hide));
            GUILayout.FlexibleSpace();
            GUILayout.Label("Hide");
            bool newHideActive = GUILayout.Toggle(GroupHierarchyStore.HideActive, "");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (newHideActive != GroupHierarchyStore.HideActive)
            {
                GroupHierarchyStore.HideActive = newHideActive;
                ParsekLog.Info("UI", $"Hide active toggled: {GroupHierarchyStore.HideActive}");
            }

            GUILayout.EndHorizontal();
        }

        private void DrawRecordingsBottomBar(List<Recording> committed)
        {
            // Bottom button bar — pinned to window bottom
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();

            if (committed.Count > 0)
            {
                string statsLabel = showExpandedStats ? "Stats \u25c0" : "Stats \u25b6";
                if (GUILayout.Button(statsLabel, GUILayout.Width(65)))
                {
                    showExpandedStats = !showExpandedStats;
                    ParsekLog.Verbose("UI", $"Recordings Stats toggled: {(showExpandedStats ? "expanded" : "collapsed")}");
                    if (showExpandedStats && recordingsWindowRect.width < 1324f)
                        recordingsWindowRect.width = 1324f;
                    else if (!showExpandedStats)
                        recordingsWindowRect.width = 1106f;
                }
            }

            if (GUILayout.Button("New Group", GUILayout.Width(80)))
            {
                string newName = GenerateUniqueGroupName();
                knownEmptyGroups.Add(newName);
                expandedGroups.Add(newName);
                renamingGroup = newName;
                renamingGroupText = newName;
                ParsekLog.Info("UI", $"Group '{newName}' created");
            }

            if (GUILayout.Button("Close"))
            {
                showRecordingsWindow = false;
                groupPopupOpen = false;
                ParsekLog.Verbose("UI", "Recordings window closed via button");
            }

            GUILayout.EndHorizontal();

            DrawResizeHandle(recordingsWindowRect, ref isResizingRecordingsWindow,
                "Recordings window");

            GUI.DragWindow();
        }

        private void DrawRecordingsWindow(int windowID)
        {
            var committed = RecordingStore.CommittedRecordings;
            double now = Planetarium.GetUniversalTime();

            HandleRecordingsDefocus(committed);

            EnsureStatusStyles();
            EnsurePhaseStyles();
            RebuildSortedIndices(committed, now);


            if (committed.Count == 0)
            {
                GUILayout.Label("No recordings.");
            }
            else
            {
                // Scrollable table body (header inside scroll view for guaranteed alignment)
                recordingsScrollPos = GUILayout.BeginScrollView(
                    recordingsScrollPos, false, true, GUILayout.ExpandHeight(true));

                DrawRecordingsTableHeader(committed);

                // Rebuild if a header click invalidated during this frame
                RebuildSortedIndices(committed, now);

                // ── Build group tree data ──────────────────────────────────
                Dictionary<string, List<int>> grpToRecs;
                Dictionary<string, List<int>> chainToRecs;
                Dictionary<string, List<string>> grpChildren;
                List<string> rootGrps;
                HashSet<string> rootChainIds;
                BuildGroupTreeData(committed, sortedIndices, knownEmptyGroups,
                    out grpToRecs, out chainToRecs, out grpChildren,
                    out rootGrps, out rootChainIds);

                // ── Build unified sorted root items ──────────────────────
                var rootItems = new List<RootDrawItem>();

                // Add root groups
                for (int g = 0; g < rootGrps.Count; g++)
                {
                    var desc = new HashSet<int>();
                    CollectDescendantRecordings(rootGrps[g], grpToRecs, grpChildren, desc);
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
                    // Skip recordings that belong to groups (drawn inside group trees)
                    if (rec.RecordingGroups != null && rec.RecordingGroups.Count > 0) continue;

                    if (!string.IsNullOrEmpty(rec.ChainId) && rootChainIds.Contains(rec.ChainId))
                    {
                        if (!seenChains.Add(rec.ChainId)) continue;
                        var members = chainToRecs[rec.ChainId];
                        rootItems.Add(new RootDrawItem
                        {
                            SortKey = GetChainSortKey(members, committed, sortColumn, now),
                            SortName = members.Count > 0 ? committed[members[0]].VesselName : "",
                            ItemType = RootItemType.Chain,
                            ChainId = rec.ChainId,
                            RecIdx = -1
                        });
                    }
                    else if (string.IsNullOrEmpty(rec.ChainId))
                    {
                        rootItems.Add(new RootDrawItem
                        {
                            SortKey = GetRecordingSortKey(rec, sortColumn, now, row),
                            SortName = string.IsNullOrEmpty(rec.VesselName) ? "Untitled" : rec.VesselName,
                            ItemType = RootItemType.Recording,
                            RecIdx = ri
                        });
                    }
                }

                // Sort root items
                var col = sortColumn;
                var asc = sortAscending;
                rootItems.Sort((a, b) =>
                {
                    int cmp;
                    if (col == SortColumn.Name || col == SortColumn.Phase)
                        cmp = string.Compare(a.SortName, b.SortName,
                            System.StringComparison.OrdinalIgnoreCase);
                    else
                        cmp = a.SortKey.CompareTo(b.SortKey);
                    return asc ? cmp : -cmp;
                });

                // ── Draw tree ─────────────────────────────────────────────
                bool deleted = false;

                for (int i = 0; i < rootItems.Count && !deleted; i++)
                {
                    var item = rootItems[i];
                    switch (item.ItemType)
                    {
                        case RootItemType.Group:
                            deleted = DrawGroupTree(item.GroupName, 0, committed, now,
                                grpToRecs, chainToRecs, grpChildren);
                            break;
                        case RootItemType.Chain:
                            deleted = DrawChainBlock(item.ChainId,
                                chainToRecs[item.ChainId], 0, committed, now);
                            break;
                        case RootItemType.Recording:
                            deleted = DrawRecordingRow(item.RecIdx, committed, now, 0f);
                            break;
                    }
                }

                GUILayout.EndScrollView();

                // Capture scroll view rect for tooltip visibility guard
                if (Event.current.type == EventType.Repaint)
                    scrollViewRect = GUILayoutUtility.GetLastRect();
            }

            DrawRecordingsBottomBar(committed);
        }

        /// <summary>
        /// Draws a single recording row. Returns true if the list was modified (break iteration).
        /// </summary>
        private bool DrawRecordingRow(int ri, List<Recording> committed, double now, float indentPx)
        {
            var rec = committed[ri];
            if (rec.Hidden && GroupHierarchyStore.HideActive) return false;
            GUILayout.BeginHorizontal();

            // Enable checkbox (always at column 0)
            bool enabled = GUILayout.Toggle(rec.PlaybackEnabled, "", GUILayout.Width(ColW_Enable));
            if (enabled != rec.PlaybackEnabled)
            {
                rec.PlaybackEnabled = enabled;
                ParsekLog.Info("UI", $"Recording '{rec.VesselName}' playback {(enabled ? "enabled" : "disabled")}" +
                    (!string.IsNullOrEmpty(rec.SegmentPhase) ? $" (segment: {RecordingStore.GetSegmentPhaseLabel(rec)})" : ""));
            }

            // #
            GUILayout.Label((ri + 1).ToString(), GUILayout.Width(ColW_Index));

            // Name (double-click to rename, deferred to next frame)
            DrawRecordingNameCell(ri, rec, committed, indentPx);

            // Phase label
            string phaseLabel = RecordingStore.GetSegmentPhaseLabel(rec);
            if (!string.IsNullOrEmpty(phaseLabel))
            {
                GUIStyle phaseStyle;
                if (rec.SegmentPhase == "atmo") phaseStyle = phaseStyleAtmo;
                else if (rec.SegmentPhase == "approach") phaseStyle = phaseStyleApproach;
                else if (rec.SegmentPhase == "space") phaseStyle = phaseStyleSpace;
                else phaseStyle = phaseStyleExo;
                GUILayout.Label(phaseLabel, phaseStyle, GUILayout.Width(ColW_Phase));
            }
            else
            {
                GUILayout.Label("", GUILayout.Width(ColW_Phase));
            }

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

            // Status (#98: merged countdown into status column)
            GUIStyle statusStyle;
            string statusText;
            if (now < rec.StartUT)
            {
                statusStyle = statusStyleFuture;
                statusText = rec.Points.Count > 0
                    ? SelectiveSpawnUI.FormatCountdown(rec.StartUT - now)
                    : "future";
            }
            else if (now <= rec.EndUT)
            {
                statusStyle = statusStyleActive;
                statusText = rec.Points.Count > 0
                    ? SelectiveSpawnUI.FormatCountdown(rec.StartUT - now)
                    : "active";
            }
            else
            {
                statusStyle = statusStylePast;
                if (rec.IsDebris)
                    statusText = "past";
                else if (rec.TerminalStateValue.HasValue)
                    statusText = rec.TerminalStateValue.Value.ToString();
                else if (rec.Points.Count > 0)
                    statusText = SelectiveSpawnUI.FormatCountdown(rec.StartUT - now);
                else
                    statusText = "past";
            }

            // Phase 6d-3: Chain status tooltip — show ghost chain info on hover
            string chainStatusTooltip = "";
            if (InFlight && flight != null)
            {
                string chainStatus = ParsekFlight.GetChainStatusForRecording(
                    flight.ActiveGhostChains, rec);
                if (chainStatus != null)
                    chainStatusTooltip = chainStatus;
            }
            var statusContent = new GUIContent(statusText, chainStatusTooltip);
            GUILayout.Label(statusContent, statusStyle, GUILayout.Width(ColW_Status));

            // Group assignment button
            if (GUILayout.Button("G", GUILayout.Width(ColW_Group)))
            {
                groupPopupPosition = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
                OpenGroupPopupForRecording(ri);
                ParsekLog.Verbose("UI", $"Group popup opened for recording index={ri} name='{rec.VesselName}'");
            }

            // Loop checkbox
            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Loop));
            GUILayout.FlexibleSpace();
            bool loop = GUILayout.Toggle(rec.LoopPlayback, "");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (loop != rec.LoopPlayback)
            {
                rec.LoopPlayback = loop;
                ApplyAutoLoopRange(rec, loop);
                if (!loop && loopPeriodFocusedRi == ri)
                    loopPeriodFocusedRi = -1;
                ParsekLog.Info("UI", $"Recording '{rec.VesselName}' loop playback set to {loop}");
            }

            // Period
            DrawLoopPeriodCell(rec, ri, dur);

            // Watch button (flight only)
            if (InFlight)
            {
                bool hasGhost = flight.HasActiveGhost(ri);
                bool sameBody = flight.IsGhostOnSameBody(ri);
                bool inRange = flight.IsGhostWithinVisualRange(ri);
                bool isWatching = flight.WatchedRecordingIndex == ri;
                bool canWatch = hasGhost && sameBody && inRange;

                GUI.enabled = canWatch;
                string watchLabel = isWatching ? "W*" : "W";
                string watchTooltip = (hasGhost && !sameBody) ? "Ghost is on a different body"
                    : (hasGhost && !inRange) ? "Ghost is beyond camera cutoff"
                    : "";
                var watchContent = new GUIContent(watchLabel, watchTooltip);
                if (GUILayout.Button(watchContent, GUILayout.Width(ColW_Watch)))
                {
                    ParsekLog.Info("UI", $"Recording #{ri} W button clicked: {(isWatching ? "exit" : "enter")} watch on \"{rec.VesselName}\"");
                    if (isWatching)
                        flight.ExitWatchMode();
                    else
                        flight.EnterWatchMode(ri);
                }
                GUI.enabled = true;
            }

            // Rewind / Fast-forward button
            {
                bool isFuture = now < rec.StartUT;
                bool isActive = now >= rec.StartUT && now <= rec.EndUT;
                bool hasRewindSave = !string.IsNullOrEmpty(rec.RewindSaveFileName);
                if (isFuture)
                {
                    // Future recording: FF button advances UT to recording start
                    string ffReason;
                    bool isRecording = InFlight && flight.IsRecording;
                    bool canFF = RecordingStore.CanFastForward(rec, out ffReason, isRecording: isRecording);
                    bool prevFF;
                    if (!lastCanFF.TryGetValue(ri, out prevFF) || prevFF != canFF)
                    {
                        lastCanFF[ri] = canFF;
                        ParsekLog.Verbose("UI", $"FF #{ri} \"{rec.VesselName}\": {(canFF ? "enabled" : "disabled — " + ffReason)}");
                    }
                    GUI.enabled = canFF;
                    string tooltip = canFF
                        ? "Fast-forward to this launch"
                        : ffReason;
                    if (GUILayout.Button(new GUIContent("FF", tooltip), GUILayout.Width(ColW_Rewind)))
                    {
                        ParsekLog.Info("UI", $"FF button clicked: #{ri} \"{rec.VesselName}\"");
                        ShowFastForwardConfirmation(rec);
                    }
                    GUI.enabled = true;
                }
                else if (hasRewindSave)
                {
                    // Past/active recording with save: R button loads quicksave
                    string rewindReason;
                    bool isRecording = InFlight && flight.IsRecording;
                    bool canRewind = RecordingStore.CanRewind(rec, out rewindReason, isRecording: isRecording);
                    bool prevR;
                    if (!lastCanRewind.TryGetValue(ri, out prevR) || prevR != canRewind)
                    {
                        lastCanRewind[ri] = canRewind;
                        ParsekLog.Verbose("UI", $"R #{ri} \"{rec.VesselName}\": {(canRewind ? "enabled" : "disabled — " + rewindReason)}");
                    }
                    GUI.enabled = canRewind;
                    string tooltip = canRewind
                        ? "Rewind to this launch"
                        : rewindReason;
                    if (GUILayout.Button(new GUIContent("R", tooltip), GUILayout.Width(ColW_Rewind)))
                    {
                        ParsekLog.Info("UI", $"Rewind button clicked: #{ri} \"{rec.VesselName}\"");
                        ShowRewindConfirmation(rec);
                    }
                    GUI.enabled = true;
                }
                else
                {
                    GUILayout.Label("", GUILayout.Width(ColW_Rewind));
                }
            }

            // Hide checkbox
            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Hide));
            GUILayout.FlexibleSpace();
            bool hidden = GUILayout.Toggle(rec.Hidden, "");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (hidden != rec.Hidden)
            {
                rec.Hidden = hidden;
                ParsekLog.Info("UI", $"Recording '{rec.VesselName}' hidden={hidden}");
            }

            GUILayout.EndHorizontal();

            return false;
        }

        /// <summary>
        /// Draws the Name column cell for a recording row, handling indent,
        /// inline rename text field, double-click-to-rename, and auto-focus.
        /// </summary>
        private void DrawRecordingNameCell(int ri, Recording rec,
            List<Recording> committed, float indentPx)
        {
            // Indent inside Name column for grouped/chained recordings
            if (indentPx > 0f) GUILayout.Space(indentPx);
            string name = string.IsNullOrEmpty(rec.VesselName) ? "Untitled" : rec.VesselName;
            if (renamingRecordingIdx == ri)
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
                    renamingRecordingIdx = -1;
                    activeRenameRect = default;
                    Event.current.Use();
                }
            }
            else
            {
                if (GUILayout.Button(name, GUI.skin.label, GUILayout.ExpandWidth(true)))
                {
                    float now2 = Time.realtimeSinceStartup;
                    if (lastClickedRecIdx == ri && now2 - lastClickTime < DoubleClickThreshold)
                    {
                        // Commit any active rename first
                        if (renamingRecordingIdx >= 0)
                            CommitRecordingRename(committed);
                        if (renamingGroup != null)
                            CommitGroupRename(renamingGroup);
                        renamingRecordingIdx = ri;
                        renamingRecordingText = rec.VesselName ?? "";
                        renamingRecordingFocused = false;
                        lastClickedRecIdx = -1;
                    }
                    else
                    {
                        lastClickedRecIdx = ri;
                        lastClickTime = now2;
                    }
                }
            }
        }

        // ─── Group tree rendering helpers ─────────────────────────────────

        /// <summary>
        /// Recursively draws a group and its children. Returns true if the recording list was modified.
        /// </summary>
        private bool DrawGroupTree(string groupName, int depth,
            List<Recording> committed, double now,
            Dictionary<string, List<int>> grpToRecs,
            Dictionary<string, List<int>> chainToRecs,
            Dictionary<string, List<string>> grpChildren)
        {
            // Skip hidden groups when hide is active
            if (GroupHierarchyStore.HideActive && GroupHierarchyStore.IsGroupHidden(groupName))
                return false;

            // Collect unique descendant recordings for aggregate controls
            var descendants = new HashSet<int>();
            CollectDescendantRecordings(groupName, grpToRecs, grpChildren, descendants);
            int memberCount = descendants.Count;

            float indent = depth * 15f;

            // ── Group header ──
            GUILayout.BeginHorizontal();

            // Enable checkbox (always at column 0)
            int enabledCount = 0;
            foreach (int idx in descendants)
                if (committed[idx].PlaybackEnabled) enabledCount++;
            bool allEnabled = memberCount > 0 && enabledCount == memberCount;
            bool newEnabled = GUILayout.Toggle(allEnabled, "", GUILayout.Width(ColW_Enable));
            if (newEnabled != allEnabled)
            {
                foreach (int idx in descendants)
                    committed[idx].PlaybackEnabled = newEnabled;
                ParsekLog.Info("UI", $"Set playback enabled for group '{groupName}': enabled={newEnabled}");
            }

            // Spacer for # column
            GUILayout.Space(ColW_Index);

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
                if (GUILayout.Button($"{arrow} {groupName} ({memberCount})",
                    GUI.skin.label, GUILayout.ExpandWidth(true)))
                {
                    float t = Time.realtimeSinceStartup;
                    if (lastClickedGroup == groupName && t - lastGroupClickTime < DoubleClickThreshold)
                    {
                        // Commit any active rename first
                        if (renamingRecordingIdx >= 0)
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
            GUILayout.Label("", GUILayout.Width(ColW_Phase));

            // Launch time (earliest among descendants)
            double grpEarliest = GetGroupEarliestStartUT(descendants, committed);
            string grpLaunchText = (memberCount > 0 && grpEarliest < double.MaxValue)
                ? KSPUtil.PrintDateCompact(grpEarliest, true)
                : "-";
            GUILayout.Label(grpLaunchText, GUILayout.Width(ColW_Launch));

            // Duration (sum of descendant durations)
            double grpTotalDur = GetGroupTotalDuration(descendants, committed);
            GUILayout.Label(FormatDuration(grpTotalDur), GUILayout.Width(ColW_Dur));

            // Expanded stats spacers
            if (showExpandedStats)
            {
                GUILayout.Label("", GUILayout.Width(ColW_MaxAlt));
                GUILayout.Label("", GUILayout.Width(ColW_MaxSpd));
                GUILayout.Label("", GUILayout.Width(ColW_Dist));
                GUILayout.Label("", GUILayout.Width(ColW_Pts));
            }

            // Status (closest active T- among descendants)
            string grpStatusText;
            int grpStatusOrder;
            GetGroupStatus(descendants, committed, now, out grpStatusText, out grpStatusOrder);
            GUIStyle grpStatusStyle = grpStatusOrder == 0 ? statusStyleFuture
                : grpStatusOrder == 1 ? statusStyleActive
                : statusStylePast;
            GUILayout.Label(grpStatusText, grpStatusStyle, GUILayout.Width(ColW_Status));

            // Group management buttons: G (assign parent) + X (disband) — share ColW_Group width
            float halfGroup = (ColW_Group - 4f) * 0.5f; // 4px for spacing between buttons
            if (GUILayout.Button("G", GUILayout.Width(halfGroup)))
            {
                groupPopupPosition = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
                OpenGroupPopupForGroup(groupName);
                ParsekLog.Verbose("UI", $"Group popup opened for group '{groupName}'");
            }
            if (GUILayout.Button("X", GUILayout.Width(halfGroup)))
            {
                ShowDisbandGroupConfirmation(groupName, descendants, grpChildren);
                ParsekLog.Verbose("UI", $"Disband clicked for group '{groupName}'");
            }

            // Loop checkbox (aggregate)
            int loopCount = 0;
            foreach (int idx in descendants)
                if (committed[idx].LoopPlayback) loopCount++;
            bool allLoop = memberCount > 0 && loopCount == memberCount;
            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Loop));
            GUILayout.FlexibleSpace();
            bool newLoop = GUILayout.Toggle(allLoop, "");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (newLoop != allLoop)
            {
                foreach (int idx in descendants)
                    committed[idx].LoopPlayback = newLoop;
                ParsekLog.Info("UI", $"Group '{groupName}' loop set to {newLoop} ({descendants.Count} recordings)");
            }

            // Period placeholder
            GUILayout.Label("", GUILayout.Width(ColW_Period));

            // Find the "main" (earliest non-debris) recording for group-level W and R/FF buttons
            int mainIdx = FindGroupMainRecordingIndex(descendants, committed);

            // Watch button (flight only) — targets main recording's ghost
            if (InFlight)
            {
                if (mainIdx >= 0)
                {
                    bool hasGhost = flight.HasActiveGhost(mainIdx);
                    bool sameBody = flight.IsGhostOnSameBody(mainIdx);
                    bool inRange = flight.IsGhostWithinVisualRange(mainIdx);
                    bool isWatching = flight.WatchedRecordingIndex == mainIdx;
                    bool canWatch = hasGhost && sameBody && inRange;

                    GUI.enabled = canWatch;
                    string watchLabel = isWatching ? "W*" : "W";
                    string watchTooltip = (hasGhost && !sameBody) ? "Ghost is on a different body"
                        : (hasGhost && !inRange) ? "Ghost is beyond visual range"
                        : "";
                    if (GUILayout.Button(new GUIContent(watchLabel, watchTooltip), GUILayout.Width(ColW_Watch)))
                    {
                        if (isWatching)
                            flight.ExitWatchMode();
                        else
                            flight.EnterWatchMode(mainIdx);
                        ParsekLog.Info("UI", $"Group '{groupName}' W button: {(isWatching ? "exit" : "enter")} watch on #{mainIdx} \"{committed[mainIdx].VesselName}\"");
                    }
                    GUI.enabled = true;
                }
                else
                {
                    GUILayout.Label("", GUILayout.Width(ColW_Watch));
                }
            }

            // Rewind / Fast-forward button — targets main recording
            // (per-recording row already logs enable/disable transitions for the same recording)
            if (mainIdx >= 0)
            {
                var mainRec = committed[mainIdx];
                bool isFuture = now < mainRec.StartUT;
                bool hasRewindSave = !string.IsNullOrEmpty(mainRec.RewindSaveFileName);
                bool isRecording = InFlight && flight.IsRecording;

                if (isFuture)
                {
                    string ffReason;
                    bool canFF = RecordingStore.CanFastForward(mainRec, out ffReason, isRecording: isRecording);
                    GUI.enabled = canFF;
                    string tooltip = canFF ? "Fast-forward to this launch" : ffReason;
                    if (GUILayout.Button(new GUIContent("FF", tooltip), GUILayout.Width(ColW_Rewind)))
                    {
                        ParsekLog.Info("UI", $"Group '{groupName}' FF button: #{mainIdx} \"{mainRec.VesselName}\"");
                        ShowFastForwardConfirmation(mainRec);
                    }
                    GUI.enabled = true;
                }
                else if (hasRewindSave)
                {
                    string rewindReason;
                    bool canRewind = RecordingStore.CanRewind(mainRec, out rewindReason, isRecording: isRecording);
                    GUI.enabled = canRewind;
                    string tooltip = canRewind ? "Rewind to this launch" : rewindReason;
                    if (GUILayout.Button(new GUIContent("R", tooltip), GUILayout.Width(ColW_Rewind)))
                    {
                        ParsekLog.Info("UI", $"Group '{groupName}' R button: #{mainIdx} \"{mainRec.VesselName}\"");
                        ShowRewindConfirmation(mainRec);
                    }
                    GUI.enabled = true;
                }
                else
                {
                    GUILayout.Label("", GUILayout.Width(ColW_Rewind));
                }
            }
            else
            {
                GUILayout.Label("", GUILayout.Width(ColW_Rewind));
            }

            // Hide group checkbox
            bool groupHidden = GroupHierarchyStore.IsGroupHidden(groupName);
            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Hide));
            GUILayout.FlexibleSpace();
            bool newGroupHidden = GUILayout.Toggle(groupHidden, "");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (newGroupHidden != groupHidden)
            {
                if (newGroupHidden)
                    GroupHierarchyStore.AddHiddenGroup(groupName);
                else
                    GroupHierarchyStore.RemoveHiddenGroup(groupName);
                ParsekLog.Info("UI", $"Group '{groupName}' hidden={newGroupHidden}");
            }

            GUILayout.EndHorizontal();

            if (!expanded) return false;

            // ── Draw children ──
            List<int> directMembers;
            grpToRecs.TryGetValue(groupName, out directMembers);

            if (directMembers != null)
            {
                // Separate chained vs standalone within this group
                var chainsInGrp = new HashSet<string>();
                var standaloneInGrp = new List<int>();

                for (int i = 0; i < directMembers.Count; i++)
                {
                    int ri = directMembers[i];
                    var rec = committed[ri];
                    if (!string.IsNullOrEmpty(rec.ChainId))
                        chainsInGrp.Add(rec.ChainId);
                    else
                        standaloneInGrp.Add(ri);
                }

                // Draw chains within this group
                var drawnChains = new HashSet<string>();
                for (int i = 0; i < directMembers.Count; i++)
                {
                    var rec = committed[directMembers[i]];
                    if (!string.IsNullOrEmpty(rec.ChainId) && drawnChains.Add(rec.ChainId))
                    {
                        List<int> fullChain;
                        if (chainToRecs.TryGetValue(rec.ChainId, out fullChain))
                        {
                            if (DrawChainBlock(rec.ChainId, fullChain, depth + 1, committed, now))
                                return true;
                        }
                    }
                }

                // Draw standalone recordings
                for (int i = 0; i < standaloneInGrp.Count; i++)
                {
                    if (DrawRecordingRow(standaloneInGrp[i], committed, now, (depth + 1) * 15f))
                        return true;
                }
            }

            // Draw child groups
            List<string> children;
            if (grpChildren.TryGetValue(groupName, out children))
            {
                for (int c = 0; c < children.Count; c++)
                {
                    if (DrawGroupTree(children[c], depth + 1, committed, now,
                        grpToRecs, chainToRecs, grpChildren))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Draws a chain block (header + members). Returns true if the recording list was modified.
        /// </summary>
        private bool DrawChainBlock(string chainId, List<int> members, int depth,
            List<Recording> committed, double now)
        {
            if (GroupHierarchyStore.HideActive)
            {
                bool anyVisible = false;
                for (int m = 0; m < members.Count; m++)
                    if (!committed[members[m]].Hidden) { anyVisible = true; break; }
                if (!anyVisible) return false;
            }

            float indent = depth * 15f;

            GUILayout.BeginHorizontal();

            // Enable checkbox (aggregate over chain members)
            int chainEnabledCount = 0;
            for (int m = 0; m < members.Count; m++)
                if (committed[members[m]].PlaybackEnabled) chainEnabledCount++;
            bool chainAllEnabled = members.Count > 0 && chainEnabledCount == members.Count;
            bool chainNewEnabled = GUILayout.Toggle(chainAllEnabled, "", GUILayout.Width(ColW_Enable));
            if (chainNewEnabled != chainAllEnabled)
            {
                for (int m = 0; m < members.Count; m++)
                    committed[members[m]].PlaybackEnabled = chainNewEnabled;
                ParsekLog.Info("UI", $"Chain '{chainId}' playback set to {chainNewEnabled} ({members.Count} segments)");
            }

            GUILayout.Space(ColW_Index);

            // Indent inside Name column for chains in sub-groups
            if (indent > 0f) GUILayout.Space(indent);

            bool expanded = expandedChains.Contains(chainId);
            string arrow = expanded ? "\u25bc" : "\u25b6";
            string chainName = committed[members[0]].VesselName;
            if (string.IsNullOrEmpty(chainName)) chainName = "Chain";

            double chainStart = double.MaxValue, chainEnd = double.MinValue;
            for (int m = 0; m < members.Count; m++)
            {
                var mr = committed[members[m]];
                if (mr.StartUT < chainStart) chainStart = mr.StartUT;
                if (mr.EndUT > chainEnd) chainEnd = mr.EndUT;
            }

            if (GUILayout.Button($"{arrow} {chainName} ({members.Count})",
                GUI.skin.label, GUILayout.ExpandWidth(true)))
            {
                if (expanded) expandedChains.Remove(chainId);
                else expandedChains.Add(chainId);
                ParsekLog.Verbose("UI", $"Chain '{chainName}' {(expanded ? "collapsed" : "expanded")} ({members.Count} segments)");
            }

            // Phase placeholder (chains have no single phase)
            GUILayout.Label("", GUILayout.Width(ColW_Phase));

            // Launch time (earliest among chain members)
            GUILayout.Label(chainStart < double.MaxValue
                ? KSPUtil.PrintDateCompact(chainStart, true) : "-",
                GUILayout.Width(ColW_Launch));

            // Duration (total span)
            GUILayout.Label(FormatDuration(chainEnd - chainStart), GUILayout.Width(ColW_Dur));

            // Expanded stats spacers
            if (showExpandedStats)
            {
                GUILayout.Label("", GUILayout.Width(ColW_MaxAlt));
                GUILayout.Label("", GUILayout.Width(ColW_MaxSpd));
                GUILayout.Label("", GUILayout.Width(ColW_Dist));
                GUILayout.Label("", GUILayout.Width(ColW_Pts));
            }

            // Status (closest active among chain members)
            string chainStatusText;
            int chainStatusOrder;
            GetChainStatus(members, committed, now, out chainStatusText, out chainStatusOrder);
            GUIStyle chainStatusStyle = chainStatusOrder == 0 ? statusStyleFuture
                : chainStatusOrder == 1 ? statusStyleActive
                : statusStylePast;
            GUILayout.Label(chainStatusText, chainStatusStyle, GUILayout.Width(ColW_Status));

            // Chain G button (share ColW_Group width with X placeholder)
            float halfGroup = (ColW_Group - 4f) * 0.5f;
            if (GUILayout.Button("G", GUILayout.Width(halfGroup)))
            {
                groupPopupPosition = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
                OpenGroupPopupForChain(chainId);
                ParsekLog.Verbose("UI", $"Group popup opened for chain '{chainName}'");
            }
            GUILayout.Space(halfGroup + 4f);

            // Loop checkbox (aggregate over chain members)
            int chainLoopCount = 0;
            for (int m = 0; m < members.Count; m++)
                if (committed[members[m]].LoopPlayback) chainLoopCount++;
            bool chainAllLoop = members.Count > 0 && chainLoopCount == members.Count;
            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Loop));
            GUILayout.FlexibleSpace();
            bool chainNewLoop = GUILayout.Toggle(chainAllLoop, "");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (chainNewLoop != chainAllLoop)
            {
                for (int m = 0; m < members.Count; m++)
                {
                    committed[members[m]].LoopPlayback = chainNewLoop;
                    ApplyAutoLoopRange(committed[members[m]], chainNewLoop);
                }
                ParsekLog.Info("UI", $"Chain '{chainId}' loop set to {chainNewLoop} ({members.Count} segments)");
            }
            GUILayout.Label("", GUILayout.Width(ColW_Period));
            if (InFlight) GUILayout.Label("", GUILayout.Width(ColW_Watch));
            GUILayout.Label("", GUILayout.Width(ColW_Rewind));
            GUILayout.Label("", GUILayout.Width(ColW_Hide));

            GUILayout.EndHorizontal();

            if (expanded)
            {
                for (int m = 0; m < members.Count; m++)
                {
                    if (DrawRecordingRow(members[m], committed, now, (depth + 1) * 15f))
                        return true;
                }
            }
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

        private void CommitRecordingRename(List<Recording> committed)
        {
            int ri = renamingRecordingIdx;
            renamingRecordingIdx = -1;
            activeRenameRect = default;
            if (ri < 0 || ri >= committed.Count) return;

            string trimmed = renamingRecordingText.Trim();
            if (!string.IsNullOrEmpty(trimmed) && trimmed != committed[ri].VesselName)
            {
                ParsekLog.Info("UI", $"Recording '{committed[ri].VesselName}' renamed to '{trimmed}'");
                committed[ri].VesselName = trimmed;
            }
        }

        private void CommitGroupRename(string oldName)
        {
            string newName = renamingGroupText.Trim();
            renamingGroup = null;
            activeRenameRect = default;

            if (string.IsNullOrEmpty(newName) || newName == oldName) return;

            if (RecordingStore.IsInvalidGroupName(newName))
            {
                ParsekLog.Warn("UI", $"Group rename rejected: '{newName}' contains invalid characters");
                return;
            }

            // Apply rename in recordings and hierarchy
            if (!RecordingStore.RenameGroup(oldName, newName))
            {
                ParsekLog.Warn("UI", $"Group rename rejected: '{newName}' already exists");
                return;
            }

            GroupHierarchyStore.RenameGroupInHierarchy(oldName, newName);

            // Update expansion state
            if (expandedGroups.Remove(oldName))
                expandedGroups.Add(newName);

            // Update knownEmptyGroups
            int emptyIdx = knownEmptyGroups.IndexOf(oldName);
            if (emptyIdx >= 0) knownEmptyGroups[emptyIdx] = newName;

            ParsekLog.Info("UI", $"Group '{oldName}' renamed to '{newName}'");
        }

        private string GenerateUniqueGroupName()
        {
            var existing = RecordingStore.GetGroupNames();
            for (int i = 0; i < knownEmptyGroups.Count; i++)
                if (!existing.Contains(knownEmptyGroups[i]))
                    existing.Add(knownEmptyGroups[i]);

            for (int n = 1; ; n++)
            {
                string name = "Group " + n;
                if (!existing.Contains(name)) return name;
            }
        }

        private void ShowDisbandGroupConfirmation(string groupName,
            HashSet<int> descendants,
            Dictionary<string, List<string>> grpChildren)
        {
            int recCount = descendants.Count;
            List<string> children;
            grpChildren.TryGetValue(groupName, out children);
            int childCount = children != null ? children.Count : 0;

            // Determine parent group (null = root)
            string parentName;
            GroupHierarchyStore.TryGetGroupParent(groupName, out parentName);

            string subText = "";
            if (childCount > 0)
            {
                string dest = parentName != null ? $"move under \"{parentName}\"" : "become top-level";
                subText = $"\n{childCount} sub-group(s) will {dest}.";
            }
            string recDest = parentName != null ? $"moved to \"{parentName}\"" : "become standalone";
            string msg = $"Disband group \"{groupName}\"?\n\n" +
                $"{recCount} recording(s) will be {recDest}.{subText}\n\n" +
                "No recordings are deleted.";

            string capturedName = groupName;
            string capturedParent = parentName;
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new MultiOptionDialog("ParsekDisbandGroupConfirm", msg,
                    "Confirm Disband Group", HighLogic.UISkin,
                    new DialogGUIButton("Disband Group", () =>
                    {
                        int updated = RecordingStore.ReplaceGroupOnAll(capturedName, capturedParent);
                        GroupHierarchyStore.RemoveGroupFromHierarchy(capturedName);
                        knownEmptyGroups.Remove(capturedName);
                        expandedGroups.Remove(capturedName);
                        ParsekLog.Info("UI", $"Group '{capturedName}' disbanded ({updated} recordings moved to '{capturedParent ?? "(standalone)"}')");
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Verbose("UI", $"Disband group '{capturedName}' cancelled");
                    })
                ), false, HighLogic.UISkin);
        }

        // ─── Group picker popup ───────────────────────────────────────

        private void OpenGroupPopupForRecording(int ri)
        {
            var rec = RecordingStore.CommittedRecordings[ri];
            groupPopupOpen = true;
            groupPopupRecIdx = ri;
            groupPopupChainId = null;
            groupPopupGroup = null;
            groupPopupChecked = rec.RecordingGroups != null
                ? new HashSet<string>(rec.RecordingGroups) : new HashSet<string>();
            groupPopupOriginal = new HashSet<string>(groupPopupChecked);
            groupPopupNewName = "";
            InitGroupPopupExpansion();
        }

        private void OpenGroupPopupForChain(string chainId)
        {
            groupPopupOpen = true;
            groupPopupRecIdx = -1;
            groupPopupChainId = chainId;
            groupPopupGroup = null;
            // Checked = groups that ALL chain members are in (single pass to find members, then check)
            var committed = RecordingStore.CommittedRecordings;
            var memberIndices = RecordingStore.GetChainMemberIndices(chainId);
            var allGroups = RecordingStore.GetGroupNames();
            groupPopupChecked = new HashSet<string>();
            for (int g = 0; g < allGroups.Count; g++)
            {
                bool allIn = true;
                for (int m = 0; m < memberIndices.Count; m++)
                {
                    var rec = committed[memberIndices[m]];
                    if (rec.RecordingGroups == null || !rec.RecordingGroups.Contains(allGroups[g]))
                    { allIn = false; break; }
                }
                if (allIn && memberIndices.Count > 0) groupPopupChecked.Add(allGroups[g]);
            }
            groupPopupOriginal = new HashSet<string>(groupPopupChecked);
            groupPopupNewName = "";
            InitGroupPopupExpansion();
        }

        private void OpenGroupPopupForGroup(string groupName)
        {
            groupPopupOpen = true;
            groupPopupRecIdx = -1;
            groupPopupChainId = null;
            groupPopupGroup = groupName;
            // For group-in-group: checked = current parent (if any)
            groupPopupChecked = new HashSet<string>();
            string parent;
            if (GroupHierarchyStore.TryGetGroupParent(groupName, out parent))
                groupPopupChecked.Add(parent);
            groupPopupOriginal = new HashSet<string>(groupPopupChecked);
            groupPopupNewName = "";
            InitGroupPopupExpansion();
        }

        private void InitGroupPopupExpansion()
        {
            // Reset popup rect so it repositions near the clicked G button
            groupPopupRect = new Rect(0, 0, 0, 0);
            isResizingGroupPopup = false;
            groupPopupExpanded = new HashSet<string>();
            // Default: all groups expanded
            foreach (var kvp in GroupHierarchyStore.GroupParents)
            {
                groupPopupExpanded.Add(kvp.Value);
            }
            var allNames = RecordingStore.GetGroupNames();
            for (int i = 0; i < allNames.Count; i++)
                groupPopupExpanded.Add(allNames[i]);
            for (int i = 0; i < knownEmptyGroups.Count; i++)
                groupPopupExpanded.Add(knownEmptyGroups[i]);
            groupPopupScrollPos = Vector2.zero;
        }

        /// <summary>
        /// Draws the group picker popup. Called from OnGUI or after the recordings window.
        /// </summary>
        private void DrawGroupPickerPopup()
        {
            if (!groupPopupOpen) return;

            // Collect all group names
            var allNames = new HashSet<string>(RecordingStore.GetGroupNames());
            foreach (var kvp in GroupHierarchyStore.GroupParents)
            {
                allNames.Add(kvp.Key);
                allNames.Add(kvp.Value);
            }
            for (int i = 0; i < knownEmptyGroups.Count; i++)
                allNames.Add(knownEmptyGroups[i]);

            // For group-in-group popup: determine which groups are cycle-invalid
            HashSet<string> cycleInvalid = null;
            if (groupPopupGroup != null)
            {
                cycleInvalid = new HashSet<string>();
                cycleInvalid.Add(groupPopupGroup);
                var desc = GroupHierarchyStore.GetDescendantGroups(groupPopupGroup);
                for (int i = 0; i < desc.Count; i++)
                    cycleInvalid.Add(desc[i]);
            }

            // Build hierarchy for display
            var parentToChildren = new Dictionary<string, List<string>>();
            foreach (var kvp in GroupHierarchyStore.GroupParents)
            {
                List<string> ch;
                if (!parentToChildren.TryGetValue(kvp.Value, out ch))
                {
                    ch = new List<string>();
                    parentToChildren[kvp.Value] = ch;
                }
                ch.Add(kvp.Key);
            }
            foreach (var ch in parentToChildren.Values)
                ch.Sort(System.StringComparer.OrdinalIgnoreCase);

            var rootNames = new List<string>();
            foreach (var n in allNames)
            {
                if (!GroupHierarchyStore.HasGroupParent(n))
                    rootNames.Add(n);
            }
            rootNames.Sort(System.StringComparer.OrdinalIgnoreCase);

            HandleResizeDrag(ref groupPopupRect, ref isResizingGroupPopup,
                GroupPopupMinW, GroupPopupMinH, null);

            // Initialize popup rect on first open
            if (groupPopupRect.width < 1f)
            {
                groupPopupRect = new Rect(
                    Mathf.Clamp(groupPopupPosition.x, 0, Screen.width - 280f),
                    Mathf.Clamp(groupPopupPosition.y, 0, Screen.height - 300f),
                    280f, 300f);
            }

            bool isGroupPopup = groupPopupGroup != null;
            string popupTitle = isGroupPopup ? "Set Parent Group" : "Manage Groups";

            EnsureOpaqueWindowStyle();
            groupPopupRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekGroupPopup".GetHashCode(),
                groupPopupRect,
                (id) => DrawGroupPopupContents(rootNames, parentToChildren, cycleInvalid, allNames, isGroupPopup),
                popupTitle,
                opaqueWindowStyle,
                GUILayout.Width(groupPopupRect.width),
                GUILayout.Height(groupPopupRect.height));
        }

        private void DrawGroupPopupContents(List<string> rootNames,
            Dictionary<string, List<string>> parentToChildren,
            HashSet<string> cycleInvalid, HashSet<string> allNames, bool isGroupPopup)
        {
            groupPopupScrollPos = GUILayout.BeginScrollView(groupPopupScrollPos, GUILayout.ExpandHeight(true));

            // For group-in-group: add "(None / Root)" option
            if (isGroupPopup)
            {
                bool noneChecked = groupPopupChecked.Count == 0;
                bool newNone = GUILayout.Toggle(noneChecked, "(None / Root level)");
                if (newNone && !noneChecked)
                    groupPopupChecked.Clear();
            }

            // Draw group hierarchy with checkboxes
            for (int r = 0; r < rootNames.Count; r++)
                DrawGroupPopupNode(rootNames[r], 0, parentToChildren, cycleInvalid, isGroupPopup);

            GUILayout.EndScrollView();

            GUILayout.Space(3);

            // New group creation
            GUILayout.BeginHorizontal();
            groupPopupNewName = GUILayout.TextField(groupPopupNewName, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("+", GUILayout.Width(25)))
            {
                string newName = groupPopupNewName.Trim();
                if (!RecordingStore.IsInvalidGroupName(newName) &&
                    !allNames.Contains(newName) && !knownEmptyGroups.Contains(newName))
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
            if (GUILayout.Button("OK", GUILayout.Width(60)))
            {
                ApplyGroupPopupChanges();
                groupPopupOpen = false;
            }
            if (GUILayout.Button("Cancel", GUILayout.Width(60)))
            {
                groupPopupOpen = false;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            DrawResizeHandle(groupPopupRect, ref isResizingGroupPopup, null);

            GUI.DragWindow();
        }

        private void DrawGroupPopupNode(string groupName, int depth,
            Dictionary<string, List<string>> parentToChildren,
            HashSet<string> cycleInvalid, bool singleSelect)
        {
            // Skip self + all descendants (can't assign a group to itself or its children)
            if (cycleInvalid != null && cycleInvalid.Contains(groupName))
                return;

            List<string> children;
            bool hasChildren = parentToChildren.TryGetValue(groupName, out children) && children.Count > 0;

            GUILayout.BeginHorizontal();
            if (depth > 0) GUILayout.Space(depth * 12f);

            bool isChecked = groupPopupChecked.Contains(groupName);
            bool newChecked = GUILayout.Toggle(isChecked, "", GUILayout.Width(20));
            if (newChecked != isChecked)
            {
                if (singleSelect)
                {
                    groupPopupChecked.Clear();
                    if (newChecked) groupPopupChecked.Add(groupName);
                }
                else
                {
                    if (newChecked) groupPopupChecked.Add(groupName);
                    else groupPopupChecked.Remove(groupName);
                }
            }

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

            GUILayout.Label(groupName, GUILayout.ExpandWidth(true));

            GUILayout.EndHorizontal();

            // Draw children if expanded
            if (hasChildren && groupPopupExpanded.Contains(groupName))
            {
                for (int c = 0; c < children.Count; c++)
                    DrawGroupPopupNode(children[c], depth + 1, parentToChildren, cycleInvalid, singleSelect);
            }
        }

        private void ApplyGroupPopupChanges()
        {
            var committed = RecordingStore.CommittedRecordings;

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
                var added = new HashSet<string>(groupPopupChecked);
                added.ExceptWith(groupPopupOriginal);
                var removed = new HashSet<string>(groupPopupOriginal);
                removed.ExceptWith(groupPopupChecked);

                foreach (var g in added)
                    RecordingStore.AddChainToGroup(groupPopupChainId, g);
                foreach (var g in removed)
                    RecordingStore.RemoveChainFromGroup(groupPopupChainId, g);
                ParsekLog.Info("UI", $"Chain '{groupPopupChainId}' groups changed: +[{string.Join(", ", added)}] -[{string.Join(", ", removed)}]");
            }
            else if (groupPopupRecIdx >= 0 && groupPopupRecIdx < committed.Count)
            {
                // Recording: add/remove groups
                var added = new HashSet<string>(groupPopupChecked);
                added.ExceptWith(groupPopupOriginal);
                var removed = new HashSet<string>(groupPopupOriginal);
                removed.ExceptWith(groupPopupChecked);

                foreach (var g in added)
                    RecordingStore.AddRecordingToGroup(groupPopupRecIdx, g);
                foreach (var g in removed)
                    RecordingStore.RemoveRecordingFromGroup(groupPopupRecIdx, g);
                ParsekLog.Info("UI", $"Recording [{groupPopupRecIdx}] groups changed: +[{string.Join(", ", added)}] -[{string.Join(", ", removed)}]");
            }
        }

        private void ShowRewindConfirmation(Recording rec)
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

        private void ShowFastForwardConfirmation(Recording rec)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            double now = Planetarium.GetUniversalTime();
            double delta = rec.StartUT - now;
            string launchDate = KSPUtil.PrintDateCompact(rec.StartUT, true);
            string message = string.Format(ic,
                "Fast-forward to \"{0}\" launch at {1}?\n\nTime will advance by {2:F0} seconds.",
                rec.VesselName, launchDate, delta);

            var capturedRec = rec;
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekFastForwardConfirm",
                    message,
                    "Confirm Fast-Forward",
                    HighLogic.UISkin,
                    new DialogGUIButton("Fast-Forward", () =>
                    {
                        ParsekLog.Info("FastForward",
                            string.Format(ic,
                                "User confirmed fast-forward to \"{0}\" at UT {1:F1}",
                                capturedRec.VesselName, capturedRec.StartUT));
                        if (InFlight && flight != null)
                            flight.FastForwardToRecording(capturedRec);
                        else
                        {
                            // KSC or other non-flight scene: advance UT directly.
                            // Intentionally duplicates jump+message from FastForwardToRecording —
                            // flight path has additional steps (NotifyRecorder, recorder state)
                            // that don't apply outside flight.
                            double preJumpUT = Planetarium.GetUniversalTime();
                            double jumpDelta = capturedRec.StartUT - preJumpUT;
                            ParsekLog.Info("FastForward",
                                string.Format(ic,
                                    "Non-flight FF to UT={0:F1} for '{1}' (delta={2:F1}s)",
                                    capturedRec.StartUT, capturedRec.VesselName, jumpDelta));
                            TimeJumpManager.ExecuteForwardJump(capturedRec.StartUT);
                            ParsekLog.ScreenMessage(
                                string.Format(ic,
                                    "Fast-forwarded to \"{0}\" ({1:F0}s)",
                                    capturedRec.VesselName, jumpDelta), 3f);
                        }
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Info("FastForward", "User cancelled fast-forward confirmation");
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
                            CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                        CrewReservationManager.ClearReplacements();
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

        private void DrawSortableHeaderCore<TCol>(
            string label, TCol col, ref TCol currentCol, ref bool ascending,
            float width, bool expand, Action onChanged)
            where TCol : struct
        {
            string arrow = EqualityComparer<TCol>.Default.Equals(currentCol, col)
                ? (ascending ? " \u25b2" : " \u25bc") : "";
            bool clicked = expand
                ? GUILayout.Button(label + arrow, GUI.skin.label, GUILayout.ExpandWidth(true))
                : GUILayout.Button(label + arrow, GUI.skin.label, GUILayout.Width(width));

            if (clicked)
            {
                if (EqualityComparer<TCol>.Default.Equals(currentCol, col))
                    ascending = !ascending;
                else
                {
                    currentCol = col;
                    ascending = true;
                }
                onChanged?.Invoke();
            }
        }

        private void DrawSortableHeader(string label, SortColumn col, float width, bool expand = false)
        {
            DrawSortableHeaderCore(label, col, ref sortColumn, ref sortAscending, width, expand, () =>
            {
                InvalidateSort();
                ParsekLog.Verbose("UI", $"Sort column changed: {sortColumn} {(sortAscending ? "asc" : "desc")}");
            });
        }

        private void InvalidateSort()
        {
            lastSortedCount = -1;
        }

        private void RebuildSortedIndices(List<Recording> committed, double now)
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

        internal static int GetStatusOrder(Recording rec, double now)
        {
            if (now < rec.StartUT) return 0;  // future
            if (now <= rec.EndUT) return 1;    // active
            return 2;                          // past
        }

        /// <summary>
        /// Sort key for a single recording based on the current sort column.
        /// For Index/Phase sort, uses the row position to preserve sortedIndices order.
        /// </summary>
        internal static double GetRecordingSortKey(Recording rec, SortColumn column, double now, int rowFallback)
        {
            switch (column)
            {
                case SortColumn.LaunchTime: return rec.StartUT;
                case SortColumn.Duration: return rec.EndUT - rec.StartUT;
                case SortColumn.Status: return GetStatusOrder(rec, now);
                default: return rowFallback;
            }
        }

        /// <summary>
        /// Sort key for a chain based on the current sort column.
        /// Uses earliest StartUT for launch, sum for duration, etc.
        /// </summary>
        internal static double GetChainSortKey(List<int> members, List<Recording> committed,
            SortColumn column, double now)
        {
            switch (column)
            {
                case SortColumn.LaunchTime:
                {
                    double earliest = double.MaxValue;
                    for (int m = 0; m < members.Count; m++)
                        if (committed[members[m]].StartUT < earliest)
                            earliest = committed[members[m]].StartUT;
                    return earliest;
                }
                case SortColumn.Duration:
                {
                    double total = 0;
                    for (int m = 0; m < members.Count; m++)
                    {
                        double dur = committed[members[m]].EndUT - committed[members[m]].StartUT;
                        if (dur > 0) total += dur;
                    }
                    return total;
                }
                case SortColumn.Status:
                {
                    // Best (lowest) status order among members
                    int best = 2;
                    for (int m = 0; m < members.Count; m++)
                    {
                        int order = GetStatusOrder(committed[members[m]], now);
                        if (order < best) best = order;
                    }
                    return best;
                }
                default:
                    return 0; // Name/Phase/Index: handled via string comparison externally
            }
        }

        internal static int CompareRecordings(
            Recording ra, Recording rb,
            SortColumn column, bool ascending, double now)
        {
            int cmp = 0;
            switch (column)
            {
                case SortColumn.Index:
                    cmp = 0; // stable — Array.Sort preserves original order for equal elements
                    break;
                case SortColumn.Phase:
                    string pa = RecordingStore.GetSegmentPhaseLabel(ra);
                    string pb = RecordingStore.GetSegmentPhaseLabel(rb);
                    cmp = string.Compare(pa, pb, System.StringComparison.OrdinalIgnoreCase);
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
            List<Recording> committed, SortColumn column, bool ascending, double now)
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

            // KSP calendar: 6-hour days, 426-day years
            const int SecsPerHour = 3600;
            const int SecsPerDay = 6 * SecsPerHour;   // 21600
            const int SecsPerYear = 426 * SecsPerDay;  // 9201600

            if (total < SecsPerDay) return $"{total / SecsPerHour}h {(total % SecsPerHour) / 60}m";
            if (total < SecsPerYear)
            {
                int days = total / SecsPerDay;
                int hours = (total % SecsPerDay) / SecsPerHour;
                return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
            }
            int years = total / SecsPerYear;
            int remDays = (total % SecsPerYear) / SecsPerDay;
            return remDays > 0 ? $"{years}y {remDays}d" : $"{years}y";
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

        /// <summary>
        /// Returns the earliest StartUT among the given descendant recording indices.
        /// Returns double.MaxValue if no descendants exist.
        /// </summary>
        internal static double GetGroupEarliestStartUT(HashSet<int> descendants, List<Recording> committed)
        {
            double earliest = double.MaxValue;
            foreach (int idx in descendants)
            {
                double st = committed[idx].StartUT;
                if (st < earliest) earliest = st;
            }
            return earliest;
        }

        /// <summary>
        /// Returns the sum of durations (EndUT - StartUT) for all descendant recordings.
        /// </summary>
        internal static double GetGroupTotalDuration(HashSet<int> descendants, List<Recording> committed)
        {
            double total = 0;
            foreach (int idx in descendants)
            {
                double dur = committed[idx].EndUT - committed[idx].StartUT;
                if (dur > 0) total += dur;
            }
            return total;
        }

        /// <summary>
        /// Returns the committed-list index of the "main" recording in a group:
        /// the earliest non-debris descendant by StartUT.  Returns -1 when
        /// the group is empty or contains only debris.
        /// </summary>
        internal static int FindGroupMainRecordingIndex(
            HashSet<int> descendants, List<Recording> committed)
        {
            int bestIdx = -1;
            double bestUT = double.MaxValue;
            foreach (int idx in descendants)
            {
                if (idx < 0 || idx >= committed.Count) continue;
                var rec = committed[idx];
                if (rec.IsDebris) continue;
                if (rec.StartUT < bestUT)
                {
                    bestUT = rec.StartUT;
                    bestIdx = idx;
                }
            }
            return bestIdx;
        }

        /// <summary>
        /// Returns the status text and style order (0=future, 1=active, 2=past) for a group,
        /// based on the most relevant descendant: most recently activated (active with StartUT
        /// closest to now) wins, then closest future, then past.
        /// </summary>
        internal static void GetGroupStatus(HashSet<int> descendants, List<Recording> committed,
            double now, out string statusText, out int statusOrder)
        {
            // Find the best candidate: prefer active (closest T-), then future (closest), then past
            double bestActiveDelta = double.MaxValue;   // smallest |now - StartUT| among active
            double bestFutureDelta = double.MaxValue;   // smallest (StartUT - now) among future
            int activeIdx = -1;
            int futureIdx = -1;
            bool anyPast = false;

            foreach (int idx in descendants)
            {
                var rec = committed[idx];
                if (now >= rec.StartUT && now <= rec.EndUT)
                {
                    // Active recording — prefer closest T- (= StartUT - now, which is negative;
                    // we want the one closest to now, i.e., smallest |delta|)
                    double delta = System.Math.Abs(rec.StartUT - now);
                    if (delta < bestActiveDelta)
                    {
                        bestActiveDelta = delta;
                        activeIdx = idx;
                    }
                }
                else if (now < rec.StartUT)
                {
                    double delta = rec.StartUT - now;
                    if (delta < bestFutureDelta)
                    {
                        bestFutureDelta = delta;
                        futureIdx = idx;
                    }
                }
                else
                {
                    anyPast = true;
                }
            }

            if (activeIdx >= 0)
            {
                var rec = committed[activeIdx];
                statusOrder = 1; // active
                statusText = rec.Points.Count > 0
                    ? SelectiveSpawnUI.FormatCountdown(rec.StartUT - now)
                    : "active";
            }
            else if (futureIdx >= 0)
            {
                var rec = committed[futureIdx];
                statusOrder = 0; // future
                statusText = rec.Points.Count > 0
                    ? SelectiveSpawnUI.FormatCountdown(rec.StartUT - now)
                    : "future";
            }
            else if (anyPast)
            {
                statusOrder = 2;
                statusText = "past";
            }
            else
            {
                statusOrder = 2;
                statusText = "-";
            }
        }

        /// <summary>
        /// Computes aggregate status for a chain block header.
        /// Delegates to GetGroupStatus with a HashSet built from the member list.
        /// </summary>
        private static void GetChainStatus(List<int> members, List<Recording> committed,
            double now, out string statusText, out int statusOrder)
        {
            chainStatusBuffer.Clear();
            for (int i = 0; i < members.Count; i++)
                chainStatusBuffer.Add(members[i]);
            GetGroupStatus(chainStatusBuffer, committed, now, out statusText, out statusOrder);
        }

        /// <summary>
        /// Computes a sort key for a group based on the current sort column.
        /// Used to interleave groups with recordings in the sorted draw order.
        /// </summary>
        internal static double GetGroupSortKey(HashSet<int> descendants, List<Recording> committed,
            SortColumn column, double now)
        {
            if (descendants.Count == 0) return double.MaxValue;

            switch (column)
            {
                case SortColumn.LaunchTime:
                    return GetGroupEarliestStartUT(descendants, committed);
                case SortColumn.Duration:
                    return GetGroupTotalDuration(descendants, committed);
                case SortColumn.Status:
                {
                    string unused;
                    int order;
                    GetGroupStatus(descendants, committed, now, out unused, out order);
                    return order;
                }
                case SortColumn.Name:
                case SortColumn.Phase:
                    return 0; // sorted by string comparison externally
                case SortColumn.Index:
                default:
                    return -1; // groups before recordings in insertion-order sort
            }
        }

        private RecordingStats GetOrComputeStats(Recording rec)
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

        private void DrawRecordingTooltip(Recording rec)
        {
            var stats = GetOrComputeStats(rec);

            string text = $"Max Altitude: {FormatAltitude(stats.maxAltitude)}\n" +
                          $"Max Speed: {FormatSpeed(stats.maxSpeed)}\n" +
                          $"Distance: {FormatDistance(stats.distanceTravelled)}\n" +
                          $"Points: {stats.pointCount}";

            // Phase 6d-3: Chain status in recording tooltip
            if (InFlight && flight != null)
            {
                string chainStatus = ParsekFlight.GetChainStatusForRecording(
                    flight.ActiveGhostChains, rec);
                if (chainStatus != null)
                    text += $"\n{chainStatus}";
            }

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

        // --- Loop time unit helpers (internal static for testability) ---

        internal static string UnitLabel(LoopTimeUnit unit)
            => unit == LoopTimeUnit.Min ? "min"
             : unit == LoopTimeUnit.Hour ? "hr"
             : unit == LoopTimeUnit.Auto ? "auto"
             : "sec";

        internal static string UnitSuffix(LoopTimeUnit unit)
            => unit == LoopTimeUnit.Min ? "m"
             : unit == LoopTimeUnit.Hour ? "h"
             : "s";

        internal static LoopTimeUnit CycleRecordingUnit(LoopTimeUnit u)
            => u == LoopTimeUnit.Sec ? LoopTimeUnit.Min
             : u == LoopTimeUnit.Min ? LoopTimeUnit.Hour
             : u == LoopTimeUnit.Hour ? LoopTimeUnit.Auto
             : LoopTimeUnit.Sec;

        internal static LoopTimeUnit CycleDisplayUnit(LoopTimeUnit u)
            => u == LoopTimeUnit.Sec ? LoopTimeUnit.Min
             : u == LoopTimeUnit.Min ? LoopTimeUnit.Hour
             : LoopTimeUnit.Sec;

        // Auto is resolved separately by callers; falls through to seconds if called directly.
        internal static double ConvertFromSeconds(double seconds, LoopTimeUnit unit)
            => unit == LoopTimeUnit.Min ? seconds / 60.0
             : unit == LoopTimeUnit.Hour ? seconds / 3600.0
             : seconds;

        // Auto is resolved separately by callers; falls through to seconds if called directly.
        internal static double ConvertToSeconds(double value, LoopTimeUnit unit)
            => unit == LoopTimeUnit.Min ? value * 60.0
             : unit == LoopTimeUnit.Hour ? value * 3600.0
             : value;

        internal static string FormatLoopValue(double value, LoopTimeUnit unit)
        {
            if (unit == LoopTimeUnit.Min || unit == LoopTimeUnit.Hour)
                return value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            return ((long)System.Math.Truncate(value)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static bool TryParseLoopInput(string text, LoopTimeUnit unit, out double value)
        {
            if (unit == LoopTimeUnit.Min || unit == LoopTimeUnit.Hour)
                return double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value);
            int intVal;
            bool ok = int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out intVal);
            value = intVal;
            return ok;
        }

        // --- Auto loop range ---

        /// <summary>
        /// Sets or clears LoopStartUT/LoopEndUT when the loop toggle changes.
        /// On toggle-on: auto-selects the interesting portion (trims boring bookends).
        /// On toggle-off: clears the range back to NaN (full recording).
        /// </summary>
        internal static void ApplyAutoLoopRange(Recording rec, bool loopEnabled)
        {
            if (loopEnabled)
            {
                var (start, end) = GhostPlaybackLogic.ComputeAutoLoopRange(rec.TrackSections);
                if (!double.IsNaN(start))
                {
                    rec.LoopStartUT = start;
                    rec.LoopEndUT = end;
                    ParsekLog.Info("UI", $"Auto loop range: '{rec.VesselName}' " +
                        $"narrowed to [{start:F1}..{end:F1}] " +
                        $"(trimmed from [{rec.StartUT:F1}..{rec.EndUT:F1}])");
                }
            }
            else
            {
                if (!double.IsNaN(rec.LoopStartUT) || !double.IsNaN(rec.LoopEndUT))
                {
                    rec.LoopStartUT = double.NaN;
                    rec.LoopEndUT = double.NaN;
                    ParsekLog.Info("UI", $"Loop range cleared for '{rec.VesselName}'");
                }
            }
        }

        // --- Loop period cell ---

        private void DrawLoopPeriodCell(Recording rec, int ri, double dur)
        {
            // All states use same [value area][unit button] layout to keep column alignment consistent.
            const float unitBtnW = 40f;
            float valueBtnW = ColW_Period - unitBtnW - 2f;

            if (!rec.LoopPlayback)
            {
                // Disabled: gray out the same two-control layout
                GUI.enabled = false;
                string disabledText;
                if (rec.LoopTimeUnit == LoopTimeUnit.Auto)
                {
                    var settings = ParsekSettings.Current;
                    double gv = settings != null
                        ? ConvertFromSeconds(settings.autoLoopIntervalSeconds, settings.AutoLoopDisplayUnit) : 10;
                    var gu = settings != null ? settings.AutoLoopDisplayUnit : LoopTimeUnit.Sec;
                    disabledText = FormatLoopValue(gv, gu) + UnitSuffix(gu);
                }
                else
                {
                    disabledText = FormatLoopValue(ConvertFromSeconds(rec.LoopIntervalSeconds, rec.LoopTimeUnit), rec.LoopTimeUnit);
                }
                GUILayout.TextField(disabledText, GUILayout.Width(valueBtnW));
                GUILayout.Button(UnitLabel(rec.LoopTimeUnit), GUILayout.Width(unitBtnW));
                GUI.enabled = true;
                return;
            }

            if (rec.LoopTimeUnit == LoopTimeUnit.Auto)
            {
                // Auto mode: disabled text field showing global value + "auto" unit button
                var settings = ParsekSettings.Current;
                double globalVal = settings != null
                    ? ConvertFromSeconds(settings.autoLoopIntervalSeconds, settings.AutoLoopDisplayUnit)
                    : 10;
                GUI.enabled = false;
                var globalDisplayUnit = settings != null ? settings.AutoLoopDisplayUnit : LoopTimeUnit.Sec;
                GUILayout.TextField(FormatLoopValue(globalVal, globalDisplayUnit) + UnitSuffix(globalDisplayUnit), GUILayout.Width(valueBtnW));
                GUI.enabled = true;
            }
            else
            {
                // Manual mode: editable text field with edit buffer.
                // When not actively editing, show formatted value from model.
                // When editing (loopPeriodFocusedRi == ri), use buffer.
                // Commit happens via click-outside (MouseDown check at top)
                // or Enter key.
                if (loopPeriodFocusedRi != ri)
                {
                    // Not editing: show model value, click to start editing
                    double displayValue = ConvertFromSeconds(rec.LoopIntervalSeconds, rec.LoopTimeUnit);
                    string displayText = FormatLoopValue(displayValue, rec.LoopTimeUnit);
                    string controlName = "LoopPeriod_" + ri;
                    GUI.SetNextControlName(controlName);
                    string newText = GUILayout.TextField(displayText, GUILayout.Width(valueBtnW));
                    if (GUI.GetNameOfFocusedControl() == controlName)
                    {
                        loopPeriodEditText = newText;
                        loopPeriodFocusedRi = ri;
                        loopPeriodEditRect = GUILayoutUtility.GetLastRect();
                        ParsekLog.Verbose("UI",
                            $"Recording '{rec.VesselName}' loop period edit started: " +
                            $"value='{newText}' unit={UnitLabel(rec.LoopTimeUnit)}");
                    }
                }
                else
                {
                    // Enter key → commit (check before TextField, which consumes KeyDown)
                    bool submitPeriod = Event.current.type == EventType.KeyDown &&
                        (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

                    // Editing: use buffer, track rect for click-outside
                    GUI.SetNextControlName("LoopPeriod_" + ri);
                    string newText = GUILayout.TextField(loopPeriodEditText, GUILayout.Width(valueBtnW));
                    loopPeriodEditRect = GUILayoutUtility.GetLastRect();
                    if (newText != loopPeriodEditText)
                        loopPeriodEditText = newText;

                    if (submitPeriod)
                    {
                        CommitLoopPeriodEdit(RecordingStore.CommittedRecordings);
                        Event.current.Use();
                    }
                }
            }

            // Unit cycling button (shared by both auto and manual modes)
            if (GUILayout.Button(UnitLabel(rec.LoopTimeUnit), GUILayout.Width(unitBtnW)))
            {
                var newUnit = CycleRecordingUnit(rec.LoopTimeUnit);
                rec.LoopTimeUnit = newUnit;
                GUIUtility.keyboardControl = 0;
                loopPeriodFocusedRi = -1;
                ParsekLog.Info("UI",
                    $"Recording '{rec.VesselName}' loop unit changed to {newUnit}");
            }
        }

        private void CommitLoopPeriodEdit(System.Collections.Generic.List<Recording> committed)
        {
            if (loopPeriodFocusedRi < 0 || loopPeriodFocusedRi >= committed.Count) { loopPeriodFocusedRi = -1; return; }
            var rec = committed[loopPeriodFocusedRi];
            double dur = rec.EndUT - rec.StartUT;
            double parsed;
            if (TryParseLoopInput(loopPeriodEditText, rec.LoopTimeUnit, out parsed))
            {
                double newSeconds = ConvertToSeconds(parsed, rec.LoopTimeUnit);
                double minSeconds = -(dur - 1.0); // cap: -totalDuration + 1s
                if (newSeconds < minSeconds)
                {
                    newSeconds = minSeconds;
                    ParsekLog.Info("UI",
                        $"Recording '{rec.VesselName}' loop interval clamped to " +
                        $"{newSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}s " +
                        $"(minimum for duration {dur.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}s)");
                }
                rec.LoopIntervalSeconds = newSeconds;
                ParsekLog.Info("UI",
                    $"Recording '{rec.VesselName}' loop interval updated to " +
                    rec.LoopIntervalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                    $"s (display: {FormatLoopValue(ConvertFromSeconds(newSeconds, rec.LoopTimeUnit), rec.LoopTimeUnit)} " +
                    $"{UnitLabel(rec.LoopTimeUnit)})");
            }
            else
            {
                ParsekLog.Warn("UI",
                    $"Recording '{rec.VesselName}' loop interval edit rejected: " +
                    $"invalid input '{loopPeriodEditText}' for unit {rec.LoopTimeUnit}");
            }
            loopPeriodFocusedRi = -1;
            loopPeriodEditRect = default;
            GUIUtility.keyboardControl = 0;
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
            statusStyleFuture.normal.textColor = Color.white;

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

            EnsureOpaqueWindowStyle();
            // Reset height each frame so GUILayout auto-sizes to content
            settingsWindowRect.height = 10;
            settingsWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekSettings".GetHashCode(),
                settingsWindowRect,
                DrawSettingsWindow,
                "Parsek \u2014 Settings",
                opaqueWindowStyle,
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

        // ════════════════════════════════════════════════════════════════
        //  Real Spawn Control window
        // ════════════════════════════════════════════════════════════════

        public void DrawSpawnControlWindowIfOpen(Rect mainWindowRect)
        {
            if (!showSpawnControlWindow)
            {
                ReleaseSpawnControlInputLock();
                return;
            }

            // Auto-close when no nearby candidates
            if (!InFlight || flight == null || flight.NearbySpawnCandidates.Count == 0)
            {
                showSpawnControlWindow = false;
                ReleaseSpawnControlInputLock();
                return;
            }

            if (spawnControlWindowRect.width < 1f)
            {
                float x = mainWindowRect.x + mainWindowRect.width + 10;
                spawnControlWindowRect = new Rect(x, mainWindowRect.y, 680, 200);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI",
                    $"Real Spawn Control window initial position: x={spawnControlWindowRect.x.ToString("F0", ic)} y={spawnControlWindowRect.y.ToString("F0", ic)}");
            }

            HandleResizeDrag(ref spawnControlWindowRect, ref isResizingSpawnControlWindow,
                MinWindowWidth, MinWindowHeight, "Real Spawn Control window");

            EnsureOpaqueWindowStyle();
            spawnControlWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekSpawnControl".GetHashCode(),
                spawnControlWindowRect,
                DrawSpawnControlWindow,
                "Parsek \u2014 Real Spawn Control",
                opaqueWindowStyle,
                GUILayout.Width(spawnControlWindowRect.width),
                GUILayout.Height(spawnControlWindowRect.height)
            );
            LogWindowPosition("SpawnControl", ref lastSpawnControlWindowRect, spawnControlWindowRect);

            if (spawnControlWindowRect.Contains(Event.current.mousePosition))
            {
                if (!spawnControlWindowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, SpawnControlInputLockId);
                    spawnControlWindowHasInputLock = true;
                }
            }
            else
            {
                ReleaseSpawnControlInputLock();
            }
        }

        private void ReleaseSpawnControlInputLock()
        {
            if (!spawnControlWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(SpawnControlInputLockId);
            spawnControlWindowHasInputLock = false;
        }

        // Spawn Control column widths (matches recordings window style)
        private const float SpawnColW_Name = 0f;    // expand
        private const float SpawnColW_Dist = 55f;
        private const float SpawnColW_SpawnTime = 100f;
        private const float SpawnColW_Countdown = 95f;
        private const float SpawnColW_State = 110f;
        private const float SpawnColW_Warp = 85f;

        private void DrawSpawnSortableHeader(string label, SpawnSortColumn col, float width)
        {
            DrawSpawnSortableHeader(label, col, false, width);
        }

        private void DrawSpawnSortableHeader(string label, SpawnSortColumn col, bool expand)
        {
            DrawSpawnSortableHeader(label, col, expand, 0);
        }

        private void DrawSpawnSortableHeader(string label, SpawnSortColumn col, bool expand, float width)
        {
            DrawSortableHeaderCore(label, col, ref spawnSortColumn, ref spawnSortAscending, width, expand, () =>
            {
                ParsekLog.Verbose("UI", $"Spawn sort changed: column={spawnSortColumn}, ascending={spawnSortAscending}");
            });
        }

        private int CompareSpawnCandidates(NearbySpawnCandidate a, NearbySpawnCandidate b)
        {
            int cmp;
            switch (spawnSortColumn)
            {
                case SpawnSortColumn.Name:
                    cmp = string.Compare(a.vesselName, b.vesselName,
                        System.StringComparison.OrdinalIgnoreCase);
                    break;
                case SpawnSortColumn.SpawnTime:
                    cmp = a.endUT.CompareTo(b.endUT);
                    break;
                default: // Distance
                    cmp = a.distance.CompareTo(b.distance);
                    break;
            }
            return spawnSortAscending ? cmp : -cmp;
        }

        private void DrawSpawnControlWindow(int windowID)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            double currentUT = Planetarium.GetUniversalTime();
            var candidates = flight.NearbySpawnCandidates;

            if (candidates.Count == 0)
            {
                GUILayout.Label("No nearby craft to spawn.");
                if (GUILayout.Button("Close"))
                    showSpawnControlWindow = false;
                GUI.DragWindow();
                return;
            }

            // Header row with sortable columns
            GUILayout.BeginHorizontal();
            DrawSpawnSortableHeader("Craft", SpawnSortColumn.Name, true);
            DrawSpawnSortableHeader("Dist", SpawnSortColumn.Distance, SpawnColW_Dist);
            DrawSpawnSortableHeader("Spawns at", SpawnSortColumn.SpawnTime, SpawnColW_SpawnTime);
            DrawSpawnSortableHeader("In T-", SpawnSortColumn.SpawnTime, SpawnColW_Countdown);
            GUILayout.Label("State", GUILayout.Width(SpawnColW_State));
            GUILayout.Label("", GUILayout.Width(SpawnColW_Warp));
            GUILayout.EndHorizontal();

            // Re-sort when candidate list, sort state, or departure info changes
            int gen = flight.ProximityCheckGeneration;
            if (candidates.Count != cachedCandidateCount
                || gen != cachedProximityGeneration
                || spawnSortColumn != cachedSortColumn
                || spawnSortAscending != cachedSortAscending)
            {
                cachedSortedCandidates.Clear();
                for (int ci = 0; ci < candidates.Count; ci++)
                    cachedSortedCandidates.Add(candidates[ci]);
                cachedSortedCandidates.Sort(CompareSpawnCandidates);
                cachedCandidateCount = candidates.Count;
                cachedProximityGeneration = gen;
                cachedSortColumn = spawnSortColumn;
                cachedSortAscending = spawnSortAscending;
            }
            var sorted = cachedSortedCandidates;

            DrawSpawnCandidateRows(sorted, currentUT, ic);

            DrawSpawnControlBottomBar(candidates, currentUT);
        }

        private void DrawSpawnCandidateRows(List<NearbySpawnCandidate> sorted,
            double currentUT, System.Globalization.CultureInfo ic)
        {
            spawnControlScrollPos = GUILayout.BeginScrollView(spawnControlScrollPos, GUILayout.ExpandHeight(true));
            for (int i = 0; i < sorted.Count; i++)
            {
                var cand = sorted[i];
                double delta = cand.endUT - currentUT;
                bool canWarp = cand.endUT > currentUT;

                GUILayout.BeginHorizontal();
                GUILayout.Label(cand.vesselName, GUILayout.ExpandWidth(true));
                GUILayout.Label(
                    string.Format(ic, "{0:F0}m", cand.distance),
                    GUILayout.Width(SpawnColW_Dist));
                GUILayout.Label(
                    KSPUtil.PrintDateCompact(cand.endUT, true),
                    GUILayout.Width(SpawnColW_SpawnTime));
                GUILayout.Label(
                    SelectiveSpawnUI.FormatCountdown(delta),
                    GUILayout.Width(SpawnColW_Countdown));

                // State column: departure info
                if (cand.willDepart)
                {
                    double depDelta = cand.departureUT - currentUT;
                    string stateText;
                    if (depDelta <= 0)
                        stateText = string.Format(ic, "Departing \u2192 {0}", cand.destination ?? "?");
                    else
                        stateText = string.Format(ic, "Departs {0}",
                            SelectiveSpawnUI.FormatCountdown(depDelta));
                    var prevColor = GUI.contentColor;
                    GUI.contentColor = depDelta <= 0
                        ? new Color(1f, 0.65f, 0.2f) // orange
                        : new Color(1f, 1f, 0.4f);    // yellow
                    GUILayout.Label(stateText, GUILayout.Width(SpawnColW_State));
                    GUI.contentColor = prevColor;
                }
                else
                {
                    GUILayout.Label("", GUILayout.Width(SpawnColW_State));
                }

                // Warp button: "FF-Depart" for departing, "FF-Spawn" for normal
                if (cand.willDepart)
                {
                    bool canWarpDep = cand.departureUT > currentUT;
                    GUI.enabled = canWarpDep;
                    if (GUILayout.Button("FF-Depart", GUILayout.Width(SpawnColW_Warp)))
                    {
                        ParsekLog.Info("UI",
                            string.Format(ic,
                                "Real Spawn Control: warp to departure '{0}' recording #{1} depUT={2:F1}",
                                cand.vesselName, cand.recordingIndex, cand.departureUT));
                        flight.WarpToDeparture(cand.recordingIndex, cand.departureUT);
                    }
                    GUI.enabled = true;
                }
                else
                {
                    GUI.enabled = canWarp;
                    if (GUILayout.Button("FF-Spawn", GUILayout.Width(SpawnColW_Warp)))
                    {
                        ParsekLog.Info("UI",
                            string.Format(ic,
                                "Real Spawn Control: warp to '{0}' recording #{1}",
                                cand.vesselName, cand.recordingIndex));
                        flight.WarpToRecordingEnd(cand.recordingIndex);
                    }
                    GUI.enabled = true;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        private void DrawSpawnControlBottomBar(List<NearbySpawnCandidate> candidates,
            double currentUT)
        {
            // Bottom section -- pinned to window bottom
            GUILayout.FlexibleSpace();

            GUILayout.BeginHorizontal();
            var next = SelectiveSpawnUI.FindNextSpawnCandidate(candidates, currentUT);
            GUI.enabled = next != null;
            string tooltip = next != null
                ? SelectiveSpawnUI.FormatNextSpawnTooltip(next, currentUT) : "";
            if (GUILayout.Button(new GUIContent("Warp to Next Real Spawn", tooltip),
                GUILayout.ExpandWidth(true)))
            {
                ParsekLog.Info("UI", "Real Spawn Control: Warp to Next Real Spawn clicked");
                flight.WarpToNextCraftSpawn();
            }
            GUI.enabled = true;
            if (GUILayout.Button("Close", GUILayout.Width(132)))
            {
                showSpawnControlWindow = false;
                ParsekLog.Verbose("UI", "Real Spawn Control window closed");
            }
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(GUI.tooltip))
            {
                GUILayout.Space(SpacingSmall);
                GUILayout.Label(GUI.tooltip, GUI.skin.box);
            }

            DrawResizeHandle(spawnControlWindowRect, ref isResizingSpawnControlWindow,
                "Real Spawn Control window");

            GUI.DragWindow();
        }

        private void CommitSettingsAutoLoopEdit(ParsekSettings s)
        {
            double parsed;
            if (TryParseLoopInput(settingsAutoLoopText, s.AutoLoopDisplayUnit, out parsed) && parsed >= 0)
            {
                // float cast: KSP GameParameters requires float; loses precision beyond ~7 digits (fine for practical intervals)
                s.autoLoopIntervalSeconds = (float)ConvertToSeconds(parsed, s.AutoLoopDisplayUnit);
                ParsekLog.Info("UI",
                    $"Setting changed: autoLoopIntervalSeconds={s.autoLoopIntervalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}s");
            }
            else
            {
                ParsekLog.Warn("UI",
                    $"Auto-loop settings edit rejected: invalid or negative input '{settingsAutoLoopText}' " +
                    $"for unit {UnitLabel(s.AutoLoopDisplayUnit)}");
            }
            settingsAutoLoopEditing = false;
            settingsAutoLoopEditRect = default;
            GUIUtility.keyboardControl = 0;
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

            // Click outside active settings edit field → commit
            if (Event.current.type == EventType.MouseDown)
            {
                if (settingsAutoLoopEditing && settingsAutoLoopEditRect.width > 0
                    && !settingsAutoLoopEditRect.Contains(Event.current.mousePosition))
                    CommitSettingsAutoLoopEdit(s);
                if (settingsCameraCutoffEditing && settingsCameraCutoffEditRect.width > 0
                    && !settingsCameraCutoffEditRect.Contains(Event.current.mousePosition))
                    CommitSettingsCameraCutoffEdit(s);
            }

            #region Settings sections
            DrawRecordingSettings(s);
            GUILayout.Space(SpacingSmall);
            DrawLoopingSettings(s);
            GUILayout.Space(SpacingSmall);
            DrawGhostSettings(s);
            GUILayout.Space(SpacingSmall);
            DrawDiagnosticsSettings(s);
            GUILayout.Space(SpacingSmall);
            DrawSamplingSettings(s);
            GUILayout.Space(SpacingSmall);
            DrawDataManagementSettings(s);
            #endregion

            GUILayout.Space(SpacingLarge);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Defaults"))
            {
                ParsekLog.Verbose("UI", "Settings Defaults button clicked");
                s.autoRecordOnLaunch = true;
                s.autoRecordOnEva = true;
                s.autoMerge = false;
                s.verboseLogging = true;
                s.maxSampleInterval = 3.0f;
                s.velocityDirThreshold = 2.0f;
                s.speedChangeThreshold = 5.0f;
                s.autoLoopIntervalSeconds = 10.0f;
                s.autoLoopTimeUnit = 0;
                s.ghostCapEnabled = false;
                s.ghostCapZone1Reduce = 8;
                s.ghostCapZone1Despawn = 15;
                s.ghostCapZone2Simplify = 20;
                s.ghostCameraCutoffKm = 300f;
                GhostSoftCapManager.Enabled = false;
                GhostSoftCapManager.ApplySettings(8, 15, 20);
                settingsAutoLoopEditing = false;
                settingsCameraCutoffEditing = false;
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

        private void DrawRecordingSettings(ParsekSettings s)
        {
            GUILayout.Label("Recording", GUI.skin.box);
            bool autoRecordOnLaunch = GUILayout.Toggle(s.autoRecordOnLaunch,
                new GUIContent(" Auto-record on launch", "Start recording when a vessel leaves the pad or runway"));
            if (autoRecordOnLaunch != s.autoRecordOnLaunch)
            {
                s.autoRecordOnLaunch = autoRecordOnLaunch;
                ParsekLog.Info("UI", $"Setting changed: autoRecordOnLaunch={s.autoRecordOnLaunch}");
            }

            bool autoRecordOnEva = GUILayout.Toggle(s.autoRecordOnEva,
                new GUIContent(" Auto-record on EVA", "Start recording when a kerbal goes EVA from the pad"));
            if (autoRecordOnEva != s.autoRecordOnEva)
            {
                s.autoRecordOnEva = autoRecordOnEva;
                ParsekLog.Info("UI", $"Setting changed: autoRecordOnEva={s.autoRecordOnEva}");
            }

            bool autoMerge = GUILayout.Toggle(s.autoMerge,
                new GUIContent(" Auto-merge recordings", "When off, a confirmation dialog appears after each recording"));
            if (autoMerge != s.autoMerge)
            {
                s.autoMerge = autoMerge;
                ParsekLog.Info("UI", $"Setting changed: autoMerge={s.autoMerge}");
            }
        }

        private void DrawLoopingSettings(ParsekSettings s)
        {
            GUILayout.Label("Looping", GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Auto-loop every",
                "Default loop interval for recordings set to 'auto' unit"), GUILayout.Width(100));
            {
                if (!settingsAutoLoopEditing)
                {
                    // Not editing: show formatted value from model
                    double displayVal = ConvertFromSeconds(s.autoLoopIntervalSeconds, s.AutoLoopDisplayUnit);
                    string displayText = FormatLoopValue(displayVal, s.AutoLoopDisplayUnit);
                    GUI.SetNextControlName("AutoLoopEdit");
                    string newText = GUILayout.TextField(displayText, GUILayout.Width(45));
                    if (GUI.GetNameOfFocusedControl() == "AutoLoopEdit")
                    {
                        settingsAutoLoopText = newText;
                        settingsAutoLoopEditing = true;
                        settingsAutoLoopEditRect = GUILayoutUtility.GetLastRect();
                        ParsekLog.Verbose("UI",
                            $"Auto-loop settings edit started: value='{newText}' unit={UnitLabel(s.AutoLoopDisplayUnit)}");
                    }
                }
                else
                {
                    // Enter key → commit (check before TextField, which consumes KeyDown)
                    bool submitAutoLoop = Event.current.type == EventType.KeyDown &&
                        (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

                    // Editing: use buffer, track rect for click-outside
                    GUI.SetNextControlName("AutoLoopEdit");
                    string newText = GUILayout.TextField(settingsAutoLoopText, GUILayout.Width(45));
                    settingsAutoLoopEditRect = GUILayoutUtility.GetLastRect();
                    if (newText != settingsAutoLoopText)
                        settingsAutoLoopText = newText;

                    if (submitAutoLoop)
                    {
                        CommitSettingsAutoLoopEdit(s);
                        Event.current.Use();
                    }
                }

                if (GUILayout.Button(UnitLabel(s.AutoLoopDisplayUnit), GUILayout.Width(40)))
                {
                    if (settingsAutoLoopEditing)
                        CommitSettingsAutoLoopEdit(s);
                    s.AutoLoopDisplayUnit = CycleDisplayUnit(s.AutoLoopDisplayUnit);
                    ParsekLog.Info("UI", $"Setting changed: autoLoopDisplayUnit={s.AutoLoopDisplayUnit}");
                }
            }
            GUILayout.EndHorizontal();
        }

        private void DrawGhostSettings(ParsekSettings s)
        {
            GUILayout.Label("Ghosts", GUI.skin.box);

            // --- Camera cutoff ---
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Camera cutoff",
                "Watch mode auto-exits when ghost exceeds this distance from the active vessel"),
                GUILayout.Width(85));
            {
                if (!settingsCameraCutoffEditing)
                {
                    string displayText = s.ghostCameraCutoffKm.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                    GUI.SetNextControlName("CameraCutoffEdit");
                    string newText = GUILayout.TextField(displayText, GUILayout.Width(45));
                    if (GUI.GetNameOfFocusedControl() == "CameraCutoffEdit")
                    {
                        settingsCameraCutoffText = newText;
                        settingsCameraCutoffEditing = true;
                        settingsCameraCutoffEditRect = GUILayoutUtility.GetLastRect();
                    }
                }
                else
                {
                    // Enter key → commit (check before TextField, which consumes KeyDown)
                    bool submitCutoff = Event.current.type == EventType.KeyDown &&
                        (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

                    GUI.SetNextControlName("CameraCutoffEdit");
                    string newText = GUILayout.TextField(settingsCameraCutoffText, GUILayout.Width(45));
                    settingsCameraCutoffEditRect = GUILayoutUtility.GetLastRect();
                    if (newText != settingsCameraCutoffText)
                        settingsCameraCutoffText = newText;

                    if (submitCutoff)
                    {
                        CommitSettingsCameraCutoffEdit(s);
                        Event.current.Use();
                    }
                }
            }
            GUILayout.Label("km");
            GUILayout.EndHorizontal();

            // --- Soft caps ---
            GUILayout.Space(SpacingSmall);

            bool enabled = GUILayout.Toggle(s.ghostCapEnabled,
                new GUIContent(" Enable soft caps",
                    "Limit ghost count per distance zone to reduce rendering load"));
            if (enabled != s.ghostCapEnabled)
            {
                s.ghostCapEnabled = enabled;
                GhostSoftCapManager.Enabled = enabled;
                ParsekLog.Info("UI", $"Ghost soft caps {(enabled ? "enabled" : "disabled")}");
            }

            if (!s.ghostCapEnabled)
            {
                GUILayout.Label("  (caps disabled \u2014 all ghosts rendered)", GUI.skin.label);
                return;
            }

            DrawGhostCapSlider("Zone 1 reduce", "Nearby ghosts above this count get reduced fidelity",
                ref s.ghostCapZone1Reduce, 2, 30, "ghostCap.zone1Reduce", s);
            DrawGhostCapSlider("Zone 1 despawn", "Nearby ghosts above this count get despawned (lowest priority first)",
                ref s.ghostCapZone1Despawn, 5, 50, "ghostCap.zone1Despawn", s);
            DrawGhostCapSlider("Zone 2 simplify", "Distant ghosts above this count get simplified to orbit lines",
                ref s.ghostCapZone2Simplify, 5, 60, "ghostCap.zone2Simplify", s);

            // Enforce constraint: reduce must be less than despawn
            if (s.ghostCapZone1Reduce >= s.ghostCapZone1Despawn)
            {
                s.ghostCapZone1Reduce = System.Math.Max(2, s.ghostCapZone1Despawn - 1);
                GhostSoftCapManager.ApplySettings(
                    s.ghostCapZone1Reduce, s.ghostCapZone1Despawn, s.ghostCapZone2Simplify);
                ParsekLog.Info("UI",
                    $"Clamped ghostCapZone1Reduce={s.ghostCapZone1Reduce} to stay below " +
                    $"ghostCapZone1Despawn={s.ghostCapZone1Despawn}");
            }
        }

        private void CommitSettingsCameraCutoffEdit(ParsekSettings s)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (float.TryParse(settingsCameraCutoffText, System.Globalization.NumberStyles.Float,
                    ic, out float parsed) && parsed >= 10f && parsed <= 10000f)
            {
                s.ghostCameraCutoffKm = parsed;
                ParsekLog.Info("UI",
                    $"Setting changed: ghostCameraCutoffKm={s.ghostCameraCutoffKm.ToString("F0", ic)}");
            }
            settingsCameraCutoffEditing = false;
            settingsCameraCutoffEditRect = default;
            GUIUtility.keyboardControl = 0;
        }

        private void DrawDiagnosticsSettings(ParsekSettings s)
        {
            GUILayout.Label("Diagnostics", GUI.skin.box);
            bool verboseLogging = GUILayout.Toggle(s.verboseLogging, " Verbose logging (development default)");
            if (verboseLogging != s.verboseLogging)
            {
                s.verboseLogging = verboseLogging;
                ParsekLog.Info("UI", $"Setting changed: verboseLogging={s.verboseLogging}");
            }

            if (GUILayout.Button(new GUIContent("In-Game Test Runner",
                "Run runtime tests to verify ghost spawning, playback, and visuals.\nAlso available via Ctrl+Shift+T in any scene.")))
            {
                showTestRunnerWindow = !showTestRunnerWindow;
                ParsekLog.Verbose("UI", $"Test runner window toggled: {(showTestRunnerWindow ? "open" : "closed")}");
            }
        }

        private void DrawSamplingSettings(ParsekSettings s)
        {
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
        }

        private void DrawGhostCapSlider(string label, string tooltip, ref int value,
            int min, int max, string logKey, ParsekSettings s)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(
                new GUIContent($"{label}: {value}", tooltip),
                GUILayout.Width(140));
            int newValue = Mathf.RoundToInt(
                GUILayout.HorizontalSlider(value, min, max));
            if (newValue != value)
            {
                value = newValue;
                GhostSoftCapManager.ApplySettings(
                    s.ghostCapZone1Reduce, s.ghostCapZone1Despawn, s.ghostCapZone2Simplify);
                ParsekLog.VerboseRateLimited("UI", logKey,
                    $"Setting changed: {logKey}={value}", 1.0);
            }
            GUILayout.EndHorizontal();
        }

        private void DrawDataManagementSettings(ParsekSettings s)
        {
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

        }

        #region Test Runner Window

        public void DrawTestRunnerWindowIfOpen(Rect mainWindowRect, MonoBehaviour host)
        {
            if (!showTestRunnerWindow)
            {
                ReleaseTestRunnerInputLock();
                return;
            }

            // Lazy-init the test runner
            if (testRunner == null)
            {
                testRunner = new InGameTestRunner(host);
                foreach (var t in testRunner.Tests)
                    expandedTestCategories.Add(t.Category);
                RebuildTestGroupCache();
            }

            if (testRunnerWindowRect.width < 1f)
            {
                testRunnerWindowRect = new Rect(
                    mainWindowRect.x + mainWindowRect.width + 10,
                    mainWindowRect.y,
                    380, 500);
            }

            EnsureOpaqueWindowStyle();
            testRunnerWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekTestRunner".GetHashCode(),
                testRunnerWindowRect,
                DrawTestRunnerWindow,
                "Parsek \u2014 Test Runner",
                opaqueWindowStyle,
                GUILayout.Width(380)
            );
            LogWindowPosition("TestRunner", ref lastTestRunnerWindowRect, testRunnerWindowRect);

            if (testRunnerWindowRect.Contains(Event.current.mousePosition))
            {
                if (!testRunnerWindowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, TestRunnerInputLockId);
                    testRunnerWindowHasInputLock = true;
                }
            }
            else
            {
                ReleaseTestRunnerInputLock();
            }
        }

        private void ReleaseTestRunnerInputLock()
        {
            if (!testRunnerWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(TestRunnerInputLockId);
            testRunnerWindowHasInputLock = false;
        }

        private void RebuildTestGroupCache()
        {
            var groups = new Dictionary<string, List<InGameTestInfo>>();
            foreach (var t in testRunner.Tests)
            {
                if (!groups.TryGetValue(t.Category, out var list))
                {
                    list = new List<InGameTestInfo>();
                    groups[t.Category] = list;
                }
                list.Add(t);
            }
            var sorted = new List<KeyValuePair<string, List<InGameTestInfo>>>(groups);
            sorted.Sort((a, b) => string.Compare(a.Key, b.Key, System.StringComparison.Ordinal));
            cachedTestGroups = sorted;
        }

        private void DrawTestCategoryList()
        {
            foreach (var group in cachedTestGroups)
            {
                var category = group.Key;
                var testsInCategory = group.Value;

                bool expanded = expandedTestCategories.Contains(category);
                int catPassed = 0, catFailed = 0;
                foreach (var t in testsInCategory)
                {
                    if (t.Status == TestStatus.Passed) catPassed++;
                    else if (t.Status == TestStatus.Failed) catFailed++;
                }

                // Category header
                GUILayout.BeginHorizontal();
                string arrow = expanded ? "\u25bc" : "\u25b6";
                string catSummary = catFailed > 0
                    ? $" ({catPassed}/{testsInCategory.Count}, {catFailed} failed)"
                    : $" ({catPassed}/{testsInCategory.Count})";
                if (GUILayout.Button($"{arrow} {category}{catSummary}", GUI.skin.label))
                {
                    if (expanded) expandedTestCategories.Remove(category);
                    else expandedTestCategories.Add(category);
                }
                // Run category button
                GUI.enabled = !testRunner.IsRunning;
                if (GUILayout.Button("Run", GUILayout.Width(40)))
                {
                    testRunner.ResetCategory(category);
                    testRunner.RunCategory(category);
                    ParsekLog.Verbose("UI", $"Test runner: Run category '{category}'");
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();

                if (!expanded) continue;

                // Individual tests
                foreach (var test in testsInCategory)
                {
                    bool eligible = test.RequiredScene == InGameTestAttribute.AnyScene
                        || test.RequiredScene == HighLogic.LoadedScene;

                    GUILayout.BeginHorizontal();
                    GUILayout.Space(16);

                    // Status indicator
                    string icon = GetTestStatusIcon(test.Status);
                    var prevColor = GUI.contentColor;
                    GUI.contentColor = GetTestStatusColor(test.Status);
                    GUILayout.Label(icon, GUILayout.Width(20));
                    GUI.contentColor = prevColor;

                    // Test name (dimmed if wrong scene)
                    if (!eligible) GUI.enabled = false;
                    string testLabel = test.Name;
                    if (test.Method.DeclaringType != null)
                        testLabel = test.Method.Name; // short name within category
                    if (test.DurationMs > 0)
                        testLabel += $" ({test.DurationMs:F0}ms)";
                    GUILayout.Label(new GUIContent(testLabel,
                        test.Description ?? (eligible ? "" : $"Requires {test.RequiredScene} scene")));
                    GUI.enabled = true;

                    // Run single button
                    GUI.enabled = !testRunner.IsRunning && eligible;
                    if (GUILayout.Button("\u25b6", GUILayout.Width(24)))
                    {
                        test.Status = TestStatus.NotRun;
                        test.ErrorMessage = null;
                        testRunner.RunSingle(test);
                    }
                    GUI.enabled = true;

                    GUILayout.EndHorizontal();

                    // Error message if failed
                    if (test.Status == TestStatus.Failed && !string.IsNullOrEmpty(test.ErrorMessage))
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Space(40);
                        var prevCol = GUI.contentColor;
                        GUI.contentColor = Color.red;
                        GUILayout.Label(test.ErrorMessage, GUILayout.MaxWidth(320));
                        GUI.contentColor = prevCol;
                        GUILayout.EndHorizontal();
                    }
                }
            }
        }

        private void DrawTestRunnerWindow(int windowID)
        {
            if (testRunner == null)
            {
                GUILayout.Label("Test runner not initialized.");
                GUI.DragWindow();
                return;
            }

            // --- Controls bar ---
            GUILayout.BeginHorizontal();
            GUI.enabled = !testRunner.IsRunning;
            if (GUILayout.Button("Run All"))
            {
                testRunner.ResetResults();
                testRunner.RunAll();
                ParsekLog.Info("UI", "Test runner: Run All clicked");
            }
            if (GUILayout.Button("Reset"))
            {
                testRunner.ResetResults();
                ParsekLog.Verbose("UI", "Test runner: Reset clicked");
            }
            GUI.enabled = testRunner.IsRunning;
            if (GUILayout.Button("Cancel"))
            {
                testRunner.Cancel();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // --- Summary ---
            GUILayout.Space(SpacingSmall);
            int total = testRunner.Tests.Count;
            string status = testRunner.IsRunning ? "RUNNING" : "idle";
            GUILayout.Label(
                $"{status} | {testRunner.Passed} passed  {testRunner.Failed} failed  {testRunner.Skipped} skipped  ({total} total)",
                GUI.skin.box);

            // --- Scene filter note ---
            GUILayout.Label($"Scene: {HighLogic.LoadedScene}", GUI.skin.label);

            // --- Rebuild cached groups when run state changes ---
            bool running = testRunner.IsRunning;
            if (cachedTestGroups == null || (testRunnerWasRunning && !running))
                RebuildTestGroupCache();
            testRunnerWasRunning = running;

            // --- Test list ---
            GUILayout.Space(SpacingSmall);
            testRunnerScrollPos = GUILayout.BeginScrollView(testRunnerScrollPos,
                GUILayout.MinHeight(200), GUILayout.MaxHeight(500));

            DrawTestCategoryList();

            GUILayout.EndScrollView();

            // --- Bottom bar ---
            GUILayout.Space(SpacingSmall);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Export Results"))
            {
                testRunner.ExportResultsFile();
                ParsekLog.Info("UI", "Test runner: manually exported results file");
            }
            if (GUILayout.Button("Close"))
            {
                showTestRunnerWindow = false;
                ParsekLog.Verbose("UI", "Test runner window closed");
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Ctrl+Shift+T to toggle from any scene", GUI.skin.label);

            // Tooltip
            if (!string.IsNullOrEmpty(GUI.tooltip))
            {
                GUILayout.Space(SpacingSmall);
                GUILayout.Label(GUI.tooltip, GUI.skin.box);
            }

            GUI.DragWindow();
        }

        private static string GetTestStatusIcon(TestStatus status)
        {
            switch (status)
            {
                case TestStatus.Passed:  return "\u2713"; // checkmark
                case TestStatus.Failed:  return "\u2717"; // X
                case TestStatus.Running: return "\u25cb"; // circle
                case TestStatus.Skipped: return "\u2013"; // dash
                default:                 return "\u00b7"; // dot
            }
        }

        private static Color GetTestStatusColor(TestStatus status)
        {
            switch (status)
            {
                case TestStatus.Passed:  return Color.green;
                case TestStatus.Failed:  return Color.red;
                case TestStatus.Running: return Color.yellow;
                case TestStatus.Skipped: return Color.gray;
                default:                 return Color.white;
            }
        }

        #endregion

        public void DrawMapMarkers()
        {
            // Resolve camera for current view; bail if unavailable
            if (MapView.MapIsEnabled)
            {
                if (PlanetariumCamera.Camera == null) return;
            }
            else
            {
                if (FlightCamera.fetch == null || FlightCamera.fetch.mainCamera == null) return;
            }

            EnsureMapMarkerResources();

            // Manual preview ghost
            if (flight.IsPlaying && flight.PreviewGhost != null)
            {
                DrawMapMarkerAt(flight.PreviewGhost.transform.position, "Preview",
                    new Color(0.2f, 1f, 0.4f, 0.9f));
            }

            // Timeline ghosts — skip if a ghost map ProtoVessel exists for this index
            // (the native KSP vessel icon replaces this marker and tracks the correct orbital position).
            // Deduplicate per chain: during warp, multiple chain segments can be active
            // simultaneously. Only draw the marker for the highest-index (latest) ghost per chain.
            var committed = RecordingStore.CommittedRecordings;

            // First pass: find the highest active index per chain
            chainTipIndexBuffer.Clear();
            foreach (var kvp in flight.TimelineGhosts)
            {
                if (kvp.Value == null) continue;
                if (kvp.Key >= committed.Count) continue;
                string chainId = committed[kvp.Key].ChainId;
                if (string.IsNullOrEmpty(chainId)) continue;
                int existing;
                if (!chainTipIndexBuffer.TryGetValue(chainId, out existing) || kvp.Key > existing)
                    chainTipIndexBuffer[chainId] = kvp.Key;
            }

            // Second pass: draw markers, skipping non-tip chain members and debris
            foreach (var kvp in flight.TimelineGhosts)
            {
                if (kvp.Value == null) continue;
                // Skip if native KSP icon is active (ProtoVessel exists and icon not suppressed).
                // When the Harmony patch suppresses the icon (below atmosphere), we draw
                // our custom marker at the ghost mesh position instead.
                if (GhostMapPresence.HasGhostVesselForRecording(kvp.Key))
                {
                    uint ghostPid = GhostMapPresence.GetGhostVesselPidForRecording(kvp.Key);
                    if (ghostPid == 0 || !GhostMapPresence.IsIconSuppressed(ghostPid))
                        continue; // native icon is active — skip our marker
                }
                if (kvp.Key < committed.Count)
                {
                    if (committed[kvp.Key].IsDebris)
                        continue;
                    string chainId = committed[kvp.Key].ChainId;
                    if (!string.IsNullOrEmpty(chainId) && chainTipIndexBuffer.Count > 0
                        && chainTipIndexBuffer.TryGetValue(chainId, out int tip) && kvp.Key != tip)
                        continue; // not the tip — skip duplicate
                }

                string ghostName = kvp.Key < committed.Count ? committed[kvp.Key].VesselName : "Ghost";
                VesselType vtype = kvp.Key < committed.Count
                    ? GhostMapPresence.ResolveVesselType(committed[kvp.Key].VesselSnapshot)
                    : VesselType.Ship;
                Color markerColor = GetGhostMarkerColorForType(vtype);
                DrawMapMarkerAt(kvp.Value.transform.position, ghostName, markerColor, vtype);
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
            ReleaseSpawnControlInputLock();
            if (mapMarkerDiamond != null)
                UnityEngine.Object.Destroy(mapMarkerDiamond);
        }

        /// <summary>
        /// Returns the orbit color for a ghost marker by vessel type.
        /// Delegates to MapMarkerRenderer.GetColorForType for consistency
        /// between flight map view and tracking station.
        /// </summary>
        private static Color GetGhostMarkerColorForType(VesselType vtype)
        {
            return MapMarkerRenderer.GetColorForType(vtype);
        }

        private void DrawMapMarkerAt(Vector3 worldPos, string label, Color color,
            VesselType vtype = VesselType.Ship)
        {
            Vector3 screenPos;
            if (MapView.MapIsEnabled)
            {
                Vector3d scaledPos = ScaledSpace.LocalToScaledSpace(worldPos);
                screenPos = PlanetariumCamera.Camera.WorldToScreenPoint(scaledPos);
            }
            else
            {
                screenPos = FlightCamera.fetch.mainCamera.WorldToScreenPoint(worldPos);
            }

            // Behind camera
            if (screenPos.z < 0) return;

            // GUI coordinates (Y inverted)
            float x = screenPos.x;
            float y = Screen.height - screenPos.y;

            // Draw vessel type icon from KSP atlas (untinted — original icon colors)
            Color prevColor = GUI.color;
            int iconSize = 20;
            Rect uvRect;
            if (vesselIconAtlas != null && vesselIconUVs != null
                && vesselIconUVs.TryGetValue(vtype, out uvRect))
            {
                GUI.color = Color.white;
                GUI.DrawTextureWithTexCoords(
                    new Rect(x - iconSize / 2, y - iconSize / 2, iconSize, iconSize),
                    vesselIconAtlas, uvRect);
            }
            else
            {
                GUI.color = color;
                GUI.DrawTexture(
                    new Rect(x - iconSize / 2, y - iconSize / 2, iconSize, iconSize),
                    mapMarkerDiamond);
            }
            GUI.color = prevColor;

            // Draw vessel name label
            mapMarkerStyle.normal.textColor = color;
            GUI.Label(new Rect(x - 75, y + iconSize / 2 + 2, 150, 20), "Ghost: " + label, mapMarkerStyle);
        }

        private void EnsureMapMarkerResources()
        {
            // Try to load KSP's stock vessel icon atlas from the MapView prefab
            if (vesselIconAtlas == null && MapView.fetch != null)
                InitVesselTypeIcons();

            // Fallback diamond texture (used when atlas unavailable)
            if (mapMarkerDiamond == null)
            {
                int size = 16;
                mapMarkerDiamond = new Texture2D(size, size, TextureFormat.ARGB32, false);
                float center = size / 2f;
                float halfDiag = size / 2f - 1f;
                for (int py = 0; py < size; py++)
                {
                    for (int px = 0; px < size; px++)
                    {
                        float manhattan = Mathf.Abs(px - center + 0.5f) + Mathf.Abs(py - center + 0.5f);
                        mapMarkerDiamond.SetPixel(px, py, manhattan <= halfDiag ? Color.white : Color.clear);
                    }
                }
                mapMarkerDiamond.Apply();
            }

            if (mapMarkerStyle == null)
            {
                mapMarkerStyle = new GUIStyle(GUI.skin.label);
                mapMarkerStyle.fontSize = 11;
                mapMarkerStyle.fontStyle = FontStyle.Bold;
                mapMarkerStyle.alignment = TextAnchor.UpperCenter;
            }
        }

        private static void InitVesselTypeIcons()
        {
            vesselIconAtlas = MapView.OrbitIconsMap;
            if (vesselIconAtlas == null) return;

            var prefab = MapView.UINodePrefab;
            if (prefab == null) return;

            var fi = typeof(MapNode).GetField("iconSprites",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi == null)
            {
                ParsekLog.Warn("UI", "InitVesselTypeIcons: iconSprites field not found on MapNode");
                vesselIconAtlas = null;
                return;
            }

            var sprites = fi.GetValue(prefab) as Sprite[];
            if (sprites == null || sprites.Length == 0)
            {
                ParsekLog.Warn("UI", "InitVesselTypeIcons: iconSprites is null or empty");
                vesselIconAtlas = null;
                return;
            }

            // VesselType → sprite index mapping from decompiled MapNode.GetIconIndex.
            // KSP-version-dependent: indices may change if the atlas is reordered.
            // Failure is graceful — falls back to diamond texture.
            var mapping = new Dictionary<VesselType, int>
            {
                { VesselType.Ship,        20 }, { VesselType.Probe,    18 },
                { VesselType.Rover,       19 }, { VesselType.Station,   0 },
                { VesselType.Plane,       23 }, { VesselType.Lander,   14 },
                { VesselType.Base,         5 }, { VesselType.EVA,      13 },
                { VesselType.Relay,       24 }, { VesselType.Debris,    7 },
                { VesselType.SpaceObject, 21 },
            };

            float atlasW = vesselIconAtlas.width;
            float atlasH = vesselIconAtlas.height;
            vesselIconUVs = new Dictionary<VesselType, Rect>();

            foreach (var kvp in mapping)
            {
                if (kvp.Value < sprites.Length && sprites[kvp.Value] != null)
                {
                    Rect texRect = sprites[kvp.Value].textureRect;
                    vesselIconUVs[kvp.Key] = new Rect(
                        texRect.x / atlasW, texRect.y / atlasH,
                        texRect.width / atlasW, texRect.height / atlasH);
                }
            }

            ParsekLog.Info("UI",
                $"InitVesselTypeIcons: loaded {vesselIconUVs.Count} vessel type icons from atlas " +
                $"({atlasW}x{atlasH}, {sprites.Length} sprites)");
        }

        /// <summary>
        /// Handles resize drag for a window. Call before the window function.
        /// Group Popup passes null for windowName to suppress logging.
        /// </summary>
        private static void HandleResizeDrag(ref Rect windowRect, ref bool isResizing,
            float minWidth, float minHeight, string windowName)
        {
            if (!isResizing) return;

            if (Event.current.type == EventType.MouseDrag || Event.current.type == EventType.MouseUp)
            {
                float newW = Mathf.Max(minWidth, Event.current.mousePosition.x - windowRect.x);
                float newH = Mathf.Max(minHeight, Event.current.mousePosition.y - windowRect.y);
                windowRect.width = newW;
                windowRect.height = newH;
            }
            if (Event.current.type == EventType.MouseUp)
            {
                isResizing = false;
                if (windowName != null)
                {
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    ParsekLog.Verbose("UI",
                        $"{windowName} resize ended: w={windowRect.width.ToString("F0", ic)} h={windowRect.height.ToString("F0", ic)}");
                }
            }
            if (Event.current.type == EventType.MouseDrag)
                Event.current.Use();
        }

        /// <summary>
        /// Draws a resize handle triangle and starts resize on mouse down.
        /// Call at the end of the window draw function.
        /// </summary>
        private static void DrawResizeHandle(Rect windowRect, ref bool isResizing, string windowName)
        {
            Rect handleRect = new Rect(
                windowRect.width - ResizeHandleSize,
                windowRect.height - ResizeHandleSize,
                ResizeHandleSize, ResizeHandleSize);
            GUI.Label(handleRect, "\u25e2");
            if (Event.current.type == EventType.MouseDown && handleRect.Contains(Event.current.mousePosition))
            {
                isResizing = true;
                if (windowName != null)
                    ParsekLog.Verbose("UI", $"{windowName} resize started");
                Event.current.Use();
            }
        }
    }
}
