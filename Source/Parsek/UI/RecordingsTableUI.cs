using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Recordings table window extracted from ParsekUI.
    /// Manages the recordings list, sorting, group tree, rename, loop period editing, and all
    /// recordings-window-specific UI state.
    /// </summary>
    internal class RecordingsTableUI
    {
        private readonly ParsekUI parentUI;

        // Recordings window
        private bool showRecordingsWindow;
        private Rect recordingsWindowRect;
        private Vector2 recordingsScrollPos;
        private bool isResizingRecordingsWindow;
        private bool recordingsWindowHasInputLock;
        private const string RecordingsInputLockId = "Parsek_RecordingsWindow";
        private const float ResizeHandleSize = 16f;
        private const float MinWindowWidth = 350f;
        private const float MinWindowHeight = 150f;

        // Column widths — shared between header and body for alignment
        private const float ColW_Enable = 20f;
        private const float ColW_Phase = 90f;
        private const float ColW_Index = 30f;
        private const float ColW_Launch = 110f;
        private const float ColW_Dur = 80f;
        private const float ColW_Status = 120f;
        private const float ColW_Loop = 55f;
        private const float ColW_Watch = 50f;
        private const float ColW_Rewind = 65f;
        private const float ColW_Hide = 50f;
        private const float ColW_Site = 90f;
        private const float ColW_Group = 50f;

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

        // Group picker popup (extracted to GroupPickerUI)
        private GroupPickerUI groupPicker;

        // Runtime-only empty groups (not persisted)
        internal List<string> KnownEmptyGroups = new List<string>();

        // Cached phase label styles
        private GUIStyle phaseStyleAtmo;
        private GUIStyle phaseStyleExo;
        private GUIStyle phaseStyleSpace;
        private GUIStyle phaseStyleApproach;
        private GUIStyle phaseStyleSurface;

        // Sort state
        internal enum SortColumn { Index, Phase, Name, LaunchTime, Duration, Status, LaunchSite }
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
        private int[] sortedIndices; // maps display row -> CommittedRecordings index
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
        private const float ColW_StartPos = 120f;
        private const float ColW_EndPos = 120f;

        // Loop period editing — buffer used while text field is focused
        private int loopPeriodFocusedRi = -1;
        private string loopPeriodEditText = "";
        private Rect loopPeriodEditRect;

        private const float ColW_Period = 80f;

        // R/FF button state tracking for transition logging
        private Dictionary<int, bool> lastCanRewind = new Dictionary<int, bool>();
        private Dictionary<int, bool> lastCanFF = new Dictionary<int, bool>();

        // Watch button enabled-state tracking for transition logging (bug #279).
        // Both dicts are keyed by RecordingId (stable across rewind/truncate
        // index reuse, unlike the existing index-keyed lastCanFF/lastCanRewind
        // which the bug #279 review flagged as a follow-up cleanup target).
        // Group entries are prefixed with "{groupName}/" so two groups whose
        // main recording happens to share an id (impossible today, but cheap
        // insurance) still get distinct entries. Pruned each table draw via
        // PruneStaleWatchTransitionEntries.
        private Dictionary<string, bool> lastCanWatchByRecId = new Dictionary<string, bool>();
        private Dictionary<string, bool> lastCanWatchByGroup = new Dictionary<string, bool>();

        // Cached styles for status labels
        private GUIStyle statusStyleFuture;
        private GUIStyle statusStyleActive;
        private GUIStyle statusStylePast;

        // Window drag tracking for position logging
        private Rect lastRecordingsWindowRect;

        private BudgetSummary cachedBudget = default(BudgetSummary);

        public bool IsOpen
        {
            get { return showRecordingsWindow; }
            set { showRecordingsWindow = value; }
        }

        // Cross-link: pending scroll target from Timeline
        private string pendingScrollToRecordingId;
        // Deferred scroll: row index found during draw pass, applied next frame
        private int pendingScrollRowIndex = -1;
        private int renderedRowCounter;

        internal RecordingsTableUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
            this.groupPicker = new GroupPickerUI(parentUI);
        }

        /// <summary>
        /// Called by the Timeline GoTo button. Ensures the target recording is visible
        /// (unhides if hidden, disables hide filter if needed, expands parent groups),
        /// opens the window, and scrolls to the recording.
        /// </summary>
        internal void ScrollToRecording(string recordingId)
        {
            if (!showRecordingsWindow) showRecordingsWindow = true;

            // Find the recording
            var committed = RecordingStore.CommittedRecordings;
            Recording target = null;
            for (int i = 0; i < committed.Count; i++)
            {
                if (committed[i].RecordingId == recordingId)
                {
                    target = committed[i];
                    break;
                }
            }

            if (target == null)
            {
                ParsekLog.Warn("UI", $"Cross-link: recording {recordingId} not found in committed list");
                return;
            }

            // Unhide if the recording itself is hidden
            if (target.Hidden)
            {
                target.Hidden = false;
                ParsekLog.Info("UI", $"Cross-link: unhid recording \"{target.VesselName}\"");
            }

            // Disable hide filter if it would prevent the recording from showing
            // (e.g., recording was just unhidden but other hidden recordings exist)
            if (GroupHierarchyStore.HideActive && target.Hidden)
            {
                GroupHierarchyStore.HideActive = false;
                ParsekLog.Info("UI", "Cross-link: disabled HideActive to show recording");
            }

            // Expand all parent groups so the recording is visible in the tree
            if (target.RecordingGroups != null)
            {
                for (int g = 0; g < target.RecordingGroups.Count; g++)
                {
                    string groupName = target.RecordingGroups[g];

                    // Expand the immediate group
                    if (!expandedGroups.Contains(groupName))
                    {
                        expandedGroups.Add(groupName);
                        ParsekLog.Verbose("UI", $"Cross-link: expanded group \"{groupName}\"");
                    }

                    // Walk up the parent chain and expand all ancestors
                    string current = groupName;
                    string parent;
                    while (GroupHierarchyStore.TryGetGroupParent(current, out parent))
                    {
                        if (!expandedGroups.Contains(parent))
                        {
                            expandedGroups.Add(parent);
                            ParsekLog.Verbose("UI", $"Cross-link: expanded ancestor group \"{parent}\"");
                        }
                        current = parent;
                    }
                }
            }

            // Unhide the group if it's in a hidden group
            if (target.RecordingGroups != null && GroupHierarchyStore.HideActive)
            {
                for (int g = 0; g < target.RecordingGroups.Count; g++)
                {
                    string groupName = target.RecordingGroups[g];
                    if (GroupHierarchyStore.IsGroupHidden(groupName))
                    {
                        GroupHierarchyStore.RemoveHiddenGroup(groupName);
                        ParsekLog.Info("UI", $"Cross-link: unhid group \"{groupName}\"");
                    }
                }
            }

            // Schedule scroll to this recording (detected during next draw pass)
            pendingScrollToRecordingId = recordingId;
            ParsekLog.Verbose("UI",
                $"Cross-link: scroll requested for \"{target.VesselName}\" id={recordingId}");
        }

        /// <summary>
        /// Returns the resource budget.
        /// </summary>
        internal BudgetSummary GetCachedBudget()
        {
            return cachedBudget;
        }

        public void DrawIfOpen(Rect mainWindowRect)
        {
            if (!showRecordingsWindow)
            {
                ReleaseInputLock();
                return;
            }

            // Position to the right of main window on first open
            if (recordingsWindowRect.width < 1f)
            {
                float recHeight = parentUI.InFlightMode ? mainWindowRect.height : mainWindowRect.height * 2;
                recordingsWindowRect = new Rect(
                    mainWindowRect.x + mainWindowRect.width + 10,
                    mainWindowRect.y,
                    1196, recHeight);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI", $"Recordings window initial position: x={recordingsWindowRect.x.ToString("F0", ic)} y={recordingsWindowRect.y.ToString("F0", ic)}");
            }

            ParsekUI.HandleResizeDrag(ref recordingsWindowRect, ref isResizingRecordingsWindow,
                MinWindowWidth, MinWindowHeight, "Recordings window");

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            recordingsWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekRecordings".GetHashCode(),
                recordingsWindowRect,
                DrawRecordingsWindow,
                "Parsek \u2014 Recordings",
                opaqueWindowStyle,
                GUILayout.Width(recordingsWindowRect.width),
                GUILayout.Height(recordingsWindowRect.height)
            );
            parentUI.LogWindowPosition("Recordings", ref lastRecordingsWindowRect, recordingsWindowRect);

            // Group picker popup (rendered outside recordings window to avoid scroll clipping)
            groupPicker.Draw();

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
                ReleaseInputLock();
            }
        }

        internal void ReleaseInputLock()
        {
            if (!recordingsWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(RecordingsInputLockId);
            recordingsWindowHasInputLock = false;
        }

        private void EnsurePhaseStyles()
        {
            if (phaseStyleAtmo != null) return;

            phaseStyleAtmo = new GUIStyle(GUI.skin.label);
            phaseStyleAtmo.normal.textColor = new Color(0.4f, 0.7f, 1f); // blue

            phaseStyleExo = new GUIStyle(GUI.skin.label);
            phaseStyleExo.normal.textColor = new Color(0.75f, 0.55f, 1f); // light purple

            phaseStyleSpace = new GUIStyle(GUI.skin.label);
            phaseStyleSpace.normal.textColor = new Color(0.2f, 1f, 0.6f); // lime green

            phaseStyleApproach = new GUIStyle(GUI.skin.label);
            phaseStyleApproach.normal.textColor = new Color(0.3f, 0.8f, 1f); // cyan

            phaseStyleSurface = new GUIStyle(GUI.skin.label);
            phaseStyleSurface.normal.textColor = new Color(1f, 0.6f, 0.2f); // orange
        }

        /// <summary>
        /// Bug #279 follow-up: removes lastCanWatchByRecId / lastCanWatchByGroup
        /// entries whose RecordingId is no longer present in the committed
        /// recording list. Called once per <see cref="DrawRecordingsWindow"/>
        /// invocation. Delegates to <see cref="PruneStaleWatchEntries"/> so the
        /// pruning logic is testable without instantiating RecordingsTableUI
        /// (which requires a live ParsekUI / Unity GameObject).
        /// </summary>
        internal void PruneStaleWatchTransitionEntries(IReadOnlyList<Recording> committed)
        {
            PruneStaleWatchEntries(lastCanWatchByRecId, lastCanWatchByGroup, committed);
        }

        /// <summary>
        /// Pure static prune logic for the watch-transition dicts. Removes
        /// every key whose recording ID is not present in <paramref name="committed"/>.
        /// Per-row dict keys ARE recording ids; group dict keys are
        /// "{groupName}/{recordingId}" and we extract the trailing segment.
        /// Both dicts are cleared if the committed list is empty/null. Callers
        /// pass references to the live dictionaries — the method mutates them
        /// in place.
        /// </summary>
        internal static void PruneStaleWatchEntries(
            Dictionary<string, bool> lastCanWatchByRecId,
            Dictionary<string, bool> lastCanWatchByGroup,
            IReadOnlyList<Recording> committed)
        {
            if (committed == null || committed.Count == 0)
            {
                lastCanWatchByRecId?.Clear();
                lastCanWatchByGroup?.Clear();
                return;
            }

            // Build a HashSet of live recording IDs once for O(1) lookup.
            var liveIds = new HashSet<string>();
            for (int i = 0; i < committed.Count; i++)
            {
                var id = committed[i].RecordingId;
                if (!string.IsNullOrEmpty(id))
                    liveIds.Add(id);
            }

            // Per-row dict: prune by direct id membership.
            if (lastCanWatchByRecId != null && lastCanWatchByRecId.Count > 0)
            {
                List<string> stale = null;
                foreach (var key in lastCanWatchByRecId.Keys)
                {
                    if (!liveIds.Contains(key))
                    {
                        if (stale == null) stale = new List<string>();
                        stale.Add(key);
                    }
                }
                if (stale != null)
                    for (int i = 0; i < stale.Count; i++)
                        lastCanWatchByRecId.Remove(stale[i]);
            }

            // Group dict: extract the trailing "/{recordingId}" segment and
            // check membership. Keys without a "/" (shouldn't happen) are
            // dropped defensively.
            if (lastCanWatchByGroup != null && lastCanWatchByGroup.Count > 0)
            {
                List<string> stale = null;
                foreach (var key in lastCanWatchByGroup.Keys)
                {
                    int slash = key.LastIndexOf('/');
                    string recId = slash >= 0 ? key.Substring(slash + 1) : null;
                    if (string.IsNullOrEmpty(recId) || !liveIds.Contains(recId))
                    {
                        if (stale == null) stale = new List<string>();
                        stale.Add(key);
                    }
                }
                if (stale != null)
                    for (int i = 0; i < stale.Count; i++)
                        lastCanWatchByGroup.Remove(stale[i]);
            }
        }

        private void HandleRecordingsDefocus(IReadOnlyList<Recording> committed)
        {
            // Click outside active rename field -> commit and close
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

            // Click outside active loop period field -> commit and defocus
            if (Event.current.type == EventType.MouseDown && loopPeriodFocusedRi >= 0)
            {
                if (loopPeriodEditRect.width > 0 && !loopPeriodEditRect.Contains(Event.current.mousePosition))
                {
                    CommitLoopPeriodEdit(committed);
                }
            }
        }

        private void DrawRecordingsTableHeader(IReadOnlyList<Recording> committed)
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
            DrawSortableHeader("Site", SortColumn.LaunchSite, ColW_Site);
            DrawSortableHeader("Launch", SortColumn.LaunchTime, ColW_Launch);
            DrawSortableHeader("Duration", SortColumn.Duration, ColW_Dur);

            if (showExpandedStats)
            {
                GUILayout.Label("MaxAlt", GUILayout.Width(ColW_MaxAlt));
                GUILayout.Label("MaxSpd", GUILayout.Width(ColW_MaxSpd));
                GUILayout.Label("Dist", GUILayout.Width(ColW_Dist));
                GUILayout.Label("Pts", GUILayout.Width(ColW_Pts));
                GUILayout.Label("Start", GUILayout.Width(ColW_StartPos));
                GUILayout.Label("End", GUILayout.Width(ColW_EndPos));
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

            if (parentUI.InFlightMode)
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

        private void DrawRecordingsBottomBar(IReadOnlyList<Recording> committed)
        {
            // Bottom button bar — pinned to window bottom
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();

            if (committed.Count > 0)
            {
                string statsLabel = showExpandedStats ? "Info \u25c0" : "Info \u25b6";
                if (GUILayout.Button(statsLabel, GUILayout.Width(65)))
                {
                    showExpandedStats = !showExpandedStats;
                    ParsekLog.Verbose("UI", $"Recordings Info toggled: {(showExpandedStats ? "expanded" : "collapsed")}");
                    if (showExpandedStats && recordingsWindowRect.width < 1654f)
                        recordingsWindowRect.width = 1654f;
                    else if (!showExpandedStats)
                        recordingsWindowRect.width = 1196f;
                }
            }

            if (GUILayout.Button("New Group", GUILayout.Width(80)))
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
                "Recordings window");

            GUI.DragWindow();
        }

        private void DrawRecordingsWindow(int windowID)
        {
            var committed = RecordingStore.CommittedRecordings;
            double now = Planetarium.GetUniversalTime();

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

            if (committed.Count == 0)
            {
                GUILayout.Label("No recordings.");
            }
            else
            {
                // Scrollable table body (header inside scroll view for guaranteed alignment)
                renderedRowCounter = 0;
                recordingsScrollPos = GUILayout.BeginScrollView(
                    recordingsScrollPos, false, true, GUILayout.ExpandHeight(true));

                DrawRecordingsTableHeader(committed);

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
                    out rootGrps, out rootChainIds);

                // -- Build unified sorted root items --
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
                {
                    int cmp;
                    if (col == SortColumn.Name || col == SortColumn.Phase || col == SortColumn.LaunchSite)
                        cmp = string.Compare(a.SortName, b.SortName,
                            StringComparison.OrdinalIgnoreCase);
                    else
                        cmp = a.SortKey.CompareTo(b.SortKey);
                    return asc ? cmp : -cmp;
                });

                // -- Draw tree --
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
        private bool DrawRecordingRow(int ri, IReadOnlyList<Recording> committed, double now, float indentPx)
        {
            var rec = committed[ri];
            if (rec.Hidden && GroupHierarchyStore.HideActive) return false;

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
                else if (rec.SegmentPhase == "surface") phaseStyle = phaseStyleSurface;
                else if (rec.SegmentPhase == "approach") phaseStyle = phaseStyleApproach;
                else if (rec.SegmentPhase == "space") phaseStyle = phaseStyleSpace;
                else phaseStyle = phaseStyleExo;
                GUILayout.Label(phaseLabel, phaseStyle, GUILayout.Width(ColW_Phase));
            }
            else
            {
                GUILayout.Label("", GUILayout.Width(ColW_Phase));
            }

            // Site (launch site name)
            GUILayout.Label(rec.LaunchSiteName ?? "", GUILayout.Width(ColW_Site));

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
                string parentVessel = ResolveParentVesselName(rec, committed);
                GUILayout.Label(FormatStartPosition(rec, parentVessel), GUILayout.Width(ColW_StartPos));
                GUILayout.Label(FormatEndPosition(rec, parentVessel), GUILayout.Width(ColW_EndPos));
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
            else if (now <= rec.EndUT && !rec.TerminalStateValue.HasValue)
            {
                statusStyle = statusStyleActive;
                statusText = rec.Points.Count > 0
                    ? SelectiveSpawnUI.FormatCountdown(rec.StartUT - now)
                    : "active";
            }
            else
            {
                statusStyle = statusStylePast;
                if (rec.TerminalStateValue.HasValue && !rec.IsDebris)
                    statusText = rec.TerminalStateValue.Value.ToString();
                else
                    statusText = "past";
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
            var statusContent = new GUIContent(statusText, chainStatusTooltip);
            GUILayout.Label(statusContent, statusStyle, GUILayout.Width(ColW_Status));

            // Group assignment button
            if (GUILayout.Button("G", GUILayout.Width(ColW_Group)))
            {
                var mousePos = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
                groupPicker.OpenForRecording(ri, mousePos);
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
            if (parentUI.InFlightMode)
            {
                bool hasGhost = flight.HasActiveGhost(ri);
                bool sameBody = flight.IsGhostOnSameBody(ri);
                bool inRange = flight.IsGhostWithinVisualRange(ri);
                bool isWatching = flight.WatchedRecordingIndex == ri;
                bool canWatch = hasGhost && sameBody && inRange && !rec.IsDebris;

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
                bool prevCanWatch;
                if (!string.IsNullOrEmpty(watchKey)
                    && (!lastCanWatchByRecId.TryGetValue(watchKey, out prevCanWatch) || prevCanWatch != canWatch))
                {
                    lastCanWatchByRecId[watchKey] = canWatch;
                    string reason = canWatch ? "enabled"
                        : rec.IsDebris ? "disabled (debris)"
                        : !hasGhost ? "disabled (no ghost)"
                        : !sameBody ? "disabled (different body)"
                        : !inRange ? "disabled (out of range)"
                        : "disabled (unknown)";
                    ParsekLog.Info("UI",
                        $"Watch button #{ri} \"{rec.VesselName}\" {reason} " +
                        $"(hasGhost={hasGhost} sameBody={sameBody} inRange={inRange} debris={rec.IsDebris})");
                }

                GUI.enabled = canWatch;
                string watchLabel = isWatching ? "W*" : "W";
                // Tooltip priority: debris first, then per-condition explanation.
                // When !hasGhost, previously the tooltip was empty, making a
                // disabled W button look broken rather than pending.
                string watchTooltip;
                if (rec.IsDebris)
                    watchTooltip = "Debris is not watchable";
                else if (!hasGhost)
                    watchTooltip = "No active ghost — recording is in the past/future or has no trajectory points";
                else if (!sameBody)
                    watchTooltip = "Ghost is on a different body";
                else if (!inRange)
                    watchTooltip = "Ghost is beyond camera cutoff";
                else
                    watchTooltip = isWatching ? "Exit watch mode" : "Follow ghost in watch mode";
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
                bool hasRewindSave = !string.IsNullOrEmpty(RecordingStore.GetRewindSaveFileName(rec));
                if (isFuture)
                {
                    // Future recording: FF button advances UT to recording start
                    string ffReason;
                    bool isRecording = parentUI.InFlightMode && flight.IsRecording;
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
                    bool isRecording = parentUI.InFlightMode && flight.IsRecording;
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
            IReadOnlyList<Recording> committed, float indentPx)
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

        // --- Group tree rendering helpers ---

        /// <summary>
        /// Recursively draws a group and its children. Returns true if the recording list was modified.
        /// </summary>
        private bool DrawGroupTree(string groupName, int depth,
            IReadOnlyList<Recording> committed, double now,
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

            // -- Group header --
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

            // Spacer for # column (fixed-width label for alignment with recording rows)
            GUILayout.Label("", GUILayout.Width(ColW_Index));

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

            // Site (from main/root recording if available)
            int grpMainIdx = FindGroupMainRecordingIndex(descendants, committed);
            string grpSite = grpMainIdx >= 0 ? committed[grpMainIdx].LaunchSiteName : null;
            GUILayout.Label(grpSite ?? "", GUILayout.Width(ColW_Site));

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
                GUILayout.Label("", GUILayout.Width(ColW_StartPos));
                GUILayout.Label("", GUILayout.Width(ColW_EndPos));
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
                var mousePos = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
                groupPicker.OpenForGroup(groupName, mousePos);
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

            var flight = parentUI.Flight;

            // Find the "main" (earliest non-debris) recording for group-level W and R/FF buttons
            int mainIdx = FindGroupMainRecordingIndex(descendants, committed);

            // Watch button (flight only) — targets main recording's ghost
            if (parentUI.InFlightMode)
            {
                if (mainIdx >= 0)
                {
                    bool hasGhost = flight.HasActiveGhost(mainIdx);
                    bool sameBody = flight.IsGhostOnSameBody(mainIdx);
                    bool inRange = flight.IsGhostWithinVisualRange(mainIdx);
                    bool isWatching = flight.WatchedRecordingIndex == mainIdx;
                    // No IsDebris check needed: FindGroupMainRecordingIndex
                    // (RecordingsTableUI.cs:~2021) already excludes debris from
                    // the candidate set, so mainIdx is never a debris row.
                    bool canWatch = hasGhost && sameBody && inRange;

                    // Bug #279: log enabled/disabled transitions for the group W
                    // button at INFO level. Group key combines group name + the
                    // RecordingId of the current main recording. RecordingId is
                    // stable across rewind/truncate index reuse, so a group whose
                    // main recording is replaced (e.g., truncate followed by a
                    // new launch) doesn't carry over the previous main's cached
                    // canWatch and emit a spurious transition.
                    //
                    // Skip the cache+log entirely if the main recording has a
                    // null/empty RecordingId. Mirrors the per-row guard above.
                    // Without this, the group dict would cache "{groupName}/",
                    // log once, get pruned by PruneStaleWatchEntries on the
                    // next draw (empty trailing recId → stale), and re-add on
                    // the draw after that — a spam loop. RecordingId being
                    // null/empty shouldn't happen in practice (all recordings
                    // get a GUID at construction), but the defensive guard is
                    // free and matches the per-row site for consistency.
                    string mainRecId = committed[mainIdx].RecordingId;
                    if (!string.IsNullOrEmpty(mainRecId))
                    {
                        string groupWatchKey = groupName + "/" + mainRecId;
                        bool prevGroupCanWatch;
                        if (!lastCanWatchByGroup.TryGetValue(groupWatchKey, out prevGroupCanWatch)
                            || prevGroupCanWatch != canWatch)
                        {
                            lastCanWatchByGroup[groupWatchKey] = canWatch;
                            string reason = canWatch ? "enabled"
                                : !hasGhost ? "disabled (no ghost)"
                                : !sameBody ? "disabled (different body)"
                                : !inRange ? "disabled (out of range)"
                                : "disabled (unknown)";
                            ParsekLog.Info("UI",
                                $"Group Watch button '{groupName}' main=#{mainIdx} \"{committed[mainIdx].VesselName}\" {reason} " +
                                $"(hasGhost={hasGhost} sameBody={sameBody} inRange={inRange})");
                        }
                    }

                    GUI.enabled = canWatch;
                    string watchLabel = isWatching ? "W*" : "W";
                    // Same tooltip policy as the per-row W button: explain
                    // !hasGhost explicitly so the user knows WHY it's disabled.
                    // Uses "camera cutoff" wording consistently with the per-row
                    // button — the setting it refers to is ghostCameraCutoffKm,
                    // so "visual range" was imprecise.
                    string watchTooltip;
                    if (!hasGhost)
                        watchTooltip = "No active ghost — recording is in the past/future or has no trajectory points";
                    else if (!sameBody)
                        watchTooltip = "Ghost is on a different body";
                    else if (!inRange)
                        watchTooltip = "Ghost is beyond camera cutoff";
                    else
                        watchTooltip = isWatching ? "Exit watch mode" : "Follow ghost in watch mode";
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
                bool hasRewindSave = !string.IsNullOrEmpty(RecordingStore.GetRewindSaveFileName(mainRec));
                bool isRecording = parentUI.InFlightMode && flight.IsRecording;

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

            // Hide group checkbox — toggles Hidden on all member recordings
            int hiddenCount = 0;
            foreach (int idx in descendants)
                if (committed[idx].Hidden) hiddenCount++;
            bool allHidden = memberCount > 0 && hiddenCount == memberCount;
            GUILayout.BeginHorizontal(GUILayout.Width(ColW_Hide));
            GUILayout.FlexibleSpace();
            bool newAllHidden = GUILayout.Toggle(allHidden, "");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (newAllHidden != allHidden)
            {
                foreach (int idx in descendants)
                    committed[idx].Hidden = newAllHidden;
                // Also update group-level visibility
                if (newAllHidden)
                    GroupHierarchyStore.AddHiddenGroup(groupName);
                else
                    GroupHierarchyStore.RemoveHiddenGroup(groupName);
                ParsekLog.Info("UI",
                    $"Group '{groupName}' hide-all={newAllHidden} ({memberCount} recordings)");
            }

            GUILayout.EndHorizontal();

            if (!expanded) return false;

            // -- Draw children --
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
            IReadOnlyList<Recording> committed, double now)
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

            // Site (first member's launch site)
            string chainSite = members.Count > 0 ? committed[members[0]].LaunchSiteName : null;
            GUILayout.Label(chainSite ?? "", GUILayout.Width(ColW_Site));

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
                GUILayout.Label("", GUILayout.Width(ColW_StartPos));
                GUILayout.Label("", GUILayout.Width(ColW_EndPos));
            }

            // Status (closest active among chain members)
            string chainStatusText;
            int chainStatusOrder;
            GetChainStatus(members, committed, now, out chainStatusText, out chainStatusOrder);
            GUIStyle chainStatusStyle = chainStatusOrder == 0 ? statusStyleFuture
                : chainStatusOrder == 1 ? statusStyleActive
                : statusStylePast;
            GUILayout.Label(chainStatusText, chainStatusStyle, GUILayout.Width(ColW_Status));

            // Chain G button takes the full ColW_Group width since chain blocks have
            // no X (disband) button — chain segments must stay grouped to preserve
            // tree lineage and rewind anchors. Matches the per-recording row layout
            // where G also fills ColW_Group.
            if (GUILayout.Button("G", GUILayout.Width(ColW_Group)))
            {
                var mousePos = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
                groupPicker.OpenForChain(chainId, mousePos);
                ParsekLog.Verbose("UI", $"Group popup opened for chain '{chainName}'");
            }

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
            if (parentUI.InFlightMode) GUILayout.Label("", GUILayout.Width(ColW_Watch));
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

        private void CommitRecordingRename(IReadOnlyList<Recording> committed)
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

            // Update KnownEmptyGroups
            int emptyIdx = KnownEmptyGroups.IndexOf(oldName);
            if (emptyIdx >= 0) KnownEmptyGroups[emptyIdx] = newName;

            ParsekLog.Info("UI", $"Group '{oldName}' renamed to '{newName}'");
        }

        private string GenerateUniqueGroupName()
        {
            var existing = RecordingStore.GetGroupNames();
            for (int i = 0; i < KnownEmptyGroups.Count; i++)
                if (!existing.Contains(KnownEmptyGroups[i]))
                    existing.Add(KnownEmptyGroups[i]);

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
                        KnownEmptyGroups.Remove(capturedName);
                        expandedGroups.Remove(capturedName);
                        ParsekLog.Info("UI", $"Group '{capturedName}' disbanded ({updated} recordings moved to '{capturedParent ?? "(standalone)"}')");
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Verbose("UI", $"Disband group '{capturedName}' cancelled");
                    })
                ), false, HighLogic.UISkin);
        }

        internal void ShowRewindConfirmation(Recording rec)
        {
            // Resolve the rewind save owner — may be the tree root for branch recordings.
            var owner = RecordingStore.GetRewindRecording(rec);
            if (owner == null) return;
            bool isTreeBranch = owner != rec;

            int futureCount = RecordingStore.CountFutureRecordings(owner.StartUT);
            string futureText = futureCount > 0
                ? $"\n\n{futureCount} recording(s) after this launch will replay as ghosts."
                : "";

            string launchDate = KSPUtil.PrintDateCompact(owner.StartUT, true);
            string branchNote = isTreeBranch
                ? $"\n(from branch \"{rec.VesselName}\")"
                : "";
            string message = $"Rewind to \"{owner.VesselName}\" launch at {launchDate}?" +
                branchNote +
                futureText +
                "\n\nAny uncommitted progress will be lost.";

            var capturedOwner = owner;
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
                            $"User confirmed rewind to \"{capturedOwner.VesselName}\" at UT {capturedOwner.StartUT}");
                        RecordingStore.InitiateRewind(capturedOwner);
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Info("Rewind", "User cancelled rewind confirmation");
                    })
                ),
                false, HighLogic.UISkin);
        }

        internal void ShowFastForwardConfirmation(Recording rec)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            double now = Planetarium.GetUniversalTime();
            double delta = rec.StartUT - now;
            string launchDate = KSPUtil.PrintDateCompact(rec.StartUT, true);
            string message = string.Format(ic,
                "Fast-forward to \"{0}\" launch at {1}?\n\nTime will advance by {2:F0} seconds.",
                rec.VesselName, launchDate, delta);

            var flight = parentUI.Flight;
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
                        if (parentUI.InFlightMode && flight != null)
                            flight.FastForwardToRecording(capturedRec);
                        else
                        {
                            // KSC or other non-flight scene: advance UT directly.
                            // Intentionally duplicates jump+message from FastForwardToRecording --
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

        internal void ShowClearRecordingConfirmation()
        {
            var flight = parentUI.Flight;
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

        private void DrawSortableHeader(string label, SortColumn col, float width, bool expand = false)
        {
            parentUI.DrawSortableHeaderCore(label, col, ref sortColumn, ref sortAscending, width, expand, () =>
            {
                InvalidateSort();
                ParsekLog.Verbose("UI", $"Sort column changed: {sortColumn} {(sortAscending ? "asc" : "desc")}");
            });
        }

        private void InvalidateSort()
        {
            lastSortedCount = -1;
        }

        private void RebuildSortedIndices(IReadOnlyList<Recording> committed, double now)
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
                    Array.Reverse(sortedIndices);
                return;
            }

            var col = sortColumn;
            var asc = sortAscending;
            Array.Sort(sortedIndices, (a, b) =>
                CompareRecordings(committed[a], committed[b], col, asc, now));
        }

        internal static int GetStatusOrder(Recording rec, double now)
        {
            if (now < rec.StartUT) return 0;  // future
            if (now <= rec.EndUT && !rec.TerminalStateValue.HasValue) return 1;    // active
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
                default: return rowFallback; // Index, Name, Phase, LaunchSite — string-based, handled externally
            }
        }

        /// <summary>
        /// Sort key for a chain based on the current sort column.
        /// Uses earliest StartUT for launch, sum for duration, etc.
        /// </summary>
        internal static double GetChainSortKey(List<int> members, IReadOnlyList<Recording> committed,
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
                    return 0; // Name/Phase/Index/LaunchSite: handled via string comparison externally
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
                    cmp = 0; // stable -- Array.Sort preserves original order for equal elements
                    break;
                case SortColumn.Phase:
                    string pa = RecordingStore.GetSegmentPhaseLabel(ra);
                    string pb = RecordingStore.GetSegmentPhaseLabel(rb);
                    cmp = string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
                    break;
                case SortColumn.Name:
                    string na = string.IsNullOrEmpty(ra.VesselName) ? "Untitled" : ra.VesselName;
                    string nb = string.IsNullOrEmpty(rb.VesselName) ? "Untitled" : rb.VesselName;
                    cmp = string.Compare(na, nb, StringComparison.OrdinalIgnoreCase);
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
                case SortColumn.LaunchSite:
                    string sa = ra.LaunchSiteName ?? "";
                    string sb = rb.LaunchSiteName ?? "";
                    cmp = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
                    if (cmp == 0)
                        cmp = ra.StartUT.CompareTo(rb.StartUT);
                    break;
            }
            return ascending ? cmp : -cmp;
        }

        internal static int[] BuildSortedIndices(
            IReadOnlyList<Recording> committed, SortColumn column, bool ascending, double now)
        {
            var indices = new int[committed.Count];
            for (int i = 0; i < committed.Count; i++)
                indices[i] = i;

            if (column == SortColumn.Index)
            {
                if (!ascending)
                    Array.Reverse(indices);
                return indices;
            }

            Array.Sort(indices, (a, b) =>
                CompareRecordings(committed[a], committed[b], column, ascending, now));
            return indices;
        }

        internal static string FormatDuration(double seconds)
            => ParsekTimeFormat.FormatDuration(seconds);

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
        /// Resolves the parent vessel name for EVA recordings by looking up ParentRecordingId.
        /// Returns null if not an EVA or parent not found.
        /// </summary>
        internal static string ResolveParentVesselName(Recording rec, IReadOnlyList<Recording> committed)
        {
            if (string.IsNullOrEmpty(rec.EvaCrewName) || string.IsNullOrEmpty(rec.ParentRecordingId))
                return null;
            for (int i = 0; i < committed.Count; i++)
                if (committed[i].RecordingId == rec.ParentRecordingId)
                    return committed[i].VesselName;
            // Also check tree recordings
            foreach (var tree in RecordingStore.CommittedTrees)
                foreach (var kvp in tree.Recordings)
                    if (kvp.Key == rec.ParentRecordingId)
                        return kvp.Value.VesselName;
            return null;
        }

        /// <summary>
        /// Formats the recording start position for the expanded stats column.
        /// Priority: launch site > EVA from vessel > situation + biome + body > biome + body > body.
        /// </summary>
        internal static string FormatStartPosition(Recording rec, string parentVesselName = null)
        {
            // EVA: show source vessel
            if (!string.IsNullOrEmpty(rec.EvaCrewName))
            {
                if (!string.IsNullOrEmpty(parentVesselName))
                    return "EVA from " + parentVesselName;
                return FormatSituationLocation(rec.StartSituation, rec.StartBiome, rec.StartBodyName, "EVA");
            }

            // Launch from a site
            if (!string.IsNullOrEmpty(rec.LaunchSiteName))
                return !string.IsNullOrEmpty(rec.StartBodyName)
                    ? rec.LaunchSiteName + ", " + rec.StartBodyName
                    : rec.LaunchSiteName;

            // General: use situation + biome + body
            return FormatSituationLocation(rec.StartSituation, rec.StartBiome, rec.StartBodyName, null);
        }

        /// <summary>
        /// Formats the recording end position for the expanded stats column.
        /// Matches timeline style: "Orbiting {body}", "{biome}, {body}", "Boarded {vessel}".
        /// Body fallback priority for terminal recordings: TerminalOrbitBody → StartBodyName.
        /// Body fallback priority for mid-segments: SegmentBodyName → last point body → StartBodyName.
        /// </summary>
        internal static string FormatEndPosition(Recording rec, string parentVesselName = null)
        {
            if (!rec.TerminalStateValue.HasValue)
            {
                // No terminal state (chain mid-segment or interior tree recording).
                // Fallback: SegmentBodyName → last trajectory point body → StartBodyName.
                string segBody = rec.SegmentBodyName;
                if (string.IsNullOrEmpty(segBody) && rec.Points != null && rec.Points.Count > 0)
                    segBody = rec.Points[rec.Points.Count - 1].bodyName;
                if (string.IsNullOrEmpty(segBody))
                    segBody = rec.StartBodyName;

                if (!string.IsNullOrEmpty(rec.SegmentPhase) && !string.IsNullOrEmpty(segBody))
                    return segBody + " " + rec.SegmentPhase;
                if (!string.IsNullOrEmpty(segBody))
                    return segBody;
                return "-";
            }

            string body = rec.TerminalOrbitBody;
            if (string.IsNullOrEmpty(body) && !string.IsNullOrEmpty(rec.StartBodyName))
                body = rec.StartBodyName;

            switch (rec.TerminalStateValue.Value)
            {
                case TerminalState.Orbiting:
                    return !string.IsNullOrEmpty(body) ? "Orbiting " + body : "Orbiting";
                case TerminalState.Docked:
                    return !string.IsNullOrEmpty(body) ? "Docked, " + body : "Docked";

                case TerminalState.Landed:
                case TerminalState.Splashed:
                    if (!string.IsNullOrEmpty(rec.EndBiome) && !string.IsNullOrEmpty(body))
                        return rec.EndBiome + ", " + body;
                    if (!string.IsNullOrEmpty(body))
                        return body;
                    return rec.TerminalStateValue.Value.ToString();

                case TerminalState.Destroyed:
                    return !string.IsNullOrEmpty(body) ? "Destroyed, " + body : "Destroyed";
                case TerminalState.Recovered:
                    return !string.IsNullOrEmpty(body) ? "Recovered, " + body : "Recovered";
                case TerminalState.SubOrbital:
                    return !string.IsNullOrEmpty(body) ? "SubOrbital, " + body : "SubOrbital";
                case TerminalState.Boarded:
                    return !string.IsNullOrEmpty(parentVesselName)
                        ? "Boarded " + parentVesselName
                        : "Boarded";

                default:
                    return "-";
            }
        }

        /// <summary>
        /// Formats a resource manifest for tooltip display.
        /// If both start and end: "Resources:\n  LiquidFuel: 3600.0 → 200.0 (-3400.0)"
        /// If start only: "Resources at start:\n  LiquidFuel: 3600.0 / 3600.0"
        /// If both null: returns null (no section shown).
        /// </summary>
        internal static string FormatResourceManifest(
            Dictionary<string, ResourceAmount> start,
            Dictionary<string, ResourceAmount> end)
        {
            if (start == null && end == null)
                return null;

            // Merge keys from both dicts
            var keys = new SortedSet<string>();
            if (start != null)
                foreach (var k in start.Keys) keys.Add(k);
            if (end != null)
                foreach (var k in end.Keys) keys.Add(k);

            if (keys.Count == 0)
                return null;

            bool hasEnd = end != null;
            var lines = new List<string>();
            lines.Add(hasEnd ? "Resources:" : "Resources at start:");

            foreach (var key in keys)
            {
                if (hasEnd)
                {
                    double startAmt = 0;
                    double endAmt = 0;
                    if (start != null && start.TryGetValue(key, out var startRa))
                        startAmt = startRa.amount;
                    if (end.TryGetValue(key, out var endRa))
                        endAmt = endRa.amount;

                    double delta = endAmt - startAmt;
                    string sign = delta >= 0 ? "+" : "";
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "  {0}: {1:F1} \u2192 {2:F1} ({3}{4:F1})",
                        key,
                        startAmt,
                        endAmt,
                        sign,
                        delta));
                }
                else
                {
                    // Start only — show amount / maxAmount
                    double amt = 0;
                    double max = 0;
                    if (start.TryGetValue(key, out var ra))
                    {
                        amt = ra.amount;
                        max = ra.maxAmount;
                    }
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "  {0}: {1:F1} / {2:F1}",
                        key, amt, max));
                }
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Formats an inventory manifest for tooltip display.
        /// If both start and end: "Inventory:\n  solarPanels5: 4 -> 0 (-4)"
        /// If start only: "Inventory at start:\n  solarPanels5: 4"
        /// If both null: returns null (no section shown).
        /// </summary>
        internal static string FormatInventoryManifest(
            Dictionary<string, InventoryItem> start,
            Dictionary<string, InventoryItem> end)
        {
            if (start == null && end == null)
                return null;

            // Merge keys from both dicts
            var keys = new SortedSet<string>();
            if (start != null)
                foreach (var k in start.Keys) keys.Add(k);
            if (end != null)
                foreach (var k in end.Keys) keys.Add(k);

            if (keys.Count == 0)
                return null;

            bool hasEnd = end != null;
            var lines = new List<string>();
            lines.Add(hasEnd ? "Inventory:" : "Inventory at start:");

            foreach (var key in keys)
            {
                if (hasEnd)
                {
                    int startCount = 0;
                    int endCount = 0;
                    if (start != null && start.TryGetValue(key, out var startItem))
                        startCount = startItem.count;
                    if (end.TryGetValue(key, out var endItem))
                        endCount = endItem.count;

                    int delta = endCount - startCount;
                    string sign = delta >= 0 ? "+" : "";
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "  {0}: {1} \u2192 {2} ({3}{4})",
                        key,
                        startCount,
                        endCount,
                        sign,
                        delta));
                }
                else
                {
                    // Start only — show count
                    int count = 0;
                    if (start.TryGetValue(key, out var item))
                        count = item.count;
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "  {0}: {1}",
                        key, count));
                }
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Formats a crew manifest for tooltip display.
        /// If both start and end: "Crew:\n  Pilot: 1 → 1 (+0)\n  Engineer: 2 → 0 (-2)"
        /// If start only: "Crew at start:\n  Pilot: 1\n  Engineer: 2"
        /// If both null: returns null (no section shown).
        /// </summary>
        internal static string FormatCrewManifest(
            Dictionary<string, int> start,
            Dictionary<string, int> end)
        {
            if (start == null && end == null)
                return null;

            // Merge keys from both dicts
            var keys = new SortedSet<string>();
            if (start != null)
                foreach (var k in start.Keys) keys.Add(k);
            if (end != null)
                foreach (var k in end.Keys) keys.Add(k);

            if (keys.Count == 0)
                return null;

            bool hasEnd = end != null;
            var lines = new List<string>();
            lines.Add(hasEnd ? "Crew:" : "Crew at start:");

            foreach (var key in keys)
            {
                if (hasEnd)
                {
                    int startCount = 0;
                    int endCount = 0;
                    if (start != null && start.TryGetValue(key, out var sc))
                        startCount = sc;
                    if (end.TryGetValue(key, out var ec))
                        endCount = ec;

                    int delta = endCount - startCount;
                    string sign = delta >= 0 ? "+" : "";
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "  {0}: {1} \u2192 {2} ({3}{4})",
                        key,
                        startCount,
                        endCount,
                        sign,
                        delta));
                }
                else
                {
                    // Start only — show count
                    int count = 0;
                    if (start.TryGetValue(key, out var sc))
                        count = sc;
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "  {0}: {1}",
                        key, count));
                }
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Formats situation + biome + body into a compact location string.
        /// "Flying, Shores, Kerbin" or "Orbiting, Kerbin" or "Kerbin" etc.
        /// </summary>
        private static string FormatSituationLocation(string situation, string biome, string body, string prefix)
        {
            // Build: "{prefix/situation}, {biome}, {body}" with missing parts omitted
            bool hasSit = !string.IsNullOrEmpty(situation);
            bool hasBiome = !string.IsNullOrEmpty(biome);
            bool hasBody = !string.IsNullOrEmpty(body);

            string label = prefix ?? (hasSit ? situation : null);

            if (label != null && hasBiome && hasBody)
                return label + ", " + biome + ", " + body;
            if (label != null && hasBody)
                return label + ", " + body;
            if (hasBiome && hasBody)
                return biome + ", " + body;
            if (hasBody)
                return body;
            if (label != null)
                return label;
            return "-";
        }

        /// <summary>
        /// Returns the earliest StartUT among the given descendant recording indices.
        /// Returns double.MaxValue if no descendants exist.
        /// </summary>
        internal static double GetGroupEarliestStartUT(HashSet<int> descendants, IReadOnlyList<Recording> committed)
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
        internal static double GetGroupTotalDuration(HashSet<int> descendants, IReadOnlyList<Recording> committed)
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
            HashSet<int> descendants, IReadOnlyList<Recording> committed)
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
        internal static void GetGroupStatus(HashSet<int> descendants, IReadOnlyList<Recording> committed,
            double now, out string statusText, out int statusOrder)
        {
            // Find the best candidate: prefer active (closest T-), then future (closest), then past
            double bestActiveDelta = double.MaxValue;   // smallest |now - StartUT| among active
            double bestFutureDelta = double.MaxValue;   // smallest (StartUT - now) among future
            int activeIdx = -1;
            int futureIdx = -1;
            bool anyPast = false;
            TerminalState? bestTerminal = null;
            double bestTerminalEndUT = double.MinValue;

            foreach (int idx in descendants)
            {
                var rec = committed[idx];
                // A recording with TerminalStateValue is done — don't treat as active
                if (now >= rec.StartUT && now <= rec.EndUT && !rec.TerminalStateValue.HasValue)
                {
                    // Active recording -- prefer closest T- (= StartUT - now, which is negative;
                    // we want the one closest to now, i.e., smallest |delta|)
                    double delta = Math.Abs(rec.StartUT - now);
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
                    // Pick terminal state from the latest-ending non-debris recording
                    if (rec.TerminalStateValue.HasValue && !rec.IsDebris && rec.EndUT > bestTerminalEndUT)
                    {
                        bestTerminal = rec.TerminalStateValue.Value;
                        bestTerminalEndUT = rec.EndUT;
                    }
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
                statusText = bestTerminal.HasValue ? bestTerminal.Value.ToString() : "past";
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
        private static void GetChainStatus(List<int> members, IReadOnlyList<Recording> committed,
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
        internal static double GetGroupSortKey(HashSet<int> descendants, IReadOnlyList<Recording> committed,
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
                case SortColumn.LaunchSite:
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

        private void DrawRecordingTooltip(Recording rec)
        {
            var stats = GetOrComputeStats(rec);

            string text = $"Max Altitude: {FormatAltitude(stats.maxAltitude)}\n" +
                          $"Max Speed: {FormatSpeed(stats.maxSpeed)}\n" +
                          $"Distance: {FormatDistance(stats.distanceTravelled)}\n" +
                          $"Points: {stats.pointCount}";

            // Phase 6d-3: Chain status in recording tooltip
            var flight = parentUI.Flight;
            if (parentUI.InFlightMode && flight != null)
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

            // Observability: append storage breakdown from cached file sizes
            try
            {
                double currentUT = Planetarium.GetUniversalTime();
                var storage = DiagnosticsComputation.GetCachedStorageBreakdown(rec, currentUT);
                if (storage.totalBytes > 0)
                {
                    text += $"\nStorage: {DiagnosticsComputation.FormatBytes(storage.totalBytes)}" +
                            $" (trajectory: {DiagnosticsComputation.FormatBytes(storage.trajectoryFileBytes)}" +
                            $", vessel: {DiagnosticsComputation.FormatBytes(storage.vesselSnapshotBytes)}" +
                            $", ghost: {DiagnosticsComputation.FormatBytes(storage.ghostSnapshotBytes)})";
                    text += $"\nEfficiency: {DiagnosticsComputation.FormatBytes((long)storage.bytesPerSecond)}/s of flight time";
                }
            }
            catch { /* Non-KSP context or Planetarium unavailable */ }

            string resourceText = FormatResourceManifest(rec.StartResources, rec.EndResources);
            if (resourceText != null)
                text += "\n" + resourceText;

            string inventoryText = FormatInventoryManifest(rec.StartInventory, rec.EndInventory);
            if (inventoryText != null)
                text += "\n" + inventoryText;

            string crewText = FormatCrewManifest(rec.StartCrew, rec.EndCrew);
            if (crewText != null)
                text += "\n" + crewText;

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

        // --- Loop helpers exclusive to recordings table ---

        internal static string UnitSuffix(LoopTimeUnit unit)
            => unit == LoopTimeUnit.Min ? "m"
             : unit == LoopTimeUnit.Hour ? "h"
             : "s";

        internal static LoopTimeUnit CycleRecordingUnit(LoopTimeUnit u)
            => u == LoopTimeUnit.Sec ? LoopTimeUnit.Min
             : u == LoopTimeUnit.Min ? LoopTimeUnit.Hour
             : u == LoopTimeUnit.Hour ? LoopTimeUnit.Auto
             : LoopTimeUnit.Sec;

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
                        ? ParsekUI.ConvertFromSeconds(settings.autoLoopIntervalSeconds, settings.AutoLoopDisplayUnit) : 10;
                    var gu = settings != null ? settings.AutoLoopDisplayUnit : LoopTimeUnit.Sec;
                    disabledText = ParsekUI.FormatLoopValue(gv, gu) + UnitSuffix(gu);
                }
                else
                {
                    disabledText = ParsekUI.FormatLoopValue(ParsekUI.ConvertFromSeconds(rec.LoopIntervalSeconds, rec.LoopTimeUnit), rec.LoopTimeUnit);
                }
                GUILayout.TextField(disabledText, GUILayout.Width(valueBtnW));
                GUILayout.Button(ParsekUI.UnitLabel(rec.LoopTimeUnit), GUILayout.Width(unitBtnW));
                GUI.enabled = true;
                return;
            }

            if (rec.LoopTimeUnit == LoopTimeUnit.Auto)
            {
                // Auto mode: disabled text field showing global value + "auto" unit button
                var settings = ParsekSettings.Current;
                double globalVal = settings != null
                    ? ParsekUI.ConvertFromSeconds(settings.autoLoopIntervalSeconds, settings.AutoLoopDisplayUnit)
                    : 10;
                GUI.enabled = false;
                var globalDisplayUnit = settings != null ? settings.AutoLoopDisplayUnit : LoopTimeUnit.Sec;
                GUILayout.TextField(ParsekUI.FormatLoopValue(globalVal, globalDisplayUnit) + UnitSuffix(globalDisplayUnit), GUILayout.Width(valueBtnW));
                GUI.enabled = true;
            }
            else
            {
                // Manual mode: editable text field with edit buffer.
                if (loopPeriodFocusedRi != ri)
                {
                    // Not editing: show model value, click to start editing
                    double displayValue = ParsekUI.ConvertFromSeconds(rec.LoopIntervalSeconds, rec.LoopTimeUnit);
                    string displayText = ParsekUI.FormatLoopValue(displayValue, rec.LoopTimeUnit);
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
                            $"value='{newText}' unit={ParsekUI.UnitLabel(rec.LoopTimeUnit)}");
                    }
                }
                else
                {
                    // Enter key -> commit (check before TextField, which consumes KeyDown)
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
            if (GUILayout.Button(ParsekUI.UnitLabel(rec.LoopTimeUnit), GUILayout.Width(unitBtnW)))
            {
                var newUnit = CycleRecordingUnit(rec.LoopTimeUnit);
                rec.LoopTimeUnit = newUnit;
                GUIUtility.keyboardControl = 0;
                loopPeriodFocusedRi = -1;
                ParsekLog.Info("UI",
                    $"Recording '{rec.VesselName}' loop unit changed to {newUnit}");
            }
        }

        private void CommitLoopPeriodEdit(IReadOnlyList<Recording> committed)
        {
            if (loopPeriodFocusedRi < 0 || loopPeriodFocusedRi >= committed.Count) { loopPeriodFocusedRi = -1; return; }
            var rec = committed[loopPeriodFocusedRi];
            double dur = rec.EndUT - rec.StartUT;
            double parsed;
            if (ParsekUI.TryParseLoopInput(loopPeriodEditText, rec.LoopTimeUnit, out parsed))
            {
                double newSeconds = ParsekUI.ConvertToSeconds(parsed, rec.LoopTimeUnit);
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
                    $"s (display: {ParsekUI.FormatLoopValue(ParsekUI.ConvertFromSeconds(newSeconds, rec.LoopTimeUnit), rec.LoopTimeUnit)} " +
                    $"{ParsekUI.UnitLabel(rec.LoopTimeUnit)})");
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

        /// <summary>
        /// Builds the group tree data structures used to render the recordings tree.
        /// Pure data-computation -- no IMGUI calls.
        /// </summary>
        internal static void BuildGroupTreeData(
            IReadOnlyList<Recording> committed, int[] sortedIndices,
            List<string> KnownEmptyGroups,
            out Dictionary<string, List<int>> grpToRecs,
            out Dictionary<string, List<int>> chainToRecs,
            out Dictionary<string, List<string>> grpChildren,
            out List<string> rootGrps,
            out HashSet<string> rootChainIds)
        {
            // group name -> list of recording indices directly in that group
            grpToRecs = new Dictionary<string, List<int>>();
            // chainId -> list of recording indices
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
